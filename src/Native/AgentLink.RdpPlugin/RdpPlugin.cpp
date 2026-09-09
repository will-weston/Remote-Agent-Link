#include "RdpPlugin.h"
#include "ServerLifetime.h"
#include "WinHandle.h"
#include "PduReassembler.h"
#include <sddl.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <deque>
#include <memory>
#include <mutex>
#include <new>
#include <stdexcept>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace ral {
namespace {
constexpr size_t MaxQueuedBytes = 32u * 1024 * 1024;
constexpr DWORD ConnectTimeoutMs = 5000;
constexpr DWORD RpcTimeoutMs = 10000;

HRESULT CurrentException() noexcept {
    try { throw; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}

bool TokenUserData(HANDLE token, std::vector<uint8_t>& data) {
    DWORD size{};
    GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    if (!size) return false;
    data.resize(size);
    return GetTokenInformation(token, TokenUser, data.data(), size, &size) != FALSE;
}

DWORD VerifyPipeServer(HANDLE pipe) {
    ULONG processId{};
    if (!GetNamedPipeServerProcessId(pipe, &processId)) return GetLastError();
    WinHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId));
    if (!process) return GetLastError();
    HANDLE raw{};
    if (!OpenProcessToken(process.get(), TOKEN_QUERY, &raw)) return GetLastError();
    WinHandle remoteToken(raw);
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw)) return GetLastError();
    WinHandle ownToken(raw);
    std::vector<uint8_t> remoteUser, ownUser;
    if (!TokenUserData(remoteToken.get(), remoteUser) || !TokenUserData(ownToken.get(), ownUser))
        return GetLastError();
    if (!EqualSid(reinterpret_cast<TOKEN_USER*>(remoteUser.data())->User.Sid,
                  reinterpret_cast<TOKEN_USER*>(ownUser.data())->User.Sid))
        return ERROR_ACCESS_DENIED;
    TOKEN_STATISTICS remoteStats{}, ownStats{};
    DWORD remoteSession{}, ownSession{}, size{};
    if (!GetTokenInformation(remoteToken.get(), TokenStatistics, &remoteStats, sizeof(remoteStats), &size) ||
        !GetTokenInformation(ownToken.get(), TokenStatistics, &ownStats, sizeof(ownStats), &size) ||
        !GetTokenInformation(remoteToken.get(), TokenSessionId, &remoteSession, sizeof(remoteSession), &size) ||
        !GetTokenInformation(ownToken.get(), TokenSessionId, &ownSession, sizeof(ownSession), &size))
        return GetLastError();
    if (remoteSession != ownSession ||
        remoteStats.AuthenticationId.HighPart != ownStats.AuthenticationId.HighPart ||
        remoteStats.AuthenticationId.LowPart != ownStats.AuthenticationId.LowPart)
        return ERROR_ACCESS_DENIED;
    DWORD flags{};
    if (!GetNamedPipeInfo(pipe, &flags, nullptr, nullptr, nullptr)) return GetLastError();
    return (flags & PIPE_TYPE_MESSAGE) ? ERROR_INVALID_DATA : ERROR_SUCCESS;
}

class Bridge : public std::enable_shared_from_this<Bridge> {
public:
    explicit Bridge(IWTSVirtualChannel* channel) : channel_(channel) {
        if (!stop_ || !dataReady_) throw std::runtime_error("CreateEvent");
        timer_ = CreateThreadpoolTimer(CancelRpc, this, nullptr);
        if (!timer_) throw std::runtime_error("CreateThreadpoolTimer");
    }
    ~Bridge() {
        if (timer_) {
            SetThreadpoolTimer(timer_, nullptr, 0, 0);
            WaitForThreadpoolTimerCallbacks(timer_, TRUE);
            CloseThreadpoolTimer(timer_);
        }
    }
    void Start() {
        // The self-reference and server lease outlive all detached worker activity.
        std::thread worker([self = shared_from_this()]() mutable {
            const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            if (SUCCEEDED(initialized)) {
                self->Run();
                self.reset();
                CoUninitialize();
            } else {
                self->Stop();
            }
        });
        worker.detach();
    }
    void Stop(bool remoteClosed = false) noexcept {
        if (remoteClosed) remoteClosed_.store(true);
        SetEvent(stop_.get());
    }
    HRESULT Enqueue(ULONG size, const BYTE* bytes) noexcept {
        if (size && !bytes) { Stop(); return E_POINTER; }
        if (size > MaxRecordBytes) { Stop(); return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE); }
        try {
            std::lock_guard lock(queueLock_);
            if (Stopped()) return HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);
            if (!size) return S_OK;
            if (size > MaxQueuedBytes - queuedBytes_) {
                Stop();
                return HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA);
            }
            queue_.emplace_back(bytes, bytes + size);
            queuedBytes_ += size;
            SetEvent(dataReady_.get());
            return S_OK;
        } catch (...) { Stop(); return CurrentException(); }
    }
private:
    bool Stopped() const noexcept { return WaitForSingleObject(stop_.get(), 0) == WAIT_OBJECT_0; }

    static void CALLBACK CancelRpc(PTP_CALLBACK_INSTANCE, void* context, PTP_TIMER) noexcept {
        auto& self = *static_cast<Bridge*>(context);
        const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(initialized)) return;
        {
            std::lock_guard lock(self.rpcLock_);
            if (self.rpcThread_ && ((self.Stopped() && !self.rpcClosing_) ||
                GetTickCount64() - self.rpcSince_ >= RpcTimeoutMs)) {
                self.Stop();
                CoCancelCall(self.rpcThread_, 0);
            }
        }
        CoUninitialize();
    }
    HRESULT ChannelCall(bool close, ULONG size = 0, BYTE* bytes = nullptr) noexcept {
        {
            std::lock_guard lock(rpcLock_);
            rpcThread_ = GetCurrentThreadId();
            rpcSince_ = GetTickCount64();
            rpcClosing_ = close;
        }
        const auto result = close ? channel_->Close() :
            (Stopped() ? HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED) : channel_->Write(size, bytes, nullptr));
        {
            std::lock_guard lock(rpcLock_);
            rpcThread_ = 0;
        }
        return result;
    }
    WinHandle ConnectPipe() {
        std::wstring name;
        if (FAILED(GetBrokerPipeName(name))) return {};
        const auto deadline = GetTickCount64() + ConnectTimeoutMs;
        while (!Stopped()) {
            WinHandle pipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
            if (pipe) {
                if (VerifyPipeServer(pipe.get()) == ERROR_SUCCESS) return pipe;
                return {};
            }
            const auto error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PIPE_BUSY && error != ERROR_PIPE_NOT_CONNECTED)
                return {};
            if (GetTickCount64() >= deadline) return {};
            if (WaitForSingleObject(stop_.get(), 100) == WAIT_OBJECT_0) return {};
        }
        return {};
    }
    DWORD PipeIo(HANDLE pipe, HANDLE event, bool write, BYTE* bytes, DWORD size, DWORD& transferred) {
        if (Stopped()) return ERROR_OPERATION_ABORTED;
        OVERLAPPED operation{};
        operation.hEvent = event;
        ResetEvent(event);
        const auto success = write ? WriteFile(pipe, bytes, size, &transferred, &operation) :
            ReadFile(pipe, bytes, size, &transferred, &operation);
        if (success) return transferred ? ERROR_SUCCESS : ERROR_BROKEN_PIPE;
        auto error = GetLastError();
        if (error != ERROR_IO_PENDING) return error;
        const HANDLE waits[]{stop_.get(), event};
        const auto wait = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
        if (wait != WAIT_OBJECT_0 + 1) {
            error = wait == WAIT_OBJECT_0 ? ERROR_OPERATION_ABORTED : GetLastError();
            CancelIoEx(pipe, &operation);
            GetOverlappedResult(pipe, &operation, &transferred, TRUE);
            return error;
        }
        if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) return GetLastError();
        return transferred ? ERROR_SUCCESS : ERROR_BROKEN_PIPE;
    }
    void ReadPipe(HANDLE pipe) noexcept {
        const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(initialized)) { Stop(); return; }
        const auto cancellation = CoEnableCallCancellation(nullptr);
        if (SUCCEEDED(cancellation)) {
            WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            std::array<BYTE, TransportChunkBytes> bytes{};
            while (event && !Stopped()) {
                DWORD count{};
                if (PipeIo(pipe, event.get(), false, bytes.data(), static_cast<DWORD>(bytes.size()), count))
                    break;
                if (FAILED(ChannelCall(false, count, bytes.data()))) break;
            }
            CoDisableCallCancellation(nullptr);
        }
        Stop();
        CoUninitialize();
    }
    void WritePipe(HANDLE pipe) {
        WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        const HANDLE waits[]{stop_.get(), dataReady_.get()};
        while (event && !Stopped()) {
            if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0 + 1) break;
            std::vector<BYTE> bytes;
            {
                std::lock_guard lock(queueLock_);
                if (queue_.empty()) continue;
                bytes = std::move(queue_.front());
                queue_.pop_front();
                if (queue_.empty()) ResetEvent(dataReady_.get());
            }
            size_t sent{};
            while (sent < bytes.size()) {
                DWORD count{};
                if (PipeIo(pipe, event.get(), true, bytes.data() + sent,
                    static_cast<DWORD>(std::min(bytes.size() - sent, size_t{TransportChunkBytes})), count)) {
                    Stop();
                    break;
                }
                sent += count;
            }
            {
                std::lock_guard lock(queueLock_);
                queuedBytes_ -= bytes.size();
            }
        }
        Stop();
    }
    void Run() noexcept {
        const auto cancellation = CoEnableCallCancellation(nullptr);
        FILETIME due{};
        const LONGLONG relative = -1000000; // First watchdog tick in 100 ms.
        std::memcpy(&due, &relative, sizeof(due));
        SetThreadpoolTimer(timer_, &due, 100, 0);
        WinHandle pipe;
        std::thread reader;
        try {
            if (SUCCEEDED(cancellation)) pipe = ConnectPipe();
            if (pipe) {
                reader = std::thread([this, handle = pipe.get()] { ReadPipe(handle); });
                WritePipe(pipe.get());
            }
        } catch (...) { OutputDebugStringW(L"AgentLink: transport worker failed.\n"); }
        Stop();
        if (reader.joinable()) reader.join();
        if (!remoteClosed_.load() && SUCCEEDED(cancellation)) ChannelCall(true);
        // Drop the external proxy even if the client keeps our callback alive.
        channel_.Reset();
        pipe.reset();
        {
            std::lock_guard lock(queueLock_);
            queue_.clear();
            queuedBytes_ = 0;
        }
        SetThreadpoolTimer(timer_, nullptr, 0, 0);
        WaitForThreadpoolTimerCallbacks(timer_, TRUE);
        if (SUCCEEDED(cancellation)) CoDisableCallCancellation(nullptr);
    }

    ServerLease lease_;
    ComPtr<IWTSVirtualChannel> channel_;
    WinHandle stop_{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    WinHandle dataReady_{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::atomic<bool> remoteClosed_{};
    std::mutex queueLock_;
    std::deque<std::vector<BYTE>> queue_;
    size_t queuedBytes_{};
    PTP_TIMER timer_{};
    std::mutex rpcLock_;
    DWORD rpcThread_{};
    ULONGLONG rpcSince_{};
    bool rpcClosing_{};
};

struct PluginState {
    std::mutex lock;
    bool accepting{true};
    bool terminated{};
    std::vector<std::weak_ptr<Bridge>> channels;

    void Stop(bool terminate) noexcept {
        std::lock_guard guard(lock);
        accepting = false;
        terminated = terminated || terminate;
        for (auto& weak : channels) if (auto channel = weak.lock()) channel->Stop();
        channels.clear();
    }
};

class ChannelCallback final : public ComObject<IWTSVirtualChannelCallback> {
public:
    explicit ChannelCallback(std::shared_ptr<Bridge> bridge) : bridge_(std::move(bridge)) {}
    ~ChannelCallback() override { bridge_->Stop(); }
    HRESULT STDMETHODCALLTYPE OnDataReceived(ULONG size, BYTE* bytes) noexcept override {
        return bridge_->Enqueue(size, bytes);
    }
    HRESULT STDMETHODCALLTYPE OnClose() noexcept override {
        bridge_->Stop(true);
        return S_OK;
    }
private:
    std::shared_ptr<Bridge> bridge_;
};

class ListenerCallback final : public ComObject<IWTSListenerCallback> {
public:
    explicit ListenerCallback(std::shared_ptr<PluginState> state) : state_(std::move(state)) {}
    HRESULT STDMETHODCALLTYPE OnNewChannelConnection(IWTSVirtualChannel* channel, BSTR,
        BOOL* accept, IWTSVirtualChannelCallback** callback) noexcept override {
        if (!accept || !callback) return E_POINTER;
        *accept = FALSE;
        *callback = nullptr;
        if (!channel) return E_POINTER;
        try {
            auto bridge = std::make_shared<Bridge>(channel);
            ComPtr<ChannelCallback> handler;
            handler.Attach(new ChannelCallback(bridge));
            {
                std::lock_guard lock(state_->lock);
                if (!state_->accepting || state_->terminated) return S_OK;
                std::erase_if(state_->channels, [](const auto& item) { return item.expired(); });
                state_->channels.push_back(bridge);
                bridge->Start();
            }
            *callback = handler.Detach();
            *accept = TRUE;
            return S_OK;
        } catch (...) { return CurrentException(); }
    }
private:
    std::shared_ptr<PluginState> state_;
};

class Plugin final : public ComObject<IWTSPlugin> {
public:
    Plugin() : state_(std::make_shared<PluginState>()) {}
    ~Plugin() override { Terminated(); }
    HRESULT STDMETHODCALLTYPE Initialize(IWTSVirtualChannelManager* manager) noexcept override {
        if (!manager) return E_POINTER;
        try {
            {
                std::lock_guard lock(state_->lock);
                if (initialized_ || state_->terminated) return E_UNEXPECTED;
                initialized_ = true;
            }
            ComPtr<ListenerCallback> callback;
            callback.Attach(new ListenerCallback(state_));
            ComPtr<IWTSListener> listener;
            const auto result = manager->CreateListener(
                reinterpret_cast<const unsigned char*>(ChannelName), 0, callback.Get(), &listener);
            if (FAILED(result)) { state_->Stop(true); return result; }
            {
                std::lock_guard lock(state_->lock);
                if (!state_->terminated) listener_ = std::move(listener);
            }
            return S_OK;
        } catch (...) { state_->Stop(true); return CurrentException(); }
    }
    HRESULT STDMETHODCALLTYPE Connected() noexcept override {
        std::lock_guard lock(state_->lock);
        if (state_->terminated) return E_UNEXPECTED;
        state_->accepting = true;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Disconnected(DWORD) noexcept override {
        state_->Stop(false);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Terminated() noexcept override {
        state_->Stop(true);
        ComPtr<IWTSListener> old;
        {
            std::lock_guard lock(state_->lock);
            old.Swap(listener_);
        }
        return S_OK;
    }
private:
    std::shared_ptr<PluginState> state_;
    ComPtr<IWTSListener> listener_;
    bool initialized_{};
};

class PluginFactory final : public ComObject<IWTSPluginFactory> {
public:
    explicit PluginFactory(bool enabled) noexcept : enabled_(enabled) {}
    HRESULT STDMETHODCALLTYPE GetPluginCount(long* count) noexcept override {
        if (!count) return E_POINTER;
        *count = enabled_ ? 1 : 0;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE CreatePlugin(long index, IWTSPlugin** plugin) noexcept override {
        if (!plugin) return E_POINTER;
        *plugin = nullptr;
        if (index != 0) return E_INVALIDARG;
        if (!enabled_) return HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY);
        try { *plugin = new Plugin; return S_OK; }
        catch (...) { return CurrentException(); }
    }
private:
    const bool enabled_;
};
}

HRESULT CreatePluginFactory(IWTSPluginFactory** factory) noexcept {
    if (!factory) return E_POINTER;
    *factory = nullptr;
    PluginLoadingMode selected{};
    const auto result = ReadPluginLoadingMode(&selected);
    return FAILED(result) ? result : CreatePluginFactory(selected, factory);
}

HRESULT CreatePluginFactory(PluginLoadingMode selectedMode, IWTSPluginFactory** factory) noexcept {
    return CreatePluginFactory(PluginLoadingMode::Modern, selectedMode, factory);
}

HRESULT CreatePluginFactory(PluginLoadingMode activationMode, PluginLoadingMode selectedMode,
    IWTSPluginFactory** factory) noexcept {
    if (!factory) return E_POINTER;
    *factory = nullptr;
    if (!IsValidLoadingMode(activationMode) || !IsValidLoadingMode(selectedMode)) return E_INVALIDARG;
    try { *factory = new PluginFactory(activationMode == selectedMode); return S_OK; }
    catch (...) { return CurrentException(); }
}

HRESULT CreatePluginActivation(PluginLoadingMode activationMode, PluginLoadingMode selectedMode,
    REFIID iid, void** object) noexcept {
    if (!object) return E_POINTER;
    *object = nullptr;
    if (!IsValidLoadingMode(activationMode) || !IsValidLoadingMode(selectedMode)) return E_INVALIDARG;
    if (activationMode == PluginLoadingMode::Legacy && iid == __uuidof(IWTSPlugin)) {
        if (selectedMode != PluginLoadingMode::Legacy)
            return HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY);
        try { *object = static_cast<IWTSPlugin*>(new Plugin); return S_OK; }
        catch (...) { return CurrentException(); }
    }
    if (iid != IID_IUnknown && iid != __uuidof(IWTSPluginFactory)) return E_NOINTERFACE;
    ComPtr<IWTSPluginFactory> factory;
    const auto result = CreatePluginFactory(activationMode, selectedMode, &factory);
    return FAILED(result) ? result : factory->QueryInterface(iid, object);
}

HRESULT GetBrokerPipeName(std::wstring& name) noexcept {
    try {
        HANDLE raw{};
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw)) return HRESULT_FROM_WIN32(GetLastError());
        WinHandle token(raw);
        std::vector<uint8_t> user;
        if (!TokenUserData(token.get(), user)) return HRESULT_FROM_WIN32(GetLastError());
        LPWSTR text{};
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, &text))
            return HRESULT_FROM_WIN32(GetLastError());
        std::unique_ptr<wchar_t, decltype(&LocalFree)> sid(text, LocalFree);
        DWORD session{};
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return HRESULT_FROM_WIN32(GetLastError());
        name = L"\\\\.\\pipe\\RemoteAgentLink.Transport." + std::wstring(sid.get()) + L"." + std::to_wstring(session);
        return S_OK;
    } catch (...) { return CurrentException(); }
}
}

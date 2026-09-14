#include "RdpPlugin.h"
#include "ServerLifetime.h"
#include "SurfaceMetadata.h"
#include "WinHandle.h"
#include "PduReassembler.h"
#include "Diagnostics.h"
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

struct ClientReport {
    char magic[8]{'R', 'A', 'L', 'C', 'L', 'T', '1', '\0'};
    GUID instance{};
    GUID epoch{};
    DWORD state{};
    DWORD reserved{};
};
static_assert(sizeof(ClientReport) == 48 && offsetof(ClientReport, state) == 40);

struct SurfaceSignal {
    WinHandle changed{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::atomic<uint64_t> revision{};
};

class WindowChangedCallback final : public ComObject<IWTSWindowChangedCallback, false> {
public:
    explicit WindowChangedCallback(std::weak_ptr<SurfaceSignal> signal) : signal_(std::move(signal)) {}
    HRESULT STDMETHODCALLTYPE WindowChanged(const WTSWindowInfo* info) noexcept override {
        if (!info) return E_POINTER;
        if (const auto signal = signal_.lock()) {
            ++signal->revision;
            SetEvent(signal->changed.get());
        }
        // Callback data is not an identity proof. Re-query on the MTA worker.
        return S_OK;
    }
private:
    std::weak_ptr<SurfaceSignal> signal_;
};

class SurfaceRpcDeadline {
public:
    explicit SurfaceRpcDeadline(HANDLE stop) : stop_(stop) {
        if (FAILED(CoEnableCallCancellation(nullptr))) throw std::runtime_error("COM cancellation");
        timer_ = CreateThreadpoolTimer(Cancel, this, nullptr);
        if (!timer_) {
            CoDisableCallCancellation(nullptr);
            throw std::runtime_error("surface RPC timer");
        }
        LARGE_INTEGER due{};
        due.QuadPart = -1000000;
        FILETIME time{due.LowPart, static_cast<DWORD>(due.HighPart)};
        SetThreadpoolTimer(timer_, &time, 100, 0);
    }
    ~SurfaceRpcDeadline() {
        SetThreadpoolTimer(timer_, nullptr, 0, 0);
        WaitForThreadpoolTimerCallbacks(timer_, TRUE);
        CloseThreadpoolTimer(timer_);
        CoDisableCallCancellation(nullptr);
    }
    template<class Call> HRESULT Invoke(Call&& call, bool cleanup = false) noexcept {
        if (!cleanup && WaitForSingleObject(stop_, 0) == WAIT_OBJECT_0)
            return HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);
        {
            std::lock_guard lock(lock_);
            thread_ = GetCurrentThreadId();
            since_ = GetTickCount64();
            cleanup_ = cleanup;
        }
        const auto result = call();
        {
            std::lock_guard lock(lock_);
            thread_ = 0;
        }
        return result;
    }
private:
    static void CALLBACK Cancel(PTP_CALLBACK_INSTANCE, void* context, PTP_TIMER) noexcept {
        auto& self = *static_cast<SurfaceRpcDeadline*>(context);
        const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(initialized)) return;
        {
            std::lock_guard lock(self.lock_);
            if (self.thread_ && (GetTickCount64() - self.since_ >= 3000 ||
                (!self.cleanup_ && WaitForSingleObject(self.stop_, 0) == WAIT_OBJECT_0)))
                CoCancelCall(self.thread_, 0);
        }
        CoUninitialize();
    }
    HANDLE stop_;
    PTP_TIMER timer_{};
    std::mutex lock_;
    DWORD thread_{};
    ULONGLONG since_{};
    bool cleanup_{};
};

bool SurfaceProcessIdentity(DWORD processId, HWND hwnd, SurfaceReport& report) {
    DWORD windowProcess{}, session{}, ownSession{};
    if (!processId || !IsWindow(hwnd) || !GetWindowThreadProcessId(hwnd, &windowProcess) ||
        windowProcess != processId || !ProcessIdToSessionId(processId, &session) ||
        !ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) || session != ownSession)
        return false;
    WinHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId));
    FILETIME started{}, exited{}, kernel{}, user{};
    HANDLE token{};
    if (!process || !GetProcessTimes(process.get(), &started, &exited, &kernel, &user) ||
        !OpenProcessToken(process.get(), TOKEN_QUERY, &token)) return false;
    WinHandle clientToken(token);
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    WinHandle ownToken(token);
    std::vector<uint8_t> clientUser, ownUser;
    TOKEN_STATISTICS clientStats{}, ownStats{};
    DWORD size{};
    if (!TokenUserData(clientToken.get(), clientUser) || !TokenUserData(ownToken.get(), ownUser) ||
        !EqualSid(reinterpret_cast<TOKEN_USER*>(clientUser.data())->User.Sid,
            reinterpret_cast<TOKEN_USER*>(ownUser.data())->User.Sid) ||
        !GetTokenInformation(clientToken.get(), TokenStatistics, &clientStats, sizeof(clientStats), &size) ||
        !GetTokenInformation(ownToken.get(), TokenStatistics, &ownStats, sizeof(ownStats), &size) ||
        clientStats.AuthenticationId.HighPart != ownStats.AuthenticationId.HighPart ||
        clientStats.AuthenticationId.LowPart != ownStats.AuthenticationId.LowPart)
        return false;
    DWORD stillWindowProcess{};
    if (!IsWindow(hwnd) || !GetWindowThreadProcessId(hwnd, &stillWindowProcess) || stillWindowProcess != processId)
        return false;
    report.processId = processId;
    report.sessionId = session;
    report.processStart = (static_cast<uint64_t>(started.dwHighDateTime) << 32) | started.dwLowDateTime;
    return true;
}

class ClientReporter : public std::enable_shared_from_this<ClientReporter> {
public:
    explicit ClientReporter(ClientConnectionPipeResolver resolver) : resolver_(resolver) {
        if (!stop_ || !changed_ || FAILED(CoCreateGuid(&report_.instance)) ||
            FAILED(CoCreateGuid(&report_.epoch))) throw std::runtime_error("client lifecycle initialization");
    }
    const GUID& Instance() const noexcept { return report_.instance; }
    void Start(IWTSVirtualChannelManager* manager) {
        std::wstring name;
        const auto result = resolver_(name);
        if (result == S_FALSE) return;
        if (FAILED(result) || name.empty()) throw std::runtime_error("client lifecycle pipe name");
        std::thread([self = shared_from_this(), name = std::move(name)]() mutable {
            const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            if (SUCCEEDED(initialized)) {
                self->Run(name);
                self.reset();
                CoUninitialize();
            } else {
                WriteDiagnostic(DiagnosticKind::ReporterFailure, initialized, 0, self->Instance());
                self->Stop();
            }
        }).detach();
        // The COM EXE and its inbound RPC callbacks run in the MTA. Never hand
        // an apartment-bound pointer to this worker from an unsupported caller.
        APTTYPE apartment{};
        APTTYPEQUALIFIER qualifier{};
        const auto apartmentResult = CoGetApartmentType(&apartment, &qualifier);
        if (FAILED(apartmentResult) || apartment != APTTYPE_MTA) {
            WriteDiagnostic(DiagnosticKind::ReporterFailure,
                FAILED(apartmentResult) ? apartmentResult : RPC_E_WRONG_THREAD, 1, Instance());
            return;
        }
        try {
            std::thread([self = shared_from_this(), manager = ComPtr<IWTSVirtualChannelManager>(manager)]() mutable {
                const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                if (SUCCEEDED(initialized)) {
                    self->RunSurface(manager.Get());
                    manager.Reset();
                    self.reset();
                    CoUninitialize();
                } else {
                    WriteDiagnostic(DiagnosticKind::ReporterFailure, initialized, 1, self->Instance());
                }
            }).detach();
        } catch (...) { WriteDiagnostic(DiagnosticKind::ReporterFailure, CurrentException(), 1, Instance()); }
    }
    HRESULT Connected() noexcept {
        std::lock_guard lock(lock_);
        if (report_.state != 1) {
            GUID epoch{};
            const auto result = CoCreateGuid(&epoch);
            if (FAILED(result)) {
                WriteDiagnostic(DiagnosticKind::PluginConnected, result, 0, Instance());
                return result;
            }
            report_.epoch = epoch;
            report_.state = 1;
            SetEvent(changed_.get());
            WriteDiagnostic(DiagnosticKind::PluginConnected, S_OK, 0, Instance());
        }
        return S_OK;
    }
    void Disconnected() noexcept {
        std::lock_guard lock(lock_);
        if (report_.state != 2)
            WriteDiagnostic(DiagnosticKind::PluginDisconnected, S_OK, 0, Instance());
        report_.state = 2;
        SetEvent(changed_.get());
    }
    void Stop() noexcept {
        SetEvent(stop_.get());
        if (!stopReported_.exchange(true))
            WriteDiagnostic(DiagnosticKind::PluginTerminated, S_OK, 0, Instance());
    }
private:
    template<class Report> bool Write(HANDLE pipe, HANDLE event, const Report& report) noexcept {
        OVERLAPPED operation{};
        operation.hEvent = event;
        ResetEvent(event);
        DWORD written{};
        if (WriteFile(pipe, &report, sizeof(report), &written, &operation))
            return written == sizeof(report);
        if (GetLastError() != ERROR_IO_PENDING) return false;
        HANDLE waits[]{stop_.get(), event};
        if (WaitForMultipleObjects(2, waits, FALSE, 2000) == WAIT_OBJECT_0 + 1)
            return GetOverlappedResult(pipe, &operation, &written, FALSE) && written == sizeof(report);
        CancelIoEx(pipe, &operation);
        GetOverlappedResult(pipe, &operation, &written, TRUE);
        return false;
    }
    ClientReport Snapshot() {
        std::lock_guard lock(lock_);
        return report_;
    }
    void RunSurface(IWTSVirtualChannelManager* manager) noexcept {
        try {
            std::wstring name;
            if (resolver_(name) != S_OK || name.empty()) return;
            name += L".Surfaces";
            SurfaceRpcDeadline rpc(stop_.get());
            auto signal = std::make_shared<SurfaceSignal>();
            WinHandle completed(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!signal->changed || !completed) {
                WriteDiagnostic(DiagnosticKind::ReporterFailure, E_FAIL, 1, Instance());
                return;
            }
            ComPtr<WindowChangedCallback> callback;
            callback.Attach(new WindowChangedCallback(signal));
            ComPtr<IWTSWindowInfoService> service;
            bool subscriptionAttempted{};
            struct SubscriptionCleanup {
                SurfaceRpcDeadline& rpc;
                ComPtr<IWTSWindowInfoService>& service;
                ComPtr<WindowChangedCallback>& callback;
                std::shared_ptr<SurfaceSignal>& signal;
                bool& attempted;
                ~SubscriptionCleanup() {
                    signal.reset();
                    if (service && attempted)
                        rpc.Invoke([&] { return service->UnsubscribeWindowChanged(nullptr, callback.Get()); }, true);
                }
            } cleanup{rpc, service, callback, signal, subscriptionAttempted};
            WinHandle pipe;
            uint64_t generation{};
            uint64_t previousRevision{};
            SurfaceReport previous{};
            while (WaitForSingleObject(stop_.get(), 0) != WAIT_OBJECT_0) {
                ResetEvent(signal->changed.get());
                const auto revision = signal->revision.load();
                const auto lifecycle = Snapshot();
                SurfaceReport report;
                report.instance = lifecycle.instance;
                report.epoch = lifecycle.epoch;
                report.result = HRESULT_FROM_WIN32(ERROR_NOT_CONNECTED);
                if (lifecycle.state == 1) {
                    HRESULT result = S_OK;
                    if (!service) {
                        ComPtr<IWTSPluginServiceProvider> provider;
                        ComPtr<IUnknown> unknown;
                        result = rpc.Invoke([&] { return manager->QueryInterface(IID_PPV_ARGS(&provider)); });
                        if (SUCCEEDED(result) && provider)
                            result = rpc.Invoke([&] { return provider->GetService(WindowInfoServiceId, &unknown); });
                        if (SUCCEEDED(result) && unknown)
                            result = rpc.Invoke([&] { return unknown.As(&service); });
                        if (SUCCEEDED(result) && !service) result = E_NOINTERFACE;
                        if (!service) report.status = result == E_PENDING ? SurfaceStatus::Pending :
                            (result == E_NOINTERFACE || result == E_NOTIMPL ||
                                result == HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED) ?
                                SurfaceStatus::ServiceUnavailable : SurfaceStatus::ServiceError);
                    }
                    if (service) {
                        RdpSessionType type{};
                        DWORD processId{};
                        WTSWindowInfo info{};
                        result = rpc.Invoke([&] { return service->GetRdpSessionType(&type); });
                        report.status = SurfaceStatus::ServiceError;
                        if (SUCCEEDED(result) && type != Desktop) {
                            report.status = SurfaceStatus::UnsupportedSession;
                        } else if (SUCCEEDED(result)) {
                            if (!subscriptionAttempted) {
                                subscriptionAttempted = true;
                                // Failure is optional; periodic queries still work. Attempt
                                // one bounded unsubscribe even if subscription failed ambiguously.
                                rpc.Invoke([&] { return service->SubscribeWindowChanged(nullptr, callback.Get()); });
                            }
                            result = rpc.Invoke([&] { return service->GetRdpClientProcessId(&processId); });
                            if (SUCCEEDED(result))
                                result = rpc.Invoke([&] { return service->GetWindowInfo(nullptr, &info); });
                            if (SUCCEEDED(result)) {
                                report.status = SurfaceStatus::InvalidGeometry;
                                if (ValidSurfaceGeometry(info)) {
                                    report.status = SurfaceStatus::IdentityAmbiguous;
                                    if (SurfaceProcessIdentity(processId, info.Hwnd, report)) {
                                        report.hwnd = reinterpret_cast<uint64_t>(info.Hwnd);
                                        report.width = info.Width;
                                        report.height = info.Height;
                                        report.viewWidth = info.ViewWidth;
                                        report.viewHeight = info.ViewHeight;
                                        report.offsetX = info.ViewOffsetX;
                                        report.offsetY = info.ViewOffsetY;
                                        report.scale = info.Scale;
                                        report.flags = 1;
                                    }
                                }
                            }
                        }
                        if (result == E_PENDING) report.status = SurfaceStatus::Pending;
                    }
                    report.result = result;
                }
                const auto current = Snapshot();
                // Never publish geometry obtained across a lifecycle/callback boundary.
                if (current.epoch != lifecycle.epoch || current.state != lifecycle.state ||
                    revision != signal->revision.load()) {
                    WaitForSingleObject(stop_.get(), 25);
                    continue;
                }
                if (generation == 0 || report.epoch != previous.epoch ||
                    revision != previousRevision ||
                    std::memcmp(&report.hwnd, &previous.hwnd, sizeof(report) - offsetof(SurfaceReport, hwnd)) != 0)
                    ++generation;
                if (previous.generation == 0 || report.epoch != previous.epoch ||
                    report.status != previous.status || report.result != previous.result)
                    WriteDiagnostic(DiagnosticKind::SurfaceStatus, report.result,
                        static_cast<uint32_t>(report.status), Instance());
                report.generation = generation;
                previous = report;
                previousRevision = revision;
                if (!pipe) {
                    pipe.reset(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                        OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
                    const auto error = pipe ? VerifyPipeServer(pipe.get()) : GetLastError();
                    if (error != ERROR_SUCCESS) {
                        WriteDiagnostic(DiagnosticKind::ReporterFailure, HRESULT_FROM_WIN32(error), 3, Instance());
                        pipe.reset();
                    }
                }
                if (pipe && !Write(pipe.get(), completed.get(), report)) {
                    WriteDiagnostic(DiagnosticKind::ReporterFailure, HRESULT_FROM_WIN32(ERROR_WRITE_FAULT), 3, Instance());
                    pipe.reset();
                }
                HANDLE waits[]{stop_.get(), signal->changed.get()};
                if (WaitForMultipleObjects(2, waits, FALSE, 1000) == WAIT_OBJECT_0) break;
            }
        } catch (...) { WriteDiagnostic(DiagnosticKind::ReporterFailure, CurrentException(), 1, Instance()); }
    }
    void Run(const std::wstring& name) noexcept {
        try {
            WinHandle completed(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!completed) {
                WriteDiagnostic(DiagnosticKind::ReporterFailure, HRESULT_FROM_WIN32(GetLastError()), 0, Instance());
                return;
            }
            WinHandle pipe;
            while (WaitForSingleObject(stop_.get(), 0) != WAIT_OBJECT_0) {
                if (!pipe) {
                    pipe.reset(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                        OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
                    const auto error = pipe ? VerifyPipeServer(pipe.get()) : GetLastError();
                    if (error != ERROR_SUCCESS) {
                        WriteDiagnostic(DiagnosticKind::ReporterFailure, HRESULT_FROM_WIN32(error), 2, Instance());
                        pipe.reset();
                    }
                    if (!pipe) {
                        if (WaitForSingleObject(stop_.get(), 1000) == WAIT_OBJECT_0) break;
                        continue;
                    }
                }
                ClientReport snapshot;
                {
                    std::lock_guard lock(lock_);
                    ResetEvent(changed_.get());
                    snapshot = report_;
                }
                if (!Write(pipe.get(), completed.get(), snapshot)) {
                    WriteDiagnostic(DiagnosticKind::ReporterFailure, HRESULT_FROM_WIN32(ERROR_WRITE_FAULT), 2, Instance());
                    pipe.reset();
                    if (WaitForSingleObject(stop_.get(), 1000) == WAIT_OBJECT_0) break;
                    continue;
                }
                HANDLE waits[]{stop_.get(), changed_.get()};
                if (WaitForMultipleObjects(2, waits, FALSE, 3000) == WAIT_OBJECT_0) break;
            }
        } catch (...) { WriteDiagnostic(DiagnosticKind::ReporterFailure, CurrentException(), 0, Instance()); }
    }
    ServerLease lease_;
    const ClientConnectionPipeResolver resolver_;
    WinHandle stop_{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    WinHandle changed_{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::mutex lock_;
    ClientReport report_;
    std::atomic<bool> stopReported_{};
};

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
                self->Failure(initialized);
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
        if (size && !bytes) { Failure(E_POINTER); Stop(); return E_POINTER; }
        if (size > MaxRecordBytes) {
            Failure(HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE));
            Stop();
            return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
        }
        try {
            std::lock_guard lock(queueLock_);
            if (Stopped()) return HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);
            if (!size) return S_OK;
            if (size > MaxQueuedBytes - queuedBytes_) {
                Failure(HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA));
                Stop();
                return HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA);
            }
            queue_.emplace_back(bytes, bytes + size);
            queuedBytes_ += size;
            SetEvent(dataReady_.get());
            return S_OK;
        } catch (...) {
            const auto result = CurrentException();
            Failure(result);
            Stop();
            return result;
        }
    }
private:
    void Failure(HRESULT result) noexcept {
        if (result != HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED) && !failureReported_.exchange(true))
            WriteDiagnostic(DiagnosticKind::BridgeFailure, result, 0, correlation_);
    }
    bool Stopped() const noexcept { return WaitForSingleObject(stop_.get(), 0) == WAIT_OBJECT_0; }

    static void CALLBACK CancelRpc(PTP_CALLBACK_INSTANCE, void* context, PTP_TIMER) noexcept {
        auto& self = *static_cast<Bridge*>(context);
        const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(initialized)) return;
        {
            std::lock_guard lock(self.rpcLock_);
            if (self.rpcThread_ && ((self.Stopped() && !self.rpcClosing_) ||
                GetTickCount64() - self.rpcSince_ >= RpcTimeoutMs)) {
                if (GetTickCount64() - self.rpcSince_ >= RpcTimeoutMs)
                    self.Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
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
        const auto named = GetBrokerPipeName(name);
        if (FAILED(named)) { Failure(named); return {}; }
        const auto deadline = GetTickCount64() + ConnectTimeoutMs;
        while (!Stopped()) {
            WinHandle pipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
            if (pipe) {
                const auto verified = VerifyPipeServer(pipe.get());
                if (verified == ERROR_SUCCESS) return pipe;
                Failure(HRESULT_FROM_WIN32(verified));
                return {};
            }
            const auto error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PIPE_BUSY && error != ERROR_PIPE_NOT_CONNECTED) {
                Failure(HRESULT_FROM_WIN32(error));
                return {};
            }
            if (GetTickCount64() >= deadline) {
                Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                return {};
            }
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
        if (FAILED(initialized)) { Failure(initialized); Stop(); return; }
        const auto cancellation = CoEnableCallCancellation(nullptr);
        if (FAILED(cancellation)) Failure(cancellation);
        if (SUCCEEDED(cancellation)) {
            WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!event) Failure(HRESULT_FROM_WIN32(GetLastError()));
            std::array<BYTE, TransportChunkBytes> bytes{};
            while (event && !Stopped()) {
                DWORD count{};
                const auto error = PipeIo(pipe, event.get(), false, bytes.data(), static_cast<DWORD>(bytes.size()), count);
                if (error) {
                    Failure(HRESULT_FROM_WIN32(error));
                    break;
                }
                const auto result = ChannelCall(false, count, bytes.data());
                if (FAILED(result)) { Failure(result); break; }
            }
            CoDisableCallCancellation(nullptr);
        }
        Stop();
        CoUninitialize();
    }
    void WritePipe(HANDLE pipe) {
        WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event) Failure(HRESULT_FROM_WIN32(GetLastError()));
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
                const auto error = PipeIo(pipe, event.get(), true, bytes.data() + sent,
                    static_cast<DWORD>(std::min(bytes.size() - sent, size_t{TransportChunkBytes})), count);
                if (error) {
                    Failure(HRESULT_FROM_WIN32(error));
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
        if (FAILED(cancellation)) Failure(cancellation);
        FILETIME due{};
        const LONGLONG relative = -1000000; // First watchdog tick in 100 ms.
        std::memcpy(&due, &relative, sizeof(due));
        SetThreadpoolTimer(timer_, &due, 100, 0);
        WinHandle pipe;
        std::thread reader;
        try {
            if (SUCCEEDED(cancellation)) pipe = ConnectPipe();
            if (pipe) {
                WriteDiagnostic(DiagnosticKind::BridgeOpen, S_OK, 0, correlation_);
                reader = std::thread([this, handle = pipe.get()] { ReadPipe(handle); });
                WritePipe(pipe.get());
            }
        } catch (...) { Failure(CurrentException()); }
        Stop();
        if (reader.joinable()) reader.join();
        if (!remoteClosed_.load() && SUCCEEDED(cancellation)) {
            const auto result = ChannelCall(true);
            if (FAILED(result)) Failure(result);
        }
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
        WriteDiagnostic(DiagnosticKind::BridgeClose, S_OK, remoteClosed_.load() ? 1u : 0u, correlation_);
    }

    ServerLease lease_;
    const GUID correlation_{NewDiagnosticCorrelation()};
    std::atomic<bool> failureReported_{};
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

    void Stop(bool terminate, ClientReporter* reporter = nullptr) noexcept {
        std::lock_guard guard(lock);
        accepting = false;
        terminated = terminated || terminate;
        if (reporter) {
            if (terminated) reporter->Stop();
            else reporter->Disconnected();
        }
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
        } catch (...) {
            const auto result = CurrentException();
            WriteDiagnostic(DiagnosticKind::BridgeFailure, result);
            return result;
        }
    }
private:
    std::shared_ptr<PluginState> state_;
};

class Plugin final : public ComObject<IWTSPlugin> {
public:
    explicit Plugin(ClientConnectionPipeResolver resolver) :
        state_(std::make_shared<PluginState>()), reporter_(std::make_shared<ClientReporter>(resolver)) {}
    ~Plugin() override { Terminated(); }
    HRESULT STDMETHODCALLTYPE Initialize(IWTSVirtualChannelManager* manager) noexcept override {
        if (!manager) return E_POINTER;
        try {
            {
                std::lock_guard lock(state_->lock);
                if (initialized_ || state_->terminated) return E_UNEXPECTED;
                initialized_ = true;
                reporter_->Start(manager);
            }
            ComPtr<ListenerCallback> callback;
            callback.Attach(new ListenerCallback(state_));
            ComPtr<IWTSListener> listener;
            const auto result = manager->CreateListener(
                reinterpret_cast<const unsigned char*>(ChannelName), 0, callback.Get(), &listener);
            if (FAILED(result)) {
                std::lock_guard lock(state_->lock);
                listenerFailed_ = true;
                state_->accepting = false;
                WriteDiagnostic(DiagnosticKind::ListenerUnavailable, result, 0, reporter_->Instance());
                WriteDiagnostic(DiagnosticKind::PluginInitialize, S_OK, 1, reporter_->Instance());
                return S_OK;
            }
            {
                std::lock_guard lock(state_->lock);
                if (!state_->terminated) listener_ = std::move(listener);
            }
            WriteDiagnostic(DiagnosticKind::PluginInitialize, S_OK, 0, reporter_->Instance());
            return S_OK;
        } catch (...) {
            const auto result = CurrentException();
            WriteDiagnostic(DiagnosticKind::PluginInitialize, result, 0, reporter_->Instance());
            state_->Stop(true, reporter_.get());
            return result;
        }
    }
    HRESULT STDMETHODCALLTYPE Connected() noexcept override {
        std::lock_guard lock(state_->lock);
        if (state_->terminated) return E_UNEXPECTED;
        const auto result = reporter_->Connected();
        if (FAILED(result)) return result;
        state_->accepting = !listenerFailed_;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Disconnected(DWORD) noexcept override {
        state_->Stop(false, reporter_.get());
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Terminated() noexcept override {
        state_->Stop(true, reporter_.get());
        ComPtr<IWTSListener> old;
        {
            std::lock_guard lock(state_->lock);
            old.Swap(listener_);
        }
        return S_OK;
    }
private:
    std::shared_ptr<PluginState> state_;
    std::shared_ptr<ClientReporter> reporter_;
    ComPtr<IWTSListener> listener_;
    bool initialized_{};
    bool listenerFailed_{};
};

class PluginFactory : public ComObject<IWTSPluginFactory> {
public:
    PluginFactory(bool enabled, ClientConnectionPipeResolver resolver) noexcept : enabled_(enabled), resolver_(resolver) {}
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
        try { *plugin = new Plugin(resolver_); return S_OK; }
        catch (...) { return CurrentException(); }
    }
private:
    const bool enabled_;
    const ClientConnectionPipeResolver resolver_;
};

// COM may activate IUnknown first and query the requested interface afterward.
// Both legacy interfaces must therefore belong to the same activation object.
class LegacyActivation final : public PluginFactory, public IWTSPlugin {
public:
    explicit LegacyActivation(ClientConnectionPipeResolver resolver) : PluginFactory(true, resolver) {
        plugin_.Attach(new Plugin(resolver));
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) noexcept override {
        if (!value) return E_POINTER;
        if (iid != __uuidof(IWTSPlugin)) return PluginFactory::QueryInterface(iid, value);
        *value = static_cast<IWTSPlugin*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return PluginFactory::AddRef(); }
    ULONG STDMETHODCALLTYPE Release() noexcept override { return PluginFactory::Release(); }
    HRESULT STDMETHODCALLTYPE Initialize(IWTSVirtualChannelManager* manager) noexcept override {
        return plugin_->Initialize(manager);
    }
    HRESULT STDMETHODCALLTYPE Connected() noexcept override { return plugin_->Connected(); }
    HRESULT STDMETHODCALLTYPE Disconnected(DWORD reason) noexcept override { return plugin_->Disconnected(reason); }
    HRESULT STDMETHODCALLTYPE Terminated() noexcept override { return plugin_->Terminated(); }
private:
    ComPtr<IWTSPlugin> plugin_;
};
}

HRESULT CreatePluginFactory(IWTSPluginFactory** factory, ClientConnectionPipeResolver resolver) noexcept {
    if (!factory) return E_POINTER;
    *factory = nullptr;
    PluginLoadingMode selected{};
    const auto result = ReadPluginLoadingMode(&selected);
    return FAILED(result) ? result : CreatePluginFactory(selected, factory, resolver);
}

HRESULT CreatePluginFactory(PluginLoadingMode selectedMode, IWTSPluginFactory** factory,
    ClientConnectionPipeResolver resolver) noexcept {
    return CreatePluginFactory(PluginLoadingMode::Modern, selectedMode, factory, resolver);
}

HRESULT CreatePluginFactory(PluginLoadingMode activationMode, PluginLoadingMode selectedMode,
    IWTSPluginFactory** factory, ClientConnectionPipeResolver resolver) noexcept {
    InitializeDiagnostics();
    if (!factory) return E_POINTER;
    *factory = nullptr;
    if (!IsValidLoadingMode(activationMode) || !IsValidLoadingMode(selectedMode) || !resolver) return E_INVALIDARG;
    try { *factory = new PluginFactory(activationMode == selectedMode, resolver); return S_OK; }
    catch (...) { return CurrentException(); }
}

HRESULT CreatePluginActivation(PluginLoadingMode activationMode, PluginLoadingMode selectedMode,
    REFIID iid, void** object, ClientConnectionPipeResolver resolver) noexcept {
    InitializeDiagnostics();
    if (!object) return E_POINTER;
    *object = nullptr;
    if (!IsValidLoadingMode(activationMode) || !IsValidLoadingMode(selectedMode) || !resolver) return E_INVALIDARG;
    if (activationMode == PluginLoadingMode::Legacy) {
        if (selectedMode != PluginLoadingMode::Legacy && iid == __uuidof(IWTSPlugin))
            return HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY);
        if (selectedMode == PluginLoadingMode::Legacy) {
            if (iid != IID_IUnknown && iid != __uuidof(IWTSPluginFactory) && iid != __uuidof(IWTSPlugin))
                return E_NOINTERFACE;
            try {
                ComPtr<IWTSPluginFactory> activation;
                activation.Attach(new LegacyActivation(resolver));
                return activation->QueryInterface(iid, object);
            } catch (...) { return CurrentException(); }
        }
    }
    if (iid != IID_IUnknown && iid != __uuidof(IWTSPluginFactory)) return E_NOINTERFACE;
    ComPtr<IWTSPluginFactory> factory;
    const auto result = CreatePluginFactory(activationMode, selectedMode, &factory, resolver);
    return FAILED(result) ? result : factory->QueryInterface(iid, object);
}

HRESULT GetClientConnectionPipeName(std::wstring& name) noexcept {
    const auto result = GetBrokerPipeName(name);
    if (FAILED(result)) return result;
    try { name += L".Clients"; return S_OK; }
    catch (...) { return CurrentException(); }
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

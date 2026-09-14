#include "RdpPlugin.h"
#include "ServerLifetime.h"
#include "SurfaceMetadata.h"
#include "WinHandle.h"
#include <wrl/client.h>
#include <array>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <thread>
#include <mutex>
#include <limits>

using Microsoft::WRL::ComPtr;
using Mode = ral::PluginLoadingMode;

namespace {
unsigned checks{};
void Check(bool value, const char* message) {
    ++checks;
    if (!value) throw std::runtime_error(message);
}
void CheckHr(HRESULT result, const char* message) {
    if (FAILED(result)) std::fprintf(stderr, "%s: 0x%08lX\n", message, static_cast<unsigned long>(result));
    Check(SUCCEEDED(result), message);
}

std::wstring ModulePath() {
    std::array<wchar_t, 32768> path{};
    const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    Check(length && length < path.size(), "cross-process test executable path");
    return {path.data(), length};
}

class ProxyRegistration {
public:
    void Register() {
        auto path = ModulePath();
        path = path.substr(0, path.find_last_of(L'\\') + 1) + L"AgentLink.ProxyStub.dll";
        module_ = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(module_ != nullptr, "load test-owned generated proxy module");
        using GetFactory = HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
        GetFactory getFactory{};
        auto address = GetProcAddress(module_, "DllGetClassObject");
        std::memcpy(&getFactory, &address, sizeof(getFactory));
        Check(getFactory != nullptr, "generated proxy factory export for process-local registration");
        const CLSID proxy{0x586b220f, 0x6970, 0x4e64, {0xb5, 0xdd, 0x43, 0x8a, 0x29, 0x8a, 0x87, 0x6a}};
        ComPtr<IPSFactoryBuffer> factory;
        CheckHr(getFactory(proxy, IID_PPV_ARGS(&factory)), "get generated proxy factory");
        CheckHr(CoRegisterClassObject(proxy, factory.Get(), CLSCTX_INPROC_SERVER, REGCLS_MULTIPLEUSE, &cookie_),
            "register generated proxy factory only in this test process");
        for (const auto& iid : {__uuidof(IWTSPluginFactory), __uuidof(IWTSPlugin),
            __uuidof(IWTSVirtualChannelManager), __uuidof(IWTSListener), __uuidof(IWTSListenerCallback),
            __uuidof(IWTSVirtualChannel), __uuidof(IWTSVirtualChannelCallback),
            __uuidof(IWTSPluginServiceProvider), __uuidof(IWTSWindowInfoService), __uuidof(IWTSWindowChangedCallback)})
            CheckHr(CoRegisterPSClsid(iid, proxy), "map test process interfaces without registry writes");
    }
    ~ProxyRegistration() {
        if (cookie_) CoRevokeClassObject(cookie_);
        // COM can cache these factories until CoUninitialize at process teardown.
        // Keep the module loaded for the remainder of this test process.
    }
private:
    HMODULE module_{};
    DWORD cookie_{};
};

HRESULT ReadMode(void* context, Mode* mode) {
    *mode = *static_cast<Mode*>(context);
    return S_OK;
}

std::wstring surfaceTestPipe;
HRESULT TestPipeResolver(std::wstring& name) noexcept {
    try { name = surfaceTestPipe; return S_OK; }
    catch (...) { return E_OUTOFMEMORY; }
}

class SurfaceManager final : public ral::ComObject<IWTSVirtualChannelManager>,
    public IWTSPluginServiceProvider, public IWTSWindowInfoService {
public:
    std::atomic<HRESULT> windowResult{E_PENDING};
    std::atomic<RdpSessionType> sessionType{Desktop};
    std::atomic<int> width{1920};
    std::atomic<bool> blockQuery{};
    bool serviceAvailable{true};
    ral::WinHandle queryEntered{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    ral::WinHandle subscribed{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    ral::WinHandle unsubscribed{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::atomic<unsigned> queries{};
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) noexcept override {
        if (!value) return E_POINTER;
        if (iid == __uuidof(IWTSPluginServiceProvider)) *value = static_cast<IWTSPluginServiceProvider*>(this);
        else if (iid == __uuidof(IWTSWindowInfoService)) *value = static_cast<IWTSWindowInfoService*>(this);
        else return ComObject::QueryInterface(iid, value);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return ComObject::AddRef(); }
    ULONG STDMETHODCALLTYPE Release() noexcept override { return ComObject::Release(); }
    HRESULT STDMETHODCALLTYPE CreateListener(const unsigned char*, ULONG, IWTSListenerCallback*, IWTSListener** listener) noexcept override {
        if (!listener) return E_POINTER;
        *listener = nullptr;
        return E_NOTIMPL; // Surface metadata must be independent of DVC readiness.
    }
    HRESULT STDMETHODCALLTYPE GetService(GUID service, IUnknown** object) noexcept override {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (!serviceAvailable || service != ral::WindowInfoServiceId) return E_NOINTERFACE;
        return QueryInterface(IID_IUnknown, reinterpret_cast<void**>(object));
    }
    HRESULT STDMETHODCALLTYPE GetWindowInfo(HWND remote, WTSWindowInfo* info) noexcept override {
        ++queries;
        if (remote || !info) return E_INVALIDARG;
        *info = {nullptr, 1080, width.load(), 960, 540, 7, 9, 0.5f};
        if (blockQuery) {
            SetEvent(queryEntered.get());
            for (unsigned attempt = 0; attempt < 100 && blockQuery; ++attempt) {
                if (CoTestCancel() == RPC_E_CALL_CANCELED) return RPC_E_CALL_CANCELED;
                Sleep(100);
            }
        }
        return windowResult.load();
    }
    HRESULT STDMETHODCALLTYPE GetRdpClientProcessId(ULONG* process) noexcept override {
        if (!process) return E_POINTER;
        *process = GetCurrentProcessId();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRdpSessionType(RdpSessionType* type) noexcept override {
        if (!type) return E_POINTER;
        *type = sessionType.load();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SubscribeWindowChanged(HWND remote, IWTSWindowChangedCallback* callback) noexcept override {
        if (remote || !callback) return E_INVALIDARG;
        std::lock_guard lock(lock_);
        callback_ = callback;
        lateCallback_ = callback;
        SetEvent(subscribed.get());
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnsubscribeWindowChanged(HWND remote, IWTSWindowChangedCallback* callback) noexcept override {
        if (remote || !callback) return E_INVALIDARG;
        std::lock_guard lock(lock_);
        callback_.Reset();
        SetEvent(unsubscribed.get());
        return S_OK;
    }
    void Notify() {
        ComPtr<IWTSWindowChangedCallback> callback;
        {
            std::lock_guard lock(lock_);
            callback = callback_;
        }
        WTSWindowInfo info{nullptr, 1080, width.load(), 960, 540, 7, 9, 0.5f};
        Check(callback && callback->WindowChanged(&info) == S_OK, "real cross-process window callback marshals public structure");
    }
    void NotifyAfterUnsubscribe() {
        WTSWindowInfo info{};
        Check(lateCallback_ && lateCallback_->WindowChanged(&info) == S_OK, "late callbacks are inert after unsubscribe");
        lateCallback_.Reset();
    }
private:
    std::mutex lock_;
    ComPtr<IWTSWindowChangedCallback> callback_;
    ComPtr<IWTSWindowChangedCallback> lateCallback_;
};

ral::WinHandle SurfacePipe() {
    const auto name = surfaceTestPipe + L".Surfaces";
    ral::WinHandle pipe(CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED |
        FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT |
        PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, nullptr));
    Check(static_cast<bool>(pipe), "random test-owned surface pipe only");
    return pipe;
}

void SurfaceConnect(HANDLE pipe) {
    ral::WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    OVERLAPPED operation{};
    operation.hEvent = event.get();
    if (ConnectNamedPipe(pipe, &operation) || GetLastError() == ERROR_PIPE_CONNECTED) return;
    Check(GetLastError() == ERROR_IO_PENDING, "surface pipe pending connect");
    DWORD bytes{};
    const auto wait = WaitForSingleObject(event.get(), 8000);
    if (wait != WAIT_OBJECT_0) {
        CancelIoEx(pipe, &operation);
        GetOverlappedResult(pipe, &operation, &bytes, TRUE);
    }
    Check(wait == WAIT_OBJECT_0 && GetOverlappedResult(pipe, &operation, &bytes, FALSE), "surface reporter connects");
}

ral::SurfaceReport ReadSurface(HANDLE pipe, ral::SurfaceStatus status, uint64_t after = 0) {
    const auto deadline = GetTickCount64() + 8000;
    while (GetTickCount64() < deadline) {
        ral::SurfaceReport report;
        DWORD offset{};
        while (offset < sizeof(report)) {
            ral::WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED operation{};
            operation.hEvent = event.get();
            DWORD read{};
            if (!ReadFile(pipe, reinterpret_cast<BYTE*>(&report) + offset,
                static_cast<DWORD>(sizeof(report)) - offset, &read, &operation)) {
                Check(GetLastError() == ERROR_IO_PENDING, "surface pipe pending read");
                const auto wait = WaitForSingleObject(event.get(), 8000);
                if (wait != WAIT_OBJECT_0) {
                    CancelIoEx(pipe, &operation);
                    GetOverlappedResult(pipe, &operation, &read, TRUE);
                }
                Check(wait == WAIT_OBJECT_0 && GetOverlappedResult(pipe, &operation, &read, FALSE),
                    "bounded surface record");
            }
            Check(read != 0, "complete surface report");
            offset += read;
        }
        Check(std::memcmp(report.magic, "RALSRF1", 8) == 0 && report.instance != GUID_NULL &&
            report.epoch != GUID_NULL && report.generation > 0 && report.reserved[0] == 0 && report.reserved[1] == 0,
            "versioned surface record identities and bounds");
        if (report.status == status && report.generation > after) return report;
    }
    throw std::runtime_error("surface status transition timeout");
}

GUID ProbeSurface(IWTSPlugin* plugin) {
    auto pipe = SurfacePipe();
    ComPtr<SurfaceManager> manager;
    manager.Attach(new SurfaceManager);
    CheckHr(plugin->Initialize(manager.Get()), "initialize cross-process fake public service manager without DVC");
    CheckHr(plugin->Connected(), "connected starts optional surface metadata query");
    SurfaceConnect(pipe.get());
    auto pending = ReadSurface(pipe.get(), ral::SurfaceStatus::Pending);
    while (pending.result != E_PENDING)
        pending = ReadSurface(pipe.get(), ral::SurfaceStatus::Pending, pending.generation);
    Check(WaitForSingleObject(manager->subscribed.get(), 5000) == WAIT_OBJECT_0,
        "public window-change callback subscription crossed process boundary");
    manager->windowResult = S_OK;
    manager->Notify();
    auto ambiguous = ReadSurface(pipe.get(), ral::SurfaceStatus::IdentityAmbiguous, pending.generation);
    Check(ambiguous.hwnd == 0 && ambiguous.flags == 0 && ambiguous.processId == 0,
        "synthetic/no HWND is not a process or connection ownership proof");
    manager->width = -1;
    manager->Notify();
    auto invalid = ReadSurface(pipe.get(), ral::SurfaceStatus::InvalidGeometry, ambiguous.generation);
    manager->width = 1920;
    manager->windowResult = E_FAIL;
    manager->Notify();
    auto error = ReadSurface(pipe.get(), ral::SurfaceStatus::ServiceError, invalid.generation);
    Check(error.result == E_FAIL && error.hwnd == 0, "failed geometry queries clear prior evidence");
    manager->windowResult = S_OK;
    manager->blockQuery = true;
    manager->Notify();
    Check(WaitForSingleObject(manager->queryEntered.get(), 5000) == WAIT_OBJECT_0, "fake remote service enters blocked query");
    const auto callStarted = GetTickCount64();
    CheckHr(plugin->Connected(), "blocked surface service never blocks COM lifecycle callbacks");
    Check(GetTickCount64() - callStarted < 1000, "lifecycle callback remains fast during blocked optional service");
    auto canceled = ReadSurface(pipe.get(), ral::SurfaceStatus::ServiceError, error.generation);
    Check(canceled.result == RPC_E_CALL_CANCELED, "three-second COM deadline cancels a hung public service query");
    manager->blockQuery = false;
    manager->sessionType = RemoteApp;
    manager->Notify();
    auto remoteApp = ReadSurface(pipe.get(), ral::SurfaceStatus::UnsupportedSession, canceled.generation);
    Check(remoteApp.hwnd == 0, "RemoteApp does not inherit full-desktop metadata");
    manager->sessionType = Desktop;
    CheckHr(plugin->Disconnected(0), "disconnect invalidates surface");
    auto disconnected = ReadSurface(pipe.get(), ral::SurfaceStatus::Pending, remoteApp.generation);
    Check(disconnected.hwnd == 0 && disconnected.epoch == pending.epoch, "disconnect clears metadata without changing instance");
    CheckHr(plugin->Connected(), "reconnect after geometry publication");
    auto reconnected = ReadSurface(pipe.get(), ral::SurfaceStatus::IdentityAmbiguous, disconnected.generation);
    Check(reconnected.instance == pending.instance && reconnected.epoch != pending.epoch, "surface reconnect invalidates epoch");
    pipe.reset();
    pipe = SurfacePipe();
    SurfaceConnect(pipe.get());
    auto restarted = ReadSurface(pipe.get(), ral::SurfaceStatus::IdentityAmbiguous);
    Check(restarted.instance == reconnected.instance && restarted.epoch == reconnected.epoch &&
        restarted.generation == reconnected.generation, "surface broker restart retains epoch and stable metadata generation");
    CheckHr(plugin->Terminated(), "terminate does not join the surface worker");
    Check(WaitForSingleObject(manager->unsubscribed.get(), 5000) == WAIT_OBJECT_0, "bounded cross-process unsubscribe");
    manager->NotifyAfterUnsubscribe();
    return reconnected.instance;
}

class TestServer {
public:
    std::array<CLSID, 4> classes{};

    void Start() {
        auto path = ModulePath();
        std::wstring command = L"\"" + path + L"\" --activation-server";
        std::wstring eventPrefix;
        for (auto& clsid : classes) {
            CheckHr(CoCreateGuid(&clsid), "unique test-owned COM class identity");
            std::array<wchar_t, 40> text{};
            Check(StringFromGUID2(clsid, text.data(), static_cast<int>(text.size())) != 0, "test COM class GUID text");
            if (eventPrefix.empty()) eventPrefix = L"Local\\RemoteAgentLink.NativeActivation." + std::wstring(text.data());
            command += L" " + std::wstring(text.data());
        }
        surfaceTestPipe = L"\\\\.\\pipe\\RemoteAgentLink.Test.Surface." + eventPrefix.substr(6);
        ready_.reset(CreateEventW(nullptr, TRUE, FALSE, (eventPrefix + L".ready").c_str()));
        Check(ready_ && GetLastError() != ERROR_ALREADY_EXISTS, "create unique test activation ready event");
        stop_.reset(CreateEventW(nullptr, TRUE, FALSE, (eventPrefix + L".stop").c_str()));
        Check(stop_ && GetLastError() != ERROR_ALREADY_EXISTS, "create unique test activation stop event");
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process{};
        Check(CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
            nullptr, nullptr, &startup, &process) != FALSE, "launch isolated COM activation test server");
        process_.reset(process.hProcess);
        ral::WinHandle thread(process.hThread);
        const HANDLE waits[]{ready_.get(), process_.get()};
        Check(WaitForMultipleObjects(2, waits, FALSE, 10000) == WAIT_OBJECT_0, "isolated COM test server is ready");
    }
    void Finish() {
        SetEvent(stop_.get());
        Check(WaitForSingleObject(process_.get(), 10000) == WAIT_OBJECT_0, "isolated COM test server exits");
        DWORD exitCode{};
        Check(GetExitCodeProcess(process_.get(), &exitCode) && exitCode == 0, "isolated COM test server succeeds");
    }
    void CheckAlive() const {
        Check(WaitForSingleObject(process_.get(), 750) == WAIT_TIMEOUT,
            "COM server survives repeated idle checks with outstanding references");
    }
    ~TestServer() {
        if (stop_) SetEvent(stop_.get());
        if (process_ && WaitForSingleObject(process_.get(), 5000) != WAIT_OBJECT_0) {
            TerminateProcess(process_.get(), 1);
            WaitForSingleObject(process_.get(), 5000);
        }
    }
private:
    ral::WinHandle ready_, stop_, process_;
};
}

int CrossProcessActivationServer(char** arguments) {
    const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) return 1;
    int result{};
    {
        ral::ServerLease keepAlive;
        ProxyRegistration proxy;
        std::array<DWORD, 4> registrations{};
        std::array<ComPtr<IClassFactory>, 4> factories;
        std::array modes{Mode::Modern, Mode::Legacy, Mode::Legacy, Mode::Modern};
        try {
            proxy.Register();
            const std::string first(arguments[0]);
            const std::wstring eventPrefix = L"Local\\RemoteAgentLink.NativeActivation." +
                std::wstring(first.begin(), first.end());
            surfaceTestPipe = L"\\\\.\\pipe\\RemoteAgentLink.Test.Surface." + eventPrefix.substr(6);
            ral::WinHandle ready(OpenEventW(EVENT_MODIFY_STATE, FALSE, (eventPrefix + L".ready").c_str()));
            ral::WinHandle stop(OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, (eventPrefix + L".stop").c_str()));
            Check(ready && stop, "open only this activation test's synchronization events");
            for (size_t i = 0; i < registrations.size(); ++i) {
                const std::string text(arguments[i]);
                const std::wstring wide(text.begin(), text.end());
                CLSID clsid{};
                CheckHr(CLSIDFromString(wide.c_str(), &clsid), "parse test-owned activation class");
                Check(clsid != ral::PluginFactoryClsid && clsid != ral::LegacyPluginClsid,
                    "test server must never register a production activation class");
                CheckHr(ral::CreatePluginClassFactory(i < 2 ? ral::PluginFactoryClsid : ral::LegacyPluginClsid,
                    &factories[i], ReadMode, &modes[i], TestPipeResolver), "construct production class factory with fixed test selection");
                CheckHr(CoRegisterClassObject(clsid, factories[i].Get(), CLSCTX_LOCAL_SERVER,
                    REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED, &registrations[i]), "register ephemeral test COM class");
            }
            CheckHr(CoResumeClassObjects(), "resume isolated test activation classes");
            Check(SetEvent(ready.get()) != FALSE, "signal isolated test activation readiness");
            std::atomic<bool> timedOut{};
            std::thread watchdog([&] {
                if (WaitForSingleObject(stop.get(), 90000) != WAIT_OBJECT_0) {
                    timedOut = true;
                    SetEvent(stop.get());
                }
            });
            const auto waited = ral::WaitForServerShutdown(stop.get(), 250);
            SetEvent(stop.get());
            watchdog.join();
            CheckHr(waited, "production shutdown loop keeps published classes alive");
            Check(!timedOut, "bounded isolated test server lifetime");
        } catch (const std::exception& error) {
            std::fprintf(stderr, "COM activation test server: %s\n", error.what());
            result = 1;
        }
        CoSuspendClassObjects();
        for (const auto registration : registrations) if (registration) CoRevokeClassObject(registration);
    }
    CoUninitialize();
    return result;
}

unsigned CrossProcessActivationTests() {
    checks = 0;
    WTSWindowInfo geometry{nullptr, 1080, 1920, 960, 540, 0, 0, 0.5f};
    Check(ral::ValidSurfaceGeometry(geometry), "valid scaled surface dimensions");
    for (const auto value : {0.0f, -1.0f, std::numeric_limits<float>::infinity(),
        std::numeric_limits<float>::quiet_NaN(), 33.0f}) {
        geometry.Scale = value;
        Check(!ral::ValidSurfaceGeometry(geometry), "reject nonfinite or unbounded geometry scale");
    }
    geometry.Scale = 1;
    geometry.ViewOffsetX = -1;
    Check(!ral::ValidSurfaceGeometry(geometry), "reject negative viewport offsets");
    geometry.ViewOffsetX = 0;
    geometry.Width = 65537;
    Check(!ral::ValidSurfaceGeometry(geometry), "reject unbounded surface dimensions");
    ProxyRegistration proxy;
    proxy.Register();
    TestServer server;
    server.Start();
    for (size_t i = 0; i < server.classes.size(); ++i) {
        const bool enabled = i == 0 || i == 2;
        ComPtr<IWTSPluginFactory> factory;
        CheckHr(CoCreateInstance(server.classes[i], nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&factory)),
            "cross-process factory activation");
        server.CheckAlive();
        long count = -1;
        Check(factory->GetPluginCount(&count) == S_OK && count == (enabled ? 1 : 0),
            "cross-process factory retains loading gate");
        ComPtr<IWTSPlugin> created;
        const auto createResult = factory->CreatePlugin(0, &created);
        Check(enabled ? createResult == S_OK && created : FAILED(createResult) && !created,
            "cross-process plugin creation follows factory gate");
        if (i == 0) {
            const auto firstInstance = ProbeSurface(created.Get());
            ComPtr<IWTSPlugin> other;
            CheckHr(factory->CreatePlugin(0, &other), "second independent plugin in the same COM server");
            ComPtr<SurfaceManager> manager;
            manager.Attach(new SurfaceManager);
            manager->serviceAvailable = false;
            auto pipe = SurfacePipe();
            CheckHr(other->Initialize(manager.Get()), "unavailable optional public service does not fail initialization");
            CheckHr(other->Connected(), "unavailable service does not fail connection lifecycle");
            SurfaceConnect(pipe.get());
            const auto unavailable = ReadSurface(pipe.get(), ral::SurfaceStatus::ServiceUnavailable);
            Check(unavailable.instance != firstInstance && unavailable.hwnd == 0 && unavailable.flags == 0,
                "same-process plugin instances retain distinct identities when optional services are unavailable");
            CheckHr(other->Terminated(), "terminate service-unavailable worker");
        }
        ComPtr<IWTSPlugin> direct;
        const auto directResult = CoCreateInstance(server.classes[i], nullptr, CLSCTX_LOCAL_SERVER,
            IID_PPV_ARGS(&direct));
        if (i == 2) {
            CheckHr(directResult, "cross-process direct legacy IWTSPlugin activation");
            Check(direct && direct->Connected() == S_OK, "cross-process directly activated legacy plugin works");
            ComPtr<IWTSPluginFactory> queried;
            CheckHr(direct.As(&queried), "cross-process legacy plugin exposes its factory interface");
            ComPtr<IUnknown> directIdentity, factoryIdentity;
            CheckHr(direct.As(&directIdentity), "cross-process direct plugin controlling identity");
            CheckHr(queried.As(&factoryIdentity), "cross-process legacy factory controlling identity");
            Check(directIdentity.Get() == factoryIdentity.Get(), "cross-process legacy interface identity is symmetric");
            Check(direct->Terminated() == S_OK && created->Connected() == S_OK,
                "cross-process direct and factory-created plugins remain independent");
        } else {
            Check(FAILED(directResult) && !direct, "cross-process disabled or modern direct activation fails");
        }
    }
    server.Finish();
    return checks;
}

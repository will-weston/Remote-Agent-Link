#include "RdpPlugin.h"
#include "ServerLifetime.h"
#include "WinHandle.h"
#include <wrl/client.h>
#include <array>
#include <cstdio>
#include <string_view>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR commandLine, int) {
    const bool checkStartup = std::wstring_view(commandLine ? commandLine : L"") == L"--check-startup";
    const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) return static_cast<int>(initialized);
    ral::WinHandle shutdown(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    HRESULT result = shutdown ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    std::array<DWORD, 2> registrations{};
    std::array<Microsoft::WRL::ComPtr<IClassFactory>, 2> factories;
    if (SUCCEEDED(result)) {
        ral::ServerShutdownEvent = shutdown.get();
        // Fast rundown must be configured before process-wide COM security.
        Microsoft::WRL::ComPtr<IGlobalOptions> options;
        result = CoCreateInstance(CLSID_GlobalOptions, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&options));
        if (SUCCEEDED(result)) result = options->Set(COMGLB_RO_SETTINGS, COMGLB_FAST_RUNDOWN);
    }
    if (SUCCEEDED(result)) {
        result = CoInitializeSecurity(nullptr, -1, nullptr, nullptr, RPC_C_AUTHN_LEVEL_PKT_PRIVACY,
            RPC_C_IMP_LEVEL_IDENTIFY, nullptr, EOAC_DISABLE_AAA, nullptr);
    }
    if (SUCCEEDED(result) && !checkStartup) {
        constexpr std::array classes{ral::PluginFactoryClsid, ral::LegacyPluginClsid};
        for (size_t i = 0; i < classes.size() && SUCCEEDED(result); ++i) {
            result = ral::CreatePluginClassFactory(classes[i], &factories[i]);
            if (SUCCEEDED(result))
                result = CoRegisterClassObject(classes[i], factories[i].Get(), CLSCTX_LOCAL_SERVER,
                    REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED, &registrations[i]);
        }
    }
    if (SUCCEEDED(result) && !checkStartup) result = CoResumeClassObjects();
    if (SUCCEEDED(result) && !checkStartup) {
        // Avoid an abandoned activation leaving an unused process running forever.
        for (;;) {
            const auto waited = WaitForSingleObject(shutdown.get(), 60000);
            if (waited == WAIT_OBJECT_0) break;
            if (waited != WAIT_TIMEOUT) { result = HRESULT_FROM_WIN32(GetLastError()); break; }
            result = CoSuspendClassObjects();
            if (FAILED(result)) break;
            CoAddRefServerProcess();
            if (CoReleaseServerProcess() == 0) break;
            result = CoResumeClassObjects();
            if (FAILED(result)) break;
        }
    }
    CoSuspendClassObjects();
    for (size_t i = registrations.size(); i-- > 0;) {
        if (registrations[i]) {
            const auto revoked = CoRevokeClassObject(registrations[i]);
            if (SUCCEEDED(result) && FAILED(revoked)) result = revoked;
        }
        factories[i].Reset();
    }
    ral::ServerShutdownEvent = nullptr;
    if (FAILED(result)) {
        std::fwprintf(stderr, L"AgentLink.RdpPlugin: COM startup failed (0x%08lX).\n", static_cast<unsigned long>(result));
        OutputDebugStringW(L"AgentLink.RdpPlugin: COM startup failed.\n");
    } else if (checkStartup) {
        std::puts("COM startup initialized; no plugin classes registered or RDP channels opened.");
    }
    CoUninitialize();
    return FAILED(result) ? static_cast<int>(result) : 0;
}

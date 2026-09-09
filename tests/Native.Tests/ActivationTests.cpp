#include "RdpPlugin.h"
#include "ServerLifetime.h"
#include <wrl/client.h>
#include <sddl.h>
#include <array>
#include <cstring>
#include <memory>
#include <new>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;
using Mode = ral::PluginLoadingMode;

namespace {
unsigned checks{};
void Check(bool value, const char* message) {
    ++checks;
    if (!value) throw std::runtime_error(message);
}

class TestSettings {
public:
    TestSettings() {
        GUID id{};
        Check(SUCCEEDED(CoCreateGuid(&id)), "isolated settings key identity");
        std::array<wchar_t, 40> text{};
        Check(StringFromGUID2(id, text.data(), static_cast<int>(text.size())) != 0, "settings key GUID");
        path_ = L"Software\\RemoteAgentLink.NativeTests." + std::wstring(text.data());
        DWORD disposition{};
        Check(RegCreateKeyExW(HKEY_CURRENT_USER, path_.c_str(), 0, nullptr, REG_OPTION_VOLATILE,
            KEY_ALL_ACCESS | KEY_WOW64_64KEY, nullptr, &root_, &disposition) == ERROR_SUCCESS,
            "create unique volatile test-owned settings root");
        Check(disposition == REG_CREATED_NEW_KEY, "never reuse an existing settings test key");
    }
    ~TestSettings() {
        if (settings_) RegCloseKey(settings_);
        if (root_) {
            RegDeleteTreeW(root_, nullptr);
            RegCloseKey(root_);
            RegDeleteKeyExW(HKEY_CURRENT_USER, path_.c_str(), KEY_WOW64_64KEY, 0);
        }
    }
    TestSettings(const TestSettings&) = delete;
    TestSettings& operator=(const TestSettings&) = delete;
    void Create() {
        Check(RegCreateKeyExW(root_, L"Settings", 0, nullptr, REG_OPTION_VOLATILE,
            KEY_ALL_ACCESS | KEY_WOW64_64KEY, nullptr, &settings_, nullptr) == ERROR_SUCCESS,
            "create isolated settings key");
    }
    void Write(DWORD type, const void* bytes, DWORD length) {
        Check(RegSetValueExW(settings_, ral::PluginLoadingModeValue, 0, type,
            static_cast<const BYTE*>(bytes), length) == ERROR_SUCCESS, "write test-owned loading mode");
    }
    void Write(Mode mode) {
        const auto value = static_cast<DWORD>(mode);
        Write(REG_DWORD, &value, sizeof(value));
    }
    HRESULT Read(Mode* mode) const noexcept {
        return ral::ReadPluginLoadingMode(root_, L"Settings", mode);
    }
    HKEY Key() const noexcept { return settings_; }
    void Cleanup() {
        if (settings_) {
            RegCloseKey(settings_);
            settings_ = nullptr;
        }
        Check(RegDeleteTreeW(root_, nullptr) == ERROR_SUCCESS, "remove owned test registry contents");
        RegCloseKey(root_);
        root_ = nullptr;
        Check(RegDeleteKeyExW(HKEY_CURRENT_USER, path_.c_str(), KEY_WOW64_64KEY, 0) == ERROR_SUCCESS,
            "remove unique test registry root");
        HKEY absent{};
        const auto result = RegOpenKeyExW(HKEY_CURRENT_USER, path_.c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &absent);
        if (absent) RegCloseKey(absent);
        Check(result == ERROR_FILE_NOT_FOUND, "owned registry key cleanup verified");
    }
private:
    HKEY root_{}, settings_{};
    std::wstring path_;
};

class RestoreKeySecurity {
public:
    explicit RestoreKeySecurity(HKEY key) : key_(key) {
        DWORD length{};
        Check(RegGetKeySecurity(key_, DACL_SECURITY_INFORMATION, nullptr, &length) == ERROR_INSUFFICIENT_BUFFER,
            "read test-owned key security size");
        data_ = std::make_unique<BYTE[]>(length);
        Check(RegGetKeySecurity(key_, DACL_SECURITY_INFORMATION, data_.get(), &length) == ERROR_SUCCESS,
            "save test-owned key security");
    }
    ~RestoreKeySecurity() { RegSetKeySecurity(key_, DACL_SECURITY_INFORMATION, data_.get()); }
private:
    HKEY key_;
    std::unique_ptr<BYTE[]> data_;
};

void SettingTests() {
    TestSettings settings;
    Mode mode = Mode::Legacy;
    Check(settings.Read(&mode) == S_OK && mode == Mode::Modern, "missing settings key defaults Modern");
    settings.Create();
    mode = Mode::Legacy;
    Check(settings.Read(&mode) == S_OK && mode == Mode::Modern, "missing settings value defaults Modern");
    Check(settings.Read(nullptr) == E_POINTER, "settings reader requires output");
    Check(ral::ReadPluginLoadingMode(nullptr, L"Settings", &mode) == E_INVALIDARG &&
        !ral::IsValidLoadingMode(mode), "invalid settings root fails closed");
    Check(ral::ReadPluginLoadingMode(HKEY_CURRENT_USER, nullptr, &mode) == E_INVALIDARG &&
        !ral::IsValidLoadingMode(mode), "invalid settings path fails closed");
    for (const auto selected : {Mode::Modern, Mode::Legacy}) {
        settings.Write(selected);
        Check(settings.Read(&mode) == S_OK && mode == selected, "explicit 64-bit DWORD loading mode");
    }
    for (const DWORD value : {2u, MAXDWORD}) {
        settings.Write(REG_DWORD, &value, sizeof(value));
        Check(FAILED(settings.Read(&mode)) && !ral::IsValidLoadingMode(mode), "invalid DWORD fails closed");
    }
    const DWORD value = 1;
    for (const DWORD type : {REG_SZ, REG_BINARY, REG_DWORD_BIG_ENDIAN, REG_NONE}) {
        settings.Write(type, &value, sizeof(value));
        Check(FAILED(settings.Read(&mode)) && !ral::IsValidLoadingMode(mode), "wrong registry type fails closed");
    }
    for (const DWORD size : {0u, 1u, 3u}) {
        settings.Write(REG_DWORD, &value, size);
        Check(FAILED(settings.Read(&mode)) && !ral::IsValidLoadingMode(mode), "short DWORD fails closed");
    }
    const ULONGLONG oversized = 1;
    settings.Write(REG_DWORD, &oversized, sizeof(oversized));
    Check(FAILED(settings.Read(&mode)) && !ral::IsValidLoadingMode(mode), "oversized DWORD fails closed");
    settings.Write(REG_QWORD, &oversized, sizeof(oversized));
    Check(FAILED(settings.Read(&mode)) && !ral::IsValidLoadingMode(mode), "QWORD is not a loading DWORD");
    settings.Write(Mode::Legacy);
    {
        RestoreKeySecurity restore(settings.Key());
        PSECURITY_DESCRIPTOR raw{};
        Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(D;;0x1;;;WD)(A;;KA;;;OW)",
            SDDL_REVISION_1, &raw, nullptr) != FALSE, "build isolated deny-query ACL");
        std::unique_ptr<void, decltype(&LocalFree)> descriptor(raw, LocalFree);
        Check(RegSetKeySecurity(settings.Key(), DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            raw) == ERROR_SUCCESS, "deny query only on test-owned key");
        Check(settings.Read(&mode) == E_ACCESSDENIED && !ral::IsValidLoadingMode(mode),
            "inaccessible settings fail explicitly instead of defaulting");
    }
    Check(settings.Read(&mode) == S_OK && mode == Mode::Legacy, "isolated key ACL restored");
    Check(RegDeleteValueW(settings.Key(), ral::PluginLoadingModeValue) == ERROR_SUCCESS, "delete test-owned setting");
    Check(settings.Read(&mode) == S_OK && mode == Mode::Modern, "deleted value defaults Modern");
    settings.Cleanup();
}

struct ModeState {
    Mode selected = Mode::Modern;
    HRESULT result = S_OK;
    unsigned reads{};
};
HRESULT ReadMode(void* context, Mode* mode) {
    auto& state = *static_cast<ModeState*>(context);
    ++state.reads;
    *mode = state.selected;
    return state.result;
}
HRESULT ReadRegistryMode(void* context, Mode* mode) {
    return static_cast<TestSettings*>(context)->Read(mode);
}
HRESULT ThrowModeReader(void*, Mode*) { throw std::runtime_error("injected settings failure"); }

class ProbeListener final : public ral::ComObject<IWTSListener> {
public:
    HRESULT STDMETHODCALLTYPE GetConfiguration(IPropertyBag** value) noexcept override {
        if (!value) return E_POINTER;
        *value = nullptr;
        return E_NOTIMPL;
    }
};
class ProbeManager final : public ral::ComObject<IWTSVirtualChannelManager> {
public:
    unsigned listeners{};
    HRESULT STDMETHODCALLTYPE CreateListener(const unsigned char* name, ULONG flags,
        IWTSListenerCallback* callback, IWTSListener** listener) noexcept override {
        if (!listener) return E_POINTER;
        *listener = nullptr;
        if (!name || !callback || flags || std::strcmp(reinterpret_cast<const char*>(name), "RALink.v1"))
            return E_INVALIDARG;
        *listener = new (std::nothrow) ProbeListener;
        if (!*listener) return E_OUTOFMEMORY;
        ++listeners;
        return S_OK;
    }
};

void ClassFactoryTests() {
    ModeState state;
    Check(ral::CreatePluginClassFactory(ral::LegacyPluginClsid, nullptr, ReadMode, &state) == E_POINTER,
        "class constructor requires output");
    auto* invalid = reinterpret_cast<IClassFactory*>(static_cast<ULONG_PTR>(1));
    Check(ral::CreatePluginClassFactory(GUID_NULL, &invalid, ReadMode, &state) == CLASS_E_CLASSNOTAVAILABLE && !invalid,
        "unknown CLSID is never treated as either plugin class");
    const CLSID expectedLegacy{0x77a190c4, 0xc7eb, 0x4db2, {0xa3, 0x17, 0x9c, 0xfa, 0xda, 0x8f, 0x2a, 0x53}};
    Check(ral::LegacyPluginClsid == expectedLegacy, "legacy application CLSID remains stable");
    for (const auto& clsid : {ral::PluginFactoryClsid, ral::LegacyPluginClsid}) {
        for (const auto selected : {Mode::Modern, Mode::Legacy}) {
            state.selected = selected;
            ComPtr<IClassFactory> activation;
            Check(ral::CreatePluginClassFactory(clsid, &activation, ReadMode, &state) == S_OK && activation,
                "construct actual CLSID activation path with injected selection");
            const auto reads = state.reads;
            ComPtr<IUnknown> unknown;
            Check(activation->CreateInstance(nullptr, IID_PPV_ARGS(&unknown)) == S_OK && unknown,
                "both activation classes support IUnknown");
            ComPtr<IWTSPluginFactory> factory;
            Check(unknown.As(&factory) == S_OK && factory && state.reads == reads + 1,
                "each new class activation reads exactly one mode snapshot");
            const bool enabled = (clsid == ral::PluginFactoryClsid) == (selected == Mode::Modern);
            long count = -1;
            Check(factory->GetPluginCount(&count) == S_OK && count == (enabled ? 1 : 0),
                "actual CLSID factory count follows its own gate");
            ComPtr<IWTSPlugin> plugin;
            const auto created = factory->CreatePlugin(0, &plugin);
            if (enabled) {
                Check(created == S_OK && plugin, "enabled CLSID factory creates actual plugin");
                ComPtr<ProbeManager> manager;
                manager.Attach(new ProbeManager);
                Check(plugin->Initialize(manager.Get()) == S_OK && manager->listeners == 1,
                    "both factory classes use the unchanged RALink.v1 listener without opening a channel");
            } else {
                Check(created == HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY) && !plugin,
                    "disabled CLSID factory creation explicitly fails");
            }
            ComPtr<IWTSPlugin> direct;
            const auto result = activation->CreateInstance(nullptr, IID_PPV_ARGS(&direct));
            if (clsid == ral::PluginFactoryClsid) {
                Check(result == E_NOINTERFACE && !direct, "modern CLSID exposes only the factory activation model");
            } else if (selected == Mode::Modern) {
                Check(result == HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY) && !direct,
                    "disabled classic direct activation returns failure, not S_OK/null");
            } else {
                Check(result == S_OK && direct && direct.Get() != plugin.Get(),
                    "legacy CLSID directly activates an independent IWTSPlugin");
                ComPtr<ProbeManager> manager;
                manager.Attach(new ProbeManager);
                Check(direct->Initialize(manager.Get()) == S_OK && manager->listeners == 1,
                    "direct legacy activation reuses the same actual RALink.v1 listener");
            }
        }
    }
}

void FactoryGateTests() {
    Check(ral::PluginFactoryClsid != ral::LegacyPluginClsid, "legacy has a separate application CLSID");
    const CLSID modern{0x583e23e9, 0x3a9f, 0x461c, {0xa3, 0xca, 0x6f, 0x99, 0xf2, 0x62, 0x5b, 0x78}};
    Check(ral::PluginFactoryClsid == modern, "modern factory CLSID remains stable");
    for (const auto path : {Mode::Modern, Mode::Legacy}) {
        for (const auto selected : {Mode::Modern, Mode::Legacy}) {
            ComPtr<IWTSPluginFactory> factory;
            Check(ral::CreatePluginFactory(path, selected, &factory) == S_OK && factory, "construct explicit gate");
            long count = -1;
            Check(factory->GetPluginCount(nullptr) == E_POINTER, "gated factory count requires output");
            Check(factory->GetPluginCount(&count) == S_OK && count == (path == selected ? 1 : 0),
                "only selected loader advertises its plugin (including modern count phase)");
            Check(factory->CreatePlugin(0, nullptr) == E_POINTER, "gated factory creation requires output");
            for (const long index : {-1L, 1L, LONG_MAX}) {
                auto* raw = reinterpret_cast<IWTSPlugin*>(static_cast<ULONG_PTR>(1));
                Check(factory->CreatePlugin(index, &raw) == E_INVALIDARG && !raw, "invalid factory index clears output");
            }
            ComPtr<IWTSPlugin> first, second;
            if (path == selected) {
                Check(factory->CreatePlugin(0, &first) == S_OK && first, "enabled factory creates plugin");
                Check(factory->CreatePlugin(0, &second) == S_OK && second && first.Get() != second.Get(),
                    "each selected factory call creates an independent plugin");
                Check(first->Terminated() == S_OK && first->Connected() == E_UNEXPECTED &&
                    second->Connected() == S_OK, "plugins have independent lifetime state");
            } else {
                auto* raw = reinterpret_cast<IWTSPlugin*>(static_cast<ULONG_PTR>(1));
                Check(factory->CreatePlugin(0, &raw) == HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY) && !raw,
                    "disabled creation returns explicit failure and null");
            }
        }
    }
    ComPtr<IWTSPluginFactory> factory;
    Check(ral::CreatePluginFactory(static_cast<Mode>(2), &factory) == E_INVALIDARG && !factory,
        "invalid explicit selection never constructs a factory");
    Check(ral::CreatePluginFactory(static_cast<Mode>(2), Mode::Modern, &factory) == E_INVALIDARG && !factory,
        "invalid activation path never constructs a factory");
    Check(ral::CreatePluginFactory(Mode::Modern, nullptr) == E_POINTER, "factory constructor null output");
    ComPtr<IWTSPlugin> plugin;
    Check(ral::CreatePluginActivation(Mode::Modern, Mode::Modern, IID_PPV_ARGS(&plugin)) == E_NOINTERFACE && !plugin,
        "modern class stays a factory, not a direct plugin");
}

void SnapshotTests() {
    ModeState state;
    ComPtr<IClassFactory> modern, legacy;
    Check(ral::CreatePluginClassFactory(ral::PluginFactoryClsid, &modern, ReadMode, &state) == S_OK &&
        ral::CreatePluginClassFactory(ral::LegacyPluginClsid, &legacy, ReadMode, &state) == S_OK,
        "construct both unregistered COM activation classes");
    Check(state.reads == 0, "server registration does not capture a process-wide mode");
    Check(modern->CreateInstance(nullptr, __uuidof(IWTSPluginFactory), nullptr) == E_POINTER,
        "class activation null output");
    void* raw = reinterpret_cast<void*>(static_cast<ULONG_PTR>(1));
    Check(modern->CreateInstance(modern.Get(), IID_IUnknown, &raw) == CLASS_E_NOAGGREGATION && !raw,
        "class activation rejects aggregation and clears output");
    Check(modern->CreateInstance(nullptr, __uuidof(IWTSListener), &raw) == E_NOINTERFACE && !raw,
        "class activation rejects unrelated interface");
    ComPtr<IWTSPluginFactory> modernBefore, legacyBefore;
    Check(modern->CreateInstance(nullptr, IID_PPV_ARGS(&modernBefore)) == S_OK &&
        legacy->CreateInstance(nullptr, IID_PPV_ARGS(&legacyBefore)) == S_OK, "activate Modern snapshots");
    ComPtr<IWTSPlugin> modernPlugin, disabled;
    Check(modernBefore->CreatePlugin(0, &modernPlugin) == S_OK, "modern snapshot creates before switch");
    Check(legacy->CreateInstance(nullptr, IID_PPV_ARGS(&disabled)) ==
        HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY) && !disabled, "direct Legacy is disabled in Modern");
    state.selected = Mode::Legacy;
    ComPtr<IWTSPluginFactory> modernAfter, legacyAfter;
    Check(modern->CreateInstance(nullptr, IID_PPV_ARGS(&modernAfter)) == S_OK &&
        legacy->CreateInstance(nullptr, IID_PPV_ARGS(&legacyAfter)) == S_OK, "activate Legacy snapshots");
    long count{};
    const auto reads = state.reads;
    Check(modernBefore->GetPluginCount(&count) == S_OK && count == 1 &&
        legacyBefore->GetPluginCount(&count) == S_OK && count == 0, "old snapshots retain Modern gate");
    Check(modernAfter->GetPluginCount(&count) == S_OK && count == 0 &&
        legacyAfter->GetPluginCount(&count) == S_OK && count == 1, "new snapshots use Legacy gate");
    Check(modernPlugin->Connected() == S_OK, "existing plugin unaffected by switch");
    ComPtr<IWTSPlugin> anotherModern;
    Check(modernBefore->CreatePlugin(0, &anotherModern) == S_OK, "old enabled snapshot can still create");
    Check(state.reads == reads, "count and creation never reread settings");
    ComPtr<IWTSPlugin> first, second;
    Check(legacy->CreateInstance(nullptr, IID_PPV_ARGS(&first)) == S_OK &&
        legacy->CreateInstance(nullptr, IID_PPV_ARGS(&second)) == S_OK && first && second && first.Get() != second.Get(),
        "classic direct activation returns independent actual plugins");
    Check(first->Terminated() == S_OK && second->Connected() == S_OK, "direct legacy plugins have independent state");
    state.selected = Mode::Modern;
    Check(second->Connected() == S_OK && legacyAfter->GetPluginCount(&count) == S_OK && count == 1,
        "switching back does not mutate old Legacy objects");
    for (const auto failure : {E_ACCESSDENIED, HRESULT_FROM_WIN32(ERROR_READ_FAULT), S_FALSE}) {
        state.result = failure;
        raw = reinterpret_cast<void*>(static_cast<ULONG_PTR>(1));
        Check(FAILED(modern->CreateInstance(nullptr, __uuidof(IWTSPluginFactory), &raw)) && !raw,
            "settings read failure or incomplete success disables modern activation");
        raw = reinterpret_cast<void*>(static_cast<ULONG_PTR>(1));
        Check(FAILED(legacy->CreateInstance(nullptr, __uuidof(IWTSPlugin), &raw)) && !raw,
            "settings read failure disables direct legacy activation");
    }
    state.result = S_OK;
    state.selected = static_cast<Mode>(2);
    raw = reinterpret_cast<void*>(static_cast<ULONG_PTR>(1));
    Check(FAILED(legacy->CreateInstance(nullptr, __uuidof(IWTSPluginFactory), &raw)) && !raw,
        "reader cannot smuggle an invalid selection into activation");
    ComPtr<IClassFactory> throwing;
    Check(ral::CreatePluginClassFactory(ral::LegacyPluginClsid, &throwing, ThrowModeReader) == S_OK,
        "construct activation with throwing reader");
    raw = reinterpret_cast<void*>(static_cast<ULONG_PTR>(1));
    Check(throwing->CreateInstance(nullptr, __uuidof(IWTSPluginFactory), &raw) == E_FAIL && !raw,
        "no settings exception crosses COM activation ABI");

    const auto leases = ral::ServerLeaseCount.load();
    Check(modern->LockServer(FALSE) == E_UNEXPECTED && ral::ServerLeaseCount.load() == leases,
        "unbalanced unlock does not decrement process references");
    Check(modern->LockServer(TRUE) == S_OK && legacy->LockServer(TRUE) == S_OK &&
        ral::ServerLeaseCount.load() == leases + 2, "independent class locks retain the COM process");
    Check(modern->LockServer(FALSE) == S_OK && ral::ServerLeaseCount.load() == leases + 1,
        "balanced class unlock");
    legacy.Reset();
    Check(ral::ServerLeaseCount.load() == leases, "class cleanup balances outstanding locks");

    TestSettings settings;
    settings.Create();
    ComPtr<IClassFactory> fromRegistry;
    Check(ral::CreatePluginClassFactory(ral::LegacyPluginClsid, &fromRegistry, ReadRegistryMode, &settings) == S_OK,
        "registry reader is injectable without production class registration");
    const DWORD malformed = 2;
    settings.Write(REG_DWORD, &malformed, sizeof(malformed));
    Check(FAILED(fromRegistry->CreateInstance(nullptr, IID_PPV_ARGS(&disabled))) && !disabled,
        "malformed registry setting blocks real class activation");
    settings.Write(Mode::Legacy);
    Check(fromRegistry->CreateInstance(nullptr, IID_PPV_ARGS(&disabled)) == S_OK && disabled,
        "corrected isolated setting applies only to new activation");
    fromRegistry.Reset();
    settings.Cleanup();
}

}

unsigned ActivationTests() {
    checks = 0;
    SettingTests();
    ClassFactoryTests();
    FactoryGateTests();
    SnapshotTests();
    return checks;
}

#include "RdpPlugin.h"
#include "ServerLifetime.h"
#include <new>

namespace ral {
namespace {
class ActivationFactory final : public ComObject<IClassFactory, false> {
public:
    ActivationFactory(PluginLoadingMode mode, PluginLoadingModeReader reader, void* context,
        ClientConnectionPipeResolver resolver) noexcept :
        mode_(mode), reader_(reader), context_(context), resolver_(resolver) {}

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** object) noexcept override {
        ServerLease call;
        if (!object) return E_POINTER;
        *object = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try {
            PluginLoadingMode selected = static_cast<PluginLoadingMode>(MAXDWORD);
            const auto result = reader_ ? reader_(context_, &selected) : ReadPluginLoadingMode(&selected);
            if (FAILED(result)) return result;
            if (result != S_OK || !IsValidLoadingMode(selected))
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            return CreatePluginActivation(mode_, selected, iid, object, resolver_);
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) noexcept override {
        if (lock) {
            CoAddRefServerProcess();
            ++ServerLeaseCount;
            ++locks_;
        } else {
            auto remaining = locks_.load();
            do {
                if (!remaining) return E_UNEXPECTED;
            } while (!locks_.compare_exchange_weak(remaining, remaining - 1));
            ReleaseLock();
        }
        return S_OK;
    }
private:
    ~ActivationFactory() override {
        auto remaining = locks_.exchange(0);
        while (remaining--) ReleaseLock();
    }
    static void ReleaseLock() noexcept {
        --ServerLeaseCount;
        if (CoReleaseServerProcess() == 0 && ServerShutdownEvent) SetEvent(ServerShutdownEvent);
    }
    const PluginLoadingMode mode_;
    const PluginLoadingModeReader reader_;
    void* const context_;
    const ClientConnectionPipeResolver resolver_;
    std::atomic<ULONG> locks_{};
};
}

HRESULT CreatePluginClassFactory(REFCLSID clsid, IClassFactory** factory,
    PluginLoadingModeReader reader, void* context, ClientConnectionPipeResolver resolver) noexcept {
    if (!factory) return E_POINTER;
    *factory = nullptr;
    if (clsid != PluginFactoryClsid && clsid != LegacyPluginClsid) return CLASS_E_CLASSNOTAVAILABLE;
    const auto activationMode = clsid == PluginFactoryClsid ? PluginLoadingMode::Modern : PluginLoadingMode::Legacy;
    if (!resolver) return E_INVALIDARG;
    *factory = new (std::nothrow) ActivationFactory(activationMode, reader, context, resolver);
    return *factory ? S_OK : E_OUTOFMEMORY;
}
}

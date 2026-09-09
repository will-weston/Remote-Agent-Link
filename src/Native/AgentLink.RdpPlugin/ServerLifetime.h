#pragma once
#include <windows.h>
#include <objbase.h>
#include <atomic>

namespace ral {
inline HANDLE ServerShutdownEvent{};
inline std::atomic<unsigned long> ServerLeaseCount{};

class ServerLease {
public:
    ServerLease() noexcept {
        CoAddRefServerProcess();
        ++ServerLeaseCount;
    }
    ~ServerLease() {
        --ServerLeaseCount;
        // CoReleaseServerProcess suspends class activation atomically at zero.
        if (CoReleaseServerProcess() == 0 && ServerShutdownEvent)
            SetEvent(ServerShutdownEvent);
    }
    ServerLease(const ServerLease&) = delete;
    ServerLease& operator=(const ServerLease&) = delete;
};

template<class Interface, bool CountServer = true>
class ComObject : public Interface {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) noexcept override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(Interface)) return E_NOINTERFACE;
        *value = static_cast<Interface*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() noexcept override {
        const auto remaining = --references_;
        if (!remaining) delete this;
        return remaining;
    }
protected:
    ComObject() noexcept {
        if constexpr (CountServer) {
            CoAddRefServerProcess();
            ++ServerLeaseCount;
        }
    }
    virtual ~ComObject() {
        if constexpr (CountServer) {
            --ServerLeaseCount;
            if (CoReleaseServerProcess() == 0 && ServerShutdownEvent)
                SetEvent(ServerShutdownEvent);
        }
    }
private:
    std::atomic<ULONG> references_{1};
};
}

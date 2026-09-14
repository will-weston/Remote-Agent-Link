#pragma once
#include <windows.h>
#include <objbase.h>
#include <atomic>

namespace ral {
inline HANDLE ServerShutdownEvent{};
inline std::atomic<unsigned long> ServerLeaseCount{};

inline HRESULT WaitForServerShutdown(HANDLE shutdown, DWORD idleTimeoutMs = 60000) noexcept {
    for (;;) {
        const auto waited = WaitForSingleObject(shutdown, idleTimeoutMs);
        if (waited == WAIT_OBJECT_0) return S_OK;
        if (waited != WAIT_TIMEOUT) return HRESULT_FROM_WIN32(GetLastError());
        // CoReleaseServerProcess atomically suspends activation only at zero.
        // Do not suspend/resume busy classes: resuming an already-published class
        // can fail and must not tear down objects that still have client references.
        CoAddRefServerProcess();
        if (CoReleaseServerProcess() == 0) return S_OK;
    }
}

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

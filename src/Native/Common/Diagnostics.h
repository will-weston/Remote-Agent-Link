#pragma once
#include <windows.h>
#include <array>
#include <atomic>
#include <cstdint>

namespace ral {
static_assert(std::atomic<uint64_t>::is_always_lock_free);
enum class DiagnosticKind : uint32_t {
    ServerStart = 0, ServerStop, Activation, PluginInitialize, PluginConnected,
    PluginDisconnected, PluginTerminated, ListenerUnavailable, BridgeOpen,
    BridgeClose, BridgeFailure, ReporterFailure, SurfaceStatus, WtsOpen, WtsClose,
    WtsFailure, Count
};

// Fixed storage and a single nonblocking lock attempt; logging never waits for
// another callback. Separate budgets keep noisy retries from starving lifecycle.
class DiagnosticRateGate {
public:
    static constexpr uint32_t Burst = 8;
    static constexpr uint64_t WindowMs = 60000;
    bool TryAcquire(uint64_t now, uint64_t& suppressed) noexcept {
        suppressed = 0;
        if (busy_.test_and_set(std::memory_order_acquire)) {
            dropped_.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
        if (!started_ || now - since_ >= WindowMs) {
            started_ = true;
            since_ = now;
            admitted_ = 0;
        }
        const bool allowed = admitted_ < Burst;
        if (allowed) {
            ++admitted_;
            suppressed = dropped_.exchange(0, std::memory_order_relaxed);
        } else {
            dropped_.fetch_add(1, std::memory_order_relaxed);
        }
        busy_.clear(std::memory_order_release);
        return allowed;
    }
private:
    std::atomic_flag busy_ = ATOMIC_FLAG_INIT;
    std::atomic<uint64_t> dropped_{};
    uint64_t since_{};
    uint32_t admitted_{};
    bool started_{};
};

void InitializeDiagnostics() noexcept;
GUID NewDiagnosticCorrelation() noexcept;
void WriteDiagnostic(DiagnosticKind kind, HRESULT result = S_OK,
    uint32_t state = 0, const GUID& correlation = GUID{}) noexcept;
}

#include "Diagnostics.h"
#include <objbase.h>
#include <TraceLoggingProvider.h>

namespace {
TRACELOGGING_DEFINE_PROVIDER(Provider, "RemoteAgentLink.Native",
    (0x945b8a72, 0x5a20, 0x4b8b, 0xb3, 0xa3, 0xb7, 0x7d, 0x82, 0x86, 0x0d, 0x15));

struct PreserveLastError {
    DWORD value{GetLastError()};
    ~PreserveLastError() { SetLastError(value); }
};
struct Registration {
    Registration() noexcept { TraceLoggingRegister(Provider); }
    ~Registration() { TraceLoggingUnregister(Provider); }
};
std::array<ral::DiagnosticRateGate, static_cast<size_t>(ral::DiagnosticKind::Count)> Gates;
}

namespace ral {
void InitializeDiagnostics() noexcept {
    PreserveLastError error;
    // Explicitly called at entry/activation, never during DLL initialization or
    // on data/window callbacks. Registration failure leaves diagnostics disabled.
    static Registration registration;
}

GUID NewDiagnosticCorrelation() noexcept {
    PreserveLastError error;
    GUID value{};
    if (FAILED(CoCreateGuid(&value))) value = {};
    return value;
}

void WriteDiagnostic(DiagnosticKind kind, HRESULT result, uint32_t state, const GUID& correlation) noexcept {
    PreserveLastError error;
    const auto index = static_cast<size_t>(kind);
    if (index >= Gates.size() || !TraceLoggingProviderEnabled(Provider, 4, 0x1)) return;
    uint64_t suppressed{};
    if (!Gates[index].TryAcquire(GetTickCount64(), suppressed)) return;
    TraceLoggingWrite(Provider, "NativeDiagnostic",
        TraceLoggingLevel(4), TraceLoggingKeyword(0x1),
        TraceLoggingUInt32(static_cast<uint32_t>(kind), "Kind"),
        TraceLoggingHResult(result, "Result"),
        TraceLoggingUInt32(state, "State"),
        TraceLoggingGuid(correlation, "Correlation"),
        TraceLoggingUInt64(suppressed, "Suppressed"));
}
}

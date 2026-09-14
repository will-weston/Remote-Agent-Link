#pragma once
#include "IWTSPlugin_h.h"
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace ral {
// Public Microsoft MIT sample service identity, not the interface IID.
inline constexpr GUID WindowInfoServiceId{
    0xa9a17a6b, 0xcdb3, 0x4e81, {0xb5, 0xc8, 0xbd, 0x63, 0x82, 0x2a, 0x8d, 0x6c}};

enum class SurfaceStatus : uint32_t {
    Pending, ServiceUnavailable, ServiceError, UnsupportedSession, InvalidGeometry, IdentityAmbiguous
};

struct SurfaceReport {
    char magic[8]{'R', 'A', 'L', 'S', 'R', 'F', '1', '\0'};
    GUID instance{};
    GUID epoch{};
    uint64_t generation{};
    uint64_t hwnd{};
    uint64_t processStart{};
    uint32_t processId{};
    uint32_t sessionId{};
    SurfaceStatus status{SurfaceStatus::Pending};
    HRESULT result{E_PENDING};
    int32_t width{}, height{}, viewWidth{}, viewHeight{}, offsetX{}, offsetY{};
    float scale{};
    uint32_t flags{};
    uint64_t reserved[2]{};
};
static_assert(sizeof(SurfaceReport) == 128 && offsetof(SurfaceReport, generation) == 40 &&
    offsetof(SurfaceReport, hwnd) == 48 && offsetof(SurfaceReport, status) == 72 &&
    offsetof(SurfaceReport, width) == 80 && offsetof(SurfaceReport, flags) == 108);
static_assert(sizeof(WTSWindowInfo) == 40 && offsetof(WTSWindowInfo, Height) == 8 &&
    offsetof(WTSWindowInfo, Width) == 12 && offsetof(WTSWindowInfo, Scale) == 32);

inline bool ValidSurfaceGeometry(const WTSWindowInfo& info) noexcept {
    // These are diagnostic bounds, not an approved coordinate transformation.
    return info.Width > 0 && info.Width <= 65536 && info.Height > 0 && info.Height <= 65536 &&
        info.ViewWidth > 0 && info.ViewWidth <= 65536 && info.ViewHeight > 0 && info.ViewHeight <= 65536 &&
        info.ViewOffsetX >= 0 && info.ViewOffsetY >= 0 && info.ViewOffsetX <= 65536 &&
        info.ViewOffsetY <= 65536 && std::isfinite(info.Scale) && info.Scale > 0 && info.Scale <= 32;
}
}

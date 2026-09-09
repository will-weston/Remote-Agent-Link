#pragma once
#include <windows.h>

namespace ral {
// Application-owned identities; never reuse the sample application's CLSIDs.
inline constexpr CLSID PluginFactoryClsid{
    0x583e23e9, 0x3a9f, 0x461c, {0xa3, 0xca, 0x6f, 0x99, 0xf2, 0x62, 0x5b, 0x78}};
inline constexpr CLSID LegacyPluginClsid{
    0x77a190c4, 0xc7eb, 0x4db2, {0xa3, 0x17, 0x9c, 0xfa, 0xda, 0x8f, 0x2a, 0x53}};

enum class PluginLoadingMode : DWORD { Modern = 0, Legacy = 1 };
inline constexpr wchar_t PluginSettingsPath[] = L"Software\\RemoteAgentLink\\PluginSettings";
inline constexpr wchar_t PluginLoadingModeValue[] = L"LoadingMode";

constexpr bool IsValidLoadingMode(PluginLoadingMode mode) noexcept {
    return mode == PluginLoadingMode::Modern || mode == PluginLoadingMode::Legacy;
}
}

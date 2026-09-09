#include "RdpPlugin.h"

namespace ral {
HRESULT ReadPluginLoadingMode(HKEY root, const wchar_t* path, PluginLoadingMode* mode) noexcept {
    if (!mode) return E_POINTER;
    *mode = static_cast<PluginLoadingMode>(MAXDWORD);
    if (!root || !path || !*path) return E_INVALIDARG;
    HKEY key{};
    auto error = RegOpenKeyExW(root, path, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key);
    if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) {
        *mode = PluginLoadingMode::Modern;
        return S_OK;
    }
    if (error != ERROR_SUCCESS) return HRESULT_FROM_WIN32(error);
    DWORD type{}, value{}, size = sizeof(value);
    error = RegQueryValueExW(key, PluginLoadingModeValue, nullptr, &type,
        reinterpret_cast<BYTE*>(&value), &size);
    RegCloseKey(key);
    if (error == ERROR_FILE_NOT_FOUND) {
        *mode = PluginLoadingMode::Modern;
        return S_OK;
    }
    if (error != ERROR_SUCCESS) return HRESULT_FROM_WIN32(error);
    if (type != REG_DWORD || size != sizeof(value) ||
        !IsValidLoadingMode(static_cast<PluginLoadingMode>(value)))
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    *mode = static_cast<PluginLoadingMode>(value);
    return S_OK;
}

HRESULT ReadPluginLoadingMode(PluginLoadingMode* mode) noexcept {
    return ReadPluginLoadingMode(HKEY_CURRENT_USER, PluginSettingsPath, mode);
}
}

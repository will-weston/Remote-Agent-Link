#pragma once
#include "IWTSPlugin_h.h"
#include "PluginActivation.h"
#include <string>

namespace ral {
inline constexpr char ChannelName[] = "RALink.v1";

HRESULT ReadPluginLoadingMode(HKEY root, const wchar_t* path, PluginLoadingMode* mode) noexcept;
HRESULT ReadPluginLoadingMode(PluginLoadingMode* mode) noexcept;
HRESULT GetClientConnectionPipeName(std::wstring& name) noexcept;
using ClientConnectionPipeResolver = HRESULT (*)(std::wstring& name) noexcept;
// A resolver returning S_FALSE disables reporting for isolated activation fixtures.
HRESULT CreatePluginFactory(IWTSPluginFactory** factory,
    ClientConnectionPipeResolver resolver = GetClientConnectionPipeName) noexcept;
HRESULT CreatePluginFactory(PluginLoadingMode selectedMode, IWTSPluginFactory** factory,
    ClientConnectionPipeResolver resolver = GetClientConnectionPipeName) noexcept;
HRESULT CreatePluginFactory(PluginLoadingMode activationMode, PluginLoadingMode selectedMode,
    IWTSPluginFactory** factory, ClientConnectionPipeResolver resolver = GetClientConnectionPipeName) noexcept;
HRESULT CreatePluginActivation(PluginLoadingMode activationMode, PluginLoadingMode selectedMode,
    REFIID iid, void** object, ClientConnectionPipeResolver resolver = GetClientConnectionPipeName) noexcept;

// The optional reader/context are borrowed for the class factory's lifetime.
using PluginLoadingModeReader = HRESULT (*)(void* context, PluginLoadingMode* mode);
HRESULT CreatePluginClassFactory(REFCLSID clsid, IClassFactory** factory,
    PluginLoadingModeReader reader = nullptr, void* context = nullptr,
    ClientConnectionPipeResolver resolver = GetClientConnectionPipeName) noexcept;
HRESULT GetBrokerPipeName(std::wstring& name) noexcept;
}

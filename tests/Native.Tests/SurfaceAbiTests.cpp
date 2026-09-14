#define CINTERFACE
#include "IWTSPlugin_h.h"
#include <cstddef>

// Exact x64 public-sample ABI, including inherited IUnknown slots.
static_assert(sizeof(IWTSPluginServiceProviderVtbl) == 4 * sizeof(void*));
static_assert(offsetof(IWTSPluginServiceProviderVtbl, GetService) == 3 * sizeof(void*));
static_assert(sizeof(IWTSWindowChangedCallbackVtbl) == 4 * sizeof(void*));
static_assert(offsetof(IWTSWindowChangedCallbackVtbl, WindowChanged) == 3 * sizeof(void*));
static_assert(sizeof(IWTSWindowInfoServiceVtbl) == 8 * sizeof(void*));
static_assert(offsetof(IWTSWindowInfoServiceVtbl, GetWindowInfo) == 3 * sizeof(void*));
static_assert(offsetof(IWTSWindowInfoServiceVtbl, GetRdpClientProcessId) == 4 * sizeof(void*));
static_assert(offsetof(IWTSWindowInfoServiceVtbl, GetRdpSessionType) == 5 * sizeof(void*));
static_assert(offsetof(IWTSWindowInfoServiceVtbl, SubscribeWindowChanged) == 6 * sizeof(void*));
static_assert(offsetof(IWTSWindowInfoServiceVtbl, UnsubscribeWindowChanged) == 7 * sizeof(void*));
static_assert(sizeof(RdpSessionType) == 4 && Desktop == 0 && RemoteApp == 1);
static_assert(sizeof(WTSWindowInfo) == 40 && offsetof(WTSWindowInfo, Hwnd) == 0 &&
    offsetof(WTSWindowInfo, Height) == 8 && offsetof(WTSWindowInfo, Width) == 12 &&
    offsetof(WTSWindowInfo, ViewWidth) == 16 && offsetof(WTSWindowInfo, ViewHeight) == 20 &&
    offsetof(WTSWindowInfo, ViewOffsetX) == 24 && offsetof(WTSWindowInfo, ViewOffsetY) == 28 &&
    offsetof(WTSWindowInfo, Scale) == 32);

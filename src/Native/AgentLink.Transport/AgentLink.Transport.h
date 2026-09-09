#pragma once
#include <stdint.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#define RAL_NOEXCEPT noexcept
#else
#define RAL_NOEXCEPT
#endif

void* __stdcall RAL_Open(const wchar_t* channelName, uint32_t* error) RAL_NOEXCEPT;
uint32_t __stdcall RAL_Read(void* context, uint8_t* buffer, uint32_t capacity,
    uint32_t timeoutMs, uint32_t* bytesRead) RAL_NOEXCEPT;
uint32_t __stdcall RAL_Write(void* context, const uint8_t* buffer, uint32_t length,
    uint32_t* bytesWritten) RAL_NOEXCEPT;
void __stdcall RAL_Close(void* context) RAL_NOEXCEPT;

#ifdef __cplusplus
}
#endif

#include "AgentLink.Transport.h"
#include "PduReassembler.h"
#include "WinHandle.h"
#include "Diagnostics.h"
#include <wtsapi32.h>
#include <array>
#include <atomic>
#include <memory>
#include <mutex>
#include <new>

namespace {
struct Context {
    const GUID correlation{ral::NewDiagnosticCorrelation()};
    bool opened{};
    HANDLE channel{};
    HANDLE file{}; // Borrowed from WTSVirtualChannelQuery, owned by channel.
    ral::WinHandle readEvent{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    OVERLAPPED readOperation{};
    std::array<uint8_t, CHANNEL_PDU_LENGTH> readBuffer{};
    bool readPending{};
    ral::PduReassembler parser;
    std::mutex readLock;
    std::mutex writeLock;
    std::atomic<DWORD> fault{};

    ~Context() {
        if (readPending) {
            CancelIoEx(file, &readOperation);
            DWORD ignored{};
            GetOverlappedResult(file, &readOperation, &ignored, TRUE);
        }
        if (channel) WTSVirtualChannelClose(channel);
        if (opened) ral::WriteDiagnostic(ral::DiagnosticKind::WtsClose, S_OK, 0, correlation);
    }
    DWORD Fail(DWORD error) noexcept {
        DWORD expected{};
        if (fault.compare_exchange_strong(expected, error))
            ral::WriteDiagnostic(ral::DiagnosticKind::WtsFailure, HRESULT_FROM_WIN32(error), 0, correlation);
        return fault.load();
    }
};

void OpenFailed(uint32_t* output, DWORD error) noexcept {
    *output = error;
    ral::WriteDiagnostic(ral::DiagnosticKind::WtsOpen, HRESULT_FROM_WIN32(error));
}

DWORD Read(Context& context, uint8_t* buffer, uint32_t capacity, uint32_t timeout, uint32_t& count) {
    std::lock_guard lock(context.readLock);
    if (const auto error = context.fault.load()) return error;
    if (!capacity) return ERROR_SUCCESS;
    count = static_cast<uint32_t>(context.parser.Read({buffer, capacity}));
    if (count) return ERROR_SUCCESS;
    // The managed owner cancels by stopping bounded polls, then closing/draining.
    const auto deadline = GetTickCount64() + std::min(timeout, 1000u);
    for (unsigned reads = 0; reads < 256; ++reads) {
        if (const auto error = context.fault.load()) return error;
        DWORD transferred{};
        if (!context.readPending) {
            context.readOperation = {};
            context.readOperation.hEvent = context.readEvent.get();
            ResetEvent(context.readEvent.get());
            if (!ReadFile(context.file, context.readBuffer.data(),
                static_cast<DWORD>(context.readBuffer.size()), &transferred, &context.readOperation)) {
                const auto error = GetLastError();
                if (error != ERROR_IO_PENDING) {
                    if (error == ERROR_MORE_DATA) return context.Fail(ERROR_INVALID_DATA);
                    if (error == ERROR_BROKEN_PIPE || error == ERROR_HANDLE_EOF)
                        return context.Fail(context.parser.EndOfStream());
                    return context.Fail(error);
                }
                context.readPending = true;
            }
        }
        if (context.readPending) {
            const auto now = GetTickCount64();
            const auto wait = WaitForSingleObject(context.readEvent.get(),
                now < deadline ? static_cast<DWORD>(deadline - now) : 0);
            if (wait == WAIT_TIMEOUT) return ERROR_SUCCESS;
            if (wait != WAIT_OBJECT_0) return context.Fail(GetLastError());
            context.readPending = false;
            if (!GetOverlappedResult(context.file, &context.readOperation, &transferred, FALSE)) {
                const auto error = GetLastError();
                if (error == ERROR_MORE_DATA) return context.Fail(ERROR_INVALID_DATA);
                if (error == ERROR_BROKEN_PIPE || error == ERROR_HANDLE_EOF)
                    return context.Fail(context.parser.EndOfStream());
                return context.Fail(error);
            }
        }
        if (!transferred) return ERROR_SUCCESS;
        if (transferred > context.readBuffer.size()) return context.Fail(ERROR_INVALID_DATA);
        const auto error = context.parser.Feed({context.readBuffer.data(), transferred});
        if (error) return context.Fail(error);
        count = static_cast<uint32_t>(context.parser.Read({buffer, capacity}));
        if (count || GetTickCount64() >= deadline) return ERROR_SUCCESS;
    }
    return ERROR_SUCCESS;
}

DWORD Write(Context& context, const uint8_t* buffer, uint32_t length, uint32_t& count) {
    std::lock_guard lock(context.writeLock);
    if (const auto error = context.fault.load()) return error;
    ral::WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event) return GetLastError();
    while (count < length) {
        if (const auto error = context.fault.load()) return error;
        OVERLAPPED operation{};
        operation.hEvent = event.get();
        ResetEvent(event.get());
        const auto requested = std::min(length - count, ral::TransportChunkBytes);
        DWORD transferred{};
        DWORD error{};
        if (!WriteFile(context.file, buffer + count, requested, &transferred, &operation)) {
            error = GetLastError();
            if (error == ERROR_IO_PENDING) {
                const auto wait = WaitForSingleObject(event.get(), 10000);
                if (wait != WAIT_OBJECT_0) {
                    error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
                    CancelIoEx(context.file, &operation);
                    GetOverlappedResult(context.file, &operation, &transferred, TRUE);
                } else {
                    error = GetOverlappedResult(context.file, &operation, &transferred, FALSE)
                        ? ERROR_SUCCESS : GetLastError();
                }
            }
        }
        if (transferred > requested) return context.Fail(ERROR_INVALID_DATA);
        count += transferred;
        if (error) return context.Fail(error);
        if (!transferred) return context.Fail(ERROR_WRITE_FAULT);
    }
    return ERROR_SUCCESS;
}
}

extern "C" void* __stdcall RAL_Open(const wchar_t* channelName, uint32_t* error) noexcept {
    ral::InitializeDiagnostics();
    if (!error) return nullptr;
    *error = ERROR_SUCCESS;
    if (!channelName || wcscmp(channelName, L"RALink.v1") != 0) {
        OpenFailed(error, ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    try {
        DWORD session{};
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) {
            OpenFailed(error, GetLastError());
            return nullptr;
        }
        if (!session) {
            OpenFailed(error, ERROR_ACCESS_DENIED);
            return nullptr;
        }
        auto context = std::make_unique<Context>();
        if (!context->readEvent) {
            OpenFailed(error, GetLastError());
            return nullptr;
        }
        char name[] = "RALink.v1";
        context->channel = WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, name, WTS_CHANNEL_OPTION_DYNAMIC);
        if (!context->channel) {
            OpenFailed(error, GetLastError());
            return nullptr;
        }
        void* value{};
        DWORD size{};
        if (!WTSVirtualChannelQuery(context->channel, WTSVirtualFileHandle, &value, &size)) {
            OpenFailed(error, GetLastError());
            return nullptr;
        }
        if (value && size == sizeof(HANDLE)) std::memcpy(&context->file, value, sizeof(HANDLE));
        WTSFreeMemory(value);
        if (!context->file || context->file == INVALID_HANDLE_VALUE) {
            OpenFailed(error, ERROR_INVALID_HANDLE);
            return nullptr;
        }
        context->opened = true;
        ral::WriteDiagnostic(ral::DiagnosticKind::WtsOpen, S_OK, 0, context->correlation);
        return context.release();
    } catch (const std::bad_alloc&) { OpenFailed(error, ERROR_NOT_ENOUGH_MEMORY); }
    catch (...) { OpenFailed(error, ERROR_GEN_FAILURE); }
    return nullptr;
}

extern "C" uint32_t __stdcall RAL_Read(void* context, uint8_t* buffer, uint32_t capacity,
    uint32_t timeoutMs, uint32_t* bytesRead) noexcept {
    if (!bytesRead) return ERROR_INVALID_PARAMETER;
    *bytesRead = 0;
    if (!context || (!buffer && capacity)) return ERROR_INVALID_PARAMETER;
    auto& state = *static_cast<Context*>(context);
    try { return Read(state, buffer, capacity, timeoutMs, *bytesRead); }
    catch (const std::bad_alloc&) { return state.Fail(ERROR_NOT_ENOUGH_MEMORY); }
    catch (...) { return state.Fail(ERROR_GEN_FAILURE); }
}

extern "C" uint32_t __stdcall RAL_Write(void* context, const uint8_t* buffer, uint32_t length,
    uint32_t* bytesWritten) noexcept {
    if (!bytesWritten) return ERROR_INVALID_PARAMETER;
    *bytesWritten = 0;
    if (!context || (!buffer && length)) return ERROR_INVALID_PARAMETER;
    auto& state = *static_cast<Context*>(context);
    try { return Write(state, buffer, length, *bytesWritten); }
    catch (const std::bad_alloc&) { return state.Fail(ERROR_NOT_ENOUGH_MEMORY); }
    catch (...) { return state.Fail(ERROR_GEN_FAILURE); }
}

extern "C" void __stdcall RAL_Close(void* context) noexcept {
    // Caller must stop and drain both its read and write calls before closing.
    delete static_cast<Context*>(context);
}

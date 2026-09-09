#pragma once
#include <windows.h>
#include <utility>

namespace ral {
class WinHandle {
public:
    WinHandle() noexcept = default;
    explicit WinHandle(HANDLE value) noexcept : value_(value) {}
    ~WinHandle() { reset(); }
    WinHandle(const WinHandle&) = delete;
    WinHandle& operator=(const WinHandle&) = delete;
    WinHandle(WinHandle&& other) noexcept : value_(other.release()) {}
    WinHandle& operator=(WinHandle&& other) noexcept {
        if (this != &other) reset(other.release());
        return *this;
    }
    HANDLE get() const noexcept { return value_; }
    explicit operator bool() const noexcept { return value_ && value_ != INVALID_HANDLE_VALUE; }
    HANDLE release() noexcept { return std::exchange(value_, nullptr); }
    void reset(HANDLE value = nullptr) noexcept {
        if (*this) CloseHandle(value_);
        value_ = value;
    }
private:
    HANDLE value_{};
};
}

#pragma once
#include <windows.h>
#include <pchannel.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <span>
#include <vector>

namespace ral {
inline constexpr uint32_t MaxRecordBytes = 16u * 1024 * 1024;
inline constexpr uint32_t TransportChunkBytes = 16u * 1024;

// Feed accepts one complete WTS read, including its CHANNEL_PDU_HEADER.
// A malformed or truncated record permanently faults the stream; never resync TLS.
class PduReassembler {
public:
    DWORD Feed(std::span<const uint8_t> pdu) {
        if (error_) return error_;
        if (Ready()) return ERROR_BUSY;
        if (pdu.size() < sizeof(CHANNEL_PDU_HEADER) || pdu.size() > CHANNEL_PDU_LENGTH)
            return Fail(ERROR_INVALID_DATA);
        CHANNEL_PDU_HEADER header{};
        std::memcpy(&header, pdu.data(), sizeof(header));
        if (header.flags & ~(CHANNEL_FLAG_FIRST | CHANNEL_FLAG_LAST))
            return Fail(ERROR_INVALID_DATA);
        if (header.length > MaxRecordBytes) return Fail(ERROR_FILE_TOO_LARGE);
        const bool first = (header.flags & CHANNEL_FLAG_FIRST) != 0;
        const bool last = (header.flags & CHANNEL_FLAG_LAST) != 0;
        if (first == assembling_) return Fail(ERROR_INVALID_DATA);
        if (first) {
            expected_ = header.length;
            data_.clear();
            data_.reserve(expected_);
            offset_ = 0;
            assembling_ = true;
        } else if (header.length != expected_) {
            return Fail(ERROR_INVALID_DATA);
        }
        const auto payload = pdu.subspan(sizeof(header));
        if (payload.size() > expected_ - data_.size()) return Fail(ERROR_INVALID_DATA);
        if (payload.empty() && !(first && last && expected_ == 0))
            return Fail(ERROR_INVALID_DATA);
        const auto total = data_.size() + payload.size();
        if (last != (total == expected_)) return Fail(ERROR_INVALID_DATA);
        data_.insert(data_.end(), payload.begin(), payload.end());
        if (last) assembling_ = false;
        return ERROR_SUCCESS;
    }

    size_t Read(std::span<uint8_t> output) noexcept {
        if (!Ready()) return 0;
        const auto count = std::min(output.size(), data_.size() - offset_);
        if (count) std::memcpy(output.data(), data_.data() + offset_, count);
        offset_ += count;
        if (offset_ == data_.size()) {
            data_.clear();
            offset_ = 0;
        }
        return count;
    }
    bool Ready() const noexcept { return !assembling_ && offset_ < data_.size() && !error_; }
    bool Incomplete() const noexcept { return assembling_; }
    DWORD EndOfStream() noexcept {
        return error_ ? error_ : (assembling_ ? Fail(ERROR_INVALID_DATA) : ERROR_BROKEN_PIPE);
    }
private:
    DWORD Fail(DWORD error) noexcept {
        error_ = error;
        data_.clear();
        offset_ = 0;
        return error;
    }
    std::vector<uint8_t> data_;
    size_t offset_{};
    uint32_t expected_{};
    bool assembling_{};
    DWORD error_{};
};
}

#pragma once
#include "Engine.h"
#include <array>

namespace qc
{
    // These are identifiers and timing only, never encoded packet copies.
    struct ReplayPacketProof
    {
        int32_t stream = 0, flags = 0;
        int64_t sequence = 0, source = 0, timeUs = 0;
        int64_t pts = 0, dts = 0, duration = 0;
        int32_t numerator = 0, denominator = 0;
        std::array<uint8_t, 10> skipSamples{};
    };

    ReplayPacketProof CaptureReplayPacketProof(const PacketRing::Entry& entry, AVRational base);

    // Written by the recording save from its immutable packet snapshot. A pending
    // proof is not eligible for joining until the published MP4 is validated.
    bool WritePendingReplayProof(const std::wstring& path, int64_t session, int64_t startedUtcUs,
        const std::vector<ReplayPacketProof>& packets, std::string& error);
}

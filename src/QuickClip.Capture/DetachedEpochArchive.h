// Experimental detached ownership for independently encoded graph epochs.
// Not connected to a live engine. Scope strings do NOT constitute capture authorization.
#pragma once
#include "Engine.h"
#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

namespace qc
{
    class DetachedEpochArchive final
    {
    public:
        struct Header
        {
            uint64_t logicalSession = 0;
            uint64_t epochId = 0;
            std::string verifiedScopeLabel; // informational; caller must enforce live HWND/PID/display grants
            uint64_t grantRevision = 0;
            int64_t graphStartUs = 0; // monotonic global time of native graph's local zero
        };

        struct EncodedPacket
        {
            AVPacket* packet = nullptr;
            int stream = -1;
            int64_t relativeUs = 0;
            int64_t globalUs = 0;
            bool key = false;
            int64_t originalSequence = 0;
            int64_t originalSource = 0;
        };

        struct Epoch
        {
            Header header;
            std::vector<AVCodecParameters*> codecs;
            std::vector<AVRational> timeBases;
            std::vector<std::string> streamTitles;
            std::vector<EncodedPacket> packets;
            int64_t startUs = 0, endUs = 0, bytes = 0;

            explicit Epoch(Header h) : header(std::move(h)) {}
            Epoch(const Epoch&) = delete;
            Epoch& operator=(const Epoch&) = delete;
            ~Epoch()
            {
                for (auto& packet : packets) av_packet_free(&packet.packet);
                for (auto*& codec : codecs) avcodec_parameters_free(&codec);
            }
        };

        explicit DetachedEpochArchive(int64_t maxBytes, int64_t retentionUs)
            : maxBytes_(maxBytes), retentionUs_(retentionUs) {}

        // Convenience path for fixture snapshots: clone caller-owned packets.
        bool Append(const Header& header, const std::vector<PacketRing::Entry>& originals,
            const std::vector<AVCodecParameters*>& codecs, const std::vector<AVRational>& bases,
            const std::vector<std::string>& titles = {})
        {
            std::vector<PacketRing::Entry> copies;
            copies.reserve(originals.size());
            for (const auto& entry : originals)
            {
                AVPacket* copy = entry.packet ? av_packet_clone(entry.packet) : nullptr;
                if (!copy)
                {
                    for (auto& pending : copies) av_packet_free(&pending.packet);
                    return false;
                }
                copies.push_back({ copy, entry.stream, entry.timeUs, entry.key, entry.sequence, entry.source });
            }
            const bool appended = AppendOwned(header, copies, codecs, bases, titles);
            for (auto& pending : copies) av_packet_free(&pending.packet);
            return appended;
        }

        // Graph teardown path: adopt existing encoded packets without cloning their buffers.
        // Failure preserves ownership in originals; success nulls transferred pointers.
        // Caller releases any unaccepted entries, including on validation failure.
        bool AppendOwned(const Header& header, std::vector<PacketRing::Entry>& originals,
            const std::vector<AVCodecParameters*>& codecs, const std::vector<AVRational>& bases,
            const std::vector<std::string>& titles = {})
        {
            if (maxBytes_ <= 0 || retentionUs_ <= 0 || header.logicalSession == 0 ||
                header.epochId == 0 || header.verifiedScopeLabel.empty() ||
                header.graphStartUs < 0 || originals.empty() || codecs.empty() ||
                codecs.size() != bases.size() || codecs.size() > 17 ||
                (!titles.empty() && titles.size() != codecs.size()) ||
                std::any_of(bases.begin(), bases.end(), [](AVRational base) {
                    return base.num <= 0 || base.den <= 0;
                })) return false;
            if (!epochs_.empty() && (header.logicalSession != epochs_.front()->header.logicalSession ||
                header.verifiedScopeLabel != epochs_.front()->header.verifiedScopeLabel ||
                header.grantRevision != epochs_.front()->header.grantRevision ||
                header.epochId <= epochs_.back()->header.epochId))
                return false;

            int64_t bytes = 0, startUs = INT64_MAX, endUs = -1;
            std::vector<int64_t> lastDts(codecs.size(), INT64_MIN);
            bool firstVideo = false;
            for (const auto& entry : originals)
            {
                if (!entry.packet || entry.packet->size < 0 ||
                    entry.stream < 0 || static_cast<size_t>(entry.stream) >= codecs.size() ||
                    entry.timeUs < 0 ||
                    entry.timeUs > INT64_MAX - header.graphStartUs ||
                    entry.packet->size > maxBytes_ - bytes) return false;
                // Encoded packets must remain in decode order within each stream.
                const int64_t dts = entry.packet->dts;
                if (dts != AV_NOPTS_VALUE)
                {
                    if (lastDts[entry.stream] != INT64_MIN && dts < lastDts[entry.stream])
                        return false;
                    lastDts[entry.stream] = dts;
                }
                if (entry.stream == 0 && !firstVideo)
                {
                    firstVideo = true;
                    if (!entry.key) return false;
                }
                // Audio and video encoders publish asynchronously; global arrival order
                // need not be timestamp order. Preserve per-stream packet order for the muxer.
                const int64_t globalUs = header.graphStartUs + entry.timeUs;
                startUs = std::min(startUs, globalUs);
                endUs = std::max(endUs, globalUs);
                bytes += entry.packet->size;
            }
            if (!firstVideo || endUs - startUs > retentionUs_) return false;
            if (!epochs_.empty() && startUs <= epochs_.back()->endUs)
                return false; // Overlap requires explicit timeline reconciliation.

            auto epoch = std::make_unique<Epoch>(header);
            for (size_t i = 0; i < codecs.size(); ++i)
            {
                auto* param = codecs[i];
                if (!param || param->codec_id == AV_CODEC_ID_NONE ||
                    (i == 0 ? param->codec_type != AVMEDIA_TYPE_VIDEO :
                        param->codec_type != AVMEDIA_TYPE_AUDIO)) return false;
                AVCodecParameters* copy = avcodec_parameters_alloc();
                if (!copy) return false;
                epoch->codecs.push_back(copy);
                if (avcodec_parameters_copy(copy, param) < 0) return false;
            }
            epoch->timeBases = bases;
            epoch->streamTitles = titles;
            if (epoch->streamTitles.empty())
                for (size_t i = 0; i < codecs.size(); ++i)
                    epoch->streamTitles.push_back(i == 0 ? "Video" : "Audio " + std::to_string(i));
            epoch->packets.reserve(originals.size());
            epoch->startUs = startUs;
            epoch->endUs = endUs;
            epoch->bytes = bytes;

            // Evict before adopting packets: a new epoch must not temporarily double
            // the archive's retained data beyond its own byte/time budget.
            while (!epochs_.empty() && (bytes_ > maxBytes_ - bytes ||
                (endUs - epochs_.front()->endUs > retentionUs_)))
            {
                bytes_ -= epochs_.front()->bytes;
                epochs_.pop_front();
            }
            for (auto& entry : originals)
            {
                AVPacket* packet = entry.packet;
                entry.packet = nullptr;
                epoch->packets.push_back({ packet, entry.stream, entry.timeUs,
                    header.graphStartUs + entry.timeUs, entry.key, entry.sequence, entry.source });
            }
            bytes_ += bytes;
            epochs_.push_back(std::move(epoch));
            return true;
        }

        size_t Count() const { return epochs_.size(); }
        int64_t Bytes() const { return bytes_; }
        const Epoch* At(size_t index) const { return index < epochs_.size() ? epochs_[index].get() : nullptr; }
        void Clear() { epochs_.clear(); bytes_ = 0; }

        // Experimental fixture/export adapter. No live capture path calls this.
        bool WriteSegment(size_t index, const std::wstring& path, std::string& error) const;

    private:
        std::deque<std::unique_ptr<Epoch>> epochs_;
        int64_t maxBytes_ = 0, retentionUs_ = 0, bytes_ = 0;
    };
}

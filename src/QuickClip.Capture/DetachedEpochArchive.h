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

        bool Append(const Header& header, const std::vector<PacketRing::Entry>& originals,
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
            bool firstVideo = false, hasKey = false;
            int64_t previousTime = -1;
            for (const auto& packet : originals)
            {
                if (!packet.packet || packet.packet->size < 0 ||
                    packet.stream < 0 || static_cast<size_t>(packet.stream) >= codecs.size() ||
                    packet.timeUs < 0 || packet.timeUs < previousTime ||
                    packet.timeUs > INT64_MAX - header.graphStartUs) return false;
                if (packet.stream == 0 && !firstVideo)
                {
                    firstVideo = true;
                    hasKey = packet.key;
                    if (!hasKey) return false;
                }
                previousTime = packet.timeUs;
                auto* clone = av_packet_clone(packet.packet);
                if (!clone) return false;
                epoch->packets.push_back({ clone, packet.stream, packet.timeUs,
                    header.graphStartUs + packet.timeUs, packet.key, packet.sequence, packet.source });
                epoch->bytes += clone->size;
                if (epoch->bytes > maxBytes_) return false;
            }
            if (!firstVideo || !hasKey) return false;
            epoch->startUs = epoch->packets.front().globalUs;
            epoch->endUs = epoch->packets.back().globalUs;
            if (epoch->endUs - epoch->startUs > retentionUs_) return false;
            if (!epochs_.empty() && epoch->startUs <= epochs_.back()->endUs)
                return false; // Explicit gaps are allowed. Overlaps require separate provenance validation.
            bytes_ += epoch->bytes;
            epochs_.push_back(std::move(epoch));
            while (!epochs_.empty() && (bytes_ > maxBytes_ ||
                (epochs_.back()->endUs - epochs_.front()->endUs > retentionUs_)))
            {
                bytes_ -= epochs_.front()->bytes;
                epochs_.pop_front();
            }
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

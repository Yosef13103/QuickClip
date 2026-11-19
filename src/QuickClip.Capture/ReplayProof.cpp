#include "ReplayProof.h"
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <limits>
#include <map>
#include <sstream>
#include <cstring>
#include <stdexcept>

extern "C" {
#include <libavutil/sha.h>
}

namespace
{
    constexpr uint64_t Magic = 0x3230464f52504351ULL; // QCPROF02, little endian
    constexpr uint32_t MaxPackets = 1'000'000;
    constexpr uint32_t MaxStreams = 32;
    using Hash = std::array<uint8_t, 32>;
    using Report = int(__stdcall*)(double);

    struct SavedPacket
    {
        qc::ReplayPacketProof original;
        int64_t pts = 0, dts = 0, duration = 0;
        int32_t flags = 0;
        Hash hash{};
        std::array<uint8_t, 10> skipSamples{};
    };
    struct Proof
    {
        uint32_t complete = 0;
        int64_t session = 0, utc = 0;
        std::vector<Hash> streams;
        std::vector<SavedPacket> packets;
    };

    template<typename T> void Put(std::ostream& out, const T& value)
    {
        out.write(reinterpret_cast<const char*>(&value), sizeof value);
        if (!out) throw std::runtime_error("Replay proof could not be written.");
    }
    template<typename T> T Get(std::istream& in)
    {
        T value{};
        in.read(reinterpret_cast<char*>(&value), sizeof value);
        if (!in) throw std::runtime_error("Replay proof is incomplete.");
        return value;
    }
    void PutOriginal(std::ostream& out, const qc::ReplayPacketProof& p)
    {
        Put(out, p.stream); Put(out, p.flags); Put(out, p.sequence); Put(out, p.source); Put(out, p.timeUs);
        Put(out, p.pts); Put(out, p.dts); Put(out, p.duration); Put(out, p.numerator); Put(out, p.denominator);
        Put(out, p.skipSamples);
    }
    qc::ReplayPacketProof GetOriginal(std::istream& in)
    {
        qc::ReplayPacketProof p;
        p.stream = Get<int32_t>(in); p.flags = Get<int32_t>(in);
        p.sequence = Get<int64_t>(in); p.source = Get<int64_t>(in); p.timeUs = Get<int64_t>(in);
        p.pts = Get<int64_t>(in); p.dts = Get<int64_t>(in); p.duration = Get<int64_t>(in);
        p.numerator = Get<int32_t>(in); p.denominator = Get<int32_t>(in);
        p.skipSamples = Get<std::array<uint8_t, 10>>(in);
        if (p.stream < 0 || p.stream >= static_cast<int>(MaxStreams) || p.numerator <= 0 || p.denominator <= 0 ||
            p.pts == AV_NOPTS_VALUE || p.dts == AV_NOPTS_VALUE || p.duration < 0 || p.timeUs < -10'000'000 ||
            p.timeUs > 604'800'000'000LL || p.pts < -1'000'000'000'000'000LL || p.pts > 1'000'000'000'000'000LL ||
            p.dts < -1'000'000'000'000'000LL || p.dts > 1'000'000'000'000'000LL)
            throw std::runtime_error("Replay proof contains unsupported packet timing.");
        const AVRational base{p.numerator, p.denominator};
        const auto ptsUs = av_rescale_q(p.pts, base, AVRational{1, 1'000'000});
        const auto dtsUs = av_rescale_q(p.dts, base, AVRational{1, 1'000'000});
        if (ptsUs < p.timeUs - 2 || ptsUs > p.timeUs + 2 || dtsUs < -10'000'000 || dtsUs > 604'800'000'000LL ||
            av_rescale_q(p.duration, base, AVRational{1, 1'000'000}) > 60'000'000)
            throw std::runtime_error("Replay proof timing cannot be represented safely.");
        return p;
    }
    void Write(const std::wstring& path, const Proof& proof)
    {
        std::ofstream out(std::filesystem::path(path), std::ios::binary | std::ios::trunc);
        Put(out, Magic); Put(out, proof.complete); Put(out, proof.session); Put(out, proof.utc);
        Put(out, static_cast<uint32_t>(proof.streams.size())); Put(out, static_cast<uint32_t>(proof.packets.size()));
        for (const auto& hash : proof.streams) Put(out, hash);
        for (const auto& p : proof.packets)
        {
            PutOriginal(out, p.original);
            if (proof.complete)
            {
                Put(out, p.pts); Put(out, p.dts); Put(out, p.duration); Put(out, p.flags); Put(out, p.hash);
                Put(out, p.skipSamples);
            }
        }
        out.flush();
        if (!out) throw std::runtime_error("Replay proof could not be flushed.");
    }
    Proof Read(const std::wstring& path)
    {
        const auto length = std::filesystem::file_size(path);
        if (length > 160'000'000) throw std::runtime_error("Replay proof exceeds the supported size.");
        std::ifstream in(std::filesystem::path(path), std::ios::binary);
        if (Get<uint64_t>(in) != Magic) throw std::runtime_error("Unsupported replay proof.");
        Proof proof;
        proof.complete = Get<uint32_t>(in); proof.session = Get<int64_t>(in); proof.utc = Get<int64_t>(in);
        auto streams = Get<uint32_t>(in), packets = Get<uint32_t>(in);
        if (proof.complete > 1 || streams > MaxStreams || packets == 0 || packets > MaxPackets ||
            proof.session <= 0 || proof.utc <= 0 || (proof.complete && streams == 0))
            throw std::runtime_error("Invalid replay proof header.");
        const uint64_t expected = 36ULL + streams * 32ULL + packets * (proof.complete ? 144ULL : 74ULL);
        if (expected != length) throw std::runtime_error("Replay proof length does not match its header.");
        proof.streams.reserve(streams); proof.packets.reserve(packets);
        for (uint32_t i = 0; i < streams; ++i) proof.streams.push_back(Get<Hash>(in));
        for (uint32_t i = 0; i < packets; ++i)
        {
            SavedPacket p;
            p.original = GetOriginal(in);
            if (proof.complete)
            {
                if (p.original.stream >= static_cast<int>(streams)) throw std::runtime_error("Invalid replay stream.");
                p.pts = Get<int64_t>(in); p.dts = Get<int64_t>(in); p.duration = Get<int64_t>(in);
                p.flags = Get<int32_t>(in); p.hash = Get<Hash>(in);
                p.skipSamples = Get<std::array<uint8_t, 10>>(in);
            }
            proof.packets.push_back(p);
        }
        return proof;
    }
    struct Input
    {
        AVFormatContext* value = nullptr;
        explicit Input(const wchar_t* path)
        {
            // Local callers have already checked availability and acquired read handles.
            if (avformat_open_input(&value, qc::Narrow(path).c_str(), av_find_input_format("mov"), nullptr) < 0)
                throw std::runtime_error("The replay MP4 could not be opened.");
            if (value->nb_streams == 0 || value->nb_streams > MaxStreams)
            {
                avformat_close_input(&value);
                throw std::runtime_error("Unsupported replay stream count.");
            }
        }
        ~Input() { avformat_close_input(&value); }
        Input(const Input&) = delete;
        Input& operator=(const Input&) = delete;
    };
    struct Packet
    {
        AVPacket* value = av_packet_alloc();
        Packet() { if (!value) throw std::bad_alloc(); }
        ~Packet() { av_packet_free(&value); }
    };
    Hash Digest(const void* bytes, size_t length)
    {
        auto* sha = av_sha_alloc();
        if (!sha) throw std::bad_alloc();
        Hash result{};
        av_sha_init(sha, 256); av_sha_update(sha, static_cast<const uint8_t*>(bytes), length);
        av_sha_final(sha, result.data()); av_free(sha);
        return result;
    }
    Hash StreamHash(AVStream* stream)
    {
        const auto& p = *stream->codecpar;
        // Serialize values, never ABI padding or owning pointers.
        std::ostringstream bytes(std::ios::binary);
        const int fields[] = {p.codec_type, p.codec_id, p.format, p.width, p.height, p.profile, p.level,
            p.sample_rate, p.ch_layout.nb_channels, p.field_order, p.color_range, p.color_primaries,
            p.color_trc, p.color_space, p.chroma_location, p.sample_aspect_ratio.num, p.sample_aspect_ratio.den,
            p.bits_per_coded_sample, p.bits_per_raw_sample, p.frame_size, p.initial_padding, p.trailing_padding,
            stream->disposition, stream->time_base.num, stream->time_base.den};
        for (int v : fields) Put(bytes, v);
        char layout[512]{};
        if (av_channel_layout_describe(&p.ch_layout, layout, sizeof layout) < 0)
            throw std::runtime_error("Unsupported channel layout.");
        bytes.write(layout, std::strlen(layout)); Put(bytes, uint8_t{});
        for (auto* key : {"title", "handler_name", "language"})
        {
            auto* tag = av_dict_get(stream->metadata, key, nullptr, 0);
            if (tag) bytes.write(tag->value, std::strlen(tag->value));
            Put(bytes, uint8_t{});
        }
        if (p.extradata_size < 0 || p.extradata_size > 1'048'576) throw std::runtime_error("Unsupported codec metadata.");
        if (p.extradata_size) bytes.write(reinterpret_cast<const char*>(p.extradata), p.extradata_size);
        auto value = bytes.str();
        return Digest(value.data(), value.size());
    }
    void Check(Report report, double progress)
    {
        if (report && report(progress)) throw std::runtime_error("Replay joining cancelled.");
    }
    // Match per-stream ordinal: MP4 interleaving can change cross-stream order.
    Proof Validate(const wchar_t* media, Proof proof, bool finalize, Report report)
    {
        Input input(media);
        auto* format = input.value;
        std::vector<Hash> hashes;
        for (unsigned s = 0; s < format->nb_streams; ++s) hashes.push_back(StreamHash(format->streams[s]));
        if ((!finalize || !proof.streams.empty()) && hashes != proof.streams) throw std::runtime_error("Replay stream parameters changed.");
        proof.streams = std::move(hashes);
        std::vector<std::vector<size_t>> ordinals(format->nb_streams);
        std::vector<size_t> next(format->nb_streams);
        for (size_t i = 0; i < proof.packets.size(); ++i)
        {
            auto s = proof.packets[i].original.stream;
            if (s >= static_cast<int>(format->nb_streams)) throw std::runtime_error("Replay stream missing.");
            ordinals[s].push_back(i);
        }
        Packet packet;
        size_t count = 0;
        int ret;
        while ((ret = av_read_frame(format, packet.value)) >= 0)
        {
            auto* p = packet.value;
            int s = p->stream_index;
            if (s < 0 || s >= static_cast<int>(next.size()) || next[s] >= ordinals[s].size())
                throw std::runtime_error("Replay packet count changed.");
            auto& saved = proof.packets[ordinals[s][next[s]++]];
            auto hash = Digest(p->data, p->size);
            std::array<uint8_t, 10> skip{};
            size_t skipSize = 0;
            const auto* skipData = av_packet_get_side_data(p, AV_PKT_DATA_SKIP_SAMPLES, &skipSize);
            if (skipData && skipSize >= skip.size()) std::copy_n(skipData, skip.size(), skip.begin());
            if (finalize)
            {
                if (saved.hash != Hash{} && saved.hash != hash) throw std::runtime_error("Muxing changed encoded packet data.");
                saved.pts = p->pts; saved.dts = p->dts; saved.duration = p->duration;
                saved.flags = p->flags; saved.hash = hash;
                saved.skipSamples = skip;
            }
            else if (saved.pts != p->pts || saved.dts != p->dts || saved.duration != p->duration ||
                saved.flags != p->flags || saved.hash != hash || saved.skipSamples != skip)
                throw std::runtime_error("Replay packet identity or timing changed.");
            av_packet_unref(p);
            if ((++count & 255) == 0) Check(report, static_cast<double>(count) / proof.packets.size());
        }
        if (ret != AVERROR_EOF || count != proof.packets.size()) throw std::runtime_error("Replay MP4 is incomplete.");
        proof.complete = 1;
        return proof;
    }
    void CopyError(const std::exception& ex, wchar_t* error, int length)
    {
        if (error && length > 0) wcsncpy_s(error, length, qc::Widen(ex.what()).c_str(), _TRUNCATE);
    }

    struct Output
    {
        AVFormatContext* value = nullptr;
        ~Output()
        {
            if (value) { if (value->pb) avio_closep(&value->pb); avformat_free_context(value); }
        }
    };

    bool SameOriginal(const SavedPacket& a, const SavedPacket& b)
    {
        const auto& x = a.original;
        const auto& y = b.original;
        return x.stream == y.stream && x.flags == y.flags && x.sequence == y.sequence &&
            x.source == y.source && x.timeUs == y.timeUs && x.pts == y.pts && x.dts == y.dts &&
            x.duration == y.duration && x.numerator == y.numerator && x.denominator == y.denominator &&
            x.skipSamples == y.skipSamples && a.hash == b.hash;
    }

    void Join(const wchar_t* const* media, const wchar_t* const* manifests, int count, const wchar_t* destination,
        const wchar_t* outputProof, int64_t capUs, Report report)
    {
        if (!media || !manifests || !destination || !outputProof || count < 2 || count > 32 || capUs <= 0 || capUs > 3'600'000'000LL)
            throw std::runtime_error("Invalid or excessive replay join request.");
        std::vector<Proof> proofs;
        proofs.reserve(count);
        size_t total = 0;
        int64_t epoch = 0, firstUs = INT64_MAX, lastUs = -1;
        // Validate every selected source before writing any output. Source handles
        // and scheduler reservations remain held by the managed job throughout.
        for (int i = 0; i < count; ++i)
        {
            Check(report, 0);
            if (!media[i] || !manifests[i]) throw std::runtime_error("Replay source missing.");
            auto proof = Read(manifests[i]);
            if (!proof.complete) throw std::runtime_error("Replay provenance is not yet validated.");
            total += proof.packets.size();
            if (total > MaxPackets) throw std::runtime_error("The selected replay metadata exceeds the job memory limit.");
            proof = Validate(media[i], std::move(proof), false, report);
            if (!proofs.empty() && (proof.session != proofs[0].session || proof.utc != proofs[0].utc || proof.streams != proofs[0].streams))
                throw std::runtime_error("Replays have different sessions or stream parameters.");
            int64_t clipStart = INT64_MAX;
            for (const auto& packet : proof.packets)
            {
                const auto& p = packet.original;
                if (p.stream != 0) continue;
                if (p.source <= 0 || (epoch && p.source != epoch)) throw std::runtime_error("Replays cross recording source epochs.");
                epoch = p.source;
                if (p.sequence <= 0 || p.pts != p.dts)
                    throw std::runtime_error("This replay requires additional decode-dependency provenance.");
                clipStart = std::min(clipStart, p.timeUs);
                firstUs = std::min(firstUs, p.timeUs); lastUs = std::max(lastUs, p.timeUs);
            }
            if (clipStart == INT64_MAX) throw std::runtime_error("Replay video is missing.");
            if (i && clipStart < [&] {
                int64_t start = INT64_MAX;
                for (const auto& p : proofs.back().packets) if (p.original.stream == 0) start = std::min(start, p.original.timeUs);
                return start;
            }()) throw std::runtime_error("Select replays in chronological order.");
            proofs.push_back(std::move(proof));
        }
        if (lastUs <= firstUs || lastUs - firstUs >= capUs) throw std::runtime_error("The replay union exceeds its duration limit.");

        struct Choice { int file; size_t index; };
        std::vector<std::map<int64_t, Choice>> selected(proofs[0].streams.size());
        std::vector<std::vector<bool>> keep;
        for (int file = 0; file < count; ++file)
        {
            const auto& proof = proofs[file];
            keep.emplace_back(proof.packets.size(), false);
            bool sharedVideo = file == 0;
            for (size_t index = 0; index < proof.packets.size(); ++index)
            {
                const auto& p = proof.packets[index];
                auto& stream = selected[p.original.stream];
                auto [where, fresh] = stream.emplace(p.original.dts, Choice{ file, index });
                if (fresh) keep[file][index] = true;
                else
                {
                    const auto& chosen = proofs[where->second.file].packets[where->second.index];
                    if (!SameOriginal(chosen, p)) throw std::runtime_error("Overlapping packet identities disagree.");
                    if (p.original.stream == 0) sharedVideo = true;
                }
            }
            if (!sharedVideo) throw std::runtime_error("These clips do not have proven overlapping footage.");
        }
        for (size_t s = 0; s < selected.size(); ++s)
        {
            const SavedPacket* previous = nullptr;
            AVRational base{};
            for (const auto& [dts, choice] : selected[s])
            {
                const auto& packet = proofs[choice.file].packets[choice.index];
                const auto& p = packet.original;
                if (!previous)
                {
                    base = {p.numerator, p.denominator};
                    if (s == 0 && !(p.flags & AV_PKT_FLAG_KEY)) throw std::runtime_error("The replay union does not begin at a decodable keyframe.");
                }
                else
                {
                    const auto& old = previous->original;
                    if (p.numerator != base.num || p.denominator != base.den)
                        throw std::runtime_error("Replay timebases changed.");
                    if (s == 0 ? p.sequence != old.sequence + 1 : old.duration <= 0 || dts - old.dts != old.duration)
                        throw std::runtime_error("A stream has missing or ambiguous encoded coverage.");
                }
                previous = &packet;
            }
            if (!previous) throw std::runtime_error("A replay stream is empty.");
        }

        Input first(media[0]);
        Output output;
        if (avformat_alloc_output_context2(&output.value, nullptr, "mp4", qc::Narrow(destination).c_str()) < 0 || !output.value)
            throw std::runtime_error("Could not create replay output.");
        auto* out = output.value;
        av_dict_copy(&out->metadata, first.value->metadata, 0);
        for (unsigned s = 0; s < first.value->nb_streams; ++s)
        {
            auto* stream = avformat_new_stream(out, nullptr);
            if (!stream || avcodec_parameters_copy(stream->codecpar, first.value->streams[s]->codecpar) < 0)
                throw std::runtime_error("Could not copy replay stream parameters.");
            stream->codecpar->codec_tag = 0;
            stream->time_base = first.value->streams[s]->time_base;
            stream->avg_frame_rate = first.value->streams[s]->avg_frame_rate;
            stream->disposition = first.value->streams[s]->disposition;
            av_dict_copy(&stream->metadata, first.value->streams[s]->metadata, 0);
        }
        if (avio_open(&out->pb, qc::Narrow(destination).c_str(), AVIO_FLAG_WRITE) < 0)
            throw std::runtime_error("Could not open staged replay output.");
        AVDictionary* options = nullptr;
        av_dict_set(&options, "movflags", "+faststart", 0);
        int ret = avformat_write_header(out, &options);
        av_dict_free(&options);
        if (ret < 0) throw std::runtime_error("Could not write replay header.");
        Proof joined;
        joined.session = proofs[0].session; joined.utc = proofs[0].utc;
        joined.streams = proofs[0].streams;
        joined.packets.reserve(total);
        size_t visited = 0;
        for (int file = 0; file < count; ++file)
        {
            Input input(media[file]);
            const auto& proof = proofs[file];
            std::vector<std::vector<size_t>> ordinals(selected.size());
            std::vector<size_t> next(selected.size());
            for (size_t index = 0; index < proof.packets.size(); ++index) ordinals[proof.packets[index].original.stream].push_back(index);
            Packet packet;
            while ((ret = av_read_frame(input.value, packet.value)) >= 0)
            {
                auto* p = packet.value;
                const int stream = p->stream_index;
                if (stream < 0 || stream >= static_cast<int>(selected.size()) || next[stream] >= ordinals[stream].size())
                    throw std::runtime_error("Replay source changed during joining.");
                const auto index = ordinals[stream][next[stream]++];
                if (keep[file][index])
                {
                    const auto& original = proof.packets[index].original;
                    const AVRational base{ original.numerator, original.denominator };
                    const auto offset = av_rescale_q(firstUs, AVRational{1, 1'000'000}, base);
                    p->pts = original.pts - offset; p->dts = original.dts - offset;
                    // Native zero-duration video packets acquire their presentation
                    // duration from the saved MP4. A later shared packet never adds
                    // another AAC priming packet at the seam.
                    p->duration = original.duration > 0 ? original.duration : av_rescale_q(p->duration, input.value->streams[stream]->time_base, base);
                    // MOV can derive different discard padding when each clip has
                    // a different positive audio start offset. Restore the original
                    // encoder's sample trimming, never a per-file demuxer's trim.
                    av_packet_side_data_remove(p->side_data, &p->side_data_elems, AV_PKT_DATA_SKIP_SAMPLES);
                    if (std::any_of(original.skipSamples.begin(), original.skipSamples.end(), [](auto b) { return b != 0; }))
                    {
                        auto* skip = av_packet_new_side_data(p, AV_PKT_DATA_SKIP_SAMPLES, original.skipSamples.size());
                        if (!skip) throw std::bad_alloc();
                        std::copy(original.skipSamples.begin(), original.skipSamples.end(), skip);
                    }
                    p->pos = -1;
                    av_packet_rescale_ts(p, base, out->streams[stream]->time_base);
                    if (av_interleaved_write_frame(out, p) < 0) throw std::runtime_error("Replay packet muxing failed.");
                    SavedPacket kept; kept.original = original; kept.hash = proof.packets[index].hash; joined.packets.push_back(kept);
                }
                av_packet_unref(p);
                if ((++visited & 255) == 0) Check(report, static_cast<double>(visited) / total);
            }
            if (ret != AVERROR_EOF) throw std::runtime_error("Replay source read failed.");
        }
        Check(report, 1);
        if (av_write_trailer(out) < 0 || avio_closep(&out->pb) < 0) throw std::runtime_error("Replay output did not close successfully.");
        auto completed = Validate(destination, std::move(joined), true, report);
        Write(outputProof, completed);
    }
}

namespace qc
{
    ReplayPacketProof CaptureReplayPacketProof(const PacketRing::Entry& entry, AVRational base)
    {
        auto* p = entry.packet;
        ReplayPacketProof proof{ entry.stream, p->flags, entry.sequence, entry.source, entry.timeUs,
            p->pts, p->dts, p->duration, base.num, base.den };
        size_t length = 0;
        const auto* skip = av_packet_get_side_data(p, AV_PKT_DATA_SKIP_SAMPLES, &length);
        if (skip && length >= proof.skipSamples.size()) std::copy_n(skip, proof.skipSamples.size(), proof.skipSamples.begin());
        return proof;
    }

    bool WritePendingReplayProof(const std::wstring& path, int64_t session, int64_t startedUtcUs,
        const std::vector<ReplayPacketProof>& packets, std::string& error)
    {
        try
        {
            if (packets.empty() || packets.size() > MaxPackets) throw std::runtime_error("Replay proof packet limit exceeded.");
            Proof proof;
            proof.session = session; proof.utc = startedUtcUs;
            proof.packets.reserve(packets.size());
            for (const auto& original : packets) { SavedPacket p; p.original = original; proof.packets.push_back(p); }
            Write(path, proof);
            return true;
        }
        catch (const std::exception& ex) { error = ex.what(); return false; }
    }
}

extern "C"
{
    // Generated-media fixture only. The recording path creates its provenance in
    // Engine::Save; no runtime caller reconstructs provenance from older media.
    __declspec(dllexport) int qc_test_replay_snapshot(const wchar_t* reference, const wchar_t* destination,
        const wchar_t* pending, int64_t fromUs, int64_t toUs, int64_t epoch, wchar_t* error, int length)
    {
        try
        {
            if (!reference || !destination || !pending || fromUs < 0 || toUs <= fromUs || toUs > 30'000'000 || epoch <= 0)
                throw std::runtime_error("Invalid generated replay fixture.");
            Input input(reference);
            qc::PacketRing ring(60'000'000);
            Packet packet;
            int ret;
            size_t count = 0;
            while ((ret = av_read_frame(input.value, packet.value)) >= 0)
            {
                auto* p = packet.value;
                if (++count > 100'000 || p->pts == AV_NOPTS_VALUE) throw std::runtime_error("Generated fixture limit exceeded.");
                int64_t time = av_rescale_q(p->pts, input.value->streams[p->stream_index]->time_base, AVRational{1, 1'000'000});
                auto* copy = av_packet_clone(p);
                if (!copy) throw std::bad_alloc();
                ring.Push(copy, p->stream_index, time, (p->flags & AV_PKT_FLAG_KEY) != 0, p->stream_index == 0 ? epoch : 0);
                av_packet_unref(p);
            }
            if (ret != AVERROR_EOF) throw std::runtime_error("Could not read generated reference.");
            int64_t startUs = 0;
            auto entries = ring.Snapshot(fromUs, toUs, startUs);
            struct FreeEntries
            {
                std::vector<qc::PacketRing::Entry>& entries;
                ~FreeEntries() { for (auto& e : entries) av_packet_free(&e.packet); }
            } cleanup{ entries };
            if (entries.empty()) throw std::runtime_error("Generated fixture has no decodable snapshot.");
            std::stable_sort(entries.begin(), entries.end(), [](const auto& a, const auto& b) { return a.timeUs < b.timeUs; });
            Output output;
            if (avformat_alloc_output_context2(&output.value, nullptr, "mp4", qc::Narrow(destination).c_str()) < 0 || !output.value)
                throw std::runtime_error("Could not create generated snapshot.");
            auto* out = output.value;
            for (unsigned s = 0; s < input.value->nb_streams; ++s)
            {
                auto* stream = avformat_new_stream(out, nullptr);
                if (!stream || avcodec_parameters_copy(stream->codecpar, input.value->streams[s]->codecpar) < 0)
                    throw std::runtime_error("Could not copy generated stream.");
                stream->codecpar->codec_tag = 0;
                stream->time_base = input.value->streams[s]->time_base;
                stream->avg_frame_rate = input.value->streams[s]->avg_frame_rate;
                stream->disposition = input.value->streams[s]->disposition;
                av_dict_copy(&stream->metadata, input.value->streams[s]->metadata, 0);
            }
            if (avio_open(&out->pb, qc::Narrow(destination).c_str(), AVIO_FLAG_WRITE) < 0 || avformat_write_header(out, nullptr) < 0)
                throw std::runtime_error("Could not open generated snapshot.");
            std::vector<qc::ReplayPacketProof> proof;
            for (auto& e : entries)
            {
                auto* p = e.packet;
                auto base = input.value->streams[e.stream]->time_base;
                proof.push_back(qc::CaptureReplayPacketProof(e, base));
                auto offset = av_rescale_q(startUs, AVRational{1, 1'000'000}, base);
                p->pts -= offset; p->dts -= offset; p->stream_index = e.stream;
                av_packet_rescale_ts(p, base, out->streams[e.stream]->time_base);
                if (av_interleaved_write_frame(out, p) < 0) throw std::runtime_error("Could not mux generated snapshot.");
            }
            if (av_write_trailer(out) < 0 || avio_closep(&out->pb) < 0) throw std::runtime_error("Generated snapshot did not close.");
            std::string message;
            if (!qc::WritePendingReplayProof(pending, 123, 1'700'000'000'000'000, proof, message)) throw std::runtime_error(message);
            return 1;
        }
        catch (const std::exception& ex) { CopyError(ex, error, length); return 0; }
    }

    __declspec(dllexport) int qc_join_replay_packets(const wchar_t* const* media, const wchar_t* const* manifests, int count,
        const wchar_t* destination, const wchar_t* outputProof, int64_t capUs, Report report, wchar_t* error, int length)
    {
        try { Join(media, manifests, count, destination, outputProof, capUs, report); return 1; }
        catch (const std::exception& ex) { CopyError(ex, error, length); return 0; }
    }

    __declspec(dllexport) int qc_finalize_replay_proof(const wchar_t* media, const wchar_t* pending,
        const wchar_t* output, Report report, wchar_t* error, int length)
    {
        try
        {
            if (!media || !pending || !output) throw std::runtime_error("Invalid replay proof request.");
            auto proof = Read(pending);
            if (proof.complete) throw std::runtime_error("Only a new recording snapshot can create replay provenance.");
            Check(report, 0);
            proof = Validate(media, std::move(proof), true, report);
            Write(output, proof);
            return 1;
        }
        catch (const std::exception& ex) { CopyError(ex, error, length); return 0; }
    }
}

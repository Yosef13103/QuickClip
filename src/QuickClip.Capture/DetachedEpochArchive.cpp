// Experimental independently decodable per-graph segment writer.
// Kept out of production save paths; built and exercised only by detached epoch fixtures.
#include "DetachedEpochArchive.h"

namespace qc
{
    bool DetachedEpochArchive::WriteSegment(size_t index, const std::wstring& path, std::string& error) const
    {
        auto epoch = Pin(index);
        if (!epoch) { error = "Detached epoch does not exist."; return false; }
        return WritePinnedSegment(*epoch, path, error);
    }

    bool DetachedEpochArchive::WritePinnedSegment(const Epoch& value, const std::wstring& path, std::string& error)
    {
        const Epoch* epoch = &value;
        if (!epoch || epoch->packets.empty() || epoch->codecs.empty() ||
            epoch->codecs.size() != epoch->timeBases.size() ||
            epoch->codecs.size() != epoch->streamTitles.size() || path.empty())
        {
            error = "Detached epoch stream configuration is invalid.";
            return false;
        }
        if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            error = "The epoch segment destination already exists.";
            return false;
        }

        const std::string output = Narrow(path);
        AVFormatContext* context = nullptr;
        int status = avformat_alloc_output_context2(&context, nullptr, "matroska", output.c_str());
        if (status < 0 || !context)
        {
            error = "Could not create the epoch container: " + AvError(status);
            return false;
        }

        for (size_t i = 0; i < epoch->codecs.size(); ++i)
        {
            AVStream* stream = avformat_new_stream(context, nullptr);
            if (!stream) { status = AVERROR(ENOMEM); break; }
            status = avcodec_parameters_copy(stream->codecpar, epoch->codecs[i]);
            if (status < 0) break;
            stream->codecpar->codec_tag = 0;
            stream->time_base = epoch->timeBases[i];
            if (i == 1) stream->disposition = AV_DISPOSITION_DEFAULT;
            if (i > 0)
            {
                av_dict_set(&stream->metadata, "title", epoch->streamTitles[i].c_str(), 0);
                av_dict_set(&stream->metadata, "handler_name", epoch->streamTitles[i].c_str(), 0);
            }
        }
        if (status >= 0) status = avio_open(&context->pb, output.c_str(), AVIO_FLAG_WRITE);
        if (status >= 0) status = avformat_write_header(context, nullptr);
        const int64_t originUs = epoch->startUs - epoch->header.graphStartUs;
        for (const auto& entry : epoch->packets)
        {
            if (status < 0) break;
            if (!entry.packet || entry.stream < 0 ||
                static_cast<size_t>(entry.stream) >= context->nb_streams)
            {
                status = AVERROR(EINVAL);
                break;
            }
            AVPacket* packet = av_packet_clone(entry.packet);
            if (!packet) { status = AVERROR(ENOMEM); break; }
            const AVRational sourceBase = epoch->timeBases[entry.stream];
            const int64_t offset = av_rescale_q(originUs, AVRational{1, 1000000}, sourceBase);
            if (packet->pts != AV_NOPTS_VALUE) packet->pts -= offset;
            if (packet->dts != AV_NOPTS_VALUE) packet->dts -= offset;
            packet->stream_index = entry.stream;
            av_packet_rescale_ts(packet, sourceBase, context->streams[entry.stream]->time_base);
            status = av_interleaved_write_frame(context, packet);
            av_packet_free(&packet);
        }
        if (status >= 0) status = av_write_trailer(context);
        if (context->pb)
        {
            const int closed = avio_closep(&context->pb);
            if (status >= 0) status = closed;
        }
        avformat_free_context(context);
        if (status < 0)
        {
            error = "Writing detached epoch media failed: " + AvError(status);
            DeleteFileW(path.c_str());
            return false;
        }
        return true;
    }
}

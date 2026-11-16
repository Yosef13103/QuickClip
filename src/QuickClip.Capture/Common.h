// Shared helpers for the QuickClip capture engine.
#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <string>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavfilter/avfilter.h>
#include <libavfilter/buffersink.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/hwcontext.h>
#include <libavutil/opt.h>
}

namespace qc
{
    constexpr int SampleRate = 48000;
    constexpr int Channels = 2;

    /// Microseconds on the performance counter. Every timestamp in the engine uses this clock, which is also
    /// the clock WASAPI reports packet times in, so audio and video line up without drift.
    inline int64_t NowUs()
    {
        static const int64_t freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f.QuadPart; }();
        LARGE_INTEGER c;
        QueryPerformanceCounter(&c);
        return c.QuadPart / freq * 1000000 + c.QuadPart % freq * 1000000 / freq;
    }

    // Values shared with the managed diagnostics bridge.
    enum class LogCategory : int { Capture = 1, Audio = 4 };
    enum class LogSeverity : int { Information, Warning, Error };
    // Source-controlled literal messages only. Keep paths, process identities and device IDs out.
    void Log(LogCategory category, const char* message, LogSeverity severity = LogSeverity::Information, int code = 0);

    std::string AvError(int err);
    std::string Narrow(const std::wstring& s);
    std::wstring Widen(const std::string& s);
}

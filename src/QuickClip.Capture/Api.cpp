// C API used by the QuickClip app (see QuickClip/Recording/CaptureEngine.cs).
#include "Engine.h"
#include <algorithm>
#include <cmath>

#include <shared_mutex>

namespace
{
    typedef void(__stdcall* LogCallback)(qc::LogCategory, qc::LogSeverity, const char*, int);
    std::atomic<LogCallback> g_log{ nullptr };

    std::shared_mutex g_engineLock;   // Save holds it shared; start/stop exclusively
    std::unique_ptr<qc::Engine> g_engine;
    std::mutex g_micTestLock;
    std::unique_ptr<qc::AudioSource> g_micTest;
    int64_t g_micTestToken = 0;
    constexpr int MicSampleMilliseconds = 5000;
    constexpr int MicSampleCount = qc::SampleRate * qc::Channels * MicSampleMilliseconds / 1000;
    // Allocated only by an explicit sample request. One worker writes; the consumer
    // copies only after TestFinished publishes completion. No audio is written to disk.
    struct MicSample
    {
        std::vector<int16_t> pcm = std::vector<int16_t>(MicSampleCount);
        size_t used = 0;
        std::mutex lock;
        bool discarded = false;
        void Discard()
        {
            std::lock_guard guard(lock);
            discarded = true;
            std::fill(pcm.begin(), pcm.end(), 0);
            used = 0;
        }
    };
    std::shared_ptr<MicSample> g_micSample;

    class MicSampleSink final : public qc::AudioSink
    {
    public:
        explicit MicSampleSink(std::shared_ptr<MicSample> sample) : sample_(std::move(sample)) {}
        void Push(const float* data, uint32_t frames, int64_t, bool) override
        {
            auto& sample = *sample_;
            std::lock_guard guard(sample.lock);
            if (sample.discarded) return;
            size_t count = std::min(static_cast<size_t>(frames) * qc::Channels, sample.pcm.size() - sample.used);
            for (size_t i = 0; i < count; ++i)
            {
                float value = data && std::isfinite(data[i]) ? std::clamp(data[i], -1.f, 1.f) : 0.f;
                sample.pcm[sample.used + i] = static_cast<int16_t>(std::lround(value * (value < 0 ? 32768.f : 32767.f)));
            }
            sample.used += count;
        }
    private:
        std::shared_ptr<MicSample> sample_;
    };

    // Called with the engine lock held. Release the test lock before joining its worker.
    void DrainMicrophoneTest()
    {
        std::unique_ptr<qc::AudioSource> test;
        {
            std::lock_guard guard(g_micTestLock);
            test = std::move(g_micTest);
            if (g_micSample) g_micSample->Discard();
            g_micSample.reset();
        }
    }

    void CopyError(const std::string& message, wchar_t* error, int length)
    {
        if (!error || length <= 0) return;
        wcsncpy_s(error, static_cast<size_t>(length), qc::Widen(message).c_str(), _TRUNCATE);
    }

    int StartEngine(const QcConfig& config, bool endpointMode, const QcEndpointTrack* endpoints, int count, wchar_t* error, int length,
        QcVideoOptions video = {}, const wchar_t* displayIdentity = nullptr, const QcGameWindow* game = nullptr)
    {
        std::unique_lock guard(g_engineLock);
        DrainMicrophoneTest();
        g_engine.reset();
        try
        {
            auto engine = std::make_unique<qc::Engine>(config, endpointMode, endpoints, count, video, displayIdentity, game);
            std::string message;
            if (engine->Start(message))
            {
                g_engine = std::move(engine);
                return 1;
            }
            CopyError(message, error, length);
        }
        catch (const std::exception& ex)
        {
            CopyError(ex.what(), error, length);
        }
        return 0;
    }
}

namespace qc
{
    void Log(LogCategory category, const char* message, LogSeverity severity, int code)
    {
        if (auto callback = g_log.load(std::memory_order_relaxed)) callback(category, severity, message, code);
    }

    std::string AvError(int err)
    {
        char buffer[AV_ERROR_MAX_STRING_SIZE] = {};
        av_strerror(err, buffer, sizeof buffer);
        return buffer;
    }

    std::string Narrow(const std::wstring& s)
    {
        if (s.empty()) return {};
        int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
        std::string out(static_cast<size_t>(n), '\0');
        WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n, nullptr, nullptr);
        return out;
    }

    std::wstring Widen(const std::string& s)
    {
        if (s.empty()) return {};
        int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
        std::wstring out(static_cast<size_t>(n), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n);
        return out;
    }
}

extern "C"
{
    __declspec(dllexport) void qc_set_diagnostic_log(LogCallback callback)
    {
        // FFmpeg text can include paths and may be generated on individual frames. Native operations
        // report failures through their existing result/status APIs and curated events instead.
        static std::once_flag quietFfmpeg;
        std::call_once(quietFfmpeg, [] { av_log_set_level(AV_LOG_QUIET); });
        g_log.store(callback, std::memory_order_relaxed);
    }

    __declspec(dllexport) int qc_enum_outputs(QcOutput* outputs, int max)
    {
        return qc::EnumOutputs(outputs, max);
    }

    __declspec(dllexport) int qc_enum_microphones(QcAudioDevice* devices, int max)
    {
        return qc::EnumMicrophones(devices, max);
    }

    __declspec(dllexport) void qc_enum_audio_endpoints(QcEndpointCallback callback)
    {
        if (callback) qc::EnumAudioEndpoints(callback);
    }

    __declspec(dllexport) int qc_get_endpoint_status(QcEndpointStatus* statuses, int capacity)
    {
        if (!statuses || capacity <= 0) return 0;
        std::shared_lock guard(g_engineLock);
        return g_engine ? g_engine->GetEndpointStatus(statuses, capacity) : 0;
    }

    __declspec(dllexport) int qc_read_audio_signals(QcAudioSignal* signals, int capacity, int size)
    {
        if (!signals || capacity <= 0 || size != sizeof(QcAudioSignal)) return 0;
        std::shared_lock guard(g_engineLock, std::try_to_lock);
        return guard.owns_lock() && g_engine ? g_engine->GetAudioSignals(signals, capacity) : 0;
    }

    static HRESULT StartMicTest(const wchar_t* id, int milliseconds, int64_t* token, bool recordSample)
    {
        if (!id || wcsnlen_s(id, 512) >= 512 || milliseconds <= 0 || milliseconds > 10000 || !token)
            return E_INVALIDARG;
        std::shared_lock engineGuard(g_engineLock, std::try_to_lock);
        if (!engineGuard.owns_lock() || g_engine) return HRESULT_FROM_WIN32(ERROR_BUSY);
        std::lock_guard guard(g_micTestLock);
        if (g_micTest && !g_micTest->TestFinished()) return HRESULT_FROM_WIN32(ERROR_BUSY);
        try
        {
            auto sample = recordSample ? std::make_shared<MicSample>() : nullptr;
            std::unique_ptr<qc::AudioSink> sink;
            if (sample) sink = std::make_unique<MicSampleSink>(sample);
            g_micTest = std::make_unique<qc::AudioSource>(qc::SourceKind::Microphone, 0, id, qc::SampleRate, std::move(sink),
                -1, false, static_cast<DWORD>(milliseconds));
            g_micSample = std::move(sample);
            *token = ++g_micTestToken;
            return S_OK;
        }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }

    __declspec(dllexport) HRESULT qc_start_mic_test(const wchar_t* id, int milliseconds, int64_t* token)
    {
        return StartMicTest(id, milliseconds, token, false);
    }

    __declspec(dllexport) HRESULT qc_start_mic_sample(const wchar_t* id, int64_t* token)
    {
        return StartMicTest(id, MicSampleMilliseconds, token, true);
    }

    // Positive = actual interleaved sample count; zero = still finishing; negative = unavailable.
    __declspec(dllexport) int qc_read_mic_sample(int64_t token, int16_t* pcm, int capacity)
    {
        if (!pcm || capacity < MicSampleCount) return -1;
        std::lock_guard guard(g_micTestLock);
        if (!g_micTest || !g_micSample || token != g_micTestToken) return -1;
        if (!g_micTest->TestFinished()) return 0;
        const auto status = g_micTest->SignalStatus();
        std::lock_guard sampleGuard(g_micSample->lock);
        if (FAILED(status.error) || status.stage != static_cast<int>(qc::AudioStage::Complete) ||
            status.actualFlow != 1 /* eCapture */ || !g_micSample->used) return -1;
        std::copy_n(g_micSample->pcm.data(), g_micSample->used, pcm);
        return static_cast<int>(g_micSample->used);
    }

    __declspec(dllexport) int qc_read_mic_test(int64_t token, QcAudioSignal* signal, int size)
    {
        if (!signal || size != sizeof(QcAudioSignal)) return 0;
        std::lock_guard guard(g_micTestLock);
        if (!g_micTest || g_micTestToken != token) return 0;
        *signal = g_micTest->SignalStatus();
        return 1;
    }

    __declspec(dllexport) void qc_stop_mic_test(int64_t token)
    {
        std::lock_guard guard(g_micTestLock);
        if (g_micTest && g_micTestToken == token)
        {
            g_micTest->RequestStop();
            if (g_micSample) g_micSample->Discard();
            g_micSample.reset();
            ++g_micTestToken;
        }
    }

    // Synthetic float fixture only: validates the default listening mix's
    // summation/soft limit without capturing any private device audio.
    __declspec(dllexport) int qc_test_playback_mix(const float* left, const float* right, int count, float* output)
    {
        if (!left || !right || !output || count < 0 || count > 192000) return -1;
        for (int i = 0; i < count; ++i)
            output[i] = qc::SoftLimitPlaybackSample(left[i] + right[i]);
        return count;
    }

    // Synthetic float fixture only. Does not open an endpoint.
    __declspec(dllexport) float qc_test_audio_packet_peak(const float* samples, int count)
    {
        return count >= 0 && count <= 48000 ? qc::AudioPacketPeak(samples, static_cast<size_t>(count)) : -1;
    }

    // Feed synthetic 20 ms packets through the production mixer without opening an endpoint.
    __declspec(dllexport) int qc_test_audio_timeline(const int* startsMs, const int* sources, const int* restarts,
        int packets, int drainBeforePacket, int drainMs, float* left, int outputFrames)
    {
        if (!startsMs || !sources || !restarts || !left || packets <= 0 || packets > 128 ||
            outputFrames <= 0 || outputFrames > qc::SampleRate ||
            drainBeforePacket < 0 || drainBeforePacket > packets ||
            drainMs < 0 || drainMs > outputFrames / (qc::SampleRate / 1000)) return 0;
        qc::TrackMixer mixer(0, true);
        qc::MixerSink first(mixer, qc::SampleRate), second(mixer, qc::SampleRate);
        constexpr int frames = qc::SampleRate / 50;
        const std::vector<float> samples(frames * qc::Channels, 0.75f);
        std::vector<float> mixed;
        int written = 0;
        auto drain = [&](int until) {
            mixer.Take(until, mixed);
            for (size_t i = 0; i < mixed.size(); i += qc::Channels)
                left[written++] = mixed[i];
        };
        for (int i = 0; i < packets; ++i)
        {
            if (startsMs[i] < 0 || startsMs[i] > 10000 ||
                (sources[i] != 0 && sources[i] != 1)) return 0;
            if (i == drainBeforePacket) drain(drainMs * (qc::SampleRate / 1000));
            auto& sink = sources[i] == 0 ? first : second;
            sink.Push(samples.data(), frames, 1000LL * startsMs[i], restarts[i] != 0);
        }
        drain(outputFrames);
        return written;
    }

    // Synthetic float fixture only. Does not open an endpoint.
    __declspec(dllexport) int qc_test_audio_levels(const float* samples, int count, float* peak, float* rms, int64_t* mixerBlocks)
    {
        if (count < 0 || count > 48000 || count % qc::Channels != 0 || !peak || !rms || !mixerBlocks) return 0;
        auto levels = qc::MeasureAudioLevels(samples, static_cast<size_t>(count));
        *peak = levels.peak;
        *rms = levels.rms;
        qc::TrackMixer mixer(0, true);
        qc::MixerSink sink(mixer, qc::SampleRate);
        sink.Push(samples, static_cast<uint32_t>(count / qc::Channels), 0, true);
        std::vector<float> mixed;
        mixer.Take(count / qc::Channels, mixed);
        *mixerBlocks = mixer.NonzeroBlocks();
        return 1;
    }

    __declspec(dllexport) void qc_display_diagnostics(wchar_t* text, int length)
    {
        CopyError(qc::DisplayDiagnostics(), text, length);
    }

    __declspec(dllexport) int qc_start(const QcConfig* config, wchar_t* error, int errorLength)
    {
        if (!config) return 0;
        return StartEngine(*config, false, nullptr, 0, error, errorLength);
    }

    __declspec(dllexport) int qc_start_with_source(const QcConfig* config, const QcEndpointTrack* endpoints, int count,
        const QcVideoOptions* video, const wchar_t* displayIdentity, const QcGameWindow* game, wchar_t* error, int errorLength)
    {
        if (!config || !video || count < -1 || count > 16 || (count > 0 && !endpoints) ||
            video->backend < 0 || video->backend > 4 || video->nvencPreset < 0 || video->nvencPreset > 2)
        {
            CopyError("Invalid endpoint audio configuration.", error, errorLength);
            return 0;
        }
        for (int i = 0; i < count; i++)
        {
            const auto& track = endpoints[i];
            if ((track.flow != 0 && track.flow != 1) || !track.deviceId || !*track.deviceId || !track.title || !*track.title)
            {
                CopyError("Each audio track needs a playback or microphone endpoint and a name.", error, errorLength);
                return 0;
            }
            for (int prior = 0; prior < i; prior++)
                if (_wcsicmp(track.deviceId, endpoints[prior].deviceId) == 0)
                {
                    CopyError("An audio endpoint can only be recorded once.", error, errorLength);
                    return 0;
                }
        }
        if (displayIdentity && (!*displayIdentity || video->backend != 3 || !config->monitorDevice || !*config->monitorDevice))
        {
            CopyError("Authorized display capture requires a named display and Desktop Duplication.", error, errorLength);
            return 0;
        }
        if (game && (!displayIdentity || !game->handle || !game->thread || !game->process || game->started <= 0))
        {
            CopyError("The game window and its authorized display could not be verified.", error, errorLength);
            return 0;
        }
        return StartEngine(*config, count >= 0, endpoints, std::max(0, count), error, errorLength, *video, displayIdentity, game);
    }

    __declspec(dllexport) int qc_start_with_display(const QcConfig* config, const QcEndpointTrack* endpoints, int count,
        const QcVideoOptions* video, const wchar_t* displayIdentity, wchar_t* error, int errorLength)
    {
        return qc_start_with_source(config, endpoints, count, video, displayIdentity, nullptr, error, errorLength);
    }

    __declspec(dllexport) int qc_start_with_video(const QcConfig* config, const QcEndpointTrack* endpoints, int count,
        const QcVideoOptions* video, wchar_t* error, int errorLength)
    {
        return qc_start_with_display(config, endpoints, count, video, nullptr, error, errorLength);
    }

    __declspec(dllexport) int qc_start_endpoints(const QcConfig* config, const QcEndpointTrack* endpoints, int count,
        wchar_t* error, int errorLength)
    {
        if (count < 0) return 0;
        QcVideoOptions video{};
        return qc_start_with_video(config, endpoints, count, &video, error, errorLength);
    }

    __declspec(dllexport) void qc_video_diagnostics(wchar_t* text, int length)
    {
        std::shared_lock guard(g_engineLock);
        CopyError(g_engine ? g_engine->VideoDiagnostics() : "", text, length);
    }

    __declspec(dllexport) void qc_stop()
    {
        std::unique_lock guard(g_engineLock);
        DrainMicrophoneTest();
        g_engine.reset();
    }

    __declspec(dllexport) void qc_set_audio_processes(uint32_t desktopExclude, const uint32_t* desktopInclude, int desktopCount,
        const uint32_t* chat, int chatCount, const uint32_t* music, int musicCount, const uint32_t* subtract, int subtractCount)
    {
        std::shared_lock guard(g_engineLock);
        if (!g_engine) return;
        auto list = [](const uint32_t* pids, int count) { return std::vector<DWORD>(pids, pids + (pids && count > 0 ? count : 0)); };
        g_engine->SetAudioProcesses(desktopExclude, list(desktopInclude, desktopCount), list(chat, chatCount), list(music, musicCount),
            list(subtract, subtractCount));
    }

    __declspec(dllexport) int64_t qc_test_subtract_offset(uint32_t pid)
    {
        std::shared_lock guard(g_engineLock);
        return g_engine ? g_engine->TestSubtractOffset(pid) : INT64_MIN;
    }

    __declspec(dllexport) void qc_test_play_noise(int milliseconds, float amplitude)
    {
        qc::PlayTestNoise(milliseconds, amplitude);
    }

    __declspec(dllexport) void qc_restart_video()
    {
        std::shared_lock guard(g_engineLock);
        if (g_engine) g_engine->RequestRebuild();
    }

    /// Records this window (a game's) instead of the monitor, at this size and bitrate; 0 goes back to the monitor.
    __declspec(dllexport) void qc_set_capture_window(uint64_t window, int width, int height, int videoKbps)
    {
        std::shared_lock guard(g_engineLock);
        if (g_engine) g_engine->SetCaptureWindow(reinterpret_cast<HWND>(static_cast<uintptr_t>(window)), width, height, videoKbps);
    }

    __declspec(dllexport) void qc_test_freeze_video(int milliseconds)
    {
        std::shared_lock guard(g_engineLock);
        if (g_engine) g_engine->TestFreezeVideo(milliseconds);
    }

    __declspec(dllexport) void qc_test_flat_below(int bytes)
    {
        std::shared_lock guard(g_engineLock);
        if (g_engine) g_engine->TestFlatBelow(bytes);
    }

    __declspec(dllexport) int qc_save(const wchar_t* path, int seconds, const wchar_t* title, wchar_t* error, int errorLength)
    {
        std::shared_lock guard(g_engineLock);
        if (!g_engine)
        {
            CopyError("Recording is off.", error, errorLength);
            return 0;
        }
        std::string message;
        if (!g_engine->Save(path, seconds, title ? title : L"", message))
        {
            CopyError(message, error, errorLength);
            return 0;
        }
        return 1;
    }

    __declspec(dllexport) int qc_get_stats(QcStats* stats)
    {
        std::shared_lock guard(g_engineLock);
        *stats = {};
        if (!g_engine) return 0;
        g_engine->GetStats(*stats);
        return 1;
    }

    __declspec(dllexport) int qc_video_source_lost()
    {
        std::shared_lock guard(g_engineLock);
        return g_engine && g_engine->VideoSourceLost();
    }

    __declspec(dllexport) int qc_has_video_output()
    {
        std::shared_lock guard(g_engineLock);
        return g_engine && g_engine->HasVideoOutput();
    }

    __declspec(dllexport) int qc_test_authorized_video_invalidation(int stall)
    {
        return qc::Engine::TestAuthorizedVideoInvalidation(stall != 0);
    }

    __declspec(dllexport) int qc_get_replay_position(QcReplayPosition* position)
    {
        std::shared_lock guard(g_engineLock);
        if (!g_engine || !position || g_engine->VideoSourceLost()) return 0;
        g_engine->GetReplayPosition(*position);
        return 1;
    }

    __declspec(dllexport) int qc_save_replay_with_video(const wchar_t* path, int seconds, const wchar_t* title, int64_t session,
        int64_t requestUs, QcReplaySpan* span, QcReplayAnchor* anchors, int count, QcReplayVideo* savedVideo,
        wchar_t* error, int errorLength)
    {
        if (savedVideo) *savedVideo = {};
        if (!path || !span || seconds <= 0 || count < 0 || count > 128 || (count && !anchors))
        {
            CopyError("Invalid replay request.", error, errorLength);
            return 0;
        }
        std::shared_lock guard(g_engineLock);
        QcReplayPosition position{};
        if (g_engine) g_engine->GetReplayPosition(position);
        if (!g_engine || position.session != session || g_engine->VideoSourceLost())
        {
            CopyError("The recording session ended before the replay could be saved.", error, errorLength);
            return 0;
        }
        std::string message;
        if (g_engine->Save(path, seconds, title ? title : L"", message, span, anchors, count, requestUs, savedVideo)) return 1;
        CopyError(message, error, errorLength);
        return 0;
    }

    __declspec(dllexport) int qc_save_replay(const wchar_t* path, int seconds, const wchar_t* title, int64_t session,
        int64_t requestUs, QcReplaySpan* span, QcReplayAnchor* anchors, int count, wchar_t* error, int errorLength)
    {
        return qc_save_replay_with_video(path, seconds, title, session, requestUs, span, anchors, count, nullptr, error, errorLength);
    }

    // Deterministic packet fixture for the managed tests. No encoder, audio endpoints or desktop are opened.
    struct QcTestReplayFrame { int64_t timeUs, source; int key, clearBefore; };

    __declspec(dllexport) int qc_test_replay_ring(const QcTestReplayFrame* frames, int frameCount, int bufferSeconds,
        int requestedSeconds, int64_t requestUs, QcReplayPosition* position, QcReplaySpan* span,
        QcReplayAnchor* anchors, int anchorCount, int* streamCounts)
    {
        if (!frames || frameCount < 1 || frameCount > 1024 || bufferSeconds < 1 || requestedSeconds < 1 ||
            !position || !span || !streamCounts || anchorCount < 0 || anchorCount > 128 || (anchorCount && !anchors)) return 0;
        qc::PacketRing ring(static_cast<int64_t>(bufferSeconds) * 1'000'000);
        for (int i = 0; i < frameCount; ++i)
        {
            const auto& frame = frames[i];
            if (frame.clearBefore) ring.Clear();
            for (int stream = 0; stream < 4; ++stream)
            {
                auto* packet = av_packet_alloc();
                if (!packet) return 0;
                ring.Push(packet, stream, frame.timeUs, stream == 0 && frame.key != 0, frame.source);
            }
        }
        *position = {};
        position->eventUs = requestUs;
        ring.Position(*position);
        std::fill_n(streamCounts, 4, 0);
        int64_t first = 0;
        auto packets = ring.Snapshot(requestUs - static_cast<int64_t>(requestedSeconds) * 1'000'000,
            requestUs, first, span, anchors, anchorCount, 1'000'000 / 60);
        for (auto& entry : packets)
        {
            ++streamCounts[entry.stream];
            av_packet_free(&entry.packet);
        }
        return !packets.empty();
    }
}

// C API used by the QuickClip app (see QuickClip/Recording/CaptureEngine.cs).
#include "Engine.h"
#include "DetachedEpochArchive.h"
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
        QcVideoOptions video = {}, const wchar_t* displayIdentity = nullptr, const QcGameWindow* game = nullptr,
        const QcGameWindow* companion = nullptr, int companionCount = 0)
    {
        std::unique_lock guard(g_engineLock);
        DrainMicrophoneTest();
        g_engine.reset();
        try
        {
            auto engine = std::make_unique<qc::Engine>(config, endpointMode, endpoints, count, video, displayIdentity, game, companion, companionCount);
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

    // Finish an explicit sample without discarding its captured PCM. The normal
    // read endpoint remains unavailable until the source has actually stopped.
    __declspec(dllexport) int qc_finish_mic_sample(int64_t token)
    {
        std::lock_guard guard(g_micTestLock);
        if (!g_micTest || !g_micSample || g_micTestToken != token) return 0;
        g_micTest->RequestStop();
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


    // Companion acquisition is isolated from all monitor capture backends.
    __declspec(dllexport) int qc_start_companion(const QcConfig* config, const QcEndpointTrack* endpoints,
        int count, const QcVideoOptions* video, const QcGameWindow* windows, int windowCount,
        wchar_t* error, int errorLength)
    {
        if (!config || !video || !windows || windowCount < 1 || windowCount > 4 ||
            count < -1 || count > 16 || (count > 0 && !endpoints) ||
            video->nvencPreset < 0 || video->nvencPreset > 2 ||
            config->outWidth < 64 || config->outWidth > 4096 ||
            config->outHeight < 64 || config->outHeight > 4096 ||
            (config->outWidth & 7) || (config->outHeight & 1))
        {
            CopyError("Invalid companion capture configuration.", error, errorLength);
            return 0;
        }
        for (int i = 0; i < count; ++i)
        {
            if ((endpoints[i].flow != 0 && endpoints[i].flow != 1) ||
                !endpoints[i].deviceId || !*endpoints[i].deviceId ||
                !endpoints[i].title || !*endpoints[i].title)
            {
                CopyError("Invalid companion audio endpoint.", error, errorLength);
                return 0;
            }
            for (int j = 0; j < i; ++j)
                if (_wcsicmp(endpoints[i].deviceId, endpoints[j].deviceId) == 0)
                {
                    CopyError("Duplicate companion audio endpoint.", error, errorLength);
                    return 0;
                }
        }
        for (int i = 0; i < windowCount; ++i)
        {
            const auto& w = windows[i];
            if (!w.handle || !w.thread || !w.process || w.started <= 0)
            {
                CopyError("Companion window identity is missing.", error, errorLength);
                return 0;
            }
            for (int j = 0; j < i; ++j)
                if (windows[j].handle == w.handle)
                {
                    CopyError("Duplicate companion window.", error, errorLength);
                    return 0;
                }
        }
        return StartEngine(*config, count >= 0, endpoints, std::max(0, count),
            error, errorLength, *video, nullptr, nullptr, windows, windowCount);
    }

    __declspec(dllexport) int qc_update_companion(const QcGameWindow* windows, int count)
    {
        if (!windows || count < 1 || count > 4) return 0;
        std::shared_lock guard(g_engineLock);
        return g_engine && g_engine->UpdateCompanionWindows(windows, count) ? 1 : 0;
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

    // Synthetic-only capacity fixture. No desktop, encoder, audio endpoint or media file.
    // Exercises the actual packet-ring pruning path with nonzero encoded packet sizes.
    __declspec(dllexport) int qc_test_replay_ring_budget(int frames, int intervalUs, int keyInterval,
        int packetBytes, int64_t byteBudget, int64_t* retainedBytes, int64_t* firstKeyUs,
        int* retainedVideoPackets, int* retainedAudioPackets)
    {
        if (frames < 1 || frames > 1024 || intervalUs < 1 || keyInterval < 1 ||
            packetBytes < 1 || packetBytes > 65536 || byteBudget < 0 ||
            !retainedBytes || !firstKeyUs || !retainedVideoPackets || !retainedAudioPackets)
            return 0;
        qc::PacketRing ring(static_cast<int64_t>(frames) * intervalUs + intervalUs, byteBudget);
        for (int frame = 0; frame < frames; frame++)
        {
            for (int stream = 0; stream < 2; stream++)
            {
                auto* packet = av_packet_alloc();
                if (!packet) return 0;
                if (av_new_packet(packet, packetBytes) < 0)
                {
                    av_packet_free(&packet);
                    return 0;
                }
                ring.Push(packet, stream, static_cast<int64_t>(frame) * intervalUs,
                    stream == 0 && frame % keyInterval == 0, 1);
            }
        }
        double seconds;
        ring.Stats(*retainedBytes, seconds);
        *retainedVideoPackets = *retainedAudioPackets = 0;
        *firstKeyUs = -1;
        auto selected = ring.Snapshot(0, static_cast<int64_t>(frames - 1) * intervalUs, *firstKeyUs);
        for (auto& entry : selected)
        {
            if (entry.stream == 0) ++*retainedVideoPackets;
            if (entry.stream == 1) ++*retainedAudioPackets;
            av_packet_free(&entry.packet);
        }
        return 1;
    }
}


extern "C"
{
    // Native epoch-archive fixture: verifies that packet refs/codec parameters survive
    // destruction of each graph-owned ring. Never opens capture or alters global Engine.
    __declspec(dllexport) int qc_test_detached_epoch_archive(
        int scenario, int* retainedEpochs, int64_t* retainedBytes,
        int64_t* firstUs, int64_t* lastUs, int* retainedStreams)
    {
        if (scenario < 0 || scenario > 5 || !retainedEpochs || !retainedBytes ||
            !firstUs || !lastUs || !retainedStreams) return 0;

        struct Codecs
        {
            AVCodecParameters* video = avcodec_parameters_alloc();
            AVCodecParameters* audio = avcodec_parameters_alloc();
            ~Codecs()
            {
                avcodec_parameters_free(&video);
                avcodec_parameters_free(&audio);
            }
        } codecs;
        if (!codecs.video || !codecs.audio) return 0;
        codecs.video->codec_type = AVMEDIA_TYPE_VIDEO;
        codecs.video->codec_id = AV_CODEC_ID_H264;
        codecs.video->width = 640;
        codecs.video->height = 360;
        codecs.audio->codec_type = AVMEDIA_TYPE_AUDIO;
        codecs.audio->codec_id = AV_CODEC_ID_AAC;
        codecs.audio->sample_rate = 48000;
        std::vector<AVCodecParameters*> parameters{ codecs.video, codecs.audio };
        std::vector<AVRational> bases{ { 1, 30 }, { 1, 48000 } };

        const int64_t budget = scenario == 2 ? 450 : 2'048;
        qc::DetachedEpochArchive archive(budget, 15'000'000);
        auto add = [&](int epochNumber, int frames, int64_t graphStart, std::string scope, uint64_t session)
        {
            qc::PacketRing oldGraph(10'000'000);
            for (int frame = 0; frame < frames; ++frame)
                for (int stream = 0; stream < 2; ++stream)
                {
                    auto* packet = av_packet_alloc();
                    if (!packet) return false;
                    if (av_new_packet(packet, 64) < 0)
                    {
                        av_packet_free(&packet);
                        return false;
                    }
                    memset(packet->data, epochNumber * 16 + frame + stream, 64);
                    oldGraph.Push(packet, stream, static_cast<int64_t>(frame) * 1'000'000,
                        stream == 0 && frame == 0, epochNumber);
                }
            int64_t firstKey = -1;
            auto snapshots = oldGraph.Snapshot(0, static_cast<int64_t>(frames - 1) * 1'000'000, firstKey);
            qc::DetachedEpochArchive::Header header{ session, static_cast<uint64_t>(epochNumber),
                std::move(scope), static_cast<uint64_t>(scenario == 5 && epochNumber == 2 ? 12 : 11), graphStart };
            bool added = archive.Append(header, snapshots, parameters, bases);
            for (auto& entry : snapshots) av_packet_free(&entry.packet);
            // oldGraph is destroyed when this closure returns; the archive must own clones.
            return added;
        };

        if (!add(1, 3, 1'000'000, "verified-game:synthetic", 42)) return 0;
        bool expectedSecond = scenario == 0 || scenario == 2;
        bool second = add(2, 2, scenario == 3 ? 2'000'000 : 5'000'000,
            scenario == 1 ? "different-game" : "verified-game:synthetic",
            scenario == 4 ? 43 : 42);
        if (second != expectedSecond) return 0;

        const auto* earliest = archive.At(0);
        const auto* latest = archive.At(archive.Count() - 1);
        if (!earliest || !latest || earliest->packets.empty() ||
            earliest->packets.front().packet == nullptr ||
            earliest->packets.front().packet->data == nullptr ||
            earliest->codecs.size() != 2 || latest->codecs.size() != 2 ||
            earliest->timeBases.size() != 2 ||
            earliest->codecs[0]->codec_id != AV_CODEC_ID_H264 ||
            earliest->codecs[1]->codec_id != AV_CODEC_ID_AAC) return 0;
        if (earliest->packets.front().packet->data[0] !=
            static_cast<uint8_t>((scenario == 2 ? 2 : 1) * 16)) return 0;

        *retainedEpochs = static_cast<int>(archive.Count());
        *retainedBytes = archive.Bytes();
        *firstUs = earliest->startUs;
        *lastUs = latest->endUs;
        *retainedStreams = static_cast<int>(latest->codecs.size());
        return 1;
    }
}

// Test-only native data path: demux separately encoded two-stem fixtures,
// hand packet references to two ephemeral graph-owned rings, detach each one,
// destroy both graphs/decoders, then remux independent MKV epoch files.
// No QuickClip app engine, display, microphone or game is started.
extern "C" __declspec(dllexport) int qc_test_detached_epoch_media_roundtrip(
    const wchar_t* sourceA, const wchar_t* sourceB,
    const wchar_t* outputA, const wchar_t* outputB, wchar_t* error, int errorLength)
{
    if (!sourceA || !sourceB || !outputA || !outputB || !error || errorLength < 2)
        return 0;
    qc::DetachedEpochArchive archive(64ll * 1024 * 1024, 10'000'000);
    std::string failure;
    auto detach = [&](const wchar_t* source, uint64_t epochId, int64_t globalStartUs) -> bool
    {
        AVFormatContext* input = nullptr;
        int result = avformat_open_input(&input, qc::Narrow(source).c_str(), nullptr, nullptr);
        if (result < 0)
        {
            failure = "Could not open encoded epoch input: " + qc::AvError(result);
            return false;
        }
        // Stream codec parameters and packets are valid only until the input closes.
        struct InputLifetime
        {
            AVFormatContext** input;
            ~InputLifetime() { avformat_close_input(input); }
        } guard{ &input };
        result = avformat_find_stream_info(input, nullptr);
        if (result < 0 || input->nb_streams < 1 || input->nb_streams > 17 ||
            input->streams[0]->codecpar->codec_type != AVMEDIA_TYPE_VIDEO)
        {
            failure = "Input epoch stream layout is invalid.";
            return false;
        }
        std::vector<AVCodecParameters*> parameters;
        std::vector<AVRational> bases;
        std::vector<std::string> titles;
        for (unsigned i = 0; i < input->nb_streams; ++i)
        {
            const auto* stream = input->streams[i];
            if (stream->codecpar->codec_type != (i == 0 ? AVMEDIA_TYPE_VIDEO : AVMEDIA_TYPE_AUDIO))
            {
                failure = "Input must contain video followed by audio stems.";
                return false;
            }
            parameters.push_back(stream->codecpar);
            bases.push_back(stream->time_base);
            auto* title = av_dict_get(stream->metadata, "title", nullptr, 0);
            titles.push_back(title && title->value ? title->value :
                (i == 0 ? "Video" : "Audio " + std::to_string(i)));
        }

        std::vector<qc::PacketRing::Entry> pending;
        struct PacketLifetime
        {
            std::vector<qc::PacketRing::Entry>& entries;
            ~PacketLifetime() { for (auto& entry : entries) av_packet_free(&entry.packet); }
        } packetGuard{pending};
        AVPacket* inputPacket = av_packet_alloc();
        if (!inputPacket) { failure = "Out of memory for archive packet."; return false; }
        int64_t sequence = 0;
        int64_t totalBytes = 0;
        while (av_read_frame(input, inputPacket) >= 0)
        {
            const int stream = inputPacket->stream_index;
            const int64_t pts = inputPacket->pts != AV_NOPTS_VALUE ?
                inputPacket->pts : inputPacket->dts;
            if (stream < 0 || static_cast<size_t>(stream) >= parameters.size() ||
                pts == AV_NOPTS_VALUE || ++sequence > 1024)
            {
                av_packet_free(&inputPacket);
                failure = "Encoded fixture exceeds packet budget or contains invalid timestamps.";
                return false;
            }
            auto* clone = av_packet_clone(inputPacket);
            if (!clone)
            {
                av_packet_free(&inputPacket);
                failure = "Could not copy encoded packet.";
                return false;
            }
            totalBytes += clone->size;
            if (totalBytes > 32ll * 1024 * 1024)
            {
                av_packet_free(&clone);
                av_packet_free(&inputPacket);
                failure = "Encoded fixture exceeds memory quota.";
                return false;
            }
            pending.push_back({clone, stream,
                av_rescale_q(pts, bases[stream], AVRational{1, 1000000}),
                stream == 0 && (inputPacket->flags & AV_PKT_FLAG_KEY) != 0,
                sequence, static_cast<int64_t>(epochId)});
            av_packet_unref(inputPacket);
        }
        av_packet_free(&inputPacket);
        if (pending.empty()) { failure = "Encoded fixture has no packets."; return false; }
        const int64_t originUs = std::min_element(pending.begin(), pending.end(),
            [](const auto& a, const auto& b) { return a.timeUs < b.timeUs; })->timeUs;
        for (auto& entry : pending)
        {
            entry.timeUs -= originUs;
            const int64_t start = av_rescale_q(originUs, AVRational{1, 1000000}, bases[entry.stream]);
            if (entry.packet->pts != AV_NOPTS_VALUE) entry.packet->pts -= start;
            if (entry.packet->dts != AV_NOPTS_VALUE) entry.packet->dts -= start;
        }
        std::stable_sort(pending.begin(), pending.end(), [](const auto& a, const auto& b) {
            return a.timeUs < b.timeUs;
        });
        const int64_t latestUs = pending.back().timeUs;
        if (latestUs >= 5'000'000) { failure = "Fixture duration exceeds 5 seconds."; return false; }
        std::vector<qc::PacketRing::Entry> snapshot;
        {
            qc::PacketRing graph(10'000'000);
            for (auto& entry : pending)
            {
                graph.Push(entry.packet, entry.stream, entry.timeUs, entry.key, entry.source);
                entry.packet = nullptr; // Transferred ownership to the short-lived graph.
            }
            int64_t firstKeyUs = -1;
            snapshot = graph.Snapshot(0, latestUs + 1000, firstKeyUs);
            if (snapshot.empty())
            {
                failure = "No decodable initial keyframe in encoded input.";
                return false;
            }
            qc::DetachedEpochArchive::Header header{42, epochId,
                "verified-game:synthetic", 1, globalStartUs};
            bool accepted = archive.Append(header, snapshot, parameters, bases, titles);
            for (auto& entry : snapshot) av_packet_free(&entry.packet);
            if (!accepted)
            {
                failure = "Failed to detach encoded epoch from its graph.";
                return false;
            }
        } // Original graph's PacketRing and its AVPackets are now destroyed.
        return true;
    };

    if (!detach(sourceA, 1, 0) || !detach(sourceB, 2, 3'000'000) ||
        !archive.WriteSegment(0, outputA, failure) ||
        !archive.WriteSegment(1, outputB, failure))
    {
        CopyError(failure.empty() ? "Detached epoch media fixture failed." : failure, error, errorLength);
        return 0;
    }
    return 1;
}

// Exercises zero-copy ownership transfer and rejection under an actual PacketRing lock.
// Isolated from the global capture engine and all real display/audio devices.
extern "C" __declspec(dllexport) int qc_test_epoch_packet_transfer()
{
    struct Codecs
    {
        AVCodecParameters* video = avcodec_parameters_alloc();
        AVCodecParameters* audio = avcodec_parameters_alloc();
        ~Codecs()
        {
            avcodec_parameters_free(&video);
            avcodec_parameters_free(&audio);
        }
    } codecs;
    if (!codecs.video || !codecs.audio) return 0;
    codecs.video->codec_type = AVMEDIA_TYPE_VIDEO;
    codecs.video->codec_id = AV_CODEC_ID_H264;
    codecs.video->width = 640;
    codecs.video->height = 360;
    codecs.audio->codec_type = AVMEDIA_TYPE_AUDIO;
    codecs.audio->codec_id = AV_CODEC_ID_AAC;
    codecs.audio->sample_rate = 48000;
    std::vector<AVCodecParameters*> parameters{ codecs.video, codecs.audio };
    std::vector<AVRational> bases{ { 1, 30 }, { 1, 48000 } };
    struct Owned
    {
        std::vector<qc::PacketRing::Entry> packets;
        ~Owned() { for (auto& entry : packets) av_packet_free(&entry.packet); }
    };
    const auto insert = [](qc::PacketRing& ring, int stream) -> AVPacket* {
        auto* packet = av_packet_alloc();
        if (!packet) return nullptr;
        if (av_new_packet(packet, 64) < 0)
        {
            av_packet_free(&packet);
            return nullptr;
        }
        // Delayed audio packets can arrive after video with earlier timestamps.
        ring.Push(packet, stream, stream == 0 ? 1'000'000 : 0, stream == 0, 1);
        return packet;
    };
    const auto drain = [](qc::PacketRing& ring, Owned& owner) {
        owner.packets = ring.Drain();
        int64_t bytes = -1;
        double seconds = -1;
        ring.Stats(bytes, seconds);
        return bytes == 0 && seconds == 0 && owner.packets.size() == 2;
    };

    qc::DetachedEpochArchive archive(192, 10'000'000);
    qc::DetachedEpochArchive::Header firstHeader{ 42, 1, "verified-game:synthetic", 11, 1'000'000 };
    AVPacket* firstVideo = nullptr;
    Owned first;
    {
        qc::PacketRing graph(10'000'000, 192);
        firstVideo = insert(graph, 0);
        if (!firstVideo || !insert(graph, 1) || !drain(graph, first)) return 0;
    } // Graph was destroyed before archive adoption.
    if (!archive.AppendOwned(firstHeader, first.packets, parameters, bases) ||
        archive.Count() != 1 || archive.Bytes() != 128 ||
        archive.At(0)->packets[0].packet != firstVideo ||
        first.packets[0].packet || first.packets[1].packet)
        return 0;

    qc::DetachedEpochArchive::Header nextHeader{ 42, 2, "verified-game:synthetic", 12, 5'000'000 };
    AVPacket* nextVideo = nullptr;
    Owned next;
    {
        qc::PacketRing graph(10'000'000, 192);
        nextVideo = insert(graph, 0);
        if (!nextVideo || !insert(graph, 1) || !drain(graph, next)) return 0;
    }
    // A stale or changed grant must fail without consuming the caller's packets.
    if (archive.AppendOwned(nextHeader, next.packets, parameters, bases) ||
        next.packets[0].packet != nextVideo ||
        archive.Count() != 1 || archive.Bytes() != 128) return 0;
    nextHeader.grantRevision = 11;
    if (!archive.AppendOwned(nextHeader, next.packets, parameters, bases) ||
        next.packets[0].packet || next.packets[1].packet ||
        archive.Count() != 1 || archive.Bytes() != 128 ||
        archive.At(0)->packets[0].packet != nextVideo)
        return 0; // Old epoch evicted before adopting the new 128-byte batch.

    // Active packets win over older archived history as the ring fills.
    qc::PacketRing active(10'000'000, 192);
    active.SetMemoryPressureHandler([&archive](int64_t liveBytes) {
        archive.TrimToBytes(192 - liveBytes);
    });
    if (!insert(active, 0) || archive.Count() != 1) return 0; // 64 + 128 = 192.
    if (!insert(active, 1) || archive.Count() != 0) return 0; // 128 + 128 exceeds limit.
    if (!insert(active, 0) || !insert(active, 1)) return 0;
    int64_t activeBytes = 0;
    double activeSeconds = 0;
    active.Stats(activeBytes, activeSeconds);
    if (activeBytes + archive.Bytes() > 192 || activeBytes != 192) return 0;
    return 1;
}

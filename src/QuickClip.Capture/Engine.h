// Replay-buffer recorder: GPU screen capture + encode, audio tracks, packets kept in RAM, saved on demand.
#pragma once

#include "Audio.h"
#include <deque>
#include <map>
#include <memory>
#include <shared_mutex>

extern "C"
{
    // Layouts are mirrored in QuickClip/Recording/CaptureEngine.cs.
    struct QcConfig
    {
        const wchar_t* monitorDevice;   // e.g. \\.\DISPLAY1
        int fps;
        int outWidth;                   // 0 = the recorded area's native size
        int outHeight;
        int aspectWidth, aspectHeight;  // record only the centered part with this shape (0 = the whole monitor)
        const char* encoder;            // FFmpeg encoder name, e.g. av1_amf
        int videoKbps;
        int audioKbps;
        int bufferSeconds;
        int captureCursor;
        int splitChat;                  // separate Chat track
        int splitMusic;                 // separate Music track
        int micCount;                   // microphones to record (0 = none)
        const wchar_t* const* micDeviceIds; // one per microphone, null/empty for the default microphone
        const wchar_t* const* micTitles;    // one per microphone, its track's title when each has its own
        int micSeparate;                // each microphone on its own track instead of all on one Mic track
    };

    struct QcStats
    {
        int running;
        int width;
        int height;
        double fps;
        int64_t bufferBytes;
        double bufferSeconds;
        int64_t droppedFrames;
    };

    struct QcVideoOptions
    {
        int backend; // 0 = legacy Auto, 1 = DDA, 2 = WGC, 3 = monitor-only DDA, 4 = monitor-only WGC
        int nvencPreset; // 0 = p4, 1 = p3, 2 = p2
        int diagnostics;
    };

    struct QcGameWindow
    {
        HWND handle;
        DWORD thread, process;
        int64_t started;
    };

    struct QcReplayPosition
    {
        int64_t session, eventUs, frameUs, sequence, firstSequence, source;
    };

    struct QcReplaySpan
    {
        int64_t firstUs, lastUs, availableUs, durationUs, firstSource, lastSource;
    };

    struct QcReplayAnchor
    {
        int64_t sequence, offsetUs;
    };

    struct QcReplayVideo
    {
        int width, height, fpsNumerator, fpsDenominator;
    };

    struct QcEndpointStatus
    {
        int flow, stage, error, mixRate, mixChannels, captureRate;
        int64_t packets, millisecondsSincePacket;
        float lastPacketPeak;
    };

    struct QcOutput
    {
        int adapter;
        int output;
        int left, top, right, bottom;
        wchar_t deviceName[32];
        wchar_t adapterName[128];
    };

    struct QcAudioDevice
    {
        wchar_t id[256];
        wchar_t name[256];
        int isDefault;
    };

    struct QcEndpointTrack
    {
        int flow;                       // eRender or eCapture
        const wchar_t* deviceId;
        const wchar_t* title;
    };

    using QcEndpointCallback = void(__stdcall*)(int flow, const wchar_t* id, const wchar_t* name, int isDefault);
}

namespace qc
{
    std::string DisplayDiagnostics();

    // The default listening mix has its own encoder. Keep every raw stem intact.
    // Deterministic soft knee, unlike a hard clamp which creates its own crackle.
    float SoftLimitPlaybackSample(float value);
    /// Encoded packets of all streams for the last N seconds.
    class PacketRing
    {
    public:
        struct Entry
        {
            AVPacket* packet;
            int stream;
            int64_t timeUs;
            bool key;
            int64_t sequence;
            int64_t source;
        };

        explicit PacketRing(int64_t windowUs) : windowUs_(windowUs) {}
        ~PacketRing() { Clear(); }

        void Push(AVPacket* packet, int stream, int64_t timeUs, bool key, int64_t source = 0);
        /// New packet references, aligned to a keyframe. Bounded requests round the start forward.
        std::vector<Entry> Snapshot(int64_t fromUs, int64_t toUs, int64_t& startUs,
            QcReplaySpan* span = nullptr, QcReplayAnchor* anchors = nullptr, int count = 0, int64_t boundedFrameUs = 0);
        void Position(QcReplayPosition& position);
        void Stats(int64_t& bytes, double& seconds);
        void Clear();

    private:
        std::mutex lock_;
        std::deque<Entry> entries_;
        int64_t bytes_ = 0;
        int64_t windowUs_;
        int64_t newestVideoUs_ = -1;
        int64_t sequence_ = 0; // keep increasing even when incompatible video clears the ring
    };

    /// Codec parameters of one output stream, captured once its encoder is open.
    struct StreamInfo
    {
        AVCodecParameters* params = nullptr;
        AVRational timeBase{ 1, 1 };
        std::string title;
        ~StreamInfo() { avcodec_parameters_free(&params); }
    };

    class Engine
    {
    public:
        Engine(const QcConfig& config, bool endpointMode, const QcEndpointTrack* endpoints, int endpointCount,
            QcVideoOptions video, const wchar_t* displayIdentity, const QcGameWindow* game);
        ~Engine();
        bool Start(std::string& error);
        /// Desktop records everything but `desktopExclude`'s process tree (nothing when that is 0) plus the trees of
        /// `desktopInclude`, less the sound of `desktopSubtract`'s trees (some of the Chat and Music processes), taken
        /// out sample for sample. Chat and Music record their processes' trees.
        void SetAudioProcesses(DWORD desktopExclude, const std::vector<DWORD>& desktopInclude,
            const std::vector<DWORD>& chat, const std::vector<DWORD>& music, const std::vector<DWORD>& desktopSubtract);
        bool Save(const std::wstring& path, int seconds, const std::wstring& title, std::string& error,
            QcReplaySpan* span = nullptr, QcReplayAnchor* anchors = nullptr, int count = 0, int64_t requestUs = -1,
            QcReplayVideo* savedVideo = nullptr);
        void GetReplayPosition(QcReplayPosition& position);
        void GetStats(QcStats& stats);
        int GetEndpointStatus(QcEndpointStatus* statuses, int capacity) const;
        int GetAudioSignals(QcAudioSignal* signals, int capacity) const;
        std::string VideoDiagnostics();
        /// Starts capture again on the next frame (the display changed; the old capture may be stale).
        void RequestRebuild();
        bool VideoSourceLost() const;
        bool HasVideoOutput() const;
        static bool TestAuthorizedVideoInvalidation(bool stall);
        /// Records `window` (a game's) instead of the monitor, at this size and bitrate; null goes back to the monitor.
        void SetCaptureWindow(HWND window, int width, int height, int videoKbps);
        /// Tests: makes the capture thread hang for this long, as a stuck capture call would.
        void TestFreezeVideo(int ms);
        /// Tests: counts keyframes smaller than this as a flat picture (0 = the normal limit).
        void TestFlatBelow(int bytes);
        /// Tests: where a subtracted process's sound was found in Desktop's capture, or INT64_MIN while it isn't.
        int64_t TestSubtractOffset(DWORD pid) { return canceller_ ? canceller_->OffsetOf(pid) : INT64_MIN; }

        struct Shared;

    private:
        void AudioLoop();
        void WatchLoop();
        void RestartVideo();

        std::shared_ptr<Shared> shared_;
        std::wstring monitor_;
        bool splitChat_, splitMusic_, micSeparate_, endpointMode_;
        int audioKbps_;

        struct Endpoint
        {
            SourceKind kind;
            std::wstring deviceId;      // empty = the default microphone
            std::string title;
        };
        std::vector<Endpoint> mics_, endpoints_;

        // Audio tracks are Desktop, then Chat and Music when enabled, then one Mic track or one per microphone.
        struct Track
        {
            std::string title;
            std::unique_ptr<TrackMixer> mixer;
            AudioEncoder encoder;
            std::atomic<int64_t> encodedPackets{ 0 };
        };
        std::vector<std::unique_ptr<Track>> tracks_;
        AudioEncoder playbackMixEncoder_;
        bool hybridAudio_ = false;
        int chatTrack_ = -1, musicTrack_ = -1;

        // App capture streams by what they record. When the apps change, only the streams that differ are
        // stopped or started, so the other tracks keep recording without a gap.
        struct SourceKey
        {
            SourceKind kind;
            DWORD pid;
            int track;
            bool subtract;              // also goes to Desktop's subtraction (everything, or an app taken out of it)
            auto operator<=>(const SourceKey&) const = default;
        };
        std::mutex sourcesLock_;
        // Taking apps out of Desktop works at the output's own rate, where their sound is identical in every capture.
        int nativeRate_ = SampleRate;
        std::unique_ptr<Canceller> canceller_;
        std::map<SourceKey, std::unique_ptr<AudioSource>> sources_;
        std::vector<std::unique_ptr<AudioSource>> endpointSources_;

        std::mutex videoRestartLock_;
        std::thread videoThread_, audioThread_, watchThread_;
    };

    int EnumOutputs(QcOutput* outputs, int max);
    int EnumMicrophones(QcAudioDevice* devices, int max);
    void EnumAudioEndpoints(QcEndpointCallback callback);
}

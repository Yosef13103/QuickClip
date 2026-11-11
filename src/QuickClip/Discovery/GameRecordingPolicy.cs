using System.Collections.Immutable;
using QuickClip.Recording;

namespace QuickClip.Discovery;

internal enum GameCaptureSource { GameWindow, GameMonitor, ExplicitMonitor }
internal enum GameCaptureFallback { GameMonitor, GlobalSource, SkipRecording }
internal enum GameRecordingCodec { Automatic, H264, Hevc, Av1 }
internal enum GameRateControl { Automatic, CustomBitrate, Cbr, Vbr, Cqp }

internal sealed record GameCaptureTarget(GameCaptureSource Source, string? MonitorId = null);
internal sealed record GameBitrate(GameRateControl Mode, int? Kbps = null, int? Quantizer = null);
internal sealed record AudioEndpointReference(EndpointFlow Flow, string DeviceId);
internal sealed record GameSaveShortcut(bool Enabled, int Modifiers = 0, int VirtualKey = 0);

/// <summary>Null inherits. An empty selection or label explicitly clears the global value.</summary>
internal sealed record GameRecordingPolicy
{
    public GameCaptureTarget? Target { get; init; }
    public GameCaptureFallback? Fallback { get; init; }
    public int? ReplaySeconds { get; init; }
    public int? SaveSeconds { get; init; }
    public int? ResolutionShortSide { get; init; }
    public int? Fps { get; init; }
    public string? Aspect { get; init; }
    public GameRecordingCodec? Codec { get; init; }
    public RecordingQuality? Quality { get; init; }
    public CaptureEngine.NvencSpeed? NvencPreset { get; init; }
    public GameBitrate? Bitrate { get; init; }
    public ImmutableArray<AudioEndpointReference>? AudioEndpoints { get; init; }
    public ImmutableArray<string>? MicrophoneIds { get; init; }
    public bool? OpenGalleryOnSessionEnd { get; init; }
    public bool? FullSessionRecording { get; init; }
    public ImmutableArray<string>? Tags { get; init; }
    public string? DefaultLabel { get; init; }
    public GameSaveShortcut? SaveShortcut { get; init; }
    public bool? BurstSave { get; init; }

    public GameRecordingPolicy ResolveAgainst(GameRecordingPolicy defaults)
    {
        Validate();
        defaults.Validate();
        var effective = new GameRecordingPolicy
        {
            Target = Target ?? defaults.Target,
            Fallback = Fallback ?? defaults.Fallback,
            ReplaySeconds = ReplaySeconds ?? defaults.ReplaySeconds,
            SaveSeconds = SaveSeconds ?? defaults.SaveSeconds,
            ResolutionShortSide = ResolutionShortSide ?? defaults.ResolutionShortSide,
            Fps = Fps ?? defaults.Fps,
            Aspect = Aspect ?? defaults.Aspect,
            Codec = Codec ?? defaults.Codec,
            Quality = Quality ?? defaults.Quality,
            NvencPreset = NvencPreset ?? defaults.NvencPreset,
            Bitrate = Bitrate ?? defaults.Bitrate,
            AudioEndpoints = AudioEndpoints ?? defaults.AudioEndpoints,
            MicrophoneIds = MicrophoneIds ?? defaults.MicrophoneIds,
            OpenGalleryOnSessionEnd = OpenGalleryOnSessionEnd ?? defaults.OpenGalleryOnSessionEnd ?? false,
            FullSessionRecording = FullSessionRecording ?? defaults.FullSessionRecording ?? false,
            Tags = Tags ?? defaults.Tags,
            DefaultLabel = DefaultLabel ?? defaults.DefaultLabel,
            SaveShortcut = SaveShortcut ?? defaults.SaveShortcut,
            BurstSave = BurstSave ?? defaults.BurstSave ?? false
        };
        effective.Validate();
        return effective;
    }

    public void Validate()
    {
        Require(Target == null || (Enum.IsDefined(Target.Source) &&
            (Target.Source == GameCaptureSource.ExplicitMonitor ? IsId(Target.MonitorId) : Target.MonitorId == null)), nameof(Target));
        Require(Defined(Fallback), nameof(Fallback));
        Require(ReplaySeconds is null or (>= 10 and <= 300), nameof(ReplaySeconds));
        Require(SaveSeconds is null or (>= 1 and <= 300) && !(SaveSeconds > ReplaySeconds), nameof(SaveSeconds));
        Require(ResolutionShortSide == null || RecordingPresets.Resolutions.Contains(ResolutionShortSide.Value), nameof(ResolutionShortSide));
        Require(Fps == null || RecordingPresets.FrameRates.Contains(Fps.Value), nameof(Fps));
        Require(Aspect == null || RecordingPresets.Aspects.Contains(Aspect), nameof(Aspect));
        Require(Defined(Codec), nameof(Codec));
        Require(Defined(Quality), nameof(Quality));
        Require(Defined(NvencPreset), nameof(NvencPreset));
        Require(Bitrate == null || IsValidBitrate(Bitrate), nameof(Bitrate));
        Require(AudioEndpoints == null || ValidEndpoints(AudioEndpoints.Value), nameof(AudioEndpoints));
        Require(MicrophoneIds == null || ValidList(MicrophoneIds.Value, EndpointAudio.MaxTracks,
            id => id == "" || IsId(id)), nameof(MicrophoneIds));
        Require(Tags == null || ValidList(Tags.Value, 32, tag => IsText(tag, 64, allowEmpty: false)), nameof(Tags));
        Require(DefaultLabel == null || IsText(DefaultLabel, 128, allowEmpty: true), nameof(DefaultLabel));
        Require(SaveShortcut == null || (SaveShortcut.Enabled
            ? (SaveShortcut.Modifiers & ~0xF) == 0 && SaveShortcut.VirtualKey is > 0 and < 255
            : SaveShortcut.Modifiers == 0 && SaveShortcut.VirtualKey == 0), nameof(SaveShortcut));
    }

    private static bool IsValidBitrate(GameBitrate value) => value.Mode switch
    {
        GameRateControl.Automatic => value.Kbps == null && value.Quantizer == null,
        GameRateControl.CustomBitrate or GameRateControl.Cbr or GameRateControl.Vbr =>
            value.Kbps is >= 1500 and <= 250_000 && value.Quantizer == null,
        GameRateControl.Cqp => value.Kbps == null && value.Quantizer is >= 0 and <= 63,
        _ => false
    };

    private static bool ValidEndpoints(ImmutableArray<AudioEndpointReference> entries) =>
        !entries.IsDefault && entries.Length <= EndpointAudio.MaxTracks &&
        entries.All(entry => entry != null && Enum.IsDefined(entry.Flow) && IsId(entry.DeviceId)) &&
        entries.Select(entry => entry.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Length;

    private static bool ValidList(ImmutableArray<string> items, int limit, Func<string, bool> valid) =>
        !items.IsDefault && items.Length <= limit && items.All(valid) &&
        items.Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Length;

    private static bool Defined<T>(T? value) where T : struct, Enum => value == null || Enum.IsDefined(value.Value);

    internal static bool IsId(string? value, int maxLength = 512) => IsText(value, maxLength, allowEmpty: false);

    private static bool IsText(string? value, int maxLength, bool allowEmpty) =>
        value != null && value.Length <= maxLength && !value.Any(char.IsControl) &&
        (value.Length == 0 ? allowEmpty : !string.IsNullOrWhiteSpace(value) && value == value.Trim());

    private static void Require(bool valid, string field)
    {
        if (!valid) throw new ArgumentException($"Invalid recording policy value for {field}.", field);
    }
}

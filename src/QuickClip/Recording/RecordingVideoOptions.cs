using QuickClip.Discovery;
using QuickClip.Media;

namespace QuickClip.Recording;

/// <summary>Immutable video settings for one capture graph; never changes the
/// live AppSettings or grants a capture source. Manual desktop stays global.</summary>
internal sealed record RecordingVideoOptions(string Aspect, int Resolution, int Fps,
    RecordingQuality Quality, string EncoderId, GameRecordingCodec? Codec,
    CaptureEngine.NvencSpeed NvencPreset, int ReplaySeconds, int? TargetKbps)
{
    internal static RecordingVideoOptions Resolve(AppSettings settings, GameRecordingPolicy? policy = null)
    {
        policy?.Validate();
        // The existing encoder accepts a target bitrate, not arbitrary CQP,
        // CBR or VBR control modes. Do not silently reinterpret draft fields.
        int? kbps = policy?.Bitrate?.Mode switch
        {
            null or GameRateControl.Automatic => null,
            GameRateControl.CustomBitrate => policy.Bitrate.Kbps,
            _ => throw new ArgumentException("This profile uses an unsupported recording rate control. Select Auto or target bitrate."),
        };
        return new(policy?.Aspect ?? settings.RecordAspect,
            policy?.ResolutionShortSide ?? settings.RecordResolution,
            policy?.Fps ?? settings.RecordFps, policy?.Quality ?? settings.RecordQuality,
            policy?.EncoderId ?? (policy?.Codec != null ? "" : settings.RecordEncoder),
            policy?.Codec, policy?.NvencPreset ?? settings.NvencPreset,
            policy?.ReplaySeconds ?? settings.BufferSeconds, kbps);
    }

    internal string? SelectEncoder(IReadOnlySet<string> available, bool exactOverride)
    {
        if (EncoderId.Length > 0)
        {
            if (RecordingPresets.RecordingEncoderOrder.Contains(EncoderId) && available.Contains(EncoderId))
                return EncoderId;
            if (exactOverride) return null;
        }
        if (Codec is GameRecordingCodec.H264 or GameRecordingCodec.Hevc or GameRecordingCodec.Av1)
        {
            string family = Codec.Value.ToString().ToLowerInvariant();
            return RecordingPresets.RecordingEncoderOrder.FirstOrDefault(id =>
                available.Contains(id) && VideoEncoders.Find(id)?.Family == family);
        }
        return RecordingPresets.DefaultEncoder(available)?.Id;
    }

    internal static GameRecordingPolicy? FindPolicy(string? gameId, IEnumerable<CatalogEntry> entries, GameProfileDocument document)
    {
        if (gameId == null) return null;
        var catalog = entries.ToArray();
        var owners = catalog.Where(entry => entry.Id == gameId || entry.AliasIds.Contains(gameId)).ToArray();
        if (owners.Length != 1) throw new InvalidOperationException("The recording game profile identity is ambiguous or unavailable.");
        var row = RecordingProfileSelection.Build(catalog, document).SingleOrDefault(r => r.GameId == owners[0].Id);
        if (row is not { Enabled: true }) throw new InvalidOperationException("The recording game profile has conflicting identities.");
        return row.Policy;
    }

    internal static GameRecordingPolicy? LoadPolicy(string? gameId, AppSettings settings)
    {
        if (gameId == null) return null;
        var loaded = GameProfileStore.ForCurrentProfile().Load();
        if (loaded.Status == GameProfileLoadStatus.Missing) return null;
        if (loaded.Status is not (GameProfileLoadStatus.Loaded or GameProfileLoadStatus.Migrated))
            throw new InvalidOperationException("Recording profiles are invalid or unavailable. Repair the profile file before recording this game.");
        return FindPolicy(gameId, GameCatalogRuntime.Rows(settings.Games), loaded.Document);
    }
}

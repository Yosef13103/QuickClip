using System.Text.Json;
using System.Text.Json.Serialization;
using QuickClip.Recording;
using QuickClip.Discovery;

namespace QuickClip;

/// <summary>User preferences persisted in the active application profile.</summary>
internal sealed class AppSettings
{
    public GameCatalogSettings Games { get; set; } = new()
    {
        DiscoverGog = AppProfile.IsPersonal,
        EnabledLaunchers = AppProfile.IsPersonal ? [.. Enum.GetValues<CatalogSource>().Where(LocalLaunchers.IsSupported)] : [],
    };
    public string[] IgnoredGameIssues { get; set; } = [];
    public System.Collections.Immutable.ImmutableDictionary<string, Library.GameCollectionPreference> RecordingCollections { get; set; } =
        System.Collections.Immutable.ImmutableDictionary<string, Library.GameCollectionPreference>.Empty;

    // The editor's encoder, quality, size and frame rate start at "same as original" for every clip.
    public InterfaceScale InterfaceScale { get; set; } = new();
    public string Format { get; set; } = "source";
    public string Encoder { get; set; } = "";                    // the last one picked, used for the original's codec
    public double TargetSizeMB { get; set; } = 25;
    public int AudioBitrate { get; set; } = 192;
    public bool MergeAudio { get; set; }                         // no longer remembered: the editor always starts unmerged
    public bool Loop { get; set; } = !AppProfile.IsPersonal;
    public string? LastFolder { get; set; }
    public string? SoundFolder { get; set; }                     // where the editor last added a sound from

    // Recording (instant replay)
    private volatile bool _recordingEnabled = !AppProfile.IsPersonal;
    private int _recordingDisableRevision;
    private int _capturePermissionRevision;

    // Runtime-only, monotonic until process restart. Every capture grant mutation
    // invalidates any detached epochs made under a previous grant generation.
    // This is never a substitute for rechecking HWND/PID and current display consent.
    internal int CapturePermissionRevision => Volatile.Read(ref _capturePermissionRevision);
    private void CapturePermissionsChanged() => Interlocked.Increment(ref _capturePermissionRevision);

    public bool RecordingEnabled
    {
        get => _recordingEnabled;
        set
        {
            if (_recordingEnabled == value) return;
            _recordingEnabled = value;
            if (!value)
            {
                Interlocked.Increment(ref _recordingDisableRevision);
                CapturePermissionsChanged();
            }
        }
    }

    internal int RecordingDisableRevision => Volatile.Read(ref _recordingDisableRevision);
    internal bool AllowsManualDesktop(int revision) => RecordingEnabled && revision == RecordingDisableRevision;
    public int GallerySidebarWidth { get; set; } = 230;        // Folder navigation width, 190–520 WPF pixels
    public string RecordMonitor { get; set; } = "";             // GDI device name; empty = main monitor
    public string RecordAspect { get; set; } = "";              // "16:9" records the monitor's 16:9 center; empty = all of it
    public int RecordFps { get; set; } = 60;
    public int RecordResolution { get; set; }                   // short side; 0 = native
    public RecordingQuality RecordQuality { get; set; } = RecordingQuality.High;
    public string RecordEncoder { get; set; } = "";             // empty = newest GPU codec available
    public CaptureEngine.MonitorBackend CaptureBackend { get; set; }
    public CaptureEngine.NvencSpeed NvencPreset { get; set; } = AppProfile.IsPersonal
        ? CaptureEngine.NvencSpeed.FastestP2 : CaptureEngine.NvencSpeed.BalancedP4;
    public bool RecordingDiagnostics { get; set; }
    public int BufferSeconds { get; set; } = 60;
    public bool CaptureCursor { get; set; } = true;
    public bool PreferGameWindow { get; set; } = AppProfile.IsPersonal;
    // Sources and authorization. Mutations invalidate detached encoded replay epochs
    // even when the user restores an earlier-looking configuration moments later.
    private bool _discordCaptureEnabled;
    public bool DiscordCaptureEnabled
    {
        get => _discordCaptureEnabled;
        set
        {
            if (_discordCaptureEnabled == value) return;
            _discordCaptureEnabled = value;
            CapturePermissionsChanged();
        }
    }

    private bool _discordCloakedMirror;
    public bool DiscordCloakedMirror
    {
        get => _discordCloakedMirror;
        set
        {
            if (_discordCloakedMirror == value) return;
            _discordCloakedMirror = value;
            CapturePermissionsChanged();
        }
    }

    private Recording.Companion.DiscordCaptureMode _discordCaptureMode =
        Recording.Companion.DiscordCaptureMode.DuringCalls;
    public Recording.Companion.DiscordCaptureMode DiscordCaptureMode
    {
        get => _discordCaptureMode;
        set
        {
            if (_discordCaptureMode == value) return;
            _discordCaptureMode = value;
            CapturePermissionsChanged();
        }
    }

    private bool _allowRecordingDisplaysByDefault = AppProfile.IsPersonal;
    public bool AllowRecordingDisplaysByDefault
    {
        get => _allowRecordingDisplaysByDefault;
        set
        {
            if (_allowRecordingDisplaysByDefault == value) return;
            _allowRecordingDisplaysByDefault = value;
            CapturePermissionsChanged();
        }
    }

    private string[] _allowedRecordingDisplays = [];
    public string[] AllowedRecordingDisplays
    {
        get => _allowedRecordingDisplays;
        set
        {
            string[] next = value ?? [];
            if (!_allowedRecordingDisplays.SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
                CapturePermissionsChanged();
            _allowedRecordingDisplays = next;
        }
    }

    private string[] _excludedGameDisplays = [];
    public string[] ExcludedGameDisplays
    {
        get => _excludedGameDisplays;
        set
        {
            string[] next = value ?? [];
            if (!_excludedGameDisplays.SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
                CapturePermissionsChanged();
            _excludedGameDisplays = next;
        }
    }

    public bool SplitChatAudio { get; set; } = true;
    public bool SplitMusicAudio { get; set; } = true;
    public bool RecordMic { get; set; } = !AppProfile.IsPersonal;
    public List<string> MicDeviceIds { get; set; } = [""];      // empty id = Windows default
    public bool SeparateMicTracks { get; set; }                 // a track per microphone instead of one shared Mic track
    public bool UseAudioEndpoints { get; set; }
    public List<EndpointTrack> EndpointTracks { get; set; } = [];

    /// <summary>The single microphone of settings from before several could be recorded, moved into <see cref="MicDeviceIds"/>
    /// when loaded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MicDeviceId { get; set; }
    public int HotkeyModifiers { get; set; } = 1;               // MOD_ALT
    public int HotkeyKey { get; set; } = 0x79;                   // Alt+F10 for a new profile
    public Shell.Hotkey MasterHotkey { get; set; }
    public Shell.Hotkey DesktopHotkey { get; set; }
    public Shell.Hotkey SwitchMonitorHotkey { get; set; }
    public bool ClipSound { get; set; } = true;
    public int ClipSoundVolume { get; set; } = 100;
    public string? ClipSoundOutputId { get; set; }
    public string? ClipSoundFilePath { get; set; } // Optional user-selected local chime; null uses bundled shutter.
    public Shell.ClipEventOptions ClipEvents { get; set; } = new();
    public bool ShowGameRecordingIndicator { get; set; }
    public bool AllowRecordingIconsInCapture { get; set; }
    public bool ShowRecordingMicrophoneIndicator { get; set; } = true;
    public Shell.OverlayAnchor GameRecordingIndicatorAnchor { get; set; } = Shell.OverlayAnchor.BottomRight;
    public bool ClipNotificationsEnabled { get; set; } = true;
    public bool ClipWindowsNotification { get; set; } = !AppProfile.IsPersonal;
    // Suppress expected no-game/off hotkey warnings by default in Personal, not actual capture errors.
    public bool ShowInactiveSaveNotifications { get; set; } = !AppProfile.IsPersonal;
    // First-run prompt is explicit: a new Personal profile never silently starts screen capture.
    public bool ReplayOnboardingCompleted { get; set; }
    // Presentation only. Existing profiles retain Advanced; fresh Personal profiles start in Basic.
    public bool BasicSettings { get; set; } = AppProfile.IsPersonal;
    public int? GuidedSetupStep { get; set; } = AppProfile.IsPersonal ? 0 : null;
    public Shell.ClipOverlayOptions ClipOverlay { get; set; } = new() { Enabled = AppProfile.IsPersonal };
    public bool EnableLibraryIndex { get; set; } = AppProfile.IsPersonal;
    public double ShareSizeMB { get; set; } = 30;                // shared copies are made to fit under this
    [JsonConverter(typeof(SharingProfiles.ListConverter))]
    public SharingProfile[]? ShareProfiles { get; set; }
    [JsonConverter(typeof(SharingProfiles.IdConverter))]
    public string? DefaultShareProfile { get; set; }
    public int MediaJobConcurrency { get; set; } = 1;
    public bool ShareMotionBlur { get; set; } = true;             // sharing a 120 fps clip blends it into 60 fps motion blur
    public string ClipsFolder { get; set; } = AppProfile.ClipsDirectory;
    public string[] AdditionalClipSources { get; set; } = [];
    public string ClipFilenameTemplate { get; set; } = Gallery.ClipNaming.DefaultTemplate;
    public string[] LibraryExclusions { get; set; } = [];
    public bool ConfirmClipDeletion { get; set; } = true;
    public bool ConfirmSaveOverOriginal { get; set; } = true;
    public string? ExternalPlayerPath { get; set; }

    // Window behaviour
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableReplayOnStartup { get; set; }
    public string? Theme { get; set; }
    public string? CustomThemeJson { get; set; }
    public string[] SavedCustomThemes { get; set; } = [];
    public bool StartInTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    internal void ApplyReplayStartupPreference()
    {
        if (!AppProfile.IsPersonal || EnableReplayOnStartup is not bool enabled ||
            !(LoadedFromFile || ReplayOnboardingCompleted)) return;
        RecordingEnabled = enabled;
        if (enabled) PreferGameWindow = true;
    }

    public bool CheckForUpdates { get; set; } = true;
    public string? UpdateChannel { get; set; }
    public string? SeenVersion { get; set; }                      // the version whose "what's new" was last shown

    /// <summary>Read from an existing settings file (QuickClip has run here before).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool LoadedFromFile { get; private set; }                   // "stable" or "nightly"; unset = the kind of build this is

    public string? EncoderCacheKey { get; set; }
    public List<string>? EncoderCache { get; set; }

    internal static string FilePath => Path.Combine(AppProfile.SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly Lazy<AppSettings> Settings = new(Load);
    public static AppSettings Current => Settings.Value;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && Parse(File.ReadAllText(FilePath)) is { } loaded)
            {
                loaded.LoadedFromFile = true;
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Essential(DiagnosticCategory.Settings, "Could not read settings", ex);
        }
        return new AppSettings();
    }

    internal static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.ClipOverlay = (settings.ClipOverlay ?? new()).Migrate();
        if (AppProfile.IsPersonal) settings.SavedCustomThemes = Themes.SavedThemes.Normalize(settings.SavedCustomThemes, settings.CustomThemeJson);
        // V1 shared a preference between minimizing and Windows startup. Only
        // the startup behavior survives; the newer key takes precedence.
        using (var legacy = JsonDocument.Parse(json))
        {
            var root = legacy.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Preserve legacy notification choices when only one channel was serialized.
                if (root.TryGetProperty(nameof(ClipWindowsNotification), out _) &&
                    !root.TryGetProperty(nameof(ClipOverlay), out _))
                    settings.ClipOverlay = new();
                if (root.TryGetProperty(nameof(ClipOverlay), out _) &&
                    !root.TryGetProperty(nameof(ClipWindowsNotification), out _))
                    settings.ClipWindowsNotification = true;
                if (!root.TryGetProperty(nameof(BasicSettings), out _)) settings.BasicSettings = false;
                if (!root.TryGetProperty(nameof(GuidedSetupStep), out _)) settings.GuidedSetupStep = null;
                if (settings.GuidedSetupStep is < 0 or > 8) settings.GuidedSetupStep = 0;
                if (!root.TryGetProperty(nameof(AllowRecordingDisplaysByDefault), out _))
                    settings.AllowRecordingDisplaysByDefault = false;
                // Only the old exact physical identity is safe to migrate. The former
                // global toggle does not identify which monitors the owner had used.
                if (AppProfile.IsPersonal && !root.TryGetProperty(nameof(AllowedRecordingDisplays), out _) &&
                    root.TryGetProperty("AuthorizedGameDisplay", out var legacyDisplay) &&
                    legacyDisplay.ValueKind == JsonValueKind.String && legacyDisplay.GetString() is { } identity &&
                    (settings.PreferGameWindow ||
                        root.TryGetProperty("AllowGameDisplayFollowing", out var following) && following.ValueKind == JsonValueKind.True) &&
                    !CaptureSourcePolicy.IsDisplayExcluded(settings, identity))
                    settings.AllowedRecordingDisplays = [identity];
                settings.AllowedRecordingDisplays = [.. (settings.AllowedRecordingDisplays ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase)];
            }
            if (root.ValueKind == JsonValueKind.Object &&
                !root.TryGetProperty(nameof(StartInTray), out _) &&
                root.TryGetProperty("MinimizeToTray", out var oldTray) &&
                oldTray.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.StartInTray = oldTray.GetBoolean();
        }
        settings.Games = GameCatalogRuntime.RestoreSavedScan(settings.Games);
        if (AppProfile.IsPersonal && settings.HotkeyKey == 0x79)
        {
            using var existing = JsonDocument.Parse(json);
            if (existing.RootElement.ValueKind != JsonValueKind.Object ||
                !existing.RootElement.TryGetProperty(nameof(HotkeyKey), out _))
                settings.HotkeyKey = 0; // older Personal profiles with no saved key were unbound
        }
        if (settings.MicDeviceId != null)
        {
            settings.MicDeviceIds = [settings.MicDeviceId];
            settings.MicDeviceId = null;
        }
        settings.EndpointTracks = EndpointAudio.Normalize(settings.EndpointTracks);
        return settings;
    }

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Debug runs that point QuickClip at test folders must not overwrite the real settings.</summary>
    internal static bool SaveDisabled { get; set; }

    public void Save() => TrySave();

    internal bool TrySave(bool? recordingEnabled = null)
    {
        if (SaveDisabled)
        {
            if (recordingEnabled is { } inMemory) RecordingEnabled = inMemory;
            return true;
        }
        string? staged = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (JsonOptions)
            {
                staged = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var snapshot = recordingEnabled.HasValue ? (AppSettings)MemberwiseClone() : this;
                if (recordingEnabled is { } enabled) snapshot.RecordingEnabled = enabled;
                File.WriteAllText(staged, snapshot.ToJson());
                File.Move(staged, FilePath, overwrite: true);
                staged = null;
                if (recordingEnabled is { } committed) RecordingEnabled = committed;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Essential(DiagnosticCategory.Settings, "Could not save settings", ex);
            return false;
        }
        finally
        {
            if (staged != null)
            {
                try { File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using QuickClip.Recording;
using QuickClip.Discovery;

namespace QuickClip;

/// <summary>User preferences persisted in the active application profile.</summary>
internal sealed class AppSettings
{
    public GameCatalogSettings Games { get; set; } = new();
    // The editor's encoder, quality, size and frame rate start at "same as original" for every clip.
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

    public bool RecordingEnabled
    {
        get => _recordingEnabled;
        set
        {
            if (_recordingEnabled == value) return;
            _recordingEnabled = value;
            if (!value) Interlocked.Increment(ref _recordingDisableRevision);
        }
    }

    internal int RecordingDisableRevision => Volatile.Read(ref _recordingDisableRevision);
    internal bool AllowsManualDesktop(int revision) => RecordingEnabled && revision == RecordingDisableRevision;
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
    // Only a new, explicit game-triggered whole-display confirmation populates this field.
    public string? AuthorizedGameDisplay { get; set; }
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
    public bool ClipNotificationsEnabled { get; set; } = true;
    public bool ClipWindowsNotification { get; set; } = true;
    // Suppress expected no-game/off hotkey warnings by default in Personal, not actual capture errors.
    public bool ShowInactiveSaveNotifications { get; set; } = !AppProfile.IsPersonal;
    // First-run prompt is explicit: a new Personal profile never silently starts screen capture.
    public bool ReplayOnboardingCompleted { get; set; }
    public Shell.ClipOverlayOptions ClipOverlay { get; set; } = new();
    public bool EnableLibraryIndex { get; set; }
    public double ShareSizeMB { get; set; } = 30;                // shared copies are made to fit under this
    public bool ShareMotionBlur { get; set; } = true;             // sharing a 120 fps clip blends it into 60 fps motion blur
    public string ClipsFolder { get; set; } = AppProfile.ClipsDirectory;
    public string[] AdditionalClipSources { get; set; } = [];
    public string ClipFilenameTemplate { get; set; } = Gallery.ClipNaming.DefaultTemplate;
    public string[] LibraryExclusions { get; set; } = [];
    public bool ConfirmClipDeletion { get; set; } = true;

    // Window behaviour
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    public bool CheckForUpdates { get; set; } = !AppProfile.IsPersonal;
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
            Log.Write("Could not read settings: " + ex.Message);
        }
        return new AppSettings();
    }

    internal static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
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

    internal bool TrySave()
    {
        if (SaveDisabled) return true;
        string? staged = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (JsonOptions)
            {
                staged = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(staged, ToJson());
                File.Move(staged, FilePath, overwrite: true);
                staged = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Could not save settings: " + ex.GetType().Name);
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

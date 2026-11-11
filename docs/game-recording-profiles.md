# Per-game profiles and session integration

## Profile file and resolution

`GameProfileStore.ForCurrentProfile()` uses the current AppProfile settings directory. Each portable data root therefore gets its own `game-recording-profiles.json`. Tests can pass an explicit settings directory to the store. Settings should persist the selected profile ID in the existing `CatalogRules.ProfileId` field. A game rename or a manual EXE edit retains its catalog ID and profile link.

Version 2 uses a map from profile ID to sparse overrides. Here is a synthetic example.

```json
{
  "schemaVersion": 2,
  "profiles": {
    "example-fast": {
      "target": { "source": "gameWindow" },
      "fallback": "gameMonitor",
      "replaySeconds": 90,
      "saveSeconds": 30,
      "resolutionShortSide": 1440,
      "fps": 60,
      "codec": "hevc",
      "nvencPreset": "fastestP2",
      "bitrate": { "mode": "vbr", "kbps": 24000 },
      "audioEndpoints": [{ "flow": "microphone", "deviceId": "fixture-mic" }],
      "openGalleryOnSessionEnd": false,
      "tags": ["Practice"]
    }
  }
}
```

`GameProfileDocument.Resolve(profileId, globalDefaults)` applies the saved override over the supplied global policy and validates the result. An omitted or null field inherits. An empty list explicitly clears tags or audio references. An empty label clears the global label, an empty aspect selects the whole source, and zero short-side resolution selects native size. A missing profile ID uses global defaults. Disabled shortcuts use `{"enabled":false}`. A configured save duration must fit within the resolved replay duration. A combination that fails validation should be surfaced in Settings and the integration should use the global policy for that session.

The store accepts the version 1 draft with `bufferSeconds` in place of `replaySeconds`, migrates it in memory, and writes version 2 on an explicit Save. Load returns a status and an empty document for missing, unreadable, corrupt or unsupported files. The caller should report corrupt and unsupported states and retain their source files until an explicit recovery choice. Saving a valid document writes a temporary file beside the profile and replaces the destination with the complete file. Serialize Settings saves within the profile owner.

Profile targets and their future adapters

| Target | Intended behavior |
| --- | --- |
| Game Window | Capture the verified game window. A covering external window is excluded. WGC may show its system border on Windows 10. |
| Game Monitor | Capture the display containing the verified game window. Covering windows appear in the output. |
| Explicit Monitor | Resolve the stored monitor ID against the available displays. A missing device goes through the selected fallback. |

Fallback values select the game monitor, the global source, or skipping capture. The recording adapter must detect unavailable sources and unsupported combinations before applying a policy. For example, if both the window and its game monitor are unavailable, surface that state and require an explicit global-source fallback to capture another display.

`GameBitrate` represents automatic, custom target bitrate, CBR, VBR or CQP. The eventual encoder adapter validates modes and maps the quantizer to the chosen codec's supported range. Endpoint references contain the existing flow and device ID. Legacy microphone references use the existing device IDs, including the empty ID for the Windows default. Resolve references through the existing `EndpointTracks` and `MicDeviceIds` lists in the active audio mode. Report missing references and preserve explicit empty selections. Gallery opening, full-session recording, tags, labels, shortcuts and burst-save are saved preferences awaiting integration. Gallery opening, full-session recording and burst-save default to false when unspecified.

## Proposed session and bookmark API

The declarations below describe the integration contract. `WindowOwner` is the existing PID, creation time, HWND and thread identity from the recorder domain.

```csharp
record GameObservation(WindowOwner Owner, CatalogMatch Match, bool IsForeground);
record GameSession(Guid Id, string GameId, WindowOwner Owner,
    DateTimeOffset StartedUtc, GameRecordingPolicy AppliedPolicy);
record SessionTransition(SessionSummary? Ended, GameSession? Started, GameSession? Active);
record SessionBookmark(Guid Id, Guid SessionId, string GameId,
    TimeSpan Elapsed, string? Label);
record SavedReplayCoverage(Guid SessionId, Guid CaptureEpochId,
    TimeSpan SessionFrom, TimeSpan SessionThrough, TimeSpan ClipOffsetAtStart);
record SessionSummary(Guid SessionId, string GameId, TimeSpan Duration,
    IReadOnlyList<SessionBookmark> Bookmarks);

SessionTransition Observe(GameObservation? observation, Func<WindowOwner, bool> isProcessAlive,
    Func<string, GameRecordingPolicy> resolvePolicy);
SessionBookmark? AddBookmark(string? label);
SessionSummary? EndSession(Guid sessionId);
TimeSpan? MapBookmarkToClip(SessionBookmark bookmark, SavedReplayCoverage coverage);
```

- **GameDetector** verifies the current HWND owner, thread, PID and process creation time before publishing a process-derived path to the catalog index. A launcher result can support a pending association. The observed renderer opens the session. A foreground change alone keeps the current session while its verified process remains alive, even if a foreground-only rule suspends capture. A replacement HWND from the same process can update the owner. A process exit, crash, or deliberate game switch closes the session. A restart creates a new session ID even if Windows reuses the PID.
- **Recorder** resolves and snapshots the policy when starting a session. Settings edits can prepare the next policy. Applying a different codec, source or other stream setting during a live session requires an explicit transition that handles the existing replay buffer and tells the user when history will reset. Keep the current session policy through alt-tab.
- **AppHost** can later dispatch a bookmark action and consume the session-ended summary. The tracker uses an injected `TimeProvider` and stores elapsed time from its monotonic session anchor. Store UTC start time for display. Relative elapsed time and session ID survive persistence without relying on monotonic timestamps from another process or boot.
- **Recorder.SaveClipAsync** currently returns the saved path, folder and duration. Extend its receipt at integration with actual retained coverage in the session's timebase, anchored to the capture clock when a capture epoch starts. Use actual packet bounds after keyframe alignment and split coverage at gaps or resets. A bookmark maps to clip time only if it belongs to the same session and falls within a retained segment. Add the elapsed delta from `SessionFrom` to that segment's `ClipOffsetAtStart`. Associate coverage with its capture epoch so a source restart cannot map old footage onto the new buffer. A bookmark outside coverage can remain in the session summary.

Future integration tests should use a fake clock and synthetic observations. Cover launcher-to-renderer transition, alt-tab continuity, HWND recreation, PID reuse, crash/restart, explicit session end, clock adjustment, policy edits during capture, and bookmark mapping at retained, evicted and reset buffer boundaries. A save that fails must leave the bookmark available for a later save or the session summary.

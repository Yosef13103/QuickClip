# Proposed implementation architecture (validate against checkout)

## Preserve upstream design
- `src/QuickClip.Capture` C++ video/audio encode, packet ring, WASAPI, Windows Graphics Capture/DXGI.
- `src/QuickClip` C# WPF settings/gallery/editor, FFmpeg exports and embedded libmpv playback.
- Existing behavior: 1 continuous video ring/session; optional game window capture; mic support; separate process Chat/Music disabled in upstream 0.7.1; upstream has user-facing timestamps/game folders but lacks structured media identity DB.
- JaredMerritt's multi-microphone PR #11 was merged upstream 2026-10-19; do not cherry-pick his stale fork.

## Extension boundaries
1. **Audio endpoint registry** (C#): enumerates Windows eRender/eCapture devices and tracks persistent endpoint IDs, current state, names, signal chain, and chosen track labels. Maintain settings schema version.
2. **Endpoint capture** (C++): add `SourceKind::RenderLoopback` on arbitrary `IMMDeviceEnumerator::GetDevice(endpointId)` + loopback; keep `Microphone` device capture and optional legacy process capture. Use separate clock-aware mixers and handle device invalidation, hotplug, default endpoint change, silence.
3. **Capture policy** (C#): deterministic state machine: active game sessions; pinned game/app; Discord-like application rules; explicit desktop fallback. Never let a foreground Discord window displace pinned game without policy selection. Avoid single magic fullscreen heuristic.
4. **Video session manager**: initially one encoder/ring; later optional independent `GameSession` and `CompanionSession` each with its own ring/encoder; share captured audio packets when feasible, avoid capturing/re-encoding audio twice. Limit concurrency/configure memory budgets and prioritize game frame delivery.
5. **Media identity/library** (C# SQLite with migrations): stable clip IDs, original immutable capture timestamp UTC+offset, source identities, user override classification, custom title, tags, file variants, edit recipe, media facts. Use local DB in fork-specific APPDATA, use journal mode and transactional file/DB moves; reconcile on crashes.
6. **Editor and exporter**: use existing ffmpeg stream-copy path where possible; after every render validate playability/duration/tracks and output location. User chooses resulting master/edited final retention.
7. **Migration importer**: separate read-only scanner and independent optional converter. Legacy files never passed to destructive functions. Logs scrubbed of sensitive data. Export reproducible machine-readable inventory to explicitly chosen *external destination* only.
8. **Perf telemetry (local-only)**: lightweight counters only: RAM ring bytes, committed/private bytes, CPU, GPU video encode/copy/3D when measurable, frame drop/capture gap, bitrate, encoder sessions; manual benchmark harness with data stored locally.

## Explicit audio capture routing modes
- `endpoint`: explicit eRender loopback + eCapture microphone (primary Windows 10 mode).
- `legacy-default`: upstream default desktop + mic fallback for backwards compatibility.
- `process`: upstream experimental per-application on supported Windows builds, never a prerequisite.
- `hybrid` if supportable and correctly deduplicated; avoid mixing overlapping endpoints unless user explicitly acknowledges duplicate playback.
- Separate *recorded stream* from *playback preview mix*: a source may appear in master once but should be mixed with adjustable gain in preview.

## Video targeting: priority policy
State model: `Idle`, `DesktopFallback`, `GamePinned`, `ManualPinned`, `DualCapture`. Events: game active/closed, focus moved, Discord window/popup changed, monitor moved, display changed, saved clip, capture session stalled. Keep video session stable across focus-only events. Distinguish actual HWND lifetime from foreground; when game closed handle fallback with prior buffer retention if format allows. Manual selection outranks automatic policy. If multiple games active, prefer last focused playable one, expose picker, and persist per-game preference.

## Discord and companion app rules
Represent companion app as a configurable rule set of path/process identity, window classification, capture mode and monitor fallback. Handle Discord.exe and update/bootstrap process ambiguities; do not label generic `Update.exe` as a game. Main window vs call popouts and floating windows may have distinct HWNDs; policy should support 'monitor containing last active companion window' and manual pin. Avoid globally recording every monitor merely to chase popouts by default. Settings UI should clearly identify *what visual content* is currently buffered.

## Dual capture feasibility
Two **independent encoded rings** is the clearest model: save game only, companion only, or both on demand. Creating them may approximately double video encoding work, GPU surfaces and RAM ring usage, while actual load varies with resolution/bitrate. Lazy companion capture on VC-active + configured rules can reduce unnecessary load but needs buffer warm-up; expose that limitation. Multi-monitor combined frame layout is a separate exporter/compositor; maintain original source aspect ratios with padding or explicit scale/crop. A 3440x1440 + 1920x1080 left/right layout needs a 5360x1440 canvas with 1080p panel letterboxed vertically; test actual encoder max width before enabling. If unsupported, choose two separate files or user-selected scale; no silent stretching.

## Development identity/isolation
Before test-running a modified build on user's PC, isolate `%APPDATA%`, `%LOCALAPPDATA%`, `Videos`, context menu, Run key, update checker and installer identity from stock QuickClip. Keep upstream legal notices. Use fork-specific namespace for development, and an explicitly versioned settings migration when packaging. No automatically installing an upstream QuickClip update over the customized app.

## Extensibility
Prefer settings schemas/presets/rules in SQLite or versioned JSON depending on domain, *not* a general plugin runtime initially. Security and stable API must precede any arbitrary executable plugin support. Avoid permanent config bloat by hiding advanced fields unless enabled.

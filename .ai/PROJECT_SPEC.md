# Functional product specification

## Primary principle
A clipping tool should stay out of the way, reliably preserve the desired moment, record native ultrawide video and independently editable audio, produce an immediately shareable file with the right date/name, and avoid unnecessary GPU/RAM/disk usage. Advanced controls are welcome, but every costly feature must be explicit and explained.

## Target system and source apps
- Windows 10 22H2, desktop NVIDIA RTX 3070, Ryzen 5 5600X, 32 GB RAM.
- Primary display: 3440×1440 ultrawide at up to 144 Hz. Secondary: 1920×1080. Do not scale/crop native ultrawide unless selected.
- SteelSeries Sonar is used as **existing application-to-virtual-audio-device routing**. Do not replace Sonar and do not conflate application audio capture with endpoint loopback.
- Current preferred 3-track behavior mirrors Moments:
  - `Game` source: **Headphones (SteelSeries Arctis Pro Game)**, a physical endpoint that carries the user's desired mixed game/media listening output.
  - `Chat` source: **SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)**, set for Discord and future chat apps.
  - `Mic` source: **SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)**, capture endpoint.
- Other available endpoints: Sonar Gaming, Sonar Media, Sonar Aux, Arctis Pro Chat. Allow arbitrary playback endpoints independently with labels and mute/toggle; do not hardcode brands. Detect/warn when capturing a physical mixed output alongside its contributing virtual channels, which may double audio in previews/exports.
- Existing Moments often downscales ultrawide to 2560×1072; native 3440×1440 capture is a key improvement.

## Capture video requirements
- Default: `GAME_FIRST_STICKY`—when recognized active game session exists, capture the game window even if Discord gets focus. Discord alone must not steal the video buffer. Detect relevant game windows using foreground plus session memory; expose a manual priority/pin override and game exception list.
- Discord has main window, call popouts and other windows. Detect by executable/process lineage/window identity, not hardcoded `Update` executable label, titles alone, or OS primary monitor assumption. Provide configurable app rules for Discord-like services.
- When no eligible game is active: configurable fallback to selected monitor, monitor containing configured Discord window, Discord application window, current foreground monitor, or disabled. If multiple Discord windows exist, allow choose main/popup/foreground/monitor policy, and explain ambiguities.
- Distinct hotkeys/actions: Save preferred/game; Save Discord/desktop (if enabled); Save both (if enabled); manual select target. The ordinary hotkey should prioritize game as user rarely clips Discord over a game. App detection must not reset a healthy replay buffer on mere focus change.
- Modes: (A) single prioritized capture (default); (B) two independent continuously buffered recordings with opt-in; (C) optional all-monitors independent, selectable; (D) optional composed canvas/export (multi-res letterboxing/layout; no distortion). Composite only if encoder/display limits permit; fallback separate files.
- Separate timelines/sessions for different resolutions. Window/display switching must not silently cause all buffered history to disappear; if a codec/container requires clearing, show warning/diagnostic and preserve old session until it expires or is saved if feasible.
- Support manual monitor selection (not primary-only), window capture, cursor show/hide, duration 10–300s or more if configurable with memory estimate, 30–240 FPS as supported, hardware codec preference, quality, native output resolution, cropped output modes, and excluded apps.
- Capture audio/video timing accurately through sleep/resume, display topology changes, alt-tab, minimized exclusive fullscreen, audio-device unplug/reconnect, and device default switches. Surface unsupported conditions and verify actual frames/sound instead of displaying misleading 'recording'.

## Audio requirements
- Multiple independently selected Windows **render endpoint loopback** streams, plus one or more **capture endpoints** for microphones; each has stable endpoint ID, friendly label, enable/disable, optional track grouping, independent buffer audio stream, and sample-rate/channel diagnostics.
- No process-based voice-chat detection required for the core feature. Keep upstream's process-loopback experimental path as a separate optional mode when supported; don't rely on Windows 11-only APIs for Windows 10 endpoint captures.
- Audio pipeline must not send the same signal to multiple tracks unintentionally or cause double-volume in composite playback. Show the source graph and warn when overlapping output and Sonar virtual channels are selected. Expose meters and a short local self-test.
- Post-record clip editor must show each track with waveform, live 0–200% gain, mute/solo/include, optional merge, and preview accurately reflecting export. Retain separated tracks in master unless user intentionally flattens; Discord-friendly shares can render merged copies while retaining master tracks.
- Provide a 'no mic' or mic mute quick action without losing unmodified master.

## Clip identity and naming
- **Capture timestamp is immutable and distinct from edit time and export time.** Persist capture UTC instant and local offset plus localized display value; treat original media metadata/file timestamps as hints, never rewrite history when editing.
- Naming configurable and previewable with placeholders, sanitized for Windows and collision-proof. Preferred final patterns:
  - `{game}__{yyyy-MM-dd}__{HH-mm-ss}_{custom_title}.mp4`
  - `{game}_{custom_title}__{yyyy-MM-dd}-{HH-mm-ss}.mp4`
  - Unnamed master: `{game}__{yyyy-MM-dd}__{HH-mm-ss}.mp4`.
- Renaming in UI updates filesystem and library atomically where possible. An edit made months later must preserve the recorded date. User can assign custom title after capture or before export. Handle no title without trailing separators. Store capture time in MP4/MKV metadata where supported.
- Game misidentification (e.g. Discord named 'Update') must be user-correctable; persist per-executable rules and per-clip overrides. Preserve original detector values for diagnostics. User-defined names are separate from game classification.

## Library and retention
- User has `E:\Stuff\Personal\Clips` containing approximately 31.5 GB of completed exported clips, arranged by game directories. Nested `E:\Stuff\Personal\Clips\SteelSeries Moments` is roughly 113 GB of Moments editing originals. This nested subtree must not be mistaken for final clips or imported twice.
- Local SQLite indexed library, but video/audio must remain standard playable files. Recover/index from filesystem if DB is lost; store identity, capture time, game/classification, custom title, favorites, multi-tags, duration, dimensions, FPS, codec, audio tracks, source capture method, fingerprints, edit instructions, export lineage, and file relations.
- Gallery: fast incremental index; thumbnails; grouped game folders; filter by games, arbitrary multi-tags, favorites, source type (game/Discord/desktop), date range, title search, file size, duration, whether final/exported/master retained. Sort by actual capture time, not when exported or trimmed.
- Distinguish immutable master, non-destructive edit project, and rendered final. Keep a source master only if policy says so. Retention choices: keep forever, keep N days after verified final, keep favorites only, or manual. Storage usage dashboard and estimated savings; options understandable and reversible. **Never delete or overwrite old media automatically without explicit feature opt-in and tested restore path.** Recycle Bin isn't freed storage.
- Adjustable target file-size presets (default examples original / 10 / 50 / 100 MB plus user-defined unlimited presets), precise expected estimated output before export, actual size and iterative retry warning, optional format/codec/profile/resolution and crop controls. Custom per-game presets allowed later.
- Normal saves should avoid overwriting existing files, preserve master when requested, and avoid unnecessary re-encode when only changing container/keyframe-aligned trim.

## Migration
- Read-only investigation of installed GG settings/library/indexes and of legacy on-disk originals. Do not assume chat/mic tracks are missing just because Windows default video player plays one track. Use ffprobe and source metadata as evidence; distinguish embedded multitrack, separate sidecar, proprietary encoding, and unrecoverable unknowns.
- Build dry-run inventory + mapping report for legacy originals and 31.5 GB exports, not a bulk copy or automatic conversion. Migrate selected items with verifiable output and sufficient free-space checks. Preserve capture date, game, custom title and any recoverable audio independently. Source archives remain untouched until user separately authorizes cleanup.

## Stretch features—not default
- Concurrent Discord + game 2x encode cost when both continuously buffered; not required for the main gameplay experience; expose opt-in when VC app is active and separate save hotkeys.
- Independent every-monitor capture; side-by-side/stacked composite with correct mixed dimensions and safe encoder limits.
- Profiles and advanced rules; hotkey groups; performance overlay; per-game exceptions; optional plugin API eventually after security design.

## Out of scope / anti-goals
- Automatic uploads, cloud sync, private media analysis by remote services, random required account creation.
- Rewriting the capture engine in Rust before gathering comparative performance evidence.
- Reading private credentials/tokens, altering GG installations, automatically deleting the archive, or trying to bypass DRM/protected content.

# Implementation roadmap (incremental, not one giant PR)

Use separate branches and atomic commits for each phase. `main` tracks upstream and `custom/main` aggregates user-facing improvements. A phase passes only when listed acceptance tests pass and `.ai/STATUS.md` is updated with observed evidence.

## Phase 0 — audit, setup, baseline, and feasibility (FIRST)
1. Check repository branch status, remotes, upstream head; inspect tracked modifications before touching files. Verify official upstream version, expected build dependencies and scripts. Review JaredMerritt/QuickClip and original upstream PR #11, confirm there is no unique code to import.
2. Static code map of C# + C++ FFI, source discovery and capture state machine, editor, FFmpeg export, installer, startup/update, gallery, tests. Annotate exact file:function paths and compare against `.ai/ARCHITECTURE.md`.
3. Read-only audit of `C:\Program Files\SteelSeries\GG`, `C:\ProgramData\SteelSeries`, and `E:\Stuff\Personal\Clips` **only if local permission is available**. Inventory file extensions, counts, size, directory structure, ffprobe streams on small samples, and likely metadata; exclude secrets/session stores and never hash 113 GB blindly. No user clips or internal metadata sent to cloud or committed.
4. Performance baseline: design repeatable local test at 3440×1440 60fps 90s buffer; Moments+Sonar vs fork+Sonar, note cannot run tests without access and hardware. Capture game/Discord alt-tab sequence, recording FPS, CPU, RAM commit and RSS, GPU encoder/copy/3D, capture and editor memory. Do not invent measurements.
5. Produce `.ai/audits/phase0-summary.md` (scrub local sensitive information), `.ai/STATUS.md` updates, architecture plan, likely risks, test gap inventory, and ordered issues/commits. Run available baseline tests and record passing/failing/skipped with reasons. Don't need to wait for user approval for safe implementation within repo after this audit.

## Phase 1 — safe fork development identity, reproducible baseline
- Establish `upstream`, keep `main` syncable, create `custom/main` and isolated feature branches; never write to upstream.
- Isolate fork development app name/settings folder/db/cache/log/install/update mechanism from official upstream QuickClip. Use dev-specific clip folder, disabled auto updater until secure owner-controlled channel. Don't touch existing Windows Run, context menu, or GG paths.
- Pin/audit external dependencies and create reproducible local builds as practical. Add test harness with tiny synthetic media fixtures, no personal media checked in.
- Baseline video/audio known-good recording, export, no GUI regression, no unintended disk writes. Keep migrations of any previous QuickClip settings explicit and opt-in.

## Phase 2 — **arbitrary WASAPI endpoint routing** (highest functional priority)
- Enumerate eRender playback and eCapture microphone endpoints with stable IDs, friendly name and availability. Explicit endpoint selection, enable/disable, label, order, audio meters, simple preview, audio format/clock diagnostics.
- Add C++ render loopback capture for **specified** `IMMDevice` ID; record each selected source on independently named stream; optional groups/duplicate warnings. Keep upstream current default desktop+mic fallback and Windows 10 support. Do not enable existing process-loopback's abandoned Chat/Music switch just to imitate endpoint routing.
- Sensible profile: Arctis Pro Game physical endpoint as `Game`, Sonar Chat as `Chat`, Sonar Microphone as `Mic`; extra Sonar Gaming, Media, Aux available, but warn about physical/virtual overlap. No auto-changing user audio device defaults.
- Handle device removal/restart, muted channels, sample-rate conversion, drift, volume parity, no app-preview feedback loops. Stream labels/mapping through save, preview, share, export.
- Unit tests for settings, routing, device naming, unavailable devices; integration tests for synthetic loopback+mic, separation and no double capture. Manually verify Sonar with native device and headphones.

## Phase 3 — app/window priority policies
- Add rule engine for game-first sticky session, fallback Discord companion app or selected monitor, manual pin and per-app rule editor; use persistent process identity + HWND lifecycle, not an executable `Update` label. Handle main Discord plus multiple popouts.
- Add capture status indicator, explicit current source/monitor label, preview of what is buffered, and game-specific preferred source rules. Prevent alt-tab from deleting video history or switching without policy.
- Test foreground Discord while game runs, popped-out call, Discord moved monitors, no game, game closes, second game opens, minimized fullscreen, screen sleep. Avoid unintended primary-display-only capture.

## Phase 4 — clip metadata, naming and gallery
- Introduce versioned SQLite library with migrations, backup and reconciliation scan; preserve independent capture/edit/export times. Store details in media when reasonable. Handle stable clip IDs and renamed paths.
- Template-based filenames, editable title/game, persistent rules (`Update` -> `Discord` when identified), tags, favorites, search/filters, file-size summaries, grouped folders and correct chronology.
- Confirm gallery indexes `E:\Stuff\Personal\Clips` while excluding nested Moments archival originals by default; no implicit moving or changing existing files.
- Validate rename collisions, long/illegal Windows names, timezone changes, Daylight Saving offsets, database corruption recovery, missing external files and duplicate path/contents.

## Phase 5 — non-destructive editing and retention
- Retain existing QuickClip editor behavior including multi-track gain, crop, trim, source stream copy when possible, undo, and custom size/codec/resolution presets with estimated file size.
- Distinguish `master`, `edit recipe`, `final export` and `share derivative`. Offer save as new, save over (with backup/undo), and save recipe without rendering. Preserve original capture time/game/title across edits.
- Optional archival policy: explicit keep master / delete after a verified exported final / keep marked masters / manual only. Default no deletion. Add true disk storage accounting including Recycle Bin.
- Verify new media with ffprobe (decodable header, duration, expected audio tracks) before suggesting deletion. NEVER remove existing Moments data in this phase.

## Phase 6 — legacy Moments migration
- Implement `moments-scan --dry-run` with selectable read-only paths, excludes, counters, ffprobe track reports, filename/date extraction, likely metadata file/index mapping, confidence score and evidence. Produce a preview and disk-space plan; NO copying 113 GB in Phase 0.
- Match legacy originals to completed exports using metadata, timestamp, game, duration and optional fingerprints; don't conflate a renamed export with a different original. Explain ambiguous matches.
- `moments-import` under a separate destination uses copy/hardlink only when safe, new playable standard files, preserve original multitrack audio where demonstrable, log per-file results, verify output, retain source. Explicit user confirmation before actual migration and before any cleanup; allow cancellation and resume.
- Special case nested `SteelSeries Moments` tree under finished library; avoid recursive rescans, duplicate records and alias collisions. Never modify GG's own live database; operate on copy of metadata/index if needed, with backup.

## Phase 7 — optional concurrent game/Discord capture and all displays
- After reliable single capture and perf baseline, add second independent source/encoder/ring, at user option; priority/hotkeys: game, Discord, both. Companion may record whole selected monitor or just selected window; preserve popout behavior. A visual indicator shows simultaneously captured content.
- Provide per-mode estimated RAM + encoded bitrate + GPU encoder overhead + file size; `Single` default, `Dual only in VC` opt-in, `Dual always` opt-in, `All monitors` experimental opt-in. Gracefully decline excessive concurrent encoder load without damaging primary game buffer.
- Audio tracks should be captured once per recording timeline and correctly remuxed to each video derivative; design cross-session clock and audio retention. Save both with linked IDs and timestamps. Separate sessions must not contaminate one another or require equal resolutions.
- Optional composite exporter with layout presets and correct padding; validate 3440x1440 + 1920x1080. Preserve native source clips. Test game focus shifts, Discord popup, missing monitor, GPU encoder capacity and recovery.

## Phase 8 — perf/security polish and upstream contributions
- Repeat baseline metrics for every mode at comparable quality/bitrate; demonstrate real benefit or document regressions. Check memory leaks after sleep/resume and 1h idle/gaming; capture FPS, encoder utilization, CPU and RAM. Add configurable limits/diagnostics.
- Review installer, updater trust, new dependencies, security permissions and locally stored metadata. No default auto-update to wrong upstream binary.
- Identify reusable self-contained improvements for upstream PRs: endpoint routing, game classification, metadata preservation, file naming, safe migration utility, better crop / export settings. Separate private/custom feature flags from upstreamable modules.

## Phase acceptance principle
If a phase requires destructive user data operations or writes outside repo/test directories, complete a dry-run or stub and stop **that specific operation** for explicit user authorization. Continue safe unrelated implementation. Do not falsely declare an untested phase complete.

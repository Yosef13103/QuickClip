# QuickClip Personal v1.0.0 — first owner-accepted release

**Windows 10 (19045+) / Windows 11, x64.** This release is the **Personal** build from `custom/main`, not the upstream `main` branch. Use the **setup EXE** for normal installation, or the **portable ZIP** for manual extraction. Both are self-contained; the .NET Desktop Runtime is bundled.

## Owner-accepted capture workflow
- Enable Instant Replay, approve your game libraries and capture displays, then let QuickClip recognize eligible games.
- Save an instant-replay clip with the hotkey or Save Clip button; review clips by game/date in the Gallery.
- Edit, trim, crop, adjust individual audio tracks, and export a compatible standalone video.
- Game-following capture retains the verified game during Discord focus and changes to the next verified foreground game after stable handoff.
- Recorder recovery, source authorization checks, resilient save dispatch, better discovery progress, clip notifications, and memory-lifecycle improvements.

## Audio
- A **new raw recording** contains one default **QuickClip Full Mix** for ordinary players **plus separate editable audio stems** when multiple audio sources are enabled.
- QuickClip's Editor recognizes the generated default Full Mix and offers the editable individual stems.
- **Export as new** produces one convenient soundtrack titled **Mix** (instead of concatenated source labels).
- **Save over original** rebuilds the default Full Mix while retaining independent source tracks, with recovery via Recycle Bin.
- Previously captured media is not silently rewritten.

## Included refinements
- Game discovery (Steam/Epic, manual executables and approved directories), candidate classification and explicit exact-EXE approvals.
- Resumable bounded scan batches and clearer candidate/catalog counts.
- Approved-display following and retry after capture failures; critical failure notifications on enabled channels.
- Theme and contrast fixes, Editor navigation, Gallery annotation autosave, clearer confirmation actions, readable notification cards.
- Animated GIF and transparent VP9 WebM notification overlay options, custom clip-save sounds, and larger marker click targets.
- More accessible mic/Chat diagnostics and corrected native audio-overlap mixing.

## Release notes / known limits
- Single live game display at a time. Multi-monitor simultaneous recording and Discord-as-a-standalone companion recorder are **future experiments**, not V1 features.
- AMD AMF / Intel QSV encoding pathways are implemented but **not owner-hardware-certified**; GPU-dependent behavior varies.
- Animated WebP auto-conversion is not yet available. GIF/WebM can be used with appropriate constraints.
- Overlay transparency, monitor changes, different display GPUs, Sonar routing, and sustained CPU stress need wider community hardware verification.
- Some earlier investigation-only PRs were deferred rather than merged as production fixes, e.g. precise-duration research and additional surround handling.
- Automatically deleting previous recordings and importing the SteelSeries Moments archive are **not** part of this release.

## Provenance and validation
- Owner independently verified gameplay detection, instant replay clipping, Editor track mixing, and export on the V1 Hybrid + Wave 2 testing candidate.
- Consolidated PR #73 includes the Wave 2 stabilization changes on top of PR #57 hybrid audio.
- The release also has a minor soundtrack-title change and version bump, and must pass strict Windows Personal/Upstream quality CI and packaging before publishing.
- Source remains GPL-3.0 with the project's inherited third-party licensing obligations.

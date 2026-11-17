# Product behavior — preserve unless explicitly changed

This is a contract, not a claim of completeness. Verify current code. Read only the relevant section.

## Capture and platform
- Master OFF = no recording and no implicit manual grants. Master ON = lightweight eligibility watch; no encoder while waiting for game. Automatic and manual Personal capture share explicit permission for each physical display. Manual desktop also needs deliberate session activation, overrides game capture, and starts a fresh replay when its display changes.
- Game priority sticky through ordinary alt-tab/Discord focus, but separately reason about multiple genuinely eligible games and deliberate transitions; do not pin dead/stale source. Verify running process, actual window/PID and physical display identity; label whole-monitor vs isolated window truthfully.
- Do not confuse discovery, capture eligibility, source authorization or backend capability. Approved folders should make legitimate game EXEs eligible without extra confidence/renderer approvals (Wave 2), not authorize every helper or unrelated executable. Explicit disable/exclude wins.
- Windows 10 no supported WGC border-removal API. Windows 11 supported borderless WGC requires runtime API/build 20348+, proper package capability, user consent, and actual confirmation; DDA full monitor is not game-only. No silent fallback to whole monitor, no anti-cheat or protected-content bypass.
- GPU-first pipeline, preserve DDA/WGC, NVENC P2 default where measured beneficial. No routine CPU pixel readback or frame/packet logging. No speculative Rust/Electron/Tauri rewrite. Record requested versus actual replay length and source truthfully.

## Audio and media format
- Support arbitrary render loopback and mic endpoints. Do not tie endpoint WASAPI to OS-limited process-loopback. Separate Game/Chat/Mic stems, prevent double capture/substitution, preserve sync, mute/solo, levels, generic notification output endpoint. Owner Sonar setup is a test case not a hard dependency.
- Shipped hybrid format: default Full Mix stream for ordinary players + untouched editable source streams. Editor must hide generated mix from editable controls so audio doesn't double. Save over original rebuilds mix and preserves stems, recycles previous original; Export as new file provides one mixed stream Mix. Preserve this architecture and actual capture UTC. Do not resurrect raw-only steelseries-style storage as default.
- Mic tests must be bounded and explicit; stop when appropriate, no unsolicited playback or private recordings retained. Surround/downmix changes must be validated with actual PCM and Sonar levels, not just Streaming status.

## Editor, Gallery and storage
- WPF/libmpv native preview, preserve edit state across theme/navigation/minimize, no repeated player initialization. Non-destructive metadata autosave separate from actual media editing, trim/crop/audio export, source preservation and safe recovery. Keep original capture UTC vs edited/export timestamps.
- Gallery clips remain useful without database; SQLite catalog IDs, tags, favorites, bookmarks/markers, source relationships, legacy sidecars and user overrides survive reindex. Gallery additional roots supported, exclusion paths apply relative to each root. Bulk delete uses Recycle Bin/Undo; no real deletion in tests.
- Default filename {game}__{yyyy-MM-dd}-{HH-mm-ss} - {custom_title}.mp4 (omit separator if title empty); metadata title/game edit should not silently rename file. Preserve older templates.
- Previous clip marker anchored to save request; actual publication sound may be later. Retain timestamp freshness and only markers whose anchored frames survive. Historical extra duration shortcuts and auto highlights retired; don't confuse old retired bookmark UI with current shipped marker/tag behavior. Re-audit rather than assume.

## UI and lifecycle
- Dark, AMOLED, Slate, Light, Custom. Semantic tokens, readable focus/selected states, shared scalable controls, keyboard/accessibility, reduced motion. No arbitrary expensive motion/blur. Preserve latest owner-approved design.
- Recording and frontend lifetime independent; normal minimize retains window/player, close-to-tray frees frontend through lifecycle, explicit quit terminates recorder. Windows startup opt-in independent from recording; no registry/shell changes during test builds.
- Status badges: master OFF original; armed/recovering amber; game red; desktop blue. Only state changes update tray/taskbar; neutral visible inactive Save Clip dot, pulsing active dot.

## Privacy and safety
- Protected archive explicitly excluded from Gallery/index/cleanup. Dedicated SteelSeries Moments migration canceled. Never edit/delete/move/recompress private recordings or bulk import old archive. Existing real clips/settings/metadata and SteelSeries install read-only unless separate specific approval.
- No telemetry, cloud upload of clips/audio/transcripts, secrets, or private inventories. No driver reinstall, Windows audio defaults, registry changes, game files or antivirus changes. Test with scratch profiles, generated media, no unsupervised capture. No destructive git cleanup; owner permission to replace agent files is narrow and does not authorize deleting unrelated files.

## Additional compatibility invariants
- Configurable Save Clip (Alt+F10 intended default; verify current setting); no retired duration shortcuts. Request markers anchored to footage, not success chime; explicit missing/deleted target explanation. No unacknowledged save requests: distinguish busy, rejected, accepted, saved/failed.
- Explicit confirmation for media overwrite; metadata edits never overwrite video. Editor validates missing/recycled media before export; Undo restores only actual restored file. Keep one muted Gallery hover preview, bounded asynchronous thumbnails; preserve favorites, tags and virtualized navigation.
- Capture geometry in physical pixels, not WPF DIP; verify real DXGI/output/window association and capability-specific resolution/codec limits. Whole-monitor capture may show other windows; disclose.
- Separate upstream/Personal settings, cache, IPC, installer identity and updates. Keep opt-outs, old templates and settings migration. Do not mutate production profile during dev.


## Shipped v1.1 notification contract
- Visual master + per-event filters. All non-save notifications compact text-only when banners chosen. Successful save alone may be Compact OR Detailed OR ImageOnly. Windows-only plus clip image is supported: Windows notifications + save celebration. Both channels remain optional. Never load/show custom image for status events. Critical capture warnings separately retain readable 5s override. Sound independent.
- Rounded themed semantic header/status glyph and supporting text; Detailed save has labeled metadata rows. No custom media in either. Edges/corners only, 2 physical px inset at rest. Default duration 1.5s, bottom-right, slide, 90% opacity.
- Separate display/position/opacity/size/motion/timing, top-right default, 100% opacity, fade; center allowed. Owner-selected built-in GIF embedded like WAV; 512x512, 85 frames, 295103 bytes, 2.59s loop; SHA256 9a447241222c1412a058495ac183b662314a6e313b504b6f63c7a362cccc9fad. src/QuickClip/Assets/clip-saved.gif, resource QuickClip.clip-saved.gif; null custom path uses default. Use default animation resets path and duration to 2.59, preserves other appearance. Custom images remain supported; image mode opt-in. No startup decode.
- Contain uses actual aspect ratio with common alpha bounds across ALL frames (no jitter); explicit Cover retains cropping. 4 physical px image edge inset, ripple reserves extra space. Custom 24–240 DIP sizes for both, fit oversize to monitor. One overlay replaces previous; click-through/no activation; reduced motion; capture-exclusion/exclusive fullscreen limits. Animated GIF/WebM bounded decoder/scheduler; verify WebP behavior rather than promise universal codec support.
- Motion: Slide, Fade, Grow/shrink, Soft pop, Lift and fade, Expanding ripple wave, None. Asset redistribution permission is not independently verified; preserve caveat and resolve before next redistribution.

# Unified editor UX handoff

The feature branch is `feat/unified-editor-ux`. Its base is `5bc5d3a237ce39824006a76eb0f756d14fd753ef`, the published performance and production-integration handoff on `origin/custom/main`.

## Gallery and editor

A gallery card opens the existing MPV editor with its trim, tracks, crop, encoding and export controls. The editor starts paused as before. **View mode** (F11) expands the video area and keeps transport and a seek slider. **Edit mode** or Escape restores the editing panels and the same clip, playhead and draft. Returning to the gallery pauses playback and keeps the editor draft in memory. Reopening that clip resumes the editing workspace. Opening a different clip or closing QuickClip prompts when edit settings would be discarded. The classic player remains available in the overflow and context menus.

The gallery uses larger, virtualized cards with visible titles, a three-dot menu, selection checkboxes and Ctrl-click. Enter opens a focused card and Space toggles its selection. **Show poster previews on hover** is off initially. When enabled, a 750 ms hover shows the existing cached image at a larger size. The preview uses no decoder or per-hover process.

Title and game classification can be saved inline in the gallery and in the editor’s **Clip details** accordion. Both use the existing local metadata store and retain capture time. A file whose size or timestamps changed while editing details must be reopened. File names and video data stay intact when saving metadata.

Gallery, editor and classic player use a dimmed, owned WPF confirmation with filename or batch count, a red Delete action and Cancel as the default. The existing Recycle Bin and Undo service handles deletion. The editor closes the clip and returns to the gallery after a successful delete. Held Delete keys cannot cascade from the editor or classic player.

Editor accordions reserve an animated fraction of their measured content height. Output moves with the Encoding panel throughout the transition. Reversals start at the current animation position, and Windows reduced-motion preferences use immediate layout changes.

## Recording settings

Settings cards use two columns when the available client width is at least 1120 WPF layout units, with a single centered column at smaller widths. The primary recording card contains source, monitor, resolution, FPS, encoder and NVENC preset. An advanced monitor backend control applies to monitor capture and fallback. **Advanced Recording Diagnostics** keeps the existing native counters, refresh and timing sampling. Samples still go to the local log.

**Automatic** maps to the existing game-first preference and uses the selected monitor as fallback. **Selected monitor** maps to the existing monitor-only preference. Strict **Game only** is shown as unavailable because the current policy always has a monitor fallback. Game-window capture uses WGC, which can show a yellow indicator on Windows 10. Implement strict game-only and per-game source policies in the later recorder integration.

Encoder labels distinguish AVC, HEVC and AV1, and identify NVENC, AMF, Quick Sync and Windows Media Foundation. The picker groups direct hardware encoders ahead of the Windows compatibility paths and uses the existing FFmpeg capability probes. Saved unavailable choices are marked unavailable and retained until the user selects another encoder. The preset picker shows Performance P2/HQ, Balanced P3/HQ and Quality P4/HQ with the existing profile defaults and IDs.

The AV1 encoder IDs and export flags are preserved. The RTX 3070 returned “No capable devices found” for AV1 NVENC in the synthetic probe and the picker excluded it. The available hardware could not verify AV1 recording/export on capable NVIDIA, AMD or Intel devices. Intel AV1 QSV entered the source with `Export: false`. The export builder has explicit QSV tuning for AVC and HEVC, and the source history does not establish an AV1 export qualification result. Keep the gate until an actual AV1 QSV export, re-probe, playback and audio-track test passes on a supported Intel GPU.

## Verification and integration

- Personal full suite passed 248 tests with 16 opt-in skips. Targeted tests covered layout displacement in the immediate path, delete batches and Undo collisions, naming and preset compatibility. Final UI cleanups were followed by the targeted suite and a Personal Release build/runtime check.
- The offscreen harness loaded 1,300 synthetic clips, with 4 of 434 gallery rows realized at first paint. It opened the unified editor, preserved playhead and trim across modes, and exported a three-audio-track montage. Encoding moved Output from 216.4 to 461.4 layout units with an intermediate sample and returned to 216.4 on collapse. Recording stayed off. This is a virtualization observation with synthetic content, not a live recording performance benchmark.
- Remaining manual checks include actual pointer and keyboard interaction with the delete modal, Recycle Bin round trips, mixed-monitor DPI, rapid accordion reversal and live recording overhead.
- MainWindow wires the editor’s return to gallery. EditorView, GalleryView and PlayerView use the new controls and dialogs. SettingsView and encoder display metadata are the shared review points for the next integration. Recorder, GameDetector, AppSettings and native capture source are outside this change.

For the normal development workflow, prepare the dependencies in the feature worktree and use the [Personal development launcher](development.md). For an already built copy, set `QUICKCLIP_PERSONAL_DATA_DIR` to an absolute scratch profile in the worktree and launch the Release executable. Set `DOTNET_ROOT` to the x64 SDK directory if using a workspace SDK.

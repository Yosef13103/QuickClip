# Unified editor UX handoff

The feature branch is `feat/unified-editor-ux`. Its base is `5bc5d3a237ce39824006a76eb0f756d14fd753ef`, the published performance and production-integration handoff on `origin/custom/main`.

## Gallery and editor

A gallery card opens the existing MPV editor with its trim, tracks, crop, encoding and export controls. The editor starts paused as before. **View mode** (F11) expands the video area and keeps transport and a seek slider. **Cinema** (Shift+F11) also hides transport and keeps Space for playback. Exit Cinema or press Escape to restore the previous mode. The same MPV instance, clip, playhead and draft stay active. Returning to the gallery pauses playback and keeps the editor draft in memory. Reopening that clip resumes the editing workspace. Opening a different clip or closing QuickClip prompts when edit settings would be discarded. The classic player remains available in the overflow and context menus.

The gallery uses larger, virtualized cards with visible titles, a three-dot menu, selection checkboxes and Ctrl-click. Enter opens a focused card and Space toggles its selection. **Show poster previews on hover** is off initially. When enabled, a 750 ms hover shows the existing cached image at a larger size. The preview uses no decoder or per-hover process.

Title and game classification can be saved inline in the gallery and in the editor’s **Clip details** accordion. Both use the existing local metadata store and retain capture time. A file whose size or timestamps changed while editing details must be reopened. File names and video data stay intact when saving metadata.

Use **Edit title, game or tags…** in a clip’s gallery menu to add or remove tag chips and mark it as a favorite. **Filter by tag** matches partial tag text without regard to case, and **Favorites only** combines with that filter. Filters clear the selection when applied. Bulk actions in a filtered gallery stay within the visible results.

Use **Bookmark** next to the trim controls to add a labeled bookmark at the playhead. Click a marker to seek and edit its label in **Clip details**. The timestamp buttons in that section also seek and can be reached by keyboard. Click **Save details** to commit changes or **Cancel** to discard them. Bookmarks use offsets from the start of the saved media and stay independent of the trim handles.

Export requires saving or canceling any open metadata draft. An editor export inherits the title, game, tags, favorite and original capture timestamp. Bookmarks inside the exported interval are shifted to its new start. The end of the interval is exclusive. Undoing **Save over original** restores the original bookmark offsets. The metadata store is local to the profile and keyed by media path and file fingerprint. Renaming the title in QuickClip keeps its metadata. An external file move, rename or replacement can leave the local record behind. Tags, favorites and bookmarks travel to the editor export through the local store. Another QuickClip profile or an external media player cannot read them from the video.

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
- The organization follow-up passed 252 automated tests with 16 opt-in skips. Tests covered legacy JSON, file replacement, tag filtering, bookmark validation and trim projection, and saving all three fields through the WPF form. A new synthetic render run verified Cinema and return to View mode on the same clip, another layout displacement sample, and a three-track montage export. Native capture and libmpv loaded in the Personal Release runtime check.
- Remaining manual checks include actual pointer and keyboard interaction with the delete modal, Recycle Bin round trips, mixed-monitor DPI, rapid accordion reversal and live recording overhead.
- For a manual organization smoke test, use a disposable clip. Add a tag and favorite in the gallery, filter by both, and open it. Add bookmarks on either side of a trim boundary, save details, and click a marker to seek. Switch between Edit, View and Cinema. Export the trim, reopen the result, and check that only the bookmark inside the trim is present at its shifted offset. Restart QuickClip and verify both records and their original capture timestamp.
- MainWindow wires the editor’s return to gallery. EditorView, GalleryView and PlayerView use the new controls and dialogs. SettingsView and encoder display metadata are the shared review points for the next integration. Recorder, GameDetector, AppSettings and native capture source are outside this change.

For the normal development workflow, prepare the dependencies in the feature worktree and use the [Personal development launcher](development.md). For an already built copy, set `QUICKCLIP_PERSONAL_DATA_DIR` to an absolute scratch profile in the worktree and launch the Release executable. Set `DOTNET_ROOT` to the x64 SDK directory if using a workspace SDK.

## Future integration

- **Recording profiles** belong in the game catalog’s details pane. Show global defaults with explicit per-game overrides and a reset action. Wire the pane after the catalog and recorder expose a stable game identity and profile API.
- **Recording quality** is the home for a custom bitrate and supported VBR, CBR and CQP controls. Show fields according to the selected codec and rate control once the recorder can persist, apply and report the effective settings.
- **Workflow and hotkeys** can group replay lengths, an explicit start/stop action for full-session recording, and a live **Add bookmark** hotkey. A live bookmark needs a recorder session ID and a timestamp from the same clock as replay packets. On save, resolve the bookmark against the actual emitted media start and end, discard offsets outside the clip, and store the remaining labeled offsets in `ClipDetails`. Apply that mapping before export trim remapping.
- **Notifications and post-game** can group optional post-game gallery, save sound, Windows toast and mini indicator. Each output needs its own toggle and should use the actual save result.
- **Additional capture sources** can hold a future optional Discord track alongside the primary game source once simultaneous recording can expose both sources and their costs. Keep per-source status in the diagnostics area.

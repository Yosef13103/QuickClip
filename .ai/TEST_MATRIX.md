# Acceptance tests and quantitative performance checklist

## Build / unit / media
- Fresh clone builds on Windows 10 22H2 with declared toolchain. Existing tests pass; newly added tests validate audio track enumeration, output naming, migration, capture policy and DB migrations.
- Synthetic media fixture with 3 or more independent audio tones, distinct known timestamps/volumes. Saved master contains individually named audible streams; exported files preserve selected volume/mute; copy-mode does not inadvertently downmix.
- Test encoder HEVC NVENC at native 3440x1440 60fps, then 1080p at native resolution, as supported by hardware. Inspect ffprobe, visual actual width/height/fps/stream metadata, no black bars caused by unintended crop.
- End-to-end capture: 90s buffer, save while game visible; Alt-tab 20s to Discord and back, clip must preserve pre-alt-tab gameplay; Discord popup must not switch game-first target. Record minimized-exclusive-fullscreen behavior explicitly rather than promising frames the game does not render.

## Audio routing / devices
- Render endpoint selection independent of default endpoint; Windows 10 endpoint loopback works without process loopback.
- Preset A: physical Arctis Pro Game + Sonar Chat + Sonar Mic, separately adjustable in editor. Preset B: Sonar Gaming + Sonar Media + Sonar Chat + Sonar Mic, when devices are actually routed with audio. Detect likely duplicated audio when both physical output and contributing virtual endpoint selected.
- Solo/mute tracks: play and export expected independent tones; source/clip tracks remain isolated. USB device unplug/replug and Sonar restart do not crash or corrupt buffer; status explains missing source. Silence/no data not misreported as recording everything.
- Sample-rate differences: no growing A/V drift after 10+ min; muted mic still available in master if recorded and only muted at export. Audio device ID persistence and renamed endpoint display.

## Capture modes
- Game-first: game focus -> Discord focus -> game focus; primary game buffer intact.
- No game: configurable explicit desktop monitor, Discord monitor, Discord window; move Discord between primary and secondary, use popout, multiple Discord windows. App manual pin overrides detected game if intentionally selected.
- Dual: same hotkey makes 2 distinct video files on one event, linked in library, different native dimensions, consistent capture timestamp/audio alignment. Demonstrate 2 hardware encoders actually run and report resource cost. If encoder rejects composite width/resolution, fallback rather than corruption.
- Resume after sleep/display disconnect, resolution switch, device changes, GPU encoder failure; stale video detected, errors surfaced, no silent unbounded memory growth.

## Library/editor/metadata
- Capture originally 2023-08-04; edit/export in 2026. User label/game/date persist; expected filename `PlateUp__2023-08-04__06-44-24_custom-title.mp4`, with no false edit-day timestamp. Untitled default no extra underscore.
- Filename sanitizer handles reserved Windows names, slashes, `:` and other invalid chars, whitespace, Unicode, long titles, collisions and duplicate names. Never overwrite without explicit user action.
- Search/filter by game, arbitrary tags, favorite, title, date range, source, export state. Rename game `Update` -> `Discord` for selected clip and optionally an application rule; no mass reclassification without confirmation.
- Render to original, 10/50/100 MB, arbitrary user size, expected estimate before export, confirm actual size and preserve source after failures.
- Import existing `E:\Stuff\Personal\Clips` in place with nested archive excluded and no change to filesystem by default. DB reset/rebuild stable id strategy. Library recoverable if SQLite missing, no absolute paths published.
- Trim/overwrite backups with undo; Recycle Bin accounted as disk usage; retention opt-in and no removal of only copy.

## Legacy inventory
- `--dry-run` under read-only ACL produces summary but does not create files inside sources or touch GG. Locate embedded/split tracks where possible, no unsupported 'recoverable' claims; uncertain records flagged. A single approved copied test recording converts with stream verification; refusal if destination lacks space.

## Performance measurements (local only)
Protocol: same game and same scene, 3440x1440 60fps HEVC, same effective bitrate, 90s buffer and Sonar running in every case. Warm up system; disable whichever competing recorder is not under test. Measure moments+Sonar baseline, QuickClip+Sonar single, optional QuickClip+Sonar dual (both 3440x1440 game and 1080p companion). Repeat with editor closed and with clip editor active. Report mean and peak working set, private bytes/commit, encoded ring bytes, GPU 3D/Copy/Video Encode utilization, VRAM, CPU total and process CPU, game FPS and 1% lows/frametimes, dropped frames and corrupted/clipped sounds, start-up and sleep/resume reliability. Run at least 10 min, a longer 30-60 min test if possible to detect leaks.

Do not claim 'lightweight' solely from code language, NVENC presence, or `EmptyWorkingSet()`. Note idle overhead and resource cost of extra streams. Optional features with material cost should have visibly measured/estimated cost in UI.

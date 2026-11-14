# QuickClip Personal

**Automatic game clipping, with editable audio and clips that just play.**

QuickClip Personal is a Windows instant-replay recorder, clip library and video editor. It waits for an **approved game**, keeps a rolling video buffer in memory, and saves the last moments of gameplay when you press a hotkey. The result is ready to watch, trim, remix or share.

**[Download QuickClip Personal v1.0.0](https://github.com/Yosef13103/QuickClip/releases/tag/personal-v1.0.0)** · [Release notes](docs/releases/personal-v1.0.0.md) · [Building from source](docs/development.md) · [GPL-3.0 license](LICENSE)

> **This is the Personal fork, not the upstream application.** Release development lives on [`custom/main`](https://github.com/Yosef13103/QuickClip/tree/custom/main); `main` preserves the separate upstream-compatible line. Original project: [swiiftex/QuickClip](https://github.com/swiiftex/QuickClip). Features below describe the **Personal v1.0.0** release unless noted otherwise.

## The workflow

1. **Set up:** Install QuickClip Personal. In Settings, choose your recording, audio and save preferences and approve the game libraries/displays you want to use.
2. **Play:** Enable Instant Replay. QuickClip recognizes eligible games and follows the authorized game display; no game means no automatic game capture.
3. **Clip:** Press **Alt+F10** (configurable) or **Save Clip**. Recording uses an encoded rolling buffer, so you don't need to start recording before something happens.
4. **Find it:** Open the Gallery to browse clips by game/date, search and organize them, and manage them without touching unrelated video folders.
5. **Edit:** Trim, crop, adjust individual audio stems, add sounds, preview and export. Your original remains available unless you explicitly choose to overwrite it.
6. **Share:** An exported new clip has a single, ordinary audio soundtrack called **Mix**. No special player is required to hear the complete recording.

## Highlights

| Area | QuickClip Personal v1.0.0 |
| --- | --- |
| **Game-aware capture** | Approved game libraries, Steam/Epic discovery, manually approved executables, safe game/launcher classification, game switching and approved-display following |
| **Instant replay** | Configurable rolling RAM buffer, hotkey saves, hardware-accelerated encoding, capture recovery and clear recording status |
| **Editable *and* playable audio** | One default **QuickClip Full Mix** for normal players **plus separate Game/Chat/Mic/etc. tracks** when configured |
| **Gallery** | Grouping by game/day, SQLite-backed indexing, search, tags, annotations, projects, multi-select, Recycle Bin deletion and Undo |
| **Editor** | Frame-oriented playback, timeline and waveforms, trimming, aspect/crop controls, volume/mute/solo, added audio, export or save over original |
| **Notifications** | Optional Windows notifications or in-app overlays, positioning, animation, image-only layouts, animated GIF, transparent WebM and custom sounds |
| **Windows integration** | Per-user installer, optional startup, tray controls, separate Personal profile and a self-contained portable release |
| **Appearance** | Dark/AMOLED/Slate/Light themes, high-DPI scaling, accessible confirmations and adjustable interfaces |

### A raw recording has both kinds of audio

A newly saved clip with several configured sources uses a **single video file**:

```text
QuickClip recording.mp4
├── Video
├── Audio 1  QuickClip Full Mix  [default; ordinary players]
├── Audio 2  Game                [editable]
├── Audio 3  Chat                [editable]
└── Audio 4  Microphone          [editable]
```

The source tracks are examples; actual tracks depend on your selected Windows audio endpoints. **QuickClip's Editor recognizes the default Full Mix and hides that generated mix from the editable stem controls**, preventing double playback. Exporting **as a new file** produces one complete mixed audio stream titled **Mix**. **Save over original** rebuilds the playable mix and preserves editable stems; the prior original is sent to the Recycle Bin for recovery.

This avoids saving two copies of the video just to get separate editing tracks. Older recordings are not silently altered.

### Automatic capture without surprise screen recording

- **Game-first:** QuickClip looks for a verified, eligible game rather than treating Discord, launchers or every fullscreen app as a game.
- **Explicit permissions:** Game discovery and display capture depend on the paths and displays you approve. An unknown or denied display is not silently substituted.
- **Display following:** The Personal release captures the **entire authorized display** containing the game (subject to its selected crop), rather than secretly capturing other monitors. A covering Discord window can therefore appear *in the video*; QuickClip retains the game association when Discord receives focus.
- **Safe handoff:** Switching to another verified foreground game starts a new capture epoch/buffer; footage from two unrelated game/display sessions isn't joined into one accidental replay.
- **Recovery:** Temporary capture failures and certain device/display changes trigger controlled recreation rather than blindly continuing with invalid frames.
- **Manual desktop:** Available as a distinct, explicit capture decision rather than an automatic fallback to unrelated desktop content.

> **Capture scope matters:** Personal v1.0.0 is primarily **approved-display capture driven by the game detector**, not invisible game-only graphics hooking. One display is captured at a time. Simultaneous displays and independent Discord companion capture are planned experiments, not shipping features.

### Video and performance controls

Choose a replay length, frame rate, target resolution, aspect/crop, bitrate, format and encoder/preset according to your hardware. QuickClip's native C++ engine integrates Windows capture, GPU scaling, FFmpeg and an encoded packet ring, avoiding full-video re-encoding just to save a replay. The Settings diagnostics show the active capture source and support performance investigation.

NVIDIA NVENC has been owner-tested on an RTX 3070. AMD AMF, Intel Quick Sync/QSV and Windows fallback paths exist, but **cross-vendor hardware compatibility is not yet certified**; available formats and hardware encoders vary by GPU and driver. Performance, especially 1% lows, depends on the selected capture mode, resolution, FPS, codec and preset.

See [recording diagnostics and benchmarks](docs/recording-diagnostics.md) for reproducible measurements and limitations.

## Install

**Requirements:** Windows 10 **22H2 / build 19045+** or Windows 11, **x64**. Hardware encoding requires compatible graphics drivers and encoder support.

| Download | Best for |
| --- | --- |
| **[QuickClip-Personal-1.0.0-setup.exe](https://github.com/Yosef13103/QuickClip/releases/download/personal-v1.0.0/QuickClip-Personal-1.0.0-setup.exe)** | Normal per-user install; Start menu/uninstall registration; recommended |
| [QuickClip-Personal-1.0.0-win-x64.zip](https://github.com/Yosef13103/QuickClip/releases/download/personal-v1.0.0/QuickClip-Personal-1.0.0-win-x64.zip) | Self-contained portable app; extract the entire folder before launching |
| [SHA256 checksums](https://github.com/Yosef13103/QuickClip/releases/download/personal-v1.0.0/SHA256SUMS.txt) | Verify release downloads |

The installer uses `%LOCALAPPDATA%\Programs\QuickClip Personal`. The release bundles the .NET runtime and required media components; you don't need the SDK to run it.

- **Personal settings:** `%APPDATA%\QuickClip Personal`
- **Local data/cache:** `%LOCALAPPDATA%\QuickClip Personal`
- **Default clips:** your Windows Videos directory under `QuickClip Personal` (configurable in Settings).
- **Optional isolated portable data root:** set `QUICKCLIP_PERSONAL_DATA_DIR` to an absolute directory before starting a Personal build. This is useful for testing; don't put persistent data inside a folder you routinely delete.
- **Updates:** automatic upstream updating is **disabled** for Personal builds. Install newer Personal releases explicitly from this repository.
- **Explorer context menu:** stock QuickClip's Explorer registration is separate; Personal does not automatically install the upstream shell extension.

**First launch:** Recording/automatic hotkeys are opt-in for a fresh Personal profile. Enable Instant Replay after checking your audio endpoints, game permissions and display selection.

## Gallery, Editor and notifications

**Gallery:** browse/search by game and date, add titles, tags and other annotations, create projects, open clips for editing, and delete through the Windows Recycle Bin with Undo where available. Your existing SteelSeries Moments archive is **not automatically imported, migrated, renamed or cleaned**.

**Editor:** inspect independent audio stems, adjust volumes, solo/mute sources, trim in/out points, choose crop/resize/encoding options, add a song/effect on the timeline, and export a new MP4 or other supported container. Fast stream-copy is used when the requested edit permits it; transformations that require encoding use FFmpeg. Overwriting is an explicit choice, with Recycle Bin recovery for the previous file.

**Notifications:** independently enable sounds and visual feedback, choose display/position/style/motion, or use a local image, animated GIF or transparent VP9 WebM for a custom overlay. Optional custom sounds support commonly used local audio formats. Validation limits apply so a huge animation does not stall the app.

## Building and contributing

The repository remains open-source under GPL-3.0. For development, check out **`custom/main`** and keep your experimental work on a short-lived feature branch.

You need **.NET SDK 10**, **Visual Studio C++ Build Tools** with the Windows SDK, and dependencies provisioned by the repository script. On Windows 10, the pinned `.7z` libmpv archive also needs a working 7-Zip-compatible extractor (such as NanaZip's CLI).

```powershell
git clone https://github.com/Yosef13103/QuickClip.git
cd QuickClip
git switch custom/main
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\get-deps.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 -BuildOnly
dotnet test QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true
```

For a normal installed Personal release (installer and portable ZIP):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1 -Personal
```

**Developer notes:**
- [Development setup, profile isolation and watcher](docs/development.md)
- [Capture policy and game/display ownership](docs/v1-game-display-following.md)
- [Game catalog architecture](docs/game-catalog-integration.md)
- [Notification WebM transparency](docs/transparent-notification-webm.md)
- [Custom notification sounds](docs/custom-notification-sound.md)
- [Release notes and known limitations](docs/releases/personal-v1.0.0.md)

The Windows CI validates strict **Personal** and **Upstream** Release builds and headless tests. Real GPU, microphone, monitor hotplug and game performance tests still require physical Windows hardware. Local `.quickclip-local` development worktrees, NuGet downloads and SDKs are scratch—not application data—but run `git worktree list` and preserve unsaved changes before deleting them.

## What's next?

**V1 is the owner-accepted game → save replay → Gallery → Editor → export workflow.** Future releases may add simultaneous multi-monitor recording, synchronized independent clips/composites, opt-in Discord companion capture when no game is active, more launcher providers, managed originals/retention, and further Intel/AMD/driver compatibility validation. None of these are prerequisites for using V1.

## Credits and license

QuickClip Personal is a [GPL-3.0](LICENSE) fork based on **[swiiftex/QuickClip](https://github.com/swiiftex/QuickClip)**. It uses **[FFmpeg](https://ffmpeg.org/)**, **[libmpv](https://mpv.io/)** and **[Inno Setup](https://jrsoftware.org/isinfo.php)**, with their applicable licenses. This fork retains upstream attribution and keeps its Personal release/build identity distinct from upstream.

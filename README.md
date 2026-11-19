# QuickClip Personal

**Save the moment, not the whole session.**

QuickClip Personal is a free, open-source instant-replay recorder, clip library and video editor for Windows. I built this fork to make everyday gameplay clipping easier: capture the last few moments with a hotkey, keep game, chat and microphone audio independently editable, and share clips without juggling several applications.

[Download the latest release](https://github.com/Yosef13103/QuickClip/releases/latest) · [What's new in v1.2.2](docs/releases/personal-v1.2.2.md) · [Report an issue](https://github.com/Yosef13103/QuickClip/issues) · [License](LICENSE)

## What's new in v1.2.2

- Resolve shared executable ownership directly in Games settings.
- Keep other eligible game executables available when a shared file needs attention.
- Improve shared-file labels and exclude a known dedicated-server executable from automatic targeting.

See [v1.2.2 release notes](docs/releases/personal-v1.2.2.md) for details.

## How it works

1. **Set up:** Choose a clip folder, audio devices, recording quality, permitted displays and game libraries. Guided setup helps new users get started.
2. **Play:** Enable Instant Replay. QuickClip detects eligible games and records their authorized display into a rolling encoded buffer. Manual desktop capture is also available.
3. **Clip:** Press **Alt+F10** (configurable) or select Save Clip to save the previous moments.
4. **Find and edit:** Browse the Gallery, rename clips, add tags and bookmarks, trim, crop and mix individual audio tracks.
5. **Share:** Export a standalone video or select a sharing profile to create a size-limited MP4 ready to attach or paste into a supported app.

## Features

| Area | Features |
| --- | --- |
| Game discovery | Steam, Epic, GOG, Xbox, EA, Riot, Battle.net, Ubisoft, Rockstar, Heroic, Origin, Amazon Games and itch.io; local collections and exact executable selection |
| Recording | Game-aware replay, manual desktop capture, configurable duration, resolution, FPS, bitrate and encoding; GPU-dependent hardware acceleration |
| Audio | Independent game, chat and microphone sources; playable default mix plus editable stems; endpoint selection and microphone testing |
| Gallery | Search, date/game grouping, SQLite-backed indexing, tags, favorites, bookmarks, projects, multi-select, Recycle Bin and Undo |
| Playback and editing | Gallery hover previews, Player, persistent edit drafts, fullscreen playback, trim, crop, audio levels and additional sounds |
| Sharing and export | Named sharing profiles, custom decimal MB limits, Original or capped exports, clipboard attachments, queued jobs, cancellation and retry where safe |
| Settings and feedback | Basic/Advanced modes, guided setup, themes, custom palettes, recording HUD, configurable sounds and visual notifications |
| Windows integration | Tray controls, optional startup, per-user installation, portable distribution, stable-release checks and opt-in diagnostics |

### One recording, multiple audio tracks

A new recording with several enabled audio sources can contain a default **QuickClip Full Mix** alongside independently editable game, chat and microphone tracks. Ordinary players can play the mix immediately; QuickClip's Editor exposes the individual stems without mixing the default track twice.

Exporting as a new file produces one mixed soundtrack. Saving over the original rebuilds its default mix while preserving editable stems, with Recycle Bin recovery for the previous file. Existing recordings are not silently rewritten.

### Share without guessing file sizes

Create named sharing profiles for your preferred services and their upload limits. Choose a profile or enter a one-off cap. QuickClip creates a compatible MP4 with a single mixed soundtrack, measures the finished file and rejects outputs that exceed the applicable byte limit. Capped editor exports also support presets and custom sizes.

Lower caps can reduce quality or take longer to encode; service-side limits and clipboard attachment support vary. Media Jobs shows progress and lets you cancel work or reveal a completed file for manual attachment. One worker is the default; up to two can be configured, with new work restricted while recording.

## Download and install

**Requirements:** Windows 10 22H2 (build 19045+) or Windows 11, x64. Hardware encoding requires compatible graphics hardware and drivers.

Open the [latest GitHub release](https://github.com/Yosef13103/QuickClip/releases/latest) and choose an asset:

| Asset | Purpose |
| --- | --- |
| `QuickClip-Personal-1.2.2-setup.exe` | Recommended per-user installer; supports a writable local installation folder |
| `QuickClip-Personal-1.2.2-win-x64.zip` | Portable build; extract the entire archive before running |
| `SHA256SUMS.txt` | SHA-256 checksums for both packages |

The .NET runtime and required media components are bundled. No SDK is required to run the app.

Upgrading from v1.1.0? Download and run the setup EXE for an installed copy, or update a portable copy manually. Supported installed copies with the new installer inventory can use the explicitly initiated in-app update handoff for future releases; unsupported layouts and portable copies retain manual installation. Updates do not silently install in the background.

### Local files and privacy

- Default install: `%LOCALAPPDATA%\Programs\QuickClip Personal`
- Settings: `%APPDATA%\QuickClip Personal`
- Local data/cache: `%LOCALAPPDATA%\QuickClip Personal`
- Default clips: the Windows Videos folder under `QuickClip Personal` (configurable).
- Optional isolated data root: set `QUICKCLIP_PERSONAL_DATA_DIR` to an absolute directory before launching.

Recording is opt-in for new Personal profiles. Display permissions apply to automatic and manual capture. QuickClip captures the authorized **display**, not an isolated game-rendering hook: other windows covering the game may appear in footage. Only one live display is captured at a time. Existing external media archives are not automatically migrated or deleted. Sharing creates local files; QuickClip does not upload clips on your behalf.

Production verbose diagnostic logging is off by default. See [diagnostics](docs/diagnostics.md) for troubleshooting.

## Compatibility and limitations

- Hardware encoding, monitor switching, audio routing and fullscreen behavior vary by GPU, driver and Windows configuration. No universal performance improvement or cross-vendor certification is claimed.
- Some games or launcher installations require an exact executable or manual folder selection. Explicit exclusions remain respected.
- On-screen recording HUD and saved-clip overlays can appear in recordings. Capture exclusion is not supported for these overlays; use the independent feedback controls to disable them during recording.
- Size caps are ceilings, not a promise of exact file size or acceptance by every receiving app.
- Independent companion capture, simultaneous multi-display recording and isolated game-hook capture are not included.

See the [v1.2.0 release notes](docs/releases/personal-v1.2.0.md) for details.

## Build from source

Development takes place on [`custom/main`](https://github.com/Yosef13103/QuickClip/tree/custom/main); `main` retains the separate upstream-compatible line. You'll need the .NET 10 SDK, Visual Studio C++ Build Tools and Windows SDK, plus a 7-Zip-compatible extractor on Windows 10.

```powershell
git clone https://github.com/Yosef13103/QuickClip.git
cd QuickClip
git switch custom/main
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\get-deps.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 -BuildOnly
dotnet test QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true
```

To build the installer and portable package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1 -Personal
```

See [development setup](docs/development.md), [recording diagnostics](docs/recording-diagnostics.md) and [sharing and media jobs](docs/wave2-sharing-media-jobs.md). Bug reports and contributions are welcome through [GitHub Issues](https://github.com/Yosef13103/QuickClip/issues).

## Credits and license

QuickClip Personal is an independently maintained [GPL-3.0](LICENSE) fork of [swiiftex/QuickClip](https://github.com/swiiftex/QuickClip), with a separate release and application profile. Thanks to the upstream project and the FFmpeg, libmpv and Inno Setup projects. Store logo artwork and trademark notices are documented in [Store logos](docs/store-logo-notices.md). Third-party components retain their applicable licenses and attribution.

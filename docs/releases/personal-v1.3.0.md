# QuickClip Personal v1.3.0 — Discord capture and expanded Gallery

This release brings the accepted Development experience to the stable Personal channel. It expands opt-in recording sources and library organization while retaining QuickClip's game-first instant replay, separate audio stems and established sharing and editing workflows.

**Windows 10 22H2 (19045+) / Windows 11 · x64 · self-contained**

## Recording sources and per-game preferences

- **Discord companion capture:** explicitly enable Discord recording and choose when it records: during calls, while Discord is open, or on manual request. QuickClip captures verified Discord windows and pop-outs in a single replay canvas while keeping the normal game/manual-desktop priority. Discord recording stops when source permissions or live window identity can no longer be verified.
- **Games first:** eligible games retain recording priority; no second encoder runs for Discord alongside an active game recording. QuickClip does not capture another display to work around missing permission.
- **Per-game recording profiles:** override supported replay/video settings for selected games without changing global defaults, and restore inherited settings when desired. Manual desktop capture retains its separate global policy.
- **Settings > Sources:** simpler Apps and Games grouping, a Discord status card reflecting the selected capture mode, stable game search and clear permission controls.

## Gallery and organization

- Game-scoped and global tags, color-coded labels, virtual collections and optional recording-collection preferences.
- Nested physical folder navigation is based on each approved source path, so same-named subfolders under different games stay distinct. Older Discord clips can appear together with newly associated Discord recordings without rewriting original files.
- Resizable Gallery folder sidebar, clearer management actions and consistent controls across the settings, Player and editor.
- Existing clips, independent audio stems, persistent editor drafts, share/export profiles and Recycle Bin / Undo remain supported.

## Installing and updating

Download **QuickClip-Personal-1.3.0-setup.exe** for an installed Personal copy or **QuickClip-Personal-1.3.0-win-x64.zip** for portable use. Verify both against **SHA256SUMS.txt**.

Supported installed copies can use the existing user-initiated in-app update flow; portable installations and unsupported layouts can update manually. The Personal app identity and default profile/library locations are unchanged. Existing clips are not migrated or rewritten simply by installing this release.

## Limitations and safety

- Discord capture is opt-in and can include private chats or profile content shown in the authorized windows. Windows 10 may show its required capture border; it is not silently bypassed.
- The Discord recording canvas is currently fixed for each recording session; automatic largest-resolution replay, cross-monitor epoch retention and automatic overlapping-clip stitching remain **experimental and are not included**.
- Game capture still records one authorized display at a time, not an isolated game hook. Other applications, multi-display composition and automatic game event detection are not promised.
- Hardware encoding and capture performance vary by system. This release makes no universal FPS or GPU-overhead claim.
- Media files remain local. QuickClip does not upload clips or silently alter existing SteelSeries Moments archives.

[Project README](https://github.com/Yosef13103/QuickClip) · [Issues](https://github.com/Yosef13103/QuickClip/issues) · [GPL-3.0 source](https://github.com/Yosef13103/QuickClip/blob/custom/main/LICENSE)

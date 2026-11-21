# QuickClip Personal v1.3.0 — Discord capture, Sources and Gallery

**Windows 10 22H2 (19045+) and Windows 11 · x64 · self-contained**

This release promotes the accepted QuickClip Development experience, building on the v1.2.2 maintenance release.

## Apps, Discord and Games

- Dedicated **Apps** section in Sources, with a Discord card showing its actual capture mode and enabled state; Games retain their own catalog and search.
- Opt-in Discord companion recording with configurable **During calls**, **While open**, and **Manual** modes. Game capture takes priority; existing recording master switch, display/source permissions and window ownership checks continue to apply.
- Discord main-window and authorized pop-out handling, including experimental Windows 10 cloaked-window support. Behavior depends on Windows graphics capture capabilities and can vary by system.
- Game library discovery, recording-profile and executable-ownership improvements, including the shared-executable fixes in v1.2.2.
- Games search retains typing focus while results refresh.

## Gallery, player and editor

- Unifies associated Discord footage and unassociated legacy Discord footage for display when the identity is unambiguous, without changing original media or grant metadata.
- Shows nested physical clip folders under the correct game, keeping similarly named folders in different games separate.
- Draggable Gallery folders divider with saved width, updated player controls, and improved settings presentation.
- Existing independent audio stems, editor, sharing profiles, file-size-limited MP4 export and clip organization remain available.

## Getting the update

Download **QuickClip-Personal-1.3.0-setup.exe** for a supported installed copy, or **QuickClip-Personal-1.3.0-win-x64.zip** for portable use; verify using **SHA256SUMS.txt**. Do not extract the ZIP over a running installation. The installer maintains the Personal installation identity and existing user settings; portable copies update manually.

## Limitations and verification

- Ordinary game-aware display recording still records the explicitly authorized display; it does not use an isolated game-rendering hook. Other windows on that display may appear.
- Discord uses the existing fixed per-session encoding canvas. Automatically choosing the largest resolution within a replay, stitching across capture-engine restarts, crash-recoverable disk replays, and auto-joining overlapping replays remain **experimental research**, not shipped features.
- Windows and graphics drivers differ; Discord cloaked-window behavior, multi-window transitions, game/monitor behavior, GPU load and audio routing are not universally certified.
- Real-world acceptance performed by the owner covers their Development configuration, not every Windows 10/11 system. Automated CI checks do not replace hardware tests.
- Older releases and their assets remain available. QuickClip does not silently rewrite existing recorded media.

QuickClip Personal is an independently maintained [GPL-3.0](../../LICENSE) fork of [swiiftex/QuickClip](https://github.com/swiiftex/QuickClip). Third-party license and artwork notices remain in the repository.

# QuickClip Personal v1.2.0 — Game discovery, editing & sharing

This release makes QuickClip easier to set up, better at finding games and more flexible when editing and sharing clips. It brings together expanded discovery, a streamlined settings experience, persistent editing and a new size-aware media workflow.

**Windows 10 22H2 (19045+) / Windows 11 · x64 · self-contained**

## Game discovery and recording

- Discover games across 13 supported launcher providers, including Steam, Epic, GOG, Xbox, EA, Riot, Battle.net and Ubisoft. Use compact launcher tiles with individual refresh controls.
- Add a specific executable, a single game installation or a collection of games. Incremental scans make completed verified installations eligible without waiting for the entire scan. Alternate executables and exact manual entries are supported.
- Review and restore a previously excluded collection explicitly without clearing unrelated exclusions. Display permissions apply to both manual and game-aware recording.
- Improved recording feedback and save recovery, clearer microphone controls and independent visual/audio notification channels. Recording HUD and saved-clip overlays can appear in footage; unsupported capture exclusion has been retired.

## Setup and appearance

- Guided first-run setup and a simpler Basic settings mode for everyday use; Advanced retains detailed controls.
- Existing settings are retained when switching modes. New Personal profiles start in Basic.
- Named custom palettes, theme import/export and inline palette editing.
- Cleaner source management, search and audio endpoint naming, plus background activity feedback for long-running tasks.

## Gallery, Player and Editor

- Persistent non-destructive edit drafts and metadata autosave, inline clip names and trim-aware muted Gallery hover previews.
- Smoother Gallery-to-Editor transitions with cancellation for slow loads.
- Dedicated Player navigation and full-viewport fullscreen playback with floating controls in both Player and Editor.
- The existing hybrid audio workflow remains: a playable default mix plus independent editable stems. New exports produce a single mixed soundtrack; explicitly replacing an original retains its editable stems.

## Sharing and exports

- Named sharing profiles with individual default or custom decimal MB caps. Share the saved clip or the current edit.
- Export as new with Original, 10, 30, 50, 100 or Custom MB. Original retains the selected editor settings; capped exports measure and validate staged files before publication, with bounded retry when needed.
- Compatible sharing MP4s with H.264 video and a single AAC mix when audio is selected. Completed files can be copied again or revealed for manual attachment.
- Background Media Jobs with progress, queueing, independent cancellation and retry where safe. One worker by default; two optional, with new starts limited while recording.
- Source and destination protection during media work; failed or cancelled staged outputs are not published.

## Installation and updates

Download **QuickClip-Personal-1.2.0-setup.exe** for an installed copy, or **QuickClip-Personal-1.2.0-win-x64.zip** for portable use. Extract the entire ZIP before running. Both packages include the required runtime and media components. Verify the downloads using **SHA256SUMS.txt**.

To upgrade from v1.1.0, run the new installer manually or update the portable copy manually. The Personal installation identity and profile locations remain unchanged. The new installed-update handoff supports compatible registered installations with an installer inventory, checks the package, protects active work and preserves the registered install location. Unsupported layouts and portable installations retain manual updates.

## Compatibility and known limitations

- Game-aware recording still captures one authorized display, not isolated hooked game rendering. Simultaneous displays and independent companion recording are not included.
- Some launcher installations and games require manual executable selection. Xbox/Bedrock, unusual game runtimes, varied GPUs, physical capture/input, mixed-DPI behavior and real alternate-drive update scenarios need broader hardware validation.
- File caps are maximums, not exact target sizes. Compression can take longer and reduce quality. Receiving applications may enforce different limits or require manual attachment.
- External added audio is not guaranteed to survive a persistent draft restart. Existing recordings are not silently rewritten.
- No matched gameplay performance improvement is claimed. Real capture and receiving-app attachment testing remain separate from automated Windows tests.

## Resources

[Project README](https://github.com/Yosef13103/QuickClip) · [Report an issue](https://github.com/Yosef13103/QuickClip/issues) · [Source and GPL-3.0 license](https://github.com/Yosef13103/QuickClip/blob/custom/main/LICENSE)

QuickClip Personal is an independently maintained fork of [swiiftex/QuickClip](https://github.com/swiiftex/QuickClip). Third-party components and artwork retain their applicable licenses and attribution.

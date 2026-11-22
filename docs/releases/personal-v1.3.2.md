# QuickClip Personal v1.3.2 — audio mixer and source controls

**Windows 10 22H2 (19045+) and Windows 11 · x64 · self-contained**

This stable release improves how independently recorded playback and microphone tracks are configured, named, reordered and mixed when clips open in the editor.

## Audio recording and mixing

- The Audio settings page separates **Playback** and **Microphones**, with a compact source list, individual include switches, track names and volume controls.
- Choose each track's default editor volume between **0% and 200%**. These settings are captured for **newly saved clips only**; earlier recordings and original editable audio stems stay unchanged.
- Fixed a mismatch between MP4 stream numbering and editable audio-track order when a combined playback stream precedes individual stems. New clips now initialize editor levels from their recorded settings.
- Fixed the Gallery's indexed clip-metadata path discarding saved initial mix values, which had made some new clips open with every track at 100%.
- Source actions are consistently aligned. The redundant Enabled/Disabled labels and reorder arrows are replaced with a quiet **six-dot drag grip**. Drag within Playback or Microphones to reorder; use **Ctrl+Up/Ctrl+Down** when focused on the grip for keyboard reordering. Settings are saved using existing source-order persistence.
- Microphones have one **Test mic** action that records and plays a five-second sample. Device names, missing-device choices and per-track controls remain visible.

## Updating

**Install v1.3.2 directly over a supported older QuickClip Personal installation; intermediate versions are not needed.** Installed versions check stable Personal releases, verify downloaded package size and SHA-256 and request confirmation before updating. Existing settings and media remain in their normal locations. Portable users update by replacing application files while preserving their separately stored data.

Assets: **QuickClip-Personal-1.3.2-setup.exe**, **QuickClip-Personal-1.3.2-win-x64.zip** and **SHA256SUMS.txt**.

The release includes the changes in [v1.3.1](personal-v1.3.1.md) and [v1.3.0](personal-v1.3.0.md).

## Scope and limitations

The experimental recording-epochs implementation, cross-graph replay stitching, and overlapping-clip consolidation are **not included**. Per-track default volume changes are not applied retroactively to older clips. Individual hardware configurations and microphone test behavior may vary; the automated release checks do not substitute for live recording/listening verification.

QuickClip Personal is an independently maintained [GPL-3.0](../../LICENSE) fork of [swiiftex/QuickClip](https://github.com/swiiftex/QuickClip).

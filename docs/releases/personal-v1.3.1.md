# QuickClip Personal v1.3.1 — update navigation and notification polish

**Windows 10 22H2 (19045+) and Windows 11 · x64 · self-contained**

A focused maintenance release following [v1.3.0](personal-v1.3.0.md); no capture architecture changes.

## Improvements

- A clearer theme-aware **Update** action in the bottom bar shows the available version with a distinct icon and hover/focus states.
- The bottom-bar action and Windows tray's update notification now take you directly to **Settings → General → Updates**, rather than opening Recording or retaining an unrelated Settings section.
- General now shows a round attention badge when an update is available and hides it when no update is available. The Sources game-detection badge remains separate.
- Accessible labels and update messaging are kept in sync with the existing stable-release checker.

## Updating

**You can upgrade directly from an installed v1.2.2 or v1.3.0 to v1.3.1; intermediate installation is unnecessary.** QuickClip chooses the newest newer stable Personal release, validates its exact installer size and SHA-256, and only installs after an approved application shutdown. Existing user settings and media stay in their current locations. For portable copies, download the ZIP and update manually.

Assets: **QuickClip-Personal-1.3.1-setup.exe**, **QuickClip-Personal-1.3.1-win-x64.zip** and **SHA256SUMS.txt**.

v1.3.1 includes the v1.3.0 Discord Sources, Gallery, player/editor and audio features. See the [v1.3.0 notes](personal-v1.3.0.md).

## Scope and limitations

This update does not enable adaptive-resolution replay stitching, game/monitor cross-graph packet retention, overlapping continuous clips, or the experimental detached epoch archive. These remain isolated research and are not shipped. Real-window visual acceptance and broader hardware compatibility vary by system.

QuickClip Personal is an independently maintained [GPL-3.0](../../LICENSE) fork of [swiiftex/QuickClip](https://github.com/swiiftex/QuickClip).

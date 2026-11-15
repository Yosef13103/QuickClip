# QuickClip Personal v1.1.0 — Wave 1 frontend and usability release

Windows 10 (19045+) / Windows 11, x64. This is the Personal edition from custom/main. Choose the self-contained setup EXE or portable ZIP; verify the provided SHA256SUMS.txt. The existing Personal install identity and media storage are retained.

## New and improved
- Refined Dark, AMOLED, Slate and Light themes, compatible custom themes, consistent navigation and storage cards, accessible animated toggle switches and scaling improvements.
- Standard Windows taskbar minimize, improved Editor native-preview ownership on repeated theme changes and restoration, centered window startup.
- Switch the selected monitor during manual desktop capture without a recurring approval dialog. Explicit selection authorizes that physical target for the session; game display exclusions remain separate. Switching capture sources still clears the replay buffer.
- Friendly monitor names, distinct tray/taskbar badges (off, standby, game and manual desktop), clearer Save Clip state.
- Shared recording/library destination controls, estimated storage breakdown and remaining clip count, smart local library catalog enabled for new Personal profiles, clearer folder exclusion help.
- Redesigned compact and detailed themed banners. Only a successful clip save can use image-only mode; image appearance and duration are independent, with a built-in animated camera GIF and custom image support. Improved edge placement, aspect ratio and motion effects.
- Existing opt-in timing diagnostics refresh automatically while the panel is visible.

## Compatibility and known limits
- Existing settings, custom images, themes and recordings are preserved. Legacy settings are migrated; existing explicit catalog opt-outs remain respected.
- The previous v1.0.0 hybrid audio workflow is unchanged. Game-aware recording still captures one authorized display, not isolated hooked game rendering.
- Dynamic replay duration, incremental discovery, independent companion capture, multi-display composition, safe Personal updater and advanced categorized logging remain on the [post-V1 roadmap](../post-v1-roadmap.md).
- Image alpha margins can vary during an animation; the shared crop prevents jitter. Ripple has additional room for its wave. Capture-exclusion and exclusive-fullscreen limitations remain.
- Strict Windows Personal/Upstream CI and self-contained packaging are required before publication. Mixed-DPI and actual GPU/overlay behavior require real Windows testing. See [Wave 1 acceptance](../post-v1-wave1-frontend.md) and [notification design](../notification-overlay-redesign.md).
- The source remains GPL-3.0 with inherited third-party licensing obligations. The owner-supplied camera animation is packaged separately as an embedded asset; verify its redistribution rights before public release.

No old releases are overwritten and no unrelated remote branches or PRs are deleted by this release workflow.

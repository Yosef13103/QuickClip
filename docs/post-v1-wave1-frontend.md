# Post-V1 wave 1: frontend and window lifecycle

Base: 70a8cf4437b4931ed72e65683a8eac9b6802607e. Frontend and startup preference migration only; no new capture backend, eligibility policy, audio or media storage changes.

## Design research

- [Fluent 2 semantic colors](https://fluent2.microsoft.design/color-tokens): neutral, brand and status tokens.
- [Radix color scales](https://www.radix-ui.com/colors/docs/palette-composition/understanding-the-scale): separate surface, hover, selection and separator roles.
- [Windows switch guidelines](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/toggles): immediate settings versus selections.
- [WCAG 2.2](https://www.w3.org/TR/WCAG22/): contrast targets used as practical desktop checks.
- [WPF Fluent resources](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/Themes/PresentationFramework.Fluent/Themes/Fluent.Dark.xaml): verified actual control resource names.

No dependency added or third-party implementation copied. Dark uses cool neutral surfaces and blue; AMOLED preserves a pure black canvas with teal; Slate is subdued blue-gray; Light is neutral white/pale blue with ink-blue interactions. Custom theme schema remains version 1. Switch and divider colors derive from existing palette tokens. Gallery and Settings use the same internal sidebar token; the Settings outer border is removed. Fullscreen black and thumbnail-overlay contrast remain intentional.

Calculated worst-case primary/secondary text contrast across the six defined surface roles: Dark 5.50:1, AMOLED 6.56:1, Slate 5.60:1, Light 6.05:1. Existing signal-surface thresholds also pass. This is a palette check, not a claim of complete visual/accessibility validation.

Immediate controls retain native WPF check state, bindings, events and automation. Expanders, multi-select, overlay-field selection, export-track inclusion and compact Solo retain their existing semantics. Manual desktop uses the existing consent flow and actual recorder state. Game switches change the existing inclusion override, not eligibility.

Normal minimize keeps the frontend alive; the existing native preview repaint path is retained. Close-to-tray continues through the existing editor close lifecycle. Quit remains explicit and stops recording. The previous MinimizeToTray JSON preference migrates to StartInTray for Windows startup only (the new key wins).

## Owner acceptance (Windows 10)

1. With a scratch profile and sample clip, open Editor, scrub and pause, minimize/restore repeatedly via taskbar and caption. Verify correct current frame, crop, playhead, no stale desktop content or lost edits. Repeat maximized and fullscreen.
2. While buffering an authorized source, minimize/restore, then close-to-tray/reopen. Verify recording remains uninterrupted and Editor opens correctly. Quit from the tray explicitly; confirm process exits. Repeat with close-to-tray disabled.
3. Check Dark, AMOLED, Slate, Light and a custom theme: video letterbox, popups, sliders, selected/hover/disabled states, danger actions. Test 100/125/150% DPI, text/control scale limits, keyboard Tab/Space and Windows high contrast.
4. Compare Gallery and Settings sidebar fills; there must be no outer Settings border. Check labels and minimum window width.
5. Toggle recording, endpoints, cursor, discovery, startup, notifications and gallery filters; verify persistence. Cancel manual desktop consent (must remain off); accept deliberately, then stop. Bulk selection still uses checkboxes. Game enable/disable rules must remain unchanged.
6. Observe memory during repeated minimize/restore, close/reopen and Editor navigation.

CI must build both profiles. Native viewport, real recording, responsiveness and screenshot appearance require owner hardware acceptance. Never auto-merge.


## Owner review follow-up (2026-11-15)

The first Windows quality run passed both profiles, but owner screenshots showed maximized client-edge clipping, a poor elliptical switch silhouette, an overlapping monitor label at increased UI scaling, and generic status labels.

- Custom chrome: use the actual Win32 client rectangle, monitor work area and current DPI to calculate overhang compensation; do not override WPF's NCCALCSIZE. Test maximized on both displays, minimize/restore, monitor move, taskbar position and real video fullscreen. Primary rail is 104 DIP at 100%; restore its dynamic resource on fullscreen exit.
- Vector pill switch: normal and master switches use the same drawing control and 180 ms eased thumb transition, respecting Windows reduced motion and high contrast. The native ToggleButton still owns state, keyboard and automation. Other page-wide animations remain a future design pass.
- Manual capture: the displayed primary monitor selection on a fresh profile is committed before requesting existing consent. A remembered disconnected device must not silently turn into another monitor. Automatic display-following requires the existing master consent and honors a new persisted exclusion list keyed by verified physical display identity. The selected display switch blocks automatic game recording there, not an independently approved manual session. Global consent remains accessible in a collapsed permissions section. Changing a prospective manual target does not itself start or switch capture.
- Recording status uses the friendly name obtained from the actual started monitor, with GDI fallback; no new per-status enumeration is required.
- Recording dimensions are side by side, and fixed-label rows wrap. Test 90–125% controls/text, long display names and the minimum window size. The entire application has not been visually audited.
- Recording and Library show the same destination folder and capacity model, use the existing safe folder-change path, and estimate the number of full clips that fit. Tracked clip space is a logical estimate from configured indexed roots on the destination volume, not an exact allocated-byte scan; untracked files stay in the other bucket. An unindexed destination reports the breakdown as unavailable rather than zero. No recursive disk crawl was added.
- Smart library catalog is the new label; defaults on for Personal settings without an explicit value, preserves saved opt-outs. Catalog and annotations are local. Exclusion help explains relative paths applied to every configured source.
- Additional tests: physical-display exclusion requires underlying consent and survives round-trip; friendly status label; switch template; catalog default.

### Deferred recording-engine task (Wave 2/3)

Do not implement duration changes by suppressing the restart alone: native ring capacity would remain unchanged. Add a supported native runtime replay-duration update that safely resizes/evicts the encoded packet history under its existing synchronization, retains frames/audio/keyframes and session identity when increasing, and restricts save to the requested shorter lookback immediately when decreasing. Keep marker timestamps, native generation, buffer duration/byte metrics, manual/game graph, and hybrid audio alignment correct. Test repeated grow/shrink under simultaneous saves, keyframe boundaries, duration caps, memory/CPU, and failed requests; unrelated encoding/source changes may still legitimately restart. No change to replay length behavior in this frontend follow-up.

Owner retest and fresh Windows CI required before merge.


## Editor lifecycle regression: theme switch with an open clip

Owner reproduced audio-only/blank native viewport after minimizing and a permanently loading preview after switching theme in Settings and returning to Editor. Recreating the window or selecting a new clip restores playback.

The Editor's Loaded handler previously created a new mpv instance on every WPF Loaded event, unlike the guarded player/project handlers. WPF explicitly permits Loaded/Unloaded during theme/template invalidation (https://learn.microsoft.com/en-us/dotnet/api/system.windows.frameworkelement.loaded). This is a concrete lifetime defect and a strong explanation of the symptoms, not yet a hardware-confirmed root cause.

- Guard initialization before and after awaiting the native window; subscribe source events once; detach callbacks on close. Repeated Loaded schedules refresh, not player allocation, placeholder reset or loop reset.
- Coalesce visible-preview refresh after layout, update native host visibility/position, request asynchronous redraw, avoid forcing non-client frame changes or synchronous child rendering. Reapply the mpv letterbox only if the effective color changes. Skip redundant WPF ThemeMode assignments.
- Never show a stale/unverified preview or accept play before the current file is ready. No changes to capture, recorded files, edit draft, trim, audio routing or playback duration.
- Added single-owner guard tests and a Debug-only scratch-video integration smoke (`--render=<scratch output dir> --editor-lifecycle-smoke-only <scratch video path>`). Use a video longer than four seconds, with a distinct test profile. It cycles themes, explicitly repeats Loaded, checks player/HWND identity, trim, position, decoded-frame screenshots and advancing playback. Success writes editor-lifecycle.txt; exceptions are logged. Offscreen smoke does not prove real on-screen Win10 DWM composition.

Owner acceptance: fully quit the old dev process before rebuilding (old orphan players cannot be repaired in place). Open a disposable clip, set a trim and pause at a recognizable frame. Repeatedly minimize/restore while paused and playing. Navigate to Settings and change Dark/Light/Slate/AMOLED/Custom, return to Editor, verify current frame, time, trim, audio lanes, loop, play/pause; no selecting another clip. Also verify standard close-to-tray/reopen and explicit quit. If blank, distinguish advancing timeline/audio from frozen timeline and capture the existing app log. CI plus actual local visual acceptance remain required.

## Final owner polish pass

See [durable four-wave backlog](post-v1-roadmap.md) for all remaining work. Owner liked the design; reported manual picker no longer switches active capture (introduced by Wave 1), first-window placement beyond work area, weak Light rail selection, awkward summary and disabled Save Clip. Explicit manual switch or selected target now authorizes that connected physical display for the current session without a recurring popup. Master off still invalidates authorization; automatic game capture policy unchanged. Source changes intentionally start fresh replay history. Newly created Personal window is centered within primary work area; existing windows restored normally. Shared rounded storage cards, floating status bar, solid semantic rail selection, neutral disabled Save Clip with dot, active status badges (amber standby, red game, blue desktop) in tray and taskbar. Tray icons are cached native handles and released on shutdown. Existing diagnostics panel auto-refreshes only when timing enabled and visible, with warning; broader opt-in event diagnostics and safe Personal updater deferred to Wave 2. Test real monitors, mixed DPI, taskbar/tray, first launch, master toggle, active switch, no accidental capture, all palettes, timing off/on, editor lifecycle and CI before merging. Version still 1.0.0 until release acceptance. No stable changes or releases in this pass.

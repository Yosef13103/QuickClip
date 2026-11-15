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

# Companion application identity — research checkpoint

## Scope

Owner-approved independent prototype, not W3 production activation. Based on remote integration/development at 5e1a7a521580047dff175d6dd54f48506db65426. No native changes, recorder integration, settings migration, Discord injection, live process inspection or capture. All new code is unused by the production runtime.

## Versioned installations

Squirrel documents installation of each new version in an immediate app-<version> directory and updating shortcuts through Update.exe; previous versions may coexist. The prototype matches an explicitly supplied ordinary install root and exact client executable leaf against the observed executable path. Numeric 2–4-part versions only in this slice; prerelease and other layouts remain unsupported. Root stubs, generic updater names, other roots/channels, traversal and nested helpers are not accepted as matching clients.

Path matching is not authorization. Before using a match, a Windows provider must validate ancestor/final handles (reject reparse escapes), verified process image and creation time, trusted publisher/product identity with an explicit trust policy, and actual eligible window ownership. Product metadata or file hash alone cannot establish publisher identity or authorize a replacement version. Verify an active client rather than prefer the newest installed directory; handle old/new simultaneous processes and changed file identity. Do not capture on presence alone. Keep registration state, identity, activity, consent and source availability distinct. Fail closed on unknown/revoked identity.

Reference: https://github.com/Squirrel/Squirrel.Windows/blob/develop/docs/using/update-process.md

## Discord activity decision

Official RPC includes GET_SELECTED_VOICE_CHANNEL, VOICE_CHANNEL_SELECT and VOICE_CONNECTION_STATUS. OAuth2 rpc.voice.read is restricted to approved partners; availability/approval for QuickClip is unverified. Research an authorized read-only integration, not unsupported authentication, private tokens, user-bots or client hooks. Do not collect channel names, participants or conversation content for activity determination.

Windows UI Automation can read exposed accessibility state; Discord-specific call indicators, minimized behavior, language changes and private/group/stream sessions need an owner-approved experiment. Return Unknown for missing/contradictory evidence; no call-triggered recording from Unknown. WASAPI session PID/activity is corroboration, not proof of a call. A session can span processes and GetProcessId can report only its initial process. Manual/focused/explicit running modes do not require call detection, but must have independent user consent.

References:
- https://github.com/discord/discord-api-docs/blob/main/developers/topics/rpc.mdx
- https://github.com/discord/discord-api-docs/blob/main/developers/topics/oauth2.mdx
- https://learn.microsoft.com/windows/win32/winauto/uiauto-uiautomationoverview
- https://learn.microsoft.com/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessioncontrol2-getprocessid

## Capture boundary

Prefer WGC CreateForWindow for verified companion HWND (Windows 10 1903+), testing occluded, minimized, closed, resized and popout windows separately. Never silently substitute whole-display capture. Existing game-first sticky behavior and explicit display consent remain unchanged. New capture source/format transitions must respect B's session and epoch contract after B stabilizes. OBS game-hook research is an independent native lane; not required for ordinary companion windows.

Reference: https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow

## Next gate

Run candidate recognition tests in hosted CI; then owner-approved isolated Windows process/window and signature provider fixtures, no live message or audio collection. Prove version update compatibility, invalid signature, revoked registration, PID reuse, window ownership changes and side-by-side versions. Only after these gates design UI registration and capture integration. Current work makes no guarantee that Discord is actually captured or that voice activity is detected.

# QuickClip P0 — platform-aware borderless capture contract

**Requirement update:** 2026-11-11. Read together with `.ai/public/INTEGRATE_P0.md`. This is an integration requirement for PR #3 + #4, not a claim that the runtime already supports it.

## Goal
Prefer verified **game-only, no-visible-yellow-border** capture wherever the current OS/package/permissions/backend make that possible. Preserve the previously working user experience wherever possible, but never secretly enable unapproved whole-display capture. On Windows 10 build 19045, neither the OS nor a newer Windows SDK can make WGC's supported border-removal API appear.

## Capability decision matrix

| Condition | Automatic preferred behavior | Honest failure and fallback |
|---|---|---|
| Windows 11 (or other supported build ≥20348), valid WGC session, granted Borderless capability and permission, actually verified no border | Prefer WGC game-window-only; automatic target follows valid game identity and approved capture rules | If user denies permission, package lacks capability/identity, another session requires borders, or borderlessness is unverified: do **not** silently call it borderless; explain and offer explicit monitor DDA |
| Windows 10 19045, no independently verified compatible game-only renderer backend | No claim of borderless WGC game-window support | Explain the Windows OS limitation; require explicit choice/consent for DDA full-monitor recording, or leave strict game-only unavailable |
| Any OS with separately verified, compatible, authorized game-only graphics capture backend | Prefer verified backend when it meets source identity, game compatibility and borderless policy | If unsupported per game, fail safely; don't patch anti-cheat or inject into protected processes |
| Explicit full-monitor recording | Desktop Duplication is a supported border-free monitor path | Disclose other windows, notifications and Discord pop-outs visible on that display may be captured |

### Windows 11 technical conditions
- Check OS capability at runtime (not only OS marketing name) and determine if the WGC border setting interface exists.
- Microsoft documents `GraphicsCaptureSession.IsBorderRequired` beginning at build 20348. To suppress the border, request the user's consent through `GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)` and declare `graphicsCaptureWithoutBorder` in an appropriate app package manifest. The existing FFmpeg `gfxcapture` filter may already attempt `put_IsBorderRequired(false)`, but a setter call alone does **not** grant authorization. Current QuickClip portable build has no proven package capability or access flow.
- Handle denied/revoked permissions, no package identity, user/system policy and competing border-required sessions honestly. Do not expose a fictional "No border" toggle just because the API exists. Runtime logging must report which backend is active and what authorization/capability was confirmed. A successful API call is NOT proof that the yellow border was actually absent during capture; include supervised visual smoke tests.
- Do not degrade compatible Windows 11 machines by permanently blocking game-only WGC in Personal. When the conditions are satisfied and tested, enable it as the default **for that machine**, not for Windows 10 by mistake.
- Don't broaden task into a complete production installer rewrite. If an app package identity/capability is necessary and unavailable, provide a bounded packaging prototype/contract and set implementation and hardware gates blocked, rather than claiming instant support.

### Windows 10 explanatory UI copy (suggested)
"Windows 10 does not support removing the Windows capture border from game-window recording. To record without the yellow outline, choose **Entire selected display (Desktop Duplication)**. This records everything visible on that monitor, including other windows. Game-only borderless capture needs a different compatible backend or a newer supported Windows capture configuration."

Use plain language in Settings and on error; allow the user to choose their display and remember an explicitly authorized fallback preference. Never silently switch from selected game capture to entire monitor. Distinguish source choice, output resolution and FPS. "Native" changes pixels, not Windows capture policy.

### Performance
DDA and WGC both incur screen acquisition/composition/scheduling and encoding costs. **There is no universal claim that DDA is faster or slower than game WGC.** Compare the same game/test scene/monitor/source resolution/output dimensions/FPS/codec/NVENC preset with recorder off, DDA, game WGC (where safely allowed), and any future game capture backend. Report mean FPS, 1% lows, frametimes, GPU Video Encode/3D use, CPU, RAM/VRAM and dropped frames across repeat trials. On monitor DDA, copying a larger display may cost more resources than a smaller game-window capture, but the difference is hardware- and scenario-dependent; no made-up percentage.

### Cross-PR integration and tests
Agent 4 merges PRs #3 and #4 in an isolated integration worktree and replaces unconditional game-only rejection with runtime-gated capabilities. Disabled/excluded games must never initiate new game-specific capture, but an already explicitly selected entire-display recording can visually include any foreground game. Add tests for Win10 no-capability, Win11 eligible/approved, Win11 denied/missing package/capability, capture permission revoke, explicit Desktop Duplication consent, unsafe fallback refusal, monitor disconnection, and verifying saved clip source metadata. Keep stock `main` untouched. Live gameplay/permission tests remain supervised; do not claim pass with synthetic tests alone.

Primary Microsoft source:
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired
- https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync
- https://learn.microsoft.com/en-us/windows-hardware/drivers/display/desktop-duplication-api

# P0 capture and Games integration

Base: 2b30a053e40c4faa7f5faf7a7867243ef3ea448f. PRs #2, #5 and #6 are included in that base. The isolated integration worktree merged [capture PR #3](https://github.com/Yosef13103/QuickClip/pull/3) at 1d3c0013bcb44af655e471a04f19aba94deae5d1 first and [Games PR #4](https://github.com/Yosef13103/QuickClip/pull/4) at 5eeba2deab368c3bfc994e817f0581aab958327f second. [Baseline push CI](https://github.com/Yosef13103/QuickClip/actions/runs/43095705950) was queued at preflight. About 208 GiB was free before setup.

## Review fixes

- [Recording Settings](https://github.com/Yosef13103/QuickClip/pull/3#discussion_r4541401581) distinguishes game-window-only from entire-display capture. Existing Game First preferences stay game-window-only. That mode reports why it cannot start. It never opens the monitor instead. Choosing the entire display authorizes everything visible there. Automatic monitor backend resolves to fixed DDA. Explicit monitor WGC remains available with a border disclosure. Missing named displays and backend mismatch fail closed.
- [Partial scan](https://github.com/Yosef13103/QuickClip/pull/4#discussion_r4541460259) unions prior and newly found associations under incomplete results and marks affected rows inactive. Approved scope and saved exclusions still apply. Configured directory exclusions veto legacy promotion.
- [Windowed Steam recognition](https://github.com/Yosef13103/QuickClip/pull/4#discussion_r4541460336) retains recognition for an exact catalog candidate in a recognized Steam install path without Game Bar or fullscreen evidence. That identity does not grant capture authority.
- [Retained rename](https://github.com/Yosef13103/QuickClip/pull/4#discussion_r4541460371) refreshes the display name after verifying the retained HWND, thread, PID and process start time. Discord focus does not replace an allowed game. Changing its classification to ineligible invalidates the retained target.

The recorder checks the verified catalog observation for Personal game-window requests. There is no approved game-window provider in this build, so it never starts or transitions into that mode. Games controls express eligibility. An independently authorized entire-display session ignores game eligibility, pins its backend and display identifier, and never changes to a game HWND. Upstream retains legacy source selection. Endpoint audio and the existing Save Clip binding remain in place. Fixed monitor receipts identify the display/backend and use finalized MP4 output dimensions, average FPS and video duration when available. Multiple capture epochs and unknown legacy provenance are reported separately.

## Capability and scope

Windows 10 build 19045 lacks the supported border toggle. Later builds require a packaged recorder with `graphicsCaptureWithoutBorder` and a user-initiated `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` grant. The current portable recorder and Explorer sparse package do not establish this capability. A bounded follow-up should build and sign an isolated Personal package, verify the runtime API and manifest identity, exercise grant/deny/revocation, and start directly on the authorized verified HWND. A graph must never bootstrap on a monitor for game-only recording. Confirm the indicator visually with competing capture sessions as well. No packaging, permission request, registration or live capture was performed here. [Microsoft API requirements](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)

Approved Steam libraries, game folders and exact EXEs are supported. Steam registry discovery, Epic, Battle.net, EA, Ubisoft, GOG, Riot, Xbox/Game Pass and scoped glob matching remain separate work. Discord application targeting and pop-out video as an extra source remain outside this integration.

## Validation

Tested code commit: `e9d4eb9b995550b6add3d64e67f4b852f984bc3a` on Windows 10 build 19045 with .NET SDK 10.0.403, existing pinned FFmpeg/libmpv dependencies and fresh test profile roots. Native C++ compiled and linked. Strict Personal and Upstream Release builds completed with zero warnings or errors.

- Personal full suite: 436 passed, 0 failed, 18 opt-in skips.
- Upstream compatibility suite: 27 passed, 0 failed, 14 live capture skips.
- The first Personal run found a null entry caused by deferred enumeration of a changing dictionary in the new partial-scan logic. Materializing updated scan rows before appending retained entries fixed it. The corrected full suite passed.

The following commands show the build and test arguments. Use the local .NET 10.0.403 executable if the SDK is not on PATH. Clear `QUICKCLIP_PERSONAL_DATA_DIR` so the test initializer controls the isolated profile. Set the live capture, Recycle Bin and discovery/library symlink opt-ins to 0.

```powershell
dotnet build QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true -p:NuGetAudit=false
dotnet test QuickClip.slnx -c Release --no-build --no-restore -p:QuickClipProfile=Personal --logger 'trx;LogFileName=final-Personal.trx' --results-directory .quickclip-local/results --blame-hang-timeout 3m
dotnet build QuickClip.slnx -c Release --no-restore -p:QuickClipProfile=Upstream -p:RequireCaptureEngine=true
dotnet test QuickClip.slnx -c Release --no-build --no-restore -p:QuickClipProfile=Upstream --filter 'FullyQualifiedName~ProfileTests|FullyQualifiedName~RecordingDefaultsTests|FullyQualifiedName~MenuPackageTests|FullyQualifiedName~CaptureEngineTests' --logger 'trx;LogFileName=final-Upstream.trx' --results-directory .quickclip-local/results --blame-hang-timeout 3m
```

## Owner-supervised checks and rollback

Use a new scratch Personal profile and generated or disposable media. Approve a Steam library and a secondary library explicitly. Verify Terraria, tModLoader and Marvel Rivals in windowed and fullscreen modes, then exclude and reallow each. Force an interrupted/partial rescan and verify the approved renderer association survives. Rename the active game while a disposable Discord window or pop-out has focus. Exercise focus changes, renderer exit/restart, display disconnect and reconnect. Confirm unsupported game-only mode captures nothing. On Win10 explicitly choose the disposable display with DDA, verify it records the entire visible display without a yellow outline, and compare native ultrawide with 16:9 crop. On Win11 repeat DDA and validate game-only consent, denial and border visibility only with a separately packaged and authorized prototype.

Use independent consenting Game, Chat and Mic signals. Verify playback and FFprobe track order, sync, duration, output dimensions and FPS. Compare same-scene recorder-off and DDA runs with matching FPS, preset and output, and measure replay latency, frame pacing and dropped frames. Check one Save Clip shortcut, existing media playback and Gallery/Editor/Share. QC-003, QC-004, QC-005 and QC-007 supervised gates remain open. The integration PR is for owner review. Close it to leave the baseline in place, or revert the eventual integration merge if already deployed.

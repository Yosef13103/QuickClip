# P0 combined field-test acceptance

Application baseline: [PR #7 at 0c77194](https://github.com/Yosef13103/QuickClip/pull/7), with [successful Windows CI](https://github.com/Yosef13103/QuickClip/actions/runs/43100251672). Requirements come from the [field test](https://github.com/Yosef13103/QuickClip/blob/639410ed569a5c41a05c09752ab4fec030c72e26/.ai/public/FIELD_TEST_2026-11-12.md), [dispatch](https://github.com/Yosef13103/QuickClip/blob/639410ed569a5c41a05c09752ab4fec030c72e26/.ai/public/FIELD_TEST_DISPATCH.md) and [FT-16 addition](https://github.com/Yosef13103/QuickClip/blob/4f51292dc34773e688b9b2c95d7e5a37fe7ad61f/.ai/public/FIELD_TEST_2026-11-12.md). This is a test plan. Application behavior remains at the baseline.

## CI and local reproduction

Windows quality keeps both strict x64 profile jobs, a read-only token, and synthetic tests with capture off. It adds the exact integration base to the pull-request trigger. Push CI still targets custom/main. If the trigger change does not exercise on this PR, verify the next integration-target PR after it lands. Check both profile jobs for the exact head and tested merge SHA. Retain TRX artifacts with pass/fail/skip counts. A scheduled or queued run gives no acceptance evidence.

From a clean scratch worktree with .NET 10.0.403 and Visual Studio C++ build tools, provision dependencies with the workflow's reviewed `scripts/get-deps.ps1 -Force` or copy the pinned workspace dependencies into that worktree. Use generated clips and disposable roots. The test module selects scratch profile paths. Clear the portable data override before running tests. Run in PowerShell 7:

```powershell
git rev-parse HEAD
dotnet --info
$env:QUICKCLIP_PERSONAL_DATA_DIR = $null
$env:QUICKCLIP_CI = '1'
foreach ($name in 'CAPTURE','RECYCLE_BIN','DISCOVERY_SYMLINK','LIBRARY_SYMLINK') {
    Set-Item -LiteralPath "Env:QUICKCLIP_TEST_$name" '0'
}
dotnet restore QuickClip.slnx
if ($LASTEXITCODE) { throw 'Restore failed' }
foreach ($profile in 'Personal','Upstream') {
    dotnet build QuickClip.slnx -c Release --no-restore "-p:QuickClipProfile=$profile" -p:RequireCaptureEngine=true
    if ($LASTEXITCODE -or !(Test-Path 'src/QuickClip.Capture/bin/QuickClipCapture.dll')) { throw "Strict build failed: $profile" }
    $test = @('test','QuickClip.slnx','-c','Release','--no-build','--no-restore',
        "-p:QuickClipProfile=$profile",'--blame-hang-timeout','3m','--logger',
        "trx;LogFileName=combined-$profile.trx",'--results-directory','TestResults')
    if ($profile -eq 'Upstream') {
        $test += @('--filter','FullyQualifiedName~ProfileTests|FullyQualifiedName~RecordingDefaultsTests|FullyQualifiedName~MenuPackageTests|FullyQualifiedName~CaptureEngineTests')
    }
    dotnet @test
    if ($LASTEXITCODE) { throw "Tests failed: $profile" }
}
```

For offline reproduction with an already-populated pinned cache, append `-p:NuGetAudit=false` to restore only. Record that package vulnerability lookup was omitted.

## Combined acceptance matrix

| Requirement | Automated fixture and expected result | Owner-supervised evidence |
| --- | --- | --- |
| FT-09 idle and master off | Fake clock and capture graph factory. Ten simulated minutes with master enabled, game mode armed, desktop disabled and no eligible owner creates zero graphs. Master off cancels pending work and leaves zero graphs. | Ten real minutes on a nonprivate test desktop. No active video graph, capture indicator or replay buffer. Record graph counters and elapsed time. |
| FT-09 authorized game trigger | Feed an eligible verified HWND/PID/start identity and approved display/backend. Expect one graph. Exit, stale HWND/PID, source loss and denied consent must stop or stay armed without opening any other source. | Disposable game and test display. Confirm explicit DDA approval, actual displayed source, launch, exit, buffer discard and no yellow outline. Win11 game-window WGC requires a separately verified package capability, consent and visual check. |
| FT-09 manual DDA and eligibility | Manual desktop authorization is separate. Fake display disconnect, disabled game, monitor change and restart. Disabled or excluded games cannot auto-start. A separately selected whole display can still show them. Reject unapproved display cycling and fallback. | Toggle manual DDA on and off on the named test display. Disconnect and reconnect that display. Verify source identity and lack of fallback to other visible windows. |
| FT-09 launch, Discord and exit | Use a renderer plus launcher, companion foreground and reused HWND/PID fixtures. Verify the same active game and graph through Discord focus. Final game exit stops automatic capture. | Terraria, tModLoader and Marvel Rivals with a disposable Discord window on the other monitor. Record source/backend IDs, switch count and actual saved footage. |
| FT-01/02/06 discovery | Generate two Steam library manifests, alternate library, nested collection, a source over 20k entries, helpers, interrupted scan and offline roots. Verify manifest names, grouping, bounded progress, stable prior EXEs, exclusions and persisted re-enable after restart. | Approve inspection of real launcher metadata separately. Compare two library drives and measured scan times. Keep real paths and filenames out of artifacts. |
| FT-09b hotkeys | Fresh, upgraded and intentionally unbound profiles. Verify only new Save Clip defaults to Alt+F10. Check conflict reporting, tray routing, display authorization and removal of retired tray commands. Preserve saved bookmarks. | Keyboard-only confirmation in a disposable profile. Test focus in the game and tray, conflict with another binding, and denied monitor switch. |
| FT-11 audio flows | Same friendly name with distinct playback and microphone fixture IDs. Verify flow-based identity, lane order, no fallback on unplug, and independently generated signals. | Owner-triggered Sonar Game/Chat/Mic test tones. Verify independent signals, silent eCapture diagnostics, recovery, and no reconfiguration. Keep media and hardware IDs private. |
| FT-10 replay correctness | Generated timestamped fixtures at 15/30/60/120 s and warm-up. Probe first decodable frame, actual duration, output FPS, track alignment and export seek. Assert human display formatting separately from mux precision. | Repeat on authorized test source and report actual versus requested duration, first keyframe and replay latency. |
| FT-03/04/05/07 Games and Settings | Accessible targeting toggles, virtualized inline details, precise offline status, coherent keyboard navigation at 1080p and high DPI. | Screen-reader/keyboard and reduced-motion smoke with synthetic game rows. |
| FT-08/12/13/14/15/16 Gallery, Editor and Storage | Generated clips across local midnight with renamed games. Verify mixed day selection and exact batch count, sharing, scroll/zoom, missing source during export, cancel/undo, draft retention, and recordings-location change without moving media. Test 1/4 columns and many groups. | Check 125/150% DPI, visible danger states and accurate confirmation counts, day feed and keyboard selection, existing export and project flows. Use disposable media only. |

## Dependencies and gate proposals

Chat 3 owns the stable discovery identity, partial-scan persistence and typed eligibility/veto contract for FT-01/02/06. Chat 1 owns the recorder state machine, source and endpoint authorization, source-loss behavior and test seam for graph transitions for FT-09/09b/11. Their feature PRs are separate dependencies. Review the exact published heads and interface changes before combining them. Chat 2 owns the separate WPF surface, including Gallery and Editor regressions. The acceptance matrix can run against a later combined SHA after those dependencies arrive.

Propose a separate ledger change after evidence. Split QC-003 discovery scale/persistence from runtime eligibility, QC-004 idle/authorization/loss from supervised source identity, QC-005 synthetic endpoint-flow isolation from supervised Sonar signals, QC-002/007 mux timing from replay latency and soak, and QC-008/009 from the new Gallery/Editor/Storage subrequirements. Track Hotkeys separately from recording authorization. Keep current audit baseline and pending hardware gates. No current gate is updated by this PR.

Rollback before merge is to close this PR. After a merge, revert the trigger change to return to custom/main-only PR checks. Application source and the existing integration worktree are outside this change.

## Preparation validation

Application tree at 0c7719410a045a7a28c7a37d1d8f14d6cc045ef7, with the proposed workflow filter change. Windows 10 build 19045, SDK 10.0.403 and pinned workspace dependencies. Used the commands above with offline restore, and stored TRX as prep-Personal.trx and prep-Upstream.trx under the ignored scratch results folder. Strict native and managed builds passed with zero warnings or errors. Personal 441 passed, 18 opt-in skips. Upstream 27 passed, 14 live capture skips. Zero failures. No live capture was performed.

# P0 Windows acceptance and CI handoff

## CI evidence (QC-001)

The workflow runs on PRs targeting `custom/main` and pushes to that branch. The Windows x64 Personal lane runs the full headless suite. The Upstream lane builds the stock profile and runs identity, defaults, packaging and capture interop checks. Live capture, Recycle Bin and privileged symlink tests are reported as skipped. Native interop enumerates endpoint capabilities and tests rejection of an invalid request before capture. It does not record. Fixture audio and video come from synthetic FFmpeg sources.

CI installs .NET SDK 10.0.403. The Windows runner must supply MSVC x64 and the Windows SDK. The existing `scripts/get-deps.ps1 -Force` downloads the pinned GPL shared FFmpeg build and corresponding headers/import libraries, libmpv development archive and Inno Setup NuGet package. It verifies SHA-256 before extraction. No dependency cache or binary release is uploaded. Deleted assets, hash mismatches, unavailable compiler/SDK or extraction tools must fail the job. Pin updates need source and license review. Redistributing a portable build has separate license and corresponding-source obligations.

To reproduce on Windows x64 from a clean checkout with those prerequisites:

```powershell
$env:QUICKCLIP_CI = '1'
$env:TEMP = (New-Item -ItemType Directory -Force ./TestResults/scratch).FullName
$env:TMP = $env:TEMP
./scripts/get-deps.ps1 -Force
dotnet restore QuickClip.slnx
dotnet build QuickClip.slnx -c Release --no-restore -p:RequireCaptureEngine=true
dotnet test QuickClip.slnx -c Release --no-build --no-restore --logger 'trx;LogFileName=results.trx' --results-directory TestResults --blame-hang-timeout 3m
./scripts/summarize-ci-tests.ps1 -Path TestResults/results.trx
python .ai/public/progress.py
```

For Upstream repeat in a separate clean checkout with `-p:QuickClipProfile=Upstream` on build and test, adding filter `FullyQualifiedName~ProfileTests|FullyQualifiedName~RecordingDefaultsTests|FullyQualifiedName~MenuPackageTests|FullyQualifiedName~CaptureEngineTests`.

## Owner-supervised Windows 10 build 19045 session

Use an isolated Personal profile, scratch output on a non-library volume or a disposable test volume, synthetic desktop content, a private test voice room with consent, and no personal windows or recordings. Stop if a private source appears, the yellow border persists unexpectedly, an output is corrupt, or disk headroom drops below the agreed floor. Never change drivers, global audio or display settings to make a test pass. Record SHA, Windows build, driver, monitors and refresh, game/scene/settings, encoder/preset, FPS, output geometry, bitrate, replay length, and track labels. Keep endpoint IDs, media and full logs local. Use sanitized summaries and test identifiers in the PR.

| Gate | Procedure and pass criteria | Result / evidence |
|---|---|---|
| QC-004, QC-006 | Start strict game-only on Win10 19045. A supported selection records only the authorized game with no yellow border. Otherwise expect explicit failure and no silent desktop capture. Opt into DDA monitor separately. Put different synthetic labels on both displays and verify actual saved frames, display/backend receipt, resolution and codec with FFprobe. Repeat at native and scaled output. | Pending |
| QC-003, QC-004 | Launch and exit Terraria, tModLoader and Marvel Rivals separately. Verify installation identity, eligible profile, and return to configured fallback. Disable each and repeat after restart. No automatic game targeting or game profile while disabled. A separately selected monitor can still show visible gameplay. Run 30 transitions across game focus, ordinary Discord and a Discord pop-out on the secondary display, minimize/restore, and game exit/relaunch. Record actual source per transition and save across selected boundaries. Prior reports of game recognition are smoke observations. | Pending |
| QC-005 | Route distinct test tones or deterministic audio to Game, Desktop, Chat and a supervised synthetic Mic input. Play only one at a time, then together. Check named stream order, tone in the intended track, absence in other tracks, silence and A/V sync. Repeat with export playback and separate gain/mute changes. A physical output already mixing virtual endpoints can duplicate tracks. Record friendly endpoint labels only. | Pending |
| QC-002, QC-006, QC-008 | Begin with five saves and decode each entire file. With explicit owner supervision, extend to 100 saves if testing reliability, with transition saves included. Record save latency, requested/actual durations, geometry, codec, decode errors and source receipts. Run 20 recoveries across target closure, resolution or monitor changes, audio disconnect, locked destination and forced interruption of staging. Use only a quota-limited disposable volume for disk-full; check explicit failure, no bogus success and no overwritten or corrupt finished media. | Pending |
| QC-007 | Compare recorder off, borderless monitor, and supported game capture using the same repeatable scene, graphics settings and equivalent output. Record three timed samples per mode after warmup. Include a separately approved 60-minute idle/game soak with 5-minute sampling and periodic saves. Stop on worsening resource use or failure. Report game FPS and p50/p95/p99 frametime, CPU, GPU Video Encode (separate from total GPU), working set, VRAM and queue/drop counters. Mark unavailable telemetry explicitly. | Pending |

Performance row template:

| SHA / mode / run | Scene and output | Duration | Game FPS / p50 / p95 / p99 ms | CPU / Encode / total GPU % | Working set / VRAM | Queue peak / dropped frames | Notes |
|---|---|---|---|---|---|---|---|
| | | | | | | | |

## PR checklist

Record source branch and head SHA, base `custom/main`, tested merge SHA from the job summary, changed QC IDs, commands, check and artifact URLs, pass/fail/skip counts, manual test evidence and blockers. Compare local `main` with the locally fetched upstream ref and report whether the PR affects it. Keep baseline tracker data and report fresh checkpoints separately. A green PR merge-ref run is distinct from a green push run on the resulting `custom/main`. Agents leave merges to the integration owner.

If protection is unverified, the owner can open repository Settings → Rules → Rulesets, target `custom/main`, require pull requests and select both `Windows x64` profile checks after their first runs. For stock `main`, restrict direct pushes and force pushes with a documented upstream sync exception. Verify rule status and bypass settings in the repository before claiming enforcement.

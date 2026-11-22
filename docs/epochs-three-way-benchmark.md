# Three-way QuickClip performance acceptance: original / Development / Epochs

Status: prepared runner and native source checks; **no matched three-way gameplay measurements yet**. The resource sampler and PresentMon are installed on Jambo. No FurMark installation has been performed.

## Exact comparable builds

| Variant | Source | Mode |
| --- | --- | --- |
| Original | `swiiftex/QuickClip` v0.7.1, pinned zip plus SHA256 in `benchmark-wave2.ps1` | Stock desktop capture; separate disposable Windows user (original profile is not isolated) |
| Development | Current `2. Development` Git HEAD captured at Prepare | Personal; scratch profile |
| Epochs | Current `3. Branches/recording-epochs` Git HEAD captured at Prepare | Personal; scratch profile |

Do not substitute old released Personal v1.1.0 for **Development**. The runner's `-StudyKind EpochsThreeWay` selects exactly Original/Development/Epochs and omits that old release.

From `3. Branches/recording-epochs`, first run the **non-capturing preflight** in PowerShell 7:

```powershell
$devSha = git -C '..\..\2. Development' rev-parse HEAD
$epochSha = git rev-parse HEAD
pwsh -File .\scripts\benchmark-wave2.ps1 -Mode Preflight -Study epochs-20261122 -StudyKind EpochsThreeWay -Development $devSha -Candidate $epochSha
```

The developer/owner must select a **separate, disposable, signed-in Windows account** and record its SID. The original upstream binary uses normal roaming `%APPDATA%\QuickClip`, and changing only `QUICKCLIP_PERSONAL_DATA_DIR` does **not** sandbox it. This is a privacy requirement, not a token/benchmark convenience. Do not create or change Windows accounts without approval.

Prepare (owner account; interactive YES confirmation, no capture/launch):

```powershell
pwsh -File .\scripts\benchmark-wave2.ps1 -Mode Prepare -Study epochs-20261122 -StudyKind EpochsThreeWay -Development $devSha -Candidate $epochSha -DisposableSid '<verified-other-user-SID>'
```

Prepare downloads the known original release and validates its fixed checksum, builds clean detached worktrees at both exact SHAs using .NET 10 and native engine, packages isolated self-contained Personal binaries, and pins archive hashes in the manifest. The original release and all generated outputs stay in ignored `.quickclip-local/benchmarks/<study>`. No production settings, update service or install hooks are touched.

Under **the registered disposable account**, with the actual game/stress scene deliberately started and exactly one capture app at a time:

```powershell
pwsh -File .\scripts\benchmark-wave2.ps1 -Mode Run -Study epochs-20261122 -Scenario Game -Comparison Controlled -Repetitions 3 -Seconds 60 -WarmupSeconds 15 -Nvidia -PresentMon
pwsh -File .\scripts\benchmark-wave2.ps1 -Mode Run -Study epochs-20261122 -Scenario Save -Comparison Controlled -Repetitions 3 -Seconds 60 -Nvidia
pwsh -File .\scripts\benchmark-wave2.ps1 -Mode Report -Study epochs-20261122
```

The runner asks for each actual game PID, equivalent settings, and deliberate recording/saving consent. PresentMon runs only for the explicitly approved game PID after warmup, exports v1 numeric presentation intervals, and computes FPS/1% low/p95; if ETW collection fails, mark it **unmeasured** and supply an independently measured `FrameMs` CSV. NVIDIA GPU samples are whole-adapter, never attributed to the QuickClip process. The report baseline is **Development**, or **GameNoRecorder** for game FPS and device-wide metrics.

## Protocol and fairness

1. Establish game-only control: same scene, resolution, refresh, driver, background load, and no recorder. Use a nonprivate, repeatable display scene; no ordinary personal desktop footage. Alternate benchmark variant order across rounds, minimum three rounds per scenario.
2. **Controlled desktop-capability series**: match the feature intersection of all three versions: capture mode, codec, resolution, FPS, bitrate, presets, and audio routing as far as supported. Original always captures desktop; use the Personal equivalent desktop mode, and record any unavoidable source/audio differences. No unproven "game hook" equivalence.
3. **Development vs Epochs game-triggered series**: compare separately under the verified-game catalog/display grant, as the epoch retention lifecycle does not run in manual desktop/Discord mode. Do not include original as a fake equivalent.
4. **Dynamic-resolution functional series**: permitted game at 3440x1440 → 1920x1080 → 3440x1440; save across transitions, inspect MP4 frame size, duration, gaps, named Game/Chat/Mic stems, synchronization, and authorized source cessation.
5. **Save stress**: capture frame-time changes during native MP4 snapshot and mixed-resolution normalization, memory peaks, elapsed save time and codec/stem correctness. Heavy CPU AV1/HEVC/H264 normalization is not free, even when priority is lowered.
6. Collect median FPS, 1% low FPS, frame-time p95/p99, CPU normalized and one-core, private/working-set memory, device-wide 3D/encode/decode/VRAM/temperature, replay buffer bytes/drops, and process I/O. Save each trial's real options and provenance. No claims of "no drop" from idle or RAM-only sampling alone.
7. Compare deltas to normal run-to-run variance. A repeatable epoch-only drop in game 1% lows or frame pacing blocks acceptance, even if average FPS and NVENC utilization look similar. Some interruption at an actual encoder teardown/resize is unavoidable; distinguish transient restart gaps from steady-state regression.

## GPU workload

FurMark is **optional**, not a replacement for a realistic repeatable game scene, and is **not installed** at the time of this checkpoint. A GPU-saturated stress run may exaggerate scheduling contention and temperature-dependent variance. If the owner deliberately installs/authorizes it later, constrain run duration, monitor temperature/power, avoid concurrent stress tools, and label the source mode accurately. Never silently whitelist FurMark as a real game or bypass catalog/display permission checks.

Existing `benchmark-video.ps1` provides a separate 5–30s **desktop → RAM-only** encoder smoke test for Development and Epochs; it has no screen/audio output file and cannot measure game FPS or stand in for the stock original executable.

## Stop conditions

Never start two recorders at once. Abort on capture-source permission changes, wrong target PID, private display content, missing source hashes, source/codec mismatch, sustained frame drops beyond normal variance, unsupported source geometry, thermal instability, or failed output/stem validation. Never merge Epochs to Development or call it zero-FPS-impact before a supervised matched result.

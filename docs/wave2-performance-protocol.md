# Wave 2 matched performance baseline (W2-P preparation)

This is a measurement tool and protocol, not measured results. Compare released Personal v1.1.0 (6de4ad684f4268b685c630d639977c439bf557ed) with the final integrated Wave 2 SHA, built before timing. Use separate disposable profiles, identical capture parameters and generated/nonprivate content. No production profile, archive, system configuration changes, automatic launch, device recording or process termination. The helper only observes an explicitly chosen PID. Do not benchmark two recorders simultaneously.

## Resource measurements

From an isolated worktree, start a known build yourself, identify its PID in Task Manager Details (or Get-Process QuickClip), then run:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-process-baseline.ps1 -TargetProcessId 1234 -BuildSha 6de4ad684f4268b685c630d639977c439bf557ed -Variant Personal -Scenario IdleVisible -RunLabel release-r1 -Seconds 60 -Nvidia

The SHA is an owner-supplied label, not an attestation; record actual build provenance. Executable hash is additional evidence, not a hash of the complete application. If running dotnet QuickClip.dll the executable hash is the host hash; note this. Use the correctly identified host PID and don't share absolute paths. The script collects CPU (one-core and normalized machine), private memory and working set, threads, handles, user/GDI object counts (zero can mean unavailable), process IO counters (not physical disk traffic), optional NVIDIA device-wide GPU/encode/decode/VRAM/clocks. GPU queries use a timeout; sampling intervals can stretch, actual interval is recorded. NVIDIA data is not attributed to QuickClip. No GPU 3D engine attribution, per-process VRAM, game frametimes or capture drops are inferred. Local CSV and JSON files only. Build outside measurement. Tool has not yet been run on Windows; validate syntax and basic output in a scratch process first.

Repeat three times each, alternating release/candidate order:
- IdleVisible: recording/scans/updates off; same page, warm up 15s, measure >=60s.
- IdleTray: actually close to tray, not minimize; >=60s. IdleMinimized separate if useful.
- ArmedNoGame: master enabled, no eligible game; no encoder. Use permitted scratch setup.
- Scan: one matched approved fixture collection, known cold/warm state; capture activity duration separately. Never compare cold vs warm.
- GameNoRecorder: game PID, recorder off. Game: same reproducible scene/settings/fullscreen mode, recorder enabled, same duration/codec/preset/bitrate/FPS/resolution/audio and display. Sample QuickClip and game PIDs in separate terminals if necessary; avoid duplicate NVIDIA queries. Record independent game frametimes/FPS/1% lows (existing game overlay or approved PresentMon-based tool), actual capture backend, encoder cadence/drops and saved clip correctness. Device-wide counters are not game FPS.
- Editor: same generated clip, repeated open/close then idle plateau; no production clips.
- Save: same replay age, bounded repeated explicit saves; record request-to-feedback and file-ready separately, actual duration/streams with existing FFprobe and timing diagnostics.
- DiagnosticsOff/On: same workload and only change one preference (logging or timing) at a time; note enabled categories. Do not measure diagnostics-on as baseline off.

Use existing scripts/benchmark-video.ps1 for explicit desktop-to-RAM source/encoder tests, not normal app gameplay measurement. No audio or files saved by that benchmark; user supervision required. Keep separate from process sample data.

Record OS/driver/adapter, display geometry, settings, workload, actual build SHA, sample completeness, and median/p95 across runs. Save raw data locally, share sanitized numeric summaries only. If unsupported, leave metric unmeasured. No claimed FPS gains without actual gameplay. Re-measure exact final combined candidate once diagnostics and UI land; do not repeat full benchmark for every intermediate PR. W2-P remains pending hardware evidence.

## Three-way historical comparison — owner request

The pinned common ancestor of fork main and custom/main is e9bccdc6ee38f1b7c29b7ee7ea8b260babcc2a3f (upstream 0.7.1, Nov 7). Use it as the original fork baseline, not the moving main branch. The fork mirror main at verification was 6f44cc1027d2d69f398c06850a77b8b45dbc5ac9 (0.7.2-nightly.4); that is six commits divergent from the common ancestor, not the original version. Optional fourth reference only. The three main comparisons are original 0.7.1, released Personal v1.1.0 (6de4ad684f4268b685c630d639977c439bf557ed), and the eventual exact combined prerelease SHA.

Original 0.7.1 is not compatible with QUICKCLIP_PERSONAL_DATA_DIR: AppSettings.cs hardcodes the roaming QuickClip settings path, Util.cs hardcodes its local log path, SingleInstance uses the original per-user identity, RecordingEnabled defaults true, and the DEBUG --clips argument is handled after Host.Start and is not profile isolation. Use a separate disposable signed-in Windows user on the same physical PC for stock baseline tests; pre-create scratch settings with recording false, mic false, updates false, scratch clips directory before launch, then deliberately enable capture only for matched test. No real production profile or installer registration. Do not alter original application source to fake isolation. Original README builds with .NET10/C++ build tools, scripts/get-deps.ps1 then dotnet build; build Release using pinned SDK, verify native DLL, FFmpeg and libmpv. Historical get-deps resolves latest artifacts rather than pinned downloads; prefer trusted known compatible cached dependencies or actual original release artifacts (if verified), record versions+hashes, disclose build dependency drift. Never overwrite root checkout dependencies. Separate detached worktrees and use this docs branch's resource sampler externally with explicit PID. Actual build/start commands should be verified at integration boundary.

Run TWO comparisons: (1) recorder off/tray and actual default workflow, noting original auto-recording vs Personal armed-no-game semantic differences; (2) identical active capture workload using intersection of available codec/resolution/FPS/bitrate/preset/source/audio features, measure output equivalence. For stricter video-only measurement disable mic and split lanes, account for unavoidable stock desktop audio; do not describe stock as equivalent if routing differs. A separate preferred-default test is valuable but not controlled apples-to-apples. No recording private content. Compare actual game FPS + 1% lows, capture cadence and saved duration/quality, not just memory. Record baseline without any recorder separately. No measured improvement yet.

## Interactive multi-version runner

Use PowerShell 7 in the isolated worktree. Prepare under the owner account. From the disposable account, run `whoami /user` to obtain the SID. Use a fresh signed-in Windows account and give it access to this study directory and the scripts. Close QuickClip, OBS, builds and encoding jobs before measurement. Preparation launches no app and installs no packages system-wide.

    pwsh -File ./scripts/benchmark-wave2.ps1 -Mode Prepare -Study wave2-final -Candidate <exact-SHA> -DisposableSid <disposable-SID>

Prepare caches and checks both stock release archives against their pinned SHA256 digests. It packages the supplied Git commit from a detached worktree using pinned dependencies and a strict self-contained Personal build. The study manifest records release and candidate SHAs and package hashes. Run rechecks the selected archive before extracting it to a fresh attempt directory. Keep the study on a local disk with sufficient space for builds, repeated extractions and clips.

Sign into the registered disposable account for every variant, including the no-recorder control. Original v0.7.1 uses that account's normal QuickClip AppData and IPC. The runner writes recording-off, microphone-off, updates-off scratch settings before launching it, and rejects original settings from outside the study. Personal variants receive separate scratch profiles. Diagnostics are disabled outside DiagnosticsOn. No recorder starts automatically for the Game baseline. The owner enables recording deliberately for each active test.

    pwsh -File ./scripts/benchmark-wave2.ps1 -Mode Run -Study wave2-final -Scenario IdleVisible -Repetitions 3 -Nvidia
    pwsh -File ./scripts/benchmark-wave2.ps1 -Mode Run -Study wave2-final -Scenario Game -Comparison Controlled -Repetitions 3 -Nvidia
    pwsh -File ./scripts/benchmark-wave2.ps1 -Mode Run -Study wave2-final -Scenario Game -Comparison Preferred -Repetitions 3 -Nvidia
    pwsh -File ./scripts/benchmark-wave2.ps1 -Mode Report -Study wave2-final

Run each protocol scenario with the matching name. Enter the same workload identifier and timing parameters on resume. Complete and explicitly skipped repetitions are retained. An interrupted or failed attempt keeps its scratch evidence and starts a new directory on retry. The runner alternates order, stops only its owned QuickClip process, and asks the operator to set up scans, recording, approved fixtures, game scenes and saves. Compare the recorded settings and mark non-equivalent builds unsupported. The common-capability and preferred-setting series remain separate. Operator startup and quit timings include reaction time.

Use the same independent frametime collector and capture interval for all Game repetitions. Export numeric milliseconds as a CSV with one `FrameMs` column and no identifiers. Supply actual observed capture drops or save times when available. The runner samples game and recorder separately and exports scratch video stream metadata via FFprobe when available. Compare the emitted duration, codec, dimensions, frame rate and audio streams with the intended settings. NVIDIA adapter data remains device-wide. Keep raw CSV, JSON, generated fixtures and profiles under the ignored study directory. The report compares across repetitions and retains missing values. The native RAM-ring script remains a separate source/encoder test.

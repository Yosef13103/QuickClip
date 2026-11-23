# Recording Settings: measurable quality, honest storage, and highest practical game FPS
2026-11-23 | QuickClip Personal | Source baseline integration/development b220c9dc48d026734f57afc7cec73e8dd3a5e5f5

## Product problem

Nontechnical users should not have to understand bitrate, NVENC presets, GOP, B-frames, or container formats to record a sharp, smooth replay without unnecessarily hurting their game or filling the drive.

The former Basic interface presented Low / Medium / High / Ultra / Indistinguishable as if they were overall quality grades. In actual code these labels primarily set a video bitrate *target*. Resolution, FPS, encoder, capture backend, audio stems, and NVENC P2/P3/P4 are independent. The highest bitrate can preserve extra details while consuming disproportionately more storage; it cannot restore frames lost earlier in capture or make a game render faster. Indistinguishable cannot guarantee visually lossless results.

The legacy Basic disk text claimed a full-length clip 'takes about X', calculated as the chosen video bitrate budget plus 160kbps per estimated AAC track, multiplied by replay length. VBR output can greatly under- or overshoot that target. Shared MKV source pools and qclip manifests further separate logical clip references from physical unique bytes. This requires explicit approximate language and later calibrated observations.

## Verified November 23 experiments

Machine: Windows 10, NVIDIA RTX 3070 (driver 621.24), Ryzen 5 5600X, 32 GB. User replaced the troublesome monitor/power cable, and disabled MSI Afterburner overclocking. FurMark OpenGL previously generated a TDR while the user reports accidentally disconnecting the active monitor; correlation is plausible, root cause not proven. GravityMark D3D12 subsequently ran without a new TDR in bounded tests.

The official GravityMark 1.89 MSI was obtained from Tellusim, and Windows Authenticode verified the Tellusim Technologies Inc. signature. Its payload was extracted as a portable test tool in external .local/benchmarks, not installed. D3D12 automated scene launch, resolution, asteroid count, timing, and controlled stopping are implemented in gravitymark_recording.py. PresentMon could not produce valid GravityMark FPS CSV on this host (lost ETW events, no usable file), so measured gameplay 1% lows are **unavailable**. FurMark baseline FPS varied greatly and is not game FPS.

Native QuickClip D3D11 capture of GravityMark: with a 20,000-asteroid window and uncapped graphics load, 7.5 Mbps HEVC P2 @ 2576x1080/60 yielded 341 decoded frames/8.05s (about 42.4fps) with 216 missed timeline slots. The corresponding VSync-limited test yielded 482 decoded frames/8.034s (about 60fps), with zero missed slots. These are isolated samples under changed GPU-pressure conditions, not evidence that QuickClip should alter game VSync automatically. They demonstrate that **capture smoothness depends on GPU headroom**, not just chosen bitrate.

### Same-frame compression quality test (most useful controlled result)

A native wide-window GravityMark source recorded by QuickClip at HEVC 2576x1080/60 was decoded into a lossless FFV1 intermediate for **240 identical frames**. It was then re-encoded through external FFmpeg HEVC NVENC P2 HQ-VBR with QuickClip-like 1s GOP, zero B-frames, 1.5x maxrate. VMAF is relative to the **already lossy high-bitrate native capture**, not a lossless renderer output; this measures additional compression from re-encoding, not original game fidelity.

| Former quality | Auto target Mbps | Actual encoded Mbps | Normalized MB/min | Relative VMAF |
|---|---:|---:|---:|---:|
| Low | 9.349 | 9.950 | 74.62 | 94.6857 |
| Medium | 15.582 | 16.316 | 122.37 | 96.2340 |
| High | 24.932 | 25.895 | 194.22 | 97.0680 |
| Ultra | 38.956 | 37.447 | 280.85 | 97.4027 |
| Indistinguishable | 62.330 | 49.968 | 374.76 | 97.6037 |

All five files contain exactly 240 frames over 4 seconds. Storage is measured bytes normalized to 60 seconds, not a measured one-minute recording; audio absent. Source and raw metrics live under .local/benchmarks/output/gravitymark-20261123/same-source-visual/results.csv.

At this source, High to maximum requires ~93% more storage for +0.536 VMAF; High to Ultra ~45% more for +0.335 VMAF. More bitrate can still matter on fine textures, particles, tiny text and intense motion; this example does not set a universal human-visible threshold. No claim that a VMAF difference of X equals 'indistinguishable'.

### Five independent native source captures

A separate native QuickClip bitrate ladder recorded windowed GravityMark in five live sessions (each 8 seconds), with actual physical MKVs and frame counts measured by ffprobe. Low 11.31Mbps, Medium 19.23Mbps, High 27.90Mbps, Ultra 37.69Mbps, Maximum 18.86Mbps. The last bitrate was lower despite a higher target because the live scene was different/easier to compress; it MUST NOT be used as a same-content codec or visual ranking. Raw data: .local/benchmarks/output/gravitymark-20261123/quality-ladder-20261123.csv.

## First implemented Basic UI pass (this isolated worktree)

- Keep all persisted quality IDs, actual bitrate formula, codec default, 60fps Personal default, P2 live NVENC default, and Advanced controls unchanged.
- Basic Quality labels become: Smallest files, Save more space, Balanced detail (recommended), Extra detail, Maximum detail (large files). These map 1:1 to existing Low / Medium / High / Ultra / Indistinguishable. Switching Basic/Advanced rebinds labels while suppressing SelectionChanged persistence/restart; per-game selection remains.
- Basic fields now say Picture size, Picture detail & file size, Recording smoothness.
- Add visible short guidance: more video detail can increase disk use, does not increase FPS; recording FPS is distinct from game FPS.
- Existing 720p30, 1080p60, native60 guided setup bundles use simpler names, with descriptive tooltips. Internal Tags still map to existing Performance/Balanced/Quality values and do not modify code policy.
- Basic resource line says Storage planning guide, and explicitly says real file sizes vary with game motion. Basic drive usage no longer promises a fixed count of clips from the nominal bitrate formula. Advanced line explicitly calls the figure a nominal bitrate-budget estimate.
- All cosmetic/wording changes are in this isolated branch, no installer/Production/current Development modification.

Verification: focused Release build succeeded, zero warnings/errors; 22/22 filtered tests pass, including tests that ensure basic quality wording does not change serialized quality values. Optional WPF render fixture should be run separately in isolated profile to check visual wrapping.

## Target product experience (next implementation phase, not claimed complete)

One clear row of mutually exclusive starting priorities at the top of Basic Recording:
- **Keep my game smooth**: lighter capture profile, not a guarantee of any particular game FPS. Does not alter game settings.
- **Recommended**: 1080p short side, 60 recorded FPS, balanced image detail. Explicit real output dimensions for ultrawide.
- **More detail**: native display resolution, 60 recorded FPS, higher storage budget. More resource intensive and not guaranteed visually different in all content.

These are shortcuts, not permanent modes, and must remain decomposable into independent user choices:
- **Recording smoothness**: Standard 30 / Smooth 60 recommended / Extra smooth 120 FPS where supported, with advanced values available. Explain this controls the saved video's temporal smoothness, NOT the game's engine FPS.
- **Picture size**: Same as screen, 1080p-sized, 720p-sized, showing actual width x height beside chosen option. On a 3440x1440 ultrawide, 1080p short side is 2576x1080, not 1920x1080; avoid implying a 16:9 crop without choosing one.
- **Picture detail and file size**: compact / balanced / extra detail, mapped to measured codec-aware quality budget; changing this alone must NEVER silently change output resolution, recording FPS, or game settings. A tiny one-sentence hint should explain motion/detail and disk impact. Do not display raw Mbps by default.

The top recommendation should not always blindly select max quality, and should never secretly cap game FPS, enable VSync, modify power plans, overclock, change display topology, install drivers, or change hardware encoder. Preserve Advanced mode with all explicit raw controls and per-game overrides.

## Capture-health feedback (high-value next feature)

Expose **target versus actual saved FPS** and dropped/missing timeline slots through existing CaptureEngine.GetStats and native diagnostics. A lightweight indicator can show:
- Recording healthy: targeting and capturing 60/60fps recently.
- Recording is losing frames: for a sustained sliding window, actual output below target; clip may look less smooth. Do not confuse this with game FPS; avoid false alerts from deliberate still frames, short startup transients, display permission changes, or stalled/disconnected monitor.
- Offer plain choices when affected: Reduce recording size, reduce recording FPS as an explicit opt-in, or consider a user-selected game FPS cap/graphics adjustment. Never apply game-side changes automatically.

Use rolling windows, hysteresis, minimum sample count, and per-session resets; record true encoded/submitted/dropped counters and verify against file ffprobe in tests. GPU utilization alone is a diagnostic hint, not proof that NVENC or GPU saturation caused the drops. The uncapped/vsync comparison demonstrates why this is more actionable than choosing P2/P4 by name.

## Honest storage estimates and shared MKV

A planning model should distinguish:
(1) nominal bitrate budget (current equation);
(2) observed recent physical encoded bytes per second, measured from the current session/saved files at same resolution/codec/FPS/game;
(3) library physical unique bytes, which count pooled immutable MKV source once, plus actual exported files;
(4) per-manifest logical length, which is NOT unique on-disk usage.

If sufficient recorded evidence exists, show 'Usually around X–Y MB for 1 minute in this game' with a small sample count/confidence note. Otherwise: 'No reliable size estimate yet — saved clips can vary with motion.' Never promise an exact figure or a hard ceiling. Preserve free-space reading from Windows. The original MP4 export should prefer stream copy for unchanged A/V. Sharing/export size caps are a separate **post-recording** action, not a recording format target.

For visual previews, use a bundled, licensed reproducible scene with source-owned lossless frames, encoder version and exact resolution; generate a few sample compressed frames offline and let a user compare before spending more space. Do not encode sample VMAF at every recording frame. The current GravityMark footage is test evidence, not assumed licensed for shipping as an embedded app asset.

## Algorithmic improvement candidates — must A/B before shipping

- Codec-aware defaults rather than simple latest-codec-is-best: verify H.264/HEVC/AV1 capability, capture throughput and decoder compatibility with the same frames. RTX 3070 does not provide hardware AV1 encoding.
- Explore CQVBR or quality-based rate control for live capture versus existing HQ VBR, with a max rate/VRAM bound; test recorded FPS, frame drops and physical bytes. Do not adopt OBS settings wholesale.
- Export-only offline quality-focused encoding can be slower and use additional B-frames/longer GOP if it improves equal-quality file size; preserve original clips and stems, offer optional smaller share copy. Keep rolling replay's 1-second keyframe and zero B-frame constraints unless a separate timeline/seek/snapshot compatibility test proves otherwise.
- Measure CPU%, GPU 3D%, GPU Video Encode, disk throughput, battery/power where relevant, exact file bytes, PSNR/SSIM/VMAF on identical source, and game present frame time with a working ETW/performance counter path. PresentMon on Jambo did not yield valid GravityMark ETW CSV.
- Re-test with real repeatable recorded game footage only when a deterministic demo/replay exists. Graphics load should be warm and consistent. Separate output capture FPS from game rendering FPS and exclude display hotplug interruptions.

## Acceptance criteria for next stage

A new user can set up recording without seeing Mbps, NVENC, a lossless claim, or unqualified 'X MB per clip'. A user can independently change recorded smoothness, picture size and picture detail, and the summary updates without changing unrelated dimensions or per-game overrides. Basic/Advanced switching preserves serialized values and must not restart capture. Gallery and storage counts do not double-count shared MKVs. A saved video is verified at its real dimensions, FPS and audio streams. Under GPU pressure users get a truthful capture-health message without automatic game setting changes. Automated tests and guided WPF render fixtures cover label ordering, line wrapping, setting persistence and missing-encoder cases.

## Worktree boundaries and evidence

- Bench lab: E:\Stuff\Personal\Personal Projects\Codes\QuickClip\.local\benchmarks (unshipped), including scripts and raw clips/CSV/JSON.
- UI source: E:\Stuff\Personal\Personal Projects\Codes\QuickClip\3. Branches\recording-clarity-lab, branch bench/recording-ux-20261123 based on b220c9d; no merge/push/PR/release.
- Previous separate shared-MKV duration fix at bench/quality-storage-20261122, commit 3a57128, not included in these UX changes.
- Source evidence: src/QuickClip/Recording/RecordingPresets.cs; src/QuickClip/Views/SettingsView.xaml(.cs), SettingsView.Experience.cs, SettingsView.Library.cs; src/QuickClip/Library/LibraryModels.cs; src/QuickClip.Capture/Engine.cpp.
- External context: https://obsproject.com/kb/advanced-recording-settings-guide , https://obsproject.com/kb/advanced-nvenc-options , https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-application-note/ , https://gravitymark.tellusim.com/

## Implemented continuation (2026-11-23, no Codex)

The isolated bench/recording-ux-20261123 branch was merged with current integration/development c3814fc (merge e5abc9e) **without modifying Development, Production, the installed app, or unrelated worktrees**.

### Camera indicator captures real recording health

- New src/QuickClip/Shell/CaptureHealthSignal.cs debounces a native recorder capture graph's actual one-second submitted FPS (native GetStats.Fps) and cumulative missing Desktop Duplication timeline slots (GetStats.DroppedFrames). ActiveTargetFps reflects the graph's resolved game override, not always global defaults.
- Startup grace 2 seconds; only sample intervals >=0.7s; reset on epoch changes, counter regression or long sampling gaps. Warn after 2 consecutive bad ~1s samples (>=10% missed slots or <82% of target submitted FPS), or immediately after severe >=25% missing slots, with a floor of 15. Isolated 1-2 misses do not trigger.
- Return to normal after 3 consecutive good intervals; signal independent of microphone confirmation. A slow 950ms soft-red opacity pulse only on the existing camera glyph when recording indicator is enabled and capture is active; no sounds, toasts, new screen popups, or game setting changes. If indicator is disabled or hidden, no warning HUD appears.
- The warning is based on recorder *capture health*; it must NOT claim the game FPS dropped or that VBR quality is low. Sudden display hotplug/authorized source transitions must reset rather than carry stale loss data.
- Settings > recording icon feedback includes a four-second “Preview missed-frame warning” button that renders the same overlay without initiating recording; existing normal preview remains.
- Unit tests in CaptureHealthSignalTests cover isolated losses, persistent degradation, severe jumps, frame-rate-only degradation, recovery, and capture epoch resets.

### Straightforward preset choice

- Onboarding, Basic settings and Advanced settings present three equal-width preset cards: Performance, Balanced, Quality. All are optional user choices; card selection visible and custom state preserved. Advanced retains all independent encoder/bitrate/frame controls. A synthetic WPF screenshot exposed non-stretching controls, corrected before commit.
- On capable direct hardware encoders: Performance = 720p short-side / 60fps / Medium bitrate quality / P2; Balanced = 1080p short-side / 60fps / High / P2; Quality = native capture dimensions / 60fps / High / P2. Quality intentionally does NOT choose Ultra/Indistinguishable's potentially large bitrate; screen size is the primary added-detail tradeoff, not an unsupported promise of losslessness.
- For a slower MediaFoundation software fallback the Performance bundle conservatively targets 720p/30fps, while the user retains explicit Basic FPS control and Advanced encoder choices.
- “Pick for my PC” checks the *actual available supported encoder ID*, recommends Balanced for direct vendor hardware encoder, Performance for Windows fallback, and asks for device verification when none is detected. This is capability-based only and does NOT claim per-game calibration, game 1% low benchmarking, or GPU performance guarantee. No GPU clock, power plan, frame cap, VSync, or graphics settings are modified.
- Basic detail choices hide Indistinguishable from new selections; if a profile already uses it, its selection remains visible and persists. Advanced retains all five legacy enum values and technical controls.
- Existing settings compatibility, manual and game override boundaries, and normal capture behavior are preserved. No forced automatic profile switching during game sessions.

### UI visual verification and desktop shortcut

- Branch Release build (0 warnings/errors) and 29 targeted tests passed before user visual preview launch.
- Build an isolated app with scripts/dev.ps1 -BuildOnly using the shared SDK environment, never Workspace.ps1 -Action Launch (it owns the canonical Development profile).
- Desktop shortcut: C:\Users\Yosef\Desktop\QuickClip Recording Preview.lnk
- Shortcut launcher in ignored .local/launchers/Run-RecordingPreview.ps1; isolated settings, clips and process mutex under .local/recording-preview-profile. It rebuilds when Git HEAD changes, but never rebuilds the executable while that same preview instance is running. If already running, it focuses the current preview and defers update to next restart. Existing regular QuickClip configuration is untouched.
- Verified actual branch QuickClip.exe --show process and independent profile persisted.

### Real game 1%-low diagnostics

- Latest official CapFrameX v1.9.2 portable ZIP was downloaded and verified against publisher's SHA256SUMS.txt (0C3374D382ABB359439F8898BDAAA1C4E6A7CC0FDD936022C0EAD0CA213BDA08); extracted into ignored .local/benchmarks/tools/CapFrameX-1.9.2. Installer and system dependencies were NOT modified.
- CapFrameX GUI launch raised Windows UAC and the user cancelled. We did not bypass UAC or retry that GUI.
- Its embedded standalone CX-PresentMon 2.6.1 in a bounded GravityMark D3D12 test still reported lost ETW events and wrote no usable CSV, matching earlier PresentMon 2.3. No valid game 1%-low figure resulted.
- Existing RivaTuner Statistics Server (RTSS) 2.21 on Jambo exposes RTSSSharedMemoryV2. After read-only validation, .local/benchmarks/rtss_benchmark_wrap.py temporarily set STATFLAG_RECORD only for its own new GravityMark process ID, restored it before exit, and measured native RTSS rendered FPS and 1% low over ~22 seconds each: baseline A1 57.6/55.2 FPS, with QuickClip 56.5/53.9 FPS, baseline A2 57.1/55.0 FPS. Scratch report: .local/benchmarks/output/rtss-bench-20261123/RESULTS.md. No RTSS config, MSI Afterburner overclock, performance profile, global state or elevated permissions changed. These are game-like, not actual game replay results.
- Real RTSS 1% low metrics are now verified from the temporary benchmark session. The client must still distinguish presented/game-frame statistics from encoded-recording FPS; further real-game tests need a deterministic playable demo/replay, repeated ABBA cases, and no display hotplug interruptions. Never infer 1% lows from sporadic instantaneous FPS reads.

### Test and handoff boundaries

- Source & docs remain in bench/recording-ux-20261123, not merged/pushed.
- Bench tools and 1GB+ scratch recordings are retained under ignored .local/benchmarks; do not include in source commits.
- Existing user-facing previous quality report and measured 62s native MKV remain available; no false precise size estimates have been reintroduced.

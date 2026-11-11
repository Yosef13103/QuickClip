# Performance follow-up and integration handoff — 2026-11-11

## Preset decision

New Personal settings use NVENC P2/HQ. An explicitly saved P2, P3 or P4 remains unchanged, including old numeric enum values. Settings with no preset field receive the current profile default. Stock-compatible builds retain P4. Resolution, FPS, quality/bitrate calculation, encoder selection, audio and replay settings are unchanged. P2 applies to NVIDIA recording presets generally, not only HEVC; AMD/Intel encoders ignore this option. The low-level native ABI and benchmark's explicit P4 baseline remain unchanged.

The owner reports Marvel Rivals at native 3440×1440/~60 fps recording: approximately 17% Video Encode with HEVC P2/P3 versus 37% with P4. Game FPS was ~133–140 for all three versus ~160 with recording off. They saw no meaningful quality difference between P2 and P4. These are informal user observations, consistent with the previous desktop encoder measurements, not an objective quality-equivalence result. P2 is a practical recommended default, not a promise of identical quality or restored game FPS. Existing profiles with saved P4 must select P2 deliberately.

## Remaining overhead: evidence and limits

The reported frame-rate difference is about 12.5–16.9%. Expressed as reciprocal FPS, average frame time changes from 6.25 ms to roughly 7.14–7.52 ms: +0.89–1.27 ms. Those calculations do not establish a cause, percentile pacing, or an additive recorder GPU cost. The unchanged game FPS across P2/P3/P4 is evidence against Video Encode utilization being a sufficient predictor of game performance; it does not prove the encoder has no scheduling impact.

Code inspection confirms these remaining costs and observability limits:

| Area | What exists | What is not established |
|---|---|---|
| DDA acquisition | FFmpeg source pacing and cropped GPU frames; repeats on an idle desktop | Whether acquire/release timing stalls the game's rendering |
| WGC | Window/monitor capture; can return more source frames than the engine submits (prior 144→60 observation) | DWM/driver scheduling cost or the game's present latency |
| GPU scaling | DDA video-processor blit only when requested output differs from cropped source; WGC scales inside its source | Isolated GPU blit/conversion duration; native uncropped DDA does not use the added scaler |
| Frame grid | Frames are captured/processed before duplicate timeline slots are discarded | The amount of avoidable upstream work for a specific game/backend |
| NVENC | D3D11 hardware frames, GPU encoder submission, packet drain/ring insertion | Separate driver wait, RGB conversion, GPU encoder execution or render-queue contention |
| CPU transfer | NVENC path has no explicit `hwdownload`; Media Foundation has CPU readback/conversion | Internal driver copies; zero GPU copies cannot be inferred from no CPU download |

Existing diagnostics measure source-return rate, submitted/encoded rate, timestamp gaps, CPU wall-clock capture wait and combined encoder-path time. The latter includes scaler submission, timing logic, packet handling and driver calls; GPU operations are asynchronous. These numbers cannot attribute game FPS loss to DWM, VRR, copy engines, GPU memory bandwidth or synchronization. Task Manager/nvidia-smi counters are device-wide utilization rather than game frame-time traces. No new causal gameplay measurement was collected in this follow-up, and no speculative capture-loop or synchronization change was made.

The next measurement should alternate recorder-off and P2 recording in the same reproducible game scene, at least three bounded repetitions in reversed order. Record game FPS/frametimes (including 1% lows if the existing game/overlay exposes them), actual backend/source/output/FPS, separate 3D/Copy/Video Encode graphs, GPU clocks and recorder timing. Compare native monitor DDA, native game-window WGC, then intentional scaling, without other recorders. Keep bitrate, FPS, scene and graphics settings fixed. Only after reproducing the loss should a short GPU/ETW trace or timestamp-query instrumentation be considered to attribute queue stalls. No new profiler dependency or global setting change is required here. Do not use an uncapped game as an assumed zero-contention baseline for unrelated runs.

## Software H.264 on Ryzen 5 5600X: bounded feasibility assessment

The bundled FFmpeg exposes `libx264` with CPU pixel formats including NV12/yuv420p; this was checked with `ffmpeg -h encoder=libx264`, with no capture or media access. The native engine already constructs a codec by name and muxes encoded packets, so the ring, separate audio streams and MP4 save path can be reused. However `NeedsSystemFrames` currently recognizes only `_mf`; simply selecting x264 would feed it D3D11 frames. A correct implementation needs an explicit software path, supported pixel-format negotiation, download/conversion, deliberate speed/thread settings and bounded backpressure. Native scaling currently depends on hardware frames, so combining CPU output with scaled DDA needs ordering work too.

At 3440×1440/60, BGRA readback alone carries about **1.19 GB/s** of raw pixels; GPU conversion to NV12 before downloading would reduce payload to about **446 MB/s**. These are arithmetic estimates (width × height × bytes/pixel × FPS), excluding alignment/copies, not measured PCIe throughput or timing. A CPU route still performs capture and a GPU-to-host transfer, then consumes CPU for conversion/encoding. On a six-core/twelve-thread 5600X, contention with game simulation/render submission could worsen gameplay pacing. It might help a specifically GPU-limited workload with spare CPU capacity, but there is no evidence here that it would recover the reported FPS. Real-time native ultrawide quality/speed on this CPU has not been measured.

A future opt-in **Software H.264 — advanced** choice should describe readback bandwidth, CPU/thermal and compression tradeoffs, expose a bounded speed/thread budget, verify real-time cadence and audio sync, retain one-second clip-start keyframes, and fail visibly on overload. Test synthetic frames first, then supervised matched gameplay against P2. Never automatically switch encoders or silently lower output quality. No CPU pipeline was added in this follow-up. References: [FFmpeg libx264 options](https://ffmpeg.org/ffmpeg-codecs.html#libx264_002c-libx264rgb), [DDA hardware frames and software-encoder download requirement](https://ffmpeg.org/ffmpeg-filters.html#ddagrab).

## Choosing a capture target

- **Game preference on:** automatic game-first targeting with selected-monitor fallback. A detected, valid game uses WGC even if the monitor backend is explicitly Desktop Duplication. External covering windows are excluded from game-window capture; overlays rendered into the game itself can remain. Windows 10 may show the yellow WGC border.
- **Game preference off:** monitor capture, including Discord or other windows covering the game. Choose Desktop Duplication for the monitor path without WGC's indicator. Auto prefers DDA through 60 fps and WGC above 60 fps; actual backend is visible in diagnostics.
- There is no arbitrary manual game-window picker, strict game-only/no-fallback mode, or per-game capture-profile editor yet. Those belong to future integration. Minimized exclusive-fullscreen games may stop rendering. No injection, hooking or border bypass was introduced.

## Integration boundaries

The base is published `b99c650`; its application tree matched the former local archive. Canonical `custom/main` and `origin/custom/main` were identical before this follow-up. Old local history remains archived; agent documentation is ignored, not republished. This work belongs on `fix/personal-performance-handoff`, with integration/push to shared `custom/main` left to the integration agent. No reset/rebase/force push is needed.

Behavioral edits are limited to `AppSettings.cs`, `Views/SettingsView.xaml` and `.xaml.cs`, plus a new defaults test. The UI workstream must retain the conditional Personal P2 default, explicit preset persistence and capture-target explanations when resolving Settings changes. Native capture, Recorder, GameDetector and game-discovery services are untouched. This work can be reviewed/cherry-picked into UI/game-discovery integration; it does not establish that those branches have already been merged or validated together.

Outstanding hardware acceptance: repeatable game frametimes and backend attribution, P2 quality in difficult motion (especially H.264 as well as HEVC), visual border/occlusion on both monitors, high-FPS cadence, Sonar separation/hotplug/long-run drift and preview leakage. Keep recording safety and project/gallery regression checks in the final combined build. See [previous measurements and benchmark commands](recording-diagnostics.md).

## Follow-up validation

- Personal full automated suite:247 passed,16 explicitly opt-in Windows tests skipped,0 failed. No new screen/audio recording was performed for this follow-up.
- Stock-compatible Release build:seven focused default/persistence tests passed, confirming P4 for missing presets and preservation of all saved choices.
- Personal Release developer build:0 warnings/errors. Native capture and libmpv loaded, eight active audio endpoints enumerated, and bundled FFmpeg/FFprobe runtime checks passed. No system device configuration was changed.
- The existing background Auto-Rebuild instance was not stopped or reconfigured. Use the normal development launcher for the new Personal build; an already-saved P4 setting remains P4 until the user selects P2.

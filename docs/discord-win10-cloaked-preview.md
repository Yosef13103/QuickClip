# Experimental Windows 10 Discord cloaked multi-window acquisition

**Status:** local feature-branch dev candidate (not merged or released). Owner approval of integrated multi-pop-out recording remains pending. Experimental toggle defaults off and is Windows 10-specific; direct WGC and Windows 11 remain unchanged.

## Existing behavior retained
- Reuse the existing Discord process/window verification, `ActivityProbe`, `DiscordGroupPolicy`, modes, game priority, dynamic pop-out add/remove/reorder, ordinary `GroupCapture` 70/30 GPU compositor, encoder/replay ring, audio, playback and export.
- Experimental acquisition difference: `GroupCapture::Source` creates a `DiscordDwmHost` for **each** authorized HWND (1–4), already verified by the same policy. The host starts hidden at 1×1, gets `DWMWA_CLOAK` before being shown, and mirrors only that HWND with a DWM thumbnail. WGC captures each cloaked host, then **the existing compositor** outputs one video stream. This does not run another Discord process or encoder.
- When source DWM dimensions change, the host resizes to those actual source dimensions, and `GroupCapture` recreates the source WGC pool as necessary. The unchanged compositor preserves aspect ratio within fixed session output dimensions. The owner can expect letterboxing or proportional downscaling rather than stretching. No oversized research canvas.
- Source identity is pinned to the authorized HWND/thread/PID/start time. Host cloak loss, invalid identity or unrecoverable DWM source errors stop capture safely rather than permit arbitrary desktop or game fallback. Minimized windows skip refresh without authorizing replacement.
- The capture video thread now pumps Win32 messages for its owned cloaked HWNDs, which prevents Windows reporting a healthy long-lived capture host as Not Responding in the repeated-session benchmark.
- Factory caches are cleared after the video worker releases its WGC objects and before COM uninitialization, preventing a reproduced subsequent-session access violation caused by a cached WinRT activation factory pointing into an unloaded DLL. Implemented in the existing C++17 `GroupCapture.cpp` WinRT compilation unit, called by both video worker cleanup paths.

## Evidence on Jambo (Windows 10, RTX 3070)
- Strict native and managed build **passed**, 0 errors/0 warnings. Focused tests passed, full headless suite **980 passed, 0 failed, 24 skipped (1004 total)** on Nov 21, 2026.
- Unsupervised capture-only ABBA CPU comparison (one existing visible main Discord source, 1920×1080, H264 NVENC P2, 60fps, no audio, four matched ~10s runs):
  - Regular WGC: total CPU 0.792%, 0.781%.
  - Experimental cloaked: total CPU 0.715%, 0.663%, one cloaked host and **zero detected hung hosts** per run.
  - All four sessions completed 60fps, no recorder-reported drops or access violations. No saved private media. The small CPU difference is not evidence experimental capture is inherently faster; GPU time/load and real-game frame times remain unmeasured.
  - Process private bytes rose across both modes in the same process. This may reflect caching or a lifecycle defect, and is not presently attributable to cloaked capture. Investigate separately.
- Full crash analysis, benchmark logs and follow-on work: `7. Evidence/discord-multi-cloaked-benchmark-2026-11-21.md` and `6. Research/Capture/discord-dwm-probe/`.
- Original separate research prototype was visually accepted by the owner for no persistent host/yellow outline on their system. This **does not establish full integrated multi-window visual acceptance** or guarantee system capture indicators are absent on other Windows installations.

## Pending acceptance and boundaries
- Owner-run local isolated dev launcher: `Desktop/QuickClip Discord Capture Tests/7 - Discord Mirror Dev Preview.cmd`. Compare normal versus experimental under equivalent settings. Check dynamic pop-outs, resize across monitors, alt-tab/taskbar, status responsiveness, saved clips and audio, source freshness and window/host teardown.
- Start game-concurrent GPU/FPS/1% low and VRAM tests only after window behavior is accepted. Earlier *separate PrintWindow* research coincided with NVIDIA driver hangs in Marvel Rivals; don't generalize that risk to this method or dismiss it.
- Do not push/merge into `integration/development`, release or install into production until the owner accepts integrated capture and a separate review confirms code/lifecycle quality. No undocumented WGC indicator bypass, Discord injection or game capture changes.

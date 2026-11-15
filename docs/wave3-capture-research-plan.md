# Wave 3 preparation — two tracks, one source contract

Owner decision: investigate isolated game capture first, then companion capture. Both research tracks can proceed independently; do not start production native architecture changes before Wave 2 settles. A single optional webcam/video picture-in-picture and optional multi-monitor capture are stretch goals, not completion requirements. No multiple cameras/videos or mandatory multi-display. The current roadmap remains the ledger reference until the integration owner reconciles it. This note records research decisions, not dispatch or acceptance.

## Evidence at pinned snapshots

QuickClip d2c34d5185adfcba88bee2f2e1e8ee4e3ece74ef: Engine.cpp VideoPipe builds FFmpeg ddagrab/gfxcapture filter graph, opens encoder from sink geometry, pixel format and AVHWFramesContext; VideoLoop creates D3D11VA device per adapter and reads av_buffersink_get_frame. GPU scaler and existing NVENC, encoded replay ring, WASAPI source lanes and muxing must remain usable. Source acquisition is not yet an interchangeable abstraction: replacing only window selection cannot deliver game-only capture.

OBS Studio fe1ed7955f300efd17fc59f65938388e43d8bb06:
- plugins/win-capture/game-capture.c opens shared texture using gs_texture_open_shared and exposes hooked/unhooked signals plus get_hooked returning title/class/executable (not strong PID/HWND identity). Window matching and retries belong to OBS and may retarget. QuickClip must verify exact approved target at acquisition and while frames publish; if unable to verify, adapter is not acceptable.
- libobs/obs.h exports obs_reset_video, obs_open_module, obs_source_video_render, obs_add_main_rendered_callback, obs_source_get_proc_handler. libobs/graphics/graphics.h exposes gs_texrender_begin/get_texture, gs_texture_get_obj and shared texture support. libobs-d3d11/d3d11-texture2d.cpp uses D3D11 shared/keyed-mutex flags and OpenSharedResource. These are concrete candidate primitives for a GPU bridge, not proof of zero copy or supported arbitrary texture ownership.
- obs_add_raw_video_callback receives video_data raw frame planes. This is a useful correctness fallback prototype, not recommended production GPU-first bridge; likely staging/readback/upload overhead. obs_render_main_texture renders the output into the current graphics context, not a return value holding an owned D3D11 texture.
- OBS DirectShow plugin uses libdshowcapture; evaluate separately only if optional video PiP is prioritized. Device acquisition, compositing, timing and permission still required.

Primary references:
https://github.com/obsproject/obs-studio/blob/fe1ed7955f300efd17fc59f65938388e43d8bb06/plugins/win-capture/game-capture.c
https://github.com/obsproject/obs-studio/blob/fe1ed7955f300efd17fc59f65938388e43d8bb06/libobs/graphics/graphics.h
https://github.com/obsproject/obs-studio/blob/fe1ed7955f300efd17fc59f65938388e43d8bb06/libobs/obs.h
https://docs.obsproject.com/reference-core
https://docs.obsproject.com/graphics
https://obsproject.com/kb/game-capture-source
https://obsproject.com/kb/game-capture-troubleshooting
https://obsproject.com/kb/capture-hook-certificate-update
https://github.com/obsproject/libdshowcapture

## Track 2 first: isolated game capture feasibility

Preferred experiment: host selected libobs modules in-process in a small native standalone harness (not OBS Studio GUI or background installation), create only one authorized game_capture source, disable OBS audio/output/encoding, render source to a GPU texture, use D3D11 shared resource or explicit same-adapter GPU copy to QuickClip's D3D11/AVHWFramesContext. Need graphics-thread ownership, adapter LUID, format/color space, sync/keyed mutex timeouts, bounded frames and timestamp contract. Verify that source target identity is exact; public hooked metadata alone is insufficient, so investigate supported targeting or a narrowly scoped upstream-compatible change. No broad capture-any-fullscreen or arbitrary hotkey retargeting. Never allow unauthorized frames into replay. Close/release all resources at stop; no OBS graphics context or plugins loaded in ordinary DDA mode if not needed. No second encoder, no OBS UI running. Compare selective module dependencies, packaging and idle/active overhead.

Alternative A: reuse only win-capture hook acquisition helpers. They rely on hook IPC, 32/64-bit helpers, graphics offsets, DXGI/OpenGL/Vulkan etc., cross-process events and synchronization, capture compatibility/signatures. Much more maintenance than simply extracting a DLL. Evaluate only if whole libobs footprint demonstrably unacceptable. No new custom hook or anti-cheat bypass; use existing approved hooks only where compatible.

Alternative B: official OBS Studio run manually by owner as benchmark reference, not required runtime or shipping architecture. Verify game-only behavior independently and collect matched frame pacing with OBS off afterward. Existing WGC window capture provides another non-injection option; Win10 capture border restrictions, minimized/non-rendering windows and exclusive-fullscreen limitations remain. DDA is current default monitor capture, never silently substitute if user chose isolated-only. CS2/Destiny 2 are known examples of hook restrictions; OBS certificates/allowlists do not confer trust on fork binaries. Verify exact licenses/dependencies/signature/redistribution obligations before distributing any helper. OBS README GPLv2-or-later is promising for GPL3, but file/dependency review and compliant corresponding source still required.

Prototype gates (not production implementation):
1. Minimal pinned libobs harness loads only required modules, reports dependency size/initialization/resource costs, no audio/video recording until owner selects generated or benign local fixture.
2. Capture one owner-selected supported game/window; verify exact identity and stop/revoke/switch, occlusion/alt-tab. No capture when minimized rendering stops; define bounded stall behavior.
3. Obtain hardware frames without CPU staging, prove adapter/sync ownership and safe lifetime. If impossible, document measured copies and decide if worth continuing.
4. Reuse QuickClip encoder and ring to mux a generated/nonprivate sample with existing hybrid audio lanes; compare geometry, cadence, sync, timestamp/epoch, output.
5. Matched Win10/Win11 reference benchmarks, CPU/WS/private bytes/VRAM, game frametime/1% lows, graphics/encode utilization and DLL footprint. Owner RTX3070 hardware results distinct from synthetic. Fallback/recovery and rights verified before experimental opt-in.

Exit decision: choose smallest maintainable path that passes target identity, performance, privacy, packaging and release gates. If no acceptable bridge exists, retain DDA and explicit WGC window option; companion mode can still proceed.

## Track 1 second: companion applications

Detection/activity policy can be researched now independently of acquisition. Distinguish verified application identity, specific window identity, user permission, activity signal, priority and backend capability. Use Windows window ownership and process identity; raw title alone insufficient, generic Update.exe not a companion. Start Discord stable/PTB/Canary and verified popout windows; Zoom and optional other applications later. Audio session activity is evidence of sound, not necessarily a call/meeting, silence not absence. Do not scrape private call contents, credentials, browser history or use unofficial client hooks for activity. Explicit manual selection/foreground mode if reliable call state unavailable. User opt-in per app and capture scope, game always first, normal alt-tab does not change game context; if no eligible game, choose permitted active companion or standby. Companion capture is a separate window source, not simultaneous game+Discord video unless explicitly adding composition later. WGC window acquisition for non-game windows; OBS game hooks are not for Discord/browser apps. Retain current source authorization policies and hybrid audio. Share a common source lifetime contract with game backend so future changes don't force a companion rewrite.

## Shared source contract, not a prescribed class hierarchy

Separate selection policy from acquisition: exact source identity + authorization generation -> backend capability and acquisition -> bounded GPU frame lease (adapter, dimensions, format/color space, monotonic timestamp, generation) -> optional GPU composition -> existing single video encoder and replay ring. Stop/revoke invalidates generation before save publication, release surfaces on source switch, no stale frame or hidden monitor substitution. Preserve default DDA path fast and no optional compositor if unused. Explicit single source initially. Only one optional PiP video/webcam, user-controlled positioning and size, no implicit mic/audio, no running camera when off. Multi-monitor optional future experiment, not required to finish Wave 3. No forced OBS rewrite, no companion capture before source policy safety proven.
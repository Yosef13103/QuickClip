# Recording performance and display diagnostics

QuickClip Personal keeps the original C++ capture engine, encoded replay ring and fast MP4 mux/save path. Settings → Recording → **Capture performance and diagnostics** shows the backend actually running, cropped source and encoded dimensions, encoder, preset, target bitrate/FPS and memory path. **Refresh diagnostics** updates it. Timing sampling is off by default; enabling it restarts the replay and adds a five-second sample to diagnostics and the local log.

For window capture, diagnostics label measured physical client geometry separately from configured requests and encoded output. Client geometry can differ from the exact WGC item extent when nonclient borders are included.

## Backend and scaling

Automatic monitor capture now prefers Desktop Duplication through 60 fps, including scaled resolutions. Cropping occurs in `ddagrab` before a D3D11 video-processor blit scales BGRA hardware frames. There is no CPU readback in the NVENC path. A pool of render-target textures remains alive through FFmpeg/NVENC frame references. Media Foundation retains its upstream CPU conversion fallback; automatic mode can fall back to WGC if GPU scaling or the chosen encoder cannot initialize. Explicit backend selections report startup errors instead of silently switching.

The bundled FFmpeg `scale_d3d11` failed texture creation on this host (HRESULT 80070057). Its output pool uses a ten-surface texture array and `D3D11_BIND_VIDEO_ENCODER`. A direct D3D11 texture-allocation probe isolated two constraints: a ten-surface NV12 render-target array failed with or without the encoder binding, while a single NV12 texture succeeded; BGRA render targets succeeded with one or ten surfaces but failed when the encoder binding was added. The local scaler uses ordinary, individually pooled BGRA render-target textures that NVENC accepts. This avoids changing FFmpeg dependencies or moving scaling to the CPU. See [FFmpeg's scaler implementation](https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vf_scale_d3d11.c).

Above 60 fps, Auto still prefers WGC. **Desktop Duplication** can be selected explicitly to avoid its monitor indicator, with actual FPS checked under load. Game-window capture always uses WGC. Windows 10 build 19045 has no supported border-removal API; [`IsBorderRequired` starts at build 20348](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired). No security/consent bypass is used. Monitor capture through Desktop Duplication avoids WGC's indicator by choosing a different capture API; visual confirmation on the user's desktop remains part of acceptance.

## Physical display dimensions

[`IDXGIOutput::GetDesc`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgioutput-getdesc) can return DPI-virtualized desktop coordinates. In the WPF process on this host it returned 2400×1350 for the secondary monitor while EnumDisplaySettings and QueryDisplayConfig both reported 1920×1080 source and signal modes. In the test host, DXGI instead underreported the primary as 2752×1152 versus its physical 3440×1440. This is a DPI-context problem, not evidence of a 2400×1350 physical monitor mode.

Native enumeration and crop geometry use EnumDisplaySettings physical pixels; native capture runs in a per-monitor-aware thread context. The gallery/settings monitor model also uses active QueryDisplayConfig source dimensions, with a physical native fallback. Diagnostics report the raw DXGI reading, physical desktop position, monitor scale/system DPI, active source size, signal size and refresh rate. Source and signal modes are kept separate: display-side scaling/virtual resolutions must not be inferred from a fixed 1.25 correction. [QueryDisplayConfig reference](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig).

## Encoder options and interpretation

Default NVENC settings remain **P4 / HQ**, VBR, configured target bitrate, 1.5× peak, 2× VBV buffer, no B frames, a one-second GOP and clock-forced IDR frames. Lookahead and multipass are not explicitly enabled; the bundled encoder/preset supplies their defaults. P3 and P2 keep the bitrate, tuning, GOP and frame rate settings but can reduce compression efficiency. They need moving-game quality comparisons before being preferred. These controls affect recording, not editor export presets.

Desktop Duplication repeats frames to maintain the requested cadence on static desktops. WGC can supply irregular frames; the engine maps them onto the target FPS timeline and skips repeated slots. One observed WGC sample supplied 144 frames/s but encoded about 60. Turning off duplication without redesigning tail duration/keyframe handling could break replay duration, so no static-frame suppression was added.

Captured FPS counts frames returned by the source; encoded FPS counts emitted video packets. Submitted FPS in JSON is the engine's trailing one-second submission count. Missing timeline slots measure timestamp gaps (the legacy dropped counter counts these only for Duplication); sparse WGC delivery does not necessarily indicate encoder overload. Capture wait includes source waiting. “Encoder path” includes GPU scaling submission, timing, encoder send/drain and replay packet insertion, not isolated NVENC GPU execution time. There are no raw frame buffers or per-frame log writes in the new diagnostics.

## Repeatable, bounded benchmark

Quit QuickClip and manually stop any other recorder first. The tool never changes Moments or its installation. Build once, then run a single case:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 -BuildOnly
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-video.ps1 -CaptureDesktop -Seconds 10 -Encoder hevc_nvenc -Preset p4 -Fps 60 -Height 0
```

`-CaptureDesktop` is explicit consent to a visible desktop capture session. It records video only into a five-second RAM ring; it exports no media, enumerates no clip library and uses its own scratch profile. Results go under ignored `.quickclip-local/benchmarks`. Duration is restricted to 5–30 seconds. The launcher bounds process lifetime and stops only its own diagnostic process on cancellation/timeout. Never use the diagnostic entry point to run unattended capture.

Compare `-Preset p4`, `p3`, `p2`, then `-Encoder h264_nvenc` using the same content and bitrate. Test `-Height 1072` for 2560×1072 ultrawide output, `-Height 1080` for the aspect-preserving 1080-line result, and native `-Fps 120 -Backend DesktopDuplication`. `-Backend GraphicsCapture` provides an explicit comparison and may show a border. `-Monitor '\\.\DISPLAY2'` chooses the other display. Do not assume a resolution preset name equals output dimensions; inspect the diagnostics.

JSON includes CPU as a percentage of the whole machine, working set, private bytes, replay bytes/seconds, frames/timing, and display modes. If `nvidia-smi` already exists, `gpu.json` adds total GPU, Video Encode, Video Decode and VRAM samples for each GPU. Those are device-wide counters and include any other process. They are not interchangeable. The first few samples include startup; discard warmup and exit samples. A missing/unsupported GPU counter is not zero usage.

For a controlled comparison, alternate cases in both orders, use at least three bounded repetitions, and keep the same game scene, camera motion, display refresh, encoder/codec, actual dimensions, FPS and target bitrate. Separately compare quality in fast foliage, smoke and text. Use Task Manager's **Video Encode**, **3D**, **Copy**, **Video Decode**, process CPU and memory graphs. Save only a deliberate non-private test scene if visual quality review is needed; the diagnostic tool itself does not save it. Do not infer gameplay results from a static desktop.

## Local evidence, 2026-11-11

Windows 10 build 19045, RTX 3070, driver 621.01; 3440×1440/144 Hz primary and 1920×1080/60 Hz secondary. Seven-second desktop samples, 20 Mbps VBR target, no audio, five-second replay, headless UI. CPU/memory averages below exclude the first two application samples; GPU averages exclude the first three tool samples and last sample (only a few steady readings). This is a short diagnostic observation, not a controlled gameplay benchmark or a before/after comparison with Moments.

| Codec / preset | Encoded size / target FPS | Video Encode average | Submitted FPS | CPU whole machine | Working set |
|---|---|---:|---:|---:|---:|
| HEVC P4 | 3440×1440 / 60 | 37.5% | 60.0 | 0.16% | 173.7 MiB |
| HEVC P3 | 3440×1440 / 60 | 16.8% | 59.4 | 0.10% | 176.4 MiB |
| HEVC P2 | 3440×1440 / 60 | 17.0% | 59.8 | 0.13% | 174.5 MiB |
| H.264 P4 | 3440×1440 / 60 | 34.0% | 60.0 | 0.05% | 182.8 MiB |
| H.264 P3 | 3440×1440 / 60 | 25.8% | 60.0 | 0.05% | 182.5 MiB |
| H.264 P2 | 3440×1440 / 60 | 18.0% | 59.8 | 0.03% | 184.8 MiB |
| HEVC P4 + GPU scaling | 1720×720 / 60 | 10.0% | 60.0 | 0.10% | 135.0 MiB |

These 60 fps samples reported zero missing slots in their first five-second timing window. Native 120 fps Duplication with HEVC P3 produced 113.8 encoded fps in that window with 28 missing slots, and 118 submitted fps in the last one-second sample. Borderless high-rate capture is available but sustained 120 fps under gameplay is not established. Scaled 2560×1072 Duplication delivered 59.8 encoded fps and no missing slots in its timing window. A scaled 1720×720 MP4 was saved and fully decoded successfully in an opt-in regression test. No original recording or permanent library file was touched.

3440×1440 has about 1.8× as many pixels as Moments' previously observed 2560×1072 output. The user's preliminary utilization figures therefore do not compare equivalent workloads. Preset choice has a substantial measured effect here, but useful quality equivalence, long-run GPU behavior and a matched Moments comparison remain unverified. Seven-second replay bytes are content-dependent and must not be extrapolated into a 45/90-second game-memory promise.

## Flicker / playback investigation

No global display, VRR, NVIDIA, audio or Windows settings are changed. No objective flicker reproduction or driver root cause is established. Candidate interactions include DDA acquisition/release, WGC composition, GPU blits, encoder scheduling, VRR and decode/encode contention. Native monitor capture previously changed API solely for scaling/high FPS; that is now explicit and diagnosable. Flat-picture recovery and watchdog restarts remain upstream behavior; correlate their log events with any visible flicker before changing them.

For each experiment use the same harmless text document, typing sequence, local non-private 60 fps video, monitor/refresh and app layout. Compare recorder off, DDA native P4, DDA native P3, DDA scaled P3, WGC, then Moments manually. Run only one recorder. Observe both displays and video cadence; note time and whether a capture restart appears in the local log. Repeat twice with the order reversed. Record 3D/Copy/Video Encode/Video Decode graphs separately. Do not toggle global VRR/HDR/driver settings to make a result disappear. Report observable darkening/stutter separately from counter differences.

## Acceptance still requiring supervised use

- Confirm no yellow monitor indicator with scaled 60 fps Duplication and explicitly chosen high-rate Duplication. WGC/game-window indicator remains a Windows 10 limitation.
- Check both monitors' labels, full-frame edges, centered 16:9 crop, ultrawide aspect, black levels and colors. Check display/resolution changes safely rebuild and a save during transition remains decodable.
- At native 3440×1440/60, compare P4/P3 moving-game quality with a 90-second replay and three selected audio tracks. Watch CPU, working set, VRAM, encoder and actual replay bytes. This longer, supervised acceptance case was not run automatically.
- Verify game → Discord → game priority and minimized/exclusive-fullscreen limitations; check separated Game/Chat/Mic sources with each producing sound alone, then together. Hotplug, drift, Sonar duplication and preview leakage remain the previous milestone's manual checks. Endpoint loopback can include QuickClip previews played on the selected output.
- Gallery: Delete → No leaves media intact; one bulk confirmation; player Delete returns to gallery without opening another clip; holding Delete cannot delete the next file. Undo restores originals, refuses path collisions, and preserves project behavior. Settings can disable confirmation; shell permanent deletion is still vetoed. Recycle Bin recovery is also available after restart.

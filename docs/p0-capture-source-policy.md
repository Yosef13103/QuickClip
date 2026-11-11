# P0 capture source policy and Windows support

## Source paths and decisions

Before this change, Recorder started the monitor graph before polling for a game. A valid foreground game HWND then went through `SetCaptureWindow` to FFmpeg `gfxcapture`. A closed HWND or failed window graph could route back to the monitor. Monitor Automatic chose WGC first above 60 fps and could fall back to WGC at other frame rates. The output resolution control selected dimensions independently of these decisions.

| Personal selection | Capture decision | Scope |
| --- | --- | --- |
| Existing Game First setting | Blocked. No verified borderless game-window provider is available | No video or audio start |
| Selected monitor with Automatic or Desktop Duplication | DDA only. Fail if the selected display or DDA graph is unavailable | Entire visible display within the configured crop |
| Selected monitor with explicit WGC | WGC only. Runtime status warns that the capture border may appear | Selected display, including visible apps |
| Future Automatic with separate monitor fallback consent | Policy resolves to DDA only | Agent 2 must wire consent and display selection |

Personal passes a fixed monitor-only choice into the native graph. Native rejects HWND switches for both fixed choices. No focus change, invalid game HWND, game exit or Discord pop-out can switch these sessions to another target. Graph rebuilds use the same Windows display identifier and backend. A missing named display fails instead of choosing the primary display. An empty display setting means the primary/default display at start. Windows display names can be reassigned during topology changes. Stable physical-display identity across such changes still needs validation.

DDA keeps the existing GPU crop and scaling path. Native 3440×1440 and centered 16:9 use the existing geometry. HEVC/NVENC P2, encoder fallback, AAC lane ordering and replay muxing keep their existing configuration. The Upstream build retains legacy source selection and native ABI entry points. Legacy HWND sources do not supply verified replay identity through this contract.

## Bundled FFmpeg and Windows 11 follow-up

- The dependency pin and installed `ffmpeg -version` identify `n9.0.2-48-gd4070a14ce-20261110`. Running `ffmpeg -hide_banner -h filter=gfxcapture` reported `display_border` default false. The bundled avfilter DLL contains both FFmpeg border warning strings. In the FFmpeg source the filter queries `IGraphicsCaptureSession3` and calls `put_IsBorderRequired(display_border)` if the interface is available. QuickClip does not override that option. [Filter option](https://ffmpeg.org/doxygen/trunk/vsrc__gfxcapture_8c_source.html) · [WinRT implementation](https://www.ffmpeg.org/doxygen/trunk/vsrc__gfxcapture__winrt_8cpp_source.html)
- Expected warnings are `Disabling border drawing unavailable` when the interface query fails and `Failed setting border drawing mode` when the setter returns failure. QuickClip forwards FFmpeg warning/error levels to its capture log. These strings and the logging path were verified without opening a capture session. No live log was collected. A missing warning alone cannot prove the border was suppressed.
- Windows 10 build 19045 predates build 20348's supported border toggle. For a later Windows 11 release, prototype a separately packaged Personal app with `graphicsCaptureWithoutBorder` in the application's package manifest. Request `GraphicsCaptureAccessKind.Borderless` in a visible, user-initiated flow before opening the FFmpeg graph. Prove consent grant and denial, restart behavior and revocation, and treat another app requiring the border as a possible override. FFmpeg already invokes the setter. Its default does not request consent. Current portable and development builds have no verified package identity or borderless permission flow, and the Explorer extension package is not evidence that the recorder has that capability. [Microsoft permission and capability requirements](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)

## Borderless game-window alternatives

| Candidate | Decision and tradeoff |
| --- | --- |
| Windows 11 WGC with the required capability and granted consent | Go for a later isolated packaging and hardware spike. It reuses GPU surfaces and the existing filter. Platform availability, user consent, competing capture sessions and minimized rendering must be checked. |
| DDA, including a crop to the game's bounds | Go only for explicitly authorized monitor capture. A crop can contain overlapping windows and unrelated content. Compare GPU copy/scale time and game frametimes in the existing diagnostics. |
| GDI/window painting | No-go for general GPU games. Application cooperation and compositor behavior vary, and copying system-memory frames into the encoder adds CPU/transfer work. A black or frozen frame is a plausible failure. |
| Established OBS-style Game Capture | Research only. It relies on game graphics hooks and usually shared GPU textures. Compatibility varies by DirectX version, graphics driver, anti-cheat and other overlays. A future proposal must establish a supported, explicitly authorized distribution path, check GPL redistribution and dependency obligations, and verify game/vendor compatibility before experiments. OBS reports shared-texture capture is cheaper than its memory fallback. No QuickClip cost or compatibility was measured. [OBS Game Capture](https://obsproject.com/kb/game-capture-source) · [OBS compatibility](https://obsproject.com/kb/game-capture-troubleshooting) |
| Direct NVENC or D3D11 shared textures | No-go as an acquisition backend. NVENC needs an input surface, and resource sharing requires a producer that supplies the surface. |

**QC-004 remains open.** There is no verified borderless game-only backend for build 19045. QC-002 game identity and QC-007 overhead remain separate acceptance work.

## Agent 2 integration patch

Use a distinct source selector for strict game-only, selected monitor and automatic. Until the cross-branch adapter is wired, describe the existing Game First choice as unavailable and point to Selected monitor. Describe Automatic monitor backend as DDA-only in Personal. Preserve the explicit WGC warning. Map the existing game policy model to `CaptureSourcePolicy.Resolve` and pass `monitorFallbackAuthorized` only for a separately accepted monitor fallback with a known display. Carry stable catalog identity alongside a validated HWND for any future game provider. A GameMonitor hint, executable match or current foreground window alone is not consent. Keep Discord focus as an observation without target authority.

The receipt keeps its existing constructor and adds optional `DesktopSource` (Windows display identifier and backend), `VideoOutput` (finalized MP4 dimensions and average FPS when available), and `SpansCaptureEpochs`. Duration still comes from the finalized MP4 video track. A graph restart can change the epoch without changing the fixed monitor source. With unknown legacy provenance and multiple epochs, show multiple or unverified sources and omit a definitive source label. Personal fixed-monitor clips are published as Desktop. Existing clips are untouched.

## Reproduction and supervised acceptance

Run `dotnet test QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true` and keep live capture opt-ins unset. `CaptureSourcePolicyTests` exercises denied and authorized decisions, pinned display selection and epoch reporting. Existing replay and window-identity tests cover synthetic packet transitions and HWND/focus selection. These checks do not establish actual GPU capture behavior.

Validation of implementation commit `6ddcbf265598480c557842cabd9d432ffdfde15d` on Windows 10 build 19045 compiled the native engine and Personal Release with zero warnings or errors. The final full suite passed 376 tests with 18 opt-in skips. One Storage Dashboard WPF timeout on the first run passed both the focused rerun and the final full run. The isolated runtime check loaded native capture and libmpv and enumerated eight active audio endpoints without opening them for recording.

With the user present on build 19045, start a short disposable session in each of Terraria, tModLoader and Marvel Rivals. Compare windowed, borderless fullscreen and exclusive fullscreen where supported. Confirm that Game First starts no capture and shows its blocked status. Explicitly select DDA and the test display. Record a short non-private scene, switch focus to a disposable Discord pop-out on the other display, return to the game, and exit it. Confirm the selected display stays fixed and no WGC indicator appears. On the selected display, covering windows should appear in the clip. Check native 3440×1440 and centered 16:9 separately. Verify receipt display/backend and inspect the saved MP4 video dimensions, average FPS, duration and Game/Chat/Mic track ordering with FFprobe. Repeat across a compatible rebuild and a resolution change, and check exclusive minimized rendering separately. Compare repeatable recorder-off and DDA trials using the existing frame-pacing handoff. For an explicitly approved WGC comparison, collect the capture log and observe the indicator in person. No live capture was performed for this branch.

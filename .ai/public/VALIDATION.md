# Personal v4 acceptance checks

Use a temporary Personal data root, a clips folder containing generated fixtures, and recording off for ordinary UI checks.

| Area | Check |
| --- | --- |
| Gallery | Compare card size and JPEG clarity at 1920×1080 and 3440×1440 at 100%, 125% and 150%. Hover for half a second, move between cards, scroll a hovered card away and switch pages. Only the current card should play, muted, with static fallback for unsupported formats. Click through to the same clip. |
| Editor | Drag both dividers, resize the window, and play a clip with a different aspect ratio. Press Space before, within and after the trim range. Enable Loop and verify it repeats once playback enters the selection. Tab to shortcut help. Open a second clip and confirm Clip details starts expanded. |
| Fullscreen | On each monitor, enter fullscreen with F11. Move the mouse, seek, adjust volume, let the controls time out, and press Escape. Verify aspect ratio, current clip, playhead, track choices and prior layout. Repeat after moving between monitors with different scale factors. |
| Modals | Open a rename prompt, a delete confirmation and an export warning on generated fixtures. Confirm background clicks are blocked, Tab cycles inside, Escape cancels, and the destructive action defaults to Cancel or No. Test Recycle Bin and Undo separately on a disposable profile. |
| Settings | Open Advanced monitor backend and Advanced Recording Diagnostics near the column breakpoint. The recording header should stay anchored and the other cards should stay in their columns. On a 3440×1440 source, check native, 16:9 crop, 1080p output and unavailable upscaled presets. |
| Overlay | Try all nine positions on a primary display and a display with negative coordinates. Change size, opacity, animation and duration, then restart. Confirm click-through and focus preservation. Exercise channel combinations and failure feedback separately. |
| Supervised capture | Use a synthetic window or nonprivate game and known audio signals. Check short and overlong replay requests, marker inclusion and eviction, three independent tracks, alt-tab continuity, save latency, and overlay inclusion in both WGC and Desktop Duplication. Try borderless and exclusive fullscreen separately. |

Repeat portable tests with an isolated profile. Use a disposable Windows environment for installer, startup registration and Explorer registration acceptance.

# QuickClip Personal v4 integration

## Git provenance

The baseline was `49515fce4bb52ec62d9ab89e2e70752b2a980aef` on `origin/custom/main`. The integration branch is `integration/queued-v4-ui-notifications`. The release target is `custom/main`. The fork and original tracking branches were both at `e9bccdc6ee38f1b7c29b7ee7ea8b260babcc2a3f`.

| Merge order | Source branch | Published source tip |
| --- | --- | --- |
| 1 | `feat/sqlite-library-index` | `1d2f49cae140e9ad8504a57143a64e4996211876` |
| 2 | `feat/unified-editor-ux` | `1ad98424a61ab2fb9021add3574cb4ea41515890` |
| 3 | `feat/clip-annotations-ux` | `542cfba0f28e78deff6e276710e361b362c5f2f3` |
| 4 | `feat/replay-markers-hotkeys` | `2e49032c3fd2f2d09abe0c9de174e1921e24cf53` |

Annotations were initially committed in their owning worktree as `5424d4e`. The final follow-ups on that branch merged after the library host wiring. The crop work appears once because the annotation branch descends from it. The replay branch's frame-pacing base was already in `custom/main`. Git merged the shared files automatically. The combined test compilation exposed an ambiguous `ClipBookmark` name, fixed by qualifying the library type in the affected test.

## Application behavior

| Area | Implemented behavior and remaining scope |
| --- | --- |
| Library and annotations | Enable indexing for the configured clips folder in Settings. The Gallery and Editor read and write the same SQLite metadata for indexed media. Existing JSON remains in place as a stat-verified import source. Tags, favorite status, capture time and bookmarks are imported. Draft updates are transactional and revision-checked. Existing media, project references and the Moments archive stay outside migration writes. Files opened outside the indexed root keep the JSON path. Turning indexing off switches back to the legacy view. Database edits are retained in SQLite when disabled. |
| Storage | The dashboard reports indexed media, missing and unavailable entries, available space, and logical bytes. The overall verified byte count deduplicates known file identities. Per-game and category rows show logical bytes. Project references do not add physical usage. Project/cache size, retention, managed-original finalization and physical allocation measurements are outside this change. |
| Recording dimensions | The crop schematic uses source dimensions without opening another capture pipeline. It distinguishes source, cropped area and output. The existing recorder consumes the custom aspect and short-side value. Exact custom pairs are accepted when the current recorder can produce them. Game-window capture retains the window's aspect. Arbitrary output pairs across changing source shapes, letterboxing and upscaling remain outside this implementation. |
| Replay durations and markers | The Personal tray offers 15, 30, 60 and 120 second requests and Add replay marker. They share the original ring and no-reencode save path. The existing global save hotkey keeps its previous behavior. Extra global bindings are outside this change. Receipts preserve actual duration and retained marker offsets. Markers seed clip metadata after publication and become part of the index when reconciled. |
| Gallery | Larger responsive cards use DPI-sized JPEG cache buckets. Hover waits 500 ms, then uses one muted WPF media preview while the gallery owns focus. Leaving, recycling, deactivation and media failure release playback. Playback depends on Windows media decoding. The static poster remains available. The card Share action uses the existing share pipeline. Delete remains in the compact menu with confirmation and Undo. |
| Editor | Ordinary playback follows the current playhead. Explicit Loop arms inside the trim range. The keyboard button opens an in-app shortcut list. Clip details start expanded. Dividers resize the inspector and timeline. F11 fullscreen reuses the mpv session, exposes timed playback controls, and restores the previous view with Escape. Live mixed-DPI behavior needs manual verification. |
| Dialogs | In-window prompts handle text, confirmations and application warnings. Controls retain focus inside the modal. Video child HWNDs are temporarily hidden because they render above WPF content. Native file and folder pickers and early startup errors use Windows surfaces. |
| Recording Settings | The capture backend wording explains visible results and monitor-only scope. `Automatic - Game First` is the source label. Settings cards retain their columns while expanders change height. |
| Clip feedback | Sound, Windows notification and the on-screen indicator are independent. The indicator is opt-in, with nine anchors, display, size, opacity, duration, fade or ripple and a test button. Success follows the published save receipt and reports actual duration when available. Failure uses a separate glyph and never plays the saved-clip sound. The reusable surface stays hidden between events. Exclusive fullscreen may hide a normal topmost window. The overlay requests supported capture exclusion, which still needs WGC and Desktop Duplication smoke testing. [Microsoft display affinity documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity) |

## Validation and release readiness

- The initial merge checkpoint passed 338 tests with 18 opt-in skips after the test namespace fix.
- Final Personal Release suite passed 365 tests with 18 opt-in skips and no failures. Skips cover live capture, Windows Recycle Bin, and reparse-point fixtures that require explicit opt-in.
- The strict native and managed builds passed, along with 25 focused stock-profile tests for defaults, identity and menu packaging. The framework-dependent Windows x64 portable output includes SQLite, the capture DLL, libmpv, FFmpeg and FFprobe. The runtime probe loaded the bundled components and enumerated eight audio endpoints. Portable tray startup and clean IPC shutdown passed with recording disabled in a separate scratch profile.
- The offscreen WPF run indexed two generated clips, verified gallery search, preserved clip, playhead and trim across editor modes, exercised custom resolution input, and exported a 1280×720 montage with three named audio tracks. WPF tests exercised modal confirm/cancel and hover-request cancellation. Thumbnail cache keys, desktop card breakpoints, the loop decision, notification settings, success/failure routing and all nine overlay positions have automated coverage.
- Final implementation code is at `a2f504f387522f3274fb58d43067636bd0383cf9`. The handoff commit on the integration branch includes the tested code and the public documentation.
- Personal development builds keep separate settings, identity and IPC. The production installer remains blocked on a Personal installer identity, owned Run value and Explorer registrations, signing/packaging, update provenance, upgrade and uninstall testing. No registration or installer was run on the development machine.
- The [acceptance matrix](../.ai/public/VALIDATION.md) covers 1080p, ultrawide, mixed DPI, navigation, hover, fullscreen, modal behavior, overlay placement and supervised recording. Live gameplay frame pacing, exact replay latency, real endpoint isolation, exclusive fullscreen and desktop capture exclusion need hands-on verification.

# FT-09b Hotkeys, FT-10 duration display and FT-11 Audio

Feature base `9d81d71b854c881a3efddf0583e2b8b3a6531026` on `integration/nov12-field-test`. This includes the native endpoint diagnostics, command dispatcher, sectioned Settings and Games view.

## Audio

Choose playback devices under **Playback (what you hear)** and input devices under **Microphones (input)**. Each configured row keeps its endpoint ID and flow. A Windows default is labeled for orientation, and adding it stores its concrete ID. Missing selections remain visible and are kept for a reconnect. **Refresh devices** enumerates the current endpoints. The shared ordered list controls saved track order across both flows.

**Running audio lanes** reads one snapshot of the already-running recorder. Each native status is paired with the same session’s immutable endpoint plan and matched by lane ordinal and flow. The UI shows stage, failure HRESULT, mix format, packet count, packet age and the most recent peak when available. It does not open an input or show any raw endpoint IDs. A missing peak can mean diagnostics are off or no packet has been sampled.

## Hotkeys and duration

The Hotkeys section exposes Save Clip, recording master, manual desktop and switch to next authorized display. Each command has a binding, Clear, Reset and registration status. Only fresh profiles default Save Clip to Alt+F10. Reset explicitly restores that default, and Clear is persistent. A rejected shortcut retains the previous assignment. The existing dispatcher and manual display consent govern both shortcut and tray actions.

The editor can seek to previously saved bookmarks and displays their labels. Title, game, tags and favorite edits keep bookmark data, and exports retain the existing trim/rebase behavior. New bookmark actions are removed from the editor. The tray already has one Save Clip action and has no duration or bookmark actions.

Clip cards, short recorder status and save notifications use whole-second `SS` below a minute and `MM:SS` thereafter. Editor trimming keeps its existing precision. This change does not alter replay selection, timing receipts or mux output. PR #21 investigates measured duration separately.

## Verification

```powershell
dotnet test QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true
dotnet build src/QuickClip -c Release -p:QuickClipProfile=Upstream -p:RequireCaptureEngine=true
```

Leave capture, Recycle Bin and symlink opt-ins unset. The WPF fixture checks both picker roles, equal device labels across flows, a missing persisted input, accessible names and keyboard-focusable hotkey controls at 1050×660 logical pixels. The Debug build accepts `--render=<scratch folder> --recording-settings-smoke-only` with an isolated `QUICKCLIP_PERSONAL_DATA_DIR`. It renders both Settings pages with fixture devices and checks that recording is inactive.

On Windows 10 build 19045.7807, strict Personal and Upstream Release builds passed with zero warnings and errors. The Personal Release suite passed 511 tests with 18 opt-in skips. The focused Debug suite passed all 40 tests. The offscreen render confirmed recording was inactive, and the Personal runtime preflight loaded native capture and libmpv and enumerated Windows endpoints.

Supervised checks still cover Narrator announcements, keyboard-only navigation at 125% and 150% DPI, real Windows shortcut conflicts and consent during background operation. A separate hardware test should isolate Game, Chat and Mic signals and compare Sonar eCapture with the physical input, including reconnect. The picker and snapshots provide evidence for that test. The cause of the silent Sonar input is unresolved.

To roll back, revert this feature commit. Audio selections, hotkeys and bookmark data use the same existing settings formats. Keep the profile when reverting to preserve these values.

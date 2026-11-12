# FT-09b recording command routing

Stacked on the FT-09 lifecycle in PR #13 at `840fdbfbfea1e0c063e930dd2a2bb854dea1cd56`. The app's existing hidden window owns the four command IDs. Tray commands and registered shortcuts share the same dispatcher. The tray exposes one Save Clip action. The duration and live-marker runtime APIs and saved bookmark data remain readable.

A fresh profile defaults Save Clip to Alt+F10. An existing Personal settings document without a saved shortcut keeps its earlier unbound behavior. An explicit saved shortcut, including a zero key, round-trips unchanged. Toggle master, toggle desktop and switch monitor default to unbound. RegisterHotKey failures and duplicates within QuickClip have separate statuses. Editing the existing Save Clip shortcut preserves the previous binding if the new combination is rejected.

Starting desktop capture from either the tray or a shortcut requires an explicit confirmation naming the display. During a manual session, **Authorize and switch display…** separately approves another connected display. **Switch to next authorized display** and its shortcut consider only connected display identities already authorized in that manual session. The game-triggered display is governed by its separate single-display grant. Manual grants expire when the master switch is turned off, manual capture is stopped or the app quits. Switching starts a new replay buffer.

## Chat 2 settings and Editor handoff

Build the dedicated Hotkeys page around `RecordingCommand`, `RecordingHotkeys.Read`, and `AppHost.SetHotkey` / `HotkeyState`. Assign the default Alt+F10 only to Save Clip, and clear the other commands with `default(Hotkey)`. Report Duplicate separately from Windows Unavailable. Use `SuspendHotkey` while listening for a key, then `RegisterHotkey` when the editor loses focus or cancels. Existing Save Clip settings use this same validator.

Remove the Editor Bookmark action and add-bookmark UI in the Editor-owned worktree. Leave read/write metadata and project migration intact, including existing saved markers. This PR changes the runtime, tray and existing Save Clip shortcut editor. It does not implement the dedicated WPF page or change the Editor.

## Validation and rollback

Run `dotnet test QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true --filter "FullyQualifiedName~RecordingHotkeysTests|FullyQualifiedName~RecordingLifecycleTests|FullyQualifiedName~ProfileTests"` on Windows. On build 19045, all 27 focused tests passed, and the scratch runtime loaded native capture and libmpv. No physical shortcut registration, desktop capture or consent dialog was exercised during these tests.

Test real key collisions and background/tray behavior in a scratch profile with the owner present. Test a previously configured profile and intentional unbind, a new profile, and display switching with unauthorized and unplugged screens. Keep FT-09b hardware acceptance open. Reverting this PR leaves the original Save Clip binding fields in place. The three additive bindings are ignored by earlier builds.

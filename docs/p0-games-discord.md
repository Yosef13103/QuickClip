# P0 Games and Discord handoff

Base `5348c6ad2dc0f51391ba26a9299a1c3461f21e21` from the fork's `custom/main`.

## Baseline audit

| Already active | Backend-only on the base | Capture integration still required |
| --- | --- | --- |
| Game Bar and path/fullscreen detection, process/window ownership checks, retained capture HWND across ordinary focus changes, and global game-first or explicit monitor selection | Approved-root Steam/folder discovery, manual entry IDs, exact-path index, rule overrides, bounded scan helpers, and sparse game profile validation and storage | Consume typed observations before selecting a target or fallback. Enforce skip, apply profiles at a stable capture transition, and label saves from the actual capture epoch. |
| Stable-ID Windows endpoint picker and multi-track plan. Configured endpoint rows display the actual available device or an unavailable state. | Legacy process-tree routing planner and tests exist. `AudioRouting.SeparateTracks` is false, so legacy Desktop audio can include Discord. | Verify endpoint disconnection/reconnection and independently signaled recorded tracks. Endpoint capture sees the selected mix, including any duplicated monitor/virtual routes. |

## Games settings and identity

- Settings → Games. Approve a Steam library containing `steamapps` or a single game directory. Scan Now uses the existing scan budget and cancellation. Optional automatic scans run at most every 15 minutes while Games is visible. A disabled toggle or navigation away cancels the current scan. Settings and the last snapshot are profile-local. Removing approval immediately removes that scope from the index.
- Add Game associates up to 32 explicitly selected EXEs. Editing or renaming a manual entry preserves its ID. Catalog paths outrank fallback heuristics. Disabled, ambiguous, launcher, inactive and explicitly non-game paths are barred from game promotion. Candidate paths require Game Bar recognition or fullscreen evidence. Discord variants, helper processes and updater paths stay non-game even if fullscreen or accidentally registered.
- The detector exposes `ForegroundObservation` and `ActiveObservation`. The latter retains verified identity across companion focus and clears invalid owners. Each observation has HWND, thread, PID, process creation time, executable, match kind, catalog ID/display name via `Match.Entry`, and target. Catalog confidence and rule source preference are on the entry. `ResolvePolicy(document, globalDefaults)` merges a saved profile when the integration host supplies the document. No profile dropdown is exposed before the recorder can honor it.

## Recorder integration contract

1. In the recorder's serialized poll, call `Poll` once. Inspect both observations. Use `ForegroundObservation.SkipAutomaticCapture` before automatic fallback; retain a still-valid permitted game across a companion switch. When the active game is disabled, excluded or loses ownership, clear its target and stop or suspend automatic capture before a fallback can expose its monitor. Keep global explicit monitor mode independent.
2. Evaluate `ActiveObservation.Target`, the entry's activation/source rules and the resolved profile. Validate source capability with Agent 1's planner. Fail closed if a strict target is unavailable. Windows 10 Automatic must never select a WGC source that shows the system indicator. Publish the actual target/source epoch for clip attribution.
3. Until this integration, legacy `ForegroundGame` keeps the former HWND eligibility rules. Newly catalog-only identities can inform attribution and the adapter, but do not newly enable WGC. Existing legacy WGC behavior and the monitor fallback are still present. A saved capture toggle alone does not stop the legacy recorder from showing a disabled game.

## Supervised acceptance still required

1. On Windows 10 19045, approve fixture roots then real Steam roots explicitly. Launch Terraria, tModLoader and Marvel Rivals, verify friendly name, executable ownership, restarts and restored settings. Check each with capture disabled in Automatic, then with explicit monitor capture.
2. Alt-tab repeatedly to Discord on the second monitor, including call, video and stream pop-outs. Check active identity and actual saved video through focus changes, launcher exit, game exit, and renderer HWND replacement. Inspect for any yellow border. This sprint adds no second video source.
3. Route distinguishable tones to Game and Chat and use a separate consenting microphone signal. Test a Discord call and shared-stream audio individually. With Sonar, check both virtual endpoints and the physical playback endpoint for a mixed duplicate. Inspect each track in the editor, probe stream names and duration, and verify silent inputs and disconnect/reconnect. No audio endpoint routing is changed by Games settings.

## Verification

On Windows, the Personal Release build succeeded with zero warnings or errors. The runtime check loaded the native capture engine and libmpv. The targeted suite passed 124 tests with two opt-in skips (junction traversal and live game-window capture). The new tests covered 12 catalog policy cases, 30 synthetic focus/ownership transitions, 10 non-game negatives, persisted exclusion, offline scan reconciliation, endpoint deduplication and WPF layout at 1050 × 660 layout units. Existing tests covered cancellation, malformed manifests and legacy process-tree audio planning.

```powershell
dotnet test QuickClip.slnx -p:RequireCaptureEngine=true --filter 'FullyQualifiedName~Game|FullyQualifiedName~Steam|FullyQualifiedName~EndpointAudio|FullyQualifiedName~AudioRouting'
```

QC-003 implementation evidence covers I03 and I04. I05, I06 and the acceptance gates remain pending. QC-004 awaits the recorder changes above. QC-005 gets synthetic endpoint-plan evidence only. Loss/reconnect, measured sync and independent signals in actual saved output remain pending.

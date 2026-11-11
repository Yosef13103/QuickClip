# FT-09 armed recording lifecycle

This change targets PR #7's integration branch at `0c7719410a045a7a28c7a37d1d8f14d6cc045ef7`. It covers the implementation portion of FT-09 and QC-003-I05/I06, with partial QC-002 and QC-004 evidence. Hardware acceptance remains open.

| Mode | Graph behavior |
| --- | --- |
| Off | Stop the active graph, clear the RAM replay and forget any manual request. |
| Armed | Observe game eligibility without starting video, audio, or encoder graphs. |
| Game-triggered | Start a single Desktop Duplication session for a currently owned, catalog-eligible HWND after separate display consent. Preserve it across Discord focus and minimization while the observed HWND remains valid. |
| Manual desktop | Start from an explicit tray confirmation for this run. The selected display takes priority over game mode until manually stopped or the master switch is turned off. |
| Unavailable | Missing consent or display leaves no graph. A source failure stops capture and does not retry the same failed request until the target changes or the user reapplies settings. |

The master switch and saved Game First intent carry over. Neither an old monitor preference nor an old WGC preference grants game-triggered recording permission. Consent is stored as a Windows monitor interface path. Native startup and rebuild require the same physical target for the selected display source. Ambiguous mirrored sources and unidentified displays fail closed. The recorder keeps the old GDI alias for a live session and stops if the alias changes. A subsequent reconciliation can use the new alias for the same authorized display. Manual activation is session-only. Switching between game and manual modes starts a new replay buffer.

The receipt's game context and catalog ID come from the graph's activating game and are captured under the same gate as the native replay session. `DesktopSource` still describes entire-display pixels even if the game supplies the filename classification. The manual session has no game context. Game exit or game replacement clears the old in-memory replay. Published recordings remain usable.

## Chat 3 producer handoff

Recorder consumes the baseline `GameCaptureObservation` and `GameWindowTarget`, requires a recognized target, a matching `WindowOwner` (HWND, thread, PID and process start), a stable catalog entry ID, and a current catalog Game/Candidate match. Disabled, unknown, launcher, excluded, offline and companion observations cannot authorize a graph. The adapter currently remembers previously observed eligible windows and removes them as ownership or eligibility expires. It cannot see a game that never becomes observable through the baseline detector. Expose a bounded list of validated current observations, including eligible background windows, from the discovery producer when available. The consumer should continue to revalidate each candidate, keep its active game while valid, and choose another eligible game only when needed. Per-game resolution/audio overrides and HWND-only capture are separate work. No Win11 package consent is assumed.

Owned paths are Recorder, CaptureSourcePolicy, RecordingLifecycle, Monitors, CaptureEngine, ReplayMarkers, AppSettings, the native start API and video graph, and the minimum tray/recording status and Settings controls. No discovery, Games, Audio WPF, Gallery or Editor code changed. FT-09b shortcuts and FT-10 mux timing remain separate.

## Verification and rollback

Run `dotnet test QuickClip.slnx -c Release -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true` with capture and Recycle Bin opt-ins unset. `RecordingLifecycleTests` uses a fake single-graph backend to verify off/idle, consent, sticky focus, game exit and replacement, manual override, monitor loss and start/runtime failures. Use a scratch profile for `--check-personal-runtime`. No capture is needed for these checks.

For supervised validation on build 19045, confirm ten idle minutes with the master switch on and no approved games produce no graph or replay bytes. Run Terraria, tModLoader and Marvel Rivals in short nonprivate sessions. Confirm a disabled game cannot trigger capture, Discord focus on the secondary display does not retarget the game, the final game exit stops recording, manual capture works independently, and unplug/reconnect never captures a different display. Check the saved receipt and MP4 source descriptions. Note any transient topology changes, anti-cheat restrictions or exclusive-fullscreen issues. Physical display and hardware capture remain unverified by synthetic tests.

Rollback is a revert of this feature PR before release or a return to the reviewed integration baseline in a scratch profile. The saved authorization field is additive. Clearing it or turning off master recording disables this PR's automatic display capture.

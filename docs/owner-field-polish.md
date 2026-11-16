# Independent owner field-polish candidate

Based on 8bc5a73591cdda5549dd1a53fcbc094a0c49fad5. Separate branch deliberately preserves unfinished local Codex work. Basic includes Sharing and all shortcuts, plain-language shortcut copy, right-aligned setup actions without Pause, smaller gallery subtitle, compact expandable discovery details to reclaim list space, deterministic x64 .NET 10 SDK selection. A known Minecraft launch stub is vetoed even in a cached catalog; a GDK manifest can supply launcher evidence instead of renderer evidence.

## Microphone playback

Explicit Record 5 seconds and Play recording beside the existing signal test, available in both audio routing modes. New optional native sink uses the identical WASAPI source, with bounded PCM16 stereo storage at 48 kHz, no persistent media file, no new dependencies. Existing telemetry-only probe is unchanged. Sample duration is limited from streaming start; show actual recorded duration if fewer frames arrive (no fabricated silence for missing packets). Explicit playback uses the existing WASAPI output helper with the default multimedia render endpoint; cancellation on page/mic changes and running recorder. Native and managed scratch buffers are discarded on cancellation/completion, and the temporary WAV stays in memory only. Physical audio acceptance still needed.

## HUD

Preserves startup mic latch. Original replay arrows and mic on compact 28 DIP dark tiles. Try the existing translucent layered window, then a compact non-layered opaque fallback if capture exclusion fails. Both must pass the Windows display-affinity check; no unsafe show-if-exclusion-fails fallback. Win32 error status and idle preview are available. Neither success nor preview proves actual DDA exclusion or exclusive-fullscreen visibility. Verify on real hardware; opaque fallback is a deliberate tradeoff rather than guaranteed transparency.

## Theme

Apply and refresh selection inside a reentrancy guard (Fluent ThemeMode can invalidate control templates), refresh choices when opening the dropdown, preserve the normalized saved library when switching themes, and clarify draft-only invalid contrast. Root cause of owner's disappearing theme not reproduced; this is defensive hardening, with serialization tests, not confirmed physical fix.

## Minecraft

Minecraft Java is independently confirmed by the owner's foreground process, a bundled javaw.exe under Minecraft Launcher/runtime, with no matching current-user AppX registration. Automatic Java title identification is not implemented here. Do not approve arbitrary Java runtimes or infer gameplay from a title string. A future adapter should validate trusted launcher runtime and actual Minecraft-specific process evidence without retaining raw command lines (they may contain session credentials), then associate with the existing title identity. Otherwise offer owner-selected exact process association with the shared-runtime limitation stated.

## Integration

Per-display permission redesign, in-flight setup cancellation and display-revocation publication races remain in the already-started local frontend/reliability lane. No updater/diagnostics implementation replaced. Reconcile with their current unpublished changes (especially Api.cpp, Audio.cpp and shared settings). Windows CI and physical audio, HUD exclusion, theme lifecycle and UI testing required on exact combined head. No release approval.

## Combined review on PR #78

- One physical-display permission applies to automatic and manual capture. The saved exact legacy grant migrates when it was active. The old global grant did not identify any displays, so reselect those permissions. Manual activation remains session-scoped and master off invalidates it.
- A display permission change and replay publication share the recorder lock. Both snapshot and publication check the active physical grant and master state. Game-start notifications and the HUD use the current video-ready graph. The HUD refreshes placement when work area and DPI change.
- The actual WPF preview reproduced layered affinity failure with Win32 error 8. The compact non-layered fallback accepted the exclusion setting and appeared. Inspect a supervised replay before relying on capture exclusion.
- An STA test imported two real theme files into a disposable profile, removed the source files and exercised the real selector. Switching to Light briefly unloaded the picker. The old loaded-state guard rejected a subsequent custom selection. The picker now guards programmatic and bubbled changes and accepts direct selections throughout that transition.
- Minecraft Java still needs a non-shared, exact executable selected by the user, or manual recording on the allowed display. The launcher and Xbox placeholder stay ineligible. Bedrock needs independent package and live window evidence before adding an automatic association. [Windows package identity API](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackageid)
- The native integration with W2-L must retain the PCM sink, exported sample ABI, token and buffer disposal in Api.cpp, and the sampling deadline reset at `AudioStage::Streaming` in Audio.cpp. The diagnostics lane changes logging next to that deadline. Merge the behavior with the logger changes and rerun the serial native microphone fixtures. W2-U can be integrated from its own branch.

## Supervised acceptance

1. Start a fresh disposable profile and try Back, Continue, Skip, leave Settings, and resume. Repeat with upgraded settings and verify custom routes and shortcuts after Basic/Advanced switches. Check keyboard order, narrow layout and mixed DPI.
2. Allow one display while master is off. Arm recording, switch verified games, relaunch and move between allowed and blocked displays. Start manual recording separately. Revoke an active display while saving and check for a stale or published clip. Try an offline launcher and the Minecraft Java manual fallback.
3. Preview the HUD, then test it on both captured displays and with taskbar/mixed-DPI changes. Test no mic, one quiet or missing mic, two independently audible mics and silence after white. Inspect a short nonprivate saved replay for HUD pixels. Test actual five-second record/playback in both audio modes with the same mic and listen to the default output.
4. Import two themes, switch to built-in and back, remove the input files, quit and restart. Verify the gallery subtitle, Games list space and saved game-source controls when moving between modes.

## Wave 2 stabilization findings

The starting point was PR #83 at `eb1085ca97fd82be82077042d8bc3ad64af8821a`. The acceptance ledger tracks the seven follow-ups individually. The stabilization branch uses generated media and disposable profiles.

| Finding | Cause and change | Remaining owner check |
| --- | --- | --- |
| W2-F05 | Reproduced the tag-order loop in the WPF metadata control before changing it. SQLite returns tags in key order, so adding two tags in reverse order kept falsely triggering another write. Metadata compares canonical values and stops after the active write and at most one follow-up. Metadata and timeline saves have separate eight-second deadlines. Navigation uses the latest request and tracks the displayed page. | Repeat the original two-tag workflow, then close the window and quit from the tray. If symptoms return, keep the process running long enough to collect the development log and dump. |
| W2-F06 | A development-only writer drains the same diagnostic buffer in bounded batches, rotates two 1 MiB files, identifies the session and build, and logs operation duration and outcome. A dispatcher probe helps distinguish a blocked UI from an outstanding asynchronous operation. | Compare diagnostics on and off in the same real workload when checking frame times and disk contention. See [diagnostics](diagnostics.md) for paths, session retrieval and hang dumps. |
| W2-F01 | Gallery commits update the existing Editor using clip identity and revision. Returning to Editor can refresh the saved metadata without reopening the recording. A dirty form retains its fields and reports same-clip conflicts explicitly. | Use Gallery and Editor on the same existing clip. Verify display title, game, favorite and tags while keeping the same trim and file identity. |
| W2-F02 | The old code delayed hiding the native surface until after source verification, media probing and draft loading. The replacement hides it once the selection is accepted and delays the progress indicator by 250 ms. The reused player reveals video after playback starts for its exact playlist entry. | Switch between visibly different clips, including slow or unavailable media. Check mixed DPI, minimize/restore, Windows animation settings and the owner's actual libmpv/GPU combination. |
| W2-F03 | The Minesweeper entry was a loose executable at the root of a verified collection, with no executable-to-install association, exact approval or block. The collection scan intentionally requires stronger ownership evidence for loose executables than it does for scoped installations. Actionable rows show the reason and offer a scoped rescan or exact registration. Ignoring an issue records its stable entry ID and type outside capture rules. | Register the exact Minesweeper file and verify automatic detection and display authorization. Exercise Ignore and Unignore through a rescan and restart. |
| W2-F04 | The owner occurrence remains unconfirmed. The picker now retains one observable collection across saves and Fluent template changes. A saved theme appears in the popup immediately, and rename has explicit duplicate handling and save rollback. | Create and edit named themes on the owner preview, open the dropdown after save, switch Light and Custom, and verify after a process restart. |
| W2-F07 | F5 and the tray's **Refresh visible page** reuse the active page's refresh path. A settled indexed Gallery can re-read the existing catalog, the Editor retains its edit state, and Games refreshes the current view of discovery. | Check Refresh during editing and capture, and with a library source offline. |

### Save ownership and recovery

A timed-out write keeps its task ownership until it settles. The SQLite writers validate revision and source under the transaction and check cancellation before commit. Sidecar writes stage a replacement and check cancellation before publishing it. An OS operation already inside a commit can finish after the deadline, with the existing revision and source checks still governing subsequent writes.

If closing with pending changes, the Editor attempts a bounded profile-local checkpoint under `local/editor-recovery`. Reopening the same verified file offers recovery. Changes to unrelated fields can coexist with the recovered draft. Same-field conflicts leave the recovery copy on disk and the current saved values intact. A failed checkpoint prompts the user to keep the editor open or explicitly discard the pending edits. The checkpoint holds local clip metadata, so treat it as private data.

### Minecraft Java

The current saved Xbox discovery evidence identifies the placeholder as a launch proxy. Earlier owner evidence identified a separate Java gameplay runtime bundled by Minecraft Launcher. **Fix detection** explains the distinction and can register a dedicated exact executable. Shared or changing Java runtimes still need a verified gameplay association before they can qualify for automatic capture. Use manual recording on an allowed display for that case. Automatic Java association and live Bedrock verification remain open.

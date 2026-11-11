# QuickClip Personal — owner field-test findings, 2026-11-12

STATUS: Owner-observed real Windows 10 hands-on feedback on PR #7, integration/p0-capture-games @ 0c7719410a045a7a28c7a37d1d8f14d6cc045ef7. This is an actionable backlog, NOT acceptance-gate evidence proving full functionality. Current custom/main is a different checkout. Do not touch existing media or upstream main.

Read the current .ai/public/ORCHESTRATOR.md, INTEGRATE_P0.md, GAME_DISCOVERY_SCOPE.md, CAPTURE_PLATFORM_POLICY.md, TRACKER.json and this note. The owner prefers completed, polished existing workflows over new feature sprawl.

## Repository observations establishing why these happened

- GameCatalogModels.CatalogRequest has MaxEntries = 20,000 and MaxDepth = 8. GameCatalogService.VisitEntries increments a counter on every file AND folder, not games. A library containing 20,000 loose files can hit the limit after finding only eight games.
- GamesSettingsView.Approve(false) creates GameScanRoot(path) with IsCollection=false, and ScanFolder treats that folder and its descendants as ONE game. ScanCollection already exists, but isn't selected by that UI path.
- GameCatalogService.ScanExecutables adds ordinary .exe files indiscriminately. FolderCandidate simply takes folder basename; Steam manifests have better game names. Launcher/uninstaller/crash helper executables can pollute the list.
- Games settings exposes raw classification and launcher-role controls, raw executable paths, generic "Needs review" and a separate bottom detail expander. Settings navigation is bare "Settings | Games" radio controls.
- AppSettings.ClipsFolder and SettingsView.ChangeFolder_Click ALREADY configure the location of saved videos in Gallery, currently buried as "Clips folder" under General. Reuse the backend, surface it prominently in Library/Storage.
- Native audio enumerates both eRender (playback) and eCapture (microphone). The same Sonar-friendly name can validly appear under two different endpoint IDs/flows; the combined device picker obscures this.
- EditorView.xaml already has TimelineDivider with drag-resize, but the default timeline is 220px, audio rows are 74px plus margins, and TimelineBar.HandleWheel consumes wheel to zoom rather than scroll.
- Personal AppSettings.HotkeyKey currently defaults to 0, while the owner's desired new default is Alt+F10 ONLY for Save Clip. AppHost still shows retired specific-duration and replay-marker menu items.

## Issues and acceptance contracts

### FT-01 — P0 — Automatic launcher discovery without per-location approval
The app should find launcher libraries and installed games by default without individually requiring approval for normal Steam/Epic/etc library paths. Discover Steam install location through registry/config/libraryfolders.vdf and scan multiple installed Steam libraries on different drives. Discover other supported launcher metadata and known install roots similarly, including non-C: drives, without blind exhaustive full-disk scans. Show detected sources and optional exclusions with transparent privacy disclosure. The owner wants auto-discovery ON by preference, not a folder approval workflow. Additional providers (Epic, Battle.net, EA, Ubisoft, GOG, Riot, Xbox/Game Pass) are incremental tested adapters; do not show them as supported until they are.

ACCEPT: Two Steam libraries on different drives are detected without separate approval dialogs, manifest names are correct, entries aren't duplicated, user exclusions persist. Reading known locations isn't permission to indiscriminately recurse every installed drive.

### FT-02 — P0 — 20k entries limit misleads and misses games
Counter currently means filesystem entries, NOT detected games. Replace one global deep traversal with segmented launcher manifests, shallow install-root discovery and selective executable probing. Bound per-root time/depth/entries, support cancel/resume and stable partial results. Progress UI says "X games found" and lists slow/incomplete sources in a nonalarming way; raw file/entry budgets belong in Diagnostics. Do NOT merely lift MaxEntries to a huge number or hide missing results.

ACCEPT: A representative >20k-file library identifies top-level installs without freezing UI; scans don't erase preexisting entries/exclusions when partially completed.

### FT-03 — P1 UX — Icon-only per-game targeting toggle
Replace wordy "Exclude from game targeting" and "Allow game targeting" row buttons with small monitor/display ON and red OFF icons, consistently aligned, with informative hover tooltips and accessible labels. Disabled state and effective recording semantics must be accurate: manual entire-display capture can still show any game visible on that display; this is eligibility for future automatic game targeting until fully wired.

ACCEPT: state immediately visible, persists restart, works with keyboard/screen reader, not color-only.

### FT-04 — P1 UX — Inline animated expandable game details and simpler defaults
Click game row to expand a compact detail view directly beneath it, not an always-open bottom details pane. Smooth short motion, reduced-motion fallback, virtualization/list stability when searching. Default details: editable meaningful game name, capture toggle, essential runtime-active options. Hide internal "Automatic/Game/Application/Companion" classification and "Toggle launcher role" under Advanced with plain-English labels, tooltips and warnings; never expose settings not actually respected by recorder as functioning.

ACCEPT: compact/expanded layouts at 8/50/200 games, correct row state after filtering, keyboard/a11y and 1080p scaling.

### FT-05 — P1 UX — Remove generic "Needs review"
Ordinary entries should be cleanly shown as Enabled/Disabled with no scary confidence labels. Genuine issues such as offline drives, inaccessible folders and unknown executable should have specific helpful status ("Drive offline — games remembered"), not a vague warning. Detailed internal classification/confidence remains accessible only in Diagnostics.

### FT-06 — P0 — D:/Games needs collection semantics and smarter game names
Adding D:/Games currently creates one giant game "Games" and records all nested EXEs. "Add game or folder to scan" should treat library roots (D:/Games, D:/FreeWare) as collections of separate installed game folders, including bounded nested layouts like D:/Games/Cracked/[GameName]/[GameExe]. Support exact EXE manual entry separately. Auto-detect whether a folder is a single install or collection when clear, or offer an understandable choice.

Use game-name ranking: launcher metadata > installed package metadata > validated EXE ProductName/FileDescription > meaningful game folder > EXE stem; support explicit rename. Folder wrappers "Collections", "bin", "x64", "Win64" are not game titles. Don't promise perfect names from binaries with missing or deliberately altered metadata. Group likely renderer EXEs, filter or demote uninstallers, updaters, crash handlers, redistributables, helper binaries and generic setup programs; don't auto-execute discovered files. Keep technical executable list under optional advanced details, not every game row.

ACCEPT: multi-game folder gives multiple game entries; representative EXEs with version metadata have sensible names; stable user overrides, launchers helpers aren't all selectable games; no infinite recursive scanning.

### FT-07 — P1 UX — Cohesive settings navigation
Replace plain Settings | Games selector with a real visually consistent sidebar or top category bar, matching Games and main settings styles. Suggested categories: General, Recording/Video, Audio, Games, Sharing, Hotkeys, Diagnostics, Library/Storage. Combine adjacent categories if separate pages feel empty. Responsive sizing/keyboard navigation, avoid giant one-page Settings and inconsistent Games styling.

### FT-08 + FT-13 — P1 UX — Destructive red at rest AND on hover/focus
Use semantic action labels (Delete / Cancel, Discard changes / Keep editing, Remove game / Keep game), not ambiguous Yes/No confirmation. Destructive actions must be visibly red at rest, on hover, keyboard focus and pressed; don't let hover revert to normal accent. Specifically Gallery "Delete selected…" is red at rest and while hovered. Make a reusable DangerButtonStyle in theme resources and apply consistently. The safe button is neutral/primary even if it used to be labelled "No". Also show disabled game state in red (informational state, NOT a destructive action). Ensure accessible contrast/color-independent labels.

### FT-09 — P0 — Armed game replay must not continuously capture idle desktop
Owner wants a master QuickClip enable switch, game capture ARMED while waiting for an eligible detected game, and a separate, explicitly chosen Desktop Capture toggle. With master ON, no eligible game and desktop OFF: zero capture sessions, zero private desktop recording. When an allowed game opens, authorized backend starts; when it closes and no other eligible game remains, recording stops/debounces and stale buffer is discarded. Do NOT rely exclusively on mouse focus: a still-running game should remain the recognized context while user alt-tabs to Discord, without switching to Discord. DDA may be a consented backend for a game-triggered session on Win10, but truthfully captures visible full-display pixels while the game remains open; do not pretend it isolates the game. Game disabled must prevent auto start, while manually chosen Desktop mode remains independent.

Design a single explicit state machine: OFF, ARMED_WAITING_FOR_GAME, GAME_TRIGGERED_RECORDING, MANUAL_DESKTOP_RECORDING, ERROR/UNAVAILABLE. Handle multiple games, launchers, minimized/closed windows, monitor changes, source failures, recorder restarts, permissions, alt-tab and tray state without generating two capture graphs. Preserve and migrate legacy PreferGameWindow settings safely; no silent whole-display consent.

ACCEPT: 10 minutes master ON with zero approved games -> no video-capture graph; open eligible Terraria -> active replay; disabled game -> idle; game alt-tab Discord on another monitor -> no display switch; final game exits -> capture stops; explicit Desktop capture on/off works; disk/source failures never fall back to unauthorized display.

### FT-09b — P0/P1 — Dedicated configurable Hotkeys page
Commands: Save Clip (new installation DEFAULT Alt+F10); Toggle master QuickClip; Toggle Desktop Capture; Switch active monitor. All other commands default UNBOUND. Optional clear/reset, conflict detection with Windows and other QuickClip bindings, explicit role/description, reliable tray/background operation. Never overwrite saved existing user binding or intentional unbind on upgrade. Switching monitors only among connected and authorized displays and must not bypass full-display consent. Remove retired "Save a specific duration" and replay marker menu actions from normal UI, preserving previously stored bookmarks/metadata.

### FT-10 — P1 video correctness — requested 60 seconds is saved 60.4–60.6
Observed actual saved-duration reports of 60.4 and 60.6 seconds when requested buffer is 60. Likely keyframe/GOP pre-roll; investigate frame timestamps with ffprobe and output mux path before assuming hotkey latency. Two distinct requirements:
(1) actual playable saved media should be as close as feasible to the configured window (ideally within one video frame plus documented audio/container tolerance) without broken first GOP, unnecessary full-clip reencode or A/V skew. Do NOT merely change displayed duration and call it fixed;
(2) short clip/tile/overlay durations should show SS below one minute or MM:SS at one minute+, never raw floating decimal; preserve precision in editor trimming.
Shorter warm-up buffer should report actual available length truthfully. Test 15/30/60/120 requested lengths using synthetic and owner-approved real capture, probe first decodable frame/audio alignment and seek/export.

### FT-11 — P0 audio defect — separate Playback versus Mic and diagnose silent Sonar eCapture
Owner's audio endpoint list shows eRender/Playback Sonar Microphone, eCapture/Microphone Sonar Microphone, and eCapture/Microphone Arctis Pro Chat. In owner test: Sonar Playback and Arctis hardware mic had recorded voice; Sonar eCapture Mic was silent. Same friendly name does NOT indicate same endpoint; Windows ID prefixes {0.0.0...}=render and {0.0.1...}=capture. These must appear in distinct "Playback (what you hear)" and "Microphones (input)" sections, optionally group per Game/Chat/Media/Mic, label flow clearly, not duplicate-looking entries in one picker.

Investigate native WASAPI eCapture source, initialize/errors/event delivery, channel format, Windows Mic privacy, Sonar gate/mute, virtual input routing and monitor path, not just UI. Never blindly hide an enumerated silent device or mark it functioning. Prefer an optional short owner-triggered level meter/test input; log sanitized HRESULT and flow/endpoint role; preserve user device selection across unplug/reconnect with visible missing state, no silent switching to another mic. Use anonymized same-name different-flow test fixtures and generated test tones; do not commit device UUIDs/audio samples. Verify 3-track Game/Chat/Mic isolation, no duplicates or missing tracks. WASAPI playback requires eRender loopback, mic requires eCapture; same-named render path can legitimately carry voice.

### FT-12 — P1 editor usability — denser tracks, visible resize and scroll-by-default
Existing TimelineDivider already allows resizing preview vs timeline but is visually obscure. Make obvious grabbable divider and preserve minimum usable preview/audio heights, ideally remember resizing. Reduce 74px audio lanes (plus margins) to a compact ~48–56px target with expand-on-demand controls; trim 56px ruler and 260px track labels where appropriate, without harming usability at 125/150% DPI. Wheel over audio/waveform defaults to vertical scrolling track list; Ctrl+wheel zooms at pointer position; Shift+wheel may pan horizontally; existing +/-/0 controls remain. Ensure WPF wheel event bubbling doesn't swallow track-list scrolling. Keep waveform, trim handles, volume, solo, export, touchpad and gallery preview behavior correct.

### FT-14 — P1 Library/Storage — where actual Gallery recordings are saved
User requests default storage location for the saved video files displayed in Gallery, NOT the rolling RAM replay and not merely the gallery SQLite/cache. Backend already exists: AppSettings.ClipsFolder and ChangeFolder_Click. Surface as "Saved recordings location" within Library/Storage, display selected folder, free space, Change/Open. Changing folder must not silently move/delete old videos or make past gallery entries vanish; provide a clear keep-old-library/add-existing-folder or explicit migration/import path with rollback and index reconciliation. Do not create competing path setting. If user actually means cache/database location, that is a separate optional advanced concern needing its own safe migration; full-session recordings are currently parked.

## Scope and gate rule
These are owner observations, NOT evidence all features pass. Update canonical TRACKER.json and QUALITY_GATES.md only in a dedicated gate-schema/acceptance PR with preserved history and new gate IDs, not vague 90% percentages. Source-level fixes and synthetic tests cannot close supervised hardware gates. Keep PR #7 and original PRs #3/#4 open until authorized; avoid stuffing all 14 items into the existing integration PR. Split by owner/area after a stable, reviewed integration baseline. Preserve upstream main and personal clip library. Avoid making the settings unnecessarily complicated; defaults should be simple and advanced controls opt-in.

### FT-15 — P1 correctness — Editor retains a source recording after Gallery deletes it
Owner reproduction: open a gallery video in Editor, leave Editor open, switch to Gallery, delete that same recording to Recycle Bin, return to Editor. The editor still shows the old title, time range, audio waveform/tracks and export actions. Playback fails with "Preview not available for this file (export may still work)"; exporting then fails because the source video no longer exists. The owner confirms this on the PR #7 field-test build.

Repository cause: GalleryView.DeleteClipsAsync calls ClipDeletion.Current.DeleteAsync then reloads Gallery. Gallery subscribes to ClipDeletion.Current.Changed, but EditorView currently does not subscribe. EditorView.IsVisibleChanged only pauses mpv when hidden, and still keeps stale _media and _tracks; its own DeleteMedia_Click handles cleanup only when deletion starts inside Editor. ExportNew_Click/RunExportAsync rely on stale MediaInfo and start the export without a fresh source-exists check.

REQUIRED:
- Synchronize active Editor source with Gallery delete / undo events and also verify source existence upon Editor reactivation (cover external moves/deletes).
- If source is gone, stop stale preview, cancel stale waveform loading, disable Save Over / Export / play controls, show a clear nontechnical message such as "This recording was moved to the Recycle Bin. Restore it or open another video." Don't leave a ghost editable clip or suggest export can work when the original is missing. Provide Restored/Open Another actions only where appropriate and safe.
- If the user has unsaved editing work or metadata changes, do NOT silently destroy it on missing file; show that edits cannot be exported until source is restored. No imaginary recovery of pixels from an mpv cache.
- After an Undo that actually restores the original path, re-probe the source and offer to resume editing, preserving safe draft state as feasible.
- Do not accidentally tear down editor just because Gallery refreshes; check exact path identity and actual file existence. Handle rename/move, path case, file lock, ongoing export, and failure-to-recycle separately.
- At export entry, validate source file still exists and is readable BEFORE expensive processing; show an immediate actionable error rather than a late FFmpeg failure. Existing generated output and unrelated media must remain untouched.

ACCEPT: reproducible automated/UI test for Editor open -> Gallery delete -> return Editor; delete canceled; delete failed; undo/restore; external delete/move; missing source before export; deletion during export; original source still present. Existing Editor own delete, Gallery reload and share flows must continue working.

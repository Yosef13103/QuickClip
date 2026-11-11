# QuickClip Personal — evidence-based quality gates (v5)

**Baseline:** `custom/main` at `4c31d749a81b769da438e4d1276f90888fd7a40e` (2026-11-11). This is a *read-only source audit*, not a fresh Windows build.

The previous agent reported **365 tests passed / 18 opt-in skipped** for the v4 integration; this was not a current-HEAD CI run.

## Scoring rules

- A feature gets **two separate numbers**: **implementation gates passed/total** and **acceptance gates passed/total**. These are *checklist counts*, not subjective “90% done” estimates.
- `reported` (agent says passed) is **not** counted until evidence is reproduced on the target HEAD; `pending` likewise does not count. A tracked gate with a real command/log or cited source and a verified result can change to `pass`.
- A P0 feature is **DONE** only at full implementation AND acceptance, with no blocked/failed cases. No new P2 work until P0 is green.
- All percentages are the fraction of **explicit acceptance gates**, not a statement about effort remaining. Adding new user requirements increases the denominator visibly.
- Reviewers should reject a PR claiming complete when the evidence cells or tests are missing.

## Current scorecard

| ID | Area | Tier | Code gates | Acceptance gates | State |
|---|---|---|---:|---:|---|
| QC-001 | Reproducible builds, CI and protected integration | P0 | 0/3 | 0/2 | active |
| QC-002 | Instant replay correctness, saving and memory safety | P0 | 3/4 | 0/5 | active |
| QC-003 | Automatic game detection and per-game profiles | P0 | 4/6 | 0/5 | active |
| QC-004 | Capture targeting and truthful recorded-source identity | P0 | 3/5 | 0/4 | active |
| QC-005 | Separated audio tracks and sync | P0 | 2/3 | 0/4 | active |
| QC-006 | Capture geometry and codec correctness | P0 | 3/3 | 0/3 | active |
| QC-007 | Recording performance and resource stability | P0 | 2/3 | 0/3 | active |
| QC-008 | Unified gallery/editor and export reliability | P1 | 4/4 | 0/3 | active |
| QC-009 | SQLite metadata, storage and recovery | P1 | 3/3 | 0/3 | active |
| QC-010 | Clip notification: short truthful information banner | P1 | 2/5 | 0/3 | active |
| QC-011 | Advanced encoding control with safe limits | P1 | 1/4 | 0/3 | active |
| QC-012 | Production Personal installer and Windows shell integration | P1 | 1/5 | 0/4 | active |
| QC-013 | Visual themes and custom Windows title bar | P2 | 0/3 | 0/3 | active |
| QC-014 | Optional OpenRGB and OLED event adapters | P2 | 0/2 | 0/2 | parked |
| QC-015 | Optional full-session recording | P2 | 0/2 | 0/2 | parked |

**Interpretation:** many source files exist, but the target-machine acceptance matrix has not been formally executed. A zero in acceptance does *not* mean the feature is broken.

## Gate-by-gate acceptance ledger

### QC-001 — Reproducible builds, CI and protected integration [P0, active]

First milestone. A passing local report is not the same as required CI.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-001-I01` | implementation | TODO | Windows GitHub Actions builds and deterministic dependency restore |
| `QC-001-I02` | implementation | TODO | Test job runs synthetic managed and relevant native tests on PR |
| `QC-001-I03` | implementation | TODO | PRs target custom/main with change/evidence handoff and protected main |
| `QC-001-A01` | acceptance | TODO | Latest custom/main passes Windows CI (0 failing; skips explicitly reported) |
| `QC-001-A02` | acceptance | TODO | Every new P0 PR has traceable acceptance IDs and reproducible commands |

### QC-002 — Instant replay correctness, saving and memory safety [P0, active]

User chooses one save-whole-buffer command and trims in editor.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-002-I01` | implementation | PASS | Native capture -> encoded replay ring and save-to-file path exist |
| `QC-002-I02` | implementation | PASS | One normal hotkey saves the configured available replay; no separate duration action needed |
| `QC-002-I03` | implementation | PASS | Saved clip receipt exposes actual duration and successful publication |
| `QC-002-I04` | implementation | TODO | Bounded replay memory policy, including high bitrate and stalled video input |
| `QC-002-A01` | acceptance | TODO | 100/100 supervised replay saves produce readable media with expected codecs/tracks |
| `QC-002-A02` | acceptance | TODO | 60-minute replay soak: no crash, uncontrolled RAM growth, or corrupt save |
| `QC-002-A03` | acceptance | TODO | Save near warm-up, encoder restart, sleep/wake, resolution/source changes passes 20/20 cases |
| `QC-002-A04` | acceptance | TODO | Saved media durations match actual available span within documented keyframe tolerance |
| `QC-002-A05` | acceptance | TODO | Failure, disk-full and permission-denied paths preserve existing media and show accurate errors |

### QC-003 — Automatic game detection and per-game profiles [P0, active]

Highest feature gap: catalog exists but live runtime is not connected.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-003-I01` | implementation | PASS | Approved-folder/Steam/manual EXE scanning service implemented |
| `QC-003-I02` | implementation | PASS | Catalog path matcher and saved profile model implemented |
| `QC-003-I03` | implementation | PASS | GameDetector polls verified process owners and applies the catalog before fallback. See commit `b13a2d3` and [synthetic evidence](../../docs/p0-games-discord.md). |
| `QC-003-I04` | implementation | PASS | Games settings exposes approved roots, scan, manual EXEs, inclusion and supported identity overrides. See [WPF and persistence evidence](../../docs/p0-games-discord.md). |
| `QC-003-I05` | implementation | TODO | Effective profile applied to capture at stable game/session boundaries |
| `QC-003-I06` | implementation | TODO | Game start/exit/alt-tab session lifecycle and explicit fallback policy |
| `QC-003-A01` | acceptance | TODO | 12/12 representative Steam and manual catalog cases correctly recognized |
| `QC-003-A02` | acceptance | TODO | 30/30 supervised launch/alt-tab/restart/exit transitions match expected identity |
| `QC-003-A03` | acceptance | TODO | 0 false positives across 10 chosen non-game applications, including Discord updater |
| `QC-003-A04` | acceptance | TODO | 10/10 excluded apps never promoted to game capture |
| `QC-003-A05` | acceptance | TODO | Profile inheritance and persistence passes 12/12 transition/restart tests |

### QC-004 — Capture targeting and truthful recorded-source identity [P0, active]

Do not promise Windows 10 border-free Game Only capture by unsupported workarounds.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-004-I01` | implementation | PASS | Borderless monitor capture via Desktop Duplication |
| `QC-004-I02` | implementation | PASS | Window-only target capture via Windows Graphics Capture |
| `QC-004-I03` | implementation | PASS | Automatic Game First mode with selected monitor fallback |
| `QC-004-I04` | implementation | TODO | Strict Game Only mode with no desktop fallback |
| `QC-004-I05` | implementation | TODO | Clip receipt captures true source/mode/display and stream geometry over saved interval |
| `QC-004-A01` | acceptance | TODO | 20/20 alt-tab cases preserve intended target or deliberately stop without leaking desktop |
| `QC-004-A02` | acceptance | TODO | 10/10 fallback cases visibly report the actual monitor and application source |
| `QC-004-A03` | acceptance | TODO | 10/10 source switch/restart scenarios retain valid clips and accurate labeling |
| `QC-004-A04` | acceptance | TODO | Windows 10 WGC border/exclusive fullscreen limitations tested and explained |

### QC-005 — Separated audio tracks and sync [P0, active]

Audio is a recording feature, not a postproduction nice-to-have.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-005-I01` | implementation | PASS | Endpoint-based audio route planner exists |
| `QC-005-I02` | implementation | PASS | Multi-stream video container recording and track naming exists |
| `QC-005-I03` | implementation | TODO | Loss/reconnect handling and visible warnings are adequately bounded |
| `QC-005-A01` | acceptance | TODO | 20/20 supervised recordings isolate Game, Chat and Mic using known test tones |
| `QC-005-A02` | acceptance | TODO | A/V sync drift <= 100 ms in 10 timed test clips at target FPS |
| `QC-005-A03` | acceptance | TODO | 10/10 device disconnect/reconnect cases degrade or recover as documented |
| `QC-005-A04` | acceptance | TODO | No silent audio-track merging or misnaming across profile switches |

### QC-006 — Capture geometry and codec correctness [P0, active]

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-006-I01` | implementation | PASS | Native resolution/aspect selection and center crop |
| `QC-006-I02` | implementation | PASS | Custom ratio/dimensions with no automatic 4K upscale on 1440p |
| `QC-006-I03` | implementation | PASS | Visual source/crop/output preview is implemented |
| `QC-006-A01` | acceptance | TODO | Probe 20/20 real output cases across native/16:9/custom and resolutions |
| `QC-006-A02` | acceptance | TODO | 10/10 source changes preserve correct output size and no unexpected stretch |
| `QC-006-A03` | acceptance | TODO | NVENC H.264/HEVC + available HW AV1 options probe and decode correctly |

### QC-007 — Recording performance and resource stability [P0, active]

Informal 160 FPS off vs 133-140 on is history, NOT a verified performance acceptance result.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-007-I01` | implementation | PASS | Capture/submit/encode-path diagnostic counters exist |
| `QC-007-I02` | implementation | PASS | Bounded opt-in benchmark runner exists |
| `QC-007-I03` | implementation | TODO | A repeatable exported performance baseline with script, hardware/settings and frame times |
| `QC-007-A01` | acceptance | TODO | At least 3 controlled runs each recorder-off/DDA/WGC with game frametimes and 1% lows |
| `QC-007-A02` | acceptance | TODO | 60-minute soak: CPU/GPU/RAM and buffers bounded with saved media verified |
| `QC-007-A03` | acceptance | TODO | No feature regression exceeds agreed frame-time budget vs measured baseline |

### QC-008 — Unified gallery/editor and export reliability [P1, active]

User reports these workflows working well; record reproducible checks before calling release-ready.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-008-I01` | implementation | PASS | Larger cached thumbnails and single muted hover preview |
| `QC-008-I02` | implementation | PASS | Unified editor with resizable workspace and fullscreen |
| `QC-008-I03` | implementation | PASS | In-app confirmation modals and safe delete/Undo |
| `QC-008-I04` | implementation | PASS | FFmpeg export/share and audio track selection are implemented |
| `QC-008-A01` | acceptance | TODO | 30/30 manual gallery/play/edit/share/delete operations on disposable clips |
| `QC-008-A02` | acceptance | TODO | 10/10 mixed-DPI/fullscreen/editor-resize scenarios pass |
| `QC-008-A03` | acceptance | TODO | 5/5 1000+ clip library navigation benchmarks satisfy agreed latency target |

### QC-009 — SQLite metadata, storage and recovery [P1, active]

Do not implement automatic deleting, dedup, or retention until explicitly approved.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-009-I01` | implementation | PASS | SQLite catalog + migrations and guarded legacy JSON import |
| `QC-009-I02` | implementation | PASS | Tags, favorites, clip metadata and library filtering integrated |
| `QC-009-I03` | implementation | PASS | Read-only indexed inventory exists |
| `QC-009-A01` | acceptance | TODO | 10/10 legacy/synthetic migration and restart cases preserve annotations |
| `QC-009-A02` | acceptance | TODO | 10/10 offline/rename/cross-volume/locked-file cases never lose source media |
| `QC-009-A03` | acceptance | TODO | Verified backup, corruption recovery and 1000+ actual clips UI latency |

### QC-010 — Clip notification: short truthful information banner [P1, active]

No false source label from stale GameForClip heuristics. Use save-time capture/stream facts.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-010-I01` | implementation | PASS | Independent sound, Windows and overlay channels exist |
| `QC-010-I02` | implementation | PASS | Success notification triggers after save receipt rather than hotkey |
| `QC-010-I03` | implementation | TODO | Small banner shows exact game OR Desktop + display identity |
| `QC-010-I04` | implementation | TODO | Banner shows verified resolution, output FPS and actual saved duration |
| `QC-010-I05` | implementation | TODO | Subtle edge/corner ripple and optional local PNG style with user controls |
| `QC-010-A01` | acceptance | TODO | 10/10 game + desktop saves show truthful source/monitor and stream facts |
| `QC-010-A02` | acceptance | TODO | 20/20 saves preserve focus, avoid capture of overlay where supported, and restore idle state |
| `QC-010-A03` | acceptance | TODO | Mixed DPI, focus, 9 anchors and failure states pass 20/20 manual checks |

### QC-011 — Advanced encoding control with safe limits [P1, active]

Do NOT ship CQP as a toggle before bounded memory accounting.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-011-I01` | implementation | PASS | Automatic bitrate and quality presets |
| `QC-011-I02` | implementation | TODO | Explicit custom target bitrate persisted and applied to Recorder |
| `QC-011-I03` | implementation | TODO | Supported rate-control modes CBR/VBR/CQP exposed conditionally and applied |
| `QC-011-I04` | implementation | TODO | Byte-bounded replay budget, warnings and codec capability validation |
| `QC-011-A01` | acceptance | TODO | 20/20 bitrate/rate-control settings survive restart with correct FFmpeg output |
| `QC-011-A02` | acceptance | TODO | Long-buffer/high-bitrate and CQP cases stay within configured memory cap |
| `QC-011-A03` | acceptance | TODO | Encoder-fallback & invalid-param handling tested on available GPUs |

### QC-012 — Production Personal installer and Windows shell integration [P1, active]

Must be complete BEFORE labeling a production release; keep dev protections.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-012-I01` | implementation | PASS | Separate Personal dev/portable identity and bundled runtime |
| `QC-012-I02` | implementation | TODO | Production Personal installer AppId/install path distinct from upstream |
| `QC-012-I03` | implementation | TODO | Personal-owned Startup Run registration and user opt-in |
| `QC-012-I04` | implementation | TODO | Personal-owned Explorer verb registration/cleanup |
| `QC-012-I05` | implementation | TODO | Signed/updater provenance and safe upgrade/uninstall design |
| `QC-012-A01` | acceptance | TODO | Side-by-side stock+Personal installation passes 12/12 disposable-VM tests |
| `QC-012-A02` | acceptance | TODO | Startup + file association enable/disable/relocation pass 12/12 tests |
| `QC-012-A03` | acceptance | TODO | Upgrade/uninstall leave stock registry and media untouched 10/10 cases |
| `QC-012-A04` | acceptance | REPORTED | Portable starts with no Windows registry changes |

### QC-013 — Visual themes and custom Windows title bar [P2, active]

Do after P0 capture/identity is stable.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-013-I01` | implementation | TODO | Semantic theme resources support runtime switching |
| `QC-013-I02` | implementation | TODO | AMOLED, blue-gray Slate, Midnight, Light presets |
| `QC-013-I03` | implementation | TODO | Custom WindowChrome title bar with correct native window semantics |
| `QC-013-A01` | acceptance | TODO | 4/4 themes checked across app, overlays and modals for contrast/readability |
| `QC-013-A02` | acceptance | TODO | 10/10 move/maximize/snap/fullscreen/mixed-DPI restore cases |
| `QC-013-A03` | acceptance | TODO | Theme persists across restart; reduced-motion/high-contrast checked |

### QC-014 — Optional OpenRGB and OLED event adapters [P2, parked]

Fun feature, intentionally parked until core accepted.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-014-I01` | implementation | TODO | Tiny clip-saved event adapter interface, local-only and opt-in |
| `QC-014-I02` | implementation | TODO | OpenRGB / supported OLED drivers plugged in without blocking saves |
| `QC-014-A01` | acceptance | TODO | 20/20 save notifications emit without delaying file publication |
| `QC-014-A02` | acceptance | TODO | Disconnect/timeout/restore state tested for each supported device |

### QC-015 — Optional full-session recording [P2, parked]

Not required for single replay-save-and-trim workflow.

| Gate | Stage | Status | Objective evidence / requirement |
|---|---|---|---|
| `QC-015-I01` | implementation | TODO | Crash-safe, segmented writer with disk quota and explicit opt-in |
| `QC-015-I02` | implementation | TODO | Playback/export from finalized recovered segments |
| `QC-015-A01` | acceptance | TODO | 60-minute controlled session survives intentional crash and disk-full |
| `QC-015-A02` | acceptance | TODO | Instant replay still works with bounded overhead during session recording |

### QC-016 — Replay duration selector and bookmark UI [—, retired]

User explicitly removed from roadmap. Hide redundant controls; preserve pre-existing bookmark metadata non-destructively.

## What to remove from the active product surface

Retire the **Save a specific duration** submenu and **Add replay marker** action from the tray and remove the saved-clip Bookmark button/UI. Keep one Save Clip action that saves the configured replay history, then trim in the editor. Existing bookmark records in older SQLite/JSON metadata must remain intact and must not be deleted automatically. Prefer a backwards-compatible, low-risk UI retirement before deleting native APIs.

## Saved-clip banner contract

A concise two-line success banner, e.g. **Marvel Rivals · 3440×1440 · 60 fps** / **Saved 58.7 s**, or **Desktop · Display 2 · 1920×1080 · 60 fps** / **Saved 59.9 s**. Source/mode/display must come from the *actual capture epoch of the saved packets*, not a current setting or a guessed foreground game. If a clip spans sources, show **Mixed sources** or verified details. Duration must be actual encoded media duration. Missing metadata should be omitted rather than invented. Small, readable, configurable position; support sound-only; edge/corner waves and custom image are post-core polish.

## Merge / agent discipline

Use three strictly scoped worktrees after creating a baseline CI branch:

1. **Quality/CI:** `.github/workflows`, synthetic tests, `TRACKER.json`, test scripts, PR checklist; do not edit recorder or UI internals.
2. **Capture/reliability:** native capture, Recorder, source identity receipt, memory bounds, audio correctness. No new gallery/theme work.
3. **Game activation:** Discovery, GameDetector, catalog configuration UI and profile resolution adapter. Avoid shared Recorder edits; define an interface/handshake in a small PR.

All branches from a verified `origin/custom/main` SHA, push and open PRs targeting **custom/main**, and merge only after CI + feature gates. For shared `Recorder.cs` changes, integrate the game adapter after the capture agent establishes a stable interface. Update test evidence and this tracker in every PR. Never modify upstream `main`.

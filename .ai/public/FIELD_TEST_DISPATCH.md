# Chat 0 — field-test orchestration dispatch plan

This is a proposed bounded execution map for the owner's first in-app test of PR #7. Source of truth: FIELD_TEST_2026-11-12.md. Do not turn every reported UX refinement into a single gigantic integration patch. First inspect the latest GitHub PR #7 head, original #3/#4 heads and current custom/main; do not trust static SHAs.

## Gate 0 — protect and identify the baseline
PR #7 is NOT merged. PR #7's Windows CI passing proves builds/synthetic tests, but user found substantial P0 gaps during actual play and scan. Preserve the tested branch, settings and media. Do not claim that game capture automatically waits for a game: present DDA currently records the entire monitor whenever the switch is enabled.

Before dispatching, resolve development branch policy with owner:
- Recommended for steady collaboration: merge reviewed PR #7 as an explicitly NONRELEASE foundation only after owner confirms acceptance of its known limitations; record pending acceptance gates and do NOT ship as finished.
- Otherwise keep PR #7 open: new independent feature worktrees start at the latest integration/p0-capture-games SHA, feature PRs can target that integration branch; Chat 4 merges these into the integration branch, and PR #7 remains the final CI-validated gate to custom/main. Note that existing workflow's PR trigger may be limited to custom/main, so Chat 4 must validate locally and verify the final integration PR CI. Do not merge original #3/#4 individually. No direct edits to old local custom/main checkout.

## Phase A — fix core recording/discovery/audio correctness before visual polish
| Work | Owner | Priority | Affected FT IDs | Dependency / completion evidence |
|---|---|---|---|---|
| Game library discovery and correct collections, default Steam multi-library scan, bounded scan budgets, executable ranking | Chat 3 — Game Discovery Development | P0 | FT-01, FT-02, FT-06 | Producer of a stable catalog/eligibility contract for Chat 1; verified fixture scan on multiple drives + huge directory + custom D:/Games; separate PR |
| Armed-only game replay, idle zero-capture, manual desktop override, eligible-game launch/exit, stable monitor and explicit source consent | Chat 1 — Performance Development | P0 | FT-09 | Depends on typed verified game eligibility from Chat 3. Build state machine with fake observation tests first; only claim runtime complete when Chat 4 integration tests prove it. No unattended recording |
| Sonar audio endpoint eCapture diagnostics and proper source routing | Chat 1 (engine); Chat 2 (WPF selector) | P0 | FT-11 | Separate flow/endpoint identity contracts. Native tests + owner-supervised Sonar test tones. **Same friendly name may be two valid endpoints; don't dedupe by label** |
| Recorder missing-source safety and existing settings migration | Chat 1 + Chat 4 | P0 | FT-09, FT-09b | Existing game-first preference and DDA display consent preserved; no unauthorized desktop recording |

Do NOT dispatch two concurrent tasks touching the same worktree. Chat 0 may send a clearly sequenced plan to one conversation, not simultaneous turns.

## Phase B — targeted UX defects (independent of most capture backend)
| Work | Owner | Priority | FT IDs | Notes |
|---|---|---|---|---|
| Editor stale deleted recording bug + missing source export guard | Chat 2 — UI/UX Development | P1 correctness (do first in Chat 2) | FT-15 | Subscribe to deletion/restore signals or revalidate on editor activation, stop preview, disable export when missing, preserve unsaved draft; tests for Gallery delete, cancel, Undo, external delete |
| Consistent danger button style and descriptive confirmations | Chat 2 | P1 UX | FT-08, FT-13 | Destructive buttons red on normal/hover/focus/pressed; Delete selected… red; safe buttons neutral, not all No buttons red |
| Settings navigation sidebar or proper tabs + recordings storage location | Chat 2 | P1 UX | FT-07, FT-14 | Make Games + General/Recording/Audio/Sharing/Hotkeys/Diagnostics/Library cohesive. Gallery saved recordings already use ClipsFolder; don't create duplicate path. Export destination may be separate |
| Inline expandable game details, on/off monitor icons, simpler status/tooltips | Chat 2 | P1 UX | FT-03, FT-04, FT-05 | Wait for Chat 3's stable list model to avoid duplicate UI work. Advanced classifications hidden by default |
| Audio compact tracks, resize visibility, Ctrl+wheel zoom | Chat 2 | P1 UX | FT-12 | Existing TimelineDivider should be enhanced, not rebuilt |
| Human-friendly saved duration SS / MM:SS | Chat 2 | P1 UX | FT-10 | Formatting ONLY; do not claim actual video duration fixed |
| Gallery selectable day feed, day-level/game summary, cross-day batch selection and share icon | Chat 2 | P1 Gallery UX | FT-16 | Reuse existing selection state, grouping metadata and sharing; after FT-15 deleted-Editor-source correctness; preserve virtualization and accessible group toggles |

## Phase C — engine precision and keyboard workflows
| Work | Owner | Priority | FT IDs | Notes |
|---|---|---|---|---|
| Measure/repair 60.4/60.6 second actual recording duration | Chat 1 | P1 correctness | FT-10 | Analyze encoded packet/GOP boundaries, FFprobe, keyframes, A/V sync; don't just round displayed duration |
| New hotkey actions and master/desktop/monitor cycle routing | Chat 1 engine + Chat 2 WPF page | P0/P1 | FT-09b | Only Save Clip defaults Alt+F10 on fresh install, others unbound; don't overwrite saved user choices; remove retired duration/bookmark controls but keep saved data readable |
| Runtime diagnostics, 60-minute soak and frame-time comparisons | Chat 1 / Chat 4 | P0 | Existing QC-002, QC-007 | Once stable recording backend/activation finished, 60/120fps DDA and native 3440x1440; no invented performance percentage |

## Chat 4 integration and evidence acceptance
Chat 4 retains exclusive responsibility for resolving shared Recorder/GameDetector/WPF contracts, carrying source PR histories, running strict native/managed builds, synthetic tests and owner-supervised acceptance instructions. Chat 0 monitors PR, tests and explicit completion. Do not call Chat 4 complete just because a dispatch turn ended. All implementation PRs must list owned files, FT/QC IDs, acceptance commands, risk, migration steps and screenshots where UI changed.

Update numerical ledger in a separate small gate-schema PR after defining measurable requirements. Retain baseline SHA and existing historical evidence, add new gate IDs or explicit subrequirements; no fabricated 100% scores. Do not mark real hardware acceptance passed from mocked tests. List P0 blockers in clear user language.

## Product principle
QuickClip master ON with game mode ARMED while idle should consume no gameplay-capture resources and record no private desktop. Game-triggered capture on Windows 10 may use an explicitly authorized selected-monitor DDA source, which necessarily records whatever is visible on that display during the eligible game's session; game detection does NOT isolate image pixels. Windows 11 only uses game-window WGC without border when actually authorized and tested. Keep other new capture backends, extra-session recording, theme work and exotic integrations parked until P0 is validated.

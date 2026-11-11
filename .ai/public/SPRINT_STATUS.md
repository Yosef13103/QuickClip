# QuickClip P0 — current PR status and next actions

This is a living coordination note. **Revalidate GitHub head, PR status and CI on every session**; the SHAs below are the reviewed snapshot, not an automatic current-state guarantee.

| PR | Branch | Reviewed SHA | Decision |
|---|---|---|---|
| #2 — Windows quality CI | `chore/p0-ci-evidence` | `04e7c5627fdeb2b48e9922b981f04d26f680d76d` | **Merged** to custom/main at `60c4172a81ffa6accc7045000a0e6b8ee00715bd`. PR checks green, follow-up push check must also finish. |
| #3 — No-border fail-closed policy | `fix/p0-borderless-capture` | `1d3c0013bcb44af655e471a04f19aba94deae5d1` | **Held for integration**. Breaks existing Game First recording by blocking on Win10; settings misdescribe behavior. No true game-only no-border backend. |
| #4 — Games + Discord identity | `feat/p0-games-discord` | `5eeba2deab368c3bfc994e817f0581aab958327f` | **Held for integration**. Disabled toggle not honored by recorder; three reviewer findings require fixes. |

**Next:** Chat 4 combines #3 and #4 in `integration/p0-capture-games`, fixes review findings, validates against merged CI and publishes an integration PR. The GitHub reviewer then rechecks and, if safe and accepted, merges into `custom/main`. No P1/P2 feature implementation until P0 verification.

Keep Chat 0 as coordinator, Chat 1 capture/performance, Chat 2 UI, Chat 3 Games/Discord and Chat 4 local integration. The legacy P0 agent prompt files have been superseded by `ORCHESTRATOR.md`, `INTEGRATE_P0.md` and this status note; requirements remain in `P0_SPRINT_CONTRACT.md`. Previous prompts are still in Git history.

Progress: `python .ai/public/progress.py` from repo root. Formal gates are in `TRACKER.json`; do not change based only on a reviewer's opinion. Mandatory distinction: Windows CI passing ≠ Windows 10 game-only borderless working.

## User updates for the next integration (2026-11-11)
The initial decision to leave PR #3/#4 open remains. Added `CAPTURE_PLATFORM_POLICY.md` and `GAME_DISCOVERY_SCOPE.md`: Windows 11 should use game-only no-border WGC **where runtime API, packaged capability, granted user consent and real-world verification permit**; Windows 10 must explain the OS limitation and offer explicitly selected full-display Desktop Duplication. Games UI must discover default launcher install metadata over time, including alternative Steam libraries, allow own `D:/FreeWare/` roots and exact exe or bounded scoped patterns, and never turn a broad `Update.exe` basename rule into silent Discord recognition. Unsupported launchers/second-video pop-out recording are tracked missing features, not assumed completed.

**Dispatch order:** Initialize Chat 0 first; then send its integration dispatch to Chat 4. Source branches #3/#4 must still be integrated and validated in a new PR by Chat 4, not separately merged. Quality report uses exact gate evidence; manual Windows 11/Win10 capture acceptance not yet done.

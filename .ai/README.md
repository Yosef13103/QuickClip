# QuickClip fork engineering workspace

**Purpose:** evolve upstream QuickClip into a reliable, lightweight, highly customizable personal instant-replay recorder/editor and clip library while retaining easy upstream synchronization.

This bundle is an engineering specification, NOT a claim that any implementation is complete. `AGENTS.md` is the always-on Codex entrypoint. Use `.ai/CODEX_START.md` for the initial agent task.

## Read order
1. `../AGENTS.md` — durable safety and working conventions.
2. `PROJECT_SPEC.md` — full user's actual workflow and feature definitions.
3. `ARCHITECTURE.md` — proposed design, options, and integration boundaries.
4. `ROADMAP.md` — implement in increments, starting with read-only audit.
5. `MIGRATION.md` — safe SteelSeries Moments reconnaissance/migration.
6. `SAFETY.md` — approvals, privacy, backup, update provenance.
7. `TEST_MATRIX.md` — acceptance criteria and performance measurements.
8. `STATUS.md`, `DECISIONS.md` — update these throughout work.
9. `UPSTREAM.md` — fork sync / PR strategy.

## Status and precedence
Actual current source code and tests are authoritative about what already exists. The user's requirements in `PROJECT_SPEC.md` are authoritative about desired behavior. If they conflict, report discrepancy and design an opt-in change. Avoid gratuitous rewrites. Review `upstream/main` on each phase.

## Integration checkpoints
Phase 0: plan/audit/diagnostic report (no destructive operations). Phase 1: protected development environment + baseline tests. Phase 2: configurable WASAPI endpoint tracks. Phase 3: capture-target policy. Phase 4: metadata, renaming, searchable library. Phase 5: original master/edited derivatives and retention. Phase 6: safe legacy import and migration. Phase 7: optional concurrent capture. Phase 8: optimize and consider upstream PRs.

# QuickClip Personal development notes

The original upstream-tracking branch is `main`. All Personal feature PRs and integration branches target **`custom/main`**; never push changes to `main`.

## Current instructions

- [0 — Orchestrator operating contract](ORCHESTRATOR.md)
- [4 — Current P0 integration handoff (PR #3 and PR #4)](INTEGRATE_P0.md)
- [P0 sprint status and PR decisions](SPRINT_STATUS.md)
- [Capture platform policy: Windows 11 permission and Windows 10 display fallback](CAPTURE_PLATFORM_POLICY.md)
- [Game discovery: launcher manifests, custom folders, EXE/path patterns and Discord](GAME_DISCOVERY_SCOPE.md)
- [Shared product, capture and Games acceptance contract](P0_SPRINT_CONTRACT.md)
- [Numeric quality gates](QUALITY_GATES.md) · [machine-readable tracker](TRACKER.json) · `python progress.py`
- [Application architecture](ARCHITECTURE.md)
- [P0 acceptance and manual checks](VALIDATION.md)

## Technical handoffs

- [Development builds and release integration](../../docs/development.md)
- [SQLite schema, reconciliation and recovery](../../docs/sqlite-library-integration.md)
- [Replay receipts and marker contract](../../docs/replay-runtime-handoff.md)
- [Editor and recording-dimensions handoff](../../docs/unified-editor-ux.md)
- [v4 integration report](../../docs/personal-v4-integration.md)

Earlier P0 agent prompt files were removed from the live instruction directory after those agents submitted PRs; Git history preserves them. For **current integration** use `INTEGRATE_P0.md` rather than re-running the superseded agent prompts.

Keep build output, downloaded dependencies, local agent scratch files and personal profile data out of Git. Use temporary profiles and generated media for ordinary verification. Personal builds disable stock Windows registration and the upstream update channel. Live desktop/audio capture requires deliberate, owner-supervised source selection. Agent PRs must explicitly target `custom/main`, include acceptance evidence, and not merge themselves while integration is in progress.

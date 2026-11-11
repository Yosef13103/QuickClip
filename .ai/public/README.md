# QuickClip Personal development notes

`main` tracks the original project. Personal development lands on `custom/main` through reviewed feature or integration branches. Keep build outputs, local agent scratch files and profile data out of Git.

- [Application architecture](ARCHITECTURE.md)
- [Acceptance checks](VALIDATION.md)
- [Development builds and release integration](../../docs/development.md)
- [SQLite schema, reconciliation and recovery](../../docs/sqlite-library-integration.md)
- [Replay receipts and marker contract](../../docs/replay-runtime-handoff.md)
- [Editor and recording-dimensions handoff](../../docs/unified-editor-ux.md)
- [Queued v4 integration report](../../docs/personal-v4-integration.md)

Use temporary profiles and generated media for routine verification. Personal builds disable stock Windows registration and the upstream update channel. A live desktop or audio capture requires a supervised test with a deliberately selected source.

## Current P0 sprint instructions

- [Shared ownership, acceptance and Games UI contract](P0_SPRINT_CONTRACT.md)
- [Agent 1 — borderless capture and truthful sources](P0_AGENT_1_CAPTURE.md)
- [Agent 2 — Games page, discovery and Discord](P0_AGENT_2_GAMES.md)
- [Agent 3 — Windows CI and quality gate evidence](P0_AGENT_3_CI.md)

All agent branches must start from current origin/custom/main, publish PRs targeting custom/main, and leave upstream main alone. In particular, do not call a borderless game-only capture feature complete if only a monitor Desktop Duplication fallback is available.

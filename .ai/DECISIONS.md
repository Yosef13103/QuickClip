# Architecture decision record (ADR-lite)

- **D-001 / 2026-11-10:** Use a proper GitHub fork, keep main a clean upstream mirror, develop on custom/main; can still sync and send upstream PRs.
- **D-002:** Preserve C++ capture + C# WPF stack initially; don't rewrite in Rust absent measured performance/safety reason.
- **D-003:** Make explicit audio endpoint capture the first technical feature. User's Sonar already routes each app; process-specific capture is unnecessary for core requirements and limited by Windows build.
- **D-004:** Single sticky game-first video capture is the default. Concurrent game/Discord/all-monitor modes are optional with explicit resource and privacy cost.
- **D-005:** Preserve original capture instant independently from edits and exports. Metadata database supplements, never replaces, ordinary playable media files.
- **D-006:** Import/index existing finished videos in place and scan nested Moments originals read-only. No implicit cleanup, overwrite or large mass conversion.
- **D-007:** Fork binary must not share default settings/cache/update install namespace with upstream QuickClip.

Add new entries when design changes and include decision, alternatives, and evidence.

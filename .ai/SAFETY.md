# Security, privacy, resource and side-effect boundaries

## Trust and provenance
Upstream QuickClip is GPL-3.0. Its build involves .NET 10, native C++, FFmpeg and libmpv. Review scripts before executing, pin dependencies/hashes where practical, and keep third-party notices/license. Don't use the original GitHub updater unmodified in a custom-branded binary; an upstream update may overwrite modifications. Prefer disabled updater until a clearly owned, integrity-checked fork-specific release channel exists. Signed binaries or reproducible CI are longer-term goals, not assumptions.

## Source directories
Safe by default: read repo source; write repo files/test fixtures; create output under explicitly configured repo/dev/temp directory. Not safe without separate authorization: writing to SteelSeries Program Files/ProgramData, live GG databases, Windows startup/run keys, device selection, user audio drivers, original user media, arbitrary personal directories. Never modify audio system routing merely to make tests pass.

## Private information
A clip directory contains private screen recordings, voice chat, names and perhaps secrets shown on screen. File names, indexes and GG caches may contain private data. Treat all bytes as private. Never send them to GitHub issues/PRs, Codex prompts, remote AI analysis, logs committed to Git, telemetry, or third-party APIs. Prefer local-only redacted aggregate data and synthetic test media. If external build tooling or Codex platform would upload samples, use synthetic data instead.

## Destructive and resource-intensive work
No unattended recursive deletion/renaming, in-place transcoding, cleaning, forced Git history rewrite, auto uninstall, driver change, or copying the ~113 GB archive. Dry-run, forecast output size, check free space, backups, collision policy, and show a clear approval boundary. Recycle Bin is not a true deletion; account for it in disk estimates.

## Recording disclosure and privacy
Game/Discord/monitor capture may include personal messages or video calls. Keep capture source explicitly selectable and visible. When adding always-on secondary/desktop capture, clearly warn about broader privacy footprint and CPU/GPU/VRAM/RAM impact. Never build covert capture, streaming, automatic media uploads, capture of protected playback, or remote monitoring. Record only when user's visible settings opt in.

## Native code
Audit C++/C# struct layout across DLL boundaries, buffer/ownership lifetime and stopped threads, COM apartment lifecycle, race conditions during scene/device switching, GPU resource cleanup, and memory handling during encoder restarts. Add tests for rapid switching, sleep/resume, audio device hot-unplug, video frame starvation and no-sound cases.

## Git safeguards
Fork origin only; upstream fetch-only; new feature branches; preserve GPL credit and notices. Avoid committing native build outputs, `deps/`, video files, logs, large inventories, binaries from third-party downloads, database files containing user media metadata, private paths. Add build output ignore patterns as appropriate. Never force push or change upstream repo.

## Validation reporting
For each phase, state **actual measured/passed** versus expected design, linked files/methods changed, skipped tests, permissions required, performance regression possibility, and explicit rollback. No 'all done' when only static analysis was completed.

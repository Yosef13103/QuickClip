# Agent instructions — QuickClip personalized fork

Read `.ai/README.md`, `.ai/PROJECT_SPEC.md`, `.ai/ROADMAP.md`, `.ai/SAFETY.md`, and `.ai/STATUS.md` before changing anything. Treat these files as project requirements; inspect the actual checked-out source and update assumptions if upstream code differs. Prefer focused changes, reversible migrations, comprehensive tests, and production-ready code over demonstrations.

## Git and upstream
- `origin` must be the owner's GitHub fork; `upstream` must be `https://github.com/swiiftex/QuickClip.git`. Never push to upstream or rewrite upstream history.
- Keep fork `main` clean and synchronizable to `upstream/main`. Place customized work on long-lived `custom/main`, with narrow `feat/*`, `fix/*`, or `audit/*` branches rooted in it. Do not merge custom work into the tracking `main` unless the user changes this policy.
- Before work, report current branch/remotes, working-tree state, upstream revision, build status, test status, available disk, and any assumptions. Do not clobber existing user changes. Never force-push without explicit permission.
- Track progress in `.ai/STATUS.md` and decisions in `.ai/DECISIONS.md`. Each implementation phase should have an incremental commit with tests and a human-readable summary. Prefer upstream-friendly modular contributions.

## Data / execution boundary
- Development code may be modified **inside this fork's workspace**. Do not edit, delete, repair, install into, or reconfigure SteelSeries GG, Sonar, Windows audio devices, Windows registry, drivers, game files, or user clip originals.
- All locations under `C:\Program Files\SteelSeries\GG`, `C:\ProgramData\SteelSeries`, and `E:\Stuff\Personal\Clips` are **read-only investigation sources** unless separately approved for a clearly identified operation. Never perform recursive bulk copies or hashing without assessing the work and available disk.
- `E:\Stuff\Personal\Clips\SteelSeries Moments` is a source archive nested inside the finished-clip library. Exclude it from finished-clip indexing unless explicitly inventoried for migration. Do not delete or overwrite any of the ~113 GB of archive data or ~31.5 GB of finished clips.
- Audio/video/metadata are private. No cloud upload, remote LLM API, telemetry, or logs containing user media/transcripts/credentials; never commit media, credentials, file inventories with private names, or local absolute paths containing personally identifying information.
- Before running any scripts/builds/installers from the repo, inspect them; document their effect and ask for approval if they touch user/global paths. Prefer sandbox/test temp directories and portable debug builds.
- Legacy input analysis and migration are **read-only and dry-run** by default. Export/copy operations must use a separately configured destination, show estimated storage usage, and never overwrite without explicit approval. Destructive cleanup is out of scope until a later separately approved phase.
- Do not implement hidden recording, remote uploading, surreptitious monitoring, or bypass of app/OS protected-content controls. Expose capture state prominently and let the user disable sensitive sources.

## Engineering
- Retain C#/.NET 10/WPF, native C++ recording, libmpv, FFmpeg for now; **do not rewrite in Rust**, Electron, or Tauri merely for aesthetics. Favor safe C++/C# interoperability and measured resource savings.
- Maintain Windows 10 22H2 compatibility; arbitrary **audio endpoint WASAPI loopback** works there, but QuickClip's existing process-loopback capture requires later Windows builds. Don't tie endpoint routing to that feature flag.
- Preserve source audio streams separately through recording, editing, and export. Prefer explicit Windows endpoint IDs, not process names, for the owner's Sonar-based routing. Prevent accidental double recording.
- Prioritize uninterrupted gameplay video when Discord takes focus. Make desktop/Discord capture and simultaneous recording opt-in with explicit RAM/VRAM/GPU/encoder/storage indicators. No surprise recording-target changes.
- Every important change requires automated tests and, where possible, manual Windows/NVIDIA/dual-monitor smoke tests. Never claim success without validation.
- Security review must account for GitHub updater provenance, old release source equivalence, dependency pinning, DLL loading, process execution, optional startup behavior, and separate development profile paths.

## Operating style
- Do not interpret this spec as permission to perform all phases at once. Follow `.ai/ROADMAP.md`; finish and validate Phase 0 before implementation. Continue with independent safe code changes if feasible; checkpoint rather than attempting a risky one-shot rebuild.
- If access to local drives isn't actually available (cloud Codex), state that limitation and implement a local scanner/diagnostic tool for the user to run, rather than inventing scan results.
- Keep options configurable, but provide sensible low-overhead defaults; use progressive disclosure for advanced settings. Log original capture time separately from edited/exported time.

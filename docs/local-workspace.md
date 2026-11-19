# Local QuickClip development workspace

The workspace is a container for Git worktrees, not a checkout. Production source: 1. Production (custom/main). Integration source: 2. Development (integration/development). Active independent worktrees: 3. Branches/<name>. Archive: retired source checkouts, not active writers. main remains the upstream-compatible mirror.

## Local owner workflow

From the workspace root, use PowerShell:
- .\Workspace.ps1 -Action Prepare -Checkout Development
- .\Workspace.ps1 -Action Build -Checkout Development
- .\Workspace.ps1 -Action Test -Checkout personal-update-reliability -Filter 'FullyQualifiedName~UpdaterTests'
- .\Workspace.ps1 -Action Test -Checkout Development -Profile Upstream
- .\Workspace.ps1 -Action Launch -Checkout Development
- .\Workspace.ps1 -Action Watch -Checkout Development
- .\Workspace.ps1 -Action Snapshot

The root entry point calls 2. Development/scripts/workspace.ps1 with the workspace location. The tracked script can also be invoked directly with -WorkspaceRoot. Specify Production, Development, or an active branch folder, not an archived checkout. Create approved new feature/fix worktrees under 3. Branches from an exact verified integration/development SHA; existing unmerged branches must be reconciled non-destructively before integration.

## Shared build inputs

4. Shared contains SDK, NuGet, Dependencies, NativeCache, and Locks. Prepare validates the exact dependency recipe hash and creates an immutable-by-policy deps junction to the pinned store. The existing dependency-provisioning script rejects redirected deps; do not remove, replace or provision through that junction. A different recipe needs a separately verified dependency store, never mutation of this cache. The shared SDK is x64 .NET 10. Use the workspace wrapper, not repeated dependency downloads.

The local native cache key covers native source/header contents, build and dependency recipe scripts, FFmpeg headers/libs, actual selected VS compiler binaries, vcvars, SDK selection and core SDK files. It verifies the cached DLL digest, locks entries, and rechecks native inputs after compilation. Cache reuse is local only; it is not a reproducible-release guarantee. Use -CleanNative for a deliberate rebuild. CI/release packaging retains its independent strict clean dependency and native build path. Keep managed outputs and native output copies per checkout; one writer/build per checkout. Do not run ordinary cleanup through junctions. Only remove verified regenerable bin/obj/publish artifacts; preserve profile data, research and evidence. Direct MSBuild outside the wrapper may compile native code.

## Data and interactive tests

5. Data/Development is a canonical persistent isolated Personal profile for all development/branch launches (even a build from production source used for compatibility testing). QUICKCLIP_PERSONAL_DATA_DIR is process-local. Ordinary builds and automated tests receive unique fixture roots, not the shared profile. The app's isolated-profile mutex/pipe plus the launcher guard prevent simultaneous normal development launches against that profile. Quit the prior build normally before switching. Watch can restart its own build; never use multiple simultaneous watchers. Snapshot copies and verifies the inactive development profile into 5. Data/Snapshots before migration experiments. Do not point development media paths at the actual clip library or ingest protected archives. Test downgrades with generated or copied fixtures and preserved snapshots; do not assume backward migration compatibility.

5. Data/Production documents the installed application's existing Windows profile and real media paths; the production application continues using its supported default environment and real library. No live profile relocated or copied by this migration. Testing installed upgrade/recovery requires a disposable clean Windows user/runner to isolate the installer registration and data. Do not run real installer fixtures against the active production user.

## Integration and evidence

Feature/fix branches target integration/development for normal integration. Only a separately approved, exact tested integration candidate targets custom/main for publication. Do not push, merge, bump versions or publish without the applicable approval. Never write to main/upstream. Windows quality CI must verify both profiles on integration PRs; actual production release workflow remains separate.

Local execution can be performed directly by ChatGPT/Desktop Commander or Codex, depending on scope. One owner per worktree; no parallel writers, no quota bypass or unapproved automatic resumption. Preserve old sessions rather than editing their internal logs/databases; resume using verified new checkout path or a fresh handoff. The workspace migration path map and private evidence live in 7. Evidence; old branch-local checkpoints may still exist. Research remains gated and independent. Record exact SHAs and commands, distinguish focused tests from full/integration/physical acceptance.

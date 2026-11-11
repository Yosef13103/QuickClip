# Agent 3 — P0 Windows CI, quality gates and reproducible acceptance
READ FIRST: .ai/public/P0_SPRINT_CONTRACT.md and .ai/public/QUALITY_GATES.md.
Repository: Yosef13103/QuickClip. Target PR: custom/main. Worktree/branch: chore/p0-ci-evidence. Reasoning: Medium / High.

## Mission
Make development measurable and reviewable without claiming that working source code equals validated recording behavior. The owner already placed QUALITY_GATES.md, TRACKER.json and progress.py under .ai/public; they were committed to custom/main. Preserve their baseline and their gate IDs; do not regenerate, wipe or hand-edit percentages. Their historical baseline is an audit snapshot, not current runtime status.

## Preflight and scopes
Fetch and branch from latest origin/custom/main; inspect worktrees and uncommitted files. Confirm actual .ai/public tracked files, .gitignore allowlist, SDK/native dependency manifests and existing build scripts. Review tests and recent integration report. You own .github/workflows, tests, synthetic fixture harness, quality ledger/docs. Avoid recording-engine and WPF production code owned by Agents 1/2. Flag code changes as separate proposed patches rather than racing their worktrees.

## CI acceptance infrastructure
- Create Windows GitHub Actions workflows on PRs explicitly targeting custom/main and relevant pushes to custom/main. Use managed .NET 10 build and test; provision C++/FFmpeg/native dependencies only through a reproducible, permitted source. Fail visibly on missing prereqs; don't declare a fake green native build. Cache only controlled dependencies; do not commit downloaded binaries or credentials.
- Synthetic, headless, no-screen/no-mic test jobs and stock-profile compatibility; opt-in capture and hardware tests must be SKIPPED with reason, not secretly enabled. Use appropriate timeout, deterministic scratch profile, fixed culture/paths and isolated test fixture media.
- Provide machine-readable test output or JUnit/TRX artifacts, test failure/skipped counts and a CI checkpoint SHA. Only claim validation if the workflow genuinely ran. CI status must be checkable from PR.
- Document native build feasibility and any third-party download/hash/license constraints. Never silently relax strict build requirements.

## Evidence tracker
- Treat .ai/public/TRACKER.json as source of truth. Maintain fixed stable QC IDs and separate implementation vs end-to-end acceptance status. Progress script derives numbers; a compiler success cannot close hardware gate.
- Add a small, auditable validation command if helpful (schema/gate ID unique, legitimate state transitions and nonempty evidence for a passed acceptance gate). If changing tracker format, retain backwards compatibility or migrate deterministically. Do not quietly rebaseline all prior gates to the new commit.
- Record a distinct checkpoint for current HEAD, tests on CI runner, skipped cases and verified manual tests. Distinguish user smoke observations ("Terraria recognized") from a repeatable acceptance suite.
- Expand test matrix only where genuinely necessary for borderless capture, game disable semantics, Discord focus/pop-out, isolated audio and real source identity; preserve quality-gate history rather than fabricating pass states.
- Ensure the currently authorized .ai/public files and safe agent prompts remain tracked without opening the full ignored .ai directory. Audit public docs for secrets/paths before publication.

## Hands-on acceptance plan to hand to owner
Create bounded user-supervised, nonprivate instructions and results template covering:
- Windows 10 19045 yellow border on game capture, monitor DDA, actual saved source, explicit strict no-border failure state.
- Terraria/tModLoader/Marvel Rivals ID, disabled game, game launch/exit, 30 source/focus transitions, alt-tab to Discord pop-out on secondary monitor.
- Game/Desktop/Chat/Mic track isolation with test tones, no duplication and correct export/playback; document endpoint names without uploading private identifiers.
- 100 saves where justified, 60-minute idle/game soak, 20 recovery conditions, honest duration/codec/geometry, protected failure and disk-full behavior; only execute with explicit owner supervision.
- Baseline off versus borderless monitor versus game capture with repeatable graphics settings, fps/frametime percentiles, CPU/GPU Video Encode utilization, memory/VRAM and encoder queue diagnostics, and a reproducible results table.
- No commands in default CI that silently record microphone, personal desktop, or consume private media.

## Git/PR workflow
Document PR format: source branch and SHA, target custom/main, changed QC IDs, exact commands/results, blocked tests, user supervision requirements, impact to upstream main. Verify main remains an untouched upstream tracking branch. You may recommend branch protection but do not claim enforced unless API/UI verifies it. If branch-protection controls unavailable, provide explicit owner UI actions. No PR merges by agents while other worktrees are in flight.

Deliver commits and PR for infrastructure only, update QC-001 with actual evidence, add reproducible QA handoff. List exact workflow run URLs, passes/failures/skips and hardware tests still needed. Do not mark capture/detection P0 gates green just because the relevant agents wrote code. Stop after this milestone.

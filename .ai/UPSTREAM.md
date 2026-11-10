# Git strategy and upstream contributions

## Why fork instead of plain clone
Forks retain upstream association, can synchronize using GitHub's **Sync fork**, and can submit PRs back to the parent. A local plain clone is also valid, but a fork is the natural home for extensive personal changes. `JaredMerritt/QuickClip` is already an example: its multi-mic PR #11 was merged upstream 2026-10-19 and it currently has no unique commits relative to parent.

## Branches
- `origin` = user's own QuickClip fork, `upstream` = `swiiftex/QuickClip`.
- `main`: a clean mirror of upstream/main; do not develop customized changes there.
- `custom/main`: integrated personal product development.
- `feat/endpoint-audio`, `feat/capture-policies`, `feat/clip-library`, `feat/moments-import`, `feat/dual-capture`: incremental branches from custom/main. Cherry-pick isolate reusable code into clean `upstream-pr/*` branches based on recent upstream/main to contribute upstream.
- `main` sync: `git fetch upstream && git switch main && git merge --ff-only upstream/main && git push origin main`. With only clean tracking commits, this is fast-forward. To update custom branch: merge `upstream/main` into `custom/main` with conflict review/testing (or rebase carefully only if private and approved). Avoid force push and silently reverting custom work.

## PR policy
- PR from `feat/*` into fork `custom/main` for internal integration; PR from `user:upstream-pr/feature` to `swiiftex/QuickClip:main` when concise and ready. Do not dump the entire personalized fork upstream.
- Preserve GPL credit/copyright, describe tests/benchmarks and feature flags, and discuss public interest with maintainer. Keep private machine path, GG media, company/game captures, generated library databases and personal identifiers out of PRs.

## One-time setup (from user-created fork)
```powershell
git clone https://github.com/<YOUR_GITHUB_USERNAME>/QuickClip.git
cd QuickClip
git remote add upstream https://github.com/swiiftex/QuickClip.git
git fetch upstream
git switch -c custom/main
# Extract the starter ZIP's AGENTS.md and .ai/ directory into this repository.
git add AGENTS.md .ai
git commit -m "docs: add personal QuickClip agent roadmap"
git push -u origin custom/main
```
If `custom/main` exists or remotes differ, inspect before changing commands. Do not run blind scripts. Always check `git status` and `git remote -v` before working.

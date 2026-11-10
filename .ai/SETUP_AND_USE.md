# How to install this starter bundle in the fork

1. On GitHub, click **Fork** on https://github.com/swiiftex/QuickClip and keep the fork relationship. The fork can sync upstream and submit PRs to original.
2. Clone **your fork** locally on the same Windows PC where GG/Clips live. Codex needs **local workspace access** to inspect `C:\` and `E:\`; a cloud Codex session cannot see them without uploaded data. Don't upload private media.
3. Inside the cloned repo, add upstream and create custom/main:

```powershell
git remote -v
git remote add upstream https://github.com/swiiftex/QuickClip.git
git fetch upstream
git switch -c custom/main
```

4. Extract the starter ZIP's `AGENTS.md` and `.ai` folder into the root of the cloned repo. It contains *no modifications to existing QuickClip source files*.
5. Commit instructions on custom/main:

```powershell
git status
git add AGENTS.md .ai
git commit -m "docs: initialize personalized QuickClip development roadmap"
git push -u origin custom/main
```

6. Open **local Codex** inside the repo (project rooted at `custom/main`), choose High reasoning for normal implementation; Extra High only where available and worthwhile for architecture and security review. Paste the content of `.ai/CODEX_START.md` or simply tell Codex `Read AGENTS.md and .ai/CODEX_START.md, then execute them in phases.`
7. Grant permission thoughtfully: read local media/SteelSeries paths only when needed and preferably through redacted local diagnostic scripts. Approve writes only to repo/dev/test locations. Never blindly approve deletes on `E:\Stuff\Personal\Clips` or edits in installed GG.

## Keeping upstream updated
From a clean worktree:

```powershell
git fetch upstream
git switch main
git merge --ff-only upstream/main
git push origin main
git switch custom/main
git merge upstream/main
# resolve conflicts, run tests, commit reviewed merge if needed
git push origin custom/main
```

Do not run the merge while Codex has uncommitted edits. If custom changes conflict, resolve rather than discard. Do not force push. For an upstream contribution, create a clean feature branch from upstream/main and transplant only the reusable feature, then open PR against swiiftex/QuickClip.

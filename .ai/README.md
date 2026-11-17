# QuickClip development system — post-v1.1.0

**Active:** Wave 2, discovery first. **Baseline verified 2026-11-15:** `custom/main` and `personal-v1.1.0` resolve to `6de4ad684f4268b685c630d639977c439bf557ed`. PR #74 merged; quality run 43724580404 both profiles passed; release run 43724580447 strict checks, package, publish jobs succeeded. Default branch `custom/main`; no open PRs returned in the design-time query. Recheck live local/remote state; none of this authorizes a merge. Release assets are recorded in the handoff; actual install and physical tests not independently proven. Current local branch, SDK, working tree and production upgrade unknown.

## Read by task, not by directory
- Every session: root `AGENTS.md`, this index, selected `TASKS.json` entry (filter by ID), dependency statuses. Read referenced sections; do not load all files or reread history each turn.
- Discovery: `DISCOVERY.md` (selection, incremental sources, provider research); for regression audit read `REGRESSIONS.md` only relevant rows. For all tasks: targeted `PRODUCT.md` and `QUALITY.md` sections. `RESEARCH.md` only for research tasks; `WORKFLOW.md` for dispatch/recovery.
- `TASKS.json` is the **single active ledger**. Requirements live in referenced contracts; future work lives in `../docs/post-v1-roadmap.md`. Activate W3/W4 by replacing active board only after owner approval and wave reconciliation. No need to regenerate root instructions.
- `prompts/01-discovery.md` begins the first milestone when owner runs it; `02-discovery-verify.md` continues it; `03-task.md` runs a bounded subsequent task; `04-close-wave.md` integrates/verifies without releasing.
- `../docs/` current source-specific docs remain technical references, not new dispatch instructions. Check `docs/development.md`, actual scripts/tests and nearest AGENTS/override files. Existing `.codex/` roles/config may contain old P0 instructions; inspect and avoid stale roles, do not overwrite unrelated settings. Native subagents optional.

For an individual task in PowerShell, avoid loading the entire board into agent context:

```powershell
(Get-Content .ai/TASKS.json -Raw | ConvertFrom-Json).tasks | Where-Object id -eq 'W2-G1' | ConvertTo-Json -Depth 12
```

## Only one owner of work at a time
Parent is planner, normally implementer, and verifier. Read-only investigators answer narrow questions with file/line and limitations; independent reviewer examines actual diff and negative cases. No second writer in same checkout. ChatGPT handles public research, GitHub read-only review and owner-facing hardware plans; Codex performs local code/build/tests; owner authorizes actual capture/installation and release. No duplicate implementation. See WORKFLOW.

## State model
Board statuses separate implementation from local/CI/hardware acceptance. `not_started`, `in_progress`, `blocked`, `reviewed`, `implemented`, `not_applicable` are progress states; checks are separate `pending`, `pass`, `fail`, `blocked`, `not_applicable`. `implemented` alone never equals accepted or shipped. Task dependencies specify required gate; code-ready tasks may proceed in one approved candidate while hardware is pending, but release cannot bypass required hardware checks. All gates tied to candidate SHA, invalidated on relevant edits. Record why a gate is not applicable. No fabricated completion percentages.

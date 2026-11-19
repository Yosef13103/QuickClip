# QuickClip development system — post-v1.1.0

**Active:** Post-v1.2.0 maintenance and remaining Wave 2. **Published baseline 2026-11-19:** custom/main and personal-v1.2.0 = 050e12ca14445d58b6df7856da9627a12afeb477; main mirrors upstream. Wave 1 and Wave 2 A/D shipped. Updater reliability first, then compatibility/usability, B overlapping replay consolidation (unfinished local implementation), C full theme designer, E gallery organization. Dynamic ring resizing deferred; Wave 3 gated capture research remains separate. Local development integration uses integration/development; reverify current refs/evidence and read docs/local-workspace.md. Historical ledger gates do not prove current release acceptance. No release or production operation is authorized by this index.

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
Parent is planner, normally implementer, and verifier. Read-only investigators answer narrow questions with file/line and limitations; independent reviewer examines actual diff and negative cases. No second writer in same checkout. ChatGPT/Desktop Commander or Codex performs local investigation/code/build/tests according to task scope and access; use shared workspace inputs and isolated worktrees per `../docs/local-workspace.md`. Owner authorizes actual capture/installation and release. No duplicate implementation. See WORKFLOW.

## State model
Board statuses separate implementation from local/CI/hardware acceptance. `not_started`, `in_progress`, `blocked`, `reviewed`, `implemented`, `not_applicable` are progress states; checks are separate `pending`, `pass`, `fail`, `blocked`, `not_applicable`. `implemented` alone never equals accepted or shipped. Task dependencies specify required gate; code-ready tasks may proceed in one approved candidate while hardware is pending, but release cannot bypass required hardware checks. All gates tied to candidate SHA, invalidated on relevant edits. Record why a gate is not applicable. No fabricated completion percentages.

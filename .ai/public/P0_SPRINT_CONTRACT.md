# QuickClip Personal — P0 capture, games and quality contract
Status: proposed sprint contract. Base at drafting: custom/main 0162173d8743904a63f96103d2c5621acff20d4a. Read the actual latest origin/custom/main before creating a worktree.

## Objective and stop rule
Finish the existing product workflow before introducing more features: game launch → correct automatic identity → authorized, borderless recording target → correct game/chat/microphone audio → one Save Clip hotkey → valid, fast saved video → reliable Gallery/Edit/Share. No new automatic highlights, full-session capture, multiple duration hotkeys, bookmark hotkeys, theme work or new overlay effects in this sprint.

Non-negotiable user requirement: a Windows 10 yellow Windows Graphics Capture (WGC) border must NEVER appear due to automatic selection in the new compliant capture policy. Native resolution is not a capture backend. On Windows 10 build 19045, WGC game-window capture produces an OS border; do not claim otherwise. If borderless game-only capture is unavailable, be truthful and let the user choose a fully described borderless monitor capture (Desktop Duplication) or leave game-only capture unavailable. Do NOT silently substitute desktop capture for a strict game-only selection. No anti-cheat bypasses, unsigned driver hacks, security setting changes or unsupported NvFBC.

## User experience: Games page
Use a separate Games section, tab or subpage, not a giant expansion of General settings. A horizontal Settings navigation such as Recording | Audio | Games | Library | General is acceptable if accessible and responsive; avoid a complete style rewrite.

Games list patterned loosely on the user's SteelSeries screenshot:
- Clear "Automatically scan for games" opt-in toggle, Scan now, Add game, visible game count and search/filter.
- Row shows friendly game name, status (Capture enabled / Disabled / Needs review), source (Steam/manual/approved folder), and an unambiguous enable/disable action. Open row details for supported per-game overrides and detected executable paths.
- Disable means "do not use this installation as an automatic game capture target"; explicitly explain that an independently selected monitor recording can still show any game visible on that monitor. Disabled games never auto-trigger a game-specific recording profile.
- First-class Terraria, tModLoader and Marvel Rivals identities must be verified, not special-cased as magic names. Manual registrations and exclusion settings persist through restart.
- No uncontrolled whole-disk scans or unapproved directory access. Rate-limit and cancel background scanning; preserve old results on errors. Remove a manual entry separately from disabling an installed game.
- Per-game settings exist only if the effect genuinely reaches the recorder, or stay hidden/unavailable with a specific tracked integration dependency. Inherited defaults are visually distinct from explicit overrides; no fake controls.

## Discord and other windows
Changing focus to Discord or its pop-out on another monitor MUST NOT replace the verified active game with Discord or unexpectedly switch capture to a different monitor. Discord voice/stream output and microphone routing should remain independently selectable and measurably isolated from game audio. "Support Discord pop-out" for P0 means recognizing it as a non-game companion, keeping the original game session, and validating audio/focus transitions. Capturing Discord pop-out VIDEO as an extra simultaneous video source is a separate, explicit-opt-in feature: not silently implemented here and never claimed complete from audio tests.

## Ownership and shared integration boundary
Agent 1: capture backend capability, no-yellow-border policy, native recorder, capture-source transitions, tested source/encoder behavior. Owns Recorder/CaptureEngine and native C++; no Games WPF layout.
Agent 2: game discovery service usage, GameDetector/catalog identity, Games UI, per-game rule persistence, Discord window identity and audio routing. Owns Discovery, GameDetector, game-facing settings UI. Do NOT edit Recorder/CaptureEngine or native capture files. Specify an adapter/contract consumed at later integration for policies.
Agent 3: Windows CI and the quality gate ledger under .ai/public, reproducible synthetic tests, pull request documentation, acceptance scripts; avoid editing production capture or game UI code.
If two agents need the same shared file, name the required minimal patch explicitly in their PR instead of racing on the file. Reserve integration of Agent 1 and Agent 2's cross-boundary adapter for a follow-up reviewed integration PR. Neither can claim that specific gate passed before the merged app demonstrates it.

## Canonical quality evidence
Use .ai/public/QUALITY_GATES.md, TRACKER.json and progress.py as the canonical ledger. Prioritize QC-001, QC-003, QC-004, QC-005, then QC-002 and QC-007 in evidence-based order. Do not manually inflate percentages or edit a gate to pass on the strength of a proposed implementation. Link each changed gate to source commit, exact tests, platform and reproducible verification output; synthetic and real-hardware evidence are different. Keep the source-derived baseline immutable and record newer checkpoints separately. Track observed (not fully accepted) user smoke tests for Terraria, tModLoader and Marvel Rivals.

## Tests and safety
Keep stock upstream main untouched; only custom/main is the intended integration target. Start feature branches from the same freshly fetched custom/main SHA; do not merge into custom/main individually. Commit/push each feature branch, open PR explicitly targeting custom/main, and preserve local profiles and existing clip media. Prefer synthetic test applications and fake process/window observations. Live capture/audio/anti-cheat games require user-supervised opt-in; never run unattended screen capture. Do not change Windows registry, disable anti-cheat or modify a protected game. Failure is explicit and safe, not automatic capture of the entire desktop. Do not claim real runtime verification unless it was actually performed.

## Acceptance principle
A green build is necessary, not sufficient. Required: no automatic WGC border on Win10 19045, correct actual saved source when requested, stable identity through alt-tab, no unwanted Discord pop-out capture, properly separated audio, successful clip-save receipt and playable output, consistent failure feedback, and measured rather than guessed recording overhead. If game-only borderless cannot be delivered safely, mark it BLOCKED and keep an explicit Desktop Duplication fallback opt-in. This is a legitimate partial result, not license to call the gate complete.

## GitHub workflow
Each agent uses its own worktree and branch; run git status/branch/worktree preflight; do not force reset/push. Upload all meaningful changes and sanitized handoff notes; create a PR with gate IDs, tests, results and unresolved risks. Never target default branch main. Do not auto-merge while other worktrees are in progress. After all PRs are available, the integration owner reviews conflicts, tests combined behavior and merges in order. GitHub-only review cannot establish live Windows gameplay or Sonar audio behavior.

## User-approved refinements (2026-11-11)
**Platform-aware capture:** See `CAPTURE_PLATFORM_POLICY.md`. A supported and *verified authorized* Windows 11 no-border WGC game-only path should be chosen by default when available; a Windows 10 19045 user should receive an explanation of the OS limitation and an explicit opt-in to border-free **whole selected display** Desktop Duplication. No universal FPS claim about DDA vs WGC; measure before recommending for performance. Never imply upgrading the Windows SDK fixes a runtime OS limitation. A current portable app without correct capability/consent must not claim it can suppress the WGC border.

**Launcher-aware game discovery:** See `GAME_DISCOVERY_SCOPE.md`. Advertise and support user-selected automatic scans of recognized launcher installation metadata and their libraries, with a visible default launcher list, source badges, Scan Now and Cancel. Steam support is already partially implemented; Epic/Battle.net/EA/Ubisoft/GOG/Riot/Xbox need separately verified adapters rather than names in a fake dropdown. User can add a custom root such as `D:/FreeWare/` and exact EXEs; scoped patterns require bounded scanning, preview and persistence before exposure. Never treat arbitrary `Update.exe` as Discord; an explicit user-authorized Discord capture target is an **application** source, not game auto-detection. Simultaneous Discord pop-out *video* remains a separate later feature.

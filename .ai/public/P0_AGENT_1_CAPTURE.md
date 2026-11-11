# Agent 1 — P0 yellow-border-free capture and source truth
READ FIRST: .ai/public/P0_SPRINT_CONTRACT.md and .ai/public/QUALITY_GATES.md.
Repository: Yosef13103/QuickClip. Target PR: custom/main. Worktree/branch: fix/p0-borderless-capture. Reasoning: High / Extra High.

## Mission
Stop QuickClip automatically showing the Windows 10 WGC yellow capture border when detecting a game, without misleading the user about what is actually recorded. Do not confuse "Native" output resolution with a capture backend. Preserve all existing stable replay, encoder, separate-audio and editor workflows.

## Mandatory first steps
- Fetch origin, inspect git worktree list and status, and create a clean feature branch at current origin/custom/main. Do not edit someone else's worktree or overwrite user data.
- Inspect Recorder.UpdateCaptureWindow, Recorder.StartCore/UpdateCapture, CaptureEngine, native Engine and capture FFmpeg graph. Document each source path and when it is chosen. Inspect relevant docs/capture-frame-pacing.md and public Windows Graphics Capture/OBS capture reference behavior.
- Determine when automatic game detection triggers WGC in Win10 build 19045. Record source identity, backend and output dimensions separately.
- Evaluate safe, maintainable game-only borderless methods, including whether established OBS-style capture integration is feasible with distribution/licensing, driver, DirectX version and anti-cheat compatibility. Do not assume a public one-line GPU API exists. Compare candidate methods on measurable CPU/GPU cost and failure mode.

## Scope, constraints and expected outcome
- Prefer stable supported capture paths. No anti-cheat bypass, protected-process injection experiments, security disabling, patching drivers, unsupported NvFBC or capture-source misrepresentation.
- Introduce a source/capability policy API that can express: (a) game-window-only, borderless requested, (b) monitor only explicitly selected, (c) automatic only with declared fallback and user consent, (d) optional legacy WGC with prominent border disclosure if still supported. The user prioritizes no border; automatic must not silently activate WGC on Windows 10.
- If game-only borderless cannot be implemented safely, do NOT claim the goal achieved: record a clear unavailable/blocked outcome and supply a precise integration plan. Offer explicit Desktop Duplication monitor mode for users who accept recording visible windows. Never fall back to it invisibly when game-only was selected.
- On game loss/alt-tab, preserve valid game source/identity where safe, and stop or require user-authorized fallback if no viable capture target remains. No surprise desktop, Discord pop-out or other window capture.
- Preserve native 3440x1440 and 16:9 center-crop behavior as actually implemented; no implicit upscale; keep HEVC/NVENC P2 and separate audio.
- Do not invent new multiple-duration hotkeys or live marker UI. Preserve legacy data and existing runtime APIs unless a small backwards-compatible adjustment is justified.
- Avoid modifying SettingsView, Games UI, GameDetector or Discovery files owned by Agent 2. Provide minimal proposed bridge interface for Agent 2's verified game identity and per-game effective policy.
- Record a truthful source descriptor for every capture epoch/clip receipt where safe: game ID/name (or Desktop + display identifier), capture backend, actual output pixels, FPS and duration. Preserve compatibility with existing save receipt consumers. A changing source inside a buffer needs segmented or explicitly mixed identity; do not claim entire clip is one game if provenance cannot be proven.
- Do NOT implement new notification visuals; only supply verified metadata contract for the future concise notification banner.

## Required checks
Unit/integration tests with synthetic windows and fake capabilities:
- Win10 border-required WGC rejected by borderless-only policy, including automatic selection and fallback.
- Monitor capture explicitly selected and truthfully labeled Desktop, not game-only.
- Invalid/lost HWND, game exit, background focus, separate-monitor Discord pop-out and source restart.
- Missing safe backend fails clearly, preserves previous media and does not start unapproved screen capture.
- Saving after capture-source transitions retains or declares mixed source correctly.
- No regression to replay save path, codec, timestamping and audio channel ordering.

Run strict relevant native/managed Release builds and synthetic tests. Document SUPERVISED acceptance plan for Terraria, tModLoader, Marvel Rivals, alt-tab, borderless/exclusive fullscreen, game window, and Saved MP4 identity. No unattended desktop/audio capture and no unapproved DLL injection.

## Tracker and delivery
Target QC-004 and related QC-002/QC-007 only when evidenced. Do not claim acceptance passed merely from mock tests or source inspection. Update source-specific handoff docs with a go/no-go decision matrix and blocked cases. Commit in meaningful chunks, push branch, open PR to custom/main; do NOT merge it. Final report must list tested source paths, actual safety properties, branch SHA, reproducible test commands, limitations, and explicit Agent-2 integration patch. Stop after the bounded P0 work: no extra visual themes, GameSense or new feature classes.

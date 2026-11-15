# QuickClip Personal — post-v1 roadmap (four waves)

Reconciled 2026-11-15 from v1.1.0 release, owner acceptance and design-time GitHub verification. This is scope and sequencing, not a dispatch or evidence of task completion. Active ledger `.ai/TASKS.json` owns implementation progress; root `AGENTS.md` and `.ai/PRODUCT.md` own contracts.

## Wave 1 — released, not an active implementation queue
Personal v1.1.0 was published from `custom/main` at `personal-v1.1.0` (`6de4ad684f4268b685c630d639977c439bf557ed`) after [PR #74](https://github.com/Yosef13103/QuickClip/pull/74) merged. [Windows quality](https://github.com/Yosef13103/QuickClip/actions/runs/43724580404) and the [release workflow](https://github.com/Yosef13103/QuickClip/actions/runs/43724580447) succeeded. Shipped: WPF minimize/restore/editor lifetime, themes and navigation, explicit manual display switching and per-monitor exclusions, storage/library settings, capture state badges, improved notifications with separate image mode/built-in GIF and exact timing, opt-in timing refresh, Personal stable-only verified downloads. See [release notes](releases/personal-v1.1.0.md), [frontend history](post-v1-wave1-frontend.md), [notification contract](notification-overlay-redesign.md).

The owner accepted the final Wave 1 design with a 2.59-second built-in GIF loop after restore, custom notification sizes in DIP, a two-physical-pixel banner margin, and verified downloads from the stable Personal feed. Existing automatic-check opt-outs remain respected.

Remaining verification, not blanket reimplementation: actual v1.0→1.1 in-place installation, final physical overlay alignment/mixed DPI and rights for owner-supplied GIF. Updater downloads verified assets and opens manual install; not automatic in-place installation. No nightly release. Existing clips, settings and opt-outs preserved by contract; actual production upgrade unverified.

## Wave 2 — active: game discovery first, then reliability and infrastructure
- W2-A targeted audit and minimal baseline in service of discovery; do not require full diagnostics/updater before useful game work.
- W2-G1/G2 simplify explicit game eligibility and fix actual multi-game source selection/recovery; preserve authorization and full-monitor source truth. W2-G3 incremental libraries, truthful counts, provenance and cache. W2-G4/G5 research and approved selective providers; keep Steam/Epic, prioritize GOG, evaluate Xbox/EA/Origin and other launchers. W2-G6 review and actual owner discovery validation. Detailed contract in `.ai/DISCOVERY.md`.
- W2-R1/R2 reconcile historical defects and fix reproduced reliability bugs only. W2-P matched resource baseline (idle/armed/game/save/editor/scan, GPU and FPS, audio and duration) before speculative optimization.
- W2-D native live replay duration resizing: grow retains history/session, shrink immediately caps/evicts; concurrent saves/keyframes/audio/markers/caps. W2-L bounded opt-in categorical diagnostics/timing, cheap off path, essential errors retained, benchmark. W2-U safe Personal installation/staging/rollback: reuse shipped stable-feed verification; explicit approval, no interruption, safe extraction/exit/recovery/profile isolation. No nightlies.
- W2-Z combined candidate strict checks, owner hardware and packaging acceptance; separate approval before release. Version depends on actual accepted scope. See active board for acceptance and dependencies. Do not require all future launcher research to finish basic game eligibility.

## Wave 3 — Capture architecture and companion applications (dormant, not dispatched)

Activation requires owner-approved W2 reconciliation/closed scope and real baseline. First research gate W3-RESEARCH; no production hook until scope, license, permissions, prototype and fallback are supported. Companion activity research can run independently; serial native writes. GPU optimization follows measured evidence; integrate W3 before W4.

### W3-RESEARCH — Research-first gate
- Before major capture implementation document actual current code, primary sources/existing solutions, license/capability/support evidence, costs, scoped prototype, fallback and tests. Can run read-only research in parallel, serialize native writes. Do not require a full architecture rewrite if a modular acquisition path works. If feasibility/permission/licensing blocked, report rather than force a feature.

### W3-CAPTURE — Real isolated game capture feasibility and implementation
- Current primary game-aware capture is entire authorized display. Target alternate acquisition backend feeds GPU textures into existing processing/NVENC/replay ring, rather than wholesale rewrite.
- Research OBS win-capture/game-capture.c, graphics-hook/d3d11-capture.cpp, window-capture.c, libobs. Compare selected reusable components, libobs, official OBS sidecar benchmark (websocket replay APIs) and existing WGC/DDA. Custom hooks last resort. Check GPL2-only vs GPL2-or-later compatibility with GPL3 and dependency obligations, Windows 10, architecture/bitness/IPC/shared textures, D3D9/10/11/12/OpenGL/Vulkan support, signing, compatibility and failure recovery.
- PrintWindow is synchronous CPU-accessible bitmap + copy/upload, unreliable with GPU/minimized surfaces; experiment only, not default fast capture. WGC border APIs unavailable on Win10 19045; new SDK doesn't solve. OBS game capture isn't Discord/Chrome/Firefox window capture. Anti-cheat compatibility not universal; OBS signing updates and restricted games don't guarantee any fork hook accepted. No bypass or injection into protected processes.
- Goal: game frames only even when Discord covers it, if the game keeps rendering. Minimized game may stop rendering; honest supported behavior, explicit fallback consent, no unverified source claims.

### W3-COMPANION — Companion applications and Discord popouts
- Separate companions settings/identity from games. Application identity, window identity, activity signals, source eligibility and priority distinct providers. Game first sticky across alt-tab; when no eligible game, explicitly authorized active companion (Discord calls/streams/popouts priority), then standby.
- Research Discord child/popout window identities, Zoom meetings, Spotify/Spotifast media, Chrome media/call activity; use reliable OS/app signals where feasible, don't pretend process existence means call/media activity. No generic Update.exe capture. User controls priority and opt-in capture.
- Independent companion source is not simultaneous gameplay+Discord video composite; latter needs additional authorized source/composition/sync and is future work. No unconsented call capture. Preserve Game/Chat/Mic audio and hybrid export.

### W3-PERF — GPU-first capture/processing optimization
- Audit D3D11 texture sharing/pools, copy and fence/sync, crop/scale/color convert, encoder input surfaces, pacing/static frames, preview cost. Avoid CPU readback unless bounded fallback. Measure both GPU engines and actual FPS, CPU, RAM, VRAM and output correctness against W2 baseline. Avoid changes that optimize one workload while regressing single-monitor default.

## Wave 4 — Multi-display recording and final integration (dormant, not dispatched)

Activation after W3 integrated acquisition/priority acceptance and explicit owner approval. Research capabilities and geometry before experimental compositor code; W4-CLOSE depends on W4-COMPOSE and all accepted earlier scope.

### W4-COMPOSE — Optional experimental selected multi-monitor composition
- Explicit selected monitors, physical topology (negative origin, vertical/portrait, rotations, mixed DPI). Native bounding rectangle or configured up/downscale, crop and accurate live preview.
- DDA source per display → D3D11 GPU compositor → one NVENC encoder → existing replay ring + one set audio tracks. No duplicate audio or one encoder per display by default. Query H264/HEVC/AV1 hardware caps, max width/height, alignment, output format and realistic FPS. Offer supported scaled outputs rather than fail/silent crops. Consider LastPresentTime/dirty region and texture reuse for static sources based on actual API behavior.
- Handle disconnect/reconnect and permission changes, stable source identity, no new display without grant. Experimental opt-in; no cost to ordinary single-display use when off. Benchmark with real monitors.

### W4-CLOSE — Integration, long-running stress and release
- All capture types + dynamic replay + hybrid audio + companion priorities + diagnostics + editor/gallery + updater and all themes/scales coexist. Long runs, saves, switches, disconnect, sleep/resume, CPU load, large libraries, memory/handle/VRAM leak checks. Strict Windows CI Personal and Upstream, real owner hardware and installer tests, compare W2 baseline.
- Document measured improvements, limitations, dependency/license provenance and release notes; explicit owner acceptance before production merge/release. No automatic release of incomplete experimental features. Version TBD based on scope.

## Shared release rules
Maintain Win10/11, both strict Windows profiles, GPU-first low overhead, actual source permission/identity, hybrid audio, user media and settings, GPL/dependency obligations. Owner-supervised hardware tests and exact candidate evidence required; no speculative rewriting, no protected content bypass or unconsented sources. Preserve upstream mirror and fork relationship. No automatic stable merge/release/nightly publication. Technical docs and verified current source trump historical dispatch snapshots. Explicit owner acceptance of a feature is distinct from CI, merge and release.

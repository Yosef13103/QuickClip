# Wave 2 full acceptance branch

Integrated pinned heads #78 f73b57ea4149896a9174c040c3b8d718ac1d0ff0 (includes #77, #75/#76 and #79), #81 2f291929a04170c50bd38cd1e81298e3cf606f5c, #82 46e277f97b9ce106420a06a629a56f9881f44553, and #80 2cde8f6008997675f9eb4b65c3e9ddcc03271fa3. Three-way base f7016d2. Overlap reconciliation preserves microphone sample deadline/ABI, logging refactor, frontend source authorization and updater handoff/guards. Diagnostics viewer mounted, timing sample and scoped events wired. Prior tests apply to individual heads only. No production merge, version change or release. W2-D deferred explicitly.

Verify exact combined Windows quality + installer smoke; owner preview capture and display revocation, game switching, physical mic/HUD exclusion, theme restart, fresh setup, updater recovery on disposable real non-system install; benchmark original v0.7.1 vs released v1.1.0 vs exact combined candidate on controlled settings. See owner-field-polish, diagnostics, wave2-performance-protocol. Updater development profile cannot exercise registered production install handoff; do not update real installation to test. Original upstream requires separate Windows user for settings/log isolation.

## Second owner acceptance integration

PR #84 `8e0a1336ffe9cd88778e827588338120179b01b8`: independent notification banner/image exclusion options; transparent affinity first and opaque fallback. CI 43806841932 passed. Physical DDA exclusion and appearance remain pending.

PR #85 `63e1db77e9d2437cd84816b83ac50ab1431fad8b`: SQLite-backed persistent non-destructive edit sessions, metadata autosave, inline display-title renaming, trim-aware muted gallery hover and reset. External added sounds remain session-only; classic player shows full source. CI 43810103745 passed. Actual upgraded-library persistence and owner flow remain pending.

PR #86 `8ef491f1dfc622da72998b2c06f0bfaaac4e5d24`: completed-install admission during scan, scoped alternate binaries, manual-exact precedence over inferred launcher evidence, one virtualized Games scroll surface and stable index on progress-only updates. CI 43812254014 passed. Owner osu!lazer, 007, Simpsons Hit & Run, display exclusion and master-off tests remain pending.

Only overlapping file was .ai/TASKS.json; retained both lanes' evidence. All three exact heads passed their own checks; combined CI and real hardware remain distinct. No production merge or release. OBS prototype is independent and not part of this integration.

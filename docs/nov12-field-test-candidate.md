# November 12 field-test candidate

This draft targets `integration/p0-capture-games` at `0c7719410a045a7a28c7a37d1d8f14d6cc045ef7` ([PR #7](https://github.com/Yosef13103/QuickClip/pull/7)). It combines the Windows CI trigger and acceptance plan ([#10](https://github.com/Yosef13103/QuickClip/pull/10), `5ca7532`), the Editor missing-source fix ([#11](https://github.com/Yosef13103/QuickClip/pull/11), `0af1933`), discovery and bounded continuation ([#12](https://github.com/Yosef13103/QuickClip/pull/12), `f0c3af8`), and the armed recording lifecycle ([#13](https://github.com/Yosef13103/QuickClip/pull/13), `840fdbf`). All merges were conflict-free.

The recorder adapter in `c85db64` requires a verified catalog target and revalidates a retained observation against the current window owner and catalog rules. The Personal app host starts the same discovery coordinator used by Games Settings and cancels it during shutdown. Editor source checks run at the deletion and restore boundaries and around export.

## Local validation

Code checkpoint `0c0356bbebac671954179991c085eaf2c0f70b29`, Windows 10 build 19045, .NET SDK 10.0.403, x64 Release. Strict builds for both profiles succeeded with zero warnings or errors and verified the native capture DLL. The following results came from the test commands in [combined acceptance](p0-field-test-acceptance.md), with cached dependencies and `-p:NuGetAudit=false` on restore.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Personal | 460 | 0 | 18 |
| Upstream compatibility filter | 27 | 0 | 14 |

Fixtures cover discovery without opening the Games view, pause and resume, opt-out, verified game retention through companion focus, idle arming and display consent, and Editor delete, restore and export identity checks. Capture, real Recycle Bin and symlink opt-ins were disabled. Live gameplay, Steam installations, Sonar, replay and Windows 11 consent still require supervised checks. FT-11 endpoint diagnostics and FT-16 Gallery are tracked separately. No acceptance gates were advanced.

The candidate can be left unmerged while the source work proceeds. The two integration commits are independently reviewable and the parent merge commits preserve source history.


## Updated draft with PRs #14 through #17

The four merges preserved each source history and completed without conflicts.

| PR | Source head | Integrated work |
| --- | --- | --- |
| [#14](https://github.com/Yosef13103/QuickClip/pull/14) | `9a189155b41d1e4e68fec10c8bc57f3704949271` | WASAPI endpoint flow and packet diagnostics |
| [#15](https://github.com/Yosef13103/QuickClip/pull/15) | `e367d62fef5630dbd6fcd773afb9f3c16614e914` | Gallery day feed, selection and Share control |
| [#16](https://github.com/Yosef13103/QuickClip/pull/16) | `98c4732e2a8c1dfb4495c9461b76ff7ea5579cfc` | Bounded passive game window observation |
| [#17](https://github.com/Yosef13103/QuickClip/pull/17) | `c59151e5ca402f4064f1186e0c8eb61b49d94dd1` | Tray and hotkey command routing |

The merged native start and rebuild paths still verify the authorized physical display identity. The endpoint status export remains additive. The Personal armed tick uses verified passive observation, keeps an eligible active owner through focus changes and revalidates ownership and catalog rules at graph start. Game display authorization and the manual session's allowed displays remain independent. A master off transition expires manual approval, including when a pending confirmation or restart crosses a quick off/on toggle.

Tested code `47e8e239ad759432e103d111878847c549a03c33` on Windows 10 build 19045 with SDK 10.0.403. Both strict Release builds and the strict Personal Debug build passed with zero warnings or errors. The combined Release suites used the same cache, filter and opt-ins described above.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Personal | 479 | 0 | 18 |
| Upstream compatibility filter | 27 | 0 | 14 |

The offscreen Debug Gallery smoke used 120 generated clips across 12 days and a scratch profile. It realized 7 of 72 rows initially, returned one search match with selection preserved and passed mixed and multiple day selection checks. Capture was disabled and no personal media was used.

Remaining work includes the Audio picker UI, additional P1 UI, actual replay duration measurement, Epic discovery and supervised Windows display consent, physical hotkeys, gameplay and Sonar testing. The draft targets PR #7's integration branch. No acceptance gates were advanced.


## Editor controls, Epic discovery and replay-duration evidence

The candidate merges [PR #19](https://github.com/Yosef13103/QuickClip/pull/19) at `711ed278f54868d2764459a79212db5a769357f8` and [PR #20](https://github.com/Yosef13103/QuickClip/pull/20) at `f4fd3f921e057a25f43f62251d8e2b422eab3c45`. Both merges completed without conflicts. Epic entries remain in Needs review until explicitly classified as Game. The recorder, its consent handling, and the native capture engine have the same source as the prior candidate.

For the FT-07/14 owner, PR #19 changes `GalleryView.xaml` at `DeleteSelectedButton`, replacing the view-local delete style with `DangerButtonStyle` from `Themes/Styles.xaml`. In `ModalDialog.cs`, it routes `ConfirmDelete` through `ConfirmAction` and applies the shared danger style to destructive primary actions while keeping the safe default. Gallery selection behavior, `GalleryView.xaml.cs`, and the Settings and Library changes remain in their existing workstreams. PR #20 also adds the Epic source controls and status to `GamesSettingsView.xaml` and its code-behind.

[PR #21 at e2432febe57f41cad8f281a23256dfebf61cf70f](https://github.com/Yosef13103/QuickClip/pull/21) remains evidence-only. Its generated fixture reported 60.416667 s and 60.616667 s of video for 60 s requests at two GOP phases. The proposed test export reads an input path and writes an output path in the production native DLL. It was left out of the candidate to preserve that runtime boundary. Production duration policy was untouched. [Measurement report](https://github.com/Yosef13103/QuickClip/blob/e2432febe57f41cad8f281a23256dfebf61cf70f/docs/p0-replay-duration.md).

### Validation

Code tested at `40925902cb5c4e8dd15eb04f6093f4f0998827d9` on Windows 10 build 19045 with SDK 10.0.403. Strict Personal and Upstream Release builds and Personal Debug passed with zero warnings or errors. The Release suites used the same isolated profile, cached restore, compatibility filter and opt-ins described above.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Personal | 493 | 0 | 18 |
| Upstream compatibility filter | 27 | 0 | 14 |

The offscreen Editor smoke used 120 generated three-track clips across 12 days. The Gallery realized 7 of 72 rows at first paint, found one search match and passed day selection. The Editor retained the clip, playhead and trim across mode changes, restored the accordion layout, and rendered the compact and taller track layouts. No screen or audio source was captured and no personal media was used.

FT-07/14 Settings and Library changes, broader P1 Games, Audio and Hotkeys UI, precise replay duration, and supervised Windows, real launcher, mixed-DPI, gameplay and Sonar checks remain pending. No acceptance gates were advanced.


## Settings, retained recording folders and Games presentation

[PR #22](https://github.com/Yosef13103/QuickClip/pull/22) at `a4a8deb39aff78f25ffd2f1eb4e40983fa9f17b6` and [PR #23](https://github.com/Yosef13103/QuickClip/pull/23) at `2677c2da8d4e5c4e4f2dd09eed62dceb323244e5` merged without conflicts. The merge retains FT-16 day selection and visible batch scope, shared destructive styles and the master-off consent fix in `47e8e23`. New saves still use `ClipsFolder`. Additional sources expand Gallery visibility and leave media in place. The Games monitor toggle uses the existing automatic targeting override. Recorder lifecycle, native capture and production replay duration policy remain at the prior candidate.

Code tested at `41ea84d7494d05182c8535a9b08ad922b33ae59c` on Windows 10 build 19045 with SDK 10.0.403. Strict Personal and Upstream Release and Personal Debug builds passed with zero warnings and errors. The local restore used the pinned cache and `-p:NuGetAudit=false`. The focused pass at the #22 merge `61b5477` returned 39 passed and one reparse-point opt-in skip.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Personal | 501 | 0 | 18 |
| Upstream compatibility filter | 27 | 0 | 14 |

The destination-change fixture verifies settings round-trip, rollback, a missing source returning, existing clip identity and favorites, and projects from both roots. Two isolated offscreen profiles exercised SQLite on and off with four generated clips across the active and retained folders and two capture days. Both profiles showed both folders, found one filtered match with selection preserved, and passed mixed and multiple day selection. The configured active destination stayed selected. Fixture files, their timestamps and the settings file were unchanged. The unavailable retained folder stayed absent. Only the SQLite-enabled profile created the index. The headless WPF Games fixtures covered 8, 50 and 200 rows, inline expansion, filtered identity, virtualization and accessible targeting labels. No live screen or audio capture ran.

This is the integration base for the final Audio and Hotkeys WPF work. Supervised Windows display consent, mixed DPI, screen reader, real launchers, gameplay and Sonar checks remain pending. PR #21 remains duration evidence only. Precise saved duration remains open, and no acceptance gates were advanced.


## Final Audio and Hotkeys candidate

[PR #24](https://github.com/Yosef13103/QuickClip/pull/24) at `a2d6ae60b465051622886af3b87245d412567a68` merged without conflicts. Audio separates playback and microphone pickers and retains missing selections by exact endpoint ID and flow. The diagnostics button reads the immutable running plan and existing native lane status under the recorder lock, without opening a source. The four Hotkeys commands share the tray dispatcher and its display consent rules, including expiry of manual approval when the master is turned off.

Existing bookmarks are labeled and seekable and survive metadata edits and export trimming. The removed bookmark creation actions leave stored data readable. Compact durations change UI formatting only. Native capture, mux policy, and replay timing remain unchanged. PR #21's synthetic measurements remain linked evidence and its native export is absent.

### Validation

Code tested at `8d13305980e3a639012cd4e73a797808e894e175` on Windows 10 build 19045 with SDK 10.0.403. Strict Personal and Upstream Release and Personal Debug builds passed with zero warnings and errors. The local restore used the pinned dependency cache and `-p:NuGetAudit=false`.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Personal | 511 | 0 | 18 |
| Upstream compatibility filter | 27 | 0 | 14 |

The offscreen Audio and Hotkeys pages rendered with recording inactive. Library and Gallery passed with SQLite on and off, retaining both configured folders and filtered day selection. The synthetic media, timestamps and settings were unchanged. The offline folder stayed absent. No live screen or audio capture ran. The full Personal suite includes opposite-flow devices with equal labels, missing endpoint preservation, native status matching, hotkey registration and consent regressions, and bookmark read/seek/preservation.

### Owner-supervised validation

Ready to exercise Windows 10 DDA on an explicitly approved display, discovery on multiple actual launcher drives, isolated Sonar signals and 125%/150% DPI and keyboard navigation using a scratch profile and disposable inputs. [Field-test checklist](p0-field-test-acceptance.md) · [Audio and Hotkeys evidence](ft09-ft11-recording-settings.md).

Precise saved duration, support for additional launchers beyond Steam and Epic, and Windows 11 borderless capture capability and permission remain open. The P0 ledger remains 19/27 implementation and 0/26 acceptance. No hardware gate was advanced.


## Saved-clip markers, player surround and final-branch validation

PR #25 was integrated by merge commit `b2e8f098930b90e4806a672b6bb14886198250b2` after a green Windows PR check (517 Personal passed, 18 skipped; 27 Upstream passed, 14 skipped). Only successful saved clips leave frame-anchored markers for later overlapping replay windows; markers are persisted in the existing metadata and displayed as diamonds in the Gallery player and bookmarks in the Editor. Player/Editor video letterboxing now uses #282828, without modifying video pixels or exports.

The chime is still rendered by WinMM PlaySound and **is not selectively excluded from endpoint loopback capture**. No silent correction or muting of unrelated track audio is claimed. Output to an existing unrecorded Sonar Aux device is a future optional design, pending hardware routing tests.

Integration PR #18 now targets `custom/main` directly. Re-run strict Windows quality on the new target's merge checkout; passing against the old PR #7 base is not sufficient. Release, on-device DDA/consent and Sonar lane acceptance remain unverified. Close superseded feature PRs after the final candidate is safely merged; PR #21's native duration diagnostic remains a separate unresolved investigation.

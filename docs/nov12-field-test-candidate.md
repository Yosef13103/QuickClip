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

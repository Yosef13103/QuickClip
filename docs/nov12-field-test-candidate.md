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

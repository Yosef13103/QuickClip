# P0 field-test discovery follow-up

Scope is FT-01, FT-02, FT-06 and QC-003. Base is PR #7 at `0c7719410a045a7a28c7a37d1d8f14d6cc045ef7`. This branch contains discovery, the detector contract, narrow Games Settings wiring and generated fixtures.

## Discovery and migration

New Games settings default to automatic discovery. Previously persisted choices still apply. With the Games page visible, QuickClip reads Steam install registration or its conventional install path, then local `config/libraryfolders.vdf` and `steamapps/libraryfolders.vdf`. It visits only the declared fixed-drive libraries and their `steamapps/appmanifest_*.acf` and install directories. Multiple installs for the same Steam app ID use one row. The library panel shows discovered sources. Excluding a source also vetoes executable matches under that source and the exclusion can be removed. Manual Steam roots and exact executables remain supported.

For a folder of several installs, use **Add game collection**. Use **Add one game folder** for an explicit single-install scope. To convert an existing saved single-install root, remove its approval and add it as a collection. Single-install and exact-EXE entries retain their current identity rules. Steam titles use the manifest name. Folder titles prefer useful version-resource strings from a bounded set of ranked candidates. Common crash reporters, installers, redistributables and generic updaters are excluded from discovery. Exact paths are used for matching and no EXEs are executed.

Collection discovery counts directories and EXE candidates instead of loose files. It separates shallow collection traversal and per-install candidate probing. There are per-segment candidate/time limits, a Steam manifest byte limit per library, and a scan-wide cooperative deadline. A single blocked Windows filesystem call can outlast that deadline. Cancellation leaves the previous snapshot in place. A later scan reconciles partial results with earlier rows and preserves overrides. There is no persisted mid-directory cursor. A consistently oversized segment may require approving a narrower scope.

## Recorder adapter for Chat 1 and integration owner

`GameDetector.ForegroundObservation` reports the current observation. `GameCaptureObservation.VerifiedTarget` is the eligible exact catalog target for the armed recorder. It is populated for an explicit `Game` match or an exact candidate from a verified Steam manifest install. Legacy fullscreen/path heuristics may still supply `Target` for existing callers. They do not authorize capture through `VerifiedTarget`.

Use `GameDetector.SelectVerifiedObservation(retained, foreground, isCurrent, match)` for a verified-only state machine. Check HWND, thread, PID and process-start identity using `WindowOwner`. Retention rechecks the current catalog match, foreground-only rules, ambiguity and exclusion/offline vetoes. A Discord companion has no verified target and can leave an existing eligible game retained. `SkipAutomaticCapture` remains the existing observation veto indicator. Code should require `VerifiedTarget` independently of that flag. Source authorization, backend selection and armed/idle transitions remain the recorder's responsibility.

## Verification and remaining scope

Use a scratch profile and the local strict Release workflow described in `docs/p0-windows-acceptance.md` if available on the integration branch. For focused validation use `dotnet test QuickClip.slnx -c Release -p:RequireCaptureEngine=true --filter "FullyQualifiedName~GameCatalog|FullyQualifiedName~GameObservation|FullyQualifiedName~SteamManifest|FullyQualifiedName~GamesSettingsView"`. Fixtures exercise Steam library configuration, multiple roots, nested same-name games, 20,001 loose files, helper rejection, exclusions, cancellation, stale results and Discord focus. The scanner can be given a fixture Steam installation provider so tests avoid registry or personal Steam data.

Owner acceptance still needs actual separate-volume Steam installations, Terraria/tModLoader/Marvel Rivals identity, and game/Discord focus under the integrated recorder. Automatic scheduling outside Games Settings and exact resumable cursors remain open. Epic should be added and validated in a separate adapter PR. Other stores remain unsupported.

Rollback by reverting the feature commits on the integration branch. Existing catalog JSON remains readable and no media or launcher files are changed. Old versions ignore discovered-source metadata and can still use manually approved roots.

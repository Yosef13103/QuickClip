# Epic installed-game manifest adapter

This follow-up is stacked on the core discovery coordinator in PR #12. It checks the standard Epic Games Launcher ProgramData manifest directory when automatic discovery is enabled. It reads .item files, bounded to 256 KiB each and 8 MiB total per pass, using the coordinator's candidate and time limits. Reads are local and read-only. [Epic documentation for the manifest location and AppName field](https://dev.epicgames.com/documentation/unreal-engine/academic-installation-of-unreal-engine)

The adapter requires an executable record and a declared relative EXE beneath a specific local fixed-drive install directory. It rejects incomplete installs, known Unreal Engine IDs, add-ons with a different main app, path traversal, executable helpers, and reparse points. It verifies the declared EXE exists and does not search the install for another executable. Steam metadata retains precedence for an overlapping install, and a matching approved folder does not create a duplicate row. Excluding the Epic manifest source or an install path prevents that source from supplying eligible rows.

Epic entries are candidates and show Needs review until explicitly classified as Game. The declared executable can be a bootstrapper and may differ from the renderer. The supported schema subset is exercised by fixtures; legacy/preview launcher versions, protected or nonstandard Epic install layouts, and real renderer identity still need supervised validation. Other launchers are outside this adapter.

Revert the Epic commit to remove the adapter. Steam, the coordinator and the existing settings/profile format remain usable. No launcher files are changed.

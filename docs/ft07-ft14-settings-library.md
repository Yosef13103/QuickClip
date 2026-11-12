# FT-07 and FT-14 Settings and saved recordings

Base is the November integration candidate `ffe6135e8ab84106b5a4dab09ac6a6881a62ca71` on `integration/nov12-field-test`, which contains the armed recording controls from PR #13.

Personal Settings has keyboard-focusable sections for Recording, Audio, Games, Hotkeys, Sharing, Library, General and Diagnostics. The selected section stays selected while switching app pages. Existing recording and hotkey handlers remain in place. Games keeps its existing discovery view.

Library shows the existing `AppSettings.ClipsFolder` destination, available bytes on its drive and an Explorer action. **Change and keep old folder** adds the old destination to Additional Gallery sources before switching new saves. Selecting it again reverses the roles. Adding and removing a Gallery source affects visibility. The media and existing project folders stay in place. Folder settings are written atomically and a write failure restores the previous in-memory settings. Fresh and legacy configurations keep their existing destination.

Gallery reads the configured sources with or without SQLite. Existing indexed IDs and favorites continue to belong to their original path. Existing projects from configured sources remain openable, and new projects use the current destination. Offline sources remain configured and are rechecked when opening Gallery. Nested roots and reparse points are excluded to avoid duplicate or unintended traversal. Relative exclusions apply to each source. Removing a source leaves its existing SQLite records available to the index inventory, which reports registered roots rather than just the currently configured Gallery sources.

```powershell
dotnet test QuickClip.slnx -p:RequireCaptureEngine=true --filter 'FullyQualifiedName~SavedRecordingFolders|FullyQualifiedName~LibraryIndex|FullyQualifiedName~LibraryGallery|FullyQualifiedName~RecordingDefaults'
```

For the offscreen smoke, run Debug with `--render=<scratch directory> --library-smoke-only`, one disposable clip in `clips` and one in an additional configured source. Use an isolated profile. Repeat with indexing on and off. A supervised desktop check should cover keyboard navigation and focus, 1080p at 125% and 150% DPI, confirming and canceling a real folder choice, an unavailable retained source returning, and searching or opening an existing project after a switch. No capture is required for those UI checks.

To roll back the destination, select the former folder in Library. To roll back the code, revert the feature commit. Keep the current `ClipsFolder` and take note of any additional sources before running an older build that only displays one folder.

# Game catalog integration proposal

The standalone service in `QuickClip.Discovery` takes explicit inputs and returns local data for later Settings integration. Its filesystem reads are limited to directory entries, file attributes, and approved Steam app manifests.

```csharp
Task<CatalogResult> GameCatalogService.ScanAsync(
    CatalogRequest request,
    CancellationToken cancellationToken = default,
    IProgress<int>? progress = null);

ImmutableArray<string> SteamManifestParser.ParseLibraryFolders(string text);
SteamAppManifest? SteamManifestParser.ParseAppManifest(string text);

CatalogMatch GameCatalogIndex.Match(string? executable, bool isForeground);
Task<CatalogResult> GameCatalogRefresh.RefreshIfDueAsync(CancellationToken cancellationToken = default);
```

- `CatalogRequest.GameRoots` accepts individual game directories or collections. A game root groups all candidate EXEs under that directory. A collection groups immediate child directories separately and groups its loose EXEs under the collection root.
- `SteamLibraryRoots` accepts explicitly approved Steam library directories. The scan reads `steamapps/appmanifest_*.acf` and checks the corresponding `steamapps/common/<installdir>`. It verifies that the filename and manifest agree on the app ID. Library-folder manifests return suggestions only. A caller must obtain approval before adding those paths to the request.
- `ManualEntries` uses a caller-created GUID and one or more absolute local EXE paths. The service stores manual paths as strings and accepts offline files. Deleting an entry removes its saved configuration.
- `Overrides` stores the user’s display name, inclusion, app classification, activation, capture preference, optional profile ID and priority by stable ID. Excluded candidates stay in the result so Settings can display and re-enable them.
- `CatalogResult` contains immutable entries, source and confidence, install directories, EXE candidates, a capped sample of issues, a visited-entry count, a limit flag and a UTC completion timestamp. Confidence describes the association to a catalog entry. Runtime matching still needs to identify the binary that renders the game.

Steam IDs use the app ID and can combine installations across approved libraries. An exact folder match to a discovered Steam installation uses the Steam entry. Folder IDs derive from normalized, case-insensitive paths and change when the directory moves. A manual ID survives display-name and EXE path edits.

Build `GameCatalogIndex` once from a completed catalog. Match the executable path of a verified process rather than a title or basename. Manual entries take precedence and same-precedence overlaps return Ambiguous. `CatalogRules.LauncherExecutables` identifies paths that belong to the game entry but represent its launcher. A Steam or folder match without an explicit game rule returns Candidate for further runtime classification. Explicit exclusions, launcher roles and non-game classifications block game promotion. Unknown paths can use the current fallback policy. The Discord updater is ignored even when a manual entry includes it.

Periodic refresh is opt-in. Construct one `GameCatalogRefresh` with the approved request and an interval from one minute to one day. Call it from a dedicated Settings-owned timer while enabled. Concurrent requests share the last completed result within the interval, which is measured using a monotonic clock. Replace the refresh instance after editing approved roots. Process polling should read the published index.

Scans accept ordinary paths on fixed local drives. The service rejects volume roots, UNC and device paths, skips reparse points including ancestors of explicitly selected roots, and checks directory exclusions by path component. It skips common dependency folders and known Discord executables or updaters during discovery. The default scan budget is 20,000 entries and eight directory levels. Requests can choose up to 100,000 entries and 16 levels across at most 128 roots. The manifest payload budget is 1 MiB per file and 8 MiB in total. Results report up to 128 issues. Scan order determines which results are available when a limit is reached. Cancellation discards the incomplete result and can be observed between filesystem calls. Concurrent filesystem mutation can invalidate path checks between verification and use.

## Proposed application changes

1. **`AppSettings.cs`** stores approved scan roots, exclusions, manual entries, and overrides separately from the last successful catalog snapshot. Preserve overrides for installations absent from a scan. Keep paths local and omit them from shared diagnostics. Assign a GUID once when adding a manual game, then reuse it for edits.
2. **`SettingsView.xaml` and `SettingsView.xaml.cs`** host a Games panel with explicit folder selection and Scan/Cancel controls. For Steam, read an explicitly selected library-folders file with a bounded read, show the parser’s suggestions, and require approval for each root. Pass a cancellation token and a UI-thread `Progress<int>` to `ScanAsync`. Show partial results and scan issues. Keep the previous successful snapshot on cancellation or failure. Permit manual EXE linking and catalog rule edits without rescanning the disk.
3. **`GameDetector.DetectForeground` and `GameDetector.GameForClip`** consume the in-memory `GameCatalogIndex` built from completed catalog results. Keep the existing window ownership checks. Only Unknown uses ordinary fallback without catalog evidence. Candidate requires runtime validation. Use the resolved identity and display name for clip attribution while retaining the current filename sanitization.
4. **`GameDetector.SelectTarget` and `Recorder.UpdateCaptureWindow`** apply foreground-only eligibility and explicit priority while retaining a valid game across ordinary focus changes. Resolve capture policies at a controlled session transition. See [per-game profiles and session integration](game-recording-profiles.md).

Integration should test an approved root and a missing root, cancelled scans, manual edits and overrides, ambiguous EXE paths, the Discord updater, and game-to-Discord focus changes. Confirm that process polling uses the in-memory snapshot and that adding an entry only updates the catalog. Runtime classification and capture policy are deferred to integration.

## Validation

The v3 targeted suite passed 37 tests with one opt-in skip. The full Personal suite passed 284 tests with 17 opt-in skips. The earlier synthetic Windows junction fixture verified exclusion for a nested junction, a selected junction root, and a root below a junction. Windows denied symbolic-link creation for the optional symbolic-link test in that run. The Personal Release build and native dependency checks passed.

# QuickClip Personal development

From the checkout root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1
```

This builds managed and native components, verifies bundled runtime dependencies, and launches the Personal identity. Use the .NET 10 x64 SDK plus Visual Studio C++ Build Tools and the pinned dependencies described in the repository README. An SDK extracted under `.quickclip-local/dotnet` also works. Missing native tools or a capture-engine compile/load failure fails the launcher visibly.

Managed dependencies restore from nuget.org using the repository's NuGet.Config. The repository-local cache is .quickclip-local/nuget. A fresh cache needs access to nuget.org; subsequent builds can reuse cached packages.

The development executable is `bin/quickclip-personal/dev/QuickClip.exe`; persistent settings and clips live under `bin/quickclip-personal/profile`. Keep the profile when rebuilding. Quit through the tray before rebuilding a running executable. The build does not install Explorer integration, replace stock QuickClip, run its updater, or access SteelSeries files. Stock and Personal profiles/process identities are distinct. New Personal profiles start with recording and the capture hotkey off.

New filename settings default to `{game}__{yyyy-MM-dd}-{HH-mm-ss} - {custom_title}.mp4`; an empty title removes ` - `. Existing configured templates and legacy filenames are preserved. Gallery/player deletion requires confirmation by default, uses the Recycle Bin, and offers the latest batch's Undo from the gallery. After restart, use the Windows Recycle Bin to recover files.

For automatic rebuild and restart:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 -Watch
```

The watcher alternates two output directories, checks a replacement before closing the old app, and relaunches the last successful app after it exits. Compile failures leave that app running. Ctrl+C stops the watcher and leaves the app open. It uses `bin/quickclip-personal/watch-profile`, independent of the normal development profile. Source rebuilds clear an active replay, so save wanted footage before editing code. Do not run the watcher while benchmarking; stop its console first and quit its application.

For a portable development directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 -Portable -BuildOnly
```

Copy `bin/quickclip-personal/portable` as a whole. It is framework-dependent and needs the x64 .NET 10 Desktop Runtime. On a machine using the workspace SDK, set `DOTNET_ROOT` to that SDK's directory before launching `QuickClip.exe`. To keep all data local, set `QUICKCLIP_PERSONAL_DATA_DIR` to an absolute data folder outside the binary directory; never place data in a folder you replace during packaging. Explicit data roots also get distinct Personal IPC identities.

Run `dotnet test QuickClip.slnx -p:RequireCaptureEngine=true` with .NET 10. Default tests use synthetic media; live screen/audio and Recycle Bin tests are opt-in. Only enable `QUICKCLIP_TEST_CAPTURE` for carefully chosen tests in a supervised, non-private session. See [recording diagnostics and manual acceptance](recording-diagnostics.md) for bounded video-only benchmarks and remaining hardware checks.

## Windows integration: shipped in Personal v1.0.0

The Personal release is **now published**: [QuickClip Personal v1.0.0](https://github.com/Yosef13103/QuickClip/releases/tag/personal-v1.0.0). The earlier pre-release plan below this heading has been superseded.

- **Installed version:** `scripts/package.ps1 -Personal` builds a self-contained x64 Personal installer and portable ZIP. The installer uses the Personal AppId and `%LOCALAPPDATA%\Programs\QuickClip Personal` and manages its own install ownership. See `installer/QuickClip.Personal.iss`.
- **Startup registration:** the installed Personal executable can opt in/out of the `QuickClip Personal` per-user Run entry, with an installation-path ownership check. Development and portable profiles don't register themselves for startup.
- **Explorer integration:** stock QuickClip's Explorer verb and optional Windows 11 package remain separate. The Personal build intentionally does **not** install or mutate the stock Explorer shell extension.
- **Settings, clips, IPC:** `AppProfile` isolates Personal settings, caches, clips, mutex and named pipes from upstream. `QUICKCLIP_PERSONAL_DATA_DIR` provides additional isolation for test profiles.
- **Updates:** Personal does **not** auto-install upstream updates. Download a newer Personal release deliberately.
- **Production build:** `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1 -Personal`. The normal `dev.ps1` launcher is still for development and should not be used as the installed app.
- **Validation:** strict Windows Personal and Upstream Release tests run in GitHub Actions. Startup registration, device capture and installer upgrade/uninstall behavior additionally require supervised real Windows tests.

The first owner-accepted release covers game detection, rolling clip capture, editing, hybrid audio and export. Simultaneous displays, independent Discord companion capture, optional game-only window capture, enhanced GPU compatibility and a Personal Explorer menu are future work, not claimed shipping features.

**Cleanup:** `.quickclip-local` is developer scratch. It may contain registered Git worktrees, unpushed patches and a downloaded .NET SDK; inspect `git worktree list --porcelain` and create a Git bundle/backup before deleting it. An installed release does not need that folder.

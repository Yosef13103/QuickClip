# QuickClip Personal development

From the checkout root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1
```

This builds managed and native components, verifies bundled runtime dependencies, and launches the Personal identity. Use the .NET 10 x64 SDK plus Visual Studio C++ Build Tools and the pinned dependencies described in the repository README. An SDK extracted under `.quickclip-local/dotnet` also works. Missing native tools or a capture-engine compile/load failure fails the launcher visibly.

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

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

## Windows integration for a Personal production release

The current project accepts `QuickClipProfile=Personal` or `Upstream`. Both Debug and Release builds with the Personal identity hide the startup and Explorer controls, reject `--register` and `--unregister`, and guard the underlying registry operations. The portable development build uses the same guards. There is currently no Personal installer or production integration profile.

| Area | Existing implementation | Personal release gap |
| --- | --- | --- |
| Start with Windows | `Startup.cs` toggles the per-user `QuickClip` Run value. Its command uses the executable path and `--tray`, which starts the app without opening the main window. | Use a Personal-owned Run value and the installed Personal executable. Expose both enable and disable controls. |
| Explorer | `ShellIntegration.cs` registers a per-user classic **Edit with QuickClip** verb filtered to supported audio and video extensions. It passes the quoted file path to the app, which can forward it to its running instance. Windows 11 also has an optional stock sparse package and COM handler for the main menu. | Use a separate Personal verb and display label. A Personal main-menu package needs its own package identity, COM CLSID, signing and deployment configuration. The handler currently resolves `QuickClip.exe` beside its DLL and must resolve the Personal executable in the release layout. |
| Installer | `scripts/package.ps1 -Upstream` builds the stock installer. `installer/QuickClip.iss` uses the stock AppId, directory and shortcuts, offers startup and context-menu tasks, and runs Explorer cleanup at uninstall. Its Run entry cleanup is attached to the installer startup task. | Provide a Personal installer identity and paths. Clean up Personal registrations created through Settings as well as those created at install time. Handle upgrades, opt-out and failed setup without leaving stale registrations. |
| App data and process routing | `AppProfile.cs` separates Personal settings, logs, clips, mutex and named pipe from stock. The development launcher also provides a workspace-local data root. | Carry that separation through executable paths, installer operations and any production portable registration. Verify that file-open and quit commands reach only the intended instance. |

For the release milestone after the UI and game-discovery workstreams are integrated:

1. Keep product identity separate from integration capability. Use explicit Personal `Development` and `Production` profiles, or an equivalent explicit build setting. Development defaults to registration disabled. Production exposes supported integration actions and uses Personal identities throughout. A Release compiler configuration alone should never enable registration.
2. Use separate install directories, executable paths, installer AppIds, shortcut names, startup values, shell verb keys, and package/COM identities. Quote executable and media paths, scope registration to the intended user, and match ownership before replacing or removing an entry. Personal cleanup must never remove stock or legacy stock entries or packages. Preserve the user’s selections across upgrades and remove only Personal-owned registrations on uninstall.
3. Keep production portable startup and Explorer integration off on first launch. Require an explicit user action to register or unregister, show which executable is registered, and provide a way to clean up a stale entry after moving a portable folder. Launching, extracting or updating a portable build should leave Windows registration alone.
4. Validate side-by-side stock and Personal installs in a disposable Windows environment. Exercise opt-in and opt-out, tray startup at sign-in, opening supported media with spaces and Unicode in the path, forwarding to a running Personal instance, upgrades and uninstalls in both orders, and portable launch/move/cleanup. Cover the Windows 10 classic menu and the Windows 11 menu paths. Verify the other installation’s registrations and settings before and after each operation.

These requirements come from source inspection. Installer and Explorer registration tests belong to the dedicated release milestone.

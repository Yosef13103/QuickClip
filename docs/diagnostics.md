# Diagnostic event logging

Production starts with event logging off. Enabling it in the Diagnostics page is a per-process troubleshooting choice. The buffer keeps the latest 2,048 events and reports events expired by capacity or skipped under native producer contention. Turning logging off discards the buffer. The viewer reads batches once per second while visible. An ordinary launch never starts a verbose event file writer.

Entries contain UTC timestamps, severity, category, a source-controlled summary and an optional numeric error code. Copy and export use the current filters. Export creates a new file at the selected location. Essential startup, capture, save, settings and crash failures go to the profile's `quickclip-errors.log`, with two-second per-category throttling for routine failures and one rotated file at 128 KiB. Managed crash entries can include up to six QuickClip method names. Existing `quickclip.log` files are outside the viewer and export path.

Recording timing continues to use `AppSettings.RecordingDiagnostics` and the existing capture restart when it changes. Capture sampling keeps one rolling summary, and replay save timing keeps one sample for the current capture session. The recording HUD's trailing FPS and microphone verification's signal measurements continue independently.

## Development sessions

`scripts/dev.ps1` enables persistent event logging for development launches in both Debug and Release. Use `scripts/dev.ps1 -NoDevelopmentDiagnostics` for benchmark launches. If you start the executable directly, set `QUICKCLIP_DEV_DIAGNOSTICS=1` in that PowerShell process before launch. Use `0` or leave it unset to start without a writer. The Diagnostics toggle pauses new verbose events for the current process.

The writer consumes the same bounded buffer as the viewer. It attempts to drain every 500 ms and flushes each batch to disk. `development.log` and `development.log.old` retain at most 1 MiB each. Every event carries a random session ID. Session headers give the build and profile, and paired operation entries report an operation ID and elapsed milliseconds. The dispatcher probe reports a delayed dispatcher acknowledgement separately from a pending save. Excess events or unavailable storage may lose a batch. Essential-error files remain independent.

The default development log directory is `bin/quickclip-personal/profile/local/diagnostics` under the checkout. For `-Watch`, use `watch-profile`. An explicit `QUICKCLIP_PERSONAL_DATA_DIR` puts the logs in that profile's `local/diagnostics`.

```powershell
$profile = if ($env:QUICKCLIP_PERSONAL_DATA_DIR) { $env:QUICKCLIP_PERSONAL_DATA_DIR } else {
    Join-Path (Get-Location) 'bin/quickclip-personal/profile'
}
$logDirectory = Join-Path $profile 'local/diagnostics'
$files = @('development.log.old', 'development.log') |
    ForEach-Object { Join-Path $logDirectory $_ } |
    Where-Object { Test-Path -LiteralPath $_ }
Select-String -LiteralPath $files -SimpleMatch 'session start' | Select-Object -Last 12
$session = Read-Host 'Enter the session ID at the start of the relevant log line'
Select-String -LiteralPath $files -SimpleMatch "$session " |
    ForEach-Object Line | Set-Content -LiteralPath (Join-Path $logDirectory "$session.txt")
```

If a hang returns, leave QuickClip running and note the UTC time. In Task Manager's Details tab, verify the PID for the affected preview before choosing **Create memory dump file**. The dialog provides the local file location. Keep the dump local because process memory can contain clips, paths and credentials. An indefinitely pending async save can leave the dispatcher responsive, so capture the dump while the symptom is present. [Task Manager process dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)

## Wave 2 integration

- Mount `QuickClip.Views.DiagnosticsView` in the Personal diagnostics section after integrating the settings layout from PRs #78 and #79. The control owns the session toggle and refresh lifecycle. Keep the existing timing sampling control independent. The performance panel can append `Recorder.Instance.LastSaveTiming` on refresh when a sampled save exists.
- Discovery can publish fixed scan started, cancelled, completed and failed summaries under `DiagnosticCategory.GameDetection`. Use outcome boundaries in the coordinator and avoid per-install or per-process events. Treat every raw process command line as sensitive, including Java launches that can carry Minecraft authentication credentials. Keep command lines, tokens, game titles, inventory, launch arguments, paths and IDs out of diagnostic payloads.
- The updater can publish fixed check, signature or hash verification, staging and install outcomes under `DiagnosticCategory.Updater`. Use error codes or HRESULTs where available. Keep feed URLs, download paths, authorization data and raw subprocess output out of the log. Migrate the update action's catch in `SettingsView.xaml.cs` while wiring the frontend.
- Migrate the four remaining legacy `Log.Write` calls in the discovery coordinator, updater and update UI. The compatibility method compiles those calls and their arguments out. Remove the method and its disabled-argument test after the last caller migrates.
- Use `Log.Event` for a constant summary plus optional status code, `Log.Failure` for optional errors and `Log.Essential` for failures requiring the independent local report. Native capture and audio diagnostics use fixed literals and numeric error codes. Raw exception text, libmpv/FFmpeg messages, device IDs, paths and process data stay out of the event stream.

## Reproduce the synthetic test

From the repository root, after a strict Personal Release build with .NET 10:

```powershell
$env:QUICKCLIP_CI = '0'
$env:QUICKCLIP_TEST_DIAGNOSTIC_BENCHMARK = '1'
try {
    dotnet test QuickClip.slnx -c Release --no-build --no-restore -p:QuickClipProfile=Personal `
        --filter 'FullyQualifiedName~QuickClip.Tests.LogTests' --logger 'console;verbosity=detailed'
}
finally { Remove-Item Env:QUICKCLIP_TEST_DIAGNOSTIC_BENCHMARK }
```

The benchmark alternates disabled and enabled producers and measures elapsed time, process CPU time, current-thread allocation, toggle allocation, retained events and logging file growth. Concurrent producers, overflow, teardown and explicit export are tested separately.

## Owner-supervised capture comparison

Quit other recorders, build the dev executable once per candidate, and use a scratch profile with a non-private test scene. These commands capture video into a five-second RAM ring for 20 seconds. They save benchmark metrics locally and export no media or audio.

```powershell
.\scripts\dev.ps1 -BuildOnly
$capture = @{ CaptureDesktop=$true; Seconds=20; Encoder='hevc_nvenc'; Preset='p3'; Fps=60; Height=0; VideoKbps=20000 }
.\scripts\benchmark-video.ps1 @capture -NoTimingSampling
.\scripts\benchmark-video.ps1 @capture -NoTimingSampling -VerboseDiagnostics
.\scripts\benchmark-video.ps1 @capture
.\scripts\benchmark-video.ps1 @capture -VerboseDiagnostics
```

Repeat three times with alternated order. Keep the monitor, backend, content, FPS, encoder, resolution and bitrate fixed. Use the result JSON for process CPU, memory, managed allocations, handles, threads, submission rate and dropped slots. GPU counters in `gpu.json` are device-wide. Compare the pinned baseline `f7016d229b16d5971b671d5f2212a714d0b81670` with the same timed benchmark arguments available on that revision. Keep these files private because the benchmark also describes the test displays.

For real-game acceptance, repeat a controlled game scene with the integrated UI on the same scratch settings, alternate event logging and timing independently, and compare visible/tray-hidden runs. Record gameplay FPS, 1% lows, frame pacing, GPU 3D/Video Encode, VRAM, process IO, replay duration and save latency. Verify the visible viewer, microphone verification and HUD with the owner. The synthetic test measures producer cost and does not measure those hardware outcomes.

## Combined integration
The viewer is mounted in Advanced Diagnostics; the capture timings panel includes the latest replay-save sample. Discovery scan outcome, update check/download/handoff/failure and HUD unavailable events now use constant summaries. The legacy compiled-out method remains temporarily for test compatibility and any residual call sites pending final audit. No raw exception text or identity is emitted by these new events. Exact combined CI and hardware acceptance pending.

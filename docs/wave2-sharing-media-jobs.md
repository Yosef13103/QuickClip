# Sharing, capped exports, and media jobs

D starts at A's verified code candidate `1cbe852a12f581edfe597b37725348611d871b0f`. The draft stacks onto #88, whose branch advanced independently. A's checkpoint `e08948f1029bde92494c51bd6c79527c78d722a3` and D both edit `AppSettings.cs`. The local three-way merge reported no textual conflict. Verify that the combined checkout keeps A's notification defaults and D's sharing settings, and recheck the Gallery open transition after integration. C owns broader appearance and theme work. Keep D's existing theme resources, Settings card, share actions, export modal, and status bar job action when reconciling the UI.

## Sharing and export

Manage named sharing profiles under Settings. Caps use decimal MB. The legacy `ShareSizeMB` creates a stable initial Default profile and mirrors the selected default when saving for older QuickClip versions. Invalid rows are skipped, and the first valid profile takes over when the default disappears. Profile changes are committed through the atomic settings write. A failed write leaves the prior profiles active.

Gallery and classic Player share the saved clip. Editor's **Share current edit** snapshots trim, crop, selected tracks, levels, and added sounds. Its menu also offers the saved clip. The default action uses the saved default profile. The menu offers alternates and a one-off cap. Share produces a compatible MP4 with H.264, up to 60 fps, and a single AAC soundtrack when audio is selected. A recognized saved Full Mix is used once. An edited share builds its single mix from the chosen editable tracks. A profile limit is a local file cap. The receiving service can apply different account or upload limits.

**Export as new file** opens a size picker with Original, 10, 30, 50, 100 and Custom MB. Original keeps the editor's format, encoder and quality settings, with stream copy when possible. An encode can change quality and size. The advanced action returns to the existing editor controls. Capped exports keep the selected settings unless the user opts into adaptive resolution, and they can retry with less video bitrate. An automatic hardware encoder can fall back to its CPU equivalent. An explicit encoder remains selected.

The hard ceiling is the smaller of the selected cap and the applicable source byte budget. Full source uses actual source bytes. Duration and crop selections use an estimate. Crop scales the estimated video portion while keeping the selected duration's estimated source audio. The encoder budget reserves container headroom, counts copied or encoded audio and respects mix and hybrid layouts. After each attempt, QuickClip measures the staged file, then probes its duration, stream layout, and video bounds. Up to three attempts cover stream-copy overshoot, measured bitrate correction, and automatic hardware failure. A failed or cancelled job never publishes the staged file. Capped mode rejects unsupported formats and targets below the minimum bitrate. Exact playback, seek and sync checks belong in fixture and owner validation.

## Clipboard and retention

QuickClip publishes a unique path in the profile's shared copies directory, which Gallery excludes from scans and indexing. Shell OLE serves the file. If unavailable, QuickClip tries a file-drop list. Both hand off paths or Shell file objects. A newer share or recopy request invalidates an older automatic copy. A Windows clipboard sequence change also invalidates a delayed copy. Explicit **Copy again** and **Reveal for manual attachment** stay available in Media jobs. The row identifies the file placed on the clipboard while its recorded sequence is current.

Shell clipboard ownership is flushed on normal exit where supported. Completed files remain available across exit. New shares carry a private retention receipt with exact file identity; after 24 hours since publication or last successful recopy, startup and hourly cleanup expire unchanged copies unless they are still offered on the clipboard or held by another media job. Untracked legacy files, modified files, unavailable clipboard state and linked paths are left alone. Cleanup never recursively deletes a directory. QuickClip admits up to 128 new-format share directories per profile. Use **Open shared copies folder** to remove old untracked copies manually. Job history holds the most recent 30 terminal jobs for the current process. Published files can be opened from Explorer after restart.

## Scheduling and ownership contract for B

- Build an immutable submission snapshot before calling `MediaJobs.Current.Submit(MediaJobRequest)`. Capture each video, audio or other media input with `MediaSource.Capture(path)`. Capture exact provenance and application metadata separately. The scheduler validates path and file identity, size and timestamps, and holds read handles while working.
- Submission reserves shared input reads and an exclusive destination. It returns a job ID and `Completion`. Multiple input paths are acquired as one request. `MediaOwnership.Current.TryMutate(path)` is the matching exclusive lease for synchronous external callers. Keep it through an entire recycle, restore, or replacement transaction. A null lease means the user should wait or retry. Path comparison is case-insensitive and existing file identities cover hard-link aliases. Existing readers and writers prevent conflicting in-app operations. Open source handles also block ordinary external writes during processing.
- Call `MediaProcessing.PublishAsync` for new outputs. Supply the same destination reserved at submission. Produce into its staging path, validate, then let it move to the final location. The move never overwrites an existing file. Use `MediaJobRequest.Replace` only for an explicitly confirmed source replacement. The scheduler closes source handles immediately before that callback and retains the exclusive lease. Revalidate the exact source immediately before replacement. The callback must provide its own rollback and return the committed path. Retry is disabled for replacement jobs since their source transaction may have advanced.
- Call `MediaJobContext.EncodeAsync` around each FFmpeg hardware encode and use `ExportRunner` for process lifetime, progress and cancellation. Report attempts and validation through the context. CPU/filter threads are constrained for the editor export builder, and FFmpeg runs below normal process priority. Automatic capped jobs can try a CPU encoder after hardware failure. Keep fallbacks bounded and explicit advanced choices intact.
- Concurrency defaults to one. Settings can allow two simultaneous workers. New starts use a ceiling of one while recording. Already running jobs finish. One background hardware encode slot limits pressure on driver sessions. Queue admission is capped at 32 active or waiting jobs, with a visible error when full. Disk free-space checks use planned bytes and headroom. The OS can still reject a write if available space changes. There is no separate worker pool for joining.
- `Snapshot` exposes progress, attempt phase, elapsed time, ETA where meaningful and completion state. `Cancel(id)` acts on one job. Retry submits the same immutable snapshot again, including its input stamps and destination. Replacement remains on the Editor's exclusive path through the recycle and Undo transaction.
- Normal quit cancels queued and active jobs and waits for settlement. Close-to-tray keeps non-destructive work running. Updates are blocked while media work is pending. The FFmpeg encoder process belongs to a Windows kill-on-close job object. Startup recovery only cleans files with a matching QuickClip staging receipt and file identity. The queue is in memory and abandoned work must be resubmitted. Preserve existing completed output and never recover a job by writing over a source.

## Isolated preview and owner acceptance

Use a fresh path for each preview. Run the following from the clean D worktree after a strict Personal build. Substitute the reported exact candidate SHA for `<D-HEAD>`. The profile uses a separate IPC identity. Fixtures must be generated or approved specifically for the test.

```powershell
$pin = '<D-HEAD>'
if ((git.exe rev-parse HEAD) -ne $pin -or (git.exe status --porcelain)) {
    throw 'Open the clean, pinned D worktree before previewing.'
}
$profile = Join-Path (Get-Location).Path ".quickclip-local\share-preview-$([guid]::NewGuid().ToString('N'))"
$settings = Join-Path $profile 'settings'
New-Item -ItemType Directory -Path $settings | Out-Null
@{
    RecordingEnabled = $false
    StartInTray = $false
    ReplayOnboardingCompleted = $true
    CheckForUpdates = $false
    Games = @{ AutoScan = $false; DiscoverSteam = $false; DiscoverEpic = $false; DiscoverGog = $false; EnabledLaunchers = @() }
    MediaJobConcurrency = 2
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $settings 'settings.json')
$oldProfile = $env:QUICKCLIP_PERSONAL_DATA_DIR
$oldSdk = $env:DOTNET_ROOT
try {
    $env:QUICKCLIP_PERSONAL_DATA_DIR = $profile
    $env:DOTNET_ROOT = (Resolve-Path '..\dotnet').Path
    & .\scripts\dev.ps1 -Configuration Release -NoDevelopmentDiagnostics
} finally {
    $env:QUICKCLIP_PERSONAL_DATA_DIR = $oldProfile
    $env:DOTNET_ROOT = $oldSdk
}
```

1. Check profile migration, add, rename, reorder, default, deletion and restart. Use Basic and Advanced settings. Try invalid caps, a duplicate name, and a write-locked scratch settings file. Inspect dark, Light and Custom at 100%, 125% and 150%.
2. Open generated H.264, HEVC and AV1 clips. Try Original, presets, a restrictive cap, an impossible cap, crop, trim, added sound and hybrid inputs. Check the displayed estimate and actual decimal bytes. Play and seek each result. For overwrite and Undo use only disposable footage.
3. Use generated results near 10, 30, 50, 64, 100 MB and a larger custom cap. Paste into an empty Explorer folder and compare hashes. In an approved receiving session, check Discord and WhatsApp attachment behavior without sending or uploading. Record local copy and attachment acceptance separately. Change the clipboard while jobs run, complete two shares in reverse order, repeat Copy again, test a busy clipboard, quit and paste, and verify manual attachment from Reveal.
4. Enable two jobs and start three outputs using separate names. Check two running and one queued, then cancel only one active job. Mix an export with a share and navigate away. Test name collision, a restored or replaced source and a full disposable volume. Verify originals, metadata, drafts and earlier results. Quit during active and queued jobs, then restart and check recovery. Test update blocking without installing an update.
5. Use the [Wave 2 performance protocol](wave2-performance-protocol.md) in a supervised session with OBS closed and an otherwise matched workload. Measure recording alone, one export, two configured exports, and queued work. Repeat hardware and CPU encodes with 1080p and 3440×1440 generated workloads and a restrictive cap. Record game/capture FPS and drops, save latency, CPU/GPU/encoder utilization, RAM, disk I/O, output size and quality, job completion times, and queue policy changes. Treat one as the default until these measurements justify changing it.

## Local Windows evidence

Strict Release builds with `RequireCaptureEngine=true` passed with both profiles. The Upstream run used the Windows quality filter plus sharing, export and media-job fixtures. It passed 137 tests and skipped 14. Full Personal passed 887 and skipped 23, with four failures. Three timeout cases passed on rerun. The remaining failure was the sandbox denying the existing test's scratch registry key. Personal with only that case excluded passed 890 and skipped 23. The opt-in Windows sharing modal test passed with its disposable settings and Light palette. Receiving-app clipboard, owner UI, physical capture and performance gates are pending.

The concurrency fixture waits for positive FFmpeg progress from two encodes at once with a third queued. It cancels the first and confirms the other two publish playable video. The fixture also checks that an in-use source cannot be mutated and that a queued destination cannot be reserved twice. Separate fixtures reject changed queued sources and cancel active and queued work on shutdown. Generated H.264, HEVC and AV1 tests cover byte ceilings, no inflation, edited crop and audio, decode and seek, and failure for an impossible cap.

## Owner release polish

The original-size option and capped presets are direct selections instead of an advanced dropdown. Large reductions (below 35% of estimated selected source bytes) warn about quality. Replacement uses the same verified byte-cap path with hybrid audio, original identity validation, recycle/Undo and explicit confirmation unless the owner opts out. Irrecoverable-delete and failed-draft prompts are never suppressed. Gallery hover shows a short original-source loop, independent of the saved trim, using one muted decoder and no new media cache. Gallery opening animation stays unchanged pending further motion research. The settings view controls the visibility of Encoding in the editor; frame rate lives under Output. Recheck actual Windows layouts, native playback, clipboard delivery and cleanup on scratch media before release.

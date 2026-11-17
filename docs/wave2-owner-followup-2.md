# Owner follow-up 2 (after 28b694c)

Owner's latest acceptance: filled issue badge requested; executable block action must be reversible; expand the entire selected Gallery card IN Gallery while Editor prepares, then switch only when verified new preview ready; designer needs coherent QuickClip design language; saved-clip banner missing even with capture exclusion off (also present on prior build); microphone action labels rendered as missing glyphs; previous/next clip editor navigation requested; remove duplicate favorite control, improve star/keyboard icons; remove Personal view mode; fullscreen icon belongs in transport and true fullscreen should use overlay controls; small audio track slider clips at top/bottom, remove Taller tracks.

Source changes in this follow-up: contrast-aware filled amber badge, explicit block/unblock action (does not override inferred non-game roles), text vs icon button factory split, editor star/keyboard vector icons, hide inspector favorite toggle in Personal while retaining underlying autosave control, hide Personal view-mode button and shortcut, relocate fullscreen to playback row, compact dedicated track volume slider, remove Taller tracks UI, remove incorrect editor-side thumbnail animation. Add focused tests and privacy-safe save feedback breadcrumbs. These changes are not owner verified.

Initial previous/next buttons added using a snapshot of Gallery's visible clip ordering and the existing editor open/save gate; verify position, stale or deleted list entries, rapid switching and gallery/player context. Outstanding: full-card Gallery-side transition with completion/cancellation handshake; true edge-to-edge fullscreen with floating auto-hiding controls that work above native mpv HWND; investigate saved notification path from actual save, settings eligibility, display selection, concurrent notification replacement, and window lifecycle, alongside click-through defect. Theme designer needs a cohesive visual redesign using QuickClip shared resources; gradients separately remain outstanding. Do not restore image capture exclusion. Keep overlap joining separate and new-provenance-only as owner clarified. No merge, release, destructive testing or production profile modifications authorized.

Prior head 28b694c passed Windows quality run 43974570786. New commits require their own CI and owner runtime testing.

## Saved feedback routing — owner log follow-up

Owner logs from 5c3251d show successful replay saves, followed by image selection or no overlay selection; no banner rendering requests. Inspection found two deterministic policy defects: ForEvent returned the image instead of a banner when both were enabled, and the saved-event text switch suppressed image and sound together. Follow-up separates sound, text (Windows/on-screen) and image routing. The visual master still controls both visual channels; sound remains independent. Existing ImageOnly JSON key is retained for compatibility but means enable saved image, not replace banner. Each visual channel uses its own lazy native window lifecycle; no substitute text banner is emitted if image loading fails and text is disabled. Explicit preview buttons target their named channel. Validate all combinations on actual saves, persistence, simultaneous image/banner placement (same-anchor overlap possible), capture exclusion of banners only, and click-through. Native visibility is not yet confirmed. Prior 5c3251d Windows quality run 43978320863 passed.

## Owner verification — notification routing, exclusion and clip age

Owner confirms independent saved sound/image/banner routing works at 0a15910 (Windows quality 43980076524 passed). Same-anchor image covers part of banner, acceptable with a warning and manual placement. Owner reports excluded banners are static and, critically, actual manual desktop clips contain black rectangles in place of excluded saved banners AND small replay/microphone HUD icons. This supersedes earlier apparent exclusion success. Do not mark exclusion accepted. Need pixel-verified DDA/WGC comparisons on Windows 10, plus click-through, without postprocessing, inpainting, or fabricating missing pixels. If no reliable lightweight method exists, disable/retire exclusion for affected capture backends and offer ordinary visible notifications, another display, tray status or no overlay; never silently substitute black rectangles. Follow-up makes saved excluded banners static, disables shared animation picker while saved exclusion is selected without changing stored animation for other banners, and adds same-anchor warning and experimental exclusion copy. Image animation remains unchanged. Gallery relative age now represents local file creation UTC (the staging file is created during save), preserving embedded first-frame timestamp, filenames, sorting and day grouping. Imported/copied files may have different local creation dates than their actual footage; tooltip explains both. No schema or historical metadata changes. New candidate requires CI and physical verification.

## Final stabilization candidate in draft #88

This pass continues from `8aff979bc77c345566eb351890febdc144ffd050`. PR #88 includes #87 and targets `integration/wave2-full-acceptance` at `eb1085ca97fd82be82077042d8bc3ad64af8821a`. The draft's head identifies the candidate. Production remains outside this pass.

| Finding | Candidate behavior and acceptance |
| --- | --- |
| W2-F01 | Existing Gallery and Editor synchronization remains in place. Generated-media WPF exercises a Gallery-side rename, return to the existing Editor with draft retained, Editor rename, restart, export and replace/Undo. Physical-owner retest pending. |
| W2-F02 | The selected Gallery card is captured once at a bounded resolution and expands in Gallery. The Editor prepares its persistent player outside the client area. The current playlist entry must report playback restart and match the requested source before the page and rail switch. The transition respects reduced motion. A slow request shows Cancel. Timeout, failed load and navigation cancellation leave Gallery current. Adjacent clips use a snapshot of Gallery order, including when opening from the Player. |
| W2-F03 | Keep #88 Games presentation, issue dismissal, repair actions and reversible executable blocking. The Minesweeper report matches the loose collection-root case without verified install ownership. Exact Add Game is the repair. The Minecraft Launcher probe uses the owner-observed dedicated runtime suffix on fixed drives. A missing or changed runtime requires locating the exact executable via the launcher runtime folder or the running game's process in Task Manager. Real Java gameplay, Xbox placeholder association, Minesweeper capture, osu!lazer, 007 and Simpsons Hit & Run are pending owner checks. Bedrock capture has no hardware evidence. |
| W2-F04 | The original missing saved themes symptom remains unreproduced. The opt-in WPF picker test covers saved themes in the actual popup, import, Light/custom switching, settings reload, duplicate and rename rollback. Owner theme library and restart remain pending. |
| W2-F05 | Confirmed #87 cause was canonical SQLite tag order disagreeing with insertion order for `g` and `fgf`. Keep bounded waits and write ownership. Generated-media WPF exercises rapid rail requests during debounce and closing a never-settling save with recovery. Contention, retry and late-write behavior remain covered by the focused suite. |
| W2-F06 | Use the same bounded diagnostics infrastructure as #87. `scripts/dev.ps1` enables it in Release and Debug and accepts `-NoDevelopmentDiagnostics` for benchmarks. [Diagnostics path, session extraction and local hang dumps](diagnostics.md#development-sessions). |
| W2-F07 | Keep the existing Refresh/F5/tray routing and page-specific guards. Owner-side refresh during capture and while editing remains pending. |

### Overlay exclusion and native input

Owner footage disproved usable exclusion on Windows 10 with an RTX 3070 in manual desktop recording. The footage contained black patches where both excluded banners and HUD icons appeared locally. The backend in that test was not recorded in the available evidence. A successful Windows affinity call cannot establish valid underlying encoded pixels or input pass-through. The candidate retires production display-affinity use for these overlays. Saved banners previously set to exclusion stay held until the owner enables ordinary on-screen banners for recordings. The existing recording and microphone HUD preferences are retained, with a separate opt-in to icons appearing in footage. Ordinary overlay windows use layered, transparent, non-activating styles and close if the applied styles are incomplete. Banner animation is available for ordinary overlays. Custom images are ordinary overlays. Saved sound, Windows/on-screen saved text and image routing retain the owner-accepted independent channels, with the visual master and manual overlap guidance.

The WPF HWND fixture checks ordinary affinity and the layer, transparency and activation flags. Actual cross-process click, drag, held-button and activation behavior remains pending. No new encoded frames were captured to compare DDA, WGC, Windows 10, Windows 11, alternate displays or DPI. The retired behavior has no claimed supported configurations. There is no additional capture or reconstruction step in the fallback.

### Fullscreen, audio and remaining verification

Editor and Player fullscreen give the native video the full client area. Their themed controls float in a separate WPF popup and preserve the video viewport when hidden. The popup follows view visibility and window deactivation. The generated WPF smoke checks viewport dimensions, a decoded mpv frame, popup state and layout restoration for both Editor and standalone Player. Physical Player and Editor keyboard/mouse, taskbar coverage, ultrawide and mixed-DPI verification remain pending. Track name editors in Basic and Advanced are opened on demand. Enter and focus loss save, and Escape restores the displayed name. Endpoint identity and ordering use the same existing persistence paths. Microphone input and encoded stems need supervised testing.

Potential performance costs are one bounded card bitmap on Gallery open, a short Gallery animation, and normal libmpv/ffprobe preparation. Development diagnostics flush in the background and should be disabled for comparison. The four-way no-recorder, upstream v0.7.1, Personal v1.1.0 and final Wave 2 comparison requires a clean supervised run. No matched benchmark was run. The historical disposable alternate-drive updater fixture passed under #82. The current PR Windows quality workflow does not exercise the real installer fixture on this base, and this pass did not install or upgrade the owner's copy.

### Local verification

From the candidate worktree, run `dotnet restore QuickClip.slnx` and `dotnet build QuickClip.slnx -c Release --no-restore -p:QuickClipProfile=<profile> -p:RequireCaptureEngine=true`. Both strict builds produced the native capture DLL. Run `dotnet test QuickClip.slnx -c Release --no-build --no-restore -p:QuickClipProfile=<profile> --blame-hang-timeout 3m` with the environment and Upstream filter in the [Windows quality workflow](../.github/workflows/windows-quality.yml). Personal passed 881 with 22 opt-in skips. Upstream passed 74 with 14 opt-in skips. The focused Personal selection passed 123 with 3 skips. The separate Debug `FrontendWindowAcceptanceTests` run with `QUICKCLIP_TEST_FRONTEND_WINDOWS=1` passed both the WPF theme popup and native HUD style tests.

The isolated Debug `--editor-draft-smoke` phases were `presentation`, `write`, `read`, `replace` and `close-pending`, using generated H.264/AAC fixtures. The separate `--recording-settings-smoke-only` run rendered Audio and Hotkeys with recording inactive. The waveform test now uses the WPF collection and awaits STA completion with its existing ten-second bound. The CI timeout's cause remains unconfirmed. The test passed locally in the focused and full suites.

The local evidence directory is `.quickclip-local/agent/evidence/final` within the isolated implementation worktree. Keep the TRX files, WPF renders and decoded fixture frames private to that workspace. PR #88 lists the results for the exact candidate.

### Owner preview launch

Use the exact SHA reported for this candidate, after quitting that preview through its tray. This PowerShell requires the existing checkout, existing profile and main checkout's local SDK. It stops if the preview is missing, dirty, still running or the draft branch no longer matches the supplied SHA. The original preview path was unavailable during this pass.

```powershell
$pin = Read-Host 'Enter the full stabilization candidate SHA from draft PR #88'
if ($pin -notmatch '^[0-9a-f]{40}$') { throw 'A full candidate SHA is required.' }
$preview = 'E:\Stuff\Personal\Personal Projects\Codes\QuickClip-Wave2-Full'
$profile = Join-Path $preview '.quickclip-local\owner-full-preview'
$sdk = 'E:\Stuff\Personal\Personal Projects\Codes\QuickClip\.quickclip-local\dotnet'
foreach ($file in (Join-Path $preview '.git'), (Join-Path $profile 'settings\settings.json'), (Join-Path $sdk 'dotnet.exe')) {
    if (!(Test-Path -LiteralPath $file)) { throw "Required preview file missing ($file)." }
}
$top = git -C $preview rev-parse --show-toplevel
if ($LASTEXITCODE -or [IO.Path]::GetFullPath($top) -ne $preview) { throw 'Unexpected preview checkout.' }
$dirty = git -C $preview status --porcelain --untracked-files=all
if ($LASTEXITCODE -or $dirty) { throw 'The preview checkout must be clean.' }
if (Get-Process QuickClip -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$preview\*" }) {
    throw 'Quit this preview from its tray before switching.'
}
git -C $preview fetch --no-tags https://github.com/Yosef13103/QuickClip.git refs/heads/chatgpt/wave2-owner-polish
if ($LASTEXITCODE) { throw 'Could not fetch the draft branch.' }
$fetched = git -C $preview rev-parse FETCH_HEAD
if ($LASTEXITCODE -or $fetched -ne $pin) { throw 'The draft head differs from the requested candidate.' }
git -C $preview switch --detach $pin
if ($LASTEXITCODE) { throw 'Could not select the pinned candidate.' }
$previousProfile = $env:QUICKCLIP_PERSONAL_DATA_DIR
$previousSdk = $env:DOTNET_ROOT
Push-Location -LiteralPath $preview
try {
    $env:QUICKCLIP_PERSONAL_DATA_DIR = $profile
    $env:DOTNET_ROOT = $sdk
    & .\scripts\dev.ps1 -Configuration Release
}
finally {
    Pop-Location
    $env:QUICKCLIP_PERSONAL_DATA_DIR = $previousProfile
    $env:DOTNET_ROOT = $previousSdk
}
```

## Wave 2 release preparation

The public Wave 2 settings use the inline Custom palette expander. This reverses the designer presentation changes in `b6f99ac` for the four theme view files. Existing theme storage, picker refresh, semantic resources, scaling and rollback remain in use. The designer work stays available in history for branch C. When bringing C back, reconcile these four view files with C's designer activation and retain the current storage and picker behavior. Branch D has no dependency on this restoration.

See the [multi-version benchmark protocol](wave2-performance-protocol.md#interactive-multi-version-runner) for preparation, measurement, reporting and resume. Physical benchmarks still require the disposable signed-in Windows account, owner-supervised capture and uncontaminated workloads.

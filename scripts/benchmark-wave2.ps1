# PowerShell 7. See docs/wave2-performance-protocol.md.
param(
 [Parameter(Mandatory)][ValidateSet('Preflight','Prepare','Run','Report')][string]$Mode,
 [ValidateSet('Wave2','EpochsThreeWay')][string]$StudyKind='Wave2',
 [ValidatePattern('^[0-9a-f]{40}$')][string]$Development,
 [Parameter(Mandatory)][ValidatePattern('^[\w-]{1,32}$')][string]$Study,
 [ValidatePattern('^[0-9a-f]{40}$')][string]$Candidate,
 [ValidatePattern('^S-1-5-21-(\d+-){3}\d+$')][string]$DisposableSid,
 [ValidateSet('Controlled','Preferred')][string]$Comparison='Controlled',
 [ValidateSet('IdleVisible','IdleTray','ArmedNoGame','Game','Editor','Scan','Save','DiagnosticsOff','DiagnosticsOn')][string]$Scenario='IdleVisible',
 [ValidateRange(1,20)][int]$Repetitions=3,
 [ValidateRange(10,1800)][int]$Seconds=60,
 [ValidateRange(0,300)][int]$WarmupSeconds=15,
 [switch]$Nvidia,
 [switch]$PresentMon
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=Join-Path $repo ".quickclip-local/benchmarks/$Study"
$manifest=Join-Path $root 'study.json'
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$rootRepo=Split-Path -Parent (Split-Path -Parent $repo)
$sharedSdk=Join-Path $rootRepo '4. Shared/SDK/dotnet.exe'
if ($Mode -eq 'Preflight') {
    if ($StudyKind -ne 'EpochsThreeWay' -or !$Candidate -or !$Development) {
        throw 'Three-way preflight requires -StudyKind EpochsThreeWay, -Development and -Candidate exact 40-character SHAs.'
    }
    $actualDev=git -C (Join-Path $rootRepo '2. Development') rev-parse HEAD
    $actualEpoch=git -C $repo rev-parse HEAD
    if ($actualDev -ne $Development -or $actualEpoch -ne $Candidate) {
        throw "Revision mismatch. Development=$actualDev, Epochs=$actualEpoch; use exact current pins."
    }
    foreach ($path in @($sharedSdk,(Join-Path $repo 'scripts/measure-process-baseline.ps1'),
        (Join-Path $repo 'scripts/benchmark-video.ps1'))) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing prerequisite: $path" }
    }
    $devWorktree=Join-Path $rootRepo '2. Development'
    if (git -C $devWorktree status --porcelain -uno) { throw 'Development source worktree is dirty.' }
    if (git -C $repo status --porcelain -uno | Where-Object { $_ -notmatch '^ M scripts/benchmark-wave2.ps1$' }) {
        Write-Warning 'Epoch checkout contains changes beyond the benchmark script; prepare after checkpointing code.'
    }
    [pscustomobject]@{
        ReadyForPrepare=$true
        Original='swiiftex/QuickClip v0.7.1; pinned release/hash in this script'
        Development=$Development
        Epochs=$Candidate
        Sdk=(& $sharedSdk --version)
        PresentMon=@((Get-Item -LiteralPath 'C:\Program Files\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe' -ErrorAction SilentlyContinue).FullName)
        RequiresDisposableInteractiveWindowsAccount=$true
        RequiresMatchedCaptureSettingsAndManualAuthorization=$true
        FurMarkInstalled=[bool](Get-Command FurMark,FurMark2 -ErrorAction SilentlyContinue)
    } | Format-List
    return
}
function Read-Json($path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
function Write-Json($path,$data) {
    $temp="$path.$([guid]::NewGuid().ToString('N')).tmp"
    $data | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $temp -Encoding utf8
    [IO.File]::Move($temp,$path,$true)
}
function Hash($path) { (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() }
function Confirm($prompt) {
    if ((Read-Host "$prompt Type YES") -cne 'YES') { throw 'Cancelled by operator.' }
}
function Assert-Clear {
    if (Get-Process QuickClip,obs64,obs32 -ErrorAction SilentlyContinue) {
        throw 'Quit all QuickClip and OBS instances before measurement.'
    }
}
if ($Mode -eq 'Prepare') {
    if (!$Candidate -or (!$Development -and $StudyKind -eq 'EpochsThreeWay') -or
        !$DisposableSid -or $DisposableSid -eq $sid -or (Test-Path -LiteralPath $manifest)) {
        throw 'Use a new study, exact candidate SHA and a separate disposable user SID. Prepare from the owner account.'
    }
    Confirm 'Verify the SID belongs to a clean disposable Windows account.'
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $drive=Get-PSDrive ([IO.Path]::GetPathRoot($root).Substring(0,1))
    if ($drive.Free -lt 8GB) { throw 'Preparation requires 8 GiB free.' }
    $resolved=git -C $repo rev-parse "$Candidate^{commit}"
    if ($LASTEXITCODE -or $resolved -ne $Candidate) { throw 'Fetch the exact candidate first.' }
    $sdk=@($sharedSdk,(Join-Path $repo '.quickclip-local/dotnet/dotnet.exe'),(Join-Path (Split-Path -Parent $repo) 'dotnet/dotnet.exe'),$env:DOTNET_ROOT + '\dotnet.exe',(Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source) |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (!$sdk -or !(& $sdk --list-sdks | Where-Object { $_ -match '^10\.' })) { throw 'Install or provision the x64 .NET 10 SDK.' }
    $packages=@(
        @{Name='Original'; Sha='e9bccdc6ee38f1b7c29b7ee7ea8b260babcc2a3f'; Hash='ab9e8ed8d0a055490b22c8ba2883989c616900362b940e6c8b5303ef6e58e701'; Url='https://github.com/swiiftex/QuickClip/releases/download/v0.7.1/QuickClip-0.7.1-win-x64.zip'}
    )
    if ($StudyKind -eq 'Wave2') {
        $packages+=@{Name='Personal'; Sha='6de4ad684f4268b685c630d639977c439bf557ed'; Hash='af5b3dad346fee74e02490756536e016aaf9b27f20f072da3acb628993b08cf5'; Url='https://github.com/Yosef13103/QuickClip/releases/download/personal-v1.1.0/QuickClip-Personal-1.1.0-win-x64.zip'}
    }
    foreach ($package in $packages) {
        $zip=Join-Path $root "$($package.Name).zip"
        if (!(Test-Path -LiteralPath $zip)) {
            Invoke-WebRequest -Uri $package.Url -OutFile "$zip.partial"
            if ((Hash "$zip.partial") -ne $package.Hash) { throw "Invalid download hash for $($package.Name)." }
            Move-Item -LiteralPath "$zip.partial" -Destination $zip
        }
        if ((Hash $zip) -ne $package.Hash) { throw "Invalid cached hash for $($package.Name)." }
    }
    $targets=if ($StudyKind -eq 'EpochsThreeWay') {
        @(@{Name='Development'; Sha=$Development}, @{Name='Epochs'; Sha=$Candidate})
    } else { @(@{Name='Candidate'; Sha=$Candidate}) }
    foreach ($target in $targets) {
        $sha=$target.Sha
        $resolved=git -C $repo rev-parse "$sha^{commit}"
        if ($LASTEXITCODE -ne 0 -or $resolved -ne $sha) {
            throw "Missing exact $($target.Name) source revision $sha."
        }
        $source=Join-Path $root "source-$sha"
        if (!(Test-Path -LiteralPath (Join-Path $source '.git'))) {
            git -C $repo worktree add --detach $source $sha
            if ($LASTEXITCODE) { throw 'Cannot create isolated benchmark worktree.' }
        }
        if ((git -C $source rev-parse HEAD) -ne $sha -or (git -C $source status --porcelain)) {
            throw "Benchmark source for $($target.Name) is not the pinned clean commit."
        }
        $output=Join-Path $root "publish-$($target.Name)-$([guid]::NewGuid().ToString('N'))"
        & (Join-Path $source 'scripts/get-deps.ps1')
        if ($LASTEXITCODE -ne 0) { throw "Could not resolve dependencies for $($target.Name)." }
        & $sdk publish (Join-Path $source 'src/QuickClip/QuickClip.csproj') -c Release -r win-x64 --self-contained true -o $output -p:QuickClipProfile=Personal -p:RequireCaptureEngine=true
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $output 'QuickClipCapture.dll'))) {
            throw "Strict packaging failed for $($target.Name)."
        }
        $zip=Join-Path $root "$($target.Name).zip"
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
        $packages+=@{Name=$target.Name;Sha=$sha;Hash=(Hash $zip);SourceWorktree=$source}
    }
    Write-Json $manifest @{Schema=2; StudyKind=$StudyKind; Owner=$sid; Disposable=$DisposableSid;
        Builds=$packages; Sdk=(& $sdk --version)}
    return
}
$studyData=Read-Json $manifest
$runs=Join-Path $root 'runs'
if ($Mode -eq 'Run') {
    if ($sid -ne $studyData.Disposable -or $sid -eq $studyData.Owner -or ![Environment]::UserInteractive) {
        throw 'Run in the registered disposable signed-in Windows account.'
    }
    if ($Scenario -eq 'DiagnosticsOn') {
        Write-Host 'Skip builds without equivalent diagnostics. Hold timing sampling and other settings constant.'
    }
    Confirm 'Use a clean disposable profile and approved nonprivate workload. Stop other recorders and all compilation or encoding.'
    Assert-Clear
    $group=Join-Path $runs "$Comparison-$Scenario"
    New-Item -ItemType Directory -Path $group -Force | Out-Null
    $lock=[IO.File]::Open((Join-Path $root 'run.lock'),'OpenOrCreate','ReadWrite','None')
    try {
        $workload=Read-Host 'Stable workload and settings identifier'
        if (!$workload.Trim()) { throw 'Supply a workload identifier.' }
        $planPath=Join-Path $group 'plan.json'
        if (!(Test-Path -LiteralPath $planPath)) {
            Write-Json $planPath @{Workload=$workload;Seconds=$Seconds;Warmup=$WarmupSeconds;Nvidia=[bool]$Nvidia;PresentMon=[bool]$PresentMon;
                OS=[Environment]::OSVersion.VersionString;CPU=@(Get-CimInstance Win32_Processor | Select-Object Name);
                GPU=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)}
        }
        $plan=Read-Json $planPath
        if ($plan.Workload -ne $workload -or $plan.Seconds -ne $Seconds -or
            $plan.Warmup -ne $WarmupSeconds -or $plan.Nvidia -ne [bool]$Nvidia -or
            [bool]$plan.PresentMon -ne [bool]$PresentMon) {
            throw 'Resume with matching options and workload.'
        }
        for ($round=1; $round -le $Repetitions; $round++) {
            $sequence=@($studyData.Builds)
            if ($Scenario -eq 'Game') { $sequence=@(@{Name='Control';Sha=('0'*40)})+$sequence }
            if ($round % 2 -eq 0) { [array]::Reverse($sequence) }
            foreach ($build in $sequence) {
                $label="$($build.Name)-$round"
                $result=Join-Path $group "$label.json"
                if (Test-Path -LiteralPath $result) { continue }
                $decision=Read-Host "$label / $Scenario. RUN, SKIP (unsupported/non-equivalent) or STOP"
                if ($decision -eq 'STOP') { return }
                if ($decision -eq 'SKIP') {
                    Write-Json $result @{Variant=$build.Name;Round=$round;Status='Unsupported';Notes=(Read-Host 'Reason')}
                    continue
                }
                if ($decision -ne 'RUN') { throw 'Choose RUN, SKIP or STOP.' }
                Assert-Clear
                $attempt=Join-Path $group "$label-$([guid]::NewGuid().ToString('N'))"
                $clips=Join-Path $attempt 'clips'
                New-Item -ItemType Directory -Path $clips -Force | Out-Null
                $original=$build.Name -eq 'Original'
                $process=$null
                $startup=$null
                $shutdown=$null
                $priorProfile=$env:QUICKCLIP_PERSONAL_DATA_DIR
                $priorDiag=$env:QUICKCLIP_DEV_DIAGNOSTICS
                try {
                    if ($build.Name -eq 'Control') { $target=[int](Read-Host 'Approved game PID') }
                    else {
                        $zip=Join-Path $root "$($build.Name).zip"
                        if ((Hash $zip) -ne $build.Hash) { throw 'Benchmark archive changed.' }
                        $bin=Join-Path $attempt 'bin'
                        Expand-Archive -LiteralPath $zip -DestinationPath $bin
                        $exe=@(Get-ChildItem -LiteralPath $bin -Recurse -File -Filter QuickClip.exe)
                        if ($exe.Count -ne 1) { throw 'Expected one stock QuickClip.exe.' }
                        $env:QUICKCLIP_PERSONAL_DATA_DIR=Join-Path $attempt 'profile'
                        $env:QUICKCLIP_DEV_DIAGNOSTICS=if ($Scenario -eq 'DiagnosticsOn') {'1'} else {'0'}
                        $settingsPath=if ($original) { Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'QuickClip/settings.json' }
                            else { Join-Path $env:QUICKCLIP_PERSONAL_DATA_DIR 'settings/settings.json' }
                        if ($original -and (Test-Path -LiteralPath $settingsPath)) {
                            $prior=Read-Json $settingsPath
                            $prefix=[IO.Path]::GetFullPath($runs)+[IO.Path]::DirectorySeparatorChar
                            if (!$prior.ClipsFolder -or !([IO.Path]::GetFullPath($prior.ClipsFolder).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase))) {
                                throw 'Existing original settings are outside this study. Use a clean disposable account.'
                            }
                            Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $attempt 'previous-settings.json')
                        }
                        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settingsPath) | Out-Null
                        $safe=@{RecordingEnabled=$false;RecordMic=$false;MicDeviceIds=@();HotkeyKey=0;CheckForUpdates=$false;
                            EnableReplayOnStartup=$false;ReplayOnboardingCompleted=$true;ClipsFolder=$clips;Games=@{AutoScan=$false}}
                        Write-Json $settingsPath $safe
                        Write-Json (Join-Path $attempt 'launch-settings.json') $safe
                        $timer=[Diagnostics.Stopwatch]::StartNew()
                        $process=Start-Process -FilePath $exe[0].FullName -WorkingDirectory $exe[0].DirectoryName -PassThru
                        $target=$process.Id
                        if ($Scenario -eq 'IdleVisible') {
                            Read-Host 'Press Enter when the comparable Gallery is visible and ready' | Out-Null
                            $startup=$timer.Elapsed.TotalMilliseconds
                        }
                    }
                    $gameId=if ($Scenario -eq 'Game' -and $build.Name -ne 'Control') { [int](Read-Host 'Approved game PID') } else { $null }
                    Confirm 'Apply matched scenario settings. Any recording/save must be deliberate and use the scratch clips folder. Prepare independent frametime collection for Game.'
                    $notes=Read-Host 'Actual settings, backend, workload and limitations'
                    if (!$notes.Trim()) { throw 'Record scenario details.' }
                    if ($process) { Write-Json (Join-Path $attempt 'measured-settings.json') (Read-Json $settingsPath) }
                    $sample=@{TargetProcessId=$target;BuildSha=$build.Sha;Variant=$(if ($original) {'Upstream'} else {'Personal'});
                        Scenario=$(if ($build.Name -eq 'Control') {'GameNoRecorder'} else {$Scenario});
                        Seconds=$Seconds;WarmupSeconds=$WarmupSeconds;RunLabel=$label;Nvidia=$Nvidia;OutputDirectory=(Join-Path $attempt 'process')}
                    $job=$null
                    $presentJob=$null
                    $presentFile=Join-Path $attempt 'presentmon-v1.csv'
                    try {
                        if ($PresentMon -and $Scenario -eq 'Game') {
                            $presentExe='C:\Program Files\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe'
                            if (!(Test-Path -LiteralPath $presentExe -PathType Leaf)) {
                                throw 'PresentMon was requested but the pinned installed executable is missing.'
                            }
                            $presentPid=if ($build.Name -eq 'Control') {$target} else {$gameId}
                            $presentJob=Start-Job -ScriptBlock {
                                param($exe,$targetProcessId,$delay,$duration,$output)
                                Start-Sleep -Seconds $delay
                                & $exe --process_id $targetProcessId --session_name "QCEpochsBench-$targetProcessId" --no_track_input --no_console_stats --v1_metrics --timed $duration --terminate_after_timed --output_file $output 2>&1 | Out-Null
                                if ($LASTEXITCODE -ne 0) {throw "PresentMon returned $LASTEXITCODE."}
                            } -ArgumentList $presentExe,$presentPid,$WarmupSeconds,$Seconds,$presentFile
                        }
                        if ($gameId) {
                            $gameSample=$sample.Clone()
                            $gameSample.TargetProcessId=$gameId
                            $gameSample.Nvidia=$false
                            $gameSample.OutputDirectory=Join-Path $attempt 'game'
                            $job=Start-Job -ScriptBlock { param($script,$options) & $script @options } -ArgumentList (Join-Path $PSScriptRoot 'measure-process-baseline.ps1'),$gameSample
                        }
                        & (Join-Path $PSScriptRoot 'measure-process-baseline.ps1') @sample
                        if ($job) { Receive-Job $job -Wait -ErrorAction Stop }
                        if ($presentJob) {
                            try { Receive-Job $presentJob -Wait -ErrorAction Stop | Out-Null }
                            catch { Write-Warning "PresentMon evidence unavailable: $($_.Exception.Message)" }
                        }
                    } finally {
                        if ($job) { Remove-Job $job -Force }
                        if ($presentJob) { Remove-Job $presentJob -Force }
                    }
                    $extra=@{}
                    if ($Scenario -eq 'Game') {
                        $csv=if ($PresentMon -and (Test-Path -LiteralPath $presentFile)) {
                            $presentFile
                        } else { Read-Host 'Independent numeric frametime CSV with FrameMs column, or blank' }
                        if ($csv) {
                            $rows=@(Import-Csv -LiteralPath $csv)
                            $frames=@($rows | ForEach-Object {
                                $cell=if ($_.PSObject.Properties['FrameMs']) {$_.FrameMs}
                                    elseif ($_.PSObject.Properties['MsBetweenPresents']) {$_.MsBetweenPresents}
                                    else {$null}
                                $number=0.0
                                if ($cell -and [double]::TryParse($cell,[Globalization.NumberStyles]::Float,
                                    [Globalization.CultureInfo]::InvariantCulture,[ref]$number) -and
                                    $number -gt 0 -and [double]::IsFinite($number)) {$number}
                            })
                            if ($frames.Count -lt 30) {throw 'No usable frame-time column or insufficient frames in independent CSV.' }
                            $frames | ForEach-Object { [pscustomobject]@{FrameMs=$_} } | Export-Csv -LiteralPath (Join-Path $attempt 'frames.csv') -NoTypeInformation
                            $orderedFrames=@($frames | Sort-Object)
                            $extra.Fps=1000/($frames | Measure-Object -Average).Average
                            $extra.OnePercentLowFps=1000/($orderedFrames | Select-Object -Last ([Math]::Ceiling($frames.Count*.01)) | Measure-Object -Average).Average
                            $extra.FrameP95Ms=$orderedFrames[[int][Math]::Ceiling(.95*$frames.Count)-1]
                            $extra.FrameP99Ms=$orderedFrames[[int][Math]::Ceiling(.99*$frames.Count)-1]
                        }
                    }
                    if ($process -and $Scenario -in 'Game','Save') {
                        foreach ($metric in 'CaptureDrops','SaveFeedbackMs','SaveFileReadyMs') {
                            $value=Read-Host "$metric from independent observation, or blank"
                            if ($value) { $extra[$metric]=[double]$value }
                        }
                        $clip=Get-ChildItem -LiteralPath $clips -Recurse -File -Filter '*.mp4' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
                        if ($clip) {
                            $probe=Get-ChildItem -LiteralPath $bin -Recurse -File -Filter ffprobe.exe | Select-Object -First 1
                            if ($probe) {
                                $encoded=& $probe.FullName -v error -show_entries 'format=duration:stream=codec_name,codec_type,width,height,avg_frame_rate,channels,sample_rate' -of json $clip.FullName
                                if ($LASTEXITCODE) { throw 'FFprobe rejected the scratch recording.' }
                                $encoded | Set-Content -LiteralPath (Join-Path $attempt 'encoded-streams.json')
                            }
                        }
                    }
                    if ($process -and !$process.HasExited) {
                        Read-Host 'Press Enter, then immediately Quit this benchmark app from its tray' | Out-Null
                        $timer=[Diagnostics.Stopwatch]::StartNew()
                        if (!$process.WaitForExit(30000)) { throw 'Benchmark app did not quit in 30 seconds.' }
                        $shutdown=$timer.Elapsed.TotalMilliseconds
                    }
                    $summary=Read-Json (Join-Path $sample.OutputDirectory 'summary.json')
                    $metrics=@{}
                    $prefix=if ($build.Name -eq 'Control') {'Game'} else {'Recorder'}
                    foreach ($metric in $summary.Metrics.PSObject.Properties) { $metrics["$prefix.$($metric.Name)"]=$metric.Value.Median }
                    if ($gameId) {
                        $game=Read-Json (Join-Path $attempt 'game/summary.json')
                        foreach ($metric in $game.Metrics.PSObject.Properties) { $metrics["Game.$($metric.Name)"]=$metric.Value.Median }
                    }
                    $gpuSamples=Join-Path $sample.OutputDirectory 'gpu-device.csv'
                    if (Test-Path -LiteralPath $gpuSamples) {
                        foreach ($adapter in (Import-Csv -LiteralPath $gpuSamples | Group-Object AdapterIndex)) {
                            foreach ($field in 'GpuPercent','EncodePercent','DecodePercent','DeviceMemoryMiB','GraphicsMHz','PowerW','TemperatureC','MemoryMHz') {
                                $values=@($adapter.Group | ForEach-Object {
                                    $number=0.0
                                    if ([double]::TryParse($_.$field,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$number)) { $number }
                                } | Sort-Object)
                                if ($values.Count) {
                                    $n=$values.Count
                                    $metrics["DeviceGPU$($adapter.Name).$field"]=($values[[int][Math]::Floor(($n-1)/2)]+$values[[int][Math]::Floor($n/2)])/2
                                }
                            }
                        }
                    }
                    foreach ($metric in $extra.Keys) { $metrics[$metric]=$extra[$metric] }
                    $metrics.StartupObservedMs=$startup
                    $metrics.ShutdownObservedMs=$shutdown
                    Write-Json $result @{Variant=$build.Name;Round=$round;Status='Complete';Notes=$notes;Build=$build;
                        Metrics=$metrics;Evidence=[IO.Path]::GetFileName($attempt)}
                } finally {
                    if ($process -and !$process.HasExited) {
                        if (!$process.CloseMainWindow() -or !$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
                    }
                    if ($process) { $process.Dispose() }
                    $env:QUICKCLIP_PERSONAL_DATA_DIR=$priorProfile
                    $env:QUICKCLIP_DEV_DIAGNOSTICS=$priorDiag
                }
            }
        }
    } finally { $lock.Dispose() }
}
$report=@('# QuickClip benchmark','','Statistics are across repetition medians or individual entered values. GPU counters are device-wide. Startup/shutdown include operator reaction. Missing data are unmeasured.','')
foreach ($group in (Get-ChildItem -LiteralPath $runs -Directory)) {
    $items=@(Get-ChildItem -LiteralPath $group.FullName -File -Filter '*.json' | Where-Object Name -ne 'plan.json' | ForEach-Object { Read-Json $_.FullName })
    $names=@($items | Where-Object Status -eq Complete | ForEach-Object {$_.Metrics.PSObject.Properties.Name} | Sort-Object -Unique)
    $report+="## $($group.Name)"
    $report+='| Metric | Variant | n | Median | p95 | Delta | Delta % |','| --- | --- | ---: | ---: | ---: | ---: | ---: |'
    foreach ($metric in $names) {
        $baseline=if ($group.Name -like '*-Game' -and ($metric -like 'Game.*' -or
            $metric -like 'DeviceGPU*' -or $metric -in 'Fps','OnePercentLowFps','FrameP95Ms','FrameP99Ms')) {
            'Control'
        } elseif ($studyData.StudyKind -eq 'EpochsThreeWay') { 'Development' } else { 'Personal' }
        $reference=$null
        $variants=@($baseline) + @($studyData.Builds | ForEach-Object {$_.Name} |
            Where-Object { $_ -ne $baseline })
        if ($group.Name -like '*-Game' -and $baseline -ne 'Control') { $variants+=@('Control') }
        foreach ($variant in ($variants | Select-Object -Unique)) {
            if ($variant -eq 'Control' -and $group.Name -notlike '*-Game') { continue }
            $values=@($items | Where-Object { $_.Variant -eq $variant -and $_.Status -eq 'Complete' } |
                ForEach-Object {$_.Metrics.$metric} | Where-Object {$null -ne $_} | Sort-Object)
            $n=$values.Count
            if (!$n) { $report+="| $metric | $variant | 0 | — | — | — | — |"; continue }
            $mid=($values[[int][Math]::Floor(($n-1)/2)]+$values[[int][Math]::Floor($n/2)])/2
            $p95=$values[[int][Math]::Ceiling(.95*$n)-1]
            if ($variant -eq $baseline) { $reference=$mid }
            $delta=if ($null -ne $reference) {'{0:F3}' -f ($mid-$reference)} else {'—'}
            $percent=if ($reference) {'{0:F2}' -f (100*($mid-$reference)/$reference)} else {'—'}
            $report+="| $metric | $variant | $n | $('{0:F3}' -f $mid) | $('{0:F3}' -f $p95) | $delta | $percent |"
        }
    }
    $report+=''
    foreach ($item in $items) { $report+="- $($item.Variant) $($item.Round) [$($item.Status)] $($item.Notes -replace '[\r\n]+',' ')" }
    $report+=''
}
$report | Set-Content -LiteralPath (Join-Path $root 'report.md') -Encoding utf8

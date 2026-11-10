<#
.SYNOPSIS
    Build, check and launch an isolated QuickClip Personal development copy.
.DESCRIPTION
    Output and the default persistent profile live under the checkout's ignored bin folder.
    -Portable produces a framework-dependent package that requires the .NET 10 Desktop Runtime.
    -Watch rebuilds on source changes and restarts its own Personal app after it exits.
#>
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Build')]
    [switch]$BuildOnly,
    [Parameter(ParameterSetName = 'Build')]
    [switch]$Portable,
    [Parameter(Mandatory = $true, ParameterSetName = 'Watch')]
    [switch]$Watch,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workspaceSdk = Join-Path $root '.quickclip-local\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $workspaceSdk) { $workspaceSdk }
          else { (Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue).Source }
if (!$dotnet -or -not (& $dotnet --list-sdks | Where-Object { $_ -match '^10\.' })) {
    throw 'The x64 .NET 10 SDK is required. Install it or extract it to .quickclip-local\dotnet.'
}

foreach ($relative in @('deps\libmpv-2.dll', 'deps\ffmpeg\ffmpeg.exe', 'deps\ffmpeg\ffprobe.exe',
                        'deps\ffmpeg-dev\include\libavcodec\avcodec.h', 'deps\ffmpeg-dev\lib\avcodec.lib')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Missing $relative. Run scripts\get-deps.ps1 to prepare the pinned native dependencies."
    }
}

$devRoot = Join-Path $root 'bin\quickclip-personal'
$project = Join-Path $root 'src\QuickClip\QuickClip.csproj'

function Get-RunningBuild([string]$directory) {
    $exe = Join-Path $directory 'QuickClip.exe'
    Get-Process -Name QuickClip -ErrorAction SilentlyContinue | Where-Object Path -eq $exe
}

function Build-PersonalOutput([string]$directory) {
    if (Get-RunningBuild $directory) {
        throw 'Quit this QuickClip Personal build from its tray menu before rebuilding it.'
    }
    $buildArgs = @($project, '-c', $Configuration, '-o', $directory, '-p:QuickClipProfile=Personal', '-p:RequireCaptureEngine=true')
    if ($Portable) { & $dotnet publish @buildArgs --no-self-contained "-p:BaseOutputPath=$(Join-Path $devRoot 'publish-build')/" }
    else { & $dotnet build @buildArgs }
    if ($LASTEXITCODE -ne 0) { throw "QuickClip Personal build failed with exit code $LASTEXITCODE." }

    foreach ($tool in 'ffmpeg', 'ffprobe') {
        & (Join-Path $directory "ffmpeg\$tool.exe") -version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Bundled $tool failed to load." }
    }
    & $dotnet (Join-Path $directory 'QuickClip.dll') --check-personal-runtime
    if ($LASTEXITCODE -ne 0) { throw "QuickClip Personal runtime check failed with exit code $LASTEXITCODE." }
    Write-Host "QuickClip Personal is ready at $(Join-Path $directory 'QuickClip.exe')"
}

function Build-Personal([string]$directory) {
    New-Item -ItemType Directory -Path $devRoot -Force | Out-Null
    try {
        $buildLock = [IO.File]::Open((Join-Path $devRoot 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch [IO.IOException] {
        throw 'Another QuickClip launcher is building, or the build lock cannot be opened.'
    }
    try { Build-PersonalOutput $directory }
    finally { $buildLock.Dispose() }
}

function Start-Personal([string]$directory) {
    Start-Process -FilePath (Join-Path $directory 'QuickClip.exe') -WorkingDirectory $directory -PassThru
}

function Get-SourceFiles([string]$directory) {
    Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue
    foreach ($child in Get-ChildItem -LiteralPath $directory -Directory -ErrorAction SilentlyContinue) {
        if ($child.Name -notin @('bin', 'obj') -and -not ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Get-SourceFiles $child.FullName
        }
    }
}

function Get-SourceStamp {
    $files = @(Get-SourceFiles (Join-Path $root 'src'))
    $files += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'build-capture.ps1')
    ($files | Sort-Object FullName | ForEach-Object {
        "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
    }) -join "`n"
}

function Watch-Personal {
    $slots = @((Join-Path $devRoot 'watch-a'), (Join-Path $devRoot 'watch-b'))
    New-Item -ItemType Directory -Path $devRoot -Force | Out-Null
    try {
        $lock = [IO.File]::Open((Join-Path $devRoot 'watch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch [IO.IOException] {
        throw 'Another QuickClip Auto-Rebuild watcher is already running, or its lock cannot be opened.'
    }

    try {
        foreach ($slot in $slots) {
            if (Get-RunningBuild $slot) { throw 'Quit the previous Auto-Rebuild app from its tray menu before starting another watcher.' }
        }
        Write-Host 'Watching QuickClip source files. Ctrl+C stops watching and leaves the app open.'
        Write-Host 'Restarting the app clears any replay held in memory.'
        $seen = $null
        $current = -1
        $running = $null
        $launched = $null
        while ($true) {
            $stamp = Get-SourceStamp
            if ($stamp -cne $seen) {
                Start-Sleep -Milliseconds 500
                if ((Get-SourceStamp) -cne $stamp) { continue }
                $seen = $stamp
                $next = if ($current -eq 0) { 1 } else { 0 }
                try {
                    Build-Personal $slots[$next]
                    if ($running -and -not $running.HasExited) {
                        Start-Process -FilePath (Join-Path $slots[$current] 'QuickClip.exe') -ArgumentList '--quit' -WindowStyle Hidden -Wait
                        if (-not $running.WaitForExit(10000)) {
                            throw 'The running app did not exit. Finish any open prompt and save a source change to retry.'
                        }
                    }
                    $running = Start-Personal $slots[$next]
                    $launched = [DateTime]::UtcNow
                    $current = $next
                }
                catch {
                    Write-Warning $_.Exception.Message
                    Write-Host 'Waiting for another source change.'
                }
            }
            if ($running -and $running.HasExited) {
                if (($running.ExitTime.ToUniversalTime() - $launched).TotalSeconds -lt 2) {
                    Write-Warning 'QuickClip exited during startup. Automatic restart is paused until the next source change.'
                    $running = $null
                }
                else {
                    Write-Host 'QuickClip exited. Restarting the last successful build.'
                    $running = Start-Personal $slots[$current]
                    $launched = [DateTime]::UtcNow
                }
            }
            Start-Sleep -Seconds 1
        }
    }
    finally {
        $lock.Dispose()
    }
}

if (!$Watch) {
    $output = Join-Path $devRoot $(if ($Portable) { 'portable' } else { 'dev' })
    Build-Personal $output
    if ($BuildOnly) { return }
}

$previousRoot = $env:DOTNET_ROOT
$previousProfile = $env:QUICKCLIP_PERSONAL_DATA_DIR
try {
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    if (!$previousProfile) {
        $env:QUICKCLIP_PERSONAL_DATA_DIR = Join-Path $devRoot $(if ($Watch) { 'watch-profile' } else { 'profile' })
    }
    if ($Watch) { Watch-Personal }
    else { Start-Personal $output | Out-Null }
}
finally {
    $env:DOTNET_ROOT = $previousRoot
    $env:QUICKCLIP_PERSONAL_DATA_DIR = $previousProfile
}

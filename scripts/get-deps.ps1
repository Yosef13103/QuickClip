<#
.SYNOPSIS
    Downloads the third-party binaries QuickClip needs into ./deps.

.DESCRIPTION
    - FFmpeg + FFprobe (pinned FFmpeg-Builds shared GPL build) -> deps/ffmpeg/
    - libmpv (shinchiro build, used for the preview player)          -> deps/libmpv-2.dll

    The build copies everything in ./deps next to QuickClip.exe.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar

$root = Split-Path -Parent $PSScriptRoot
$deps = Join-Path $root 'deps'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("quickclip-deps-" + [guid]::NewGuid().ToString('N'))
if ((Test-Path -LiteralPath $deps) -and ((Get-Item -LiteralPath $deps -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The dependency directory must not be a reparse point.'
}
New-Item -ItemType Directory -Force -Path $deps, $tmp | Out-Null

function Save-Asset([uri]$url, [string]$sha256) {
    $name = $url.Segments[-1]
    $file = Join-Path $tmp $name
    Write-Host "Downloading $name..."
    Invoke-WebRequest $url -UseBasicParsing -OutFile $file -Headers @{ 'User-Agent' = 'QuickClip' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $sha256) {
        throw "SHA-256 mismatch for $name."
    }
    return $file
}

function Remove-DependencyDirectory([string]$path, [string]$parent) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $directory = Get-Item -LiteralPath $path -Force
    $parentDirectory = Get-Item -LiteralPath $parent -Force
    if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($parentDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $directory.Parent.FullName -ne $parentDirectory.FullName.TrimEnd([char[]]'\/')) {
        throw "Unsafe dependency cleanup target: $path"
    }
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}

try {
    # --- FFmpeg ----------------------------------------------------------------
    # Binaries go next to the app; headers/import libs (same build) are for compiling the capture engine.
    $ffDir = Join-Path $deps 'ffmpeg'
    $ffDev = Join-Path $deps 'ffmpeg-dev'
    if ($Force -or -not (Test-Path (Join-Path $ffDir 'ffmpeg.exe')) -or -not (Test-Path (Join-Path $ffDev 'include\libavcodec\avcodec.h'))) {
        $zip = Save-Asset 'https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-11-10-12-39/ffmpeg-n9.0.2-48-gd4070a14ce-win64-gpl-shared-9.0.zip' `
            '33e50d43c978337588f143f1b8e88d332173827934d664eb960db30a7de8037c'
        $out = Join-Path $tmp 'ffmpeg'
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        tar -xf $zip -C $out
        if ($LASTEXITCODE -ne 0) { throw 'Extracting FFmpeg failed.' }

        $bin = Get-ChildItem -Path $out -Recurse -Directory -Filter bin | Select-Object -First 1
        Remove-DependencyDirectory $ffDir $deps
        New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
        Get-ChildItem $bin.FullName -File |
            Where-Object { $_.Name -ne 'ffplay.exe' } |
            Copy-Item -Destination $ffDir
        Copy-Item (Get-ChildItem -Path $out -Recurse -Filter LICENSE.txt | Select-Object -First 1).FullName (Join-Path $ffDir 'LICENSE.txt') -ErrorAction SilentlyContinue

        Remove-DependencyDirectory $ffDev $deps
        New-Item -ItemType Directory -Force -Path $ffDev | Out-Null
        Copy-Item (Join-Path $bin.Parent.FullName 'include') $ffDev -Recurse
        Copy-Item (Join-Path $bin.Parent.FullName 'lib') $ffDev -Recurse
        Write-Host "FFmpeg -> $ffDir (headers -> $ffDev)"
    } else {
        Write-Host 'FFmpeg already present (use -Force to update).'
    }

    # --- Inno Setup (compiler for the installer, from its NuGet package) ---------
    $inno = Join-Path $deps 'innosetup'
    if ($Force -or -not (Test-Path (Join-Path $inno 'ISCC.exe'))) {
        $version = '6.7.3'
        $package = Save-Asset "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$version/tools.innosetup.$version.nupkg" `
            'f780898e402ff80612cc8d9fcb8c6e02932bd1cb4c900ffdaa31f9341cfb49f4'
        $out = Join-Path $tmp 'innosetup'
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        tar -xf $package -C $out
        if ($LASTEXITCODE -ne 0) { throw 'Extracting Inno Setup failed.' }
        Remove-DependencyDirectory $inno $deps
        Copy-Item (Join-Path $out 'tools') $inno -Recurse
        Write-Host "Inno Setup -> $inno"
    } else {
        Write-Host 'Inno Setup already present (use -Force to update).'
    }

    # --- libmpv ----------------------------------------------------------------
    $mpvDll = Join-Path $deps 'libmpv-2.dll'
    if ($Force -or -not (Test-Path $mpvDll)) {
        $archive = Save-Asset 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261110/mpv-dev-x86_64-20261110-git-a61f6c3abd.7z' `
            'f07b97a72f2394a7798d6e1d36d952bbfd63577e25cef051f716751ea5dc9688'
        $out = Join-Path $tmp 'mpv'
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        tar -xf $archive -C $out
        if ($LASTEXITCODE -ne 0) {
            $7z = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
            if (-not (Test-Path $7z)) { throw 'Extracting libmpv failed (needs Windows 11 tar or 7-Zip).' }
            & $7z x $archive "-o$out" -y | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Extracting libmpv with 7-Zip failed.' }
        }
        $dll = Get-ChildItem -Path $out -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
        if (-not $dll) { throw 'libmpv-2.dll not found in the archive.' }
        Copy-Item $dll.FullName $mpvDll -Force
        Write-Host "libmpv -> $mpvDll"
    } else {
        Write-Host 'libmpv already present (use -Force to update).'
    }
}
finally {
    Remove-DependencyDirectory $tmp ([IO.Path]::GetTempPath())
}

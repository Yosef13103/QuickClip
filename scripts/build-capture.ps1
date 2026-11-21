<#
.SYNOPSIS
    Builds the native recording engine (QuickClipCapture.dll) into src\QuickClip.Capture\bin.

.DESCRIPTION
    Needs Visual Studio's C++ tools and the FFmpeg headers from scripts\get-deps.ps1 (deps\ffmpeg-dev).
    The QuickClip project runs this automatically when the engine sources change.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src\QuickClip.Capture'
$out = Join-Path $src 'bin'
$ffdev = Join-Path $root 'deps\ffmpeg-dev'
if (-not (Test-Path (Join-Path $ffdev 'include\libavcodec\avcodec.h'))) { throw 'FFmpeg headers are missing; run scripts\get-deps.ps1.' }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
if (-not $vs) { throw 'Visual Studio C++ build tools were not found (needed to build the recording engine).' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$vsVersion = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion
if ($vsVersion -notmatch '^(\d+)\.') { throw "Cannot determine Visual Studio version: $vsVersion" }
# The pinned Win10 C++/WinRT projection requires legacy coroutines on VS 2019/2022.
# VS 2026 (MSVC 14.50+) rejects <experimental/coroutine>, so compile the isolated
# WinRT translation unit with standard C++20 coroutines instead.
$groupCoroutineMode = if ([int]$Matches[1] -ge 18) { '/std:c++20' } else { '/await /std:c++17' }
Write-Host "Compiling WinRT group with $groupCoroutineMode (Visual Studio $vsVersion)"

$sources = (Get-ChildItem $src -Filter *.cpp | Where-Object Name -ne 'GroupCapture.cpp' | ForEach-Object { "`"$($_.FullName)`"" }) -join ' '
$libs = 'avcodec.lib avformat.lib avfilter.lib avutil.lib swresample.lib dxgi.lib d3d11.lib d3dcompiler.lib runtimeobject.lib windowsapp.lib ole32.lib avrt.lib mmdevapi.lib winmm.lib user32.lib dwmapi.lib gdi32.lib'
$commands = @(
    "call `"$vcvars`" >nul 2>nul",
    "cl /nologo /O2 /EHsc $groupCoroutineMode /MT /W4 /DUNICODE /D_UNICODE /external:I `"$ffdev\include`" /external:W0 /c /Fo`"$out\GroupCapture.obj`" `"$src\GroupCapture.cpp`"",
    "cl /nologo /O2 /EHsc /std:c++20 /MT /W4 /DUNICODE /D_UNICODE /external:I `"$ffdev\include`" /external:W0 /LD /Fo`"$out\\`" $sources `"$out\GroupCapture.obj`" /link /OUT:`"$out\QuickClipCapture.dll`" /IMPLIB:`"$out\QuickClipCapture.lib`" /LIBPATH:`"$ffdev\lib`" $libs"
) -join ' && '
cmd /c $commands
if ($LASTEXITCODE -ne 0) { throw 'Building the recording engine failed.' }

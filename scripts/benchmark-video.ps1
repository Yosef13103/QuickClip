<#
.SYNOPSIS
    Explicit, bounded video-only benchmark. Captures the chosen desktop into a five-second RAM ring; saves no media.
.DESCRIPTION
    Writes diagnostic JSON and optional nvidia-smi samples under the ignored workspace .quickclip-local directory.
    Does not read user settings, run audio, change device routing or change display/driver settings.
    Quit any other QuickClip recording instance before running. Launch the regular app separately afterwards.
#>
[CmdletBinding()]
param(
    [switch]$CaptureDesktop,
    [ValidateRange(5,30)][int]$Seconds = 10,
    [ValidateRange(15,240)][int]$Fps = 60,
    [ValidateRange(0,4320)][int]$Height = 0,
    [ValidateSet('hevc_nvenc','h264_nvenc')][string]$Encoder = 'hevc_nvenc',
    [ValidateSet('p4','p3','p2')][string]$Preset = 'p4',
    [ValidateSet('Automatic','DesktopDuplication','GraphicsCapture')][string]$Backend = 'DesktopDuplication',
    [ValidateRange(500,250000)][int]$VideoKbps = 20000,
    [switch]$AutoVideoBitrate,
    [string]$Monitor = '',
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
if (!$CaptureDesktop) { throw 'Desktop capture requires -CaptureDesktop. No media or audio will be saved.' }
if ($AutoVideoBitrate -and $PSBoundParameters.ContainsKey('VideoKbps')) { throw 'Choose either -AutoVideoBitrate or -VideoKbps.' }
if (Get-Process -Name QuickClip -ErrorAction SilentlyContinue) { throw 'Quit QuickClip before benchmarking to avoid overlapping recorders. Also stop other recording tools manually.' }
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $root '.quickclip-local\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
if ($Build) { & (Join-Path $PSScriptRoot 'dev.ps1') -BuildOnly }
$dll = Join-Path $root 'bin\quickclip-personal\dev\QuickClip.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Build the development application first: .\scripts\dev.ps1 -BuildOnly' }
$directory = Join-Path $root ('.quickclip-local\benchmarks\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$inputFile = Join-Path $directory 'input.json'
$outputFile = Join-Path $directory 'result.json'
$targetKbps = if ($AutoVideoBitrate) { $null } else { $VideoKbps }
@{ AllowDesktopCapture=$true; Seconds=$Seconds; Fps=$Fps; Height=$Height; Encoder=$Encoder; Preset=$Preset; Backend=$Backend; VideoKbps=$targetKbps; Monitor=$Monitor } |
    ConvertTo-Json | Set-Content -LiteralPath $inputFile -Encoding UTF8
$previousProfile = $env:QUICKCLIP_PERSONAL_DATA_DIR
$env:QUICKCLIP_PERSONAL_DATA_DIR = Join-Path $directory 'profile'
$process = $null
try {
    Write-Host "Recording desktop to RAM for $Seconds seconds: $Encoder $Preset, $Backend, ${Fps}fps, height=$Height (0=native). No audio."
    $arguments = @(('"{0}"' -f $dll), '--benchmark-video', ('"{0}"' -f $inputFile), ('"{0}"' -f $outputFile))
    $process = Start-Process -FilePath $sdk -ArgumentList $arguments -PassThru -WindowStyle Hidden -WorkingDirectory $root
    $gpuTool = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    $gpu = @()
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds + 20)
    while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if ($gpuTool) {
            $sample = & $gpuTool.Source --query-gpu=utilization.gpu,utilization.encoder,utilization.decoder,memory.used --format=csv,noheader,nounits 2>$null
            if ($LASTEXITCODE -eq 0) { $gpu += [pscustomobject]@{ Utc=[DateTime]::UtcNow.ToString('o'); GpuEncoderDecoderVram=$sample } }
        }
        Start-Sleep -Milliseconds 1000
    }
    if (!$process.HasExited) { throw 'Benchmark did not finish in its bounded time. The launched diagnostic process will be stopped.' }
    $process.WaitForExit()
    if ($gpu.Count) { $gpu | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'gpu.json') -Encoding UTF8 }
    if ($process.ExitCode -ne 0) { throw "Benchmark failed. Inspect $outputFile" }
    Write-Host "Results: $outputFile"
    $result = Get-Content -LiteralPath $outputFile -Raw | ConvertFrom-Json
    $result.Samples[-1] | Format-List
}
finally {
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
    $env:QUICKCLIP_PERSONAL_DATA_DIR = $previousProfile
}

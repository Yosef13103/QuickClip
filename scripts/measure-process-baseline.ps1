<#
.SYNOPSIS
    Owner-supervised, read-only process resource sampler for matched QuickClip comparisons.
.DESCRIPTION
    Explicit PID and scenario; does not launch, stop, configure, or record anything.
    Output contains resource measurements, not process command lines, paths, window titles or media.
    Run from a separate terminal after preparing the desired scenario. GPU metrics are optional,
    aggregate NVIDIA-device counters, not per-process attribution.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$TargetProcessId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$BuildSha,
    [Parameter(Mandatory)][ValidateSet('Personal','Upstream','OBS-reference')][string]$Variant,
    [Parameter(Mandatory)][ValidateSet('IdleVisible','IdleTray','IdleMinimized','ArmedNoGame','Game','Editor','Scan','Save','DiagnosticsOff','DiagnosticsOn','GameNoRecorder')][string]$Scenario,
    [ValidateRange(10,1800)][int]$Seconds = 60,
    [ValidateRange(0,300)][int]$WarmupSeconds = 15,
    [ValidateRange(500,10000)][int]$IntervalMs = 1000,
    [ValidatePattern('^[a-zA-Z0-9_-]{1,48}$')][string]$RunLabel = 'run1',
    [switch]$Nvidia
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root ('.quickclip-local\benchmarks\process-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$process = Get-Process -Id $TargetProcessId -ErrorAction Stop
$started = $process.StartTime
$logicalCpus = [Environment]::ProcessorCount
$gpuTool = $null
if ($Nvidia) { $gpuTool = Get-Command nvidia-smi.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not ('QuickClipBench.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace QuickClipBench {
  public static class Native {
    [StructLayout(LayoutKind.Sequential)]
    public struct Io {
      public ulong ReadOperations, WriteOperations, OtherOperations;
      public ulong ReadBytes, WriteBytes, OtherBytes;
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessIoCounters(IntPtr process, out Io counters);
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint flags);
  }
}
'@
}
function Read-Target {
    $process.Refresh()
    if ($process.HasExited -or $process.StartTime -ne $started) { throw 'Selected process ended or changed.' }
    $io = New-Object QuickClipBench.Native+Io
    $hasIo = $false
    $gdi = $null; $user = $null
    try {
        $hasIo = [QuickClipBench.Native]::GetProcessIoCounters($process.Handle, [ref]$io)
        $gdi = [QuickClipBench.Native]::GetGuiResources($process.Handle, 0)
        $user = [QuickClipBench.Native]::GetGuiResources($process.Handle, 1)
    } catch { }
    [pscustomobject]@{
        Ticks = [Diagnostics.Stopwatch]::GetTimestamp()
        CpuSeconds = $process.TotalProcessorTime.TotalSeconds
        PrivateBytes = $process.PrivateMemorySize64
        WorkingSetBytes = $process.WorkingSet64
        Handles = $process.HandleCount
        Threads = $process.Threads.Count
        GdiObjects = $gdi
        UserObjects = $user
        ReadBytes = if ($hasIo) { $io.ReadBytes } else { $null }
        WriteBytes = if ($hasIo) { $io.WriteBytes } else { $null }
    }
}
function Stats($values) {
    $ordered = @($values | Where-Object { $null -ne $_ } | Sort-Object)
    if (!$ordered.Count) { return $null }
    $n = $ordered.Count
    $mid = [int][Math]::Floor($n / 2)
    $median = if ($n % 2) { $ordered[$mid] } else { ([double]$ordered[$mid-1] + [double]$ordered[$mid]) / 2 }
    [pscustomobject]@{ Count=$n; Median=$median; P95=$ordered[[int][Math]::Ceiling(.95*$n)-1]; Min=$ordered[0]; Max=$ordered[-1] }
}
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$identity = 'unavailable'
try {
    if ($process.Path) { $identity = (Get-FileHash -LiteralPath $process.Path -Algorithm SHA256).Hash }
} catch {}
Write-Host "Measuring PID $TargetProcessId ($Scenario). Warmup $WarmupSeconds s, capture window $Seconds s. No capture is started by this script."
Start-Sleep -Seconds $WarmupSeconds
$previous = Read-Target
$first = $previous
$watch = [Diagnostics.Stopwatch]::StartNew()
$rows = New-Object 'System.Collections.Generic.List[object]'
$gpu = New-Object 'System.Collections.Generic.List[object]'
$failure = $null
try {
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds ([int][Math]::Min($IntervalMs, [Math]::Max(1, ($Seconds-$watch.Elapsed.TotalSeconds)*1000)))
        $current = Read-Target
        $dt = ($current.Ticks-$previous.Ticks)/[double][Diagnostics.Stopwatch]::Frequency
        if ($dt -le 0) { continue }
        $cpu = 100*($current.CpuSeconds-$previous.CpuSeconds)/$dt
        $rows.Add([pscustomobject]@{
            ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds,3)
            IntervalSeconds = $dt
            CpuOneCorePercent = $cpu
            CpuMachinePercent = $cpu/$logicalCpus
            PrivateMiB = $current.PrivateBytes/1MB
            WorkingSetMiB = $current.WorkingSetBytes/1MB
            Handles = $current.Handles
            Threads = $current.Threads
            GdiObjects = $current.GdiObjects
            UserObjects = $current.UserObjects
            ReadBytesPerSec = if ($null -ne $current.ReadBytes -and $null -ne $previous.ReadBytes) { ($current.ReadBytes-$previous.ReadBytes)/$dt } else { $null }
            WriteBytesPerSec = if ($null -ne $current.WriteBytes -and $null -ne $previous.WriteBytes) { ($current.WriteBytes-$previous.WriteBytes)/$dt } else { $null }
        })
        $previous = $current
        if ($gpuTool) {
            # Bound the external query. It reads only adapter counters; it never changes driver settings.
            $temp = Join-Path $directory 'gpu-query.tmp'
            $err = Join-Path $directory 'gpu-query-error.tmp'
            $query = $null
            try {
                $query = Start-Process -FilePath $gpuTool.Source -ArgumentList '--query-gpu=index,utilization.gpu,utilization.encoder,utilization.decoder,memory.used,clocks.gr --format=csv,noheader,nounits' -PassThru -WindowStyle Hidden -RedirectStandardOutput $temp -RedirectStandardError $err
                if (!$query.WaitForExit(2000)) { $query.Kill(); $query.WaitForExit() }
                elseif ($query.ExitCode -eq 0) {
                    foreach ($line in (Get-Content -LiteralPath $temp)) {
                        $v = @($line.Split(',') | ForEach-Object { $_.Trim() })
                        if ($v.Count -eq 6) {
                            $gpu.Add([pscustomobject]@{ ElapsedSeconds=$watch.Elapsed.TotalSeconds; AdapterIndex=$v[0]; GpuPercent=$v[1]; EncodePercent=$v[2]; DecodePercent=$v[3]; DeviceMemoryMiB=$v[4]; GraphicsMHz=$v[5] })
                        }
                    }
                }
            } catch {} finally {
                if ($query) { $query.Dispose() }
                Remove-Item -LiteralPath $temp,$err -ErrorAction SilentlyContinue
            }
        }
    }
} catch { $failure = $_.Exception.GetType().Name }
$rows | Export-Csv -LiteralPath (Join-Path $directory 'samples.csv') -NoTypeInformation -Encoding UTF8
if ($gpu.Count) { $gpu | Export-Csv -LiteralPath (Join-Path $directory 'gpu-device.csv') -NoTypeInformation -Encoding UTF8 }
$metrics = [ordered]@{}
foreach ($field in @('CpuOneCorePercent','CpuMachinePercent','PrivateMiB','WorkingSetMiB','Handles','Threads','GdiObjects','UserObjects','ReadBytesPerSec','WriteBytesPerSec')) {
    $metrics[$field] = Stats @($rows | ForEach-Object { $_.$field })
}
$report = [ordered]@{
    Schema=1; Utc=[DateTime]::UtcNow.ToString('o'); BuildSha=$BuildSha.ToLowerInvariant(); BuildShaEvidence='owner supplied; not binary attestation'; ExecutableSha256=$identity
    Variant=$Variant; Scenario=$Scenario; RunLabel=$RunLabel; ProcessId=$TargetProcessId
    OsVersion=[Environment]::OSVersion.Version.ToString(); LogicalCpus=$logicalCpus
    WarmupSeconds=$WarmupSeconds; RequestedSeconds=$Seconds; IntervalMs=$IntervalMs
    ObservedSeconds=$watch.Elapsed.TotalSeconds; Samples=$rows.Count; Complete=($null -eq $failure)
    FailureType=$failure; NvidiaRequested=[bool]$Nvidia; NvidiaSamples=$gpu.Count
    GpuScope='device-wide only; no per-process attribution'
    Metrics=$metrics
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'summary.json') -Encoding UTF8
$process.Dispose()
Write-Host "Results: $directory"
[pscustomobject]$metrics | Format-List
if ($failure) { throw "Incomplete measurement ($failure). Partial evidence saved." }

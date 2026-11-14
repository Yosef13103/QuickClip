<#
.SYNOPSIS
Reads MP4 audio stream metadata and decodes a short window of one selected track to measure peak and RMS.
.DESCRIPTION
Uses the repository's bundled FFprobe and FFmpeg. Prints JSON to stdout. Audio is decoded locally
and discarded. The script does not write to the clip, produce audio files, or use the network.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Clip,
    [ValidateRange(0, 2147483647)][int]$AudioTrack = 0,
    [ValidateRange(0, 86400)][double]$StartSeconds = 0,
    [ValidateRange(1, 30)][int]$Seconds = 15
)

$ErrorActionPreference = 'Stop'
$clipFile = Get-Item -LiteralPath $Clip
if ($clipFile -isnot [System.IO.FileInfo] -or $clipFile.Extension -ine '.mp4') { throw 'Choose a QuickClip MP4 file.' }
$ffmpegDir = Join-Path $PSScriptRoot '../deps/ffmpeg'
$ffprobe = Join-Path $ffmpegDir 'ffprobe.exe'
$ffmpeg = Join-Path $ffmpegDir 'ffmpeg.exe'
foreach ($tool in $ffprobe, $ffmpeg) {
    if (!(Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Bundled FFmpeg and FFprobe are required.' }
}

# Force the local MP4 demuxer and disable external MOV data references.
$inputOptions = @('-protocol_whitelist', 'file', '-f', 'mov', '-enable_drefs', '0')
$entries = 'stream=index,codec_name,sample_rate,channels,duration:stream_tags=handler_name:format=duration'
$metadata = & $ffprobe -v error @inputOptions -select_streams a -show_entries $entries -of json $clipFile.FullName
if ($LASTEXITCODE -ne 0) { throw 'FFprobe could not read this MP4.' }
$probe = $metadata | Out-String | ConvertFrom-Json
if ($AudioTrack -ge $probe.streams.Count) { throw "The MP4 has no audio track at index $AudioTrack." }

$start = $StartSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$filter = 'astats=reset=0:measure_perchannel=none:measure_overall=Peak_level+RMS_level+Number_of_samples'
# Windows PowerShell treats redirected stderr as error records, including successful astats output.
try {
    $ErrorActionPreference = 'Continue'
    $stats = & $ffmpeg -hide_banner -nostdin -nostats -loglevel info @inputOptions -ss $start -t $Seconds -i $clipFile.FullName -map "0:a:$AudioTrack" -af $filter -f null - 2>&1 | Out-String
    $decodeExitCode = $LASTEXITCODE
}
finally { $ErrorActionPreference = 'Stop' }
if ($decodeExitCode -ne 0) { throw 'FFmpeg could not decode the selected audio track.' }
$peak = [regex]::Match($stats, 'Peak level dB:\s*(\S+)')
$rms = [regex]::Match($stats, 'RMS level dB:\s*(\S+)')
$samples = [regex]::Match($stats, 'Number of samples:\s*(\d+)')
if (!$peak.Success -or !$rms.Success -or !$samples.Success -or [long]$samples.Groups[1].Value -eq 0) {
    throw 'No audio samples were decoded in the requested window.'
}

[ordered]@{
    InspectedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    AudioTrack = $AudioTrack
    StartSeconds = $StartSeconds
    WindowSeconds = $Seconds
    PeakDbfs = $peak.Groups[1].Value
    RmsDbfs = $rms.Groups[1].Value
    SamplesPerChannel = [long]$samples.Groups[1].Value
    Container = $probe.format
    AudioStreams = $probe.streams
} | ConvertTo-Json -Depth 5

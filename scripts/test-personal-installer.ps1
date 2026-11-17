# CI-only: exercises the real packaged installer under a disposable Windows runner.
# SUBST verifies non-system drive-letter handling, not physical-drive ACL behavior.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Run this smoke test only on a disposable GitHub Actions runner.'
}
$root = Split-Path -Parent $PSScriptRoot
$setups = @(Get-ChildItem (Join-Path $root 'publish-personal') -Filter '*-setup.exe' -File)
if ($setups.Count -ne 1) { throw 'Expected one Personal installer.' }
$used = @(Get-PSDrive -PSProvider FileSystem | ForEach-Object Name)
$letter = @('Q','R','S','T','U','V','W','X','Y','Z') | Where-Object { $_ -notin $used } | Select-Object -First 1
if (!$letter) { throw 'No available test drive letter.' }
$drive = "${letter}:"
$backing = Join-Path $env:RUNNER_TEMP ('quickclip-installer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backing | Out-Null
subst.exe $drive $backing
if ($LASTEXITCODE) { throw 'Could not create test drive.' }
try {
    $destination = "$drive\Games With Spaces\QuickClip Personal"
    $log = Join-Path $env:RUNNER_TEMP 'quickclip-install-smoke.log'
    $args = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="{0}" /LOG="{1}"' -f $destination, $log
    $setup = Start-Process -FilePath $setups[0].FullName -ArgumentList $args -Wait -PassThru
    if ($setup.ExitCode -ne 0) {
        if (Test-Path $log) { Get-Content $log -Tail 60 }
        throw "Installer failed ($($setup.ExitCode))."
    }
    foreach ($name in 'QuickClip.exe','QuickClipCapture.dll','libmpv-2.dll','ffmpeg\ffmpeg.exe','unins000.exe') {
        if (!(Test-Path -LiteralPath (Join-Path $destination $name) -PathType Leaf)) { throw "Missing $name" }
    }
    $registered = (Get-ItemProperty 'HKCU:\Software\QuickClip Personal\Install').Executable
    if ($registered -ne (Join-Path $destination 'QuickClip.exe')) { throw "Wrong registered executable: $registered" }
    if (Get-ChildItem -LiteralPath $backing -Recurse -Directory -Filter '.quickclip-write-test-*') { throw 'Unexpected leftover destination probe.' }
    Write-Host "PASS: installed application and current-user registration on $drive"
}
finally {
    subst.exe $drive /D
}

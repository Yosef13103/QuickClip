# Exercise the real Inno scripts and update worker with tiny disposable packages.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Run this fixture only on a disposable GitHub-hosted runner.'
}
if (Test-Path 'HKCU:\Software\QuickClip Personal\Install') { throw 'The fixture requires a clean user profile.' }

$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $env:RUNNER_TEMP ('quickclip-upgrade-' + [Guid]::NewGuid().ToString('N'))
$updates = Join-Path $scratch 'updates'
$work = Join-Path $updates ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$used = @(Get-PSDrive -PSProvider FileSystem | Select-Object -ExpandProperty Name)
$letter = @('Q','R','S','T','U','V','W','X','Y','Z') | Where-Object { $_ -notin $used } | Select-Object -First 1
if (!$letter) { throw 'No available fixture drive letter.' }
$drive = $letter + ':'
subst.exe $drive $scratch
if ($LASTEXITCODE) { throw 'Could not create the alternate fixture drive.' }

$destination = "$drive\Games With Spaces\QuickClip Personal"
$exe = Join-Path $destination 'QuickClip.exe'
$env:QC_UPGRADE_STARTED = Join-Path $scratch 'started'
$env:QC_UPGRADE_RELEASE = Join-Path $scratch 'release'
$worker = $parent = $null

function New-FixtureSetup([string]$Version, [string]$Out) {
    $payload = Join-Path $scratch "payload-$Version"
    New-Item -ItemType Directory -Path $payload | Out-Null
    $source = Join-Path $scratch "fixture-$Version.cs"
    @"
using System;
using System.IO;
using System.Reflection;
using System.Threading;
[assembly: AssemblyProduct("QuickClip Personal")]
[assembly: AssemblyFileVersion("$Version.0")]
public static class PackageFixture {
    public static int Main(string[] args) {
        if (args.Length > 0 && args[0] == "--quit") return 0;
        File.WriteAllText(Environment.GetEnvironmentVariable("QC_UPGRADE_STARTED"), "$Version");
        if ("$Version" == "1.0.0") {
            for (int attempt = 0; attempt < 600; attempt++) {
                if (File.Exists(Environment.GetEnvironmentVariable("QC_UPGRADE_RELEASE"))) return 0;
                Thread.Sleep(100);
            }
            return 12;
        }
        // The update worker verifies that the newly launched app stays alive.
        // This fixture needs no application work; remain alive for that check.
        Thread.Sleep(5000);
        return 0;
    }
}
"@ | Set-Content -LiteralPath $source -Encoding utf8
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe "/out:$payload\QuickClip.exe" $source
    if ($LASTEXITCODE) { throw 'Compiling the disposable application failed.' }
    $owned = if ($Version -eq '1.0.0') { 'old.dll' } else { 'new.dll' }
    Set-Content -LiteralPath (Join-Path $payload $owned) -Value $Version
    Set-Content -LiteralPath (Join-Path $payload 'quickclip-install-files.txt') -Value @('QuickClip.exe', $owned)
    & (Join-Path $repo 'deps\innosetup\ISCC.exe') /Q "/DAppVersion=$Version" "/DNumericVersion=$Version.0" "/DDisplayVersion=$Version" "/DSourceDir=$payload" "/DOutputDir=$Out" (Join-Path $repo 'installer\QuickClip.Personal.iss')
    if ($LASTEXITCODE) { throw 'Compiling the disposable setup failed.' }
}

function Wait-ForFile([string]$Path, [Diagnostics.Process]$Process) {
    $until = [DateTime]::UtcNow.AddSeconds(35)
    while (![IO.File]::Exists($Path)) {
        if ($Process.HasExited -or [DateTime]::UtcNow -gt $until) { throw "Did not observe $([IO.Path]::GetFileName($Path))." }
        Start-Sleep -Milliseconds 100
    }
}

try {
    New-FixtureSetup '1.0.0' $scratch
    New-FixtureSetup '1.0.1' $work
    $oldSetup = Join-Path $scratch 'QuickClip-Personal-1.0.0-setup.exe'
    $setupArgs = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="{0}"' -f $destination
    $old = Start-Process -FilePath $oldSetup -ArgumentList $setupArgs -Wait -PassThru
    if ($old.ExitCode -ne 0) { throw "Fresh fixture setup failed ($($old.ExitCode))." }
    $destinationAcl = (Get-Acl -LiteralPath $destination).Sddl

    $wrongRoot = "$drive\wrong-destination"
    $badArgs = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /QCUPDATE=1.0.0 /DIR="{0}"' -f $wrongRoot
    $package = Join-Path $work 'QuickClip-Personal-1.0.1-setup.exe'
    $bad = Start-Process -FilePath $package -ArgumentList $badArgs -Wait -PassThru
    if ($bad.ExitCode -eq 0 -or (Test-Path -LiteralPath $wrongRoot)) { throw 'Setup accepted a different destination.' }

    $sentinel = Join-Path $scratch 'external-library.sqlite'
    Set-Content -LiteralPath $sentinel -Value 'unchanged library fixture'
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash
    $parent = Start-Process -FilePath $exe -PassThru
    Wait-ForFile $env:QC_UPGRADE_STARTED $parent
    $workerScript = Join-Path $work 'install.ps1'
    Copy-Item -LiteralPath (Join-Path $repo 'src\QuickClip\Updates\PersonalInstall.ps1') -Destination $workerScript
    $plan = @{
        Executable = $exe; Package = [IO.Path]::GetFileName($package); Size = (Get-Item -LiteralPath $package).Length
        Sha256 = (Get-FileHash -LiteralPath $package).Hash; CurrentVersion = '1.0.0'; Version = '1.0.1'
        ParentId = $parent.Id; ParentStart = $parent.StartTime.ToFileTimeUtc().ToString()
    }
    [IO.File]::WriteAllText((Join-Path $work 'plan.json'), (ConvertTo-Json -InputObject $plan))
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($arg in @('-NoProfile', '-NonInteractive', '-STA', '-ExecutionPolicy', 'Bypass', '-File', $workerScript)) {
        $start.ArgumentList.Add($arg)
    }
    $worker = [Diagnostics.Process]::Start($start)
    Wait-ForFile (Join-Path $work 'ready') $worker
    [IO.File]::WriteAllText((Join-Path $work 'commit'), '')
    Remove-Item -LiteralPath $env:QC_UPGRADE_STARTED
    Start-Sleep -Milliseconds 400
    if ($parent.HasExited -or (Test-Path -LiteralPath (Join-Path $work 'setup.log')) -or
        (Get-Content -LiteralPath (Join-Path $destination 'old.dll')) -ne '1.0.0') {
        throw 'Setup advanced before the old application exited.'
    }
    [IO.File]::WriteAllText($env:QC_UPGRADE_RELEASE, '')
    if (!$worker.WaitForExit(45000)) { throw 'The update worker did not finish.' }
    $until = [DateTime]::UtcNow.AddSeconds(10)
    while (!(Test-Path -LiteralPath $env:QC_UPGRADE_STARTED) -and [DateTime]::UtcNow -lt $until) {
        Start-Sleep -Milliseconds 100
    }
    $registration = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{90FB3786-4F27-41D1-B7D6-44E671DE28CA}_is1'
    if ((Get-Content -LiteralPath $env:QC_UPGRADE_STARTED) -ne '1.0.1' -or
        (Get-Content -LiteralPath (Join-Path $destination 'new.dll')) -ne '1.0.1' -or
        (Test-Path -LiteralPath (Join-Path $destination 'old.dll')) -or
        (Test-Path -LiteralPath (Join-Path $updates 'pending')) -or
        (Get-ItemProperty -LiteralPath 'HKCU:\Software\QuickClip Personal\Install').Executable -ne $exe -or
        $registration.InstallLocation -ne ($destination + '\') -or $registration.DisplayVersion -ne '1.0.1' -or
        (Get-Acl -LiteralPath $destination).Sddl -ne $destinationAcl -or
        (Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) {
        throw 'Upgrade did not preserve the destination and registration or did not restart.'
    }
    Wait-Process -Name QuickClip -Timeout 10 -ErrorAction SilentlyContinue
    Write-Host "PASS: approved upgrade waited for exit, upgraded on $drive, and restarted."
}
catch {
    foreach ($name in 'error.txt', 'setup.log') {
        $log = Join-Path $work $name
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 40 }
    }
    throw
}
finally {
    if ($worker -and !$worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }
    if ($parent -and !$parent.HasExited) { $parent.Kill(); $parent.WaitForExit() }
    try {
        $uninstaller = Join-Path $destination 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) {
            $uninstall = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
            if ($uninstall.ExitCode -ne 0) { throw "Fixture uninstall failed ($($uninstall.ExitCode))." }
        }
    } finally { subst.exe $drive /D }
}

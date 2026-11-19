# Hosted-only real released application, embedded worker and candidate installer.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Default','Alternate')][string]$Layout,
    [Parameter(Mandatory)][string]$CandidateSetup,
    [Parameter(Mandatory)][string]$CandidatePayload
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Disposable GitHub-hosted runner only.' }
$installKey = 'HKCU:\Software\QuickClip Personal\Install'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{90FB3786-4F27-41D1-B7D6-44E671DE28CA}_is1'
$profile = Join-Path $env:APPDATA 'QuickClip Personal'
$local = Join-Path $env:LOCALAPPDATA 'QuickClip Personal'
$default = Join-Path $env:LOCALAPPDATA 'Programs\QuickClip Personal'
if ((Test-Path $installKey) -or (Test-Path $uninstallKey) -or (Test-Path $profile) -or
    (Test-Path $local) -or (Test-Path $default) -or (Get-Process QuickClip -ErrorAction SilentlyContinue) -or $env:QUICKCLIP_PERSONAL_DATA_DIR) { throw 'Unused installed Personal profile required.' }
$CandidateSetup = (Resolve-Path -LiteralPath $CandidateSetup).Path
$CandidatePayload = (Resolve-Path -LiteralPath $CandidatePayload).Path
$version = '1.2.2'
if ([IO.Path]::GetFileName($CandidateSetup) -cne "QuickClip-Personal-$version-setup.exe") { throw 'Wrong fixture candidate package.' }
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($CandidateSetup)
if ($info.FileVersion.Trim() -ne "$version.0" -or $info.FileDescription.TrimEnd() -ne 'QuickClip Personal setup (in-place update v1)') { throw 'Candidate setup identity/protocol differs.' }
$scratch = Join-Path $env:RUNNER_TEMP ('quickclip-released-upgrade-'+[Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$destination = $default
$drive = $null
$parent = $worker = $restarted = $null
$stage = $null
$oldSetup = Join-Path $scratch 'QuickClip-Personal-1.2.1-setup.exe'
function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$Failure) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 200 } while ([DateTime]::UtcNow -lt $until)
    throw $Failure
}
function Invoke-Setup([string]$File, [string]$Arguments) {
    $proc = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru
    try {
        if (!$proc.WaitForExit(180000)) { $proc.Kill($true); throw 'Fixture setup timed out.' }
        if ($proc.ExitCode -ne 0) { throw "Fixture setup failed ($($proc.ExitCode))." }
    } finally { $proc.Dispose() }
}
function Assert-Installed([string]$Expected) {
    $reg = Get-ItemProperty -LiteralPath $installKey
    $unreg = Get-ItemProperty -LiteralPath $uninstallKey
    $exeInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($reg.Executable -ine $exe -or $unreg.InstallLocation -ine ($destination+'\') -or
        $unreg.DisplayVersion -cne $Expected -or $exeInfo.ProductName -cne 'QuickClip Personal' -or
        $exeInfo.FileVersion.Trim() -ne "$Expected.0") { throw "Unexpected installed identity for $Expected." }
}
function Quit-Fixture([Diagnostics.Process]$Process) {
    # The released app checks the installer lease before routing CLI arguments.
    # Send the same --quit IPC message directly, after checking the actual parent.
    # This only runs in the previously verified clean disposable profile.
    if ($Process.HasExited -or $Process.MainModule.FileName -ine $exe) { throw 'Wrong fixture process.' }
    $null = $Process.Handle
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', ('QuickClip.Personal.Instance.'+[Environment]::UserName), [IO.Pipes.PipeDirection]::Out)
    try {
        $pipe.Connect(10000)
        $bytes = [Text.Encoding]::UTF8.GetBytes('--quit')
        $pipe.Write($bytes,0,$bytes.Length)
    } finally { $pipe.Dispose() }
    if (!$Process.WaitForExit(30000)) { throw "Application PID $($Process.Id) did not exit in time." }
    if ($Process.ExitCode -ne 0) { throw "Application PID $($Process.Id) exited with code $($Process.ExitCode)." }
}

try {
    if ($Layout -eq 'Alternate') {
        $used = @(Get-PSDrive -PSProvider FileSystem | ForEach-Object Name)
        $letter = @('Q','R','S','T','U','V','W','X','Y','Z') | Where-Object {$_ -notin $used} | Select-Object -First 1
        if (!$letter) { throw 'No free fixture drive letter.' }
        $drive = $letter+':'
        subst.exe $drive $scratch
        if ($LASTEXITCODE) { throw 'Could not create alternate fixture drive.' }
        $destination = "$drive\Games With Spaces\QuickClip Personal"
    }
    $exe = Join-Path $destination 'QuickClip.exe'
    Invoke-WebRequest -Uri 'https://github.com/Yosef13103/QuickClip/releases/download/personal-v1.2.1/QuickClip-Personal-1.2.1-setup.exe' -OutFile $oldSetup -TimeoutSec 180
    if ((Get-Item -LiteralPath $oldSetup).Length -ne 134867044 -or (Get-FileHash -LiteralPath $oldSetup -Algorithm SHA256).Hash -ine '8ef300dfe49b45b43c7b2d4a2bc7234dbd52d13186a5f793bdf25dbb9f9ba89e') { throw 'Released installer digest/length mismatch.' }
    $installArgs = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    if ($Layout -eq 'Alternate') { $installArgs += ' /DIR="'+$destination+'"' }
    Invoke-Setup $oldSetup $installArgs
    Assert-Installed '1.2.1'
    $acl = (Get-Acl -LiteralPath $destination).Sddl
    # Read released resource without loading/executing its managed assembly.
    $helper = Join-Path $scratch 'ExtractWorker.cs'
    @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using var pe = new PEReader(File.OpenRead(args[0]));
var md = pe.GetMetadataReader();
foreach (var h in md.ManifestResources)
{
    var r = md.GetManifestResource(h);
    if (md.GetString(r.Name) != "QuickClip.PersonalInstall.ps1" || !r.Implementation.IsNil) continue;
    var section = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
    int offset = checked((int)r.Offset);
    var reader = section.GetReader(offset, section.Length-offset);
    int size = reader.ReadInt32();
    if (size <= 0 || size > reader.RemainingBytes) throw new InvalidDataException("Invalid worker resource.");
    File.WriteAllBytes(args[1], reader.ReadBytes(size));
    return;
}
throw new InvalidDataException("Released worker resource missing.");
'@ | Set-Content -LiteralPath $helper -Encoding utf8
    $stage = Join-Path (Join-Path $local 'updates') ([Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $stage -Force
    $script = Join-Path $stage 'install.ps1'
    $helperProject = Join-Path $scratch 'ExtractWorker.csproj'
    [IO.File]::WriteAllText($helperProject, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="ExtractWorker.cs" /></ItemGroup></Project>')
    dotnet run --project $helperProject -- (Join-Path $destination 'QuickClip.dll') $script
    if ($LASTEXITCODE -or !(Test-Path -LiteralPath $script -PathType Leaf)) { throw 'Released worker extraction failed.' }
    Write-Host "Exact released worker SHA256: $((Get-FileHash -LiteralPath $script).Hash)"
    $package = Join-Path $stage ([IO.Path]::GetFileName($CandidateSetup))
    Copy-Item -LiteralPath $CandidateSetup -Destination $package
    $size = (Get-Item -LiteralPath $package).Length
    $hash = (Get-FileHash -LiteralPath $package).Hash
    if ($hash -ne (Get-FileHash -LiteralPath $CandidateSetup).Hash) { throw 'Fixture package copy differs.' }
    $clips = Join-Path $scratch 'fixture-clips'
    $null = New-Item -ItemType Directory -Path $clips, $profile -Force
    $sentinel = Join-Path $clips 'library-sentinel.txt'
    [IO.File]::WriteAllText($sentinel,'keep this fixture unchanged')
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash
    $settings = Join-Path $profile 'settings.json'
    [IO.File]::WriteAllText($settings,(@{
        RecordingEnabled=$false; EnableReplayOnStartup=$false; ReplayOnboardingCompleted=$true
        CheckForUpdates=$false; StartInTray=$true; GuidedSetupStep=$null
        BufferSeconds=47; ClipsFolder=$clips; SeenVersion=$version
        Games=@{EnabledLaunchers=@(); DiscoverSteam=$false; DiscoverEpic=$false; DiscoverGog=$false}
    } | ConvertTo-Json -Depth 5))
    $parent = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru
    Wait-Until {
        if ($parent.HasExited) { throw 'Released application exited before readiness.' }
        try { $m = [Threading.Mutex]::OpenExisting('Local\QuickClip.Personal.Instance'); $m.Dispose(); return $true }
        catch [Threading.WaitHandleCannotBeOpenedException] { return $false }
    } 30 'Released application did not acquire its primary instance.'
    Start-Sleep -Seconds 2
    $plan = @{Executable=$exe; Package=[IO.Path]::GetFileName($package); Size=$size; Sha256=$hash
        CurrentVersion='1.2.1'; Version=$version; ParentId=$parent.Id; ParentStart=$parent.StartTime.ToFileTimeUtc().ToString()}
    [IO.File]::WriteAllText((Join-Path $stage 'plan.json'),($plan | ConvertTo-Json))
    $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'))
    $psi.UseShellExecute=$false
    $psi.CreateNoWindow=$true
    foreach ($arg in @('-NoProfile','-NonInteractive','-STA','-ExecutionPolicy','Bypass','-File',$script)) { $psi.ArgumentList.Add($arg) }
    $worker = [Diagnostics.Process]::Start($psi)
    Wait-Until {
        if ($worker.HasExited -or (Test-Path -LiteralPath (Join-Path $stage 'error.txt'))) { throw 'Released worker failed before readiness.' }
        Test-Path -LiteralPath (Join-Path $stage 'ready')
    } 90 'Released worker never signaled readiness.'
    # Instrumented approval; actual ordinary application shutdown and released worker.
    [IO.File]::WriteAllText((Join-Path $stage 'commit'),'')
    Start-Sleep -Milliseconds 500
    if ($parent.HasExited -or (Test-Path -LiteralPath (Join-Path $stage 'setup.log'))) { throw 'Setup started before old app shutdown.' }
    Assert-Installed '1.2.1'
    Quit-Fixture $parent
    Wait-Until {
        if (Test-Path -LiteralPath (Join-Path $stage 'error.txt')) { throw 'Released worker reported a failure.' }
        $worker.HasExited
    } 240 'Released worker did not finish.'
    if ($worker.ExitCode -ne 0) { throw "Released worker exited $($worker.ExitCode)." }
    Assert-Installed $version
    if (Test-Path -LiteralPath (Join-Path $local 'updates\pending')) { throw 'Successful upgrade left a recovery marker.' }
    foreach ($rel in [IO.File]::ReadAllLines((Join-Path $CandidatePayload 'quickclip-install-files.txt'))) {
        $expected = Join-Path $CandidatePayload $rel
        $actual = Join-Path $destination $rel
        if (!(Test-Path -LiteralPath $actual -PathType Leaf) -or (Get-FileHash -LiteralPath $actual).Hash -ne (Get-FileHash -LiteralPath $expected).Hash) { throw "Installed payload differs: $rel" }
    }
    if ((Get-Acl -LiteralPath $destination).Sddl -ne $acl) { throw 'Installation ACL changed.' }
    Wait-Until {
        $script:restarted = Get-Process QuickClip -ErrorAction SilentlyContinue | Where-Object {$_.Id -ne $parent.Id -and $_.Path -ieq $exe} | Select-Object -First 1
        $null -ne $script:restarted
    } 30 'Updated application did not restart.'
    Start-Sleep -Seconds 3
    if ($restarted.HasExited) { throw 'Updated application exited after restart.' }
    Quit-Fixture $restarted
    $saved = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    if ($saved.BufferSeconds -ne 47 -or $saved.RecordingEnabled -or $saved.ClipsFolder -ine $clips -or (Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) { throw 'Settings or fixture library changed.' }
    Write-Host "PASS: exact released installer and worker upgraded actual application to $version ($Layout), validated payload, restart, registration, ACL and fixture data."
} catch {
    if ($stage) { foreach ($file in @('error.txt','setup.log')) { $log = Join-Path $stage $file; if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 40 } } }
    throw
} finally {
    # Clean hosted runner only. Never touch an owner's app.
    foreach ($proc in @($worker,$parent,$restarted)) { if ($proc -and !$proc.HasExited) { $proc.Kill($true); $proc.WaitForExit(10000) | Out-Null } }
    if ($drive) { subst.exe $drive /D }
}

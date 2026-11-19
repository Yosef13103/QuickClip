# Local QuickClip workspace orchestration. CI and release packaging use their own clean inputs.
[CmdletBinding()]
param(
    [ValidateSet('Prepare','Native','Build','Test','Launch','Watch','Snapshot')][string]$Action = 'Build',
    [string]$Checkout = 'Development',
    [ValidateSet('Personal','Upstream')][string]$Profile = 'Personal',
    [string]$Filter,
    [switch]$CleanNative,
    [string]$WorkspaceRoot
)
$ErrorActionPreference = 'Stop'
if (!$WorkspaceRoot) { $WorkspaceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$root = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$prod = Join-Path $root '1. Production'
$dev = Join-Path $root '2. Development'
$branches = Join-Path $root '3. Branches'
$shared = Join-Path $root '4. Shared'
$data = Join-Path $root '5. Data\Development'
$deps = Join-Path $shared 'Dependencies'
$sdk = Join-Path $shared 'SDK\dotnet.exe'
if ($Checkout -eq 'Production') { $work = $prod }
elseif ($Checkout -eq 'Development') { $work = $dev }
else {
    if ($Checkout -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $Checkout -eq 'Archive') {
        throw 'Specify Production, Development, or an active branch folder name.'
    }
    $work = Join-Path $branches $Checkout
}
if (!(Test-Path -LiteralPath $work -PathType Container)) { throw "Missing checkout: $work" }
$work = (Resolve-Path -LiteralPath $work).Path
if ($work -ine $prod -and $work -ine $dev -and !(($work.StartsWith($branches+'\',[StringComparison]::OrdinalIgnoreCase)) -and (Split-Path -Parent $work) -ieq $branches)) {
    throw 'Checkout resolved outside the active workspace.'
}
if ((Get-Item -LiteralPath $work -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected active checkout is not supported.' }
$top = git -C $work rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or $top.Replace('/','\') -ine $work) { throw 'Checkout is not a registered Git worktree.' }
$common = git -C $work rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -ne 0 -or $common.Replace('/','\') -ine (Join-Path $prod '.git')) { throw 'Checkout does not belong to this repository.' }
function Hash-File([string]$Path) {
    $s = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($s)).Replace('-','') } finally { $sha.Dispose(); $s.Dispose() }
}
function Hash-Text([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Replace('-','') } finally { $sha.Dispose() }
}
function Prepare-Dependencies {
    $recipe = Hash-File (Join-Path $work 'scripts\get-deps.ps1')
    $expected = (Get-Content -LiteralPath (Join-Path $shared 'dependency-recipe.sha256') -Raw).Trim()
    if ($recipe -cne $expected) { throw 'Dependency recipe differs from shared pinned inputs. Provision a separate verified cache before building.' }
    foreach ($name in @('ffmpeg\ffmpeg.exe','ffmpeg\ffprobe.exe','ffmpeg-dev\lib\avcodec.lib','ffmpeg-dev\include\libavcodec\avcodec.h','libmpv-2.dll','innosetup\ISCC.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $deps $name) -PathType Leaf)) { throw "Missing shared dependency: $name" }
    }
    $link = Join-Path $work 'deps'
    if (Test-Path -LiteralPath $link) {
        $item = Get-Item -LiteralPath $link -Force
        if ($item.LinkType -ne 'Junction' -or @($item.Target).Count -ne 1 -or ([IO.Path]::GetFullPath([string]$item.Target)).TrimEnd('\') -ine $deps.TrimEnd('\')) {
            throw 'Existing deps is not the verified workspace junction. Inspect it; never overwrite or remove automatically.'
        }
    } else {
        New-Item -ItemType Junction -Path $link -Target $deps | Out-Null
    }
    Write-Host "Shared pinned dependencies: $link"
}
function Native-Key {
    $native = Join-Path $work 'src\QuickClip.Capture'
    $build = Join-Path $work 'scripts\build-capture.ps1'
    $entries = [Collections.Generic.List[string]]::new()
    $entries.Add('quickclip-local-native-cache-v1;x64')
    $entries.Add('build-script=' + (Hash-File $build))
    $entries.Add('dependency-recipe=' + (Hash-File (Join-Path $work 'scripts\get-deps.ps1')))
    foreach ($f in (Get-ChildItem -LiteralPath $native -File | Sort-Object Name)) {
        $entries.Add('native/' + $f.Name + '=' + (Hash-File $f.FullName))
    }
    $devDeps = Join-Path $deps 'ffmpeg-dev'
    foreach ($f in (Get-ChildItem -LiteralPath $devDeps -Recurse -File | Sort-Object FullName)) {
        $entries.Add('ffmpeg-dev/' + $f.FullName.Substring($devDeps.Length+1) + '=' + (Hash-File $f.FullName))
    }
    # vcvars determines the actual compiler and Windows SDK selected by build-capture.ps1.
    $pf = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $vswhere = Join-Path $pf 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or !$vs) { throw 'Visual Studio C++ Build Tools not found.' }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    foreach ($name in @('CL','_CL_','LINK','_LINK_')) {
        if ([Environment]::GetEnvironmentVariable($name)) { throw "Unexpected native compiler override: $name. Use a clean shell." }
    }
    $selected = @(cmd /c ('call "'+$vcvars+'" >nul 2>&1 && set VCToolsInstallDir && set WindowsSDKVersion && set WindowsSdkDir && set INCLUDE && set LIB'))
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine native toolchain.' }
    $entries.Add('vcvars=' + (Hash-File $vcvars))
    foreach ($line in $selected) { $entries.Add([string]$line) }
    $vc = ($selected | Where-Object {$_ -like 'VCToolsInstallDir=*'} | Select-Object -First 1) -replace '^VCToolsInstallDir=',''
    if (!$vc) { throw 'No selected VC tools directory.' }
    foreach ($name in @('cl.exe','link.exe','c1xx.dll','c2.dll')) {
        $file = Join-Path $vc ("bin\Hostx64\x64\"+$name)
        $entries.Add($name+'='+(Hash-File $file))
    }
    # SDK version/selected include and library paths plus the core headers/libs used by capture.
    $sdkDir = ($selected | Where-Object {$_ -like 'WindowsSdkDir=*'}|Select-Object -First 1) -replace '^WindowsSdkDir=',''
    $sdkVer = (($selected | Where-Object {$_ -like 'WindowsSDKVersion=*'}|Select-Object -First 1) -replace '^WindowsSDKVersion=','').TrimEnd('\')
    if (!$sdkDir -or !$sdkVer) { throw 'No selected Windows SDK.' }
    foreach ($relative in @("Include\$sdkVer\um\Windows.h","Include\$sdkVer\um\d3d11.h","Include\$sdkVer\shared\dxgi.h","Lib\$sdkVer\um\x64\dxgi.lib","Lib\$sdkVer\um\x64\user32.lib")) {
        $file=Join-Path $sdkDir $relative
        if (Test-Path -LiteralPath $file -PathType Leaf) { $entries.Add($relative+'='+(Hash-File $file)) }
    }
    Hash-Text (($entries.ToArray()) -join [char]10)
}
function Prepare-Native {
    $key = Native-Key
    $store = Join-Path $shared 'NativeCache'
    New-Item -ItemType Directory -Path $store -Force | Out-Null
    $entry = Join-Path $store $key
    $dll = Join-Path $entry 'QuickClipCapture.dll'
    $hash = Join-Path $entry 'sha256.txt'
    $output = Join-Path $work 'src\QuickClip.Capture\bin\QuickClipCapture.dll'
    $lock = [IO.File]::Open((Join-Path $store ($key+'.lock')), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        if (!$CleanNative -and (Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $hash) -and (Hash-File $dll) -ceq (Get-Content -LiteralPath $hash -Raw).Trim()) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
            Copy-Item -LiteralPath $dll -Destination $output -Force
            [IO.File]::SetLastWriteTimeUtc($output,[DateTime]::UtcNow)
            Write-Host "Native cache HIT $key"
        } else {
            Write-Host "Native cache MISS $key"
            Push-Location -LiteralPath $work
            try { & (Join-Path $work 'scripts\build-capture.ps1') } finally { Pop-Location }
            if (!(Test-Path -LiteralPath $output -PathType Leaf)) { throw 'Native build did not produce DLL.' }
            if ((Native-Key) -cne $key) { throw 'Native inputs changed during compilation; not caching output.' }
            New-Item -ItemType Directory -Path $entry -Force | Out-Null
            Copy-Item -LiteralPath $output -Destination $dll -Force
            [IO.File]::WriteAllText($hash,(Hash-File $dll))
        }
        if ((Hash-File $output) -cne (Get-Content -LiteralPath $hash -Raw).Trim()) { throw 'Native cache output verification failed.' }
    } finally { $lock.Dispose() }
}
$locks = Join-Path $shared 'Locks'
New-Item -ItemType Directory -Path $locks -Force | Out-Null
$lease = [IO.File]::Open((Join-Path $locks ((Hash-Text $work.ToUpperInvariant())+'.lock')), 'OpenOrCreate','ReadWrite','None')
$vars = @('DOTNET_ROOT','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','QUICKCLIP_PERSONAL_DATA_DIR','QUICKCLIP_CI','QUICKCLIP_TEST_CAPTURE','QUICKCLIP_TEST_RECYCLE_BIN','QUICKCLIP_TEST_DISCOVERY_SYMLINK')
$previous = @{}
foreach ($v in $vars) { $previous[$v]=[Environment]::GetEnvironmentVariable($v,'Process') }
$profileLease = $null
try {
    if ($Action -in @('Launch','Watch','Snapshot')) {
        $profileLease = [IO.File]::Open((Join-Path $locks 'development-profile.lock'), 'OpenOrCreate','ReadWrite','None')
        $running = @(Get-Process -Name QuickClip -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -and $_.Path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)
        })
        if ($running) { throw 'Close the running shared-development QuickClip normally before switching builds or snapshotting.' }
    }
    if ($Action -eq 'Snapshot') {
        $snapshots = Join-Path $root '5. Data\Snapshots'
        $snapshot = Join-Path $snapshots ('development-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
        if (!(Test-Path -LiteralPath $data -PathType Container)) { throw 'Shared development profile not found.' }
        $items = @(Get-ChildItem -LiteralPath $data -Force -Recurse)
        if ($items | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}) { throw 'Development profile contains a redirected path; snapshot manually after inspection.' }
        Copy-Item -LiteralPath $data -Destination $snapshot -Recurse
        $copy = @(Get-ChildItem -LiteralPath $snapshot -Force -Recurse -File)
        $files = @($items | Where-Object {!$_.PSIsContainer})
        if ($copy.Count -ne $files.Count) { throw 'Snapshot file count differs.' }
        foreach ($f in $files) {
            $target = Join-Path $snapshot $f.FullName.Substring($data.Length+1)
            if (!(Test-Path -LiteralPath $target -PathType Leaf) -or (Hash-File $target) -cne (Hash-File $f.FullName)) { throw 'Snapshot verification failed.' }
        }
        Write-Host "Verified development snapshot: $snapshot"
        return
    }
    Prepare-Dependencies
    if ($Action -eq 'Prepare') { return }
    if (!(Test-Path -LiteralPath $sdk -PathType Leaf)) { throw 'Shared .NET SDK missing.' }
    $env:DOTNET_ROOT = Split-Path -Parent $sdk
    $env:NUGET_PACKAGES = Join-Path $shared 'NuGet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:QUICKCLIP_PERSONAL_DATA_DIR = if ($Action -in @('Launch','Watch')) { $data } else {
        Join-Path $root ('5. Data\Fixtures\run-'+[Guid]::NewGuid().ToString('N'))
    }
    $env:QUICKCLIP_CI = '1'
    $env:QUICKCLIP_TEST_CAPTURE = '0'
    $env:QUICKCLIP_TEST_RECYCLE_BIN = '0'
    $env:QUICKCLIP_TEST_DISCOVERY_SYMLINK = '0'
    # A running branch build must not be overwritten. The production installation lives outside this workspace.
    $active = @(Get-Process -Name QuickClip -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($work+'\',[StringComparison]::OrdinalIgnoreCase)})
    if ($active) { throw 'Close this development build normally before rebuilding.' }
    Prepare-Native
    if ($Action -eq 'Native') { return }
    Push-Location -LiteralPath $work
    try {
        if ($Action -in @('Launch','Watch')) {
            if ($Profile -ne 'Personal') { throw 'Shared development launch supports only isolated Personal builds.' }
            # All development checkouts receive the canonical profile path. Existing app IPC/mutex prevents two Personal writers.
            $env:QUICKCLIP_CI = '0'
            if ($Action -eq 'Watch') { & (Join-Path $work 'scripts\dev.ps1') -Watch }
            else { & (Join-Path $work 'scripts\dev.ps1') }
            return
        }
        & $sdk restore QuickClip.slnx --source $env:NUGET_PACKAGES
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed; inspect missing pinned packages.' }
        & $sdk build QuickClip.slnx -c Release --no-restore "-p:QuickClipProfile=$Profile" -p:RequireCaptureEngine=true
        if ($LASTEXITCODE -ne 0) { throw 'Strict build failed.' }
        if ($Action -eq 'Test') {
            $args = @('test','QuickClip.slnx','-c','Release','--no-build','--no-restore',"-p:QuickClipProfile=$Profile",'--blame-hang-timeout','3m')
            if (!$Filter -and $Profile -eq 'Upstream') {
                $Filter = 'FullyQualifiedName~ProfileTests|FullyQualifiedName~RecordingDefaultsTests|FullyQualifiedName~MenuPackageTests|FullyQualifiedName~CaptureEngineTests|FullyQualifiedName~NativeAudioTimelineTests|FullyQualifiedName~UpdaterTests'
            }
            if ($Filter) { $args += @('--filter',$Filter) }
            & $sdk @args
            if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
        }
    } finally { Pop-Location }
} finally {
    foreach ($v in $vars) { [Environment]::SetEnvironmentVariable($v,$previous[$v],'Process') }
    if ($profileLease) { $profileLease.Dispose() }
    $lease.Dispose()
}

param([switch]$Recover)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

function Assert-OrdinaryPath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:\\') { throw 'Expected a local drive path.' }
    for ($item = $Path; $item; $item = [IO.Path]::GetDirectoryName($item)) {
        try { $attributes = [IO.File]::GetAttributes($item) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] { continue }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'The installation or staging folder contains a redirected path.'
        }
    }
}

function Read-OwnedFiles([string]$Root, [string[]]$AllowedExtra = @()) {
    $inventory = 'quickclip-install-files.txt'
    Assert-OrdinaryPath (Join-Path $Root $inventory)
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $dirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in [IO.File]::ReadAllLines((Join-Path $Root $inventory))) {
        if ($name -notmatch '^[^\\/:*?"<>|\x00-\x1F]+(\\[^\\/:*?"<>|\x00-\x1F]+)*$' -or
            ($name.Split('\') | Where-Object { $_ -match '[. ]$|^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)' })) {
            throw 'The installation inventory contains an unsafe path.'
        }
        if (!$files.Add($name)) { throw 'Duplicate installation inventory entry.' }
        for ($parent = [IO.Path]::GetDirectoryName($name); $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
            $null = $dirs.Add($parent)
        }
    }
    if (!$files.Contains('QuickClip.exe')) { throw 'The installation inventory is incomplete.' }
    $null = $files.Add($inventory)
    # Inno may use a different numeric suffix if an existing log belongs to another application.
    foreach ($item in [IO.Directory]::EnumerateFiles($Root, 'unins*')) {
        $name = [IO.Path]::GetFileName($item)
        if ($name -match '^unins\d{3}\.(exe|dat|msg)$') { $null = $files.Add($name) }
    }
    if (!($files | Where-Object { $_ -match '^unins\d{3}\.exe$' })) {
        throw 'The Personal uninstaller is missing.'
    }

    $extra = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $AllowedExtra) {
        $null = $extra.Add($name)
        for ($parent = [IO.Path]::GetDirectoryName($name); $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
            $null = $extra.Add($parent)
        }
    }

    # Only enter directories owned by the package. An unexpected item means this
    # location needs manual installation, even if the item is next to app files.
    foreach ($dir in (@('') + @($dirs))) {
        $folder = if ($dir) { Join-Path $Root $dir } else { $Root }
        Assert-OrdinaryPath $folder
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($folder)) {
            $relative = $entry.Substring($Root.Length + 1)
            if (!$files.Contains($relative) -and !$dirs.Contains($relative) -and !$extra.Contains($relative)) {
                throw 'The installation folder contains files outside the installer inventory. Use the manual setup.'
            }
            Assert-OrdinaryPath $entry
        }
    }
    return $files
}

function Copy-OwnedFiles([string]$From, [string]$To, [string[]]$Files, [bool]$Overwrite) {
    foreach ($name in $Files) {
        $source = Join-Path $From $name
        $target = Join-Path $To $name
        Assert-OrdinaryPath $source
        Assert-OrdinaryPath $target
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        if (!$Overwrite) {
            [IO.File]::Copy($source, $target, $false)
            $copied = [IO.File]::Open($target, 'Open', 'Write', 'Read')
            try { $copied.Flush($true) } finally { $copied.Dispose() }
            continue
        }
        $staged = Join-Path ([IO.Path]::GetDirectoryName($target)) ('.quickclip-restore-' + [Guid]::NewGuid().ToString('N'))
        [IO.File]::Copy($source, $staged, $false)
        try {
            if ([IO.File]::Exists($target)) { [IO.File]::Replace($staged, $target, [NullString]::Value) }
            else { [IO.File]::Move($staged, $target) }
        } finally { [IO.File]::Delete($staged) }
    }
}

function Assert-Version([string]$File, [string]$Version, [string]$Description) {
    Assert-OrdinaryPath $File
    if (![IO.File]::Exists($File)) { throw 'A required executable is missing.' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($File)
    $numeric = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
    $identity = if ($Description -eq 'QuickClip Personal') { $info.ProductName } else { $info.FileDescription }
    # Inno's PE strings are padded with ASCII spaces.
    if ($numeric -cne "$Version.0" -or ([string]$identity).TrimEnd(' ') -cne $Description) {
        throw "Expected $Description $Version.0 but found $identity ($numeric)."
    }
}

function Wait-ForQuickClipExit([int]$Id, [long]$Start, [string]$Exe, [string]$Ready, [int]$TimeoutMs) {
    $parent = [Diagnostics.Process]::GetProcessById($Id)
    try {
        # Open and keep a real handle before telling QuickClip it can shut down.
        $null = $parent.Handle
        if ($parent.StartTime.ToFileTimeUtc() -ne $Start -or $parent.MainModule.FileName -ine $Exe) {
            throw 'The QuickClip process changed before the update could begin.'
        }
        [IO.File]::WriteAllText($Ready, '')
        if (!$parent.WaitForExit($TimeoutMs)) { throw 'QuickClip did not exit. Setup was not started.' }
        return $parent.ExitCode
    } finally { $parent.Dispose() }
}

function Confirm-ApprovedExit([int]$ExitCode, [string]$Commit) {
    if ($ExitCode -ne 0 -or ![IO.File]::Exists($Commit)) {
        throw 'QuickClip did not complete the approved shutdown. Setup was not started.'
    }
}

function Restore-Installation([string]$Root, [string]$Backup, [string]$Version) {
    $files = @(Read-OwnedFiles $Backup)
    Copy-OwnedFiles $Backup $Root $files $true
    Assert-Version (Join-Path $Root 'QuickClip.exe') $Version 'QuickClip Personal'
}

# Restoring the previous binaries is not sufficient if a failed setup left
# files outside the previous inventory. Preserve unknown files and the recovery
# marker instead of reporting a complete rollback or launching mixed binaries.
function Complete-Restoration([string]$Root, [string]$Pending) {
    $null = Read-OwnedFiles $Root
    [IO.File]::Delete($Pending)
}

function Remove-EmptyParents([string]$Root, [string[]]$Files) {
    $dirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $Files) {
        for ($parent = [IO.Path]::GetDirectoryName($name); $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
            $null = $dirs.Add($parent)
        }
    }
    foreach ($dir in ($dirs | Sort-Object Length -Descending)) {
        $path = Join-Path $Root $dir
        Assert-OrdinaryPath $path
        if ([IO.Directory]::Exists($path) -and !([IO.Directory]::EnumerateFileSystemEntries($path) | Select-Object -First 1)) {
            [IO.Directory]::Delete($path)
        }
    }
}

function Invoke-InstallTransaction([string]$Root, [string]$Backup, [string]$Pending, [string]$Id,
    [string]$Previous, [string]$Version, [scriptblock]$Install, [scriptblock]$RestoreRegistration) {
    $owned = @(Read-OwnedFiles $Root)
    Copy-OwnedFiles $Root $Backup $owned $false
    $marker = [IO.File]::Open($Pending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes($Id)
        $marker.Write($bytes, 0, $bytes.Length)
        $marker.Flush($true)
    } finally { $marker.Dispose() }
    try {
        & $Install
        Assert-Version (Join-Path $Root 'QuickClip.exe') $Version 'QuickClip Personal'
        $updated = @(Read-OwnedFiles $Root $owned)
        $obsolete = @($owned | Where-Object { $_ -notin $updated })
        foreach ($name in $obsolete) {
            $path = Join-Path $Root $name
            Assert-OrdinaryPath $path
            [IO.File]::Delete($path)
        }
        Remove-EmptyParents $Root $obsolete
        [IO.File]::Delete($Pending)
    } catch {
        $errorMessage = $_.Exception.Message
        try {
            Restore-Installation $Root $Backup $Previous
            & $RestoreRegistration
            Complete-Restoration $Root $Pending
        } catch {
            throw "$errorMessage Recovery was incomplete. Keep the staging folder and run the recovery script after Setup exits. $($_.Exception.Message)"
        }
        throw "$errorMessage The previous installation was restored."
    }
}

function Save-UninstallRegistration([string]$BackupFile, [string]$Root, [string]$Version) {
    $keyName = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{90FB3786-4F27-41D1-B7D6-44E671DE28CA}_is1'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyName)
    if (!$key) { throw 'The per-user Personal uninstall registration is missing.' }
    try {
        $uninstaller = $key.GetValue('UninstallString')
        if ($key.GetValue('InstallLocation') -ine ($Root + '\') -or $key.GetValue('DisplayVersion') -cne $Version -or
            $uninstaller -notmatch '^"(.+\\unins\d{3}\.exe)"$' -or [IO.Path]::GetDirectoryName($Matches[1]) -ine $Root) {
            throw 'The existing Personal installer registration does not match this application.'
        }
        Assert-OrdinaryPath $Matches[1]
        if (![IO.File]::Exists($Matches[1])) { throw 'The registered Personal uninstaller is missing.' }
        $values = foreach ($name in $key.GetValueNames()) {
            [pscustomobject]@{ Name = $name; Kind = $key.GetValueKind($name).ToString()
                Data = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
        }
        [IO.File]::WriteAllText($BackupFile, (ConvertTo-Json -InputObject @($values) -Depth 4))
    } finally { $key.Dispose() }
}

function Restore-UninstallRegistration([string]$BackupFile) {
    Assert-OrdinaryPath $BackupFile
    $values = [IO.File]::ReadAllText($BackupFile) | ConvertFrom-Json
    $keyName = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{90FB3786-4F27-41D1-B7D6-44E671DE28CA}_is1'
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyName)
    try {
        foreach ($entry in $values) {
            $value = $entry.Data
            switch ($entry.Kind) {
                'DWord' { $value = [int]$value }
                'QWord' { $value = [long]$value }
                'Binary' { $value = [byte[]]$value }
                'MultiString' { $value = [string[]]$value }
            }
            $key.SetValue($entry.Name, $value, [Microsoft.Win32.RegistryValueKind]$entry.Kind)
        }
    } finally { $key.Dispose() }
}

function Invoke-PersonalInstall {
    $work = $PSScriptRoot
    $updates = [IO.Path]::GetDirectoryName($work)
    $pending = Join-Path $updates 'pending'
    $backup = Join-Path $work 'backup'
    $registration = Join-Path $work 'registration.json'
    $lease = $null
    $packageLock = $null
    $recoveryMutex = $null
    $restart = $false
    $failure = $null

    try {
        Assert-OrdinaryPath $work
        Assert-OrdinaryPath (Join-Path $work 'plan.json')
        $plan = [IO.File]::ReadAllText((Join-Path $work 'plan.json')) | ConvertFrom-Json
        $installed = (Get-ItemProperty -LiteralPath 'HKCU:\Software\QuickClip Personal\Install' -Name Executable).Executable
        if ($installed -ine $plan.Executable -or [IO.Path]::GetFileName($installed) -ine 'QuickClip.exe') {
            throw 'The registered QuickClip Personal installation changed.'
        }
        Assert-OrdinaryPath $installed
        $root = [IO.Path]::GetDirectoryName($installed)
        $leasePath = Join-Path $updates 'install.lock'
        Assert-OrdinaryPath $leasePath
        $lease = [IO.File]::Open($leasePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        Assert-OrdinaryPath $pending
        if ($Recover) {
            $created = $false
            $recoveryMutex = [Threading.Mutex]::new($false, 'Local\QuickClip.Personal.Setup', [ref]$created)
            if (!$created) { throw 'Close QuickClip Personal Setup before restoring the backup.' }
            if (![IO.File]::Exists($pending) -or [IO.File]::ReadAllText($pending) -ne [IO.Path]::GetFileName($work)) {
                throw 'There is no interrupted update in this staging folder.'
            }
            if (Get-Process -Name QuickClip -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $installed }) {
                throw 'Close QuickClip Personal before restoring the backup.'
            }
            Restore-Installation $root $backup $plan.CurrentVersion
            Restore-UninstallRegistration $registration
            Complete-Restoration $root $pending
            $restart = $true
            return
        }
        if ([IO.File]::Exists($pending)) { throw 'An interrupted update needs recovery before another installation.' }
        if ($plan.Package -cne "QuickClip-Personal-$($plan.Version)-setup.exe" -or
            $plan.Version -notmatch '^\d+\.\d+\.\d+$' -or $plan.CurrentVersion -notmatch '^\d+\.\d+\.\d+$' -or
            [version]$plan.Version -le [version]$plan.CurrentVersion) { throw 'Invalid Personal upgrade version.' }
        $setup = Join-Path $work $plan.Package
        Assert-Version $setup $plan.Version 'QuickClip Personal setup (in-place update v1)'
        $packageLock = [IO.File]::Open($setup, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            if ($packageLock.Length -ne $plan.Size -or
                [BitConverter]::ToString($sha.ComputeHash($packageLock)).Replace('-', '') -ine $plan.Sha256) {
                throw 'The staged installer did not pass length and SHA-256 verification.'
            }
        } finally { $sha.Dispose() }

        Assert-Version $installed $plan.CurrentVersion 'QuickClip Personal'
        $null = Read-OwnedFiles $root
        Save-UninstallRegistration $registration $root $plan.CurrentVersion
        $exitCode = Wait-ForQuickClipExit $plan.ParentId ([long]$plan.ParentStart) $installed (Join-Path $work 'ready') 300000
        Confirm-ApprovedExit $exitCode (Join-Path $work 'commit')

        Assert-Version $installed $plan.CurrentVersion 'QuickClip Personal'
        Invoke-InstallTransaction $root $backup $pending ([IO.Path]::GetFileName($work)) $plan.CurrentVersion $plan.Version {
            $setupLog = Join-Path $work 'setup.log'
            $arguments = '/SP- /SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART /RESTARTEXITCODE=20 /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /QCUPDATE={0} /DIR="{1}" /LOG="{2}"' -f $plan.CurrentVersion, $root, $setupLog
            $start = [Diagnostics.ProcessStartInfo]::new($setup, $arguments)
            $start.WorkingDirectory = $work
            $start.UseShellExecute = $false
            $process = [Diagnostics.Process]::Start($start)
            try {
                $process.WaitForExit()
                if ($process.ExitCode -ne 0) { throw "Setup returned exit code $($process.ExitCode)." }
            } finally { $process.Dispose() }
        } { Restore-UninstallRegistration $registration }
        $restart = $true
    } catch {
        $failure = $_.Exception.Message
        try { [IO.File]::WriteAllText((Join-Path $work 'error.txt'), $failure) } catch { }
    } finally {
        if ($packageLock) { $packageLock.Dispose() }
        if ($recoveryMutex) { $recoveryMutex.Dispose() }
        if ($lease) { $lease.Dispose() }
        if ($restart) {
            try { Start-Process -FilePath $installed -WorkingDirectory $root }
            catch { $failure = 'Installation completed. Open QuickClip from the Start menu to restart it.' }
        }
        if ($failure -and ($Recover -or [IO.File]::Exists((Join-Path $work 'ready')))) {
            Add-Type -AssemblyName System.Windows.Forms
            [Windows.Forms.MessageBox]::Show("$failure" + [Environment]::NewLine + [Environment]::NewLine +
                "Update files: $work", 'QuickClip Personal update') | Out-Null
        }
        # Successful handoffs and recovery need no backup or installer after restart.
        if ($restart -and !$failure) {
            try {
                $files = @(Read-OwnedFiles $backup)
                foreach ($name in $files) { [IO.File]::Delete((Join-Path $backup $name)) }
                Remove-EmptyParents $backup $files
                [IO.Directory]::Delete($backup)
                [IO.File]::Delete((Join-Path $work $plan.Package))
                [IO.File]::Delete($registration)
            } catch { }
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-PersonalInstall }

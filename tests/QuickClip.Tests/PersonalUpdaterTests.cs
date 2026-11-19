using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using QuickClip.Updates;

namespace QuickClip.Tests;

public sealed class PersonalUpdaterTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("quickclip-personal-update-").FullName;
    private static readonly IProgress<double> Progress = new Sink();

    [Theory]
    [InlineData("missing digest")]
    [InlineData("wrong digest")]
    [InlineData("wrong header length")]
    [InlineData("interrupted")]
    [InlineData("overlong body")]
    public async Task Invalid_downloads_leave_no_executable_or_partial_file(string failure)
    {
        byte[] bytes = [1, 2, 3];
        long size = failure is "interrupted" ? 4 : failure is "overlong body" ? 2 : 3;
        string? hash = failure switch
        {
            "missing digest" => null,
            "wrong digest" => new string('0', 64),
            _ => Convert.ToHexString(SHA256.HashData(bytes))
        };
        using var client = new HttpClient(new ResponseHandler(bytes, failure is "wrong header length" ? 9 : size));
        string path = Path.Combine(_scratch, "setup.exe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updater.StagePersonalDownloadAsync(client,
            new("https://example.test/setup.exe", size, hash), path, Progress, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_scratch));
    }

    [Fact]
    public async Task Personal_portable_download_stays_an_archive_even_with_a_traversal_entry()
    {
        using var data = new MemoryStream();
        using (var archive = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var payload = archive.CreateEntry("../outside.exe").Open();
            payload.WriteByte(123);
        }
        byte[] bytes = data.ToArray();
        var asset = new ReleaseAsset("https://example.test/portable.zip", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
        using var client = new HttpClient(new ResponseHandler(bytes, bytes.LongLength));
        string path = Path.Combine(_scratch, "portable.zip");
        Assert.Equal(path, await Updater.StagePersonalDownloadAsync(client, asset, path, Progress, CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(path, Assert.Single(Directory.EnumerateFileSystemEntries(_scratch)));
    }

    [Theory]
    [InlineData(true, false, false, "export")]
    [InlineData(false, true, false, "export")]
    [InlineData(false, false, true, "clip")]
    public void Installation_waits_for_active_exports_and_saves(bool editor, bool montage, bool saving, string message)
    {
        Assert.Contains(message, PersonalInstaller.ShutdownBlock(editor, montage, saving)!);
        Assert.Null(PersonalInstaller.ShutdownBlock(false, false, false));
    }

    [Theory]
    [InlineData(false, true, true, "disabled")]
    [InlineData(true, false, true, "registered")]
    [InlineData(true, true, false, "inventory")]
    [InlineData(true, true, true, null)]
    public void Automatic_install_block_explains_ineligible_layout(bool enabled, bool registered, bool inventory, string? expected)
    {
        string? reason = PersonalInstaller.GetAutomaticInstallBlock(enabled, registered, inventory);
        if (expected == null) Assert.Null(reason);
        else Assert.Contains(expected, reason!);
    }

    [Theory]
    [InlineData(false, null, false, "Portable")]
    [InlineData(true, "inventory missing", false, "inventory missing")]
    [InlineData(true, null, false, "protocol")]
    [InlineData(true, null, true, null)]
    public void Manual_fallback_preserves_package_type_and_reason(bool setup, string? block, bool handoff, string? expected)
    {
        string? reason = PersonalInstaller.ManualFallbackReason(setup, block, handoff);
        if (expected == null) Assert.Null(reason);
        else Assert.Contains(expected, reason!);
    }

    [Theory]
    [InlineData(@"C:\Users\Test User\AppData\Local\QuickClip Personal\updates\abc\QuickClip-Personal-1.2.1-setup.exe")]
    [InlineData(@"E:\Alternate Drive\staged package.zip")]
    public void Explorer_select_switch_quotes_only_the_path(string path)
    {
        var start = PersonalInstaller.CreatePackageRevealStartInfo(path);
        Assert.Equal($"/select,\"{path}\"", start.Arguments);
        Assert.Empty(start.ArgumentList);
        Assert.True(start.UseShellExecute);
        Assert.Equal("explorer.exe", Path.GetFileName(start.FileName), ignoreCase: true);
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("C:\\invalid\"name.exe")]
    public void Explorer_reveal_rejects_invalid_package_paths(string path) =>
        Assert.Throws<ArgumentException>(() => PersonalInstaller.CreatePackageRevealStartInfo(path));

    [Fact]
    public void Isolated_or_upstream_profile_cannot_apply_a_personal_update()
    {
        if (AppProfile.IsPersonal) Assert.False(Updater.IsEnabled); // test assembly uses an isolated profile
        Assert.False(PersonalInstaller.CanApply);
    }

    [Fact]
    public async Task Worker_requires_permission_and_a_clean_exit()
    {
        await RunWorkerFixtureAsync("""
            $commit = Join-Path $Scratch 'commit'
            try {
                Confirm-ApprovedExit 0 $commit
                throw 'An unapproved exit was accepted'
            } catch {
                if ($_.Exception.Message -notlike '*approved shutdown*') { throw }
            }
            [IO.File]::WriteAllText($commit, '')
            try {
                Confirm-ApprovedExit 3 $commit
                throw 'An abnormal exit was accepted'
            } catch {
                if ($_.Exception.Message -notlike '*approved shutdown*') { throw }
            }
            Confirm-ApprovedExit 0 $commit
            """);
    }

    [Fact]
    public async Task Worker_rejects_a_redirected_owned_directory()
    {
        await RunWorkerFixtureAsync("""
            $root = Join-Path $Scratch 'app'
            $outside = Join-Path $Scratch 'external'
            $null = [IO.Directory]::CreateDirectory($root)
            $null = [IO.Directory]::CreateDirectory($outside)
            [IO.File]::WriteAllLines((Join-Path $root 'quickclip-install-files.txt'), @('QuickClip.exe', 'ffmpeg\ffmpeg.exe'))
            [IO.File]::WriteAllText((Join-Path $root 'unins000.exe'), 'fixture')
            [IO.File]::WriteAllText((Join-Path $outside 'ffmpeg.exe'), 'unchanged')
            $link = Join-Path $root 'ffmpeg'
            $null = New-Item -ItemType Junction -Path $link -Target $outside
            try {
                try {
                    $null = Read-OwnedFiles $root
                    throw 'Followed a redirected installer directory'
                } catch {
                    if ($_.Exception.Message -notlike '*redirected path*') { throw }
                }
                if ([IO.File]::ReadAllText((Join-Path $outside 'ffmpeg.exe')) -ne 'unchanged') { throw 'External fixture changed' }
            } finally { [IO.Directory]::Delete($link) }
            """);
    }

    [Fact]
    public async Task Worker_wait_requires_the_correct_process_and_actual_exit()
    {
        string fixture = """
            $ready = Join-Path $Scratch 'ready'
            $command = "while (![IO.File]::Exists('" + $ready.Replace("'", "''") + "')) { Start-Sleep -Milliseconds 50 }; exit 13"
            $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe'))
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
            $child = [Diagnostics.Process]::Start($psi)
            try {
                $start = $child.StartTime.ToFileTimeUtc()
                $exe = $child.MainModule.FileName
                try {
                    $null = Wait-ForQuickClipExit $child.Id ($start - 1) $exe $ready 100
                    throw 'Wrong process start was accepted'
                } catch {
                    if ($_.Exception.Message -notlike '*process changed*') { throw }
                }
                if ([IO.File]::Exists($ready)) { throw 'Invalid parent signaled ready' }
                try {
                    $null = Wait-ForQuickClipExit $child.Id $start $exe (Join-Path $Scratch 'ready-timeout') 10
                    throw 'Process was treated as exited while running'
                } catch {
                    if ($_.Exception.Message -notlike '*did not exit*') { throw }
                }
                $exitCode = Wait-ForQuickClipExit $child.Id $start $exe $ready 30000
                if ($exitCode -ne 13) { throw "Expected exit code 13, got $exitCode" }
            } finally {
                if (!$child.HasExited) { $child.Kill() }
                $child.Dispose()
            }
            """;
        await RunWorkerFixtureAsync(fixture);
    }

    [Fact]
    public async Task Failed_install_restores_files_and_interrupted_recovery_leaves_a_retry_marker()
    {
        if (!AppProfile.IsPersonal) return;
        string fixture = """
            $root = Join-Path $Scratch 'nondefault installation'
            $backup = Join-Path $Scratch 'backup'
            $pending = Join-Path $Scratch 'pending'
            $null = [IO.Directory]::CreateDirectory($root)
            $exe = Join-Path $root 'QuickClip.exe'
            [IO.File]::Copy($Application, $exe)
            [IO.File]::WriteAllText((Join-Path $root 'unins000.exe'), 'uninstall test fixture')
            $data = Join-Path $root 'runtime.dll'
            [IO.File]::WriteAllText($data, 'old runtime')
            $inventory = Join-Path $root 'quickclip-install-files.txt'
            [IO.File]::WriteAllLines($inventory, @('QuickClip.exe', 'runtime.dll'))
            $script:registrationRestored = $false
            try {
                Invoke-InstallTransaction $root $backup $pending 'test' $Version '99.0.0' {
                    if (![IO.File]::Exists($pending)) { throw 'Setup started without a recovery marker' }
                    [IO.File]::WriteAllText($exe, 'partially written exe')
                    [IO.File]::Delete($data)
                    throw 'Synthetic setup failure'
                } { $script:registrationRestored = $true }
                throw 'The transaction reported success'
            } catch {
                if ($_.Exception.Message -notlike 'Synthetic setup failure*previous installation was restored*') { throw }
            }
            if (!$script:registrationRestored -or [IO.File]::Exists($pending) -or [IO.File]::ReadAllText($data) -ne 'old runtime') {
                throw 'Incomplete rollback'
            }
            [IO.File]::WriteAllText($data, 'second interrupted update')
            [IO.File]::WriteAllText($pending, 'test')
            $lock = [IO.File]::Open($data, 'Open', 'Read', 'None')
            try {
                try {
                    Restore-Installation $root $backup $Version
                    throw 'Locked application file unexpectedly allowed restore'
                } catch {
                    if (!$_.Exception.InnerException -or $_.Exception.InnerException -isnot [IO.IOException]) { throw }
                }
                if (![IO.File]::Exists($pending)) { throw 'Recovery marker was removed too early' }
            } finally { $lock.Dispose() }
            Restore-Installation $root $backup $Version
            if ([IO.File]::ReadAllText($data) -ne 'old runtime') { throw 'Retry did not restore the runtime' }
            """;
        await RunWorkerFixtureAsync(fixture);
    }

    [Fact]
    public async Task Partial_install_with_unknown_new_files_retains_recovery_marker()
    {
        if (!AppProfile.IsPersonal) return;
        await RunWorkerFixtureAsync("""
            $root = Join-Path $Scratch 'partial installation'
            $backup = Join-Path $Scratch 'backup'
            $pending = Join-Path $Scratch 'pending'
            $null = [IO.Directory]::CreateDirectory($root)
            $exe = Join-Path $root 'QuickClip.exe'
            [IO.File]::Copy($Application, $exe)
            [IO.File]::WriteAllText((Join-Path $root 'unins000.exe'), 'fixture uninstaller')
            $old = Join-Path $root 'old.dll'
            $extra = Join-Path $root 'new.dll'
            [IO.File]::WriteAllText($old, 'old content')
            [IO.File]::WriteAllLines((Join-Path $root 'quickclip-install-files.txt'), @('QuickClip.exe', 'old.dll'))
            $script:registrationRestored = $false
            try {
                Invoke-InstallTransaction $root $backup $pending 'test' $Version '99.0.0' {
                    [IO.File]::WriteAllText($old, 'overwritten')
                    [IO.File]::WriteAllText($extra, 'unknown new content')
                    throw 'Synthetic partial installation failure'
                } { $script:registrationRestored = $true }
                throw 'The transaction reported success'
            } catch {
                if ($_.Exception.Message -notlike 'Synthetic partial installation failure*Recovery was incomplete*') { throw }
            }
            if (!$script:registrationRestored -or ![IO.File]::Exists($pending) -or
                [IO.File]::ReadAllText($old) -ne 'old content' -or
                [IO.File]::ReadAllText($extra) -ne 'unknown new content') {
                throw 'Rollback did not preserve recovery state and unknown files'
            }
            try {
                Complete-Restoration $root $backup $pending
                throw 'Recovery completed despite unknown files'
            } catch {
                if ($_.Exception.Message -notlike '*outside the installer inventory*') { throw }
            }
            if (![IO.File]::Exists($pending)) { throw 'Recovery marker was cleared too early' }
            # Only the fixture owns this known generated file. A real worker must not remove unknown files.
            [IO.File]::Delete($extra)
            [IO.File]::WriteAllText($old, 'unexpected modification')
            try {
                Complete-Restoration $root $backup $pending
                throw 'Changed file was accepted'
            } catch {
                if ($_.Exception.Message -notlike '*content differs from the backup*') { throw }
            }
            if (![IO.File]::Exists($pending)) { throw 'Recovery marker was cleared before content validation' }
            Restore-Installation $root $backup $Version
            Complete-Restoration $root $backup $pending
            if ([IO.File]::Exists($pending)) { throw 'Verified recovery did not clear its marker' }
            """);
    }

    [Fact]
    public async Task Worker_rejects_inventory_with_missing_owned_file()
    {
        await RunWorkerFixtureAsync("""
            [IO.File]::WriteAllText((Join-Path $Scratch 'QuickClip.exe'), 'fixture')
            [IO.File]::WriteAllText((Join-Path $Scratch 'unins000.exe'), 'fixture')
            [IO.File]::WriteAllLines((Join-Path $Scratch 'quickclip-install-files.txt'), @('QuickClip.exe', 'missing.dll', 'worker.ps1', 'fixture.ps1'))
            try {
                $null = Read-OwnedFiles $Scratch
                throw 'Missing owned file was accepted'
            } catch {
                if ($_.Exception.Message -notlike '*missing file*') { throw }
            }
            """);
    }

    [Theory]
    [InlineData(@"..\outside.dll")]
    [InlineData(@"C:\outside.dll")]
    [InlineData(@"runtime.dll:stream")]
    [InlineData(@"NUL.txt")]
    public async Task Worker_does_not_accept_unsafe_inventory_paths(string unsafePath)
    {
        File.WriteAllLines(Path.Combine(_scratch, "quickclip-install-files.txt"), ["QuickClip.exe", unsafePath]);
        await RunWorkerFixtureAsync("""
            try {
                $null = Read-OwnedFiles $Scratch
                throw 'Accepted unsafe inventory'
            } catch {
                if ($_.Exception.Message -notlike '*unsafe path*') { throw }
            }
            """);
    }

    private async Task RunWorkerFixtureAsync(string fixture)
    {
        string worker = Path.Combine(_scratch, "worker.ps1");
        using (var resource = typeof(Updater).Assembly.GetManifestResourceStream("QuickClip.PersonalInstall.ps1")!)
        using (var output = File.Create(worker))
            await resource.CopyToAsync(output);

        string script = Path.Combine(_scratch, "fixture.ps1");
        await File.WriteAllTextAsync(script, """
            param($Scratch, $Application, $Version)
            $ErrorActionPreference = 'Stop'
            . (Join-Path $Scratch 'worker.ps1')

            """ + fixture);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            _scratch, typeof(AppVersion).Assembly.Location, AppVersion.Current.ToString() })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.True(process.ExitCode == 0, await error);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private sealed class ResponseHandler(byte[] bytes, long length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentLength = length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class Sink : IProgress<double>
    {
        public void Report(double value) { }
    }
}

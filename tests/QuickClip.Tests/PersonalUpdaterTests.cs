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
            $release = Join-Path $Scratch 'release'
            $command = "while (![IO.File]::Exists('" + $release.Replace("'", "''") + "')) { Start-Sleep -Milliseconds 50 }; exit 13"
            $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe'))
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
            $child = [Diagnostics.Process]::Start($psi)
            try {
                $ready = Join-Path $Scratch 'ready'
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
                    $null = Wait-ForQuickClipExit $child.Id $start $exe $ready 10
                    throw 'Process was treated as exited while running'
                } catch {
                    if ($_.Exception.Message -notlike '*did not exit*') { throw }
                }
                [IO.File]::WriteAllText($release, '')
                if ((Wait-ForQuickClipExit $child.Id $start $exe $ready 30000) -ne 13) { throw 'Wrong exit code' }
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

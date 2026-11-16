using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using QuickClip.Shell;

namespace QuickClip.Updates;

/// <summary>Hands an approved update to a worker running outside the installation directory.</summary>
internal static class PersonalInstaller
{
    internal const string Protocol = "QuickClip Personal setup (in-place update v1)";
    internal const string Inventory = "quickclip-install-files.txt";
    private static string UpdateRoot => Path.Combine(AppProfile.LocalDirectory, "updates");
    internal static string LeasePath => Path.Combine(UpdateRoot, "install.lock");

    internal static string? PendingRecoveryScript()
    {
        string pending = Path.Combine(UpdateRoot, "pending");
        if (!File.Exists(pending)) return null;
        RequireOrdinaryPath(pending);
        string id = File.ReadAllText(pending);
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidOperationException("The pending update marker is damaged.");
        string script = Path.Combine(UpdateRoot, id, "install.ps1");
        RequireOrdinaryPath(script);
        return script;
    }

    internal static string? ShutdownBlock(bool editorExport, bool montageExport, bool savingClip) =>
        editorExport || montageExport ? "Finish the active export before installing the update." :
        savingClip ? "Finish saving the current clip before installing the update." : null;

    internal static bool CanApply => Updater.IsEnabled && Updater.IsPersonalInstalledCopy &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, Inventory));

    internal static bool CanStartInstalledApp()
    {
        if (!Updater.IsEnabled || !Updater.IsPersonalInstalledCopy) return true;
        try
        {
            using var lease = File.Open(LeasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
    }

    internal static string CreateStagingDirectory()
    {
        RequireOrdinaryPath(AppProfile.LocalDirectory);
        Directory.CreateDirectory(UpdateRoot);
        RequireOrdinaryPath(UpdateRoot);
        string work = Path.Combine(UpdateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    // Inspect existing ancestors too. Unknown or redirected locations are left for manual installation.
    internal static void RequireOrdinaryPath(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (!Path.IsPathFullyQualified(path) || root?.Length != 3 || root[1] != ':')
            throw new InvalidOperationException("The updater requires a local drive.");
        for (string? entry = path; entry != null; entry = Path.GetDirectoryName(entry))
        {
            try
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The updater cannot use a redirected file or folder.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }

    internal static bool SupportsHandoff(string package, AppVersion version)
    {
        var info = FileVersionInfo.GetVersionInfo(package);
        // Inno pads its PE string resources. Compare the description without that padding.
        return info.FileDescription?.TrimEnd(' ') == Protocol &&
            (info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart) ==
            (version.Major, version.Minor, version.Patch, 0);
    }

    internal static async Task<Handoff> PrepareAsync(ReleaseInfo release, string package, CancellationToken ct)
    {
        if (!CanApply || release.Setup is not { Sha256: { } hash } asset || !SupportsHandoff(package, release.Version) ||
            !Updater.TryPersonalVersion(release.Tag, out var version) || version != release.Version || !(version > AppVersion.Current))
            throw new InvalidOperationException("This installation requires the manual setup.");
        string exe = Environment.ProcessPath!;
        string work = Path.GetDirectoryName(package)!;
        RequireOrdinaryPath(exe);
        RequireOrdinaryPath(package);
        if (!string.Equals(Path.GetDirectoryName(work), UpdateRoot, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(package) != $"QuickClip-Personal-{version}-setup.exe")
            throw new InvalidOperationException("Unexpected update staging location.");

        using var current = Process.GetCurrentProcess();
        string script = Path.Combine(work, "install.ps1");
        using (var resource = typeof(PersonalInstaller).Assembly.GetManifestResourceStream("QuickClip.PersonalInstall.ps1")!)
        using (var output = new FileStream(script, FileMode.CreateNew))
            await resource.CopyToAsync(output, ct);

        var plan = new
        {
            Executable = exe, Package = Path.GetFileName(package), asset.Size, Sha256 = hash,
            CurrentVersion = AppVersion.Current.ToString(), Version = version.ToString(), ParentId = current.Id,
            ParentStart = current.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        await File.WriteAllTextAsync(Path.Combine(work, "plan.json"), JsonSerializer.Serialize(plan), ct);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = work
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-ExecutionPolicy", "Bypass", "-File", script })
            start.ArgumentList.Add(argument);

        var process = Process.Start(start) ?? throw new Win32Exception("Could not start the update worker.");
        var handoff = new Handoff(work, process);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            while (!File.Exists(Path.Combine(work, "ready")))
            {
                if (process.HasExited)
                {
                    string error = Path.Combine(work, "error.txt");
                    throw new InvalidOperationException(File.Exists(error) ? File.ReadAllText(error) : "The update worker did not start.");
                }
                await Task.Delay(100, deadline.Token);
            }
            return handoff;
        }
        catch
        {
            handoff.Dispose();
            throw;
        }
    }

    internal sealed class Handoff(string work, Process worker) : IDisposable
    {
        private bool _committed;

        internal void Commit()
        {
            if (worker.HasExited) throw new InvalidOperationException("The update worker stopped before QuickClip could close.");
            using (new FileStream(Path.Combine(work, "commit"), FileMode.CreateNew)) { }
            _committed = true;
        }

        public void Dispose()
        {
            try
            {
                if (!_committed && !worker.HasExited) worker.Kill();
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception ex) { Log.Write("Could not stop the update worker: " + ex.Message); }
            finally { worker.Dispose(); }
        }
    }
}

using System.Windows;
using QuickClip.Shell;
using QuickClip.Updates;

namespace QuickClip;

public partial class App
{
    internal static bool IsQuittingForUpdate => Current is App { _quittingForUpdate: true };
    private bool _quittingForUpdate;

    private bool CanOpenDuringUpdate(string[] args)
    {
        if (!Updater.IsEnabled || !Updater.IsPersonalInstalledCopy) return true;
        try
        {
            if (!PersonalInstaller.CanStartInstalledApp()) return false;
            if (PersonalInstaller.PendingRecoveryScript() is not { } script) return true;
            if (!args.Contains("--windows-startup"))
                MessageBox.Show("A previous QuickClip update was interrupted. Close any QuickClip Setup window, then restore the backup before reopening QuickClip.\n\n" +
                    $"Run this in Windows PowerShell:\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Recover",
                    "QuickClip Personal update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show("QuickClip could not read its pending update. " + ex.Message, "QuickClip Personal update",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return false;
    }

    internal string? UpdateShutdownBlock =>
        PersonalInstaller.ShutdownBlock(_main?.HasActiveEditorExport == true, _main?.HasActiveMontageExport == true,
            _main?.IsSavingClip == true || BackgroundActivity.Snapshot(0).Any(a => a.Label == "Saving clip"));

    internal async Task<bool> QuitForPersonalUpdateAsync(PersonalInstaller.Handoff handoff)
    {
        if (_quitting || _quittingForUpdate) return false;
        if (UpdateShutdownBlock is { } block) throw new InvalidOperationException(block);
        _quittingForUpdate = true;
        try { return await QuitAsync(handoff.Commit); }
        finally { _quittingForUpdate = false; }
    }
}

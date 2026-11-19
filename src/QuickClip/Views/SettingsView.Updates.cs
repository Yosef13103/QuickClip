using System.Diagnostics;
using System.Windows;
using QuickClip.Updates;

namespace QuickClip.Views;

public partial class SettingsView
{
    private async Task<string?> DownloadAndApplyPersonalAsync(ReleaseInfo release, IProgress<double> progress)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        string package = await Updater.DownloadPersonalAsync(release, progress, timeout.Token);
        Log.Event(DiagnosticCategory.Updater, "Personal package downloaded and verified");
        bool setup = Path.GetExtension(package).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        string? installBlock = PersonalInstaller.AutomaticInstallBlock;
        string? manualReason = PersonalInstaller.ManualFallbackReason(setup, installBlock,
            setup && installBlock == null && PersonalInstaller.SupportsHandoff(package, release.Version));
        if (manualReason != null)
        {
            string instructions = setup
                ? "Verified installer ready. Quit QuickClip before running setup."
                : "Verified portable ZIP ready. Quit QuickClip and extract it to a new folder.";
            try { Process.Start(PersonalInstaller.CreatePackageRevealStartInfo(package))?.Dispose(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Essential(DiagnosticCategory.Updater, "Could not reveal verified update package", ex);
                instructions += " Explorer could not open the package; use the path below.";
            }
            return $"{manualReason}\n{instructions}\n{package}";
        }

        var app = (App)Application.Current;
        if (app.UpdateShutdownBlock is { } block) return block;
        if (!ModalDialog.AskConfirmation(Window.GetWindow(this),
                $"QuickClip Personal {release.Version.Display} is verified and ready. Installing will close QuickClip, stop any recording, and discard the current unsaved replay buffer. Save any replay you want to keep first.\n\nQuickClip will reopen after setup finishes.",
                "Install QuickClip Personal update", "Install and restart"))
            return "Installation canceled.";

        UpdateStatusText.Text = "Preparing verified setup…";
        UpdateProgress.IsIndeterminate = true;
        using var handoff = await PersonalInstaller.PrepareAsync(release, package, timeout.Token);
        Log.Event(DiagnosticCategory.Updater, "Verified installer handoff prepared");
        return await app.QuitForPersonalUpdateAsync(handoff) ? null : "Installation canceled. QuickClip is still open.";
    }
}

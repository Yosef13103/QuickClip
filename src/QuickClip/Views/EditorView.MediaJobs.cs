using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickClip.Gallery;
using QuickClip.Media;
using Container = QuickClip.Media.Container;

namespace QuickClip.Views;

public partial class EditorView
{
    private sealed record ExportLimit(double? MB, bool Adaptive, bool UseEditorQuality)
    {
        internal static readonly ExportLimit Existing = new(null, false, true);
    }

    private void ShareSelection_Click(object sender, RoutedEventArgs e) => SubmitEditedShare(null);

    private void ShareSelectionOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || !SourceReady || CurrentFile is not { } source) return;
        SharingDialogs.OpenMenu(anchor, profile =>
        {
            if (EditorSource.MatchesLoadedPreview(CurrentFile, source)) SubmitEditedShare(profile);
        }, () => _ = ClipSharing.ShareAsync(source));
    }

    private void SubmitEditedShare(SharingProfile? profile)
    {
        if (CurrentFile is not { } path || !SourceReady || !VerifySource(path) || _media?.Video == null) return;
        var snapshot = CreateDraft("", Containers.Mp4);
        _ = ClipSharing.ShareAsync(path, profile, snapshot);
    }

    private ExportLimit? PickExportLimit(Container container, bool overwrite = false)
    {
        if (OwnerWindow is not { Content: Panel } owner) return overwrite ? null : ExportLimit.Existing;
        ExportRequest current = CreateDraft("", container);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "File size", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var presets = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        panel.Children.Add(presets);
        var choices = new List<(RadioButton Button, double? MB)>();
        var group = Guid.NewGuid().ToString("N");
        var custom = new TextBox { Text = SharingDialogs.Format(current.TargetSizeMB), MaxLength = 24,
            MinWidth = 48, Margin = new Thickness(6, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(custom, "Custom size in MB");
        foreach (double? size in new double?[] { null, 10, 30, 50, 100, 0 })
        {
            var button = new RadioButton { GroupName = group, Content = size == null ? "Original" : size == 0 ? "Custom" : $"{size} MB",
                Margin = new Thickness(0, 0, 4, 5), Padding = new Thickness(7, 10, 7, 10),
                HorizontalContentAlignment = HorizontalAlignment.Center };
            if (owner.TryFindResource("ExportSizeTile") is Style tile) button.Style = tile;
            choices.Add((button, size));
            if (size == 0)
            {
                var cell = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 5), ToolTip = "Custom size (MB)" };
                DockPanel.SetDock(button, Dock.Left);
                button.Margin = new Thickness(0);
                cell.Children.Add(button);
                cell.Children.Add(custom);
                presets.Children.Add(cell);
            }
            else presets.Children.Add(button);
        }
        var adaptive = new CheckBox { Content = "Lower resolution if needed", Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "Allow a smaller video resolution to keep more detail within the chosen file size." };
        panel.Children.Add(adaptive);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "SubtleText");
        panel.Children.Add(summary);
        var warning = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        panel.Children.Add(warning);
        ExportLimit? accepted = null;
        foreach (var choice in choices) choice.Button.Checked += (_, _) => Refresh();
        custom.GotKeyboardFocus += (_, _) => choices[^1].Button.IsChecked = true;
        custom.TextChanged += (_, _) => { choices[^1].Button.IsChecked = true; Refresh(); };
        adaptive.Checked += (_, _) => Refresh();
        adaptive.Unchecked += (_, _) => Refresh();
        choices[current.Quality == "size" ? choices.Count - 1 : 0].Button.IsChecked = true;
        Refresh();
        return ModalDialog.Run(owner, overwrite ? "Save over original" : "Export clip", panel,
            overwrite ? "Save" : "Export", "Cancel", initialFocus: choices.First(x => x.Button.IsChecked == true).Button,
            accent: true, wide: true, validate: () => { accepted = Refresh(); return accepted != null; }) ? accepted : null;

        ExportLimit? Refresh()
        {
            var selected = choices.FirstOrDefault(x => x.Button.IsChecked == true);
            double? value = selected.MB;
            bool capped = value.HasValue;
            adaptive.Visibility = capped ? Visibility.Visible : Visibility.Collapsed;
            warning.Text = "";
            if (value == 0)
            {
                if (!SharingProfiles.TryLimit(custom.Text, out double size))
                {
                    summary.Text = "Enter a size between 0 and 4000 MB (greater than zero).";
                    return null;
                }
                value = size;
            }
            var limit = new ExportLimit(value, capped && adaptive.IsChecked == true, false);
            try
            {
                var plan = BuildExportPlan("", container, limit, overwrite);
                _ = ExportBuilder.BuildArgs(plan);
                double estimate = plan.Media.Size * Math.Min(1, plan.Duration / plan.Media.Duration) / TargetSizeExport.BytesPerMB;
                if (plan.Crop is { } crop && plan.Media.Video is { Width: > 0, Height: > 0 } video)
                {
                    double ratio = Math.Clamp(crop.W * (double)crop.H / (video.Width * (double)video.Height), 0, 1);
                    double audio = Math.Clamp(plan.Media.Audio.Sum(a => a.BitRate > 0 ? a.BitRate : 160_000L) * plan.Media.Duration / (8d * plan.Media.Size), 0, 1);
                    estimate *= audio + (1 - audio) * ratio;
                }
                summary.Text = capped ? $"Up to {plan.TargetSizeMB:0.##} MB" : $"About {estimate:0.##} MB of source footage";
                if (capped && plan.TargetSizeMB < estimate * .35)
                    warning.Text = "Large size reduction — video quality may be noticeably lower.";
                if (overwrite) summary.Text += " · replaces this clip";
                return limit;
            }
            catch (ExportException ex) { summary.Text = ex.Message; return null; }
        }
    }

    private ExportRequest BuildExportPlan(string output, Container container, ExportLimit selection, bool overwrite)
    {
        var raw = CreateDraft(output, container);
        double? limit = selection.MB ?? (selection.UseEditorQuality && raw.Quality == "size" ? raw.TargetSizeMB : null);
        var request = BuildRequest(output, container, limit.HasValue || raw.Quality == "size" ? "original" : null);
        if (limit is { } cap)
        {
            if (TargetSizeExport.Ceiling(request, cap) < request.Media.Size * request.Duration / request.Media.Duration)
                request = request with { VideoMode = VideoMode.Encode, EncoderId = raw.EncoderId.Length == 0 ? SameCodecEncoder(container) : raw.EncoderId,
                    Start = raw.Start };
            if (request.EncoderId.Length == 0) request = request with { EncoderId = SameCodecEncoder(container) };
        }
        request = request with
        {
            MergeAudio = !overwrite && request.Tracks.Count > 1,
            HybridAudio = overwrite && container.HasVideo && !container.SingleAudioTrack,
        };
        return limit is { } max ? TargetSizeExport.Plan(request, max, selection.Adaptive) : request;
    }

    private async Task QueueExportAsync(string destination, ExportRequest plan, ClipDetails details, bool wait)
    {
        var sources = MediaProcessing.Sources(plan.Media.Path, plan.Tracks);
        if (_sourceStamp != sources[0].Stamp)
            throw new ExportException("The selected clip changed before export. Reopen it and try again.");
        var exported = AppProfile.IsPersonal ? details.ForExport(plan.Start, plan.End) : null;
        string? fallback = (EncoderCombo.SelectedItem as Choice)?.Value is not string { Length: > 0 } &&
            VideoEncoders.Find(plan.EncoderId) is { Hardware: true } hardware
            ? EncodersFor(plan.Container).FirstOrDefault(e => !e.Hardware && e.Family == hardware.Family)?.Id : null;
        _ = ExportBuilder.BuildArgs(plan);
        var submission = MediaJobs.Current.Submit(new(Path.GetFileName(destination), false, sources, destination,
            (job, ct) => ProcessExportAsync(destination, plan, exported, fallback, job, ct)));
        StatusText.Text = "Export queued. Follow it in Media jobs.";
        if (wait && (await submission.Completion).Output is not { })
            throw new ExportException("The queued export did not publish a file.");
    }

    private static async Task<string> ProcessExportAsync(string destination, ExportRequest plan, ClipDetails? details,
        string? fallback, MediaJobContext job, CancellationToken ct)
    {
        long expected = plan.SizeCeilingBytes ?? plan.Media.Size;
        MediaProcessing.CheckFreeSpace(Path.GetDirectoryName(destination)!, expected);
        return await MediaProcessing.PublishAsync(destination, plan.Container.Extension, details,
            staging => EncodeExportAsync(plan with { OutputPath = staging }, expected, fallback, job, ct), ct);
    }

    private static async Task EncodeExportAsync(ExportRequest plan, long ceiling, string? fallback, MediaJobContext job, CancellationToken ct)
    {
        if (plan.Quality == "size")
        {
            await TargetSizeExport.RunAsync(plan, ceiling, fallback,
                new ProgressCallback<(int Attempt, double Progress)>(p => job.Progress(p.Attempt, p.Progress)),
                job.Validating, ct, job);
            return;
        }
        await job.EncodeAsync(plan.VideoMode == VideoMode.Encode ? plan.EncoderId : "", async () =>
        {
            await ExportRunner.RunAsync(ExportBuilder.BuildArgs(plan), plan.OutputPath, plan.Duration,
                new ProgressCallback<double>(p => job.Progress(1, p)), ct);
            return true;
        }, ct);
        job.Validating();
        await TargetSizeExport.ValidateAsync(plan, ct);
    }
}

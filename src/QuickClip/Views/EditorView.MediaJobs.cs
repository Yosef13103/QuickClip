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

    private sealed record LimitChoice(string Name, double? MB)
    {
        public override string ToString() => Name;
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

    private ExportLimit? PickExportLimit(Container container)
    {
        if (OwnerWindow is not { Content: Panel } owner) return ExportLimit.Existing;
        ExportRequest current = CreateDraft("", container);
        var panel = new StackPanel();
        var choices = new ComboBox { Margin = new Thickness(0, 4, 0, 6) };
        choices.ItemsSource = new LimitChoice[]
        {
            new("Original", null), new("Up to 10 MB", 10), new("Up to 30 MB", 30), new("Up to 50 MB", 50),
            new("Up to 100 MB", 100), new("Custom MB", 0),
        };
        AutomationProperties.SetName(choices, "File size");
        panel.Children.Add(new TextBlock { Text = "File size" });
        panel.Children.Add(choices);
        var custom = new TextBox { Text = SharingDialogs.Format(current.TargetSizeMB), MaxLength = 24 };
        AutomationProperties.SetName(custom, "Custom size in MB");
        panel.Children.Add(custom);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(summary);
        var advanced = new Expander { Header = "Advanced options", Margin = new Thickness(0, 10, 0, 0) };
        var options = new StackPanel();
        var adaptive = new CheckBox { Content = "Allow lower resolution to fit", Margin = new Thickness(0, 4, 0, 0) };
        options.Children.Add(adaptive);
        var editAdvanced = new Button { Content = "Edit format, encoder, video and audio options…", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0) };
        options.Children.Add(editAdvanced);
        advanced.Content = options;
        panel.Children.Add(advanced);

        bool showAdvanced = false;
        editAdvanced.Click += (_, _) => showAdvanced = true;
        choices.SelectionChanged += (_, _) => Refresh();
        custom.TextChanged += (_, _) => Refresh();
        adaptive.Checked += (_, _) => Refresh();
        adaptive.Unchecked += (_, _) => Refresh();
        choices.SelectedIndex = current.Quality == "size" ? 5 : 0;
        while (ModalDialog.Run(owner, "Export clip", panel, "Choose file…", "Cancel", initialFocus: choices, dismissOnClick: editAdvanced))
        {
            if (Read() is { } limit)
            {
                try { _ = ExportBuilder.BuildArgs(BuildExportPlan("", container, limit, false)); return limit; }
                catch (ExportException ex) { summary.Text = ex.Message; }
            }
        }
        if (showAdvanced)
        {
            OutputExpander.IsExpanded = EncodingExpander.IsExpanded = FpsExpander.IsExpanded = true;
            FormatCombo.BringIntoView();
            FormatCombo.Focus();
        }
        return null;

        ExportLimit? Read()
        {
            double? value = ((LimitChoice)choices.SelectedItem).MB;
            if (value != 0) return new(value, value.HasValue && adaptive.IsChecked == true, false);
            return SharingProfiles.TryLimit(custom.Text, out double size) ? new(size, adaptive.IsChecked == true, false) : null;
        }

        void Refresh()
        {
            bool capped = (choices.SelectedItem as LimitChoice)?.MB != null;
            custom.Visibility = (choices.SelectedItem as LimitChoice)?.MB == 0 ? Visibility.Visible : Visibility.Collapsed;
            adaptive.Visibility = capped ? Visibility.Visible : Visibility.Collapsed;
            if (Read() is not { } limit) { summary.Text = "Enter a positive size up to 4000 MB."; return; }
            try
            {
                var plan = BuildExportPlan("", container, limit, false);
                _ = ExportBuilder.BuildArgs(plan);
                string format = container.Extension.TrimStart('.').ToUpperInvariant();
                var v = plan.Media.Video;
                int width = plan.Crop?.W ?? v?.Width ?? 0, height = plan.Crop?.H ?? v?.Height ?? 0;
                int side = plan.MaxShortSide;
                if (plan.Container.IsGif && side == 0) side = 480;
                if (side > 0 && Math.Min(width, height) > side)
                {
                    double scale = side / (double)Math.Min(width, height);
                    width = (int)Math.Round(width * scale / 2) * 2;
                    height = (int)Math.Round(height * scale / 2) * 2;
                }
                double fps = plan.Fps > 0 ? plan.Fps : v?.Fps ?? 0;
                string video = width > 0 && container.HasVideo ? $" · {width}×{height} · {fps:0.##} fps" : "";
                double estimate = plan.Media.Size * Math.Min(1, plan.Duration / plan.Media.Duration) / TargetSizeExport.BytesPerMB;
                string size = capped
                    ? $"Up to {SharingDialogs.Format(plan.TargetSizeMB)} MB (verified before saving)."
                    : $"Source footage estimate: {estimate:0.##} MB. Export size varies with encoding.";
                string quality = plan.VideoMode == VideoMode.Copy ? "Video copied when possible." :
                    container.IsAudioOnly ? "Audio exported with the options selected in the editor." : "Video will be re-encoded.";
                summary.Text = $"{Fmt.Time(plan.Duration, false)} · {format}{video}\n{size}\n{quality}" +
                    (capped ? " Smaller caps can reduce quality. The job may take up to three attempts." : "") +
                    (limit.Adaptive ? " The job may use a smaller resolution or a slower CPU encoder." : "");
            }
            catch (ExportException ex) { summary.Text = ex.Message; }
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

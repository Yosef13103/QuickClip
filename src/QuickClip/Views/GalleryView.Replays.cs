using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Media;
using QuickClip.Shell;

namespace QuickClip.Views;

public partial class GalleryView
{
    private async void FindOverlaps_Click(object sender, RoutedEventArgs e)
    {
        if (_released || !PersonalLibrary.Enabled || _loading)
        {
            ModalDialog.Show(Window.GetWindow(this), "Wait for the Gallery index to finish loading, or enable the library index.", "Find overlapping clips");
            return;
        }
        FindOverlapsButton.IsEnabled = false;
        try
        {
            var clips = _clips.ToDictionary(c => LibraryFiles.PathKey(c.Path), StringComparer.OrdinalIgnoreCase);
            var keys = clips.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ReplayCluster[] clusters;
            using (BackgroundActivity.Begin("Finding overlapping clips"))
                clusters = await Task.Run(() => ReplayPlanner.Find(ReplayProvenanceStore.Current.ReadCandidates(keys)), _lifetime.Token);
            if (_released) return;
            if (clusters.Length == 0)
            {
                ModalDialog.Show(Window.GetWindow(this), "No verified overlaps found. Only new recordings with validated packet provenance qualify.", "Find overlapping clips");
                return;
            }
            if (Window.GetWindow(this) is not { } owner) return;
            var clusterBox = new ComboBox { MaxWidth = 410 };
            var firstBox = new ComboBox { MaxWidth = 410 };
            var lastBox = new ComboBox { MaxWidth = 410 };
            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            var panel = new StackPanel();
            var timeline = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            var replace = new CheckBox { Content = "Replace originals (move to Recycle Bin)", Margin = new Thickness(0, 8, 0, 0) };
            var clustersLabels = clusters.Select((group, i) =>
                $"{i + 1}. {clips[LibraryFiles.PathKey(group.Clips[0].Media.Path)].Folder} — {group.Clips.Count} clips — {Fmt.Time(group.UnionUs / 1_000_000d)}").ToArray();
            Add("Overlapping group", clusterBox);
            Add("First clip", firstBox);
            Add("Last clip", lastBox);
            panel.Children.Add(preview);
            panel.Children.Add(timeline);
            panel.Children.Add(replace);
            ReplayCluster? selected = null;
            bool changing = false;
            replace.Checked += (_, _) => Update();
            replace.Unchecked += (_, _) => Update();
            clusterBox.SelectionChanged += (_, _) =>
            {
                if (clusterBox.SelectedIndex < 0) return;
                changing = true;
                var group = clusters[clusterBox.SelectedIndex];
                string[] labels = group.Clips.Select((p, i) => $"{i + 1}. {clips[LibraryFiles.PathKey(p.Media.Path)].Title} (+{Fmt.Time((p.FirstUs - group.FirstUs) / 1_000_000d)})").ToArray();
                firstBox.ItemsSource = labels; lastBox.ItemsSource = labels;
                firstBox.SelectedIndex = 0; lastBox.SelectedIndex = labels.Length - 1;
                changing = false;
                Update();
            };
            firstBox.SelectionChanged += (_, _) => Update();
            lastBox.SelectionChanged += (_, _) => Update();
            clusterBox.ItemsSource = clustersLabels;
            clusterBox.SelectedIndex = 0;
            while (ModalDialog.Run(owner, "Find overlapping clips", panel, "Combine clips", "Cancel", safeDefault: true))
            {
                if (selected == null)
                {
                    ModalDialog.Show(owner, "Choose a valid consecutive range of overlapping clips.", "Find overlapping clips");
                    continue;
                }
                if (_released || !PersonalLibrary.Enabled || _loading) return;
                var current = _clips.ToDictionary(c => LibraryFiles.PathKey(c.Path), StringComparer.OrdinalIgnoreCase);
                if (selected.Clips.Any(c => !current.ContainsKey(LibraryFiles.PathKey(c.Media.Path))))
                {
                    ModalDialog.Show(owner, "The Gallery changed. Choose the clips again.", "Find overlapping clips");
                    return;
                }
                var details = selected.Clips.Select(c => current[LibraryFiles.PathKey(c.Media.Path)].Details).ToArray();
                var first = selected.Clips[0].Media.Path;
                string destination = ClipNaming.AvailablePath(Path.GetDirectoryName(first)!, "Combined replay", ".mp4");
                bool replaceOriginals = replace.IsChecked == true;
                if (replaceOriginals && !ModalDialog.ConfirmAction(owner,
                    $"Create the combined recording, then move {selected.Clips.Count} original clips to the Recycle Bin?\n\n" +
                    "The combined file inherits favorites, tags and bookmarks. Restore replay originals can recover the original files and their information while the Recycle Bin retains them. The combined file will be kept during recovery.",
                    "Replace overlapping clips", "Combine and recycle originals", "Keep originals", destructive: true))
                    return;
                var job = ReplayCombine.Submit(selected, destination, ReplayCombine.MaxDuration, details,
                    replacement: replaceOriginals ? ReplayReplacementService.Current : null);
                var result = await job.Completion;
                if (_released) return;
                if (result.State == MediaJobState.Failed)
                    ModalDialog.Show(owner, result.Message ?? "Could not combine the clips.", "Find overlapping clips");
                QueueReload(); // a safe combined copy may exist even if replacement rolled back
                return;
            }

            void Add(string label, ComboBox box)
            {
                panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
                AutomationProperties.SetName(box, label);
                panel.Children.Add(box);
            }
            void Update()
            {
                if (changing || clusterBox.SelectedIndex < 0) return;
                try
                {
                    selected = ReplayPlanner.Subset(clusters[clusterBox.SelectedIndex], firstBox.SelectedIndex,
                        lastBox.SelectedIndex, ReplayCombine.MaxDuration);
                    if (selected.Clips.Count > ReplayCombine.MaxSources)
                        throw new ExportException("Choose at most 32 clips at once.");
                    preview.Text = $"{selected.Clips.Count} consecutive clips\n" +
                        $"Original footage: {Fmt.Time(selected.IndividualUs / 1_000_000d)}\n" +
                        $"Combined footage: {Fmt.Time(selected.UnionUs / 1_000_000d)}\n" +
                        $"Repeated footage removed: {Fmt.Time(selected.DuplicateUs / 1_000_000d)}\n\n" +
                        $"Original file sizes: {Fmt.Size(selected.Clips.Sum(c => c.Media.Stamp.Bytes))}\n\n" +
                        (replace.IsChecked == true
                            ? "The combined file will replace the originals in Gallery. Recycled originals occupy disk space until Windows clears them."
                            : "Creates a new lossless combined file. All originals stay in the Gallery.");
                    timeline.Children.Clear();
                    foreach (var clip in selected.Clips)
                    {
                        double start = (clip.FirstUs - selected.FirstUs) / (double)selected.UnionUs;
                        double length = clip.DurationUs / (double)selected.UnionUs;
                        string name = clips[LibraryFiles.PathKey(clip.Media.Path)].Title;
                        timeline.Children.Add(new TextBlock { Text = $"{name}  [{Fmt.Time((clip.FirstUs - selected.FirstUs) / 1_000_000d)} – {Fmt.Time((clip.EndUs - selected.FirstUs) / 1_000_000d)}]", TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name });
                        var track = new Grid { Height = 7, Margin = new Thickness(0, 2, 0, 6) };
                        track.SetResourceReference(Panel.BackgroundProperty, "DividerBrush");
                        track.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(0, start), GridUnitType.Star) });
                        track.ColumnDefinitions.Add(new() { Width = new GridLength(length, GridUnitType.Star) });
                        track.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(0, 1 - start - length), GridUnitType.Star) });
                        var bar = new Border { CornerRadius = new CornerRadius(2) };
                        bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                        Grid.SetColumn(bar, 1);
                        track.Children.Add(bar);
                        timeline.Children.Add(track);
                    }
                }
                catch (ExportException ex) { selected = null; preview.Text = ex.Message; timeline.Children.Clear(); }
            }
        }
        catch (OperationCanceledException) when (_released) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Could not find or combine overlapping clips", ex);
            if (!_released) ModalDialog.Show(Window.GetWindow(this),
                ex is ExportException ? ex.Message : "Could not find or combine overlapping clips.", "Find overlapping clips");
        }
        finally { if (!_released) FindOverlapsButton.IsEnabled = true; }
    }

    private async void RestoreReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_released || Window.GetWindow(this) is not { } owner) return;
        RestoreReplayButton.IsEnabled = false;
        try
        {
            var records = await Task.Run(ReplayReplacementService.Current.List, _lifetime.Token);
            if (_released) return;
            if (records.Length == 0)
            {
                ModalDialog.Show(owner, "No replay original groups need restoration.", "Restore replay originals");
                return;
            }
            var choices = new ComboBox { ItemsSource = records.Select(r =>
                $"{Path.GetFileName(r.Output.Path)} ({r.Items.Count} originals) — {r.Id.ToString("N")[..8]}").ToArray(), SelectedIndex = 0 };
            AutomationProperties.SetName(choices, "Replay recovery group");
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Choose a replay combination. Recovery restores its original recordings without overwriting files, and keeps the combined file. Recycled originals must still be available.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(choices);
            if (!ModalDialog.Run(owner, "Restore replay originals", panel, "Restore originals", "Cancel", safeDefault: true) ||
                choices.SelectedIndex < 0 || _released) return;
            var id = records[choices.SelectedIndex].Id;
            using (BackgroundActivity.Begin("Restoring replay originals"))
                await Task.Run(() => ReplayReplacementService.Current.RestoreAsync(id, _lifetime.Token), _lifetime.Token);
            if (!_released)
            {
                _scanRoot = true;
                QueueReload();
                ModalDialog.Show(owner, "Original recordings restored. The combined copy was kept.", "Restore replay originals");
            }
        }
        catch (OperationCanceledException) when (_released) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Replay restoration incomplete", ex);
            if (!_released)
            {
                _scanRoot = true;
                QueueReload();
                ModalDialog.Show(owner, ex is ExportException ? ex.Message : "Some originals could not be restored. Recovery remains available if the files still exist.", "Restore replay originals");
            }
        }
        finally { if (!_released) RestoreReplayButton.IsEnabled = true; }
    }
}
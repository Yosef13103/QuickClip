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
            var clustersLabels = clusters.Select((group, i) =>
                $"{i + 1}. {clips[LibraryFiles.PathKey(group.Clips[0].Media.Path)].Folder} — {group.Clips.Count} clips — {Fmt.Time(group.UnionUs / 1_000_000d)}").ToArray();
            Add("Overlapping group", clusterBox);
            Add("First clip", firstBox);
            Add("Last clip", lastBox);
            panel.Children.Add(preview);
            ReplayCluster? selected = null;
            bool changing = false;
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
            while (ModalDialog.Run(owner, "Find overlapping clips", panel, "Create combined copy", "Cancel", safeDefault: true))
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
                var job = ReplayCombine.Submit(selected, destination, ReplayCombine.MaxDuration, details);
                var result = await job.Completion;
                if (_released) return;
                if (result.State == MediaJobState.Failed)
                    ModalDialog.Show(owner, result.Message ?? "Could not combine the clips.", "Find overlapping clips");
                if (result.State == MediaJobState.Completed)
                    QueueReload();
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
                        "Creates a new lossless combined file. All originals stay in the Gallery.";
                }
                catch (ExportException ex) { selected = null; preview.Text = ex.Message; }
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
}
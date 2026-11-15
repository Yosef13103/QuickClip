using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QuickClip.Views;

public partial class DiagnosticsView : UserControl
{
    private sealed record CategoryItem(DiagnosticCategory? Value, string Label);
    private DispatcherTimer? _refresh;
    private DiagnosticCursor _cursor;
    private DiagnosticSnapshot? _snapshot;
    private DiagnosticEntry[] _visible = [];
    private bool _synchronizing;
    private bool _filtersChanged;

    public DiagnosticsView()
    {
        InitializeComponent();
        Categories.DisplayMemberPath = nameof(CategoryItem.Label);
        Categories.ItemsSource = new[] { new CategoryItem(null, "All categories") }
            .Concat(Enum.GetValues<DiagnosticCategory>().Select(value => new CategoryItem(value, DiagnosticEntry.CategoryLabel(value))));
        Categories.SelectedIndex = 0;
    }

    private void Loaded_View(object sender, RoutedEventArgs e) => SyncView();

    private void Visibility_Changed(object sender, DependencyPropertyChangedEventArgs e) => SyncView();

    private void Unloaded_View(object sender, RoutedEventArgs e) => ResetView();

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _synchronizing) return;
        Log.SetEnabled(EnabledBox.IsChecked == true);
        SyncView();
    }

    private void SyncView()
    {
        if (Results == null) return;
        if (!IsLoaded || !IsVisible)
        {
            ResetView();
            return;
        }
        _synchronizing = true;
        EnabledBox.IsChecked = Log.Enabled;
        _synchronizing = false;
        if (!Log.Enabled)
        {
            ResetView();
            return;
        }
        if (_refresh != null) return;
        _refresh = new(TimeSpan.FromSeconds(1), DispatcherPriority.Background, Refresh, Dispatcher);
        _refresh.Start();
        Refresh(this, EventArgs.Empty);
    }

    private void ResetView()
    {
        _refresh?.Stop();
        _refresh = null;
        _cursor = default;
        _snapshot = null;
        _visible = [];
        _filtersChanged = false;
        Results.ItemsSource = null;
        CopyButton.IsEnabled = ExportButton.IsEnabled = false;
        Status.Text = Log.Enabled ? "" : "Diagnostic logging is off.";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => _filtersChanged = true;

    private void Refresh(object? sender, EventArgs e)
    {
        if (!Log.Enabled)
        {
            SyncView();
            return;
        }
        var batch = Log.Read(ref _cursor);
        if (batch == null && !_filtersChanged) return;
        _snapshot = batch ?? _snapshot;
        _filtersChanged = false;
        _visible = _snapshot?.Filter((Categories.SelectedItem as CategoryItem)?.Value, ErrorsOnly.IsChecked == true,
            Search.Text.Trim()).ToArray() ?? [];
        Results.ItemsSource = _visible.Select(entry => entry.ToLine()).ToArray();
        CopyButton.IsEnabled = ExportButton.IsEnabled = _visible.Length > 0;
        Status.Text = _snapshot is { Dropped: > 0 } snapshot
            ? $"{_visible.Length:N0} matching events · {snapshot.Dropped:N0} expired or skipped"
            : $"{_visible.Length:N0} matching events";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Refresh(this, EventArgs.Empty);
        if (_visible.Length == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _visible.Select(entry => entry.ToLine())));
            Status.Text = $"Copied {_visible.Length:N0} events.";
        }
        catch (ExternalException) { Status.Text = "The clipboard is busy. Try copying again."; }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        Refresh(this, EventArgs.Empty);
        if (_visible.Length == 0) return;
        DiagnosticEntry[] entries = _visible;
        long session = _cursor.Session;
        var dialog = new SaveFileDialog
        {
            Filter = "Text log (*.txt)|*.txt", DefaultExt = ".txt", FileName = "quickclip-diagnostics.txt",
            AddExtension = true, OverwritePrompt = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Refresh(this, EventArgs.Empty);
        if (!Log.Enabled || _cursor.Session != session) return;
        try
        {
            DiagnosticSnapshot.Export(dialog.FileName, entries);
            Status.Text = $"Exported {entries.Length:N0} events.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Could not write the log. Choose a new file in a writable folder.";
        }
    }
}

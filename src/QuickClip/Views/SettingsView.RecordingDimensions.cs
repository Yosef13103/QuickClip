using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using QuickClip.Recording;

namespace QuickClip.Views;

public partial class SettingsView
{
    private bool _updatingDimensionFields;
    private bool _widthEdited, _heightEdited;

    private void RefreshAreas(string value)
    {
        string[] aspects = [.. RecordingPresets.Aspects, "1:1"];
        if (!aspects.Contains(value) && RecordingPresets.ParseAspect(value).Width > 0)
            aspects = [.. aspects, value];
        var items = aspects.Select(aspect => new Choice(aspect == "" ? "Whole source / Original" : $"{aspect} center crop", aspect)).ToList();
        bool wasLoading = _loading;
        _loading = true;
        AreaCombo.ItemsSource = items;
        Select(AreaCombo, value);
        _loading = wasLoading;
    }

    private void RefreshResolutions(int value)
    {
        var area = SelectedArea;
        var items = new List<Choice> { new(area is { } n ? $"Native ({n.Width} × {n.Height})" : "Native", 0) };
        if (area is { } a)
        {
            foreach (int limit in RecordingDimensions.AvailableResolutions(a))
            {
                var (w, h) = RecordingPresets.OutputSize(a.Width, a.Height, limit);
                string name = a.Width < a.Height ? $"{limit} short side" : $"{limit}p";
                items.Add(new Choice($"{name} ({w} × {h})", limit));
            }
            if (value > 0 && value <= Math.Min(a.Width, a.Height) && items.All(choice => !Equals(choice.Value, value)))
            {
                var (w, h) = RecordingPresets.OutputSize(a.Width, a.Height, value);
                items.Add(new Choice($"Custom ({w} × {h})", value));
            }
        }
        bool wasLoading = _loading;
        _loading = true;
        ResolutionCombo.ItemsSource = items;
        Select(ResolutionCombo, value);
        _loading = wasLoading;
    }

    private void UpdateCropPreview()
    {
        bool automatic = SourceCombo.SelectedIndex == 0;
        PreviewHeading.Text = automatic ? "Monitor fallback preview" : "Selected monitor preview";
        if (SelectedMonitor is not { } monitor || SelectedArea is not { } area)
        {
            CropPreview.Visibility = Visibility.Collapsed;
            UseNativeButton.Visibility = Visibility.Collapsed;
            PreviewDimensions.Text = "No display is available for preview.";
            PreviewHint.Text = "";
            return;
        }

        int limit = AppSettings.Current.RecordResolution;
        var (width, height) = RecordingPresets.OutputSize(area.Width, area.Height, limit);
        CropPreview.Visibility = Visibility.Visible;
        CropPreview.SetArea(monitor.Width, monitor.Height, area);
        bool whole = area.Width == monitor.Width && area.Height == monitor.Height;
        string region = whole ? "whole source" : "center crop";
        PreviewDimensions.Text = $"Source {monitor.Width} × {monitor.Height}\nCapture area {area.Width} × {area.Height} ({region})\nOutput {width} × {height}";
        var notes = new List<string> { "Output width uses multiples of 8, and height uses even pixels." };
        if (whole && SelectedAspect.Length > 0)
            notes.Add("This monitor is within 1% of the selected aspect, so the recorder keeps the whole source.");
        bool usingFallbackSize = limit > Math.Min(area.Width, area.Height);
        UseNativeButton.Visibility = usingFallbackSize ? Visibility.Visible : Visibility.Collapsed;
        if (usingFallbackSize)
            notes.Add($"This area uses Native. The saved {limit}-pixel short-side limit can still apply to larger sources.");
        if (automatic)
            notes.Add("Game windows keep their own aspect. The output short-side limit still applies when a game window is captured.");
        PreviewHint.Text = string.Join(" ", notes);
    }

    private void ResetDimensionFields()
    {
        _updatingDimensionFields = true;
        CustomAspectBox.Text = SelectedAspect;
        CustomAspectHint.Text = "Enter a ratio such as 16:9 or 32:9.";
        ResetSizeFields();
    }

    private void ResetSizeFields()
    {
        _updatingDimensionFields = true;
        var (width, height) = SelectedArea is { } area
            ? RecordingPresets.OutputSize(area.Width, area.Height, AppSettings.Current.RecordResolution) : default;
        CustomWidthBox.Text = width > 0 ? width.ToString(CultureInfo.InvariantCulture) : "";
        CustomHeightBox.Text = height > 0 ? height.ToString(CultureInfo.InvariantCulture) : "";
        _widthEdited = _heightEdited = false;
        _updatingDimensionFields = false;
        UpdateCustomSize();
    }

    private bool TryCustomAspect(out string aspect)
    {
        if (!RecordingDimensions.TryParseAspect(CustomAspectBox.Text, out aspect))
        {
            CustomAspectHint.Text = "Enter two positive whole numbers separated by a colon, such as 32:9.";
            return false;
        }
        if (SelectedMonitor is not { } monitor)
        {
            CustomAspectHint.Text = "Select an available monitor first.";
            return false;
        }
        var area = RecordingPresets.Area(monitor.Width, monitor.Height, aspect);
        if (area.Width < RecordingDimensions.MinimumCustomDimension || area.Height < RecordingDimensions.MinimumCustomDimension)
        {
            CustomAspectHint.Text = $"This ratio leaves fewer than {RecordingDimensions.MinimumCustomDimension} pixels on one side. Choose a ratio closer to the monitor’s shape.";
            return false;
        }
        CustomAspectHint.Text = $"Capture area after alignment: {area.Width} × {area.Height}.";
        return true;
    }

    private void CustomAspect_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_updatingDimensionFields && IsInitialized) TryCustomAspect(out _);
    }

    private void ApplyAspect_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || !TryCustomAspect(out string aspect)) return;
        ApplyDimensions(aspect, AppSettings.Current.RecordResolution);
    }

    private void CustomDimension_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingDimensionFields || !IsInitialized) return;
        bool editWidth = sender == CustomWidthBox;
        if (editWidth) _widthEdited = true;
        else _heightEdited = true;
        if (LockOutputRatio.IsChecked == true && !(_widthEdited && _heightEdited) &&
            sender is TextBox field && RecordingDimensions.TryPixels(field.Text, out int value) && SelectedArea is { } area)
        {
            var (width, height) = RecordingDimensions.MatchDimension(area, value, editWidth);
            int paired = editWidth ? height : width;
            if (paired > 0)
            {
                _updatingDimensionFields = true;
                (editWidth ? CustomHeightBox : CustomWidthBox).Text = paired.ToString(CultureInfo.InvariantCulture);
                _updatingDimensionFields = false;
            }
        }
        UpdateCustomSize();
    }

    private (int Limit, bool Matches, string? CropAspect, string Message) CheckCustomSize()
    {
        if (SelectedMonitor is not { } monitor || SelectedArea is not { } area)
            return (0, false, null, "Select an available monitor first.");
        if (!RecordingDimensions.TryPixels(CustomWidthBox.Text, out int width) ||
            !RecordingDimensions.TryPixels(CustomHeightBox.Text, out int height))
            return (0, false, null, "Enter a positive whole number for both dimensions.");
        if (RecordingDimensions.SizeError(width, height, monitor.Width, monitor.Height) is { } error)
            return (0, false, null, error);
        int limit = Math.Min(width, height);
        if (RecordingDimensions.CanProduce(area, width, height))
            return (limit, true, null, $"Output {width} × {height} with the current capture area.");
        string aspect = RecordingDimensions.Aspect(width, height);
        var crop = RecordingPresets.Area(monitor.Width, monitor.Height, aspect);
        if (RecordingDimensions.CanProduce(crop, width, height))
            return (limit, false, aspect, $"Crop to {aspect} to use {width} × {height}. This changes the monitor capture area to {crop.Width} × {crop.Height}.");
        var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, limit);
        return (0, false, null, $"This size cannot be produced with the current crop and alignment rules. Try {w} × {h}, or reset the fields.");
    }

    private void UpdateCustomSize()
    {
        var result = CheckCustomSize();
        CustomSizeHint.Text = result.Message;
        ApplySizeButton.IsEnabled = result.Matches;
        CropToFitButton.Visibility = result.CropAspect != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplySize_Click(object sender, RoutedEventArgs e)
    {
        var result = CheckCustomSize();
        if (!_loading && result.Matches) ApplyDimensions(SelectedAspect, result.Limit);
    }

    private void CropToFit_Click(object sender, RoutedEventArgs e)
    {
        var result = CheckCustomSize();
        if (!_loading && result.CropAspect is { } aspect) ApplyDimensions(aspect, result.Limit);
    }

    private void ResetSize_Click(object sender, RoutedEventArgs e) => ResetSizeFields();

    private void UseNative_Click(object sender, RoutedEventArgs e)
    {
        if (!_loading) ApplyDimensions(SelectedAspect, 0);
    }

    private void ApplyDimensions(string aspect, int limit)
    {
        var settings = AppSettings.Current;
        bool changed = settings.RecordAspect != aspect || settings.RecordResolution != limit;
        settings.RecordAspect = aspect;
        settings.RecordResolution = limit;
        RefreshAreas(aspect);
        RefreshResolutions(limit);
        ResetDimensionFields();
        UpdateCropPreview();
        UpdateEstimates();
        if (!changed) return;
        settings.Save();
        ScheduleRecorderRestart();
    }

#if DEBUG
    internal string CheckCustomSizeForRender()
    {
        if (SelectedArea is not { Width: > 272, Height: > 272 } area)
            return "Custom size smoke skipped: no suitable monitor.";

        var settings = AppSettings.Current;
        int saved = settings.RecordResolution;
        int limit = Math.Min(area.Width, area.Height) - 16;
        var (width, height) = RecordingPresets.OutputSize(area.Width, area.Height, limit);
        var shortSide = area.Width <= area.Height ? CustomWidthBox : CustomHeightBox;
        var longSide = area.Width <= area.Height ? CustomHeightBox : CustomWidthBox;
        try
        {
            ResetSizeFields();
            shortSide.Text = limit.ToString(CultureInfo.InvariantCulture);
            if (!CheckCustomSize().Matches)
                throw new InvalidOperationException("Ratio lock did not produce the aligned output.");
            longSide.Text = "7";
            if (shortSide.Text != limit.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Editing both dimensions changed the other field.");
            ApplySize_Click(this, new RoutedEventArgs());
            if (settings.RecordResolution != saved)
                throw new InvalidOperationException("An invalid size replaced the saved setting.");

            longSide.Text = Math.Max(width, height).ToString(CultureInfo.InvariantCulture);
            ApplySize_Click(this, new RoutedEventArgs());
            if (settings.RecordResolution != limit || !PreviewDimensions.Text.EndsWith($"Output {width} × {height}"))
                throw new InvalidOperationException("The valid custom size did not reach settings and the preview.");
            return $"Custom size smoke: {width} × {height}, ratio lock and invalid draft checked.";
        }
        finally
        {
            ApplyDimensions(SelectedAspect, saved);
        }
    }
#endif
}

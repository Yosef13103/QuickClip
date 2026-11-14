using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using QuickClip.Themes;

namespace QuickClip.Views;

public partial class CustomThemeEditor : UserControl
{
    private Dictionary<ThemeColor, Color> _draft = new(ThemePalette.Get(ThemeChoice.Dark));
    private readonly int[] _customSwatches = new int[16];
    private bool _updating;
    private bool _initialized;
    internal event Action? Saved;

    public CustomThemeEditor()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_initialized || !AppProfile.IsPersonal || Application.Current is not App) return;
            _initialized = true;
            ColorPicker.ItemsSource = ThemePalette.Colors;
            ColorPicker.SelectedIndex = 0;
            LoadDraft(ThemeResources.ReadSaved(AppSettings.Current.CustomThemeJson) ?? DefaultTheme());
        };
    }

    private static ThemeDocument DefaultTheme() => new("Custom", ThemePalette.Get(ThemeChoice.Dark));
    private ThemeDocument Draft => new(NameBox.Text, _draft);

    private void LoadDraft(ThemeDocument theme)
    {
        _updating = true;
        try
        {
            _draft = new(theme.Palette);
            NameBox.Text = theme.Name;
            HexBox.Text = ThemePalette.Hex(_draft[(ThemeColor)ColorPicker.SelectedItem]);
        }
        finally { _updating = false; }
        UpdatePreview();
    }

    private void Color_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updating || ColorPicker.SelectedItem is not ThemeColor color) return;
        HexBox.Text = ThemePalette.Hex(_draft[color]);
        UpdatePreview();
    }

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_updating) UpdatePreview();
    }

    private void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _updating || ColorPicker.SelectedItem is not ThemeColor color) return;
        if (ThemePalette.TryParseColor(HexBox.Text, out var value)) _draft[color] = value;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var color = (ThemeColor)ColorPicker.SelectedItem;
        Swatch.BorderBrush = ThemeResources.Frozen(_draft[color]);
        Swatch.BorderThickness = new Thickness(3);
        Preview.Background = ThemeResources.Frozen(_draft[ThemeColor.Surface]);
        PreviewPrimary.Foreground = ThemeResources.Frozen(_draft[ThemeColor.PrimaryText]);
        PreviewSecondary.Foreground = ThemeResources.Frozen(_draft[ThemeColor.SecondaryText]);
        var accent = _draft[ThemeColor.Accent];
        PreviewAccent.Background = ThemeResources.Frozen(accent);
        PreviewAccentText.Foreground = ThemePalette.Contrast(accent, Colors.Black) >= ThemePalette.Contrast(accent, Colors.White)
            ? Brushes.Black : Brushes.White;

        string? invalid = null;
        try { Draft.Validate(); }
        catch (InvalidDataException ex) { invalid = ex.Message; }
        if (!ThemePalette.TryParseColor(HexBox.Text, out _)) invalid = "Enter a color in #RRGGBB format.";
        string[] warnings = ThemePalette.ReadabilityWarnings(_draft).ToArray();
        Feedback.Text = invalid ?? (warnings.Length == 0 ? "" : string.Join(" ", warnings.Take(3)));
        SaveButton.IsEnabled = invalid == null && warnings.Length == 0;
        ExportButton.IsEnabled = invalid == null;
    }

    private void ResetColor_Click(object sender, RoutedEventArgs e) =>
        HexBox.Text = ThemePalette.Hex(ThemePalette.Get(ThemeChoice.Dark)[(ThemeColor)ColorPicker.SelectedItem]);

    private void Restore_Click(object sender, RoutedEventArgs e) => LoadDraft(DefaultTheme());

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window == null) return;
        var value = _draft[(ThemeColor)ColorPicker.SelectedItem];
        var pin = GCHandle.Alloc(_customSwatches, GCHandleType.Pinned);
        try
        {
            var picker = new ChooseColor
            {
                Size = Marshal.SizeOf<ChooseColor>(), Owner = new WindowInteropHelper(window).Handle,
                RgbResult = value.R | value.G << 8 | value.B << 16, CustomColors = pin.AddrOfPinnedObject(),
                Flags = 3, // CC_RGBINIT | CC_FULLOPEN
            };
            if (ChooseColorDialog(ref picker))
                HexBox.Text = ThemePalette.Hex(Color.FromRgb((byte)picker.RgbResult, (byte)(picker.RgbResult >> 8), (byte)(picker.RgbResult >> 16)));
        }
        finally { pin.Free(); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled || Application.Current is not App app) return;
        using var data = new MemoryStream();
        Draft.Write(data);
        var settings = AppSettings.Current;
        string? previous = settings.CustomThemeJson, previousChoice = settings.Theme;
        settings.CustomThemeJson = Encoding.UTF8.GetString(data.ToArray());
        settings.Theme = nameof(ThemeChoice.Custom);
        if (!settings.TrySave())
        {
            settings.CustomThemeJson = previous;
            settings.Theme = previousChoice;
            Feedback.Text = "QuickClip could not save the palette.";
            return;
        }
        app.Themes.Apply(app, settings);
        Saved?.Invoke();
        Feedback.Text = "Custom theme saved.";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "QuickClip theme (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            LoadDraft(ThemeDocument.Read(stream));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Feedback.Text = ex.Message;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "QuickClip theme (*.json)|*.json", DefaultExt = ".json", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!string.Equals(Path.GetExtension(dialog.FileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            Feedback.Text = "Choose a .json file for the theme.";
            return;
        }
        string? staged = null;
        try
        {
            staged = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, $".{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Draft.Write(stream);
            File.Move(staged, dialog.FileName, overwrite: true);
            staged = null;
            Feedback.Text = "Theme exported.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Feedback.Text = ex.Message;
        }
        finally
        {
            if (staged != null)
                try { File.Delete(staged); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColor
    {
        public int Size;
        public IntPtr Owner, Instance;
        public int RgbResult;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustomData, Hook, Template;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColorDialog(ref ChooseColor color);
}

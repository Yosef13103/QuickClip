using System.Windows;
using System.Windows.Controls;
using QuickClip.Discovery;

namespace QuickClip.Views;

public partial class SettingsView
{
    private bool _loadingGameProfiles;
    private void RefreshGameProfiles(string? selectedId = null)
    {
        if (GameProfilePanel == null) return;
        GameProfilePanel.Visibility = AppProfile.IsPersonal && !_guideActive && !IsBasicExperience
            ? Visibility.Visible : Visibility.Collapsed;
        if (GameProfilePanel.Visibility != Visibility.Visible) return;
        var loaded = GameProfileStore.ForCurrentProfile().Load();
        bool readable = loaded.Status is GameProfileLoadStatus.Loaded or GameProfileLoadStatus.Missing or GameProfileLoadStatus.Migrated;
        _loadingGameProfiles = true;
        try
        {
            var rows = RecordingProfileSelection.Build(GameCatalogRuntime.Rows(AppSettings.Current.Games), loaded.Document);
            GameProfileCombo.ItemsSource = rows;
            GameProfileCombo.SelectedItem = rows.FirstOrDefault(row => row.Enabled && selectedId != null && row.GameId == selectedId) ?? rows[0];
            GameProfileCombo.IsEnabled = readable;
            GameProfileStatus.Text = readable ? "" : "Profile file is invalid or unavailable. It will not be overwritten.";
            RefreshGameTagEditor();
        }
        finally { _loadingGameProfiles = false; }
    }

    private void GameProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingGameProfiles && !_loading && IsInitialized) RefreshGameTagEditor();
    }

    private void RefreshGameTagEditor()
    {
        if (GameProfileCombo.SelectedItem is RecordingProfileRow { Kind: RecordingProfileRowKind.Game, Enabled: true } row &&
            GameProfileCombo.IsEnabled)
        {
            GameTagsPanel.Visibility = Visibility.Visible;
            GameTagBox.Text = row.Policy?.Tags is { } tags ? string.Join(Environment.NewLine, tags) : "";
            GameProfileStatus.Text = "Automatic tags apply to new clips of this game. Video controls below still edit All Games in this preview.";
        }
        else
        {
            GameTagsPanel.Visibility = Visibility.Collapsed;
            if (GameProfileCombo.IsEnabled)
                GameProfileStatus.Text = "Global recording settings. Select a game to configure its automatic tags.";
        }
    }

    private void SaveGameTags_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingGameProfiles || _loading || !GameProfileCombo.IsEnabled ||
            GameProfileCombo.SelectedItem is not RecordingProfileRow { Kind: RecordingProfileRowKind.Game, Enabled: true } row) return;
        try
        {
            var tags = GameDefaultTags.Parse(GameTagBox.Text);
            var store = GameProfileStore.ForCurrentProfile();
            var loaded = store.Load();
            if (loaded.Status is not (GameProfileLoadStatus.Loaded or GameProfileLoadStatus.Missing or GameProfileLoadStatus.Migrated))
                throw new InvalidOperationException("Profile file is invalid or unavailable.");
            var fresh = RecordingProfileSelection.Build(GameCatalogRuntime.Rows(AppSettings.Current.Games), loaded.Document)
                .SingleOrDefault(choice => choice.GameId == row.GameId);
            if (fresh is not { Enabled: true } || fresh.ProfileKey != row.ProfileKey ||
                !(fresh.Policy?.Tags?.ToArray() ?? []).SequenceEqual(row.Policy?.Tags?.ToArray() ?? []))
                throw new InvalidOperationException("The game or tags changed. Refresh before saving.");
            var policy = (fresh.Policy ?? new GameRecordingPolicy()) with { Tags = tags.IsEmpty ? null : tags };
            store.Save(RecordingProfileSelection.Change(loaded.Document, fresh, policy));
            RefreshGameProfiles(row.GameId);
            GameProfileStatus.Text = "Automatic tags saved. Existing clips are unchanged. Video controls below still edit All Games.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            GameProfileStatus.Text = ex.Message;
        }
    }

    private void ResetGameTags_Click(object sender, RoutedEventArgs e)
    {
        GameTagBox.Clear();
        SaveGameTags_Click(sender, e);
    }
}

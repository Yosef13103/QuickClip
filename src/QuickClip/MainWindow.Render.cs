#if DEBUG
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickClip.Gallery;
using QuickClip.Recording;

namespace QuickClip;

// Debug-only: renders each page of this window to PNG files (no screen capture) for checking the layout.
public partial class MainWindow
{
    private async Task VerifyEditorDraftAsync(string dir, string? editorFile, string phase)
    {
        if (!AppProfile.IsPersonal || AppProfile.UsesDefaultDataRoot || Recorder.Instance.IsRunning ||
            editorFile == null || !Environment.GetCommandLineArgs().Any(arg => arg.StartsWith("--clips=", StringComparison.Ordinal)) ||
            !Path.GetFullPath(editorFile).Equals(Path.Combine(AppSettings.Current.ClipsFolder, "fixture-a.mp4"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Supply the generated fixture-a.mp4 in an explicitly isolated Personal profile and clips folder.");

        static async Task WaitForAsync(Func<bool> ready, string message)
        {
            for (int i = 0; i < 100 && !ready(); i++) await Task.Delay(100);
            if (!ready()) throw new InvalidOperationException(message);
        }

        Log.Event(DiagnosticCategory.Editor, "Draft smoke waiting for Gallery");
        await WaitForAsync(() => Gallery.DraftCardForRender(editorFile) != null, "Gallery did not load the fixture.");
        Log.Event(DiagnosticCategory.Editor, "Draft smoke opening Editor");
        if (phase == "presentation")
        {
            await WaitForAsync(() => Gallery.DraftCardForRender(editorFile)?.Thumbnail != null,
                "Gallery thumbnail did not load.");
            UpdateLayout();
            var item = Gallery.DraftCardForRender(editorFile);
            var card = FindCard(Gallery, item) ?? throw new InvalidOperationException("Gallery card was not realized.");
            var opening = OpenGalleryCardAsync(editorFile, card);
            if (!Gallery.IsVisible || GalleryNav.IsChecked != true || EditorNav.IsChecked == true)
                throw new InvalidOperationException("Gallery lost its rail selection before the preview was ready.");
            UpdateLayout();
            if (SystemParameters.ClientAreaAnimation &&
                System.Windows.Documents.AdornerLayer.GetAdornerLayer(Gallery)?.GetAdorners(Gallery)?.Length is not > 0)
                throw new InvalidOperationException("Gallery card transition was not attached.");
            SaveVisual(Path.Combine(dir, "gallery-opening.png"));
            await opening;
            if (!EditorPage.IsVisible || EditorNav.IsChecked != true || !EditorPage.PreviewReadyForRender)
                throw new InvalidOperationException("Editor did not wait for the loaded playlist entry.");
            EditorPage.ToggleFullscreenForRender();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!EditorPage.VerifyFullscreenLayoutForRender() || !EditorPage.SavePreviewForRender(Path.Combine(dir, "fullscreen-frame.png")))
                throw new InvalidOperationException("Fullscreen did not fill the Editor client or expose a decoded frame.");
            SaveVisual(Path.Combine(dir, "fullscreen-layout.png"));
            EditorPage.ExitFullscreen();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!EditorPage.VerifyRestoredLayoutForRender())
                throw new InvalidOperationException("Fullscreen controls or layout did not restore.");
            var player = PlayerPage;
            if (item == null || !await ShowPageAsync(player) || AppProfile.IsPersonal && PlayerNav.IsChecked != true)
                throw new InvalidOperationException("Could not open the standalone player.");
            player.Play([item], 0);
            SetFullScreen(true);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await WaitForAsync(() => player.TrySaveFrameForRender(Path.Combine(dir, "player-fullscreen-frame.png")),
                "The standalone player did not present a decoded frame.");
            if (!player.VerifyFullscreenLayoutForRender())
                throw new InvalidOperationException("The standalone player reserved space for fullscreen controls.");
            SetFullScreen(false);
            if (!player.VerifyRestoredLayoutForRender())
                throw new InvalidOperationException("The standalone player controls did not restore.");

            File.WriteAllText(Path.Combine(dir, "editor-draft-presentation.txt"), "Passed");
            return;
        }
        await OpenInEditorAsync(editorFile);
        var editor = EditorPage;
        await WaitForAsync(() => editor.PreviewReadyForRender, "Editor preview did not load.");
        Log.Event(DiagnosticCategory.Editor, "Draft smoke preview ready");
        if (phase == "write")
        {
            editor.EditDraftForRender();
            // Dispatch the same events as the three rail buttons, with a pending metadata debounce.
            EditorNav.IsChecked = false;
            GalleryNav.IsChecked = true;
            GalleryNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            SettingsNav.IsChecked = true;
            SettingsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            GalleryNav.IsChecked = true;
            GalleryNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitForAsync(() => Gallery.IsVisible, "The latest rail request did not win.");
            if (GalleryNav.IsChecked != true || SettingsNav.IsChecked == true ||
                Math.Abs((Gallery.DraftCardForRender(editorFile)?.SelectedDuration ?? 0) - 3.5) > 0.01)
                throw new InvalidOperationException("Leaving the editor did not save the trim to Gallery.");
            var card = Gallery.DraftCardForRender(editorFile)!;
            var target = ClipAnnotationTarget.For(card);
            var rename = await Library.PersonalLibrary.Current.SaveDraftAsync(target,
                card.Details with { Title = "Gallery rename fixture" }, CancellationToken.None);
            Gallery.ApplyClipDetails(editorFile, rename.Details ?? throw new InvalidOperationException("Gallery rename failed."));
            if (!await ShowPageAsync(editor) || editor.FileTitle != "Gallery rename fixture" ||
                Math.Abs(editor.TrimEndForRender - 4.5) > 0.01)
                throw new InvalidOperationException("Returning from Gallery did not update the title while retaining the editor draft.");
            editor.SetTitleForRender("Persisted fixture");
            if (!await ShowPageAsync(Gallery) || Gallery.DraftCardForRender(editorFile)?.Title != "Persisted fixture")
                throw new InvalidOperationException("Editor rename did not update Gallery.");
            UpdateLayout();
            await Gallery.VerifyHoverOriginalForRenderAsync(Gallery.DraftCardForRender(editorFile)!);
            string other = Path.Combine(AppSettings.Current.ClipsFolder, "fixture-b.mp4");
            var opening = OpenInEditorAsync(other);
            await WaitForAsync(() => editor.ProbingSourceForRender || opening.IsCompleted, "The next preview probe did not start.");
            if (editor.ProbingSourceForRender && (await ShowPageAsync(SettingsPage) || EditorNav.IsChecked != true))
                throw new InvalidOperationException("An in-progress source switch changed the selected page.");
            await Task.WhenAll(opening, OpenInEditorAsync(editorFile));
            await WaitForAsync(() => editor.CurrentFile == editorFile && editor.PreviewReadyForRender, "Rapid switching lost the last clip.");
            editor.VerifyDraftForRender();
        }
        else if (phase is "read" or "replace")
        {
            editor.VerifyDraftForRender();
            if (Gallery.DraftCardForRender(editorFile) is not { } card ||
                Math.Abs((card.SelectedDuration ?? 0) - 3.5) > 0.01 || card.Title != "Persisted fixture")
                throw new InvalidOperationException("Gallery trim or metadata did not survive the restart.");
            if (phase == "read")
            {
                string output = Path.Combine(dir, "export.mp4");
                Log.Event(DiagnosticCategory.Editor, "Draft smoke exporting fixture");
                await editor.ExportDraftForRender(output);
                Log.Event(DiagnosticCategory.Editor, "Draft smoke export settled");
                var media = await Media.MediaInfo.ProbeAsync(output);
                if (Math.Abs(media.Duration - 3.5) > 0.15 || media.Audio.Count != 1)
                    throw new InvalidOperationException("Export did not write the trimmed single-mix video.");
                editor.VerifyDraftForRender();
            }
            else
            {
                if (Environment.GetEnvironmentVariable("QUICKCLIP_TEST_RECYCLE_BIN") != "1")
                    throw new InvalidOperationException("The generated-media replacement test needs Recycle Bin opt-in.");
                await editor.SaveOverForRender(1);
                await WaitForAsync(() => editor.PreviewReadyForRender, "Replacement preview did not load.");
                if (!editor.CanUndo || Math.Abs(editor.TrimEndForRender - 3.5) > 0.15)
                    throw new InvalidOperationException("Replacement did not start with the edited video and clean trim.");
                await editor.UndoForRender();
                await WaitForAsync(() => editor.PreviewReadyForRender, "Restored preview did not load.");
                editor.VerifyDraftForRender();
            }
        }
        else if (phase == "close-pending")
        {
            var pending = new StalledAnnotationWrite();
            editor.AnnotationActions = pending;
            editor.SetTitleForRender("Recover this title");
            var stamp = ClipAnnotationTarget.ReadSource(editorFile) ?? throw new InvalidOperationException("Fixture identity is unavailable.");
            if (!await RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(15)) || !pending.Token.IsCancellationRequested)
                throw new InvalidOperationException("Close did not finish and cancel the stalled save.");
            if (await Task.Run(() => Library.EditorRecovery.Read(stamp)) is not
                { Details.Draft.Title: "Recover this title" })
                throw new InvalidOperationException("Close did not preserve the pending title for recovery.");
        }
        else throw new ArgumentException("Expected write, read, replace or close-pending.");
        File.WriteAllText(Path.Combine(dir, "editor-draft-" + phase + ".txt"), "Passed");
    }

    private static FrameworkElement? FindCard(DependencyObject root, ClipItem? item)
    {
        if (root is Border { Name: "Card" } card && ReferenceEquals(card.DataContext, item)) return card;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindCard(VisualTreeHelper.GetChild(root, i), item) is { } found) return found;
        return null;
    }

    private sealed class StalledAnnotationWrite : IClipAnnotationActions
    {
        internal CancellationToken Token { get; private set; }

        public Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token)
        {
            Token = token;
            return new TaskCompletionSource<ClipAnnotationResult>().Task;
        }

        public Task<IReadOnlyList<ClipAnnotationResult>> ApplyAsync(IReadOnlyList<ClipAnnotationTarget> targets,
            string? addTag, bool? favorite, CancellationToken token) => throw new NotSupportedException();
    }

    internal async Task RenderPagesAsync(string dir, string? editorFile)
    {
        Directory.CreateDirectory(dir);
        if (Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--editor-draft-smoke=", StringComparison.Ordinal))
            is { } draftSmoke)
        {
            await VerifyEditorDraftAsync(dir, editorFile, draftSmoke["--editor-draft-smoke=".Length..]);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--editor-lifecycle-smoke-only"))
        {
            if (string.IsNullOrWhiteSpace(editorFile))
                throw new ArgumentException("Supply a scratch video at least four seconds long.");
            await OpenInEditorAsync(editorFile);
            var editor = EditorPage;
            for (int i = 0; i < 100 && !editor.PreviewReadyForRender; i++) await Task.Delay(100);
            if (!editor.PreviewReadyForRender || editor.PreviewDurationForRender < 4)
                throw new InvalidOperationException("The scratch video did not load or is too short.");
            var player = editor.PreviewPlayerForRender;
            var hwnd = editor.PreviewHwndForRender;
            editor.SetTrimForRender(editor.PreviewDurationForRender - .5);
            editor.SeekForRender(1);
            await Task.Delay(500);
            double trim = editor.TrimEndForRender;
            string? originalTheme = AppSettings.Current.Theme;
            var results = new System.Text.StringBuilder();
            try
            {
                foreach (string theme in new[] { "Light", "Dark", "Slate", "Amoled", "Light" })
                {
                    ShowSettings();
                    AppSettings.Current.Theme = theme;
                    ((App)Application.Current).Themes.Apply(Application.Current, AppSettings.Current);
                    await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    // Exercise the documented repeat-Loaded path even when this
                    // specific Windows configuration does not invalidate the template.
                    editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, editor));
                    await Task.Delay(150);
                    ShowPage(editor);
                    await Task.Delay(500);
                    if (!ReferenceEquals(player, editor.PreviewPlayerForRender) ||
                        hwnd != editor.PreviewHwndForRender || !editor.PreviewReadyForRender ||
                        editor.TrimEndForRender != trim || Math.Abs(editor.PositionForRender - 1) > .25)
                        throw new InvalidOperationException("Theme switch changed preview ownership or editing state: " + theme);
                    if (!editor.SavePreviewForRender(Path.Combine(dir, $"preview-{theme}.png")))
                        throw new InvalidOperationException("mpv could not produce a preview frame: " + theme);
                    editor.PlayForRender();
                    await Task.Delay(450);
                    editor.PlayForRender();
                    await Task.Delay(150);
                    if (editor.PositionForRender <= 1.1)
                        throw new InvalidOperationException("Playback did not advance after theme switch: " + theme);
                    editor.SeekForRender(1);
                    await Task.Delay(450);
                    results.AppendLine(theme + ": original player, HWND, trim, seek, decoded frame and playback retained.");
                }
                File.WriteAllText(Path.Combine(dir, "editor-lifecycle.txt"), results.ToString());
            }
            finally
            {
                AppSettings.Current.Theme = originalTheme;
                ((App)Application.Current).Themes.Apply(Application.Current, AppSettings.Current);
            }
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--interface-scale-smoke-only"))
        {
            ShowSettings();
            _settings!.GeneralSection.IsChecked = true;
            var scaleReport = new System.Text.StringBuilder();
            foreach (var (width, scale) in new[] { (1180, 100), (820, 125) })
            {
                Width = width;
                Height = 680;
                _settings.CombinedScale.SelectedItem = scale;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveVisual(Path.Combine(dir, $"general-{width}-{scale}.png"));
                bool compact = _settings.SettingsSections.Tag is true;
                if (compact != (width == 820) || _settings.SectionTitle.FontSize != 24 * scale / 100d ||
                    SaveClipButton.MinHeight != 36 * scale / 100d || !_settings.GeneralSection.IsTabStop)
                    throw new InvalidOperationException("Scale resources or Settings rail state did not apply.");
                var label = (TextBlock)_settings.GeneralSection.Template.FindName("SectionLabel", _settings.GeneralSection);
                if ((label.Visibility == Visibility.Collapsed) != compact)
                    throw new InvalidOperationException("Compact Settings label visibility did not update.");
                scaleReport.AppendLine($"{width} DIP, scale {scale}%, compact {compact}, DPI {VisualTreeHelper.GetDpi(this).PixelsPerInchX}.");
            }
            File.WriteAllText(Path.Combine(dir, "interface-scale.txt"), scaleReport.ToString());
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--recording-settings-smoke-only"))
        {
            ShowSettings();
            _settings!.ShowRecordingSettingsForRender(audio: true);
            await Task.Delay(250);
            SaveVisual(Path.Combine(dir, "settings-audio.png"));
            _settings.ShowRecordingSettingsForRender(audio: false);
            await Task.Delay(250);
            SaveVisual(Path.Combine(dir, "settings-hotkeys.png"));
            if (Recorder.Instance.IsRunning) throw new InvalidOperationException("Recording must be inactive during the smoke test.");
            File.WriteAllText(Path.Combine(dir, "report.txt"), "Audio and Hotkeys rendered with recording inactive.");
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--library-smoke-only"))
        {
            await Task.Delay(4000);
            var clips = Gallery.SelectForRender(2);
            ShowSettings();
            await _settings!.ShowLibraryForRenderAsync();
            SaveVisual(Path.Combine(dir, "settings-library.png"));
            int folders = clips.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            File.WriteAllText(Path.Combine(dir, "report.txt"), $"Library settings rendered. Gallery selected {clips.Count} clips across {folders} folders. Recording active: {Recorder.Instance.IsRunning}.");
            return;
        }
        var report = new System.Text.StringBuilder();
        await Task.Delay(5000);
        SaveVisual(Path.Combine(dir, "gallery.png"));
        report.AppendLine($"gallery realized rows: {Gallery.RealizedRowsForRender}");
        report.AppendLine(await Gallery.VerifySearchForRenderAsync());
        if (Environment.GetCommandLineArgs().Contains("--gallery-smoke-only"))
        {
            Gallery.SelectForRender(2);
            await Task.Delay(300);
            SaveVisual(Path.Combine(dir, "gallery-selected.png"));
            File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
            return;
        }

        if (Gallery.OpenFirstForRender())
        {
            for (int i = 0; i < 60 && _editor?.CurrentFile == null; i++) await Task.Delay(100);
            if (_editor?.CurrentFile is not { } opened || EditorNav.IsChecked != true)
                throw new InvalidOperationException("Gallery did not open the unified editor.");
            double originalEnd = _editor.TrimEndForRender;
            _editor.SetTrimForRender(originalEnd * 0.75);
            double position = _editor.PositionForRender;
            double trim = _editor.TrimEndForRender;
            SaveVisual(Path.Combine(dir, "unified-editor.png"));
            _editor.ToggleModeForRender();
            if (!_editor.ViewingForRender || _editor.CurrentFile != opened ||
                _editor.PositionForRender != position || _editor.TrimEndForRender != trim)
                throw new InvalidOperationException("View mode lost clip, playhead or trim state.");
            SaveVisual(Path.Combine(dir, "unified-view.png"));
            _editor.ToggleFullscreenForRender();
            if (!_editor.FullscreenForRender || _editor.CurrentFile != opened ||
                _editor.PositionForRender != position || _editor.TrimEndForRender != trim)
                throw new InvalidOperationException("Fullscreen lost clip, playhead or trim state.");
            _editor.ToggleFullscreenForRender();
            if (!_editor.ViewingForRender) throw new InvalidOperationException("Fullscreen did not restore View mode.");
            _editor.ToggleModeForRender();
            _editor.SetTrimForRender(originalEnd);
            report.AppendLine("gallery opened unified editor and mode changes preserved clip, playhead and trim");
            report.AppendLine(await _editor.MeasureAccordionForRenderAsync());
            if (Environment.GetCommandLineArgs().Contains("--editor-smoke-only"))
            {
                _editor.ExpandedTracks = true;
                await Task.Delay(300);
                SaveVisual(Path.Combine(dir, "editor-taller-tracks.png"));
                File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
                return;
            }
            ShowPage(Gallery);
        }

        if (Gallery.PlayFirstForRender())
        {
            await Task.Delay(3000);
            SaveVisual(Path.Combine(dir, "player.png"));
            _player!.SaveVideoForRender(Path.Combine(dir, "player-video.png"));
            report.AppendLine($"player title={Title} {_player.DebugState}");
            // The full-screen layout, without taking over the screen.
            ApplyFullScreenLayout(true);
            await Task.Delay(1000);
            SaveVisual(Path.Combine(dir, "player-full.png"));
            await Task.Delay(3500);
            SaveVisual(Path.Combine(dir, "player-full-idle.png"));
            report.AppendLine($"player after 4.5 s full screen: controls={_player.ControlBar.Visibility} {_player.DebugState}");
            ApplyFullScreenLayout(false);
            ShowPage(Gallery);
        }

        // Projects: tick two clips, add them to a new project, add music, preview across the join, reorder, export.
        if (Gallery.SelectForRender(2) is { Count: 2 } picked)
        {
            await Task.Delay(500);
            SaveVisual(Path.Combine(dir, "gallery-selected.png"));
            string clips = AppSettings.Current.ClipsFolder;
            var project = ProjectLibrary.Create(clips, "Render test");
            await Gallery.AddSelectionForRender(project.Folder);
            var pane = Gallery.ProjectForRender;
            if (Directory.GetFiles(clips, "*.wav").FirstOrDefault() is { } song) await pane.SetMusicForRender(song);
            await Task.Delay(4000);
            SaveVisual(Path.Combine(dir, "project.png"));
            report.AppendLine($"project after adding {picked.Count}: {pane.DebugState}");
            pane.SeekForRender(pane.FirstClipDuration - 1.5);
            await Task.Delay(500);
            pane.PlayForRender();
            await Task.Delay(3500);
            report.AppendLine($"project 3.5 s after playing from 1.5 s before the join: {pane.DebugState}");
            pane.SaveVideoForRender(Path.Combine(dir, "project-video.png"));
            pane.MoveForRender(1, 0);
            report.AppendLine($"after moving the second clip first: {string.Join(", ", ProjectLibrary.Load(project.Folder).Clips.Select(Path.GetFileName))}");
            pane.ExportForRender();
            for (int i = 0; i < 1200 && pane.Exporting; i++) await Task.Delay(100);
            await Task.Delay(3000);
            SaveVisual(Path.Combine(dir, "montage-editor.png"));
            report.AppendLine($"after export: page={(EditorNav.IsChecked == true ? "editor" : "other")} title={Title}");
            if (_editor?.CurrentFile is { } montage)
            {
                var info = await Media.MediaInfo.ProbeAsync(montage);
                report.AppendLine($"montage {Path.GetFileName(montage)}: {info.Video?.Width}x{info.Video?.Height} {info.Duration:0.00}s " +
                                  $"tracks=[{string.Join(", ", info.Audio.Select(a => a.DisplayName))}]");
            }
            ShowPage(Gallery);
        }

        ShowSettings();
        await Updates.Updater.CheckAsync();
        await Task.Delay(3000);
        SaveVisual(Path.Combine(dir, "settings-1.png"));
        _settings!.SetGameWindowForRender(false);
        _settings.SelectAspectForRender("16:9");
        await Task.Delay(500);
        SaveVisual(Path.Combine(dir, "settings-16x9.png"));
        report.AppendLine(_settings.CheckCustomSizeForRender());
        _settings.SetGameWindowForRender(true);
        await Task.Delay(500);
        SaveVisual(Path.Combine(dir, "settings-game-window.png"));
        _settings.SetGameWindowForRender(false);
        _settings.AddMicForRender();
        await Task.Delay(500);
        SaveVisual(Path.Combine(dir, "settings-mics.png"));
        if (FindChild<ScrollViewer>(Pages.Children.OfType<Views.SettingsView>().First()) is { } scroller)
        {
            scroller.ScrollToEnd();
            await Task.Delay(500);
            SaveVisual(Path.Combine(dir, "settings-2.png"));
        }

        // What's new after an update from 0.4.2: the real notes of every release since, from GitHub.
        await ShowWhatsNewAsync(new Updates.AppVersion(0, 4, 2, null));
        await Task.Delay(800);
        SaveVisual(Path.Combine(dir, "whats-new.png"));
        report.AppendLine($"what's new: visible={WhatsNew.Visibility}");
        WhatsNew.Visibility = Visibility.Collapsed;

        if (editorFile != null)
        {
            await OpenInEditorAsync(editorFile);
            await Task.Delay(3000);
            SaveVisual(Path.Combine(dir, "editor.png"));
            report.AppendLine($"editor frame rates: {_editor?.FpsChoicesForRender}");
            _editor?.ExpandOptionsForRender();
            await Task.Delay(500);
            SaveVisual(Path.Combine(dir, "editor-options.png"));
            _editor?.SelectQualityForRender("high");
            await Task.Delay(500);
            SaveVisual(Path.Combine(dir, "editor-options-reencode.png"));
            _editor?.SelectQualityForRender("original");
            if (_editor != null)
            {
                // A format that can't hold the original's codec, then back: the video is copied again.
                report.AppendLine($"editor at first: {_editor.EncodingForRender}");
                _editor.SelectFormatForRender("webm");
                await Task.Delay(300);
                report.AppendLine($"editor as WebM: {_editor.EncodingForRender}");
                _editor.SelectFormatForRender("source");
                await Task.Delay(300);
                report.AppendLine($"editor back to the source format: {_editor.EncodingForRender}");
                _editor.SelectQualityForRender("small");
                _editor.SelectFormatForRender("mkv");
                await Task.Delay(300);
                SaveVisual(Path.Combine(dir, "editor-changed.png"));
                report.AppendLine($"editor changed: {_editor.EncodingForRender}");
                _editor.RevertForRender();
                await Task.Delay(300);
                report.AppendLine($"editor reverted: {_editor.EncodingForRender}");
            }

            // Save over the clip (trimmed), then undo that.
            if (_editor != null && Path.GetFileName(editorFile).StartsWith("undo-test", StringComparison.OrdinalIgnoreCase))
            {
                double before = (await Media.MediaInfo.ProbeAsync(editorFile)).Duration;
                await _editor.SaveOverForRender(2);
                await Task.Delay(1500);
                var edited = await Media.MediaInfo.ProbeAsync(editorFile);
                report.AppendLine($"undo: {before:0.00}s before, {edited.Duration:0.00}s after saving over; can undo={_editor.CanUndo}; status={_editor.StatusForRender}");
                SaveVisual(Path.Combine(dir, "editor-saved-over.png"));
                await _editor.UndoForRender();
                await Task.Delay(1500);
                var restored = await Media.MediaInfo.ProbeAsync(editorFile);
                report.AppendLine($"undo: {restored.Duration:0.00}s after undo; can undo={_editor.CanUndo}; status={_editor.StatusForRender}; " +
                                  $"leftovers=[{string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(editorFile)!).Select(Path.GetFileName))}]");
                SaveVisual(Path.Combine(dir, "editor-undone.png"));
            }

            // Add a sound next to the clip, play across it, then move it.
            if (_editor != null && Directory.GetFiles(Path.GetDirectoryName(editorFile)!, "*.wav").FirstOrDefault() is { } sound)
            {
                await _editor.AddSoundForRender(sound, 1.0);
                await Task.Delay(2500);
                report.AppendLine($"editor after adding a sound at 1 s: {_editor.DebugState}");
                SaveVisual(Path.Combine(dir, "editor-sound.png"));
                _editor.SeekForRender(0.5);
                await Task.Delay(300);
                _editor.PlayForRender();
                await Task.Delay(2000);
                report.AppendLine($"editor 2 s after playing from 0.5 s: {_editor.DebugState}");
                _editor.PlayForRender();
                _editor.MoveSoundForRender(2.5);
                await Task.Delay(2500);
                report.AppendLine($"editor after moving the sound to 2.5 s: {_editor.DebugState}");
                SaveVisual(Path.Combine(dir, "editor-sound-moved.png"));
            }
            double width = Width;
            Width = MinWidth;
            await Task.Delay(1000);
            SaveVisual(Path.Combine(dir, "editor-narrow.png"));
            Width = width;
        }

        report.AppendLine($"content {((FrameworkElement)Content).ActualWidth}x{((FrameworkElement)Content).ActualHeight}, drawn bounds {VisualTreeHelper.GetDescendantBounds((Visual)Content)}");
        foreach (UIElement page in Pages.Children)
            report.AppendLine($"  {page.GetType().Name} {page.Visibility} bounds {VisualTreeHelper.GetDescendantBounds(page)}");
        var r = Recorder.Instance;
        var stats = r.Stats();
        report.AppendLine($"recorder running={r.IsRunning} error={r.Error} summary={r.State.Summary} chat=[{string.Join(",", r.State.ChatApps)}] music=[{string.Join(",", r.State.MusicApps)}]");
        report.AppendLine($"stats {stats.Width}x{stats.Height} fps={stats.Fps} buffered={stats.BufferSeconds:0.0}s bytes={stats.BufferBytes} dropped={stats.DroppedFrames}");
        report.AppendLine($"hotkey active={App.Host?.HotkeyActive} status={RecStatus.Text}");
        File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
    }

    private void SaveVisual(string path) => SaveVisual(this, path);

    private static void SaveVisual(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var content = (FrameworkElement)window.Content;
        var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            // Map exactly the window's area, even if something hangs off its edge.
            var area = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area }, null, area);
        }
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
#endif

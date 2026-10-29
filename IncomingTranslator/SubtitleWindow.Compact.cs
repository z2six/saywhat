using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IncomingTranslator;

internal sealed partial class SubtitleWindow
{
    private readonly StackPanel _compactRows = new();
    private readonly TextBlock _compactStatus = new();
    private SubtitleTextSurface? _textSurface;
    private SubtitleActionSurface? _actionSurface;
    private readonly List<SubtitleActionAnchor> _compactActions = new();
    private bool _compactActionsDirty;
    private int _compactSelectionDepth;
    private readonly HashSet<long> _personPickerMessages = new();
    private Window? _transcriptWindow;
    private FrameworkElement _transcriptRoot = null!;
    private readonly Dictionary<long, TranscriptEntryView> _transcriptEntries = new();
    private Popup? _appearancePopup;
    private System.Windows.Threading.DispatcherTimer? _appearanceSaveTimer;
    private AppSettings? _appearanceBeforeEdit;
    private bool _followLatest = true;
    private bool _statusIsError;
    private bool _surfaceFailed;
    private readonly List<Action> _refreshCompactCopy = new();
    internal FrameworkElement TranscriptRoot => _transcriptRoot;
    internal FrameworkElement CompactPreview => _compactRows;
    internal int CompactBubbleCount => _compactRows.Children.OfType<Border>().Count();
    internal IReadOnlyList<SubtitleActionAnchor> CompactActions => _compactActions;

    private FrameworkElement CreateCompactToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _subtitleHeading.FontSize = 11; _subtitleHeading.FontWeight = FontWeights.SemiBold;
        _subtitleHeading.VerticalAlignment = VerticalAlignment.Center;
        var grip = new TextBlock { Text = "⠿", FontSize = 19, Foreground = OverlayMuted, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0), IsHitTestVisible = false };
        var drag = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 9, 0), Cursor = Cursors.SizeAll };
        drag.Children.Add(grip);
        if (HeaderMarkProvider?.Invoke(14) is Image mark)
        { mark.Name = "SubtitleBrandMark"; mark.Margin = new Thickness(0, 0, 5, 0); drag.Children.Add(mark); }
        drag.Children.Add(_subtitleHeading); row.Children.Add(drag);
        row.Children.Add(CompactButton("SubtitleAppearanceButton", "Appearance", (_, _) => ToggleAppearancePopup()));
        row.Children.Add(CompactButton("SubtitleTranscriptButton", "Logs", (_, _) => OpenTranscript()));
        _quickGuideButton.Width = 25; _quickGuideButton.Height = 26; _quickGuideButton.Margin = new Thickness(2, 0, 0, 0);
        _alwaysOnTopButton.Width = 27; _alwaysOnTopButton.Height = 26; _alwaysOnTopButton.Padding = new Thickness(3);
        _alwaysOnTopButton.Margin = new Thickness(2, 0, 0, 0);
        _alwaysOnTopButton.Content = "◇";
        row.Children.Add(_alwaysOnTopButton); row.Children.Add(_quickGuideButton);
        var close = CompactButton("SubtitleCloseButton", "Close", (_, _) => Close(), "×");
        close.Width = 25; close.FontSize = 17; close.ToolTip = Ui("Stop subtitles without stopping your voice translation"); row.Children.Add(close);
        var plate = new Border { Background = OverlayPanel, BorderBrush = OverlayStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 5, 7, 5), Child = row };
        return plate;
    }

    private Button CompactButton(string name, string caption, RoutedEventHandler click, string? glyph = null, bool trackLanguage = true)
    {
        var button = new Button { Name = name, Content = glyph ?? Ui(caption), Height = 26, Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = Ui(caption) };
        MarkLiteralUiProvider?.Invoke(button); AutomationProperties.SetName(button, Ui(caption));
        if (trackLanguage) _refreshCompactCopy.Add(() => { button.Content = glyph ?? Ui(caption); button.ToolTip = Ui(caption); AutomationProperties.SetName(button, Ui(caption)); });
        button.Click += click; return button;
    }

    private FrameworkElement CreateTranscriptContent()
    {
        var layout = new DockPanel();
        var top = new StackPanel();
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        actions.Children.Add(CompactButton("TranscriptCopyAllButton", "Copy all", (_, _) => CopyTranscriptText()));
        actions.Children.Add(CompactButton("TranscriptSettingsButton", "Settings", (_, _) => OpenSettings()));
        var follow = new CheckBox { Name = "TranscriptFollowLatest", IsChecked = true, Foreground = OverlayText,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        _refreshCompactCopy.Add(() => follow.Content = Ui("Follow latest")); follow.Content = Ui("Follow latest");
        follow.Checked += (_, _) => { _followLatest = true; _speakerScroll.ScrollToTop(); };
        follow.Unchecked += (_, _) => _followLatest = false; actions.Children.Add(follow);
        top.Children.Add(actions); top.Children.Add(_status); top.Children.Add(CreateSubtitleProfileBar());
        _source.Margin = new Thickness(0, 5, 0, 8);
        _quickGuide.MaxHeight = 240;
        top.Children.Add(_quickGuide); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var footer = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        footer.Children.Add(_outgoingState); footer.Children.Add(_playbackWarning); footer.Children.Add(_pinStatus);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer); layout.Children.Add(_speakerScroll);
        var frame = new Border { Background = OverlayBackground, Padding = new Thickness(16), Child = layout };
        frame.Resources.MergedDictionaries.Add(Resources);
        return frame;
    }

    private void OpenTranscript(bool maximize = false)
    {
        if (_shutdown.IsCancellationRequested || !IsLoaded) return;
        if (_transcriptWindow is null)
        {
            _transcriptWindow = new Window { Title = AppBrand.DisplayName + " · " + Ui("Transcript"), Icon = Icon,
                Width = 800, Height = 640, MinWidth = 420, MinHeight = 400, Content = _transcriptRoot,
                Background = OverlayBackground, Foreground = OverlayText, FontFamily = ConversationFont,
                Topmost = false, ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            _transcriptWindow.Resources.MergedDictionaries.Add(Resources);
            _transcriptWindow.SizeChanged += (_, _) => _quickGuide.MaxHeight = Math.Clamp(_transcriptWindow.ActualHeight * .45, 120, 320);
            _transcriptWindow.Activated += (_, _) => { _actionSurface?.Hide(); _textSurface?.Hide(); };
            _transcriptWindow.Deactivated += (_, _) => ShowPassiveSurface();
            _transcriptWindow.PreviewKeyDown += (_, args) =>
            { if (args.Key == Key.Escape && _quickGuide.Mode != SubtitleQuickGuideMode.Hidden) { _quickGuide.Dismiss(); args.Handled = true; } };
            _transcriptWindow.Closing += (_, args) => { if (!_shutdown.IsCancellationRequested) { args.Cancel = true; _transcriptWindow.Hide(); } };
        }
        _transcriptWindow.Show();
        if (_transcriptWindow.WindowState == WindowState.Minimized) _transcriptWindow.WindowState = WindowState.Normal;
        if (maximize) _transcriptWindow.WindowState = WindowState.Maximized;
        _transcriptWindow.Activate(); RefreshOutgoingState();
        if (_followLatest) _speakerScroll.ScrollToTop();
    }

    private void ToggleAppearancePopup()
    {
        if (!IsLoaded || _shutdown.IsCancellationRequested) return;
        if (_appearancePopup is { IsOpen: true }) { _appearancePopup.IsOpen = false; return; }
        _appearancePopup = new Popup { Name = "SubtitleAppearancePopup", PlacementTarget = _dragHeader,
            Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = CreateAppearanceControls() };
        _appearancePopup.Closed += (_, _) => FlushAppearanceSave();
        _appearancePopup.IsOpen = true;
    }

    internal FrameworkElement CreateAppearanceControls()
    {
        var body = new StackPanel { Width = 255 };
        void SliderControl(string label, string name, double value, double minimum, double maximum, double tick, Action<double> apply)
        {
            var caption = new TextBlock { Foreground = OverlayText, FontSize = 12, Margin = new Thickness(0, 4, 0, 2) };
            var slider = new Slider { Name = name, Minimum = minimum, Maximum = maximum, Value = value,
                TickFrequency = tick, IsSnapToTickEnabled = true, Foreground = AppearanceTheme.Accent,
                Margin = new Thickness(0, 3, 0, 8) };
            void RefreshCaption() => caption.Text = Ui(label) + " · " + Math.Round(slider.Value).ToString(System.Globalization.CultureInfo.CurrentCulture)
                + (label == "Background opacity" ? "%" : "");
            RefreshCaption(); AutomationProperties.SetName(slider, Ui(label));
            slider.ValueChanged += (_, _) => { RefreshCaption(); apply(slider.Value); QueueAppearanceSave(); };
            body.Children.Add(caption); body.Children.Add(slider);
        }
        SliderControl("Visible messages", "SubtitleCountSlider", _settings.SubtitlesVisibleMessages, 1, 10, 1, v => _settings.SubtitlesVisibleMessages = (int)v);
        SliderControl("Font size", "SubtitleFontSlider", _settings.SubtitlesFontSize, 12, 30, 1, v => _settings.SubtitlesFontSize = v);
        SliderControl("Bubble width", "SubtitleWidthSlider", _settings.SubtitlesBubbleWidth, 240, 900, 20, v => _settings.SubtitlesBubbleWidth = v);
        SliderControl("Background opacity", "SubtitleOpacitySlider", _settings.SubtitlesBackgroundOpacity * 100, 0, 100, 5, v => _settings.SubtitlesBackgroundOpacity = v / 100);
        var original = new CheckBox { Name = "SubtitleOriginalTextToggle", Content = Ui("Show original text"), Foreground = OverlayText,
            IsChecked = _settings.SubtitlesShowOriginalText, Margin = new Thickness(0, 3, 0, 8) };
        original.Click += (_, _) => { _settings.SubtitlesShowOriginalText = original.IsChecked == true; QueueAppearanceSave(); };
        body.Children.Add(original);
        // Existing recognition profiles remain one click away, without filling the overlay.
        var modes = new WrapPanel();
        foreach (string id in new[] { TranslationProfiles.NormalId, TranslationProfiles.GroupsId })
        {
            string chosen = id;
            modes.Children.Add(CompactButton("SubtitleQuickMode" + id.Replace("-", ""), id == TranslationProfiles.GroupsId ? "Groups" : "Normal",
                async (_, _) => { if (_appearancePopup is not null) _appearancePopup.IsOpen = false; await RequestSubtitleProfileAsync(chosen); }, trackLanguage: false));
        }
        body.Children.Add(modes);
        var popupPlate = new Border { Background = OverlayPanel, BorderBrush = OverlayStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(13), Child = body };
        popupPlate.Resources.MergedDictionaries.Add(Resources);
        return popupPlate;
    }

    private void QueueAppearanceSave()
    {
        _appearanceBeforeEdit ??= SettingsStore.Load();
        SettingsStore.NormalizeSubtitleAppearance(_settings); RefreshCompactSurface(); RefreshTranscriptTypography();
        _appearanceSaveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _appearanceSaveTimer.Tick -= SaveAppearanceTick; _appearanceSaveTimer.Tick += SaveAppearanceTick;
        _appearanceSaveTimer.Stop(); _appearanceSaveTimer.Start();
    }
    private void SaveAppearanceTick(object? sender, EventArgs args) => FlushAppearanceSave();
    private void FlushAppearanceSave()
    {
        _appearanceSaveTimer?.Stop(); if (_appearanceBeforeEdit is null) return;
        AppSettings previous = _appearanceBeforeEdit; _appearanceBeforeEdit = null;
        try { SettingsStore.SaveSubtitleAppearancePreference(_settings); }
        catch (Exception error)
        {
            LogOverlayError("Save subtitle appearance", error);
            _settings.SubtitlesVisibleMessages = previous.SubtitlesVisibleMessages;
            _settings.SubtitlesFontSize = previous.SubtitlesFontSize; _settings.SubtitlesBubbleWidth = previous.SubtitlesBubbleWidth;
            _settings.SubtitlesBackgroundOpacity = previous.SubtitlesBackgroundOpacity; _settings.SubtitlesShowOriginalText = previous.SubtitlesShowOriginalText;
            RefreshCompactSurface(); RefreshTranscriptTypography();
            _pinStatus.Text = Ui("Couldn't save your settings. Please try again."); _pinStatus.Visibility = Visibility.Visible;
        }
    }

    private void ShowPassiveSurface()
    {
        if (_surfaceFailed || _shutdown.IsCancellationRequested) return;
        if (!IsLoaded || !IsVisible || WindowState == WindowState.Minimized || _transcriptWindow?.IsActive == true)
        { _actionSurface?.Hide(); _textSurface?.Hide(); return; }
        try
        {
            _textSurface ??= new SubtitleTextSurface(this, _compactRows);
            _textSurface.Topmost = Topmost;
            _textSurface.PlaceRelativeToOwner(_settings.SubtitlesBubbleWidth);
            if (!_textSurface.IsVisible) _textSurface.Show();
            _textSurface.PlaceRelativeToOwner(_settings.SubtitlesBubbleWidth);
            _actionSurface ??= new SubtitleActionSurface(this, _textSurface);
            _actionSurface.Topmost = Topmost;
            if (_compactActionsDirty) { _actionSurface.SetActions(_compactActions); _compactActionsDirty = false; }
            _actionSurface.SyncToTextSurface();
            if (_actionSurface.HasVisibleActions && !_actionSurface.IsVisible) _actionSurface.Show();
            else if (!_actionSurface.HasVisibleActions) _actionSurface.Hide();
            _actionSurface.SyncToTextSurface();
        }
        catch (Exception error)
        {
            _surfaceFailed = true; LogOverlayError("Open passive subtitle surface", error);
            try { _actionSurface?.Close(); } catch (Exception cleanup) { LogOverlayError("Close failed subtitle actions", cleanup); }
            _actionSurface = null;
            try { _textSurface?.Close(); } catch (Exception cleanup) { LogOverlayError("Close failed subtitle surface", cleanup); }
            _textSurface = null;
            _subtitleHeading.Foreground = AppearanceTheme.Error;
            _pinStatus.Text = Ui("Couldn't show the overlay. Open Logs to read subtitles, or restart subtitles.");
            _pinStatus.Visibility = Visibility.Visible; _dragHeader.ToolTip = _pinStatus.Text;
        }
    }

    private void RefreshCompactSurface()
    {
        // Keep the clicked message and its search anchor still until the person
        // picker closes. Capture, translation and retained history continue.
        if (_compactSelectionDepth > 0) return;
        _compactRows.Children.Clear();
        _compactActions.Clear(); _compactActionsDirty = true;
        IEnumerable<TranslationDisplay> recent = _displayHistory.OrderByDescending(item => item.At).ThenByDescending(item => item.UtteranceId);
        foreach (TranslationDisplay item in recent.Take(Math.Clamp(_settings.SubtitlesVisibleMessages, 1, 10)))
        {
            var panel = new StackPanel();
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 3), MinHeight = 26 };
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var identity = new DockPanel { Margin = new Thickness(0, 0, 5, 0) };
            if (item.PersonId is not null && _people.TryGetValue(item.PersonId, out SpeakerPerson? person))
            {
                var avatar = PersonAvatar.Create(person, 18, OverlayStroke, OverlayText); avatar.Margin = new Thickness(0, 0, 5, 0);
                DockPanel.SetDock(avatar, Dock.Left); identity.Children.Add(avatar);
            }
            identity.Children.Add(new TextBlock { Text = DisplayPersonLabel(item), Foreground = OverlayMuted, FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            heading.Children.Add(identity);
            var actions = new DockPanel { LastChildFill = false, Tag = item.UtteranceId };
            actions.Resources.MergedDictionaries.Add(Resources);
            AddCorrectionButton(actions, item); AddSuggestionReviewButtons(actions, item);
            if (actions.Children.Count > 0)
            {
                actions.Measure(new Size(Math.Max(1, _settings.SubtitlesBubbleWidth - 20), double.PositiveInfinity));
                var actionAnchor = new Border { Name = "CompactPersonActionsAnchor", Tag = item.UtteranceId,
                    Width = Math.Min(actions.DesiredSize.Width, Math.Max(1, _settings.SubtitlesBubbleWidth - 20)),
                    Height = Math.Max(26, actions.DesiredSize.Height) };
                Grid.SetColumn(actionAnchor, 1); heading.Children.Add(actionAnchor);
                _compactActions.Add(new SubtitleActionAnchor(actionAnchor, actions));
            }
            panel.Children.Add(heading);
            if (_settings.SubtitlesShowOriginalText && !string.IsNullOrWhiteSpace(item.Source))
                panel.Children.Add(new TextBlock { Name = "CompactOriginalText", Text = item.Source, FontFamily = ConversationFont,
                    FontSize = Math.Max(10, _settings.SubtitlesFontSize * .75), Foreground = OverlayMuted,
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = _settings.SubtitlesFontSize * 2.2, Margin = new Thickness(0, 0, 0, 3) });
            panel.Children.Add(new TextBlock { Name = "CompactTranslationText", Text = item.Translation, FontFamily = ConversationFont,
                Foreground = OverlayText, FontSize = _settings.SubtitlesFontSize, LineHeight = _settings.SubtitlesFontSize * 1.35,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = _settings.SubtitlesFontSize * 5.4 });
            var color = AppearanceTheme.Surface.Color; color.A = (byte)Math.Round(255 * _settings.SubtitlesBackgroundOpacity);
            _compactRows.Children.Add(new Border { Name = "CompactSubtitleBubble", Tag = item.UtteranceId, Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 0, 5),
                IsHitTestVisible = false, Child = panel });
        }
        if (_displayHistory.Count == 0 || _statusIsError)
        {
            _compactStatus.Text = _statusIsError ? Ui(_lastStatus) : Ui("No subtitles yet.");
            _compactStatus.Foreground = _statusIsError ? AppearanceTheme.Error : OverlayMuted;
            _compactStatus.FontSize = 11; _compactStatus.TextWrapping = TextWrapping.Wrap;
            _compactStatus.Margin = new Thickness(8, 5, 8, 5); _compactRows.Children.Add(_compactStatus);
        }
        ShowPassiveSurface();
    }

    private void RenderTranscript()
    {
        bool grouping = _settings.OverlayMode is not ("Sentence bubbles" or "Latest sentence");
        TranslationDisplay[] ordered = _displayHistory.OrderByDescending(item => item.At).ThenByDescending(item => item.UtteranceId).ToArray();
        foreach (long obsolete in _transcriptEntries.Keys.Except(ordered.Select(item => item.UtteranceId)).ToArray())
        { if (_transcriptEntries[obsolete].Root.Parent is Panel parent) parent.Children.Remove(_transcriptEntries[obsolete].Root); _transcriptEntries.Remove(obsolete); }
        foreach (TranslationDisplay item in ordered)
        {
            if (!_transcriptEntries.TryGetValue(item.UtteranceId, out TranscriptEntryView? row))
                _transcriptEntries[item.UtteranceId] = row = new TranscriptEntryView(this, item);
            row.Update(item);
        }
        // Reparent only when the requested order/group structure actually changes.
        string key = grouping ? "groups" : "sentences";
        if (!Equals(_speakerRows.Tag, key))
        {
            foreach (var row in _transcriptEntries.Values) if (row.Root.Parent is Panel parent) parent.Children.Remove(row.Root);
            _speakerRows.Children.Clear(); _speakerRows.Tag = key;
        }
        if (grouping)
        {
            int groupIndex = 0;
            foreach (var group in ordered.GroupBy(DisplayGroupKey))
            {
                var outer = _speakerRows.Children.OfType<StackPanel>().FirstOrDefault(p => Equals(p.Tag, group.Key));
                if (outer is null) outer = new StackPanel { Tag = group.Key };
                if (groupIndex >= _speakerRows.Children.Count || !ReferenceEquals(_speakerRows.Children[groupIndex], outer))
                { _speakerRows.Children.Remove(outer); _speakerRows.Children.Insert(groupIndex, outer); }
                groupIndex++;
                int entryIndex = 0;
                foreach (var item in group)
                {
                    Border entry = _transcriptEntries[item.UtteranceId].Root;
                    if (entryIndex >= outer.Children.Count || !ReferenceEquals(outer.Children[entryIndex], entry))
                    { if (entry.Parent is Panel old) old.Children.Remove(entry); outer.Children.Insert(entryIndex, entry); }
                    entryIndex++;
                }
            }
            foreach (var empty in _speakerRows.Children.OfType<StackPanel>().Where(p => p.Children.Count == 0).ToArray()) _speakerRows.Children.Remove(empty);
        }
        else
            for (int index = 0; index < ordered.Length; index++)
            {
                Border entry = _transcriptEntries[ordered[index].UtteranceId].Root;
                if (index >= _speakerRows.Children.Count || !ReferenceEquals(_speakerRows.Children[index], entry))
                { if (entry.Parent is Panel parent) parent.Children.Remove(entry); _speakerRows.Children.Insert(Math.Min(index, _speakerRows.Children.Count), entry); }
            }
        RefreshTranscriptTypography();
        if (_followLatest && !_speakerRows.IsKeyboardFocusWithin)
            _ = Dispatcher.InvokeAsync(() =>
            { if (_followLatest && !_speakerRows.IsKeyboardFocusWithin && !_shutdown.IsCancellationRequested) _speakerScroll.ScrollToTop(); },
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RefreshTranscriptTypography()
    {
        foreach (TranscriptEntryView row in _transcriptEntries.Values)
        {
            row.Translation.FontSize = _settings.SubtitlesFontSize;
            row.Source.FontSize = Math.Max(11, _settings.SubtitlesFontSize * .75);
            row.Source.Visibility = _settings.SubtitlesShowOriginalText ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private sealed class TranscriptEntryView
    {
        internal Border Root { get; }
        internal TextBox Source { get; }
        internal TextBox Translation { get; }
        private readonly StackPanel _panel = new();
        private readonly SubtitleWindow _owner;
        private TranslationDisplay? _last;
        private DockPanel? _header;
        private StackPanel? _feedback;
        private string? _feedbackKey;
        internal TranscriptEntryView(SubtitleWindow owner, TranslationDisplay item)
        {
            _owner = owner;
            Source = ReadOnlyText("TranscriptOriginalText", OverlayMuted); Translation = ReadOnlyText("TranscriptTranslationText", OverlayText);
            _panel.Children.Add(Source); _panel.Children.Add(Translation);
            Root = new Border { Background = OverlayPanel, BorderBrush = OverlayStroke, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 8, 11, 9), Margin = new Thickness(0, 0, 0, 7), Child = _panel };
        }
        internal void Update(TranslationDisplay item)
        {
            if (Source.Text != item.Source) Source.Text = item.Source;
            if (Translation.Text != item.Translation) Translation.Text = item.Translation;
            if (_owner._personPickerMessages.Contains(item.UtteranceId) && _header is not null)
            {
                // A pending picker owns this anchor. Keep it attached while new
                // transcript text arrives; refresh names when the choice finishes.
                _owner.RefreshPersonActionAvailability(_header, item.UtteranceId);
                return;
            }
            _owner._people.TryGetValue(item.PersonId ?? "", out SpeakerPerson? person);
            string? feedbackKey = _owner._speakerReviewMessages.GetValueOrDefault(item.UtteranceId) + "|" +
                (_owner._speakerCorrections.TryGetValue(item.UtteranceId, out var correction) ? FeedbackSummary(correction) : "") + "|" +
                _owner._speakerReviewBusy.Contains(item.UtteranceId) + "|" + (person?.AvatarFileName ?? person?.VoiceAvatarSeed) + "|" +
                (_owner.CorrectSpeakerRequested is not null);
            if (_last is null || _last.Value.Speaker != item.Speaker || _last.Value.PersonId != item.PersonId ||
                _last.Value.IsManuallyAttributed != item.IsManuallyAttributed || _last.Value.EvidenceId != item.EvidenceId ||
                _last.Value.SpeakerRecognitionEpoch != item.SpeakerRecognitionEpoch || _feedbackKey != feedbackKey)
            {
                if (_header is not null) _panel.Children.Remove(_header);
                _header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                _owner.AddCorrectionButton(_header, item); _owner.AddSuggestionReviewButtons(_header, item); _owner.AddPersonAvatar(_header, item);
                var copy = new Button { Content = Ui("Copy"), Tag = new SubtitleInterfaceLabel("Copy"), FontSize = 10,
                    Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
                copy.Click += (_, _) => _owner.CopyTranscriptText(item.UtteranceId); DockPanel.SetDock(copy, Dock.Right); _header.Children.Add(copy);
                _header.Children.Add(new TextBlock { Text = DisplayPersonLabel(item), Tag = InterfacePersonLabel(item), Foreground = OverlayMuted,
                    FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                _panel.Children.Insert(0, _header);
                if (_feedback is not null) _panel.Children.Remove(_feedback);
                _feedback = new StackPanel(); _owner.AddFeedbackSummary(_feedback, item); _panel.Children.Add(_feedback);
                _feedbackKey = feedbackKey;
            }
            _last = item;
            if (_header is not null) _owner.RefreshPersonActionAvailability(_header, item.UtteranceId);
        }
        private static TextBox ReadOnlyText(string name, Brush foreground) => new() { Name = name, IsReadOnly = true,
            IsReadOnlyCaretVisible = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = foreground, FontFamily = ConversationFont, Padding = new Thickness(0),
            Margin = new Thickness(0, 2, 0, 3), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    internal string TranscriptText(long? id = null) => string.Join(Environment.NewLine + Environment.NewLine,
        _displayHistory.Where(item => id is null || item.UtteranceId == id).OrderByDescending(item => item.At).ThenByDescending(item => item.UtteranceId).Select(item =>
            $"{item.At.ToLocalTime():HH:mm:ss} · {DisplayPersonLabel(item)}{Environment.NewLine}" +
            (_settings.SubtitlesShowOriginalText && !string.IsNullOrWhiteSpace(item.Source) ? item.Source + Environment.NewLine : "") + item.Translation));

    private void CopyTranscriptText(long? id = null)
    {
        try { Clipboard.SetText(TranscriptText(id)); _status.Text = Ui("Copied"); }
        catch (Exception error) { LogOverlayError("Copy transcript", error); _status.Text = Ui("Couldn't copy text. Try again."); }
    }
}

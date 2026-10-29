using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace IncomingTranslator;

// Inert, unshown WPF controls and pure placement mathematics only. The overlay
// never receives Loaded, creates a capture service, starts a model, or moves an
// actual monitor window. DesktopTests redirects all settings to its own fixture.
internal static class SubtitleWindowControlsTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Subtitle window controls: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string? redirectedRoot = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(!string.IsNullOrWhiteSpace(redirectedRoot) && AppPaths.IsPackagedApplication &&
            string.Equals(Path.GetFullPath(redirectedRoot), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase),
            "requires the isolated desktop-test data root before creating any overlay");
        string originalLanguage = UiText.Language;
        AppSettings originalAppearance = SettingsStore.Load();
        string[] watchedPaths = [AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "incoming-translator.log"), Path.Combine(AppPaths.Root, "incoming-speaker-feedback.json")];
        var filesBefore = watchedPaths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        SubtitleWindow? overlay = null;
        try
        {
            UiText.Language = "en";
            overlay = new SubtitleWindow();
            T Field<T>(string name) => (T)typeof(SubtitleWindow).GetField(name, flags)!.GetValue(overlay)!;
            Check(!overlay.IsLoaded && !overlay.IsVisible && overlay.Dispatcher.CheckAccess(),
                "overlay remains unshown on its creating STA");
            Check(Field<object?>("_service") is null && !Field<bool>("_serviceStarted") && !overlay.ServiceStopped.IsCompleted,
                "constructing controls did not start or stop any translation service");
            FrameworkElement content = (FrameworkElement)overlay.Content;
            FrameworkElement header = Descendants(content).OfType<FrameworkElement>().Single(item => item.Name == "SubtitleDragHeader");
            FrameworkElement transcript = overlay.TranscriptRoot;
            FrameworkElement preview = overlay.CompactPreview;
            Check(overlay.ResizeMode == ResizeMode.NoResize && overlay.WindowStyle == WindowStyle.None &&
                overlay.WindowState == WindowState.Normal && overlay.SizeToContent == SizeToContent.Height && overlay.Width == 420,
                "the compact toolbar has a bounded width, fits its control height and exposes no maximize or resizing chrome");
            Check(overlay.AllowsTransparency && overlay.Background == Brushes.Transparent,
                "the toolbar does not add a full opaque subtitle window behind its compact controls");
            Check(!ReferenceEquals(content, transcript) && !ReferenceEquals(content, preview) && !ReferenceEquals(transcript, preview)
                && LogicalTreeHelper.GetParent(transcript) is null && LogicalTreeHelper.GetParent(preview) is null,
                "transcript and passive subtitles are separate detached content trees before the toolbar is loaded");
            Check(!Descendants(content).Contains(Field<ScrollViewer>("_speakerScroll")) &&
                Descendants(transcript).Contains(Field<ScrollViewer>("_speakerScroll")),
                "conversation history and its scrollbar belong to the transcript rather than the toolbar");
            Check(!Descendants(content).OfType<TextBox>().Any() && !Descendants(content).OfType<ScrollViewer>().Any() &&
                !Descendants(preview).Any(item => item is ButtonBase or TextBoxBase or Selector or ScrollBar),
                "compact subtitles have no editable, selectable or scrolling controls");
            Button logs = Descendants(header).OfType<Button>().Single(button => button.Name == "SubtitleTranscriptButton");
            Button appearance = Descendants(header).OfType<Button>().Single(button => button.Name == "SubtitleAppearanceButton");
            Check(logs.IsEnabled && appearance.IsEnabled && !Descendants(header).OfType<Button>().Any(button => button.Name.Contains("Maximize", StringComparison.Ordinal)),
                "the toolbar exposes Logs and Appearance instead of a misleading maximize control");
            Check(SubtitleWindow.IsSubtitleDragSource(header, header), "blank title/header space may start a drag");
            Check(!SubtitleWindow.IsSubtitleDragSource(null, header), "an absent event source never starts dragging");
            foreach (Button button in Descendants(header).OfType<Button>())
            {
                Check(!SubtitleWindow.IsSubtitleDragSource(button, header), "header button does not start dragging: " + button.Content);
                foreach (DependencyObject child in Descendants(button).Skip(1))
                    Check(!SubtitleWindow.IsSubtitleDragSource(child, header), "nested button artwork/text does not start dragging");
            }
            TextBlock title = Field<TextBlock>("_subtitleHeading");
            Check(SubtitleWindow.IsSubtitleDragSource(title, header), "plain title text remains draggable");
            Check(!SubtitleWindow.IsSubtitleDragSource(Field<TextBlock>("_source"), header), "speech preview is not a title-bar drag source");
            Check(!SubtitleWindow.IsSubtitleDragSource(Field<ScrollViewer>("_speakerScroll"), header), "scrolling conversation history cannot drag the window");

            var syntheticHeader = new StackPanel();
            var buttonText = new TextBlock { Text = "Interactive child" };
            syntheticHeader.Children.Add(new Button { Content = buttonText });
            syntheticHeader.Children.Add(new TextBox()); syntheticHeader.Children.Add(new ComboBox());
            syntheticHeader.Children.Add(new ScrollBar());
            foreach (DependencyObject item in Descendants(syntheticHeader).Skip(1))
                Check(!SubtitleWindow.IsSubtitleDragSource(item, syntheticHeader), "interactive descendants never become synthetic drag handles");
            Check(!SubtitleWindow.IsSubtitleDragSource(new TextBlock(), header), "unrelated content outside the header never starts dragging");

            // Existing sentence/reinforcement data belongs to the transcript.
            // Locale, theme and toolbar navigation must preserve exact evidence.
            overlay.CorrectSpeakerRequested = (_, _, _, _) => Task.FromResult<SpeakerFeedbackResult?>(null);
            TranslationDisplay sentence = new("Synthetic speaker", "Synthetic source", "Synthetic translated sentence", 871,
                new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)) { EvidenceId = "unchanged-evidence-871" };
            overlay.Publish(sentence);
            overlay.ApplySpeakerCorrection(sentence.UtteranceId,
                new SpeakerFeedbackResult(false, "Synthetic label-only correction", "synthetic-person", "Synthetic corrected name", true), sentence.EvidenceId);
            TranslationDisplay? historyBefore = overlay.HistoryEntry(sentence.UtteranceId);
            DependencyObject[] bubbleObjects = Field<StackPanel>("_speakerRows").Children.Cast<DependencyObject>().ToArray();
            Check(historyBefore is { Speaker: "Synthetic corrected name", PersonId: "synthetic-person", EvidenceId: "unchanged-evidence-871" },
                "synthetic corrected sentence is present in the detached transcript");
            Check(overlay.CompactBubbleCount == 1 && Descendants(preview).OfType<Border>()
                .Where(item => item.Name == "CompactSubtitleBubble").All(item => !item.IsHitTestVisible),
                "the same translated sentence has a noninteractive compact preview");
            int optionRequests = 0;
            overlay.OpenOptionsRequested = () => optionRequests++;
            Button pin = Field<Button>("_alwaysOnTopButton");
            var settings = Field<AppSettings>("_settings");
            foreach (bool pinned in new[] { true, false })
            {
                settings.SubtitlesAlwaysOnTop = pinned; overlay.Topmost = pinned;
                foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
                {
                    UiText.Language = language; overlay.RefreshInterfaceLanguage();
                    string settingsBefore = JsonSerializer.Serialize(settings);
                    Check(pin.Content?.ToString() == (pinned ? "◆" : "◇") &&
                        AutomationProperties.GetName(pin) == UiText.Format("Always on top · {0}", UiText.T(pinned ? "On" : "Off")) &&
                        pin.ToolTip?.ToString() == UiText.T(pinned
                            ? "Keep subtitles in front of other windows. Click to turn off."
                            : "Click to keep subtitles in front of other windows."),
                        "the compact pin glyph has a full localized state and explanation: " + language);
                    Check(logs.Content?.ToString() == UiText.T("Logs") && AutomationProperties.GetName(logs) == UiText.T("Logs") &&
                        appearance.Content?.ToString() == UiText.T("Appearance") && AutomationProperties.GetName(appearance) == UiText.T("Appearance"),
                        "toolbar navigation remains localized and accessible: " + language);
                    logs.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    typeof(SubtitleWindow).GetMethod("OpenTranscript", flags)!.Invoke(overlay, [true]);
                    Check(overlay.WindowState == WindowState.Normal && Field<Window?>("_transcriptWindow") is null &&
                        Field<SubtitleTextSurface?>("_textSurface") is null && new WindowInteropHelper(overlay).Handle == 0,
                        "Logs and the maximize-transcript request stay inert before the toolbar is loaded");
                    Check(overlay.Topmost == pinned, "transcript navigation preserves the toolbar pin preference");
                    Check(JsonSerializer.Serialize(settings) == settingsBefore && ReferenceEquals(settings, Field<AppSettings>("_settings")),
                        "toolbar navigation never rewrites translation or recognition settings");
                    Check(Equals(overlay.HistoryEntry(sentence.UtteranceId), historyBefore) &&
                        Field<StackPanel>("_speakerRows").Children.Cast<DependencyObject>().SequenceEqual(bubbleObjects),
                        "toolbar navigation retains sentence history, manual labels, evidence and existing transcript rows");
                    Check(optionRequests == 0 && Field<object?>("_service") is null && !Field<bool>("_serviceStarted") &&
                        !overlay.IsLoaded && !overlay.IsVisible && !overlay.ServiceStopped.IsCompleted,
                        "toolbar navigation does not show settings, create capture, load a model, or change service lifetime");
                }
            }
            UiText.Language = "en";
            foreach (ThemePreset preset in AppearanceTheme.Presets)
            {
                var themed = originalAppearance.Copy(); themed.UiThemeId = preset.Id;
                AppearanceTheme.Apply(themed);
                foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
                {
                    UiText.Language = language; overlay.RefreshInterfaceLanguage();
                    Check(pin.IsEnabled && pin.Visibility == Visibility.Visible &&
                        ReferenceEquals(pin.Foreground, settings.SubtitlesAlwaysOnTop ? AppearanceTheme.Good : AppearanceTheme.Muted),
                        "the pin follows its shared state color in " + preset.Id + " / " + language);
                    content.Measure(new Size(1200, 100));
                    var viewport = new Size(overlay.Width, Math.Max(34, content.DesiredSize.Height));
                    content.Arrange(new Rect(new Point(), viewport)); content.UpdateLayout();
                    Check(viewport.Height < 100 && viewport.Width < 1200,
                        "the toolbar stays compact in " + preset.Id + " / " + language);
                    foreach (Button button in Descendants(header).OfType<Button>())
                    {
                        Point origin = button.TranslatePoint(new Point(), content);
                        Check(double.IsFinite(origin.X) && double.IsFinite(origin.Y) && button.ActualWidth >= 24 && button.ActualHeight >= 24 &&
                            origin.X >= 0 && origin.Y >= 0 && origin.X + button.ActualWidth <= viewport.Width + 0.5 &&
                            origin.Y + button.ActualHeight <= viewport.Height + 0.5,
                            $"each toolbar action remains reachable in {preset.Id} / {language}: {button.Name}, origin={origin}, size={button.RenderSize}, viewport={viewport}");
                    }
                }
            }
            Check(new WindowInteropHelper(overlay).Handle == 0 && !overlay.IsLoaded && !overlay.IsVisible,
                "theme and layout checks never created or activated a toolbar window");

            CheckPlacement(Check);
            foreach ((string path, byte[]? bytes) in filesBefore)
                Check(bytes is null ? !File.Exists(path) : File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes),
                    "window control tests never write settings, microphone configuration, recognition data or incoming logs: " + Path.GetFileName(path));
        }
        finally
        {
            overlay?.Close(); // Detach theme listeners. Never shown, so OnLoaded never ran.
            UiText.Language = originalLanguage;
            AppearanceTheme.Apply(originalAppearance); ThemeBackground.Apply(originalAppearance);
        }
        return checks;
    }

    private static void CheckPlacement(Action<bool, string> check)
    {
        static bool Finite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
        var normal = new Size(960, 430);
        check(SubtitleWindowPlacement.SafeRestoredSize(normal, new Size(800, 300)) == normal, "valid RestoreBounds dimensions are preserved exactly");
        check(SubtitleWindowPlacement.SafeRestoredSize(new Size(10, 20), normal) == new Size(560, 260), "too-small restoration respects the subtitle minimum size");
        check(SubtitleWindowPlacement.SafeRestoredSize(new Size(double.NaN, double.NaN), normal) == normal, "invalid RestoreBounds use the last safe fallback dimensions");
        check(SubtitleWindowPlacement.SafeRestoredSize(new Size(double.PositiveInfinity, double.PositiveInfinity), new Size(double.NaN, double.NaN)) == normal,
            "invalid requested and fallback sizes use safe 960 by 430 defaults");
        foreach (Rect workArea in new[] { new Rect(0, 0, 1920, 1040), new Rect(-2560, 0, 2560, 1400),
            new Rect(1920, -100, 2560, 1400), new Rect(-1920, -1200, 1920, 1160) })
        {
            var cursor = new Point(workArea.Left + workArea.Width / 2, workArea.Top + 32);
            Point centered = SubtitleWindowPlacement.RestoredDragPosition(cursor, normal, 0.5, 24, workArea);
            check(Finite(centered), "drag restoration remains finite for work area " + workArea);
            check(Math.Abs(centered.X - (cursor.X - 480)) < 0.01 && Math.Abs(centered.Y - (cursor.Y - 24)) < 0.01,
                "restoring preserves the grabbed title point under the pointer on " + workArea);
            if (workArea.Left < 0) check(centered.X < 0, "left/negative-origin monitor position is not clamped to the primary monitor");
            if (workArea.Top < -100) check(centered.Y < 0, "monitor above the primary retains its negative vertical origin");
            foreach (double ratio in new[] { -1d, 0d, 0.5, 1d, 2d, double.NaN, double.PositiveInfinity })
            foreach (double offset in new[] { -20d, 0d, 24d, 1000d, double.NaN, double.PositiveInfinity })
            {
                Point position = SubtitleWindowPlacement.RestoredDragPosition(cursor, normal, ratio, offset, workArea);
                check(Finite(position) && position.X + normal.Width > workArea.Left && position.X < workArea.Right &&
                    position.Y >= workArea.Top && position.Y < workArea.Bottom,
                    "extreme or invalid grab coordinates leave a reachable header on " + workArea);
            }
        }
        foreach (Rect workArea in new[] { Rect.Empty, new Rect(double.NaN, double.NaN, double.NaN, double.NaN),
            new Rect(0, 0, double.PositiveInfinity, double.PositiveInfinity) })
            check(Finite(SubtitleWindowPlacement.RestoredDragPosition(new Point(-300, -20), normal, 0.5, 20, workArea)),
                "invalid monitor bounds do not create a non-finite restored window position");
        check(Finite(SubtitleWindowPlacement.RestoredDragPosition(new Point(double.NaN, double.PositiveInfinity), normal,
            double.NaN, double.NaN, new Rect(-1920, 0, 1920, 1040))), "invalid cursor information fails to a safe finite position");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject descendant in Descendants(dependency)) yield return descendant;
    }
}

using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            var maximize = Field<Button>("_maximizeButton");
            Check(maximize.Name == "SubtitleMaximizeButton" && maximize.IsEnabled && maximize.Visibility == Visibility.Visible,
                "the independent maximize/restore control is discoverable and available");
            FrameworkElement content = (FrameworkElement)overlay.Content;
            FrameworkElement header = Descendants(content).OfType<FrameworkElement>().Single(item => item.Name == "SubtitleDragHeader");
            Check(SubtitleWindow.IsSubtitleDragSource(header, header), "blank title/header space may start a drag");
            Check(!SubtitleWindow.IsSubtitleDragSource(null, header), "an absent event source never starts dragging");
            Check(Descendants(header).Contains(maximize), "restore control is in the draggable header, not hidden in settings");
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

            // Keep existing sentence/reinforcement data and exact evidence intact as
            // the window is maximized and restored repeatedly. No host is attached.
            overlay.CorrectSpeakerRequested = (_, _, _, _) => Task.FromResult<SpeakerFeedbackResult?>(null);
            TranslationDisplay sentence = new("Synthetic speaker", "Synthetic source", "Synthetic translated sentence", 871,
                new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)) { EvidenceId = "unchanged-evidence-871" };
            overlay.Publish(sentence);
            overlay.ApplySpeakerCorrection(sentence.UtteranceId,
                new SpeakerFeedbackResult(false, "Synthetic label-only correction", "synthetic-person", "Synthetic corrected name", true), sentence.EvidenceId);
            TranslationDisplay? historyBefore = overlay.HistoryEntry(sentence.UtteranceId);
            DependencyObject[] bubbleObjects = Field<StackPanel>("_speakerRows").Children.Cast<DependencyObject>().ToArray();
            Check(historyBefore is { Speaker: "Synthetic corrected name", PersonId: "synthetic-person", EvidenceId: "unchanged-evidence-871" },
                "synthetic corrected sentence is present before changing window state");
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
                    object? pinText = pin.Content; object? pinTooltip = pin.ToolTip;
                    overlay.WindowState = WindowState.Normal; NotifyStateChanged(overlay);
                    string normalGlyph = Glyph(maximize);
                    Check(maximize.ToolTip?.ToString() == UiText.T("Maximize window") &&
                        AutomationProperties.GetName(maximize) == UiText.T("Maximize window"),
                        "normal-window hint and accessibility name are localized: " + language);
                    Check(normalGlyph.Length > 0, "normal-window control has a visible glyph");
                    maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(overlay.WindowState == WindowState.Maximized, "one click maximizes the unshown window");
                    Check(maximize.ToolTip?.ToString() == UiText.T("Restore window") &&
                        AutomationProperties.GetName(maximize) == UiText.T("Restore window") && Glyph(maximize) != normalGlyph,
                        "maximized-window control switches to the localized restore hint and different glyph: " + language);
                    maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(overlay.WindowState == WindowState.Normal && Glyph(maximize) == normalGlyph &&
                        maximize.ToolTip?.ToString() == UiText.T("Maximize window"), "second click restores and resets its visual state");
                    overlay.WindowState = WindowState.Maximized; NotifyStateChanged(overlay);
                    Check(maximize.ToolTip?.ToString() == UiText.T("Restore window"), "an OS/Snap state-change callback refreshes the restore affordance");
                    maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(overlay.WindowState == WindowState.Normal, "Snap-maximized window can be restored by the same control");
                    Check(overlay.Topmost == pinned && Equals(pin.Content, pinText) && Equals(pin.ToolTip, pinTooltip),
                        "changing window state preserves pin preference and the pin control");
                    Check(JsonSerializer.Serialize(settings) == settingsBefore && ReferenceEquals(settings, Field<AppSettings>("_settings")),
                        "window state changes never rewrite translation or recognition settings");
                    Check(Equals(overlay.HistoryEntry(sentence.UtteranceId), historyBefore) &&
                        Field<StackPanel>("_speakerRows").Children.Cast<DependencyObject>().SequenceEqual(bubbleObjects),
                        "window state changes retain sentence history, manual labels, evidence and existing bubble objects");
                    Check(optionRequests == 0 && Field<object?>("_service") is null && !Field<bool>("_serviceStarted") &&
                        !overlay.IsLoaded && !overlay.IsVisible && !overlay.ServiceStopped.IsCompleted,
                        "maximize/restore is local chrome only, with no settings window, capture, model, or service lifetime change");
                }
            }
            UiText.Language = "en";
            foreach (ThemePreset preset in AppearanceTheme.Presets)
            {
                var themed = originalAppearance.Copy(); themed.UiThemeId = preset.Id;
                AppearanceTheme.Apply(themed);
                foreach (WindowState state in new[] { WindowState.Normal, WindowState.Maximized })
                {
                    overlay.WindowState = state; NotifyStateChanged(overlay);
                    Check(maximize.IsEnabled && maximize.Visibility == Visibility.Visible && maximize.Content is not null,
                        "restore/maximize stays available in " + preset.Id + " / " + state);
                    Check(maximize.Content is System.Windows.Shapes.Path glyph && ReferenceEquals(glyph.Stroke, AppearanceTheme.Text),
                        "window-state glyph follows the shared text-color theme role in " + preset.Id + " / " + state);
                    foreach (Size viewport in new[] { new Size(560, 260), new Size(960, 430), new Size(1920, 1080) })
                    {
                        content.Measure(viewport); content.Arrange(new Rect(new Point(), viewport)); content.UpdateLayout();
                        Point origin = maximize.TranslatePoint(new Point(), content);
                        Check(double.IsFinite(origin.X) && double.IsFinite(origin.Y) && maximize.ActualWidth >= 24 && maximize.ActualHeight >= 24 &&
                            origin.X >= 0 && origin.Y >= 0 && origin.X + maximize.ActualWidth <= viewport.Width + 0.5 &&
                            origin.Y + maximize.ActualHeight <= viewport.Height + 0.5,
                            "window control remains fully reachable at " + viewport + " in " + preset.Id + " / " + state);
                    }
                }
            }

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

    // An unshown Window has no native WM_SIZE. Raise only its managed callback to
    // simulate Windows changing state after Snap; do not show/create a live HWND.
    private static void NotifyStateChanged(Window window) => typeof(Window)
        .GetMethod("OnStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);

    private static string Glyph(Button button) => button.Content is System.Windows.Shapes.Path path
        ? path.Data?.ToString() ?? "" : "";

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject descendant in Descendants(dependency)) yield return descendant;
    }
}

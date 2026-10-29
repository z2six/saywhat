using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

// Inert cards and unshown subtitle/transcript controls only. Never raises Loaded, reads voice
// evidence, opens Settings, starts capture/models, or writes real preferences.
internal static class SubtitleQuickGuideTests
{
    internal static int Run(string directory, MainWindow? dashboard = null)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Subtitle quick guide: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string? redirected = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        directory = Path.GetFullPath(directory);
        Check(!string.IsNullOrWhiteSpace(redirected) && AppPaths.IsPackagedApplication &&
            string.Equals(Path.GetFullPath(redirected), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Directory.GetParent(AppPaths.Root)?.FullName, directory, StringComparison.OrdinalIgnoreCase),
            "requires an isolated desktop-test fixture before creating overlay controls");
        AppSettings original = SettingsStore.Load();
        string languageBefore = UiText.Language;
        var watched = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "incoming-translator.log"), Path.Combine(AppPaths.Root, "speaker-feedback.json") }
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        SubtitleWindow? overlay = null;
        try
        {
            Check(!new AppSettings().SubtitleGuidePromptSeen && original.Copy().SubtitleGuidePromptSeen == original.SubtitleGuidePromptSeen,
                "fresh users are offered help and preference snapshots retain the stored answer");
            Check(SettingsStore.ReadSettings("{\"SubtitleGuidePromptSeen\":true,\"OwnVoiceTargetLanguage\":\"Chinese\"}") is
                { SubtitleGuidePromptSeen: true, OwnVoiceTargetLanguage: "Chinese" }, "the one-time answer survives serialization without changing languages");
            CheckPreferencePersistence(Path.Combine(directory, "subtitle-guide-preferences"), Check);
            if (dashboard is not null) CheckSettingsDestination(dashboard, Check);
            foreach (string phrase in SubtitleQuickGuide.Phrases)
            {
                Check(UiText.Contains(phrase), "all guide copy has a localization key: " + phrase);
                foreach (string language in new[] { "zh-CN", "ja", "ko" })
                {
                    UiText.Language = language;
                    Check(UiText.T(phrase) != phrase, "guide copy is translated in " + language + ": " + phrase);
                }
            }
            UiText.Language = "en";
            var writes = new List<bool>(); int settingsRequests = 0;
            var guide = new SubtitleQuickGuide(UiText.T, seen => writes.Add(seen), () => settingsRequests++);
            Button Button(SubtitleQuickGuide card, string name) => Nodes(card).OfType<Button>().Single(button => button.Name == name);
            Check(guide.Mode == SubtitleQuickGuideMode.Hidden && guide.Visibility == Visibility.Collapsed && writes.Count == 0,
                "construction only creates hidden controls");
            Check(!guide.ShowPromptIfNeeded(true) && guide.Mode == SubtitleQuickGuideMode.Hidden,
                "a saved answer skips the first-use question");
            Check(guide.ShowPromptIfNeeded(false) && guide.Mode == SubtitleQuickGuideMode.Prompt && writes.Count == 0,
                "offering first-use help does not write settings or wait on an external dialog");
            Button(guide, "SubtitleGuideSecondary").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(guide.Mode == SubtitleQuickGuideMode.Hidden && writes.SequenceEqual(new[] { true }) && !guide.ShowPromptIfNeeded(false),
                "Not now saves exactly one answer and does not repeat the question in the same session");
            guide.ShowGuide(); guide.CloseGuide(); guide.ShowGuide();
            Check(writes.Count == 1 && settingsRequests == 0 && guide.Mode == SubtitleQuickGuideMode.Guide,
                "reopening and closing the guide does not change translation settings");
            foreach (string phrase in new[]
            {
                "By default, the overlay shows the latest three messages. Its bubbles let clicks pass through; drag or use controls from the toolbar.",
                "Original text is what speech recognition heard; the translation appears below. Either step can make mistakes.",
                "Choose Logs for up to 200 recent messages in Transcript. Select text, use Copy all, or turn Follow latest on to keep up.",
                "Use Select a person and the ✓ / ✕ buttons in Transcript to correct or review names.",
                "Open Appearance on the toolbar to change Font size, Bubble width, Background and Original text. Changes save immediately.",
                "The background starts at 65% opacity; text stays solid. Always on top controls whether the overlay stays in front.",
                "Use Appearance or Transcript to switch Normal / Groups. Open Settings in Transcript; use ? for this guide.",
                "Normal uses your chosen speech model. Groups uses an extra local model to recognize separate voices; it adds delay and can still miss overlapping speech.",
                "Maybe names are suggestions. ✓ confirms one; ✕ marks it unknown. Guesses never teach themselves.",
                "Selecting a person can save a clear voice example. Mixed, short or old audio may only change the name. You can undo a review."
            })
                Check(Nodes(guide).OfType<TextBlock>().Any(text => text.Text == phrase),
                    "the guide explains compact display, full transcript and safe review: " + phrase);
            Check(!Nodes(guide).OfType<TextBlock>().Any(text => text.Text ==
                "Choose Select a person below a message, then pick or add their name. Use a clear example with only that person speaking. When recognition is on, this helps the app recognize them next time."),
                "the guide no longer directs users to click passive overlay bubbles");
            Button(guide, "SubtitleGuidePrimary").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(guide.Mode == SubtitleQuickGuideMode.Hidden && settingsRequests == 1 && writes.Count == 1,
                "speaker-settings link closes only the guide and invokes its dedicated settings callback");
            int answers = 0;
            var accepted = new SubtitleQuickGuide(UiText.T, _ => answers++, () => settingsRequests++);
            accepted.ShowPromptIfNeeded(false);
            Button(accepted, "SubtitleGuidePrimary").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(answers == 1 && accepted.Mode == SubtitleQuickGuideMode.Guide && settingsRequests == 1,
                "Show guide accepts the prompt without navigating or starting a service");
            accepted.ShowGuide(); accepted.Dismiss();
            Check(answers == 1 && accepted.Mode == SubtitleQuickGuideMode.Hidden, "guide dismissal never repeats the prompt write");
            int failedWrites = 0, reported = 0;
            var failed = new SubtitleQuickGuide(UiText.T, _ => { failedWrites++; throw new IOException("Synthetic write failure"); },
                () => settingsRequests++, _ => reported++);
            failed.ShowPromptIfNeeded(false); failed.ShowGuide();
            Check(failedWrites == 1 && reported == 1 && failed.Mode == SubtitleQuickGuideMode.Guide,
                "a failed preference write is reported without crashing or blocking the guide");
            failed.CloseGuide();
            Check(!failed.ShowPromptIfNeeded(false) && failedWrites == 1, "a save failure does not cause an endless prompt loop in this session");

            overlay = new SubtitleWindow();
            T Read<T>(string field) => (T)typeof(SubtitleWindow).GetField(field, flags)!.GetValue(overlay)!;
            var helpButton = Read<Button>("_quickGuideButton");
            var integrated = Read<SubtitleQuickGuide>("_quickGuide");
            FrameworkElement content = overlay.TranscriptRoot;
            var dragHeader = Read<Border>("_dragHeader");
            Check(helpButton.Content?.ToString() == "?" && helpButton.Width >= 24 && helpButton.IsEnabled &&
                !SubtitleWindow.IsSubtitleDragSource(helpButton, dragHeader), "literal question-mark button is independent of title-bar dragging");
            Check(Nodes(content).Contains(integrated) && !Nodes((DependencyObject)overlay.Content).Contains(integrated),
                "the guide belongs to the interactive transcript, not the passive subtitle surface or compact toolbar");
            TranslationDisplay sample = new("Speaker 1", "Synthetic source", "Synthetic translated sentence", 971, DateTimeOffset.UtcNow);
            overlay.Publish(sample);
            object? historyBefore = overlay.HistoryEntry(sample.UtteranceId);
            DependencyObject[] bubbles = Read<StackPanel>("_speakerRows").Children.Cast<DependencyObject>().ToArray();
            string settingsBefore = JsonSerializer.Serialize(Read<AppSettings>("_settings"));
            int optionsRequests = 0, speakerRequests = 0;
            overlay.OpenOptionsRequested = () => optionsRequests++;
            overlay.OpenSpeakerSettingsRequested = () => speakerRequests++;
            helpButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(integrated.Mode == SubtitleQuickGuideMode.Guide && !overlay.IsLoaded && !overlay.IsVisible,
                "toolbar help prepares its guide without showing windows or starting audio in an unshown fixture");
            Button(integrated, "SubtitleGuidePrimary").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(speakerRequests == 1 && optionsRequests == 0 && integrated.Mode == SubtitleQuickGuideMode.Hidden,
                "guide uses the real speaker-settings entry point rather than the language page");
            foreach (ThemePreset preset in AppearanceTheme.Presets)
            {
                var themed = original.Copy(); themed.UiThemeId = preset.Id;
                AppearanceTheme.Apply(themed);
                foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
                {
                    UiText.Language = language; overlay.RefreshInterfaceLanguage(); integrated.ShowGuide();
                    Check(helpButton.Content?.ToString() == "?" && helpButton.ToolTip?.ToString() == UiText.T("Quick subtitle guide") &&
                        AutomationProperties.GetName(helpButton) == UiText.T("Quick subtitle guide"), "help glyph/hint remains localized in " + preset.Id + "/" + language);
                    Check(ReferenceEquals(integrated.Background, AppearanceTheme.Surface) &&
                        Nodes(integrated).OfType<TextBlock>().All(text => ReferenceEquals(text.Foreground, AppearanceTheme.Text) || ReferenceEquals(text.Foreground, AppearanceTheme.Muted)),
                        "every guide text and surface uses shared theme roles in " + preset.Id + "/" + language);
                    Check(Nodes(integrated).OfType<TextBlock>().Any(text => text.Text == UiText.T("Saved names and voice examples stay on your device. They aren't uploaded.")) &&
                        Nodes(integrated).OfType<TextBlock>().Any(text => text.Text == UiText.T("You can turn Voice recognition on or off in Settings. Translations still work when it's off.")),
                        "local storage and optional recognition are explained without hiding the settings action");
                    foreach (Size viewport in new[] { new Size(560, 400), new Size(960, 640) })
                    {
                        integrated.MaxHeight = Math.Clamp(viewport.Height * .45, 120, 320);
                        content.InvalidateMeasure(); content.InvalidateArrange();
                        content.Measure(viewport); content.Arrange(new Rect(new Point(), viewport)); content.UpdateLayout();
                        foreach (Button button in new[] { Button(integrated, "SubtitleGuidePrimary"), Button(integrated, "SubtitleGuideSecondary") })
                        {
                            Point point = button.TranslatePoint(new Point(), content);
                            Check(double.IsFinite(point.X) && double.IsFinite(point.Y) && button.ActualWidth >= 24 && button.ActualHeight >= 24 &&
                                point.X >= 0 && point.Y >= 0 && point.X + button.ActualWidth <= viewport.Width + .5 && point.Y + button.ActualHeight <= viewport.Height + .5,
                                "guide action remains visible in " + preset.Id + "/" + language + "/" + viewport + "/" + button.Name);
                        }
                        var scroll = Nodes(integrated).OfType<ScrollViewer>().Single();
                        Check(scroll.ActualHeight > 10 && scroll.ExtentHeight > 0 && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                            "guide content can scroll while its actions stay fixed at " + viewport);
                    }
                }
            }
            UiText.Language = "en"; AppearanceTheme.Apply(original); overlay.RefreshInterfaceLanguage(); integrated.ShowGuide();
            foreach ((string name, Size viewport) in new[] { ("subtitle-quick-guide.png", new Size(960, 430)),
                ("subtitle-quick-guide-small.png", new Size(560, 260)) }) Render(content, viewport, Path.Combine(directory, name));
            integrated.Dismiss();
            Check(integrated.Mode == SubtitleQuickGuideMode.Hidden && !overlay.IsVisible && !overlay.IsLoaded,
                "dismissing the guide does not close or show the subtitle window");
            Check(JsonSerializer.Serialize(Read<AppSettings>("_settings")) == settingsBefore &&
                Equals(historyBefore, overlay.HistoryEntry(sample.UtteranceId)) &&
                Read<StackPanel>("_speakerRows").Children.Cast<DependencyObject>().SequenceEqual(bubbles),
                "help preserves settings, sentence history and existing chat bubble objects");
            Check(Read<object?>("_service") is null && !Read<bool>("_serviceStarted") && !overlay.ServiceStopped.IsCompleted,
                "all help actions are UI-only and leave audio/model lifetimes unchanged");
            foreach ((string path, byte[]? previous) in watched)
                Check(previous is null ? !File.Exists(path) : File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(previous),
                    "inert guide tests never write live preferences, configuration, voice data, or logs: " + Path.GetFileName(path));
        }
        finally
        {
            overlay?.Close(); UiText.Language = languageBefore;
            AppearanceTheme.Apply(original); ThemeBackground.Apply(original);
        }
        return checks;
    }

    private static void CheckPreferencePersistence(string fixture, Action<bool, string> check)
    {
        string? before = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        check(before is not null && Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Directory.GetParent(before)!.FullName) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "preference fixture stays within the isolated test output");
        try
        {
            Environment.SetEnvironmentVariable(AppBrand.DataEnvironmentVariable, fixture);
            Directory.CreateDirectory(fixture);
            var stale = new AppSettings { OwnVoiceTargetLanguage = "Chinese", IncomingTargetLanguage = "Korean",
                OwnManagedModelId = "saved-model", Glossary = "Synthetic private example", UiThemeId = "paper",
                SubtitlesAlwaysOnTop = false, UseAiSpeakerRecognition = false, SubtitleGuidePromptSeen = false };
            JsonObject document = JsonSerializer.SerializeToNode(stale)!.AsObject();
            document["FutureOption"] = new JsonObject { ["KeepMe"] = "Synthetic unknown preference" };
            File.WriteAllText(AppPaths.SettingsPath, document.ToJsonString());
            File.WriteAllText(AppPaths.FastConfigPath, "{}"); File.WriteAllText(AppPaths.FoxTransConfigPath, "{}");
            byte[] fastBefore = File.ReadAllBytes(AppPaths.FastConfigPath), sentenceBefore = File.ReadAllBytes(AppPaths.FoxTransConfigPath);
            JsonObject expected = (JsonObject)document.DeepClone(); expected[nameof(AppSettings.SubtitleGuidePromptSeen)] = true;
            SettingsStore.SaveSubtitleGuidePromptPreference(true);
            JsonNode? persisted = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath));
            check(JsonNode.DeepEquals(expected, persisted), "answer write changes only its boolean and preserves every other preference including unknown fields");
            check(File.ReadAllBytes(AppPaths.FastConfigPath).SequenceEqual(fastBefore) && File.ReadAllBytes(AppPaths.FoxTransConfigPath).SequenceEqual(sentenceBefore),
                "answer write does not rewrite microphone/translation configuration");
            SettingsStore.Save(stale);
            check(SettingsStore.Load().SubtitleGuidePromptSeen && stale.SubtitleGuidePromptSeen &&
                SettingsStore.Load() is { OwnVoiceTargetLanguage: "Chinese", IncomingTargetLanguage: "Korean", UseAiSpeakerRecognition: false },
                "a later stale dashboard/settings save keeps the answered prompt without enabling recognition or changing translation targets");
        }
        finally { Environment.SetEnvironmentVariable(AppBrand.DataEnvironmentVariable, before); }
    }

    private static void CheckSettingsDestination(MainWindow dashboard, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Read<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(dashboard)!;
        check(Read<bool>("_preview") && !dashboard.IsLoaded && !dashboard.IsVisible && dashboard.Dispatcher.CheckAccess(),
            "speaker-settings navigation only uses an inert preview dashboard");
        var settings = Read<AppSettings>("_settings");
        string themeBefore = settings.UiThemeId, pageBefore = Read<string>("_page");
        var runtime = Read<RuntimeController>("_runtime");
        var frame = (FrameworkElement)dashboard.Content;
        var content = Read<ContentControl>("_content");
        var requests = new List<DependencyObject>();
        RequestBringIntoViewEventHandler listener = (_, args) => requests.Add(args.TargetObject);
        frame.AddHandler(FrameworkElement.RequestBringIntoViewEvent, listener, handledEventsToo: true);
        try
        {
            // Expanded custom-theme options put recognition well below the fold.
            settings.UiThemeId = "custom";
            dashboard.Navigate("Home"); Layout(frame, new Size(900, 650));
            dashboard.NavigateToSpeakerSettings(); Layout(frame, new Size(900, 650)); Drain();
            FrameworkElement section = Read<FrameworkElement>("_speakerRecognitionSettingsSection");
            check(Read<string>("_page") == "Settings" && section.Name == "SpeakerRecognitionSettingsSection" &&
                requests.Any(target => ReferenceEquals(target, section)), "the guide link explicitly brings the actual recognition section into view");
            var scroll = Nodes(frame).OfType<ScrollViewer>().Single(view => ReferenceEquals(view.Content, content));
            Layout(frame, new Size(900, 650)); Drain();
            Point location = section.TranslatePoint(new Point(), scroll);
            check(scroll.VerticalOffset > 0 && scroll.ViewportHeight > 0 && location.Y >= -.5 &&
                location.Y + section.ActualHeight <= scroll.ViewportHeight + .5,
                "recognition controls are visible below expanded theme options without manual scrolling");
            check(Nodes(section).OfType<Button>().Any(button => button.Content?.ToString() == UiText.T(settings.UseAiSpeakerRecognition
                ? "Turn off recognition" : "Enable recognition")), "the scrolled destination contains the actual immediate on/off control");

            dashboard.NavigateToSpeakerSettings();
            dashboard.Navigate("Help"); requests.Clear(); Drain();
            check(Read<string>("_page") == "Help" && requests.Count == 0,
                "delayed reveal never hijacks navigation or scrolls a detached settings section");
            check(!runtime.OwnRunning && !runtime.IncomingRunning && !dashboard.IsVisible && !dashboard.IsLoaded,
                "navigation does not show a window, change recognition, or start any translation model/service");
        }
        finally
        {
            frame.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, listener);
            settings.UiThemeId = themeBefore; dashboard.Navigate(pageBefore); Drain();
        }
    }

    private static void Layout(FrameworkElement frame, Size viewport)
    {
        frame.InvalidateMeasure(); frame.InvalidateArrange();
        frame.Measure(viewport); frame.Arrange(new Rect(new Point(), viewport)); frame.UpdateLayout();
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Render(FrameworkElement content, Size viewport, string path)
    {
        content.InvalidateMeasure(); content.InvalidateArrange();
        content.Measure(viewport); content.Arrange(new Rect(new Point(), viewport)); content.UpdateLayout();
        var image = new RenderTargetBitmap((int)viewport.Width, (int)viewport.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject item in Nodes(dependency)) yield return item;
    }
}

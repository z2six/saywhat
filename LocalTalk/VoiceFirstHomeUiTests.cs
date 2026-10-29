using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

// Inert previews only: no microphone enumeration, network requests or model starts.
internal static class VoiceFirstHomeUiTests
{
    internal static int Run(MainWindow window, string? directory = null)
    {
        int checks = 0;
        void Check(bool value, string detail)
        { if (!value) throw new InvalidOperationException("Voice-first Home: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, flags)!;
        Check((bool)Field("_preview").GetValue(window)! && !window.IsVisible, "requires an unshown preview");
        var settings = (AppSettings)Field("_settings").GetValue(window)!;
        var runtime = (RuntimeController)Field("_runtime").GetValue(window)!;
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "test must not inherit running models");
        AppSettings before = settings.Copy();
        string previousLanguage = UiText.Language;
        string previousPage = (string)Field("_page").GetValue(window)!;
        bool previousBusy = (bool)Field("_busy").GetValue(window)!;
        bool previousClosing = (bool)Field("_closing").GetValue(window)!;
        AppSettings? previousApplied = ((AppSettings?)Field("_saveStateAppliedSettings").GetValue(window))?.Copy();
        try
        {
            Field("_busy").SetValue(window, false); Field("_closing").SetValue(window, false);
            settings.FirstRunSetupCompleted = true;
            settings.OwnRecognitionEngine = "nemotron";
            settings.OwnVoiceSourceLanguage = "English";
            settings.OwnManagedModelId = "hy-mt2-1.8b-q4_k_m";
            settings.UseManagedTranslator = true;
            settings.OwnVoiceTargetLanguage = "Chinese";
            settings.OwnVoiceAdditionalLanguagesEnabled = false;
            settings.OwnVoiceAdditionalTargetLanguages = [];
            settings.OwnMicrophoneDeviceId = "default";
            settings.OwnVoicePauseWhenVrchatMuted = true;
            Check(MainWindow.ToggleVoiceFirstTarget(settings, "ja") && MainWindow.ToggleVoiceFirstTarget(settings, "th") &&
                MainWindow.ToggleVoiceFirstTarget(settings, "English") &&
                settings.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "Chinese", "Japanese", "Thai", "English" }) &&
                settings.OwnVoiceSourceLanguage == "English",
                "four targets including Thai are canonical, distinct and retain click order with English speech");
            Check(!MainWindow.ToggleVoiceFirstTarget(settings, "French") && settings.EffectiveOwnVoiceTargetLanguages().Count == 4,
                "a fifth target cannot silently displace an existing choice");
            Check(MainWindow.ToggleVoiceFirstTarget(settings, "Chinese") && settings.OwnVoiceTargetLanguage == "Japanese" &&
                settings.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "Japanese", "Thai", "English" }),
                "removing the first target promotes the next target without reordering");
            MainWindow.ToggleVoiceFirstTarget(settings, "Thai"); MainWindow.ToggleVoiceFirstTarget(settings, "English");
            Check(!MainWindow.ToggleVoiceFirstTarget(settings, "Japanese") && !settings.OwnVoiceAdditionalLanguagesEnabled,
                "one remaining target is kept, and extra-output flag tracks actual choices");
            settings.OwnManagedModelId = "lfm2-350m-enjp-mt-q4_k_m";
            Check(!MainWindow.ToggleVoiceFirstTarget(settings, "Chinese") && settings.OwnVoiceTargetLanguage == "Japanese",
                "direct target callbacks cannot bypass Liquid's English/Japanese restriction");
            settings.OwnManagedModelId = "hy-mt2-1.8b-q4_k_m";
            settings.OwnVoiceTargetLanguage = "Chinese";
            settings.OwnVoiceAdditionalTargetLanguages = ["Japanese", "Thai"];
            settings.OwnVoiceAdditionalLanguagesEnabled = true;
            Field("_saveStateAppliedSettings").SetValue(window, settings.Copy());
            if (directory is not null) RenderPreviews(window, settings, directory);
            foreach (string locale in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = locale;
                window.Navigate("Home");
                var nodes = Nodes(window).ToArray();
                var primary = nodes.OfType<Button>().Single(button => button.Name == "OwnVoiceStartButton");
                Check(primary.Content?.ToString() == VoiceFirstText.T("Start translating my voice") &&
                    primary.Background == ModernTheme.Accent, "one primary own-voice start, localized: " + locale);
                Check(!nodes.OfType<Button>().Any(button => button.Content?.ToString() == UiText.T("Start both")),
                    "Home never defaults to starting both pipelines: " + locale);
                Check(nodes.OfType<Button>().Count(button => button.Name is "HomeNemotronRecognitionButton" or "HomeVoxtralRecognitionButton") == 2 &&
                    nodes.OfType<Button>().Count(button => button.Name is "HomeHyFastModelButton" or "HomeHyLargerModelButton" or
                        "HomeTranslateGemmaModelButton" or "HomeLiquidModelButton") == 4,
                    "recognizers and translators are visible explanatory choices, not dropdown-only: " + locale);
                Check(nodes.OfType<WrapPanel>().Single(panel => panel.Name == "HomeSpokenLanguageChoices").Children.Count == 10 &&
                    nodes.OfType<WrapPanel>().Single(panel => panel.Name == "HomeTargetLanguageChoices").Children.Count == 10,
                    "spoken and target language choices are available on Home: " + locale);
                Button thaiTarget = nodes.OfType<WrapPanel>().Single(panel => panel.Name == "HomeTargetLanguageChoices")
                    .Children.OfType<Button>().Single(button => (string?)button.Tag == "Thai");
                Check(thaiTarget.Content?.ToString() == "3 · " + UiText.T("Thai") && thaiTarget.IsEnabled &&
                    AutomationProperties.GetName(thaiTarget) == UiText.T("Translate my voice to") + ": " + UiText.T("Thai") &&
                    !nodes.OfType<WrapPanel>().Single(panel => panel.Name == "HomeSpokenLanguageChoices")
                        .Children.OfType<Button>().Any(button => (string?)button.Tag == "Thai"),
                    "Thai is a localized selected output with send order, outside the supported spoken choices: " + locale);
                Check(nodes.OfType<Button>().Where(button => button.Name is "HomeNemotronRecognitionButton" or "HomeHyFastModelButton" or "HomeLiquidModelButton")
                    .All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) &&
                        !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(button))), "choice cards have accessible names and explanations: " + locale);
                Check(nodes.OfType<ComboBox>().Single(combo => combo.Name == "HomeMicrophonePicker").Items.Count == 1,
                    "preview microphone choice does not enumerate real hardware: " + locale);
                string microphoneCaption = UiText.T("Microphone");
                Check(UiText.Contains("Microphone") && (locale == "en" || microphoneCaption != "Microphone") &&
                    nodes.OfType<TextBlock>().Any(text => text.Text == microphoneCaption) &&
                    AutomationProperties.GetName(nodes.OfType<ComboBox>().Single(combo => combo.Name == "HomeMicrophonePicker")) == microphoneCaption,
                    "microphone label and accessible picker name have a real translation: " + locale);
                Check(nodes.OfType<CheckBox>().Single(box => box.Name == "HomeEarlyTranslationsToggle").IsChecked == settings.OwnVoiceSendEarlyPreviews &&
                    nodes.OfType<Button>().Count(button => button.Name.StartsWith("HomePause", StringComparison.Ordinal)) == 4,
                    "message timing and progressive sends are on Home: " + locale);
                const string muteLabel = "Pause when my VRChat mic is muted";
                const string muteHelp = "When off, SayWhat can capture and send translations while your VRChat mic is muted.";
                var followMute = nodes.OfType<CheckBox>().Single(box => box.Name == "HomeFollowVrchatMuteToggle");
                var muteHint = nodes.OfType<TextBlock>().Single(text => text.Name == "HomeFollowVrchatMuteHint");
                Check(followMute.IsChecked == settings.OwnVoicePauseWhenVrchatMuted && followMute.IsEnabled &&
                    followMute.Content is TextBlock caption && caption.Text == VoiceFirstText.T(muteLabel) &&
                    caption.TextWrapping == TextWrapping.Wrap && AutomationProperties.GetName(followMute) == caption.Text &&
                    AutomationProperties.GetHelpText(followMute) == VoiceFirstText.T(muteHelp),
                    "mute following is a localized, accessible Home checkbox: " + locale);
                Check(muteHint.Text == VoiceFirstText.T(muteHelp) && muteHint.TextWrapping == TextWrapping.Wrap &&
                    muteHint.Visibility == (settings.OwnVoicePauseWhenVrchatMuted ? Visibility.Collapsed : Visibility.Visible) &&
                    (locale == "en" || muteHint.Text != muteHelp) &&
                    !nodes.OfType<TextBlock>().Any(text => text.Text == UiText.T("Follows your VRChat microphone mute")),
                    "Home explains the off behavior without an unconditional mute-following claim: " + locale);
                Check(nodes.OfType<Border>().Single(border => border.Name == "HomeOwnVoiceCard").Background == ModernTheme.Hero &&
                    nodes.OfType<Border>().Single(border => border.Name == "HomeSubtitlesCard").Background == ModernTheme.Surface,
                    "own voice has emphasis while subtitles remain independent and visible: " + locale);
                followMute.IsChecked = false;
                var offNodes = Nodes(window).ToArray();
                Check(!settings.OwnVoicePauseWhenVrchatMuted &&
                    offNodes.OfType<CheckBox>().Single(box => box.Name == "HomeFollowVrchatMuteToggle").IsChecked == false &&
                    offNodes.OfType<TextBlock>().Single(text => text.Name == "HomeFollowVrchatMuteHint").Visibility == Visibility.Visible &&
                    offNodes.OfType<TextBlock>().Any(text => text.Text == VoiceFirstText.T(
                        "For your voice to appear in chat, turn on OSC in VRChat's Action Menu: OSC → Enabled. You only need to enable it once.")) &&
                    !runtime.OwnRunning && !runtime.IncomingRunning,
                    "turning mute following off stages the setting and updates Home without starting translation: " + locale);
                offNodes.OfType<CheckBox>().Single(box => box.Name == "HomeFollowVrchatMuteToggle").IsChecked = true;
                Check(settings.OwnVoicePauseWhenVrchatMuted &&
                    Nodes(window).OfType<CheckBox>().Single(box => box.Name == "HomeFollowVrchatMuteToggle").IsChecked == true,
                    "turning mute following back on restores the checked Home state: " + locale);
            }
            UiText.Language = "en";
            checks += CheckStagedBatch(window, settings, runtime, Field);
            settings.OwnRecognitionEngine = "voxtral"; settings.OwnVoiceSourceLanguage = "Automatic";
            window.Navigate("Home");
            var sources = Nodes(window).OfType<WrapPanel>().Single(panel => panel.Name == "HomeSpokenLanguageChoices");
            Check(sources.Children.OfType<Button>().Single(button => (string?)button.Tag == "Automatic").IsEnabled &&
                sources.Children.OfType<Button>().Where(button => (string?)button.Tag != "Automatic").All(button => !button.IsEnabled),
                "Voxtral never presents an unsupported forced-source setting as functional");
            settings.OwnRecognitionEngine = "nemotron"; settings.OwnVoiceSourceLanguage = "English";
            window.Navigate("Home");
            var liquid = Nodes(window).OfType<Button>().Single(button => button.Name == "HomeLiquidModelButton");
            Check(!liquid.IsEnabled && !string.IsNullOrEmpty(liquid.ToolTip?.ToString()), "incompatible translator explains why it cannot be selected");
            Field("_busy").SetValue(window, true); window.Navigate("Home");
            Check(Nodes(window).OfType<Button>().Where(button => button.Name is "OwnVoiceStartButton" or "HomeNemotronRecognitionButton" or "HomeHyFastModelButton")
                .All(button => !button.IsEnabled), "busy operations do not accumulate competing starts or changes");
            var busyMute = Nodes(window).OfType<CheckBox>().Single(box => box.Name == "HomeFollowVrchatMuteToggle");
            Check(!busyMute.IsEnabled, "mute following cannot compete with a busy operation");
            busyMute.IsChecked = false;
            Check(settings.OwnVoicePauseWhenVrchatMuted, "busy mute-following callbacks leave the setting unchanged");
            Check(!runtime.OwnRunning && !runtime.IncomingRunning, "UI construction never starts a pipeline");
            foreach (var entry in VoiceFirstText.TestCatalog)
                Check(entry.Value.Length == 3 && entry.Value.All(value => !string.IsNullOrWhiteSpace(value)), "all new labels include three translations: " + entry.Key);
            Field("_busy").SetValue(window, false);
            foreach (ThemePreset theme in AppearanceTheme.Presets)
            {
                AppSettings appearance = before.Copy(); appearance.UiThemeId = theme.Id;
                AppearanceTheme.ApplyForTests(appearance);
                foreach (string locale in new[] { "en", "zh-CN", "ja", "ko" })
                foreach (Size size in new[] { new Size(1160, 800), new Size(900, 650) })
                {
                    UiText.Language = locale; window.Navigate("Home");
                    FrameworkElement body = (FrameworkElement)window.Content;
                    body.Measure(size); body.Arrange(new Rect(new Point(), size)); body.UpdateLayout();
                    Check(body.ActualWidth <= size.Width + .5 && Nodes(window).OfType<WrapPanel>()
                        .Where(panel => panel.Name is "HomeSpokenLanguageChoices" or "HomeTargetLanguageChoices")
                        .All(panel => panel.ActualWidth > 0 && panel.ActualWidth < size.Width),
                        "flat choices wrap inside both desktop and narrow layouts: " + theme.Id + "/" + locale + "/" + size.Width);
                    foreach (Button choice in Nodes(window).OfType<Button>().Where(button => button.Name is
                        "HomeNemotronRecognitionButton" or "HomeVoxtralRecognitionButton" or "HomeHyFastModelButton" or
                        "HomeHyLargerModelButton" or "HomeTranslateGemmaModelButton" or "HomeLiquidModelButton"))
                    {
                        Check(choice.ActualWidth > 0 && Nodes(choice).OfType<TextBlock>().All(text =>
                            ReferenceEquals(text.Foreground, ModernTheme.Text) || ReferenceEquals(text.Foreground, ModernTheme.Muted) ||
                            ReferenceEquals(text.Foreground, ModernTheme.Warning)),
                            "model titles and explanations use live text colors in every light/dark theme: " + theme.Id + "/" + locale);
                    }
                }
            }
            return checks;
        }
        finally
        {
            TranslationProfiles.CopyInto(before, settings);
            Field("_saveStateAppliedSettings").SetValue(window, previousApplied);
            Field("_busy").SetValue(window, previousBusy); Field("_closing").SetValue(window, previousClosing);
            AppearanceTheme.ApplyForTests(before);
            UiText.Language = previousLanguage; window.Navigate(previousPage);
        }
    }

    private static int CheckStagedBatch(MainWindow window, AppSettings settings, RuntimeController runtime, Func<string, FieldInfo> field)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Own Home staged choices: " + detail); checks++; }
        string? isolated = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(!string.IsNullOrWhiteSpace(isolated) && Path.GetFullPath(isolated) == Path.GetFullPath(AppPaths.Root) &&
            !string.Equals(Path.GetFullPath(AppPaths.Root), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppBrand.FileName), StringComparison.OrdinalIgnoreCase),
            "the evidence check uses the desktop runner's isolated data directory");
        var paths = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath };
        var filesBefore = paths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        AppSettings before = settings.Copy();
        AppSettings? appliedBefore = ((AppSettings?)field("_saveStateAppliedSettings").GetValue(window))?.Copy();
        try
        {
            settings.OwnRecognitionEngine = "nemotron"; settings.OwnVoiceSourceLanguage = "English";
            settings.UseManagedTranslator = true; settings.OwnManagedModelId = "hy-mt2-1.8b-q4_k_m";
            settings.OwnVoiceTargetLanguage = "Chinese"; settings.OwnVoiceAdditionalLanguagesEnabled = false;
            settings.OwnVoiceAdditionalTargetLanguages = [];
            settings.OwnMicrophoneDeviceId = "saved test microphone";
            settings.OwnVoiceEndPauseMilliseconds = 1200; settings.OwnVoiceSendEarlyPreviews = false;
            settings.OwnVoicePauseWhenVrchatMuted = true;
            AppSettings baseline = settings.Copy();
            field("_saveStateAppliedSettings").SetValue(window, baseline.Copy());
            settings.IncomingTargetLanguage = baseline.IncomingTargetLanguage == "Korean" ? "Japanese" : "Korean";
            settings.Glossary = "Unapplied advanced glossary";
            window.Navigate("Home");
            Button Button(string name) => Nodes(window).OfType<Button>().Single(button => button.Name == name);
            void Click(Button button)
            { Check(button.IsEnabled, "the intended staged choice is available: " + button.Name); button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
            Check(!Button("ApplyHomeOwnVoiceChoicesButton").IsEnabled &&
                Nodes(window).OfType<TextBlock>().Single(text => text.Name == "HomeOwnVoiceSelectionState").Text == VoiceFirstText.T("Current selection"),
                "unrelated incoming and Advanced drafts do not enable own Apply");
            Button Source(string language) => Nodes(window).OfType<WrapPanel>().Single(panel => panel.Name == "HomeSpokenLanguageChoices")
                .Children.OfType<Button>().Single(button => (string?)button.Tag == language);
            Button Target(string language) => Nodes(window).OfType<WrapPanel>().Single(panel => panel.Name == "HomeTargetLanguageChoices")
                .Children.OfType<Button>().Single(button => (string?)button.Tag == language);
            Click(Source("Automatic"));
            Click(Button("HomeVoxtralRecognitionButton"));
            Click(Target("Japanese")); Click(Target("Korean"));
            Click(Button("HomeHyLargerModelButton"));
            var microphone = Nodes(window).OfType<ComboBox>().Single(combo => combo.Name == "HomeMicrophonePicker");
            Check(microphone.Items.Count == 2, "preview exposes only default plus the saved disconnected test microphone");
            microphone.SelectedItem = microphone.Items[0];
            Click(Button("HomePause900"));
            Nodes(window).OfType<CheckBox>().Single(box => box.Name == "HomeEarlyTranslationsToggle").IsChecked = true;
            Nodes(window).OfType<CheckBox>().Single(box => box.Name == "HomeFollowVrchatMuteToggle").IsChecked = false;

            // Pump past the removed 250ms autosave deadline. Only an unshown
            // preview is used; no microphone enumeration, model or server starts.
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
            Check(filesBefore.All(pair => pair.Value is null ? !File.Exists(pair.Key) :
                File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "a batch of choices leaves saved settings and both host configurations byte-for-byte unchanged after the old deadline");
            Check(!runtime.OwnRunning && !runtime.IncomingRunning && !(bool)field("_busy").GetValue(window)!,
                "batch selection never starts a feature or launches a preparation operation");
            Check(TranslationProfiles.ConfigurationEquals((AppSettings)field("_saveStateAppliedSettings").GetValue(window)!, baseline),
                "batch selection does not replace the applied settings baseline");
            Check(settings.OwnRecognitionEngine == "voxtral" && settings.OwnVoiceSourceLanguage == "Automatic" &&
                settings.OwnManagedModelId == "hy-mt2-7b-q4_k_m" && settings.OwnMicrophoneDeviceId == "default" &&
                settings.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "Chinese", "Japanese", "Korean" }) &&
                settings.OwnVoiceEndPauseMilliseconds == 900 && settings.OwnVoiceSendEarlyPreviews && !settings.OwnVoicePauseWhenVrchatMuted,
                "source, recognition, model, outputs, microphone, pause, early updates and mute remain staged together");
            Check(Button("ApplyHomeOwnVoiceChoicesButton").IsEnabled &&
                Nodes(window).OfType<TextBlock>().Single(text => text.Name == "HomeOwnVoiceSelectionState").Text == VoiceFirstText.T("Not applied yet") &&
                Button("OwnVoiceStartButton").Content?.ToString() == VoiceFirstText.T("Apply & start"),
                "pending choices expose an explicit Apply action and make Start's application behavior clear");
            AppSettings candidate = MainWindow.VoiceFirstCandidate(baseline, settings, includeModels: true);
            Check(candidate.OwnManagedModelId == settings.OwnManagedModelId && candidate.OwnRecognitionEngine == "voxtral" &&
                candidate.OwnVoiceTargetLanguage == "Chinese" && candidate.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "Japanese", "Korean" }) &&
                candidate.OwnMicrophoneDeviceId == "default" && candidate.OwnVoiceEndPauseMilliseconds == 900 &&
                candidate.OwnVoiceSendEarlyPreviews && !candidate.OwnVoicePauseWhenVrchatMuted,
                "the single explicit-apply candidate includes every selected own-voice choice");
            Check(candidate.IncomingTargetLanguage == baseline.IncomingTargetLanguage && candidate.Glossary == baseline.Glossary &&
                settings.IncomingTargetLanguage != baseline.IncomingTargetLanguage && settings.Glossary != baseline.Glossary,
                "the own-scoped candidate does not commit pending incoming or Advanced choices");
            AppSettings draft = settings.Copy();
            AppSettings cancelled = MainWindow.VoiceFirstDraftAfterApplyAttempt(draft, baseline, baseline.Copy());
            Check(TranslationProfiles.ConfigurationEquals(cancelled, draft),
                "cancelled consent or failed preparation preserves the entire draft for retry");
            AppSettings legacy = baseline.Copy(); legacy.OwnVoiceAdditionalLanguagesEnabled = false;
            legacy.OwnVoiceAdditionalTargetLanguages = ["ja", "Japanese"];
            AppSettings legacyDraft = legacy.Copy(); legacyDraft.OwnVoiceEndPauseMilliseconds = 900;
            Check(MainWindow.VoiceFirstDraftAfterApplyAttempt(legacyDraft, legacy, legacy.Copy()).OwnVoiceEndPauseMilliseconds == 900,
                "normalizing an unchanged disabled legacy output list cannot be mistaken for a committed change and erase the draft");
            AppSettings overlaySaved = baseline.Copy(); overlaySaved.SubtitlesFontSize += 2;
            AppSettings rebased = MainWindow.VoiceFirstDraftAfterApplyAttempt(draft, baseline, overlaySaved);
            Check(rebased.SubtitlesFontSize == overlaySaved.SubtitlesFontSize && rebased.OwnManagedModelId == draft.OwnManagedModelId &&
                rebased.IncomingTargetLanguage == draft.IncomingTargetLanguage && rebased.Glossary == draft.Glossary,
                "an independently saved overlay preference is rebased without discarding any pending choices");
            AppSettings committed = baseline.Copy(); committed.OwnVoiceEndPauseMilliseconds = 1800;
            AppSettings known = MainWindow.VoiceFirstDraftAfterApplyAttempt(draft, baseline, committed);
            Check(known.OwnVoiceEndPauseMilliseconds == committed.OwnVoiceEndPauseMilliseconds && known.OwnManagedModelId == committed.OwnManagedModelId &&
                known.IncomingTargetLanguage == draft.IncomingTargetLanguage && known.Glossary == draft.Glossary,
                "a partial apply failure shows the known committed own choices and preserves unrelated drafts");
            TranslationProfiles.CopyInto(MainWindow.VoiceFirstCandidate(settings, baseline, includeModels: true), settings);
            window.Navigate("Home");
            Check(!Button("ApplyHomeOwnVoiceChoicesButton").IsEnabled && Button("OwnVoiceStartButton").Content?.ToString() == VoiceFirstText.T("Start translating my voice"),
                "returning own choices to their baseline disables Apply despite unrelated pending settings");
        }
        finally
        {
            TranslationProfiles.CopyInto(before, settings);
            field("_saveStateAppliedSettings").SetValue(window, appliedBefore);
            window.Navigate("Home");
        }
        return checks;
    }

    private static void RenderPreviews(MainWindow window, AppSettings settings, string directory)
    {
        string previousLanguage = UiText.Language;
        AppSettings appearance = settings.Copy();
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var sample in new[]
            {
                (File: "voice-first-home.png", Theme: "coral-night", Locale: "en", Width: 1160, Height: 800),
                (File: "voice-first-home-narrow.png", Theme: "coral-night", Locale: "en", Width: 900, Height: 650),
                (File: "voice-first-home-light-zh.png", Theme: "paper", Locale: "zh-CN", Width: 1160, Height: 800),
                (File: "voice-first-home-ko.png", Theme: "coral-night", Locale: "ko", Width: 1160, Height: 800)
            })
            {
                AppSettings selected = appearance.Copy(); selected.UiThemeId = sample.Theme;
                AppearanceTheme.ApplyForTests(selected); UiText.Language = sample.Locale; window.Navigate("Home");
                FrameworkElement frame = (FrameworkElement)window.Content;
                Size size = new(sample.Width, sample.Height);
                frame.Measure(size); frame.Arrange(new Rect(new Point(), size)); frame.UpdateLayout();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var content = (ContentControl)typeof(MainWindow).GetField("_content", flags)!.GetValue(window)!;
                (content.Parent as ScrollViewer)?.ScrollToTop(); frame.UpdateLayout();
                var bitmap = new RenderTargetBitmap(sample.Width, sample.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(frame);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, sample.File)); encoder.Save(file);
            }
        }
        finally
        {
            AppearanceTheme.ApplyForTests(appearance); UiText.Language = previousLanguage; window.Navigate("Home");
        }
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject nested in Nodes(dependency)) yield return nested;
    }
}

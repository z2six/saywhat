using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Detached settings, synthetic runtime flags, and an unshown preview only.
// These checks never download, load a model, enumerate devices, or capture audio.
internal static class HomeSubtitleUiTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Home subtitle controls: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, flags)!;
        var settings = (AppSettings)Field("_settings").GetValue(window)!;
        var runtime = (RuntimeController)Field("_runtime").GetValue(window)!;
        var ownFlag = typeof(RuntimeController).GetField("_ownRunning", flags)!;
        var incomingFlag = typeof(RuntimeController).GetField("_incomingRunning", flags)!;
        Check((bool)Field("_preview").GetValue(window)! && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning,
            "requires the isolated, unshown preview");
        AppSettings before = settings.Copy(); object? appliedBefore = Field("_saveStateAppliedSettings").GetValue(window);
        string languageBefore = UiText.Language, pageBefore = (string)Field("_page").GetValue(window)!;
        bool busyBefore = (bool)Field("_busy").GetValue(window)!, closingBefore = (bool)Field("_closing").GetValue(window)!;
        var protectedFiles = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath }
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        Button Choice(string name, object tag) => Nodes(window).OfType<Button>().Single(button => button.Name == name && Equals(button.Tag, tag));
        void Click(string name, object tag) => Choice(name, tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            var saved = new AppSettings
            {
                FirstRunSetupCompleted = true, UseManagedTranslator = true, ShareTranslationModel = true,
                OwnManagedModelId = "hy-mt2-1.8b-q4_k_m", IncomingManagedModelId = "hy-mt2-1.8b-q4_k_m",
                OwnRecognitionEngine = "nemotron", OwnVoiceSourceLanguage = "English", OwnVoiceTargetLanguage = "Chinese",
                IncomingSpeechEngine = "firered2-aed", IncomingSourceLanguage = "Chinese",
                IncomingNormalSpeechEngine = "firered2-aed", IncomingNormalSourceLanguage = "Chinese",
                IncomingTargetLanguage = "English", IncomingSettleMilliseconds = 1200, UseAiSpeakerRecognition = false,
                DetectAnySourceLanguage = false, SourceLanguages = ["Chinese"]
            };
            var draft = saved.Copy();
            draft.OwnManagedModelId = "hy-mt2-7b-q4_k_m"; draft.OwnVoiceSourceLanguage = "Japanese";
            draft.OwnVoiceTargetLanguage = "Korean"; draft.OwnVoiceEndPauseMilliseconds = 2400;
            draft.OwnMicrophoneDeviceId = "synthetic unsaved microphone"; draft.OwnVoicePauseWhenVrchatMuted = false;
            draft.IncomingSpeechEngine = "firered2-ctc"; draft.IncomingTargetLanguage = "Japanese";
            draft.IncomingSettleMilliseconds = 1800; draft.IncomingAudioMode = "application";
            draft.IncomingApplicationPath = @"C:\Synthetic\Discord.exe"; draft.IncomingApplicationName = "Discord";
            draft.IncomingSinglePerson = true; draft.IncomingPersonId = "synthetic-person";
            AppSettings candidate = MainWindow.HomeSubtitleCandidate(saved, draft);
            Check(candidate.IncomingSpeechEngine == "firered2-ctc" && candidate.IncomingTargetLanguage == "Japanese" &&
                candidate.IncomingSettleMilliseconds == 1800 && candidate.IncomingAudioMode == "application" &&
                candidate.IncomingApplicationPath == draft.IncomingApplicationPath && candidate.IncomingSinglePerson && candidate.IncomingPersonId == "synthetic-person",
                "scoped apply includes recognizer, languages, pause, source and assigned person");
            Check(candidate.OwnManagedModelId == saved.OwnManagedModelId && candidate.OwnVoiceSourceLanguage == saved.OwnVoiceSourceLanguage &&
                candidate.OwnVoiceTargetLanguage == saved.OwnVoiceTargetLanguage && candidate.OwnVoiceEndPauseMilliseconds == saved.OwnVoiceEndPauseMilliseconds &&
                candidate.OwnMicrophoneDeviceId == saved.OwnMicrophoneDeviceId && candidate.OwnVoicePauseWhenVrchatMuted == saved.OwnVoicePauseWhenVrchatMuted,
                "subtitle apply cannot commit unrelated microphone edits");
            var changes = SettingsRuntimeChanges.Between(saved, candidate);
            Check(!changes.OwnChanged && changes.IncomingChanged && !changes.SharedTranslatorChanged,
                "incoming-only changes leave the working microphone and shared translator alone");
            AppSettings ownStart = MainWindow.VoiceFirstCandidate(saved, draft, includeModels: true);
            Check(ownStart.OwnManagedModelId == draft.OwnManagedModelId && ownStart.OwnVoiceTargetLanguage == draft.OwnVoiceTargetLanguage &&
                ownStart.IncomingSpeechEngine == saved.IncomingSpeechEngine && ownStart.IncomingTargetLanguage == saved.IncomingTargetLanguage &&
                ownStart.IncomingSettleMilliseconds == saved.IncomingSettleMilliseconds && ownStart.IncomingAudioMode == saved.IncomingAudioMode &&
                ownStart.IncomingApplicationPath == saved.IncomingApplicationPath && ownStart.IncomingPersonId == saved.IncomingPersonId &&
                ownStart.ShareTranslationModel == saved.ShareTranslationModel,
                "the own Start preparation snapshot leaves subtitle drafts and sharing unapplied");
            candidate = saved.Copy();
            Check(MainWindow.StageHomeSubtitleTranslator(candidate, "hy-mt2-7b-q4_k_m") && !candidate.ShareTranslationModel &&
                candidate.OwnManagedModelId == saved.OwnManagedModelId && candidate.IncomingManagedModelId == "hy-mt2-7b-q4_k_m",
                "choosing a different incoming model visibly separates it without overwriting the own model");
            changes = SettingsRuntimeChanges.Between(saved, candidate);
            Check(changes.SharedTranslatorChanged, "changing sharing reaches the existing explicit restart confirmation");
            Check(MainWindow.StageHomeSubtitleSharing(candidate, true) && candidate.ShareTranslationModel,
                "a compatible incoming direction can return to sharing");
            candidate.UseManagedTranslator = false;
            Check(!MainWindow.StageHomeSubtitleTranslator(candidate, "hy-mt2-7b-q4_k_m") && !candidate.UseManagedTranslator,
                "the incoming card never takes over an external microphone translator");
            candidate = saved.Copy(); candidate.IncomingSourceLanguage = "Automatic";
            Check(!MainWindow.StageHomeSubtitleTranslator(candidate, "translategemma-4b-q4_k_m") &&
                !MainWindow.StageHomeSubtitleTranslator(candidate, "lfm2-350m-enjp-mt-q4_k_m"),
                "specialized translators require a supported explicit spoken language");
            candidate.IncomingSourceLanguage = "Japanese";
            Check(MainWindow.StageHomeSubtitleTranslator(candidate, "lfm2-350m-enjp-mt-q4_k_m") &&
                !MainWindow.StageHomeSubtitleTarget(candidate, "Chinese") && MainWindow.StageHomeSubtitleTarget(candidate, "en"),
                "Liquid permits only English/Japanese incoming targets and canonicalizes aliases");
            Check(!MainWindow.StageHomeSubtitlePause(candidate, 599) && !MainWindow.StageHomeSubtitlePause(candidate, 2401) &&
                MainWindow.StageHomeSubtitlePause(candidate, 900) && candidate.OwnVoiceEndPauseMilliseconds == saved.OwnVoiceEndPauseMilliseconds,
                "pause editing is bounded and cannot shorten microphone sentences");
            candidate = saved.Copy(); int recognitionDelay = candidate.RecognitionDelayMilliseconds;
            Check(MainWindow.StageHomeSubtitleProfile(candidate, TranslationProfiles.GroupsId) && candidate.IncomingSpeechEngine == "vibevoice" &&
                candidate.IncomingNormalSpeechEngine == "firered2-aed" && candidate.IncomingSourceLanguage == "Automatic" &&
                MainWindow.StageHomeSubtitleProfile(candidate, TranslationProfiles.NormalId) && candidate.IncomingSpeechEngine == "firered2-aed" && candidate.IncomingSourceLanguage == "Chinese" &&
                candidate.RecognitionDelayMilliseconds == recognitionDelay && candidate.OwnVoiceEndPauseMilliseconds == saved.OwnVoiceEndPauseMilliseconds,
                "Normal/Groups remembers the incoming choice without applying a global speed preset");
            Check(!MainWindow.StageHomeSubtitleProfile(candidate, "invalid") && !MainWindow.StageHomeSubtitleTranslator(candidate, "voxtral-realtime-q4_k_m"),
                "hidden profile and speech-model IDs cannot become translation choices");
            var quick = saved.Copy(); quick.ActiveTranslationProfileId = TranslationProfiles.QuickId;
            var normalDraft = quick.Copy(); MainWindow.StageHomeSubtitleProfile(normalDraft, TranslationProfiles.NormalId);
            Check(MainWindow.HomeSubtitleCandidate(quick, normalDraft).ActiveTranslationProfileId == TranslationProfiles.NormalId &&
                MainWindow.HomeSubtitleCandidate(quick, normalDraft).RecognitionDelayMilliseconds == quick.RecognitionDelayMilliseconds,
                "the Home Normal choice exits Quick without changing its global recognition delay");

            TranslationProfiles.CopyInto(saved, settings); Field("_saveStateAppliedSettings").SetValue(window, saved.Copy());
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language; window.Navigate("Home");
                var nodes = Nodes(window).ToArray();
                Check(nodes.OfType<Button>().Count(button => button.Name == "SubtitleSpeechModelChoice") == 4 &&
                    nodes.OfType<Button>().Count(button => button.Name == "SubtitleSourceLanguageChoice") == 10 &&
                    nodes.OfType<Button>().Count(button => button.Name == "HomeSubtitleTargetChoice") == AppSettings.TranslationLanguages.Length &&
                    nodes.OfType<Button>().Count(button => button.Name == "HomeSubtitleTranslatorChoice") == 4,
                    "Home directly exposes source, target, recognition and translation choices: " + language);
                Check(nodes.OfType<Button>().Count(button => button.Name == "HomeSubtitleAudioSourceChoice") == 3 &&
                    nodes.OfType<Button>().Count(button => button.Name == "HomeSubtitlePauseChoice") == 4 &&
                    nodes.OfType<CheckBox>().Any(check => check.Name == "HomeSubtitlePersonRecognitionToggle"),
                    "audio source, sentence timing and optional people recognition are on Home");
                var chineseOnly = nodes.OfType<CheckBox>().Single(check => check.Name == "SubtitleChineseOnlyToggle");
                Check(chineseOnly.IsEnabled && chineseOnly.Visibility == Visibility.Visible &&
                    chineseOnly.Content?.ToString() == UiText.T("Only show Chinese speech") &&
                    AutomationProperties.GetName(chineseOnly) == UiText.T("Only show Chinese speech") &&
                    nodes.OfType<TextBlock>().Any(text => text.Text == UiText.T("Skip other languages. Short or mixed-language speech may sometimes be missed.")) &&
                    nodes.OfType<TextBlock>().Any(text => text.Text == UiText.T("Changing only this filter does not restart models.")),
                    "Home exposes the translated Chinese filter, limits and live-apply behavior beside spoken-language choices: " + language);
                Check(nodes.OfType<TextBlock>().Any(text => text.Text == SubtitleSpeechText.T("Translation model · translates their words")) &&
                    nodes.OfType<Button>().Where(button => button.Name == "HomeSubtitleTargetChoice")
                        .All(button => AutomationProperties.GetName(button).StartsWith(UiText.T("Translate their voices to") + ": ", StringComparison.Ordinal)),
                    "incoming labels and accessible language names use the selected interface language");
                Check(nodes.OfType<Button>().Single(button => button.Name == "OwnVoiceStartButton").IsEnabled,
                    "optional subtitle controls do not disable own-voice start");
            }
            UiText.Language = "en"; window.Navigate("Home");
            Nodes(window).OfType<CheckBox>().Single(check => check.Name == "SubtitleChineseOnlyToggle")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.IncomingChineseOnly && settings.OwnVoiceSourceLanguage == saved.OwnVoiceSourceLanguage &&
                settings.OwnVoiceTargetLanguage == saved.OwnVoiceTargetLanguage,
                "Home's filter is a staged incoming-only change without applying microphone drafts");
            Click("SubtitleSourceLanguageChoice", "Automatic");
            Check(settings.IncomingSpeechEngine == "nemotron" && settings.IncomingSourceLanguage == "Automatic" && !settings.IncomingChineseOnly,
                "Home switches to a multilingual recognizer and unrestricted filtering for Any language");
            Check(!Choice("HomeSubtitleTranslatorChoice", "translategemma-4b-q4_k_m").IsEnabled, "TranslateGemma explains its explicit-language requirement before selection");
            Click("SubtitleSourceLanguageChoice", "English"); Click("HomeSubtitleTranslatorChoice", "lfm2-350m-enjp-mt-q4_k_m");
            Check(!settings.ShareTranslationModel && settings.IncomingManagedModelId == "lfm2-350m-enjp-mt-q4_k_m" &&
                settings.OwnManagedModelId == saved.OwnManagedModelId && !Choice("HomeSubtitleTargetChoice", "Chinese").IsEnabled,
                "Home keeps the own translator and disables unsupported targets after choosing Liquid");
            ownFlag.SetValue(runtime, true); incomingFlag.SetValue(runtime, true); window.Navigate("Home");
            var liveFilter = Nodes(window).OfType<CheckBox>().Single(check => check.Name == "SubtitleChineseOnlyToggle");
            Check(liveFilter.IsEnabled, "the filter remains editable while both synthetic directions are running");
            liveFilter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.IncomingChineseOnly && runtime.OwnRunning && runtime.IncomingRunning,
                "staging a live filter choice does not stop either direction");
            Click("HomeSubtitlePauseChoice", 1800);
            Check(settings.IncomingSettleMilliseconds == 1800 && runtime.OwnRunning && runtime.IncomingRunning &&
                Choice("HomeSubtitleAudioSourceChoice", "application").IsEnabled,
                "choices can be staged while running without stopping either synthetic direction");
            Click("HomeSubtitleAudioSourceChoice", "application");
            Check(Nodes(window).OfType<Button>().Any(button => button.Name == "HomeSubtitleChooseApplicationButton" && button.IsEnabled),
                "application selection is available directly on Home while staging");
            incomingFlag.SetValue(runtime, false); window.Navigate("Home");
            Check(!Nodes(window).OfType<Button>().Single(button => button.Name == "SubtitlesStartButton").IsEnabled &&
                Nodes(window).OfType<Button>().Single(button => button.Name == "ApplyHomeSubtitleChoicesButton").IsEnabled,
                "unapplied choices show a distinct action rather than starting old saved settings");
            foreach (bool busy in new[] { false, true })
            foreach (bool closing in new[] { false, true })
            {
                Field("_busy").SetValue(window, busy); Field("_closing").SetValue(window, closing); window.Navigate("Home");
                Check(Choice("HomeSubtitlePauseChoice", 1200).IsEnabled == (!busy && !closing), "staging respects busy and closing guards");
                Check(Nodes(window).OfType<CheckBox>().Single(check => check.Name == "SubtitleChineseOnlyToggle").IsEnabled == (!busy && !closing),
                    "the Chinese filter respects busy and closing guards");
            }
            Field("_busy").SetValue(window, false); Field("_closing").SetValue(window, false); ownFlag.SetValue(runtime, false);
            TranslationProfiles.CopyInto(saved, settings); window.Navigate("Home");
            var card = (FrameworkElement)typeof(MainWindow).GetMethod("CreateVoiceFirstSubtitleCard", flags)!.Invoke(window, null)!;
            Render(card, Path.Combine(directory, "home-subtitle-controls.png"), 780);
            Check(protectedFiles.All(file => file.Value is null ? !File.Exists(file.Key) : File.Exists(file.Key) && File.ReadAllBytes(file.Key).SequenceEqual(file.Value)),
                "preview staging never writes preferences or capture-host configs");
            return checks;
        }
        finally
        {
            ownFlag.SetValue(runtime, false); incomingFlag.SetValue(runtime, false);
            Field("_busy").SetValue(window, busyBefore); Field("_closing").SetValue(window, closingBefore);
            Field("_saveStateAppliedSettings").SetValue(window, appliedBefore);
            TranslationProfiles.CopyInto(before, settings); AppearanceTheme.Apply(before); UiText.Language = languageBefore; window.Navigate(pageBefore);
        }
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node)
                foreach (DependencyObject descendant in Nodes(node)) yield return descendant;
    }

    private static void Render(FrameworkElement element, string path, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity)); double height = Math.Ceiling(element.DesiredSize.Height);
        element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var output = File.Create(path); encoder.Save(output);
    }
}

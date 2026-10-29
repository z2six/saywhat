using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Selection, persistence and unshown UI only. No recognizer is constructed,
// downloads are never requested, and no audio device or live process is touched.
internal static class SubtitleSpeechUiTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Subtitle speech UI: " + message); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", flags)!.GetValue(window)!;
        var runtime = (RuntimeController)typeof(MainWindow).GetField("_runtime", flags)!.GetValue(window)!;
        Check((bool)typeof(MainWindow).GetField("_preview", flags)!.GetValue(window)! && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning,
            "requires an unshown isolated preview");
        AppSettings original = settings.Copy();
        string originalLanguage = UiText.Language;
        string pageBefore = (string)typeof(MainWindow).GetField("_page", flags)!.GetValue(window)!;
        string[] paths = [AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "incoming-translator.log")];
        var bytesBefore = paths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        try
        {
            AppSettings legacy = SettingsStore.ReadSettings("{\"IncomingSpeechEngine\":\"voxtral\",\"IncomingSourceLanguage\":\"Automatic\"}");
            Check(legacy.IncomingSpeechEngine == "voxtral" && legacy.IncomingNormalSpeechEngine == "voxtral" && legacy.IncomingSourceLanguage == "Automatic",
                "existing Voxtral installations retain their recognizer and automatic source");
            var selected = new AppSettings { IncomingSpeechEngine = "firered2-aed", IncomingSourceLanguage = "Chinese", OwnVoiceSourceLanguage = "English" };
            selected = SettingsStore.ReadSettings(JsonSerializer.Serialize(selected));
            Check(selected.IncomingSpeechEngine == "firered2-aed" && selected.IncomingNormalSpeechEngine == "firered2-aed" && selected.IncomingNormalSourceLanguage == "Chinese",
                "settings round trip retains native engine and model memory");
            var copied = selected.Copy(); copied.IncomingNormalSourceLanguage = "English";
            Check(selected.IncomingNormalSourceLanguage == "Chinese", "runtime snapshots copy independent recognition choices");
            foreach (string id in new[] { TranslationProfiles.QuickId, TranslationProfiles.NormalId })
            {
                var preset = TranslationProfiles.Select(selected, id);
                Check(preset.IncomingSpeechEngine == "firered2-aed" && preset.IncomingSourceLanguage == "Chinese", "speed profile keeps the selected subtitle model and language");
            }
            var groups = TranslationProfiles.Select(selected, TranslationProfiles.GroupsId);
            Check(groups.IncomingSpeechEngine == "vibevoice" && groups.IncomingSourceLanguage == "Automatic" &&
                groups.IncomingNormalSpeechEngine == "firered2-aed" && groups.IncomingNormalSourceLanguage == "Chinese",
                "Groups remembers the Normal recognition pair while using its own automatic model");
            var normal = TranslationProfiles.Select(groups, TranslationProfiles.NormalId);
            Check(normal.IncomingSpeechEngine == "firered2-aed" && normal.IncomingSourceLanguage == "Chinese", "returning from Groups restores the prior model and language");
            var savedProfile = TranslationProfiles.Create(selected, "Chinese subtitle model");
            var restoredProfile = TranslationProfiles.Select(TranslationProfiles.Select(savedProfile, TranslationProfiles.GroupsId), savedProfile.ActiveTranslationProfileId);
            Check(restoredProfile.IncomingSpeechEngine == "firered2-aed" && restoredProfile.IncomingSourceLanguage == "Chinese", "custom profiles include recognition choices");

            var draft = new AppSettings { OwnVoiceSourceLanguage = "Japanese", OwnVoiceTargetLanguage = "Korean", OwnManagedModelId = "another-own-model",
                IncomingSpeechEngine = "firered2-ctc", IncomingSourceLanguage = "Chinese", IncomingNormalSpeechEngine = "firered2-ctc", IncomingNormalSourceLanguage = "Chinese" };
            var baseline = new AppSettings();
            var scoped = MainWindow.SubtitleSpeechCandidate(baseline, draft);
            Check(scoped.IncomingSpeechEngine == "firered2-ctc" && scoped.IncomingSourceLanguage == "Chinese" && !scoped.DetectAnySourceLanguage && scoped.SourceLanguages.SequenceEqual(new[] { "Chinese" }),
                "the scoped action applies incoming recognition and compatible legacy translation hints");
            Check(scoped.OwnVoiceSourceLanguage == baseline.OwnVoiceSourceLanguage && scoped.OwnVoiceTargetLanguage == baseline.OwnVoiceTargetLanguage && scoped.OwnManagedModelId == baseline.OwnManagedModelId,
                "unsaved microphone language/model changes cannot be applied by the subtitle action");
            var stage = baseline.Copy();
            Check(MainWindow.StageSubtitleSourceLanguage(stage, "Chinese") && stage.IncomingSpeechEngine == "firered2-aed" && stage.IncomingSourceLanguage == "Chinese", "Chinese from automatic Voxtral visibly selects a compatible Chinese model");
            Check(MainWindow.StageSubtitleSourceLanguage(stage, "Automatic") && stage.IncomingSpeechEngine == "nemotron" && stage.IncomingSourceLanguage == "Automatic", "Any language replaces a Chinese-only model with multilingual recognition");
            Check(MainWindow.StageSubtitleSpeechEngine(stage, "firered2-ctc") && stage.IncomingSourceLanguage == "Chinese", "choosing FireRed from Automatic selects a supported Chinese source");
            Check(MainWindow.StageSubtitleSourceLanguage(stage, "Japanese") && stage.IncomingSpeechEngine == "nemotron", "Japanese never reaches a Chinese/English-only model");
            Check(!MainWindow.StageSubtitleSourceLanguage(stage, "Thai") && !MainWindow.StageSubtitleSpeechEngine(stage, "unknown") && !MainWindow.StageSubtitleSpeechEngine(stage, "vibevoice"),
                "unsupported speech inputs and hidden engine IDs cannot be staged");
            Check(stage.OwnVoiceSourceLanguage == baseline.OwnVoiceSourceLanguage && stage.OwnVoiceTargetLanguage == baseline.OwnVoiceTargetLanguage,
                "recognition staging leaves own voice choices untouched");

            TranslationProfiles.CopyInto(new AppSettings { FirstRunSetupCompleted = true, UseAiSpeakerRecognition = false,
                IncomingSpeechEngine = "firered2-aed", IncomingSourceLanguage = "Chinese" }, settings);
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language; window.Navigate("Listening");
                var modelChoices = Nodes(window).OfType<Button>().Where(button => button.Name == "SubtitleSpeechModelChoice").ToArray();
                Check(modelChoices.Length == 4 && modelChoices.Any(button => Equals(button.Tag, "firered2-aed")) && modelChoices.Any(button => Equals(button.Tag, "firered2-ctc")),
                    "both real FireRed2 variants, Nemotron and Voxtral are visible without a model dropdown");
                Check(modelChoices.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))), "model buttons have accessible names");
                Check(Nodes(window).OfType<TextBlock>().Any(text => text.Text == SubtitleSpeechText.T("Subtitle speech recognition")), "the section is translated in " + language);
                var choices = Nodes(window).OfType<Button>().Where(button => button.Name == "SubtitleSourceLanguageChoice").ToArray();
                Check(choices.Length == 10 && choices.Any(button => Equals(button.Tag, "Chinese")) && choices.Any(button => Equals(button.Tag, "Automatic")), "source languages are directly visible");
                var auto = choices.Single(button => Equals(button.Tag, "Automatic"));
                auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(settings.IncomingSpeechEngine == "nemotron" && settings.IncomingSourceLanguage == "Automatic", "the language action immediately reveals a compatible model");
                Nodes(window).OfType<Button>().Single(button => button.Name == "SubtitleSpeechModelChoice" && Equals(button.Tag, "firered2-aed")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(settings.IncomingSpeechEngine == "firered2-aed" && settings.IncomingSourceLanguage == "Chinese", "staging a model keeps controls in sync without saving or starting it");
                Check(Nodes(window).OfType<Button>().Single(button => button.Name == "ApplySubtitleSpeechChoicesButton").IsEnabled, "the explicit apply/download action is visible for staged choices");
                if (language == "en")
                {
                    FrameworkElement card = Nodes(window).OfType<FrameworkElement>().Single(element => element.Name == "SubtitleSpeechSettingsCard");
                    Render(card, Path.Combine(directory, "subtitle-speech-options.png"), 760, 860);
                }
            }
            foreach (var entry in SubtitleSpeechText.TestCatalog)
                Check(entry.Value.Length == 3 && entry.Value.All(value => !string.IsNullOrWhiteSpace(value)), "every new interface phrase has all three translations");
            Check(bytesBefore.All(pair => pair.Value is null ? !File.Exists(pair.Key) : File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "browsing and staging did not change saved settings/config/audio/logs");
            return checks;
        }
        finally
        {
            TranslationProfiles.CopyInto(original, settings); AppearanceTheme.Apply(original); UiText.Language = originalLanguage; window.Navigate(pageBefore);
        }
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject node in Nodes(dependency)) yield return node;
    }

    private static void Render(FrameworkElement element, string path, double width, double height)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        double measured = Math.Min(Math.Max(element.DesiredSize.Height, height), 1600);
        element.Arrange(new Rect(0, 0, width, measured)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)measured, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var stream = File.Create(path); encoder.Save(stream);
    }
}

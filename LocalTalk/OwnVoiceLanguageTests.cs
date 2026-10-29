using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Synthetic settings and unshown preview controls only. The parent runner has redirected
// AppPaths to a fresh test directory before this runs; no runtime or audio method is called.
internal static class OwnVoiceLanguageTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Own voice languages: " + detail); checks++; }
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Check((bool)typeof(MainWindow).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! && !window.IsVisible,
            "UI checks require an unshown inert preview");
        string settingsBefore = File.ReadAllText(AppPaths.SettingsPath);
        string fastBefore = File.ReadAllText(AppPaths.FastConfigPath), sentenceBefore = File.ReadAllText(AppPaths.FoxTransConfigPath);
        string previousUi = UiText.Language;
        string priorPage = (string)typeof(MainWindow).GetField("_page", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        AppSettings settingsSnapshot = settings.Copy();
        try
        {
            Check(new AppSettings() is { OwnVoiceAdditionalLanguagesEnabled: false } defaults && defaults.OwnVoiceAdditionalTargetLanguages.Count == 0,
                "new users have no additional translations");
            var legacy = SettingsStore.ReadSettings("{\"OwnVoiceTargetLanguage\":\"Japanese\"}");
            Check(!legacy.OwnVoiceAdditionalLanguagesEnabled && legacy.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "Japanese" }),
                "old profiles remain single-language and retain their chosen target");
            Check(legacy.OwnVoiceSourceLanguage == "Automatic" && legacy.IncomingSourceLanguage == "Automatic", "old profiles keep automatic source detection");
            Check(MainWindow.SetupModelsRequiringTerms(new AppSettings()).Count == 0, "default Hy setup requires no optional-model terms prompt");
            var specialSetup = new AppSettings { UseManagedTranslator = true, ShareTranslationModel = false,
                OwnManagedModelId = "translategemma-4b-q4_k_m", IncomingManagedModelId = "lfm2-350m-enjp-mt-q4_k_m" };
            Check(MainWindow.SetupModelsRequiringTerms(specialSetup).Select(model => model.Id).SequenceEqual(new[] { "translategemma-4b-q4_k_m", "lfm2-350m-enjp-mt-q4_k_m" }),
                "setup requires explicit model terms for both independent optional selections before system changes");
            specialSetup.ShareTranslationModel = true;
            Check(MainWindow.SetupModelsRequiringTerms(specialSetup).Count == 1, "one shared optional model has one terms confirmation");
            specialSetup.FirstRunSetupCompleted = true; specialSetup.UseManagedTranslator = false;
            Check(MainWindow.SetupModelsRequiringTerms(specialSetup).Count == 0, "existing external endpoints do not silently download a catalog model");
            var source = new AppSettings { OwnVoiceSourceLanguage = "English", IncomingSourceLanguage = "Chinese" };
            Check(source.Copy().OwnVoiceSourceLanguage == "English" && SettingsStore.ReadSettings(JsonSerializer.Serialize(source)).IncomingSourceLanguage == "Chinese",
                "runtime snapshots and profile persistence retain the independent explicit source selections");
            var four = new AppSettings { OwnVoiceTargetLanguage = "English", OwnVoiceAdditionalLanguagesEnabled = true,
                OwnVoiceAdditionalTargetLanguages = [" ja ", "Japanese", "zh-CN", "English", "Korean", "invalid", ""] };
            Check(four.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "English", "Japanese", "Chinese", "Korean" }),
                "primary first, canonical aliases, duplicate removal and maximum four languages");
            var copy = four.Copy(); copy.OwnVoiceAdditionalTargetLanguages.Clear();
            Check(four.OwnVoiceAdditionalTargetLanguages.Count == 7 && copy.OwnVoiceAdditionalLanguagesEnabled,
                "runtime snapshot owns an independent extra-language list");
            var roundtrip = SettingsStore.ReadSettings(JsonSerializer.Serialize(four));
            Check(roundtrip.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "Japanese", "Chinese", "Korean" }) && roundtrip.OwnVoiceAdditionalLanguagesEnabled,
                "profile roundtrip canonicalizes extras without disabling the explicit choice");
            roundtrip.OwnVoiceAdditionalLanguagesEnabled = false;
            Check(roundtrip.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "English" }) && roundtrip.OwnVoiceAdditionalTargetLanguages.Count == 3,
                "turning off extras retains saved choices but uses only the primary target");
            Check(SettingsStore.ReadSettings("{\"OwnVoiceAdditionalLanguagesEnabled\":true,\"OwnVoiceAdditionalTargetLanguages\":null}")
                .EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "Chinese" }), "null lists are safely empty");
            var primaryOnly = new AppSettings { OwnVoiceTargetLanguage = "English" };
            SettingsStore.Save(primaryOnly);
            JsonNode primaryConfig = JsonNode.Parse(File.ReadAllText(AppPaths.FastConfigPath))!;
            SettingsStore.Save(four);
            Check(SettingsStore.Load().EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "English", "Japanese", "Chinese", "Korean" }),
                "saving persists enabled extras for the host");
            foreach (string path in new[] { AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath })
            {
                JsonNode config = JsonNode.Parse(File.ReadAllText(path))!;
                Check(JsonNode.DeepEquals(config["pipeline"]?["translation"], primaryConfig["pipeline"]?["translation"]),
                    "generated FoxTrans translator configuration remains primary-only: " + Path.GetFileName(path));
                Check(!config["pipeline"]!["translation"]!.ToJsonString().Contains("Japanese", StringComparison.Ordinal),
                    "additional targets do not leak into the dedicated single-target translation prompt");
            }

            settings.OwnVoiceTargetLanguage = "English";
            settings.OwnRecognitionEngine = "nemotron";
            settings.OwnVoiceAdditionalTargetLanguages = ["Japanese", "Chinese", "Korean"];
            settings.OwnVoiceAdditionalLanguagesEnabled = false;
            UiText.Language = "en"; window.Navigate("Languages");
            CheckBox enabled = Nodes(window).OfType<CheckBox>().Single(box => box.Content?.ToString() == "Translate my voice into more languages");
            ComboBox Picker(string label) => Nodes(window).OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == label);
            ComboBox first = Picker("Additional language 1"), second = Picker("Additional language 2"), third = Picker("Additional language 3");
            var extraPanel = (StackPanel)((Grid)((FrameworkElement)first.Parent!).Parent!).Parent!;
            Check(extraPanel.Visibility == Visibility.Collapsed && enabled.IsChecked == false, "extra selectors are hidden by default on the everyday Languages page");
            Check(settings.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "Japanese", "Chinese", "Korean" }), "rendering the page does not edit saved choices");
            enabled.IsChecked = true;
            Check(extraPanel.Visibility == Visibility.Visible && settings.OwnVoiceAdditionalLanguagesEnabled, "one toggle reveals all three optional selectors");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1160, 820)); content.Arrange(new Rect(0, 0, 1160, 820)); content.UpdateLayout();
            MainWindow.ApplySlimScrollbars(content); content.UpdateLayout();
            var image = new RenderTargetBitmap(1160, 820, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(directory, "languages-extra-preview.png"))) encoder.Save(file);
            Check(!first.Items.Contains("English") && !first.Items.Contains("Chinese") && !first.Items.Contains("Korean") &&
                !second.Items.Contains("Japanese") && !third.Items.Contains("Japanese") && !third.Items.Contains("Chinese"), "selectors exclude primary and every other selected extra");
            Check(first.Items.Contains("Thai"), "additional-output selector offers Thai from the canonical target catalog");
            first.SelectedItem = "Thai";
            Check(settings.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "English", "Thai", "Chinese", "Korean" }),
                "Thai can replace an additional output while preserving the other selected languages");
            first.SelectedItem = "French";
            Check(settings.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "English", "French", "Chinese", "Korean" }), "selecting an extra updates all four runtime preferences");
            Picker("Translate my voice to").SelectedItem = "French";
            Check(settings.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "French", "Chinese", "Korean" }) && (string)first.SelectedItem == "None",
                "switching the primary removes its duplicate from extras");
            enabled.IsChecked = false;
            Check(settings.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "French" }) && settings.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "Chinese", "Korean" }),
                "disabling additional outputs does not discard remaining choices");
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language; window.Navigate("Languages");
                Check(Nodes(window).OfType<CheckBox>().Any(box => box.Content?.ToString() == UiText.T("Translate my voice into more languages")),
                    "the additional-language option is localized: " + language);
                foreach (string label in new[] { "Additional language 1", "Additional language 2", "Additional language 3" })
                    Check(Nodes(window).OfType<ComboBox>().Any(box => AutomationProperties.GetName(box) == UiText.T(label)),
                        "extra selector accessible labels follow interface language: " + language);
            }
            UiText.Language = "en"; settings.FirstRunSetupCompleted = true;
            window.Navigate("Home");
            settings.OwnVoiceAdditionalLanguagesEnabled = true;
            settings.OwnVoiceAdditionalTargetLanguages = ["English", "Chinese", "Korean"];
            window.Navigate("Home");
            var chips = Nodes(window).OfType<WrapPanel>().Single(panel => panel.Name == "HomeTargetLanguageChoices");
            Check(chips.Children.OfType<Button>().Where(button => button.Content?.ToString()?.StartsWith("+ ") == false)
                .Select(button => (string)button.Tag).ToHashSet().SetEquals(new[] { "French", "English", "Chinese", "Korean" }),
                "Home shows all four active own-voice output languages as visible chips");
            settings.UseManagedTranslator = true; settings.ShareTranslationModel = true;
            settings.OwnManagedModelId = "translategemma-4b-q4_k_m";
            window.Navigate("Languages");
            Check(Picker("Spoken language · my voice").Items.Contains("English") && Picker("Spoken language · their voices").Items.Contains("Chinese"),
                "TranslateGemma exposes independent explicit source selectors even when the model is shared");
            Picker("Spoken language · my voice").SelectedItem = "English";
            Picker("Spoken language · their voices").SelectedItem = "Chinese";
            Check(settings.OwnVoiceSourceLanguage == "English" && settings.IncomingSourceLanguage == "Chinese" &&
                IncomingRecognitionCatalog.Find(settings.IncomingSpeechEngine).SupportsSource("Chinese"),
                "source selectors do not change the other direction and select compatible incoming recognition");
            settings.OwnManagedModelId = "lfm2-350m-enjp-mt-q4_k_m";
            settings.OwnVoiceTargetLanguage = "Chinese";
            settings.OwnVoiceAdditionalTargetLanguages = ["Korean"];
            window.Navigate("Languages");
            var limited = Picker("Translate my voice to");
            Check(limited.Items.Cast<string>().SequenceEqual(new[] { "English", "Japanese" }) && !Picker("Additional language 1").Items.Contains("Korean"),
                "Liquid primary and extra selectors only offer its English/Japanese language pair");
            Check(settings.OwnVoiceTargetLanguage == "Chinese" && settings.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "Korean" }),
                "model capability changes never silently switch saved translation languages");
            Check(!Picker("Spoken language · my voice").Items.Contains("Chinese"), "Liquid does not offer unsupported source languages");
            settings.OwnManagedModelId = "hy-mt2-1.8b-q4_k_m";
            window.Navigate("Languages");
            Check(Picker("Spoken language · my voice").Items.Contains("Chinese") && Picker("Spoken language · my voice").Items.Contains("Automatic") &&
                Picker("Spoken language · their voices").Items.Contains("Chinese") && Picker("Spoken language · their voices").Items.Contains("Automatic"),
                "both speech-language selectors remain available even with the default Hy translator");
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language;
                foreach (var model in LocalTalkProvisioner.ModelCatalog.Where(model => model.RequiresTermsAcceptance))
                {
                    Window consent = window.CreateModelTermsDialog(model);
                    var accept = Nodes(consent).OfType<CheckBox>().Single();
                    var confirm = Nodes(consent).OfType<Button>().Single(button => button.Content?.ToString() == UiText.T("Download"));
                    Check(!consent.IsVisible && !consent.Topmost && !confirm.IsEnabled && !confirm.IsDefault && accept.IsChecked == false,
                        "custom model downloads require unchecked explicit terms acceptance: " + language + "/" + model.Id);
                    Check(Nodes(consent).OfType<TextBlock>().Any(text => text.Text == UiText.T(model.TermsNotice)),
                        "model scope, provenance and terms are localized before download");
                    accept.IsChecked = true; Check(confirm.IsEnabled, "review acceptance enables only the download action");
                    accept.IsChecked = false; Check(!confirm.IsEnabled, "revoking acceptance disables download");
                }
            }
            return checks;
        }
        finally
        {
            // Incoming source selection now also chooses compatible recognition
            // and records Normal-mode memory. Restore the complete snapshot so
            // those new preferences cannot leak into another fixture.
            TranslationProfiles.CopyInto(settingsSnapshot, settings);
            UiText.Language = previousUi; window.Navigate(priorPage);
            File.WriteAllText(AppPaths.SettingsPath, settingsBefore);
            File.WriteAllText(AppPaths.FastConfigPath, fastBefore); File.WriteAllText(AppPaths.FoxTransConfigPath, sentenceBefore);
        }
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node) foreach (DependencyObject descendant in Nodes(node)) yield return descendant;
    }
}

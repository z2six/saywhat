using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace IncomingTranslator;

// Unshown preview controls and a separately redirected settings fixture only.
// Runtime flags are synthetic; no runtime methods, audio, network or model loads.
internal static class HomeTargetLanguageUiTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Home target languages: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo WindowField(string name) => typeof(MainWindow).GetField(name, flags)!;
        var settings = (AppSettings)WindowField("_settings").GetValue(window)!;
        var runtime = (RuntimeController)WindowField("_runtime").GetValue(window)!;
        FieldInfo ownFlag = typeof(RuntimeController).GetField("_ownRunning", flags)!;
        FieldInfo incomingFlag = typeof(RuntimeController).GetField("_incomingRunning", flags)!;
        Check((bool)WindowField("_preview").GetValue(window)! && !window.IsVisible && window.Dispatcher.CheckAccess(),
            "UI tests require the unshown preview on its UI thread");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "no real translation direction may be running during this test");
        string? originalData = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        string originalRoot = Path.GetFullPath(AppPaths.Root);
        string testRoot = Path.GetFullPath(directory);
        Check(AppPaths.IsPackagedApplication && !string.IsNullOrWhiteSpace(originalData) &&
            originalRoot == Path.GetFullPath(originalData) &&
            originalRoot.StartsWith(testRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "settings tests require the runner's redirected data directory, never live user data");
        var protectedFiles = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath }
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        AppSettings before = settings.Copy();
        string pageBefore = (string)WindowField("_page").GetValue(window)!;
        string languageBefore = UiText.Language;
        bool busyBefore = (bool)WindowField("_busy").GetValue(window)!;
        bool closingBefore = (bool)WindowField("_closing").GetValue(window)!;
        Button[] IncomingHomeChoices() => Nodes(window).OfType<Button>().Where(button => button.Name == "HomeSubtitleTargetChoice").ToArray();
        static Button[] Options(Border menu) => Nodes(menu).OfType<Button>().Where(button => button.Tag is string).ToArray();
        static string[] Targets(Border menu) => Options(menu).Select(button => (string)button.Tag).ToArray();
        void GuardState(bool busy, bool closing, bool own, bool incoming)
        {
            WindowField("_busy").SetValue(window, busy);
            WindowField("_closing").SetValue(window, closing);
            ownFlag.SetValue(runtime, own);
            incomingFlag.SetValue(runtime, incoming);
        }
        try
        {
            settings.FirstRunSetupCompleted = true;
            settings.UseManagedTranslator = settings.ShareTranslationModel = true;
            settings.OwnManagedModelId = settings.IncomingManagedModelId = "hy-mt2-1.8b-q4_k_m";
            settings.OwnVoiceSourceLanguage = settings.IncomingSourceLanguage = "Automatic";
            settings.OwnVoiceTargetLanguage = "Chinese"; settings.IncomingTargetLanguage = "English";
            settings.OwnVoiceAdditionalLanguagesEnabled = true;
            settings.OwnVoiceAdditionalTargetLanguages = ["Japanese", "Korean"];
            UiText.Language = "en";
            for (int mask = 0; mask < 16; mask++)
            {
                bool busy = (mask & 1) != 0, closing = (mask & 2) != 0;
                bool own = (mask & 4) != 0, incoming = (mask & 8) != 0;
                GuardState(busy, closing, own, incoming);
                window.Navigate("Home");
                foreach (bool direction in new[] { false, true })
                {
                    bool expected = !busy && !closing && !(direction ? incoming : own);
                    Check(MainWindow.CanChangeHomeTargetLanguage(direction, busy, closing, own, incoming) == expected,
                        "pure guard depends on only that direction plus busy/closing: " + mask + "/" + direction);
                    if (direction) Check(IncomingHomeChoices().All(button => button.IsEnabled == (!busy && !closing)),
                        "subtitle chips stage choices without interrupting a running direction: " + mask);
                    else Check(Nodes(window).OfType<WrapPanel>().Any(panel => panel.Name == "HomeTargetLanguageChoices"),
                        "own voice presents visible multi-language chips rather than the retired popup: " + mask);
                    if (!expected)
                    {
                        string ownTarget = settings.OwnVoiceTargetLanguage, incomingTarget = settings.IncomingTargetLanguage;
                        Check(!window.SelectHomeTargetLanguage(direction, "Japanese") && settings.OwnVoiceTargetLanguage == ownTarget &&
                            settings.IncomingTargetLanguage == incomingTarget,
                            "selection rechecks a running/busy/closing guard rather than trusting an old menu");
                    }
                }
            }
            GuardState(false, false, false, false);
            window.Navigate("Home");
            WrapPanel ownTargets = Nodes(window).OfType<WrapPanel>().Single(panel => panel.Name == "HomeTargetLanguageChoices");
            Check(ownTargets.Children.OfType<Button>().Where(button => button.Content?.ToString()?.StartsWith("1 · ") == true ||
                button.Content?.ToString()?.StartsWith("2 · ") == true || button.Content?.ToString()?.StartsWith("3 · ") == true)
                .Select(button => (string)button.Tag).SequenceEqual(new[] { "Chinese", "Japanese", "Korean" }),
                "own Home chips show every enabled language with its send order");
            Check(Targets(window.CreateHomeTargetLanguageMenu(false)).SequenceEqual(AppSettings.TranslationLanguages) &&
                Targets(window.CreateHomeTargetLanguageMenu(true)).SequenceEqual(AppSettings.TranslationLanguages),
                "general translator menus offer the supported canonical language catalog");
            Check(window.SelectHomeTargetLanguage(false, " ja ") && settings.OwnVoiceTargetLanguage == "Japanese" &&
                settings.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "Korean" }) && settings.IncomingTargetLanguage == "English",
                "preview canonicalizes the own alias and removes only its duplicate extra");
            Check(window.SelectHomeTargetLanguage(true, "zh-CN") && settings.IncomingTargetLanguage == "Chinese" &&
                settings.OwnVoiceTargetLanguage == "Japanese", "incoming alias changes only the incoming direction");
            string validPreview = settings.IncomingTargetLanguage;
            Check(!window.SelectHomeTargetLanguage(true, "Unsupported language") && settings.IncomingTargetLanguage == validPreview,
                "unsupported preview language is rejected rather than added to settings");
            foreach (string ui in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = ui; window.Navigate("Home");
                Check(Nodes(window).OfType<WrapPanel>().Single(panel => panel.Name == "HomeTargetLanguageChoices").Children.OfType<Button>()
                    .All(button => AutomationProperties.GetName(button).StartsWith(UiText.T("Translate my voice to") + ": ", StringComparison.Ordinal)) &&
                    IncomingHomeChoices().All(button => AutomationProperties.GetName(button).StartsWith(UiText.T("Translate their voices to") + ": ", StringComparison.Ordinal)),
                    "visible own and subtitle chips follow the selected interface language: " + ui);
                Border menu = window.CreateHomeTargetLanguageMenu(true);
                Check(menu.CornerRadius.TopLeft > 0 && menu.Background is not null &&
                    Nodes(menu).OfType<ScrollViewer>().Single().MaxHeight == 280 &&
                    Options(menu).All(option => option.Content?.ToString() == UiText.T((string)option.Tag)),
                    "small themed scrollable choices are localized without changing canonical tags: " + ui);
            }
            UiText.Language = "en";
            settings.OwnManagedModelId = "lfm2-350m-enjp-mt-q4_k_m";
            settings.OwnVoiceSourceLanguage = "English";
            settings.IncomingSourceLanguage = "Japanese";
            settings.OwnVoiceTargetLanguage = "Chinese"; // Invalid old choice must not silently change on render.
            settings.ShareTranslationModel = true;
            Check(Targets(window.CreateHomeTargetLanguageMenu(false)).SequenceEqual(new[] { "English", "Japanese" }) &&
                Targets(window.CreateHomeTargetLanguageMenu(true)).SequenceEqual(new[] { "English", "Japanese" }) && settings.OwnVoiceTargetLanguage == "Chinese",
                "shared Liquid restricts both menus without silently replacing old language choices");
            Check(!window.SelectHomeTargetLanguage(false, "Chinese") && settings.OwnVoiceTargetLanguage == "Chinese",
                "Liquid cannot accept an unsupported own target even through a direct callback");
            settings.OwnVoiceAdditionalLanguagesEnabled = false;
            Check(window.SelectHomeTargetLanguage(false, "ja") && settings.OwnVoiceTargetLanguage == "Japanese",
                "Liquid preview accepts a supported canonicalized target");
            settings.ShareTranslationModel = false;
            Check(Targets(window.CreateHomeTargetLanguageMenu(true)).SequenceEqual(AppSettings.TranslationLanguages),
                "separate incoming Hy model does not inherit the own Liquid restriction");
            settings.UseManagedTranslator = false;
            settings.OwnTranslatorModel = "liquidai/lfm2-350m-enjp-mt";
            settings.TranslatorModel = "tencent/hy-mt2-1.8b";
            Check(Targets(window.CreateHomeTargetLanguageMenu(false)).SequenceEqual(new[] { "English", "Japanese" }) &&
                Targets(window.CreateHomeTargetLanguageMenu(true)).SequenceEqual(AppSettings.TranslationLanguages),
                "external aliases use the same per-direction model capabilities without health probes");
            foreach (var file in protectedFiles)
                Check(file.Value is null ? !File.Exists(file.Key) : File.ReadAllBytes(file.Key).SequenceEqual(file.Value),
                    "preview navigation and quick choices do not write saved preferences or capture configs");

            GuardState(false, false, false, false);
            Environment.SetEnvironmentVariable(AppBrand.DataEnvironmentVariable, Path.Combine(testRoot, "home-target-fixtures"));
            checks += RunStoreChecks(AppPaths.Root);
            Check(!runtime.OwnRunning && !runtime.IncomingRunning,
                "real targeted persistence leaves both inert runtimes stopped");
            return checks;
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppBrand.DataEnvironmentVariable, originalData);
            GuardState(busyBefore, closingBefore, false, false);
            TranslationProfiles.CopyInto(before, settings);
            UiText.Language = languageBefore;
            window.Navigate(pageBefore);
        }
    }

    private static int RunStoreChecks(string directory)
    {
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Home target persistence: " + detail); checks++; }
        JsonObject Read() => JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
        void Write(JsonObject document) => File.WriteAllText(AppPaths.SettingsPath, document.ToJsonString());
        JsonObject Baseline()
        {
            JsonObject document = JsonSerializer.SerializeToNode(new AppSettings
            {
                UseManagedTranslator = true, ShareTranslationModel = true, OwnManagedModelId = "hy-mt2-1.8b-q4_k_m",
                IncomingManagedModelId = "lfm2-350m-enjp-mt-q4_k_m", IncomingTargetLanguage = "English", OwnVoiceTargetLanguage = "Chinese",
                OwnVoiceAdditionalLanguagesEnabled = false, OwnVoiceAdditionalTargetLanguages = ["ja", "zh-CN", "Korean", "legacy-unknown"],
                ActiveTranslationProfileId = "custom-preserved", CustomTranslationProfiles = [new TranslationProfile
                {
                    Id = "custom-preserved", Name = "Preserved custom", Snapshot = new JsonObject { [nameof(AppSettings.OwnVoiceTargetLanguage)] = "Korean" }
                }],
                UiThemeId = "custom", CustomThemeBackground = "#234567", OwnVoiceEndPauseMilliseconds = 1800,
                IncomingSettleMilliseconds = 775, SpeakerMatchThreshold = .67, SubtitleGuidePromptSeen = true
            })!.AsObject();
            document["FuturePreference"] = new JsonObject { ["mustRemain"] = new JsonArray("one", "two") };
            Write(document);
            return document;
        }
        void Reject<T>(Action action, string detail) where T : Exception
        {
            byte[] before = File.ReadAllBytes(AppPaths.SettingsPath);
            bool rejected = false;
            try { action(); } catch (T) { rejected = true; }
            Check(rejected && File.ReadAllBytes(AppPaths.SettingsPath).SequenceEqual(before), detail + "; no saved-file mutation");
        }
        void RejectAtomicWrite(Action action, string detail)
        {
            byte[] before = File.ReadAllBytes(AppPaths.SettingsPath);
            bool rejected = false;
            try { action(); }
            // Windows overwrite can report a held destination as either a sharing
            // violation or access denied. Neither permits bypassing the lock.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(AppPaths.SettingsPath).SequenceEqual(before), detail + "; no saved-file mutation");
        }
        byte[] fast = System.Text.Encoding.UTF8.GetBytes("synthetic capture config: do not rewrite"), sentence = [0, 1, 2, 3, 4];
        File.WriteAllBytes(AppPaths.FastConfigPath, fast);
        File.WriteAllBytes(AppPaths.FoxTransConfigPath, sentence);
        JsonObject baseline = Baseline();
        AppSettings incoming = SettingsStore.SaveHomeTargetLanguagePreference(true, " ko ");
        JsonObject expected = (JsonObject)baseline.DeepClone(); expected[nameof(AppSettings.IncomingTargetLanguage)] = "Korean";
        Check(incoming.IncomingTargetLanguage == "Korean" && JsonNode.DeepEquals(Read(), expected),
            "incoming canonical alias patches one key and preserves all raw unrelated settings/profile snapshots");
        using (var held = new FileStream(AppPaths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] sameBefore = File.ReadAllBytes(AppPaths.SettingsPath);
            AppSettings same = SettingsStore.SaveHomeTargetLanguagePreference(true, "Korean");
            Check(same.IncomingTargetLanguage == "Korean" && File.ReadAllBytes(AppPaths.SettingsPath).SequenceEqual(sameBefore),
                "same canonical value does not attempt an atomic replace while settings are held read-only");
        }
        AppSettings own = SettingsStore.SaveHomeTargetLanguagePreference(false, "JA");
        expected[nameof(AppSettings.OwnVoiceTargetLanguage)] = "Japanese";
        expected[nameof(AppSettings.OwnVoiceAdditionalTargetLanguages)] = new JsonArray("zh-CN", "Korean", "legacy-unknown");
        Check(own.OwnVoiceTargetLanguage == "Japanese" && own.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "zh-CN", "Korean", "legacy-unknown" }) &&
            JsonNode.DeepEquals(Read(), expected), "own choice removes only a matching primary extra, preserving disabled raw extra choices");
        SettingsStore.SaveSubtitlePinPreference(false);
        SettingsStore.SaveUiLanguagePreference("ja");
        SettingsStore.SaveHomeTargetLanguagePreference(false, "zh-CN");
        expected[nameof(AppSettings.OwnVoiceTargetLanguage)] = "Chinese";
        expected[nameof(AppSettings.OwnVoiceAdditionalTargetLanguages)] = new JsonArray("Korean", "legacy-unknown");
        expected[nameof(AppSettings.SubtitlesAlwaysOnTop)] = false; expected[nameof(AppSettings.UiLanguage)] = "ja";
        Check(JsonNode.DeepEquals(Read(), expected), "latest independently saved pin/language preferences survive a targeted update");
        Task.WaitAll(Task.Run(() => SettingsStore.SaveHomeTargetLanguagePreference(true, "French")),
            Task.Run(() => SettingsStore.SaveSubtitlePinPreference(true)));
        expected[nameof(AppSettings.IncomingTargetLanguage)] = "French"; expected[nameof(AppSettings.SubtitlesAlwaysOnTop)] = true;
        Check(JsonNode.DeepEquals(Read(), expected), "concurrent targeted controls serialize without losing either preference or unrelated raw data");
        Reject<ModelSelectionUserException>(() => SettingsStore.SaveHomeTargetLanguagePreference(false, "invalid"), "unknown target is rejected");

        JsonObject liquid = Baseline();
        liquid[nameof(AppSettings.OwnManagedModelId)] = "lfm2-350m-enjp-mt-q4_k_m";
        liquid[nameof(AppSettings.OwnVoiceSourceLanguage)] = "English";
        liquid[nameof(AppSettings.IncomingSourceLanguage)] = "Japanese";
        Write(liquid);
        Reject<ModelSelectionUserException>(() => SettingsStore.SaveHomeTargetLanguagePreference(false, "Chinese"), "Liquid refuses a Chinese own target");
        Reject<ModelSelectionUserException>(() => SettingsStore.SaveHomeTargetLanguagePreference(true, "Korean"), "shared Liquid refuses an unsupported incoming target");
        AppSettings disabledExtras = SettingsStore.SaveHomeTargetLanguagePreference(false, "Japanese");
        Check(disabledExtras.OwnVoiceTargetLanguage == "Japanese" && disabledExtras.OwnVoiceAdditionalTargetLanguages.Contains("Korean"),
            "disabled unsupported extras remain saved without constraining the active primary pair");
        liquid = Read(); liquid[nameof(AppSettings.OwnVoiceAdditionalLanguagesEnabled)] = true; Write(liquid);
        Reject<ModelSelectionUserException>(() => SettingsStore.SaveHomeTargetLanguagePreference(false, "English"), "enabled unsupported Liquid extras are rejected, not erased");
        liquid[nameof(AppSettings.OwnVoiceAdditionalLanguagesEnabled)] = false;
        liquid[nameof(AppSettings.OwnVoiceSourceLanguage)] = "Automatic"; Write(liquid);
        Reject<ModelSelectionUserException>(() => SettingsStore.SaveHomeTargetLanguagePreference(false, "Japanese"), "specialized translator requires an explicit persisted source");
        liquid[nameof(AppSettings.OwnVoiceSourceLanguage)] = "English";
        liquid[nameof(AppSettings.ShareTranslationModel)] = false;
        liquid[nameof(AppSettings.IncomingManagedModelId)] = "hy-mt2-1.8b-q4_k_m";
        Write(liquid);
        Check(SettingsStore.SaveHomeTargetLanguagePreference(true, "Chinese").IncomingTargetLanguage == "Chinese",
            "separate incoming model—not the own Liquid choice—governs target validation");
        liquid = Read(); liquid[nameof(AppSettings.OwnManagedModelId)] = "missing-model"; Write(liquid);
        Reject<ModelSelectionUserException>(() => SettingsStore.SaveHomeTargetLanguagePreference(false, "Japanese"), "unknown managed catalog ID fails clearly without installing a model");
        liquid[nameof(AppSettings.UseManagedTranslator)] = false;
        liquid[nameof(AppSettings.OwnTranslatorModel)] = "google/translategemma-4b-it"; Write(liquid);
        Reject<ModelSelectionUserException>(() => SettingsStore.SaveHomeTargetLanguagePreference(false, "Japanese"), "external TranslateGemma cannot bypass managed-template requirements");
        liquid[nameof(AppSettings.OwnTranslatorModel)] = "liquidai/lfm2-350m-enjp-mt"; Write(liquid);
        Reject<ModelSelectionUserException>(() => SettingsStore.SaveHomeTargetLanguagePreference(false, "Chinese"), "external Liquid alias retains language restrictions");

        Baseline();
        using (var held = new FileStream(AppPaths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            RejectAtomicWrite(() => SettingsStore.SaveHomeTargetLanguagePreference(true, "Japanese"), "failed atomic replacement preserves the old preferences");
        File.WriteAllText(AppPaths.SettingsPath, "{corrupt");
        Reject<JsonException>(() => SettingsStore.SaveHomeTargetLanguagePreference(true, "Japanese"), "corrupt saved settings are not replaced with defaults");
        File.WriteAllText(AppPaths.SettingsPath, "[]");
        Reject<InvalidDataException>(() => SettingsStore.SaveHomeTargetLanguagePreference(true, "Japanese"), "non-object saved settings fail closed");
        Check(File.ReadAllBytes(AppPaths.FastConfigPath).SequenceEqual(fast) && File.ReadAllBytes(AppPaths.FoxTransConfigPath).SequenceEqual(sentence),
            "targeted updates never create or rewrite live generated capture-host configuration");
        Check(!Directory.EnumerateFiles(directory, "*.tmp-*", SearchOption.TopDirectoryOnly).Any(), "successful or failed atomic operations clean only their temporary files");
        return checks;
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node)
                foreach (DependencyObject descendant in Nodes(node)) yield return descendant;
    }
}

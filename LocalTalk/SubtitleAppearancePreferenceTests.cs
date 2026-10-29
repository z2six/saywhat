using System.Text.Json.Nodes;

namespace IncomingTranslator;

// File-only fixtures in DesktopTests' isolated data root. No window, capture or model starts.
internal static class SubtitleAppearancePreferenceTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Subtitle appearance: " + reason);
            checks++;
        }

        string? previousRoot = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(AppPaths.IsPackagedApplication && !string.IsNullOrWhiteSpace(previousRoot), "tests require the isolated packaged-app data root");
        string fixtureRoot = Path.Combine(AppPaths.Root, "subtitle-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        Environment.SetEnvironmentVariable(AppBrand.DataEnvironmentVariable, fixtureRoot);
        try
        {
            void CheckDefaults(AppSettings settings, string context)
            {
                Check(settings.SubtitlesVisibleMessages == 3 && settings.SubtitlesFontSize == 16 && settings.SubtitlesBubbleWidth == 420 &&
                    settings.SubtitlesBackgroundOpacity == .65 && settings.SubtitlesShowOriginalText,
                    context + " uses readable first-run defaults");
            }

            CheckDefaults(new AppSettings(), "new settings");
            CheckDefaults(SettingsStore.Load(), "missing settings");
            CheckDefaults(SettingsStore.ReadSettings("""{"SubtitlesAlwaysOnTop":false,"OwnVoiceTargetLanguage":"Chinese"}"""), "legacy settings");
            Check(!SettingsStore.ReadSettings("""{"SubtitlesAlwaysOnTop":false}""").SubtitlesAlwaysOnTop,
                "migrating appearance does not change the pin preference");
            Check(SettingsStore.ReadSettings("""{"OverlayMode":"Latest sentence"}""").SubtitlesVisibleMessages == 1,
                "an old one-message layout migrates to one only when no explicit visible count exists");
            Check(SettingsStore.ReadSettings("""{"OverlayMode":"Latest sentence","SubtitlesVisibleMessages":5}""").SubtitlesVisibleMessages == 5,
                "an explicit count overrides a legacy one-message layout");
            Check(SettingsStore.ReadSettings("""{"OverlayMode":"Per-speaker history"}""").SubtitlesVisibleMessages == 3,
                "other legacy layouts use the three-message factory count");
            foreach ((int value, int expected) in new[] { (int.MinValue, 1), (0, 1), (1, 1), (5, 5), (10, 10), (11, 10), (int.MaxValue, 10) })
            {
                var visible = new AppSettings { SubtitlesVisibleMessages = value };
                SettingsStore.NormalizeSubtitleAppearance(visible);
                Check(visible.SubtitlesVisibleMessages == expected, "visible message count is bounded to one through ten");
            }

            foreach ((double font, double width, double opacity, double expectedFont, double expectedWidth, double expectedOpacity) in new[]
            {
                (11d, 239d, -.1d, 12d, 240d, 0d),
                (31d, 901d, 1.1d, 30d, 900d, 1d),
                (21.5d, 621.5d, .35d, 21.5d, 621.5d, .35d),
                (double.NaN, double.PositiveInfinity, double.NegativeInfinity, 16d, 420d, .65d),
                (double.PositiveInfinity, double.NegativeInfinity, double.NaN, 16d, 420d, .65d)
            })
            {
                var bounded = new AppSettings
                {
                    SubtitlesFontSize = font, SubtitlesBubbleWidth = width,
                    SubtitlesBackgroundOpacity = opacity, SubtitlesShowOriginalText = false
                };
                SettingsStore.NormalizeSubtitleAppearance(bounded);
                Check(bounded.SubtitlesFontSize == expectedFont && bounded.SubtitlesBubbleWidth == expectedWidth &&
                    bounded.SubtitlesBackgroundOpacity == expectedOpacity && !bounded.SubtitlesShowOriginalText,
                    "normalization clamps numeric values and preserves the original-text choice");
            }

            AppSettings readClamped = SettingsStore.ReadSettings("""{"SubtitlesVisibleMessages":15,"SubtitlesFontSize":2,"SubtitlesBubbleWidth":2000,"SubtitlesBackgroundOpacity":-2,"SubtitlesShowOriginalText":false}""");
            Check(readClamped.SubtitlesVisibleMessages == 10 && readClamped.SubtitlesFontSize == 12 && readClamped.SubtitlesBubbleWidth == 900 &&
                readClamped.SubtitlesBackgroundOpacity == 0 && !readClamped.SubtitlesShowOriginalText,
                "out-of-range saved appearance is normalized while loading");

            var baseline = new AppSettings
            {
                OwnVoiceTargetLanguage = "Chinese", IncomingTargetLanguage = "English",
                OwnManagedModelId = "hy-mt2-7b-q4_k_m", SubtitlesAlwaysOnTop = false,
                Glossary = "Synthetic appearance fixture", UiThemeId = "coral-night"
            };
            SettingsStore.Save(baseline);
            string microphoneBefore = File.ReadAllText(AppPaths.FastConfigPath);
            string sentenceBefore = File.ReadAllText(AppPaths.FoxTransConfigPath);
            JsonObject before = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
            before["FuturePreference"] = new JsonObject { ["keep"] = true };
            File.WriteAllText(AppPaths.SettingsPath, before.ToJsonString());

            AppSettings overlay = baseline.Copy();
            overlay.SubtitlesVisibleMessages = 5;
            overlay.SubtitlesFontSize = 24;
            overlay.SubtitlesBubbleWidth = 650;
            overlay.SubtitlesBackgroundOpacity = .8;
            overlay.SubtitlesShowOriginalText = false;
            // Deliberately stale unrelated values: this save may touch only the five appearance keys.
            overlay.SubtitlesAlwaysOnTop = true;
            overlay.OwnVoiceTargetLanguage = "Japanese";
            overlay.IncomingTargetLanguage = "Korean";
            overlay.OwnManagedModelId = "hy-mt2-1.8b-q4_k_m";
            overlay.UiThemeId = "custom";
            using (File.Open(AppPaths.FastConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (File.Open(AppPaths.FoxTransConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                SettingsStore.SaveSubtitleAppearancePreference(overlay);

            var appearanceKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(AppSettings.SubtitlesVisibleMessages),
                nameof(AppSettings.SubtitlesFontSize), nameof(AppSettings.SubtitlesBubbleWidth),
                nameof(AppSettings.SubtitlesBackgroundOpacity), nameof(AppSettings.SubtitlesShowOriginalText)
            };
            JsonObject after = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
            Check(before.Count == after.Count, "scoped save neither removes nor adds unrelated preferences");
            foreach (var preference in before)
                if (!appearanceKeys.Contains(preference.Key))
                    Check(JsonNode.DeepEquals(preference.Value, after[preference.Key]), "scoped save preserves " + preference.Key);

            AppSettings loaded = SettingsStore.Load();
            Check(loaded.SubtitlesVisibleMessages == 5 && loaded.SubtitlesFontSize == 24 && loaded.SubtitlesBubbleWidth == 650 &&
                loaded.SubtitlesBackgroundOpacity == .8 && !loaded.SubtitlesShowOriginalText,
                "all five appearance values persist across loading");
            AppSettings copy = loaded.Copy();
            Check(copy.SubtitlesVisibleMessages == 5 && copy.SubtitlesFontSize == 24 && copy.SubtitlesBubbleWidth == 650 &&
                copy.SubtitlesBackgroundOpacity == .8 && !copy.SubtitlesShowOriginalText,
                "settings copies retain all appearance values");
            Check(!loaded.SubtitlesAlwaysOnTop, "appearance saving preserves the independently saved pin preference");
            Check(File.ReadAllText(AppPaths.FastConfigPath) == microphoneBefore && File.ReadAllText(AppPaths.FoxTransConfigPath) == sentenceBefore,
                "appearance saving never rewrites microphone configuration");

            AppSettings staleDashboard = baseline.Copy();
            SettingsStore.Save(staleDashboard);
            AppSettings preserved = SettingsStore.Load();
            Check(preserved.SubtitlesVisibleMessages == 5 && preserved.SubtitlesFontSize == 24 && preserved.SubtitlesBubbleWidth == 650 &&
                preserved.SubtitlesBackgroundOpacity == .8 && !preserved.SubtitlesShowOriginalText,
                "a stale dashboard save cannot undo live subtitle appearance");
            Check(staleDashboard.SubtitlesVisibleMessages == 5 && staleDashboard.SubtitlesFontSize == 24 && staleDashboard.SubtitlesBubbleWidth == 650,
                "the saved dashboard snapshot reflects the preserved appearance");

            overlay.SubtitlesVisibleMessages = 99;
            overlay.SubtitlesFontSize = double.NaN;
            overlay.SubtitlesBubbleWidth = 10;
            overlay.SubtitlesBackgroundOpacity = 5;
            SettingsStore.SaveSubtitleAppearancePreference(overlay);
            loaded = SettingsStore.Load();
            Check(loaded.SubtitlesVisibleMessages == 10 && loaded.SubtitlesFontSize == 16 && loaded.SubtitlesBubbleWidth == 240 && loaded.SubtitlesBackgroundOpacity == 1,
                "scoped saves normalize a detached copy before serialization");
            Check(overlay.SubtitlesVisibleMessages == 99 && double.IsNaN(overlay.SubtitlesFontSize) && overlay.SubtitlesBubbleWidth == 10 && overlay.SubtitlesBackgroundOpacity == 5,
                "normalizing a scoped save does not mutate its caller");

            string settingsBeforeFailure = File.ReadAllText(AppPaths.SettingsPath);
            bool denied = false;
            using (File.Open(AppPaths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                try { SettingsStore.SaveSubtitleAppearancePreference(baseline); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { denied = true; }
            Check(denied && File.ReadAllText(AppPaths.SettingsPath) == settingsBeforeFailure,
                "an atomic-save failure leaves the previous appearance intact");
            Check(!Directory.EnumerateFiles(AppPaths.Root, "vrchat-translation-settings.json.tmp-*").Any(),
                "failed scoped saves leave no temporary files");
            var raceAppearance = baseline.Copy();
            raceAppearance.SubtitlesVisibleMessages = 9; raceAppearance.SubtitlesFontSize = 22;
            raceAppearance.SubtitlesBubbleWidth = 520; raceAppearance.SubtitlesBackgroundOpacity = .55;
            raceAppearance.SubtitlesShowOriginalText = true;
            JsonObject raceDocument = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
            raceDocument["FutureRacePreference"] = "keep";
            File.WriteAllText(AppPaths.SettingsPath, raceDocument.ToJsonString());
            Task.WhenAll(
                Task.Run(() => SettingsStore.SaveSubtitleAppearancePreference(raceAppearance)),
                Task.Run(() => SettingsStore.SaveSubtitlePinPreference(true)),
                Task.Run(() => SettingsStore.SaveUiLanguagePreference("ko"))).GetAwaiter().GetResult();
            AppSettings raced = SettingsStore.Load();
            Check(raced.SubtitlesVisibleMessages == 9 && raced.SubtitlesFontSize == 22 && raced.SubtitlesBubbleWidth == 520 &&
                raced.SubtitlesBackgroundOpacity == .55 && raced.SubtitlesShowOriginalText && raced.SubtitlesAlwaysOnTop && raced.UiLanguage == "ko",
                "concurrent scoped saves retain all five appearance fields plus unrelated live pin and language choices");
            Check(JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!["FutureRacePreference"]!.GetValue<string>() == "keep" &&
                File.ReadAllText(AppPaths.FastConfigPath) == microphoneBefore && File.ReadAllText(AppPaths.FoxTransConfigPath) == sentenceBefore,
                "racing targeted saves preserve unknown data and never rewrite microphone configuration");
            CheckProfileOwnership(Check);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppBrand.DataEnvironmentVariable, previousRoot);
        }
        return checks;
    }

    private static void CheckProfileOwnership(Action<bool, string> check)
    {
        var original = new AppSettings
        {
            SubtitlesVisibleMessages = 2,
            SubtitlesFontSize = 18, SubtitlesBubbleWidth = 500,
            SubtitlesBackgroundOpacity = .45, SubtitlesShowOriginalText = false,
            UiThemeId = "paper", SubtitlesAlwaysOnTop = false, Glossary = "Saved profile fixture"
        };
        AppSettings savedProfile = TranslationProfiles.Create(original, "Appearance fixture");
        string customId = savedProfile.ActiveTranslationProfileId;
        SettingsStore.SaveTranslationProfileSelection(savedProfile);
        AppSettings dashboard = savedProfile.Copy(), appliedBaseline = savedProfile.Copy();
        AppSettings overlay = savedProfile.Copy();
        overlay.SubtitlesVisibleMessages = 7;
        overlay.SubtitlesFontSize = 26; overlay.SubtitlesBubbleWidth = 700;
        overlay.SubtitlesBackgroundOpacity = .25; overlay.SubtitlesShowOriginalText = true;
        SettingsStore.SaveSubtitleAppearancePreference(overlay);
        AppSettings latest = SettingsStore.Load();
        bool HasLiveAppearance(AppSettings settings) => settings.SubtitlesVisibleMessages == 7 && settings.SubtitlesFontSize == 26 && settings.SubtitlesBubbleWidth == 700 &&
            settings.SubtitlesBackgroundOpacity == .25 && settings.SubtitlesShowOriginalText;

        AppSettings rebased = SettingsSaveState.Rebase(dashboard, appliedBaseline, latest);
        check(HasLiveAppearance(rebased) && !SettingsSaveState.HasChanges(rebased, latest),
            "external-only overlay autosaves rebase to a clean ordinary Save all state");
        AppSettings unrelatedDraft = dashboard.Copy(); unrelatedDraft.OwnVoiceTargetLanguage = "Japanese";
        AppSettings rebasedDraft = SettingsSaveState.Rebase(unrelatedDraft, appliedBaseline, latest);
        check(HasLiveAppearance(rebasedDraft) && rebasedDraft.OwnVoiceTargetLanguage == "Japanese" && SettingsSaveState.HasChanges(rebasedDraft, latest),
            "external appearance changes keep a genuine unrelated draft pending");

        SettingsStore.CopySubtitleAppearance(latest, dashboard);
        SettingsStore.CopySubtitleAppearance(latest, appliedBaseline);
        check(HasLiveAppearance(dashboard) && HasLiveAppearance(appliedBaseline) &&
            !SettingsSaveState.HasChanges(dashboard, appliedBaseline) && TranslationProfiles.ConfigurationEquals(dashboard, latest),
            "profile-selection rebasing merges the five live values into both snapshots without a false unsaved prompt");

        var scope = new AppSettings
        {
            UiThemeId = "coral-night", SubtitlesAlwaysOnTop = true, Glossary = "Keep pending text",
            OwnVoiceTargetLanguage = "Korean", OwnVoiceSendEarlyPreviews = true, IncomingSendEarlyPreviews = true
        };
        SettingsStore.CopySubtitleAppearance(latest, scope);
        check(HasLiveAppearance(scope) && scope.UiThemeId == "coral-night" && scope.SubtitlesAlwaysOnTop &&
            scope.Glossary == "Keep pending text" && scope.OwnVoiceTargetLanguage == "Korean" &&
            scope.OwnVoiceSendEarlyPreviews && scope.IncomingSendEarlyPreviews,
            "the five-value copy does not rebase theme, pin, translation or delivery preferences");

        foreach (string id in new[] { TranslationProfiles.QuickId, TranslationProfiles.NormalId, TranslationProfiles.GroupsId })
        {
            AppSettings candidate = TranslationProfiles.Select(dashboard, id);
            check(HasLiveAppearance(candidate), "a " + id + " selection inherits the overlay's latest saved appearance");
            SettingsStore.SaveTranslationProfileSelection(candidate);
            check(HasLiveAppearance(SettingsStore.Load()), "applying " + id + " does not overwrite live appearance with an old dashboard snapshot");
        }

        AppSettings staleOrdinarySave = savedProfile.Copy();
        SettingsStore.Save(staleOrdinarySave);
        check(HasLiveAppearance(staleOrdinarySave) && HasLiveAppearance(SettingsStore.Load()),
            "an ordinary Save all still merges the latest appearance instead of restoring stale values");

        dashboard.Glossary = "A real unsaved glossary";
        SettingsStore.CopySubtitleAppearance(latest, dashboard);
        SettingsStore.CopySubtitleAppearance(latest, appliedBaseline);
        check(dashboard.Glossary == "A real unsaved glossary" && SettingsSaveState.HasChanges(dashboard, appliedBaseline),
            "profile rebasing does not erase a real draft that still needs a replacement prompt");

        AppSettings explicitCustom = TranslationProfiles.Select(dashboard, customId);
        check(explicitCustom.SubtitlesVisibleMessages == 2 && explicitCustom.SubtitlesFontSize == 18 && explicitCustom.SubtitlesBubbleWidth == 500 &&
            explicitCustom.SubtitlesBackgroundOpacity == .45 && !explicitCustom.SubtitlesShowOriginalText,
            "an explicitly chosen custom profile can restore its own saved appearance after rebasing");
        SettingsStore.SaveTranslationProfileSelection(explicitCustom);
        AppSettings restored = SettingsStore.Load();
        check(restored.SubtitlesVisibleMessages == 2 && restored.SubtitlesFontSize == 18 && restored.SubtitlesBubbleWidth == 500 &&
            restored.SubtitlesBackgroundOpacity == .45 && !restored.SubtitlesShowOriginalText &&
            restored.CustomTranslationProfiles.Single().Id == customId,
            "explicit custom appearance persists without losing the profile library");
    }
}

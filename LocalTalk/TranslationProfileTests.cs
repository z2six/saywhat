using System.Text.Json;
using System.Text.Json.Nodes;

namespace IncomingTranslator;

/// <summary>Pure settings fixtures: no audio, model loading, network or installed settings.</summary>
internal static class TranslationProfileTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Translation profile check failed: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException) { rejected = true; }
            Check(rejected, message);
        }

        var initial = new AppSettings
        {
            OwnManagedModelId = "hy-mt2-7b-q4_k_m", IncomingManagedModelId = "translategemma-4b-q4_k_m",
            OwnTranslatorModel = "custom-own-model", TranslatorModel = "custom-incoming-model",
            OwnVoiceTargetLanguage = "Chinese", IncomingTargetLanguage = "Korean", UiLanguage = "ja",
            UiThemeId = "custom", CustomThemeBackground = "#123456", CustomThemeTextPalette = "light",
            CustomThemeImagePath = @"C:\example\wallpaper.png", CustomThemeImageOpacity = .25,
            OwnVoiceAdditionalLanguagesEnabled = true, OwnVoiceAdditionalTargetLanguages = ["English", "Japanese"],
            Glossary = "Ringo = リンゴ", FirstRunSetupCompleted = true, FirstRunIntroductionCompleted = true,
            SubtitleGuidePromptSeen = true, CheckForUpdatesOnLaunch = false, SubtitlesAlwaysOnTop = false,
            CloseButtonBehavior = "Tray", SpeakerMatchThreshold = .63
        };
        AppSettings quick = TranslationProfiles.Select(initial, TranslationProfiles.QuickId);
        Check(quick is { IncomingSettleMilliseconds: 800, SubtitlePreviewStableMilliseconds: 250,
            RecognitionDelayMilliseconds: 160, UseAiSpeakerRecognition: false, IncomingSpeechEngine: "voxtral",
            OwnVoiceEndPauseMilliseconds: 1200, TranslationQuality: "Fast" }, "Quick applies actual speed settings without cutting the requested own-voice pause");
        Check(quick.OwnManagedModelId == initial.OwnManagedModelId && quick.IncomingManagedModelId == initial.IncomingManagedModelId &&
            quick.OwnTranslatorModel == initial.OwnTranslatorModel && quick.TranslatorModel == initial.TranslatorModel,
            "built-ins preserve explicitly selected translation models");
        Check(quick.OwnVoiceTargetLanguage == "Chinese" && quick.IncomingTargetLanguage == "Korean" &&
            quick.UiLanguage == "ja" && quick.CustomThemeBackground == "#123456" && quick.Glossary == initial.Glossary &&
            !quick.CheckForUpdatesOnLaunch && quick.CloseButtonBehavior == "Tray", "built-ins preserve languages, appearance and user preferences");
        Check(initial.UseAiSpeakerRecognition && initial.RecognitionDelayMilliseconds == 240,
            "selecting a profile never mutates its source snapshot");
        Check(!TranslationProfiles.IsModified(quick), "a newly applied Quick profile is not marked modified");
        quick.TranslatorModel = "another-deliberately-selected-model";
        Check(!TranslationProfiles.IsModified(quick), "changing models does not count as changing an operational template");
        quick.IncomingSettleMilliseconds = 900;
        Check(TranslationProfiles.IsModified(quick), "changing a template's speed setting is detected");
        AppSettings normal = TranslationProfiles.Select(quick, TranslationProfiles.NormalId);
        Check(normal is { IncomingSettleMilliseconds: 1200, SubtitlePreviewStableMilliseconds: 500,
            RecognitionDelayMilliseconds: 240, UseAiSpeakerRecognition: true, IncomingSpeechEngine: "voxtral" },
            "Normal restores familiar-person recognition and current timings");
        quick.IncomingSettleMilliseconds = 800;
        AppSettings groups = TranslationProfiles.Select(quick, TranslationProfiles.GroupsId);
        Check(groups.IncomingSpeechEngine == "vibevoice" && !groups.UseAiSpeakerRecognition && groups.IncomingSettleMilliseconds == 800 &&
            groups.OwnManagedModelId == quick.OwnManagedModelId, "Groups selects its speech engine without forcing fingerprinting or replacing user settings");
        Check(!TranslationProfiles.IsModified(groups), "optional fingerprint settings do not mark Groups modified");

        AppSettings saved = TranslationProfiles.Create(initial, "  Private   worlds  ");
        string savedId = saved.ActiveTranslationProfileId;
        TranslationProfile profile = saved.CustomTranslationProfiles.Single();
        Check(profile.Name == "Private worlds" && savedId.StartsWith("custom-", StringComparison.Ordinal) &&
            !TranslationProfiles.IsModified(saved), "custom profiles have stable IDs, normalized names and full snapshots");
        Check(!profile.Snapshot.ContainsKey(nameof(AppSettings.CustomTranslationProfiles)) &&
            !profile.Snapshot.ContainsKey(nameof(AppSettings.ActiveTranslationProfileId)) &&
            !profile.Snapshot.ContainsKey(nameof(AppSettings.FirstRunSetupCompleted)) &&
            !profile.Snapshot.ContainsKey(nameof(AppSettings.FirstRunIntroductionCompleted)) &&
            !profile.Snapshot.ContainsKey(nameof(AppSettings.SubtitleGuidePromptSeen)), "snapshots exclude recursive metadata and onboarding acknowledgements");
        Check(profile.Snapshot.ContainsKey(nameof(AppSettings.CheckForUpdatesOnLaunch)) &&
            profile.Snapshot.ContainsKey(nameof(AppSettings.CloseButtonBehavior)) &&
            profile.Snapshot.ContainsKey(nameof(AppSettings.SubtitlesAlwaysOnTop)), "editable lifecycle and privacy preferences are still part of custom profiles");
        AppSettings working = TranslationProfiles.Select(saved, TranslationProfiles.QuickId);
        working.OwnManagedModelId = "temporary-other-model";
        working.OwnVoiceTargetLanguage = "English";
        working.CustomThemeBackground = "#654321";
        working.OwnVoiceAdditionalTargetLanguages.Clear();
        working.FirstRunSetupCompleted = false;
        working.FirstRunIntroductionCompleted = false;
        working.SubtitleGuidePromptSeen = false;
        AppSettings restored = TranslationProfiles.Select(working, savedId);
        Check(restored.OwnManagedModelId == initial.OwnManagedModelId && restored.OwnVoiceTargetLanguage == "Chinese" &&
            restored.CustomThemeBackground == "#123456" && restored.SpeakerMatchThreshold == .63 &&
            restored.OwnVoiceAdditionalTargetLanguages.SequenceEqual(new[] { "English", "Japanese" }),
            "selecting a custom profile restores all user settings including models, languages, appearance and list values");
        Check(!restored.FirstRunSetupCompleted && !restored.FirstRunIntroductionCompleted && !restored.SubtitleGuidePromptSeen,
            "custom profiles cannot acknowledge or reset setup and guides");
        restored.IncomingTargetLanguage = "Japanese";
        Check(TranslationProfiles.IsModified(restored), "custom settings edits are detectable without silently rewriting the saved profile");
        AppSettings updated = TranslationProfiles.Update(restored, savedId);
        Check(!TranslationProfiles.IsModified(updated) && TranslationProfiles.Select(updated, savedId).IncomingTargetLanguage == "Japanese",
            "explicit update replaces the saved snapshot");
        Check(saved.CustomTranslationProfiles.Single().Snapshot[nameof(AppSettings.IncomingTargetLanguage)]!.GetValue<string>() == "Korean",
            "updating a copied library cannot mutate the source profile");
        AppSettings renamed = TranslationProfiles.Rename(updated, savedId, "World friends");
        Check(renamed.ActiveTranslationProfileId == savedId && TranslationProfiles.DisplayName(renamed) == "World friends" &&
            updated.CustomTranslationProfiles.Single().Name == "Private worlds", "renaming preserves identity and is deeply isolated");
        AppSettings deleted = TranslationProfiles.Delete(renamed, savedId);
        Check(deleted.ActiveTranslationProfileId == TranslationProfiles.UnsavedId && deleted.CustomTranslationProfiles.Count == 0 &&
            TranslationProfiles.SameConfiguration(deleted, renamed), "deleting the active profile retains the exact current settings as unsaved");
        Check(TranslationProfiles.List(deleted).Any(choice => choice.Id == TranslationProfiles.UnsavedId),
            "unsaved settings remain selectable and visibly named");
        Reject(() => TranslationProfiles.Create(saved, "private WORLDS"), "duplicate custom names are rejected case-insensitively");
        Reject(() => TranslationProfiles.Create(saved, "Quick"), "built-in names cannot be impersonated by custom profiles");
        Reject(() => TranslationProfiles.Create(saved, "   "), "empty profile names are rejected");
        Reject(() => TranslationProfiles.Create(saved, new string('x', 61)), "oversized names are rejected");
        Reject(() => TranslationProfiles.Update(saved, TranslationProfiles.NormalId), "built-in templates cannot be overwritten");
        Reject(() => TranslationProfiles.Rename(saved, TranslationProfiles.GroupsId, "Custom group"), "built-in templates cannot be renamed");
        Reject(() => TranslationProfiles.Delete(saved, TranslationProfiles.QuickId), "built-in templates cannot be deleted");
        Reject(() => TranslationProfiles.Select(saved, "missing-profile"), "selecting a deleted profile fails clearly rather than applying another template");

        AppSettings migrated = SettingsStore.ReadSettings("{\"IncomingSettleMilliseconds\":1700,\"OwnVoiceEndPauseMilliseconds\":1800,\"UseAiSpeakerRecognition\":false,\"TranslatorModel\":\"my-model\"}");
        Check(migrated is { IncomingSettleMilliseconds: 1700, OwnVoiceEndPauseMilliseconds: 1800, UseAiSpeakerRecognition: false,
            TranslatorModel: "my-model", ActiveTranslationProfileId: "custom-current" } && migrated.CustomTranslationProfiles.Count == 1,
            "existing installations migrate to a snapshot without applying new built-in defaults");
        AppSettings roundTrip = SettingsStore.ReadSettings(JsonSerializer.Serialize(migrated));
        Check(roundTrip.CustomTranslationProfiles.Count == 1 && roundTrip.ActiveTranslationProfileId == "custom-current" &&
            !TranslationProfiles.IsModified(roundTrip), "migration is stable across repeated load/save cycles");
        AppSettings alteredBuiltIn = SettingsStore.ReadSettings("{\"ActiveTranslationProfileId\":\"quick\",\"IncomingSettleMilliseconds\":1500,\"UseAiSpeakerRecognition\":true}");
        Check(alteredBuiltIn.IncomingSettleMilliseconds == 1500 && alteredBuiltIn.UseAiSpeakerRecognition &&
            TranslationProfiles.IsModified(alteredBuiltIn), "reading a modified built-in never silently reapplies it");
        Check(SettingsStore.ReadSettings("{\"ActiveTranslationProfileId\":\"deleted\",\"IncomingSettleMilliseconds\":1400}") is
            { ActiveTranslationProfileId: "custom", IncomingSettleMilliseconds: 1400 }, "unknown profile IDs preserve actual settings and become unsaved");
        AppSettings malformed = SettingsStore.ReadSettings("{\"ActiveTranslationProfileId\":\"broken\",\"CustomTranslationProfiles\":[{\"Id\":\"broken\",\"Name\":\"Broken\",\"Snapshot\":{\"IncomingSettleMilliseconds\":\"not a number\"}}]}");
        Check(malformed.CustomTranslationProfiles.Count == 0 && malformed.ActiveTranslationProfileId == "custom",
            "one malformed snapshot does not prevent loading the application settings");
        AppSettings bounded = SettingsStore.ReadSettings("{\"ActiveTranslationProfileId\":\"quick\",\"IncomingSettleMilliseconds\":800,\"SubtitlePreviewStableMilliseconds\":250,\"RecognitionDelayMilliseconds\":160}");
        Check(bounded is { IncomingSettleMilliseconds: 800, SubtitlePreviewStableMilliseconds: 250, RecognitionDelayMilliseconds: 160 },
            "normalization permits Quick's lower subtitle timing rather than undoing it");
        var metadataOnly = saved.Copy();
        metadataOnly.ActiveTranslationProfileId = TranslationProfiles.QuickId;
        metadataOnly.FirstRunIntroductionCompleted = !saved.FirstRunIntroductionCompleted;
        Check(TranslationProfiles.SameConfiguration(saved, metadataOnly), "settings equality ignores profile bookkeeping and introduction flags");
        AppSettings copiedInto = new();
        TranslationProfiles.CopyInto(saved, copiedInto);
        copiedInto.CustomTranslationProfiles.Single().Snapshot[nameof(AppSettings.Glossary)] = "changed only in copy";
        copiedInto.OwnVoiceAdditionalTargetLanguages.Clear();
        Check(saved.CustomTranslationProfiles.Single().Snapshot[nameof(AppSettings.Glossary)]!.GetValue<string>() == initial.Glossary &&
            saved.OwnVoiceAdditionalTargetLanguages.Count == 2, "copying settings into an existing object deeply isolates profile JSON and collections");
        Check(TranslationProfiles.NormalizeVibeVoiceEndpoint("https://example.com") == TranslationProfiles.DefaultVibeVoiceEndpoint &&
            TranslationProfiles.NormalizeVibeVoiceEndpoint("http://localhost:8082/") == "http://localhost:8082" &&
            TranslationProfiles.NormalizeVibeVoiceEndpoint("http://user:password@localhost:8082") == TranslationProfiles.DefaultVibeVoiceEndpoint,
            "VibeVoice endpoints remain local and cannot leak audio or credentials to a remote host");
        Check(TranslationProfiles.CanonicalVibeVoiceModel("microsoft/VibeVoice-ASR-Streaming-7B").EndsWith("7B", StringComparison.Ordinal) &&
            TranslationProfiles.CanonicalVibeVoiceModel("unsupported") == TranslationProfiles.DefaultVibeVoiceModel,
            "the two supported VibeVoice choices normalize safely");
        AppSettings managedProfile = TranslationProfiles.Create(new AppSettings { UseManagedTranslator = true }, "Managed example");
        AppSettings afterManagedStart = managedProfile.Copy();
        afterManagedStart.TranslatorBaseUrl = "http://127.0.0.1:12888/v1";
        afterManagedStart.OwnTranslatorBaseUrl = "http://127.0.0.1:12888/v1";
        afterManagedStart.TranslatorModel = afterManagedStart.OwnTranslatorModel = "managed-runtime-request-alias";
        Check(!TranslationProfiles.IsModified(afterManagedStart) && TranslationProfiles.ConfigurationEquals(managedProfile, afterManagedStart),
            "managed runtime port/alias allocation does not falsely modify a custom profile");
        AppSettings externalProfile = TranslationProfiles.Create(new AppSettings { UseManagedTranslator = false }, "External example");
        AppSettings changedExternal = externalProfile.Copy(); changedExternal.TranslatorBaseUrl = "http://127.0.0.1:12888/v1";
        Check(TranslationProfiles.IsModified(changedExternal) && !TranslationProfiles.ConfigurationEquals(externalProfile, changedExternal),
            "external runtime endpoint changes remain real editable profile changes");
        Console.WriteLine($"PASS: {checks} translation profile checks; no audio, models, network or installed settings used.");
        return checks;
    }
}

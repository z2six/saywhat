using System.Text.Json;
using System.Text.Json.Nodes;

namespace IncomingTranslator;

// Pure settings snapshots and JSON fixtures. No settings writes, downloads,
// inference process, WSL, audio device or model construction is involved.
internal static class NativeSubtitleDefaultTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Native subtitle defaults: " + detail);
            checks++;
        }
        static bool Engines(AppSettings settings, string incoming, string normal) =>
            settings.IncomingSpeechEngine == incoming && settings.IncomingNormalSpeechEngine == normal;

        var fresh = new AppSettings();
        Check(Engines(fresh, "nemotron", "nemotron") && fresh.OwnRecognitionEngine == "nemotron",
            "a fresh user gets native recognition for both independently started directions");
        Check(IncomingRecognitionCatalog.UsesNative(fresh), "fresh subtitles use the native setup route");
        Check(Engines(fresh.Copy(), "nemotron", "nemotron"), "fresh snapshots retain the native choice");
        Check(Engines(SettingsStore.Normalize(fresh.Copy()), "nemotron", "nemotron"), "normalization does not change fresh recognition");
        Check(Engines(SettingsStore.ReadSettings(JsonSerializer.Serialize(fresh)), "nemotron", "nemotron"),
            "saved fresh preferences keep their explicit native choices on reload");
        foreach (string id in new[] { TranslationProfiles.QuickId, TranslationProfiles.NormalId })
            Check(Engines(TranslationProfiles.Select(fresh, id), "nemotron", "nemotron"),
                "a fresh " + id + " profile retains native recognition");
        AppSettings freshGroups = TranslationProfiles.Select(fresh, TranslationProfiles.GroupsId);
        Check(Engines(freshGroups, "vibevoice", "nemotron"), "Groups remembers a fresh user's native Normal engine");
        Check(Engines(TranslationProfiles.Select(freshGroups, TranslationProfiles.NormalId), "nemotron", "nemotron"),
            "returning from Groups restores a fresh user's native engine");

        foreach (string json in new[]
        {
            "{}",
            """{"FirstRunSetupCompleted":true,"ActiveTranslationProfileId":"normal"}""",
            """{"OwnRecognitionEngine":"nemotron","OwnVoiceSourceLanguage":"English"}"""
        })
        {
            AppSettings legacy = SettingsStore.ReadSettings(json);
            Check(Engines(legacy, "voxtral", "voxtral"), "existing JSON without subtitle fields retains historical Voxtral");
            Check(Engines(SettingsStore.ReadSettings(JsonSerializer.Serialize(legacy)), "voxtral", "voxtral"),
                "a migrated legacy choice survives another settings roundtrip");
        }
        Check(Engines(SettingsStore.ReadSettings("""{"IncomingSpeechEngine":"vibevoice"}"""), "vibevoice", "voxtral"),
            "legacy Groups preferences without a remembered engine retain historical Voxtral");
        Check(Engines(SettingsStore.ReadSettings("""{"IncomingSpeechEngine":"nemotron"}"""), "nemotron", "nemotron"),
            "an explicit native incoming choice supplies its missing Normal memory");

        foreach (string engine in new[] { "voxtral", "nemotron", "firered2-aed", "firered2-ctc" })
        {
            var selected = new AppSettings
            {
                IncomingSpeechEngine = engine, IncomingNormalSpeechEngine = engine,
                IncomingSourceLanguage = "Chinese", IncomingNormalSourceLanguage = "Chinese",
                OwnRecognitionEngine = "voxtral", OwnVoiceSourceLanguage = "Automatic"
            };
            AppSettings reloaded = SettingsStore.ReadSettings(JsonSerializer.Serialize(selected));
            Check(Engines(reloaded, engine, engine) && reloaded.OwnRecognitionEngine == "voxtral",
                "saved recognition choices are preserved: " + engine);
            foreach (string id in new[] { TranslationProfiles.QuickId, TranslationProfiles.NormalId })
                Check(Engines(TranslationProfiles.Select(reloaded, id), engine, engine),
                    id + " preserves an explicitly selected " + engine + " engine");
            AppSettings groups = TranslationProfiles.Select(reloaded, TranslationProfiles.GroupsId);
            groups = SettingsStore.ReadSettings(JsonSerializer.Serialize(groups));
            Check(Engines(groups, "vibevoice", engine), "saved Groups preferences remember " + engine);
            AppSettings restored = TranslationProfiles.Select(groups, TranslationProfiles.NormalId);
            Check(Engines(restored, engine, engine) && restored.IncomingSourceLanguage == "Chinese",
                "leaving Groups restores the selected engine and language: " + engine);
            AppSettings custom = TranslationProfiles.Create(reloaded, "Synthetic recognition selection");
            custom = SettingsStore.ReadSettings(JsonSerializer.Serialize(custom));
            Check(Engines(TranslationProfiles.Select(custom, custom.ActiveTranslationProfileId), engine, engine),
                "imported custom profiles keep an explicit engine: " + engine);
        }

        var sparse = new TranslationProfile
        {
            Id = "custom-legacy-subtitles", Name = "Synthetic older subtitles",
            Snapshot = new JsonObject { [nameof(AppSettings.IncomingSourceLanguage)] = "Automatic" }
        };
        AppSettings WithProfile(TranslationProfile profile) => new()
        {
            ActiveTranslationProfileId = profile.Id, CustomTranslationProfiles = [profile]
        };
        AppSettings imported = SettingsStore.ReadSettings(JsonSerializer.Serialize(WithProfile(sparse)));
        Check(Engines(imported, "nemotron", "nemotron"), "reading an old profile library does not replace the live explicit native choice");
        AppSettings importedChoice = TranslationProfiles.Select(imported, sparse.Id);
        Check(Engines(importedChoice, "voxtral", "voxtral"), "a sparse imported profile retains its historical Voxtral default");
        Check(imported.CustomTranslationProfiles.Single().Snapshot[nameof(AppSettings.IncomingSpeechEngine)]!.GetValue<string>() == "voxtral",
            "the migrated profile snapshot records its historical engine explicitly");
        TranslationProfile sparseGroups = sparse.Copy();
        sparseGroups.Snapshot[nameof(AppSettings.IncomingSpeechEngine)] = "vibevoice";
        imported = SettingsStore.ReadSettings(JsonSerializer.Serialize(WithProfile(sparseGroups)));
        importedChoice = TranslationProfiles.Select(imported, sparseGroups.Id);
        Check(Engines(importedChoice, "vibevoice", "voxtral"), "a sparse imported Groups profile retains its historical Normal engine");
        Check(Engines(TranslationProfiles.Select(importedChoice, TranslationProfiles.NormalId), "voxtral", "voxtral"),
            "leaving an imported legacy Groups profile restores Voxtral");

        return checks;
    }
}

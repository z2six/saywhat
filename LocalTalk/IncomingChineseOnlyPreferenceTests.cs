using System.Text.Json;

namespace IncomingTranslator;

// Pure settings, selection and runtime-planning fixtures; no models, files or capture.
internal static class IncomingChineseOnlyPreferenceTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool valid, string reason)
        {
            if (!valid) throw new InvalidOperationException("Chinese subtitle preference: " + reason);
            checks++;
        }

        Check(!new AppSettings().IncomingChineseOnly && !SettingsStore.ReadSettings("{}").IncomingChineseOnly,
            "new and missing preferences keep all languages unrestricted");
        foreach (string json in new[]
        {
            """{"IncomingSourceLanguage":"Chinese"}""",
            """{"IncomingSourceLanguage":"zh-CN","DetectAnySourceLanguage":true}""",
            """{"IncomingSourceLanguage":"Automatic","DetectAnySourceLanguage":false,"SourceLanguages":["Chinese"]}""",
            """{"DetectAnySourceLanguage":false,"SourceLanguages":["zh"]}"""
        }) Check(SettingsStore.ReadSettings(json).IncomingChineseOnly, "a saved Chinese-only choice migrates to the new explicit filter");
        foreach (string json in new[]
        {
            """{"IncomingSourceLanguage":"Automatic"}""",
            """{"IncomingSourceLanguage":"Automatic","DetectAnySourceLanguage":true,"SourceLanguages":["Chinese"]}""",
            """{"IncomingSourceLanguage":"Automatic","DetectAnySourceLanguage":false,"SourceLanguages":["Chinese","English"]}""",
            """{"IncomingSourceLanguage":"Automatic","DetectAnySourceLanguage":false,"SourceLanguages":[]}""",
            """{"IncomingSourceLanguage":"Automatic","DetectAnySourceLanguage":false}""",
            """{"IncomingSourceLanguage":"English","DetectAnySourceLanguage":false,"SourceLanguages":["Chinese"]}""",
            """{"IncomingSourceLanguage":"Chinese","IncomingChineseOnly":false}""",
            """{"DetectAnySourceLanguage":false,"SourceLanguages":["Chinese"],"IncomingChineseOnly":false}"""
        }) Check(!SettingsStore.ReadSettings(json).IncomingChineseOnly, "Any/bilingual/default preferences and explicit opt-outs do not become restricted");

        foreach (bool enabled in new[] { false, true })
        {
            var selected = new AppSettings { IncomingSourceLanguage = "Chinese", IncomingChineseOnly = enabled };
            Check(selected.Copy().IncomingChineseOnly == enabled && SettingsStore.Normalize(selected.Copy()).IncomingChineseOnly == enabled,
                "copying and normalization preserve the explicitly selected filter");
            Check(SettingsStore.ReadSettings(JsonSerializer.Serialize(selected)).IncomingChineseOnly == enabled,
                "an explicit saved true or false survives repeated settings round trips");
            AppSettings custom = TranslationProfiles.Create(selected, "Filter choice");
            string id = custom.ActiveTranslationProfileId;
            Check(custom.CustomTranslationProfiles.Single().Snapshot[nameof(AppSettings.IncomingChineseOnly)]!.GetValue<bool>() == enabled,
                "custom profiles capture the filter without special metadata");
            AppSettings changed = custom.Copy(); changed.IncomingChineseOnly = !enabled;
            Check(TranslationProfiles.IsModified(changed) && !TranslationProfiles.ConfigurationEquals(custom, changed),
                "an independent filter edit is part of saved configuration comparison");
            Check(TranslationProfiles.Select(changed, id).IncomingChineseOnly == enabled,
                "selecting a custom profile restores its filter choice");
        }

        var source = new AppSettings();
        Check(MainWindow.StageSubtitleSourceLanguage(source, "Chinese") && source.IncomingChineseOnly,
            "switching the spoken-language choice to Chinese enables filtering");
        source.IncomingChineseOnly = false;
        Check(MainWindow.StageSubtitleSourceLanguage(source, "Chinese") && !source.IncomingChineseOnly,
            "clicking the already-selected Chinese choice does not undo a deliberate filter opt-out");
        source.IncomingChineseOnly = true;
        Check(MainWindow.StageSubtitleSourceLanguage(source, "English") && !source.IncomingChineseOnly,
            "choosing another spoken language clears filtering");
        source.IncomingChineseOnly = true;
        Check(MainWindow.StageSubtitleSourceLanguage(source, "Automatic") && !source.IncomingChineseOnly,
            "explicitly choosing Any language clears filtering");
        source.IncomingChineseOnly = true;
        Check(MainWindow.StageHomeSubtitleProfile(source, TranslationProfiles.GroupsId) && source.IncomingSourceLanguage == "Automatic" && source.IncomingChineseOnly,
            "Groups' automatic source switch preserves the independent post-recognition filter");
        Check(TranslationProfiles.Select(source, TranslationProfiles.GroupsId).IncomingChineseOnly,
            "the general Groups preset also preserves the filter");
        var voxtral = new AppSettings { IncomingSourceLanguage = "Chinese", IncomingChineseOnly = true, IncomingSpeechEngine = "firered2-aed" };
        Check(MainWindow.StageSubtitleSpeechEngine(voxtral, "voxtral") && voxtral.IncomingSourceLanguage == "Automatic" && voxtral.IncomingChineseOnly,
            "switching to automatic Voxtral recognition can retain the separate Chinese filter");

        var saved = new AppSettings();
        var draft = saved.Copy(); draft.IncomingChineseOnly = true;
        draft.OwnVoiceTargetLanguage = "Japanese"; draft.OwnVoiceEndPauseMilliseconds = 2400;
        AppSettings candidate = MainWindow.HomeSubtitleCandidate(saved, draft);
        Check(candidate.IncomingChineseOnly && candidate.OwnVoiceTargetLanguage == saved.OwnVoiceTargetLanguage &&
            candidate.OwnVoiceEndPauseMilliseconds == saved.OwnVoiceEndPauseMilliseconds,
            "Home's scoped apply copies the filter without applying microphone drafts");
        Check(MainWindow.SubtitleSpeechCandidate(saved, draft).IncomingChineseOnly &&
            !MainWindow.VoiceFirstCandidate(saved, draft, includeModels: true).IncomingChineseOnly,
            "Listening applies this incoming option and microphone Start leaves it alone");
        SettingsRuntimeChanges changes = SettingsRuntimeChanges.Between(saved, candidate);
        var plan = RuntimeController.BuildPlan(saved, incoming: true);
        Check(!changes.OwnChanged && changes.IncomingChanged && !changes.SharedTranslatorChanged &&
            RuntimeProfiles.CanApplyLive(saved, candidate, plan, plan),
            "filter-only apply uses the existing live subtitle update without restarting speech, translation or microphone models");
        return checks;
    }
}

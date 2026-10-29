using System.Text.Json;

namespace IncomingTranslator;

// Pure settings/profile fixtures. No files, UI, capture, models or network.
internal static class EarlyTranslationPreferenceTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool passed, string reason)
        {
            if (!passed) throw new InvalidOperationException("Early translation preference: " + reason);
            checks++;
        }

        bool FinalOnly(AppSettings settings) => !settings.OwnVoiceSendEarlyPreviews && !settings.IncomingSendEarlyPreviews;
        var defaults = new AppSettings();
        Check(FinalOnly(defaults) && FinalOnly(defaults.Copy()), "fresh settings and copies wait for finished sentences in both directions");
        Check(FinalOnly(SettingsStore.ReadSettings("{}")), "legacy settings without preview flags use final-only defaults");
        Check(FinalOnly(SettingsStore.ReadSettings(JsonSerializer.Serialize(defaults))), "serialized new defaults stay final-only");
        Check(SettingsStore.ReadSettings("""{"OwnVoiceEndPauseMilliseconds":2400}""") is
            { OwnVoiceEndPauseMilliseconds: 1200, OwnVoiceSendEarlyPreviews: false, IncomingSendEarlyPreviews: false },
            "the existing narrow pause migration does not opt into previews");
        Check(SettingsStore.ReadSettings("""{"OwnVoiceEndPauseMilliseconds":2400,"OwnVoiceSendEarlyPreviews":true}""") is
            { OwnVoiceEndPauseMilliseconds: 2400, OwnVoiceSendEarlyPreviews: true, IncomingSendEarlyPreviews: false },
            "a saved microphone opt-in and custom pause survive while a missing subtitle flag defaults off");

        foreach (bool own in new[] { false, true })
        foreach (bool incoming in new[] { false, true })
        {
            var chosen = new AppSettings { OwnVoiceSendEarlyPreviews = own, IncomingSendEarlyPreviews = incoming };
            AppSettings copy = chosen.Copy();
            Check(copy.OwnVoiceSendEarlyPreviews == own && copy.IncomingSendEarlyPreviews == incoming,
                "copies preserve independent own/subtitle choices");
            AppSettings normalized = SettingsStore.Normalize(copy);
            Check(normalized.OwnVoiceSendEarlyPreviews == own && normalized.IncomingSendEarlyPreviews == incoming,
                "normalization preserves explicit boolean choices");
            AppSettings reloaded = SettingsStore.ReadSettings(JsonSerializer.Serialize(chosen));
            Check(reloaded.OwnVoiceSendEarlyPreviews == own && reloaded.IncomingSendEarlyPreviews == incoming,
                "settings round trips preserve explicit choices instead of applying new defaults");

            AppSettings saved = TranslationProfiles.Create(chosen, "Delivery choice");
            string id = saved.ActiveTranslationProfileId;
            TranslationProfile profile = saved.CustomTranslationProfiles.Single();
            Check(profile.Snapshot[nameof(AppSettings.OwnVoiceSendEarlyPreviews)]!.GetValue<bool>() == own &&
                profile.Snapshot[nameof(AppSettings.IncomingSendEarlyPreviews)]!.GetValue<bool>() == incoming,
                "custom profile JSON includes both delivery choices");
            AppSettings changed = saved.Copy();
            changed.OwnVoiceSendEarlyPreviews = !own;
            changed.IncomingSendEarlyPreviews = !incoming;
            Check(TranslationProfiles.IsModified(changed) && !TranslationProfiles.SameConfiguration(saved, changed),
                "changing both preview preferences is an operational configuration change");
            AppSettings incomingOnly = saved.Copy(); incomingOnly.IncomingSendEarlyPreviews = !incoming;
            Check(TranslationProfiles.IsModified(incomingOnly) && !TranslationProfiles.SameConfiguration(saved, incomingOnly),
                "changing subtitles alone is an operational configuration change");
            AppSettings ownOnly = saved.Copy(); ownOnly.OwnVoiceSendEarlyPreviews = !own;
            Check(TranslationProfiles.IsModified(ownOnly) && !TranslationProfiles.SameConfiguration(saved, ownOnly),
                "changing microphone delivery alone is an operational configuration change");
            AppSettings restored = TranslationProfiles.Select(changed, id);
            Check(restored.OwnVoiceSendEarlyPreviews == own && restored.IncomingSendEarlyPreviews == incoming,
                "selecting a saved custom profile restores its delivery choices");
            AppSettings roundTrip = SettingsStore.ReadSettings(JsonSerializer.Serialize(saved));
            Check(TranslationProfiles.Select(roundTrip, id).IncomingSendEarlyPreviews == incoming &&
                TranslationProfiles.Select(roundTrip, id).OwnVoiceSendEarlyPreviews == own,
                "profile snapshots preserve the new field across settings serialization");
            var copiedInto = new AppSettings();
            TranslationProfiles.CopyInto(chosen, copiedInto);
            Check(copiedInto.OwnVoiceSendEarlyPreviews == own && copiedInto.IncomingSendEarlyPreviews == incoming,
                "profile copy-into includes the new incoming choice");
        }

        foreach (string id in new[] { TranslationProfiles.QuickId, TranslationProfiles.NormalId, TranslationProfiles.GroupsId })
        {
            Check(FinalOnly(TranslationProfiles.Select(new AppSettings(), id)), "fresh " + id + " profile does not enable early translations");
            AppSettings optedIn = TranslationProfiles.Select(new AppSettings
                { OwnVoiceSendEarlyPreviews = true, IncomingSendEarlyPreviews = true }, id);
            Check(optedIn.OwnVoiceSendEarlyPreviews && optedIn.IncomingSendEarlyPreviews,
                id + " does not silently replace explicit preview choices");
        }

        const string legacyProfile = """
            {"ActiveTranslationProfileId":"custom-old","OwnVoiceSendEarlyPreviews":true,
             "CustomTranslationProfiles":[{"Id":"custom-old","Name":"Old profile",
             "Snapshot":{"OwnVoiceSendEarlyPreviews":true,"OwnVoiceTargetLanguage":"Chinese"}}]}
            """;
        AppSettings migrated = SettingsStore.ReadSettings(legacyProfile);
        Check(migrated.OwnVoiceSendEarlyPreviews && !migrated.IncomingSendEarlyPreviews,
            "existing profile settings preserve their saved own-voice opt-in");
        AppSettings migratedProfile = TranslationProfiles.Select(migrated, "custom-old");
        Check(migratedProfile.OwnVoiceSendEarlyPreviews && !migratedProfile.IncomingSendEarlyPreviews &&
            migratedProfile.OwnVoiceTargetLanguage == "Chinese",
            "old custom snapshots default the new field off without losing other choices");

        return checks;
    }
}

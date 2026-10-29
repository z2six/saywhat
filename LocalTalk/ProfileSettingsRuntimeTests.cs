namespace IncomingTranslator;

internal static class ProfileSettingsRuntimeTests
{
    // Only value comparisons and pure BuildPlan validation. No WSL, downloads,
    // model loading, audio devices, windows or persisted settings are touched.
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Profile runtime planning: " + reason); checks++; }
        var normal = TranslationProfiles.Select(new AppSettings { UseManagedTranslator = true }, TranslationProfiles.NormalId);
        var same = SettingsRuntimeChanges.Between(normal, normal.Copy());
        Check(same is { OwnChanged: false, IncomingChanged: false, SharedTranslatorChanged: false }, "identical settings require no engine changes");
        Check(TranslationProfiles.ConfigurationEquals(normal, normal.Copy()), "identical Save can return without any runtime operation");
        var theme = normal.Copy(); theme.UiThemeId = "custom"; theme.CustomThemeBackground = "#F8F0DD";
        Check(SettingsRuntimeChanges.Between(normal, theme) is { OwnChanged: false, IncomingChanged: false, SharedTranslatorChanged: false }, "a UI appearance change does not unload translation engines");
        var close = normal.Copy(); close.CloseButtonBehavior = "Tray"; close.CheckForUpdatesOnLaunch = !normal.CheckForUpdatesOnLaunch;
        Check(SettingsRuntimeChanges.Between(normal, close) is { OwnChanged: false, IncomingChanged: false, SharedTranslatorChanged: false }, "desktop preferences are independent of translation engines");
        var groups = TranslationProfiles.Select(normal, TranslationProfiles.GroupsId);
        Check(SettingsRuntimeChanges.Between(normal, groups) is { OwnChanged: false, IncomingChanged: true, SharedTranslatorChanged: false }, "Normal to Groups switches incoming recognition only");
        var identity = normal.Copy(); identity.UseAiSpeakerRecognition = !normal.UseAiSpeakerRecognition;
        Check(SettingsRuntimeChanges.Between(normal, identity) is { OwnChanged: false, IncomingChanged: true, SharedTranslatorChanged: false }, "fingerprint toggle is incoming-only");
        var incomingLanguage = normal.Copy(); incomingLanguage.IncomingTargetLanguage = "Japanese";
        Check(SettingsRuntimeChanges.Between(normal, incomingLanguage) is { OwnChanged: false, IncomingChanged: true, SharedTranslatorChanged: false }, "incoming translation language does not restart microphone capture");
        var application = normal.Copy(); application.IncomingAudioMode = "application";
        application.IncomingApplicationPath = @"C:\Example\Discord.exe"; application.IncomingApplicationName = "Discord";
        Check(SettingsRuntimeChanges.Between(normal, application) is { OwnChanged: false, IncomingChanged: true, SharedTranslatorChanged: false },
            "changing the listened-to application never restarts the microphone or shared translator");
        var single = application.Copy(); single.IncomingSinglePerson = true; single.IncomingPersonId = "friend-a";
        Check(SettingsRuntimeChanges.Between(application, single) is { OwnChanged: false, IncomingChanged: true, SharedTranslatorChanged: false },
            "one-person selection changes incoming capture/identity only");
        var person = single.Copy(); person.IncomingPersonId = "friend-b";
        Check(SettingsRuntimeChanges.Between(single, person) is { OwnChanged: false, IncomingChanged: true, SharedTranslatorChanged: false },
            "choosing another person leaves microphone and resident translation models alone");
        var ownLanguage = normal.Copy(); ownLanguage.OwnVoiceTargetLanguage = "Japanese";
        Check(SettingsRuntimeChanges.Between(normal, ownLanguage) is { OwnChanged: true, IncomingChanged: false, SharedTranslatorChanged: false }, "own language change only requires the outgoing feature");
        var sharedModel = normal.Copy(); sharedModel.OwnManagedModelId = "hy-mt2-7b-q4_k_m";
        Check(SettingsRuntimeChanges.Between(normal, sharedModel) is { OwnChanged: true, IncomingChanged: true, SharedTranslatorChanged: true }, "changing a shared translator coordinates both owners");
        var separate = normal.Copy(); separate.ShareTranslationModel = false;
        var incomingModel = separate.Copy(); incomingModel.IncomingManagedModelId = "hy-mt2-7b-q4_k_m";
        Check(SettingsRuntimeChanges.Between(separate, incomingModel) is { OwnChanged: false, IncomingChanged: true, SharedTranslatorChanged: false }, "separate incoming model change does not affect own translator");
        var hidden = normal.Copy(); hidden.IncomingManagedModelId = "hy-mt2-7b-q4_k_m";
        Check(SettingsRuntimeChanges.Between(normal, hidden) is { OwnChanged: false, IncomingChanged: false, SharedTranslatorChanged: false }, "inactive separate-model choice cannot trigger a restart while sharing");
        var quick = TranslationProfiles.Select(normal, TranslationProfiles.QuickId);
        Check(SettingsRuntimeChanges.Between(normal, quick) is { OwnChanged: true, IncomingChanged: true, SharedTranslatorChanged: false }, "Quick look-ahead change applies both capture settings without replacing the translator");
        var saved = TranslationProfiles.Create(normal, "My normal settings");
        Check(TranslationProfiles.ConfigurationEquals(normal, saved) && SettingsRuntimeChanges.Between(normal, saved) is { OwnChanged: false, SharedTranslatorChanged: false }, "saving a profile snapshot does not replace models or change operational settings");
        var invalidOld = normal.Copy(); invalidOld.ManagedRuntimeId = "removed-runtime";
        Check(SettingsRuntimeChanges.Between(invalidOld, normal) is { OwnChanged: true, IncomingChanged: true, SharedTranslatorChanged: true }, "a valid selection can replace an invalid legacy runtime");
        return checks;
    }
}

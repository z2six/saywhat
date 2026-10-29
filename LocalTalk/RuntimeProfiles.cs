namespace IncomingTranslator;

// Profile choice and application-wide preferences are independent from the
// exact incoming runtime identity. Changing names/themes/timings must not
// unload an unchanged model, while a new Groups checkpoint must be restarted.
internal static class RuntimeProfiles
{
    internal static bool UsesGroups(AppSettings settings) => settings.IncomingSpeechEngine == "vibevoice";

    // The saved preference remains unchanged in a one-person session. That
    // session already has its identity supplied by the user, so no fingerprint
    // model is required or admitted until they return to automatic matching.
    internal static bool UsesSpeakerRecognition(AppSettings settings) =>
        settings.UseAiSpeakerRecognition && !settings.IncomingSinglePerson;

    internal static void ValidateSpeakerRecognitionAssets(AppSettings settings)
    {
        if (UsesSpeakerRecognition(settings) && !System.IO.File.Exists(settings.SpeakerModelPath))
            throw new System.IO.FileNotFoundException("Download or select the voice recognition model first, or turn person recognition off.", settings.SpeakerModelPath);
    }

    internal static void ValidateIncomingSpeech(AppSettings settings)
    {
        if (settings.IncomingSpeechEngine is not ("voxtral" or "vibevoice" or "nemotron" or "firered2-ctc" or "firered2-aed"))
            throw new ArgumentException("Choose a supported subtitle speech mode.");
        // Older versions allowed a source hint for the text translator while
        // Voxtral still auto-detected audio. Keep those working saved choices;
        // the new UI routes explicit recognition choices to compatible models.
        bool legacyTranslationHint = settings.IncomingSpeechEngine == "voxtral" &&
            AppSettings.CanonicalTranslationLanguage(settings.IncomingSourceLanguage) is not null;
        if (!legacyTranslationHint && !IncomingRecognitionCatalog.Find(settings.IncomingSpeechEngine).SupportsSource(settings.IncomingSourceLanguage))
            throw new ArgumentException("Choose a spoken language supported by the selected subtitle speech model.");
        if (!UsesGroups(settings)) return;
        if (!VibeVoiceRuntime.IsSupportedModel(settings.VibeVoiceModelId))
            throw new ArgumentException("Choose a supported VibeVoice streaming model.");
        VibeVoiceRuntime.ValidateEndpoint(settings.VibeVoiceEndpoint);
    }

    internal static bool SameIncomingSpeech(AppSettings previous, AppSettings next) =>
        previous.IncomingSpeechEngine == next.IncomingSpeechEngine &&
        previous.AutomaticManagedPorts == next.AutomaticManagedPorts &&
        (next.AutomaticManagedPorts || (UsesGroups(next) ? previous.GroupsSpeechPort == next.GroupsSpeechPort : previous.IncomingSpeechPort == next.IncomingSpeechPort)) &&
        (!UsesGroups(next) || previous.VibeVoiceModelId == next.VibeVoiceModelId &&
            string.Equals(previous.VibeVoiceEndpoint, next.VibeVoiceEndpoint, StringComparison.OrdinalIgnoreCase));

    internal static bool SameIncomingCapture(AppSettings previous, AppSettings next) =>
        IncomingListening.SameSource(previous, next) &&
        previous.IncomingSourceLanguage == next.IncomingSourceLanguage &&
        previous.IncomingSinglePerson == next.IncomingSinglePerson &&
        (!next.IncomingSinglePerson || string.Equals(previous.IncomingPersonId, next.IncomingPersonId, StringComparison.Ordinal));

    internal static bool CanReuseIncomingModels(AppSettings previous, AppSettings next,
        RuntimeController.RuntimePlan previousPlan, RuntimeController.RuntimePlan nextPlan) =>
        SameIncomingSpeech(previous, next) && previousPlan.TranslationKey == nextPlan.TranslationKey &&
        previousPlan.Managed == nextPlan.Managed && previousPlan.Shared == nextPlan.Shared;

    internal static bool CanApplyLive(AppSettings previous, AppSettings next,
        RuntimeController.RuntimePlan previousPlan, RuntimeController.RuntimePlan nextPlan) =>
        SameIncomingCapture(previous, next) && CanReuseIncomingModels(previous, next, previousPlan, nextPlan);

    internal static int RunOfflineChecks()
    {
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Runtime profile planning: " + name); checks++; }
        AppSettings normal = new(), quick = normal.Copy();
        quick.UseAiSpeakerRecognition = false; quick.IncomingSettleMilliseconds = 800;
        Check(SameIncomingSpeech(normal, quick), "normal/quick settings do not imply an engine replacement");
        AppSettings groups = normal.Copy(); groups.IncomingSpeechEngine = "vibevoice";
        Check(!SameIncomingSpeech(normal, groups), "Groups replaces only incoming speech engine");
        ValidateIncomingSpeech(groups); checks++;
        AppSettings larger = groups.Copy(); larger.VibeVoiceModelId = VibeVoiceRuntime.Models[1].Id;
        Check(!SameIncomingSpeech(groups, larger), "a different Groups checkpoint restarts speech");
        var plan = RuntimeController.BuildPlan(normal, incoming: true);
        Check(CanApplyLive(normal, quick, plan, plan), "timing/recognition updates stay live");
        Check(!CanApplyLive(normal, groups, plan, plan), "engine switches pause and resume same overlay");
        Check(!CanApplyLive(normal, quick, plan, plan with { ModelAlias = "another" }), "new translation model cannot apply without lifecycle work");

        AppSettings application = normal.Copy();
        application.IncomingAudioMode = "application";
        application.IncomingApplicationPath = @"C:\Apps\Discord\Discord.exe";
        application.IncomingApplicationName = "Discord";
        Check(!SameIncomingCapture(normal, application), "an application changes incoming capture without changing the microphone");
        Check(!CanApplyLive(normal, application, plan, plan), "application switch recreates capture instead of mixing audio from different sources");
        Check(CanReuseIncomingModels(normal, application, plan, plan), "application switch retains resident speech and translation models");
        AppSettings renamed = application.Copy(); renamed.IncomingApplicationName = "Discord voice call";
        Check(SameIncomingCapture(application, renamed), "application display-name changes do not replace the capture target");
        AppSettings another = application.Copy(); another.IncomingApplicationPath = @"C:\Apps\Steam\steam.exe";
        Check(!SameIncomingCapture(application, another), "a different application replaces capture");

        AppSettings single = application.Copy(); single.IncomingSinglePerson = true; single.IncomingPersonId = "person-a";
        single.SpeakerModelPath = System.IO.Path.Combine(AppContext.BaseDirectory, "absent-inert-speaker-model.onnx");
        Check(single.UseAiSpeakerRecognition && !UsesSpeakerRecognition(single), "one-person mode bypasses matching without changing the saved preference");
        ValidateSpeakerRecognitionAssets(single); checks++;
        Check(!CanApplyLive(application, single, plan, plan) && CanReuseIncomingModels(application, single, plan, plan),
            "one-person changes rebuild only the capture service");
        AppSettings otherPerson = single.Copy(); otherPerson.IncomingPersonId = "person-b";
        Check(!SameIncomingCapture(single, otherPerson), "a different selected person clears temporary identity context");
        AppSettings automatic = single.Copy(); automatic.IncomingSinglePerson = false;
        Check(UsesSpeakerRecognition(automatic), "automatic matching restores the existing user preference");
        bool missingModelRejected = false;
        try { ValidateSpeakerRecognitionAssets(automatic); }
        catch (System.IO.FileNotFoundException) { missingModelRejected = true; }
        Check(missingModelRejected, "automatic matching rejects a missing fingerprint model");
        AppSettings singleGroups = groups.Copy(); singleGroups.IncomingSinglePerson = true;
        Check(UsesGroups(singleGroups) && !UsesSpeakerRecognition(singleGroups), "one-person attribution does not silently replace Groups speech mode");
        AppSettings endpointChange = groups.Copy(); endpointChange.VibeVoiceEndpoint = "http://127.0.0.1:8083";
        Check(!SameIncomingSpeech(groups, endpointChange), "changing the Groups endpoint is a speech runtime change");
        return checks;
    }
}

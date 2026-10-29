namespace IncomingTranslator;

internal sealed partial class IncomingTranslationService
{
    private bool HasSinglePersonSelectionLocked()
    {
        AppSettings settings = CurrentSettings();
        return settings.IncomingSinglePerson && !string.IsNullOrEmpty(settings.IncomingPersonId) && _personNames.ContainsKey(settings.IncomingPersonId) &&
            !_deletedPeople.Contains(settings.IncomingPersonId);
    }

    private SpeakerAttribution SinglePersonAttributionLocked(SpeakerAttribution speaker)
    {
        AppSettings settings = CurrentSettings();
        string? id = settings.IncomingPersonId;
        bool named = !string.IsNullOrEmpty(id) && !_deletedPeople.Contains(id) &&
            _personNames.TryGetValue(id, out _);
        return speaker with
        {
            Label = named ? _personNames[id!] : "Unidentified speaker",
            PersonId = named ? id : null, SuggestedPersonId = null, PersonConfirmed = named,
            Confidence = 0, CanLearn = false, IsMixed = false, Pan = 0, SessionVoiceKey = "",
            Diagnostic = "One-person listening uses the selected name; no automatic matching or voice learning is performed."
        };
    }

    private SpeakerAttribution QueueSinglePersonAttribution(SpeakerAttribution fallback)
    {
        // Label-only evidence permits per-message corrections but contains no
        // embedding, so assumed identities can never become training examples.
        SpeakerAttribution observation = _aiSpeakerIdentifier.RecordLabelOnlyObservation(fallback);
        lock (_feedbackGate) return SinglePersonAttributionLocked(observation);
    }

    internal static async Task<int> RunSinglePersonListeningChecksAsync(string root)
    {
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("One-person listening: " + name); checks++; }
        using var identifier = new AiSpeakerIdentifier(Path.Combine(root, "anonymous.json"), Path.Combine(root, "people.json"));
        SpeakerPerson a = await identifier.SaveSpeakerPersonAsync("Alice");
        SpeakerPerson b = await identifier.SaveSpeakerPersonAsync("Bob");
        var settings = new AppSettings
        {
            IncomingAudioMode = "application", IncomingApplicationPath = @"C:\Example\Discord.exe",
            IncomingApplicationName = "Discord", IncomingSinglePerson = true, IncomingPersonId = a.Id,
            UseAiSpeakerRecognition = true, SpeakerModelPath = Path.Combine(root, "not-installed.onnx")
        };
        using var service = new IncomingTranslationService(settings,
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, identifier);
        SpeakerRecognitionStatus status = await service.ApplySpeakerRecognitionAsync(true);
        Check(!status.Enabled && !status.ModelLoaded && !identifier.RecognitionEnabled && identifier.ExtractorLoadAttempts == 0,
            "An enabled preference cannot admit a fingerprint model in one-person mode");
        Check(service.CurrentSettings().UseAiSpeakerRecognition, "Bypassing recognition preserves the saved preference");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SpeakerAttribution speaker = service.QueueSpeakerIdentification(1, "Hello", now.AddSeconds(-2), now, now, 0);
        Check(speaker.PersonId == a.Id && speaker.Label == "Alice" && speaker.PersonConfirmed && !speaker.CanLearn && speaker.Confidence == 0,
            "A selected person is an explicit label, not an AI confidence claim");
        Check(!string.IsNullOrEmpty(speaker.EvidenceId) && service._latestIdentityJobs.Count == 0 && identifier.ExtractorLoadAttempts == 0,
            "No background identity job or voice inference is queued");
        SpeakerAttribution group = service.QueueGroupSpeakerIdentification(2, "Hi", Array.Empty<float>(),
            new("Voice 2", .8, true, -.5), now, 0, "voice:2");
        Check(group.PersonId == a.Id && !group.IsMixed && group.SessionVoiceKey == "" && !group.CanLearn,
            "Groups text can use the same chosen identity without enrolling audio");
        TranslationRequest request = service.CreateTranslationRequest("Hello", speaker, now.AddSeconds(-2), now, 1) with { IsCommitted = true };
        var display = new TranslationDisplay(speaker.Label, "Hello", "Hello", 1, now)
        { PersonId = speaker.PersonId, EvidenceId = speaker.EvidenceId };
        await service.PublishTranslationDisplayAsync(display);
        Check(service._publishedDisplays[1].IsManuallyAttributed && service._publishedDisplays[1].PersonId == a.Id,
            "The subtitle shows the name and avatar without a misleading Maybe suggestion");
        await service.RecordPublishedTranslationAsync(request, CancellationToken.None);
        SpeakerPerson counted = (await identifier.GetSpeakerFeedbackStateAsync()).People.Single(person => person.Id == a.Id);
        Check(counted.TranslationCount == 1 && counted.ConfirmedSamples == 0 && counted.ConfirmedSuggestions == 0,
            "Named translations count once but never reinforce an assumed voice");
        SpeakerFeedbackResult correction = await service.CorrectSpeakerAsync(speaker.EvidenceId, b.Id);
        SpeakerAttribution corrected = service.ResolveSpeaker(1, speaker);
        Check(correction.Applied && !correction.Learned && corrected.PersonId == b.Id && corrected.PersonConfirmed,
            "An explicit per-message correction overrides the selected session person without learning");
        await service.DeleteSpeakerPersonAsync(a.Id);
        SpeakerAttribution missing = service.ResolveSpeaker(3, speaker);
        Check(missing.PersonId is null && !missing.PersonConfirmed && !missing.CanLearn,
            "A deleted selected person becomes unknown rather than leaking a stale name");
        AppSettings unnamed = settings.Copy(); unnamed.IncomingPersonId = "";
        service.UpdateSettings(unnamed);
        Check(service.ResolveSpeaker(4, speaker).PersonId is null, "One-person mode also works without choosing a saved name");
        AppSettings multiple = settings.Copy(); multiple.IncomingSinglePerson = false;
        service.UpdateSettings(multiple);
        status = await service.ApplySpeakerRecognitionAsync(true);
        Check(status.Enabled && !status.ModelLoaded && identifier.ExtractorLoadAttempts == 0,
            "Returning to multiple people restores recognition without eagerly loading its model");
        Check((await identifier.GetSpeakerFeedbackStateAsync()).People.Single().ConfirmedSamples == 0,
            "All one-person labels and corrections leave saved voice training unchanged");
        await service.ApplySpeakerRecognitionAsync(false);
        return checks;
    }
}

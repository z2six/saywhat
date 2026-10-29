using System.Reflection;

namespace IncomingTranslator;

// Synthetic fingerprints and isolated metadata only. Service.RunAsync is never
// invoked, so these fixtures cannot capture sound or contact translation servers.
internal static class SpeakerReviewServiceTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        string? dataRoot = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(dataRoot) || !AppPaths.IsPackagedApplication)
            throw new InvalidOperationException("Review service tests require the redirected offline desktop fixture.");
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException("Person suggestion service: " + detail); checks++; }
        string fixture = Path.Combine(directory, "fixture-" + Guid.NewGuid().ToString("N"));
        using var identifier = new AiSpeakerIdentifier(Path.Combine(fixture, "anonymous.json"), Path.Combine(fixture, "people.json"));
        var published = new List<TranslationDisplay>();
        using var service = new IncomingTranslationService(new AppSettings { UseAiSpeakerRecognition = true, IncomingSpeechEngine = "vibevoice" },
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, display => { published.Add(display); return Task.CompletedTask; }, identifier);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(IncomingTranslationService).GetField(name, flags)!.GetValue(service)!;
        object? Call(string name, params object?[] args) => typeof(IncomingTranslationService).GetMethods(flags)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(service, args);
        void Set(string name, object value) => typeof(IncomingTranslationService).GetField(name, flags)!.SetValue(service, value);
        async Task Publish(TranslationDisplay display) => await ((Task)Call("PublishTranslationDisplayAsync", display)!).ConfigureAwait(false);
        var evidenceBubbles = Field<Dictionary<string, long>>("_evidenceBubbles");
        var groupBubbles = Field<Dictionary<long, string>>("_groupBubbleVoices");
        var committed = Field<Dictionary<long, DateTimeOffset>>("_publishedCommittedTranslations");
        var manual = Field<Dictionary<long, SpeakerFeedbackResult>>("_manualSpeakers");
        DateTimeOffset at = DateTimeOffset.UtcNow;
        const string key = "vibevoice:review-fixture:0:0";
        string model = new('a', 64);
        SpeakerPerson ringo = await service.SaveSpeakerPersonAsync("Ringo").ConfigureAwait(false);
        SpeakerPerson other = await service.SaveSpeakerPersonAsync("Another person").ConfigureAwait(false);
        await service.AcknowledgeSpeakerLearningAsync().ConfigureAwait(false);
        Set("_nextUtteranceId", 10L);
        SpeakerAttribution Evidence(float[]? embedding, string personId, string name, long bubble, bool mixed = false)
        {
            SpeakerAttribution sample = identifier.AddSyntheticFeedbackEvidence(embedding, embedding is null ? "" : model,
                new(name, .9, mixed, 0) { CanLearn = !mixed, PersonId = personId, SuggestedPersonId = personId,
                    SessionVoiceKey = key, EvidenceStart = at.AddSeconds(bubble * 3), EvidenceEnd = at.AddSeconds(bubble * 3 + 2) });
            evidenceBubbles[sample.EvidenceId] = bubble; groupBubbles[bubble] = key; committed[bubble] = at;
            return sample;
        }
        TranslationDisplay Display(SpeakerAttribution sample, long bubble) => new(sample.Label, "Synthetic source " + bubble, "Synthetic translation " + bubble, bubble, at)
        { EvidenceId = sample.EvidenceId, PersonId = sample.PersonId, SessionVoiceKey = sample.SessionVoiceKey,
            SpeakerRecognitionEpoch = 0 };
        SpeakerAttribution first = Evidence([1f, 0f], ringo.Id, ringo.Name, 1);
        SpeakerAttribution second = Evidence([0f, 1f], ringo.Id, ringo.Name, 2);
        await Publish(Display(first, 1)).ConfigureAwait(false);
        await Publish(Display(second, 2)).ConfigureAwait(false);
        Check(published.All(display => display.PersonId == ringo.Id && !display.IsManuallyAttributed),
            "automatic named bubbles remain tentative before any review");
        Call("OnGroupSpeakerIdentified", key, first, 0L);
        Call("OnGroupSpeakerIdentified", key, first with { PersonId = null, SuggestedPersonId = null, Label = "Voice 1" }, 0L);
        SpeakerAttribution unlinked = (SpeakerAttribution)Call("ResolveGroupSpeakerLocked",
            new SpeakerAttribution("Voice 1", 0, false, 0) { SessionVoiceKey = key }, (long?)0)!;
        Check(unlinked.PersonId is null, "a newer unmatched or unsafe result revokes an old automatic group name");
        SpeakerFeedbackState initial = await service.GetSpeakerFeedbackStateAsync().ConfigureAwait(false);
        Check(initial.People.All(person => person.ConfirmedSuggestions == 0 && person.RejectedSuggestions == 0 && person.ConfirmedSamples == 0),
            "publishing guesses does not save examples or treat guesses as correct");
        SpeakerFeedbackResult confirmed = await service.ReviewSpeakerSuggestionAsync(first.EvidenceId, ringo.Id, true).ConfigureAwait(false);
        Check(confirmed.Applied && confirmed.Learned && confirmed.IsManuallyAttributed && confirmed.SuggestionConfirmed == true,
            "explicit confirmation saves an eligible voice example and confirms its own bubble");
        Check(manual[1].PersonId == ringo.Id && !manual.ContainsKey(2), "confirmation applies only to the reviewed bubble");
        await Publish(Display(second, 2)).ConfigureAwait(false);
        Check(published[^1].PersonId == ringo.Id && !published[^1].IsManuallyAttributed,
            "the next bubble on the same group track is not automatically user-confirmed");
        var state = await service.GetSpeakerFeedbackStateAsync().ConfigureAwait(false);
        SpeakerPerson updated = state.People.Single(person => person.Id == ringo.Id);
        Check(updated.ConfirmedSuggestions == 1 && updated.ConfirmedSamples == 1 && updated.RejectedSuggestions == 0 && updated.TranslationCount == 1,
            "a confirmed committed sentence is counted exactly once");
        SpeakerFeedbackResult duplicate = await service.ReviewSpeakerSuggestionAsync(first.EvidenceId, ringo.Id, true).ConfigureAwait(false);
        updated = (await service.GetSpeakerFeedbackStateAsync().ConfigureAwait(false)).People.Single(person => person.Id == ringo.Id);
        Check(!duplicate.Applied && updated.ConfirmedSuggestions == 1 && updated.ConfirmedSamples == 1,
            "a duplicate or obsolete confirmation cannot inflate reviewed history");
        SpeakerFeedbackResult rejected = await service.ReviewSpeakerSuggestionAsync(second.EvidenceId, ringo.Id, false).ConfigureAwait(false);
        Check(rejected.Applied && rejected.NegativeLearned && rejected.PersonId is null && rejected.SuggestionConfirmed == false,
            "a clear rejected voice is remembered as not the suggested person, never as a new person");
        updated = (await service.GetSpeakerFeedbackStateAsync().ConfigureAwait(false)).People.Single(person => person.Id == ringo.Id);
        Check(updated.ConfirmedSuggestions == 1 && updated.RejectedSuggestions == 1 && updated.ConfirmedSamples == 1 &&
            identifier.GetNegativeAnchorsForTest(model).Count == 1,
            "negative review has its own statistic/reference without damaging valid examples");
        Call("OnGroupSpeakerIdentified", key, first, 0L);
        await Publish(Display(first, 3) with { EvidenceId = "synthetic-future-group-guess" }).ConfigureAwait(false);
        Check(published[^1].PersonId is null && !published[^1].IsManuallyAttributed,
            "a late track-level match cannot immediately resurrect a rejected person");
        SpeakerAttribution fourth = Evidence(null, ringo.Id, ringo.Name, 4);
        // A second explicit rejection contribution is inserted without inference to
        // verify session veto accounting, independent of the learned sample collection.
        Call("ApplyContextCorrection", fourth.EvidenceId, new SpeakerFeedbackResult(false, "Synthetic second rejection", null, "Unidentified speaker", true)
        { SuggestedPersonId = ringo.Id, SuggestionConfirmed = false });
        SpeakerFeedbackResult undone = await service.UndoSpeakerCorrectionAsync(second.EvidenceId).ConfigureAwait(false);
        Check(undone.Applied && !undone.IsManuallyAttributed && undone.PersonId == ringo.Id, "undo returns the old candidate as a suggestion, not a confirmation");
        SpeakerAttribution stillBlocked = (SpeakerAttribution)Call("ResolveSpeaker", 2L, second, (long?)0)!;
        Check(stillBlocked.PersonId is null, "undoing one rejection cannot bypass a second active same-track rejection");
        updated = (await service.GetSpeakerFeedbackStateAsync().ConfigureAwait(false)).People.Single(person => person.Id == ringo.Id);
        Check(updated.RejectedSuggestions == 0 && updated.ConfirmedSuggestions == 1 && identifier.GetNegativeAnchorsForTest(model).Count == 0 && updated.ConfirmedSamples == 1,
            "undo removes only its rejection statistic and negative example, preserving confirmed evidence");
        Call("ApplyContextCorrection", fourth.EvidenceId, new SpeakerFeedbackResult(false, "Synthetic undo of second rejection", ringo.Id, ringo.Name, false)
        { SuggestedPersonId = ringo.Id, IsManuallyAttributed = false });
        SpeakerAttribution restored = (SpeakerAttribution)Call("ResolveSpeaker", 2L, second, (long?)0)!;
        Check(restored.PersonId == ringo.Id && !restored.PersonConfirmed, "after all active rejections are undone, a track can suggest the name again");
        SpeakerAttribution stale = Evidence([1f, 0f], ringo.Id, ringo.Name, 5);
        await Publish(Display(stale, 5)).ConfigureAwait(false);
        SpeakerFeedbackResult wrongName = await service.ReviewSpeakerSuggestionAsync(stale.EvidenceId, other.Id, false).ConfigureAwait(false);
        Check(!wrongName.Applied && !manual.ContainsKey(5), "a stale different suggested-person action cannot teach the wrong negative voice");
        Set("_speakerRecognitionEpoch", 1L);
        SpeakerFeedbackResult staleEpoch = await service.ReviewSpeakerSuggestionAsync(stale.EvidenceId, ringo.Id, true).ConfigureAwait(false);
        Check(!staleEpoch.Applied && !manual.ContainsKey(5), "an old recognition epoch cannot review a no-longer-current guess");
        Set("_speakerRecognitionEpoch", 0L);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool cancellationObserved = false;
        try { await service.ReviewSpeakerSuggestionAsync(stale.EvidenceId, ringo.Id, true, canceled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved && !manual.ContainsKey(5), "canceling before review does not partially change labels or saved data");
        Call("ApplyContextCorrection", first.EvidenceId,
            new SpeakerFeedbackResult(false, "Synthetic manually chosen group name", ringo.Id, ringo.Name, true));
        await service.ApplySpeakerRecognitionAsync(false).ConfigureAwait(false);
        SpeakerAttribution confirmedAfterOff = (SpeakerAttribution)Call("ResolveSpeaker", 1L, first, (long?)0)!;
        Check(confirmedAfterOff.PersonId == ringo.Id && confirmedAfterOff.PersonConfirmed,
            "turning recognition off preserves the name explicitly chosen for the original bubble");
        await Publish(Display(first, 6) with { EvidenceId = "synthetic-future-while-off" }).ConfigureAwait(false);
        Check(published[^1].PersonId is null && !published[^1].IsManuallyAttributed && !SubtitleWindow.IsSpeakerSuggestion(published[^1]),
            "off does not inherit a named track guess or show unusable review controls on future bubbles");
        Check(identifier.ExtractorLoadAttempts == 0 && !identifier.IsModelLoaded && Field<object?>("_capture") is null && Field<object?>("_socket") is null,
            "every review fixture avoided model loading, audio capture, and speech network connections");
        return checks;
    }
}

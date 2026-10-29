using System.Threading.Channels;

namespace IncomingTranslator;

internal sealed partial class IncomingTranslationService
{
    // Identity is best-effort metadata, never a gate before translation. Its audio
    // snapshots are short-lived RAM only and bounded independently of text work.
    private readonly Channel<SpeakerIdentificationJob> _identificationJobs = Channel.CreateBounded<SpeakerIdentificationJob>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly Dictionary<long, long> _latestIdentityJobs = new();
    private readonly Dictionary<long, (long Epoch, SpeakerAttribution Speaker)> _automaticSpeakerResults = new();
    private readonly Dictionary<long, TranslationDisplay> _publishedDisplays = new();
    private readonly SemaphoreSlim _displayPublicationGate = new(1, 1);
    private long _identityJobSequence;
    private TranslationRequest? _activeTranslation;
    private CancellationTokenSource? _activeTranslationCancellation;

    private sealed record SpeakerIdentificationJob(long BubbleId, string Source, SpeakerAttribution Fallback,
        float[] Samples, AppSettings Settings, DateTimeOffset At, long Epoch, long Sequence, string VoiceKey);

    private SpeakerAttribution QueueSpeakerIdentification(long bubbleId, string source, DateTimeOffset start,
        DateTimeOffset end, DateTimeOffset at, long recognitionEpoch)
    {
        SpeakerAttribution fallback = _speakerTracker.Identify(start, end);
        AppSettings settings = CurrentSettings();
        if (settings.IncomingSinglePerson) return QueueSinglePersonAttribution(fallback);
        bool automatic = settings.UseAiSpeakerRecognition && _aiSpeakerIdentifier.RecognitionEnabled;
        if (!automatic) fallback = WithoutAutomaticIdentity(fallback);
        float[] samples = automatic && !fallback.IsMixed && fallback.CanLearn
            ? _speakerTracker.GetMonoAudio16k(fallback.EvidenceStart, fallback.EvidenceEnd) : [];
        return QueueCapturedSpeakerIdentification(bubbleId, source, samples, fallback, settings, at, recognitionEpoch, "");
    }

    private SpeakerAttribution QueueGroupSpeakerIdentification(long bubbleId, string source, float[] samples,
        SpeakerAttribution fallback, DateTimeOffset at, long recognitionEpoch, string voiceKey)
    {
        AppSettings settings = CurrentSettings();
        if (settings.IncomingSinglePerson) return QueueSinglePersonAttribution(fallback);
        // The group router supplies audio only after verifying an isolated chunk.
        // Copy it now; later capture or model work must not mutate our evidence.
        if (GroupSpeechProtocol.IsUnidentifiedKey(voiceKey)) fallback = fallback with
        { IsMixed = true, CanLearn = false, PersonId = null, Confidence = 0, Label = "Voice" };
        float[] captured = !GroupSpeechProtocol.IsUnidentifiedKey(voiceKey) && settings.UseAiSpeakerRecognition && _aiSpeakerIdentifier.RecognitionEnabled &&
            !fallback.IsMixed && fallback.CanLearn ? samples.ToArray() : [];
        return QueueCapturedSpeakerIdentification(bubbleId, source, captured, fallback, settings, at, recognitionEpoch, voiceKey);
    }

    private SpeakerAttribution QueueGroupSpeakerIdentification(long bubbleId, string source, DateTimeOffset start,
        DateTimeOffset end, DateTimeOffset at, long recognitionEpoch, SpeakerAttribution fallback, float[] samples, string voiceKey) =>
        QueueGroupSpeakerIdentification(bubbleId, source, samples,
            fallback with { EvidenceStart = start, EvidenceEnd = end }, at, recognitionEpoch, voiceKey);

    private SpeakerAttribution QueueCapturedSpeakerIdentification(long bubbleId, string source, float[] samples,
        SpeakerAttribution fallback, AppSettings settings, DateTimeOffset at, long epoch, string voiceKey)
    {
        SpeakerAttribution labelOnly = _aiSpeakerIdentifier.RecordLabelOnlyObservation(fallback);
        SpeakerAttribution immediate = fallback with { EvidenceId = labelOnly.EvidenceId, CanLearn = false };
        long sequence = Interlocked.Increment(ref _identityJobSequence);
        lock (_feedbackGate)
        {
            _evidenceBubbles[immediate.EvidenceId] = bubbleId;
            _latestIdentityJobs[bubbleId] = sequence;
            PruneIdentityMetadataLocked();
        }
        if (!GroupSpeechProtocol.IsUnidentifiedKey(voiceKey) && settings.UseAiSpeakerRecognition && _aiSpeakerIdentifier.RecognitionEnabled && !_disposed)
            _identificationJobs.Writer.TryWrite(new(bubbleId, source, fallback, samples, settings.Copy(), at, epoch, sequence, voiceKey));
        return immediate;
    }

    private async Task IdentifyQueuedSpeakersAsync(CancellationToken token)
    {
        await foreach (SpeakerIdentificationJob job in _identificationJobs.Reader.ReadAllAsync(token))
        {
            if (!IsIdentityJobCurrent(job)) continue;
            try
            {
                // PCM was captured when the text became ready, not after this
                // queue drained. The inference never reads a moving live buffer.
                SpeakerAttribution result = await _aiSpeakerIdentifier.IdentifyAsync(job.Samples,
                    job.Fallback, job.Settings, token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(job.VoiceKey)) result = result with
                {
                    SessionVoiceKey = job.VoiceKey,
                    Label = result.PersonId is null ? GroupSpeechProtocol.VoiceLabel(job.VoiceKey) : result.Label
                };
                if (!IsIdentityJobCurrent(job)) continue;
                await ApplyIdentityResultAsync(job, result, token).ConfigureAwait(false);
                Log($"Background person recognition: subtitle={job.BubbleId}; {_aiSpeakerIdentifier.LastDiagnostic}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Failed person matching leaves the already scheduled/published
                // translation intact. Its label-only evidence remains usable.
                Log("Person recognition failed; subtitles continue without a name: " + error);
            }
        }
    }

    private bool IsIdentityJobCurrent(SpeakerIdentificationJob job)
    {
        if (_disposed || !IsRecognitionAttributionCurrent(job.Epoch)) return false;
        lock (_feedbackGate) return _latestIdentityJobs.TryGetValue(job.BubbleId, out long latest) && latest == job.Sequence;
    }

    private async Task ApplyIdentityResultAsync(SpeakerIdentificationJob job, SpeakerAttribution result, CancellationToken token)
    {
        await _speakerReviewChanges.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ApplyIdentityResultCoreAsync(job, result, token).ConfigureAwait(false);
        }
        finally { _speakerReviewChanges.Release(); }
    }

    private async Task ApplyIdentityResultCoreAsync(SpeakerIdentificationJob job, SpeakerAttribution result, CancellationToken token)
    {
        bool accepted;
        lock (_feedbackGate)
        {
            accepted = !_disposed && IsRecognitionAttributionCurrent(job.Epoch) &&
                _latestIdentityJobs.TryGetValue(job.BubbleId, out long latest) && latest == job.Sequence;
            if (!accepted) return;
            if (result.PersonId is string personId && _deletedPeople.Contains(personId))
                result = result with { PersonId = null, SuggestedPersonId = null, PersonConfirmed = false,
                    Label = "Unidentified speaker", Confidence = 0 };
            result = SuppressRejectedGroupSuggestionLocked(result);
            _automaticSpeakerResults[job.BubbleId] = (job.Epoch, result);
            if (!string.IsNullOrEmpty(result.EvidenceId)) _evidenceBubbles[result.EvidenceId] = job.BubbleId;
            if (result.PersonId is not null) _personNames.TryAdd(result.PersonId, result.Label);
            SpeakerAttribution resolved = ResolveSpeakerLocked(job.BubbleId, result, job.Epoch);
            if (_speakerContext.TryGetValue(job.BubbleId, out SpeakerContextEntry? context))
                _speakerContext[job.BubbleId] = context with { Identity = SpeakerIdentity(resolved with { IsMixed = context.IsMixed }) };
            PruneIdentityMetadataLocked();
        }
        if (!string.IsNullOrEmpty(job.VoiceKey)) OnGroupSpeakerIdentified(job.VoiceKey, result, job.Epoch);
        await PublishIdentityUpdateAsync(job.BubbleId, job.Epoch, token).ConfigureAwait(false);
        // A committed bubble may have first appeared unnamed. Counting a later
        // match uses the same translation ID, so it cannot create a duplicate.
        await UpdateLateIdentityTranslationCountAsync(job.BubbleId, job.Epoch, token).ConfigureAwait(false);
    }

    private async Task PublishIdentityUpdateAsync(long bubbleId, long epoch, CancellationToken token)
    {
        await _displayPublicationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            TranslationDisplay display;
            lock (_feedbackGate)
            {
                if (!IsRecognitionAttributionCurrent(epoch) || !_publishedDisplays.TryGetValue(bubbleId, out display)) return;
                // Use the newest published text, never the source/translation
                // captured by an older identification job.
                display = ResolveDisplayIdentityLocked(display);
                _publishedDisplays[bubbleId] = display;
            }
            if (!IncomingSourceFilter.EvaluateChinese(display.Source, CurrentSettings().IncomingChineseOnly).Allowed) return;
            await _translation(display).ConfigureAwait(false);
        }
        finally { _displayPublicationGate.Release(); }
    }

    private Task<bool> PublishTranslationDisplayAsync(TranslationDisplay display) => PublishSourceFilteredDisplayAsync(display, null);

    private async Task<bool> PublishSourceFilteredDisplayAsync(TranslationDisplay display, TranslationRequest? sourceRequest)
    {
        await _displayPublicationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Recheck inside the publication lease: an option can change while
            // HTTP or an earlier identity update is still completing.
            if (sourceRequest is TranslationRequest request && !await AdmitIncomingRequestAsync(request)) return false;
            if (!IncomingSourceFilter.EvaluateChinese(display.Source, CurrentSettings().IncomingChineseOnly).Allowed) return false;
            lock (_feedbackGate)
            {
                if (_publishedDisplays.TryGetValue(display.UtteranceId, out TranslationDisplay latest) && latest.At > display.At) return false;
                display = ResolveDisplayIdentityLocked(display);
                _publishedDisplays[display.UtteranceId] = display;
                PruneIdentityMetadataLocked();
            }
            await _translation(display).ConfigureAwait(false);
            return true;
        }
        finally { _displayPublicationGate.Release(); }
    }

    private TranslationDisplay ResolveDisplayIdentityLocked(TranslationDisplay display)
    {
        SpeakerAttribution speaker = new(display.Speaker, 0, false, 0)
        {
            EvidenceId = display.EvidenceId, PersonId = display.PersonId,
            SessionVoiceKey = display.SessionVoiceKey, PersonConfirmed = display.IsManuallyAttributed
        };
        speaker = ResolveSpeakerLocked(display.UtteranceId, speaker, display.SpeakerRecognitionEpoch);
        return display with
        {
            Speaker = speaker.Label, PersonId = speaker.PersonId, EvidenceId = speaker.EvidenceId,
            IsManuallyAttributed = IsConfirmedDisplayLocked(display.UtteranceId)
        };
    }

    private async Task UpdateLateIdentityTranslationCountAsync(long bubbleId, long epoch, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await _personCountChanges.WaitAsync(token).ConfigureAwait(false); entered = true;
            string? personId; DateTimeOffset at;
            lock (_feedbackGate)
            {
                if (!IsRecognitionAttributionCurrent(epoch) || !_publishedCommittedTranslations.TryGetValue(bubbleId, out at) ||
                    !_automaticSpeakerResults.TryGetValue(bubbleId, out var automatic)) return;
                SpeakerAttribution resolved = ResolveSpeakerLocked(bubbleId, automatic.Speaker, epoch);
                personId = resolved.PersonConfirmed ? resolved.PersonId : null;
            }
            await _aiSpeakerIdentifier.RecordPersonTranslationAsync(_translationSessionId + ":" + bubbleId, personId, at, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { Log("Person matched, but its translation count could not be updated: " + error); }
        finally { if (entered) _personCountChanges.Release(); }
    }

    private void PruneIdentityMetadataLocked()
    {
        long oldest = Interlocked.Read(ref _nextUtteranceId) - 250;
        foreach (long id in _latestIdentityJobs.Keys.Where(id => id < oldest).ToArray()) _latestIdentityJobs.Remove(id);
        foreach (long id in _automaticSpeakerResults.Keys.Where(id => id < oldest).ToArray()) _automaticSpeakerResults.Remove(id);
        foreach (long id in _publishedDisplays.Keys.Where(id => id < oldest).ToArray()) _publishedDisplays.Remove(id);
        foreach (long id in _groupRejectedSuggestions.Keys.Where(id => id < oldest).ToArray()) _groupRejectedSuggestions.Remove(id);
        foreach (string id in _evidenceBubbles.Where(pair => pair.Value < oldest).Select(pair => pair.Key).ToArray()) _evidenceBubbles.Remove(id);
        while (_evidenceBubbles.Count > 1000) _evidenceBubbles.Remove(_evidenceBubbles.Keys.First());
    }

    private async Task QueueTranslationAsync(TranslationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await AdmitIncomingRequestAsync(request)) return;
        // Live opt-out also covers drafts already produced by a decoder before
        // its next settings snapshot. Finished sentences are never discarded.
        if (!request.IsCommitted && !CurrentSettings().IncomingSendEarlyPreviews) return;
        if (!request.IsCommitted && IsSuperseded(request)) return;
        CancellationTokenSource? cancel = null;
        lock (_latestUtteranceRevisions)
        {
            if (_activeTranslation is TranslationRequest active && !active.IsCommitted &&
                active.UtteranceId == request.UtteranceId && active.At < request.At)
                cancel = _activeTranslationCancellation;
        }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
        if (_translations.Writer.TryWrite(request)) return;
        if (!request.IsCommitted)
        {
            // Only expendable draft revisions are skipped under pressure. A final
            // sentence is admitted with backpressure instead of silently lost.
            Interlocked.Increment(ref _translationQueueDrops);
            return;
        }
        Log("Incoming subtitle queue is busy; retaining the finished sentence until the translator catches up.");
        await ReportHealthAsync("translation-backlog", "Translation is catching up…", true, onlyWhenHealthy: true).ConfigureAwait(false);
        await _translations.Writer.WriteAsync(request, token).ConfigureAwait(false);
    }

    internal static async Task<int> RunProfilePipelineOfflineChecksAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Incoming profile pipeline check failed: " + message);
            checks++;
        }
        var published = new List<TranslationDisplay>();
        using var identifier = new AiSpeakerIdentifier(Path.Combine(directory, "fingerprints.json"), Path.Combine(directory, "people.json"));
        using var service = new IncomingTranslationService(new AppSettings { IncomingSendEarlyPreviews = true }, (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask, display => { published.Add(display); return Task.CompletedTask; }, identifier);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        long epoch = Interlocked.Read(ref service._speakerRecognitionEpoch);
        service._nextUtteranceId = 5;
        var fallback = new SpeakerAttribution("Unidentified speaker", 0, false, 0)
        { CanLearn = false, EvidenceStart = start.AddSeconds(-2), EvidenceEnd = start };
        SpeakerAttribution initial = service.QueueCapturedSpeakerIdentification(1, "first text", [], fallback, new AppSettings(), start, epoch, "");
        Check(initial.EvidenceId.Length > 0 && !initial.CanLearn && published.Count == 0,
            "scheduling identity immediately returns label-only evidence without awaiting an extractor or publishing text");
        Check(service._identificationJobs.Reader.TryRead(out SpeakerIdentificationJob? firstJob), "identity inference has its own bounded queue");
        SpeakerAttribution updated = service.QueueCapturedSpeakerIdentification(1, "newer text", [], fallback, new AppSettings(), start.AddSeconds(1), epoch, "");
        Check(!service.IsIdentityJobCurrent(firstJob!), "an obsolete preview's identity work cannot replace a newer observation");
        Check(service._identificationJobs.Reader.TryRead(out SpeakerIdentificationJob? currentJob), "the current identity observation is queued separately");
        var newest = new TranslationDisplay("Unidentified speaker", "newest source", "newest translation", 1, start.AddSeconds(2))
        { EvidenceId = updated.EvidenceId, SpeakerRecognitionEpoch = epoch };
        await service.PublishTranslationDisplayAsync(newest).ConfigureAwait(false);
        SpeakerAttribution result = identifier.AddSyntheticFeedbackEvidence(null, "", fallback with { Label = "Named person", PersonId = "person-a" }, canLearn: false);
        await service.ApplyIdentityResultAsync(currentJob!, result, CancellationToken.None).ConfigureAwait(false);
        Check(published.Count == 2 && published[^1].Source == "newest source" && published[^1].Translation == "newest translation" &&
            published[^1].Speaker == "Named person" && published[^1].EvidenceId == result.EvidenceId,
            "late matching patches names/evidence on the latest published translation, never stale preview text");
        Check(service._evidenceBubbles.ContainsKey(initial.EvidenceId) && service._evidenceBubbles.ContainsKey(updated.EvidenceId) &&
            service._evidenceBubbles[result.EvidenceId] == 1, "original and new evidence IDs remain aliases for the same bubble");
        service._manualSpeakers[1] = new(false, "Manual choice", "person-manual", "Manual person", true);
        await service.ApplyIdentityResultAsync(currentJob!, result, CancellationToken.None).ConfigureAwait(false);
        Check(published[^1].Speaker == "Manual person" && published[^1].IsManuallyAttributed,
            "manual labels remain authoritative after an automatic result arrives");
        service._manualSpeakers.Remove(1);
        service._deletedPeople.Add("person-a");
        await service.ApplyIdentityResultAsync(currentJob!, result, CancellationToken.None).ConfigureAwait(false);
        Check(published[^1].PersonId is null, "a deleted person cannot be resurrected by delayed model output");
        int beforeEpochChange = published.Count;
        Interlocked.Increment(ref service._speakerRecognitionEpoch);
        await service.ApplyIdentityResultAsync(currentJob!, result, CancellationToken.None).ConfigureAwait(false);
        Check(published.Count == beforeEpochChange, "results from a previous recognition epoch cannot patch subtitles");
        await service.PublishTranslationDisplayAsync(newest with { Source = "obsolete", Translation = "obsolete", At = start }).ConfigureAwait(false);
        Check(published.Count == beforeEpochChange, "an older text revision cannot replace a newer published bubble");

        for (int index = 0; index < 8; index++)
            Check(service._translations.Writer.TryWrite(new("queued", fallback, 10 + index, start)), "the bounded text queue accepts its committed capacity");
        var committed = new TranslationRequest("must retain", fallback, 50, start);
        service._latestUtteranceRevisions[50] = start.AddSeconds(2);
        Check(!service.IsSuperseded(committed) && service.IsSuperseded(committed with { IsCommitted = false }),
            "a newer draft supersedes obsolete previews but never invalidates a completed sentence");
        Task queued = service.QueueTranslationAsync(committed, CancellationToken.None);
        Check(!queued.IsCompleted && service.HealthSnapshot.Stage == "translation-backlog", "committed backpressure is visible instead of silently dropping a sentence");
        Check(service._translations.Reader.TryRead(out _), "consuming one request makes room");
        await queued.ConfigureAwait(false);
        Check(service._translations.Reader.Count == 8 && service._translations.Reader.TryPeek(out _), "the retained committed sentence enters the bounded queue");
        var draft = new TranslationRequest("expendable", fallback, 60, start) { IsCommitted = false };
        await service.QueueTranslationAsync(draft, CancellationToken.None).ConfigureAwait(false);
        Check(service._translations.Reader.Count == 8 && service._translationQueueDrops == 1,
            "a full queue skips an expendable preview without replacing committed work");
        service._activeTranslation = draft;
        using var previewCancellation = new CancellationTokenSource();
        service._activeTranslationCancellation = previewCancellation;
        await service.QueueTranslationAsync(draft with { At = start.AddSeconds(1) }, CancellationToken.None).ConfigureAwait(false);
        Check(previewCancellation.IsCancellationRequested, "a newer revision cancels only the same bubble's active preview");
        using var finalCancellation = new CancellationTokenSource();
        service._activeTranslation = committed;
        service._activeTranslationCancellation = finalCancellation;
        await service.QueueTranslationAsync(new("other draft", fallback, 50, start.AddSeconds(1)) { IsCommitted = false }, CancellationToken.None).ConfigureAwait(false);
        Check(!finalCancellation.IsCancellationRequested, "new previews never cancel a committed translation");

        using var defaultIdentifier = new AiSpeakerIdentifier(Path.Combine(directory, "final-identities.json"), Path.Combine(directory, "final-people.json"));
        using var finalOnlyService = new IncomingTranslationService(new AppSettings { UseAiSpeakerRecognition = false },
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, defaultIdentifier);
        using var untouchedOperation = new CancellationTokenSource();
        finalOnlyService._activeTranslation = draft;
        finalOnlyService._activeTranslationCancellation = untouchedOperation;
        await finalOnlyService.QueueTranslationAsync(draft with { At = start.AddSeconds(1) }, CancellationToken.None).ConfigureAwait(false);
        Check(finalOnlyService._translations.Reader.Count == 0 && !untouchedOperation.IsCancellationRequested && finalOnlyService._translationQueueDrops == 0,
            "the default queue rejects an unfinished revision before altering active work or reporting a backlog");
        finalOnlyService._latestUtteranceRevisions[committed.UtteranceId] = start.AddSeconds(10);
        await finalOnlyService.QueueTranslationAsync(committed, CancellationToken.None).ConfigureAwait(false);
        Check(finalOnlyService._translations.Reader.TryRead(out TranslationRequest retainedFinal) && retainedFinal == committed,
            "the final-only backstop never drops a finished sentence, including one older than a speculative watermark");
        AppSettings optIn = finalOnlyService.CurrentSettings(); optIn.IncomingSendEarlyPreviews = true;
        finalOnlyService.UpdateSettings(optIn);
        await finalOnlyService.QueueTranslationAsync(draft with { UtteranceId = 70 }, CancellationToken.None).ConfigureAwait(false);
        Check(finalOnlyService._translations.Reader.TryRead(out TranslationRequest permittedDraft) && !permittedDraft.IsCommitted &&
            !finalOnlyService._captureRefreshNeeded && finalOnlyService._socket is null,
            "an explicit live opt-in permits drafts without opening audio or restarting capture");
        optIn.IncomingSendEarlyPreviews = false; finalOnlyService.UpdateSettings(optIn);
        await finalOnlyService.QueueTranslationAsync(draft with { UtteranceId = 71 }, CancellationToken.None).ConfigureAwait(false);
        await finalOnlyService.QueueTranslationAsync(committed with { UtteranceId = 71 }, CancellationToken.None).ConfigureAwait(false);
        Check(finalOnlyService._translations.Reader.TryRead(out retainedFinal) && retainedFinal.IsCommitted && retainedFinal.UtteranceId == 71 &&
            !finalOnlyService._translations.Reader.TryRead(out _),
            "live opt-out immediately ignores new drafts while allowing the same sentence's final through");
        Check(defaultIdentifier.ExtractorLoadAttempts == 0 && !defaultIdentifier.IsModelLoaded,
            "final-only admission fixtures never load voice identification");
        Check(identifier.ExtractorLoadAttempts == 0 && !identifier.IsModelLoaded,
            "all pipeline fixtures avoided model loading, audio capture and network requests");
        Console.WriteLine($"PASS: {checks} incoming profile pipeline checks; synthetic metadata only, no models, capture or network.");
        return checks;
    }
}

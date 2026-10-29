using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SherpaOnnx;

namespace IncomingTranslator;

internal sealed class AiSpeakerIdentifier : IDisposable, ISpeakerFeedbackHost
{
    private const int MaximumProfiles = 8;
    private const int MaximumCachedEvidence = 200;
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(5);
    private readonly string _profilesPath;
    private readonly SpeakerFeedbackStore _feedback;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _evidenceGate = new();
    private readonly Dictionary<string, EvidenceObservation> _evidence = new();
    private readonly Queue<string> _evidenceOrder = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<PendingSpeaker> _pending = new();
    private ProfileDatabase _database = new();
    private List<AiSpeakerProfile> _profiles = new();
    private SpeakerEmbeddingExtractor? _extractor;
    private string _loadedModel = "";
    private string _modelKey = "";
    private long _loadedLength;
    private DateTime _loadedWriteTime;
    private DateTimeOffset _lastSavedAt;
    private bool _needsLegacyBackup;
    private bool _databaseDirty;
    private int _recognitionEnabled = 1;
    private int _recognitionEpoch;
    private int _extractorLoadAttempts;
    private int _disposeRequested;
    private Task _disposalCompleted = Task.CompletedTask;

    public string LastDiagnostic { get; private set; } = "Neural speaker model has not been loaded.";
    public string LoadedModelPath => _loadedModel;
    public bool RecognitionEnabled => Volatile.Read(ref _recognitionEnabled) != 0 && Volatile.Read(ref _disposeRequested) == 0;
    public bool IsModelLoaded => _extractor is not null;
    public SpeakerFeedbackState? SpeakerFeedbackSnapshot
    {
        get
        {
            try
            {
                ThrowIfDisposed();
                return _feedback.State(IsModelLoaded ? _modelKey : null);
            }
            // A best-effort picker peek must never stop its window from opening. The async
            // state API still reports the actionable closed/corrupt-data error to the UI.
            catch (ObjectDisposedException) { return null; }
            catch (InvalidDataException) { return null; }
        }
    }
    public int ProfileCount => _profiles.Count;
    internal Task DisposalCompleted => _disposalCompleted;
    internal int ExtractorLoadAttempts => Volatile.Read(ref _extractorLoadAttempts);

    public AiSpeakerIdentifier() : this(Path.Combine(AppPaths.Root, "incoming-ai-speaker-profiles.json"), Path.Combine(AppPaths.Root, "incoming-speaker-feedback.json")) { }

    internal AiSpeakerIdentifier(string profilesPath, string feedbackPath, Func<DateTimeOffset>? clock = null)
    {
        _profilesPath = Path.GetFullPath(profilesPath);
        if (string.Equals(_profilesPath, Path.GetFullPath(feedbackPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Anonymous profiles and saved-person feedback must use separate files.", nameof(feedbackPath));
        _feedback = new SpeakerFeedbackStore(feedbackPath, _profilesPath);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        LoadDatabase();
    }

    public async Task SetRecognitionEnabledAsync(bool enabled, CancellationToken token = default)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        if (!enabled)
        {
            // Block queued recognition immediately. Unloading waits for any already-running native
            // embedding under the same gate, so its stream can never outlive its extractor.
            Volatile.Write(ref _recognitionEnabled, 0);
            Interlocked.Increment(ref _recognitionEpoch);
        }
        // Once an off request has been admitted, finish releasing its model even if the caller's
        // token is canceled. Cancellation must not leave an "off" model resident indefinitely.
        await _gate.WaitAsync(enabled ? token : CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (enabled)
            {
                Volatile.Write(ref _recognitionEnabled, 1);
                LastDiagnostic = IsModelLoaded
                    ? "AI speaker recognition is enabled; its local voice model is loaded."
                    : "AI speaker recognition is enabled. Its local model will load on the next clear speech sample.";
                return;
            }
            Volatile.Write(ref _recognitionEnabled, 0);
            try
            {
                UnloadExtractor();
                LastDiagnostic = "AI speaker recognition is off. Its model is unloaded; saved names and voice profiles stay on this PC.";
            }
            catch (Exception error)
            {
                LastDiagnostic = "Speaker recognition is off, but its model could not finish unloading: " + error.Message;
                throw;
            }
            finally
            {
                ClearPendingEnrollment();
                ClearTrainableEvidence();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<SpeakerAttribution> IdentifyAsync(
        float[] samples,
        SpeakerAttribution fallback,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        // Every displayed observation can be labelled, even when no usable fingerprint was produced.
        fallback = fallback with { EvidenceId = "", PersonId = null, SuggestedPersonId = null, PersonConfirmed = false };
        if (!settings.UseAiSpeakerRecognition)
        {
            if (RecognitionEnabled || IsModelLoaded)
                await SetRecognitionEnabledAsync(false, cancellationToken).ConfigureAwait(false);
            return RecognitionOff(fallback);
        }
        if (!RecognitionEnabled) return RecognitionOff(fallback);
        int observationEpoch = Volatile.Read(ref _recognitionEpoch);
        if (!File.Exists(settings.SpeakerModelPath))
            return Unknown(fallback, "Speaker model is missing; choose or download a model in Options.");
        if (fallback.IsMixed)
            return Unknown(fallback, "Speaker attribution withheld: audio contains a changing or mixed voice.");
        if (!fallback.CanLearn)
            return Unknown(fallback, $"Too little voiced audio to learn identity ({fallback.SpeechSeconds:0.00}s). Position is not used as identity.");

        AudioQuality quality = PrepareAudio(samples);
        if (quality.Samples.Length < 11200 || quality.SpeechSeconds < 0.55)
            return Unknown(fallback, "Speaker attribution withheld: too little clear speech.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!RecognitionPermitted(observationEpoch)) return RecognitionOff(fallback);
            EnsureExtractor(settings.SpeakerModelPath);
            int maximumProfiles = Math.Clamp(settings.MaxSpeakerProfiles, 2, MaximumProfiles);
            using OnlineStream stream = _extractor!.CreateStream();
            stream.AcceptWaveform(16000, quality.Samples);
            stream.InputFinished();
            if (!_extractor.IsReady(stream))
                return Unknown(fallback, "Speaker model needs a longer speech sample.");

            float[] embedding = await Task.Run(() => _extractor.Compute(stream), cancellationToken);
            ThrowIfDisposed();
            if (!RecognitionPermitted(observationEpoch)) return RecognitionOff(fallback);
            if (embedding.Length == 0 || embedding.Any(value => !float.IsFinite(value)) || embedding.Sum(value => (double)value * value) <= 1e-9)
                return Unknown(fallback, "Speaker model returned an invalid voice fingerprint.");
            Normalize(embedding);
            bool enoughToLearn = quality.SpeechSeconds >= 1.35 && quality.ClippedFraction < 0.015;
            fallback = CacheEvidence(fallback, embedding, _modelKey, quality.SpeechSeconds, quality.ClippedFraction, enoughToLearn);
            var ranked = _profiles
                .OrderBy(profile => profile.Id).Take(maximumProfiles)
                .Where(profile => profile.Embedding.Length == embedding.Length)
                .Select(profile => (Profile: profile, Score: Cosine(embedding, profile.Embedding)))
                .OrderByDescending(item => item.Score)
                .ToList();

            double threshold = Math.Clamp(settings.SpeakerMatchThreshold, 0.35, 0.85);
            double best = ranked.Count > 0 ? ranked[0].Score : -1;
            double runnerUp = ranked.Count > 1 ? ranked[1].Score : -1;
            double margin = best - runnerUp;

            // Human-confirmed examples are separate immutable anchors. Automatic guesses never train
            // them, and the anonymous-profile cap must not hide an explicitly saved person.
            var named = RankConfirmed(embedding, _modelKey);
            double namedBest = named.Count > 0 ? named[0].Score : -1;
            double namedMargin = named.Count > 1 ? namedBest - named[1].Score : 2;
            double required = NamedRequiredThreshold(threshold, named.Count > 0 ? named[0].References : 0, quality.SpeechSeconds);
            if (named.Count > 0 && !named[0].Vetoed && namedBest >= required && namedMargin >= 0.06 && best < namedBest + 0.12)
            {
                NamedMatch selected = named[0];
                LastDiagnostic = $"Saved speaker {selected.Person.Name}; confirmedMatch={namedBest:0.00}, margin={namedMargin:0.00}, clearSpeech={quality.SpeechSeconds:0.0}s. No automatic profile adaptation.";
                return CacheResult(fallback with { Label = selected.Person.Name, PersonId = selected.Person.Id, SuggestedPersonId = selected.Person.Id,
                    PersonConfirmed = false, Confidence = namedBest, IsMixed = false, Diagnostic = LastDiagnostic });
            }
            bool matched = best >= threshold + (quality.SpeechSeconds < 1 ? 0.06 : 0) &&
                (ranked.Count == 1 || margin >= 0.06);
            if (matched)
            {
                AiSpeakerProfile selected = ranked[0].Profile;
                // Freeze guessed centroids. Only explicit corrections may reinforce named people;
                // repeated confident mistakes (especially music) must not poison any stored voice.
                LastDiagnostic = $"Speaker {selected.Id}; match={best:0.00}, margin={margin:0.00}, clearSpeech={quality.SpeechSeconds:0.0}s, profiles={Math.Min(_profiles.Count, maximumProfiles)}/{maximumProfiles}.";
                return CacheResult(fallback with { Label = $"Speaker {selected.Id}", Confidence = best, IsMixed = false, Diagnostic = LastDiagnostic });
            }

            if (!enoughToLearn)
                return Unknown(fallback, $"No confident match; clearSpeech={quality.SpeechSeconds:0.0}s, nearest={best:0.00}. Short speech cannot enroll a new speaker.");
            // A near miss can be the same person's changing pitch or microphone level. Do not create another ID for it.
            if ((ranked.Count > 0 || named.Count > 0) && Math.Max(best, namedBest) > threshold - 0.10)
                return Unknown(fallback, $"Ambiguous voice; nearest={best:0.00}, margin={margin:0.00}. No new profile created.");
            if (_profiles.Count >= maximumProfiles)
                return Unknown(fallback, $"Expected speaker limit ({maximumProfiles}) reached. This voice is not a confident match; adjust the expected count or reset learned speakers for a new group.");

            DateTimeOffset now = _clock();
            _pending.RemoveAll(candidate => now - candidate.LastSeenAt > TimeSpan.FromSeconds(40));
            var candidateMatches = _pending
                .Where(candidate => candidate.Embedding.Length == embedding.Length)
                .Select(candidate => (Candidate: candidate, Score: Cosine(embedding, candidate.Embedding)))
                .OrderByDescending(item => item.Score).ToList();
            PendingSpeaker pending;
            if (candidateMatches.Count > 0 && candidateMatches[0].Score >= Math.Max(0.60, threshold + 0.08))
                pending = candidateMatches[0].Candidate;
            else
            {
                if (_pending.Count >= 3) _pending.RemoveAt(0);
                pending = new PendingSpeaker { Embedding = embedding.ToArray() };
                _pending.Add(pending);
            }
            // Repeated transcription of the same audio is only one observation, not evidence for a new person.
            bool independent = pending.LastEvidenceEnd == default || fallback.EvidenceEnd == default ||
                fallback.EvidenceStart >= pending.LastEvidenceEnd.AddMilliseconds(-120);
            if (independent)
            {
                pending.Count++;
                pending.SpeechSeconds += quality.SpeechSeconds;
                Blend(pending.Embedding, embedding, 1f / Math.Max(1, pending.Count));
                pending.LastEvidenceEnd = fallback.EvidenceEnd;
            }
            pending.LastSeenAt = now;

            // The first voice can be enrolled from one long, clean utterance; every later identity needs repeated evidence.
            bool canEnroll = _profiles.Count == 0
                ? quality.SpeechSeconds >= 2.0 || pending.Count >= 2 && pending.SpeechSeconds >= 3.0
                : pending.Count >= 3 && pending.SpeechSeconds >= 4.5;
            if (!canEnroll)
                return Unknown(fallback, $"Learning a possible new voice: {pending.Count} independent samples, {pending.SpeechSeconds:0.0}s clear speech; nearest={best:0.00}." );

            var profile = new AiSpeakerProfile
            {
                Id = NextId(), Embedding = pending.Embedding.ToArray(), Samples = pending.Count,
                LastSeenUtc = now
            };
            bool wasDirty = _databaseDirty;
            _profiles.Add(profile);
            _pending.Remove(pending);
            _databaseDirty = true;
            try { SaveDatabase(force: true); }
            catch
            {
                _profiles.Remove(profile);
                _pending.Add(pending);
                _databaseDirty = wasDirty;
                throw;
            }
            LastDiagnostic = $"Learned Speaker {profile.Id} from {pending.Count} sample(s), {pending.SpeechSeconds:0.0}s clear speech; profiles={_profiles.Count}/{maximumProfiles}.";
            return CacheResult(fallback with { Label = $"Speaker {profile.Id}", Confidence = 0.75, IsMixed = false, Diagnostic = LastDiagnostic });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeRequested) != 0) { throw; }
        catch (Exception exception)
        {
            return Unknown(fallback, $"Speaker recognition failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally { _gate.Release(); }
    }

    public async Task ResetProfilesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            BackupProfiles();
            ProfileDatabase previousDatabase = _database;
            List<AiSpeakerProfile> previousProfiles = _profiles;
            bool wasDirty = _databaseDirty;
            _database = JsonSerializer.Deserialize<ProfileDatabase>(JsonSerializer.Serialize(_database)) ?? new();
            _profiles = new();
            if (string.IsNullOrEmpty(_modelKey)) _database.Models.Clear();
            else if (_database.Models.TryGetValue(_modelKey, out ModelProfiles? model)) model.Profiles = _profiles;
            try { _databaseDirty = true; SaveDatabase(force: true); }
            catch
            {
                _database = previousDatabase; _profiles = previousProfiles; _databaseDirty = wasDirty;
                throw;
            }
            _pending.Clear();
            LastDiagnostic = "Automatic anonymous speakers cleared; saved people and confirmed voice samples were kept.";
        }
        finally { _gate.Release(); }
    }

    private SpeakerAttribution Unknown(SpeakerAttribution fallback, string reason)
    {
        LastDiagnostic = reason;
        SpeakerAttribution result = fallback with
        {
            Label = fallback.IsMixed ? "Overlapping/uncertain voices" : "Unidentified speaker",
            PersonId = null, SuggestedPersonId = null, PersonConfirmed = false, Confidence = 0, Diagnostic = reason
        };
        return string.IsNullOrEmpty(result.EvidenceId)
            ? CacheEvidence(result with { CanLearn = false }, null, "", 0, 0, false)
            : CacheResult(result);
    }

    private bool RecognitionPermitted(int observationEpoch) => RecognitionEnabled && Volatile.Read(ref _recognitionEpoch) == observationEpoch;

    private SpeakerAttribution RecognitionOff(SpeakerAttribution fallback)
    {
        if (!RecognitionEnabled)
            LastDiagnostic = "AI speaker recognition is off; this sentence can be labelled manually but cannot teach a voice.";
        return RecordLabelOnlyObservation(fallback);
    }

    public SpeakerAttribution RecordLabelOnlyObservation(SpeakerAttribution fallback)
    {
        ThrowIfDisposed();
        // An old service settings snapshot can still describe an off turn after the user enables
        // recognition. Recording its metadata must never alter the NEW model admission state.
        return CacheEvidence(fallback with
        {
            EvidenceId = "", Label = "Unidentified speaker", PersonId = null, SuggestedPersonId = null, PersonConfirmed = false, Confidence = 0, CanLearn = false,
            Diagnostic = "AI speaker recognition was not applied to this sentence; it can be labelled but cannot teach a voice."
        }, null, "", 0, 0, false);
    }

    public async Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token);
        try { ThrowIfDisposed(); return _feedback.State(IsModelLoaded ? _modelKey : null); }
        finally { _gate.Release(); }
    }

    public async Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            SpeakerPerson saved = _feedback.SavePerson(name, personId);
            lock (_evidenceGate)
                foreach (EvidenceObservation observation in _evidence.Values)
                    if (observation.Current.PersonId == saved.Id)
                        observation.Current = observation.Current with { Label = saved.Name };
            return saved;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            _feedback.DeletePerson(personId);
            lock (_evidenceGate)
                foreach (EvidenceObservation observation in _evidence.Values)
                    if (observation.Current.PersonId == personId)
                        observation.Current = observation.Current with { PersonId = null, Label = "Unidentified speaker", Confidence = 0 };
        }
        finally { _gate.Release(); }
    }

    public async Task<SpeakerPerson> SetPersonPhotoAsync(string personId, string? sourcePath, CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token);
        try { ThrowIfDisposed(); token.ThrowIfCancellationRequested(); return _feedback.SetPersonPhoto(personId, sourcePath); }
        finally { _gate.Release(); }
    }

    public async Task RecordPersonTranslationAsync(string translationId, string? personId, DateTimeOffset at, CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); token.ThrowIfCancellationRequested();
            // Successful subtitles can arrive on the window dispatcher. Persist aggregate
            // statistics away from it; they must not make the live conversation stutter.
            await Task.Run(() => _feedback.RecordTranslation(translationId, personId, at), token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token);
        try { ThrowIfDisposed(); _feedback.Acknowledge(); }
        finally { _gate.Release(); }
    }

    public async Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            return ApplyCorrection(evidenceId, personId, specialLabel, null, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<SpeakerFeedbackResult> ReviewSpeakerSuggestionAsync(string evidenceId, string suggestedPersonId, bool confirmed, CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            _feedback.RequirePerson(suggestedPersonId);
            EvidenceObservation? observation;
            lock (_evidenceGate) { PruneEvidence(); _evidence.TryGetValue(evidenceId, out observation); }
            SpeakerFeedbackContribution existing = ResolveFeedbackContribution(observation, evidenceId);
            SpeakerSuggestionReview? stored = existing.Review;
            string? original = stored?.SuggestedPersonId ?? observation?.OriginalSuggestedPersonId;
            if (original != suggestedPersonId)
                return new(false, "This suggestion is no longer available for review. Choose a person manually instead.",
                    observation?.Current.PersonId, observation?.Current.Label ?? "Unidentified speaker", false, false);
            return ApplyCorrection(evidenceId, confirmed ? suggestedPersonId : null, confirmed ? null : "unidentified", suggestedPersonId, token);
        }
        finally { _gate.Release(); }
    }

    // Groups may reuse a clean turn's saved-person suggestion for a later timestamped utterance.
    // Registering that suggestion is metadata only: no inference, files, references or statistics.
    // Its first identity is immutable, even if a later guess or manual relabel changes the display.
    internal bool RegisterSpeakerSuggestion(string evidenceId, string personId)
    {
        ThrowIfDisposed();
        SpeakerPerson person = _feedback.RequirePerson(personId);
        lock (_evidenceGate)
        {
            PruneEvidence();
            if (!_evidence.TryGetValue(evidenceId, out EvidenceObservation? observation)) return false;
            if (observation.OriginalSuggestedPersonId is not null && observation.OriginalSuggestedPersonId != personId) return false;
            if (observation.IsManuallyAttributed) return observation.OriginalSuggestedPersonId == personId;
            observation.OriginalSuggestedPersonId = personId;
            observation.Current = observation.Current with { SuggestedPersonId = personId, PersonId = personId, Label = person.Name, PersonConfirmed = false };
            return true;
        }
    }

    private SpeakerFeedbackResult ApplyCorrection(string evidenceId, string? personId, string? specialLabel, string? reviewedPersonId, CancellationToken token)
    {
        SpeakerPerson? person = personId is null ? null : _feedback.RequirePerson(personId);
        string label = person?.Name ?? SpecialLabel(specialLabel);
        EvidenceObservation? observation;
        lock (_evidenceGate) { PruneEvidence(); _evidence.TryGetValue(evidenceId, out observation); }
        SpeakerFeedbackContribution previous = ResolveFeedbackContribution(observation, evidenceId);
        string anchorKey = previous.EvidenceId ?? observation?.AnchorKey ?? evidenceId;
        string? suggestedId = previous.Review?.SuggestedPersonId ?? observation?.OriginalSuggestedPersonId ?? reviewedPersonId;
        // A deleted saved person cannot own a new review. Manual labels remain usable.
        if (suggestedId is not null && !_feedback.State().People.Any(saved => saved.Id == suggestedId)) suggestedId = null;
        bool? confirmed = suggestedId is null ? null : personId == suggestedId;
        bool acknowledged = _feedback.State().InstructionAcknowledged;
        bool clear = RecognitionEnabled && acknowledged && observation is { CanLearn: true, Embedding: not null };
        bool positiveFull = clear && person is not null && !_feedback.CanStoreContribution(anchorKey, person.Id, observation!.ModelSha);
        bool negativeFull = clear && confirmed == false && label != "Music / recording" &&
            !_feedback.CanStoreNegativeContribution(anchorKey, suggestedId!, observation!.ModelSha);
        bool learn = clear && person is not null && !positiveFull;
        bool learnNegative = clear && confirmed == false && label != "Music / recording" && !negativeFull;
        string revisionToken = Guid.NewGuid().ToString("N");
        string[] aliases = previous.Aliases.Where(id => id != evidenceId && id != anchorKey)
            .Distinct(StringComparer.Ordinal).TakeLast(30).Append(anchorKey).Append(evidenceId).Distinct(StringComparer.Ordinal).ToArray();
        SpeakerFeedbackAnchor MakeAnchor(string target) => new()
        {
            EvidenceId = anchorKey, PersonId = target, ModelSha = observation!.ModelSha,
            Embedding = observation.Embedding!.ToArray(), ObservedAtUtc = observation.ObservedAtUtc,
            SpeechSeconds = observation.SpeechSeconds, ClippedFraction = observation.ClippedFraction,
            EvidenceStart = observation.Current.EvidenceStart, EvidenceEnd = observation.Current.EvidenceEnd,
            RevisionToken = revisionToken, EvidenceAliases = aliases
        };
        SpeakerFeedbackAnchor? positive = learn ? MakeAnchor(person!.Id) :
            person is not null && previous.Positive?.PersonId == person.Id ? previous.Positive with { RevisionToken = revisionToken, EvidenceAliases = aliases } : null;
        SpeakerFeedbackAnchor? negative = learnNegative ? MakeAnchor(suggestedId!) :
            confirmed == false && previous.Negative is not null && previous.Negative.PersonId == suggestedId && label != "Music / recording"
                ? previous.Negative with { RevisionToken = revisionToken, EvidenceAliases = aliases } : null;
        SpeakerSuggestionReview? review = confirmed.HasValue ? new()
        {
            EvidenceId = anchorKey, SuggestedPersonId = suggestedId!, Confirmed = confirmed.Value,
            ReviewedAtUtc = _clock(), RevisionToken = revisionToken, EvidenceAliases = aliases
        } : null;
        // The same clicked decision is one review. Do not rewrite a bounded reference or add an
        // undo step merely because two UI events submitted the same action before its redraw.
        if (reviewedPersonId is not null && review is not null && previous.Review is not null && previous.Review.Confirmed == review.Confirmed &&
            observation is not null && observation.Current.PersonId == personId && observation.Current.Label == label &&
            (positive is null || previous.Positive?.PersonId == positive.PersonId) &&
            (negative is null || previous.Negative?.PersonId == negative.PersonId))
            return new(false, "This suggestion was already reviewed.", personId, label, observation.History.Count > 0)
            { SuggestedPersonId = suggestedId, SuggestionConfirmed = confirmed };

        token.ThrowIfCancellationRequested();
        _feedback.ReplaceFeedback(anchorKey, new(positive, negative, review), person?.Id, person is null ? null : _clock());
        bool canUndo = false;
        if (observation is not null)
        {
            lock (_evidenceGate)
            {
                observation.History.Add(new(observation.Current, previous, review?.RevisionToken ?? positive?.RevisionToken ?? negative?.RevisionToken,
                    observation.IsManuallyAttributed));
                if (observation.History.Count > 16) observation.History.RemoveAt(0);
                observation.AnchorKey = anchorKey;
                observation.Current = observation.Current with { PersonId = person?.Id, Label = label, Confidence = person is null ? 0 : 1,
                    PersonConfirmed = person is not null, SuggestedPersonId = suggestedId };
                observation.IsManuallyAttributed = true;
                canUndo = true;
            }
        }
        string message = learn && learnNegative
            ? "Person updated; a clear voice example was saved and the wrong suggestion was remembered."
            : learn ? "Label corrected and this clean voice sample was confirmed for " + person!.Name + "."
            : learnNegative ? "Suggestion rejected; this clear voice example was remembered as not " + _feedback.RequirePerson(suggestedId!).Name + "."
            : observation is null ? "Label corrected only. This voice evidence expired or was not retained; no new learning was performed. Any previous wrong assignment was removed."
            : !RecognitionEnabled ? "Label corrected only. AI speaker recognition is off; no new voice sample was learned and no model was loaded. Any previous wrong assignment was removed."
            : !acknowledged ? "Label corrected only. Confirm the one-time speaker-learning explanation before corrections reinforce voices."
            : positiveFull || negativeFull ? "Label corrected only. The bounded voice collection is full; no additional example was stored. Existing examples were preserved."
            : person is null && suggestedId is null ? "Label corrected only. Music / recordings and unidentified speech never train a person's voice; any wrong sample assignment was removed."
            : "Label corrected only. This sample is too short, mixed, clipped, or has no usable fingerprint; it cannot safely teach a person's voice.";
        LastDiagnostic = message;
        return new(learn, message, person?.Id, label, canUndo)
        { SuggestedPersonId = suggestedId, SuggestionConfirmed = confirmed, NegativeLearned = learnNegative };
    }

    private SpeakerFeedbackContribution ResolveFeedbackContribution(EvidenceObservation? observation, string evidenceId)
    {
        SpeakerFeedbackContribution contribution = _feedback.FeedbackContribution(observation?.AnchorKey ?? evidenceId);
        if (contribution.EvidenceId is null && RecognitionEnabled && observation?.Embedding is not null &&
            FindEquivalentContribution(observation) is SpeakerFeedbackAnchor equivalent)
            contribution = _feedback.FeedbackContribution(equivalent.EvidenceId);
        return contribution;
    }

    public async Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            EvidenceObservation? observation;
            lock (_evidenceGate) { PruneEvidence(); _evidence.TryGetValue(evidenceId, out observation); }
            if (observation is null)
                return new(false, "This correction can no longer be undone: its temporary voice evidence expired. Saved samples have not been changed.", null, "Unidentified speaker", false, false);
            if (observation.History.Count == 0)
                return new(false, "There is no correction to undo for this sentence.", observation.Current.PersonId, observation.Current.Label, false, false);
            UndoEntry undo = observation.History[^1];
            SpeakerFeedbackContribution current = _feedback.FeedbackContribution(observation.AnchorKey);
            if (current.RevisionToken != undo.ExpectedRevisionToken)
                return new(false, "A newer correction changed this same voice sample. Undo was not applied, so the newer correction is preserved.", observation.Current.PersonId, observation.Current.Label, false, false);
            if (undo.Previous.Positive is not null) _feedback.RequirePerson(undo.Previous.Positive.PersonId);
            if (undo.Previous.Negative is not null) _feedback.RequirePerson(undo.Previous.Negative.PersonId);
            if (undo.Previous.Review is not null) _feedback.RequirePerson(undo.Previous.Review.SuggestedPersonId);
            SpeakerAttribution restored = undo.PreviousAttribution;
            if (restored.PersonId is not null)
                restored = restored with { Label = _feedback.RequirePerson(restored.PersonId).Name };
            token.ThrowIfCancellationRequested();
            _feedback.ReplaceFeedback(observation.AnchorKey, undo.Previous);
            lock (_evidenceGate)
            {
                observation.History.RemoveAt(observation.History.Count - 1);
                observation.Current = restored;
                observation.IsManuallyAttributed = undo.PreviousManuallyAttributed;
            }
            string message = "Correction undone; only this sample's previous assignment was restored.";
            LastDiagnostic = message;
            return new(RecognitionEnabled && undo.Previous.Positive is not null, message, restored.PersonId, restored.Label, observation.History.Count > 0)
            { SuggestedPersonId = restored.SuggestedPersonId, SuggestionConfirmed = undo.Previous.Review?.Confirmed,
                NegativeLearned = RecognitionEnabled && undo.Previous.Negative is not null, IsManuallyAttributed = undo.PreviousManuallyAttributed };
        }
        finally { _gate.Release(); }
    }

    private static string SpecialLabel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "music" or "music / recording" => "Music / recording",
        "unidentified" or "unknown" or "unidentified speaker" => "Unidentified speaker",
        _ => throw new ArgumentException("Choose a saved person, Music / recording, or Unidentified speaker.", nameof(value))
    };

    private SpeakerFeedbackAnchor? FindEquivalentContribution(EvidenceObservation observation)
    {
        if (observation.Current.EvidenceStart == default || observation.Current.EvidenceEnd <= observation.Current.EvidenceStart) return null;
        return _feedback.Anchors(observation.ModelSha).Concat(_feedback.NegativeAnchors(observation.ModelSha)).FirstOrDefault(anchor =>
        {
            if (anchor.EvidenceStart == default || anchor.EvidenceEnd <= anchor.EvidenceStart || anchor.Embedding.Length != observation.Embedding!.Length) return false;
            double overlap = (Min(anchor.EvidenceEnd, observation.Current.EvidenceEnd) - Max(anchor.EvidenceStart, observation.Current.EvidenceStart)).TotalSeconds;
            double shortest = Math.Min((anchor.EvidenceEnd - anchor.EvidenceStart).TotalSeconds, (observation.Current.EvidenceEnd - observation.Current.EvidenceStart).TotalSeconds);
            return overlap > 0 && overlap / shortest >= 0.85 && Cosine(anchor.Embedding, observation.Embedding) >= 0.98;
        });
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    private List<NamedMatch> RankConfirmed(float[] embedding, string modelSha)
    {
        var people = _feedback.State().People.ToDictionary(person => person.Id);
        var negative = _feedback.NegativeAnchors(modelSha).Where(anchor => anchor.Embedding.Length == embedding.Length)
            .GroupBy(anchor => anchor.PersonId).ToDictionary(group => group.Key, group => group.Max(anchor => Cosine(embedding, anchor.Embedding)));
        return _feedback.Anchors(modelSha).Where(anchor => anchor.Embedding.Length == embedding.Length)
            .GroupBy(anchor => anchor.PersonId)
            .Select(group =>
            {
                double[] scores = group.Select(anchor => Cosine(embedding, anchor.Embedding)).OrderDescending().ToArray();
                // One unusually similar example cannot win over the rest of a person's references.
                // Require two agreeing examples when available; a one-example profile has a stricter
                // admission floor below. This does not average away natural changes in the voice.
                double score = scores.Length > 1 ? scores[1] : scores[0];
                double rejected = negative.GetValueOrDefault(group.Key, -1);
                // A negative is local to this person and exact embedding model. Veto only near
                // repeats of a reviewed mistake, never force the sample onto another saved person.
                bool veto = rejected >= 0.88 && rejected >= scores[0] - 0.02;
                return new NamedMatch(people[group.Key], score, scores.Length, veto);
            })
            .OrderByDescending(match => match.Score).ToList();
    }

    private static double NamedRequiredThreshold(double threshold, int references, double speechSeconds) =>
        (references < 2 ? Math.Max(0.62, threshold + 0.10) : threshold) + (speechSeconds < 1 ? 0.06 : 0);

    private SpeakerAttribution CacheEvidence(SpeakerAttribution attribution, float[]? embedding, string modelSha, double speechSeconds, double clippedFraction, bool canLearn)
    {
        string id = Guid.NewGuid().ToString("N");
        bool valid = RecognitionEnabled && embedding is { Length: > 0 and <= 4096 } && embedding.All(float.IsFinite) && embedding.Sum(value => (double)value * value) > 1e-9;
        float[]? immutableEmbedding = valid ? embedding!.ToArray() : null;
        if (immutableEmbedding is not null) Normalize(immutableEmbedding);
        string key = valid ? SpeakerFeedbackStore.NormalizeModelSha(modelSha) : "";
        bool eligible = valid && canLearn && attribution.CanLearn && !attribution.IsMixed && double.IsFinite(speechSeconds) && speechSeconds >= 1.35 && double.IsFinite(clippedFraction) && clippedFraction is >= 0 and < 0.015;
        attribution = attribution with { EvidenceId = id, CanLearn = eligible, SpeechSeconds = speechSeconds };
        var observation = new EvidenceObservation(id, attribution, immutableEmbedding, key, _clock(), speechSeconds, clippedFraction, eligible);
        lock (_evidenceGate)
        {
            ThrowIfDisposed();
            PruneEvidence();
            _evidence[id] = observation;
            _evidenceOrder.Enqueue(id);
            PruneEvidence();
        }
        return attribution;
    }

    private SpeakerAttribution CacheResult(SpeakerAttribution attribution)
    {
        lock (_evidenceGate)
            if (_evidence.TryGetValue(attribution.EvidenceId, out EvidenceObservation? observation))
            {
                if (!attribution.PersonConfirmed && attribution.PersonId is not null && observation.OriginalSuggestedPersonId is null)
                    observation.OriginalSuggestedPersonId = attribution.SuggestedPersonId ?? attribution.PersonId;
                attribution = attribution with { SuggestedPersonId = observation.OriginalSuggestedPersonId };
                observation.Current = attribution;
            }
        return attribution;
    }

    private void PruneEvidence()
    {
        DateTimeOffset now = _clock();
        while (_evidenceOrder.TryPeek(out string? id))
        {
            if (!_evidence.TryGetValue(id, out EvidenceObservation? observation)) { _evidenceOrder.Dequeue(); continue; }
            if (_evidence.Count <= MaximumCachedEvidence && now - observation.ObservedAtUtc <= EvidenceLifetime) break;
            _evidenceOrder.Dequeue();
            _evidence.Remove(id);
        }
    }

    // Deliberately inference-free test seams: no model file, audio device, or native extractor access.
    internal SpeakerAttribution AddSyntheticFeedbackEvidence(float[]? embedding, string modelSha, SpeakerAttribution attribution, double clearSeconds = 2, double clippedFraction = 0, bool canLearn = true)
        => CacheEvidence(attribution, embedding, modelSha, clearSeconds, clippedFraction, canLearn);
    internal IReadOnlyList<SpeakerFeedbackAnchor> GetConfirmedAnchorsForTest(string modelSha) => _feedback.Anchors(modelSha);
    internal IReadOnlyList<SpeakerFeedbackAnchor> GetNegativeAnchorsForTest(string modelSha) => _feedback.NegativeAnchors(modelSha);
    internal int CachedEvidenceCount { get { lock (_evidenceGate) { PruneEvidence(); return _evidence.Count; } } }
    internal int CachedTrainableEvidenceCount { get { lock (_evidenceGate) { PruneEvidence(); return _evidence.Values.Count(observation => observation.CanLearn && observation.Embedding is not null); } } }
    internal string? MatchConfirmedPersonForTest(float[] embedding, string modelSha, double threshold = 0.55)
    {
        float[] copy = embedding.ToArray(); Normalize(copy);
        List<NamedMatch> ranked = RankConfirmed(copy, SpeakerFeedbackStore.NormalizeModelSha(modelSha));
        return ranked.Count > 0 && !ranked[0].Vetoed && ranked[0].Score >= NamedRequiredThreshold(threshold, ranked[0].References, 2) &&
            (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 0.06) ? ranked[0].Person.Id : null;
    }

    internal static async Task<int> RunRecognitionControlOfflineSelfTestsAsync(string testDirectory, CancellationToken token = default)
    {
        // Explicit test paths only. Fingerprints and PCM below are synthetic; the deliberately
        // invalid .onnx must never reach a native extractor, audio device, network, or real model.
        string folder = Path.Combine(Path.GetFullPath(testDirectory), "recognition-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string profilesPath = Path.Combine(folder, "anonymous.json");
        string feedbackPath = Path.Combine(folder, "feedback.json");
        string inertModel = Path.Combine(folder, "not-a-model.onnx");
        File.WriteAllText(inertModel, "Synthetic invalid model; this test must never load it.");
        string modelSha = new('A', 64);
        var database = new ProfileDatabase();
        database.Models[modelSha] = new ModelProfiles
        {
            ModelFileName = "synthetic.onnx",
            Profiles = new() { new AiSpeakerProfile { Id = 1, Samples = 1, Embedding = [1, 0], LastSeenUtc = DateTimeOffset.UtcNow } }
        };
        string originalProfiles = JsonSerializer.Serialize(database);
        File.WriteAllText(profilesPath, originalProfiles);
        int checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Speaker recognition control self-check failed."); checks++; }
        using var identifier = new AiSpeakerIdentifier(profilesPath, feedbackPath);
        Check(identifier.RecognitionEnabled && !identifier.IsModelLoaded && identifier.ExtractorLoadAttempts == 0);
        await identifier.AcknowledgeSpeakerLearningAsync(token).ConfigureAwait(false);
        SpeakerPerson person = await identifier.SaveSpeakerPersonAsync("Saved friend", token: token).ConfigureAwait(false);
        SpeakerAttribution fallback = new("Voice on left", 0.9, false, -0.8) { CanLearn = true, SpeechSeconds = 2 };
        SpeakerAttribution confirmed = identifier.AddSyntheticFeedbackEvidence([1, 0], modelSha, fallback);
        Check((await identifier.CorrectSpeakerAsync(confirmed.EvidenceId, person.Id, token: token).ConfigureAwait(false)).Learned);
        SpeakerAttribution recent = identifier.AddSyntheticFeedbackEvidence([0, 1], modelSha, fallback);
        identifier._pending.Add(new PendingSpeaker { Embedding = [1, 0], Count = 1, SpeechSeconds = 2 });
        Check(identifier.CachedTrainableEvidenceCount == 2 && identifier._pending.Count == 1);
        string savedFeedback = File.ReadAllText(feedbackPath);
        await identifier.SetRecognitionEnabledAsync(false, token).ConfigureAwait(false);
        Check(!identifier.RecognitionEnabled && !identifier.IsModelLoaded && identifier.LoadedModelPath.Length == 0);
        Check(identifier._pending.Count == 0 && identifier.CachedTrainableEvidenceCount == 0 && identifier.CachedEvidenceCount == 2);
        Check(File.ReadAllText(profilesPath) == originalProfiles && File.ReadAllText(feedbackPath) == savedFeedback);
        Check((await identifier.GetSpeakerFeedbackStateAsync(token).ConfigureAwait(false)).People.Single().Id == person.Id);
        SpeakerFeedbackResult manual = await identifier.CorrectSpeakerAsync(recent.EvidenceId, person.Id, token: token).ConfigureAwait(false);
        Check(manual.Applied && !manual.Learned && manual.Label == "Saved friend" && manual.CanUndo);
        Check(identifier.GetConfirmedAnchorsForTest(modelSha).Count == 1);
        var settings = new AppSettings { UseAiSpeakerRecognition = true, SpeakerModelPath = inertModel };
        float[] pcm = Enumerable.Repeat(0.04f, 32000).ToArray();
        SpeakerAttribution off = await identifier.IdentifyAsync(pcm, fallback, settings, token).ConfigureAwait(false);
        Check(off.Label == "Unidentified speaker" && off.PersonId is null && !off.CanLearn && off.Confidence == 0 && off.EvidenceId.Length > 0);
        Check(identifier.ExtractorLoadAttempts == 0);
        await identifier.SetRecognitionEnabledAsync(true, token).ConfigureAwait(false);
        Check(identifier.RecognitionEnabled && !identifier.IsModelLoaded && identifier.ExtractorLoadAttempts == 0);
        SpeakerAttribution staleOffTurn = identifier.RecordLabelOnlyObservation(fallback);
        Check(identifier.RecognitionEnabled && !staleOffTurn.CanLearn && staleOffTurn.Label == "Unidentified speaker" && identifier.ExtractorLoadAttempts == 0);
        SpeakerFeedbackResult metadataCorrection = await identifier.CorrectSpeakerAsync(staleOffTurn.EvidenceId, person.Id, token: token).ConfigureAwait(false);
        Check(metadataCorrection.Applied && !metadataCorrection.Learned && identifier.GetConfirmedAnchorsForTest(modelSha).Count == 1);
        SpeakerFeedbackResult stale = await identifier.CorrectSpeakerAsync(recent.EvidenceId, person.Id, token: token).ConfigureAwait(false);
        Check(stale.Applied && !stale.Learned && identifier.GetConfirmedAnchorsForTest(modelSha).Count == 1);

        // A recognizer already queued behind another gate owner must not load after a stop request,
        // even if its per-observation settings snapshot still says enabled.
        await identifier._gate.WaitAsync(token).ConfigureAwait(false);
        Task<SpeakerAttribution> queued;
        Task stopping;
        try
        {
            queued = identifier.IdentifyAsync(pcm, fallback, settings, token);
            stopping = identifier.SetRecognitionEnabledAsync(false, token);
            Check(!identifier.RecognitionEnabled && !queued.IsCompleted && !stopping.IsCompleted);
        }
        finally { identifier._gate.Release(); }
        await Task.WhenAll(queued, stopping).WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        Check((await queued.ConfigureAwait(false)).Label == "Unidentified speaker" && identifier.ExtractorLoadAttempts == 0);
        Check(!identifier.RecognitionEnabled && !identifier.IsModelLoaded && identifier.GetConfirmedAnchorsForTest(modelSha).Count == 1);
        Check(File.ReadAllText(profilesPath) == originalProfiles);

        await identifier.SetRecognitionEnabledAsync(true, token).ConfigureAwait(false);
        settings.UseAiSpeakerRecognition = false;
        SpeakerAttribution explicitlyOff = await identifier.IdentifyAsync(pcm, fallback, settings, token).ConfigureAwait(false);
        Check(!identifier.RecognitionEnabled && !explicitlyOff.CanLearn && explicitlyOff.Label == "Unidentified speaker" && identifier.ExtractorLoadAttempts == 0);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool cancelObserved = false;
        try { await identifier.SetRecognitionEnabledAsync(true, canceled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelObserved = true; }
        Check(cancelObserved && !identifier.RecognitionEnabled && !identifier.IsModelLoaded);
        return checks;
    }

    private void EnsureExtractor(string modelPath)
    {
        string fullPath = Path.GetFullPath(modelPath);
        var file = new FileInfo(fullPath);
        if (_extractor is not null && string.Equals(_loadedModel, fullPath, StringComparison.OrdinalIgnoreCase) &&
            _loadedLength == file.Length && _loadedWriteTime == file.LastWriteTimeUtc) return;
        // The embedding coordinate system is model-specific, even when two models produce equally sized vectors.
        using (FileStream input = File.OpenRead(fullPath))
            _modelKey = Convert.ToHexString(SHA256.HashData(input));
        _extractor?.Dispose();
        _extractor = null;
        Interlocked.Increment(ref _extractorLoadAttempts);
        _extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
        {
            Model = fullPath, NumThreads = 2, Debug = 0, Provider = "cpu"
        });
        _loadedModel = fullPath;
        _loadedLength = file.Length;
        _loadedWriteTime = file.LastWriteTimeUtc;
        if (!_database.Models.TryGetValue(_modelKey, out ModelProfiles? model))
        {
            model = new ModelProfiles { ModelFileName = Path.GetFileName(fullPath) };
            _database.Models[_modelKey] = model;
        }
        _profiles = model.Profiles.Where(profile => profile.Id > 0 && profile.Samples > 0 &&
            profile.Embedding.Length > 0 && profile.Embedding.All(float.IsFinite))
            .GroupBy(profile => profile.Id).Select(group => group.First()).Take(MaximumProfiles).ToList();
        foreach (AiSpeakerProfile profile in _profiles) Normalize(profile.Embedding);
        model.Profiles = _profiles;
        _pending.Clear();
        LastDiagnostic = $"Loaded {Path.GetFileName(fullPath)}; {_profiles.Count} learned voice(s), 2 CPU threads.";
    }

    private static AudioQuality PrepareAudio(float[] samples)
    {
        const int frameLength = 320;
        if (samples.Length < frameLength) return new(Array.Empty<float>(), 0, 0);
        int frameCount = samples.Length / frameLength;
        var levels = new double[frameCount];
        int clipped = 0;
        for (int frame = 0; frame < frameCount; frame++)
        {
            double energy = 0;
            for (int offset = 0; offset < frameLength; offset++)
            {
                float sample = samples[frame * frameLength + offset];
                if (!float.IsFinite(sample)) return new(Array.Empty<float>(), 0, 1);
                energy += sample * sample;
                if (Math.Abs(sample) > 0.985f) clipped++;
            }
            levels[frame] = Math.Sqrt(energy / frameLength);
        }
        double[] sorted = levels.Order().ToArray();
        // Capture already verified a usable voice turn. A fixed 0.002 raw floor
        // rejected quiet voices that transcription could hear after its gain stage.
        // Keep a raw noise floor, but measure speech relative to this sample's level.
        double speechFloor = Math.Max(0.0006, sorted[(int)(sorted.Length * 0.90)] * 0.16);
        int first = Array.FindIndex(levels, level => level >= speechFloor);
        int last = Array.FindLastIndex(levels, level => level >= speechFloor);
        if (first < 0) return new(Array.Empty<float>(), 0, 0);
        double speechSeconds = levels.Count(level => level >= speechFloor) * 0.02;
        // Keep the silence inside the utterance; stitching voiced frames together destroys the model's time context.
        int start = Math.Max(0, first - 2) * frameLength;
        int end = Math.Min(frameCount, last + 3) * frameLength;
        start = Math.Max(start, end - 16000 * 6);
        if (end - start < samples.Length) speechSeconds = levels.Skip(start / frameLength).Take((end - start) / frameLength).Count(level => level >= speechFloor) * 0.02;
        float[] prepared = samples[start..end];
        double rms = Math.Sqrt(prepared.Sum(value => (double)value * value) / prepared.Length);
        double peak = prepared.Max(value => Math.Abs((double)value));
        double gain = Math.Clamp(Math.Min(0.025 / Math.Max(rms, 1e-9), 0.90 / Math.Max(peak, 1e-9)), 1, 8);
        if (gain > 1)
            for (int index = 0; index < prepared.Length; index++) prepared[index] = (float)(prepared[index] * gain);
        // Clipping eligibility is measured BEFORE gain, and existing minimum
        // durations still apply. Normalization cannot make a short/mixed clip trainable.
        return new(prepared, speechSeconds, (double)clipped / samples.Length);
    }

    internal static int RunAudioPreparationOfflineChecks()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Speaker audio preparation: " + description);
            checks++;
        }
        static float[] Tone(int samples, float amplitude) => Enumerable.Range(0, samples)
            .Select(index => (float)(Math.Sin(2 * Math.PI * 180 * index / 16000.0) * amplitude)).ToArray();
        float[] quietInput = Tone(32000, 0.0022f);
        float[] untouched = quietInput.ToArray();
        AudioQuality quiet = PrepareAudio(quietInput);
        Check(quiet.Samples.Length >= 30000 && quiet.SpeechSeconds >= 1.9 && quiet.ClippedFraction == 0,
            "A sustained quiet voice remains eligible without a loud fixed-volume gate");
        Check(quiet.Samples.Max(Math.Abs) > quietInput.Max(Math.Abs) * 7 && quietInput.SequenceEqual(untouched),
            "Recognition gain is bounded and does not change captured evidence");
        Check(PrepareAudio(new float[32000]).Samples.Length == 0 &&
            PrepareAudio(Tone(32000, 0.0002f)).Samples.Length == 0,
            "Silence and below-floor background audio are not promoted into usable voice samples");
        AudioQuality shortVoice = PrepareAudio(Tone(3840, 0.0022f));
        Check(shortVoice.Samples.Length < 11200 && shortVoice.SpeechSeconds < 0.55,
            "Gain cannot bypass minimum recognition or learning durations");
        AudioQuality clippedVoice = PrepareAudio(Enumerable.Repeat(0.99f, 32000).ToArray());
        Check(clippedVoice.ClippedFraction > 0.015,
            "Normalization cannot hide original clipping from the learning safeguard");
        AudioQuality ordinary = PrepareAudio(Tone(32000, 0.1f));
        Check(ordinary.SpeechSeconds >= 1.9 && ordinary.Samples.Max(Math.Abs) <= 0.101f,
            "Ordinary voice levels are not unnecessarily amplified");
        float[] withPause = Tone(16000, 0.0022f).Concat(new float[8000]).Concat(Tone(16000, 0.0022f)).ToArray();
        AudioQuality paused = PrepareAudio(withPause);
        Check(paused.Samples.Length == withPause.Length && paused.Samples.Skip(16000).Take(8000).All(value => value == 0),
            "Natural pauses retain their original position rather than splicing separate sounds together");
        return checks;
    }

    private static double Cosine(float[] first, float[] second)
    {
        if (first.Length != second.Length) return -1;
        double dot = 0;
        for (int index = 0; index < first.Length; index++) dot += first[index] * second[index];
        return Math.Clamp(dot, -1, 1);
    }

    private static void Normalize(float[] values)
    {
        double length = Math.Sqrt(values.Sum(value => (double)value * value));
        if (length <= 1e-9) return;
        for (int index = 0; index < values.Length; index++) values[index] = (float)(values[index] / length);
    }

    private static void Blend(float[] centroid, float[] embedding, float rate)
    {
        for (int index = 0; index < embedding.Length; index++)
            centroid[index] = centroid[index] * (1 - rate) + embedding[index] * rate;
        Normalize(centroid);
    }

    private int NextId() => _profiles.Count == 0 ? 1 : _profiles.Max(profile => profile.Id) + 1;

    private void LoadDatabase()
    {
        try
        {
            if (!File.Exists(_profilesPath)) return;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_profilesPath));
            // Legacy profiles were polluted by mixed/short audio and had no model provenance. Leave the file intact
            // until clean profiles are learned, but do not silently reuse these identities for another model.
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Version", out JsonElement version) || version.GetInt32() != 2)
            {
                _needsLegacyBackup = true;
                return;
            }
            _database = JsonSerializer.Deserialize<ProfileDatabase>(document.RootElement.GetRawText()) ?? new();
        }
        catch { _database = new(); }
    }

    private void SaveDatabase(bool force = false)
    {
        // Merely viewing names, constructing the service, or running offline tests must not create a
        // profile file. Guessed profiles are persisted only after a real enrollment/reset mutation.
        if (!_databaseDirty || string.IsNullOrEmpty(_modelKey) && !force || !force && _clock() - _lastSavedAt < TimeSpan.FromSeconds(5)) return;
        try
        {
            if (_needsLegacyBackup)
            {
                BackupProfiles();
            }
            string? directory = Path.GetDirectoryName(_profilesPath);
            if (directory is not null) Directory.CreateDirectory(directory);
            string temporary = _profilesPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_database, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _profilesPath, true);
            _lastSavedAt = _clock();
            _databaseDirty = false;
            _needsLegacyBackup = false;
        }
        catch (Exception exception)
        {
            LastDiagnostic = "Speaker profile save failed: " + exception.Message;
            throw new IOException(LastDiagnostic + " No persistent speaker change was applied.", exception);
        }
    }

    private void BackupProfiles()
    {
        if (!File.Exists(_profilesPath)) return;
        string backup = _profilesPath + ".backup-" + _clock().ToString("yyyyMMdd-HHmmss-fffffff");
        File.Copy(_profilesPath, backup, false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        // Never block the WPF thread behind inference or abandon existing WaitAsync callers by
        // disposing their semaphore. No OS wait handle is used; this managed gate can be GC'd.
        if (_gate.Wait(0))
        {
            try { DisposeResources(); }
            catch (Exception exception) { LastDiagnostic = "Speaker cleanup failed: " + exception.Message; }
            finally { _gate.Release(); }
        }
        else _disposalCompleted = DisposeDeferredAsync();
    }

    private async Task DisposeDeferredAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { DisposeResources(); }
        catch (Exception exception) { LastDiagnostic = "Speaker cleanup failed: " + exception.Message; }
        finally { _gate.Release(); }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeRequested) != 0) throw new ObjectDisposedException(nameof(AiSpeakerIdentifier), "Speaker recognition is closed. Reopen the overlay before making corrections.");
    }

    private void DisposeResources()
    {
        try { SaveDatabase(force: true); } catch { /* Preserve the original file and diagnostic on shutdown. */ }
        UnloadExtractor();
        lock (_evidenceGate)
        {
            foreach (EvidenceObservation observation in _evidence.Values)
            {
                if (observation.Embedding is not null) Array.Clear(observation.Embedding);
                foreach (UndoEntry entry in observation.History)
                {
                    if (entry.Previous.Positive is not null) Array.Clear(entry.Previous.Positive.Embedding);
                    if (entry.Previous.Negative is not null) Array.Clear(entry.Previous.Negative.Embedding);
                }
                observation.History.Clear();
            }
            _evidence.Clear();
            _evidenceOrder.Clear();
        }
        ClearPendingEnrollment();
    }

    private void UnloadExtractor()
    {
        _extractor?.Dispose();
        _extractor = null;
        _loadedModel = "";
        _loadedLength = 0;
        _loadedWriteTime = default;
        // _modelKey and its profile list remain as provenance for the preserved database, not as
        // a claim that a model is resident. A later real load chooses its exact SHA again.
    }

    private void ClearPendingEnrollment()
    {
        foreach (PendingSpeaker pending in _pending) Array.Clear(pending.Embedding);
        _pending.Clear();
    }

    private void ClearTrainableEvidence()
    {
        lock (_evidenceGate)
            foreach (EvidenceObservation observation in _evidence.Values)
            {
                if (observation.Embedding is not null) Array.Clear(observation.Embedding);
                observation.Embedding = null;
                observation.CanLearn = false;
                observation.Current = observation.Current with { CanLearn = false };
                // Keep labels, UUIDs, anchor aliases and undo metadata. Saved-person samples are
                // intentionally retained; turning recognition off is not deletion or a reset.
            }
    }

    private sealed record AudioQuality(float[] Samples, double SpeechSeconds, double ClippedFraction);
    private sealed record NamedMatch(SpeakerPerson Person, double Score, int References, bool Vetoed);
    private sealed record UndoEntry(SpeakerAttribution PreviousAttribution, SpeakerFeedbackContribution Previous, string? ExpectedRevisionToken,
        bool PreviousManuallyAttributed);
    private sealed class EvidenceObservation
    {
        public EvidenceObservation(string id, SpeakerAttribution attribution, float[]? embedding, string modelSha, DateTimeOffset at, double speechSeconds, double clippedFraction, bool canLearn)
        {
            AnchorKey = id; Current = attribution; Embedding = embedding; ModelSha = modelSha;
            ObservedAtUtc = at; SpeechSeconds = speechSeconds; ClippedFraction = clippedFraction; CanLearn = canLearn;
            OriginalSuggestedPersonId = attribution.SuggestedPersonId ?? (attribution.PersonConfirmed ? null : attribution.PersonId);
            IsManuallyAttributed = attribution.PersonConfirmed;
        }
        public string AnchorKey { get; set; }
        public string? OriginalSuggestedPersonId { get; set; }
        public bool IsManuallyAttributed { get; set; }
        public SpeakerAttribution Current { get; set; }
        public float[]? Embedding { get; set; }
        public string ModelSha { get; }
        public DateTimeOffset ObservedAtUtc { get; }
        public double SpeechSeconds { get; }
        public double ClippedFraction { get; }
        public bool CanLearn { get; set; }
        public List<UndoEntry> History { get; } = new();
    }
    private sealed class PendingSpeaker
    {
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public int Count { get; set; }
        public double SpeechSeconds { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public DateTimeOffset LastEvidenceEnd { get; set; }
    }
    private sealed class ProfileDatabase
    {
        public int Version { get; set; } = 2;
        public Dictionary<string, ModelProfiles> Models { get; set; } = new();
    }
    private sealed class ModelProfiles
    {
        public string ModelFileName { get; set; } = "";
        public List<AiSpeakerProfile> Profiles { get; set; } = new();
    }
    private sealed class AiSpeakerProfile
    {
        public int Id { get; set; }
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public int Samples { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
    }
}

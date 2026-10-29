using System.Text.Json;

namespace IncomingTranslator;

/// <summary>Synthetic feedback tests: no microphone, playback, network, extractor, or GPU inference.</summary>
public static class SpeakerFeedbackOfflineTests
{
    private static readonly string ModelA = new('A', 64);
    private static readonly string ModelB = new('B', 64);

    // DesktopTests synchronously waits on its STA. Run all asynchronous backend work on
    // the pool so neither these tests nor a backend semaphore captures that UI context.
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default) =>
        await Task.Run(() => RunBackendAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

    private static async Task<int> RunBackendAsync(CancellationToken token)
    {
        int checks = 0;
        string root = Path.Combine(Path.GetTempPath(), "LocalTalk-SpeakerFeedbackTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(bool value, string message)
        {
            token.ThrowIfCancellationRequested();
            if (!value) throw new InvalidOperationException("Speaker feedback test failed: " + message);
            checks++;
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            bool rejected = false;
            try { action(); } catch (T) { rejected = true; }
            Check(rejected, message);
        }
        async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
        {
            bool rejected = false;
            try { await action().ConfigureAwait(false); } catch (T) { rejected = true; }
            Check(rejected, message);
        }
        string Data(string name) => Path.Combine(root, name);
        DateTimeOffset now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        SpeakerAttribution Attribution(string label = "Speaker 1", bool mixed = false) => new(label, .8, mixed, 0)
        {
            CanLearn = true, SpeechSeconds = 2, EvidenceStart = now.AddSeconds(-2), EvidenceEnd = now,
            Diagnostic = "SYNTHETIC_PRIVATE_TRANSCRIPT_MUST_NOT_BE_PERSISTED"
        };

        // Anonymous V2 provenance is preserved, not silently promoted to a confirmed person.
        string legacy = Data("anonymous-v2.json");
        string legacyContents = JsonSerializer.Serialize(new
        {
            Version = 2, Models = new Dictionary<string, object>
            {
                [ModelA] = new { ModelFileName = "synthetic-model-a.onnx", Profiles = new[] { new { Id = 1, Embedding = new[] { 1f, 0f }, Samples = 3, LastSeenUtc = now } } },
                [ModelB] = new { ModelFileName = "synthetic-model-b.onnx", Profiles = new[] { new { Id = 1, Embedding = new[] { 0f, 1f }, Samples = 2, LastSeenUtc = now } } }
            }
        });
        File.WriteAllText(legacy, legacyContents);
        string feedback = Data("named.json");
        var store = new SpeakerFeedbackStore(feedback, legacy);
        Check(!File.Exists(feedback) && store.State().People.Count == 0, "constructing the store does not create a file or confirm anonymous voices");
        SpeakerPerson mira = store.SavePerson("  Mira  ", null);
        Check(mira.Name == "Mira" && mira.ConfirmedSamples == 0, "saving a trimmed name does not train a voice");
        Check(File.ReadAllText(legacy) == legacyContents, "V2 anonymous IDs and model provenance are untouched");
        Check(File.ReadAllText(legacy + ".before-named-feedback-v1.backup") == legacyContents, "first named save makes an exact recoverable legacy checkpoint");
        Check(!store.State().InstructionAcknowledged, "first-time learning acknowledgment is explicit");
        store.Acknowledge();
        Check(new SpeakerFeedbackStore(feedback).State().InstructionAcknowledged, "learning acknowledgment survives reload");
        SpeakerPerson kai = store.SavePerson("Kai", null);
        Reject<ArgumentException>(() => store.SavePerson("mira", null), "duplicate names are rejected case-insensitively");
        Reject<ArgumentException>(() => store.SavePerson(" ", null), "empty names are rejected");
        Reject<ArgumentException>(() => store.SavePerson("bad\nname", null), "control characters are rejected");
        Reject<ArgumentException>(() => store.SavePerson(new string('x', 65), null), "oversized names are rejected");
        Check(store.SavePerson("Mira renamed", mira.Id).Id == mira.Id, "rename keeps an immutable person identity");

        SpeakerFeedbackAnchor Anchor(string id, string personId, string sha, float[] embedding) => new()
        {
            EvidenceId = id, PersonId = personId, ModelSha = sha, Embedding = embedding,
            ObservedAtUtc = now, SpeechSeconds = 2, ClippedFraction = 0,
            EvidenceStart = now.AddSeconds(-2), EvidenceEnd = now, RevisionToken = Guid.NewGuid().ToString("N")
        };
        store.ReplaceContribution("store-a", Anchor("store-a", mira.Id, ModelA.ToLowerInvariant(), [1, 0]) with { EvidenceAliases = ["revision-a"] });
        store.ReplaceContribution("store-b", Anchor("store-b", mira.Id, ModelB, [1, 0]));
        Check(store.Anchors(ModelA).Count == 1 && store.Anchors(ModelB).Count == 1, "equal-sized vectors remain isolated by exact model hash");
        Check(store.Contribution("store-a")!.ModelSha == ModelA, "model hashes are canonicalized");
        SpeakerFeedbackAnchor cloned = store.Anchors(ModelA)[0];
        cloned.Embedding[0] = 99;
        cloned.EvidenceAliases[0] = "changed-alias";
        Check(store.Anchors(ModelA)[0].Embedding[0] == 1 && store.Contribution("revision-a")!.EvidenceId == "store-a", "anchor snapshots do not expose mutable embeddings or revision aliases");
        Reject<InvalidDataException>(() => store.ReplaceContribution("duplicate-alias", Anchor("duplicate-alias", mira.Id, ModelA, [1, 0]) with { EvidenceAliases = ["revision-a"] }),
            "one revised observation cannot secretly belong to two confirmed samples");
        store.ReplaceContribution("store-a", Anchor("store-a", kai.Id, ModelA, [1, 0]));
        Check(store.State().People.Single(person => person.Id == mira.Id).ConfirmedSamples == 1 &&
            store.State().People.Single(person => person.Id == kai.Id).ConfirmedSamples == 1, "reassign removes the old contribution rather than duplicating it");
        var reloaded = new SpeakerFeedbackStore(feedback, legacy);
        Check(reloaded.Contribution("store-a")!.PersonId == kai.Id && reloaded.State().People.Count == 2, "names, reassignments, and provenance survive restart");
        Reject<ArgumentException>(() => store.ReplaceContribution("bad-sha", Anchor("bad-sha", kai.Id, "not-a-hash", [1, 0])), "invalid model provenance cannot be saved");
        Reject<InvalidDataException>(() => store.ReplaceContribution("bad-zero", Anchor("bad-zero", kai.Id, ModelA, [0, 0])), "zero embeddings cannot be saved");
        Reject<InvalidDataException>(() => store.ReplaceContribution("bad-dimension", Anchor("bad-dimension", kai.Id, ModelA, [1, 0, 0])), "one model cannot mix embedding dimensions");
        Reject<InvalidDataException>(() => store.ReplaceContribution("bad-quality", Anchor("bad-quality", kai.Id, ModelA, [1, 0]) with { SpeechSeconds = .4 }), "insufficient speech cannot be persisted as confirmed evidence");
        store.DeletePerson(kai.Id);
        Check(store.Contribution("store-a") is null && store.Anchors(ModelB).Count == 1, "deleting one person removes only their confirmed evidence");
        Check(new SpeakerFeedbackStore(feedback).State().People.Count == 1, "deletion is persisted");
        Check(!File.ReadAllText(feedback).Contains("SYNTHETIC_PRIVATE", StringComparison.Ordinal) &&
            !File.ReadAllText(feedback).Contains("Transcript", StringComparison.OrdinalIgnoreCase), "named persistence contains no raw audio or transcript content");

        string corrupt = Data("corrupt.json");
        File.WriteAllText(corrupt, "{\"Version\":999,\"People\":[],\"Anchors\":[]}");
        var invalidStore = new SpeakerFeedbackStore(corrupt);
        Reject<InvalidDataException>(() => invalidStore.State(), "unsupported schemas fail closed");
        Reject<InvalidDataException>(() => invalidStore.SavePerson("Never overwrite", null), "unreadable data cannot be silently overwritten");
        Check(File.ReadAllText(corrupt).Contains("999"), "unsupported saved data stays recoverable");
        string blocked = Data("existing-directory"); Directory.CreateDirectory(blocked);
        var failingStore = new SpeakerFeedbackStore(blocked);
        Reject<IOException>(() => failingStore.SavePerson("Failed save", null), "failed persistence is reported");
        Check(failingStore.State().People.Count == 0, "failed persistence does not publish an in-memory mutation");

        // Synthetic identity seam supplies fingerprints directly; never load an ONNX extractor.
        using (var identifier = new AiSpeakerIdentifier(Data("ai-anonymous.json"), Data("ai-feedback.json"), () => now))
        {
            Check(!(await identifier.GetSpeakerFeedbackStateAsync(token).ConfigureAwait(false)).InstructionAcknowledged, "fresh identifier requires explicit acknowledgment");
            SpeakerPerson first = await identifier.SaveSpeakerPersonAsync("First", token: token).ConfigureAwait(false);
            SpeakerPerson second = await identifier.SaveSpeakerPersonAsync("Second", token: token).ConfigureAwait(false);
            SpeakerAttribution evidence = identifier.AddSyntheticFeedbackEvidence([1, 0, 0], ModelA, Attribution());
            Check(!string.IsNullOrEmpty(evidence.EvidenceId), "cached synthetic evidence gets an opaque identity");
            SpeakerFeedbackResult beforeAck = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            Check(beforeAck.Applied && !beforeAck.Learned && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 0, "label-only correction before acknowledgment cannot train");
            await identifier.AcknowledgeSpeakerLearningAsync(token).ConfigureAwait(false);
            SpeakerFeedbackResult learned = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            Check(learned.Applied && learned.Learned && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 1, "acknowledged clean recent evidence can train");
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            Check(identifier.GetConfirmedAnchorsForTest(ModelA).Count == 1, "repeated correction of one evidence item is counted once");
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            Check(identifier.GetConfirmedAnchorsForTest(ModelA).Single().PersonId == second.Id, "reassignment moves the same voice observation");
            SpeakerFeedbackResult undone = await identifier.UndoSpeakerCorrectionAsync(evidence.EvidenceId, token).ConfigureAwait(false);
            Check(undone.Applied && undone.PersonId == first.Id && identifier.GetConfirmedAnchorsForTest(ModelA).Single().PersonId == first.Id, "undo restores the previous person and contribution");
            SpeakerFeedbackResult earlierUndo = await identifier.UndoSpeakerCorrectionAsync(evidence.EvidenceId, token).ConfigureAwait(false);
            Check(earlierUndo.Applied && earlierUndo.PersonId == first.Id && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 1, "successive undo restores one preceding explicit correction without duplicating evidence");
            SpeakerAttribution untouched = identifier.AddSyntheticFeedbackEvidence(null, ModelA, Attribution());
            Check(!(await identifier.UndoSpeakerCorrectionAsync(untouched.EvidenceId, token).ConfigureAwait(false)).Applied, "uncorrected evidence has no invented undo history");

            now = now.AddSeconds(10);
            SpeakerAttribution otherModel = identifier.AddSyntheticFeedbackEvidence([1, 0, 0], ModelB, Attribution());
            await identifier.CorrectSpeakerAsync(otherModel.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            Check(identifier.GetConfirmedAnchorsForTest(ModelA).Count == 1 && identifier.GetConfirmedAnchorsForTest(ModelB).Count == 1, "feedback does not cross-train equal-dimensional model spaces");
            Check(identifier.MatchConfirmedPersonForTest([1, 0, 0], ModelA) == first.Id && identifier.MatchConfirmedPersonForTest([1, 0, 0], ModelB) == second.Id,
                "named recognition uses the person confirmed in the exact model coordinate space");
            Check(identifier.MatchConfirmedPersonForTest([1, 0, 0], new string('C', 64)) is null && identifier.MatchConfirmedPersonForTest([0, 1, 0], ModelA) is null,
                "unknown model provenance and insufficient voice similarity do not borrow another person's identity");

            foreach (var quality in new[] { (Mixed: true, Seconds: 2d, Clipped: 0d, CanLearn: true), (Mixed: false, Seconds: .5, Clipped: 0d, CanLearn: true),
                (Mixed: false, Seconds: 2d, Clipped: .03, CanLearn: true), (Mixed: false, Seconds: 2d, Clipped: 0d, CanLearn: false) })
            {
                now = now.AddSeconds(10);
                SpeakerAttribution weak = identifier.AddSyntheticFeedbackEvidence([0, 1, 0], ModelA, Attribution(mixed: quality.Mixed), quality.Seconds, quality.Clipped, quality.CanLearn);
                SpeakerFeedbackResult correction = await identifier.CorrectSpeakerAsync(weak.EvidenceId, second.Id, token: token).ConfigureAwait(false);
                Check(correction.Applied && !correction.Learned && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 1, "mixed/short/clipped/disallowed evidence relabels without learning");
            }
            now = now.AddSeconds(10);
            SpeakerAttribution noFingerprint = identifier.AddSyntheticFeedbackEvidence(null, ModelA, Attribution());
            SpeakerFeedbackResult manual = await identifier.CorrectSpeakerAsync(noFingerprint.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            Check(manual.Applied && !manual.Learned, "AI-off or missing-model metadata permits label-only feedback");
            now = now.AddMinutes(6);
            SpeakerFeedbackResult expired = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            Check(expired.Applied && !expired.Learned && !expired.CanUndo && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 0, "expired reassignments relabel without learning and remove the known-wrong old contribution");
            Check(!(await identifier.UndoSpeakerCorrectionAsync(evidence.EvidenceId, token).ConfigureAwait(false)).Applied, "expired undo is rejected without changing the label");
            await identifier.DeleteSpeakerPersonAsync(second.Id, token).ConfigureAwait(false);
            Check(identifier.GetConfirmedAnchorsForTest(ModelB).Count == 0, "person deletion removes their other-model examples too");
            await RejectAsync<ArgumentException>(async () => { await identifier.CorrectSpeakerAsync(noFingerprint.EvidenceId, second.Id, token: token).ConfigureAwait(false); }, "deleted target persons cannot receive feedback").ConfigureAwait(false);
            for (int index = 0; index < 205; index++)
                identifier.AddSyntheticFeedbackEvidence(null, ModelA, Attribution());
            Check(identifier.CachedEvidenceCount <= 200, "metadata and embedding cache is capacity-bounded");
            now = now.AddMinutes(6);
            identifier.AddSyntheticFeedbackEvidence(null, ModelA, Attribution());
            Check(identifier.CachedEvidenceCount <= 1, "expired cache entries are pruned, not retained indefinitely");
        }
        Check(!File.Exists(Data("ai-anonymous.json")), "offline feedback and disposal do not create an anonymous model database");
        using (var reload = new AiSpeakerIdentifier(Data("ai-anonymous.json"), Data("ai-feedback.json"), () => now))
        {
            SpeakerFeedbackState state = await reload.GetSpeakerFeedbackStateAsync(token).ConfigureAwait(false);
            Check(state.InstructionAcknowledged && state.People.Count == 1 && state.People[0].ConfirmedSamples == 0, "acknowledgment, deletion, and removal of wrong expired contributions survive identifier restart");
            Check(reload.CachedEvidenceCount == 0, "recent ephemeral evidence is not loaded from disk");
        }

        // Exercise production metadata-only branches; the synthetic .onnx is deliberately invalid,
        // so any accidental extractor initialization fails rather than silently doing inference.
        string inertModel = Data("not-a-real-model.onnx"); File.WriteAllText(inertModel, "Synthetic non-model fixture.");
        using (var identifier = new AiSpeakerIdentifier(Data("early-anonymous.json"), Data("early-feedback.json"), () => now))
        {
            await identifier.AcknowledgeSpeakerLearningAsync(token).ConfigureAwait(false);
            SpeakerPerson person = await identifier.SaveSpeakerPersonAsync("Metadata only", token: token).ConfigureAwait(false);
            var earlyReturns = new[]
            {
                (Settings: new AppSettings { UseAiSpeakerRecognition = false, SpeakerModelPath = inertModel }, Fallback: Attribution()),
                (Settings: new AppSettings { UseAiSpeakerRecognition = true, SpeakerModelPath = Data("missing-model.onnx") }, Fallback: Attribution()),
                (Settings: new AppSettings { UseAiSpeakerRecognition = true, SpeakerModelPath = inertModel }, Fallback: Attribution(mixed: true)),
                (Settings: new AppSettings { UseAiSpeakerRecognition = true, SpeakerModelPath = inertModel }, Fallback: Attribution() with { CanLearn = false }),
                (Settings: new AppSettings { UseAiSpeakerRecognition = true, SpeakerModelPath = inertModel }, Fallback: Attribution())
            };
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var branch in earlyReturns)
            {
                SpeakerAttribution evidence = await identifier.IdentifyAsync(Array.Empty<float>(), branch.Fallback, branch.Settings, token).ConfigureAwait(false);
                Check(!string.IsNullOrEmpty(evidence.EvidenceId) && observed.Add(evidence.EvidenceId), "each metadata-only production observation gets a unique cached correction identity");
                SpeakerFeedbackResult result = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, person.Id, token: token).ConfigureAwait(false);
                Check(result.Applied && !result.Learned && result.CanUndo, "production off/missing/mixed/disallowed/short branches permit label-only feedback without voice learning");
            }
            Check(identifier.LoadedModelPath.Length == 0 && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 0,
                "metadata-only production paths never initialize an extractor or confirm a voice model");
        }

        // Revised windows are not independent votes, and undo never erases another transaction.
        using (var identifier = new AiSpeakerIdentifier(Data("dedup-anonymous.json"), Data("dedup-feedback.json"), () => now))
        {
            await identifier.AcknowledgeSpeakerLearningAsync(token).ConfigureAwait(false);
            SpeakerPerson first = await identifier.SaveSpeakerPersonAsync("A", token: token).ConfigureAwait(false);
            SpeakerPerson second = await identifier.SaveSpeakerPersonAsync("B", token: token).ConfigureAwait(false);
            SpeakerPerson third = await identifier.SaveSpeakerPersonAsync("C", token: token).ConfigureAwait(false);
            SpeakerAttribution initial = identifier.AddSyntheticFeedbackEvidence([1, 0], ModelA, Attribution());
            await identifier.CorrectSpeakerAsync(initial.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            now = now.AddMilliseconds(80);
            SpeakerAttribution revision = identifier.AddSyntheticFeedbackEvidence([1, 0], ModelA, Attribution());
            await identifier.CorrectSpeakerAsync(revision.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            Check(identifier.GetConfirmedAnchorsForTest(ModelA).Count == 1, "overlapping revisions with the same voice do not create duplicate training samples");
            await identifier.CorrectSpeakerAsync(initial.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            await identifier.CorrectSpeakerAsync(revision.EvidenceId, third.Id, token: token).ConfigureAwait(false);
            SpeakerFeedbackResult conflict = await identifier.UndoSpeakerCorrectionAsync(initial.EvidenceId, token).ConfigureAwait(false);
            Check(!conflict.Applied && identifier.GetConfirmedAnchorsForTest(ModelA).Single().PersonId == third.Id, "stale undo cannot overwrite a newer correction of the same evidence");

            now = now.AddSeconds(10);
            SpeakerAttribution independent = identifier.AddSyntheticFeedbackEvidence([0, 1], ModelA, Attribution());
            await identifier.CorrectSpeakerAsync(independent.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            await identifier.CorrectSpeakerAsync(independent.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            now = now.AddSeconds(10);
            SpeakerAttribution later = identifier.AddSyntheticFeedbackEvidence([0, 1], ModelA, Attribution());
            await identifier.CorrectSpeakerAsync(later.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            await identifier.UndoSpeakerCorrectionAsync(independent.EvidenceId, token).ConfigureAwait(false);
            var anchors = identifier.GetConfirmedAnchorsForTest(ModelA);
            Check(anchors.Count == 3 && anchors.Count(anchor => anchor.PersonId == first.Id) == 1 && anchors.Count(anchor => anchor.PersonId == second.Id) == 1,
                "undo only restores its contribution and preserves an unrelated later sample");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(index => identifier.SaveSpeakerPersonAsync("Concurrent " + index, token: token))).ConfigureAwait(false);
            Check((await identifier.GetSpeakerFeedbackStateAsync(token).ConfigureAwait(false)).People.Count == 11, "serialized concurrent mutations publish a coherent people snapshot");
            now = now.AddMinutes(6);
            SpeakerFeedbackResult expiredRevision = await identifier.CorrectSpeakerAsync(revision.EvidenceId, null, "music", token).ConfigureAwait(false);
            Check(expiredRevision.Applied && !expiredRevision.Learned && !expiredRevision.CanUndo &&
                identifier.GetConfirmedAnchorsForTest(ModelA).Count == 2 && identifier.GetConfirmedAnchorsForTest(ModelA).All(anchor => anchor.PersonId != third.Id),
                "expired revised-window feedback resolves its persisted alias and removes the original wrong contribution without touching independent samples");
        }

        // Music never becomes a named voice; its undo restores only the prior confirmed example.
        using (var identifier = new AiSpeakerIdentifier(Data("music-anonymous.json"), Data("music-feedback.json"), () => now))
        {
            await identifier.AcknowledgeSpeakerLearningAsync(token).ConfigureAwait(false);
            SpeakerPerson person = await identifier.SaveSpeakerPersonAsync("Listener", token: token).ConfigureAwait(false);
            float[] callerEmbedding = [3, 0];
            SpeakerAttribution evidence = identifier.AddSyntheticFeedbackEvidence(callerEmbedding, ModelA, Attribution());
            callerEmbedding[0] = 0; callerEmbedding[1] = 3;
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, person.Id, token: token).ConfigureAwait(false);
            Check(identifier.GetConfirmedAnchorsForTest(ModelA).Single().Embedding[0] == 1, "cached evidence owns an immutable normalized fingerprint independent of caller mutations");
            SpeakerFeedbackResult music = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, null, "music", token).ConfigureAwait(false);
            Check(music.Applied && !music.Learned && music.PersonId is null && music.Label == "Music / recording" && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 0,
                "a music correction removes the wrong named training example without training music");
            SpeakerFeedbackResult restored = await identifier.UndoSpeakerCorrectionAsync(evidence.EvidenceId, token).ConfigureAwait(false);
            Check(restored.Applied && restored.PersonId == person.Id && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 1, "undo of music restores the previous named contribution");
            now = now.AddMinutes(6);
            SpeakerFeedbackResult expiredMusic = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, null, "music", token).ConfigureAwait(false);
            Check(expiredMusic.Applied && !expiredMusic.Learned && !expiredMusic.CanUndo && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 0,
                "expired music feedback still removes a known-wrong anchor but never invents a reusable fingerprint");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await RejectAsync<OperationCanceledException>(async () => { await identifier.SaveSpeakerPersonAsync("Canceled person", token: canceled.Token).ConfigureAwait(false); },
                "canceled mutations do not publish names").ConfigureAwait(false);
            Check((await identifier.GetSpeakerFeedbackStateAsync(token).ConfigureAwait(false)).People.Count == 1, "canceled mutations preserve the last committed snapshot");
        }

        var boundedStore = new SpeakerFeedbackStore(Data("bounded-feedback.json"));
        SpeakerPerson boundedPerson = boundedStore.SavePerson("Bounded", null);
        for (int index = 0; index < 64; index++)
            boundedStore.ReplaceContribution("bounded-" + index, Anchor("bounded-" + index, boundedPerson.Id, ModelA, [1, 0]));
        Reject<InvalidOperationException>(() => boundedStore.ReplaceContribution("bounded-overflow", Anchor("bounded-overflow", boundedPerson.Id, ModelA, [1, 0])),
            "persistent samples are bounded per person and model");
        Check(boundedStore.Anchors(ModelA).Count == 64 && new SpeakerFeedbackStore(Data("bounded-feedback.json")).Anchors(ModelA).Count == 64,
            "rejected capacity overflow preserves both the in-memory and persisted confirmed counts");
        using (var identifier = new AiSpeakerIdentifier(Data("bounded-anonymous.json"), Data("bounded-feedback.json"), () => now))
        {
            await identifier.AcknowledgeSpeakerLearningAsync(token).ConfigureAwait(false);
            SpeakerAttribution overflow = identifier.AddSyntheticFeedbackEvidence([0, 1], ModelA, Attribution());
            SpeakerFeedbackResult result = await identifier.CorrectSpeakerAsync(overflow.EvidenceId, boundedPerson.Id, token: token).ConfigureAwait(false);
            Check(result.Applied && !result.Learned && result.PersonId == boundedPerson.Id && identifier.GetConfirmedAnchorsForTest(ModelA).Count == 64,
                "the public correction operation stays label-only at capacity without silently evicting confirmed voices");
        }
        var disposable = new AiSpeakerIdentifier(Data("disposed-anonymous.json"), Data("disposed-feedback.json"), () => now);
        disposable.AddSyntheticFeedbackEvidence([1, 0], ModelA, Attribution());
        disposable.Dispose();
        Check(disposable.CachedEvidenceCount == 0 && !File.Exists(Data("disposed-feedback.json")),
            "disposal immediately releases cached fingerprints and does not persist ephemeral evidence");

        var queuedIdentifier = new AiSpeakerIdentifier(Data("queued-anonymous.json"), Data("queued-feedback.json"), () => now);
        queuedIdentifier.AddSyntheticFeedbackEvidence([1, 0], ModelA, Attribution());
        var queuedGate = (SemaphoreSlim?)typeof(AiSpeakerIdentifier).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(queuedIdentifier)
            ?? throw new InvalidOperationException("Speaker feedback shutdown test could not find its isolated gate.");
        await queuedGate.WaitAsync(token).ConfigureAwait(false);
        Task<SpeakerFeedbackState> queuedState;
        try
        {
            queuedState = queuedIdentifier.GetSpeakerFeedbackStateAsync(token);
            Check(!queuedState.IsCompleted, "the isolated speaker state operation is actually waiting behind a held gate");
            queuedIdentifier.Dispose(); // Must return without blocking the owner/UI thread.
        }
        finally { queuedGate.Release(); }
        await RejectAsync<ObjectDisposedException>(async () =>
        {
            await queuedState.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }, "a queued operation completes with a clean closed-identifier error instead of hanging after disposal").ConfigureAwait(false);
        await queuedIdentifier.DisposalCompleted.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        Check(queuedIdentifier.CachedEvidenceCount == 0 && !File.Exists(Data("queued-feedback.json")) && !File.Exists(Data("queued-anonymous.json")),
            "deferred disposal clears cached fingerprints without creating profile or feedback files");
        await RejectAsync<ObjectDisposedException>(async () =>
        {
            await queuedIdentifier.GetSpeakerFeedbackStateAsync(token).WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }, "new operations on an already disposed idle identifier reject promptly").ConfigureAwait(false);
        return checks;
    }

    // Called separately by DesktopTests on its existing STA/Application; never show this window.
    public static int RunOverlayChecks()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Speaker feedback overlay test failed: " + message);
            checks++;
        }
        var overlay = new SubtitleWindow();
        try
        {
            DateTimeOffset now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
            overlay.Publish(new TranslationDisplay("Speaker 1", "Synthetic source", "Synthetic translation", 41, now) { EvidenceId = "first-evidence" });
            overlay.ApplySpeakerCorrection(41, new(false, "Synthetic correction", "person-a", "Mira", true), "first-evidence");
            overlay.Publish(new TranslationDisplay("Speaker 2", "Synthetic revision", "Revised translation", 41, now.AddSeconds(1)) { EvidenceId = "new-evidence" });
            Check(overlay.HistoryEntry(41)!.Value.Speaker == "Mira" && overlay.HistoryEntry(41)!.Value.PersonId == "person-a", "a late revision cannot undo a manual person assignment");
            Check(overlay.HistoryEntry(41)!.Value.EvidenceId == "first-evidence", "late revisions preserve the exact corrected evidence needed for undo");
            overlay.ApplySpeakerCorrection(41, new(false, "Expired undo rejected", null, "Unidentified speaker", false, false));
            Check(overlay.HistoryEntry(41)!.Value.PersonId == "person-a", "rejected or expired feedback does not relabel the bubble");
            overlay.Publish(new TranslationDisplay("Speaker 2", "Older revision", "Must not win", 41, now.AddMilliseconds(100)));
            Check(overlay.HistoryEntry(41)!.Value.Translation == "Revised translation", "older revisions do not replace newer corrected bubbles");
            overlay.RefreshSpeakerNames(new(new[] { new SpeakerPerson("person-a", "Mira renamed", 1) }, true));
            Check(overlay.HistoryEntry(41)!.Value.Speaker == "Mira renamed", "rename refreshes an existing bubble without changing its identity");
            overlay.Publish(new TranslationDisplay("Speaker 1", "Next revision", "Another revision", 41, now.AddSeconds(2)));
            Check(overlay.HistoryEntry(41)!.Value.Speaker == "Mira renamed", "rename also updates the override for later revisions");
            overlay.RefreshSpeakerNames(new(Array.Empty<SpeakerPerson>(), true));
            Check(overlay.HistoryEntry(41)!.Value.Speaker == "Unidentified speaker" && overlay.HistoryEntry(41)!.Value.PersonId is null, "deleted person no longer labels historical bubbles");
            overlay.Publish(new TranslationDisplay("Speaker 1", "Final revision", "Final synthetic translation", 41, now.AddSeconds(3)));
            Check(overlay.HistoryEntry(41)!.Value.Speaker == "Unidentified speaker" && overlay.HistoryEntry(41)!.Value.PersonId is null, "a late revision cannot resurrect a deleted person");
            Check(overlay.HistoryEntryCount == 1, "correction and revisions preserve one bubble identity");
        }
        finally { overlay.Close(); }
        return checks;
    }
}

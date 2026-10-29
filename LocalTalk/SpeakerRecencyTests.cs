using System.Reflection;
using System.Text.Json;

namespace IncomingTranslator;

/// <summary>Isolated local metadata tests; no audio, real speaker data, extractor, models, or network.</summary>
internal static class SpeakerRecencyTests
{
    private static readonly string ModelSha = new('C', 64);

    // DesktopTests can synchronously await this on its STA without capturing its dispatcher.
    internal static Task<int> RunAsync(CancellationToken token = default) =>
        Task.Run(() => RunBackendAsync(token), token);

    private static async Task<int> RunBackendAsync(CancellationToken token)
    {
        int checks = 0;
        string folder = Path.Combine(Path.GetTempPath(), "SayWhat-SpeakerRecencyTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string Data(string name) => Path.Combine(folder, name);
        void Check(bool condition, string message)
        {
            token.ThrowIfCancellationRequested();
            if (!condition) throw new InvalidOperationException("Speaker recency test failed: " + message);
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
        DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        SpeakerAttribution Attribution() => new("Unidentified speaker", .7, false, 0)
        {
            CanLearn = true, SpeechSeconds = 2, EvidenceStart = now.AddSeconds(-2), EvidenceEnd = now
        };
        static SpeakerPerson Person(SpeakerFeedbackState state, string id) => state.People.Single(person => person.Id == id);
        SpeakerFeedbackAnchor Anchor(string id, string personId, DateTimeOffset observed) => new()
        {
            EvidenceId = id, PersonId = personId, ModelSha = ModelSha, Embedding = [1, 0],
            ObservedAtUtc = observed, SpeechSeconds = 2, ClippedFraction = 0,
            EvidenceStart = observed.AddSeconds(-2), EvidenceEnd = observed, RevisionToken = Guid.NewGuid().ToString("N")
        };

        // Existing Version 1 databases have no recency field. Reading and browsing must neither
        // fabricate use times nor rewrite the file, and subsequent ordinary changes remain V1.
        string oldPath = Data("old-v1.json");
        const string oldJson = "{\"Version\":1,\"InstructionAcknowledged\":false,\"People\":[{\"Id\":\"old-person\",\"Name\":\"Old friend\"}],\"Anchors\":[]}";
        File.WriteAllText(oldPath, oldJson);
        var old = new SpeakerFeedbackStore(oldPath);
        Check(old.State().People.Single().LastUsedAtUtc == default && old.RequirePerson("old-person").LastUsedAtUtc == default,
            "old V1 names have no invented manual-use timestamp");
        Check(File.ReadAllText(oldPath) == oldJson, "snapshot and lookup do not write or migrate on read");
        SpeakerFeedbackState oldSnapshot = old.State();
        old.SavePerson("Old friend renamed", "old-person");
        old.Acknowledge();
        Check(old.State().People.Single().LastUsedAtUtc == default, "rename and learning consent are not manual use");
        Check(oldSnapshot.People.Single().Name == "Old friend" && !oldSnapshot.InstructionAcknowledged,
            "a captured state is independent of later commits");
        using (JsonDocument saved = JsonDocument.Parse(File.ReadAllText(oldPath)))
            Check(saved.RootElement.GetProperty("Version").GetInt32() == 1, "optional recency field retains backwards-compatible schema version");

        string storePath = Data("store.json");
        var store = new SpeakerFeedbackStore(storePath);
        SpeakerPerson zara = store.SavePerson("Zara", null);
        SpeakerPerson alice = store.SavePerson("Alice", null);
        Check(zara.LastUsedAtUtc == default && alice.LastUsedAtUtc == default, "creating names does not mark them recently used");
        SpeakerFeedbackState original = store.State();
        DateTimeOffset firstUse = now.AddSeconds(1);
        store.ReplaceContribution("label-only-zara", null, zara.Id, firstUse);
        Check(Person(store.State(), zara.Id).LastUsedAtUtc == firstUse && Person(store.State(), zara.Id).ConfirmedSamples == 0,
            "known-person label-only use persists without a voice anchor");
        Check(original.People.All(person => person.LastUsedAtUtc == default), "older snapshots keep their original timestamps");
        Check(store.State().People.Select(person => person.Name).SequenceEqual(["Alice", "Zara"]),
            "management snapshot keeps stable alphabetical order");
        var exposedList = (IList<SpeakerPerson>)store.State().People;
        Reject<NotSupportedException>(() => exposedList[0] = exposedList[0] with { Name = "Caller mutation" },
            "snapshot people cannot be mutated through a collection cast");
        Check(new SpeakerFeedbackStore(storePath).RequirePerson(zara.Id).LastUsedAtUtc == firstUse,
            "manual-use recency survives store restart");
        SpeakerPerson renamed = store.SavePerson("Zara renamed", zara.Id);
        Check(renamed.LastUsedAtUtc == firstUse, "rename copy-on-write preserves recency");
        DateTimeOffset secondUse = now.AddSeconds(2);
        store.ReplaceContribution("label-only-alice", null, alice.Id, secondUse.ToOffset(TimeSpan.FromHours(9)));
        Check(store.RequirePerson(alice.Id).LastUsedAtUtc == secondUse && store.RequirePerson(alice.Id).LastUsedAtUtc.Offset == TimeSpan.Zero,
            "manual-use timestamps are persisted in UTC");
        Check(store.State().People.OrderByDescending(person => person.LastUsedAtUtc).ThenBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
            .Select(person => person.Id).SequenceEqual([alice.Id, zara.Id]), "recent ordering is based on successful manual use, not alphabet or rename");
        store.ReplaceContribution("backwards-clock", null, zara.Id, firstUse.AddMinutes(-1));
        Check(store.RequirePerson(zara.Id).LastUsedAtUtc == firstUse, "a backwards clock cannot demote prior manual recency");
        string beforeRejected = File.ReadAllText(storePath);
        Reject<ArgumentException>(() => store.ReplaceContribution("bad-person", null, "not-saved", now.AddHours(1)),
            "missing person cannot create a recency entry");
        Reject<ArgumentException>(() => store.ReplaceContribution("bad-time", null, usedAt: now), "orphaned use timestamp is rejected");
        Reject<InvalidDataException>(() => store.ReplaceContribution("bad-anchor", Anchor("bad-anchor", alice.Id, now) with { Embedding = [0, 0] }, alice.Id, now.AddHours(1)),
            "invalid training sample rejects the complete recency transaction");
        Check(File.ReadAllText(storePath) == beforeRejected && store.RequirePerson(alice.Id).LastUsedAtUtc == secondUse,
            "rejected transactions do not alter persisted or visible recency");
        store.DeletePerson(zara.Id);
        Check(new SpeakerFeedbackStore(storePath).State().People.Count == 1 && store.State().People.All(person => person.Id != zara.Id),
            "deleting a person removes their recency instead of retaining a hidden recent entry");
        Check(original.People.Count == 2, "deleting a person cannot mutate an already captured snapshot");

        // AI-off labels still update recency, but never load a model or claim to train a voice.
        string offPath = Data("off-feedback.json");
        using (var identifier = new AiSpeakerIdentifier(Data("off-anonymous.json"), offPath, () => now))
        {
            await identifier.SetRecognitionEnabledAsync(false, token).ConfigureAwait(false);
            SpeakerPerson first = await identifier.SaveSpeakerPersonAsync("First", token: token).ConfigureAwait(false);
            SpeakerPerson second = await identifier.SaveSpeakerPersonAsync("Second", token: token).ConfigureAwait(false);
            SpeakerAttribution evidence = identifier.RecordLabelOnlyObservation(Attribution());
            Check(identifier.SpeakerFeedbackSnapshot!.People.All(person => person.LastUsedAtUtc == default),
                "observing a sentence or opening the snapshot does not mark a person used");
            now = now.AddSeconds(10);
            SpeakerFeedbackResult corrected = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            DateTimeOffset firstManualTime = now;
            Check(corrected.Applied && !corrected.Learned && corrected.PersonId == first.Id && corrected.CanUndo,
                "AI-off correction is a successful label-only assignment");
            Check(Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == firstManualTime &&
                new SpeakerFeedbackStore(offPath).RequirePerson(first.Id).LastUsedAtUtc == firstManualTime,
                "AI-off correction updates both committed snapshot and disk");
            Check(!identifier.IsModelLoaded && identifier.ExtractorLoadAttempts == 0 && identifier.GetConfirmedAnchorsForTest(ModelSha).Count == 0,
                "label-only recency is completely inference-free");
            now = now.AddSeconds(5);
            await identifier.UndoSpeakerCorrectionAsync(evidence.EvidenceId, token).ConfigureAwait(false);
            Check(Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == firstManualTime &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == default,
                "undo neither fabricates use of the restored label nor changes actual past manual use");
            now = now.AddSeconds(5);
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            DateTimeOffset secondManualTime = now;
            now = now.AddSeconds(1);
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            secondManualTime = now;
            Check(Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == secondManualTime &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).ConfirmedSamples == 0,
                "repeating an actual label-only selection refreshes use without inventing anchors");
            now = now.AddSeconds(1);
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, null, "music", token).ConfigureAwait(false);
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, null, "unidentified", token).ConfigureAwait(false);
            Check(Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == firstManualTime &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == secondManualTime,
                "music and unknown corrections do not mark any person used");
            await RejectAsync<ArgumentException>(async () =>
                await identifier.CorrectSpeakerAsync(evidence.EvidenceId, "absent-person", token: token).ConfigureAwait(false),
                "rejected missing-person correction cannot update recency").ConfigureAwait(false);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await RejectAsync<OperationCanceledException>(async () =>
                await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: canceled.Token).ConfigureAwait(false),
                "a canceled selection cannot update recency").ConfigureAwait(false);
            Check(Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == firstManualTime &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == secondManualTime,
                "rejected and canceled assignments leave manual history unchanged");
            now = now.AddMinutes(6);
            SpeakerFeedbackResult expired = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            Check(expired.Applied && !expired.Learned && !expired.CanUndo && Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == now,
                "an expired-evidence manual label remains an actual use but never claims reusable learning");
        }
        Check(!File.Exists(Data("off-anonymous.json")), "recency operations do not create an anonymous-model database");

        // Repeated confirmations of the same observation replace one anchor. Undo restores its
        // prior provenance, not a new manual-use time for either the old or the selected person.
        using (var identifier = new AiSpeakerIdentifier(Data("learn-anonymous.json"), Data("learn-feedback.json"), () => now))
        {
            await identifier.AcknowledgeSpeakerLearningAsync(token).ConfigureAwait(false);
            SpeakerPerson first = await identifier.SaveSpeakerPersonAsync("Named A", token: token).ConfigureAwait(false);
            SpeakerPerson second = await identifier.SaveSpeakerPersonAsync("Named B", token: token).ConfigureAwait(false);
            SpeakerAttribution evidence = identifier.AddSyntheticFeedbackEvidence([1, 0], ModelSha, Attribution());
            SpeakerFeedbackResult firstResult = await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            now = now.AddSeconds(1);
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            DateTimeOffset firstManualTime = now;
            Check(firstResult.Learned && identifier.GetConfirmedAnchorsForTest(ModelSha).Count == 1 &&
                Person(identifier.SpeakerFeedbackSnapshot!, first.Id).ConfirmedSamples == 1,
                "repeated same-person confirmation is one sample, not duplicate training data");
            now = now.AddSeconds(1);
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, second.Id, token: token).ConfigureAwait(false);
            DateTimeOffset secondManualTime = now;
            Check(Person(identifier.SpeakerFeedbackSnapshot!, first.Id).ConfirmedSamples == 0 &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).ConfirmedSamples == 1 &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == secondManualTime,
                "reassignment atomically moves the sample and updates only the chosen person's recency");
            now = now.AddSeconds(1);
            SpeakerFeedbackResult undo = await identifier.UndoSpeakerCorrectionAsync(evidence.EvidenceId, token).ConfigureAwait(false);
            Check(undo.Applied && undo.PersonId == first.Id && identifier.GetConfirmedAnchorsForTest(ModelSha).Single().PersonId == first.Id,
                "undo restores the previous confirmed person without a duplicate sample");
            Check(Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == firstManualTime &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == secondManualTime,
                "undo does not turn either person into a fabricated newer manual use");
            string saved = File.ReadAllText(Data("learn-feedback.json"));
            Check(identifier.MatchConfirmedPersonForTest([1, 0], ModelSha) == first.Id,
                "existing confirmed fingerprints still match automatically");
            Check(File.ReadAllText(Data("learn-feedback.json")) == saved &&
                Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == firstManualTime,
                "automatic matching does not refresh manual recency or persist a new sample");
            Check(identifier.ExtractorLoadAttempts == 0 && !identifier.IsModelLoaded, "synthetic confirmations never initialize an extractor");
        }

        // A real local file-sharing failure rejects the entire transaction before observation
        // history/current labels change. No administrator permission or live data is involved.
        string failedPath = Data("failed-feedback.json");
        using (var identifier = new AiSpeakerIdentifier(Data("failed-anonymous.json"), failedPath, () => now))
        {
            await identifier.AcknowledgeSpeakerLearningAsync(token).ConfigureAwait(false);
            SpeakerPerson first = await identifier.SaveSpeakerPersonAsync("Write A", token: token).ConfigureAwait(false);
            SpeakerPerson second = await identifier.SaveSpeakerPersonAsync("Write B", token: token).ConfigureAwait(false);
            SpeakerAttribution evidence = identifier.AddSyntheticFeedbackEvidence([1, 0], ModelSha, Attribution());
            await identifier.CorrectSpeakerAsync(evidence.EvidenceId, first.Id, token: token).ConfigureAwait(false);
            DateTimeOffset committedTime = now;
            string committedJson = File.ReadAllText(failedPath);
            now = now.AddSeconds(1);
            using (var lockFile = new FileStream(failedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await RejectAsync<IOException>(async () =>
                    await identifier.CorrectSpeakerAsync(evidence.EvidenceId, second.Id, token: token).ConfigureAwait(false),
                    "failed anchor-and-recency write is reported rather than applied").ConfigureAwait(false);
                Check(File.ReadAllText(failedPath) == committedJson &&
                    Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == committedTime &&
                    Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == default &&
                    identifier.GetConfirmedAnchorsForTest(ModelSha).Single().PersonId == first.Id,
                    "failed disk replacement leaves names, recency, counts, and anchor ownership at the last commit");
            }
            SpeakerFeedbackResult undo = await identifier.UndoSpeakerCorrectionAsync(evidence.EvidenceId, token).ConfigureAwait(false);
            Check(undo.Applied && undo.PersonId is null && undo.Label == "Unidentified speaker" && !undo.CanUndo,
                "failed assignment did not alter the cached label or append an undo entry");
            Check(Person(identifier.SpeakerFeedbackSnapshot!, first.Id).LastUsedAtUtc == committedTime &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == default,
                "undo after failure keeps only the actual successful use history");

            await identifier.SetRecognitionEnabledAsync(false, token).ConfigureAwait(false);
            SpeakerAttribution labelOnly = identifier.RecordLabelOnlyObservation(Attribution());
            string beforeLabelOnlyFailure = File.ReadAllText(failedPath);
            using (var lockFile = new FileStream(failedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await RejectAsync<IOException>(async () =>
                    await identifier.CorrectSpeakerAsync(labelOnly.EvidenceId, second.Id, token: token).ConfigureAwait(false),
                    "label-only assignments must also report failed recency persistence").ConfigureAwait(false);
            SpeakerFeedbackResult noUndo = await identifier.UndoSpeakerCorrectionAsync(labelOnly.EvidenceId, token).ConfigureAwait(false);
            Check(!noUndo.Applied && noUndo.PersonId is null && File.ReadAllText(failedPath) == beforeLabelOnlyFailure &&
                Person(identifier.SpeakerFeedbackSnapshot!, second.Id).LastUsedAtUtc == default,
                "a failed label-only save leaves the visible/cache label and recency unchanged");
            Check(!Directory.EnumerateFiles(folder, "failed-feedback.json.tmp-*").Any(), "failed transactions remove only their own temporary writes");
        }

        // The picker snapshot does not wait behind inference. Legacy async state access may wait,
        // but the fast local read stays usable while the isolated serialization gate is held.
        var fast = new AiSpeakerIdentifier(Data("fast-anonymous.json"), Data("fast-feedback.json"), () => now);
        await fast.SaveSpeakerPersonAsync("Fast friend", token: token).ConfigureAwait(false);
        var gate = (SemaphoreSlim?)typeof(AiSpeakerIdentifier).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(fast)
            ?? throw new InvalidOperationException("Speaker recency fixture could not find its isolated gate.");
        await gate.WaitAsync(token).ConfigureAwait(false);
        Task<SpeakerFeedbackState> queuedState = fast.GetSpeakerFeedbackStateAsync(token);
        try
        {
            Check(!queuedState.IsCompleted, "legacy state access is actually queued behind the held test gate");
            SpeakerFeedbackState? snapshot = await Task.Run(() => ((ISpeakerFeedbackHost)fast).SpeakerFeedbackSnapshot, token)
                .WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            Check(snapshot?.People.Single().Name == "Fast friend" && fast.ExtractorLoadAttempts == 0,
                "quick snapshot returns committed metadata without waiting for inference or loading a model");
        }
        finally { gate.Release(); }
        await queuedState.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        fast.Dispose();
        await fast.DisposalCompleted.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        Check(fast.SpeakerFeedbackSnapshot is null, "closed identifier returns an unavailable fast snapshot without blocking its picker");
        await RejectAsync<ObjectDisposedException>(async () =>
            await fast.GetSpeakerFeedbackStateAsync(token).ConfigureAwait(false),
            "async fallback still reports the actionable closed-identifier error").ConfigureAwait(false);
        string unreadablePath = Data("unreadable.json");
        File.WriteAllText(unreadablePath, "{\"Version\":999,\"People\":[],\"Anchors\":[]}");
        using (var unreadable = new AiSpeakerIdentifier(Data("unreadable-anonymous.json"), unreadablePath, () => now))
        {
            Check(unreadable.SpeakerFeedbackSnapshot is null, "unreadable metadata is unavailable rather than a false empty picker or opening failure");
            await RejectAsync<InvalidDataException>(async () =>
                await unreadable.GetSpeakerFeedbackStateAsync(token).ConfigureAwait(false),
                "async fallback still reports unreadable metadata for recovery").ConfigureAwait(false);
        }
        Check(File.ReadAllText(unreadablePath).Contains("999", StringComparison.Ordinal), "failed snapshot preserves unreadable data for recovery");
        Check(((ISpeakerFeedbackHost)new LegacyHost()).SpeakerFeedbackSnapshot is null, "legacy feedback hosts retain the optional async fallback");

        // One atomically published database owns names, recency, and sample counts. Concurrent
        // fast readers must never combine fields from two successive commits.
        var concurrent = new SpeakerFeedbackStore(Data("concurrent.json"));
        SpeakerPerson concurrentPerson = concurrent.SavePerson("Concurrent", null);
        DateTimeOffset start = now;
        Task writer = Task.Run(() =>
        {
            for (int index = 1; index <= 32; index++)
            {
                token.ThrowIfCancellationRequested();
                string id = "atomic-" + index;
                concurrent.ReplaceContribution(id, Anchor(id, concurrentPerson.Id, start.AddSeconds(index)), concurrentPerson.Id, start.AddSeconds(index));
            }
        }, token);
        bool coherent = true;
        int reads = 0;
        do
        {
            SpeakerPerson person = concurrent.State().People.Single();
            coherent &= person.ConfirmedSamples == 0 ? person.LastUsedAtUtc == default : person.LastUsedAtUtc == start.AddSeconds(person.ConfirmedSamples);
            reads++;
            await Task.Yield();
        } while (!writer.IsCompleted);
        await writer.ConfigureAwait(false);
        Check(coherent && reads > 0 && concurrent.State().People.Single().ConfirmedSamples == 32,
            "concurrent snapshots contain consistent recency and confirmed counts from one committed database");
        Check(new SpeakerFeedbackStore(Data("concurrent.json")).State().People.Single().LastUsedAtUtc == start.AddSeconds(32),
            "the final concurrent snapshot matches persisted recency");
        return checks;
    }

    private sealed class LegacyHost : ISpeakerFeedbackHost
    {
        public Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default) => Task.FromResult(new SpeakerFeedbackState(Array.Empty<SpeakerPerson>(), false));
        public Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default) => throw new NotSupportedException();
        public Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default) => throw new NotSupportedException();
    }
}

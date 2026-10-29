using System.Text.Json;

namespace IncomingTranslator;

// Synthetic embeddings and isolated files only. No native extractor, audio capture or network.
internal static class SpeakerSuggestionReviewTests
{
    internal static async Task<int> RunAsync(string directory, CancellationToken token = default)
    {
        string root = Path.Combine(Path.GetFullPath(directory), "person-review-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Person review check failed: " + name);
            checks++;
        }
        string sha = new('A', 64), otherSha = new('B', 64);
        DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        string PathFor(string file) => Path.Combine(root, file);
        SpeakerAttribution Voice(string? suggested = null, DateTimeOffset? start = null) => new("Unidentified speaker", 0, false, 0)
        {
            CanLearn = true, PersonId = suggested, SuggestedPersonId = suggested, PersonConfirmed = false,
            EvidenceStart = start ?? now, EvidenceEnd = (start ?? now).AddSeconds(2)
        };
        SpeakerPerson Person(AiSpeakerIdentifier identifier, string id) => identifier.SpeakerFeedbackSnapshot!.People.Single(person => person.Id == id);
        string feedbackPath = PathFor("feedback.json");
        using (var id = new AiSpeakerIdentifier(PathFor("anonymous.json"), feedbackPath, () => now))
        {
            SpeakerPerson a = await id.SaveSpeakerPersonAsync("Ringo", token: token);
            SpeakerPerson b = await id.SaveSpeakerPersonAsync("Other friend", token: token);
            string before = File.ReadAllText(feedbackPath);
            SpeakerAttribution candidate = id.AddSyntheticFeedbackEvidence([1, 0], sha, Voice(a.Id));
            Check(File.ReadAllText(feedbackPath) == before && Person(id, a.Id).ConfirmedSamples == 0 &&
                Person(id, a.Id).ConfirmedSuggestions == 0 && Person(id, a.Id).RejectedSuggestions == 0,
                "a guess never writes a reference or review");
            Check(!id.RegisterSpeakerSuggestion(candidate.EvidenceId, b.Id), "first suggested identity is immutable");
            SpeakerFeedbackResult badCandidate = await id.ReviewSpeakerSuggestionAsync(candidate.EvidenceId, b.Id, false, token);
            Check(!badCandidate.Applied && File.ReadAllText(feedbackPath) == before, "wrong candidate request is rejected without mutation");
            SpeakerFeedbackResult noAck = await id.ReviewSpeakerSuggestionAsync(candidate.EvidenceId, a.Id, false, token);
            Check(noAck.Applied && !noAck.Learned && !noAck.NegativeLearned && noAck.PersonId is null &&
                noAck.SuggestionConfirmed == false && noAck.CanUndo, "no before consent still fixes label");
            Check(Person(id, a.Id).RejectedSuggestions == 1 && id.GetNegativeAnchorsForTest(sha).Count == 0,
                "no consent records review only");
            string oneReview = File.ReadAllText(feedbackPath);
            await id.ReviewSpeakerSuggestionAsync(candidate.EvidenceId, a.Id, false, token);
            Check(File.ReadAllText(feedbackPath) == oneReview && Person(id, a.Id).RejectedSuggestions == 1,
                "double-click review is deduplicated without rewrite");
            SpeakerFeedbackResult restoredGuess = await id.UndoSpeakerCorrectionAsync(candidate.EvidenceId, token);
            Check(restoredGuess.Applied && !restoredGuess.IsManuallyAttributed && restoredGuess.PersonId == a.Id &&
                restoredGuess.SuggestionConfirmed is null && Person(id, a.Id).RejectedSuggestions == 0,
                "undo restores tentative identity and removes review");
            await id.AcknowledgeSpeakerLearningAsync(token);
            SpeakerFeedbackResult yes = await id.ReviewSpeakerSuggestionAsync(candidate.EvidenceId, a.Id, true, token);
            Check(yes.Learned && yes.SuggestionConfirmed == true && !yes.NegativeLearned && Person(id, a.Id).ConfirmedSuggestions == 1 &&
                id.GetConfirmedAnchorsForTest(sha).Count == 1, "yes stores positive and review atomically");
            string seed = Person(id, a.Id).VoiceAvatarSeed!;
            SpeakerFeedbackResult relabel = await id.CorrectSpeakerAsync(candidate.EvidenceId, b.Id, token: token);
            Check(relabel.Learned && relabel.NegativeLearned && relabel.SuggestionConfirmed == false &&
                Person(id, a.Id).ConfirmedSuggestions == 0 && Person(id, a.Id).RejectedSuggestions == 1 &&
                Person(id, b.Id).ConfirmedSamples == 1 && Person(id, b.Id).ConfirmedSuggestions == 0,
                "manual relabel records original wrong suggestion, not another guessed review");
            Check(id.GetConfirmedAnchorsForTest(sha).Single().PersonId == b.Id &&
                id.GetNegativeAnchorsForTest(sha).Single().PersonId == a.Id && Person(id, a.Id).VoiceAvatarSeed == seed,
                "relabel moves positive and adds person-specific negative without changing decoration");
            await id.UndoSpeakerCorrectionAsync(candidate.EvidenceId, token);
            Check(Person(id, a.Id).ConfirmedSuggestions == 1 && Person(id, a.Id).RejectedSuggestions == 0 &&
                Person(id, a.Id).ConfirmedSamples == 1 && Person(id, b.Id).ConfirmedSamples == 0 && id.GetNegativeAnchorsForTest(sha).Count == 0,
                "undo restores positive, negative and reviewed counters together");
            await id.UndoSpeakerCorrectionAsync(candidate.EvidenceId, token);
            Check(Person(id, a.Id).ConfirmedSuggestions == 0 && Person(id, a.Id).ConfirmedSamples == 0,
                "second undo removes the sole positive review contribution");

            // Train independent anchors, then reject an unrelated clean voice which resembled A.
            var anchor = id.AddSyntheticFeedbackEvidence([1, 0], sha, Voice(start: now.AddSeconds(10)));
            await id.CorrectSpeakerAsync(anchor.EvidenceId, a.Id, token: token);
            Check(id.MatchConfirmedPersonForTest([1, 0], sha) == a.Id, "confirmed anchor matches before a rejection");
            var falseGuess = id.AddSyntheticFeedbackEvidence([.92f, .392f], sha, Voice(a.Id, now.AddSeconds(20)));
            Check(id.MatchConfirmedPersonForTest([.92f, .392f], sha) == a.Id, "synthetic false voice was initially plausible");
            SpeakerFeedbackResult rejected = await id.ReviewSpeakerSuggestionAsync(falseGuess.EvidenceId, a.Id, false, token);
            Check(rejected.NegativeLearned && !rejected.Learned && rejected.PersonId is null && Person(id, a.Id).RejectedSuggestions == 1,
                "no records clear negative example");
            Check(id.MatchConfirmedPersonForTest([.92f, .392f], sha) is null && id.MatchConfirmedPersonForTest([1, 0], sha) == a.Id &&
                id.MatchConfirmedPersonForTest([.92f, .392f], otherSha) is null,
                "near-negative veto does not erase distinct valid positive or cross model");
            string afterReject = File.ReadAllText(feedbackPath);
            var changedGuess = id.AddSyntheticFeedbackEvidence([.92f, .392f], sha, Voice(b.Id, now.AddSeconds(20.02)));
            SpeakerFeedbackResult mismatchedRevision = await id.ReviewSpeakerSuggestionAsync(changedGuess.EvidenceId, b.Id, true, token);
            Check(!mismatchedRevision.Applied && File.ReadAllText(feedbackPath) == afterReject,
                "a revised guess cannot silently replace the original reviewed candidate");
            var revised = id.AddSyntheticFeedbackEvidence([.92f, .392f], sha, Voice(a.Id, now.AddSeconds(20.04)));
            await id.ReviewSpeakerSuggestionAsync(revised.EvidenceId, a.Id, false, token);
            Check(Person(id, a.Id).RejectedSuggestions == 1 && id.GetNegativeAnchorsForTest(sha).Count == 1,
                "revised interval remains one negative and reviewed outcome");
            SpeakerFeedbackResult conflict = await id.UndoSpeakerCorrectionAsync(falseGuess.EvidenceId, token);
            Check(!conflict.Applied && File.ReadAllText(feedbackPath) != afterReject,
                "older undo cannot undo a newer revision's contribution");
            await id.UndoSpeakerCorrectionAsync(revised.EvidenceId, token);
            Check(Person(id, a.Id).RejectedSuggestions == 1 && id.GetNegativeAnchorsForTest(sha).Count == 1,
                "newer undo restores prior rejected revision rather than removing unrelated state");

            var weak = id.AddSyntheticFeedbackEvidence([0, 1], sha, Voice(a.Id, now.AddSeconds(30)), clearSeconds: .5);
            SpeakerFeedbackResult shortResult = await id.ReviewSpeakerSuggestionAsync(weak.EvidenceId, a.Id, false, token);
            Check(!shortResult.NegativeLearned && shortResult.SuggestionConfirmed == false && Person(id, a.Id).RejectedSuggestions == 2,
                "short rejection records decision without teaching");
            var clipped = id.AddSyntheticFeedbackEvidence([0, 1], sha, Voice(a.Id, now.AddSeconds(40)), clippedFraction: .02);
            Check(!(await id.ReviewSpeakerSuggestionAsync(clipped.EvidenceId, a.Id, false, token)).NegativeLearned,
                "clipped evidence never teaches negatives");
            var mixed = id.AddSyntheticFeedbackEvidence([0, 1], sha, Voice(a.Id, now.AddSeconds(50)) with { IsMixed = true });
            Check(!(await id.ReviewSpeakerSuggestionAsync(mixed.EvidenceId, a.Id, true, token)).Learned,
                "mixed confirmation never teaches positive references");
            var music = id.AddSyntheticFeedbackEvidence([0, 1], sha, Voice(a.Id, now.AddSeconds(60)));
            Check(!(await id.CorrectSpeakerAsync(music.EvidenceId, null, "music", token)).NegativeLearned,
                "music label never saves ambiguous negative voice evidence");
            await id.CorrectSpeakerAsync(music.EvidenceId, null, "unidentified", token);
            SpeakerFeedbackResult undoManualUnknown = await id.UndoSpeakerCorrectionAsync(music.EvidenceId, token);
            Check(undoManualUnknown.IsManuallyAttributed && undoManualUnknown.Label == "Music / recording",
                "undo restores manual unknown/music as manual, not a renewed automatic guess");
            var noFingerprint = id.AddSyntheticFeedbackEvidence(null, "", Voice(start: now.AddSeconds(70)), canLearn: false);
            Check(id.RegisterSpeakerSuggestion(noFingerprint.EvidenceId, a.Id) && !id.RegisterSpeakerSuggestion(noFingerprint.EvidenceId, b.Id),
                "Groups inherited candidate is metadata only and immutable");
            Check(!(await id.ReviewSpeakerSuggestionAsync(noFingerprint.EvidenceId, a.Id, false, token)).NegativeLearned,
                "Groups label-only rejection updates review without invention");
            Check(id.ExtractorLoadAttempts == 0 && !id.IsModelLoaded, "all review operations are inference-free");

            // Data written before this feature still reads, and new counters/references survive restart.
            SpeakerPerson saved = new SpeakerFeedbackStore(feedbackPath).RequirePerson(a.Id);
            Check(saved.RejectedSuggestions == Person(id, a.Id).RejectedSuggestions && saved.VoiceAvatarSeed == seed,
                "review totals and avatar persist across reload");
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(feedbackPath));
            Check(json.RootElement.GetProperty("Version").GetInt32() == 1 &&
                !File.ReadAllText(feedbackPath).Contains("Transcript", StringComparison.Ordinal), "version 1 retained without transcript persistence");
        }

        // A failed replacement cannot publish counters, references or a corrected cached identity.
        string blocked = PathFor("blocked.json");
        using (var id = new AiSpeakerIdentifier(PathFor("blocked-anonymous.json"), blocked, () => now))
        {
            SpeakerPerson person = await id.SaveSpeakerPersonAsync("Write failure", token: token);
            await id.AcknowledgeSpeakerLearningAsync(token);
            var candidate = id.AddSyntheticFeedbackEvidence([1, 0], sha, Voice(person.Id));
            string kept = blocked + ".kept";
            File.Move(blocked, kept);
            Directory.CreateDirectory(blocked);
            bool failed = false;
            try { await id.ReviewSpeakerSuggestionAsync(candidate.EvidenceId, person.Id, false, token); }
            catch (IOException) { failed = true; }
            Check(failed && Person(id, person.Id).RejectedSuggestions == 0 && id.GetNegativeAnchorsForTest(sha).Count == 0,
                "failed write leaves committed review and reference snapshots unchanged");
            Check(!(await id.UndoSpeakerCorrectionAsync(candidate.EvidenceId, token)).Applied,
                "failed write creates no undo entry or optimistic label change");
            Directory.Delete(blocked); File.Move(kept, blocked);
            Check((await id.ReviewSpeakerSuggestionAsync(candidate.EvidenceId, person.Id, false, token)).NegativeLearned,
                "a retry after write failure can commit successfully");
        }

        // Consensus: the strongest single similar reference cannot carry a multi-example identity.
        using (var id = new AiSpeakerIdentifier(PathFor("consensus-anonymous.json"), PathFor("consensus.json"), () => now))
        {
            SpeakerPerson a = await id.SaveSpeakerPersonAsync("Consensus person", token: token);
            await id.AcknowledgeSpeakerLearningAsync(token);
            var first = id.AddSyntheticFeedbackEvidence([1, 0], sha, Voice(start: now.AddSeconds(100)));
            await id.CorrectSpeakerAsync(first.EvidenceId, a.Id, token: token);
            Check(id.MatchConfirmedPersonForTest([.60f, .80f], sha) is null, "one-reference profile uses stricter sparse floor");
            var second = id.AddSyntheticFeedbackEvidence([0, 1], sha, Voice(start: now.AddSeconds(110)));
            await id.CorrectSpeakerAsync(second.EvidenceId, a.Id, token: token);
            Check(id.MatchConfirmedPersonForTest([1, 0], sha) is null && id.MatchConfirmedPersonForTest([.707f, .707f], sha) == a.Id,
                "multi-reference identity needs two consistent examples");
        }
        return checks;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace IncomingTranslator;

// Isolated synthetic embeddings and temporary files only. Never loads an extractor,
// captures speech, accesses saved user data, downloads files or uses GPU inference.
internal static class PersonAvatarSeedTests
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool valid, string detail)
        {
            if (!valid) throw new InvalidOperationException("Person avatar seed: " + detail);
            checks++;
        }
        void Reject<T>(Action action, string detail) where T : Exception
        {
            bool rejected = false;
            try { action(); } catch (T) { rejected = true; }
            Check(rejected, detail);
        }
        string path = Path.Combine(directory, "voice-avatar-people.json");
        var store = new SpeakerFeedbackStore(path);
        SpeakerPerson alice = store.SavePerson("Alice", null);
        SpeakerPerson bob = store.SavePerson("Bob", null);
        string modelA = new('A', 64), modelB = new('B', 64);
        DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        SpeakerFeedbackAnchor Anchor(string evidence, string personId, float[] embedding, string? model = null,
            DateTimeOffset? observed = null) => new()
        {
            EvidenceId = evidence, PersonId = personId, ModelSha = model ?? modelA, Embedding = embedding,
            ObservedAtUtc = observed ?? now, SpeechSeconds = 2, ClippedFraction = 0,
            EvidenceStart = now.AddSeconds(-2), EvidenceEnd = now, RevisionToken = Guid.NewGuid().ToString("N")
        };

        string fallback = PersonAvatarSeed.Resolve(alice);
        Check(alice.VoiceAvatarSeed is null && PersonAvatarSeed.Canonical(fallback) == fallback,
            "unenrolled people have a valid deterministic ID fallback, not a invented voice seed");
        Check(fallback == PersonAvatarSeed.Resolve(new SpeakerFeedbackStore(path).RequirePerson(alice.Id)),
            "ID fallback survives restart without enrolled voice data");
        Check(PersonAvatarSeed.Resolve(alice with { Name = "Different name", TranslationCount = 999, ConfirmedSamples = 99 }) == fallback,
            "name, translation count and example count cannot drive generated decoration");
        Check(fallback != PersonAvatarSeed.Resolve(bob), "distinct saved people have different fallback decoration");

        string expected = PersonAvatarSeed.FromVoice(alice.Id, modelA, new float[] { 1, 0 });
        Check(expected == PersonAvatarSeed.FromVoice(alice.Id, modelA.ToLowerInvariant(), new float[] { 1, 0 }),
            "equivalent model hashes canonicalize to one voice seed");
        Check(expected != PersonAvatarSeed.FromVoice(alice.Id, modelA, new float[] { 0, 1 }),
            "actual normalized voice features—not counters—change the initial voice seed");
        Check(expected != PersonAvatarSeed.FromVoice(alice.Id, modelB, new float[] { 1, 0 }) &&
            expected != PersonAvatarSeed.FromVoice(bob.Id, modelA, new float[] { 1, 0 }),
            "model provenance and stable local person identity participate in the seed");
        Check(PersonAvatarSeed.Canonical(expected.ToUpperInvariant()) == expected &&
            PersonAvatarSeed.Canonical("../photo.png") is null && PersonAvatarSeed.Canonical(new string('g', 64)) is null,
            "only canonicalizable fixed-length hexadecimal decoration is accepted");
        Reject<ArgumentException>(() => PersonAvatarSeed.FromVoice(alice.Id, modelA, new float[] { 0, 0 }), "zero voice vectors cannot generate decoration");
        Reject<ArgumentException>(() => PersonAvatarSeed.FromVoice(alice.Id, modelA, new float[] { float.NaN, 0 }), "nonfinite voice vectors cannot generate decoration");
        Reject<ArgumentException>(() => PersonAvatarSeed.FromVoice(alice.Id, modelA, new float[4097]), "oversized voice vectors are bounded");
        Reject<ArgumentException>(() => PersonAvatarSeed.FromVoice(alice.Id, "invalid-model", new float[] { 1, 0 }), "unverified model provenance is refused");

        store.ReplaceContribution("first-clear-voice", Anchor("first-clear-voice", alice.Id, [1, 0]));
        alice = store.RequirePerson(alice.Id);
        Check(alice.VoiceAvatarSeed == expected && PersonAvatarSeed.Resolve(alice) == expected,
            "first accepted confirmed embedding freezes a voice-derived avatar seed");
        Check(new SpeakerFeedbackStore(path).RequirePerson(alice.Id).VoiceAvatarSeed == expected,
            "confirmed voice seed is durable after a successful normal feedback commit");
        store.ReplaceContribution("first-clear-voice", Anchor("first-clear-voice", alice.Id, [0, 1]));
        store.ReplaceContribution("other-model-voice", Anchor("other-model-voice", alice.Id, [0, 1], modelB));
        store.SavePerson("Alice renamed", alice.Id);
        store.RecordTranslation(new string('c', 32) + ":1", alice.Id, now);
        Check(store.RequirePerson(alice.Id).VoiceAvatarSeed == expected && store.RequirePerson(alice.Id).TranslationCount == 1,
            "later voice corrections, model changes, rename and counts preserve the frozen seed");
        store.SetPersonPhoto(alice.Id, null);
        Check(store.RequirePerson(alice.Id).VoiceAvatarSeed == expected, "photo preference changes do not erase voice decoration");

        SpeakerFeedbackAnchor previous = store.Contribution("first-clear-voice")!;
        store.ReplaceContribution("first-clear-voice", previous with { PersonId = bob.Id, RevisionToken = Guid.NewGuid().ToString("N") });
        string bobSeed = store.RequirePerson(bob.Id).VoiceAvatarSeed!;
        store.ReplaceContribution("first-clear-voice", previous); // Same persistence path used by undo.
        Check(store.RequirePerson(alice.Id).VoiceAvatarSeed == expected && store.RequirePerson(bob.Id).VoiceAvatarSeed == bobSeed &&
            store.Contribution("first-clear-voice")!.PersonId == alice.Id,
            "undo restores voice evidence without changing decorative seeds or person identity");
        store.ReplaceContribution("first-clear-voice", null);
        store.ReplaceContribution("other-model-voice", null);
        Check(store.RequirePerson(alice.Id).ConfirmedSamples == 0 && store.RequirePerson(alice.Id).VoiceAvatarSeed == expected,
            "removing learned evidence does not regenerate the already frozen decorative hash");

        // A read of old data is inert. The deterministic migration becomes durable
        // only alongside the next authorized, successful normal metadata write.
        string legacyPath = Path.Combine(directory, "legacy-avatar-people.json");
        string legacyId = "legacy-person";
        SpeakerFeedbackAnchor early = Anchor("early", legacyId, [1, 0], observed: now.AddSeconds(-10));
        SpeakerFeedbackAnchor late = Anchor("late", legacyId, [0, 1]);
        string legacyContents = JsonSerializer.Serialize(new { Version = 1, People = new[] { new { Id = legacyId, Name = "Legacy friend" } },
            Anchors = new[] { late, early } });
        File.WriteAllText(legacyPath, legacyContents);
        var legacy = new SpeakerFeedbackStore(legacyPath);
        string legacySeed = PersonAvatarSeed.FromVoice(legacyId, modelA, early.Embedding);
        Check(legacy.RequirePerson(legacyId).VoiceAvatarSeed == legacySeed && File.ReadAllText(legacyPath) == legacyContents,
            "older confirmed people derive from their earliest retained example without a read-time disk write");
        legacy.SavePerson("Legacy renamed", legacyId);
        Check(new SpeakerFeedbackStore(legacyPath).RequirePerson(legacyId).VoiceAvatarSeed == legacySeed &&
            JsonNode.Parse(File.ReadAllText(legacyPath))!["People"]![0]!["VoiceAvatarSeed"]!.GetValue<string>() == legacySeed,
            "a later normal commit persists the migrated decoration without changing voice examples");

        string badDecorationPath = Path.Combine(directory, "bad-decoration-people.json");
        string badDecoration = JsonSerializer.Serialize(new { Version = 1,
            People = new[] { new { Id = "untrained", Name = "Still visible", VoiceAvatarSeed = "not-a-seed" } },
            Anchors = Array.Empty<SpeakerFeedbackAnchor>() });
        File.WriteAllText(badDecorationPath, badDecoration);
        SpeakerPerson repaired = new SpeakerFeedbackStore(badDecorationPath).RequirePerson("untrained");
        Check(repaired.Name == "Still visible" && repaired.VoiceAvatarSeed is null &&
            PersonAvatarSeed.Resolve(repaired) == PersonAvatarSeed.FromPersonId("untrained") && File.ReadAllText(badDecorationPath) == badDecoration,
            "corrupt optional decoration cannot hide saved people or trigger a repair write");

        string blockedPath = Path.Combine(directory, "blocked-avatar-people.json");
        var blocked = new SpeakerFeedbackStore(blockedPath);
        SpeakerPerson unseeded = blocked.SavePerson("Unseeded", null);
        string beforeBlocked = File.ReadAllText(blockedPath);
        using (var held = new FileStream(blockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject<IOException>(() => blocked.ReplaceContribution("never-committed", Anchor("never-committed", unseeded.Id, [1, 0])),
                "failed persistence does not claim a confirmed voice/avatar update");
        Check(blocked.RequirePerson(unseeded.Id).VoiceAvatarSeed is null && blocked.RequirePerson(unseeded.Id).ConfirmedSamples == 0 &&
            File.ReadAllText(blockedPath) == beforeBlocked, "failed avatar commit retains both visible and saved pre-enrollment state");
        Check(!Directory.EnumerateFiles(directory, Path.GetFileName(blockedPath) + ".tmp-*", SearchOption.TopDirectoryOnly).Any(),
            "failed avatar persistence does not leak an uncommitted temporary database");

        string invalidBefore = File.ReadAllText(blockedPath);
        Reject<InvalidDataException>(() => blocked.ReplaceContribution("invalid-example", Anchor("invalid-example", unseeded.Id, [1, 0]) with { SpeechSeconds = .4 }),
            "an ineligible voice example cannot initialize decoration");
        Check(blocked.RequirePerson(unseeded.Id).VoiceAvatarSeed is null && File.ReadAllText(blockedPath) == invalidBefore,
            "rejected evidence leaves decoration and saved data unchanged");
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
            Check(document.RootElement.GetProperty("Version").GetInt32() == 1 &&
                !File.ReadAllText(path).Contains("Transcript", StringComparison.OrdinalIgnoreCase),
                "optional decorative field is backwards compatible and stores no conversation text");
        return checks;
    }
}

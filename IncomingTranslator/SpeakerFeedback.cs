using System.IO;
using System.Text.Json;

namespace IncomingTranslator;

internal sealed record SpeakerPerson(string Id, string Name, int ConfirmedSamples)
{
    // Manual assignments only. Missing in older Version 1 files means never explicitly used.
    public DateTimeOffset LastUsedAtUtc { get; init; }
    public string? AvatarFileName { get; init; }
    // Decorative hash derived once from a confirmed voice; null before enrollment.
    // It is not used for matching and never contains the underlying embedding.
    public string? VoiceAvatarSeed { get; init; }
    public long TranslationCount { get; init; }
    public DateTimeOffset LastTranslatedAtUtc { get; init; }
    // Outcomes of explicitly reviewed suggestions, not recognition confidence or all guesses.
    public long ConfirmedSuggestions { get; init; }
    public long RejectedSuggestions { get; init; }
    // Null means no model is currently loaded. Counts describe examples, never accuracy.
    public int? CurrentModelConfirmedSamples { get; init; }
}
internal sealed record SpeakerFeedbackState(IReadOnlyList<SpeakerPerson> People, bool InstructionAcknowledged);
internal sealed record SpeakerFeedbackResult(bool Learned, string Message, string? PersonId, string Label, bool CanUndo, bool Applied = true)
{
    public string? SuggestedPersonId { get; init; }
    public bool? SuggestionConfirmed { get; init; }
    public bool NegativeLearned { get; init; }
    public bool IsManuallyAttributed { get; init; } = true;
}

internal interface ISpeakerFeedbackHost
{
    // Optional inference-free snapshot for a picker. Older/inert hosts can retain the async path.
    SpeakerFeedbackState? SpeakerFeedbackSnapshot => null;
    Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default);
    Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default);
    Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default);
    Task<SpeakerPerson> SetPersonPhotoAsync(string personId, string? sourcePath, CancellationToken token = default) =>
        Task.FromException<SpeakerPerson>(new NotSupportedException("Person pictures are not available in this host."));
    Task RecordPersonTranslationAsync(string translationId, string? personId, DateTimeOffset at, CancellationToken token = default) => Task.CompletedTask;
    Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default);
    Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default);
    Task<SpeakerFeedbackResult> ReviewSpeakerSuggestionAsync(string evidenceId, string suggestedPersonId, bool confirmed, CancellationToken token = default) =>
        Task.FromException<SpeakerFeedbackResult>(new NotSupportedException("Person suggestion reviews are not available in this host."));
    Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default);
}

// A review is an explicit user decision. An unreviewed automatic guess is never persisted here.
// Opaque aliases make revised transcriptions one decision rather than several successful guesses.
internal sealed record SpeakerSuggestionReview
{
    public string EvidenceId { get; init; } = "";
    public string SuggestedPersonId { get; init; } = "";
    public bool Confirmed { get; init; }
    public string RevisionToken { get; init; } = "";
    public string[] EvidenceAliases { get; init; } = Array.Empty<string>();
    public DateTimeOffset ReviewedAtUtc { get; init; }
    internal SpeakerSuggestionReview Copy() => this with { EvidenceAliases = EvidenceAliases.ToArray() };
}

internal sealed record SpeakerFeedbackContribution(SpeakerFeedbackAnchor? Positive, SpeakerFeedbackAnchor? Negative, SpeakerSuggestionReview? Review)
{
    internal string? RevisionToken => Review?.RevisionToken ?? Positive?.RevisionToken ?? Negative?.RevisionToken;
    internal string? EvidenceId => Review?.EvidenceId ?? Positive?.EvidenceId ?? Negative?.EvidenceId;
    internal IEnumerable<string> Aliases => (Positive?.EvidenceAliases ?? Array.Empty<string>())
        .Concat(Negative?.EvidenceAliases ?? Array.Empty<string>()).Concat(Review?.EvidenceAliases ?? Array.Empty<string>());
}

// Only voice fingerprints and their provenance are stored, never audio or transcript text. A saved
// name is independent of the model; its confirmed examples are meaningful only for that exact SHA.
internal sealed record SpeakerFeedbackAnchor
{
    public string EvidenceId { get; init; } = "";
    public string PersonId { get; init; } = "";
    public string ModelSha { get; init; } = "";
    public float[] Embedding { get; init; } = Array.Empty<float>();
    public DateTimeOffset ObservedAtUtc { get; init; }
    public double SpeechSeconds { get; init; }
    public double ClippedFraction { get; init; }
    public DateTimeOffset EvidenceStart { get; init; }
    public DateTimeOffset EvidenceEnd { get; init; }
    public string RevisionToken { get; init; } = "";
    public string[] EvidenceAliases { get; init; } = Array.Empty<string>();

    internal SpeakerFeedbackAnchor Copy() => this with { Embedding = Embedding.ToArray(), EvidenceAliases = EvidenceAliases.ToArray() };
}

// Mutations are copy-on-write: a failed disk write must not masquerade as a successful correction.
// The owner serializes calls with its semaphore. This store is also usable in isolated offline tests.
internal sealed class SpeakerFeedbackStore
{
    private const int MaxPeople = 64;
    private const int MaxAnchors = 4096;
    private const int MaxAnchorsPerPersonAndModel = 64;
    private const int MaxRecentTranslations = 4096;
    private const int MaxNegativeAnchors = 4096;
    private const int MaxNegativeAnchorsPerPersonAndModel = 64;
    private const int MaxRecentReviews = 4096;
    private readonly string _path;
    private readonly string? _legacyProfilesPath;
    private FeedbackDatabase _data = new();
    private Exception? _loadFailure;

    internal SpeakerFeedbackStore(string path, string? legacyProfilesPath = null)
    {
        _path = Path.GetFullPath(path);
        _legacyProfilesPath = legacyProfilesPath is null ? null : Path.GetFullPath(legacyProfilesPath);
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 64L * 1024 * 1024)
                throw new InvalidDataException("The saved speaker file is unexpectedly large.");
            _data = JsonSerializer.Deserialize<FeedbackDatabase>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("The saved speaker file is empty.");
            Validate(_data);
        }
        catch (Exception exception)
        {
            _data = new();
            _loadFailure = exception;
        }
    }

    internal SpeakerFeedbackState State(string? currentModelSha = null)
    {
        FeedbackDatabase data = ReadData();
        var counts = data.Anchors.GroupBy(anchor => anchor.PersonId).ToDictionary(group => group.Key, group => group.Count());
        SpeakerPerson[] people = data.People.Select(person => new SpeakerPerson(person.Id, person.Name, counts.GetValueOrDefault(person.Id))
        {
            LastUsedAtUtc = person.LastUsedAtUtc, AvatarFileName = person.AvatarFileName, VoiceAvatarSeed = person.VoiceAvatarSeed,
            TranslationCount = person.TranslationCount, LastTranslatedAtUtc = person.LastTranslatedAtUtc,
            ConfirmedSuggestions = person.ConfirmedSuggestions, RejectedSuggestions = person.RejectedSuggestions,
            CurrentModelConfirmedSamples = string.IsNullOrEmpty(currentModelSha) ? null :
                data.Anchors.Count(anchor => anchor.PersonId == person.Id && anchor.ModelSha == currentModelSha)
        }).OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        // Records and the read-only collection are independent of the committed database. A
        // concurrent commit cannot combine old names with new sample counts or mutate this view.
        return new(Array.AsReadOnly(people), data.InstructionAcknowledged);
    }

    internal SpeakerPerson RequirePerson(string personId)
    {
        FeedbackDatabase data = ReadData();
        PersonRecord? person = data.People.FirstOrDefault(item => item.Id == personId);
        return person is null ? throw new ArgumentException("That saved speaker no longer exists. Choose a current person.", nameof(personId)) : ToPerson(person, data);
    }

    internal SpeakerPerson SavePerson(string name, string? personId)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
            throw new ArgumentException("Use a speaker name of 1–64 characters without control characters.", nameof(name));
        FeedbackDatabase next = Clone();
        PersonRecord? existing = personId is null ? null : next.People.FirstOrDefault(person => person.Id == personId);
        if (personId is not null && existing is null) throw new ArgumentException("That saved speaker no longer exists.", nameof(personId));
        if (next.People.Any(person => person.Id != personId && string.Equals(person.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("That name is already saved. Choose that person or use another name.", nameof(name));
        if (existing is null)
        {
            if (next.People.Count >= MaxPeople) throw new InvalidOperationException("The saved people limit (64) has been reached.");
            existing = new() { Id = Guid.NewGuid().ToString("N"), Name = name };
            next.People.Add(existing);
        }
        else existing.Name = name;
        Commit(next);
        return ToPerson(existing, next);
    }

    internal void DeletePerson(string personId)
    {
        SpeakerPerson old = RequirePerson(personId);
        FeedbackDatabase next = Clone();
        next.People.RemoveAll(person => person.Id == personId);
        next.Anchors.RemoveAll(anchor => anchor.PersonId == personId);
        next.NegativeAnchors.RemoveAll(anchor => anchor.PersonId == personId);
        next.Reviews.RemoveAll(review => review.SuggestedPersonId == personId);
        foreach (TranslationRecord record in next.RecentTranslations.Where(record => record.PersonId == personId)) record.PersonId = null;
        Commit(next);
        DeleteUnusedPhoto(old.AvatarFileName, next);
    }

    internal SpeakerPerson SetPersonPhoto(string personId, string? sourcePath)
    {
        SpeakerPerson old = RequirePerson(personId);
        string dataRoot = Path.GetDirectoryName(_path)!;
        string? imported = sourcePath is null ? null : PersonAvatar.Import(sourcePath, dataRoot);
        FeedbackDatabase next = Clone();
        PersonRecord person = next.People.Single(person => person.Id == personId);
        person.AvatarFileName = imported;
        try { Commit(next); }
        catch { PersonAvatar.DeleteManaged(imported, dataRoot); throw; }
        DeleteUnusedPhoto(old.AvatarFileName, next);
        return ToPerson(person, next);
    }

    // Opaque session/utterance keys are retained for bounded deduplication and live corrections.
    // No transcript is stored. Counters survive restart; recent relabelling moves, not duplicates,
    // a count. Old totals cannot be attributed retroactively without inventing unavailable data.
    internal void RecordTranslation(string translationId, string? personId, DateTimeOffset at)
    {
        if (!IsTranslationKey(translationId)) throw new ArgumentException("A translation needs an opaque session and utterance identity.", nameof(translationId));
        if (personId is not null) RequirePerson(personId);
        FeedbackDatabase data = ReadData();
        TranslationRecord? old = data.RecentTranslations.FirstOrDefault(record => record.Id == translationId);
        // Most subtitles initially have no saved identity. The live service retains
        // their opaque ID for later manual assignment; do not rewrite the voice database
        // for a new unnamed sentence just to persist a zero-person ledger entry.
        if (old is null && personId is null) return;
        if (old is not null && old.PersonId == personId) return;
        FeedbackDatabase next = Clone(data);
        TranslationRecord? record = next.RecentTranslations.FirstOrDefault(record => record.Id == translationId);
        if (record is null)
        {
            record = new() { Id = translationId, AtUtc = at.ToUniversalTime() };
            next.RecentTranslations.Add(record);
        }
        else if (record.PersonId is string previousId && next.People.FirstOrDefault(person => person.Id == previousId) is PersonRecord previous)
            previous.TranslationCount = Math.Max(0, previous.TranslationCount - 1);
        record.PersonId = personId;
        if (personId is not null)
        {
            PersonRecord person = next.People.Single(person => person.Id == personId);
            person.TranslationCount = checked(person.TranslationCount + 1);
            if (record.AtUtc > person.LastTranslatedAtUtc) person.LastTranslatedAtUtc = record.AtUtc;
        }
        while (next.RecentTranslations.Count > MaxRecentTranslations) next.RecentTranslations.RemoveAt(0);
        Commit(next);
    }

    private void DeleteUnusedPhoto(string? name, FeedbackDatabase data)
    {
        if (!data.People.Any(person => person.AvatarFileName == name))
            PersonAvatar.DeleteManaged(name, Path.GetDirectoryName(_path)!);
    }

    private static bool IsTranslationKey(string value) => value.Length is >= 34 and <= 53 &&
        value[32] == ':' && value[..32].All(Uri.IsHexDigit) && long.TryParse(value[33..], out long number) && number > 0;

    internal void Acknowledge()
    {
        if (State().InstructionAcknowledged) return;
        FeedbackDatabase next = Clone();
        next.InstructionAcknowledged = true;
        Commit(next);
    }

    internal IReadOnlyList<SpeakerFeedbackAnchor> Anchors(string modelSha)
    {
        FeedbackDatabase data = ReadData();
        string key = NormalizeModelSha(modelSha);
        return data.Anchors.Where(anchor => anchor.ModelSha == key).Select(anchor => anchor.Copy()).ToArray();
    }

    internal SpeakerFeedbackAnchor? Contribution(string evidenceId)
    {
        FeedbackDatabase data = ReadData();
        return data.Anchors.FirstOrDefault(anchor => anchor.EvidenceId == evidenceId || anchor.EvidenceAliases.Contains(evidenceId, StringComparer.Ordinal))?.Copy();
    }

    internal IReadOnlyList<SpeakerFeedbackAnchor> NegativeAnchors(string modelSha)
    {
        FeedbackDatabase data = ReadData();
        string key = NormalizeModelSha(modelSha);
        return data.NegativeAnchors.Where(anchor => anchor.ModelSha == key).Select(anchor => anchor.Copy()).ToArray();
    }

    internal SpeakerFeedbackContribution FeedbackContribution(string evidenceId)
    {
        FeedbackDatabase data = ReadData();
        static bool Owns(string id, string main, string[] aliases) => main == id || aliases.Contains(id, StringComparer.Ordinal);
        return new(data.Anchors.FirstOrDefault(anchor => Owns(evidenceId, anchor.EvidenceId, anchor.EvidenceAliases))?.Copy(),
            data.NegativeAnchors.FirstOrDefault(anchor => Owns(evidenceId, anchor.EvidenceId, anchor.EvidenceAliases))?.Copy(),
            data.Reviews.FirstOrDefault(review => Owns(evidenceId, review.EvidenceId, review.EvidenceAliases))?.Copy());
    }

    internal bool CanStoreNegativeContribution(string evidenceId, string personId, string modelSha)
    {
        FeedbackDatabase data = ReadData();
        string key = NormalizeModelSha(modelSha);
        return data.NegativeAnchors.Count(anchor => anchor.EvidenceId != evidenceId) < MaxNegativeAnchors &&
            data.NegativeAnchors.Count(anchor => anchor.EvidenceId != evidenceId && anchor.PersonId == personId && anchor.ModelSha == key) < MaxNegativeAnchorsPerPersonAndModel;
    }

    internal bool CanStoreContribution(string evidenceId, string personId, string modelSha)
    {
        FeedbackDatabase data = ReadData();
        string key = NormalizeModelSha(modelSha);
        return data.Anchors.Count(anchor => anchor.EvidenceId != evidenceId) < MaxAnchors &&
            data.Anchors.Count(anchor => anchor.EvidenceId != evidenceId && anchor.PersonId == personId && anchor.ModelSha == key) < MaxAnchorsPerPersonAndModel;
    }

    internal void ReplaceContribution(string evidenceId, SpeakerFeedbackAnchor? replacement, string? usedPersonId = null, DateTimeOffset? usedAt = null)
        => ReplaceFeedback(evidenceId, new(replacement, null, null), usedPersonId, usedAt);

    // Positive/negative examples and the reviewed outcome are committed together. A relabel or
    // undo replaces only this evidence contribution, never unrelated references or historical totals.
    internal void ReplaceFeedback(string evidenceId, SpeakerFeedbackContribution replacement, string? usedPersonId = null, DateTimeOffset? usedAt = null)
    {
        FeedbackDatabase data = ReadData();
        FeedbackDatabase next = Clone(data);
        if (usedPersonId is not null)
        {
            PersonRecord? used = next.People.FirstOrDefault(person => person.Id == usedPersonId);
            if (used is null) throw new ArgumentException("That saved speaker no longer exists. Choose a current person.", nameof(usedPersonId));
            DateTimeOffset timestamp = (usedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
            // A backwards system-clock adjustment must not demote a previously recent person.
            if (timestamp > used.LastUsedAtUtc) used.LastUsedAtUtc = timestamp;
        }
        else if (usedAt is not null)
            throw new ArgumentException("A manual-use timestamp needs a saved speaker identity.", nameof(usedAt));
        next.Anchors.RemoveAll(anchor => anchor.EvidenceId == evidenceId);
        next.NegativeAnchors.RemoveAll(anchor => anchor.EvidenceId == evidenceId);
        SpeakerSuggestionReview? oldReview = next.Reviews.FirstOrDefault(review => review.EvidenceId == evidenceId);
        next.Reviews.RemoveAll(review => review.EvidenceId == evidenceId);
        if (oldReview is not null) AdjustReviewCounter(next, oldReview, -1);
        if (replacement.Positive is not null)
        {
            SpeakerFeedbackAnchor positive = replacement.Positive;
            RequirePerson(positive.PersonId);
            if (positive.EvidenceId != evidenceId) throw new ArgumentException("A corrected sample has mismatched evidence identity.");
            positive = positive.Copy() with { ModelSha = NormalizeModelSha(positive.ModelSha) };
            if (next.Anchors.Count >= MaxAnchors || next.Anchors.Count(anchor => anchor.PersonId == positive.PersonId && anchor.ModelSha == positive.ModelSha) >= MaxAnchorsPerPersonAndModel)
                throw new InvalidOperationException("This person's confirmed sample limit has been reached. The label was not changed; remove unused saved people before adding more samples.");
            next.Anchors.Add(positive);
        }
        if (replacement.Negative is not null)
        {
            SpeakerFeedbackAnchor negative = replacement.Negative;
            RequirePerson(negative.PersonId);
            if (negative.EvidenceId != evidenceId) throw new ArgumentException("A rejected sample has mismatched evidence identity.");
            negative = negative.Copy() with { ModelSha = NormalizeModelSha(negative.ModelSha) };
            if (next.NegativeAnchors.Count >= MaxNegativeAnchors || next.NegativeAnchors.Count(anchor => anchor.PersonId == negative.PersonId && anchor.ModelSha == negative.ModelSha) >= MaxNegativeAnchorsPerPersonAndModel)
                throw new InvalidOperationException("The bounded rejected voice collection is full.");
            next.NegativeAnchors.Add(negative);
        }
        if (replacement.Review is not null)
        {
            SpeakerSuggestionReview review = replacement.Review.Copy();
            RequirePerson(review.SuggestedPersonId);
            if (review.EvidenceId != evidenceId) throw new ArgumentException("A suggestion review has mismatched evidence identity.");
            next.Reviews.Add(review);
            AdjustReviewCounter(next, review, 1);
            // Old totals stay intact when their bounded deduplication record ages out.
            while (next.Reviews.Count > MaxRecentReviews) next.Reviews.RemoveAt(0);
        }
        // Known-person label-only corrections still persist their recency. Special labels and
        // undo never manufacture a manual use; without an anchor change they need no disk write.
        if (usedPersonId is null && replacement.Positive is null && replacement.Negative is null && replacement.Review is null &&
            next.Anchors.Count == data.Anchors.Count && next.NegativeAnchors.Count == data.NegativeAnchors.Count && oldReview is null) return;
        Commit(next);
    }

    private static void AdjustReviewCounter(FeedbackDatabase data, SpeakerSuggestionReview review, int delta)
    {
        PersonRecord person = data.People.Single(person => person.Id == review.SuggestedPersonId);
        if (review.Confirmed) person.ConfirmedSuggestions = checked(person.ConfirmedSuggestions + delta);
        else person.RejectedSuggestions = checked(person.RejectedSuggestions + delta);
    }

    internal static string NormalizeModelSha(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Voice samples need the exact 64-character model SHA-256 fingerprint.", nameof(value));
        return value.ToUpperInvariant();
    }

    private FeedbackDatabase Clone(FeedbackDatabase? source = null)
    {
        FeedbackDatabase data = source ?? ReadData();
        return new()
        {
            InstructionAcknowledged = data.InstructionAcknowledged,
            People = data.People.Select(person => new PersonRecord { Id = person.Id, Name = person.Name, LastUsedAtUtc = person.LastUsedAtUtc,
                AvatarFileName = person.AvatarFileName, VoiceAvatarSeed = person.VoiceAvatarSeed,
                TranslationCount = person.TranslationCount, LastTranslatedAtUtc = person.LastTranslatedAtUtc,
                ConfirmedSuggestions = person.ConfirmedSuggestions, RejectedSuggestions = person.RejectedSuggestions }).ToList(),
            Anchors = data.Anchors.Select(anchor => anchor.Copy()).ToList(),
            NegativeAnchors = data.NegativeAnchors.Select(anchor => anchor.Copy()).ToList(),
            Reviews = data.Reviews.Select(review => review.Copy()).ToList(),
            RecentTranslations = data.RecentTranslations.Select(record => new TranslationRecord { Id = record.Id, PersonId = record.PersonId, AtUtc = record.AtUtc }).ToList()
        };
    }

    private void Commit(FeedbackDatabase next)
    {
        Validate(next);
        string? directory = Path.GetDirectoryName(_path);
        if (directory is not null) Directory.CreateDirectory(directory);
        // Retain V2 anonymous identities exactly as they are, with a recoverable migration checkpoint.
        if (_legacyProfilesPath is not null && File.Exists(_legacyProfilesPath))
        {
            string backup = _legacyProfilesPath + ".before-named-feedback-v1.backup";
            if (!File.Exists(backup)) File.Copy(_legacyProfilesPath, backup, false);
        }
        string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
            // Only publish a fully persisted, never-again-mutated database to fast readers.
            Volatile.Write(ref _data, next);
        }
        catch (Exception exception)
        {
            throw new IOException("Saved speaker changes could not be written. No learning or label change was applied. " + exception.Message, exception);
        }
        finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch { } }
    }

    private void CheckReadable()
    {
        if (_loadFailure is not null)
            throw new InvalidDataException("The saved speaker file could not be read; it has been preserved. Restore a valid backup before making changes. " + _loadFailure.Message, _loadFailure);
    }

    private FeedbackDatabase ReadData()
    {
        CheckReadable();
        return Volatile.Read(ref _data);
    }

    private static SpeakerPerson ToPerson(PersonRecord person, FeedbackDatabase data) => new(person.Id, person.Name, data.Anchors.Count(anchor => anchor.PersonId == person.Id))
    {
        LastUsedAtUtc = person.LastUsedAtUtc, AvatarFileName = person.AvatarFileName, VoiceAvatarSeed = person.VoiceAvatarSeed,
        TranslationCount = person.TranslationCount, LastTranslatedAtUtc = person.LastTranslatedAtUtc,
        ConfirmedSuggestions = person.ConfirmedSuggestions, RejectedSuggestions = person.RejectedSuggestions
    };

    private static void Validate(FeedbackDatabase data)
    {
        if (data.Version != 1 || data.People is null || data.Anchors is null || data.NegativeAnchors is null || data.Reviews is null || data.RecentTranslations is null || data.People.Count > MaxPeople || data.Anchors.Count > MaxAnchors || data.NegativeAnchors.Count > MaxNegativeAnchors || data.Reviews.Count > MaxRecentReviews || data.RecentTranslations.Count > MaxRecentTranslations)
            throw new InvalidDataException("Unsupported or oversized saved speaker file.");
        if (data.People.Any(person => person is null || string.IsNullOrWhiteSpace(person.Id) || string.IsNullOrWhiteSpace(person.Name) || person.Name.Length > 64 || person.Name.Any(char.IsControl)) || data.People.Select(person => person.Id).Distinct().Count() != data.People.Count || data.People.Select(person => person.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.People.Count)
            throw new InvalidDataException("Saved speaker names contain invalid or duplicate identities.");
        foreach (PersonRecord person in data.People)
        {
            // Bad/missing pictures must never hide the person's saved name or examples.
            if (!PersonAvatar.IsManagedFileName(person.AvatarFileName)) person.AvatarFileName = null;
            // Damaged decoration must not hide a saved person's name or examples.
            person.VoiceAvatarSeed = PersonAvatarSeed.Canonical(person.VoiceAvatarSeed);
            if (person.TranslationCount < 0 || person.ConfirmedSuggestions < 0 || person.RejectedSuggestions < 0) throw new InvalidDataException("A saved person's translation or review count is invalid.");
        }
        if (data.RecentTranslations.Any(record => record is null || !IsTranslationKey(record.Id) ||
                record.PersonId is not null && !data.People.Any(person => person.Id == record.PersonId)) ||
            data.RecentTranslations.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != data.RecentTranslations.Count)
            throw new InvalidDataException("Saved translation counts contain invalid identities.");
        var dimensions = new Dictionary<string, int>();
        var evidenceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SpeakerFeedbackAnchor anchor in data.Anchors.Concat(data.NegativeAnchors))
        {
            if (anchor is null || string.IsNullOrWhiteSpace(anchor.EvidenceId) || string.IsNullOrWhiteSpace(anchor.RevisionToken) || !data.People.Any(person => person.Id == anchor.PersonId) || anchor.Embedding is null || anchor.Embedding.Length is < 1 or > 4096 || anchor.Embedding.Any(value => !float.IsFinite(value)) || Math.Abs(anchor.Embedding.Sum(value => (double)value * value) - 1) > 0.01 || !double.IsFinite(anchor.SpeechSeconds) || anchor.SpeechSeconds < 1.35 || !double.IsFinite(anchor.ClippedFraction) || anchor.ClippedFraction < 0 || anchor.ClippedFraction >= 0.015)
                throw new InvalidDataException("A saved voice sample has invalid quality or provenance.");
            if (NormalizeModelSha(anchor.ModelSha) != anchor.ModelSha) throw new InvalidDataException("Saved model fingerprints must be canonical SHA-256 values.");
            if (dimensions.TryGetValue(anchor.ModelSha, out int length) && length != anchor.Embedding.Length)
                throw new InvalidDataException("Voice fingerprints for one model have inconsistent dimensions.");
            dimensions[anchor.ModelSha] = anchor.Embedding.Length;
            if (anchor.EvidenceAliases is null || anchor.EvidenceAliases.Length > 32 || anchor.EvidenceAliases.Any(string.IsNullOrWhiteSpace) || anchor.EvidenceAliases.Distinct(StringComparer.Ordinal).Count() != anchor.EvidenceAliases.Length)
                throw new InvalidDataException("A saved voice sample has invalid revision aliases.");
            foreach (string id in anchor.EvidenceAliases.Append(anchor.EvidenceId).Distinct(StringComparer.Ordinal))
            {
                if (evidenceOwners.TryGetValue(id, out string? owner) && owner != anchor.EvidenceId)
                    throw new InvalidDataException("One revised voice observation was assigned to multiple samples.");
                evidenceOwners[id] = anchor.EvidenceId;
            }
        }
        if (data.Anchors.Select(anchor => anchor.EvidenceId).Distinct().Count() != data.Anchors.Count || data.Anchors.GroupBy(anchor => (anchor.PersonId, anchor.ModelSha)).Any(group => group.Count() > MaxAnchorsPerPersonAndModel))
            throw new InvalidDataException("Duplicate or oversized confirmed voice samples.");
        if (data.NegativeAnchors.Select(anchor => anchor.EvidenceId).Distinct().Count() != data.NegativeAnchors.Count || data.NegativeAnchors.GroupBy(anchor => (anchor.PersonId, anchor.ModelSha)).Any(group => group.Count() > MaxNegativeAnchorsPerPersonAndModel))
            throw new InvalidDataException("Duplicate or oversized rejected voice samples.");
        foreach (SpeakerSuggestionReview review in data.Reviews)
        {
            if (review is null || string.IsNullOrWhiteSpace(review.EvidenceId) || string.IsNullOrWhiteSpace(review.RevisionToken) ||
                !data.People.Any(person => person.Id == review.SuggestedPersonId) || review.EvidenceAliases is null ||
                review.EvidenceAliases.Length > 32 || review.EvidenceAliases.Any(string.IsNullOrWhiteSpace) ||
                review.EvidenceAliases.Distinct(StringComparer.Ordinal).Count() != review.EvidenceAliases.Length)
                throw new InvalidDataException("A saved suggestion review has invalid provenance.");
            foreach (string id in review.EvidenceAliases.Append(review.EvidenceId).Distinct(StringComparer.Ordinal))
            {
                if (evidenceOwners.TryGetValue(id, out string? owner) && owner != review.EvidenceId)
                    throw new InvalidDataException("One revised voice observation has multiple suggestion reviews.");
                evidenceOwners[id] = review.EvidenceId;
            }
            SpeakerFeedbackAnchor? positive = data.Anchors.FirstOrDefault(anchor => anchor.EvidenceId == review.EvidenceId);
            SpeakerFeedbackAnchor? negative = data.NegativeAnchors.FirstOrDefault(anchor => anchor.EvidenceId == review.EvidenceId);
            if (positive is not null && (positive.RevisionToken != review.RevisionToken ||
                    review.Confirmed != (positive.PersonId == review.SuggestedPersonId)) ||
                negative is not null && (negative.RevisionToken != review.RevisionToken || review.Confirmed || negative.PersonId != review.SuggestedPersonId))
                throw new InvalidDataException("A reviewed outcome disagrees with its voice examples.");
        }
        if (data.Reviews.Select(review => review.EvidenceId).Distinct(StringComparer.Ordinal).Count() != data.Reviews.Count ||
            data.People.Any(person => person.ConfirmedSuggestions < data.Reviews.LongCount(review => review.SuggestedPersonId == person.Id && review.Confirmed) ||
                person.RejectedSuggestions < data.Reviews.LongCount(review => review.SuggestedPersonId == person.Id && !review.Confirmed)))
            throw new InvalidDataException("Saved suggestion review counts are inconsistent.");
        foreach (PersonRecord person in data.People.Where(person => person.VoiceAvatarSeed is null))
        {
            // Legacy records derive from the earliest retained confirmed sample.
            // Reads only update the private in-memory snapshot; the seed becomes
            // durable on the next ordinary successful metadata/feedback commit.
            SpeakerFeedbackAnchor? first = data.Anchors.Where(anchor => anchor.PersonId == person.Id)
                .OrderBy(anchor => anchor.ObservedAtUtc).ThenBy(anchor => anchor.EvidenceId, StringComparer.Ordinal).FirstOrDefault();
            if (first is not null)
                person.VoiceAvatarSeed = PersonAvatarSeed.FromVoice(person.Id, first.ModelSha, first.Embedding);
        }
    }

    private sealed class FeedbackDatabase
    {
        public int Version { get; set; } = 1;
        public bool InstructionAcknowledged { get; set; }
        public List<PersonRecord> People { get; set; } = new();
        public List<SpeakerFeedbackAnchor> Anchors { get; set; } = new();
        public List<SpeakerFeedbackAnchor> NegativeAnchors { get; set; } = new();
        public List<SpeakerSuggestionReview> Reviews { get; set; } = new();
        public List<TranslationRecord> RecentTranslations { get; set; } = new();
    }
    private sealed class PersonRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset LastUsedAtUtc { get; set; }
        public string? AvatarFileName { get; set; }
        public string? VoiceAvatarSeed { get; set; }
        public long TranslationCount { get; set; }
        public DateTimeOffset LastTranslatedAtUtc { get; set; }
        public long ConfirmedSuggestions { get; set; }
        public long RejectedSuggestions { get; set; }
    }
    private sealed class TranslationRecord
    {
        public string Id { get; set; } = "";
        public string? PersonId { get; set; }
        public DateTimeOffset AtUtc { get; set; }
    }
}

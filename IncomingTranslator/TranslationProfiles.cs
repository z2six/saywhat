using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

/// <summary>A user-owned settings snapshot; voice examples and model files are not copied into it.</summary>
internal sealed class TranslationProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonObject Snapshot { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    internal TranslationProfile Copy() => new()
    {
        Id = Id, Name = Name, Snapshot = (JsonObject)(Snapshot ?? new()).DeepClone(),
        CreatedUtc = CreatedUtc, ModifiedUtc = ModifiedUtc
    };
}

internal sealed record TranslationProfileChoice(string Id, string Name, string Description, bool IsBuiltIn)
{
    public override string ToString() => Name;
}

/// <summary>
/// Built-ins are operational templates, not replacements for a user's models,
/// languages or appearance. Custom profiles capture every editable AppSettings
/// property except profile bookkeeping and one-time onboarding acknowledgements.
/// </summary>
internal static class TranslationProfiles
{
    internal const string QuickId = "quick";
    internal const string NormalId = "normal";
    internal const string GroupsId = "groups";
    internal const string UnsavedId = "custom";
    internal const string DefaultVibeVoiceEndpoint = "http://127.0.0.1:8082";
    internal const string DefaultVibeVoiceModel = "microsoft/VibeVoice-ASR-Streaming-1.5B";
    private const int MaximumCustomProfiles = 64;
    private static readonly PropertyInfo[] SettingsProperties = typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.CanWrite && property.GetCustomAttribute<JsonIgnoreAttribute>() is null).ToArray();
    private static readonly HashSet<string> SnapshotExcludedProperties = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.ActiveTranslationProfileId), nameof(AppSettings.CustomTranslationProfiles),
        nameof(AppSettings.FirstRunSetupCompleted), nameof(AppSettings.FirstRunIntroductionCompleted),
        nameof(AppSettings.SubtitleGuidePromptSeen)
    };
    private static readonly HashSet<string> SnapshotProperties = SettingsProperties.Select(property => property.Name)
        .Where(name => !SnapshotExcludedProperties.Contains(name)).ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyList<TranslationProfileChoice> List(AppSettings settings)
    {
        var choices = new List<TranslationProfileChoice>
        {
            new(QuickId, "Quick", "For one-to-one conversations. Earlier subtitles; automatic person recognition is off.", true),
            new(NormalId, "Normal", "Recognize familiar people while translating their speech.", true),
            new(GroupsId, "Groups", "Recognize separate voices with VibeVoice. Adds delay and uses more memory.", true)
        };
        choices.AddRange((settings.CustomTranslationProfiles ?? new()).Where(profile => profile is not null)
            .Select(profile => new TranslationProfileChoice(profile.Id, profile.Name, "Your saved settings.", false)));
        if (settings.ActiveTranslationProfileId == UnsavedId)
            choices.Add(new(UnsavedId, "Custom settings", "These settings have not been saved as a profile.", false));
        return choices;
    }

    internal static bool IsBuiltIn(string? id) => id is QuickId or NormalId or GroupsId;

    internal static string DisplayName(AppSettings settings) => List(settings)
        .FirstOrDefault(choice => choice.Id == settings.ActiveTranslationProfileId)?.Name ?? "Custom settings";

    internal static AppSettings Select(AppSettings source, string id)
    {
        AppSettings result = source.Copy();
        SettingsStore.Normalize(result);
        if (id == UnsavedId)
        {
            result.ActiveTranslationProfileId = UnsavedId;
            return result;
        }
        if (IsBuiltIn(id)) ApplyBuiltIn(result, id);
        else
        {
            TranslationProfile profile = FindCustom(result, id);
            JsonObject merged = JsonSerializer.SerializeToNode(result)!.AsObject();
            foreach (var pair in profile.Snapshot)
                if (SnapshotProperties.Contains(pair.Key)) merged[pair.Key] = pair.Value?.DeepClone();
            result = JsonSerializer.Deserialize<AppSettings>(merged) ?? throw new InvalidDataException("The saved profile could not be read.");
        }
        result.ActiveTranslationProfileId = id;
        return SettingsStore.Normalize(result);
    }

    internal static AppSettings Create(AppSettings source, string name)
    {
        AppSettings result = source.Copy();
        SettingsStore.Normalize(result);
        if (result.CustomTranslationProfiles.Count >= MaximumCustomProfiles)
            throw new InvalidOperationException($"You can save up to {MaximumCustomProfiles} profiles. Remove one before adding another.");
        name = ValidateName(result, name, null);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var profile = new TranslationProfile
        {
            Id = "custom-" + Guid.NewGuid().ToString("N"), Name = name, Snapshot = Capture(result),
            CreatedUtc = now, ModifiedUtc = now
        };
        result.CustomTranslationProfiles.Add(profile);
        result.ActiveTranslationProfileId = profile.Id;
        return result;
    }

    internal static AppSettings Update(AppSettings source, string id)
    {
        AppSettings result = source.Copy();
        SettingsStore.Normalize(result);
        TranslationProfile profile = FindCustom(result, id);
        profile.Snapshot = Capture(result);
        profile.ModifiedUtc = DateTimeOffset.UtcNow;
        result.ActiveTranslationProfileId = id;
        return result;
    }

    internal static AppSettings Rename(AppSettings source, string id, string name)
    {
        AppSettings result = source.Copy();
        SettingsStore.Normalize(result);
        TranslationProfile profile = FindCustom(result, id);
        profile.Name = ValidateName(result, name, id);
        profile.ModifiedUtc = DateTimeOffset.UtcNow;
        return result;
    }

    internal static AppSettings Delete(AppSettings source, string id)
    {
        AppSettings result = source.Copy();
        SettingsStore.Normalize(result);
        TranslationProfile profile = FindCustom(result, id);
        result.CustomTranslationProfiles.Remove(profile);
        // Removing a preset must not unexpectedly change the running configuration.
        if (result.ActiveTranslationProfileId == id) result.ActiveTranslationProfileId = UnsavedId;
        return result;
    }

    internal static bool IsModified(AppSettings settings)
    {
        string id = settings.ActiveTranslationProfileId;
        if (id == UnsavedId) return false;
        if (IsBuiltIn(id))
        {
            AppSettings template = settings.Copy();
            ApplyBuiltIn(template, id);
            return !SameConfiguration(template, settings);
        }
        TranslationProfile? profile = (settings.CustomTranslationProfiles ?? new()).FirstOrDefault(item => item?.Id == id);
        return profile is null || !JsonNode.DeepEquals(ComparisonCapture(settings), ComparisonCapture(profile.Snapshot));
    }

    internal static bool SameConfiguration(AppSettings left, AppSettings right) => JsonNode.DeepEquals(ComparisonCapture(left), ComparisonCapture(right));

    internal static bool ConfigurationEquals(AppSettings left, AppSettings right) => SameConfiguration(left, right);

    private static JsonObject ComparisonCapture(AppSettings settings) => ComparisonCapture(Capture(settings));

    private static JsonObject ComparisonCapture(JsonObject snapshot)
    {
        JsonObject comparison = (JsonObject)snapshot.DeepClone();
        // Legacy settings have an implicit VRChat/desktop choice. Treat it as
        // equal to its canonical saved mode without losing real source edits.
        AppSettings listening = JsonSerializer.Deserialize<AppSettings>(snapshot) ?? new();
        IncomingListening.Normalize(listening);
        comparison[nameof(AppSettings.IncomingAudioMode)] = listening.IncomingAudioMode;
        comparison[nameof(AppSettings.CaptureVrChatOnly)] = listening.CaptureVrChatOnly;
        comparison[nameof(AppSettings.IncomingApplicationPath)] = listening.IncomingApplicationPath;
        comparison[nameof(AppSettings.IncomingApplicationName)] = listening.IncomingApplicationName;
        comparison[nameof(AppSettings.IncomingSinglePerson)] = listening.IncomingSinglePerson;
        comparison[nameof(AppSettings.IncomingPersonId)] = listening.IncomingPersonId;
        // Managed startup assigns ports and request aliases. Those are derived
        // runtime values, not edits to the profile's selected model/settings.
        if (comparison[nameof(AppSettings.UseManagedTranslator)] is JsonValue managed && managed.TryGetValue<bool>(out bool enabled) && enabled)
            foreach (string derived in new[] { nameof(AppSettings.OwnTranslatorModel), nameof(AppSettings.TranslatorModel),
                nameof(AppSettings.OwnTranslatorBaseUrl), nameof(AppSettings.TranslatorBaseUrl) }) comparison.Remove(derived);
        return comparison;
    }

    internal static void CopyInto(AppSettings source, AppSettings target)
    {
        AppSettings copy = source.Copy();
        foreach (PropertyInfo property in SettingsProperties) property.SetValue(target, property.GetValue(copy));
    }

    internal static JsonObject Capture(AppSettings settings)
    {
        // Select before serializing: no recursive profile library is ever traversed,
        // and adding a new editable AppSettings property automatically includes it.
        JsonObject snapshot = new();
        foreach (PropertyInfo property in SettingsProperties)
            if (SnapshotProperties.Contains(property.Name))
                snapshot[property.Name] = JsonSerializer.SerializeToNode(property.GetValue(settings), property.PropertyType);
        return snapshot;
    }

    internal static void MigrateExistingSettings(AppSettings settings)
    {
        // An upgrade does not know whether an old value was intentional. Capture
        // the actual configuration instead of silently applying today's template.
        if (settings.CustomTranslationProfiles.Count >= MaximumCustomProfiles)
        {
            settings.ActiveTranslationProfileId = UnsavedId;
            return;
        }
        const string migrationId = "custom-current";
        TranslationProfile? profile = settings.CustomTranslationProfiles.FirstOrDefault(item => item.Id == migrationId);
        if (profile is null)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            profile = new TranslationProfile
            {
                Id = migrationId, Name = UniqueName(settings, "Current settings"), Snapshot = Capture(settings),
                CreatedUtc = now, ModifiedUtc = now
            };
            settings.CustomTranslationProfiles.Add(profile);
        }
        settings.ActiveTranslationProfileId = profile.Id;
    }

    internal static void Normalize(AppSettings settings)
    {
        var valid = new List<TranslationProfile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TranslationProfile? profile in settings.CustomTranslationProfiles ?? new())
        {
            if (profile is null || valid.Count >= MaximumCustomProfiles || string.IsNullOrWhiteSpace(profile.Id) ||
                profile.Id.Length > 80 || IsBuiltIn(profile.Id) || profile.Id == UnsavedId || !ids.Add(profile.Id)) continue;
            string name = NormalizeName(profile.Name);
            if (name.Length == 0 || name.Length > 60 || !names.Add(name)) continue;
            try
            {
                JsonObject safe = new();
                foreach (var pair in profile.Snapshot ?? new())
                    if (SnapshotProperties.Contains(pair.Key)) safe[pair.Key] = pair.Value?.DeepClone();
                AppSettings restored = JsonSerializer.Deserialize<AppSettings>(safe) ?? new();
                // Snapshot metadata was stripped, so this recursive normalizer has
                // no nested custom library and cannot recurse through stored profiles.
                SettingsStore.Normalize(restored);
                profile.Name = name;
                profile.Snapshot = Capture(restored);
                valid.Add(profile);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { /* One malformed profile cannot make the app's live settings unreadable. */ }
        }
        settings.CustomTranslationProfiles = valid;
        if (!IsBuiltIn(settings.ActiveTranslationProfileId) && settings.ActiveTranslationProfileId != UnsavedId &&
            !valid.Any(profile => profile.Id == settings.ActiveTranslationProfileId))
            settings.ActiveTranslationProfileId = UnsavedId;
    }

    internal static string CanonicalSpeechEngine(string? value) => IncomingRecognitionCatalog.CanonicalEngine(value);

    internal static string CanonicalVibeVoiceModel(string? value) =>
        string.Equals(value?.Trim(), "microsoft/VibeVoice-ASR-Streaming-7B", StringComparison.OrdinalIgnoreCase)
            ? "microsoft/VibeVoice-ASR-Streaming-7B" : DefaultVibeVoiceModel;

    internal static string NormalizeVibeVoiceEndpoint(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != "http" || !uri.IsLoopback || uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return DefaultVibeVoiceEndpoint;
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static void ApplyBuiltIn(AppSettings settings, string id)
    {
        if (id == GroupsId)
        {
            // Keep the user's fingerprint switch and speed settings. Groups
            // changes only the recognizer; it adds its own buffering internally.
            if (settings.IncomingSpeechEngine != "vibevoice")
            {
                settings.IncomingNormalSpeechEngine = settings.IncomingSpeechEngine;
                settings.IncomingNormalSourceLanguage = settings.IncomingSourceLanguage;
            }
            settings.IncomingSpeechEngine = "vibevoice";
            settings.IncomingSourceLanguage = "Automatic";
            settings.DetectAnySourceLanguage = true;
            return;
        }
        if (settings.IncomingSpeechEngine == "vibevoice")
        {
            settings.IncomingSpeechEngine = settings.IncomingNormalSpeechEngine;
            settings.IncomingSourceLanguage = settings.IncomingNormalSourceLanguage;
            settings.DetectAnySourceLanguage = settings.IncomingSourceLanguage == "Automatic";
            if (!settings.DetectAnySourceLanguage) settings.SourceLanguages = [settings.IncomingSourceLanguage];
        }
        settings.IncomingSettleMilliseconds = id == QuickId ? 800 : 1200;
        settings.SubtitlePreviewStableMilliseconds = id == QuickId ? 250 : 500;
        settings.RecognitionDelayMilliseconds = id == QuickId ? 160 : 240;
        settings.UseAiSpeakerRecognition = id != QuickId;
        settings.OwnVoiceEndPauseMilliseconds = AppSettings.DefaultSentencePauseMilliseconds;
        if (id == QuickId) settings.TranslationQuality = "Fast";
    }

    private static TranslationProfile FindCustom(AppSettings settings, string id)
    {
        if (IsBuiltIn(id)) throw new InvalidOperationException("Built-in profiles cannot be changed. Save a custom profile instead.");
        return settings.CustomTranslationProfiles.FirstOrDefault(profile => profile.Id == id)
            ?? throw new KeyNotFoundException("That saved profile no longer exists.");
    }

    private static string ValidateName(AppSettings settings, string? name, string? ownId)
    {
        name = NormalizeName(name);
        if (name.Length == 0 || name.Length > 60) throw new ArgumentException("Enter a profile name between 1 and 60 characters.", nameof(name));
        if (List(settings).Any(profile => profile.Id != ownId && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A profile with that name already exists. Choose a different name.", nameof(name));
        return name;
    }

    private static string UniqueName(AppSettings settings, string name)
    {
        if (!List(settings).Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        for (int number = 2; ; number++)
        {
            string candidate = $"{name} {number}";
            if (!List(settings).Any(profile => string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private static string NormalizeName(string? name) => Regex.Replace(name?.Trim() ?? "", @"\s+", " ");
}

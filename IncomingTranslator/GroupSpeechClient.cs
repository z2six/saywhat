using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

internal sealed record GroupVoiceText(string VoiceId, string Text);
internal sealed record GroupTranscriptChunk(string SessionId, int Epoch, long ChunkIndex,
    double AudioStartMilliseconds, double AudioEndMilliseconds, IReadOnlyList<GroupVoiceText> Segments)
{
    // Audio bounds cover the original whole chunk. Removing a sound label must
    // never make its remaining speech look like an isolated voice example.
    public int NonSpeechAnnotationCount { get; init; }
    public bool HasNonSpeechAnnotations => NonSpeechAnnotationCount > 0;
}

// Protocol audio spans refer to whole input chunks, not individual voices or words.
internal static class GroupSpeechProtocol
{
    internal const string UnidentifiedVoiceId = "unknown";
    private static readonly Regex Annotation = new(@"\[(?<label>[^\[\]\r\n]{1,80})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SoundLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "music", "background music", "instrumental music", "laughter", "laughing", "laughs",
        "chuckling", "chuckles", "applause", "clapping", "cheering", "noise", "background noise",
        "silence", "singing", "cough", "coughing", "coughs", "sigh", "sighing", "sighs",
        "breathing", "gasping", "gasps", "crying", "inaudible", "unintelligible", "crosstalk",
        "overlapping speech", "音乐", "音樂", "笑声", "笑聲", "音楽", "笑い", "음악", "웃음"
    };
    public static Uri Endpoint(string value, bool socket = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "http"
            || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Group speech recognition must use a local HTTP endpoint.");
        return new UriBuilder(uri) { Scheme = socket ? "ws" : "http", Path = socket ? "/v1/realtime" : "/health" }.Uri;
    }

    public static GroupTranscriptChunk? Parse(string json)
    {
        if (json.Length > 1_000_000) throw new InvalidDataException("Group speech result exceeded its size limit.");
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Group speech result did not contain a valid message type.");
        string? type = typeElement.GetString();
        if (type == "error") throw new InvalidOperationException(root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() ?? "Group speech recognition failed." : "Group speech recognition failed.");
        if (type != "transcript.chunk") return null;
        string session = RequiredString(root, "session_id");
        if (!root.TryGetProperty("epoch", out var epochElement) || epochElement.ValueKind != JsonValueKind.Number || !epochElement.TryGetInt32(out int epoch) ||
            !root.TryGetProperty("chunk_index", out var indexElement) || indexElement.ValueKind != JsonValueKind.Number || !indexElement.TryGetInt64(out long index) ||
            !root.TryGetProperty("audio_start_ms", out var startElement) || startElement.ValueKind != JsonValueKind.Number || !startElement.TryGetDouble(out double start) ||
            !root.TryGetProperty("audio_end_ms", out var endElement) || endElement.ValueKind != JsonValueKind.Number || !endElement.TryGetDouble(out double end))
            throw new InvalidDataException("Group speech result did not contain valid numeric audio bounds.");
        if (session.Length is < 1 or > 128 || epoch < 0 || index < 0 || !double.IsFinite(start) || !double.IsFinite(end)
            || start < 0 || end <= start || end - start > 10_000 || end > TimeSpan.FromDays(7).TotalMilliseconds)
            throw new InvalidDataException("Group speech result contained invalid session or audio bounds.");
        if (root.TryGetProperty("word_timestamps", out var timestamps) && timestamps.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Unsupported word-timing protocol; no person mapping was applied.");
        if (!root.TryGetProperty("segments", out JsonElement segmentsElement) || segmentsElement.ValueKind != JsonValueKind.Array ||
            segmentsElement.GetArrayLength() > 256)
            throw new InvalidDataException("Group speech result did not contain a bounded voice list.");
        var segments = new List<GroupVoiceText>();
        int totalText = 0;
        foreach (JsonElement item in segmentsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("voice_id", out JsonElement voice))
                throw new InvalidDataException("Group speech returned an invalid temporary voice label.");
            bool unidentified = voice.ValueKind == JsonValueKind.Null;
            string id = unidentified ? UnidentifiedVoiceId : voice.ValueKind == JsonValueKind.Number && voice.TryGetInt32(out int numeric)
                ? numeric.ToString(CultureInfo.InvariantCulture)
                : voice.ValueKind == JsonValueKind.String ? voice.GetString() ?? "" : "";
            string text = RequiredString(item, "text").Trim();
            int ordinal = 0;
            if (!unidentified && (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) || ordinal is < 0 or > 255))
                throw new InvalidDataException("Group speech returned an invalid temporary voice label.");
            totalText += text.Length;
            if (text.Length > 16_000 || totalText > 64_000) throw new InvalidDataException("Group speech output exceeded its text limit.");
            if (text.Length > 0) segments.Add(new(unidentified ? UnidentifiedVoiceId : ordinal.ToString(CultureInfo.InvariantCulture), text));
        }
        // Validate the raw limits above, then remove non-spoken annotations. An
        // empty result remains a real decoded chunk so timing/settlement advances.
        return FilterNonSpeechAnnotations(new(session, epoch, index, start, end, segments));
    }

    internal static GroupTranscriptChunk FilterNonSpeechAnnotations(GroupTranscriptChunk chunk)
    {
        int removed = 0;
        var speech = new List<GroupVoiceText>(chunk.Segments.Count);
        foreach (GroupVoiceText segment in chunk.Segments)
        {
            int segmentRemoved = 0;
            string cleaned = segment.Text.Contains('[') ? Annotation.Replace(segment.Text, match =>
            {
                string label = Whitespace.Replace(match.Groups["label"].Value.Trim(), " ");
                if (!SoundLabels.Contains(label)) return match.Value;
                segmentRemoved++;
                return " "; // Do not join words around an annotation into one word.
            }) : segment.Text;
            if (segmentRemoved > 0)
            {
                cleaned = Whitespace.Replace(cleaned, " ").Trim();
                if (cleaned.All(character => char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character)))
                    cleaned = "";
            }
            int controls = RecognitionTextFilter.ControlTokenCount(cleaned);
            if (controls > 0) { segmentRemoved += controls; cleaned = ""; }
            removed += segmentRemoved;
            if (cleaned.Length > 0) speech.Add(segment with { Text = cleaned });
        }
        return removed == 0 ? chunk : chunk with
        {
            Segments = speech,
            NonSpeechAnnotationCount = chunk.NonSpeechAnnotationCount + removed
        };
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Group speech result did not contain valid text fields.");
        return value.GetString() ?? "";
    }

    public static string Key(GroupTranscriptChunk chunk, string voice) => $"vibevoice:{chunk.SessionId}:{chunk.Epoch}:{voice}";
    internal static bool IsUnidentifiedKey(string? key) => key?.EndsWith(":" + UnidentifiedVoiceId, StringComparison.Ordinal) == true;
    public static string VoiceLabel(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "Voice";
        string tail = key[(key.LastIndexOf(':') + 1)..];
        return int.TryParse(tail, out int ordinal) && ordinal is >= 0 and <= 255 ? $"Voice {ordinal + 1}" : "Voice";
    }
}

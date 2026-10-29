using System.Globalization;
using System.Text;

namespace IncomingTranslator;

internal enum DiagnosticArea { App, OwnVoice, Subtitles }
internal sealed record DiagnosticIssue(DateTimeOffset At, DiagnosticArea Area, string Stage, string Code,
    string Message, bool Recovered = false, int Occurrences = 1);
internal sealed record DiagnosticsSnapshot(IReadOnlyList<DiagnosticIssue> Issues, DateTimeOffset CheckedAt)
{
    internal static readonly DiagnosticsSnapshot Empty = new([], DateTimeOffset.MinValue);
    internal DiagnosticIssue? ActiveOwnIssue(DateTimeOffset now)
    {
        // Historical problems remain in Troubleshooting, but never masquerade as current failures.
        var own = Issues.Where(issue => issue.Area == DiagnosticArea.OwnVoice).ToArray();
        return own.Where(issue => !issue.Recovered && now >= issue.At && now - issue.At <= TimeSpan.FromSeconds(45)
                && !own.Any(success => success.Recovered && success.At >= issue.At &&
                    ResolvesOwnIssue(success, issue)))
            .MaxBy(issue => issue.At);
    }

    private static bool ResolvesOwnIssue(DiagnosticIssue success, DiagnosticIssue issue)
    {
        // Mute verification shares the capture stage; readiness or an explicit opt-out ends only that wait.
        if (success.Code is "mute-state-ready" or "mute-sync-disabled") return issue.Code == "mute-state-unverified";
        if (issue.Code == "mute-state-unverified") return false;
        return success.Stage == issue.Stage || success.Code == "own-voice-recovered";
    }
}

/// <summary>Reads small, fixed-name local tails. Only whitelisted, text-free event summaries leave this class.</summary>
internal sealed class DiagnosticsMonitor(string root)
{
    internal const int TailLimit = 64 * 1024;
    internal static readonly string[] LogNames = ["saywhat.log", "incoming-translator.log", "foxtrans-local-host.log",
        "foxtrans-mute-sync.log", "foxtrans-osc-sentence.log", "foxtrans-provider-errors.log", "localtalk.log"];
    private readonly string _root = Path.GetFullPath(root);
    private readonly Dictionary<string, (long Length, DateTime Modified, DiagnosticIssue[] Issues)> _cache = new(StringComparer.Ordinal);

    internal DiagnosticsSnapshot Read(DateTimeOffset now)
    {
        var issues = new List<DiagnosticIssue>();
        foreach (string name in LogNames)
        {
            string path = Path.Combine(_root, name);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { _cache.Remove(name); continue; }
                // A configured log folder must not turn diagnostics into arbitrary-file reads.
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                long length = info.Length; DateTime modified = info.LastWriteTimeUtc;
                if (!_cache.TryGetValue(name, out var cached) || cached.Length != length || cached.Modified != modified)
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    bool partial = file.Length > TailLimit;
                    if (partial) file.Seek(-TailLimit, SeekOrigin.End);
                    using var reader = new StreamReader(file, Encoding.UTF8, true);
                    if (partial) reader.ReadLine(); // Do not classify a half event or UTF-8 prefix.
                    var parsed = new List<DiagnosticIssue>();
                    while (reader.ReadLine() is string line)
                        if (TryParse(line, name, out DiagnosticIssue? issue)) parsed.Add(issue!);
                    cached = (length, modified, parsed.ToArray()); _cache[name] = cached;
                }
                issues.AddRange(cached.Issues);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Failure itself is useful, without exporting the exception's potentially private file path.
                issues.Add(new(now, AreaFor(name), "App", "log-unavailable", "A diagnostic log couldn't be read. Try Refresh."));
            }
        }
        DateTimeOffset cutoff = now.AddDays(-7);
        var grouped = issues.Where(issue => issue.At >= cutoff && issue.At <= now.AddMinutes(1))
            .GroupBy(issue => (issue.Area, issue.Stage, issue.Code))
            .Select(group => group.MaxBy(issue => issue.At)! with { Occurrences = group.Count() })
            .OrderByDescending(issue => issue.At).Take(24).ToArray();
        return new(grouped, now);
    }

    internal static DiagnosticArea AreaFor(string name) => name == "incoming-translator.log" ? DiagnosticArea.Subtitles
        : name is "foxtrans-local-host.log" or "foxtrans-mute-sync.log" or "foxtrans-osc-sentence.log" or "foxtrans-provider-errors.log" ? DiagnosticArea.OwnVoice : DiagnosticArea.App;

    internal static bool TryParse(string line, string logName, out DiagnosticIssue? issue)
    {
        issue = null;
        if (line.Length > TailLimit || !TryTimestamp(line, out DateTimeOffset at, out string body)) return false;
        var area = AreaFor(logName);
        if (body.StartsWith("Microphone mute controller: ", StringComparison.Ordinal))
        {
            area = DiagnosticArea.OwnVoice;
            string nested = body["Microphone mute controller: ".Length..];
            if (TryTimestamp(nested, out DateTimeOffset nestedAt, out string nestedBody)) { at = nestedAt; body = nestedBody; }
            else body = nested;
        }
        // Raw conversation text is never searched for error markers. Only its known event prefix
        // may tell us that a subtitle was published.
        if (body.StartsWith("Source:", StringComparison.Ordinal))
        {
            issue = new(at, area, "Translation", "subtitle-published", "A translated subtitle was shown.", true); return true;
        }
        // Never interpolate the original log, exception, speaker identity, transcript or local endpoint.
        (string Stage, string Code, string Message, bool Recovered)? details = body switch
        {
            var text when HasOwnCode(text, "mute-state-unverified") =>
                ("Audio capture", "mute-state-unverified", "Waiting for VRChat microphone status. If this continues, mute and unmute once in VRChat.", false),
            var text when text.StartsWith("OWN VOICE recovered; code=mute-state-ready;", StringComparison.Ordinal) =>
                ("Audio capture", "mute-state-ready", "VRChat microphone status is ready.", true),
            var text when text.StartsWith("OWN VOICE recovered; code=mute-sync-disabled;", StringComparison.Ordinal) =>
                ("Audio capture", "mute-sync-disabled", "VRChat microphone muting is ignored.", true),
            var text when HasOwnCode(text, "asr-progress-timeout") =>
                ("Speech recognition", "asr-progress-timeout", "Audio was received, but no new words arrived. Speech recognition is recovering.", false),
            var text when HasOwnCode(text, "capture-backpressure") || HasOwnCode(text, "capture-buffer-overflow") =>
                ("Audio capture", "capture-backpressure", "Speech processing fell behind. Queued speech is kept; some new audio was skipped.", false),
            var text when text.StartsWith("OWN VOICE recovered; code=capture-ready;", StringComparison.Ordinal) =>
                ("Audio capture", "capture-recovered", "Audio capture recovered.", true),
            var text when text.StartsWith("OWN VOICE recovered; code=turn-completed;", StringComparison.Ordinal) =>
                ("Speech recognition", "own-voice-recovered", "Your voice translator recovered.", true),
            var text when HasOwnCode(text, "translation-or-output-failed") =>
                ("Translation", "translation-or-output-failed", "Your sentence couldn't finish translating or reach VRChat. See the detailed logs to check which step failed.", false),
            var text when HasOwnCode(text, "asr-empty-final") =>
                ("Speech recognition", "asr-empty-final", "Audio was received, but no readable words were returned for this sentence. Listening continues.", false),
            var text when HasOwnCode(text, "server_busy") || HasOwnCode(text, "server_not_ready") || HasOwnCode(text, "health_request_failed") || HasOwnCode(text, "unexpected_close") || HasOwnCode(text, "invalid_event") || HasOwnCode(text, "other_protocol_failure") || HasOwnCode(text, "provider_failure") || HasOwnCode(text, "session_completion_timeout") || HasOwnCode(text, "bounded_timeout") =>
                ("Speech recognition", "speech-provider-error", "The speech connection failed for this sentence. Captured sentences waiting their turn are kept.", false),
            var text when text.StartsWith("Final speech response stalled:", StringComparison.Ordinal) =>
                ("Speech recognition", "speech-final-timeout", "The final speech response took too long. Speech recognition is reconnecting.", false),
            var text when text.StartsWith("Speech stalled:", StringComparison.Ordinal) || text.StartsWith("Decoder recovery attempts without meaningful progress:", StringComparison.Ordinal) =>
                ("Speech recognition", "speech-no-progress", "Audio was received, but no new words arrived. Speech recognition is recovering.", false),
            var text when text.StartsWith("Speech progress restored", StringComparison.Ordinal) =>
                ("Speech recognition", "speech-recovered", "Speech recognition recovered.", true),
            var text when text.StartsWith("Realtime session reconnect #", StringComparison.Ordinal) =>
                ("Speech recognition", "speech-reconnect", "The speech connection was interrupted and is reconnecting.", false),
            var text when text.StartsWith("Audio backlog capped", StringComparison.Ordinal) || text.StartsWith("Queue limit:", StringComparison.Ordinal) =>
                ("Translation", "queue-overflow", "Translation fell behind. Older work was skipped to keep up with the conversation.", false),
            var text when text.StartsWith("Translation timed out", StringComparison.Ordinal) || HasOwnCode(text, "translation-timeout") || text.StartsWith("ERROR RealtimeOutputTimedOut;", StringComparison.Ordinal) =>
                ("Translation", "translation-timeout", "Words were recognized, but their translation took too long and wasn't shown.", false),
            var text when text.StartsWith("Translation failed:", StringComparison.Ordinal) || HasOwnCode(text, "translation-failed") || text.StartsWith("ERROR RealtimeTranslationFailed;", StringComparison.Ordinal) =>
                ("Translation", "translation-failed", "Words were recognized, but their translation failed.", false),
            var text when text.StartsWith("Capture stopped:", StringComparison.Ordinal) =>
                ("Audio capture", "capture-stopped", "Audio capture stopped unexpectedly. Check your audio device or restart translation.", false),
            var text when text.StartsWith("Playback capture restarted successfully.", StringComparison.Ordinal) =>
                ("Audio capture", "capture-recovered", "Audio capture recovered.", true),
            var text when new[] { "Translation worker failed", "Sentence worker failed", "Audio worker failed", "Translation worker ended unexpectedly;", "Sentence worker ended unexpectedly;", "Audio worker ended unexpectedly;" }.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal)) =>
                ("Translation", "worker-restart", "Part of translation stopped unexpectedly and is restarting.", false),
            var text when HasOwnCode(text, "chat-send-failed") || text.StartsWith("ERROR OutputError;", StringComparison.Ordinal) =>
                ("VRChat chat", "chat-send-failed", "A translation was ready, but couldn't be sent to VRChat. Check the OSC connection.", false),
            var text when logName == "foxtrans-osc-sentence.log" && (text.StartsWith("discarded-final-overflow:", StringComparison.Ordinal) || text.StartsWith("expired-captured-finals:", StringComparison.Ordinal)) =>
                ("VRChat chat", "chat-message-dropped", "A finished chat message was skipped or expired before sending. See the detailed logs for timing and queue details.", false),
            var text when logName == "foxtrans-osc-sentence.log" && text.StartsWith("fatal:", StringComparison.Ordinal) =>
                ("VRChat chat", "chat-connection-stopped", "The VRChat chat connection stopped. Restart your voice translation or check the detailed logs.", false),
            var text when area == DiagnosticArea.OwnVoice && text.StartsWith("FATAL:", StringComparison.Ordinal) =>
                ("Audio capture", "microphone-controller-stopped", "Your microphone controller stopped unexpectedly. Restart your voice translation or check the detailed logs.", false),
            var text when text.StartsWith("Wrong-language translator output rejected", StringComparison.Ordinal) =>
                ("Translation", "wrong-language-retry", "The translator replied in the wrong language. The sentence is being retried.", false),
            var text when text.StartsWith("APP ISSUE code=operation-failed;", StringComparison.Ordinal) =>
                ("App", "operation-failed", "An app action couldn't finish. The detailed logs contain more information.", false),
            _ => null
        };
        if (details is not { } value) return false;
        if (body.StartsWith("ERROR own-voice:", StringComparison.Ordinal) || body.StartsWith("OWN VOICE", StringComparison.Ordinal)) area = DiagnosticArea.OwnVoice;
        issue = new(at, area, value.Stage, value.Code, value.Message, value.Recovered); return true;
    }

    private static bool HasOwnCode(string body, string code) => body.StartsWith("ERROR own-voice: code=" + code + ";", StringComparison.Ordinal);

    internal static bool TryTimestamp(string line, out DateTimeOffset timestamp, out string body)
    {
        timestamp = default; body = "";
        // Application log timestamps are bracketed local time or ISO 8601. No timestamp means no current-warning claim.
        if (!line.StartsWith('[')) return false;
        int closing = line.IndexOf(']'); if (closing is < 2 or > 45) return false;
        if (!DateTimeOffset.TryParse(line[1..closing], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out timestamp)) return false;
        body = line[(closing + 1)..].TrimStart(); return true;
    }

    internal static string BuildSafeSummary(DiagnosticsSnapshot snapshot, bool ownRunning, bool incomingRunning,
        string? incomingStage = null, int recoveries = 0, long droppedAudioChunks = 0)
    {
        // Deliberately do not localize support codes: fixed labels keep reports easy to compare across locales.
        var output = new StringBuilder();
        output.AppendLine($"SayWhat? {AppBrand.Version} diagnostics");
        output.AppendLine("Speech, translations, people, file paths and exception details are excluded.");
        output.AppendLine("Checked UTC: " + snapshot.CheckedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        output.AppendLine($"Own voice running: {ownRunning}; subtitles running: {incomingRunning}");
        if (incomingStage is "waiting" or "listening" or "translating" or "stalled" or "recovering" or "reconnecting" or "backlog" or "translation-error")
            output.AppendLine($"Subtitle stage: {incomingStage}; recoveries: {Math.Max(0, recoveries)}; dropped audio chunks: {Math.Max(0, droppedAudioChunks)}");
        foreach (DiagnosticIssue item in snapshot.Issues.Where(IsSafeIssue).Take(24))
            output.AppendLine($"{item.At.UtcDateTime:O} | {item.Area} | {item.Stage} | {item.Code} | {(item.Recovered ? "recovered" : "issue")} | occurrences={item.Occurrences}");
        return output.ToString();
    }

    private static bool IsSafeIssue(DiagnosticIssue issue) => Enum.IsDefined(issue.Area) &&
        issue.Stage is "App" or "Speech recognition" or "Audio capture" or "Translation" or "VRChat chat" &&
        issue.Code is "mute-state-unverified" or "mute-state-ready" or "mute-sync-disabled" or "asr-progress-timeout" or "capture-backpressure" or "capture-recovered" or "own-voice-recovered" or
            "translation-or-output-failed" or "asr-empty-final" or "speech-provider-error" or "speech-final-timeout" or
            "speech-no-progress" or "speech-recovered" or "speech-reconnect" or "queue-overflow" or "translation-timeout" or
            "translation-failed" or "capture-stopped" or "worker-restart" or "chat-send-failed" or "wrong-language-retry" or
            "subtitle-published" or "log-unavailable" or "operation-failed" or "chat-message-dropped" or
            "chat-connection-stopped" or "microphone-controller-stopped" && issue.Occurrences > 0;
}

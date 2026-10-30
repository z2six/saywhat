using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

internal enum DiagnosticArea { App, OwnVoice, Subtitles }
internal sealed record DiagnosticFailureEvidence(string Component, string Category, string? ExceptionType = null,
    string? HResult = null, int? NativeCode = null);
internal sealed record DiagnosticIssue(DateTimeOffset At, DiagnosticArea Area, string Stage, string Code,
    string Message, bool Recovered = false, int Occurrences = 1)
{
    internal DiagnosticFailureEvidence? Evidence { get; init; }
}
internal sealed record DiagnosticRuntimeEvent(DateTimeOffset At, string Component, string Event,
    int? Restart = null, int? Exit = null, string? Code = null, string? HResult = null, int? NativeCode = null,
    int? Frames = null, int? Voiced = null, int? Peak = null, int? Chars = null, int? DurationMilliseconds = null);

// A report has a deliberately smaller schema than settings. Even an advanced
// imported model alias, device name or endpoint must never become report text.
internal sealed record DiagnosticSupportContext(string WindowsVersion, string OsArchitecture, string ProcessArchitecture,
    string OwnRecognizer, string OwnSource, IReadOnlyList<string> OwnTargets, string OwnTranslator,
    string IncomingRecognizer, string IncomingSource, string IncomingTarget, string IncomingTranslator,
    string Runtime, bool ManagedTranslator, bool SharedTranslator, bool PauseWhenMuted, string Microphone, string IncomingAudioMode,
    bool? IncomingManagedTranslator = null, string? IncomingRuntime = null)
{
    internal static DiagnosticSupportContext Create(AppSettings settings, AppSettings? incoming = null)
    {
        incoming ??= settings;
        return new(Environment.OSVersion.Version.ToString(), RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
            settings.OwnRecognitionEngine, settings.OwnVoiceSourceLanguage, settings.EffectiveOwnVoiceTargetLanguages(),
            settings.OwnManagedModelId, incoming.IncomingSpeechEngine, incoming.IncomingSourceLanguage, incoming.IncomingTargetLanguage,
            incoming.ShareTranslationModel ? incoming.OwnManagedModelId : incoming.IncomingManagedModelId,
            settings.ManagedRuntimeId, settings.UseManagedTranslator, incoming.ShareTranslationModel, settings.OwnVoicePauseWhenVrchatMuted,
            string.IsNullOrWhiteSpace(settings.OwnMicrophoneDeviceId) || settings.OwnMicrophoneDeviceId == "default" ? "default" : "selected",
            string.IsNullOrWhiteSpace(incoming.IncomingAudioMode) ? incoming.CaptureVrChatOnly ? "vrchat" : "desktop" : incoming.IncomingAudioMode,
            incoming.UseManagedTranslator, incoming.ManagedRuntimeId);
    }
}
internal sealed record DiagnosticsSnapshot(IReadOnlyList<DiagnosticIssue> Issues, DateTimeOffset CheckedAt)
{
    internal static readonly DiagnosticsSnapshot Empty = new([], DateTimeOffset.MinValue);
    internal IReadOnlyList<DiagnosticRuntimeEvent> RuntimeEvents { get; init; } = [];
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
    private readonly object _readGate = new();
    private readonly Dictionary<string, (long Length, DateTime Modified, DiagnosticIssue[] Issues, DiagnosticRuntimeEvent[] Events)> _cache = new(StringComparer.Ordinal);

    internal DiagnosticsSnapshot Read(DateTimeOffset now)
    {
        lock (_readGate) return ReadCore(now);
    }

    private DiagnosticsSnapshot ReadCore(DateTimeOffset now)
    {
        var issues = new List<DiagnosticIssue>();
        var runtimeEvents = new List<DiagnosticRuntimeEvent>();
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
                    var events = new List<DiagnosticRuntimeEvent>();
                    while (reader.ReadLine() is string line)
                    {
                        if (TryParse(line, name, out DiagnosticIssue? issue)) parsed.Add(issue!);
                        if (TryRuntimeEvent(line, name, out DiagnosticRuntimeEvent? status)) events.Add(status!);
                    }
                    cached = (length, modified, parsed.ToArray(), events.ToArray()); _cache[name] = cached;
                }
                issues.AddRange(cached.Issues);
                runtimeEvents.AddRange(cached.Events);
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
        return new(grouped, now)
        {
            RuntimeEvents = runtimeEvents.Where(item => item.At >= cutoff && item.At <= now.AddMinutes(1))
                .GroupBy(item => (item.Component, item.Event)).Select(group => group.MaxBy(item => item.At)!)
                .OrderByDescending(item => item.At).Take(16).ToArray()
        };
    }

    internal static DiagnosticArea AreaFor(string name) => name == "incoming-translator.log" ? DiagnosticArea.Subtitles
        : name is "foxtrans-local-host.log" or "foxtrans-mute-sync.log" or "foxtrans-osc-sentence.log" or "foxtrans-provider-errors.log" ? DiagnosticArea.OwnVoice : DiagnosticArea.App;

    internal static bool TryParse(string line, string logName, out DiagnosticIssue? issue)
    {
        issue = null;
        if (!TryKnownBody(line, logName, out DateTimeOffset at, out string body, out string component, out bool nestedOwn)) return false;
        var area = AreaFor(logName);
        if (nestedOwn) area = DiagnosticArea.OwnVoice;
        // Raw conversation text is never searched for error markers. Only its known event prefix
        // may tell us that a subtitle was published.
        if (body.StartsWith("Source:", StringComparison.Ordinal))
        {
            issue = new(at, area, "Translation", "subtitle-published", "A translated subtitle was shown.", true); return true;
        }
        if (RuntimeController.TryParseSupportStatus(body, out var runtime) && runtime is not null)
        {
            if (runtime.Event == "capture-ready")
            {
                issue = new(at, DiagnosticArea.OwnVoice, "Audio capture", "capture-recovered", "Audio capture recovered.", true);
                return true;
            }
            if (runtime.Event is "failed" or "host-exited")
            {
                string failedComponent = runtime.Event == "host-exited" ? "microphone-host" : runtime.Component;
                var evidence = new DiagnosticFailureEvidence(failedComponent, SafeFailureCode(runtime.Code) ?? "unknown",
                    HResult: SafeHResult(runtime.HResult), NativeCode: SafeNativeCode(runtime.NativeCode));
                issue = FailureIssue(at, evidence); return true;
            }
            // A process starting or opening its control socket does not prove
            // that audio is captured. Keep it in report metadata, not warnings.
            return false;
        }
        if (area == DiagnosticArea.OwnVoice && TryFatalEvidence(body, component, out var failure))
        {
            issue = FailureIssue(at, failure!); return true;
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

    private static bool TryKnownBody(string line, string logName, out DateTimeOffset at, out string body,
        out string component, out bool nestedOwn)
    {
        component = logName switch
        {
            "foxtrans-local-host.log" => "microphone-host",
            "foxtrans-mute-sync.log" => "microphone-controller",
            "foxtrans-osc-sentence.log" => "osc-relay",
            _ => "unknown"
        };
        nestedOwn = false;
        if (line.Length > TailLimit || !TryTimestamp(line, out at, out body)) { at = default; body = ""; return false; }
        if (logName is not ("saywhat.log" or "localtalk.log")) return true;
        foreach (var prefix in new[] { "Microphone mute controller: ", "OSC final-message proxy: " })
        {
            if (!body.StartsWith(prefix, StringComparison.Ordinal)) continue;
            nestedOwn = true;
            // The controller inherits child stdout. This prefix cannot tell us
            // whether a legacy FATAL came from the host or controller itself.
            component = prefix.StartsWith("OSC", StringComparison.Ordinal) ? "osc-relay" : "unknown";
            body = body[prefix.Length..];
            if (TryTimestamp(body, out DateTimeOffset nestedAt, out string nestedBody)) { at = nestedAt; body = nestedBody; }
            break;
        }
        return true;
    }

    private static bool TryRuntimeEvent(string line, string logName, out DiagnosticRuntimeEvent? result)
    {
        result = null;
        if (!TryKnownBody(line, logName, out var at, out string body, out _, out _) ||
            !RuntimeController.TryParseSupportStatus(body, out var status) || status is null) return false;
        result = new(at, status.Component, status.Event, status.Restart, status.Exit, SafeFailureCode(status.Code),
            SafeHResult(status.HResult), SafeNativeCode(status.NativeCode), status.Frames, status.Voiced, status.Peak,
            status.Chars, status.DurationMilliseconds);
        return true;
    }

    private static readonly string[] KnownExceptionTypes =
    [
        "DllNotFoundException", "EntryPointNotFoundException", "BadImageFormatException", "AudioDeviceSelectionException",
        "UnauthorizedAccessException", "FileNotFoundException", "DirectoryNotFoundException", "IOException",
        "HttpRequestException", "WebSocketException", "SocketException", "TimeoutException", "Win32Exception",
        "InvalidOperationException", "ArgumentException", "ConfigurationException", "JsonException", "AggregateException", "COMException", "TypeInitializationException"
    ];
    private static readonly string[] FailureCodes =
    [
        "native-library-missing", "native-library-invalid", "native-library-incompatible", "microphone-unavailable",
        "microphone-access-denied", "access-denied", "endpoint-unavailable", "provider-connection-failed",
        "log-unavailable", "file-in-use", "file-missing", "port-in-use", "host-failed", "controller-failed", "proxy-failed", "unknown"
    ];
    private static string? SafeFailureCode(string? code) => code is not null && FailureCodes.Contains(code, StringComparer.Ordinal) ? code : null;
    private static string? SafeComponent(string? component) => component is "microphone-controller" or "microphone-host" or "osc-relay" or "unknown" ? component : null;
    private static string? SafeHResult(string? value) => value is { Length: 10 } && value.ToUpperInvariant() is string upper &&
        upper is "0X80070005" or "0X80070020" or "0X8007007E" or "0X8007000B" or "0X8007007F" or "0X80070490" or
            "0X88890004" or "0X8889000A" or "0X8889000F" or "0X88890008" or "0X80004005"
            ? "0x" + upper[2..] : null;
    private static int? SafeNativeCode(int? value) => value is -6 or -204 or 5 or 32 or 87 or 126 or 127 or 193 or 1168 or
        10013 or 10048 or 10049 or 10050 or 10051 or 10060 or 10061 or 10065 ? value : null;

    private static bool TryFatalEvidence(string body, string component, out DiagnosticFailureEvidence? result)
    {
        result = null;
        if (!body.StartsWith("FATAL:", StringComparison.Ordinal) && !(component == "osc-relay" && body.StartsWith("fatal:", StringComparison.Ordinal))) return false;
        string payload = body[6..].TrimStart();
        // Only an anchored .NET type declaration is evidence. Never search an
        // arbitrary exception message for keywords that could be spoken text.
        Match typed = Regex.Match(payload[..Math.Min(256, payload.Length)],
            @"^(?:(?:System(?:\.[A-Za-z][A-Za-z0-9]*)*\.)?)(?<type>[A-Za-z][A-Za-z0-9]*Exception)(?:\s+\((?<number>0x[A-Fa-f0-9]{8}|-?[0-9]{1,10})\))?(?::|$)",
            RegexOptions.CultureInvariant);
        string? exceptionType = typed.Success && KnownExceptionTypes.Contains(typed.Groups["type"].Value, StringComparer.Ordinal)
            ? typed.Groups["type"].Value : null;
        string? hresult = null; int? native = null;
        if (exceptionType is not null && typed.Groups["number"].Success)
        {
            string number = typed.Groups["number"].Value;
            if (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hresult = SafeHResult(number);
            else if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) native = SafeNativeCode(parsed);
        }
        string category = exceptionType switch
        {
            "DllNotFoundException" => "native-library-missing",
            "BadImageFormatException" => "native-library-invalid",
            "EntryPointNotFoundException" => "native-library-incompatible",
            "AudioDeviceSelectionException" => "microphone-unavailable",
            "UnauthorizedAccessException" => "access-denied",
            "FileNotFoundException" or "DirectoryNotFoundException" => "file-missing",
            "SocketException" when native == 10048 => "port-in-use",
            "SocketException" when native == 10013 => "access-denied",
            "SocketException" => "endpoint-unavailable",
            "HttpRequestException" or "WebSocketException" => "provider-connection-failed",
            "IOException" when hresult == "0x80070020" => "file-in-use",
            _ when hresult == "0x80070005" => "access-denied",
            _ => "unknown"
        };
        result = new(SafeComponent(component) ?? "unknown", category, exceptionType, hresult, native);
        return true;
    }

    // Used before a supervised process's privacy filter. This returns a fixed
    // type/code declaration only, never the exception message or stack trace.
    internal static string? SafeFatalDiagnosticLine(string line, string componentHint = "unknown")
    {
        string body = line;
        if (TryTimestamp(line, out _, out string nested)) body = nested;
        if (!TryFatalEvidence(body, SafeComponent(componentHint) ?? "unknown", out var failure)) return null;
        if (failure!.ExceptionType is null) return "FATAL: unknown";
        string numeric = failure.HResult ?? failure.NativeCode?.ToString(CultureInfo.InvariantCulture) ?? "";
        return "FATAL: " + failure.ExceptionType + (numeric.Length == 0 ? "" : " (" + numeric + ")") + ":";
    }

    private static DiagnosticIssue FailureIssue(DateTimeOffset at, DiagnosticFailureEvidence evidence)
    {
        string category = SafeFailureCode(evidence.Category) ?? "unknown";
        if (category is "unknown" or "host-failed" or "controller-failed" or "proxy-failed")
            category = SafeHResult(evidence.HResult) switch
            {
                "0x80070005" => "access-denied", "0x80070020" => "file-in-use",
                "0x8007007E" => "native-library-missing", "0x8007000B" => "native-library-invalid",
                "0x8007007F" => "native-library-incompatible",
                _ => SafeNativeCode(evidence.NativeCode) switch
                {
                    5 or 10013 => "access-denied", 32 => "file-in-use", 126 => "native-library-missing",
                    127 => "native-library-incompatible", 193 => "native-library-invalid", 10048 => "port-in-use",
                    10060 or 10061 or 10065 => "endpoint-unavailable", _ => category
                }
            };
        (string stage, string code, string message) = category switch
        {
            "native-library-missing" => ("Audio capture", category, "A required native component is missing. Reinstall SayWhat and check Windows security history."),
            "native-library-invalid" or "native-library-incompatible" => ("Audio capture", category, "A native component couldn't be loaded. Reinstall the Windows x64 version of SayWhat."),
            "microphone-unavailable" => ("Audio capture", category, "No usable microphone was found. Choose a connected microphone on Home."),
            "microphone-access-denied" => ("Audio capture", category, "Microphone access was denied. Check Windows microphone permissions and whether another app is using the device."),
            "access-denied" => ("App", category, "An app file or component couldn't be accessed. Check its permissions and Windows security history."),
            "file-in-use" => ("App", category, "A required app file is in use. Close other tools using it and try again."),
            "file-missing" => ("App", category, "A required app file couldn't be found. Check setup or reinstall SayWhat."),
            "port-in-use" => ("App", category, "Another app is already using a required local port. Stop the other translation tool and try again."),
            "endpoint-unavailable" or "provider-connection-failed" => ("App", category, "A required local service couldn't be reached. Restart your voice translation or check its startup log."),
            _ when evidence.Component == "osc-relay" => ("VRChat chat", "chat-connection-stopped", "The VRChat chat connection stopped. Restart your voice translation or check the detailed logs."),
            _ when evidence.Component == "microphone-controller" => ("Audio capture", "microphone-controller-stopped", "Your microphone controller stopped unexpectedly. Restart your voice translation or check the detailed logs."),
            _ when evidence.Component == "microphone-host" => ("Audio capture", "microphone-host-stopped", "Your microphone host stopped unexpectedly. Restart your voice translation or check the detailed logs."),
            _ => ("Audio capture", "microphone-process-stopped", "Your microphone tools stopped unexpectedly. Restart your voice translation or check the detailed logs.")
        };
        return new(at, DiagnosticArea.OwnVoice, stage, code, message) { Evidence = evidence with { Category = category } };
    }

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
        string? incomingStage = null, int recoveries = 0, long droppedAudioChunks = 0, DiagnosticSupportContext? context = null)
    {
        // Deliberately do not localize support codes: fixed labels keep reports easy to compare across locales.
        var output = new StringBuilder();
        output.AppendLine($"SayWhat? {AppBrand.Version} diagnostics");
        output.AppendLine("Speech, translations, people, file paths and raw exception text are excluded.");
        output.AppendLine("Checked UTC: " + snapshot.CheckedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        output.AppendLine("Windows: " + SafeVersion(context?.WindowsVersion ?? Environment.OSVersion.Version.ToString()) +
            "; OS architecture: " + SafeArchitecture(context?.OsArchitecture ?? RuntimeInformation.OSArchitecture.ToString()) +
            "; process architecture: " + SafeArchitecture(context?.ProcessArchitecture ?? RuntimeInformation.ProcessArchitecture.ToString()));
        output.AppendLine($"CPU features: AVX={System.Runtime.Intrinsics.X86.Avx.IsSupported}; AVX2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
        output.AppendLine($"Own voice running: {ownRunning}; subtitles running: {incomingRunning}");
        if (context is not null) AppendSupportContext(output, context);
        if (incomingStage is "waiting" or "listening" or "translating" or "stalled" or "recovering" or "reconnecting" or "backlog" or "translation-error")
            output.AppendLine($"Subtitle stage: {incomingStage}; recoveries: {Math.Max(0, recoveries)}; dropped audio chunks: {Math.Max(0, droppedAudioChunks)}");
        foreach (DiagnosticIssue item in snapshot.Issues.Where(IsSafeIssue).Take(24))
        {
            output.AppendLine($"{item.At.UtcDateTime:O} | {item.Area} | {item.Stage} | {item.Code} | {(item.Recovered ? "recovered" : "issue")} | occurrences={item.Occurrences}");
            if (item.Evidence is { } evidence && SafeComponent(evidence.Component) is string component && SafeFailureCode(evidence.Category) is string category)
            {
                output.Append("  Failure evidence: component=").Append(component).Append("; category=").Append(category);
                if (evidence.ExceptionType is string exceptionType && KnownExceptionTypes.Contains(exceptionType, StringComparer.Ordinal))
                    output.Append("; exception=").Append(exceptionType);
                if (SafeHResult(evidence.HResult) is string hresult) output.Append("; hresult=").Append(hresult);
                if (SafeNativeCode(evidence.NativeCode) is int native) output.Append("; native=").Append(native.ToString(CultureInfo.InvariantCulture));
                output.AppendLine();
            }
        }
        foreach (DiagnosticRuntimeEvent item in snapshot.RuntimeEvents.Where(IsSafeRuntimeEvent).OrderByDescending(item => item.At).Take(16))
        {
            output.Append($"{item.At.UtcDateTime:O} | runtime | ").Append(item.Component).Append(" | ").Append(item.Event);
            if (item.Restart is int restart) output.Append(" | restart=").Append(restart.ToString(CultureInfo.InvariantCulture));
            if (item.Exit is int exit) output.Append(" | exit=").Append(exit.ToString(CultureInfo.InvariantCulture));
            if (SafeFailureCode(item.Code) is string code) output.Append(" | code=").Append(code);
            if (SafeHResult(item.HResult) is string hresult) output.Append(" | hresult=").Append(hresult);
            if (SafeNativeCode(item.NativeCode) is int native) output.Append(" | native=").Append(native.ToString(CultureInfo.InvariantCulture));
            if (item.Frames is int frames) output.Append(" | frames=").Append(frames.ToString(CultureInfo.InvariantCulture));
            if (item.Voiced is int voiced) output.Append(" | voiced=").Append(voiced.ToString(CultureInfo.InvariantCulture));
            if (item.Peak is int peak) output.Append(" | peak_rms_millionths=").Append(peak.ToString(CultureInfo.InvariantCulture));
            if (item.Chars is int chars) output.Append(" | chars=").Append(chars.ToString(CultureInfo.InvariantCulture));
            if (item.DurationMilliseconds is int duration) output.Append(" | duration_ms=").Append(duration.ToString(CultureInfo.InvariantCulture));
            output.AppendLine();
        }
        return output.ToString();
    }

    private static string SafeVersion(string? value) => value is not null && value.Length <= 40 &&
        Regex.IsMatch(value, @"\A[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,8}(?:\.[0-9]{1,8})?\z", RegexOptions.CultureInvariant)
            ? value : "unknown";
    private static string SafeArchitecture(string? value) => value is "X64" or "X86" or "Arm64" or "Arm" or "Wasm" ? value : "unknown";
    private static string SafeLanguage(string? value) => value is "Automatic" ? value : AppSettings.CanonicalTranslationLanguage(value) ?? "unknown";
    private static string SafeRecognizer(string? value) => value is "nemotron" or "voxtral" or "firered2-ctc" or "firered2-aed" or "vibevoice" ? value : "unknown";
    private static string SafeModel(string? value) => LocalTalkProvisioner.ModelCatalog.Any(model =>
        model.Role == ProvisionModelRole.Translation && model.Id == value) ? value! : "custom-or-unavailable";
    private static string SafeRuntime(string? value) => LocalTalkProvisioner.RuntimeCatalog.Any(runtime => runtime.Id == value) ? value! : "custom-or-unavailable";
    private static void AppendSupportContext(StringBuilder output, DiagnosticSupportContext context)
    {
        bool incomingManaged = context.IncomingManagedTranslator ?? context.ManagedTranslator;
        output.Append("Own choices: recognizer=").Append(SafeRecognizer(context.OwnRecognizer)).Append("; source=").Append(SafeLanguage(context.OwnSource))
            .Append("; targets=").Append(string.Join(",", (context.OwnTargets ?? []).Take(4).Select(SafeLanguage)))
            .Append("; translator=").Append(context.ManagedTranslator ? SafeModel(context.OwnTranslator) : "external")
            .Append("; microphone=").Append(context.Microphone is "default" or "selected" ? context.Microphone : "unknown")
            .Append("; follow_vrchat_mute=").Append(context.PauseWhenMuted).AppendLine();
        output.Append("Subtitle choices: recognizer=").Append(SafeRecognizer(context.IncomingRecognizer))
            .Append("; source=").Append(SafeLanguage(context.IncomingSource)).Append("; target=").Append(SafeLanguage(context.IncomingTarget))
            .Append("; translator=").Append(incomingManaged ? SafeModel(context.IncomingTranslator) : "external")
            .Append("; audio_mode=").Append(context.IncomingAudioMode is "vrchat" or "application" or "desktop" ? context.IncomingAudioMode : "unknown").AppendLine();
        output.Append("Translation runtime: ").Append(context.ManagedTranslator ? SafeRuntime(context.Runtime) : "external")
            .Append("; subtitle_runtime=").Append(incomingManaged ? SafeRuntime(context.IncomingRuntime ?? context.Runtime) : "external")
            .Append("; shared_model=").Append(context.SharedTranslator).AppendLine();
    }
    private static bool IsSafeRuntimeEvent(DiagnosticRuntimeEvent item) =>
        SafeComponent(item.Component) is not null && item.Component != "unknown" &&
        item.Event is "host-started" or "host-exited" or "control-ready" or "capture-ready" or "failed" or
            "capture-levels" or "recognition-final" or "translation-start" or "translation-completed" or "chat-queued" &&
        (item.Restart is null or >= 0) && (item.Code is null || SafeFailureCode(item.Code) is not null) &&
        (item.HResult is null || SafeHResult(item.HResult) is not null) && (item.NativeCode is null || SafeNativeCode(item.NativeCode) is not null) &&
        (item.Frames is null or >= 0) && (item.Voiced is null or >= 0) &&
        (item.Frames is null || item.Voiced is null || item.Voiced <= item.Frames) &&
        (item.Peak is null or >= 0 and <= 1_000_000) && (item.Chars is null or >= 0 and <= 1_000_000) &&
        (item.DurationMilliseconds is null or >= 0 and <= 86_400_000);

    private static bool IsSafeIssue(DiagnosticIssue issue) => Enum.IsDefined(issue.Area) &&
        issue.Stage is "App" or "Speech recognition" or "Audio capture" or "Translation" or "VRChat chat" &&
        issue.Code is "mute-state-unverified" or "mute-state-ready" or "mute-sync-disabled" or "asr-progress-timeout" or "capture-backpressure" or "capture-recovered" or "own-voice-recovered" or
            "translation-or-output-failed" or "asr-empty-final" or "speech-provider-error" or "speech-final-timeout" or
            "speech-no-progress" or "speech-recovered" or "speech-reconnect" or "queue-overflow" or "translation-timeout" or
            "translation-failed" or "capture-stopped" or "worker-restart" or "chat-send-failed" or "wrong-language-retry" or
            "subtitle-published" or "log-unavailable" or "operation-failed" or "chat-message-dropped" or
            "chat-connection-stopped" or "microphone-controller-stopped" or "microphone-host-stopped" or "microphone-process-stopped" or
            "native-library-missing" or "native-library-invalid" or "native-library-incompatible" or "microphone-unavailable" or "microphone-access-denied" or
            "access-denied" or "file-in-use" or "file-missing" or "port-in-use" or "endpoint-unavailable" or "provider-connection-failed" && issue.Occurrences > 0;
}

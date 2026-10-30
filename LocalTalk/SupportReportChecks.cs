using System.Net;
using System.Text;
using System.Text.Json;

namespace IncomingTranslator;

internal sealed record SupportServiceTarget(string Component, Uri Endpoint);
internal sealed record SupportCheck(string Component, string Result, int? HttpStatus = null);

// A support check observes installed files and already-running owned services.
// It never loads a model, opens capture, sends chat, repairs setup or uploads a
// report. Responses are reduced to fixed states, never copied into the report.
internal static class SupportReportChecks
{
    internal const string LastFailureFile = "saywhat-support-last-failure.txt";
    internal static readonly string[] Components = ["own-speech", "subtitle-speech", "own-translation", "subtitle-translation", "groups-speech"];
    internal static readonly string[] Results = ["present", "missing", "unavailable", "valid", "invalid", "legacy-over-limit",
        "ready", "loading", "busy", "empty", "error", "timeout", "unreachable", "http-error", "invalid-response", "skipped"];
    private static readonly (string Component, string Path)[] Files =
    [
        ("capture-host-executable", "tools/FoxTransLocalHost/FoxTrans.LocalHost.exe"),
        ("capture-host-assembly", "tools/FoxTransLocalHost/FoxTrans.LocalHost.dll"),
        ("capture-native-library", "tools/FoxTransLocalHost/FoxTrans.Native.dll"),
        ("microphone-controller-script", "tools/FoxTrans-VRChatMuteSync.ps1"),
        ("osc-relay-script", "tools/FoxTrans-OscSentenceProxy.ps1")
    ];

    internal static IReadOnlyList<SupportCheck> InspectFiles(string appDirectory, string dataRoot)
    {
        var result = new List<SupportCheck>();
        foreach (var entry in Files)
        {
            try { result.Add(new(entry.Component, File.Exists(Path.Combine(appDirectory, entry.Path)) ? "present" : "missing")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result.Add(new(entry.Component, "unavailable")); }
        }
        bool projected = File.Exists(Path.Combine(dataRoot, OwnVoiceRuntimePreferences.FileName)) ||
            Directory.Exists(Path.Combine(dataRoot, OwnVoiceRuntimePreferences.FileName));
        try
        {
            using var document = OwnVoiceRuntimePreferences.Read(dataRoot);
            result.Add(new("worker-settings", "valid"));
        }
        catch (FileNotFoundException) { result.Add(new("worker-settings", "missing")); }
        catch (JsonException) { result.Add(new("worker-settings", "invalid")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            bool legacyLarge = false;
            try { legacyLarge = !projected && new FileInfo(Path.Combine(dataRoot, OwnVoiceRuntimePreferences.LegacyFileName)).Length > OwnVoiceRuntimePreferences.MaximumBytes; }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException) { }
            result.Add(new("worker-settings", legacyLarge ? "legacy-over-limit" : "unavailable"));
        }
        return result;
    }

    internal static bool CanProbe(SupportServiceTarget target) => Components.Contains(target.Component, StringComparer.Ordinal) &&
        target.Endpoint.IsAbsoluteUri && target.Endpoint.Scheme == "http" && target.Endpoint.IsLoopback &&
        target.Endpoint.Port is > 1023 and <= 65535 && target.Endpoint.UserInfo.Length == 0 &&
        target.Endpoint.Query.Length == 0 && target.Endpoint.Fragment.Length == 0 &&
        target.Endpoint.AbsolutePath is "/ready" or "/health" or "/v1/models" or "/models";

    internal static async Task<IReadOnlyList<SupportCheck>> ProbeAsync(IEnumerable<SupportServiceTarget> targets,
        CancellationToken token, HttpMessageHandler? fixtureHandler = null)
    {
        using var client = new HttpClient(fixtureHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromSeconds(8));
        var result = new List<SupportCheck>();
        foreach (var target in targets.Take(8))
        {
            token.ThrowIfCancellationRequested();
            if (!CanProbe(target)) { result.Add(new("service", "skipped")); continue; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(total.Token); deadline.CancelAfter(TimeSpan.FromMilliseconds(1200));
            try
            {
                using var response = await client.GetAsync(target.Endpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                int status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode) { result.Add(new(target.Component, "http-error", status)); continue; }
                using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                byte[] bytes = new byte[32_769]; int length = 0;
                while (length < bytes.Length)
                {
                    int read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
                    if (read == 0) break;
                    length += read;
                }
                if (length > 32_768) { result.Add(new(target.Component, "invalid-response", status)); continue; }
                using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
                string state = "invalid-response";
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = json.RootElement;
                    if (target.Endpoint.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                        state = root.TryGetProperty("data", out var models) && models.ValueKind == JsonValueKind.Array
                            ? models.GetArrayLength() > 0 ? "ready" : "empty" : "invalid-response";
                    else if (root.TryGetProperty("ready", out var ready) && ready.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        state = ready.GetBoolean() ? "ready" : "loading";
                    else if (root.TryGetProperty("status", out var health) && health.ValueKind == JsonValueKind.String)
                        state = health.GetString() switch { "ok" or "ready" => "ready", "loading" => "loading", "busy" => "busy", "error" => "error", _ => "invalid-response" };
                }
                result.Add(new(target.Component, state, status));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { result.Add(new(target.Component, "timeout")); }
            catch (HttpRequestException) { result.Add(new(target.Component, "unreachable")); }
            catch (JsonException) { result.Add(new(target.Component, "invalid-response")); }
            catch (IOException) { result.Add(new(target.Component, "invalid-response")); }
            if (total.IsCancellationRequested) break;
        }
        return result;
    }

    internal static string Append(string summary, IEnumerable<SupportCheck> checks, VrchatOscConnectionState osc)
    {
        var text = new StringBuilder(summary);
        text.AppendLine("Read-only checks; no capture, model startup, chat test or upload was performed.");
        text.AppendLine("OSC availability: " + (Enum.IsDefined(osc) ? osc.ToString() : "Unknown") + " (not a chat-delivery receipt)");
        foreach (var check in checks.Take(24))
        {
            if (!(Components.Contains(check.Component, StringComparer.Ordinal) || Files.Any(file => file.Component == check.Component) ||
                check.Component is "worker-settings" or "service") || !Results.Contains(check.Result, StringComparer.Ordinal)) continue;
            text.Append(check.Component).Append(": ").Append(check.Result);
            if (check.HttpStatus is >= 100 and <= 599) text.Append("; HTTP ").Append(check.HttpStatus.Value);
            text.AppendLine();
        }
        return text.ToString();
    }
}

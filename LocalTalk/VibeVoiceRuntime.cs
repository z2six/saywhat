using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

internal sealed record VibeVoiceModelChoice(string Id, string Name, long ApproximateDownloadBytes);
internal sealed record VibeVoicePreparation(bool Prepared, string ModelId, string ModelPath, string RuntimeRoot,
    IReadOnlyList<string> Missing);
internal sealed record VibeVoiceHealth(string Status, string Owner, string ModelId, string? Error,
    double? ChunkMilliseconds, double? LookaheadMilliseconds, int ActiveSessions)
{
    internal bool Ready => Status == "ready";
}

/// <summary>
/// Inert process-start construction and local health checks for Groups speech.
/// The caller retains the Process and ownership token. Only explicit Prepare
/// downloads files; Start uses prepared local weights with networking disabled.
/// </summary>
internal static class VibeVoiceRuntime
{
    internal const int Port = 8082;
    internal const string Endpoint = "http://127.0.0.1:8082";
    internal const string DefaultModelId = "microsoft/VibeVoice-ASR-Streaming-1.5B";
    internal const string UpstreamRevision = "1541f590c7099820f10ea012f48d2399282df69f";
    internal static IReadOnlyList<VibeVoiceModelChoice> Models { get; } = new[]
    {
        new VibeVoiceModelChoice(DefaultModelId, "VibeVoice 1.5B", 5_650_000_000),
        new VibeVoiceModelChoice("microsoft/VibeVoice-ASR-Streaming-7B", "VibeVoice 7B", 17_400_000_000)
    };

    internal static bool IsSupportedModel(string? modelId) => Models.Any(model => model.Id == modelId);

    internal static ProcessStartInfo CreatePrepareStartInfo(WslSpeechRegistration registration, string dataRoot, string modelId,
        string? owner = null)
    {
        var info = CreateInfo(registration, "bootstrap-vibevoice.sh", "--prepare", DataPath(dataRoot), ValidateModel(modelId),
            ValidateOwner(owner ?? Guid.NewGuid().ToString("N")));
        // A distinct process group lets explicit cancellation stop only this
        // setup job's apt/pip/download children, not other WSL applications.
        int shell = info.ArgumentList.IndexOf("/bin/bash");
        info.ArgumentList.Insert(shell, "/usr/bin/setsid");
        info.ArgumentList.Insert(shell + 1, "--wait");
        return info;
    }

    internal static ProcessStartInfo CreateCheckStartInfo(WslSpeechRegistration registration, string dataRoot, string modelId) =>
        CreateInfo(registration, "bootstrap-vibevoice.sh", "--check", DataPath(dataRoot), ValidateModel(modelId));

    internal static ProcessStartInfo BuildStartInfo(WslSpeechRegistration registration, string dataRoot, string modelId, string owner,
        int port = Port) =>
        CreateInfo(registration, "run-vibevoice.sh", DataPath(dataRoot), ValidateModel(modelId), ValidateOwner(owner), PortArgument(port));

    internal static ProcessStartInfo BuildStopStartInfo(WslSpeechRegistration registration, string owner, int port = Port) =>
        CreateInfo(registration, "stop-vibevoice.sh", ValidateOwner(owner), PortArgument(port));

    internal static ProcessStartInfo BuildCancelPreparationInfo(WslSpeechRegistration registration, string owner) =>
        CreateInfo(registration, "cancel-prepare.sh", ValidateOwner(owner));

    internal static VibeVoicePreparation ParsePreparation(string output, string expectedModelId)
    {
        ValidateModel(expectedModelId);
        // pip/HuggingFace progress may precede this single JSON receipt. Only a
        // complete matching receipt is accepted; absence never means success.
        foreach (string line in output.Split('\n').Reverse())
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("prepared", out JsonElement prepared) ||
                    !root.TryGetProperty("model_id", out JsonElement model) || model.GetString() != expectedModelId)
                    continue;
                string[] missing = root.GetProperty("missing").EnumerateArray().Select(item => item.GetString() ?? "Missing file").ToArray();
                return new(prepared.ValueKind == JsonValueKind.True && missing.Length == 0, expectedModelId,
                    root.GetProperty("model_path").GetString() ?? "", root.GetProperty("runtime_root").GetString() ?? "", missing);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { /* Keep looking for a well-formed preparation receipt. */ }
        }
        return new(false, expectedModelId, "", "", ["No valid Groups setup receipt was returned."]);
    }

    internal static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != "http" || uri.Port is < 1024 or > 65535 ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              (IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address))))
            throw new ArgumentException("Groups speech must use a loopback HTTP endpoint on a port from 1024 to 65535.", nameof(endpoint));
        return uri;
    }

    internal static async Task<VibeVoiceHealth> ReadHealthAsync(HttpClient http, string endpoint, string expectedOwner,
        string expectedModelId, CancellationToken token)
    {
        ValidateOwner(expectedOwner); ValidateModel(expectedModelId);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await http.GetAsync(new Uri(ValidateEndpoint(endpoint), "health"), bounded.Token);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(bounded.Token));
        JsonElement root = document.RootElement;
        if (root.GetProperty("engine").GetString() != "vibevoice" || root.GetProperty("protocol").GetInt32() != 1 ||
            root.GetProperty("owner").GetString() != expectedOwner || root.GetProperty("model_id").GetString() != expectedModelId ||
            root.GetProperty("word_timestamps").GetBoolean())
            throw new InvalidOperationException("The selected endpoint did not return this app's owned Groups speech service.");
        double? Number(string key) => root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        return new(root.GetProperty("status").GetString() ?? "error", expectedOwner, expectedModelId,
            root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null,
            Number("chunk_ms"), Number("lookahead_ms"), root.GetProperty("active_sessions").GetInt32());
    }

    private static ProcessStartInfo CreateInfo(WslSpeechRegistration registration, string script, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!Regex.IsMatch(registration.Distribution, "^[A-Za-z0-9_.-]{1,64}$"))
            throw new ArgumentException("Choose a valid WSL speech distribution.", nameof(registration));
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--distribution"); info.ArgumentList.Add(registration.Distribution);
        if (registration.RunAsRoot) { info.ArgumentList.Add("--user"); info.ArgumentList.Add("root"); }
        info.ArgumentList.Add("--exec"); info.ArgumentList.Add("/bin/bash");
        info.ArgumentList.Add(LocalTalkProvisioner.WindowsPathToWsl(FindScript(script)));
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static string FindScript(string name)
    {
        // A fixed filename is supplied by our callers, never from user settings.
        string packaged = Path.Combine(AppContext.BaseDirectory, "provisioning", "vibevoice", name);
        if (File.Exists(packaged)) return packaged;
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string development = Path.Combine(directory.FullName, "Packaging", "vibevoice", name);
            if (File.Exists(development)) return development;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Groups speech tools are missing. Reinstall SayWhat? to restore them.", packaged);
    }

    private static string DataPath(string dataRoot)
    {
        string full = Path.GetFullPath(dataRoot);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Groups data cannot be stored at the root of a drive.", nameof(dataRoot));
        return LocalTalkProvisioner.WindowsPathToWsl(full);
    }
    private static string ValidateModel(string modelId) => IsSupportedModel(modelId) ? modelId :
        throw new ArgumentException("Choose a supported VibeVoice streaming model.", nameof(modelId));
    private static string PortArgument(int port) => port is >= 1024 and <= 65535
        ? port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(port), "Managed service ports must be between 1024 and 65535.");
    private static string ValidateOwner(string owner) => owner is not null && Regex.IsMatch(owner, "^[a-f0-9]{32}$") ? owner :
        throw new ArgumentException("An app-owned token is required to manage Groups speech.", nameof(owner));
}

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

internal sealed record ModelDownloadChoice(string Name, string ModelKey, string DownloadSpec, string Description)
{
    public bool RequiresReasoningSetup { get; init; }
    public long ExpectedSizeBytes { get; init; }
    public override string ToString() => Name;
}

internal sealed record InstalledLocalModel(string Key, string Name, long SizeBytes, bool IsLoaded)
{
    public string[] LoadedInstanceIds { get; init; } = Array.Empty<string>();
    public string Quantization { get; init; } = "";
    public override string ToString() => Name;
}

internal sealed record LmsCommandResult(int ExitCode, string Output);

internal static class LocalModelManager
{
    public const string LmsExecutablePath = @"C:\Program Files\Bionic\resources\app\.webpack-bionic\lms.exe";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly SemaphoreSlim CliGate = new(1, 1);
    private static readonly SemaphoreSlim ModelChangeGate = new(1, 1);
    private static readonly Regex AnsiCodes = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static IReadOnlyList<ModelDownloadChoice> Catalog { get; } = new[]
    {
        new ModelDownloadChoice(
            "Qwen3 14B Q4_K_M · 9 GB",
            "qwen/qwen3-14b",
            "qwen/qwen3-14b@q4_k_m",
            "Needs about 9 GB for weights plus working memory. " +
            "Share one copy for both directions. Loading it alongside an 8B model may leave too little GPU memory for VRChat.")
            { ExpectedSizeBytes = 9_000_000_000 },
        new ModelDownloadChoice(
            "Qwen3.5 9B Q4_K_M · 5.7 GB · experimental",
            "qwen/qwen3.5-9b",
            "qwen/qwen3.5-9b@q4_k_m",
            "Downloading does not select or load this experimental model. " +
            "Reasoning-off support must be verified; /no_think alone does not disable reasoning.")
            { ExpectedSizeBytes = 5_680_000_000, RequiresReasoningSetup = true }
    };

    public static async Task<IReadOnlyList<InstalledLocalModel>> ListInstalledAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        string root = ApiRoot(baseUrl);
        Exception? nativeApiFailure = null;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(root + "/api/v1/models", cancellationToken);
            response.EnsureSuccessStatusCode();
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var models = new List<InstalledLocalModel>();
            foreach (JsonElement model in document.RootElement.GetProperty("models").EnumerateArray())
            {
                if (GetString(model, "type") is string type && type != "llm") continue;
                string key = GetString(model, "key") ?? "";
                if (string.IsNullOrWhiteSpace(key)) continue;
                string[] instances = model.TryGetProperty("loaded_instances", out JsonElement loaded)
                    && loaded.ValueKind == JsonValueKind.Array
                    ? loaded.EnumerateArray().Select(item => GetString(item, "id"))
                        .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToArray()
                    : Array.Empty<string>();
                string quantization = model.TryGetProperty("quantization", out JsonElement quant)
                    && quant.ValueKind == JsonValueKind.Object ? GetString(quant, "name") ?? "" : "";
                long size = model.TryGetProperty("size_bytes", out JsonElement sizeValue)
                    && sizeValue.TryGetInt64(out long bytes) ? bytes : 0;
                models.Add(new InstalledLocalModel(key, GetString(model, "display_name") ?? key, size, instances.Length > 0)
                {
                    LoadedInstanceIds = instances,
                    Quantization = quantization
                });
            }
            return models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { nativeApiFailure = exception; }

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(root + "/v1/models", cancellationToken);
            response.EnsureSuccessStatusCode();
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var models = new List<InstalledLocalModel>();
            foreach (JsonElement item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                string id = GetString(item, "id") ?? "";
                if (string.IsNullOrWhiteSpace(id)) continue;
                // OpenAI's list does not guarantee residency; confirm it with this PC's CLI when loading.
                models.Add(new InstalledLocalModel(id, id, 0, false));
            }
            return models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Could not list local models. Check that Bionic's local API is running. " +
                exception.Message, nativeApiFailure);
        }
    }

    public static async Task DownloadAsync(
        ModelDownloadChoice model,
        Action<string> status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        await ModelChangeGate.WaitAsync(cancellationToken);
        try
        {
            Report(status, "Downloading " + model.Name + ". This does not change the selected translator.");
            await RunLmsAsync(new[] { "get", model.DownloadSpec, "--gguf", "-y" }, status, cancellationToken,
                TimeSpan.FromHours(1));
            Report(status, model.RequiresReasoningSetup
                ? "Download finished. This experimental model still needs reasoning-off compatibility before live use."
                : "Download finished. You can now select this model separately for each translation direction.");
        }
        finally { ModelChangeGate.Release(); }
    }

    public static async Task EnsureLoadedAsync(
        string modelKey,
        string baseUrl,
        Action<string> status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelKey)) throw new ArgumentException("Choose a translator model first.", nameof(modelKey));
        modelKey = modelKey.Trim();
        Uri endpoint = new(ApiRoot(baseUrl));
        if (!endpoint.IsLoopback)
            throw new InvalidOperationException("The local model loader can only manage this PC's Bionic API. Load models on other endpoints through that server.");
        await ModelChangeGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<InstalledLocalModel> installed = await ListInstalledAsync(baseUrl, cancellationToken);
            InstalledLocalModel? model = installed.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, modelKey, StringComparison.OrdinalIgnoreCase) ||
                candidate.LoadedInstanceIds.Contains(modelKey, StringComparer.OrdinalIgnoreCase));
            if (model?.IsLoaded == true)
            {
                Report(status, model.Name + " is already loaded; keeping the existing instance.");
                return;
            }

            // /v1/models can include on-demand, unloaded models. Confirm actual residency with lms ps.
            if (await IsLoadedByCliAsync(modelKey, cancellationToken))
            {
                Report(status, modelKey + " is already loaded; keeping the existing instance.");
                return;
            }
            if (Catalog.Any(choice => choice.RequiresReasoningSetup &&
                string.Equals(choice.ModelKey, modelKey.Split('@')[0], StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This experimental model needs verified reasoning-off setup before automatic loading. Choose Qwen3 8B or 14B for the current live pipeline.");

            string keyToLoad = model?.Key ?? modelKey;
            Report(status, "Loading " + keyToLoad + " with a short context and one inference slot…");
            await RunLmsAsync(new[]
            {
                "load", keyToLoad, "--gpu", "max", "--context-length", "4096", "--parallel", "1",
                "--ttl", "7200", "--identifier", modelKey, "-y"
            }, status, cancellationToken, TimeSpan.FromMinutes(5));
            if (!await IsLoadedByCliAsync(modelKey, cancellationToken))
                throw new InvalidOperationException("The loader finished, but the selected model did not appear as loaded. Check the local model log.");
            Report(status, modelKey + " is ready.");
        }
        finally { ModelChangeGate.Release(); }
    }

    public static async Task<LmsCommandResult> RunLmsAsync(
        IEnumerable<string> arguments,
        Action<string>? status = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (!File.Exists(LmsExecutablePath))
            throw new FileNotFoundException("Bionic's local model helper is missing. Check the Bionic installation.", LmsExecutablePath);
        await CliGate.WaitAsync(cancellationToken);
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCancellation.CancelAfter(timeout ?? TimeSpan.FromMinutes(10));
            var startInfo = new ProcessStartInfo(LmsExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(LmsExecutablePath)!
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            object outputGate = new();
            process.Start();
            process.StandardInput.Close();
            Task stdout = ReadOutputAsync(process.StandardOutput, output, outputGate, status, linkedCancellation.Token);
            Task stderr = ReadOutputAsync(process.StandardError, output, outputGate, status, linkedCancellation.Token);
            try
            {
                await process.WaitForExitAsync(linkedCancellation.Token);
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                StopOwnCli(process);
                try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("The local model command timed out. Its helper was stopped; the model server was kept running.");
            }
            string text;
            lock (outputGate) text = output.ToString();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The local model helper exited with code {process.ExitCode}. " + LastUsefulLine(text));
            return new LmsCommandResult(process.ExitCode, text);
        }
        finally { CliGate.Release(); }
    }

    private static async Task<bool> IsLoadedByCliAsync(string modelKey, CancellationToken cancellationToken)
    {
        LmsCommandResult result = await RunLmsAsync(new[] { "ps", "--json" }, cancellationToken: cancellationToken,
            timeout: TimeSpan.FromSeconds(20));
        using JsonDocument document = JsonDocument.Parse(result.Output.Trim());
        if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
        return document.RootElement.EnumerateArray().Any(model =>
            string.Equals(GetString(model, "identifier"), modelKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetString(model, "modelKey"), modelKey, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        StringBuilder output,
        object outputGate,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is string rawLine)
        {
            string line = AnsiCodes.Replace(rawLine, "").Replace('\0', ' ').Trim();
            if (line.Length == 0) continue;
            lock (outputGate)
            {
                output.AppendLine(line);
                if (output.Length > 65536) output.Remove(0, output.Length - 65536);
            }
            Report(status, line);
        }
    }

    private static void StopOwnCli(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
    }

    private static void Report(Action<string>? status, string message)
    {
        try { status?.Invoke(message); } catch { /* UI status callbacks must not interrupt a model operation. */ }
    }

    private static string ApiRoot(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
            !uri.IsLoopback || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Enter a valid local HTTP model endpoint.", nameof(baseUrl));
        string root = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^7];
        else if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
        return root;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string LastUsefulLine(string output)
        => output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? "No error details were returned.";
}

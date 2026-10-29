using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace IncomingTranslator;

internal enum ProvisionModelRole { Translation, Speech }

internal sealed record ProvisionModelChoice(string Id, string Name, string FileName, long SizeBytes,
    string Sha256, string DownloadUrl, string License, string LicenseUrl, string ApiAlias, ProvisionModelRole Role)
{
    public string Architecture { get; init; } = "qwen3";
    public string SourceRepositoryUrl { get; init; } = "";
    public string TermsNotice { get; init; } = "";
    public bool RequiresTermsAcceptance { get; init; }
    public override string ToString() => Name;
}

internal sealed record ProvisionAsset(string FileName, long SizeBytes, string Sha256, string DownloadUrl);
internal sealed record ProvisionRuntimeChoice(string Id, string Name, IReadOnlyList<ProvisionAsset> Assets)
{
    public long SizeBytes => Assets.Sum(asset => asset.SizeBytes);
    public override string ToString() => Name;
}

internal sealed record ProvisionProgress(string Stage, string Message, long BytesReceived = 0, long TotalBytes = 0)
{
    public string? Detail { get; init; }
    public int? StepIndex { get; init; }
    public int? StepCount { get; init; }
    // A fraction is for the current task only, never an invented overall setup estimate.
    public double? WorkFraction { get; init; }
    public double? Fraction => WorkFraction is double fraction && double.IsFinite(fraction)
        ? Math.Clamp(fraction, 0, 1) : TotalBytes > 0 ? Math.Clamp(BytesReceived / (double)TotalBytes, 0, 1) : null;
}
internal sealed record ProvisionInstalledModel(string Id, string Name, string Path, long SizeBytes, bool IsImported);
internal sealed record ProvisionInventory(IReadOnlyList<ProvisionInstalledModel> InstalledModels,
    IReadOnlyList<string> ExistingModelCandidates, IReadOnlyList<string> InstalledRuntimeIds,
    IReadOnlyList<string> WslDistributions);
internal sealed record WslSpeechRegistration(string Distribution, string ServerPath, string ModelPath, string Backend)
{
    // Only the app's isolated automatic-setup distribution uses root; never change an existing default user.
    public bool RunAsRoot { get; init; }
}

internal sealed class SpeechSetupException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

internal sealed class SetupCommandException(int exitCode, string command, string outputTail)
    : InvalidOperationException($"Setup helper exited with code {exitCode}.{Environment.NewLine}Command: {command}{Environment.NewLine}" +
        (string.IsNullOrWhiteSpace(outputTail) ? "The helper produced no diagnostic output." : outputTail))
{
    public int ExitCode { get; } = exitCode;
    public string Command { get; } = command;
    public string OutputTail { get; } = outputTail;
}

/// <summary>
/// Download/install only on explicit consent. Inspection and start-info construction never load
/// a model, open audio, start an engine, change another application's settings, or require Bionic.
/// Model/release versions and SHA-256 digests are pinned; inference itself is completely local.
/// </summary>
internal sealed class LocalTalkProvisioner : IDisposable
{
    public const string LlamaVersion = "b11401";
    public const string VoxtralCommit = "3d1ed513debc88b4a383ad83e0cd0bcb84889a60";
    public const string SpeechBuildIdentity = VoxtralCommit + "-localtalk-r2";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly object _preferencesGate = new();
    private ProvisionPreferences _preferences;
    public string DataRoot { get; }
    public string ModelsRoot => Path.Combine(DataRoot, "models");
    public string RuntimesRoot => Path.Combine(DataRoot, "runtimes");
    public WslSpeechRegistration? SpeechRegistration { get { lock (_preferencesGate) return _preferences.Speech; } }

    public static IReadOnlyList<ProvisionModelChoice> ModelCatalog { get; } = new[]
    {
        Model("qwen3-1.7b-q4_k_m", "Qwen3 1.7B • smallest / quality not benchmarked (1.3 GB)",
            "Qwen_Qwen3-1.7B-Q4_K_M.gguf", 1_282_439_584,
            "72c5c3cb38fa32d5256e2fe30d03e7a64c6c79e668ad84057e3bd66e250b24fb",
            "bartowski/Qwen_Qwen3-1.7B-GGUF", "dcb19155b962dbb6389f4691a982043a8e651022", "qwen/qwen3-1.7b"),
        Model("qwen3-4b-q4_k_m", "Qwen3 4B • smaller (2.5 GB)", "Qwen3-4B-Q4_K_M.gguf", 2_497_280_256,
            "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
            "Qwen/Qwen3-4B-GGUF", "bc640142c66e1fdd12af0bd68f40445458f3869b", "qwen/qwen3-4b"),
        Model("qwen3-8b-q4_k_m", "Qwen3 8B • balanced (5.0 GB)", "Qwen3-8B-Q4_K_M.gguf", 5_027_783_488,
            "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785",
            "Qwen/Qwen3-8B-GGUF", "7c41481f57cb95916b40956ab2f0b139b296d974", "qwen/qwen3-8b"),
        Model("qwen3-14b-q4_k_m", "Qwen3 14B • optional larger model (9.0 GB)", "Qwen3-14B-Q4_K_M.gguf", 9_001_752_960,
            "500a8806e85ee9c83f3ae08420295592451379b4f8cf2d0f41c15dffeb6b81f0",
            "Qwen/Qwen3-14B-GGUF", "530227a7d994db8eca5ab5ced2fb692b614357fd", "qwen/qwen3-14b"),
        new ProvisionModelChoice("voxtral-realtime-q4_k_m", "Voxtral Mini 4B Realtime 2602 • required speech model (2.9 GB)",
            "Voxtral-Mini-4B-Realtime-2602-Q4_K_M.gguf", 2_897_727_648,
            "90131ebe4882735c185dba0ed16ce90c3f59da32776ec85a50fca482fe72e137",
            "https://huggingface.co/andrijdavid/Voxtral-Mini-4B-Realtime-2602-GGUF/resolve/edbd87e314ba357e2b57e9e572da6238ba78eaeb/Q4_K_M.gguf",
            "Apache-2.0", "https://huggingface.co/mistralai/Voxtral-Mini-4B-Realtime-2602", "voxtral-realtime", ProvisionModelRole.Speech)
            { Architecture = "voxtral_realtime" },
        Model("hy-mt2-1.8b-q4_k_m", "Hy-MT2 1.8B • speed-first default / dedicated translation (1.1 GB)",
            "Hy-MT2-1.8B-Q4_K_M.gguf", 1_133_080_448,
            "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699",
            "tencent/Hy-MT2-1.8B-GGUF", "a0c709d9fac510f2c807aa3af52872340dc37a4a", "tencent/hy-mt2-1.8b")
            with { Architecture = "hunyuan-dense" },
        Model("hy-mt2-7b-q4_k_m", "Hy-MT2 7B • larger quality candidate / validate in-game delay (4.6 GB)",
            "Hy-MT2-7B-Q4_K_M.gguf", 4_624_648_896,
            "9f96256500f3fc1ab4d64336b58f52a949a95ad7516b0c229476eef782f9f77b",
            "tencent/Hy-MT2-7B-GGUF", "ab8472660ac61fac25f1af43fac2599d52a8a775", "tencent/hy-mt2-7b")
            with { Architecture = "hunyuan-dense" },
        new ProvisionModelChoice("translategemma-4b-q4_k_m", "TranslateGemma 4B • optional dedicated translation (2.5 GB)",
            "translategemma-4b-it-Q4_K_M.gguf", 2_489_909_312,
            "7f7357c14abd9da4eb200b38b05da502cd6e10d7e1d403fbc9f78c19f3209b72",
            "https://huggingface.co/bullerwins/translategemma-4b-it-GGUF/resolve/7c938465a870d8624bcfa98a8e4a3510053c19a8/translategemma-4b-it-Q4_K_M.gguf",
            "Gemma terms", "https://ai.google.dev/gemma/terms", "google/translategemma-4b-it", ProvisionModelRole.Translation)
            { Architecture = "gemma3", SourceRepositoryUrl = "https://huggingface.co/bullerwins/translategemma-4b-it-GGUF",
                RequiresTermsAcceptance = true,
                TermsNotice = "Google TranslateGemma weights, converted to GGUF by bullerwins (not an official Google GGUF). Gemma terms and use restrictions apply. Review and accept the model terms before downloading. Requires a chosen spoken language and SayWhat?'s built-in runtime; speed and quality in VRChat have not been benchmarked." },
        new ProvisionModelChoice("lfm2-350m-enjp-mt-q4_k_m", "LFM2 350M EN–JP • English/Japanese only (229 MB)",
            "LFM2-350M-ENJP-MT-Q4_K_M.gguf", 229_310_240,
            "574ef7980dd20d69b494bce82565db5f25124d404be02052bbca62277d33077b",
            "https://huggingface.co/LiquidAI/LFM2-350M-ENJP-MT-GGUF/resolve/889c88c3d3681b4f342ae210a1d2a1369aba0e10/LFM2-350M-ENJP-MT-Q4_K_M.gguf",
            "LFM Open License 1.0", "https://huggingface.co/LiquidAI/LFM2-350M-ENJP-MT-GGUF/blob/889c88c3d3681b4f342ae210a1d2a1369aba0e10/LICENSE",
            "liquidai/lfm2-350m-enjp-mt", ProvisionModelRole.Translation)
            { Architecture = "lfm2", SourceRepositoryUrl = "https://huggingface.co/LiquidAI/LFM2-350M-ENJP-MT-GGUF",
                RequiresTermsAcceptance = true,
                TermsNotice = "Official Liquid AI GGUF. English ↔ Japanese only; not a Chinese or Korean translator. LFM Open License 1.0 applies, including a $10M annual-revenue limit for commercial use. Review and accept the model terms before downloading. Speed and quality in VRChat have not been benchmarked." }
    };

    public static IReadOnlyList<ProvisionRuntimeChoice> RuntimeCatalog { get; } = new[]
    {
        new ProvisionRuntimeChoice("llama-vulkan", "Native Windows Vulkan • NVIDIA / AMD / Intel (33 MB)", new[]
        {
            Asset("llama-b11401-bin-win-vulkan-x64.zip", 33_308_209, "4bdbc0b2e79f04ef3e66ecd34a996499401d94501e8387dec0c8963544ef6419")
        }),
        new ProvisionRuntimeChoice("llama-cpu", "Native Windows CPU • testing / no GPU offload (19 MB)", new[]
        {
            Asset("llama-b11401-bin-win-cpu-x64.zip", 19_389_961, "4f3554e15fbadc217069e9774d2a6d528a138f5b4de7bc85e0f694a830c1ea1b")
        }),
        new ProvisionRuntimeChoice("llama-cuda", "Native Windows CUDA 13.4 • compatible NVIDIA driver required (577 MB)", new[]
        {
            Asset("llama-b11401-bin-win-cuda-13.4-x64.zip", 153_112_788, "2746413a038b5d997261933a10f7d73686c337df2319302dd2db639537e0c5c2"),
            Asset("cudart-llama-bin-win-cuda-13.4-x64.zip", 423_535_356, "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668")
        })
    };

    public LocalTalkProvisioner(string? dataRoot = null, HttpClient? http = null)
    {
        DataRoot = Path.GetFullPath(dataRoot ?? AppBrand.DataRoot);
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = http is null;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"SayWhat/{AppBrand.Version} (+https://github.com/ggml-org/llama.cpp)");
        _preferences = ReadPreferences();
    }

    public ProvisionInventory InspectLocal()
    {
        var models = new List<ProvisionInstalledModel>();
        foreach (ProvisionModelChoice choice in ModelCatalog)
        {
            string? path = ResolveModelPath(choice.Id);
            if (path is null) continue;
            models.Add(new(choice.Id, choice.Name, path, new FileInfo(path).Length,
                !string.Equals(Path.GetDirectoryName(path), ModelsRoot, StringComparison.OrdinalIgnoreCase)));
        }
        // Bounded search of the standard existing model folder, never the entire drive.
        var existing = new List<string>();
        string legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models");
        FindGguf(legacyRoot, depth: 4, existing);
        var distributions = new List<string>();
        // Registry inspection does not start WSL (unlike opening a \\wsl.localhost path).
        using RegistryKey? lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (lxss is not null)
            foreach (string key in lxss.GetSubKeyNames())
            {
                using RegistryKey? distribution = lxss.OpenSubKey(key);
                if (distribution?.GetValue("DistributionName") is string name) distributions.Add(name);
            }
        string[] runtimes = RuntimeCatalog.Where(runtime => FindRuntimeExecutable(runtime.Id) is not null)
            .Select(runtime => runtime.Id).ToArray();
        return new(models, existing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), runtimes, distributions);
    }

    public string? ResolveModelPath(string modelId)
    {
        ProvisionModelChoice model = FindModel(modelId);
        string? reference;
        lock (_preferencesGate) _preferences.ModelReferences.TryGetValue(modelId, out reference);
        if (!string.IsNullOrEmpty(reference) && File.Exists(reference)) return reference;
        string managedPath = Path.Combine(ModelsRoot, model.FileName);
        return File.Exists(managedPath) ? managedPath : null;
    }

    public async Task<string> DownloadModelAsync(string modelId, bool explicitConsent,
        IProgress<ProvisionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireConsent(explicitConsent, "Downloading a multi-gigabyte local model requires your confirmation.");
        ProvisionModelChoice model = FindModel(modelId);
        await _changes.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ModelsRoot);
            string path = Path.Combine(ModelsRoot, model.FileName);
            await DownloadVerifiedAsync(new(model.FileName, model.SizeBytes, model.Sha256, model.DownloadUrl), path, progress, cancellationToken);
            lock (_preferencesGate) _preferences.ModelReferences[modelId] = path;
            await SavePreferencesAsync(cancellationToken);
            return path;
        }
        finally { _changes.Release(); }
    }

    public async Task<string> RegisterExistingModelAsync(string modelId, string path, CancellationToken cancellationToken = default)
    {
        ProvisionModelChoice model = FindModel(modelId);
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Choose an existing GGUF model file.", path);
        // Alternative converters can legitimately have different GGUF bytes. Validate the
        // container and expected architecture without pretending an imported file is hash-pinned.
        await ValidateGgufArchitectureAsync(path, model, cancellationToken);
        await _changes.WaitAsync(cancellationToken);
        try
        {
            lock (_preferencesGate) _preferences.ModelReferences[modelId] = path;
            await SavePreferencesAsync(cancellationToken);
            return path;
        }
        finally { _changes.Release(); }
    }

    public async Task<string> InstallRuntimeAsync(string runtimeId, bool explicitConsent,
        IProgress<ProvisionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireConsent(explicitConsent, "Installing a local runtime requires your confirmation.");
        ProvisionRuntimeChoice runtime = RuntimeCatalog.FirstOrDefault(item => item.Id == runtimeId)
            ?? throw new ArgumentException("Choose a runtime from the verified catalog.", nameof(runtimeId));
        await _changes.WaitAsync(cancellationToken);
        string? staging = null;
        try
        {
            if (FindRuntimeExecutable(runtimeId) is string installed) return installed;
            Directory.CreateDirectory(RuntimesRoot);
            string downloads = Path.Combine(DataRoot, "downloads");
            Directory.CreateDirectory(downloads);
            staging = Path.Combine(RuntimesRoot, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            foreach (ProvisionAsset asset in runtime.Assets)
            {
                string archive = Path.Combine(downloads, asset.FileName);
                await DownloadVerifiedAsync(asset, archive, progress, cancellationToken);
                progress?.Report(new("Extracting", "Installing " + runtime.Name));
                ExtractVerifiedZip(archive, staging, cancellationToken);
            }
            if (!Directory.EnumerateFiles(staging, "llama-server.exe", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("The verified archive did not contain llama-server.exe.");
            await File.WriteAllTextAsync(Path.Combine(staging, "localtalk-runtime.json"),
                JsonSerializer.Serialize(new { runtime.Id, Version = LlamaVersion, Hashes = runtime.Assets.Select(asset => asset.Sha256) }), cancellationToken);
            string destination = RuntimeDirectory(runtimeId);
            if (Directory.Exists(destination)) throw new IOException("An incomplete runtime folder already exists. Choose another runtime or remove that specific incomplete folder before retrying: " + destination);
            Directory.Move(staging, destination);
            staging = null;
            return FindRuntimeExecutable(runtimeId) ?? throw new IOException("Runtime installation could not be confirmed.");
        }
        finally
        {
            if (staging is not null && IsWithin(staging, RuntimesRoot)) TryDeleteOwnedDirectory(staging);
            _changes.Release();
        }
    }

    public ProcessStartInfo BuildLlamaStartInfo(string runtimeId, string modelPath, string apiAlias, int port = 1235)
    {
        if (port is not 1235 and not 1236) throw new ArgumentOutOfRangeException(nameof(port), "Managed translators use ports 1235 and 1236.");
        string executable = FindRuntimeExecutable(runtimeId) ?? throw new FileNotFoundException("Install the selected local runtime first.");
        modelPath = Path.GetFullPath(modelPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The selected GGUF model is missing.", modelPath);
        if (string.IsNullOrWhiteSpace(apiAlias)) throw new ArgumentException("Enter a model API alias.", nameof(apiAlias));
        var info = NewProcess(executable);
        string[] args = { "--model", modelPath, "--alias", apiAlias, "--host", "127.0.0.1", "--port", port.ToString(),
            "--ctx-size", TranslationModelPrompts.IsTranslateGemma(apiAlias) ? "2048" : "4096", "--parallel", "1", "--batch-size", "128", "--ubatch-size", "64",
            "--threads", "4", "--threads-batch", "4", "--n-gpu-layers", runtimeId == "llama-cpu" ? "0" : "99",
            "--flash-attn", "auto", "--jinja", "--reasoning", "off" };
        foreach (string argument in args) info.ArgumentList.Add(argument);
        ProvisionModelChoice? profile = ModelCatalog.FirstOrDefault(choice => choice.ApiAlias == apiAlias);
        if (profile?.Architecture == "qwen3")
            foreach (string argument in new[] { "--reasoning-budget", "0", "--chat-template-kwargs", "{\"enable_thinking\":false}" })
                info.ArgumentList.Add(argument);
        if (TranslationModelPrompts.IsTranslateGemma(apiAlias))
        {
            // Explicit Start is the only caller that reaches this construction.
            // No weights are read/loaded here. Keep an app-owned role-only text
            // template because b11401 drops language fields from typed content;
            // the task adapter renders Google's exact translation text itself.
            string templateDirectory = Path.Combine(DataRoot, "templates");
            Directory.CreateDirectory(templateDirectory);
            string templatePath = Path.Combine(templateDirectory, "translategemma-text-v1.jinja");
            if (!File.Exists(templatePath) || File.ReadAllText(templatePath) != TranslationModelPrompts.TranslateGemmaTextTemplate)
                File.WriteAllText(templatePath, TranslationModelPrompts.TranslateGemmaTextTemplate, new UTF8Encoding(false));
            info.ArgumentList.Add("--chat-template-file"); info.ArgumentList.Add(templatePath);
        }
        return info; // RuntimeController owns explicit start, health, logs, and stop.
    }

    public async Task RegisterWslSpeechAsync(string distribution, string serverPath, string modelPath, string backend,
        CancellationToken cancellationToken = default, bool runAsRoot = false)
    {
        ValidateDistribution(distribution);
        ValidateLinuxPath(serverPath);
        ValidateLinuxPath(modelPath);
        ValidateBackend(backend);
        await VerifySpeechFileAsync(distribution, serverPath, executable: true, runAsRoot, cancellationToken);
        await VerifySpeechFileAsync(distribution, modelPath, executable: false, runAsRoot, cancellationToken);
        await _changes.WaitAsync(cancellationToken);
        try
        {
            lock (_preferencesGate) _preferences.Speech = new(distribution, serverPath, modelPath, backend) { RunAsRoot = runAsRoot };
            await SavePreferencesAsync(cancellationToken);
        }
        finally { _changes.Release(); }
    }

    private static async Task VerifySpeechFileAsync(string distribution, string path, bool executable, bool runAsRoot,
        CancellationToken token)
    {
        try
        {
            await RunCommandAsync(WithUser(WslCommand(distribution, "/usr/bin/test", executable ? "-x" : "-s", path), runAsRoot),
                null, token, TimeSpan.FromSeconds(20));
        }
        catch (SetupCommandException error)
        {
            string description = executable ? "program" : "model";
            string message = error.ExitCode == 1 && string.IsNullOrWhiteSpace(error.OutputTail)
                ? $"Speech recognition isn't ready: its {description} is missing or inaccessible. Open Troubleshooting for details, then retry setup."
                : $"Couldn't verify the speech recognition {description}. Open Troubleshooting for details, then retry setup.";
            throw new SpeechSetupException(message, error);
        }
    }

    public ProcessStartInfo BuildWslSpeechStartInfo(WslSpeechRegistration speech, int port, string ownerGuid)
    {
        ValidateDistribution(speech.Distribution);
        ValidateLinuxPath(speech.ServerPath);
        ValidateLinuxPath(speech.ModelPath);
        ValidateBackend(speech.Backend);
        ValidateOwner(ownerGuid);
        if (port is not 8080 and not 8081) throw new ArgumentOutOfRangeException(nameof(port));
        var info = WithUser(WslCommand(speech.Distribution, "/bin/bash", WindowsPathToWsl(BundledScript("run-voxtral.sh")),
            speech.ServerPath, speech.ModelPath, speech.Backend, port.ToString(), ownerGuid), speech.RunAsRoot);
        // Migration occurs only when the user has already requested speech to
        // start. Idle app startup never opens WSL or loads an inference model.
        if (IsManagedSpeechRegistration(speech)) info.ArgumentList.Add("--hide-desktop-integration");
        return info;
    }

    internal static bool IsManagedSpeechRegistration(WslSpeechRegistration? speech) => speech is
        { RunAsRoot: true } && AppBrand.IsOwnedSpeechDistributionName(speech.Distribution) &&
        Regex.IsMatch(speech.ServerPath, "^/opt/localtalk-voxtral/[a-f0-9]{40}-localtalk-r[0-9]+/build-(cuda|vulkan|cpu)/voxtral-server$");

    public ProcessStartInfo BuildWslSpeechStopInfo(WslSpeechRegistration speech, int port, string ownerGuid)
    {
        ValidateDistribution(speech.Distribution);
        ValidateLinuxPath(speech.ServerPath);
        ValidateOwner(ownerGuid);
        if (port is not 8080 and not 8081) throw new ArgumentOutOfRangeException(nameof(port));
        return WithUser(WslCommand(speech.Distribution, "/bin/bash", WindowsPathToWsl(BundledScript("stop-voxtral.sh")),
            speech.ServerPath, port.ToString(), ownerGuid), speech.RunAsRoot);
    }

    public async Task<WslSpeechRegistration> BootstrapWslSpeechAsync(string distribution, string backend,
        bool explicitConsent, IProgress<ProvisionProgress>? progress = null, CancellationToken cancellationToken = default,
        bool runAsRoot = false, bool hideDesktopIntegration = false)
    {
        RequireConsent(explicitConsent, "WSL speech setup can install Linux build packages and a multi-GB CUDA toolkit. Confirm before continuing.");
        ValidateDistribution(distribution);
        ValidateBackend(backend);
        string model = ResolveModelPath("voxtral-realtime-q4_k_m") ??
            throw new InvalidOperationException("Download or import the realtime Voxtral GGUF first (separate 2.9 GB confirmation).");
        string script = WindowsPathToWsl(BundledScript("bootstrap-voxtral-wsl.sh"));
        string jobId = Guid.NewGuid().ToString("N");
        var command = BuildWslSpeechBootstrapInfo(distribution, backend, script, jobId);
        if (hideDesktopIntegration)
        {
            if (!AppBrand.IsOwnedSpeechDistributionName(distribution)) throw new InvalidOperationException("Desktop integration changes require an app-owned speech component.");
            command.ArgumentList.Add("--hide-desktop-integration");
        }
        string server = $"/opt/localtalk-voxtral/{SpeechBuildIdentity}/build-{backend}/voxtral-server";
        progress?.Report(new("Building speech runtime", "Installing signed distribution packages and building pinned Voxtral with one CPU job. No model is loaded."));
        try
        {
            await RunCommandAsync(command, progress, cancellationToken, TimeSpan.FromHours(2),
                requiredOutputLine: "LOCALTALK_VOXTRAL_SERVER=" + server);
        }
        catch (Exception error)
        {
            // Any failure targets the job-specific process group, never the WSL distro,
            // the GPU driver, another shell, or another translation engine.
            var stop = WslCommand(distribution, "/bin/bash", WindowsPathToWsl(BundledScript("cancel-bootstrap.sh")), jobId);
            stop.ArgumentList.Insert(2, "--user");
            stop.ArgumentList.Insert(3, "root");
            try { await RunCommandAsync(stop, null, CancellationToken.None, TimeSpan.FromSeconds(15)); } catch { }
            if (error is SetupCommandException)
                throw new SpeechSetupException("Speech recognition setup failed. Open Troubleshooting to see the build error, then choose Continue setup.", error);
            throw;
        }
        string linuxModel = WindowsPathToWsl(model);
        await RegisterWslSpeechAsync(distribution, server, linuxModel, backend, cancellationToken, runAsRoot);
        return SpeechRegistration!;
    }

    internal static ProcessStartInfo BuildWslSpeechBootstrapInfo(string distribution, string backend, string linuxScript, string jobId)
    {
        ValidateDistribution(distribution);
        ValidateBackend(backend);
        ValidateLinuxPath(linuxScript);
        ValidateOwner(jobId);
        // WSL can make setsid a process-group leader. Without --wait it then forks,
        // reports success early, and loses the actual build's output/exit status.
        // Keep the job-specific session for cancellation, but supervise its child.
        return WithUser(WslCommand(distribution, "/usr/bin/setsid", "--wait", "/bin/bash", linuxScript, backend, jobId), root: true);
    }

    // The Windows feature installer may require elevation/reboot. No automatic reboot,
    // no distro launch, no engine launch. The caller must present a separate consent page.
    public static ProcessStartInfo BuildWslFeatureInstallInfo(bool explicitConsent)
    {
        RequireConsent(explicitConsent, "Installing WSL/Ubuntu requires explicit consent and may require a restart.");
        var command = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        { UseShellExecute = true, Verb = "runas", Arguments = "--install --distribution Ubuntu-24.04 --no-launch" };
        return command;
    }

    // Read metadata only: never load a DLL or start a native engine to probe it.
    public static string? CheckNativeRuntimePrerequisite()
    {
        foreach (string name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
            try
            {
                if (!File.Exists(path) || FileVersionInfo.GetVersionInfo(path).FileMajorPart < 14)
                    return "Microsoft Visual C++ x64 runtime is missing or outdated. Run the SayWhat? installer and select the Microsoft runtime setup task (internet/admin consent may be required), then try again. No Visual Studio or LM Studio installation is needed.";
            }
            catch (IOException) { return "Microsoft Visual C++ x64 runtime could not be checked. Rerun SayWhat? setup and choose the Microsoft runtime task before starting translation."; }
            catch (UnauthorizedAccessException) { return "Microsoft Visual C++ x64 runtime could not be checked. Rerun SayWhat? setup and choose the Microsoft runtime task before starting translation."; }
        }
        return null;
    }

    internal async Task DownloadVerifiedAsync(ProvisionAsset asset, string destination,
        IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        if (asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("A pinned SHA-256 digest is required.");
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https")
            throw new InvalidDataException("Verified downloads must use HTTPS.");
        if (File.Exists(destination))
        {
            progress?.Report(new("Verifying", "Checking the existing file without downloading it again."));
            if (new FileInfo(destination).Length == asset.SizeBytes && await VerifyDigestAsync(destination, asset.Sha256, token)) return;
            throw new InvalidDataException("The existing file does not match this catalog item. It was kept unchanged; import it separately or choose another location: " + destination);
        }
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        long needed = asset.SizeBytes + 64L * 1024 * 1024;
        var disk = new DriveInfo(Path.GetPathRoot(destination)!);
        if (disk.AvailableFreeSpace < needed) throw new IOException($"At least {needed / 1_000_000_000.0:0.0} GB of free disk space is required.");
        string partial = destination + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size != asset.SizeBytes)
                throw new InvalidDataException("The download size differs from the pinned catalog.");
            await using Stream source = await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[1024 * 1024];
                long received = 0;
                DateTimeOffset lastReport = DateTimeOffset.MinValue;
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(), token);
                    if (read == 0) break;
                    received += read;
                    if (received > asset.SizeBytes) throw new InvalidDataException("The download exceeded the pinned size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    if (DateTimeOffset.UtcNow - lastReport >= TimeSpan.FromMilliseconds(150))
                    {
                        progress?.Report(new("Downloading", asset.FileName, received, asset.SizeBytes));
                        lastReport = DateTimeOffset.UtcNow;
                    }
                }
                if (received != asset.SizeBytes || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Download verification failed. The incomplete file will not be installed.");
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: false);
            progress?.Report(new("Verified", asset.FileName, asset.SizeBytes, asset.SizeBytes));
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    internal static async Task<bool> VerifyDigestAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, token);
        return string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ExtractVerifiedZip(string archive, string target, CancellationToken token)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        long expanded = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!IsWithin(path, target) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe archive path or symbolic link rejected.");
            expanded += entry.Length;
            if (expanded > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Runtime archive exceeded its safe extraction limit.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // CUDA's companion ZIP can intentionally contain matching runtime files.
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static Task ValidateGgufArchitectureAsync(string path, ProvisionModelChoice model, CancellationToken token) =>
        Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            try
            {
                if (reader.ReadUInt32() != 0x46554747 || reader.ReadUInt32() is not 2 and not 3)
                    throw new InvalidDataException("The selected file is not a supported GGUF v2/v3 model.");
                ulong tensors = reader.ReadUInt64(), metadataCount = reader.ReadUInt64();
                if (tensors == 0 || metadataCount > 100_000) throw new InvalidDataException("The GGUF header is not a model header.");
                for (ulong index = 0; index < metadataCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (stream.Position > 256L * 1024 * 1024) throw new InvalidDataException("GGUF metadata exceeded the import safety limit.");
                    string key = ReadGgufString(reader);
                    uint type = reader.ReadUInt32();
                    if (key == "general.architecture")
                    {
                        if (type != 8) throw new InvalidDataException("GGUF architecture must be a string.");
                        string architecture = ReadGgufString(reader);
                        if (architecture != model.Architecture)
                            throw new InvalidDataException(model.Role == ProvisionModelRole.Speech
                                ? "Choose the Voxtral Realtime GGUF, not the offline Mini model."
                                : "This profile requires a " + model.Architecture + " GGUF, not " + architecture + ".");
                        return;
                    }
                    SkipGgufValue(reader, type, token);
                }
                throw new InvalidDataException("The model has no general.architecture metadata.");
            }
            catch (EndOfStreamException exception) { throw new InvalidDataException("The GGUF header is truncated.", exception); }
        }, token);

    private static string ReadGgufString(BinaryReader reader)
    {
        ulong length = reader.ReadUInt64();
        if (length > 16 * 1024 * 1024 || length > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
            throw new InvalidDataException("Invalid GGUF metadata string length.");
        byte[] bytes = reader.ReadBytes((int)length);
        if (bytes.Length != (int)length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }
    private static void SkipGgufValue(BinaryReader reader, uint type, CancellationToken token, int depth = 0)
    {
        if (depth > 2) throw new InvalidDataException("Nested GGUF metadata arrays exceeded the safety limit.");
        long bytes = type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
        if (bytes > 0) { SkipBytes(bytes); return; }
        if (type == 8)
        {
            ulong length = reader.ReadUInt64();
            if (length > 16 * 1024 * 1024) throw new InvalidDataException("GGUF string exceeded its safety limit.");
            SkipBytes((long)length);
            return;
        }
        if (type != 9) throw new InvalidDataException("Unsupported GGUF metadata type.");
        uint elementType = reader.ReadUInt32();
        ulong count = reader.ReadUInt64();
        if (count > 2_000_000) throw new InvalidDataException("GGUF array exceeded its safety limit.");
        for (ulong index = 0; index < count; index++)
        { token.ThrowIfCancellationRequested(); SkipGgufValue(reader, elementType, token, depth + 1); }
        void SkipBytes(long countBytes)
        {
            if (countBytes < 0 || countBytes > reader.BaseStream.Length - reader.BaseStream.Position) throw new EndOfStreamException();
            reader.BaseStream.Seek(countBytes, SeekOrigin.Current);
        }
    }

    private string? FindRuntimeExecutable(string id)
    {
        // The installer includes the verified Vulkan runtime in its read-only app
        // directory. Explicit optional runtime installations remain per-user.
        return FindPinnedRuntimeExecutable(RuntimeDirectory(id), id) ??
            FindPinnedRuntimeExecutable(Path.Combine(AppContext.BaseDirectory, "runtimes", id), id);
    }
    internal static string? FindPinnedRuntimeExecutable(string directory, string id)
    {
        ProvisionRuntimeChoice runtime = RuntimeCatalog.FirstOrDefault(item => item.Id == id)
            ?? throw new ArgumentException("Unknown managed runtime.", nameof(id));
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "localtalk-runtime.json")));
            JsonElement root = manifest.RootElement;
            if (root.GetProperty("Id").GetString() != id || root.GetProperty("Version").GetString() != LlamaVersion ||
                !root.GetProperty("Hashes").EnumerateArray().Select(value => value.GetString())
                    .SequenceEqual(runtime.Assets.Select(asset => asset.Sha256), StringComparer.OrdinalIgnoreCase)) return null;
            return Directory.EnumerateFiles(directory, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    private string RuntimeDirectory(string id)
    {
        if (!RuntimeCatalog.Any(runtime => runtime.Id == id)) throw new ArgumentException("Unknown managed runtime.", nameof(id));
        return Path.Combine(RuntimesRoot, id + "-" + LlamaVersion);
    }
    private ProvisionPreferences ReadPreferences()
    {
        try { return JsonSerializer.Deserialize<ProvisionPreferences>(File.ReadAllText(Path.Combine(DataRoot, "provisioning.json"))) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }
    private async Task SavePreferencesAsync(CancellationToken token)
    {
        Directory.CreateDirectory(DataRoot);
        string path = Path.Combine(DataRoot, "provisioning.json");
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        byte[] json;
        lock (_preferencesGate) json = JsonSerializer.SerializeToUtf8Bytes(_preferences, new JsonSerializerOptions { WriteIndented = true });
        try { await File.WriteAllBytesAsync(temporary, json, token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void FindGguf(string root, int depth, List<string> paths)
    {
        if (depth < 0 || paths.Count >= 100) return;
        try
        {
            if (!Directory.Exists(root)) return;
            paths.AddRange(Directory.EnumerateFiles(root, "*.gguf").Take(100 - paths.Count));
            foreach (string directory in Directory.EnumerateDirectories(root).Take(100))
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) FindGguf(directory, depth - 1, paths);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static ProvisionModelChoice Model(string id, string name, string file, long bytes, string sha, string repo, string revision, string alias) =>
        new(id, name, file, bytes, sha, $"https://huggingface.co/{repo}/resolve/{revision}/{file}", "Apache-2.0",
            $"https://huggingface.co/{repo}", alias, ProvisionModelRole.Translation);
    private static ProvisionAsset Asset(string file, long bytes, string sha) =>
        new(file, bytes, sha, $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaVersion}/{file}");
    private static ProvisionModelChoice FindModel(string id) => ModelCatalog.FirstOrDefault(model => model.Id == id)
        ?? throw new ArgumentException("Choose a model from the verified catalog.", nameof(id));
    private static void RequireConsent(bool consent, string message) { if (!consent) throw new InvalidOperationException(message); }
    internal static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);
    private static void TryDeleteOwnedDirectory(string directory) { try { Directory.Delete(directory, true); } catch (IOException) { } }
    private static ProcessStartInfo NewProcess(string executable) => new(executable)
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    private static ProcessStartInfo WslCommand(string distribution, params string[] arguments)
    {
        var info = NewProcess(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"));
        foreach (string argument in new[] { "--distribution", distribution, "--exec" }.Concat(arguments)) info.ArgumentList.Add(argument);
        return info;
    }
    private static ProcessStartInfo WithUser(ProcessStartInfo info, bool root)
    {
        if (root) { info.ArgumentList.Insert(2, "--user"); info.ArgumentList.Insert(3, "root"); }
        return info;
    }
    private static string BundledScript(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "provisioning", name);
        if (!File.Exists(path)) throw new FileNotFoundException("A setup script is missing from this installation. Reinstall the app.", path);
        return path;
    }
    internal static string WindowsPathToWsl(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Length < 3 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
            throw new ArgumentException("Choose a model on a local Windows drive; network/UNC model paths are not supported by the WSL setup.", nameof(path));
        return "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
    }
    private static void ValidateDistribution(string distribution)
    { if (!Regex.IsMatch(distribution, "^[A-Za-z0-9_.-]{1,64}$")) throw new ArgumentException("Choose a valid WSL distribution name."); }
    private static void ValidateLinuxPath(string path)
    { if (!path.StartsWith('/') || path.Contains('\0') || path.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("Choose an absolute Linux path without parent-directory traversal."); }
    private static void ValidateBackend(string backend)
    { if (backend is not "cuda" and not "vulkan" and not "cpu") throw new ArgumentException("Choose cuda, vulkan or cpu."); }
    private static void ValidateOwner(string owner)
    { if (!Regex.IsMatch(owner, "^[a-f0-9]{32}$")) throw new ArgumentException("An app-owned GUID token is required to manage a speech process."); }
    internal static async Task RunCommandAsync(ProcessStartInfo info, IProgress<ProvisionProgress>? progress, CancellationToken token, TimeSpan timeout,
        string? requiredOutputLine = null)
    {
        token.ThrowIfCancellationRequested();
        if (requiredOutputLine is not null && string.IsNullOrWhiteSpace(requiredOutputLine))
            throw new ArgumentException("A required completion line cannot be empty.", nameof(requiredOutputLine));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(timeout);
        using Process process = Process.Start(info) ?? throw new IOException("The setup helper could not start.");
        var lines = new Queue<string>();
        object gate = new();
        bool requiredOutputFound = requiredOutputLine is null;
        async Task Drain(StreamReader reader, bool standardOutput)
        {
            while (await reader.ReadLineAsync(linked.Token) is string line)
            {
                lock (gate)
                {
                    if (standardOutput && string.Equals(line, requiredOutputLine, StringComparison.Ordinal)) requiredOutputFound = true;
                    lines.Enqueue(line); while (lines.Count > 20) lines.Dequeue();
                }
                progress?.Report(new("Setup", line));
            }
        }
        Task stdout = Drain(process.StandardOutput, standardOutput: true), stderr = Drain(process.StandardError, standardOutput: false);
        try
        {
            // Observe reader/progress failures immediately, not after a long-running
            // setup helper exits. A failing callback must never leave that helper alive.
            var pending = new List<Task> { process.WaitForExitAsync(linked.Token), stdout, stderr };
            while (pending.Count > 0)
            {
                Task finished = await Task.WhenAny(pending);
                await finished;
                pending.Remove(finished);
            }
            lock (gate)
            {
                string command = info.FileName + " " + (info.ArgumentList.Count > 0
                    ? string.Join(" ", info.ArgumentList.Select(argument => JsonSerializer.Serialize(argument))) : info.Arguments);
                string outputTail = string.Join(Environment.NewLine, lines);
                if (process.ExitCode != 0) throw new SetupCommandException(process.ExitCode, command, outputTail);
                if (!requiredOutputFound)
                    throw new SpeechSetupException("Speech setup ended before it confirmed completion. Open Troubleshooting for details, then retry setup.",
                        new InvalidDataException($"Helper reported success without the required completion line: {requiredOutputLine}{Environment.NewLine}Command: {command}{Environment.NewLine}{outputTail}"));
            }
        }
        catch (OperationCanceledException)
        {
            // A progress callback can also throw cancellation independently of the
            // supplied token. Cancel both readers before awaiting their completion.
            linked.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Setup timed out. No model was started; review the setup log before retrying.");
        }
        catch
        {
            linked.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            throw;
        }
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); _changes.Dispose(); }
    private sealed class ProvisionPreferences
    {
        public Dictionary<string, string> ModelReferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public WslSpeechRegistration? Speech { get; set; }
    }
}

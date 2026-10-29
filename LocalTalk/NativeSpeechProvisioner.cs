using System.Diagnostics;
using System.Text.Json;

namespace IncomingTranslator;

// Preparation downloads verified files only. It never runs inference, captures
// audio, installs system software or changes another application's environment.
internal sealed class NativeSpeechProvisioner(string dataRoot)
{
    internal const string RuntimeVersion = "0.2.0";
    internal const string ModelRevision = "ea30d66debe3740a08b573244286791d423d6b3e";
    internal const string ModelFile = "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf";
    internal static readonly ProvisionAsset RuntimeAsset = new(
        "nemo-speech-0.2.0-windows-x86_64-vulkan.zip", 19_302_464,
        "edf15a04ba98740aa0ab75ae4681ac7ad0a488b22b35ce2cbebd10590bb1c1e1",
        "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.2.0/nemo-speech-0.2.0-windows-x86_64-vulkan.zip");
    internal static readonly ProvisionAsset ModelAsset = new(ModelFile, 742_090_464,
        "3fc991d3badad7277c11030a7519832cddaf2057aafed6d4b25147e953a070b1",
        "https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/resolve/" + ModelRevision + "/" + ModelFile);
    private static readonly SemaphoreSlim PreparationGate = new(1, 1);
    private readonly string _root = Path.GetFullPath(dataRoot);
    internal string RuntimeRoot => Path.Combine(_root, "runtimes", "nemo-speech-" + RuntimeVersion + "-vulkan");
    internal string ModelPath => Path.Combine(_root, "models", "own-speech", ModelFile);
    private string ReceiptPath => Path.Combine(RuntimeRoot, "saywhat-verified.json");

    internal string? ExecutablePath => Directory.Exists(RuntimeRoot)
        ? Directory.EnumerateFiles(RuntimeRoot, "nemo-speech.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

    private bool RuntimeFilesComplete
    {
        get
        {
            string? executable = ExecutablePath;
            if (executable is null) return false;
            string directory = Path.GetDirectoryName(executable)!;
            return new[] { "ggml.dll", "ggml-base.dll", "ggml-cpu.dll", "ggml-vulkan.dll", "nemo_speech_asr.dll", "nemo_speech_asr_c.dll" }
                .All(name => File.Exists(Path.Combine(directory, name)));
        }
    }

    public bool IsReady(AppSettings settings)
    {
        if (settings.OwnRecognitionEngine == "voxtral") return false;
        try
        {
            if (!File.Exists(ReceiptPath) || !File.Exists(ModelPath) || new FileInfo(ModelPath).Length != ModelAsset.SizeBytes || !RuntimeFilesComplete)
                return false;
            using var receipt = JsonDocument.Parse(File.ReadAllText(ReceiptPath));
            return receipt.RootElement.GetProperty("runtimeSha256").GetString() == RuntimeAsset.Sha256 &&
                receipt.RootElement.GetProperty("modelSha256").GetString() == ModelAsset.Sha256;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
        { return false; }
    }

    public long PendingDownloadBytes(AppSettings settings)
    {
        if (settings.OwnRecognitionEngine == "voxtral" || IsReady(settings)) return 0;
        long bytes = File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelAsset.SizeBytes ? 0 : ModelAsset.SizeBytes;
        if (!RuntimeFilesComplete || !File.Exists(ReceiptPath)) bytes += RuntimeAsset.SizeBytes;
        return bytes;
    }

    public async Task PrepareAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        if (settings.OwnRecognitionEngine == "voxtral") throw new SetupUserException("Choose native recognition before preparing its files.");
        if (!OwnRecognitionCatalog.Find(settings.OwnRecognitionEngine).SupportsSource(settings.OwnVoiceSourceLanguage))
            throw new SetupUserException("Choose a supported spoken language.");
        await PreparationGate.WaitAsync(token);
        try
        {
            if (IsReady(settings)) return;
            using var downloads = new LocalTalkProvisioner(_root);
            string archive = Path.Combine(_root, "downloads", RuntimeAsset.FileName);
            progress?.Report(new("Preparing speech recognition", "Downloading the Windows speech tools. No model is started."));
            await downloads.DownloadVerifiedAsync(RuntimeAsset, archive, progress, token);
            // Re-extract an unverified preparation even if its executable exists:
            // a cancelled extraction may have left the executable but no DLLs.
            Directory.CreateDirectory(RuntimeRoot);
            await Task.Run(() => LocalTalkProvisioner.ExtractVerifiedZip(archive, RuntimeRoot, token), token);
            if (ExecutablePath is null) throw new InvalidDataException("The verified speech archive did not contain its Windows executable.");
            progress?.Report(new("Preparing speech recognition", "Downloading the selected speech model."));
            await downloads.DownloadVerifiedAsync(ModelAsset, ModelPath, progress, token);
            token.ThrowIfCancellationRequested();
            string temporary = ReceiptPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
                { runtimeSha256 = RuntimeAsset.Sha256, modelSha256 = ModelAsset.Sha256, modelRevision = ModelRevision }), token);
                File.Move(temporary, ReceiptPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { PreparationGate.Release(); }
    }

    internal ProcessStartInfo BuildStartInfo(AppSettings settings, int port)
    {
        if (port is < 1024 or > 65535 || !IsReady(settings))
            throw new ModelSelectionUserException("Prepare the selected speech model on Home before starting.");
        var info = new ProcessStartInfo(ExecutablePath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(ExecutablePath!)!
        };
        foreach (string argument in ServerArguments(ModelPath, port))
            info.ArgumentList.Add(argument);
        // Explicit local paths avoid the runtime's optional first-use model pulls.
        info.Environment["NEMO_SPEECH_MODEL_DIR"] = Path.Combine(_root, "models", "own-speech");
        return info;
    }

    // Pure argument construction is checked without preparing files or starting
    // an engine. Native boolean overrides require '='; a separate 'false' token
    // is treated as an unexpected positional argument by the pinned CLI.
    internal static string[] ServerArguments(string modelPath, int port) =>
        new[] { "serve", "--asr-model", modelPath, "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--asr.enabled=true", "--tts.enabled=false", "--nmt.enabled=false", "--asr.endpointing.enable=false",
            "--asr.batching.enabled=false", "--asr.streaming.rnnt_right_context=3", "--asr.backend.threads=2", "--device", "vulkan:0", "--no-ui", "--threads", "2" };
}

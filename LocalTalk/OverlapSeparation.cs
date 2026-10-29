using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IncomingTranslator;

// Speaker embeddings recognize a voice. This separate model actually estimates two waveforms.
// Its output slots are not stable identities and must never be called Speaker 1/2 without matching.
internal sealed record OverlapModelChoice(
    string Id, string Name, string Description, string FileName, Uri DownloadUri,
    string Sha256, long ExpectedSizeBytes, string Revision, string SourceUrl, string License)
{
    public override string ToString() => Name;
}

internal static class OverlapModelCatalog
{
    public static readonly OverlapModelChoice MossFormer2Int8 = new(
        "mossformer2-int8-16k",
        "MossFormer2 — experimental two-voice separator (91 MB, CPU)",
        "Estimates two separate voices from one mixed recording. This is a noncausal, blind separator, " +
        "not a speaker-fingerprint model. Mandarin/VRChat accuracy and live delay have not been validated. " +
        "Downloading or evaluating it does not enable live separation or load anything onto the GPU.",
        "mossformer2-ss-16k.int8.onnx",
        new Uri("https://huggingface.co/ipsilondev/MossFormer2-SS-16K-ONNX/resolve/abd8b1268dfded6b0e972ddae4c2dd6a3faf4f8a/model.int8.onnx"),
        "1776c854784a2400feb7582ac4e9f922073cd706cb4fd216c98abad937d642b1",
        90_943_046,
        "abd8b1268dfded6b0e972ddae4c2dd6a3faf4f8a",
        "https://huggingface.co/ipsilondev/MossFormer2-SS-16K-ONNX",
        "Apache-2.0 (upstream model and ONNX export)");

    public static IReadOnlyList<OverlapModelChoice> Models { get; } = new[] { MossFormer2Int8 };
    // Deliberately false: evaluating separation alone does not prove two-stream ASR or gaming performance.
    public const bool LivePipelineImplemented = false;
    public const string LiveUnavailableReason =
        "Live overlapping-voice translation is not enabled yet: separated audio still needs a validated " +
        "two-stream recognizer, stable fingerprint matching, and an in-game performance test. " +
        "You can explicitly download the real separator and evaluate a local 16 kHz WAV sample.";
}

internal sealed record OverlapDownloadProgress(long BytesReceived, long TotalBytes, string Message)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : 0;
}

internal static class OverlapModelStore
{
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public static string GetModelPath(string appDataRoot, OverlapModelChoice? model = null)
    {
        model ??= OverlapModelCatalog.MossFormer2Int8;
        ValidateCatalogChoice(model);
        return Path.Combine(Path.GetFullPath(appDataRoot), "models", "separation", model.FileName);
    }

    public static async Task VerifyAsync(string path, OverlapModelChoice model, CancellationToken cancellationToken = default)
    {
        ValidateCatalogChoice(model);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Download the selected speech-separation model first.", path);
        if (info.Length != model.ExpectedSizeBytes)
            throw new InvalidDataException("The separation model has the wrong size. It will not be loaded.");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!Convert.ToHexString(hash).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The separation model failed its pinned SHA-256 check. It will not be loaded.");
    }

    // Only an explicit UI request calls this. No audio ever leaves the computer.
    public static async Task<string> DownloadAsync(
        string appDataRoot, OverlapModelChoice model, Action<OverlapDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCatalogChoice(model);
        string target = GetModelPath(appDataRoot, model);
        await DownloadGate.WaitAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken downloadToken = linked.Token;
        string? temporary = null;
        try
        {
            if (File.Exists(target))
            {
                await VerifyAsync(target, model, downloadToken);
                progress?.Invoke(new(model.ExpectedSizeBytes, model.ExpectedSizeBytes, "Verified existing model. Nothing was loaded."));
                return target;
            }
            string directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, model.FileName + ".partial-" + Guid.NewGuid().ToString("N"));
            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(15) };
            using HttpResponseMessage response = await http.GetAsync(model.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead, downloadToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != model.ExpectedSizeBytes)
                throw new InvalidDataException("The download size does not match the pinned model catalogue.");
            await using (Stream source = await response.Content.ReadAsStreamAsync(downloadToken))
            await using (FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072];
                long received = 0;
                var lastProgress = Stopwatch.StartNew();
                int count;
                while ((count = await source.ReadAsync(buffer, downloadToken)) != 0)
                {
                    received += count;
                    if (received > model.ExpectedSizeBytes) throw new InvalidDataException("Model download exceeded its expected size.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), downloadToken);
                    if (lastProgress.ElapsedMilliseconds >= 200)
                    {
                        progress?.Invoke(new(received, model.ExpectedSizeBytes, "Downloading optional model; live translation remains unchanged."));
                        lastProgress.Restart();
                    }
                }
                await destination.FlushAsync(downloadToken);
            }
            await VerifyAsync(temporary, model, downloadToken);
            // Never replace an existing file. A concurrent/external installation is verified instead.
            if (File.Exists(target)) await VerifyAsync(target, model, downloadToken);
            else File.Move(temporary, target);
            progress?.Invoke(new(model.ExpectedSizeBytes, model.ExpectedSizeBytes, "Downloaded and verified. Model is not loaded; live separation remains off."));
            return target;
        }
        finally
        {
            // This is the unique partial file created by this operation, not a model or user file.
            try
            {
                if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            }
            finally { DownloadGate.Release(); }
        }
    }

    private static void ValidateCatalogChoice(OverlapModelChoice model)
    {
        if (!OverlapModelCatalog.Models.Contains(model))
            throw new ArgumentException("Only a pinned separation-model catalogue entry can be downloaded or verified.", nameof(model));
    }
}

internal sealed record SeparatedAudio(
    float[] Source0, float[] Source1, int SampleRate, TimeSpan ProcessingTime,
    double RealTimeFactor, double InterSourceCorrelation, double CommonGain, string Diagnostic)
{
    public double AudioSeconds => (double)Source0.Length / SampleRate;
    public bool OutputsNearlyIdentical => Math.Abs(InterSourceCorrelation) >= 0.985;
}

internal sealed record SeparationEvaluation(
    SeparatedAudio LastResult, int MeasuredRuns, double MedianRealTimeFactor,
    double WorstRealTimeFactor, TimeSpan WarmupTime, string Summary);

internal interface IOverlapInferenceEngine : IDisposable
{
    (float[] Source0, float[] Source1) Run(float[] samples, CancellationToken cancellationToken);
}

internal sealed class OverlapSeparationService : IDisposable
{
    public const int SampleRate = 16000;
    public const int MinimumSamples = SampleRate; // Shorter fragments give too little context for this noncausal model.
    public const int MaximumSamples = SampleRate * 6; // Fixed memory/context bound; this is not an unlimited recording service.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IOverlapInferenceEngine? _engine;
    private bool _disposed;

    public bool IsLoaded => _engine is not null && !_disposed;
    public string LoadedModelPath { get; private set; } = "";
    public string LastDiagnostic { get; private set; } = "Separation model is not loaded. Live separation remains off.";

    public OverlapSeparationService() { }
    internal OverlapSeparationService(IOverlapInferenceEngine engine) => _engine = engine; // Deterministic offline test seam.

    public async Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await OverlapModelStore.VerifyAsync(modelPath, OverlapModelCatalog.MossFormer2Int8, cancellationToken);
            // Explicit action only. CPU provider is intentional; no CUDA/DML allocation, no automatic fallback.
            IOverlapInferenceEngine next = await Task.Run<IOverlapInferenceEngine>(
                () => new OnnxOverlapInferenceEngine(modelPath), cancellationToken);
            if (cancellationToken.IsCancellationRequested) { next.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
            _engine?.Dispose();
            _engine = next;
            LoadedModelPath = Path.GetFullPath(modelPath);
            LastDiagnostic = "Optional separator loaded for evaluation on CPU (2 threads). Live translation remains unchanged.";
        }
        finally { _gate.Release(); }
    }

    public async Task<SeparatedAudio> SeparateAsync(float[] mono16k, CancellationToken cancellationToken = default)
    {
        ValidateInput(mono16k);
        // Zero backlog: concurrent evaluations must not silently queue expensive work behind live translation.
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("The separator is already evaluating audio. Wait or cancel that operation first.");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engine is null) throw new InvalidOperationException("Download and explicitly load the optional separation model first.");
            float[] snapshot = mono16k.ToArray();
            var timer = Stopwatch.StartNew();
            (float[] first, float[] second) = await Task.Run(() => _engine.Run(snapshot, cancellationToken), cancellationToken);
            timer.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOutputs(snapshot.Length, first, second);
            // Preserve relative source energy. Per-source RMS normalization could amplify leakage into a false second voice.
            float peak = Math.Max(first.Max(value => Math.Abs(value)), second.Max(value => Math.Abs(value)));
            double gain = peak > 0.98 ? 0.98 / peak : 1.0;
            if (gain < 1) for (int index = 0; index < first.Length; index++) { first[index] *= (float)gain; second[index] *= (float)gain; }
            double correlation = Correlation(first, second);
            double seconds = (double)snapshot.Length / SampleRate;
            double factor = timer.Elapsed.TotalSeconds / seconds;
            LastDiagnostic = $"Estimated two waveform tracks from {seconds:0.00}s in {timer.Elapsed.TotalMilliseconds:0}ms " +
                $"(RTF {factor:0.000}, CPU 2 threads). Track slots are not speaker identities; Mandarin quality/live timing unverified." +
                (Math.Abs(correlation) >= 0.985 ? " Outputs are nearly identical: do not assume two distinct voices." : "");
            return new(first, second, SampleRate, timer.Elapsed, factor, correlation, gain, LastDiagnostic);
        }
        finally { _gate.Release(); }
    }

    public async Task<SeparationEvaluation> EvaluateAsync(float[] mono16k, int measuredRuns = 3,
        CancellationToken cancellationToken = default)
    {
        if (measuredRuns is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(measuredRuns));
        SeparatedAudio warmup = await SeparateAsync(mono16k, cancellationToken);
        var results = new List<SeparatedAudio>();
        for (int index = 0; index < measuredRuns; index++)
            results.Add(await SeparateAsync(mono16k, cancellationToken));
        double[] factors = results.Select(result => result.RealTimeFactor).Order().ToArray();
        double median = factors.Length % 2 == 1 ? factors[factors.Length / 2]
            : (factors[factors.Length / 2 - 1] + factors[factors.Length / 2]) / 2;
        string summary = $"CPU-only separator evaluation: median RTF {median:0.000}, worst {factors[^1]:0.000}, " +
            $"{measuredRuns} runs, {warmup.AudioSeconds:0.00}s sample; warmup {warmup.ProcessingTime.TotalMilliseconds:0}ms. " +
            "RTF < 1 means separation computation alone is faster than the sample duration, NOT that live translation is ready. " +
            "Noncausal audio-window delay, two recognizer streams, voice matching and VRChat/GPU contention are not included. " +
            "No audio was played, saved, or sent to a service. Live separation remains off.";
        return new(results[^1], measuredRuns, median, factors[^1], warmup.ProcessingTime, summary);
    }

    internal static void ValidateInput(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length < MinimumSamples || samples.Length > MaximumSamples)
            throw new ArgumentException("Select between 1 and 6 seconds of mono 16 kHz audio for bounded separation evaluation.", nameof(samples));
        if (samples.Any(value => !float.IsFinite(value) || Math.Abs(value) > 1.001f))
            throw new ArgumentException("Input must be finite normalized float PCM (-1 to 1).", nameof(samples));
        if (samples.All(value => Math.Abs(value) < 0.000001f))
            throw new ArgumentException("The sample is silent; a separator benchmark cannot evaluate voice quality from silence.", nameof(samples));
    }

    private static void ValidateOutputs(int count, float[] first, float[] second)
    {
        if (first.Length != count || second.Length != count)
            throw new InvalidDataException("The separator returned unexpected waveform lengths; tracks were rejected.");
        if (first.Any(value => !float.IsFinite(value)) || second.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("The separator returned invalid samples; tracks were rejected.");
    }

    private static double Correlation(float[] first, float[] second)
    {
        double dot = 0, aa = 0, bb = 0;
        for (int index = 0; index < first.Length; index++) { dot += first[index] * second[index]; aa += first[index] * first[index]; bb += second[index] * second[index]; }
        return aa > 1e-20 && bb > 1e-20 ? dot / Math.Sqrt(aa * bb) : 0;
    }

    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; _engine?.Dispose(); _engine = null; }
        finally { _gate.Release(); }
    }
}

internal sealed class OnnxOverlapInferenceEngine : IOverlapInferenceEngine
{
    private readonly InferenceSession _session;
    public OnnxOverlapInferenceEngine(string modelPath)
    {
        using var options = new SessionOptions
        {
            InterOpNumThreads = 1, IntraOpNumThreads = 2, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableCpuMemArena = false
        };
        // Disable spinning so an idle/evaluation session does not consume cores while the user is gaming.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        _session = new InferenceSession(modelPath, options); // CPU is the only appended/default execution provider.
        try
        {
            RequireWaveform(_session.InputMetadata, "inputs");
            RequireWaveform(_session.OutputMetadata, "spk0");
            RequireWaveform(_session.OutputMetadata, "spk1");
            if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 2)
                throw new InvalidDataException("This is not the supported two-output separation model.");
        }
        catch { _session.Dispose(); throw; }
    }

    private static void RequireWaveform(IReadOnlyDictionary<string, NodeMetadata> metadata, string name)
    {
        if (!metadata.TryGetValue(name, out NodeMetadata? node) || node.ElementType != typeof(float)
            || node.Dimensions.Length != 2 || (node.Dimensions[0] > 0 && node.Dimensions[0] != 1)
            || node.Dimensions[1] > 0)
            throw new InvalidDataException($"Unsupported ONNX tensor '{name}'; expected dynamic float32 [1,T].");
    }

    public (float[] Source0, float[] Source1) Run(float[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => runOptions.Terminate = true);
        var tensor = new DenseTensor<float>(samples, new[] { 1, samples.Length });
        var input = NamedOnnxValue.CreateFromTensor("inputs", tensor);
        try
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                _session.Run(new[] { input }, new[] { "spk0", "spk1" }, runOptions);
            cancellationToken.ThrowIfCancellationRequested();
            float[] first = outputs.First(item => item.Name == "spk0").AsTensor<float>().ToArray();
            float[] second = outputs.First(item => item.Name == "spk1").AsTensor<float>().ToArray();
            return (first, second);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
    }

    public void Dispose() => _session.Dispose();
}

internal static class SeparationWaveSample
{
    // Reads an explicitly selected local sample, never captures a device or persists decoded audio.
    public static float[] ReadMono16k(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > 16_000_000) throw new InvalidDataException("Select a short WAV sample (at most 16 MB).");
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Select a PCM/float RIFF WAV file.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Select a WAV file.");
        ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
        uint rate = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = new(reader.ReadChars(4));
            uint size = reader.ReadUInt32();
            if (size > stream.Length - stream.Position) throw new InvalidDataException("WAV chunk exceeds the file size.");
            long end = stream.Position + size;
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("Invalid WAV format header.");
                format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadUInt32();
                reader.ReadUInt32(); blockAlign = reader.ReadUInt16(); bits = reader.ReadUInt16();
            }
            else if (id == "data") data = reader.ReadBytes(checked((int)size));
            stream.Position = end + (size % 2);
        }
        if (data is null || rate != 16000 || channels is < 1 or > 2
            || !((format == 1 && bits == 16) || (format == 3 && bits == 32))
            || blockAlign != channels * (bits / 8) || data.Length % blockAlign != 0)
            throw new InvalidDataException("Evaluation requires a 16 kHz mono/stereo WAV in PCM16 or float32 format (1–6 seconds).");
        int frames = data.Length / blockAlign;
        if (frames < OverlapSeparationService.MinimumSamples || frames > OverlapSeparationService.MaximumSamples)
            throw new InvalidDataException("Select a WAV containing between 1 and 6 seconds, rather than a full recording.");
        float[] mono = new float[frames];
        for (int frame = 0; frame < frames; frame++)
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = frame * blockAlign + channel * (bits / 8);
                float value = format == 1 ? BitConverter.ToInt16(data, offset) / 32768f : BitConverter.ToSingle(data, offset);
                mono[frame] += value / channels; // Monaural neural separation, not position-based identities.
            }
        OverlapSeparationService.ValidateInput(mono);
        return mono;
    }
}

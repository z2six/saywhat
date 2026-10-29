using SherpaOnnx;

namespace IncomingTranslator;

// One subtitle service owns one recognizer. Explicit CPU/2-thread limits keep
// this optional path from competing with VRChat for additional GPU memory.
internal sealed class SherpaSpeechRecognizer : IDisposable
{
    private readonly OfflineRecognizer _recognizer;
    private readonly string _engine;
    private bool _disposed;
    internal SherpaSpeechRecognizer(string engine, string dataRoot)
    {
        var files = new FireRedSpeechProvisioner(dataRoot);
        if (!files.IsReady(engine)) throw new InvalidOperationException("Download the selected subtitle speech model before starting.");
        _engine = engine;
        _recognizer = new OfflineRecognizer(CreateConfig(engine, files));
    }
    internal static OfflineRecognizerConfig CreateConfig(string engine, FireRedSpeechProvisioner files)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000; config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = 2; config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Tokens = files.ModelPath(engine, "tokens.txt");
        if (engine == "firered2-ctc") config.ModelConfig.FireRedAsrCtc.Model = files.ModelPath(engine, "model.int8.onnx");
        else if (engine == "firered2-aed")
        {
            config.ModelConfig.FireRedAsr.Encoder = files.ModelPath(engine, "encoder.int8.onnx");
            config.ModelConfig.FireRedAsr.Decoder = files.ModelPath(engine, "decoder.int8.onnx");
        }
        else throw new ArgumentException("Choose a FireRedASR2 model.", nameof(engine));
        return config;
    }
    internal Task<string> DecodeAsync(float[] samples, string source, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IncomingRecognitionCatalog.SupportsSource(_engine, source)) throw new ArgumentException("FireRedASR2 recognizes Chinese and English.");
        if (samples.Length > FireRedAudioSegmenter.MaxSamples) throw new ArgumentException("Speech sections must remain bounded.");
        token.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using OfflineStream stream = _recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            _recognizer.Decode(stream);
            token.ThrowIfCancellationRequested();
            // FireRed has no language-force parameter. Source selection chooses
            // the specialist and translator context, not an invented ASR prompt.
            return CleanTranscript(stream.Result.Text);
        }, token);
    }
    internal static string CleanTranscript(string text) => RecognitionTextFilter.CleanControlOnly(text.Trim());
    public void Dispose() { if (_disposed) return; _disposed = true; _recognizer.Dispose(); }
}

using System.Text.Json;

namespace IncomingTranslator;

// Pinned ONNX conversions of FireRedASR2, not the older FireRedASR1. Downloads
// are optional and contain no captured audio. Preparation never loads a model.
internal sealed class FireRedSpeechProvisioner(string dataRoot)
{
    internal const string AedRevision = "374cff185e952c40fcf2f6da972a3b6cf340608d";
    internal const string CtcRevision = "423be1acfdc5d8aaa5a485fb6263fe4ea8570b06";
    private static readonly SemaphoreSlim PreparationGate = new(1, 1);
    private readonly string _root = Path.GetFullPath(dataRoot);
    internal static readonly ProvisionAsset[] AedAssets =
    [
        Asset("encoder.int8.onnx", 817286833, "54048d66b6e8f3c80ea7ce95efe794587b0fd81d7271651d0decd3803852ae82", false),
        Asset("decoder.int8.onnx", 417291928, "b840ce7196ae4a14d05ae84bbf56082b6b61ccec5610fda907dddbcea37354ff", false),
        Asset("tokens.txt", 79172, "1bc613de2112d257e61a349c3e72d1b1a9cf19c33d3ca954197ad2171e5ea07b", false)
    ];
    internal static readonly ProvisionAsset[] CtcAssets =
    [
        Asset("model.int8.onnx", 775861420, "ca3dbabd82170110cc0b343c2890866d449984bc9cd92b9a18371ff80a81bb99", true),
        Asset("tokens.txt", 79172, "1bc613de2112d257e61a349c3e72d1b1a9cf19c33d3ca954197ad2171e5ea07b", true)
    ];
    private static ProvisionAsset Asset(string file, long size, string hash, bool ctc) => new(file, size, hash,
        "https://huggingface.co/csukuangfj2/" + (ctc ? "sherpa-onnx-fire-red-asr2-ctc-zh_en-int8-2026-02-25" : "sherpa-onnx-fire-red-asr2-zh_en-int8-2026-02-26") +
        "/resolve/" + (ctc ? CtcRevision : AedRevision) + "/" + file);
    internal static IReadOnlyList<ProvisionAsset> Assets(string engine) => engine switch
    { "firered2-ctc" => CtcAssets, "firered2-aed" => AedAssets, _ => throw new ArgumentException("Choose a FireRedASR2 model.", nameof(engine)) };
    internal string ModelRoot(string engine) { _ = Assets(engine); return Path.Combine(_root, "models", "subtitle-speech", engine); }
    internal string ModelPath(string engine, string file) => Path.Combine(ModelRoot(engine), Assets(engine).Single(asset => asset.FileName == file).FileName);
    private string ReceiptPath(string engine) => Path.Combine(ModelRoot(engine), "saywhat-verified.json");

    internal bool IsReady(string engine)
    {
        if (!IncomingRecognitionCatalog.UsesFireRed(engine)) return false;
        try
        {
            using var receipt = JsonDocument.Parse(File.ReadAllText(ReceiptPath(engine)));
            if (receipt.RootElement.ValueKind != JsonValueKind.Object || receipt.RootElement.GetProperty("engine").GetString() != engine) return false;
            JsonElement files = receipt.RootElement.GetProperty("files");
            return Assets(engine).All(asset => File.Exists(ModelPath(engine, asset.FileName)) &&
                new FileInfo(ModelPath(engine, asset.FileName)).Length == asset.SizeBytes &&
                files.GetProperty(asset.FileName).GetString() == asset.Sha256);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    internal long PendingDownloadBytes(string engine) => IsReady(engine) ? 0 : Assets(engine)
        .Where(asset => !File.Exists(ModelPath(engine, asset.FileName)) || new FileInfo(ModelPath(engine, asset.FileName)).Length != asset.SizeBytes)
        .Sum(asset => asset.SizeBytes);

    internal async Task PrepareAsync(string engine, IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        _ = Assets(engine); token.ThrowIfCancellationRequested();
        await PreparationGate.WaitAsync(token);
        try
        {
            if (IsReady(engine)) return;
            using var downloads = new LocalTalkProvisioner(_root);
            foreach (ProvisionAsset asset in Assets(engine))
            {
                progress?.Report(new("Preparing subtitle speech recognition", "Downloading " + asset.FileName));
                await downloads.DownloadVerifiedAsync(asset, ModelPath(engine, asset.FileName), progress, token);
            }
            token.ThrowIfCancellationRequested();
            string temporary = ReceiptPath(engine) + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
                    { engine, files = Assets(engine).ToDictionary(asset => asset.FileName, asset => asset.Sha256) }), token);
                File.Move(temporary, ReceiptPath(engine), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { PreparationGate.Release(); }
    }
}

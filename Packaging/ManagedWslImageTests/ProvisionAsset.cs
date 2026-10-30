namespace IncomingTranslator;

// Keep this test host independent of the Windows app, WPF, model provisioner
// and every audio/inference dependency.
internal sealed record ProvisionAsset(string FileName, long SizeBytes, string Sha256, string DownloadUrl);

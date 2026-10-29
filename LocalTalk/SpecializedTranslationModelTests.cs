using System.Text.Json;

namespace IncomingTranslator;

internal static class SpecializedTranslationModelTests
{
    public static int Run(string directory)
    {
        int checks = 0;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Specialized model check failed: " + message); checks++; }
        ProvisionModelChoice gemma = LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == "translategemma-4b-q4_k_m");
        ProvisionModelChoice liquid = LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == "lfm2-350m-enjp-mt-q4_k_m");
        Require(gemma.Architecture == "gemma3" && liquid.Architecture == "lfm2" &&
            gemma.Role == ProvisionModelRole.Translation && liquid.Role == ProvisionModelRole.Translation,
            "new dedicated models use their supported runtime architectures");
        Require(gemma.SizeBytes == 2_489_909_312 && gemma.Sha256 == "7f7357c14abd9da4eb200b38b05da502cd6e10d7e1d403fbc9f78c19f3209b72" &&
            gemma.DownloadUrl.Contains("/resolve/7c938465a870d8624bcfa98a8e4a3510053c19a8/", StringComparison.Ordinal), "Gemma conversion is pinned by revision, size and SHA-256");
        Require(liquid.SizeBytes == 229_310_240 && liquid.Sha256 == "574ef7980dd20d69b494bce82565db5f25124d404be02052bbca62277d33077b" &&
            liquid.DownloadUrl.Contains("/resolve/889c88c3d3681b4f342ae210a1d2a1369aba0e10/", StringComparison.Ordinal), "official Liquid GGUF is pinned by revision, size and SHA-256");
        Require(gemma.RequiresTermsAcceptance && liquid.RequiresTermsAcceptance && gemma.License == "Gemma terms" && liquid.License == "LFM Open License 1.0" &&
            gemma.SourceRepositoryUrl.Contains("bullerwins", StringComparison.Ordinal) && liquid.SourceRepositoryUrl.Contains("LiquidAI", StringComparison.Ordinal),
            "catalog distinguishes third-party conversion provenance and non-Apache model terms");
        Require(new AppSettings().OwnManagedModelId == "hy-mt2-1.8b-q4_k_m", "existing speed-first default is unchanged");
        using (var body = JsonDocument.Parse(JsonSerializer.Serialize(IncomingTranslationService.IncomingTranslationPayload("你好", "generic must not leak", new AppSettings {
            TranslatorModel = gemma.ApiAlias, IncomingSourceLanguage = "Chinese", IncomingTargetLanguage = "English" }, 256, strictRetry: true))))
        {
            JsonElement root = body.RootElement; JsonElement messages = root.GetProperty("messages");
            Require(messages.GetArrayLength() == 1 && messages[0].GetProperty("role").GetString() == "user" &&
                messages[0].GetProperty("content").GetString() == TranslationModelPrompts.TranslateGemmaUserMessage("你好", "Chinese", "English"),
                "incoming Gemma uses the same exact dedicated source/target task, even for a retry");
            Require(!root.TryGetProperty("chat_template_kwargs", out _) && !root.TryGetProperty("min_p", out _) && !root.TryGetProperty("repeat_penalty", out _) &&
                root.GetProperty("temperature").GetDouble() == 0, "incoming Gemma does not serialize null sampling options or Qwen toggles");
        }
        using (var body = JsonDocument.Parse(JsonSerializer.Serialize(IncomingTranslationService.IncomingTranslationPayload("Hello", "generic must not leak", new AppSettings {
            TranslatorModel = liquid.ApiAlias, IncomingSourceLanguage = "English", IncomingTargetLanguage = "Japanese" }, 256, strictRetry: true))))
        {
            JsonElement root = body.RootElement; JsonElement messages = root.GetProperty("messages");
            Require(messages.GetArrayLength() == 2 && messages[0].GetProperty("content").GetString() == "Translate to Japanese." &&
                messages[1].GetProperty("content").GetString() == "Hello", "incoming Liquid uses its required system prompt plus raw source");
            Require(root.GetProperty("min_p").GetDouble() == .1 && root.GetProperty("temperature").GetDouble() == .5 &&
                root.GetProperty("repeat_penalty").GetDouble() == 1.05, "incoming Liquid uses its documented sampling");
        }
        bool invalid = false;
        try { _ = IncomingTranslationService.IncomingTranslationPayload("你好", "", new AppSettings {
            TranslatorModel = liquid.ApiAlias, IncomingSourceLanguage = "Chinese", IncomingTargetLanguage = "English" }, 256); }
        catch (ArgumentException) { invalid = true; }
        Require(invalid, "incoming unsupported Liquid source is refused before a request");

        // Inert fake executable/model files live only in the isolated test root.
        // We inspect ProcessStartInfo; never call Process.Start or read weights.
        string rootDirectory = Path.Combine(Path.GetFullPath(directory), "specialized-model-launch");
        using var provisioner = new LocalTalkProvisioner(rootDirectory);
        ProvisionRuntimeChoice runtime = LocalTalkProvisioner.RuntimeCatalog.Single(item => item.Id == "llama-cpu");
        string runtimeDirectory = Path.Combine(provisioner.RuntimesRoot, runtime.Id + "-" + LocalTalkProvisioner.LlamaVersion);
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(Path.Combine(runtimeDirectory, "llama-server.exe"), "offline fixture only; never execute");
        File.WriteAllText(Path.Combine(runtimeDirectory, "localtalk-runtime.json"), JsonSerializer.Serialize(new {
            Id = runtime.Id, Version = LocalTalkProvisioner.LlamaVersion, Hashes = runtime.Assets.Select(asset => asset.Sha256).ToArray() }));
        string modelPath = Path.Combine(rootDirectory, "not-a-real-model.gguf");
        File.WriteAllText(modelPath, "offline path fixture only; never load");
        var gemmaInfo = provisioner.BuildLlamaStartInfo(runtime.Id, modelPath, gemma.ApiAlias);
        string[] arguments = gemmaInfo.ArgumentList.ToArray();
        string Argument(string name) => arguments[Array.IndexOf(arguments, name) + 1];
        Require(Argument("--ctx-size") == "2048" && Argument("--host") == "127.0.0.1" && Argument("--n-gpu-layers") == "0",
            "managed Gemma launch reserves the documented 2K context and remains local");
        string templatePath = Argument("--chat-template-file");
        Require(LocalTalkProvisioner.IsWithin(templatePath, rootDirectory) && File.ReadAllText(templatePath) == TranslationModelPrompts.TranslateGemmaTextTemplate &&
            !arguments.Contains("--chat-template-kwargs"), "managed Gemma selects the exact app-owned compatible wrapper, never a Qwen override");
        foreach (string alias in new[] { liquid.ApiAlias, "tencent/hy-mt2-1.8b", "qwen/qwen3-8b" })
        {
            string[] other = provisioner.BuildLlamaStartInfo(runtime.Id, modelPath, alias).ArgumentList.ToArray();
            Require(other[Array.IndexOf(other, "--ctx-size") + 1] == "4096" && !other.Contains("--chat-template-file"),
                "other models retain existing context and embedded chat template");
        }
        foreach (int port in new[] { 1024, 25347, 65535 })
        {
            string[] selected = provisioner.BuildLlamaStartInfo(runtime.Id, modelPath, liquid.ApiAlias, port).ArgumentList.ToArray();
            Require(selected[Array.IndexOf(selected, "--port") + 1] == port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "managed translator accepts a selected safe port without loading a model");
        }
        foreach (int port in new[] { -1, 0, 1023, 65536, int.MaxValue })
        {
            bool refused = false;
            try { provisioner.BuildLlamaStartInfo(runtime.Id, modelPath, liquid.ApiAlias, port); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            Require(refused, "managed translator rejects unsafe ports before runtime construction");
        }
        Console.WriteLine($"PASS: {checks} specialized desktop model catalog, incoming adapter and inert launch checks; no model loaded or process started.");
        return checks;
    }
}

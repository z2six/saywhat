using System.Net;
using System.Text;
using System.Text.Json;

internal static class SpecializedTranslationTests
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Specialized translation check failed: " + message);
        _checks++;
    }
    public static async Task RunAsync()
    {
        const string gemma = "google/translategemma-4b-it", lfm = "liquidai/lfm2-350m-enjp-mt";
        Check(TranslationModelCapabilities.RequiresExplicitSource(gemma) && TranslationModelCapabilities.RequiresExplicitSource(lfm) &&
            !TranslationModelCapabilities.RequiresExplicitSource("tencent/hy-mt2-1.8b") && !TranslationModelCapabilities.RequiresExplicitSource("qwen/qwen3-8b"), "explicit source requirements apply only to new models");
        Check(TranslationModelCapabilities.RequiresManagedRuntime(gemma) && !TranslationModelCapabilities.RequiresManagedRuntime(lfm), "Gemma cannot silently use an incompatible external chat template");
        Check(TranslationModelCapabilities.SupportedTargets(lfm).SequenceEqual(new[] { "English", "Japanese" }) &&
            TranslationModelCapabilities.SupportedTargets(gemma).Count == 10 &&
            !TranslationModelCapabilities.SupportedTargets(lfm).Contains("Thai"), "Liquid does not expose unsupported Chinese, Korean or Thai targets");
        Check(TranslationModelCapabilities.Validate(gemma, "English", new[] { "Thai" }) is null &&
            TranslationModelPrompts.TranslateGemmaUserMessage("Hello", "English", "Thai").Contains("Thai (th)", StringComparison.Ordinal),
            "TranslateGemma uses the supported Thai language and ISO code in its explicit translation task");
        foreach (string model in new[] { gemma, lfm })
            Check(TranslationModelCapabilities.Validate(model, "Automatic", new[] { "Japanese" }) is not null &&
                TranslationModelCapabilities.Validate(model, null, new[] { "Japanese" }) is not null, "no model source language is guessed from script or an unknown ASR field");
        foreach (string source in new[] { "Chinese", "Korean", "French", "Automatic" })
            Check(TranslationModelCapabilities.Validate(lfm, source, new[] { "English" }) is not null, "Liquid rejects every unsupported explicit spoken language");
        foreach (string target in new[] { "Chinese", "Korean", "French" })
            Check(TranslationModelCapabilities.Validate(lfm, "English", new[] { "Japanese", target }) is not null, "Liquid validates additional targets, not only the primary");
        Check(TranslationModelCapabilities.Validate(lfm, "en", new[] { "ja", "EN" }) is null &&
            TranslationModelCapabilities.Validate(gemma, "zh-CN", new[] { "English", "Japanese", "Korean" }) is null, "known aliases and same-language pass-through are supported explicitly");
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        ResolvedOpenAiChatSettings Settings(string model) => new(new Uri("http://127.0.0.1:1235/v1/chat/completions"), null, model, "GENERIC PROMPT MUST NOT LEAK /no_think");
        bool rejected = false;
        try { _ = LocalHost.OwnVoiceTranslator(http, Settings(lfm), new(new[] { "Japanese", "Chinese" }, SourceLanguage: "English", ManagedTranslator: true)); }
        catch (ConfigurationException) { rejected = true; }
        Check(rejected && fixture.Bodies.Count == 0, "host rejects unsupported Liquid extras before any provider request");
        rejected = false;
        try { _ = LocalHost.OwnVoiceTranslator(http, Settings(gemma), new(new[] { "Chinese" }, SourceLanguage: "English", ManagedTranslator: false)); }
        catch (ConfigurationException) { rejected = true; }
        Check(rejected && fixture.Bodies.Count == 0, "host rejects external Gemma template ambiguity before HTTP");
        var englishJapanese = new SpecializedTextTranslator(http, Settings(lfm), "English", "Japanese");
        Check(await englishJapanese.TranslateAsync("What is C. elegans?", CancellationToken.None) == "翻訳", "Liquid mock translation parses normally");
        using (var body = JsonDocument.Parse(fixture.Bodies[^1]))
        {
            JsonElement payload = body.RootElement; JsonElement messages = payload.GetProperty("messages");
            Check(messages.GetArrayLength() == 2 && messages[0].GetProperty("role").GetString() == "system" &&
                messages[0].GetProperty("content").GetString() == "Translate to Japanese." && messages[1].GetProperty("content").GetString() == "What is C. elegans?", "Liquid receives its exact documented system task plus unmodified raw source");
            Check(payload.GetProperty("temperature").GetDouble() == .5 && payload.GetProperty("top_p").GetDouble() == 1 &&
                payload.GetProperty("min_p").GetDouble() == .1 && payload.GetProperty("repeat_penalty").GetDouble() == 1.05 &&
                payload.GetProperty("max_tokens").GetInt32() == 256 && !payload.TryGetProperty("chat_template_kwargs", out _), "Liquid author sampling is bounded and contains no Qwen toggles");
        }
        await new SpecializedTextTranslator(http, Settings(lfm), "Japanese", "English").TranslateAsync("これは何ですか？", CancellationToken.None);
        using (var body = JsonDocument.Parse(fixture.Bodies[^1]))
            Check(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == "Translate to English.", "Liquid reverse pair does not inherit the earlier direction");
        int requests = fixture.Bodies.Count;
        Check(await new SpecializedTextTranslator(http, Settings(lfm), "English", "English").TranslateAsync(" Hello ", CancellationToken.None) == "Hello" &&
            fixture.Bodies.Count == requests, "explicit same-language target returns captured text without inference");
        var grouped = LocalHost.OwnVoiceTranslator(http, Settings(lfm), new(new[] { "English", "Japanese" }, SourceLanguage: "English", ManagedTranslator: true));
        Check(await grouped.TranslateAsync("Hello", CancellationToken.None) == "Hello\n翻訳" && fixture.Bodies.Count == requests + 1,
            "Liquid can combine explicit original English and translated Japanese without a duplicate model request");
        const string sourceText = " Today I walked my dog. ";
        await new SpecializedTextTranslator(http, Settings(gemma), "English", "Chinese").TranslateAsync(sourceText, CancellationToken.None);
        using (var body = JsonDocument.Parse(fixture.Bodies[^1]))
        {
            JsonElement payload = body.RootElement; JsonElement messages = payload.GetProperty("messages");
            string user = messages[0].GetProperty("content").GetString()!;
            Check(messages.GetArrayLength() == 1 && messages[0].GetProperty("role").GetString() == "user" &&
                user == TranslationModelPrompts.TranslateGemmaUserMessage(sourceText, "English", "Chinese"), "Gemma uses Google's exact rendered text task in one user message");
            Check(user.StartsWith("You are a professional English (en) to Chinese (zh) translator.", StringComparison.Ordinal) &&
                user.EndsWith("\n\n\nToday I walked my dog.", StringComparison.Ordinal) && !user.Contains("GENERIC PROMPT", StringComparison.Ordinal) &&
                !user.Contains("/no_think", StringComparison.Ordinal), "Gemma receives explicit source/target codes and full source once, with no generic assistant instructions");
            Check(payload.GetProperty("temperature").GetDouble() == 0 && payload.GetProperty("max_tokens").GetInt32() == 256,
                "Gemma comparison uses bounded deterministic decoding, not Hy-MT sampling");
        }
        requests = fixture.Bodies.Count;
        rejected = false;
        try { await new SpecializedTextTranslator(http, Settings(gemma), "English", "Chinese").TranslateAsync(new string('界', 400), CancellationToken.None); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && fixture.Bodies.Count == requests, "Gemma rejects oversized full sentences rather than silently clipping its 2K context");
        string exactBound = new('x', TranslationModelPrompts.TranslateGemmaMaximumSourceUtf8Bytes);
        Check(TranslationModelPrompts.SpecializedTask(gemma, "English", "Chinese", exactBound).Messages[0].Content.EndsWith(exactBound, StringComparison.Ordinal), "Gemma source bound retains complete text up to its documented app safety budget");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        rejected = false;
        try { await englishJapanese.TranslateAsync("cancelled source", cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected && fixture.Bodies.Count == requests, "already cancelled specialized work makes no HTTP request");
        string task = TranslationModelPrompts.TranslateGemmaUserMessage("Hello", "English", "Japanese");
        string expected = "<bos><start_of_turn>user\n" + task + "<end_of_turn>\n<start_of_turn>model\n";
        Check(Encoding.UTF8.GetByteCount(task) < 700 && TranslationModelPrompts.TranslateGemmaTextTemplate.Contains("{{ bos_token }}", StringComparison.Ordinal) &&
            !TranslationModelPrompts.TranslateGemmaTextTemplate.Contains("/no_think", StringComparison.Ordinal), "managed Gemma wrapper retains BOS/role structure without injecting another task");
        // Actual pure Jinja rendering is checked separately from this generated
        // fixture; it resides only in the mandatory isolated self-test root.
        File.WriteAllText(Path.Combine(LocalHost.Root, "translategemma-template-fixture.json"), JsonSerializer.Serialize(new {
            template = TranslationModelPrompts.TranslateGemmaTextTemplate,
            messages = new[] { new { role = "user", content = task } }, expected,
            sourceText = "Hello", sourceCode = "en", targetCode = "ja" }));
        Console.WriteLine($"PASS: {_checks} specialized translation mock checks; no models, GPU, audio, or network used.");
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri!.Host == "127.0.0.1" && request.RequestUri.AbsolutePath == "/v1/chat/completions", "specialized task uses only the configured local endpoint");
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"翻訳\"}}]}") };
        }
    }
}

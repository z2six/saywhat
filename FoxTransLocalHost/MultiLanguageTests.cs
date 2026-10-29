using System.Net;
using System.Text.Json;

// Mock providers only: no audio, UDP, GPU/models, live settings, or real HTTP.
internal static class MultiLanguageTests
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Multi-language check failed: " + message);
        _checks++;
    }

    public static async Task RunAsync()
    {
        foreach (string json in new[] { "{}", "[]", "{\"OwnVoiceAdditionalLanguagesEnabled\":false}",
            "{\"OwnVoiceAdditionalLanguagesEnabled\":\"true\"}",
            "{\"OwnVoiceAdditionalLanguagesEnabled\":true,\"OwnVoiceTargetLanguage\":null}" })
        {
            using var settings = JsonDocument.Parse(json);
            Check(!LocalHost.ResolveOwnVoiceLanguages(settings.RootElement).Multiple,
                "missing, disabled, and malformed opt-ins retain the single-target path");
        }
        using var preferences = JsonDocument.Parse("""{"OwnVoiceAdditionalLanguagesEnabled":true,"OwnVoiceTargetLanguage":" EN ","OwnVoiceAdditionalTargetLanguages":["English"," ja ","JAPANESE","zh-CN","ko","unknown",null],"Glossary":"Doritos → 多力多滋"}""");
        var options = LocalHost.ResolveOwnVoiceLanguages(preferences.RootElement);
        Check(options.Targets.SequenceEqual(new[] { "English", "Japanese", "Chinese", "Korean" }), "primary-first canonical aliases, deduplication and four-target cap");
        Check(options.Glossary == "Doritos → 多力多滋", "glossary is retained for matching source terms");
        using var thaiPreferences = JsonDocument.Parse("""{"OwnRecognitionEngine":"nemotron","OwnVoiceSourceLanguage":"English","OwnVoiceTargetLanguage":"Japanese","OwnVoiceAdditionalLanguagesEnabled":true,"OwnVoiceAdditionalTargetLanguages":["zh-CN","th-TH","Thai"]}""");
        var thaiOptions = LocalHost.ResolveOwnVoiceLanguages(thaiPreferences.RootElement);
        Check(thaiOptions.Targets.SequenceEqual(new[] { "Japanese", "Chinese", "Thai" }) &&
            LocalHost.ResolveRecognitionPreferences(thaiPreferences.RootElement).Language == "en-US",
            "Japanese, Chinese and Thai outputs preserve English speech recognition and deduplicate Thai aliases");
        Check(LocalHost.NormalizeChatLanguage(" th ") == "Thai" && LocalHost.NormalizeChatLanguage("TH-TH") == "Thai" &&
            TranslationModelPrompts.LanguageCode("Thai") == "th", "desktop and host Thai aliases map to the real text-translation code");
        Check(TranslationModelCapabilities.Validate("tencent/hy-mt2-1.8b", "English", thaiOptions.Targets) is null &&
            TranslationModelCapabilities.Validate("tencent/hy-mt2-7b", "English", thaiOptions.Targets) is null &&
            TranslationModelCapabilities.Validate("lfm2-350m-enjp-mt", "English", new[] { "Thai" }) is not null,
            "both Hy-MT models accept Thai while the English/Japanese-only translator rejects it");
        using var extraInvalid = JsonDocument.Parse("""{"OwnVoiceAdditionalLanguagesEnabled":true,"OwnVoiceTargetLanguage":"Chinese","OwnVoiceAdditionalTargetLanguages":[false,null,"unsupported"," Chinese "]}""");
        Check(!LocalHost.ResolveOwnVoiceLanguages(extraInvalid.RootElement).Multiple, "invalid and duplicate extras do not activate requests");
        var settingsValue = new ResolvedOpenAiChatSettings(new Uri("http://127.0.0.1:1235/v1/chat/completions"), null,
            "tencent/hy-mt2-1.8b", TranslationModelPrompts.HyMtInstruction("English"));
        using var fixture = new TranslationFixture();
        using var http = new HttpClient(fixture);
        Check(LocalHost.OwnVoiceTranslator(http, settingsValue, LocalHost.OwnVoiceLanguages.Disabled) is OpenAiTextTranslator,
            "one target uses exactly the original provider, prompt, endpoint, and budget path");
        ITextTranslator translator = LocalHost.OwnVoiceTranslator(http, settingsValue, options);
        Check(await translator.TranslateAsync("Hello there", CancellationToken.None) == "Hello\nこんにちは 友達\n你好\n안녕하세요", "ordered native-script target lines normalize provider line breaks");
        Check(fixture.Bodies.Count == 4 && fixture.MaximumActive == 1, "one shared model receives exactly four serial requests, never parallel GPU work");
        for (int index = 0; index < fixture.Bodies.Count; index++)
        {
            using var body = JsonDocument.Parse(fixture.Bodies[index]);
            JsonElement request = body.RootElement;
            Check(request.GetProperty("model").GetString() == settingsValue.Model && request.GetProperty("messages").GetArrayLength() == 1 &&
                request.GetProperty("messages")[0].GetProperty("role").GetString() == "user", "each Hy-MT target keeps the dedicated single-user prompt and same model");
            Check(request.GetProperty("messages")[0].GetProperty("content").GetString()!.EndsWith("\nHello there", StringComparison.Ordinal), "each target receives the whole same source context");
        }
        Check(fixture.Bodies[0].Contains("into English", StringComparison.Ordinal) && fixture.Bodies[1].Contains("into Japanese", StringComparison.Ordinal) &&
            fixture.Bodies[2].Contains("into Simplified Chinese", StringComparison.Ordinal) && fixture.Bodies[3].Contains("into Korean", StringComparison.Ordinal), "requested languages cannot inherit the primary language prompt");
        using (var thaiFixture = new TranslationFixture())
        using (var thaiHttp = new HttpClient(thaiFixture))
        {
            ITextTranslator thaiTranslator = LocalHost.OwnVoiceTranslator(thaiHttp,
                settingsValue with { Prompt = TranslationModelPrompts.HyMtInstruction("Japanese") }, thaiOptions);
            Check(await thaiTranslator.TranslateAsync("Hello there", CancellationToken.None) == "こんにちは 友達\n你好\nสวัสดี",
                "English speech produces ordered Japanese, Chinese and Thai lines in one chat message");
            Check(thaiFixture.Bodies.Count == 3 && thaiFixture.MaximumActive == 1 &&
                thaiFixture.Bodies[2].Contains("into Thai", StringComparison.Ordinal),
                "Thai uses its own target prompt through one shared serial local translator");
            ITextTranslator thaiPrimary = LocalHost.OwnVoiceTranslator(thaiHttp,
                settingsValue with { Prompt = TranslationModelPrompts.HyMtInstruction("Thai") },
                new LocalHost.OwnVoiceLanguages(new[] { "Thai" }, SourceLanguage: "English"));
            Check(await thaiPrimary.TranslateAsync("Hello there", CancellationToken.None) == "สวัสดี",
                "Thai also works as the only primary output through the original single-target path");
        }
        Check(TranslationModelPrompts.CasualChatInstruction("Chinese").Contains("Simplified Chinese", StringComparison.Ordinal) &&
            TranslationModelPrompts.CasualChatInstruction("Japanese").Contains("not romanized", StringComparison.Ordinal), "general models use an explicit native written script target");
        var cadence = LocalHost.PreviewSchedulingSettings(new(250, 700, 2, 1200, 800), 3);
        Check(cadence.MinimumIntervalMs == 4500 && cadence.MaximumIntervalMs == 9000, "multilingual speculative work is throttled by target count");
        await TestFailuresAndTimeoutsAsync();
        await TestCancellationAndSerialGroupsAsync();
        TestFormatting();
        Console.WriteLine($"PASS: {_checks} multi-language offline checks; no live settings, models, audio, or network used.");
    }

    private static async Task TestFailuresAndTimeoutsAsync()
    {
        var failures = new List<string>();
        ITextTranslator Failure() => new DelegateTranslator((_, _) => throw new OpenAiProviderException("mock", "private source must never reach a failure report"));
        ITextTranslator Good(string text) => new DelegateTranslator((_, _) => Task.FromResult(text));
        var missingPrimary = new MultiTargetTextTranslator(new[] { ("English", Failure()), ("Japanese", Good("こんにちは")), ("Chinese", Good("你好")) },
            (target, reason) => failures.Add(target + ":" + reason));
        Check(await missingPrimary.TranslateAsync("synthetic source", CancellationToken.None) == "こんにちは\n你好", "a missing primary translation does not discard successful extras or reorder them");
        Check(failures.SequenceEqual(new[] { "English:provider-failed" }), "missing-language diagnostics contain known target/reason, never response bodies");
        failures.Clear();
        var missingExtra = new MultiTargetTextTranslator(new[] { ("English", Good("Hello")), ("Japanese", Failure()), ("Chinese", Good("你好")) },
            (target, reason) => failures.Add(target + ":" + reason));
        Check(await missingExtra.TranslateAsync("synthetic source", CancellationToken.None) == "Hello\n你好" && failures.SequenceEqual(new[] { "Japanese:provider-failed" }),
            "an additional target failure preserves the primary and later successes with diagnostics");
        failures.Clear();
        var forever = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new MultiTargetTextTranslator(new[] { ("English", Good("Hello")),
            ("Japanese", (ITextTranslator)new DelegateTranslator((_, _) => forever.Task)), ("Chinese", Good("你好")) },
            (target, reason) => failures.Add(target + ":" + reason), TimeSpan.FromMilliseconds(70));
        Task<string> timed = timeout.TranslateAsync("synthetic source", CancellationToken.None);
        Check(await timed.WaitAsync(TimeSpan.FromSeconds(1)) == "Hello", "a cancellation-ignoring provider cannot freeze the group and successful text still sends");
        Check(failures.SequenceEqual(new[] { "Japanese:group-timeout", "Chinese:group-timeout" }), "timed-out and not-started targets are both identified in diagnostics");
        forever.SetException(new IOException("late private provider detail"));
        var allFailed = new MultiTargetTextTranslator(new[] { ("English", Failure()), ("Japanese", Failure()) });
        bool failed = false;
        try { await allFailed.TranslateAsync("synthetic source", CancellationToken.None); }
        catch (IOException error) { failed = !error.Message.Contains("private", StringComparison.Ordinal); }
        Check(failed, "all-target failure reports a safe bounded failure, never an empty success");
    }

    private static async Task TestCancellationAndSerialGroupsAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledFirst = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0;
        var provider = new DelegateTranslator(async (source, token) =>
        {
            int now = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, now);
            try
            {
                if (source == "stale") { entered.TrySetResult(); return await cancelledFirst.Task.WaitAsync(token); }
                await Task.Delay(2, token);
                return source;
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var translator = new MultiTargetTextTranslator(new[] { ("English", (ITextTranslator)provider), ("Chinese", (ITextTranslator)provider) });
        using var cancellation = new CancellationTokenSource();
        Task<string> stale = translator.TranslateAsync("stale", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<string> final = translator.TranslateAsync("current", CancellationToken.None);
        Check(!final.IsCompleted && active == 1, "a newer group queues behind at most one active translation request");
        cancellation.Cancel();
        bool cancelled = false;
        try { await stale; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && await final == "current\ncurrent" && maximum == 1, "superseded or muted work never publishes a partial and releases the gate for the final");
        cancelledFirst.TrySetResult("late stale result");
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        cancelled = false;
        try { await translator.TranslateAsync("must not start", alreadyCancelled.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && active == 0, "parent cancellation before scheduling starts no model request");
    }

    private static void TestFormatting()
    {
        Check(LocalHost.FormatChatDisplay("Hello\nこんにちは\n你好", true) == "Hello\nこんにちは\n你好", "short multi-language messages retain one line per target");
        string[] inputs = [new string('英', 200) + "A", new string('日', 200) + "B", new string('中', 200) + "C"];
        string display = LocalHost.FormatChatDisplay(string.Join('\n', inputs), true);
        string[] lines = display.Split('\n');
        Check(lines.Length == 3 && lines.All(line => line.StartsWith('…')) && TranslationWindows.TextElementCount(display) <= 144,
            "long translations share the full chat budget rather than deleting the primary language");
        Check(lines[0].EndsWith('A') && lines[1].EndsWith('B') && lines[2].EndsWith('C') &&
            lines.Select(TranslationWindows.TextElementCount).Max() - lines.Select(TranslationWindows.TextElementCount).Min() <= 1,
            "each target keeps its newest context and allocations differ by at most one character");
        display = LocalHost.FormatChatDisplay("OK\n" + inputs[1] + "\n" + inputs[2], true);
        lines = display.Split('\n');
        Check(lines[0] == "OK" && TranslationWindows.TextElementCount(display) == 144 && TranslationWindows.TextElementCount(lines[1]) > 47,
            "short targets donate unused space to longer translations");
        string emoji = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦e\u0301", 100));
        display = LocalHost.FormatChatDisplay(emoji + "\r\n" + emoji + "\r" + emoji, true);
        Check(display.Split('\n').Length == 3 && display.Count(value => value == '\n') <= 8 &&
            TranslationWindows.TextElementCount(display) <= 144 && display.EnumerateRunes().All(rune => rune.Value != 0xfffd) &&
            display.Split('\n').All(line => line.EndsWith("👨‍👩‍👧‍👦e\u0301", StringComparison.Ordinal)), "emoji/combining characters and CRLF keep whole graphemes under both chat limits");
        Check(LocalHost.FormatChatDisplay("one\ntwo\nthree\nfour\nfive", true) == "one\ntwo\nthree\nfour", "malformed groups cannot exceed four target rows");
        string thaiCluster = "ก\u0E49";
        string thaiLong = string.Concat(Enumerable.Repeat(thaiCluster, 200));
        display = LocalHost.FormatChatDisplay("こんにちは\n你好\n" + thaiLong, true);
        lines = display.Split('\n');
        Check(lines.Length == 3 && lines[0] == "こんにちは" && lines[1] == "你好" &&
            TranslationWindows.TextElementCount(display) <= 144 &&
            lines[2].EndsWith(thaiCluster, StringComparison.Ordinal) &&
            lines[2] == "…" + string.Concat(Enumerable.Repeat(thaiCluster, TranslationWindows.TextElementCount(lines[2]) - 1)),
            "Thai combining marks stay with their base letters when three target rows share the chat budget");
        display = LocalHost.FormatChatDisplay(string.Join('\n', Enumerable.Repeat(emoji, 4)), true);
        Check(display.Split('\n').Length == 4 && TranslationWindows.TextElementCount(display) <= 144 &&
            display.Split('\n').All(line => line.EndsWith("👨‍👩‍👧‍👦e\u0301", StringComparison.Ordinal)),
            "four long targets share the VRChat budget without breaking graphemes or losing a target");
    }

    private sealed class DelegateTranslator(Func<string, CancellationToken, Task<string>> translate) : ITextTranslator
    {
        public Task<string> TranslateAsync(string sourceText, CancellationToken cancellationToken) => translate(sourceText, cancellationToken);
    }

    private sealed class TranslationFixture : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public int MaximumActive { get; private set; }
        private int _active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref _active));
            try
            {
                Check(request.RequestUri!.AbsoluteUri == "http://127.0.0.1:1235/v1/chat/completions", "all target requests use the configured local endpoint");
                string body = await request.Content!.ReadAsStringAsync(token); Bodies.Add(body);
                using var document = JsonDocument.Parse(body);
                string prompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                string output = prompt.Contains("into Japanese", StringComparison.Ordinal) ? "こんにちは\r\n友達" :
                    prompt.Contains("into Simplified Chinese", StringComparison.Ordinal) ? "你好" :
                    prompt.Contains("into Korean", StringComparison.Ordinal) ? "안녕하세요" :
                    prompt.Contains("into Thai", StringComparison.Ordinal) ? "สวัสดี" : "Hello";
                await Task.Yield();
                string response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = output } } } });
                return new(HttpStatusCode.OK) { Content = new StringContent(response) };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}

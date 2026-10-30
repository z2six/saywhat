using System.Text;
using System.Text.Json;

// Isolated file handles and synthetic output callbacks only. No capture device,
// model, recognizer, HTTP endpoint or real OSC destination is opened.
internal static class HostLoggingTests
{
    internal static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Host log fixture: " + detail);
            checks++;
        }
        string root = Path.Combine(LocalHost.Root, "host-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "fixture.log");
        var console = new List<string>();
        var logger = new BestEffortHostLogger(console.Add);
        string previousLog = LocalHost.LogPath;
        string previousRoot = LocalHost.Root;
        TextWriter previousOutput = Console.Out;
        try
        {
            foreach (var item in new (Exception Error, string Code)[]
            {
                (new DllNotFoundException("PRIVATE binary"), "native-library-missing"),
                (new BadImageFormatException("PRIVATE binary"), "native-library-invalid"),
                (new EntryPointNotFoundException("PRIVATE symbol"), "native-library-invalid"),
                (new AudioDeviceSelectionException("PRIVATE device name"), "microphone-unavailable"),
                (new UnauthorizedAccessException("PRIVATE model file"), "access-denied"),
                (new HttpRequestException("PRIVATE endpoint or request"), "provider-connection-failed"),
                (new InvalidOperationException("PRIVATE arbitrary exception"), "host-failed"),
                (new TypeInitializationException("PRIVATE type", new DllNotFoundException("PRIVATE dependency")), "native-library-missing")
            })
            {
                string evidence = LocalHost.SupportFailureLine(item.Error);
                Check(evidence.StartsWith("SAYWHAT_STATUS component=microphone-host event=failed code=" + item.Code + " hresult=0x", StringComparison.Ordinal) &&
                    !evidence.Contains("PRIVATE", StringComparison.Ordinal), "typed failure evidence excludes exception text, file names, devices and endpoints");
            }
            Check(!LocalHost.SupportFailureLine(new UnauthorizedAccessException("PRIVATE"))
                .Contains("microphone-access-denied", StringComparison.Ordinal), "a generic access failure is not mislabeled as microphone permission denial");
            var deniedMicrophone = new AudioDeviceSelectionException("PRIVATE device description") { NativeCode = -6 };
            Check(deniedMicrophone.NativeCode == -6 && LocalHost.SupportFailureLine(deniedMicrophone)
                .Contains("code=microphone-access-denied", StringComparison.Ordinal) &&
                LocalHost.SupportFailureLine(deniedMicrophone).EndsWith(" native=-6", StringComparison.Ordinal),
                "the actual audio API access-denied result is preserved without parsing its description");
            Check(LocalHost.SupportFailureLine(new AudioDeviceSelectionException("PRIVATE") { NativeCode = -204 })
                .EndsWith(" native=-204", StringComparison.Ordinal), "the actual no-device audio result is retained separately from a generic HRESULT");
            string socketFailure = LocalHost.SupportFailureLine(new System.Net.Sockets.SocketException(10061));
            Check(socketFailure.Contains("code=endpoint-unavailable", StringComparison.Ordinal) && socketFailure.EndsWith(" native=10061", StringComparison.Ordinal),
                "a typed socket failure retains its real numeric native error");

            LocalHost.Root = root;
            LocalHost.LogPath = path;
            string legacy = Path.Combine(root, "vrchat-translation-settings.json");
            string projection = Path.Combine(root, "own-voice-runtime.json");
            File.WriteAllText(legacy, JsonSerializer.Serialize(new { CustomProfiles = new string('x', 70000) }));
            File.WriteAllText(projection, """{"OwnRecognitionEngine":"nemotron","OwnVoiceSourceLanguage":"Chinese","OwnVoiceTargetLanguage":"English","OwnVoiceAdditionalLanguagesEnabled":true,"OwnVoiceAdditionalTargetLanguages":["Japanese","Korean"],"OwnVoicePauseWhenVrchatMuted":false,"FinishCapturedSentenceOnMute":false,"OwnVoiceEndPauseMilliseconds":1800,"OwnVoiceSendEarlyPreviews":true,"UseManagedTranslator":true,"Glossary":"synthetic terms"}""");
            Check(LocalHost.ReadRecognitionPreferences() == new LocalHost.OwnRecognitionPreferences(true, "zh-CN"),
                "native recognition reads the small projection despite an oversized valid dashboard profile library");
            var languages = LocalHost.ReadOwnVoiceLanguages();
            Check(languages.Targets.SequenceEqual(new[] { "English", "Japanese", "Korean" }) && languages.ManagedTranslator && languages.Glossary == "synthetic terms",
                "three chosen target languages and translator preferences come from the same small projection");
            Check(!LocalHost.FinishCaptured() && LocalHost.EarlyPreviewsEnabled() &&
                LocalHost.ReadLivePreferences(new(1200, false)) == new LocalHost.OwnVoiceLivePreferences(1800, true),
                "finish, preview and live timing readers also use projected preferences");
            File.WriteAllText(legacy, """{"OwnRecognitionEngine":"nemotron","OwnVoiceSourceLanguage":"English"}""");
            File.WriteAllText(projection, "[]");
            bool invalidProjectionRejected = false;
            try { _ = LocalHost.ReadRecognitionPreferences(); } catch (JsonException) { invalidProjectionRejected = true; }
            Check(invalidProjectionRejected, "a malformed present projection cannot silently revive stale legacy recognition settings");
            File.Delete(projection);
            Check(LocalHost.ReadRecognitionPreferences() == new LocalHost.OwnRecognitionPreferences(true, "en-US"),
                "standalone legacy roots are read when the projection is genuinely absent");
            File.Delete(legacy);
            Check(LocalHost.ReadRecognitionPreferences() == new LocalHost.OwnRecognitionPreferences(false, "auto"),
                "an old root without any preferences retains the historical recognizer default");
            LocalHost.Root = previousRoot;
            LocalHost.LogPath = previousLog;

            File.WriteAllText(path, "kept fixture\n", new UTF8Encoding(false));
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                logger.Write(path, "blocked fixture event");
                logger.Write(path, "another blocked fixture event");
                Check(console.Count(line => line.StartsWith("WARNING own-voice: code=log-write-unavailable;", StringComparison.Ordinal)) == 1,
                    "an exclusive log lock is nonfatal and its stdout warning is rate limited");
                Check(console.Contains("blocked fixture event") && console.Contains("another blocked fixture event"),
                    "ordinary metadata events still reach the independent stdout sink");

                LocalHost.LogPath = path;
                using var unavailableOutput = new ThrowingOutput();
                Console.SetOut(unavailableOutput);
                var turn = new CapturedTurn(new(16000, 16, 1));
                turn.TryWrite(new(new byte[640], turn.Format), voiced: true);
                turn.Complete(verifiedQuiet: true);
                var sent = new List<string>();
                bool completed = await LocalHost.ProcessCapturedTurnAsync(turn, Task.CompletedTask, () =>
                {
                    LocalHost.Log("isolated synthetic translation completed");
                    sent.Add("synthetic translated output");
                    return Task.CompletedTask;
                }, CancellationToken.None);
                Check(completed && sent.SequenceEqual(new[] { "synthetic translated output" }),
                    "the real captured-turn wrapper reaches its output callback despite both locked log and broken stdout");
                Check(unavailableOutput.Attempts > 0, "the simulated redirected stdout really failed during the turn");
                Console.SetOut(previousOutput);
                LocalHost.LogPath = previousLog;
            }
            logger.Write(path, "recovered UTF8 fixture: 中文 한국어 日本語");
            Check(File.ReadAllText(path, Encoding.UTF8).Contains("kept fixture\n") &&
                File.ReadAllText(path, Encoding.UTF8).Contains("recovered UTF8 fixture: 中文 한국어 日本語"),
                "UTF8 logging resumes without replacing previous content after the lock is released");

            File.SetAttributes(path, FileAttributes.ReadOnly);
            logger.Write(path, "readonly fixture event");
            Check(console.Contains("readonly fixture event"), "read-only log access cannot prevent independent stdout reporting");
            Check(!File.ReadAllText(path, Encoding.UTF8).Contains("readonly fixture event"),
                "the read-only fixture really prevents the attempted file append");
            File.SetAttributes(path, FileAttributes.Normal);

            var silentConsole = new BestEffortHostLogger(_ => throw new IOException("synthetic unavailable output"));
            silentConsole.Write(path, "file survives stdout failure");
            Check(File.ReadAllText(path, Encoding.UTF8).Contains("file survives stdout failure"),
                "a redirected stdout I/O failure does not prevent the independent file append");
            var deniedConsole = new BestEffortHostLogger(_ => throw new UnauthorizedAccessException("synthetic denied output"));
            deniedConsole.Write(path, "file survives denied stdout");
            Check(File.ReadAllText(path, Encoding.UTF8).Contains("file survives denied stdout"),
                "stdout access denial also cannot abort logging or a translation caller");

            bool unexpectedEscaped = false;
            var programmingFailure = new BestEffortHostLogger(_ => throw new InvalidOperationException("synthetic programming failure"));
            try { programmingFailure.Write(path, "must not hide a programming error"); }
            catch (InvalidOperationException) { unexpectedEscaped = true; }
            Check(unexpectedEscaped, "non-I/O programming exceptions are still surfaced");
            unexpectedEscaped = false;
            try { logger.Write(path + '\0', "invalid path fixture"); }
            catch (ArgumentException) { unexpectedEscaped = true; }
            Check(unexpectedEscaped, "invalid path arguments are not mistaken for transient file errors");
        }
        finally
        {
            Console.SetOut(previousOutput);
            LocalHost.LogPath = previousLog;
            LocalHost.Root = previousRoot;
            foreach (string preference in new[] { "own-voice-runtime.json", "vrchat-translation-settings.json" })
            {
                string preferencePath = Path.Combine(root, preference);
                if (File.Exists(preferencePath)) File.Delete(preferencePath);
            }
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            Directory.Delete(root);
        }
        Console.WriteLine($"PASS: {checks} isolated host logging/output checks; no audio, model or live protocol was used.");
    }

    private sealed class ThrowingOutput : TextWriter
    {
        public int Attempts { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("synthetic unavailable redirected stdout");
        }
    }
}

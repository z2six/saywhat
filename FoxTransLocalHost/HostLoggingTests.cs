using System.Text;

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
        TextWriter previousOutput = Console.Out;
        try
        {
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

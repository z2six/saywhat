using System.Text;

namespace IncomingTranslator;

// Isolated protocol checks use only metadata fakes and memory streams. They never
// enumerate live processes, open a real pipe, initialize AppPaths, or start tools.
internal static class InstallerShutdownOfflineTests
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Installer shutdown check failed: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); } catch (ArgumentException) { Check(true, message); return; }
            Check(false, message);
        }
        const string app = @"C:\OfflineFixture\SayWhat.exe";
        const string other = @"C:\OtherFixture\SayWhat.exe";
        var parsed = InstallerShutdownProtocol.ParseClientArguments(["--installer-shutdown", "--installation-path", app]);
        Check(parsed == (app, 75), "the control path has one exact installation and a bounded default wait");
        Check(InstallerShutdownProtocol.ParseClientArguments(["--installer-shutdown", "--installation-path", app, "--timeout-seconds", "120"]).TimeoutSeconds == 120,
            "an explicitly bounded wait is accepted");
        foreach (string invalid in new[] { @"SayWhat.exe", @"\\server\share\SayWhat.exe", @"C:\OfflineFixture\other.exe", @"C:\OfflineFixture\SayWhat.exe\", "" })
            Reject(() => InstallerShutdownProtocol.NormalizeExecutablePath(invalid), "unsafe/foreign executable paths are rejected");
        Reject(() => InstallerShutdownProtocol.ParseClientArguments(["--installer-shutdown", "--installation-path", app, "--timeout-seconds", "4"]), "short unbounded cleanup is not accepted");
        Reject(() => InstallerShutdownProtocol.ParseClientArguments(["--installer-shutdown", "--installation-path", app, "--timeout-seconds", "121"]), "excessive waits are not accepted");
        Reject(() => InstallerShutdownProtocol.ParseClientArguments(["--installer-shutdown", "--installation-path", app, "--unexpected"]), "unknown arguments do not enter normal app startup");
        Check(InstallerShutdownProtocol.PipeName(app, 2) == InstallerShutdownProtocol.PipeName(app.ToLowerInvariant(), 2), "Windows path casing has one pipe identity");
        Check(InstallerShutdownProtocol.PipeName(app, 2) != InstallerShutdownProtocol.PipeName(other, 2), "different installations do not share a shutdown pipe");
        Check(InstallerShutdownProtocol.PipeName(app, 2) != InstallerShutdownProtocol.PipeName(app, 3), "different Windows sessions do not share a shutdown pipe");
        var request = new InstallerShutdownRequest(1, InstallerShutdownProtocol.QuitCommand, app, Guid.NewGuid().ToString("N"));
        Check(InstallerShutdownProtocol.IsValidRequest(request, app), "the exact installation request is valid");
        Check(!InstallerShutdownProtocol.IsValidRequest(request with { Version = 2 }, app), "unknown protocol versions are refused");
        Check(!InstallerShutdownProtocol.IsValidRequest(request with { Command = "kill" }, app), "force-stop commands are not supported");
        Check(!InstallerShutdownProtocol.IsValidRequest(request with { Nonce = "not-a-nonce" }, app), "malformed request IDs are refused");
        Check(!InstallerShutdownProtocol.IsValidRequest(request with { ExecutablePath = other }, app), "another installation cannot request quit");
        Check(InstallerShutdownProtocol.Deserialize<InstallerShutdownRequest>(InstallerShutdownProtocol.Serialize(request)) == request,
            "bounded request serialization preserves the identity and nonce");

        var platform = new FakePlatform(app);
        var result = await Run(platform).ConfigureAwait(false);
        Check(result.ExitCode == 0 && platform.RequestCount == 0 && platform.WaitCount == 0, "a stopped app requires no request and starts nothing");
        platform = new FakePlatform(app) { Instances = [new(10, 2, app)] };
        result = await Run(platform).ConfigureAwait(false);
        Check(result.ExitCode == 0 && platform.RequestCount == 0, "the client does not request shutdown of itself");
        platform = new FakePlatform(app) { Instances = [new(20, 2, other)] };
        result = await Run(platform).ConfigureAwait(false);
        Check(result.ExitCode == 0 && platform.RequestCount == 0, "a known unrelated installation is not stopped");
        platform = new FakePlatform(other) { Instances = [new(20, 2, app)] };
        result = await Run(platform).ConfigureAwait(false);
        Check(result.ExitCode == 22 && platform.RequestCount == 0, "a foreign helper cannot target another installation");
        platform = new FakePlatform(app) { Instances = [new(20, 2, null)] };
        result = await Run(platform).ConfigureAwait(false);
        Check(result.ExitCode == 26 && platform.RequestCount == 0, "unreadable process metadata fails closed");
        platform = new FakePlatform(app) { Instances = [new(20, 3, app)] };
        result = await Run(platform).ConfigureAwait(false);
        Check(result.ExitCode == 25 && platform.RequestCount == 0, "another user's/session's app is never stopped");

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        platform = new FakePlatform(app) { Instances = [new(20, 2, app)], ExitGate = exited.Task };
        Task<InstallerShutdownResult> pending = Run(platform);
        Check(platform.RequestCount == 1 && platform.WaitCount == 1 && !pending.IsCompleted,
            "acknowledgement alone never claims DLLs, models or process handles were released");
        exited.SetResult();
        result = await pending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Check(result.ExitCode == 0 && platform.Instances.Count == 0, "completion waits for the exact process to exit and rescans");
        foreach (string corruption in new[] { "version", "status", "pid", "path", "nonce" })
        {
            platform = new FakePlatform(app) { Instances = [new(20, 2, app)], CorruptAcknowledgement = corruption };
            result = await Run(platform).ConfigureAwait(false);
            Check(result.ExitCode == 24 && platform.WaitCount == 0, "bad acknowledgement " + corruption + " is refused without waiting/killing");
        }
        platform = new FakePlatform(app) { Instances = [new(20, 2, app)], Reopen = true };
        result = await Run(platform).ConfigureAwait(false);
        Check(result.ExitCode == 24 && platform.WaitCount == 1, "an app reopened during cleanup prevents installation success");
        foreach ((Exception failure, int code) in new (Exception, int)[]
        {
            (new TimeoutException(), 21), (new IOException(), 21), (new UnauthorizedAccessException(), 25), (new OperationCanceledException(), 23)
        })
        {
            platform = new FakePlatform(app) { Instances = [new(20, 2, app)], Failure = failure };
            result = await Run(platform).ConfigureAwait(false);
            Check(result.ExitCode == code && !string.IsNullOrWhiteSpace(result.Message) && platform.WaitCount == 0,
                "legacy/unavailable/denied/timed-out shutdown has honest manual-close feedback");
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); platform = new FakePlatform(app) { Instances = [new(20, 2, app)] };
            try { await InstallerShutdownProtocol.RunClientAsync(app, TimeSpan.FromSeconds(5), platform, canceled.Token).ConfigureAwait(false); Check(false, "caller cancellation propagates"); }
            catch (OperationCanceledException) { Check(platform.RequestCount == 0, "canceled requests do not contact the app"); }
        }
        foreach (string line in new[] { "hello\n", "hello\r\n" })
        {
            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(line)));
            Check(await InstallerShutdownProtocol.ReadBoundedLineAsync(reader, CancellationToken.None).ConfigureAwait(false) == "hello", "LF/CRLF framed messages decode exactly");
        }
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', InstallerShutdownProtocol.MaximumLineLength + 1) + "\n"))))
        {
            try { await InstallerShutdownProtocol.ReadBoundedLineAsync(reader, CancellationToken.None).ConfigureAwait(false); Check(false, "oversized messages are refused"); }
            catch (InvalidDataException) { Check(true, "oversized messages are refused"); }
        }
        using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("incomplete"))))
        {
            try { await InstallerShutdownProtocol.ReadBoundedLineAsync(reader, CancellationToken.None).ConfigureAwait(false); Check(false, "incomplete messages are refused"); }
            catch (EndOfStreamException) { Check(true, "incomplete messages are refused"); }
        }
        return checks;

        Task<InstallerShutdownResult> Run(FakePlatform fake) => InstallerShutdownProtocol.RunClientAsync(app, TimeSpan.FromSeconds(5), fake, CancellationToken.None);
    }

    private sealed class FakePlatform(string executable) : IInstallerShutdownPlatform
    {
        public int CurrentProcessId => 10;
        public int CurrentSessionId => 2;
        public string CurrentExecutablePath => executable;
        internal IReadOnlyList<InstallerAppInstance> Instances { get; set; } = [];
        internal Task ExitGate { get; set; } = Task.CompletedTask;
        internal Exception? Failure { get; set; }
        internal string? CorruptAcknowledgement { get; set; }
        internal bool Reopen { get; set; }
        internal int RequestCount { get; private set; }
        internal int WaitCount { get; private set; }
        public IReadOnlyList<InstallerAppInstance> FindAppInstances() => Instances;
        public Task<InstallerShutdownAcknowledgement> RequestQuitAsync(InstallerAppInstance instance, InstallerShutdownRequest request, CancellationToken token)
        {
            RequestCount++;
            if (Failure is not null) return Task.FromException<InstallerShutdownAcknowledgement>(Failure);
            var ack = new InstallerShutdownAcknowledgement(1, "accepted", instance.ProcessId, executable, request.Nonce);
            ack = CorruptAcknowledgement switch
            {
                "version" => ack with { Version = 2 }, "status" => ack with { Status = "rejected" }, "pid" => ack with { ProcessId = 42 },
                "path" => ack with { ExecutablePath = @"C:\OtherFixture\SayWhat.exe" }, "nonce" => ack with { Nonce = Guid.NewGuid().ToString("N") }, _ => ack
            };
            return Task.FromResult(ack);
        }
        public async Task WaitForExitAsync(InstallerAppInstance instance, CancellationToken token)
        {
            WaitCount++;
            await ExitGate.WaitAsync(token).ConfigureAwait(false);
            Instances = Reopen ? [new(21, 2, executable)] : [];
        }
    }
}

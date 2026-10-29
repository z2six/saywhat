using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace IncomingTranslator;

internal sealed record InstallerShutdownRequest(int Version, string Command, string ExecutablePath, string Nonce);
internal sealed record InstallerShutdownAcknowledgement(int Version, string Status, int ProcessId, string ExecutablePath, string Nonce);
internal sealed record InstallerAppInstance(int ProcessId, int SessionId, string? ExecutablePath, long StartedAtTicks = 0);
internal sealed record InstallerShutdownResult(int ExitCode, string Message);

internal interface IInstallerShutdownPlatform
{
    int CurrentProcessId { get; }
    int CurrentSessionId { get; }
    string CurrentExecutablePath { get; }
    IReadOnlyList<InstallerAppInstance> FindAppInstances();
    Task<InstallerShutdownAcknowledgement> RequestQuitAsync(InstallerAppInstance instance, InstallerShutdownRequest request, CancellationToken token);
    Task WaitForExitAsync(InstallerAppInstance instance, CancellationToken token);
}

// This control path runs BEFORE the desktop mutex, AppPaths, WPF, audio and models.
// It can request only this exact installation's graceful quit; it never kills a process.
internal static class InstallerShutdownProtocol
{
    internal const int ProtocolVersion = 1;
    internal const string QuitCommand = "quit-for-installer";
    internal const string MinimumAppVersion = "0.1.11";
    internal const int DefaultTimeoutSeconds = 75;
    internal const int MaximumLineLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };

    internal static bool TryHandleClientCommand(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains("--installer-shutdown", StringComparer.Ordinal)) return false;
        try
        {
            (string executable, int timeout) = ParseClientArguments(args);
            var platform = new WindowsInstallerShutdownPlatform();
            EnsurePlainExecutable(executable);
            InstallerShutdownResult result = RunClientAsync(executable, TimeSpan.FromSeconds(timeout), platform, CancellationToken.None)
                .GetAwaiter().GetResult();
            exitCode = result.ExitCode;
            if (exitCode != 0) Console.Error.WriteLine(result.Message);
        }
        catch (Exception error)
        {
            // No log/data-root initialization or interactive error window in installer mode.
            Console.Error.WriteLine("The app could not be closed safely. Quit SayWhat? from its tray menu, then retry setup. " + error.Message);
            exitCode = 20;
        }
        return true;
    }

    internal static (string ExecutablePath, int TimeoutSeconds) ParseClientArguments(IReadOnlyList<string> args)
    {
        if (args.Count is not 3 and not 5 || args[0] != "--installer-shutdown" || args[1] != "--installation-path")
            throw new ArgumentException("Installer shutdown requires one exact installation path.");
        int timeout = DefaultTimeoutSeconds;
        if (args.Count == 5 && (args[3] != "--timeout-seconds" ||
            !int.TryParse(args[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out timeout) ||
            timeout is < 5 or > 120)) throw new ArgumentException("The installer shutdown timeout must be between 5 and 120 seconds.");
        return (NormalizeExecutablePath(args[2]), timeout);
    }

    internal static string NormalizeExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\0') ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Choose an exact app executable on a local Windows drive.");
        string normalized = Path.GetFullPath(path);
        string name = Path.GetFileName(normalized);
        if ((!name.Equals("SayWhat.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("LocalTalk.exe", StringComparison.OrdinalIgnoreCase)) ||
            Path.GetPathRoot(normalized)?.Length != 3)
            throw new ArgumentException("The installer shutdown target must be SayWhat.exe in its installation folder.");
        return normalized;
    }

    internal static string PipeName(string executablePath, int sessionId)
    {
        if (sessionId < 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
        string identity = NormalizeExecutablePath(executablePath).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "SayWhat.InstallerQuit.v1." + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + hash;
    }

    internal static bool IsValidRequest(InstallerShutdownRequest? request, string expectedExecutable) => request is not null &&
        request.Version == ProtocolVersion && request.Command == QuitCommand && Guid.TryParseExact(request.Nonce, "N", out _) &&
        SameExecutable(request.ExecutablePath, expectedExecutable);

    internal static bool SameExecutable(string? actual, string expected)
    {
        try { return actual is not null && string.Equals(NormalizeExecutablePath(actual), NormalizeExecutablePath(expected), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    internal static async Task<InstallerShutdownResult> RunClientAsync(string executablePath, TimeSpan timeout,
        IInstallerShutdownPlatform platform, CancellationToken token)
    {
        string target = NormalizeExecutablePath(executablePath);
        if (!SameExecutable(platform.CurrentExecutablePath, target))
            return new(22, "The shutdown helper does not belong to the selected installation. No app was stopped.");
        if (timeout < TimeSpan.FromSeconds(5) || timeout > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        token.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            IReadOnlyList<InstallerAppInstance> discovered = platform.FindAppInstances();
            if (discovered.Any(instance => instance.ProcessId != platform.CurrentProcessId && instance.ExecutablePath is null))
                return new(26, "A running app could not be identified safely. Close all SayWhat? instances, then retry setup.");
            InstallerAppInstance[] targets = discovered.Where(instance => instance.ProcessId != platform.CurrentProcessId && SameExecutable(instance.ExecutablePath, target)).ToArray();
            if (targets.Any(instance => instance.SessionId != platform.CurrentSessionId))
                return new(25, "This installation is open in another Windows session. Ask that user to quit SayWhat?, then retry setup.");
            foreach (InstallerAppInstance instance in targets)
            {
                var request = new InstallerShutdownRequest(ProtocolVersion, QuitCommand, target, Guid.NewGuid().ToString("N"));
                InstallerShutdownAcknowledgement acknowledgement = await platform.RequestQuitAsync(instance, request, limit.Token).ConfigureAwait(false);
                if (acknowledgement.Version != ProtocolVersion || acknowledgement.Status != "accepted" ||
                    acknowledgement.ProcessId != instance.ProcessId || acknowledgement.Nonce != request.Nonce ||
                    !SameExecutable(acknowledgement.ExecutablePath, target))
                    return new(24, "The app did not acknowledge a safe shutdown. Quit SayWhat? from its tray menu, then retry setup.");
                // Acknowledgement is NOT completion. The exact process must release its
                // mutex, DLLs and model/job ownership before setup may replace app files.
                await platform.WaitForExitAsync(instance, limit.Token).ConfigureAwait(false);
            }
            if (platform.FindAppInstances().Any(instance => instance.ProcessId != platform.CurrentProcessId &&
                (instance.ExecutablePath is null || SameExecutable(instance.ExecutablePath, target))))
                return new(24, "The app is still open or was reopened. Quit SayWhat?, then retry setup. No files were changed.");
            return new(0, targets.Length == 0 ? "This installation is not running." : "SayWhat? finished closing safely.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(23, "The app has not finished closing. Wait for translation tools to stop or choose Retry quit in the app, then retry setup."); }
        catch (TimeoutException)
        { return new(21, "This app version did not respond to the shutdown request. Choose Quit SayWhat? in its tray menu, then retry setup."); }
        catch (IOException)
        { return new(21, "The app could not be contacted safely. Choose Quit SayWhat? in its tray menu, then retry setup."); }
        catch (UnauthorizedAccessException)
        { return new(25, "The app belongs to another Windows account or cannot be contacted. Quit it from that account, then retry setup."); }
    }

    private static void EnsurePlainExecutable(string path)
    {
        string? current = NormalizeExecutablePath(path);
        if (!File.Exists(current)) throw new FileNotFoundException("The selected app executable is missing.", current);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked installation folders are not eligible for automatic shutdown.");
            current = Path.GetDirectoryName(current);
        }
    }

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    internal static T? Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, JsonOptions);
    internal static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= MaximumLineLength) throw new InvalidDataException("Installer shutdown message exceeded its safe limit.");
            text.Append(character[0]);
        }
        throw new EndOfStreamException("Installer shutdown connection closed before a complete message.");
    }

    internal sealed class WindowsInstallerShutdownPlatform : IInstallerShutdownPlatform
    {
        public int CurrentProcessId => Environment.ProcessId;
        public int CurrentSessionId { get { using var process = Process.GetCurrentProcess(); return process.SessionId; } }
        public string CurrentExecutablePath => Environment.ProcessPath ?? throw new IOException("The current executable path could not be determined.");
        public IReadOnlyList<InstallerAppInstance> FindAppInstances()
        {
            var instances = new List<InstallerAppInstance>();
            foreach (string name in new[] { "SayWhat", "LocalTalk" })
                foreach (Process process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        if (process.Id == CurrentProcessId) continue;
                        try
                        {
                            if (!process.HasExited) instances.Add(new(process.Id, process.SessionId, process.MainModule?.FileName, process.StartTime.ToUniversalTime().Ticks));
                        }
                        catch (InvalidOperationException) { /* This process exited during read-only discovery. */ }
                        catch (System.ComponentModel.Win32Exception) { instances.Add(new(process.Id, -1, null)); }
                    }
            return instances;
        }
        public async Task<InstallerShutdownAcknowledgement> RequestQuitAsync(InstallerAppInstance instance, InstallerShutdownRequest request, CancellationToken token)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(request.ExecutablePath, CurrentSessionId), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != instance.ProcessId)
                throw new IOException("The named-pipe server is not the retained app process.");
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(Serialize(request).AsMemory(), token).ConfigureAwait(false);
            string response = await ReadBoundedLineAsync(reader, token).ConfigureAwait(false);
            return Deserialize<InstallerShutdownAcknowledgement>(response) ?? throw new IOException("The app sent no acknowledgement.");
        }
        public async Task WaitForExitAsync(InstallerAppInstance instance, CancellationToken token)
        {
            Process process;
            try { process = Process.GetProcessById(instance.ProcessId); }
            catch (ArgumentException) { return; }
            using (process)
            {
                if (process.HasExited) return;
                if (process.StartTime.ToUniversalTime().Ticks != instance.StartedAtTicks) return;
                if (!SameExecutable(process.MainModule?.FileName, instance.ExecutablePath!)) throw new IOException("The app process identity changed.");
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}

// One current-user-only listener for the live desktop. No endpoint is created by
// the CLI client, offline preview or an unshown window. Disposal cancels accept/read.
internal sealed class InstallerShutdownServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly string _executable;
    private readonly int _session;
    private readonly Action _requestQuit;
    private readonly Task _listener;
    private int _disposed;
    internal InstallerShutdownServer(string executable, int session, Action requestQuit)
    {
        _executable = InstallerShutdownProtocol.NormalizeExecutablePath(executable);
        _session = session; _requestQuit = requestQuit;
        _listener = ListenAsync();
        _ = ObserveAsync(_listener);
    }
    internal Task Completion => _listener;
    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(InstallerShutdownProtocol.PipeName(_executable, _session), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                string raw = await InstallerShutdownProtocol.ReadBoundedLineAsync(reader, requestTimeout.Token).ConfigureAwait(false);
                InstallerShutdownRequest? request = InstallerShutdownProtocol.Deserialize<InstallerShutdownRequest>(raw);
                bool valid = InstallerShutdownProtocol.IsValidRequest(request, _executable) && IsExactInstalledClient(pipe);
                var acknowledgement = new InstallerShutdownAcknowledgement(InstallerShutdownProtocol.ProtocolVersion,
                    valid ? "accepted" : "rejected", Environment.ProcessId, _executable, request?.Nonce ?? "");
                await writer.WriteLineAsync(InstallerShutdownProtocol.Serialize(acknowledgement).AsMemory(), requestTimeout.Token).ConfigureAwait(false);
                if (valid) _requestQuit(); // Existing dispatcher/lifetime owns cleanup, never this I/O thread.
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { /* A stalled request is bounded; keep serving valid clients. */ }
            catch (IOException)
            {
                if (_stop.IsCancellationRequested) break;
                // A busy/failed pipe must not turn into an unbounded CPU retry loop.
                try { await Task.Delay(250, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (JsonException) { /* Refuse malformed local messages without touching the runtime. */ }
            catch (UnauthorizedAccessException) { break; }
        }
    }
    private bool IsExactInstalledClient(NamedPipeServerStream pipe)
    {
        if (!InstallerShutdownProtocol.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) return false;
        try
        {
            using Process process = Process.GetProcessById(checked((int)pid));
            return process.SessionId == _session && InstallerShutdownProtocol.SameExecutable(process.MainModule?.FileName, _executable);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }
    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) { try { DesktopLog.Write("Installer shutdown listener stopped: " + error.GetType().Name); } catch { } }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ = DisposeAfterAsync();
    }
    private async Task DisposeAfterAsync()
    {
        try { await _listener.ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }
}

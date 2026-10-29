using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text;

namespace IncomingTranslator;

internal enum ManagedServiceKind { OwnSpeech, IncomingSpeech, OwnTranslation, IncomingTranslation, GroupsSpeech }

internal sealed class ManagedPortConfigurationException : ArgumentException
{
    internal ManagedPortConfigurationException(string template, params object?[] arguments)
        : base(string.Format(CultureInfo.InvariantCulture, template, arguments))
    { Template = template; Arguments = Array.AsReadOnly((object?[])arguments.Clone()); }
    internal string Template { get; }
    internal IReadOnlyList<object?> Arguments { get; }
}

internal sealed class ManagedPortConflictException : IOException
{
    internal ManagedPortConflictException(string service, int port, Exception? inner = null)
        : base(string.Format(CultureInfo.InvariantCulture, MessageTemplate(port), service, port), inner)
    { Service = service; Port = port; }
    internal string Service { get; }
    internal int Port { get; }
    internal string Template => MessageTemplate(Port);
    private static string MessageTemplate(int port) => port == 0
        ? "Windows couldn't reserve a local connection for {0}. Try again or open Troubleshooting."
        : "{0} couldn't use local port {1}. Another app or Windows may be using it. Choose Automatic in Connection settings, or select another port.";
}

/// <summary>
/// An exclusive IPv4-loopback bind, not a running HTTP listener. Keep this lease
/// until immediately before starting the owned child. Child servers do not
/// inherit its socket, so a release/start race remains and must be handled by
/// checking exact child ownership and retrying only verified bind conflicts.
/// </summary>
internal sealed class ManagedPortReservation : IDisposable
{
    private Socket? _socket;
    private readonly int _port;
    internal ManagedPortReservation(Socket socket)
    {
        _socket = socket;
        _port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
    internal int Port => _port;
    internal IPEndPoint EndPoint => new(IPAddress.Loopback, _port);
    internal bool IsReleased => Volatile.Read(ref _socket) is null;
    internal void ReleaseForStart() => Interlocked.Exchange(ref _socket, null)?.Dispose();
    public void Dispose() => ReleaseForStart();
}

internal static class ManagedPortAllocator
{
    internal const int MinimumFixedPort = 1024;
    internal const int MaximumPort = 65535;
    internal const int MaximumStartAttempts = 3;

    internal static ManagedPortReservation Reserve(bool automatic, int fixedPort, string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        if (!automatic) ValidatePort(fixedPort, service);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.ExclusiveAddressUse = true;
            // Port zero asks Windows to choose a currently bindable port. This
            // respects occupied/excluded ranges without scanning other services.
            socket.Bind(new IPEndPoint(IPAddress.Loopback, automatic ? 0 : fixedPort));
            return new(socket);
        }
        catch (SocketException error)
        {
            socket.Dispose();
            throw new ManagedPortConflictException(service, automatic ? 0 : fixedPort, error);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static void EnsureBindable(int port, string service)
    {
        using var reservation = Reserve(automatic: false, port, service);
    }

    // The callback owns its process and readiness proof. Before throwing a bind
    // conflict it must stop ONLY that owned child, then check bindability. A
    // generic model/driver/health error must never be reclassified as contention.
    internal static async Task<T> ExecuteWithRetryAsync<T>(bool automatic, int fixedPort, string service,
        Func<ManagedPortReservation, CancellationToken, Task<T>> start, CancellationToken token = default,
        int maximumAttempts = MaximumStartAttempts)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (maximumAttempts is < 1 or > MaximumStartAttempts)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts), "Managed startup allows one to three attempts.");
        int attempts = automatic ? maximumAttempts : 1;
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var reservation = Reserve(automatic, fixedPort, service);
                token.ThrowIfCancellationRequested();
                return await start(reservation, token).ConfigureAwait(false);
            }
            catch (ManagedPortConflictException)
            {
                token.ThrowIfCancellationRequested();
                if (!automatic || attempt >= attempts) throw;
                // Windows chooses again; never adopt or stop the winning foreign listener.
            }
        }
    }

    internal static void ValidatePort(int port, string service)
    {
        if (port is < MinimumFixedPort or > MaximumPort)
            throw new ManagedPortConfigurationException("Choose a local port from {0} to {1} for {2}, or turn on Automatic in Connection settings.", MinimumFixedPort, MaximumPort, service);
    }

    internal static int ConfiguredPort(AppSettings settings, ManagedServiceKind service) => service switch
    {
        ManagedServiceKind.OwnSpeech => settings.OwnSpeechPort,
        ManagedServiceKind.IncomingSpeech => settings.IncomingSpeechPort,
        ManagedServiceKind.OwnTranslation => settings.OwnTranslationPort,
        ManagedServiceKind.IncomingTranslation => settings.IncomingTranslationPort,
        ManagedServiceKind.GroupsSpeech => settings.GroupsSpeechPort,
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    internal static string ServiceName(ManagedServiceKind service) => service switch
    {
        ManagedServiceKind.OwnSpeech => "Your voice recognition",
        ManagedServiceKind.IncomingSpeech => "Subtitle speech recognition",
        ManagedServiceKind.OwnTranslation => "Your voice translator",
        ManagedServiceKind.IncomingTranslation => "Subtitle translator",
        ManagedServiceKind.GroupsSpeech => "Groups speech recognition",
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    // A caller may validate just the services about to run, so an unused optional
    // subtitle preference cannot block an otherwise valid microphone-only start.
    // With no subset, this is the explicit fixed-settings editor's full check.
    internal static void ValidateConfiguration(AppSettings settings, params ManagedServiceKind[] requiredServices)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AutomaticManagedPorts) return;
        IEnumerable<ManagedServiceKind> services = requiredServices.Length > 0 ? requiredServices : Enum.GetValues<ManagedServiceKind>();
        var used = new Dictionary<int, ManagedServiceKind>();
        foreach (ManagedServiceKind service in services.Distinct())
        {
            if (!settings.UseManagedTranslator && service is ManagedServiceKind.OwnTranslation or ManagedServiceKind.IncomingTranslation) continue;
            if (service == ManagedServiceKind.IncomingTranslation && settings.ShareTranslationModel) continue;
            int port = ConfiguredPort(settings, service);
            ValidatePort(port, ServiceName(service));
            if (used.TryGetValue(port, out ManagedServiceKind other))
                throw new ManagedPortConfigurationException("{0} and {1} use the same local port {2}. Choose different ports, or turn on Automatic in Connection settings.", ServiceName(other), ServiceName(service), port);
            used.Add(port, service);
        }
    }

    internal const int MaximumOwnershipOutputCharacters = 1024 * 1024;

    // The signed Windows utility supplies ownership metadata without custom
    // native-memory access. Fixed numeric TCP arguments perform no connection
    // probe. Its snapshot is kept only for this check and never written to logs.
    internal static async Task<bool> IsOwnedTcpListenerAsync(int port, int processId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (port is < MinimumFixedPort or > MaximumPort || processId <= 0 || !OperatingSystem.IsWindows()) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process { StartInfo = OwnershipProbeInfo() };
        bool started = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) return false;
            started = true;
            Task<string> output = ReadBoundedOwnershipOutputAsync(process.StandardOutput, MaximumOwnershipOutputCharacters, deadline.Token);
            Task<string> errors = ReadBoundedOwnershipOutputAsync(process.StandardError, 16 * 1024, deadline.Token);
            Task completion = Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token));
            // Bound the whole operation even if a pipe read cannot observe its
            // cancellation until the retained utility is killed in finally.
            _ = completion.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return process.ExitCode == 0 && string.IsNullOrWhiteSpace(await errors.ConfigureAwait(false)) &&
                ContainsOwnedListener(await output.ConfigureAwait(false), port, processId);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return false; }
        finally
        {
            if (started)
            {
                // This is the exact utility Process we started, not a PID or
                // process-name search; no model/application process is affected.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: false);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            }
        }
    }

    internal static ProcessStartInfo OwnershipProbeInfo()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netstat.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-ano", "-p", "tcp" }) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<string> ReadBoundedOwnershipOutputAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder(Math.Min(limit, 4096));
        char[] buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (text.Length > limit - read) throw new InvalidDataException("Windows connection metadata exceeded its safe size.");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    // This parser is exclusively for `netstat -ano -p tcp` (never -q). In that
    // output a TCP peer of 0.0.0.0:0 denotes a listener, so localized state labels
    // need not be interpreted. Headers and non-TCP rows supply no proof.
    internal static bool ContainsOwnedListener(string? snapshot, int port, int processId)
    {
        if (string.IsNullOrEmpty(snapshot) || snapshot.Length > MaximumOwnershipOutputCharacters || snapshot.IndexOf('\0') >= 0 ||
            port is < MinimumFixedPort or > MaximumPort || processId <= 0) return false;
        bool owned = false;
        using var reader = new StringReader(snapshot);
        while (reader.ReadLine() is string line)
        {
            if (line.Length > 1024) return false;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || !fields[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields.Length is < 5 or > 8 || !TryNumeric(fields[^1], int.MaxValue, out int owner)) return false;
            int stateLength = fields.Skip(3).Take(fields.Length - 4).Sum(value => value.Length);
            if (stateLength is < 1 or > 64) return false;
            if (!TryIpv4Endpoint(fields[1], out string local, out int localPort) ||
                !TryIpv4Endpoint(fields[2], out string peer, out int peerPort)) return false;
            if (localPort != port || local is not ("127.0.0.1" or "0.0.0.0") || peer != "0.0.0.0" || peerPort != 0) continue;
            // SO_REUSEADDR must not let an owned row hide another reachable PID.
            if (owner != processId) return false;
            owned = true;
        }
        return owned;
    }

    private static bool TryIpv4Endpoint(string text, out string address, out int port)
    {
        address = ""; port = 0;
        int colon = text.IndexOf(':');
        if (colon <= 0 || colon != text.LastIndexOf(':') ||
            !TryNumeric(text[(colon + 1)..], MaximumPort, out port)) return false;
        string host = text[..colon];
        if (!IPAddress.TryParse(host, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork ||
            parsed.ToString() != host) return false;
        address = host;
        return true;
    }

    private static bool TryNumeric(string text, int maximum, out int value)
    {
        value = 0;
        return text.Length is > 0 and <= 10 && text.All(character => character is >= '0' and <= '9') &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum;
    }
}

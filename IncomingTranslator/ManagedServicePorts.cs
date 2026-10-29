using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Globalization;

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

    // A healthy HTTP response is insufficient proof after the reserved socket
    // has been handed off. Read the Windows ownership table, never connect to an
    // arbitrary endpoint, and accept only the exact native child PID. WSL's
    // Windows forwarding PID is different; its servers need owner-token proof.
    internal static bool IsOwnedTcpListener(int port, int processId)
    {
        if (port is < MinimumFixedPort or > MaximumPort || processId <= 0 || !OperatingSystem.IsWindows()) return false;
        int length = 0;
        const uint insufficientBuffer = 122;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref length, false, 2, 3, 0);
        if (result != insufficientBuffer || length < 4 || length > 16 * 1024 * 1024) return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int capacity = length;
            IntPtr buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                result = GetExtendedTcpTable(buffer, ref length, false, 2, 3, 0);
                if (result == insufficientBuffer)
                {
                    if (length < 4 || length > 16 * 1024 * 1024) return false;
                    continue;
                }
                if (result != 0 || length < 4 || length > capacity) return false;
                byte[] snapshot = new byte[length];
                Marshal.Copy(buffer, snapshot, 0, snapshot.Length);
                return ContainsOwnedListener(snapshot, port, processId);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }

    internal static bool ContainsOwnedListener(ReadOnlySpan<byte> table, int port, int processId)
    {
        if (table.Length < 4 || port is < MinimumFixedPort or > MaximumPort || processId <= 0) return false;
        uint rows = BinaryPrimitives.ReadUInt32LittleEndian(table);
        const int rowSize = 24;
        if (rows > (table.Length - 4) / rowSize) return false;
        bool owned = false;
        for (int index = 0; index < rows; index++)
        {
            ReadOnlySpan<byte> row = table.Slice(4 + index * rowSize, rowSize);
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(row);
            uint address = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            int localPort = BinaryPrimitives.ReadUInt16BigEndian(row[8..]);
            uint owner = BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
            // IP bytes are 127.0.0.1 (little-endian uint 0x0100007f) or wildcard.
            if (state == 2 && (address is 0 or 0x0100007f) && localPort == port)
            {
                // A child using SO_REUSEADDR must not count as exclusive proof
                // if another PID can also receive this same loopback address.
                if (owner != processId) return false;
                owned = true;
            }
        }
        return owned;
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int length,
        [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}

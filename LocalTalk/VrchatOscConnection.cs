using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

internal enum VrchatOscConnectionState { Checking, NotRunning, Unavailable, WrongPort, Connected, Error }
internal sealed record VrchatOscConnectionStatus(VrchatOscConnectionState State, int? OscPort = null);
internal sealed record VrchatProcessIdentity(int Id, long StartedUtcTicks);
internal sealed record VrchatOscSnapshot(IReadOnlyList<VrchatProcessIdentity> Processes,
    IReadOnlyList<int> QueryPorts, IReadOnlySet<int> UdpPorts, IReadOnlySet<int>? LocalQueryListeners = null);

// A read-only OSCQuery availability check, not a UDP delivery acknowledgment.
// Construction is inert. No models, capture, advertisements, or OSC packets are started.
internal sealed class VrchatOscConnectionProbe : IDisposable
{
    private const int MaximumResponseBytes = 16384;
    private readonly Func<VrchatOscSnapshot> _snapshot;
    private readonly Func<VrchatProcessIdentity, bool> _current;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private Task<VrchatOscSnapshot>? _pendingSnapshot;
    private readonly List<(VrchatProcessIdentity Process, int Port)> _verifiedPorts = new();

    public VrchatOscConnectionProbe() : this(ReadSnapshot, IsCurrent,
        new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { }

    internal VrchatOscConnectionProbe(Func<VrchatOscSnapshot> snapshot,
        Func<VrchatProcessIdentity, bool> current, HttpMessageHandler handler)
    {
        _snapshot = snapshot; _current = current;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<VrchatOscConnectionStatus> CheckAsync(CancellationToken token = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _disposed.Token);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(2500));
            VrchatOscSnapshot snapshot = await SnapshotAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            _verifiedPorts.RemoveAll(item => !snapshot.Processes.Contains(item.Process) ||
                snapshot.LocalQueryListeners?.Contains(item.Port) == false);
            if (snapshot.Processes.Count == 0) return new(VrchatOscConnectionState.NotRunning);
            int? wrongPort = null;
            foreach (int port in _verifiedPorts.Select(item => item.Port).Concat(snapshot.QueryPorts)
                .Where(p => p is > 0 and <= 65535).Distinct().Take(4))
            {
                try
                {
                    // A small per-candidate budget also leaves time to try a second service.
                    using var candidate = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    candidate.CancelAfter(TimeSpan.FromMilliseconds(1000));
                    string host = await ReadJsonAsync(port, "/?HOST_INFO", candidate.Token).ConfigureAwait(false);
                    if (!TryHostInfo(host, out int oscPort)) continue;
                    string mute = await ReadJsonAsync(port, "/avatar/parameters/MuteSelf", candidate.Token).ConfigureAwait(false);
                    if (!HasMuteValue(mute)) continue;
                    if (!snapshot.Processes.Any(_current)) return new(VrchatOscConnectionState.NotRunning);
                    // Require a current VRChat-owned UDP endpoint as well as a live service reply.
                    VrchatOscSnapshot fresh = await SnapshotAsync(deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!fresh.Processes.Intersect(snapshot.Processes).Any()) return new(VrchatOscConnectionState.NotRunning);
                    if (!fresh.UdpPorts.Contains(oscPort)) continue;
                    if (fresh.LocalQueryListeners?.Contains(port) == false) continue;
                    foreach (var process in fresh.Processes.Intersect(snapshot.Processes))
                        if (!_verifiedPorts.Contains((process, port))) _verifiedPorts.Add((process, port));
                    if (_verifiedPorts.Count > 4) _verifiedPorts.RemoveRange(0, _verifiedPorts.Count - 4);
                    if (oscPort != 9000) { wrongPort = oscPort; continue; }
                    return new(VrchatOscConnectionState.Connected, oscPort);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or IOException) { }
                if (deadline.IsCancellationRequested) break;
            }
            lifetime.Token.ThrowIfCancellationRequested();
            return wrongPort is int alternate
                ? new(VrchatOscConnectionState.WrongPort, alternate)
                : new(VrchatOscConnectionState.Unavailable);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        { return new(VrchatOscConnectionState.Unavailable); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return new(VrchatOscConnectionState.Error); }
        finally { _gate.Release(); }
    }

    private async Task<VrchatOscSnapshot> SnapshotAsync(CancellationToken token)
    {
        // Native enumeration cannot be interrupted mid-call. Keep at most one
        // outstanding read even after a deadline, rather than accumulate workers.
        var pending = _pendingSnapshot ??= Task.Run(_snapshot);
        try { return await pending.WaitAsync(token).ConfigureAwait(false); }
        finally { if (pending.IsCompleted) _pendingSnapshot = null; }
    }

    private async Task<string> ReadJsonAsync(int port, string path, CancellationToken token)
    {
        using var response = await _http.GetAsync(new Uri($"http://127.0.0.1:{port}{path}"),
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("OSCQuery response exceeds its limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[2048];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaximumResponseBytes) throw new InvalidDataException("OSCQuery response exceeds its limit.");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    internal static bool TryHostInfo(string json, out int port)
    {
        port = 0;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("NAME", out var name) || name.ValueKind != JsonValueKind.String ||
            !(name.GetString() ?? "").StartsWith("VRChat", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("OSC_PORT", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out port) || port is <= 0 or > 65535) return false;
        if (root.TryGetProperty("OSC_TRANSPORT", out var transport) &&
            (transport.ValueKind != JsonValueKind.String || !string.Equals(transport.GetString(), "UDP", StringComparison.OrdinalIgnoreCase))) return false;
        if (root.TryGetProperty("OSC_IP", out var ip) &&
            (ip.ValueKind != JsonValueKind.String || !IPAddress.TryParse(ip.GetString(), out var address) ||
             (!IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any)))) return false;
        return true;
    }

    internal static bool HasMuteValue(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("FULL_PATH", out var path) ||
            path.ValueKind != JsonValueKind.String || path.GetString() != "/avatar/parameters/MuteSelf" ||
            !root.TryGetProperty("VALUE", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 1) return false;
        var value = values[0];
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ||
            (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number is 0 or 1);
    }

    private static VrchatOscSnapshot ReadSnapshot()
    {
        var identities = new List<VrchatProcessIdentity>();
        foreach (var process in Process.GetProcessesByName("VRChat"))
        {
            using (process)
                try { if (!process.HasExited) identities.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks)); }
                catch (InvalidOperationException) { }
        }
        if (identities.Count == 0) return new(identities, Array.Empty<int>(), new HashSet<int>());
        var ids = identities.Select(p => p.Id).ToHashSet();
        var tcp = ReadOwnedPorts(tcp: true);
        var queryPorts = tcp.Where(p => ids.Contains(p.Pid)).Select(p => p.Port).ToList();
        // HttpListener may be hosted by Windows HTTP.sys (PID 4), not the game PID.
        // Only log-advertised ports from a current game log are eligible in that case;
        // never scan arbitrary services or upload/read entire conversation logs.
        var kernelPorts = tcp.Where(p => p.Pid == 4).Select(p => p.Port).ToHashSet();
        queryPorts.InsertRange(0, ReadQueryHints(identities.Min(p => p.StartedUtcTicks)).Where(kernelPorts.Contains));
        var udp = ReadOwnedPorts(tcp: false).Where(p => ids.Contains(p.Pid)).Select(p => p.Port).ToHashSet();
        return new(identities, queryPorts.Distinct().Take(4).ToArray(), udp, tcp.Select(p => p.Port).ToHashSet());
    }

    private static bool IsCurrent(VrchatProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Id);
            return !process.HasExited && process.ProcessName.Equals("VRChat", StringComparison.OrdinalIgnoreCase) &&
                process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static IEnumerable<int> ReadQueryHints(long processStartedUtcTicks)
    {
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "VRChat", "VRChat");
        if (!Directory.Exists(logs)) return Array.Empty<int>();
        try
        {
            var latest = new DirectoryInfo(logs).EnumerateFiles("output_log*.txt")
                .Where(f => f.LastWriteTimeUtc.Ticks >= processStartedUtcTicks).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest is null) return Array.Empty<int>();
            using var input = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = new byte[(int)Math.Min(input.Length, 512 * 1024)];
            input.ReadExactly(start);
            var tail = new byte[(int)Math.Min(input.Length, 64 * 1024)];
            input.Seek(-tail.Length, SeekOrigin.End); input.ReadExactly(tail);
            return ParseQueryHints(Encoding.UTF8.GetString(start) + "\n" + Encoding.UTF8.GetString(tail));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Array.Empty<int>(); }
    }

    internal static int[] ParseQueryHints(string text) => Regex.Matches(text, @"\btype OSCQuery on ([0-9]{1,5})\b", RegexOptions.CultureInvariant)
        .Cast<Match>().Reverse().Select(match => int.Parse(match.Groups[1].Value)).Where(port => port is > 0 and <= 65535).Distinct().Take(4).ToArray();

    private static IReadOnlyList<(int Pid, int Port)> ReadOwnedPorts(bool tcp)
    {
        uint length = 0;
        uint result = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref length, false, 2, 3, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref length, false, 2, 1, 0);
        if (result != 122 || length is < 4 or > 4 * 1024 * 1024) throw new InvalidOperationException("Could not inspect local OSC endpoints.");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint capacity = length;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)capacity));
            try
            {
                result = tcp ? GetExtendedTcpTable(buffer, ref length, false, 2, 3, 0)
                    : GetExtendedUdpTable(buffer, ref length, false, 2, 1, 0);
                if (result == 122 && length <= 4 * 1024 * 1024) continue;
                if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
                int count = Marshal.ReadInt32(buffer), rowSize = tcp ? 24 : 12;
                if (count < 0 || count > (capacity - 4) / rowSize) throw new InvalidDataException("Invalid local endpoint table.");
                var ports = new List<(int, int)>();
                for (int index = 0; index < count; index++)
                {
                    int offset = 4 + index * rowSize;
                    uint address = unchecked((uint)Marshal.ReadInt32(buffer, offset + (tcp ? 4 : 0)));
                    if (address != 0 && (address & 255) != 127) continue;
                    uint encodedPort = unchecked((uint)Marshal.ReadInt32(buffer, offset + (tcp ? 8 : 4)));
                    int port = DecodePort(encodedPort), pid = Marshal.ReadInt32(buffer, offset + (tcp ? 20 : 8));
                    if (port > 0 && pid > 0) ports.Add((pid, port));
                }
                return ports;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Local endpoints changed during the check.");
    }

    internal static int DecodePort(uint encoded) => (int)(((encoded & 255) << 8) | ((encoded >> 8) & 255));

    public void Dispose() { _disposed.Cancel(); _http.Dispose(); }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr buffer, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool ordered, uint family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr buffer, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool ordered, uint family, int tableClass, uint reserved);
}

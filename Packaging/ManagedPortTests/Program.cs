using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Standalone developer/CI fixture. This executable and its script are not part
// of the application payload, and no app self-test launches them implicitly.
internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static async Task<int> Main(string[] arguments)
    {
        try
        {
            if (arguments.Length != 2) throw new ArgumentException("Provide the built application directory and an isolated fixture directory.");
            string appDirectory = Path.GetFullPath(arguments[0]);
            string directory = Path.GetFullPath(arguments[1]);
            string app = Path.Combine(appDirectory, "SayWhat.dll");
            if (!File.Exists(app)) throw new FileNotFoundException("Build the application before running its isolated port fixture.", app);
            string script = Path.Combine(AppContext.BaseDirectory, "fixtures", "owned-http.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("The test-only socket fixture is missing.", script);
            Directory.CreateDirectory(directory);
            string data = Path.Combine(directory, "data"); Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("SAYWHAT_DATA_ROOT", data);
            Environment.SetEnvironmentVariable("LOCALTALK_DATA_ROOT", data);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string path = Path.Combine(appDirectory, name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
            };
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(app);
            Type Find(string name) => assembly.GetType("IncomingTranslator." + name, throwOnError: true)!;
            Type runtimeType = Find("RuntimeController");
            Type ownedType = runtimeType.GetNestedType("OwnedProcess", BindingFlags.NonPublic)!;
            Type allocator = Find("ManagedPortAllocator");
            MethodInfo waitHealth = runtimeType.GetMethod("WaitHealthAsync", Instance)!;
            MethodInfo stop = ownedType.GetMethod("StopAsync", Instance)!;
            PropertyInfo alive = ownedType.GetProperty("IsAlive", Instance)!;
            PropertyInfo pid = ownedType.GetProperty("ProcessId", Instance)!;
            bool IsAlive(object process) => (bool)alive.GetValue(process)!;
            Task Stop(object process) => (Task)stop.Invoke(process, [true])!;
            Task Wait(object runtime, Uri endpoint, object process, TimeSpan timeout, Func<JsonElement, bool> accepted, CancellationToken token) =>
                (Task)waitHealth.Invoke(runtime, [endpoint, process, timeout, accepted, token, true, null])!;
            using var provisioner = (IDisposable)Activator.CreateInstance(Find("LocalTalkProvisioner"), Instance, null, [directory, null], null)!;
            object controller = Activator.CreateInstance(runtimeType, Instance, null, [provisioner, null], null)!;
            object? http = null, idle = null;
            int checks = 0;
            void Check(bool valid, string detail)
            { if (!valid) throw new InvalidOperationException("Owned readiness handoff: " + detail); checks++; }
            string firstReady = Path.Combine(directory, "http-ready.txt"), idleReady = Path.Combine(directory, "idle-ready.txt");
            string requests = Path.Combine(directory, "http-requests.txt");
            foreach (string path in new[] { firstReady, idleReady, requests }) if (File.Exists(path)) File.Delete(path);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                // Reserve only a fixture-owned ephemeral address. The child
                // cannot inherit the socket, matching the real startup handoff.
                int port;
                using (var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true })
                { reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = ((IPEndPoint)reservation.LocalEndPoint!).Port; }
                ProcessStartInfo Info(int requestedPort, string readyPath)
                {
                    var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
                    foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                        "-Port", requestedPort.ToString(CultureInfo.InvariantCulture), "-ReadyPath", readyPath, "-RequestsPath", requests })
                        info.ArgumentList.Add(argument);
                    return info;
                }
                http = Activator.CreateInstance(ownedType, Instance, null, [Info(port, firstReady), "Synthetic local HTTP fixture", null], null)!;
                idle = Activator.CreateInstance(ownedType, Instance, null, [Info(0, idleReady), "Synthetic idle ownership fixture", null], null)!;
                async Task Ready(object process, string path)
                {
                    while (!File.Exists(path))
                    {
                        if (!IsAlive(process)) throw new InvalidOperationException("Synthetic readiness helper exited before binding.");
                        await Task.Delay(25, deadline.Token);
                    }
                }
                await Ready(http, firstReady); await Ready(idle, idleReady);
                Check(IsAlive(http) && IsAlive(idle) && (bool)allocator.GetMethod("IsOwnedTcpListener", Static)!.Invoke(null, [port, pid.GetValue(http)])!,
                    "the fixture owns its actual loopback listener and both retained children are alive");
                var endpoint = new Uri($"http://127.0.0.1:{port}/health");
                bool timedOut = false;
                try
                {
                    await Wait(controller, endpoint, idle, TimeSpan.FromMilliseconds(450),
                        root => root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True, deadline.Token);
                }
                catch (TimeoutException) { timedOut = true; }
                Check(timedOut && !File.Exists(requests) && IsAlive(http) && IsAlive(idle),
                    "a different PID receives no HTTP request and cannot be adopted or stopped");
                await Wait(controller, endpoint, http, TimeSpan.FromSeconds(4),
                    root => root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True, deadline.Token);
                Check(File.Exists(requests) && File.ReadAllLines(requests).Length == 1 && IsAlive(http),
                    "the exact owned child can satisfy real HTTP readiness");
                bool staleReadyRejected = false;
                object responseOwner = http;
                try
                {
                    await Wait(controller, endpoint, responseOwner, TimeSpan.FromSeconds(4), root =>
                    {
                        bool ready = root.TryGetProperty("ready", out JsonElement value) && value.ValueKind == JsonValueKind.True;
                        if (ready) Stop(responseOwner).GetAwaiter().GetResult();
                        return ready;
                    }, deadline.Token);
                }
                catch (InvalidOperationException) { staleReadyRejected = true; }
                Check(staleReadyRejected && !IsAlive(http) && IsAlive(idle),
                    "a decoded ready response cannot outlive its owned process");
            }
            finally
            {
                try { if (http is not null) await Stop(http); }
                finally
                {
                    try { if (idle is not null) await Stop(idle); }
                    finally { await ((IAsyncDisposable)controller).DisposeAsync(); }
                }
            }
            Check(http is not null && idle is not null && !IsAlive(http) && !IsAlive(idle), "the exact fixture children were released");
            string report = $"PASS {checks} owned-listener readiness checks; test-only local sockets and hidden helper processes, no models/audio/OSC.";
            await File.WriteAllTextAsync(Path.Combine(directory, "results.log"), report + Environment.NewLine);
            Console.WriteLine(report);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : error);
            return 1;
        }
    }
}

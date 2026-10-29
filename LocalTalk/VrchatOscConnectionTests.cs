using System.Net;
using System.Net.Http;
using System.Text;

namespace IncomingTranslator;

// Synthetic process metadata and in-memory HTTP only. No live game, log file,
// port enumeration, packet, microphone, engine or model is touched by these checks.
internal static class VrchatOscConnectionTests
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string description)
        { if (!value) throw new InvalidOperationException("OSC availability check failed: " + description); checks++; }
        const string host = "{\"NAME\":\"VRChat-Client\",\"OSC_IP\":\"127.0.0.1\",\"OSC_PORT\":9000,\"OSC_TRANSPORT\":\"UDP\"}";
        const string mute = "{\"FULL_PATH\":\"/avatar/parameters/MuteSelf\",\"TYPE\":\"T\",\"VALUE\":[false]}";
        Check(VrchatOscConnectionProbe.TryHostInfo(host, out int port) && port == 9000, "expected local UDP endpoint");
        foreach (string invalid in new[] { host.Replace("VRChat-Client", "OtherApp"), host.Replace("127.0.0.1", "192.0.2.1"),
            host.Replace("UDP", "TCP"), host.Replace("9000", "0"), host.Replace("9000", "65536"), "{}", "[]" })
            Check(!VrchatOscConnectionProbe.TryHostInfo(invalid, out _), "invalid host info cannot confirm a connection");
        Check(VrchatOscConnectionProbe.HasMuteValue(mute), "unmuted state is a valid response, not absence");
        Check(VrchatOscConnectionProbe.HasMuteValue(mute.Replace("false", "true")), "muted state also confirms availability");
        foreach (string invalid in new[] { "{}", "[]", mute.Replace("[false]", "[]"), mute.Replace("[false]", "[false,true]"),
            mute.Replace("[false]", "[\"false\"]"), mute.Replace("[false]", "[2]"), mute.Replace("MuteSelf", "Other") })
            Check(!VrchatOscConnectionProbe.HasMuteValue(invalid), "invalid mute values cannot confirm a connection");
        Check(VrchatOscConnectionProbe.DecodePort(0x2823) == 9000, "Windows table uses network-order port bytes");
        Check(VrchatOscConnectionProbe.ParseQueryHints("type OSCQuery on 42001\ntype OSCQuery on 0\ntype OSCQuery on 65536\ntype OSCQuery on 42002\ntype OSCQuery on 42001")
            .SequenceEqual(new[] { 42001, 42002 }), "bounded distinct newest advertised ports");
        var identity = new VrchatProcessIdentity(100, 20);
        var snapshot = new VrchatOscSnapshot(new[] { identity }, new[] { 42001 }, new HashSet<int> { 9000 });
        using (var handler = new FakeHandler(request => Json(request.RequestUri!.Query.Length > 0 ? host : mute)))
        using (var probe = new VrchatOscConnectionProbe(() => snapshot, _ => true, handler))
        {
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.Connected, "live replies and owned UDP endpoint confirm availability");
            Check(handler.Requests.Count == 2 && handler.Requests.All(uri => uri.Host == "127.0.0.1" && uri.Port == 42001), "requests stay on selected loopback port");
            Check(handler.Requests[0].PathAndQuery == "/?HOST_INFO" && handler.Requests[1].AbsolutePath == "/avatar/parameters/MuteSelf", "only status paths read");
        }
        foreach (var fixture in new[] {
            (Snapshot: snapshot with { Processes = Array.Empty<VrchatProcessIdentity>() }, Expected: VrchatOscConnectionState.NotRunning),
            (Snapshot: snapshot with { QueryPorts = Array.Empty<int>() }, Expected: VrchatOscConnectionState.Unavailable),
            (Snapshot: snapshot with { UdpPorts = new HashSet<int>() }, Expected: VrchatOscConnectionState.Unavailable) })
        {
            using var handler = new FakeHandler(request => Json(request.RequestUri!.Query.Length > 0 ? host : mute));
            using var probe = new VrchatOscConnectionProbe(() => fixture.Snapshot, _ => true, handler);
            Check((await probe.CheckAsync()).State == fixture.Expected, "presence and UDP ownership are independently required");
            if (fixture.Expected == VrchatOscConnectionState.NotRunning) Check(handler.Requests.Count == 0, "no requests without a game process");
        }
        using (var handler = new FakeHandler(request => Json(request.RequestUri!.Query.Length > 0 ? host.Replace("9000", "9002") : mute)))
        using (var probe = new VrchatOscConnectionProbe(() => snapshot with { UdpPorts = new HashSet<int> { 9002 } }, _ => true, handler))
            Check(await probe.CheckAsync() == new VrchatOscConnectionStatus(VrchatOscConnectionState.WrongPort, 9002), "custom receiving port is not reported as connected to our 9000 output");
        using (var handler = new FakeHandler(request => Json(request.RequestUri!.Query.Length > 0 ? host : mute)))
        using (var probe = new VrchatOscConnectionProbe(() => snapshot, _ => false, handler))
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.NotRunning, "exited or reused process never remains green");
        int reads = 0;
        using (var handler = new FakeHandler(request => Json(request.RequestUri!.Query.Length > 0 ? host : mute)))
        using (var probe = new VrchatOscConnectionProbe(() => ++reads == 1 ? snapshot : snapshot with { Processes = new[] { identity with { StartedUtcTicks = 21 } } }, _ => true, handler))
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.NotRunning, "game restart invalidates previous replies");
        foreach (string invalid in new[] { "not-json", host.Replace("9000", "\"9000\""), new string('x', 16385) })
        {
            using var handler = new FakeHandler(_ => Json(invalid));
            using var probe = new VrchatOscConnectionProbe(() => snapshot, _ => true, handler);
            Check((await probe.CheckAsync()).State != VrchatOscConnectionState.Connected, "invalid/oversized replies fail closed");
        }
        using (var handler = new FakeHandler(request => Json(request.RequestUri!.Port == 42001 ? host.Replace("9000", "\"9000\"") : request.RequestUri.Query.Length > 0 ? host : mute)))
        using (var probe = new VrchatOscConnectionProbe(() => snapshot with { QueryPorts = new[] { 42001, 42002 } }, _ => true, handler))
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.Connected, "malformed first service does not hide a valid second one");
        var cachedSnapshot = snapshot with { LocalQueryListeners = new HashSet<int> { 42001 } };
        using (var handler = new FakeHandler(request => Json(request.RequestUri!.Query.Length > 0 ? host : mute)))
        using (var probe = new VrchatOscConnectionProbe(() => cachedSnapshot, _ => true, handler))
        {
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.Connected, "cache starts from a verified response");
            cachedSnapshot = cachedSnapshot with { QueryPorts = Array.Empty<int>() };
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.Connected, "verified port survives log-window movement");
            cachedSnapshot = cachedSnapshot with { LocalQueryListeners = new HashSet<int>() };
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.Unavailable, "disappeared listener removes the cached port");
        }
        using (var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)))
        using (var probe = new VrchatOscConnectionProbe(() => snapshot, _ => true, handler))
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.Unavailable, "redirect is not a successful handshake");
        using (var handler = new FakeHandler(_ => Json(host)))
        using (var probe = new VrchatOscConnectionProbe(() => throw new UnauthorizedAccessException(), _ => true, handler))
            Check((await probe.CheckAsync()).State == VrchatOscConnectionState.Error, "permissions error is distinct from game absence");
        using (var handler = new FakeHandler(_ => Json(host)))
        using (var probe = new VrchatOscConnectionProbe(() => snapshot, _ => true, handler))
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool observed = false;
            try { await probe.CheckAsync(cancelled.Token); } catch (OperationCanceledException) { observed = true; }
            Check(observed && handler.Requests.Count == 0, "shutdown cancellation stops checks before any request");
        }

        async Task ExpectCancelled(Task<VrchatOscConnectionStatus> task, string description)
        {
            bool observed = false;
            try { await task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
            catch (OperationCanceledException) { observed = true; }
            Check(observed, description);
        }

        // A native table/log read cannot be cancelled once it has entered. The
        // probe must abandon its wait, but reuse that one outstanding worker.
        // Always release the synthetic worker, even if an assertion fails.
        using (var release = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int workers = 0;
            using var handler = new FakeHandler(_ => Json(host));
            using var probe = new VrchatOscConnectionProbe(() =>
            {
                Interlocked.Increment(ref workers); entered.TrySetResult();
                try { release.Wait(); return snapshot; }
                finally { exited.TrySetResult(); }
            }, _ => true, handler);
            using var firstCancel = new CancellationTokenSource();
            using var secondCancel = new CancellationTokenSource();
            try
            {
                var first = probe.CheckAsync(firstCancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                firstCancel.Cancel();
                await ExpectCancelled(first, "caller cancellation interrupts a stalled native snapshot wait").ConfigureAwait(false);
                var second = probe.CheckAsync(secondCancel.Token); secondCancel.Cancel();
                await ExpectCancelled(second, "a second cancelled check does not wait for the stalled native worker").ConfigureAwait(false);
                Check(Volatile.Read(ref workers) == 1 && handler.Requests.Count == 0,
                    "cancelled checks keep at most one outstanding snapshot worker and send no requests");
                var third = probe.CheckAsync(); probe.Dispose();
                await ExpectCancelled(third, "disposing a probe interrupts the stalled native snapshot wait").ConfigureAwait(false);
                Check(Volatile.Read(ref workers) == 1, "dispose never starts another native worker");
            }
            finally
            {
                firstCancel.Cancel(); secondCancel.Cancel(); probe.Dispose(); release.Set();
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
        }

        // Exercise the actual overall deadline while the native worker remains
        // blocked. This is deliberately not a real Windows enumeration.
        using (var release = new ManualResetEventSlim())
        {
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new FakeHandler(_ => Json(host));
            using var probe = new VrchatOscConnectionProbe(() =>
            {
                try { release.Wait(); return snapshot; }
                finally { exited.TrySetResult(); }
            }, _ => true, handler);
            try
            {
                Check((await probe.CheckAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).State == VrchatOscConnectionState.Unavailable,
                    "stalled native snapshot reaches the overall deadline without publishing green");
                Check(handler.Requests.Count == 0, "timed-out native snapshot never sends a query");
            }
            finally { probe.Dispose(); release.Set(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
        }

        foreach (bool body in new[] { false, true })
        foreach (bool dispose in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var input = new StalledBodyStream(entered, exited);
            using var cancel = new CancellationTokenSource();
            using var handler = new AsyncHandler(async (_, token) =>
            {
                if (body) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) };
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return Json(host); }
                finally { exited.TrySetResult(); }
            });
            using var probe = new VrchatOscConnectionProbe(() => snapshot, _ => true, handler);
            try
            {
                var pending = probe.CheckAsync(cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                if (dispose) probe.Dispose(); else cancel.Cancel();
                await ExpectCancelled(pending, (dispose ? "dispose" : "caller cancellation") +
                    " interrupts stalled HTTP " + (body ? "body" : "headers")).ConfigureAwait(false);
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                Check(handler.Requests == 1, "cancelled HTTP operation never advances to a second request");
                if (body) Check(input.IsDisposed, "cancelled response disposes its body stream");
            }
            finally
            {
                cancel.Cancel(); probe.Dispose(); input.Dispose();
                if (entered.Task.IsCompleted) await exited.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
        }
        foreach (bool body in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var input = new StalledBodyStream(entered, exited);
            using var handler = new AsyncHandler(async (_, token) =>
            {
                if (body) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) };
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return Json(host); }
                finally { exited.TrySetResult(); }
            });
            using var probe = new VrchatOscConnectionProbe(() => snapshot, _ => true, handler);
            try
            {
                Check((await probe.CheckAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false)).State == VrchatOscConnectionState.Unavailable,
                    "stalled HTTP " + (body ? "body" : "headers") + " reaches the candidate deadline");
                Check(handler.Requests == 1, "timed-out candidate does not publish or request mute status");
                if (body) Check(input.IsDisposed, "timed-out response body is disposed");
            }
            finally
            {
                probe.Dispose();
                if (entered.Task.IsCompleted) await exited.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
        }

        using (var handler = new FakeHandler(_ => Json(host)))
        using (var probe = new VrchatOscConnectionProbe(() => snapshot, _ => true, handler))
        {
            probe.Dispose();
            await ExpectCancelled(probe.CheckAsync(), "a disposed probe cannot resume querying").ConfigureAwait(false);
            Check(handler.Requests.Count == 0, "disposed probe makes no HTTP requests");
        }
        return checks;
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!); return Task.FromResult(response(request)); }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _requests); return response(request, token); }
    }
    private sealed class StalledBodyStream(TaskCompletionSource entered, TaskCompletionSource exited) : Stream
    {
        internal bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return 0; }
            finally { exited.TrySetResult(); }
        }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

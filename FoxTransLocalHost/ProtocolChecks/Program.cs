using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

// Entirely synthetic PCM and in-memory transport. No microphone, actual socket/UDP,
// provider HTTP request, local app data, GPU/model, or production process is used.
try
{
    int checks = await ProtocolChecks.RunAsync();
    Console.WriteLine($"PASS: {checks} offline protocol checks. No audio devices, sockets, or models used.");
    return 0;
}
catch (Exception error)
{
    // The fixture must fail as an ordinary console result, never an unhandled Windows dialog.
    Console.Error.WriteLine("FAIL: " + error);
    return 1;
}

internal static class ProtocolChecks
{
    private static readonly AudioFormat Format = new(16000, 16, 1);
    private const string Created = """{"type":"session.created","session_id":"offline","protocol_version":1,"model":"voxtral-mini","transcription_delay_ms":240,"audio":{"format":"pcm_s16le","sample_rate":16000,"channels":1}}""";
    private const string Final = """{"type":"transcript.final","sequence":1,"text":"synthetic final","audio_end_ms":80}""";
    private static int _checks;
    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        _checks++;
    }

    internal static async Task<int> RunAsync()
    {
        await CompletionMetricsAsync();
        await OptionalMetricsAsync();
        await WarningFieldsAsync();
        await CancellationAsync();
        await UnexpectedCompletionAsync();
        await FinalWithoutCompletedAsync();
        await BoundedCompletionAsync();
        return _checks;
    }

    private static VoxtralFoxTranscriber Transcriber(FakeSocket socket, bool finish = true) => new(
        ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig("http://127.0.0.1:8080", null, 240), null),
        new FakeFactory(socket), finishOnAudioEnd: finish);

    private static async Task CompletionMetricsAsync()
    {
        var socket = new FakeSocket(); socket.Enqueue(Created);
        await using var transcriber = Transcriber(socket);
        Task<List<StreamingTranscriptionEvent>> run = Collect(transcriber, Frames(4));
        await WaitUntil(() => socket.Sent.Any(message => message.Is("input_audio.end")));
        socket.Enqueue(Final);
        socket.Enqueue("""{"type":"session.completed","sequence":2,"audio_duration_ms":80,"processing":{"realtime_factor":0.67,"backlog_final_ms":12.5,"backlog_slope_ms_per_s":-4.25,"text":"should-not-leak","tokens":["should-not-leak"],"prompt":"should-not-leak"}}""");
        List<StreamingTranscriptionEvent> events = await run.WaitAsync(TimeSpan.FromSeconds(5));
        StreamingSessionCompleted completed = events.OfType<StreamingSessionCompleted>().Single();
        Check(completed.Sequence == 2 && completed.AudioDurationMs == 80, "completion identity/audio duration");
        Check(completed.Processing == new VoxtralProcessingStats(.67, 12.5, -4.25), "exact typed processing metrics and signed slope");
        Check(!completed.Processing!.ToString().Contains("should-not-leak", StringComparison.Ordinal), "no arbitrary speech/token/prompt data retained in typed diagnostics");
        Check(events.FindIndex(item => item is StreamingFinalTranscript) < events.FindIndex(item => item is StreamingSessionCompleted), "final then completed ordering retained");
        byte[] actual = socket.Sent.Where(message => message.Type == WebSocketMessageType.Binary).SelectMany(message => message.Bytes).ToArray();
        byte[] expected = Enumerable.Range(1, 4).SelectMany(index => Enumerable.Repeat((byte)index, 640)).ToArray();
        Check(actual.SequenceEqual(expected), "exact finite PCM without extra client padding");
        Check(socket.Sent.Count(message => message.Is("input_audio.end")) == 1, "one end packet");
        Check(socket.Sent.Count(message => message.Is("session.configure")) == 1, "one configure packet");
        Check(!socket.Sent.Any(message => message.Is("session.cancel")), "successful finite stream needs no cancel");
        Check(socket.Disposed, "successful finite stream disposes its in-memory socket");
    }

    private static async Task OptionalMetricsAsync()
    {
        string[] cases = {
            "", ",\"processing\":null", ",\"processing\":[]", ",\"processing\":\"older peer\"",
            ",\"processing\":{\"realtime_factor\":\"invalid\",\"backlog_final_ms\":1e9999,\"backlog_slope_ms_per_s\":null}",
            ",\"processing\":{\"realtime_factor\":-1,\"backlog_final_ms\":-2,\"unknown\":\"should-not-leak\"}"
        };
        foreach (string extra in cases)
        {
            var socket = new FakeSocket(); socket.Enqueue(Created);
            await using var transcriber = Transcriber(socket);
            Task<List<StreamingTranscriptionEvent>> run = Collect(transcriber, Frames(1));
            await WaitUntil(() => socket.Sent.Any(message => message.Is("input_audio.end")));
            socket.Enqueue(Final);
            socket.Enqueue("{\"type\":\"session.completed\",\"sequence\":2,\"audio_duration_ms\":20" + extra + "}");
            List<StreamingTranscriptionEvent> events = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Check(events.OfType<StreamingSessionCompleted>().Single().Processing is null, "absent/malformed/nonfinite optional diagnostics ignored");
            Check(events.OfType<StreamingFinalTranscript>().Count() == 1, "older compatible completion retains final speech event");
            Check(socket.Disposed, "optional-diagnostic case closes transport");
        }
        // A partially supported metric object retains independent good fields.
        var partialSocket = new FakeSocket(); partialSocket.Enqueue(Created);
        await using var partialTranscriber = Transcriber(partialSocket);
        Task<List<StreamingTranscriptionEvent>> partialRun = Collect(partialTranscriber, Frames(1));
        await WaitUntil(() => partialSocket.Sent.Any(message => message.Is("input_audio.end")));
        partialSocket.Enqueue(Final);
        partialSocket.Enqueue("""{"type":"session.completed","sequence":2,"audio_duration_ms":20,"processing":{"realtime_factor":1.25,"backlog_final_ms":"older","backlog_slope_ms_per_s":-2}}""");
        List<StreamingTranscriptionEvent> partialEvents = await partialRun.WaitAsync(TimeSpan.FromSeconds(5));
        Check(partialEvents.OfType<StreamingSessionCompleted>().Single().Processing == new VoxtralProcessingStats(1.25, null, -2), "valid partial metric fields remain available");
    }

    private static async Task WarningFieldsAsync()
    {
        var socket = new FakeSocket(); socket.Enqueue(Created);
        await using var transcriber = Transcriber(socket);
        Task<List<StreamingTranscriptionEvent>> run = Collect(transcriber, Frames(1));
        await WaitUntil(() => socket.Sent.Any(message => message.Is("input_audio.end")));
        socket.Enqueue("""{"type":"session.warning","code":"processing_lag","message":"synthetic warning","backlog_ms":1200.5,"input_backlog_ms":2,"runtime_backlog_ms":3,"pcm_queue_ms":4,"outbound_messages":5,"outbound_bytes":6,"websocket_write_age_ms":7,"worker_outbound_wait_ms":8}""");
        socket.Enqueue("""{"type":"session.warning","code":"processing_lag","message":"synthetic overflow","backlog_ms":1e9999}""");
        socket.Enqueue(Final);
        socket.Enqueue("""{"type":"session.completed","sequence":2,"audio_duration_ms":20}""");
        List<StreamingTranscriptionEvent> events = await run.WaitAsync(TimeSpan.FromSeconds(5));
        StreamingServerWarning[] warnings = events.OfType<StreamingServerWarning>().ToArray();
        Check(warnings.Length == 2, "warnings retained without masking completed stream");
        StreamingServerWarning value = warnings[0];
        Check(value.BacklogMs == 1200.5 && value.InputBacklogMs == 2 && value.RuntimeBacklogMs == 3 && value.PcmQueueMs == 4, "typed audio/runtime warning backlogs");
        Check(value.OutboundMessages == 5 && value.OutboundBytes == 6 && value.WebSocketWriteAgeMs == 7 && value.WorkerOutboundWaitMs == 8, "typed outbound warning metadata");
        Check(warnings[1].BacklogMs is null, "nonfinite warning metric is never exposed for logging");
    }

    private static async Task CancellationAsync()
    {
        var socket = new FakeSocket(); socket.Enqueue(Created);
        using var cancel = new CancellationTokenSource();
        await using var transcriber = Transcriber(socket, finish: false);
        Task<List<StreamingTranscriptionEvent>> run = Collect(transcriber, Continuous(cancel.Token), cancel.Token);
        await WaitUntil(() => socket.Sent.Any(message => message.Type == WebSocketMessageType.Binary));
        var elapsed = Stopwatch.StartNew(); cancel.Cancel();
        try { await run.WaitAsync(TimeSpan.FromSeconds(4)); throw new InvalidOperationException("cancellation was swallowed"); }
        catch (OperationCanceledException) { Check(true, "application cancellation remains cancellation"); }
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(4), "cancellation handshake is bounded");
        Check(socket.Sent.Any(message => message.Is("session.cancel")), "courtesy cancel sent to synthetic peer");
        Check(!socket.Sent.Any(message => message.Is("input_audio.end")), "persistent cancellation does not invent an audio final");
        Check(socket.Disposed, "cancelled transport disposed");
    }

    private static async Task UnexpectedCompletionAsync()
    {
        var socket = new FakeSocket(); socket.Enqueue(Created);
        using var cancel = new CancellationTokenSource();
        await using var transcriber = Transcriber(socket);
        Task<List<StreamingTranscriptionEvent>> run = Collect(transcriber, Continuous(cancel.Token), cancel.Token);
        await WaitUntil(() => socket.Sent.Any(message => message.Type == WebSocketMessageType.Binary));
        socket.Enqueue("""{"type":"session.completed","sequence":2,"audio_duration_ms":20}""");
        VoxtralFoxException failure = await ExpectVoxtralFailure(run);
        Check(failure.Code == "unexpected_completion", "completion before input ended is rejected");
        Check(socket.Disposed, "invalid ordering transport disposed");
    }

    private static async Task FinalWithoutCompletedAsync()
    {
        var socket = new FakeSocket(); socket.Enqueue(Created);
        await using var transcriber = Transcriber(socket);
        Task<List<StreamingTranscriptionEvent>> run = Collect(transcriber, Frames(1));
        await WaitUntil(() => socket.Sent.Any(message => message.Is("input_audio.end")));
        socket.Enqueue(Final); socket.EnqueueClose();
        VoxtralFoxException failure = await ExpectVoxtralFailure(run);
        Check(failure.Code == "unexpected_close", "final followed by close is not silently accepted as completed");
        Check(socket.Disposed, "incomplete-final transport disposed");
    }

    private static async Task BoundedCompletionAsync()
    {
        var socket = new FakeSocket(); socket.Enqueue(Created);
        await using var transcriber = Transcriber(socket);
        Task<List<StreamingTranscriptionEvent>> run = Collect(transcriber, Frames(1));
        await WaitUntil(() => socket.Sent.Any(message => message.Is("input_audio.end")));
        var elapsed = Stopwatch.StartNew();
        VoxtralFoxException failure = await ExpectVoxtralFailure(run, TimeSpan.FromSeconds(12));
        Check(failure.Code == "session_completion_timeout", "eight-second completion deadline identifies ASR end stage");
        Check(failure.InnerException is TimeoutException, "completion timeout retains timeout cause");
        Check(elapsed.Elapsed >= TimeSpan.FromSeconds(7) && elapsed.Elapsed < TimeSpan.FromSeconds(12), "existing eight-second deadline not blindly raised or removed");
        Check(socket.Disposed, "timed-out transport disposed");
    }

    private static async Task<VoxtralFoxException> ExpectVoxtralFailure(Task<List<StreamingTranscriptionEvent>> run, TimeSpan? timeout = null)
    {
        try { await run.WaitAsync(timeout ?? TimeSpan.FromSeconds(5)); }
        catch (VoxtralFoxException failure) { return failure; }
        throw new InvalidOperationException("Expected a typed Voxtral protocol failure.");
    }
    private static async Task<List<StreamingTranscriptionEvent>> Collect(IStreamingTranscriber transcriber, IAsyncEnumerable<AudioFrame> frames, CancellationToken token = default)
    {
        var events = new List<StreamingTranscriptionEvent>();
        await foreach (StreamingTranscriptionEvent item in transcriber.TranscribeAsync(frames, token)) events.Add(item);
        return events;
    }
    private static async IAsyncEnumerable<AudioFrame> Frames(int count)
    {
        for (int index = 1; index <= count; index++)
        { yield return new(Enumerable.Repeat((byte)index, 640).ToArray(), Format); await Task.Yield(); }
    }
    private static async IAsyncEnumerable<AudioFrame> Continuous([EnumeratorCancellation] CancellationToken token)
    {
        while (true) { token.ThrowIfCancellationRequested(); yield return new(new byte[640], Format); await Task.Delay(5, token); }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(5);
        if (!condition()) throw new TimeoutException("Synthetic peer did not receive the expected protocol message.");
    }
    private sealed record Sent(byte[] Bytes, WebSocketMessageType Type)
    {
        public bool Is(string eventType)
        {
            if (Type != WebSocketMessageType.Text) return false;
            using JsonDocument document = JsonDocument.Parse(Bytes);
            return document.RootElement.GetProperty("type").GetString() == eventType;
        }
    }
    private sealed record Incoming(byte[] Bytes, WebSocketMessageType Type);
    private sealed class FakeFactory(FakeSocket socket) : IVoxtralWebSocketFactory
    { public IVoxtralWebSocket Create() => socket; }
    private sealed class FakeSocket : IVoxtralWebSocket
    {
        private readonly Channel<Incoming> _incoming = Channel.CreateUnbounded<Incoming>();
        public ConcurrentQueue<Sent> Sent { get; } = new();
        public WebSocketState State { get; private set; } = WebSocketState.None;
        public bool Disposed { get; private set; }
        public Task ConnectAsync(Uri endpoint, string? apiKey, CancellationToken token)
        { token.ThrowIfCancellationRequested(); State = WebSocketState.Open; return Task.CompletedTask; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var message = new Sent(bytes.ToArray(), type); Sent.Enqueue(message);
            if (message.Is("session.cancel")) Enqueue("""{"type":"session.cancelled"}""");
            return ValueTask.CompletedTask;
        }
        public async ValueTask<VoxtralSocketReceiveResult> ReceiveAsync(Memory<byte> bytes, CancellationToken token)
        {
            Incoming message = await _incoming.Reader.ReadAsync(token); message.Bytes.CopyTo(bytes);
            if (message.Type == WebSocketMessageType.Close) State = WebSocketState.CloseReceived;
            return new(message.Bytes.Length, message.Type, true, WebSocketCloseStatus.NormalClosure);
        }
        public Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token)
        { token.ThrowIfCancellationRequested(); State = WebSocketState.Closed; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        { Disposed = true; State = WebSocketState.Closed; _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
        public void Enqueue(string json)
        { if (!_incoming.Writer.TryWrite(new(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text))) throw new InvalidOperationException("Synthetic transport closed early."); }
        public void EnqueueClose()
        { if (!_incoming.Writer.TryWrite(new([], WebSocketMessageType.Close))) throw new InvalidOperationException("Synthetic transport closed early."); }
    }
}

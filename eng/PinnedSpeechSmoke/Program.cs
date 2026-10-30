using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

// Explicit, manually dispatched integration fixture. It never opens capture,
// contacts translation services or sends OSC. Only the verified native speech
// server is started, with CPU inference and an original file-based test voice.
internal static class PinnedSpeechSmoke
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(12);
    private static readonly double[] Durations = [0.5, 1, 2, 5];

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "z2six/saywhat" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        {
            Console.Error.WriteLine("This fixture runs only on a GitHub-hosted Windows x64 runner.");
            return 2;
        }
        if (args.Length != 4) { Console.Error.WriteLine("Expected runtime, model, synthetic WAV and metadata receipt paths."); return 2; }
        var report = new Receipt
        {
            SourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown",
            CpuThreads = 2, LogicalProcessors = Environment.ProcessorCount,
            WindowsBuild = Environment.OSVersion.Version.Build
        };
        using var total = new CancellationTokenSource(TotalBudget);
        try
        {
            byte[] spoken = ReadPcmWave(args[2]);
            int first = FirstVoicedSample(spoken);
            if (first < 0 || spoken.Length - first < 5 * 32000) throw new FixtureException("synthetic_voice_too_short");
            spoken = spoken.AsSpan(first).ToArray();
            foreach (bool silence in new[] { false, true })
            foreach (double seconds in Durations)
            {
                var sample = new byte[(int)(seconds * 32000)];
                if (!silence) spoken.AsSpan(0, sample.Length).CopyTo(sample);
                var result = new CaseResult { Kind = silence ? "silence" : "voice", AudioSeconds = seconds, Client = "production-adapter", Rms = Rms(sample) };
                report.Cases.Add(result);
                try
                {
                    await using var server = await Server.StartAsync(args[0], args[1], total.Token);
                    result.ReadyMs = server.ReadyMs;
                    if (report.ChineseHandshake is null)
                        report.ChineseHandshake = await CheckHandshakeAsync(server.Endpoint, "zh-CN", total.Token);
                    await RunAdapterAsync(server.Endpoint, sample, result, total.Token);
                }
                catch (Exception error) { result.Error = ErrorCode(error, total.Token); }
                result.Pass = result.Error is null && result.SessionCompleted && result.FinalEvents == 1 &&
                    (silence || seconds < 2 || result.FinalCharacters > 0);
                if (!result.Pass && result.Error is null) result.Error = "completion_or_transcript_assertion";
                Print(result);
                if (result.Error is "server_exited_before_ready" or "server_ready_timeout" || total.IsCancellationRequested) break;
            }
            // If the exact application adapter fails, compare the 5 s voiced
            // stream with a longer raw protocol deadline on a fresh server.
            // This is diagnostic only: it never turns a draft into a final.
            if (report.Cases.Any(c => !c.Pass) && !total.IsCancellationRequested)
            {
                var result = new CaseResult { Kind = "voice", AudioSeconds = 5, Client = "raw-protocol-30s", Rms = Rms(spoken.AsSpan(0, 160000)) };
                report.Cases.Add(result);
                try
                {
                    await using var server = await Server.StartAsync(args[0], args[1], total.Token);
                    result.ReadyMs = server.ReadyMs;
                    await RunRawAsync(server.Endpoint, spoken.AsMemory(0, 160000), result, total.Token);
                }
                catch (Exception error) { result.Error = ErrorCode(error, total.Token); }
                result.Pass = result.Error is null && result.SessionCompleted && result.NativeCompleted > 0 && result.FinalCharacters > 0;
                if (!result.Pass && result.Error is null) result.Error = "completion_or_transcript_assertion";
                Print(result);
            }
            report.Pass = report.Cases.Count(c => c.Client == "production-adapter") == 8 && report.Cases.All(c => c.Pass) && report.ChineseHandshake == "passed";
        }
        catch (Exception error) { report.Error = ErrorCode(error, total.Token); }
        finally
        {
            await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(report, JsonOptions));
        }
        Console.WriteLine(report.Pass ? "Pinned speech protocol smoke passed (CPU, synthetic speech only)." : "Pinned speech protocol smoke failed; inspect metadata receipt.");
        return report.Pass ? 0 : 1;
    }

    private static async Task RunAdapterAsync(Uri endpoint, byte[] audio, CaseResult result, CancellationToken token)
    {
        var observation = new WireObservation(result);
        await using var adapter = new NemotronTranscriber(endpoint, "en-US", new ObservedFactory(observation));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var clock = Stopwatch.StartNew();
        try
        {
            await foreach (StreamingTranscriptionEvent item in adapter.TranscribeAsync(Frames(audio, deadline.Token), deadline.Token))
            {
                if (item is StreamingFinalTranscript final) { result.FinalEvents++; result.FinalCharacters += final.Text.Length; }
                if (item is StreamingSessionCompleted) result.SessionCompleted = true;
            }
        }
        finally { result.ElapsedMs = clock.ElapsedMilliseconds; }
    }

    private static async IAsyncEnumerable<AudioFrame> Frames(byte[] audio, [EnumeratorCancellation] CancellationToken token)
    {
        // Match microphone delivery cadence, rather than buffering an entire
        // recording into a single WebSocket send. No physical audio device.
        for (int offset = 0; offset < audio.Length; offset += 640)
        {
            token.ThrowIfCancellationRequested();
            yield return new(audio.AsMemory(offset, Math.Min(640, audio.Length - offset)), new AudioFormat(16000, 16, 1));
            await Task.Delay(20, token);
        }
    }

    private static async Task<string> CheckHandshakeAsync(Uri endpoint, string language, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(WebSocketEndpoint(endpoint), deadline.Token);
        RequireType(await ReceiveJsonAsync(socket, deadline.Token), "session.created");
        await UpdateAsync(socket, language, deadline.Token);
        RequireType(await ReceiveJsonAsync(socket, deadline.Token), "session.updated");
        // Configuration support only; this does not claim Mandarin accuracy.
        socket.Abort();
        return "passed";
    }

    private static async Task RunRawAsync(Uri endpoint, ReadOnlyMemory<byte> audio, CaseResult result, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var socket = new ClientWebSocket();
        var observation = new WireObservation(result);
        var clock = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(WebSocketEndpoint(endpoint), deadline.Token);
            RequireType(await ReceiveJsonAsync(socket, deadline.Token), "session.created");
            await UpdateAsync(socket, "en-US", deadline.Token);
            RequireType(await ReceiveJsonAsync(socket, deadline.Token), "session.updated");
            var receiving = Task.Run(async () =>
            {
                while (true)
                {
                    JsonElement message = await ReceiveJsonAsync(socket, deadline.Token);
                    observation.Observe(message);
                    string? type = message.GetProperty("type").GetString();
                    if (type == "conversation.item.input_audio_transcription.completed")
                    { result.FinalEvents++; result.FinalCharacters += message.GetProperty("transcript").GetString()?.Length ?? 0; }
                    if (type == "error") throw new FixtureException("native_server_error");
                    if (type == "input_audio_buffer.committed") { result.SessionCompleted = result.CompletedAfterCommit > 0; return; }
                }
            }, deadline.Token);
            try
            {
                for (int offset = 0; offset < audio.Length; offset += 2560)
                {
                    await socket.SendAsync(audio.Slice(offset, Math.Min(2560, audio.Length - offset)), WebSocketMessageType.Binary, true, deadline.Token);
                    await Task.Delay(80, deadline.Token);
                }
                observation.MarkCommit();
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "input_audio_buffer.commit" }), WebSocketMessageType.Text, true, deadline.Token);
                await receiving.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);
            }
            finally
            {
                deadline.Cancel(); socket.Abort();
                try { await receiving; } catch { }
            }
        }
        finally { result.ElapsedMs = clock.ElapsedMilliseconds; }
    }

    private static async Task UpdateAsync(ClientWebSocket socket, string language, CancellationToken token) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "session.update", session = new
        { sample_rate = 16000, language, automatic_punctuation = true, speaker_diarization = false, word_timestamps = false } }), WebSocketMessageType.Text, true, token);

    private static Uri WebSocketEndpoint(Uri endpoint) => new UriBuilder(endpoint) { Scheme = "ws", Path = "/v1/audio/transcriptions/realtime" }.Uri;
    private static void RequireType(JsonElement message, string type)
    { if (!message.TryGetProperty("type", out var value) || value.GetString() != type) throw new FixtureException("unexpected_handshake"); }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var buffer = new MemoryStream(); byte[] chunk = new byte[8192];
        while (true)
        {
            var item = await socket.ReceiveAsync(chunk.AsMemory(), token);
            if (item.MessageType != WebSocketMessageType.Text || buffer.Length + item.Count > 65536) throw new FixtureException("invalid_wire_message");
            buffer.Write(chunk, 0, item.Count);
            if (!item.EndOfMessage) continue;
            using var document = JsonDocument.Parse(buffer.ToArray()); return document.RootElement.Clone();
        }
    }

    private static byte[] ReadPcmWave(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new FixtureException("invalid_synthetic_wave");
        bool valid = false;
        for (int offset = 12; offset + 8 <= bytes.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (size < 0 || (long)offset + 8 + size > bytes.Length) throw new FixtureException("invalid_synthetic_wave");
            if (bytes.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                var f = bytes.AsSpan(offset + 8, size);
                valid = size >= 16 && BinaryPrimitives.ReadInt16LittleEndian(f) == 1 &&
                    BinaryPrimitives.ReadInt16LittleEndian(f[2..]) == 1 && BinaryPrimitives.ReadInt32LittleEndian(f[4..]) == 16000 &&
                    BinaryPrimitives.ReadInt16LittleEndian(f[14..]) == 16;
            }
            if (bytes.AsSpan(offset, 4).SequenceEqual("data"u8))
            { if (!valid || size % 2 != 0) throw new FixtureException("invalid_synthetic_wave"); return bytes.AsSpan(offset + 8, size).ToArray(); }
            offset = checked(offset + 8 + size + (size & 1));
        }
        throw new FixtureException("invalid_synthetic_wave");
    }

    private static int FirstVoicedSample(byte[] pcm)
    {
        for (int offset = 0; offset < pcm.Length; offset += 2)
            if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset, 2))) > 200)
                return Math.Max(0, offset - 1600); // Keep 50 ms of leading context.
        return -1;
    }
    private static double Rms(ReadOnlySpan<byte> pcm)
    {
        double sum = 0;
        for (int i = 0; i < pcm.Length; i += 2) { double n = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]) / 32768d; sum += n * n; }
        return Math.Round(Math.Sqrt(sum / (pcm.Length / 2)), 6);
    }
    private static string ErrorCode(Exception error, CancellationToken token) => error switch
    {
        FixtureException fixture => fixture.Code,
        VoxtralFoxException protocol => protocol.Code,
        OperationCanceledException => token.IsCancellationRequested ? "total_budget_exceeded" : "case_deadline_exceeded",
        TimeoutException => "raw_completion_timeout",
        HttpRequestException => "http_error",
        WebSocketException => "websocket_error",
        _ => error.GetType().Name
    };
    private static void Print(CaseResult item) => Console.WriteLine($"{item.Client} {item.Kind} {item.AudioSeconds:0.0}s: {(item.Pass ? "passed" : item.Error)}; final events={item.FinalEvents}, native completed={item.NativeCompleted}, committed={item.Committed}, elapsed={item.ElapsedMs}ms");

    private sealed class FixtureException(string code) : Exception(code) { internal string Code => code; }
    private sealed class Receipt
    {
        public string Schema { get; set; } = "saywhat-pinned-speech-smoke-v1";
        public string SourceCommit { get; set; } = "";
        public string RuntimeVersion { get; set; } = "0.2.0";
        public string ModelRevision { get; set; } = "ea30d66debe3740a08b573244286791d423d6b3e";
        public string Backend { get; set; } = "cpu";
        public int CpuThreads { get; set; }
        public int LogicalProcessors { get; set; }
        public int WindowsBuild { get; set; }
        public string Scope { get; set; } = "Synthetic file audio and native protocol only. Not a GPU, Windows 10, microphone, translation, OSC or accuracy benchmark.";
        public string? ChineseHandshake { get; set; }
        public bool Pass { get; set; }
        public string? Error { get; set; }
        public List<CaseResult> Cases { get; } = [];
    }
    private sealed class CaseResult
    {
        public string Kind { get; set; } = "";
        public double AudioSeconds { get; set; }
        public string Client { get; set; } = "";
        public double Rms { get; set; }
        public long ReadyMs { get; set; }
        public long ElapsedMs { get; set; }
        public int DeltaEvents { get; set; }
        public int NonemptyDeltaEvents { get; set; }
        public int NativeCompleted { get; set; }
        public int CompletedAfterCommit { get; set; }
        public int Committed { get; set; }
        public int NativeErrors { get; set; }
        public int FinalEvents { get; set; }
        public int FinalCharacters { get; set; }
        public long? FirstDeltaMs { get; set; }
        public long? FirstCompletedMs { get; set; }
        public long? CommitSentMs { get; set; }
        public long? CommitAcknowledgedMs { get; set; }
        public bool SessionCompleted { get; set; }
        public bool Pass { get; set; }
        public string? Error { get; set; }
    }

    private sealed class WireObservation(CaseResult result)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        internal void MarkCommit() => result.CommitSentMs = _clock.ElapsedMilliseconds;
        internal void Observe(JsonElement message)
        {
            if (!message.TryGetProperty("type", out var type)) return;
            switch (type.GetString())
            {
                case "conversation.item.input_audio_transcription.delta":
                    result.DeltaEvents++;
                    if (message.TryGetProperty("delta", out var delta) && delta.GetString()?.Length > 0)
                    { result.NonemptyDeltaEvents++; result.FirstDeltaMs ??= _clock.ElapsedMilliseconds; }
                    break;
                case "conversation.item.input_audio_transcription.completed":
                    result.NativeCompleted++; if (result.CommitSentMs is not null) result.CompletedAfterCommit++;
                    result.FirstCompletedMs ??= _clock.ElapsedMilliseconds; break;
                case "input_audio_buffer.committed": result.Committed++; result.CommitAcknowledgedMs = _clock.ElapsedMilliseconds; break;
                case "error": result.NativeErrors++; break;
            }
        }
    }
    private sealed class ObservedFactory(WireObservation observation) : IVoxtralWebSocketFactory
    { public IVoxtralWebSocket Create() => new ObservedSocket(observation); }
    private sealed class ObservedSocket(WireObservation observation) : IVoxtralWebSocket
    {
        private readonly ClientVoxtralWebSocket _socket = new();
        private readonly MemoryStream _message = new();
        public WebSocketState State => _socket.State;
        public Task ConnectAsync(Uri endpoint, string? apiKey, CancellationToken token) => _socket.ConnectAsync(endpoint, apiKey, token);
        public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool last, CancellationToken token)
        {
            if (type == WebSocketMessageType.Text)
            {
                using var doc = JsonDocument.Parse(buffer);
                if (doc.RootElement.GetProperty("type").GetString() == "input_audio_buffer.commit") observation.MarkCommit();
            }
            await _socket.SendAsync(buffer, type, last, token);
        }
        public async ValueTask<VoxtralSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
        {
            var item = await _socket.ReceiveAsync(buffer, token);
            if (item.MessageType == WebSocketMessageType.Text)
            {
                if (_message.Length + item.Count > 65536) throw new FixtureException("invalid_wire_message");
                _message.Write(buffer.Span[..item.Count]);
                if (item.EndOfMessage)
                {
                    using var doc = JsonDocument.Parse(_message.ToArray()); observation.Observe(doc.RootElement); _message.SetLength(0);
                }
            }
            return item;
        }
        public Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) => _socket.CloseOutputAsync(status, description, token);
        public async ValueTask DisposeAsync() { _message.Dispose(); await _socket.DisposeAsync(); }
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task _stdout, _stderr;
        private Server(Process process, int port)
        {
            _process = process; Endpoint = new Uri($"http://127.0.0.1:{port}");
            // Drain privately, never publish native logs: some runtimes include
            // recognized text in diagnostic output. Only fixed metadata leaves.
            _stdout = DrainAsync(process.StandardOutput); _stderr = DrainAsync(process.StandardError);
        }
        internal Uri Endpoint { get; }
        internal long ReadyMs { get; private set; }
        internal static async Task<Server> StartAsync(string executable, string model, CancellationToken token)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            // Match NativeSpeechProvisioner.ServerArguments, except that this
            // hosted fixture explicitly selects CPU instead of vulkan:0.
            foreach (string argument in new[] { "serve", "--asr-model", model, "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--asr.enabled=true", "--tts.enabled=false", "--nmt.enabled=false", "--asr.endpointing.enable=false", "--asr.batching.enabled=false",
                "--asr.streaming.rnnt_right_context=3", "--asr.backend.threads=2", "--device", "cpu", "--no-ui", "--threads", "2" }) info.ArgumentList.Add(argument);
            info.Environment["NEMO_SPEECH_MODEL_DIR"] = Path.GetDirectoryName(model)!;
            var server = new Server(Process.Start(info) ?? throw new FixtureException("server_start_failed"), port);
            var clock = Stopwatch.StartNew();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                while (clock.Elapsed < TimeSpan.FromSeconds(90))
                {
                    token.ThrowIfCancellationRequested();
                    if (server._process.HasExited) throw new FixtureException("server_exited_before_ready");
                    try { await NemotronReadiness.CheckAsync(http, server.Endpoint, token); server.ReadyMs = clock.ElapsedMilliseconds; return server; }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or VoxtralFoxException) { }
                    await Task.Delay(250, token);
                }
                throw new FixtureException("server_ready_timeout");
            }
            catch { await server.DisposeAsync(); throw; }
        }
        private static async Task DrainAsync(StreamReader reader)
        {
            char[] chunk = new char[4096];
            try { while (await reader.ReadAsync(chunk) > 0) { } } catch (IOException) { }
        }
        public async ValueTask DisposeAsync()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            _process.Dispose();
        }
    }
}

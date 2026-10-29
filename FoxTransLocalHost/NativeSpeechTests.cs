using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

internal static class NativeSpeechTests
{
    internal static async Task RunAsync()
    {
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Native recognition fixture: " + message); Console.WriteLine("PASS: " + message); }
        using (var json = JsonDocument.Parse("""{"OwnRecognitionEngine":"nemotron","OwnVoiceSourceLanguage":"Chinese"}"""))
            Check(LocalHost.ResolveRecognitionPreferences(json.RootElement) == new LocalHost.OwnRecognitionPreferences(true, "zh-CN"),
                "native recognizer receives the explicitly selected Chinese language");
        using (var json = JsonDocument.Parse("{}"))
            Check(!LocalHost.ResolveRecognitionPreferences(json.RootElement).Native, "legacy host preferences preserve Voxtral without implicitly switching providers");
        foreach (var (name, code) in new[] { ("English", "en-US"), ("Japanese", "ja-JP"), ("Korean", "ko-KR"), ("Automatic", "auto") })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { OwnRecognitionEngine = "nemotron", OwnVoiceSourceLanguage = name }));
            Check(LocalHost.ResolveRecognitionPreferences(json.RootElement).Language == code, "native language selection maps " + name);
        }
        foreach (string source in new[] { "Thai", "th-TH", "unsupported" })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { OwnRecognitionEngine = "nemotron", OwnVoiceSourceLanguage = source }));
            bool refused = false;
            try { _ = LocalHost.ResolveRecognitionPreferences(json.RootElement); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "unsupported explicit microphone languages never silently fall back to automatic: " + source);
        }
        bool remoteRejected = false;
        try { _ = new NemotronTranscriber(new("https://example.com")); } catch (ArgumentException) { remoteRejected = true; }
        Check(remoteRejected, "native speech adapter refuses remote/cloud endpoints");

        var socket = new NativeSocket();
        await using (var provider = new NemotronTranscriber(new("http://127.0.0.1:8080"), "zh-CN", new NativeFactory(socket)))
        {
            var events = new List<StreamingTranscriptionEvent>();
            await foreach (var item in provider.TranscribeAsync(Audio(), CancellationToken.None)) events.Add(item);
            Check(socket.Language == "zh-CN" && socket.SampleRate == 16000 && socket.BinaryBytes == 3200 && socket.Commits == 1,
                "native protocol configures language before sending exactly the captured PCM and one commit");
            Check(events.OfType<StreamingPartialTranscript>().Single().Text == "partial source" &&
                events.OfType<StreamingFinalTranscript>().Single().Text == "authoritative completed sentence" &&
                events[^1] is StreamingSessionCompleted && events.OfType<StreamingSessionCompleted>().Single().AudioDurationMs == 100,
                "native authoritative completion replaces partial text and precedes the mapped session acknowledgment");
            Check(socket.Endpoint?.AbsolutePath == "/v1/audio/transcriptions/realtime" && socket.Closed,
                "native adapter uses its own websocket contract and releases the connection after the captured turn");
        }
        foreach (string mode in new[] { "missing-final", "missing-ack", "invalid-json", "server-error", "bad-start" })
        {
            var failedSocket = new NativeSocket { Failure = mode };
            await using var provider = new NemotronTranscriber(new("http://127.0.0.1:8080"), "ja-JP", new NativeFactory(failedSocket));
            bool failed = false; var seen = new List<StreamingTranscriptionEvent>();
            try { await foreach (var item in provider.TranscribeAsync(Audio(), CancellationToken.None)) seen.Add(item); }
            catch (VoxtralFoxException) { failed = true; }
            Check(failed && !seen.OfType<StreamingSessionCompleted>().Any() && !seen.OfType<StreamingFinalTranscript>().Any(),
                "native " + mode + " cannot promote partial or unacknowledged text to an application final");
        }
        var invalidAudioSocket = new NativeSocket();
        await using (var provider = new NemotronTranscriber(new("http://127.0.0.1:8080"), "auto", new NativeFactory(invalidAudioSocket)))
        {
            bool failed = false;
            try { await foreach (var item in provider.TranscribeAsync(Audio(new(48000, 16, 2)), CancellationToken.None)) { } }
            catch (VoxtralFoxException) { failed = true; }
            Check(failed && invalidAudioSocket.BinaryBytes == 0 && invalidAudioSocket.Commits == 0,
                "native adapter rejects unsupported PCM before transmitting audio");
        }
        foreach (string text in new[] { "SIL", "<sil>", "[Silence]", "SIL International" })
        {
            var controlSocket = new NativeSocket { PartialText = text, CompletedText = text };
            await using var provider = new NemotronTranscriber(new("http://127.0.0.1:8080"), "auto", new NativeFactory(controlSocket));
            var events = new List<StreamingTranscriptionEvent>();
            await foreach (var item in provider.TranscribeAsync(Audio(), CancellationToken.None)) events.Add(item);
            bool control = text != "SIL International";
            Check(events.OfType<StreamingFinalTranscript>().Single().Text == (control ? "" : text) &&
                events.OfType<StreamingPartialTranscript>().Count() == (control ? 0 : 1) && events[^1] is StreamingSessionCompleted,
                "own Nemotron filters control-only words while preserving mixed speech and completion: " + text);
        }
        using (var handler = new ReadinessHandler("{\"ready\":true}"))
        using (var http = new HttpClient(handler))
        {
            await NemotronReadiness.CheckAsync(http, new("http://127.0.0.1:8080/health"), CancellationToken.None);
            Check(handler.Path == "/ready", "native health check uses readiness, not Voxtral's incompatible health schema");
        }
        foreach (string body in new[] { "{\"ready\":false}", "{\"status\":\"ok\"}", "not json" })
        {
            using var handler = new ReadinessHandler(body); using var http = new HttpClient(handler);
            bool failed = false;
            try { await NemotronReadiness.CheckAsync(http, new("http://127.0.0.1:8080"), CancellationToken.None); }
            catch (VoxtralFoxException) { failed = true; }
            Check(failed, "native readiness refuses an unavailable or invalid model response");
        }
        LivePreferenceChecks(Check);
    }
    private static void LivePreferenceChecks(Action<bool, string> check)
    {
        var original = new LocalHost.OwnVoiceLivePreferences(1200, true);
        using (var json = JsonDocument.Parse("""{"OwnVoiceEndPauseMilliseconds":1700,"OwnVoiceSendEarlyPreviews":false}"""))
            check(LocalHost.ResolveLivePreferences(json.RootElement, original) == new LocalHost.OwnVoiceLivePreferences(1700, false),
                "live preferences parse timing and early previews together without starting a model");
        using (var json = JsonDocument.Parse("{}"))
            check(LocalHost.ResolveLivePreferences(json.RootElement, original) == original, "partial legacy preferences preserve validated live values");
        foreach (string value in new[] { "{\"OwnVoiceEndPauseMilliseconds\":10}", "{\"OwnVoiceEndPauseMilliseconds\":\"1200\"}",
            "{\"OwnVoiceSendEarlyPreviews\":\"true\"}", "[]" })
        {
            using var json = JsonDocument.Parse(value); bool rejected = false;
            try { _ = LocalHost.ResolveLivePreferences(json.RootElement, original); } catch (JsonException) { rejected = true; }
            check(rejected, "invalid live settings cannot replace the last working capture preferences");
        }
        var inert = new InertAudio();
        var capture = new ContinuousCapture(inert, () => { }, new(16000, 16, 1), 1200);
        capture.SetPauseMilliseconds(1500);
        check(capture.PauseMilliseconds == 1500 && capture.Completion.IsCompleted, "pause can change on the existing capture object without opening a microphone");
        bool failed = false;
        try { capture.SetPauseMilliseconds(0); } catch (ArgumentOutOfRangeException) { failed = true; }
        check(failed && capture.PauseMilliseconds == 1500, "out-of-range live pause leaves the working threshold unchanged");
    }
    private static async IAsyncEnumerable<AudioFrame> Audio(AudioFormat? format = null,
        [EnumeratorCancellation] CancellationToken token = default)
    { for (int i = 0; i < 5; i++) { token.ThrowIfCancellationRequested(); yield return new(new byte[640], format ?? new(16000, 16, 1)); } await Task.CompletedTask; }
    private sealed class InertAudio : IAudioSource
    {
        public AudioFormat Format => new(16000, 16, 1);
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        { await Task.CompletedTask; yield break; }
    }
    private sealed class NativeFactory(NativeSocket socket) : IVoxtralWebSocketFactory
    { public IVoxtralWebSocket Create() => socket; }
    private sealed class NativeSocket : IVoxtralWebSocket
    {
        private readonly Channel<byte[]> _events = Channel.CreateUnbounded<byte[]>();
        public WebSocketState State { get; private set; }
        public string Failure = "";
        public string PartialText = "partial source", CompletedText = "authoritative completed sentence";
        public string? Language;
        public int SampleRate, BinaryBytes, Commits;
        public Uri? Endpoint;
        public bool Closed;
        private void Queue(object value) => _events.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        public Task ConnectAsync(Uri endpoint, string? key, CancellationToken token)
        { Endpoint = endpoint; State = WebSocketState.Open; Queue(new { type = Failure == "bad-start" ? "unexpected" : "session.created", session = new { sample_rate = 16000 } }); return Task.CompletedTask; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, bool end, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (type == WebSocketMessageType.Binary)
            {
                if (BinaryBytes == 0)
                {
                    if (Failure == "invalid-json") _events.Writer.TryWrite(Encoding.UTF8.GetBytes("not json"));
                    else if (Failure == "server-error") Queue(new { type = "error", error = new { message = "private diagnostic", type = "invalid_request_error" } });
                    else Queue(new { type = "conversation.item.input_audio_transcription.delta", delta = PartialText, audio_processed = 0.08 });
                }
                BinaryBytes += bytes.Length;
            }
            else
            {
                using var json = JsonDocument.Parse(bytes);
                switch (json.RootElement.GetProperty("type").GetString())
                {
                    case "session.update":
                        Language = json.RootElement.GetProperty("session").GetProperty("language").GetString();
                        SampleRate = json.RootElement.GetProperty("session").GetProperty("sample_rate").GetInt32();
                        Queue(new { type = "session.updated" }); break;
                    case "input_audio_buffer.commit":
                        Commits++;
                        if (Failure != "missing-final") Queue(new { type = "conversation.item.input_audio_transcription.completed", transcript = CompletedText, audio_processed = 0.1 });
                        if (Failure == "missing-ack") _events.Writer.TryWrite([0]);
                        else Queue(new { type = "input_audio_buffer.committed" });
                        break;
                }
            }
            return ValueTask.CompletedTask;
        }
        public async ValueTask<VoxtralSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
        {
            byte[] data = await _events.Reader.ReadAsync(token);
            if (data.Length == 1 && data[0] == 0)
            { State = WebSocketState.CloseReceived; return new(0, WebSocketMessageType.Close, true); }
            data.CopyTo(buffer); return new(data.Length, WebSocketMessageType.Text, true);
        }
        public Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token)
        { Closed = true; State = WebSocketState.Closed; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Closed = true; State = WebSocketState.Closed; return ValueTask.CompletedTask; }
    }
    private sealed class ReadinessHandler(string body) : HttpMessageHandler
    {
        public string? Path;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Path = request.RequestUri!.AbsolutePath; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }); }
    }
}

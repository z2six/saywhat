using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

// Adapter for the pinned NeMo-Speech.cpp v0.2.0 transcription protocol. This is
// deliberately separate from Voxtral's protocol, even though both use local PCM.
public sealed class NemotronTranscriber : IStreamingTranscriber
{
    private readonly Uri _endpoint;
    private readonly string _language;
    private readonly IVoxtralWebSocketFactory _factory;
    private int _started, _disposed, _commitRequested;
    private long _sentSamples;
    public NemotronTranscriber(Uri server, string language = "auto", IVoxtralWebSocketFactory? socketFactory = null)
    {
        if (!server.IsAbsoluteUri || !server.IsLoopback || server.Scheme is not "http" and not "https")
            throw new ArgumentException("Speech recognition must use a local HTTP endpoint.", nameof(server));
        _endpoint = new UriBuilder(server) { Scheme = server.Scheme == "https" ? "wss" : "ws",
            Path = "/v1/audio/transcriptions/realtime", Query = "", Fragment = "" }.Uri;
        _language = string.IsNullOrWhiteSpace(language) ? "auto" : language;
        _factory = socketFactory ?? new ClientVoxtralWebSocketFactory();
    }

    public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(IAsyncEnumerable<AudioFrame> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("A recognizer owns one captured speech session.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = Channel.CreateBounded<StreamingTranscriptionEvent>(new BoundedChannelOptions(32)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        Task session = RunSessionAsync(audio, events.Writer, lifetime);
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken)) yield return item;
            await session;
        }
        finally
        {
            lifetime.Cancel();
            try { await session; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task RunSessionAsync(IAsyncEnumerable<AudioFrame> audio, ChannelWriter<StreamingTranscriptionEvent> output,
        CancellationTokenSource lifetime)
    {
        await using var socket = _factory.Create();
        Task? sending = null, receiving = null;
        try
        {
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await socket.ConnectAsync(_endpoint, null, startup.Token);
                    JsonElement created = await ReceiveAsync(socket, startup.Token);
                    RequireType(created, "session.created");
                    await SendJsonAsync(socket, new { type = "session.update", session = new
                        { sample_rate = 16000, language = _language, automatic_punctuation = true,
                            speaker_diarization = false, word_timestamps = false } }, startup.Token);
                    RequireType(await ReceiveAsync(socket, startup.Token), "session.updated");
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                { throw Protocol("session_start_timeout", "The local recognizer did not finish configuring within five seconds."); }
            }
            await output.WriteAsync(new StreamingSessionStarted("local-native", 1, "nemotron", 0, 1), lifetime.Token);
            sending = SendAudioAsync(socket, audio, lifetime.Token);
            receiving = ReceiveTranscriptsAsync(socket, output, lifetime.Token);
            Task first = await Task.WhenAny(sending, receiving);
            await first;
            if (ReferenceEquals(first, sending))
            {
                try { await receiving.WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token); }
                catch (TimeoutException) { throw Protocol("session_completion_timeout", "The local recognizer did not acknowledge the captured sentence."); }
            }
            else await sending;
        }
        catch (Exception error) { output.TryComplete(error); }
        finally
        {
            lifetime.Cancel();
            if (sending is not null) await ObserveAsync(sending);
            if (receiving is not null) await ObserveAsync(receiving);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Captured speech ended", closing.Token); }
                catch { /* Shutdown cannot promote an unacknowledged transcript. */ }
            }
            output.TryComplete();
        }
    }

    private async Task SendAudioAsync(IVoxtralWebSocket socket, IAsyncEnumerable<AudioFrame> audio, CancellationToken token)
    {
        await foreach (var chunk in PcmChunkAggregator.AggregateAsync(ValidateAudio(audio, token), token))
        {
            if (chunk.IsEmpty) continue;
            await SendBoundedAsync(socket, chunk, WebSocketMessageType.Binary, token);
            Interlocked.Add(ref _sentSamples, chunk.Length / 2);
        }
        Interlocked.Exchange(ref _commitRequested, 1);
        await SendJsonAsync(socket, new { type = "input_audio_buffer.commit" }, token);
    }

    private async Task ReceiveTranscriptsAsync(IVoxtralWebSocket socket, ChannelWriter<StreamingTranscriptionEvent> output, CancellationToken token)
    {
        long sequence = 0;
        long lastAudioEnd = 0;
        string partial = "", confirmed = "";
        bool sawNativeFinal = false, sawFinalAfterCommit = false;
        while (true)
        {
            JsonElement message = await ReceiveAsync(socket, token);
            string type = RequiredString(message, "type");
            long sentAudioEnd = Interlocked.Read(ref _sentSamples) * 1000 / 16000;
            long audioEnd = sentAudioEnd;
            if (message.TryGetProperty("audio_processed", out var processed) && processed.ValueKind == JsonValueKind.Number &&
                processed.TryGetDouble(out double seconds) && double.IsFinite(seconds) && seconds >= 0)
                audioEnd = (long)Math.Clamp(seconds * 1000, 0, sentAudioEnd);
            audioEnd = Math.Max(lastAudioEnd, audioEnd);
            lastAudioEnd = audioEnd;
            if (type == "conversation.item.input_audio_transcription.delta")
            {
                // v0.2.0 has no revision marker: like its reference client, append
                // deltas. The completed transcript below is authoritative and
                // replaces this speculative text, including any revised wording.
                string delta = RequiredString(message, "delta");
                if (delta.Length == 0) continue; // The native server reports many empty processing-progress deltas.
                partial += delta;
                if (partial.Length + confirmed.Length > 16384)
                    throw Protocol("transcript_limit", "Recognition exceeded the bounded text limit.");
                await output.WriteAsync(new StreamingPartialTranscript(++sequence, confirmed + partial, audioEnd), token);
            }
            else if (type == "conversation.item.input_audio_transcription.completed")
            {
                string final = RequiredString(message, "transcript");
                confirmed += final;
                partial = ""; sawNativeFinal = true;
                sawFinalAfterCommit |= Volatile.Read(ref _commitRequested) != 0;
                if (confirmed.Length > 16384) throw Protocol("transcript_limit", "Recognition exceeded the bounded text limit.");
                // No application final is emitted yet. The server must finish
                // the client commit successfully, not merely emit text early.
            }
            else if (type == "input_audio_buffer.committed")
            {
                if (Volatile.Read(ref _commitRequested) == 0 || !sawNativeFinal || !sawFinalAfterCommit)
                    throw Protocol("invalid_event", "The recognizer acknowledged audio without an authoritative completed transcript.");
                await output.WriteAsync(new StreamingFinalTranscript(++sequence, confirmed, audioEnd), token);
                await output.WriteAsync(new StreamingSessionCompleted(++sequence, audioEnd), token);
                return;
            }
            else if (type is "input_audio_buffer.cleared" or "session.created" or "session.updated")
                throw Protocol("invalid_event", "The local recognizer unexpectedly reset its active session.");
            else throw Protocol("invalid_event", "The local recognizer sent an unsupported transcription event.");
        }
    }

    private static async IAsyncEnumerable<AudioFrame> ValidateAudio(IAsyncEnumerable<AudioFrame> audio,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var frame in audio.WithCancellation(token))
        {
            if (frame.Format != new AudioFormat(16000, 16, 1) || (frame.Pcm.Length & 1) != 0)
                throw Protocol("unsupported_audio_format", "Recognition requires mono 16000 Hz signed PCM16LE.");
            yield return frame;
        }
    }

    private static async Task<JsonElement> ReceiveAsync(IVoxtralWebSocket socket, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            var received = await socket.ReceiveAsync(chunk, token);
            if (received.MessageType == WebSocketMessageType.Close)
                throw Protocol("unexpected_close", "The local recognizer closed before the captured sentence was acknowledged.");
            if (received.MessageType != WebSocketMessageType.Text || buffer.Length + received.Count > 65536)
                throw Protocol("invalid_event", "The local recognizer sent an invalid or oversized event.");
            buffer.Write(chunk, 0, received.Count);
            if (!received.EndOfMessage) continue;
            try
            {
                using var parsed = JsonDocument.Parse(buffer.ToArray());
                JsonElement value = parsed.RootElement.Clone();
                if (value.ValueKind != JsonValueKind.Object) throw Protocol("invalid_event", "The local recognizer event is not an object.");
                if (RequiredString(value, "type") == "error")
                    throw Protocol("native_server_error", "The local recognizer rejected the request. Check its startup log and chosen language.");
                return value;
            }
            catch (JsonException error) { throw new VoxtralFoxException("invalid_event", "The local recognizer returned malformed JSON.", inner: error); }
        }
    }
    private static string RequiredString(JsonElement parent, string property) => parent.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
        ? item.GetString()! : throw Protocol("invalid_event", "The local recognizer event is missing a required text field.");
    private static void RequireType(JsonElement message, string expected)
    { if (RequiredString(message, "type") != expected) throw Protocol("invalid_event", "The local recognizer sent an unexpected configuration acknowledgment."); }
    private static ValueTask SendJsonAsync(IVoxtralWebSocket socket, object value, CancellationToken token) =>
        SendBoundedAsync(socket, JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, token);
    private static async ValueTask SendBoundedAsync(IVoxtralWebSocket socket, ReadOnlyMemory<byte> bytes, WebSocketMessageType type, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try { await socket.SendAsync(bytes, type, true, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Protocol("audio_send_timeout", "The local recognizer stopped accepting captured audio."); }
    }
    private static VoxtralFoxException Protocol(string code, string message) => new(code, message);
    private static async Task ObserveAsync(Task task) { try { await task; } catch { } }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }
}

public static class NemotronReadiness
{
    public static async Task CheckAsync(HttpClient http, Uri server, CancellationToken token)
    {
        if (!server.IsLoopback) throw new ArgumentException("Recognition must use a local endpoint.");
        using var response = await http.GetAsync(new Uri(server, "/ready"), HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new VoxtralFoxException("server_not_ready", "The local recognizer is not ready.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[] bytes = new byte[16385]; int total = 0, count;
        while (total < bytes.Length && (count = await stream.ReadAsync(bytes.AsMemory(total), token)) != 0) total += count;
        if (total == bytes.Length) throw new VoxtralFoxException("invalid_health_response", "Recognition readiness exceeded its safe size limit.");
        try
        {
            using var json = JsonDocument.Parse(bytes.AsMemory(0, total));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True)
                throw new VoxtralFoxException("server_not_ready", "The local recognizer is still loading.");
        }
        catch (JsonException error) { throw new VoxtralFoxException("invalid_health_response", "Recognition readiness returned malformed JSON.", inner: error); }
    }
}

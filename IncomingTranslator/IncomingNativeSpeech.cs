using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace IncomingTranslator;

// NeMo-Speech.cpp's protocol is intentionally separate from the Voxtral
// protocol. Both use the same bounded capture queue and subtitle publication.
internal sealed partial class IncomingTranslationService
{
    // Decoder progress includes audio that produced no words. Keep this apart
    // from the real-text timestamp used to attach a person's voice evidence.
    private DateTimeOffset? _lastDecodedAudioEnd;

    private void ObserveDecodedAudio(DateTimeOffset? audioEnd)
    {
        if (audioEnd is not DateTimeOffset acknowledged) return;
        lock (_gate)
            if (_lastDecodedAudioEnd is null || acknowledged > _lastDecodedAudioEnd.Value)
                _lastDecodedAudioEnd = acknowledged;
    }

    private void ResetDecodedAudioPosition()
    {
        lock (_gate) _lastDecodedAudioEnd = null;
    }

    private async Task ObserveNativeSubtitleUpdateAsync(NativeSubtitleUpdate update)
    {
        // A sent-audio offset is retained on the protocol result for legacy
        // display compatibility, but cannot prove that inference reached it.
        DateTimeOffset? audioEnd = update.VerifiedAudioEndMilliseconds is long verified
            ? _sentAudioTimeline.Map(verified) : null;
        ObserveDecodedAudio(audioEnd);
        if (update.Text is not string text) return;
        Interlocked.Increment(ref _transcriptEvents);
        _lastTranscriptAt = DateTimeOffset.UtcNow;
        await ObserveTranscriptAsync(text, audioEnd);
    }

    internal static object NativeSubtitleConfiguration(string sourceLanguage) => new
    {
        type = "session.update",
        session = new
        {
            sample_rate = 16000,
            language = IncomingRecognitionCatalog.LanguageCode(sourceLanguage),
            automatic_punctuation = true,
            speaker_diarization = false,
            word_timestamps = false
        }
    };

    internal static async Task ConfigureNativeSubtitleSessionAsync(
        Func<CancellationToken, Task<JsonElement>> receive,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> send,
        string sourceLanguage, CancellationToken token)
    {
        NativeSubtitleTranscript.RequireType(await receive(token), "session.created");
        await send(JsonSerializer.SerializeToUtf8Bytes(NativeSubtitleConfiguration(sourceLanguage)), token);
        NativeSubtitleTranscript.RequireType(await receive(token), "session.updated");
    }

    private async Task RunNativeSpeechSessionAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var socket = new ClientWebSocket();
        _socket = socket;
        Task? sender = null, receiver = null, watchdog = null;
        var transcript = new NativeSubtitleTranscript();
        int commitRequested = 0;
        long samplesSent = 0;
        bool firstChunk = true;
        try
        {
            await AwaitSessionCreatedAsync(async startup =>
            {
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:8081/v1/audio/transcriptions/realtime"), startup);
                await ConfigureNativeSubtitleSessionAsync(receiveToken => ReceiveNativeSubtitleJsonAsync(socket, receiveToken),
                    (bytes, sendToken) => socket.SendAsync(bytes, WebSocketMessageType.Text, true, sendToken).AsTask(),
                    CurrentSettings().IncomingSourceLanguage, startup);
                return "configured";
            }, TimeSpan.FromSeconds(5), lifetime.Token);

            InitializeNativeSubtitleSession();
            await ReportHealthAsync(_awaitingRecognitionProgress ? "recovering" : "listening",
                _awaitingRecognitionProgress ? "Listening again… waiting for new words." : ListeningStatus(CurrentSettings()),
                _awaitingRecognitionProgress);
            Log("Continuous native subtitle recognition connected; chosen spoken language applied, capture remains continuous during silence.");

            sender = IncomingContinuousSessionPolicy.StreamAsync(_continuousAudio, async (chunk, sendToken) =>
            {
                if (firstChunk) { _audioTimelineStart = chunk.At; firstChunk = false; }
                _sentAudioTimeline.Append(chunk.At, chunk.Bytes.Length);
                await socket.SendAsync(chunk.Bytes.AsMemory(), WebSocketMessageType.Binary, true, sendToken);
                Interlocked.Add(ref samplesSent, chunk.Bytes.Length / 2);
                Interlocked.Add(ref _audioBytesSent, chunk.Bytes.Length);
                Interlocked.Exchange(ref _lastAudioSendTicks, DateTimeOffset.UtcNow.UtcTicks);
            }, async sendToken =>
            {
                _finalizingAudio = true;
                Interlocked.Exchange(ref commitRequested, 1);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "input_audio_buffer.commit" }),
                    WebSocketMessageType.Text, true, sendToken);
            }, () =>
            {
                lock (_gate) return IncomingContinuousSessionPolicy.ShouldRollOver(_latest, DateTimeOffset.UtcNow - _sessionStartedAt);
            }, lifetime.Token, TimeSpan.FromSeconds(3));

            receiver = ReceiveNativeSubtitlesAsync();
            watchdog = WatchdogAsync(lifetime.Token);
            await AwaitNaturalCompletionAsync(sender, receiver, watchdog, TimeSpan.FromSeconds(8), token,
                () => _continuousAudio.BufferedBytes > 0, TimeSpan.FromMilliseconds(1200));
            await FlushActiveAsync(token);
        }
        finally
        {
            lifetime.Cancel();
            socket.Abort();
            try { await Task.WhenAll(new[] { sender, receiver, watchdog }.OfType<Task>()); }
            catch { /* The primary connection/worker error is handled by the outer recovery loop. */ }
            _finalizingAudio = false;
            _connected = false;
            if (ReferenceEquals(_socket, socket)) _socket = null;
        }

        async Task ReceiveNativeSubtitlesAsync()
        {
            while (true)
            {
                JsonElement message = await ReceiveNativeSubtitleJsonAsync(socket, lifetime.Token);
                NativeSubtitleUpdate update = transcript.Observe(message, Interlocked.Read(ref samplesSent) / 16,
                    Volatile.Read(ref commitRequested) != 0);
                Interlocked.Exchange(ref _lastDecoderProgressTicks, DateTimeOffset.UtcNow.UtcTicks);
                await ObserveNativeSubtitleUpdateAsync(update);
                if (update.Completed) return;
            }
        }
    }

    // A session boundary changes text context, never the capture source or
    // speaker model. The source-language apply path creates a fresh service.
    private void InitializeNativeSubtitleSession()
    {
        ResetTranscriptState();
        ResetDecodedAudioPosition();
        _sessionStartedAt = DateTimeOffset.UtcNow;
        _audioTimelineStart = _sessionStartedAt;
        _sentAudioTimeline.Reset();
        _currentUtteranceId = Interlocked.Increment(ref _nextUtteranceId);
        _lastTextAudioEnd = null;
        _lastMeaningfulAudioEnd = null;
        _lastTranscriptAt = _sessionStartedAt;
        Interlocked.Exchange(ref _lastMeaningfulTicks, _sessionStartedAt.UtcTicks);
        Interlocked.Exchange(ref _lastDecoderProgressTicks, _sessionStartedAt.UtcTicks);
        Interlocked.Exchange(ref _lastAudioSendTicks, 0);
        _meaningfulTranscript = "";
        _blankRecoveryAttempts = 0;
        _utterancesInSession = 0;
        _finalizingAudio = false;
        _connected = true;
    }

    private static async Task<JsonElement> ReceiveNativeSubtitleJsonAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            WebSocketReceiveResult frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 65536)
                throw new InvalidDataException("The local subtitle recognizer closed or returned an invalid response.");
            message.Write(buffer, 0, frame.Count);
            if (!frame.EndOfMessage) continue;
            using JsonDocument parsed = JsonDocument.Parse(message.ToArray());
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The local subtitle recognizer returned a non-object response.");
            return parsed.RootElement.Clone();
        }
    }
}

internal readonly record struct NativeSubtitleUpdate(string? Text, long AudioEndMilliseconds, bool Completed,
    long? VerifiedAudioEndMilliseconds = null);

// Pure protocol state, also exercised without a socket, audio or model. Empty
// processing deltas prove decoder activity but are never treated as new words.
internal sealed class NativeSubtitleTranscript
{
    private string _confirmed = "", _partial = "";
    private bool _finalAfterCommit;
    private long _lastAudioEnd;

    internal static void RequireType(JsonElement message, string expected)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out JsonElement type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != expected)
            throw new InvalidDataException("The local subtitle recognizer sent an unexpected event.");
    }

    internal NativeSubtitleUpdate Observe(JsonElement message, long sentAudioMilliseconds, bool commitRequested)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out JsonElement typeValue) ||
            typeValue.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid subtitle recognition event.");
        long sentAudioEnd = Math.Max(0, sentAudioMilliseconds);
        long audioEnd = sentAudioEnd;
        long? verifiedAudioEnd = null;
        if (message.TryGetProperty("audio_processed", out JsonElement processed) && processed.ValueKind == JsonValueKind.Number &&
            processed.TryGetDouble(out double seconds) && double.IsFinite(seconds) && seconds >= 0)
        {
            double milliseconds = seconds * 1000;
            audioEnd = milliseconds >= sentAudioEnd ? sentAudioEnd : (long)milliseconds;
            // Tolerate sub-millisecond JSON/float rounding at the sent boundary,
            // but reject impossible future cursors as verified decoder proof.
            if (double.IsFinite(milliseconds) && milliseconds <= (double)sentAudioEnd + 1)
                verifiedAudioEnd = audioEnd;
        }
        audioEnd = _lastAudioEnd = Math.Max(_lastAudioEnd, audioEnd);
        string type = typeValue.GetString()!;
        if (type == "conversation.item.input_audio_transcription.delta")
        {
            string delta = Text("delta");
            if (delta.Length == 0) return new(null, audioEnd, false, verifiedAudioEnd);
            _partial += delta;
            CheckSize();
            return new(VisibleText(), audioEnd, false, verifiedAudioEnd);
        }
        if (type == "conversation.item.input_audio_transcription.completed")
        {
            _confirmed += RecognitionTextFilter.CleanControlOnly(Text("transcript"));
            _partial = "";
            _finalAfterCommit |= commitRequested;
            CheckSize();
            return new(VisibleText(), audioEnd, false, verifiedAudioEnd);
        }
        if (type == "input_audio_buffer.committed" && commitRequested && _finalAfterCommit)
            return new(VisibleText(), audioEnd, true, verifiedAudioEnd);
        throw new InvalidDataException(type == "error"
            ? "The local subtitle recognizer rejected the request. Check the selected spoken language."
            : "The local subtitle recognizer reset or ended without a verified final transcript.");

        string Text(string name) => message.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException("The recognition event is missing required text.");
        string? VisibleText()
        {
            string text = _confirmed + RecognitionTextFilter.CleanControlOnly(_partial);
            return text.Length == 0 ? null : text;
        }
        void CheckSize()
        {
            if (_confirmed.Length + _partial.Length > 16384)
                throw new InvalidDataException("Subtitle recognition exceeded the bounded text limit.");
        }
    }
}

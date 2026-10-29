namespace IncomingTranslator;

/// <summary>Bounded, RAM-only PCM queue. Speech guesses never control audio admission.</summary>
internal sealed class IncomingContinuousAudioBuffer
{
    internal const int NormalCapacityChunks = 25;
    internal const int HandoffCapacityChunks = 100;
    private readonly object _gate = new();
    private readonly Queue<IncomingPcmChunk> _chunks = new();
    // Bounded RAM-only recovery tail, including audio already sent to a decoder
    // that remained responsive but produced no words. Never written to disk.
    private readonly Queue<IncomingPcmChunk> _recent = new();
    private int _capacityChunks;
    private long _bufferedBytes, _droppedChunks;

    public IncomingContinuousAudioBuffer(int capacityChunks = NormalCapacityChunks)
    {
        if (capacityChunks < 1) throw new ArgumentOutOfRangeException(nameof(capacityChunks));
        _capacityChunks = capacityChunks;
    }

    public long BufferedBytes { get { lock (_gate) return _bufferedBytes; } }
    public long DroppedChunks { get { lock (_gate) return _droppedChunks; } }
    public int Count { get { lock (_gate) return _chunks.Count; } }
    public int CapacityMilliseconds { get { lock (_gate) return _capacityChunks * 80; } }

    public void Observe(byte[] pcm, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length == 0) return;
        lock (_gate)
        {
            var chunk = new IncomingPcmChunk(pcm.ToArray(), at);
            _chunks.Enqueue(chunk);
            _recent.Enqueue(chunk);
            while (_recent.Count > HandoffCapacityChunks) _recent.Dequeue();
            _bufferedBytes += pcm.Length;
            Trim();
        }
    }

    public bool TryRead(out IncomingPcmChunk chunk)
    {
        lock (_gate)
        {
            if (!_chunks.TryDequeue(out chunk)) return false;
            _bufferedBytes -= chunk.Bytes.Length;
            // A rare context handoff may temporarily retain more than two seconds.
            // Restore the normal bound only AFTER that audio has drained, not by
            // discarding the captured start of the next sentence on reconnect.
            if (_capacityChunks > NormalCapacityChunks && _chunks.Count <= NormalCapacityChunks)
                _capacityChunks = NormalCapacityChunks;
            return true;
        }
    }

    public void SetCapacity(int capacityChunks)
    {
        if (capacityChunks < 1) throw new ArgumentOutOfRangeException(nameof(capacityChunks));
        lock (_gate) { _capacityChunks = capacityChunks; Trim(); }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _chunks.Clear(); _recent.Clear(); _bufferedBytes = 0;
            _capacityChunks = Math.Min(_capacityChunks, NormalCapacityChunks);
            // Health/drop reporting is cumulative across capture changes.
        }
    }

    public long ReplayUnrecognizedAfter(DateTimeOffset? recognizedThrough)
    {
        lock (_gate)
        {
            _chunks.Clear(); _bufferedBytes = 0;
            _capacityChunks = HandoffCapacityChunks;
            foreach (IncomingPcmChunk recent in _recent)
            {
                IncomingPcmChunk chunk = recent;
                if (recognizedThrough is DateTimeOffset boundary)
                {
                    // PCM16 mono 16 kHz: cut only at a whole-sample boundary.
                    // Do not resend audio already covered by recognized text.
                    DateTimeOffset end = chunk.At.AddSeconds(chunk.Bytes.Length / 32000.0);
                    if (end <= boundary) continue;
                    if (chunk.At < boundary)
                    {
                        int samples = (int)Math.Ceiling((boundary - chunk.At).TotalSeconds * 16000);
                        int bytes = Math.Min(chunk.Bytes.Length, samples * 2);
                        if (bytes >= chunk.Bytes.Length) continue;
                        chunk = new(chunk.Bytes[bytes..], chunk.At.AddSeconds(bytes / 32000.0));
                    }
                }
                _chunks.Enqueue(chunk);
                _bufferedBytes += chunk.Bytes.Length;
            }
            // This replaces rather than concatenates pending PCM: every retained
            // capture chunk appears once, including new audio arriving at recovery.
            return _bufferedBytes;
        }
    }

    private void Trim()
    {
        while (_chunks.Count > _capacityChunks)
        {
            _bufferedBytes -= _chunks.Dequeue().Bytes.Length;
            _droppedChunks++;
        }
    }
}

internal static class IncomingContinuousSessionPolicy
{
    // The current server clips each epoch's partial text to 4095 UTF-8 bytes.
    // Drain a final before reaching that limit; this is NOT a speech/silence gate.
    public static bool ShouldRollOver(string cumulative, TimeSpan sessionAge) =>
        System.Text.Encoding.UTF8.GetByteCount(cumulative) >= 3072 || sessionAge >= TimeSpan.FromMinutes(10);

    // PAD tokens are valid decoder progress during silence. No-word guesses,
    // unchanged phrases and pitch/volume alone do not prove a transport fault.
    public static bool ShouldRecoverTransport(TimeSpan decoderAge, TimeSpan sendAge,
        TimeSpan sessionAge, bool finalizing) => !finalizing && decoderAge >= TimeSpan.FromSeconds(30) &&
        sendAge >= TimeSpan.Zero && sendAge <= TimeSpan.FromSeconds(3) && sessionAge >= TimeSpan.FromSeconds(30);

    // A responsive decoder can still emit only PAD while missing sustained speech.
    // This independent policy is conservative evidence for renewing recognition,
    // not a gate on capture, sending PCM, or ordinary silent decoder activity.
    public static bool ShouldRecoverRecognition(double voicedSeconds, double voiceAgeSeconds,
        double meaningfulAgeSeconds, double sessionAgeSeconds, double fallbackAgeSeconds,
        int failedServerRecoveries, int consecutiveRecoveries, bool finalizing) =>
        !finalizing && double.IsFinite(voicedSeconds) && double.IsFinite(voiceAgeSeconds) &&
        double.IsFinite(meaningfulAgeSeconds) && double.IsFinite(sessionAgeSeconds) &&
        double.IsFinite(fallbackAgeSeconds) && voicedSeconds >= 2.0 &&
        voiceAgeSeconds is >= 0 and <= 2.5 && meaningfulAgeSeconds >= 12 && sessionAgeSeconds >= 12 &&
        fallbackAgeSeconds >= RecognitionRecoveryCooldownSeconds(consecutiveRecoveries) &&
        (failedServerRecoveries >= 2 || meaningfulAgeSeconds >= 18);

    public static double RecognitionRecoveryCooldownSeconds(int consecutiveRecoveries) =>
        consecutiveRecoveries < 2 ? 20 : consecutiveRecoveries == 2 ? 30 : 40;

    internal static async Task StreamAsync(IncomingContinuousAudioBuffer audio,
        Func<IncomingPcmChunk, CancellationToken, Task> sendAudio,
        Func<CancellationToken, Task> endAudio, Func<bool> shouldRollOver, CancellationToken token,
        TimeSpan? sendTimeout = null)
    {
        TimeSpan timeout = sendTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sendTimeout));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (shouldRollOver())
            {
                audio.SetCapacity(IncomingContinuousAudioBuffer.HandoffCapacityChunks);
                await SendWithDeadlineAsync(endAudio, timeout, token).ConfigureAwait(false);
                return;
            }
            if (audio.TryRead(out IncomingPcmChunk chunk))
                await SendWithDeadlineAsync(sendToken => sendAudio(chunk, sendToken), timeout, token).ConfigureAwait(false);
            else await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    private static async Task SendWithDeadlineAsync(Func<CancellationToken, Task> send,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { await send(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A full server input queue can block SendAsync. A last-successful-send
            // watchdog alone would go stale and leave this task stuck forever.
            throw new TimeoutException("The speech server stopped accepting audio. Reconnecting without stopping capture.");
        }
    }
}

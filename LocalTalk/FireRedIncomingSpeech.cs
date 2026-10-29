namespace IncomingTranslator;

internal sealed record FireRedDecodeJob(long Segment, int Revision, float[] Samples,
    DateTimeOffset Start, DateTimeOffset End, bool Final);

// FireRed is an offline model used incrementally. Capture is never gated by
// speech guesses: quiet audio only decides where a short decode section ends.
internal sealed class FireRedAudioSegmenter
{
    internal const int MaxSamples = 128000; // Eight seconds, hard RAM/inference bound.
    private readonly List<float> _samples = new(MaxSamples);
    private readonly Queue<float> _preRoll = new();
    private DateTimeOffset _start, _end;
    private DateTimeOffset? _lastInputEnd;
    private long _segment;
    private int _revision, _quietSamples, _lastPreviewSamples;
    internal bool SendEarlyPreviews { get; set; }
    internal bool TryFinishAtGap(DateTimeOffset nextStart, out FireRedDecodeJob? job)
    {
        job = null;
        if (_samples.Count == 0)
        {
            if (_lastInputEnd is DateTimeOffset previous && (nextStart - previous).Duration() > TimeSpan.FromMilliseconds(160)) _preRoll.Clear();
            return false;
        }
        if ((nextStart - _end).Duration() <= TimeSpan.FromMilliseconds(160)) return false;
        job = Snapshot(final: true);
        _samples.Clear(); _preRoll.Clear(); _quietSamples = 0;
        return true;
    }
    public FireRedDecodeJob? Observe(IncomingPcmChunk chunk, int pauseMilliseconds)
    {
        if (chunk.Bytes.Length % 2 != 0 || chunk.Bytes.Length > 32000)
            throw new ArgumentException("Expected bounded PCM16 mono audio.");
        float[] pcm = new float[chunk.Bytes.Length / 2];
        _lastInputEnd = chunk.At.AddTicks(pcm.Length * 625L);
        double energy = 0;
        for (int index = 0; index < pcm.Length; index++)
        {
            pcm[index] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(chunk.Bytes.AsSpan(index * 2, 2)) / 32768f;
            energy += pcm[index] * pcm[index];
        }
        bool audible = pcm.Length > 0 && Math.Sqrt(energy / pcm.Length) >= .0005;
        if (_samples.Count == 0 && !audible)
        {
            foreach (float sample in pcm) _preRoll.Enqueue(sample);
            while (_preRoll.Count > 3840) _preRoll.Dequeue();
            return null;
        }
        if (_samples.Count == 0)
        {
            _segment++; _revision = 0; _lastPreviewSamples = 0; _quietSamples = 0;
            _start = chunk.At.AddTicks(-_preRoll.Count * 625L);
            _samples.AddRange(_preRoll); _preRoll.Clear();
        }
        int room = MaxSamples - _samples.Count;
        _samples.AddRange(pcm.Take(room));
        _end = chunk.At.AddTicks(Math.Min(room, pcm.Length) * 625L);
        _quietSamples = audible ? 0 : _quietSamples + pcm.Length;
        bool final = _samples.Count >= MaxSamples || _quietSamples >= Math.Clamp(pauseMilliseconds, 600, 2400) * 16;
        if (final)
        {
            FireRedDecodeJob result = Snapshot(final: true);
            _samples.Clear(); _quietSamples = 0;
            // Preserve every sample crossing the hard section boundary. No
            // overlap is retranscribed, so a phrase cannot be duplicated.
            if (pcm.Length > room)
            {
                _segment++; _revision = 0; _lastPreviewSamples = 0;
                _start = _end;
                _samples.AddRange(pcm.Skip(room));
                _end = chunk.At.AddTicks(pcm.Length * 625L);
            }
            return result;
        }
        if (SendEarlyPreviews && _samples.Count >= 38400 && _samples.Count - _lastPreviewSamples >= 38400)
        {
            _lastPreviewSamples = _samples.Count;
            return Snapshot(final: false);
        }
        return null;
    }
    private FireRedDecodeJob Snapshot(bool final) => new(_segment, ++_revision, _samples.ToArray(), _start, _end, final);
}

// One inference at a time. New drafts replace drafts, not final work. Under
// sustained overload keep a bounded number of newest sections and explicitly
// report any skipped older audio instead of accumulating minutes of delay.
internal sealed class FireRedDecodeQueue
{
    private readonly object _gate = new();
    private readonly Queue<FireRedDecodeJob> _finals = new();
    private FireRedDecodeJob? _preview;
    private long _finalizedThrough;
    private volatile bool _sendEarlyPreviews;
    internal long DroppedSections { get; private set; }
    internal bool SendEarlyPreviews
    {
        get => _sendEarlyPreviews;
        set
        {
            lock (_gate)
            {
                _sendEarlyPreviews = value;
                if (!value) _preview = null;
            }
        }
    }
    internal void Enqueue(FireRedDecodeJob job)
    {
        lock (_gate)
        {
            if (job.Final)
            {
                _finalizedThrough = Math.Max(_finalizedThrough, job.Segment);
                if (_preview?.Segment == job.Segment) _preview = null;
                if (_finals.Count == 3) { _finals.Dequeue(); DroppedSections++; }
                _finals.Enqueue(job);
            }
            else if (SendEarlyPreviews && job.Segment > _finalizedThrough) _preview = job;
        }
    }
    internal bool TryRead(out FireRedDecodeJob? job)
    {
        lock (_gate)
        {
            if (_finals.TryDequeue(out job)) return true;
            job = _preview; _preview = null; return job is not null;
        }
    }
    internal bool IsObsoletePreview(FireRedDecodeJob job)
    {
        if (job.Final) return false;
        lock (_gate) return !SendEarlyPreviews || job.Segment <= _finalizedThrough ||
            _preview is { } newer && newer.Segment == job.Segment && newer.Revision > job.Revision;
    }
}

internal sealed partial class IncomingTranslationService
{
    private async Task RunFireRedSessionAsync(CancellationToken token)
    {
        AppSettings settings = CurrentSettings();
        if (!IncomingRecognitionCatalog.SupportsSource(settings.IncomingSpeechEngine, settings.IncomingSourceLanguage))
            throw new InvalidOperationException("Choose Chinese or English for FireRedASR2 subtitles.");
        await ReportHealthAsync("starting", "Loading subtitle speech model…");
        // Loading is explicitly deferred until Start. Finish any native decode
        // before disposal; cancellation never frees a model still in use.
        using var recognizer = await Task.Run(() => new SherpaSpeechRecognizer(settings.IncomingSpeechEngine, AppPaths.Root), token);
        InitializeNativeSubtitleSession();
        _connected = true;
        await ReportHealthAsync("listening", ListeningStatus(settings));
        var segmenter = new FireRedAudioSegmenter();
        var queue = new FireRedDecodeQueue();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task intake = IntakeAsync(lifetime.Token), decoding = DecodeAsync(lifetime.Token);
        try
        {
            Task first = await Task.WhenAny(intake, decoding);
            await first;
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Subtitle speech recognition ended unexpectedly.");
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(intake, decoding); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            _connected = false;
        }

        async Task IntakeAsync(CancellationToken cancellation)
        {
            long loggedDrops = 0;
            while (!cancellation.IsCancellationRequested)
            {
                CheckCaptureState(DateTimeOffset.UtcNow);
                if (_continuousAudio.TryRead(out IncomingPcmChunk chunk))
                {
                    Interlocked.Add(ref _audioBytesSent, chunk.Bytes.Length);
                    Interlocked.Exchange(ref _lastAudioSendTicks, DateTimeOffset.UtcNow.UtcTicks);
                    AppSettings current = CurrentSettings();
                    segmenter.SendEarlyPreviews = queue.SendEarlyPreviews = current.IncomingSendEarlyPreviews;
                    if (segmenter.TryFinishAtGap(chunk.At, out FireRedDecodeJob? beforeGap)) queue.Enqueue(beforeGap!);
                    if (segmenter.Observe(chunk, current.IncomingSettleMilliseconds) is { } job) queue.Enqueue(job);
                    if (queue.DroppedSections > loggedDrops)
                    {
                        loggedDrops = queue.DroppedSections;
                        Log($"FireRed subtitle recognition backlog: skippedSections={loggedDrops}; capture remains open.");
                        await ReportHealthAsync("backlog", "Subtitles fell behind. Older audio was skipped to catch up.", true,
                            "The selected subtitle speech model could not keep up with incoming sections.");
                    }
                }
                else await Task.Delay(15, cancellation);
            }
        }

        async Task DecodeAsync(CancellationToken cancellation)
        {
            var bubbles = new Dictionary<long, long>();
            while (!cancellation.IsCancellationRequested)
            {
                if (!queue.TryRead(out FireRedDecodeJob? job)) { await Task.Delay(25, cancellation); continue; }
                cancellation.ThrowIfCancellationRequested();
                if (queue.IsObsoletePreview(job!) || !job!.Final && !CurrentSettings().IncomingSendEarlyPreviews) continue;
                string text = await recognizer.DecodeAsync(job!.Samples, settings.IncomingSourceLanguage, cancellation);
                if (queue.IsObsoletePreview(job) || !job.Final && !CurrentSettings().IncomingSendEarlyPreviews) continue;
                await ObserveFireRedTranscriptAsync(job, text, bubbles, cancellation);
            }
        }
    }

    // Keep the actual decoded-result boundary testable without loading a model
    // or audio device. Filtered words still acknowledge the original PCM range.
    private async Task ObserveFireRedTranscriptAsync(FireRedDecodeJob job, string text,
        Dictionary<long, long> bubbles, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _transcriptEvents);
        Interlocked.Exchange(ref _lastDecoderProgressTicks, DateTimeOffset.UtcNow.UtcTicks);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (job.Final && bubbles.Remove(job.Segment))
                await ReportHealthAsync("speech-empty", "Some speech wasn't recognized. Still listening.", true,
                    "A final FireRed speech section returned no words after an earlier draft.");
            return;
        }
        MarkMeaningfulProgress();
        _lastTranscriptAt = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _lastWordTicks, _lastTranscriptAt.UtcTicks);
        _lastMeaningfulAudioEnd = job.End;
        _awaitingRecognitionProgress = false;
        _lastTextAudioEnd = job.End;
        await ReportHealthAsync("listening", ListeningStatus(CurrentSettings()));
        bubbles.TryGetValue(job.Segment, out long bubble);
        // Filter only after acknowledgements, before voice evidence or work.
        if (!await AdmitIncomingSourceAsync(text, bubble, job.End))
        {
            await PublishIncomingSourceAsync(text);
            if (job.Final) bubbles.Remove(job.Segment);
            return;
        }
        if (bubble == 0) bubbles[job.Segment] = bubble = Interlocked.Increment(ref _nextUtteranceId);
        long epoch = Interlocked.Read(ref _speakerRecognitionEpoch);
        SpeakerAttribution speaker = QueueSpeakerIdentification(bubble, text, job.Start, job.End, job.End, epoch);
        TranslationRequest request = CreateTranslationRequest(text, speaker, job.Start, job.End, bubble, epoch) with { IsCommitted = job.Final };
        await PublishIncomingSourceAsync(text, $"{LiveSpeakerLabel(speaker)}: {text}");
        await QueueTranslationAsync(request, cancellation);
        Log($"FireRed subtitle section: id={bubble}; final={job.Final}; audioMs={(job.End - job.Start).TotalMilliseconds:0}; sourceChars={text.Length}.");
        if (job.Final) bubbles.Remove(job.Segment);
        while (bubbles.Count > 16) bubbles.Remove(bubbles.Keys.Min());
    }
}

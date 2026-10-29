using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace IncomingTranslator;

internal sealed partial class IncomingTranslationService
{
    private readonly Channel<IncomingPcmChunk> _groupAudio = Channel.CreateBounded<IncomingPcmChunk>(new BoundedChannelOptions(25)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private readonly Dictionary<string, GroupSentenceState> _groupSentences = new();
    private readonly Dictionary<string, (SpeakerAttribution Speaker, long Epoch, bool Manual)> _groupPeople = new();
    private readonly Dictionary<long, string> _groupBubbleVoices = new();
    private readonly Dictionary<string, long> _groupChunkWatermarks = new();
    private readonly SemaphoreSlim _groupProcessingGate = new(1, 1);
    private readonly Queue<string> _retiredGroupSessions = new();
    private string _groupSessionKey = "";
    private int _groupEpoch;
    private DateTimeOffset _lastGroupAnnotationLogAt = DateTimeOffset.MinValue;

    private async Task RunGroupSpeechSessionAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var socket = new ClientWebSocket();
        _socket = socket;
        AppSettings settings = CurrentSettings();
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        probeCancellation.CancelAfter(TimeSpan.FromSeconds(5));
        using HttpResponseMessage health = await _http.GetAsync(GroupSpeechProtocol.Endpoint(settings.VibeVoiceEndpoint), probeCancellation.Token);
        health.EnsureSuccessStatusCode();
        using JsonDocument readiness = JsonDocument.Parse(await health.Content.ReadAsStringAsync(probeCancellation.Token));
        if (!readiness.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ready")
            throw new InvalidOperationException("Group speech recognition is not ready. Prepare it in Settings.");
        await socket.ConnectAsync(GroupSpeechProtocol.Endpoint(settings.VibeVoiceEndpoint, socket: true), probeCancellation.Token);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        { type = "session.start", protocol = 1, sample_rate = 16000, format = "pcm_s16le", max_tokens = 256 }),
            WebSocketMessageType.Text, true, probeCancellation.Token);
        // Never replay the startup backlog. A group decoder keeps its state across ordinary silence.
        while (_groupAudio.Reader.TryRead(out _)) { }
        _sentAudioTimeline.Reset();
        await _groupProcessingGate.WaitAsync(token);
        try
        {
            lock (_gate)
            {
                _groupSessionKey = ""; _groupEpoch = 0; _groupSentences.Clear();
                _groupChunkWatermarks.Clear(); _retiredGroupSessions.Clear();
            }
            lock (_feedbackGate) _groupPeople.Clear();
        }
        finally { _groupProcessingGate.Release(); }
        _connected = true;
        _sessionStartedAt = DateTimeOffset.UtcNow;
        await ReportHealthAsync("listening", ListeningStatus(settings));
        Task sender = SendGroupAudioAsync(socket, lifetime.Token);
        Task receiver = ReceiveGroupTextAsync(socket, lifetime.Token);
        try
        {
            Task first = await Task.WhenAny(sender, receiver);
            await first;
            if (!token.IsCancellationRequested) throw new SessionRefreshException("The group speech session ended; a new session is required.");
        }
        finally
        {
            lifetime.Cancel(); socket.Abort();
            try { await Task.WhenAll(sender, receiver); } catch (Exception) when (lifetime.IsCancellationRequested) { }
            // Closing/switching must never wait on a full queue after its worker
            // has stopped. A reconnect gets a bounded opportunity to finish text.
            if (!token.IsCancellationRequested)
            {
                using var flushDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                flushDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                try { await FlushGroupSentencesAsync(flushDeadline.Token); }
                catch (OperationCanceledException) when (flushDeadline.IsCancellationRequested)
                { Log("Group reconnection skipped unfinished subtitle drafts after its bounded shutdown window."); }
                catch (ChannelClosedException) { Log("Group text queue already closed; no further subtitles were scheduled."); }
            }
            _connected = false;
        }
    }

    private async Task SendGroupAudioAsync(ClientWebSocket socket, CancellationToken token)
    {
        DateTimeOffset previous = default;
        await foreach (IncomingPcmChunk chunk in _groupAudio.Reader.ReadAllAsync(token))
        {
            CheckCaptureState(DateTimeOffset.UtcNow);
            if (previous != default && chunk.At - previous > TimeSpan.FromMilliseconds(240))
                throw new SessionRefreshException("Group audio capture fell behind. Restarting without mixing audio across the gap.");
            previous = chunk.At;
            _sentAudioTimeline.Append(chunk.At, chunk.Bytes.Length);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync(chunk.Bytes, WebSocketMessageType.Binary, true, deadline.Token);
            Interlocked.Add(ref _audioBytesSent, chunk.Bytes.Length);
        }
    }

    private async Task ReceiveGroupTextAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            string text = await ReceiveGroupEnvelopeAsync(socket, deadline.Token);
            GroupTranscriptChunk? chunk = GroupSpeechProtocol.Parse(text);
            if (chunk is not null) await ObserveGroupChunkAsync(chunk, token);
        }
    }

    private static async Task<string> ReceiveGroupEnvelopeAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new SessionRefreshException("The group speech server closed its connection.");
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 1_000_000)
                throw new InvalidDataException("The group speech server returned an unsupported or oversized message.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return new System.Text.UTF8Encoding(false, true).GetString(message.ToArray());
        }
    }

    private async Task ObserveGroupChunkAsync(GroupTranscriptChunk chunk, CancellationToken token)
    {
        await _groupProcessingGate.WaitAsync(token);
        try { await ObserveGroupChunkCoreAsync(chunk, token); }
        finally { _groupProcessingGate.Release(); }
    }

    private async Task ObserveGroupChunkCoreAsync(GroupTranscriptChunk chunk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        chunk = GroupSpeechProtocol.FilterNonSpeechAnnotations(chunk);
        string sessionKey = $"{chunk.SessionId}:{chunk.Epoch}";
        bool newSession;
        lock (_gate)
        {
            if (_retiredGroupSessions.Contains(sessionKey)) return;
            newSession = _groupSessionKey != sessionKey;
        }
        if (newSession)
        {
            await SettleGroupSentencesCoreAsync(token, final: true);
            lock (_gate)
            {
                if (_groupSessionKey.Length > 0) _retiredGroupSessions.Enqueue(_groupSessionKey);
                while (_retiredGroupSessions.Count > 16) _retiredGroupSessions.Dequeue();
                _groupSentences.Clear(); _groupChunkWatermarks.Clear(); _groupSessionKey = sessionKey; _groupEpoch = chunk.Epoch;
            }
            // Persistent People remain untouched; temporary voice ordinals never cross sessions.
            lock (_feedbackGate) { _groupPeople.Clear(); }
        }
        lock (_gate)
        {
            if (_groupChunkWatermarks.TryGetValue(sessionKey, out long previous) && chunk.ChunkIndex <= previous) return;
            _groupChunkWatermarks[sessionKey] = chunk.ChunkIndex;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (chunk.HasNonSpeechAnnotations && now - _lastGroupAnnotationLogAt >= TimeSpan.FromSeconds(30))
        {
            _lastGroupAnnotationLogAt = now;
            Log($"Groups sound labels omitted: annotations={chunk.NonSpeechAnnotationCount}, spokenSegments={chunk.Segments.Count}. Audio timing retained; no clean voice example from this chunk.");
        }
        long recognitionEpoch = Interlocked.Read(ref _speakerRecognitionEpoch);
        string[] voices = chunk.Segments.Select(item => item.VoiceId).Distinct().ToArray();
        bool audioMapped = _sentAudioTimeline.TryMapExactRange(chunk.AudioStartMilliseconds, chunk.AudioEndMilliseconds, out var audioStart, out var audioEnd);
        bool solo = voices.Length == 1 && voices[0] != GroupSpeechProtocol.UnidentifiedVoiceId && audioMapped && !chunk.HasNonSpeechAnnotations;
        lock (_gate)
            foreach (GroupSentenceState previousState in _groupSentences.Values)
                previousState.DecodedThrough = audioMapped ? audioEnd : null;
        foreach (GroupVoiceText segment in chunk.Segments)
        {
            string key = GroupSpeechProtocol.Key(chunk, segment.VoiceId);
            GroupSentenceState state;
            lock (_gate)
            {
                if (!_groupSentences.TryGetValue(key, out state!))
                    _groupSentences[key] = state = new GroupSentenceState(key);
                state.Text = AppendGroupText(state.Text, segment.Text);
                state.LastTextAt = now;
                state.DecodedThrough = state.LastVoiceDecodedThrough = audioMapped ? audioEnd : null;
                state.Segments.Observe(state.Text, now);
                state.RecognitionEpoch = recognitionEpoch;
                if (solo)
                {
                    state.EvidenceStart = audioStart; state.EvidenceEnd = audioEnd;
                    state.Candidate = _speakerTracker.Identify(audioStart, audioEnd, exactInterval: true) with
                        { Label = GroupSpeechProtocol.VoiceLabel(key), SessionVoiceKey = key };
                    // Original audio, not a transcript or assumed separated waveform.
                    state.Samples = !state.Candidate.IsMixed && state.Candidate.CanLearn
                        ? _speakerTracker.GetMonoAudio16k(audioStart, audioEnd, exactInterval: true) : Array.Empty<float>();
                }
                else
                {
                    state.EvidenceStart = state.EvidenceEnd = now;
                    state.Candidate = new(GroupSpeechProtocol.VoiceLabel(key), 0, true, 0)
                    { SessionVoiceKey = key, CanLearn = false, Diagnostic = chunk.HasNonSpeechAnnotations
                        ? "A chunk containing sound annotations cannot provide a clean voice example."
                        : "A mixed chunk cannot provide one clean example per voice." };
                    state.Samples = Array.Empty<float>();
                }
            }
        }
        if (chunk.Segments.Count > 0)
        {
            Interlocked.Exchange(ref _lastWordTicks, now.UtcTicks);
            Interlocked.Increment(ref _transcriptEvents);
            var visible = chunk.Segments.Where(item => IncomingSourceFilter.EvaluateChinese(item.Text, CurrentSettings().IncomingChineseOnly).Allowed).ToArray();
            string live = string.Join("  ·  ", visible.Select(item => GroupSpeechProtocol.VoiceLabel(GroupSpeechProtocol.Key(chunk, item.VoiceId)) + ": " + item.Text));
            string raw = string.Join(" ", visible.Length > 0 ? visible.Select(item => item.Text) : chunk.Segments.Select(item => item.Text));
            await ReportHealthAsync("listening", ListeningStatus(CurrentSettings()));
            await PublishIncomingSourceAsync(raw, live);
        }
        await SettleGroupSentencesCoreAsync(token);
    }

    private static string AppendGroupText(string previous, string next)
    {
        if (next.Length == 0) return previous;
        if (previous.Length == 0) return next;
        char last = previous[^1], first = next[0];
        bool space = char.IsLetterOrDigit(last) && char.IsLetterOrDigit(first)
            && last < '\u3000' && first < '\u3000';
        return previous + (space ? " " : "") + next;
    }

    private async Task SettleGroupSentencesAsync(CancellationToken token, bool final = false)
    {
        await _groupProcessingGate.WaitAsync(token);
        try { await SettleGroupSentencesCoreAsync(token, final); }
        finally { _groupProcessingGate.Release(); }
    }

    private async Task SettleGroupSentencesCoreAsync(CancellationToken token, bool final = false)
    {
        token.ThrowIfCancellationRequested();
        var ready = new List<GroupReadySubtitle>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AppSettings settings = CurrentSettings();
        lock (_gate)
        {
            foreach (GroupSentenceState state in _groupSentences.Values)
            {
                state.Segments.StabilityMilliseconds = settings.SubtitlePreviewStableMilliseconds;
                state.Segments.SendEarlyPreviews = settings.IncomingSendEarlyPreviews;
                // Chunk cadence is not silence: do not finalize between VibeVoice's 2.9-second packets.
                bool quiet = SubtitleSegmentation.HasAudioPause(now, _speakerTracker.LastVoiceLikeAt,
                    state.DecodedThrough, TimeSpan.FromMilliseconds(settings.IncomingSettleMilliseconds));
                // A later decoded chunk containing other voices (or silence) is
                // evidence that this voice has stopped. Packet delivery time is not.
                bool voiceAbsent = state.DecodedThrough is DateTimeOffset observedThrough
                    && state.LastVoiceDecodedThrough is DateTimeOffset lastVoiceThrough
                    && observedThrough - lastVoiceThrough >= TimeSpan.FromMilliseconds(settings.IncomingSettleMilliseconds);
                while (state.Segments.TryTakeReady(now, final || quiet || voiceAbsent, TimeSpan.FromSeconds(10),
                    out var segment, allowQuietCommit: false))
                {
                    if (!state.Bubbles.TryGetValue(segment.SequenceIndex, out long id))
                        state.Bubbles[segment.SequenceIndex] = id = Interlocked.Increment(ref _nextUtteranceId);
                    // Freeze all provenance with the segment. No later chunk may
                    // attach its audio to an older sentence waiting in a queue.
                    ready.Add(new(state.Key, segment, id, state.Candidate, state.Samples.ToArray(),
                        state.EvidenceStart, state.EvidenceEnd, state.RecognitionEpoch));
                }
                if (state.Text.Length > 16_000 && state.Segments.ActiveText.Length == 0)
                { state.Text = ""; state.Segments.Reset(); state.Bubbles.Clear(); }
            }
        }
        foreach (var item in ready)
        {
            if (!await AdmitIncomingSourceAsync(item.Segment.Text, item.Id, now)) continue;
            SpeakerAttribution speaker = item.Candidate with { SessionVoiceKey = item.Key };
            lock (_feedbackGate)
            {
                _groupBubbleVoices[item.Id] = item.Key;
                speaker = ResolveGroupSpeakerLocked(speaker, item.RecognitionEpoch);
                foreach (long old in _groupBubbleVoices.Keys.Where(id => id < _nextUtteranceId - 250).ToArray()) _groupBubbleVoices.Remove(old);
            }
            SpeakerAttribution observed = QueueGroupSpeakerIdentification(item.Id, item.Segment.Text, item.EvidenceStart,
                item.EvidenceEnd, now, item.RecognitionEpoch, speaker, item.Samples, item.Key);
            var request = CreateTranslationRequest(item.Segment.Text, observed, item.EvidenceStart, now, item.Id,
                item.RecognitionEpoch) with { IsCommitted = item.Segment.IsCommitted };
            await QueueTranslationAsync(request, token);
        }
    }

    private Task FlushGroupSentencesAsync(CancellationToken token) => SettleGroupSentencesAsync(token, final: true);

    private void OnGroupSpeakerIdentified(string voiceKey, SpeakerAttribution speaker, long epoch)
    {
        if (GroupSpeechProtocol.IsUnidentifiedKey(voiceKey) || !RecognitionStatus.Enabled || epoch != Interlocked.Read(ref _speakerRecognitionEpoch)) return;
        lock (_feedbackGate)
        {
            if (speaker.PersonId is null)
            {
                // A newer unmatched/unsafe turn revokes the old automatic guess.
                // Otherwise a rejected or below-threshold result could still be
                // shown under the previous named link indefinitely.
                if (_groupPeople.TryGetValue(voiceKey, out var prior) && !prior.Manual) _groupPeople.Remove(voiceKey);
                return;
            }
            if (_deletedPeople.Contains(speaker.PersonId) || IsGroupSuggestionRejectedLocked(voiceKey, speaker.PersonId)) return;
            if (_groupPeople.TryGetValue(voiceKey, out var previous) && previous.Manual) return;
            // A recognizer mistake can merge/split tracks. Conflicting clean matches revoke a guess.
            if (_groupPeople.TryGetValue(voiceKey, out previous) && previous.Speaker.PersonId != speaker.PersonId)
            { _groupPeople.Remove(voiceKey); return; }
            _groupPeople[voiceKey] = (speaker, epoch, false);
        }
    }

    private void ApplyGroupContextCorrection(string evidenceId, SpeakerFeedbackResult correction)
    {
        if (!correction.Applied) return;
        lock (_feedbackGate)
        {
            if (!_evidenceBubbles.TryGetValue(evidenceId, out long bubble) || !_groupBubbleVoices.TryGetValue(bubble, out string? key) ||
                GroupSpeechProtocol.IsUnidentifiedKey(key)) return;
            _groupRejectedSuggestions.Remove(bubble);
            if (correction.SuggestionConfirmed == false && correction.SuggestedPersonId is string rejected)
                _groupRejectedSuggestions[bubble] = (key, rejected);
            if (correction.PersonId is null && correction.Label != "Music / recording") { _groupPeople.Remove(key); return; }
            _groupPeople[key] = (new SpeakerAttribution(correction.Label, 1, false, 0)
                { PersonId = correction.PersonId, SuggestedPersonId = correction.PersonId, SessionVoiceKey = key, CanLearn = false },
                Interlocked.Read(ref _speakerRecognitionEpoch), correction.PersonId is null && correction.IsManuallyAttributed);
        }
    }

    // Caller already owns _feedbackGate. Explicit user labels are independent of
    // whether the optional fingerprint extractor is currently enabled.
    private SpeakerAttribution ResolveGroupSpeakerLocked(SpeakerAttribution speaker, long? recognitionEpoch = null)
    {
        if (speaker.SessionVoiceKey.Length == 0 || GroupSpeechProtocol.IsUnidentifiedKey(speaker.SessionVoiceKey) ||
            !_groupPeople.TryGetValue(speaker.SessionVoiceKey, out var linked)) return speaker;
        if (linked.Speaker.PersonId is not null && (!RecognitionStatus.Enabled ||
            recognitionEpoch is long candidateEpoch && candidateEpoch != Interlocked.Read(ref _speakerRecognitionEpoch))) return speaker;
        if (!linked.Manual && (!RecognitionStatus.Enabled || linked.Epoch != Interlocked.Read(ref _speakerRecognitionEpoch) ||
            recognitionEpoch is long epoch && epoch != linked.Epoch)) return speaker;
        if (linked.Speaker.PersonId is string personId && _deletedPeople.Contains(personId)) return speaker;
        // A user's confirmation concerns one message, not every future utterance
        // on a track that the group recognizer can accidentally merge.
        return SuppressRejectedGroupSuggestionLocked(speaker with { Label = linked.Speaker.Label,
            PersonId = linked.Speaker.PersonId, SuggestedPersonId = linked.Speaker.PersonId,
            PersonConfirmed = false, Confidence = linked.Speaker.Confidence });
    }

    private bool IsManualGroupAttributionLocked(long bubbleId) => _groupBubbleVoices.TryGetValue(bubbleId, out string? key)
        && _groupPeople.TryGetValue(key, out var linked) && linked.Manual && linked.Speaker.PersonId is null;

    internal static async Task<int> RunGroupRoutingOfflineChecksAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Group speech routing check failed: " + message);
            checks++;
        }
        var sources = new List<string>();
        var displays = new List<TranslationDisplay>();
        using var identifier = new AiSpeakerIdentifier(Path.Combine(directory, "identities.json"), Path.Combine(directory, "people.json"));
        using var service = new IncomingTranslationService(new AppSettings { IncomingSpeechEngine = "vibevoice", UseAiSpeakerRecognition = false },
            (_, _) => Task.CompletedTask, text => { sources.Add(text); return Task.CompletedTask; },
            display => { displays.Add(display); return Task.CompletedTask; }, identifier);
        await service.ApplySpeakerRecognitionAsync(false).ConfigureAwait(false);
        GroupVoiceText[] voices = Enumerable.Range(0, 5).Select(index => new GroupVoiceText(index.ToString(), $"Person {index + 1} said a different sentence.")).ToArray();
        var first = new GroupTranscriptChunk("offline-session-a", 0, 0, 0, 2900, voices);
        await service.ObserveGroupChunkAsync(first, CancellationToken.None).ConfigureAwait(false);
        await service.ObserveGroupChunkAsync(first, CancellationToken.None).ConfigureAwait(false);
        Check(sources.Count == 1, "duplicate chunks do not replay source text or postpone sentence boundaries");
        await service.FlushGroupSentencesAsync(CancellationToken.None).ConfigureAwait(false);
        var requests = new List<TranslationRequest>();
        while (service._translations.Reader.TryRead(out TranslationRequest request)) requests.Add(request);
        Check(requests.Count == 5 && requests.Select(request => request.UtteranceId).Distinct().Count() == 5 &&
            requests.Select(request => request.Speaker.SessionVoiceKey).Distinct().Count() == 5,
            "five temporary voices become five independently routed sentence requests");
        Check(requests.Select(request => request.Source).OrderBy(text => text).SequenceEqual(voices.Select(voice => voice.Text).OrderBy(text => text)),
            "text from different voices is never concatenated into a combined translation");
        Check(requests.All(request => request.IsCommitted && !request.Speaker.CanLearn && request.Speaker.IsMixed),
            "a mixed chunk never becomes a clean voice example for any of its attributed text");
        Check(service._identificationJobs.Reader.Count == 0, "Groups with voice recognition disabled does not schedule fingerprint inference");
        await service.ObserveGroupChunkAsync(first, CancellationToken.None).ConfigureAwait(false);
        Check(service._translations.Reader.Count == 0 && sources.Count == 1, "watermarks persist after finalizing a chunk");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TranslationRequest firstPerson = requests[0];
        service.ApplyContextCorrection(firstPerson.Speaker.EvidenceId,
            new(false, "Manual label", "person-ringo", "Ringo", true));
        SpeakerAttribution labelled;
        lock (service._feedbackGate) labelled = service.ResolveSpeakerLocked(firstPerson.UtteranceId, firstPerson.Speaker, firstPerson.SpeakerRecognitionEpoch);
        Check(labelled.Label == "Ringo" && labelled.PersonId == "person-ringo",
            "a user-assigned message keeps its name even while the optional fingerprint model is disabled");
        await service.PublishTranslationDisplayAsync(new("Voice 1", firstPerson.Source, "Synthetic translated text", firstPerson.UtteranceId, now)
        { SessionVoiceKey = firstPerson.Speaker.SessionVoiceKey, EvidenceId = firstPerson.Speaker.EvidenceId,
            SpeakerRecognitionEpoch = firstPerson.SpeakerRecognitionEpoch }).ConfigureAwait(false);
        Check(displays[^1].Speaker == "Ringo" && displays[^1].IsManuallyAttributed,
            "manual group names are marked authoritative when a subtitle is published");
        lock (service._feedbackGate) labelled = service.ResolveGroupSpeakerLocked(firstPerson.Speaker, firstPerson.SpeakerRecognitionEpoch);
        Check(labelled.PersonId is null && !labelled.PersonConfirmed,
            "a track label never guesses another future utterance while voice recognition is off");
        service._deletedPeople.Add("person-ringo");
        lock (service._feedbackGate) labelled = service.ResolveGroupSpeakerLocked(firstPerson.Speaker, firstPerson.SpeakerRecognitionEpoch);
        Check(labelled.PersonId is null, "an established group mapping cannot resurrect a deleted saved person");

        var second = new GroupTranscriptChunk("offline-session-b", 0, 0, 0, 2900, [new("0", "A new session starts with another person.")]);
        await service.ObserveGroupChunkAsync(second, CancellationToken.None).ConfigureAwait(false);
        await service.FlushGroupSentencesAsync(CancellationToken.None).ConfigureAwait(false);
        Check(service._translations.Reader.TryRead(out TranslationRequest newSession) &&
            newSession.Speaker.SessionVoiceKey != firstPerson.Speaker.SessionVoiceKey && newSession.Speaker.PersonId is null,
            "voice zero in a new session cannot inherit another session's saved-person assignment");
        int sourceCount = sources.Count;
        await service.ObserveGroupChunkAsync(first, CancellationToken.None).ConfigureAwait(false);
        Check(sources.Count == sourceCount && service._groupSessionKey == "offline-session-b:0",
            "late chunks from a retired session cannot reset the current voice tracks");
        service._sentAudioTimeline.Reset();
        service._sentAudioTimeline.Append(DateTimeOffset.UtcNow.AddSeconds(-6), 6000 * 32);
        var continuing = new GroupTranscriptChunk("offline-pauses", 0, 0, 0, 2900, [new("0", "Still talking")]);
        await service.ObserveGroupChunkAsync(continuing, CancellationToken.None).ConfigureAwait(false);
        Check(service._translations.Reader.Count == 0,
            "packet cadence alone does not finalize a short unfinished utterance");
        await service.ObserveGroupChunkAsync(continuing with { ChunkIndex = 1, AudioStartMilliseconds = 2900,
            AudioEndMilliseconds = 5800, Segments = [new("1", "Another person has now started speaking")] }, CancellationToken.None).ConfigureAwait(false);
        Check(service._translations.Reader.TryRead(out TranslationRequest stoppedVoice) && stoppedVoice.Source == "Still talking" && stoppedVoice.IsCommitted,
            "a later decoded interval without a voice finalizes that voice without waiting for global room silence");

        using var finalPolicyIdentifier = new AiSpeakerIdentifier(Path.Combine(directory, "final-policy-identities.json"), Path.Combine(directory, "final-policy-people.json"));
        using var finalPolicyService = new IncomingTranslationService(new AppSettings
            { IncomingSpeechEngine = "vibevoice", UseAiSpeakerRecognition = false },
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, finalPolicyIdentifier);
        var punctuated = new GroupTranscriptChunk("offline-final-policy", 0, 0, 0, 2900,
            [new("0", "This sentence has punctuation. The same person is continuing")]);
        await finalPolicyService.ObserveGroupChunkAsync(punctuated, CancellationToken.None).ConfigureAwait(false);
        GroupSentenceState pendingState = finalPolicyService._groupSentences.Values.Single();
        pendingState.Segments.Reset();
        pendingState.Segments.Observe(pendingState.Text, DateTimeOffset.UtcNow.AddSeconds(-2));
        await finalPolicyService.SettleGroupSentencesAsync(CancellationToken.None).ConfigureAwait(false);
        Check(finalPolicyService._translations.Reader.Count == 0 && !pendingState.Segments.SendEarlyPreviews,
            "default Groups ignores stable punctuation and packet silence when no actual turn-ending evidence exists");
        await finalPolicyService.FlushGroupSentencesAsync(CancellationToken.None).ConfigureAwait(false);
        var finalPolicyRequests = new List<TranslationRequest>();
        while (finalPolicyService._translations.Reader.TryRead(out TranslationRequest request)) finalPolicyRequests.Add(request);
        Check(finalPolicyRequests.Count == 2 && finalPolicyRequests.All(request => request.IsCommitted) &&
            string.Join(" ", finalPolicyRequests.Select(request => request.Source)) == punctuated.Segments.Single().Text &&
            finalPolicyRequests.Select(request => request.UtteranceId).Distinct().Count() == 2,
            "an actual Groups final releases the complete retained text in distinct finished sentence bubbles without loss");
        AppSettings earlySettings = finalPolicyService.CurrentSettings(); earlySettings.IncomingSendEarlyPreviews = true;
        finalPolicyService.UpdateSettings(earlySettings);
        pendingState.Segments.Reset(); pendingState.Bubbles.Clear();
        pendingState.Text = "Another sentence. This thought is still growing";
        pendingState.Segments.Observe(pendingState.Text, DateTimeOffset.UtcNow.AddMilliseconds(-700));
        await finalPolicyService.SettleGroupSentencesAsync(CancellationToken.None).ConfigureAwait(false);
        Check(finalPolicyService._translations.Reader.TryRead(out TranslationRequest earlySentence) && earlySentence.Source == "Another sentence." &&
            pendingState.Segments.SendEarlyPreviews && !finalPolicyService._captureRefreshNeeded,
            "explicit early Groups mode can publish a stable sentence before a real pause, without a capture restart");
        Check(finalPolicyIdentifier.ExtractorLoadAttempts == 0 && !finalPolicyIdentifier.IsModelLoaded,
            "group final-policy fixtures never run inference or open an audio source");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool canceledFlush = false;
        try { await service.FlushGroupSentencesAsync(canceled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { canceledFlush = true; }
        Check(canceledFlush, "a canceled shutdown flush exits without waiting on text backpressure");
        long previousEpoch = Interlocked.Read(ref service._speakerRecognitionEpoch);
        await service.ResetSpeakersAsync(CancellationToken.None).ConfigureAwait(false);
        Check(Interlocked.Read(ref service._speakerRecognitionEpoch) > previousEpoch && service._groupPeople.Count == 0 &&
            service._automaticSpeakerResults.Count == 0 && service._latestIdentityJobs.Count == 0,
            "explicit reset invalidates in-flight matching and clears temporary group identity state");
        Check(identifier.ExtractorLoadAttempts == 0 && !identifier.IsModelLoaded,
            "routing tests never loaded a fingerprint model or opened an audio device");

        using var captureIdentifier = new AiSpeakerIdentifier(Path.Combine(directory, "snapshot-identities.json"), Path.Combine(directory, "snapshot-people.json"));
        using var captureService = new IncomingTranslationService(new AppSettings(), (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask, _ => Task.CompletedTask, captureIdentifier);
        float[] original = [0.1f, 0.2f, 0.3f];
        var clean = new SpeakerAttribution("Voice 1", 0, false, 0)
        { CanLearn = true, EvidenceStart = now.AddSeconds(-2), EvidenceEnd = now, SessionVoiceKey = "vibevoice:audio:0:0" };
        captureService.QueueGroupSpeakerIdentification(1, "clean sample", clean.EvidenceStart, clean.EvidenceEnd,
            now, 0, clean, original, clean.SessionVoiceKey);
        original[0] = 9;
        Check(captureService._identificationJobs.Reader.TryRead(out SpeakerIdentificationJob? cleanJob) &&
            cleanJob.Samples[0] == .1f && cleanJob.Fallback.EvidenceStart == clean.EvidenceStart && cleanJob.Fallback.EvidenceEnd == clean.EvidenceEnd,
            "clean audio and exact interval provenance are frozen before asynchronous identification");
        captureService.QueueGroupSpeakerIdentification(2, "mixed sample", clean.EvidenceStart, clean.EvidenceEnd,
            now, 0, clean with { IsMixed = true }, original, clean.SessionVoiceKey);
        Check(captureService._identificationJobs.Reader.TryRead(out SpeakerIdentificationJob? mixedJob) && mixedJob.Samples.Length == 0,
            "a mixed candidate cannot attach a supplied waveform as a trainable isolated sample");
        string unknownKey = "vibevoice:audio:0:" + GroupSpeechProtocol.UnidentifiedVoiceId;
        SpeakerAttribution unknown = captureService.QueueGroupSpeakerIdentification(3, "unlabelled sample", clean.EvidenceStart, clean.EvidenceEnd,
            now, 0, clean, original, unknownKey);
        Check(captureService._identificationJobs.Reader.Count == 0 && unknown.PersonId is null && unknown.IsMixed && !unknown.CanLearn,
            "unlabelled text cannot schedule fingerprint inference even when the supplied waveform looks clean");
        captureService._groupBubbleVoices[3] = unknownKey;
        captureService.ApplyGroupContextCorrection(unknown.EvidenceId, new(false, "Manual bubble only", "person-ringo", "Ringo", true));
        Check(!captureService._groupPeople.ContainsKey(unknownKey),
            "manually labelling an unknown bubble cannot assign every future unknown voice to that person");
        Check(captureIdentifier.ExtractorLoadAttempts == 0, "queued sample fixtures did not execute model inference");
        var tracker = new SpeakerTracker();
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(16000, 2);
        byte[] syntheticPcm = new byte[16000 * 2 * sizeof(float)];
        for (int sample = 0; sample < 16000; sample++)
        {
            float value = (float)(.2 * Math.Sin(2 * Math.PI * 220 * sample / 16000));
            BitConverter.TryWriteBytes(syntheticPcm.AsSpan(sample * 8, 4), value);
            BitConverter.TryWriteBytes(syntheticPcm.AsSpan(sample * 8 + 4, 4), value);
        }
        tracker.Observe(syntheticPcm, syntheticPcm.Length, format);
        Check(tracker.LastVoiceLikeAt != DateTimeOffset.MinValue, "the synthetic PCM fixture contains voiced frames without opening a device");
        DateTimeOffset clippedEnd = tracker.LastVoiceLikeAt.AddMilliseconds(-17), clippedStart = tracker.LastVoiceLikeAt.AddMilliseconds(-713);
        SpeakerAttribution clipped = tracker.Identify(clippedStart, clippedEnd, exactInterval: true);
        Check(clipped.EvidenceStart == clippedStart && clipped.EvidenceEnd == clippedEnd && Math.Abs(clipped.SpeechSeconds - .696) < .001 &&
            tracker.GetMonoAudio16k(clippedStart, clippedEnd, exactInterval: true).Length == 11136,
            "exact-interval identification clips partial frames to the same interval represented by its PCM samples");
        Console.WriteLine($"PASS: {checks} Groups routing checks; synthetic transcripts and local fixture metadata only.");
        return checks;
    }

    private sealed record GroupReadySubtitle(string Key, SubtitleSegment Segment, long Id, SpeakerAttribution Candidate,
        float[] Samples, DateTimeOffset EvidenceStart, DateTimeOffset EvidenceEnd, long RecognitionEpoch);

    private sealed class GroupSentenceState(string key)
    {
        public string Key { get; } = key;
        public string Text = "";
        public SubtitleSegmentation Segments { get; } = new();
        public Dictionary<int, long> Bubbles { get; } = new();
        public DateTimeOffset LastTextAt, EvidenceStart, EvidenceEnd;
        public DateTimeOffset? DecodedThrough, LastVoiceDecodedThrough;
        public SpeakerAttribution Candidate = new(GroupSpeechProtocol.VoiceLabel(key), 0, true, 0)
            { SessionVoiceKey = key, CanLearn = false };
        public float[] Samples = Array.Empty<float>();
        public long RecognitionEpoch;
    }
}

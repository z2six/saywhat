using System.Text.Json;

namespace IncomingTranslator;

internal static class RecognitionTextFilterTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Recognition control text: " + detail); checks++; }
        foreach (string text in new[] { "SIL", " SIL. ", "[SIL]", "[sil]", "<sil>", "<SIL>", "[Silence]",
            "[BLANK_AUDIO]", "[NO_SPEECH]", "<blank>", "<unk>", "<pad>", "<sos>", "<eos>", "<sos/eos>",
            "<s></s>", "<eps>", "<|nospeech|>", "<sil><sil>", "SIL [Silence] <pad>…", "\ufeff[SIL]。\u200b" })
        {
            Check(RecognitionTextFilter.IsControlOnly(text) && RecognitionTextFilter.CleanControlOnly(text) == "",
                "standalone marker-only recognition is suppressed: " + text);
            Check(SherpaSpeechRecognizer.CleanTranscript(text) == "", "FireRed's actual decode-output cleanup uses the common filter");
        }
        foreach (string text in new[] { "sil", "Sil", "silence", "Silence!", "SIL International", "My friend Sil is here.",
            "silly", "SILENT", "Do not say SIL.", "你好 SIL", "SIL 안녕하세요", "<sil> real words", "hello [SIL] there",
            "[Silence] Wait a second.", "\"SIL\"", "[name]", "<html>", "[SILVER]", "SIL/SIL", "SILence" })
            Check(!RecognitionTextFilter.IsControlOnly(text) && RecognitionTextFilter.CleanControlOnly(text) == text,
                "ordinary words, mixed speech and unknown markup are preserved exactly: " + text);
        Check(!RecognitionTextFilter.IsControlOnly(null) && !RecognitionTextFilter.IsControlOnly("") &&
            !RecognitionTextFilter.IsControlOnly(" ... "), "empty/punctuation-only text is not mislabeled as a known control");
        Check(RecognitionTextFilter.ControlTokenCount("SIL [Silence] <pad>") == 3 &&
            RecognitionTextFilter.CleanControlOnly(RecognitionTextFilter.CleanControlOnly("<sil>")) == "",
            "counting is deterministic and cleanup is idempotent");
        Check(RecognitionTextFilter.ControlTokenCount(string.Concat(Enumerable.Repeat("<sil>", 10000))) == 10000,
            "long marker streams use a bounded linear scan, without regex backtracking");

        var transcript = new NativeSubtitleTranscript();
        JsonElement Event(string type, string property, string text)
        { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = type, [property] = text })); return doc.RootElement.Clone(); }
        NativeSubtitleUpdate delta = transcript.Observe(Event("conversation.item.input_audio_transcription.delta", "delta", "<sil>"), 100, false);
        NativeSubtitleUpdate final = transcript.Observe(Event("conversation.item.input_audio_transcription.completed", "transcript", "SIL"), 200, true);
        using var ack = JsonDocument.Parse("""{"type":"input_audio_buffer.committed"}""");
        NativeSubtitleUpdate done = transcript.Observe(ack.RootElement, 200, true);
        Check(delta.Text is null && final.Text is null && done.Text is null && done.Completed && done.AudioEndMilliseconds == 200,
            "incoming Nemotron suppresses silence while retaining audio progress and verified completion");
        transcript.Observe(Event("conversation.item.input_audio_transcription.completed", "transcript", "Hello."), 300, false);
        delta = transcript.Observe(Event("conversation.item.input_audio_transcription.delta", "delta", "SIL"), 400, false);
        final = transcript.Observe(Event("conversation.item.input_audio_transcription.completed", "transcript", "<sil>"), 500, false);
        Check(delta.Text == "Hello." && final.Text == "Hello.", "a silence update cannot append marker text to prior real speech");
        final = transcript.Observe(Event("conversation.item.input_audio_transcription.completed", "transcript", " SIL International"), 600, false);
        Check(final.Text == "Hello. SIL International", "a genuinely mixed transcript retains the exact SIL spelling");
        var chunk = new GroupTranscriptChunk("synthetic-controls", 2, 4, 1000, 2000,
            [new("0", "SIL"), new("1", "<sil><pad>"), new("2", "[Silence]")]);
        var cleaned = GroupSpeechProtocol.FilterNonSpeechAnnotations(chunk);
        Check(cleaned.Segments.Count == 0 && cleaned.NonSpeechAnnotationCount == 4 &&
            cleaned.AudioStartMilliseconds == chunk.AudioStartMilliseconds && cleaned.AudioEndMilliseconds == chunk.AudioEndMilliseconds && cleaned.ChunkIndex == chunk.ChunkIndex,
            "Groups omits controls without losing chunk timing or overlap/learning safety metadata");
        var mixed = GroupSpeechProtocol.FilterNonSpeechAnnotations(chunk with { Segments = [new("0", "SIL International"), new("1", "<sil> Wait.")] });
        Check(mixed.Segments.Select(segment => segment.Text).SequenceEqual(new[] { "SIL International", "<sil> Wait." }) && mixed.NonSpeechAnnotationCount == 0,
            "Groups does not strip a control-looking substring from real mixed speech");
        return checks;
    }
}

internal sealed partial class IncomingTranslationService
{
    internal static async Task<int> RunRecognitionControlOfflineChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory); int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Incoming recognition control routing: " + detail); checks++; }
        var sources = new List<string>(); var statuses = new List<string>(); var output = new List<TranslationDisplay>();
        using var identifier = new AiSpeakerIdentifier(Path.Combine(directory, "identities.json"), Path.Combine(directory, "people.json"));
        using var service = new IncomingTranslationService(new AppSettings { UseAiSpeakerRecognition = false },
            (text, _) => { statuses.Add(text); return Task.CompletedTask; },
            text => { sources.Add(text); return Task.CompletedTask; },
            display => { output.Add(display); return Task.CompletedTask; }, identifier);
        foreach (string marker in new[] { "SIL", "[SIL]", "<sil>", "[Silence]", "<blank><pad>" })
            await service.ObserveTranscriptAsync(marker);
        await service.FlushActiveAsync();
        Check(service._latest.Length == 0 && sources.Count == 0 && output.Count == 0 && service._translations.Reader.Count == 0,
            "the shared Voxtral/NeMo observation boundary produces no source preview or translation request for control-only text");
        Check(service._nextUtteranceId == 0 && service._identificationJobs.Reader.Count == 0 && identifier.ExtractorLoadAttempts == 0,
            "controls do not create a bubble, voice-learning evidence or inference job");
        DateTimeOffset at = DateTimeOffset.UtcNow;
        SpeakerAttribution person = service.QueueSpeakerIdentification(10, "SIL", at, at, at, 0);
        TranslationRequest request = service.CreateTranslationRequest("SIL", person, at, at, 10);
        await service.TranslateAndPublishAsync(request, new CancellationToken(canceled: true));
        Check(statuses.Count == 0 && output.Count == 0,
            "a defensive publication guard rejects a directly supplied control request before translation work; the token is pre-cancelled so no network could be used");
        await service.ObserveTranscriptAsync("Hello there.");
        long wordTicks = Interlocked.Read(ref service._lastWordTicks);
        await service.ObserveTranscriptAsync("<sil>");
        await service.FlushActiveAsync();
        Check(service._translations.Reader.TryRead(out TranslationRequest real) && real.Source == "Hello there." &&
            service._translations.Reader.Count == 0 && Interlocked.Read(ref service._lastWordTicks) == wordTicks,
            "later silence leaves a real sentence intact, then finalizes it once without inventing new-word progress");

        // Synthetic RAM audio supplies a genuine last-voiced-frame boundary.
        // The optional clock exercises its later pause without sleeping, opening
        // an audio device, or modifying the tracker's private timestamps.
        byte[] pcm = new byte[16000 * 2 * sizeof(float)];
        for (int sample = 0; sample < 16000; sample++)
        {
            float value = (float)(.2 * Math.Sin(2 * Math.PI * 220 * sample / 16000));
            BitConverter.TryWriteBytes(pcm.AsSpan(sample * 8, 4), value);
            BitConverter.TryWriteBytes(pcm.AsSpan(sample * 8 + 4, 4), value);
        }
        service._speakerTracker.Observe(pcm, pcm.Length, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(16000, 2));
        DateTimeOffset lastVoice = service._speakerTracker.LastVoiceLikeAt;
        Check(lastVoice != DateTimeOffset.MinValue, "the fixture contains real voice-like frames without capture");
        var completedIds = new List<long>();
        const string phrase = "This real sentence is complete";
        foreach (string acknowledgement in new[] { "control", "empty-native", "duplicate-native" })
        {
            service.InitializeNativeSubtitleSession();
            Check(service._lastDecodedAudioEnd is null, "a new recognizer session clears its processed-audio watermark");
            service._audioTimelineStart = lastVoice.AddSeconds(-1);
            service._sentAudioTimeline.Append(service._audioTimelineStart, 1000 * 32);
            await service.ObserveNativeSubtitleUpdateAsync(new(phrase, 800, false, 800));
            DateTimeOffset realTextEnd = lastVoice.AddMilliseconds(-200);
            DateTimeOffset afterPause = DateTimeOffset.UtcNow.AddSeconds(2);
            Check(service._lastDecodedAudioEnd == realTextEnd &&
                await service.TakeSubtitleSegmentAsync(false, CancellationToken.None, afterPause) is null,
                "a quiet wall clock cannot finalize text while the decoder has not reached the latest voice frame");
            long meaningfulTicks = Interlocked.Read(ref service._lastMeaningfulTicks);
            long latestWordTicks = Interlocked.Read(ref service._lastWordTicks);
            int sourceCount = sources.Count;
            long transcriptCount = Interlocked.Read(ref service._transcriptEvents);
            if (acknowledgement == "control") await service.ObserveTranscriptAsync("SIL", lastVoice);
            else await service.ObserveNativeSubtitleUpdateAsync(new(acknowledgement == "empty-native" ? null : phrase, 1000, false, 1000));
            Check(service._lastDecodedAudioEnd == lastVoice && service._lastTextAudioEnd == realTextEnd &&
                service._lastMeaningfulAudioEnd == realTextEnd && service._latest == phrase && sources.Count == sourceCount &&
                Interlocked.Read(ref service._lastMeaningfulTicks) == meaningfulTicks && Interlocked.Read(ref service._lastWordTicks) == latestWordTicks,
                "silent, empty and duplicate acknowledgements advance processed audio without changing real words or their evidence: " + acknowledgement);
            if (acknowledgement == "empty-native")
                Check(service._transcriptEvents == transcriptCount, "an empty native processing acknowledgement is not counted as a transcript event");
            service.ObserveDecodedAudio(lastVoice.AddMilliseconds(-300));
            Check(service._lastDecodedAudioEnd == lastVoice, "out-of-order acknowledgements cannot move decoded audio backwards");
            TranslationRequest? completed = await service.TakeSubtitleSegmentAsync(false, CancellationToken.None, afterPause);
            Check(completed is { IsCommitted: true, Source: phrase },
                "a decoded real pause releases the retained phrase rather than stalling behind silence: " + acknowledgement);
            completedIds.Add(completed!.Value.UtteranceId);
            await service.QueueTranslationAsync(completed.Value, CancellationToken.None);
            Check(service._translations.Reader.TryRead(out TranslationRequest queued) && queued == completed.Value &&
                service._translations.Reader.Count == 0 && await service.TakeSubtitleSegmentAsync(false, CancellationToken.None, afterPause) is null,
                "each acknowledged phrase enters the final translation queue exactly once without a SIL request");
        }
        service.ResetTranscriptState();
        Check(service._lastDecodedAudioEnd is null && completedIds.Distinct().Count() == completedIds.Count &&
            identifier.ExtractorLoadAttempts == 0 && output.Count == 0,
            "session reset removes stale acknowledgements, keeps sentence identifiers unique and never loads a model or publishes a synthetic translation");

        service.InitializeNativeSubtitleSession();
        service._audioTimelineStart = lastVoice.AddSeconds(-1);
        service._sentAudioTimeline.Append(service._audioTimelineStart, 6000 * 32);
        var verifiedTranscript = new NativeSubtitleTranscript();
        async Task<NativeSubtitleUpdate> NativeMessage(string json)
        {
            using var parsed = JsonDocument.Parse(json);
            NativeSubtitleUpdate update = verifiedTranscript.Observe(parsed.RootElement, 6000, false);
            await service.ObserveNativeSubtitleUpdateAsync(update);
            return update;
        }
        await NativeMessage("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"transcript\":" +
            JsonSerializer.Serialize(phrase) + ",\"audio_processed\":0.8}");
        DateTimeOffset verifiedPause = DateTimeOffset.UtcNow.AddSeconds(2);
        long verifiedWords = Interlocked.Read(ref service._lastWordTicks);
        foreach (string metadata in new[] { "", ",\"audio_processed\":null", ",\"audio_processed\":\"1.0\"",
            ",\"audio_processed\":-1", ",\"audio_processed\":1e309", ",\"audio_processed\":99" })
        {
            foreach (string text in new[] { "", "<sil>" })
            {
                NativeSubtitleUpdate unverified = await NativeMessage("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"delta\":" +
                    JsonSerializer.Serialize(text) + metadata + "}");
                Check(unverified.VerifiedAudioEndMilliseconds is null && service._lastDecodedAudioEnd == lastVoice.AddMilliseconds(-200) &&
                    await service.TakeSubtitleSegmentAsync(false, CancellationToken.None, verifiedPause) is null &&
                    service._latest == phrase && Interlocked.Read(ref service._lastWordTicks) == verifiedWords,
                    "missing/invalid processing metadata cannot promote sent audio, invent words or finish the pending phrase");
            }
        }
        await NativeMessage("""{"type":"conversation.item.input_audio_transcription.delta","delta":"","audio_processed":0.4}""");
        Check(service._lastDecodedAudioEnd == lastVoice.AddMilliseconds(-200),
            "an out-of-order valid acknowledgement cannot move the service's verified cursor backwards");
        NativeSubtitleUpdate resumed = await NativeMessage("""{"type":"conversation.item.input_audio_transcription.delta","delta":"","audio_processed":1.0}""");
        TranslationRequest? verifiedFinal = await service.TakeSubtitleSegmentAsync(false, CancellationToken.None, verifiedPause);
        Check(resumed.AudioEndMilliseconds == 6000 && resumed.VerifiedAudioEndMilliseconds == 1000 &&
            service._lastDecodedAudioEnd == lastVoice && verifiedFinal is { IsCommitted: true, Source: phrase } &&
            await service.TakeSubtitleSegmentAsync(false, CancellationToken.None, verifiedPause) is null,
            "valid server progress after a sent fallback finishes the phrase once using the verified cursor, never the inflated legacy position");

        using var hushedIdentifier = new AiSpeakerIdentifier(Path.Combine(directory, "hushed-identities.json"), Path.Combine(directory, "hushed-people.json"));
        using var hushed = new IncomingTranslationService(new AppSettings { UseAiSpeakerRecognition = false },
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, hushedIdentifier);
        foreach (string quietPhrase in new[] { "Real words from a quiet voice", "好" })
        {
            hushed.InitializeNativeSubtitleSession();
            DateTimeOffset audioStart = DateTimeOffset.UtcNow.AddSeconds(-5);
            hushed._audioTimelineStart = audioStart;
            hushed._sentAudioTimeline.Append(audioStart, 3000 * 32);
            await hushed.ObserveNativeSubtitleUpdateAsync(new(quietPhrase, 1000, false, 1000));
            DateTimeOffset afterPause = DateTimeOffset.UtcNow.AddSeconds(2);
            Check(hushed._speakerTracker.LastVoiceLikeAt == DateTimeOffset.MinValue && hushed._speakerTracker.LastAudibleAt == DateTimeOffset.MinValue &&
                await hushed.TakeSubtitleSegmentAsync(false, CancellationToken.None, afterPause) is null,
                "a real quiet ASR phrase does not finish just because no tracker frame qualified as a voice");
            long hushWords = Interlocked.Read(ref hushed._lastWordTicks);
            await hushed.ObserveNativeSubtitleUpdateAsync(new(null, 2199, false, 2199));
            Check(await hushed.TakeSubtitleSegmentAsync(false, CancellationToken.None, afterPause) is null,
                "hushed speech waits for the complete decoded quiet span, not a shorter processing update");
            await hushed.ObserveNativeSubtitleUpdateAsync(new(null, 2200, false, 2200));
            TranslationRequest? quietFinal = await hushed.TakeSubtitleSegmentAsync(false, CancellationToken.None, afterPause);
            Check(quietFinal is { IsCommitted: true } && quietFinal.Value.Source == quietPhrase &&
                await hushed.TakeSubtitleSegmentAsync(false, CancellationToken.None, afterPause) is null &&
                Interlocked.Read(ref hushed._lastWordTicks) == hushWords,
                "decoded quiet finishes a quiet phrase, including one Chinese character, exactly once without new-word progress");
        }
        Check(hushedIdentifier.ExtractorLoadAttempts == 0 && !hushedIdentifier.IsModelLoaded,
            "quiet-speech fallback fixtures never load a speech or person model");
        var unpitched = new SpeakerTracker();
        byte[] audiblePcm = new byte[16000 * 2 * sizeof(float)];
        for (int sample = 0; sample < 16000; sample++)
        {
            BitConverter.TryWriteBytes(audiblePcm.AsSpan(sample * 8, 4), .002f);
            BitConverter.TryWriteBytes(audiblePcm.AsSpan(sample * 8 + 4, 4), .002f);
        }
        unpitched.Observe(audiblePcm, audiblePcm.Length, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(16000, 2));
        DateTimeOffset audibleThrough = unpitched.LastAudibleAt;
        Check(unpitched.LastVoiceLikeAt == DateTimeOffset.MinValue && audibleThrough != DateTimeOffset.MinValue,
            "a synthetic unpitched signal can be audible while failing the conservative person tracker's voice heuristic");
        Check(!SubtitleSegmentation.HasAudioPause(audibleThrough.AddMilliseconds(100), audibleThrough.AddSeconds(-2),
            audibleThrough.AddMilliseconds(100), TimeSpan.FromMilliseconds(1200),
            lastWordAudioEnd: audibleThrough.AddMilliseconds(-500), lastAudibleAt: audibleThrough) &&
            SubtitleSegmentation.HasAudioPause(audibleThrough.AddSeconds(2), DateTimeOffset.MinValue,
                audibleThrough.AddMilliseconds(1200), TimeSpan.FromMilliseconds(1200),
                lastWordAudioEnd: audibleThrough.AddMilliseconds(-500), lastAudibleAt: audibleThrough),
            "continuing audible unpitched activity blocks a finish, then a complete decoded quiet span releases it");
        return checks;
    }
}

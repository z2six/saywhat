namespace IncomingTranslator;

internal sealed partial class IncomingTranslationService
{
    internal static async Task<int> RunGroupSoundLabelOfflineChecksAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Groups sound-label routing check failed: " + message);
            checks++;
        }

        var sources = new List<string>();
        using var identifier = new AiSpeakerIdentifier(Path.Combine(directory, "identities.json"), Path.Combine(directory, "people.json"));
        using var service = new IncomingTranslationService(new AppSettings
            { IncomingSpeechEngine = "vibevoice", IncomingSettleMilliseconds = 1200, UseAiSpeakerRecognition = true },
            (_, _) => Task.CompletedTask, text => { sources.Add(text); return Task.CompletedTask; },
            _ => Task.CompletedTask, identifier);
        DateTimeOffset audioStart = DateTimeOffset.UtcNow.AddSeconds(-6);
        service._sentAudioTimeline.Append(audioStart, 6000 * 32);
        long originalWordTicks = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks;
        Interlocked.Exchange(ref service._lastWordTicks, originalWordTicks);
        var soundOnly = new GroupTranscriptChunk("sound-routing", 0, 0, 0, 1000,
            [new("0", "[Music][Music]"), new("1", "[Laughter]")]);
        // Use the internal entry point directly: callers other than JSON parsing
        // must receive the same filter rather than leak raw labels into a bubble.
        await service.ObserveGroupChunkCoreAsync(soundOnly, CancellationToken.None).ConfigureAwait(false);
        Check(sources.Count == 0 && service._groupSentences.Count == 0 && service._translations.Reader.Count == 0,
            "sound-only direct chunks create no source text, sentence state or translation request");
        Check(service._identificationJobs.Reader.Count == 0 && service._latestIdentityJobs.Count == 0 &&
            service._identityJobSequence == 0 && service._nextUtteranceId == 0,
            "sound-only chunks create no person evidence, inference job or subtitle identifier");
        Check(Interlocked.Read(ref service._lastWordTicks) == originalWordTicks && service._transcriptEvents == 0,
            "non-speech annotations are not counted as newly recognized useful words");
        Check(service._groupChunkWatermarks.TryGetValue("sound-routing:0", out long watermark) && watermark == 0,
            "an empty filtered result still advances the session watermark");

        var speech = soundOnly with { ChunkIndex = 1, AudioStartMilliseconds = 1000, AudioEndMilliseconds = 2000,
            Segments = [new("0", "This unfinished sentence can still be translated")] };
        await service.ObserveGroupChunkCoreAsync(speech, CancellationToken.None).ConfigureAwait(false);
        Check(sources.Count == 1 && sources[0].Contains(speech.Segments[0].Text, StringComparison.Ordinal) &&
            service._transcriptEvents == 1 && Interlocked.Read(ref service._lastWordTicks) != originalWordTicks,
            "ordinary speech immediately following annotations still reaches the normal recognition path");
        Check(service._translations.Reader.Count == 0,
            "the first short speech packet is not mistaken for a finished sentence");
        long speechWordTicks = Interlocked.Read(ref service._lastWordTicks);
        var laterSounds = soundOnly with { ChunkIndex = 2, AudioStartMilliseconds = 2000, AudioEndMilliseconds = 4900 };
        await service.ObserveGroupChunkCoreAsync(laterSounds, CancellationToken.None).ConfigureAwait(false);
        GroupSentenceState state = service._groupSentences.Values.Single();
        Check(state.DecodedThrough == audioStart.AddMilliseconds(4900) && state.LastVoiceDecodedThrough == audioStart.AddMilliseconds(2000),
            "annotation-only input advances decoded audio while preserving the last real voice boundary");
        Check(service._translations.Reader.TryRead(out TranslationRequest finished) && finished.IsCommitted &&
            finished.Source == speech.Segments[0].Text && service._translations.Reader.Count == 0,
            "a timed annotation-only chunk finalizes prior speech once without translating the annotations");
        Check(sources.Count == 1 && service._transcriptEvents == 1 && Interlocked.Read(ref service._lastWordTicks) == speechWordTicks,
            "later annotations do not postpone useful-word stall diagnostics or overwrite the source preview");
        int identityJobCount = service._identificationJobs.Reader.Count;
        await service.ObserveGroupChunkCoreAsync(laterSounds, CancellationToken.None).ConfigureAwait(false);
        Check(service._translations.Reader.Count == 0 && service._identificationJobs.Reader.Count == identityJobCount &&
            service._groupChunkWatermarks["sound-routing:0"] == 2,
            "duplicate empty chunks neither replay a sentence nor manufacture identity work");
        Check(identifier.ExtractorLoadAttempts == 0 && !identifier.IsModelLoaded,
            "routing annotation fixtures never load a voice model or open an audio device");

        using var mixedIdentifier = new AiSpeakerIdentifier(Path.Combine(directory, "mixed-identities.json"), Path.Combine(directory, "mixed-people.json"));
        var mixedSources = new List<string>();
        using var mixedService = new IncomingTranslationService(new AppSettings
            { IncomingSpeechEngine = "vibevoice", UseAiSpeakerRecognition = true },
            (_, _) => Task.CompletedTask, text => { mixedSources.Add(text); return Task.CompletedTask; },
            _ => Task.CompletedTask, mixedIdentifier);
        // Pure RAM synthetic PCM ensures exact audio mapping would otherwise be
        // eligible as a clean solo sample. No model or physical device is used.
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(16000, 2);
        byte[] syntheticPcm = new byte[16000 * 2 * sizeof(float)];
        for (int sample = 0; sample < 16000; sample++)
        {
            float value = (float)(.2 * Math.Sin(2 * Math.PI * 220 * sample / 16000));
            BitConverter.TryWriteBytes(syntheticPcm.AsSpan(sample * 8, 4), value);
            BitConverter.TryWriteBytes(syntheticPcm.AsSpan(sample * 8 + 4, 4), value);
        }
        mixedService._speakerTracker.Observe(syntheticPcm, syntheticPcm.Length, format);
        DateTimeOffset cleanEnd = mixedService._speakerTracker.LastVoiceLikeAt;
        DateTimeOffset cleanStart = cleanEnd.AddSeconds(-1);
        SpeakerAttribution baseline = mixedService._speakerTracker.Identify(cleanStart, cleanEnd, exactInterval: true);
        Check(baseline.CanLearn && !baseline.IsMixed && mixedService._speakerTracker.GetMonoAudio16k(cleanStart, cleanEnd, exactInterval: true).Length > 0,
            "the synthetic baseline is independently eligible for clean solo evidence");
        mixedService._sentAudioTimeline.Append(cleanStart, 1000 * 32);
        var mixed = new GroupTranscriptChunk("mixed-sounds", 0, 0, 0, 1000,
            [new("0", "[Laughter] Keep the real spoken words."), new("1", "[Music]")]);
        await mixedService.ObserveGroupChunkCoreAsync(mixed, CancellationToken.None).ConfigureAwait(false);
        GroupSentenceState mixedState = mixedService._groupSentences.Values.Single();
        Check(mixedSources.Count == 1 && mixedState.Text == "Keep the real spoken words." &&
            !mixedSources[0].Contains('['),
            "mixed sound labels disappear from source previews while actual speech remains");
        Check(mixedState.Candidate.IsMixed && !mixedState.Candidate.CanLearn && mixedState.Samples.Length == 0,
            "removing a second voice's music label cannot convert the original waveform into a clean solo example");
        await mixedService.FlushGroupSentencesAsync(CancellationToken.None).ConfigureAwait(false);
        Check(mixedService._translations.Reader.TryRead(out TranslationRequest mixedRequest) &&
            mixedRequest.Source == "Keep the real spoken words." && mixedRequest.Speaker.IsMixed && !mixedRequest.Speaker.CanLearn,
            "cleaned mixed text is translated, but cannot be used as a trainable person example");
        Check(mixedService._identificationJobs.Reader.TryRead(out SpeakerIdentificationJob? mixedJob) &&
            mixedJob.Samples.Length == 0 && mixedJob.Fallback.IsMixed && !mixedJob.Fallback.CanLearn,
            "asynchronous matching receives no falsely isolated waveform from an annotated chunk");
        Check(mixedIdentifier.ExtractorLoadAttempts == 0 && !mixedIdentifier.IsModelLoaded,
            "the mixed-evidence fixture queues metadata only and never executes model inference");

        return checks;
    }
}

using System.Text;

namespace IncomingTranslator;

/// <summary>Offline regressions: no capture devices, speech models, sockets or live app data.</summary>
internal static class IncomingContinuousAudioTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
        }

        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var audio = new IncomingContinuousAudioBuffer();
        byte[] silence = new byte[2560];
        byte[] hushed = MakePcm(17, 2560);
        byte[] unpitched = new byte[2560];
        new Random(7281).NextBytes(unpitched);

        foreach (byte[] pcm in new[] { silence, hushed, unpitched })
        {
            audio.Observe(pcm, start);
            Check(audio.TryRead(out IncomingPcmChunk chunk) && chunk.At == start && chunk.Bytes.SequenceEqual(pcm),
                "Continuous recognition admits silence, very quiet and unpitched PCM unchanged, without a VAD decision");
        }
        Check(audio.Count == 0 && audio.BufferedBytes == 0 && audio.DroppedChunks == 0,
            "Draining a continuous queue releases its buffered PCM without requiring a speech boundary");

        byte[] reusedCaptureBuffer = MakePcm(29, 2560);
        byte[] expectedCopy = (byte[])reusedCaptureBuffer.Clone();
        audio.Observe(reusedCaptureBuffer, start.AddMilliseconds(80));
        Array.Clear(reusedCaptureBuffer);
        Check(audio.TryRead(out IncomingPcmChunk copied) && copied.Bytes.SequenceEqual(expectedCopy) &&
            !ReferenceEquals(copied.Bytes, reusedCaptureBuffer),
            "A reused capture buffer cannot corrupt the PCM already queued for recognition");

        audio.Observe(hushed, start);
        Check(audio.TryRead(out IncomingPcmChunk beforePause) && beforePause.At == start,
            "Quiet speech onset reaches the sender immediately rather than waiting for a pitched vowel");
        bool silenceWasContinuous = true;
        for (int index = 1; index <= 750; index++)
        {
            DateTimeOffset at = start.AddMilliseconds(index * 80);
            audio.Observe(silence, at);
            silenceWasContinuous &= audio.TryRead(out IncomingPcmChunk chunk) && chunk.At == at &&
                chunk.Bytes.SequenceEqual(silence) && audio.Count == 0;
        }
        Check(silenceWasContinuous && audio.DroppedChunks == 0,
            "A full minute of silence stays continuously available on the same queue with no pause or turn reopening");
        DateTimeOffset afterPauseAt = start.AddMilliseconds(751 * 80);
        audio.Observe(unpitched, afterPauseAt);
        Check(audio.TryRead(out IncomingPcmChunk afterPause) && afterPause.At == afterPauseAt &&
            afterPause.Bytes.SequenceEqual(unpitched),
            "Speech after a long silence is admitted even when it contains no reliably detected pitch");

        var bounded = new IncomingContinuousAudioBuffer(capacityChunks: 3);
        for (int index = 0; index < 8; index++)
            bounded.Observe(MakePcm(index + 1, 2560), start.AddMilliseconds(index * 80));
        Check(bounded.Count == 3 && bounded.BufferedBytes == 3 * 2560 && bounded.DroppedChunks == 5,
            "Continuous capture keeps a bounded queue and counts each dropped oldest chunk");
        bool boundedSuffixIsCorrect = true;
        for (int index = 5; index < 8; index++)
            boundedSuffixIsCorrect &= bounded.TryRead(out IncomingPcmChunk chunk) &&
                chunk.At == start.AddMilliseconds(index * 80) && chunk.Bytes.SequenceEqual(MakePcm(index + 1, 2560));
        Check(boundedSuffixIsCorrect && !bounded.TryRead(out _) && bounded.BufferedBytes == 0,
            "Overflow retains the newest suffix in FIFO order with original audio timestamps");

        var disconnected = new IncomingContinuousAudioBuffer(capacityChunks: 4);
        disconnected.Observe(MakePcm(1, 2560), start);
        disconnected.Observe(MakePcm(2, 2560), start.AddMilliseconds(80));
        Check(disconnected.TryRead(out IncomingPcmChunk sent) && sent.Bytes.SequenceEqual(MakePcm(1, 2560)),
            "The connected sender consumes a chunk only once");
        // A transport replacement retains this same queue; no audio-reset operation is involved.
        disconnected.Observe(MakePcm(3, 2560), start.AddMilliseconds(160));
        disconnected.Observe(MakePcm(4, 2560), start.AddMilliseconds(240));
        bool pendingAudioSurvives = true;
        for (int index = 2; index <= 4; index++)
            pendingAudioSurvives &= disconnected.TryRead(out IncomingPcmChunk chunk) &&
                chunk.At == start.AddMilliseconds((index - 1) * 80) && chunk.Bytes.SequenceEqual(MakePcm(index, 2560));
        Check(pendingAudioSurvives && !disconnected.TryRead(out _),
            "Replacing a socket conserves unsent and newly captured PCM without replaying consumed speech");

        var adjustable = new IncomingContinuousAudioBuffer(capacityChunks: 3);
        for (int index = 0; index < 3; index++)
            adjustable.Observe(MakePcm(index + 1, 2560), start.AddMilliseconds(index * 80));
        adjustable.SetCapacity(8);
        for (int index = 3; index < 8; index++)
            adjustable.Observe(MakePcm(index + 1, 2560), start.AddMilliseconds(index * 80));
        Check(adjustable.Count == 8 && adjustable.BufferedBytes == 8 * 2560 && adjustable.DroppedChunks == 0,
            "A rare context rollover can temporarily reserve more pending PCM without dropping current audio");
        adjustable.SetCapacity(2);
        Check(adjustable.Count == 2 && adjustable.BufferedBytes == 2 * 2560 && adjustable.DroppedChunks == 6,
            "Restoring the normal queue bound immediately drops only the oldest excess PCM");
        Check(adjustable.TryRead(out IncomingPcmChunk keptFirst) && keptFirst.At == start.AddMilliseconds(480) &&
            adjustable.TryRead(out IncomingPcmChunk keptSecond) && keptSecond.At == start.AddMilliseconds(560),
            "Capacity changes preserve the retained newest audio order and timestamps");
        adjustable.Observe(hushed, start.AddMinutes(2));
        adjustable.Reset();
        Check(adjustable.Count == 0 && adjustable.BufferedBytes == 0 && !adjustable.TryRead(out _) &&
            adjustable.DroppedChunks == 6,
            "Stopping clears unsent PCM while keeping lifetime drop diagnostics monotonic");
        DateTimeOffset restartedAt = start.AddMinutes(3);
        adjustable.Observe(unpitched, restartedAt);
        Check(adjustable.TryRead(out IncomingPcmChunk restarted) && restarted.At == restartedAt &&
            restarted.Bytes.SequenceEqual(unpitched) && !adjustable.TryRead(out _),
            "Starting after a reset cannot resend stale source audio from the previous session");

        Check(!IncomingContinuousSessionPolicy.ShouldRollOver(new string('a', 3071), TimeSpan.FromSeconds(30)),
            "Continuous speech context is not reopened at ordinary sentence boundaries");
        Check(IncomingContinuousSessionPolicy.ShouldRollOver(new string('a', 3072), TimeSpan.FromSeconds(30)),
            "A bounded transcript context rolls over at its UTF-8 byte limit");
        string multibyte = new('你', 1024);
        Check(Encoding.UTF8.GetByteCount(multibyte) == 3072 &&
            IncomingContinuousSessionPolicy.ShouldRollOver(multibyte, TimeSpan.FromSeconds(30)) &&
            !IncomingContinuousSessionPolicy.ShouldRollOver(multibyte[..^1], TimeSpan.FromSeconds(30)),
            "Chinese context limits count UTF-8 bytes, not just characters");
        Check(!IncomingContinuousSessionPolicy.ShouldRollOver("", TimeSpan.FromMinutes(9)) &&
            IncomingContinuousSessionPolicy.ShouldRollOver("", TimeSpan.FromMinutes(10)),
            "Long-lived sessions renew on a bounded lifecycle timer, not because silence was classified");

        Check(!Recover(decoderSeconds: 0, sendSeconds: 0, sessionSeconds: 3600),
            "Healthy decoder heartbeat or PAD progress prevents recovery even during a long silent session");
        Check(!Recover(decoderSeconds: 29.99, sendSeconds: 0, sessionSeconds: 60),
            "An ordinary quiet interval cannot cause an early transport restart");
        Check(Recover(decoderSeconds: 30, sendSeconds: 3, sessionSeconds: 30),
            "A genuinely stalled decoder with a recently active sender has a bounded recovery path");
        Check(!Recover(decoderSeconds: 60, sendSeconds: 3.01, sessionSeconds: 60),
            "Inactive capture is not misdiagnosed as a decoder transport stall");
        Check(!Recover(decoderSeconds: 60, sendSeconds: -1, sessionSeconds: 60),
            "A future send timestamp cannot qualify stale decoder recovery");
        Check(!Recover(decoderSeconds: 60, sendSeconds: 0, sessionSeconds: 29.99),
            "New connections receive their startup grace before stall recovery");
        Check(!IncomingContinuousSessionPolicy.ShouldRecoverTransport(TimeSpan.FromMinutes(2), TimeSpan.Zero,
            TimeSpan.FromMinutes(2), finalizing: true),
            "An intentional context renewal is not treated as a competing stalled-transport recovery");

        RunRecognitionRecoveryChecks(Check);
        RunRecognitionReplayChecks(Check);

        Task.Run(() => RunStreamChecksAsync(Check)).GetAwaiter().GetResult();

        return checks;
    }

    private static void RunRecognitionReplayChecks(Action<bool, string> check)
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var unanswered = new IncomingContinuousAudioBuffer();
        for (int index = 0; index < 3; index++)
            unanswered.Observe(MakePcm(index + 1, 2560), start.AddMilliseconds(index * 80));
        check(unanswered.TryRead(out _) && unanswered.TryRead(out _) && unanswered.Count == 1,
            "The recognition replay fixture includes both consumed and still-pending PCM");
        check(unanswered.ReplayUnrecognizedAfter(null) == 3 * 2560 && unanswered.Count == 3,
            "Without recognized audio, a responsive-but-blank decoder can recover sent and pending PCM together");
        unanswered.Observe(MakePcm(4, 2560), start.AddMilliseconds(240));
        bool capturedAndReplayedOnce = true;
        for (int index = 0; index < 4; index++)
            capturedAndReplayedOnce &= unanswered.TryRead(out IncomingPcmChunk chunk) &&
                chunk.At == start.AddMilliseconds(index * 80) && chunk.Bytes.SequenceEqual(MakePcm(index + 1, 2560));
        check(capturedAndReplayedOnce && !unanswered.TryRead(out _) && unanswered.BufferedBytes == 0,
            "Recovery replaces rather than appends pending audio and preserves capture arriving after restoration exactly once");
        check(unanswered.CapacityMilliseconds == 2000,
            "A short recovered tail returns to the normal pending-audio bound as soon as it drains");

        var acknowledged = new IncomingContinuousAudioBuffer();
        for (int index = 0; index < 3; index++)
        {
            acknowledged.Observe(MakePcm(index + 11, 2560), start.AddMilliseconds(index * 80));
            acknowledged.TryRead(out _);
        }
        DateTimeOffset recognizedThrough = start.AddMilliseconds(120).AddTicks(100);
        check(acknowledged.ReplayUnrecognizedAfter(recognizedThrough) == 1278 + 2560 &&
            acknowledged.TryRead(out IncomingPcmChunk crossing) && crossing.Bytes.Length == 1278 &&
            crossing.Bytes.SequenceEqual(MakePcm(12, 2560)[1282..]) &&
            crossing.At == start.AddMilliseconds(80).AddTicks(400625) && crossing.At >= recognizedThrough,
            "Already recognized chunks are excluded and a crossing chunk is trimmed to the next whole PCM16 sample with an exact adjusted timestamp");
        check(acknowledged.TryRead(out IncomingPcmChunk suffix) && suffix.At == start.AddMilliseconds(160) &&
            suffix.Bytes.SequenceEqual(MakePcm(13, 2560)) && !acknowledged.TryRead(out _) && acknowledged.BufferedBytes == 0,
            "Only the unrecognized suffix follows a sliced acknowledgement boundary in original capture order");
        check(acknowledged.ReplayUnrecognizedAfter(start.AddMilliseconds(240)) == 0 && acknowledged.Count == 0 &&
            !acknowledged.TryRead(out _),
            "A fully acknowledged recovery tail cannot replay any old speech");

        var bounded = new IncomingContinuousAudioBuffer();
        for (int index = 0; index < 120; index++)
        {
            bounded.Observe(MakePcm(index + 1, 2560), start.AddMilliseconds(index * 80));
            bounded.TryRead(out _);
        }
        long dropsBeforeReplay = bounded.DroppedChunks;
        check(bounded.ReplayUnrecognizedAfter(null) == 100 * 2560 && bounded.Count == 100 &&
            bounded.CapacityMilliseconds == 8000 && bounded.DroppedChunks == dropsBeforeReplay,
            "Recovery history is bounded to eight seconds of standard capture even after a longer fully sent session");
        bool boundedTailCorrect = true, drainedCapacityCorrect = true;
        for (int index = 20; index < 120; index++)
        {
            boundedTailCorrect &= bounded.TryRead(out IncomingPcmChunk chunk) &&
                chunk.At == start.AddMilliseconds(index * 80) && chunk.Bytes.SequenceEqual(MakePcm(index + 1, 2560));
            if (index == 93) drainedCapacityCorrect &= bounded.CapacityMilliseconds == 8000;
            if (index == 94) drainedCapacityCorrect &= bounded.CapacityMilliseconds == 2000;
        }
        check(boundedTailCorrect && bounded.Count == 0 && bounded.BufferedBytes == 0 &&
            drainedCapacityCorrect && bounded.CapacityMilliseconds == 2000,
            "An eight-second recovered tail prunes old history, drains without gaps, and shrinks only after at most twenty-five chunks remain");
        bounded.Reset();
        check(bounded.Count == 0 && bounded.BufferedBytes == 0 && bounded.CapacityMilliseconds == 2000 &&
            bounded.ReplayUnrecognizedAfter(null) == 0 && bounded.Count == 0,
            "Stopping capture clears private recovery history as well as the pending queue, preventing audio from a previous session returning");
        bounded.Observe(MakePcm(501, 2560), start.AddMinutes(10));
        check(bounded.ReplayUnrecognizedAfter(null) == 2560 && bounded.TryRead(out IncomingPcmChunk fresh) &&
            fresh.At == start.AddMinutes(10) && fresh.Bytes.SequenceEqual(MakePcm(501, 2560)) && !bounded.TryRead(out _),
            "Only newly captured PCM can be restored after a stop and fresh start");
    }

    private static void RunRecognitionRecoveryChecks(Action<bool, string> check)
    {
        bool Recovery(double voice = 3, double voiceAge = 0, double wordsAge = 18,
            double sessionAge = 60, double fallbackAge = 60, int serverAttempts = 0,
            int fallbackCount = 0, bool finalizing = false) =>
            IncomingContinuousSessionPolicy.ShouldRecoverRecognition(voice, voiceAge, wordsAge,
                sessionAge, fallbackAge, serverAttempts, fallbackCount, finalizing);

        check(!Recovery(voice: 0, voiceAge: 60, wordsAge: 600, sessionAge: 600, serverAttempts: 20),
            "Idle playback and healthy PAD during silence never renew recognition merely because words are old");
        check(!Recovery(voice: 1.99, serverAttempts: 20),
            "A short chirp or isolated short reply is insufficient evidence for a no-word recognition refresh");
        check(Recovery(voice: 2, wordsAge: 18),
            "Sustained recent speech with no useful words has a bounded recovery even while PAD keeps transport healthy");
        check(!Recovery(wordsAge: 17.99),
            "Without failed server recoveries, normal decoding receives the full eighteen-second recognition grace");
        check(Recovery(wordsAge: 12, serverAttempts: 2),
            "Two failed server recovery attempts allow the earlier twelve-second recognition deadline");
        check(!Recovery(wordsAge: 11.99, serverAttempts: 50) &&
            !Recovery(wordsAge: 12, serverAttempts: 1),
            "Repeated server recovery messages cannot bypass the minimum useful-word deadline or two-attempt threshold");
        check(!Recovery(wordsAge: 0, serverAttempts: 50) && !Recovery(wordsAge: 8, serverAttempts: 50),
            "Actual recently changed words veto an unnecessary recognition refresh");
        check(!Recovery(voiceAge: 2.51, serverAttempts: 20) &&
            Recovery(voiceAge: 2.5, wordsAge: 18),
            "Old voice activity cannot qualify a silence interval, while the recent-voice boundary is inclusive");
        check(!Recovery(voiceAge: -0.01, serverAttempts: 20),
            "A future or invalid voice timestamp cannot trigger recognition recovery");
        check(!Recovery(sessionAge: 11.99, serverAttempts: 20) &&
            Recovery(sessionAge: 12, wordsAge: 12, serverAttempts: 2),
            "A newly connected decoder receives startup grace before even server-assisted recognition recovery");
        check(!Recovery(finalizing: true, serverAttempts: 20),
            "Recognition recovery cannot race an intentional text-capacity handoff");
        check(!Recovery(fallbackAge: 19.99) && Recovery(fallbackAge: 20),
            "The first recognition recovery observes a bounded twenty-second cooldown");
        check(!Recovery(fallbackAge: 29.99, fallbackCount: 2) &&
            Recovery(fallbackAge: 30, fallbackCount: 2),
            "The third attempted recognition recovery uses a thirty-second cooldown");
        check(!Recovery(fallbackAge: 39.99, fallbackCount: 1000) &&
            Recovery(fallbackAge: 40, fallbackCount: 1000),
            "Repeated recognition stalls back off to forty seconds without an eventual permanent lockout");
        check(IncomingContinuousSessionPolicy.RecognitionRecoveryCooldownSeconds(0) == 20 &&
            IncomingContinuousSessionPolicy.RecognitionRecoveryCooldownSeconds(1) == 20 &&
            IncomingContinuousSessionPolicy.RecognitionRecoveryCooldownSeconds(2) == 30 &&
            IncomingContinuousSessionPolicy.RecognitionRecoveryCooldownSeconds(3) == 40,
            "Useful-word recovery cadence is explicit, bounded, and shared by its policy");
        check(!Recovery(voice: double.NaN) && !Recovery(voice: double.PositiveInfinity) &&
            !Recovery(voiceAge: double.NaN) && !Recovery(wordsAge: double.PositiveInfinity) &&
            !Recovery(sessionAge: double.PositiveInfinity) && !Recovery(fallbackAge: double.NaN),
            "Non-finite diagnostic input is rejected rather than mistaken for sustained missed speech");

        var recoveryTimes = new List<int>();
        int failures = 0, lastFallback = -60, startedAt = -60;
        for (int second = 0; second <= 220; second++)
            if (Recovery(wordsAge: second - startedAt, sessionAge: second - startedAt,
                fallbackAge: second - lastFallback, fallbackCount: failures))
            {
                recoveryTimes.Add(second); failures++; lastFallback = second; startedAt = second;
            }
        check(recoveryTimes.Count >= 5 && recoveryTimes.Zip(recoveryTimes.Skip(1),
            (before, after) => after - before).All(interval => interval is >= 20 and <= 40),
            "An always-responsive blank decoder continues bounded useful-word recovery attempts instead of getting stuck indefinitely");
    }

    private static async Task RunStreamChecksAsync(Action<bool, string> check)
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var audio = new IncomingContinuousAudioBuffer();
        byte[] silence = new byte[2560], hushed = MakePcm(11, 2560);
        audio.Observe(silence, start);
        audio.Observe(hushed, start.AddMilliseconds(80));
        var initialSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<IncomingPcmChunk>();
        int endCount = 0;
        using (var stop = new CancellationTokenSource())
        {
            Task sender = IncomingContinuousSessionPolicy.StreamAsync(audio, (chunk, _) =>
            {
                sent.Add(chunk);
                if (sent.Count == 2) initialSent.TrySetResult(true);
                if (sent.Count == 3) laterSent.TrySetResult(true);
                return Task.CompletedTask;
            }, _ => { endCount++; return Task.CompletedTask; }, () => false, stop.Token);
            try
            {
                await initialSent.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                check(sent[0].Bytes.SequenceEqual(silence) && sent[1].Bytes.SequenceEqual(hushed),
                    "The continuous sender actually emits preloaded silence and hushed PCM unchanged");
                await Task.Delay(70).ConfigureAwait(false);
                check(!sender.IsCompleted && audio.Count == 0 && endCount == 0,
                    "An empty audio queue waits without ending or pausing the same recognition sender");
                DateTimeOffset laterAt = start.AddSeconds(45);
                audio.Observe(MakePcm(37, 2560), laterAt);
                await laterSent.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                check(!sender.IsCompleted && sent.Count == 3 && sent[2].At == laterAt &&
                    sent[2].Bytes.SequenceEqual(MakePcm(37, 2560)) && endCount == 0,
                    "Audio captured after a quiet interval resumes in the very same sender without reopening a speech turn");
            }
            finally { stop.Cancel(); }
            bool canceled = false;
            try { await sender.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && endCount == 0,
                "User cancellation releases an idle sender promptly without issuing an end-audio or rollover request");
        }

        var alreadyCanceledAudio = new IncomingContinuousAudioBuffer();
        alreadyCanceledAudio.Observe(hushed, start);
        using (var alreadyStopped = new CancellationTokenSource())
        {
            alreadyStopped.Cancel();
            int canceledSends = 0, canceledEnds = 0;
            bool canceled = false;
            try
            {
                await IncomingContinuousSessionPolicy.StreamAsync(alreadyCanceledAudio,
                    (_, _) => { canceledSends++; return Task.CompletedTask; },
                    _ => { canceledEnds++; return Task.CompletedTask; }, () => true,
                    alreadyStopped.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && canceledSends == 0 && canceledEnds == 0 && alreadyCanceledAudio.Count == 1,
                "A sender canceled before admission neither consumes buffered PCM nor starts a context renewal");
        }

        var handoff = new IncomingContinuousAudioBuffer();
        int rolloverSends = 0, rolloverEnds = 0;
        await IncomingContinuousSessionPolicy.StreamAsync(handoff,
            (_, _) => { rolloverSends++; return Task.CompletedTask; }, _ =>
            {
                rolloverEnds++;
                // Audio capture remains live while an old context is ending.
                for (int index = 0; index < 50; index++)
                    handoff.Observe(MakePcm(index + 1, 2560), start.AddMilliseconds(index * 80));
                return Task.CompletedTask;
            }, () => true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        check(rolloverSends == 0 && rolloverEnds == 1,
            "A lifecycle rollover ends an empty silent session exactly once instead of waiting for a VAD utterance");
        check(handoff.Count == 50 && handoff.BufferedBytes == 50 * 2560 && handoff.DroppedChunks == 0 &&
            handoff.CapacityMilliseconds == IncomingContinuousAudioBuffer.HandoffCapacityChunks * 80,
            "Pending future audio captured during context renewal survives within the temporary handoff capacity");

        int resumedCount = 0, resumedEnds = 0;
        bool resumedOrderIsCorrect = true, wideBeforeNormal = false, normalAtBoundary = false;
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var stop = new CancellationTokenSource())
        {
            Task replacementSender = IncomingContinuousSessionPolicy.StreamAsync(handoff, (chunk, _) =>
            {
                resumedOrderIsCorrect &= chunk.At == start.AddMilliseconds(resumedCount * 80) &&
                    chunk.Bytes.SequenceEqual(MakePcm(resumedCount + 1, 2560));
                resumedCount++;
                if (resumedCount == 24)
                    wideBeforeNormal = handoff.Count == 26 && handoff.CapacityMilliseconds ==
                        IncomingContinuousAudioBuffer.HandoffCapacityChunks * 80;
                if (resumedCount == 25)
                    normalAtBoundary = handoff.Count == 25 && handoff.CapacityMilliseconds ==
                        IncomingContinuousAudioBuffer.NormalCapacityChunks * 80;
                if (resumedCount == 50) drained.TrySetResult(true);
                return Task.CompletedTask;
            }, _ => { resumedEnds++; return Task.CompletedTask; }, () => false, stop.Token);
            try
            {
                await drained.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                check(resumedCount == 50 && resumedOrderIsCorrect && handoff.DroppedChunks == 0,
                    "The replacement sender drains every retained handoff chunk in order, with no duplicate or missing speech");
                check(wideBeforeNormal && normalAtBoundary && handoff.Count == 0 &&
                    handoff.CapacityMilliseconds == IncomingContinuousAudioBuffer.NormalCapacityChunks * 80,
                    "Handoff capacity returns to the normal bound only after sufficient queued PCM drains, without dropping it");
                check(!replacementSender.IsCompleted && resumedEnds == 0,
                    "Finishing the handoff backlog leaves the replacement continuous sender listening");
            }
            finally { stop.Cancel(); }
            try { await replacementSender.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        await RunSendDeadlineChecksAsync(check).ConfigureAwait(false);
    }

    private static async Task RunSendDeadlineChecksAsync(Action<bool, string> check)
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TimeSpan deadline = TimeSpan.FromMilliseconds(40);
        var blocked = new IncomingContinuousAudioBuffer();
        blocked.Observe(MakePcm(1, 2560), start);
        CancellationTokenSource? blockedFixture = null;
        Task? blockedFixtureTask = null;
        int blockedEnds = 0;
        bool audioTimedOut;
        try
        {
            audioTimedOut = await HasSendDeadlineAsync(IncomingContinuousSessionPolicy.StreamAsync(blocked,
                (_, sendToken) =>
                {
                    blocked.Observe(MakePcm(2, 2560), start.AddMilliseconds(80));
                    blocked.Observe(MakePcm(3, 2560), start.AddMilliseconds(160));
                    blockedFixture = CancellationTokenSource.CreateLinkedTokenSource(sendToken);
                    return blockedFixtureTask = Task.Delay(Timeout.Infinite, blockedFixture.Token);
                }, _ => { blockedEnds++; return Task.CompletedTask; }, () => false,
                CancellationToken.None, sendTimeout: deadline)).ConfigureAwait(false);
        }
        finally
        {
            blockedFixture?.Cancel();
            if (blockedFixtureTask is not null)
                try { await blockedFixtureTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            blockedFixture?.Dispose();
        }
        check(audioTimedOut && blockedEnds == 0,
            "An audio send blocked by server backpressure fails within its deadline instead of freezing recognition");
        check(blocked.Count == 2 && blocked.DroppedChunks == 0 &&
            blocked.TryRead(out IncomingPcmChunk retainedFirst) && retainedFirst.At == start.AddMilliseconds(80) &&
            blocked.TryRead(out IncomingPcmChunk retainedSecond) && retainedSecond.At == start.AddMilliseconds(160),
            "Newer captured PCM remains available for the replacement connection after a timed-out audio send");

        var ignoresCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unresponsive = new IncomingContinuousAudioBuffer();
        unresponsive.Observe(MakePcm(7, 2560), start);
        bool ignoredTimedOut;
        try
        {
            ignoredTimedOut = await HasSendDeadlineAsync(IncomingContinuousSessionPolicy.StreamAsync(unresponsive,
                (_, _) => ignoresCancellation.Task, _ => Task.CompletedTask, () => false,
                CancellationToken.None, sendTimeout: deadline)).ConfigureAwait(false);
        }
        finally { ignoresCancellation.TrySetCanceled(); }
        check(ignoredTimedOut,
            "A send implementation that ignores cancellation still cannot hold the continuous sender past its deadline");

        var finalizing = new IncomingContinuousAudioBuffer();
        CancellationTokenSource? endFixture = null;
        Task? endFixtureTask = null;
        int endCalls = 0, unexpectedAudioCalls = 0;
        bool endTimedOut;
        try
        {
            endTimedOut = await HasSendDeadlineAsync(IncomingContinuousSessionPolicy.StreamAsync(finalizing,
                (_, _) => { unexpectedAudioCalls++; return Task.CompletedTask; }, endToken =>
                {
                    endCalls++;
                    finalizing.Observe(MakePcm(13, 2560), start.AddSeconds(1));
                    endFixture = CancellationTokenSource.CreateLinkedTokenSource(endToken);
                    return endFixtureTask = Task.Delay(Timeout.Infinite, endFixture.Token);
                }, () => true, CancellationToken.None, sendTimeout: deadline)).ConfigureAwait(false);
        }
        finally
        {
            endFixture?.Cancel();
            if (endFixtureTask is not null)
                try { await endFixtureTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            endFixture?.Dispose();
        }
        check(endTimedOut && endCalls == 1 && unexpectedAudioCalls == 0 && finalizing.Count == 1 &&
            finalizing.DroppedChunks == 0,
            "A stuck end-audio send is bounded and retains audio captured during the failed context handoff");

        var cancellationAudio = new IncomingContinuousAudioBuffer();
        cancellationAudio.Observe(MakePcm(17, 2560), start);
        var enteredSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? parentFixture = null;
        Task? parentFixtureTask = null;
        using (var stop = new CancellationTokenSource())
        {
            Task sender = IncomingContinuousSessionPolicy.StreamAsync(cancellationAudio, (_, sendToken) =>
            {
                parentFixture = CancellationTokenSource.CreateLinkedTokenSource(sendToken);
                parentFixtureTask = Task.Delay(Timeout.Infinite, parentFixture.Token);
                enteredSend.TrySetResult(true);
                return parentFixtureTask;
            }, _ => Task.CompletedTask, () => false, stop.Token, sendTimeout: TimeSpan.FromSeconds(2));
            bool parentCanceled = false;
            try
            {
                await enteredSend.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                stop.Cancel();
                try { await sender.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (OperationCanceledException) { parentCanceled = true; }
            }
            finally
            {
                stop.Cancel();
                parentFixture?.Cancel();
                if (parentFixtureTask is not null)
                    try { await parentFixtureTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                parentFixture?.Dispose();
            }
            check(parentCanceled,
                "User stop during a blocked send remains cancellation rather than a misleading server timeout");
        }

        bool rejectedZero = false;
        int invalidCalls = 0;
        try
        {
            await IncomingContinuousSessionPolicy.StreamAsync(new IncomingContinuousAudioBuffer(),
                (_, _) => { invalidCalls++; return Task.CompletedTask; },
                _ => { invalidCalls++; return Task.CompletedTask; }, () => true,
                CancellationToken.None, sendTimeout: TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException) { rejectedZero = true; }
        check(rejectedZero && invalidCalls == 0,
            "A zero send deadline is rejected before any audio or end-audio operation starts");
    }

    private static async Task<bool> HasSendDeadlineAsync(Task sender)
    {
        try { await sender.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException exception)
        {
            // The outer fixture timeout must not masquerade as a successful server deadline.
            return exception.Message.Contains("stopped accepting audio", StringComparison.Ordinal);
        }
        return false;
    }

    private static bool Recover(double decoderSeconds, double sendSeconds, double sessionSeconds) =>
        IncomingContinuousSessionPolicy.ShouldRecoverTransport(TimeSpan.FromSeconds(decoderSeconds),
            TimeSpan.FromSeconds(sendSeconds), TimeSpan.FromSeconds(sessionSeconds), finalizing: false);

    private static byte[] MakePcm(int marker, int bytes)
    {
        var pcm = new byte[bytes];
        for (int index = 0; index + 1 < bytes; index += 2)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index, 2), (short)marker);
        return pcm;
    }
}

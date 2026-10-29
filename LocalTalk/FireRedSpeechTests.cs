using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

// Metadata, managed configuration and synthetic PCM only. Never constructs a
// native recognizer, downloads a model, reads real preferences or opens audio.
internal static class FireRedSpeechTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("FireRed subtitle speech: " + message); checks++; }
        void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException("FireRed subtitle speech: " + message);
        }

        Check(IncomingRecognitionCatalog.Choices.Select(item => item.Id).Distinct().Count() == IncomingRecognitionCatalog.Choices.Count,
            "each visible speech model has a unique stable identifier");
        foreach (string id in new[] { "firered2-ctc", "firered2-aed", "nemotron", "voxtral", "vibevoice" })
        {
            Check(IncomingRecognitionCatalog.Find(id).Id == id && IncomingRecognitionCatalog.CanonicalEngine(id) == id,
                "each implemented incoming engine is selectable: " + id);
            Check(!IncomingRecognitionCatalog.SupportsSource(id, "unsupported"),
                "an unrecognized source is not silently advertised as automatic: " + id);
        }
        Check(IncomingRecognitionCatalog.Find(null).Id == "voxtral" && IncomingRecognitionCatalog.Find("unknown").Id == "voxtral" &&
            IncomingRecognitionCatalog.CanonicalEngine("unknown") == "voxtral",
            "legacy or missing incoming engine preference retains the established recognizer");
        foreach (string id in new[] { "firered2-ctc", "firered2-aed" })
        {
            Check(IncomingRecognitionCatalog.UsesNative(id) && IncomingRecognitionCatalog.UsesFireRed(id), "FireRed uses the native path: " + id);
            Check(IncomingRecognitionCatalog.SourceLanguagesFor(id).SequenceEqual(new[] { "Chinese", "English" }),
                "FireRed does not imply the VAD/LID system's much broader transcription coverage");
            Check(IncomingRecognitionCatalog.SupportsSource(id, "Chinese") && IncomingRecognitionCatalog.SupportsSource(id, "zh-CN") &&
                IncomingRecognitionCatalog.Find(id).SupportsSource("English"), "FireRed accepts the two real source languages and aliases");
            foreach (string source in new[] { "Automatic", "Japanese", "Korean", "Thai", "Spanish" })
                Check(!IncomingRecognitionCatalog.SupportsSource(id, source), "FireRed rejects unsupported source selection: " + source);
        }
        foreach (string source in new[] { "Automatic", "Chinese", "English", "Japanese", "Korean", "Spanish", "French", "German", "Portuguese", "Russian" })
            Check(IncomingRecognitionCatalog.SupportsSource("nemotron", source), "native multilingual subtitles preserve source choice: " + source);
        Check(!IncomingRecognitionCatalog.SupportsSource("nemotron", "Thai"), "adaptation-ready Thai is not promoted into working transcription");
        foreach (string id in new[] { "voxtral", "vibevoice" })
            Check(IncomingRecognitionCatalog.SupportsSource(id, "Automatic") && !IncomingRecognitionCatalog.SupportsSource(id, "Chinese") &&
                !IncomingRecognitionCatalog.UsesNative(id), "automatic-only engines do not pretend to force decoder languages: " + id);
        foreach ((string source, string code) in new[] { ("Chinese", "zh-CN"), ("en", "en-US"), ("Japanese", "ja-JP"),
            ("Korean", "ko-KR"), ("Portuguese", "pt-BR"), ("Automatic", "auto") })
            Check(IncomingRecognitionCatalog.LanguageCode(source) == code, "actual language codes are separate from display names: " + source);

        var settings = new AppSettings { OwnRecognitionEngine = "voxtral", OwnVoiceSourceLanguage = "English",
            OwnVoiceTargetLanguage = "Korean", IncomingSpeechEngine = "nemotron", IncomingSourceLanguage = "Chinese" };
        string original = JsonSerializer.Serialize(settings);
        AppSettings adapted = IncomingRecognitionCatalog.CreateNativeSettings(settings);
        Check(!ReferenceEquals(settings, adapted) && adapted.OwnRecognitionEngine == "nemotron" &&
            adapted.OwnVoiceSourceLanguage == "Chinese" && adapted.OwnVoiceTargetLanguage == "Korean",
            "incoming native preparation uses an isolated compatible settings snapshot");
        Check(JsonSerializer.Serialize(settings) == original && IncomingRecognitionCatalog.UsesNative(settings),
            "preparing incoming recognition never changes the user's microphone choices");
        var detached = settings.Copy(); detached.IncomingSpeechEngine = "firered2-aed";
        Check(IncomingRecognitionCatalog.UsesFireRed(detached) && !IncomingRecognitionCatalog.UsesFireRed(settings),
            "settings-based engine predicates preserve channel separation");

        string absentRoot = Path.Combine(Path.GetTempPath(), "SayWhat-FireRed-Metadata-" + Guid.NewGuid().ToString("N"));
        var provisioner = new FireRedSpeechProvisioner(absentRoot);
        Check(!Directory.Exists(absentRoot), "metadata construction does not create app data");
        foreach ((string engine, string revision, long total) in new[]
        {
            ("firered2-ctc", "423be1acfdc5d8aaa5a485fb6263fe4ea8570b06", 775940592L),
            ("firered2-aed", "374cff185e952c40fcf2f6da972a3b6cf340608d", 1234657933L)
        })
        {
            IReadOnlyList<ProvisionAsset> assets = FireRedSpeechProvisioner.Assets(engine);
            Check(assets.Sum(asset => asset.SizeBytes) == total && provisioner.PendingDownloadBytes(engine) == total,
                "fresh download progress uses measured direct-file byte sizes: " + engine);
            Check(!provisioner.IsReady(engine), "absent files never count as an installed recognizer: " + engine);
            Check(provisioner.ModelRoot(engine) == Path.Combine(absentRoot, "models", "subtitle-speech", engine),
                "model family storage is isolated in the app's data root");
            foreach (ProvisionAsset asset in assets)
            {
                var uri = new Uri(asset.DownloadUrl);
                Check(uri.Scheme == "https" && uri.Host == "huggingface.co" && string.IsNullOrEmpty(uri.UserInfo) &&
                    string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
                    uri.AbsolutePath.Contains("/csukuangfj2/sherpa-onnx-fire-red-asr2-") &&
                    uri.AbsolutePath.EndsWith("/resolve/" + revision + "/" + asset.FileName, StringComparison.Ordinal),
                    "verified assets use an immutable maintainer conversion revision: " + asset.FileName);
                Check(Regex.IsMatch(asset.Sha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) && asset.SizeBytes > 0,
                    "every required file has a complete size and SHA-256");
                Check(provisioner.ModelPath(engine, asset.FileName) == Path.Combine(provisioner.ModelRoot(engine), asset.FileName),
                    "asset paths are drawn from the fixed manifest");
            }
            var config = SherpaSpeechRecognizer.CreateConfig(engine, provisioner);
            Check(config.FeatConfig.SampleRate == 16000 && config.FeatConfig.FeatureDim == 80 &&
                config.ModelConfig.NumThreads == 2 && config.ModelConfig.Provider == "cpu",
                "pure configuration bounds CPU work and uses the real model's audio features");
            Check(config.ModelConfig.Tokens == provisioner.ModelPath(engine, "tokens.txt"), "configuration uses this model's verified vocabulary");
            if (engine == "firered2-ctc")
                Check(config.ModelConfig.FireRedAsrCtc.Model == provisioner.ModelPath(engine, "model.int8.onnx") &&
                    string.IsNullOrEmpty(config.ModelConfig.FireRedAsr.Encoder) && string.IsNullOrEmpty(config.ModelConfig.FireRedAsr.Decoder),
                    "CTC configuration does not accidentally load the AED decoder");
            else
                Check(config.ModelConfig.FireRedAsr.Encoder == provisioner.ModelPath(engine, "encoder.int8.onnx") &&
                    config.ModelConfig.FireRedAsr.Decoder == provisioner.ModelPath(engine, "decoder.int8.onnx") &&
                    string.IsNullOrEmpty(config.ModelConfig.FireRedAsrCtc.Model), "AED configuration selects both second-generation components only");
        }
        Check(FireRedSpeechProvisioner.CtcAssets[0].Sha256 == "ca3dbabd82170110cc0b343c2890866d449984bc9cd92b9a18371ff80a81bb99" &&
            FireRedSpeechProvisioner.AedAssets[0].Sha256 == "54048d66b6e8f3c80ea7ce95efe794587b0fd81d7271651d0decd3803852ae82" &&
            FireRedSpeechProvisioner.AedAssets[1].Sha256 == "b840ce7196ae4a14d05ae84bbf56082b6b61ccec5610fda907dddbcea37354ff",
            "the exact reviewed model digests cannot drift to first-generation or mutable files");
        Check(FireRedSpeechProvisioner.CtcAssets.Last().Sha256 == "1bc613de2112d257e61a349c3e72d1b1a9cf19c33d3ca954197ad2171e5ea07b" &&
            FireRedSpeechProvisioner.AedAssets.Last().Sha256 == FireRedSpeechProvisioner.CtcAssets.Last().Sha256,
            "the shared bilingual token file retains its independently checked digest");
        Check(!provisioner.IsReady("voxtral"), "a foreign engine does not appear installed by this provisioner");
        Reject<ArgumentException>(() => FireRedSpeechProvisioner.Assets("unknown"), "unknown model cannot choose arbitrary download metadata");
        Reject<InvalidOperationException>(() => provisioner.ModelPath("firered2-ctc", "../model.onnx"), "a path outside the manifest is rejected");
        Reject<ArgumentException>(() => SherpaSpeechRecognizer.CreateConfig("nemotron", provisioner), "a different family cannot construct FireRed configuration");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Reject<OperationCanceledException>(() => provisioner.PrepareAsync("firered2-ctc", null, cancelled.Token).GetAwaiter().GetResult(),
                "cancelled preparation reaches neither downloads nor inference");
        }
        Check(!Directory.Exists(absentRoot), "all metadata, configuration and cancelled preparation leave the candidate data root absent");

        DateTimeOffset start = DateTimeOffset.Parse("2026-10-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        IncomingPcmChunk Chunk(int sampleCount, short level, DateTimeOffset at)
        {
            byte[] pcm = new byte[sampleCount * 2];
            for (int i = 0; i < sampleCount; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), level);
            return new(pcm, at);
        }
        var invalid = new FireRedAudioSegmenter();
        Reject<ArgumentException>(() => invalid.Observe(new(new byte[1], start), 1200), "partial PCM16 samples are rejected");
        Reject<ArgumentException>(() => invalid.Observe(new(new byte[32002], start), 1200), "oversized capture chunks cannot grow a segment unboundedly");
        Check(invalid.Observe(new(Array.Empty<byte>(), start), 1200) is null, "an empty capture chunk produces no decode work");

        var finalOnly = new FireRedAudioSegmenter();
        Check(!finalOnly.SendEarlyPreviews, "FireRed does not repeatedly decode unfinished speech by default");
        var completeSections = new List<FireRedDecodeJob>();
        var completeSamples = new List<float>();
        for (int i = 0; i < 140; i++)
        {
            completeSamples.AddRange(Enumerable.Repeat(1000 / 32768f, 1280));
            if (finalOnly.Observe(Chunk(1280, 1000, start.AddMilliseconds(i * 80)), 1200) is { } job) completeSections.Add(job);
        }
        Check(completeSections.Count == 1 && completeSections[0] is { Final: true, Revision: 1 } &&
            completeSections[0].Samples.Length == FireRedAudioSegmenter.MaxSamples,
            "ongoing default speech creates only the eight-second finished section, with no repeated preview inference");
        for (int i = 0; i < 15; i++)
        {
            completeSamples.AddRange(Enumerable.Repeat(0f, 1280));
            if (finalOnly.Observe(Chunk(1280, 0, start.AddMilliseconds((140 + i) * 80)), 1200) is { } job) completeSections.Add(job);
        }
        Check(completeSections.Count == 2 && completeSections.All(job => job.Final && job.Revision == 1) &&
            completeSections.SelectMany(job => job.Samples).SequenceEqual(completeSamples),
            "the real pause finishes the retained default tail without dropping or duplicating captured samples");
        var livePolicy = new FireRedAudioSegmenter { SendEarlyPreviews = true };
        for (int i = 0; i < 30; i++) livePolicy.Observe(Chunk(1280, 1000, start.AddMilliseconds(i * 80)), 1200);
        livePolicy.SendEarlyPreviews = false;
        FireRedDecodeJob? afterOptOut = null;
        for (int i = 30; i < 100; i++)
            if (livePolicy.Observe(Chunk(1280, 1000, start.AddMilliseconds(i * 80)), 1200) is { } job)
            { Check(job.Final, "turning early translations off suppresses further draft decode work immediately"); afterOptOut = job; }
        Check(afterOptOut is { Final: true } && afterOptOut.Samples.Length == FireRedAudioSegmenter.MaxSamples,
            "turning drafts off retains audio collected before the setting changed");

        var continuous = new FireRedAudioSegmenter { SendEarlyPreviews = true };
        var snapshots = new List<FireRedDecodeJob>();
        for (int i = 0; i < 100; i++)
            if (continuous.Observe(Chunk(1280, 1000, start.AddMilliseconds(i * 80)), 1200) is { } snapshot) snapshots.Add(snapshot);
        Check(snapshots.Count == 4 && snapshots.Select(item => item.Samples.Length).SequenceEqual(new[] { 38400, 76800, 115200, 128000 }),
            "continuous speech receives spaced previews and a hard eight-second final without waiting for silence");
        Check(snapshots.Take(3).All(item => !item.Final) && snapshots[3].Final &&
            snapshots.Select(item => item.Revision).SequenceEqual(new[] { 1, 2, 3, 4 }) && snapshots.All(item => item.Segment == 1),
            "draft revisions and final refer to the same section");
        foreach (FireRedDecodeJob item in snapshots)
        {
            // PCM16 at 16 kHz has exactly 625 DateTime ticks per sample.
            // Expected offsets deliberately avoid floating AddSeconds rounding.
            long expectedTicks = item.Samples.LongLength * 625;
            Check(item.Start == start && item.End == start.AddTicks(expectedTicks),
                $"preview timestamps cover exact sample offsets; revision={item.Revision}; samples={item.Samples.Length}; " +
                $"startTicks={(item.Start - start).Ticks}; endTicks={(item.End - start).Ticks}; expectedEndTicks={expectedTicks}");
        }
        Check(snapshots[0].Samples.Length == 38400 && snapshots[0].Samples.All(sample => sample == 1000 / 32768f),
            "later capture leaves earlier PCM snapshots immutable");
        FireRedDecodeJob? secondContinuous = null;
        for (int i = 0; i < 30; i++) secondContinuous = continuous.Observe(Chunk(1280, 2000, start.AddSeconds(8).AddMilliseconds(i * 80)), 1200) ?? secondContinuous;
        Check(secondContinuous is { Segment: 2, Revision: 1, Final: false } && secondContinuous.Start == start.AddSeconds(8) &&
            secondContinuous.Samples.Length == 38400, "uninterrupted speech starts another section immediately after the hard boundary");

        var quietResume = new FireRedAudioSegmenter();
        for (int i = 0; i < 40; i++) Check(quietResume.Observe(Chunk(16000, 0, start.AddSeconds(i)), 1200) is null,
            "long silence does not create hallucination-prone decode jobs or pause future intake");
        Check(quietResume.Observe(Chunk(1280, 1000, start.AddSeconds(40)), 1200) is null, "short speech after long silence is retained");
        FireRedDecodeJob? resumedFinal = null;
        for (int i = 0; i < 15; i++)
            if (quietResume.Observe(Chunk(1280, 0, start.AddMilliseconds(40080 + i * 80)), 1200) is { } job) resumedFinal = job;
        Check(resumedFinal is { Final: true, Segment: 1 } && resumedFinal.Samples.Length == 3840 + 1280 + 19200,
            "a single short utterance finalizes with bounded pre-roll and the selected silence pause");
        Check(resumedFinal!.Start == start.AddMilliseconds(39760) && resumedFinal.End == start.AddMilliseconds(41280),
            "pre-roll changes the start timestamp rather than stretching the speech duration");
        Check(resumedFinal.Samples.Take(3840).All(sample => sample == 0) &&
            resumedFinal.Samples.Skip(3840).Take(1280).All(sample => sample == 1000 / 32768f), "quiet pre-roll never removes the beginning of resumed speech");
        quietResume.Observe(Chunk(1280, 1500, start.AddSeconds(42)), 1200);
        FireRedDecodeJob? laterFinal = null;
        for (int i = 0; i < 15; i++) laterFinal = quietResume.Observe(Chunk(1280, 0, start.AddMilliseconds(42080 + i * 80)), 1200) ?? laterFinal;
        Check(laterFinal is { Segment: 2, Final: true } && laterFinal.Samples.Take(1280).All(sample => sample == 1500 / 32768f),
            "another sentence is still accepted after an earlier final and quiet interval");

        foreach ((int pause, int quietChunks) in new[] { (1, 8), (600, 8), (1200, 15), (2400, 30), (10000, 30) })
        {
            var boundedPause = new FireRedAudioSegmenter();
            boundedPause.Observe(Chunk(1280, 1000, start), pause);
            FireRedDecodeJob? final = null;
            for (int i = 0; i < quietChunks; i++)
            {
                FireRedDecodeJob? result = boundedPause.Observe(Chunk(1280, 0, start.AddMilliseconds(80 * (i + 1))), pause);
                if (result?.Final == true) { Check(i == quietChunks - 1, "pause is bounded but does not finalize before the full selected silence"); final = result; }
            }
            Check(final is not null && final.Samples.Length == 1280 * (quietChunks + 1), "minimum/maximum pause boundaries preserve every sample: " + pause);
        }

        // A hard cut can occur inside a single input chunk because pre-roll
        // changes alignment. Concatenated finals must equal the exact input.
        var crossing = new FireRedAudioSegmenter();
        crossing.Observe(Chunk(16000, 0, start), 1200);
        var expected = new List<float>(Enumerable.Repeat(0f, 3840));
        FireRedDecodeJob? boundaryFinal = null, tailFinal = null;
        for (int i = 0; i < 8; i++)
        {
            short marker = (short)(1000 + i * 300);
            expected.AddRange(Enumerable.Repeat(marker / 32768f, 16000));
            if (crossing.Observe(Chunk(16000, marker, start.AddSeconds(i + 1)), 1200) is { Final: true } final) boundaryFinal = final;
        }
        expected.AddRange(Enumerable.Repeat(5000 / 32768f, 1280));
        crossing.Observe(Chunk(1280, 5000, start.AddSeconds(9)), 1200);
        for (int i = 0; i < 15; i++)
        {
            expected.AddRange(Enumerable.Repeat(0f, 1280));
            if (crossing.Observe(Chunk(1280, 0, start.AddMilliseconds(9080 + i * 80)), 1200) is { Final: true } final) tailFinal = final;
        }
        Check(boundaryFinal is not null && tailFinal is not null && boundaryFinal.Samples.Length == FireRedAudioSegmenter.MaxSamples &&
            tailFinal.Segment == boundaryFinal.Segment + 1, "a capture chunk crossing eight seconds retains its suffix in the next section");
        Check(boundaryFinal!.Samples.Concat(tailFinal!.Samples).SequenceEqual(expected), "hard-boundary audio is neither lost, duplicated nor reordered");
        Check(boundaryFinal.End == tailFinal.Start && boundaryFinal.End == start.AddMilliseconds(8760) && tailFinal.End == start.AddMilliseconds(10280),
            "cross-boundary section timestamps remain contiguous at exact sample offsets");

        var discontinuous = new FireRedAudioSegmenter();
        for (int i = 0; i < 3; i++) discontinuous.Observe(Chunk(1280, 1000, start.AddMilliseconds(i * 80)), 1200);
        DateTimeOffset resumedAt = start.AddSeconds(1);
        Check(discontinuous.TryFinishAtGap(resumedAt, out FireRedDecodeJob? beforeGap) &&
            beforeGap is { Final: true, Segment: 1, Revision: 1 } && beforeGap.Start == start &&
            beforeGap.End == start.AddMilliseconds(240) && beforeGap.Samples.Length == 3840 &&
            beforeGap.Samples.All(sample => sample == 1000 / 32768f),
            "a missing capture interval finalizes only real old PCM at its actual end, without stretching evidence across the gap");
        Check(!discontinuous.TryFinishAtGap(resumedAt, out FireRedDecodeJob? repeatedGap) && repeatedGap is null,
            "checking the same capture gap twice never duplicates the old final");
        Check(discontinuous.Observe(Chunk(1280, 3000, resumedAt), 1200) is null,
            "the first resumed PCM is admitted after flushing the old segment");
        FireRedDecodeJob? afterGap = null;
        for (int i = 0; i < 15; i++)
            if (discontinuous.Observe(Chunk(1280, 0, resumedAt.AddMilliseconds(80 + i * 80)), 1200) is { Final: true } final) afterGap = final;
        Check(afterGap is { Final: true, Segment: 2, Revision: 1 } && afterGap.Start == resumedAt &&
            afterGap.End == resumedAt.AddMilliseconds(1280) && afterGap.Samples.Length == 20480 &&
            afterGap.Samples.Take(1280).All(sample => sample == 3000 / 32768f) && afterGap.Samples.Skip(1280).All(sample => sample == 0),
            "resumed speech owns a fresh section and retains all new PCM without the prior person's audio or missing time");

        var gapLimit = new FireRedAudioSegmenter();
        gapLimit.Observe(Chunk(1280, 1000, start), 1200);
        Check(!gapLimit.TryFinishAtGap(start.AddMilliseconds(80), out FireRedDecodeJob? contiguousGap) && contiguousGap is null,
            "ordinary contiguous capture does not invent section boundaries");
        Check(!gapLimit.TryFinishAtGap(start.AddMilliseconds(240), out FireRedDecodeJob? allowedJitter) && allowedJitter is null,
            "the documented 160ms timestamp tolerance does not prematurely finalize capture");
        Check(gapLimit.TryFinishAtGap(start.AddMilliseconds(240).AddTicks(1), out FireRedDecodeJob? overLimit) &&
            overLimit is { Final: true } && overLimit.End == start.AddMilliseconds(80) && overLimit.Samples.Length == 1280,
            "one tick beyond the gap limit creates a boundary at the previous sample end");

        var backwards = new FireRedAudioSegmenter();
        backwards.Observe(Chunk(1280, 1500, start.AddSeconds(2)), 1200);
        Check(backwards.TryFinishAtGap(start.AddSeconds(1), out FireRedDecodeJob? beforeClockJump) &&
            beforeClockJump is { Final: true } && beforeClockJump.Start == start.AddSeconds(2) &&
            beforeClockJump.End == start.AddMilliseconds(2080) && beforeClockJump.Samples.Length == 1280,
            "a backwards capture-clock jump cannot combine unrelated audio into stretched voice evidence");

        var idleGap = new FireRedAudioSegmenter();
        Check(!idleGap.TryFinishAtGap(start, out FireRedDecodeJob? absentAudio) && absentAudio is null,
            "a gap check with no audio creates no synthetic final");
        for (int i = 0; i < 20; i++)
        {
            DateTimeOffset quietAt = start.AddMilliseconds(i * 80);
            Check(!idleGap.TryFinishAtGap(quietAt, out FireRedDecodeJob? quietGap) && quietGap is null &&
                idleGap.Observe(Chunk(1280, 0, quietAt), 1200) is null,
                "continuous quiet capture remains admitted without generating repeated empty gap sections");
        }

        FireRedDecodeJob Job(long segment, int revision, bool final) => new(segment, revision, new[] { .1f }, start, start.AddSeconds(1), final);
        var finalQueue = new FireRedDecodeQueue();
        finalQueue.Enqueue(Job(1, 1, false));
        Check(!finalQueue.TryRead(out _) && finalQueue.IsObsoletePreview(Job(1, 1, false)) && finalQueue.DroppedSections == 0,
            "the default decode queue cannot admit a speculative job from an older settings snapshot");
        finalQueue.SendEarlyPreviews = true; finalQueue.Enqueue(Job(1, 2, false));
        finalQueue.SendEarlyPreviews = false;
        finalQueue.Enqueue(Job(1, 3, true));
        Check(finalQueue.TryRead(out FireRedDecodeJob? finishedOnly) && finishedOnly is { Final: true, Revision: 3 } && !finalQueue.TryRead(out _),
            "opting out clears a queued draft while preserving the finished section");

        var queue = new FireRedDecodeQueue { SendEarlyPreviews = true };
        Check(!queue.TryRead(out FireRedDecodeJob? empty) && empty is null && queue.DroppedSections == 0, "an empty queue has no synthetic work");
        FireRedDecodeJob preview1 = Job(1, 1, false), preview2 = Job(1, 2, false);
        queue.Enqueue(preview1); queue.Enqueue(preview2);
        Check(queue.IsObsoletePreview(preview1) && !queue.IsObsoletePreview(preview2), "a newer draft invalidates only its earlier revision");
        Check(queue.TryRead(out FireRedDecodeJob? newest) && ReferenceEquals(newest, preview2) && !queue.TryRead(out _) && queue.DroppedSections == 0,
            "rapid drafts coalesce rather than building stale inference backlog");

        FireRedDecodeJob pending = Job(2, 1, false), committed = Job(1, 3, true);
        queue.Enqueue(pending); queue.Enqueue(committed);
        Check(queue.TryRead(out FireRedDecodeJob? first) && ReferenceEquals(first, committed) &&
            queue.TryRead(out FireRedDecodeJob? next) && ReferenceEquals(next, pending), "final sections are processed before previews without dropping the newer draft");
        FireRedDecodeJob ownDraft = Job(3, 1, false), ownFinal = Job(3, 2, true);
        queue.Enqueue(ownDraft); queue.Enqueue(ownFinal); queue.Enqueue(Job(3, 3, false));
        Check(queue.IsObsoletePreview(ownDraft) && !queue.IsObsoletePreview(ownFinal), "queued final supersedes in-flight drafts but never itself");
        Check(queue.TryRead(out FireRedDecodeJob? finalRead) && ReferenceEquals(finalRead, ownFinal) && !queue.TryRead(out _),
            "a draft cannot appear again after its final is already queued");

        var overloaded = new FireRedDecodeQueue { SendEarlyPreviews = true };
        for (int i = 1; i <= 6; i++) overloaded.Enqueue(Job(i, 1, true));
        FireRedDecodeJob future = Job(7, 1, false); overloaded.Enqueue(future);
        var retained = new List<FireRedDecodeJob>();
        while (overloaded.TryRead(out FireRedDecodeJob? job)) retained.Add(job!);
        Check(overloaded.DroppedSections == 3 && retained.Select(item => item.Segment).SequenceEqual(new long[] { 4, 5, 6, 7 }),
            "sustained overload retains the latest three finals and one draft, reporting every skipped old section");
        Check(retained.Take(3).All(item => item.Final) && !retained.Last().Final && !overloaded.TryRead(out _),
            "bounded overload recovery does not reorder retained finals or duplicate work");

        var inFlight = new FireRedDecodeQueue { SendEarlyPreviews = true };
        FireRedDecodeJob oldInFlight = Job(1, 1, false);
        inFlight.Enqueue(oldInFlight); inFlight.TryRead(out _);
        for (int i = 1; i <= 4; i++) inFlight.Enqueue(Job(i, 2, true));
        Check(inFlight.DroppedSections == 1 && inFlight.IsObsoletePreview(oldInFlight),
            "a final dropped during overload cannot resurrect its older in-flight draft");
        while (inFlight.TryRead(out _)) { }
        Check(inFlight.IsObsoletePreview(oldInFlight), "draining finals does not make already-finalized drafts current again");
        inFlight.Enqueue(oldInFlight);
        Check(!inFlight.TryRead(out _), "late drafts from finalized or skipped sections do not reopen old bubbles");
        return checks;
    }
}

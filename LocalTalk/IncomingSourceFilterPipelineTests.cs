using System.Net;
using System.Net.Http;
using System.Text;

namespace IncomingTranslator;

internal static class IncomingSourceFilterPipelineTests
{
    internal static Task<int> RunAsync(string directory) => IncomingTranslationService.RunSourceFilterPipelineChecksAsync(directory);
}

internal sealed partial class IncomingTranslationService
{
    internal static async Task<int> RunSourceFilterPipelineChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        int checks = 0, instance = 0;
        void Check(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException("Incoming Chinese-only pipeline: " + detail); checks++; }
        AppSettings Settings(bool enabled = true) => new()
        {
            IncomingChineseOnly = enabled, IncomingSourceLanguage = "Chinese", IncomingTargetLanguage = "English",
            IncomingSpeechEngine = "firered2-aed", UseAiSpeakerRecognition = false, IncomingSendEarlyPreviews = false
        };
        IncomingTranslationService Service(AppSettings settings, List<string> sources, List<TranslationDisplay> displays,
            SourceFilterTestHandler handler, List<(string Text, bool Warning)>? statuses = null)
        {
            string prefix = Path.Combine(directory, (++instance).ToString());
            return new(settings, (text, warning) => { statuses?.Add((text, warning)); return Task.CompletedTask; },
                text => { sources.Add(text); return Task.CompletedTask; },
                display => { displays.Add(display); return Task.CompletedTask; },
                new AiSpeakerIdentifier(prefix + "-identities.json", prefix + "-people.json"), translationHandler: handler);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const string english = "NICE TO SEE YOU. TAKE TOMORROW AND COME BACK.";
        const string chinese = "你好，今天怎么样？";
        const string fused = "SCREAMMMMMM HOWTOSEE呢小蜜蜂";

        // The common observer is the actual boundary used by Voxtral and native
        // recognition. Its raw cumulative context and audio proof must survive.
        var sources = new List<string>(); var output = new List<TranslationDisplay>();
        var statuses = new List<(string Text, bool Warning)>(); var handler = new SourceFilterTestHandler();
        using (var service = Service(Settings(), sources, output, handler, statuses))
        {
            service.InitializeNativeSubtitleSession();
            service._audioTimelineStart = now;
            service._sentAudioTimeline.Append(now, 32000);
            await service.ObserveNativeSubtitleUpdateAsync(new(english, 800, false, 800));
            DateTimeOffset? progress = service._lastMeaningfulAudioEnd;
            await service.FlushActiveAsync();
            Check(service._latest == english && service._subtitleSegments.ActiveText.Length == 0 && progress == now.AddMilliseconds(800),
                "excluded native text retains raw recognition and acknowledged audio, while its sentence watermark is consumed");
            Check(service._translations.Reader.Count == 0 && service._identificationJobs.Reader.Count == 0 &&
                service._evidenceBubbles.Count == 0 && service._nextUtteranceId == 1 && handler.Calls == 0,
                "English creates no subtitle bubble, evidence, fingerprint job or translation request");
            Check(sources.All(text => !text.Contains("NICE", StringComparison.Ordinal)) &&
                sources.Contains(SourceFilteredMessage) && statuses.Any(status => status.Text == SourceFilteredMessage && !status.Warning),
                "excluded raw previews are replaced by a healthy, neutral listening message");
            DateTimeOffset logAt = service._lastSourceFilterLogAt;
            await service.ObserveTranscriptAsync(english + " EXTRA ENGLISH.", now.AddMilliseconds(900));
            await service.FlushActiveAsync();
            Check(service._lastSourceFilterLogAt == logAt && !service.HealthSnapshot.IsWarning &&
                service._lastMeaningfulAudioEnd == now.AddMilliseconds(900),
                "repeated excluded speech stays acknowledged and rate-limits metadata diagnostics without a stall warning");
            await service.ObserveTranscriptAsync(english + " EXTRA ENGLISH. " + chinese, now.AddSeconds(1));
            await service.FlushActiveAsync();
            Check(service._translations.Reader.TryRead(out TranslationRequest accepted) && accepted.Source == chinese &&
                service._translations.Reader.Count == 0 && service._evidenceBubbles.Count == 1,
                "Chinese after consumed English becomes exactly one eligible request without replaying its prefix");
            await service.TranslateAndPublishAsync(accepted, CancellationToken.None);
            Check(handler.Calls == 1 && output.Count == 1 && output[0].Source == chinese,
                "accepted Chinese reaches the fake translator and publication through the real worker method");
            service.ResetTranscriptState();
            await service.ObserveTranscriptAsync(chinese, now.AddSeconds(2));
            await service.FlushActiveAsync();
            Check(service._translations.Reader.TryRead(out TranslationRequest afterReset) && afterReset.Source == chinese &&
                service._translations.Reader.Count == 0,
                "a session reset does not retain or replay previously excluded English");
        }

        // A single flush must keep draining after an excluded sentence, rather
        // than returning null and losing the valid suffix during session reset.
        sources = new(); output = new(); handler = new();
        using (var service = Service(Settings(), sources, output, handler))
        {
            await service.ObserveTranscriptAsync(english + " " + chinese, now);
            await service.FlushActiveAsync();
            Check(service._translations.Reader.TryRead(out TranslationRequest accepted) && accepted.Source == chinese &&
                service._translations.Reader.Count == 0 && service._subtitleSegments.ActiveText.Length == 0,
                "one final flush drains excluded English then retains the later Chinese sentence");
            service.ResetTranscriptState();
            await service.ObserveTranscriptAsync(fused, now.AddSeconds(1));
            await service.FlushActiveAsync();
            Check(service._translations.Reader.Count == 0 && handler.Calls == 0 &&
                sources[^1] == SourceFilteredMessage,
                "Latin-dominant recognition with incidental Han characters never leaks into translation or the live source");
        }

        // The same decoded-result method is used by FireRed itself. No recognizer
        // constructor, ONNX model, waveform capture or inference is invoked here.
        sources = new(); output = new(); handler = new();
        using (var service = Service(Settings(), sources, output, handler))
        {
            var bubbles = new Dictionary<long, long>();
            var job = new FireRedDecodeJob(1, 1, [], now, now.AddSeconds(1), true);
            await service.ObserveFireRedTranscriptAsync(job, english, bubbles, CancellationToken.None);
            Check(service._lastMeaningfulAudioEnd == job.End && service._lastTextAudioEnd == job.End &&
                service.HealthSnapshot.LastWordAt is not null && service._transcriptEvents == 1,
                "FireRed acknowledges excluded words and their PCM range as successful recognition");
            Check(service._translations.Reader.Count == 0 && service._evidenceBubbles.Count == 0 && bubbles.Count == 0 &&
                service._identificationJobs.Reader.Count == 0 && handler.Calls == 0 && sources.Single() == SourceFilteredMessage,
                "FireRed filters before voice evidence and translation admission");
            await service.ObserveFireRedTranscriptAsync(job with { Segment = 2 }, "我们用VRChat聊天。", bubbles, CancellationToken.None);
            Check(service._translations.Reader.TryRead(out TranslationRequest allowed) && allowed.Source == "我们用VRChat聊天。" &&
                service._translations.Reader.Count == 0 && bubbles.Count == 0,
                "normal Chinese containing a short product name still reaches translation unchanged");
        }

        sources = new(); output = new(); handler = new();
        AppSettings groups = Settings(); groups.IncomingSpeechEngine = "vibevoice"; groups.IncomingSourceLanguage = "Automatic";
        using (var service = Service(groups, sources, output, handler))
        {
            var chunk = new GroupTranscriptChunk("synthetic-cn-filter", 0, 0, 0, 2900, [new("0", english), new("1", chinese)]);
            await service.ObserveGroupChunkAsync(chunk, CancellationToken.None);
            await service.FlushGroupSentencesAsync(CancellationToken.None);
            Check(service._translations.Reader.TryRead(out TranslationRequest accepted) && accepted.Source == chinese &&
                service._translations.Reader.Count == 0 && service._evidenceBubbles.Count == 1,
                "Groups independently filters an English voice without losing another voice's Chinese");
            Check(service._groupSentences.Values.All(state => state.Segments.ActiveText.Length == 0) &&
                sources.All(source => !source.Contains("NICE", StringComparison.Ordinal)) && handler.Calls == 0,
                "Groups consumes excluded source watermarks and excludes raw English from visible previews");
            await service.ObserveGroupChunkAsync(chunk with { ChunkIndex = 1, AudioStartMilliseconds = 2900,
                AudioEndMilliseconds = 5800, Segments = [new("0", chinese)] }, CancellationToken.None);
            await service.FlushGroupSentencesAsync(CancellationToken.None);
            Check(service._translations.Reader.TryRead(out TranslationRequest next) && next.Source == chinese &&
                service._translations.Reader.Count == 0,
                "a previously excluded group voice can later speak Chinese without replaying its English");
            await service.ObserveGroupChunkAsync(chunk with { ChunkIndex = 2, AudioStartMilliseconds = 5800,
                AudioEndMilliseconds = 8700, Segments = [new("0", english)] }, CancellationToken.None);
            Check(service.HealthSnapshot.Stage == "source-filtered" && !service.HealthSnapshot.IsWarning && sources[^1] == SourceFilteredMessage,
                "Groups keeps the intentional-filter status instead of immediately overwriting it with generic listening");
        }

        sources = new(); output = new(); handler = new();
        using (var service = Service(Settings(), sources, output, handler))
        {
            var speaker = new SpeakerAttribution("Voice", 0, false, 0);
            TranslationRequest excluded = service.CreateTranslationRequest(english, speaker, now, now, 30);
            await service.QueueTranslationAsync(excluded, CancellationToken.None);
            await service.TranslateAndPublishAsync(excluded, CancellationToken.None);
            Check(service._translations.Reader.Count == 0 && handler.Calls == 0 && output.Count == 0,
                "defensive queue and worker gates reject directly supplied excluded requests before HTTP");

            AppSettings previews = Settings(); previews.IncomingSendEarlyPreviews = true; service.UpdateSettings(previews);
            TranslationRequest older = service.CreateTranslationRequest(chinese, speaker, now, now, 31) with { IsCommitted = false };
            using var active = new CancellationTokenSource();
            service._activeTranslation = older; service._activeTranslationCancellation = active;
            await service.AdmitIncomingSourceAsync(english, 31, now);
            Check(active.IsCancellationRequested && service.IsExcludedSourceRevision(older),
                "an excluded same-bubble correction cancels its earlier Chinese draft even when audio timestamps are equal");
            await service.QueueTranslationAsync(older, CancellationToken.None);
            TranslationRequest corrected = service.CreateTranslationRequest(chinese, speaker, now, now, 31);
            await service.QueueTranslationAsync(corrected, CancellationToken.None);
            Check(service._translations.Reader.TryRead(out TranslationRequest latest) && latest == corrected &&
                service._translations.Reader.Count == 0,
                "a later accepted correction at the same timestamp receives a fresh revision and is not lost");
            service._activeTranslation = null; service._activeTranslationCancellation = null;
        }

        // Pending work is filtered permanently once the restriction is enabled;
        // disabling it later permits newly captured English, not old queued text.
        sources = new(); output = new(); handler = new();
        using (var service = Service(Settings(false), sources, output, handler))
        {
            var speaker = new SpeakerAttribution("Voice", 0, false, 0);
            TranslationRequest queued = service.CreateTranslationRequest(english, speaker, now, now, 40);
            await service.QueueTranslationAsync(queued, CancellationToken.None);
            service.UpdateSettings(Settings()); service.UpdateSettings(Settings(false));
            Check(service._translations.Reader.TryRead(out TranslationRequest pending), "the fixture retained an existing queued request");
            await service.TranslateAndPublishAsync(pending, CancellationToken.None);
            Check(handler.Calls == 0 && output.Count == 0,
                "a pre-enable queued English request cannot replay after a quick enable/disable");
            TranslationRequest fresh = service.CreateTranslationRequest(english, speaker, now, now.AddSeconds(1), 41);
            await service.TranslateAndPublishAsync(fresh, CancellationToken.None);
            Check(handler.Calls == 1 && output.Count == 1 && output[0].Source == english,
                "new English after disable is unrestricted without restarting or changing a model");
        }

        sources = new(); output = new(); handler = new() { Block = true };
        using (var service = Service(Settings(false), sources, output, handler))
        using (var active = new CancellationTokenSource())
        {
            var speaker = new SpeakerAttribution("Voice", 0, false, 0);
            TranslationRequest request = service.CreateTranslationRequest(english, speaker, now, now, 50);
            service._activeTranslation = request; service._activeTranslationCancellation = active;
            Task translating = service.TranslateAndPublishAsync(request, active.Token);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            service.UpdateSettings(Settings());
            handler.Release.TrySetResult();
            await translating.WaitAsync(TimeSpan.FromSeconds(3));
            Check(active.IsCancellationRequested && output.Count == 0 && service._speakerContext.Count == 0 &&
                !service.HealthSnapshot.IsWarning,
                "enabling restriction cancels in-flight English and prevents publication or translation-context pollution");
            service._activeTranslation = null; service._activeTranslationCancellation = null;
        }

        sources = new(); output = new(); handler = new();
        using (var service = Service(Settings(false), sources, output, handler))
        {
            var speaker = new SpeakerAttribution("Voice", 0, false, 0);
            TranslationRequest request = service.CreateTranslationRequest(english, speaker, now, now, 60);
            await service._displayPublicationGate.WaitAsync();
            Task<bool> publishing = service.PublishSourceFilteredDisplayAsync(new("Voice", english, "Synthetic subtitle.", 60, now), request);
            service.UpdateSettings(Settings());
            service._displayPublicationGate.Release();
            Check(!await publishing.WaitAsync(TimeSpan.FromSeconds(3)) && output.Count == 0,
                "the publication lease rechecks a policy changed while a completed translation waited for the UI");
        }
        return checks;
    }

    private sealed class SourceFilterTestHandler : HttpMessageHandler
    {
        internal int Calls;
        internal bool Block;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            // Deliberately finish even after cancellation, like an already completed
            // local server response. Publication must still enforce the new policy.
            if (Block) await Release.Task;
            return new(HttpStatusCode.OK) { Content = new StringContent(
                """{"choices":[{"message":{"content":"Synthetic subtitle."}}]}""", Encoding.UTF8, "application/json") };
        }
    }
}

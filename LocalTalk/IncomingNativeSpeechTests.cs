using System.Text;
using System.Text.Json;

namespace IncomingTranslator;

internal static class IncomingNativeSpeechTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        int checks = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("Incoming native speech: " + label); checks++; }
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, label);
        }
        JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        var chinese = new AppSettings { IncomingSpeechEngine = "nemotron", IncomingSourceLanguage = "Chinese",
            OwnRecognitionEngine = "voxtral", OwnVoiceSourceLanguage = "Automatic", UseAiSpeakerRecognition = false };
        AppSettings mapped = IncomingRecognitionCatalog.CreateNativeSettings(chinese);
        Check(mapped.OwnRecognitionEngine == "nemotron" && mapped.OwnVoiceSourceLanguage == "Chinese" &&
            chinese.OwnRecognitionEngine == "voxtral" && chinese.OwnVoiceSourceLanguage == "Automatic",
            "native preparation copies the subtitle selection without changing the microphone recognizer");
        using (JsonDocument configuration = JsonDocument.Parse(JsonSerializer.Serialize(IncomingTranslationService.NativeSubtitleConfiguration("Chinese"))))
        {
            Check(configuration.RootElement.GetProperty("type").GetString() == "session.update" &&
                configuration.RootElement.GetProperty("session").GetProperty("language").GetString() == "zh-CN",
                "explicit subtitle source reaches the NeMo session configuration");
            Check(!configuration.RootElement.GetProperty("session").GetProperty("speaker_diarization").GetBoolean(),
                "normal native recognition does not misrepresent NeMo as overlap separation");
        }
        using (JsonDocument configuration = JsonDocument.Parse(JsonSerializer.Serialize(IncomingTranslationService.NativeSubtitleConfiguration("Automatic"))))
            Check(configuration.RootElement.GetProperty("session").GetProperty("language").GetString() == "auto",
                "automatic source remains an actual automatic recognizer setting");
        var handshake = new Queue<JsonElement>([Json("""{"type":"session.created"}"""), Json("""{"type":"session.updated"}""")]);
        byte[]? sentConfiguration = null;
        await IncomingTranslationService.ConfigureNativeSubtitleSessionAsync(
            _ => Task.FromResult(handshake.Dequeue()), (bytes, _) => { sentConfiguration = bytes.ToArray(); return Task.CompletedTask; },
            "Chinese", CancellationToken.None);
        Check(handshake.Count == 0 && sentConfiguration is not null &&
            Json(Encoding.UTF8.GetString(sentConfiguration)).GetProperty("session").GetProperty("language").GetString() == "zh-CN",
            "the actual handshake state machine waits for created, sends the chosen language, and requires updated");
        foreach (string unexpected in new[] { "error", "session.created", "input_audio_buffer.committed" })
        {
            handshake = new Queue<JsonElement>([Json("""{"type":"session.created"}"""), Json(JsonSerializer.Serialize(new { type = unexpected }))]);
            bool rejected = false;
            try
            {
                await IncomingTranslationService.ConfigureNativeSubtitleSessionAsync(_ => Task.FromResult(handshake.Dequeue()),
                    (_, _) => Task.CompletedTask, "English", CancellationToken.None);
            }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "an unexpected " + unexpected + " event cannot acknowledge native configuration");
        }
        var transcript = new NativeSubtitleTranscript();
        NativeSubtitleUpdate delta = transcript.Observe(Json("""{"type":"conversation.item.input_audio_transcription.delta","delta":"你好","audio_processed":0.4}"""), 800, false);
        Check(delta.Text == "你好" && delta.AudioEndMilliseconds == 400 && !delta.Completed,
            "partial words use acknowledged audio time and remain speculative");
        NativeSubtitleUpdate empty = transcript.Observe(Json("""{"type":"conversation.item.input_audio_transcription.delta","delta":"","audio_processed":0.5}"""), 900, false);
        Check(empty.Text is null && !empty.Completed, "empty processing updates cannot fabricate words or finalize speech");
        NativeSubtitleUpdate final = transcript.Observe(Json("""{"type":"conversation.item.input_audio_transcription.completed","transcript":"你好吗？","audio_processed":0.45}"""), 900, false);
        Check(final.Text == "你好吗？" && final.AudioEndMilliseconds == 500 && !final.Completed,
            "authoritative completed text replaces partial guesses and timestamps never move backward");
        delta = transcript.Observe(Json("""{"type":"conversation.item.input_audio_transcription.delta","delta":"再见","audio_processed":99999}"""), 1200, false);
        Check(delta.Text == "你好吗？再见" && delta.AudioEndMilliseconds == 1200,
            "later native turns accumulate while reported positions are clamped to sent PCM");
        Reject(() => transcript.Observe(Json("""{"type":"input_audio_buffer.committed"}"""), 1200, false),
            "unsolicited commit acknowledgements cannot release a session");
        Reject(() => transcript.Observe(Json("""{"type":"input_audio_buffer.committed"}"""), 1200, true),
            "a final from before commit cannot stand in for the requested final");
        final = transcript.Observe(Json("""{"type":"conversation.item.input_audio_transcription.completed","transcript":"再见。"}"""), 1200, true);
        NativeSubtitleUpdate complete = transcript.Observe(Json("""{"type":"input_audio_buffer.committed"}"""), 1200, true);
        Check(final.Text == "你好吗？再见。" && complete.Completed && complete.Text == final.Text,
            "verified commit completion retains the authoritative final transcript");
        Reject(() => new NativeSubtitleTranscript().Observe(Json("[]"), 0, false), "non-object events are rejected");
        Reject(() => new NativeSubtitleTranscript().Observe(Json("""{"type":"conversation.item.input_audio_transcription.delta","delta":7}"""), 0, false),
            "malformed delta types are rejected");
        Reject(() => new NativeSubtitleTranscript().Observe(Json("""{"type":"session.created"}"""), 0, false),
            "unexpected session resets cannot erase context invisibly");
        Reject(() => new NativeSubtitleTranscript().Observe(Json(JsonSerializer.Serialize(new
            { type = "conversation.item.input_audio_transcription.delta", delta = new string('a', 16385) })), 0, false),
            "text accumulation remains bounded");

        var english = chinese.Copy(); english.IncomingSourceLanguage = "English";
        var plan = RuntimeController.BuildPlan(chinese, incoming: true);
        Check(RuntimeProfiles.SameIncomingSpeech(chinese, english) && !RuntimeProfiles.SameIncomingCapture(chinese, english) &&
            RuntimeProfiles.CanReuseIncomingModels(chinese, english, plan, plan) && !RuntimeProfiles.CanApplyLive(chinese, english, plan, plan),
            "source-language changes recreate text/audio context while keeping resident models");
        var fire = chinese.Copy(); fire.IncomingSpeechEngine = "firered2-ctc";
        Check(!RuntimeProfiles.SameIncomingSpeech(chinese, fire) && !RuntimeProfiles.CanReuseIncomingModels(chinese, fire, plan, plan),
            "a different speech engine requires a controlled subtitle-only lifecycle switch");

        foreach (string engine in new[] { "nemotron", "firered2-ctc", "firered2-aed" })
        {
            AppSettings selected = chinese.Copy(); selected.IncomingSpeechEngine = engine;
            selected.FirstRunSetupCompleted = true; selected.OwnManagedModelId = "unused-own-invalid";
            selected.ShareTranslationModel = false;
            var platform = new IncomingNativeSetupFixture();
            var setup = new AutomaticSetupService(platform, Path.Combine(directory, engine), incomingVoiceOnly: true);
            AutomaticSetupPlan proposed = await setup.PlanAsync(selected);
            Check(proposed.DownloadBytes == 123 && proposed.SpeechSetupRequired && !proposed.RequiresAdministrator && !proposed.RestartMayBeNeeded,
                engine + " setup plans only selected native speech files without Linux prerequisites");
            AutomaticSetupResult result = await setup.RunAsync(selected, explicitConsent: true);
            Check(result.Completed && platform.Prepared == engine && platform.LinuxCalls == 0 &&
                selected.OwnRecognitionEngine == "voxtral" && selected.OwnManagedModelId == "unused-own-invalid" &&
                selected.OwnVoiceSourceLanguage == "Automatic", engine + " preparation is scoped to subtitles and preserves microphone selections");
        }
        Console.WriteLine($"PASS: {checks} incoming native speech protocol/runtime/setup checks; synthetic text and fixture files only.");
        return checks;
    }

    private sealed class IncomingNativeSetupFixture : IAutomaticSetupPlatform
    {
        public string? Prepared;
        public int LinuxCalls;
        public Task<AutomaticSetupSnapshot> InspectAsync(AppSettings settings, CancellationToken token) => Task.FromResult(new AutomaticSetupSnapshot(
            LocalTalkProvisioner.ModelCatalog.Select(choice => choice.Id).ToHashSet(),
            LocalTalkProvisioner.RuntimeCatalog.Select(choice => choice.Id).ToHashSet(), new HashSet<string>(), null, true, true, true));
        public long PendingIncomingNativeSpeechBytes(AppSettings settings) => 123;
        public bool IsIncomingNativeSpeechReady(AppSettings settings) => Prepared == settings.IncomingSpeechEngine;
        public Task EnsureIncomingNativeSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Prepared = settings.IncomingSpeechEngine; return Task.CompletedTask; }
        public Task<SetupPrerequisiteResult> EnsureNativeRuntimeAsync(IProgress<ProvisionProgress>? progress, CancellationToken token) =>
            Task.FromResult(SetupPrerequisiteResult.Ready);
        public Task<SetupPrerequisiteResult> EnsureWindowsSpeechAsync(IProgress<ProvisionProgress>? progress, CancellationToken token)
        { LinuxCalls++; throw new InvalidOperationException("Native subtitle setup must not install Linux."); }
        public Task EnsureDistributionAsync(string name, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { LinuxCalls++; throw new InvalidOperationException("Native subtitle setup must not create a distribution."); }
        public Task ValidateSpeechAsync(WslSpeechRegistration speech, CancellationToken token)
        { LinuxCalls++; throw new InvalidOperationException("Native subtitle setup must not inspect WSL."); }
        public Task BuildSpeechAsync(string distribution, string backend, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { LinuxCalls++; throw new InvalidOperationException("Native subtitle setup must not build a Linux server."); }
        public Task EnsureModelAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.CompletedTask;
        public Task EnsureRuntimeAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.CompletedTask;
        public Task<string> EnsureSpeakerModelAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token) =>
            Task.FromResult(settings.SpeakerModelPath);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace IncomingTranslator;

/// <summary>Isolated settings/policy fixtures only: no audio, network, models or installed state.</summary>
internal static class TimingDefaultsTests
{
    public static async Task<int> RunAsync(string directory)
    {
        int checks = 0;
        void Check(bool condition, string description)
        { if (!condition) throw new InvalidOperationException("Timing/introduction check failed: " + description); checks++; }
        bool Fast(AppSettings settings) => settings.OwnVoiceEndPauseMilliseconds == 1200 && settings.IncomingSettleMilliseconds == 1200;
        var defaults = new AppSettings();
        Check(Fast(defaults) && Fast(defaults.Copy()), "factory defaults and snapshots use 1.2 seconds in both directions");
        Check(Fast(SettingsStore.ReadSettings("{}")), "missing timing preferences use the current factory defaults");
        Check(Fast(SettingsStore.ReadSettings(JsonSerializer.Serialize(defaults))), "saved 1.2-second defaults survive load unchanged");
        Check(SettingsStore.ReadSettings("{\"IncomingSettleMilliseconds\":1300}").IncomingSettleMilliseconds == 1300,
            "an explicit old incoming value is preserved because its edit history is unknown");
        Check(SettingsStore.ReadSettings("{\"IncomingSettleMilliseconds\":1700,\"OwnVoiceEndPauseMilliseconds\":1800}") is
            { IncomingSettleMilliseconds: 1700, OwnVoiceEndPauseMilliseconds: 1800 }, "custom pauses in both directions survive upgrades");
        Check(SettingsStore.ReadSettings("{\"OwnVoiceEndPauseMilliseconds\":2400}").OwnVoiceEndPauseMilliseconds == 1200,
            "existing narrowly identified legacy delivery default migrates as before");
        Check(SettingsStore.ReadSettings("{\"OwnVoiceEndPauseMilliseconds\":2400,\"OwnVoiceSendEarlyPreviews\":false}") is
            { OwnVoiceEndPauseMilliseconds: 2400, OwnVoiceSendEarlyPreviews: false }, "a saved explicit delivery choice keeps the longer pause");
        Check(!defaults.FirstRunIntroductionCompleted && !defaults.Copy().FirstRunIntroductionCompleted &&
            !SettingsStore.ReadSettings("{}").FirstRunIntroductionCompleted, "new users and uncompleted setups get the introduction");
        Check(SettingsStore.ReadSettings("{\"FirstRunSetupCompleted\":true}").FirstRunIntroductionCompleted,
            "an existing completed installation does not get a new first-run popup");
        Check(!SettingsStore.ReadSettings("{\"FirstRunSetupCompleted\":true,\"FirstRunIntroductionCompleted\":false}").FirstRunIntroductionCompleted,
            "an explicit introduction reset is not overridden by upgrade inference");
        var introduced = new AppSettings { FirstRunIntroductionCompleted = true };
        Check(introduced.Copy().FirstRunIntroductionCompleted && SettingsStore.ReadSettings(JsonSerializer.Serialize(introduced)).FirstRunIntroductionCompleted,
            "introduction acknowledgement survives snapshots and serialization");

        // The same setting reaches both real incoming timing consumers. These
        // fixed-time PCM/transcript fixtures exercise them without an audio device.
        DateTimeOffset start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        var audio = new IncomingUtteranceAudioBuffer();
        byte[] frame = new byte[2560]; // 80 ms of synthetic PCM; explicit test activity.
        audio.Observe(frame, start, PcmVoiceActivity.Speech, defaults.IncomingSettleMilliseconds);
        Check(audio.TryTake(out IncomingAudioUtterance? turn), "synthetic incoming voice opens a bounded turn");
        for (int index = 1; index <= 14; index++) audio.Observe(frame, start.AddMilliseconds(index * 80), PcmVoiceActivity.Silence, defaults.IncomingSettleMilliseconds);
        Check(!turn!.Complete, "1120 ms of real PCM quiet does not finish a 1200 ms incoming turn");
        audio.Observe(frame, start.AddMilliseconds(15 * 80), PcmVoiceActivity.Speech, defaults.IncomingSettleMilliseconds);
        for (int index = 16; index <= 29; index++) audio.Observe(frame, start.AddMilliseconds(index * 80), PcmVoiceActivity.Silence, defaults.IncomingSettleMilliseconds);
        Check(!turn.Complete, "fresh incoming speech resets the 1.2-second quiet boundary");
        audio.Observe(frame, start.AddMilliseconds(30 * 80), PcmVoiceActivity.Silence, defaults.IncomingSettleMilliseconds);
        Check(turn.Complete && turn.EndReason == "speech pause", "exactly 1200 ms of consecutive PCM quiet finishes the incoming turn");
        var subtitles = new SubtitleSegmentation(); subtitles.Observe("测试", start);
        Check(!subtitles.TryTakeReady(start.AddMilliseconds(1199), false, TimeSpan.FromMilliseconds(defaults.IncomingSettleMilliseconds), out _),
            "unpunctuated short subtitle does not commit before its 1.2-second settle boundary");
        Check(subtitles.TryTakeReady(start.AddMilliseconds(1200), false, TimeSpan.FromMilliseconds(defaults.IncomingSettleMilliseconds), out SubtitleSegment segment) &&
            segment.IsCommitted && segment.Text == "测试", "subtitle settle commits at the same default 1.2-second boundary");

        var gpu = new LocalGpuProfile("timing-test-gpu", "Synthetic GPU", 0x10de, 32768);
        var hardware = new LocalHardwareProfile("Synthetic CPU", 16, 65536, 50000, [gpu], []);
        Check(new LocalAdvisorOptions().SpeechQuietMilliseconds == 1200 && HardwareAdvisor.Recommend(hardware).Settings.SpeechQuietMilliseconds == 1200,
            "hardware recommendation defaults no longer advertise a stale 2.4-second pause");
        var customOptions = new LocalAdvisorOptions { SpeechQuietMilliseconds = 1800 };
        Check(HardwareAdvisor.Recommend(hardware, options: customOptions).Settings.SpeechQuietMilliseconds == 1800 &&
            HardwareAdvisor.ConfigurationKey(new(), gpu.Id) != HardwareAdvisor.ConfigurationKey(customOptions, gpu.Id),
            "explicit advisor timing is preserved and changes the measurement configuration identity");

        string fixtureDirectory = Path.Combine(Path.GetFullPath(directory), "timing-defaults");
        Directory.CreateDirectory(fixtureDirectory);
        foreach (bool shared in new[] { true, false })
        {
            var settings = new AppSettings { ShareTranslationModel = shared, IncomingManagedModelId = "hy-mt2-7b-q4_k_m" };
            var service = new AutomaticSetupService(new ReadyPlatform(), Path.Combine(fixtureDirectory, shared ? "shared-setup" : "separate-setup"));
            AutomaticSetupPlan plan = await service.PlanAsync(settings);
            AutomaticSetupResult result = await service.RunAsync(settings, explicitConsent: true);
            Check(result.Completed && !result.NeedsRestart && plan.DownloadBytes == 0 && Fast(settings),
                "mock automatic setup preserves fast defaults for shared and separate translators");
            Check(!settings.FirstRunIntroductionCompleted && !settings.FirstRunSetupCompleted,
                "tool preparation does not acknowledge the introduction or claim listening has started");
        }
        var customized = new AppSettings { OwnVoiceEndPauseMilliseconds = 2700, IncomingSettleMilliseconds = 1700, FirstRunIntroductionCompleted = true };
        await new AutomaticSetupService(new ReadyPlatform(), Path.Combine(fixtureDirectory, "custom-setup")).RunAsync(customized, true);
        Check(customized is { OwnVoiceEndPauseMilliseconds: 2700, IncomingSettleMilliseconds: 1700, FirstRunIntroductionCompleted: true },
            "mock setup never overwrites customized timing or introduction acknowledgement");

        // DesktopTests must have selected its isolated data root before this test.
        // Refuse to write if a caller accidentally points at live app data.
        Check(LocalTalkProvisioner.IsWithin(AppPaths.SettingsPath, Path.GetFullPath(directory)), "preference tests are inside the mandatory isolated root");
        string settingsBefore = File.ReadAllText(AppPaths.SettingsPath);
        var configsBefore = new[] { AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath }.ToDictionary(path => path, File.ReadAllText);
        try
        {
            var custom = new AppSettings { OwnVoiceEndPauseMilliseconds = 2100, IncomingSettleMilliseconds = 1700,
                OwnVoiceTargetLanguage = "Korean", IncomingTargetLanguage = "Japanese", UiLanguage = "ko" };
            SettingsStore.Save(custom);
            var customDocument = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
            customDocument["FutureSetting"] = new JsonObject { ["Keep"] = "untouched" };
            File.WriteAllText(AppPaths.SettingsPath, customDocument.ToJsonString());
            var unchangedConfigs = configsBefore.Keys.ToDictionary(path => path, File.ReadAllText);
            using (var heldFast = File.Open(AppPaths.FastConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var heldSentence = File.Open(AppPaths.FoxTransConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                SettingsStore.SaveFirstRunIntroductionPreference(true);
            var afterIntroduction = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
            Check(afterIntroduction[nameof(AppSettings.FirstRunIntroductionCompleted)]!.GetValue<bool>(), "targeted acknowledgement saves only the introduction flag");
            Check(afterIntroduction["FutureSetting"]?["Keep"]?.GetValue<string>() == "untouched" &&
                afterIntroduction[nameof(AppSettings.OwnVoiceEndPauseMilliseconds)]!.GetValue<int>() == 2100 &&
                afterIntroduction[nameof(AppSettings.IncomingSettleMilliseconds)]!.GetValue<int>() == 1700 &&
                afterIntroduction[nameof(AppSettings.OwnVoiceTargetLanguage)]!.GetValue<string>() == "Korean",
                "targeted save preserves unknown fields, custom timings and translation choices");
            Check(unchangedConfigs.All(pair => File.ReadAllText(pair.Key) == pair.Value), "acknowledging the intro never rewrites capture/provider configuration");
            await Task.WhenAll(Task.Run(() => SettingsStore.SaveFirstRunIntroductionPreference(true)),
                Task.Run(() => SettingsStore.SaveSubtitlePinPreference(false)), Task.Run(() => SettingsStore.SaveUiLanguagePreference("ja")));
            AppSettings merged = SettingsStore.Load();
            Check(merged.FirstRunIntroductionCompleted && !merged.SubtitlesAlwaysOnTop && merged.UiLanguage == "ja" &&
                merged.OwnVoiceEndPauseMilliseconds == 2100 && merged.IncomingSettleMilliseconds == 1700,
                "concurrent targeted UI preferences merge latest data rather than losing other changes");
            custom.FirstRunIntroductionCompleted = false; // Simulate a stale dashboard snapshot.
            SettingsStore.Save(custom);
            Check(custom.FirstRunIntroductionCompleted && SettingsStore.Load().FirstRunIntroductionCompleted,
                "later full settings saves cannot undo a newer introduction acknowledgement");
            SettingsStore.SaveFirstRunIntroductionPreference(false);
            Check(!SettingsStore.Load().FirstRunIntroductionCompleted, "explicit targeted reset can show the introduction again");
            Check(!Directory.EnumerateFiles(AppPaths.Root, "vrchat-translation-settings.json.tmp-*").Any(), "atomic targeted preference writes leave no temporary files");
            const string malformed = "{not-json";
            File.WriteAllText(AppPaths.SettingsPath, malformed);
            bool rejected = false;
            try { SettingsStore.SaveFirstRunIntroductionPreference(true); } catch (JsonException) { rejected = true; }
            Check(rejected && File.ReadAllText(AppPaths.SettingsPath) == malformed,
                "targeted acknowledgement does not destructively replace malformed settings");
        }
        finally
        {
            File.WriteAllText(AppPaths.SettingsPath, settingsBefore);
            foreach (var pair in configsBefore) File.WriteAllText(pair.Key, pair.Value);
        }
        Console.WriteLine($"PASS: {checks} timing defaults and introduction persistence checks; fake setup only, no models, audio, network or live settings used.");
        return checks;
    }

    private sealed class ReadyPlatform : IAutomaticSetupPlatform
    {
        public Task<AutomaticSetupSnapshot> InspectAsync(AppSettings settings, CancellationToken token) => Task.FromResult(new AutomaticSetupSnapshot(
            LocalTalkProvisioner.ModelCatalog.Select(model => model.Id).ToHashSet(), LocalTalkProvisioner.RuntimeCatalog.Select(runtime => runtime.Id).ToHashSet(),
            new HashSet<string> { "Offline-Test-Ubuntu" }, new("Offline-Test-Ubuntu", "/inert/server", "/inert/model.gguf", "cpu"), true, true, false));
        public Task<SetupPrerequisiteResult> EnsureNativeRuntimeAsync(IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.FromResult(SetupPrerequisiteResult.Ready);
        public Task<SetupPrerequisiteResult> EnsureWindowsSpeechAsync(IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.FromResult(SetupPrerequisiteResult.Ready);
        public Task EnsureDistributionAsync(string name, IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.CompletedTask;
        public Task EnsureModelAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.CompletedTask;
        public Task EnsureRuntimeAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.CompletedTask;
        public Task<string> EnsureSpeakerModelAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.FromResult(settings.SpeakerModelPath);
        public Task ValidateSpeechAsync(WslSpeechRegistration speech, CancellationToken token) => Task.CompletedTask;
        public Task BuildSpeechAsync(string distribution, string backend, IProgress<ProvisionProgress>? progress, CancellationToken token) => Task.CompletedTask;
    }
}

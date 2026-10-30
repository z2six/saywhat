using System.Text.Json;

namespace IncomingTranslator;

// All platform work below is fake: no Windows servicing, registry, WSL, network,
// inference, audio, GPU, or real model files are accessed by these checks.
internal static class AutomaticSetupOfflineTests
{
    public static async Task<int> RunAsync(CancellationToken token = default) =>
        await Task.Run(() => RunCoreAsync(token), token).ConfigureAwait(false);

    private static async Task<int> RunCoreAsync(CancellationToken token)
    {
        int checks = 0;
        string testRoot = Path.Combine(Path.GetTempPath(), "LocalTalk-AutomaticSetupTests-" + Guid.NewGuid().ToString("N"));
        string Data(string name) => Path.Combine(testRoot, name);
        void Check(bool value, string message)
        {
            token.ThrowIfCancellationRequested();
            if (!value) throw new InvalidOperationException("Automatic setup test failed: " + message);
            checks++;
        }
        async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
        {
            bool rejected = false;
            try { await action().ConfigureAwait(false); } catch (T) { rejected = true; }
            Check(rejected, message);
        }
        AppSettings Settings() => new()
        {
            UseManagedTranslator = false, UseAiSpeakerRecognition = false,
            IncomingSpeechEngine = "voxtral", IncomingNormalSpeechEngine = "voxtral",
            OwnManagedModelId = "hy-mt2-7b-q4_k_m", IncomingManagedModelId = "hy-mt2-1.8b-q4_k_m",
            ShareTranslationModel = true, OwnVoiceTargetLanguage = "Chinese", IncomingTargetLanguage = "English",
            OwnVoiceEndPauseMilliseconds = 2700, IncomingSettleMilliseconds = 1700,
            RecognitionDelayMilliseconds = 480, SpeakerModelPath = "synthetic-original-speaker.onnx"
        };
        WslSpeechRegistration existing = new("User-Ubuntu", "/synthetic/existing/voxtral-server", "/synthetic/realtime.gguf", "cuda");
        var ready = new FakePlatform(existing);
        var consentService = new AutomaticSetupService(ready, Data("consent"));
        Check(!Directory.Exists(testRoot), "construction does not create setup state");
        await RejectAsync<InvalidOperationException>(async () =>
        { await consentService.RunAsync(Settings(), false, token: token).ConfigureAwait(false); }, "consent is required before inspection or writes").ConfigureAwait(false);
        Check(ready.Calls.Count == 0 && !Directory.Exists(testRoot), "refused consent performs no platform inspection, work, or checkpoint write");
        AppSettings invalid = Settings(); invalid.OwnManagedModelId = "voxtral-realtime-q4_k_m";
        await RejectAsync<InvalidOperationException>(async () =>
        { await consentService.RunAsync(invalid, true, token: token).ConfigureAwait(false); }, "speech models cannot be used as a translation selection").ConfigureAwait(false);
        Check(ready.Calls.Count == 0 && !Directory.Exists(testRoot), "invalid selections fail before setup state is created");

        AppSettings shared = Settings();
        AutomaticSetupPlan plan = await consentService.PlanAsync(shared, token).ConfigureAwait(false);
        Check(plan.DownloadBytes == ModelBytes(shared.OwnManagedModelId), "shared setup counts only the selected 7B model, not an ignored incoming selection");
        Check(!plan.RequiresAdministrator && !plan.RestartMayBeNeeded && !Directory.Exists(testRoot), "existing-ready planning is read-only and does not request elevation or reboot");
        Check(shared.OwnManagedModelId == "hy-mt2-7b-q4_k_m" && shared.IncomingManagedModelId == "hy-mt2-1.8b-q4_k_m" && shared.OwnVoiceTargetLanguage == "Chinese",
            "planning preserves the current model and language choices");
        AppSettings separate = Settings(); separate.ShareTranslationModel = false;
        Check((await consentService.PlanAsync(separate, token).ConfigureAwait(false)).DownloadBytes == ModelBytes(separate.OwnManagedModelId) + ModelBytes(separate.IncomingManagedModelId),
            "separate translation selections count both independent model downloads");
        separate.IncomingManagedModelId = separate.OwnManagedModelId;
        Check(AutomaticSetupService.TranslationIds(separate).Length == 1, "identical separate selections do not duplicate the same file download");

        var reused = new FakePlatform(existing);
        reused.Models.UnionWith([shared.OwnManagedModelId, "voxtral-realtime-q4_k_m"]);
        var reusedProgress = new List<ProvisionProgress>();
        AutomaticSetupResult reusedResult = await new AutomaticSetupService(reused, Data("reuse")).RunAsync(shared, true,
            new InlineProgress(reusedProgress.Add), token).ConfigureAwait(false);
        Check(reusedResult.Completed && !reusedResult.NeedsRestart, "existing validated engines can finish guided setup");
        Check(reused.Calls.SequenceEqual(new[] { "Inspect", "Model:" + shared.OwnManagedModelId, "Runtime:llama-vulkan", "ValidateSpeech" }),
            "existing setup reuses assets, validates imported speech, and never creates or rebuilds a distro");
        Check(reused.DownloadedModels.Count == 0 && reused.CreatedDistributions.Count == 0 && reused.Builds.Count == 0, "existing reuse causes no duplicate downloads or Linux builds");
        Check(reused.ValidatedSpeech == existing, "the exact current imported speech registration is validated, not replaced");
        Check(shared.UseManagedTranslator && !shared.FirstRunSetupCompleted && shared.OwnManagedModelId == "hy-mt2-7b-q4_k_m" &&
            shared.OwnVoiceTargetLanguage == "Chinese" && shared.IncomingTargetLanguage == "English" && shared.OwnVoiceEndPauseMilliseconds == 2700 &&
            shared.IncomingSettleMilliseconds == 1700 && shared.RecognitionDelayMilliseconds == 480,
            "completion selects the managed runtime but preserves live choices and leaves the UI-owned first-run marker unchanged");
        Check(reusedProgress.Select(item => item.Stage).Distinct().SequenceEqual(new[] { "Checking this PC", "Downloading translation files", "Preparing translation", "Checking speech recognition" }),
            "setup reports ordered friendly phases instead of raw helper commands");
        Check(!File.Exists(Path.Combine(Data("reuse"), "vrchat-translation-settings.json")), "the coordinator does not persist user settings or start tools itself");

        string managedModelsRoot = Path.Combine(Data("managed-speech-paths"), "models");
        string managedSpeechPath = Path.Combine(managedModelsRoot,
            LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == "voxtral-realtime-q4_k_m").FileName);
        WslSpeechRegistration managedSpeech = existing with { ModelPath = LocalTalkProvisioner.WindowsPathToWsl(managedSpeechPath) };
        int existenceProbes = 0;
        Check(WindowsAutomaticSetupPlatform.IsManagedSpeechModelMissing(managedSpeech, managedModelsRoot, path =>
            { existenceProbes++; return false; }) && existenceProbes == 1,
            "only an exact Windows-mapped managed speech registration detects a missing cached file");
        Check(!WindowsAutomaticSetupPlatform.IsManagedSpeechModelMissing(managedSpeech, managedModelsRoot, path => path == managedSpeechPath),
            "an existing registered cached speech model needs no repair");
        foreach (string foreignPath in new[] { "/opt/voxtral.cpp/models/voxtral/Q4_K_M.gguf", "/synthetic/imported/realtime.gguf",
            LocalTalkProvisioner.WindowsPathToWsl(Path.Combine(Data("foreign-cache"), Path.GetFileName(managedSpeechPath))),
            managedSpeech.ModelPath + "/../other.gguf" })
            Check(!WindowsAutomaticSetupPlatform.IsManagedSpeechModelMissing(existing with { ModelPath = foreignPath }, managedModelsRoot,
                _ => throw new InvalidOperationException("Foreign paths must not probe this cache.")),
                "foreign, manual, imported and noncanonical model paths do not trigger managed repair");
        Check(!WindowsAutomaticSetupPlatform.IsManagedSpeechModelMissing(null, managedModelsRoot, _ => false),
            "a new speech installation is not mistaken for a managed-cache repair");

        var managedRepair = new FakePlatform(managedSpeech) { ManagedSpeechModelMissing = true };
        // A registered imported reference can make this ID appear installed even while
        // the older speech registration's exact cached file is missing.
        managedRepair.Models.UnionWith([shared.OwnManagedModelId, "voxtral-realtime-q4_k_m"]);
        managedRepair.Distributions.Add(managedSpeech.Distribution);
        string repairRoot = Data("managed-speech-repair");
        Directory.CreateDirectory(repairRoot);
        File.WriteAllText(Path.Combine(repairRoot, "setup-progress.json"), JsonSerializer.Serialize(new
            { DistributionName = "LocalTalk-Ubuntu-0123abcd", Stage = "Preparing speech" }));
        var repairService = new AutomaticSetupService(managedRepair, repairRoot);
        AutomaticSetupPlan repairPlan = await repairService.PlanAsync(Settings(), token).ConfigureAwait(false);
        Check(repairPlan.DownloadBytes == ModelBytes("voxtral-realtime-q4_k_m") &&
            !repairPlan.RequiresAdministrator && !repairPlan.RestartMayBeNeeded &&
            repairPlan.Summary.Contains("Restore the missing speech model", StringComparison.Ordinal),
            "managed speech repair counts the required 2.9 GB even if an external reference has the same model ID, without claiming an engine rebuild or elevation");
        int repairInspections = managedRepair.Calls.Count;
        await RejectAsync<InvalidOperationException>(async () =>
            { await repairService.RunAsync(Settings(), false, token: token).ConfigureAwait(false); },
            "managed speech repair still needs explicit setup/download consent").ConfigureAwait(false);
        Check(managedRepair.Calls.Count == repairInspections && ReadStage(repairRoot) == "Preparing speech",
            "refused repair consent does not inspect again or alter the retained checkpoint");
        managedRepair.Calls.Clear();
        var repairProgress = new List<ProvisionProgress>();
        AppSettings repairSettings = Settings();
        AutomaticSetupResult repairResult = await repairService.RunAsync(repairSettings, true,
            new InlineProgress(repairProgress.Add), token).ConfigureAwait(false);
        Check(repairResult.Completed && managedRepair.DownloadedModels.SequenceEqual(new[] { "voxtral-realtime-q4_k_m" }) &&
            managedRepair.Calls.SequenceEqual(new[] { "Inspect", "Model:" + shared.OwnManagedModelId, "Runtime:llama-vulkan",
                "Model:voxtral-realtime-q4_k_m", "ValidateSpeech" }),
            "consented managed repair restores the speech model once before validating its existing engine");
        Check(managedRepair.Speech == managedSpeech && managedRepair.ValidatedSpeech == managedSpeech &&
            managedRepair.Builds.Count == 0 && managedRepair.CreatedDistributions.Count == 0 &&
            managedRepair.Distributions.SetEquals([managedSpeech.Distribution]) &&
            ReadDistribution(repairRoot) == "LocalTalk-Ubuntu-0123abcd" && !repairSettings.FirstRunSetupCompleted,
            "repair preserves exact speech registration, inventory, checkpointed distribution and UI-owned completion state");
        Check(repairProgress.Any(value => value.Stage == "Restoring speech recognition files") &&
            repairProgress.All(value => !value.Message.Contains("RAW_HELPER", StringComparison.Ordinal)),
            "repair progress is functional and keeps raw helper details out of setup");
        await repairService.RunAsync(repairSettings, true, token: token).ConfigureAwait(false);
        Check(managedRepair.DownloadedModels.Count == 1 && managedRepair.Calls.Count(call => call == "Model:voxtral-realtime-q4_k_m") == 1,
            "a completed repair is reused on subsequent setup without another speech download");

        var failedRepair = new FakePlatform(managedSpeech)
            { ManagedSpeechModelMissing = true, FailAt = "Model:voxtral-realtime-q4_k_m" };
        failedRepair.Models.Add(shared.OwnManagedModelId);
        string failedRepairRoot = Data("managed-speech-repair-failure");
        AppSettings failedRepairSettings = Settings();
        await RejectAsync<IOException>(async () =>
            { await new AutomaticSetupService(failedRepair, failedRepairRoot).RunAsync(failedRepairSettings, true, token: token).ConfigureAwait(false); },
            "a speech-model restore failure is surfaced before validating or completing setup").ConfigureAwait(false);
        Check(ReadStage(failedRepairRoot) != "Ready" && !failedRepairSettings.FirstRunSetupCompleted &&
            failedRepair.ValidatedSpeech is null && failedRepair.Speech == managedSpeech && failedRepair.Builds.Count == 0,
            "failed repair keeps the registration and choices and does not mark first-run setup complete");
        failedRepair.FailAt = null;
        Check((await new AutomaticSetupService(failedRepair, failedRepairRoot).RunAsync(failedRepairSettings, true, token: token)
            .ConfigureAwait(false)).Completed && failedRepair.DownloadedModels.SequenceEqual(new[] { "voxtral-realtime-q4_k_m" }),
            "a failed repair retries only its missing model rather than replacing an engine");

        var invalidRepairedEngine = new FakePlatform(managedSpeech)
            { ManagedSpeechModelMissing = true, FailAt = "ValidateSpeech" };
        invalidRepairedEngine.Models.Add(shared.OwnManagedModelId);
        string invalidRepairedRoot = Data("managed-speech-invalid-engine");
        AppSettings invalidRepairedSettings = Settings();
        await RejectAsync<IOException>(async () =>
            { await new AutomaticSetupService(invalidRepairedEngine, invalidRepairedRoot)
                .RunAsync(invalidRepairedSettings, true, token: token).ConfigureAwait(false); },
            "restoring the managed model does not hide an unrelated existing-engine validation failure").ConfigureAwait(false);
        Check(invalidRepairedEngine.DownloadedModels.SequenceEqual(new[] { "voxtral-realtime-q4_k_m" }) &&
            invalidRepairedEngine.Speech == managedSpeech && invalidRepairedEngine.Builds.Count == 0 &&
            invalidRepairedEngine.CreatedDistributions.Count == 0 && !invalidRepairedSettings.FirstRunSetupCompleted &&
            ReadStage(invalidRepairedRoot) != "Ready",
            "an invalid registered engine remains registered and never becomes a completed setup after model repair");
        invalidRepairedEngine.FailAt = null;
        Check((await new AutomaticSetupService(invalidRepairedEngine, invalidRepairedRoot)
            .RunAsync(invalidRepairedSettings, true, token: token).ConfigureAwait(false)).Completed &&
            invalidRepairedEngine.DownloadedModels.Count == 1,
            "retrying engine validation reuses the already restored speech file");

        var foreignSpeech = new FakePlatform(existing);
        foreignSpeech.Models.Add(shared.OwnManagedModelId);
        AutomaticSetupPlan foreignPlan = await new AutomaticSetupService(foreignSpeech, Data("foreign-speech"))
            .PlanAsync(Settings(), token).ConfigureAwait(false);
        await new AutomaticSetupService(foreignSpeech, Data("foreign-speech")).RunAsync(Settings(), true, token: token).ConfigureAwait(false);
        Check(foreignPlan.DownloadBytes == 0 && foreignSpeech.DownloadedModels.Count == 0 && foreignSpeech.ValidatedSpeech == existing,
            "foreign registered speech is only validated, never treated as missing app-managed speech");

        var separatePlatform = new FakePlatform(existing);
        AppSettings separateRun = Settings(); separateRun.ShareTranslationModel = false;
        await new AutomaticSetupService(separatePlatform, Data("separate")).RunAsync(separateRun, true, token: token).ConfigureAwait(false);
        Check(separatePlatform.DownloadedModels.SequenceEqual(new[] { "hy-mt2-7b-q4_k_m", "hy-mt2-1.8b-q4_k_m" }) && !separateRun.ShareTranslationModel,
            "separate setup prepares both explicit translation selections without forcing them to share");
        var livePlatform = new FakePlatform(existing);
        AppSettings live = Settings(); live.FirstRunSetupCompleted = true;
        live.OwnTranslatorBaseUrl = "http://localhost:1234/v1"; live.TranslatorBaseUrl = "http://127.0.0.1:1234/v1";
        live.OwnTranslatorModel = live.TranslatorModel = "synthetic-current-external-model";
        Check((await new AutomaticSetupService(livePlatform, Data("live")).PlanAsync(live, token).ConfigureAwait(false)).DownloadBytes == 0,
            "a completed external/live translator is not replaced by a new managed default during planning");
        livePlatform.Calls.Clear();
        await new AutomaticSetupService(livePlatform, Data("live")).RunAsync(live, true, token: token).ConfigureAwait(false);
        Check(livePlatform.Calls.SequenceEqual(new[] { "Inspect", "ValidateSpeech" }) && !live.UseManagedTranslator && live.FirstRunSetupCompleted &&
            live.OwnTranslatorModel == "synthetic-current-external-model" && live.OwnTranslatorBaseUrl == "http://localhost:1234/v1",
            "existing external endpoints, model names, and first-run status are preserved without managed downloads");

        var fresh = new FakePlatform { NativeReady = false, Nvidia = true, SpeakerReady = false };
        AppSettings freshSettings = Settings(); freshSettings.UseAiSpeakerRecognition = true;
        var friendly = new List<ProvisionProgress>();
        var freshService = new AutomaticSetupService(fresh, Data("fresh"));
        AutomaticSetupPlan freshPlan = await freshService.PlanAsync(freshSettings, token).ConfigureAwait(false);
        Check(freshPlan.RequiresAdministrator && freshPlan.RestartMayBeNeeded && freshPlan.DownloadBytes >= ModelBytes(freshSettings.OwnManagedModelId) + ModelBytes("voxtral-realtime-q4_k_m"),
            "fresh planning discloses prerequisites and both speech and translation downloads");
        fresh.Calls.Clear();
        AutomaticSetupResult freshResult = await freshService.RunAsync(freshSettings, true, new InlineProgress(friendly.Add), token).ConfigureAwait(false);
        Check(freshResult.Completed && fresh.Builds.Single() == (AppBrand.SpeechDistributionName, "cuda"), "fresh NVIDIA setup chooses CUDA only in the app-owned distribution");
        Check(fresh.Calls.SequenceEqual(new[] { "Inspect", "Native", "WindowsSpeech", "Distribution:" + AppBrand.SpeechDistributionName, "Model:hy-mt2-7b-q4_k_m",
            "Runtime:llama-vulkan", "Speaker", "Model:voxtral-realtime-q4_k_m", "Build:" + AppBrand.SpeechDistributionName + ":cuda" }), "fresh setup respects prerequisite/distribution/assets/build order");
        Check(fresh.DownloadedModels.Count == 2 && fresh.CreatedDistributions.Count == 1 && freshSettings.SpeakerModelPath == FakePlatform.ResolvedSpeakerPath,
            "fresh setup prepares the two required models and an optional speaker model once");
        Check(friendly.All(item => !item.Message.Contains("RAW_HELPER", StringComparison.Ordinal)) && friendly.Any(item => item.BytesReceived == 3 && item.TotalBytes == 10),
            "helper details are hidden from friendly progress while byte progress remains available");
        Check(friendly.Any(item => item.StepIndex == 4 && item.StepCount == 5 && item.WorkFraction == 0.25 &&
            item.Message == "Building speech recognition" && item.Detail == "Compiling the speech engine · 25 of 100 build tasks"),
            "guided setup preserves measured speech phases and task counters instead of replacing them with a generic loading message");
        Check(!freshSettings.FirstRunSetupCompleted && ReadStage(Data("fresh")) == "Ready", "Ready is a checkpoint, not a claim that live inference/audio was tested or started");

        var noNvidia = new FakePlatform { Nvidia = false };
        await new AutomaticSetupService(noNvidia, Data("vulkan")).RunAsync(Settings(), true, token: token).ConfigureAwait(false);
        Check(noNvidia.Builds.Single().Backend == "vulkan" && noNvidia.Builds.All(build => build.Backend != "cpu"), "non-NVIDIA setup selects Vulkan, never silently falls back to CPU");
        var renamed = new FakePlatform { ReplacementDistributionName = "SayWhat-Ubuntu-abcdef12" };
        string renamedRoot = Data("verified-replacement");
        await new AutomaticSetupService(renamed, renamedRoot).RunAsync(Settings(), true, token: token).ConfigureAwait(false);
        using (JsonDocument replacedCheckpoint = JsonDocument.Parse(File.ReadAllText(Path.Combine(renamedRoot, "setup-progress.json"))))
            Check(renamed.Builds.Single().Distribution == renamed.ReplacementDistributionName &&
                replacedCheckpoint.RootElement.GetProperty("DistributionName").GetString() == renamed.ReplacementDistributionName,
                "the verified replacement name is saved and built instead of blindly reusing a collided checkpoint name");
        var collision = new FakePlatform(); collision.Distributions.Add(AppBrand.SpeechDistributionName); collision.Distributions.Add("LocalTalk-Ubuntu"); collision.Distributions.Add("Ubuntu");
        await new AutomaticSetupService(collision, Data("collision")).RunAsync(Settings(), true, token: token).ConfigureAwait(false);
        Check(collision.CreatedDistributions.Single().StartsWith(AppBrand.SpeechDistributionName + "-", StringComparison.Ordinal) && collision.Distributions.Contains("Ubuntu") &&
            collision.Distributions.Contains("LocalTalk-Ubuntu"), "an existing distro name is not taken over, converted, or unregistered");

        // A pre-rename checkpoint belongs to the same installation. Resume it in
        // place rather than allocating another speech distro after a rebrand.
        string legacyCheckpointRoot = Data("legacy-checkpoint");
        Directory.CreateDirectory(legacyCheckpointRoot);
        File.WriteAllText(Path.Combine(legacyCheckpointRoot, "setup-progress.json"), JsonSerializer.Serialize(new
        { DistributionName = "LocalTalk-Ubuntu-0123abcd", Stage = "Preparing speech" }));
        var legacyCheckpoint = new FakePlatform();
        AutomaticSetupResult legacyResumed = await new AutomaticSetupService(legacyCheckpoint, legacyCheckpointRoot)
            .RunAsync(Settings(), true, token: token).ConfigureAwait(false);
        Check(legacyResumed.Completed && legacyCheckpoint.Builds.Single().Distribution == "LocalTalk-Ubuntu-0123abcd"
            && legacyCheckpoint.CreatedDistributions.Single() == "LocalTalk-Ubuntu-0123abcd",
            "a pre-rename setup checkpoint resumes the same registered speech component");

        var restart = new FakePlatform { NativeReady = false, NativeOutcome = SetupPrerequisiteResult.RestartRequired };
        string restartRoot = Data("restart");
        var restartService = new AutomaticSetupService(restart, restartRoot);
        AppSettings restartSettings = Settings();
        AutomaticSetupResult paused = await restartService.RunAsync(restartSettings, true, token: token).ConfigureAwait(false);
        Check(!paused.Completed && paused.NeedsRestart && restart.Calls.SequenceEqual(new[] { "Inspect", "Native" }) && ReadStage(restartRoot) == "Restart needed",
            "a Windows prerequisite restart checkpoints before any model or Linux work");
        int callsBeforeResume = restart.Calls.Count;
        AutomaticSetupResult premature = await new AutomaticSetupService(restart, restartRoot).RunAsync(restartSettings, true, token: token).ConfigureAwait(false);
        Check(premature.NeedsRestart && !premature.Completed && restart.Calls.Count == callsBeforeResume && !restartSettings.FirstRunSetupCompleted,
            "reopening before a reboot cannot bypass the restart checkpoint or inspect/download again");
        // Simulate an actual later boot by changing only this test checkpoint's timestamp.
        File.WriteAllText(Path.Combine(restartRoot, "setup-progress.json"), JsonSerializer.Serialize(new
        { DistributionName = (string?)null, RestartRequestedAt = DateTimeOffset.MinValue, Stage = "Restart needed" }));
        restart.NativeReady = true; restart.NativeOutcome = SetupPrerequisiteResult.Ready;
        AutomaticSetupResult resumed = await new AutomaticSetupService(restart, restartRoot).RunAsync(restartSettings, true, token: token).ConfigureAwait(false);
        Check(resumed.Completed && restart.Calls.Skip(callsBeforeResume).First() == "Inspect" && restart.Builds.Count == 1,
            "post-reboot resume re-inspects actual readiness before continuing once");
        Check(AutomaticSetupService.RebootedSince(DateTimeOffset.MinValue) && !AutomaticSetupService.RebootedSince(DateTimeOffset.UtcNow.AddMinutes(1)),
            "reboot evidence distinguishes a historical request from the current boot or a future timestamp");

        var windowsRestart = new FakePlatform { WindowsOutcome = SetupPrerequisiteResult.RestartRequired };
        AutomaticSetupResult windowsPaused = await new AutomaticSetupService(windowsRestart, Data("windows-restart")).RunAsync(Settings(), true, token: token).ConfigureAwait(false);
        Check(windowsPaused.NeedsRestart && windowsRestart.Calls.SequenceEqual(new[] { "Inspect", "WindowsSpeech" }) && windowsRestart.DownloadedModels.Count == 0,
            "WSL prerequisite restart also pauses before creating distributions or downloading models");

        var distributionRestart = new FakePlatform { DistributionNeedsRestart = true };
        string distributionRestartRoot = Data("distribution-restart"); AppSettings distributionRestartSettings = Settings();
        AutomaticSetupResult distributionPaused = await new AutomaticSetupService(distributionRestart, distributionRestartRoot)
            .RunAsync(distributionRestartSettings, true, token: token).ConfigureAwait(false);
        string restartDistribution = ReadDistribution(distributionRestartRoot)!;
        Check(distributionPaused.NeedsRestart && !distributionPaused.Completed && ReadStage(distributionRestartRoot) == "Restart needed" &&
            distributionRestart.DownloadedModels.Count == 0 && !distributionRestartSettings.FirstRunSetupCompleted,
            "a distro/WSL update restart pauses and checkpoints before downloading assets or completing setup");
        int distributionCalls = distributionRestart.Calls.Count;
        Check((await new AutomaticSetupService(distributionRestart, distributionRestartRoot)
            .RunAsync(distributionRestartSettings, true, token: token).ConfigureAwait(false)).NeedsRestart && distributionRestart.Calls.Count == distributionCalls,
            "same-boot continuation cannot repeat a WSL update after its restart checkpoint");
        File.WriteAllText(Path.Combine(distributionRestartRoot, "setup-progress.json"), JsonSerializer.Serialize(new
        { DistributionName = restartDistribution, RestartRequestedAt = DateTimeOffset.MinValue, Stage = "Restart needed" }));
        distributionRestart.DistributionNeedsRestart = false;
        AutomaticSetupResult distributionResumed = await new AutomaticSetupService(distributionRestart, distributionRestartRoot)
            .RunAsync(distributionRestartSettings, true, token: token).ConfigureAwait(false);
        Check(distributionResumed.Completed && distributionRestart.CreatedDistributions.Single() == restartDistribution &&
            distributionRestart.Builds.Single().Distribution == restartDistribution,
            "post-reboot WSL update resume creates/builds the same checkpointed owned distro once");

        var failure = new FakePlatform { FailAt = "Build:" + AppBrand.SpeechDistributionName + ":cuda" };
        string failureRoot = Data("failure"); AppSettings failedSettings = Settings();
        await RejectAsync<IOException>(async () =>
        { await new AutomaticSetupService(failure, failureRoot).RunAsync(failedSettings, true, token: token).ConfigureAwait(false); }, "a failed build is surfaced without marking setup complete").ConfigureAwait(false);
        Check(ReadStage(failureRoot) != "Ready" && !failedSettings.FirstRunSetupCompleted && !failedSettings.UseManagedTranslator,
            "failure preserves the user's runtime mode and first-run completion state");
        string savedDistro = ReadDistribution(failureRoot)!;
        failure.FailAt = null;
        AutomaticSetupResult retry = await new AutomaticSetupService(failure, failureRoot).RunAsync(failedSettings, true, token: token).ConfigureAwait(false);
        Check(retry.Completed && failure.CreatedDistributions.Count == 1 && failure.Calls.Where(call => call.StartsWith("Distribution:", StringComparison.Ordinal)).All(call => call == "Distribution:" + savedDistro),
            "failure retry reuses the checkpointed app-owned distro rather than creating duplicates");
        Check(failure.DownloadedModels.Count == 2, "retry reuses previously completed model assets instead of downloading them again");

        var lateFailure = new FakePlatform { SpeakerReady = false, FailAt = "Build:" + AppBrand.SpeechDistributionName + ":cuda" };
        AppSettings lateFailedSettings = Settings(); lateFailedSettings.UseAiSpeakerRecognition = true;
        await RejectAsync<IOException>(async () =>
        { await new AutomaticSetupService(lateFailure, Data("late-failure")).RunAsync(lateFailedSettings, true, token: token).ConfigureAwait(false); },
            "a late speech build failure is surfaced after optional assets were prepared").ConfigureAwait(false);
        Check(lateFailedSettings.SpeakerModelPath == "synthetic-original-speaker.onnx" && !lateFailedSettings.UseManagedTranslator && !lateFailedSettings.FirstRunSetupCompleted,
            "late failure does not publish a staged speaker-model path or partially changed settings");

        var brokenImport = new FakePlatform(existing) { FailAt = "ValidateSpeech" };
        await RejectAsync<IOException>(async () =>
        { await new AutomaticSetupService(brokenImport, Data("broken-import")).RunAsync(Settings(), true, token: token).ConfigureAwait(false); }, "a broken imported engine is not silently replaced").ConfigureAwait(false);
        Check(brokenImport.Builds.Count == 0 && brokenImport.CreatedDistributions.Count == 0 && brokenImport.Speech == existing,
            "import validation failure leaves current speech data and choices intact");

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var canceled = new FakePlatform(); canceled.BeforeCall = call => { if (call == "Model:hy-mt2-7b-q4_k_m") cancellation.Cancel(); };
        string canceledRoot = Data("cancel"); AppSettings canceledSettings = Settings();
        await RejectAsync<OperationCanceledException>(async () =>
        { await new AutomaticSetupService(canceled, canceledRoot).RunAsync(canceledSettings, true, token: cancellation.Token).ConfigureAwait(false); }, "cancellation stops scheduling setup work").ConfigureAwait(false);
        Check(!canceledSettings.FirstRunSetupCompleted && !canceledSettings.UseManagedTranslator && canceled.Builds.Count == 0 && canceled.DownloadedModels.Count == 0,
            "cancellation cannot load/build a model or publish completed settings");
        string canceledDistro = ReadDistribution(canceledRoot)!;
        canceled.BeforeCall = null;
        await new AutomaticSetupService(canceled, canceledRoot).RunAsync(canceledSettings, true, token: token).ConfigureAwait(false);
        Check(canceled.CreatedDistributions.Count == 1 && ReadDistribution(canceledRoot) == canceledDistro && canceled.Builds.Count == 1,
            "cancellation retry resumes the same owned component and produces a single successful build");
        Check(!Directory.EnumerateFiles(testRoot, "*.new-*", SearchOption.AllDirectories).Any(), "completed, paused, failed, and canceled setup leave no checkpoint temporary files");

        var native = new FakePlatform { NativeSpeechReady = false, NativeSpeechBytes = 42_000_000, SpeakerReady = false };
        AppSettings nativeSettings = Settings(); nativeSettings.OwnRecognitionEngine = "nemotron";
        nativeSettings.OwnVoiceSourceLanguage = "Chinese"; nativeSettings.UseAiSpeakerRecognition = true;
        nativeSettings.ShareTranslationModel = false; nativeSettings.IncomingManagedModelId = "not-selected-during-own-voice-setup";
        var nativeService = new AutomaticSetupService(native, Data("own-native"), ownVoiceOnly: true);
        AutomaticSetupPlan nativePlan = await nativeService.PlanAsync(nativeSettings, token).ConfigureAwait(false);
        Check(nativePlan.DownloadBytes == ModelBytes(nativeSettings.OwnManagedModelId) + 42_000_000 &&
            nativePlan.SpeechSetupRequired && !nativePlan.RequiresAdministrator && !nativePlan.RestartMayBeNeeded,
            "native own-voice planning counts only the selected own recognizer/translator without optional people or Linux components");
        native.Calls.Clear();
        AutomaticSetupResult nativeResult = await nativeService.RunAsync(nativeSettings, true, token: token).ConfigureAwait(false);
        Check(nativeResult.Completed && native.Calls.SequenceEqual(new[] { "Inspect", "Model:" + nativeSettings.OwnManagedModelId,
            "Runtime:llama-vulkan", "OwnNativeSpeech" }), "native onboarding never installs WSL, speaker recognition, incoming models, or legacy Voxtral");
        Check(nativeSettings.OwnVoiceSourceLanguage == "Chinese" && nativeSettings.OwnRecognitionEngine == "nemotron" &&
            nativeSettings.IncomingManagedModelId == "not-selected-during-own-voice-setup" && nativeSettings.UseAiSpeakerRecognition,
            "own-voice setup preserves explicit speech language and optional incoming preferences");
        Check(File.Exists(Path.Combine(Data("own-native"), "own-voice-setup-progress.json")) &&
            !File.Exists(Path.Combine(Data("own-native"), "setup-progress.json")), "new onboarding checkpoints cannot inherit a separate legacy repair restart request");

        var ownLegacy = new FakePlatform(existing) { SpeakerReady = false };
        AppSettings ownLegacySettings = Settings(); ownLegacySettings.OwnRecognitionEngine = "voxtral";
        ownLegacySettings.ShareTranslationModel = false; ownLegacySettings.UseAiSpeakerRecognition = true;
        await new AutomaticSetupService(ownLegacy, Data("own-legacy"), ownVoiceOnly: true)
            .RunAsync(ownLegacySettings, true, token: token).ConfigureAwait(false);
        Check(ownLegacy.Calls.SequenceEqual(new[] { "Inspect", "Model:" + ownLegacySettings.OwnManagedModelId,
            "Runtime:llama-vulkan", "ValidateSpeech" }), "choosing legacy speech still reuses the exact registered engine while deferring optional incoming assets");

        // Optional subtitles have their own preparation path: invalid, dormant
        // own-voice choices must not block downloading a valid incoming model.
        var incoming = new FakePlatform { SpeakerReady = false };
        AppSettings incomingSettings = Settings();
        incomingSettings.UseManagedTranslator = true; incomingSettings.ShareTranslationModel = false;
        incomingSettings.OwnRecognitionEngine = "nemotron"; incomingSettings.OwnVoiceSourceLanguage = "unsupported-own-source";
        incomingSettings.OwnManagedModelId = "unused-invalid-own-model";
        incomingSettings.OwnMicrophoneDeviceId = "keep-this-own-microphone";
        incomingSettings.UseAiSpeakerRecognition = true;
        var incomingService = new AutomaticSetupService(incoming, Data("incoming-only"), incomingVoiceOnly: true);
        AutomaticSetupPlan incomingPlan = await incomingService.PlanAsync(incomingSettings, token).ConfigureAwait(false);
        Check(incomingPlan.DownloadBytes == ModelBytes(incomingSettings.IncomingManagedModelId) + ModelBytes("voxtral-realtime-q4_k_m") +
            WindowsAutomaticSetupPlatform.SpeakerAsset.SizeBytes && incomingPlan.SpeechSetupRequired,
            "optional incoming preparation counts only its selected translation, legacy speech and enabled person-recognition assets");
        incoming.Calls.Clear();
        AutomaticSetupResult incomingResult = await incomingService.RunAsync(incomingSettings, true, token: token).ConfigureAwait(false);
        Check(incomingResult.Completed && incoming.Calls.SequenceEqual(new[] { "Inspect", "WindowsSpeech", "Distribution:" + AppBrand.SpeechDistributionName,
            "Model:" + incomingSettings.IncomingManagedModelId, "Runtime:llama-vulkan", "Speaker", "Model:voxtral-realtime-q4_k_m",
            "Build:" + AppBrand.SpeechDistributionName + ":cuda" }),
            "subtitles can be prepared after native onboarding without validating, preparing or starting the user's own recognizer");
        Check(incomingSettings.OwnManagedModelId == "unused-invalid-own-model" && incomingSettings.OwnVoiceSourceLanguage == "unsupported-own-source" &&
            incomingSettings.OwnRecognitionEngine == "nemotron" && incomingSettings.OwnMicrophoneDeviceId == "keep-this-own-microphone" &&
            incomingSettings.OwnVoiceTargetLanguage == "Chinese" && incomingSettings.UseManagedTranslator && !incomingSettings.ShareTranslationModel &&
            incomingSettings.SpeakerModelPath == FakePlatform.ResolvedSpeakerPath,
            "optional incoming setup changes only its prepared speaker asset path and leaves own-voice selections intact");
        Check(File.Exists(Path.Combine(Data("incoming-only"), "incoming-voice-setup-progress.json")) &&
            !File.Exists(Path.Combine(Data("incoming-only"), "own-voice-setup-progress.json")),
            "optional subtitles have a resumable checkpoint independent of own-voice onboarding");

        var noPeople = new FakePlatform(existing) { SpeakerReady = false };
        AppSettings noPeopleSettings = incomingSettings.Copy(); noPeopleSettings.UseAiSpeakerRecognition = false;
        await new AutomaticSetupService(noPeople, Data("incoming-without-people"), incomingVoiceOnly: true)
            .RunAsync(noPeopleSettings, true, token: token).ConfigureAwait(false);
        Check(noPeople.Calls.SequenceEqual(new[] { "Inspect", "Model:" + noPeopleSettings.IncomingManagedModelId, "Runtime:llama-vulkan", "ValidateSpeech" }),
            "person-recognition assets remain deferred when optional incoming voice identification is off");

        var onePerson = new FakePlatform(existing) { SpeakerReady = false };
        AppSettings onePersonSettings = incomingSettings.Copy(); onePersonSettings.IncomingSinglePerson = true;
        var onePersonService = new AutomaticSetupService(onePerson, Data("incoming-one-person"), incomingVoiceOnly: true);
        AutomaticSetupPlan onePersonPlan = await onePersonService.PlanAsync(onePersonSettings, token).ConfigureAwait(false);
        Check(onePersonPlan.DownloadBytes == ModelBytes(onePersonSettings.IncomingManagedModelId),
            "a fixed one-person subtitle source does not count an unused voice-identification model");
        onePerson.Calls.Clear();
        await onePersonService.RunAsync(onePersonSettings, true, token: token).ConfigureAwait(false);
        Check(onePerson.Calls.SequenceEqual(new[] { "Inspect", "Model:" + onePersonSettings.IncomingManagedModelId, "Runtime:llama-vulkan", "ValidateSpeech" }) &&
            onePersonSettings.UseAiSpeakerRecognition, "one-person preparation defers identification assets while preserving the user's requested recognition preference");

        var externalIncoming = new FakePlatform(existing);
        AppSettings externalIncomingSettings = incomingSettings.Copy(); externalIncomingSettings.UseManagedTranslator = false;
        externalIncomingSettings.FirstRunSetupCompleted = true; externalIncomingSettings.UseAiSpeakerRecognition = false;
        externalIncomingSettings.OwnTranslatorBaseUrl = "https://example.com/not-used-by-subtitles";
        externalIncomingSettings.TranslatorBaseUrl = "http://127.0.0.1:1235/v1";
        await new AutomaticSetupService(externalIncoming, Data("incoming-external"), incomingVoiceOnly: true)
            .RunAsync(externalIncomingSettings, true, token: token).ConfigureAwait(false);
        Check(externalIncoming.Calls.SequenceEqual(new[] { "Inspect", "ValidateSpeech" }) && !externalIncomingSettings.UseManagedTranslator,
            "an incoming local external translator does not validate an unused own endpoint or change the existing global connection mode");
        bool mixedDirectionsRejected = false;
        try { _ = new AutomaticSetupService(new FakePlatform(), Data("invalid-directions"), ownVoiceOnly: true, incomingVoiceOnly: true); }
        catch (ArgumentException) { mixedDirectionsRejected = true; }
        Check(mixedDirectionsRejected && !Directory.Exists(Data("invalid-directions")),
            "mutually exclusive independent setup directions are rejected without any platform work or checkpoint writes");
        return checks;
    }

    private static long ModelBytes(string id) => LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == id).SizeBytes;
    private static string ReadStage(string root)
    { using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "setup-progress.json"))); return document.RootElement.GetProperty("Stage").GetString()!; }
    private static string? ReadDistribution(string root)
    { using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "setup-progress.json"))); return document.RootElement.GetProperty("DistributionName").GetString(); }
    private sealed class InlineProgress(Action<ProvisionProgress> report) : IProgress<ProvisionProgress>
    { public void Report(ProvisionProgress value) => report(value); }

    private sealed class FakePlatform : IAutomaticSetupPlatform
    {
        internal const string ResolvedSpeakerPath = "synthetic-resolved-speaker.onnx";
        internal readonly List<string> Calls = new();
        internal readonly HashSet<string> Models = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Runtimes = new(StringComparer.Ordinal) { "llama-vulkan" };
        internal readonly HashSet<string> Distributions = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> DownloadedModels = new(), CreatedDistributions = new();
        internal readonly List<(string Distribution, string Backend)> Builds = new();
        internal WslSpeechRegistration? Speech, ValidatedSpeech;
        internal bool NativeReady = true, SpeakerReady = true, Nvidia = true, DistributionNeedsRestart, ManagedSpeechModelMissing;
        internal bool NativeSpeechReady;
        internal long NativeSpeechBytes;
        internal SetupPrerequisiteResult NativeOutcome = SetupPrerequisiteResult.Ready, WindowsOutcome = SetupPrerequisiteResult.Ready;
        internal string? FailAt;
        internal string? ReplacementDistributionName;
        internal Action<string>? BeforeCall;
        internal FakePlatform(WslSpeechRegistration? speech = null) => Speech = speech;
        private void Step(string call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(call); BeforeCall?.Invoke(call); token.ThrowIfCancellationRequested();
            if (call == FailAt) throw new IOException("Synthetic setup failure; no external work performed.");
        }
        private static void Detail(IProgress<ProvisionProgress>? progress) => progress?.Report(new("RAW_HELPER", "RAW_HELPER must remain hidden", 3, 10));
        public Task<AutomaticSetupSnapshot> InspectAsync(AppSettings settings, CancellationToken token)
        {
            Step("Inspect", token);
            return Task.FromResult(new AutomaticSetupSnapshot(Models.ToHashSet(), Runtimes.ToHashSet(), Distributions.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Speech, NativeReady, SpeakerReady, Nvidia) { ManagedSpeechModelMissing = ManagedSpeechModelMissing });
        }
        public Task<SetupPrerequisiteResult> EnsureNativeRuntimeAsync(IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Native", token); if (NativeOutcome == SetupPrerequisiteResult.Ready) NativeReady = true; return Task.FromResult(NativeOutcome); }
        public Task<SetupPrerequisiteResult> EnsureWindowsSpeechAsync(IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("WindowsSpeech", token); return Task.FromResult(WindowsOutcome); }
        public Task<string> EnsureDistributionAsync(string name, IProgress<ProvisionProgress>? progress, CancellationToken token)
        {
            Step("Distribution:" + name, token);
            if (DistributionNeedsRestart) throw new SetupRestartRequiredException();
            name = ReplacementDistributionName ?? name;
            if (Distributions.Add(name)) CreatedDistributions.Add(name);
            return Task.FromResult(name);
        }
        public Task EnsureModelAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token)
        {
            Step("Model:" + id, token);
            bool repairingSpeech = id == "voxtral-realtime-q4_k_m" && ManagedSpeechModelMissing;
            if (Models.Add(id) || repairingSpeech) DownloadedModels.Add(id);
            if (repairingSpeech) ManagedSpeechModelMissing = false;
            Detail(progress); return Task.CompletedTask;
        }
        public Task EnsureRuntimeAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Runtime:" + id, token); Runtimes.Add(id); Detail(progress); return Task.CompletedTask; }
        public Task<string> EnsureSpeakerModelAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Speaker", token); Detail(progress); return Task.FromResult(SpeakerReady ? settings.SpeakerModelPath : ResolvedSpeakerPath); }
        public Task ValidateSpeechAsync(WslSpeechRegistration speech, CancellationToken token)
        { Step("ValidateSpeech", token); ValidatedSpeech = speech; return Task.CompletedTask; }
        public Task BuildSpeechAsync(string distribution, string backend, IProgress<ProvisionProgress>? progress, CancellationToken token)
        {
            Step("Build:" + distribution + ":" + backend, token); Builds.Add((distribution, backend));
            progress?.Report(new("Speech setup", "Building speech recognition")
            {
                StepIndex = 4, StepCount = 5, WorkFraction = 0.25,
                Detail = "Compiling the speech engine · 25 of 100 build tasks"
            });
            Speech = new(distribution, "/synthetic/built/voxtral-server", "/synthetic/realtime.gguf", backend) { RunAsRoot = true };
            return Task.CompletedTask;
        }
        public long PendingOwnNativeSpeechBytes(AppSettings settings) => NativeSpeechReady ? 0 : NativeSpeechBytes;
        public bool IsOwnNativeSpeechReady(AppSettings settings) => NativeSpeechReady;
        public Task EnsureOwnNativeSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("OwnNativeSpeech", token); NativeSpeechReady = true; Detail(progress); return Task.CompletedTask; }
    }
}

namespace IncomingTranslator;

// Complete setup routing with fake platform work and isolated checkpoint files.
// No Windows servicing, WSL, models, audio, network or executable is used.
internal static class GeneralNativeSetupTests
{
    internal static Task<int> RunAsync(string directory, CancellationToken token = default) =>
        Task.Run(() => RunCoreAsync(directory, token), token);

    private static async Task<int> RunCoreAsync(string directory, CancellationToken token)
    {
        int checks = 0;
        string root = Path.Combine(Path.GetFullPath(directory), "general-native-setup");
        void Check(bool condition, string detail)
        {
            token.ThrowIfCancellationRequested();
            if (!condition) throw new InvalidOperationException("General native setup: " + detail);
            checks++;
        }
        AppSettings Settings() => new()
        {
            UseManagedTranslator = true, ShareTranslationModel = false, UseAiSpeakerRecognition = false,
            OwnManagedModelId = "hy-mt2-1.8b-q4_k_m", IncomingManagedModelId = "hy-mt2-7b-q4_k_m",
            IncomingAudioMode = "desktop", CaptureVrChatOnly = false
        };
        AutomaticSetupService Service(FakePlatform platform, string name, bool ownOnly = false, bool incomingOnly = false,
            int windowsBuild = 22000) => new(platform, Path.Combine(root, name), ownOnly, incomingOnly,
                () => new Version(10, 0, windowsBuild));
        static long ModelBytes(string id) => LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == id).SizeBytes;
        static bool NoLinux(FakePlatform platform) => !platform.Calls.Any(call => call.StartsWith("Linux:", StringComparison.Ordinal));

        var cached = new FakePlatform
        {
            RejectLinux = true, ManagedSpeechModelMissing = true,
            Speech = new("Unrelated-Ubuntu", "/not-run/voxtral-server", "/not-read/missing.gguf", "cuda")
        };
        AppSettings both = Settings();
        var full = Service(cached, "both-nemotron");
        AutomaticSetupPlan proposed = await full.PlanAsync(both, token).ConfigureAwait(false);
        Check(proposed.DownloadBytes == ModelBytes(both.OwnManagedModelId) + ModelBytes(both.IncomingManagedModelId) + FakePlatform.NemoBytes,
            "full native planning counts both translators and their shared Nemotron assets once");
        Check(proposed.SpeechSetupRequired && !proposed.RequiresAdministrator && !proposed.RestartMayBeNeeded &&
            proposed.Summary.Contains("No Linux component is needed.", StringComparison.Ordinal),
            "native preparation does not inherit unrelated registered WSL prerequisites");
        Check(cached.Calls.SequenceEqual(new[] { "Inspect" }), "planning native setup performs no Linux capability check");
        cached.Calls.Clear();
        AutomaticSetupResult result = await full.RunAsync(both, true, token: token).ConfigureAwait(false);
        Check(result.Completed && !result.NeedsRestart && cached.Calls.SequenceEqual(new[]
            { "Inspect", "Model:" + both.OwnManagedModelId, "Model:" + both.IncomingManagedModelId,
                "Runtime:llama-vulkan", "OwnNative", "IncomingNative:nemotron" }),
            "the unscoped Check/Repair path prepares both native directions and separate translation models");
        Check(cached.NemoPreparations == 1 && cached.FireRedPreparations == 0 && NoLinux(cached),
            "shared native files are prepared once and cached unrelated WSL is never launched or repaired");
        AutomaticSetupPlan ready = await full.PlanAsync(both, token).ConfigureAwait(false);
        Check(ready.DownloadBytes == 0 && !ready.SpeechSetupRequired && NoLinux(cached),
            "all native readiness is reported after both directions finish");

        var fresh = new FakePlatform { RejectLinux = true };
        AppSettings freshSettings = Settings(); freshSettings.ShareTranslationModel = true;
        AutomaticSetupResult freshResult = await Service(fresh, "fresh-no-linux").RunAsync(freshSettings, true, token: token).ConfigureAwait(false);
        Check(freshResult.Completed && NoLinux(fresh) && fresh.Speech is null && fresh.NemoPreparations == 1,
            "fresh general setup does not create a default Linux speech component");
        Check(fresh.Calls.Count(call => call.StartsWith("Model:", StringComparison.Ordinal)) == 1,
            "shared translation models remain deduplicated independently of speech routing");

        var distinct = new FakePlatform { RejectLinux = true };
        AppSettings fireIncoming = Settings(); fireIncoming.IncomingSpeechEngine = "firered2-aed";
        fireIncoming.IncomingSourceLanguage = "Chinese";
        var distinctService = Service(distinct, "native-nemotron-firered");
        AutomaticSetupPlan distinctPlan = await distinctService.PlanAsync(fireIncoming, token).ConfigureAwait(false);
        Check(distinctPlan.DownloadBytes == ModelBytes(fireIncoming.OwnManagedModelId) + ModelBytes(fireIncoming.IncomingManagedModelId) +
            FakePlatform.NemoBytes + FakePlatform.FireRedBytes, "different native recognizers count both distinct asset sets");
        await distinctService.RunAsync(fireIncoming, true, token: token).ConfigureAwait(false);
        Check(distinct.NemoPreparations == 1 && distinct.FireRedPreparations == 1 && NoLinux(distinct),
            "full native preparation includes FireRed subtitles as well as the microphone's Nemotron");
        distinct.FireRedReady = false;
        AutomaticSetupPlan partlyReady = await distinctService.PlanAsync(fireIncoming, token).ConfigureAwait(false);
        Check(partlyReady.SpeechSetupRequired && partlyReady.DownloadBytes == FakePlatform.FireRedBytes,
            "ready microphone assets do not hide missing native subtitle assets");

        var incomingWsl = new FakePlatform();
        AppSettings mixedIncoming = Settings(); mixedIncoming.IncomingSpeechEngine = "voxtral";
        var mixedIncomingService = Service(incomingWsl, "native-own-wsl-incoming");
        AutomaticSetupPlan mixedPlan = await mixedIncomingService.PlanAsync(mixedIncoming, token).ConfigureAwait(false);
        Check(mixedPlan.DownloadBytes == ModelBytes(mixedIncoming.OwnManagedModelId) + ModelBytes(mixedIncoming.IncomingManagedModelId) +
            FakePlatform.NemoBytes + ModelBytes("voxtral-realtime-q4_k_m"), "mixed preparation counts native and WSL speech assets");
        Check(mixedPlan.SpeechSetupRequired && mixedPlan.RequiresAdministrator && mixedPlan.RestartMayBeNeeded &&
            !mixedPlan.Summary.Contains("No Linux component is needed.", StringComparison.Ordinal),
            "a mixed selection retains the selected WSL prerequisites and an honest summary");
        incomingWsl.Calls.Clear();
        await mixedIncomingService.RunAsync(mixedIncoming, true, token: token).ConfigureAwait(false);
        Check(incomingWsl.NemoPreparations == 1 && incomingWsl.Calls.Contains("OwnNative") &&
            !incomingWsl.Calls.Any(call => call.StartsWith("IncomingNative:", StringComparison.Ordinal)) &&
            incomingWsl.Calls.Contains("Linux:Build:" + AppBrand.SpeechDistributionName + ":cuda"),
            "native own voice and WSL incoming speech are both prepared before Ready");
        Check(incomingWsl.Calls.IndexOf("OwnNative") < incomingWsl.Calls.FindIndex(call => call.StartsWith("Linux:Build:", StringComparison.Ordinal)),
            "the native branch does not exclude the subsequent selected WSL build");

        var ownWsl = new FakePlatform
        { Speech = new("Selected-Ubuntu", "/synthetic/server", "/synthetic/model.gguf", "cpu"), NemoReady = true };
        AppSettings mixedOwn = Settings(); mixedOwn.OwnRecognitionEngine = "voxtral"; mixedOwn.OwnVoiceSourceLanguage = "Automatic";
        mixedOwn.IncomingSpeechEngine = "firered2-ctc"; mixedOwn.IncomingSourceLanguage = "Chinese";
        var mixedOwnService = Service(ownWsl, "wsl-own-native-incoming");
        AutomaticSetupPlan ownWslPlan = await mixedOwnService.PlanAsync(mixedOwn, token).ConfigureAwait(false);
        Check(ownWslPlan.DownloadBytes == ModelBytes(mixedOwn.OwnManagedModelId) + ModelBytes(mixedOwn.IncomingManagedModelId) + FakePlatform.FireRedBytes &&
            ownWslPlan.SpeechSetupRequired && !ownWslPlan.RequiresAdministrator,
            "existing WSL plus native FireRed planning counts only the missing selected native files");
        ownWsl.Calls.Clear();
        await mixedOwnService.RunAsync(mixedOwn, true, token: token).ConfigureAwait(false);
        Check(ownWsl.NemoPreparations == 0 && ownWsl.FireRedPreparations == 1 && ownWsl.Calls.Contains("IncomingNative:firered2-ctc") &&
            ownWsl.Calls.Contains("Linux:Validate") && !ownWsl.Calls.Contains("Linux:WindowsSpeech"),
            "WSL own voice is validated while native incoming speech is prepared independently");
        ownWsl.ManagedSpeechModelMissing = true;
        Check((await mixedOwnService.PlanAsync(mixedOwn, token).ConfigureAwait(false)).SpeechSetupRequired,
            "a missing model in the selected WSL component still requires speech setup");

        var win10 = new FakePlatform { RejectLinux = true };
        AppSettings win10Settings = Settings(); win10Settings.IncomingAudioMode = "vrchat"; win10Settings.CaptureVrChatOnly = true;
        var win10Service = Service(win10, "win10-dormant-capture", windowsBuild: 17763);
        AutomaticSetupPlan win10Plan = await win10Service.PlanAsync(win10Settings, token).ConfigureAwait(false);
        Check(win10Plan.SpeechSetupRequired && !win10Plan.RequiresAdministrator && NoLinux(win10),
            "general Windows 10 planning accepts dormant process capture while planning native files only");
        AutomaticSetupResult win10Result = await win10Service.RunAsync(win10Settings, true, token: token).ConfigureAwait(false);
        Check(win10Result.Completed && win10.Calls.Contains("OwnNative") && win10.Calls.Contains("IncomingNative:nemotron") && NoLinux(win10) &&
            win10Settings.IncomingAudioMode == "vrchat", "general Windows 10 setup prepares native files without starting or broadening dormant application capture");

        async Task RejectBeforeInspection(AppSettings invalid, string name, int windowsBuild = 22000, bool incomingOnly = false)
        {
            var platform = new FakePlatform(); bool planRejected = false, runRejected = false;
            var service = Service(platform, name, incomingOnly: incomingOnly, windowsBuild: windowsBuild);
            try { await service.PlanAsync(invalid, token).ConfigureAwait(false); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { planRejected = true; }
            try { await service.RunAsync(invalid, true, token: token).ConfigureAwait(false); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { runRejected = true; }
            Check(planRejected && runRejected && platform.Calls.Count == 0 && !Directory.Exists(Path.Combine(root, name)),
                "setup validates included speech selections and explicit capture before inspection: " + name);
        }
        AppSettings badOwn = Settings(); badOwn.OwnVoiceSourceLanguage = "unsupported-language";
        await RejectBeforeInspection(badOwn, "bad-own-language").ConfigureAwait(false);
        AppSettings badIncoming = Settings(); badIncoming.IncomingSpeechEngine = "firered2-aed"; badIncoming.IncomingSourceLanguage = "Japanese";
        await RejectBeforeInspection(badIncoming, "bad-incoming-language").ConfigureAwait(false);
        AppSettings unsupportedCapture = Settings(); unsupportedCapture.IncomingAudioMode = "vrchat"; unsupportedCapture.CaptureVrChatOnly = true;
        await RejectBeforeInspection(unsupportedCapture, "unsupported-capture", windowsBuild: 19045, incomingOnly: true).ConfigureAwait(false);

        var ownOnly = new FakePlatform { RejectLinux = true };
        AppSettings dormantIncoming = Settings(); dormantIncoming.IncomingSpeechEngine = "unsupported";
        dormantIncoming.IncomingSourceLanguage = "unsupported"; dormantIncoming.IncomingManagedModelId = "unused-invalid";
        dormantIncoming.IncomingAudioMode = "vrchat";
        await Service(ownOnly, "own-only", ownOnly: true, windowsBuild: 17763)
            .RunAsync(dormantIncoming, true, token: token).ConfigureAwait(false);
        Check(ownOnly.Calls.Contains("OwnNative") && !ownOnly.Calls.Any(call => call.StartsWith("IncomingNative:", StringComparison.Ordinal)) && NoLinux(ownOnly),
            "own-only setup still ignores unavailable capture and dormant incoming preferences");
        var incomingOnly = new FakePlatform { RejectLinux = true };
        AppSettings dormantOwn = Settings(); dormantOwn.ShareTranslationModel = false;
        dormantOwn.OwnVoiceSourceLanguage = "unsupported"; dormantOwn.OwnManagedModelId = "unused-invalid";
        await Service(incomingOnly, "incoming-only", incomingOnly: true).RunAsync(dormantOwn, true, token: token).ConfigureAwait(false);
        Check(incomingOnly.Calls.Contains("IncomingNative:nemotron") && !incomingOnly.Calls.Contains("OwnNative") && NoLinux(incomingOnly),
            "incoming-only setup still ignores dormant microphone choices");

        return checks;
    }

    private sealed class FakePlatform : IAutomaticSetupPlatform
    {
        internal const long NemoBytes = 100, FireRedBytes = 300;
        internal readonly List<string> Calls = new();
        internal readonly HashSet<string> Models = new(StringComparer.Ordinal);
        internal bool RejectLinux, NemoReady, FireRedReady, ManagedSpeechModelMissing;
        internal int NemoPreparations, FireRedPreparations;
        internal WslSpeechRegistration? Speech;
        private void Step(string call, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (RejectLinux && call.StartsWith("Linux:", StringComparison.Ordinal))
                throw new InvalidOperationException("Native settings must never launch or inspect WSL.");
            Calls.Add(call);
        }
        public Task<AutomaticSetupSnapshot> InspectAsync(AppSettings settings, CancellationToken token)
        {
            Step("Inspect", token);
            return Task.FromResult(new AutomaticSetupSnapshot(Models.ToHashSet(), new HashSet<string> { "llama-vulkan" },
                new HashSet<string>(), Speech, true, true, true) { ManagedSpeechModelMissing = ManagedSpeechModelMissing });
        }
        public long PendingOwnNativeSpeechBytes(AppSettings settings) => NemoReady ? 0 : NemoBytes;
        public bool IsOwnNativeSpeechReady(AppSettings settings) => NemoReady;
        public long PendingIncomingNativeSpeechBytes(AppSettings settings) => IncomingRecognitionCatalog.UsesFireRed(settings)
            ? FireRedReady ? 0 : FireRedBytes : NemoReady ? 0 : NemoBytes;
        public bool IsIncomingNativeSpeechReady(AppSettings settings) => IncomingRecognitionCatalog.UsesFireRed(settings) ? FireRedReady : NemoReady;
        private void PrepareNemo() { if (!NemoReady) NemoPreparations++; NemoReady = true; }
        public Task EnsureOwnNativeSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("OwnNative", token); PrepareNemo(); return Task.CompletedTask; }
        public Task EnsureIncomingNativeSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token)
        {
            Step("IncomingNative:" + settings.IncomingSpeechEngine, token);
            if (IncomingRecognitionCatalog.UsesFireRed(settings))
            { if (!FireRedReady) FireRedPreparations++; FireRedReady = true; }
            else PrepareNemo();
            return Task.CompletedTask;
        }
        public void ValidateSpeechPlatform(string backend) => Step("Linux:Platform:" + backend);
        public Task<SetupPrerequisiteResult> EnsureNativeRuntimeAsync(IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("NativeRuntime", token); return Task.FromResult(SetupPrerequisiteResult.Ready); }
        public Task<SetupPrerequisiteResult> EnsureWindowsSpeechAsync(IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Linux:WindowsSpeech", token); return Task.FromResult(SetupPrerequisiteResult.Ready); }
        public Task<string> EnsureDistributionAsync(string name, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Linux:Distribution:" + name, token); return Task.FromResult(name); }
        public Task EnsureModelAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Model:" + id, token); Models.Add(id); if (id == "voxtral-realtime-q4_k_m") ManagedSpeechModelMissing = false; return Task.CompletedTask; }
        public Task EnsureRuntimeAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Runtime:" + id, token); return Task.CompletedTask; }
        public Task<string> EnsureSpeakerModelAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Speaker", token); return Task.FromResult(settings.SpeakerModelPath); }
        public Task ValidateSpeechAsync(WslSpeechRegistration speech, CancellationToken token)
        { Step("Linux:Validate", token); return Task.CompletedTask; }
        public Task BuildSpeechAsync(string distribution, string backend, IProgress<ProvisionProgress>? progress, CancellationToken token)
        { Step("Linux:Build:" + distribution + ":" + backend, token); Speech = new(distribution, "/synthetic/server", "/synthetic/model.gguf", backend); return Task.CompletedTask; }
    }
}

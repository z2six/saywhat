namespace IncomingTranslator;

/// <summary>Synthetic policy tests only: no hardware probe, audio, network, or model runtime.</summary>
public static class HardwareAdvisorOfflineTests
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!condition) throw new InvalidOperationException("Hardware advisor test failed: " + message);
            checks++;
        }
        var gpu = new LocalGpuProfile("test-gpu", "Synthetic 32 GiB GPU", 0x10de, 32768);
        var hardware = new LocalHardwareProfile("Synthetic CPU", 32, 65536, 50000, [gpu], []);
        var options = new LocalAdvisorOptions { GpuId = gpu.Id };
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        // Generic memory-policy fixtures are not user-facing model recommendations.
        var models = HardwareAdvisor.InitialTranslationProfiles.Concat(new LocalTranslationProfile[] {
            new("test-small", "Synthetic small policy fixture", 2304, 3072),
            new("test-medium", "Synthetic medium policy fixture", 4096, 5120),
            new("test-large", "Synthetic large policy fixture", 7168, 8192),
            new("test-extra", "Synthetic over-budget policy fixture", 11264, 12288)
        }).ToArray();
        LocalHardwareRecommendation Advice(params LocalBenchmarkRecord[] records) =>
            HardwareAdvisor.Recommend(hardware, models, records, options, now);
        LocalBenchmarkRecord Record(string id, double latency = 3200, long peakVram = 22000) =>
            new(hardware.Fingerprint, id, HardwareAdvisor.ConfigurationKey(options, gpu.Id), now,
                true, 24, latency, 0.5, 400, peakVram, 23000, 0, 0, true);
        AdvisoryStatus Status(LocalHardwareRecommendation result, string id) =>
            result.Candidates.Single(c => c.ModelId == id).Status;
        string small = "test-small", medium = "test-medium", large = "test-large", extra = "test-extra";
        const string dedicatedSmall = "hy-mt2-1.8b-q4_k_m", dedicatedLarge = "hy-mt2-7b-q4_k_m";
        var hySmall = models.Single(model => model.Id == dedicatedSmall);
        var hyLarge = models.Single(model => model.Id == dedicatedLarge);
        Check(hySmall.EstimatedResidentVramMiB == 2048 && hySmall.EstimatedResidentRamMiB == 3072,
            "Dedicated small catalog candidate has conservative explicit memory estimates.");
        Check(hyLarge.EstimatedResidentVramMiB == 6144 && hyLarge.EstimatedResidentRamMiB == 7168,
            "Dedicated larger catalog candidate has conservative explicit memory estimates.");
        Check(hySmall.DisplayName.Contains("unmeasured") && hyLarge.DisplayName.Contains("unmeasured"),
            "Dedicated catalog candidates do not advertise measured quality or speed.");
        Check(HardwareAdvisor.InitialTranslationProfiles.Count == 2 && HardwareAdvisor.InitialTranslationProfiles.All(model => model.Id.StartsWith("hy-mt2-", StringComparison.Ordinal)),
            "Default hardware recommendations contain only the dedicated Hy-MT2 choices.");

        var unmeasured = Advice();
        Check(unmeasured.RecommendedModelId is null, "No measured winner is invented.");
        Check(unmeasured.ProvisionalModelId == dedicatedSmall, "Lowest-residency dedicated candidate is a provisional starting selection, not a benchmark winner.");
        Check(unmeasured.IsProvisional && unmeasured.NeedsBenchmark, "Unmeasured advice is explicitly provisional.");
        Check(unmeasured.GameVramReserveMiB == 8192 && unmeasured.AppVramBudgetMiB == 24576, "Game keeps one quarter of 32 GiB.");
        Check(unmeasured.Settings.TranslationInstances == 1 && unmeasured.Settings.TranslationConcurrency == 1, "Default shares one residency and bounds concurrency.");
        Check(Status(unmeasured, large) == AdvisoryStatus.NeedsBenchmark && unmeasured.ModelId != large, "A larger tier fitting capacity is not automatically preferred.");
        Check(Status(unmeasured, extra) == AdvisoryStatus.Rejected, "Optional 14B is not an automatic upgrade.");
        Check(Status(unmeasured, dedicatedLarge) == AdvisoryStatus.NeedsBenchmark && unmeasured.ModelId != dedicatedLarge,
            "A larger dedicated translator is not preferred without measured language-pair quality and latency.");
        Check(unmeasured.SuggestedCpuThreads == 8, "CPU thread suggestion preserves the remaining logical CPUs.");
        Check(unmeasured.Title.Contains("Provisional") && unmeasured.Explanation.Contains("unmeasured"), "UI copy does not imply a speed result.");
        Check(unmeasured.Warnings.Any(w => w.Contains("stateless")), "Shared residency documents independent contexts.");

        var measured = Advice(Record(small, 3500, 20000), Record(medium), Record(large, 2800, 26000));
        Check(measured.RecommendedModelId == medium && !measured.IsProvisional, "Measured latency can select a middle tier over smallest/largest.");
        Check(Status(measured, large) == AdvisoryStatus.Rejected, "Fast but over-budget measurement is rejected.");
        Check(Advice(Record(medium) with { QualityFixturePassed = false }).RecommendedModelId is null, "Quality is measured, not assumed from size.");
        Check(Status(Advice(Record(medium) with { QualityFixturePassed = false }), medium) == AdvisoryStatus.Rejected, "Failed language-pair fixture is rejected.");
        Check(Status(Advice(Record(medium) with { DroppedAudioFrames = 1 }), medium) == AdvisoryStatus.Rejected, "Audio drops are a hard failure.");
        Check(Status(Advice(Record(medium) with { Failures = 1 }), medium) == AdvisoryStatus.Rejected, "Runtime faults are a hard failure.");
        Check(Status(Advice(Record(medium, 4100)), medium) == AdvisoryStatus.Rejected, "P95 final latency, not mean/token throughput, gates readiness.");
        Check(Status(Advice(Record(medium) with { AsrRealTimeFactor = 1.1 }), medium) == AdvisoryStatus.Rejected, "Speech must stay ahead of live audio.");
        Check(Status(Advice(Record(medium) with { MaximumAsrLagMs = 1500 }), medium) == AdvisoryStatus.Rejected, "Speech lag has its own bound.");
        Check(Status(Advice(Record(medium) with { PeakAppRamMiB = 60000 }), medium) == AdvisoryStatus.Rejected, "RAM headroom is independent of GPU capacity.");
        Check(Status(Advice(Record(medium) with { PeakAppVramMiB = 0 }), medium) == AdvisoryStatus.Rejected, "A missing GPU peak does not mean zero footprint.");
        Check(Advice(Record(medium) with { AlongsideGame = false }).RecommendedModelId is null, "Idle benchmark cannot certify the gaming workload.");
        Check(Advice(Record(medium) with { HardwareFingerprint = "other-hardware" }).RecommendedModelId is null, "Other hardware measurement is ignored.");
        Check(Advice(Record(medium) with { ConfigurationKey = "different-configuration" }).RecommendedModelId is null, "Different context/concurrency/language/runtime configuration is ignored.");
        Check(Advice(Record(medium) with { RecordedAt = now.AddDays(-31) }).RecommendedModelId is null, "Old measurements are ignored.");
        Check(Advice(Record(medium) with { RecordedAt = now.AddMinutes(1) }).RecommendedModelId is null, "Future measurements are ignored.");
        Check(Status(Advice(Record(medium) with { Samples = 1 }), medium) == AdvisoryStatus.NeedsBenchmark, "Insufficient repetitions remain provisional.");
        Check(Status(Advice(Record(medium) with { P95FinalLatencyMs = double.NaN }), medium) == AdvisoryStatus.NeedsBenchmark, "NaN cannot pass thresholds.");
        Check(Status(Advice(Record(extra) with { Samples = 0 }), extra) == AdvisoryStatus.Rejected, "Invalid records cannot override an over-budget estimate.");

        var doubled = HardwareAdvisor.Recommend(hardware, models, [], options with { TranslationInstances = 2 }, now);
        Check(Status(doubled, medium) == AdvisoryStatus.Rejected, "Separate loaded translation instances duplicate resident memory.");
        Check(doubled.Warnings.Any(w => w.Contains("extra GPU compute")), "A second residency is not represented as more compute capacity.");
        var occupied = hardware with { Gpus = [gpu with { AvailableVramMiB = 10000 }] };
        var limited = HardwareAdvisor.Recommend(occupied, models, [], options, now);
        Check(limited.AppVramBudgetMiB == 8976 && limited.ModelId is null, "Current free VRAM caps the static total-memory estimate.");
        var unknown = HardwareAdvisor.Recommend(hardware with { Gpus = [] }, models, [], options, now);
        Check(unknown.ModelId is null && unknown.Warnings.Any(w => w.Contains("no usable")), "Missing GPU inventory fails closed for GPU advice.");
        var cpu = HardwareAdvisor.Recommend(hardware with { Gpus = [] }, models, [], options with { Backend = LocalComputeBackend.Cpu }, now);
        Check(cpu.ProvisionalModelId == dedicatedSmall && cpu.AppVramBudgetMiB == 0 && cpu.NeedsBenchmark,
            "CPU-only least-memory advice remains unmeasured and does not invent GPU memory or realtime speed.");
        var lowRam = HardwareAdvisor.Recommend(hardware with { AvailableRamMiB = 16000 }, models, [], options, now);
        Check(lowRam.ModelId is null, "Busy system RAM can prevent a configuration fitting nominal capacity.");
        var amd = HardwareAdvisor.Recommend(hardware with { Gpus = [gpu with { VendorId = 0x1002 }] }, models, [], options with { Backend = LocalComputeBackend.Cuda }, now);
        Check(amd.ModelId is null, "CUDA is not offered for an AMD adapter.");
        Check(HardwareAdvisor.ConfigurationKey(options, gpu.Id) != HardwareAdvisor.ConfigurationKey(options with { LanguagePair = "ja-en" }, gpu.Id), "Language-pair quality results have independent keys.");
        Check(HardwareAdvisor.ConfigurationKey(options, gpu.Id) != HardwareAdvisor.ConfigurationKey(options with { TranslationConcurrency = 2 }, gpu.Id), "Compute contention measurement has an independent key.");
        Check(HardwareAdvisor.ConfigurationKey(options, gpu.Id) != HardwareAdvisor.ConfigurationKey(options with { RuntimeVersion = "updated-runtime" }, gpu.Id), "Runtime upgrades require new measurements.");
        Check(HardwareAdvisor.ConfigurationKey(options, gpu.Id) != HardwareAdvisor.ConfigurationKey(options with { EnableThinking = true }, gpu.Id), "Reasoning mode has an independent latency key.");
        Check(HardwareAdvisor.ConfigurationKey(options, gpu.Id) != HardwareAdvisor.ConfigurationKey(options with { SpeechQuietMilliseconds = 1800 }, gpu.Id), "Endpoint delay is part of end-to-end latency configuration.");
        Check(HardwareAdvisor.ConfigurationKey(options, gpu.Id) != HardwareAdvisor.ConfigurationKey(options with { TranslationArtifactIdentity = "changed-file-hash" }, gpu.Id), "Model artifact changes invalidate measurements.");
        Check(hardware.Fingerprint != (hardware with { Gpus = [gpu with { DriverIdentity = "new-driver" }] }).Fingerprint, "Driver package changes invalidate hardware measurements.");
        Check(unmeasured.Settings.EnableThinking == false, "Initial translation settings use non-thinking mode.");

        var runner = new SyntheticRunner(Record(small));
        var coordinator = new LocalBenchmarkCoordinator();
        var request = new LocalBenchmarkRequest(hardware, models.Single(model => model.Id == small), options, true, TimeSpan.FromSeconds(1), true, true);
        async Task<bool> Fails(Func<Task> operation)
        { try { await operation(); return false; } catch (InvalidOperationException) { return true; } }
        Check(await Fails(() => coordinator.StartAsync(request, BenchmarkStartPermission.None, runner, cancellationToken)), "No consent means no benchmark.");
        Check(runner.Opens == 0 && runner.Measures == 0, "Denied benchmark never opens a runtime.");
        Check(await Fails(() => coordinator.StartAsync(request with { ModelIsInstalled = false }, BenchmarkStartPermission.ExplicitUserRequest, runner, cancellationToken)), "Uninstalled model is not downloaded automatically.");
        Check(await Fails(() => coordinator.StartAsync(request with { FixtureSetIsInstalled = false }, BenchmarkStartPermission.ExplicitUserRequest, runner, cancellationToken)), "Missing local fixtures cannot trigger live capture.");
        Check(runner.Opens == 0, "Missing local assets never open the runtime.");
        _ = await coordinator.StartAsync(request, BenchmarkStartPermission.ExplicitUserRequest, runner, cancellationToken);
        Check(runner.Opens == 1 && runner.Measures == 1 && runner.Disposals == 1, "Explicit mock benchmark starts once and disposes its owned session.");
        var cancelling = new SyntheticRunner(Record(small), waitForCancellation: true);
        bool cancelled = false;
        try { await coordinator.StartAsync(request with { MaximumDuration = TimeSpan.FromMilliseconds(10) }, BenchmarkStartPermission.ExplicitUserRequest, cancelling, cancellationToken); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && cancelling.Disposals == 1, "Bounded mock cancellation is awaited and disposed.");
        return checks;
    }

    private sealed class SyntheticRunner(LocalBenchmarkRecord result, bool waitForCancellation = false) : ILocalBenchmarkRunner
    {
        private readonly LocalBenchmarkRecord Result = result;
        private readonly bool WaitForCancellation = waitForCancellation;
        public int Opens, Measures, Disposals;
        public Task<ILocalBenchmarkSession> OpenAsync(LocalBenchmarkRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Opens++; return Task.FromResult<ILocalBenchmarkSession>(new Session(this)); }
        private sealed class Session(SyntheticRunner owner) : ILocalBenchmarkSession
        {
            public async Task<LocalBenchmarkRecord> MeasureAsync(LocalBenchmarkRequest request, CancellationToken cancellationToken)
            {
                owner.Measures++;
                if (owner.WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return owner.Result;
            }
            public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
        }
    }
}

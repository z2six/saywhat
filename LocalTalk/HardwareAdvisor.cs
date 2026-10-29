using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace IncomingTranslator;

public enum LocalComputeBackend { Cpu, Vulkan, Cuda }
public enum AdvisoryStatus { MeasuredReady, NeedsBenchmark, Rejected }

public sealed record LocalGpuProfile(string Id, string Name, uint VendorId,
    long DedicatedVramMiB, long? AvailableVramMiB = null)
{
    public string DriverIdentity { get; init; } = "unknown";
    // Vendor presence is not proof that a particular runtime/driver is compatible.
    public bool IsNvidia => VendorId == 0x10de;
}

public sealed record LocalHardwareProfile(string CpuName, int LogicalProcessors,
    long PhysicalRamMiB, long AvailableRamMiB, IReadOnlyList<LocalGpuProfile> Gpus,
    IReadOnlyList<string> Warnings)
{
    public string SystemSoftwareIdentity { get; init; } = RuntimeInformation.OSDescription;
    public string Fingerprint
    {
        get
        {
            string value = CpuName + "|" + LogicalProcessors + "|" + PhysicalRamMiB + "|" + SystemSoftwareIdentity + "|" +
                string.Join(";", Gpus.OrderBy(g => g.Id, StringComparer.Ordinal)
                    .Select(g => g.Id + ":" + g.DedicatedVramMiB + ":" + g.DriverIdentity));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }
}

/// <summary>Metadata-only Windows inventory; never loads a model or starts a benchmark.</summary>
public static class LocalHardwareDetector
{
    public static Task<LocalHardwareProfile> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Detect, cancellationToken);

    public static LocalHardwareProfile Detect()
    {
        var warnings = new List<string>();
        long totalRam = 0, availableRam = 0;
        var gpus = new List<LocalGpuProfile>();
        if (!OperatingSystem.IsWindows())
            warnings.Add("Native hardware inventory is Windows-only; supply a profile to the offline advisor.");
        else
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory))
            {
                totalRam = (long)(memory.TotalPhysical / (1024 * 1024));
                availableRam = (long)(memory.AvailablePhysical / (1024 * 1024));
            }
            else warnings.Add("Windows did not return physical memory information.");
            ReadAdapters(gpus, warnings);
        }
        string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ??
            RuntimeInformation.ProcessArchitecture.ToString();
        return new(cpu, Environment.ProcessorCount, totalRam, availableRam, gpus, warnings);
    }

    [SupportedOSPlatform("windows")]
    private static void ReadAdapters(List<LocalGpuProfile> gpus, List<string> warnings)
    {
        DxgiFactory1? factory = null;
        try
        {
            Guid iid = typeof(DxgiFactory1).GUID;
            int result = CreateDXGIFactory1(ref iid, out factory);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            for (uint index = 0; ; index++)
            {
                DxgiAdapter1? adapter = null;
                try
                {
                    result = factory.EnumAdapters1(index, out adapter);
                    if (result == unchecked((int)0x887a0002)) break; // DXGI_ERROR_NOT_FOUND
                    if (result < 0) Marshal.ThrowExceptionForHR(result);
                    result = adapter.GetDesc1(out var description);
                    if (result < 0) Marshal.ThrowExceptionForHR(result);
                    if ((description.Flags & 2) != 0) continue; // Software adapter.
                    long vram = (long)(description.DedicatedVideoMemory.ToUInt64() / (1024 * 1024));
                    // Do not use Win32_VideoController.AdapterRAM: its UInt32 wraps above 4 GiB.
                    string id = $"{description.VendorId:x4}:{description.DeviceId:x4}:{description.SubSysId:x8}:{description.Description.Trim()}";
                    if (gpus.Any(g => g.Id == id)) id += ":" + index;
                    // DXGI returns the package's UMD version without creating a GPU device.
                    Guid deviceInterface = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
                    string driverIdentity = adapter.CheckInterfaceSupport(ref deviceInterface, out long driverVersion) >= 0
                        ? driverVersion.ToString("X16") : "unknown";
                    gpus.Add(new(id, description.Description.Trim(), description.VendorId, vram)
                        { DriverIdentity = driverIdentity });
                }
                finally { if (adapter is not null) Marshal.ReleaseComObject(adapter); }
            }
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException)
        { warnings.Add("GPU inventory unavailable: " + error.Message); }
        finally { if (factory is not null) Marshal.ReleaseComObject(factory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    // All inherited COM slots must be present, even though only the final methods are used.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DxgiFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr window, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr window);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr chain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint index, out DxgiAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DxgiAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(IntPtr description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid iid, out long version);
        [PreserveSig] int GetDesc1(out AdapterDescription1 description);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out DxgiFactory1 factory);
}

/// <summary>Conservative planning estimates, not a performance or accuracy claim.</summary>
public sealed record LocalTranslationProfile(string Id, string DisplayName,
    long EstimatedResidentVramMiB, long EstimatedResidentRamMiB);

public sealed record LocalAdvisorOptions
{
    public LocalComputeBackend Backend { get; init; } = LocalComputeBackend.Vulkan;
    public string? GpuId { get; init; }
    public string SpeechModelId { get; init; } = "voxtral-realtime-q4_k_m";
    public int SpeechInstances { get; init; } = 2;
    public int TranslationInstances { get; init; } = 1;
    public int TranslationConcurrency { get; init; } = 1;
    public int ContextTokens { get; init; } = 2048;
    public int OutputTokens { get; init; } = 256;
    public bool EnableThinking { get; init; }
    public int SpeechQuietMilliseconds { get; init; } = 1200;
    public int TranscriptionDelayMilliseconds { get; init; } = 160;
    public string LanguagePair { get; init; } = "auto-en";
    public string FixtureSetId { get; init; } = "localtalk-conversation-v1";
    public string RuntimeVersion { get; init; } = "llama-b11401";
    // The eventual benchmark adapter must fill identities from the verified local files/build.
    public string TranslationArtifactIdentity { get; init; } = "pinned-catalog";
    public string SpeechArtifactIdentity { get; init; } = "pinned-catalog";
    public string SpeechRuntimeIdentity { get; init; } = "local-build";
    public string PipelineVersion { get; init; } = "finite-turn-final-only-v1";
    public long SpeechVramMiBPerInstance { get; init; } = 8192;
    public long SpeechRamMiBPerInstance { get; init; } = 8192;
    public long RuntimeReserveMiB { get; init; } = 1024;
    public long? GameVramReserveMiB { get; init; }
    public long GameRamReserveMiB { get; init; } = 8192;
    public long FreeMemorySafetyMiB { get; init; } = 1024;
    public double MaximumP95FinalLatencyMs { get; init; } = 4000;
    public double MaximumAsrRealTimeFactor { get; init; } = 0.8;
    public double MaximumAsrLagMs { get; init; } = 1000;
    public int MinimumBenchmarkSamples { get; init; } = 20;
    public bool RequireAlongsideGameMeasurement { get; init; } = true;
    public TimeSpan MaximumMeasurementAge { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>The peak memory includes all SayWhat? ASR/translation instances, not just weights.</summary>
public sealed record LocalBenchmarkRecord(string HardwareFingerprint, string TranslationModelId,
    string ConfigurationKey, DateTimeOffset RecordedAt, bool AlongsideGame, int Samples,
    double P95FinalLatencyMs, double AsrRealTimeFactor, double MaximumAsrLagMs,
    long PeakAppVramMiB, long PeakAppRamMiB, int DroppedAudioFrames, int Failures,
    bool QualityFixturePassed);

public sealed record LocalRankedRecommendation(string ModelId, AdvisoryStatus Status,
    long PlannedAppVramMiB, long PlannedAppRamMiB, double? MeasuredP95FinalLatencyMs,
    string Reason);

public sealed record LocalRecommendedSettings(LocalComputeBackend Backend, int CpuThreads,
    int SpeechInstances, int TranslationInstances, int TranslationConcurrency,
    int ContextTokens, int OutputTokens, bool EnableThinking, int SpeechQuietMilliseconds,
    int TranscriptionDelayMilliseconds, string SpeechModelId, string? TranslationModelId);

public sealed record LocalHardwareRecommendation(string? RecommendedModelId,
    string? ProvisionalModelId, string? SelectedGpuId, long AppVramBudgetMiB,
    long AppRamBudgetMiB, long GameVramReserveMiB, int SuggestedCpuThreads,
    IReadOnlyList<LocalRankedRecommendation> Candidates, IReadOnlyList<string> Warnings,
    LocalRecommendedSettings Settings)
{
    public bool NeedsBenchmark => RecommendedModelId is null && ProvisionalModelId is not null;
    public bool IsProvisional => RecommendedModelId is null;
    public string? ModelId => RecommendedModelId ?? ProvisionalModelId;
    public string Title => RecommendedModelId is not null ? "Measured local configuration" :
        ProvisionalModelId is not null ? "Provisional — benchmark when you are ready" : "No configuration fits the reserved budget";
    public string Explanation => (Candidates.FirstOrDefault(c => c.ModelId == ModelId)?.Reason ??
        "Keep translation stopped; reduce residency or adjust the explicit game reserve.") +
        $" App GPU budget: {AppVramBudgetMiB} MiB; game reserve: {GameVramReserveMiB} MiB. " +
        string.Join(" ", Warnings);
}

public static class HardwareAdvisor
{
    // Match the pinned installer catalog. These estimates deliberately include room
    // beyond file size for context/cache/workspace and must be replaced by measurements.
    public static IReadOnlyList<LocalTranslationProfile> InitialTranslationProfiles { get; } =
    [
        // Recommend dedicated translators only. Existing/manual general-purpose choices remain usable,
        // but more VRAM alone is never evidence that the larger translator will meet gaming latency.
        new("hy-mt2-1.8b-q4_k_m", "Hy-MT2 1.8B — speed-first starting choice (end-to-end unmeasured)", 2048, 3072),
        new("hy-mt2-7b-q4_k_m", "Hy-MT2 7B — larger quality candidate (end-to-end unmeasured)", 6144, 7168)
    ];

    public static Task<LocalHardwareProfile> DetectAsync(CancellationToken cancellationToken = default) =>
        LocalHardwareDetector.DetectAsync(cancellationToken);

    public static LocalHardwareRecommendation Recommend(LocalHardwareProfile hardware,
        IReadOnlyList<LocalBenchmarkRecord>? measurements = null, LocalAdvisorOptions? options = null) =>
        Recommend(hardware, InitialTranslationProfiles, measurements ?? [], options ?? new(), DateTimeOffset.UtcNow);

    public static string ConfigurationKey(LocalAdvisorOptions options, string? selectedGpuId) =>
        string.Join("|", options.Backend, selectedGpuId ?? "cpu", options.SpeechModelId,
            options.SpeechInstances, options.TranslationInstances, options.TranslationConcurrency,
            options.ContextTokens, options.OutputTokens, options.EnableThinking,
            options.SpeechQuietMilliseconds, options.TranscriptionDelayMilliseconds,
            options.LanguagePair, options.FixtureSetId, options.RuntimeVersion,
            options.TranslationArtifactIdentity, options.SpeechArtifactIdentity,
            options.SpeechRuntimeIdentity, options.PipelineVersion);

    public static LocalHardwareRecommendation Recommend(LocalHardwareProfile hardware,
        IReadOnlyList<LocalTranslationProfile> models, IReadOnlyList<LocalBenchmarkRecord> measurements,
        LocalAdvisorOptions options, DateTimeOffset now)
    {
        Validate(options);
        var warnings = new List<string>(hardware.Warnings);
        LocalGpuProfile? gpu = options.Backend == LocalComputeBackend.Cpu ? null :
            options.GpuId is not null ? hardware.Gpus.FirstOrDefault(g => g.Id == options.GpuId) :
            hardware.Gpus.OrderByDescending(g => g.DedicatedVramMiB).FirstOrDefault();
        bool usableGpu = gpu is not null && gpu.DedicatedVramMiB > 0 &&
            (options.Backend != LocalComputeBackend.Cuda || gpu.IsNvidia);
        long gameVram = options.GameVramReserveMiB ??
            Math.Max(4096, (gpu?.DedicatedVramMiB ?? 0) / 4);
        long vramBudget = usableGpu ? Math.Max(0, gpu!.DedicatedVramMiB - gameVram) : 0;
        if (gpu?.AvailableVramMiB is long freeVram)
            vramBudget = Math.Min(vramBudget, Math.Max(0, freeVram - options.FreeMemorySafetyMiB));
        long ramBudget = Math.Max(0, hardware.PhysicalRamMiB - options.GameRamReserveMiB);
        ramBudget = Math.Min(ramBudget, Math.Max(0, hardware.AvailableRamMiB - options.FreeMemorySafetyMiB));
        if (options.Backend != LocalComputeBackend.Cpu && !usableGpu)
            warnings.Add("The selected GPU/backend has no usable dedicated memory inventory; choose CPU or verify the runtime manually.");
        if (options.Backend == LocalComputeBackend.Cuda && usableGpu)
            warnings.Add("NVIDIA hardware was detected; CUDA runtime/driver compatibility still requires the explicit setup check.");
        warnings.Add("VRAM capacity is not speed. Advice never starts engines, downloads models, or applies settings.");
        if (gpu?.DriverIdentity == "unknown")
            warnings.Add("GPU driver identity is unavailable; explicitly repeat measurements after driver updates.");
        if (options.TranslationInstances == 1)
            warnings.Add("Shared translation weights save residency; each direction must still send an independent stateless request/context. GPU compute is shared.");
        else warnings.Add("Separate translation instances duplicate weights/caches on the same GPU; they do not create extra GPU compute capacity.");

        string key = ConfigurationKey(options, gpu?.Id);
        var ranked = new List<LocalRankedRecommendation>();
        foreach (var model in models)
        {
            if (model.EstimatedResidentVramMiB < 0 || model.EstimatedResidentRamMiB < 0)
                throw new ArgumentException("Resident memory estimates cannot be negative.", nameof(models));
            long plannedVram = options.Backend == LocalComputeBackend.Cpu ? 0 : checked(
                options.SpeechVramMiBPerInstance * options.SpeechInstances +
                model.EstimatedResidentVramMiB * options.TranslationInstances + options.RuntimeReserveMiB);
            long plannedRam = checked(options.SpeechRamMiBPerInstance * options.SpeechInstances +
                model.EstimatedResidentRamMiB * options.TranslationInstances + options.RuntimeReserveMiB);
            bool estimatedFits = plannedRam <= ramBudget && (options.Backend == LocalComputeBackend.Cpu ||
                usableGpu && plannedVram <= vramBudget);
            var matching = measurements.Where(m => m.HardwareFingerprint == hardware.Fingerprint &&
                m.TranslationModelId == model.Id && m.ConfigurationKey == key &&
                m.RecordedAt <= now && now - m.RecordedAt <= options.MaximumMeasurementAge &&
                (!options.RequireAlongsideGameMeasurement || m.AlongsideGame))
                .OrderByDescending(m => m.RecordedAt).FirstOrDefault();
            if (matching is not null)
            {
                bool finite = double.IsFinite(matching.P95FinalLatencyMs) && matching.P95FinalLatencyMs > 0 &&
                    double.IsFinite(matching.AsrRealTimeFactor) && matching.AsrRealTimeFactor > 0 &&
                    double.IsFinite(matching.MaximumAsrLagMs) && matching.MaximumAsrLagMs >= 0;
                if (matching.Samples < options.MinimumBenchmarkSamples || !finite ||
                    matching.PeakAppVramMiB < 0 || matching.PeakAppRamMiB <= 0 ||
                    matching.DroppedAudioFrames < 0 || matching.Failures < 0)
                {
                    ranked.Add(new(model.Id, estimatedFits ? AdvisoryStatus.NeedsBenchmark : AdvisoryStatus.Rejected,
                        plannedVram, plannedRam, null, estimatedFits
                            ? "Matching measurements are incomplete or invalid; repeat the explicit fixture benchmark."
                            : "Invalid measurements cannot override a memory estimate that exceeds the reserved budget."));
                    continue;
                }
                string? failure = !matching.QualityFixturePassed ? "Language-pair quality fixtures did not pass." :
                    matching.Failures != 0 || matching.DroppedAudioFrames != 0 ? "The fixture run failed or dropped audio." :
                    matching.P95FinalLatencyMs > options.MaximumP95FinalLatencyMs ? "Measured final latency exceeds the target." :
                    matching.AsrRealTimeFactor > options.MaximumAsrRealTimeFactor || matching.MaximumAsrLagMs > options.MaximumAsrLagMs
                        ? "Measured speech processing cannot maintain the required lag headroom." :
                    matching.PeakAppRamMiB > ramBudget ? "Measured app memory would consume the RAM reserve." :
                    options.Backend != LocalComputeBackend.Cpu && (!usableGpu || matching.PeakAppVramMiB <= 0 || matching.PeakAppVramMiB > vramBudget)
                        ? "Measured app memory would consume the GPU/game reserve." : null;
                ranked.Add(new(model.Id, failure is null ? AdvisoryStatus.MeasuredReady : AdvisoryStatus.Rejected,
                    matching.PeakAppVramMiB, matching.PeakAppRamMiB, matching.P95FinalLatencyMs,
                    failure ?? "Matching quality, latency, audio-integrity, and game-headroom measurements passed."));
            }
            else
            {
                ranked.Add(new(model.Id, estimatedFits ? AdvisoryStatus.NeedsBenchmark : AdvisoryStatus.Rejected,
                    plannedVram, plannedRam, null, estimatedFits
                        ? "Conservative memory estimate fits; speed and language-pair quality are unmeasured."
                        : "Conservative memory estimate exceeds the available app/game budget."));
            }
        }
        // Accuracy is a fixture gate, never inferred from parameter count. Among
        // passing runs, prefer actual final latency; provisional candidates use least memory.
        ranked = ranked.OrderBy(r => r.Status).ThenBy(r => r.MeasuredP95FinalLatencyMs ?? double.MaxValue)
            .ThenBy(r => options.Backend == LocalComputeBackend.Cpu ? r.PlannedAppRamMiB : r.PlannedAppVramMiB)
            .ThenBy(r => r.ModelId, StringComparer.Ordinal).ToList();
        string? recommended = ranked.FirstOrDefault(r => r.Status == AdvisoryStatus.MeasuredReady)?.ModelId;
        string? provisional = ranked.FirstOrDefault(r => r.Status == AdvisoryStatus.NeedsBenchmark)?.ModelId;
        int threads = Math.Clamp(hardware.LogicalProcessors / 4, 1, 8);
        return new(recommended, provisional, gpu?.Id, vramBudget, ramBudget, gameVram, threads, ranked, warnings,
            new(options.Backend, threads, options.SpeechInstances, options.TranslationInstances,
                options.TranslationConcurrency, options.ContextTokens, options.OutputTokens, options.EnableThinking,
                options.SpeechQuietMilliseconds, options.TranscriptionDelayMilliseconds,
                options.SpeechModelId, recommended ?? provisional));
    }

    private static void Validate(LocalAdvisorOptions options)
    {
        if (options.SpeechInstances is < 1 or > 2 || options.TranslationInstances is < 1 or > 2 ||
            options.TranslationConcurrency is < 1 or > 2 || options.ContextTokens < 256 || options.OutputTokens < 1 ||
            options.SpeechQuietMilliseconds < 0 || options.TranscriptionDelayMilliseconds < 0 ||
            options.SpeechVramMiBPerInstance < 0 || options.SpeechRamMiBPerInstance < 0 ||
            options.RuntimeReserveMiB < 0 || options.GameVramReserveMiB < 0 || options.GameRamReserveMiB < 0 ||
            options.FreeMemorySafetyMiB < 0 || options.MinimumBenchmarkSamples < 1 ||
            !double.IsFinite(options.MaximumP95FinalLatencyMs) || options.MaximumP95FinalLatencyMs <= 0 ||
            !double.IsFinite(options.MaximumAsrRealTimeFactor) || options.MaximumAsrRealTimeFactor <= 0 ||
            !double.IsFinite(options.MaximumAsrLagMs) || options.MaximumAsrLagMs < 0 ||
            options.MaximumMeasurementAge <= TimeSpan.Zero)
            throw new ArgumentException("Invalid hardware recommendation policy.", nameof(options));
    }
}

public sealed record LocalBenchmarkRequest(LocalHardwareProfile Hardware, LocalTranslationProfile Model,
    LocalAdvisorOptions Options, bool AlongsideGame, TimeSpan MaximumDuration,
    bool ModelIsInstalled = false, bool FixtureSetIsInstalled = false);

/// <summary>Implementations may use only installed local assets and prerecorded fixtures.
/// They must stop their owned engine work on cancellation/disposal, never route to the
/// chatbox, capture live audio, download assets, or fall back to a cloud service.</summary>
public interface ILocalBenchmarkSession : IAsyncDisposable
{
    Task<LocalBenchmarkRecord> MeasureAsync(LocalBenchmarkRequest request, CancellationToken cancellationToken);
}

public interface ILocalBenchmarkRunner
{
    Task<ILocalBenchmarkSession> OpenAsync(LocalBenchmarkRequest request, CancellationToken cancellationToken);
}

public enum BenchmarkStartPermission { None, ExplicitUserRequest }

/// <summary>No automatic runner is installed. Only an explicit UI action can call this entry point.</summary>
public sealed class LocalBenchmarkCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<LocalBenchmarkRecord> StartAsync(LocalBenchmarkRequest request,
        BenchmarkStartPermission permission, ILocalBenchmarkRunner runner, CancellationToken cancellationToken)
    {
        if (permission != BenchmarkStartPermission.ExplicitUserRequest)
            throw new InvalidOperationException("A benchmark requires an explicit user Start benchmark action.");
        if (!request.ModelIsInstalled || !request.FixtureSetIsInstalled)
            throw new InvalidOperationException("Select an installed local model and installed prerecorded fixtures first; benchmarking never downloads them.");
        if (request.MaximumDuration <= TimeSpan.Zero || request.MaximumDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Benchmark duration must be bounded to at most five minutes.", nameof(request));
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A benchmark is already running.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(request.MaximumDuration);
            await using var session = await runner.OpenAsync(request, deadline.Token);
            // Cooperative cancellation is intentional: do not abandon a GPU worker
            // and tell the user it stopped. Session disposal must await actual shutdown.
            return await session.MeasureAsync(request, deadline.Token);
        }
        finally { gate.Release(); }
    }
}

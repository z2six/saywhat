using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace IncomingTranslator;

internal sealed record AutomaticSetupPlan(string Summary, long DownloadBytes,
    bool RequiresAdministrator, bool RestartMayBeNeeded)
{
    // Linux packages/toolkits are outside the pinned-model download estimate.
    public bool SpeechSetupRequired { get; init; }
}
internal sealed record AutomaticSetupResult(bool Completed, bool NeedsRestart, string Message);
internal sealed record AutomaticSetupSnapshot(IReadOnlySet<string> ModelIds, IReadOnlySet<string> RuntimeIds,
    IReadOnlySet<string> DistributionNames, WslSpeechRegistration? Speech, bool NativeRuntimeReady,
    bool SpeakerModelReady, bool HasNvidiaGpu)
{
    public bool ManagedSpeechModelMissing { get; init; }
}
internal enum SetupPrerequisiteResult { Ready, RestartRequired }
internal sealed class SetupRestartRequiredException : Exception;

// The coordinator is independent of WPF so the complete fresh-install/retry/cancel path
// can be exercised with a fake platform, without installing Windows features or models.
internal interface IAutomaticSetupPlatform
{
    Task<AutomaticSetupSnapshot> InspectAsync(AppSettings settings, CancellationToken token);
    Task<SetupPrerequisiteResult> EnsureNativeRuntimeAsync(IProgress<ProvisionProgress>? progress, CancellationToken token);
    Task<SetupPrerequisiteResult> EnsureWindowsSpeechAsync(IProgress<ProvisionProgress>? progress, CancellationToken token);
    Task EnsureDistributionAsync(string name, IProgress<ProvisionProgress>? progress, CancellationToken token);
    Task EnsureModelAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token);
    Task EnsureRuntimeAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token);
    Task<string> EnsureSpeakerModelAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token);
    Task ValidateSpeechAsync(WslSpeechRegistration speech, CancellationToken token);
    Task BuildSpeechAsync(string distribution, string backend, IProgress<ProvisionProgress>? progress, CancellationToken token);
    long PendingOwnNativeSpeechBytes(AppSettings settings) => throw new NotSupportedException("This setup platform does not support the selected speech recognizer.");
    bool IsOwnNativeSpeechReady(AppSettings settings) => false;
    Task EnsureOwnNativeSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token) =>
        throw new NotSupportedException("This setup platform does not support the selected speech recognizer.");
    long PendingIncomingNativeSpeechBytes(AppSettings settings) =>
        PendingOwnNativeSpeechBytes(IncomingRecognitionCatalog.CreateNativeSettings(settings));
    bool IsIncomingNativeSpeechReady(AppSettings settings) =>
        IsOwnNativeSpeechReady(IncomingRecognitionCatalog.CreateNativeSettings(settings));
    Task EnsureIncomingNativeSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token) =>
        EnsureOwnNativeSpeechAsync(IncomingRecognitionCatalog.CreateNativeSettings(settings), progress, token);
}

internal sealed class AutomaticSetupService
{
    private readonly IAutomaticSetupPlatform _platform;
    private readonly string _statePath;
    private readonly bool _ownVoiceOnly;
    private readonly bool _incomingVoiceOnly;
    private readonly SemaphoreSlim _changes = new(1, 1);
    public AutomaticSetupService(LocalTalkProvisioner provisioner, bool ownVoiceOnly = false, bool incomingVoiceOnly = false)
        : this(new WindowsAutomaticSetupPlatform(provisioner), provisioner.DataRoot, ownVoiceOnly, incomingVoiceOnly) { }
    internal AutomaticSetupService(IAutomaticSetupPlatform platform, string dataRoot, bool ownVoiceOnly = false, bool incomingVoiceOnly = false)
    {
        if (ownVoiceOnly && incomingVoiceOnly) throw new ArgumentException("Choose one independent setup direction.");
        _platform = platform; _ownVoiceOnly = ownVoiceOnly; _incomingVoiceOnly = incomingVoiceOnly;
        _statePath = Path.Combine(Path.GetFullPath(dataRoot), ownVoiceOnly ? "own-voice-setup-progress.json"
            : incomingVoiceOnly ? "incoming-voice-setup-progress.json" : "setup-progress.json");
    }

    private bool UsesOwnNativeSpeech(AppSettings settings) => _ownVoiceOnly && settings.OwnRecognitionEngine != "voxtral";
    private bool UsesIncomingNativeSpeech(AppSettings settings) => _incomingVoiceOnly && IncomingRecognitionCatalog.UsesNative(settings);
    private bool UsesNativeSpeech(AppSettings settings) => UsesOwnNativeSpeech(settings) || UsesIncomingNativeSpeech(settings);
    private long PendingNativeSpeechBytes(AppSettings settings) => UsesIncomingNativeSpeech(settings)
        ? _platform.PendingIncomingNativeSpeechBytes(settings) : _platform.PendingOwnNativeSpeechBytes(settings);
    private bool IsNativeSpeechReady(AppSettings settings) => UsesIncomingNativeSpeech(settings)
        ? _platform.IsIncomingNativeSpeechReady(settings) : _platform.IsOwnNativeSpeechReady(settings);
    private bool NeedsSpeakerAssets(AppSettings settings) => !_ownVoiceOnly && settings.UseAiSpeakerRecognition &&
        (!_incomingVoiceOnly || !settings.IncomingSinglePerson);

    // Inspection does not launch WSL, a speech engine, a translator, or an audio device.
    public async Task<AutomaticSetupPlan> PlanAsync(AppSettings settings, CancellationToken token = default)
    {
        ValidateSelections(settings, _ownVoiceOnly, _incomingVoiceOnly);
        AutomaticSetupSnapshot snapshot = await _platform.InspectAsync(settings, token);
        bool nativeSpeech = UsesNativeSpeech(settings);
        string[] models = UsesBuiltInTranslation(settings) ? TranslationIds(settings, _ownVoiceOnly, _incomingVoiceOnly) : [];
        long bytes = models.Where(id => !snapshot.ModelIds.Contains(id))
            .Sum(id => LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == id).SizeBytes);
        if (nativeSpeech) bytes += PendingNativeSpeechBytes(settings);
        else if (snapshot.ManagedSpeechModelMissing || (snapshot.Speech is null && !snapshot.ModelIds.Contains("voxtral-realtime-q4_k_m")))
            bytes += LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == "voxtral-realtime-q4_k_m").SizeBytes;
        if (UsesBuiltInTranslation(settings) && !snapshot.RuntimeIds.Contains(settings.ManagedRuntimeId))
            bytes += LocalTalkProvisioner.RuntimeCatalog.Single(runtime => runtime.Id == settings.ManagedRuntimeId).SizeBytes;
        if (NeedsSpeakerAssets(settings) && !snapshot.SpeakerModelReady) bytes += WindowsAutomaticSetupPlatform.SpeakerAsset.SizeBytes;
        if (!snapshot.NativeRuntimeReady) bytes += WindowsAutomaticSetupPlatform.NativeRuntimeAsset.SizeBytes;
        string summary = bytes > 0 ? $"Download about {bytes / 1e9:0.0} GB of translation files. " : "Reuse the translation files already on this PC. ";
        summary += nativeSpeech ? "Prepare your selected speech recognizer on Windows. No Linux component is needed."
            : snapshot.Speech is null
            ? "Set up the speech component automatically. This also downloads Windows/Linux components and build tools; an NVIDIA setup can need several more GB and at least 20 GB of free space. Windows may ask for permission or a restart."
            : snapshot.ManagedSpeechModelMissing
                ? "Restore the missing speech model for your existing speech component. Keep its registered engine and save the selected languages."
                : "Check your existing speech component and save the selected languages.";
        if (!UsesBuiltInTranslation(settings)) summary += " Keep your existing translation connection.";
        if (!snapshot.NativeRuntimeReady) summary += " Install the Microsoft runtime if needed.";
        summary += _incomingVoiceOnly ? " Live subtitles stay stopped until you press Start. Your own-voice choices are kept."
            : " Translation stays stopped until you press Start.";
        return new(summary, bytes, !snapshot.NativeRuntimeReady || (!nativeSpeech && snapshot.Speech is null),
            !snapshot.NativeRuntimeReady || (!nativeSpeech && snapshot.Speech is null))
            { SpeechSetupRequired = nativeSpeech ? !IsNativeSpeechReady(settings) : snapshot.Speech is null };
    }

    public async Task<AutomaticSetupResult> RunAsync(AppSettings settings, bool explicitConsent,
        IProgress<ProvisionProgress>? progress = null, CancellationToken token = default)
    {
        if (!explicitConsent) throw new SetupUserException("Confirm setup before downloading files or changing Windows components.");
        ValidateSelections(settings, _ownVoiceOnly, _incomingVoiceOnly);
        await _changes.WaitAsync(token);
        try
        {
            SetupCheckpoint state = LoadCheckpoint();
            if (state.RestartRequestedAt is DateTimeOffset requested && !RebootedSince(requested))
                return new(false, true, "Restart Windows, then open SayWhat? and choose Continue setup.");
            state.RestartRequestedAt = null;
            await Stage("Checking this PC", state, progress, token);
            AutomaticSetupSnapshot snapshot = await _platform.InspectAsync(settings, token);
            bool nativeSpeech = UsesNativeSpeech(settings);
            if (!snapshot.NativeRuntimeReady)
            {
                await Stage("Preparing Windows components", state, progress, token);
                if (await _platform.EnsureNativeRuntimeAsync(progress, token) == SetupPrerequisiteResult.RestartRequired)
                    return await PauseForRestart(state, token);
            }
            if (!nativeSpeech && snapshot.Speech is null)
            {
                await Stage("Preparing the speech component", state, progress, token);
                if (await _platform.EnsureWindowsSpeechAsync(progress, token) == SetupPrerequisiteResult.RestartRequired)
                    return await PauseForRestart(state, token);
                if (state.DistributionName is null)
                {
                    // Never take over, convert, unregister, or change the default of somebody else's distro.
                    state.DistributionName = snapshot.DistributionNames.Contains(AppBrand.SpeechDistributionName)
                        ? AppBrand.SpeechDistributionName + "-" + Guid.NewGuid().ToString("N")[..8] : AppBrand.SpeechDistributionName;
                    await SaveCheckpoint(state, token);
                }
                try { await _platform.EnsureDistributionAsync(state.DistributionName, progress, token); }
                catch (SetupRestartRequiredException) { return await PauseForRestart(state, token); }
            }
            foreach (string id in UsesBuiltInTranslation(settings) ? TranslationIds(settings, _ownVoiceOnly, _incomingVoiceOnly) : [])
            {
                token.ThrowIfCancellationRequested();
                await Stage("Downloading translation files", state, progress, token);
                await _platform.EnsureModelAsync(id, FriendlyProgress(progress, "Downloading translation files"), token);
            }
            if (UsesBuiltInTranslation(settings))
            {
                await Stage("Preparing translation", state, progress, token);
                await _platform.EnsureRuntimeAsync(settings.ManagedRuntimeId, FriendlyProgress(progress, "Preparing translation"), token);
            }
            string? speakerPath = null;
            if (NeedsSpeakerAssets(settings))
            {
                await Stage("Preparing voice recognition", state, progress, token);
                speakerPath = await _platform.EnsureSpeakerModelAsync(settings, FriendlyProgress(progress, "Preparing voice recognition"), token);
            }
            if (nativeSpeech)
            {
                await Stage("Preparing speech recognition", state, progress, token);
                if (UsesIncomingNativeSpeech(settings))
                    await _platform.EnsureIncomingNativeSpeechAsync(settings, FriendlyProgress(progress, "Preparing speech recognition"), token);
                else await _platform.EnsureOwnNativeSpeechAsync(settings, FriendlyProgress(progress, "Preparing speech recognition"), token);
            }
            else if (snapshot.Speech is WslSpeechRegistration existing)
            {
                if (snapshot.ManagedSpeechModelMissing)
                {
                    await Stage("Restoring speech recognition files", state, progress, token);
                    await _platform.EnsureModelAsync("voxtral-realtime-q4_k_m",
                        FriendlyProgress(progress, "Restoring speech recognition files"), token);
                }
                await Stage("Checking speech recognition", state, progress, token);
                // Fail visibly on a broken imported runtime instead of rebuilding or replacing it behind the user's back.
                await _platform.ValidateSpeechAsync(existing, token);
            }
            else
            {
                await Stage("Downloading speech recognition", state, progress, token);
                await _platform.EnsureModelAsync("voxtral-realtime-q4_k_m", FriendlyProgress(progress, "Downloading speech recognition"), token);
                await Stage("Installing speech recognition — this can take a while", state, progress, token);
                await _platform.BuildSpeechAsync(state.DistributionName!, snapshot.HasNvidiaGpu ? "cuda" : "vulkan",
                    progress, token);
            }
            token.ThrowIfCancellationRequested();
            state.Stage = "Ready";
            await SaveCheckpoint(state, token);
            token.ThrowIfCancellationRequested();
            if (!_incomingVoiceOnly) settings.UseManagedTranslator = UsesBuiltInTranslation(settings);
            if (speakerPath is not null) settings.SpeakerModelPath = speakerPath;
            return new(true, false, "You're ready. Choose Start on Home when you want to translate.");
        }
        finally { _changes.Release(); }
    }

    private async Task Stage(string stage, SetupCheckpoint checkpoint, IProgress<ProvisionProgress>? progress, CancellationToken token)
    { checkpoint.Stage = stage; await SaveCheckpoint(checkpoint, token); progress?.Report(new(stage, stage)); }
    private async Task<AutomaticSetupResult> PauseForRestart(SetupCheckpoint state, CancellationToken token)
    {
        state.Stage = "Restart needed"; state.RestartRequestedAt = DateTimeOffset.UtcNow;
        await SaveCheckpoint(state, token);
        return new(false, true, "Restart Windows, then open SayWhat? and choose Continue setup. Your choices are saved.");
    }
    internal static bool RebootedSince(DateTimeOffset requested) =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) > requested;
    private SetupCheckpoint LoadCheckpoint()
    {
        try { return JsonSerializer.Deserialize<SetupCheckpoint>(File.ReadAllText(_statePath)) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }
    private async Task SaveCheckpoint(SetupCheckpoint checkpoint, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        string temporary = _statePath + ".new-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(checkpoint), token); File.Move(temporary, _statePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class SetupCheckpoint
    {
        public string? DistributionName { get; set; }
        public DateTimeOffset? RestartRequestedAt { get; set; }
        public string Stage { get; set; } = "Not started";
    }
    internal static string[] TranslationIds(AppSettings settings, bool ownVoiceOnly = false, bool incomingVoiceOnly = false) =>
        (incomingVoiceOnly ? new[] { settings.ShareTranslationModel ? settings.OwnManagedModelId : settings.IncomingManagedModelId }
        : ownVoiceOnly || settings.ShareTranslationModel ? new[] { settings.OwnManagedModelId }
        : new[] { settings.OwnManagedModelId, settings.IncomingManagedModelId }).Distinct().ToArray();
    internal static bool UsesBuiltInTranslation(AppSettings settings) => settings.UseManagedTranslator || !settings.FirstRunSetupCompleted;
    internal static void ValidateSelections(AppSettings settings, bool ownVoiceOnly = false, bool incomingVoiceOnly = false)
    {
        if (ownVoiceOnly && incomingVoiceOnly) throw new ArgumentException("Choose one independent setup direction.");
        if (incomingVoiceOnly) RuntimeProfiles.ValidateIncomingSpeech(settings);
        if (!UsesBuiltInTranslation(settings))
        {
            if (!incomingVoiceOnly) RuntimeController.ValidateLocalEndpoint(settings.OwnTranslatorBaseUrl);
            if (!ownVoiceOnly) RuntimeController.ValidateLocalEndpoint(settings.TranslatorBaseUrl);
            return;
        }
        foreach (string id in TranslationIds(settings, ownVoiceOnly, incomingVoiceOnly))
            if (!LocalTalkProvisioner.ModelCatalog.Any(model => model.Id == id && model.Role == ProvisionModelRole.Translation))
                throw new SetupUserException("Choose a translation model in Setup or Advanced.");
        if (!LocalTalkProvisioner.RuntimeCatalog.Any(runtime => runtime.Id == settings.ManagedRuntimeId))
            throw new SetupUserException("Choose a supported translation engine in Advanced.");
    }
    private static IProgress<ProvisionProgress>? FriendlyProgress(IProgress<ProvisionProgress>? target, string stage) =>
        target is null ? null : new InlineSetupProgress(value => target.Report(value with
        {
            Stage = stage, Message = stage,
            Detail = value.Stage == "Verified" ? "Download verified."
                : value.Stage == "Verifying" ? "Checking downloaded files." : value.Detail
        }));
    private sealed class InlineSetupProgress(Action<ProvisionProgress> action) : IProgress<ProvisionProgress>
    { public void Report(ProvisionProgress value) => action(value); }
}

internal sealed class WindowsAutomaticSetupPlatform(LocalTalkProvisioner provisioner) : IAutomaticSetupPlatform
{
    public long PendingOwnNativeSpeechBytes(AppSettings settings) => new NativeSpeechProvisioner(provisioner.DataRoot).PendingDownloadBytes(settings);
    public bool IsOwnNativeSpeechReady(AppSettings settings) => new NativeSpeechProvisioner(provisioner.DataRoot).IsReady(settings);
    public Task EnsureOwnNativeSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token) =>
        new NativeSpeechProvisioner(provisioner.DataRoot).PrepareAsync(settings, progress, token);
    public long PendingIncomingNativeSpeechBytes(AppSettings settings) => IncomingRecognitionCatalog.UsesFireRed(settings)
        ? new FireRedSpeechProvisioner(provisioner.DataRoot).PendingDownloadBytes(settings.IncomingSpeechEngine)
        : new NativeSpeechProvisioner(provisioner.DataRoot).PendingDownloadBytes(IncomingRecognitionCatalog.CreateNativeSettings(settings));
    public bool IsIncomingNativeSpeechReady(AppSettings settings) => IncomingRecognitionCatalog.UsesFireRed(settings)
        ? new FireRedSpeechProvisioner(provisioner.DataRoot).IsReady(settings.IncomingSpeechEngine)
        : new NativeSpeechProvisioner(provisioner.DataRoot).IsReady(IncomingRecognitionCatalog.CreateNativeSettings(settings));
    public Task EnsureIncomingNativeSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token) =>
        IncomingRecognitionCatalog.UsesFireRed(settings)
            ? new FireRedSpeechProvisioner(provisioner.DataRoot).PrepareAsync(settings.IncomingSpeechEngine, progress, token)
            : new NativeSpeechProvisioner(provisioner.DataRoot).PrepareAsync(IncomingRecognitionCatalog.CreateNativeSettings(settings), progress, token);
    internal static readonly ProvisionAsset NativeRuntimeAsset = new("VC_redist.x64.exe", 18_731_856,
        "843068991daaa1f73ad9f6239bce4d0f6a07a51f18c37ea2a867e9beca71295c",
        "https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe");
    // Independently checked against the official release bytes; not an unverified size-only download.
    internal static readonly ProvisionAsset SpeakerAsset = new("3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx", 28_281_138,
        "f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx");
    public async Task<AutomaticSetupSnapshot> InspectAsync(AppSettings settings, CancellationToken token)
    {
        ProvisionInventory inventory = provisioner.InspectLocal();
        LocalHardwareProfile hardware = await LocalHardwareDetector.DetectAsync(token);
        WslSpeechRegistration? speech = provisioner.SpeechRegistration;
        return new(inventory.InstalledModels.Select(model => model.Id).ToHashSet(), inventory.InstalledRuntimeIds.ToHashSet(),
            inventory.WslDistributions.ToHashSet(StringComparer.OrdinalIgnoreCase), speech,
            LocalTalkProvisioner.CheckNativeRuntimePrerequisite() is null, File.Exists(settings.SpeakerModelPath), hardware.Gpus.Any(gpu => gpu.IsNvidia))
            { ManagedSpeechModelMissing = IsManagedSpeechModelMissing(speech, provisioner.ModelsRoot) };
    }

    internal static bool IsManagedSpeechModelMissing(WslSpeechRegistration? speech, string modelsRoot,
        Func<string, bool>? fileExists = null)
    {
        if (speech is null) return false;
        ProvisionModelChoice model = LocalTalkProvisioner.ModelCatalog.Single(choice => choice.Id == "voxtral-realtime-q4_k_m");
        string cachedPath;
        string expectedLinuxPath;
        try
        {
            cachedPath = Path.Combine(Path.GetFullPath(modelsRoot), model.FileName);
            expectedLinuxPath = LocalTalkProvisioner.WindowsPathToWsl(cachedPath);
        }
        catch (ArgumentException) { return false; }
        // Only repair the exact registration produced by this app's managed setup.
        // Imported/Linux-owned models are never inferred from a filename or /mnt prefix.
        if (!string.Equals(speech.ModelPath, expectedLinuxPath, StringComparison.Ordinal)) return false;
        fileExists ??= File.Exists;
        return !fileExists(cachedPath);
    }
    public async Task<SetupPrerequisiteResult> EnsureNativeRuntimeAsync(IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        if (LocalTalkProvisioner.CheckNativeRuntimePrerequisite() is null) return SetupPrerequisiteResult.Ready;
        string path = Path.Combine(provisioner.DataRoot, "downloads", NativeRuntimeAsset.FileName);
        await provisioner.DownloadVerifiedAsync(NativeRuntimeAsset, path, progress, token);
        string verifier = Path.Combine(AppContext.BaseDirectory, "provisioning", "verify-microsoft-signature.ps1");
        if (!File.Exists(verifier)) throw new FileNotFoundException("A setup file is missing. Reinstall the app.");
        var check = Command(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", verifier, "-Path", path);
        await LocalTalkProvisioner.RunCommandAsync(check, null, token, TimeSpan.FromMinutes(1));
        int code = await RunElevatedAsync(new(path) { UseShellExecute = true, Verb = "runas", Arguments = "/install /passive /norestart", WindowStyle = ProcessWindowStyle.Hidden }, token);
        if (code == 3010) return SetupPrerequisiteResult.RestartRequired;
        if (code != 0 || LocalTalkProvisioner.CheckNativeRuntimePrerequisite() is not null)
            throw new SetupUserException("Windows couldn't finish installing the Microsoft runtime. Try setup again; details are in Troubleshooting.");
        return SetupPrerequisiteResult.Ready;
    }
    public async Task<SetupPrerequisiteResult> EnsureWindowsSpeechAsync(IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        if (await CanUseWslAsync(token)) return SetupPrerequisiteResult.Ready;
        int code = await RunElevatedAsync(new(WslExecutable) { UseShellExecute = true, Verb = "runas",
            Arguments = "--install --no-distribution", WindowStyle = ProcessWindowStyle.Hidden }, token);
        if (code is not 0 and not 3010) throw new SetupUserException("Windows couldn't prepare speech support. Try setup again; details are in Troubleshooting.");
        return code == 3010 || !await CanUseWslAsync(token) ? SetupPrerequisiteResult.RestartRequired : SetupPrerequisiteResult.Ready;
    }
    public async Task EnsureDistributionAsync(string name, IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        if (!AppBrand.IsOwnedSpeechDistributionName(name)) throw new InvalidOperationException("Invalid app-owned speech component name.");
        if (!provisioner.InspectLocal().WslDistributions.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            // --name and --version avoid altering the default distribution or converting user data.
            string help = await ReadCommandAsync(Command(WslExecutable, "--help"), token);
            if (!help.Contains("--name", StringComparison.Ordinal))
            {
                int update = await RunElevatedAsync(new(WslExecutable) { UseShellExecute = true, Verb = "runas", Arguments = "--update", WindowStyle = ProcessWindowStyle.Hidden }, token);
                if (update is not 0 and not 3010) throw new SetupUserException("Windows couldn't update speech support. Try setup again; details are in Troubleshooting.");
                if (update == 3010) throw new SetupRestartRequiredException();
                help = await ReadCommandAsync(Command(WslExecutable, "--help"), token);
                if (!help.Contains("--name", StringComparison.Ordinal))
                    throw new SetupRestartRequiredException();
            }
            await LocalTalkProvisioner.RunCommandAsync(Command(WslExecutable, "--install", "--distribution", "Ubuntu-24.04",
                "--name", name, "--version", "2", "--no-launch"), DetailedProgress(), token, TimeSpan.FromMinutes(40));
        }
        using RegistryKey? lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        bool version2 = false;
        if (lxss is not null) foreach (string key in lxss.GetSubKeyNames())
        {
            using RegistryKey? distro = lxss.OpenSubKey(key);
            if (string.Equals(distro?.GetValue("DistributionName") as string, name, StringComparison.OrdinalIgnoreCase))
                version2 = Convert.ToInt32(distro?.GetValue("Version") ?? 0) == 2;
        }
        if (!version2) throw new SetupRestartRequiredException();
        // Root command bypasses Ubuntu's interactive account wizard; it applies only to this isolated app distro.
        await LocalTalkProvisioner.RunCommandAsync(Command(WslExecutable, "--distribution", name, "--user", "root", "--exec", "/bin/true"),
            null, token, TimeSpan.FromSeconds(90));
        await HideManagedDesktopEntriesAsync(name, serverPath: null, token);
    }
    public async Task EnsureModelAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        if (id == "voxtral-realtime-q4_k_m" && IsManagedSpeechModelMissing(provisioner.SpeechRegistration, provisioner.ModelsRoot))
        {
            ProvisionModelChoice model = LocalTalkProvisioner.ModelCatalog.Single(choice => choice.Id == id);
            // A newer imported ModelReferences entry may point elsewhere. Restore the
            // registered cached file without replacing that external model reference.
            await provisioner.DownloadVerifiedAsync(new(model.FileName, model.SizeBytes, model.Sha256, model.DownloadUrl),
                Path.Combine(provisioner.ModelsRoot, model.FileName), progress, token);
        }
        else if (provisioner.ResolveModelPath(id) is null)
            await provisioner.DownloadModelAsync(id, true, progress, token);
    }
    public async Task EnsureRuntimeAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token) =>
        await provisioner.InstallRuntimeAsync(id, true, progress, token);
    public async Task<string> EnsureSpeakerModelAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        if (File.Exists(settings.SpeakerModelPath)) return settings.SpeakerModelPath; // Keep explicit advanced imports intact.
        if (!string.Equals(Path.GetFileName(settings.SpeakerModelPath), SpeakerAsset.FileName, StringComparison.OrdinalIgnoreCase))
            throw new SetupUserException("Your selected voice recognition model is missing. Download it in Advanced → Speaker options, or choose the recommended model there and retry setup.");
        string path = Path.Combine(provisioner.DataRoot, "models", "speaker-id", SpeakerAsset.FileName);
        await provisioner.DownloadVerifiedAsync(SpeakerAsset, path, progress, token);
        return path;
    }
    public async Task ValidateSpeechAsync(WslSpeechRegistration speech, CancellationToken token)
    {
        await LocalTalkProvisioner.RunCommandAsync(SpeechCommand(speech, "/usr/bin/grep", "-qi", "microsoft.*WSL2", "/proc/sys/kernel/osrelease"),
            null, token, TimeSpan.FromSeconds(30));
        if (speech.Backend == "cuda")
            await LocalTalkProvisioner.RunCommandAsync(SpeechCommand(speech, "/usr/lib/wsl/lib/nvidia-smi", "--query-gpu=name", "--format=csv,noheader"),
                null, token, TimeSpan.FromSeconds(30));
        await provisioner.RegisterWslSpeechAsync(speech.Distribution, speech.ServerPath, speech.ModelPath, speech.Backend, token, speech.RunAsRoot);
        if (ManagedSpeechIntegration.IsManagedRegistration(speech))
        {
            await HideManagedDesktopEntriesAsync(speech.Distribution, speech.ServerPath, token);
            ManagedSpeechIntegration.TryHideGeneratedWindowsShortcuts(speech, provisioner.DataRoot);
        }
    }
    public async Task BuildSpeechAsync(string distribution, string backend, IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        await provisioner.BootstrapWslSpeechAsync(distribution, backend, true, SpeechBuildProgress(progress), token,
            runAsRoot: true, hideDesktopIntegration: true);
        ManagedSpeechIntegration.TryHideGeneratedWindowsShortcuts(provisioner.SpeechRegistration, provisioner.DataRoot);
    }

    private static async Task HideManagedDesktopEntriesAsync(string distribution, string? serverPath, CancellationToken token)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "provisioning", "hide-speech-desktop-entries.sh");
        if (!File.Exists(script)) return; // Older development payloads remain compatible.
        string[] args = serverPath is null ? ["--owned-setup"] : ["--owned-runtime", serverPath];
        try
        {
            string[] prefix = ["--distribution", distribution, "--user", "root", "--exec",
                "/bin/bash", LocalTalkProvisioner.WindowsPathToWsl(script)];
            await LocalTalkProvisioner.RunCommandAsync(Command(WslExecutable, prefix.Concat(args).ToArray()),
                null, token, TimeSpan.FromSeconds(20));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        { DesktopLog.Write("Optional internal desktop entry cleanup failed; speech setup is still available. " + error.Message); }
    }

    internal static IProgress<ProvisionProgress> SpeechBuildProgress(IProgress<ProvisionProgress>? progress)
    {
        // Keep raw output in Troubleshooting; the main view gets measured, friendly phases.
        var parser = new SpeechSetupProgressParser(DateTimeOffset.UtcNow);
        object gate = new();
        return new DirectProgress(value =>
        {
            DesktopLog.Write("Setup: " + value.Message);
            if (string.IsNullOrWhiteSpace(value.Message.Replace("\0", "", StringComparison.Ordinal))) return;
            lock (gate)
            {
                SpeechSetupProgressSnapshot snapshot = parser.Observe(value.Message, DateTimeOffset.UtcNow);
                progress?.Report(new("Speech setup", snapshot.Stage)
                {
                    Detail = snapshot.Message, StepIndex = snapshot.StepIndex, StepCount = snapshot.StepCount,
                    WorkFraction = snapshot.Fraction
                });
            }
        });
    }
    private static string WslExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
    private static ProcessStartInfo Command(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    private static ProcessStartInfo SpeechCommand(WslSpeechRegistration speech, params string[] arguments)
    {
        string[] prefix = speech.RunAsRoot ? ["--distribution", speech.Distribution, "--user", "root", "--exec"]
            : ["--distribution", speech.Distribution, "--exec"];
        return Command(WslExecutable, prefix.Concat(arguments).ToArray());
    }
    private static IProgress<ProvisionProgress> DetailedProgress(IProgress<ProvisionProgress>? progress = null) =>
        new DirectProgress(value => { DesktopLog.Write("Setup: " + value.Message); progress?.Report(new("Installing speech recognition", "Installing speech recognition")); });
    private static async Task<bool> CanUseWslAsync(CancellationToken token)
    {
        try { await LocalTalkProvisioner.RunCommandAsync(Command(WslExecutable, "--status"), null, token, TimeSpan.FromSeconds(20)); return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException) { return false; }
    }
    private static async Task<string> ReadCommandAsync(ProcessStartInfo info, CancellationToken token)
    {
        using Process process = Process.Start(info) ?? throw new IOException("Windows setup couldn't start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token), stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await stderr; return (await stdout).Replace("\0", ""); }
        catch { try { if (!process.HasExited) process.Kill(); } catch { } throw; }
    }
    private static async Task<int> RunElevatedAsync(ProcessStartInfo info, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Process process = Process.Start(info) ?? throw new IOException("Windows setup couldn't start.");
        // Do not forcibly kill a Windows servicing/MSI transaction when the user cancels.
        // Stop scheduling further work and observe its bounded completion in the background.
        try { await process.WaitForExitAsync(token); return process.ExitCode; }
        catch (OperationCanceledException) { DesktopLog.Write("Windows prerequisite setup may still finish; no further SayWhat? setup steps will run."); throw; }
    }
    private sealed class DirectProgress(Action<ProvisionProgress> action) : IProgress<ProvisionProgress>
    { public void Report(ProvisionProgress value) => action(value); }
}

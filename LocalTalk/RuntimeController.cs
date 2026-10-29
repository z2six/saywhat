using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace IncomingTranslator;

// Construction is inert. Only explicit UI Start calls may start installed local engines.
// All cleanup is scoped to retained Process objects and token-owned WSL wrappers, never ports/PIDs discovered elsewhere.
internal sealed partial class RuntimeController : IAsyncDisposable, ISpeakerFeedbackHost
{
    private readonly LocalTalkProvisioner _provisioner;
    private readonly Func<Version> _audioWindowsVersion;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Dictionary<int, TranslatorProcess> _translators = new();
    private readonly HashSet<string> _knownResidencyEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private OwnedSpeech? _ownSpeech, _incomingSpeech;
    private OwnedProcess? _nativeOwnSpeech, _nativeIncomingSpeech;
    private OwnedGroupSpeech? _incomingGroupSpeech;
    private OwnedProcess? _proxy, _muteController;
    private SubtitleWindow? _overlay;
    private readonly List<SubtitleWindow> _retiringOverlays = new();
    private readonly Dictionary<string, OwnedSpeech> _pendingSpeechCleanup = new();
    private readonly Dictionary<string, OwnedGroupSpeech> _pendingGroupCleanup = new();
    private readonly Dictionary<string, OwnedGroupPreparation> _pendingGroupPreparationCleanup = new();
    private int? _ownTranslatorPort, _incomingTranslatorPort;
    private RuntimePlan? _ownPlan, _incomingPlan;
    private AppSettings? _ownSettings;
    private AppSettings? _incomingSettings;
    private CancellationTokenSource? _startupCancellation;
    private bool _closingOverlay, _disposed;
    private volatile bool _disposeRequested;
    private volatile bool _ownRunning, _incomingRunning;

    public RuntimeController(LocalTalkProvisioner provisioner, Func<Version>? audioWindowsVersion = null)
    { _provisioner = provisioner; _audioWindowsVersion = audioWindowsVersion ?? (() => IncomingCaptureCapabilities.CurrentVersion); }
    public bool OwnRunning => _ownRunning;
    public bool IncomingRunning => _incomingRunning;
    internal IncomingHealthSnapshot? IncomingHealth => _overlay?.HealthSnapshot;
    public SpeakerRecognitionStatus RecognitionStatus => _overlay?.RecognitionStatus ?? new(false, false, "Off");
    public event Action? StateChanged;

    internal void RefreshInterfaceLanguage()
    {
        if (_overlay is SubtitleWindow window && !window.Dispatcher.HasShutdownStarted)
            window.Dispatcher.InvokeAsync(window.RefreshInterfaceLanguage);
    }

    public async Task StartOwnAsync(AppSettings settings, CancellationToken cancellationToken = default,
        IProgress<ProvisionProgress>? progress = null)
    {
        var phases = new RuntimePhaseProgress(progress);
        await _changes.WaitAsync(cancellationToken);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startupCancellation = startup;
        cancellationToken = startup.Token;
        try
        {
            await StartOwnCoreAsync(settings, cancellationToken, phases);
        }
        finally { Interlocked.CompareExchange(ref _startupCancellation, null, startup); _changes.Release(); }
    }

    private async Task StartOwnCoreAsync(AppSettings settings, CancellationToken token, RuntimePhaseProgress phases)
    {
        ThrowIfDisposed();
        if (_ownRunning) return;
        phases.Report("Checking translation tools");
        RequirePreviousCleanup();
        ValidateOwnSource(settings);
        RuntimePlan plan = ResolveRetainedTranslatorPlan(BuildPlan(settings, incoming: false));
        ManagedPortAllocator.ValidateConfiguration(settings, ManagedServiceKind.OwnSpeech, ManagedServiceKind.OwnTranslation);
        CheckSharedCompatibility(plan, _incomingPlan);
        ValidateManagedAssets(plan);
        ValidateOwnCaptureHelpers();
        if (settings.OwnRecognitionEngine == "voxtral") ValidateSpeechHelpers(RequireSpeechRegistration(), 8080);
        else _ = new NativeSpeechProvisioner(_provisioner.DataRoot).BuildStartInfo(settings, 8080);
        if (!settings.AutomaticManagedPorts) ManagedPortAllocator.EnsureBindable(settings.OwnSpeechPort, "Your voice recognition");
        RequireUdpPortFree(9010); RequireUdpPortFree(9001); RequireUdpPortFree(9011);
        try
        {
            plan = await AcquireTranslatorAsync(plan, settings, incoming: false, token, phases);
            _ownTranslatorPort = plan.Managed ? plan.Port : null;
            ApplyPlan(settings, plan, incoming: false);
            SaveControllerSettings(settings);
            if (settings.OwnRecognitionEngine == "voxtral")
                _ownSpeech = await StartManagedVoxtralAsync(RequireSpeechRegistration(), settings, incoming: false, token, phases);
            else await StartNativeOwnSpeechAsync(settings, token, phases);
            await StartOwnCaptureCoreAsync(settings, token, phases);
            token.ThrowIfCancellationRequested();
            _ownPlan = plan; _ownSettings = settings.Copy(); _ownRunning = true;
            if (_ownSpeech is not null) WatchOwn(_ownSpeech.Process);
            if (_nativeOwnSpeech is not null) WatchOwn(_nativeOwnSpeech);
            Log("Your-voice translation started. Capture remains gated by verified VRChat mute/presence; no overlay was started.");
            NotifyState();
        }
        catch
        {
            await StopOwnCoreAsync(phases);
            throw;
        }
    }

    private static void ValidateOwnSource(AppSettings settings)
    {
        // Older Voxtral installations may already have an explicit source for a
        // translation-only hint (for example TranslateGemma). Retain that working
        // pairing; Voxtral still detects audio language automatically. New Home
        // source controls never claim to condition Voxtral with this hint.
        if (settings.OwnRecognitionEngine == "voxtral" &&
            (settings.OwnVoiceSourceLanguage == "Automatic" || OwnRecognitionCatalog.SupportedSourceLanguages.Contains(AppSettings.CanonicalTranslationLanguage(settings.OwnVoiceSourceLanguage) ?? ""))) return;
        if (!OwnRecognitionCatalog.Find(settings.OwnRecognitionEngine).SupportsSource(settings.OwnVoiceSourceLanguage))
            throw new ModelSelectionUserException("Voxtral detects the spoken language automatically. Choose Automatic or switch to Nemotron.");
    }

    private static void ValidateOwnCaptureHelpers()
    {
        _ = FindOwnHost(); _ = RequireTool("FoxTrans-OscSentenceProxy.ps1"); _ = RequireTool("FoxTrans-VRChatMuteSync.ps1");
    }

    private async Task StartNativeOwnSpeechAsync(AppSettings settings, CancellationToken token, RuntimePhaseProgress phases)
    {
        if (_nativeOwnSpeech is not null) throw new InvalidOperationException("The previous speech recognizer has not stopped yet. Retry Stop all.");
        phases.Report("Starting speech recognition", "Loading the selected Windows speech model.");
        var started = await StartManagedWindowsAsync(settings, ManagedServiceKind.OwnSpeech,
            port => new NativeSpeechProvisioner(_provisioner.DataRoot).BuildStartInfo(settings, port), "Windows speech recognition",
            port => LocalEndpoint(port) + "/ready",
            root => root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True,
            TimeSpan.FromSeconds(120),
            (port, process) => { _nativeOwnSpeech = process; _ownSpeechPort = port; },
            (_, process) => { if (ReferenceEquals(_nativeOwnSpeech, process)) { _nativeOwnSpeech = null; _ownSpeechPort = null; } }, token);
        settings.OwnSpeechEndpoint = LocalEndpoint(started.Port);
    }

    private async Task StartOwnCaptureCoreAsync(AppSettings settings, CancellationToken token, RuntimePhaseProgress phases)
    {
        ValidateOwnCaptureHelpers();
        ApplyRetainedSessionEndpoints(settings);
        SettingsStore.WriteOwnVoiceConfig(settings, settings.OwnSpeechEndpoint,
            string.IsNullOrWhiteSpace(settings.OwnTranslationEndpoint) ? settings.OwnTranslatorBaseUrl : settings.OwnTranslationEndpoint);
        RequireUdpPortFree(9010); RequireUdpPortFree(9001); RequireUdpPortFree(9011);
        phases.Report("Connecting your microphone", "Connecting the microphone and VRChat chat output.");
        string proxyLog = Path.Combine(AppPaths.Root, "foxtrans-osc-sentence.log");
        _proxy = StartOwned(PowerShellInfo(RequireTool("FoxTrans-OscSentenceProxy.ps1"),
            "-ListenPort", "9010", "-TargetPort", "9000", "-LogPath", proxyLog,
            "-FinalGraceMs", "500", "-DraftQuietMs", "0"), "OSC final-message proxy");
        await WaitUdpListenerAsync(9010, _proxy, token);
        _muteController = StartOwned(PowerShellInfo(RequireTool("FoxTrans-VRChatMuteSync.ps1"),
            "-FoxTransPath", FindOwnHost(), "-ConfigPath", AppPaths.FastConfigPath,
            "-DataRoot", AppPaths.Root, "-LogPath", Path.Combine(AppPaths.Root, "foxtrans-mute-sync.log"),
            "-ProxyLogPath", proxyLog, "-SentenceProxyPort", "9010", "-VrChatOutputPort", "9001",
            "-HostControlPort", "9011", "-UnmutedIdleRestartSeconds", "0"), "Microphone mute controller");
        await WaitUdpListenerAsync(9001, _muteController, token);
        WatchOwn(_muteController); WatchOwn(_proxy);
    }

    private async Task StopOwnCaptureCoreAsync()
    {
        // Called after a verified quiesce for changes, or immediately for an
        // explicit Stop. A failed close retains the exact owned reference.
        if (_proxy is not null) { SendBooleanControl(9010, "/foxtrans/control/muted", true); SendBooleanControl(9010, "/foxtrans/control/reset", true); }
        if (_muteController is not null) { await _muteController.StopAsync(killTree: true); _muteController = null; }
        if (_proxy is not null) { await _proxy.StopAsync(killTree: true); _proxy = null; }
    }

    internal static bool CanReuseOwnModels(AppSettings previous, AppSettings next) =>
        previous.OwnRecognitionEngine == next.OwnRecognitionEngine && SameOwnSpeechPortPolicy(previous, next) &&
        SameTranslator(BuildPlan(previous, false), BuildPlan(next, false));

    private static bool SameTranslator(RuntimePlan previous, RuntimePlan next) => previous.TranslationKey == next.TranslationKey &&
        previous.Managed == next.Managed && previous.Shared == next.Shared;

    internal static bool SameOwnCaptureChoices(AppSettings previous, AppSettings next) =>
        CanReuseOwnModels(previous, next) && previous.OwnMicrophoneDeviceId == next.OwnMicrophoneDeviceId &&
        previous.OwnVoiceSourceLanguage == next.OwnVoiceSourceLanguage &&
        previous.EffectiveOwnVoiceTargetLanguages().SequenceEqual(next.EffectiveOwnVoiceTargetLanguages()) &&
        previous.RecognitionDelayMilliseconds == next.RecognitionDelayMilliseconds &&
        previous.FinishCapturedSentenceOnMute == next.FinishCapturedSentenceOnMute &&
        previous.TranslationQuality == next.TranslationQuality && previous.Glossary == next.Glossary;

    // This entry point affects only a running own-voice feature. It deliberately
    // does not build/validate an unused incoming plan or require a speaker model.
    public async Task ApplyOwnSettingsAsync(AppSettings settings, CancellationToken token = default,
        IProgress<ProvisionProgress>? progress = null)
    {
        await _changes.WaitAsync(token);
        using var update = CancellationTokenSource.CreateLinkedTokenSource(token);
        _startupCancellation = update;
        token = update.Token;
        var phases = new RuntimePhaseProgress(progress);
        try
        {
            ThrowIfDisposed(); RequirePreviousCleanup(); ValidateOwnSource(settings);
            ManagedPortAllocator.ValidateConfiguration(settings, ManagedServiceKind.OwnSpeech, ManagedServiceKind.OwnTranslation);
            RuntimePlan plan = ResolveRetainedTranslatorPlan(BuildPlan(settings, incoming: false));
            ApplyRetainedSessionEndpoints(settings);
            CheckSharedCompatibility(plan, _incomingPlan);
            if (!_ownRunning)
            {
                ApplyPlan(settings, plan, incoming: false); SaveControllerSettings(settings);
                return; // Saving a stopped feature never loads recognition or translation.
            }
            AppSettings previous = _ownSettings?.Copy() ?? SettingsStore.Load();
            if (SameOwnCaptureChoices(previous, settings))
            {
                phases.Report("Applying voice settings", "The current speech and translation models stay loaded.");
                ApplyPlan(settings, plan, incoming: false); SaveControllerSettings(settings);
                SendBooleanControl(9011, "/foxtrans/control/reload", true);
                _ownPlan = plan; _ownSettings = settings.Copy();
                Log("Own-voice timing, progressive-message and VRChat mute preferences saved live without restarting capture or models; the controller observes mute-policy changes from preferences.");
                NotifyState(); return;
            }

            // Preserve the last captured sentence before changing its language,
            // microphone or engine. No settings are written until the exact host
            // acknowledges that its old pipeline has finished all captured work.
            phases.Report("Finishing your current message", "New microphone capture is briefly paused while your current message finishes.");
            bool frozen = false, captureStopped = false;
            bool reuse = CanReuseOwnModels(previous, settings);
            try
            {
                frozen = true;
                await QuiesceOwnCaptureAsync(token);
                if (reuse)
                {
                    phases.Report("Updating your voice translation", "Keeping the speech and translation models loaded.");
                    captureStopped = true;
                    await StopOwnCaptureCoreAsync();
                    ApplyPlan(settings, plan, incoming: false); SaveControllerSettings(settings);
                    await StartOwnCaptureCoreAsync(settings, token, phases);
                    _ownPlan = plan; _ownSettings = settings.Copy();
                    Log("Own-voice capture restarted with the selected languages/microphone; speech and translation models stayed resident.");
                    NotifyState(); return;
                }
                ValidateManagedAssets(plan);
                if (settings.OwnRecognitionEngine == "voxtral") ValidateSpeechHelpers(RequireSpeechRegistration(), 8080);
                else _ = new NativeSpeechProvisioner(_provisioner.DataRoot).BuildStartInfo(settings, 8080);
                captureStopped = true;
                await StopOwnCoreAsync(phases);
                ApplyPlan(settings, plan, incoming: false); SaveControllerSettings(settings);
                await StartOwnCoreAsync(settings, token, phases);
            }
            catch (Exception applyError)
            {
                if (!captureStopped)
                {
                    // Timeout/cancel before the acknowledgement must preserve the
                    // old host, settings and captured message, never force a kill.
                    if (frozen) ResumeOwnCaptureAfterFailedChange();
                    throw;
                }
                if (applyError is OperationCanceledException || token.IsCancellationRequested || _disposeRequested)
                {
                    SaveControllerSettings(previous);
                    await StopOwnCoreAsync(phases);
                    throw;
                }
                phases.Report("Restoring your voice translation", "The change could not finish. Restoring the previous settings.");
                try
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                    if (reuse)
                    {
                        await StopOwnCaptureCoreAsync(); SaveControllerSettings(previous);
                        await StartOwnCaptureCoreAsync(previous, recovery.Token, phases);
                        _ownPlan = BuildPlan(previous, incoming: false); _ownSettings = previous.Copy(); _ownRunning = true;
                        NotifyState();
                    }
                    else
                    {
                        await StopOwnCoreAsync(phases); SaveControllerSettings(previous);
                        await StartOwnCoreAsync(previous, recovery.Token, phases);
                    }
                }
                catch (Exception recoveryError)
                {
                    // A partially stopped controller must not leave a green
                    // Running badge. Try every retained cleanup path; failures
                    // remain explicitly retryable through Stop all.
                    try { await StopOwnCoreAsync(phases); }
                    catch (Exception cleanupError)
                    { throw new AggregateException("Your voice settings could not change or be restored. Choose Stop all, then retry. Open Diagnostics for details.", applyError, recoveryError, cleanupError); }
                    throw new AggregateException("Your voice settings could not change or be restored. Choose Stop all, then retry. Open Diagnostics for details.", applyError, recoveryError);
                }
                throw new InvalidOperationException("The change could not finish. Your previous voice settings were restored.", applyError);
            }
        }
        finally { Interlocked.CompareExchange(ref _startupCancellation, null, update); _changes.Release(); }
    }

    private async Task QuiesceOwnCaptureAsync(CancellationToken token)
    {
        if (_muteController is null || !_muteController.IsAlive)
            throw new InvalidOperationException("The microphone controller is not responding. Stop and restart voice translation before changing it.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string nonce = Guid.NewGuid().ToString("N");
        try
        {
            bool hostExists = await RequestOwnControlAcknowledgementAsync(9001, "/foxtrans/control/freeze", "/foxtrans/control/frozen", nonce, deadline.Token);
            if (hostExists)
                _ = await RequestOwnControlAcknowledgementAsync(9011, "/foxtrans/control/quiesce", "/foxtrans/control/quiesced", nonce, deadline.Token);
            // A host acknowledgement proves its final packet was queued, not
            // that the proxy's VRChat cadence slot has delivered it yet.
            _ = await RequestOwnControlAcknowledgementAsync(9010, "/foxtrans/control/flush", "/foxtrans/control/flushed", nonce, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Your current message has not finished yet. Its settings were left unchanged; try again after the translation appears."); }
    }

    public async Task<bool> FinishOwnMessageForSettingsAsync(CancellationToken token = default,
        IProgress<ProvisionProgress>? progress = null)
    {
        await _changes.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (!_ownRunning) return false;
            new RuntimePhaseProgress(progress).Report("Finishing your current message",
                "New microphone capture is briefly paused while your current message finishes.");
            try { await QuiesceOwnCaptureAsync(token); return true; }
            catch { ResumeOwnCaptureAfterFailedChange(); throw; }
        }
        finally { _changes.Release(); }
    }

    public async Task ResumeOwnAfterCancelledSettingsAsync()
    {
        await _changes.WaitAsync();
        try { if (_ownRunning) ResumeOwnCaptureAfterFailedChange(); }
        finally { _changes.Release(); }
    }

    private static async Task<bool> RequestOwnControlAcknowledgementAsync(int port, string request, string expected,
        string nonce, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] payload = StringControlPacket(request, nonce);
        try
        {
            Task<UdpReceiveResult>? pending = null;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await udp.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port), deadline.Token);
                pending ??= udp.ReceiveAsync(deadline.Token).AsTask();
                Task wait = Task.Delay(500, deadline.Token);
                if (await Task.WhenAny(pending, wait) != pending) continue;
                UdpReceiveResult result = await pending; pending = null;
                if (!IPAddress.IsLoopback(result.RemoteEndPoint.Address) || result.RemoteEndPoint.Port != port) continue;
                if (!TryReadOwnControlAcknowledgement(result.Buffer, expected, nonce, out bool hostExists)) continue;
                return hostExists;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Your current message has not finished yet. Its settings were left unchanged; try again after the translation appears.");
        }
    }

    internal static byte[] StringControlPacket(string address, string value)
    {
        if (address.Length > 256 || value.Length > 256 || address.Contains('\0') || value.Contains('\0'))
            throw new ArgumentException("Control strings must be bounded and cannot contain a terminator.");
        using var data = new MemoryStream();
        void Write(string text) { data.Write(Encoding.UTF8.GetBytes(text)); data.WriteByte(0); while (data.Length % 4 != 0) data.WriteByte(0); }
        Write(address); Write(",s"); Write(value); return data.ToArray();
    }

    internal static bool TryReadOwnControlAcknowledgement(byte[] packet, string expected, string nonce, out bool hostExists)
    {
        hostExists = false;
        int offset = 0;
        string? address = ReadControlString(packet, ref offset), tags = ReadControlString(packet, ref offset),
            receivedNonce = ReadControlString(packet, ref offset);
        bool validTags = expected == "/foxtrans/control/frozen" ? tags is ",sT" or ",sF" : tags == ",s";
        if (address != expected || receivedNonce != nonce || !validTags || offset != packet.Length) return false;
        hostExists = tags != ",sF"; return true;
    }

    internal static int RunVoiceFirstOfflineChecks()
    {
        int checks = 0;
        void Check(bool passed, string detail)
        { if (!passed) throw new InvalidOperationException("Own-voice runtime planning: " + detail); checks++; }
        var previous = new AppSettings { OwnRecognitionEngine = "nemotron", OwnVoiceSourceLanguage = "English",
            UseManagedTranslator = true, ShareTranslationModel = false, OwnManagedModelId = "hy-mt2-1.8b-q4_k_m",
            IncomingManagedModelId = "unused-invalid", IncomingSourceLanguage = "unused-invalid", SpeakerModelPath = "missing-optional-person-model" };
        var next = previous.Copy(); next.OwnVoiceEndPauseMilliseconds = 1800; next.OwnVoiceSendEarlyPreviews = false;
        Check(CanReuseOwnModels(previous, next) && SameOwnCaptureChoices(previous, next), "timing and early-message changes keep capture and models");
        next = previous.Copy(); next.OwnVoicePauseWhenVrchatMuted = false;
        Check(CanReuseOwnModels(previous, next) && SameOwnCaptureChoices(previous, next),
            "VRChat mute preference applies through the live controller without restarting capture or models");
        var scopedMute = MainWindow.VoiceFirstCandidate(previous, next, includeModels: false);
        Check(!scopedMute.OwnVoicePauseWhenVrchatMuted && previous.OwnVoicePauseWhenVrchatMuted &&
            scopedMute.OwnVoiceSourceLanguage == previous.OwnVoiceSourceLanguage &&
            scopedMute.IncomingSourceLanguage == previous.IncomingSourceLanguage &&
            !TranslationProfiles.ConfigurationEquals(previous, scopedMute),
            "the Home mute preference is a real scoped saved change without mutating unrelated settings");
        next.OwnVoiceSourceLanguage = "Chinese";
        Check(CanReuseOwnModels(previous, next) && !SameOwnCaptureChoices(previous, next), "spoken language replaces only the capture session");
        next = previous.Copy(); next.OwnMicrophoneDeviceId = "synthetic microphone";
        Check(CanReuseOwnModels(previous, next) && !SameOwnCaptureChoices(previous, next), "microphone choice replaces capture, not model residency");
        next = previous.Copy(); next.OwnVoiceAdditionalLanguagesEnabled = true; next.OwnVoiceAdditionalTargetLanguages = ["Japanese", "Korean", "English"];
        Check(CanReuseOwnModels(previous, next) && !SameOwnCaptureChoices(previous, next), "all four language targets reuse both models");
        next = previous.Copy(); next.OwnRecognitionEngine = "voxtral"; next.OwnVoiceSourceLanguage = "Automatic";
        Check(!CanReuseOwnModels(previous, next), "recognizer selection requires a real engine change");
        next = previous.Copy(); next.OwnManagedModelId = "hy-mt2-7b-q4_k_m";
        Check(!CanReuseOwnModels(previous, next), "translator selection requires its selected model");
        var legacy = previous.Copy(); legacy.OwnRecognitionEngine = "voxtral"; legacy.OwnManagedModelId = "translategemma-4b-q4_k_m";
        ValidateOwnSource(legacy);
        Check(BuildPlan(legacy, false).ModelId == "translategemma-4b-q4_k_m" && !OwnRecognitionCatalog.Find("voxtral").SupportsExplicitLanguage,
            "legacy explicit translator hint remains valid without claiming Voxtral language conditioning");
        string nonce = new('a', 32);
        byte[] ack = StringControlPacket("/foxtrans/control/quiesced", nonce);
        Check(TryReadOwnControlAcknowledgement(ack, "/foxtrans/control/quiesced", nonce, out _), "exact host acknowledgement is accepted");
        Check(!TryReadOwnControlAcknowledgement(ack, "/foxtrans/control/flushed", nonce, out _), "a host acknowledgement is not a proxy flush acknowledgement");
        Check(!TryReadOwnControlAcknowledgement(ack, "/foxtrans/control/quiesced", new string('b', 32), out _), "another operation's acknowledgement cannot complete the switch");
        Check(!TryReadOwnControlAcknowledgement(ack.Concat(new byte[] { 0 }).ToArray(), "/foxtrans/control/quiesced", nonce, out _), "trailing malformed payload is rejected");
        Check(!TryReadOwnControlAcknowledgement(ack[..^1], "/foxtrans/control/quiesced", nonce, out _), "truncated acknowledgement is rejected");
        Check(!TryReadOwnControlAcknowledgement(ack, "/foxtrans/control/frozen", nonce, out _), "controller freeze requires an explicit child-exists flag");
        return checks;
    }

    private static string? ReadControlString(byte[] data, ref int offset)
    {
        if (offset < 0 || offset >= data.Length) return null;
        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < offset || end - offset > 256) return null;
        string value = Encoding.UTF8.GetString(data, offset, end - offset);
        offset = (end + 4) & ~3;
        return offset <= data.Length ? value : null;
    }

    private static void ResumeOwnCaptureAfterFailedChange()
    {
        SendBooleanControl(9011, "/foxtrans/control/resume", true);
        SendBooleanControl(9001, "/foxtrans/control/unfreeze", true);
    }

    public async Task StartIncomingAsync(AppSettings settings, Action openOptions,
        CancellationToken cancellationToken = default, IProgress<ProvisionProgress>? progress = null, Action? openSpeakerSettings = null,
        Func<string, Task>? profileCallback = null)
    {
        var phases = new RuntimePhaseProgress(progress);
        await _changes.WaitAsync(cancellationToken);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startupCancellation = startup;
        cancellationToken = startup.Token;
        try
        {
            ThrowIfDisposed();
            IncomingCaptureCapabilities.Validate(settings, _audioWindowsVersion());
            if (_incomingRunning)
            {
                if (_overlay is not null) await _overlay.Dispatcher.InvokeAsync(() => { _overlay.Show(); _overlay.Activate(); });
                return;
            }
            phases.Report("Checking translation tools");
            RequirePreviousCleanup();
            WslSpeechRegistration? speech = IncomingRecognitionCatalog.UsesNative(settings) ? null : RequireSpeechRegistration();
            RuntimeProfiles.ValidateIncomingSpeech(settings);
            IncomingListening.Validate(settings);
            RuntimePlan plan = ResolveRetainedTranslatorPlan(BuildPlan(settings, incoming: true));
            ValidateIncomingPortSelection(settings);
            CheckSharedCompatibility(plan, _ownPlan);
            ValidateManagedAssets(plan);
            if (RuntimeProfiles.UsesGroups(settings))
            {
                await RequireGroupsPreparedCoreAsync(settings, speech!, cancellationToken);
                ValidateGroupsHelpers(speech!, settings);
                if (!settings.AutomaticManagedPorts) ManagedPortAllocator.EnsureBindable(settings.GroupsSpeechPort, "Groups speech recognition");
            }
            else if (IncomingRecognitionCatalog.UsesNative(settings))
            {
                ValidateNativeIncomingSpeech(settings);
                if (!IncomingRecognitionCatalog.UsesFireRed(settings) && !settings.AutomaticManagedPorts)
                    ManagedPortAllocator.EnsureBindable(settings.IncomingSpeechPort, "Subtitle speech recognition");
            }
            else
            {
                ValidateSpeechHelpers(speech!, 8081);
                if (!settings.AutomaticManagedPorts) ManagedPortAllocator.EnsureBindable(settings.IncomingSpeechPort, "Subtitle speech recognition");
            }
            RuntimeProfiles.ValidateSpeakerRecognitionAssets(settings);
            try
            {
                plan = await AcquireTranslatorAsync(plan, settings, incoming: true, cancellationToken, phases);
                _incomingTranslatorPort = plan.Managed ? plan.Port : null;
                ApplyPlan(settings, plan, incoming: true);
                await StartIncomingSpeechCoreAsync(settings, speech, cancellationToken, phases);
                SaveControllerSettings(settings);
                cancellationToken.ThrowIfCancellationRequested();
                if (Application.Current is null) throw new InvalidOperationException("Start subtitles from the SayWhat? application.");
                phases.Report("Opening subtitles", "Opening the subtitle window.");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _overlay = new SubtitleWindow(settings.Copy()) { OpenOptionsRequested = openOptions, OpenSpeakerSettingsRequested = openSpeakerSettings,
                        CorrectSpeakerRequested = SpeakerQuickPicker.ShowAsync,
                        EnsureSpeakerLearningRequested = SpeakerSuggestionReviewUi.EnsureLearningAcknowledgedAsync,
                        ProfileChangeRequested = profileCallback };
                    _overlay.Closed += OverlayClosed;
                    _overlay.Show(); // The existing window starts its own incoming service on Loaded.
                });
                _incomingPlan = plan;
                _incomingSettings = settings.Copy();
                _incomingRunning = true;
                WatchActiveIncomingSpeech();
                Log("Incoming voice overlay started. Closing it stops incoming capture and only its owned speech engine.");
                NotifyState();
            }
            catch
            {
                await StopIncomingCoreAsync(phases);
                throw;
            }
        }
        finally { Interlocked.CompareExchange(ref _startupCancellation, null, startup); _changes.Release(); }
    }

    public async Task<bool> CheckGroupSpeechPreparedAsync(AppSettings settings, CancellationToken token = default)
    {
        await _changes.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            IncomingCaptureCapabilities.Validate(settings, _audioWindowsVersion());
            RuntimeProfiles.ValidateIncomingSpeech(settings);
            WslSpeechRegistration registration = RequireSpeechRegistration();
            return (await CheckGroupsPreparedCoreAsync(settings, registration, token)).Prepared;
        }
        finally { _changes.Release(); }
    }

    public async Task PrepareGroupSpeechAsync(AppSettings settings, IProgress<ProvisionProgress>? progress = null,
        CancellationToken token = default)
    {
        await _changes.WaitAsync(token);
        using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _startupCancellation = preparationCancellation;
        token = preparationCancellation.Token;
        OwnedGroupPreparation? preparation = null;
        bool completed = false;
        try
        {
            ThrowIfDisposed(); IncomingCaptureCapabilities.Validate(settings, _audioWindowsVersion()); RequirePreviousCleanup();
            RuntimeProfiles.ValidateIncomingSpeech(settings);
            WslSpeechRegistration registration = RequireSpeechRegistration();
            string owner = Guid.NewGuid().ToString("N");
            string? receipt = null;
            var phases = new RuntimePhaseProgress(progress);
            phases.Report("Preparing Groups mode", "Downloading local speech tools. Translation keeps running until the switch is ready.");
            void Receive(string line, bool stderr)
            {
                if (stderr) return;
                if (line.StartsWith("SAYWHAT_GROUPS_PROGRESS=", StringComparison.Ordinal))
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(line["SAYWHAT_GROUPS_PROGRESS=".Length..]);
                        JsonElement value = document.RootElement;
                        string stage = value.GetProperty("stage").GetString() ?? "";
                        if (stage is not ("Preparing Groups speech tools" or "Installing local Groups runtime" or "Downloading Groups speech model")) return;
                        long Number(string name) => value.TryGetProperty(name, out JsonElement number) && number.TryGetInt64(out long integer) && integer >= 0 ? integer : 0;
                        if (value.TryGetProperty("unit", out JsonElement unit) && unit.GetString() == "B")
                            progress?.Report(new(stage, stage, Number("completed"), Number("total")));
                        else phases.Report(stage);
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
                    { Log("Groups preparation returned an invalid progress record; preparation continued."); }
                }
                else if (line.Length <= 8192 && line.StartsWith('{') && line.Contains("\"prepared\"", StringComparison.Ordinal))
                    Interlocked.Exchange(ref receipt, line);
            }
            OwnedProcess process = StartOwned(VibeVoiceRuntime.CreatePrepareStartInfo(registration, _provisioner.DataRoot,
                settings.VibeVoiceModelId, owner), "Groups preparation", Receive);
            preparation = new(process, registration, owner);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromHours(2));
            await process.Exited.WaitAsync(deadline.Token);
            await process.OutputDrained.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            if (process.ExitCode is not 0)
                throw new InvalidOperationException("Groups setup did not finish. Open Diagnostics for details, then retry preparation.");
            VibeVoicePreparation result = VibeVoiceRuntime.ParsePreparation(receipt ?? "", settings.VibeVoiceModelId);
            if (!result.Prepared)
                throw new InvalidOperationException("Groups setup is incomplete: " + string.Join(", ", result.Missing));
            completed = true;
            phases.Report("Groups setup ready", "The speech model will load only when Groups subtitles start.");
            Log("Groups speech tools and pinned local checkpoint prepared; no inference model was loaded.");
        }
        finally
        {
            try
            {
                if (preparation is not null)
                {
                    if (completed) await preparation.Process.StopAsync(killTree: false);
                    else await CancelGroupPreparationAsync(preparation);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _startupCancellation, null, preparationCancellation);
                _changes.Release();
            }
        }
    }

    public async Task SwitchIncomingProfileAsync(AppSettings settings, Action openOptions,
        CancellationToken token = default, IProgress<ProvisionProgress>? progress = null, Action? openSpeakerSettings = null,
        Func<string, Task>? profileCallback = null)
    {
        await _changes.WaitAsync(token);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        _startupCancellation = startup;
        token = startup.Token;
        var phases = new RuntimePhaseProgress(progress);
        try
        {
            ThrowIfDisposed(); IncomingCaptureCapabilities.Validate(settings, _audioWindowsVersion()); RequirePreviousCleanup();
            // Applying settings while subtitles are off must not start capture/models.
            if (!_incomingRunning || _overlay is null) return;
            SubtitleWindow overlay = _overlay;
            settings = PreferenceSettings(settings);
            AppSettings previous = PreferenceSettings(_incomingSettings?.Copy() ?? SettingsStore.Load());
            RuntimePlan previousPlan = _incomingPlan ?? BuildPlan(previous, incoming: true);
            RuntimePlan nextPlan = ResolveRetainedTranslatorPlan(BuildPlan(settings, incoming: true));
            ValidateIncomingPortSelection(settings);
            WslSpeechRegistration? registration = IncomingRecognitionCatalog.UsesNative(settings) ? null : RequireSpeechRegistration();
            RuntimeProfiles.ValidateIncomingSpeech(settings);
            IncomingListening.Validate(settings);
            CheckSharedCompatibility(nextPlan, _ownPlan);
            ValidateManagedAssets(nextPlan);
            RuntimeProfiles.ValidateSpeakerRecognitionAssets(settings);
            if (RuntimeProfiles.UsesGroups(settings))
            {
                ValidateGroupsHelpers(registration!, settings);
                await RequireGroupsPreparedCoreAsync(settings, registration!, token);
            }
            else if (IncomingRecognitionCatalog.UsesNative(settings)) ValidateNativeIncomingSpeech(settings);
            else ValidateSpeechHelpers(registration!, 8081);
            bool applyLive = RuntimeProfiles.CanApplyLive(previous, settings, previousPlan, nextPlan);
            bool reuseModels = RuntimeProfiles.CanReuseIncomingModels(previous, settings, previousPlan, nextPlan);
            ApplyPlan(settings, nextPlan, incoming: true);
            if (applyLive)
            {
                ApplyRetainedSessionEndpoints(settings);
                phases.Report("Applying subtitle settings", "Keeping the current speech and translation models loaded.");
                try
                {
                    await overlay.Dispatcher.InvokeAsync(() => overlay.ApplyLiveProfileAsync(settings.Copy(), token)).Task.Unwrap();
                }
                catch (Exception applyError)
                {
                    // A failed live update may already have changed the capture
                    // session or recognition switch. Recreate only that service
                    // with the old settings; do not claim it remained unchanged.
                    try
                    {
                        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        await overlay.Dispatcher.InvokeAsync(() => overlay.PauseForProfileAsync(recovery.Token)).Task.Unwrap();
                        SaveControllerSettings(previous, profile: true);
                        ApplyRetainedSessionEndpoints(previous);
                        await overlay.Dispatcher.InvokeAsync(() => overlay.ResumeProfileAsync(previous.Copy(), recovery.Token)).Task.Unwrap();
                    }
                    catch (Exception recoveryError)
                    {
                        try { await StopIncomingCoreAsync(phases); }
                        catch (Exception cleanupError) { Log("Live subtitle settings cleanup remains retryable: " + cleanupError.GetType().Name); }
                        throw new AggregateException("Subtitle settings could not be applied or restored. Subtitles were stopped; your microphone translator was left unchanged.", applyError, recoveryError);
                    }
                    throw new InvalidOperationException("Subtitle settings could not be applied. The previous settings and subtitle history were restored.", applyError);
                }
                await overlay.Dispatcher.InvokeAsync(() => overlay.ProfileChangeRequested = profileCallback);
                _incomingPlan = nextPlan; _incomingSettings = settings.Copy();
                Log("Incoming profile applied live; existing speech/translation models and subtitle history were preserved.");
                NotifyState();
                return;
            }

            if (reuseModels)
            {
                ApplyRetainedSessionEndpoints(settings);
                // A new application or a user-selected identity needs a fresh
                // capture service so old audio, tracker state and person hints
                // cannot cross the boundary. Its models need not be reloaded.
                phases.Report("Applying subtitle settings", "Keeping the current speech and translation models loaded.");
                try
                {
                    await overlay.Dispatcher.InvokeAsync(() => overlay.PauseForProfileAsync(token)).Task.Unwrap();
                    token.ThrowIfCancellationRequested();
                    SaveControllerSettings(settings, profile: true);
                    await overlay.Dispatcher.InvokeAsync(() => overlay.ResumeProfileAsync(settings.Copy(), token)).Task.Unwrap();
                    await overlay.Dispatcher.InvokeAsync(() => overlay.ProfileChangeRequested = profileCallback);
                    _incomingPlan = nextPlan; _incomingSettings = settings.Copy();
                    Log("Incoming listening source/person changed; capture history was isolated, existing speech and translation models were retained, and your microphone translator was left unchanged.");
                    NotifyState();
                    return;
                }
                catch (OperationCanceledException)
                {
                    SaveControllerSettings(previous, profile: true);
                    await StopIncomingCoreAsync(phases);
                    throw;
                }
                catch (Exception applyError)
                {
                    try
                    {
                        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        await overlay.Dispatcher.InvokeAsync(() => overlay.PauseForProfileAsync(recovery.Token)).Task.Unwrap();
                        SaveControllerSettings(previous, profile: true);
                        ApplyRetainedSessionEndpoints(previous);
                        await overlay.Dispatcher.InvokeAsync(() => overlay.ResumeProfileAsync(previous.Copy(), recovery.Token)).Task.Unwrap();
                        _incomingPlan = previousPlan; _incomingSettings = previous.Copy();
                        NotifyState();
                    }
                    catch (Exception recoveryError)
                    {
                        try { await StopIncomingCoreAsync(phases); }
                        catch (Exception cleanupError) { Log("Listening source cleanup remains retryable: " + cleanupError.GetType().Name); }
                        throw new AggregateException("The listening source could not change or be restored. Subtitles were stopped; your microphone translator was left unchanged.", applyError, recoveryError);
                    }
                    throw new InvalidOperationException("The listening source could not change. The previous source and subtitle history were restored.", applyError);
                }
            }

            bool translationChanged = previousPlan.TranslationKey != nextPlan.TranslationKey ||
                previousPlan.Managed != nextPlan.Managed || previousPlan.Shared != nextPlan.Shared;
            bool oldTranslatorReleased = false;
            bool nextTranslatorAcquired = false;
            bool paused = false;
            try
            {
                phases.Report("Switching subtitle mode", "Subtitles are briefly paused while the speech model changes.");
                paused = true;
                await overlay.Dispatcher.InvokeAsync(() => overlay.PauseForProfileAsync(token)).Task.Unwrap();
                await StopIncomingSpeechCoreAsync();
                token.ThrowIfCancellationRequested();
                if (translationChanged)
                {
                    await ReleaseTranslatorAsync(_incomingTranslatorPort, phases);
                    _incomingTranslatorPort = null; oldTranslatorReleased = true;
                    nextPlan = await AcquireTranslatorAsync(nextPlan, settings, incoming: true, token, phases);
                    _incomingTranslatorPort = nextPlan.Managed ? nextPlan.Port : null;
                    nextTranslatorAcquired = true;
                }
                ApplyPlan(settings, nextPlan, incoming: true);
                await StartIncomingSpeechCoreAsync(settings, registration, token, phases);
                token.ThrowIfCancellationRequested();
                SaveControllerSettings(settings, profile: true);
                await overlay.Dispatcher.InvokeAsync(() => overlay.ResumeProfileAsync(settings.Copy(), token)).Task.Unwrap();
                await overlay.Dispatcher.InvokeAsync(() => overlay.ProfileChangeRequested = profileCallback);
                _incomingPlan = nextPlan; _incomingSettings = settings.Copy();
                WatchActiveIncomingSpeech();
                Log("Incoming speech mode switched; subtitle window/history, saved People and your microphone translator were preserved.");
                NotifyState();
            }
            catch (Exception switchError)
            {
                if (!paused) throw;
                if (switchError is OperationCanceledException)
                {
                    // Cancelling/closing must release the partially loaded new
                    // model, never start another GPU model as a side effect.
                    SaveControllerSettings(previous, profile: true);
                    await StopIncomingCoreAsync(phases);
                    throw;
                }
                phases.Report("Restoring previous subtitle mode", "The new mode could not start. Restoring your previous subtitles.");
                try
                {
                    await StopIncomingSpeechCoreAsync();
                    if (nextTranslatorAcquired)
                    {
                        await ReleaseTranslatorAsync(_incomingTranslatorPort, phases);
                        _incomingTranslatorPort = null;
                    }
                    RequirePreviousCleanup();
                    using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                    if (oldTranslatorReleased)
                    {
                        previousPlan = await AcquireTranslatorAsync(previousPlan, previous, incoming: true, rollback.Token, phases);
                        _incomingTranslatorPort = previousPlan.Managed ? previousPlan.Port : null;
                    }
                    ApplyPlan(previous, previousPlan, incoming: true);
                    WslSpeechRegistration? previousRegistration = IncomingRecognitionCatalog.UsesNative(previous) ? null : RequireSpeechRegistration();
                    await StartIncomingSpeechCoreAsync(previous, previousRegistration, rollback.Token, phases);
                    SaveControllerSettings(previous, profile: true);
                    await overlay.Dispatcher.InvokeAsync(() => overlay.ResumeProfileAsync(previous.Copy(), rollback.Token)).Task.Unwrap();
                    _incomingPlan = previousPlan; _incomingSettings = previous.Copy();
                    WatchActiveIncomingSpeech(); NotifyState();
                }
                catch (Exception rollbackError)
                {
                    Log("Incoming mode switch and recovery failed; the subtitle feature is stopping cleanly.");
                    try { await StopIncomingCoreAsync(phases); }
                    catch (Exception cleanupError) { Log("Incoming switch cleanup remains retryable: " + cleanupError.GetType().Name); }
                    throw new AggregateException("The subtitle mode could not switch or recover. Your microphone translator was left unchanged. Open Diagnostics, then retry subtitles.", switchError, rollbackError);
                }
                throw new InvalidOperationException("The new subtitle mode could not start. Your previous mode and subtitle history were restored.", switchError);
            }
        }
        finally { Interlocked.CompareExchange(ref _startupCancellation, null, startup); _changes.Release(); }
    }

    public async Task StopOwnAsync()
    {
        CancelPendingStartup();
        await _changes.WaitAsync();
        try { await StopOwnCoreAsync(); }
        finally { _changes.Release(); }
    }
    public async Task ResetSpeakerProfilesAsync(CancellationToken token=default)
    {
        await _changes.WaitAsync(token);
        try
        {
            if(_overlay is not null) await _overlay.Dispatcher.InvokeAsync(()=>_overlay.ResetSpeakerLearningAsync(token)).Task.Unwrap();
            else {using var identifier=new AiSpeakerIdentifier();await identifier.ResetProfilesAsync(token);}
        }
        finally {_changes.Release();}
    }

    public async Task<SpeakerRecognitionStatus> SetSpeakerRecognitionAsync(bool enabled, CancellationToken token = default)
    {
        await _changes.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            SpeakerRecognitionStatus status = _overlay is not null
                ? await _overlay.Dispatcher.InvokeAsync(() => _overlay.ApplySpeakerRecognitionAsync(enabled, token)).Task.Unwrap()
                : new(enabled, false, enabled ? "Enabled · starts with subtitles" : "Off");
            if (_incomingSettings is not null) _incomingSettings.UseAiSpeakerRecognition = enabled;
            Log(enabled ? "Optional speaker recognition enabled; its model loads only when incoming speech needs it."
                : "Optional speaker recognition stopped; translation engines were left unchanged and voice-ID model memory was released.");
            NotifyState();
            return status;
        }
        finally { _changes.Release(); }
    }

    // Metadata/correction operations never start audio, load a model, or create a hidden engine.
    private async Task<T> WithSpeakerHostAsync<T>(Func<ISpeakerFeedbackHost, Task<T>> action, CancellationToken token)
    {
        await _changes.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (_overlay?.SpeakerFeedbackHost is ISpeakerFeedbackHost live) return await action(live);
            using var identifier = new AiSpeakerIdentifier();
            return await action(identifier);
        }
        finally { _changes.Release(); }
    }

    public Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default) =>
        WithSpeakerHostAsync(host => host.GetSpeakerFeedbackStateAsync(token), token);

    public async Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default)
    {
        SpeakerPerson person = await WithSpeakerHostAsync(host => host.SaveSpeakerPersonAsync(name, personId, token), token);
        await RefreshSpeakerLabelsAsync(token);
        return person;
    }

    public async Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default)
    {
        await WithSpeakerHostAsync(async host => { await host.DeleteSpeakerPersonAsync(personId, token); return true; }, token);
        await RefreshSpeakerLabelsAsync(token);
    }

    public async Task<SpeakerPerson> SetPersonPhotoAsync(string personId, string? sourcePath, CancellationToken token = default)
    {
        SpeakerPerson person = await WithSpeakerHostAsync(host => host.SetPersonPhotoAsync(personId, sourcePath, token), token);
        await RefreshSpeakerLabelsAsync(token);
        return person;
    }

    public async Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default) =>
        await WithSpeakerHostAsync(async host => { await host.AcknowledgeSpeakerLearningAsync(token); return true; }, token);

    public Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default) =>
        WithSpeakerHostAsync(host => host.CorrectSpeakerAsync(evidenceId, personId, specialLabel, token), token);

    public Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default) =>
        WithSpeakerHostAsync(host => host.UndoSpeakerCorrectionAsync(evidenceId, token), token);

    public Task<SpeakerFeedbackResult> ReviewSpeakerSuggestionAsync(string evidenceId, string suggestedPersonId, bool confirmed, CancellationToken token = default) =>
        WithSpeakerHostAsync(host => host.ReviewSpeakerSuggestionAsync(evidenceId, suggestedPersonId, confirmed, token), token);

    private async Task RefreshSpeakerLabelsAsync(CancellationToken token)
    {
        SpeakerFeedbackState state = await GetSpeakerFeedbackStateAsync(token);
        if (_overlay is SubtitleWindow overlay) await overlay.Dispatcher.InvokeAsync(() => overlay.RefreshSpeakerNames(state));
    }
    public async Task StopIncomingAsync()
    {
        CancelPendingStartup();
        await _changes.WaitAsync();
        try { await StopIncomingCoreAsync(); }
        finally { _changes.Release(); }
    }
    public async Task StopAllAsync(IProgress<ProvisionProgress>? progress = null)
    {
        var phases = new RuntimePhaseProgress(progress);
        CancelPendingStartup();
        await _changes.WaitAsync();
        try
        {
            var failures = new List<Exception>();
            try { await StopOwnCoreAsync(phases); } catch (Exception error) { failures.Add(error); }
            try { await StopIncomingCoreAsync(phases); } catch (Exception error) { failures.Add(error); }
            foreach (OwnedSpeech speech in _pendingSpeechCleanup.Values.ToArray())
            {
                if (speech.Owner == _ownSpeech?.Owner || speech.Owner == _incomingSpeech?.Owner) continue;
                try { phases.Report(speech.Incoming ? "Stopping subtitles" : "Stopping your voice translator", "Waiting for translation tools to stop safely."); await StopSpeechAsync(speech); }
                catch (Exception error) { failures.Add(error); }
            }
            foreach (OwnedGroupSpeech groups in _pendingGroupCleanup.Values.ToArray())
                try
                {
                    phases.Report("Stopping subtitles", "Waiting for Groups speech to release its model.");
                    await StopGroupsAsync(groups);
                    if (ReferenceEquals(_incomingGroupSpeech, groups)) _incomingGroupSpeech = null;
                }
                catch (Exception error) { failures.Add(error); }
            foreach (OwnedGroupPreparation preparation in _pendingGroupPreparationCleanup.Values.ToArray())
                try { await CancelGroupPreparationAsync(preparation); }
                catch (Exception error) { failures.Add(error); }
            foreach (var entry in _translators.ToArray())
                try { phases.Report("Releasing translation models", "Waiting for translation tools to stop safely."); await entry.Value.Process.StopAsync(killTree: true); _translators.Remove(entry.Key); ForgetTranslatorHealth(entry.Key); }
                catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("Some translation tools haven't finished stopping. Retry Quit to finish releasing them.", failures);
        }
        finally { _changes.Release(); }
    }

    private async Task StopOwnCoreAsync(RuntimePhaseProgress? phases = null)
    {
        bool changed = _ownRunning || _ownSpeech is not null || _nativeOwnSpeech is not null || _muteController is not null || _proxy is not null;
        if (changed) phases?.Report("Stopping your voice translator", "Stopping microphone capture and its speech engine.");
        _ownRunning = false; _ownPlan = null; _ownSettings = null;
        // An explicit Stop discards pending sends. The separate ordinary VRC mute path can finish a captured sentence.
        var failures = new List<Exception>();
        try { await StopOwnCaptureCoreAsync(); SettingsStore.ClearOwnRuntimeEndpoints(); } catch (Exception error) { failures.Add(error); }
        if (_ownSpeech is not null)
            try { await StopSpeechAsync(_ownSpeech); _ownSpeech = null; }
            catch (Exception error) { failures.Add(error); }
        if (_nativeOwnSpeech is not null)
            try { await _nativeOwnSpeech.StopAsync(killTree: true); _nativeOwnSpeech = null;
                if (_ownSpeechPort is int port) ForgetHealth("windows-" + ManagedServiceKind.OwnSpeech + "-" + port); }
            catch (Exception error) { failures.Add(error); }
        if (_nativeOwnSpeech is null && _ownSpeech is null) _ownSpeechPort = null;
        try { await ReleaseTranslatorAsync(_ownTranslatorPort, phases); _ownTranslatorPort = null; }
        catch (Exception error) { failures.Add(error); }
        if (changed) { Log("Your-voice translation stopped; only SayWhat?-owned processes were stopped."); NotifyState(); }
        if (failures.Count > 0) throw new AggregateException("Your voice translator has not finished stopping. Retry Stop all to release its remaining tools.", failures);
    }

    private async Task StopIncomingCoreAsync(RuntimePhaseProgress? phases = null)
    {
        bool changed = _incomingRunning || _incomingSpeech is not null || _nativeIncomingSpeech is not null || _incomingGroupSpeech is not null || _overlay is not null;
        if (changed) phases?.Report("Stopping subtitles", "Stopping playback capture and its speech engine.");
        _incomingRunning = false; _incomingPlan = null; _incomingSettings = null;
        SubtitleWindow? overlay = _overlay; _overlay = null;
        if (overlay is not null)
        {
            _closingOverlay = true;
            try { await overlay.Dispatcher.InvokeAsync(() => { overlay.Closed -= OverlayClosed; overlay.Close(); }); }
            finally { _closingOverlay = false; }
            _retiringOverlays.Add(overlay);
        }
        Exception? speechFailure = null;
        try { await StopIncomingSpeechCoreAsync(); }
        catch (Exception error) { speechFailure = error; }
        int? port = _incomingTranslatorPort; _incomingTranslatorPort = null;
        await ReleaseTranslatorAsync(port, phases);
        Exception? captureFailure = null;
        foreach (SubtitleWindow retiring in _retiringOverlays.ToArray())
        {
            try { await retiring.ServiceStopped.WaitAsync(TimeSpan.FromSeconds(5)); _retiringOverlays.Remove(retiring); }
            catch (TimeoutException error) { captureFailure = error; Log("Incoming capture has not acknowledged shutdown; references are retained for a retry."); }
        }
        if (changed) { Log("Incoming overlay stopped; your-voice translation was left unchanged."); NotifyState(); }
        if (speechFailure is not null) throw speechFailure;
        if (captureFailure is not null) throw new InvalidOperationException("Subtitle capture hasn't finished stopping. Retry Quit to finish releasing it.", captureFailure);
    }

    private async void OverlayClosed(object? sender, EventArgs eventArgs)
    {
        if (_closingOverlay) return;
        try { await StopIncomingAsync(); }
        catch (Exception error) { Log("Incoming cleanup failed: " + error.GetType().Name + ". See process exit metadata."); }
    }

    private WslSpeechRegistration RequireSpeechRegistration() => _provisioner.SpeechRegistration
        ?? throw new InvalidOperationException("Complete Speech setup first: select an installed WSL distribution and register/build the local realtime Voxtral server. Starting never installs packages or downloads models.");

    private void ValidateSpeechHelpers(WslSpeechRegistration speech, int port)
    {
        string validationOwner = Guid.NewGuid().ToString("N");
        _provisioner.BuildWslSpeechStartInfo(speech, port, validationOwner);
        _provisioner.BuildWslSpeechStopInfo(speech, port, validationOwner);
    }

    private void ValidateGroupsHelpers(WslSpeechRegistration speech, AppSettings settings)
    {
        string owner = Guid.NewGuid().ToString("N");
        VibeVoiceRuntime.ValidateEndpoint(settings.VibeVoiceEndpoint);
        VibeVoiceRuntime.BuildStartInfo(speech, _provisioner.DataRoot, settings.VibeVoiceModelId, owner);
        VibeVoiceRuntime.BuildStopStartInfo(speech, owner);
    }

    private async Task<VibeVoicePreparation> CheckGroupsPreparedCoreAsync(AppSettings settings,
        WslSpeechRegistration registration, CancellationToken token)
    {
        string? receipt = null;
        OwnedProcess? process = null;
        try
        {
            process = StartOwned(VibeVoiceRuntime.CreateCheckStartInfo(registration, _provisioner.DataRoot, settings.VibeVoiceModelId),
                "Groups setup check", (line, stderr) =>
                {
                    if (!stderr && line.Length <= 8192 && line.StartsWith('{') && line.Contains("\"prepared\"", StringComparison.Ordinal))
                        Interlocked.Exchange(ref receipt, line);
                });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            await process.Exited.WaitAsync(deadline.Token);
            await process.OutputDrained.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            if (process.ExitCode is not 0 and not 3)
                throw new InvalidOperationException("Could not check Groups setup. Open Diagnostics for details, then retry.");
            return VibeVoiceRuntime.ParsePreparation(receipt ?? "", settings.VibeVoiceModelId);
        }
        finally { if (process is not null) await process.StopAsync(killTree: false); }
    }

    private async Task RequireGroupsPreparedCoreAsync(AppSettings settings,
        WslSpeechRegistration registration, CancellationToken token)
    {
        VibeVoicePreparation result = await CheckGroupsPreparedCoreAsync(settings, registration, token);
        if (!result.Prepared)
            throw new InvalidOperationException("Groups mode is not prepared yet. Choose Groups in Settings and prepare its speech tools first. Starting subtitles never downloads or installs files.");
    }

    private void ValidateNativeIncomingSpeech(AppSettings settings)
    {
        if (IncomingRecognitionCatalog.UsesFireRed(settings))
        {
            if (!new FireRedSpeechProvisioner(_provisioner.DataRoot).IsReady(settings.IncomingSpeechEngine))
                throw new ModelSelectionUserException("Prepare the selected subtitle speech model before starting.");
        }
        else _ = new NativeSpeechProvisioner(_provisioner.DataRoot).BuildStartInfo(IncomingRecognitionCatalog.CreateNativeSettings(settings), 8081);
    }

    private async Task StartIncomingSpeechCoreAsync(AppSettings settings, WslSpeechRegistration? registration,
        CancellationToken token, RuntimePhaseProgress? phases)
    {
        if (_incomingSpeech is not null || _nativeIncomingSpeech is not null || _incomingGroupSpeech is not null)
            throw new InvalidOperationException("The previous subtitle speech engine has not stopped yet.");
        if (RuntimeProfiles.UsesGroups(settings))
            _incomingGroupSpeech = await StartManagedGroupsAsync(registration!, settings, token, phases);
        else if (IncomingRecognitionCatalog.UsesFireRed(settings))
        {
            // The subtitle service owns this in-process recognizer and releases it
            // when capture stops. Preparation/readiness never loads its weights.
            ValidateNativeIncomingSpeech(settings);
        }
        else if (settings.IncomingSpeechEngine == "nemotron")
        {
            phases?.Report("Starting subtitle speech recognition", "Loading the selected Windows speech model.");
            var started = await StartManagedWindowsAsync(settings, ManagedServiceKind.IncomingSpeech,
                port => new NativeSpeechProvisioner(_provisioner.DataRoot).BuildStartInfo(IncomingRecognitionCatalog.CreateNativeSettings(settings), port),
                "Subtitle Windows speech recognition", port => LocalEndpoint(port) + "/ready",
                root => root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True,
                TimeSpan.FromSeconds(120),
                (port, process) => { _nativeIncomingSpeech = process; _incomingSpeechPort = port; },
                (_, process) => { if (ReferenceEquals(_nativeIncomingSpeech, process)) { _nativeIncomingSpeech = null; _incomingSpeechPort = null; } }, token);
            settings.IncomingSpeechEndpoint = LocalEndpoint(started.Port);
        }
        else _incomingSpeech = await StartManagedVoxtralAsync(registration!, settings, incoming: true, token, phases);
    }

    private async Task StopIncomingSpeechCoreAsync()
    {
        var failures = new List<Exception>();
        if (_incomingSpeech is not null)
            try { await StopSpeechAsync(_incomingSpeech); _incomingSpeech = null; }
            catch (Exception error) { failures.Add(error); }
        if (_nativeIncomingSpeech is not null)
            try { await _nativeIncomingSpeech.StopAsync(killTree: true); _nativeIncomingSpeech = null;
                if (_incomingSpeechPort is int port) ForgetHealth("windows-" + ManagedServiceKind.IncomingSpeech + "-" + port); }
            catch (Exception error) { failures.Add(error); }
        if (_incomingGroupSpeech is not null)
            try { await StopGroupsAsync(_incomingGroupSpeech); _incomingGroupSpeech = null; }
            catch (Exception error) { failures.Add(error); }
        if (_incomingSpeech is null && _nativeIncomingSpeech is null && _incomingGroupSpeech is null) _incomingSpeechPort = null;
        if (failures.Count > 0) throw new AggregateException("Subtitle speech shutdown was not confirmed. Retry Stop all before starting another mode.", failures);
    }

    private void WatchActiveIncomingSpeech()
    {
        if (_incomingSpeech is not null) WatchIncoming(_incomingSpeech.Process);
        if (_nativeIncomingSpeech is not null) WatchIncoming(_nativeIncomingSpeech);
        if (_incomingGroupSpeech is not null) WatchIncoming(_incomingGroupSpeech.Process);
    }

    private async Task<OwnedGroupSpeech> StartGroupsAsync(WslSpeechRegistration registration, AppSettings settings, int port,
        CancellationToken token, RuntimePhaseProgress? phases, Action? ownershipObserved = null)
    {
        phases?.Report("Loading Groups speech model", "Loading the selected local speech model. Subtitles will resume when it is ready.");
        string owner = Guid.NewGuid().ToString("N");
        int bindConflict = 0;
        bool reportedOwnership = false;
        OwnedProcess process = StartOwned(VibeVoiceRuntime.BuildStartInfo(registration, _provisioner.DataRoot,
            settings.VibeVoiceModelId, owner, port), "Groups speech", (line, _) =>
            { if (line == $"LOCALTALK_PORT_CONFLICT={owner}:{port}") Interlocked.Exchange(ref bindConflict, 1); });
        var groups = new OwnedGroupSpeech(process, registration, owner, settings.VibeVoiceModelId, port);
        TrackHealth("groups-" + owner, process, LocalEndpoint(port) + "/health");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(150));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!process.IsAlive)
                    throw new InvalidOperationException("Groups speech exited before it was ready. Open Diagnostics for startup details.");
                try
                {
                    VibeVoiceHealth health = await VibeVoiceRuntime.ReadHealthAsync(_http, settings.VibeVoiceEndpoint,
                        owner, settings.VibeVoiceModelId, deadline.Token);
                    if (!reportedOwnership)
                    {
                        reportedOwnership = true;
                        ownershipObserved?.Invoke();
                    }
                    if (health.Status == "error")
                        throw new InvalidOperationException("Groups speech could not load: " + (health.Error ?? "Unknown local model error."));
                    if (health.Ready && health.ActiveSessions == 0) return groups;
                }
                catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !deadline.IsCancellationRequested)
                { /* Keep waiting while the owned process is starting. */ }
                await Task.Delay(250, deadline.Token);
            }
        }
        catch (Exception startupError)
        {
            try { if (!process.IsAlive) await process.OutputDrained.WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { }
            try { await StopGroupsAsync(groups); }
            catch (Exception cleanupError) { throw new AggregateException("Groups startup failed and model cleanup needs a retry.", startupError, cleanupError); }
            if (Volatile.Read(ref bindConflict) != 0 && !token.IsCancellationRequested)
                throw new ManagedPortConflictException("Groups speech recognition", port, startupError);
            if (startupError is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("Groups speech did not become ready in time. Its owned startup process was stopped.", startupError);
            throw;
        }
    }

    private async Task StopGroupsAsync(OwnedGroupSpeech groups)
    {
        _pendingGroupCleanup[groups.Owner] = groups;
        OwnedProcess? stopper = null;
        Exception? failure = null;
        try
        {
            stopper = StartOwned(VibeVoiceRuntime.BuildStopStartInfo(groups.Registration, groups.Owner, groups.Port), "Groups speech cleanup");
            await stopper.Exited.WaitAsync(TimeSpan.FromSeconds(20));
            if (stopper.ExitCode is not 0)
                throw new InvalidOperationException("Groups token-owned shutdown returned " + stopper.ExitCode + ".");
        }
        catch (Exception error) { failure = error; Log("Groups cleanup was not confirmed; its exact ownership reference remains retryable."); }
        finally
        {
            if (stopper is not null) await stopper.StopAsync(killTree: false);
            await groups.Process.StopAsync(killTree: false);
        }
        if (failure is not null)
            throw new InvalidOperationException("Groups shutdown was not confirmed. Retry Stop all to finish releasing its speech model.", failure);
        _pendingGroupCleanup.Remove(groups.Owner);
        ForgetHealth("groups-" + groups.Owner);
    }

    private async Task CancelGroupPreparationAsync(OwnedGroupPreparation preparation)
    {
        _pendingGroupPreparationCleanup[preparation.Owner] = preparation;
        OwnedProcess? stopper = null;
        Exception? failure = null;
        try
        {
            stopper = StartOwned(VibeVoiceRuntime.BuildCancelPreparationInfo(preparation.Registration, preparation.Owner), "Groups preparation cancellation");
            await stopper.Exited.WaitAsync(TimeSpan.FromSeconds(20));
            if (stopper.ExitCode is not 0) throw new InvalidOperationException("Groups setup cancellation could not verify completion.");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (stopper is not null) await stopper.StopAsync(killTree: false);
            await preparation.Process.StopAsync(killTree: false);
        }
        if (failure is not null)
            throw new InvalidOperationException("Groups preparation has not finished stopping. Retry Stop all before preparing another model.", failure);
        _pendingGroupPreparationCleanup.Remove(preparation.Owner);
    }

    private void ValidateManagedAssets(RuntimePlan plan)
    {
        if (!plan.Managed) { ValidateLocalEndpoint(plan.Endpoint); return; }
        if(LocalTalkProvisioner.CheckNativeRuntimePrerequisite() is string prerequisite)
            throw new InvalidOperationException(prerequisite);
        if (_provisioner.ResolveModelPath(plan.ModelId) is null)
            throw new InvalidOperationException("The selected translation model is not installed. Download or import it in Model setup before starting.");
        // This checks the runtime and model paths without running anything.
        _provisioner.BuildLlamaStartInfo(plan.RuntimeId, _provisioner.ResolveModelPath(plan.ModelId)!, plan.ModelAlias, plan.Port);
    }

    private async Task<RuntimePlan> AcquireTranslatorAsync(RuntimePlan plan, AppSettings settings, bool incoming, CancellationToken cancellationToken,
        RuntimePhaseProgress? phases = null)
    {
        if (!plan.Managed)
        {
            phases?.ReportModel("Checking translation tools", plan.ModelAlias);
            await RequireAdvancedTranslatorAsync(plan, cancellationToken);
            return plan; // Existing endpoint is not owned, never stopped or automatically loaded.
        }
        foreach (var entry in _translators)
        {
            TranslatorProcess existing = entry.Value;
            if (existing.Key != plan.TranslationKey) continue;
            if (!existing.Process.IsAlive)
                throw new InvalidOperationException("A running translator uses different settings or exited. Stop the active features before changing their model/runtime.");
            phases?.ReportModel("Checking the shared translation model", plan.ModelAlias);
            RuntimePlan retained = AtSessionPort(plan, entry.Key);
            await WaitTranslatorAsync(retained, existing.Process, TimeSpan.FromSeconds(5), cancellationToken);
            existing.Users++;
            return retained;
        }
        string path = _provisioner.ResolveModelPath(plan.ModelId)!;
        phases?.ReportModel("Loading translation model", plan.ModelAlias);
        ManagedServiceKind service = incoming && !plan.Shared ? ManagedServiceKind.IncomingTranslation : ManagedServiceKind.OwnTranslation;
        var started = await StartManagedWindowsAsync(settings, service,
            port => _provisioner.BuildLlamaStartInfo(plan.RuntimeId, path, plan.ModelAlias, port), "Local translator",
            port => LocalEndpoint(port) + "/v1/models", root => HasModel(root, plan.ModelAlias), TimeSpan.FromSeconds(90),
            (port, process) => _translators.Add(port, new TranslatorProcess(process, plan.TranslationKey) { Users = 1 }),
            (port, process) => { if (_translators.TryGetValue(port, out var owned) && ReferenceEquals(owned.Process, process)) _translators.Remove(port); },
            cancellationToken);
        RuntimePlan resolved = AtSessionPort(plan, started.Port);
        _ = started.Process.Exited.ContinueWith(_ => _ = HandleTranslatorExitAsync(started.Port, started.Process), TaskScheduler.Default);
        Log($"Installed local translator ready on port {started.Port}; model {plan.ModelId}. No external model manager was changed.");
        return resolved;
    }

    private async Task ReleaseTranslatorAsync(int? port, RuntimePhaseProgress? phases = null)
    {
        if (port is null || !_translators.TryGetValue(port.Value, out TranslatorProcess? translator)) return;
        translator.Users--;
        if (translator.Users > 0) return;
        phases?.Report("Releasing translation models", "Waiting for translation tools to stop safely.");
        await translator.Process.StopAsync(killTree: true);
        _translators.Remove(port.Value);
        ForgetHealth("windows-" + ManagedServiceKind.OwnTranslation + "-" + port.Value);
        ForgetHealth("windows-" + ManagedServiceKind.IncomingTranslation + "-" + port.Value);
        Log($"Managed translator {port} unloaded because neither selected feature uses it. Existing external endpoints were not touched.");
    }

    private async Task<OwnedSpeech> StartSpeechAsync(WslSpeechRegistration registration, int port, CancellationToken cancellationToken,
        RuntimePhaseProgress? phases = null, bool incoming = false, Action? ownershipObserved = null)
    {
        phases?.Report("Starting speech recognition", "Waiting for speech recognition to be ready.");
        string owner = Guid.NewGuid().ToString("N");
        var listenerProof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int bindConflict = 0;
        OwnedProcess wrapper = StartOwned(_provisioner.BuildWslSpeechStartInfo(registration, port, owner), "Realtime speech " + port,
            (line, _) =>
            {
                if (line == $"SAYWHAT_OWNED_LISTENER {owner} {port}") listenerProof.TrySetResult();
                else if (line == $"LOCALTALK_PORT_CONFLICT={owner}:{port}") Interlocked.Exchange(ref bindConflict, 1);
            });
        var speech = new OwnedSpeech(wrapper, registration, port, owner, incoming);
        TrackHealth("wsl-" + owner, wrapper, LocalEndpoint(port) + "/health");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            Task first = await Task.WhenAny(listenerProof.Task, wrapper.Exited).WaitAsync(deadline.Token);
            if (first != listenerProof.Task || !listenerProof.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("The owned speech wrapper exited before confirming its own listener.");
            ownershipObserved?.Invoke();
            await WaitSpeechAsync(port, wrapper, deadline.Token);
            return speech;
        }
        catch (Exception startupError)
        {
            try { if (!wrapper.IsAlive) await wrapper.OutputDrained.WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { }
            phases?.Report(incoming ? "Stopping subtitles" : "Stopping your voice translator", "Waiting for translation tools to stop safely.");
            await StopSpeechAsync(speech);
            if (Volatile.Read(ref bindConflict) != 0 && !cancellationToken.IsCancellationRequested)
                throw new ManagedPortConflictException(incoming ? "Subtitle speech recognition" : "Your voice recognition", port, startupError);
            if (startupError is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("The owned speech recognizer did not become ready in time.", startupError);
            throw;
        }
    }

    private async Task StopSpeechAsync(OwnedSpeech speech)
    {
        _pendingSpeechCleanup[speech.Owner] = speech;
        // The unique owner GUID is checked together with Linux executable/PID/birth/port by the stop wrapper.
        // It is safe to issue even when startup failed: a different application's marker cannot match.
        OwnedProcess? stopper = null;
        Exception? failure = null;
        try
        {
            stopper = StartOwned(_provisioner.BuildWslSpeechStopInfo(speech.Registration, speech.Port, speech.Owner), "Speech cleanup " + speech.Port);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await stopper.Exited.WaitAsync(deadline.Token);
            if (stopper.ExitCode is not 0) throw new InvalidOperationException($"Token-owned speech cleanup {speech.Port} returned {stopper.ExitCode}.");
        }
        catch (Exception error) { failure = error; Log($"Speech cleanup {speech.Port}: {error.GetType().Name}. The exact owned Windows wrapper will be closed; failed cleanup remains retryable."); }
        finally
        {
            if (stopper is not null) await stopper.StopAsync(killTree: false);
            await speech.Process.StopAsync(killTree: false);
        }
        if (failure is not null)
        {
            _pendingSpeechCleanup[speech.Owner] = speech;
            throw new InvalidOperationException("Speech shutdown wasn't confirmed. Retry Quit to finish releasing the speech model.", failure);
        }
        _pendingSpeechCleanup.Remove(speech.Owner);
        ForgetHealth("wsl-" + speech.Owner);
    }

    private async Task WaitSpeechAsync(int port, OwnedProcess process, CancellationToken cancellationToken)
    {
        await WaitHealthAsync(new Uri($"http://127.0.0.1:{port}/health"), process, TimeSpan.FromSeconds(90),
            root => root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True
                && (!root.TryGetProperty("busy", out JsonElement busy) || busy.ValueKind != JsonValueKind.True), cancellationToken);
    }

    // Progress belongs to this exact caller/operation, never a global runtime event. It reports
    // only genuine phase transitions, not health polling, output text, or invented percentages.
    private sealed class RuntimePhaseProgress
    {
        private readonly IProgress<ProvisionProgress>? _target;
        private string? _lastStage, _lastDetail;
        public RuntimePhaseProgress(IProgress<ProvisionProgress>? target) => _target = target;
        public void Report(string stage, string? detail = null)
        {
            if (_target is null || stage == _lastStage && detail == _lastDetail) return;
            _lastStage = stage; _lastDetail = detail;
            try { _target.Report(new(stage, stage) { Detail = detail }); }
            catch (Exception error)
            {
                // UI/reporting failures must never skip a retained-process release or leave a
                // partially created startup resource behind. Do not retain callback payloads.
                try { Log("Runtime progress callback failed (" + error.GetType().Name + "); engine lifecycle continued."); }
                catch { /* Even diagnostic IO is secondary to safe lifecycle operations. */ }
            }
        }
        public void ReportModel(string stage, string alias) => Report(stage, "Model: " + SafeModelAlias(alias));
    }

    internal static string SafeModelAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return "";
        string safe = new(alias.Where(character => !char.IsControl(character) &&
            char.GetUnicodeCategory(character) is not (System.Globalization.UnicodeCategory.Format or
                System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)).ToArray());
        safe = safe.Trim();
        return safe.Length <= 120 ? safe : safe[..120] + "…";
    }

    internal static int RunProgressOfflineChecks()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Runtime progress self-check failed."); checks++; }
        var reports = new List<ProvisionProgress>();
        var phases = new RuntimePhaseProgress(new InlinePhaseProgress(reports.Add));
        phases.Report("Checking translation tools"); phases.Report("Checking translation tools");
        phases.Report("Starting speech recognition", "Waiting for speech recognition to be ready.");
        phases.Report("Starting speech recognition", "Waiting for speech recognition to be ready.");
        Check(reports.Count == 2);
        Check(reports.All(report => report.Fraction is null && report.StepIndex is null && report.StepCount is null));
        Check(reports.Select(report => report.Stage).SequenceEqual(new[] { "Checking translation tools", "Starting speech recognition" }));
        Check(SafeModelAlias("  hy-mt2\r\n\u001b\u202E7b  ") == "hy-mt27b");
        Check(SafeModelAlias(new string('x', 150)).Length == 121);
        Check(SafeModelAlias(null) == "");
        int calls = 0;
        var unreliable = new RuntimePhaseProgress(new InlinePhaseProgress(_ => { calls++; if (calls == 1) throw new InvalidOperationException("callback detail is not retained"); }));
        unreliable.Report("Stopping subtitles"); unreliable.Report("Releasing translation models");
        Check(calls == 2);
        new RuntimePhaseProgress(null).Report("Checking translation tools");
        Check(true);
        return checks;
    }

    private sealed class InlinePhaseProgress(Action<ProvisionProgress> receive) : IProgress<ProvisionProgress>
    { public void Report(ProvisionProgress value) => receive(value); }

    private Task WaitTranslatorAsync(RuntimePlan plan, OwnedProcess process, TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitHealthAsync(new Uri(plan.Endpoint.TrimEnd('/') + "/models"), process, timeout,
            root => HasModel(root, plan.ModelAlias), cancellationToken, requireOwnedListener: true);

    private async Task RequireAdvancedTranslatorAsync(RuntimePlan plan, CancellationToken cancellationToken)
    {
        ValidateLocalEndpoint(plan.Endpoint);
        try
        {
            await WaitHealthAsync(new Uri(plan.Endpoint.TrimEnd('/') + "/models"), null, TimeSpan.FromSeconds(4),
                root => HasModel(root, plan.ModelAlias), cancellationToken);
            await VerifyAdvertisedResidencyAsync(plan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            throw new InvalidOperationException("The advanced local translator endpoint is not ready with the selected model. Start/load it in your existing local model application, or choose SayWhat?'s built-in runtime. SayWhat? did not load or replace an external model.", error);
        }
    }

    private async Task VerifyAdvertisedResidencyAsync(RuntimePlan plan, CancellationToken cancellationToken)
    {
        // Bionic/LM Studio's OpenAI model list can advertise downloaded but UNLOADED models.
        // If its native residency API exists, require a loaded instance rather than triggering JIT loading later.
        string apiRoot = plan.Endpoint.TrimEnd('/');
        if (apiRoot.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) apiRoot = apiRoot[..^3];
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(TimeSpan.FromSeconds(2));
        JsonDocument? json = null;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(apiRoot + "/api/v1/models", HttpCompletionOption.ResponseHeadersRead, probe.Token);
            if (response.IsSuccessStatusCode)
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(probe.Token);
                json = await JsonDocument.ParseAsync(stream, cancellationToken: probe.Token);
            }
        }
        catch (HttpRequestException) { }
        catch (JsonException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        try
        {
            if (json is not null && json.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array
                && models.EnumerateArray().Any(model => model.TryGetProperty("loaded_instances", out _)))
            {
                _knownResidencyEndpoints.Add(apiRoot);
                bool loaded = models.EnumerateArray().Any(model =>
                {
                    if (!model.TryGetProperty("loaded_instances", out JsonElement instances) || instances.ValueKind != JsonValueKind.Array) return false;
                    bool matchingKey = model.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String && key.GetString() == plan.ModelAlias;
                    return instances.EnumerateArray().Any(instance => matchingKey || instance.TryGetProperty("id", out JsonElement id)
                        && id.ValueKind == JsonValueKind.String && id.GetString() == plan.ModelAlias);
                });
                if (!loaded) throw new InvalidOperationException("The external model is downloaded but not loaded. Load it manually in your local model application; SayWhat? will not trigger its just-in-time loader.");
                return;
            }
        }
        finally { json?.Dispose(); }
        // The known/default Bionic endpoint must fail closed if its residency query disappears or times out.
        if (_knownResidencyEndpoints.Contains(apiRoot) || new Uri(apiRoot).Port == 1234)
            throw new InvalidOperationException("Could not verify the external model's loaded residency. Start/load it manually and restore its local model-status API, or choose SayWhat?'s built-in runtime. An advertised download is not proof of a loaded model.");
        // Generic servers are accepted only with a separate positive ready/ok health acknowledgement.
        // Merely advertising a model in /v1/models is insufficient and may hide a just-in-time loader.
        await WaitHealthAsync(new Uri(apiRoot + "/health"), null, TimeSpan.FromSeconds(3), root =>
            root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True
            || root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String
                && status.GetString() is "ok" or "ready", cancellationToken);
    }

    private async Task WaitHealthAsync(Uri endpoint, OwnedProcess? process, TimeSpan timeout,
        Func<JsonElement, bool> accepted, CancellationToken cancellationToken, bool requireOwnedListener = false,
        Action? ownershipObserved = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        string last = "not reachable";
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process is not null && !process.IsAlive)
                    throw new InvalidOperationException($"{process.Label} exited before it was ready (code {process.ExitCode}). Review Diagnostics for startup metadata.");
                if (requireOwnedListener && (process is null || !ManagedPortAllocator.IsOwnedTcpListener(endpoint.Port, process.ProcessId)))
                {
                    last = "waiting for the exact owned process to bind";
                    await Task.Delay(100, deadline.Token);
                    continue;
                }
                if (requireOwnedListener) ownershipObserved?.Invoke();
                using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    using HttpResponseMessage response = await _http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, request.Token);
                    last = "HTTP " + (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        await using Stream stream = await response.Content.ReadAsStreamAsync(request.Token);
                        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: request.Token);
                        if (accepted(json.RootElement) && (!requireOwnedListener ||
                            process is not null && process.IsAlive && ManagedPortAllocator.IsOwnedTcpListener(endpoint.Port, process.ProcessId))) return;
                        last = "endpoint responded but selected model/capacity is not ready";
                    }
                }
                catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !deadline.IsCancellationRequested)
                { last = error.GetType().Name; }
                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"Local engine readiness timed out at {endpoint.Host}:{endpoint.Port} ({last}). Owned startup processes will be stopped."); }
    }

    private static bool HasModel(JsonElement root, string alias) => root.TryGetProperty("data", out JsonElement data)
        && data.ValueKind == JsonValueKind.Array && data.EnumerateArray().Any(item => item.TryGetProperty("id", out JsonElement id)
            && id.ValueKind == JsonValueKind.String && id.GetString() == alias);

    internal static string? SafeDiagnosticLine(string line)
    {
        line = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim();
        // Check the ENTIRE line before truncation. A content marker after character 512 is still rejected.
        if (line.Length == 0 || Regex.IsMatch(line,
            @"(?i)\b(?:prompts?|transcripts?|translations?|source(?:_text)?|messages?|content|texts?|chat_template|completions?|request_body|response_body|rendered|tokenized)\b|<\||\|>")) return null;
        // Native aborts previously disappeared behind the privacy filter. Keep only
        // a fixed error category, never an arbitrary assertion/body/spoken string.
        if (Regex.IsMatch(line, @"(?i)GGML_ASSERT|assertion[^\r\n]*failed")) return "Native engine assertion failed; raw detail omitted.";
        if (Regex.IsMatch(line, @"(?i)CUDA error|CUDA_ERROR_")) return "Native engine CUDA error; raw detail omitted.";
        if (Regex.IsMatch(line, @"(?i)terminate called|double free|\bAborted\b")) return "Native engine aborted; raw detail omitted.";
        if (line.StartsWith('{'))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(line);
                if (!json.RootElement.TryGetProperty("event", out JsonElement ev) || ev.ValueKind != JsonValueKind.String
                    || ev.GetString() is not ("startup" or "model_loaded" or "context_ready" or "server_listening" or "error")) return null;
                var fields = new List<string>();
                foreach (string name in new[] { "event", "backend", "code", "listen", "port", "ready", "busy", "sample_rate", "max_streams", "server_version" })
                {
                    if (!json.RootElement.TryGetProperty(name, out JsonElement value)) continue;
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        string text = value.GetString() ?? "";
                        if (text.Length <= 80 && Regex.IsMatch(text, @"^[A-Za-z0-9_.:/ -]*$")) fields.Add(name + "=" + text);
                    }
                    else if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        fields.Add(name + "=" + value.GetRawText());
                }
                return string.Join("; ", fields); // Arbitrary message/detail/body strings are NEVER retained.
            }
            catch (JsonException) { return null; }
        }
        // Recognized loader/backend prefixes, not arbitrary root stdout or raw spoken sentences.
        bool enginePrefix = Regex.IsMatch(line,
            @"^(?:ggml_[A-Za-z0-9_]+|llama_model_[A-Za-z0-9_]+|load_tensors|load_backend|print_info|init|main|srv (?:init|load_model)):\s", RegexOptions.CultureInvariant);
        bool helperFailure = Regex.IsMatch(line,
            @"^\[\d{4}-\d{2}-\d{2} [0-9:.]+\]\s(?:FATAL:|ERROR\s|OVERLOADED\b)", RegexOptions.CultureInvariant);
        if (!enginePrefix && !helperFailure) return null;
        // Refuse embedded object bodies/control characters; no multiline record or terminal-control injection.
        if (line.Contains('{') || line.Any(character => char.IsControl(character))) return null;
        return line.Length <= 512 ? line : line[..512] + "…";
    }

    internal static RuntimePlan BuildPlan(AppSettings settings, bool incoming)
    {
        if (!settings.UseManagedTranslator)
        {
            string endpoint = incoming && !settings.ShareTranslationModel ? settings.TranslatorBaseUrl : settings.OwnTranslatorBaseUrl;
            string alias = incoming && !settings.ShareTranslationModel ? settings.TranslatorModel : settings.OwnTranslatorModel;
            ValidateLocalEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Choose a translator model first.");
            ValidateTranslationSelection(settings, alias, incoming, managed: false);
            return new(false, 0, "", "", alias.Trim(), endpoint.TrimEnd('/'), settings.ShareTranslationModel);
        }
        string id = incoming && !settings.ShareTranslationModel ? settings.IncomingManagedModelId : settings.OwnManagedModelId;
        ProvisionModelChoice model = LocalTalkProvisioner.ModelCatalog.FirstOrDefault(item => item.Id == id && item.Role == ProvisionModelRole.Translation)
            ?? throw new ArgumentException("Choose an installed translation model from SayWhat?'s catalog.");
        if (!LocalTalkProvisioner.RuntimeCatalog.Any(runtime => runtime.Id == settings.ManagedRuntimeId))
            throw new ArgumentException("Choose a local translation runtime from Setup first.");
        ValidateTranslationSelection(settings, model.ApiAlias, incoming, managed: true);
        int port = incoming && !settings.ShareTranslationModel
            ? settings.AutomaticManagedPorts ? 1236 : settings.IncomingTranslationPort
            : settings.AutomaticManagedPorts ? 1235 : settings.OwnTranslationPort;
        return new(true, port, settings.ManagedRuntimeId, model.Id, model.ApiAlias, $"http://127.0.0.1:{port}/v1", settings.ShareTranslationModel)
            { ConfiguredPort = port, AutomaticPort = settings.AutomaticManagedPorts };
    }

    private static void ValidateTranslationSelection(AppSettings settings, string alias, bool incoming, bool managed)
    {
        if (!managed && TranslationModelCapabilities.RequiresManagedRuntime(alias))
            throw new ModelSelectionUserException("TranslateGemma requires SayWhat?'s built-in runtime and its compatible translation template. Select the built-in runtime in Settings.");
        string source = incoming ? settings.IncomingSourceLanguage : settings.OwnVoiceSourceLanguage;
        IEnumerable<string> targets = incoming ? new[] { settings.IncomingTargetLanguage } : settings.EffectiveOwnVoiceTargetLanguages();
        if (TranslationModelCapabilities.Validate(alias, source, targets) is { } invalid)
            throw new ModelSelectionUserException(invalid);
    }

    private static void ApplyPlan(AppSettings settings, RuntimePlan plan, bool incoming)
    {
        if (plan.Managed)
        {
            if (plan.Shared)
            {
                settings.OwnTranslationEndpoint = settings.IncomingTranslationEndpoint = plan.Endpoint;
                settings.OwnTranslatorModel = settings.TranslatorModel = plan.ModelAlias;
            }
            else if (incoming) { settings.IncomingTranslationEndpoint = plan.Endpoint; settings.TranslatorModel = plan.ModelAlias; }
            else { settings.OwnTranslationEndpoint = plan.Endpoint; settings.OwnTranslatorModel = plan.ModelAlias; }
            return;
        }
        if (plan.Shared) settings.OwnTranslationEndpoint = settings.IncomingTranslationEndpoint = "";
        else if (incoming) settings.IncomingTranslationEndpoint = ""; else settings.OwnTranslationEndpoint = "";
        if (plan.Shared)
        {
            settings.OwnTranslatorBaseUrl = settings.TranslatorBaseUrl = plan.Endpoint;
            settings.OwnTranslatorModel = settings.TranslatorModel = plan.ModelAlias;
        }
        else if (incoming)
        { settings.TranslatorBaseUrl = plan.Endpoint; settings.TranslatorModel = plan.ModelAlias; }
        else { settings.OwnTranslatorBaseUrl = plan.Endpoint; settings.OwnTranslatorModel = plan.ModelAlias; }
    }

    private static void CheckSharedCompatibility(RuntimePlan plan, RuntimePlan? active)
    {
        if (active is null) return;
        if (active.Shared != plan.Shared || active.Managed != plan.Managed || (plan.Shared && active.TranslationKey != plan.TranslationKey))
            throw new InvalidOperationException("Stop the active translations before changing shared/separate mode, runtime, or the shared model. A running feature will not be silently reconfigured.");
    }

    internal static void ValidateLocalEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback
            || uri.Scheme is not "http" and not "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Advanced translation must use a local loopback HTTP endpoint, without credentials, query strings or fragments. Cloud endpoints are not supported.");
    }

    // Pure configuration/interop-layout tests: no processes, HTTP, WSL, audio devices or native job APIs are invoked.
    internal static int RunOfflineSelfTests()
    {
        int checks = 0;
        void Require(bool value, string name) { if (!value) throw new InvalidOperationException("Runtime planning test failed: " + name); checks++; }
        void Reject(Action operation, string name)
        {
            try { operation(); } catch (ArgumentException) { checks++; return; }
            throw new InvalidOperationException("Unsafe runtime configuration accepted: " + name);
        }
        var settings = new AppSettings
        {
            UseManagedTranslator = true, ShareTranslationModel = true,
            OwnManagedModelId = "qwen3-8b-q4_k_m", IncomingManagedModelId = "qwen3-4b-q4_k_m",
            ManagedRuntimeId = "llama-vulkan", OwnVoiceTargetLanguage = "Chinese", IncomingTargetLanguage = "English",
            OwnTranslatorBaseUrl = "http://127.0.0.1:15000/v1", TranslatorBaseUrl = "http://127.0.0.1:15001/v1"
        };
        RuntimePlan own = BuildPlan(settings, false), incoming = BuildPlan(settings, true);
        Require(own.Port == 1235 && incoming.Port == 1235, "shared port");
        Require(own.TranslationKey == incoming.TranslationKey, "shared instance key");
        Require(incoming.ModelId == settings.OwnManagedModelId, "shared model uses one selected resident copy");
        Require(own.ModelAlias == "qwen/qwen3-8b", "catalog API alias");
        Require(own.Endpoint == "http://127.0.0.1:1235/v1", "managed loopback endpoint");
        Require(settings.OwnVoiceTargetLanguage == "Chinese" && settings.IncomingTargetLanguage == "English", "targets unchanged by planning");
        AppSettings shared = settings.Copy(); RuntimePlan sharedSession = AtSessionPort(own, 50500);
        ApplyPlan(shared, sharedSession, incoming: false);
        Require(shared.OwnTranslationEndpoint == sharedSession.Endpoint && shared.IncomingTranslationEndpoint == sharedSession.Endpoint &&
            shared.OwnTranslatorModel == own.ModelAlias && shared.TranslatorModel == own.ModelAlias,
            "a managed shared session supplies the same resolved endpoint and model to both directions");
        Require(shared.OwnTranslatorBaseUrl == settings.OwnTranslatorBaseUrl && shared.TranslatorBaseUrl == settings.TranslatorBaseUrl,
            "managed shared selection preserves both configured external server URLs");
        settings.ShareTranslationModel = false;
        own = BuildPlan(settings, false); incoming = BuildPlan(settings, true);
        Require(own.Port == 1235 && incoming.Port == 1236, "separate ports");
        Require(own.TranslationKey != incoming.TranslationKey, "separate residency keys");
        Require(incoming.ModelAlias == "qwen/qwen3-4b", "independent incoming model");
        AppSettings changed = settings.Copy(); changed.OwnTranslationEndpoint = "http://127.0.0.1:14100/v1";
        RuntimePlan incomingSession = AtSessionPort(incoming, 50501); ApplyPlan(changed, incomingSession, true);
        Require(changed.IncomingTranslationEndpoint == incomingSession.Endpoint && changed.OwnTranslationEndpoint == "http://127.0.0.1:14100/v1" &&
            changed.TranslatorBaseUrl == settings.TranslatorBaseUrl && changed.OwnTranslatorBaseUrl == settings.OwnTranslatorBaseUrl &&
            changed.TranslatorModel == incoming.ModelAlias && changed.OwnTranslatorModel == settings.OwnTranslatorModel,
            "incoming session updates only its runtime endpoint and alias, preserving own runtime and configured external URLs");
        changed = settings.Copy(); changed.IncomingTranslationEndpoint = "http://127.0.0.1:14101/v1";
        RuntimePlan ownSession = AtSessionPort(own, 50502); ApplyPlan(changed, ownSession, false);
        Require(changed.OwnTranslationEndpoint == ownSession.Endpoint && changed.IncomingTranslationEndpoint == "http://127.0.0.1:14101/v1" &&
            changed.OwnTranslatorBaseUrl == settings.OwnTranslatorBaseUrl && changed.TranslatorBaseUrl == settings.TranslatorBaseUrl &&
            changed.OwnTranslatorModel == own.ModelAlias && changed.TranslatorModel == settings.TranslatorModel,
            "own session updates only its runtime endpoint and alias, preserving incoming runtime and configured external URLs");
        settings.UseManagedTranslator = false;
        settings.OwnTranslatorBaseUrl = "http://localhost:1234/v1";
        settings.TranslatorBaseUrl = "http://127.0.0.1:5000/v1";
        settings.OwnTranslatorModel = "existing-own"; settings.TranslatorModel = "existing-incoming";
        own = BuildPlan(settings, false); incoming = BuildPlan(settings, true);
        Require(!own.Managed && own.ModelAlias == "existing-own" && incoming.ModelAlias == "existing-incoming", "advanced model selection preserved");
        Require(own.Port == 0 && incoming.Port == 0, "advanced endpoints are never process-owned");
        settings.ShareTranslationModel = true;
        incoming = BuildPlan(settings, true);
        Require(incoming.Endpoint == settings.OwnTranslatorBaseUrl && incoming.ModelAlias == "existing-own", "explicit advanced sharing");
        ValidateLocalEndpoint("http://[::1]:1234/v1"); checks++;
        Reject(() => ValidateLocalEndpoint("https://api.example.com/v1"), "cloud");
        Reject(() => ValidateLocalEndpoint("http://localhost.evil.example/v1"), "misleading hostname");
        Reject(() => ValidateLocalEndpoint("file:///C:/model"), "file endpoint");
        Reject(() => ValidateLocalEndpoint("http://token@localhost:1234/v1"), "credential-bearing endpoint");
        Reject(() => ValidateLocalEndpoint("http://localhost:1234/v1?token=test"), "query-bearing endpoint");
        settings.UseManagedTranslator = true; settings.ManagedRuntimeId = "llama-vulkan";
        settings.OwnManagedModelId = "lfm2-350m-enjp-mt-q4_k_m";
        settings.OwnVoiceSourceLanguage = "English"; settings.OwnVoiceTargetLanguage = "Chinese";
        Reject(() => BuildPlan(settings, false), "Liquid English to Chinese");
        settings.OwnVoiceTargetLanguage = "Japanese";
        Require(BuildPlan(settings, false).Managed, "Liquid English to Japanese");
        settings.OwnVoiceAdditionalLanguagesEnabled = true;
        settings.OwnVoiceAdditionalTargetLanguages = new() { "Chinese" };
        Reject(() => BuildPlan(settings, false), "unsupported additional Liquid target");
        settings.OwnVoiceAdditionalLanguagesEnabled = false;
        Require(BuildPlan(settings, false).Managed, "disabled extra languages do not change active Liquid pair");
        settings.IncomingSourceLanguage = "Chinese"; settings.IncomingTargetLanguage = "English";
        Reject(() => BuildPlan(settings, true), "unsupported incoming source on shared Liquid model");
        settings.OwnManagedModelId = "translategemma-4b-q4_k_m";
        settings.OwnVoiceSourceLanguage = "Automatic";
        Reject(() => BuildPlan(settings, false), "TranslateGemma without source language");
        settings.OwnVoiceSourceLanguage = "English"; settings.OwnVoiceTargetLanguage = "Chinese";
        Require(BuildPlan(settings, false).Managed, "managed TranslateGemma explicit source");
        settings.UseManagedTranslator = false; settings.OwnTranslatorModel = "translategemma-4b";
        Reject(() => BuildPlan(settings, false), "unverified external TranslateGemma template");
        settings.UseManagedTranslator = true; settings.OwnManagedModelId = "voxtral-realtime-q4_k_m";
        Reject(() => BuildPlan(settings, false), "speech model as translator");
        settings.OwnManagedModelId = "qwen3-8b-q4_k_m"; settings.ManagedRuntimeId = "unknown";
        Reject(() => BuildPlan(settings, false), "unknown runtime");
        Require(OwnedProcessJob.ValidatedLayout, "Windows job native layout");
        Require(SafeDiagnosticLine("ggml_cuda_init: found 1 CUDA device") is not null, "useful GPU startup diagnostic");
        Require(SafeDiagnosticLine("llama_model_load: error opening model C:\\models\\qwen.gguf") is not null, "useful model-load failure");
        Require(SafeDiagnosticLine("load_backend: loaded Vulkan backend") is not null, "useful backend diagnostic");
        Require(SafeDiagnosticLine("GGML_ASSERT(example predicate with private words) failed") == "Native engine assertion failed; raw detail omitted.", "fatal assertion category retained without raw detail");
        Require(SafeDiagnosticLine("CUDA error: arbitrary private words") == "Native engine CUDA error; raw detail omitted.", "fatal CUDA category retained without raw detail");
        Require(SafeDiagnosticLine("Good morning, how are you?") is null, "raw English speech omitted");
        Require(SafeDiagnosticLine("你好，今天怎么样？") is null, "raw translation omitted");
        Require(SafeDiagnosticLine("llama_model_loader: prompt = actual private speech") is null, "prefixed prompt omitted");
        Require(SafeDiagnosticLine("ggml_cuda_init: content = private source") is null, "prefixed content omitted");
        Require(SafeDiagnosticLine("llama_model_loader: tokenizer.chat_template is private") is null, "chat template omitted");
        Require(SafeDiagnosticLine("{\"event\":\"transcript\",\"body\":\"private speech\"}") is null, "speech event omitted");
        string? safeJson = SafeDiagnosticLine("{\"event\":\"server_listening\",\"port\":8080,\"detail\":\"private user sentence\"}");
        Require(safeJson is not null && safeJson.Contains("port=8080") && !safeJson.Contains("private"), "JSON metadata whitelist drops arbitrary bodies");
        Require(SafeDiagnosticLine("llama_model_load: " + new string('a', 700))!.Length <= 513, "bounded diagnostic record");
        Require(SafeDiagnosticLine("llama_model_load: " + new string('a', 700) + " prompt private") is null, "content marker beyond bound still omitted");
        Require(SafeDiagnosticLine("ggml_cuda_init: safe\rraw private speech") is null, "multiline injection omitted");
        string inertRoot = Path.Combine(Path.GetTempPath(), "localtalk-inert-test-" + Guid.NewGuid().ToString("N"));
        using (var provisioner = new LocalTalkProvisioner(inertRoot))
        {
            var controller = new RuntimeController(provisioner);
            Require(!controller.OwnRunning && !controller.IncomingRunning, "constructor is inert");
            controller.StopAllAsync().GetAwaiter().GetResult();
            Require(!controller.OwnRunning && !controller.IncomingRunning, "stopping an inactive controller does not start helpers");
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Require(!Directory.Exists(inertRoot), "inert lifecycle does not create runtime/model assets");
        }
        return checks;
    }

    private static string FindOwnHost()
    {
        string[] candidates =
        {
            Path.Combine(AppPaths.ToolRoot, "FoxTransLocalHost", "FoxTrans.LocalHost.exe"),
            Path.Combine(AppPaths.ToolRoot, "FoxTransLocalHost", "publish", "FoxTrans.LocalHost.exe"),
            Path.Combine(AppPaths.ToolRoot, "FoxTransLocalHost", "bin", "Release", "net9.0-windows", "FoxTrans.LocalHost.exe"),
            Path.Combine(AppPaths.ToolRoot, "FoxTransLocalHost", "bin", "Release", "net9.0", "FoxTrans.LocalHost.exe")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("The capture-gated microphone host is missing. Reinstall SayWhat?; it will not fall back to an unsafe vendor capture client.");
    }
    private static string RequireTool(string name)
    {
        string path = Path.Combine(AppPaths.ToolRoot, name);
        return File.Exists(path) ? path : throw new FileNotFoundException("A microphone helper is missing. Reinstall the app.", path);
    }
    private static ProcessStartInfo PowerShellInfo(string script, params string[] arguments)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(exe);
        // A launch from PowerShell 7 can otherwise pass incompatible modules to
        // these Windows PowerShell helpers. Scope this to our child process.
        info.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(exe)!, "Modules");
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments)) info.ArgumentList.Add(arg);
        return info;
    }
    private static OwnedProcess StartOwned(ProcessStartInfo info, string label, Action<string, bool>? output = null)
    {
        info.UseShellExecute = false; info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.WorkingDirectory = AppPaths.Root;
        Directory.CreateDirectory(AppPaths.Root);
        return new OwnedProcess(info, label, output);
    }
    private static void RequireUdpPortFree(int port)
    {
        try { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); }
        catch (SocketException error) { throw new InvalidOperationException($"Local UDP port {port} is already occupied. Close the existing translation helper before starting SayWhat?.", error); }
    }
    private static async Task WaitUdpListenerAsync(int port, OwnedProcess process, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.IsAlive) throw new InvalidOperationException($"{process.Label} exited during startup (code {process.ExitCode}).");
            try { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); }
            catch (SocketException) { return; }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException($"{process.Label} did not bind its local control port in time.");
    }
    private static void SendBooleanControl(int port, string address, bool value)
    {
        try
        {
            using var data = new MemoryStream();
            void Write(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); data.Write(bytes); data.WriteByte(0); while (data.Length % 4 != 0) data.WriteByte(0); }
            Write(address); Write(value ? ",T" : ",F");
            using var udp = new UdpClient();
            byte[] packet = data.ToArray(); udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException) { }
    }

    private void WatchOwn(OwnedProcess process) => _ = process.Exited.ContinueWith(_ => _ = HandleFeatureExitAsync(process, own: true), TaskScheduler.Default);
    private void WatchIncoming(OwnedProcess process) => _ = process.Exited.ContinueWith(_ => _ = HandleFeatureExitAsync(process, own: false), TaskScheduler.Default);
    private async Task HandleFeatureExitAsync(OwnedProcess process, bool own)
    {
        await _changes.WaitAsync();
        try
        {
            bool current = own ? _ownRunning && (ReferenceEquals(_proxy, process) || ReferenceEquals(_muteController, process) || ReferenceEquals(_ownSpeech?.Process, process) || ReferenceEquals(_nativeOwnSpeech, process))
                : _incomingRunning && (ReferenceEquals(_incomingSpeech?.Process, process) || ReferenceEquals(_nativeIncomingSpeech, process) || ReferenceEquals(_incomingGroupSpeech?.Process, process));
            if (!current || process.StopRequested) return;
            Log($"{process.Label} exited unexpectedly ({process.ExitCode}); stopping its feature instead of leaving a frozen running indicator.");
            if (own) await StopOwnCoreAsync(); else await StopIncomingCoreAsync();
        }
        catch (Exception error) { Log("Runtime exit cleanup failed: " + error.GetType().Name); }
        finally { _changes.Release(); }
    }
    private async Task HandleTranslatorExitAsync(int port, OwnedProcess process)
    {
        await _changes.WaitAsync();
        try
        {
            if (!_translators.TryGetValue(port, out TranslatorProcess? translator) || !ReferenceEquals(translator.Process, process) || process.StopRequested) return;
            Log($"Managed translator {port} exited unexpectedly ({process.ExitCode}); affected features will stop cleanly.");
            if (_ownTranslatorPort == port) await StopOwnCoreAsync();
            if (_incomingTranslatorPort == port) await StopIncomingCoreAsync();
        }
        catch (Exception error) { Log("Translator cleanup failed: " + error.GetType().Name); }
        finally { _changes.Release(); }
    }
    private void CancelPendingStartup()
    {
        try { _startupCancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _disposeRequested, this);
    private void RequirePreviousCleanup()
    {
        if (_pendingSpeechCleanup.Count > 0 || _pendingGroupCleanup.Count > 0 || _pendingGroupPreparationCleanup.Count > 0 || _retiringOverlays.Count > 0 ||
            !_ownRunning && (_nativeOwnSpeech is not null || _muteController is not null || _proxy is not null || _ownSpeech is not null) ||
            !_incomingRunning && (_nativeIncomingSpeech is not null || _incomingSpeech is not null || _incomingGroupSpeech is not null))
            throw new InvalidOperationException("A previous translation tool hasn't finished stopping. Choose Stop all before starting again.");
    }
    private void NotifyState()
    {
        if (StateChanged is null) return;
        foreach (Action handler in StateChanged.GetInvocationList().Cast<Action>())
            try { handler(); } catch (Exception error) { Log("Runtime UI state notification failed: " + error.GetType().Name); }
    }
    private static void Log(string message) { try { DesktopLog.Write(message); } catch { /* Logging must not prevent owned-process cleanup. */ } }
    public async ValueTask DisposeAsync()
    {
        _disposeRequested = true;
        CancelPendingStartup();
        await _changes.WaitAsync();
        try
        {
            if (_disposed) return;
            // _disposeRequested already rejects queued starts. Retain state until cleanup succeeds so Quit can retry.
            await StopOwnCoreAsync();
            await StopIncomingCoreAsync();
            foreach (OwnedSpeech speech in _pendingSpeechCleanup.Values.ToArray()) await StopSpeechAsync(speech);
            foreach (OwnedGroupSpeech groups in _pendingGroupCleanup.Values.ToArray()) await StopGroupsAsync(groups);
            foreach (OwnedGroupPreparation preparation in _pendingGroupPreparationCleanup.Values.ToArray()) await CancelGroupPreparationAsync(preparation);
            foreach (TranslatorProcess translator in _translators.Values.ToArray()) await translator.Process.StopAsync(killTree: true);
            _translators.Clear();
            _sessionHealth.Clear();
            _http.Dispose();
            _disposed = true;
        }
        finally { _changes.Release(); }
        // Keep the small semaphore alive for already-queued exit callbacks; they become harmless no-ops.
    }

    internal sealed record RuntimePlan(bool Managed, int Port, string RuntimeId, string ModelId,
        string ModelAlias, string Endpoint, bool Shared)
    {
        internal int ConfiguredPort { get; init; }
        internal bool AutomaticPort { get; init; }
        public string TranslationKey => Managed ? $"{(AutomaticPort ? "auto" : "fixed")}|{(ConfiguredPort > 0 ? ConfiguredPort : Port)}|{RuntimeId}|{ModelId}|{ModelAlias}" : $"external|{Endpoint}|{ModelAlias}";
    }
    private sealed record OwnedSpeech(OwnedProcess Process, WslSpeechRegistration Registration, int Port, string Owner, bool Incoming);
    private sealed record OwnedGroupSpeech(OwnedProcess Process, WslSpeechRegistration Registration, string Owner, string ModelId, int Port);
    private sealed record OwnedGroupPreparation(OwnedProcess Process, WslSpeechRegistration Registration, string Owner);
    private sealed class TranslatorProcess(OwnedProcess process, string key)
    {
        public OwnedProcess Process { get; } = process;
        public string Key { get; } = key;
        public int Users { get; set; }
    }

    private sealed class OwnedProcess
    {
        private readonly Process _process;
        private readonly OwnedProcessJob _job;
        private readonly Task _stdout, _stderr;
        private readonly Action<string, bool>? _output;
        private long _stdoutLines, _stderrLines;
        private bool _disposed;
        public string Label { get; }
        public int ProcessId => _process.Id;
        public bool StopRequested { get; private set; }
        public Task Exited { get; }
        public Task OutputDrained => Task.WhenAll(_stdout, _stderr);
        public bool IsAlive { get { try { return !_disposed && !_process.HasExited; } catch { return false; } } }
        public int? ExitCode { get { try { return !_disposed && _process.HasExited ? _process.ExitCode : null; } catch { return null; } } }
        public OwnedProcess(ProcessStartInfo info, string label, Action<string, bool>? output = null)
        {
            Label = label;
            _output = output;
            _process = Process.Start(info) ?? throw new IOException(label + " could not start.");
            try { _job = new OwnedProcessJob(_process); }
            catch
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
                _process.Dispose();
                throw;
            }
            Log(label + $" started as an owned hidden process (PID {_process.Id}).");
            _stdout = DrainAsync(_process.StandardOutput, stderr: false);
            _stderr = DrainAsync(_process.StandardError, stderr: true);
            Exited = ObserveExitAsync();
        }
        private async Task DrainAsync(StreamReader reader, bool stderr)
        {
            try
            {
                while (await reader.ReadLineAsync() is string line)
                {
                    if (stderr) Interlocked.Increment(ref _stderrLines); else Interlocked.Increment(ref _stdoutLines);
                    try { _output?.Invoke(line, stderr); }
                    catch (Exception error) { Log(Label + " output observer failed: " + error.GetType().Name); }
                    string? diagnostic = SafeDiagnosticLine(line);
                    if (diagnostic is not null) Log(Label + ": " + diagnostic);
                    else if (stderr && _stderrLines == 1) Log(Label + " produced stderr outside the diagnostic whitelist; content was omitted.");
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            { Log(Label + " output pipe closed (" + error.GetType().Name + ")."); }
        }
        private async Task ObserveExitAsync()
        {
            await _process.WaitForExitAsync();
            _job.Dispose(); // An unexpectedly exited controller cannot leave a microphone-host descendant running.
            // Root exit must be reported immediately: a child can otherwise keep inherited output pipes open forever.
            Log($"{Label} exited with code {ExitCode}; drained {_stdoutLines} stdout/{_stderrLines} stderr lines without retaining content.");
        }
        public async Task StopAsync(bool killTree)
        {
            if (_disposed) return;
            StopRequested = true;
            try
            {
                // A private Windows job also stops descendants when their parent already exited unexpectedly.
                _job.Dispose();
                if (!_process.HasExited) _process.Kill(entireProcessTree: killTree);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await Exited.WaitAsync(timeout.Token); }
                catch (OperationCanceledException) { Log(Label + " shutdown wait timed out after its exact owned root was stopped."); }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            { Log(Label + " cleanup: " + error.GetType().Name); }
            finally
            {
                _job.Dispose();
                try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (error is TimeoutException or IOException or ObjectDisposedException)
                { Log(Label + " output drains stopped after owned-root cleanup."); }
                // Keep the retained process handle retryable if Windows hasn't confirmed exit.
                // Never report released memory merely because a kill was requested.
                if (!_process.HasExited) throw new InvalidOperationException(Label + " hasn't exited yet. Cleanup can be retried.");
                _disposed = true; _process.Dispose();
            }
        }
    }

    // A new unnamed kernel job owns only the exact process just created and children it subsequently creates.
    // Closing it cannot match/terminate VRChat, Bionic, or another engine by executable name or port.
    private sealed class OwnedProcessJob : IDisposable
    {
        private IntPtr _handle;
        internal static bool ValidatedLayout => Marshal.SizeOf<BasicJobLimits>() == (IntPtr.Size == 8 ? 64 : 48)
            && Marshal.SizeOf<ExtendedJobLimits>() == (IntPtr.Size == 8 ? 144 : 112);
        public OwnedProcessJob(Process process)
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create the owned-process safety job.");
            try
            {
                var limits = new ExtendedJobLimits { Basic = new BasicJobLimits { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
                if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedJobLimits>())
                    || !AssignProcessToJobObject(_handle, process.Handle))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not safely own the new runtime process. No translation was started.");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicJobLimits
        {
            public long PerProcessUserTime, PerJobUserTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedJobLimits
        {
            public BasicJobLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedJobLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

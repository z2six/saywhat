using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace IncomingTranslator;

internal sealed partial class RuntimeController
{
    private int? _ownSpeechPort, _incomingSpeechPort;
    private readonly ConcurrentDictionary<string, (OwnedProcess Process, string Endpoint)> _sessionHealth = new();
    private readonly Dictionary<string, string> _groupsPreferenceEndpoints = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string> HealthEndpoints()
    {
        var endpoints = _sessionHealth.Values.Where(item => item.Process.IsAlive).Select(item => item.Endpoint).ToList();
        if (_ownRunning && _ownPlan is { Managed: false } own) endpoints.Add(own.Endpoint.TrimEnd('/') + "/models");
        if (_incomingRunning && _incomingPlan is { Managed: false } incoming) endpoints.Add(incoming.Endpoint.TrimEnd('/') + "/models");
        return endpoints.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string LocalEndpoint(int port) => $"http://127.0.0.1:{port}";
    private void TrackHealth(string key, OwnedProcess process, string endpoint) => _sessionHealth[key] = (process, endpoint);
    private void ForgetHealth(string key) => _sessionHealth.TryRemove(key, out _);
    private void ForgetTranslatorHealth(int port)
    {
        ForgetHealth("windows-" + ManagedServiceKind.OwnTranslation + "-" + port);
        ForgetHealth("windows-" + ManagedServiceKind.IncomingTranslation + "-" + port);
    }

    private static bool SameOwnSpeechPortPolicy(AppSettings previous, AppSettings next) =>
        previous.AutomaticManagedPorts == next.AutomaticManagedPorts &&
        (next.AutomaticManagedPorts || previous.OwnSpeechPort == next.OwnSpeechPort);

    private static void ValidateIncomingPortSelection(AppSettings settings)
    {
        var services = new List<ManagedServiceKind>();
        if (RuntimeProfiles.UsesGroups(settings)) services.Add(ManagedServiceKind.GroupsSpeech);
        else if (!IncomingRecognitionCatalog.UsesFireRed(settings)) services.Add(ManagedServiceKind.IncomingSpeech);
        if (settings.UseManagedTranslator) services.Add(settings.ShareTranslationModel ? ManagedServiceKind.OwnTranslation : ManagedServiceKind.IncomingTranslation);
        if (services.Count > 0) ManagedPortAllocator.ValidateConfiguration(settings, services.ToArray());
    }

    private static RuntimePlan AtSessionPort(RuntimePlan plan, int port) => plan with
    {
        ConfiguredPort = plan.ConfiguredPort > 0 ? plan.ConfiguredPort : plan.Port,
        Port = port, Endpoint = LocalEndpoint(port) + "/v1"
    };

    private RuntimePlan ResolveRetainedTranslatorPlan(RuntimePlan plan)
    {
        if (!plan.Managed) return plan;
        foreach (var entry in _translators)
            if (entry.Value.Key == plan.TranslationKey && entry.Value.Process.IsAlive) return AtSessionPort(plan, entry.Key);
        return plan;
    }

    // Session addresses never replace the configured Groups preference, even
    // when a rollback saves a previously running snapshot with an ephemeral URL.
    private AppSettings PreferenceSettings(AppSettings settings)
    {
        AppSettings copy = settings.Copy();
        if (_groupsPreferenceEndpoints.TryGetValue(copy.VibeVoiceEndpoint, out string? configured)) copy.VibeVoiceEndpoint = configured;
        return copy;
    }

    private void SaveControllerSettings(AppSettings settings, bool profile = false)
    {
        AppSettings copy = PreferenceSettings(settings);
        if (profile) SettingsStore.SaveTranslationProfileSelection(copy); else SettingsStore.Save(copy);
        string groupSession = settings.VibeVoiceEndpoint;
        TranslationProfiles.CopyInto(copy, settings);
        if (_groupsPreferenceEndpoints.ContainsKey(groupSession)) settings.VibeVoiceEndpoint = groupSession;
    }

    private void ApplyRetainedSessionEndpoints(AppSettings settings)
    {
        if (_ownSpeechPort is int own) settings.OwnSpeechEndpoint = LocalEndpoint(own);
        if (_incomingSpeechPort is int incoming)
        {
            settings.IncomingSpeechEndpoint = LocalEndpoint(incoming);
            if (RuntimeProfiles.UsesGroups(settings))
            {
                string configured = PreferenceSettings(settings).VibeVoiceEndpoint;
                settings.VibeVoiceEndpoint = LocalEndpoint(incoming);
                _groupsPreferenceEndpoints[settings.VibeVoiceEndpoint] = configured;
            }
        }
    }

    private async Task<(OwnedProcess Process, int Port)> StartManagedWindowsAsync(AppSettings settings, ManagedServiceKind service,
        Func<int, ProcessStartInfo> startInfo, string label, Func<int, string> healthEndpoint, Func<JsonElement, bool> accepted,
        TimeSpan timeout, Action<int, OwnedProcess> retain, Action<int, OwnedProcess> release, CancellationToken token)
    {
        string serviceName = ManagedPortAllocator.ServiceName(service);
        return await ManagedPortAllocator.ExecuteWithRetryAsync(settings.AutomaticManagedPorts,
            ManagedPortAllocator.ConfiguredPort(settings, service), serviceName, async (reservation, cancellation) =>
            {
                ProcessStartInfo info = startInfo(reservation.Port);
                cancellation.ThrowIfCancellationRequested();
                reservation.ReleaseForStart();
                OwnedProcess process = StartOwned(info, label);
                string key = "windows-" + service + "-" + reservation.Port;
                int sawOwnedListener = 0;
                try
                {
                    retain(reservation.Port, process);
                    TrackHealth(key, process, healthEndpoint(reservation.Port));
                    await WaitHealthAsync(new Uri(healthEndpoint(reservation.Port)), process, timeout, accepted, cancellation,
                        requireOwnedListener: true, ownershipObserved: () => Interlocked.Exchange(ref sawOwnedListener, 1));
                    return (process, reservation.Port);
                }
                catch
                {
                    // Confirm only our exact process stopped before deciding
                    // whether Windows handed the released address to another app.
                    await process.StopAsync(killTree: true);
                    release(reservation.Port, process); ForgetHealth(key);
                    // A server that already owned this listener has a genuine
                    // readiness/model failure. Its own accepted connections may
                    // remain in TIME_WAIT; that cannot justify loading it again.
                    if (ShouldProbeUnownedStartupCollision(Volatile.Read(ref sawOwnedListener) != 0, cancellation.IsCancellationRequested))
                        ManagedPortAllocator.EnsureBindable(reservation.Port, serviceName);
                    throw;
                }
            }, token);
    }

    private static bool ShouldProbeUnownedStartupCollision(bool sawOwnedListener, bool canceled) => !sawOwnedListener && !canceled;

    private Task<OwnedSpeech> StartManagedVoxtralAsync(WslSpeechRegistration registration, AppSettings settings, bool incoming,
        CancellationToken token, RuntimePhaseProgress? phases)
    {
        ManagedServiceKind service = incoming ? ManagedServiceKind.IncomingSpeech : ManagedServiceKind.OwnSpeech;
        return ManagedPortAllocator.ExecuteWithRetryAsync(settings.AutomaticManagedPorts,
            ManagedPortAllocator.ConfiguredPort(settings, service), ManagedPortAllocator.ServiceName(service), async (reservation, cancellation) =>
            {
                int sawOwnedListener = 0;
                cancellation.ThrowIfCancellationRequested(); reservation.ReleaseForStart();
                try
                {
                    OwnedSpeech speech = await StartSpeechAsync(registration, reservation.Port, cancellation, phases, incoming,
                        ownershipObserved: () => Interlocked.Exchange(ref sawOwnedListener, 1));
                    if (incoming) _incomingSpeechPort = speech.Port; else _ownSpeechPort = speech.Port;
                    if (incoming) settings.IncomingSpeechEndpoint = LocalEndpoint(speech.Port); else settings.OwnSpeechEndpoint = LocalEndpoint(speech.Port);
                    return speech;
                }
                catch
                {
                    if (ShouldProbeUnownedStartupCollision(Volatile.Read(ref sawOwnedListener) != 0, cancellation.IsCancellationRequested) &&
                        _pendingSpeechCleanup.Count == 0)
                        ManagedPortAllocator.EnsureBindable(reservation.Port, ManagedPortAllocator.ServiceName(service));
                    throw;
                }
            }, token);
    }

    private Task<OwnedGroupSpeech> StartManagedGroupsAsync(WslSpeechRegistration registration, AppSettings settings,
        CancellationToken token, RuntimePhaseProgress? phases) =>
        ManagedPortAllocator.ExecuteWithRetryAsync(settings.AutomaticManagedPorts, settings.GroupsSpeechPort, "Groups speech recognition",
            async (reservation, cancellation) =>
            {
                int sawOwnedListener = 0;
                string configured = PreferenceSettings(settings).VibeVoiceEndpoint;
                string endpoint = LocalEndpoint(reservation.Port);
                _groupsPreferenceEndpoints[endpoint] = configured;
                settings.IncomingSpeechEndpoint = settings.VibeVoiceEndpoint = endpoint;
                cancellation.ThrowIfCancellationRequested(); reservation.ReleaseForStart();
                try
                {
                    OwnedGroupSpeech groups = await StartGroupsAsync(registration, settings, reservation.Port, cancellation, phases,
                        ownershipObserved: () => Interlocked.Exchange(ref sawOwnedListener, 1));
                    _incomingSpeechPort = groups.Port;
                    return groups;
                }
                catch
                {
                    if (ShouldProbeUnownedStartupCollision(Volatile.Read(ref sawOwnedListener) != 0, cancellation.IsCancellationRequested) &&
                        _pendingGroupCleanup.Count == 0)
                        ManagedPortAllocator.EnsureBindable(reservation.Port, "Groups speech recognition");
                    throw;
                }
            }, token);

    internal static int RunManagedPortWiringOfflineChecks()
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Managed runtime endpoint wiring: " + detail); checks++; }
        var settings = new AppSettings { UseManagedTranslator = true, ShareTranslationModel = true,
            AutomaticManagedPorts = true, OwnRecognitionEngine = "nemotron", OwnVoiceSourceLanguage = "English",
            OwnVoiceTargetLanguage = "Chinese", IncomingSourceLanguage = "Chinese", IncomingTargetLanguage = "English",
            OwnTranslatorBaseUrl = "http://127.0.0.1:15000/v1", TranslatorBaseUrl = "http://127.0.0.1:15001/v1" };
        RuntimePlan own = BuildPlan(settings, false), incoming = BuildPlan(settings, true);
        RuntimePlan first = AtSessionPort(own, 50500), second = AtSessionPort(own, 50501);
        Check(first.Port != second.Port && first.Endpoint != second.Endpoint && first.TranslationKey == second.TranslationKey,
            "random session addresses cannot alter the logical model identity");
        Check(first.TranslationKey == incoming.TranslationKey && first.ConfiguredPort == own.Port,
            "both shared directions retain one configured-role identity while using the actual allocated listener");
        ApplyPlan(settings, first, incoming: false);
        Check(settings.OwnTranslationEndpoint == first.Endpoint && settings.IncomingTranslationEndpoint == first.Endpoint &&
            settings.OwnTranslatorBaseUrl == "http://127.0.0.1:15000/v1" && settings.TranslatorBaseUrl == "http://127.0.0.1:15001/v1",
            "managed session endpoints never replace configured external server URLs");
        settings.OwnSpeechEndpoint = LocalEndpoint(50502); settings.IncomingSpeechEndpoint = LocalEndpoint(50503);
        AppSettings copy = settings.Copy();
        Check(copy.OwnSpeechEndpoint == settings.OwnSpeechEndpoint && copy.IncomingSpeechEndpoint == settings.IncomingSpeechEndpoint &&
            copy.OwnTranslationEndpoint == first.Endpoint && copy.IncomingTranslationEndpoint == first.Endpoint,
            "capture/service copies retain all resolved session addresses");
        string serialized = JsonSerializer.Serialize(settings);
        Check(!serialized.Contains("50500", StringComparison.Ordinal) && !serialized.Contains("50502", StringComparison.Ordinal) &&
            !serialized.Contains("50503", StringComparison.Ordinal) && serialized.Contains("15000", StringComparison.Ordinal),
            "serialized preferences retain configured endpoints and exclude runtime-selected addresses");
        AppSettings unusedFixed = settings.Copy(); unusedFixed.OwnSpeechPort = 50510; unusedFixed.OwnTranslationPort = 50511;
        Check(CanReuseOwnModels(settings, unusedFixed) && BuildPlan(unusedFixed, false).TranslationKey == own.TranslationKey,
            "unused fixed-port edits do not unload models in Automatic mode");
        AppSettings fixedMode = settings.Copy(); fixedMode.AutomaticManagedPorts = false;
        Check(!CanReuseOwnModels(settings, fixedMode) && BuildPlan(fixedMode, false).TranslationKey != own.TranslationKey,
            "switching Automatic/fixed policy changes the configured runtime identity");
        AppSettings movedSpeech = fixedMode.Copy(); movedSpeech.OwnSpeechPort++;
        Check(!CanReuseOwnModels(fixedMode, movedSpeech), "an explicit speech-port move cannot reuse its old listener");
        AppSettings separate = settings.Copy(); separate.ShareTranslationModel = false;
        Check(BuildPlan(separate, false).TranslationKey != BuildPlan(separate, true).TranslationKey,
            "separate translators keep separate role identities regardless of chosen ephemeral ports");
        AppSettings external = separate.Copy(); external.UseManagedTranslator = false;
        external.OwnTranslatorModel = external.TranslatorModel = "tencent/hy-mt2-1.8b";
        RuntimePlan externalPlan = BuildPlan(external, true);
        ApplyPlan(external, externalPlan, incoming: true);
        Check(!externalPlan.Managed && externalPlan.Port == 0 && externalPlan.Endpoint == "http://127.0.0.1:15001/v1" &&
            external.IncomingTranslationEndpoint.Length == 0 && external.OwnTranslatorBaseUrl == "http://127.0.0.1:15000/v1",
            "an explicitly configured external server stays unowned and keeps its chosen address");
        Check(ShouldProbeUnownedStartupCollision(false, false) && !ShouldProbeUnownedStartupCollision(false, true),
            "only an unowned, uncancelled failed startup can be reclassified by a fresh bind check");
        Check(!ShouldProbeUnownedStartupCollision(true, false) && !ShouldProbeUnownedStartupCollision(true, true),
            "an owned listener's readiness/model error is preserved even when its former port cannot immediately rebind");
        return checks;
    }
}

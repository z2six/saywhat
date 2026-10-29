namespace IncomingTranslator;

// A value snapshot, not a reference to the editable settings. Only choices that
// currently affect translation participate in equality; inactive fields stay
// saved in AppSettings without making the Setup page look changed.
internal sealed record SetupSelection(
    bool UseManagedTranslator,
    bool ShareTranslationModel,
    string OwnManagedModelId,
    string IncomingManagedModelId,
    string ManagedRuntimeId,
    string OwnTranslatorBaseUrl,
    string TranslatorBaseUrl,
    string OwnTranslatorModel,
    string TranslatorModel,
    string OwnVoiceTargetLanguage,
    string IncomingTargetLanguage,
    string OwnVoiceSourceLanguage,
    string IncomingSourceLanguage,
    string IncomingSpeechEngine)
{
    internal static SetupSelection Capture(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool managed = settings.UseManagedTranslator, shared = settings.ShareTranslationModel;
        return new(managed, shared,
            managed ? Value(settings.OwnManagedModelId) : "",
            managed && !shared ? Value(settings.IncomingManagedModelId) : "",
            managed ? Value(settings.ManagedRuntimeId) : "",
            !managed ? Endpoint(settings.OwnTranslatorBaseUrl) : "",
            !managed && !shared ? Endpoint(settings.TranslatorBaseUrl) : "",
            !managed ? Value(settings.OwnTranslatorModel) : "",
            !managed && !shared ? Value(settings.TranslatorModel) : "",
            Language(settings.OwnVoiceTargetLanguage), Language(settings.IncomingTargetLanguage),
            Language(settings.OwnVoiceSourceLanguage), Language(settings.IncomingSourceLanguage),
            IncomingRecognitionCatalog.CanonicalEngine(settings.IncomingSpeechEngine));
    }

    internal bool TranslatorDiffersFrom(SetupSelection other) =>
        UseManagedTranslator != other.UseManagedTranslator || ShareTranslationModel != other.ShareTranslationModel ||
        OwnManagedModelId != other.OwnManagedModelId || IncomingManagedModelId != other.IncomingManagedModelId ||
        ManagedRuntimeId != other.ManagedRuntimeId || OwnTranslatorBaseUrl != other.OwnTranslatorBaseUrl ||
        TranslatorBaseUrl != other.TranslatorBaseUrl || OwnTranslatorModel != other.OwnTranslatorModel ||
        TranslatorModel != other.TranslatorModel;

    internal bool LanguagesDifferFrom(SetupSelection other) =>
        OwnVoiceTargetLanguage != other.OwnVoiceTargetLanguage || IncomingTargetLanguage != other.IncomingTargetLanguage ||
        OwnVoiceSourceLanguage != other.OwnVoiceSourceLanguage || IncomingSourceLanguage != other.IncomingSourceLanguage;

    private static string Value(string? value) => value?.Trim() ?? "";
    private static string Endpoint(string? value) => Value(value).TrimEnd('/');
    private static string Language(string? value) => string.Equals(Value(value), "Automatic", StringComparison.OrdinalIgnoreCase)
        ? "Automatic" : AppSettings.CanonicalTranslationLanguage(value) ?? Value(value);
}

// Called only after the UI has obtained download consent. This deliberately
// prepares translation assets only: no speech/WSL/hardware checks, Windows
// prerequisite installers, settings writes, engine launches or audio capture.
internal sealed class TranslationSelectionPreparation
{
    private readonly Func<string, bool> _modelReady;
    private readonly Func<string, bool> _runtimeReady;
    private readonly Func<string, IProgress<ProvisionProgress>?, CancellationToken, Task> _ensureModel;
    private readonly Func<string, IProgress<ProvisionProgress>?, CancellationToken, Task> _ensureRuntime;

    internal TranslationSelectionPreparation(LocalTalkProvisioner provisioner)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        _modelReady = id => provisioner.ResolveModelPath(id) is not null;
        _runtimeReady = id => LocalTalkProvisioner.FindPinnedRuntimeExecutable(
            Path.Combine(provisioner.RuntimesRoot, id + "-" + LocalTalkProvisioner.LlamaVersion), id) is not null ||
            LocalTalkProvisioner.FindPinnedRuntimeExecutable(Path.Combine(AppContext.BaseDirectory, "runtimes", id), id) is not null;
        _ensureModel = async (id, progress, token) => { await provisioner.DownloadModelAsync(id, true, progress, token); };
        _ensureRuntime = async (id, progress, token) => { await provisioner.InstallRuntimeAsync(id, true, progress, token); };
    }

    internal TranslationSelectionPreparation(Func<string, bool> modelReady, Func<string, bool> runtimeReady,
        Func<string, CancellationToken, Task> ensureModel, Func<string, CancellationToken, Task> ensureRuntime)
    {
        ArgumentNullException.ThrowIfNull(modelReady); ArgumentNullException.ThrowIfNull(runtimeReady);
        ArgumentNullException.ThrowIfNull(ensureModel); ArgumentNullException.ThrowIfNull(ensureRuntime);
        _modelReady = modelReady; _runtimeReady = runtimeReady;
        _ensureModel = (id, _, token) => ensureModel(id, token);
        _ensureRuntime = (id, _, token) => ensureRuntime(id, token);
    }

    internal IReadOnlyList<string> MissingModelIds(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UseManagedTranslator) return Array.Empty<string>();
        AutomaticSetupService.ValidateSelections(settings);
        return AutomaticSetupService.TranslationIds(settings).Where(id => !_modelReady(id)).ToArray();
    }

    internal bool NeedsDownload(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.UseManagedTranslator &&
            (MissingModelIds(settings).Count != 0 || !_runtimeReady(settings.ManagedRuntimeId));
    }

    internal long MissingDownloadBytes(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UseManagedTranslator) return 0;
        long models = MissingModelIds(settings).Sum(id => LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == id).SizeBytes);
        return models + (_runtimeReady(settings.ManagedRuntimeId) ? 0 :
            LocalTalkProvisioner.RuntimeCatalog.Single(runtime => runtime.Id == settings.ManagedRuntimeId).SizeBytes);
    }

    internal async Task PrepareAsync(AppSettings settings, IProgress<ProvisionProgress>? progress = null,
        CancellationToken token = default, bool allowDownload = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        token.ThrowIfCancellationRequested();
        AutomaticSetupService.ValidateSelections(settings);
        if (!settings.UseManagedTranslator) return;
        // Freeze this operation's choices before its first asynchronous wait.
        string[] models = AutomaticSetupService.TranslationIds(settings);
        string runtime = settings.ManagedRuntimeId;
        foreach (string id in models)
        {
            token.ThrowIfCancellationRequested();
            if (_modelReady(id)) continue; // Includes registered/imported models.
            RequireDownloadConsent(allowDownload);
            progress?.Report(new("Downloading translation files", "Downloading translation files")
                { Detail = LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == id).Name });
            await _ensureModel(id, progress, token);
            token.ThrowIfCancellationRequested();
            if (!_modelReady(id)) throw new SetupUserException("The selected translation model could not be prepared. Try downloading it again.");
        }
        token.ThrowIfCancellationRequested();
        if (!_runtimeReady(runtime))
        {
            RequireDownloadConsent(allowDownload);
            progress?.Report(new("Preparing translation", "Preparing translation"));
            await _ensureRuntime(runtime, progress, token);
            token.ThrowIfCancellationRequested();
            if (!_runtimeReady(runtime)) throw new SetupUserException("The selected translation runtime could not be prepared. Try downloading it again.");
        }
    }

    private static void RequireDownloadConsent(bool allowDownload)
    {
        if (!allowDownload) throw new SetupUserException("Translation files changed. Choose Download & apply to prepare them before saving.");
    }

    // Pure/mock-only tests. They do not construct a provisioner, read model
    // files, write settings, perform network requests or launch any process.
    internal static async Task<int> RunOfflineChecksAsync(CancellationToken token = default)
    {
        int checks = 0;
        void Check(bool passed, string detail)
        { if (!passed) throw new InvalidOperationException("Setup selection check: " + detail); checks++; }
        async Task Reject<T>(Func<Task> action, string detail) where T : Exception
        {
            try { await action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException("Setup selection check: " + detail);
        }
        AppSettings Settings() => new()
        {
            FirstRunSetupCompleted = true, UseManagedTranslator = true, ShareTranslationModel = true,
            OwnManagedModelId = "hy-mt2-1.8b-q4_k_m", IncomingManagedModelId = "hy-mt2-7b-q4_k_m",
            ManagedRuntimeId = "llama-vulkan", OwnVoiceTargetLanguage = "Chinese", IncomingTargetLanguage = "English",
            OwnVoiceSourceLanguage = "Automatic", IncomingSourceLanguage = "Automatic"
        };
        AppSettings saved = Settings(); SetupSelection baseline = SetupSelection.Capture(saved);
        Check(baseline == SetupSelection.Capture(saved.Copy()), "copies capture the same saved effective choices");
        AppSettings draft = saved.Copy(); draft.IncomingManagedModelId = "qwen3-8b-q4_k_m";
        Check(baseline == SetupSelection.Capture(draft), "a hidden incoming model is ignored when shared");
        draft.OwnTranslatorBaseUrl = "http://localhost:9999/v1"; draft.TranslatorBaseUrl = "http://localhost:9998/v1";
        draft.OwnTranslatorModel = "inactive-own"; draft.TranslatorModel = "inactive-incoming";
        Check(baseline == SetupSelection.Capture(draft), "external connection fields are ignored in managed mode");
        draft.UiThemeId = "paper"; draft.UiLanguage = "ja"; draft.IncomingSettleMilliseconds = 2500;
        Check(baseline == SetupSelection.Capture(draft), "unrelated appearance and timing choices do not dirty Setup");
        draft.OwnManagedModelId = "hy-mt2-7b-q4_k_m";
        SetupSelection changed = SetupSelection.Capture(draft);
        Check(changed != baseline && changed.TranslatorDiffersFrom(baseline) && !changed.LanguagesDifferFrom(baseline),
            "a model-only change is identified separately from language changes");
        Check(changed == SetupSelection.Capture(draft) && baseline != SetupSelection.Capture(draft.Copy()),
            "saved baseline does not move when a page recaptures or copies draft settings");
        draft.OwnManagedModelId = saved.OwnManagedModelId;
        Check(baseline == SetupSelection.Capture(draft), "reverting active changes returns to the saved state");
        draft.ManagedRuntimeId = "llama-cuda";
        Check(SetupSelection.Capture(draft).TranslatorDiffersFrom(baseline), "selected managed runtime is part of the snapshot");
        draft = saved.Copy(); draft.ShareTranslationModel = false;
        SetupSelection separate = SetupSelection.Capture(draft);
        Check(separate != baseline, "shared versus separate mode is a meaningful change");
        draft.IncomingManagedModelId = "qwen3-4b-q4_k_m";
        Check(SetupSelection.Capture(draft) != separate, "separate incoming choice participates in equality");
        draft = saved.Copy(); draft.IncomingSpeechEngine = "nemotron";
        changed = SetupSelection.Capture(draft);
        Check(changed != baseline && !changed.LanguagesDifferFrom(baseline) && !changed.TranslatorDiffersFrom(baseline),
            "a speech-model-only change dirties Setup without inventing translation-model or language changes");
        foreach (Action<AppSettings> edit in new Action<AppSettings>[]
        {
            value => value.OwnVoiceTargetLanguage = "Japanese", value => value.IncomingTargetLanguage = "Korean",
            value => value.OwnVoiceSourceLanguage = "English", value => value.IncomingSourceLanguage = "Chinese"
        })
        {
            draft = saved.Copy(); edit(draft); changed = SetupSelection.Capture(draft);
            Check(changed.LanguagesDifferFrom(baseline) && !changed.TranslatorDiffersFrom(baseline),
                "each primary target and explicit spoken language is a language-only change");
        }
        draft = saved.Copy(); draft.OwnVoiceTargetLanguage = "zh-CN"; draft.IncomingTargetLanguage = "en";
        draft.OwnVoiceSourceLanguage = " automatic ";
        Check(SetupSelection.Capture(draft) == baseline, "equivalent language aliases do not manufacture pending changes");
        AppSettings external = saved.Copy(); external.UseManagedTranslator = false;
        external.OwnTranslatorBaseUrl = "http://127.0.0.1:1234/v1"; external.OwnTranslatorModel = "external-primary";
        SetupSelection externalBaseline = SetupSelection.Capture(external);
        Check(externalBaseline != baseline, "switching to an existing translator is meaningful");
        external.ManagedRuntimeId = "inactive-runtime"; external.OwnManagedModelId = "inactive-model";
        external.IncomingManagedModelId = "inactive-incoming"; external.TranslatorBaseUrl = "http://localhost:7777/v1";
        external.TranslatorModel = "inactive-secondary";
        Check(SetupSelection.Capture(external) == externalBaseline, "inactive managed and shared incoming external fields are ignored");
        external.OwnTranslatorBaseUrl += "/";
        Check(SetupSelection.Capture(external) == externalBaseline, "an endpoint trailing slash does not alter the effective connection");
        external.OwnTranslatorModel = "external-other";
        Check(SetupSelection.Capture(external).TranslatorDiffersFrom(externalBaseline), "active external model alias is compared");
        external.OwnTranslatorModel = "external-primary"; external.OwnTranslatorBaseUrl = "http://127.0.0.1:1237/v1";
        Check(SetupSelection.Capture(external).TranslatorDiffersFrom(externalBaseline), "active external endpoint is compared");
        external = saved.Copy(); external.UseManagedTranslator = false; external.ShareTranslationModel = false;
        external.OwnTranslatorBaseUrl = "http://127.0.0.1:1234/v1"; external.TranslatorBaseUrl = "http://127.0.0.1:1236/v1";
        separate = SetupSelection.Capture(external); external.TranslatorModel = "external-incoming-other";
        Check(SetupSelection.Capture(external).TranslatorDiffersFrom(separate), "separate incoming external alias is compared");
        external.TranslatorModel = saved.TranslatorModel; external.TranslatorBaseUrl = "http://127.0.0.1:1238/v1";
        Check(SetupSelection.Capture(external).TranslatorDiffersFrom(separate), "separate incoming external endpoint is compared");

        var models = new HashSet<string>(StringComparer.Ordinal);
        var runtimes = new HashSet<string>(StringComparer.Ordinal);
        var calls = new List<string>();
        var phases = new List<ProvisionProgress>();
        var preparation = new TranslationSelectionPreparation(models.Contains, runtimes.Contains,
            (id, _) => { calls.Add("model:" + id); models.Add(id); return Task.CompletedTask; },
            (id, _) => { calls.Add("runtime:" + id); runtimes.Add(id); return Task.CompletedTask; });
        AppSettings selected = saved.Copy();
        Check(preparation.MissingModelIds(selected).SequenceEqual(new[] { selected.OwnManagedModelId }), "shared preparation lists only the effective own model");
        Check(preparation.NeedsDownload(selected), "missing model/runtime needs preparation without inference");
        long ownBytes = LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == selected.OwnManagedModelId).SizeBytes;
        long runtimeBytes = LocalTalkProvisioner.RuntimeCatalog.Single(runtime => runtime.Id == selected.ManagedRuntimeId).SizeBytes;
        Check(preparation.MissingDownloadBytes(selected) == ownBytes + runtimeBytes,
            "consent counts only the missing effective shared model and runtime");
        await preparation.PrepareAsync(selected, new InlineProgress(phases.Add), token);
        Check(calls.SequenceEqual(new[] { "model:" + selected.OwnManagedModelId, "runtime:" + selected.ManagedRuntimeId }),
            "only missing selected translation assets are prepared in order");
        Check(phases.Select(value => value.Stage).SequenceEqual(new[] { "Downloading translation files", "Preparing translation" }),
            "missing assets expose useful translation-specific phases");
        Check(!preparation.NeedsDownload(selected) && preparation.MissingModelIds(selected).Count == 0, "prepared selections report no remaining download");
        Check(preparation.MissingDownloadBytes(selected) == 0, "cached selections have no estimated download bytes");
        Check(SetupSelection.Capture(selected) == baseline && selected.FirstRunSetupCompleted,
            "preparation never edits selected choices or first-run completion");
        calls.Clear(); phases.Clear();
        await preparation.PrepareAsync(selected, new InlineProgress(phases.Add), token);
        Check(calls.Count == 0 && phases.Count == 0, "cached/imported selected model and pinned runtime are reused without repair work");
        selected.ShareTranslationModel = false;
        Check(preparation.MissingModelIds(selected).SequenceEqual(new[] { selected.IncomingManagedModelId }), "separate incoming missing selection is reported");
        Check(preparation.MissingDownloadBytes(selected) == LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == selected.IncomingManagedModelId).SizeBytes,
            "separate consent counts only the missing incoming model");
        await preparation.PrepareAsync(selected, token: token);
        Check(calls.SequenceEqual(new[] { "model:" + selected.IncomingManagedModelId }), "a cached own/imported selection is preserved while only incoming is downloaded");
        calls.Clear(); models.Clear(); selected.IncomingManagedModelId = selected.OwnManagedModelId;
        Check(preparation.MissingModelIds(selected).Count == 1, "identical separate model files are deduplicated");
        Check(preparation.MissingDownloadBytes(selected) == ownBytes, "identical separate selections do not duplicate consent byte estimates");
        await preparation.PrepareAsync(selected, token: token);
        Check(calls.SequenceEqual(new[] { "model:" + selected.OwnManagedModelId }), "identical separate selections trigger one model download");
        calls.Clear(); runtimes.Clear();
        Check(preparation.MissingModelIds(selected).Count == 0 && preparation.NeedsDownload(selected), "missing runtime alone still reports preparation needed");
        Check(preparation.MissingDownloadBytes(selected) == runtimeBytes, "runtime-only consent excludes cached model bytes");
        await preparation.PrepareAsync(selected, token: token);
        Check(calls.SequenceEqual(new[] { "runtime:" + selected.ManagedRuntimeId }), "runtime-only preparation never redownloads ready models");
        calls.Clear(); external.FirstRunSetupCompleted = true;
        Check(preparation.MissingModelIds(external).Count == 0 && !preparation.NeedsDownload(external), "external mode has no managed asset requirements");
        Check(preparation.MissingDownloadBytes(external) == 0, "external translation is not assigned managed download bytes");
        await preparation.PrepareAsync(external, token: token);
        Check(calls.Count == 0, "valid external mode does not prepare or take over any model/runtime");
        external.OwnTranslatorBaseUrl = "https://example.invalid/v1";
        await Reject<ArgumentException>(() => preparation.PrepareAsync(external, token: token), "non-local external selection is rejected before work");
        Check(calls.Count == 0, "rejected external endpoint performs no asset work");
        AppSettings invalid = saved.Copy(); invalid.OwnManagedModelId = "voxtral-realtime-q4_k_m";
        await Reject<SetupUserException>(() => preparation.PrepareAsync(invalid, token: token), "speech model cannot be downloaded as translator");
        invalid = saved.Copy(); invalid.ManagedRuntimeId = "unknown-runtime";
        await Reject<SetupUserException>(() => preparation.PrepareAsync(invalid, token: token), "unknown runtime selection fails before work");
        Check(calls.Count == 0, "invalid translation selections do not prepare any assets");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => preparation.PrepareAsync(saved.Copy(), token: cancelled.Token), "pre-cancelled preparation performs no work");
            Check(calls.Count == 0, "pre-cancelled asset preparation never invokes delegates");
        }
        var failedCalls = new List<string>();
        var failing = new TranslationSelectionPreparation(_ => false, _ => false,
            (id, _) => { failedCalls.Add("model:" + id); throw new IOException("Synthetic download failure"); },
            (id, _) => { failedCalls.Add("runtime:" + id); return Task.CompletedTask; });
        selected = saved.Copy(); selected.ShareTranslationModel = false;
        SetupSelection failureBaseline = SetupSelection.Capture(selected);
        await Reject<IOException>(() => failing.PrepareAsync(selected, token: token), "download failure remains visible");
        Check(failedCalls.SequenceEqual(new[] { "model:" + selected.OwnManagedModelId }), "failure stops scheduling incoming/runtime work");
        Check(SetupSelection.Capture(selected) == failureBaseline, "failed preparation does not mutate saved/draft model or language choices");
        var notReady = new TranslationSelectionPreparation(_ => false, _ => true,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        await Reject<SetupUserException>(() => notReady.PrepareAsync(saved.Copy(), token: token), "a no-op download cannot falsely claim that the selected file is ready");
        var noRuntime = new TranslationSelectionPreparation(_ => true, _ => false,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        await Reject<SetupUserException>(() => noRuntime.PrepareAsync(saved.Copy(), token: token), "a no-op runtime install cannot falsely claim readiness");
        using (var cancelled = new CancellationTokenSource())
        {
            var cancelledCalls = new List<string>();
            var cancelling = new TranslationSelectionPreparation(_ => false, _ => false,
                (id, _) => { cancelledCalls.Add("model:" + id); cancelled.Cancel(); return Task.CompletedTask; },
                (id, _) => { cancelledCalls.Add("runtime:" + id); return Task.CompletedTask; });
            await Reject<OperationCanceledException>(() => cancelling.PrepareAsync(selected, token: cancelled.Token), "cancellation after a model prevents later work");
            Check(cancelledCalls.Count == 1, "cancelled operation does not continue into another model or runtime");
        }
        var frozenCalls = new List<string>(); var frozenModels = new HashSet<string>(); var frozenRuntimes = new HashSet<string>();
        selected = saved.Copy(); selected.ShareTranslationModel = false; string originalIncoming = selected.IncomingManagedModelId;
        string originalRuntime = selected.ManagedRuntimeId;
        var freezing = new TranslationSelectionPreparation(frozenModels.Contains, frozenRuntimes.Contains,
            (id, _) => { frozenCalls.Add("model:" + id); frozenModels.Add(id); selected.IncomingManagedModelId = "qwen3-4b-q4_k_m"; selected.ManagedRuntimeId = "llama-cuda"; return Task.CompletedTask; },
            (id, _) => { frozenCalls.Add("runtime:" + id); frozenRuntimes.Add(id); return Task.CompletedTask; });
        await freezing.PrepareAsync(selected, token: token);
        Check(frozenCalls.SequenceEqual(new[] { "model:" + saved.OwnManagedModelId, "model:" + originalIncoming, "runtime:" + originalRuntime }),
            "asynchronous preparation keeps its original model/runtime plan despite later UI edits");

        var raceModels = new HashSet<string> { saved.OwnManagedModelId };
        var raceRuntimes = new HashSet<string> { saved.ManagedRuntimeId };
        var raceCalls = new List<string>();
        var racePhases = new List<ProvisionProgress>();
        var consentRace = new TranslationSelectionPreparation(raceModels.Contains, raceRuntimes.Contains,
            (id, _) => { raceCalls.Add("model:" + id); raceModels.Add(id); return Task.CompletedTask; },
            (id, _) => { raceCalls.Add("runtime:" + id); raceRuntimes.Add(id); return Task.CompletedTask; });
        Check(!consentRace.NeedsDownload(saved), "cached/imported models and runtime require no initial download consent");
        raceModels.Clear();
        await Reject<SetupUserException>(() => consentRace.PrepareAsync(saved, new InlineProgress(racePhases.Add), token, allowDownload: false),
            "a cached model disappearing after inspection cannot silently start an unapproved download");
        Check(raceCalls.Count == 0 && racePhases.Count == 0, "refused model consent race performs no download and reports no downloading phase");
        raceModels.Add(saved.OwnManagedModelId);
        Check(!consentRace.NeedsDownload(saved), "restored imported model returns to a no-download apply");
        raceRuntimes.Clear();
        await Reject<SetupUserException>(() => consentRace.PrepareAsync(saved, new InlineProgress(racePhases.Add), token, allowDownload: false),
            "a runtime disappearing after inspection cannot silently start an unapproved download");
        Check(raceCalls.Count == 0 && racePhases.Count == 0, "refused runtime consent race performs no asset work or preparation phase");
        raceRuntimes.Add(saved.ManagedRuntimeId);
        await consentRace.PrepareAsync(saved, token: token, allowDownload: false);
        Check(raceCalls.Count == 0, "a no-download apply reuses intact cached/imported files without network work");
        raceModels.Clear(); raceRuntimes.Clear();
        await consentRace.PrepareAsync(saved, token: token, allowDownload: true);
        Check(raceCalls.SequenceEqual(new[] { "model:" + saved.OwnManagedModelId, "runtime:" + saved.ManagedRuntimeId }),
            "explicit download consent still permits preparing missing selected translation assets");
        return checks;
    }

    private sealed class InlineProgress(Action<ProvisionProgress> report) : IProgress<ProvisionProgress>
    { public void Report(ProvisionProgress value) => report(value); }
}

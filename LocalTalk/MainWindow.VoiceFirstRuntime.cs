namespace IncomingTranslator;

internal sealed partial class MainWindow
{
    private CancellationTokenSource? _voiceFirstDebounce;
    private long _voiceFirstChangeVersion;

    // Home's everyday controls are scoped saves, not an implicit Save all for
    // unrelated drafts on Advanced pages. Rapid clicks coalesce into one update.
    private async Task ApplyVoiceFirstChangeAsync(bool requireOwnRestart = false)
    {
        if (_preview || _closing || _busy) return;
        long version = ++_voiceFirstChangeVersion;
        _voiceFirstDebounce?.Cancel();
        using var debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _voiceFirstDebounce = debounce;
        if (_page == "Home") Navigate("Home");
        try
        {
            await Task.Delay(250, debounce.Token);
            if (version != _voiceFirstChangeVersion || _closing || _busy) return;
            AppSettings previous = ReadVoiceFirstSavedSettings();
            // A model draft selected on another page is already visible on
            // Home. Prepare it explicitly rather than showing that draft while
            // quietly applying the language to a different, saved recognizer.
            if (VoiceFirstOwnModelDraftDiffers(previous, _settings))
            { await ApplyVoiceFirstModelChangeAsync(); return; }
            AppSettings candidate = VoiceFirstCandidate(previous, _settings, includeModels: false);
            ValidateVoiceFirstOwnSelection(candidate);
            if (TranslationProfiles.ConfigurationEquals(candidate, previous)) { CompleteVoiceFirstSave(previous); return; }
            bool applied = false;
            await RunTask(async token =>
            {
                await _runtime.ApplyOwnSettingsAsync(candidate, token, ProvisionProgress());
                CompleteVoiceFirstSave(candidate);
                applied = true;
            }, "Applying voice settings", "Settings applied.");
            if (!applied) RestoreVoiceFirstChoices(previous, includeModels: false);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested) { }
        catch (Exception error)
        {
            try { RestoreVoiceFirstChoices(ReadVoiceFirstSavedSettings(), includeModels: false); }
            catch { /* Keep the visible draft if the saved file itself is unavailable. */ }
            Error("Apply voice settings", error);
        }
        finally
        {
            if (ReferenceEquals(_voiceFirstDebounce, debounce)) _voiceFirstDebounce = null;
        }
    }

    private async Task ApplyVoiceFirstModelChangeAsync()
    {
        if (_preview || _busy || _closing) return;
        ++_voiceFirstChangeVersion; _voiceFirstDebounce?.Cancel();
        AppSettings previous;
        try { previous = ReadVoiceFirstSavedSettings(); }
        catch (Exception error) { Error("Read voice settings", error); return; }
        AppSettings candidate = VoiceFirstCandidate(previous, _settings, includeModels: true);
        bool applied = false;
        try
        {
            ValidateVoiceFirstOwnSelection(candidate);
            bool sharedChange = VoiceFirstSharedTranslatorChanged(previous, candidate);
            if (_runtime.IncomingRunning && sharedChange)
            {
                // A shared choice may be incompatible with an active subtitle
                // language. Validate only that running direction, never an unused
                // one, and never silently change its language to make it fit.
                _ = RuntimeController.BuildPlan(candidate, incoming: true);
                if (!Confirm(VoiceFirstText.T("Subtitles use this translator too. Changing it will briefly pause both features. Continue?")))
                    return;
            }
            RuntimeController.RuntimePlan plan = RuntimeController.BuildPlan(candidate, incoming: false);
            ProvisionModelChoice? missingTranslator = plan.Managed && _provisioner.ResolveModelPath(plan.ModelId) is null
                ? LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == plan.ModelId) : null;
            if (missingTranslator?.RequiresTermsAcceptance == true && CreateModelTermsDialog(missingTranslator).ShowDialog() != true) return;

            bool legacySpeechSetup = candidate.OwnRecognitionEngine == "voxtral" && _provisioner.SpeechRegistration is null;
            long bytes = MissingVoiceFirstDownloadBytes(candidate);
            if (!legacySpeechSetup && bytes > 0 && !Confirm(VoiceFirstText.Format(
                "Download the selected translation tools ({0:0.0} GB)? Your current translation keeps running until the files are ready.", bytes / 1e9))) return;

            await RunTask(async token =>
            {
                if (legacySpeechSetup)
                {
                    var setup = new AutomaticSetupService(_provisioner, ownVoiceOnly: true);
                    TaskStage("Checking setup requirements");
                    AutomaticSetupPlan preparation = await setup.PlanAsync(candidate, token);
                    if (CreateSetupConsentDialog(preparation).ShowDialog() != true) throw new OperationCanceledException();
                    AutomaticSetupResult result = await setup.RunAsync(candidate, true, ProvisionProgress(), token);
                    if (!result.Completed) throw new SetupUserException(result.Message);
                }
                else await PrepareVoiceFirstOwnAssetsAsync(candidate, token);
                token.ThrowIfCancellationRequested();
                if (_runtime.IncomingRunning && sharedChange)
                    await ApplyVoiceFirstSharedModelAsync(previous, candidate, token);
                else await _runtime.ApplyOwnSettingsAsync(candidate, token, ProvisionProgress());
                CompleteVoiceFirstSave(candidate); applied = true;
            }, "Preparing selected models", "Settings applied.");
        }
        catch (Exception error) { Error("Change voice model", error); }
        finally
        {
            if (!applied) RestoreVoiceFirstChoices(previous, includeModels: true);
        }
    }

    private async Task PrepareVoiceFirstOwnAssetsAsync(AppSettings candidate, CancellationToken token)
    {
        RuntimeController.RuntimePlan plan = RuntimeController.BuildPlan(candidate, incoming: false);
        if (plan.Managed)
        {
            if (_provisioner.ResolveModelPath(plan.ModelId) is null)
            {
                TaskStage("Downloading translation files");
                await _provisioner.DownloadModelAsync(plan.ModelId, true, ProvisionProgress(), token);
            }
            if (!VoiceFirstRuntimeReady(plan.RuntimeId))
            {
                TaskStage("Preparing translation");
                await _provisioner.InstallRuntimeAsync(plan.RuntimeId, true, ProvisionProgress(), token);
            }
        }
        if (candidate.OwnRecognitionEngine != "voxtral")
        {
            var speech = new NativeSpeechProvisioner(_provisioner.DataRoot);
            if (!speech.IsReady(candidate))
            {
                TaskStage("Preparing speech recognition");
                await speech.PrepareAsync(candidate, ProvisionProgress(), token);
            }
        }
    }

    private long MissingVoiceFirstDownloadBytes(AppSettings candidate)
    {
        RuntimeController.RuntimePlan plan = RuntimeController.BuildPlan(candidate, incoming: false);
        long bytes = 0;
        if (plan.Managed)
        {
            if (_provisioner.ResolveModelPath(plan.ModelId) is null)
                bytes += LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == plan.ModelId).SizeBytes;
            if (!VoiceFirstRuntimeReady(plan.RuntimeId))
                bytes += LocalTalkProvisioner.RuntimeCatalog.Single(runtime => runtime.Id == plan.RuntimeId).SizeBytes;
        }
        if (candidate.OwnRecognitionEngine != "voxtral")
            bytes += new NativeSpeechProvisioner(_provisioner.DataRoot).PendingDownloadBytes(candidate);
        return bytes;
    }

    private bool VoiceFirstRuntimeReady(string id) => LocalTalkProvisioner.FindPinnedRuntimeExecutable(
        Path.Combine(_provisioner.RuntimesRoot, id + "-" + LocalTalkProvisioner.LlamaVersion), id) is not null ||
        LocalTalkProvisioner.FindPinnedRuntimeExecutable(Path.Combine(AppContext.BaseDirectory, "runtimes", id), id) is not null;

    private async Task ApplyVoiceFirstSharedModelAsync(AppSettings previous, AppSettings candidate, CancellationToken token)
    {
        bool own = _runtime.OwnRunning, incoming = _runtime.IncomingRunning;
        bool stopped = false, quiesced = false;
        try
        {
            TaskStage("Applying shared translation model", "Translation briefly pauses while the prepared model changes.");
            quiesced = await _runtime.FinishOwnMessageForSettingsAsync(token, ProvisionProgress());
            token.ThrowIfCancellationRequested();
            await _runtime.StopAllAsync(ProvisionProgress()); stopped = true;
            token.ThrowIfCancellationRequested();
            SettingsStore.Save(candidate);
            if (own) await _runtime.StartOwnAsync(candidate.Copy(), token, ProvisionProgress());
            if (incoming) await _runtime.StartIncomingAsync(candidate.Copy(), OpenOptions, token, ProvisionProgress(), OpenSpeakerSettings, ApplyTranslationProfileAsync);
        }
        catch (Exception applyError)
        {
            if (!stopped)
            {
                if (quiesced) await _runtime.ResumeOwnAfterCancelledSettingsAsync();
                throw;
            }
            if (applyError is OperationCanceledException || token.IsCancellationRequested || _closing || _lifetime.IsCancellationRequested)
            {
                await _runtime.StopAllAsync(ProvisionProgress()); SettingsStore.Save(previous); throw;
            }
            try
            {
                TaskStage("Restoring your previous model", "The new model could not start. Restoring the translation that was running.");
                await _runtime.StopAllAsync(ProvisionProgress()); SettingsStore.Save(previous);
                using var recovery = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                recovery.CancelAfter(TimeSpan.FromSeconds(150));
                if (own) await _runtime.StartOwnAsync(previous.Copy(), recovery.Token, ProvisionProgress());
                if (incoming) await _runtime.StartIncomingAsync(previous.Copy(), OpenOptions, recovery.Token, ProvisionProgress(), OpenSpeakerSettings, ApplyTranslationProfileAsync);
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("The new model could not start and the previous translation could not be restored. Choose Stop all and open Diagnostics.", applyError, recoveryError);
            }
            throw new InvalidOperationException("The new model could not start. Your previous settings were restored.", applyError);
        }
    }

    internal static bool VoiceFirstSharedTranslatorChanged(AppSettings previous, AppSettings next)
    {
        RuntimeController.RuntimePlan plan = RuntimeController.BuildPlan(next, incoming: false), oldPlan;
        try { oldPlan = RuntimeController.BuildPlan(previous, incoming: false); }
        catch (ArgumentException) { return true; } // A valid replacement can repair an invalid old selection.
        return oldPlan.Managed != plan.Managed || oldPlan.Shared != plan.Shared ||
            oldPlan.RuntimeId != plan.RuntimeId || (oldPlan.Shared || plan.Shared) && oldPlan.TranslationKey != plan.TranslationKey;
    }

    internal static bool VoiceFirstOwnModelDraftDiffers(AppSettings previous, AppSettings next) =>
        previous.OwnRecognitionEngine != next.OwnRecognitionEngine || previous.UseManagedTranslator != next.UseManagedTranslator ||
        (next.UseManagedTranslator ? previous.OwnManagedModelId != next.OwnManagedModelId || previous.ManagedRuntimeId != next.ManagedRuntimeId
            : previous.OwnTranslatorBaseUrl.TrimEnd('/') != next.OwnTranslatorBaseUrl.TrimEnd('/') || previous.OwnTranslatorModel != next.OwnTranslatorModel);

    private static void ValidateVoiceFirstOwnSelection(AppSettings candidate)
    {
        _ = RuntimeController.BuildPlan(candidate, incoming: false);
        if (candidate.OwnRecognitionEngine == "voxtral" &&
            (candidate.OwnVoiceSourceLanguage == "Automatic" || OwnRecognitionCatalog.SupportedSourceLanguages.Contains(AppSettings.CanonicalTranslationLanguage(candidate.OwnVoiceSourceLanguage) ?? ""))) return;
        if (!OwnRecognitionCatalog.Find(candidate.OwnRecognitionEngine).SupportsSource(candidate.OwnVoiceSourceLanguage))
            throw new ModelSelectionUserException("Voxtral detects the spoken language automatically. Choose Automatic or switch to Nemotron.");
    }

    internal static AppSettings VoiceFirstCandidate(AppSettings saved, AppSettings draft, bool includeModels)
    {
        AppSettings result = saved.Copy();
        result.OwnVoiceSourceLanguage = draft.OwnVoiceSourceLanguage;
        result.OwnVoiceTargetLanguage = draft.OwnVoiceTargetLanguage;
        result.OwnVoiceAdditionalLanguagesEnabled = draft.OwnVoiceAdditionalLanguagesEnabled;
        result.OwnVoiceAdditionalTargetLanguages = draft.NormalizedOwnVoiceAdditionalLanguages();
        result.OwnMicrophoneDeviceId = draft.OwnMicrophoneDeviceId;
        result.OwnVoiceEndPauseMilliseconds = draft.OwnVoiceEndPauseMilliseconds;
        result.OwnVoiceSendEarlyPreviews = draft.OwnVoiceSendEarlyPreviews;
        result.OwnVoicePauseWhenVrchatMuted = draft.OwnVoicePauseWhenVrchatMuted;
        if (includeModels)
        {
            result.OwnRecognitionEngine = draft.OwnRecognitionEngine;
            result.UseManagedTranslator = draft.UseManagedTranslator;
            result.OwnManagedModelId = draft.OwnManagedModelId;
            result.ManagedRuntimeId = draft.ManagedRuntimeId;
            result.OwnTranslatorBaseUrl = draft.OwnTranslatorBaseUrl;
            result.OwnTranslatorModel = draft.OwnTranslatorModel;
        }
        return result;
    }

    private AppSettings ReadVoiceFirstSavedSettings() => File.Exists(AppPaths.SettingsPath)
        ? SettingsStore.ReadSettings(File.ReadAllText(AppPaths.SettingsPath)) : _saveStateAppliedSettings?.Copy() ?? new AppSettings();

    private void CompleteVoiceFirstSave(AppSettings applied)
    {
        _savedSetupSelection = SetupSelection.Capture(applied);
        RefreshSaveState(reloadSaved: true);
    }

    private bool VoiceFirstIncomingNeedsPreparation()
    {
        // Home is a local metadata view: no WSL process, health request, audio
        // enumeration or inference is part of deciding this button's label.
        if (_preview || _runtime.IncomingRunning) return false;
        try
        {
            AppSettings candidate = ReadVoiceFirstSavedSettings();
            return VoiceFirstIncomingNeedsPreparationFor(candidate);
        }
        catch (ArgumentException) { return false; } // Start reports a precise language/model selection error.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return false; } // An unreadable settings file is not silently treated as a new installation.
    }

    private async Task ToggleVoiceFirstSubtitlesAsync()
    {
        if (_preview || _busy || _closing) return;
        if (_runtime.IncomingRunning)
        {
            await RunTask(async _ => { TaskStage("Stopping subtitles"); await _runtime.StopIncomingAsync(); }, "Subtitles");
            return;
        }
        await RunTask(async token =>
        {
            // Start from the latest applied choices, not an unsaved own-voice
            // model draft on another page. Preparing this optional feature must
            // leave the active microphone configuration and model untouched.
            AppSettings candidate = ReadVoiceFirstSavedSettings();
            _ = RuntimeController.BuildPlan(candidate, incoming: true);
            IncomingListening.Validate(candidate);
            RuntimeProfiles.ValidateIncomingSpeech(candidate);
            var setup = new AutomaticSetupService(_provisioner, incomingVoiceOnly: true);
            if (VoiceFirstIncomingNeedsPreparationFor(candidate))
            {
                TaskStage("Checking subtitle setup");
                AutomaticSetupPlan plan = await setup.PlanAsync(candidate, token);
                string id = candidate.ShareTranslationModel ? candidate.OwnManagedModelId : candidate.IncomingManagedModelId;
                ProvisionModelChoice? model = candidate.UseManagedTranslator && _provisioner.ResolveModelPath(id) is null
                    ? LocalTalkProvisioner.ModelCatalog.FirstOrDefault(item => item.Id == id) : null;
                if (model?.RequiresTermsAcceptance == true && CreateModelTermsDialog(model).ShowDialog() != true) throw new OperationCanceledException();
                if (CreateSetupConsentDialog(plan).ShowDialog() != true) throw new OperationCanceledException();
                AutomaticSetupResult result = await setup.RunAsync(candidate, true, ProvisionProgress(), token);
                if (!result.Completed) throw new SetupUserException(result.Message);
                token.ThrowIfCancellationRequested();
                SettingsStore.Save(candidate);
                RefreshSaveState(reloadSaved: true);
            }
            await EnsureGroupSpeechReadyForProfileAsync(candidate, token);
            token.ThrowIfCancellationRequested();
            await _runtime.StartIncomingAsync(candidate.Copy(), OpenOptions, token, ProvisionProgress(), OpenSpeakerSettings, ApplyTranslationProfileAsync);
            RefreshSaveState(reloadSaved: true);
        }, "Preparing live subtitles", "Subtitles started.");
    }

    private bool VoiceFirstIncomingNeedsPreparationFor(AppSettings candidate)
    {
        RuntimeController.RuntimePlan plan = RuntimeController.BuildPlan(candidate, incoming: true);
        return !IncomingRecognitionCatalog.UsesNative(candidate) && _provisioner.SpeechRegistration is null ||
            SubtitleSpeechFilesMissing(candidate) ||
            plan.Managed && (_provisioner.ResolveModelPath(plan.ModelId) is null || !VoiceFirstRuntimeReady(plan.RuntimeId)) ||
            RuntimeProfiles.UsesSpeakerRecognition(candidate) && !File.Exists(candidate.SpeakerModelPath);
    }

    private void RestoreVoiceFirstChoices(AppSettings previous, bool includeModels)
    {
        AppSettings restored = VoiceFirstCandidate(_settings, previous, includeModels);
        TranslationProfiles.CopyInto(restored, _settings);
        RefreshSaveState(reloadSaved: true);
        if (!_busy && !_closing && _page == "Home") Navigate("Home");
    }
}

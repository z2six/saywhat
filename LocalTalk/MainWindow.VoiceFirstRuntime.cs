namespace IncomingTranslator;

internal sealed partial class MainWindow
{
    // Own Home choices remain a draft until its Apply button or Start is used.
    // This scoped path never commits unrelated incoming or Advanced drafts.
    private async Task ApplyVoiceFirstModelChangeAsync(bool startOwn = false)
    {
        if (_preview || _busy || _closing) return;
        AppSettings previous;
        try { previous = ReadVoiceFirstSavedSettings(); }
        catch (Exception error) { Error("Read voice settings", error); return; }
        AppSettings draftBefore = _settings.Copy();
        AppSettings candidate = VoiceFirstCandidate(previous, draftBefore, includeModels: true);
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
                // Downloads can take time. Keep independently saved appearance,
                // incoming choices and other untouched preferences current.
                AppSettings latest = ReadVoiceFirstSavedSettings();
                candidate = SettingsSaveState.Rebase(candidate, previous, latest);
                previous = latest;
                ValidateVoiceFirstOwnSelection(candidate);
                sharedChange = VoiceFirstSharedTranslatorChanged(previous, candidate);
                if (_runtime.IncomingRunning && sharedChange) _ = RuntimeController.BuildPlan(candidate, incoming: true);
                if (_runtime.IncomingRunning && sharedChange)
                    await ApplyVoiceFirstSharedModelAsync(previous, candidate, token);
                else await _runtime.ApplyOwnSettingsAsync(candidate, token, ProvisionProgress());
                CompleteVoiceFirstSave(candidate); applied = true;
                if (startOwn)
                {
                    TaskStage("Starting your voice translator");
                    await _runtime.StartOwnAsync(candidate.Copy(), token, ProvisionProgress());
                }
            }, "Preparing selected models", "Settings applied.");
        }
        catch (Exception error) { Error("Change voice model", error); }
        finally
        {
            if (!applied)
            {
                try
                {
                    AppSettings latest = ReadVoiceFirstSavedSettings();
                    TranslationProfiles.CopyInto(VoiceFirstDraftAfterApplyAttempt(draftBefore, previous, latest), _settings);
                }
                catch { /* Keep the user's draft if saved state cannot be read reliably. */ }
                RefreshSaveState(reloadSaved: true);
                if (!_busy && !_closing && _page == "Home") Navigate("Home");
            }
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

    private AppSettings ReadVoiceFirstSavedSettings() => _preview
        ? _saveStateAppliedSettings?.Copy() ?? _settings.Copy()
        : File.Exists(AppPaths.SettingsPath) ? SettingsStore.ReadSettings(File.ReadAllText(AppPaths.SettingsPath))
            : _saveStateAppliedSettings?.Copy() ?? new AppSettings();

    internal static AppSettings VoiceFirstDraftAfterApplyAttempt(AppSettings draft, AppSettings previous, AppSettings latest)
    {
        AppSettings preserved = SettingsSaveState.Rebase(draft, previous, latest);
        // Consent/preparation failures leave the applied own choices unchanged:
        // keep the whole own draft so the user can retry or adjust it. If a later
        // failure did commit settings, show that known own configuration instead.
        bool committedOwnChange = !TranslationProfiles.ConfigurationEquals(VoiceFirstCandidate(previous, previous, includeModels: true),
            VoiceFirstCandidate(previous, latest, includeModels: true));
        return committedOwnChange ? VoiceFirstCandidate(preserved, latest, includeModels: true) : preserved;
    }

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
            if (!IncomingCaptureCapabilities.SupportsSource(candidate, SubtitleWindowsVersion)) return false;
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
            IncomingCaptureCapabilities.Validate(candidate, SubtitleWindowsVersion);
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

}

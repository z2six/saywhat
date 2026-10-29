using System.Text.Json;

namespace IncomingTranslator;

// Pure change planning keeps UI-only saves and incoming speech switches from
// unloading the microphone or an unchanged, shared translation model.
internal sealed record SettingsRuntimeChanges(bool OwnChanged, bool IncomingChanged, bool SharedTranslatorChanged)
{
    internal static SettingsRuntimeChanges Between(AppSettings previous, AppSettings next)
    {
        RuntimeController.RuntimePlan newOwn = RuntimeController.BuildPlan(next, incoming: false);
        RuntimeController.RuntimePlan newIncoming = RuntimeController.BuildPlan(next, incoming: true);
        RuntimeController.RuntimePlan oldOwn, oldIncoming;
        try
        {
            oldOwn = RuntimeController.BuildPlan(previous, incoming: false);
            oldIncoming = RuntimeController.BuildPlan(previous, incoming: true);
        }
        catch (ArgumentException)
        {
            // An invalid old selection must not prevent applying a valid replacement.
            return new(true, true, true);
        }
        static bool TranslatorChanged(RuntimeController.RuntimePlan left, RuntimeController.RuntimePlan right) =>
            left.TranslationKey != right.TranslationKey || left.Shared != right.Shared || left.Managed != right.Managed;
        bool own = TranslatorChanged(oldOwn, newOwn)
            || previous.OwnRecognitionEngine != next.OwnRecognitionEngine || previous.OwnMicrophoneDeviceId != next.OwnMicrophoneDeviceId
            || previous.OwnVoiceTargetLanguage != next.OwnVoiceTargetLanguage || previous.OwnVoiceSourceLanguage != next.OwnVoiceSourceLanguage
            || !previous.EffectiveOwnVoiceTargetLanguages().SequenceEqual(next.EffectiveOwnVoiceTargetLanguages())
            || previous.OwnVoiceEndPauseMilliseconds != next.OwnVoiceEndPauseMilliseconds || previous.OwnVoiceSendEarlyPreviews != next.OwnVoiceSendEarlyPreviews
            || previous.FinishCapturedSentenceOnMute != next.FinishCapturedSentenceOnMute || previous.RecognitionDelayMilliseconds != next.RecognitionDelayMilliseconds
            || previous.TranslationQuality != next.TranslationQuality || previous.Glossary != next.Glossary;
        bool incoming = TranslatorChanged(oldIncoming, newIncoming)
            || previous.IncomingSpeechEngine != next.IncomingSpeechEngine || previous.VibeVoiceModelId != next.VibeVoiceModelId || previous.VibeVoiceEndpoint != next.VibeVoiceEndpoint
            || previous.IncomingTargetLanguage != next.IncomingTargetLanguage || previous.IncomingSourceLanguage != next.IncomingSourceLanguage
            || previous.IncomingSettleMilliseconds != next.IncomingSettleMilliseconds || previous.SubtitlePreviewStableMilliseconds != next.SubtitlePreviewStableMilliseconds
            || previous.RecognitionDelayMilliseconds != next.RecognitionDelayMilliseconds || previous.CaptureVrChatOnly != next.CaptureVrChatOnly
            || previous.PlaybackDeviceId != next.PlaybackDeviceId || previous.NormalizeQuietPlayback != next.NormalizeQuietPlayback
            || !IncomingListening.SameSource(previous, next) || previous.IncomingSinglePerson != next.IncomingSinglePerson
            || previous.IncomingPersonId != next.IncomingPersonId
            || previous.UseAiSpeakerRecognition != next.UseAiSpeakerRecognition || previous.SpeakerModelPath != next.SpeakerModelPath
            || previous.SpeakerMatchThreshold != next.SpeakerMatchThreshold || previous.MaxSpeakerProfiles != next.MaxSpeakerProfiles
            || previous.TranslationQuality != next.TranslationQuality || previous.Glossary != next.Glossary
            || previous.OverlayMode != next.OverlayMode || previous.HistoryLinesPerSpeaker != next.HistoryLinesPerSpeaker
            || previous.VisibleSpeakerRows != next.VisibleSpeakerRows || previous.SubtitlesAlwaysOnTop != next.SubtitlesAlwaysOnTop
            || previous.ActiveTranslationProfileId != next.ActiveTranslationProfileId;
        bool shared = oldOwn.Shared != newOwn.Shared || oldOwn.Managed != newOwn.Managed
            || ((oldOwn.Shared || newOwn.Shared) && TranslatorChanged(oldOwn, newOwn));
        return new(own, incoming, shared);
    }
}

internal sealed partial class MainWindow
{
    internal async Task ApplyTranslationProfileAsync(string id)
    {
        if (_preview || _busy || _closing) return;
        AppSettings candidate;
        try
        {
            candidate = TranslationProfiles.Select(_settings, id);
            AppSettings saved = SettingsStore.Load();
            if (!TranslationProfiles.ConfigurationEquals(_settings, saved) && !TranslationProfiles.ConfigurationEquals(candidate, _settings)
                && !Confirm("Switch profiles and replace your unsaved changes? Save them as a custom profile first if you want to keep them.")) return;
        }
        catch (Exception error) { Error("Apply profile", error); return; }
        await ApplyTranslationSettingsAsync(candidate, applyingProfile: true);
    }

    private async Task ApplyTranslationSettingsAsync(AppSettings candidate, bool applyingProfile)
    {
        if (_preview || _busy || _closing) return;
        AppSettings previous = SettingsStore.Load();
        try
        {
            // Validate both directions before any live process is stopped.
            _ = RuntimeController.BuildPlan(candidate, incoming: false);
            _ = RuntimeController.BuildPlan(candidate, incoming: true);
            IncomingListening.Validate(candidate);
            RuntimeProfiles.ValidateIncomingSpeech(candidate);
            if (_runtime.IncomingRunning && RuntimeProfiles.UsesSpeakerRecognition(candidate) && !File.Exists(candidate.SpeakerModelPath))
                throw new ModelSelectionUserException("Download or choose a voice recognition model first, then enable recognition.");
        }
        catch (Exception error) { Error("Apply settings", error); return; }
        if (TranslationProfiles.ConfigurationEquals(candidate, previous) && !SubtitleSpeechFilesMissing(candidate))
        {
            try
            {
                if (candidate.ActiveTranslationProfileId != previous.ActiveTranslationProfileId
                    || JsonSerializer.Serialize(candidate.CustomTranslationProfiles) != JsonSerializer.Serialize(previous.CustomTranslationProfiles))
                    SettingsStore.SaveTranslationProfileMetadata(candidate);
                TranslationProfiles.CopyInto(candidate, _settings);
                if (_runtime.IncomingRunning && applyingProfile)
                    await _runtime.SwitchIncomingProfileAsync(_settings.Copy(), OpenOptions, _lifetime.Token, null, OpenSpeakerSettings, ApplyTranslationProfileAsync);
                UiLocalization.SetText(_status, "Profile applied. Translation was not restarted.");
                RefreshTranslationProfilesUi();
                RefreshSaveState(reloadSaved: true);
            }
            catch (Exception error) { Error("Apply profile", error); }
            return;
        }
        AppSettings draftBefore = _settings.Copy();
        bool own = _runtime.OwnRunning, incoming = _runtime.IncomingRunning;
        SettingsRuntimeChanges changes = SettingsRuntimeChanges.Between(previous, candidate);
        if (!applyingProfile && (own && changes.OwnChanged || own && incoming && changes.SharedTranslatorChanged)
            && !Confirm("Apply your changes? Translation will briefly pause, and an unfinished sentence may be lost.")) return;
        await RunTask(async token =>
        {
            await PrepareTranslationProfileAssetsAsync(candidate, token);
            token.ThrowIfCancellationRequested();
            TranslationProfiles.CopyInto(candidate, _settings);
            try { await ApplySettingsCoreAsync(own, incoming, token, applyingProfile, previous); }
            catch
            {
                // Preserve the user's draft when preparation/commit never completed;
                // after a runtime failure show the configuration actually saved.
                AppSettings saved = SettingsStore.Load();
                TranslationProfiles.CopyInto(TranslationProfiles.ConfigurationEquals(saved, previous) ? draftBefore : saved, _settings);
                throw;
            }
        }, applyingProfile ? "Applying profile" : "Applying settings", "Settings applied.");
        RefreshTranslationProfilesUi();
        RefreshSaveState();
    }

    private async Task PrepareTranslationProfileAssetsAsync(AppSettings candidate, CancellationToken token)
    {
        var preparation = new TranslationSelectionPreparation(_provisioner);
        bool download = preparation.NeedsDownload(candidate);
        foreach (string id in preparation.MissingModelIds(candidate))
        {
            ProvisionModelChoice model = LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == id);
            if (model.RequiresTermsAcceptance && CreateModelTermsDialog(model).ShowDialog() != true) throw new OperationCanceledException();
        }
        if (download && !Confirm(UiText.Format("Download required translation files ({0:0.0} GB) and apply these choices?", preparation.MissingDownloadBytes(candidate) / 1e9)))
            throw new OperationCanceledException();
        await preparation.PrepareAsync(candidate, ProvisionProgress(), token, allowDownload: download);
        await PrepareSubtitleSpeechFilesAsync(candidate, token);
        await EnsureGroupSpeechReadyForProfileAsync(candidate, token);
    }

    private async Task EnsureGroupSpeechReadyForProfileAsync(AppSettings candidate, CancellationToken token)
    {
        if (candidate.IncomingSpeechEngine != "vibevoice") return;
        TaskStage("Checking Groups setup");
        if (await _runtime.CheckGroupSpeechPreparedAsync(candidate, token)) return;
        string consent = candidate.VibeVoiceModelId.EndsWith("-7B", StringComparison.OrdinalIgnoreCase)
            ? "Prepare Groups mode? This downloads about 18 GB of speech-model files plus several GB of local tools. Audio stays on this PC. The speech model loads only if subtitles are running or you start them."
            : "Prepare Groups mode? This downloads about 6 GB of speech-model files plus several GB of local tools. Audio stays on this PC. The speech model loads only if subtitles are running or you start them.";
        if (!Confirm(consent)) throw new OperationCanceledException();
        TaskStage("Preparing Groups mode", "Downloading local speech tools. Translation keeps running until the switch is ready.");
        await _runtime.PrepareGroupSpeechAsync(candidate, ProvisionProgress(), token);
    }

    private void ApplySelectedProfileAppearance()
    {
        AppearanceTheme.Apply(_settings);
        ThemeBackground.Apply(_settings);
        UiText.Language = _settings.UiLanguage;
        _runtime.RefreshInterfaceLanguage();
    }
}

using System.Windows;
using System.Windows.Controls;

namespace IncomingTranslator;

internal sealed partial class MainWindow
{
    private readonly Grid _dashboardFrame;
    private FirstRunSetupView? _setupFlow;
    private FirstRunTourOverlay? _quickTour;
    private FrameworkElement? _ownVoiceTourTarget, _subtitlesTourTarget;
    private string _guidedReturnPage = "Home";

    internal static bool ShouldShowGuidedSetup(AppSettings settings, bool preview, bool explicitlyStartAll) =>
        !preview && !explicitlyStartAll && !settings.FirstRunSetupCompleted;

    private async Task RestartGuidedSetupAsync()
    {
        if (_busy || _closing || _setupFlow is not null) return;
        if (_runtime.OwnRunning || _runtime.IncomingRunning)
        {
            if (_preview || !Confirm("Run setup again? Active translations will stop. Your downloaded files and saved people will be kept.")) return;
            bool stopped = false;
            await RunTask(async _ => { await StopForTaskAsync(); stopped = true; }, "Stop all");
            if (!stopped || _runtime.OwnRunning || _runtime.IncomingRunning || _closing) return;
        }
        OpenGuidedSetup(firstRun: false);
    }

    // Also used by inert UI tests; preview hosts refuse every external operation.
    internal void OpenGuidedSetup(bool firstRun)
    {
        if (_busy || _closing || _setupFlow is not null) return;
        CloseQuickTour();
        _guidedReturnPage = firstRun ? "Home" : "Settings";
        _setupFlow = new FirstRunSetupView(_settings.Copy(),
            token => GuidedOperationAsync(async operation =>
            {
                RefusePreviewSetupWork();
                _hardware = await LocalHardwareDetector.DetectAsync(operation);
                operation.ThrowIfCancellationRequested();
                _recommendedModelId = HardwareAdvisor.Recommend(_hardware).ModelId;
                return _recommendedModelId;
            }, token),
            (draft, token) => GuidedOperationAsync(async operation =>
            {
                RefusePreviewSetupWork();
                return await new AutomaticSetupService(_provisioner, ownVoiceOnly: true).PlanAsync(draft.Copy(), operation);
            }, token),
            (draft, plan, progress, token) => GuidedOperationAsync(async operation =>
            {
                RefusePreviewSetupWork();
                // The view obtains explicit download/system-change consent on its review screen.
                // Additional model licences are never treated as implied by that consent.
                foreach (ProvisionModelChoice model in SetupModelsRequiringTerms(draft)
                    .Where(model => model.Id == draft.OwnManagedModelId))
                {
                    if (CreateModelTermsDialog(model).ShowDialog() != true)
                        throw new OperationCanceledException(operation);
                    operation.ThrowIfCancellationRequested();
                }
                if (_runtime.OwnRunning || _runtime.IncomingRunning)
                    throw new SetupUserException("Stop translation before running setup again.");
                _ = RuntimeController.BuildPlan(draft, incoming: false);
                bool alreadyConfigured = _settings.FirstRunSetupCompleted;
                // New users need resumable choices before a Windows restart. On a rerun,
                // keep the previous working selection until its replacement is verified.
                if (ShouldCommitGuidedChoices(alreadyConfigured, null)) { ApplyGuidedChoices(draft); Persist(); }
                AutomaticSetupResult result = await new AutomaticSetupService(_provisioner, ownVoiceOnly: true)
                    .RunAsync(draft, true, progress, operation);
                operation.ThrowIfCancellationRequested();
                if (ShouldCommitGuidedChoices(alreadyConfigured, result))
                {
                    ApplyGuidedChoices(draft);
                    if (result.Completed) _settings.FirstRunSetupCompleted = true;
                    Persist();
                }
                return result;
            }, token),
            CompleteGuidedSetup, LeaveGuidedSetup, ApplyGuidedAppearance);
        Content = _setupFlow;
        UiLocalization.ApplyTree(_setupFlow);
    }

    private void RefusePreviewSetupWork()
    {
        if (_preview) throw new InvalidOperationException("Offline preview: this action is disabled.");
    }

    internal static bool ShouldCommitGuidedChoices(bool alreadyConfigured, AutomaticSetupResult? result) =>
        !alreadyConfigured || result?.Completed == true;

    private async Task<T> GuidedOperationAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        if (_busy || _closing) throw new OperationCanceledException(token);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _busy = true; _operation = operation; _operationFinished = finished;
        try { return await action(operation.Token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            DesktopLog.Write("Guided setup failed: " + error);
            throw;
        }
        finally
        {
            _busy = false; _operation = null;
            finished.TrySetResult();
        }
    }

    private void ApplyGuidedAppearance(AppSettings draft)
    {
        // Only appearance preferences are saved while choosing. Translation choices are
        // committed after the user explicitly asks us to prepare the required files.
        if (!_preview)
        {
            SettingsStore.SaveUiLanguagePreference(draft.UiLanguage);
            SettingsStore.SaveAppearancePreference(draft);
        }
        _settings.UiLanguage = draft.UiLanguage;
        _settings.UiThemeId = draft.UiThemeId;
        AppearanceTheme.Apply(draft);
        ThemeBackground.Apply(draft);
        UiText.Language = draft.UiLanguage;
        RefreshSaveState(reloadSaved: !_preview);
    }

    private void ApplyGuidedChoices(AppSettings draft)
    {
        _settings.UiLanguage = draft.UiLanguage;
        _settings.UiThemeId = draft.UiThemeId;
        _settings.OwnVoiceTargetLanguage = draft.OwnVoiceTargetLanguage;
        _settings.OwnVoiceSourceLanguage = draft.OwnVoiceSourceLanguage;
        _settings.OwnVoiceAdditionalLanguagesEnabled = draft.OwnVoiceAdditionalLanguagesEnabled;
        _settings.OwnVoiceAdditionalTargetLanguages = draft.OwnVoiceAdditionalTargetLanguages.ToList();
        _settings.OwnRecognitionEngine = draft.OwnRecognitionEngine;
        _settings.OwnMicrophoneDeviceId = draft.OwnMicrophoneDeviceId;
        _settings.UseManagedTranslator = draft.UseManagedTranslator;
        _settings.ShareTranslationModel = draft.ShareTranslationModel;
        _settings.OwnManagedModelId = draft.OwnManagedModelId;
        if (draft.ShareTranslationModel) _settings.IncomingManagedModelId = draft.IncomingManagedModelId;
        _settings.ManagedRuntimeId = draft.ManagedRuntimeId;
        _settings.OwnTranslatorModel = draft.OwnTranslatorModel;
        if (draft.ShareTranslationModel) _settings.TranslatorModel = draft.TranslatorModel;
    }

    private void CompleteGuidedSetup(AppSettings draft)
    {
        if (_busy || _closing) return;
        // Preparation already verified readiness. Finishing only opens Home;
        // it deliberately cannot start an engine or open a microphone.
        ApplyGuidedChoices(draft);
        _settings.FirstRunSetupCompleted = true;
        if (!_preview) Persist();
        _guidedReturnPage = "Home";
        LeaveGuidedSetup();
        ShowQuickTour();
    }

    internal void LeaveGuidedSetup()
    {
        if (_busy || _closing || _setupFlow is null) return;
        _setupFlow.Dispose(); _setupFlow = null;
        Content = _dashboardFrame;
        Navigate(_settings.FirstRunSetupCompleted ? _guidedReturnPage : "Home");
        UiLocalization.ApplyTree(this);
    }

    internal void ShowQuickTour()
    {
        if (_busy || _closing || _setupFlow is not null) return;
        CloseQuickTour();
        Navigate("Home");
        ResetVoiceFirstHomeScroll();
        // A tour is useful only when the two Start controls actually exist.
        if (_ownVoiceTourTarget is null || _subtitlesTourTarget is null) return;
        string? lastTargetKey = null;
        FrameworkElement? ResolveTourTarget(string key)
        {
            FrameworkElement? target = key switch
            {
                "Help" => _navigation.GetValueOrDefault("Help"),
                "OwnVoice" => _ownVoiceTourTarget,
                "Subtitles" => _subtitlesTourTarget,
                "Settings" => _navigation.GetValueOrDefault("Settings"),
                _ => null
            };
            // Optional subtitles are deliberately below the main voice controls.
            // Scroll only when changing tour steps, never on every layout pass.
            // Mark the key first: BringIntoView/UpdateLayout can notify the tour
            // synchronously and must not create a repeated scrolling loop.
            if (target is not null && !string.Equals(lastTargetKey, key, StringComparison.Ordinal))
            {
                lastTargetKey = key;
                target.BringIntoView();
                _dashboardFrame.UpdateLayout();
            }
            return target;
        }
        _quickTour = new FirstRunTourOverlay(ResolveTourTarget, () =>
        {
            CloseQuickTour();
            if (_preview) return;
            try
            {
                SettingsStore.SaveFirstRunIntroductionPreference(true);
                _settings.FirstRunIntroductionCompleted = true;
            }
            catch (Exception error) { Error("Save introduction preference", error); }
        });
        Grid.SetColumnSpan(_quickTour, 2); Panel.SetZIndex(_quickTour, 100);
        _dashboardFrame.Children.Add(_quickTour);
        Dispatcher.InvokeAsync(() => _quickTour?.RefreshPlacement(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CloseQuickTour()
    {
        if (_quickTour is null) return;
        _quickTour.Dispose(); _dashboardFrame.Children.Remove(_quickTour); _quickTour = null;
        // The tour may have revealed the optional subtitle card below the main
        // controls. Closing it must return to the app's primary Start action,
        // rather than leaving the very first Home screen scrolled past it.
        ResetVoiceFirstHomeScroll();
    }

    private void ResetVoiceFirstHomeScroll()
    {
        if (_closing || _page != "Home" || !ReferenceEquals(Content, _dashboardFrame)) return;
        if (_content.Parent is not ScrollViewer viewport) return;
        viewport.ScrollToTop();
        _dashboardFrame.UpdateLayout();
        // Finish/Skip can remove the coach during a layout callback. Repeat once
        // after that callback only if this exact Home is still current; never
        // reset a different page or a new tour opened in the meantime.
        object home = _content.Content;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_closing || _quickTour is not null || _page != "Home" ||
                !ReferenceEquals(Content, _dashboardFrame) || !ReferenceEquals(_content.Content, home)) return;
            viewport.ScrollToTop(); _dashboardFrame.UpdateLayout();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}

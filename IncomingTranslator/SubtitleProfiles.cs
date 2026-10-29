using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IncomingTranslator;

internal sealed partial class SubtitleWindow
{
    internal Func<string, Task>? ProfileChangeRequested { get; set; }
    private Button? _normalProfileButton, _groupsProfileButton;
    private TextBlock? _subtitleProfileCaption;
    private bool _profileSwitchPending;
    private CancellationTokenSource? _serviceRunCancellation;
    private Task? _serviceRunTask;

    private FrameworkElement CreateSubtitleProfileBar()
    {
        var stack = new StackPanel();
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _normalProfileButton = ProfileButton("SubtitleNormalProfileButton", TranslationProfiles.NormalId);
        _groupsProfileButton = ProfileButton("SubtitleGroupsProfileButton", TranslationProfiles.GroupsId);
        buttons.Children.Add(_normalProfileButton); buttons.Children.Add(_groupsProfileButton);
        DockPanel.SetDock(buttons, Dock.Left); row.Children.Add(buttons);
        _subtitleProfileCaption = new TextBlock { Foreground = OverlayMuted, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis };
        MarkLiteralUiProvider?.Invoke(_subtitleProfileCaption);
        row.Children.Add(_subtitleProfileCaption);
        stack.Children.Add(row); stack.Children.Add(_source);
        RefreshSubtitleProfileButtons();
        return stack;
    }

    private Button ProfileButton(string name, string id)
    {
        var button = new Button { Name = name, Height = 27, MinWidth = 68,
            Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand };
        MarkLiteralUiProvider?.Invoke(button);
        button.Click += async (_, _) => await RequestSubtitleProfileAsync(id);
        return button;
    }

    private void RefreshSubtitleProfileButtons()
    {
        if (_normalProfileButton is null || _groupsProfileButton is null || _subtitleProfileCaption is null) return;
        _normalProfileButton.Content = Ui("Normal");
        _groupsProfileButton.Content = Ui("Groups");
        _normalProfileButton.ToolTip = Ui("Recognize familiar people while translating their speech.");
        _groupsProfileButton.ToolTip = Ui("Recognize separate voices with VibeVoice. Adds delay and uses more memory.") + " · " + Ui("Experimental");
        foreach (var pair in new[] { (Button: _normalProfileButton, Id: TranslationProfiles.NormalId),
            (Button: _groupsProfileButton, Id: TranslationProfiles.GroupsId) })
        {
            bool selected = _settings.ActiveTranslationProfileId == pair.Id;
            pair.Button.Foreground = selected ? OverlaySage : OverlayMuted;
            pair.Button.BorderBrush = selected ? OverlaySage : OverlayStroke;
            pair.Button.IsEnabled = !_profileSwitchPending && ProfileChangeRequested is not null && !_shutdown.IsCancellationRequested;
            System.Windows.Automation.AutomationProperties.SetName(pair.Button, Ui(pair.Id == TranslationProfiles.NormalId ? "Normal" : "Groups"));
        }
        _subtitleProfileCaption.Text = _profileSwitchPending ? Ui("Switching subtitle mode…") :
            _settings.ActiveTranslationProfileId == TranslationProfiles.QuickId ? Ui("Quick · change profiles in Settings") :
            _settings.ActiveTranslationProfileId == TranslationProfiles.GroupsId ? Ui("Experimental") :
            TranslationProfiles.IsBuiltIn(_settings.ActiveTranslationProfileId) ? "" : TranslationProfiles.DisplayName(_settings);
    }

    private async Task RequestSubtitleProfileAsync(string id)
    {
        if (id is not (TranslationProfiles.NormalId or TranslationProfiles.GroupsId) || _profileSwitchPending
            || ProfileChangeRequested is null || _shutdown.IsCancellationRequested) return;
        if (id == _settings.ActiveTranslationProfileId && !TranslationProfiles.IsModified(_settings)) return;
        _profileSwitchPending = true; RefreshSubtitleProfileButtons();
        try { await ProfileChangeRequested(id); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogOverlayError("Switch subtitle profile", error);
            await SetStatus("Couldn't switch subtitle mode. Previous settings kept.", true);
        }
        finally { _profileSwitchPending = false; RefreshSubtitleProfileButtons(); }
    }

    private void StartOverlayService()
    {
        if (_shutdown.IsCancellationRequested) throw new ObjectDisposedException(nameof(SubtitleWindow));
        if (_serviceRunTask is { IsCompleted: false }) throw new InvalidOperationException("The previous subtitle service is still stopping.");
        _serviceRunCancellation?.Dispose();
        _serviceRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var runCancellation = _serviceRunCancellation;
        long lastId = _displayHistory.Count == 0 ? 0 : _displayHistory.Max(display => display.UtteranceId);
        var service = new IncomingTranslationService(_settings, SetStatus,
            text => Dispatcher.InvokeAsync(new Action(() => { _receivedSource = true; _source.Text = text; })).Task,
            result => Dispatcher.InvokeAsync(new Action(() => Publish(result))).Task, initialBubbleId: lastId);
        _service = service;
        _serviceRunTask = RunOverlayServiceAsync(service, runCancellation);
        RefreshSubtitleProfileButtons();
    }

    private async Task RunOverlayServiceAsync(IncomingTranslationService service, CancellationTokenSource runCancellation)
    {
        try { await service.RunAsync(runCancellation.Token); }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogOverlayError("Subtitle service stopped", error);
            await SetStatus("Subtitles stopped. Check Settings, then restart subtitles.", true);
        }
        finally
        {
            service.Dispose();
            if (ReferenceEquals(_service, service)) _service = null;
            if (_shutdown.IsCancellationRequested) _serviceStopped.TrySetResult();
        }
    }

    internal async Task PauseForProfileAsync(CancellationToken token = default)
    {
        Task? stopping = await Dispatcher.InvokeAsync(() =>
        {
            _serviceRunCancellation?.Cancel();
            return _serviceRunTask;
        }).Task.ConfigureAwait(false);
        if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        await SetStatus("Subtitles paused while switching", false).ConfigureAwait(false);
    }

    internal Task ResumeProfileAsync(AppSettings settings, CancellationToken token = default) => Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested();
        _settings = settings.Copy();
        _receivedSource = false;
        RefreshInterfaceLanguage();
        StartOverlayService();
    }).Task;

    internal async Task ApplyLiveProfileAsync(AppSettings settings, CancellationToken token = default)
    {
        IncomingTranslationService? service = await Dispatcher.InvokeAsync(() => _service).Task.ConfigureAwait(false);
        if (service is null)
        {
            await ResumeProfileAsync(settings, token).ConfigureAwait(false);
            return;
        }
        bool modelChanged = !string.Equals(_settings.SpeakerModelPath, settings.SpeakerModelPath, StringComparison.OrdinalIgnoreCase);
        if (modelChanged) await service.ApplySpeakerRecognitionAsync(false, token).ConfigureAwait(false);
        service.UpdateSettings(settings);
        await service.ApplySpeakerRecognitionAsync(settings.UseAiSpeakerRecognition, token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            _settings = settings.Copy();
            RefreshInterfaceLanguage();
            RenderHistory();
        }).Task.ConfigureAwait(false);
    }
}

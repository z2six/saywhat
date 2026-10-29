using NAudio.CoreAudioApi;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IncomingTranslator;

internal sealed partial class MainWindow
{
    private StackPanel VoiceFirstHomePage()
    {
        var page = Page("");
        var voice = new StackPanel();
        var heading = new DockPanel { LastChildFill = true };
        HomeOwnVoiceApplyState ownApplyState = GetHomeOwnVoiceApplyState();
        var ownActions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        var headerApply = CreateHomeOwnVoiceApplyButton("HomeOwnVoiceHeaderApplyButton", ownApplyState);
        headerApply.Visibility = ownApplyState.Pending ? Visibility.Visible : Visibility.Collapsed;
        ownActions.Children.Add(headerApply);
        _ownVoiceTourTarget = VoiceFirstButton(_runtime.OwnRunning ? "Stop my voice" : ownApplyState.Pending ? "Apply & start" : "Start translating my voice",
            async (_, _) => await ToggleVoiceFirstOwnAsync(), primary: true);
        _ownVoiceTourTarget.Name = "OwnVoiceStartButton";
        _ownVoiceTourTarget.MinWidth = 190;
        _ownVoiceTourTarget.IsEnabled = !_busy && !_closing;
        ownActions.Children.Add(_ownVoiceTourTarget);
        DockPanel.SetDock(ownActions, Dock.Right); heading.Children.Add(ownActions);
        var title = new StackPanel();
        title.Children.Add(ModernTheme.Kicker("MY VOICE", ModernTheme.Accent));
        title.Children.Add(VoiceFirstHeading("Translate into VRChat chat", 27));
        heading.Children.Add(title); voice.Children.Add(heading);
        AddBadge(voice, "own", _runtime.OwnRunning ? "RUNNING" : "STOPPED");

        const string muteLabel = "Pause when my VRChat mic is muted";
        const string muteHelp = "When off, SayWhat can capture and send translations while your VRChat mic is muted.";
        var muteCaption = VoiceFirstLabel(muteLabel, 12); muteCaption.Margin = new Thickness(0);
        var followMute = new CheckBox
        {
            Name = "HomeFollowVrchatMuteToggle", Content = muteCaption,
            IsChecked = _settings.OwnVoicePauseWhenVrchatMuted, Foreground = ModernTheme.Text,
            Margin = new Thickness(0, 0, 0, 4), IsEnabled = CanEditVoiceFirst(),
            ToolTip = VoiceFirstText.T(muteHelp)
        };
        UiLocalization.Literal(followMute);
        AutomationProperties.SetName(followMute, VoiceFirstText.T(muteLabel));
        AutomationProperties.SetHelpText(followMute, VoiceFirstText.T(muteHelp));
        followMute.Checked += async (_, _) => await SetVoiceFirstFollowVrchatMuteAsync(true);
        followMute.Unchecked += async (_, _) => await SetVoiceFirstFollowVrchatMuteAsync(false);
        voice.Children.Add(followMute);
        var muteHint = VoiceFirstLabel(muteHelp, 11, ModernTheme.Warning);
        muteHint.Name = "HomeFollowVrchatMuteHint";
        muteHint.Visibility = _settings.OwnVoicePauseWhenVrchatMuted ? Visibility.Collapsed : Visibility.Visible;
        voice.Children.Add(muteHint);

        voice.Children.Add(VoiceFirstLabel("I speak", 13));
        voice.Children.Add(CreateVoiceFirstSourceChoices());
        if (_settings.OwnRecognitionEngine == "voxtral")
            voice.Children.Add(VoiceFirstLabel("Voxtral detects the spoken language automatically. Choose Nemotron to set it yourself.", 12, ModernTheme.Muted));
        voice.Children.Add(VoiceFirstLabel("Translate to · choose up to four", 13));
        voice.Children.Add(CreateVoiceFirstTargetChoices());
        var space = VoiceFirstLabel("Translations share VRChat’s 144-character limit. More languages leave less room for each message.", 11,
            _settings.EffectiveOwnVoiceTargetLanguages().Count > 1 ? ModernTheme.Warning : ModernTheme.Muted);
        space.Name = "HomeChatLengthHint"; voice.Children.Add(space);

        voice.Children.Add(VoiceFirstLabel("Speech recognition · hears your words", 13));
        var recognizers = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 4) };
        foreach (OwnRecognitionChoice choice in OwnRecognitionCatalog.Choices)
        {
            bool compatible = choice.SupportsSource(_settings.OwnVoiceSourceLanguage);
            string detail = choice.Id == "nemotron" ? "Native Windows. Uses your chosen spoken language."
                : "Continuous recognition. Detects the language automatically.";
            var button = VoiceFirstChoiceCard(choice.Name, detail, choice.Id == _settings.OwnRecognitionEngine,
                async (_, _) => await SelectVoiceFirstRecognitionAsync(choice.Id), compatible,
                compatible ? null : "Choose Automatic above to use this recognizer.");
            button.Name = choice.Id == "nemotron" ? "HomeNemotronRecognitionButton" : "HomeVoxtralRecognitionButton";
            button.Tag = choice.Id; recognizers.Children.Add(button);
        }
        voice.Children.Add(recognizers);

        voice.Children.Add(VoiceFirstLabel("Translation model · translates your words", 13));
        var translators = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var choice in VoiceFirstTranslationChoices)
        {
            ProvisionModelChoice model = LocalTalkProvisioner.ModelCatalog.First(item => item.Id == choice.Id);
            string? problem = TranslationModelCapabilities.Validate(model.ApiAlias, _settings.OwnVoiceSourceLanguage,
                _settings.EffectiveOwnVoiceTargetLanguages());
            var button = VoiceFirstChoiceCard(choice.Title, choice.Description,
                _settings.UseManagedTranslator && _settings.OwnManagedModelId == choice.Id,
                async (_, _) => await SelectVoiceFirstTranslationAsync(choice.Id), problem is null,
                problem is null ? null : TranslationModelCapabilities.IsLfmEnJp(model.ApiAlias)
                    ? "English ↔ Japanese only. Choose those languages above first."
                    : "Choose the language you speak above first.");
            button.Name = choice.ButtonName; button.Tag = choice.Id; translators.Children.Add(button);
        }
        voice.Children.Add(translators);
        if (!_settings.UseManagedTranslator || !VoiceFirstTranslationChoices.Any(choice => choice.Id == _settings.OwnManagedModelId))
        {
            string current = _settings.UseManagedTranslator ? _settings.OwnManagedModelId : _settings.OwnTranslatorModel;
            voice.Children.Add(ModernTheme.Label(VoiceFirstText.Format("Current translator: {0}", current), 12, ModernTheme.Muted, localize: false));
        }
        var modelLinks = new DockPanel();
        var allModels = VoiceFirstButton("All models and downloads", (_, _) => Navigate("Models"));
        allModels.MinHeight = 28; allModels.Padding = new Thickness(8, 4, 8, 4);
        allModels.Margin = new Thickness(8, 0, 0, 4); DockPanel.SetDock(allModels, Dock.Right); modelLinks.Children.Add(allModels);
        var modelsNote = VoiceFirstLabel(_settings.ShareTranslationModel
            ? "Subtitles use this translator too." : "Subtitles keep their own translator.", 11, ModernTheme.Muted);
        modelsNote.VerticalAlignment = VerticalAlignment.Center; modelLinks.Children.Add(modelsNote); voice.Children.Add(modelLinks);

        var controls = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition()); controls.ColumnDefinitions.Add(new ColumnDefinition());
        var timing = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        timing.Children.Add(VoiceFirstLabel("When messages appear", 13));
        timing.Children.Add(VoiceFirstLabel("How long to wait after I stop speaking", 11, ModernTheme.Muted));
        var pause = new WrapPanel();
        foreach (int milliseconds in new[] { 900, 1200, 1800, 2400 })
        {
            int value = milliseconds;
            string seconds = (milliseconds / 1000d).ToString("0.0", System.Globalization.CultureInfo.CurrentUICulture) + " s";
            var choice = VoiceFirstButton(seconds, (_, _) =>
            {
                if (!CanEditVoiceFirst()) return;
                _settings.OwnVoiceEndPauseMilliseconds = value;
                RefreshHomeOwnVoiceChoices();
            }, literal: true);
            choice.Name = "HomePause" + milliseconds;
            choice.Tag = value; choice.MinHeight = 30; choice.Padding = new Thickness(9, 5, 9, 5);
            choice.Background = Math.Abs(_settings.OwnVoiceEndPauseMilliseconds - value) < 50 ? ModernTheme.Selection : ModernTheme.Raised;
            pause.Children.Add(choice);
        }
        timing.Children.Add(pause);
        if (!new[] { 900, 1200, 1800, 2400 }.Contains(_settings.OwnVoiceEndPauseMilliseconds))
            timing.Children.Add(ModernTheme.Label(VoiceFirstText.Format("Current wait: {0:0.0} s", _settings.OwnVoiceEndPauseMilliseconds / 1000d), 11, ModernTheme.Muted, localize: false));
        controls.Children.Add(timing);
        var microphone = new StackPanel();
        microphone.Children.Add(VoiceFirstLabel("Microphone", 13));
        microphone.Children.Add(CreateVoiceFirstMicrophonePicker());
        Grid.SetColumn(microphone, 1); controls.Children.Add(microphone); voice.Children.Add(controls);
        var previews = new CheckBox
        {
            Name = "HomeEarlyTranslationsToggle", Content = VoiceFirstText.T("Show translations while I’m still speaking"),
            IsChecked = _settings.OwnVoiceSendEarlyPreviews, Foreground = ModernTheme.Text, Margin = new Thickness(0, 6, 0, 6),
            IsEnabled = CanEditVoiceFirst()
        };
        UiLocalization.Literal(previews);
        previews.Checked += async (_, _) => await SetVoiceFirstEarlyAsync(true);
        previews.Unchecked += async (_, _) => await SetVoiceFirstEarlyAsync(false);
        voice.Children.Add(previews);
        voice.Children.Add(VoiceFirstLabel("On: chat updates as you speak. Off: send only after the pause.", 11, ModernTheme.Muted));
        AppendHomeOwnVoiceApply(voice, ownApplyState);
        var ownCard = ModernTheme.Card(voice); ownCard.Name = "HomeOwnVoiceCard";
        ownCard.Background = ModernTheme.Hero; ownCard.BorderBrush = ModernTheme.HeroStroke;
        ownCard.Padding = new Thickness(20); page.Children.Add(ownCard);
        page.Children.Add(CreatePipelineIssueBanner());
        page.Children.Add(CreateVoiceFirstSubtitleCard());
        if (_runtime.OwnRunning || _runtime.IncomingRunning)
            page.Children.Add(ModernTheme.Label("Stop all releases the translation models from memory.", 11, ModernTheme.Muted));
        var vrchatHelp = new StackPanel();
        _oscHelpText = ModernTheme.Label("", 13, ModernTheme.Text); vrchatHelp.Children.Add(_oscHelpText);
        vrchatHelp.Children.Add(VoiceFirstLabel(_settings.OwnVoicePauseWhenVrchatMuted
            ? "For your voice to appear in chat, turn on OSC in VRChat's Action Menu: OSC → Enabled. You only need to enable it once. Then unmute your VRChat microphone."
            : "For your voice to appear in chat, turn on OSC in VRChat's Action Menu: OSC → Enabled. You only need to enable it once.", 13, ModernTheme.Muted));
        vrchatHelp.Children.Add(ModernTheme.Button("Check connection again", async (_, _) => await CheckOscConnectionAsync(manual: true)));
        _oscHelp = new Expander { Content = vrchatHelp, Foreground = ModernTheme.Muted, Margin = new Thickness(0, 8, 0, 0) };
        // A literal parent would suppress live translations of status changes in
        // its children. Bind the header and keep the help body localizable.
        UiLocalization.Bind(_oscHelp, HeaderedContentControl.HeaderProperty, "My voice isn't appearing in VRChat");
        page.Children.Add(_oscHelp); RenderOscConnectionStatus();
        return page;
    }

    private static readonly (string Id, string Title, string Description, string ButtonName)[] VoiceFirstTranslationChoices =
    [
        ("hy-mt2-1.8b-q4_k_m", "HY-MT2 · Fast", "Small, dedicated translator. Recommended starting point.", "HomeHyFastModelButton"),
        ("hy-mt2-7b-q4_k_m", "HY-MT2 · Larger", "More memory. Try it for difficult wording; it may take longer.", "HomeHyLargerModelButton"),
        ("translategemma-4b-q4_k_m", "TranslateGemma", "Alternative translator. Needs a chosen spoken language.", "HomeTranslateGemmaModelButton"),
        ("lfm2-350m-enjp-mt-q4_k_m", "Liquid · EN/JP", "Very small. Only translates English and Japanese.", "HomeLiquidModelButton")
    ];

    private FrameworkElement CreateVoiceFirstSourceChoices()
    {
        var choices = new WrapPanel { Name = "HomeSpokenLanguageChoices", Margin = new Thickness(0, 0, 0, 5) };
        OwnRecognitionChoice recognizer = OwnRecognitionCatalog.Choices.First(choice => choice.Id == _settings.OwnRecognitionEngine);
        foreach (string language in new[] { "Automatic" }.Concat(OwnRecognitionCatalog.SupportedSourceLanguages))
        {
            string value = language;
            bool supported = recognizer.SupportsSource(language);
            var button = VoiceFirstButton(UiText.T(language), (_, _) =>
            {
                if (!CanEditVoiceFirst() || !recognizer.SupportsSource(value)) return;
                _settings.OwnVoiceSourceLanguage = value;
                RefreshHomeOwnVoiceChoices();
            }, literal: true);
            button.Tag = value;
            button.Background = value == _settings.OwnVoiceSourceLanguage ? ModernTheme.Selection : ModernTheme.Raised;
            button.BorderBrush = value == _settings.OwnVoiceSourceLanguage ? ModernTheme.Accent : ModernTheme.Stroke;
            button.IsEnabled = CanEditVoiceFirst() && supported;
            button.MinHeight = 30; button.Padding = new Thickness(9, 5, 9, 5);
            if (!supported) button.ToolTip = VoiceFirstText.T("Choose Nemotron below to set a spoken language.");
            ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetName(button, VoiceFirstText.T("I speak") + ": " + UiText.T(value));
            choices.Children.Add(button);
        }
        return choices;
    }

    private FrameworkElement CreateVoiceFirstTargetChoices()
    {
        var choices = new WrapPanel { Name = "HomeTargetLanguageChoices", Margin = new Thickness(0, 0, 0, 5) };
        IReadOnlyList<string> current = _settings.EffectiveOwnVoiceTargetLanguages();
        IReadOnlyList<string> allowed = AllowedTargetLanguages(incoming: false);
        foreach (string language in Languages)
        {
            string value = language;
            bool selected = current.Contains(value);
            int index = current.ToList().IndexOf(value);
            var button = VoiceFirstButton((selected ? (index + 1) + " · " : "+ ") + UiText.T(value), (_, _) =>
            {
                if (!CanEditVoiceFirst() || !ToggleVoiceFirstTarget(_settings, value)) return;
                RefreshHomeOwnVoiceChoices();
            }, literal: true);
            button.Tag = value; button.Background = selected ? ModernTheme.Selection : ModernTheme.Raised;
            button.BorderBrush = selected ? ModernTheme.Accent : ModernTheme.Stroke;
            button.IsEnabled = CanEditVoiceFirst() && allowed.Contains(value) && (selected ? current.Count > 1 : current.Count < 4);
            button.MinHeight = 30; button.Padding = new Thickness(9, 5, 9, 5);
            string help = !allowed.Contains(value) ? "This translator does not support that language."
                : selected && current.Count == 1 ? "Keep at least one translation language."
                : !selected && current.Count == 4 ? "Remove a language before adding another." : "Click to add or remove a language.";
            button.ToolTip = VoiceFirstText.T(help); ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetName(button, UiText.T("Translate my voice to") + ": " + UiText.T(value));
            AutomationProperties.SetHelpText(button, VoiceFirstText.T(help));
            choices.Children.Add(button);
        }
        return choices;
    }

    internal static bool ToggleVoiceFirstTarget(AppSettings settings, string language)
    {
        language = AppSettings.CanonicalTranslationLanguage(language) ?? "";
        if (language.Length == 0 || !TranslationModelCapabilities.SupportedTargets(HomeTranslationAlias(settings, incoming: false),
            settings.OwnVoiceSourceLanguage).Contains(language)) return false;
        List<string> targets = settings.EffectiveOwnVoiceTargetLanguages().ToList();
        if (targets.Contains(language)) { if (targets.Count == 1) return false; targets.Remove(language); }
        else { if (targets.Count == 4) return false; targets.Add(language); }
        settings.OwnVoiceTargetLanguage = targets[0];
        settings.OwnVoiceAdditionalTargetLanguages = targets.Skip(1).ToList();
        settings.OwnVoiceAdditionalLanguagesEnabled = targets.Count > 1;
        return true;
    }

    private FrameworkElement CreateVoiceFirstSubtitleCard()
    {
        var incoming = new StackPanel();
        var row = new DockPanel();
        var incomingActions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        var headerApply = CreateHomeSubtitleApplyButton(inHeader: true);
        headerApply.Visibility = HasPendingHomeSubtitleChoices() ? Visibility.Visible : Visibility.Collapsed;
        incomingActions.Children.Add(headerApply);
        _subtitlesTourTarget = ModernTheme.Button(_runtime.IncomingRunning ? "Stop subtitles" :
            VoiceFirstIncomingNeedsPreparation() ? "Prepare live subtitles" : "Start subtitles", async (_, _) => await ToggleVoiceFirstSubtitlesAsync());
        _subtitlesTourTarget.Name = "SubtitlesStartButton";
        incomingActions.Children.Add(_subtitlesTourTarget);
        DockPanel.SetDock(incomingActions, Dock.Right); row.Children.Add(incomingActions);
        var copy = new StackPanel(); copy.Children.Add(VoiceFirstHeading("Optional · live subtitles", 21));
        copy.Children.Add(VoiceFirstLabel("Read other people’s translated speech in a separate window.", 12, ModernTheme.Muted));
        row.Children.Add(copy); incoming.Children.Add(row);
        if (!SupportsApplicationSubtitles) incoming.Children.Add(CreateIncomingCaptureNotice("HomeSubtitleCaptureCompatibilityNotice"));
        _subtitlesTourTarget.IsEnabled = CanEditSubtitleSpeech() && (_runtime.IncomingRunning || SupportsSelectedSubtitleSource && !HasPendingHomeSubtitleChoices());
        if (!_runtime.IncomingRunning && !SupportsSelectedSubtitleSource)
            _subtitlesTourTarget.ToolTip = UiText.T(IncomingCaptureCapabilities.UnavailableMessage);
        if (!_runtime.IncomingRunning && SupportsSelectedSubtitleSource && HasPendingHomeSubtitleChoices())
            _subtitlesTourTarget.ToolTip = SubtitleSpeechText.T("Apply your subtitle choices before starting.");
        ToolTipService.SetShowOnDisabled(_subtitlesTourTarget, true);
        incoming.Children.Add(CreateSubtitleSpeechCard(onHome: true));
        var card = ModernTheme.Card(incoming); card.Name = "HomeSubtitlesCard"; card.Padding = new Thickness(18);
        return card;
    }

    private FrameworkElement CreateVoiceFirstMicrophonePicker()
    {
        var choices = new List<VoiceFirstMicrophoneChoice> { new("default", VoiceFirstText.T("Windows default microphone")) };
        if (!_preview)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (MMDevice device in devices)
                {
                    // FoxTrans's native audio selector accepts a unique display
                    // name, not a CoreAudio endpoint GUID. Its resolver rejects
                    // ambiguous names instead of choosing the wrong microphone.
                    using (device)
                        if (!choices.Any(choice => choice.Id == device.FriendlyName))
                            choices.Add(new(device.FriendlyName, device.FriendlyName));
                }
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
            { DesktopLog.Write("Microphone choice list unavailable: " + error.GetType().Name); }
        }
        if (!choices.Any(choice => choice.Id == _settings.OwnMicrophoneDeviceId))
            choices.Add(new(_settings.OwnMicrophoneDeviceId, VoiceFirstText.T("Saved microphone · not connected")));
        var picker = new ComboBox
        {
            Name = "HomeMicrophonePicker", ItemsSource = choices,
            SelectedItem = choices.First(choice => choice.Id == _settings.OwnMicrophoneDeviceId),
            IsEnabled = CanEditVoiceFirst(), MinHeight = 34, Margin = new Thickness(0, 0, 0, 6)
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (!CanEditVoiceFirst() || picker.SelectedItem is not VoiceFirstMicrophoneChoice choice) return;
            _settings.OwnMicrophoneDeviceId = choice.Id;
            RefreshHomeOwnVoiceChoices();
        };
        UiLocalization.Literal(picker); AutomationProperties.SetName(picker, VoiceFirstText.T("Microphone"));
        return picker;
    }

    private sealed record VoiceFirstMicrophoneChoice(string Id, string Name) { public override string ToString() => Name; }

    private bool CanEditVoiceFirst() => !_busy && !_closing;

    private void RefreshHomeOwnVoiceChoices()
    {
        RefreshSaveState();
        if (!_closing && _page == "Home") Navigate("Home");
    }

    private Task SetVoiceFirstEarlyAsync(bool value)
    {
        if (!CanEditVoiceFirst()) return Task.CompletedTask;
        _settings.OwnVoiceSendEarlyPreviews = value;
        RefreshHomeOwnVoiceChoices();
        return Task.CompletedTask;
    }

    private Task SetVoiceFirstFollowVrchatMuteAsync(bool value)
    {
        if (!CanEditVoiceFirst()) return Task.CompletedTask;
        _settings.OwnVoicePauseWhenVrchatMuted = value;
        RefreshHomeOwnVoiceChoices();
        return Task.CompletedTask;
    }

    private Task SelectVoiceFirstRecognitionAsync(string id)
    {
        if (!CanEditVoiceFirst() || !OwnRecognitionCatalog.Choices.Any(choice => choice.Id == id && choice.SupportsSource(_settings.OwnVoiceSourceLanguage))) return Task.CompletedTask;
        _settings.OwnRecognitionEngine = id;
        RefreshHomeOwnVoiceChoices();
        return Task.CompletedTask;
    }

    private Task SelectVoiceFirstTranslationAsync(string id)
    {
        if (!CanEditVoiceFirst()) return Task.CompletedTask;
        ProvisionModelChoice? choice = LocalTalkProvisioner.ModelCatalog.FirstOrDefault(model => model.Id == id && model.Role == ProvisionModelRole.Translation);
        if (choice is null || TranslationModelCapabilities.Validate(choice.ApiAlias, _settings.OwnVoiceSourceLanguage, _settings.EffectiveOwnVoiceTargetLanguages()) is not null) return Task.CompletedTask;
        _settings.UseManagedTranslator = true;
        _settings.OwnManagedModelId = id;
        RefreshHomeOwnVoiceChoices();
        return Task.CompletedTask;
    }

    private AppSettings HomeOwnVoiceSavedSettings()
    {
        try { return ReadVoiceFirstSavedSettings(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { return _saveStateAppliedSettings?.Copy() ?? _settings.Copy(); }
    }

    private readonly record struct HomeOwnVoiceApplyState(bool Pending, bool Download);

    private HomeOwnVoiceApplyState GetHomeOwnVoiceApplyState()
    {
        AppSettings saved = HomeOwnVoiceSavedSettings();
        bool changed = !TranslationProfiles.ConfigurationEquals(VoiceFirstCandidate(saved, saved, includeModels: true),
            VoiceFirstCandidate(saved, _settings, includeModels: true));
        bool download = false;
        if (changed)
        {
            try
            {
                AppSettings candidate = VoiceFirstCandidate(saved, _settings, includeModels: true);
                download = MissingVoiceFirstDownloadBytes(candidate) > 0 ||
                    candidate.OwnRecognitionEngine == "voxtral" && _provisioner.SpeechRegistration is null;
            }
            catch (ArgumentException) { /* Apply reports the exact incompatible model/language selection. */ }
        }
        return new(changed, download);
    }

    private Button CreateHomeOwnVoiceApplyButton(string name, HomeOwnVoiceApplyState state)
    {
        var apply = VoiceFirstButton(state.Download ? "Download & apply" : "Apply changes", async (_, _) => await ApplyVoiceFirstModelChangeAsync());
        apply.Name = name; apply.IsEnabled = CanEditVoiceFirst() && state.Pending;
        apply.Background = apply.IsEnabled ? ModernTheme.Accent : ModernTheme.Raised;
        apply.Foreground = apply.IsEnabled ? ModernTheme.Ink : ModernTheme.Muted;
        return apply;
    }

    private void AppendHomeOwnVoiceApply(StackPanel voice, HomeOwnVoiceApplyState state)
    {
        var status = VoiceFirstLabel(state.Pending ? "Not applied yet" : "Current selection", 12, state.Pending ? ModernTheme.Warning : ModernTheme.Good);
        status.Name = "HomeOwnVoiceSelectionState"; voice.Children.Add(status);
        voice.Children.Add(CreateHomeOwnVoiceApplyButton("ApplyHomeOwnVoiceChoicesButton", state));
        voice.Children.Add(VoiceFirstLabel(_runtime.OwnRunning
            ? "Choose everything first, then apply once. Your current translation keeps running until you apply."
            : "Choose everything first, then apply once. Applying does not start your microphone.", 11, ModernTheme.Muted));
    }

    private async Task ToggleVoiceFirstOwnAsync()
    {
        if (_busy || _closing || _preview) return;
        if (!_runtime.OwnRunning && !_settings.FirstRunSetupCompleted) { OpenGuidedSetup(firstRun: true); return; }
        if (!_runtime.OwnRunning)
        {
            // Preparing and saving through the own-voice path keeps every
            // unapplied subtitle choice staged, even when both cards are open.
            await ApplyVoiceFirstModelChangeAsync(startOwn: true);
            return;
        }
        await RunTask(async token =>
        {
            TaskStage("Stopping your voice translator"); await _runtime.StopOwnAsync();
        }, "Voice translation");
    }

    private static TextBlock VoiceFirstLabel(string text, double size = 14, Brush? brush = null) =>
        ModernTheme.Label(VoiceFirstText.T(text), size, brush, localize: false);

    private static TextBlock VoiceFirstHeading(string text, double size = 27)
    {
        var heading = VoiceFirstLabel(text, size); heading.FontFamily = new FontFamily("Georgia"); return heading;
    }

    private Button VoiceFirstButton(string text, RoutedEventHandler click, bool primary = false, bool literal = false)
    {
        string caption = literal ? text : VoiceFirstText.T(text);
        var button = ModernTheme.Button(caption, click, primary);
        UiLocalization.Literal(button);
        AutomationProperties.SetName(button, caption); return button;
    }

    private Button VoiceFirstChoiceCard(string title, string description, bool selected, RoutedEventHandler click, bool compatible = true, string? reason = null)
    {
        var content = new StackPanel();
        var label = VoiceFirstLabel((selected ? "✓ " : "") + VoiceFirstText.T(title), 12);
        label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, 0, 0, 5); content.Children.Add(label);
        var detail = VoiceFirstLabel(description, 11, ModernTheme.Muted); detail.Margin = new Thickness(0); content.Children.Add(detail);
        if (!compatible && reason is not null)
        {
            var why = VoiceFirstLabel(reason, 10, ModernTheme.Warning); why.Margin = new Thickness(0, 5, 0, 0); content.Children.Add(why);
        }
        var button = new Button
        {
            Content = content, Background = selected ? ModernTheme.Selection : ModernTheme.Raised,
            Foreground = ModernTheme.Text, BorderBrush = selected ? ModernTheme.Accent : ModernTheme.Stroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 0, 7, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
            IsEnabled = CanEditVoiceFirst() && compatible
        };
        button.Click += click; UiLocalization.Literal(button);
        AutomationProperties.SetName(button, VoiceFirstText.T(title));
        AutomationProperties.SetHelpText(button, VoiceFirstText.T(description));
        if (reason is not null) button.ToolTip = VoiceFirstText.T(reason);
        ToolTipService.SetShowOnDisabled(button, true); return button;
    }
}

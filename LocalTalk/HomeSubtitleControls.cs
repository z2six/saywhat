using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace IncomingTranslator;

// The optional Home section stages an incoming-only snapshot. Applying it uses
// the same preparation and runtime switch as Save all; it never commits an
// unrelated microphone draft or starts a stopped translation direction.
internal sealed partial class MainWindow
{
    internal static AppSettings HomeSubtitleCandidate(AppSettings saved, AppSettings draft)
    {
        AppSettings candidate = SubtitleSpeechCandidate(saved, draft);
        if (saved.IncomingSpeechEngine == draft.IncomingSpeechEngine && saved.IncomingSourceLanguage == draft.IncomingSourceLanguage)
        {
            // Old installations may still carry unused translation hints. Do
            // not turn opening Home into an unapplied recognition change.
            candidate.DetectAnySourceLanguage = saved.DetectAnySourceLanguage;
            candidate.SourceLanguages = saved.SourceLanguages.ToList();
        }
        candidate.IncomingTargetLanguage = draft.IncomingTargetLanguage;
        candidate.IncomingManagedModelId = draft.IncomingManagedModelId;
        candidate.ShareTranslationModel = draft.ShareTranslationModel;
        candidate.TranslatorBaseUrl = draft.TranslatorBaseUrl;
        candidate.TranslatorModel = draft.TranslatorModel;
        candidate.IncomingAudioMode = draft.IncomingAudioMode;
        candidate.CaptureVrChatOnly = draft.CaptureVrChatOnly;
        candidate.IncomingApplicationPath = draft.IncomingApplicationPath;
        candidate.IncomingApplicationName = draft.IncomingApplicationName;
        candidate.IncomingSinglePerson = draft.IncomingSinglePerson;
        candidate.IncomingPersonId = draft.IncomingPersonId;
        candidate.IncomingSettleMilliseconds = draft.IncomingSettleMilliseconds;
        candidate.IncomingSendEarlyPreviews = draft.IncomingSendEarlyPreviews;
        // Profile selection here changes only the incoming recognizer. In
        // particular, it must not apply a preset's global recognition delay.
        if (draft.IncomingSpeechEngine == "vibevoice") candidate.ActiveTranslationProfileId = TranslationProfiles.GroupsId;
        else if (draft.ActiveTranslationProfileId == TranslationProfiles.NormalId || saved.ActiveTranslationProfileId == TranslationProfiles.GroupsId)
            candidate.ActiveTranslationProfileId = TranslationProfiles.NormalId;
        return candidate;
    }

    internal static bool StageHomeSubtitleTarget(AppSettings settings, string language)
    {
        string? canonical = AppSettings.CanonicalTranslationLanguage(language);
        if (canonical is null || !TranslationModelCapabilities.SupportedTargets(HomeTranslationAlias(settings, incoming: true), settings.IncomingSourceLanguage).Contains(canonical)) return false;
        settings.IncomingTargetLanguage = canonical;
        return true;
    }

    internal static bool StageHomeSubtitleTranslator(AppSettings settings, string id)
    {
        ProvisionModelChoice? model = LocalTalkProvisioner.ModelCatalog.FirstOrDefault(model => model.Id == id && model.Role == ProvisionModelRole.Translation);
        // External translation is a global advanced choice. A subtitle card may
        // not silently replace the microphone's external translator with ours.
        if (!settings.UseManagedTranslator || model is null || TranslationModelCapabilities.Validate(model.ApiAlias, settings.IncomingSourceLanguage, [settings.IncomingTargetLanguage]) is not null) return false;
        if (settings.ShareTranslationModel && id != settings.OwnManagedModelId) settings.ShareTranslationModel = false;
        settings.IncomingManagedModelId = id;
        return true;
    }

    internal static bool StageHomeSubtitleSharing(AppSettings settings, bool share)
    {
        if (share && TranslationModelCapabilities.Validate(HomeTranslationAlias(settings, incoming: false), settings.IncomingSourceLanguage, [settings.IncomingTargetLanguage]) is not null) return false;
        if (!share && settings.ShareTranslationModel)
        {
            settings.IncomingManagedModelId = settings.OwnManagedModelId;
            settings.TranslatorBaseUrl = settings.OwnTranslatorBaseUrl;
            settings.TranslatorModel = settings.OwnTranslatorModel;
        }
        settings.ShareTranslationModel = share;
        return true;
    }

    internal static bool StageHomeSubtitlePause(AppSettings settings, int milliseconds)
    {
        if (milliseconds < 600 || milliseconds > 2400) return false;
        settings.IncomingSettleMilliseconds = milliseconds;
        return true;
    }

    internal static bool StageHomeSubtitleProfile(AppSettings settings, string id)
    {
        if (id == TranslationProfiles.GroupsId)
        {
            if (TranslationModelCapabilities.Validate(HomeTranslationAlias(settings, true), "Automatic", [settings.IncomingTargetLanguage]) is not null) return false;
            if (settings.IncomingSpeechEngine != "vibevoice")
            {
                settings.IncomingNormalSpeechEngine = settings.IncomingSpeechEngine;
                settings.IncomingNormalSourceLanguage = settings.IncomingSourceLanguage;
            }
            settings.IncomingSpeechEngine = "vibevoice"; settings.IncomingSourceLanguage = "Automatic";
            settings.DetectAnySourceLanguage = true; settings.ActiveTranslationProfileId = TranslationProfiles.GroupsId;
            return true;
        }
        if (id != TranslationProfiles.NormalId) return false;
        if (settings.IncomingSpeechEngine == "vibevoice")
        {
            settings.IncomingSpeechEngine = settings.IncomingNormalSpeechEngine;
            settings.IncomingSourceLanguage = settings.IncomingNormalSourceLanguage;
            settings.DetectAnySourceLanguage = settings.IncomingSourceLanguage == "Automatic";
            if (!settings.DetectAnySourceLanguage) settings.SourceLanguages = [settings.IncomingSourceLanguage];
        }
        settings.ActiveTranslationProfileId = TranslationProfiles.NormalId;
        return true;
    }

    private AppSettings HomeSubtitleSavedSettings() => _preview ? _saveStateAppliedSettings?.Copy() ?? _settings.Copy() : SettingsStore.Load();
    private AppSettings HomeSubtitleSelection() => _preview ? _settings.Copy() : HomeSubtitleCandidate(HomeSubtitleSavedSettings(), _settings);
    private bool HasPendingHomeSubtitleChoices()
    {
        AppSettings saved = HomeSubtitleSavedSettings();
        return !TranslationProfiles.ConfigurationEquals(saved, HomeSubtitleCandidate(saved, _settings));
    }

    private void RefreshHomeSubtitleChoices()
    {
        RefreshSaveState();
        // Navigating to the same page retains its scroll position.
        Navigate(_page);
    }

    private void AppendHomeSubtitleTargets(StackPanel body)
    {
        body.Children.Add(SubtitleSpeechLabel("Translate their voices to", 14));
        var choices = new WrapPanel { Name = "HomeSubtitleTargetChoices", Margin = new Thickness(0, 0, 0, 8) };
        AppSettings selection = HomeSubtitleSelection();
        IReadOnlyList<string> allowed = TranslationModelCapabilities.SupportedTargets(HomeTranslationAlias(selection, true), selection.IncomingSourceLanguage);
        foreach (string language in AppSettings.TranslationLanguages)
        {
            string value = language;
            var button = SubtitleSpeechButton(UiText.T(value), (_, _) =>
            {
                if (!CanEditSubtitleSpeech()) return;
                AppSettings staged = HomeSubtitleSelection();
                if (!StageHomeSubtitleTarget(staged, value)) return;
                _settings.IncomingTargetLanguage = staged.IncomingTargetLanguage; RefreshHomeSubtitleChoices();
            });
            button.Name = "HomeSubtitleTargetChoice"; button.Tag = value;
            button.IsEnabled = CanEditSubtitleSpeech() && allowed.Contains(value);
            SetHomeSubtitleSelected(button, value == _settings.IncomingTargetLanguage);
            AutomationProperties.SetName(button, UiText.T("Translate their voices to") + ": " + UiText.T(value));
            if (!allowed.Contains(value)) button.ToolTip = SubtitleSpeechText.T("Choose compatible languages or another translator first.");
            ToolTipService.SetShowOnDisabled(button, true); choices.Children.Add(button);
        }
        body.Children.Add(choices);
    }

    private void AppendHomeSubtitleConfiguration(StackPanel body)
    {
        AppSettings selection = HomeSubtitleSelection();
        body.Children.Add(SubtitleSpeechLabel("Translation model · translates their words", 14));
        var shared = HomeSubtitleCheck("Use the same translator as My voice", _settings.ShareTranslationModel, (_, _) =>
        {
            if (!CanEditSubtitleSpeech()) return;
            AppSettings staged = HomeSubtitleSelection();
            if (!StageHomeSubtitleSharing(staged, !_settings.ShareTranslationModel)) return;
            _settings.ShareTranslationModel = staged.ShareTranslationModel;
            _settings.IncomingManagedModelId = staged.IncomingManagedModelId;
            _settings.TranslatorBaseUrl = staged.TranslatorBaseUrl;
            _settings.TranslatorModel = staged.TranslatorModel;
            RefreshHomeSubtitleChoices();
        });
        shared.Name = "HomeSubtitleSharedTranslatorToggle";
        shared.IsEnabled = CanEditSubtitleSpeech() && (_settings.ShareTranslationModel ||
            TranslationModelCapabilities.Validate(HomeTranslationAlias(selection, false), selection.IncomingSourceLanguage, [selection.IncomingTargetLanguage]) is null);
        body.Children.Add(shared);
        body.Children.Add(SubtitleSpeechLabel("Sharing a translator uses less memory. Choosing a different model below gives subtitles their own translator.", 12, ModernTheme.Muted));
        var translators = new UniformGrid { Name = "HomeSubtitleTranslatorChoices", Columns = 2 };
        foreach (var choice in VoiceFirstTranslationChoices)
        {
            string id = choice.Id;
            ProvisionModelChoice model = LocalTalkProvisioner.ModelCatalog.First(item => item.Id == id);
            bool supported = selection.UseManagedTranslator && TranslationModelCapabilities.Validate(model.ApiAlias, selection.IncomingSourceLanguage, [selection.IncomingTargetLanguage]) is null;
            bool selected = selection.UseManagedTranslator && (selection.ShareTranslationModel ? selection.OwnManagedModelId : selection.IncomingManagedModelId) == id;
            var copy = new StackPanel(); copy.Children.Add(VoiceFirstLabel(choice.Title, 14)); copy.Children.Add(VoiceFirstLabel(choice.Description, 12, ModernTheme.Muted));
            var button = SubtitleSpeechButton("", (_, _) =>
            {
                if (!CanEditSubtitleSpeech()) return;
                AppSettings staged = HomeSubtitleSelection();
                if (!StageHomeSubtitleTranslator(staged, id)) return;
                _settings.ShareTranslationModel = staged.ShareTranslationModel;
                _settings.IncomingManagedModelId = staged.IncomingManagedModelId;
                RefreshHomeSubtitleChoices();
            });
            button.Content = copy; button.Name = "HomeSubtitleTranslatorChoice"; button.Tag = id;
            button.IsEnabled = CanEditSubtitleSpeech() && supported; button.MinHeight = 82;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Top;
            SetHomeSubtitleSelected(button, selected);
            AutomationProperties.SetName(button, SubtitleSpeechText.T("Translation model · translates their words") + ": " + VoiceFirstText.T(choice.Title));
            AutomationProperties.SetHelpText(button, VoiceFirstText.T(choice.Description));
            if (!supported) button.ToolTip = SubtitleSpeechText.T(selection.UseManagedTranslator
                ? "Choose compatible languages or another translator first." : "An external translator is selected. Change this in Models to use downloaded translators.");
            ToolTipService.SetShowOnDisabled(button, true); translators.Children.Add(button);
        }
        body.Children.Add(translators);
        if (!selection.UseManagedTranslator || !VoiceFirstTranslationChoices.Any(choice => choice.Id == (selection.ShareTranslationModel ? selection.OwnManagedModelId : selection.IncomingManagedModelId)))
            body.Children.Add(ModernTheme.Label(VoiceFirstText.Format("Current translator: {0}", HomeTranslationAlias(selection, true)), 12, ModernTheme.Muted, localize: false));

        AppendHomeSubtitleListening(body);
        body.Children.Add(SubtitleSpeechLabel("How long to wait after they stop speaking", 14));
        body.Children.Add(SubtitleSpeechLabel("A shorter pause shows sentences sooner. A longer pause keeps more of a thought together.", 12, ModernTheme.Muted));
        var pauses = new WrapPanel { Name = "HomeSubtitlePauseChoices" };
        foreach (int milliseconds in new[] { 900, 1200, 1800, 2400 })
        {
            int value = milliseconds;
            string caption = (value / 1000d).ToString("0.0", System.Globalization.CultureInfo.CurrentUICulture) + " s";
            var button = SubtitleSpeechButton(caption, (_, _) =>
            { if (CanEditSubtitleSpeech() && StageHomeSubtitlePause(_settings, value)) RefreshHomeSubtitleChoices(); });
            button.Name = "HomeSubtitlePauseChoice"; button.Tag = value; button.IsEnabled = CanEditSubtitleSpeech();
            SetHomeSubtitleSelected(button, _settings.IncomingSettleMilliseconds == value); pauses.Children.Add(button);
        }
        body.Children.Add(pauses);
        if (!new[] { 900, 1200, 1800, 2400 }.Contains(_settings.IncomingSettleMilliseconds))
            body.Children.Add(ModernTheme.Label(VoiceFirstText.Format("Current wait: {0:0.0} s", _settings.IncomingSettleMilliseconds / 1000d), 12, ModernTheme.Muted, localize: false));

        var early = HomeSubtitleCheck("Show early translations", _settings.IncomingSendEarlyPreviews, (_, _) =>
        { if (CanEditSubtitleSpeech()) { _settings.IncomingSendEarlyPreviews = !_settings.IncomingSendEarlyPreviews; RefreshHomeSubtitleChoices(); } });
        early.Name = "HomeSubtitleEarlyTranslationsToggle"; body.Children.Add(early);
        body.Children.Add(SubtitleSpeechLabel("Show a translation before the sentence is finished. It may change as more words arrive.", 12, ModernTheme.Muted));
        AppendHomeSubtitlePeople(body);
        AppSettings saved = HomeSubtitleSavedSettings();
        bool changed = !TranslationProfiles.ConfigurationEquals(saved, HomeSubtitleCandidate(saved, _settings));
        string translatorId = selection.ShareTranslationModel ? selection.OwnManagedModelId : selection.IncomingManagedModelId;
        ProvisionModelChoice? selectedTranslator = LocalTalkProvisioner.ModelCatalog.FirstOrDefault(model => model.Id == translatorId && model.Role == ProvisionModelRole.Translation);
        bool translationMissing = selection.UseManagedTranslator &&
            (selectedTranslator is null || _provisioner.ResolveModelPath(translatorId) is null || !VoiceFirstRuntimeReady(selection.ManagedRuntimeId));
        bool missing = SubtitleSpeechFilesMissing(selection) || translationMissing;
        var status = SubtitleSpeechLabel(changed ? "Not applied yet" : "Current selection", 12, changed ? ModernTheme.Warning : ModernTheme.Good);
        status.Name = "HomeSubtitleSelectionState"; body.Children.Add(status);
        if (missing)
        {
            long bytes = PendingSubtitleSpeechBytes(selection);
            if (selection.UseManagedTranslator)
            {
                if (selectedTranslator is not null && _provisioner.ResolveModelPath(translatorId) is null) bytes += selectedTranslator.SizeBytes;
                if (!VoiceFirstRuntimeReady(selection.ManagedRuntimeId)) bytes += LocalTalkProvisioner.RuntimeCatalog.FirstOrDefault(runtime => runtime.Id == selection.ManagedRuntimeId)?.SizeBytes ?? 0;
            }
            var download = ModernTheme.Label(SubtitleSpeechText.Format("Download needed: {0:0.0} GB. Files stay on this PC and are reused.", bytes / 1e9), 12, ModernTheme.Muted, localize: false);
            download.Name = "HomeSubtitleDownloadSize"; body.Children.Add(download);
        }
        var apply = SubtitleSpeechButton(missing ? "Download & apply" : "Apply subtitle choices", async (_, _) => await ApplyHomeSubtitleChoicesAsync());
        apply.Name = "ApplyHomeSubtitleChoicesButton"; apply.IsEnabled = CanEditSubtitleSpeech() && SupportsSelectedSubtitleSource && (changed || missing);
        apply.Background = apply.IsEnabled ? ModernTheme.Accent : ModernTheme.Raised;
        apply.Foreground = apply.IsEnabled ? ModernTheme.Ink : ModernTheme.Muted; body.Children.Add(apply);
        body.Children.Add(SubtitleSpeechLabel(saved.ShareTranslationModel != selection.ShareTranslationModel
            ? "Changing model sharing briefly pauses both translators. Your microphone choices stay the same."
            : _runtime.IncomingRunning ? "Files download before subtitles briefly pause to apply the change. Your voice translator keeps running."
                : "Apply these choices, then choose Start subtitles on Home. Models load only when subtitles start.", 12, ModernTheme.Muted));
    }

    private void AppendHomeSubtitleListening(StackPanel body)
    {
        body.Children.Add(SubtitleSpeechLabel("Listen to", 14));
        var sources = new WrapPanel { Name = "HomeSubtitleAudioSourceChoices" };
        foreach (var source in new[] { new IncomingSourceChoice("vrchat", "VRChat"), new IncomingSourceChoice("application", "Another application"), new IncomingSourceChoice("desktop", "All playback") })
        {
            string id = source.Id;
            var button = SubtitleSpeechButton(source.Label, (_, _) =>
            {
                if (!CanEditSubtitleSpeech() || id != "desktop" && !SupportsApplicationSubtitles) return;
                _settings.IncomingAudioMode = id; _settings.CaptureVrChatOnly = id == "vrchat"; RefreshHomeSubtitleChoices();
            });
            button.Name = "HomeSubtitleAudioSourceChoice"; button.Tag = id; button.IsEnabled = CanEditSubtitleSpeech() && (id == "desktop" || SupportsApplicationSubtitles);
            if (id != "desktop" && !SupportsApplicationSubtitles) button.ToolTip = UiText.T(IncomingCaptureCapabilities.UnavailableMessage);
            ToolTipService.SetShowOnDisabled(button, true);
            SetHomeSubtitleSelected(button, IncomingListening.SourceMode(_settings) == id); sources.Children.Add(button);
        }
        body.Children.Add(sources);
        if (IncomingListening.SourceMode(_settings) == "application")
        {
            var selected = ModernTheme.Label(IncomingSourceDisplayName(_settings), 13, ModernTheme.Text, localize: false);
            selected.Name = "HomeSubtitleSelectedApplication"; body.Children.Add(selected);
            var choose = SubtitleSpeechButton("Choose application…", (_, _) => OpenHomeSubtitleApplicationPicker());
            choose.Name = "HomeSubtitleChooseApplicationButton"; choose.IsEnabled = CanEditSubtitleSpeech() && SupportsApplicationSubtitles; body.Children.Add(choose);
        }
        else if (IncomingListening.SourceMode(_settings) == "desktop")
            body.Children.Add(SubtitleSpeechLabel("All playback includes music, browsers and notifications. Choose an application to exclude them.", 12, ModernTheme.Warning));
    }

    private void AppendHomeSubtitlePeople(StackPanel body)
    {
        body.Children.Add(SubtitleSpeechLabel("People", 14));
        var single = HomeSubtitleCheck("Just one person", _settings.IncomingSinglePerson, (_, _) =>
        { if (CanEditSubtitleSpeech()) { _settings.IncomingSinglePerson = !_settings.IncomingSinglePerson; RefreshHomeSubtitleChoices(); } });
        single.Name = "HomeSubtitleSinglePersonToggle"; body.Children.Add(single);
        if (_settings.IncomingSinglePerson)
        {
            string name = _savedPeople.FirstOrDefault(person => person.Id == _settings.IncomingPersonId)?.Name ?? UiText.T(string.IsNullOrEmpty(_settings.IncomingPersonId) ? "No name selected" : "Saved person selected");
            var label = ModernTheme.Label(name, 13, localize: false); label.Name = "HomeSubtitleAssignedPerson"; body.Children.Add(label);
            var choose = SubtitleSpeechButton("Choose from People…", (_, _) => OpenHomeSubtitlePersonPicker());
            choose.Name = "HomeSubtitleChoosePersonButton"; choose.IsEnabled = CanEditSubtitleSpeech(); body.Children.Add(choose);
            body.Children.Add(SubtitleSpeechLabel("All subtitles use this name. Automatic person recognition is skipped, and this choice does not teach a voice example.", 12, ModernTheme.Muted));
        }
        bool quick = _settings.ActiveTranslationProfileId == TranslationProfiles.QuickId;
        var recognition = HomeSubtitleCheck("Recognize saved people by their voices", _settings.UseAiSpeakerRecognition, async (_, _) =>
        {
            if (!CanEditSubtitleSpeech() || _settings.IncomingSinglePerson || _settings.ActiveTranslationProfileId == TranslationProfiles.QuickId) return;
            if (_preview) { _settings.UseAiSpeakerRecognition = !_settings.UseAiSpeakerRecognition; RefreshHomeSubtitleChoices(); }
            else { await ChangeSpeakerRecognitionAsync(!_settings.UseAiSpeakerRecognition); if (!_closing && _page == "Home") Navigate("Home"); }
        });
        recognition.Name = "HomeSubtitlePersonRecognitionToggle"; recognition.IsEnabled = CanEditSubtitleSpeech() && !_settings.IncomingSinglePerson && !quick;
        body.Children.Add(recognition);
        body.Children.Add(SubtitleSpeechLabel("Voice recognition is optional. Voice examples and names stay on this PC.", 12, ModernTheme.Muted));
        var profiles = new WrapPanel { Name = "HomeSubtitleProfileChoices" };
        foreach (string id in new[] { TranslationProfiles.NormalId, TranslationProfiles.GroupsId })
        {
            string value = id;
            var proposed = HomeSubtitleSelection(); bool compatible = StageHomeSubtitleProfile(proposed, value);
            var button = SubtitleSpeechButton(id == TranslationProfiles.GroupsId ? "Groups" : "Normal", (_, _) =>
            {
                if (!CanEditSubtitleSpeech() || !compatible) return;
                AppSettings staged = HomeSubtitleSelection(); if (!StageHomeSubtitleProfile(staged, value)) return;
                _settings.IncomingSpeechEngine = staged.IncomingSpeechEngine; _settings.IncomingSourceLanguage = staged.IncomingSourceLanguage;
                _settings.IncomingNormalSpeechEngine = staged.IncomingNormalSpeechEngine; _settings.IncomingNormalSourceLanguage = staged.IncomingNormalSourceLanguage;
                _settings.DetectAnySourceLanguage = staged.DetectAnySourceLanguage; _settings.SourceLanguages = staged.SourceLanguages.ToList();
                _settings.ActiveTranslationProfileId = staged.ActiveTranslationProfileId; RefreshHomeSubtitleChoices();
            });
            button.Name = "HomeSubtitleProfileChoice"; button.Tag = value; button.IsEnabled = CanEditSubtitleSpeech() && compatible;
            SetHomeSubtitleSelected(button, value == TranslationProfiles.GroupsId ? _settings.IncomingSpeechEngine == "vibevoice"
                : _settings.IncomingSpeechEngine != "vibevoice" && !quick); profiles.Children.Add(button);
        }
        body.Children.Add(profiles);
        if (quick) body.Children.Add(SubtitleSpeechLabel("Quick is active and skips voice recognition. Choose Normal to recognize saved people.", 12, ModernTheme.Muted));
        body.Children.Add(SubtitleSpeechLabel("Normal shows words sooner. Groups tries to separate overlapping voices, but needs more memory and adds delay.", 12, ModernTheme.Muted));
    }

    private CheckBox HomeSubtitleCheck(string caption, bool selected, RoutedEventHandler changed)
    {
        var check = new CheckBox { Content = SubtitleSpeechText.T(caption), IsChecked = selected,
            Foreground = ModernTheme.Text, IsEnabled = CanEditSubtitleSpeech(), Margin = new Thickness(0, 5, 0, 7) };
        UiLocalization.Literal(check); AutomationProperties.SetName(check, SubtitleSpeechText.T(caption));
        check.Click += changed; return check;
    }

    private static void SetHomeSubtitleSelected(Button button, bool selected)
    { button.Background = selected ? ModernTheme.Selection : ModernTheme.Raised; button.BorderBrush = selected ? ModernTheme.Accent : ModernTheme.Stroke; }

    private async Task ApplyHomeSubtitleChoicesAsync()
    {
        if (_preview || !CanEditSubtitleSpeech()) return;
        AppSettings saved = SettingsStore.Load(), draft = _settings.Copy();
        try { IncomingCaptureCapabilities.Validate(HomeSubtitleCandidate(saved, draft), SubtitleWindowsVersion); }
        catch (IncomingListeningSelectionException error) { Error("Apply subtitle choices", error); return; }
        await ApplyTranslationSettingsAsync(HomeSubtitleCandidate(saved, draft), applyingProfile: false);
        TranslationProfiles.CopyInto(SettingsSaveState.Rebase(draft, saved, SettingsStore.Load()), _settings);
        RefreshSaveState(reloadSaved: true); if (!_closing) Navigate(_page);
    }

    private void OpenHomeSubtitleApplicationPicker()
    {
        if (!CanEditSubtitleSpeech() || !SupportsApplicationSubtitles) return;
        Window? dialog = null;
        var view = new ListeningApplicationPicker(ApplicationAudioCatalog.ListRunning, choice =>
        {
            if (!CanEditSubtitleSpeech() || !SupportsApplicationSubtitles || ApplicationAudioCatalog.CanonicalExecutable(choice.Path).Length == 0) return;
            _settings.IncomingAudioMode = "application"; _settings.CaptureVrChatOnly = false;
            _settings.IncomingApplicationPath = choice.Path; _settings.IncomingApplicationName = choice.Name;
            dialog!.DialogResult = true; RefreshHomeSubtitleChoices();
        });
        dialog = CreateListeningDialog("Choose application", view);
        dialog.Loaded += async (_, _) => { view.FocusSearch(); if (!_preview) await view.RefreshAsync(); };
        dialog.Closed += (_, _) => view.CancelRefresh(); dialog.ShowDialog();
    }

    private void OpenHomeSubtitlePersonPicker()
    {
        if (!CanEditSubtitleSpeech()) return;
        Window? dialog = null;
        var view = new ListeningPersonPicker(_savedPeople, _savedPeopleLoaded || _preview, person =>
        {
            if (!CanEditSubtitleSpeech()) return;
            _settings.IncomingPersonId = person?.Id ?? "";
            if (person is not null && !_savedPeople.Any(existing => existing.Id == person.Id)) _savedPeople = _savedPeople.Append(person).ToArray();
            dialog!.DialogResult = true; RefreshHomeSubtitleChoices();
        });
        dialog = CreateListeningDialog("Choose a person", view); bool closed = false;
        dialog.Loaded += async (_, _) =>
        {
            view.FocusSearch(); if (_preview) return;
            try { await ReadSavedPeopleAsync(_lifetime.Token); if (!closed) view.SetPeople(_savedPeople); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closed) view.SetError(); DesktopLog.Write("Home person picker metadata read failed: " + error.GetType().Name); }
        };
        dialog.Closed += (_, _) => closed = true; dialog.ShowDialog();
    }
}

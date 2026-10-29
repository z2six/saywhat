using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace IncomingTranslator;

// Speech choices belong to incoming audio only. Browsing and staging them never
// starts a model, captures audio, or changes the working microphone translator.
internal sealed partial class MainWindow
{
    private static readonly string[] SubtitleSourceChoices = ["Automatic", "Chinese", "English", "Japanese", "Korean", "Spanish", "French", "German", "Portuguese", "Russian"];

    internal static AppSettings SubtitleSpeechCandidate(AppSettings saved, AppSettings draft)
    {
        AppSettings candidate = saved.Copy();
        candidate.IncomingSpeechEngine = draft.IncomingSpeechEngine;
        candidate.IncomingSourceLanguage = draft.IncomingSourceLanguage;
        candidate.IncomingChineseOnly = draft.IncomingChineseOnly;
        candidate.IncomingNormalSpeechEngine = draft.IncomingNormalSpeechEngine;
        candidate.IncomingNormalSourceLanguage = draft.IncomingNormalSourceLanguage;
        candidate.DetectAnySourceLanguage = draft.IncomingSourceLanguage == "Automatic";
        if (!candidate.DetectAnySourceLanguage) candidate.SourceLanguages = [draft.IncomingSourceLanguage];
        if (candidate.IncomingSpeechEngine != "vibevoice" && candidate.ActiveTranslationProfileId == TranslationProfiles.GroupsId)
            candidate.ActiveTranslationProfileId = TranslationProfiles.NormalId;
        return candidate;
    }

    internal static bool StageSubtitleSpeechEngine(AppSettings settings, string id)
    {
        if (!IncomingRecognitionCatalog.Choices.Any(choice => choice.Id == id) || id == "vibevoice") return false;
        settings.IncomingSpeechEngine = id;
        if (!IncomingRecognitionCatalog.Find(id).SupportsSource(settings.IncomingSourceLanguage))
            settings.IncomingSourceLanguage = IncomingRecognitionCatalog.UsesFireRed(id) ? "Chinese" : "Automatic";
        RememberSubtitleSpeech(settings);
        return true;
    }

    internal static bool StageSubtitleSourceLanguage(AppSettings settings, string language)
    {
        if (!SubtitleSourceChoices.Contains(language)) return false;
        bool sourceChanged = settings.IncomingSourceLanguage != language;
        // A language is a real recognition choice, not merely a translation hint.
        // Select a compatible recognizer visibly when the old one cannot use it.
        if (!IncomingRecognitionCatalog.Find(settings.IncomingSpeechEngine).SupportsSource(language))
            settings.IncomingSpeechEngine = language == "Chinese" ? "firered2-aed" : "nemotron";
        settings.IncomingSourceLanguage = language;
        if (language != "Chinese") settings.IncomingChineseOnly = false;
        else if (sourceChanged) settings.IncomingChineseOnly = true;
        RememberSubtitleSpeech(settings);
        return true;
    }

    private static void RememberSubtitleSpeech(AppSettings settings)
    {
        if (settings.IncomingSpeechEngine != "vibevoice")
        {
            settings.IncomingNormalSpeechEngine = settings.IncomingSpeechEngine;
            settings.IncomingNormalSourceLanguage = settings.IncomingSourceLanguage;
            if (settings.ActiveTranslationProfileId == TranslationProfiles.GroupsId)
                settings.ActiveTranslationProfileId = TranslationProfiles.NormalId;
        }
        settings.DetectAnySourceLanguage = settings.IncomingSourceLanguage == "Automatic";
        if (!settings.DetectAnySourceLanguage) settings.SourceLanguages = [settings.IncomingSourceLanguage];
    }

    private bool CanEditSubtitleSpeech() => !_busy && !_closing;

    private Border CreateSubtitleSpeechCard(bool onHome = false)
    {
        var body = new StackPanel { Name = "SubtitleSpeechSettings" };
        if (!onHome)
        {
            body.Children.Add(SubtitleSpeechLabel("Subtitle speech recognition", 21));
            body.Children.Add(SubtitleSpeechLabel("Choose what people are speaking and the model that hears their words. Your microphone settings stay unchanged.", 12, ModernTheme.Muted));
        }
        if (_settings.IncomingSpeechEngine == "vibevoice")
            body.Children.Add(SubtitleSpeechLabel("Groups uses VibeVoice and detects the language automatically. Choosing a model or a specific language below returns to Normal subtitles.", 12, ModernTheme.Warning));
        body.Children.Add(SubtitleSpeechLabel("They speak", 14));
        var languages = new WrapPanel { Name = "SubtitleSpokenLanguageChoices", Margin = new Thickness(0, 0, 0, 8) };
        string alias = onHome ? HomeTranslationAlias(HomeSubtitleSelection(), incoming: true) : TranslationAlias(incoming: true);
        foreach (string language in SubtitleSourceChoices)
        {
            string choice = language;
            bool automaticAllowed = language != "Automatic" || !TranslationModelCapabilities.RequiresExplicitSource(alias);
            bool pairAllowed = !TranslationModelCapabilities.IsLfmEnJp(alias) || language is "English" or "Japanese";
            var button = SubtitleSpeechButton(language == "Automatic" ? "Any language" : UiText.T(language), (_, _) =>
            {
                if (!CanEditSubtitleSpeech() || !automaticAllowed || !pairAllowed) return;
                StageSubtitleSourceLanguage(_settings, choice);
                RefreshSaveState(); Navigate(_page);
            });
            button.Name = "SubtitleSourceLanguageChoice"; button.Tag = choice;
            button.IsEnabled = CanEditSubtitleSpeech() && automaticAllowed && pairAllowed;
            button.Background = choice == _settings.IncomingSourceLanguage ? ModernTheme.Selection : ModernTheme.Raised;
            button.BorderBrush = choice == _settings.IncomingSourceLanguage ? ModernTheme.Accent : ModernTheme.Stroke;
            AutomationProperties.SetName(button, SubtitleSpeechText.T("They speak") + ": " + (choice == "Automatic" ? SubtitleSpeechText.T("Any language") : UiText.T(choice)));
            if (!automaticAllowed || !pairAllowed)
                button.ToolTip = UiText.T(TranslationModelCapabilities.IsLfmEnJp(alias) ? "This model supports English and Japanese only."
                    : "This model needs a spoken language. Automatic detection is unavailable.");
            ToolTipService.SetShowOnDisabled(button, true); languages.Children.Add(button);
        }
        body.Children.Add(languages);
        body.Children.Add(SubtitleSpeechLabel("Choose a language when you know it. Any language uses a multilingual recognizer and may mistake short or unclear speech.", 12, ModernTheme.Muted));
        var chineseOnly = HomeSubtitleCheck("Only show Chinese speech", _settings.IncomingChineseOnly, (_, _) =>
        {
            if (!CanEditSubtitleSpeech()) return;
            _settings.IncomingChineseOnly = !_settings.IncomingChineseOnly;
            RefreshSaveState(); Navigate(_page);
        });
        chineseOnly.Name = "SubtitleChineseOnlyToggle";
        body.Children.Add(chineseOnly);
        body.Children.Add(SubtitleSpeechLabel("Skip other languages. Short or mixed-language speech may sometimes be missed.", 12, ModernTheme.Muted));
        body.Children.Add(SubtitleSpeechLabel("Changing only this filter does not restart models.", 12, ModernTheme.Muted));
        if (onHome) AppendHomeSubtitleTargets(body);
        body.Children.Add(SubtitleSpeechLabel("Speech model", 14));
        var models = new System.Windows.Controls.Primitives.UniformGrid { Name = "SubtitleSpeechModelChoices", Columns = 2 };
        foreach (var model in IncomingRecognitionCatalog.Choices.Where(choice => choice.Id != "vibevoice"))
        {
            string id = model.Id;
            var proposed = _settings.Copy(); StageSubtitleSpeechEngine(proposed, id);
            bool compatible = !onHome || TranslationModelCapabilities.Validate(alias, proposed.IncomingSourceLanguage, [proposed.IncomingTargetLanguage]) is null;
            var copy = new StackPanel();
            copy.Children.Add(SubtitleSpeechLabel(model.Name, 15));
            copy.Children.Add(SubtitleSpeechLabel(model.Description, 12, ModernTheme.Muted));
            var button = SubtitleSpeechButton("", (_, _) =>
            {
                if (!CanEditSubtitleSpeech() || !compatible) return;
                StageSubtitleSpeechEngine(_settings, id);
                RefreshSaveState(); Navigate(_page);
            });
            button.Name = "SubtitleSpeechModelChoice"; button.Tag = id; button.Content = copy;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Top;
            button.Padding = new Thickness(12); button.MinHeight = 76;
            button.IsEnabled = CanEditSubtitleSpeech() && compatible;
            if (!compatible) button.ToolTip = SubtitleSpeechText.T("Choose compatible languages or another translator first.");
            ToolTipService.SetShowOnDisabled(button, true);
            button.Background = id == _settings.IncomingSpeechEngine ? ModernTheme.Selection : ModernTheme.Raised;
            button.BorderBrush = id == _settings.IncomingSpeechEngine ? ModernTheme.Accent : ModernTheme.Stroke;
            AutomationProperties.SetName(button, SubtitleSpeechText.T("Speech model") + ": " + SubtitleSpeechText.T(model.Name));
            models.Children.Add(button);
        }
        body.Children.Add(models);
        if (IncomingRecognitionCatalog.UsesFireRed(_settings.IncomingSpeechEngine))
            body.Children.Add(SubtitleSpeechLabel("FireRedASR2 recognizes Chinese and English. The selected language guides translation; FireRed can still recognize English words in Chinese speech. It processes short audio sections rather than updating every word.", 12, ModernTheme.Muted));
        else if (_settings.IncomingSpeechEngine == "voxtral")
            body.Children.Add(SubtitleSpeechLabel("Voxtral detects the language itself. Choose Nemotron or FireRedASR2 to select a spoken language.", 12, ModernTheme.Muted));

        if (onHome)
        {
            AppendHomeSubtitleConfiguration(body);
            return new Border { Name = "HomeSubtitleControls", Child = body };
        }

        AppSettings saved = _preview ? _saveStateAppliedSettings?.Copy() ?? _settings.Copy() : SettingsStore.Load();
        bool changed = !SameSubtitleSpeechSelection(saved, _settings);
        long bytes = PendingSubtitleSpeechBytes(_settings);
        bool missing = SubtitleSpeechFilesMissing(_settings);
        string state = changed ? "Not applied yet" : "Current selection";
        var status = SubtitleSpeechLabel(state, 12, changed ? ModernTheme.Warning : ModernTheme.Good);
        status.Name = "SubtitleSpeechSelectionState"; body.Children.Add(status);
        if (missing)
        {
            var download = ModernTheme.Label(SubtitleSpeechText.Format("Download needed: {0:0.0} GB. Files stay on this PC and are reused.", bytes / 1e9), 12, ModernTheme.Muted, localize: false);
            download.Name = "SubtitleSpeechDownloadSize"; body.Children.Add(download);
        }
        var apply = SubtitleSpeechButton(missing ? "Download & apply" : "Apply subtitle choices", async (_, _) => await ApplySubtitleSpeechSelectionAsync());
        apply.Name = "ApplySubtitleSpeechChoicesButton"; apply.IsEnabled = CanEditSubtitleSpeech() && SupportsSelectedSubtitleSource && (changed || missing);
        apply.Background = apply.IsEnabled ? ModernTheme.Accent : ModernTheme.Raised;
        apply.Foreground = apply.IsEnabled ? ModernTheme.Ink : ModernTheme.Muted; body.Children.Add(apply);
        body.Children.Add(SubtitleSpeechLabel(_runtime.IncomingRunning
            ? "Files download before subtitles briefly pause to apply the change. Your voice translator keeps running."
            : "Apply these choices, then choose Start subtitles on Home. Models load only when subtitles start.", 12, ModernTheme.Muted));
        var card = ModernTheme.Card(body); card.Name = "SubtitleSpeechSettingsCard"; return card;
    }

    private static bool SameSubtitleSpeechSelection(AppSettings left, AppSettings right) =>
        left.IncomingSpeechEngine == right.IncomingSpeechEngine && left.IncomingSourceLanguage == right.IncomingSourceLanguage
        && left.IncomingChineseOnly == right.IncomingChineseOnly;

    internal FrameworkElement CreateHomeSubtitleSpeechSummary()
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var change = SubtitleSpeechButton("Speech model & language", (_, _) => Navigate("Listening"));
        change.Name = "HomeSubtitleSpeechButton"; change.IsEnabled = CanEditSubtitleSpeech();
        DockPanel.SetDock(change, Dock.Right); row.Children.Add(change);
        string language = _settings.IncomingSourceLanguage == "Automatic" ? SubtitleSpeechText.T("Any language") : UiText.T(_settings.IncomingSourceLanguage);
        var label = ModernTheme.Label(language + " · " + SubtitleSpeechText.T(IncomingRecognitionCatalog.Find(_settings.IncomingSpeechEngine).Name), 12, ModernTheme.Muted, localize: false);
        label.Name = "HomeSubtitleSpeechSummary"; label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label); return row;
    }

    private long PendingSubtitleSpeechBytes(AppSettings settings) => IncomingRecognitionCatalog.UsesFireRed(settings.IncomingSpeechEngine)
        ? new FireRedSpeechProvisioner(_provisioner.DataRoot).PendingDownloadBytes(settings.IncomingSpeechEngine)
        : settings.IncomingSpeechEngine == "nemotron"
            ? new NativeSpeechProvisioner(_provisioner.DataRoot).PendingDownloadBytes(IncomingRecognitionCatalog.CreateNativeSettings(settings)) : 0;

    private bool SubtitleSpeechFilesMissing(AppSettings settings) => IncomingRecognitionCatalog.UsesFireRed(settings.IncomingSpeechEngine)
        ? !new FireRedSpeechProvisioner(_provisioner.DataRoot).IsReady(settings.IncomingSpeechEngine)
        : settings.IncomingSpeechEngine == "nemotron" &&
            !new NativeSpeechProvisioner(_provisioner.DataRoot).IsReady(IncomingRecognitionCatalog.CreateNativeSettings(settings));

    private async Task PrepareSubtitleSpeechFilesAsync(AppSettings candidate, CancellationToken token)
    {
        RuntimeProfiles.ValidateIncomingSpeech(candidate);
        if (IncomingRecognitionCatalog.UsesFireRed(candidate.IncomingSpeechEngine))
            await new FireRedSpeechProvisioner(_provisioner.DataRoot).PrepareAsync(candidate.IncomingSpeechEngine, ProvisionProgress(), token);
        else if (candidate.IncomingSpeechEngine == "nemotron")
            await new NativeSpeechProvisioner(_provisioner.DataRoot).PrepareAsync(IncomingRecognitionCatalog.CreateNativeSettings(candidate), ProvisionProgress(), token);
    }

    private async Task ApplySubtitleSpeechSelectionAsync()
    {
        if (_preview || !CanEditSubtitleSpeech()) return;
        AppSettings saved = SettingsStore.Load(), draft = _settings.Copy();
        try { IncomingCaptureCapabilities.Validate(SubtitleSpeechCandidate(saved, draft), SubtitleWindowsVersion); }
        catch (IncomingListeningSelectionException error) { Error("Apply subtitle choices", error); return; }
        await ApplyTranslationSettingsAsync(SubtitleSpeechCandidate(saved, draft), applyingProfile: false);
        AppSettings latest = SettingsStore.Load();
        // Keep unrelated edits staged elsewhere; this action applies only the
        // incoming recognition selection, never an unsaved microphone change.
        TranslationProfiles.CopyInto(SettingsSaveState.Rebase(draft, saved, latest), _settings);
        RefreshSaveState(reloadSaved: true);
        if (!_closing) Navigate(_page);
    }

    private static TextBlock SubtitleSpeechLabel(string english, double size, Brush? foreground = null) =>
        UiLocalization.Literal(ModernTheme.Label(SubtitleSpeechText.T(english), size, foreground, localize: false));

    private static Button SubtitleSpeechButton(string english, RoutedEventHandler click)
    {
        var button = ModernTheme.Button(SubtitleSpeechText.T(english), click);
        UiLocalization.Literal(button); button.MinHeight = 32; button.Padding = new Thickness(10, 6, 10, 6);
        AutomationProperties.SetName(button, SubtitleSpeechText.T(english)); return button;
    }
}

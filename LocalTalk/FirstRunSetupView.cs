using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IncomingTranslator;

// A separate first-run surface. Its host owns persistence, logging and setup permissions;
// this view owns only a draft and explicit, cancellable navigation. No runtime is started.
internal sealed class FirstRunSetupView : Grid, IDisposable
{
    internal const string SmallModelId = "hy-mt2-1.8b-q4_k_m";
    internal const string LargeModelId = "hy-mt2-7b-q4_k_m";
    private const int StepCount = 4;
    private readonly Func<CancellationToken, Task<string?>> _recommendModel;
    private readonly Func<AppSettings, CancellationToken, Task<AutomaticSetupPlan>> _inspectPlan;
    private readonly Func<AppSettings, AutomaticSetupPlan, IProgress<ProvisionProgress>, CancellationToken, Task<AutomaticSetupResult>> _prepare;
    private readonly Action<AppSettings> _complete;
    private readonly Action _exit;
    private readonly Action<AppSettings> _appearanceChanged;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _elapsedTimer;
    private CancellationTokenSource? _work;
    private Task _currentOperation = Task.CompletedTask;
    private int _step;
    private bool _disposed, _recommendationAttempted, _recommendationAvailable, _memoryCaution, _confirmChoice, _prepared, _needsRestart, _paused, _completionInvoked;
    private string _selectedModel, _recommendedModel = SmallModelId;
    private bool _modelChosen, _modelChoiceAccepted;
    private string? _error;
    private AutomaticSetupPlan? _plan;
    private ProvisionProgress? _progressValue;
    private DateTimeOffset _startedAt, _lastProgressAt;
    private TextBlock? _progressTitle, _progressDetail, _progressTiming;
    private ProgressBar? _progressBar;

    public AppSettings Draft { get; }
    public bool IsBusy { get; private set; }
    internal event Action? BusyChanged;
    internal int StepIndex => _step;
    internal string RecommendedModelId => _recommendedModel;
    internal Task CurrentOperation => _currentOperation;

    internal FirstRunSetupView(AppSettings draft,
        Func<CancellationToken, Task<string?>> recommendModel,
        Func<AppSettings, CancellationToken, Task<AutomaticSetupPlan>> inspectPlan,
        Func<AppSettings, AutomaticSetupPlan, IProgress<ProvisionProgress>, CancellationToken, Task<AutomaticSetupResult>> prepare,
        Action<AppSettings> complete, Action exit, Action<AppSettings> appearanceChanged)
    {
        Draft = draft.Copy();
        Draft.UiLanguage = UiText.NormalizeLanguage(Draft.UiLanguage);
        _recommendModel = recommendModel; _inspectPlan = inspectPlan; _prepare = prepare;
        _complete = complete; _exit = exit; _appearanceChanged = appearanceChanged;
        _selectedModel = IsBasicModel(Draft.OwnManagedModelId) ? Draft.OwnManagedModelId : SmallModelId;
        _modelChosen = Draft.FirstRunSetupCompleted;
        Name = "FirstRunSetup"; Background = ModernTheme.Background;
        _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateProgress(), Dispatcher);
        _elapsedTimer.Stop();
        Render();
    }

    public void RefreshLanguage()
    {
        if (_disposed) return;
        Render();
    }

    public void Cancel()
    {
        if (_disposed || !IsBusy) return;
        _work?.Cancel();
        if (_step == 2 && _progressTitle is not null)
            _progressTitle.Text = UiText.T("Canceling preparation…");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _work?.Cancel(); _elapsedTimer.Stop();
        // The active operation disposes its own linked source after the backend has exited.
        _lifetime.Dispose();
    }

    internal Task AdvanceAsync()
    {
        if (_disposed || IsBusy || _completionInvoked) return Task.CompletedTask;
        _error = null; _paused = false;
        if (_step == 0) { _step++; Render(); return Task.CompletedTask; }
        if (_step == 1)
        {
            _step = 2; Render();
            return _recommendationAttempted ? Task.CompletedTask : BeginOperationAsync(RecommendAsync);
        }
        if (_step == 2)
        {
            if (!_recommendationAttempted) return BeginOperationAsync(RecommendAsync);
            if (_recommendationAvailable && _selectedModel != _recommendedModel && !_modelChoiceAccepted)
            { _confirmChoice = true; Render(); return Task.CompletedTask; }
            ApplyModelSelection();
            return BeginOperationAsync(_plan is null ? InspectAsync : PrepareAsync);
        }
        if (_step == 3 && _prepared)
        {
            _completionInvoked = true;
            try { _complete(Draft.Copy()); }
            catch (Exception error)
            {
                _completionInvoked = false; _error = UserFacingErrors.Describe(error); Render();
            }
        }
        return Task.CompletedTask;
    }

    private Task BeginOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        _currentOperation = PerformOperationAsync(action);
        return _currentOperation;
    }

    private async Task PerformOperationAsync(Func<CancellationToken, Task> action)
    {
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _work = work; IsBusy = true; BusyChanged?.Invoke(); Render();
        try { await action(work.Token); }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        { _paused = true; _needsRestart = false; }
        catch (Exception error) { _error = UserFacingErrors.Describe(error); }
        finally
        {
            _elapsedTimer.Stop(); _work = null; IsBusy = false; BusyChanged?.Invoke();
            if (!_disposed) Render();
        }
    }

    private async Task RecommendAsync(CancellationToken token)
    {
        try
        {
            string? recommendation = await _recommendModel(token);
            token.ThrowIfCancellationRequested();
            _recommendationAvailable = IsBasicModel(recommendation);
            _memoryCaution = recommendation is null;
            _recommendedModel = IsBasicModel(recommendation) ? recommendation! : SmallModelId;
            if (!_modelChosen) _selectedModel = _recommendedModel;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { _error = "Couldn't check the hardware. You can still choose a translator."; }
        _recommendationAttempted = true;
    }

    private async Task InspectAsync(CancellationToken token)
    {
        _plan = await _inspectPlan(Draft, token);
        token.ThrowIfCancellationRequested();
    }

    private async Task PrepareAsync(CancellationToken token)
    {
        AutomaticSetupPlan plan = _plan ?? throw new InvalidOperationException("Preparation was not inspected.");
        _startedAt = _lastProgressAt = DateTimeOffset.UtcNow;
        _progressValue = new("preparing", "Preparing translation…");
        Render(); _elapsedTimer.Start();
        var progress = new Progress<ProvisionProgress>(value =>
        {
            if (_disposed || !IsBusy || token.IsCancellationRequested) return;
            _progressValue = value; _lastProgressAt = DateTimeOffset.UtcNow; UpdateProgress();
        });
        AutomaticSetupResult result = await _prepare(Draft, plan, progress, token);
        token.ThrowIfCancellationRequested();
        _needsRestart = result.NeedsRestart; _prepared = result.Completed;
        if (_prepared) _step = 3;
        else if (!_needsRestart) _error = string.IsNullOrWhiteSpace(result.Message)
            ? "Couldn't finish this step. Try again or open Troubleshooting for details." : result.Message;
    }

    private void ApplyModelSelection()
    {
        Draft.UseManagedTranslator = true;
        if (!Draft.FirstRunSetupCompleted) Draft.ShareTranslationModel = true;
        Draft.OwnManagedModelId = _selectedModel;
        if (Draft.ShareTranslationModel) Draft.IncomingManagedModelId = _selectedModel;
        if (!LocalTalkProvisioner.RuntimeCatalog.Any(runtime => runtime.Id == Draft.ManagedRuntimeId))
            Draft.ManagedRuntimeId = "llama-vulkan";
        // The chosen spoken language is also passed to recognition. Model selection
        // must not silently replace it, or any optional subtitles preferences.
        string alias = _selectedModel == LargeModelId ? "tencent/hy-mt2-7b" : "tencent/hy-mt2-1.8b";
        Draft.OwnTranslatorModel = alias;
        if (Draft.ShareTranslationModel) Draft.TranslatorModel = alias;
    }

    private void GoBack()
    {
        if (_disposed || IsBusy || _step <= 0 || _prepared) return;
        _step--; _error = null; _paused = false; _confirmChoice = false; _modelChoiceAccepted = false; _needsRestart = false;
        if (_step < 2) { _plan = null; _progressValue = null; }
        Render();
    }

    private void Render()
    {
        if (_disposed) return;
        Children.Clear(); _progressTitle = _progressDetail = _progressTiming = null; _progressBar = null;
        Children.Add(ThemeBackground.CreateLayer());
        var frame = new Grid { Margin = new Thickness(36, 25, 36, 20), MaxWidth = 900 };
        frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(frame);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = BrandArtwork.Lockup(155); brand.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(brand);
        var step = ModernTheme.Label("", 12, ModernTheme.Muted); step.Margin = new Thickness(18, 0, 0, 0);
        UiLocalization.SetFormat(step, "Step {0} of {1}", _step + 1, StepCount);
        step.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(step, 1); header.Children.Add(step);
        var rail = new Grid { Height = 3, Margin = new Thickness(0, 19, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        for (int i = 0; i < StepCount; i++)
        {
            rail.ColumnDefinitions.Add(new());
            var segment = new Border { CornerRadius = new(2), Background = i <= _step ? ModernTheme.Accent : ModernTheme.Raised,
                Margin = new Thickness(i == 0 ? 0 : 3, 0, i == StepCount - 1 ? 0 : 3, 0) };
            Grid.SetColumn(segment, i); rail.Children.Add(segment);
        }
        var headStack = new StackPanel(); headStack.Children.Add(header); headStack.Children.Add(rail); frame.Children.Add(headStack);

        var body = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        string title = _step switch
        {
            0 => "Welcome to SayWhat?", 1 => "Your voice",
            2 => _needsRestart ? "Restart Windows to continue" : "Prepare your translator", _ => "Your voice translator is ready"
        };
        body.Children.Add(ModernTheme.Heading(title, 32));
        switch (_step)
        {
            case 0: LanguageStep(body); break;
            case 1: TranslationStep(body); break;
            case 2:
                if (!_needsRestart && _progressValue is null && (!IsBusy || !_recommendationAttempted)) ModelStep(body);
                if (_recommendationAttempted && !_confirmChoice) PreparationStep(body);
                break;
            case 3:
                body.Children.Add(ModernTheme.Label("Choose Start on Home to send your translated voice to VRChat chat.", 15, ModernTheme.Muted));
                body.Children.Add(Summary());
                body.Children.Add(ModernTheme.Label("Live subtitles and People are optional. You can set them up later.", 14, ModernTheme.Muted));
                break;
        }
        if (_error is not null) body.Children.Add(Notice(_error, ModernTheme.Error));
        if (_paused) body.Children.Add(Notice("Setup paused. Downloaded files are kept for the next attempt.", ModernTheme.Muted));
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body, Padding = new Thickness(0, 0, 5, 0) };
        Grid.SetRow(scroll, 1); frame.Children.Add(scroll);
        var footer = Footer(); Grid.SetRow(footer, 2); frame.Children.Add(footer);
        UiLocalization.ApplyTree(this);
        if (SystemParameters.ClientAreaAnimation && IsVisible)
        {
            body.BeginAnimation(OpacityProperty, new DoubleAnimation(.25, 1, TimeSpan.FromMilliseconds(180)));
            var offset = new TranslateTransform(); body.RenderTransform = offset;
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        UpdateProgress();
    }

    private void LanguageStep(StackPanel body)
    {
        body.Children.Add(ModernTheme.Label("Choose the language for the app.", 15, ModernTheme.Muted));
        var choices = new StackPanel();
        foreach (UiLanguageChoice language in UiText.Choices)
        {
            var item = new RadioButton { Content = language.Label, IsChecked = Draft.UiLanguage == language.Code,
                Margin = new Thickness(0, 7, 0, 7), Padding = new Thickness(8, 9, 8, 9), FontSize = 17, GroupName = "FirstRunLanguage" };
            UiLocalization.Literal(item); AutomationProperties.SetName(item, language.Label);
            item.Checked += (_, _) =>
            {
                Draft.UiLanguage = language.Code;
                PreviewAppearance();
            };
            choices.Children.Add(item);
        }
        body.Children.Add(ModernTheme.Card(choices));
        var appearance = new StackPanel(); ThemeStep(appearance);
        var optionalTheme = new Expander { Header = UiText.T("Change appearance"), Content = appearance,
            IsExpanded = false, Margin = new Thickness(0, 12, 0, 0) };
        UiLocalization.Bind(optionalTheme, HeaderedContentControl.HeaderProperty, "Change appearance");
        body.Children.Add(optionalTheme);
    }

    private void ThemeStep(StackPanel body)
    {
        body.Children.Add(ModernTheme.Label("Choose a theme. You can change it later in Settings.", 15, ModernTheme.Muted));
        var choices = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (ThemePreset theme in AppearanceTheme.Presets.Where(value => value.Id != "custom" || Draft.UiThemeId == "custom"))
        {
            var content = new StackPanel();
            content.Children.Add(ModernTheme.Label(theme.Name, 15));
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 7) };
            foreach (string color in ThemeSwatches(theme.Id)) swatches.Children.Add(new Ellipse
                { Width = 16, Height = 16, Fill = ModernTheme.Brush(color), Margin = new Thickness(0, 0, 6, 0) });
            content.Children.Add(swatches);
            var button = new RadioButton { Content = content, IsChecked = Draft.UiThemeId == theme.Id,
                GroupName = "FirstRunTheme", Margin = new Thickness(0), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            UiLocalization.Bind(button, AutomationProperties.NameProperty, theme.Name);
            var tile = new Border { Name = "SetupThemeTile_" + theme.Id.Replace('-', '_'), Width = 190,
                CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 12), Padding = new Thickness(12),
                Background = ModernTheme.Surface, BorderBrush = Draft.UiThemeId == theme.Id ? ModernTheme.Accent : ModernTheme.Stroke,
                BorderThickness = new Thickness(2), Child = button, Cursor = System.Windows.Input.Cursors.Hand };
            tile.MouseLeftButtonUp += (_, args) =>
            {
                if (button.IsChecked != true) { button.Focus(); button.IsChecked = true; }
                args.Handled = true;
            };
            button.Checked += (_, _) =>
            {
                Draft.UiThemeId = theme.Id; PreviewAppearance();
            };
            choices.Children.Add(tile);
        }
        body.Children.Add(choices);
    }

    private void PreviewAppearance()
    {
        _error = null;
        try { _appearanceChanged(Draft.Copy()); }
        catch (Exception error) { _error = UserFacingErrors.Describe(error); }
        // A preference-write failure must not turn a radio-button click into a crash.
        // Preview remains usable; the host retries saving on explicit preparation.
        AppearanceTheme.Apply(Draft); ThemeBackground.Apply(Draft); UiText.Language = Draft.UiLanguage;
        RefreshLanguage();
    }

    private void TranslationStep(StackPanel body)
    {
        body.Children.Add(ModernTheme.Label("Choose what you speak and what appears in VRChat chat.", 15, ModernTheme.Muted));
        var source = new StackPanel(); source.Children.Add(ModernTheme.Label("I speak", 17));
        var sourceChoices = new WrapPanel();
        foreach (string language in new[] { "English", "Chinese", "Japanese", "Korean", "Automatic", "Spanish", "French", "German", "Portuguese", "Russian" })
        {
            var choice = new RadioButton { Content = UiText.T(language), GroupName = "SetupSpokenLanguage",
                IsChecked = Draft.OwnVoiceSourceLanguage == language, Margin = new Thickness(0, 7, 14, 7), Padding = new Thickness(8), FontSize = 15 };
            UiLocalization.Bind(choice, ContentControl.ContentProperty, language);
            AutomationProperties.SetName(choice, UiText.T("I speak") + ": " + UiText.T(language));
            choice.Checked += (_, _) => { Draft.OwnVoiceSourceLanguage = language; _plan = null; };
            sourceChoices.Children.Add(choice);
        }
        source.Children.Add(sourceChoices);
        source.Children.Add(ModernTheme.Label("Choosing your language can improve speech recognition. Choose Automatic if you regularly switch languages.", 13, ModernTheme.Muted));
        body.Children.Add(ModernTheme.Card(source));
        var targets = new StackPanel(); targets.Children.Add(ModernTheme.Label("Translate my voice to", 17));
        var targetChoices = new WrapPanel();
        var selected = new List<string> { AppSettings.CanonicalTranslationLanguage(Draft.OwnVoiceTargetLanguage) ?? "Chinese" };
        if (Draft.OwnVoiceAdditionalLanguagesEnabled) selected.AddRange(Draft.NormalizedOwnVoiceAdditionalLanguages());
        foreach (string language in AppSettings.TranslationLanguages)
        {
            var choice = new CheckBox { Content = UiText.T(language), IsChecked = selected.Contains(language),
                Margin = new Thickness(0, 7, 14, 7), Padding = new Thickness(8), FontSize = 15 };
            UiLocalization.Bind(choice, ContentControl.ContentProperty, language);
            AutomationProperties.SetName(choice, UiText.T("Translate my voice to") + ": " + UiText.T(language));
            choice.Click += (_, _) =>
            {
                if (choice.IsChecked == true)
                {
                    if (selected.Count >= 4) { choice.IsChecked = false; return; }
                    if (!selected.Contains(language)) selected.Add(language);
                }
                else if (selected.Count <= 1) { choice.IsChecked = true; return; }
                else selected.Remove(language);
                Draft.OwnVoiceTargetLanguage = selected[0];
                Draft.OwnVoiceAdditionalTargetLanguages = selected.Skip(1).ToList();
                Draft.OwnVoiceAdditionalLanguagesEnabled = selected.Count > 1;
                _plan = null;
            };
            targetChoices.Children.Add(choice);
        }
        targets.Children.Add(targetChoices);
        targets.Children.Add(ModernTheme.Label("Choose up to four languages. More languages share the same short VRChat message, so longer translations may be shortened.", 13, ModernTheme.Muted));
        body.Children.Add(ModernTheme.Card(targets));
    }

    private void ModelStep(StackPanel body)
    {
        body.Children.Add(ModernTheme.Label("Your PC is checked locally to suggest a starting model. Hardware details are not sent anywhere.", 14, ModernTheme.Muted));
        if (IsBusy) { body.Children.Add(Activity("Checking which translator suits this PC…")); return; }
        if (_confirmChoice)
        {
            var confirmation = new StackPanel(); confirmation.Children.Add(ModernTheme.Label("Use a different translator?", 17));
            var advice = ModernTheme.Label("", 14, ModernTheme.Muted);
            UiLocalization.SetFormat(advice, "We recommend starting with {0}. You can add and try other models later.", ModelName(_recommendedModel));
            confirmation.Children.Add(advice);
            var actions = new WrapPanel();
            actions.Children.Add(ModernTheme.Button("Start with recommended", async (_, _) =>
            { _selectedModel = _recommendedModel; _modelChoiceAccepted = true; _confirmChoice = false; await AdvanceAsync(); }, true));
            actions.Children.Add(ModernTheme.Button("Use my choice", async (_, _) =>
            { _modelChoiceAccepted = true; _confirmChoice = false; await AdvanceAsync(); }));
            confirmation.Children.Add(actions); body.Children.Add(ModernTheme.Card(confirmation));
            return;
        }
        var options = new UniformGrid { Columns = 2 };
        foreach (string id in new[] { SmallModelId, LargeModelId })
        {
            var labels = new StackPanel();
            labels.Children.Add(ModernTheme.Label(ModelName(id), 20, localize: false));
            labels.Children.Add(ModernTheme.Label(id == SmallModelId ? "Fast · smaller download" : "Larger model · larger download", 14, ModernTheme.Muted));
            if (_recommendationAvailable && id == _recommendedModel)
                labels.Children.Add(ModernTheme.Chip("Recommended for this PC", ModernTheme.Good));
            var choice = new RadioButton { Content = labels, IsChecked = id == _selectedModel, GroupName = "FirstRunModel",
                Margin = new Thickness(0, 6, 0, 8), Padding = new Thickness(12), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice, ModelName(id));
            choice.Checked += (_, _) => { _selectedModel = id; _modelChosen = true; _modelChoiceAccepted = false; _confirmChoice = false; _plan = null; _error = null; Render(); };
            Border card = ModernTheme.Card(choice); card.Padding = new Thickness(14);
            card.Margin = new Thickness(id == LargeModelId ? 7 : 0, 0, id == SmallModelId ? 7 : 0, 16);
            options.Children.Add(card);
        }
        body.Children.Add(options);
        body.Children.Add(ModernTheme.Label("You can switch models from Home later. A larger model may improve wording but is not always faster.", 13, ModernTheme.Muted));
        body.Children.Add(ModernTheme.Heading("Speech recognition", 20));
        var recognition = new StackPanel();
        foreach (var engine in OwnRecognitionCatalog.Choices)
        {
            var labels = new StackPanel(); labels.Children.Add(ModernTheme.Label(engine.Name, 16, localize: false));
            labels.Children.Add(ModernTheme.Label(engine.Description, 13, ModernTheme.Muted));
            var choice = new RadioButton { Content = labels, GroupName = "SetupSpeechRecognition",
                IsChecked = Draft.OwnRecognitionEngine == engine.Id, Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice, engine.Name);
            choice.Checked += (_, _) =>
            {
                Draft.OwnRecognitionEngine = engine.Id;
                if (!engine.SupportsExplicitLanguage) Draft.OwnVoiceSourceLanguage = "Automatic";
                _plan = null; _error = null; Render();
            };
            recognition.Children.Add(choice);
        }
        body.Children.Add(ModernTheme.Card(recognition));
        if (_memoryCaution)
            body.Children.Add(Notice("Memory is limited or could not be checked. Start with the smaller translator and close other heavy apps if needed.", ModernTheme.Warning));
    }

    private void PreparationStep(StackPanel body)
    {
        if (_needsRestart)
        {
            body.Children.Add(Notice("Restart Windows, then reopen SayWhat? to continue setup.", ModernTheme.Warning));
            return;
        }
        if (IsBusy && _progressValue is not null)
        {
            var progress = new StackPanel();
            _progressTitle = ModernTheme.Label("Preparing translation…", 17);
            _progressDetail = ModernTheme.Label("", 14, ModernTheme.Muted);
            _progressTiming = ModernTheme.Label("", 12, ModernTheme.Muted);
            _progressBar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 1, IsIndeterminate = true,
                Foreground = ModernTheme.Accent, Background = ModernTheme.Raised, BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 14, 0, 18) };
            progress.Children.Add(_progressTitle); progress.Children.Add(new Border
                { CornerRadius = new(4), ClipToBounds = true, Child = _progressBar });
            progress.Children.Add(_progressDetail); progress.Children.Add(_progressTiming);
            body.Children.Add(ModernTheme.Card(progress));
            return;
        }
        if (_plan is null)
        {
            if (IsBusy) body.Children.Add(Activity("Checking what needs to be prepared…"));
            return;
        }
        body.Children.Add(Summary());
        if (_plan.DownloadBytes > 0)
        {
            var bytes = ModernTheme.Label("", 16);
            UiLocalization.SetFormat(bytes, "Models and translation tools: about {0}", Bytes(_plan.DownloadBytes)); body.Children.Add(bytes);
        }
        body.Children.Add(ModernTheme.Label("Files already on this PC will be reused.", 14, ModernTheme.Muted));
        if (_plan.SpeechSetupRequired && Draft.OwnRecognitionEngine == "voxtral")
            body.Children.Add(ModernTheme.Label("Preparing speech recognition may need extra downloads and at least 20 GB of free space.", 14, ModernTheme.Muted));
        if (_plan.RequiresAdministrator)
            body.Children.Add(ModernTheme.Label("Windows may ask for permission to prepare speech recognition.", 14, ModernTheme.Muted));
        if (_plan.RestartMayBeNeeded)
            body.Children.Add(ModernTheme.Label("A Windows restart may be needed. You can continue setup afterward.", 14, ModernTheme.Muted));
        body.Children.Add(ModernTheme.Label("No microphone is opened and no AI model is loaded during setup.", 13, ModernTheme.Muted));
    }

    private Border Summary()
    {
        var list = new StackPanel();
        var own = ModernTheme.Label("", 14); UiLocalization.SetFormat(own, "My voice → {0}", new UiLocalization.Phrase(Draft.OwnVoiceTargetLanguage));
        list.Children.Add(own);
        foreach (string target in Draft.EffectiveOwnVoiceTargetLanguages().Skip(1))
            list.Children.Add(ModernTheme.Label(UiText.T(target), 14, localize: false));
        var source = ModernTheme.Label("", 14); UiLocalization.SetFormat(source, "I speak: {0}", new UiLocalization.Phrase(Draft.OwnVoiceSourceLanguage));
        list.Children.Insert(0, source);
        list.Children.Add(ModernTheme.Label(ModelName(_selectedModel), 14, ModernTheme.Muted, localize: false));
        return ModernTheme.Card(list);
    }

    private FrameworkElement Footer()
    {
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var exit = ModernTheme.Button("Exit setup", (_, _) => { if (!IsBusy) _exit(); });
        exit.IsEnabled = !IsBusy; footer.Children.Add(exit);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 2); footer.Children.Add(actions);
        if (_step > 0 && !_prepared)
        {
            var back = ModernTheme.Button("Back", (_, _) => GoBack()); back.IsEnabled = !IsBusy; actions.Children.Add(back);
        }
        if (IsBusy)
        {
            actions.Children.Add(ModernTheme.Button(_step == 2 && _progressValue is not null ? "Cancel preparation" : "Cancel", (_, _) => Cancel()));
        }
        else
        {
            string next = _step == 3 ? "Open Home" : _step == 2 ? _needsRestart ? "Check again" : _error is not null ? "Try again" : _plan is null ? "Next" : "Prepare translation" : "Next";
            var button = ModernTheme.Button(next, async (_, _) => await AdvanceAsync(), true);
            button.Name = "FirstRunNext"; button.MinWidth = 150; button.Margin = new Thickness(0, 6, 0, 6);
            button.IsEnabled = !_completionInvoked && !(_step == 2 && _confirmChoice);
            actions.Children.Add(button);
        }
        return footer;
    }

    private void UpdateProgress()
    {
        if (_disposed || _progressValue is null || _progressTitle is null || _progressBar is null) return;
        if (_work?.IsCancellationRequested == true)
        { _progressTitle.Text = UiText.T("Canceling preparation…"); return; }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SetupProgressText presentation = SetupProgressPresentation.Format(_progressValue, now - _startedAt, now - _lastProgressAt);
        _progressTitle.Text = presentation.Title; _progressDetail!.Text = presentation.Detail; _progressTiming!.Text = presentation.Timing;
        UiLocalization.Literal(_progressTitle); UiLocalization.Literal(_progressDetail); UiLocalization.Literal(_progressTiming);
        _progressBar.IsIndeterminate = presentation.Fraction is null && SystemParameters.ClientAreaAnimation;
        _progressBar.Value = presentation.Fraction ?? 0;
        AutomationProperties.SetName(_progressBar, presentation.Title);
    }

    private static FrameworkElement Activity(string text)
    {
        var row = new Grid { Margin = new Thickness(0, 18, 0, 18) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new());
        var ring = new Ellipse { Width = 24, Height = 24, Stroke = ModernTheme.Accent, StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection { 4, 2 }, RenderTransformOrigin = new Point(.5, .5), Margin = new Thickness(0, 0, 14, 0) };
        if (SystemParameters.ClientAreaAnimation)
        {
            var rotation = new RotateTransform(); ring.RenderTransform = rotation;
            rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2))
                { RepeatBehavior = RepeatBehavior.Forever });
        }
        var label = ModernTheme.Label(text, 15, ModernTheme.Muted); label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1); row.Children.Add(ring); row.Children.Add(label); return row;
    }

    private static Border Notice(string text, Brush brush) => new()
    {
        Background = ModernTheme.Raised, CornerRadius = new(10), Padding = new Thickness(14), Margin = new Thickness(0, 8, 0, 12),
        Child = ModernTheme.Label(text, 14, brush)
    };

    private static bool IsBasicModel(string? id) => id is SmallModelId or LargeModelId;
    private static string ModelName(string id) => id == LargeModelId ? "HY-MT2 7B" : "HY-MT2 1.8B";
    private static string Bytes(long value) => value >= 1_000_000_000 ? $"{value / 1e9:0.0} GB" : $"{value / 1e6:0.0} MB";
    private static string[] ThemeSwatches(string id) => id switch
    {
        "ink" => ["#101419", "#A6C8F0", "#ECEFF5"], "forest" => ["#141B17", "#C3DB88", "#F0F1E4"],
        "plum" => ["#211821", "#E9A2C5", "#F9EEF6"], "paper" => ["#F7F6F2", "#276B79", "#222A2C"],
        "sand" => ["#F2EBDF", "#963D2C", "#342A24"], "mint" => ["#EAF4EF", "#276554", "#20392E"],
        "high-contrast" => ["#000000", "#FFE666", "#FFFFFF"],
        "custom" => ["#81746A", "#BBB0A6", "#F6EFE7"], _ => ["#191817", "#F28C78", "#F6EFE7"]
    };
}

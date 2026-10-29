using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace IncomingTranslator;

internal sealed partial class MainWindow : Window
{
    internal static readonly string[] Pages = ["Home", "Languages", "Speakers", "Settings", "Listening", "Themes", "Setup", "Help", "Advanced", "Translation", "Audio & timing", "Models", "Performance", "Diagnostics", "Manual setup", "Speaker options"];
    internal static readonly string[] PrimaryPages = ["Home", "Languages", "Speakers", "Settings", "Setup", "Help", "Advanced"];
    private static readonly string[] Languages = AppSettings.TranslationLanguages;
    private readonly LocalTalkProvisioner _provisioner = new(AppPaths.Root);
    private readonly RuntimeController _runtime;
    private readonly DesktopLifetime _desktopLifetime;
    private readonly AppSettings _settings;
    private SetupSelection _savedSetupSelection;
    private readonly bool _preview;
    private readonly ContentControl _content = new();
    private readonly ScrollViewer _navigationScroll = new();
    private readonly Button _saveButton;
    private readonly Button _stopButton;
    private readonly Button _backButton;
    private readonly TextBlock _pageTitle = ModernTheme.Heading("Home", 32);
    private readonly TextBlock _pageEyebrow = ModernTheme.Kicker("TRANSLATION");
    private readonly TextBlock _status = ModernTheme.Label("Not translating", 14, ModernTheme.Muted);
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Foreground = ModernTheme.Accent,
        Background = ModernTheme.Raised, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _progressDetail = ModernTheme.Label("", 12, ModernTheme.Text);
    private readonly TextBlock _progressTiming = ModernTheme.Label("", 12, ModernTheme.Muted);
    private ProvisionProgress? _currentProgress;
    private DateTimeOffset _operationStartedAt, _lastProgressAt;
    private readonly Button _cancel;
    private readonly Button _taskTroubleshooting;
    private readonly Dictionary<string, Button> _navigation = new();
    private readonly Dictionary<string, TextBlock> _badges = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationFinished;
    private bool _busy, _closing, _finishingSafeShutdown;
    private int _taskGeneration;
    private bool _startupRequested;
    private bool _introductionShown;
    private IReadOnlyList<SpeakerPerson> _savedPeople = Array.Empty<SpeakerPerson>();
    private PeopleListView? _peopleView;
    private bool _savedPeopleLoaded, _savedPeopleLoading;
    private DateTimeOffset _lastPeopleRefreshAt;
    private string? _savedPeopleError;
    private string _setupTranslationModelId = "hy-mt2-1.8b-q4_k_m";
    private string _page = "Home";
    private string? _setupMessage;
    private LocalHardwareProfile? _hardware;
    private string? _recommendedModelId;
    private SeparatedAudio? _lastSeparation;
    private string _performanceReport = "Choose Check hardware to inspect memory and processors. This does not start any model or benchmark.";
    private TextBox? _logView;
    private TextBlock? _speakerRecognitionState;
    private FrameworkElement? _speakerRecognitionSettingsSection;
    private Button? _setupActionButton;
    private StackPanel? _setupChoicesPanel;
    private TextBlock? _setupChoiceStatus;
    private TextBlock? _setupActionHint;
    private ComboBox? _interfaceLanguagePicker;
    private bool _changingInterfaceLanguage;
    private string _logName = "saywhat.log";
    private readonly System.Windows.Threading.DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Threading.DispatcherTimer _taskTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Threading.DispatcherTimer _oscTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private VrchatOscConnectionProbe? _oscProbe;
    private VrchatOscConnectionStatus _oscConnection = new(VrchatOscConnectionState.Checking);
    private readonly TextBlock _oscConnectionText = ModernTheme.Label("Checking VRChat…", 11, ModernTheme.Muted);
    private readonly System.Windows.Shapes.Path _oscConnectionIcon = (System.Windows.Shapes.Path)ModernTheme.Icon("Connection");
    private Button? _oscConnectionButton;
    private TextBlock? _oscHelpText;
    private Expander? _oscHelp;
    private bool _checkingOsc;
    private readonly Border _updateBanner = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _updateBannerText = ModernTheme.Label("", 14);
    private AppUpdateInfo? _availableUpdate;
    private bool _startupUpdatesChecked;

    public MainWindow(bool preview = false, LocalTalkLaunchOptions? launchOptions = null)
    {
        _preview = preview;
        _settings = SettingsStore.Load();
        _savedSetupSelection = SetupSelection.Capture(_settings);
        AppearanceTheme.Apply(_settings);
        bool artworkAvailable=ThemeBackground.Apply(_settings);
        UiText.Language = _settings.UiLanguage;
        if (_settings.OwnManagedModelId is "hy-mt2-1.8b-q4_k_m" or "hy-mt2-7b-q4_k_m")
            _setupTranslationModelId = _settings.OwnManagedModelId;
        _runtime = new RuntimeController(_provisioner);
        BrandArtwork.ConfigureSubtitleBranding();
        Title = AppBrand.DisplayName + " — " + UiText.T("VRChat translation");
        Icon = BrandArtwork.WindowIcon;
        Width = 1160; Height = 820; MinWidth = 900; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = ModernTheme.Background; Foreground = ModernTheme.Text;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic"); Topmost = false;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        SourceInitialized += (_,_) => AppearanceTheme.ApplyWindowChrome(this);
        var frame = _dashboardFrame = new Grid { Background = ModernTheme.Background };
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
        frame.ColumnDefinitions.Add(new ColumnDefinition());
        var artwork=ThemeBackground.CreateLayer();Grid.SetColumnSpan(artwork,2);frame.Children.Add(artwork);
        frame.Children.Add(BuildSidebar());
        var body = new Grid { Margin = new Thickness(34, 28, 30, 16) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20),Background=ModernTheme.Background };
        var actions=new WrapPanel();
        _stopButton=ModernTheme.Button("Stop all",async (_,_)=>await EmergencyStopAsync());
        actions.Children.Add(_stopButton);
        _saveButton = ModernTheme.Button("Save & apply", async (_, _) => await SaveAsync(), true);
        InitializeSaveStateTracking();
        _saveButton.ToolTip="Apply your changes. Active translation restarts with the new settings.";
        actions.Children.Add(_saveButton); actions.VerticalAlignment=VerticalAlignment.Center; DockPanel.SetDock(actions,Dock.Right); header.Children.Add(actions);
        var title = new StackPanel();
        _backButton=ModernTheme.Button("← Back to Advanced", (_,_)=>Navigate(_page is "Themes" or "Listening"?"Settings":"Advanced"));
        _backButton.Padding=new Thickness(0,4,10,4); _backButton.MinHeight=28; _backButton.Background=Brushes.Transparent; _backButton.BorderThickness=new Thickness(0); _backButton.Foreground=ModernTheme.Accent;
        title.Children.Add(_backButton);title.Children.Add(_pageEyebrow);title.Children.Add(_pageTitle);header.Children.Add(title);
        var headerArea = new StackPanel(); headerArea.Children.Add(header);
        var updateContent = new DockPanel();
        var updateActions = new WrapPanel();
        updateActions.Children.Add(ModernTheme.Button("Update now", async (_, _) => await InstallAvailableUpdateAsync(), true));
        updateActions.Children.Add(ModernTheme.Button("Later", (_, _) => _updateBanner.Visibility = Visibility.Collapsed));
        DockPanel.SetDock(updateActions, Dock.Right); updateContent.Children.Add(updateActions);
        var updateCopy = new StackPanel(); updateCopy.Children.Add(_updateBannerText);
        updateCopy.Children.Add(ModernTheme.Label("Translation will stop during installation. Your settings and models are kept.", 12, ModernTheme.Muted));
        updateContent.Children.Add(updateCopy);
        _updateBanner.Child = updateContent; _updateBanner.Padding = new Thickness(16);
        _updateBanner.Background = ModernTheme.Surface; _updateBanner.BorderBrush = ModernTheme.Accent;
        _updateBanner.BorderThickness = new Thickness(1); _updateBanner.CornerRadius = new CornerRadius(12); _updateBanner.Margin = new Thickness(0, 0, 0, 16);
        headerArea.Children.Add(_updateBanner); body.Children.Add(headerArea);
        var scroll = new ScrollViewer { Content = _content, Padding = new Thickness(0, 0, 12, 0),Style=(Style)Application.Current.FindResource(typeof(ScrollViewer)) };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0),Background=ModernTheme.Background };
        footer.Children.Add(new Border {Height=1,Background=ModernTheme.Stroke,Margin=new Thickness(0,0,0,12)});
        footer.Children.Add(_progress);
        var statusRow = new DockPanel();
        _cancel = ModernTheme.Button("Cancel task", (_, _) => _operation?.Cancel());
        _cancel.Visibility=Visibility.Collapsed; _cancel.Padding = new Thickness(10, 5, 10, 5);
        _taskTroubleshooting = ModernTheme.Button("Troubleshooting", (_, _) => Navigate("Diagnostics"));
        _taskTroubleshooting.Name = "TaskTroubleshootingButton";
        _taskTroubleshooting.Visibility = Visibility.Collapsed;
        _taskTroubleshooting.Padding = new Thickness(10, 5, 10, 5);
        var taskActions = new WrapPanel();
        taskActions.Children.Add(_taskTroubleshooting); taskActions.Children.Add(_cancel);
        DockPanel.SetDock(taskActions, Dock.Right);
        _status.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(taskActions); statusRow.Children.Add(_status); footer.Children.Add(statusRow);
        _progressDetail.Visibility = _progressTiming.Visibility = Visibility.Collapsed;
        _progressDetail.Margin = new Thickness(0, 4, 0, 0); _progressTiming.Margin = new Thickness(0, 4, 0, 0);
        footer.Children.Add(_progressDetail); footer.Children.Add(_progressTiming);
        System.Windows.Automation.AutomationProperties.SetName(_progress, "Current task progress");
        System.Windows.Automation.AutomationProperties.SetName(_progressDetail, "Current task detail");
        System.Windows.Automation.AutomationProperties.SetName(_progressTiming, "Task elapsed time and activity");
        System.Windows.Automation.AutomationProperties.SetName(_status,"Translation status");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_status,System.Windows.Automation.AutomationLiveSetting.Polite);
        Grid.SetRow(footer, 2); body.Children.Add(footer);
        Grid.SetColumn(body, 1); frame.Children.Add(body); Content = frame;
        _runtime.StateChanged += RuntimeChanged;
        Activated += (_, _) =>
        {
            RefreshSaveState(reloadSaved: !_preview);
            if (!_preview && !_busy && !_closing && _page == "Speakers" && !_savedPeopleLoading)
                _ = RefreshSavedPeopleAsync();
        };
        _logTimer.Tick += (_, _) =>
        {
            if (_page == "Diagnostics") RefreshLog();
            _ = RefreshDiagnosticsFeedbackAsync();
            if (_page == "Speakers" && _speakerRecognitionState is not null) UiLocalization.SetText(_speakerRecognitionState, SpeakerRecognitionStateText());
            if (_page == "Speakers" && IsVisible && !_busy && !_closing && !_savedPeopleLoading &&
                DateTimeOffset.UtcNow - _lastPeopleRefreshAt >= TimeSpan.FromSeconds(10)) _ = RefreshSavedPeopleAsync();
        };
        _taskTimer.Tick += (_, _) => RefreshProgressTiming();
        _oscTimer.Tick += async (_, _) => await CheckOscConnectionAsync();
        if (!preview) _logTimer.Start();
        _desktopLifetime = new DesktopLifetime(this, _runtime, _provisioner, _settings, () =>
        {
            _setupFlow?.Cancel(); _setupFlow?.Dispose(); CloseQuickTour();
            _operation?.Cancel(); _lifetime.Cancel(); _logTimer.Stop(); _taskTimer.Stop(); _oscTimer.Stop(); _oscProbe?.Dispose();
            _runtime.StateChanged -= RuntimeChanged;
        }, () => { _closing = true; CloseHomeTargetLanguagePicker(); RefreshSaveState(); }, preview, () => _operationFinished?.Task ?? Task.CompletedTask, () => _operation?.Cancel());
        Loaded+=async (_,_)=>
        {
            ApplySlimScrollbars((FrameworkElement)Content);
            if (launchOptions?.StartAll != true && _setupFlow is null && _settings.FirstRunSetupCompleted && !_settings.FirstRunIntroductionCompleted)
                ShowQuickTour();
            if (!preview && !_closing)
            {
                _oscTimer.Start();
                _ = CheckOscConnectionAsync();
                if (!_startupUpdatesChecked && _settings.CheckForUpdatesOnLaunch)
                { _startupUpdatesChecked = true; _ = CheckForStartupUpdateAsync(); }
            }
            if (!preview && !_startupRequested && launchOptions?.StartAll == true)
            {
                _startupRequested = true;
                await StartRequestedAsync(launchOptions);
            }
        };
        Navigate(_settings.FirstRunSetupCompleted || launchOptions?.StartAll == true ? "Home" : "Setup");
        if (ShouldShowGuidedSetup(_settings, preview, launchOptions?.StartAll == true)) OpenGuidedSetup(firstRun: true);
        UiText.LanguageChanged += RefreshInterfaceLanguage;
        Closed += (_, _) => UiText.LanguageChanged -= RefreshInterfaceLanguage;
        UiLocalization.ApplyTree(this);
        if(!artworkAvailable)UiLocalization.SetText(_status,"Background image unavailable. Choose it again in Settings → Themes.");
        if (!preview) DesktopLog.Write(launchOptions?.StartAll == true
            ? "Dashboard opened with an explicit --start-all request. Startup will run after the window loads."
            : "Dashboard opened. Engines remain stopped until Start is selected.");
    }

    internal Window CreateFirstRunIntroductionDialog() => FirstRunIntroductionUi.Create(this);

    internal bool TryShowFirstRunIntroduction()
    {
        if (_preview || _closing || _busy || _introductionShown || _settings.FirstRunIntroductionCompleted || !IsLoaded || !IsVisible) return false;
        _introductionShown = true;
        Window introduction = CreateFirstRunIntroductionDialog();
        introduction.ShowDialog();
        try
        {
            SettingsStore.SaveFirstRunIntroductionPreference(true);
            _settings.FirstRunIntroductionCompleted = true;
        }
        catch (Exception error) { Error("Save introduction preference", error); }
        if (!_closing && introduction.Tag is IntroductionChoice choice)
        {
            if (choice == IntroductionChoice.Setup) Navigate("Setup");
            else if (choice == IntroductionChoice.Help) Navigate("Help");
        }
        return true;
    }

    private async Task StartRequestedAsync(LocalTalkLaunchOptions options) => await RunTask(async token =>
    {
        void Phase(string text) { Progress(text, null); DesktopLog.Write("Requested startup: " + text); }
        Phase("Checking SayWhat?");
        ProvisionModelChoice? model = options.TranslationModelId is string modelId
            ? LocalTalkProvisioner.ModelCatalog.FirstOrDefault(choice => choice.Id == modelId && choice.Role == ProvisionModelRole.Translation)
                ?? throw new ArgumentException("The selected catalogue item is not a translation model: " + modelId)
            : null;
        if (options.DownloadModel && model?.RequiresTermsAcceptance == true && CreateModelTermsDialog(model).ShowDialog() != true)
            throw new SetupUserException("Download cancelled. Review and accept the model terms before downloading.");
        if (options.SpeakerModelPath is string speaker && !File.Exists(speaker))
            throw new FileNotFoundException("The requested existing ONNX fingerprint model was not found. It will not be downloaded automatically.", speaker);
        if (options.Speech is WslSpeechRegistration speech)
        {
            Phase("Checking speech recognition");
            await _provisioner.RegisterWslSpeechAsync(speech.Distribution, speech.ServerPath, speech.ModelPath, speech.Backend, token);
        }
        if (options.DownloadModel)
        {
            if (model is null) throw new ArgumentException("An explicit translation model ID is required before authorizing a download.");
            Phase(UiText.Format("Downloading {0} ({1} GB)",model.Name,(model.SizeBytes / 1e9).ToString("0.0")));
            await _provisioner.DownloadModelAsync(model.Id, true, ProvisionProgress(), token);
            Phase("Download checked");
        }
        else if (model is not null && _provisioner.ResolveModelPath(model.Id) is null)
            throw new InvalidOperationException("The requested translation model is not registered. Import it in Models or explicitly use --download-model; no download was started.");
        token.ThrowIfCancellationRequested();
        if (model is not null)
        {
            _settings.UseManagedTranslator = true;
            _settings.ShareTranslationModel = true;
            _settings.OwnManagedModelId = _settings.IncomingManagedModelId = model.Id;
        }
        if (options.SpeakerModelPath is string existingSpeaker)
        { _settings.SpeakerModelPath = existingSpeaker; _settings.UseAiSpeakerRecognition = true; SettingsStore.SaveSpeakerRecognitionPreference(true); }
        Phase("Applying your saved settings");
        Persist();
        try
        {
            Phase("Starting your voice translator");
            await _runtime.StartOwnAsync(_settings.Copy(), token, ProvisionProgress());
            token.ThrowIfCancellationRequested();
            Phase("Starting the VRChat subtitle translator");
            await _runtime.StartIncomingAsync(_settings.Copy(), OpenOptions, token, ProvisionProgress(), OpenSpeakerSettings, ApplyTranslationProfileAsync);
            Phase("Translation is ready");
        }
        catch
        {
            DesktopLog.Write("Requested startup did not complete; stopping only app-owned tools.");
            try { await StopForTaskAsync(); } catch (Exception error) { DesktopLog.Write("Requested startup cleanup: " + error.Message); }
            throw;
        }
    }, "Starting translation", "Running · your voice and subtitles");

    private Border BuildSidebar()
    {
        var panel = new DockPanel { Margin = new Thickness(16, 28, 16, 20) };
        var brand = new StackPanel { Margin = new Thickness(12, 0, 0, 34) };
        var lockup = BrandArtwork.Lockup(164); lockup.Name = "SidebarBrandLockup";
        lockup.Margin = new Thickness(0, 0, 0, 10); brand.Children.Add(lockup);
        brand.Children.Add(ModernTheme.Label("VRChat translation", 12, ModernTheme.Muted));
        DockPanel.SetDock(brand, Dock.Top); panel.Children.Add(brand);
        var bottom = new StackPanel { Name = "SidebarFooter", Margin = new Thickness(12, 0, 0, 0) };
        _oscConnectionButton = ModernTheme.Button("Check VRChat OSC", async (_, _) =>
        {
            if (!_preview && !_closing) { if (_page != "Home") Navigate("Home"); await CheckOscConnectionAsync(manual: true); }
        });
        _oscConnectionButton.Padding = new Thickness(0, 5, 0, 5); _oscConnectionButton.MinHeight = 30;
        _oscConnectionButton.Background = Brushes.Transparent; _oscConnectionButton.BorderThickness = new Thickness(0);
        _oscConnectionButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        var connection = new StackPanel { Orientation = Orientation.Horizontal };
        _oscConnectionIcon.Width = _oscConnectionIcon.Height = 16; _oscConnectionIcon.Margin = new Thickness(0, 0, 8, 0);
        _oscConnectionIcon.VerticalAlignment = VerticalAlignment.Center;
        _oscConnectionText.Margin = new Thickness(0); _oscConnectionText.MaxWidth = 122; _oscConnectionText.VerticalAlignment = VerticalAlignment.Center;
        connection.Children.Add(_oscConnectionIcon); connection.Children.Add(_oscConnectionText);
        _oscConnectionButton.Content = connection; bottom.Children.Add(_oscConnectionButton);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_oscConnectionText, System.Windows.Automation.AutomationLiveSetting.Polite);
        UiLocalization.Bind(_oscConnectionText, System.Windows.Automation.AutomationProperties.NameProperty, "VRChat OSC status");
        RenderOscConnectionStatus();
        bottom.Children.Add(ModernTheme.Label("Interface language", 11, ModernTheme.Muted));
        _interfaceLanguagePicker = new ComboBox { Name="InterfaceLanguagePicker", ItemsSource=UiText.Choices,
            SelectedItem=UiText.Choices.First(choice=>choice.Code==UiText.Language), MinHeight=38, Margin=new Thickness(0,0,0,12) };
        UiLocalization.Bind(_interfaceLanguagePicker, System.Windows.Automation.AutomationProperties.NameProperty, "Interface language");
        _interfaceLanguagePicker.SelectionChanged += (_,_) => {
            if (!_changingInterfaceLanguage && _interfaceLanguagePicker.SelectedItem is UiLanguageChoice choice) ChangeInterfaceLanguage(choice.Code);
        };
        bottom.Children.Add(_interfaceLanguagePicker);
        var version=ModernTheme.Label("",11,ModernTheme.Muted);
        UiLocalization.SetFormat(version,"Preview {0}",AppBrand.Version);bottom.Children.Add(version);
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var navigation = new StackPanel();
        foreach (string page in PrimaryPages)
        {
            var button = ModernTheme.Button(PageDisplayName(page), (_, _) => Navigate(page));
            var label = new StackPanel { Orientation=Orientation.Horizontal };
            var icon=ModernTheme.Icon(page);icon.Margin=new Thickness(0,0,12,0);label.Children.Add(icon);
            var navigationLabel=ModernTheme.Label(PageDisplayName(page));navigationLabel.Margin=new Thickness(0);navigationLabel.VerticalAlignment=VerticalAlignment.Center;
            label.Children.Add(navigationLabel);button.Content=label;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 1, 0, 3); button.Padding = new Thickness(12, 9, 12, 9);
            button.BorderThickness = new Thickness(1); _navigation[page] = button; navigation.Children.Add(button);
            if(page=="Speakers")navigation.Children.Add(new Border {Height=1,Background=ModernTheme.Stroke,Margin=new Thickness(12,12,12,12)});
        }
        _navigationScroll.Name = "SidebarNavigationScroll";
        _navigationScroll.Content = navigation;
        _navigationScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _navigationScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _navigationScroll.CanContentScroll = false;
        _navigationScroll.ClipToBounds = true;
        _navigationScroll.Background = ModernTheme.Surface;
        _navigationScroll.Margin = new Thickness(0, 0, 0, 12);
        _navigationScroll.Style = (Style)Application.Current.FindResource(typeof(ScrollViewer));
        _navigationScroll.ScrollChanged += (_, change) =>
        {
            if (change.ViewportHeightChange != 0 || change.ExtentHeightChange != 0) EnsureSelectedNavigationVisible();
        };
        panel.Children.Add(_navigationScroll);
        return new Border { Background = ModernTheme.Surface, BorderBrush = ModernTheme.Stroke, BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };
    }

    private void EnsureSelectedNavigationVisible()
    {
        string selected = _page is "Themes" or "Listening" ? "Settings" : PrimaryPages.Contains(_page) ? _page : "Advanced";
        if (_navigationScroll.Content is not FrameworkElement navigation ||
            !_navigation.TryGetValue(selected, out Button? button) || _navigationScroll.ViewportHeight <= 0 || button.ActualHeight <= 0) return;
        double top = button.TranslatePoint(new Point(), navigation).Y;
        double bottom = top + button.ActualHeight;
        double offset = _navigationScroll.VerticalOffset;
        if (top < offset) offset = top;
        else if (bottom > offset + _navigationScroll.ViewportHeight) offset = bottom - _navigationScroll.ViewportHeight;
        else return;
        _navigationScroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, _navigationScroll.ScrollableHeight));
    }

    internal void Navigate(string page)
    {
        CloseHomeTargetLanguagePicker();
        if (page == "Overview") page = "Home";
        if (page == "People") page = "Speakers";
        bool changingPage = _page != page;
        if (page == "Speakers" && _page != "Speakers") { _savedPeopleLoaded = false; _savedPeopleError = null; }
        _page = page; UiLocalization.SetText(_pageTitle,PageDisplayName(page));
        UiLocalization.SetText(_pageEyebrow,PrimaryPages.Contains(page)||page is "Themes" or "Listening"?AppBrand.DisplayName:"ADVANCED");
        _backButton.Visibility=PrimaryPages.Contains(page)?Visibility.Collapsed:Visibility.Visible;
        UiLocalization.Bind(_backButton,ContentControl.ContentProperty,page is "Themes" or "Listening"?"← Back to Settings":"← Back to Advanced");
        UiLocalization.Bind(_backButton,System.Windows.Automation.AutomationProperties.NameProperty,page is "Themes" or "Listening"?"← Back to Settings":"← Back to Advanced");
        _stopButton.Visibility=_runtime.OwnRunning||_runtime.IncomingRunning||_busy?Visibility.Visible:Visibility.Collapsed;
        _content.IsEnabled = !_busy;
        UiLocalization.Bind(_saveButton, ContentControl.ContentProperty, "Save & apply");
        _saveButton.Visibility = page == "Setup" ? (_settings.FirstRunSetupCompleted ? Visibility.Visible : Visibility.Collapsed)
            : page is "Home" or "Speakers" or "Help" or "Advanced" or "Themes" or "Diagnostics" or "Performance" ? Visibility.Collapsed : Visibility.Visible;
        string navigationPage = page is "Themes" or "Listening"?"Settings":PrimaryPages.Contains(page) ? page : "Advanced";
        foreach (var entry in _navigation)
        {
            bool selected=entry.Key==navigationPage;
            entry.Value.Background=selected?ModernTheme.Selection:Brushes.Transparent;
            entry.Value.BorderBrush=selected?ModernTheme.HeroStroke:Brushes.Transparent;
            entry.Value.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
            entry.Value.Foreground=selected?ModernTheme.Accent:ModernTheme.Muted;
        }
        EnsureSelectedNavigationVisible();
        _logView = null; _badges.Clear(); _oscHelp = null; _oscHelpText = null;
        _ownVoiceTourTarget = _subtitlesTourTarget = null;
        _speakerRecognitionSettingsSection = null;
        _peopleView = null;
        _setupActionButton = null; _setupChoicesPanel = null; _setupChoiceStatus = null; _setupActionHint = null;
        _content.Content = page switch
        {
            "Languages" => LanguagesPage(), "Settings" => ApplicationSettingsPage(), "Listening" => ListeningPage(), "Themes" => ThemesPage(), "Advanced" => AdvancedPage(),
            "Translation" => TranslationPage(), "Audio & timing" => AudioPage(), "Speakers" => SpeakersPage(), "Speaker options" => SpeakersPage(true),
            "Models" => ModelsPage(), "Performance" => PerformancePage(), "Diagnostics" => DiagnosticsPage(),
            "Setup" => SetupPage(), "Help" => HelpUi.Build(Navigate), "Manual setup" => ManualSetupPage(), _ => OverviewPage()
        };
        UiLocalization.ApplyTree(_content);
        // Keep position for live changes on this page, but entering another page
        // must not inherit a deep Settings scroll and hide its primary controls.
        if (changingPage && _content.Parent is ScrollViewer pageScroll) pageScroll.ScrollToTop();
        RefreshSaveState();
        if(IsLoaded) Dispatcher.InvokeAsync(()=>ApplySlimScrollbars((FrameworkElement)Content),System.Windows.Threading.DispatcherPriority.Loaded);
    }

    internal static string PageDisplayName(string page) => page switch { "Speakers" => "People", "Speaker options" => "Voice recognition", "Listening" => "Subtitle listening", _ => page };

    internal void ChangeInterfaceLanguage(string code)
    {
        if (_closing || (code == _settings.UiLanguage && code == UiText.Language)) return;
        string previous=_settings.UiLanguage;
        try {
            if (!_preview) SettingsStore.SaveUiLanguagePreference(code);
            _settings.UiLanguage=UiLanguagePreference.Normalize(code);
            UiText.Language=_settings.UiLanguage;
            RefreshSaveState(reloadSaved: !_preview);
        }
        catch(Exception error) {
            _changingInterfaceLanguage=true;
            try { if(_interfaceLanguagePicker is not null) _interfaceLanguagePicker.SelectedItem=UiText.Choices.First(choice=>choice.Code==previous); }
            finally { _changingInterfaceLanguage=false; }
            Error("Save interface language",error);
        }
    }

    private void RefreshInterfaceLanguage()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(RefreshInterfaceLanguage);return; }
        if (_closing) return;
        _changingInterfaceLanguage=true;
        try { if(_interfaceLanguagePicker is not null) _interfaceLanguagePicker.SelectedItem=UiText.Choices.First(choice=>choice.Code==UiText.Language); }
        finally { _changingInterfaceLanguage=false; }
        Title=AppBrand.DisplayName+" — "+UiText.T("VRChat translation");
        if (_setupFlow is not null) _setupFlow.RefreshLanguage();
        else if (!_busy) Navigate(_page);
        else if (_currentProgress is not null) { RenderTaskProgress(); RefreshProgressTiming(); }
        UiLocalization.ApplyTree(this);
        RenderOscConnectionStatus();
        _runtime.RefreshInterfaceLanguage();
    }

    internal static void ApplySlimScrollbars(DependencyObject root)
    {
        if(root is System.Windows.Controls.Primitives.ScrollBar bar && bar.Orientation==Orientation.Vertical)
        {
            bar.Width=7;bar.MinWidth=0;bar.MaxWidth=7;bar.ClipToBounds=true;
            bar.Style=(Style)Application.Current.FindResource("SlimScrollBar");
        }
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) ApplySlimScrollbars(VisualTreeHelper.GetChild(root,i));
    }

    private StackPanel OverviewPage()
    {
        return VoiceFirstHomePage();
    }

    private async Task CheckOscConnectionAsync(bool manual = false)
    {
        if (_preview || _closing || _lifetime.IsCancellationRequested || _checkingOsc) return;
        _checkingOsc = true;
        try
        {
            if (manual) SetOscConnectionStatus(new(VrchatOscConnectionState.Checking));
            _oscProbe ??= new VrchatOscConnectionProbe();
            var status = await _oscProbe.CheckAsync(_lifetime.Token);
            if (!_closing && !_lifetime.IsCancellationRequested)
            {
                SetOscConnectionStatus(status);
                if (manual && _oscHelp is not null) _oscHelp.IsExpanded = true;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing && !_lifetime.IsCancellationRequested)
            { SetOscConnectionStatus(new(VrchatOscConnectionState.Error)); DesktopLog.Write("OSC availability check: " + error.GetType().Name); }
        }
        finally { _checkingOsc = false; }
    }

    internal void SetOscConnectionStatus(VrchatOscConnectionStatus status)
    { _oscConnection = status; RenderOscConnectionStatus(); }

    private void RenderOscConnectionStatus()
    {
        string label = _oscConnection.State switch
        {
            VrchatOscConnectionState.Connected => "VRChat OSC available",
            VrchatOscConnectionState.NotRunning => "VRChat is closed",
            VrchatOscConnectionState.Unavailable => "No OSC connection",
            VrchatOscConnectionState.WrongPort => "OSC port mismatch",
            VrchatOscConnectionState.Error => "Couldn't check OSC",
            _ => "Checking VRChat…"
        };
        string detail = _oscConnection.State switch
        {
            VrchatOscConnectionState.Connected => "VRChat responded to OSCQuery. Chatbox visibility still depends on VRChat settings.",
            VrchatOscConnectionState.NotRunning => "Open VRChat to send your translated voice to its chatbox.",
            VrchatOscConnectionState.Unavailable => "VRChat is open, but its OSC connection couldn't be verified. Check Action Menu → OSC → Enabled, then check again.",
            VrchatOscConnectionState.WrongPort => "VRChat's OSC port differs from SayWhat?'s port 9000. Set VRChat's OSC input port to 9000, then check again.",
            VrchatOscConnectionState.Error => "The connection check failed. Check again or open Advanced → Diagnostics for details.",
            _ => "Checking whether VRChat's OSC connection is available."
        };
        Brush color = _oscConnection.State == VrchatOscConnectionState.Connected ? ModernTheme.Good
            : _oscConnection.State is VrchatOscConnectionState.Unavailable or VrchatOscConnectionState.WrongPort or VrchatOscConnectionState.Error ? ModernTheme.Warning : ModernTheme.Muted;
        UiLocalization.SetText(_oscConnectionText, label); _oscConnectionText.Foreground = color; _oscConnectionIcon.Stroke = color;
        if (_oscConnectionButton is not null) UiLocalization.Bind(_oscConnectionButton, FrameworkElement.ToolTipProperty, detail);
        if (_oscHelpText is not null) UiLocalization.SetText(_oscHelpText, detail);
        if (_oscHelp is not null && _oscConnection.State is VrchatOscConnectionState.Unavailable or VrchatOscConnectionState.WrongPort or VrchatOscConnectionState.Error)
            _oscHelp.IsExpanded = true;
    }

    private async Task CheckForStartupUpdateAsync()
    {
        if (_preview || _closing || _lifetime.IsCancellationRequested) return;
        try
        {
            var result = await AppUpdateChecker.CheckAsync(_lifetime.Token);
            if (!_closing && !_lifetime.IsCancellationRequested) ShowAvailableUpdate(result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing) DesktopLog.Write("Startup update check unavailable: " + error.GetType().Name); }
    }

    internal void ShowAvailableUpdate(AppUpdateInfo result)
    {
        if (!result.Ready || !result.IsNewer || result.Installer is null) return;
        _availableUpdate = result;
        UiLocalization.SetFormat(_updateBannerText, "Preview {0} is available.", result.Version);
        _updateBanner.Visibility = Visibility.Visible;
        UiLocalization.ApplyTree(_updateBanner);
    }

    private async Task InstallAvailableUpdateAsync()
    {
        if (_preview || _closing || _busy || _availableUpdate is null) return;
        if (!Confirm("Download and install this update? Translation will stop during installation. Settings and models are kept. This preview installer is unsigned; Windows may ask for permission.")) return;
        AppUpdateInfo update = _availableUpdate;
        await RunTask(async token =>
        {
            var progress = new Progress<AppUpdateProgress>(value =>
                UpdateProvisionProgress(new(value.Stage, value.Stage, value.ReceivedBytes, value.TotalBytes)));
            using AppUpdateDownload download = await AppUpdateInstallerService.DownloadAsync(update, progress, token);
            token.ThrowIfCancellationRequested();
            TaskStage("Starting update", "SayWhat? will close and stop translation before installing. It will reopen without starting models.");
            AppUpdateInstallerService.LaunchInstaller(download, _settings.UiLanguage, Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable."));
            _updateBanner.Visibility = Visibility.Collapsed;
        }, "Updating SayWhat?", "Update started. SayWhat? will reopen when it finishes. Windows may ask for permission.", refreshView: false);
    }

    private static Border ConversationHero(StackPanel content)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(104)});
        content.Margin=new Thickness(0,0,20,0);grid.Children.Add(content);
        var art=BrandArtwork.Mark(84);art.Name="HeroBrandMark";art.VerticalAlignment=VerticalAlignment.Center;art.HorizontalAlignment=HorizontalAlignment.Center;Grid.SetColumn(art,1);grid.Children.Add(art);
        var hero=ModernTheme.Card(grid);hero.Background=ModernTheme.Hero;hero.BorderBrush=ModernTheme.HeroStroke;hero.Padding=new Thickness(26,24,26,24);return hero;
    }

    private async Task StartBothAsync()
    {
        if (!_settings.FirstRunSetupCompleted && _provisioner.SpeechRegistration is null) { Navigate("Setup"); return; }
        await RunTask(async token =>
        {
            // Save edited choices only while both capture hosts are stopped.
            if (_runtime.OwnRunning || _runtime.IncomingRunning) await StopForTaskAsync();
            token.ThrowIfCancellationRequested(); TaskStage("Saving your settings"); Persist();
            try
            {
                await _runtime.StartOwnAsync(_settings.Copy(), token, ProvisionProgress());
                await _runtime.StartIncomingAsync(_settings.Copy(), OpenOptions, token, ProvisionProgress(), OpenSpeakerSettings, ApplyTranslationProfileAsync);
            }
            catch { try { await StopForTaskAsync(); } catch { } throw; }
        }, "Starting translation", "Translating your voice and showing subtitles.");
    }

    private StackPanel LanguagesPage()
    {
        var page = Page("Choose the translation language for each direction.");
        page.Children.Add(ModernTheme.Card(LanguageSection()));
        var style = Section("Translation style", "Fast keeps replies concise. Careful allows longer replies.");
        style.Children.Add(Select(new[] { "Fast", "Careful" }, _settings.TranslationQuality, value => _settings.TranslationQuality = value));
        style.Children.Add(ModernTheme.Label("Names and special words · optional", 13, ModernTheme.Muted));
        var glossary = Input(_settings.Glossary); glossary.AcceptsReturn = true; glossary.TextWrapping = TextWrapping.Wrap; glossary.MinHeight = 95; glossary.MaxLength = 2000;
        glossary.TextChanged += (_, _) => _settings.Glossary = glossary.Text;
        style.Children.Add(glossary); style.Children.Add(ModernTheme.Label("Write one pair per line, for example: 多力多滋 → Doritos", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(style));
        return page;
    }

    private StackPanel ApplicationSettingsPage()
    {
        var page = Page("");
        page.Children.Add(CreateTranslationProfilesCard());
        var guided = Section("Run setup again", "Reopen guided setup to change your languages, theme, or translator. Downloaded files are reused.");
        var restartSetup = ModernTheme.Button("Run setup again", async (_, _) => await RestartGuidedSetupAsync());
        restartSetup.Name = "RestartGuidedSetupButton"; restartSetup.IsEnabled = !_busy;
        guided.Children.Add(restartSetup);
        guided.Children.Add(ModernTheme.Button("Show the quick tour", (_, _) => ShowQuickTour()));
        page.Children.Add(ModernTheme.Card(guided));
        page.Children.Add(CreateListeningSettingsCard());
        page.Children.Add(ModernTheme.Card(ThemesSection()));
        var recognition = Section("Voice recognition · optional", "A small CPU model matches voices to saved people. Speech-to-text and translation use separate models and work without person recognition.");
        recognition.Name = "SpeakerRecognitionSettingsSection";
        _speakerRecognitionSettingsSection = recognition;
        var recognitionToggle = ModernTheme.Button(_settings.UseAiSpeakerRecognition ? "Turn off recognition" : "Enable recognition",
            async (_, _) => await ChangeSpeakerRecognitionAsync(!_settings.UseAiSpeakerRecognition), !_settings.UseAiSpeakerRecognition);
        recognitionToggle.IsEnabled = !_settings.IncomingSinglePerson; recognition.Children.Add(recognitionToggle);
        if (_settings.IncomingSinglePerson) recognition.Children.Add(ModernTheme.Label("Not needed for one person", 13, ModernTheme.Muted));
        recognition.Children.Add(ModernTheme.Label("Off releases the recognition model. Translation keeps working, and your saved people are kept.", 13, ModernTheme.Muted));
        recognition.Children.Add(ModernTheme.Label("CPU time and memory are shared with your game. Turning person recognition off may improve performance.", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(recognition));
        var closing = Section("When I close this window", "You can stop translation, or keep it running in the notification area beside the clock.");
        var choices = new[] {
            new CloseBehaviorChoice("Ask", "Ask me each time"),
            new CloseBehaviorChoice("Quit", "Quit and stop translation"),
            new CloseBehaviorChoice("Tray", "Keep translation running in the tray") };
        closing.Children.Add(Field("Close button", Select(choices, choices.FirstOrDefault(choice => choice.Id == _settings.CloseButtonBehavior) ?? choices[0], choice => _settings.CloseButtonBehavior = choice.Id)));
        closing.Children.Add(ModernTheme.Label("Use the SayWhat? icon beside the clock to reopen the window or quit. Save & apply remembers your choice.", 13, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(closing));
        var messages = Section("My VRChat messages", "Updates use the full sentence heard so far. Early wording may change. Turn this off to send only finished sentences.");
        messages.Children.Add(Check("Show translations while I speak", _settings.OwnVoiceSendEarlyPreviews, value => _settings.OwnVoiceSendEarlyPreviews = value));
        messages.Children.Add(ModernTheme.Button("Adjust sentence timing", (_, _) => Navigate("Audio & timing")));
        page.Children.Add(ModernTheme.Card(messages));
        var updates = Section("Updates", "");
        updates.Children.Add(Check("Check for updates when SayWhat? opens", _settings.CheckForUpdatesOnLaunch, value => _settings.CheckForUpdatesOnLaunch = value));
        var updateStatus = ModernTheme.Label("", 13, ModernTheme.Muted);
        UiLocalization.SetFormat(updateStatus, "Installed preview · {0}", AppBrand.Version);
        updates.Children.Add(updateStatus);
        Uri? releasePage = null;
        var downloads = ModernTheme.Button("Open download page", (_, _) =>
        {
            if (releasePage is not null) Process.Start(new ProcessStartInfo(releasePage.AbsoluteUri) { UseShellExecute = true });
        });
        downloads.Visibility = Visibility.Collapsed;
        var checkUpdates = ModernTheme.Button("Check for updates", async (_, _) => await RunTask(async token =>
        {
            downloads.Visibility = Visibility.Collapsed;
            UiLocalization.SetText(updateStatus, "Checking for updates…");
            try
            {
                var result = await AppUpdateChecker.CheckAsync(token);
                releasePage = result.ReleasePage;
                if (!result.Ready) UiLocalization.SetText(updateStatus, "No downloadable preview is available yet.");
                else if (!result.IsNewer) UiLocalization.SetText(updateStatus, "You're up to date.");
                else { UiLocalization.SetFormat(updateStatus, "Preview {0} is available.", result.Version); downloads.Visibility = Visibility.Visible; ShowAvailableUpdate(result); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
            { UiLocalization.SetText(updateStatus, "Couldn't check for updates. Try again later."); }
        }, "Checking for updates", refreshView: false));
        checkUpdates.IsEnabled = !_preview;
        updates.Children.Add(checkUpdates);
        updates.Children.Add(downloads);
        page.Children.Add(ModernTheme.Card(updates));
        var privacy = Section("Your speech", "Speech and translations are processed on your computer. Setup uses the internet to download what the app needs.");
        privacy.Children.Add(ModernTheme.Label("Your saved people and voice examples stay on this PC. Recordings are not saved by speaker learning. Logs may contain spoken or translated text; review them before sharing.", 13, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(privacy));
        page.Children.Add(CreateDiagnosticsSettingsCard());
        return page;
    }

    private sealed record AppearanceChoice(string Id,string Name)
    { public override string ToString()=>UiText.T(Name); }

    private StackPanel ThemesPage()
    {
        var page=Page("");page.Children.Add(ModernTheme.Card(ThemesSection()));return page;
    }

    private StackPanel ThemesSection()
    {
        var section=Section("Themes","Choose a preset, or customize the background and text palette.");
        var custom=new StackPanel { Visibility=_settings.UiThemeId=="custom"?Visibility.Visible:Visibility.Collapsed };
        var themes=AppearanceTheme.Presets.Select(p=>new AppearanceChoice(p.Id,p.Name)).ToArray();
        var picker=new ComboBox { Name="ThemePicker",ItemsSource=themes,SelectedItem=themes.First(p=>p.Id==_settings.UiThemeId),Margin=new Thickness(0,0,0,12) };
        bool selecting=false;
        picker.SelectionChanged+=(_,_)=>{
            if(selecting || picker.SelectedItem is not AppearanceChoice choice)return;
            if(!ChangeAppearance(s=>s.UiThemeId=choice.Id)) { selecting=true;picker.SelectedItem=themes.First(p=>p.Id==_settings.UiThemeId);selecting=false; }
            custom.Visibility=_settings.UiThemeId=="custom"?Visibility.Visible:Visibility.Collapsed;
        };
        section.Children.Add(Field("Theme",picker));
        var sample=new WrapPanel { Margin=new Thickness(0,0,0,12) };
        foreach(var brush in new[]{ModernTheme.Background,ModernTheme.Surface,ModernTheme.Raised,ModernTheme.Text,ModernTheme.Muted,ModernTheme.Accent,ModernTheme.Good})
            sample.Children.Add(new Border { Background=brush,BorderBrush=ModernTheme.Stroke,BorderThickness=new Thickness(1),Width=32,Height=24,CornerRadius=new CornerRadius(5),Margin=new Thickness(0,0,6,0),IsHitTestVisible=false });
        section.Children.Add(sample);
        var color=Input(_settings.CustomThemeBackground);color.Name="CustomBackgroundColor";color.MaxLength=7;
        UiLocalization.Bind(color,System.Windows.Automation.AutomationProperties.NameProperty,"Background color");
        var colorActions=new DockPanel();var apply=ModernTheme.Button("Apply color",(_,_)=>{
            if(!AppearanceTheme.TryColor(color.Text,out _)) { UiLocalization.SetText(_status,"Use a six-digit color such as #191817.");return; }
            ChangeAppearance(s=>s.CustomThemeBackground=color.Text);color.Text=_settings.CustomThemeBackground;
        });DockPanel.SetDock(apply,Dock.Right);colorActions.Children.Add(apply);colorActions.Children.Add(color);
        var colorPanel=Field("Background color",colorActions);
        var swatches=new WrapPanel { Margin=new Thickness(0,0,0,6) };
        foreach(string hex in new[]{"#191817","#101419","#173028","#332039","#F7F6F2","#F2EBDF","#EAF4EF","#FFFFFF"})
        {
            var swatch=ModernTheme.Button("",(_,_)=>{ if(ChangeAppearance(s=>s.CustomThemeBackground=hex))color.Text=_settings.CustomThemeBackground; });
            swatch.Content=new Border { Width=20,Height=20,CornerRadius=new CornerRadius(4),Background=ModernTheme.Brush(hex),IsHitTestVisible=false };
            swatch.Padding=new Thickness(5);swatch.MinHeight=32;swatch.ToolTip=hex;
            System.Windows.Data.BindingOperations.ClearBinding(swatch,System.Windows.Automation.AutomationProperties.NameProperty);
            System.Windows.Automation.AutomationProperties.SetName(swatch,UiText.T("Background color")+" "+hex);swatches.Children.Add(swatch);
        }
        colorPanel.Children.Add(swatches);
        var palettes=AppearanceTheme.TextPalettes.Select(p=>new AppearanceChoice(p.Id,p.Name)).ToArray();
        var palette=Select(palettes,palettes.First(p=>p.Id==_settings.CustomThemeTextPalette),choice=>ChangeAppearance(s=>s.CustomThemeTextPalette=choice.Id));palette.Name="TextPalettePicker";
        custom.Children.Add(TwoFields(colorPanel,Field("Text palette",palette)));
        custom.Children.Add(ModernTheme.Label("Text colors adjust for readability. Text palettes coordinate headings, regular text, hints and status colors.",12,ModernTheme.Muted));
        var imageName=ModernTheme.Label("",12,ModernTheme.Muted,localize:false);imageName.Name="BackgroundImageName";
        void DescribeImage() { if(string.IsNullOrEmpty(_settings.CustomThemeImagePath)) { imageName.ClearValue(UiLocalization.LiteralProperty);UiLocalization.SetText(imageName,"No image selected"); }
            else { System.Windows.Data.BindingOperations.ClearBinding(imageName,TextBlock.TextProperty);UiLocalization.Literal(imageName);imageName.Text=Path.GetFileName(_settings.CustomThemeImagePath); } }
        DescribeImage();custom.Children.Add(ModernTheme.Label("Background image",12,ModernTheme.Muted));custom.Children.Add(imageName);
        var imageActions=new WrapPanel();
        imageActions.Children.Add(ModernTheme.Button("Add image…",(_,_)=>{
            var dialog=new OpenFileDialog { Title=UiText.T("Choose a background image"),Filter=UiText.T("PNG or JPEG images")+" (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",CheckFileExists=true,Multiselect=false };
            if(dialog.ShowDialog(this)!=true)return;
            try { ThemeBackground.ReadImage(dialog.FileName);ChangeAppearance(s=>s.CustomThemeImagePath=dialog.FileName,reloadImage:true);DescribeImage(); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.IO.FileFormatException or InvalidOperationException)
            { UiLocalization.SetText(_status,"Couldn't load this image. Choose a local PNG or JPEG under 20 MB."); }
        }));
        imageActions.Children.Add(ModernTheme.Button("Remove image",(_,_)=>{ChangeAppearance(s=>s.CustomThemeImagePath="");DescribeImage();}));custom.Children.Add(imageActions);
        var opacity=new Slider { Name="BackgroundImageOpacity",Minimum=0,Maximum=60,Value=_settings.CustomThemeImageOpacity*100,TickFrequency=1,IsSnapToTickEnabled=true };
        var opacityLabel=ModernTheme.Label("",12,ModernTheme.Muted);UiLocalization.SetFormat(opacityLabel,"Image opacity · {0}%",Math.Round(opacity.Value));
        opacity.ValueChanged+=(_,_)=>{ChangeAppearance(s=>s.CustomThemeImageOpacity=opacity.Value/100);UiLocalization.SetFormat(opacityLabel,"Image opacity · {0}%",Math.Round(opacity.Value));};
        System.Windows.Automation.AutomationProperties.SetName(opacity,UiText.T("Image opacity"));custom.Children.Add(opacityLabel);custom.Children.Add(opacity);
        System.Windows.Automation.AutomationProperties.SetLabeledBy(opacity,opacityLabel);
        section.Children.Add(custom);
        section.Children.Add(ModernTheme.Label("Changes apply immediately and are saved without restarting translation.",12,ModernTheme.Muted));
        return section;
    }

    internal bool ChangeAppearance(Action<AppSettings> change,bool reloadImage=false)
    {
        if(_closing)return false;
        var previous=_settings.Copy();change(_settings);AppearanceTheme.Normalize(_settings);
        try { if(!_preview)SettingsStore.SaveAppearancePreference(_settings); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            CopyAppearance(previous,_settings);
            UiLocalization.SetText(_status,"Couldn't save the theme. Your previous theme is still active.");
            return false;
        }
        if(previous.UiThemeId!=_settings.UiThemeId || previous.CustomThemeBackground!=_settings.CustomThemeBackground || previous.CustomThemeTextPalette!=_settings.CustomThemeTextPalette)
            AppearanceTheme.Apply(_settings);
        bool artworkAvailable=ThemeBackground.Apply(_settings,reloadImage);
        UiLocalization.SetText(_status,artworkAvailable?"Theme updated.":"Background image unavailable. Choose it again in Settings → Themes.");
        RefreshSaveState(reloadSaved: !_preview);
        return true;
    }
    private static void CopyAppearance(AppSettings source,AppSettings target)
    {
        target.UiThemeId=source.UiThemeId;target.CustomThemeBackground=source.CustomThemeBackground;target.CustomThemeTextPalette=source.CustomThemeTextPalette;
        target.CustomThemeImagePath=source.CustomThemeImagePath;target.CustomThemeImageOpacity=source.CustomThemeImageOpacity;
    }

    private sealed record CloseBehaviorChoice(string Id, string Name)
    { public override string ToString() => Name; }

    private StackPanel LanguageSection(bool includeAdditionalLanguages = true, Action? choicesChanged = null)
    {
        var targets = Section("Translation languages", "Choose the language you speak and where each translation goes.");
        Action? refreshChoices = null;
        var own=new StackPanel();own.Children.Add(ModernTheme.Kicker("MY VOICE → VRCHAT CHAT",ModernTheme.Accent));
        AddSourceLanguagePicker(own, incoming: false, choicesChanged);
        own.Children.Add(Field("Translate my voice to",Select(AllowedTargetLanguages(false),_settings.OwnVoiceTargetLanguage,value=> { _settings.OwnVoiceTargetLanguage=value;refreshChoices?.Invoke(); choicesChanged?.Invoke(); })));
        var incoming=new StackPanel();incoming.Children.Add(ModernTheme.Kicker("THEIR VOICES → MY SUBTITLES",ModernTheme.Good));
        AddSourceLanguagePicker(incoming, incoming: true, choicesChanged);
        incoming.Children.Add(Field("Translate their voices to",Select(AllowedTargetLanguages(true),_settings.IncomingTargetLanguage,value=> { _settings.IncomingTargetLanguage=value; choicesChanged?.Invoke(); })));
        targets.Children.Add(TwoFields(own,incoming));
        if (!includeAdditionalLanguages) return targets;
        var extra = new StackPanel { Visibility = _settings.OwnVoiceAdditionalLanguagesEnabled ? Visibility.Visible : Visibility.Collapsed };
        var saved = _settings.NormalizedOwnVoiceAdditionalLanguages();
        string[] selected = [saved.ElementAtOrDefault(0) ?? "None", saved.ElementAtOrDefault(1) ?? "None", saved.ElementAtOrDefault(2) ?? "None"];
        var pickers = new ComboBox[3];
        bool updating = false;
        void SaveChoices() => _settings.OwnVoiceAdditionalTargetLanguages = selected.Where(value => value != "None").ToList();
        refreshChoices = () =>
        {
            if (updating) return;
            updating = true;
            try
            {
                for (int index = 0; index < selected.Length; index++)
                    if (selected[index] == _settings.OwnVoiceTargetLanguage || selected.Take(index).Contains(selected[index])) selected[index] = "None";
                for (int index = 0; index < pickers.Length; index++)
                {
                    if (pickers[index] is null) continue;
                    pickers[index].ItemsSource = new[] { "None" }.Concat(AllowedTargetLanguages(false).Where(language => language != _settings.OwnVoiceTargetLanguage && !selected.Where((_, slot) => slot != index).Contains(language))).ToArray();
                    pickers[index].SelectedItem = selected[index];
                }
                SaveChoices();
            }
            finally { updating = false; }
        };
        for (int index = 0; index < pickers.Length; index++)
        {
            int slot = index;
            pickers[index] = Select(new[] { "None" }.Concat(AllowedTargetLanguages(false)), selected[index], value =>
            { if (updating) return; selected[slot] = value; refreshChoices(); });
        }
        // Creating a page is inert; normalize the picker display without changing saved choices.
        updating = true;
        for (int index = 0; index < pickers.Length; index++)
        {
            pickers[index].ItemsSource = new[] { "None" }.Concat(AllowedTargetLanguages(false).Where(language => language != _settings.OwnVoiceTargetLanguage && !selected.Where((_, slot) => slot != index).Contains(language))).ToArray();
            pickers[index].SelectedItem = selected[index];
        }
        updating = false;
        targets.Children.Add(Check("Translate my voice into more languages", _settings.OwnVoiceAdditionalLanguagesEnabled, value =>
        { _settings.OwnVoiceAdditionalLanguagesEnabled = value; extra.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }));
        extra.Children.Add(TwoFields(Field("Additional language 1", pickers[0]), Field("Additional language 2", pickers[1])));
        extra.Children.Add(Field("Additional language 3", pickers[2]));
        extra.Children.Add(ModernTheme.Label("Choose up to three more languages. Each translation uses a separate line. More languages can add delay and share VRChat's 144-character chat limit.", 12, ModernTheme.Muted));
        targets.Children.Add(extra);
        return targets;
    }

    private string TranslationAlias(bool incoming)
    {
        bool separateIncoming = incoming && !_settings.ShareTranslationModel;
        if (!_settings.UseManagedTranslator) return separateIncoming ? _settings.TranslatorModel : _settings.OwnTranslatorModel;
        string id = separateIncoming ? _settings.IncomingManagedModelId : _settings.OwnManagedModelId;
        return LocalTalkProvisioner.ModelCatalog.FirstOrDefault(model => model.Id == id)?.ApiAlias ?? id;
    }

    private IReadOnlyList<string> AllowedTargetLanguages(bool incoming) => TranslationModelCapabilities.SupportedTargets(
        TranslationAlias(incoming), incoming ? _settings.IncomingSourceLanguage : _settings.OwnVoiceSourceLanguage);

    private void AddSourceLanguagePicker(StackPanel panel, bool incoming, Action? choicesChanged = null)
    {
        string alias = TranslationAlias(incoming);
        bool explicitTranslation = TranslationModelCapabilities.RequiresExplicitSource(alias);
        if (incoming)
        {
            IEnumerable<string> sourceChoices = SubtitleSourceChoices;
            if (explicitTranslation) sourceChoices = sourceChoices.Where(value => value != "Automatic");
            if (TranslationModelCapabilities.IsLfmEnJp(alias)) sourceChoices = sourceChoices.Where(value => value is "English" or "Japanese");
            panel.Children.Add(Field("Spoken language · their voices", Select(sourceChoices, _settings.IncomingSourceLanguage, value =>
            {
                if (!CanEditSubtitleSpeech()) return;
                StageSubtitleSourceLanguage(_settings, value);
                choicesChanged?.Invoke();
                if (choicesChanged is null) Navigate(_page);
            })));
            var detail = ModernTheme.Label(SubtitleSpeechText.T(IncomingRecognitionCatalog.Find(_settings.IncomingSpeechEngine).Name), 12, ModernTheme.Muted, localize: false);
            panel.Children.Add(detail);
            panel.Children.Add(SubtitleSpeechButton("Speech model & language", (_, _) => Navigate("Listening")));
            if (explicitTranslation) panel.Children.Add(ModernTheme.Label("This model needs a spoken language. Automatic detection is unavailable.", 12, ModernTheme.Warning));
            return;
        }
        string[] languages = TranslationModelCapabilities.IsLfmEnJp(alias) ? ["English", "Japanese"] : Languages;
        if (!incoming) languages = languages.Where(OwnRecognitionCatalog.SupportedSourceLanguages.Contains).ToArray();
        string selected = incoming ? _settings.IncomingSourceLanguage : _settings.OwnVoiceSourceLanguage;
        bool legacyHint = !incoming && _settings.OwnRecognitionEngine == "voxtral";
        if (legacyHint && !explicitTranslation) languages = languages.Where(language => language == selected).ToArray();
        panel.Children.Add(Field(incoming ? "Spoken language · their voices" : legacyHint ? VoiceFirstText.T("Translation language hint") : "Spoken language · my voice",
            Select(new[] { "Automatic" }.Concat(languages), selected, value =>
            { if (incoming) _settings.IncomingSourceLanguage = value; else _settings.OwnVoiceSourceLanguage = value; choicesChanged?.Invoke(); })));
        if (explicitTranslation) panel.Children.Add(ModernTheme.Label("This model needs a spoken language. Automatic detection is unavailable.", 12, ModernTheme.Warning));
        if (!incoming && _settings.OwnRecognitionEngine == "voxtral")
            panel.Children.Add(VoiceFirstLabel("Voxtral detects the spoken language automatically. Choose Nemotron to set it yourself.", 12, ModernTheme.Muted));
        if (TranslationModelCapabilities.IsLfmEnJp(alias))
            panel.Children.Add(ModernTheme.Label("This model supports English and Japanese only.", 12, ModernTheme.Muted));
    }

    private StackPanel AdvancedPage()
    {
        var page = Page("Fine-tune translation, audio and troubleshooting. Most people can keep the recommended setup.");
        var options=new Grid();options.ColumnDefinitions.Add(new ColumnDefinition());options.ColumnDefinitions.Add(new ColumnDefinition());int index=0;
        foreach (var item in new[] {
            ("Translation", "Choose smaller or larger models, share one model, or use separate models."),
            ("Audio & timing", "Adjust sentence pauses, microphone mute behavior and subtitle layout."),
            ("Speaker options", "Choose a voice-recognition model and test overlapping-voice separation."),
            ("Models", "Manage downloads, import existing model files and choose a runtime."),
            ("Performance", "Inspect hardware recommendations and measure translation delay."),
            ("Diagnostics", "View logs, connection checks and details for troubleshooting."),
            ("Manual setup", "Reuse an existing speech server or translator and edit connection details.") })
        {
            string visibleName = PageDisplayName(item.Item1);
            var section = Section(visibleName, item.Item2);
            section.Children.Add(ModernTheme.Button(UiText.Format("Open {0}", UiText.Language == "en" ? visibleName.ToLowerInvariant() : UiText.T(visibleName)), (_, _) => Navigate(item.Item1)));
            int row=index/2,col=index%2;if(col==0)options.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var card=ModernTheme.Card(section);card.Margin=new Thickness(col==0?0:8,0,col==0?8:0,16);Grid.SetRow(card,row);Grid.SetColumn(card,col);options.Children.Add(card);index++;
        }
        page.Children.Add(options);
        return page;
    }

    private StackPanel TranslationPage()
    {
        var page = Page("Each direction has its own target language. Separate requests never share conversational text, even when they use one model.");
        var languageCard = ModernTheme.Card(LanguageSection()); page.Children.Add(languageCard);
        void RefreshLanguageChoices() { languageCard.Child = LanguageSection(); UiLocalization.ApplyTree(languageCard); }
        var models = Section("Translation models", "Sharing saves model residency and graphics memory. Running two models is optional, not a way to separate speakers, and can make gaming slower.");
        var incoming = ModelSelector(_settings.ShareTranslationModel ? _settings.OwnManagedModelId : _settings.IncomingManagedModelId, id => {if(!_settings.ShareTranslationModel)_settings.IncomingManagedModelId = id; RefreshLanguageChoices();});
        var shared = Check("Use the same model for both directions (recommended)", _settings.ShareTranslationModel, value => { _settings.ShareTranslationModel = value; incoming.IsEnabled = !value;incoming.SelectedItem=LocalTalkProvisioner.ModelCatalog.First(m=>m.Id==(value?_settings.OwnManagedModelId:_settings.IncomingManagedModelId)); RefreshLanguageChoices(); });
        incoming.IsEnabled = !_settings.ShareTranslationModel;
        models.Children.Add(shared);
        models.Children.Add(TwoFields(Field("My voice", ModelSelector(_settings.OwnManagedModelId, id => { _settings.OwnManagedModelId = id;if(_settings.ShareTranslationModel)incoming.SelectedItem=LocalTalkProvisioner.ModelCatalog.First(m=>m.Id==id); RefreshLanguageChoices(); })), Field("Other people", incoming)));
        models.Children.Add(ModernTheme.Button("Browse / download models", (_, _) => Navigate("Models")));
        page.Children.Add(ModernTheme.Card(models));
        var quality = Section("Output style", "Fast is concise and deterministic. Careful allows more output tokens, but is not a different or automatically better model.");
        quality.Children.Add(Select(new[] { "Fast", "Careful" }, _settings.TranslationQuality, value => _settings.TranslationQuality = value));
        quality.Children.Add(ModernTheme.Label("Optional glossary · one mapping per line", 13, ModernTheme.Muted));
        var glossary = Input(_settings.Glossary); glossary.AcceptsReturn = true; glossary.TextWrapping = TextWrapping.Wrap; glossary.MinHeight = 95; glossary.MaxLength = 2000;
        glossary.TextChanged += (_, _) => _settings.Glossary = glossary.Text; quality.Children.Add(glossary);
        quality.Children.Add(ModernTheme.Label("Example: 多力多滋 → Doritos. A glossary helps matching terms; it cannot recover a word the recognizer never heard.", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(quality)); return page;
    }

    private StackPanel AudioPage()
    {
        var page = Page("Choose when a sentence is finished and whether translations appear while you speak.");
        var timing = Section("Sentence timing", "The final translation uses the complete sentence. A longer pause helps avoid splitting a thought into separate messages.");
        timing.Children.Add(Check("Show translations while I speak", _settings.OwnVoiceSendEarlyPreviews, value => _settings.OwnVoiceSendEarlyPreviews = value));
        timing.Children.Add(ModernTheme.Label("Updates use the full sentence heard so far. Early wording may change. Turn this off to send only finished sentences.", 12, ModernTheme.Muted));
        timing.Children.Add(SliderField("My voice: silence before finalizing", _settings.OwnVoiceEndPauseMilliseconds, 900, 3500, 100, value => _settings.OwnVoiceEndPauseMilliseconds = (int)value, "ms"));
        timing.Children.Add(SliderField("Other voices: silence before finalizing", _settings.IncomingSettleMilliseconds, 600, 2400, 100, value => _settings.IncomingSettleMilliseconds = (int)value, "ms"));
        timing.Children.Add(SliderField("Subtitle preview stability", _settings.SubtitlePreviewStableMilliseconds, 150, 1200, 50, value => _settings.SubtitlePreviewStableMilliseconds = (int)value, "ms"));
        timing.Children.Add(ModernTheme.Label("Shorter waits show interim subtitles sooner. This does not send a message to VRChat.", 12, ModernTheme.Muted));
        timing.Children.Add(Field("Recognition lookahead", Select(new[] { 160, 240, 480, 800 }, _settings.RecognitionDelayMilliseconds, value => _settings.RecognitionDelayMilliseconds = value)));
        timing.Children.Add(ModernTheme.Label("More lookahead can improve recognition but adds delay. Finalization also includes translation time, not just the silence threshold.", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(timing));
        var capture = Section("Capture & mute", "The mute option on Home decides whether capture pauses with VRChat. When enabled, you can also choose whether already-captured speech should finish.");
        capture.Children.Add(Check("Finish and send already-captured speech after muting", _settings.FinishCapturedSentenceOnMute, value => _settings.FinishCapturedSentenceOnMute = value));
        capture.Children.Add(CreateHomeListeningButton());
        capture.Children.Add(Check("Boost quiet speech for recognition (does not change playback volume)", _settings.NormalizeQuietPlayback, value => _settings.NormalizeQuietPlayback = value));
        if (IncomingListening.SourceMode(_settings) == IncomingListening.DesktopMode && !_preview)
        {
            try { var devices = AudioRouting.Choices(); capture.Children.Add(Field("Playback device", Select(devices, devices.FirstOrDefault(d => d.Id == _settings.PlaybackDeviceId) ?? devices[0], value => _settings.PlaybackDeviceId = value.Id))); }
            catch (Exception error) { var message = ModernTheme.Label("", 12, ModernTheme.Warning); UiLocalization.SetFormat(message, "Audio device list unavailable: {0}", error.Message); capture.Children.Add(message); }
        }
        else if (IncomingListening.SourceMode(_settings) == IncomingListening.DesktopMode)
            capture.Children.Add(ModernTheme.Label("Playback: Automatic — follow VRChat (offline UI preview)", 13, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(capture));
        var overlay = Section("Overlay", "The overlay is optional, resizable and closable. Options always remain available in this dashboard.");
        overlay.Children.Add(Field("Layout", Select(new[] { "Sentence bubbles", "Per-speaker history" }, _settings.OverlayMode, value => _settings.OverlayMode = value)));
        overlay.Children.Add(SliderField("Visible history lines", _settings.HistoryLinesPerSpeaker, 3, 40, 1, value => _settings.HistoryLinesPerSpeaker = (int)value, "lines"));
        page.Children.Add(ModernTheme.Card(overlay)); return page;
    }

    private StackPanel SpeakersPage(bool advanced = false)
    {
        var page = Page(advanced ? "Adjust voice matching and manage recognition models." : "Keep familiar people, their photos and clear voice examples here.");
        if (!advanced)
        {
            if (!_preview && !_savedPeopleLoaded && !_savedPeopleLoading) _ = RefreshSavedPeopleAsync();
            page.Children.Add(SavedPeopleSection());
            var learning = Section("Voice recognition · optional", "A small CPU model matches voices to saved people. Speech-to-text and translation use separate models and work without person recognition.");
            _speakerRecognitionState = ModernTheme.Label(SpeakerRecognitionStateText(), 16, _settings.UseAiSpeakerRecognition ? ModernTheme.Good : ModernTheme.Muted);
            learning.Children.Add(_speakerRecognitionState);
            var recognitionActions=new WrapPanel();
            var recognitionToggle = ModernTheme.Button(_settings.UseAiSpeakerRecognition ? "Turn off recognition" : "Enable recognition",
                async (_, _) => await ChangeSpeakerRecognitionAsync(!_settings.UseAiSpeakerRecognition), !_settings.UseAiSpeakerRecognition);
            recognitionToggle.IsEnabled = !_settings.IncomingSinglePerson; recognitionActions.Children.Add(recognitionToggle);
            recognitionActions.Children.Add(ModernTheme.Button("More voice options", (_, _) => Navigate("Speaker options")));learning.Children.Add(recognitionActions);
            learning.Children.Add(ModernTheme.Label("Off releases the recognition model. Translation keeps working, and your saved people are kept.", 13, ModernTheme.Muted));
            learning.Children.Add(ModernTheme.Label("CPU time and memory are shared with your game. Turning person recognition off may improve performance.", 12, ModernTheme.Muted));
            var recognitionNotes=new StackPanel();recognitionNotes.Children.Add(ModernTheme.Label(_settings.UseAiSpeakerRecognition
                ? "If several people talk at once, the app may not know who spoke. Choose Music / recording or Unidentified voice when a message should not teach a voice."
                : "Sentences are shown without guessing who spoke. You can still assign a name by hand, but the app won't learn voice examples while recognition is off.", 13, ModernTheme.Muted));
            learning.Children.Add(new Expander {Header="How voice recognition works",Content=recognitionNotes});
            learning.Children.Add(ModernTheme.Label("Saved names, photos and voice examples stay on this device. They are not collected or uploaded. Audio recordings are not saved.", 12, ModernTheme.Muted));
            page.Children.Add(ModernTheme.Card(learning));
            return page;
        }
        var identity = Section("Recognize familiar voices", "Learn familiar voices from clear examples. Short, unclear or overlapping voices may remain unidentified.");
        var recognitionCheck = Check("Enable AI voice recognition", _settings.UseAiSpeakerRecognition, value => _ = ChangeSpeakerRecognitionAsync(value));
        recognitionCheck.IsEnabled = !_settings.IncomingSinglePerson; identity.Children.Add(recognitionCheck);
        if (_settings.IncomingSinglePerson) identity.Children.Add(ModernTheme.Label("Not needed for one person", 13, ModernTheme.Muted));
        SpeakerModelChoice selected = AppPaths.SpeakerModels.FirstOrDefault(choice => string.Equals(choice.Path, _settings.SpeakerModelPath, StringComparison.OrdinalIgnoreCase)) ?? AppPaths.SpeakerModels[0];
        var choice = Select(AppPaths.SpeakerModels, selected, value => _settings.SpeakerModelPath = value.Path);
        identity.Children.Add(Field("Voice recognition model", choice));
        identity.Children.Add(ModernTheme.Label("These models identify people, not words. They use CPU time and RAM, not GPU memory, and may affect game performance.", 12, ModernTheme.Muted));
        identity.Children.Add(ModernTheme.Label(File.Exists(_settings.SpeakerModelPath) ? "Selected model file is available" : "Selected model file is missing — download it or import one", 12, ModernTheme.Muted));
        var buttons = new WrapPanel();
        buttons.Children.Add(ModernTheme.Button("Download voice recognition model", async (_, _) => await DownloadFingerprintAsync((SpeakerModelChoice)choice.SelectedItem)));
        buttons.Children.Add(ModernTheme.Button("Use existing ONNX…", (_, _) => { var picker = new OpenFileDialog { Filter = UiText.T("Voice recognition model") + " (*.onnx)|*.onnx" }; if (picker.ShowDialog(this) == true) { _settings.SpeakerModelPath = picker.FileName; Navigate("Speaker options"); } }));
        buttons.Children.Add(ModernTheme.Button("Reset unnamed voices", async (_, _) => { if (!Confirm("Reset automatically learned unnamed voices for a new group? Saved people and their confirmed voice examples are kept, along with your models and settings.")) return; await RunTask(async token => await _runtime.ResetSpeakerProfilesAsync(token), "Reset unnamed voices"); }));
        identity.Children.Add(buttons);
        identity.Children.Add(SliderField("Unnamed voice profile limit", _settings.MaxSpeakerProfiles, 2, 8, 1, value => _settings.MaxSpeakerProfiles = (int)value, "voices"));
        identity.Children.Add(ModernTheme.Label("Saved people remain searchable beyond this unnamed-profile limit.", 12, ModernTheme.Muted));
        identity.Children.Add(SliderField("Match threshold", _settings.SpeakerMatchThreshold, .35, .85, .05, value => _settings.SpeakerMatchThreshold = value, "similarity"));
        page.Children.Add(ModernTheme.Card(identity));
        var separation = Section("Extract overlapping voices · experimental", OverlapModelCatalog.MossFormer2Int8.Description);
        separation.Children.Add(ModernTheme.Label("Real waveform separation is available for evaluation. Live overlapping-voice translation is NOT enabled.", 14, ModernTheme.Warning));
        separation.Children.Add(ModernTheme.Label(OverlapModelCatalog.LiveUnavailableReason, 12, ModernTheme.Muted));
        var actions = new WrapPanel();
        actions.Children.Add(ModernTheme.Button("Download optional separator · 91 MB", async (_, _) =>
        {
            if (!Confirm("Download the optional MossFormer2 INT8 ONNX model (91 MB) from Hugging Face? It does not enable live separation or load a GPU model.")) return;
            await RunTask(async token => await OverlapModelStore.DownloadAsync(AppPaths.Root, OverlapModelCatalog.MossFormer2Int8,
                progress => Dispatcher.InvokeAsync(() => Progress(progress.Message, progress.Fraction)), token), "Download separator");
        }));
        actions.Children.Add(ModernTheme.Button("Evaluate a local WAV…", async (_, _) => await EvaluateSeparationAsync()));
        var export=ModernTheme.Button("Export anonymous sources…",(_,_)=>ExportSeparatedSources()); export.IsEnabled=_lastSeparation is not null; actions.Children.Add(export);
        separation.Children.Add(actions); page.Children.Add(ModernTheme.Card(separation)); return page;
    }

    private StackPanel SavedPeopleSection()
    {
        _peopleView = new PeopleListView(_savedPeople, _preview || _savedPeopleLoaded, RuntimeProfiles.UsesSpeakerRecognition(_settings),
            async () => await EditSavedPersonAsync(),
            async () => await RunTask(async token => await ReadSavedPeopleAsync(token), "Refresh saved people"),
            async person => await EditSavedPersonAsync(person), async person => await DeleteSavedPersonAsync(person),
            async (person, remove) => await ChangePersonPhotoAsync(person, remove), _savedPeopleError);
        return _peopleView;
    }

    private string SpeakerRecognitionStateText() => _settings.IncomingSinglePerson ? "Not needed for one person"
        : !RuntimeProfiles.UsesSpeakerRecognition(_settings) ? "Off"
        : !_runtime.IncomingRunning ? "Enabled · starts with subtitles"
        : _runtime.RecognitionStatus.ModelLoaded ? "Running" : "Enabled · waiting for speech";

    private async Task ChangeSpeakerRecognitionAsync(bool enabled)
    {
        if (_preview) { UiLocalization.SetText(_status, "Offline preview: this action is disabled."); return; }
        AppSettings proposed = _settings.Copy(); proposed.UseAiSpeakerRecognition = enabled;
        if (RuntimeProfiles.UsesSpeakerRecognition(proposed) && !File.Exists(_settings.SpeakerModelPath))
        {
            UiLocalization.SetText(_status, "Download or choose a voice recognition model first, then enable recognition.");
            Navigate("Speaker options");
            return;
        }
        await RunTask(async token =>
        {
            bool previous = SettingsStore.Load().UseAiSpeakerRecognition;
            SettingsStore.SaveSpeakerRecognitionPreference(enabled);
            try
            {
                await _runtime.SetSpeakerRecognitionAsync(enabled, token);
                _settings.UseAiSpeakerRecognition = enabled;
            }
            catch
            {
                try
                {
                    SettingsStore.SaveSpeakerRecognitionPreference(previous);
                    await _runtime.SetSpeakerRecognitionAsync(previous);
                }
                catch (Exception error) { DesktopLog.Write("Speaker recognition preference rollback: " + error); }
                _settings.UseAiSpeakerRecognition = SettingsStore.Load().UseAiSpeakerRecognition;
                throw;
            }
        }, enabled ? "Enable voice recognition" : "Stop voice recognition",
            enabled ? "Voice recognition enabled. It starts with clear incoming speech." : "Voice recognition is off. Translation keeps working.");
        if (!_closing && (_page is "Speakers" or "Speaker options")) Navigate(_page);
    }

    private async Task ReadSavedPeopleAsync(CancellationToken token)
    {
        SpeakerFeedbackState state = await _runtime.GetSpeakerFeedbackStateAsync(token);
        token.ThrowIfCancellationRequested();
        _savedPeople = state.People; _savedPeopleLoaded = true; _savedPeopleError = null;
        _peopleView?.SetPeople(_savedPeople);
    }

    private async Task RefreshSavedPeopleAsync()
    {
        _savedPeopleLoading = true;
        _lastPeopleRefreshAt = DateTimeOffset.UtcNow;
        // Let the current page finish construction before a synchronous metadata read refreshes it.
        await Task.Yield();
        try { await ReadSavedPeopleAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { _savedPeopleLoaded = true; _savedPeopleError = "Saved people could not be read. Use Refresh people to try again."; }
        finally { _savedPeopleLoading = false; if (!_closing && _page == "Speakers") _peopleView?.SetPeople(_savedPeople, _savedPeopleLoaded, _savedPeopleError); }
    }

    private async Task EditSavedPersonAsync(SpeakerPerson? person = null)
    {
        if (_preview || _busy || _closing) return;
        Window dialog = SpeakerFeedbackUi.CreatePersonDialog(this, person);
        if (dialog.ShowDialog() != true || dialog.Tag is not string name) return;
        await RunTask(async token =>
        {
            await _runtime.SaveSpeakerPersonAsync(name, person?.Id, token);
            await ReadSavedPeopleAsync(token);
        }, person is null ? "Add saved person" : "Rename saved person");
    }

    private async Task DeleteSavedPersonAsync(SpeakerPerson person)
    {
        if (_preview || _busy || _closing || !Confirm(UiText.Format("Delete {0}?\nThis removes their saved name, photo and voice examples. Existing translations and other people are kept.",person.Name))) return;
        await RunTask(async token =>
        {
            await _runtime.DeleteSpeakerPersonAsync(person.Id, token);
            await ReadSavedPeopleAsync(token);
        }, "Delete saved person");
    }

    private async Task ChangePersonPhotoAsync(SpeakerPerson person, bool remove)
    {
        if (_preview || _busy || _closing) return;
        System.Windows.Media.Imaging.BitmapSource? edited = null;
        if (!remove)
        {
            var editor = new PersonPhotoEditor(this, person);
            if (editor.ShowDialog() != true || editor.Result is null) return;
            edited = editor.Result;
        }
        await RunTask(async token =>
        {
            using PersonAvatar.TemporaryPhoto? staged = edited is null ? null : PersonAvatar.StageEditedPhoto(edited);
            await _runtime.SetPersonPhotoAsync(person.Id, staged?.Path, token);
            await ReadSavedPeopleAsync(token);
        }, remove ? "Remove person's photo" : "Save person's photo");
    }

    private StackPanel ModelsPage()
    {
        var page = Page("Download a model, or reuse one you already have.");
        // Unshown UI checks must not inspect the real user's registry, WSL
        // registrations, or external model folders merely to render a page.
        ProvisionInventory inventory = _preview ? new([], [], [], []) : _provisioner.InspectLocal();
        foreach (ProvisionModelChoice model in LocalTalkProvisioner.ModelCatalog.OrderBy(model => model.Role == ProvisionModelRole.Speech ? 1 : model.ApiAlias.Contains("hy-mt", StringComparison.OrdinalIgnoreCase) ? 0 : 2))
        {
            var card = Section(model.Name, model.Role == ProvisionModelRole.Speech ? "Recognizes speech while someone is speaking"
                : model.Id == "hy-mt2-1.8b-q4_k_m" ? "Recommended starting translator for speed while gaming"
                : model.ApiAlias.Contains("hy-mt", StringComparison.OrdinalIgnoreCase) ? "Larger translator · uses more memory and may respond more slowly"
                : TranslationModelCapabilities.IsTranslateGemma(model.ApiAlias) ? "Dedicated translator · requires a spoken language"
                : TranslationModelCapabilities.IsLfmEnJp(model.ApiAlias) ? "Compact translator · English and Japanese only"
                : "Alternative general-purpose model");
            ProvisionInstalledModel? installed = inventory.InstalledModels.FirstOrDefault(item => item.Id == model.Id);
            card.Children.Add(ModernTheme.Label(installed is null ? UiText.Format("Not registered · {0:0.0} GB · {1}",model.SizeBytes / 1e9,model.License) : UiText.Format("Available · {0}",UiText.T(installed.IsImported ? "linked existing file" : "managed download")), 12, installed is null ? ModernTheme.Muted : ModernTheme.Good));
            var buttons = new WrapPanel();
            var download = ModernTheme.Button(installed is null ? "Download" : "Verify / repair download", async (_, _) => await DownloadModelAsync(model));
            buttons.Children.Add(download);
            buttons.Children.Add(ModernTheme.Button("Import existing GGUF…", async (_, _) => await ImportModelAsync(model)));
            if (model.Role == ProvisionModelRole.Translation)
            {
                buttons.Children.Add(ModernTheme.Button("Use for my voice", (_, _) =>
                { _settings.OwnManagedModelId = model.Id; _settings.UseManagedTranslator = true; if (TranslationModelCapabilities.RequiresExplicitSource(model.ApiAlias)) Navigate("Languages"); UiLocalization.SetText(_status, "Selection changed. Save settings to apply."); }));
                buttons.Children.Add(ModernTheme.Button("Use for subtitles", (_, _) =>
                { _settings.IncomingManagedModelId = model.Id; _settings.UseManagedTranslator = true; _settings.ShareTranslationModel = false; if (TranslationModelCapabilities.RequiresExplicitSource(model.ApiAlias)) Navigate("Languages"); UiLocalization.SetText(_status, "Selection changed. Save settings to apply."); }));
            }
            if (!string.IsNullOrWhiteSpace(model.SourceRepositoryUrl)) buttons.Children.Add(ModernTheme.Button("Model source", (_, _) => OpenModelLink(model.SourceRepositoryUrl)));
            buttons.Children.Add(ModernTheme.Button("Model terms", (_, _) => OpenModelLink(model.LicenseUrl)));
            if (!string.IsNullOrWhiteSpace(model.TermsNotice)) card.Children.Add(ModernTheme.Label(model.TermsNotice, 12, ModernTheme.Muted));
            card.Children.Add(buttons);
            if (installed is not null) card.Children.Add(ModernTheme.Label(installed.Path, 11, ModernTheme.Muted, localize:false));
            page.Children.Add(ModernTheme.Card(card));
        }
        var runtime = Section("Translation engine", "Vulkan supports NVIDIA, AMD and Intel graphics. CUDA needs a compatible NVIDIA driver. CPU mode may be too slow while gaming.");
        var selection = Select(LocalTalkProvisioner.RuntimeCatalog, LocalTalkProvisioner.RuntimeCatalog.FirstOrDefault(r => r.Id == _settings.ManagedRuntimeId) ?? LocalTalkProvisioner.RuntimeCatalog[0], r => _settings.ManagedRuntimeId = r.Id);
        runtime.Children.Add(selection);
        runtime.Children.Add(ModernTheme.Button("Install selected runtime", async (_, _) =>
        {
            var selected = (ProvisionRuntimeChoice)selection.SelectedItem;
            if (!Confirm(UiText.Format("Download and extract {0}?\nPinned runtime: llama.cpp {1}. No model will start.",selected.Name,LocalTalkProvisioner.LlamaVersion))) return;
            await RunTask(async token => await _provisioner.InstallRuntimeAsync(selected.Id, true, ProvisionProgress(), token), "Install translation runtime");
        }));
        runtime.Children.Add(ModernTheme.Label(UiText.Format("Installed: {0}",inventory.InstalledRuntimeIds.Count == 0 ? UiText.T("none") : string.Join(", ", inventory.InstalledRuntimeIds)), 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(runtime));
        if (inventory.ExistingModelCandidates.Count > 0)
        {
            var detected = Section("Existing model downloads", "These files were found in the standard LM Studio model directory. Pick the matching catalogue entry above and import it; files are linked, not copied.");
            foreach (string path in inventory.ExistingModelCandidates.Take(8)) detected.Children.Add(ModernTheme.Label(path, 11, ModernTheme.Muted, localize:false));
            page.Children.Add(ModernTheme.Card(detected));
        }
        return page;
    }

    private StackPanel PerformancePage()
    {
        var page = Page("Balance translation quality with smooth VRChat performance.");
        var advisor = Section("Find a starting model", "Check your graphics card and memory. Try the suggested model while playing before moving to a larger one.");
        advisor.Children.Add(ModernTheme.Label(_performanceReport, 13, ModernTheme.Muted, localize:false));
        advisor.Children.Add(ModernTheme.Button("Check hardware & recommend", async (_, _) => await RunTask(async token =>
        {
            _hardware = await HardwareAdvisor.DetectAsync(token);
            var options = new LocalAdvisorOptions { SpeechInstances = 2, TranslationInstances = _settings.ShareTranslationModel ? 1 : 2,
                LanguagePair = "auto-" + _settings.OwnVoiceTargetLanguage + "/" + _settings.IncomingTargetLanguage };
            var recommendation = HardwareAdvisor.Recommend(_hardware, options: options);
            _recommendedModelId = recommendation.ModelId;
            _performanceReport = $"{_hardware.CpuName}\n{_hardware.PhysicalRamMiB / 1024d:0.0} GB RAM\n" +
                string.Join("\n", _hardware.Gpus.Select(g => $"{g.Name} · {g.DedicatedVramMiB / 1024d:0.0} GB VRAM")) + "\n\n" + recommendation.Title + "\n" + recommendation.Explanation + "\n\n" +
                string.Join("\n", recommendation.Candidates.Select(c => c.ModelId + " · " + c.Status + " · " + c.Reason));
        }, "Hardware recommendation"), true));
        advisor.Children.Add(ModernTheme.Button("Use provisional starting selection", (_,_) => {
            if(_recommendedModelId is null) { UiLocalization.SetText(_status, "Check hardware first. No selection has been recommended yet."); return; }
            if(!Confirm("Use the provisional low-memory starting model for both directions? This is NOT a measured accuracy or speed recommendation. No model is downloaded or loaded. Save settings when ready.")) return;
            _settings.OwnManagedModelId=_settings.IncomingManagedModelId=_recommendedModelId; _settings.ShareTranslationModel=true;
            UiLocalization.SetText(_status, "Starting selection chosen. Review Translation and Models, then Save settings.");
        }));
        page.Children.Add(ModernTheme.Card(advisor));
        var benchmark = Section("Measure translation instead of guessing", "When a translator is already running, this sends a few neutral text examples to its local endpoint and measures translation-only latency. It does not record your microphone or count as an end-to-end gaming benchmark.");
        benchmark.Children.Add(ModernTheme.Button("Run local translation timing test", async (_, _) => await BenchmarkTranslationAsync()));
        benchmark.Children.Add(ModernTheme.Label("Review the translations with a fluent speaker. Recognition errors, overlapping voices, sentence timing and GPU contention need separate in-game testing before an automatic best-model choice is trustworthy.", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(benchmark)); return page;
    }

    private StackPanel DiagnosticsPage()
    {
        var page = Page("Local startup, capture, reconnection and provider errors are readable here. Logs may contain recognized or translated speech; review them before sharing.");
        page.Children.Add(CreateRecentIssuesCard());
        var panel = Section("Troubleshooting", "Choose a log to see recent activity and errors.");
        string[] names = ["saywhat.log", "incoming-translator.log", "foxtrans-local-host.log", "foxtrans-mute-sync.log", "foxtrans-osc-sentence.log", "foxtrans-provider-errors.log"];
        if(File.Exists(Path.Combine(AppPaths.Root,"localtalk.log"))) names = [..names,"localtalk.log"]; // Retain access to pre-rename diagnostics.
        panel.Children.Add(Select(names, _logName, name => { _logName = name; RefreshLog(); }));
        _logView = Input(""); _logView.IsReadOnly = true; _logView.AcceptsReturn = true; _logView.TextWrapping = TextWrapping.Wrap;
        _logView.Height = 330; _logView.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _logView.FontFamily = new FontFamily("Cascadia Mono"); _logView.FontSize = 11;
        panel.Children.Add(_logView);
        var actions = new WrapPanel(); actions.Children.Add(ModernTheme.Button("Refresh", (_, _) => RefreshLog()));
        actions.Children.Add(ModernTheme.Button("Export current log…", (_, _) => ExportLog()));
        actions.Children.Add(ModernTheme.Button("Local health checks", async (_, _) => await HealthChecksAsync()));
        panel.Children.Add(actions); page.Children.Add(ModernTheme.Card(panel));
        var help = Section("Quick checks", "Connection refused: speech/runtime is stopped or the port differs. 503: the speech server may be busy. Audio stalled: inspect capture status and queue age, not just HTTP health.");
        help.Children.Add(ModernTheme.Label("Own speech 127.0.0.1:8080 · incoming speech 127.0.0.1:8081 · managed translation 1235 / 1236 · final OSC output 127.0.0.1:9000", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(help)); RefreshLog(); return page;
    }

    private StackPanel SetupPage()
    {
        var page = Page(_settings.FirstRunSetupCompleted ? "Choose languages or a translator, then save your changes." : "Choose translation languages, then prepare the required files.");
        _setupChoicesPanel = page; page.IsEnabled = !_busy;
        var languages=new StackPanel();languages.Children.Add(StepHeading("01","Choose your languages",ModernTheme.Accent));
        void RefreshLanguages()
        {
            while (languages.Children.Count > 1) languages.Children.RemoveAt(1);
            var choices = LanguageSection(includeAdditionalLanguages: false, choicesChanged: () => RefreshSetupAction());
            choices.Children.RemoveAt(0); languages.Children.Add(choices); UiLocalization.ApplyTree(languages);
        }
        RefreshLanguages();
        page.Children.Add(ModernTheme.Card(languages));
        var setup = new StackPanel();setup.Children.Add(StepHeading("02",_settings.FirstRunSetupCompleted?"Translation setup":"Prepare translation",ModernTheme.Good));
        if (!_settings.FirstRunSetupCompleted)
            setup.Children.Add(ModernTheme.Label("We'll download and prepare the translation tools. No separate translation app to install or open.",14,ModernTheme.Muted));
        var modelOptions = new StackPanel();
        modelOptions.Children.Add(ModernTheme.Label("Choosing one of these translators uses it for both your voice and subtitles.", 12, ModernTheme.Muted));
        var profiles = new[] { new SetupModelChoice("hy-mt2-1.8b-q4_k_m", "Fast · recommended", "Hy-MT2 1.8B · smaller download and more room for your game"),
            new SetupModelChoice("hy-mt2-7b-q4_k_m", "Larger translator", "Hy-MT2 7B · may improve translations, but can take longer") };
        foreach (SetupModelChoice profile in profiles)
        {
            var radio = new RadioButton { Content = profile.Title, GroupName = "SetupTranslationProfile", IsChecked = _settings.UseManagedTranslator && _settings.ShareTranslationModel && _settings.OwnManagedModelId == profile.Id,
                Foreground = ModernTheme.Text, Margin = new Thickness(0, 8, 0, 2) };
            radio.Checked += (_, _) =>
            {
                _settings.OwnManagedModelId = _settings.IncomingManagedModelId = profile.Id;
                _settings.ShareTranslationModel = true; _settings.UseManagedTranslator = true;
                _setupMessage = null; RefreshLanguages(); RefreshSetupAction();
            };
            modelOptions.Children.Add(radio); modelOptions.Children.Add(ModernTheme.Label(profile.Description, 12, ModernTheme.Muted));
        }
        setup.Children.Add(new Expander { Header = "Choose a translator", Content = modelOptions, IsExpanded = _settings.FirstRunSetupCompleted,
            Foreground = ModernTheme.Text, Margin = new Thickness(0, 10, 0, 10) });
        _setupChoiceStatus = ModernTheme.Label("", 13, ModernTheme.Accent);
        _setupActionHint = ModernTheme.Label("", 12, ModernTheme.Muted);
        setup.Children.Add(_setupChoiceStatus); setup.Children.Add(_setupActionHint);
        var action = ModernTheme.Button("Save & apply", async (_, _) =>
        { if (_settings.FirstRunSetupCompleted) await ApplySetupChangesAsync(); else await RecommendedSetupAsync(); }, true);
        action.IsEnabled = !_busy; _setupActionButton = action; setup.Children.Add(action);
        RefreshSetupAction();
        if (_settings.FirstRunSetupCompleted)
            setup.Children.Add(ModernTheme.Label(_runtime.OwnRunning || _runtime.IncomingRunning
                ? "Active translation will briefly pause and restart when you apply changes."
                : "Saving does not start translation. Choose Start on Home when you are ready.", 12, ModernTheme.Muted));
        if (!_settings.FirstRunSetupCompleted && !string.IsNullOrWhiteSpace(_setupMessage))
            setup.Children.Add(ModernTheme.Label(_setupMessage, 13, ModernTheme.Accent));
        var otherActions = new WrapPanel();
        if (_settings.FirstRunSetupCompleted)
            otherActions.Children.Add(ModernTheme.Button("Check & repair setup", async (_, _) => await RecommendedSetupAsync()));
        otherActions.Children.Add(ModernTheme.Button("Manual setup", (_, _) => Navigate("Manual setup")));
        otherActions.Children.Add(ModernTheme.Button("Troubleshooting", (_, _) => Navigate("Diagnostics")));
        setup.Children.Add(new Expander { Header = "Other setup options", Content = otherActions, Foreground = ModernTheme.Muted, Margin = new Thickness(0, 10, 0, 8) });
        if (!_settings.FirstRunSetupCompleted)
            setup.Children.Add(ModernTheme.Label("Then choose Start on Home. Nothing listens or loads a model before that.", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(setup));
        return page;
    }

    private void RefreshSetupAction(bool? needsDownload = null)
    {
        if (_setupActionButton is null || _setupChoiceStatus is null || _setupActionHint is null) return;
        if (!_settings.FirstRunSetupCompleted)
        {
            bool continuing = File.Exists(Path.Combine(AppPaths.Root, "setup-progress.json"));
            UiLocalization.Bind(_setupActionButton, ContentControl.ContentProperty, continuing ? "Continue setup" : "Set up SayWhat?");
            _setupActionButton.IsEnabled = !_busy;
            _setupChoiceStatus.Visibility = _setupActionHint.Visibility = Visibility.Collapsed;
            RefreshSaveState();
            return;
        }
        bool changed = SetupSelection.Capture(_settings) != _savedSetupSelection;
        bool download;
        try { download = needsDownload ?? new TranslationSelectionPreparation(_provisioner).NeedsDownload(_settings); }
        catch (SetupUserException error)
        {
            UiLocalization.Bind(_setupActionButton, ContentControl.ContentProperty, "Save & apply");
            _setupActionButton.IsEnabled = false;
            UiLocalization.Bind(_saveButton, ContentControl.ContentProperty, "Save & apply"); _saveButton.IsEnabled = false;
            UiLocalization.SetText(_setupChoiceStatus, "Prepare selected translator");
            UiLocalization.SetText(_setupActionHint, error.Message);
            _setupChoiceStatus.Visibility = _setupActionHint.Visibility = Visibility.Visible;
            RefreshSaveState();
            return;
        }
        UiLocalization.Bind(_setupActionButton, ContentControl.ContentProperty, download ? "Download & apply" : "Save & apply");
        _setupActionButton.IsEnabled = !_busy && (changed || download);
        UiLocalization.Bind(_saveButton, ContentControl.ContentProperty, download ? "Download & apply" : "Save & apply");
        _saveButton.IsEnabled = _setupActionButton.IsEnabled;
        UiLocalization.SetText(_setupChoiceStatus, changed ? "Unsaved changes" : download ? "Prepare selected translator" : "Saved");
        UiLocalization.SetText(_setupActionHint, download ? "Required translation files will be downloaded before your choices are saved."
            : changed ? "Save to use these choices. Returning Home does not save them." : "Your choices are saved. You can return to Home.");
        _setupChoiceStatus.Visibility = _setupActionHint.Visibility = Visibility.Visible;
        RefreshSaveState();
    }

    private async Task ApplySetupChangesAsync()
    {
        if (_preview || _busy || _closing) return;
        var preparation = new TranslationSelectionPreparation(_provisioner);
        AppSettings choices = _settings.Copy();
        try
        {
            _ = RuntimeController.BuildPlan(choices, incoming: false);
            _ = RuntimeController.BuildPlan(choices, incoming: true);
            RuntimeProfiles.ValidateIncomingSpeech(choices);
        }
        catch (Exception error) { Error("Apply setup choices", error); return; }
        bool download = preparation.NeedsDownload(choices);
        if (!download && !SubtitleSpeechFilesMissing(choices) && SetupSelection.Capture(choices) == _savedSetupSelection) return;
        bool own = _runtime.OwnRunning, incoming = _runtime.IncomingRunning;
        if ((own || incoming) && !Confirm("Apply your changes? Translation will briefly pause, and an unfinished sentence may be lost.")) return;
        foreach (string id in preparation.MissingModelIds(choices))
        {
            ProvisionModelChoice model = LocalTalkProvisioner.ModelCatalog.Single(model => model.Id == id);
            if (model.RequiresTermsAcceptance && CreateModelTermsDialog(model).ShowDialog() != true) return;
        }
        if (download)
        {
            long bytes = preparation.MissingDownloadBytes(choices);
            if (!Confirm(UiText.Format("Download required translation files ({0:0.0} GB) and apply these choices?", bytes / 1e9))) return;
        }
        // Download before pausing active tools, and save only after all files are ready.
        // This path never repairs/rebuilds speech recognition or starts an idle model.
        await RunTask(async token =>
        {
            if (download) TaskStage("Prepare selected translator", "Only translation files are prepared. Speech recognition is not reinstalled.");
            await preparation.PrepareAsync(choices, ProvisionProgress(), token, allowDownload: download);
            await PrepareSubtitleSpeechFilesAsync(choices, token);
            await EnsureGroupSpeechReadyForProfileAsync(choices, token);
            token.ThrowIfCancellationRequested();
            await ApplySettingsCoreAsync(own, incoming, token);
            _setupMessage = own || incoming ? "Settings applied. Translation restarted." : "Changes saved. You can start translation on Home.";
        }, "Applying settings", own || incoming ? "Settings applied. Translation restarted." : "Changes saved. You can start translation on Home.");
    }
    private sealed record SetupModelChoice(string Id, string Title, string Description);
    private static FrameworkElement StepHeading(string number,string text,Brush accent)
    {
        var row=new DockPanel {Margin=new Thickness(0,0,0,14)};
        var badge=ModernTheme.Chip(number,accent);badge.Margin=new Thickness(0,0,12,0);badge.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(badge);
        var heading=ModernTheme.Heading(text,23);heading.Margin=new Thickness(0);heading.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(heading);return row;
    }

    private async Task RecommendedSetupAsync()
    {
        string? outcome = null;
        await RunTask(async token =>
        {
            TaskStage("Checking setup requirements");
            var setup = new AutomaticSetupService(_provisioner);
            AutomaticSetupPlan plan = await setup.PlanAsync(_settings.Copy(), token);
            token.ThrowIfCancellationRequested();
            foreach (ProvisionModelChoice model in SetupModelsRequiringTerms(_settings))
            {
                if (CreateModelTermsDialog(model).ShowDialog() != true)
                { outcome = "Setup cancelled. Your changes are still unsaved."; return; }
                token.ThrowIfCancellationRequested();
            }
            if (CreateSetupConsentDialog(plan).ShowDialog() != true) { outcome = "Setup cancelled. Your changes are still unsaved."; return; }
            token.ThrowIfCancellationRequested();
            if (_runtime.OwnRunning || _runtime.IncomingRunning) await StopForTaskAsync();
            token.ThrowIfCancellationRequested();
            // Remember languages before a prerequisite/restart, but not completion.
            Persist();
            AutomaticSetupResult result = await setup.RunAsync(_settings, true, ProvisionProgress(), token);
            _setupMessage = outcome = result.Message;
            if (result.Completed)
            {
                _settings.FirstRunSetupCompleted = true; Persist(); Navigate("Home");
            }
            else { Persist(); Navigate("Setup"); }
        }, "Setting up SayWhat?");
        if (outcome is not null) UiLocalization.SetText(_status, outcome);
    }

    internal static IReadOnlyList<ProvisionModelChoice> SetupModelsRequiringTerms(AppSettings settings) => AutomaticSetupService.UsesBuiltInTranslation(settings)
        ? AutomaticSetupService.TranslationIds(settings).Select(id => LocalTalkProvisioner.ModelCatalog.First(model => model.Id == id))
            .Where(model => model.RequiresTermsAcceptance).ToArray()
        : Array.Empty<ProvisionModelChoice>();

    internal Window CreateSetupConsentDialog(AutomaticSetupPlan plan)
    {
        var dialog = new Window { Owner = this, Title = "Set up SayWhat?", Icon = BrandArtwork.WindowIcon, Width = 550, MinWidth = 420,
            MaxHeight = 760, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ModernTheme.Background, Foreground = ModernTheme.Text, FontFamily = new FontFamily("Segoe UI"),
            Topmost = false, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        var body = Section("Confirm setup", UiLocalization.SetupSummary(plan.Summary));
        if (plan.DownloadBytes > 0) body.Children.Add(ModernTheme.Label(UiText.Format("Estimated model/tool downloads: {0} GB", (plan.DownloadBytes / 1e9).ToString("0.0")), 14));
        if (plan.RequiresAdministrator) body.Children.Add(ModernTheme.Label("Windows will ask you to approve any system changes.", 13, ModernTheme.Muted));
        if (plan.RestartMayBeNeeded) body.Children.Add(ModernTheme.Label("A restart may be needed. Your choices will be saved so you can continue afterward.", 13, ModernTheme.Muted));
        body.Children.Add(ModernTheme.Label("You can cancel setup. Finished downloads are kept for next time. Translation will not start until you choose Start.", 13, ModernTheme.Muted));
        var actions = new WrapPanel();
        actions.Children.Add(ModernTheme.Button("Set up now", (_, _) => dialog.DialogResult = true, true));
        var cancel = ModernTheme.Button("Not now", (_, _) => dialog.DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        body.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = body, Padding = new Thickness(26), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UiLocalization.ApplyTree(dialog);
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog;
    }

    private StackPanel ManualSetupPage()
    {
        var page = Page("One dashboard manages the tools. Translation runs natively on Windows; realtime Voxtral currently still requires WSL2. Installing Windows features may require a restart.");
        page.Children.Add(ModernTheme.Card(FirstRunModelSection()));
        var translation = Section("1 · Local translation runtime", "The managed Windows runtime does not require Bionic / LM Studio. Download a Hy-MT2 model here or import an existing compatible GGUF; model loading only happens when you press Start.");
        translation.Children.Add(Check("Use the built-in translation runtime", _settings.UseManagedTranslator, value => _settings.UseManagedTranslator = value));
        translation.Children.Add(ModernTheme.Button("Models & runtimes", (_, _) => Navigate("Models")));
        translation.Children.Add(ModernTheme.Label("Advanced: existing local OpenAI-compatible endpoints", 13, ModernTheme.Muted));
        var ownEndpoint = Input(_settings.OwnTranslatorBaseUrl); ownEndpoint.TextChanged += (_, _) => _settings.OwnTranslatorBaseUrl = ownEndpoint.Text.Trim();
        var incomingEndpoint = Input(_settings.TranslatorBaseUrl); incomingEndpoint.TextChanged += (_, _) => _settings.TranslatorBaseUrl = incomingEndpoint.Text.Trim();
        translation.Children.Add(TwoFields(Field("My voice endpoint", ownEndpoint), Field("Other voices endpoint", incomingEndpoint)));
        var ownModel = Input(_settings.OwnTranslatorModel); ownModel.TextChanged += (_, _) => _settings.OwnTranslatorModel = ownModel.Text.Trim();
        var incomingModel = Input(_settings.TranslatorModel); incomingModel.TextChanged += (_, _) => _settings.TranslatorModel = incomingModel.Text.Trim();
        translation.Children.Add(TwoFields(Field("Existing endpoint model ID · my voice", ownModel), Field("Existing endpoint model ID · others", incomingModel)));
        translation.Children.Add(ModernTheme.Label("Local HTTP loopback addresses only. In managed mode these are assigned automatically; external mode does not launch or unload another app's models.", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(translation));
        var speech = Section("2 · Realtime speech engine", "Existing users can register the installed WSL server. Fresh installations can install WSL and build the pinned speech runtime here, with separate confirmations.");
        WslSpeechRegistration? registered = _provisioner.SpeechRegistration;
        speech.Children.Add(ModernTheme.Label(registered is null ? "No speech engine registered" : $"{registered.Distribution} · {registered.Backend}\n{registered.ServerPath}\n{registered.ModelPath}", 12, registered is null ? ModernTheme.Warning : ModernTheme.Good));
        ProvisionInventory inventory = _preview ? new([], [], [], []) : _provisioner.InspectLocal();
        var distro = Select(inventory.WslDistributions.Count > 0 ? inventory.WslDistributions : new[] { "Ubuntu-24.04" }, registered?.Distribution ?? inventory.WslDistributions.FirstOrDefault() ?? "Ubuntu-24.04", _ => { });
        speech.Children.Add(Field("WSL distribution", distro));
        var server = Input(registered?.ServerPath ?? "/opt/voxtral.cpp/build-server-cuda3/voxtral-server");
        var model = Input(registered?.ModelPath ?? "/opt/voxtral.cpp/models/voxtral/Q4_K_M.gguf");
        speech.Children.Add(Field("Existing Linux server path", server)); speech.Children.Add(Field("Existing realtime GGUF path", model));
        var backend = Select(new[] { "cuda", "vulkan", "cpu" }, registered?.Backend ?? "cuda", _ => { }); speech.Children.Add(Field("Speech backend", backend));
        var actions = new WrapPanel();
        actions.Children.Add(ModernTheme.Button("Register existing engine", async (_, _) => await RunTask(async token =>
            await _provisioner.RegisterWslSpeechAsync(distro.SelectedItem.ToString()!, server.Text, model.Text, backend.SelectedItem.ToString()!, token), "Register speech engine")));
        actions.Children.Add(ModernTheme.Button("Build speech runtime…", async (_, _) =>
        {
            if (!Confirm("Build the pinned Voxtral runtime in WSL? This installs Ubuntu build packages; CUDA can additionally download a multi-GB toolkit. It uses one build job.\n\nFirst import/download the realtime model in Models. No model will load, and no automatic CPU fallback occurs.")) return;
            await RunTask(async token => await _provisioner.BootstrapWslSpeechAsync(distro.SelectedItem.ToString()!, backend.SelectedItem.ToString()!, true,
                WindowsAutomaticSetupPlatform.SpeechBuildProgress(ProvisionProgress()), token), "Build speech runtime");
        }));
        actions.Children.Add(ModernTheme.Button("Install WSL / Ubuntu…", (_, _) =>
        {
            if (!Confirm("Ask Windows to install WSL and Ubuntu 24.04? Administrator approval and a restart may be required. No automatic reboot or model start.")) return;
            try { Process.Start(LocalTalkProvisioner.BuildWslFeatureInstallInfo(true)); UiLocalization.SetText(_status, "Windows WSL setup requested. Complete any Windows prompt, then reopen Setup after restarting if needed."); }
            catch (Exception error) { Error("Windows setup", error); }
        })); speech.Children.Add(actions); page.Children.Add(ModernTheme.Card(speech));
        var privacy = Section("3 · Privacy & control", "Models may be downloaded from GitHub / Hugging Face after confirmation. Speech and translations use loopback-local endpoints only. Opening the dashboard never records audio or loads a model.");
        var dataLocation = ModernTheme.Label("", 12, ModernTheme.Muted);
        UiLocalization.SetFormat(dataLocation, "Data: {0}\nInstaller does not remove downloaded models or user logs on uninstall.", AppPaths.Root);
        privacy.Children.Add(dataLocation);
        privacy.Children.Add(ModernTheme.Button(_settings.FirstRunSetupCompleted ? "Save setup & return Home" : "Finish manual setup", async (_, _) => await CompleteFirstRunSetupAsync(), true));
        privacy.Children.Add(ModernTheme.Label("Finishing setup only saves your choices. It does not start translation, audio capture or a timing test.", 12, ModernTheme.Muted));
        page.Children.Add(ModernTheme.Card(privacy)); return page;
    }

    private StackPanel FirstRunModelSection()
    {
        var guide = Section(_settings.FirstRunSetupCompleted ? "Choose speed or quality" : "Welcome · a fast starting point", "Start with Hy-MT2 1.8B while gaming, even on a large GPU. Try 7B later if you prefer a larger translation model and can spare the extra memory. These are starting choices, not a measured speed or accuracy guarantee.");
        ProvisionModelChoice[] models = LocalTalkProvisioner.ModelCatalog.Where(model => model.Id is "hy-mt2-1.8b-q4_k_m" or "hy-mt2-7b-q4_k_m").ToArray();
        var choice = Select(models, models.First(model => model.Id == _setupTranslationModelId), model => _setupTranslationModelId = model.Id);
        guide.Children.Add(Field("Suggested translation model", choice));
        guide.Children.Add(ModernTheme.Label("1.8B · speed-first · about 1.1 GB download\n7B · larger quality option · about 4.6 GB download; more memory and possible extra game latency", 13, ModernTheme.Muted));
        ProvisionModelChoice? saved = LocalTalkProvisioner.ModelCatalog.FirstOrDefault(model => model.Id == _settings.OwnManagedModelId);
        var currentChoice = ModernTheme.Label("", 12, ModernTheme.Accent);
        UiLocalization.SetFormat(currentChoice, "Current choice: {0}{1}", saved?.Name ?? _settings.OwnManagedModelId,
            new UiLocalization.Phrase(_settings.ShareTranslationModel ? " · shared" : " · separate incoming choice kept"));
        guide.Children.Add(currentChoice);
        var actions = new WrapPanel();
        actions.Children.Add(ModernTheme.Button("Choose for both directions", (_, _) =>
        {
            _settings.UseManagedTranslator = true; _settings.ShareTranslationModel = true;
            _settings.OwnManagedModelId = _settings.IncomingManagedModelId = _setupTranslationModelId;
            UiLocalization.SetText(_status, "Shared model selected. Download or import it, then Finish setup to save. Nothing was started.");
            Navigate("Manual setup");
        }, true));
        actions.Children.Add(ModernTheme.Button("Download selected model…", async (_, _) => await DownloadModelAsync((ProvisionModelChoice)choice.SelectedItem)));
        actions.Children.Add(ModernTheme.Button("Import existing GGUF…", async (_, _) => await ImportModelAsync((ProvisionModelChoice)choice.SelectedItem)));
        guide.Children.Add(actions);
        guide.Children.Add(ModernTheme.Label("Downloading does not switch either model automatically. Choose for both directions is explicit; advanced separate-model choices remain available in Translation.", 12, ModernTheme.Muted));
        return guide;
    }

    private async Task CompleteFirstRunSetupAsync()
    {
        if (_preview || _busy || _closing) return;
        if (_provisioner.SpeechRegistration is null)
        { UiLocalization.SetText(_status, "Complete step 2: register an existing realtime speech engine, or build it in WSL first."); return; }
        if (_settings.UseManagedTranslator)
        {
            string incomingId = _settings.ShareTranslationModel ? _settings.OwnManagedModelId : _settings.IncomingManagedModelId;
            if (_provisioner.ResolveModelPath(_settings.OwnManagedModelId) is null || _provisioner.ResolveModelPath(incomingId) is null)
            { UiLocalization.SetText(_status, "Download or import the selected translation model(s) before finishing setup. Nothing was started."); return; }
            if (!_provisioner.InspectLocal().InstalledRuntimeIds.Contains(_settings.ManagedRuntimeId))
            { UiLocalization.SetText(_status, "Install the selected native translation runtime from Models before finishing setup."); return; }
            if (LocalTalkProvisioner.CheckNativeRuntimePrerequisite() is string prerequisite)
            { UiLocalization.SetText(_status, prerequisite); return; }
        }
        bool active = _runtime.OwnRunning || _runtime.IncomingRunning;
        if (active && !Confirm("Save setup choices and stop the active translation tools? Nothing restarts automatically; unfinished sentences may be discarded.")) return;
        await RunTask(async token =>
        {
            if (active) await StopForTaskAsync();
            token.ThrowIfCancellationRequested();
            TaskStage("Saving your settings");
            bool previous = _settings.FirstRunSetupCompleted;
            _settings.FirstRunSetupCompleted = true;
            try { Persist(); }
            catch { _settings.FirstRunSetupCompleted = previous; throw; }
            Navigate("Home");
        }, "Finish setup", "Setup choices saved. Choose Start on Home when you are ready.");
    }

    private void Persist(bool applyingProfile = false, bool applySpeakerRecognition = false)
    {
        AppSettings latest = SettingsStore.Load();
        if (!applyingProfile)
        {
            _settings.SubtitlesAlwaysOnTop = latest.SubtitlesAlwaysOnTop;
            if (!applySpeakerRecognition) _settings.UseAiSpeakerRecognition = latest.UseAiSpeakerRecognition;
        }
        _settings.DetectAnySourceLanguage = _settings.IncomingSourceLanguage == "Automatic";
        if (!_settings.DetectAnySourceLanguage) _settings.SourceLanguages = [_settings.IncomingSourceLanguage];
        if (_settings.UseManagedTranslator)
        {
            ProvisionModelChoice own = LocalTalkProvisioner.ModelCatalog.First(m => m.Id == _settings.OwnManagedModelId && m.Role == ProvisionModelRole.Translation);
            ProvisionModelChoice incoming = LocalTalkProvisioner.ModelCatalog.First(m => m.Id == (_settings.ShareTranslationModel ? own.Id : _settings.IncomingManagedModelId) && m.Role == ProvisionModelRole.Translation);
            _settings.OwnTranslatorBaseUrl = "http://127.0.0.1:1235/v1"; _settings.TranslatorBaseUrl = _settings.ShareTranslationModel ? "http://127.0.0.1:1235/v1" : "http://127.0.0.1:1236/v1";
            _settings.OwnTranslatorModel = own.ApiAlias; _settings.TranslatorModel = incoming.ApiAlias;
        }
        else
        {
            ValidateEndpoint(_settings.OwnTranslatorBaseUrl); ValidateEndpoint(_settings.TranslatorBaseUrl);
            if (string.IsNullOrWhiteSpace(_settings.OwnTranslatorModel) || string.IsNullOrWhiteSpace(_settings.TranslatorModel)) throw new InvalidOperationException("Choose a model ID for each existing endpoint.");
            if (_settings.ShareTranslationModel) { _settings.TranslatorModel = _settings.OwnTranslatorModel; _settings.TranslatorBaseUrl = _settings.OwnTranslatorBaseUrl; }
        }
        if (applyingProfile) SettingsStore.SaveTranslationProfileSelection(_settings);
        else if (applySpeakerRecognition) SettingsStore.SaveExplicitSpeakerRecognitionSelection(_settings);
        else SettingsStore.Save(_settings);
        DesktopLog.Write("Settings saved. Targets: my voice=" + string.Join("/",_settings.EffectiveOwnVoiceTargetLanguages()) + "; incoming=" + _settings.IncomingTargetLanguage + "; shared=" + _settings.ShareTranslationModel);
        _savedSetupSelection = SetupSelection.Capture(_settings);
        RefreshSaveState(reloadSaved: true);
    }

    private async Task SaveAsync()
    {
        if (_preview || _busy || _closing) return;
        if (_page == "Settings" && _translationProfilesPicker?.SelectedItem is TranslationProfileUiChoice profile
            && profile.Id != _settings.ActiveTranslationProfileId)
        { await ApplyTranslationProfileAsync(profile.Id); return; }
        // Setup can have unchanged choices but missing files: its explicit download
        // action is different from a no-op settings save.
        if (_page == "Setup" && _settings.FirstRunSetupCompleted) { await ApplySetupChangesAsync(); return; }
        if (TranslationProfiles.ConfigurationEquals(_settings, SettingsStore.Load()))
        {
            UiLocalization.SetText(_status, "No changes to apply.");
            RefreshTranslationProfilesUi();
            RefreshSaveState(reloadSaved: true);
            return;
        }
        await ApplyTranslationSettingsAsync(_settings.Copy(), applyingProfile: false,
            applySpeakerRecognition: _saveStateAppliedSettings is not null &&
                _settings.UseAiSpeakerRecognition != _saveStateAppliedSettings.UseAiSpeakerRecognition);
    }

    private async Task ApplySettingsCoreAsync(bool own, bool incoming, CancellationToken token, bool applyingProfile = false, AppSettings? previousSettings = null, bool applySpeakerRecognition = false)
    {
        AppSettings previous = previousSettings?.Copy() ?? SettingsStore.Load();
        SettingsRuntimeChanges changes = SettingsRuntimeChanges.Between(previous, _settings,
            includeIncoming: incoming || IncomingCaptureCapabilities.SupportsSource(_settings, SubtitleWindowsVersion));
        bool restartOwn = own && changes.OwnChanged;
        bool applyIncoming = incoming && changes.IncomingChanged;
        bool sharedRestart = own && incoming && changes.SharedTranslatorChanged;
        int step = 0, count = 1 + (sharedRestart ? 1 : restartOwn ? 1 : 0) + (restartOwn || sharedRestart ? 1 : 0) + (applyIncoming || sharedRestart ? 1 : 0);
        bool ownStoppedByApply = false;
        try
        {
            if (sharedRestart)
            {
                ownStoppedByApply = true;
                await StopForTaskAsync(++step, count);
            }
            else if (restartOwn)
            {
                ownStoppedByApply = true;
                TaskStage("Stopping your voice translator", null, ++step, count);
                await _runtime.StopOwnAsync();
            }
            token.ThrowIfCancellationRequested();
            TaskStage("Saving your settings", null, ++step, count);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            token.ThrowIfCancellationRequested(); Persist(applyingProfile, applySpeakerRecognition);
            if (applyIncoming && !sharedRestart)
                await _runtime.SwitchIncomingProfileAsync(_settings.Copy(), OpenOptions, token, RuntimeTaskProgress(++step, count), OpenSpeakerSettings, ApplyTranslationProfileAsync);
            if (restartOwn || sharedRestart)
                await _runtime.StartOwnAsync(_settings.Copy(), token, RuntimeTaskProgress(++step, count));
            if (sharedRestart)
                await _runtime.StartIncomingAsync(_settings.Copy(), OpenOptions, token, RuntimeTaskProgress(++step, count), OpenSpeakerSettings, ApplyTranslationProfileAsync);
            ApplySelectedProfileAppearance();
        }
        catch (Exception applyError)
        {
            if (ownStoppedByApply && !_runtime.OwnRunning && applyError is not OperationCanceledException
                && !token.IsCancellationRequested && !_closing && !_lifetime.IsCancellationRequested)
            {
                try
                {
                    // Incoming switch recovery may already have restored its old
                    // full settings snapshot. Use what is actually saved, not the
                    // failed draft, when bringing back this previously-running tool.
                    AppSettings restored = SettingsStore.Load();
                    TaskStage("Restoring your voice translation", "A settings change could not finish. Restoring the voice translator that was running.");
                    using var recovery = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
                    recovery.CancelAfter(TimeSpan.FromSeconds(45));
                    await _runtime.StartOwnAsync(restored.Copy(), recovery.Token, ProvisionProgress());
                }
                catch (Exception recoveryError)
                {
                    throw new AggregateException("Settings could not be applied and your voice translator could not be restored. See Diagnostics for both errors.", applyError, recoveryError);
                }
            }
            throw;
        }
    }

    private async Task StopForTaskAsync(int? stepIndex = null, int? stepCount = null)
    {
        _finishingSafeShutdown = true;
        TaskStage("Stopping current translations", null, stepIndex, stepCount);
        try { await _runtime.StopAllAsync(CreateTaskProgress(stepIndex, stepCount)); }
        finally { _finishingSafeShutdown = false; }
    }

    private async Task RunTask(Func<CancellationToken, Task> action, string name, string? completedMessage = null, bool refreshView = true)
    {
        if (_preview) { UiLocalization.SetText(_status, "Offline preview: this action is disabled."); return; }
        if (_busy) return; // Keep the real active stage visible if another action is clicked.
        _busy = true; using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation;
        CloseHomeTargetLanguagePicker();
        Interlocked.Increment(ref _taskGeneration);
        if (_setupActionButton is not null) _setupActionButton.IsEnabled = false;
        if (_setupChoicesPanel is not null) _setupChoicesPanel.IsEnabled = false;
        _content.IsEnabled = false;
        _operationStartedAt = _lastProgressAt = DateTimeOffset.UtcNow;
        _currentProgress = new(name, name);
        RefreshSaveState(); _stopButton.Visibility = Visibility.Visible;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operationFinished = finished;
        _taskTroubleshooting.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Collapsed; _progress.IsIndeterminate = false;
        _progressDetail.Visibility = Visibility.Collapsed; _progressTiming.Visibility = Visibility.Visible;
        _cancel.Visibility=Visibility.Visible; UiLocalization.Bind(_cancel,ContentControl.ContentProperty,"Cancel task"); _cancel.IsEnabled = true; UiLocalization.SetText(_status, UiText.T(name) + "…");
        RefreshProgressTiming();
        _taskTimer.Start();
        try { await action(operation.Token); UiLocalization.SetText(_status, completedMessage ?? UiText.Format("{0} completed.", UiText.T(name))); }
        catch (OperationCanceledException) { UiLocalization.SetText(_status, UiText.Format("{0} cancelled.", UiText.T(name))); }
        catch (Exception error) { Error(name, error); }
        finally
        {
            try
            {
                _busy = false; _operation = null; _currentProgress = null;
                _taskTimer.Stop();
                _finishingSafeShutdown = false;
                _content.IsEnabled = true;
                _taskTroubleshooting.Visibility = Visibility.Collapsed;
                _progress.Visibility = _progressDetail.Visibility = _progressTiming.Visibility = Visibility.Collapsed;
                _cancel.Visibility=Visibility.Collapsed; if (!_closing && refreshView) Navigate(_page);
                RefreshSaveState(reloadSaved: true);
            }
            finally { finished.TrySetResult(); }
        }
    }

    private async Task EmergencyStopAsync()
    {
        if(_preview) return;
        _operation?.Cancel();
        try
        {
            if (_busy) { await _runtime.StopAllAsync(ProvisionProgress()); return; }
            await RunTask(async _ => await StopForTaskAsync(), "Stop all", "Translation stopped.");
        }
        catch(Exception error) {Error("Stop all",error);}
    }

    private async Task DownloadModelAsync(ProvisionModelChoice model)
    {
        if (_preview) return;
        if (model.RequiresTermsAcceptance)
        { if (CreateModelTermsDialog(model).ShowDialog() != true) return; }
        else if (!Confirm(UiText.Format("Download {0}?\nSize: {1:0.0} GB. Source: Hugging Face. License: {2}.\nThe file is verified and stored locally. Nothing loads onto the GPU.",model.Name,model.SizeBytes / 1e9,model.License))) return;
        await RunTask(async token => await _provisioner.DownloadModelAsync(model.Id, true, ProvisionProgress(), token), "Download model");
    }

    private static void OpenModelLink(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    internal Window CreateModelTermsDialog(ProvisionModelChoice model)
    {
        var dialog = new Window { Owner = _preview ? null : this, Title = UiText.T("Model terms"), Icon = BrandArtwork.WindowIcon,
            Width = 610, Height = 600, MinWidth = 540, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ModernTheme.Background, Foreground = ModernTheme.Text, Topmost = false, FontFamily = FontFamily };
        dialog.SourceInitialized += (_, _) => AppearanceTheme.ApplyWindowChrome(dialog);
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(ModernTheme.Heading("Review model terms", 24));
        content.Children.Add(ModernTheme.Label(model.Name, 16, localize: false));
        content.Children.Add(ModernTheme.Label(model.TermsNotice, 13, ModernTheme.Muted));
        content.Children.Add(ModernTheme.Label(UiText.Format("Download · {0:0.0} GB · {1}", model.SizeBytes / 1e9, model.License), 13, ModernTheme.Muted));
        var links = new WrapPanel();
        links.Children.Add(ModernTheme.Button("Model terms", (_, _) => OpenModelLink(model.LicenseUrl)));
        links.Children.Add(ModernTheme.Button("Model source", (_, _) => OpenModelLink(model.SourceRepositoryUrl))); content.Children.Add(links);
        var accept = Check("I have reviewed and agree to this model's terms.", false, _ => { }); content.Children.Add(accept);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        var download = ModernTheme.Button("Download", (_, _) => dialog.DialogResult = true, true); download.IsEnabled = false;
        accept.Checked += (_, _) => download.IsEnabled = true; accept.Unchecked += (_, _) => download.IsEnabled = false;
        var cancel = ModernTheme.Button("Cancel", (_, _) => dialog.DialogResult = false); cancel.IsCancel = true;
        actions.Children.Add(download); actions.Children.Add(cancel); content.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UiLocalization.ApplyTree(dialog); return dialog;
    }

    private async Task ImportModelAsync(ProvisionModelChoice model)
    {
        if (_preview) return;
        if (TranslationModelCapabilities.RequiresExplicitSource(model.ApiAlias) &&
            !Confirm("Import only the matching translation model. A compatible GGUF architecture does not prove that it is this translator or preserves its expected template. Do not select a general Gemma or LFM model. Continue?")) return;
        var picker = new OpenFileDialog { Title = UiText.Format("Choose the matching {0}",model.Name), Filter = "GGUF model (*.gguf)|*.gguf" };
        if (picker.ShowDialog(this) == true) await RunTask(async token => { TaskStage("Inspecting the selected model"); await _provisioner.RegisterExistingModelAsync(model.Id, picker.FileName, token); }, "Import existing model");
    }

    private async Task DownloadFingerprintAsync(SpeakerModelChoice model)
    {
        if (!Confirm("Download " + model.Name + " from sherpa-onnx's GitHub release? It is an optional CPU voice-identification model, not a speech recognizer or separator.")) return;
        await RunTask(async token =>
        {
            Directory.CreateDirectory(AppPaths.ModelsDirectory);
            string temporary = model.Path + ".download";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            long expected = response.Content.Headers.ContentLength ?? 0;
            await using Stream input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; long received = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0) { await output.WriteAsync(buffer.AsMemory(0, count), token); received += count; Progress("Downloading voice recognition model", expected > 0 ? received / (double)expected : null); }
                if (received < 1_000_000 || expected > 0 && received != expected) throw new InvalidDataException("Fingerprint model download was incomplete.");
            }
            File.Move(temporary, model.Path, true); _settings.SpeakerModelPath = model.Path;
        }, "Download voice recognition model");
    }

    private async Task EvaluateSeparationAsync()
    {
        var picker = new OpenFileDialog { Filter = "16 kHz WAV sample (*.wav)|*.wav", Title = "Choose a 1–6 second sample of two overlapping voices" };
        if (picker.ShowDialog(this) != true) return;
        if (!Confirm("Evaluate this local sample with the optional separator? This explicitly loads the CPU model (two threads), runs a warm-up plus three measured passes, then unloads it. No microphone recording, GPU load or live translation.")) return;
        await RunTask(async token =>
        {
            string path = OverlapModelStore.GetModelPath(AppPaths.Root);
            await OverlapModelStore.VerifyAsync(path, OverlapModelCatalog.MossFormer2Int8, token);
            float[] sample = SeparationWaveSample.ReadMono16k(picker.FileName);
            using var service = new OverlapSeparationService(); await service.LoadAsync(path, token);
            SeparationEvaluation evaluation = await service.EvaluateAsync(sample, cancellationToken: token);
            _lastSeparation=evaluation.LastResult;
            DesktopLog.Write("Separator evaluation: " + evaluation.Summary);
            AppearanceTheme.ShowMessage(this, evaluation.Summary + "\n\nTiming alone does not establish useful Mandarin separation. Output slots are not persistent speaker identities. Live separation is still disabled.", "Separation evaluation", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "Evaluate separator");
    }

    private async Task BenchmarkTranslationAsync()
    {
        if (!_runtime.OwnRunning && !_runtime.IncomingRunning) { UiLocalization.SetText(_status, "Start a translation tool first. This test does not automatically load a model."); return; }
        if (!Confirm("Send four neutral text samples to the already-running local translator? This temporarily adds translation work. It measures translation-only latency and is not an in-game performance or quality guarantee.")) return;
        await RunTask(async token =>
        {
            string endpoint = _runtime.OwnRunning ? _settings.OwnTranslatorBaseUrl : _settings.TranslatorBaseUrl;
            string model = _runtime.OwnRunning ? _settings.OwnTranslatorModel : _settings.TranslatorModel;
            string target = _runtime.OwnRunning ? _settings.OwnVoiceTargetLanguage : _settings.IncomingTargetLanguage;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var results = new StringBuilder(); var timings = new List<double>();
            bool specialized = TranslationModelCapabilities.RequiresExplicitSource(model);
            string sampleLanguage = target == "English" ? TranslationModelCapabilities.IsLfmEnJp(model) ? "Japanese" : "Chinese" : "English";
            string[] samples = specialized ? sampleLanguage switch {
                "Japanese" => ["テストです。ちゃんと動いていますか？", "もう帰ろうと思っていたけど、あと10分はいられます。", "もう一度言ってくれる？最後の部分が聞こえなかった。", "このチップスは少し辛いけど、おいしいね。"],
                "Chinese" => ["测试，测试。能正常工作吗？", "我本来打算离开，但还可以再待十分钟。", "你能再说一遍吗？我没听清最后那一部分。", "这些多力多滋有点辣，但是味道很好。"],
                _ => ["Testing, testing. Is this working?", "I was going to leave, but I can stay for another ten minutes.", "Could you repeat that? I missed the last part.", "These chips are a little spicy, but they taste good."] }
                : ["Testing, testing. Is this working?", "I was going to leave, but I can stay for another ten minutes.", "Could you repeat that? I missed the last part.", "这些多力多滋有点辣，但是味道很好。"];
            if (TranslationModelCapabilities.Validate(model, sampleLanguage, new[] { target }) is { } invalid) throw new ModelSelectionUserException(invalid);
            foreach (string source in samples)
            {
                var timer = Stopwatch.StartNew();
                bool dedicated=TranslationModelPrompts.IsHyMt(model);
                object request;
                if (specialized)
                {
                    var task = TranslationModelPrompts.SpecializedTask(model, sampleLanguage, target, source);
                    var body = new Dictionary<string, object> { ["model"] = model, ["max_tokens"] = 192, ["temperature"] = task.Temperature, ["top_p"] = task.TopP,
                        ["messages"] = task.Messages.Select(message => new { role = message.Role, content = message.Content }).ToArray() };
                    if (task.MinP is double minP) body["min_p"] = minP;
                    if (task.RepeatPenalty is double penalty) body["repeat_penalty"] = penalty;
                    request = body;
                }
                else request = new { model, temperature = dedicated ? 0.7 : 0.0, max_tokens = 192,
                    messages = dedicated ? new[] { new { role="user",content=TranslationModelPrompts.HyMtUserMessage(source,target,_settings.Glossary) } } : new[] { new { role = "system", content = "Translate into natural " + SettingsStore.TargetDescription(target) + ". Output only the translation. /no_think" }, new { role = "user", content = source } },
                    top_p=dedicated?0.6:1.0,top_k=dedicated?20:40,repeat_penalty=dedicated?1.05:1.0,
                    chat_template_kwargs = new { enable_thinking = false } };
                using var response = await http.PostAsJsonAsync(endpoint.TrimEnd('/') + "/chat/completions", request, token);
                response.EnsureSuccessStatusCode(); using var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); timer.Stop();
                string output = responseBody.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                timings.Add(timer.Elapsed.TotalMilliseconds); results.AppendLine($"{timer.Elapsed.TotalSeconds:0.00}s  {source}\n→ {output}\n");
            }
            string report = $"Translation-only test · {model} → {target}\nMedian: {timings.Order().ElementAt(timings.Count / 2):0}ms; slowest: {timings.Max():0}ms\nNot an end-to-end / alongside-game benchmark. Quality review required.\n\n{results}";
            _performanceReport = report; DesktopLog.Write(report); AppearanceTheme.ShowMessage(this, report, "Local translation timing", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "Local translation test");
    }

    private async Task HealthChecksAsync() => await RunTask(async token =>
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string ownHealth = _settings.OwnRecognitionEngine == "voxtral" ? "http://127.0.0.1:8080/health" : "http://127.0.0.1:8080/ready";
        foreach (string url in new[] { ownHealth, "http://127.0.0.1:8081/health", _settings.OwnTranslatorBaseUrl.TrimEnd('/') + "/models", _settings.TranslatorBaseUrl.TrimEnd('/') + "/models" }.Distinct())
        {
            try { using var response = await http.GetAsync(url, token); DesktopLog.Write($"Health {url} → {(int)response.StatusCode} {response.ReasonPhrase}"); }
            catch (HttpRequestException error) { DesktopLog.Write("Health " + url + " → unavailable: " + error.Message); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { DesktopLog.Write("Health " + url + " → timed out"); }
        }
    }, "Local health checks");

    private void RefreshLog()
    {
        if (_logView is null) return;
        string path = Path.Combine(AppPaths.Root, _logName);
        try
        {
            if (!File.Exists(path)) { _logView.Text = "No entries yet. Starting a tool or running a health check creates its log."; return; }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 128_000) file.Seek(-128_000, SeekOrigin.End);
            using var reader = new StreamReader(file); string text = reader.ReadToEnd();
            if (_logView.Text != text) { _logView.Text = text; _logView.ScrollToEnd(); }
        }
        catch (IOException error) { _logView.Text = "Log is temporarily unavailable: " + error.Message; }
    }

    private void ExportLog()
    {
        string path = Path.Combine(AppPaths.Root, _logName);
        if (!File.Exists(path)) { UiLocalization.SetText(_status, "No log file exists yet."); return; }
        if (!Confirm("Logs may contain spoken and translated text. Review them before sending to anyone. Export the current log?")) return;
        var picker = new SaveFileDialog { FileName = _logName, Filter = "Log file (*.log)|*.log" };
        if (picker.ShowDialog(this) == true) try { File.Copy(path, picker.FileName, true); UiLocalization.SetText(_status, "Log exported."); } catch (Exception error) { Error("Export log", error); }
    }

    private void ExportSeparatedSources()
    {
        if(_lastSeparation is null) return;
        var picker=new SaveFileDialog {Title="Choose a base filename for the two anonymous audio sources",FileName="separation.wav",Filter="WAV audio (*.wav)|*.wav"};
        if(picker.ShowDialog(this)!=true) return;
        string directory=Path.GetDirectoryName(picker.FileName)!; string stem=Path.GetFileNameWithoutExtension(picker.FileName);
        string first=Path.Combine(directory,stem+"-source-a.wav"),second=Path.Combine(directory,stem+"-source-b.wav");
        if((File.Exists(first)||File.Exists(second))&&!Confirm("One of the generated source files already exists. Replace these files?\n"+first+"\n"+second)) return;
        try {
            Write(first,_lastSeparation.Source0); Write(second,_lastSeparation.Source1);
            UiLocalization.SetText(_status, "Exported source A / B for listening review. These are anonymous slots, NOT learned speaker identities.");
        } catch(Exception error) {Error("Export separation",error);}
        static void Write(string path,float[] samples) {using var writer=new NAudio.Wave.WaveFileWriter(path,NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(16000,1));writer.WriteSamples(samples,0,samples.Length);}
    }

    private void OpenOptions() { Show(); WindowState = WindowState.Normal; Activate(); Navigate("Languages"); }
    private void OpenSpeakerSettings() { Show(); WindowState = WindowState.Normal; Activate(); NavigateToSpeakerSettings(); }

    // A deep link from subtitle help must reveal the actual control, not leave the
    // reader at the theme controls at the top of this scrollable page. Kept separate
    // from Show/Activate so inert UI checks can exercise navigation without a window.
    internal void NavigateToSpeakerSettings()
    {
        Navigate("Settings");
        FrameworkElement? target = _speakerRecognitionSettingsSection;
        object pageContent = _content.Content;
        if (target is null) return;
        void Reveal()
        {
            // Do not scroll a different page if the user navigates away before layout.
            if (_closing || _page != "Settings" || !ReferenceEquals(_content.Content, pageContent)) return;
            _content.UpdateLayout();
            target.BringIntoView();
        }
        Reveal();
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(Reveal));
    }
    private void RuntimeChanged()
    {
        int generation = Volatile.Read(ref _taskGeneration);
        if (_closing || _busy) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_closing || _busy || generation != Volatile.Read(ref _taskGeneration)) return;
            UiLocalization.SetFormat(_status,"My voice: {0} · subtitles: {1}",new UiLocalization.Phrase(_runtime.OwnRunning ? "on" : "off"),new UiLocalization.Phrase(_runtime.IncomingRunning ? "on" : "off"));
            if (_page is "Home" or "Listening" or "Settings") Navigate(_page);
            RefreshSaveState(reloadSaved: true);
        });
    }
    private void Error(string action, Exception error)
    {
        UiLocalization.SetText(_status, UserFacingErrors.Describe(error));
        DesktopLog.Write("APP ISSUE code=operation-failed; cause=" + error.GetType().Name);
        DesktopLog.Write(action + " failed: " + error);
    }
    private bool Confirm(string message) => !_preview && AppearanceTheme.ShowMessage(this, UiText.T(message), AppBrand.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void TaskStage(string message, string? detail = null, int? stepIndex = null, int? stepCount = null) =>
        UpdateProvisionProgress(new(message, message) { Detail = detail, StepIndex = stepIndex, StepCount = stepCount });
    private void Progress(string message, double? fraction) => UpdateProvisionProgress(new(message, message) { WorkFraction = fraction });
    private void UpdateProvisionProgress(ProvisionProgress value)
    {
        if (!_busy || _closing) return;
        if (_setupActionButton is not null) _setupActionButton.IsEnabled = false;
        if (_setupChoicesPanel is not null) _setupChoicesPanel.IsEnabled = false;
        if (_currentProgress != value) _lastProgressAt = DateTimeOffset.UtcNow;
        _currentProgress = value; _saveButton.IsEnabled = false;
        RenderTaskProgress();
        RefreshProgressTiming();
    }
    private void RenderTaskProgress()
    {
        if (!_busy || _currentProgress is null) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SetupProgressText text = SetupProgressPresentation.Format(_currentProgress, now - _operationStartedAt, now - _lastProgressAt);
        UiLocalization.SetText(_status, text.Title);
        _progressDetail.Text = text.Detail;
        _progressDetail.Visibility = string.IsNullOrWhiteSpace(text.Detail) ? Visibility.Collapsed : Visibility.Visible;
        // Never animate an invented percentage. Unknown work is explained by its phase and activity.
        _progress.IsIndeterminate = false;
        _progress.Value = text.Fraction is double fraction ? fraction * 100 : 0;
        _progress.Visibility = text.Fraction is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RefreshProgressTiming()
    {
        if (!_busy || _currentProgress is null) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RenderTaskProgress();
        _progressTiming.Text = SetupProgressPresentation.Format(_currentProgress, now - _operationStartedAt, now - _lastProgressAt).Timing;
        _progressTiming.Visibility = Visibility.Visible;
        _taskTroubleshooting.Visibility = now - _lastProgressAt >= TimeSpan.FromSeconds(30) ? Visibility.Visible : Visibility.Collapsed;
        bool cancelling = _operation?.IsCancellationRequested == true;
        UiLocalization.Bind(_cancel,ContentControl.ContentProperty,cancelling ? (_finishingSafeShutdown ? "Finishing safe shutdown…" : "Cancelling…") : "Cancel task"); _cancel.IsEnabled = !cancelling;
    }
    private IProgress<ProvisionProgress> ProvisionProgress()
        => CreateTaskProgress(null, null);
    private IProgress<ProvisionProgress> RuntimeTaskProgress(int stepIndex, int stepCount)
        => CreateTaskProgress(stepIndex, stepCount);
    private IProgress<ProvisionProgress> CreateTaskProgress(int? stepIndex, int? stepCount)
    {
        CancellationTokenSource? operation = _operation;
        return new Progress<ProvisionProgress>(value =>
        {
            if (!ReferenceEquals(operation, _operation)) return;
            // An async engine can enqueue its last stage just before the next numbered
            // phase starts. Do not let that callback move Save & apply backwards.
            if (stepIndex is int step && _currentProgress is { StepIndex: int currentStep } current && current.StepCount == stepCount && currentStep > step) return;
            UpdateProvisionProgress(stepIndex is null ? value : value with { StepIndex = stepIndex, StepCount = stepCount });
        });
    }
    private static void ValidateEndpoint(string value) { if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback || uri.Scheme != "http") throw new InvalidOperationException("Use a local HTTP loopback endpoint; cloud endpoints are not supported."); }
    private void AddBadge(StackPanel panel, string name, string text) { var label = ModernTheme.Label(text=="RUNNING"?"● Translating":"○ Ready to start", 11, text == "RUNNING" ? ModernTheme.Good : ModernTheme.Muted); _badges[name] = label; panel.Children.Add(label); }
    private static StackPanel Page(string description) { var page = new StackPanel(); if(!string.IsNullOrWhiteSpace(description)){var intro=ModernTheme.Label(description,14,ModernTheme.Muted);intro.Background=ModernTheme.Background;intro.Margin=new Thickness(0,0,0,20);page.Children.Add(intro);}return page; }
    private static StackPanel Section(string title, string description) { var panel = new StackPanel(); panel.Children.Add(ModernTheme.Heading(title,21)); var copy=ModernTheme.Label(description,13,ModernTheme.Muted);copy.Margin=new Thickness(0,0,0,14);panel.Children.Add(copy);return panel; }
    private static TextBox Input(string value) => new() { Text = value, Margin = new Thickness(0, 0, 0, 10), MinHeight = 40 };
    private static StackPanel Field(string name, FrameworkElement control) { var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) }; var label=ModernTheme.Label(name,12,ModernTheme.Muted);panel.Children.Add(label);System.Windows.Automation.AutomationProperties.SetName(control,name);System.Windows.Automation.AutomationProperties.SetLabeledBy(control,label);panel.Children.Add(control);return panel; }
    private static Grid TwoFields(FrameworkElement left, FrameworkElement right) { var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition()); left.Margin = new Thickness(0, 0, 12, 0); right.Margin = new Thickness(12, 0, 0, 0); grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid; }
    private static ComboBox Select<T>(IEnumerable<T> choices, T selected, Action<T> changed) { var combo = new ComboBox { ItemsSource = choices.ToArray(), SelectedItem = selected, Margin = new Thickness(0, 0, 0, 8) }; if(typeof(T)==typeof(string)||typeof(T)==typeof(ProvisionModelChoice))UiLocalization.TranslateOptionLabels(combo); combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is T item) changed(item); }; return combo; }
    private static CheckBox Check(string name, bool value, Action<bool> changed) { var check = new CheckBox { Content = name, IsChecked = value }; check.Checked += (_, _) => changed(true); check.Unchecked += (_, _) => changed(false); return check; }
    private static FrameworkElement SliderField(string title, double value, double min, double max, double tick, Action<double> changed, string unit)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) }; var label = ModernTheme.Label(UiText.T(title) + $" · {value:0.##} " + UiText.T(unit), 13); panel.Children.Add(label);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = tick, IsSnapToTickEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(slider,title);System.Windows.Automation.AutomationProperties.SetLabeledBy(slider,label);
        slider.ValueChanged += (_, _) => { changed(slider.Value); label.Text = UiText.T(title) + $" · {slider.Value:0.##} " + UiText.T(unit); }; panel.Children.Add(slider); return panel;
    }
    private static ComboBox ModelSelector(string selected, Action<string> changed) { var choices = LocalTalkProvisioner.ModelCatalog.Where(m => m.Role == ProvisionModelRole.Translation).OrderBy(m => m.ApiAlias.Contains("hy-mt", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray(); return Select(choices, choices.FirstOrDefault(m => m.Id == selected) ?? choices.First(m => m.Id == "hy-mt2-1.8b-q4_k_m"), model => changed(model.Id)); }
}

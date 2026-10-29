using System.IO;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace IncomingTranslator;

internal static class Program
{
    [STAThread]
    public static void Main(string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] == "--self-test")
        {
            SmokeTests.Run(arguments[1]);
            return;
        }
        if (arguments.Length == 2 && arguments[0] == "--test-server-recovery")
        {
            SmokeTests.RunServerRecovery(arguments[1]);
            return;
        }
        using var mutex = new Mutex(true, "Local\\VRChatIncomingTranslator", out bool first);
        if (!first) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new SubtitleWindow());
    }
}

internal sealed partial class SubtitleWindow : Window
{
    // Optional packaged-app artwork keeps this shared source independent of the
    // dashboard project. A standalone overlay simply uses its existing text header.
    internal static Func<ImageSource?>? WindowIconProvider { get; set; }
    internal static Func<double, Image?>? HeaderMarkProvider { get; set; }
    internal static Func<string, string>? TranslateUiProvider { get; set; }
    internal static Func<string, object[], string>? FormatUiProvider { get; set; }
    internal static Action<DependencyObject>? LocalizeUiProvider { get; set; }
    internal static Action<DependencyObject>? MarkLiteralUiProvider { get; set; }
    private static string Ui(string value) => TranslateUiProvider?.Invoke(value) ?? value;
    private static string UiFormat(string format, params object[] values) => FormatUiProvider?.Invoke(format, values) ?? string.Format(format, values);
    // Stable shared brushes update controls AND existing conversation history in place.
    // AppearanceTheme is shared with the standalone build, without dashboard dependencies.
    private static Brush OverlayBackground => AppearanceTheme.Background;
    private static Brush OverlayPanel => AppearanceTheme.Surface;
    private static Brush OverlayText => AppearanceTheme.Text;
    private static Brush OverlayMuted => AppearanceTheme.Muted;
    private static Brush OverlayStroke => AppearanceTheme.Stroke;
    private static Brush OverlayCoral => AppearanceTheme.Warning;
    private static Brush OverlaySage => AppearanceTheme.Good;
    private static readonly FontFamily ConversationFont = new("Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic");
    internal Action? OpenOptionsRequested { get; set; }
    internal Action? OpenSpeakerSettingsRequested { get; set; }
    internal Func<Window, FrameworkElement, ISpeakerFeedbackHost, TranslationDisplay, Task<SpeakerFeedbackResult?>>? CorrectSpeakerRequested { get; set; }
    internal Func<Window, ISpeakerFeedbackHost, string, bool, Task<bool>>? EnsureSpeakerLearningRequested { get; set; }
    internal ISpeakerFeedbackHost? SpeakerFeedbackHost => _service;
    internal IncomingHealthSnapshot? HealthSnapshot => _service?.HealthSnapshot;
    internal SpeakerRecognitionStatus RecognitionStatus => _service?.RecognitionStatus ?? new(
        _settings.UseAiSpeakerRecognition && !_settings.IncomingSinglePerson, false,
        _settings.UseAiSpeakerRecognition && !_settings.IncomingSinglePerson ? "Enabled · starts with subtitles" : "Off");

    internal async Task<SpeakerRecognitionStatus> ApplySpeakerRecognitionAsync(bool enabled, CancellationToken token = default)
    {
        IncomingTranslationService? service = await Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (_shutdown.IsCancellationRequested) throw new ObjectDisposedException(nameof(SubtitleWindow));
            // If Loaded has not created its service yet, change the settings atomically on the
            // owning dispatcher so that the later constructor sees the requested preference.
            if (_service is null)
            {
                AppSettings next = _settings.Copy(); next.UseAiSpeakerRecognition = enabled; _settings = next;
            }
            return _service;
        }).Task.ConfigureAwait(false);
        bool effective = enabled && !_settings.IncomingSinglePerson;
        SpeakerRecognitionStatus status = service is null
            ? new(effective, false, effective ? "Enabled · starts with subtitles" : "Off")
            : await service.ApplySpeakerRecognitionAsync(enabled, token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_shutdown.IsCancellationRequested) throw new ObjectDisposedException(nameof(SubtitleWindow));
            AppSettings next = _settings.Copy(); next.UseAiSpeakerRecognition = enabled; _settings = next;
        }).Task.ConfigureAwait(false);
        return status;
    }
    private readonly TextBlock _status;
    private readonly TextBlock _subtitleHeading;
    private string _lastStatus = "Starting subtitles…";
    private bool _receivedSource;
    private readonly TextBlock _source;
    private readonly TextBlock _outgoingState;
    private readonly TextBlock _playbackWarning;
    private readonly Button _alwaysOnTopButton;
    private readonly Button _quickGuideButton;
    private readonly SubtitleQuickGuide _quickGuide;
    private readonly Button _maximizeButton;
    private readonly Border _dragHeader;
    private bool _headerDragPending;
    private Point _headerDragOrigin;
    private readonly TextBlock _pinStatus;
    private DateTimeOffset _lastPlaybackWarningProbe;
    private readonly System.Windows.Threading.DispatcherTimer _outgoingStateTimer;
    private readonly StackPanel _speakerRows;
    private readonly ScrollViewer _speakerScroll;
    private readonly Dictionary<string, SpeakerCard> _cards = new();
    private readonly List<TranslationDisplay> _displayHistory = new();
    private readonly Dictionary<long, SpeakerFeedbackResult> _speakerCorrections = new();
    private readonly Dictionary<string, SpeakerPerson> _people = new();
    private readonly Dictionary<long, string> _correctionEvidenceIds = new();
    private readonly HashSet<long> _speakerReviewBusy = new();
    private readonly Dictionary<long, string> _speakerReviewMessages = new();
    private readonly Dictionary<string, Brush> _speakerColors = new();
    private readonly CancellationTokenSource _shutdown = new();
    private AppSettings _settings = SettingsStore.Load();
    private IncomingTranslationService? _service;
    private readonly TaskCompletionSource _serviceStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task ServiceStopped => _serviceRunTask ?? _serviceStopped.Task;
    private bool _serviceStarted;
    internal async Task ResetSpeakerLearningAsync(CancellationToken token)
    {
        if(_service is not null) await _service.ResetSpeakersAsync(token);
        else { using var identifier=new AiSpeakerIdentifier();await identifier.ResetProfilesAsync(token); }
        _speakerCorrections.Clear();
        _correctionEvidenceIds.Clear();
        _speakerReviewMessages.Clear();
        RefreshSpeakerNames(await (_service is ISpeakerFeedbackHost host ? host.GetSpeakerFeedbackStateAsync(token) : Task.FromResult(new SpeakerFeedbackState(Array.Empty<SpeakerPerson>(), false))));
    }

    public SubtitleWindow()
    {
        AppearanceTheme.Apply(_settings);
        ThemeBackground.Apply(_settings);
        Title = AppBrand.DisplayName + " · Subtitles";
        Icon = WindowIconProvider?.Invoke();
        Width = 960;
        Height = 430;
        MinWidth = 560;
        MinHeight = 260;
        FontFamily = ConversationFont;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Topmost = _settings.SubtitlesAlwaysOnTop;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;
        ApplyOverlayStyles();

        var frame = new Border
        {
            Background = OverlayBackground,
            BorderBrush = OverlayStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(20, 16, 20, 14)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = new TextBlock
        {
            Text = "Starting subtitles…",
            Foreground = OverlaySage,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        Image? headerMark = HeaderMarkProvider?.Invoke(22);
        if (headerMark is not null)
        {
            headerMark.Name = "SubtitleBrandMark";
            headerMark.Margin = new Thickness(0, 0, 9, 0);
            headerMark.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(headerMark, Dock.Left); titleRow.Children.Add(headerMark);
        }
        _subtitleHeading = new TextBlock { Text = AppBrand.DisplayName + " · " + Ui("Subtitles"), FontSize = 17,
            FontWeight = FontWeights.SemiBold, Foreground = OverlayText,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center };
        MarkLiteralUiProvider?.Invoke(_subtitleHeading);
        titleRow.Children.Add(_subtitleHeading);
        heading.Children.Add(titleRow);
        heading.Children.Add(_status);
        var options = new Button
        {
            Content = "Settings",
            Width = 82,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Change languages, people and subtitle settings",
            Cursor = Cursors.Hand
        };
        options.Click += (_, _) => OpenSettings();
        _quickGuideButton = new Button { Name = "SubtitleQuickGuideButton", Content = "?", Width = 32, Height = 32,
            FontSize = 16, FontWeight = FontWeights.SemiBold, Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
        // The glyph is literal; its localized tooltip/name are refreshed explicitly.
        MarkLiteralUiProvider?.Invoke(_quickGuideButton);
        _alwaysOnTopButton = new Button
        {
            Content = AlwaysOnTopButtonText(_settings.SubtitlesAlwaysOnTop),
            Width = 164,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = AlwaysOnTopToolTip(_settings.SubtitlesAlwaysOnTop),
            Cursor = Cursors.Hand
        };
        _alwaysOnTopButton.Click += (_, _) => ToggleAlwaysOnTop();
        _maximizeButton = new Button { Name = "SubtitleMaximizeButton", Width = 32, Height = 32,
            Padding = new Thickness(7), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
        _maximizeButton.Click += (_, _) => ToggleMaximized();
        var close = new Button
        {
            Content = "×",
            Width = 32,
            Height = 32,
            FontSize = 19,
            Foreground = OverlayMuted,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            ToolTip = "Stop subtitles without stopping your voice translation",
            Cursor = Cursors.Hand
        };
        close.Click += (_, _) => Close();
        header.Children.Add(heading);
        Grid.SetColumn(_quickGuideButton, 1);
        Grid.SetColumn(options, 2);
        Grid.SetColumn(_alwaysOnTopButton, 3);
        Grid.SetColumn(_maximizeButton, 4);
        Grid.SetColumn(close, 5);
        header.Children.Add(_quickGuideButton);
        header.Children.Add(options);
        header.Children.Add(_alwaysOnTopButton);
        header.Children.Add(_maximizeButton);
        header.Children.Add(close);

        _source = new TextBlock
        {
            Text = IncomingListening.WaitingMessage(_settings),
            Foreground = OverlayMuted,
            FontSize = 12,
            LineHeight = 18,
            MaxHeight = 54,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0),
            TextWrapping = TextWrapping.Wrap
        };
        var liveAudioPreview = new Border { Background = OverlayPanel, BorderBrush = OverlayStroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 14), Child = CreateSubtitleProfileBar() };
        _speakerRows = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        _outgoingState = new TextBlock { Foreground = OverlayMuted, FontSize = 11, LineHeight = 17, TextWrapping = TextWrapping.Wrap };
        var footer = new Border { Background = OverlayPanel, BorderBrush = OverlayStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 7, 0, 0), Child = _outgoingState };
        _playbackWarning = new TextBlock { Foreground = OverlayCoral, FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        _pinStatus = new TextBlock { Foreground = OverlayCoral, FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        _outgoingStateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _outgoingStateTimer.Tick += (_, _) => RefreshOutgoingState();
        _speakerScroll = new ScrollViewer
        {
            Content = _speakerRows,
            Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly
        };
        // Artwork is decorative, not a text backdrop. Solid role-colored backplates
        // protect every label/status even when a user chooses a high-contrast image.
        _dragHeader = new Border { Name = "SubtitleDragHeader", Background = OverlayPanel, CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10), Child = header };
        var playbackBackplate = StatusBackplate(_playbackWarning);
        var pinBackplate = StatusBackplate(_pinStatus);
        Grid.SetRow(_dragHeader, 0);
        Grid.SetRow(liveAudioPreview, 1);
        Grid.SetRow(_speakerScroll, 2);
        Grid.SetRow(footer, 3);
        Grid.SetRow(playbackBackplate, 4);
        Grid.SetRow(pinBackplate, 5);
        root.Children.Add(_dragHeader);
        root.Children.Add(liveAudioPreview);
        root.Children.Add(footer);
        root.Children.Add(playbackBackplate);
        root.Children.Add(_speakerScroll);
        root.Children.Add(pinBackplate);
        _quickGuide = new SubtitleQuickGuide(Ui, seen =>
        {
            SettingsStore.SaveSubtitleGuidePromptPreference(seen);
            _settings.SubtitleGuidePromptSeen = seen;
        }, () =>
        {
            if (OpenSpeakerSettingsRequested is not null) OpenSpeakerSettingsRequested();
            else OpenSettings();
        }, error => LogOverlayError("Save subtitle guide preference", error));
        _quickGuideButton.Click += (_, _) => _quickGuide.ShowGuide();
        _quickGuide.Dismissed += () => _quickGuideButton.Focus();
        MarkLiteralUiProvider?.Invoke(_quickGuide);
        Grid.SetRow(_quickGuide, 1); Grid.SetRowSpan(_quickGuide, 5);
        root.Children.Add(_quickGuide);
        var themedContent = new Grid();
        themedContent.Children.Add(ThemeBackground.CreateLayer());
        themedContent.Children.Add(root);
        frame.Child = themedContent;
        Content = frame;
        MarkLiteralUiProvider?.Invoke(_source);
        // Translation history contains user names/source/translated text, never interface copy.
        MarkLiteralUiProvider?.Invoke(_speakerRows);
        ApplyAlwaysOnTop(_settings.SubtitlesAlwaysOnTop);
        RefreshInterfaceLanguage();

        _dragHeader.MouseLeftButtonDown += HeaderMouseDown;
        _dragHeader.MouseMove += HeaderMouseMove;
        _dragHeader.MouseLeftButtonUp += (_, _) => CancelHeaderDrag();
        _dragHeader.LostMouseCapture += (_, _) => _headerDragPending = false;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || _quickGuide.Mode == SubtitleQuickGuideMode.Hidden) return;
            _quickGuide.Dismiss(); args.Handled = true;
        };
        StateChanged += (_, _) => RefreshWindowStateButton();
        Loaded += OnLoaded;
        AppearanceTheme.Changed += RefreshAppearance;
        Closed += (_, _) => AppearanceTheme.Changed -= RefreshAppearance;
        Closing += (_, _) =>
        {
            CancelHeaderDrag();
            _shutdown.Cancel();
            if (!_serviceStarted) _serviceStopped.TrySetResult();
            _outgoingStateTimer.Stop();
        };
    }

    private static Border StatusBackplate(TextBlock status)
    {
        var plate = new Border { Background = OverlayPanel, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 5, 0, 0), Child = status };
        plate.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = status });
        return plate;
    }

    private void RefreshAppearance()
    {
        if (Dispatcher.HasShutdownStarted || _shutdown.IsCancellationRequested) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(RefreshAppearance)); return; }
        // No service restart or history rebuild: brush mutation preserves the text and scroll.
        ApplyAlwaysOnTop(_settings.SubtitlesAlwaysOnTop);
        RefreshWindowStateButton();
        _quickGuideButton.ToolTip = Ui("Quick subtitle guide");
        System.Windows.Automation.AutomationProperties.SetName(_quickGuideButton, Ui("Quick subtitle guide"));
        _quickGuide.RefreshLanguage();
    }

    internal void RefreshInterfaceLanguage()
    {
        Title=AppBrand.DisplayName+" · "+Ui("Subtitles");
        _subtitleHeading.Text=Title;
        _status.Text=_lastStatus == IncomingListening.WaitingMessage(_settings)
            ? ListeningSourceWaitingUi(_settings) : Ui(_lastStatus);
        if(!_receivedSource) _source.Text=ListeningSourceWaitingUi(_settings);
        ApplyAlwaysOnTop(_settings.SubtitlesAlwaysOnTop);
        RefreshWindowStateButton();
        _quickGuideButton.ToolTip = Ui("Quick subtitle guide");
        System.Windows.Automation.AutomationProperties.SetName(_quickGuideButton, Ui("Quick subtitle guide"));
        _quickGuide.RefreshLanguage();
        RefreshSubtitleProfileButtons();
        foreach (var child in _speakerRows.Children.OfType<Border>())
            RefreshCorrectionButton(child);
        LocalizeUiProvider?.Invoke(this);
    }

    private void ToggleMaximized()
    {
        CancelHeaderDrag();
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        RefreshWindowStateButton();
    }

    private void RefreshWindowStateButton()
    {
        bool maximized = WindowState == WindowState.Maximized;
        string caption = Ui(maximized ? "Restore window" : "Maximize window");
        _maximizeButton.ToolTip = caption;
        System.Windows.Automation.AutomationProperties.SetName(_maximizeButton, caption);
        _maximizeButton.Content = new System.Windows.Shapes.Path { Width = 12, Height = 12, Stretch = Stretch.Uniform,
            Stroke = OverlayText, StrokeThickness = 1.2, IsHitTestVisible = false,
            Data = Geometry.Parse(maximized ? "M3,0 H12 V9 H9 M0,3 H9 V12 H0 Z M3,0 V3" : "M0,0 H12 V12 H0 Z") };
    }

    internal static bool IsSubtitleDragSource(DependencyObject? source, DependencyObject header)
    {
        for (DependencyObject? node = source; node is not null;)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.Selector or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Documents.Hyperlink) return false;
            if (ReferenceEquals(node, header)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : node is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void HeaderMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ButtonState != MouseButtonState.Pressed || !IsSubtitleDragSource(args.OriginalSource as DependencyObject, _dragHeader)) return;
        args.Handled = true;
        if (args.ClickCount == 2) { ToggleMaximized(); return; }
        if (WindowState == WindowState.Maximized)
        {
            // A simple click must not restore the window. Wait for an intentional drag.
            _headerDragOrigin = args.GetPosition(this);
            _headerDragPending = _dragHeader.CaptureMouse();
        }
        else TryHeaderDragMove();
    }

    private void HeaderMouseMove(object sender, MouseEventArgs args)
    {
        if (!_headerDragPending) return;
        if (args.LeftButton != MouseButtonState.Pressed) { CancelHeaderDrag(); return; }
        Point position = args.GetPosition(this);
        if (Math.Abs(position.X - _headerDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _headerDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        Point origin = _headerDragOrigin;
        double grabRatio = origin.X / Math.Max(1, ActualWidth);
        CancelHeaderDrag();
        if (WindowState == WindowState.Maximized)
        {
            try { SubtitleWindowPlacement.RestoreUnderPointer(this, grabRatio, origin.Y); }
            catch (Exception error) { LogOverlayError("Restore subtitle window for dragging", error); WindowState = WindowState.Normal; }
        }
        TryHeaderDragMove();
    }

    private void CancelHeaderDrag()
    {
        _headerDragPending = false;
        if (_dragHeader.IsMouseCaptured) _dragHeader.ReleaseMouseCapture();
    }

    private void TryHeaderDragMove()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed || WindowState != WindowState.Normal) return;
        try { DragMove(); }
        catch (InvalidOperationException error) { LogOverlayError("Move subtitle window", error); }
    }
    private static void RefreshCorrectionButton(DependencyObject root)
    {
        if(root is Button button && button.Tag is TranslationDisplay)
        { button.Content=Ui("Select a person");button.ToolTip=Ui("Choose a saved person, mark music, or undo a name change."); }
        else if (root is Button reviewButton && reviewButton.Tag is SpeakerSuggestionAction review)
        {
            string caption = review.Confirmed ? "Confirm this person" : "Not this person";
            reviewButton.ToolTip = Ui(caption);
            System.Windows.Automation.AutomationProperties.SetName(reviewButton, Ui(caption));
        }
        else if (root is TextBlock text && text.Tag is SubtitleInterfaceLabel label)
            text.Text = label.Values is null ? Ui(label.Source) : UiFormat(label.Source, label.Values);
        foreach(object child in LogicalTreeHelper.GetChildren(root)) if(child is DependencyObject dependency) RefreshCorrectionButton(dependency);
    }

    private void ApplyOverlayStyles()
    {
        Resources["OverlayText"] = AppearanceTheme.Text;
        Resources["OverlayPanel"] = AppearanceTheme.Surface;
        Resources["OverlayRaised"] = AppearanceTheme.Raised;
        Resources["OverlayStroke"] = AppearanceTheme.Stroke;
        Resources["OverlayAccent"] = AppearanceTheme.Accent;
        Resources["OverlayFocus"] = AppearanceTheme.Focus;
        Resources["OverlayScrollGrip"] = AppearanceTheme.ScrollGrip;
        Resources["OverlayMuted"] = AppearanceTheme.Muted;
        const string styles = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Button">
            <Setter Property="Foreground" Value="{DynamicResource OverlayText}"/>
            <Setter Property="Background" Value="{DynamicResource OverlayPanel}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource OverlayStroke}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="FontSize" Value="11"/>
            <Setter Property="Padding" Value="10,5"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="shell" CornerRadius="9" Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                        Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource OverlayAccent}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="shell" Property="Background" Value="{DynamicResource OverlayRaised}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource OverlayFocus}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="shell" Property="Opacity" Value="0.5"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="ScrollBar">
            <Setter Property="Width" Value="8"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Grid Background="Transparent" Margin="1,3">
                  <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}">
                    <Track.DecreaseRepeatButton><RepeatButton Opacity="0" Focusable="False" Command="ScrollBar.PageUpCommand"/></Track.DecreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb MinHeight="22">
                        <Thumb.Template><ControlTemplate TargetType="Thumb">
                          <Border x:Name="thumb" Background="{DynamicResource OverlayScrollGrip}" CornerRadius="3"/>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="thumb" Property="Background" Value="{DynamicResource OverlayMuted}"/></Trigger>
                            <Trigger Property="IsDragging" Value="True"><Setter TargetName="thumb" Property="Background" Value="{DynamicResource OverlayAccent}"/></Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate></Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                    <Track.IncreaseRepeatButton><RepeatButton Opacity="0" Focusable="False" Command="ScrollBar.PageDownCommand"/></Track.IncreaseRepeatButton>
                  </Track>
                </Grid>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """;
        Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(styles));
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_serviceStarted || _shutdown.IsCancellationRequested) return;
        _serviceStarted = true;
        Rect area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 45;
        RefreshOutgoingState();
        _outgoingStateTimer.Start();
        // This only offers help. It never waits for a dialog or delays audio startup.
        _quickGuide.ShowPromptIfNeeded(_settings.SubtitleGuidePromptSeen);
        try { SettingsStore.Save(_settings); }
        catch (Exception exception)
        {
            LogOverlayError("Save subtitle settings", exception);
            await SetStatus("Couldn't save your settings. Please try again.", true);
        }
        StartOverlayService();
    }

    private void RefreshOutgoingState()
    {
        try
        {
            var hosts = System.Diagnostics.Process.GetProcessesByName("FoxTrans.LocalHost")
                .Concat(System.Diagnostics.Process.GetProcessesByName("FoxTrans.Cli")).ToArray();
            bool hostRunning = hosts.Length > 0;
            foreach (var host in hosts) host.Dispose();
            if (!hostRunning)
                _outgoingState.Text = UiFormat("My voice → {0}: off.", Ui(_settings.OwnVoiceTargetLanguage));
            else
            {
            string path = Path.Combine(AppPaths.Root,"foxtrans-mute-sync.log");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(Math.Max(0, stream.Length - 8192), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();
                bool? muted = ReadLoggedMuteState(text);
                _outgoingState.Text = muted is null
                    ? UiFormat("My voice → {0}: checking VRChat microphone…", Ui(_settings.OwnVoiceTargetLanguage))
                    : muted.Value
                    ? UiFormat("My voice → {0}: muted in VRChat. {1}", Ui(_settings.OwnVoiceTargetLanguage),
                        Ui(_settings.FinishCapturedSentenceOnMute ? "Captured speech may still send." : "Nothing new will be sent."))
                    : UiFormat("My voice → {0}: listening. Pause to send.", Ui(_settings.OwnVoiceTargetLanguage));
            }
        }
        catch { _outgoingState.Text = UiFormat("My voice → {0}: checking microphone…", Ui(_settings.OwnVoiceTargetLanguage)); }
        if (DateTimeOffset.UtcNow - _lastPlaybackWarningProbe >= TimeSpan.FromSeconds(3))
        {
            _lastPlaybackWarningProbe = DateTimeOffset.UtcNow;
            try
            {
                string playbackWarning = IncomingListening.UsesProcessCapture(_settings) ? "" : AudioRouting.PlaybackWarning(_settings.PlaybackDeviceId);
                _playbackWarning.Text = IncomingListening.UsesProcessCapture(_settings)
                    ? IncomingListening.TryResolveProcess(_settings) is null ? ListeningSourceWaitingUi(_settings) : ""
                    : string.IsNullOrEmpty(playbackWarning) ? "" : Ui("Playback is muted or at zero volume. Unmute it for subtitles.");
                _playbackWarning.Visibility = string.IsNullOrEmpty(_playbackWarning.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { /* A device changing during the probe must not interrupt translation. */ }
        }
    }

    private static string ListeningSourceWaitingUi(AppSettings settings) => IncomingListening.SourceMode(settings) == "application"
        ? UiFormat("Waiting for {0}.", IncomingListening.SourceName(settings))
        : Ui(IncomingListening.WaitingMessage(settings));

    internal static bool? ReadLoggedMuteState(string text)
    {
        var matches = Regex.Matches(text,
            @"MuteSelf=(?<muted>true|false)|Capture-gate host started[^\r\n]*?muted=(?<muted>true|false)|VRChat mic is (?<legacy>UNMUTED|MUTED)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0) return null;
        Match latest = matches[^1];
        return latest.Groups["muted"].Success
            ? bool.Parse(latest.Groups["muted"].Value)
            : string.Equals(latest.Groups["legacy"].Value, "MUTED", StringComparison.OrdinalIgnoreCase);
    }

    private Task SetStatus(string text, bool error = false) =>
        Dispatcher.InvokeAsync(new Action(() =>
        {
            _lastStatus=text; _status.Text = text == IncomingListening.WaitingMessage(_settings)
                ? ListeningSourceWaitingUi(_settings) : Ui(text);
            _status.Foreground = error ? AppearanceTheme.Error : OverlaySage;
        })).Task;

    private static void LogOverlayError(string action, Exception error)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(Path.Combine(AppPaths.Root, "incoming-translator.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Overlay {action}: {error.ToString().Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch { /* A logging problem must not prevent the overlay from displaying a helpful status. */ }
    }

    private void ToggleAlwaysOnTop()
    {
        bool previous = _settings.SubtitlesAlwaysOnTop;
        try
        {
            ChangeAlwaysOnTopPreference(previous, !previous,
                SettingsStore.SaveSubtitlePinPreference, ApplyAlwaysOnTop);
            _pinStatus.Text = "";
            _pinStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            LogOverlayError("Save Always on top preference", error);
            _pinStatus.Text = Ui("Couldn't save Always on top. Previous setting kept. Try again.");
            _pinStatus.Visibility = Visibility.Visible;
        }
    }

    private void ApplyAlwaysOnTop(bool enabled)
    {
        _settings.SubtitlesAlwaysOnTop = enabled;
        Topmost = enabled;
        _alwaysOnTopButton.Content = UiFormat("Always on top · {0}", Ui(enabled ? "On" : "Off"));
        _alwaysOnTopButton.ToolTip = Ui(AlwaysOnTopToolTip(enabled));
        _alwaysOnTopButton.Foreground = enabled ? OverlaySage : OverlayMuted;
        _alwaysOnTopButton.BorderBrush = enabled ? OverlaySage : OverlayStroke;
    }

    internal static string AlwaysOnTopButtonText(bool enabled) => "Always on top · " + (enabled ? "On" : "Off");

    private static string AlwaysOnTopToolTip(bool enabled) => enabled
        ? "Keep subtitles in front of other windows. Click to turn off."
        : "Click to keep subtitles in front of other windows.";

    internal static AppSettings WithAlwaysOnTopPreference(AppSettings latestSaved, bool enabled)
    {
        AppSettings next = latestSaved.Copy();
        next.SubtitlesAlwaysOnTop = enabled;
        return next;
    }

    // The delegates let offline checks verify immediate application and rollback without showing
    // a window, touching real settings, or starting translation. The dedicated store operation
    // atomically merges this preference only; it never rewrites microphone configuration.
    internal static void ChangeAlwaysOnTopPreference(bool previous, bool requested,
        Action<bool> save, Action<bool> apply)
    {
        try
        {
            apply(requested);
            save(requested);
        }
        catch
        {
            apply(previous);
            throw;
        }
    }

    internal static int RunAlwaysOnTopOfflineSelfTests()
    {
        int checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Subtitle Always on top self-check failed."); checks++; }
        Check(new AppSettings().SubtitlesAlwaysOnTop);
        Check(AlwaysOnTopButtonText(true).EndsWith("On", StringComparison.Ordinal));
        Check(AlwaysOnTopButtonText(false).EndsWith("Off", StringComparison.Ordinal));
        var saved = new AppSettings { SubtitlesAlwaysOnTop = true, OwnVoiceTargetLanguage = "Japanese",
            IncomingTargetLanguage = "English", OwnManagedModelId = "selected-model", Glossary = "saved glossary", CloseButtonBehavior = "Tray" };
        AppSettings next = WithAlwaysOnTopPreference(saved, false);
        Check(saved.SubtitlesAlwaysOnTop && !next.SubtitlesAlwaysOnTop);
        Check(next.OwnVoiceTargetLanguage == saved.OwnVoiceTargetLanguage && next.IncomingTargetLanguage == saved.IncomingTargetLanguage);
        Check(next.OwnManagedModelId == saved.OwnManagedModelId && next.Glossary == saved.Glossary && next.CloseButtonBehavior == saved.CloseButtonBehavior);
        bool applied = true;
        var order = new List<string>();
        AppSettings persisted = saved;
        ChangeAlwaysOnTopPreference(true, false,
            enabled => { order.Add("save"); persisted = WithAlwaysOnTopPreference(persisted, enabled); }, enabled => { order.Add("apply"); applied = enabled; });
        Check(order.SequenceEqual(new[] { "apply", "save" }));
        Check(!applied && !persisted.SubtitlesAlwaysOnTop);
        Check(persisted.OwnVoiceTargetLanguage == "Japanese" && persisted.OwnManagedModelId == "selected-model");
        var applicationHistory = new List<bool>();
        bool failureObserved = false;
        int writes = 0;
        try
        {
            ChangeAlwaysOnTopPreference(false, true,
                _ => { writes++; throw new IOException("Synthetic atomic save failure"); }, enabled => { applied = enabled; applicationHistory.Add(enabled); });
        }
        catch (IOException) { failureObserved = true; }
        Check(failureObserved && !applied && !persisted.SubtitlesAlwaysOnTop);
        Check(applicationHistory.SequenceEqual(new[] { true, false }));
        Check(writes == 1 && !persisted.SubtitlesAlwaysOnTop);
        Check(persisted.OwnVoiceTargetLanguage == "Japanese" && persisted.Glossary == "saved glossary");
        return checks;
    }

    private void OpenSettings()
    {
        if(OpenOptionsRequested is not null) { OpenOptionsRequested(); return; }
        var window = new SettingsWindow(_settings) { Owner = this, Icon = Icon };
        window.ResetSpeakersRequested += async () =>
        {
            if (_service is null) return;
            await _service.ResetSpeakersAsync(_shutdown.Token);
            _cards.Clear();
            _speakerRows.Children.Clear();
            _displayHistory.Clear();
            _speakerColors.Clear();
        };
        window.Saved += async settings =>
        {
            bool previousRecognition = RecognitionStatus.Enabled;
            try
            {
                await ApplySpeakerRecognitionAsync(settings.UseAiSpeakerRecognition, _shutdown.Token);
                _settings = settings.Copy();
                ApplyAlwaysOnTop(settings.SubtitlesAlwaysOnTop);
                _service?.UpdateSettings(settings);
                RenderHistory();
                await SetStatus("Settings saved.");
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception error)
            {
                LogOverlayError("Apply voice-recognition preference", error);
                try { SettingsStore.SaveSpeakerRecognitionPreference(previousRecognition); }
                catch (Exception saveError) { LogOverlayError("Restore voice-recognition preference", saveError); }
                await SetStatus("Couldn't change voice recognition. Translation settings were kept; please try again.", true);
            }
        };
        window.Show();
    }

    internal int HistoryEntryCount => _displayHistory.Count;
    internal int VisibleBubbleCount => _speakerRows.Children.Count;
    internal TranslationDisplay? HistoryEntry(long utteranceId) =>
        _displayHistory.FirstOrDefault(item => item.UtteranceId == utteranceId) is var item && item.UtteranceId == utteranceId ? item : null;

    internal void Publish(TranslationDisplay result)
    {
        RefreshPersonSnapshot();
        if (_speakerCorrections.TryGetValue(result.UtteranceId, out SpeakerFeedbackResult? corrected))
            result = result with { Speaker = corrected.Label, PersonId = corrected.PersonId,
                EvidenceId = _correctionEvidenceIds.GetValueOrDefault(result.UtteranceId, result.EvidenceId),
                IsManuallyAttributed = corrected.IsManuallyAttributed };
        else if (!result.IsManuallyAttributed && _service is IncomingTranslationService service &&
            !service.IsRecognitionAttributionCurrent(result.SpeakerRecognitionEpoch))
            // Recheck on the dispatcher, not only before awaiting it: Stop may have completed
            // while this display callback was queued. Existing history is left untouched.
            result = result with { Speaker = GroupSpeechProtocol.VoiceLabel(result.SessionVoiceKey), PersonId = null };
        int existing = _displayHistory.FindIndex(item => item.UtteranceId == result.UtteranceId);
        if (existing >= 0 && _displayHistory[existing].At > result.At) return;
        if (existing >= 0) _displayHistory[existing] = result;
        else _displayHistory.Add(result);
        while (_displayHistory.Count > 200)
        {
            _speakerCorrections.Remove(_displayHistory[0].UtteranceId);
            _correctionEvidenceIds.Remove(_displayHistory[0].UtteranceId);
            _speakerReviewMessages.Remove(_displayHistory[0].UtteranceId);
            _displayHistory.RemoveAt(0);
        }
        RenderHistory();
    }

    private void RenderHistory()
    {
        _speakerRows.Children.Clear();
        _cards.Clear();
        if (_settings.OverlayMode is "Sentence bubbles" or "Latest sentence")
        {
            foreach (TranslationDisplay sentence in _displayHistory.OrderBy(item => item.At)
                .TakeLast(_settings.OverlayMode == "Latest sentence" ? 1 : _settings.HistoryLinesPerSpeaker))
                CreateSentenceBubble(sentence);
        }
        else
        {
            // A guessed name is not a confirmed shared identity. Keep uncertain voices
            // separate even when the matcher proposes the same saved person for both.
            var speakers = _displayHistory.Select(DisplayGroupKey).Distinct().OrderBy(speaker =>
                _displayHistory.Last(item => DisplayGroupKey(item) == speaker).At).TakeLast(_settings.VisibleSpeakerRows);
            foreach (string speakerId in speakers)
                CreateSpeakerCard(speakerId, _displayHistory.Where(item => DisplayGroupKey(item) == speakerId).TakeLast(_settings.HistoryLinesPerSpeaker).ToList());
        }
        _speakerScroll.UpdateLayout();
        _speakerScroll.ScrollToEnd();
        _ = Dispatcher.InvokeAsync(_speakerScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CreateSentenceBubble(TranslationDisplay sentence)
    {
        var panel = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        AddCorrectionButton(header, sentence);
        AddSuggestionReviewButtons(header, sentence);
        AddPersonAvatar(header, sentence);
        var time = new TextBlock { Text = $"{sentence.At.ToLocalTime():HH:mm}", Foreground = OverlayMuted,
            FontSize = 10, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(time, Dock.Right); header.Children.Add(time);
        var speaker = new TextBlock { Text = DisplayPersonLabel(sentence), Foreground = IsSpeakerSuggestion(sentence) ? OverlayMuted : GetSpeakerColor(sentence.Speaker),
            Tag = InterfacePersonLabel(sentence),
            FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(speaker);
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = sentence.Translation, Foreground = OverlayText, FontFamily = ConversationFont,
            FontSize = 19, FontWeight = FontWeights.Normal, LineHeight = 28, TextWrapping = TextWrapping.Wrap });
        AddFeedbackSummary(panel, sentence);
        _speakerRows.Children.Add(new Border { Background = OverlayPanel,
            BorderBrush = OverlayStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4, 16, 16, 16), Padding = new Thickness(15, 11, 15, 12),
            Margin = new Thickness(0, 0, 0, 10), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left, Child = panel });
    }

    private void CreateSpeakerCard(string speakerId, List<TranslationDisplay> lines)
    {
        TranslationDisplay result = lines[^1];
        if (!_cards.TryGetValue(speakerId, out SpeakerCard? card))
        {
            var speaker = new TextBlock
            {
                Text = DisplayPersonLabel(result),
                Tag = InterfacePersonLabel(result),
                Foreground = IsSpeakerSuggestion(result) ? OverlayMuted : GetSpeakerColor(result.Speaker),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 7),
                FontWeight = FontWeights.SemiBold
            };
            var translation = new TextBlock { Foreground = OverlayText, FontFamily = ConversationFont, FontSize = 19,
                FontWeight = FontWeights.Normal, LineHeight = 28, TextWrapping = TextWrapping.Wrap };
            var source = new TextBlock { Foreground = OverlayMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0) };
            var panel = new StackPanel();
            var header = new DockPanel();
            AddCorrectionButton(header, result);
            AddSuggestionReviewButtons(header, result);
            AddPersonAvatar(header, result);
            header.Children.Add(speaker);
            panel.Children.Add(header);
            panel.Children.Add(translation);
            panel.Children.Add(source);
            AddFeedbackSummary(panel, result);
            var border = new Border
            {
                Background = OverlayPanel,
                BorderBrush = OverlayStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4, 16, 16, 16),
                Padding = new Thickness(15, 11, 15, 12),
                Margin = new Thickness(0, 0, 0, 10),
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = panel
            };
            card = new SpeakerCard(border, translation, source, DateTimeOffset.UtcNow);
            _cards[speakerId] = card;
            _speakerRows.Children.Add(border);
        }
        card.Translation.Text = string.Join(Environment.NewLine + Environment.NewLine,
            lines.Select(item => $"{item.At.ToLocalTime():HH:mm:ss}  {item.Translation}"));
        card.Source.Text = result.Source;
        card.LastUpdated = DateTimeOffset.UtcNow;
    }

    private void AddCorrectionButton(DockPanel header, TranslationDisplay sentence)
    {
        if (CorrectSpeakerRequested is null) return;
        var button = new Button { Content = Ui("Select a person"), Padding = new Thickness(8, 3, 8, 3),
            FontSize = 10, Foreground = OverlayMuted, Background = Brushes.Transparent,
            Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand,
            ToolTip = Ui("Choose a saved person, mark music, or undo a name change."), Tag = sentence,
            IsEnabled = !_speakerReviewBusy.Contains(sentence.UtteranceId) };
        DockPanel.SetDock(button, Dock.Right);
        button.Click += async (_, args) =>
        {
            args.Handled = true;
            if (_service is null || CorrectSpeakerRequested is null || _speakerReviewBusy.Contains(sentence.UtteranceId)) return;
            button.IsEnabled = false;
            try
            {
                SpeakerFeedbackResult? result = await CorrectSpeakerRequested(this, button, _service, sentence);
                if (result is not null && !_shutdown.IsCancellationRequested)
                {
                    ApplySpeakerCorrection(sentence.UtteranceId, result, sentence.EvidenceId);
                    await SetStatus(FeedbackSummary(result), !result.Applied);
                }
            }
            catch (Exception error)
            {
                LogOverlayError("Save speaker correction", error);
                if (!_shutdown.IsCancellationRequested) await SetStatus("Couldn't save that name change. Please try again.", true);
            }
            finally { button.IsEnabled = true; }
        };
        header.Children.Add(button);
    }

    private static string FeedbackSummary(SpeakerFeedbackResult result)
    {
        if (!result.Applied) return "Correction not applied; your current labels were kept.";
        if (result.Message.StartsWith("Correction undone", StringComparison.Ordinal)) return "Name change undone.";
        if (result.NegativeLearned) return "Wrong match remembered · voice example saved";
        if (result.SuggestionConfirmed == false) return "Marked as unknown · no voice example saved";
        if (result.Learned) return "Person updated · voice example saved";
        if (result.PersonId is null && result.Label == "Music / recording") return "Marked as music or a recording · not used to learn voices";
        return "Name updated · no voice example saved";
    }

    internal void ApplySpeakerCorrection(long utteranceId, SpeakerFeedbackResult result, string? evidenceId = null)
    {
        if (!result.Applied) return;
        RefreshPersonSnapshot();
        _speakerCorrections[utteranceId] = result;
        if (evidenceId is not null) _correctionEvidenceIds[utteranceId] = evidenceId;
        int index = _displayHistory.FindIndex(item => item.UtteranceId == utteranceId);
        if (index >= 0) _displayHistory[index] = _displayHistory[index] with { Speaker = result.Label, PersonId = result.PersonId,
            EvidenceId = _correctionEvidenceIds.GetValueOrDefault(utteranceId, _displayHistory[index].EvidenceId),
            IsManuallyAttributed = result.IsManuallyAttributed };
        _speakerReviewMessages.Remove(utteranceId);
        RenderHistory();
    }

    internal void RefreshSpeakerNames(SpeakerFeedbackState state)
    {
        _people.Clear();
        foreach (SpeakerPerson person in state.People) _people[person.Id] = person;
        var names = state.People.ToDictionary(person => person.Id, person => person.Name);
        for (int index = 0; index < _displayHistory.Count; index++)
        {
            TranslationDisplay item = _displayHistory[index];
            if (item.PersonId is null) continue;
            bool exists = names.TryGetValue(item.PersonId, out string? name);
            _displayHistory[index] = item with { Speaker = exists ? name! : "Unidentified speaker", PersonId = exists ? item.PersonId : null };
        }
        foreach (long id in _speakerCorrections.Keys.ToArray())
        {
            SpeakerFeedbackResult item = _speakerCorrections[id];
            if (item.PersonId is null) continue;
            bool exists = names.TryGetValue(item.PersonId, out string? name);
            _speakerCorrections[id] = item with { Label = exists ? name! : "Unidentified speaker", PersonId = exists ? item.PersonId : null };
        }
        RenderHistory();
    }

    private void RefreshPersonSnapshot()
    {
        if (_service?.SpeakerFeedbackSnapshot is not SpeakerFeedbackState state) return;
        _people.Clear();
        foreach (SpeakerPerson person in state.People) _people[person.Id] = person;
    }

    private void AddPersonAvatar(DockPanel header, TranslationDisplay sentence)
    {
        if (sentence.PersonId is null || !_people.TryGetValue(sentence.PersonId, out SpeakerPerson? person)) return;
        FrameworkElement avatar = PersonAvatar.Create(person, 30, OverlayStroke, OverlayText);
        avatar.Margin = new Thickness(0, 0, 9, 0);
        DockPanel.SetDock(avatar, Dock.Left); header.Children.Add(avatar);
    }

    internal static bool IsSpeakerSuggestion(TranslationDisplay sentence) => sentence.PersonId is not null && !sentence.IsManuallyAttributed;
    private static string DisplayGroupKey(TranslationDisplay sentence) => IsSpeakerSuggestion(sentence)
        ? "suggested-voice:" + (!string.IsNullOrWhiteSpace(sentence.SessionVoiceKey) ? sentence.SessionVoiceKey
            : sentence.UtteranceId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        : sentence.PersonId is not null ? "person:" + sentence.PersonId : "voice:" + (!string.IsNullOrWhiteSpace(sentence.SessionVoiceKey)
            ? sentence.SessionVoiceKey : sentence.Speaker);
    private static string DisplayPersonLabel(TranslationDisplay sentence) => IsSpeakerSuggestion(sentence)
        ? UiFormat("Maybe {0}", sentence.Speaker)
        : sentence.PersonId is null && sentence.Speaker == "Unidentified speaker" ? Ui("Unknown person") : sentence.Speaker;
    private static SubtitleInterfaceLabel? InterfacePersonLabel(TranslationDisplay sentence) =>
        IsSpeakerSuggestion(sentence) ? new("Maybe {0}", [sentence.Speaker])
        : sentence.PersonId is null && sentence.Speaker == "Unidentified speaker" ? new("Unknown person") : null;
    private sealed record SubtitleInterfaceLabel(string Source, object[]? Values = null);
    private sealed record SpeakerSuggestionAction(TranslationDisplay Sentence, bool Confirmed);

    private void AddFeedbackSummary(StackPanel panel, TranslationDisplay sentence)
    {
        string? message = _speakerReviewMessages.GetValueOrDefault(sentence.UtteranceId);
        SpeakerFeedbackResult? feedback = _speakerCorrections.GetValueOrDefault(sentence.UtteranceId);
        message ??= feedback is null ? null : FeedbackSummary(feedback);
        if (message is null) return;
        panel.Children.Add(new TextBlock { Text = Ui(message), Tag = new SubtitleInterfaceLabel(message),
            FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0),
            Foreground = _speakerReviewMessages.ContainsKey(sentence.UtteranceId) && !_speakerReviewBusy.Contains(sentence.UtteranceId)
                ? AppearanceTheme.Error : feedback is { Learned: true } or { NegativeLearned: true } ? OverlaySage : OverlayMuted });
    }

    private void AddSuggestionReviewButtons(DockPanel header, TranslationDisplay sentence)
    {
        if (!IsSpeakerSuggestion(sentence)) return;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        foreach (bool confirmed in new[] { true, false })
        {
            string caption = confirmed ? "Confirm this person" : "Not this person";
            var button = new Button { Name = confirmed ? "ConfirmSpeakerSuggestionButton" : "RejectSpeakerSuggestionButton",
                Content = confirmed ? "✓" : "×", MinWidth = 30, MinHeight = 28,
                Padding = new Thickness(5, 2, 5, 2), FontSize = 16, Background = Brushes.Transparent,
                Foreground = confirmed ? OverlaySage : OverlayCoral, Margin = new Thickness(confirmed ? 0 : 4, 0, 0, 0),
                Cursor = Cursors.Hand, ToolTip = Ui(caption), Tag = new SpeakerSuggestionAction(sentence, confirmed),
                IsEnabled = !_speakerReviewBusy.Contains(sentence.UtteranceId) && !string.IsNullOrWhiteSpace(sentence.EvidenceId) };
            System.Windows.Automation.AutomationProperties.SetName(button, Ui(caption));
            button.Click += async (_, args) => { args.Handled = true; await ReviewSpeakerSuggestionAsync(sentence, confirmed); };
            actions.Children.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
    }

    private bool IsCurrentSuggestion(TranslationDisplay sentence) => HistoryEntry(sentence.UtteranceId) is TranslationDisplay latest
        && IsSpeakerSuggestion(latest) && latest.PersonId == sentence.PersonId && latest.EvidenceId == sentence.EvidenceId
        && latest.SpeakerRecognitionEpoch == sentence.SpeakerRecognitionEpoch;

    private bool ShowsReviewedResult(TranslationDisplay sentence, SpeakerFeedbackResult result) =>
        HistoryEntry(sentence.UtteranceId) is TranslationDisplay latest && latest.PersonId == result.PersonId
        && latest.Speaker == result.Label && latest.IsManuallyAttributed == result.IsManuallyAttributed
        && latest.EvidenceId == sentence.EvidenceId;

    // The injected host seam is used only by model-free tests; production uses the
    // live service. Awaiting feedback never blocks new transcription or subtitles.
    internal async Task ReviewSpeakerSuggestionAsync(TranslationDisplay sentence, bool confirmed, ISpeakerFeedbackHost? host = null)
    {
        host ??= _service;
        if (host is null || _shutdown.IsCancellationRequested || !IsCurrentSuggestion(sentence)
            || string.IsNullOrWhiteSpace(sentence.EvidenceId) || !_speakerReviewBusy.Add(sentence.UtteranceId)) return;
        _speakerReviewMessages[sentence.UtteranceId] = "Reviewing this suggestion…";
        RenderHistory();
        try
        {
            SpeakerFeedbackState state = host.SpeakerFeedbackSnapshot ?? await host.GetSpeakerFeedbackStateAsync(_shutdown.Token);
            if (!state.InstructionAcknowledged)
            {
                bool acknowledged = EnsureSpeakerLearningRequested is not null
                    && await EnsureSpeakerLearningRequested(this, host, sentence.Speaker, confirmed);
                if (_shutdown.IsCancellationRequested) return;
                if (!acknowledged && confirmed) { _speakerReviewMessages.Remove(sentence.UtteranceId); return; }
            }
            if (!IsCurrentSuggestion(sentence))
            { _speakerReviewMessages[sentence.UtteranceId] = "This suggestion changed. Review the latest name."; return; }
            SpeakerFeedbackResult result = await host.ReviewSpeakerSuggestionAsync(sentence.EvidenceId, sentence.PersonId!, confirmed, _shutdown.Token);
            if (_shutdown.IsCancellationRequested) return;
            if (!result.Applied)
            {
                _speakerReviewMessages[sentence.UtteranceId] = result.Message is "This suggestion was already reviewed."
                    or "This suggestion is no longer available for review. Choose a person manually instead."
                    ? result.Message : "Couldn't review that suggestion. Please try again.";
                return;
            }
            // A concurrent manual correction or newer candidate must win over an
            // old click. The backend also validates the evidence/candidate pair.
            if (!IsCurrentSuggestion(sentence) && !ShowsReviewedResult(sentence, result))
            { _speakerReviewMessages[sentence.UtteranceId] = "This suggestion changed. Review the latest name."; return; }
            ApplySpeakerCorrection(sentence.UtteranceId, result, sentence.EvidenceId);
            await SetStatus(FeedbackSummary(result));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogOverlayError("Review person suggestion", error);
            if (!_shutdown.IsCancellationRequested)
            {
                _speakerReviewMessages[sentence.UtteranceId] = "Couldn't review that suggestion. Please try again.";
                await SetStatus("Couldn't review that suggestion. Please try again.", true);
            }
        }
        finally
        {
            _speakerReviewBusy.Remove(sentence.UtteranceId);
            if (!_shutdown.IsCancellationRequested) RenderHistory();
        }
    }

    private Brush GetSpeakerColor(string speaker)
    {
        if (!_speakerColors.TryGetValue(speaker, out Brush? brush))
            _speakerColors[speaker] = brush = SpeakerColor(_speakerColors.Count);
        return brush;
    }

    private static Brush SpeakerColor(int index)
    {
        Brush[] colors = { AppearanceTheme.Speaker0, AppearanceTheme.Speaker1,
            AppearanceTheme.Speaker2, AppearanceTheme.Speaker3, AppearanceTheme.Speaker4, AppearanceTheme.Speaker5 };
        return colors[index % colors.Length];
    }

    private sealed class SpeakerCard
    {
        public Border Container { get; }
        public TextBlock Translation { get; }
        public TextBlock Source { get; }
        public DateTimeOffset LastUpdated { get; set; }
        public SpeakerCard(Border container, TextBlock translation, TextBlock source, DateTimeOffset lastUpdated) =>
            (Container, Translation, Source, LastUpdated) = (container, translation, source, lastUpdated);
    }
}

internal readonly record struct TranslationRequest(string Source, SpeakerAttribution Speaker, long UtteranceId, DateTimeOffset At)
{
    public long SpeakerRecognitionEpoch { get; init; }
    public bool IsCommitted { get; init; } = true;
}
internal sealed record SpeakerRecognitionStatus(bool Enabled, bool ModelLoaded, string Message);
internal sealed record IncomingHealthSnapshot(string Stage, string Message, bool IsWarning, bool Connected,
    DateTimeOffset UpdatedAt, DateTimeOffset? LastWordAt, long AudioBytesCaptured, long AudioBytesSent,
    long DroppedAudioChunks, int RecoveryCount, string LatestError);
internal readonly record struct TranslationDisplay(string Speaker, string Source, string Translation, long UtteranceId, DateTimeOffset At)
{
    public string EvidenceId { get; init; } = "";
    public string? PersonId { get; init; }
    public long SpeakerRecognitionEpoch { get; init; }
    public bool IsManuallyAttributed { get; init; }
    public string SessionVoiceKey { get; init; } = "";
}

internal sealed partial class IncomingTranslationService : IDisposable, ISpeakerFeedbackHost
{
    private const string VoxtralSocket = "ws://127.0.0.1:8081/v1/realtime/transcription";
    private const int AudioChunkBytes = 2560;
    private static readonly string LogPath = Path.Combine(AppPaths.Root,"incoming-translator.log");
    private readonly Func<string, bool, Task> _status;
    private readonly Func<string, Task> _source;
    private readonly Func<TranslationDisplay, Task> _translation;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _gate = new();
    private readonly object _settingsGate = new();
    private readonly SemaphoreSlim _healthUpdates = new(1, 1);
    private IncomingHealthSnapshot _health = new("waiting", "Waiting for speech…", false, false,
        DateTimeOffset.UtcNow, null, 0, 0, 0, 0, "");
    private long _lastWordTicks;
    private volatile bool _awaitingRecognitionProgress;
    private readonly SpeakerTracker _speakerTracker = new();
    private readonly AiSpeakerIdentifier _aiSpeakerIdentifier;
    private readonly SemaphoreSlim _recognitionChanges = new(1, 1);
    private long _speakerRecognitionEpoch;
    private readonly object _feedbackGate = new();
    private readonly Dictionary<long, SpeakerContextEntry> _speakerContext = new();
    private readonly Dictionary<string, long> _evidenceBubbles = new();
    private readonly string _translationSessionId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<long, DateTimeOffset> _publishedCommittedTranslations = new();
    private readonly SemaphoreSlim _personCountChanges = new(1, 1);
    private readonly Dictionary<long, SpeakerFeedbackResult> _manualSpeakers = new();
    private readonly Dictionary<string, string> _personNames = new();
    private readonly HashSet<string> _deletedPeople = new();
    private readonly Dictionary<long, DateTimeOffset> _latestUtteranceRevisions = new();
    private readonly IncomingContinuousAudioBuffer _continuousAudio = new();
    private readonly SubtitleSegmentation _subtitleSegments = new();
    private readonly Dictionary<int, long> _subtitleBubbleIds = new();
    private readonly IncomingAudioTimeline _sentAudioTimeline = new();
    private readonly object _pcmPumpGate = new();
    private DateTimeOffset _lastAudioDropLogAt = DateTimeOffset.MinValue;
    private long _lastLoggedAudioDrops;
    private long _nextUtteranceId;
    private long _currentUtteranceId;
    private volatile bool _finalizingAudio;
    private AppSettings _settings;
    private readonly Channel<TranslationRequest> _translations;
    private long _translationQueueDrops;
    private long _reportedTranslationQueueDrops;
    private IWaveIn? _capture;
    private int? _captureProcessId;
    private BufferedWaveProvider? _buffered;
    private ClientWebSocket? _socket;
    private IWaveProvider? _pcmProvider;
    private RecognitionGainSampleProvider? _recognitionGain;
    private NAudio.CoreAudioApi.MMDevice? _captureDevice;
    private string _captureDeviceId = "";
    private string _captureDeviceName = "";
    private bool _captureRefreshNeeded;
    private DateTimeOffset _lastDeviceProbeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _audioTimelineStart;
    private DateTimeOffset? _lastTextAudioEnd;
    private DateTimeOffset? _lastMeaningfulAudioEnd;
    private long _padTokens;
    private long _lexicalTokens;
    private long _lastMeaningfulTicks = DateTimeOffset.UtcNow.UtcTicks;
    private long _lastDecoderProgressTicks = DateTimeOffset.UtcNow.UtcTicks;
    private long _lastAudioSendTicks;
    private long _lastFallbackTicks;
    private int _consecutiveStallFallbacks;
    private int _blankRecoveryAttempts;
    private string _meaningfulTranscript = "";
    private string _latest = "";
    private DateTimeOffset _lastTranscriptAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _sessionStartedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastLagLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDiagnosticAt = DateTimeOffset.MinValue;
    private long _audioBytesCaptured;
    private long _audioBytesSent;
    private long _transcriptEvents;
    private int _utterancesInSession;
    private int _reconnectCount;
    private volatile bool _connected;
    private bool _disposed;

    internal IncomingHealthSnapshot HealthSnapshot => Volatile.Read(ref _health) with
    {
        Connected = _connected,
        LastWordAt = Interlocked.Read(ref _lastWordTicks) is long ticks && ticks != 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
        AudioBytesCaptured = Interlocked.Read(ref _audioBytesCaptured),
        AudioBytesSent = Interlocked.Read(ref _audioBytesSent),
        DroppedAudioChunks = _continuousAudio.DroppedChunks,
        RecoveryCount = _reconnectCount
    };

    private async Task ReportHealthAsync(string stage, string message, bool warning = false,
        string? error = null, bool onlyWhenHealthy = false)
    {
        // Serialize status changes so a queued, older translation cannot erase the
        // warning from a recognition failure while the next session is reconnecting.
        await _healthUpdates.WaitAsync();
        try
        {
            IncomingHealthSnapshot previous = Volatile.Read(ref _health);
            if (onlyWhenHealthy && (_awaitingRecognitionProgress || previous.Stage is "stalled" or "recovering" or "reconnecting")) return;
            Volatile.Write(ref _health, previous with { Stage = stage, Message = message, IsWarning = warning,
                UpdatedAt = DateTimeOffset.UtcNow, LatestError = error ?? previous.LatestError });
            await _status(message, warning);
        }
        finally { _healthUpdates.Release(); }
    }

    private async Task ClearQuietRecognitionWarningAsync(double voiceAgeSeconds, TimeSpan decoderAge,
        TimeSpan sendAge)
    {
        // Quiet input is not successful recognition. Only clear this particular
        // active-speech warning, preserving its diagnostic and recovery history.
        if (!_connected || _finalizingAudio || !double.IsFinite(voiceAgeSeconds) || voiceAgeSeconds < 4 ||
            decoderAge < TimeSpan.Zero || decoderAge > TimeSpan.FromSeconds(3) ||
            sendAge < TimeSpan.Zero || sendAge > TimeSpan.FromSeconds(3)) return;
        await _healthUpdates.WaitAsync();
        try
        {
            IncomingHealthSnapshot previous = Volatile.Read(ref _health);
            if (previous.Stage != "stalled" || previous.Message != RecognitionStalledMessage) return;
            string message = IncomingListening.WaitingMessage(CurrentSettings());
            Volatile.Write(ref _health, previous with { Stage = "waiting", Message = message,
                IsWarning = false, UpdatedAt = DateTimeOffset.UtcNow });
            await _status(message, false);
        }
        finally { _healthUpdates.Release(); }
    }

    private const string RecognitionStalledMessage = "Audio is arriving, but no new words were recognized. Still listening…";

    public IncomingTranslationService(
        AppSettings settings,
        Func<string, bool, Task> status,
        Func<string, Task> source,
        Func<TranslationDisplay, Task> translation,
        AiSpeakerIdentifier? speakerIdentifier = null,
        long initialBubbleId = 0)
    {
        _settings = settings.Copy();
        _nextUtteranceId = Math.Max(0, initialBubbleId);
        _aiSpeakerIdentifier = speakerIdentifier ?? new AiSpeakerIdentifier();
        if (_aiSpeakerIdentifier.SpeakerFeedbackSnapshot is SpeakerFeedbackState people)
            foreach (SpeakerPerson person in people.People) _personNames[person.Id] = person.Name;
        _status = status;
        _source = source;
        _translation = translation;
        _translations = Channel.CreateBounded<TranslationRequest>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false
        });
    }

    public void UpdateSettings(AppSettings settings)
    {
        AppSettings previous = CurrentSettings();
        lock (_settingsGate)
        {
            AppSettings next = settings.Copy();
            // Model admission/unload needs an awaited acknowledgement. Generic options updates
            // cannot silently re-enable it or strand an extractor after switching a checkbox off.
            next.UseAiSpeakerRecognition = _settings.UseAiSpeakerRecognition;
            _settings = next;
        }
        if (!IncomingListening.SameSource(previous, settings) || previous.RecognitionDelayMilliseconds != settings.RecognitionDelayMilliseconds)
        {
            _captureRefreshNeeded = true;
            _socket?.Abort();
        }
        Log($"Options applied: model={settings.TranslatorModel}, quality={settings.TranslationQuality}, neuralSpeakers={RecognitionStatus.Enabled}, settleMs={settings.IncomingSettleMilliseconds}.");
    }

    internal SpeakerRecognitionStatus RecognitionStatus
    {
        get
        {
            AppSettings settings = CurrentSettings();
            bool enabled = settings.UseAiSpeakerRecognition && !settings.IncomingSinglePerson && _aiSpeakerIdentifier.RecognitionEnabled;
            bool loaded = _aiSpeakerIdentifier.IsModelLoaded;
            return new(enabled, loaded, !enabled ? "Off" : loaded ? "Running" : "Enabled · waiting for speech");
        }
    }

    internal bool IsRecognitionAttributionCurrent(long epoch) => RecognitionStatus.Enabled &&
        epoch == Interlocked.Read(ref _speakerRecognitionEpoch);

    public async Task<SpeakerRecognitionStatus> ApplySpeakerRecognitionAsync(bool enabled, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _recognitionChanges.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            AppSettings previous = CurrentSettings();
            bool effective = enabled && !previous.IncomingSinglePerson;
            bool changed = previous.UseAiSpeakerRecognition != enabled || _aiSpeakerIdentifier.RecognitionEnabled != effective;
            await _aiSpeakerIdentifier.SetRecognitionEnabledAsync(effective, token).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_settingsGate)
            {
                AppSettings next = _settings.Copy(); next.UseAiSpeakerRecognition = enabled; _settings = next;
            }
            // This is temporary translation context, not saved names, profiles, corrections or
            // transcript history. Do not carry uncertain automatic identities across a stop/start.
            if (changed)
            {
                Interlocked.Increment(ref _speakerRecognitionEpoch);
                lock (_feedbackGate) _speakerContext.Clear();
            }
            SpeakerRecognitionStatus status = RecognitionStatus;
            Log(effective ? "Voice recognition enabled; the model loads only when clear speech needs identification."
                : "Voice recognition stopped and its model unloaded; speech recognition and translation continue.");
            return status;
        }
        finally { _recognitionChanges.Release(); }
    }

    public async Task ResetSpeakersAsync(CancellationToken cancellationToken)
    {
        await _recognitionChanges.WaitAsync(cancellationToken);
        try
        {
            // Invalidate in-flight matching before waiting for the extractor gate.
            // An old result cannot reintroduce names after this explicit reset.
            Interlocked.Increment(ref _speakerRecognitionEpoch);
            await _aiSpeakerIdentifier.ResetProfilesAsync(cancellationToken);
            lock (_latestUtteranceRevisions) _latestUtteranceRevisions.Clear();
            lock (_feedbackGate)
            {
                _speakerContext.Clear(); _manualSpeakers.Clear(); _evidenceBubbles.Clear(); _personNames.Clear(); _deletedPeople.Clear();
                _automaticSpeakerResults.Clear(); _latestIdentityJobs.Clear(); _groupPeople.Clear(); _groupBubbleVoices.Clear();
                _groupRejectedSuggestions.Clear();
            }
            Log("Speaker profiles and temporary voice associations reset by user.");
        }
        finally { _recognitionChanges.Release(); }
    }

    public SpeakerFeedbackState? SpeakerFeedbackSnapshot => _aiSpeakerIdentifier.SpeakerFeedbackSnapshot;

    public async Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default)
    {
        SpeakerFeedbackState state = await _aiSpeakerIdentifier.GetSpeakerFeedbackStateAsync(token);
        lock (_feedbackGate)
            foreach (SpeakerPerson person in state.People) _personNames[person.Id] = person.Name;
        return state;
    }

    public async Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default)
    {
        SpeakerPerson person = await _aiSpeakerIdentifier.SaveSpeakerPersonAsync(name, personId, token);
        lock (_feedbackGate) { _personNames[person.Id] = person.Name; _deletedPeople.Remove(person.Id); }
        Log("Saved speaker name/profile. No audio or translation model was started.");
        return person;
    }

    public Task<SpeakerPerson> SetPersonPhotoAsync(string personId, string? sourcePath, CancellationToken token = default) =>
        _aiSpeakerIdentifier.SetPersonPhotoAsync(personId, sourcePath, token);

    public async Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default)
    {
        await _aiSpeakerIdentifier.DeleteSpeakerPersonAsync(personId, token);
        lock (_feedbackGate)
        {
            _personNames.Remove(personId); _deletedPeople.Add(personId);
            foreach (long id in _speakerContext.Where(pair => pair.Value.Identity == "person:" + personId).Select(pair => pair.Key).ToArray())
                _speakerContext.Remove(id);
            foreach (long id in _manualSpeakers.Where(pair => pair.Value.PersonId == personId).Select(pair => pair.Key).ToArray())
                _manualSpeakers[id] = new(false, "Person removed.", null, "Unidentified speaker", false);
        }
        Log("Saved person and their confirmed voice examples removed locally.");
    }

    public Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default) =>
        _aiSpeakerIdentifier.AcknowledgeSpeakerLearningAsync(token);

    public async Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default)
    {
        await _speakerReviewChanges.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RegisterCurrentSpeakerSuggestion(evidenceId);
            SpeakerFeedbackResult result = await _aiSpeakerIdentifier.CorrectSpeakerAsync(evidenceId, personId, specialLabel, token);
            ApplyContextCorrection(evidenceId, result);
            await UpdateCorrectedTranslationCountAsync(evidenceId, result, token);
            Log("Speaker correction: " + result.Message);
            return result;
        }
        finally { _speakerReviewChanges.Release(); }
    }

    public async Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default)
    {
        await _speakerReviewChanges.WaitAsync(token).ConfigureAwait(false);
        try
        {
            SpeakerFeedbackResult result = await _aiSpeakerIdentifier.UndoSpeakerCorrectionAsync(evidenceId, token);
            ApplyContextCorrection(evidenceId, result);
            await UpdateCorrectedTranslationCountAsync(evidenceId, result, token);
            Log("Speaker correction undo: " + result.Message);
            return result;
        }
        finally { _speakerReviewChanges.Release(); }
    }

    private async Task UpdateCorrectedTranslationCountAsync(string evidenceId, SpeakerFeedbackResult result, CancellationToken token)
    {
        if (!result.Applied) return;
        bool entered = false;
        try
        {
            await _personCountChanges.WaitAsync(token).ConfigureAwait(false); entered = true;
            long id; DateTimeOffset at; string? personId;
            lock (_feedbackGate)
            {
                if (!_evidenceBubbles.TryGetValue(evidenceId, out id) || !_publishedCommittedTranslations.TryGetValue(id, out at)) return;
                SpeakerFeedbackResult latest = _manualSpeakers.TryGetValue(id, out SpeakerFeedbackResult? saved) ? saved : result;
                personId = latest.IsManuallyAttributed ? latest.PersonId : null;
            }
            await _aiSpeakerIdentifier.RecordPersonTranslationAsync(_translationSessionId + ":" + id, personId, at, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Log("Person label applied, but its translation count could not be updated: " + error); }
        finally { if (entered) _personCountChanges.Release(); }
    }

    private void ApplyContextCorrection(string evidenceId, SpeakerFeedbackResult result)
    {
        if (!result.Applied) return;
        ApplyGroupContextCorrection(evidenceId, result);
        lock (_feedbackGate)
        {
            if (!_evidenceBubbles.TryGetValue(evidenceId, out long id)) return;
            _manualSpeakers[id] = result;
            if (result.PersonId is not null) _personNames[result.PersonId] = result.Label;
            if (_speakerContext.TryGetValue(id, out SpeakerContextEntry? entry) && !entry.Identity.StartsWith("vibevoice:", StringComparison.Ordinal))
                _speakerContext[id] = entry with { Identity = !result.IsManuallyAttributed || result.PersonId is null || entry.IsMixed ? "" : "person:" + result.PersonId };
        }
    }

    private SpeakerAttribution ResolveSpeaker(long utteranceId, SpeakerAttribution speaker, long? recognitionEpoch = null)
    {
        lock (_feedbackGate) return ResolveSpeakerLocked(utteranceId, speaker, recognitionEpoch);
    }

    private SpeakerAttribution ResolveSpeakerLocked(long utteranceId, SpeakerAttribution speaker, long? recognitionEpoch = null)
    {
        bool hasCorrection = _manualSpeakers.TryGetValue(utteranceId, out SpeakerFeedbackResult? correction);
        if (!hasCorrection && CurrentSettings().IncomingSinglePerson) return SinglePersonAttributionLocked(speaker);
        bool explicitlyAttributed = hasCorrection && correction!.IsManuallyAttributed;
        bool automaticCurrent = RecognitionStatus.Enabled &&
            (recognitionEpoch is not long suppliedEpoch || suppliedEpoch == Interlocked.Read(ref _speakerRecognitionEpoch));
        if (!hasCorrection && _automaticSpeakerResults.TryGetValue(utteranceId, out var identified)
            && identified.Epoch == Interlocked.Read(ref _speakerRecognitionEpoch) && RecognitionStatus.Enabled)
            speaker = identified.Speaker;
        if (!explicitlyAttributed && !automaticCurrent)
            speaker = WithoutAutomaticIdentity(speaker);
        speaker = SuppressRejectedGroupSuggestionLocked(ResolveGroupSpeakerLocked(speaker, recognitionEpoch));
        if (hasCorrection && (explicitlyAttributed || automaticCurrent))
        {
            speaker = speaker with { Label = correction!.Label, PersonId = correction.PersonId,
                PersonConfirmed = explicitlyAttributed && correction.PersonId is not null,
                SuggestedPersonId = explicitlyAttributed ? null : correction.PersonId };
            if (!explicitlyAttributed) speaker = SuppressRejectedGroupSuggestionLocked(speaker);
        }
        if (speaker.PersonId is string personId)
        {
            if (_deletedPeople.Contains(personId)) return speaker with { Label = "Unidentified speaker", PersonId = null,
                SuggestedPersonId = null, PersonConfirmed = false, Confidence = 0 };
            if (_personNames.TryGetValue(personId, out string? name)) speaker = speaker with { Label = name };
        }
        return speaker;
    }

    private AppSettings CurrentSettings()
    {
        lock (_settingsGate) return _settings.Copy();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Saved Off is applied before any capture or background sentence work. Starting subtitles
        // must not temporarily expose an enabled speaker model or load a voice-ID extractor.
        await ApplySpeakerRecognitionAsync(CurrentSettings().UseAiSpeakerRecognition, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        _captureRefreshNeeded = true;

        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task translationWorker = SuperviseWorkerAsync(TranslationWorkerAsync, "Translation", workerCancellation.Token);
        Task settlementWorker = SuperviseWorkerAsync(SettleLoopAsync, "Sentence", workerCancellation.Token);
        Task audioWorker = SuperviseWorkerAsync(PumpAudioAsync, "Audio", workerCancellation.Token);
        Task identityWorker = SuperviseWorkerAsync(IdentifyQueuedSpeakersAsync, "People", workerCancellation.Token);
        int retryDelayMs = 350;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitForListeningApplicationAsync(cancellationToken);
                try
                {
                    if (_captureRefreshNeeded || _capture is null)
                    {
                        await RestartCaptureAsync(cancellationToken);
                        _captureRefreshNeeded = false;
                    }
                    if (CurrentSettings().IncomingSpeechEngine == "vibevoice")
                        await RunGroupSpeechSessionAsync(cancellationToken);
                    else if (IncomingRecognitionCatalog.UsesFireRed(CurrentSettings()))
                        await RunFireRedSessionAsync(cancellationToken);
                    else if (CurrentSettings().IncomingSpeechEngine == "nemotron")
                        await RunNativeSpeechSessionAsync(cancellationToken);
                    else
                        await RunVoxtralSessionAsync(cancellationToken);
                    retryDelayMs = 350;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _connected = false;
                    _reconnectCount++;
                    await FlushActiveAsync(cancellationToken);
                    if (exception is SessionRefreshException { ReplayUnrecognizedAudio: true })
                    {
                        long replayBytes = _continuousAudio.ReplayUnrecognizedAfter(_lastMeaningfulAudioEnd);
                        Log($"Recognition recovery retained {replayBytes / 32000.0:0.00}s of recent unrecognized audio in RAM; recognized audio excluded, capture stayed open.");
                    }
                    ResetTranscriptState();
                    // Keep unsent PCM across a real connection fault. Silence and
                    // subtitle boundaries never close a speech connection.
                    Log($"Realtime session reconnect #{_reconnectCount}: {exception.Message}");
                    bool plannedRefresh = exception is SessionRefreshException;
                    _awaitingRecognitionProgress = true;
                    await ReportHealthAsync(plannedRefresh ? "recovering" : "reconnecting", plannedRefresh
                        ? "Speech recognition paused. Reconnecting…"
                        : IncomingRecognitionCatalog.UsesFireRed(CurrentSettings())
                            ? "Speech recognition paused. Trying again…"
                            : "Speech connection lost. Reconnecting…", true, exception.Message);
                    await Task.Delay(plannedRefresh ? 80 : retryDelayMs, cancellationToken);
                    if (!plannedRefresh) retryDelayMs = Math.Min(3000, retryDelayMs * 2);
                }
            }
        }
        finally
        {
            _connected = false;
            workerCancellation.Cancel();
            _translations.Writer.TryComplete();
            _identificationJobs.Writer.TryComplete();
            try { await Task.WhenAll(translationWorker, settlementWorker, audioWorker, identityWorker); } catch (OperationCanceledException) { }
        }
    }

    private async Task SuperviseWorkerAsync(Func<CancellationToken, Task> worker, string name, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await worker(token);
                if (token.IsCancellationRequested) return;
                Log(name + " worker ended unexpectedly; restarting.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                // A background fault must not leave a healthy-looking but frozen overlay.
                Log(name + " worker failed and will restart: " + exception);
                await ReportHealthAsync("recovering", "Subtitles paused. Trying again…", true, name + " worker: " + exception.Message);
            }
            await Task.Delay(500, token);
        }
    }

    private void SetupCapture()
    {
        AppSettings settings = CurrentSettings();
        IncomingListening.Validate(settings);
        if (IncomingListening.UsesProcessCapture(settings))
        {
            _captureProcessId = IncomingListening.TryResolveProcess(settings)
                ?? throw new SessionRefreshException(IncomingListening.WaitingMessage(settings));
            _captureDevice = null;
            _captureDeviceId = "process:" + _captureProcessId;
            _captureDeviceName = IncomingListening.SourceName(settings) + " only (PID " + _captureProcessId + ")";
            _capture = new ProcessLoopbackCapture(_captureProcessId.Value);
        }
        else
        {
            _captureProcessId = null;
            _captureDevice = AudioRouting.Resolve(CurrentSettings().PlaybackDeviceId, _captureDeviceId);
            _captureDeviceId = _captureDevice.ID;
            _captureDeviceName = _captureDevice.FriendlyName;
            _capture = new WasapiLoopbackCapture(_captureDevice);
        }
        IWaveIn capture = _capture;
        Log("Playback capture selected: " + _captureDeviceName + " [" + _captureDeviceId + "].");
        WaveFormat captureFormat = _capture.WaveFormat;
        _buffered = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        BufferedWaveProvider captureBuffer = _buffered;
        _capture.DataAvailable += (_, args) =>
        {
            if (_disposed || !ReferenceEquals(_capture, capture)) return;
            Interlocked.Add(ref _audioBytesCaptured, args.BytesRecorded);
            _speakerTracker.Observe(args.Buffer, args.BytesRecorded, captureFormat);
            captureBuffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
        };
        _capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null) Log("Capture stopped: " + args.Exception.Message);
            if (!_disposed && ReferenceEquals(_capture, capture)) _captureRefreshNeeded = true;
        };

        ISampleProvider samples = _buffered.ToSampleProvider();
        ISampleProvider mono = new SpatialVoiceDownmixSampleProvider(samples);
        ISampleProvider resampled = mono.WaveFormat.SampleRate == 16000
            ? mono
            : new WdlResamplingSampleProvider(mono, 16000);
        _recognitionGain = new RecognitionGainSampleProvider(resampled, () => CurrentSettings().NormalizeQuietPlayback);
        lock (_pcmPumpGate) _pcmProvider = new SampleToWaveProvider16(_recognitionGain);
    }

    private async Task WaitForListeningApplicationAsync(CancellationToken cancellationToken)
    {
        while (IncomingListening.UsesProcessCapture(CurrentSettings()) && IncomingListening.TryResolveProcess(CurrentSettings()) is null)
        {
            AppSettings settings = CurrentSettings();
            IncomingListening.Validate(settings);
            await ReportHealthAsync("waiting", IncomingListening.WaitingMessage(settings));
            await Task.Delay(1000, cancellationToken);
        }
    }

    private Task StartCaptureAsync(CancellationToken token)
    {
        if (_capture is ProcessLoopbackCapture processCapture) return processCapture.StartRecordingAsync(token);
        _capture!.StartRecording();
        return Task.CompletedTask;
    }

    private async Task RestartCaptureAsync(CancellationToken token)
    {
        IWaveIn? oldCapture = _capture;
        _capture = null;
        lock (_pcmPumpGate)
        {
            _pcmProvider = null;
            _continuousAudio.Reset();
        }
        try { oldCapture?.StopRecording(); } catch { }
        oldCapture?.Dispose();
        _captureDevice?.Dispose();
        SetupCapture();
        await StartCaptureAsync(token);
        Log("Playback capture restarted successfully.");
    }

    private async Task PumpAudioAsync(CancellationToken cancellationToken)
    {
        byte[] pcm = new byte[AudioChunkBytes];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(80));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            bool backlogSkipped = false;
            lock (_pcmPumpGate)
            {
                if (_pcmProvider is not IWaveProvider provider) continue;
                int read = provider.Read(pcm, 0, pcm.Length);
                if (read < pcm.Length) Array.Clear(pcm, read, pcm.Length - read);
                DateTimeOffset end = DateTimeOffset.UtcNow;
                if (CurrentSettings().IncomingSpeechEngine == "vibevoice")
                    _groupAudio.Writer.TryWrite(new IncomingPcmChunk(pcm.ToArray(), end.AddMilliseconds(-80)));
                else
                    _continuousAudio.Observe(pcm, end.AddMilliseconds(-80));
                long dropped = _continuousAudio.DroppedChunks;
                if (dropped > _lastLoggedAudioDrops && end - _lastAudioDropLogAt >= TimeSpan.FromSeconds(5))
                {
                    Log($"Audio backlog capped at {_continuousAudio.CapacityMilliseconds} ms: skipped {dropped - _lastLoggedAudioDrops} older PCM chunks; newest audio retained.");
                    _lastLoggedAudioDrops = dropped;
                    _lastAudioDropLogAt = end;
                    backlogSkipped = true;
                }
            }
            if (backlogSkipped)
                await ReportHealthAsync("backlog", "Subtitles fell behind. Older audio was skipped to catch up.", true,
                    "Audio backlog reached its bounded limit.", onlyWhenHealthy: true);
        }
    }

    private async Task RunVoxtralSessionAsync(CancellationToken cancellationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var socket = new ClientWebSocket();
        _socket = socket;
        Task? sender = null;
        Task? receiver = null;
        Task? watchdog = null;
        bool completed = false;
        try
        {
        await WaitForSpeechServerAsync(sessionCancellation.Token);
        byte[] configure = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "session.configure",
            transcription_delay_ms = CurrentSettings().RecognitionDelayMilliseconds,
            audio = new { format = "pcm_s16le", sample_rate = 16000, channels = 1 },
            language = "auto",
            events = new { token = true, partial = true }
        });
        string created = await AwaitSessionCreatedAsync(async handshakeToken =>
        {
            await socket.ConnectAsync(new Uri(VoxtralSocket), handshakeToken);
            await socket.SendAsync(configure, WebSocketMessageType.Text, true, handshakeToken);
            return await ReceiveTextAsync(socket, handshakeToken);
        }, TimeSpan.FromSeconds(5), sessionCancellation.Token);
        using (JsonDocument document = JsonDocument.Parse(created))
        {
            if (document.RootElement.GetProperty("type").GetString() != "session.created")
                throw new InvalidOperationException("Voxtral did not create the incoming session: " + created);
        }

        ResetTranscriptState();
        _sessionStartedAt = DateTimeOffset.UtcNow;
        _audioTimelineStart = _sessionStartedAt;
        _sentAudioTimeline.Reset();
        _currentUtteranceId = Interlocked.Increment(ref _nextUtteranceId);
        _lastTextAudioEnd = null;
        _lastMeaningfulAudioEnd = null;
        _lastTranscriptAt = _sessionStartedAt;
        Interlocked.Exchange(ref _lastMeaningfulTicks, _sessionStartedAt.UtcTicks);
        Interlocked.Exchange(ref _lastDecoderProgressTicks, _sessionStartedAt.UtcTicks);
        Interlocked.Exchange(ref _lastAudioSendTicks, 0);
        _meaningfulTranscript = "";
        _blankRecoveryAttempts = 0;
        _utterancesInSession = 0;
        _finalizingAudio = false;
        _connected = true;
        await ReportHealthAsync(_awaitingRecognitionProgress ? "recovering" : "listening",
            _awaitingRecognitionProgress ? "Listening again… waiting for new words." : ListeningStatus(CurrentSettings()),
            _awaitingRecognitionProgress);
        Log($"Continuous speech session {_currentUtteranceId} connected; quiet audio remains connected (failure reconnects: {_reconnectCount}).");

        sender = SendAudioAsync(socket, sessionCancellation.Token);
        receiver = ReceiveTranscriptsAsync(socket, sessionCancellation.Token);
        watchdog = WatchdogAsync(sessionCancellation.Token);
        try
        {
            await AwaitNaturalCompletionAsync(sender, receiver, watchdog, TimeSpan.FromSeconds(8), cancellationToken);
        }
        catch (TimeoutException exception) when (_finalizingAudio)
        {
            Log("Speech context handoff stalled: " + exception.Message + $" Pending audio bytes={_continuousAudio.BufferedBytes}; releasing the old session.");
            throw new SessionRefreshException("The previous speech response took too long; continuing with the newest audio.");
        }
        await FlushActiveAsync(cancellationToken);
        completed = true;
        _connected = false;
        Log($"Speech context handed off normally; capture stayed open, pendingBytes={_continuousAudio.BufferedBytes}.");

        // A capacity handoff releases the lease before the next continuous session.
        // Subtitle boundaries never perform this close.
        using var closeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closeCancellation.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Speech context handoff", closeCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { socket.Abort(); }
        catch (WebSocketException) { socket.Abort(); }
        }
        finally
        {
            sessionCancellation.Cancel();
            if (!completed) socket.Abort();
            var workers = new[] { sender, receiver, watchdog }.OfType<Task>().ToArray();
            try { await Task.WhenAll(workers); } catch { /* The primary worker fault is surfaced above. */ }
            _finalizingAudio = false;
            _connected = false;
            if (ReferenceEquals(_socket, socket)) _socket = null;
        }
    }

    internal static async Task<string> AwaitSessionCreatedAsync(
        Func<CancellationToken, Task<string>> handshake, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try { return await handshake(deadline.Token).WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The speech server did not acknowledge the new session in time.");
        }
    }

    internal static async Task AwaitNaturalCompletionAsync(Task sender, Task receiver, Task watchdog,
        TimeSpan finalizationTimeout, CancellationToken cancellationToken, Func<bool>? hasPendingAudio = null,
        TimeSpan? pendingAudioTimeout = null)
    {
        Task first = await Task.WhenAny(sender, receiver, watchdog).WaitAsync(cancellationToken);
        if (ReferenceEquals(first, watchdog))
        {
            await watchdog;
            throw new InvalidOperationException("The speech watchdog ended unexpectedly.");
        }
        if (ReferenceEquals(first, sender)) await sender;
        else await receiver;
        Task remaining = ReferenceEquals(first, sender) ? receiver : sender;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + finalizationTimeout;
        DateTimeOffset? pendingDeadline = null;
        while (true)
        {
            // Give the final transcript a chance to arrive, but do not let an old
            // decoder monopolize the server while the two-second queue drops new speech.
            if (hasPendingAudio?.Invoke() == true)
                pendingDeadline ??= DateTimeOffset.UtcNow + (pendingAudioTimeout ?? TimeSpan.FromMilliseconds(1200));
            DateTimeOffset effectiveDeadline = pendingDeadline is DateTimeOffset queued && queued < deadline ? queued : deadline;
            TimeSpan remainingTime = effectiveDeadline - DateTimeOffset.UtcNow;
            if (remaining.IsCompleted) { await remaining; return; }
            if (watchdog.IsCompleted) { await watchdog; throw new InvalidOperationException("The speech watchdog ended unexpectedly."); }
            if (remainingTime <= TimeSpan.Zero)
                throw new TimeoutException(pendingDeadline is null ? "The final speech response did not arrive in time." : "New speech is waiting for the previous final response.");
            Task tick = Task.Delay(remainingTime < TimeSpan.FromMilliseconds(50) ? remainingTime : TimeSpan.FromMilliseconds(50), cancellationToken);
            Task drain = await Task.WhenAny(remaining, watchdog, tick).WaitAsync(cancellationToken);
            if (!ReferenceEquals(drain, tick))
            {
                await drain;
                if (ReferenceEquals(drain, watchdog)) throw new InvalidOperationException("The speech watchdog ended unexpectedly.");
                return;
            }
            await tick;
        }
    }

    private Task SendAudioAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        bool first = true;
        return IncomingContinuousSessionPolicy.StreamAsync(_continuousAudio, async (chunk, token) =>
        {
            if (first) { _audioTimelineStart = chunk.At; first = false; }
            _sentAudioTimeline.Append(chunk.At, chunk.Bytes.Length);
            await socket.SendAsync(chunk.Bytes.AsMemory(), WebSocketMessageType.Binary, true, token);
            Interlocked.Add(ref _audioBytesSent, chunk.Bytes.Length);
            Interlocked.Exchange(ref _lastAudioSendTicks, DateTimeOffset.UtcNow.UtcTicks);
        }, async token =>
        {
            _finalizingAudio = true;
            Log("Refreshing speech context at its text/time capacity; playback capture remains continuous.");
            byte[] end = JsonSerializer.SerializeToUtf8Bytes(new { type = "input_audio.end" });
            await socket.SendAsync(end, WebSocketMessageType.Text, true, token);
        }, () =>
        {
            lock (_gate) return IncomingContinuousSessionPolicy.ShouldRollOver(_latest, DateTimeOffset.UtcNow - _sessionStartedAt);
        }, cancellationToken);
    }

    private async Task ReceiveTranscriptsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        bool receivedFinal = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            string json = await ReceiveTextAsync(socket, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement value) ? value.GetString() : null;
            if (type is "transcript.partial" or "transcript.final")
            {
                Interlocked.Increment(ref _transcriptEvents);
                _lastTranscriptAt = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref _lastDecoderProgressTicks, _lastTranscriptAt.UtcTicks);
                DateTimeOffset? audioEnd = root.TryGetProperty("audio_end_ms", out JsonElement endValue) && endValue.TryGetInt64(out long endMs)
                    ? _sentAudioTimeline.Map(endMs) : null;
                await ObserveTranscriptAsync(root.GetProperty("text").GetString() ?? "", audioEnd);
                if (type == "transcript.final") receivedFinal = true;
            }
            else if (type == "session.completed")
            {
                if (!_finalizingAudio || !receivedFinal)
                    throw new InvalidOperationException("The speech session completed without a requested final transcript.");
                return;
            }
            else if (type == "transcript.token")
            {
                if (root.TryGetProperty("token_id", out JsonElement tokenValue) && tokenValue.TryGetInt32(out int tokenId))
                {
                    Interlocked.Exchange(ref _lastDecoderProgressTicks, DateTimeOffset.UtcNow.UtcTicks);
                    if (tokenId == 32) Interlocked.Increment(ref _padTokens);
                    else if (tokenId is not 0 and not 1 and not 2)
                    {
                        Interlocked.Increment(ref _lexicalTokens);
                        // Tokens measure decoder activity, not user-visible progress.
                        // A looping decoder can emit lexical tokens without changing its text.
                    }
                }
            }
            else if (type == "error")
            {
                throw new InvalidOperationException("Voxtral error: " + json);
            }
            else if (type == "session.warning")
            {
                string? code = root.TryGetProperty("code", out JsonElement codeValue) ? codeValue.GetString() : null;
                if (code == "decoder_blank_recovered")
                {
                    _blankRecoveryAttempts = root.TryGetProperty("failed_recovery_attempts", out JsonElement attemptsValue) && attemptsValue.TryGetInt32(out int attempts)
                        ? attempts : _blankRecoveryAttempts + 1;
                    Log($"Decoder recovery attempts without meaningful progress: {_blankRecoveryAttempts}.");
                }
                else if (code == "decoder_progress_restored")
                {
                    Log("Decoder reported recovery; waiting for changed transcript text before clearing the stall.");
                }
                else if (DateTimeOffset.UtcNow - _lastLagLogAt >= TimeSpan.FromSeconds(30))
                {
                    _lastLagLogAt = DateTimeOffset.UtcNow;
                    Log("Voxtral warning (throttled): " + json);
                }
            }
        }
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - _lastDiagnosticAt >= TimeSpan.FromSeconds(15))
            {
                _lastDiagnosticAt = now;
                double audibleAge = _speakerTracker.LastAudibleAt == DateTimeOffset.MinValue ? -1 : (now - _speakerTracker.LastAudibleAt).TotalSeconds;
                double voiceAge = _speakerTracker.LastVoiceLikeAt == DateTimeOffset.MinValue ? -1 : (now - _speakerTracker.LastVoiceLikeAt).TotalSeconds;
                Log($"Speech diagnostic: capturedBytes={Interlocked.Read(ref _audioBytesCaptured)}, sentBytes={Interlocked.Read(ref _audioBytesSent)}, transcripts={Interlocked.Read(ref _transcriptEvents)}, audibleAgeSec={audibleAge:0.0}, voiceAgeSec={voiceAge:0.0}, transcriptAgeSec={(now - _lastTranscriptAt).TotalSeconds:0.0}, bufferedBytes={_buffered?.BufferedBytes ?? 0}.");
                Log($"Recognition input: device={_captureDeviceName}, rawRms={_recognitionGain?.LastRms:0.000000}, rawPeak={_recognitionGain?.LastPeak:0.000000}, gain={_recognitionGain?.LastGain:0.0}, padTokens={Interlocked.Read(ref _padTokens)}, lexicalTokens={Interlocked.Read(ref _lexicalTokens)}.");
            }
            CheckCaptureState(now);
            long queueDrops = Interlocked.Read(ref _translationQueueDrops);
            if (queueDrops > _reportedTranslationQueueDrops)
            {
                _reportedTranslationQueueDrops = queueDrops;
                await ReportHealthAsync("translation-backlog", "Translation is catching up…", true,
                    "An interim preview was skipped because the translation queue was busy; finished sentences were retained.", onlyWhenHealthy: true);
            }
            double meaningfulAge = (now.UtcTicks - Interlocked.Read(ref _lastMeaningfulTicks)) / (double)TimeSpan.TicksPerSecond;
            double fallbackAge = (now.UtcTicks - Interlocked.Read(ref _lastFallbackTicks)) / (double)TimeSpan.TicksPerSecond;
            double voicedSeconds = _speakerTracker.VoicedSeconds(now.AddSeconds(-8), now);
            double activeVoiceAge = (now - _speakerTracker.LastVoiceLikeAt).TotalSeconds;
            bool speechStillActive = voicedSeconds >= 1.0 && activeVoiceAge >= 0 && activeVoiceAge <= 2.5;
            if (!_finalizingAudio && speechStillActive && meaningfulAge >= 10 &&
                (HealthSnapshot.Stage != "stalled" || HealthSnapshot.Message != RecognitionStalledMessage))
            {
                Log($"Speech stalled: audio is arriving without changed transcript text for {meaningfulAge:0.0}s; decoderAttempts={_blankRecoveryAttempts}; fallbackCount={_consecutiveStallFallbacks}.");
                _awaitingRecognitionProgress = true;
                await ReportHealthAsync("stalled", RecognitionStalledMessage, true,
                    "Speech input is active without changed transcript text.");
            }
            TimeSpan decoderAge = TimeSpan.FromTicks(now.UtcTicks - Interlocked.Read(ref _lastDecoderProgressTicks));
            long sentAt = Interlocked.Read(ref _lastAudioSendTicks);
            TimeSpan sendAge = sentAt == 0 ? TimeSpan.MaxValue : TimeSpan.FromTicks(now.UtcTicks - sentAt);
            await ClearQuietRecognitionWarningAsync(activeVoiceAge, decoderAge, sendAge);
            if (sendAge >= TimeSpan.Zero && sendAge <= TimeSpan.FromSeconds(3) &&
                IncomingContinuousSessionPolicy.ShouldRecoverRecognition(voicedSeconds, activeVoiceAge,
                meaningfulAge, (now - _sessionStartedAt).TotalSeconds, fallbackAge,
                _blankRecoveryAttempts, _consecutiveStallFallbacks, _finalizingAudio))
            {
                // PAD tokens prove transport activity, not recognition success.
                // Recheck actual words before replacing a live but blank decoder.
                if (now.UtcTicks - Interlocked.Read(ref _lastMeaningfulTicks) < TimeSpan.FromSeconds(12).Ticks) continue;
                _consecutiveStallFallbacks++;
                Interlocked.Exchange(ref _lastFallbackTicks, now.UtcTicks);
                throw new SessionRefreshException($"Speech remained active without new words for {meaningfulAge:0.0}s despite decoder activity; refreshing recognition, attempt {_consecutiveStallFallbacks}.",
                    replayUnrecognizedAudio: true);
            }
            if (IncomingContinuousSessionPolicy.ShouldRecoverTransport(decoderAge, sendAge,
                now - _sessionStartedAt, _finalizingAudio) && fallbackAge >= StallRecoveryCooldownSeconds(_consecutiveStallFallbacks))
            {
                // Recheck progress immediately before aborting; PAD or a late partial
                // must prevent an unnecessary reset of a healthy silent stream.
                if (now.UtcTicks - Interlocked.Read(ref _lastDecoderProgressTicks) < TimeSpan.FromSeconds(30).Ticks) continue;
                _consecutiveStallFallbacks++;
                Interlocked.Exchange(ref _lastFallbackTicks, now.UtcTicks);
                throw new SessionRefreshException("Speech decoder stopped responding to continuous audio for 30 seconds; recovery " + _consecutiveStallFallbacks +
                    $" (next cooldown {StallRecoveryCooldownSeconds(_consecutiveStallFallbacks):0}s). Unsent audio is retained.");
            }

        }
    }

    private void CheckCaptureState(DateTimeOffset now)
    {
        if (_captureRefreshNeeded) throw new SessionRefreshException("Playback capture needs to be reopened.");
        if (now - _lastDeviceProbeAt < TimeSpan.FromSeconds(3)) return;
        _lastDeviceProbeAt = now;
        try
        {
            AppSettings settings = CurrentSettings();
            if (IncomingListening.UsesProcessCapture(settings))
            {
                if (IncomingListening.TryResolveProcess(settings) != _captureProcessId)
                {
                    _captureRefreshNeeded = true;
                    throw new SessionRefreshException(IncomingListening.SourceName(settings) + " closed or restarted; following only the selected application.");
                }
            }
            else
            {
                using var desiredDevice = AudioRouting.Resolve(CurrentSettings().PlaybackDeviceId, _captureDeviceId);
                if (desiredDevice.ID != _captureDeviceId)
                {
                    _captureRefreshNeeded = true;
                    throw new SessionRefreshException("The playback device changed; following the new output.");
                }
            }
        }
        catch (SessionRefreshException) { throw; }
        catch (Exception exception) { Log("Playback device probe: " + exception.Message); }
    }

    internal static bool ShouldRefreshStalledSpeech(double voicedSeconds, double voiceAge, double meaningfulAge,
        double sessionAge, double fallbackAge, int attempts, int consecutiveFallbacks) =>
        voicedSeconds >= 1.0 && voiceAge >= 0 && voiceAge <= 2.5 && meaningfulAge >= 10 &&
        sessionAge >= 10 && fallbackAge >= StallRecoveryCooldownSeconds(consecutiveFallbacks) &&
        (attempts >= 2 || meaningfulAge >= 18);

    internal static double StallRecoveryCooldownSeconds(int consecutiveFallbacks) =>
        consecutiveFallbacks < 2 ? 20 : consecutiveFallbacks == 2 ? 30 : 40;

    internal static bool IsMeaningfulTranscriptChange(string previous, string current)
    {
        string before = new(previous.Where(char.IsLetterOrDigit).ToArray());
        string after = new(current.Where(char.IsLetterOrDigit).ToArray());
        return after.Length > 0 && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
    }

    private void MarkMeaningfulProgress()
    {
        Interlocked.Exchange(ref _lastMeaningfulTicks, DateTimeOffset.UtcNow.UtcTicks);
        _consecutiveStallFallbacks = 0;
        _blankRecoveryAttempts = 0;
    }

    private async Task WaitForSpeechServerAsync(CancellationToken cancellationToken)
    {
        // A cancelled CUDA graph can take a moment to release the single-stream lease.
        // Opening a second WebSocket before that happens causes avoidable HTTP 503 errors.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCancellation.CancelAfter(TimeSpan.FromSeconds(2));
            using HttpResponseMessage response = await _http.GetAsync("http://127.0.0.1:8081/health", probeCancellation.Token);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(probeCancellation.Token), cancellationToken: probeCancellation.Token);
            JsonElement health = document.RootElement;
            bool ready = health.TryGetProperty("ready", out JsonElement readyValue) && readyValue.GetBoolean();
            bool busy = health.TryGetProperty("busy", out JsonElement busyValue) && busyValue.GetBoolean();
            if (ready && !busy) return;
            if (DateTimeOffset.UtcNow >= deadline) throw new SessionRefreshException("Waiting for the speech server to release its previous stream.");
            await Task.Delay(150, cancellationToken);
        }
    }

    private async Task ObserveTranscriptAsync(string cumulative, DateTimeOffset? audioEnd = null)
    {
        string active;
        bool meaningfulChanged = false;
        lock (_gate)
        {
            if (string.Equals(cumulative, _latest, StringComparison.Ordinal)) return;
            if (IsMeaningfulTranscriptChange(_meaningfulTranscript, cumulative))
            {
                _meaningfulTranscript = cumulative;
                // Only the server's mapped audio position acknowledges captured
                // PCM. Wall-clock arrival time is unsafe when recognition lags.
                if (audioEnd is DateTimeOffset recognizedEnd &&
                    (_lastMeaningfulAudioEnd is null || recognizedEnd > _lastMeaningfulAudioEnd.Value))
                    _lastMeaningfulAudioEnd = recognizedEnd;
                MarkMeaningfulProgress();
                Interlocked.Exchange(ref _lastWordTicks, DateTimeOffset.UtcNow.UtcTicks);
                meaningfulChanged = true;
            }
            _lastTextAudioEnd = audioEnd ?? DateTimeOffset.UtcNow.AddMilliseconds(-CurrentSettings().RecognitionDelayMilliseconds);
            _latest = cumulative;
            // Recognition keeps its full turn context. Display/translation only use
            // the current sentence-sized suffix, never replaying committed bubbles.
            _subtitleSegments.Observe(_latest, DateTimeOffset.UtcNow);
            active = _subtitleSegments.ActiveText;
        }
        if (_awaitingRecognitionProgress && meaningfulChanged)
        {
            _awaitingRecognitionProgress = false;
            Log("Speech progress restored: changed transcript text arrived; recovery cooldown reset.");
            await ReportHealthAsync("listening", ListeningStatus(CurrentSettings()));
        }
        if (!string.IsNullOrWhiteSpace(active)) await _source("Listening: " + active);
    }

    private async Task SettleLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (CurrentSettings().IncomingSpeechEngine == "vibevoice")
            {
                await SettleGroupSentencesAsync(cancellationToken);
                continue;
            }
            TranslationRequest? request = await TakeSettledRequestAsync(cancellationToken);
            if (request is not null && !IsSuperseded(request.Value))
            {
                await QueueTranslationAsync(request.Value, cancellationToken);
                await _source($"{LiveSpeakerLabel(request.Value.Speaker)}: {request.Value.Source}");
            }
        }
    }

    private async Task<TranslationRequest?> TakeSettledRequestAsync(CancellationToken cancellationToken)
    {
        if (_finalizingAudio) return null;
        return await TakeSubtitleSegmentAsync(final: false, cancellationToken);
    }

    private Task<TranslationRequest?> TakeSubtitleSegmentAsync(bool final, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SubtitleSegment segment;
        long bubbleId;
        DateTimeOffset end = DateTimeOffset.UtcNow;
        DateTimeOffset evidenceEnd, evidenceStart;
        lock (_gate)
        {
            _subtitleSegments.StabilityMilliseconds = CurrentSettings().SubtitlePreviewStableMilliseconds;
            if (!_subtitleSegments.TryTakeReady(end, final,
                TimeSpan.FromMilliseconds(Math.Clamp(CurrentSettings().IncomingSettleMilliseconds, 600, 2400)), out segment)) return Task.FromResult<TranslationRequest?>(null);
            if (!_subtitleBubbleIds.TryGetValue(segment.SequenceIndex, out bubbleId))
                _subtitleBubbleIds[segment.SequenceIndex] = bubbleId = Interlocked.Increment(ref _nextUtteranceId);
            evidenceEnd = _lastTextAudioEnd ?? end;
            double seconds = Math.Clamp((end - segment.StartedAt).TotalSeconds + 1, 1.5, 12);
            evidenceStart = evidenceEnd.AddSeconds(-seconds);
            if (evidenceStart < _audioTimelineStart) evidenceStart = _audioTimelineStart;
        }
        long recognitionEpoch = Interlocked.Read(ref _speakerRecognitionEpoch);
        // Capture evidence before scheduling: expensive inference must never hold text release.
        SpeakerAttribution speaker = QueueSpeakerIdentification(bubbleId, segment.Text, evidenceStart, evidenceEnd, end, recognitionEpoch);
        Interlocked.Increment(ref _utterancesInSession);
        Log($"Subtitle segment: id={bubbleId}; committed={segment.IsCommitted}; sourceChars={segment.Text.Length}.");
        return Task.FromResult<TranslationRequest?>(CreateTranslationRequest(segment.Text, speaker, evidenceStart, end, bubbleId, recognitionEpoch) with { IsCommitted = segment.IsCommitted });
    }

    private async Task<SpeakerAttribution> IdentifySpeakerAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        SpeakerAttribution fallback = _speakerTracker.Identify(start, end);
        long recognitionEpoch = Interlocked.Read(ref _speakerRecognitionEpoch);
        AppSettings settings = CurrentSettings();
        if (settings.IncomingSinglePerson) return QueueSinglePersonAttribution(fallback);
        bool automatic = settings.UseAiSpeakerRecognition && _aiSpeakerIdentifier.RecognitionEnabled;
        if (!automatic) fallback = WithoutAutomaticIdentity(fallback);
        // With neural recognition off, the backend records only label metadata and never loads a model.
        float[] samples = automatic
            ? _speakerTracker.GetMonoAudio16k(fallback.EvidenceStart, fallback.EvidenceEnd) : Array.Empty<float>();
        try
        {
            // A stale Off snapshot must not call the legacy IdentifyAsync(false) path, which can
            // otherwise disable a newer explicit Enable. Recording a manual label is state-neutral.
            SpeakerAttribution result = automatic
                ? await _aiSpeakerIdentifier.IdentifyAsync(samples, fallback, settings, cancellationToken)
                : _aiSpeakerIdentifier.RecordLabelOnlyObservation(fallback);
            Log("Speaker ID: " + _aiSpeakerIdentifier.LastDiagnostic);
            return automatic && RecognitionStatus.Enabled && recognitionEpoch == Interlocked.Read(ref _speakerRecognitionEpoch)
                ? result : WithoutAutomaticIdentity(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // A speaker-model failure must not silently kill the sentence/translation worker.
            Log("Speaker identification failed; translation continues without a voice ID: " + exception);
            return automatic && RecognitionStatus.Enabled && recognitionEpoch == Interlocked.Read(ref _speakerRecognitionEpoch)
                ? fallback : WithoutAutomaticIdentity(fallback);
        }
    }

    private static SpeakerAttribution WithoutAutomaticIdentity(SpeakerAttribution speaker) => speaker with
    {
        Label = GroupSpeechProtocol.VoiceLabel(speaker.SessionVoiceKey), PersonId = null, SuggestedPersonId = null,
        PersonConfirmed = false, Confidence = 0, Pan = 0, CanLearn = false,
        Diagnostic = "Voice recognition is off; automatic speaker identity is not inferred."
    };

    private TranslationRequest CreateTranslationRequest(string text, SpeakerAttribution speaker, DateTimeOffset start, DateTimeOffset end, long utteranceId,
        long? recognitionEpoch = null)
    {
        long attributionEpoch = recognitionEpoch ?? Interlocked.Read(ref _speakerRecognitionEpoch);
        if (utteranceId == 0) utteranceId = Interlocked.Increment(ref _nextUtteranceId);
        if (string.IsNullOrEmpty(speaker.EvidenceId)) speaker = speaker with { EvidenceId = Guid.NewGuid().ToString("N") };
        lock (_feedbackGate)
        {
            _evidenceBubbles[speaker.EvidenceId] = utteranceId;
            if (speaker.PersonId is not null) _personNames.TryAdd(speaker.PersonId, speaker.Label);
            foreach (string old in _evidenceBubbles.Where(pair => pair.Value < _nextUtteranceId - 250).Select(pair => pair.Key).ToArray())
                _evidenceBubbles.Remove(old);
            while (_evidenceBubbles.Count > 1000) _evidenceBubbles.Remove(_evidenceBubbles.Keys.First());
            foreach (long old in _manualSpeakers.Keys.Where(id => id < _nextUtteranceId - 250).ToArray()) _manualSpeakers.Remove(old);
            speaker = ResolveSpeakerLocked(utteranceId, speaker, attributionEpoch);
        }
        lock (_latestUtteranceRevisions)
        {
            if (!_latestUtteranceRevisions.TryGetValue(utteranceId, out DateTimeOffset previous) || end > previous)
                _latestUtteranceRevisions[utteranceId] = end;
            // Revisions use one sentence ID; committed sentences retain separate history
            // even when the ASR voice turn continues without an audio pause.
            foreach (long old in _latestUtteranceRevisions.Keys.Where(id => id < _nextUtteranceId - 250).ToArray())
                _latestUtteranceRevisions.Remove(old);
            return new TranslationRequest(text, speaker, utteranceId, end)
                { SpeakerRecognitionEpoch = attributionEpoch };
        }
    }

    private static string ListeningStatus(AppSettings settings) =>
        $"Listening · {settings.IncomingTargetLanguage} subtitles";

    private static string DominantSpeaker(string label)
    {
        int plus = label.IndexOf(" + ", StringComparison.Ordinal);
        return (plus > 0 ? label[..plus] : label).Replace(" (mixed)", "", StringComparison.OrdinalIgnoreCase);
    }

    private async Task FlushActiveAsync(CancellationToken cancellationToken = default)
    {
        // A real final/refresh flush emits only the uncommitted suffix, rather than
        // replacing earlier sentence bubbles with the entire voice-turn transcript.
        while (await TakeSubtitleSegmentAsync(final: true, cancellationToken) is TranslationRequest request)
        {
            await QueueTranslationAsync(request, cancellationToken);
            await _source($"{LiveSpeakerLabel(request.Speaker)}: {request.Source}");
        }
    }

    private async Task TranslationWorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (TranslationRequest request in _translations.Reader.ReadAllAsync(cancellationToken))
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_latestUtteranceRevisions)
            {
                _activeTranslation = request;
                _activeTranslationCancellation = operation;
                if (IsSuperseded(request)) operation.Cancel();
            }
            try { await TranslateAndPublishAsync(request, operation.Token); }
            finally
            {
                lock (_latestUtteranceRevisions)
                    if (ReferenceEquals(_activeTranslationCancellation, operation))
                    { _activeTranslationCancellation = null; _activeTranslation = null; }
            }
        }
    }

    private async Task TranslateAndPublishAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (IsSuperseded(request)) return;
            request = request with { Speaker = ResolveSpeaker(request.UtteranceId, request.Speaker, request.SpeakerRecognitionEpoch) };
            await ReportHealthAsync("translating", "Translating…", onlyWhenHealthy: true);
            AppSettings settings = CurrentSettings();
            string context = GetContext(request.Speaker, request.UtteranceId, settings.TranslationQuality == "Quality" ? 4 : settings.TranslationQuality == "Balanced" ? 2 : 0);
            string languages = settings.IncomingSourceLanguage != "Automatic" ? settings.IncomingSourceLanguage
                : settings.DetectAnySourceLanguage ? "any language (detect automatically)" : string.Join(", ", settings.SourceLanguages);
            string target = SettingsStore.TargetDescription(settings.IncomingTargetLanguage);
            string prompt = $"The expected spoken languages are: {languages}. Translate the CURRENT transcript into concise, natural {target} for live subtitles. Preserve meaning, tone and names. Speech recognition may be imperfect; do not invent meaning to repair unrecognizable words. If it is already in the target language, reproduce it cleanly. Return ONLY the {target} translation, with no source text, labels, notes or explanations. /no_think";
            if(!string.IsNullOrWhiteSpace(settings.Glossary)) prompt += " Glossary (use only for matching source terms; do not invent missing words): " + settings.Glossary[..Math.Min(2000,settings.Glossary.Length)];
            if (!string.IsNullOrWhiteSpace(context)) prompt += " Previous speech for context only (do not translate it again): " + context;
            string translated = await RequestTranslationAsync(request.Source, prompt, settings, cancellationToken);
            if (NeedsTargetRetry(translated, settings.IncomingTargetLanguage))
            {
                Log($"Wrong-language translator output rejected and retried ({settings.IncomingTargetLanguage} requested): {translated}");
                translated = await RequestTranslationAsync(
                    request.Source,
                    $"Translate the user's text to {target.ToUpperInvariant()}. Your entire reply MUST use {target}. Do not repeat the source text or explain. Output only the translation. /no_think",
                    settings,
                    cancellationToken,
                    strictRetry: true);
            }
            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException("Bionic returned an empty translation.");
            if (NeedsTargetRetry(translated, settings.IncomingTargetLanguage))
                throw new InvalidOperationException($"The model returned the wrong output language twice ({settings.IncomingTargetLanguage} requested). Try another translator model.");
            if (IsSuperseded(request)) return;
            request = request with { Speaker = ResolveSpeaker(request.UtteranceId, request.Speaker, request.SpeakerRecognitionEpoch) };
            string pan = request.Speaker.Pan switch
            {
                < -0.20 => "left",
                > 0.20 => "right",
                _ => "center"
            };
            Log($"Source: [{request.Speaker.Label}; {pan}; confidence {request.Speaker.Confidence:0.00}] {request.Source} | {settings.IncomingTargetLanguage}: {translated}");
            RememberContext(request);
            bool manuallyAttributed;
            lock (_feedbackGate) manuallyAttributed = IsConfirmedDisplayLocked(request.UtteranceId);
            var display = new TranslationDisplay(request.Speaker.Label, request.Source, translated, request.UtteranceId, request.At)
                { EvidenceId = request.Speaker.EvidenceId, PersonId = request.Speaker.PersonId,
                    SpeakerRecognitionEpoch = request.SpeakerRecognitionEpoch, IsManuallyAttributed = manuallyAttributed,
                    SessionVoiceKey = request.Speaker.SessionVoiceKey };
            await PublishTranslationDisplayAsync(display);
            if (request.IsCommitted) await RecordPublishedTranslationAsync(request, cancellationToken);
            await ReportHealthAsync(_connected ? "listening" : "waiting",
                _connected ? ListeningStatus(settings) : "Waiting for speech…", onlyWhenHealthy: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            Log($"Translation timed out and was dropped: {request.Source}");
            await ReportHealthAsync("translation-error", "Translation timed out. Still listening…", true,
                "The translator did not return before its deadline.", onlyWhenHealthy: true);
        }
        catch (Exception exception)
        {
            Log("Translation failed: " + exception);
            await ReportHealthAsync("translation-error", "Translation failed. Still listening…", true,
                exception.Message, onlyWhenHealthy: true);
        }
    }

    private async Task RecordPublishedTranslationAsync(TranslationRequest request, CancellationToken token)
    {
        if (!request.IsCommitted) return;
        bool entered = false;
        try
        {
            await _personCountChanges.WaitAsync(token).ConfigureAwait(false); entered = true;
            SpeakerAttribution attributed;
            lock (_feedbackGate)
            {
                _publishedCommittedTranslations.TryAdd(request.UtteranceId, request.At);
                attributed = ResolveSpeakerLocked(request.UtteranceId, request.Speaker, request.SpeakerRecognitionEpoch);
                foreach (long old in _publishedCommittedTranslations.Keys.Where(id => id < _nextUtteranceId - 400).ToArray())
                    _publishedCommittedTranslations.Remove(old);
            }
            await _aiSpeakerIdentifier.RecordPersonTranslationAsync(_translationSessionId + ":" + request.UtteranceId,
                attributed.PersonConfirmed ? attributed.PersonId : null, request.At, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Log("Subtitle published, but its person translation count could not be saved: " + error); }
        finally { if (entered) _personCountChanges.Release(); }
    }

    private bool IsSuperseded(TranslationRequest request)
    {
        // A completed sentence cannot disappear when a newer preview arrives.
        if (request.IsCommitted) return false;
        lock (_latestUtteranceRevisions)
            return _latestUtteranceRevisions.TryGetValue(request.UtteranceId, out DateTimeOffset revision) && revision > request.At;
    }

    private async Task<string> RequestTranslationAsync(
        string source,
        string prompt,
        AppSettings settings,
        CancellationToken cancellationToken,
        bool strictRetry = false)
    {
        int maxTokens = settings.TranslationQuality is "Quality" or "Careful" ? 192 : settings.TranslationQuality == "Balanced" ? 128 : 128;
        int timeoutSeconds = settings.TranslationQuality is "Quality" or "Careful" ? 22 : settings.TranslationQuality == "Balanced" ? 14 : 12;
        bool specialized = TranslationModelPrompts.RequiresExplicitSource(settings.TranslatorModel);
        if (specialized)
        {
            string? invalid = TranslationModelCapabilities.Validate(settings.TranslatorModel, settings.IncomingSourceLanguage, new[] { settings.IncomingTargetLanguage });
            if (invalid is not null) throw new InvalidOperationException(invalid);
            if (TranslationModelCapabilities.RequiresManagedRuntime(settings.TranslatorModel) && !settings.UseManagedTranslator)
                throw new InvalidOperationException("TranslateGemma requires SayWhat?'s built-in runtime and its compatible translation template. Select the built-in runtime in Settings.");
            cancellationToken.ThrowIfCancellationRequested();
            if (TranslationModelPrompts.CanonicalLanguage(settings.IncomingSourceLanguage) == TranslationModelPrompts.CanonicalLanguage(settings.IncomingTargetLanguage))
                return source.Trim();
        }
        object payload = IncomingTranslationPayload(source, prompt, settings, maxTokens, strictRetry);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        string endpoint = settings.TranslatorBaseUrl.TrimEnd('/') + "/chat/completions";
        using HttpResponseMessage response = await _http.PostAsJsonAsync(endpoint, payload, requestCancellation.Token);
        string body = await response.Content.ReadAsStringAsync(requestCancellation.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Translator returned {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
    }

    internal static object IncomingTranslationPayload(string source, string prompt, AppSettings settings, int maxTokens, bool strictRetry = false)
    {
        if (TranslationModelPrompts.RequiresExplicitSource(settings.TranslatorModel))
        {
            SpecializedTranslationTask task = TranslationModelPrompts.SpecializedTask(settings.TranslatorModel,
                settings.IncomingSourceLanguage, settings.IncomingTargetLanguage, source);
            // New dedicated adapters never receive generic context, glossary,
            // Qwen reasoning toggles, or a retry prefix inside the raw source.
            var payload = new Dictionary<string, object> {
                ["model"] = settings.TranslatorModel,
                ["messages"] = task.Messages.Select(message => new { role = message.Role, content = message.Content }).ToArray(),
                ["temperature"] = task.Temperature, ["top_p"] = task.TopP, ["max_tokens"] = maxTokens };
            if (task.MinP is double minimumProbability) payload["min_p"] = minimumProbability;
            if (task.RepeatPenalty is double repeatPenalty) payload["repeat_penalty"] = repeatPenalty;
            return payload;
        }
        bool dedicated = settings.TranslatorModel.Contains("hy-mt", StringComparison.OrdinalIgnoreCase);
        string dedicatedReferences = string.IsNullOrWhiteSpace(settings.Glossary) ? "" : "Reference the following translations (only when the source terms occur):\n"+settings.Glossary[..Math.Min(2000,settings.Glossary.Length)]+"\n\n";
        return new
        {
            model = settings.TranslatorModel,
            messages = dedicated ? new object[] { new { role = "user", content = dedicatedReferences + $"Translate the following text into {settings.IncomingTargetLanguage}. Note that you should only output the translated result without any additional explanation:\n{source}" } } : new object[]
            {
                new { role = "system", content = prompt },
                new { role = "user", content = strictRetry ? $"{settings.IncomingTargetLanguage.ToUpperInvariant()} ONLY: {source}" : source }
            },
            temperature = strictRetry ? 0.0 : dedicated ? 0.7 : 0.1,
            max_tokens = maxTokens,
            top_p = dedicated ? 0.6 : 1.0,
            top_k = dedicated ? 20 : 40,
            repeat_penalty = dedicated ? 1.05 : 1.0,
            chat_template_kwargs = new { enable_thinking = false }
        };
    }

    internal static bool NeedsTargetRetry(string translated, string target)
    {
        if (string.IsNullOrWhiteSpace(translated)) return true;
        int outputCjk = translated.Count(IsCjk);
        int outputLetters = translated.Count(char.IsLetter);
        if (target is "English" or "Spanish") return outputCjk > 0;
        if (target == "Chinese") return !translated.Any(value => value is >= '\u3400' and <= '\u9FFF') && outputLetters > 3;
        if (target == "Korean") return !translated.Any(value => value is >= '\uAC00' and <= '\uD7AF') && outputLetters > 3;
        if (target == "Japanese") return outputCjk == 0 && outputLetters > 3;
        return false;
    }

    private static bool IsCjk(char value) =>
        value is >= '\u3400' and <= '\u9FFF' or >= '\u3040' and <= '\u30FF' or >= '\uAC00' and <= '\uD7AF';

    private static string SpeakerIdentity(SpeakerAttribution speaker) =>
        speaker.SessionVoiceKey.StartsWith("vibevoice:", StringComparison.Ordinal) && !GroupSpeechProtocol.IsUnidentifiedKey(speaker.SessionVoiceKey)
            ? speaker.SessionVoiceKey // The speech model owns its separate text tracks, not the fingerprint matcher.
            : speaker.IsMixed ? "" : speaker.PersonId is not null ? speaker.PersonConfirmed ? "person:" + speaker.PersonId : ""
            : speaker.Confidence > 0 && speaker.Label.StartsWith("Speaker ", StringComparison.Ordinal) ? "voice:" + speaker.Label : "";

    private string GetContext(SpeakerAttribution speaker, long currentId, int count)
    {
        if (count <= 0) return "";
        string identity = SpeakerIdentity(speaker);
        if (identity.Length == 0) return "";
        lock (_feedbackGate)
            return string.Join(" / ", _speakerContext.Where(pair => pair.Key != currentId && pair.Value.Identity == identity)
                .OrderBy(pair => pair.Value.At).TakeLast(Math.Min(count, 6)).Select(pair => pair.Value.Source));
    }

    private void RememberContext(TranslationRequest request)
    {
        lock (_feedbackGate)
        {
            SpeakerAttribution speaker = ResolveSpeakerLocked(request.UtteranceId, request.Speaker, request.SpeakerRecognitionEpoch);
            _speakerContext[request.UtteranceId] = new(SpeakerIdentity(speaker), request.Source, request.At, speaker.IsMixed);
            while (_speakerContext.Count > 200) _speakerContext.Remove(_speakerContext.MinBy(pair => pair.Value.At).Key);
        }
    }

    private sealed record SpeakerContextEntry(string Identity, string Source, DateTimeOffset At, bool IsMixed);

    internal static int RunSpeakerContextChecks()
    {
        // Inert service only: no RunAsync, extractor, provider, audio device or network call.
        using var service = new IncomingTranslationService(new AppSettings(), (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        int checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Speaker correction context self-check failed."); checks++; }
        DateTimeOffset at = DateTimeOffset.UtcNow;
        SpeakerAttribution a = new("Old name", 1, false, 0) { PersonId = "test-person-a", EvidenceId = "test-evidence-a", PersonConfirmed = true };
        SpeakerAttribution b = a with { PersonId = "test-person-b", Label = "Other person", EvidenceId = "test-evidence-b" };
        service.RememberContext(new("First partial", a, 1, at));
        service.RememberContext(new("Revised full sentence", a, 1, at.AddSeconds(1)));
        Check(service.GetContext(a, 2, 4) == "Revised full sentence");
        Check(service.GetContext(a, 1, 4) == "");
        Check(service.GetContext(b, 2, 4) == "");
        Check(service.GetContext(a with { Label = "Renamed" }, 2, 4) == "Revised full sentence");
        service._evidenceBubbles[a.EvidenceId] = 1;
        service.ApplyContextCorrection(a.EvidenceId, new(false, "Synthetic reassignment", b.PersonId, b.Label, true));
        Check(service.GetContext(a, 2, 4) == "" && service.GetContext(b, 2, 4) == "Revised full sentence");
        service.ApplyContextCorrection(a.EvidenceId, new(false, "Rejected", null, "Unidentified speaker", false, false));
        Check(service.GetContext(b, 2, 4) == "Revised full sentence");
        Check(service.ResolveSpeaker(1, a).PersonId == b.PersonId);
        service.ApplyContextCorrection(a.EvidenceId, new(false, "Synthetic music", null, "Music / recording", true));
        Check(service.GetContext(b, 2, 4) == "" && service.ResolveSpeaker(1, a).PersonId is null);
        service.RememberContext(new("Mixed text", a with { IsMixed = true }, 3, at.AddSeconds(2)));
        service._evidenceBubbles["test-mixed"] = 3;
        service.ApplyContextCorrection("test-mixed", new(false, "Label only", a.PersonId, a.Label, true));
        Check(service.GetContext(a, 4, 4) == "" && service.GetContext(a with { IsMixed = true }, 4, 4) == "");
        service._deletedPeople.Add(a.PersonId!);
        Check(service.ResolveSpeaker(99, a).PersonId is null);
        SpeakerAttribution groupA = new("Voice 1", 0, true, 0) { SessionVoiceKey = "vibevoice:context-session:0:0" };
        SpeakerAttribution groupB = groupA with { SessionVoiceKey = "vibevoice:context-session:0:1" };
        service.RememberContext(new("Separate track A", groupA, 10, at));
        Check(service.GetContext(groupA, 11, 2) == "Separate track A" && service.GetContext(groupB, 11, 2) == "");
        Check(service.GetContext(groupA with { SessionVoiceKey = "vibevoice:other-session:0:0" }, 11, 2) == "");
        Check(SpeakerIdentity(groupA with { SessionVoiceKey = "vibevoice:context-session:0:unknown" }) == "");
        return checks;
    }

    internal static async Task<int> RunSpeakerRecognitionOfflineChecksAsync(string isolatedRoot) =>
        await Task.Run(async () =>
        {
            // Only isolated metadata and synthetic attributions are used. No RunAsync, capture,
            // server, HTTP request, ONNX extractor, downloaded model, or GPU API is invoked.
            int checks = 0;
            void Check(bool value, string message)
            {
                if (!value) throw new InvalidOperationException("Voice-recognition control check failed: " + message);
                checks++;
            }
            string root = Path.Combine(Path.GetFullPath(isolatedRoot), "recognition-" + Guid.NewGuid().ToString("N"));
            var identifier = new AiSpeakerIdentifier(Path.Combine(root, "anonymous.json"), Path.Combine(root, "named.json"));
            AppSettings original = new() { UseAiSpeakerRecognition = true, SpeakerModelPath = Path.Combine(root, "not-installed.onnx") };
            using var service = new IncomingTranslationService(original, (_, _) => Task.CompletedTask,
                _ => Task.CompletedTask, _ => Task.CompletedTask, identifier);
            Check(service.RecognitionStatus is { Enabled: true, ModelLoaded: false, Message: "Enabled · waiting for speech" },
                "initial enabled status is lazy and does not load an extractor");
            SpeakerPerson person = await service.SaveSpeakerPersonAsync("Synthetic saved person").ConfigureAwait(false);
            DateTimeOffset at = DateTimeOffset.UtcNow;
            SpeakerAttribution automatic = new("Speaker 3", 0.9, false, 0.8)
                { PersonId = "old-automatic-person", EvidenceId = "synthetic-automatic-evidence" };
            TranslationRequest oldRequest = service.CreateTranslationRequest("Synthetic source", automatic, at, at, 10);
            service.RememberContext(oldRequest);
            Check(service.GetContext(automatic, 11, 2) == "", "unreviewed named guesses never share person context");
            Check(service._translations.Writer.TryWrite(oldRequest), "the translation queue is initially open");

            SpeakerRecognitionStatus stopped = await service.ApplySpeakerRecognitionAsync(false).ConfigureAwait(false);
            Check(stopped is { Enabled: false, ModelLoaded: false, Message: "Off" } && !identifier.RecognitionEnabled && !identifier.IsModelLoaded,
                "Stop acknowledges an unloaded disabled voice-ID backend");
            Check(service._capture is null && service._socket is null && service._translations.Writer.TryWrite(oldRequest),
                "Stop neither starts/stops capture nor completes the translation queue");
            Check(service.GetContext(automatic, 11, 2) == "", "Stop drops transient automatic identity context");
            SpeakerAttribution oldAfterStop = service.ResolveSpeaker(10, automatic, oldRequest.SpeakerRecognitionEpoch);
            Check(oldAfterStop is { Label: "Voice", PersonId: null, Confidence: 0, Pan: 0, CanLearn: false },
                "queued automatic attribution becomes plain Voice without spatial identity");
            SpeakerAttribution observed = await service.IdentifySpeakerAsync(at.AddSeconds(-1), at, CancellationToken.None).ConfigureAwait(false);
            Check(observed is { Label: "Voice", PersonId: null, Confidence: 0, Pan: 0, CanLearn: false } && observed.EvidenceId.Length > 0,
                "Off observations retain correction metadata but infer no automatic identity");
            TranslationRequest labeled = service.CreateTranslationRequest("Synthetic label-only source", observed, at, at, 12);
            SpeakerFeedbackResult correction = await service.CorrectSpeakerAsync(observed.EvidenceId, person.Id).ConfigureAwait(false);
            Check(correction.Applied && !correction.Learned && correction.PersonId == person.Id,
                "manual correction while Off changes only the label, not a learned voice example");
            Check(service.ResolveSpeaker(labeled.UtteranceId, observed, labeled.SpeakerRecognitionEpoch).PersonId == person.Id,
                "manual labels survive independently of automatic recognition");
            Check((await service.GetSpeakerFeedbackStateAsync().ConfigureAwait(false)).People.Single().Id == person.Id,
                "Stop retains saved named people");
            AppSettings generic = original.Copy(); generic.IncomingTargetLanguage = "Korean";
            service.UpdateSettings(generic);
            Check(!service.CurrentSettings().UseAiSpeakerRecognition && service.CurrentSettings().IncomingTargetLanguage == "Korean",
                "generic options update other settings without silently enabling voice ID");

            SpeakerRecognitionStatus started = await service.ApplySpeakerRecognitionAsync(true).ConfigureAwait(false);
            Check(started is { Enabled: true, ModelLoaded: false, Message: "Enabled · waiting for speech" } && identifier.LoadedModelPath.Length == 0,
                "Start remains lazy even when no model asset exists");
            Check(service.ResolveSpeaker(10, automatic, oldRequest.SpeakerRecognitionEpoch).Label == "Voice",
                "Off/On cannot revive an old in-flight automatic speaker attribution");
            Check(service.CreateTranslationRequest("Synthetic delayed attribution", automatic, at, at, 13,
                oldRequest.SpeakerRecognitionEpoch).Speaker.Label == "Voice",
                "an old identification result cannot acquire a new epoch while entering the translation queue");
            Check(!service.IsRecognitionAttributionCurrent(oldRequest.SpeakerRecognitionEpoch) &&
                service.IsRecognitionAttributionCurrent(Interlocked.Read(ref service._speakerRecognitionEpoch)),
                "the dispatcher can distinguish stale automatic display callbacks from the current epoch");
            Check(service.ResolveSpeaker(labeled.UtteranceId, observed, labeled.SpeakerRecognitionEpoch).PersonId == person.Id,
                "Off/On preserves explicit human labels");
            Check((await service.GetSpeakerFeedbackStateAsync().ConfigureAwait(false)).People.Single().Id == person.Id && original.UseAiSpeakerRecognition,
                "controls retain named data and do not mutate the caller's settings object");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool canceledRejected = false;
            try { await service.ApplySpeakerRecognitionAsync(false, canceled.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { canceledRejected = true; }
            Check(canceledRejected && service.RecognitionStatus.Enabled, "canceled control requests cannot partially disable the service");
            Check(!File.Exists(original.SpeakerModelPath) && !File.Exists(Path.Combine(root, "anonymous.json")) &&
                service._capture is null && service._socket is null && identifier.ExtractorLoadAttempts == 0,
                "all checks used metadata only, without a model or audio device");
            service.Dispose();
            bool disposedRejected = false;
            try { await service.ApplySpeakerRecognitionAsync(false).ConfigureAwait(false); }
            catch (ObjectDisposedException) { disposedRejected = true; }
            Check(disposedRejected, "a disposed subtitle service rejects new recognition operations");
            await identifier.DisposalCompleted.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var initialOffIdentifier = new AiSpeakerIdentifier(Path.Combine(root, "initial-off-anonymous.json"), Path.Combine(root, "initial-off-named.json"));
            using var initialOffService = new IncomingTranslationService(new AppSettings { UseAiSpeakerRecognition = false },
                (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, initialOffIdentifier);
            Check(initialOffService.RecognitionStatus is { Enabled: false, ModelLoaded: false, Message: "Off" },
                "saved Off never reports a temporarily enabled recognizer even before Run initialization");
            await initialOffService.ApplySpeakerRecognitionAsync(false).ConfigureAwait(false);
            Check(!initialOffIdentifier.RecognitionEnabled && initialOffIdentifier.ExtractorLoadAttempts == 0 &&
                initialOffService._capture is null && initialOffService._socket is null,
                "saved-Off initialization unloads/adopts admission before any capture or model work");
            return checks;
        }).ConfigureAwait(false);

    private void ResetTranscriptState()
    {
        lock (_gate)
        {
            _latest = "";
            _currentUtteranceId = 0;
            _subtitleSegments.Reset();
            _subtitleBubbleIds.Clear();
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The incoming Voxtral WebSocket closed unexpectedly.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("Voxtral sent an unexpected binary response.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch { }
    }

    internal static async Task<int> RunRecoveryHealthChecksAsync()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }
        var statuses = new List<(string Message, bool Warning)>();
        using var service = new IncomingTranslationService(new AppSettings { UseAiSpeakerRecognition = false },
            (message, warning) => { statuses.Add((message, warning)); return Task.CompletedTask; },
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        await service.ReportHealthAsync("stalled", "recognition stalled", true, "test failure");
        await service.ReportHealthAsync("translating", "old translation finishing", onlyWhenHealthy: true);
        Check(service.HealthSnapshot.Stage == "stalled" && statuses.Count == 1,
            "An old translation cannot erase a recognition-stall warning");
        await service.ReportHealthAsync("recovering", "reconnecting", true);
        await service.ReportHealthAsync("waiting", "old translation finished", onlyWhenHealthy: true);
        Check(service.HealthSnapshot.Stage == "recovering" && service.HealthSnapshot.LatestError == "test failure",
            "Waiting and backlog activity retain the recovery warning and its latest diagnostic");
        service._awaitingRecognitionProgress = true;
        service._consecutiveStallFallbacks = 9;
        await service.ObserveTranscriptAsync("Hi");
        Check(service.HealthSnapshot.Stage == "listening" && service.HealthSnapshot.LastWordAt is not null &&
            service._consecutiveStallFallbacks == 0, "Short actual words restore health and reset bounded retry cooldown");
        await service.ReportHealthAsync("stalled", "recognition stalled", true);
        service._awaitingRecognitionProgress = true;
        await service.ObserveTranscriptAsync("Hi!");
        Check(service.HealthSnapshot.Stage == "stalled", "Punctuation-only revisions cannot claim recognition recovered");
        await service.ObserveTranscriptAsync("Hi there");
        Check(service.HealthSnapshot.Stage == "listening", "A newly recognized word clears recovery status");
        await service.ReportHealthAsync("translation-error", "translation failed", true, "test translator deadline");
        await service.ReportHealthAsync("translating", "next translation", onlyWhenHealthy: true);
        Check(service.HealthSnapshot.Stage == "translating" && service.HealthSnapshot.LatestError == "test translator deadline",
            "A recovered translation error does not permanently lock the pipeline health state");
        service._connected = true;
        service._awaitingRecognitionProgress = true;
        service._consecutiveStallFallbacks = 2;
        service._blankRecoveryAttempts = 2;
        long lastWord = Interlocked.Read(ref service._lastWordTicks);
        long lastMeaningful = Interlocked.Read(ref service._lastMeaningfulTicks);
        await service.ReportHealthAsync("stalled", RecognitionStalledMessage, true, "no new words");
        await service.ClearQuietRecognitionWarningAsync(1, TimeSpan.Zero, TimeSpan.Zero);
        Check(service.HealthSnapshot.Stage == "stalled", "Ongoing speech cannot hide its missing-word warning");
        await service.ClearQuietRecognitionWarningAsync(5, TimeSpan.FromSeconds(4), TimeSpan.Zero);
        await service.ClearQuietRecognitionWarningAsync(5, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        Check(service.HealthSnapshot.Stage == "stalled", "Inactive decoder or blocked sending cannot look like healthy silence");
        service._connected = false;
        await service.ClearQuietRecognitionWarningAsync(5, TimeSpan.Zero, TimeSpan.Zero);
        service._connected = true;
        service._finalizingAudio = true;
        await service.ClearQuietRecognitionWarningAsync(5, TimeSpan.Zero, TimeSpan.Zero);
        Check(service.HealthSnapshot.Stage == "stalled", "Disconnected and finalizing sessions cannot clear recognition warnings");
        service._finalizingAudio = false;
        await service.ClearQuietRecognitionWarningAsync(5, TimeSpan.Zero, TimeSpan.Zero);
        Check(service.HealthSnapshot.Stage == "waiting" && !service.HealthSnapshot.IsWarning &&
            service.HealthSnapshot.LatestError == "no new words", "Healthy quiet input restores waiting but retains error details");
        Check(service._awaitingRecognitionProgress && service._consecutiveStallFallbacks == 2 &&
            service._blankRecoveryAttempts == 2 && Interlocked.Read(ref service._lastWordTicks) == lastWord &&
            Interlocked.Read(ref service._lastMeaningfulTicks) == lastMeaningful,
            "Quiet status cannot fabricate a word or reset failed recognition history");
        await service.ReportHealthAsync("translating", "old translation finishing", onlyWhenHealthy: true);
        Check(service.HealthSnapshot.Stage == "waiting", "An old translation cannot claim recognition recovered during quiet waiting");
        await service.ReportHealthAsync("reconnecting", "connection lost", true);
        await service.ClearQuietRecognitionWarningAsync(5, TimeSpan.Zero, TimeSpan.Zero);
        Check(service.HealthSnapshot.Stage == "reconnecting", "Quiet input cannot overwrite an unrelated connection error");
        await service.ObserveTranscriptAsync("Hi there again");
        Check(!service._awaitingRecognitionProgress && service.HealthSnapshot.Stage == "listening" &&
            service._consecutiveStallFallbacks == 0, "New words still restore recognition after quiet waiting");
        Check(service._lastMeaningfulAudioEnd is null,
            "A transcript without a server audio position cannot acknowledge newer audio by wall clock");
        DateTimeOffset acknowledged = DateTimeOffset.UtcNow.AddSeconds(-3);
        await service.ObserveTranscriptAsync("Hi there again now", acknowledged);
        await service.ObserveTranscriptAsync("Hi there again now please", acknowledged.AddSeconds(-1));
        Check(service._lastMeaningfulAudioEnd == acknowledged, "A delayed transcript cannot move acknowledged audio backwards");
        await service.ObserveTranscriptAsync("Hi there again now please listen");
        Check(service._lastMeaningfulAudioEnd == acknowledged, "Missing server positions preserve the last real acknowledgment");
        await service.ObserveTranscriptAsync("Hi there again now please listen carefully", acknowledged.AddSeconds(1));
        Check(service._lastMeaningfulAudioEnd == acknowledged.AddSeconds(1), "New mapped audio advances the replay boundary");
        return checks;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _captureDevice?.Dispose();
        _socket?.Dispose();
        _aiSpeakerIdentifier.Dispose();
        _http.Dispose();
    }
}

internal enum PcmVoiceActivity { Silence, Continuation, Speech }

internal readonly record struct IncomingPcmChunk(byte[] Bytes, DateTimeOffset At);

internal sealed class IncomingAudioUtterance
{
    internal readonly Queue<IncomingPcmChunk> Chunks = new();
    internal bool Taken;
    internal bool Complete;
    internal string EndReason = "speech pause";
    internal DateTimeOffset LastVoiceAt;
    internal DateTimeOffset LastPitchedAt;
    public DateTimeOffset FirstQueuedAudioAt { get; }
    public long DroppedChunks { get; internal set; }
    internal IncomingAudioUtterance(DateTimeOffset firstAudioAt) => FirstQueuedAudioAt = firstAudioAt;
}

/// <summary>Only RAM: 320 ms idle pre-roll and at most two seconds of queued PCM across all turns.</summary>
internal sealed class IncomingUtteranceAudioBuffer
{
    private readonly object _gate = new();
    private readonly Queue<IncomingPcmChunk> _preRoll = new();
    private readonly LinkedList<IncomingAudioUtterance> _utterances = new();
    private readonly int _capacityChunks;
    private readonly int _preRollChunks;
    private readonly TimeSpan _maximumTurnDuration;
    private IncomingAudioUtterance? _capturing;
    private int _queuedChunks;
    private long _droppedChunks;
    private DateTimeOffset _lastVoiceAt = DateTimeOffset.MinValue;
    public IncomingUtteranceAudioBuffer(int capacityChunks = 25, int preRollChunks = 4, int maximumTurnMilliseconds = 30000)
    {
        if (capacityChunks < 1 || preRollChunks < 0 || preRollChunks > capacityChunks)
            throw new ArgumentOutOfRangeException(nameof(capacityChunks));
        _capacityChunks = capacityChunks;
        _preRollChunks = preRollChunks;
        if (maximumTurnMilliseconds < 2000) throw new ArgumentOutOfRangeException(nameof(maximumTurnMilliseconds));
        _maximumTurnDuration = TimeSpan.FromMilliseconds(maximumTurnMilliseconds);
    }
    public int BufferedBytes { get { lock (_gate) return _queuedChunks * 2560; } }
    public bool HasPendingAudio { get { lock (_gate) return _utterances.Any(item => !item.Taken && item.Chunks.Count > 0); } }
    public long DroppedChunks { get { lock (_gate) return _droppedChunks; } }
    public DateTimeOffset LastVoiceAt { get { lock (_gate) return _lastVoiceAt; } }

    public void Observe(byte[] pcm, DateTimeOffset at, PcmVoiceActivity activity, int quietMilliseconds)
    {
        var chunk = new IncomingPcmChunk((byte[])pcm.Clone(), at);
        DateTimeOffset end = at.AddSeconds(pcm.Length / 32000.0);
        lock (_gate)
        {
            if (_capturing is null)
            {
                if (activity != PcmVoiceActivity.Speech)
                {
                    KeepPreRoll(chunk);
                    return;
                }
                _capturing = new IncomingAudioUtterance(_preRoll.Count > 0 ? _preRoll.Peek().At : at);
                _utterances.AddLast(_capturing);
                while (_preRoll.Count > 0) Enqueue(_capturing, _preRoll.Dequeue());
            }
            Enqueue(_capturing, chunk);
            if (activity == PcmVoiceActivity.Speech) _capturing.LastPitchedAt = end;
            // Unvoiced consonants can continue recent speech, but steady unpitched
            // ambience must not hold a finished turn open indefinitely.
            bool voiceContinues = activity == PcmVoiceActivity.Speech ||
                activity == PcmVoiceActivity.Continuation &&
                end - _capturing.LastPitchedAt <= TimeSpan.FromMilliseconds(640);
            if (voiceContinues)
            {
                _capturing.LastVoiceAt = end;
                _lastVoiceAt = end;
            }
            bool safetyLimit = end - _capturing.FirstQueuedAudioAt >= _maximumTurnDuration;
            if (safetyLimit || !voiceContinues && end - _capturing.LastVoiceAt >= TimeSpan.FromMilliseconds(quietMilliseconds))
            {
                _capturing.EndReason = safetyLimit ? "continuous-audio safety limit" : "speech pause";
                _capturing.Complete = true;
                _capturing = null;
                KeepPreRoll(chunk);
            }
            // Drop the oldest PCM globally, never allow a finalizing turn to grow another
            // unbounded queue, and retain new voice while the receiver drains its final result.
            while (_queuedChunks > _capacityChunks)
            {
                IncomingAudioUtterance? oldest = _utterances.FirstOrDefault(item => item.Chunks.Count > 0);
                if (oldest is null) break;
                oldest.Chunks.Dequeue();
                oldest.DroppedChunks++;
                _droppedChunks++;
                _queuedChunks--;
            }
            LinkedListNode<IncomingAudioUtterance>? node = _utterances.First;
            while (node is not null)
            {
                LinkedListNode<IncomingAudioUtterance>? next = node.Next;
                if (!node.Value.Taken && node.Value.Complete && node.Value.Chunks.Count == 0)
                    _utterances.Remove(node);
                node = next;
            }
        }
    }

    public bool TryTake([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IncomingAudioUtterance? utterance)
    {
        lock (_gate)
        {
            utterance = _utterances.FirstOrDefault(item => !item.Taken);
            if (utterance is null) return false;
            utterance.Taken = true;
            return true;
        }
    }

    public bool TryRead(IncomingAudioUtterance utterance, out IncomingPcmChunk chunk, out bool complete)
    {
        lock (_gate)
        {
            if (utterance.Chunks.Count > 0)
            {
                chunk = utterance.Chunks.Dequeue();
                _queuedChunks--;
                complete = false;
                return true;
            }
            chunk = default;
            complete = utterance.Complete;
            return false;
        }
    }

    public void Release(IncomingAudioUtterance utterance, bool preserveUnsent = false)
    {
        lock (_gate)
        {
            if (preserveUnsent && utterance.Chunks.Count > 0 && _utterances.Find(utterance) is { } node)
            {
                // A failed stream has not consumed this audio. Re-home the freshest
                // bounded suffix as a new turn instead of deleting it with the old socket.
                var resumed = new IncomingAudioUtterance(utterance.Chunks.Peek().At)
                {
                    LastVoiceAt = utterance.LastVoiceAt, LastPitchedAt = utterance.LastPitchedAt,
                    Complete = utterance.Complete, EndReason = utterance.EndReason,
                    DroppedChunks = utterance.DroppedChunks
                };
                while (utterance.Chunks.Count > 0) resumed.Chunks.Enqueue(utterance.Chunks.Dequeue());
                _utterances.AddBefore(node, resumed);
                if (ReferenceEquals(_capturing, utterance)) _capturing = resumed;
            }
            if (ReferenceEquals(_capturing, utterance)) _capturing = null;
            _queuedChunks -= utterance.Chunks.Count;
            utterance.Chunks.Clear();
            utterance.Complete = true;
            _utterances.Remove(utterance);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            foreach (IncomingAudioUtterance utterance in _utterances)
            {
                utterance.Chunks.Clear();
                utterance.Complete = true;
            }
            _utterances.Clear();
            _preRoll.Clear();
            _capturing = null;
            _queuedChunks = 0;
            _lastVoiceAt = DateTimeOffset.MinValue;
        }
    }

    private void Enqueue(IncomingAudioUtterance utterance, IncomingPcmChunk chunk)
    {
        utterance.Chunks.Enqueue(chunk);
        _queuedChunks++;
    }
    private void KeepPreRoll(IncomingPcmChunk chunk)
    {
        _preRoll.Enqueue(chunk);
        while (_preRoll.Count > _preRollChunks) _preRoll.Dequeue();
    }
}

internal static class IncomingPcmVoiceDetector
{
    public static PcmVoiceActivity Classify(ReadOnlySpan<byte> pcm, double rawRms = double.NaN)
    {
        // Recognition gain helps distant speech but must not turn quiet background
        // hiss/ambience into endless "speech". Make the silence decision before gain.
        if (double.IsFinite(rawRms) && rawRms < 0.00060) return PcmVoiceActivity.Silence;
        int samples = pcm.Length / 2;
        if (samples < 160) return PcmVoiceActivity.Silence;
        double energy = 0, differenceEnergy = 0, previous = 0;
        int crossings = 0;
        for (int index = 0; index < samples; index++)
        {
            double value = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(index * 2, 2)) / 32768.0;
            energy += value * value;
            if (index > 0)
            {
                differenceEnergy += (value - previous) * (value - previous);
                if ((value >= 0) != (previous >= 0)) crossings++;
            }
            previous = value;
        }
        double rms = Math.Sqrt(energy / samples);
        double zcr = crossings / (double)samples;
        double brightness = Math.Sqrt(differenceEnergy / Math.Max(1e-12, energy));
        if (rms < 0.0012 || zcr > 0.38 || brightness >= 1.9) return PcmVoiceActivity.Silence;

        // A cheap, downsampled pitch check starts a turn conservatively. Once started,
        // audible unvoiced consonants can sustain it without needing another pitched vowel.
        int count = Math.Min(320, samples / 4);
        Span<double> downsampled = stackalloc double[count];
        for (int index = 0; index < count; index++)
            downsampled[index] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(index * 8, 2)) / 32768.0;
        double best = 0;
        for (int lag = 10; lag <= 57 && lag < count / 2; lag++)
        {
            double cross = 0, firstEnergy = 0, secondEnergy = 0;
            for (int index = lag; index < count; index++)
            {
                double first = downsampled[index], second = downsampled[index - lag];
                cross += first * second;
                firstEnergy += first * first;
                secondEnergy += second * second;
            }
            best = Math.Max(best, cross / Math.Sqrt(Math.Max(1e-12, firstEnergy * secondEnergy)));
        }
        return zcr >= 0.003 && brightness < 1.7 && best >= 0.20
            ? PcmVoiceActivity.Speech : PcmVoiceActivity.Continuation;
    }
}

/// <summary>Maps compressed server offsets back to real PCM times, including queue-drop gaps.</summary>
internal sealed class IncomingAudioTimeline
{
    private readonly object _gate = new();
    private readonly Queue<(double Offset, double Duration, DateTimeOffset At)> _chunks = new();
    private double _nextOffset;
    public void Reset() { lock (_gate) { _chunks.Clear(); _nextOffset = 0; } }
    public void Append(DateTimeOffset at, int bytes)
    {
        double duration = bytes / 32.0; // PCM16 mono/16 kHz: 32 bytes per millisecond.
        lock (_gate)
        {
            _chunks.Enqueue((_nextOffset, duration, at));
            _nextOffset += duration;
            while (_chunks.Count > 300) _chunks.Dequeue();
        }
    }
    public DateTimeOffset? Map(double offsetMilliseconds)
    {
        lock (_gate)
        {
            if (_chunks.Count == 0) return null;
            foreach (var chunk in _chunks)
                if (offsetMilliseconds <= chunk.Offset + chunk.Duration)
                    return chunk.At.AddMilliseconds(Math.Clamp(offsetMilliseconds - chunk.Offset, 0, chunk.Duration));
            var last = _chunks.Last();
            return last.At.AddMilliseconds(last.Duration);
        }
    }
    public bool TryMapExactRange(double startMilliseconds, double endMilliseconds, out DateTimeOffset start, out DateTimeOffset end)
    {
        lock (_gate)
        {
            start = end = default;
            if (_chunks.Count == 0 || !double.IsFinite(startMilliseconds) || !double.IsFinite(endMilliseconds)
                || endMilliseconds <= startMilliseconds || startMilliseconds < _chunks.Peek().Offset) return false;
            var last = _chunks.Last();
            if (endMilliseconds > last.Offset + last.Duration + 1) return false;
            start = Map(startMilliseconds)!.Value; end = Map(endMilliseconds)!.Value;
            return Math.Abs((end - start).TotalMilliseconds - (endMilliseconds - startMilliseconds)) < 100;
        }
    }
}

internal sealed class SessionRefreshException : Exception
{
    public bool ReplayUnrecognizedAudio { get; }
    public SessionRefreshException(string message, bool replayUnrecognizedAudio = false) : base(message)
        => ReplayUnrecognizedAudio = replayUnrecognizedAudio;
}

internal sealed class SpatialVoiceDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _scratch = Array.Empty<float>();
    public SpatialVoiceDownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }
    public WaveFormat WaveFormat { get; }
    public int Read(float[] buffer, int offset, int count)
    {
        int channels = _source.WaveFormat.Channels;
        int required = count * channels;
        if (_scratch.Length < required) _scratch = new float[required];
        int frames = _source.Read(_scratch, 0, required) / channels;
        if (channels == 1)
        {
            Array.Copy(_scratch, 0, buffer, offset, frames);
            return frames;
        }

        double leftEnergy = 0;
        double rightEnergy = 0;
        double averageEnergy = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            float left = _scratch[frame * channels];
            float right = _scratch[frame * channels + 1];
            leftEnergy += left * left;
            rightEnergy += right * right;
            float average = (left + right) * 0.5f;
            averageEnergy += average * average;
        }
        double strongestChannel = Math.Max(leftEnergy, rightEnergy);
        bool phaseCancellation = averageEnergy < strongestChannel * 0.30;
        bool leftDominant = leftEnergy >= rightEnergy;
        for (int frame = 0; frame < frames; frame++)
        {
            float left = _scratch[frame * channels];
            float right = _scratch[frame * channels + 1];
            if (phaseCancellation)
            {
                buffer[offset + frame] = leftDominant ? left : right;
            }
            else
            {
                double total = leftEnergy + rightEnergy + 1e-12;
                float leftWeight = (float)Math.Clamp(leftEnergy / total, 0.25, 0.75);
                buffer[offset + frame] = left * leftWeight + right * (1 - leftWeight);
            }
        }
        return frames;
    }
}

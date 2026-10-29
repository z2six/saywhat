using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace IncomingTranslator;

/// <summary>
/// An in-window, read-only tour. The overlay consumes input outside its own
/// controls, and resolving a target never activates it. It owns no settings,
/// services, model loading or navigation. Its host records completion.
/// </summary>
internal sealed class FirstRunTourOverlay : Grid, IDisposable
{
    internal sealed record TourStep(string Target, string Heading, string Description);
    internal static readonly IReadOnlyList<TourStep> Steps = Array.AsReadOnly(new[]
    {
        new TourStep("Help", "Find help",
            "Open Help for setup instructions and fixes when something is not working."),
        new TourStep("OwnVoice", "Translate your voice",
            "Start here to send your translated microphone to VRChat chat. Your spoken language, translation languages and model choices are also on Home."),
        new TourStep("Subtitles", "Read other voices",
            "Optional subtitles translate other people's voices in a separate window. They start independently and are not needed for your own voice translation."),
        new TourStep("Settings", "Change your settings",
            "Open Settings for timing, profiles, appearance or closing behavior. Your everyday language and model choices stay on Home.")
    });

    private readonly Func<string, FrameworkElement?> _resolveTarget;
    private readonly Action _completed;
    private readonly Canvas _canvas = new() { IsHitTestVisible = true };
    private readonly Path _scrim = new() { Name = "TourScrim", IsHitTestVisible = false, Fill = ModernTheme.Background, Opacity = .82 };
    private readonly Border _highlight = new()
    {
        Name = "TourSpotlight", BorderBrush = ModernTheme.Accent, BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, IsHitTestVisible = false
    };
    private readonly Border _card;
    private readonly TextBlock _counter = ModernTheme.Label("", 11, ModernTheme.Muted);
    private readonly TextBlock _heading = ModernTheme.Heading("", 25);
    private readonly TextBlock _description = ModernTheme.Label("", 14, ModernTheme.Muted);
    private readonly Button _skip;
    private readonly Button _back;
    private readonly Button _next;
    private readonly Ellipse[] _dots;
    private bool _disposed;
    private bool _finished;
    private bool _refreshingPlacement;
    private Rect _placedViewport = Rect.Empty;
    private Rect _placedTarget = Rect.Empty;
    private Size _placedCardSize;
    private int _placedStepIndex = -1;
    private int _stepIndex;

    internal int StepIndex => _stepIndex;
    internal string TargetKey => Steps[_stepIndex].Target;
    internal bool HasTargetHighlight => _highlight.Visibility == Visibility.Visible;
    internal Rect HighlightBounds { get; private set; } = Rect.Empty;
    internal Rect CardBounds { get; private set; } = Rect.Empty;
    internal bool IsFinished => _finished;

    internal FirstRunTourOverlay(Func<string, FrameworkElement?> resolveTarget, Action completed)
    {
        ArgumentNullException.ThrowIfNull(resolveTarget);
        ArgumentNullException.ThrowIfNull(completed);
        _resolveTarget = resolveTarget; _completed = completed;
        Name = "FirstRunTourOverlay";
        Background = Brushes.Transparent; // The spotlight must not pass clicks through to Start.
        Focusable = true; ClipToBounds = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
        FocusManager.SetIsFocusScope(this, true);
        Panel.SetZIndex(this, 1000);
        AutomationProperties.SetName(this, UiText.T("Quick tour"));
        UiLocalization.Bind(this, AutomationProperties.NameProperty, "Quick tour");

        var body = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _counter.Name = "TourStepCounter"; _counter.Margin = new Thickness(0);
        _counter.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_counter);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _dots = Enumerable.Range(0, Steps.Count).Select(_ => new Ellipse
        {
            Width = 6, Height = 6, Margin = new Thickness(3, 0, 0, 0), Fill = ModernTheme.Stroke,
            IsHitTestVisible = false
        }).ToArray();
        foreach (Ellipse dot in _dots) dots.Children.Add(dot);
        Grid.SetColumn(dots, 1); top.Children.Add(dots); body.Children.Add(top);
        _heading.Name = "TourHeading"; _heading.Margin = new Thickness(0, 16, 0, 10);
        AutomationProperties.SetHeadingLevel(_heading, AutomationHeadingLevel.Level2);
        AutomationProperties.SetLiveSetting(_heading, AutomationLiveSetting.Polite);
        body.Children.Add(_heading);
        _description.Name = "TourDescription"; _description.Margin = new Thickness(0, 0, 0, 20);
        body.Children.Add(_description);

        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _skip = ModernTheme.Button("Skip tour", (_, _) => Skip());
        _skip.Name = "TourSkipButton"; _skip.MinHeight = 36; _skip.Padding = new Thickness(10, 7, 10, 7);
        _skip.Margin = new Thickness(0, 0, 12, 0); _skip.Background = Brushes.Transparent;
        _skip.BorderThickness = new Thickness(0); _skip.Foreground = ModernTheme.Muted;
        _back = ModernTheme.Button("Back", (_, _) => GoBack());
        _back.Name = "TourBackButton"; _back.MinHeight = 36; _back.Padding = new Thickness(12, 7, 12, 7);
        _back.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(_back, 2);
        _next = ModernTheme.Button("Next", (_, _) => Advance(), primary: true);
        _next.Name = "TourNextButton"; _next.MinHeight = 36; _next.MinWidth = 66;
        _next.Padding = new Thickness(12, 7, 12, 7); _next.Margin = new Thickness(0); Grid.SetColumn(_next, 3);
        actions.Children.Add(_skip); actions.Children.Add(_back); actions.Children.Add(_next);
        var cardContents = new Grid();
        cardContents.RowDefinitions.Add(new RowDefinition());
        cardContents.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardContents.Children.Add(new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Style = Application.Current?.TryFindResource(typeof(ScrollViewer)) as Style
        });
        Grid.SetRow(actions, 1); cardContents.Children.Add(actions);
        _card = new Border
        {
            Name = "TourCoachCard", Background = ModernTheme.Surface, BorderBrush = ModernTheme.Accent,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(22),
            Child = cardContents, UseLayoutRounding = true, SnapsToDevicePixels = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 26, ShadowDepth = 8, Opacity = .20 }
        };
        _canvas.Children.Add(_highlight); _canvas.Children.Add(_card);
        Children.Add(_scrim); Children.Add(_canvas);
        SizeChanged += OnSizeChanged;
        LayoutUpdated += OnLayoutUpdated;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
        RefreshStep(animate: false);
    }

    internal void Advance()
    {
        if (_disposed || _finished) return;
        if (_stepIndex == Steps.Count - 1) { Finish(); return; }
        _stepIndex++; RefreshStep(animate: IsLoaded);
    }

    internal void GoBack()
    {
        if (_disposed || _finished || _stepIndex == 0) return;
        _stepIndex--; RefreshStep(animate: IsLoaded);
    }

    internal void Skip()
    {
        if (_disposed || _finished) return;
        Finish();
    }

    private void Finish()
    {
        _finished = true;
        // The callback may remove this visual immediately. Never touch the host afterwards.
        Dispose();
        _completed();
    }

    private void RefreshStep(bool animate)
    {
        TourStep step = Steps[_stepIndex];
        UiLocalization.SetFormat(_counter, "Quick tour · {0} of {1}", _stepIndex + 1, Steps.Count);
        UiLocalization.SetText(_heading, step.Heading);
        UiLocalization.SetText(_description, step.Description);
        _back.IsEnabled = _stepIndex > 0;
        string next = _stepIndex == Steps.Count - 1 ? "Done" : "Next";
        UiLocalization.Bind(_next, ContentControl.ContentProperty, next);
        UiLocalization.Bind(_next, AutomationProperties.NameProperty, next);
        for (int i = 0; i < _dots.Length; i++) _dots[i].Fill = i == _stepIndex ? ModernTheme.Accent : ModernTheme.Stroke;
        if (animate && SystemParameters.ClientAreaAnimation)
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(.5, 1, TimeSpan.FromMilliseconds(130)));
        RefreshPlacement();
        if (IsLoaded) _next.Focus();
    }

    /// <summary>Re-resolves targets after navigation controls or translated Home content are rebuilt.</summary>
    internal void RefreshPlacement()
    {
        if (_disposed || _refreshingPlacement) return;
        double width = ActualWidth, height = ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1) return;
        Rect viewport = new(0, 0, width, height);
        Rect target = ResolveBounds(viewport);
        // LayoutUpdated is a dispatcher-wide notification. Re-measuring the card or
        // replacing an identical geometry on every notification schedules more layout,
        // potentially starving lower-priority work. Unchanged tours are strictly inert.
        if (_placedViewport == viewport && _placedTarget == target && _placedStepIndex == _stepIndex &&
            _card.IsMeasureValid && _card.DesiredSize == _placedCardSize) return;
        _refreshingPlacement = true;
        try
        {
            HighlightBounds = target;
            _highlight.Visibility = target.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            if (!target.IsEmpty)
            {
                Canvas.SetLeft(_highlight, target.X); Canvas.SetTop(_highlight, target.Y);
                _highlight.Width = target.Width; _highlight.Height = target.Height;
            }
            if (_placedViewport != viewport || _placedTarget != target)
            {
                Geometry outside = new RectangleGeometry(viewport);
                _scrim.Data = target.IsEmpty ? outside : new CombinedGeometry(GeometryCombineMode.Exclude,
                    outside, new RectangleGeometry(target, 12, 12));
            }

            double cardWidth = Math.Max(1, Math.Min(378, width - 32));
            _card.Width = cardWidth;
            _card.MaxHeight = Math.Max(1, height - 32);
            _card.Measure(new Size(cardWidth, _card.MaxHeight));
            double cardHeight = _card.DesiredSize.Height;
            Rect card = PlaceCard(viewport, target, new Size(cardWidth, cardHeight));
            CardBounds = card;
            Canvas.SetLeft(_card, card.Left); Canvas.SetTop(_card, card.Top);
            _placedViewport = viewport; _placedTarget = target;
            _placedStepIndex = _stepIndex; _placedCardSize = _card.DesiredSize;
        }
        finally { _refreshingPlacement = false; }
    }

    private Rect ResolveBounds(Rect viewport)
    {
        FrameworkElement? element = _resolveTarget(TargetKey);
        if (element is null || element.Visibility != Visibility.Visible || element.ActualWidth < 1 || element.ActualHeight < 1)
            return Rect.Empty;
        // TranslatePoint can return an apparent origin for unpresented, unrelated
        // visual roots. Never spotlight a stale control that has been detached.
        if (!ReferenceEquals(VisualRoot(element), VisualRoot(this))) return Rect.Empty;
        try
        {
            Point origin = element.TranslatePoint(new Point(), this);
            if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y)) return Rect.Empty;
            Rect rect = new(origin.X - 7, origin.Y - 7, element.ActualWidth + 14, element.ActualHeight + 14);
            rect.Intersect(viewport);
            return rect.IsEmpty || rect.Width < 1 || rect.Height < 1 ? Rect.Empty : rect;
        }
        catch (InvalidOperationException) { return Rect.Empty; } // Detached or replaced Home content.
        catch (ArgumentException) { return Rect.Empty; }
    }

    private static DependencyObject VisualRoot(DependencyObject element)
    {
        DependencyObject current = element;
        while (VisualTreeHelper.GetParent(current) is DependencyObject parent) current = parent;
        return current;
    }

    internal static Rect PlaceCard(Rect viewport, Rect target, Size cardSize)
    {
        const double edge = 16, gap = 18;
        double width = Math.Min(cardSize.Width, Math.Max(1, viewport.Width - edge * 2));
        double height = Math.Min(cardSize.Height, Math.Max(1, viewport.Height - edge * 2));
        double left = viewport.Left + edge, top = viewport.Top + edge;
        double right = Math.Max(left, viewport.Right - edge - width);
        double bottom = Math.Max(top, viewport.Bottom - edge - height);
        Rect Clamp(double x, double y) => new(Math.Clamp(x, left, right), Math.Clamp(y, top, bottom), width, height);
        if (target.IsEmpty) return Clamp(viewport.Left + (viewport.Width - width) / 2, viewport.Top + (viewport.Height - height) / 2);
        Rect[] options =
        [
            Clamp(target.Right + gap, target.Top + (target.Height - height) / 2),
            Clamp(target.Left - width - gap, target.Top + (target.Height - height) / 2),
            Clamp(target.Left + (target.Width - width) / 2, target.Bottom + gap),
            Clamp(target.Left + (target.Width - width) / 2, target.Top - height - gap)
        ];
        foreach (Rect option in options) if (!option.IntersectsWith(target)) return option;
        // A tiny/obstructed host still gets reachable Skip/Next instead of losing the tour.
        return options.OrderBy(option => IntersectionArea(option, target)).First();
    }

    private static double IntersectionArea(Rect a, Rect b)
    {
        a.Intersect(b);
        return a.IsEmpty ? 0 : a.Width * a.Height;
    }

    internal bool HandleKey(Key key)
    {
        if (_disposed || _finished) return false;
        switch (key)
        {
            case Key.Escape: Skip(); return true;
            case Key.Right: Advance(); return true;
            case Key.Left: GoBack(); return true;
            // Enter on Back/Skip must keep the focused button's normal click behavior.
            case Key.Enter when !ReferenceEquals(Keyboard.FocusedElement, _back) && !ReferenceEquals(Keyboard.FocusedElement, _skip):
                Advance(); return true;
            default: return false;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (HandleKey(args.Key)) args.Handled = true;
    }
    private void OnLoaded(object sender, RoutedEventArgs args) { RefreshPlacement(); _next.Focus(); }
    private void OnUnloaded(object sender, RoutedEventArgs args) => Dispose();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => RefreshPlacement();
    private void OnLayoutUpdated(object? sender, EventArgs args) => RefreshPlacement();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SizeChanged -= OnSizeChanged; LayoutUpdated -= OnLayoutUpdated;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; PreviewKeyDown -= OnPreviewKeyDown;
        _card.BeginAnimation(OpacityProperty, null);
    }
}

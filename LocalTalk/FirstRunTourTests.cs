using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

/// <summary>Unshown fake dashboard targets only; no service, capture, model or real settings are opened.</summary>
internal static class FirstRunTourTests
{
    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("First-run tour: " + detail); checks++; }
        string originalLanguage = UiText.Language;
        AppSettings originalAppearance = SettingsStore.Load();
        try
        {
            Check(FirstRunTourOverlay.Steps.Select(step => step.Target).SequenceEqual(new[] { "Help", "OwnVoice", "Subtitles", "Settings" }),
                "Help is first, followed only by everyday translation controls and Settings");
            string[] guidance = FirstRunTourOverlay.Steps.SelectMany(step => new[] { step.Heading, step.Description }).ToArray();
            Check(guidance.All(UiText.Contains), "every voice-first tour instruction has a catalog entry");
            Check(FirstRunTourOverlay.Steps[1].Description.Contains("model choices are also on Home", StringComparison.Ordinal) &&
                FirstRunTourOverlay.Steps[2].Description.StartsWith("Optional subtitles", StringComparison.Ordinal),
                "tour presents everyday model controls on Home and subtitles as independent and optional");
            int underlyingClicks = 0;
            foreach (ThemePreset preset in AppearanceTheme.Presets)
            {
                var appearance = originalAppearance.Copy(); appearance.UiThemeId = preset.Id;
                AppearanceTheme.ApplyForTests(appearance);
                foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
                {
                    UiText.Language = language;
                    Check(language == "en" || guidance.All(text => UiText.T(text) != text),
                        "tour instructions are actually translated in " + language);
                    foreach (Size viewport in new[] { new Size(1160, 800), new Size(900, 650), new Size(600, 420) })
                    {
                        int completed = 0;
                        var fixture = CreateFixture(() => underlyingClicks++);
                        using var tour = new FirstRunTourOverlay(key => fixture.Targets.GetValueOrDefault(key), () => completed++);
                        fixture.Root.Children.Add(tour);
                        for (int step = 0; step < FirstRunTourOverlay.Steps.Count; step++)
                        {
                            Layout(fixture.Root, tour, viewport);
                            Check(!tour.IsLoaded && !tour.IsVisible, "fake tour is never shown or attached to a live window");
                            Check(tour.StepIndex == step && tour.TargetKey == FirstRunTourOverlay.Steps[step].Target,
                                "only the expected visible step is selected");
                            Check(Find<TextBlock>(tour, "TourHeading").Text == UiText.T(FirstRunTourOverlay.Steps[step].Heading),
                                "heading follows interface language: " + language);
                            Check(Find<TextBlock>(tour, "TourDescription").Text == UiText.T(FirstRunTourOverlay.Steps[step].Description),
                                "instruction follows interface language: " + language);
                            Check(Find<TextBlock>(tour, "TourStepCounter").Text == UiText.Format("Quick tour · {0} of {1}", step + 1, 4),
                                "step counter is formatted using interface language: " + language);
                            var skip = Find<Button>(tour, "TourSkipButton");
                            var back = Find<Button>(tour, "TourBackButton");
                            var next = Find<Button>(tour, "TourNextButton");
                            Check(skip.Visibility == Visibility.Visible && skip.IsEnabled &&
                                AutomationProperties.GetName(skip) == UiText.T("Skip tour"), "Skip stays available and localized on every step");
                            Check(back.IsEnabled == (step > 0) && next.Content?.ToString() == UiText.T(step == 3 ? "Done" : "Next"),
                                "Back and final Done accurately describe their actions");
                            Check(ReferenceEquals(Find<Border>(tour, "TourCoachCard").Background, AppearanceTheme.Surface) &&
                                ReferenceEquals(Find<Border>(tour, "TourSpotlight").BorderBrush, AppearanceTheme.Accent) &&
                                ReferenceEquals(Find<TextBlock>(tour, "TourHeading").Foreground, AppearanceTheme.Text) &&
                                ReferenceEquals(Find<TextBlock>(tour, "TourDescription").Foreground, AppearanceTheme.Muted),
                                "the card, highlight and both text roles follow the whole-app theme: " + preset.Id);
                            Check(IsInside(tour.CardBounds, new Rect(new Point(), viewport)), "coach card stays inside resized dashboard bounds");
                            foreach (Button button in new[] { skip, back, next })
                            {
                                Point origin = button.TranslatePoint(new Point(), tour);
                                Check(button.ActualWidth > 1 && button.ActualHeight >= 30 &&
                                    IsInside(new Rect(origin, new Size(button.ActualWidth, button.ActualHeight)), new Rect(new Point(), viewport)),
                                    "navigation remains reachable in compact, translated layouts: " + button.Name);
                            }
                            if (tour.HasTargetHighlight)
                            {
                                Check(IsInside(tour.HighlightBounds, new Rect(new Point(), viewport)), "spotlight does not extend beyond the viewport");
                                Point center = new(tour.HighlightBounds.X + tour.HighlightBounds.Width / 2,
                                    tour.HighlightBounds.Y + tour.HighlightBounds.Height / 2);
                                // InputHitTest deliberately ignores an unshown UIElement's
                                // IsVisible=false. Visual hit testing plus the same declared
                                // visibility/hit-test flags can inspect this inert tree without
                                // ever showing a window or attaching a presentation source.
                                DependencyObject? hit = VisualInputHit(fixture.Root, center);
                                Check(hit is not null && IsDescendantOf(hit, tour),
                                    "spotlight never lets a click pass through to Start or navigation: " +
                                    $"viewport={viewport}, theme={preset.Id}, language={language}, step={step}, " +
                                    $"hit={hit?.GetType().Name ?? "null"}, target={tour.HighlightBounds}, card={tour.CardBounds}");
                            }
                            Check(completed == 0 && underlyingClicks == 0, "moving between tour steps does not complete, click, navigate or start translation");
                            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        Check(completed == 1 && tour.IsFinished && underlyingClicks == 0, "Done completes once without touching underlying controls");
                        tour.Advance(); tour.Skip(); tour.GoBack();
                        Check(completed == 1, "completion is idempotent after all navigation actions");
                    }
                }
            }

            UiText.Language = "en";
            AppearanceTheme.ApplyForTests(originalAppearance);
            CheckInteraction(Check);
            CheckPlacement(Check);
            RenderPreview(directory); checks++;
            Check(underlyingClicks == 0, "all theme/layout/keyboard checks leave dummy translation buttons untouched");
        }
        finally
        {
            UiText.Language = originalLanguage;
            AppearanceTheme.Apply(originalAppearance); ThemeBackground.Apply(originalAppearance);
        }
        return checks;
    }

    private static void CheckInteraction(Action<bool, string> check)
    {
        int completed = 0;
        var fixture = CreateFixture(() => throw new InvalidOperationException("Tour clicked an underlying target."));
        using (var tour = new FirstRunTourOverlay(key => fixture.Targets.GetValueOrDefault(key), () => completed++))
        {
            fixture.Root.Children.Add(tour); Layout(fixture.Root, tour, new Size(900, 650));
            check(KeyboardNavigation.GetTabNavigation(tour) == KeyboardNavigationMode.Cycle && FocusManager.GetIsFocusScope(tour),
                "keyboard tabbing stays within the visible tour controls");
            Geometry stableGeometry = Find<System.Windows.Shapes.Path>(tour, "TourScrim").Data;
            for (int index = 0; index < 100; index++) tour.RefreshPlacement();
            check(ReferenceEquals(stableGeometry, Find<System.Windows.Shapes.Path>(tour, "TourScrim").Data),
                "unchanged layout does not replace geometry or schedule another placement pass");
            Flush(tour.Dispatcher, DispatcherPriority.ContextIdle);
            check(tour.StepIndex == 0 && completed == 0, "layout settles and allows idle-priority work without advancing the tour");
            check(tour.HandleKey(Key.Left) && tour.StepIndex == 0, "Back on first step is a safe no-op");
            check(tour.HandleKey(Key.Right) && tour.StepIndex == 1, "Right advances without activating target");
            check(tour.HandleKey(Key.Left) && tour.StepIndex == 0, "Left returns to Help without navigation");
            check(!tour.HandleKey(Key.F1), "unrelated keys are not rewritten as Next");
            UiText.Language = "ko"; Flush(tour.Dispatcher);
            check(Find<TextBlock>(tour, "TourHeading").Text == UiText.T("Find help") &&
                Find<Button>(tour, "TourSkipButton").Content?.ToString() == UiText.T("Skip tour"),
                "an already-open tour updates language without recreation");
            check(tour.HandleKey(Key.Escape) && tour.IsFinished && completed == 1, "Escape skips and reports completion once");
            check(!tour.HandleKey(Key.Escape) && completed == 1, "repeat Escape does not repeat completion");
        }
        UiText.Language = "en";
        using (var skipped = new FirstRunTourOverlay(_ => null, () => completed++))
        {
            var root = new Grid(); root.Children.Add(skipped); Layout(root, skipped, new Size(900, 650));
            check(!skipped.HasTargetHighlight && !skipped.CardBounds.IsEmpty, "missing targets retain a centered usable instruction card");
            Find<Button>(skipped, "TourSkipButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(completed == 2 && skipped.IsFinished, "Skip works immediately before any other step");
        }
        using (var disposed = new FirstRunTourOverlay(_ => null, () => completed++))
        {
            disposed.Dispose(); disposed.Skip(); disposed.Advance();
            check(completed == 2 && !disposed.IsFinished, "host cleanup does not falsely mark the tour completed");
        }
        var detached = new Button { Width = 160, Height = 42 };
        detached.Measure(new Size(160, 42)); detached.Arrange(new Rect(0, 0, 160, 42));
        using var detachedTour = new FirstRunTourOverlay(_ => detached, () => completed++);
        var detachedRoot = new Grid(); detachedRoot.Children.Add(detachedTour); Layout(detachedRoot, detachedTour, new Size(900, 650));
        check(!detachedTour.HasTargetHighlight, "a rebuilt or detached target falls back safely rather than crashing");
    }

    private static void CheckPlacement(Action<bool, string> check)
    {
        foreach (Rect viewport in new[] { new Rect(0, 0, 900, 650), new Rect(0, 0, 1160, 800), new Rect(0, 0, 600, 420) })
        foreach (Rect target in new[] { new Rect(20, 180, 165, 42), new Rect(240, 340, 150, 42),
            new Rect(viewport.Width - 180, viewport.Height - 62, 160, 42), Rect.Empty })
        {
            Rect card = FirstRunTourOverlay.PlaceCard(viewport, target, new Size(378, 260));
            check(IsInside(card, viewport), "placement stays inside bounds for sidebar, Start, corner and missing targets");
            if (!target.IsEmpty) check(!card.IntersectsWith(target), "coach card does not cover a target when there is room beside it");
        }
        Rect tight = FirstRunTourOverlay.PlaceCard(new Rect(0, 0, 220, 160), new Rect(20, 20, 200, 120), new Size(378, 260));
        check(IsInside(tight, new Rect(0, 0, 220, 160)), "tiny hosts clamp the card instead of putting actions off-screen");
    }

    private static void RenderPreview(string directory)
    {
        var fixture = CreateFixture(() => { });
        using var tour = new FirstRunTourOverlay(key => fixture.Targets.GetValueOrDefault(key), () => { });
        fixture.Root.Children.Add(tour);
        Layout(fixture.Root, tour, new Size(1160, 800));
        var image = new RenderTargetBitmap(1160, 800, 96, 96, PixelFormats.Pbgra32); image.Render(fixture.Root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, "first-run-tour.png")); encoder.Save(file);
    }

    private static (Grid Root, Dictionary<string, FrameworkElement> Targets) CreateFixture(Action clicked)
    {
        var root = new Grid { Background = ModernTheme.Background };
        var canvas = new Canvas(); root.Children.Add(canvas);
        var sidebar = new Border { Width = 208, Height = 800, Background = ModernTheme.Surface };
        canvas.Children.Add(sidebar);
        var brand = ModernTheme.Heading("SayWhat?", 27); Canvas.SetLeft(brand, 28); Canvas.SetTop(brand, 32); canvas.Children.Add(brand);
        var title = ModernTheme.Heading("Home", 34); Canvas.SetLeft(title, 252); Canvas.SetTop(title, 48); canvas.Children.Add(title);
        var targets = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        AddTarget("Help", "Help", 24, 284, 160);
        AddTarget("Settings", "Settings", 24, 226, 160);
        AddTarget("OwnVoice", "Start", 280, 382, 140);
        AddTarget("Subtitles", "Start", 674, 382, 140);
        foreach ((double x, string text) in new[] { (252d, "Translate your voice"), (646d, "Read other voices") })
        {
            var card = new Border { Width = 366, Height = 200, CornerRadius = new CornerRadius(16),
                Background = ModernTheme.Surface, BorderBrush = ModernTheme.Stroke, BorderThickness = new Thickness(1),
                Child = ModernTheme.Heading(text, 23), Padding = new Thickness(28) };
            Canvas.SetLeft(card, x); Canvas.SetTop(card, 242); Panel.SetZIndex(card, -1); canvas.Children.Add(card);
        }
        return (root, targets);

        void AddTarget(string key, string text, double x, double y, double width)
        {
            var button = ModernTheme.Button(text, (_, _) => clicked(), primary: key is "OwnVoice" or "Subtitles");
            button.Width = width; button.Height = 42; button.Margin = new Thickness(0);
            Canvas.SetLeft(button, x); Canvas.SetTop(button, y); canvas.Children.Add(button); targets.Add(key, button);
        }
    }

    private static void Layout(Grid root, FirstRunTourOverlay tour, Size viewport)
    {
        root.InvalidateMeasure(); root.InvalidateArrange();
        root.Measure(viewport); root.Arrange(new Rect(new Point(), viewport)); root.UpdateLayout();
        tour.RefreshPlacement(); root.UpdateLayout(); Flush(root.Dispatcher);
        root.Measure(viewport); root.Arrange(new Rect(new Point(), viewport)); root.UpdateLayout();
        tour.RefreshPlacement(); root.UpdateLayout();
    }

    private static void Flush(Dispatcher dispatcher, DispatcherPriority priority = DispatcherPriority.Background)
    {
        var frame = new DispatcherFrame();
        bool finished = false;
        var deadline = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, dispatcher);
        _ = dispatcher.BeginInvoke(priority, (Action)(() => { finished = true; frame.Continue = false; }));
        try { Dispatcher.PushFrame(frame); }
        finally { deadline.Stop(); }
        if (!finished) throw new InvalidOperationException("First-run tour layout did not settle before the test deadline.");
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(item => item.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private static DependencyObject? VisualInputHit(Visual root, Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(root,
            candidate => candidate is UIElement element &&
                (!element.IsHitTestVisible || element.Visibility != Visibility.Visible)
                    ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(point));
        return hit;
    }
    private static bool IsInside(Rect inner, Rect outer) => !inner.IsEmpty && inner.X >= outer.X - .5 && inner.Y >= outer.Y - .5 &&
        inner.Right <= outer.Right + .5 && inner.Bottom <= outer.Bottom + .5;
}

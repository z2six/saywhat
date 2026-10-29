using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

internal static class OnboardingIntegrationTests
{
    // A preview MainWindow with isolated fixture settings; never shown, never started.
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Onboarding integration: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", flags)!.GetValue(window)!;
        AppSettings original = settings.Copy();
        string language = UiText.Language;
        byte[] savedSettings = File.ReadAllBytes(AppPaths.SettingsPath);
        try
        {
            var fresh = new AppSettings();
            Check(MainWindow.ShouldShowGuidedSetup(fresh, false, false), "new users enter dedicated setup");
            fresh.FirstRunIntroductionCompleted = true;
            Check(MainWindow.ShouldShowGuidedSetup(fresh, false, false), "old introduction acknowledgement cannot skip unfinished setup");
            fresh.FirstRunSetupCompleted = true;
            Check(!MainWindow.ShouldShowGuidedSetup(fresh, false, false), "updates do not force existing users through setup");
            fresh.FirstRunSetupCompleted = false;
            Check(!MainWindow.ShouldShowGuidedSetup(fresh, true, false), "preview cannot initiate real setup");
            Check(!MainWindow.ShouldShowGuidedSetup(fresh, false, true), "explicit startup workflow is not unexpectedly replaced");
            Check(MainWindow.ShouldCommitGuidedChoices(false, null), "first-time choices are retained for prerequisite restart resume");
            Check(!MainWindow.ShouldCommitGuidedChoices(true, null), "rerun cannot replace a working selection before preparation");
            Check(!MainWindow.ShouldCommitGuidedChoices(true, new(false, true, "restart")), "a prerequisite restart cannot overwrite an existing working model");
            Check(!MainWindow.ShouldCommitGuidedChoices(true, new(false, false, "failure")), "a failed rerun keeps existing translation choices");
            Check(MainWindow.ShouldCommitGuidedChoices(true, new(true, false, "ready")), "only verified preparation commits a rerun selection");
            settings.FirstRunSetupCompleted = true;
            settings.OwnManagedModelId = settings.IncomingManagedModelId = "hy-mt2-7b-q4_k_m";
            settings.OwnVoiceTargetLanguage = "Korean"; settings.IncomingTargetLanguage = "Japanese";
            settings.UseAiSpeakerRecognition = false;
            foreach (string locale in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = locale; window.Navigate("Settings");
                var page = (StackPanel)((ContentControl)typeof(MainWindow).GetField("_content", flags)!.GetValue(window)!).Content;
                var replay = page.Children.OfType<Border>().Take(2).SelectMany(Descendants).OfType<Button>()
                    .Single(button => button.Name == "RestartGuidedSetupButton");
                Check(replay.IsEnabled && replay.Content?.ToString() == UiText.T("Run setup again"), "setup replay remains near the top of Settings below profiles in " + locale);
                window.OpenGuidedSetup(firstRun: false);
                Check(window.Content is FirstRunSetupView, "replay completely replaces dashboard content");
                var flow = (FirstRunSetupView)window.Content;
                Check(flow.Draft.OwnManagedModelId == "hy-mt2-7b-q4_k_m" && flow.Draft.OwnVoiceTargetLanguage == "Korean" &&
                    flow.Draft.IncomingTargetLanguage == "Japanese" && !flow.Draft.UseAiSpeakerRecognition,
                    "replay seeds saved languages/model/speaker preferences instead of resetting data");
                Check(!flow.IsBusy && !window.IsLoaded && !window.IsVisible, "opening setup does not inspect hardware or start preparation");
                window.LeaveGuidedSetup();
                Check(window.Content is Grid && Read<string>(window, "_page") == "Settings", "leaving replay restores Settings");
                Check(File.ReadAllBytes(AppPaths.SettingsPath).SequenceEqual(savedSettings), "viewing/leaving replay does not alter saved choices");
            }
            UiText.Language = "en";
            window.ShowQuickTour();
            var frame = (Grid)window.Content;
            var tour = frame.Children.OfType<FirstRunTourOverlay>().Single();
            frame.Measure(new Size(1160, 800)); frame.Arrange(new Rect(0, 0, 1160, 800)); frame.UpdateLayout(); tour.RefreshPlacement(); frame.UpdateLayout();
            Check(tour.TargetKey == "Help" && tour.HasTargetHighlight, "real dashboard tour highlights Help first");
            var bitmap = new RenderTargetBitmap(1160, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "dashboard-quick-tour.png"))) encoder.Save(file);
            foreach (string target in new[] { "OwnVoice", "Subtitles", "Settings" })
            {
                tour.Advance(); frame.UpdateLayout(); tour.RefreshPlacement();
                Check(tour.TargetKey == target && tour.HasTargetHighlight, "real dashboard resolves " + target + " without activating it");
                if (target == "Subtitles")
                {
                    FrameworkElement subtitles = Read<FrameworkElement>(window, "_subtitlesTourTarget");
                    ScrollViewer? viewport = null;
                    for (DependencyObject? parent = VisualTreeHelper.GetParent(subtitles); parent is not null;
                        parent = VisualTreeHelper.GetParent(parent))
                        if (parent is ScrollViewer scroll) { viewport = scroll; break; }
                    Check(viewport is not null, "optional subtitles retain a scrollable Home viewport");
                    ScrollViewer visibleViewport = viewport!;
                    Point origin = subtitles.TranslatePoint(new Point(), visibleViewport);
                    Check(origin.Y >= -.5 && origin.Y + subtitles.ActualHeight <= visibleViewport.ViewportHeight + .5,
                        "the tour scrolls the secondary subtitle action fully into view before highlighting it");
                    double offset = visibleViewport.VerticalOffset;
                    for (int refresh = 0; refresh < 20; refresh++) tour.RefreshPlacement();
                    Check(Math.Abs(visibleViewport.VerticalOffset - offset) < .5,
                        "repeated placement in the same tour step does not keep forcing the Home scroll position");
                }
            }
            tour.Skip();
            Check(!frame.Children.OfType<FirstRunTourOverlay>().Any(), "skipping removes overlay from real dashboard");
            frame.UpdateLayout();
            FrameworkElement ownStart = Read<FrameworkElement>(window, "_ownVoiceTourTarget");
            var homeContent = Read<ContentControl>(window, "_content");
            Check(homeContent.Parent is ScrollViewer, "Home keeps its expected scroll owner");
            var homeViewport = (ScrollViewer)homeContent.Parent;
            Point ownOrigin = ownStart.TranslatePoint(new Point(), homeViewport);
            Check(homeViewport.VerticalOffset < .5 && ownOrigin.Y >= -.5 &&
                ownOrigin.Y + ownStart.ActualHeight <= homeViewport.ViewportHeight + .5,
                "closing the tour restores Home to the top with the primary own-voice action visible");
            window.ShowQuickTour();
            FirstRunTourOverlay completedTour = frame.Children.OfType<FirstRunTourOverlay>().Single();
            for (int step = 0; step < FirstRunTourOverlay.Steps.Count; step++)
            { frame.UpdateLayout(); completedTour.RefreshPlacement(); completedTour.Advance(); }
            frame.UpdateLayout();
            Check(!frame.Children.OfType<FirstRunTourOverlay>().Any() && homeViewport.VerticalOffset < .5,
                "finishing every tour step also restores the primary Home position");
            Check(File.ReadAllBytes(AppPaths.SettingsPath).SequenceEqual(savedSettings), "preview tour does not write acknowledgement or translation settings");
        }
        finally
        {
            window.LeaveGuidedSetup();
            settings.FirstRunSetupCompleted = original.FirstRunSetupCompleted;
            settings.OwnManagedModelId = original.OwnManagedModelId; settings.IncomingManagedModelId = original.IncomingManagedModelId;
            settings.OwnVoiceTargetLanguage = original.OwnVoiceTargetLanguage; settings.IncomingTargetLanguage = original.IncomingTargetLanguage;
            settings.UseAiSpeakerRecognition = original.UseAiSpeakerRecognition;
            UiText.Language = language; window.Navigate("Home");
        }
        return checks;
    }
    private static T Read<T>(MainWindow window, string field) =>
        (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject descendant in Descendants(dependency)) yield return descendant;
    }
}

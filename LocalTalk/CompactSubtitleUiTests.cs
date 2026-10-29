using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Unshown layout, synthetic text, and isolated preference files only. No
// Loaded event, service, capture, clipboard or model is used by these checks.
internal static class CompactSubtitleUiTests
{
    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Compact subtitles: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        AppSettings before = SettingsStore.Load();
        var overlay = new SubtitleWindow();
        var settings = (AppSettings)typeof(SubtitleWindow).GetField("_settings", flags)!.GetValue(overlay)!;
        try
        {
            settings.OverlayMode = "Sentence bubbles"; settings.SubtitlesFontSize = 16;
            settings.SubtitlesBubbleWidth = 420; settings.SubtitlesBackgroundOpacity = .65; settings.SubtitlesShowOriginalText = true;
            Check(overlay.Width <= 420 && overlay.ResizeMode == ResizeMode.NoResize && overlay.AllowsTransparency,
                "the only interactive window is a compact, non-resizing transparent toolbar");
            DateTimeOffset time = DateTimeOffset.UnixEpoch;
            for (int i = 0; i < 10; i++)
                overlay.Publish(new("Mira", "今天我们一起去看看。" + i, "Let's go and have a look together. " + i, i + 1, time.AddSeconds(i)));
            Check(overlay.CompactBubbleCount == 3 && overlay.HistoryEntryCount == 10,
                "the passive surface shows only three recent sentences without losing retained history");
            Check(Nodes(overlay.CompactPreview).OfType<Button>().Count() == 0 &&
                Nodes(overlay.CompactPreview).OfType<Border>().Where(b => b.Name == "CompactSubtitleBubble").All(b => !b.IsHitTestVisible),
                "subtitle bubbles contain no interactive controls");
            Check(Nodes(overlay.CompactPreview).OfType<TextBlock>().Count(t => t.Name == "CompactOriginalText") == 3 &&
                Nodes(overlay.CompactPreview).OfType<TextBlock>().Where(t => t.Name == "CompactOriginalText").All(t => t.FontSize < 16),
                "original Chinese is visible in smaller text above the translation");
            Check(Nodes(overlay.CompactPreview).OfType<Border>().Where(b => b.Name == "CompactSubtitleBubble")
                .All(b => b.Background is SolidColorBrush brush && brush.Color.A is >= 165 and <= 166) && overlay.Opacity == 1,
                "backgrounds are semi-transparent without fading the whole window or text");
            TextBox first = Nodes(overlay.TranscriptRoot).OfType<TextBox>().First(t => t.Name == "TranscriptTranslationText");
            first.Select(6, 8);
            overlay.Publish(new("Jin", "我也想去。", "I'd like to go too.", 11, time.AddSeconds(11)));
            Check(ReferenceEquals(first, Nodes(overlay.TranscriptRoot).OfType<TextBox>().First(t => t.Name == "TranscriptTranslationText")) &&
                first.SelectionStart == 6 && first.SelectionLength == 8,
                "another sentence cannot destroy an earlier selectable text control or its selection");
            Check(overlay.TranscriptText().Contains("今天我们一起去看看。0", StringComparison.Ordinal) &&
                overlay.TranscriptText().Contains("I'd like to go too.", StringComparison.Ordinal) &&
                Nodes(overlay.TranscriptRoot).OfType<TextBox>().Where(t => t.Name == "TranscriptTranslationText").All(t => t.IsReadOnly),
                "the transcript retains both languages in chronological copyable text");
            var appearance = overlay.CreateAppearanceControls();
            Check(Nodes(appearance).OfType<Slider>().Count() == 3 && Nodes(appearance).OfType<CheckBox>().Any(t => t.Name == "SubtitleOriginalTextToggle"),
                "one small panel exposes font, width, background and original text");
            foreach (var slider in Nodes(appearance).OfType<Slider>())
                slider.Value = slider.Name switch { "SubtitleFontSlider" => 14, "SubtitleWidthSlider" => 360, _ => 0 };
            Check(settings.SubtitlesFontSize == 14 && settings.SubtitlesBubbleWidth == 360 && settings.SubtitlesBackgroundOpacity == 0 &&
                Nodes(overlay.CompactPreview).OfType<Border>().Where(b => b.Name == "CompactSubtitleBubble").All(b => ((SolidColorBrush)b.Background).Color.A == 0),
                "quick controls immediately apply readable text and fully transparent backgrounds");
            var original = Nodes(appearance).OfType<CheckBox>().Single(t => t.Name == "SubtitleOriginalTextToggle");
            original.IsChecked = false; original.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Check(!Nodes(overlay.CompactPreview).OfType<TextBlock>().Any(t => t.Name == "CompactOriginalText") &&
                Nodes(overlay.TranscriptRoot).OfType<TextBox>().Where(t => t.Name == "TranscriptOriginalText").All(t => t.Visibility == Visibility.Collapsed),
                "original text can be hidden without changing stored recognition results");
            settings.SubtitlesShowOriginalText = true; settings.SubtitlesBackgroundOpacity = .65;
            settings.SubtitlesFontSize = 16; settings.SubtitlesBubbleWidth = 420;
            typeof(SubtitleWindow).GetMethod("RefreshCompactSurface", flags)!.Invoke(overlay, null);
            typeof(SubtitleWindow).GetMethod("RefreshTranscriptTypography", flags)!.Invoke(overlay, null);
            RenderScene(overlay, Path.Combine(directory, "compact-subtitle-overlay.png"));
            Render(overlay.TranscriptRoot, Path.Combine(directory, "subtitle-transcript.png"), 800, 640);
            Render(appearance, Path.Combine(directory, "subtitle-quick-appearance.png"), 285, 345);
            for (int i = 12; i <= 212; i++) overlay.Publish(new("Synthetic person", "原文 " + i, "Translation " + i, i, time.AddSeconds(i)));
            Check(overlay.HistoryEntryCount == 200 && overlay.CompactBubbleCount == 3 && overlay.HistoryEntry(1) is null,
                "both retained and visible history have explicit bounds");
            Check(!overlay.IsLoaded && !overlay.IsVisible && typeof(SubtitleWindow).GetField("_service", flags)!.GetValue(overlay) is null &&
                typeof(SubtitleWindow).GetField("_textSurface", flags)!.GetValue(overlay) is null,
                "all layout and preference actions stayed inert");
        }
        finally { overlay.Close(); SettingsStore.SaveSubtitleAppearancePreference(before); SettingsStore.Save(before); }
        return checks;
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node) foreach (DependencyObject descendant in Nodes(node)) yield return descendant;
    }
    private static RenderTargetBitmap Bitmap(FrameworkElement view, int width, int height)
    {
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view); return bitmap;
    }
    private static void Save(BitmapSource bitmap, string path)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file); }
    private static void Render(FrameworkElement view, string path, int width, int height) => Save(Bitmap(view, width, height), path);
    private static void RenderScene(SubtitleWindow overlay, string path)
    {
        var toolbar = Bitmap((FrameworkElement)overlay.Content, 420, 38); var body = Bitmap(overlay.CompactPreview, 420, 355);
        var scene = new DrawingVisual();
        using (DrawingContext draw = scene.RenderOpen())
        {
            draw.DrawRectangle(new LinearGradientBrush(Color.FromRgb(43, 64, 51), Color.FromRgb(129, 103, 75), 30), null, new Rect(0, 0, 460, 420));
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(91, 112, 93)), null, new Rect(0, 240, 460, 180));
            draw.DrawImage(toolbar, new Rect(20, 16, 420, 38)); draw.DrawImage(body, new Rect(20, 60, 420, 355));
        }
        var output = new RenderTargetBitmap(460, 420, 96, 96, PixelFormats.Pbgra32); output.Render(scene); Save(output, path);
    }
}

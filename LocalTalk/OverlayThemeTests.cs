using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Unshown controls and embedded artwork only. Never starts subtitles, captures audio,
// creates a tray icon, or contacts translation/speech providers.
internal static class OverlayThemeTests
{
    internal static int Run()
    {
        AppSettings previous = SettingsStore.Load();
        var overlay = new SubtitleWindow();
        int checks = 0;
        void Check(bool value, string description)
        { if (!value) throw new InvalidOperationException("Overlay theme check: " + description); checks++; }
        T Field<T>(string name) => (T)typeof(SubtitleWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
        try
        {
            var source = Field<TextBlock>("_source");
            string originalSource = source.Text;
            var body = Field<StackPanel>("_speakerRows");
            var existingTranslation = new TextBlock { Text = "Unchanged sentence 中文 日本語 한국어", Foreground = AppearanceTheme.Text };
            var bubble = new Border { Background = AppearanceTheme.Surface, BorderBrush = AppearanceTheme.Stroke, Child = existingTranslation };
            body.Children.Add(bubble);
            Image lockup = BrandArtwork.Lockup();
            foreach (ThemePreset preset in AppearanceTheme.Presets.Where(p => p.Id != "custom"))
            {
                var settings = previous.Copy(); settings.UiThemeId = preset.Id;
                AppearanceTheme.Apply(settings);
                Check(ReferenceEquals(existingTranslation.Foreground, AppearanceTheme.Text), preset.Id + " existing subtitle text follows the shared role");
                Check(ReferenceEquals(bubble.Background, AppearanceTheme.Surface) && ReferenceEquals(bubble.BorderBrush, AppearanceTheme.Stroke), preset.Id + " existing bubble follows shared roles");
                Check(existingTranslation.Text == "Unchanged sentence 中文 日本語 한국어" && body.Children.Contains(bubble), preset.Id + " does not replace transcript history");
                Check(source.Text == originalSource, preset.Id + " does not rewrite speech data");
                Check(Field<object?>("_service") is null && !Field<bool>("_serviceStarted"), preset.Id + " does not start audio or a model");
                Check(ReferenceEquals(Field<Button>("_alwaysOnTopButton").BorderBrush, previous.SubtitlesAlwaysOnTop ? AppearanceTheme.Good : AppearanceTheme.Stroke), preset.Id + " pin state border follows theme");
                Check(ReferenceEquals(overlay.Resources["OverlayText"], AppearanceTheme.Text) &&
                    ReferenceEquals(overlay.Resources["OverlayScrollGrip"], AppearanceTheme.ScrollGrip), preset.Id + " templates and scrollbars use shared roles");
                if (!preset.IsDark)
                {
                    var bitmap = new FormatConvertedBitmap((BitmapSource)lockup.Source, PixelFormats.Bgra32, null, 0);
                    var corner = new byte[4]; bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
                    Check(corner[3] == 0, preset.Id + " wordmark has no old opaque dark backing");
                    Check(AppearanceTheme.Contrast(AppearanceTheme.Text.Color, AppearanceTheme.Surface.Color) >= 4.5,
                        preset.Id + " body text has readable light-theme contrast");
                }
            }
        }
        finally
        {
            overlay.Close(); // Detach theme listeners; OnLoaded was never invoked.
            AppearanceTheme.Apply(previous);
            ThemeBackground.Apply(previous);
        }
        return checks;
    }
}

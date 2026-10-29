using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.Windows.Data;

namespace IncomingTranslator;

// Artwork is decoded entirely from embedded resources. OnLoad releases each resource
// stream immediately; frozen cached sources can be shared without sharing UI elements.
internal static class BrandArtwork
{
    internal const string IconResourcePath = "Assets/SayWhat.ico";
    internal const string WindowIconResourcePath = "Assets/saywhat-icon-256.png";
    internal const string MarkResourcePath = "Assets/saywhat-mark.png";
    internal const string LockupResourcePath = "Assets/saywhat-lockup.png";

    private static readonly Lazy<BitmapSource> IconCache = new(() => Load(WindowIconResourcePath));
    private static readonly Lazy<BitmapSource> MarkCache = new(() => Load(MarkResourcePath));
    private static readonly Lazy<BitmapSource> LockupCache = new(() => Load(LockupResourcePath));
    private static readonly ThemeArtworkState ThemedSources = new();

    internal static BitmapSource WindowIcon => IconCache.Value;
    internal static BitmapSource MarkSource => MarkCache.Value;
    internal static BitmapSource LockupSource => LockupCache.Value;

    internal static Image Mark(double size = 44)
    {
        RequirePositiveSize(size);
        return Artwork(MarkSource, "SayWhatMark", "SayWhat.Mark", size, size, nameof(ThemeArtworkState.Mark));
    }

    internal static Image Lockup(double width = 164)
    {
        RequirePositiveSize(width);
        return Artwork(LockupSource, "SayWhatLockup", "SayWhat.Lockup", width, null, nameof(ThemeArtworkState.Lockup));
    }

    internal static void ConfigureSubtitleBranding()
    {
        // The shared subtitle source can still compile/run on its own, without referencing
        // this dashboard helper or requiring the packaged application resources.
        SubtitleWindow.WindowIconProvider = () => WindowIcon;
        SubtitleWindow.HeaderMarkProvider = size => Mark(size);
        SubtitleWindow.TranslateUiProvider = UiText.T;
        SubtitleWindow.FormatUiProvider = UiText.Format;
        SubtitleWindow.LocalizeUiProvider = UiLocalization.ApplyTree;
        SubtitleWindow.MarkLiteralUiProvider = element => UiLocalization.Literal(element);
        AppearanceTheme.TranslateMessageUi = UiText.T;
    }

    // The caller owns and disposes the returned icon. No resource stream/file handle is
    // retained, and disposing a tray icon cannot affect another caller's icon instance.
    internal static System.Drawing.Icon CreateTrayIcon()
    {
        using Stream resource = OpenIconResource();
        using var decoded = new System.Drawing.Icon(resource, 32, 32);
        return (System.Drawing.Icon)decoded.Clone();
    }

    internal static Stream OpenIconResource() => OpenResource(IconResourcePath);

    private static Stream OpenResource(string path)
    {
        string assembly = typeof(BrandArtwork).Assembly.GetName().Name
            ?? throw new InvalidOperationException("The application artwork assembly has no name.");
        var uri = new Uri("/" + assembly + ";component/" + path, UriKind.Relative);
        return Application.GetResourceStream(uri)?.Stream
            ?? throw new InvalidDataException("The embedded SayWhat? artwork is missing: " + path);
    }

    private static BitmapSource Load(string path)
    {
        using Stream resource = OpenResource(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = resource;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static Image Artwork(BitmapSource source, string name, string tag, double width, double? height, string themedProperty)
    {
        var image = new Image
        {
            Name = name, Tag = tag, Source = source, Width = width,
            Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false, Focusable = false, SnapsToDevicePixels = true
        };
        // WPF's weak property subscription keeps factory-created images collectible. A theme
        // change recolors the approved pixel silhouette rather than introducing a new logo.
        image.SetBinding(Image.SourceProperty, new Binding(themedProperty) { Source = ThemedSources });
        if (height is double fixedHeight) image.Height = fixedHeight;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image, AppBrand.DisplayName + " logo");
        return image;
    }

    private sealed class ThemeArtworkState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private BitmapSource? _mark, _lockup;
        private (Color Text, Color Accent, Color Surface)? _palette;
        public BitmapSource Mark { get { EnsurePalette(); return _mark!; } }
        public BitmapSource Lockup { get { EnsurePalette(); return _lockup!; } }
        public ThemeArtworkState() => AppearanceTheme.Changed += () =>
        {
            _palette = null;
            PropertyChanged?.Invoke(this, new(nameof(Mark)));
            PropertyChanged?.Invoke(this, new(nameof(Lockup)));
        };
        private void EnsurePalette()
        {
            var palette = (AppearanceTheme.Text.Color, AppearanceTheme.Accent.Color, AppearanceTheme.Surface.Color);
            if (_palette == palette) return;
            // The canonical brand theme retains the approved original pixels even when
            // contrast normalization slightly adjusts functional control colors.
            bool original = AppearanceTheme.CurrentPresetId == "coral-night";
            _mark = original ? MarkSource : Recolor(MarkSource, AppearanceTheme.Ink.Color, palette.Item2, false);
            _lockup = original ? LockupSource : Recolor(LockupSource, palette.Item1, palette.Item2, true);
            _palette = palette;
        }
    }

    // The master lockup was supplied on an opaque charcoal backing. Recover its two
    // antialiased foreground masks, then composite with the theme's text/accent colors.
    // No image files are modified, downloaded, or retained outside embedded resources.
    private static BitmapSource Recolor(BitmapSource source, Color text, Color accent, bool removeBacking)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        double[] backing = { 31, 33, 36 }, ivory = { 231, 239, 246 }, coral = { 120, 140, 242 };
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] == 0) continue;
            double coverage = 1;
            bool isAccent;
            if (removeBacking)
            {
                (double alpha, double error) Fit(double[] foreground)
                {
                    double numerator = 0, denominator = 0;
                    for (int channel = 0; channel < 3; channel++)
                    { double vector = foreground[channel] - backing[channel]; numerator += (pixels[offset + channel] - backing[channel]) * vector; denominator += vector * vector; }
                    double alpha = Math.Clamp(numerator / denominator, 0, 1), error = 0;
                    for (int channel = 0; channel < 3; channel++)
                    { double residual = pixels[offset + channel] - (backing[channel] + alpha * (foreground[channel] - backing[channel])); error += residual * residual; }
                    return (alpha, error);
                }
                var textFit = Fit(ivory); var accentFit = Fit(coral);
                isAccent = accentFit.error < textFit.error;
                coverage = (isAccent ? accentFit.alpha : textFit.alpha) * pixels[offset + 3] / 255d;
                // The supplied raster includes harmless backing compression noise.
                if (coverage < 0.035) coverage = 0;
            }
            else isAccent = pixels[offset + 2] > pixels[offset] + 20;
            Color foregroundColor = isAccent ? accent : text;
            pixels[offset] = foregroundColor.B; pixels[offset + 1] = foregroundColor.G; pixels[offset + 2] = foregroundColor.R;
            if (removeBacking) pixels[offset + 3] = (byte)Math.Round(coverage * 255);
        }
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze(); return result;
    }

    private static void RequirePositiveSize(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Artwork size must be finite and positive.");
    }
}

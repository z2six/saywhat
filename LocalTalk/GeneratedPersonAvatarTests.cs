using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

internal static class GeneratedPersonAvatarTests
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Generated person artwork: " + detail); checks++; }
        var person = new SpeakerPerson("synthetic-person", "Alice", 0);
        DrawingImage first = GeneratedPersonAvatar.Resolve(person);
        Check(first.IsFrozen && first.Width == 128 && first.Height == 128, "bounded scalable vector art is frozen");
        Check(ReferenceEquals(first, GeneratedPersonAvatar.Resolve(person with { Name = "Renamed", TranslationCount = 800 })),
            "name and translation count do not alter a person's artwork");
        var voiced = person with { VoiceAvatarSeed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-voice-only"))).ToLowerInvariant() };
        DrawingImage fromVoice = GeneratedPersonAvatar.Resolve(voiced);
        Check(!ReferenceEquals(first, fromVoice) && Fingerprint(first) != Fingerprint(fromVoice), "confirmed voice seed changes fallback artwork");
        Check(ReferenceEquals(fromVoice, GeneratedPersonAvatar.Resolve(voiced with { ConfirmedSamples = 20 })),
            "saved voice-derived artwork is stable as examples grow");
        var generated = (Grid)PersonAvatar.Create(voiced, 96, Brushes.Gray, Brushes.White, dataRoot: directory);
        Check(ReferenceEquals(generated.Children.OfType<Image>().Single().Source, fromVoice), "saved people use generated artwork without photos");
        var anonymous = (Grid)PersonAvatar.Create(null, 30, Brushes.Gray, Brushes.White, "Unknown", directory);
        Check(anonymous.Children.OfType<TextBlock>().Single().Text == "U", "unknown unsaved voices retain a neutral fallback");
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen()) context.DrawRectangle(Brushes.Coral, null, new Rect(0, 0, 512, 512));
        var pixels = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32); pixels.Render(visual);
        using var staged = PersonAvatar.StageEditedPhoto(pixels);
        string photo = PersonAvatar.Import(staged.Path, directory);
        try
        {
            var custom = (Grid)PersonAvatar.Create(voiced with { AvatarFileName = photo }, 96, Brushes.Gray, Brushes.White, dataRoot: directory);
            Check(custom.Children.OfType<Image>().Single().Source is BitmapSource, "chosen photos override generated art");
            var removed = (Grid)PersonAvatar.Create(voiced, 30, Brushes.Gray, Brushes.White, dataRoot: directory);
            Check(ReferenceEquals(removed.Children.OfType<Image>().Single().Source, fromVoice), "removing a photo restores the same generated art");
        }
        finally { PersonAvatar.DeleteManaged(photo, directory); }
        var render = new DrawingVisual();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        using (DrawingContext context = render.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(27, 25, 23)), null, new Rect(0, 0, 720, 560));
            context.DrawText(new FormattedText("SayWhat? · Generated people pictures", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 22, Brushes.Bisque, 1), new Point(24, 18));
            for (int index = 0; index < 24; index++)
            {
                var item = new SpeakerPerson("gallery-person-" + index, "Person " + (index + 1), 1)
                { VoiceAvatarSeed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-gallery-voice-" + index))).ToLowerInvariant() };
                DrawingImage image = GeneratedPersonAvatar.Resolve(item);
                signatures.Add(Fingerprint(image));
                double x = 24 + index % 6 * 115, y = 64 + index / 6 * 120;
                context.PushClip(new EllipseGeometry(new Point(x + 48, y + 48), 48, 48));
                context.DrawImage(image, new Rect(x, y, 96, 96)); context.Pop();
            }
        }
        Check(signatures.Count == 24, "different voice seeds produce distinct rendered pictures");
        var gallery = new RenderTargetBitmap(720, 560, 96, 96, PixelFormats.Pbgra32); gallery.Render(render);
        using (var output = new FileStream(Path.Combine(directory, "generated-people-gallery.png"), FileMode.Create, FileAccess.Write))
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(gallery)); encoder.Save(output); }
        // Exercise eviction: no unbounded per-person retention and rebuilding is deterministic.
        string before = Fingerprint(first);
        for (int index = 0; index < 140; index++) _ = GeneratedPersonAvatar.Resolve(new("cache-test-" + index, "Unused", 0));
        Check(before == Fingerprint(GeneratedPersonAvatar.Resolve(person)), "art is deterministic after bounded-cache eviction");
        return checks;
    }

    private static string Fingerprint(ImageSource image)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen()) context.DrawImage(image, new Rect(0, 0, 128, 128));
        var rendered = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
        byte[] pixels = new byte[128 * 128 * 4]; rendered.CopyPixels(pixels, 128 * 4, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }
}

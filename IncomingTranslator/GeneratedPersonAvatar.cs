using System.Windows;
using System.Windows.Media;

namespace IncomingTranslator;

// Decorative vector art, not an inference about appearance or an audio waveform.
// Only the opaque, locally derived seed enters the renderer. No audio/model access.
internal static class GeneratedPersonAvatar
{
    private const int MaximumCachedImages = 128;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DrawingImage> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private static readonly string[][] Palettes =
    [
        ["#302441", "#F5D3B3", "#F19482", "#C3B6E9", "#758878"],
        ["#243D3B", "#ECD7B1", "#AAD0B5", "#E89570", "#749CA3"],
        ["#3F2931", "#F1D9CC", "#ECAFAF", "#AAB6DB", "#C49264"],
        ["#292C43", "#DBD9ED", "#E4BA70", "#B9C3A8", "#BD8DB5"],
        ["#3D342B", "#F7E2B7", "#DCAE79", "#A4CDBB", "#DA948C"],
        ["#293541", "#CAE5D5", "#91B6CE", "#E8AB97", "#BEA5DC"]
    ];

    internal static DrawingImage Resolve(SpeakerPerson person)
    {
        string seed = PersonAvatarSeed.Resolve(person);
        lock (Gate)
        {
            if (Cache.TryGetValue(seed, out DrawingImage? cached)) return cached;
            DrawingImage image = Draw(Convert.FromHexString(seed));
            while (Cache.Count >= MaximumCachedImages && Order.Count > 0) Cache.Remove(Order.Dequeue());
            Cache.Add(seed, image); Order.Enqueue(seed);
            return image;
        }
    }

    private static DrawingImage Draw(byte[] seed)
    {
        Brush[] palette = Palettes[seed[0] % Palettes.Length].Select(value =>
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return (Brush)brush;
        }).ToArray();
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 128, 128)) };
        using (DrawingContext context = drawing.Open())
        {
            context.DrawRectangle(palette[0], null, new Rect(0, 0, 128, 128));
            // Off-centre moon and fine orbit provide a common visual language.
            var wash = new SolidColorBrush(((SolidColorBrush)palette[1]).Color) { Opacity = .08 }; wash.Freeze();
            context.DrawEllipse(wash, null, new Point(24 + seed[1] % 75, 30 + seed[2] % 65), 58, 58);
            var orbitBrush = new SolidColorBrush(((SolidColorBrush)palette[1]).Color) { Opacity = .26 }; orbitBrush.Freeze();
            context.DrawEllipse(null, new Pen(orbitBrush, .8), new Point(64, 64), 53, 53);
            context.PushTransform(new RotateTransform((seed[3] % 8) * 45, 64, 64));
            switch (seed[4] % 4)
            {
                case 0: DrawRibbon(context, seed, palette); break;
                case 1: DrawBloom(context, seed, palette); break;
                case 2: DrawOrbit(context, seed, palette); break;
                default: DrawWeave(context, seed, palette); break;
            }
            context.Pop();
            // A different tiny constellation for every seed, without using letters.
            for (int index = 0; index < 3; index++)
            {
                double angle = (seed[20 + index] / 255d * 360) * Math.PI / 180;
                double radius = 45 + seed[23 + index] % 7;
                context.DrawEllipse(palette[1 + index], null,
                    new Point(64 + Math.Cos(angle) * radius, 64 + Math.Sin(angle) * radius), 1.5 + index * .3, 1.5 + index * .3);
            }
        }
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }

    private static void DrawRibbon(DrawingContext context, byte[] seed, Brush[] palette)
    {
        int count = 3 + seed[5] % 3;
        double spacing = 8 + seed[6] % 4;
        for (int index = count - 1; index >= 0; index--)
        {
            double shift = (index - (count - 1) / 2d) * spacing;
            var geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(new Point(27, 77 + shift), false, false);
                path.BezierTo(new Point(42, 15 + shift), new Point(86, 105 + shift), new Point(101, 44 + shift), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(null, new Pen(palette[1 + index % 4], 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }
    }

    private static void DrawBloom(DrawingContext context, byte[] seed, Brush[] palette)
    {
        int petals = 5 + seed[7] % 4;
        for (int index = 0; index < petals; index++)
        {
            context.PushTransform(new RotateTransform(index * 360d / petals, 64, 64));
            var geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(new Point(64, 70), true, true);
                path.BezierTo(new Point(33, 51), new Point(45, 17 + seed[8] % 9), new Point(64, 23), true, false);
                path.BezierTo(new Point(87, 31), new Point(83, 53), new Point(64, 70), true, false);
            }
            geometry.Freeze(); context.DrawGeometry(palette[1 + index % 4], null, geometry); context.Pop();
        }
        context.DrawEllipse(palette[0], null, new Point(64, 64), 12, 12);
        context.DrawEllipse(palette[1], null, new Point(64, 64), 5, 5);
    }

    private static void DrawOrbit(DrawingContext context, byte[] seed, Brush[] palette)
    {
        // Interlocking ceramic loops around a deliberately asymmetric centre.
        for (int index = 0; index < 3; index++)
        {
            context.PushTransform(new RotateTransform(index * 60 + seed[9] % 25, 64, 64));
            context.DrawEllipse(null, new Pen(palette[1 + index], 8 + seed[10 + index] % 4),
                new Point(64, 64), 17 + seed[13 + index] % 7, 34);
            context.Pop();
        }
        context.DrawEllipse(palette[0], null, new Point(64, 64), 11, 11);
        context.DrawEllipse(palette[4], null, new Point(66, 61), 6, 6);
    }

    private static void DrawWeave(DrawingContext context, byte[] seed, Brush[] palette)
    {
        int rows = 3 + seed[16] % 3;
        for (int row = 0; row < rows; row++)
        {
            double y = 64 + (row - (rows - 1) / 2d) * 12;
            double inset = seed[17 + row % 3] % 14;
            context.DrawRoundedRectangle(palette[1 + row % 4], null, new Rect(25 + inset, y - 4, 76 - inset, 9), 4.5, 4.5);
        }
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(new Point(53, 27), false, false);
            path.BezierTo(new Point(90, 48), new Point(32, 80), new Point(75, 101), true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(null, new Pen(palette[0], 13) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        context.DrawGeometry(null, new Pen(palette[4], 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}

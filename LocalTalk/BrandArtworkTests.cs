using System.Buffers.Binary;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Embedded artwork and unshown WPF controls only. No NotifyIcon, window Show,
// settings dialog, audio device, provider, model, or live app-data access.
internal static class BrandArtworkTests
{
    internal static int RunFactories()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Brand artwork check: " + description);
            checks++;
        }

        BitmapSource icon = BrandArtwork.WindowIcon;
        BitmapSource mark = BrandArtwork.MarkSource;
        BitmapSource lockup = BrandArtwork.LockupSource;
        Check(ReferenceEquals(icon, BrandArtwork.WindowIcon), "window icon is cached");
        Check(ReferenceEquals(mark, BrandArtwork.MarkSource), "symbol pixels are cached");
        Check(ReferenceEquals(lockup, BrandArtwork.LockupSource), "wordmark pixels are cached");
        foreach (BitmapSource source in new[] { icon, mark, lockup })
        {
            Check(source.IsFrozen, "cached image pixels are frozen for cross-dispatcher reuse");
            Check(source.PixelWidth > 0 && source.PixelHeight > 0, "cached artwork is decoded and nonempty");
            Check(Pixels(source).Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0), "cached artwork contains visible pixels");
        }
        CheckAlpha(icon, Check);
        CheckAlpha(mark, Check);
        Check(CornerPixels(icon).All(pixel => pixel.A == 0), "window icon has transparent outside corners");
        Check(CornerPixels(mark).All(pixel => pixel.A == 0), "symbol has transparent outside corners");
        Check(icon.PixelWidth == 256 && icon.PixelHeight == 256, "window icon retains its 256-pixel master");
        Check(mark.PixelWidth == mark.PixelHeight, "standalone symbol has square bounds");
        double lockupRatio = (double)lockup.PixelWidth / lockup.PixelHeight;
        Check(lockupRatio is >= 1.95 and <= 2.05, "approved horizontal lockup retains its 2:1 aspect ratio");
        Check(CornerPixels(lockup).All(pixel => pixel.A == 255 &&
            Math.Abs(pixel.R - 0x24) <= 5 && Math.Abs(pixel.G - 0x21) <= 5 && Math.Abs(pixel.B - 0x1F) <= 5),
            "opaque raster lockup corners stay within five channel levels of the warm sidebar surface");
        foreach (var item in new[]
        {
            (Path: BrandArtwork.WindowIconResourcePath, Source: icon),
            (Path: BrandArtwork.MarkResourcePath, Source: mark),
            (Path: BrandArtwork.LockupResourcePath, Source: lockup)
        })
        {
            using Stream resource = OpenPngResource(item.Path);
            var decoder = new PngBitmapDecoder(resource, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(decoder.Frames.Count == 1, "embedded PNG is a single image: " + item.Path);
            BitmapFrame decoded = decoder.Frames[0];
            Check(decoded.PixelWidth == item.Source.PixelWidth && decoded.PixelHeight == item.Source.PixelHeight,
                "factory dimensions match the embedded PNG: " + item.Path);
            Check(Pixels(decoded).SequenceEqual(Pixels(item.Source)), "factory pixels match the selected embedded asset: " + item.Path);
        }

        Image firstMark = BrandArtwork.Mark(), secondMark = BrandArtwork.Mark(24);
        Image firstLockup = BrandArtwork.Lockup(), secondLockup = BrandArtwork.Lockup(140);
        Check(!ReferenceEquals(firstMark, secondMark) && !ReferenceEquals(firstLockup, secondLockup),
            "each surface receives its own image control, not an already parented element");
        Check(ReferenceEquals(firstMark.Source, mark) && ReferenceEquals(secondMark.Source, mark), "symbol controls share frozen cached pixels");
        Check(ReferenceEquals(firstLockup.Source, lockup) && ReferenceEquals(secondLockup.Source, lockup), "lockup controls share frozen cached pixels");
        Check(firstMark.Width == 44 && secondMark.Width == 24 && firstLockup.Width == 164 && secondLockup.Width == 140,
            "factory sizing supports sidebar and compact overlay artwork");
        foreach (Image image in new[] { firstMark, secondMark, firstLockup, secondLockup })
            Check(!image.IsHitTestVisible && !image.Focusable, "decorative artwork cannot intercept pointer or keyboard input");
        Check((string?)firstMark.Tag == "SayWhat.Mark" && (string?)firstLockup.Tag == "SayWhat.Lockup",
            "artwork controls retain explicit branding identifiers");
        foreach (double invalidSize in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
        {
            Check(RejectsSize(() => BrandArtwork.Mark(invalidSize)), "symbol rejects invalid dimensions");
            Check(RejectsSize(() => BrandArtwork.Lockup(invalidSize)), "lockup rejects invalid dimensions");
        }

        using Stream firstIconStream = BrandArtwork.OpenIconResource();
        using Stream secondIconStream = BrandArtwork.OpenIconResource();
        Check(!ReferenceEquals(firstIconStream, secondIconStream) && firstIconStream.CanRead && secondIconStream.CanRead,
            "icon resource opens independent caller-owned streams");
        using var rawIcon = new MemoryStream();
        firstIconStream.CopyTo(rawIcon);
        byte[] bytes = rawIcon.ToArray();
        Check(bytes.Length >= 6 && bytes.Length <= 8 * 1024 * 1024, "embedded icon has bounded ICO bytes");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)) == 1, "embedded icon has an ICO directory header");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        Check(count == 9 && bytes.Length >= 6 + count * 16, "ICO contains all nine complete size entries");
        int[] sizes = new int[count];
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = bytes.AsSpan(6 + index * 16, 16);
            int width = entry[0] == 0 ? 256 : entry[0], height = entry[1] == 0 ? 256 : entry[1];
            sizes[index] = width;
            uint imageLength = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12]);
            uint imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]);
            Check(width == height && entry[3] == 0, "ICO directory entry is a valid square icon");
            Check(imageLength > 0 && imageOffset >= 6 + count * 16 && (ulong)imageOffset + imageLength <= (ulong)bytes.Length,
                "ICO image bytes are contained within their embedded resource");
            using var frameStream = new MemoryStream(bytes, checked((int)imageOffset), checked((int)imageLength), writable: false);
            var frameDecoder = new PngBitmapDecoder(frameStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(frameDecoder.Frames.Count == 1 && frameDecoder.Frames[0].PixelWidth == width && frameDecoder.Frames[0].PixelHeight == height,
                "individual PNG-encoded ICO payload decodes at its directory size");
        }
        int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        Check(sizes.Order().SequenceEqual(expectedSizes), "ICO provides the required standard sizes plus intermediate taskbar variants");
        using var decodeStream = new MemoryStream(bytes, writable: false);
        var icoDecoder = new IconBitmapDecoder(decodeStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check(icoDecoder.Frames.Select(frame => frame.PixelWidth).Order().SequenceEqual(expectedSizes), "every ICO directory entry decodes at its stated size");
        foreach (BitmapFrame frame in icoDecoder.Frames)
        {
            Check(frame.PixelWidth == frame.PixelHeight, "decoded ICO frame keeps square bounds");
            CheckAlpha(frame, Check);
        }

        // Construct owned Icon objects only: never create a tray or allocate a window.
        using System.Drawing.Icon retained = BrandArtwork.CreateTrayIcon();
        System.Drawing.Icon disposed = BrandArtwork.CreateTrayIcon();
        try { Check(!ReferenceEquals(retained, disposed), "tray factory returns independently owned icon clones"); }
        finally { disposed.Dispose(); }
        using var retainedBytes = new MemoryStream();
        retained.Save(retainedBytes);
        Check(retained.Size.Width == 32 && retained.Size.Height == 32 && retainedBytes.Length > 6,
            "disposing one tray clone leaves another valid 32-pixel icon and serializable");
        return checks;
    }

    internal static int CheckMainWindow(MainWindow window)
    {
        int checks = CheckWindowIcon(window);
        Image[] lockups = Nodes(window).OfType<Image>().Where(image => image.Name == "SidebarBrandLockup").ToArray();
        Require(lockups.Length == 1 && ReferenceEquals(lockups[0].Source, BrandArtwork.LockupSource),
            "dashboard sidebar shows exactly one selected branded lockup"); checks++;
        Require(!lockups[0].IsHitTestVisible && !lockups[0].Focusable, "sidebar lockup remains decorative"); checks++;
        return checks;
    }

    internal static int CheckHeroArtwork(MainWindow window)
    {
        Image[] marks = Nodes(window).OfType<Image>().Where(image => image.Name == "HeroBrandMark").ToArray();
        Require(marks.Length == 1 && ReferenceEquals(marks[0].Source, BrandArtwork.MarkSource), "home hero shows the selected branded symbol");
        Require(!marks[0].IsHitTestVisible && !marks[0].Focusable, "home hero symbol remains decorative");
        return 2;
    }

    internal static int CheckSubtitleWindow(SubtitleWindow window)
    {
        int checks = CheckWindowIcon(window);
        Image[] marks = Nodes(window).OfType<Image>().Where(image => image.Name == "SubtitleBrandMark").ToArray();
        Require(marks.Length == 1 && ReferenceEquals(marks[0].Source, BrandArtwork.MarkSource), "subtitle header uses the selected compact symbol"); checks++;
        Require(marks[0].Width <= 32 && marks[0].Width > 0 && !marks[0].IsHitTestVisible && !marks[0].Focusable,
            "subtitle symbol stays small and does not intercept controls"); checks++;
        Require(!window.IsLoaded && !window.IsVisible && !window.ServiceStopped.IsCompleted,
            "artwork inspection leaves subtitle capture and providers uninitialized"); checks++;
        return checks;
    }

    internal static int CheckWindowIcon(Window window)
    {
        Require(!window.IsLoaded && !window.IsVisible, "branded window inspection requires an unshown owner");
        Require(ReferenceEquals(window.Icon, BrandArtwork.WindowIcon) && window.Icon is { IsFrozen: true },
            "window uses the selected frozen cached icon, not a generic application icon");
        return 2;
    }

    private static Stream OpenPngResource(string path) => Application.GetResourceStream(new Uri("/" + typeof(BrandArtwork).Assembly.GetName().Name + ";component/" + path, UriKind.Relative))?.Stream
        ?? throw new InvalidOperationException("Embedded brand PNG is missing: " + path);
    private static bool RejectsSize(Action create)
    {
        try { create(); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }
    private static Color[] CornerPixels(BitmapSource source)
    {
        byte[] pixels = Pixels(source);
        int width = source.PixelWidth, height = source.PixelHeight;
        int[] offsets = [0, (width - 1) * 4, (height - 1) * width * 4, (height * width - 1) * 4];
        return offsets.Select(index => Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index])).ToArray();
    }
    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
    private static void CheckAlpha(BitmapSource source, Action<bool, string> check)
    {
        byte[] pixels = Pixels(source);
        bool transparent = false, visible = false;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            transparent |= pixels[index] == 0;
            visible |= pixels[index] > 0;
        }
        check(transparent && visible, "artwork has visible pixels and genuine transparent bounds");
    }
    private static void Require(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("Brand artwork surface check: " + description); }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out DependencyObject? node))
        {
            if (!seen.Add(node)) continue;
            yield return node;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject dependency) pending.Push(dependency);
            if (node is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) pending.Push(VisualTreeHelper.GetChild(node, index));
        }
    }
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

internal sealed class PersonPhotoException(string message, Exception? inner = null) : ArgumentException(message, inner);

// User photos are copied, stripped of metadata and reduced to a bounded local PNG. The
// database stores only our generated leaf name, never the original path or a remote URL.
internal static class PersonAvatar
{
    private const long MaximumInputBytes = 10 * 1024 * 1024;
    private const int MaximumDimension = 8192;
    private const long MaximumPixels = 16 * 1024 * 1024;
    internal const int PhotoSize = 512;
    internal const int SmallPhotoSize = 64;
    internal const int MaximumStoredBytes = 1024 * 1024;
    private const int MaximumPreviewDimension = 2048;
    private const int MaximumCachedPhotos = 128;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();

    internal static bool IsManagedFileName(string? name) => name is { Length: 36 } &&
        name.EndsWith(".png", StringComparison.Ordinal) && name[..32].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string PhotoDirectory(string dataRoot) => Path.Combine(Path.GetFullPath(dataRoot), "people-photos");

    internal static string Import(string sourcePath, string dataRoot)
    {
        BitmapSource picture = ReadPicture(sourcePath);
        double side = Math.Min(picture.PixelWidth, picture.PixelHeight);
        BitmapSource photo = RenderCrop(picture, new Rect((picture.PixelWidth - side) / 2, (picture.PixelHeight - side) / 2, side, side));
        return Store(photo, dataRoot);
    }

    internal static BitmapSource ReadPicture(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath) || sourcePath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new PersonPhotoException("Choose a PNG or JPEG picture saved on this computer.");
        string source = Path.GetFullPath(sourcePath);
        string extension = Path.GetExtension(source);
        if (!new[] { ".png", ".jpg", ".jpeg" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new PersonPhotoException("Choose a PNG or JPEG picture saved on this computer.");
        string? driveRoot = Path.GetPathRoot(source);
        if (string.IsNullOrEmpty(driveRoot) || new DriveInfo(driveRoot).DriveType == DriveType.Network)
            throw new PersonPhotoException("Choose a PNG or JPEG picture saved on this computer.");
        CheckAncestorChain(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length <= 0 || input.Length > MaximumInputBytes)
            throw new PersonPhotoException("Choose a picture smaller than 10 MB.");
        BitmapDecoder decoder;
        try { decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
        { throw new PersonPhotoException("This picture couldn't be opened. Choose another PNG or JPEG picture.", error); }
        if (decoder is not PngBitmapDecoder and not JpegBitmapDecoder || decoder.Frames.Count != 1)
            throw new PersonPhotoException("Choose a PNG or JPEG picture saved on this computer.");
        BitmapFrame frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > MaximumDimension ||
            frame.PixelHeight > MaximumDimension || (long)frame.PixelWidth * frame.PixelHeight > MaximumPixels)
            throw new PersonPhotoException("This picture is too large. Choose one with no more than 16 million pixels.");
        return SanitizeForEditing(Orient(frame));
    }

    // Phone JPEGs often store rotation as EXIF rather than rotated pixels. Honor it
    // before cropping, then discard the metadata together with the original image.
    private static BitmapSource Orient(BitmapFrame frame)
    {
        ushort orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is object value)
                orientation = Convert.ToUInt16(value, CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or FormatException or OverflowException or System.Runtime.InteropServices.COMException) { }
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new MatrixTransform(0, 1, 1, 0, 0, 0),
            6 => new RotateTransform(90),
            7 => new MatrixTransform(0, -1, -1, 0, 0, 0),
            8 => new RotateTransform(270),
            _ => null
        };
        return transform is null ? frame : new TransformedBitmap(frame, transform);
    }

    // Also used for an explicitly pasted clipboard bitmap. No clipboard polling, file-drop
    // paths, text or remote URLs are interpreted here. Limit pixels before copying them.
    internal static BitmapSource SanitizeForEditing(BitmapSource image)
    {
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0 || image.PixelWidth > MaximumDimension ||
            image.PixelHeight > MaximumDimension || (long)image.PixelWidth * image.PixelHeight > MaximumPixels)
            throw new PersonPhotoException("This picture is too large. Choose one with no more than 16 million pixels.");
        double scale = Math.Min(1, (double)MaximumPreviewDimension / Math.Max(image.PixelWidth, image.PixelHeight));
        int width = Math.Max(1, (int)Math.Round(image.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(image.PixelHeight * scale));
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawImage(image, new Rect(0, 0, width, height));
        }
        var preview = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        preview.Render(drawing); preview.Freeze(); return preview;
    }

    internal static BitmapSource RenderCrop(BitmapSource source, Rect crop, int size = PhotoSize)
    {
        if (size is < 1 or > PhotoSize || crop.IsEmpty || !double.IsFinite(crop.X + crop.Y + crop.Width + crop.Height) ||
            crop.Width <= 0 || Math.Abs(crop.Width - crop.Height) > .01 || crop.X < 0 || crop.Y < 0 ||
            crop.Right > source.PixelWidth + .01 || crop.Bottom > source.PixelHeight + .01)
            throw new ArgumentException("A profile picture needs a square crop inside the image.", nameof(crop));
        // Fresh pixels, fixed 96 DPI, no source metadata. Flatten transparency to white,
        // then encode 24-bit lossless PNG: even noisy 512px photos stay below the 1 MB cap.
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));
            double scale = size / crop.Width;
            context.DrawImage(source, new Rect(-crop.X * scale, -crop.Y * scale,
                source.PixelWidth * scale, source.PixelHeight * scale));
        }
        RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.HighQuality);
        var rendered = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); rendered.Render(drawing);
        var result = new FormatConvertedBitmap(rendered, PixelFormats.Bgr24, null, 0); result.Freeze(); return result;
    }

    internal static byte[] EncodePhoto(BitmapSource photo)
    {
        if (photo.PixelWidth != PhotoSize || photo.PixelHeight != PhotoSize)
            throw new ArgumentException("The saved profile picture must be 512 pixels square.", nameof(photo));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(photo));
        using var output = new MemoryStream(); encoder.Save(output);
        if (output.Length > MaximumStoredBytes) throw new PersonPhotoException("This picture couldn't be prepared. Choose another picture.");
        return output.ToArray();
    }

    private static string Store(BitmapSource photo, string dataRoot)
    {
        byte[] bytes = EncodePhoto(photo);
        string directory = PhotoDirectory(dataRoot);
        // Validate before creation: even a currently absent people-photos folder must
        // not be created through a redirected parent outside the user's data location.
        CheckAncestorChain(directory);
        Directory.CreateDirectory(directory);
        CheckAncestorChain(directory);
        string name = Guid.NewGuid().ToString("N") + ".png", destination = Path.Combine(directory, name);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(bytes);
            File.Move(temporary, destination, false);
            return name;
        }
        finally { if (File.Exists(temporary)) try { CheckAncestorChain(temporary); File.Delete(temporary); } catch { } }
    }

    // The existing provider's atomic photo import is path-based. Only an already-cropped,
    // sanitized photo is staged, on explicit Save; originals/clipboard images are never saved.
    internal static TemporaryPhoto StageEditedPhoto(BitmapSource photo)
    {
        byte[] bytes = EncodePhoto(photo);
        string directory = Path.Combine(Path.GetTempPath(), "SayWhat-photo-" + Guid.NewGuid().ToString("N"));
        CheckAncestorChain(directory); Directory.CreateDirectory(directory); CheckAncestorChain(directory);
        string path = Path.Combine(directory, "photo.png");
        try
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(bytes);
            return new TemporaryPhoto(path, directory);
        }
        catch
        {
            try { CheckAncestorChain(path); if (File.Exists(path)) File.Delete(path); } catch { }
            try { CheckAncestorChain(directory); Directory.Delete(directory, false); } catch { }
            throw;
        }
    }

    internal sealed class TemporaryPhoto(string path, string directory) : IDisposable
    {
        internal string Path { get; } = path;
        public void Dispose()
        {
            try { CheckAncestorChain(Path); if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            try { CheckAncestorChain(directory); Directory.Delete(directory, false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void DeleteManaged(string? name, string dataRoot)
    {
        if (!IsManagedFileName(name)) return;
        string directory = PhotoDirectory(dataRoot), path = Path.Combine(directory, name!);
        try
        {
            if (!Directory.Exists(directory)) return;
            CheckAncestorChain(directory);
            if (!File.Exists(path)) return;
            CheckAncestorChain(path);
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static BitmapSource? Load(SpeakerPerson? person, string? dataRoot = null, bool thumbnail = false)
    {
        if (!IsManagedFileName(person?.AvatarFileName)) return null;
        string directory = PhotoDirectory(dataRoot ?? AppPaths.Root), path = Path.Combine(directory, person!.AvatarFileName!);
        try
        {
            CheckAncestorChain(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumStoredBytes) return null;
            string key = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + thumbnail;
            lock (CacheGate) if (Cache.TryGetValue(key, out BitmapSource? cached)) return cached;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            if (decoder is not PngBitmapDecoder || decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth is <= 0 or > PhotoSize ||
                decoder.Frames[0].PixelHeight is <= 0 or > PhotoSize) return null;
            BitmapSource image;
            if (thumbnail)
            {
                BitmapSource frame = decoder.Frames[0];
                double side = Math.Min(frame.PixelWidth, frame.PixelHeight);
                image = RenderCrop(frame, new Rect((frame.PixelWidth - side) / 2, (frame.PixelHeight - side) / 2, side, side), SmallPhotoSize);
            }
            else { image = new WriteableBitmap(decoder.Frames[0]); image.Freeze(); }
            lock (CacheGate)
            {
                if (Cache.TryGetValue(key, out BitmapSource? cached)) return cached;
                while (Cache.Count >= MaximumCachedPhotos && CacheOrder.Count > 0) Cache.Remove(CacheOrder.Dequeue());
                Cache[key] = image; CacheOrder.Enqueue(key);
            }
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
        { return null; }
    }

    internal static FrameworkElement Create(SpeakerPerson? person, double size, Brush background, Brush foreground, string? fallbackName = null, string? dataRoot = null)
    {
        var content = new Grid { Width = size, Height = size, Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2) };
        content.Children.Add(new System.Windows.Shapes.Ellipse { Fill = background });
        BitmapSource? image = Load(person, dataRoot, thumbnail: size <= 48);
        if (image is not null) content.Children.Add(new Image { Source = image, Stretch = Stretch.UniformToFill });
        else if (person is not null) content.Children.Add(new Image { Source = GeneratedPersonAvatar.Resolve(person), Stretch = Stretch.UniformToFill });
        else content.Children.Add(new TextBlock { Text = Initials(fallbackName ?? ""), Foreground = foreground,
            FontSize = Math.Max(10, size * .34), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center });
        System.Windows.Automation.AutomationProperties.SetName(content, person?.Name ?? fallbackName ?? "Person");
        return content;
    }

    internal static string Initials(string name)
    {
        string[] words = name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        return string.Concat(words.Take(2).Select(word => StringInfo.GetNextTextElement(word))).ToUpperInvariant();
    }

    private static void CheckNotReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The picture storage location is not a regular local folder or file.");
    }

    private static void CheckAncestorChain(string path)
    {
        var chain = new Stack<string>();
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            chain.Push(current);
        // Check from the drive down, never accessing a child through an unvalidated link.
        foreach (string current in chain)
        {
            try { CheckNotReparse(current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

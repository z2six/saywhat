using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Synthetic pixels, injected clipboard/file actions, unshown windows and isolated files.
// Never reads the real clipboard, images, models, audio, or the user's profile database.
internal static class PersonPhotoEditorTests
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory); int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Person photo editor: " + detail); checks++; }
        void Reject<T>(Action action, string detail) where T : Exception
        { bool failed = false; try { action(); } catch (T) { failed = true; } Check(failed, detail); }
        BitmapSource source = Picture(640, 320);
        var crop = new PersonPhotoCrop(source);
        Check(crop.Bounds == new Rect(160, 0, 320, 320), "wide images begin with a centered square, never stretch");
        crop.SetZoom(2);
        Check(crop.Bounds == new Rect(240, 80, 160, 160), "zoom preserves the visible center");
        crop.MovePicture(320, 320, 320);
        Check(crop.Bounds == new Rect(80, 0, 160, 160), "drag pans and clamps without blank image borders");
        crop.MovePicture(10000, 10000, 320);
        Check(crop.Bounds.X == 0 && crop.Bounds.Y == 0, "large drag cannot leave the source image");
        crop.MovePicture(-10000, -10000, 320);
        Check(crop.Bounds.Right == 640 && crop.Bounds.Bottom == 320, "opposite drag clamps to the opposite edge");
        crop.SetZoom(double.NaN); crop.SetZoom(double.PositiveInfinity);
        Check(crop.Zoom == 2, "non-finite zoom is ignored");
        crop.SetZoom(99); Check(crop.Zoom == 4, "zoom has a bounded maximum");
        crop.SetZoom(-3); Check(crop.Zoom == 1, "zoom cannot reveal blanks outside the image");
        crop.Reset(); Check(crop.Bounds == new Rect(160, 0, 320, 320), "reset restores centered crop");
        var random = new Random(21871);
        foreach ((int width, int height) in new[] { (640, 320), (320, 640), (320, 320), (1, 320), (320, 1) })
        {
            var varied = new PersonPhotoCrop(Picture(width, height));
            for (int index = 0; index < 100; index++)
            {
                varied.SetZoom(random.NextDouble() * 10 - 3);
                varied.MovePicture(random.NextDouble() * 2000 - 1000, random.NextDouble() * 2000 - 1000, 320);
                Rect area = varied.Bounds;
                Check(area.X >= 0 && area.Y >= 0 && area.Right <= width + .001 && area.Bottom <= height + .001 &&
                    area.Width == area.Height && area.Width > 0, "every crop stays square and inside all aspect ratios");
            }
        }
        Reject<ArgumentException>(() => PersonAvatar.RenderCrop(source, new Rect(-1, 0, 100, 100)), "negative crop rejected");
        Reject<ArgumentException>(() => PersonAvatar.RenderCrop(source, new Rect(0, 0, 100, 90)), "non-square crop rejected");
        Reject<ArgumentException>(() => PersonAvatar.RenderCrop(source, new Rect(0, 0, 700, 700)), "out-of-range crop rejected");
        Reject<ArgumentException>(() => PersonAvatar.RenderCrop(source, new Rect(0, 0, 100, 100), 513), "unbounded output dimensions rejected");
        BitmapSource noise = Noise();
        BitmapSource full = PersonAvatar.RenderCrop(noise, new Rect(0, 0, 512, 512));
        byte[] encoded = PersonAvatar.EncodePhoto(full);
        Check(full is { PixelWidth: 512, PixelHeight: 512, IsFrozen: true } && full.Format == PixelFormats.Bgr24 && encoded.Length < PersonAvatar.MaximumStoredBytes,
            "even incompressible images are resized and losslessly compressed below one megabyte");
        using (var stream = new MemoryStream(encoded))
        {
            BitmapDecoder decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check(decoded is PngBitmapDecoder && decoded.Frames.Count == 1 && decoded.Frames[0].PixelWidth == 512 && Math.Abs(decoded.Frames[0].DpiX - 96) < .1,
                "stored files have a standard format and normalized size/DPI");
        }
        BitmapSource widePreview = PersonAvatar.SanitizeForEditing(Picture(2400, 1200));
        Check(widePreview is { PixelWidth: 2048, PixelHeight: 1024, IsFrozen: true }, "editing preview bounds memory without distorting aspect ratio");
        Reject<PersonPhotoException>(() => PersonAvatar.SanitizeForEditing(BitmapSource.Create(8193, 1, 96, 96, PixelFormats.Gray8, null, new byte[8193], 8193)),
            "clipboard input is validated before rendering");
        Reject<PersonPhotoException>(() => PersonAvatar.SanitizeForEditing(BitmapSource.Create(4097, 4097, 96, 96, PixelFormats.Gray8, null,
            new byte[4097 * 4097], 4097)), "clipboard pixel count is bounded independently of compressed bytes");

        string sourcePath = Path.Combine(directory, "chosen.png");
        using (var stream = File.Create(sourcePath))
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(stream); }
        byte[] unchanged = File.ReadAllBytes(sourcePath);
        string rotatedPath = Path.Combine(directory, "phone-orientation.jpg");
        using (var stream = File.Create(rotatedPath))
        {
            var metadata = new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null)); encoder.Save(stream);
        }
        Check(PersonAvatar.ReadPicture(rotatedPath) is { PixelWidth: 320, PixelHeight: 640 },
            "phone JPEG orientation is applied before cropping instead of saving a sideways photo");
        int clipboardReads = 0, pickerCalls = 0;
        var person = new SpeakerPerson("test-person", "Literal · Home · Mira", 0);
        var editor = new PersonPhotoEditor(null, person, () => { clipboardReads++; return source; }, () => { pickerCalls++; return sourcePath; });
        Check(clipboardReads == 0 && pickerCalls == 0 && !editor.SaveButton.IsEnabled && !editor.ResetButton.IsEnabled && editor.Result is null,
            "opening an editor cannot read the clipboard, open a picker or save an image");
        editor.PasteImage();
        Check(clipboardReads == 1 && editor.SaveButton.IsEnabled && editor.ZoomSlider.IsEnabled && editor.Result is null,
            "explicit paste reads only once and enables an unsaved crop");
        editor.ZoomSlider.Value = 2; editor.CropView.Crop!.MovePicture(75, 0, 320); editor.CropView.Redraw();
        Check(editor.CropView.Crop.Zoom == 2 && editor.CropView.Crop.Bounds.Width == 160, "the slider actually controls the saved crop");
        BitmapSource? ready = editor.PrepareResult();
        Check(ready is { PixelWidth: 512, PixelHeight: 512 } && editor.Result is null,
            "preparing a crop produces bounded pixels without accepting the dialog or writing files");
        editor.ResetCrop(); Check(editor.CropView.Crop.Zoom == 1 && editor.ZoomSlider.Value == 1, "reset keeps preview and zoom controls synchronized");
        editor.ChooseFile();
        Check(pickerCalls == 1 && clipboardReads == 1 && editor.Result is null && File.ReadAllBytes(sourcePath).SequenceEqual(unchanged),
            "choosing a file never copies or changes the original");
        editor.Close();
        Check(!Directory.Exists(PersonAvatar.PhotoDirectory(directory)), "cancelling/closing the editor leaves no saved or staged image");
        var emptyClipboard = new PersonPhotoEditor(null, person, () => null);
        emptyClipboard.PasteImage();
        Check(!emptyClipboard.SaveButton.IsEnabled && emptyClipboard.CropView.Crop is null, "empty/text-only clipboard cannot become a profile photo");
        emptyClipboard.Close();
        var busyClipboard = new PersonPhotoEditor(null, person, () => throw new System.Runtime.InteropServices.ExternalException("private details"));
        busyClipboard.PasteImage();
        Check(!busyClipboard.SaveButton.IsEnabled && !Nodes(busyClipboard).OfType<TextBlock>().Any(text => text.Text.Contains("private details", StringComparison.Ordinal)),
            "clipboard failure does not expose its contents or technical details");
        busyClipboard.Close();
        var badPicker = new PersonPhotoEditor(null, person, () => null, () => "https://example.invalid/picture.png");
        badPicker.ChooseFile(); Check(badPicker.CropView.Crop is null, "file selection never fetches network addresses"); badPicker.Close();

        string stagedPath, stagedDirectory;
        using (PersonAvatar.TemporaryPhoto staged = PersonAvatar.StageEditedPhoto(ready!))
        {
            stagedPath = staged.Path; stagedDirectory = Path.GetDirectoryName(staged.Path)!;
            Check(File.Exists(stagedPath) && new FileInfo(stagedPath).Length <= PersonAvatar.MaximumStoredBytes,
                "only a bounded sanitized crop is staged for the atomic provider");
            string imported = PersonAvatar.Import(stagedPath, directory);
            var saved = person with { AvatarFileName = imported };
            Check(PersonAvatar.Load(saved, directory) is { PixelWidth: 512, PixelHeight: 512 } &&
                PersonAvatar.Load(saved, directory, thumbnail: true) is { PixelWidth: 64, PixelHeight: 64 },
                "the saved crop supports separate full profile and subtitle images");
            PersonAvatar.DeleteManaged(imported, directory);
        }
        Check(!File.Exists(stagedPath) && !Directory.Exists(stagedDirectory), "success releases the temporary sanitized image and directory");
        try { using var staged = PersonAvatar.StageEditedPhoto(ready!); stagedPath = staged.Path; throw new IOException("synthetic provider failure"); }
        catch (IOException) { }
        Check(!File.Exists(stagedPath), "provider failure also releases the temporary image");

        string originalLanguage = UiText.Language; AppSettings originalAppearance = SettingsStore.Load().Copy();
        try
        {
            foreach (ThemePreset theme in AppearanceTheme.Presets)
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                AppSettings appearance = originalAppearance.Copy(); appearance.UiThemeId = theme.Id; appearance.CustomThemeImagePath = "";
                AppearanceTheme.ApplyForTests(appearance); UiText.Language = language;
                var localized = new PersonPhotoEditor(null, person, () => source); localized.PasteImage();
                Check(localized.Title == UiText.T("Edit person's photo") && AutomationProperties.GetName(localized.CropView) == UiText.T("Crop picture"),
                    "editor and keyboard crop are localized in " + theme.Id + "/" + language);
                Check(Nodes(localized).OfType<TextBlock>().Any(text => text.Text == person.Name), "person names remain literal in the photo editor");
                foreach (double width in new[] { 380d, 540d })
                {
                    var content = (FrameworkElement)localized.Content; content.Measure(new Size(width, 670)); content.Arrange(new Rect(0, 0, width, 670)); content.UpdateLayout();
                    MainWindow.ApplySlimScrollbars(content); content.UpdateLayout();
                    Check(Nodes(content).OfType<ScrollViewer>().All(scroll => scroll.ScrollableWidth < 1), "photo editing never requires horizontal scrolling");
                    Check(Nodes(content).OfType<Button>().All(button => button.ActualWidth <= width - 24), "translated crop controls fit a narrow window");
                }
                if (theme.Id == "coral-night" && language == "en" || theme.Id == "paper" && language == "ja")
                {
                    var content = (FrameworkElement)localized.Content;
                    content.Measure(new Size(540, 824)); content.Arrange(new Rect(0, 0, 540, 824)); content.UpdateLayout();
                    MainWindow.ApplySlimScrollbars(content); content.UpdateLayout();
                    var image = new RenderTargetBitmap(540, 824, 96, 96, PixelFormats.Pbgra32); image.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(directory, "photo-editor-" + theme.Id + "-" + language + ".png")); encoder.Save(output);
                }
                localized.Close();
            }
        }
        finally { UiText.Language = originalLanguage; AppearanceTheme.ApplyForTests(originalAppearance); }
        return checks;
    }

    private static BitmapSource Picture(int width, int height)
    {
        var pixels = new byte[checked(width * height * 3)];
        for (int row = 0; row < height; row++) for (int column = 0; column < width; column++)
        { int offset = (row * width + column) * 3; pixels[offset] = (byte)(column * 255 / Math.Max(1, width - 1)); pixels[offset + 1] = (byte)(row * 255 / Math.Max(1, height - 1)); pixels[offset + 2] = 180; }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3); image.Freeze(); return image;
    }
    private static BitmapSource Noise()
    {
        byte[] pixels = new byte[512 * 512 * 4]; new Random(4025).NextBytes(pixels);
        BitmapSource image = BitmapSource.Create(512, 512, 200, 200, PixelFormats.Bgra32, null, pixels, 512 * 4); image.Freeze(); return image;
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency) foreach (DependencyObject descendant in Nodes(dependency)) yield return descendant;
    }
}

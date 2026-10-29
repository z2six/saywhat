using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IncomingTranslator;

// Geometry is independent of input devices and files, so crop boundaries can be tested offline.
internal sealed class PersonPhotoCrop
{
    internal BitmapSource Image { get; }
    private double _centerX, _centerY;
    internal double Zoom { get; private set; } = 1;
    internal Rect Bounds
    {
        get
        {
            double side = Math.Min(Image.PixelWidth, Image.PixelHeight) / Zoom;
            return new Rect(Math.Clamp(_centerX - side / 2, 0, Image.PixelWidth - side),
                Math.Clamp(_centerY - side / 2, 0, Image.PixelHeight - side), side, side);
        }
    }
    internal PersonPhotoCrop(BitmapSource image)
    {
        Image = image; Reset();
    }
    internal void Reset() { Zoom = 1; _centerX = Image.PixelWidth / 2d; _centerY = Image.PixelHeight / 2d; }
    internal void SetZoom(double zoom)
    {
        if (!double.IsFinite(zoom)) return;
        Rect previous = Bounds; _centerX = previous.X + previous.Width / 2; _centerY = previous.Y + previous.Height / 2;
        Zoom = Math.Clamp(zoom, 1, 4); ConstrainCenter();
    }
    internal void MovePicture(double horizontal, double vertical, double viewportSize)
    {
        if (!double.IsFinite(horizontal + vertical + viewportSize) || viewportSize <= 0) return;
        double scale = Bounds.Width / viewportSize;
        _centerX -= horizontal * scale; _centerY -= vertical * scale; ConstrainCenter();
    }
    private void ConstrainCenter()
    {
        double half = Math.Min(Image.PixelWidth, Image.PixelHeight) / Zoom / 2;
        _centerX = Math.Clamp(_centerX, half, Image.PixelWidth - half);
        _centerY = Math.Clamp(_centerY, half, Image.PixelHeight - half);
    }
    internal BitmapSource Render(int size = PersonAvatar.PhotoSize) => PersonAvatar.RenderCrop(Image, Bounds, size);
}

internal sealed class PersonPhotoCropView : FrameworkElement
{
    internal PersonPhotoCrop? Crop { get; private set; }
    internal event Action? Changed;
    internal event Action<double>? ZoomRequested;
    private Point? _dragPoint;
    internal PersonPhotoCropView()
    {
        Name = "PersonPhotoCrop"; Width = Height = 320; Focusable = true; ClipToBounds = true;
        Cursor = Cursors.SizeAll;
        UiLocalization.Bind(this, AutomationProperties.NameProperty, "Crop picture");
        UiLocalization.Bind(this, FrameworkElement.ToolTipProperty, "Use arrow keys to move the picture; + and − change the zoom.");
        MouseLeftButtonDown += (_, e) => { if (Crop is null) return; Focus(); _dragPoint = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseMove += (_, e) =>
        {
            if (_dragPoint is not Point previous || !IsMouseCaptured || Crop is null) return;
            Point current = e.GetPosition(this); Crop.MovePicture(current.X - previous.X, current.Y - previous.Y, ActualWidth);
            _dragPoint = current; Redraw(); e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) => { if (_dragPoint is null) return; _dragPoint = null; ReleaseMouseCapture(); e.Handled = true; };
        LostMouseCapture += (_, _) => _dragPoint = null;
        MouseWheel += (_, e) => { if (Crop is null) return; ZoomRequested?.Invoke(Crop.Zoom + Math.Sign(e.Delta) * .15); e.Handled = true; };
        KeyDown += (_, e) =>
        {
            if (Crop is null) return;
            double move = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 20 : 5;
            switch (e.Key)
            {
                case Key.Left: Crop.MovePicture(-move, 0, ActualWidth); break;
                case Key.Right: Crop.MovePicture(move, 0, ActualWidth); break;
                case Key.Up: Crop.MovePicture(0, -move, ActualWidth); break;
                case Key.Down: Crop.MovePicture(0, move, ActualWidth); break;
                case Key.Add: case Key.OemPlus: ZoomRequested?.Invoke(Crop.Zoom + .1); break;
                case Key.Subtract: case Key.OemMinus: ZoomRequested?.Invoke(Crop.Zoom - .1); break;
                case Key.Home: Crop.Reset(); ZoomRequested?.Invoke(1); break;
                default: return;
            }
            Redraw(); e.Handled = true;
        };
    }
    internal void SetImage(BitmapSource image) { Crop = new PersonPhotoCrop(image); Redraw(); }
    internal void Redraw() { InvalidateVisual(); Changed?.Invoke(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(ModernTheme.Raised, null, new Rect(RenderSize));
        if (Crop is null) return;
        Rect crop = Crop.Bounds; double scale = ActualWidth / crop.Width;
        context.DrawImage(Crop.Image, new Rect(-crop.X * scale, -crop.Y * scale,
            Crop.Image.PixelWidth * scale, Crop.Image.PixelHeight * scale));
        // A square is saved; the circle shows how it will appear beside a name.
        var outline = new Pen(Brushes.White, 1.5) { DashStyle = DashStyles.Dash }; outline.Freeze();
        context.DrawEllipse(null, outline, new Point(ActualWidth / 2, ActualHeight / 2), Math.Max(0, ActualWidth / 2 - 2), Math.Max(0, ActualHeight / 2 - 2));
    }
}

// No image, clipboard or disk access occurs merely by constructing/opening this editor.
// Clipboard is read once, only after an explicit Paste/Control+V action. Cancel never saves.
internal sealed class PersonPhotoEditor : Window
{
    internal PersonPhotoCropView CropView { get; } = new();
    internal Slider ZoomSlider { get; } = new() { Name = "PersonPhotoZoom", Minimum = 1, Maximum = 4, Value = 1,
        TickFrequency = .1, SmallChange = .1, LargeChange = .5, IsSnapToTickEnabled = false, MinHeight = 34 };
    internal Button SaveButton { get; }
    internal Button ResetButton { get; }
    internal BitmapSource? Result { get; private set; }
    private readonly Image _preview = new() { Width = 96, Height = 96, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _status = ModernTheme.Label("", 12, ModernTheme.Warning);
    private readonly Func<BitmapSource?> _clipboardImage;
    private readonly Func<string?> _pickFile;
    private bool _settingImage;

    internal PersonPhotoEditor(Window? owner, SpeakerPerson person, Func<BitmapSource?>? clipboardImage = null, Func<string?>? pickFile = null)
    {
        _clipboardImage = clipboardImage ?? (() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null);
        _pickFile = pickFile ?? (() =>
        {
            var picker = new OpenFileDialog { Title = UiText.T("Choose a person's photo"),
                Filter = UiText.T("PNG or JPEG images") + " (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                CheckFileExists = true, Multiselect = false };
            return picker.ShowDialog(this) == true ? picker.FileName : null;
        });
        Name = "PersonPhotoEditor"; Owner = owner; Width = 540;
        MaxHeight = Math.Max(440, SystemParameters.WorkArea.Height - 24); Height = Math.Min(860, MaxHeight);
        MinWidth = 380; MinHeight = Math.Min(560, MaxHeight);
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = ModernTheme.Background; Foreground = ModernTheme.Text; Icon = BrandArtwork.WindowIcon;
        Topmost = false; ShowInTaskbar = false; UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic");
        UiLocalization.Bind(this, TitleProperty, "Edit person's photo");
        SourceInitialized += (_, _) => AppearanceTheme.ApplyWindowChrome(this);
        var root = new Grid { Background = ModernTheme.Background }; root.Children.Add(ThemeBackground.CreateLayer());
        var body = new StackPanel { Margin = new Thickness(24), Background = AppearanceTheme.Surface };
        body.Children.Add(ModernTheme.Heading("Edit person's photo", 25));
        body.Children.Add(ModernTheme.Label(person.Name, 17, localize: false));
        body.Children.Add(ModernTheme.Label("Choose a picture or paste one with Ctrl+V.", 13, ModernTheme.Muted));
        var input = new WrapPanel();
        var choose = ModernTheme.Button("Choose image…", (_, _) => ChooseFile()); choose.Name = "ChoosePersonPhoto"; input.Children.Add(choose);
        var paste = ModernTheme.Button("Paste image (Ctrl+V)", (_, _) => PasteImage()); paste.Name = "PastePersonPhoto"; input.Children.Add(paste);
        body.Children.Add(input);
        var viewport = new Viewbox { Child = CropView, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            MaxHeight = 320, Margin = new Thickness(0, 12, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(viewport);
        body.Children.Add(ModernTheme.Label("Drag the picture to position it. Use the zoom slider or mouse wheel to crop.", 12, ModernTheme.Muted));
        UiLocalization.Bind(ZoomSlider, AutomationProperties.NameProperty, "Zoom");
        ZoomSlider.IsEnabled = false;
        var cropActions = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        cropActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cropActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var controls = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        controls.Children.Add(ModernTheme.Label("Zoom", 12, ModernTheme.Muted)); controls.Children.Add(ZoomSlider);
        ResetButton = ModernTheme.Button("Reset crop", (_, _) => ResetCrop()); ResetButton.Name = "ResetPersonPhotoCrop"; ResetButton.IsEnabled = false;
        controls.Children.Add(ResetButton); cropActions.Children.Add(controls);
        var previewContainer = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var previewMask = new Grid { Width = 96, Height = 96, Clip = new EllipseGeometry(new Point(48, 48), 48, 48) };
        previewMask.Children.Add(_preview); previewContainer.Children.Add(previewMask);
        var previewLabel = ModernTheme.Label("Preview", 12, ModernTheme.Muted); previewLabel.HorizontalAlignment = HorizontalAlignment.Center;
        previewLabel.Margin = new Thickness(0, 5, 0, 0); previewContainer.Children.Add(previewLabel);
        Grid.SetColumn(previewContainer, 1); cropActions.Children.Add(previewContainer); body.Children.Add(cropActions);
        body.Children.Add(_status);
        body.Children.Add(ModernTheme.Label("Saved as a small picture on this device. The original is unchanged.", 12, ModernTheme.Muted));
        var footer = new WrapPanel { Margin = new Thickness(24, 10, 24, 18) };
        SaveButton = ModernTheme.Button("Save photo", (_, _) => SavePhoto(), true); SaveButton.Name = "SavePersonPhoto"; SaveButton.IsEnabled = false;
        footer.Children.Add(SaveButton);
        var cancel = ModernTheme.Button("Cancel", (_, _) => { Result = null; DialogResult = false; }); cancel.IsCancel = true; footer.Children.Add(cancel);
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        layout.Children.Add(scroll); Grid.SetRow(footer, 1); layout.Children.Add(footer); root.Children.Add(layout); Content = root;
        Loaded += (_, _) => MainWindow.ApplySlimScrollbars(root);
        CropView.Changed += UpdatePreview;
        CropView.ZoomRequested += value => ZoomSlider.Value = Math.Clamp(value, 1, 4);
        ZoomSlider.ValueChanged += (_, _) => { if (_settingImage || CropView.Crop is null) return; CropView.Crop.SetZoom(ZoomSlider.Value); CropView.Redraw(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { PasteImage(); e.Handled = true; }
        };
        UiLocalization.ApplyTree(this);
    }

    internal void ChooseFile()
    {
        try { string? source = _pickFile(); if (source is not null) SetImage(PersonAvatar.ReadPicture(source)); }
        catch (Exception error) { ShowError(error); }
    }
    internal void PasteImage()
    {
        try
        {
            BitmapSource? source = _clipboardImage();
            if (source is null) { UiLocalization.SetText(_status, "Copy an image first, then choose Paste image or press Ctrl+V."); return; }
            SetImage(PersonAvatar.SanitizeForEditing(source));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        { UiLocalization.SetText(_status, "The clipboard couldn't be opened. Try copying the image again."); }
        catch (Exception error) { ShowError(error); }
    }
    internal void SetImage(BitmapSource image)
    {
        _settingImage = true;
        try { CropView.SetImage(image); ZoomSlider.Value = 1; ZoomSlider.IsEnabled = SaveButton.IsEnabled = ResetButton.IsEnabled = true; UiLocalization.SetText(_status, ""); }
        finally { _settingImage = false; }
        UpdatePreview();
        if (IsVisible) CropView.Focus();
    }
    internal void ResetCrop()
    {
        if (CropView.Crop is null) return;
        CropView.Crop.Reset(); ZoomSlider.Value = 1; CropView.Redraw();
    }
    private void UpdatePreview()
    {
        if (CropView.Crop is not null) _preview.Source = CropView.Crop.Render(96);
    }
    internal BitmapSource? PrepareResult()
    {
        if (CropView.Crop is null) return null;
        BitmapSource prepared = CropView.Crop.Render();
        _ = PersonAvatar.EncodePhoto(prepared); // Verify the size/compression bound before closing.
        return prepared;
    }
    private void SavePhoto()
    {
        try { Result = PrepareResult(); if (Result is not null) DialogResult = true; }
        catch (Exception error) { ShowError(error); }
    }
    private void ShowError(Exception error)
    {
        // No paths, file contents or clipboard contents in the UI or diagnostics.
        string message = error is PersonPhotoException ? error.Message : "This picture couldn't be prepared. Choose another picture.";
        UiLocalization.SetText(_status, message);
    }
}

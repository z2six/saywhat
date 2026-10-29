using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;

namespace IncomingTranslator;

internal readonly record struct SubtitleActionAnchor(FrameworkElement Anchor, FrameworkElement Control);
internal readonly record struct SubtitleActionPlacement(FrameworkElement Control, Rect Bounds);

// Only the small per-message action rectangles exist in this window's native
// region. The text remains in a different, entirely click-through HWND.
internal sealed class SubtitleActionSurface : Window
{
    internal const long RequiredExtendedStyles = 0x00080000L | 0x00000080L; // LAYERED | TOOLWINDOW
    internal const long ForbiddenExtendedStyles = 0x00000020L | 0x08000000L | 0x00040000L;
    private const int ExtendedStyleIndex = -20;
    private const uint NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private readonly Window _toolbarOwner;
    private readonly SubtitleTextSurface _textSurface;
    private readonly Canvas _canvas;
    private readonly List<SubtitleActionAnchor> _actions = new();
    private readonly List<SubtitleActionPlacement> _placements = new();
    private bool _closed;
    internal bool NativeStylesApplied { get; private set; }
    internal bool HasVisibleActions { get; private set; }
    internal IReadOnlyList<FrameworkElement> ActionControls => _actions.Select(item => item.Control).ToArray();
    internal IReadOnlyList<SubtitleActionPlacement> Placements => _placements.ToArray();
    internal FrameworkElement PreviewRoot => _canvas;

    internal SubtitleActionSurface(Window toolbarOwner, SubtitleTextSurface textSurface)
    {
        ArgumentNullException.ThrowIfNull(toolbarOwner); ArgumentNullException.ThrowIfNull(textSurface);
        if (toolbarOwner.Dispatcher != Dispatcher || textSurface.Dispatcher != Dispatcher)
            throw new ArgumentException("Subtitle windows must use the same UI thread.");
        _toolbarOwner = toolbarOwner; _textSurface = textSurface;
        AllowsTransparency = true; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual; Focusable = false;
        Topmost = toolbarOwner.Topmost; FontFamily = toolbarOwner.FontFamily; Language = toolbarOwner.Language;
        FlowDirection = FlowDirection.LeftToRight; UseLayoutRounding = true; SnapsToDevicePixels = true;
        // Alpha zero pixels in a layered window pass mouse input through even
        // inside its region. Alpha one makes the whole masked button rectangle
        // clickable, including a button's transparent padding, without a visible
        // backdrop. Pixels outside the native region are absent altogether.
        var hitBackground = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); hitBackground.Freeze();
        Background = hitBackground;
        _canvas = new Canvas { Background = hitBackground, ClipToBounds = true };
        Content = _canvas;
        Resources.MergedDictionaries.Add(toolbarOwner.Resources);
        AttachOwnerIfReady();
        SourceInitialized += (_, _) =>
        {
            if (!SubtitleTextSurface.SupportsWindowsVersion(IncomingCaptureCapabilities.CurrentVersion))
                throw new PlatformNotSupportedException("Subtitle actions need Windows 10 version 1607 or newer.");
            AttachOwnerIfReady();
            nint handle = new WindowInteropHelper(this).Handle;
            ApplyNativeStyles(handle);
            // Never start as a broad transparent input surface, even if a caller
            // creates a handle before the first successful placement.
            ApplyRegion(handle, Array.Empty<Int32Rect>());
        };
        Closed += (_, _) =>
        {
            _closed = true; HasVisibleActions = false;
            _canvas.Children.Clear(); _actions.Clear(); _placements.Clear();
        };
    }

    internal static long ActionExtendedStyles(long existing) => (existing | RequiredExtendedStyles) & ~ForbiddenExtendedStyles;

    internal void SetActions(IEnumerable<SubtitleActionAnchor> actions)
    {
        Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(_closed, this); ArgumentNullException.ThrowIfNull(actions);
        SubtitleActionAnchor[] next = actions.ToArray();
        var controls = new HashSet<FrameworkElement>();
        foreach (SubtitleActionAnchor action in next)
        {
            ArgumentNullException.ThrowIfNull(action.Anchor); ArgumentNullException.ThrowIfNull(action.Control);
            if (action.Anchor.Dispatcher != Dispatcher || action.Control.Dispatcher != Dispatcher ||
                ReferenceEquals(action.Anchor, action.Control) || !controls.Add(action.Control))
                throw new ArgumentException("Each subtitle action needs a separate control on the same UI thread.", nameof(actions));
            DependencyObject? parent = VisualTreeHelper.GetParent(action.Control) ?? LogicalTreeHelper.GetParent(action.Control);
            if (parent is not null && !ReferenceEquals(parent, _canvas))
                throw new ArgumentException("Subtitle action controls cannot also belong to another display.", nameof(actions));
        }
        if (_actions.SequenceEqual(next)) return;
        // Keep surviving controls on the same parent. Moving a sibling window or
        // adding a sentence must not replace the owner of an open person picker.
        foreach (UIElement old in _canvas.Children.Cast<UIElement>().ToArray())
            if (old is not FrameworkElement element || !controls.Contains(element)) _canvas.Children.Remove(old);
        foreach (SubtitleActionAnchor action in next)
            if (!_canvas.Children.Contains(action.Control)) _canvas.Children.Add(action.Control);
        _actions.Clear(); _actions.AddRange(next); _placements.Clear();
        ClearExistingRegion();
    }

    // The same managed layout is available to inert previews/tests. It creates
    // no native handle and does not show or activate any window.
    internal void LayoutForPreview(Size viewportSize)
    {
        Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(_closed, this);
        if (!double.IsFinite(viewportSize.Width) || viewportSize.Width <= 0 ||
            !double.IsFinite(viewportSize.Height) || viewportSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportSize));
        _placements.Clear();
        var viewport = (FrameworkElement)_textSurface.Content;
        foreach (SubtitleActionAnchor action in _actions)
        {
            action.Control.Visibility = Visibility.Hidden;
            if (action.Anchor.Visibility != Visibility.Visible || action.Anchor.RenderSize.Width <= 0 || action.Anchor.RenderSize.Height <= 0) continue;
            Rect bounds;
            try { bounds = action.Anchor.TransformToAncestor(viewport).TransformBounds(new Rect(new Point(), action.Anchor.RenderSize)); }
            catch (InvalidOperationException) { continue; } // A just-retired bubble no longer belongs to the sibling tree.
            catch (ArgumentException) { continue; }
            if (!Finite(bounds) || bounds.Width <= 0 || bounds.Height <= 0) continue;
            action.Control.Width = bounds.Width; action.Control.Height = bounds.Height;
            Canvas.SetLeft(action.Control, bounds.Left); Canvas.SetTop(action.Control, bounds.Top);
            action.Control.Visibility = Visibility.Visible;
            _placements.Add(new(action.Control, bounds));
        }
        _canvas.Width = viewportSize.Width; _canvas.Height = viewportSize.Height;
        _canvas.Measure(viewportSize); _canvas.Arrange(new Rect(new Point(), viewportSize)); _canvas.UpdateLayout();
    }

    internal void SyncToTextSurface()
    {
        Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(_closed, this);
        nint sibling = new WindowInteropHelper(_textSurface).Handle;
        if (sibling == 0) { ClearExistingRegion(); return; }
        try
        {
            if (!GetWindowRect(sibling, out NativeRect bounds)) throw NativeFailure("Windows couldn't locate the subtitle display.");
            int width = Math.Max(0, bounds.Right - bounds.Left), height = Math.Max(0, bounds.Bottom - bounds.Top);
            if (width == 0 || height == 0) { ClearExistingRegion(); return; }
            uint dpi = GetDpiForWindow(sibling);
            double scale = dpi > 0 ? dpi / 96d : VisualTreeHelper.GetDpi(_textSurface).DpiScaleX;
            if (!double.IsFinite(scale) || scale <= 0) scale = 1;
            _textSurface.UpdateLayout();
            LayoutForPreview(new Size(width / scale, height / scale));
            IReadOnlyList<Int32Rect> rectangles = ClipRegions(_placements.Select(item => item.Bounds), width, height, scale);
            if (rectangles.Count == 0) { ClearExistingRegion(); return; }
            Width = width / scale; Height = height / scale; Topmost = _toolbarOwner.Topmost;
            AttachOwnerIfReady();
            nint handle = new WindowInteropHelper(this).EnsureHandle();
            if (!NativeStylesApplied) throw new InvalidOperationException("The subtitle actions could not be prepared.");
            if (!SetWindowPos(handle, Topmost ? new nint(-1) : new nint(-2), bounds.Left, bounds.Top, width, height, NoActivate | NoOwnerZOrder))
                throw NativeFailure("Windows couldn't position subtitle actions.");
            ApplyRegion(handle, rectangles);
            HasVisibleActions = true;
        }
        catch
        {
            // An old region must not remain at a newly moved position after a
            // partial failure. Empty is a real HRGN; NULL would restore a full
            // rectangular input surface.
            try { ClearExistingRegion(); } catch { if (IsVisible) Hide(); }
            throw;
        }
    }

    internal static IReadOnlyList<Int32Rect> ClipRegions(IEnumerable<Rect> boundsDip, int pixelWidth, int pixelHeight, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(boundsDip);
        if (pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth > 0x03ffffff || pixelHeight > 0x03ffffff ||
            !double.IsFinite(dpiScale) || dpiScale <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        var result = new List<Int32Rect>();
        foreach (Rect bounds in boundsDip)
        {
            if (bounds.IsEmpty) continue;
            if (!Finite(bounds)) throw new ArgumentOutOfRangeException(nameof(boundsDip));
            int left = (int)Math.Clamp(Math.Floor(bounds.Left * dpiScale), 0, pixelWidth);
            int top = (int)Math.Clamp(Math.Floor(bounds.Top * dpiScale), 0, pixelHeight);
            int right = (int)Math.Clamp(Math.Ceiling(bounds.Right * dpiScale), 0, pixelWidth);
            int bottom = (int)Math.Clamp(Math.Ceiling(bounds.Bottom * dpiScale), 0, pixelHeight);
            if (right > left && bottom > top) result.Add(new(left, top, right - left, bottom - top));
        }
        return result;
    }

    private void AttachOwnerIfReady()
    {
        if (Owner is null && new WindowInteropHelper(_toolbarOwner).Handle != 0) Owner = _toolbarOwner;
    }
    private void ClearExistingRegion()
    {
        HasVisibleActions = false;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) ApplyRegion(handle, Array.Empty<Int32Rect>());
    }
    private static void ApplyRegion(nint handle, IReadOnlyList<Int32Rect> rectangles)
    {
        using NativeRegion union = NativeRegion.Create(0, 0, 0, 0);
        foreach (Int32Rect rectangle in rectangles)
        {
            using NativeRegion part = NativeRegion.Create(rectangle.X, rectangle.Y, rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height);
            if (CombineRgn(union.DangerousGetHandle(), union.DangerousGetHandle(), part.DangerousGetHandle(), 2) == 0)
                throw NativeFailure("Windows couldn't combine subtitle action areas.");
        }
        if (SetWindowRgn(handle, union.DangerousGetHandle(), true) == 0)
            throw NativeFailure("Windows couldn't apply subtitle action areas.");
        // SetWindowRgn takes ownership only on success. The safe handle releases
        // every temporary/failure region and never deletes the transferred one.
        union.SetHandleAsInvalid();
    }
    private void ApplyNativeStyles(nint handle)
    {
        long desired = ActionExtendedStyles(SubtitleTextSurface.ReadExtendedStyles(handle));
        SetLastError(0);
        nint result = nint.Size == 8 ? SetWindowLongPtr(handle, ExtendedStyleIndex, new nint(desired))
            : new nint(SetWindowLong(handle, ExtendedStyleIndex, unchecked((int)desired)));
        int error = Marshal.GetLastWin32Error();
        if (result == 0 && error != 0) throw new Win32Exception(error, "Windows couldn't prepare subtitle actions.");
        if (!SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020 | NoActivate | NoOwnerZOrder))
            throw NativeFailure("Windows couldn't apply the subtitle action style.");
        long actual = SubtitleTextSurface.ReadExtendedStyles(handle);
        if ((actual & RequiredExtendedStyles) != RequiredExtendedStyles || (actual & ForbiddenExtendedStyles) != 0)
            throw new InvalidOperationException("Windows did not apply the subtitle action style.");
        NativeStylesApplied = true;
    }
    private static bool Finite(Rect bounds) => double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);
    private static Win32Exception NativeFailure(string message) => new(Marshal.GetLastWin32Error(), message);
    private sealed class NativeRegion : SafeHandleZeroOrMinusOneIsInvalid
    {
        private NativeRegion(nint handle) : base(true) => SetHandle(handle);
        internal static NativeRegion Create(int left, int top, int right, int bottom)
        {
            nint handle = CreateRectRgn(left, top, right, bottom);
            if (handle == 0) throw NativeFailure("Windows couldn't create a subtitle action area.");
            return new(handle);
        }
        protected override bool ReleaseHandle() => DeleteObject(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}

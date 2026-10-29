using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace IncomingTranslator;

internal readonly record struct SubtitleSurfacePlacement(int X, int Y, int Width, int Height);

// A passive text-only HWND. The separate owner retains all interactive controls.
// Layered + transparent is intentional: unlike HTTRANSPARENT alone, this passes
// mouse input through to windows belonging to other applications as well.
// https://learn.microsoft.com/windows/win32/winmsg/window-features#layered-windows
internal sealed class SubtitleTextSurface : Window
{
    internal const long RequiredExtendedStyles = 0x00080000L | 0x00000020L | 0x08000000L | 0x00000080L;
    private const long AppWindowStyle = 0x00040000L;
    private const int ExtendedStyleIndex = -20;
    private const uint NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private readonly Window _toolbarOwner;
    private readonly FrameworkElement _textContent;
    private readonly Border _viewport;
    internal bool NativeStylesApplied { get; private set; }

    internal SubtitleTextSurface(Window owner, FrameworkElement content)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(content);
        if (owner.Dispatcher != Dispatcher || content.Dispatcher != Dispatcher)
            throw new ArgumentException("The subtitle display and toolbar must use the same UI thread.");
        _toolbarOwner = owner; _textContent = content;
        // The viewport can be shorter than the three retained bubbles. WPF
        // arranges oversized bottom-aligned content upward, so clipping removes
        // older text rather than hiding the newest sentence at the bottom.
        content.VerticalAlignment = VerticalAlignment.Bottom;
        AllowsTransparency = true; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = false; Topmost = owner.Topmost; FontFamily = owner.FontFamily; Language = owner.Language;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        _viewport = new Border { Child = content, Background = Brushes.Transparent, ClipToBounds = true, IsHitTestVisible = false };
        Content = _viewport;
        // WPF forbids assigning Owner until that window has a native handle.
        // Construction must remain inert for previews and never create/show it.
        AttachOwnerIfReady();
        SourceInitialized += (_, _) =>
        {
            if (!SupportsWindowsVersion(IncomingCaptureCapabilities.CurrentVersion))
                throw new PlatformNotSupportedException("The passive subtitle display needs Windows 10 version 1607 or newer.");
            AttachOwnerIfReady();
            ApplyNativeStyles(new WindowInteropHelper(this).Handle);
        };
    }

    internal static bool SupportsWindowsVersion(Version version) => version >= new Version(10, 0, 14393);
    internal static long PassiveExtendedStyles(long existing) => (existing | RequiredExtendedStyles) & ~AppWindowStyle;

    private void AttachOwnerIfReady()
    {
        if (Owner is null && new WindowInteropHelper(_toolbarOwner).Handle != 0) Owner = _toolbarOwner;
    }

    internal void PlaceRelativeToOwner(double width)
    {
        Dispatcher.VerifyAccess();
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        nint ownerHandle = new WindowInteropHelper(_toolbarOwner).Handle;
        if (ownerHandle == 0) return; // The caller waits for its toolbar to load.
        if (!GetWindowRect(ownerHandle, out NativeRect ownerBounds)) throw NativeFailure("Windows couldn't locate the subtitle toolbar.");
        nint monitor = MonitorFromWindow(ownerHandle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw NativeFailure("Windows couldn't locate the subtitle display area.");
        uint dpi = GetDpiForWindow(ownerHandle);
        double scale = dpi > 0 ? dpi / 96d : VisualTreeHelper.GetDpi(_toolbarOwner).DpiScaleX;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        Rect work = info.Work.ToRect();
        double widthDip = Math.Min(width, work.Width / scale);
        _textContent.Measure(new Size(widthDip, double.PositiveInfinity));
        double desiredHeight = Math.Min(MaxHeight, Math.Max(1, _textContent.DesiredSize.Height));
        SubtitleSurfacePlacement placement = CalculatePlacement(ownerBounds.ToRect(), work, widthDip, desiredHeight, scale);
        Width = placement.Width / scale;
        // A wrapper bounds SizeToContent without overwriting the user's chosen
        // MaxHeight. Moving the toolbar back into space can expand the text again.
        _viewport.MaxHeight = placement.Height / scale;
        Topmost = _toolbarOwner.Topmost;
        AttachOwnerIfReady();
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        if (!NativeStylesApplied) throw new InvalidOperationException("The subtitle display could not be made click-through.");
        if (!SetWindowPos(handle, Topmost ? new nint(-1) : new nint(-2), placement.X, placement.Y,
            placement.Width, placement.Height, NoActivate | NoOwnerZOrder))
            throw NativeFailure("Windows couldn't position the subtitle display.");
    }

    // Native pixels are used end-to-end, including monitors with negative origins
    // and different scaling. WPF's global Left/Top conversion is not used here.
    internal static SubtitleSurfacePlacement CalculatePlacement(Rect ownerPixels, Rect workPixels,
        double widthDip, double heightDip, double dpiScale)
    {
        if (ownerPixels.IsEmpty || workPixels.IsEmpty || workPixels.Width < 1 || workPixels.Height < 1 ||
            !Finite(ownerPixels) || !Finite(workPixels) || !double.IsFinite(widthDip) || widthDip <= 0 ||
            !double.IsFinite(heightDip) || heightDip < 0 || !double.IsFinite(dpiScale) || dpiScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(workPixels), "Subtitle placement requires finite display bounds and sizes.");
        int left = checked((int)Math.Ceiling(workPixels.Left)), top = checked((int)Math.Ceiling(workPixels.Top));
        int right = checked((int)Math.Floor(workPixels.Right)), bottom = checked((int)Math.Floor(workPixels.Bottom));
        int width = checked((int)Math.Clamp(Math.Ceiling(widthDip * dpiScale), 1, right - left));
        int height = checked((int)Math.Clamp(Math.Ceiling(heightDip * dpiScale), 1, bottom - top));
        int gap = checked((int)Math.Ceiling(6 * dpiScale));
        double below = ownerPixels.Bottom + gap, above = ownerPixels.Top - gap - height;
        double y;
        if (below >= top && below + height <= bottom) y = below;
        else if (above >= top && above + height <= bottom) y = above;
        else
        {
            double lowerSpace = Math.Max(0, bottom - Math.Max(top, below));
            double upperSpace = Math.Max(0, Math.Min(bottom, ownerPixels.Top - gap) - top);
            double space = Math.Max(lowerSpace, upperSpace);
            if (space >= 1)
            {
                height = Math.Min(height, checked((int)Math.Floor(space)));
                y = lowerSpace >= upperSpace ? below : ownerPixels.Top - gap - height;
            }
            else y = top; // An oversized/off-screen toolbar leaves no separate side.
        }
        int x = checked((int)Math.Clamp(Math.Round(ownerPixels.Left), left, right - width));
        int clampedY = checked((int)Math.Clamp(Math.Round(y), top, bottom - height));
        return new(x, clampedY, width, height);
    }

    private static bool Finite(Rect rect) => double.IsFinite(rect.Left) && double.IsFinite(rect.Top) &&
        double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);

    private void ApplyNativeStyles(nint handle)
    {
        long previous = ReadExtendedStyles(handle);
        long desired = PassiveExtendedStyles(previous);
        SetLastError(0);
        nint result = nint.Size == 8 ? SetWindowLongPtr(handle, ExtendedStyleIndex, new nint(desired))
            : new nint(SetWindowLong(handle, ExtendedStyleIndex, unchecked((int)desired)));
        int error = Marshal.GetLastWin32Error();
        if (result == 0 && error != 0) throw new Win32Exception(error, "Windows couldn't make the subtitle display click-through.");
        if (!SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020 | NoActivate | NoOwnerZOrder))
            throw NativeFailure("Windows couldn't apply the subtitle display style.");
        long actual = ReadExtendedStyles(handle);
        if ((actual & RequiredExtendedStyles) != RequiredExtendedStyles || (actual & AppWindowStyle) != 0)
            throw new InvalidOperationException("Windows did not apply the click-through subtitle display style.");
        NativeStylesApplied = true;
    }

    internal static long ReadExtendedStyles(nint handle)
    {
        SetLastError(0);
        nint result = nint.Size == 8 ? GetWindowLongPtr(handle, ExtendedStyleIndex) : new nint(GetWindowLong(handle, ExtendedStyleIndex));
        int error = Marshal.GetLastWin32Error();
        if (result == 0 && error != 0) throw new Win32Exception(error, "Windows couldn't read the subtitle display style.");
        return result.ToInt64();
    }

    private static Win32Exception NativeFailure(string message) => new(Marshal.GetLastWin32Error(), message);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top)); }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

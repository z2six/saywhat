using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IncomingTranslator;

internal static class SubtitleWindowPlacement
{
    internal static Size SafeRestoredSize(Size requested, Size fallback)
    {
        static bool Valid(Size value) => !value.IsEmpty && double.IsFinite(value.Width) && double.IsFinite(value.Height)
            && value.Width > 0 && value.Height > 0;
        Size selected = Valid(requested) ? requested : Valid(fallback) ? fallback : new Size(960, 430);
        return new Size(Math.Max(560, selected.Width), Math.Max(260, selected.Height));
    }

    // All arguments use the SAME native screen-coordinate space. Never assign a physical
    // screen point directly to WPF Left/Top: the monitor may have a different DPI or origin.
    internal static Point RestoredDragPosition(Point cursorScreen, Size restoredWindowSize,
        double horizontalGrabRatio, double headerGrabOffset, Rect workArea)
    {
        if (workArea.IsEmpty || !double.IsFinite(workArea.Left) || !double.IsFinite(workArea.Top)
            || !double.IsFinite(workArea.Right) || !double.IsFinite(workArea.Bottom)
            || workArea.Width <= 0 || workArea.Height <= 0) workArea = new Rect(0, 0, 1920, 1080);
        if (restoredWindowSize.IsEmpty || !double.IsFinite(restoredWindowSize.Width) || !double.IsFinite(restoredWindowSize.Height)
            || restoredWindowSize.Width <= 0 || restoredWindowSize.Height <= 0) restoredWindowSize = new Size(960, 430);
        double ratio = double.IsFinite(horizontalGrabRatio) ? Math.Clamp(horizontalGrabRatio, 0, 1) : .5;
        double offset = double.IsFinite(headerGrabOffset) ? Math.Clamp(headerGrabOffset, 0, restoredWindowSize.Height) : 24;
        double cursorX = double.IsFinite(cursorScreen.X) ? cursorScreen.X : workArea.Left + workArea.Width / 2;
        double cursorY = double.IsFinite(cursorScreen.Y) ? cursorScreen.Y : workArea.Top + workArea.Height / 2;
        double visibleHeader = Math.Min(64, Math.Min(workArea.Width, workArea.Height));
        double minimumLeft = restoredWindowSize.Width <= workArea.Width ? workArea.Left
            : workArea.Left - restoredWindowSize.Width + visibleHeader;
        double maximumLeft = restoredWindowSize.Width <= workArea.Width ? workArea.Right - restoredWindowSize.Width
            : workArea.Right - visibleHeader;
        double maximumTop = restoredWindowSize.Height <= workArea.Height ? workArea.Bottom - restoredWindowSize.Height
            : workArea.Bottom - visibleHeader;
        return new Point(Math.Clamp(cursorX - restoredWindowSize.Width * ratio, minimumLeft, maximumLeft),
            Math.Clamp(cursorY - offset, workArea.Top, maximumTop));
    }

    internal static bool RestoreUnderPointer(Window window, double horizontalGrabRatio, double headerGrabOffsetDip)
    {
        Rect previous = window.RestoreBounds;
        Size restored = SafeRestoredSize(previous.IsEmpty ? Size.Empty : previous.Size, new Size(window.Width, window.Height));
        nint handle = new WindowInteropHelper(window).Handle; // Never creates/shows an inert test window.
        bool hasPointer = GetCursorPos(out NativePoint pointer);
        window.WindowState = WindowState.Normal;
        if (previous.IsEmpty || !double.IsFinite(previous.Width) || !double.IsFinite(previous.Height)
            || previous.Width <= 0 || previous.Height <= 0)
        { window.Width = restored.Width; window.Height = restored.Height; }
        if (handle == 0 || !hasPointer) return false;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(pointer, 2), ref monitor)) return false;
        Rect workArea = new(monitor.Work.Left, monitor.Work.Top,
            monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
        // Moving between differently scaled monitors can synchronously resize the HWND. Read
        // its resulting native size and DPI again rather than guessing a global WPF scale.
        for (int pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(handle, out NativeRect bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
            uint dpi = GetDpiForWindow(handle);
            double scale = dpi == 0 ? 1 : dpi / 96d;
            Point position = RestoredDragPosition(new Point(pointer.X, pointer.Y),
                new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), horizontalGrabRatio, headerGrabOffsetDip * scale, workArea);
            static int Coordinate(double value) => (int)Math.Round(Math.Clamp(value, int.MinValue + 32768d, int.MaxValue - 32768d));
            if (!SetWindowPos(handle, 0, Coordinate(position.X), Coordinate(position.Y), 0, 0, 0x0001 | 0x0004 | 0x0010)) return false;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}

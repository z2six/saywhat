using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace IncomingTranslator;

internal static class SubtitleTextSurfaceTests
{
    internal static int Run(bool nativeSmoke = true)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Passive subtitle surface: " + detail); checks++; }
        foreach (int build in new[] { 14392, 14393, 19044, 19045, 20348, 22000 })
            Check(SubtitleTextSurface.SupportsWindowsVersion(new Version(10, 0, build)) == (build >= 14393), "DPI API version boundary: " + build);
        const long unrelated = 0x00000100;
        long styles = SubtitleTextSurface.PassiveExtendedStyles(unrelated | 0x00040000);
        Check((styles & SubtitleTextSurface.RequiredExtendedStyles) == SubtitleTextSurface.RequiredExtendedStyles &&
            (styles & 0x00040000) == 0 && (styles & unrelated) != 0, "required layered/transparent/noactivate/toolwindow flags preserve unrelated bits and remove appwindow");
        var work = new Rect(0, 0, 1920, 1040);
        var below = SubtitleTextSurface.CalculatePlacement(new Rect(100, 100, 420, 70), work, 420, 200, 1);
        Check(below == new SubtitleSurfacePlacement(100, 176, 420, 200), "text is below the toolbar when there is room");
        var above = SubtitleTextSurface.CalculatePlacement(new Rect(1700, 960, 420, 70), work, 420, 200, 1);
        Check(above == new SubtitleSurfacePlacement(1500, 754, 420, 200), "near the bottom, text fits above and clamps horizontally");
        var negative = SubtitleTextSurface.CalculatePlacement(new Rect(-200, -100, 300, 80), new Rect(-1920, -1080, 1920, 1040), 420, 240, 1.5);
        Check(negative == new SubtitleSurfacePlacement(-630, -469, 630, 360), "negative monitor origins and 150% DPI stay in native pixels");
        var clipped = SubtitleTextSurface.CalculatePlacement(new Rect(10, 200, 100, 70), new Rect(0, 0, 300, 500), 420, 900, 1);
        Check(clipped.Width == 300 && clipped.Height == 224 && clipped.Y == 276, "oversized content uses the roomier side without leaving the monitor");
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        foreach (Rect monitor in new[] { work, new Rect(-2560, 0, 2560, 1400), new Rect(1920, -300, 2560, 1400) })
        foreach (Rect owner in new[] { new Rect(monitor.Left - 100, monitor.Top + 20, 420, 70), new Rect(monitor.Right - 100, monitor.Bottom - 80, 420, 70), monitor })
        {
            var placement = SubtitleTextSurface.CalculatePlacement(owner, monitor, 420, 2000, scale);
            Check(monitor.Contains(new Rect(placement.X, placement.Y, placement.Width, placement.Height)), "all placement combinations remain inside their monitor work area");
        }
        bool invalid = false;
        try { SubtitleTextSurface.CalculatePlacement(new Rect(0, 0, 100, 50), work, double.NaN, 100, 1); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "invalid dimensions fail before any native positioning");
        checks += CheckNewestOverflow();
        var toolbar = new Window { Width = 420, Height = 70, ShowActivated = false, ShowInTaskbar = false };
        var text = new TextBlock { Text = "Synthetic subtitle text", Foreground = Brushes.White, FontSize = 16 };
        var surface = new SubtitleTextSurface(toolbar, text) { MaxHeight = 300 };
        try
        {
            Check(!toolbar.IsVisible && !surface.IsVisible && new WindowInteropHelper(surface).Handle == 0 &&
                surface.AllowsTransparency && surface.WindowStyle == WindowStyle.None && surface.ResizeMode == ResizeMode.NoResize &&
                !surface.ShowInTaskbar && !surface.ShowActivated && !surface.Focusable && surface.SizeToContent == SizeToContent.Height &&
                surface.Background == Brushes.Transparent, "construction is inert, transparent and noninteractive");
            if (nativeSmoke && OperatingSystem.IsWindows() && SubtitleTextSurface.SupportsWindowsVersion(IncomingCaptureCapabilities.CurrentVersion))
            {
                nint active = GetForegroundWindow();
                nint ownerHandle = new WindowInteropHelper(toolbar).EnsureHandle();
                nint handle = new WindowInteropHelper(surface).EnsureHandle();
                Check(surface.NativeStylesApplied && (SubtitleTextSurface.ReadExtendedStyles(handle) & SubtitleTextSurface.RequiredExtendedStyles) == SubtitleTextSurface.RequiredExtendedStyles,
                    "the real, never-shown HWND receives every passive native flag");
                surface.PlaceRelativeToOwner(420);
                Check(!IsWindowVisible(ownerHandle) && !IsWindowVisible(handle) && !toolbar.IsVisible && !surface.IsVisible && GetForegroundWindow() == active,
                    "EnsureHandle and placement never show or activate either test window");
                Check(ReferenceEquals(surface.Owner, toolbar) && surface.Topmost == toolbar.Topmost && surface.MaxHeight == 300,
                    "deferred owner attachment preserves toolbar topmost and the user height preference");
            }
            return checks;
        }
        finally { surface.Close(); toolbar.Close(); }
    }

    private static int CheckNewestOverflow()
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Passive subtitle overflow: " + detail); checks++; }
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false };
        var rows = new StackPanel();
        TextBlock latest = null!;
        foreach (string caption in new[] { "Oldest sentence", "Previous sentence", "Newest sentence" })
        {
            var text = new TextBlock { Text = string.Join(Environment.NewLine, Enumerable.Repeat(caption, 5)),
                FontSize = 30, LineHeight = 40, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
            rows.Children.Add(new Border { Child = text, Padding = new Thickness(10), Background = Brushes.Black });
            latest = text;
        }
        var surface = new SubtitleTextSurface(owner, rows) { Width = 420, MaxHeight = 440 };
        var viewport = (Border)surface.Content;
        try
        {
            foreach (double height in new[] { 440d, 240, 120 })
            {
                viewport.MaxHeight = height;
                viewport.Measure(new Size(420, height));
                viewport.Arrange(new Rect(0, 0, 420, height)); viewport.UpdateLayout();
                Rect latestBounds = latest.TransformToAncestor(viewport).TransformBounds(new Rect(new Point(), latest.RenderSize));
                Point offset = rows.TranslatePoint(new Point(), viewport);
                Check(rows.ActualHeight > height && offset.Y < 0 && Math.Abs(offset.Y + rows.ActualHeight - height) < .5,
                    "a taller bubble stack moves upward and retains its bottom at viewport height " + height);
                Check(latestBounds.Bottom <= height + .5 && latestBounds.Bottom >= height - 11 &&
                    latestBounds.IntersectsWith(new Rect(0, 0, 420, height)),
                    "the newest text remains visible instead of the oldest text at viewport height " + height);
                if (height >= 240) Check(latestBounds.Top >= 0,
                    "the complete newest large-font sentence fits when there is enough room for it");
            }
            viewport.MaxHeight = 1000;
            viewport.Measure(new Size(420, double.PositiveInfinity));
            double fullHeight = viewport.DesiredSize.Height;
            viewport.Arrange(new Rect(0, 0, 420, fullHeight)); viewport.UpdateLayout();
            Check(Math.Abs(rows.TranslatePoint(new Point(), viewport).Y) < .5 && Math.Abs(rows.ActualHeight - fullHeight) < .5,
                "restoring enough display space exposes the full stack again without retaining the earlier clip");
            Check(!surface.IsVisible && !owner.IsVisible && !surface.IsLoaded &&
                new WindowInteropHelper(surface).Handle == 0 && new WindowInteropHelper(owner).Handle == 0,
                "overflow layout checks never create, display or activate a window");
            return checks;
        }
        finally { surface.Close(); owner.Close(); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}

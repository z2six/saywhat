using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace IncomingTranslator;

// Pure geometry, unshown controls and optional never-visible native handles.
// No mouse/keyboard input, capture, models, clipboard or network is used.
internal static class SubtitleActionSurfaceTests
{
    internal static int Run(bool nativeSmoke = true)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Subtitle action surface: " + detail); checks++; }
        const long unrelated = 0x100;
        long styles = SubtitleActionSurface.ActionExtendedStyles(unrelated | SubtitleActionSurface.ForbiddenExtendedStyles);
        Check((styles & SubtitleActionSurface.RequiredExtendedStyles) == SubtitleActionSurface.RequiredExtendedStyles &&
            (styles & SubtitleActionSurface.ForbiddenExtendedStyles) == 0 && (styles & unrelated) != 0,
            "actions use layered/toolwindow styles but permit intentional activation and ordinary control input");
        var clipped = SubtitleActionSurface.ClipRegions([new(-10, -5, 50, 30), new(380, 80, 100, 40), new(0, 200, 100, 30)], 400, 100, 1);
        Check(clipped.SequenceEqual(new[] { new Int32Rect(0, 0, 40, 25), new Int32Rect(380, 80, 20, 20) }),
            "action regions intersect the visible sibling viewport and omit fully clipped headers");
        Check(SubtitleActionSurface.ClipRegions([new(10.2, 20.1, 30.4, 10.2)], 600, 300, 1.5)
            .SequenceEqual(new[] { new Int32Rect(15, 30, 46, 16) }), "fractional DIP bounds round outward at 150% DPI");
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2 })
        {
            IReadOnlyList<Int32Rect> regions = SubtitleActionSurface.ClipRegions(
                [new(-50, -30, 90, 80), new(10, 30, 120, 28), new(300, 150, 200, 80), Rect.Empty], 420, 200, dpi);
            Check(regions.All(rect => rect.Width > 0 && rect.Height > 0 && rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= 420 && rect.Y + rect.Height <= 200),
                "all actionable pixels remain within actual native bounds at DPI scale " + dpi);
        }
        bool invalid = false;
        try { SubtitleActionSurface.ClipRegions([new(double.NaN, 0, 10, 10)], 420, 100, 1); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "nonfinite geometry is rejected before native region creation");

        var owner = new Window { Width = 420, Height = 38, ShowActivated = false, ShowInTaskbar = false };
        var body = new Canvas { Width = 420, Height = 300 };
        var anchors = new List<Border>();
        foreach (double top in new[] { 10d, 120, 260 })
        {
            var anchor = new Border { Width = 120, Height = 26 };
            Canvas.SetLeft(anchor, 280); Canvas.SetTop(anchor, top); body.Children.Add(anchor); anchors.Add(anchor);
        }
        var text = new SubtitleTextSurface(owner, body) { MaxHeight = 160 };
        var action = new SubtitleActionSurface(owner, text);
        var controls = anchors.Select((_, index) => new Button { Content = "Synthetic action " + index, Background = Brushes.Transparent }).ToArray();
        SubtitleActionAnchor[] pairs = anchors.Select((anchor, index) => new SubtitleActionAnchor(anchor, controls[index])).ToArray();
        var viewport = (FrameworkElement)text.Content;
        void LayoutText(double height)
        {
            viewport.Measure(new Size(420, height)); viewport.Arrange(new Rect(0, 0, 420, height)); viewport.UpdateLayout();
        }
        try
        {
            LayoutText(160); action.SetActions(pairs); action.LayoutForPreview(new Size(420, 160));
            Check(action.ActionControls.SequenceEqual(controls) && action.Placements.Count == 3 &&
                action.Placements[0].Bounds == new Rect(280, 10, 120, 26),
                "unshown layout places each control exactly over its reserved passive anchor");
            IReadOnlyList<Int32Rect> previewRegions = SubtitleActionSurface.ClipRegions(action.Placements.Select(item => item.Bounds), 420, 160, 1);
            Check(previewRegions.Count == 2 && previewRegions.All(rect => rect.X == 280 && rect.Width == 120),
                "only visible right-side action anchors are interactive; text on the left stays outside every region");
            DependencyObject stableParent = VisualTreeHelper.GetParent(controls[0]);
            action.SetActions([pairs[0], pairs[1]]); action.LayoutForPreview(new Size(420, 160));
            Check(ReferenceEquals(stableParent, VisualTreeHelper.GetParent(controls[0])) &&
                ReferenceEquals(stableParent, VisualTreeHelper.GetParent(controls[1])) && VisualTreeHelper.GetParent(controls[2]) is null,
                "removing a sibling action preserves surviving control parents and an existing picker's owner");
            Check(((SolidColorBrush)action.Background).Color.A == 1 && action.PreviewRoot is Canvas { Background: SolidColorBrush background } && background.Color.A == 1 &&
                !action.ShowActivated && !action.ShowInTaskbar && action.ResizeMode == ResizeMode.NoResize && action.AllowsTransparency,
                "the masked window has nonzero-alpha clickable padding without activating on background show");
            action.SyncToTextSurface();
            Check(!action.HasVisibleActions && !owner.IsVisible && !text.IsVisible && !action.IsVisible &&
                new WindowInteropHelper(owner).Handle == 0 && new WindowInteropHelper(text).Handle == 0 && new WindowInteropHelper(action).Handle == 0,
                "construction, action assignment, preview layout and sync without a sibling handle remain inert");

            if (nativeSmoke && OperatingSystem.IsWindows() && SubtitleTextSurface.SupportsWindowsVersion(IncomingCaptureCapabilities.CurrentVersion))
            {
                nint foreground = GetForegroundWindow();
                nint ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
                new WindowInteropHelper(text).EnsureHandle(); text.PlaceRelativeToOwner(420);
                LayoutText(160);
                nint actionHandle = new WindowInteropHelper(action).EnsureHandle();
                Check(ReadRegion(actionHandle, _ => true) == 1,
                    "a newly created but never-shown action HWND starts with a real empty region");
                action.SyncToTextSurface();
                long actual = SubtitleTextSurface.ReadExtendedStyles(actionHandle);
                Check(action.NativeStylesApplied && (actual & SubtitleActionSurface.RequiredExtendedStyles) == SubtitleActionSurface.RequiredExtendedStyles &&
                    (actual & SubtitleActionSurface.ForbiddenExtendedStyles) == 0,
                    "the native action window receives the intended activatable control styles");
                GetWindowRect(new WindowInteropHelper(text).Handle, out NativeRect textBounds);
                GetWindowRect(actionHandle, out NativeRect actionBounds);
                Check(action.HasVisibleActions && textBounds.Equals(actionBounds) && ReferenceEquals(action.Owner, owner) && action.Topmost == owner.Topmost,
                    "native actions share the sibling's actual pixel bounds and the toolbar's owner/topmost state");
                double scale = GetDpiForWindow(actionHandle) / 96d;
                IReadOnlyList<Int32Rect> expected = SubtitleActionSurface.ClipRegions(action.Placements.Select(item => item.Bounds),
                    actionBounds.Right - actionBounds.Left, actionBounds.Bottom - actionBounds.Top, scale);
                int shape = ReadRegion(actionHandle, region => expected.All(rect => PtInRegion(region, rect.X + rect.Width / 2, rect.Y + rect.Height / 2)) &&
                    !PtInRegion(region, 1, 1) && !PtInRegion(region, (int)(100 * scale), (int)(80 * scale)));
                Check(shape >= 2, "the actual HRGN includes action centers and physically excludes unrelated subtitle/body pixels");
                action.SetActions(Array.Empty<SubtitleActionAnchor>());
                Check(!action.HasVisibleActions && ReadRegion(actionHandle, region => !PtInRegion(region, 300, 20)) == 1,
                    "empty actions install an empty HRGN rather than restoring the broad rectangular window");
                Check(!IsWindowVisible(ownerHandle) && !IsWindowVisible(new WindowInteropHelper(text).Handle) && !IsWindowVisible(actionHandle) &&
                    !owner.IsVisible && !text.IsVisible && !action.IsVisible && GetForegroundWindow() == foreground,
                    "native styles, geometry and regions never display a window or change the foreground app");
            }
            return checks;
        }
        finally { action.Close(); text.Close(); owner.Close(); }
    }

    private static int ReadRegion(nint window, Func<nint, bool> check)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new InvalidOperationException("Could not allocate a test region.");
        try
        {
            int kind = GetWindowRgn(window, region);
            if (kind == 0 || !check(region)) throw new InvalidOperationException("The action window's native region differs from its reserved controls.");
            return kind;
        }
        finally { DeleteObject(region); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
}

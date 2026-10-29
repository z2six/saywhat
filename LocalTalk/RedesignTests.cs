using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace IncomingTranslator;

// Selected design/accessibility regressions, not a full accessibility certification. This method
// only measures an existing, unshown PREVIEW window and creates unshown controls/dialogs. It never
// clicks, focuses, raises Loaded, calls runtime APIs, writes settings, or opens an audio device.
internal static class RedesignTests
{
    internal static int Run(MainWindow window)
    {
        int checks = 0;
        void Check(bool passed, string detail)
        {
            if (!passed) throw new InvalidOperationException("Redesign check: " + detail);
            checks++;
        }
        Check(Read<bool>(window, "_preview"), "layout checks require an inert preview window");
        Check(!window.IsLoaded && !window.IsVisible, "preview must never be shown or loaded");
        Check(window.Dispatcher.CheckAccess(), "WPF layout checks run on the creating STA");
        AppSettings settings = Read<AppSettings>(window, "_settings");
        RuntimeController runtime = Read<RuntimeController>(window, "_runtime");
        string before = JsonSerializer.Serialize(settings);
        string? settingsFile = File.Exists(AppPaths.SettingsPath) ? File.ReadAllText(AppPaths.SettingsPath) : null;
        bool setupComplete = settings.FirstRunSetupCompleted;
        string originalPage = Read<string>(window, "_page");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "preview has no active capture/runtime feature");
        try
        {
            Check(ColorOf(ModernTheme.Background).R >= ColorOf(ModernTheme.Background).B, "background is warm charcoal, not dark blue");
            Check(ColorOf(ModernTheme.Surface).R >= ColorOf(ModernTheme.Surface).B && ColorOf(ModernTheme.Raised).R >= ColorOf(ModernTheme.Raised).B, "surfaces stay in the warm family");
            Color coral = ColorOf(ModernTheme.Accent), sage = ColorOf(ModernTheme.Good);
            Check(coral.R > coral.G && coral.R > coral.B, "primary accent is coral");
            Check(sage.G > sage.R && sage.G > sage.B, "secondary accent is sage");
            foreach (Brush background in new[] { ModernTheme.Background, ModernTheme.Surface, ModernTheme.Raised, ModernTheme.Selection })
            {
                Check(Contrast(ModernTheme.Text, background) >= 4.5, "normal text has at least 4.5:1 contrast");
                Check(Contrast(ModernTheme.Muted, background) >= 4.5, "supporting text has at least 4.5:1 contrast");
            }
            Check(Contrast(ModernTheme.Ink, ModernTheme.Accent) >= 7, "primary coral action has high-contrast ink text");
            Check(Contrast(ModernTheme.Accent, ModernTheme.Raised) >= 3 && Contrast(ModernTheme.Text, ModernTheme.Raised) >= 3, "focus colors contrast with input surfaces");
            Check(window.MinWidth <= 900 && window.MinHeight <= 650, "dashboard permits smaller laptop-sized layouts");
            Check(!window.Topmost, "dashboard never forces itself above the game");

            var primary = ModernTheme.Button("Start translation", (_, _) => { }, true);
            var secondary = ModernTheme.Button("Choose languages", (_, _) => { });
            Prepare(primary); Prepare(secondary);
            Check(SameColor(primary.Background, ModernTheme.Accent) && SameColor(primary.Foreground, ModernTheme.Ink), "primary action uses coral and ink");
            Check(SameColor(secondary.Background, ModernTheme.Raised), "secondary actions do not compete with the primary action");
            Check(AutomationProperties.GetName(primary) == "Start translation", "button remains named for screen readers");
            Check(primary.MinHeight >= 40 && HasFocusTrigger(primary), "button has a usable target and keyboard focus treatment");
            TextBlock heading = ModernTheme.Heading("Voice translation");
            Check(heading.FontFamily.Source == "Georgia" && heading.TextWrapping == TextWrapping.Wrap, "editorial headings wrap instead of clipping");
            FrameworkElement mark = ModernTheme.ConversationMark();
            Check(!mark.IsHitTestVisible, "decorative conversation mark cannot intercept input");

            foreach (string page in new[] { "Home", "Languages", "Speakers", "Settings", "Setup", "Advanced", "Audio & timing", "Speaker options" })
            {
                window.Navigate(page);
                foreach (Size size in new[] { new Size(900, 650), new Size(1160, 800) })
                {
                    Layout(window, size);
                    var content = (FrameworkElement)window.Content;
                    Check(double.IsFinite(content.ActualWidth) && content.ActualWidth <= size.Width + 0.5, page + " fits the available width");
                    ScrollViewer[] wideScrolls = Nodes(content).OfType<ScrollViewer>().Where(view => view.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled || view.ScrollableWidth >= 1).ToArray();
                    Check(wideScrolls.Length == 0, page + " avoids horizontal scrolling at " + size.Width + ": " +
                        string.Join("; ", wideScrolls.Select(view => view.Name + "/" + view.TemplatedParent?.GetType().Name +
                            " mode=" + view.HorizontalScrollBarVisibility + " extent=" + view.ExtentWidth + " viewport=" + view.ViewportWidth + " overflow=" + view.ScrollableWidth)));
                    Check(Nodes(content).OfType<TextBlock>().Where(text => text.Text.Length > 100).All(text => text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None), page + " long guidance wraps or trims deliberately");
                    foreach (Expander expander in Nodes(content).OfType<Expander>().Where(expander => expander.ActualHeight > 0 && expander.Header is string))
                        Check(RenderedExpanderHeaderHasContrast(expander), page + " rendered section header has readable contrast");
                    if (size.Width == 1160 && page == "Home")
                        CheckHomePrimaryControlsInView(window, Check);
                    else if (size.Width == 1160 && page == "Setup")
                        CheckSetupActionReachable(window, Check);
                }
                var navigation = Read<Dictionary<string, Button>>(window, "_navigation");
                string active = MainWindow.PrimaryPages.Contains(page) ? page : "Advanced";
                Check(navigation.Values.All(button => button.HorizontalContentAlignment == HorizontalAlignment.Left), "sidebar labels are left aligned");
                Check(navigation[active].FontWeight == FontWeights.SemiBold && SameColor(navigation[active].Background, ModernTheme.Selection), "selected navigation has a shape/weight cue, not color alone");
                Check(navigation.All(item => AutomationProperties.GetName(item.Value) == MainWindow.PageDisplayName(item.Key)), "icon navigation retains its personal, spoken label");
                Button activeButton = navigation[active];
                Check(Nodes(activeButton).OfType<ContentPresenter>().Any(presenter => presenter.TemplatedParent == activeButton && presenter.HorizontalAlignment == HorizontalAlignment.Left), "button template honors sidebar content alignment");
            }

            window.Navigate("Languages"); Layout(window, new Size(900, 650));
            ComboBox[] languageFields = Nodes(window).OfType<ComboBox>().Where(control => AutomationProperties.GetName(control) is "Translate my voice to" or "Translate their voices to").ToArray();
            Check(languageFields.Length == 2 && languageFields.All(control => AutomationProperties.GetLabeledBy(control) is not null), "both translation directions have accessible language labels");
            Check(languageFields.All(HasFocusTrigger), "actual language selectors have keyboard focus treatment");
            Check(languageFields.All(ComboFocusIsWired), "actual selector focus changes a visible border, not an unused property");
            TextBox[] inputs = Nodes(window).OfType<TextBox>().ToArray();
            Check(inputs.Length > 0 && inputs.All(HasFocusTrigger), "actual text inputs have keyboard focus treatment");
            window.Navigate("Audio & timing"); Layout(window, new Size(900, 650));
            Slider[] sliders = Nodes(window).OfType<Slider>().ToArray();
            Check(sliders.Length >= 3 && sliders.All(control => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)) && AutomationProperties.GetLabeledBy(control) is not null), "timing/history sliders announce the setting they change");
            Check(sliders.All(HasFocusTrigger), "actual timing sliders have keyboard focus treatment");
            Check(Nodes(window).OfType<CheckBox>().Any() && Nodes(window).OfType<CheckBox>().All(HasFocusTrigger), "actual optional controls retain visible keyboard focus");
            TextBlock? status = Nodes(window).OfType<TextBlock>().FirstOrDefault(text => AutomationProperties.GetName(text) == "Translation status");
            Check(status is not null && AutomationProperties.GetLiveSetting(status) == AutomationLiveSetting.Polite, "completion and errors have a polite status region");

            settings.FirstRunSetupCompleted = true; // In-memory fixture only: NEVER Persist/Save.
            window.Navigate("Home"); Layout(window, new Size(900, 650));
            string home = TextOf(window);
            Check(home.Contains("Translate into VRChat chat", StringComparison.Ordinal), "Home has a functional own-voice translation heading");
            Check(home.Contains("MY VOICE", StringComparison.Ordinal) && home.Contains("Optional · live subtitles", StringComparison.Ordinal), "own voice leads and the separate optional subtitles remain identified in words");
            Check(home.Contains(settings.OwnVoiceTargetLanguage, StringComparison.Ordinal) && home.Contains(settings.IncomingTargetLanguage, StringComparison.Ordinal), "Home reflects the existing two language choices");
            Button? ownStart = Nodes(window).OfType<Button>().FirstOrDefault(button => button.Name == "OwnVoiceStartButton");
            Check(ownStart is not null && ownStart.Content?.ToString() == "Start translating my voice" &&
                !Nodes(window).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Start both"),
                "configured Home offers one clear own-voice action, not an implicit start-both action");
            Check(ownStart is not null && RenderedCaptionHasInk(ownStart, "Start translating my voice"), "rendered primary caption inherits ink rather than the implicit light text style");
            Check(home.Contains("Ready to start", StringComparison.Ordinal), "idle direction state is explicit without implying listening");
            Layout(window, new Size(1160, 800));
            CheckHomePrimaryControlsInView(window, Check);
            window.Navigate("Setup"); Layout(window, new Size(1160, 800));
            CheckSetupActionReachable(window, Check);
            settings.FirstRunSetupCompleted = false;
            window.Navigate("Setup"); Layout(window, new Size(900, 650));
            string setup = TextOf(window);
            Check(setup.Contains(AppBrand.DisplayName, StringComparison.Ordinal) && setup.Contains("prepare the required files", StringComparison.OrdinalIgnoreCase), "first run identifies the app and explains the setup task");
            Check(setup.Contains("Nothing listens or loads a model", StringComparison.Ordinal), "first run makes the explicit-start boundary clear");
            Check(Nodes(window).OfType<Expander>().Count() >= 2, "model/manual details stay progressively disclosed");
            RadioButton[] setupChoices = Nodes(window).OfType<RadioButton>().ToArray();
            foreach (RadioButton choice in setupChoices) choice.ApplyTemplate();
            Check(setupChoices.Length >= 2 && setupChoices.All(HasFocusTrigger), "actual setup choices retain visible keyboard focus");

            Window consent = window.CreateSetupConsentDialog(new("Prepare local translation tools. No audio will be captured.", 4_000_000_000, true, true));
            Check(!consent.IsVisible && !consent.IsLoaded && !consent.Topmost, "setup consent is an unshown, non-topmost factory");
            Prepare((FrameworkElement)consent.Content, new Size(550, 700));
            string consentText = TextOf(consent);
            Check(consentText.Contains("4.0 GB", StringComparison.Ordinal), "consent shows the download size");
            Check(consentText.Contains("approve", StringComparison.OrdinalIgnoreCase) && consentText.Contains("restart", StringComparison.OrdinalIgnoreCase), "consent explains Windows approval and restart");
            Check(consentText.Contains("cancel", StringComparison.OrdinalIgnoreCase) && consentText.Contains("Translation will not start", StringComparison.Ordinal), "consent preserves cancelability and explicit-start boundary");
            Check(Nodes(consent).OfType<Button>().Any(button => button.IsCancel) && Nodes(consent).OfType<Button>().All(button => !button.IsDefault), "Escape cancels and Enter cannot silently approve downloads");
        }
        finally
        {
            settings.FirstRunSetupCompleted = setupComplete;
            window.Navigate(originalPage);
        }
        Check(JsonSerializer.Serialize(settings) == before, "design checks preserve every language/model/pipeline setting");
        Check((File.Exists(AppPaths.SettingsPath) ? File.ReadAllText(AppPaths.SettingsPath) : null) == settingsFile, "design checks do not save settings");
        Check(!window.IsLoaded && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning, "design checks end with all capture/models inert");
        return checks;
    }

    private static T Read<T>(object instance, string field) => (T)(instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException("Missing preview test field: " + field));
    private static void Layout(MainWindow window, Size size) => Prepare((FrameworkElement)window.Content, size);
    private static void Prepare(FrameworkElement element, Size? size = null)
    {
        if (element is Control control) control.ApplyTemplate();
        Size bounds = size ?? new Size(350, 80);
        element.Measure(bounds); element.Arrange(new Rect(new Point(), bounds)); element.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        var seen = new HashSet<DependencyObject>(); pending.Push(root);
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
    private static string TextOf(DependencyObject root) => string.Join("\n", Nodes(root).OfType<TextBlock>().Select(text => text.Text));
    private static bool HasFocusTrigger(Control control) => control.Template?.Triggers.OfType<Trigger>().Any(trigger =>
        trigger.Property == UIElement.IsKeyboardFocusedProperty || trigger.Property == UIElement.IsKeyboardFocusWithinProperty) == true;
    private static bool RenderedCaptionHasInk(Button button, string caption)
    {
        DependencyObject[] rendered = Nodes(button).Where(node => node is TextBlock text && text.Text == caption || node is AccessText access && access.Text == caption).ToArray();
        return rendered.Length > 0 && rendered.All(node => node.GetValue(System.Windows.Documents.TextElement.ForegroundProperty) is Brush foreground && SameColor(foreground, ModernTheme.Ink));
    }
    private static void CheckHomePrimaryControlsInView(MainWindow window, Action<bool, string> check)
    {
        ScrollViewer viewport = OuterPageScroll(window);
        foreach (string name in new[] { "OwnVoiceStartButton", "HomeSpokenLanguageChoices", "HomeTargetLanguageChoices",
            "HomeNemotronRecognitionButton", "HomeVoxtralRecognitionButton", "HomeHyFastModelButton", "HomeHyLargerModelButton" })
        {
            FrameworkElement element = Nodes(window).OfType<FrameworkElement>().Single(node => node.Name == name);
            Point origin = element.TranslatePoint(new Point(), viewport);
            check(element.ActualWidth > 0 && element.ActualHeight > 0 && origin.Y >= -.5 &&
                origin.Y + element.ActualHeight <= viewport.ViewportHeight + .5,
                "Home's own-voice language/model/start choices stay in view at normal size: " + name +
                $" y={origin.Y:0.0}, height={element.ActualHeight:0.0}, viewport={viewport.ViewportHeight:0.0}");
        }
    }

    private static void CheckSetupActionReachable(MainWindow window, Action<bool, string> check)
    {
        // Fresh installs now use their separate, step-by-step setup view. This
        // secondary repair/configuration page includes real spoken-language
        // controls and may scroll; its action must stay usable, not force those
        // controls to disappear merely to satisfy the former one-screen design.
        ScrollViewer viewport = OuterPageScroll(window);
        Button lower = Read<Button>(window, "_setupActionButton");
        Button sticky = Read<Button>(window, "_saveButton");
        bool configured = Read<AppSettings>(window, "_settings").FirstRunSetupCompleted;
        if (configured)
        {
            var frame = (FrameworkElement)window.Content;
            Point origin = sticky.TranslatePoint(new Point(), frame);
            check(sticky.Visibility == Visibility.Visible && sticky.ActualWidth > 0 && sticky.ActualHeight >= 40 &&
                origin.X >= -.5 && origin.Y >= -.5 && origin.X + sticky.ActualWidth <= frame.ActualWidth + .5 &&
                origin.Y + sticky.ActualHeight <= frame.ActualHeight + .5,
                "Setup's configured Save/Download action stays visible in its fixed header");
            check(sticky.Content?.ToString() == lower.Content?.ToString() && sticky.IsEnabled == lower.IsEnabled,
                "Setup's fixed action mirrors the same save/download state as the detailed action");
        }
        viewport.ScrollToBottom(); ((FrameworkElement)window.Content).UpdateLayout();
        Point lowerOrigin = lower.TranslatePoint(new Point(), viewport);
        check(lower.ActualWidth > 0 && lower.ActualHeight >= 40 && lowerOrigin.Y >= -.5 &&
            lowerOrigin.Y + lower.ActualHeight <= viewport.ViewportHeight + .5 && viewport.ScrollableWidth <= .5,
            "Setup's detailed primary action remains reachable without horizontal scrolling");
        viewport.ScrollToTop(); ((FrameworkElement)window.Content).UpdateLayout();
    }

    private static ScrollViewer OuterPageScroll(MainWindow window)
    {
        ContentControl page = Read<ContentControl>(window, "_content");
        return Nodes(window).OfType<ScrollViewer>().First(view => ReferenceEquals(view.Content, page));
    }
    private static bool RenderedExpanderHeaderHasContrast(Expander expander)
    {
        expander.ApplyTemplate();
        if (expander.Header is not string caption || expander.Template?.FindName("header", expander) is not ToggleButton header) return false;
        header.ApplyTemplate();
        DependencyObject[] rendered = Nodes(header).Where(node => node is TextBlock text && text.Text == caption || node is AccessText access && access.Text == caption).ToArray();
        return rendered.Length > 0 && rendered.All(node =>
            node.GetValue(System.Windows.Documents.TextElement.ForegroundProperty) is Brush foreground && Contrast(foreground, RenderedBackground(node)) >= 4.5);
    }
    private static Brush RenderedBackground(DependencyObject element)
    {
        // Inspect painted ancestors, not a custom control's unused Background property. This catches
        // black text on either the warm page or a card without assuming all headers share a surface.
        for (DependencyObject? current = element; current is Visual; current = VisualTreeHelper.GetParent(current))
        {
            Brush? brush = current switch { Border border => border.Background, Panel panel => panel.Background, Window window => window.Background, _ => null };
            if (brush is SolidColorBrush solid && solid.Color.A == 255) return brush;
        }
        return ModernTheme.Background;
    }
    private static bool ComboFocusIsWired(ComboBox combo)
    {
        // Either a named root focus border is changed directly, or the dropdown's visible shell
        // follows the ComboBox.BorderBrush property changed by its focus trigger.
        if (combo.Template is null) return false;
        if (combo.Template.Triggers.OfType<Trigger>().Where(trigger => trigger.Property == UIElement.IsKeyboardFocusWithinProperty)
            .SelectMany(trigger => trigger.Setters.OfType<Setter>()).Any(setter => !string.IsNullOrEmpty(setter.TargetName) && combo.Template.FindName(setter.TargetName, combo) is Border)) return true;
        if (combo.Template.FindName("toggle", combo) is not ToggleButton toggle) return false;
        toggle.ApplyTemplate();
        if (toggle.Template?.FindName("shell", toggle) is not Border shell) return false;
        Binding? binding = BindingOperations.GetBinding(shell, Border.BorderBrushProperty);
        return binding?.Path?.Path == "BorderBrush" && binding.RelativeSource?.Mode == RelativeSourceMode.FindAncestor && binding.RelativeSource.AncestorType == typeof(ComboBox);
    }
    private static bool SameColor(Brush first, Brush second) => ColorOf(first) == ColorOf(second);
    private static Color ColorOf(Brush brush) => brush is SolidColorBrush solid ? solid.Color : throw new InvalidOperationException("Contrast checks require a solid theme brush.");
    private static double Contrast(Brush first, Brush second)
    {
        double a = Luminance(ColorOf(first)), b = Luminance(ColorOf(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static double Luminance(Color color)
    {
        if (color.A != 255) throw new InvalidOperationException("Contrast checks require opaque colors.");
        static double Linear(byte value) { double v = value / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}

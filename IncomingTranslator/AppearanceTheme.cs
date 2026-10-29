using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Data;
using System.Windows.Media;

namespace IncomingTranslator;

internal sealed record ThemePreset(string Id, string Name, bool IsDark);
internal sealed record ThemeTextPalette(string Id, string Name);

/// <summary>
/// Shared appearance roles. Brush identities never change: controls and templates that are
/// already open follow a theme change without discarding their editing state.
/// Apply is called on the WPF dispatcher by each UI host; it never opens images or models.
/// </summary>
internal static class AppearanceTheme
{
    // Resource lookup may freeze ordinary WPF brushes. A real color binding makes
    // these shared brushes non-freezable while preserving their object identities.
    private static readonly Dictionary<SolidColorBrush, BrushColorState> BrushColors = new(ReferenceEqualityComparer.Instance);
    public static readonly SolidColorBrush Background = New("#191817");
    public static readonly SolidColorBrush Surface = New("#24211F");
    public static readonly SolidColorBrush Raised = New("#302B27");
    public static readonly SolidColorBrush Stroke = New("#665A50");
    public static readonly SolidColorBrush Text = New("#F6EFE7");
    public static readonly SolidColorBrush Muted = New("#BBB0A6");
    public static readonly SolidColorBrush Accent = New("#F28C78");
    public static readonly SolidColorBrush Good = New("#ADD1B0");
    public static readonly SolidColorBrush Warning = New("#E5BC79");
    public static readonly SolidColorBrush Error = New("#FFA3A3");
    public static readonly SolidColorBrush Ink = New("#211B18");
    public static readonly SolidColorBrush Selection = New("#49312B");
    public static readonly SolidColorBrush Hero = New("#342823");
    public static readonly SolidColorBrush HeroStroke = New("#6D5144");
    public static readonly SolidColorBrush Focus = New("#F6EFE7");
    public static readonly SolidColorBrush ScrollGrip = New("#81746A");
    public static readonly SolidColorBrush Speaker0 = New("#F28C78");
    public static readonly SolidColorBrush Speaker1 = New("#ADD1B0");
    public static readonly SolidColorBrush Speaker2 = New("#E5BC79");
    public static readonly SolidColorBrush Speaker3 = New("#BAC8EA");
    public static readonly SolidColorBrush Speaker4 = New("#E5AFE1");
    public static readonly SolidColorBrush Speaker5 = New("#97D8D7");
    public static IReadOnlyList<SolidColorBrush> Speakers { get; } = new[] { Speaker0, Speaker1, Speaker2, Speaker3, Speaker4, Speaker5 };

    public static IReadOnlyList<ThemePreset> Presets { get; } = new[] {
        new ThemePreset("coral-night", "Coral Night", true),
        new ThemePreset("ink", "Ink", true),
        new ThemePreset("forest", "Forest", true),
        new ThemePreset("plum", "Plum", true),
        new ThemePreset("paper", "Paper", false),
        new ThemePreset("sand", "Sand", false),
        new ThemePreset("mint", "Mint", false),
        new ThemePreset("high-contrast", "High Contrast", true),
        new ThemePreset("custom", "Custom", true)
    };
    public static IReadOnlyList<ThemeTextPalette> TextPalettes { get; } = new[] {
        new ThemeTextPalette("auto", "Automatic"),
        new ThemeTextPalette("warm-light", "Warm light text"),
        new ThemeTextPalette("cool-light", "Cool light text"),
        new ThemeTextPalette("neutral-dark", "Neutral dark text"),
        new ThemeTextPalette("warm-dark", "Warm dark text")
    };

    public static string CurrentPresetId { get; private set; } = "coral-night";
    public static bool IsDark { get; private set; } = true;
    public static event Action? Changed;
    public static Func<string, string>? TranslateMessageUi { get; set; }

    public static void Normalize(AppSettings settings)
    {
        if (!Presets.Any(p => string.Equals(p.Id, settings.UiThemeId, StringComparison.Ordinal))) settings.UiThemeId = "coral-night";
        if (!TryColor(settings.CustomThemeBackground, out Color background)) background = ColorOf("#191817");
        settings.CustomThemeBackground = Hex(background);
        if (!TextPalettes.Any(p => string.Equals(p.Id, settings.CustomThemeTextPalette, StringComparison.Ordinal))) settings.CustomThemeTextPalette = "auto";
        if (!double.IsFinite(settings.CustomThemeImageOpacity)) settings.CustomThemeImageOpacity = .16;
        settings.CustomThemeImageOpacity = Math.Clamp(settings.CustomThemeImageOpacity, 0, .6);
        settings.CustomThemeImagePath ??= "";
    }

    public static void Apply(AppSettings settings) => ApplyCore(settings, notify: true);
    // Numeric contrast checks need the actual computed colors but not 1,000 raster-logo
    // refreshes. Only inert tests use this path; every production apply notifies all UI.
    internal static void ApplyForTests(AppSettings settings) => ApplyCore(settings, notify: false);
    private static void ApplyCore(AppSettings settings, bool notify)
    {
        Normalize(settings);
        Palette p = settings.UiThemeId switch {
            "ink" => PaletteOf("#101419", "#1B222B", "#27313E", "#ECEFF5", "#AEBCCF", "#A6C8F0", "#9DD4B4", "#E7C27D"),
            "forest" => PaletteOf("#141B17", "#202D24", "#2C3B30", "#F0F1E4", "#B5C4AD", "#C3DB88", "#97D8BA", "#E5BF79"),
            "plum" => PaletteOf("#211821", "#302330", "#403043", "#F9EEF6", "#CBB4C8", "#E9A2C5", "#B2D7BC", "#ECCA8F"),
            "paper" => PaletteOf("#F7F6F2", "#FFFFFF", "#ECEBE6", "#222A2C", "#565E60", "#276B79", "#286A43", "#845C13"),
            "sand" => PaletteOf("#F2EBDF", "#FBF7EF", "#E9DFC9", "#342A24", "#695C4F", "#963D2C", "#356C43", "#805709"),
            "mint" => PaletteOf("#EAF4EF", "#F8FFFB", "#DCEBE3", "#20392E", "#51685D", "#276554", "#28663D", "#806015"),
            "high-contrast" => PaletteOf("#000000", "#000000", "#111111", "#FFFFFF", "#E7E7E7", "#FFE666", "#9CFFAE", "#FFDCA0"),
            "custom" => Custom(settings),
            _ => PaletteOf("#191817", "#24211F", "#302B27", "#F6EFE7", "#BBB0A6", "#F28C78", "#ADD1B0", "#E5BC79")
        };
        CurrentPresetId = settings.UiThemeId;
        IsDark = Luminance(p.Background) < .35;
        Color selection = SelectionColor(p);
        Color[] grounds = { p.Background, p.Surface, p.Raised, selection };
        Set(Background, p.Background); Set(Surface, p.Surface); Set(Raised, p.Raised);
        Set(Text, Readable(p.Text, grounds)); Set(Muted, Readable(p.Muted, grounds));
        Set(Accent, Readable(p.Accent, grounds)); Set(Good, Readable(p.Good, grounds)); Set(Warning, Readable(p.Warning, grounds));
        Set(Error, Readable(IsDark ? ColorOf("#FFA3A3") : ColorOf("#A42532"), grounds));
        // The primary button's ink is chosen for its actual accent fill, not for the page.
        Set(Ink, Contrast(ColorOf("#000000"), Accent.Color) >= Contrast(ColorOf("#FFFFFF"), Accent.Color) ? ColorOf("#000000") : ColorOf("#FFFFFF"));
        Set(Stroke, settings.UiThemeId == "high-contrast" ? ColorOf("#FFFFFF") : Readable(Mix(p.Raised, Text.Color, .26), grounds, 3));
        Set(Selection, selection);
        Set(Hero, p.Surface); Set(HeroStroke, Mix(p.Surface, Accent.Color, .34));
        Set(Focus, Text.Color); Set(ScrollGrip, Readable(Mix(p.Raised, Text.Color, .42), grounds, 3));
        Color[] speakerSeeds = IsDark
            ? new[] { p.Accent, p.Good, p.Warning, ColorOf("#B8C8F0"), ColorOf("#E4AFE0"), ColorOf("#94D8D7") }
            : new[] { p.Accent, p.Good, p.Warning, ColorOf("#3F5DA3"), ColorOf("#863B86"), ColorOf("#166C77") };
        for (int i = 0; i < Speakers.Count; i++) Set(Speakers[i], Readable(speakerSeeds[i], grounds));
        if (notify) Changed?.Invoke();
    }

    public static bool TryColor(string? value, out Color color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#') return false;
        if (!uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true;
    }

    public static double Contrast(Color a, Color b)
    {
        double first = Luminance(a), second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    /// <summary>Native title bars follow appearance too; unsupported Windows versions remain usable.</summary>
    public static void ApplyWindowChrome(Window window)
    {
        if (window.GetValue(ChromeAttachedProperty) is true) return;
        window.SetValue(ChromeAttachedProperty, true);
        void Update()
        {
            if (!window.Dispatcher.CheckAccess()) { _ = window.Dispatcher.BeginInvoke((Action)Update); return; }
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == 0) return;
            try {
                int dark = IsDark ? 1 : 0;
                int result = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
                if (result != 0) _ = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
                int caption = ColorRef(Background.Color), text = ColorRef(Text.Color);
                _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
                _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        EventHandler ready = (_, _) => Update();
        EventHandler? closed = null;
        closed = (_, _) => { Changed -= Update; window.SourceInitialized -= ready; window.Closed -= closed; };
        window.SourceInitialized += ready;
        window.Closed += closed;
        Changed += Update;
        Update();
    }
    private static readonly DependencyProperty ChromeAttachedProperty = DependencyProperty.RegisterAttached("ChromeAttached", typeof(bool), typeof(AppearanceTheme), new PropertyMetadata(false));
    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    // App-owned confirmations/errors use the shared theme. Message bodies stay literal:
    // callers may supply names, paths, benchmark output or translation text.
    public static MessageBoxResult ShowMessage(Window? owner, string message, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        Window dialog = CreateMessageDialog(owner, message, caption, buttons, image);
        _ = dialog.ShowDialog();
        return (MessageBoxResult)dialog.GetValue(MessageResultProperty);
    }

    public static Window CreateMessageDialog(Window? owner, string message, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new Window {
            Title = caption, Width = 540, MinWidth = 340, MaxHeight = 760,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is { IsLoaded: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = owner is not { IsLoaded: true }, Background = Background, Foreground = Text,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic"),
            UseLayoutRounding = true, SnapsToDevicePixels = true, Topmost = false
        };
        if (owner is { IsLoaded: true }) { dialog.Owner = owner; dialog.Icon = owner.Icon; }
        dialog.Resources.MergedDictionaries.Add(CreateResources());
        ApplyWindowChrome(dialog);
        var content = new StackPanel { Margin = new Thickness(26) };
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (image != MessageBoxImage.None) {
            SolidColorBrush tone = image == MessageBoxImage.Error ? Error : image == MessageBoxImage.Warning ? Warning : Accent;
            string glyph = image == MessageBoxImage.Question ? "?" : image == MessageBoxImage.Information ? "i" : "!";
            var icon = new Border { Background = Raised, CornerRadius = new CornerRadius(18), Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = glyph, Foreground = tone, FontSize = 23, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            AutomationProperties.SetName(icon, caption); title.Children.Add(icon);
        }
        title.Children.Add(new TextBlock { Text = caption, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(title);
        var body = new TextBlock { Text = message, Foreground = Text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        AutomationProperties.SetName(body, message);
        content.Children.Add(new ScrollViewer { Content = body, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        (string Label, MessageBoxResult Result)[] choices = buttons switch {
            MessageBoxButton.OKCancel => [("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            _ => [("OK", MessageBoxResult.OK)]
        };
        MessageBoxResult dismiss = buttons is MessageBoxButton.YesNo ? MessageBoxResult.No : buttons is MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
        for (int i = 0; i < choices.Length; i++) {
            (string label, MessageBoxResult result) = choices[i];
            var action = new Button { Content = TranslateMessageUi?.Invoke(label) ?? label, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0),
                Background = i == 0 ? Accent : Raised, Foreground = i == 0 ? Ink : Text, BorderBrush = i == 0 ? Accent : Stroke,
                IsDefault = i == 0, IsCancel = result == dismiss, HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(action, (string)action.Content);
            action.Click += (_, _) => { dialog.SetValue(MessageResultProperty, result); dialog.Close(); };
            actions.Children.Add(action);
        }
        content.Children.Add(actions); dialog.Content = content;
        dialog.Closing += (_, _) => { if ((MessageBoxResult)dialog.GetValue(MessageResultProperty) == MessageBoxResult.None) dialog.SetValue(MessageResultProperty, dismiss); };
        return dialog;
    }
    private static readonly DependencyProperty MessageResultProperty = DependencyProperty.RegisterAttached("MessageResult", typeof(MessageBoxResult), typeof(AppearanceTheme), new PropertyMetadata(MessageBoxResult.None));

    /// <summary>Complete shared control styles, including popups and chrome-like secondary surfaces.</summary>
    public static ResourceDictionary CreateResources()
    {
        var resources = (ResourceDictionary)XamlReader.Parse(ControlStyles);
        resources["BackgroundBrush"] = Background; resources["PanelBrush"] = Surface; resources["InputBrush"] = Raised;
        resources["TextBrush"] = Text; resources["MutedBrush"] = Muted; resources["BorderBrush"] = Stroke;
        resources["AccentBrush"] = Accent; resources["GoodBrush"] = Good; resources["WarningBrush"] = Warning;
        resources["ErrorBrush"] = Error; resources["InkBrush"] = Ink; resources["SelectionBrush"] = Selection;
        resources["HeroBrush"] = Hero; resources["HeroStrokeBrush"] = HeroStroke; resources["FocusBrush"] = Focus;
        resources["ScrollGripBrush"] = ScrollGrip;
        for (int i = 0; i < Speakers.Count; i++) resources[$"Speaker{i}Brush"] = Speakers[i];
        return resources;
    }

    private const string ControlStyles = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Window"><Setter Property="Background" Value="{DynamicResource BackgroundBrush}"/><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="FontFamily" Value="Segoe UI"/></Style>
          <Style TargetType="TextBlock"><Setter Property="FontFamily" Value="Segoe UI"/><Setter Property="FontSize" Value="14"/><Setter Property="TextWrapping" Value="Wrap"/></Style>
          <Style TargetType="Label"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="Transparent"/></Style>
          <Style TargetType="Button">
            <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="18,11"/><Setter Property="Cursor" Value="Hand"/><Setter Property="MinHeight" Value="42"/><Setter Property="FontSize" Value="14"/><Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="shell" CornerRadius="10" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
              </Border><ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource FocusBrush}"/><Setter TargetName="shell" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="shell" Property="Opacity" Value="0.75"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="shell" Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="RepeatButton">
            <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Padding" Value="0"/><Setter Property="MinWidth" Value="0"/><Setter Property="MinHeight" Value="0"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border x:Name="shell" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="3"><ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="shell" Property="Background" Value="{DynamicResource SelectionBrush}"/><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="shell" Property="Background" Value="{DynamicResource SelectionBrush}"/><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource FocusBrush}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource FocusBrush}"/><Setter TargetName="shell" Property="BorderThickness" Value="1"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ToggleButton"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="12,8"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton"><Border x:Name="shell" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="8"><ContentPresenter VerticalAlignment="Center" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" RecognizesAccessKey="True"/></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="shell" Property="Background" Value="{DynamicResource SelectionBrush}"/><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource FocusBrush}"/><Setter TargetName="shell" Property="BorderThickness" Value="2"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="TextBox"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="CaretBrush" Value="{DynamicResource TextBrush}"/><Setter Property="Padding" Value="12,10"/><Setter Property="FontSize" Value="14"/><Setter Property="SelectionBrush" Value="{DynamicResource SelectionBrush}"/><Setter Property="SelectionOpacity" Value="0.8"/><Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox"><Border x:Name="shell" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"><ScrollViewer x:Name="PART_ContentHost"/></Border><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBox">
            <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="MinHeight" Value="40"/><Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
              <ToggleButton x:Name="toggle" Focusable="False" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" IsChecked="{Binding IsDropDownOpen,Mode=TwoWay,RelativeSource={RelativeSource TemplatedParent}}">
                <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="shell" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{Binding BorderBrush,RelativeSource={RelativeSource AncestorType=ComboBox}}" BorderThickness="{TemplateBinding BorderThickness}"><TextBlock Text="⌄" Foreground="{DynamicResource MutedBrush}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
              </ToggleButton>
              <ContentPresenter x:Name="selection" Margin="12,0,34,0" VerticalAlignment="Center" IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
              <TextBox x:Name="PART_EditableTextBox" Margin="4,2,30,2" Visibility="Collapsed" Background="{DynamicResource InputBrush}" BorderThickness="0" IsReadOnly="{TemplateBinding IsReadOnly}"/>
              <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False"><Border Background="{DynamicResource InputBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="8" MinWidth="{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="320"><ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border></Popup>
            </Grid><ControlTemplate.Triggers><Trigger Property="IsEditable" Value="True"><Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/><Setter TargetName="selection" Property="Visibility" Value="Collapsed"/></Trigger><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource FocusBrush}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBoxItem"><Setter Property="Padding" Value="12,10"/><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="item" Padding="{TemplateBinding Padding}" Background="{TemplateBinding Background}"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="item" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="item" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="CheckBox"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="FontSize" Value="14"/><Setter Property="Margin" Value="0,8,0,8"/><Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><DockPanel LastChildFill="True">
              <Border x:Name="check" Width="20" Height="20" Background="{DynamicResource InputBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="5" Margin="0,0,10,0" VerticalAlignment="Center"><TextBlock x:Name="mark" Text="✓" FontSize="13" Foreground="{DynamicResource InkBrush}" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/></Border>
              <ContentPresenter VerticalAlignment="Center" RecognizesAccessKey="True"/>
            </DockPanel><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="mark" Property="Visibility" Value="Visible"/><Setter TargetName="check" Property="Background" Value="{DynamicResource AccentBrush}"/><Setter TargetName="check" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsChecked" Value="{x:Null}"><Setter TargetName="mark" Property="Visibility" Value="Visible"/><Setter TargetName="mark" Property="Text" Value="−"/><Setter TargetName="check" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="check" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="check" Property="BorderBrush" Value="{DynamicResource FocusBrush}"/><Setter TargetName="check" Property="BorderThickness" Value="2"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="RadioButton"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="FontSize" Value="14"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton"><DockPanel><Border x:Name="ring" Width="20" Height="20" CornerRadius="10" Background="{DynamicResource InputBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" Margin="0,0,10,0"><Ellipse x:Name="dot" Width="10" Height="10" Fill="{DynamicResource AccentBrush}" Visibility="Collapsed"/></Border><ContentPresenter VerticalAlignment="Center" RecognizesAccessKey="True"/></DockPanel><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="dot" Property="Visibility" Value="Visible"/><Setter TargetName="ring" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ring" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="ring" Property="BorderBrush" Value="{DynamicResource FocusBrush}"/><Setter TargetName="ring" Property="BorderThickness" Value="2"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="Expander"><Setter Property="Foreground" Value="{DynamicResource MutedBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Expander"><StackPanel><ToggleButton x:Name="header" Foreground="{TemplateBinding Foreground}" IsChecked="{Binding IsExpanded,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}" HorizontalContentAlignment="Left" Cursor="Hand"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="shell" Padding="0,9"><DockPanel><TextBlock x:Name="arrow" Text="+" Foreground="{DynamicResource AccentBrush}" FontSize="18" Width="22"/><ContentPresenter Content="{Binding Header,RelativeSource={RelativeSource AncestorType=Expander}}" VerticalAlignment="Center"/></DockPanel></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="arrow" Property="Text" Value="−"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="shell" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton><ContentPresenter x:Name="body" ContentSource="Content" Margin="0,4,0,8" Visibility="Collapsed"/></StackPanel><ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="body" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style x:Key="SlimScrollBar" TargetType="ScrollBar">
            <Setter Property="Width" Value="7"/><Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar"><Grid Background="Transparent" Margin="1,4"><Track x:Name="PART_Track" IsDirectionReversed="True"><Track.DecreaseRepeatButton><RepeatButton Opacity="0" Focusable="False" Command="ScrollBar.PageUpCommand"/></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="grip" Background="{DynamicResource ScrollGripBrush}" CornerRadius="3"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="grip" Property="Background" Value="{DynamicResource MutedBrush}"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="grip" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Opacity="0" Focusable="False" Command="ScrollBar.PageDownCommand"/></Track.IncreaseRepeatButton></Track></Grid><ControlTemplate.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="7"/><Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ScrollBar" BasedOn="{StaticResource SlimScrollBar}"/>
          <Style TargetType="ScrollViewer"><Setter Property="Background" Value="Transparent"/><Setter Property="HorizontalScrollBarVisibility" Value="Disabled"/><Setter Property="VerticalScrollBarVisibility" Value="Auto"/><Setter Property="PanningMode" Value="VerticalOnly"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollViewer"><Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><Grid.RowDefinitions><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
              <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Margin="{TemplateBinding Padding}" CanContentScroll="{TemplateBinding CanContentScroll}"/>
              <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Style="{StaticResource SlimScrollBar}" Orientation="Vertical" Minimum="0" Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}" Value="{Binding VerticalOffset,RelativeSource={RelativeSource TemplatedParent},Mode=OneWay}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
              <ScrollBar x:Name="PART_HorizontalScrollBar" Grid.Row="1" Style="{StaticResource SlimScrollBar}" Orientation="Horizontal" Minimum="0" Maximum="{TemplateBinding ScrollableWidth}" ViewportSize="{TemplateBinding ViewportWidth}" Value="{Binding HorizontalOffset,RelativeSource={RelativeSource TemplatedParent},Mode=OneWay}" Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}"/>
            </Grid></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="Slider"><Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="Margin" Value="0,8,0,8"/><Setter Property="Height" Value="24"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Slider"><Grid>
              <Border x:Name="focus" BorderBrush="Transparent" BorderThickness="1" CornerRadius="6"/>
              <Border Height="4" Background="{DynamicResource ScrollGripBrush}" CornerRadius="2" VerticalAlignment="Center" Margin="8,0"/>
              <Track x:Name="PART_Track"><Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton><Track.Thumb><Thumb Width="18" Height="18"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="{DynamicResource AccentBrush}" BorderBrush="{DynamicResource FocusBrush}" BorderThickness="1" CornerRadius="9"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton></Track>
            </Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="focus" Property="BorderBrush" Value="{DynamicResource FocusBrush}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ProgressBar"><Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Height" Value="8"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ProgressBar"><Grid><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4"/><Border x:Name="PART_Track" Margin="1"/><Border x:Name="PART_Indicator" Background="{TemplateBinding Foreground}" HorizontalAlignment="Left" CornerRadius="3" Margin="1"/></Grid><ControlTemplate.Triggers><Trigger Property="IsIndeterminate" Value="True"><Setter TargetName="PART_Indicator" Property="HorizontalAlignment" Value="Stretch"/><Trigger.EnterActions><BeginStoryboard Name="IndeterminatePulse"><Storyboard><DoubleAnimation Storyboard.TargetName="PART_Indicator" Storyboard.TargetProperty="Opacity" From="0.25" To="0.85" Duration="0:0:0.8" AutoReverse="True" RepeatBehavior="Forever"/></Storyboard></BeginStoryboard></Trigger.EnterActions><Trigger.ExitActions><StopStoryboard BeginStoryboardName="IndeterminatePulse"/></Trigger.ExitActions></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ToolTip"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="Padding" Value="10,7"/><Setter Property="MaxWidth" Value="400"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToolTip"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" Padding="{TemplateBinding Padding}" CornerRadius="8"><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ContextMenu"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="Padding" Value="5"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" Padding="{TemplateBinding Padding}" CornerRadius="8"><StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/></Border></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="MenuItem"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="Padding" Value="11,8"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem"><Grid><Border x:Name="shell" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}" CornerRadius="5"><DockPanel><ContentPresenter ContentSource="Icon" Margin="0,0,8,0"/><TextBlock x:Name="check" Text="✓" Visibility="Collapsed" Foreground="{DynamicResource AccentBrush}" Margin="0,0,8,0"/><TextBlock Text="{TemplateBinding InputGestureText}" Foreground="{DynamicResource MutedBrush}" DockPanel.Dock="Right" Margin="20,0,0,0"/><ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/></DockPanel></Border><Popup x:Name="PART_Popup" IsOpen="{Binding IsSubmenuOpen,RelativeSource={RelativeSource TemplatedParent}}" Placement="Right" AllowsTransparency="True" Focusable="False"><Border Background="{DynamicResource InputBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="8" Padding="5"><StackPanel IsItemsHost="True"/></Border></Popup></Grid><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="shell" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="check" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="Separator"><Setter Property="Margin" Value="4,5"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Background="{DynamicResource BorderBrush}"/></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ListBox"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="{DynamicResource InputBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/></Style>
          <Style TargetType="ListBoxItem"><Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="Padding" Value="10,7"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem"><Border x:Name="shell" Background="Transparent" Padding="{TemplateBinding Padding}" CornerRadius="6"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="shell" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="shell" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{DynamicResource FocusBrush}"/><Setter TargetName="shell" Property="BorderThickness" Value="1"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
        </ResourceDictionary>
        """;

    private sealed record Palette(Color Background, Color Surface, Color Raised, Color Text, Color Muted, Color Accent, Color Good, Color Warning);
    private static Palette PaletteOf(string bg, string surface, string raised, string text, string muted, string accent, string good, string warning) =>
        new(ColorOf(bg), ColorOf(surface), ColorOf(raised), ColorOf(text), ColorOf(muted), ColorOf(accent), ColorOf(good), ColorOf(warning));
    private static Palette Custom(AppSettings settings)
    {
        Color bg = ColorOf(settings.CustomThemeBackground);
        bool dark = Luminance(bg) < .35;
        // Midtone backgrounds sit at the black/white contrast crossover. Surface shades
        // must move away from the chosen foreground pole, never cross that boundary.
        bool whiteText = Contrast(ColorOf("#FFFFFF"), bg) >= Contrast(ColorOf("#000000"), bg);
        Color surfacePole = whiteText ? ColorOf("#000000") : ColorOf("#FFFFFF");
        Color surface = Mix(bg, surfacePole, .055), raised = Mix(bg, surfacePole, .105);
        Palette basis = settings.CustomThemeTextPalette switch {
            "warm-light" => PaletteOf("#191817", "#24211F", "#302B27", "#FFF0DA", "#CBBBA8", "#F2B08B", "#BCD8A7", "#E4CA83"),
            "cool-light" => PaletteOf("#101419", "#1B222B", "#27313E", "#F0F5FF", "#B8C5D9", "#A6C8F0", "#9DD8C0", "#E7CD91"),
            "neutral-dark" => PaletteOf("#F7F7F7", "#FFFFFF", "#ECECEC", "#20272D", "#56616B", "#345A8A", "#286344", "#79540D"),
            "warm-dark" => PaletteOf("#F2EBDF", "#FBF7EF", "#E9DFC9", "#342A24", "#695C4F", "#963D2C", "#356C43", "#805709"),
            _ => dark
                ? PaletteOf("#191817", "#24211F", "#302B27", "#F6EFE7", "#BBB0A6", "#F28C78", "#ADD1B0", "#E5BC79")
                : PaletteOf("#F7F6F2", "#FFFFFF", "#ECEBE6", "#222A2C", "#565E60", "#276B79", "#286A43", "#845C13")
        };
        return basis with { Background = bg, Surface = surface, Raised = raised };
    }
    private sealed class BrushColorState : INotifyPropertyChanged
    {
        private Color _value;
        public BrushColorState(Color value) => _value = value;
        public Color Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static SolidColorBrush New(string hex)
    {
        var state = new BrushColorState(ColorOf(hex));
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(nameof(BrushColorState.Value)) { Source = state, Mode = BindingMode.OneWay });
        BrushColors.Add(brush, state);
        return brush;
    }
    private static Color ColorOf(string hex) { TryColor(hex, out Color color); return color; }
    private static string Hex(Color value) => $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    private static void Set(SolidColorBrush brush, Color color)
    {
        BrushColors[brush].Value = color;
        // Deliver synchronously even before the window's dispatcher message loop starts.
        BindingOperations.GetBindingExpression(brush, SolidColorBrush.ColorProperty)?.UpdateTarget();
    }
    private static Color Mix(Color a, Color b, double weight) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * weight), (byte)Math.Round(a.G + (b.G - a.G) * weight), (byte)Math.Round(a.B + (b.B - a.B) * weight));
    private static Color SelectionColor(Palette palette)
    {
        Color black = ColorOf("#000000"), white = ColorOf("#FFFFFF");
        Color[] opaque = { palette.Background, palette.Surface, palette.Raised };
        Color pole = opaque.Min(bg => Contrast(white, bg)) >= opaque.Min(bg => Contrast(black, bg)) ? white : black;
        for (int i = 16; i >= 0; i--) {
            Color selection = Mix(palette.Raised, palette.Accent, i / 100.0);
            if (Contrast(pole, selection) >= 4.5) return selection;
        }
        return palette.Raised;
    }
    private static Color Readable(Color candidate, Color[] backgrounds, double minimum = 4.5)
    {
        if (backgrounds.All(bg => Contrast(candidate, bg) >= minimum)) return candidate;
        Color black = ColorOf("#000000"), white = ColorOf("#FFFFFF");
        // Choose the same pole for the whole background family; preserve the palette hue
        // until its shade reaches WCAG normal-text contrast on every opaque UI surface.
        Color pole = backgrounds.Min(bg => Contrast(white, bg)) >= backgrounds.Min(bg => Contrast(black, bg)) ? white : black;
        for (int i = 1; i <= 100; i++) {
            Color adjusted = Mix(candidate, pole, i / 100.0);
            if (backgrounds.All(bg => Contrast(adjusted, bg) >= minimum)) return adjusted;
        }
        return pole;
    }
    private static double Luminance(Color color)
    {
        static double Channel(byte value) { double n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
}

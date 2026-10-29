using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IncomingTranslator;

/// <summary>Dashboard helpers use the same appearance roles as subtitles and standalone tools.</summary>
internal static class ModernTheme
{
    public static SolidColorBrush Background => AppearanceTheme.Background;
    public static SolidColorBrush Surface => AppearanceTheme.Surface;
    public static SolidColorBrush Raised => AppearanceTheme.Raised;
    public static SolidColorBrush Stroke => AppearanceTheme.Stroke;
    public static SolidColorBrush Text => AppearanceTheme.Text;
    public static SolidColorBrush Muted => AppearanceTheme.Muted;
    public static SolidColorBrush Accent => AppearanceTheme.Accent;
    public static SolidColorBrush Good => AppearanceTheme.Good;
    public static SolidColorBrush Warning => AppearanceTheme.Warning;
    public static SolidColorBrush Error => AppearanceTheme.Error;
    public static SolidColorBrush Ink => AppearanceTheme.Ink;
    public static SolidColorBrush Selection => AppearanceTheme.Selection;
    public static SolidColorBrush Hero => AppearanceTheme.Hero;
    public static SolidColorBrush HeroStroke => AppearanceTheme.HeroStroke;
    public static SolidColorBrush Focus => AppearanceTheme.Focus;
    public static SolidColorBrush ScrollGrip => AppearanceTheme.ScrollGrip;

    // Fixed-color brushes are only for user artwork or non-theme color previews.
    public static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    public static void Apply(Application app)
    {
        if (app.Resources.MergedDictionaries.Any(d => d.Contains("SayWhatAppearanceResources"))) return;
        app.Resources.MergedDictionaries.Add(Create());
    }
    public static ResourceDictionary Create()
    {
        ResourceDictionary resources = AppearanceTheme.CreateResources();
        resources["SayWhatAppearanceResources"] = true;
        return resources;
    }

    public static Border Card(UIElement child) => new() { Background = Surface, BorderBrush = Stroke,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(24),
        Margin = new Thickness(0,0,0,16), Child = child };
    public static TextBlock Label(string text, double size=14, Brush? brush=null, bool localize=true) {
        var label = new TextBlock { FontSize=size, Foreground=brush ?? Text, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,8) };
        if(localize) UiLocalization.Bind(label, TextBlock.TextProperty, text);
        else { label.Text=text; UiLocalization.Literal(label); }
        return label;
    }
    public static Button Button(string text, RoutedEventHandler action, bool primary=false) {
        var button=new Button {Margin=new Thickness(0,6,10,6),Background=primary?Accent:Raised,
            Foreground=primary?Ink:Text, BorderBrush=primary?Accent:Stroke, HorizontalContentAlignment=HorizontalAlignment.Center};
        UiLocalization.Bind(button, ContentControl.ContentProperty, text);
        UiLocalization.Bind(button, System.Windows.Automation.AutomationProperties.NameProperty, text);
        button.Click+=action; return button;
    }
    public static TextBlock Heading(string text, double size=30) { var label=Label(text,size); label.FontFamily=new FontFamily("Georgia"); return label; }
    public static TextBlock Kicker(string text, Brush? color=null) { var label=Label(text.ToUpperInvariant(),10,color??Muted); label.FontWeight=FontWeights.SemiBold; return label; }
    public static Border Chip(string text, Brush? color=null) {
        var label=Label(text,11,color??Muted);label.Margin=new Thickness(0);
        return new() { CornerRadius=new CornerRadius(7), Padding=new Thickness(9,5,9,5), Background=Raised,Child=label };
    }
    public static FrameworkElement ConversationMark(double size=44)
    {
        var mark=new Canvas {Width=size,Height=size,IsHitTestVisible=false}; double scale=size/44;
        var first=new System.Windows.Shapes.Path {Data=Geometry.Parse("M5,5 H26 Q31,5 31,10 V24 Q31,29 26,29 H15 L7,35 V29 H5 Q0,29 0,24 V10 Q0,5 5,5 Z"),Fill=Accent,RenderTransform=new ScaleTransform(scale,scale)};
        var second=new System.Windows.Shapes.Path {Data=Geometry.Parse("M22,17 H39 Q44,17 44,22 V34 Q44,39 39,39 H36 V44 L29,39 H22 Q17,39 17,34 V22 Q17,17 22,17 Z"),Fill=Good,Stroke=Background,StrokeThickness=2,RenderTransform=new ScaleTransform(scale,scale)};
        mark.Children.Add(first);mark.Children.Add(second);return mark;
    }
    public static FrameworkElement Icon(string name, Brush? color=null)
    {
        string data=name switch {
            "Home"=>"M3,10 L12,3 L21,10 M6,9 V21 H18 V9 M10,21 V14 H14 V21",
            "Languages"=>"M3,5 H14 M9,2 V5 M5,8 Q7,14 14,17 M13,5 Q12,14 3,17 M15,21 L19,11 L23,21 M17,18 H21",
            "Speakers"=>"M8,11 A4,4 0 1 1 8,3 A4,4 0 1 1 8,11 M2,21 V18 Q2,14 8,14 Q14,14 14,18 V21 M17,5 Q23,8 17,12 M18,15 Q22,16 22,21",
            "Settings"=>"M4,5 H21 M4,12 H21 M4,19 H21 M8,2 V8 M17,9 V15 M11,16 V22",
            "Themes"=>"M12,3 A9,9 0 1 0 21,12 Q21,10 18,10 H16 Q13,10 14,14 Q15,17 12,17 M7,8 H7.1 M11,6 H11.1 M16,7 H16.1",
            "Setup"=>"M3,5 H21 V21 H3 Z M7,2 V8 M17,2 V8 M7,15 L11,18 L18,11",
            "Help"=>"M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M8.5,8 A3.5,3.5 0 0 1 15.5,8 Q15.5,10 12,12 V14 M12,17 H12.1",
            "Microphone"=>"M9,3 Q12,0 15,3 V12 Q12,16 9,12 Z M5,10 V12 Q5,19 12,19 Q19,19 19,12 V10 M12,19 V23 M8,23 H16",
            "Headphones"=>"M3,13 V11 A9,9 0 0 1 21,11 V13 M3,12 H7 V21 H3 Z M17,12 H21 V21 H17 Z",
            "Connection"=>"M9,15 L15,9 M7,13 L4,16 A4,4 0 0 0 10,22 L13,19 M11,5 L14,2 A4,4 0 0 1 20,8 L17,11",
            _=>"M8,4 L2,12 L8,20 M16,4 L22,12 L16,20 M14,2 L10,22"
        };
        return new System.Windows.Shapes.Path {Data=Geometry.Parse(data),Stroke=color??Muted,StrokeThickness=1.6,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Width=22,Height=22,Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Left,IsHitTestVisible=false};
    }
}

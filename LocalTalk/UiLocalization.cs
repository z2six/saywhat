using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace IncomingTranslator;

// Only interface copy is bound here. Stored option keys and all user/model/audio text
// stay unchanged. WPF bindings subscribe weakly to the shared language notifier.
internal static class UiLocalization
{
    private sealed class LanguageState : INotifyPropertyChanged
    {
        public string Language => UiText.Language;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal LanguageState() => UiText.LanguageChanged += () => PropertyChanged?.Invoke(this, new(nameof(Language)));
    }
    private sealed class TranslationConverter : IValueConverter, IMultiValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => parameter is FormatSource format
            ? UiText.Format(format.English,format.Arguments.Select(argument => argument is Phrase phrase ? UiText.T(phrase.English) : argument).ToArray())
            : UiText.T(parameter as string ?? value?.ToString() ?? "");
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => UiText.T(values.FirstOrDefault()?.ToString() ?? "");
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
    private sealed class ProfileLabelConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.FirstOrDefault() is TranslationProfileUiChoice choice ? choice.ToString() : "";
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
    private static readonly LanguageState State = new();
    private static readonly TranslationConverter Converter = new();
    internal sealed record Phrase(string English);
    private sealed record FormatSource(string English,object?[] Arguments);
    internal static readonly DependencyProperty LiteralProperty = DependencyProperty.RegisterAttached("Literal", typeof(bool), typeof(UiLocalization), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    internal static T Literal<T>(T element) where T : DependencyObject { element.SetValue(LiteralProperty, true); return element; }

    internal static void Bind(DependencyObject target, DependencyProperty property, string english)
    {
        if ((bool)target.GetValue(LiteralProperty)) { target.SetValue(property, english); return; }
        BindingOperations.SetBinding(target, property, new Binding(nameof(LanguageState.Language)) { Source = State, Converter = Converter, ConverterParameter = english, Mode = BindingMode.OneWay });
    }
    internal static void SetText(TextBlock target, string english) => Bind(target, TextBlock.TextProperty, english);
    internal static void SetFormat(TextBlock target,string english,params object?[] arguments) =>
        BindingOperations.SetBinding(target,TextBlock.TextProperty,new Binding(nameof(LanguageState.Language)) {
            Source=State,Converter=Converter,ConverterParameter=new FormatSource(english,arguments),Mode=BindingMode.OneWay });

    internal static void ApplyTree(DependencyObject root)
    {
        if ((bool)root.GetValue(LiteralProperty)) return;
        if (root is TextBlock text) BindKnown(text, TextBlock.TextProperty);
        if (root is ContentControl content && root is not ComboBoxItem) BindKnown(content, ContentControl.ContentProperty);
        if (root is HeaderedContentControl header) BindKnown(header, HeaderedContentControl.HeaderProperty);
        if (root is Window window) BindKnown(window, Window.TitleProperty);
        if (root is FrameworkElement element)
        {
            BindKnown(element, FrameworkElement.ToolTipProperty);
            BindKnown(element, System.Windows.Automation.AutomationProperties.NameProperty);
        }
        // TextBox content is editable/user text, not an interface phrase. ComboBox values
        // remain stable model/language/config keys; only their display template is translated.
        if (root is TextBox || root is ComboBox) return;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency) ApplyTree(dependency);
    }
    private static void BindKnown(DependencyObject target, DependencyProperty property)
    {
        if (BindingOperations.IsDataBound(target, property)) return;
        if (target.GetValue(property) is string english && UiText.Contains(english)) Bind(target, property, english);
    }
    internal static void TranslateOptionLabels(ComboBox combo)
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        var binding = new MultiBinding { Converter = Converter };
        binding.Bindings.Add(new Binding());
        binding.Bindings.Add(new Binding(nameof(LanguageState.Language)) { Source = State });
        factory.SetBinding(TextBlock.TextProperty, binding);
        combo.ItemTemplate = new DataTemplate { VisualTree = factory };
    }

    // Unlike option keys, saved profile names are literal user text even if the user
    // happens to name a profile "Settings" or another known interface phrase.
    internal static void TranslateProfileLabels(ComboBox combo)
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        var binding = new MultiBinding { Converter = new ProfileLabelConverter() };
        binding.Bindings.Add(new Binding());
        binding.Bindings.Add(new Binding(nameof(LanguageState.Language)) { Source = State });
        factory.SetBinding(TextBlock.TextProperty, binding);
        combo.ItemTemplate = new DataTemplate { VisualTree = factory };
    }

    internal static string SetupSummary(string english)
    {
        var download=System.Text.RegularExpressions.Regex.Match(english,@"^Download about ([0-9.,]+) GB of translation files\. ");
        string result=download.Success ? UiText.Format("Download about {0} GB of translation files.",download.Groups[1].Value)+" "+english[download.Length..] : english;
        foreach(string sentence in new[]{
            "Reuse the translation files already on this PC.",
            "Set up the speech component automatically. This also downloads Windows/Linux components and build tools; an NVIDIA setup can need several more GB and at least 20 GB of free space. Windows may ask for permission or a restart.",
            "Restore the missing speech model for your existing speech component. Keep its registered engine and save the selected languages.",
            "Check your existing speech component and save the selected languages.",
            "Keep your existing translation connection.","Install the Microsoft runtime if needed.","Translation stays stopped until you press Start."})
            result=result.Replace(sentence,UiText.T(sentence),StringComparison.Ordinal);
        return result;
    }
}

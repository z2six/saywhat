using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace IncomingTranslator;

internal sealed partial class MainWindow
{
    private StackPanel ConnectionSettingsSection()
    {
        var section = Section("Connection settings", "Automatic connections avoid conflicts with other apps. VRChat OSC addresses and existing external translators keep their configured connections.");
        var fixedFields = new StackPanel { IsEnabled = !_settings.AutomaticManagedPorts };
        section.Children.Add(Check("Choose local ports automatically (recommended)", _settings.AutomaticManagedPorts,
            enabled => { _settings.AutomaticManagedPorts = enabled; fixedFields.IsEnabled = !enabled; }));
        TextBox Port(string name, int current, Action<int> set)
        {
            var input = Input(current.ToString(CultureInfo.InvariantCulture)); input.Name = name; input.MaxLength = 5;
            input.TextChanged += (_, _) => set(int.TryParse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0);
            return input;
        }
        fixedFields.Children.Add(TwoFields(
            Field("Your voice recognition port", Port("OwnSpeechPort", _settings.OwnSpeechPort, port => _settings.OwnSpeechPort = port)),
            Field("Subtitle speech recognition port", Port("IncomingSpeechPort", _settings.IncomingSpeechPort, port => _settings.IncomingSpeechPort = port))));
        fixedFields.Children.Add(TwoFields(
            Field("Your voice translator port", Port("OwnTranslationPort", _settings.OwnTranslationPort, port => _settings.OwnTranslationPort = port)),
            Field("Separate subtitle translator port", Port("IncomingTranslationPort", _settings.IncomingTranslationPort, port => _settings.IncomingTranslationPort = port))));
        fixedFields.Children.Add(Field("Groups recognition port", Port("GroupsSpeechPort", _settings.GroupsSpeechPort, port => _settings.GroupsSpeechPort = port)));
        fixedFields.Children.Add(ModernTheme.Label("Fixed ports must be different and between 1024 and 65535. Save all applies changes to running translation tools.", 12, ModernTheme.Muted));
        section.Children.Add(fixedFields);
        return section;
    }
}

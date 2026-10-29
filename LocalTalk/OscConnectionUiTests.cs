using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace IncomingTranslator;

// Only injects synthetic status values into the inert preview. Never calls the real probe.
internal static class OscConnectionUiTests
{
    internal static int Run(MainWindow window)
    {
        int checks = 0;
        void Check(bool value, string description)
        { if (!value) throw new InvalidOperationException("OSC UI check: " + description); checks++; }
        T Read<T>(string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Check(Read<bool>("_preview") && !window.IsVisible, "all injected checks are unshown previews");
        Check(!Read<System.Windows.Threading.DispatcherTimer>("_oscTimer").IsEnabled && Read<VrchatOscConnectionProbe?>("_oscProbe") is null,
            "preview does not enumerate game processes or query ports");
        var settings = Read<AppSettings>("_settings"); bool complete = settings.FirstRunSetupCompleted;
        string previousLanguage = UiText.Language, page = Read<string>("_page");
        VrchatOscConnectionStatus original = Read<VrchatOscConnectionStatus>("_oscConnection");
        try
        {
            settings.FirstRunSetupCompleted = true;
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language; window.Navigate("Home");
                foreach (var (state, label) in new[] {
                    (VrchatOscConnectionState.Checking, "Checking VRChat…"), (VrchatOscConnectionState.Connected, "VRChat OSC available"),
                    (VrchatOscConnectionState.NotRunning, "VRChat is closed"), (VrchatOscConnectionState.Unavailable, "No OSC connection"),
                    (VrchatOscConnectionState.WrongPort, "OSC port mismatch"), (VrchatOscConnectionState.Error, "Couldn't check OSC") })
                {
                    window.SetOscConnectionStatus(new(state, state == VrchatOscConnectionState.WrongPort ? 9002 : null));
                    Check(Read<TextBlock>("_oscConnectionText").Text == UiText.T(label), "state label is localized and not color-only: " + language + "/" + state);
                    if (state is VrchatOscConnectionState.Unavailable or VrchatOscConnectionState.WrongPort or VrchatOscConnectionState.Error)
                        Check(Read<Expander>("_oscHelp").IsExpanded, "unavailable status reveals actionable help");
                    string detail = Read<TextBlock>("_oscHelpText").Text;
                    if (state == VrchatOscConnectionState.Unavailable)
                        Check(detail.Contains("OSC") && detail.Contains("Enabled") && !detail.Contains("OSC is off", StringComparison.Ordinal), "no response does not prove OSC is disabled");
                    if (state == VrchatOscConnectionState.Connected)
                        Check(detail == UiText.T("VRChat responded to OSCQuery. Chatbox visibility still depends on VRChat settings."), "available status does not promise chat delivery");
                }
            }
            Check(!Read<System.Windows.Threading.DispatcherTimer>("_oscTimer").IsEnabled && Read<VrchatOscConnectionProbe?>("_oscProbe") is null,
                "injected UI states never trigger a network probe");
            return checks;
        }
        finally
        {
            settings.FirstRunSetupCompleted = complete; UiText.Language = previousLanguage;
            window.SetOscConnectionStatus(original); window.Navigate(page);
        }
    }
}

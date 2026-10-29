using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace IncomingTranslator;

// Injects already-parsed synthetic metadata only. Does not make an HTTP request,
// download a file, execute setup, stop translation or load a model.
internal static class UpdateUiTests
{
    internal static int Run(MainWindow window)
    {
        int checks = 0;
        void Check(bool result, string detail)
        { if (!result) throw new InvalidOperationException("Update UI: " + detail); checks++; }
        T Read<T>(string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Check(Read<bool>("_preview") && !window.IsVisible, "preview is inert and unshown");
        Check(!Read<bool>("_startupUpdatesChecked"), "preview never starts an automatic update check");
        Check(new AppSettings().CheckForUpdatesOnLaunch, "launch update checks default on");
        Check(new AppSettings { CheckForUpdatesOnLaunch = false }.Copy().CheckForUpdatesOnLaunch == false &&
            !SettingsStore.ReadSettings("{\"CheckForUpdatesOnLaunch\":false}").CheckForUpdatesOnLaunch, "saved opt-out survives snapshots and loads");
        string previousUi = UiText.Language;
        AppUpdateInfo? prior = Read<AppUpdateInfo?>("_availableUpdate");
        Border banner = Read<Border>("_updateBanner"); Visibility previousVisibility = banner.Visibility;
        try
        {
            banner.Visibility = Visibility.Collapsed;
            window.ShowAvailableUpdate(new(true, true, "99.0.0", new Uri("https://github.com/z2six/saywhat/releases/tag/v99.0.0")));
            Check(banner.Visibility == Visibility.Collapsed, "metadata without installer instructions cannot offer installation");
            var installer = new AppUpdateInstaller(new Uri("https://github.com/z2six/saywhat/releases/download/v99.0.0/SayWhat-99.0.0-Setup.exe"), new string('a', 64), 100);
            var update = new AppUpdateInfo(true, true, "99.0.0", new Uri("https://github.com/z2six/saywhat/releases/tag/v99.0.0"), installer);
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language; window.ShowAvailableUpdate(update);
                Check(banner.Visibility == Visibility.Visible && Read<TextBlock>("_updateBannerText").Text == UiText.Format("Preview {0} is available.", "99.0.0"),
                    "available version is visible and localized: " + language);
                foreach (string action in new[] { "Update now", "Later" })
                    Check(Nodes(banner).OfType<Button>().Any(button => AutomationProperties.GetName(button) == UiText.T(action)), "banner offers localized explicit action: " + action);
            }
            Check(!Read<bool>("_startupUpdatesChecked"), "showing synthetic metadata does not start a network check");
            return checks;
        }
        finally
        {
            typeof(MainWindow).GetField("_availableUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, prior);
            banner.Visibility = previousVisibility; UiText.Language = previousUi;
        }
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object item in LogicalTreeHelper.GetChildren(root))
            if (item is DependencyObject child) foreach (DependencyObject node in Nodes(child)) yield return node;
    }
}

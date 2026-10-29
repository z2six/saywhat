using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;

namespace IncomingTranslator;

internal static class SubtitleProfileUiTests
{
    // A never-shown subtitle window. Loaded is never raised and test callbacks
    // only collect profile IDs; they cannot start capture, models or downloads.
    internal static int Run()
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Subtitle profile UI: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string? fixture = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(!string.IsNullOrWhiteSpace(fixture) && string.Equals(Path.GetFullPath(fixture), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase), "requires the desktop runner's isolated data root");
        AppSettings original = SettingsStore.Load();
        string languageBefore = UiText.Language;
        var protectedPaths = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "incoming-translator.log"), Path.Combine(AppPaths.Root, "speaker-feedback.json") };
        var filesBefore = protectedPaths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        var window = new SubtitleWindow();
        T Read<T>(string name) => (T)(typeof(SubtitleWindow).GetField(name, flags)?.GetValue(window)
            ?? throw new InvalidOperationException("Missing subtitle profile field: " + name));
        void SetSettings(AppSettings settings) => typeof(SubtitleWindow).GetField("_settings", flags)!.SetValue(window, settings.Copy());
        void Refresh() => typeof(SubtitleWindow).GetMethod("RefreshSubtitleProfileButtons", flags)!.Invoke(window, null);
        Task Request(string id) => (Task)typeof(SubtitleWindow).GetMethod("RequestSubtitleProfileAsync", flags)!.Invoke(window, [id])!;
        try
        {
            Button normal = Read<Button>("_normalProfileButton"), groups = Read<Button>("_groupsProfileButton");
            Check(!window.IsVisible && !window.IsLoaded && window.SpeakerFeedbackHost is null, "window construction is inert");
            Check(!normal.IsEnabled && !groups.IsEnabled, "standalone overlays cannot switch an unmanaged runtime without an app callback");
            var profileButtons = Nodes(window.TranscriptRoot).OfType<Button>().Where(button => button.Name.Contains("ProfileButton", StringComparison.Ordinal)).ToArray();
            Check(profileButtons.Length == 2 && profileButtons.Contains(normal) && profileButtons.Contains(groups), "transcript contains only Normal and Groups switches, not Quick");
            Check(!Nodes(window).Contains(normal) && !Nodes(window).Contains(groups) &&
                !Nodes(window.CompactPreview).OfType<Button>().Any() && LogicalTreeHelper.GetParent(window.TranscriptRoot) is null,
                "profile controls stay in the detached transcript rather than the compact toolbar or passive subtitles");
            var quick = TranslationProfiles.Select(original, TranslationProfiles.QuickId);
            SetSettings(quick); Refresh();
            Check(Read<TextBlock>("_subtitleProfileCaption").Text == UiText.T("Quick · change profiles in Settings"), "Quick has a Settings caption rather than an overlay switch");
            Request(TranslationProfiles.NormalId).GetAwaiter().GetResult();
            Check(Read<AppSettings>("_settings").ActiveTranslationProfileId == TranslationProfiles.QuickId, "missing runtime callback cannot mutate the selected profile");
            var requested = new List<string>();
            window.ProfileChangeRequested = id => { requested.Add(id); return Task.CompletedTask; };
            Refresh();
            Check(normal.IsEnabled && groups.IsEnabled, "app-managed mode makes both overlay choices available");
            Request(TranslationProfiles.QuickId).GetAwaiter().GetResult();
            Check(requested.Count == 0, "Quick is rejected even if requested through the internal callback path");
            Request(TranslationProfiles.NormalId).GetAwaiter().GetResult();
            Request(TranslationProfiles.GroupsId).GetAwaiter().GetResult();
            Check(requested.SequenceEqual(new[] { TranslationProfiles.NormalId, TranslationProfiles.GroupsId }), "overlay choices call one shared application apply path");
            typeof(SubtitleWindow).GetField("_profileSwitchPending", flags)!.SetValue(window, true); Refresh();
            Check(!normal.IsEnabled && !groups.IsEnabled && Read<TextBlock>("_subtitleProfileCaption").Text == UiText.T("Switching subtitle mode…"), "switching has a visible state and prevents duplicate requests");
            typeof(SubtitleWindow).GetField("_profileSwitchPending", flags)!.SetValue(window, false); Refresh();

            foreach (ThemePreset theme in AppearanceTheme.Presets)
            {
                AppSettings appearance = original.Copy(); appearance.UiThemeId = theme.Id;
                AppearanceTheme.Apply(appearance);
                foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
                {
                    UiText.Language = language;
                    SetSettings(quick); window.RefreshInterfaceLanguage();
                    Check(normal.Content?.ToString() == UiText.T("Normal") && groups.Content?.ToString() == UiText.T("Groups")
                        && Read<TextBlock>("_subtitleProfileCaption").Text == UiText.T("Quick · change profiles in Settings"), "switch labels and Quick caption follow " + theme.Id + "/" + language);
                    Check(AutomationProperties.GetName(normal) == UiText.T("Normal") && AutomationProperties.GetName(groups) == UiText.T("Groups"), "switches have localized accessible labels");
                    Check(ReferenceEquals(normal.Foreground, AppearanceTheme.Muted) && ReferenceEquals(groups.Foreground, AppearanceTheme.Muted)
                        && ReferenceEquals(normal.BorderBrush, AppearanceTheme.Stroke), "Quick leaves both switches unselected in the current theme");
                    SetSettings(TranslationProfiles.Select(quick, TranslationProfiles.NormalId)); Refresh();
                    Check(ReferenceEquals(normal.Foreground, AppearanceTheme.Good) && ReferenceEquals(groups.Foreground, AppearanceTheme.Muted), "Normal receives the selected theme role");
                    SetSettings(TranslationProfiles.Select(quick, TranslationProfiles.GroupsId)); Refresh();
                    Check(ReferenceEquals(groups.Foreground, AppearanceTheme.Good) && Read<TextBlock>("_subtitleProfileCaption").Text == UiText.T("Experimental"), "Groups has one restrained localized experimental caption");
                    Check(groups.ToolTip?.ToString() == UiText.T("Recognize separate voices with VibeVoice. Adds delay and uses more memory.") + " · " + UiText.T("Experimental"),
                        "Groups provides an explanation rather than only a mode name");
                }
            }
            var named = TranslationProfiles.Create(quick, "Settings"); SetSettings(named); Refresh();
            Check(Read<TextBlock>("_subtitleProfileCaption").Text == "Settings", "custom names remain literal even when they match a translated UI key");
            Check(!window.IsLoaded && !window.IsVisible && new WindowInteropHelper(window).Handle == 0 &&
                LogicalTreeHelper.GetParent(window.TranscriptRoot) is null && window.SpeakerFeedbackHost is null,
                "profile checks never created a window or started an incoming service");
            Check(filesBefore.All(pair => pair.Value is null ? !File.Exists(pair.Key) : File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "profile checks did not save preferences, audio evidence or logs");
            return checks;
        }
        finally
        {
            window.ProfileChangeRequested = null;
            window.Close();
            AppearanceTheme.Apply(original); ThemeBackground.Apply(original);
            UiText.Language = languageBefore;
        }
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject node in Nodes(dependency)) yield return node;
    }
}

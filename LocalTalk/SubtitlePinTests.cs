using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;

namespace IncomingTranslator;

internal static class SubtitlePinOfflineTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        // DesktopTests sets SAYWHAT_DATA_ROOT to an isolated workspace before calling this.
        AppSettings original = SettingsStore.Load();
        SubtitleWindow? overlay = null;
        try
        {
            AppSettings selected = original.Copy();
            selected.OwnVoiceTargetLanguage = "Japanese";
            selected.IncomingTargetLanguage = "Korean";
            selected.OwnManagedModelId = "hy-mt2-7b-q4_k_m";
            selected.CloseButtonBehavior = "Tray";
            selected.Glossary = "Synthetic saved glossary";
            SettingsStore.Save(selected);
            string microphoneBefore = File.ReadAllText(AppPaths.FastConfigPath);
            string sentenceBefore = File.ReadAllText(AppPaths.FoxTransConfigPath);
            JsonObject document = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
            document["FuturePreference"] = "keep this unknown setting";
            File.WriteAllText(AppPaths.SettingsPath, document.ToJsonString());

            using (File.Open(AppPaths.FastConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (File.Open(AppPaths.FoxTransConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                SettingsStore.SaveSubtitlePinPreference(false);

            AppSettings unpinned = SettingsStore.Load();
            Check(!unpinned.SubtitlesAlwaysOnTop && !unpinned.Copy().SubtitlesAlwaysOnTop, "Pin preference survives saving and copying.");
            Check(unpinned.OwnVoiceTargetLanguage == "Japanese" && unpinned.IncomingTargetLanguage == "Korean", "Pin toggle preserves language choices.");
            Check(unpinned.OwnManagedModelId == selected.OwnManagedModelId && unpinned.CloseButtonBehavior == "Tray" && unpinned.Glossary == selected.Glossary, "Pin toggle preserves model, close and glossary choices.");
            Check(File.ReadAllText(AppPaths.FastConfigPath) == microphoneBefore && File.ReadAllText(AppPaths.FoxTransConfigPath) == sentenceBefore, "Pin toggle never rewrites live microphone configs.");
            Check(JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!["FuturePreference"]!.GetValue<string>() == "keep this unknown setting", "Pin toggle preserves unknown preferences.");

            AppSettings staleStartup = selected.Copy();
            SettingsStore.Save(staleStartup);
            Check(!SettingsStore.Load().SubtitlesAlwaysOnTop && !staleStartup.SubtitlesAlwaysOnTop, "A stale dashboard/startup save cannot undo a later pin toggle.");
            Check(File.ReadAllText(AppPaths.FastConfigPath) == microphoneBefore && File.ReadAllText(AppPaths.FoxTransConfigPath) == sentenceBefore, "Merging the newer pin flag does not alter microphone settings.");

            overlay = new SubtitleWindow(); // Never shown: no Loaded event, service or capture.
            Check(!overlay.Topmost, "New subtitles honor the saved unpinned preference without starting capture.");
            var pin = (Button)typeof(SubtitleWindow).GetField("_alwaysOnTopButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
            Check(pin.Content?.ToString() == "◇" && AutomationProperties.GetName(pin) == UiText.Format("Always on top · {0}", UiText.T("Off")),
                "The unpinned compact glyph exposes the full localized state to assistive tools.");
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(SettingsStore.Load().SubtitlesAlwaysOnTop && overlay.Topmost && pin.Content?.ToString() == "◆" &&
                AutomationProperties.GetName(pin) == UiText.Format("Always on top · {0}", UiText.T("On")),
                "One compact pin click immediately enables and saves the preference.");
            Check(new WindowInteropHelper(overlay).Handle == 0 && !overlay.IsLoaded && !overlay.IsVisible &&
                LogicalTreeHelper.GetParent(overlay.TranscriptRoot) is null && LogicalTreeHelper.GetParent(overlay.CompactPreview) is null &&
                overlay.SpeakerFeedbackHost is null,
                "Pinning an unloaded toolbar does not create a window, attach transcript content, or start capture.");
            Check(File.ReadAllText(AppPaths.FastConfigPath) == microphoneBefore && File.ReadAllText(AppPaths.FoxTransConfigPath) == sentenceBefore,
                "The compact pin action preserves microphone configs too.");

            bool denied = false;
            using (File.Open(AppPaths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                try { SettingsStore.SaveSubtitlePinPreference(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { denied = true; }
            Check(denied && SettingsStore.Load().SubtitlesAlwaysOnTop, "Failed atomic replacement leaves the previous pin preference intact.");
            Check(!Directory.EnumerateFiles(AppPaths.Root, "vrchat-translation-settings.json.tmp-*").Any(), "Pin-save failures leave no temporary settings files.");
        }
        finally { overlay?.Close(); SettingsStore.Save(original); }
        return checks;
    }
}

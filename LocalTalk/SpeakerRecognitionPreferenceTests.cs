using System.Text.Json.Nodes;

namespace IncomingTranslator;

internal static class SpeakerRecognitionPreferenceTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        AppSettings original = SettingsStore.Load();
        try
        {
            string microphoneBefore = File.ReadAllText(AppPaths.FastConfigPath);
            string sentenceBefore = File.ReadAllText(AppPaths.FoxTransConfigPath);
            JsonObject document = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
            document["FuturePreference"] = "retain this value";
            File.WriteAllText(AppPaths.SettingsPath, document.ToJsonString());
            using (File.Open(AppPaths.FastConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (File.Open(AppPaths.FoxTransConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                SettingsStore.SaveSpeakerRecognitionPreference(false);
            AppSettings disabled = SettingsStore.Load();
            Check(!disabled.UseAiSpeakerRecognition, "Speaker recognition can be disabled independently.");
            Check(disabled.OwnVoiceTargetLanguage == original.OwnVoiceTargetLanguage && disabled.IncomingTargetLanguage == original.IncomingTargetLanguage && disabled.OwnManagedModelId == original.OwnManagedModelId,
                "Recognition toggle preserves translation selections.");
            Check(disabled.SubtitlesAlwaysOnTop == original.SubtitlesAlwaysOnTop && disabled.CloseButtonBehavior == original.CloseButtonBehavior,
                "Recognition toggle preserves window preferences.");
            Check(File.ReadAllText(AppPaths.FastConfigPath) == microphoneBefore && File.ReadAllText(AppPaths.FoxTransConfigPath) == sentenceBefore,
                "Recognition toggle never rewrites microphone configuration.");
            Check(JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!["FuturePreference"]!.GetValue<string>() == "retain this value",
                "Recognition toggle preserves unknown preferences.");
            AppSettings stale = original.Copy();
            stale.UseAiSpeakerRecognition = true;
            SettingsStore.Save(stale);
            Check(!SettingsStore.Load().UseAiSpeakerRecognition && !stale.UseAiSpeakerRecognition,
                "An older startup/settings snapshot cannot re-enable recognition after Stop.");
            SettingsStore.SaveSpeakerRecognitionPreference(true);
            Check(SettingsStore.Load().UseAiSpeakerRecognition, "Recognition can be re-enabled without modifying translation files.");
            bool denied = false;
            using (File.Open(AppPaths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                try { SettingsStore.SaveSpeakerRecognitionPreference(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { denied = true; }
            Check(denied && SettingsStore.Load().UseAiSpeakerRecognition, "Failed preference replacement keeps the previous recognition setting.");
        }
        finally
        {
            SettingsStore.SaveSpeakerRecognitionPreference(original.UseAiSpeakerRecognition);
            SettingsStore.Save(original);
        }
        return checks;
    }
}

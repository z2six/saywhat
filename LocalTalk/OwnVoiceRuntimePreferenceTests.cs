using System.Text;
using System.Text.Json;

namespace IncomingTranslator;

internal static class OwnVoiceRuntimePreferenceTests
{
    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool value, string description)
        { if (!value) throw new InvalidOperationException("Runtime preferences: " + description); checks++; }
        string projected = Path.Combine(AppPaths.Root, OwnVoiceRuntimePreferences.FileName);
        string[] paths = [AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath, projected];
        var saved = paths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        try
        {
            var settings = new AppSettings
            {
                OwnRecognitionEngine = "nemotron", OwnVoiceSourceLanguage = "Chinese",
                OwnVoiceTargetLanguage = "English", OwnVoicePauseWhenVrchatMuted = false,
                OwnVoiceAdditionalLanguagesEnabled = true, OwnVoiceAdditionalTargetLanguages = ["Japanese", "Korean"],
                Glossary = new string('g', 2000)
            };
            for (int index = 0; index < 24; index++) settings = TranslationProfiles.Create(settings, "Fixture " + index);
            SettingsStore.Save(settings);
            Check(new FileInfo(AppPaths.SettingsPath).Length > OwnVoiceRuntimePreferences.MaximumBytes,
                "a supported profile library genuinely exceeds the old host reader limit");
            Check(new FileInfo(projected).Length < 16_384, "the host view stays small independently of saved profiles");
            using (var runtime = OwnVoiceRuntimePreferences.Read(AppPaths.Root))
            {
                JsonElement root = runtime.RootElement;
                Check(root.GetProperty("OwnRecognitionEngine").GetString() == "nemotron" &&
                    root.GetProperty("OwnVoiceSourceLanguage").GetString() == "Chinese" &&
                    !root.GetProperty("OwnVoicePauseWhenVrchatMuted").GetBoolean(),
                    "large valid settings preserve the recognizer, source language and explicit mute opt-out");
                Check(root.GetProperty("OwnVoiceAdditionalTargetLanguages").GetArrayLength() == 2,
                    "additional output languages do not disappear with a large profile library");
                Check(!root.TryGetProperty("CustomTranslationProfiles", out _) && !root.TryGetProperty("OwnTranslatorBaseUrl", out _),
                    "the worker view excludes profile snapshots and connection credentials");
            }
            Check(SettingsStore.Load().CustomTranslationProfiles.Count == 24, "projection leaves all saved profiles intact");
            byte[] before = File.ReadAllBytes(projected);
            DateTime stamp = File.GetLastWriteTimeUtc(projected);
            SettingsStore.SaveUiLanguagePreference("ja");
            Check(File.ReadAllBytes(projected).SequenceEqual(before) && File.GetLastWriteTimeUtc(projected) == stamp,
                "a window-only preference does not rewrite identical worker settings");
        }
        finally
        {
            foreach (string path in paths)
                if (saved[path] is { } bytes) File.WriteAllBytes(path, bytes); else if (File.Exists(path)) File.Delete(path);
        }
        string legacy = Path.Combine(directory, "runtime-preference-legacy"); Directory.CreateDirectory(legacy);
        string legacyPath = Path.Combine(legacy, OwnVoiceRuntimePreferences.LegacyFileName);
        string projectionPath = Path.Combine(legacy, OwnVoiceRuntimePreferences.FileName);
        File.WriteAllText(legacyPath, "{\"OwnVoicePauseWhenVrchatMuted\":false}", new UTF8Encoding(true));
        using (var document = OwnVoiceRuntimePreferences.Read(legacy))
            Check(!document.RootElement.GetProperty("OwnVoicePauseWhenVrchatMuted").GetBoolean(), "legacy roots and UTF8 BOM remain supported");
        File.WriteAllText(projectionPath, "{broken");
        bool rejected = false;
        try { using var document = OwnVoiceRuntimePreferences.Read(legacy); } catch (JsonException) { rejected = true; }
        Check(rejected, "a malformed preferred view cannot silently revive stale legacy preferences");
        File.WriteAllText(projectionPath, new string(' ', OwnVoiceRuntimePreferences.MaximumBytes + 1)); rejected = false;
        try { using var document = OwnVoiceRuntimePreferences.Read(legacy); } catch (IOException) { rejected = true; }
        Check(rejected, "the worker reader remains strictly bounded");
        return checks;
    }
}

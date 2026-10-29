using System.Globalization;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

/// <summary>Pure catalogue checks: no windows, files, processes, models or settings access.</summary>
internal static class UiTextTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Interface translation: " + reason);
            checks++;
        }
        string prior = UiText.Language;
        CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
        int changes = 0;
        void Changed() => changes++;
        try
        {
            UiText.Language = "en";
            UiText.LanguageChanged += Changed;
            UiText.Language = " zh_hans ";
            Check(UiText.Language == "zh-CN" && changes == 1, "Chinese aliases normalize and notify once");
            UiText.Language = "zh-CN";
            Check(changes == 1, "same canonical selection does not notify twice");
            UiText.Language = "JA-jp";
            Check(UiText.Language == "ja" && changes == 2, "Japanese region normalizes");
            UiText.Language = "ko_kr";
            Check(UiText.Language == "ko" && changes == 3, "Korean region normalizes");
            UiText.Language = "unsupported";
            Check(UiText.Language == "en" && changes == 4, "unknown codes safely use English");
            Check(UiText.NormalizeLanguage(null) == "en", "null normalization defaults to English");
            Check(UiText.Choices.Select(item => item.Code).SequenceEqual(new[] { "en", "zh-CN", "ja", "ko" }), "stable language choice codes");
            Check(UiText.Choices.Select(item => item.Label).SequenceEqual(new[] { "English", "简体中文", "日本語", "한국어" }), "native names are understandable before switching");
            Check(UiText.Choices.All(item => item.ToString() == item.Label), "choice display does not reveal record fields");
            Check(UiText.Contains("Home") && !UiText.Contains("home") && !UiText.Contains(" Home "), "lookup is exact and case-sensitive");
            Check(!UiText.Contains(null!), "missing lookup is harmless");
            var catalog = UiText.CatalogForTests();
            Check(catalog.Count >= 300, "main interface catalogue is comprehensive");
            string[] required = ["Home", "Languages", "Speakers", "Settings", "Setup", "Advanced", "Interface language",
                "Subtitle appearance", "Font size", "Bubble width", "Background", "Background opacity", "Original text", "Show original text",
                "Show early translations", "Show a translation before the sentence is finished. It may change as more words arrive.",
                "Couldn't show the overlay. Open Logs to read subtitles, or restart subtitles.",
                "Transcript", "Open transcript", "Close transcript", "Copy all", "Copy", "Follow latest", "No subtitles yet.", "Copied", "Couldn't copy text. Try again.",
                "My VRChat messages", "Show translations while I speak", "Adjust sentence timing",
                "Translate my voice into more languages", "Additional language 1", "Additional language 2", "None", "Thai",
                "Choose up to two more languages. Each translation uses a separate line. More languages can add delay and share VRChat's 144-character chat limit.",
                "Updates", "Check for updates", "Checking for updates…", "Open download page",
                "Updates use the full sentence heard so far. Early wording may change. Turn this off to send only finished sentences.",
                "Translate my voice to", "Translate their voices to", "Choose your languages", "Set up SayWhat?", "Quit or keep running?",
                "Who said this?", "For this first example, was only this person speaking clearly?", "Always on top · {0}",
                "Themes", "Theme", "Custom theme", "Background color", "Background image", "Text palette", "Image opacity · {0}%", "← Back to Settings", "Yes", "No", "OK",
                "In progress · Elapsed {0} · {1}", "Still working · no new update for {0}", "Waiting for this step to finish. You can open Troubleshooting for details.",
                "Loading translation model", "Applying settings", "Current task detail", "Task elapsed time and activity", "Model: {0}",
                "Step {0} of {1} · {2}", "Elapsed {0} · {1}", "My voice → {0}: muted in VRChat. {1}",
                "Waiting for VRChat microphone status. If this continues, mute and unmute once in VRChat.",
                "VRChat microphone status is ready.",
                "VRChat microphone muting is ignored.",
                "SayWhat? was installed, but couldn't open automatically. Open it from the Start menu. No translation tools were started."];
            foreach (string key in required) Check(catalog.ContainsKey(key), "required key: " + key);
            foreach (var pair in catalog)
            {
                Check(pair.Value.Count == 3 && pair.Value.All(value => !string.IsNullOrWhiteSpace(value)), "three nonempty translations: " + pair.Key);
                int[] expected = Placeholders(pair.Key);
                foreach (string translation in pair.Value)
                {
                    Check(Placeholders(translation).SequenceEqual(expected), "numbered argument parity: " + pair.Key);
                    // CompositeFormat.Parse validates brace escaping and format syntax without executing an engine.
                    Check(System.Text.CompositeFormat.Parse(translation).MinimumArgumentCount ==
                        System.Text.CompositeFormat.Parse(pair.Key).MinimumArgumentCount, "valid composite formatting: " + pair.Key);
                }
            }
            foreach (var pair in SubtitleSpeechText.TestCatalog)
            {
                Check(pair.Value.Length == 3 && pair.Value.All(value => !string.IsNullOrWhiteSpace(value)),
                    "subtitle speech copy has all interface languages: " + pair.Key);
                foreach (string translation in pair.Value)
                    Check(Placeholders(translation).SequenceEqual(Placeholders(pair.Key)) &&
                        System.Text.CompositeFormat.Parse(translation).MinimumArgumentCount == System.Text.CompositeFormat.Parse(pair.Key).MinimumArgumentCount,
                        "subtitle speech copy keeps valid numbered arguments: " + pair.Key);
            }
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language;
                Check(UiText.T("Thai") == (language switch { "zh-CN" => "泰语", "ja" => "タイ語", "ko" => "태국어", _ => "Thai" }),
                    "Thai target language label has the expected translation: " + language);
                foreach (string key in new[] { "Themes", "← Back to Settings", "Yes", "No", "OK" })
                    if (language != "en" && !(language == "ja" && key == "OK"))
                        Check(UiText.T(key) != key, "themed navigation and confirmation button translated: " + language + "/" + key);
                Check(UiText.T("A friend's unchanged sentence: 今天见！") == "A friend's unchanged sentence: 今天见！", "unknown text fallback");
                Check(UiText.T("SayWhat?") == "SayWhat?", "brand is never translated");
                Check(UiText.T("Hy-MT2 1.8B Q4_K_M") == "Hy-MT2 1.8B Q4_K_M", "model identifiers are never translated");
                Check(UiText.T(@"C:\models\friend.onnx") == @"C:\models\friend.onnx", "paths are never translated");
                Check(UiText.T("") == "", "empty text unchanged");
                string name = "Alice · Home · 東京";
                Check(UiText.Format("Rename {0}", name).Contains(name, StringComparison.Ordinal), "format arguments including UI words are not translated");
                Check(UiText.Format("Step {0} of {1} · {2}", 2, 5, "CUSTOM STAGE").Contains("CUSTOM STAGE", StringComparison.Ordinal), "dynamic stage must be explicitly localized by caller");
                Check(UiText.Format("Literal {{braces}} and {0}", "{UNCHANGED}") == "Literal {braces} and {UNCHANGED}", "escaped braces and arbitrary arguments preserved");
                foreach (var pair in catalog)
                {
                    if (language == "en") Check(UiText.T(pair.Key) == pair.Key, "English source unchanged");
                    else Check(UiText.T(pair.Key) == pair.Value[language == "zh-CN" ? 0 : language == "ja" ? 1 : 2], "selected catalogue column");
                }
                foreach (var pair in SubtitleSpeechText.TestCatalog)
                    Check(SubtitleSpeechText.T(pair.Key) == (language == "en" ? pair.Key : pair.Value[language == "zh-CN" ? 0 : language == "ja" ? 1 : 2]),
                        "subtitle speech copy follows the selected interface language");
            }
            Check(ReferenceEquals(CultureInfo.CurrentCulture, culture) && ReferenceEquals(CultureInfo.CurrentUICulture, uiCulture), "interface switching never changes process-wide cultures");
            return checks;
        }
        finally
        {
            UiText.LanguageChanged -= Changed;
            UiText.Language = prior;
        }
    }

    private static int[] Placeholders(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).OrderBy(index => index).ToArray();
}

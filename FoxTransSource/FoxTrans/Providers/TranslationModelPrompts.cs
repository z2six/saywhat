/// <summary>Dedicated Hy-MT models expect a translation task in one user message, not a general-assistant system prompt.</summary>
public static class TranslationModelPrompts
{
    public static readonly IReadOnlyList<string> AvailableLanguages = Array.AsReadOnly(new[] { "Chinese", "English", "Japanese", "Korean", "Spanish", "French", "German", "Portuguese", "Russian", "Thai" });
    public static bool IsHyMt(string model) => model.Contains("hy-mt", StringComparison.OrdinalIgnoreCase);
    public static bool IsTranslateGemma(string model) => model.Contains("translategemma", StringComparison.OrdinalIgnoreCase);
    public static bool IsLfmEnJp(string model) => model.Contains("lfm2-350m-enjp-mt", StringComparison.OrdinalIgnoreCase);
    public static bool RequiresExplicitSource(string model) => IsTranslateGemma(model) || IsLfmEnJp(model);
    public const int TranslateGemmaMaximumSourceUtf8Bytes = 1024;

    public static string? CanonicalLanguage(string? value)
    {
        string? name = value?.Trim().ToLowerInvariant() switch {
            "zh" or "zh-cn" or "simplified chinese" => "Chinese", "en" => "English", "ja" => "Japanese", "ko" => "Korean",
            "es" => "Spanish", "fr" => "French", "de" => "German", "pt" => "Portuguese", "ru" => "Russian",
            "th" or "th-th" => "Thai", _ => value?.Trim() };
        return AvailableLanguages.FirstOrDefault(language => string.Equals(language, name, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> SupportedTargets(string model, string? source = null) => IsLfmEnJp(model)
        ? Array.AsReadOnly(new[] { "English", "Japanese" }) : AvailableLanguages;

    public static string? ValidateLanguages(string model, string? source, IEnumerable<string> targets)
    {
        if (!RequiresExplicitSource(model)) return null;
        string? sourceName = CanonicalLanguage(source);
        if (sourceName is null) return "Choose a spoken language for this model. Automatic language detection is not supported by this translator.";
        string[] targetNames = targets.Select(target => CanonicalLanguage(target) ?? "").ToArray();
        if (targetNames.Length == 0 || targetNames.Any(target => target.Length == 0)) return "Choose supported translation languages before starting this model.";
        if (IsLfmEnJp(model) && (sourceName is not ("English" or "Japanese") || targetNames.Any(target => target is not ("English" or "Japanese"))))
            return "LFM2 EN–JP supports only English and Japanese. Choose English or Japanese for the spoken language and every translation language, or select another model.";
        return null;
    }

    public static string LanguageCode(string language) => CanonicalLanguage(language) switch {
        "Chinese" => "zh", "English" => "en", "Japanese" => "ja", "Korean" => "ko", "Spanish" => "es", "French" => "fr",
        "German" => "de", "Portuguese" => "pt", "Russian" => "ru", "Thai" => "th", _ => throw new ArgumentException("Unsupported explicit translation language.", nameof(language)) };

    /// <summary>Exact text task emitted by Google's pinned TranslateGemma template for supported text languages.</summary>
    public static string TranslateGemmaUserMessage(string sourceText, string source, string target)
    {
        string sourceName = CanonicalLanguage(source) ?? throw new ArgumentException("TranslateGemma requires an explicit source language.");
        string targetName = CanonicalLanguage(target) ?? throw new ArgumentException("TranslateGemma requires an explicit target language.");
        return $"You are a professional {sourceName} ({LanguageCode(sourceName)}) to {targetName} ({LanguageCode(targetName)}) translator. Your goal is to accurately convey the meaning and nuances of the original {sourceName} text while adhering to {targetName} grammar, vocabulary, and cultural sensitivities.\nProduce only the {targetName} translation, without any additional explanations or commentary. Please translate the following {sourceName} text into {targetName}:\n\n\n{sourceText.Trim()}";
    }

    public static string LfmSystemPrompt(string source, string target)
    {
        string? invalid = ValidateLanguages("lfm2-350m-enjp-mt", source, new[] { target });
        if (invalid is not null) throw new ArgumentException(invalid);
        return CanonicalLanguage(target) == "Japanese" ? "Translate to Japanese." : "Translate to English.";
    }

    public static SpecializedTranslationTask SpecializedTask(string model, string source, string target, string text)
    {
        string? invalid = ValidateLanguages(model, source, new[] { target });
        if (invalid is not null) throw new ArgumentException(invalid);
        if (IsTranslateGemma(model))
        {
            // Conservative byte bound reserves space in Google's 2K context for
            // its task text, role tokens, byte fallback and 256 output tokens.
            // Reject the whole sentence rather than silently clipping its context.
            if (System.Text.Encoding.UTF8.GetByteCount(text) > TranslateGemmaMaximumSourceUtf8Bytes)
                throw new ArgumentException("This sentence is too long for TranslateGemma's small input context. Try shorter sentences or choose Hy-MT2.");
            return new(new[] { new TranslationTaskMessage("user", TranslateGemmaUserMessage(text, source, target)) },
                Temperature: 0, TopP: 1, MinP: null, RepeatPenalty: null);
        }
        if (IsLfmEnJp(model)) return new(new[] { new TranslationTaskMessage("system", LfmSystemPrompt(source, target)), new TranslationTaskMessage("user", text) },
            Temperature: .5, TopP: 1, MinP: .1, RepeatPenalty: 1.05);
        throw new ArgumentException("This model uses the existing generic or Hy-MT adapter.", nameof(model));
    }

    // The pinned llama.cpp release strips custom fields from typed content.
    // Tasks are rendered above and wrapped exactly once by this role-only text
    // template. It is selected only for managed TranslateGemma, not other models.
    public const string TranslateGemmaTextTemplate = "{{ bos_token }}{% for message in messages %}{% if message['role'] != 'user' %}{{ raise_exception('TranslateGemma text translation supports one user message only.') }}{% endif %}{% if loop.index0 != 0 %}{{ raise_exception('TranslateGemma text translation supports one user message only.') }}{% endif %}{{ '<start_of_turn>user\\n' + message['content'] + '<end_of_turn>\\n' }}{% endfor %}{{ '<start_of_turn>model\\n' }}";

    public static string HyMtInstruction(string target, string? glossary = null)
    {
        string references = string.IsNullOrWhiteSpace(glossary) ? "" :
            "Reference the following translations (only when the source terms occur):\n" + glossary[..Math.Min(2000, glossary.Length)] + "\n\n";
        return references + $"Translate the following text into {target}. Note that you should only output the translated result without any additional explanation:";
    }

    public static string HyMtUserMessage(string source, string target, string? glossary = null) =>
        HyMtInstruction(target, glossary) + "\n" + source;

    public static string CasualChatInstruction(string target, string? glossary = null)
    {
        string language = target == "Chinese" ? "Simplified Chinese" : target;
        string terms = string.IsNullOrWhiteSpace(glossary) ? "" :
            " Use this glossary only for matching source terms; do not invent missing words: " + glossary[..Math.Min(2000, glossary.Length)];
        return $"Translate the source speech from any language into concise, natural {language} suitable for casual VRChat conversation. Preserve meaning and tone. Use the language's normal written script, not romanized pronunciation. Output only the translation with no label, explanation, alternatives, or extra text. /no_think" + terms;
    }
}

public sealed record TranslationTaskMessage(string Role, string Content);
public sealed record SpecializedTranslationTask(IReadOnlyList<TranslationTaskMessage> Messages, double Temperature,
    double TopP, double? MinP, double? RepeatPenalty);

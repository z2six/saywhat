namespace IncomingTranslator;

internal sealed record OwnRecognitionChoice(string Id, string Name, string Description, bool SupportsExplicitLanguage)
{
    public bool SupportsSource(string language) => !SupportsExplicitLanguage
        ? string.Equals(language, "Automatic", StringComparison.OrdinalIgnoreCase)
        : string.Equals(language?.Trim(), "Automatic", StringComparison.OrdinalIgnoreCase) ||
            OwnRecognitionCatalog.SupportedSourceLanguages.Contains(AppSettings.CanonicalTranslationLanguage(language) ?? "");
}

internal static class OwnRecognitionCatalog
{
    // Translation targets are not proof of speech-recognition support. In
    // particular, Thai is adaptation-ready, not usable out of the box with the
    // pinned Nemotron model. Keep output additions off microphone selectors.
    public static IReadOnlyList<string> SupportedSourceLanguages { get; } = Array.AsReadOnly(new[]
        { "Chinese", "English", "Japanese", "Korean", "Spanish", "French", "German", "Portuguese", "Russian" });

    public static IReadOnlyList<OwnRecognitionChoice> Choices { get; } = new[]
    {
        new OwnRecognitionChoice("nemotron", "Nemotron", "Continuous recognition with a chosen spoken language. Runs directly on Windows.", true),
        new OwnRecognitionChoice("voxtral", "Voxtral", "The previous continuous recognizer. Detects the spoken language automatically and uses the managed Linux tools.", false)
    };

    public static OwnRecognitionChoice Find(string? id) => Choices.First(choice => choice.Id == (id == "voxtral" ? "voxtral" : "nemotron"));
    public static string LanguageCode(string? language) => AppSettings.CanonicalTranslationLanguage(language) switch
    {
        "Chinese" => "zh-CN", "English" => "en-US", "Japanese" => "ja-JP", "Korean" => "ko-KR",
        "Spanish" => "es-ES", "French" => "fr-FR", "German" => "de-DE", "Portuguese" => "pt-BR", "Russian" => "ru-RU", _ => "auto"
    };
}

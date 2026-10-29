namespace IncomingTranslator;

internal sealed record IncomingRecognitionChoice(string Id, string Name, string Description)
{
    public bool SupportsSource(string? source) => IncomingRecognitionCatalog.SupportsSource(Id, source);
}

// Recognition languages describe the audio model, independently of translation
// targets. A translator supporting Japanese does not make FireRed understand it.
internal static class IncomingRecognitionCatalog
{
    public static IReadOnlyList<IncomingRecognitionChoice> Choices { get; } = Array.AsReadOnly(new[]
    {
        new IncomingRecognitionChoice("firered2-aed", "FireRedASR2 · Accurate", "Chinese and English. More processing to help with difficult speech."),
        new IncomingRecognitionChoice("firered2-ctc", "FireRedASR2 · Fast", "Chinese and English. Lighter recognition for quicker subtitles."),
        new IncomingRecognitionChoice("nemotron", "Nemotron", "Multilingual streaming recognition. Choose a language or let it detect one."),
        new IncomingRecognitionChoice("voxtral", "Voxtral", "Continuous multilingual recognition. Detects the language automatically."),
        new IncomingRecognitionChoice("vibevoice", "VibeVoice", "Groups recognition with separate voice labels. Adds buffering.")
    });

    private static readonly IReadOnlyList<string> Multilingual = Array.AsReadOnly(new[]
        { "Automatic", "Chinese", "English", "Japanese", "Korean", "Spanish", "French", "German", "Portuguese", "Russian" });
    private static readonly IReadOnlyList<string> FireRed = Array.AsReadOnly(new[] { "Chinese", "English" });
    private static readonly IReadOnlyList<string> Automatic = Array.AsReadOnly(new[] { "Automatic" });
    public static string CanonicalEngine(string? id) => id?.Trim().ToLowerInvariant() is string value &&
        value is "vibevoice" or "nemotron" or "firered2-ctc" or "firered2-aed" ? value : "voxtral";
    public static string CanonicalSource(string? source) => AppSettings.CanonicalTranslationLanguage(source) ?? "Automatic";
    public static IncomingRecognitionChoice Find(string? id) => Choices.FirstOrDefault(choice => choice.Id == id) ?? Choices.First(choice => choice.Id == "voxtral");
    public static IReadOnlyList<string> SourceLanguagesFor(string? engine) => UsesFireRed(engine) ? FireRed : engine == "nemotron" ? Multilingual : Automatic;
    public static bool UsesNative(string? engine) => engine is "nemotron" or "firered2-ctc" or "firered2-aed";
    public static bool UsesNative(AppSettings settings) => UsesNative(settings.IncomingSpeechEngine);
    public static bool UsesFireRed(string? engine) => engine is "firered2-ctc" or "firered2-aed";
    public static bool UsesFireRed(AppSettings settings) => UsesFireRed(settings.IncomingSpeechEngine);
    public static bool SupportsSource(string? engine, string? source)
    {
        if (source is null || string.Equals(source.Trim(), "Automatic", StringComparison.OrdinalIgnoreCase))
            return SourceLanguagesFor(engine).Contains("Automatic", StringComparer.Ordinal);
        string? canonical = AppSettings.CanonicalTranslationLanguage(source);
        return canonical is not null && SourceLanguagesFor(engine).Contains(canonical, StringComparer.Ordinal);
    }
    public static string LanguageCode(string? language) => AppSettings.CanonicalTranslationLanguage(language) switch
    {
        "Chinese" => "zh-CN", "English" => "en-US", "Japanese" => "ja-JP", "Korean" => "ko-KR",
        "Spanish" => "es-ES", "French" => "fr-FR", "German" => "de-DE", "Portuguese" => "pt-BR", "Russian" => "ru-RU", _ => "auto"
    };

    // The current NeMo provisioner accepts own-voice-shaped settings. Adapt a
    // detached copy only; incoming preparation never changes the microphone.
    public static AppSettings CreateNativeSettings(AppSettings settings)
    {
        var copy = settings.Copy();
        copy.OwnRecognitionEngine = "nemotron";
        copy.OwnVoiceSourceLanguage = CanonicalSource(settings.IncomingSourceLanguage);
        return copy;
    }
}

/// <summary>Pure model/language validation; never loads models or guesses language from script.</summary>
public static class TranslationModelCapabilities
{
    public static bool IsTranslateGemma(string model) => TranslationModelPrompts.IsTranslateGemma(model);
    public static bool IsLfmEnJp(string model) => TranslationModelPrompts.IsLfmEnJp(model);
    public static bool RequiresExplicitSource(string model) => TranslationModelPrompts.RequiresExplicitSource(model);
    public static bool RequiresManagedRuntime(string model) => TranslationModelPrompts.IsTranslateGemma(model);
    public static IReadOnlyList<string> SupportedTargets(string model, string? source = null) => TranslationModelPrompts.SupportedTargets(model, source);
    public static string? Validate(string model, string? source, IEnumerable<string> targets) => TranslationModelPrompts.ValidateLanguages(model, source, targets);
}

using System.Text;
using System.Text.Json;

/// <summary>Explicit-language, single-turn task adapters for dedicated optional models.</summary>
public sealed class SpecializedTextTranslator : ITextTranslator
{
    private readonly HttpClient _http;
    private readonly ResolvedOpenAiChatSettings _settings;
    private readonly string _source;
    private readonly string _target;

    public SpecializedTextTranslator(HttpClient http, ResolvedOpenAiChatSettings settings, string? source, string? target)
    {
        if (!TranslationModelPrompts.RequiresExplicitSource(settings.Model))
            throw new ArgumentException("This model uses the existing generic or Hy-MT adapter.");
        string? invalid = TranslationModelPrompts.ValidateLanguages(settings.Model, source, new[] { target ?? "" });
        if (invalid is not null) throw new ConfigurationException(invalid);
        _http = http; _settings = settings;
        _source = TranslationModelPrompts.CanonicalLanguage(source)!;
        _target = TranslationModelPrompts.CanonicalLanguage(target)!;
    }

    public async Task<string> TranslateAsync(string sourceText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // An explicit same-language selection needs no inference. This is not
        // script-based autodetection and never silently changes the chosen pair.
        if (_source == _target) return sourceText.Trim();
        SpecializedTranslationTask task = TranslationModelPrompts.SpecializedTask(_settings.Model, _source, _target, sourceText);
        var payload = new OpenAiTextTranslationRequest(_settings.Model,
            task.Messages.Select(message => new OpenAiTextMessage(message.Role, message.Content)).ToArray(),
            Temperature: task.Temperature, TopP: task.TopP, RepeatPenalty: task.RepeatPenalty, MaxTokens: 256, MinP: task.MinP);
        using var request = OpenAiProtocol.Request(_settings.Endpoint, _settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload,
            FoxTransJsonContext.Default.OpenAiTextTranslationRequest), Encoding.UTF8, "application/json");
        string json = await OpenAiProtocol.SendAsync(_http, request, "specialized text translation", cancellationToken);
        return OpenAiProtocol.ChatText(json, "specialized text translation");
    }
}

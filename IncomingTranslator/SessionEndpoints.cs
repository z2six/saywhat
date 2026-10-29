using System.Net;

namespace IncomingTranslator;

// Only resolved session copies contain these addresses. User preferences keep
// manually configured external URLs; dynamic managed addresses are never saved.
internal static class SessionEndpoints
{
    internal static string OwnSpeech(AppSettings settings) => Speech(settings.OwnSpeechEndpoint, settings.AutomaticManagedPorts ? 8080 : settings.OwnSpeechPort);
    internal static string IncomingSpeech(AppSettings settings) => Speech(settings.IncomingSpeechEndpoint, settings.AutomaticManagedPorts ? 8081 : settings.IncomingSpeechPort);
    internal static string OwnTranslation(AppSettings settings) => Translation(settings.OwnTranslationEndpoint, settings.OwnTranslatorBaseUrl);
    internal static string IncomingTranslation(AppSettings settings) => Translation(settings.IncomingTranslationEndpoint, settings.TranslatorBaseUrl);

    private static string Speech(string resolved, int configuredPort) =>
        Validate(string.IsNullOrWhiteSpace(resolved) ? $"http://127.0.0.1:{configuredPort}" : resolved).TrimEnd('/');
    private static string Translation(string resolved, string configured) =>
        Validate(string.IsNullOrWhiteSpace(resolved) ? configured : resolved).TrimEnd('/');

    internal static Uri SpeechUri(AppSettings settings, string path, bool socket = false)
    {
        var uri = new UriBuilder(IncomingSpeech(settings));
        uri.Path = path; uri.Scheme = socket ? "ws" : "http";
        return uri.Uri;
    }

    internal static string Validate(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != "http" ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              IPAddress.TryParse(uri.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip)))
            throw new ArgumentException("Choose a local HTTP loopback connection.", nameof(endpoint));
        return endpoint;
    }

    internal static AppSettings PreserveRuntime(AppSettings current, AppSettings changed)
    {
        AppSettings next = changed.Copy();
        if (string.IsNullOrWhiteSpace(next.OwnSpeechEndpoint)) next.OwnSpeechEndpoint = current.OwnSpeechEndpoint;
        if (string.IsNullOrWhiteSpace(next.IncomingSpeechEndpoint)) next.IncomingSpeechEndpoint = current.IncomingSpeechEndpoint;
        if (string.IsNullOrWhiteSpace(next.OwnTranslationEndpoint)) next.OwnTranslationEndpoint = current.OwnTranslationEndpoint;
        if (string.IsNullOrWhiteSpace(next.IncomingTranslationEndpoint)) next.IncomingTranslationEndpoint = current.IncomingTranslationEndpoint;
        // Groups' legacy configured endpoint remains saved; active clients use
        // the runtime speech address so settings refresh cannot return to the old port.
        return next;
    }
}

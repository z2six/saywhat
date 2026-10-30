namespace IncomingTranslator;

internal sealed partial class RuntimeController
{
    internal IReadOnlyList<SupportServiceTarget> GetSupportServiceTargets()
    {
        var result = new List<SupportServiceTarget>();
        foreach (var pair in _sessionHealth.ToArray())
        {
            if (!pair.Value.Process.IsAlive || !Uri.TryCreate(pair.Value.Endpoint, UriKind.Absolute, out var endpoint)) continue;
            string? component = pair.Key.StartsWith("windows-OwnSpeech-", StringComparison.Ordinal) ? "own-speech"
                : pair.Key.StartsWith("windows-IncomingSpeech-", StringComparison.Ordinal) ? "subtitle-speech"
                : pair.Key.StartsWith("windows-OwnTranslation-", StringComparison.Ordinal) ? "own-translation"
                : pair.Key.StartsWith("windows-IncomingTranslation-", StringComparison.Ordinal) ? "subtitle-translation"
                : pair.Key.StartsWith("groups-", StringComparison.Ordinal) ? "groups-speech"
                : pair.Key.StartsWith("wsl-", StringComparison.Ordinal)
                    ? ReferenceEquals(_ownSpeech?.Process, pair.Value.Process) ? "own-speech" : "subtitle-speech" : null;
            if (component is not null) result.Add(new(component, endpoint));
        }
        // Explicit external translators are deliberately not contacted by a
        // support-report action. Only retained app-owned services are probed.
        return result.Take(8).ToArray();
    }
}

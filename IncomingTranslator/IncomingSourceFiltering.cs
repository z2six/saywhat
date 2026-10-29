namespace IncomingTranslator;

internal sealed partial class IncomingTranslationService
{
    private const string SourceFilteredMessage = "Skipping non-Chinese speech. Still listening…";
    private readonly Dictionary<long, (long Version, DateTimeOffset At)> _excludedSourceRevisions = new();
    private long _sourceFilterEpoch;
    private long _lastChineseOnlyEnabledEpoch = long.MinValue;
    private readonly object _sourceFilterStatusGate = new();
    private DateTimeOffset _lastSourceFilterLogAt = DateTimeOffset.MinValue;
    private string _lastSourcePreview = "";

    // A request produced before an enable still belongs to that restriction even
    // if the user disables it again before a busy translation queue reaches it.
    // New speech after disable uses the new epoch and is unrestricted.
    private bool NeedsChineseSourceFilter(TranslationRequest request) =>
        CurrentSettings().IncomingChineseOnly || request.SourceFilterEpoch < Interlocked.Read(ref _lastChineseOnlyEnabledEpoch);

    private long SourceRevisionVersionLocked(long id) =>
        _excludedSourceRevisions.TryGetValue(id, out var revision) ? revision.Version : 0;

    private bool IsExcludedSourceRevision(TranslationRequest request)
    {
        lock (_latestUtteranceRevisions)
            return _excludedSourceRevisions.TryGetValue(request.UtteranceId, out var revision) &&
                request.SourceRevisionVersion < revision.Version && request.At <= revision.At;
    }

    private void InvalidateExcludedSource(long id, DateTimeOffset at)
    {
        if (id == 0) return; // A wholly excluded sentence never needs a bubble or voice evidence.
        CancellationTokenSource? cancel = null;
        lock (_latestUtteranceRevisions)
        {
            var previous = _excludedSourceRevisions.GetValueOrDefault(id);
            DateTimeOffset through = previous.At > at ? previous.At : at;
            _excludedSourceRevisions[id] = (previous.Version + 1, through);
            if (!_latestUtteranceRevisions.TryGetValue(id, out DateTimeOffset latest) || through > latest)
                _latestUtteranceRevisions[id] = through;
            if (_activeTranslation is TranslationRequest active && active.UtteranceId == id && active.At <= through)
                cancel = _activeTranslationCancellation;
            long oldest = Interlocked.Read(ref _nextUtteranceId) - 250;
            foreach (long old in _excludedSourceRevisions.Keys.Where(value => value < oldest).ToArray())
                _excludedSourceRevisions.Remove(old);
            while (_excludedSourceRevisions.Count > 1000) _excludedSourceRevisions.Remove(_excludedSourceRevisions.Keys.First());
        }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task<bool> AdmitIncomingSourceAsync(string text, long id = 0, DateTimeOffset? at = null)
    {
        IncomingSourceFilterResult result = IncomingSourceFilter.EvaluateChinese(text, CurrentSettings().IncomingChineseOnly);
        if (result.Allowed) return true;
        InvalidateExcludedSource(id, at ?? DateTimeOffset.UtcNow);
        await ReportFilteredSourceAsync(result);
        return false;
    }

    private async Task<bool> AdmitIncomingRequestAsync(TranslationRequest request)
    {
        if (IsExcludedSourceRevision(request)) return false;
        IncomingSourceFilterResult result = IncomingSourceFilter.EvaluateChinese(request.Source, NeedsChineseSourceFilter(request));
        if (result.Allowed) return true;
        InvalidateExcludedSource(request.UtteranceId, request.At);
        await ReportFilteredSourceAsync(result);
        return false;
    }

    private async Task ReportFilteredSourceAsync(IncomingSourceFilterResult result)
    {
        bool log;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_sourceFilterStatusGate)
        {
            log = now - _lastSourceFilterLogAt >= TimeSpan.FromSeconds(15);
            if (log) _lastSourceFilterLogAt = now;
        }
        if (log)
            Log($"Incoming source omitted: reason={result.Reason}; han={result.HanCharacters}; latinLetters={result.LatinLetters}; latinWords={result.LatinWords}; kana={result.KanaCharacters}; hangul={result.HangulCharacters}; otherLetters={result.OtherLetters}.");
        // This is successful recognition of intentionally excluded speech, not a
        // disconnected/stalled decoder. Keep its normal progress and health.
        if (HealthSnapshot.Stage != "source-filtered" || now - HealthSnapshot.UpdatedAt >= TimeSpan.FromSeconds(2))
            await ReportHealthAsync("source-filtered", SourceFilteredMessage, onlyWhenHealthy: true);
    }

    private async Task PublishIncomingSourceAsync(string raw, string? display = null)
    {
        lock (_sourceFilterStatusGate) _lastSourcePreview = raw;
        IncomingSourceFilterResult result = IncomingSourceFilter.EvaluateChinese(raw, CurrentSettings().IncomingChineseOnly);
        if (!result.Allowed)
        {
            await _source(SourceFilteredMessage);
            await ReportFilteredSourceAsync(result);
            return;
        }
        await _source(display ?? raw);
        if (HealthSnapshot.Stage == "source-filtered")
            await ReportHealthAsync(_connected ? "listening" : "waiting", ListeningStatus(CurrentSettings()), onlyWhenHealthy: true);
    }

    private void ApplySourceFilterSettings(AppSettings previous, AppSettings next)
    {
        if (previous.IncomingChineseOnly == next.IncomingChineseOnly) return;
        long epoch = Interlocked.Increment(ref _sourceFilterEpoch);
        if (!next.IncomingChineseOnly)
        {
            _ = RefreshSourceFilterPreviewAsync();
            return;
        }
        Interlocked.Exchange(ref _lastChineseOnlyEnabledEpoch, epoch);
        TranslationRequest? active;
        lock (_latestUtteranceRevisions) active = _activeTranslation;
        string? excludedActive = null;
        if (active is TranslationRequest request && !IncomingSourceFilter.EvaluateChinese(request.Source, true).Allowed)
        {
            excludedActive = request.Source;
            InvalidateExcludedSource(request.UtteranceId, request.At);
        }
        // Updating an option does not reopen the speech model or replay old text.
        // If the currently visible raw preview is excluded, replace it neutrally.
        _ = RefreshSourceFilterPreviewAsync(excludedActive);
    }

    private async Task RefreshSourceFilterPreviewAsync(string? excludedActive = null)
    {
        try
        {
            string raw;
            lock (_sourceFilterStatusGate) raw = excludedActive ?? _lastSourcePreview;
            if (_disposed) return;
            if (!CurrentSettings().IncomingChineseOnly)
            {
                if (HealthSnapshot.Stage == "source-filtered")
                {
                    await _source(ListeningStatus(CurrentSettings()));
                    await ReportHealthAsync("listening", ListeningStatus(CurrentSettings()), onlyWhenHealthy: true);
                }
            }
            else if (raw.Length > 0 &&
                !IncomingSourceFilter.EvaluateChinese(raw, true).Allowed)
            {
                await _source(SourceFilteredMessage);
                await ReportFilteredSourceAsync(IncomingSourceFilter.EvaluateChinese(raw, true));
            }
        }
        catch (Exception error) { Log("Refresh source filter preview failed: " + error.GetType().Name); }
    }
}

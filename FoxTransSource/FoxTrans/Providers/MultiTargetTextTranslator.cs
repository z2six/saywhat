using System.Globalization;

/// <summary>
/// Uses one translator/model at a time for a bounded, ordered set of targets.
/// Dedicated translation models keep their single-target task format. No model
/// is loaded by this adapter, and no intermediate per-language result is sent.
/// </summary>
public sealed class MultiTargetTextTranslator : ITextTranslator
{
    public const int MaximumTargets = 4;
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(7500);
    private readonly IReadOnlyList<(string Language, ITextTranslator Translator)> _targets;
    private readonly TimeSpan _budget;
    private readonly Action<string, string>? _reportFailure;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MultiTargetTextTranslator(IReadOnlyList<(string Language, ITextTranslator Translator)> targets,
        Action<string, string>? reportFailure = null, TimeSpan? budget = null)
    {
        if (targets.Count is < 2 or > MaximumTargets)
            throw new ArgumentException("Multi-language translation requires two or three targets.", nameof(targets));
        if (targets.Any(target => string.IsNullOrWhiteSpace(target.Language) || target.Translator is null) ||
            targets.Select(target => target.Language).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new ArgumentException("Translation targets must be nonempty and distinct.", nameof(targets));
        _targets = targets.ToArray();
        _budget = budget ?? DefaultBudget;
        if (_budget <= TimeSpan.Zero || _budget > DefaultBudget)
            throw new ArgumentOutOfRangeException(nameof(budget));
        _reportFailure = reportFailure;
    }

    public int TargetCount => _targets.Count;

    public async Task<string> TranslateAsync(string sourceText, CancellationToken cancellationToken)
    {
        // A cancelled speculative group must release its slot before the final
        // starts. Targets within a group never contend for parallel GPU slots.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_budget);
            var translated = new List<string>(_targets.Count);
            foreach (var target in _targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                {
                    _reportFailure?.Invoke(target.Language, "group-timeout");
                    continue;
                }
                try
                {
                    Task<string> request = target.Translator.TranslateAsync(sourceText, deadline.Token);
                    // Even a misbehaving custom provider cannot hold the turn
                    // drain indefinitely. Observe any late failure without
                    // persisting arbitrary response/exception text.
                    _ = request.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    string text;
                    try { text = OneLine(await request.WaitAsync(deadline.Token)); }
                    finally
                    {
                        if (deadline.IsCancellationRequested && !request.IsCompleted)
                        {
                            // Let a normal provider finish observing cancellation
                            // before releasing the shared slot to the final. A
                            // broken provider gets only a bounded grace period.
                            try { await request.WaitAsync(TimeSpan.FromMilliseconds(250)); }
                            catch (Exception) { /* The request is observed above; no speech/error body is logged. */ }
                        }
                    }
                    if (string.IsNullOrWhiteSpace(text)) throw new IOException("The target returned no translation.");
                    translated.Add(text);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                { _reportFailure?.Invoke(target.Language, "group-timeout"); }
                catch (Exception error) when (error is OpenAiProviderException or HttpRequestException or IOException or TimeoutException)
                { _reportFailure?.Invoke(target.Language, "provider-failed"); }
            }
            // Parent cancellation means stale work, not permission to publish a
            // partially completed preview/final. Independent target failures may
            // still yield useful text without cancelling all successful targets.
            cancellationToken.ThrowIfCancellationRequested();
            if (translated.Count == 0)
                throw new IOException("None of the selected languages produced a translation within the bounded request budget.");
            return string.Join('\n', translated);
        }
        finally { _gate.Release(); }
    }

    internal static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).Trim();
}

/// <summary>Display-only limits retain a fair portion of every target language.</summary>
public static class MultiLanguageChatFormatter
{
    public static string Format(string translated)
    {
        string[] lines = translated.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(MultiTargetTextTranslator.MaximumTargets)
            .Select(MultiTargetTextTranslator.OneLine).Where(line => line.Length != 0).ToArray();
        if (lines.Length == 0) return "";
        int[][] starts = lines.Select(StringInfo.ParseCombiningCharacters).ToArray();
        int textBudget = VrChatTextFormatter.MaximumTextElements - (lines.Length - 1);
        var allocation = new int[lines.Length];
        // Water-filling: short translations keep their entire line, and unused
        // space is shared among longer ones rather than discarded or allowing a
        // last language to displace the first. Newlines count toward the budget.
        int remaining = textBudget;
        while (remaining > 0)
        {
            bool changed = false;
            for (int index = 0; index < lines.Length && remaining > 0; index++)
                if (allocation[index] < starts[index].Length)
                { allocation[index]++; remaining--; changed = true; }
            if (!changed) break;
        }
        for (int index = 0; index < lines.Length; index++)
        {
            if (allocation[index] >= starts[index].Length) continue;
            int keep = Math.Max(0, allocation[index] - 1);
            // Preserve the newest context consistently with the single-target
            // chat formatter; never split a surrogate pair/combining character.
            lines[index] = keep == 0 ? "…" : "…" + lines[index][starts[index][starts[index].Length - keep]..];
        }
        return string.Join('\n', lines);
    }
}

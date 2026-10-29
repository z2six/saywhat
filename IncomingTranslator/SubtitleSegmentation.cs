namespace IncomingTranslator;

internal sealed record SubtitleSegment(string Text, int SequenceIndex, bool IsCommitted, DateTimeOffset StartedAt);

/// <summary>Turns cumulative ASR into bounded subtitle chunks without resetting recognition.</summary>
internal sealed class SubtitleSegmentation
{
    public int StabilityMilliseconds { get; set; } = 500;
    private static readonly TimeSpan SoftDuration = TimeSpan.FromSeconds(7);
    private string _full = "", _committedPrefix = "", _active = "", _boundaryText = "", _lastPreview = "";
    private int _sequence;
    private DateTimeOffset _changedAt, _startedAt, _boundaryAt;
    private bool _hasActive;
    public string ActiveText => _active;

    public void Reset()
    {
        _full = _committedPrefix = _active = _boundaryText = _lastPreview = "";
        _sequence = 0; _changedAt = _startedAt = _boundaryAt = default; _hasActive = false;
    }

    public void Observe(string cumulative, DateTimeOffset now)
    {
        string next = (cumulative ?? "").Trim();
        if (next == _full) return; // Duplicate events cannot postpone the settle deadline.
        // A delayed older snapshot must not move the committed watermark backwards.
        if (next.Length < _committedPrefix.Length && _full.StartsWith(next, StringComparison.Ordinal)) return;
        int boundary = _committedPrefix.Length;
        if (_committedPrefix.Length > 0 && !next.StartsWith(_committedPrefix, StringComparison.Ordinal))
            boundary = MapCommittedBoundary(_full, next, _committedPrefix.Length, _active);
        boundary = Math.Clamp(boundary, 0, next.Length);
        _committedPrefix = next[..boundary];
        _full = next;
        string suffix = next[boundary..].Trim();
        if (suffix == _active) return; // An old-prefix correction is not new speech.
        if (!_hasActive && suffix.Length > 0) { _startedAt = now; _hasActive = true; }
        _active = suffix; _changedAt = now;
        if (_active.Length == 0) _hasActive = false;
        TrackBoundary(now);
    }

    public bool TryTakeReady(DateTimeOffset now, bool final, TimeSpan settle, out SubtitleSegment segment)
    {
        segment = null!;
        if (settle < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(settle));
        TimeSpan punctuationStable = TimeSpan.FromMilliseconds(Math.Clamp(StabilityMilliseconds, 150, 1500));
        if (_active.Length == 0) return false;
        int end = 0;
        if (final)
        {
            int sentence = SentenceEnds(_active).FirstOrDefault(index => index >= 2);
            int bounded = _active.Length >= 220 || CountCjk(_active) >= 80 ? SoftEnd(_active) : 0;
            end = sentence > 0 && (bounded == 0 || sentence <= bounded) ? sentence
                : bounded > 0 ? bounded : _active.Length;
        }
        else if (_boundaryText.Length >= 2 && now - _boundaryAt >= punctuationStable) end = _boundaryText.Length;
        else if (_active.Length >= 2 && now - _changedAt >= settle) end = _active.Length;
        else if (_active.Length >= 220 || CountCjk(_active) >= 80 || now - _startedAt >= SoftDuration)
            end = SoftEnd(_active);
        if (end > 0 && (end > 220 || CountCjk(_active[..end]) > 80))
            end = Math.Min(end, SoftEnd(_active));
        if (end > 0)
        {
            string text = _active[..end].Trim();
            if (text.Length == 0) return false;
            segment = new(text, _sequence++, true, _startedAt);
            // Source positions remain cumulative even though the displayed/translated text does not.
            int sourceStart = _committedPrefix.Length;
            while (sourceStart < _full.Length && char.IsWhiteSpace(_full[sourceStart])) sourceStart++;
            int through = Math.Min(_full.Length, sourceStart + end);
            while (through < _full.Length && char.IsWhiteSpace(_full[through])) through++;
            _committedPrefix = _full[..through];
            _active = _full[through..].Trim();
            _lastPreview = ""; _hasActive = _active.Length > 0;
            // A retained suffix was already observed at the most recent ASR update.
            _startedAt = _changedAt;
            _boundaryText = ""; TrackBoundary(_changedAt);
            return true;
        }
        // Useful stable previews may revise the same bubble. Tiny word fragments do not
        // generate previews, and only punctuation/settle/soft limits commit a new bubble.
        if ((_active.Length >= 24 || CountCjk(_active) >= 8) && now - _changedAt >= punctuationStable && _lastPreview != _active)
        {
            _lastPreview = _active;
            segment = new(_active, _sequence, false, _startedAt);
            return true;
        }
        return false;
    }

    private void TrackBoundary(DateTimeOffset now)
    {
        int end = SentenceEnds(_active).FirstOrDefault(index => index >= 2);
        string candidate = end == 0 ? "" : _active[..end];
        if (candidate != _boundaryText) { _boundaryText = candidate; _boundaryAt = now; }
    }

    private static IEnumerable<int> SentenceEnds(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current is not ('.' or '!' or '?' or '。' or '！' or '？')) continue;
            if (current == '.' && index > 0 && index + 1 < text.Length &&
                ((char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1])) ||
                 (IsLatin(text[index - 1]) && IsLatin(text[index + 1])))) continue;
            int end = index + 1;
            while (end < text.Length && text[end] is '.' or '!' or '?' or '。' or '！' or '？' or '"' or '\'' or '”' or '’' or '」' or '』' or ')' or ']') end++;
            yield return end;
            index = end - 1;
        }
    }

    private static int SoftEnd(string text)
    {
        int limit = Math.Min(text.Length, 220), cjk = 0;
        for (int index = 0; index < limit; index++)
            if (IsCjk(text[index]) && ++cjk == 80) { limit = index + 1; break; }
        // Prefer a recent clause boundary, then a complete word. CJK needs no spaces.
        int punctuation = 0, whitespace = 0;
        for (int index = 1; index < limit; index++)
        {
            if (text[index] is ',' or ';' or ':' or '，' or '；' or '：' or '.' or '!' or '?' or '。' or '！' or '？') punctuation = index + 1;
            if (char.IsWhiteSpace(text[index])) whitespace = index;
        }
        return punctuation >= 2 ? punctuation : whitespace >= 2 ? whitespace : limit;
    }

    private static int MapCommittedBoundary(string old, string next, int boundary, string active)
    {
        int common = 0;
        while (common < Math.Min(old.Length, next.Length) && old[common] == next[common]) common++;
        if (common >= boundary) return boundary;
        // Prefer surviving text around the old boundary, not absolute offsets that shift
        // when ASR corrects a word in an already-translated sentence.
        foreach (int length in new[] { 48, 24, 12, 6, 4 })
        {
            if (active.Length < length) continue;
            int found = FindNear(next, active[..length], boundary);
            if (found >= 0) return found;
        }
        foreach (int length in new[] { 32, 16, 8 })
        {
            if (boundary < length) continue;
            int found = FindNear(next, old.Substring(boundary - length, length), boundary - length);
            if (found >= 0) return found + length;
        }
        string committed = old[..Math.Min(boundary, old.Length)].TrimEnd();
        int[] oldEnds = SentenceEnds(committed).ToArray();
        if (oldEnds.Length > 0 && oldEnds[^1] == committed.Length)
        {
            int[] nextEnds = SentenceEnds(next).Take(oldEnds.Length).ToArray();
            if (nextEnds.Length == oldEnds.Length) return nextEnds[^1];
        }
        // Bounded prefix edit alignment handles short unpunctuated corrections. This
        // fallback is deliberately limited: pathological full rewrites cannot replay
        // arbitrary history or trigger quadratic work on a long ASR session.
        int start = Math.Max(common, boundary - 192);
        int oldCount = boundary - start;
        int nextCount = Math.Min(384, Math.Max(0, next.Length - start));
        if (oldCount <= 0 || start > next.Length) return Math.Min(boundary, next.Length);
        var previous = new int[nextCount + 1];
        for (int j = 0; j <= nextCount; j++) previous[j] = j;
        for (int i = 1; i <= oldCount; i++)
        {
            var row = new int[nextCount + 1]; row[0] = i;
            for (int j = 1; j <= nextCount; j++)
                row[j] = Math.Min(Math.Min(previous[j] + 1, row[j - 1] + 1), previous[j - 1] + (old[start + i - 1] == next[start + j - 1] ? 0 : 1));
            previous = row;
        }
        int best = Math.Min(oldCount, nextCount);
        for (int j = 0; j <= nextCount; j++)
            if (previous[j] < previous[best] || previous[j] == previous[best] && Math.Abs(j - oldCount) < Math.Abs(best - oldCount)) best = j;
        return start + best;
    }

    private static int FindNear(string source, string anchor, int expected)
    {
        int start = Math.Max(0, expected - 256), end = Math.Min(source.Length, expected + 256 + anchor.Length);
        int best = -1, position = start;
        while (position + anchor.Length <= end)
        {
            int found = source.IndexOf(anchor, position, end - position, StringComparison.Ordinal);
            if (found < 0) break;
            if (best < 0 || Math.Abs(found - expected) < Math.Abs(best - expected)) best = found;
            position = found + 1;
        }
        return best;
    }
    private static bool IsLatin(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsCjk(char value) => value is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af';
    private static int CountCjk(string text) => text.Count(IsCjk);

    internal static int RunSelfTests()
    {
        int checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Subtitle segmentation: " + label); checks++; }
        DateTimeOffset time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        TimeSpan settle = TimeSpan.FromSeconds(1);
        var cut = new SubtitleSegmentation();
        cut.Observe("你好世界。接下来我们去哪里？", time);
        Check(cut.TryTakeReady(time.AddMilliseconds(600), false, settle, out var first) && first.Text == "你好世界。" && first.SequenceIndex == 0 && first.IsCommitted, "CJK sentence without spaces");
        Check(cut.TryTakeReady(time.AddMilliseconds(600), true, settle, out var tail) && tail.Text == "接下来我们去哪里？" && tail.SequenceIndex == 1, "Final returns only uncommitted tail");
        Check(!cut.TryTakeReady(time.AddSeconds(2), true, settle, out _), "Duplicate final emits nothing");
        cut.Reset(); cut.Observe("This is a sufficiently long unfinished statement", time);
        Check(cut.TryTakeReady(time.AddMilliseconds(600), false, settle, out var preview) && !preview.IsCommitted, "Stable meaningful preview");
        cut.Observe("This is a sufficiently long corrected statement", time.AddMilliseconds(700));
        Check(cut.TryTakeReady(time.AddMilliseconds(1300), false, settle, out var revision) && revision.SequenceIndex == preview.SequenceIndex && !revision.IsCommitted && revision.Text.Contains("corrected"), "Active correction revises same sequence");
        cut.Observe("This is a sufficiently long corrected statement", time.AddMilliseconds(1400));
        Check(cut.TryTakeReady(time.AddMilliseconds(1800), false, settle, out var completed) && completed.IsCommitted && completed.SequenceIndex == preview.SequenceIndex, "Duplicate cumulative does not defer requested settle");
        cut.Reset(); cut.Observe("Hello world. Here is the next sentence", time);
        Check(cut.TryTakeReady(time.AddMilliseconds(600), false, settle, out _), "Initial committed sentence");
        cut.Observe("Hello wonderful world. Here is the next corrected sentence", time.AddMilliseconds(700));
        Check(cut.ActiveText == "Here is the next corrected sentence", "Old-prefix rewrite does not replay committed text");
        Check(cut.TryTakeReady(time.AddMilliseconds(800), true, settle, out var corrected) && corrected.SequenceIndex == 1 && corrected.Text == cutPrefix(), "Corrected final tail only");
        cut.Reset(); cut.Observe(new string('中', 100), time);
        Check(cut.TryTakeReady(time, false, settle, out var longCjk) && longCjk.Text.Length == 80, "Long continuous CJK soft cut");
        cut.Observe(new string('中', 110), time.AddMilliseconds(100));
        Check(cut.ActiveText.Length == 30 && cut.TryTakeReady(time.AddMilliseconds(100), true, settle, out var longTail) && longTail.Text.Length == 30, "Continuous append excludes committed prefix");
        cut.Reset(); cut.Observe("Short tail", time); cut.Observe("Short tail", time.AddMilliseconds(900));
        Check(cut.TryTakeReady(time.AddMilliseconds(1100), false, settle, out var shortTail) && shortTail.IsCommitted, "Short unpunctuated text commits after quiet");
        cut.Observe("Short tail with a new thought", time.AddMilliseconds(1200));
        Check(cut.ActiveText == "with a new thought", "New words after quiet start the next bubble");
        cut.Reset(); cut.Observe(new string('x', 500), time);
        var finalParts = new List<SubtitleSegment>();
        while (cut.TryTakeReady(time, true, settle, out var part)) finalParts.Add(part);
        Check(finalParts.Count == 3 && finalParts.All(part => part.IsCommitted && part.Text.Length <= 220)
            && string.Concat(finalParts.Select(part => part.Text)) == new string('x', 500), "A giant final-only transcript is drained in bounded chunks without losing text");
        cut.Reset(); cut.Observe(new string('x', 500) + "!", time);
        Check(cut.TryTakeReady(time.AddMilliseconds(600), false, settle, out var longPunctuation)
            && longPunctuation.Text.Length <= 220, "A stable distant punctuation boundary cannot bypass the size bound");
        cut.Reset(); cut.Observe(new string('x', 500), time);
        Check(cut.TryTakeReady(time.AddMilliseconds(1500), false, settle, out var longQuiet)
            && longQuiet.Text.Length <= 220, "A quiet bulk transcript cannot bypass the size bound");
        cut.Reset();
        var continuous = new List<SubtitleSegment>();
        string cumulative = "";
        for (int index = 0; index < 1500; index++) // Five minutes of continuously growing recognition.
        {
            cumulative += "abcd";
            DateTimeOffset tick = time.AddMilliseconds(index * 200);
            cut.Observe(cumulative, tick);
            while (cut.TryTakeReady(tick, false, settle, out var part)) if (part.IsCommitted) continuous.Add(part);
        }
        while (cut.TryTakeReady(time.AddMinutes(5), true, settle, out var part)) continuous.Add(part);
        Check(continuous.Count > 20 && continuous.All(part => part.Text.Length <= 220)
            && continuous.Select(part => part.SequenceIndex).Distinct().Count() == continuous.Count
            && string.Concat(continuous.Select(part => part.Text)) == cumulative, "Five-minute continuous stream reconstructs exactly with unique bounded committed chunks");
        cut.Reset(); cut.Observe("First sentence. Remaining words", time);
        Check(cut.TryTakeReady(time.AddMilliseconds(600), false, settle, out _), "Stale-snapshot setup commits its first sentence");
        cut.Observe("First", time.AddMilliseconds(700));
        Check(cut.ActiveText == "Remaining words", "Stale short snapshot cannot shrink the committed watermark");
        cut.Observe("First sentence. Remaining words grow", time.AddMilliseconds(800));
        Check(cut.TryTakeReady(time.AddMilliseconds(800), true, settle, out var afterStale)
            && afterStale.SequenceIndex == 1 && afterStale.Text == "Remaining words grow", "Growth after stale snapshot does not replay committed source");
        return checks;
        static string cutPrefix() => "Here is the next corrected sentence";
    }
}

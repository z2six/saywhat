// Some recognizers expose their silence or tokenizer-control symbols as text.
// Suppress only a whole control-only result: never remove substrings or rewrite
// ordinary/mixed speech. In particular, lower-case "sil" and the name "Sil" are
// not the unwrapped SIL marker. FireRed's pinned token list includes <sil>,
// <blank>, <unk>, <pad>, <sos> and <eos>.
public static class RecognitionTextFilter
{
    public static bool IsControlOnly(string? text) => ControlTokenCount(text) != 0;

    public static string CleanControlOnly(string? text) =>
        text is null || IsControlOnly(text) ? "" : text;

    public static int ControlTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        ReadOnlySpan<char> remaining = text.AsSpan();
        int count = 0;
        while (!remaining.IsEmpty)
        {
            while (!remaining.IsEmpty && IsSeparator(remaining[0])) remaining = remaining[1..];
            if (remaining.IsEmpty) return count;
            if (remaining.StartsWith("SIL", StringComparison.Ordinal)) remaining = remaining[3..];
            else if (remaining[0] is '[' or '<')
            {
                bool square = remaining[0] == '[';
                int end = remaining.IndexOf(square ? ']' : '>');
                if (end is < 2 or > 40) return 0;
                ReadOnlySpan<char> label = remaining[1..end].Trim();
                if (!(square ? SquareControl(label) : AngleControl(label))) return 0;
                remaining = remaining[(end + 1)..];
            }
            else return 0;
            count++;
        }
        return count;
    }

    private static bool SquareControl(ReadOnlySpan<char> label) =>
        Equal(label, "sil") || Equal(label, "silence") || Equal(label, "blank_audio") ||
        Equal(label, "no_speech") || Equal(label, "no speech");

    private static bool AngleControl(ReadOnlySpan<char> label) =>
        Equal(label, "sil") || Equal(label, "silence") || Equal(label, "blank") ||
        Equal(label, "unk") || Equal(label, "pad") || Equal(label, "sos") || Equal(label, "eos") ||
        Equal(label, "sos/eos") || Equal(label, "s") || Equal(label, "/s") ||
        Equal(label, "eps") || Equal(label, "epsilon") || Equal(label, "|nospeech|") ||
        Equal(label, "|endoftext|") || Equal(label, "|startoftranscript|");

    private static bool Equal(ReadOnlySpan<char> label, string expected) => label.Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static bool IsSeparator(char value) => char.IsWhiteSpace(value) ||
        value is '.' or ',' or '!' or '?' or ':' or ';' or '-' or '–' or '—' or '…' or '，' or '。' or '！' or '？' or '：' or '；' or '、' or '\u200b' or '\ufeff';
}

using System.Globalization;
using System.Text;

namespace IncomingTranslator;

internal enum IncomingSourceFilterReason
{
    Disabled,
    ChineseCompatible,
    Empty,
    TooLong,
    InvalidUnicode,
    NoChineseText,
    OtherScript,
    PredominantlyLatin
}

internal readonly record struct IncomingSourceFilterResult(
    bool Allowed,
    IncomingSourceFilterReason Reason,
    int HanCharacters = 0,
    int LatinLetters = 0,
    int LatinWords = 0,
    int KanaCharacters = 0,
    int HangulCharacters = 0,
    int OtherLetters = 0);

/// <summary>
/// An optional, bounded text eligibility check, not a language detector. Han is
/// shared by several languages: Han-only Japanese, for example, cannot reliably
/// be distinguished from Chinese here. The recognizer's language setting remains
/// responsible for recognition. This check only declines incompatible output;
/// it never changes, translates or guesses missing words.
/// </summary>
internal static class IncomingSourceFilter
{
    internal const int MaximumTranscriptLength = 16_384;

    internal static IncomingSourceFilterResult EvaluateChinese(string? transcript, bool enabled)
    {
        // Opting out must preserve every existing recognition path, including
        // its own empty/control/size handling. No scan or allocation is needed.
        if (!enabled) return new(true, IncomingSourceFilterReason.Disabled);
        if (transcript is null || transcript.Length == 0) return new(false, IncomingSourceFilterReason.Empty);
        if (transcript.Length > MaximumTranscriptLength) return new(false, IncomingSourceFilterReason.TooLong);
        if (string.IsNullOrWhiteSpace(transcript)) return new(false, IncomingSourceFilterReason.Empty);

        int han = 0, latin = 0, words = 0, kana = 0, hangul = 0, other = 0;
        bool latinWord = false, invalid = false;
        foreach (Rune rune in transcript.EnumerateRunes())
        {
            int value = rune.Value;
            if (IsHanIdeograph(value)) { han++; latinWord = false; continue; }
            if (IsKana(value)) { kana++; latinWord = false; continue; }
            if (IsHangul(value)) { hangul++; latinWord = false; continue; }
            if (IsLatinLetter(rune))
            {
                latin++;
                if (!latinWord) words++;
                latinWord = true;
                continue;
            }
            if (value == Rune.ReplacementChar.Value) invalid = true;
            if (Rune.IsLetter(rune)) { other++; latinWord = false; continue; }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            // Accents, variation selectors and invisible format marks do not
            // manufacture extra words or increase the Chinese evidence. Digits
            // and internal punctuation keep names such as VRChat2/U.S./O'Neil
            // together; ordinary spaces and other punctuation end a word.
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark or UnicodeCategory.Format || Rune.IsDigit(rune) ||
                value is '\'' or '\u2019' or '-' or '.') continue;
            latinWord = false;
        }

        IncomingSourceFilterResult Result(bool allowed, IncomingSourceFilterReason reason) =>
            new(allowed, reason, han, latin, words, kana, hangul, other);
        if (invalid) return Result(false, IncomingSourceFilterReason.InvalidUnicode);
        if (kana > 0 || hangul > 0 || other > 0) return Result(false, IncomingSourceFilterReason.OtherScript);
        if (han == 0) return Result(false, IncomingSourceFilterReason.NoChineseText);

        // Chinese replies may be a single character. Allow short names, common
        // app names and code-switching, without letting a tiny Han suffix turn
        // a largely Latin transcript into an eligible Chinese sentence. Both
        // budgets matter: a long unbroken junk token and many tiny words fail.
        int latinLetterBudget = Math.Max(6, 3 * han);
        int latinWordBudget = Math.Max(1, (han + 1) / 2);
        if (latin > latinLetterBudget || words > latinWordBudget)
            return Result(false, IncomingSourceFilterReason.PredominantlyLatin);
        return Result(true, IncomingSourceFilterReason.ChineseCompatible);
    }

    // Assigned Han ideographs (Unicode 17), rather than UTF-16 code units or a
    // broad supplementary-plane range. Includes traditional/compatibility name
    // forms and extensions through J. Script data: unicode.org/Public/17.0.0/ucd/Scripts.txt.
    private static bool IsHanIdeograph(int value) => value == 0x3007 || value is
        >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFA6D or >= 0xFA70 and <= 0xFAD9 or
        >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B81D or
        >= 0x2B820 and <= 0x2CEAD or >= 0x2CEB0 and <= 0x2EBE0 or
        >= 0x2EBF0 and <= 0x2EE5D or >= 0x2F800 and <= 0x2FA1D or
        >= 0x30000 and <= 0x3134A or >= 0x31350 and <= 0x33479;

    private static bool IsKana(int value) => value is
        >= 0x3041 and <= 0x3096 or >= 0x309D and <= 0x309F or
        >= 0x30A1 and <= 0x30FA or >= 0x30FD and <= 0x30FF or
        >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F or
        >= 0x1AFF0 and <= 0x1AFFF or >= 0x1B000 and <= 0x1B16F;

    private static bool IsHangul(int value) => value is
        >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or
        >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF or
        >= 0xFFA0 and <= 0xFFDC;

    private static bool IsLatinLetter(Rune rune)
    {
        int value = rune.Value;
        return Rune.IsLetter(rune) && (value is
            >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= 0x00C0 and <= 0x02AF or >= 0x1E00 and <= 0x1EFF or
            >= 0x2C60 and <= 0x2C7F or >= 0xA720 and <= 0xA7FF or
            >= 0xAB30 and <= 0xAB6F or >= 0xFF21 and <= 0xFF3A or
            >= 0xFF41 and <= 0xFF5A);
    }
}

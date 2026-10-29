namespace IncomingTranslator;

internal static class ChineseSubtitleFilterTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Chinese subtitle eligibility: " + detail); checks++; }
        IncomingSourceFilterResult Evaluate(string? text) => IncomingSourceFilter.EvaluateChinese(text, enabled: true);
        foreach (string text in new[] { "好", "嗯", "哦。", "哈哈", "是的", "这个故事听起来很有意思。", "這個翻譯很好。",
            "請問陳小姐在哪裡？", "我叫 John", "我爱 VRChat", "嗯 OK", "这个 map 很 cool", "我用 AI 翻译", "我在玩 Discord", "我的朋友叫 José",
            "我的朋友叫 Jose\u0301", "他叫 O'Neil", "我的朋友叫 Ｊｏｈｎ", "今天 3 点去吗？", "你好🙂！", "二〇二六年", "你好\uFE0F" })
        {
            IncomingSourceFilterResult result = Evaluate(text);
            Check(result.Allowed && result.Reason == IncomingSourceFilterReason.ChineseCompatible && result.HanCharacters > 0,
                "Chinese replies, names and brief code-switching remain eligible: " + text);
            Check(IncomingSourceFilter.EvaluateChinese(text, enabled: false).Reason == IncomingSourceFilterReason.Disabled,
                "filter remains an explicit opt-in");
        }
        foreach (string text in new[] { "Hello there.", "Luma zeno bari navo kala tero sumi fena rido nali.", "VRChat", "nǐ hǎo", "123 ... 🙂" })
            Check(!Evaluate(text).Allowed && Evaluate(text).Reason == IncomingSourceFilterReason.NoChineseText,
                "all-Latin speech/romanized noise and non-letter symbols cannot establish Chinese: " + text);
        foreach (string text in new[] { "hello there 好", "This is a long confused transcription 嗯", "好 this is a long confused transcription",
            "abcdefghijklmnop好", "你 ab cd ef", "你 a b", "hello there 1234567890 !!!!!!!!! 好", "abcdefghijklmno中文",
            "luma zeno bari navo kala tero sumi fena rido nali 谢谢", "你好 abcdefg",
            "STATICSTATICWORDWORDUNRECOGNIZEDNOISE呢小星星" })
            Check(!Evaluate(text).Allowed && Evaluate(text).Reason == IncomingSourceFilterReason.PredominantlyLatin,
                "tiny Chinese additions do not admit predominantly Latin text: " + text);
        foreach (string text in new[] { "こんにちは", "今日はいい天気", "안녕하세요", "这是こんにちは", "你好 안녕", "中文 ไทย", "你好 Привет", "你好 Ελληνικά",
            "\uFF76\uFF80\uFF76\uFF85", "你好 \U0001B001", "你好 \u1100", "你好 \uFFA1" })
            Check(!Evaluate(text).Allowed && Evaluate(text).Reason == IncomingSourceFilterReason.OtherScript,
                "incompatible letter scripts do not pass the Chinese-only option: " + text);

        Check(Evaluate("你 abcdef").Allowed && Evaluate("你 abcdef").LatinLetters == 6 && Evaluate("你 abcdef").LatinWords == 1 &&
            !Evaluate("你 abcdefg").Allowed, "one-character reply has a six-letter/one-word code-switch budget");
        Check(Evaluate("你好 abcd").Allowed && !Evaluate("你好 ab cd").Allowed,
            "many short Latin words cannot evade the separate word budget");
        Check(Evaluate("你好吗 abc def").Allowed && !Evaluate("你好吗 abc defg hij").Allowed,
            "larger Chinese contexts increase both bounded budgets at their documented thresholds");
        Check(Evaluate("我的朋友来自 U.S.").LatinWords == 1 && Evaluate("他叫 O'Neil").LatinWords == 1 &&
            Evaluate("我玩 VRChat2").LatinWords == 1, "name connectors and digits do not manufacture extra Latin word runs");
        Check(Evaluate("你好\u200B🙂").HanCharacters == 2 && Evaluate("你好\u0301").HanCharacters == 2 &&
            !Evaluate("hello\u200Bthere好").Allowed, "emoji/format/combining marks add no Chinese evidence and do not hide Latin letters");

        foreach (int codePoint in new[] { 0x3400, 0x9FFF, 0xF900, 0xFA70, 0x20000, 0x2A700, 0x2B820, 0x2CEB0,
            0x2EBF0, 0x2F800, 0x30000, 0x31350, 0x323B0, 0x33479 })
        {
            string character = char.ConvertFromUtf32(codePoint);
            IncomingSourceFilterResult result = Evaluate(character);
            Check(result.Allowed && result.HanCharacters == 1 && result.LatinLetters == 0,
                "each assigned supplementary/traditional Han scalar counts once, including beyond the runtime's old Unicode tables");
        }
        Check(Evaluate("\U00020000\U0002F800").HanCharacters == 2 && "\U00020000\U0002F800".Length == 4,
            "supplementary Han counts scalars rather than UTF-16 surrogate halves");
        Check(Evaluate("東京").Allowed && Evaluate("大学").Allowed,
            "Han-only languages are explicitly ambiguous; this script heuristic does not falsely claim language identification");
        foreach (string text in new[] { "\uD840", "\uDC00", "你好\uD840", "你好\uFFFD" })
            Check(!Evaluate(text).Allowed && Evaluate(text).Reason == IncomingSourceFilterReason.InvalidUnicode,
                "malformed/replacement text fails safely without throwing");
        foreach (string? text in new string?[] { null, "", " \r\n\t" })
            Check(!Evaluate(text).Allowed && Evaluate(text).Reason == IncomingSourceFilterReason.Empty,
                "empty input does not become a subtitle candidate");
        string maximum = new('好', IncomingSourceFilter.MaximumTranscriptLength);
        Check(Evaluate(maximum).Allowed && Evaluate(maximum).HanCharacters == maximum.Length,
            "maximum-size text receives a bounded linear scan");
        Check(Evaluate(maximum + "好").Reason == IncomingSourceFilterReason.TooLong && Evaluate(maximum + "好").HanCharacters == 0,
            "over-limit text is declined before scanning, never truncated into another sentence");
        Check(Evaluate(new string(' ', IncomingSourceFilter.MaximumTranscriptLength + 1)).Reason == IncomingSourceFilterReason.TooLong,
            "even oversized whitespace is rejected before an unbounded empty-text scan");
        string supplementaryMaximum = string.Concat(Enumerable.Repeat("\U00020000", IncomingSourceFilter.MaximumTranscriptLength / 2));
        Check(Evaluate(supplementaryMaximum).Allowed && Evaluate(supplementaryMaximum).HanCharacters == IncomingSourceFilter.MaximumTranscriptLength / 2,
            "the work bound is measured safely in UTF-16 length while evidence is counted in Unicode scalars");
        foreach (string? text in new string?[] { null, "", "Hello.", "今日は", "你好\uD840", maximum + maximum })
        {
            IncomingSourceFilterResult disabled = IncomingSourceFilter.EvaluateChinese(text, enabled: false);
            Check(disabled.Allowed && disabled.Reason == IncomingSourceFilterReason.Disabled && disabled.HanCharacters == 0,
                "opt-out bypasses the heuristic for every language and oversized/malformed input");
        }
        return checks;
    }
}

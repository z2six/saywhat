using System.Text.Json;

namespace IncomingTranslator;

internal static class GroupSpeechProtocolTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Group speech protocol check failed: " + message);
            checks++;
        }
        void Reject(Func<object?> operation, string message)
        {
            bool rejected = false;
            try { operation(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or JsonException) { rejected = true; }
            Check(rejected, message);
        }
        string Envelope(object segments, object? start = null, object? end = null, object? epoch = null, object? index = null,
            object? timestamps = null, object? session = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "transcript.chunk", ["session_id"] = session ?? "local-fixture", ["epoch"] = epoch ?? 0,
            ["chunk_index"] = index ?? 0, ["audio_start_ms"] = start ?? 0, ["audio_end_ms"] = end ?? 2900,
            ["word_timestamps"] = timestamps ?? false, ["segments"] = segments
        });
        object[] valid = [new { voice_id = "00", text = "  你好。  " }, new { voice_id = "1", text = "Hello." }];
        GroupTranscriptChunk chunk = GroupSpeechProtocol.Parse(Envelope(valid))!;
        Check(chunk.Segments.Count == 2 && chunk.Segments[0] == new GroupVoiceText("0", "你好。"),
            "text is trimmed and temporary voice IDs are canonical, not permanent names");
        Check(chunk.AudioStartMilliseconds == 0 && chunk.AudioEndMilliseconds == 2900 &&
            GroupSpeechProtocol.Key(chunk, "0") != GroupSpeechProtocol.Key(chunk with { Epoch = 1 }, "0"),
            "chunk audio spans and session-scoped identity epochs stay distinct");
        Check(GroupSpeechProtocol.Parse("{\"type\":\"session.ready\"}") is null, "non-transcript protocol messages are harmless");
        GroupTranscriptChunk unidentified = GroupSpeechProtocol.Parse(Envelope(new[] { new { voice_id = (string?)null, text = "Still useful speech." } }))!;
        Check(unidentified.Segments.Single().VoiceId == GroupSpeechProtocol.UnidentifiedVoiceId &&
            GroupSpeechProtocol.VoiceLabel(GroupSpeechProtocol.Key(unidentified, unidentified.Segments.Single().VoiceId)) == "Voice",
            "honest unlabelled output is translated anonymously rather than forcing a reconnect or guessing a person");
        Check(GroupSpeechProtocol.Endpoint("http://localhost:8082/").AbsoluteUri == "http://localhost:8082/health" &&
            GroupSpeechProtocol.Endpoint("http://127.0.0.1:8082", true).Scheme == "ws", "health and streaming URLs remain local");
        Reject(() => GroupSpeechProtocol.Endpoint("http://example.com"), "remote endpoints cannot receive captured voice");
        Reject(() => GroupSpeechProtocol.Endpoint("http://user:password@localhost:8082"), "embedded credentials are not accepted");
        Reject(() => GroupSpeechProtocol.Endpoint("https://localhost:8082"), "unsupported endpoint schemes fail clearly");
        Reject(() => GroupSpeechProtocol.Parse("[]"), "a non-object message is rejected");
        Reject(() => GroupSpeechProtocol.Parse("{}"), "missing protocol type is rejected");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, session: "")), "empty session IDs are rejected");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, session: 3)), "numeric session IDs cannot accidentally become identities");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, epoch: -1)), "negative recognition epochs are rejected");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, epoch: "0")), "numeric fields cannot masquerade as strings");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, index: -1)), "negative chunk indices are rejected");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, start: -1)), "negative audio bounds are rejected");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, start: 2900, end: 2900)), "empty audio spans cannot become clean evidence");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, end: 10_001)), "oversized input spans are rejected");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, timestamps: true)), "the adapter never invents per-word timestamps");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(valid, timestamps: "yes")), "unsupported timestamp claims are rejected even if malformed");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(new { voice_id = 0, text = "wrong list shape" })), "segments must be an array");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(new[] { new { voice_id = -1, text = "bad" } })), "negative voice ordinals are rejected");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(new[] { new { voice_id = 256, text = "bad" } })), "voice ordinals have a bounded namespace");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(new[] { new { voice_id = true, text = "bad" } })), "non-numeric voice labels are rejected");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(new[] { new { voice_id = 0, text = 123 } })), "transcript text must be text");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(Enumerable.Range(0, 257).Select(_ => new { voice_id = 0, text = "" }).ToArray())),
            "empty segments count toward the item bound too");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(new[] { new { voice_id = 0, text = new string('x', 16_001) } })), "individual text size is bounded");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(Enumerable.Range(0, 5).Select(index => new { voice_id = index, text = new string('x', 16_000) }).ToArray())),
            "aggregate text size is bounded");
        Reject(() => GroupSpeechProtocol.Parse(new string(' ', 1_000_001)), "a message is bounded before parsing");
        Reject(() => GroupSpeechProtocol.Parse("{\"type\":\"error\",\"message\":\"A local model failed\"}"), "server errors propagate instead of appearing as an empty transcript");

        GroupTranscriptChunk musicOnly = GroupSpeechProtocol.Parse(Envelope(new[] { new { voice_id = 0, text = "[Music][Music]" } },
            start: 300, end: 3200, epoch: 2, index: 7, session: "sound-fixture"))!;
        Check(musicOnly.Segments.Count == 0 && musicOnly.NonSpeechAnnotationCount == 2 && musicOnly.HasNonSpeechAnnotations,
            "the screenshot's adjacent music labels become an empty timed chunk, not two subtitles");
        Check(musicOnly.SessionId == "sound-fixture" && musicOnly.Epoch == 2 && musicOnly.ChunkIndex == 7 &&
            musicOnly.AudioStartMilliseconds == 300 && musicOnly.AudioEndMilliseconds == 3200,
            "removing sound annotations preserves all original audio and session provenance");
        GroupTranscriptChunk laughterAndSpeech = GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = "[Laughter] Or even sign up for this home. This home, and how?" } }))!;
        Check(laughterAndSpeech.Segments.Single().Text == "Or even sign up for this home. This home, and how?" &&
            laughterAndSpeech.NonSpeechAnnotationCount == 1,
            "laughter beside real words is removed without dropping the spoken sentence");
        GroupTranscriptChunk caseVariants = GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = "[mUsIc] [LAUGHTER]" } }))!;
        Check(caseVariants.Segments.Count == 0 && caseVariants.NonSpeechAnnotationCount == 2,
            "known complete sound labels are case-insensitive");
        GroupTranscriptChunk wordBoundary = GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = "before[Music]after" } }))!;
        Check(wordBoundary.Segments.Single().Text == "before after",
            "removing an annotation cannot join neighboring spoken words into a new word");
        GroupTranscriptChunk punctuationOnly = GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = "[Music] ... [Laughter] !" } }))!;
        Check(punctuationOnly.Segments.Count == 0 && punctuationOnly.NonSpeechAnnotationCount == 2,
            "punctuation surrounding sound-only labels does not become an empty-looking subtitle");
        GroupTranscriptChunk annotatedLyrics = GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = "[Music]Here are the actual sung lyrics." } }))!;
        Check(annotatedLyrics.Segments.Single().Text == "Here are the actual sung lyrics.",
            "a music label does not delete the real lyrics that follow it");
        GroupTranscriptChunk localizedSounds = GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = "[音乐][笑聲][音楽][웃음]" } }))!;
        Check(localizedSounds.Segments.Count == 0 && localizedSounds.NonSpeechAnnotationCount == 4,
            "known Mandarin, Japanese and Korean sound labels are removed consistently");
        GroupTranscriptChunk hiddenOtherVoice = GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = "Keep this spoken sentence." }, new { voice_id = 1, text = "[Music]" } }))!;
        Check(hiddenOtherVoice.Segments.Count == 1 && hiddenOtherVoice.Segments[0].VoiceId == "0" &&
            hiddenOtherVoice.HasNonSpeechAnnotations,
            "a removed second voice leaves chunk-level evidence that the waveform was not clean solo speech");
        string[] ordinaryText = ["I like music and laughter.", "[Alice] says hello.", "[bracketed unknown words]",
            "你好，这是歌词。", "音楽が好きです。", "음악을 좋아해요.", "É.", "[Music is part of the title]", "[Music"];
        foreach (string text in ordinaryText)
        {
            GroupTranscriptChunk ordinary = GroupSpeechProtocol.Parse(Envelope(new[] { new { voice_id = 0, text } }))!;
            Check(ordinary.Segments.Single().Text == text && !ordinary.HasNonSpeechAnnotations,
                "ordinary speech, names, lyrics, unknown labels and incomplete brackets stay intact: " + text);
        }
        GroupTranscriptChunk multilingual = GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = "[Laughter]你好，朋友。[Music]안녕하세요。" } }))!;
        Check(multilingual.Segments.Single().Text == "你好，朋友。 안녕하세요。" && multilingual.NonSpeechAnnotationCount == 2,
            "known labels do not consume adjacent Mandarin or Korean words");
        GroupTranscriptChunk filteredAgain = GroupSpeechProtocol.FilterNonSpeechAnnotations(multilingual);
        Check(filteredAgain.NonSpeechAnnotationCount == multilingual.NonSpeechAnnotationCount &&
            filteredAgain.Segments.SequenceEqual(multilingual.Segments) && filteredAgain.SessionId == multilingual.SessionId &&
            filteredAgain.Epoch == multilingual.Epoch && filteredAgain.ChunkIndex == multilingual.ChunkIndex &&
            filteredAgain.AudioStartMilliseconds == multilingual.AudioStartMilliseconds && filteredAgain.AudioEndMilliseconds == multilingual.AudioEndMilliseconds,
            "defensive repeated filtering is idempotent and preserves annotation evidence");
        GroupTranscriptChunk filteredEmptyAgain = GroupSpeechProtocol.FilterNonSpeechAnnotations(musicOnly);
        Check(filteredEmptyAgain.Segments.Count == 0 && filteredEmptyAgain.NonSpeechAnnotationCount == 2,
            "an already empty annotated chunk remains timed and annotated after a second filter");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(Enumerable.Range(0, 257)
            .Select(_ => new { voice_id = 0, text = "[Music]" }).ToArray())),
            "stripping annotations cannot bypass the raw segment count limit");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(new[]
            { new { voice_id = 0, text = string.Concat(Enumerable.Repeat("[Music]", 2286)) } })),
            "stripping annotations cannot bypass the original individual text size limit");
        Reject(() => GroupSpeechProtocol.Parse(Envelope(Enumerable.Range(0, 5)
            .Select(index => new { voice_id = index, text = string.Concat(Enumerable.Repeat("[Music]", 2000)) }).ToArray())),
            "stripping annotations cannot bypass the original aggregate text size limit");
        checks += await IncomingTranslationService.RunGroupRoutingOfflineChecksAsync(Path.Combine(Path.GetFullPath(directory), "routing")).ConfigureAwait(false);
        checks += await IncomingTranslationService.RunGroupSoundLabelOfflineChecksAsync(Path.Combine(Path.GetFullPath(directory), "sound-labels")).ConfigureAwait(false);
        Console.WriteLine($"PASS: {checks} Groups protocol and routing checks; no models, audio capture or network.");
        return checks;
    }
}

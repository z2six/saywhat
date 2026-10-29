using System.IO;
using System.Text.Json;
using System.Net.WebSockets;
using System.Text;
using NAudio.Wave;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

internal static class SmokeTests
{
    public static void RunServerRecovery(string directory)
    {
        Directory.CreateDirectory(directory);
        try { TestServerRecoveryAsync(directory).GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(directory, "server-recovery-results.json"), JsonSerializer.Serialize(new { passed = false, error = exception.ToString() }));
            Environment.ExitCode = 1;
        }
    }

    private static async Task TestServerRecoveryAsync(string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient();
        using var health = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:8080/health", timeout.Token));
        if (health.RootElement.GetProperty("busy").GetBoolean()) throw new InvalidOperationException("Outgoing server is busy; test will not interfere with it.");
        byte[] first = ReadPcm(@"\\wsl.localhost\Ubuntu\opt\voxtral.cpp\tests\fixtures\audio\8297-275156-0000.wav");
        byte[] second = ReadPcm(@"\\wsl.localhost\Ubuntu\opt\voxtral.cpp\tests\fixtures\audio\8297-275156-0001.wav");
        byte[] audio = first.Concat(new byte[6 * 32000]).Concat(second).Concat(new byte[3 * 32000]).ToArray();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("ws://127.0.0.1:8080/v1/realtime/transcription"), timeout.Token);
        byte[] config = JsonSerializer.SerializeToUtf8Bytes(new { type = "session.configure", transcription_delay_ms = 160, audio = new { format = "pcm_s16le", sample_rate = 16000, channels = 1 }, language = "auto", events = new { token = true, partial = true } });
        await socket.SendAsync(config, WebSocketMessageType.Text, true, timeout.Token);
        var events = new List<(double Seconds, string Text)>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Task receive = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            while (!timeout.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                string? type = document.RootElement.GetProperty("type").GetString();
                if (type is "transcript.partial" or "transcript.final")
                    events.Add((watch.Elapsed.TotalSeconds, document.RootElement.GetProperty("text").GetString() ?? ""));
                else if (type == "error") throw new InvalidOperationException(Encoding.UTF8.GetString(message.ToArray()));
            }
        });
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(80));
        for (int offset = 0; offset < audio.Length; offset += 2560)
        {
            await timer.WaitForNextTickAsync(timeout.Token);
            byte[] chunk = new byte[2560];
            Array.Copy(audio, offset, chunk, 0, Math.Min(chunk.Length, audio.Length - offset));
            await socket.SendAsync(chunk, WebSocketMessageType.Binary, true, timeout.Token);
        }
        await Task.Delay(1000, timeout.Token);
        timeout.Cancel();
        socket.Abort();
        try { await receive; } catch (OperationCanceledException) { } catch (WebSocketException) { }
        double secondStart = first.Length / 32000.0 + 6;
        string initial = events.LastOrDefault(item => item.Seconds < secondStart).Text ?? "";
        string final = events.LastOrDefault().Text ?? "";
        bool passed = !string.IsNullOrWhiteSpace(initial) && final.Length > initial.Length && events.Any(item => item.Seconds > secondStart && item.Text != initial);
        File.WriteAllText(Path.Combine(directory, "server-recovery-results.json"), JsonSerializer.Serialize(new { passed, oneWebSocket = true, silenceBetweenSeconds = 6, firstTranscript = initial, finalTranscript = final, events = events.Select(item => new { seconds = item.Seconds, text = item.Text }) }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) Environment.ExitCode = 1;
    }

    private static byte[] ReadPcm(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1) throw new InvalidOperationException("Test fixture must be mono PCM16/16kHz.");
        using var pcm = new MemoryStream();
        reader.CopyTo(pcm);
        return pcm.ToArray();
    }

    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        try
        {
            Require(SubtitleWindow.ReadLoggedMuteState("MuteSelf=True received from OSC.") == true, "Read new controller muted state", checks);
            Require(SubtitleWindow.ReadLoggedMuteState("MuteSelf=True\nMuteSelf=False") == false, "Latest controller mute state wins", checks);
            Require(SubtitleWindow.ReadLoggedMuteState("VRChat mic is UNMUTED\nMuteSelf=True") == true, "New mute state overrides legacy listening log", checks);
            Require(SubtitleWindow.ReadLoggedMuteState("Capture-gate host started (PID 123); muted=True.") == true, "Host startup stays safely muted", checks);
            Require(SubtitleWindow.ReadLoggedMuteState("No verified mute state yet") is null, "Unknown mute state is not shown as listening", checks);
            Require(IncomingTranslationService.NeedsTargetRetry("你好，这是中文。", "English"), "Reject Chinese for English target", checks);
            Require(!IncomingTranslationService.NeedsTargetRetry("Hello, is this working?", "English"), "Accept English target", checks);
            Require(IncomingTranslationService.NeedsTargetRetry("Hello there, how are you?", "Chinese"), "Reject English for Chinese target", checks);
            Require(!IncomingTranslationService.NeedsTargetRetry("你好，这能用吗？", "Chinese"), "Accept Chinese target", checks);
            Require(!IncomingTranslationService.NeedsTargetRetry("こんにちは", "Japanese"), "Accept Japanese target", checks);
            Require(!IncomingTranslationService.NeedsTargetRetry("안녕하세요", "Korean"), "Accept Korean target", checks);
            Require(IncomingTranslationService.NeedsTargetRetry("Let's enter the 副本", "English"), "Reject mixed Chinese in English subtitles", checks);
            Require(AudioRouting.MutedPlaybackWarning(true, 0.5f, "Headphones").Contains("muted"), "Warn when playback is muted", checks);
            Require(AudioRouting.MutedPlaybackWarning(false, 0f, "Headphones").Contains("0%"), "Warn when playback volume is zero", checks);
            Require(AudioRouting.MutedPlaybackWarning(false, 0.5f, "Headphones") == "", "Do not warn on audible playback", checks);
            TestStallRecoveryPolicy(checks);
            TestFiniteUtterances(checks);
            checks.Add($"Continuous recognition: {IncomingContinuousAudioTests.Run()} offline checks passed");
            TestHandshakeTimeoutAsync(checks).GetAwaiter().GetResult();
            TestNaturalCompletionAsync(checks).GetAwaiter().GetResult();
            var application = new Application();
            var window = new SubtitleWindow();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            window.Publish(new TranslationDisplay("Speaker 1", "你好", "Hello!", 1, now));
            window.Publish(new TranslationDisplay("Speaker 2", "今天怎么样？", "How's your day going?", 2, now.AddSeconds(1)));
            window.Publish(new TranslationDisplay("Speaker 1", "我们去别的地方吧", "Let's go somewhere else.", 3, now.AddSeconds(2)));
            window.Publish(new TranslationDisplay("Speaker 1", "我们去别的地方吧，这里太吵了", "Let's go somewhere else; it's too noisy here.", 3, now.AddSeconds(3)));
            window.Publish(new TranslationDisplay("Speaker 1", "我们去别的地方吧", "Old partial must not replace the final line.", 3, now.AddSeconds(2)));
            Require(window.HistoryEntryCount == 3, "History preserves completed lines and revises current line once", checks);
            Require(window.VisibleBubbleCount == 3, "Each translated sentence has its own chronological bubble", checks);
            Render((FrameworkElement)window.Content, 960, 430, Path.Combine(directory, "overlay-preview.png"));
            Require(!VisualText((DependencyObject)window.Content).Contains("Old partial must not replace", StringComparison.Ordinal),
                "A late partial cannot overwrite a newer final bubble", checks);
            var options = new SettingsWindow(new AppSettings());
            Render((FrameworkElement)options.Content, 620, 740, Path.Combine(directory, "options-preview.png"));
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { passed = false, checks, error = exception.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
    }

    private static void Require(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }

    private static async Task TestHandshakeTimeoutAsync(List<string> checks)
    {
        string result = await IncomingTranslationService.AwaitSessionCreatedAsync(
            _ => Task.FromResult("session.created"), TimeSpan.FromSeconds(1), CancellationToken.None);
        Require(result == "session.created", "Healthy speech session acknowledgment completes", checks);
        bool timedOut = false;
        try
        {
            await IncomingTranslationService.AwaitSessionCreatedAsync(async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return "unexpected";
            }, TimeSpan.FromMilliseconds(40), CancellationToken.None);
        }
        catch (TimeoutException) { timedOut = true; }
        Require(timedOut, "A missing speech session acknowledgment cannot freeze startup forever", checks);
    }

    internal static int RunFiniteUtteranceChecks()
    {
        var checks = new List<string>();
        TestFiniteUtterances(checks);
        TestStallRecoveryPolicy(checks);
        int continuousChecks = IncomingContinuousAudioTests.Run() + AiSpeakerIdentifier.RunAudioPreparationOfflineChecks();
        int healthChecks = Task.Run(async () =>
        {
            await TestHandshakeTimeoutAsync(checks);
            await TestNaturalCompletionAsync(checks);
            return await IncomingTranslationService.RunRecoveryHealthChecksAsync();
        }).GetAwaiter().GetResult();
        return checks.Count + healthChecks + continuousChecks;
    }

    private static void TestStallRecoveryPolicy(List<string> checks)
    {
        Require(!IncomingTranslationService.ShouldRefreshStalledSpeech(0, 60, 60, 60, 60, 2, 0), "Silence never triggers speech reconnect", checks);
        Require(IncomingTranslationService.ShouldRefreshStalledSpeech(1.2, 1, 12, 20, 30, 2, 0), "Intermittent speech can recover a stalled stream", checks);
        Require(IncomingTranslationService.ShouldRefreshStalledSpeech(2, 0, 12, 20, 30, 2, 2),
            "A third recovery remains possible after two failed fallbacks", checks);
        Require(IncomingTranslationService.ShouldRefreshStalledSpeech(2, 0, 110, 110, 110, 2, 50),
            "A long stalled stream cannot become permanently ineligible for recovery", checks);
        Require(!IncomingTranslationService.ShouldRefreshStalledSpeech(2, 0, 12, 20, 5, 2, 0), "Reconnect cooldown is respected", checks);
        Require(!IncomingTranslationService.ShouldRefreshStalledSpeech(2, 0, 18, 20, 39, 2, 8),
            "Repeated stalls respect the capped forty-second backoff", checks);
        Require(!IncomingTranslationService.ShouldRefreshStalledSpeech(2, 0, 12, 20, 30, 1, 0), "In-place recovery gets a chance before fallback", checks);
        Require(!IncomingTranslationService.ShouldRefreshStalledSpeech(2, 0, 3, 20, 30, 2, 0), "Actual new words prevent an unnecessary reconnect", checks);
        Require(!IncomingTranslationService.ShouldRefreshStalledSpeech(2, 4, 30, 30, 30, 2, 0), "Old voice activity is not mistaken for continuing speech", checks);
        Require(IncomingTranslationService.StallRecoveryCooldownSeconds(0) == 20 &&
            IncomingTranslationService.StallRecoveryCooldownSeconds(1) == 20 &&
            IncomingTranslationService.StallRecoveryCooldownSeconds(2) == 30 &&
            IncomingTranslationService.StallRecoveryCooldownSeconds(5000) == 40,
            "Recovery cadence backs off without creating a minutes-long or permanent lockout", checks);
        int fallbacks = 0;
        double lastFallback = -30, startedAt = -20;
        var recoveryTimes = new List<double>();
        for (int second = 0; second <= 200; second++)
            if (IncomingTranslationService.ShouldRefreshStalledSpeech(2, 0, second - startedAt, second - startedAt,
                second - lastFallback, 2, fallbacks))
            {
                fallbacks++; lastFallback = second; startedAt = second; recoveryTimes.Add(second);
            }
        Require(recoveryTimes.Count >= 5 && recoveryTimes.Zip(recoveryTimes.Skip(1), (before, after) => after - before)
            .All(interval => interval is >= 20 and <= 40),
            "Continuous failures keep retrying at a bounded rate instead of stopping after two attempts", checks);
        Require(IncomingTranslationService.IsMeaningfulTranscriptChange("", "Hi") &&
            IncomingTranslationService.IsMeaningfulTranscriptChange("你好", "你好呀") &&
            IncomingTranslationService.IsMeaningfulTranscriptChange("word one", "word two"),
            "Short words, Chinese text and real transcript corrections count as progress", checks);
        Require(!IncomingTranslationService.IsMeaningfulTranscriptChange("Hi", "Hi!") &&
            !IncomingTranslationService.IsMeaningfulTranscriptChange("Hi", "HI") &&
            !IncomingTranslationService.IsMeaningfulTranscriptChange("Hi there", "Hi  there") &&
            !IncomingTranslationService.IsMeaningfulTranscriptChange("", " … "),
            "Repeated text, punctuation and spacing cannot masquerade as fresh recognized speech", checks);
    }

    private static void TestFiniteUtterances(List<string> checks)
    {
        byte[] silence = new byte[2560];
        byte[] voice = new byte[2560];
        for (int index = 0; index < voice.Length / 2; index++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 180 * index / 16000.0) * 0.03 * short.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(voice.AsSpan(index * 2, 2), sample);
        }
        Require(IncomingPcmVoiceDetector.Classify(silence) == PcmVoiceActivity.Silence, "PCM silence never opens a decoder", checks);
        Require(IncomingPcmVoiceDetector.Classify(voice) == PcmVoiceActivity.Speech, "Voiced PCM can open a decoder", checks);
        Require(IncomingPcmVoiceDetector.Classify(voice, 0.00035) == PcmVoiceActivity.Silence,
            "Amplified background audio cannot keep a speech turn open", checks);
        Require(IncomingPcmVoiceDetector.Classify(voice, 0.002) == PcmVoiceActivity.Speech,
            "Quiet genuine speech remains eligible for recognition gain", checks);
        byte[] noise = new byte[2560];
        var random = new Random(42);
        for (int index = 0; index < noise.Length / 2; index++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(noise.AsSpan(index * 2, 2), (short)random.Next(-1000, 1001));
        Require(IncomingPcmVoiceDetector.Classify(noise) == PcmVoiceActivity.Silence, "Broadband noise does not start a voice turn", checks);

        var audio = new IncomingUtteranceAudioBuffer();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        DateTimeOffset at = start;
        void Add(PcmVoiceActivity activity)
        {
            audio.Observe(activity == PcmVoiceActivity.Silence ? silence : voice, at, activity, 1300);
            at = at.AddMilliseconds(80);
        }
        for (int index = 0; index < 60; index++) Add(PcmVoiceActivity.Silence);
        Require(!audio.TryTake(out _) && audio.BufferedBytes == 0, "Long idle silence stays in pre-roll, not an ASR session", checks);
        Add(PcmVoiceActivity.Speech);
        Require(audio.TryTake(out IncomingAudioUtterance? first), "New voice makes one pending utterance", checks);
        Require(audio.TryRead(first!, out IncomingPcmChunk firstChunk, out _) &&
            firstChunk.At == start.AddMilliseconds(56 * 80), "320 ms pre-roll preserves the onset", checks);
        while (audio.TryRead(first!, out _, out _)) { }
        for (int index = 0; index < 6; index++) Add(PcmVoiceActivity.Silence);
        while (audio.TryRead(first!, out _, out _)) { }
        audio.TryRead(first!, out _, out bool complete);
        Require(!complete, "A short pause does not finish a voice turn", checks);
        Add(PcmVoiceActivity.Continuation);
        while (audio.TryRead(first!, out _, out _)) { }
        for (int index = 0; index < 16; index++) Add(PcmVoiceActivity.Silence);
        while (audio.TryRead(first!, out _, out _)) { }
        audio.TryRead(first!, out _, out complete);
        Require(!complete, "Unvoiced continuation restarts the real audio quiet timer", checks);
        Add(PcmVoiceActivity.Silence);
        int drained = 0;
        while (audio.TryRead(first!, out _, out _)) drained++;
        audio.TryRead(first!, out _, out complete);
        Require(complete && drained == 1, "End-of-audio appears only after queued tail audio drains", checks);

        Add(PcmVoiceActivity.Speech);
        Require(audio.TryTake(out IncomingAudioUtterance? second) && !ReferenceEquals(first, second),
            "Voice arriving during finalization has its own pending turn", checks);
        audio.Release(first!);
        Require(audio.TryRead(second!, out _, out _), "Finishing an old turn does not clear the new voice", checks);

        var ambience = new IncomingUtteranceAudioBuffer();
        ambience.Observe(voice, start, PcmVoiceActivity.Speech, 1300);
        ambience.TryTake(out IncomingAudioUtterance? ambienceTurn);
        for (int index = 1; index <= 27; index++)
        {
            ambience.Observe(voice, start.AddMilliseconds(index * 80), PcmVoiceActivity.Continuation, 1300);
            while (ambience.TryRead(ambienceTurn!, out _, out _)) { }
        }
        ambience.TryRead(ambienceTurn!, out _, out bool ambienceComplete);
        Require(ambienceComplete, "Steady unpitched ambience cannot hold a finished voice turn open forever", checks);

        var limited = new IncomingUtteranceAudioBuffer(maximumTurnMilliseconds: 2000);
        limited.Observe(voice, start, PcmVoiceActivity.Speech, 1300);
        limited.TryTake(out IncomingAudioUtterance? limitedTurn);
        for (int index = 1; index < 26; index++)
        {
            limited.Observe(voice, start.AddMilliseconds(index * 80), PcmVoiceActivity.Speech, 1300);
            while (limited.TryRead(limitedTurn!, out _, out _)) { }
        }
        limited.TryRead(limitedTurn!, out _, out bool limitComplete);
        Require(limitComplete && limitedTurn!.EndReason == "continuous-audio safety limit",
            "Continuous pitched audio has a finite recognition turn", checks);
        limited.Observe(voice, start.AddMilliseconds(26 * 80), PcmVoiceActivity.Speech, 1300);
        Require(limited.TryTake(out IncomingAudioUtterance? followingTurn) && !ReferenceEquals(limitedTurn, followingTurn),
            "Speech after a safety boundary starts a new turn without stopping capture", checks);

        var bounded = new IncomingUtteranceAudioBuffer(capacityChunks: 3, preRollChunks: 0);
        for (int index = 0; index < 8; index++) bounded.Observe(voice, start.AddMilliseconds(index * 80), PcmVoiceActivity.Speech, 1300);
        Require(bounded.BufferedBytes == 3 * 2560 && bounded.DroppedChunks == 5,
            "Backlog is bounded and old skipped chunks are counted", checks);
        bounded.TryTake(out IncomingAudioUtterance? newest);
        Require(bounded.TryRead(newest!, out IncomingPcmChunk newestChunk, out _) && newestChunk.At == start.AddMilliseconds(400),
            "Overflow retains newest PCM with its original timestamp", checks);

        var recovered = new IncomingUtteranceAudioBuffer(capacityChunks: 3, preRollChunks: 0);
        recovered.Observe(voice, start, PcmVoiceActivity.Speech, 1300);
        recovered.TryTake(out IncomingAudioUtterance? failedTurn);
        recovered.TryRead(failedTurn!, out _, out _);
        for (int index = 1; index <= 8; index++)
            recovered.Observe(voice, start.AddMilliseconds(index * 80), PcmVoiceActivity.Speech, 1300);
        recovered.Release(failedTurn!, preserveUnsent: true);
        bool pendingResumed = recovered.HasPendingAudio;
        bool resumed = recovered.TryTake(out IncomingAudioUtterance? resumedTurn);
        Require(recovered.BufferedBytes == 3 * 2560 && pendingResumed &&
            resumed && !ReferenceEquals(failedTurn, resumedTurn),
            "A failed socket preserves the freshest unread audio as a new bounded turn", checks);
        Require(recovered.TryRead(resumedTurn!, out IncomingPcmChunk freshestRetained, out _) &&
            freshestRetained.At == start.AddMilliseconds(480) && resumedTurn!.FirstQueuedAudioAt == freshestRetained.At,
            "A recovery resumes from the newest bounded suffix with a correct new turn onset", checks);
        recovered.Release(resumedTurn!);
        var continuing = new IncomingUtteranceAudioBuffer(capacityChunks: 3, preRollChunks: 0);
        continuing.Observe(voice, start, PcmVoiceActivity.Speech, 1300);
        continuing.TryTake(out IncomingAudioUtterance? original);
        continuing.Observe(voice, start.AddMilliseconds(80), PcmVoiceActivity.Speech, 1300);
        continuing.Release(original!, preserveUnsent: true);
        continuing.TryTake(out IncomingAudioUtterance? replacement);
        Require(continuing.TryRead(replacement!, out IncomingPcmChunk retainedOnset, out _) && retainedOnset.At == start,
            "Recovered PCM retains original speaker timestamps instead of joining a fictitious continuous timeline", checks);
        while (continuing.TryRead(replacement!, out _, out _)) { }
        continuing.Observe(voice, start.AddMilliseconds(160), PcmVoiceActivity.Speech, 1300);
        Require(continuing.TryRead(replacement!, out IncomingPcmChunk liveContinuation, out _) &&
            liveContinuation.At == start.AddMilliseconds(160),
            "Capture continues into the replacement turn after a recoverable socket failure", checks);
        continuing.Release(replacement!);
        Require(continuing.BufferedBytes == 0 && !continuing.HasPendingAudio,
            "Stopping without preservation still releases all abandoned PCM", checks);

        var timeline = new IncomingAudioTimeline();
        timeline.Append(start, 2560);
        timeline.Append(start.AddSeconds(5), 2560);
        Require(timeline.Map(100) == start.AddSeconds(5).AddMilliseconds(20),
            "Server audio offsets preserve real speaker times across dropped PCM gaps", checks);
        Require(timeline.Map(80) == start.AddMilliseconds(80), "Boundary offsets refer to the completed chunk, not a later gap", checks);
        Require(timeline.Map(1000) == start.AddSeconds(5).AddMilliseconds(80), "Decoder padding cannot invent future speaker evidence", checks);
    }

    private static async Task TestNaturalCompletionAsync(List<string> checks)
    {
        var receiver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task completion = IncomingTranslationService.AwaitNaturalCompletionAsync(
            Task.CompletedTask, receiver.Task, watchdog.Task, TimeSpan.FromSeconds(1), CancellationToken.None);
        await Task.Delay(20);
        Require(!completion.IsCompleted && !receiver.Task.IsCanceled,
            "Normal sender completion keeps receiving the final transcript", checks);
        receiver.SetResult(true);
        await completion;
        Require(completion.IsCompletedSuccessfully, "Final session completion ends a turn without reconnect failure", checks);

        bool timedOut = false;
        try
        {
            await IncomingTranslationService.AwaitNaturalCompletionAsync(Task.CompletedTask,
                new TaskCompletionSource<bool>().Task, watchdog.Task, TimeSpan.FromMilliseconds(30), CancellationToken.None);
        }
        catch (TimeoutException) { timedOut = true; }
        Require(timedOut, "Missing final completion has a bounded drain timeout", checks);
        var pendingReceiver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool pendingTimedOut = false;
        try
        {
            await IncomingTranslationService.AwaitNaturalCompletionAsync(Task.CompletedTask, pendingReceiver.Task,
                watchdog.Task, TimeSpan.FromSeconds(8), CancellationToken.None, () => true, TimeSpan.FromMilliseconds(40));
        }
        catch (TimeoutException) { pendingTimedOut = true; }
        Require(pendingTimedOut && watch.Elapsed < TimeSpan.FromSeconds(2),
            "A blocked old final response cannot spend eight seconds discarding the next voice turn", checks);
        var healthyFinal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pendingButHealthy = IncomingTranslationService.AwaitNaturalCompletionAsync(Task.CompletedTask,
            healthyFinal.Task, watchdog.Task, TimeSpan.FromSeconds(8), CancellationToken.None, () => true,
            TimeSpan.FromSeconds(1));
        healthyFinal.SetResult(true);
        await pendingButHealthy;
        Require(pendingButHealthy.IsCompletedSuccessfully,
            "A healthy final transcript still completes normally when the next voice is already waiting", checks);
        bool queuedAfterDrain = false;
        Task newSpeech = IncomingTranslationService.AwaitNaturalCompletionAsync(Task.CompletedTask,
            pendingReceiver.Task, watchdog.Task, TimeSpan.FromSeconds(8), CancellationToken.None,
            () => queuedAfterDrain, TimeSpan.FromMilliseconds(40));
        await Task.Delay(30);
        Require(!newSpeech.IsCompleted, "The shorter final deadline does not start before new speech arrives", checks);
        queuedAfterDrain = true;
        pendingTimedOut = false;
        try { await newSpeech.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { pendingTimedOut = true; }
        Require(pendingTimedOut && newSpeech.IsFaulted,
            "Speech arriving after finalization begins also releases the old stream promptly", checks);
        using var canceled = new CancellationTokenSource();
        Task canceledDrain = IncomingTranslationService.AwaitNaturalCompletionAsync(Task.CompletedTask,
            pendingReceiver.Task, watchdog.Task, TimeSpan.FromSeconds(8), canceled.Token);
        canceled.Cancel();
        bool canceledProperly = false;
        try { await canceledDrain; }
        catch (OperationCanceledException) { canceledProperly = true; }
        Require(canceledProperly, "User stop cancels a waiting final response without triggering a recovery retry", checks);
        bool failed = false;
        try
        {
            await IncomingTranslationService.AwaitNaturalCompletionAsync(Task.FromException(new InvalidOperationException("mock sender failure")),
                new TaskCompletionSource<bool>().Task, watchdog.Task, TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Require(failed, "An actual sender fault is never treated as normal finalization", checks);
    }

    private static void Render(FrameworkElement content, int width, int height, string path)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static string VisualText(DependencyObject element)
    {
        var text = new StringBuilder();
        if (element is System.Windows.Controls.TextBlock block) text.AppendLine(block.Text);
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            text.Append(VisualText(VisualTreeHelper.GetChild(element, index)));
        return text.ToString();
    }
}

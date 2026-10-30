using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

return await LocalHost.RunAsync(args);

internal static class LocalHost
{
    public static readonly object LogGate = new();
    private static readonly BestEffortHostLogger HostLogger = new(line => Console.WriteLine(line));
    public static string Root = Environment.CurrentDirectory;
    public static string LogPath = Path.Combine(Root, "foxtrans-local-host.log");
    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null)
        {
            if (File.Exists(Path.Combine(folder.FullName, "config.fast-chinese.jsonc"))) return folder.FullName;
            folder = folder.Parent;
        }
        return Environment.CurrentDirectory;
    }
    public static void Log(string message)
    {
        lock (LogGate)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            HostLogger.Write(LogPath, line);
        }
    }

    public static bool FinishCaptured()
    {
        try
        {
            using var settings = IncomingTranslator.OwnVoiceRuntimePreferences.Read(Root);
            return !settings.RootElement.TryGetProperty("FinishCapturedSentenceOnMute", out var flag) || flag.GetBoolean();
        }
        catch { return true; }
    }

    public static bool EarlyPreviewsEnabled()
    {
        try
        {
            using var settings = IncomingTranslator.OwnVoiceRuntimePreferences.Read(Root);
            return ResolveEarlyPreviewPreference(settings.RootElement);
        }
        catch (FileNotFoundException) { return false; }
        catch { return false; } // Malformed/unreadable preferences are final-only.
    }

    internal static bool ResolveEarlyPreviewPreference(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty("OwnVoiceSendEarlyPreviews", out var flag) &&
            flag.ValueKind == JsonValueKind.True;

    internal sealed record OwnVoiceLivePreferences(int PauseMilliseconds, bool EarlyPreviews);
    internal static OwnVoiceLivePreferences ResolveLivePreferences(JsonElement settings, OwnVoiceLivePreferences fallback)
    {
        if (settings.ValueKind != JsonValueKind.Object) throw new JsonException("Preferences are not an object.");
        int pause = fallback.PauseMilliseconds;
        bool early = fallback.EarlyPreviews;
        if (settings.TryGetProperty("OwnVoiceEndPauseMilliseconds", out var timing))
        {
            if (timing.ValueKind != JsonValueKind.Number || !timing.TryGetInt32(out pause) || pause is < 900 or > 3500)
                throw new JsonException("Sentence timing is invalid.");
        }
        if (settings.TryGetProperty("OwnVoiceSendEarlyPreviews", out var preview))
        {
            if (preview.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                throw new JsonException("Early translation preference is invalid.");
            early = preview.GetBoolean();
        }
        return new(pause, early);
    }
    internal static OwnVoiceLivePreferences ReadLivePreferences(OwnVoiceLivePreferences fallback)
    {
        using var document = IncomingTranslator.OwnVoiceRuntimePreferences.Read(Root);
        return ResolveLivePreferences(document.RootElement, fallback);
    }
    internal sealed record OwnRecognitionPreferences(bool Native, string Language);
    internal static OwnRecognitionPreferences ResolveRecognitionPreferences(JsonElement settings)
    {
        bool native = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("OwnRecognitionEngine", out var engine) &&
            engine.ValueKind == JsonValueKind.String && engine.GetString() == "nemotron";
        string? language = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("OwnVoiceSourceLanguage", out var source) &&
            source.ValueKind == JsonValueKind.String ? source.GetString() : null;
        string code = NormalizeChatLanguage(language) switch
        {
            "Chinese" => "zh-CN", "English" => "en-US", "Japanese" => "ja-JP", "Korean" => "ko-KR",
            "Spanish" => "es-ES", "French" => "fr-FR", "German" => "de-DE", "Portuguese" => "pt-BR", "Russian" => "ru-RU", _ => "auto"
        };
        if (native && code == "auto" && !string.IsNullOrWhiteSpace(language) &&
            !string.Equals(language.Trim(), "Automatic", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This speech recognizer does not support the selected spoken language. Choose a supported spoken language before starting.");
        return new(native, code);
    }
    internal static OwnRecognitionPreferences ReadRecognitionPreferences()
    {
        try
        {
            using var document = IncomingTranslator.OwnVoiceRuntimePreferences.Read(Root);
            return ResolveRecognitionPreferences(document.RootElement);
        }
        catch (FileNotFoundException) { return new(false, "auto"); } // Legacy configurations keep their existing provider.
    }

    internal sealed record OwnVoiceLanguages(IReadOnlyList<string> Targets, string? Glossary = null,
        string? SourceLanguage = null, bool ManagedTranslator = false)
    {
        public bool Multiple => Targets.Count > 1;
        public static readonly OwnVoiceLanguages Disabled = new(Array.Empty<string>());
    }

    internal static OwnVoiceLanguages ReadOwnVoiceLanguages()
    {
        try
        {
            using var settings = IncomingTranslator.OwnVoiceRuntimePreferences.Read(Root);
            return ResolveOwnVoiceLanguages(settings.RootElement);
        }
        catch (FileNotFoundException) { return OwnVoiceLanguages.Disabled; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Log("WARN Multiple chat languages could not be read; using the existing single-language translation configuration.");
            return OwnVoiceLanguages.Disabled;
        }
    }

    internal static OwnVoiceLanguages ResolveOwnVoiceLanguages(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("OwnVoiceTargetLanguage", out var primary) || primary.ValueKind != JsonValueKind.String ||
            NormalizeChatLanguage(primary.GetString()) is not { } first)
            return OwnVoiceLanguages.Disabled;
        var targets = new List<string> { first };
        if (settings.TryGetProperty("OwnVoiceAdditionalLanguagesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True &&
            settings.TryGetProperty("OwnVoiceAdditionalTargetLanguages", out var extras) && extras.ValueKind == JsonValueKind.Array)
            foreach (var extra in extras.EnumerateArray())
            {
                string? target = extra.ValueKind == JsonValueKind.String ? NormalizeChatLanguage(extra.GetString()) : null;
                if (target is not null && !targets.Contains(target, StringComparer.OrdinalIgnoreCase)) targets.Add(target);
                if (targets.Count == MultiTargetTextTranslator.MaximumTargets) break;
            }
        string? glossary = settings.TryGetProperty("Glossary", out var terms) && terms.ValueKind == JsonValueKind.String ? terms.GetString() : null;
        string? source = settings.TryGetProperty("OwnVoiceSourceLanguage", out var spoken) && spoken.ValueKind == JsonValueKind.String
            ? NormalizeChatLanguage(spoken.GetString()) : null;
        bool managed = settings.TryGetProperty("UseManagedTranslator", out var runtime) && runtime.ValueKind == JsonValueKind.True;
        return new(targets, glossary, source, managed);
    }

    internal static string? NormalizeChatLanguage(string? language) => TranslationModelPrompts.CanonicalLanguage(language);

    internal static ITextTranslator OwnVoiceTranslator(HttpClient http, ResolvedOpenAiChatSettings primary, OwnVoiceLanguages languages)
    {
        bool specialized = TranslationModelPrompts.RequiresExplicitSource(primary.Model);
        if (TranslationModelCapabilities.RequiresManagedRuntime(primary.Model) && !languages.ManagedTranslator)
            throw new ConfigurationException("TranslateGemma requires SayWhat?'s built-in runtime and its compatible translation template. Select the built-in runtime in Settings.");
        if (specialized && TranslationModelPrompts.ValidateLanguages(primary.Model, languages.SourceLanguage, languages.Targets) is { } invalid)
            throw new ConfigurationException(invalid);
        ITextTranslator original = specialized
            ? new SpecializedTextTranslator(http, primary, languages.SourceLanguage, languages.Targets.FirstOrDefault())
            : new OpenAiTextTranslator(http, primary);
        if (!languages.Multiple) return original;
        var targets = new List<(string Language, ITextTranslator Translator)> { (languages.Targets[0], original) };
        foreach (string target in languages.Targets.Skip(1))
        {
            string prompt = TranslationModelPrompts.IsHyMt(primary.Model)
                ? TranslationModelPrompts.HyMtInstruction(target == "Chinese" ? "Simplified Chinese" : target, languages.Glossary)
                : TranslationModelPrompts.CasualChatInstruction(target, languages.Glossary);
            targets.Add((target, specialized ? new SpecializedTextTranslator(http, primary, languages.SourceLanguage, target)
                : new OpenAiTextTranslator(http, primary with { Prompt = prompt })));
        }
        return new MultiTargetTextTranslator(targets, (target, reason) =>
            Log($"WARN {target} translation unavailable; reason={reason}; successful selected languages may still be sent. Check the selected model in Settings."));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            try
            {
                int index = Array.IndexOf(args, "--root");
                if (index < 0 || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
                    throw new ArgumentException("Self-tests require --root with an existing, isolated absolute test directory.");
                string testRoot = Path.GetFullPath(args[index + 1]);
                if (!Directory.Exists(testRoot) || File.Exists(Path.Combine(testRoot, "config.fast-chinese.jsonc")) ||
                    File.Exists(Path.Combine(testRoot, "vrchat-translation-settings.json")) ||
                    File.Exists(Path.Combine(testRoot, IncomingTranslator.OwnVoiceRuntimePreferences.FileName)))
                    throw new ArgumentException("The self-test root must be an existing isolated directory without live settings.");
                Root = testRoot;
                LogPath = Path.Combine(Root, "foxtrans-local-host-tests.log");
                return await LocalTests.RunAsync();
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
        Root = FindRoot();
        string config = Path.Combine(Root, "config.fast-chinese.jsonc");
        int port = 9011;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config") config = Path.GetFullPath(args[++i]);
                else if (args[i] == "--control-port") port = int.Parse(args[++i]);
                else if (args[i] == "--root") Root = Path.GetFullPath(args[++i]);
                else if (args[i] is not "run" and not "check") throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        catch (Exception error) { Console.Error.WriteLine("Invalid local host command: " + error.Message); return 2; }
        LogPath = Path.Combine(Root, "foxtrans-local-host.log");
        using var shutdown = new CancellationTokenSource();
        Exception? controlFailure = null;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        try
        {
            ConfigLoadResult loaded = AppConfig.LoadExplicit(config);
            var resolution = ExecutionPlanResolver.Resolve(loaded.Config!,
                new NativeAudioInputDeviceCatalogue().GetInputs(), Environment.GetEnvironmentVariable);
            if (!resolution.IsValid) throw new ConfigurationException(string.Join("; ", resolution.Issues.Select(i => i.Path + ": " + i.Message)));
            var plan = resolution.Plan!;
            OwnRecognitionPreferences recognition = ReadRecognitionPreferences();
            using var http = new HttpClient();
            if (args.Contains("check"))
            {
                if (plan.Translation is { } checkingTranslation && TranslationModelPrompts.RequiresExplicitSource(checkingTranslation.Model))
                    _ = OwnVoiceTranslator(http, checkingTranslation, ReadOwnVoiceLanguages());
                var ready = await ReadinessChecks.RunAsync(recognition.Native ? plan with { Voxtral = null } : plan, http, shutdown.Token);
                if (recognition.Native)
                {
                    using var nativeCheck = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    nativeCheck.CancelAfter(TimeSpan.FromSeconds(3));
                    try { await NemotronReadiness.CheckAsync(http, plan.Voxtral!.HealthEndpoint, nativeCheck.Token); Console.WriteLine("[OK] Nemotron /ready: ready"); }
                    catch (Exception error) when (error is not OperationCanceledException || !shutdown.IsCancellationRequested)
                    { Console.WriteLine("[FAIL] Nemotron /ready: unavailable; " + SafeFailureCode(error)); return 3; }
                }
                foreach (string ok in ready.Successes) Console.WriteLine("[OK] " + ok);
                foreach (string error in ready.Failures) Console.WriteLine("[FAIL] " + error);
                return ready.IsReady ? 0 : 3;
            }
            if (plan.PipelineKind != PipelineKind.RealtimeTranscriptionTranslation)
                throw new ConfigurationException("The local capture-gate host requires the Voxtral realtime pipeline.");
            bool earlyPreviews = EarlyPreviewsEnabled();
            OwnVoiceLivePreferences livePreferences = new(plan.Realtime!.NewUtteranceAfterMs, earlyPreviews);
            try { livePreferences = ReadLivePreferences(livePreferences); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { Log("WARN live preferences unavailable; keeping the validated capture timing and safe preview preference."); }
            OwnVoiceLanguages ownLanguages = ReadOwnVoiceLanguages();
            ITextTranslator ownTranslator = OwnVoiceTranslator(http, plan.Translation!, ownLanguages);
            using var control = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            using var changes = new SemaphoreSlim(0, 1);
            object stateGate = new();
            bool muted = true;
            bool reconfiguring = false;
            (IPEndPoint Endpoint, string Nonce)? pendingQuiesce = null;
            ContinuousCapture? active = null;
            CancellationTokenSource? activeCancellation = null;
            async Task AcknowledgeQuiesceIfIdleAsync()
            {
                (IPEndPoint Endpoint, string Nonce)? reply;
                lock (stateGate)
                {
                    if (active is not null || pendingQuiesce is null) return;
                    reply = pendingQuiesce; pendingQuiesce = null;
                }
                await control.SendAsync(Osc.Packet("/foxtrans/control/quiesced", ",s", reply.Value.Nonce), reply.Value.Endpoint, shutdown.Token);
                Log("CAPTURE quiesced; the permitted captured sentence and output drain have finished; awaiting reconfiguration.");
            }
            string instance = Guid.NewGuid().ToString("N");
            Log("SAYWHAT_STATUS component=microphone-host event=control-ready");
            Log($"Local FoxTrans v2.0.0 fork ready. Control 127.0.0.1:{port}; microphone {plan.Audio.DisplayName}; real PCM silence {livePreferences.PauseMilliseconds} ms; recognizer={(recognition.Native ? "nemotron" : "voxtral")}; language={recognition.Language}.");
            Log(earlyPreviews ? $"Cumulative early chat previews enabled; translation groups are throttled to at least {1500 * Math.Max(1, ownLanguages.Targets.Count)} ms." : "Final-only chat output enabled; early previews are disabled.");
            if (ownLanguages.Multiple)
                Log($"Multiple chat languages enabled: {string.Join(", ", ownLanguages.Targets)}; one shared model, sequential requests, 7.5-second group budget. More languages may take longer.");
            Log("MUTED done; microphone is closed. Awaiting verified VRChat unmute.");
            Task commands = Task.Run(async () =>
            {
                while (!shutdown.IsCancellationRequested)
                {
                    var received = await control.ReceiveAsync(shutdown.Token);
                    int offset = 0;
                    if (!Osc.Read(received.Buffer, ref offset, out string address) ||
                        !Osc.Read(received.Buffer, ref offset, out string tags)) continue;
                    if (address == "/foxtrans/control/quiesce" && tags == ",s" &&
                        Osc.Read(received.Buffer, ref offset, out string nonce) && Guid.TryParse(nonce, out _))
                    {
                        lock (stateGate)
                        {
                            bool firstRequest = !reconfiguring;
                            reconfiguring = true; muted = true;
                            pendingQuiesce = (received.RemoteEndPoint, nonce);
                            if (firstRequest)
                            {
                                active?.PauseAndDrain();
                                activeCancellation?.CancelAfter(TimeSpan.FromSeconds(20));
                            }
                        }
                        Log("CAPTURE reconfiguration requested; microphone capture stopped; finishing the permitted captured sentence before acknowledgment.");
                        await AcknowledgeQuiesceIfIdleAsync();
                        if (changes.CurrentCount == 0) changes.Release();
                        continue;
                    }
                    if (address == "/foxtrans/control/resume" && tags == ",T")
                    {
                        lock (stateGate) { reconfiguring = false; pendingQuiesce = null; }
                        Log("CAPTURE reconfiguration canceled; awaiting the next verified mute heartbeat before capturing again.");
                        continue;
                    }
                    if (address == "/foxtrans/control/reload" && (tags is "," or ",T"))
                    {
                        try
                        {
                            var updated = ReadLivePreferences(Volatile.Read(ref livePreferences));
                            lock (stateGate)
                            {
                                active?.SetPauseMilliseconds(updated.PauseMilliseconds);
                                Volatile.Write(ref livePreferences, updated);
                            }
                            Log($"LIVE preferences applied; pause_ms={updated.PauseMilliseconds}; early_previews={updated.EarlyPreviews}; capture and models were kept running.");
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
                        { Log("WARN live preferences reload rejected; keeping the last working timing and preview preference."); }
                        continue;
                    }
                    if (address != "/foxtrans/control/muted") continue;
                    if (tags is not ",T" and not ",F") continue;
                    bool requested = tags.Contains('T');
                    lock (stateGate)
                    {
                        if (reconfiguring) continue;
                        bool changed = muted != requested;
                        muted = requested;
                        if (requested && active is not null && !active.IsDraining)
                        {
                            bool finish = FinishCaptured();
                            active.PauseAndDrain(); // Native callbacks are gated before any ASR/LLM waiting.
                            Log(finish ? "MUTED finishing captured tail; new microphone capture is stopped." : "MUTED discarding captured sentence; microphone is stopped.");
                            if (!finish) activeCancellation?.Cancel();
                            else activeCancellation?.CancelAfter(TimeSpan.FromSeconds(20));
                        }
                        if (changed && !requested) Log(active?.IsDraining == true
                            ? "UNMUTED resume queued until captured tail completes." : "UNMUTED opening microphone.");
                    }
                    if (changes.CurrentCount == 0) changes.Release();
                }
            }, shutdown.Token);
            _ = commands.ContinueWith(task =>
            {
                if (!shutdown.IsCancellationRequested)
                {
                    controlFailure = task.Exception?.GetBaseException() ?? new IOException("The mute-control receiver stopped unexpectedly.");
                    Log("ERROR mute control: " + controlFailure.Message + "; closing microphone and failing safely.");
                    shutdown.Cancel();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            long session = 0;
            while (!shutdown.IsCancellationRequested)
            {
                bool wait;
                lock (stateGate) wait = muted;
                if (wait) { await changes.WaitAsync(shutdown.Token); continue; }
                using var native = new NativeAudioMicrophoneSource(plan.Audio);
                var source = new ContinuousCapture(native, native.StopCapture, native.Format, Volatile.Read(ref livePreferences).PauseMilliseconds);
                using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                lock (stateGate)
                {
                    if (muted) continue;
                    source.SetPauseMilliseconds(Volatile.Read(ref livePreferences).PauseMilliseconds);
                    active = source;
                    activeCancellation = sessionCancellation;
                }
                bool captureFailed = false;
                try
                {
                    source.Start(sessionCancellation.Token);
                    bool mutedFinalSent = false;
                    await foreach (CapturedTurn turn in source.ReadTurnsAsync(sessionCancellation.Token))
                    {
                        bool currentlyMuted;
                        lock (stateGate) currentlyMuted = muted;
                        if (ShouldDiscardMutedTurn(currentlyMuted, mutedFinalSent))
                        {
                            Log($"MUTED queued captured turn discarded explicitly: only one captured sentence is authorized; {turn.FramesWritten} buffered frames. No new audio was captured.");
                            turn.Discard(); turn.Outcome("muted-extra-turn-discarded");
                            continue;
                        }
                        await ProcessCapturedTurnAsync(turn, source.Completion, async () =>
                        {
                            turn.Stage("health-check");
                            string backend;
                            if (recognition.Native)
                            {
                                using var ready = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
                                ready.CancelAfter(TimeSpan.FromMilliseconds(900));
                                try { await NemotronReadiness.CheckAsync(http, plan.Voxtral!.HealthEndpoint, ready.Token); }
                                catch (OperationCanceledException) when (!sessionCancellation.IsCancellationRequested)
                                { throw new TimeoutException("Native recognition readiness exceeded its 900 ms timeout."); }
                                backend = "nemotron";
                            }
                            else backend = (await WaitReadyAsync(http, plan.Voxtral!, source.Completion, sessionCancellation.Token)).Model;
                            Log($"ASR opening voiced turn {++session}; backend {backend}; no raw audio is written.");
                            await using IStreamingTranscriber provider = recognition.Native
                                ? new NemotronTranscriber(plan.Voxtral!.HealthEndpoint, recognition.Language)
                                : new VoxtralFoxTranscriber(plan.Voxtral!, finishOnAudioEnd: true);
                            using var sink = new FinalOscSink(plan.Outputs[0], instance + "-" + session, turn, ownLanguages.Multiple);
                            try
                            {
                                await RunPipelineAsync(turn, provider, ownTranslator,
                                    sink, plan.Realtime!, sessionCancellation.Token, earlyPreviews,
                                    () => { lock (stateGate) return !muted; },
                                    earlyPreviewPreference: () => Volatile.Read(ref livePreferences).EarlyPreviews);
                            }
                            finally
                            {
                                // A post-send drain failure must not grant another
                                // muted final merely because RunPipeline threw.
                                lock (stateGate) currentlyMuted = muted;
                                mutedFinalSent |= ShouldMarkMutedFinal(currentlyMuted, sink.FinalCount);
                            }
                        }, sessionCancellation.Token);
                    }
                    await source.Completion;
                    Log("MUTED done; capture stopped and permitted captured audio/server/translation/output drained.");
                }
                catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
                { Log("Capture session cancelled; no unfinished draft was presented as a final."); }
                catch (Exception error)
                {
                    captureFailed = true;
                    Log("ERROR capture/drain: " + error.Message + "; no stale draft will be sent as complete.");
                    source.PauseAndDrain();
                }
                finally
                {
                    source.PauseAndDrain();
                    try { await source.Completion; } catch { /* The main failure is already logged. */ }
                    lock (stateGate) { active = null; activeCancellation = null; }
                }
                await AcknowledgeQuiesceIfIdleAsync();
                lock (stateGate) wait = muted;
                if (!wait && captureFailed) await Task.Delay(1000, shutdown.Token);
            }
            await commands;
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return controlFailure is null ? 0 : 1; }
        catch (Exception error) { Log(SupportFailureLine(error)); Log("FATAL: " + error); return 1; }
    }

    internal static string SupportFailureLine(Exception error)
    {
        Exception cause = error.GetBaseException();
        // A generic access failure might concern a model/configuration file,
        // not the microphone. Do not claim microphone permission denial unless
        // the capture API supplies specific typed evidence.
        string code = cause switch
        {
            DllNotFoundException => "native-library-missing",
            BadImageFormatException or EntryPointNotFoundException => "native-library-invalid",
            AudioDeviceSelectionException { NativeCode: -6 } => "microphone-access-denied",
            AudioDeviceSelectionException => "microphone-unavailable",
            UnauthorizedAccessException => "access-denied",
            System.Net.Sockets.SocketException => "endpoint-unavailable",
            HttpRequestException or WebSocketException => "provider-connection-failed",
            _ => "host-failed"
        };
        int? nativeCode = cause is AudioDeviceSelectionException audio ? audio.NativeCode
            : cause is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : null;
        string native = nativeCode is int number ? " native=" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return "SAYWHAT_STATUS component=microphone-host event=failed code=" + code +
            " hresult=0x" + unchecked((uint)cause.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + native;
    }

    // A provider/translation failure belongs to one captured turn. It must not
    // stop the healthy capture pump or erase speech already in its bounded queue.
    // Device/overflow failures and explicit session cancellation still propagate.
    internal static async Task<bool> ProcessCapturedTurnAsync(CapturedTurn turn, Task captureCompletion,
        Func<Task> process, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        { turn.Discard(); turn.Outcome("cancelled"); token.ThrowIfCancellationRequested(); }
        if (turn.IsDiscarded)
        {
            // A queued turn may already have exceeded its PCM cap while an
            // earlier sentence was being recognized. Do not open a new server
            // session for that explicitly failed/empty buffer.
            Log($"TURN skipped id={turn.Id}; code=discarded-before-processing; previously failed bounded captured turn; next queued turn is kept.");
            return false;
        }
        if (captureCompletion.IsFaulted || captureCompletion.IsCanceled)
        { turn.Outcome("capture-failed"); await captureCompletion; }
        turn.BeginProcessing();
        try
        {
            await process();
            if (captureCompletion.IsFaulted || captureCompletion.IsCanceled) await captureCompletion;
            token.ThrowIfCancellationRequested();
            turn.Outcome("completed");
            Log($"OWN VOICE recovered; code=turn-completed; turn={turn.Id}.");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            turn.Discard();
            turn.Outcome("cancelled");
            throw;
        }
        catch (Exception error)
        {
            if (captureCompletion.IsFaulted || captureCompletion.IsCanceled)
            { turn.Outcome("capture-failed"); await captureCompletion; }
            if (!IsRecoverableTurnFailure(error)) { turn.Outcome("fatal-failure"); throw; }
            turn.Discard();
            turn.Outcome(error is CapturedTurnFailureException failure ? failure.Code :
                error is TimeoutException ? "timeout" :
                error is VoxtralFoxException { Code: "session_completion_timeout" } ? "asr-timeout" :
                error is VoxtralFoxException ? "asr-failed" : "translation-failed");
            Log($"TURN recovery id={turn.Id}; stage={turn.CurrentStage}; cause={error.GetType().Name}; code={SafeFailureCode(error)}; capture remains active and the next queued turn is kept.");
            Log($"ERROR own-voice: code={SafeFailureCode(error)}; turn={turn.Id}; queued captured turns kept.");
            // A broken recognizer must not cause a tight loop of new sessions.
            // The healthy capture pump continues filling its bounded queue.
            await Task.Delay(750, token);
            return false;
        }
    }

    private static bool IsRecoverableTurnFailure(Exception error) =>
        error is CapturedTurnFailureException or VoxtralFoxException or OpenAiProviderException or
            TimeoutException or HttpRequestException or WebSocketException;

    internal static bool ShouldDiscardMutedTurn(bool muted, bool mutedFinalSent) => muted && mutedFinalSent;
    internal static bool ShouldMarkMutedFinal(bool muted, int sentFinalCount) => muted && sentFinalCount > 0;

    private static string SafeFailureCode(Exception error) => error switch
    {
        CapturedTurnFailureException failure => failure.Code,
        // Codes received from a server can be arbitrary strings. Persist only
        // known protocol identifiers, never arbitrary server message content.
        VoxtralFoxException { Code: "session_completion_timeout" } => "session_completion_timeout",
        VoxtralFoxException { Code: "server_busy" } => "server_busy",
        VoxtralFoxException { Code: "server_not_ready" } => "server_not_ready",
        VoxtralFoxException { Code: "health_request_failed" } => "health_request_failed",
        VoxtralFoxException { Code: "unexpected_close" } => "unexpected_close",
        VoxtralFoxException { Code: "invalid_event" } => "invalid_event",
        VoxtralFoxException => "other_protocol_failure",
        TimeoutException => "bounded_timeout",
        _ => "provider_failure"
    };

    internal static async Task<VoxtralHealthInfo> WaitReadyAsync(HttpClient http,
        ResolvedVoxtralFoxSettings settings, Task captureCompletion, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        bool waitingLogged = false;
        while (true)
        {
            if (captureCompletion.IsFaulted) await captureCompletion;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
            Task<VoxtralHealthInfo> checking = new VoxtralFoxHealthClient(http).CheckAsync(settings, timeout.Token);
            Task first = await Task.WhenAny(checking, captureCompletion);
            if (ReferenceEquals(first, captureCompletion) && captureCompletion.IsFaulted)
            { timeout.Cancel(); await captureCompletion; }
            try { return await checking; }
            catch (VoxtralFoxException error) when (error.Code is "server_busy" or "server_not_ready" && DateTimeOffset.UtcNow < deadline)
            {
                if (!waitingLogged) { Log("ASR waiting for the previous server lease to release (bounded 5 seconds); captured audio stays buffered."); waitingLogged = true; }
                await Task.Delay(150, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
            { throw new TimeoutException("ASR health request exceeded its 900 ms timeout; this captured turn cannot start recognition."); }
        }
    }

    internal static async Task RunPipelineAsync(ICapturedSpeechSource source, IStreamingTranscriber provider,
        ITextTranslator translator, IOutputSink output, ResolvedRealtimeSettings settings, CancellationToken token,
        bool enableEarlyPreviews = false, Func<bool>? canPreview = null, Func<DateTimeOffset>? getUtcNow = null,
        Func<bool>? earlyPreviewPreference = null)
    {
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = pipelineCancellation.Token;
        var turn = source as CapturedTurn;
        var utcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        var progress = new OwnSpeechProgressDeadline(utcNow(), turn?.VoicedSamples ?? 0);
        turn?.Stage("asr-start");
        bool previewCapable = enableEarlyPreviews || earlyPreviewPreference is not null;
        bool EarlyEnabled() => earlyPreviewPreference?.Invoke() ?? enableEarlyPreviews;
        bool CanOutputPreview() => EarlyEnabled() && (canPreview?.Invoke() ?? true);
        var reporter = new MetadataReporter(turn, previewCapable);
        using var guardedOutput = new CumulativePreviewOutput(output, previewCapable, CanOutputPreview);
        var scheduling = previewCapable ? PreviewSchedulingSettings(settings,
            translator is MultiTargetTextTranslator multiple ? multiple.TargetCount : 1) : settings with { MaxSourceCharacters = CompleteSourceLimit };
        await using var scheduler = new RealtimeTranslationScheduler(translator, [guardedOutput], scheduling, reporter, token, utcNow);
        var tracker = LogicalUtteranceState.Initial;
        using var trackerGate = new SemaphoreSlim(1, 1);
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool previewsStopped = !previewCapable;
        bool previewsWereEnabled = EarlyEnabled();
        async Task StopPreviewsLocked()
        {
            guardedOutput.ClosePreviews();
            if (previewsStopped) return;
            previewsStopped = true;
            // Epoch invalidation cancels one active speculative request, drops
            // its one pending revision and invalidates queued preview output.
            // The cumulative tracker text is retained for the real final.
            tracker = tracker with { TranscriptEpoch = tracker.TranscriptEpoch + 1 };
            await scheduler.InvalidateTranscriptEpochAsync(tracker.TranscriptEpoch, token);
        }
        async Task Apply(UtteranceTransition transition, bool mayPreview = true)
        {
            if (transition.Candidate is not { } candidate) return;
            candidate = candidate with { SourceText = tracker.LatestCumulativeText, SourceWasTruncated = false };
            if (transition.TranscriptEpochChanged)
                await scheduler.InvalidateTranscriptEpochAsync(candidate.TranscriptEpoch, token);
            Log($"ASR {transition.Kind} e{candidate.TranscriptEpoch}/u{candidate.UtteranceId}/r{candidate.Revision}; {candidate.SourceText.Length} chars; audio_end_ms={candidate.AudioEndMs}.");
            if (candidate.IsSettled)
            {
                reporter.EnterFinalPhase();
                await scheduler.SubmitAsync(candidate, utcNow(), token);
            }
            else if (previewCapable && !previewsStopped && mayPreview && guardedOutput.PreviewAllowed &&
                UsefulPreviewSource(candidate.SourceText) && TranslationWindows.TextElementCount(candidate.SourceText) <= CompleteSourceLimit)
            {
                await scheduler.SubmitAsync(candidate, utcNow(), token);
            }
        }
        Task timer = Task.Run(async () =>
        {
            while (!timerCancellation.IsCancellationRequested)
            {
                await Task.Delay(75, timerCancellation.Token);
                // Production supplies the verified unmute gate even in final-only
                // mode. Muting must protect the authorized captured final from
                // a last-millisecond ongoing-voice watchdog race.
                if (turn is not null && (canPreview?.Invoke() ?? true) &&
                    progress.IsStalled(utcNow(), turn.VoicedSamples, turn.HasOngoingVoice))
                {
                    turn.Stage("asr-progress-timeout");
                    throw new CapturedTurnFailureException("asr-progress-timeout",
                        "Speech recognition made no new word progress during ongoing voiced input; the failed turn is released and queued captured speech is kept.");
                }
                if (previewCapable && !(canPreview?.Invoke() ?? true))
                {
                    await trackerGate.WaitAsync(timerCancellation.Token);
                    try { await StopPreviewsLocked(); }
                    finally { trackerGate.Release(); }
                }
                else if (previewCapable && !previewsStopped && previewsWereEnabled != EarlyEnabled())
                {
                    await trackerGate.WaitAsync(timerCancellation.Token);
                    try
                    {
                        bool enabledNow = EarlyEnabled();
                        if (previewsWereEnabled && !enabledNow)
                        {
                            // Cancel only speculative work; retain the captured
                            // sentence and allow an explicit later re-enable.
                            tracker = tracker with { TranscriptEpoch = tracker.TranscriptEpoch + 1 };
                            await scheduler.InvalidateTranscriptEpochAsync(tracker.TranscriptEpoch, timerCancellation.Token);
                        }
                        previewsWereEnabled = enabledNow;
                    }
                    finally { trackerGate.Release(); }
                }
                await scheduler.TickAsync(utcNow(), timerCancellation.Token);
            }
        }, timerCancellation.Token);
        _ = timer.ContinueWith(task => { if (task.IsFaulted) pipelineCancellation.Cancel(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        bool completed = false;
        bool sawServerFinal = false;
        bool pipelineSucceeded = false;
        string serverFinalText = "";
        try
        {
            // Early cumulative updates use submitted chat bubbles, never the
            // chatbox editor. Their bounded scheduler retains full source text.
            if (output is ICapturedTurnOutput capturedOutput) await capturedOutput.BeginTurnAsync(token);
            await output.PublishAsync(TranslationUpdate.Typing(true), token);
            await foreach (var item in provider.TranscribeAsync(source.ReadFramesAsync(token), token).WithCancellation(token))
            {
                if (item is StreamingPartialTranscript or StreamingFinalTranscript)
                {
                    var partial = item is StreamingPartialTranscript p ? p : new StreamingPartialTranscript(
                        ((StreamingFinalTranscript)item).Sequence, ((StreamingFinalTranscript)item).Text, ((StreamingFinalTranscript)item).AudioEndMs);
                    progress.Observe(partial.Text, utcNow(), turn?.VoicedSamples ?? 0);
                    await trackerGate.WaitAsync(token);
                    try
                    {
                        if (item is StreamingFinalTranscript) await StopPreviewsLocked();
                        var transition = UtteranceTracking.ReducePartial(tracker, partial, utcNow(),
                            CompleteSourceLimit);
                        tracker = transition.State;
                        if (previewCapable && TranslationWindows.TextElementCount(tracker.LatestCumulativeText) > CompleteSourceLimit)
                            await StopPreviewsLocked();
                        await Apply(transition, mayPreview: item is StreamingPartialTranscript);
                        if (item is StreamingFinalTranscript)
                        {
                            sawServerFinal = true;
                            serverFinalText = partial.Text;
                            // A final alone is not success: defer final output until
                            // the mandatory session.completed acknowledgment arrives.
                            Log("ASR server final received for captured audio.");
                            Log("SAYWHAT_STATUS component=microphone-host event=recognition-final chars=" +
                                Math.Min(1_000_000, partial.Text.Length).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            turn?.Stage("asr-final", $"recognized_chars={partial.Text.Length}");
                        }
                    }
                    finally { trackerGate.Release(); }
                }
                else if (item is StreamingSessionCompleted completion)
                {
                    completed = true; Log("ASR session.completed received.");
                    turn?.Stage("asr-completed", $"audio_duration_ms={completion.AudioDurationMs}");
                    if (completion.Processing is { } processing)
                        Log($"ASR processing turn={turn?.Id}; realtime_factor={Metric(processing.RealtimeFactor)}; backlog_final_ms={Metric(processing.BacklogFinalMs)}; backlog_slope_ms_per_s={Metric(processing.BacklogSlopeMsPerSecond)}.");
                }
                else if (item is StreamingServerWarning warning)
                    Log($"ASR warning: {(warning.Code == "processing_lag" ? "processing_lag" : "server-warning")}; turn={turn?.Id}; backlog_ms={Metric(warning.BacklogMs)}; input_backlog_ms={Metric(warning.InputBacklogMs)}; runtime_backlog_ms={Metric(warning.RuntimeBacklogMs)}; pcm_queue_ms={Metric(warning.PcmQueueMs)}.");
            }
            if (!completed) throw new CapturedTurnFailureException("asr-missing-completed", "ASR ended without the mandatory session.completed acknowledgment.");
            if (!sawServerFinal) throw new CapturedTurnFailureException("asr-missing-final", "ASR completed without the mandatory transcript.final event.");
            if (source.HasSpeech && !UtteranceTracking.HasLexicalContent(serverFinalText))
                throw new CapturedTurnFailureException("asr-empty-final", "ASR final contained no recognized captured speech; no stale draft was sent.");
            if (TranslationWindows.TextElementCount(serverFinalText) > CompleteSourceLimit)
                throw new CapturedTurnFailureException("asr-context-limit", "The full captured sentence exceeded the 2048-character context safety limit; no source suffix was silently translated.");
            await trackerGate.WaitAsync(token);
            try
            {
                var final = UtteranceTracking.Settle(tracker, utcNow(),
                    CompleteSourceLimit);
                tracker = final.State;
                await Apply(final);
            }
            finally { trackerGate.Release(); }
            await scheduler.WaitForIdleAsync(TimeSpan.FromSeconds(8), token);
            if (reporter.Failed) throw new CapturedTurnFailureException("translation-or-output-failed", "Translation or output failed while draining captured speech.");
            if (source.HasSpeech && !UtteranceTracking.HasLexicalContent(tracker.LatestCumulativeText))
                throw new CapturedTurnFailureException("asr-empty-transcript", "ASR completed without recognizing the captured speech; no stale draft was sent.");
            pipelineSucceeded = true;
        }
        finally
        {
            guardedOutput.ClosePreviews();
            timerCancellation.Cancel();
            try { await timer; } catch (OperationCanceledException) when (timerCancellation.IsCancellationRequested) { }
            if (!pipelineSucceeded && output is ICapturedTurnOutput { HasQueuedFinal: false } capturedOutput)
                try { await capturedOutput.CancelTurnAsync(CancellationToken.None); } catch { }
            try { await output.PublishAsync(TranslationUpdate.Typing(false), CancellationToken.None); } catch { }
        }
    }

    internal const int CompleteSourceLimit = 2048;
    internal static string FormatChatDisplay(string translated, bool multipleLanguages = false)
    {
        if (multipleLanguages) return MultiLanguageChatFormatter.Format(translated);
        // Only the wire/display copy is limited. The model's complete source and
        // translation remain unchanged internally. Normalize display line breaks
        // so CRLF or lone CR cannot bypass VRChat's nine-line limit.
        string display = VrChatTextFormatter.Format(translated.Replace("\r\n", "\n").Replace('\r', '\n'));
        int excessLines = display.Count(character => character == '\n') - 8;
        if (excessLines <= 0) return display;
        int start = 0;
        for (int line = 0; line < excessLines; line++) start = display.IndexOf('\n', start) + 1;
        string tail = display[start..];
        return VrChatTextFormatter.Format(tail.StartsWith('…') ? tail : "…" + tail);
    }
    internal static ResolvedRealtimeSettings PreviewSchedulingSettings(ResolvedRealtimeSettings settings, int targetCount = 1) => settings with
    { MinimumIntervalMs = 1500 * Math.Clamp(targetCount, 1, 4), MaximumIntervalMs = 3000 * Math.Clamp(targetCount, 1, 4), MinimumChangedWords = 1, MaxSourceCharacters = CompleteSourceLimit };
    internal static bool UsefulPreviewSource(string source)
    {
        if (source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(UtteranceTracking.HasLexicalContent) >= 3) return true;
        int nonAsciiLexical = 0;
        foreach (var rune in source.EnumerateRunes())
            if (rune.Value > 127 && Rune.IsLetterOrDigit(rune) && ++nonAsciiLexical >= 6) return true;
        return false;
    }

    private static string Metric(double? value) => value is double number && double.IsFinite(number)
        ? number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
}

internal sealed class CapturedTurnFailureException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

// Log files and redirected output can be unavailable while translation is
// healthy. A diagnostic I/O failure must not abort a capture/translation turn.
// Unexpected programming errors still propagate to the caller.
internal sealed class BestEffortHostLogger(Action<string> writeConsole)
{
    private readonly object gate = new();
    private long lastWarning;
    private bool warningReported;

    public void Write(string path, string line)
    {
        lock (gate)
        {
            TryConsole(line);
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                stream.Write(bytes);
                stream.Flush();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                long now = Stopwatch.GetTimestamp();
                if (!warningReported || now - lastWarning >= Stopwatch.Frequency * 30L)
                {
                    warningReported = true; lastWarning = now;
                    // Do not include paths, conversation text or exception
                    // bodies, and never recurse into the unavailable file.
                    TryConsole("WARNING own-voice: code=log-write-unavailable; The local log could not be updated. Voice translation continues.");
                }
            }
        }
    }

    private void TryConsole(string line)
    {
        try { writeConsole(line); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

// The deadline is about new lexical content during continuing real voice, not
// transport keepalives, padded final audio, punctuation or elapsed idle time.
internal sealed class OwnSpeechProgressDeadline(DateTimeOffset started, long voicedSamples)
{
    private readonly object gate = new();
    private string lexicalText = "";
    private DateTimeOffset lastProgress = started;
    private long progressVoicedSamples = voicedSamples;
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    internal void Observe(string text, DateTimeOffset now, long capturedVoicedSamples)
    {
        var lexical = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) lexical.Append(Rune.ToLowerInvariant(rune).ToString());
        string normalized = lexical.ToString();
        if (normalized.Length == 0) return;
        lock (gate)
        {
            if (normalized == lexicalText) return;
            lexicalText = normalized;
            lastProgress = now;
            progressVoicedSamples = capturedVoicedSamples;
        }
    }
    internal bool IsStalled(DateTimeOffset now, long capturedVoicedSamples, bool ongoingVoice)
    {
        lock (gate) return ongoingVoice && now - lastProgress >= Deadline &&
            capturedVoicedSamples - progressVoicedSamples >= 16000 * 2;
    }
}

internal interface ICapturedSpeechSource : IAudioSource
{
    bool HasSpeech { get; }
}

internal interface ICapturedTurnOutput
{
    bool HasQueuedFinal { get; }
    Task BeginTurnAsync(CancellationToken token);
    Task CancelTurnAsync(CancellationToken token);
}

// A newer ASR token must not starve useful in-flight snapshots. This boundary
// rejects only already-delivered/older revisions, mute, failure or a real final.
internal sealed class CumulativePreviewOutput(IOutputSink inner, bool enabled, Func<bool> allowed) : IOutputSink, IDisposable
{
    private readonly SemaphoreSlim publication = new(1, 1);
    private int closed;
    private long deliveredRevision;
    public string Name => inner.Name;
    public bool PreviewAllowed => enabled && Volatile.Read(ref closed) == 0 && allowed();
    public void ClosePreviews() => Interlocked.Exchange(ref closed, 1);
    public async Task PublishAsync(TranslationUpdate update, CancellationToken token)
    {
        bool preview = update.Kind == TranslationUpdateKind.Translation && !update.IsSettled;
        if (preview && (!PreviewAllowed || update.Revision <= Interlocked.Read(ref deliveredRevision))) return;
        if (update.Kind == TranslationUpdateKind.Translation && update.IsSettled) ClosePreviews();
        await publication.WaitAsync(token);
        try
        {
            if (preview && (!PreviewAllowed || update.Revision <= Interlocked.Read(ref deliveredRevision))) return;
            await inner.PublishAsync(update, token);
            if (preview) Interlocked.Exchange(ref deliveredRevision, update.Revision);
        }
        finally { publication.Release(); }
    }
    public void Dispose() => publication.Dispose();
}

internal sealed class CapturedTurn(AudioFormat format) : ICapturedSpeechSource
{
    // At most 15 seconds of unread PCM per turn; never an unbounded backlog.
    private readonly Channel<AudioFrame> frames = Channel.CreateBounded<AudioFrame>(
        // Failure cleanup may drain while the cancelled ASR sender unwinds.
        new BoundedChannelOptions(750) { SingleWriter = true, SingleReader = false });
    private int discarded;
    private int completed;
    private readonly object frameGate = new();
    private long bufferedBytes;
    internal long MaximumBufferedBytes => (long)Format.BytesPerSecond * 15;
    internal long BufferedBytes => Interlocked.Read(ref bufferedBytes);
    private int framesWritten;
    private static long nextId;
    private readonly long openedTimestamp = Stopwatch.GetTimestamp();
    private long processingTimestamp, lastVoiceTimestamp, sampleCount, voicedSampleCount, lastVoiceSample;
    private int outcomeReported;
    private string currentStage = "captured";
    public long Id { get; } = Interlocked.Increment(ref nextId);
    public int FramesWritten => Volatile.Read(ref framesWritten);
    public long SamplesCaptured => Interlocked.Read(ref sampleCount);
    public long VoicedSamples => Interlocked.Read(ref voicedSampleCount);
    public bool IsDiscarded => Volatile.Read(ref discarded) != 0;
    public string CurrentStage => Volatile.Read(ref currentStage);
    public bool HasOngoingVoice => Volatile.Read(ref completed) == 0 && !IsDiscarded &&
        Interlocked.Read(ref lastVoiceTimestamp) is long stamp && stamp != 0 &&
        Stopwatch.GetElapsedTime(stamp) < TimeSpan.FromSeconds(1);
    public AudioFormat Format => format;
    public bool HasSpeech => VoicedSamples > 0;
    public bool TryWrite(AudioFrame frame, bool voiced = true)
    {
        Interlocked.Increment(ref framesWritten);
        long end = Interlocked.Add(ref sampleCount, frame.Pcm.Length / 2);
        if (voiced)
        {
            Interlocked.Add(ref voicedSampleCount, frame.Pcm.Length / 2);
            Interlocked.Exchange(ref lastVoiceSample, end);
            Interlocked.Exchange(ref lastVoiceTimestamp, Stopwatch.GetTimestamp());
        }
        lock (frameGate)
        {
            if (Volatile.Read(ref discarded) != 0) return true;
            if (frame.Pcm.Length > MaximumBufferedBytes ||
                Interlocked.Add(ref bufferedBytes, frame.Pcm.Length) > MaximumBufferedBytes)
            {
                // Oversized frames were not reserved above.
                if (frame.Pcm.Length <= MaximumBufferedBytes) Interlocked.Add(ref bufferedBytes, -frame.Pcm.Length);
                return false;
            }
            if (frames.Writer.TryWrite(frame)) return true;
            Interlocked.Add(ref bufferedBytes, -frame.Pcm.Length);
            return false;
        }
    }
    public void Complete(Exception? error = null, bool verifiedQuiet = false)
    {
        Interlocked.Exchange(ref completed, 1);
        if (frames.Writer.TryComplete(error) && verifiedQuiet)
            Stage("quiet-boundary", $"quiet_audio_ms={AudioMs(Math.Max(0, SamplesCaptured - Interlocked.Read(ref lastVoiceSample)))}");
    }
    public void BeginProcessing()
    {
        Interlocked.CompareExchange(ref processingTimestamp, Stopwatch.GetTimestamp(), 0);
        Stage("processing", $"queue_age_ms={ElapsedMs(openedTimestamp)}");
    }
    public void Stage(string stage, string detail = "")
    {
        Volatile.Write(ref currentStage, stage);
        LocalHost.Log($"TURN stage id={Id}; stage={stage}; since_open_ms={ElapsedMs(openedTimestamp)}; captured_ms={AudioMs(SamplesCaptured)}; voiced_ms={AudioMs(VoicedSamples)}" +
            (detail.Length == 0 ? "." : "; " + detail + "."));
    }
    public void Outcome(string outcome)
    {
        if (Interlocked.Exchange(ref outcomeReported, 1) != 0) return;
        long processing = Interlocked.Read(ref processingTimestamp), lastVoice = Interlocked.Read(ref lastVoiceTimestamp);
        LocalHost.Log($"TURN outcome id={Id}; outcome={outcome}; stage={CurrentStage}; since_open_ms={ElapsedMs(openedTimestamp)}; processing_ms={(processing == 0 ? 0 : ElapsedMs(processing))}; captured_ms={AudioMs(SamplesCaptured)}; voiced_ms={AudioMs(VoicedSamples)}; last_voice_age_ms={(lastVoice == 0 ? 0 : ElapsedMs(lastVoice))}.");
    }
    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    private long AudioMs(long samples) => samples * 1000 / Format.SampleRate;
    public void Discard()
    {
        lock (frameGate)
        {
            Interlocked.Exchange(ref discarded, 1);
            while (frames.Reader.TryRead(out var frame)) Interlocked.Add(ref bufferedBytes, -frame.Pcm.Length);
        }
    }
    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var frame in frames.Reader.ReadAllAsync(token))
        {
            Interlocked.Add(ref bufferedBytes, -frame.Pcm.Length);
            yield return frame;
        }
        // input_audio.end asks the server to perform its authoritative model
        // right-padding and final flush. Do not add another synthetic PCM tail.
        token.ThrowIfCancellationRequested();
        Stage("asr-await-final");
    }
}

internal sealed class ContinuousCapture(IAudioSource microphone, Action stopCapture,
    AudioFormat format, int pauseMs)
{
    // One extra finite pending turn absorbs a normal slow final without killing
    // capture and throwing away the sentence already queued behind it.
    internal const int MaximumPendingTurns = 2;
    private readonly Channel<CapturedTurn> turns = Channel.CreateBounded<CapturedTurn>(
        new BoundedChannelOptions(MaximumPendingTurns) { SingleReader = true, SingleWriter = true });
    private int draining;
    private int started;
    private int _pauseMilliseconds = pauseMs;
    internal int PauseMilliseconds => Volatile.Read(ref _pauseMilliseconds);
    public void SetPauseMilliseconds(int milliseconds)
    {
        if (milliseconds is < 900 or > 3500) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        Volatile.Write(ref _pauseMilliseconds, milliseconds);
    }
    public bool IsDraining => Volatile.Read(ref draining) != 0;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public void PauseAndDrain()
    {
        if (Interlocked.Exchange(ref draining, 1) == 0) stopCapture();
    }
    public void Start(CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Capture was started twice.");
        Completion = PumpAsync(token);
    }
    public IAsyncEnumerable<CapturedTurn> ReadTurnsAsync(CancellationToken token) => turns.Reader.ReadAllAsync(token);
    private async Task PumpAsync(CancellationToken token)
    {
        var preRoll = new Queue<(AudioFrame Frame, bool Voiced)>();
        int preRollSamples = 0;
        int preRollLimit = format.SampleRate * 300 / 1000;
        long quietSamples = 0;
        int consecutiveVoice = 0;
        int frameCount = 0, voiceFrames = 0, opened = 0;
        bool captureReadyReported = false;
        double maxRms = 0;
        bool backpressure = false;
        long skippedSamples = 0;
        CapturedTurn? current = null;
        Exception? failure = null;
        var lastLog = DateTimeOffset.UtcNow;
        try
        {
            LocalHost.Log($"CAPTURE pre-roll gate active; ASR remains closed until voice. threshold RMS=0.006; pre-roll=300ms; pending turns<={MaximumPendingTurns}; unread turn PCM<=15s.");
            await foreach (AudioFrame frame in microphone.ReadFramesAsync(token).WithCancellation(token))
            {
                int samples = frame.Pcm.Length / 2;
                if (samples > 0 && !captureReadyReported)
                {
                    captureReadyReported = true;
                    LocalHost.Log("SAYWHAT_STATUS component=microphone-host event=capture-ready");
                }
                double sum = 0;
                ReadOnlySpan<byte> pcm = frame.Pcm.Span;
                for (int i = 0; i + 1 < pcm.Length; i += 2)
                { double x = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]) / 32768d; sum += x * x; }
                double rms = samples == 0 ? 0 : Math.Sqrt(sum / samples);
                bool voice = rms >= 0.006;
                frameCount++;
                if (voice) voiceFrames++;
                maxRms = Math.Max(maxRms, rms);
                if (current is { IsDiscarded: true })
                {
                    long failedTurnId = current.Id;
                    current.Complete();
                    current = null;
                    quietSamples = 0; consecutiveVoice = 0;
                    preRoll.Clear(); preRollSamples = 0;
                    LocalHost.Log($"CAPTURE failed turn {failedTurnId} released; fresh voice can reopen the bounded gate without waiting for sentence silence.");
                }
                // Muting may leave a small already-captured native carry. It can
                // finish an existing valid turn, but cannot open a replacement
                // turn after a failed pointer has been discarded.
                if (IsDraining && current is null) continue;
                if (backpressure)
                {
                    if (turns.Reader.Count >= MaximumPendingTurns)
                    {
                        skippedSamples += samples;
                        continue;
                    }
                    backpressure = false;
                    preRoll.Clear(); preRollSamples = 0; consecutiveVoice = 0;
                    LocalHost.Log($"OWN VOICE recovered; code=capture-ready; skipped_audio_ms={skippedSamples * 1000 / format.SampleRate}; queued captured turns kept.");
                    skippedSamples = 0;
                }
                if (current is null)
                {
                    preRoll.Enqueue((frame, voice));
                    preRollSamples += samples;
                    while (preRoll.Count > 1 && preRollSamples > preRollLimit)
                        preRollSamples -= preRoll.Dequeue().Frame.Pcm.Length / 2;
                    consecutiveVoice = voice ? consecutiveVoice + 1 : 0;
                    if (consecutiveVoice >= 2)
                    {
                        current = new CapturedTurn(format);
                        foreach (var captured in preRoll)
                            if (!current.TryWrite(captured.Frame, captured.Voiced)) throw new AudioBufferOverflowException(750);
                        preRoll.Clear(); preRollSamples = 0; quietSamples = 0; consecutiveVoice = 0;
                        if (!turns.Writer.TryWrite(current))
                        {
                            skippedSamples = current.SamplesCaptured;
                            current.Discard(); current.Complete(); current.Outcome("capture-backpressure");
                            current = null; backpressure = true;
                            LocalHost.Log($"ERROR own-voice: code=capture-backpressure; pending_turns={MaximumPendingTurns}; queue retained; fresh audio skipped until a slot opens.");
                            continue;
                        }
                        LocalHost.Log($"VOICE start turn {++opened}; RMS={rms:F5}; captureFrames={frameCount}; voicedFrames={voiceFrames}.");
                    }
                }
                else
                {
                    if (!current.TryWrite(frame, voice))
                    {
                        long interruptedId = current.Id;
                        current.Discard();
                        current.Complete(new CapturedTurnFailureException("capture-buffer-overflow",
                            "The captured turn exceeded its 15-second unread PCM limit; queued captured turns are kept and fresh capture may resume."));
                        current.Outcome("capture-buffer-overflow");
                        current = null; quietSamples = 0; consecutiveVoice = 0;
                        preRoll.Clear(); preRollSamples = 0;
                        LocalHost.Log($"ERROR own-voice: code=capture-buffer-overflow; turn={interruptedId}; pending_turns={turns.Reader.Count}; queued captured turns kept.");
                        continue;
                    }
                    quietSamples = voice ? 0 : quietSamples + samples;
                    if (quietSamples >= (long)format.SampleRate * Volatile.Read(ref _pauseMilliseconds) / 1000)
                    {
                        current.Complete(verifiedQuiet: true);
                        LocalHost.Log($"VOICE quiet turn {opened}; verified PCM silence={quietSamples * 1000 / format.SampleRate}ms; requesting real ASR final.");
                        current = null; quietSamples = 0;
                    }
                }
                var now = DateTimeOffset.UtcNow;
                if (now - lastLog >= TimeSpan.FromSeconds(5))
                {
                    LocalHost.Log($"CAPTURE levels: frames={frameCount}, voiced={voiceFrames}, maxRms={maxRms:F5}, activeTurn={current is not null}, queuedTurns={turns.Reader.Count}.");
                    LocalHost.Log("SAYWHAT_STATUS component=microphone-host event=capture-levels frames=" +
                        frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " voiced=" +
                        voiceFrames.ToString(System.Globalization.CultureInfo.InvariantCulture) + " peak=" +
                        Math.Clamp((int)Math.Round(maxRms * 1_000_000), 0, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    lastLog = now; maxRms = 0;
                }
            }
            if (!IsDraining) throw new IOException("Microphone source ended unexpectedly; unfinished captured audio is not a verified final.");
        }
        catch (Exception error)
        {
            failure = error;
            PauseAndDrain();
            LocalHost.Log("CAPTURE gate error: " + error.Message);
            throw;
        }
        finally
        {
            preRoll.Clear();
            if (backpressure) LocalHost.Log($"ERROR own-voice: code=capture-backpressure; skipped_audio_ms={skippedSamples * 1000 / format.SampleRate}; capture stopped; queued captured turns kept.");
            current?.Complete(failure);
            turns.Writer.TryComplete(failure);
            LocalHost.Log($"CAPTURE closed; frames={frameCount}; voiced={voiceFrames}; turns={opened}; no new microphone input remains.");
        }
    }
}

internal sealed class ControlledAudioSource(IAudioSource inner, Action stopCapture, AudioFormat format) : ICapturedSpeechSource
{
    private long lastSpeechTicks;
    private long lastFrameTicks;
    private int draining;
    public AudioFormat Format => format;
    public bool IsDraining => Volatile.Read(ref draining) != 0;
    public bool HasSpeech => Interlocked.Read(ref lastSpeechTicks) != 0;
    public void Observe(AudioFrame frame)
    {
        if (IsDraining) return;
        double sum = 0;
        var pcm = frame.Pcm.Span;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        { double x = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]) / 32768d; sum += x * x; }
        long now = DateTimeOffset.UtcNow.UtcTicks;
        Interlocked.Exchange(ref lastFrameTicks, now);
        if (pcm.Length > 0 && Math.Sqrt(sum / (pcm.Length / 2)) >= 0.006)
            Interlocked.Exchange(ref lastSpeechTicks, now);
    }
    public bool CanSettle(DateTimeOffset now, int silenceMs) => !IsDraining &&
        Interlocked.Read(ref lastSpeechTicks) != 0 &&
        now.UtcTicks - Interlocked.Read(ref lastSpeechTicks) >= TimeSpan.FromMilliseconds(silenceMs).Ticks &&
        now.UtcTicks - Interlocked.Read(ref lastFrameTicks) < TimeSpan.FromSeconds(1).Ticks;
    public void PauseAndDrain()
    {
        if (Interlocked.Exchange(ref draining, 1) == 0) stopCapture();
    }
    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var frame in inner.ReadFramesAsync(token).WithCancellation(token)) yield return frame;
        if (!IsDraining) throw new IOException("Microphone capture ended unexpectedly.");
        // Only synthetic silence follows mute. No new device samples are captured.
        var silence = new AudioFrame(new byte[640], Format);
        for (int i = 0; i < 60; i++) { token.ThrowIfCancellationRequested(); yield return silence; }
    }
}

internal sealed class FinalOscSink(ResolvedOscEndpoint endpoint, string session, CapturedTurn? turn = null, bool multipleLanguages = false) : IOutputSink, ICapturedTurnOutput, IDisposable
{
    private readonly UdpClient udp = new();
    private readonly HashSet<(long Epoch, long Utterance)> delivered = [];
    private readonly Queue<(long Epoch, long Utterance)> deliveredOrder = [];
    public string Name => "Local captured-turn OSC";
    public int FinalCount { get; private set; }
    public bool HasQueuedFinal => FinalCount > 0;
    private int started;
    public async Task BeginTurnAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
            await SendAsync(Osc.Packet("/foxtrans/control/captured-start", ",s", session), token);
    }
    public Task CancelTurnAsync(CancellationToken token) =>
        SendAsync(Osc.Packet("/foxtrans/control/captured-cancel", ",s", session), token);
    private Task SendAsync(byte[] packet, CancellationToken token) => udp.SendAsync(packet,
        new IPEndPoint(IPAddress.Parse(endpoint.Host == "localhost" ? "127.0.0.1" : endpoint.Host), endpoint.Port), token).AsTask();
    public async Task PublishAsync(TranslationUpdate update, CancellationToken token)
    {
        if (update.Kind == TranslationUpdateKind.Typing && endpoint.TypingIndicator)
        {
            await udp.SendAsync(OscPacketFormatter.Typing(update.IsTyping),
                new IPEndPoint(IPAddress.Parse(endpoint.Host == "localhost" ? "127.0.0.1" : endpoint.Host), endpoint.Port), token);
            return;
        }
        if (update.Kind != TranslationUpdateKind.Translation || string.IsNullOrWhiteSpace(update.Text)) return;
        if (!update.IsSettled)
        {
            if (update.Revision is <= 0 or > int.MaxValue) return;
            await SendAsync(Osc.PreviewPacket(session, (int)update.Revision, LocalHost.FormatChatDisplay(update.Text, multipleLanguages)), token);
            turn?.Stage("preview-queued", $"revision={update.Revision}; translated_chars={update.Text.Length}");
            return;
        }
        if (!delivered.Add((update.TranscriptEpoch, update.UtteranceId))) return;
        deliveredOrder.Enqueue((update.TranscriptEpoch, update.UtteranceId));
        while (deliveredOrder.Count > 64) delivered.Remove(deliveredOrder.Dequeue());
        byte[] packet = Osc.Packet("/foxtrans/control/captured-final", ",ss",
            session, LocalHost.FormatChatDisplay(update.Text, multipleLanguages));
        await udp.SendAsync(packet, new IPEndPoint(IPAddress.Parse(endpoint.Host == "localhost" ? "127.0.0.1" : endpoint.Host), endpoint.Port), token);
        FinalCount++;
        LocalHost.Log("SAYWHAT_STATUS component=microphone-host event=chat-queued chars=" +
            Math.Min(1_000_000, update.Text.Length).ToString(System.Globalization.CultureInfo.InvariantCulture));
        LocalHost.Log($"FINAL queued e{update.TranscriptEpoch}/u{update.UtteranceId}; {update.Text.Length} translated chars.");
        turn?.Stage("final-queued", $"translated_chars={update.Text.Length}");
    }
    public void Dispose() => udp.Dispose();
}

internal sealed class MetadataReporter(CapturedTurn? turn = null, bool previews = false) : IAppReporter
{
    public bool Failed { get; private set; }
    private int finalPhase = previews ? 0 : 1;
    public void EnterFinalPhase() => Volatile.Write(ref finalPhase, 1);
    public void Report(AppEvent item)
    {
        if (item.Kind == AppEventKind.TranslationRequestStarted)
        {
            LocalHost.Log("SAYWHAT_STATUS component=microphone-host event=translation-start");
            turn?.Stage("translation-start");
        }
        else if (item.Kind == AppEventKind.RealtimeTranslationCompleted && item.TranslationTelemetry is { } t)
        {
            LocalHost.Log($"LLM e{t.TranscriptEpoch}/u{t.UtteranceId}/r{t.RequestedRevision}: {t.Disposition}; {t.TranslationDuration.TotalMilliseconds:F0} ms.");
            LocalHost.Log("SAYWHAT_STATUS component=microphone-host event=translation-completed duration_ms=" +
                Math.Clamp((long)t.TranslationDuration.TotalMilliseconds, 0, 86_400_000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            turn?.Stage("translation-completed", $"duration_ms={t.TranslationDuration.TotalMilliseconds:F0}; disposition={t.Disposition}");
        }
        else if (item.Kind is AppEventKind.RealtimeTranslationFailed or AppEventKind.OutputError or AppEventKind.RealtimeOutputTimedOut)
        {
            // A speculative translation can fail without poisoning the later
            // definitive sentence. Output/quarantine failures still invalidate
            // this sink, and errors in the final phase remain actual failures.
            if (item.Kind != AppEventKind.RealtimeTranslationFailed || Volatile.Read(ref finalPhase) != 0)
                Failed = true;
            // Provider response bodies can echo speech or prompts. Keep the
            // event type/stage, not those bodies, in this metadata-only host log.
            LocalHost.Log($"ERROR {item.Kind}; turn={turn?.Id}; stage={turn?.CurrentStage}.");
        }
    }
}

internal static class Osc
{
    public static byte[] PreviewPacket(string identity, int revision, string text)
    {
        using var bytes = new MemoryStream();
        bytes.Write(Packet("/foxtrans/control/captured-preview", ",sis", identity));
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, revision); bytes.Write(integer);
        WriteString(bytes, text);
        return bytes.ToArray();
    }
    public static byte[] Packet(string address, string tags, params string[] values)
    {
        using var bytes = new MemoryStream();
        foreach (string value in new[] { address, tags }.Concat(values))
            WriteString(bytes, value);
        return bytes.ToArray();
    }
    private static void WriteString(Stream bytes, string value)
    { bytes.Write(Encoding.UTF8.GetBytes(value)); bytes.WriteByte(0); while ((bytes.Length & 3) != 0) bytes.WriteByte(0); }
    public static bool Read(byte[] bytes, ref int offset, out string value)
    {
        value = "";
        int end = offset;
        while (end < bytes.Length && bytes[end] != 0) end++;
        if (end >= bytes.Length) return false;
        value = Encoding.UTF8.GetString(bytes, offset, end - offset);
        offset = (end + 4) & ~3;
        return offset <= bytes.Length;
    }
}

internal sealed class TranslationProtocolFixture : HttpMessageHandler
{
    public string Body { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Body=await request.Content!.ReadAsStringAsync(token);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("{\"choices\":[{\"message\":{\"content\":\"这是测试。\"}}]}") };
    }
}

internal static class LocalTests
{
    public static async Task<int> RunAsync()
    {
        await HostLoggingTests.RunAsync();
        await NativeSpeechTests.RunAsync();
        await MultiLanguageTests.RunAsync();
        await SpecializedTranslationTests.RunAsync();
        using (var handler = new TranslationProtocolFixture())
        using (var http = new HttpClient(handler))
        {
            string prompt=TranslationModelPrompts.HyMtInstruction("Chinese");
            var hyFixtureTranslator=new OpenAiTextTranslator(http,new(new Uri("http://127.0.0.1:1235/v1/chat/completions"),null,"tencent/hy-mt2-1.8b",prompt));
            Check(await hyFixtureTranslator.TranslateAsync("This is a test.",CancellationToken.None)=="这是测试。","dedicated translation mock response is parsed");
            using JsonDocument payload=JsonDocument.Parse(handler.Body);
            JsonElement body=payload.RootElement;
            Check(body.GetProperty("messages").GetArrayLength()==1 && body.GetProperty("messages")[0].GetProperty("role").GetString()=="user","Hy-MT gets a single user task, not a generic system prompt");
            Check(body.GetProperty("messages")[0].GetProperty("content").GetString()==prompt+"\nThis is a test.","Hy-MT receives complete source and explicit target");
            Check(body.GetProperty("temperature").GetDouble()==.7 && body.GetProperty("top_p").GetDouble()==.6 && body.GetProperty("top_k").GetInt32()==20 && body.GetProperty("max_tokens").GetInt32()==256,"dedicated author sampling and finite output limit are serialized");
        }
        string nativePath = Path.Combine(AppContext.BaseDirectory, "FoxTrans.Native.dll");
        Check(File.Exists(nativePath), "unchanged native dependency is published beside the host, not in an unresolved subfolder");
        IntPtr nativeHandle = System.Runtime.InteropServices.NativeLibrary.Load(nativePath);
        try { Check(System.Runtime.InteropServices.NativeLibrary.GetExport(nativeHandle, "foxtrans_audio_capture_stop") != IntPtr.Zero,
            "native dependency loads and exports capture-stop without opening any audio device"); }
        finally { System.Runtime.InteropServices.NativeLibrary.Free(nativeHandle); }
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // Offline fixtures only; production budgets are unchanged.
        var raw = new FakeCapture();
        var source = new ControlledAudioSource(raw, raw.Stop, raw.Format);
        raw.OnFrame = source.Observe;
        var socket = new FakeSocket();
        await using var provider = new VoxtralFoxTranscriber(
            ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig("http://127.0.0.1:8080", null, 240), null),
            new FakeFactory(socket), finishOnAudioEnd: true);
        var sink = new TestSink();
        var translator = new TestTranslator();
        var run = LocalHost.RunPipelineAsync(source, provider, translator, sink,
            new ResolvedRealtimeSettings(250, 700, 2, 2400, 800), token.Token);
        await Task.Delay(3300, token.Token);
        Check(raw.Frames > 100, "synthetic microphone is continuously speaking");
        Check(sink.Finals.Count == 0, "stale ASR partial cannot finalize while PCM voice remains active beyond 2.4 seconds");
        Check(translator.Requests.Count == 0, "own voice performs no LLM work for partial transcripts while speaking");
        source.PauseAndDrain();
        int count = raw.Frames;
        await run;
        Check(raw.Frames == count, "capture stops immediately on mute; no additional microphone frames");
        Check(socket.EndCount == 1 && socket.CancelCount == 0, "all captured PCM plus finite silence drain through input_audio.end, not cancellation");
        Check(socket.SilentBytes >= 38400, "at least 1.2 seconds of finite synthetic silence reached the server");
        Check(sink.Finals.Count == 1 && sink.Finals[0] == "完整句子", "real server final replaces fragment; exactly one full translated sentence is emitted");
        Check(translator.Requests.SequenceEqual(new[] { "testing testing is this working" }), "exactly one LLM request receives the definitive full source, not its partial fragment");
        await TestNaturalQuietAsync(token.Token);
        await TestTwelveHundredPcmBoundaryAsync(token.Token);
        await TestCaptureCarryAsync(token.Token);
        await TestHealthRetryAsync(token.Token);
        await TestBoundedCaptureAsync(token.Token);
        await TestPendingBackpressureAsync(token.Token);
        await TestMutedBackpressureAsync(token.Token);
        await TestCapturedByteBudgetAsync(token.Token);
        TestSpeechProgressDeadline();
        await TestSpeechProgressRecoveryAsync(token.Token);
        await TestMutedSpeechProgressGraceAsync(token.Token);
        await TestInvalidCompletionAsync(1, token.Token);
        await TestInvalidCompletionAsync(2, token.Token);
        await TestAuthoritativeAudioEndAsync(token.Token);
        await TestQueuedTurnRecoveryAsync(false, token.Token);
        await TestQueuedTurnRecoveryAsync(true, token.Token);
        await TestEarlyContinuousVoiceRecoveryAsync(token.Token);
        await TestFatalCaptureAndCancellationAsync(token.Token);
        await TestMutedPendingTurnAsync(token.Token);
        await TestPostSendFailureMutePermitAsync(token.Token);
        TestPreviewPreferenceAndProtocol();
        await TestPreviewOutputBoundaryAsync(token.Token);
        await TestCumulativeEarlyPipelineAsync(token.Token);
        await TestPreviewFailureStillFinalizesAsync(token.Token);
        await TestMultiLanguageFinalPipelineAsync(token.Token);
        await TestCompleteContextAndPreviewMuteAsync(token.Token);
        await TestLivePreviewPreferenceAsync(token.Token);
        await TestLivePauseBoundaryAsync(token.Token);
        Console.WriteLine("PASS: isolated capture gate, authoritative finish, failed-turn queue recovery, cancellation/mute safety, exactly one final.");
        return 0;
    }

    private static async Task TestLivePreviewPreferenceAsync(CancellationToken token)
    {
        var clock = new ManualClock();
        var provider = new ScriptedTranscriber();
        var translator = new GatedTranslator();
        var sink = new TestSink();
        int early = 1;
        Task run = LocalHost.RunPipelineAsync(CompletedTurn(), provider, translator, sink, TestSettings(), token,
            enableEarlyPreviews: true, getUtcNow: clock.Now, earlyPreviewPreference: () => Volatile.Read(ref early) == 1);
        provider.Emit(new StreamingPartialTranscript(1, "Today I went", 240));
        await WaitUntilAsync(() => provider.Processed >= 1, token);
        clock.Advance(1500); await WaitUntilAsync(() => translator.Count == 1, token);
        Volatile.Write(ref early, 0);
        await WaitUntilAsync(() => translator.Cancelled >= 1, token);
        provider.Emit(new StreamingPartialTranscript(2, "Today I went to walk my dog", 480));
        await WaitUntilAsync(() => provider.Processed >= 2, token);
        clock.Advance(1500); await Task.Delay(100, token);
        Check(translator.Count == 1 && sink.Previews.Count == 0, "disabling early translations live cancels speculative work and preserves captured context without output");
        Volatile.Write(ref early, 1);
        provider.Emit(new StreamingPartialTranscript(3, "Today I went to walk my dog and visited the store", 720));
        await WaitUntilAsync(() => translator.Count == 2, token);
        translator.Complete(0, "canceled obsolete preview");
        translator.Complete(1, "new enabled preview");
        await WaitUntilAsync(() => sink.Previews.Count == 1, token);
        provider.Emit(new StreamingFinalTranscript(4, "Today I went to walk my dog and visited the store.", 900));
        await WaitUntilAsync(() => provider.Processed >= 4, token);
        provider.Emit(new StreamingSessionCompleted(5, 1000)); provider.Complete();
        await WaitUntilAsync(() => translator.Count == 3, token);
        translator.Complete(2, "complete authoritative final"); await run;
        Check(sink.Previews.SequenceEqual(new[] { "new enabled preview" }) && sink.Finals.SequenceEqual(new[] { "complete authoritative final" }) &&
            translator.Source(2) == "Today I went to walk my dog and visited the store.",
            "reenabling early translations uses the same running pipeline and retains the complete exactly-once final");
    }

    private static async Task TestLivePauseBoundaryAsync(CancellationToken token)
    {
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 1200);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        try
        {
            raw.PushVoice(3); raw.PushQuiet(10);
            Check(await turns.MoveNextAsync(), "live-pause fixture owns one continuously captured synthetic sentence");
            async Task DrainAsync() { await foreach (var frame in turns.Current.ReadFramesAsync(token)) { } }
            Task drain = DrainAsync();
            await WaitUntilAsync(() => raw.FramesRead >= 13, token);
            Check(!drain.IsCompleted, "the active sentence is not prematurely finalized before the updated silence threshold");
            capture.SetPauseMilliseconds(900);
            raw.PushQuiet(35); await drain;
            Check(!capture.Completion.IsCompleted && capture.PauseMilliseconds == 900,
                "changing sentence pause applies to the active captured sentence without restarting capture or models");
        }
        finally { capture.PauseAndDrain(); await capture.Completion; }
    }

    private static async Task TestAuthoritativeAudioEndAsync(CancellationToken token)
    {
        var turn = new CapturedTurn(new(16000, 16, 1));
        byte[] quiet = new byte[640];
        byte[] carry = Enumerable.Range(0, 646).Select(i => (byte)(i % 251)).ToArray();
        Check(turn.TryWrite(new(quiet, turn.Format), voiced: false) && !turn.HasSpeech,
            "quiet capture metadata does not pretend speech was heard");
        Check(turn.TryWrite(new(carry, turn.Format), voiced: true), "finite turn accepts voiced PCM and a sub-frame carry");
        turn.Complete();
        var received = new List<byte>();
        await foreach (var frame in turn.ReadFramesAsync(token)) received.AddRange(frame.Pcm.ToArray());
        Check(received.SequenceEqual(quiet.Concat(carry)), "authoritative finish sends exactly captured PCM; no redundant 1.2-second client padding");
        Check(turn.SamplesCaptured == 643 && turn.VoicedSamples == 323 && turn.FramesWritten == 2,
            "privacy-safe captured and voiced durations count real PCM including carry");
        var socket = new FakeSocket();
        await using var provider = NewProvider(socket);
        var source = CompletedTurn();
        var sink = new TestSink();
        await LocalHost.RunPipelineAsync(source, provider, new TestTranslator(), sink, TestSettings(), token);
        Check(socket.PcmBytes == source.SamplesCaptured * 2 && socket.EndCount == 1 && socket.CancelCount == 0,
            "finite host PCM has exactly one input_audio.end and no extra synthetic audio or cancellation");
        Check(sink.Finals.Count == 1 && sink.TypingStates.LastOrDefault() == false,
            "server final and completion still produce one full final and clear typing after padding removal");
    }

    private static async Task TestQueuedTurnRecoveryAsync(bool timeout, CancellationToken token)
    {
        var raw = new ScriptedCapture();
        // This fixture uses 40 ms only to test the production queue deterministically;
        // the configured production sentence pause remains 2400 ms.
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 40);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        try
        {
            raw.PushSentence();
            Check(await turns.MoveNextAsync(), "synthetic first sentence opens an isolated bounded turn");
            CapturedTurn first = turns.Current;
            var badSocket = new FakeSocket
            {
                FirstText = "stale partial that must not be sent",
                FinalText = "", EndMode = timeout ? 3 : 0, EndResponseDelayMs = 150
            };
            await using var badProvider = NewProvider(badSocket);
            var badSink = new TestSink();
            var badTranslator = new TestTranslator();
            Task<bool> failing = LocalHost.ProcessCapturedTurnAsync(first, capture.Completion,
                () => LocalHost.RunPipelineAsync(first, badProvider, badTranslator, badSink, TestSettings(), token), token);
            await WaitUntilAsync(() => Volatile.Read(ref badSocket.EndCount) == 1, token);
            raw.PushSentence();
            await WaitUntilAsync(() => Volatile.Read(ref raw.FramesRead) >= 10, token);
            Check(!await failing && first.IsDiscarded && !capture.Completion.IsCompleted,
                timeout ? "ASR completion timeout is isolated without recycling healthy microphone capture"
                        : "empty real ASR final is isolated without recycling healthy microphone capture");
            Check(badSink.Finals.Count == 0 && badTranslator.Requests.Count == 0 &&
                badSink.TypingStates.Count > 0 && !badSink.TypingStates[^1],
                "failed turn never translates or sends its stale partial and always clears its typing indicator");
            Check(await turns.MoveNextAsync(), "already-captured next sentence survives the failed turn");
            CapturedTurn second = turns.Current;
            Check(second.Id != first.Id && !second.IsDiscarded && second.HasSpeech,
                "recovery retains a distinct voiced queued turn, not the failed turn's draft");
            var goodSocket = new FakeSocket();
            await using var goodProvider = NewProvider(goodSocket);
            var goodSink = new TestSink();
            var goodTranslator = new TestTranslator();
            Check(await LocalHost.ProcessCapturedTurnAsync(second, capture.Completion,
                () => LocalHost.RunPipelineAsync(second, goodProvider, goodTranslator, goodSink, TestSettings(), token), token),
                "next captured sentence completes normally after the isolated failure");
            Check(goodSink.Finals.SequenceEqual(new[] { "完整句子" }) && goodTranslator.Requests.Count == 1 &&
                goodSocket.EndCount == 1 && !capture.Completion.IsCompleted,
                "recovered queue publishes one definitive full translation with continuous capture still active");
        }
        finally { capture.PauseAndDrain(); await capture.Completion; }

    }

    private static async Task TestMutedSpeechProgressGraceAsync(CancellationToken token)
    {
        var mutedClock = new ManualClock();
        var mutedRaw = new ScriptedCapture();
        var mutedCapture = new ContinuousCapture(mutedRaw, mutedRaw.Stop, mutedRaw.Format, 40);
        mutedCapture.Start(token);
        await using var mutedTurns = mutedCapture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        try
        {
            mutedRaw.PushVoice(5);
            Check(await mutedTurns.MoveNextAsync(), "muted progress-deadline fixture has already-captured voice");
            CapturedTurn permitted = mutedTurns.Current;
            var mutedSocket = new FakeSocket { FirstText = "" };
            await using var mutedProvider = NewProvider(mutedSocket);
            var mutedSink = new TestSink();
            Task<bool> finishing = LocalHost.ProcessCapturedTurnAsync(permitted, mutedCapture.Completion,
                () => LocalHost.RunPipelineAsync(permitted, mutedProvider, new TestTranslator(), mutedSink,
                    TestSettings(), token, canPreview: () => false, getUtcNow: mutedClock.Now), token);
            await WaitUntilAsync(() => mutedSocket.Binaries > 0, token);
            mutedRaw.PushVoice(105);
            await WaitUntilAsync(() => permitted.VoicedSamples >= 110 * 320, token);
            mutedClock.Advance(9000);
            await Task.Delay(150, token);
            Check(!finishing.IsCompleted && !permitted.IsDiscarded,
                "verified mute excludes the capture-drain race from ongoing-voice progress recovery");
            mutedCapture.PauseAndDrain();
            await mutedCapture.Completion;
            Check(await finishing && mutedSink.Finals.Count == 1 && mutedSocket.EndCount == 1 && mutedSocket.CancelCount == 0,
                "already-captured muted speech can still finish and send its one definitive sentence after the progress grace");
        }
        finally { mutedCapture.PauseAndDrain(); await mutedCapture.Completion; }
    }

    private static async Task TestFatalCaptureAndCancellationAsync(CancellationToken token)
    {
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 40);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        raw.PushSentence();
        Check(await turns.MoveNextAsync(), "fatal-capture fixture has a pending real synthetic turn");
        raw.Stop(); // Deliberately not PauseAndDrain: unexpected device EOF is fatal.
        bool failed = false, invoked = false;
        try { await capture.Completion; } catch (IOException) { failed = true; }
        Check(failed, "unexpected capture-pump termination remains fatal rather than normal quiet");
        failed = false;
        try { await LocalHost.ProcessCapturedTurnAsync(turns.Current, capture.Completion,
            () => { invoked = true; return Task.CompletedTask; }, token); }
        catch (IOException) { failed = true; }
        Check(failed && !invoked, "failed capture pump aborts a pending turn before provider work");
        foreach (Exception fatal in new Exception[] { new IOException("synthetic device failure"), new AudioBufferOverflowException(750), new InvalidOperationException("synthetic invariant failure") })
        {
            failed = false;
            try { await LocalHost.ProcessCapturedTurnAsync(CompletedTurn(), Task.CompletedTask,
                () => Task.FromException(fatal), token); }
            catch (Exception error) when (ReferenceEquals(error, fatal)) { failed = true; }
            Check(failed, "capture/overflow/invariant exception is not disguised as a recoverable provider turn");
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancel.Cancel();
        var cancelled = CompletedTurn(); invoked = false; failed = false;
        try { await LocalHost.ProcessCapturedTurnAsync(cancelled, Task.CompletedTask,
            () => { invoked = true; return Task.CompletedTask; }, cancel.Token); }
        catch (OperationCanceledException) { failed = true; }
        Check(failed && !invoked && cancelled.IsDiscarded, "parent cancellation before a turn discards it without invoking ASR");

        using var runningCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = CompletedTurn();
        var delayed = new FakeSocket { EndResponseDelayMs = 1500 };
        await using var provider = NewProvider(delayed);
        var sink = new TestSink();
        Task<bool> work = LocalHost.ProcessCapturedTurnAsync(running, Task.CompletedTask,
            () => LocalHost.RunPipelineAsync(running, provider, new TestTranslator(), sink, TestSettings(), runningCancel.Token), runningCancel.Token);
        await WaitUntilAsync(() => Volatile.Read(ref delayed.EndCount) == 1, token);
        runningCancel.Cancel(); failed = false;
        try { await work; } catch (OperationCanceledException) { failed = true; }
        Check(failed && running.IsDiscarded && sink.Finals.Count == 0 && sink.TypingStates.Count > 0 && !sink.TypingStates[^1],
            "parent cancellation during server drain propagates, publishes no stale final and clears typing");
    }

    private static async Task TestEarlyContinuousVoiceRecoveryAsync(CancellationToken token)
    {
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 40);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        try
        {
            raw.PushVoice(5);
            Check(await turns.MoveNextAsync(), "early-failure fixture opens while continuous voice has no quiet boundary");
            CapturedTurn first = turns.Current;
            var earlySocket = new FakeSocket { FailFirstAudio = true };
            await using var failingProvider = NewProvider(earlySocket);
            var badSink = new TestSink();
            var badTranslator = new TestTranslator();
            Check(!await LocalHost.ProcessCapturedTurnAsync(first, capture.Completion,
                () => LocalHost.RunPipelineAsync(first, failingProvider, badTranslator, badSink, TestSettings(), token), token),
                "provider failure before sentence end discards only its incomplete turn");
            Check(earlySocket.EndCount == 0 && first.IsDiscarded && !capture.Completion.IsCompleted &&
                badSink.Finals.Count == 0 && badTranslator.Requests.Count == 0 && !badSink.TypingStates[^1],
                "pre-quiet provider failure has no stale final, stops typing and keeps capture healthy");
            raw.PushVoice(5, level: 2500); // Still no quiet PCM between the two portions.
            Check(await turns.MoveNextAsync(), "fresh continuous voice opens a replacement turn without waiting for 2.4-second silence");
            CapturedTurn recovered = turns.Current;
            await WaitUntilAsync(() => recovered.SamplesCaptured >= 1600, token);
            Check(recovered.Id != first.Id && recovered.SamplesCaptured == 1600 && recovered.VoicedSamples == 1600,
                "replacement contains only new voiced PCM, with no failed-turn pre-roll or audio replay");
            raw.PushQuiet(2);
            var socket = new FakeSocket();
            await using var provider = NewProvider(socket);
            var sink = new TestSink();
            Check(await LocalHost.ProcessCapturedTurnAsync(recovered, capture.Completion,
                () => LocalHost.RunPipelineAsync(recovered, provider, new TestTranslator(), sink, TestSettings(), token), token) &&
                sink.Finals.Count == 1 && socket.EndCount == 1 && socket.PcmBytes == 7 * 640,
                "fresh portion completes one authoritative final after early continuous-voice recovery");
        }
        finally { capture.PauseAndDrain(); await capture.Completion; }

        // A muted/draining pump must not reopen after the same failed pointer.
        var mutedRaw = new ScriptedCapture();
        var mutedCapture = new ContinuousCapture(mutedRaw, mutedRaw.Stop, mutedRaw.Format, 40);
        mutedCapture.Start(token);
        await using var mutedTurns = mutedCapture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        mutedRaw.PushVoice(5);
        Check(await mutedTurns.MoveNextAsync(), "muted early-failure fixture has one captured turn");
        await WaitUntilAsync(() => Volatile.Read(ref mutedRaw.FramesRead) >= 5, token);
        mutedTurns.Current.Discard();
        mutedRaw.HoldDelivery();
        mutedRaw.PushVoice(5);
        mutedCapture.PauseAndDrain();
        mutedRaw.ReleaseDelivery();
        await mutedCapture.Completion;
        Check(!await mutedTurns.MoveNextAsync(), "draining muted capture never opens a replacement ASR turn after early failure");
    }

    private static async Task TestMutedPendingTurnAsync(CancellationToken token)
    {
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 40);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        raw.PushSentence();
        Check(await turns.MoveNextAsync(), "mute fixture has one permitted captured sentence");
        CapturedTurn permitted = turns.Current;
        raw.PushSentence();
        await WaitUntilAsync(() => Volatile.Read(ref raw.FramesRead) >= 10, token);
        capture.PauseAndDrain(); await capture.Completion;
        int stoppedAt = raw.FramesRead;
        bool mutedFinalSent = false;
        Check(!LocalHost.ShouldDiscardMutedTurn(true, mutedFinalSent), "muting still permits the first already-captured final");
        var sink = new TestSink();
        await using var provider = NewProvider(new FakeSocket());
        await LocalHost.ProcessCapturedTurnAsync(permitted, capture.Completion,
            () => LocalHost.RunPipelineAsync(permitted, provider, new TestTranslator(), sink, TestSettings(), token), token);
        mutedFinalSent = sink.Finals.Count > 0;
        Check(await turns.MoveNextAsync(), "one pre-mute queued turn remains explicitly observable");
        if (LocalHost.ShouldDiscardMutedTurn(true, mutedFinalSent)) turns.Current.Discard();
        Check(mutedFinalSent && sink.Finals.Count == 1 && turns.Current.IsDiscarded && raw.FramesRead == stoppedAt,
            "muted queue sends at most one final, discards the extra captured turn and captures no new PCM");
        Check(!LocalHost.ShouldDiscardMutedTurn(false, true), "a prior muted final does not disable ordinary unmuted translation");
    }

    private static async Task TestPostSendFailureMutePermitAsync(CancellationToken token)
    {
        var sink = new TestSink();
        bool mutedFinalSent = false;
        bool completed = await LocalHost.ProcessCapturedTurnAsync(CompletedTurn(), Task.CompletedTask, async () =>
        {
            try
            {
                await sink.PublishAsync(new(TranslationUpdateKind.Translation, Text: "synthetic final", IsSettled: true), token);
                throw new CapturedTurnFailureException("translation-or-output-failed", "synthetic post-send drain failure");
            }
            finally { mutedFinalSent |= LocalHost.ShouldMarkMutedFinal(true, sink.Finals.Count); }
        }, token);
        var queued = CompletedTurn();
        if (LocalHost.ShouldDiscardMutedTurn(true, mutedFinalSent)) queued.Discard();
        Check(!completed && sink.Finals.Count == 1 && mutedFinalSent && queued.IsDiscarded,
            "post-send isolated failure retains the used muted permit and cannot authorize a second final");
        Check(!LocalHost.ShouldMarkMutedFinal(true, 0) && !LocalHost.ShouldMarkMutedFinal(false, 1),
            "empty failed turns do not spend a mute permit and pre-mute finals do not spend a future permit");
    }

    private static void TestPreviewPreferenceAndProtocol()
    {
        foreach ((string json, bool expected) in new[]
        {
            ("{}", false), ("{\"OwnVoiceSendEarlyPreviews\":true}", true),
            ("{\"OwnVoiceSendEarlyPreviews\":false}", false), ("{\"OwnVoiceSendEarlyPreviews\":\"true\"}", false),
            ("{\"OwnVoiceSendEarlyPreviews\":null}", false), ("[]", false)
        })
        {
            using var settings = JsonDocument.Parse(json);
            Check(LocalHost.ResolveEarlyPreviewPreference(settings.RootElement) == expected,
                "preview preference uses the default opt-in but malformed values fail safely to final-only");
        }
        Check(!LocalHost.UsefulPreviewSource("Today") && !LocalHost.UsefulPreviewSource("Today I") &&
            LocalHost.UsefulPreviewSource("Today I went") && LocalHost.UsefulPreviewSource("今天我去散步") &&
            !LocalHost.UsefulPreviewSource("😀😀😀😀😀😀"), "first preview requires a useful phrase, not a single word or emoji noise");
        var cadence = LocalHost.PreviewSchedulingSettings(TestSettings());
        Check(cadence.MinimumIntervalMs == 1500 && cadence.MaximumIntervalMs == 3000,
            "speculative requests have an independent conservative cadence rather than token-by-token work");
        byte[] packet = Osc.PreviewPacket("synthetic-turn", 123, "synthetic cumulative text");
        int offset = 0;
        Check(Osc.Read(packet, ref offset, out string address) && address == "/foxtrans/control/captured-preview" &&
            Osc.Read(packet, ref offset, out string tags) && tags == ",sis" &&
            Osc.Read(packet, ref offset, out string identity) && identity == "synthetic-turn",
            "internal preview OSC has the agreed identity/int32/text signature without a transport");
        Check(BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(offset, 4)) == 123, "OSC preview revision is a big-endian int32, not a string");
        offset += 4;
        Check(Osc.Read(packet, ref offset, out string text) && text == "synthetic cumulative text" && offset == packet.Length,
            "preview text follows the integer without trailing or malformed OSC fields");
        string longText = new string('旧', 200) + " recent ending";
        string display = LocalHost.FormatChatDisplay(longText);
        Check(TranslationWindows.TextElementCount(display) <= 144 && display.StartsWith('…') && display.EndsWith("recent ending"),
            "a long chat bubble shows its newest readable tail while source context remains separate");
        string nineLines = string.Join('\n', Enumerable.Range(1, 9).Select(number => "L" + number));
        Check(LocalHost.FormatChatDisplay(nineLines) == nineLines,
            "exactly nine chat lines remain unchanged instead of being needlessly clipped");
        string tenLines = string.Join('\n', Enumerable.Range(1, 10).Select(number => "L" + number));
        Check(LocalHost.FormatChatDisplay(tenLines) == "…" + string.Join('\n', Enumerable.Range(2, 9).Select(number => "L" + number)),
            "a tenth chat line keeps the newest nine lines and marks the omitted prefix");
        string carriageLines = tenLines.Replace("\n", "\r\n");
        Check(LocalHost.FormatChatDisplay(carriageLines) == LocalHost.FormatChatDisplay(tenLines) &&
            LocalHost.FormatChatDisplay(tenLines.Replace('\n', '\r')) == LocalHost.FormatChatDisplay(tenLines),
            "CRLF and lone CR obey the same display-only nine-line limit");
        string unicodeLines = string.Join('\n', Enumerable.Range(1, 20).Select(number => $"说{number} 🐶e\u0301"));
        string unicodeDisplay = LocalHost.FormatChatDisplay(unicodeLines);
        Check(unicodeDisplay.Count(character => character == '\n') <= 8 && TranslationWindows.TextElementCount(unicodeDisplay) <= 144 &&
            unicodeDisplay.EndsWith("说20 🐶e\u0301") && unicodeDisplay.EnumerateRunes().All(rune => rune.Value != 0xFFFD),
            "line and length limits retain the latest Unicode text without splitting graphemes or surrogate pairs");
    }

    private static async Task TestPreviewOutputBoundaryAsync(CancellationToken token)
    {
        var sink = new TestSink();
        bool unmuted = true;
        using var guarded = new CumulativePreviewOutput(sink, true, () => unmuted);
        TranslationUpdate Draft(long revision) => new(TranslationUpdateKind.Translation, Text: "draft " + revision,
            Revision: revision, TranscriptEpoch: 1, UtteranceId: 1);
        await guarded.PublishAsync(Draft(2), token);
        await guarded.PublishAsync(Draft(2), token);
        await guarded.PublishAsync(Draft(1), token);
        Check(sink.Previews.Count == 1, "duplicate/older delivered preview revisions cannot reappear");
        unmuted = false; await guarded.PublishAsync(Draft(3), token);
        Check(sink.Previews.Count == 1 && sink.Finals.Count == 0, "muted previews neither send nor spend the captured-final allowance");
        unmuted = true;
        await guarded.PublishAsync(new(TranslationUpdateKind.Translation, Text: "authoritative final", IsSettled: true, Revision: 4), token);
        await guarded.PublishAsync(Draft(5), token);
        Check(sink.Previews.Count == 1 && sink.Finals.Count == 1,
            "an authoritative final permanently closes this turn's preview output boundary");
    }

    private static async Task TestCumulativeEarlyPipelineAsync(CancellationToken token)
    {
        var clock = new ManualClock();
        var provider = new ScriptedTranscriber();
        var translator = new GatedTranslator();
        var sink = new TestSink();
        Task run = LocalHost.RunPipelineAsync(CompletedTurn(), provider, translator, sink, TestSettings(), token,
            enableEarlyPreviews: true, getUtcNow: clock.Now);
        provider.Emit(new StreamingPartialTranscript(1, "Today I went", 240));
        await WaitUntilAsync(() => provider.Processed >= 1, token);
        clock.Advance(1499);
        await Task.Delay(100, token);
        Check(translator.Count == 0, "early mode does not start a translation before its 1500 ms cadence");
        clock.Advance(1);
        await WaitUntilAsync(() => translator.Count == 1, token);
        provider.Emit(new StreamingPartialTranscript(2, "Today I went to walk my", 480));
        provider.Emit(new StreamingPartialTranscript(3, "Today I went to walk my dog", 720));
        provider.Emit(new StreamingPartialTranscript(4, "Today I went to walk my dog", 800));
        await WaitUntilAsync(() => provider.Processed >= 4, token);
        Check(translator.Count == 1 && translator.MaximumConcurrent == 1,
            "growing ASR coalesces one newest cumulative revision instead of queuing token translations");
        clock.Advance(1500);
        translator.Complete(0, "first early snapshot");
        await WaitUntilAsync(() => sink.Previews.Count == 1 && translator.Count == 2, token);
        Check(translator.Source(0) == "Today I went" && translator.Source(1) == "Today I went to walk my dog",
            "an in-flight cumulative snapshot can publish during growing ASR and the next request keeps the entire latest context");
        Check(sink.Finals.Count == 0, "early submitted snapshots are not counted as authoritative captured finals");
        provider.Emit(new StreamingFinalTranscript(5, "Today I went to walk my dog.", 900));
        await WaitUntilAsync(() => provider.Processed >= 5, token);
        await Task.Delay(100, token);
        Check(translator.Count == 2 && translator.Cancelled >= 1 && sink.Finals.Count == 0,
            "real server final cancels speculative work but cannot send without session.completed");
        provider.Emit(new StreamingSessionCompleted(6, 1000)); provider.Complete();
        await WaitUntilAsync(() => translator.Count == 3, token);
        Check(translator.Source(2) == "Today I went to walk my dog." && translator.MaximumConcurrent == 1,
            "the authoritative final has full cumulative context, no duplicated prefix and priority without concurrent LLM work");
        translator.Complete(1, "late superseded snapshot");
        translator.Complete(2, "definitive translation");
        await run;
        Check(sink.Previews.Count == 1 && sink.Finals.SequenceEqual(new[] { "definitive translation" }) && sink.CancelCount == 0,
            "late cancelled preview cannot follow the exactly-once final, and successful queued output is never cancelled");
        Check(sink.EventOrder.Count >= 2 && sink.EventOrder[0] == "start" && sink.EventOrder[1] == "typing-true",
            "captured-start precedes typing so the proxy can retire old queued previews immediately");
    }

    private static async Task TestCompleteContextAndPreviewMuteAsync(CancellationToken token)
    {
        string completeSource = string.Join(' ', Enumerable.Repeat("context", 150)); // Beyond the legacy 800-character suffix window.
        var mutedProvider = new ScriptedTranscriber();
        var mutedTranslator = new RecordingTranslator();
        var mutedSink = new TestSink();
        Task mutedRun = LocalHost.RunPipelineAsync(CompletedTurn(), mutedProvider, mutedTranslator, mutedSink,
            TestSettings(), token, enableEarlyPreviews: true, canPreview: () => false);
        mutedProvider.Emit(new StreamingPartialTranscript(1, completeSource, 240));
        mutedProvider.Emit(new StreamingFinalTranscript(2, completeSource, 480));
        mutedProvider.Emit(new StreamingSessionCompleted(3, 600)); mutedProvider.Complete();
        await mutedRun;
        Check(mutedTranslator.Requests.SequenceEqual(new[] { completeSource }) && mutedSink.Previews.Count == 0 && mutedSink.Finals.Count == 1,
            "muting suppresses all previews while the one permitted final retains complete source beyond 800 characters");

        var oversized = new ScriptedTranscriber(); var oversizedTranslator = new RecordingTranslator(); var oversizedSink = new TestSink();
        Task tooLong = LocalHost.RunPipelineAsync(CompletedTurn(), oversized, oversizedTranslator, oversizedSink,
            TestSettings(), token, enableEarlyPreviews: true);
        string overcap = new('界', LocalHost.CompleteSourceLimit + 1);
        oversized.Emit(new StreamingPartialTranscript(1, overcap, 240));
        oversized.Emit(new StreamingFinalTranscript(2, overcap, 480));
        oversized.Emit(new StreamingSessionCompleted(3, 600)); oversized.Complete();
        bool limited = false;
        try { await tooLong; } catch (CapturedTurnFailureException error) when (error.Code == "asr-context-limit") { limited = true; }
        Check(limited && oversizedTranslator.Requests.Count == 0 && oversizedSink.Finals.Count == 0 && oversizedSink.CancelCount == 1,
            "over 2048 complete source reports context-limit instead of silently translating a clipped suffix or sending stale queued previews");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new ScriptedTranscriber(); var cancelledSink = new TestSink();
        Task cancelled = LocalHost.RunPipelineAsync(CompletedTurn(), pending, new RecordingTranslator(), cancelledSink,
            TestSettings(), cancel.Token, enableEarlyPreviews: true);
        pending.Emit(new StreamingPartialTranscript(1, "Today I went", 240));
        await WaitUntilAsync(() => pending.Processed >= 1, token);
        cancel.Cancel(); bool propagated = false;
        try { await cancelled; } catch (OperationCanceledException) { propagated = true; }
        Check(propagated && cancelledSink.Previews.Count == 0 && cancelledSink.Finals.Count == 0 && cancelledSink.CancelCount == 1 &&
            !cancelledSink.TypingStates[^1], "cancelling early mode retires queued proxy work, sends no stale draft and clears typing");
    }

    private static async Task TestPreviewFailureStillFinalizesAsync(CancellationToken token)
    {
        var clock = new ManualClock();
        var provider = new ScriptedTranscriber(); var sink = new TestSink();
        var translator = new FailFirstTranslator();
        Task run = LocalHost.RunPipelineAsync(CompletedTurn(), provider, translator, sink, TestSettings(), token,
            enableEarlyPreviews: true, getUtcNow: clock.Now);
        provider.Emit(new StreamingPartialTranscript(1, "Today I went", 240));
        await WaitUntilAsync(() => provider.Processed >= 1, token);
        clock.Advance(1500);
        await WaitUntilAsync(() => translator.Count == 1, token);
        await Task.Delay(100, token);
        provider.Emit(new StreamingFinalTranscript(2, "Today I went to walk my dog.", 480));
        provider.Emit(new StreamingSessionCompleted(3, 600)); provider.Complete();
        await run;
        Check(translator.Count == 2 && sink.Previews.Count == 0 && sink.Finals.Count == 1 && sink.CancelCount == 0,
            "failed speculative translation cannot poison the later successful authoritative sentence");
    }

    private static async Task TestMultiLanguageFinalPipelineAsync(CancellationToken token)
    {
        var provider = new ScriptedTranscriber(); var sink = new TestSink();
        var english = new FixedLanguageTranslator("Hello there.", () => provider.Processed);
        var japanese = new FixedLanguageTranslator("こんにちは。", () => provider.Processed);
        var chinese = new FixedLanguageTranslator("你好。", () => provider.Processed);
        var translator = new MultiTargetTextTranslator(new[] {
            ("English", (ITextTranslator)english), ("Japanese", (ITextTranslator)japanese), ("Chinese", (ITextTranslator)chinese) });
        Task run = LocalHost.RunPipelineAsync(CompletedTurn(), provider, translator, sink, TestSettings(), token,
            enableEarlyPreviews: true, canPreview: () => false);
        provider.Emit(new StreamingPartialTranscript(1, "Hello", 240));
        await WaitUntilAsync(() => provider.Processed >= 1, token);
        Check(english.Requests.Count == 0 && sink.Finals.Count == 0, "multiple targets cannot finalize a muted speculative fragment");
        provider.Emit(new StreamingFinalTranscript(2, "Hello there, full sentence.", 480));
        provider.Emit(new StreamingSessionCompleted(3, 600)); provider.Complete();
        await run;
        Check(sink.Finals.SequenceEqual(new[] { "Hello there.\nこんにちは。\n你好。" }) && sink.Previews.Count == 0 && sink.CancelCount == 0,
            "captured speech completes once as a grouped ordered multi-language final, never one final per language");
        Check(new[] { english, japanese, chinese }.All(target => target.Requests.SequenceEqual(new[] { "Hello there, full sentence." }) && target.CompletedEvents == 3),
            "every selected target sees full source only after both mandatory ASR final/completed events");
        Check(sink.TypingStates[^1] == false, "a multi-language final clears typing normally after captured mute drain");
    }

    private static CapturedTurn CompletedTurn()
    {
        var turn = new CapturedTurn(new(16000, 16, 1));
        turn.TryWrite(new(new byte[640], turn.Format), voiced: true);
        turn.Complete();
        return turn;
    }
    private static ResolvedRealtimeSettings TestSettings() => new(250, 700, 2, 2400, 800);
    private static VoxtralFoxTranscriber NewProvider(FakeSocket socket) => new(
        ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig("http://127.0.0.1:8080", null, 240), null),
        new FakeFactory(socket), finishOnAudioEnd: true);
    private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(5, token);
        Check(ready(), "isolated synthetic fixture reached its expected bounded stage");
    }
    private static async Task TestNaturalQuietAsync(CancellationToken token)
    {
        var raw = new FakeCapture { Quiet = true };
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 2400);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        Task<bool> firstTurn = turns.MoveNextAsync().AsTask();
        await Task.Delay(500, token);
        Check(!firstTurn.IsCompleted, "first-voice gate keeps the ASR turn closed during idle silence");
        raw.Quiet = false;
        Check(await firstTurn, "first real voice opens a pre-rolled bounded captured turn");
        var source = turns.Current;
        var socket = new FakeSocket { FirstText = "testing testing is this working", EndResponseDelayMs = 750 };
        await using var provider = new VoxtralFoxTranscriber(
            ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig("http://127.0.0.1:8080", null, 240), null),
            new FakeFactory(socket), finishOnAudioEnd: true);
        var sink = new TestSink();
        var translator = new TestTranslator();
        var run = LocalHost.RunPipelineAsync(source, provider, translator, sink,
            new ResolvedRealtimeSettings(250, 700, 2, 2400, 800), token);
        await Task.Delay(500, token);
        raw.Quiet = true;
        await Task.Delay(2100, token);
        Check(sink.Finals.Count == 0 && socket.EndCount == 0, "actual microphone silence shorter than 2.4 seconds does not cut the sentence off");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);
        while (socket.EndCount == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10, token);
        Check(socket.EndCount == 1 && sink.Finals.Count == 0, "real PCM quiet requests input_audio.end but delayed ASR cannot publish a stale cached partial");
        Check(translator.Requests.Count == 0, "no LLM request starts before the delayed ASR final and completion acknowledgment");
        raw.Quiet = false;
        Task<bool> queuedTurn = turns.MoveNextAsync().AsTask();
        await Task.Delay(100, token);
        Check(queuedTurn.IsCompletedSuccessfully && queuedTurn.Result, "new voice during normal ASR drain is captured as one bounded pending turn, not lost");
        await run;
        Check(sink.Finals.Count == 1 && sink.Finals[0] == "完整句子", "normal quiet emits a full final once only after actual ASR final/completed");
        Check(translator.Requests.Count == 1, "normal quiet also performs exactly one full-sentence LLM request");
        Check(!capture.Completion.IsCompleted, "microphone capture stays active while unmuted after an ASR turn completes");
        capture.PauseAndDrain();
        int frames = raw.Frames;
        await capture.Completion;
        turns.Current.Discard();
        Check(raw.Frames == frames && sink.Finals.Count == 1, "muting closes continuous capture and does not resend the prior sentence");
    }
    private static async Task TestCaptureCarryAsync(CancellationToken token)
    {
        var format = new AudioFormat(16000, 16, 1);
        var buffer = new NormalizedCaptureFrameBuffer(format, 5, () => { });
        byte[] pcm = Enumerable.Range(0, 646).Select(i => (byte)(i % 253)).ToArray();
        Check(buffer.TryWrite(new AudioFrame(pcm, format)), "arbitrary native callback audio is accepted");
        buffer.Complete();
        var received = new List<byte>();
        await foreach (AudioFrame frame in buffer.ReadAllAsync(token)) received.AddRange(frame.Pcm.ToArray());
        Check(received.SequenceEqual(pcm), "capture stop preserves all PCM including sub-20ms normalizer carry");
    }

    private static async Task TestTwelveHundredPcmBoundaryAsync(CancellationToken token)
    {
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 1200);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        try
        {
            raw.PushVoice(3);
            Check(await turns.MoveNextAsync(), "1.2-second boundary fixture opens a real synthetic voiced turn");
            CapturedTurn source = turns.Current;
            async Task<long> DrainPcm()
            {
                long samples = 0;
                await foreach (var frame in source.ReadFramesAsync(token)) samples += frame.Pcm.Length / 2;
                return samples;
            }
            Task<long> drained = DrainPcm();
            raw.PushQuiet(59); // 1180 ms of actual PCM, not elapsed wall time.
            await WaitUntilAsync(() => source.SamplesCaptured >= (3 + 59) * 320, token);
            Check(!drained.IsCompleted, "1180 ms of consecutive real quiet PCM does not finish a 1200 ms turn");
            raw.PushQuietSamples(160); // Normalizer-like 10 ms carry: 1190 ms total.
            await WaitUntilAsync(() => source.SamplesCaptured >= (3 + 59) * 320 + 160, token);
            Check(!drained.IsCompleted, "1190 ms quiet including a sub-frame carry still cannot finish the sentence");
            raw.PushVoice(1); // Any fresh voice resets the consecutive-quiet count.
            raw.PushQuiet(59);
            await WaitUntilAsync(() => source.SamplesCaptured >= (4 + 118) * 320 + 160, token);
            Check(!drained.IsCompleted, "fresh voice resets the quiet boundary rather than adding two separate pauses");
            raw.PushQuietSamples(160);
            await WaitUntilAsync(() => source.SamplesCaptured >= (4 + 118) * 320 + 320, token);
            Check(!drained.IsCompleted, "1190 ms after fresh voice is still below the chosen 1.2-second boundary");
            raw.PushQuietSamples(160);
            long samples = await drained.WaitAsync(TimeSpan.FromSeconds(2), token);
            Check(samples == (4 + 118) * 320 + 480 && source.VoicedSamples == 4 * 320 && !capture.Completion.IsCompleted,
                "exactly 1200 ms consecutive quiet PCM completes the finite turn while continuous capture stays active");
        }
        finally { capture.PauseAndDrain(); await capture.Completion; }
    }
    private static async Task TestHealthRetryAsync(CancellationToken token)
    {
        var handler = new TestHealthHandler();
        using var http = new HttpClient(handler);
        var settings = ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig("http://127.0.0.1:8080", null, 240), null);
        var health = await LocalHost.WaitReadyAsync(http, settings, Task.CompletedTask, token);
        Check(health.Ready && handler.Requests == 3, "normal transient server-busy lease release retries without dropping captured audio");
        bool failed = false;
        try { await LocalHost.WaitReadyAsync(http, settings, Task.FromException(new IOException("synthetic capture overflow")), token); }
        catch (IOException) { failed = true; }
        Check(failed && handler.Requests == 3, "a failed capture pump aborts health wait immediately instead of hiding overflow");
        using var slow = new HttpClient(new TestHealthHandler { Slow = true });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        failed = false;
        try { await LocalHost.WaitReadyAsync(slow, settings, Task.CompletedTask, token); }
        catch (TimeoutException) { failed = true; }
        Check(failed && elapsed.Elapsed < TimeSpan.FromSeconds(2), "health HTTP request has a real short timeout rather than a 100-second hidden wait");
    }
    private static async Task TestBoundedCaptureAsync(CancellationToken token)
    {
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 2400);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        try
        {
            raw.PushVoice(2);
            Check(await turns.MoveNextAsync(), "bounded-buffer fixture opens one voiced turn");
            CapturedTurn first = turns.Current;
            raw.PushVoice(749);
            await WaitUntilAsync(() => first.IsDiscarded, token);
            Check(first.FramesWritten == 751 && !capture.Completion.IsCompleted,
                "15-second unread PCM cap releases only the overloaded turn, not the healthy capture pump or queue");
            bool failed = false;
            try { await foreach (var _ in first.ReadFramesAsync(token)) { } }
            catch (CapturedTurnFailureException error) { failed = error.Code == "capture-buffer-overflow"; }
            Check(failed, "bounded PCM overflow has a visible typed failed-turn outcome rather than a fabricated successful final");
            raw.PushSentence();
            Check(await turns.MoveNextAsync() && turns.Current.Id != first.Id && turns.Current.HasSpeech,
                "new speech still opens after the bounded unread buffer overflows");
        }
        finally { capture.PauseAndDrain(); await capture.Completion; }
    }

    private static async Task TestPendingBackpressureAsync(CancellationToken token)
    {
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 40);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        try
        {
            raw.PushSentence();
            Check(await turns.MoveNextAsync(), "backpressure fixture starts the first already-captured sentence");
            CapturedTurn first = turns.Current;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> processing = LocalHost.ProcessCapturedTurnAsync(first, capture.Completion, async () =>
            {
                await foreach (var _ in first.ReadFramesAsync(token)) { }
                await release.Task.WaitAsync(token);
            }, token);
            raw.PushSentence(); raw.PushSentence(); raw.PushSentence();
            await WaitUntilAsync(() => raw.FramesRead >= 20, token);
            Check(!capture.Completion.IsCompleted && !first.IsDiscarded,
                "a third pending sentence does not stop capture or invalidate the active sentence");
            release.TrySetResult();
            Check(await processing, "active sentence remains successful even when fresh capture experiences backpressure");
            Check(await turns.MoveNextAsync(), "first queued captured sentence survives backpressure");
            CapturedTurn second = turns.Current;
            Check(await turns.MoveNextAsync(), "one additional finite queued sentence also survives backpressure");
            CapturedTurn third = turns.Current;
            long secondFrames = 0, thirdFrames = 0;
            await foreach (var _ in second.ReadFramesAsync(token)) secondFrames++;
            await foreach (var _ in third.ReadFramesAsync(token)) thirdFrames++;
            Check(secondFrames == 5 && thirdFrames == 5 && second.Id != third.Id &&
                !second.IsDiscarded && !third.IsDiscarded,
                "bounded pending queue preserves two complete distinct sentences without replay or merged audio");
            raw.PushSentence();
            Check(await turns.MoveNextAsync() && turns.Current.HasSpeech && turns.Current.Id != third.Id,
                "fresh microphone speech resumes automatically after a pending slot opens");
            capture.PauseAndDrain(); await capture.Completion;
            Check(!await turns.MoveNextAsync(), "backpressure never grows an unbounded hidden turn queue");
            string log = File.ReadAllText(LocalHost.LogPath);
            Check(log.Contains("code=capture-backpressure; pending_turns=2", StringComparison.Ordinal) &&
                log.Contains("code=capture-ready; skipped_audio_ms=", StringComparison.Ordinal),
                "any unavoidable new-audio gap is explicitly logged together with subsequent capture recovery");
        }
        finally { capture.PauseAndDrain(); await capture.Completion; }
    }

    private static void TestSpeechProgressDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new OwnSpeechProgressDeadline(now, 0);
        Check(!progress.IsStalled(now.AddSeconds(7.9), 64000, true), "normal eight-second startup grace is preserved");
        Check(!progress.IsStalled(now.AddSeconds(90), 31000, true), "brief voiced noise cannot trigger repeated speech-progress recovery");
        Check(!progress.IsStalled(now.AddSeconds(90), 64000, false), "quiet, muted or already-finished audio never trips the own-speech watchdog");
        progress.Observe("Hello!", now.AddSeconds(1), 16000);
        progress.Observe(" hello ... ", now.AddSeconds(8), 64000);
        progress.Observe("", now.AddSeconds(8.5), 65000);
        Check(progress.IsStalled(now.AddSeconds(9), 64000, true),
            "duplicate, case-only, punctuation-only and empty transcript callbacks cannot disguise stalled recognition");
        progress.Observe("Hello world", now.AddSeconds(9), 64000);
        Check(!progress.IsStalled(now.AddSeconds(10), 100000, true), "actual new words reset the progress deadline");
        Check(!progress.IsStalled(now.AddSeconds(20), 65000, true), "no additional real voiced audio after progress cannot falsely trigger recovery");
    }

    private static async Task TestCapturedByteBudgetAsync(CancellationToken token)
    {
        var turn = new CapturedTurn(new(16000, 16, 1));
        Check(turn.MaximumBufferedBytes == 480000 && turn.TryWrite(new(new byte[480000], turn.Format)) &&
            turn.BufferedBytes == 480000 && !turn.TryWrite(new(new byte[2], turn.Format)),
            "unread turn is bounded by exact 15-second PCM bytes as well as frame count");
        turn.Complete();
        await foreach (var _ in turn.ReadFramesAsync(token)) { }
        Check(turn.BufferedBytes == 0, "reading a bounded PCM frame releases its precise byte budget");
        var oversized = new CapturedTurn(turn.Format);
        Check(!oversized.TryWrite(new(new byte[480002], turn.Format)) && oversized.BufferedBytes == 0,
            "one malformed oversized frame cannot bypass the unread PCM byte budget");
        var discarded = new CapturedTurn(turn.Format);
        Check(discarded.TryWrite(new(new byte[640], turn.Format)), "byte-budget discard fixture accepts a normalized frame");
        discarded.Discard();
        Check(discarded.BufferedBytes == 0 && discarded.TryWrite(new(new byte[640], turn.Format)) &&
            discarded.BufferedBytes == 0, "discard releases budget and cannot retain additional raw PCM");
        bool invoked = false;
        Check(!await LocalHost.ProcessCapturedTurnAsync(discarded, Task.CompletedTask,
            () => { invoked = true; return Task.CompletedTask; }, token) && !invoked,
            "an already-failed queued PCM buffer skips provider startup instead of spending another recognition session");
    }

    private static async Task TestMutedBackpressureAsync(CancellationToken token)
    {
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 40);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        raw.PushSentence();
        Check(await turns.MoveNextAsync(), "muted-backpressure fixture has the authorized captured sentence");
        CapturedTurn permitted = turns.Current;
        raw.PushSentence(); raw.PushSentence(); raw.PushSentence();
        await WaitUntilAsync(() => raw.FramesRead >= 20, token);
        capture.PauseAndDrain(); await capture.Completion;
        int stoppedAt = raw.FramesRead;
        var sink = new TestSink();
        await using var provider = NewProvider(new FakeSocket());
        Check(await LocalHost.ProcessCapturedTurnAsync(permitted, capture.Completion,
            () => LocalHost.RunPipelineAsync(permitted, provider, new TestTranslator(), sink, TestSettings(), token), token),
            "mute during queue backpressure still finishes and sends its already-captured authorized sentence");
        bool mutedFinalSent = LocalHost.ShouldMarkMutedFinal(true, sink.Finals.Count);
        int extras = 0;
        while (await turns.MoveNextAsync())
        {
            Check(LocalHost.ShouldDiscardMutedTurn(true, mutedFinalSent), "post-mute extra captured turn is not authorized to send");
            turns.Current.Discard(); extras++;
        }
        Check(sink.Finals.Count == 1 && extras == ContinuousCapture.MaximumPendingTurns && raw.FramesRead == stoppedAt,
            "queue backpressure preserves one muted final, stops capture and cannot send a second queued sentence");
    }

    private static async Task TestSpeechProgressRecoveryAsync(CancellationToken token)
    {
        var clock = new ManualClock();
        var raw = new ScriptedCapture();
        var capture = new ContinuousCapture(raw, raw.Stop, raw.Format, 40);
        capture.Start(token);
        await using var turns = capture.ReadTurnsAsync(token).GetAsyncEnumerator(token);
        try
        {
            raw.PushVoice(5);
            Check(await turns.MoveNextAsync(), "zero-progress fixture captures ongoing synthetic microphone voice");
            CapturedTurn stalled = turns.Current;
            var socket = new FakeSocket { FirstText = "" };
            await using var provider = NewProvider(socket);
            var sink = new TestSink();
            var translator = new TestTranslator();
            Task<bool> processing = LocalHost.ProcessCapturedTurnAsync(stalled, capture.Completion,
                () => LocalHost.RunPipelineAsync(stalled, provider, translator, sink, TestSettings(), token,
                    getUtcNow: clock.Now), token);
            await WaitUntilAsync(() => socket.Binaries > 0, token);
            raw.PushVoice(105);
            await WaitUntilAsync(() => stalled.VoicedSamples >= 110 * 320, token);
            clock.Advance(9000);
            Check(!await processing && stalled.IsDiscarded && !capture.Completion.IsCompleted,
                "continued voiced input without words triggers bounded failed-turn recovery while capture remains alive");
            Check(socket.CancelCount == 1 && socket.EndCount == 0 && translator.Requests.Count == 0 &&
                sink.Finals.Count == 0 && !sink.TypingStates[^1],
                "speech-progress timeout cancels only its session, invents no final and clears typing");
            raw.PushVoice(5); raw.PushQuiet(2);
            Check(await turns.MoveNextAsync() && turns.Current.Id != stalled.Id,
                "fresh voice can resume after zero-word recovery without waiting for a manual restart");
            await using var recoveredProvider = NewProvider(new FakeSocket());
            var recoveredSink = new TestSink();
            Check(await LocalHost.ProcessCapturedTurnAsync(turns.Current, capture.Completion,
                () => LocalHost.RunPipelineAsync(turns.Current, recoveredProvider, new TestTranslator(), recoveredSink,
                    TestSettings(), token), token) && recoveredSink.Finals.Count == 1,
                "first subsequent sentence translates and completes normally after speech progress recovery");
        }
        finally { capture.PauseAndDrain(); await capture.Completion; }
    }
    private static void Check(bool pass, string message)
    { if (!pass) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static async Task TestInvalidCompletionAsync(int mode, CancellationToken token)
    {
        var raw = new FakeCapture();
        var source = new ControlledAudioSource(raw, raw.Stop, raw.Format);
        raw.OnFrame = source.Observe;
        var socket = new FakeSocket { EndMode = mode };
        await using var provider = new VoxtralFoxTranscriber(
            ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig("http://127.0.0.1:8080", null, 240), null),
            new FakeFactory(socket), finishOnAudioEnd: true);
        var sink = new TestSink();
        var run = LocalHost.RunPipelineAsync(source, provider, new TestTranslator(), sink,
            new ResolvedRealtimeSettings(250, 700, 2, 2400, 800), token);
        await Task.Delay(150, token);
        source.PauseAndDrain();
        bool failed = false;
        try { await run; } catch (Exception error) when (error is not OperationCanceledException) { failed = true; }
        Check(failed && sink.Finals.Count == 0, mode == 1
            ? "session.completed without transcript.final fails without publishing a partial"
            : "transcript.final followed by close without session.completed fails without publishing even a cached final");
    }
    private sealed class TestTranslator : ITextTranslator
    {
        public readonly List<string> Requests = [];
        public async Task<string> TranslateAsync(string source, CancellationToken token)
        { Requests.Add(source); await Task.Delay(80, token); return source.Contains("working") ? "完整句子" : "片段"; }
    }
    private sealed class TestHealthHandler : HttpMessageHandler
    {
        public int Requests;
        public bool Slow;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            int count = Interlocked.Increment(ref Requests);
            if (Slow) await Task.Delay(3000, token);
            string json = JsonSerializer.Serialize(new
            {
                status = "ok", ready = true, busy = count < 3, busy_mode = "realtime", server_version = "1.1.0", voxtral_version = "1.1.0", model = "test",
                capabilities = new { sample_rate = 16000, channels = 1, audio_format = "pcm_s16le", max_active_streams = 1,
                    transcription_delay = new { default_ms = 240, supported_ms = new[] { 160, 240, 480 } } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
    private sealed class TestSink : IOutputSink, ICapturedTurnOutput
    {
        public string Name => "test, no UDP";
        public List<string> Finals = [];
        public List<bool> TypingStates = [];
        public List<string> Previews = [];
        public List<string> EventOrder = [];
        public int CancelCount;
        public bool HasQueuedFinal => Finals.Count > 0;
        public Task BeginTurnAsync(CancellationToken token) { EventOrder.Add("start"); return Task.CompletedTask; }
        public Task CancelTurnAsync(CancellationToken token) { CancelCount++; EventOrder.Add("cancel"); return Task.CompletedTask; }
        public Task PublishAsync(TranslationUpdate update, CancellationToken token)
        {
            if (update.Kind == TranslationUpdateKind.Typing) { TypingStates.Add(update.IsTyping); EventOrder.Add(update.IsTyping ? "typing-true" : "typing-false"); }
            else if (!update.IsSettled) Previews.Add(update.Text!);
            if (update.IsSettled) Finals.Add(update.Text!);
            return Task.CompletedTask;
        }
    }
    private sealed class ManualClock
    {
        private long ticks = DateTimeOffset.UtcNow.Ticks;
        public DateTimeOffset Now() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(int milliseconds) => Interlocked.Add(ref ticks, TimeSpan.FromMilliseconds(milliseconds).Ticks);
    }
    private sealed class ScriptedTranscriber : IStreamingTranscriber
    {
        private readonly Channel<StreamingTranscriptionEvent> events = Channel.CreateUnbounded<StreamingTranscriptionEvent>();
        private int processed;
        public int Processed => Volatile.Read(ref processed);
        public void Emit(StreamingTranscriptionEvent item) => events.Writer.TryWrite(item);
        public void Complete() => events.Writer.TryComplete();
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var item in events.Reader.ReadAllAsync(token))
            { yield return item; Interlocked.Increment(ref processed); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RecordingTranslator : ITextTranslator
    {
        public List<string> Requests = [];
        public Task<string> TranslateAsync(string source, CancellationToken token)
        { Requests.Add(source); return Task.FromResult("translated complete sentence"); }
    }
    private sealed class FixedLanguageTranslator(string translated, Func<int> processed) : ITextTranslator
    {
        public List<string> Requests { get; } = [];
        public int CompletedEvents { get; private set; }
        public Task<string> TranslateAsync(string source, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests.Add(source); CompletedEvents = processed(); return Task.FromResult(translated); }
    }
    private sealed class FailFirstTranslator : ITextTranslator
    {
        public int Count;
        public Task<string> TranslateAsync(string source, CancellationToken token) => Interlocked.Increment(ref Count) == 1
            ? Task.FromException<string>(new InvalidOperationException("synthetic speculative failure"))
            : Task.FromResult("definitive translation");
    }
    private sealed class GatedTranslator : ITextTranslator
    {
        private readonly object gate = new();
        private readonly List<(string Source, TaskCompletionSource<string> Result)> requests = [];
        private int active, cancelled;
        public int MaximumConcurrent { get; private set; }
        public int Count { get { lock (gate) return requests.Count; } }
        public int Cancelled => Volatile.Read(ref cancelled);
        public string Source(int index) { lock (gate) return requests[index].Source; }
        public void Complete(int index, string text) { lock (gate) requests[index].Result.TrySetResult(text); }
        public async Task<string> TranslateAsync(string source, CancellationToken token)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { requests.Add((source, completion)); MaximumConcurrent = Math.Max(MaximumConcurrent, ++active); }
            using var cancellation = token.Register(() => completion.TrySetCanceled(token));
            try { return await completion.Task; }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            finally { lock (gate) active--; }
        }
    }
    private sealed class ScriptedCapture : IAudioSource
    {
        private readonly Channel<AudioFrame> frames = Channel.CreateUnbounded<AudioFrame>();
        private TaskCompletionSource? deliveryGate;
        public AudioFormat Format => new(16000, 16, 1);
        public int FramesRead;
        public void Stop() => frames.Writer.TryComplete();
        public void HoldDelivery() => Volatile.Write(ref deliveryGate, new(TaskCreationOptions.RunContinuationsAsynchronously));
        public void ReleaseDelivery() => Interlocked.Exchange(ref deliveryGate, null)?.TrySetResult();
        public void PushSentence()
        {
            PushVoice(3); PushQuiet(2);
        }
        public void PushVoice(int count, int level = 5000)
        {
            byte[] voice = new byte[640];
            for (int i = 0; i < 320; i++) BinaryPrimitives.WriteInt16LittleEndian(voice.AsSpan(i * 2), (short)(Math.Sin(i * .15) * level));
            for (int i = 0; i < count; i++) frames.Writer.TryWrite(new(voice, Format));
        }
        public void PushQuiet(int count)
        { for (int i = 0; i < count; i++) frames.Writer.TryWrite(new(new byte[640], Format)); }
        public void PushQuietSamples(int samples) => frames.Writer.TryWrite(new(new byte[samples * 2], Format));
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(token))
            {
                if (Volatile.Read(ref deliveryGate) is { } gate) await gate.Task.WaitAsync(token);
                Interlocked.Increment(ref FramesRead); yield return frame;
            }
        }
    }
    private sealed class FakeCapture : IAudioSource
    {
        public AudioFormat Format => new(16000, 16, 1);
        public Action<AudioFrame>? OnFrame;
        public int Frames;
        private int stopped;
        public volatile bool Quiet;
        public int DelayMilliseconds = 20;
        public void Stop() => Interlocked.Exchange(ref stopped, 1);
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            var pcm = new byte[640];
            for (int i = 0; i < 320; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i * 0.15) * 5000));
            while (Volatile.Read(ref stopped) == 0)
            { var frame = new AudioFrame(Quiet ? new byte[640] : pcm, Format); OnFrame?.Invoke(frame); Frames++; yield return frame; await Task.Delay(DelayMilliseconds, token); }
        }
    }
    private sealed class FakeFactory(FakeSocket socket) : IVoxtralWebSocketFactory
    { public IVoxtralWebSocket Create() => socket; }
    private sealed class FakeSocket : IVoxtralWebSocket
    {
        private readonly Channel<byte[]> events = Channel.CreateUnbounded<byte[]>();
        public WebSocketState State { get; private set; }
        public int EndCount, CancelCount, SilentBytes, Binaries, PcmBytes;
        public int EndMode;
        public int EndResponseDelayMs;
        public string FirstText = "testing testing";
        public string FinalText = "testing testing is this working";
        public bool FailFirstAudio;
        private void Queue(string json) => events.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        public Task ConnectAsync(Uri endpoint, string? key, CancellationToken token)
        { State = WebSocketState.Open; return Task.CompletedTask; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, bool end, CancellationToken token)
        {
            if (type == WebSocketMessageType.Binary)
            {
                PcmBytes += bytes.Length;
                // Aggregator boundaries can put the first silence samples in
                // the tail of a non-silent binary chunk. Count that suffix too.
                int silent = 0;
                for (int i = bytes.Length - 2; i >= 0 && BinaryPrimitives.ReadInt16LittleEndian(bytes.Span[i..]) == 0; i -= 2) silent += 2;
                SilentBytes += silent;
                if (++Binaries == 1)
                {
                    Queue(JsonSerializer.Serialize(new { type = "transcript.partial", sequence = 1, text = FirstText, audio_end_ms = 80 }));
                    if (FailFirstAudio) Queue("""{"type":"error","code":"unexpected_close","message":"synthetic early failure","fatal":true}""");
                }
            }
            else
            {
                using var json = JsonDocument.Parse(bytes);
                switch (json.RootElement.GetProperty("type").GetString())
                {
                    case "session.configure": Queue("""{"type":"session.created","session_id":"test","protocol_version":1,"model":"voxtral-mini","transcription_delay_ms":240,"audio":{"format":"pcm_s16le","sample_rate":16000,"channels":1}}"""); break;
                    case "input_audio.end":
                        EndCount++;
                        void Finish()
                        {
                            // Inject the already-tested typed timeout without waiting
                            // eight seconds in this host queue-isolation fixture.
                            if (EndMode == 3)
                            {
                                Queue("""{"type":"error","code":"session_completion_timeout","message":"synthetic deadline","fatal":true}""");
                                return;
                            }
                            if (EndMode != 1) Queue(JsonSerializer.Serialize(new { type = "transcript.final", sequence = 2, text = FinalText, audio_end_ms = 4500 }));
                            if (EndMode == 2) events.Writer.TryWrite([0]);
                            else Queue("""{"type":"session.completed","sequence":3,"audio_duration_ms":4500}""");
                        }
                        if (EndResponseDelayMs == 0) Finish();
                        else _ = Task.Run(async () => { await Task.Delay(EndResponseDelayMs, token); Finish(); }, token);
                        break;
                    case "session.cancel": CancelCount++; Queue("""{"type":"session.cancelled"}"""); break;
                }
            }
            return ValueTask.CompletedTask;
        }
        public async ValueTask<VoxtralSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
        {
            byte[] bytes = await events.Reader.ReadAsync(token);
            if (bytes.Length == 1 && bytes[0] == 0)
            { State = WebSocketState.CloseReceived; return new(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "test missing acknowledgment"); }
            bytes.CopyTo(buffer); return new(bytes.Length, WebSocketMessageType.Text, true, null, null);
        }
        public Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token)
        { State = WebSocketState.Closed; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { State = WebSocketState.Closed; return ValueTask.CompletedTask; }
    }
}

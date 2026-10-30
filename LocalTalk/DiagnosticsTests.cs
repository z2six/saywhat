using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

internal static class DiagnosticsTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Diagnostics: " + message); checks++; }
        Check(Read<bool>(window, "_preview") && !window.IsVisible, "tests are inert preview only");
        string root = Path.Combine(directory, "diagnostics-fixtures"); Directory.CreateDirectory(root);
        DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        string Stamp(DateTimeOffset at, string body) => "[" + at.ToString("O") + "] " + body + Environment.NewLine;
        string incomingPath = Path.Combine(root, "incoming-translator.log");
        string privateText = "PRIVATE-SPEECH-SECRET 我的朋友名字 C:\\Users\\PrivateName\\passwords.txt token=secret-token";
        File.WriteAllText(incomingPath, Stamp(now.AddMinutes(-2), "Speech stalled: " + privateText) +
            Stamp(now.AddMinutes(-1), "Speech stalled: " + privateText) +
            Stamp(now.AddSeconds(-30), "Speech progress restored after decoder recovery.") +
            Stamp(now.AddSeconds(-20), "Translation failed: " + privateText) +
            Stamp(now.AddSeconds(-10), "Source: " + privateText + " code=asr-progress-timeout; worker failed and will restart:"));
        File.WriteAllText(Path.Combine(root, "foxtrans-local-host.log"),
            Stamp(now.AddSeconds(-20), "ERROR own-voice: code=asr-progress-timeout; turn=3; queued captured turns kept.") +
            Stamp(now.AddSeconds(-15), "OWN VOICE recovered; code=capture-ready; skipped_audio_ms=50."));
        File.WriteAllText(Path.Combine(root, "foxtrans-mute-sync.log"),
            Stamp(now.AddSeconds(-40), "ERROR own-voice: code=mute-state-unverified; " + privateText) +
            Stamp(now.AddSeconds(-36), "ERROR own-voice: code=mute-state-unverified; " + privateText) +
            Stamp(now.AddSeconds(-33), "OWN VOICE recovered; code=mute-state-ready; " + privateText) +
            Stamp(now.AddSeconds(-32), "OWN VOICE recovered; code=mute-sync-disabled; " + privateText));
        File.WriteAllText(Path.Combine(root, "saywhat.log"), Stamp(now.AddSeconds(-5),
            "Microphone mute controller: [2026-10-06T11:59:55+00:00] ERROR own-voice: code=translation-or-output-failed; turn=5; queued captured turns kept."));
        var monitor = new DiagnosticsMonitor(root);
        var snapshot = monitor.Read(now);
        Check(snapshot.Issues.Any(issue => issue.Code == "speech-no-progress" && issue.Occurrences == 2), "repeated stalls are summarized with count");
        Check(snapshot.Issues.Any(issue => issue.Code == "speech-recovered" && issue.Recovered), "recovery is retained in history");
        Check(snapshot.Issues.Count(issue => issue.Code == "asr-progress-timeout") == 1, "a spoken error-code string cannot create an issue");
        var muteWaiting = snapshot.Issues.Single(issue => issue.Code == "mute-state-unverified");
        var muteReady = snapshot.Issues.Single(issue => issue.Code == "mute-state-ready");
        var muteIgnored = snapshot.Issues.Single(issue => issue.Code == "mute-sync-disabled");
        Check(muteWaiting.Area == DiagnosticArea.OwnVoice && muteWaiting.Stage == "Audio capture" &&
            !muteWaiting.Recovered && muteWaiting.Occurrences == 2 && muteWaiting.Message ==
                "Waiting for VRChat microphone status. If this continues, mute and unmute once in VRChat.",
            "unverified mute status is repeated as a fixed, actionable capture issue");
        Check(muteReady.Area == DiagnosticArea.OwnVoice && muteReady.Stage == "Audio capture" && muteReady.Recovered,
            "verified mute status is a distinct recovery event");
        var muteOnly = snapshot with { Issues = [muteWaiting] };
        Check(muteOnly.ActiveOwnIssue(now)?.Code == "mute-state-unverified", "waiting for verified microphone status is current feedback");
        Check((muteOnly with { Issues = [muteWaiting, muteReady] }).ActiveOwnIssue(now) is null,
            "verified microphone status clears its waiting issue");
        Check(muteIgnored.Recovered && muteIgnored.Message == "VRChat microphone muting is ignored." &&
            (muteOnly with { Issues = [muteWaiting, muteIgnored] }).ActiveOwnIssue(now) is null,
            "an explicit mute-following opt-out clears the waiting issue without claiming verified status");
        var captureProblem = new DiagnosticIssue(now.AddSeconds(-38), DiagnosticArea.OwnVoice, "Audio capture",
            "capture-backpressure", "Speech processing fell behind. Queued speech is kept; some new audio was skipped.");
        Check((muteOnly with { Issues = [captureProblem, muteWaiting, muteReady] }).ActiveOwnIssue(now)?.Code == "capture-backpressure",
            "mute readiness leaves an unrelated capture problem active");
        Check((muteOnly with { Issues = [captureProblem, muteWaiting, muteIgnored] }).ActiveOwnIssue(now)?.Code == "capture-backpressure",
            "ignoring mute status leaves an unrelated capture problem active");
        var captureReady = snapshot.Issues.Single(issue => issue.Code == "capture-recovered");
        Check((muteOnly with { Issues = [muteWaiting, captureReady] }).ActiveOwnIssue(now)?.Code == "mute-state-unverified",
            "capture recovery cannot claim that microphone status was verified");
        var turnCompleted = new DiagnosticIssue(now, DiagnosticArea.OwnVoice, "Speech recognition", "own-voice-recovered",
            "Your voice translator recovered.", true);
        Check((muteOnly with { Issues = [muteWaiting, turnCompleted] }).ActiveOwnIssue(now)?.Code == "mute-state-unverified",
            "a completed speech turn cannot substitute for verified microphone status");
        Check((muteOnly with { Issues = [muteReady, muteWaiting with { At = now }] }).ActiveOwnIssue(now)?.Code == "mute-state-unverified",
            "an older ready event does not clear renewed waiting");
        Check((muteOnly with { Issues = [muteIgnored, muteWaiting with { At = now }] }).ActiveOwnIssue(now)?.Code == "mute-state-unverified",
            "turning mute following on again can surface renewed waiting after an earlier opt-out");
        Check(snapshot.Issues.Single(issue => issue.Code == "translation-or-output-failed").Area == DiagnosticArea.OwnVoice, "nested microphone messages use the right pipeline");
        Check(snapshot.ActiveOwnIssue(now)?.Code == "translation-or-output-failed", "unrelated capture recovery does not clear a translation or recognition problem");
        var recognitionOnly = snapshot with { Issues = snapshot.Issues.Where(issue => issue.Code is "asr-progress-timeout" or "capture-recovered").ToArray() };
        Check(recognitionOnly.ActiveOwnIssue(now)?.Code == "asr-progress-timeout", "capture-ready does not falsely claim recognized words");
        Check(snapshot.ActiveOwnIssue(now.AddMinutes(1)) is null, "old issues are not presented as current stalls");
        string report = DiagnosticsMonitor.BuildSafeSummary(snapshot, true, true, "stalled", 3, 12);
        Check(!report.Contains("PRIVATE", StringComparison.Ordinal) && !report.Contains("secret-token", StringComparison.Ordinal) && !report.Contains("朋友", StringComparison.Ordinal), "safe copy omits exception, conversation and names");
        Check(report.Contains("asr-progress-timeout") && report.Contains("recoveries: 3") && report.Contains("dropped audio chunks: 12"), "safe copy keeps useful metadata");
        Check(report.Contains("mute-state-unverified") && report.Contains("mute-state-ready") && report.Contains("mute-sync-disabled"),
            "safe copy includes only fixed mute-status event codes");
        var hostile = snapshot with { Issues = [new(now, DiagnosticArea.App, privateText, privateText, privateText)] };
        Check(!DiagnosticsMonitor.BuildSafeSummary(hostile, false, false, privateText).Contains("PRIVATE"), "unknown injected stage or code is not exported");
        Check(snapshot.Issues.All(issue => !issue.Message.Contains("PRIVATE", StringComparison.Ordinal)), "visible summaries contain only fixed messages");
        Check(!DiagnosticsMonitor.TryParse("Source: un-timestamped " + privateText, "incoming-translator.log", out _), "events without timestamps cannot become active warnings");
        Check(!DiagnosticsMonitor.TryParse(Stamp(now, "LLM " + privateText + " code=capture-backpressure;"), "foxtrans-local-host.log", out _), "unanchored error markers are not classified");
        Check(!DiagnosticsMonitor.TryParse(Stamp(now, "LLM ERROR own-voice: code=mute-state-unverified; " + privateText), "foxtrans-mute-sync.log", out _),
            "unanchored mute-status errors cannot create a warning");
        Check(!DiagnosticsMonitor.TryParse(Stamp(now, "LLM OWN VOICE recovered; code=mute-state-ready; " + privateText), "foxtrans-mute-sync.log", out _),
            "unanchored mute-status recovery cannot clear a warning");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "Source: ERROR own-voice: code=mute-state-unverified; OWN VOICE recovered; code=mute-state-ready; " + privateText),
            "incoming-translator.log", out var spokenMuteCodes) && spokenMuteCodes?.Code == "subtitle-published",
            "conversation text containing mute-status codes is never searched for issues or recovery");
        Check(!DiagnosticsMonitor.TryParse(Stamp(now, "VRChat closed. Capture ended normally."), "incoming-translator.log", out _), "normal application exit does not report a capture fault");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "ERROR OutputError; turn=9; stage=Output."), "foxtrans-local-host.log", out var output) && output?.Stage == "VRChat chat", "translation to chat-output failures have a distinct stage");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "expired-captured-finals: 1 queued muted final(s) expired."), "foxtrans-osc-sentence.log", out var expired) && expired?.Code == "chat-message-dropped", "finished messages expiring before send are surfaced");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "fatal: " + privateText), "foxtrans-osc-sentence.log", out var fatal) && fatal?.Code == "chat-connection-stopped" && !fatal.Message.Contains("PRIVATE"), "proxy fatal errors are actionable but text-free");
        Check(!DiagnosticsMonitor.TryParse(Stamp(now, "discarded-muted-final: expected-configured-discard"), "foxtrans-osc-sentence.log", out _), "intentional mute discard is not mislabeled as a broken pipeline");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: System.InvalidOperationException: " + privateText), "foxtrans-local-host.log", out var hostFatal) &&
            hostFatal?.Code == "microphone-host-stopped" && hostFatal.Evidence?.Component == "microphone-host",
            "a host fatal is no longer mislabeled as a controller failure");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: " + privateText), "foxtrans-mute-sync.log", out var controllerFatal) &&
            controllerFatal?.Code == "microphone-controller-stopped" && controllerFatal.Evidence?.Component == "microphone-controller",
            "a controller-owned fatal keeps its exact component without guessing a cause");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "Microphone mute controller: [2026-10-06T12:00:00Z] FATAL: System.InvalidOperationException: " + privateText),
            "saywhat.log", out var inheritedFatal) && inheritedFatal?.Code == "microphone-process-stopped" && inheritedFatal.Evidence?.Component == "unknown",
            "legacy inherited stdout does not falsely identify the failed child as its controller");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: System.DllNotFoundException: " + privateText), "foxtrans-local-host.log", out var missingLibrary) &&
            missingLibrary?.Code == "native-library-missing" && missingLibrary.Evidence?.ExceptionType == "DllNotFoundException" &&
            !missingLibrary.Message.Contains("PRIVATE", StringComparison.Ordinal), "anchored native-loader failures have fixed, actionable evidence");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: AudioDeviceSelectionException: No microphone input devices are available. " + privateText),
            "foxtrans-local-host.log", out var noMicrophone) && noMicrophone?.Code == "microphone-unavailable",
            "typed audio-device failure is distinguished without retaining device names");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: System.UnauthorizedAccessException: " + privateText), "foxtrans-local-host.log", out var accessDenied) &&
            accessDenied?.Code == "access-denied" && accessDenied.Code != "microphone-access-denied", "generic access denial does not invent a microphone-permission diagnosis");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: System.Runtime.InteropServices.COMException (0x80070005): " + privateText), "foxtrans-local-host.log", out var comDenied) &&
            comDenied?.Evidence?.HResult == "0x80070005" && comDenied.Code == "access-denied", "known HRESULT evidence survives without arbitrary COM text");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: System.IO.IOException (0x80070020): " + privateText), "foxtrans-local-host.log", out var lockedFile) &&
            lockedFile?.Code == "file-in-use", "typed file-sharing HRESULT has its own diagnosis");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: System.Net.Sockets.SocketException (10048): " + privateText), "foxtrans-local-host.log", out var occupiedPort) &&
            occupiedPort?.Code == "port-in-use" && occupiedPort.Evidence?.NativeCode == 10048, "a known socket port conflict keeps only its numeric evidence");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: System.Net.Http.HttpRequestException: " + privateText), "foxtrans-local-host.log", out var unavailableProvider) &&
            unavailableProvider?.Code == "provider-connection-failed", "typed HTTP failure is a local-service failure without exporting its URL");
        Check(DiagnosticsMonitor.TryParse(Stamp(now, "FATAL: " + privateText + " System.DllNotFoundException (0x8007007E):"), "foxtrans-local-host.log", out var fakeTypedFatal) &&
            fakeTypedFatal?.Evidence?.Category == "unknown" && fakeTypedFatal.Evidence.ExceptionType is null,
            "exception-like words in an arbitrary message never become typed failure evidence");
        Check(DiagnosticsMonitor.SafeFatalDiagnosticLine(Stamp(now, "FATAL: System.IO.IOException (0x80070020): " + privateText)) ==
            "FATAL: IOException (0x80070020):", "supervised fatal normalization excludes the original body and path");
        Check(DiagnosticsMonitor.SafeFatalDiagnosticLine(Stamp(now, "Source: FATAL: System.DllNotFoundException: " + privateText)) is null,
            "speech containing a fatal-looking sentence is never normalized as process evidence");
        string Status(string body) => Stamp(now, body).TrimEnd('\r', '\n');
        const string hostStarted = "SAYWHAT_STATUS component=microphone-controller event=host-started restart=1";
        const string hostControlReady = "SAYWHAT_STATUS component=microphone-host event=control-ready";
        const string captureReadyStatus = "SAYWHAT_STATUS component=microphone-host event=capture-ready";
        Check(!DiagnosticsMonitor.TryParse(Status(hostStarted), "foxtrans-mute-sync.log", out _) &&
            !DiagnosticsMonitor.TryParse(Status(hostControlReady), "foxtrans-local-host.log", out _),
            "normal process/control readiness creates report metadata rather than a false Recent issue");
        Check(DiagnosticsMonitor.TryParse(Status(captureReadyStatus), "foxtrans-local-host.log", out var actualCaptureReady) &&
            actualCaptureReady?.Code == "capture-recovered" && actualCaptureReady.Recovered, "actual PCM capture readiness has its own scoped recovery");
        Check(DiagnosticsMonitor.TryParse(Status("SAYWHAT_STATUS component=microphone-host event=failed code=microphone-access-denied hresult=0x80070005"),
            "foxtrans-local-host.log", out var typedAccess) && typedAccess?.Code == "microphone-access-denied" && typedAccess.Evidence?.HResult == "0x80070005",
            "a producer's explicit microphone-permission code can be distinguished from generic access denial");
        Check(DiagnosticsMonitor.TryParse(Status("SAYWHAT_STATUS component=microphone-controller event=failed code=controller-failed hresult=0x80070020"),
            "foxtrans-mute-sync.log", out var typedLockedFile) && typedLockedFile?.Code == "file-in-use",
            "a generic typed failure with a known sharing HRESULT is refined without retaining its exception message");
        Check(DiagnosticsMonitor.TryParse(Status("SAYWHAT_STATUS component=microphone-host event=failed code=microphone-access-denied native=-6"),
            "foxtrans-local-host.log", out var nativeAccess) && nativeAccess?.Code == "microphone-access-denied" && nativeAccess.Evidence?.NativeCode == -6,
            "the explicit miniaudio access-denied code remains distinct and exportable");
        Check(DiagnosticsMonitor.TryParse(Status("SAYWHAT_STATUS component=microphone-host event=failed code=microphone-unavailable native=-204"),
            "foxtrans-local-host.log", out var nativeNoDevice) && nativeNoDevice?.Evidence?.NativeCode == -204,
            "the explicit miniaudio no-device code remains exportable without device names");
        Check(DiagnosticsMonitor.TryParse(Status("SAYWHAT_STATUS component=microphone-controller event=host-exited restart=4 exit=-532462766"),
            "foxtrans-mute-sync.log", out var childExit) && childExit?.Code == "microphone-host-stopped", "a supervised child exit is not mistaken for a controller exit");
        File.AppendAllText(Path.Combine(root, "foxtrans-mute-sync.log"), Stamp(now, hostStarted));
        File.AppendAllText(Path.Combine(root, "foxtrans-local-host.log"), Stamp(now, hostControlReady) +
            Stamp(now, "SAYWHAT_STATUS component=microphone-host event=capture-levels frames=500 voiced=100 peak=42000") +
            Stamp(now, "SAYWHAT_STATUS component=microphone-host event=recognition-final chars=27") +
            Stamp(now, "SAYWHAT_STATUS component=microphone-host event=translation-completed duration_ms=640") +
            Stamp(now, "SAYWHAT_STATUS component=microphone-host event=chat-queued chars=48"));
        var runtimeSnapshot = monitor.Read(now);
        Check(runtimeSnapshot.RuntimeEvents.Count == 6 && runtimeSnapshot.RuntimeEvents.Any(item => item.Event == "chat-queued" && item.Chars == 48),
            "the latest numeric lifecycle/progress events survive without transcript text");
        Check(!runtimeSnapshot.Issues.Any(item => item.Code is "host-started" or "control-ready" or "capture-levels"),
            "ordinary numeric capture activity is not presented as an error");
        AppSettings reportSettings = new()
        {
            OwnRecognitionEngine = "nemotron", OwnVoiceSourceLanguage = "Chinese", OwnVoiceTargetLanguage = "English",
            OwnVoiceAdditionalLanguagesEnabled = true, OwnVoiceAdditionalTargetLanguages = ["Japanese", "Korean"],
            OwnManagedModelId = "hy-mt2-7b-q4_k_m", UseManagedTranslator = true, OwnVoicePauseWhenVrchatMuted = false,
            OwnMicrophoneDeviceId = privateText, IncomingApplicationPath = privateText, IncomingPersonId = privateText,
            OwnTranslatorModel = privateText, OwnTranslatorBaseUrl = "https://private.invalid/" + privateText,
            IncomingAudioMode = "application"
        };
        var choices = DiagnosticSupportContext.Create(reportSettings) with { WindowsVersion = "10.0.19045.0", OsArchitecture = "X64", ProcessArchitecture = "X64" };
        string detailedReport = DiagnosticsMonitor.BuildSafeSummary(runtimeSnapshot with { Issues = [missingLibrary!, lockedFile!, occupiedPort!] },
            true, false, context: choices);
        Check(detailedReport.Contains("10.0.19045.0") && detailedReport.Contains("source=Chinese") && detailedReport.Contains("targets=English,Japanese,Korean") &&
            detailedReport.Contains("hy-mt2-7b-q4_k_m") && detailedReport.Contains("follow_vrchat_mute=False") && detailedReport.Contains("microphone=selected"),
            "one report includes safe Windows/architecture and actual built-in voice choices");
        Check(detailedReport.Contains("exception=DllNotFoundException") && detailedReport.Contains("hresult=0x80070020") && detailedReport.Contains("native=10048") &&
            detailedReport.Contains("frames=500") && detailedReport.Contains("voiced=100") && detailedReport.Contains("chars=48") && detailedReport.Contains("duration_ms=640"),
            "one report includes the typed fault and last working pipeline-stage counters");
        Check(!detailedReport.Contains("PRIVATE", StringComparison.Ordinal) && !detailedReport.Contains("private.invalid", StringComparison.Ordinal) &&
            !detailedReport.Contains("朋友", StringComparison.Ordinal), "selected device IDs, endpoints, people and raw exceptions are absent from support context");
        var separateOwn = reportSettings.Copy(); separateOwn.UseManagedTranslator = false;
        var separateIncoming = reportSettings.Copy(); separateIncoming.UseManagedTranslator = true;
        separateIncoming.ShareTranslationModel = false; separateIncoming.IncomingManagedModelId = "hy-mt2-1.8b-q4_k_m";
        separateIncoming.IncomingSourceLanguage = "Japanese"; separateIncoming.IncomingTargetLanguage = "Korean";
        string separateReport = DiagnosticsMonitor.BuildSafeSummary(DiagnosticsSnapshot.Empty, false, false,
            context: DiagnosticSupportContext.Create(separateOwn, separateIncoming));
        Check(separateReport.Split('\n').Single(line => line.StartsWith("Own choices:", StringComparison.Ordinal)).Contains("translator=external") &&
            separateReport.Split('\n').Single(line => line.StartsWith("Subtitle choices:", StringComparison.Ordinal)).Contains("source=Japanese; target=Korean; translator=hy-mt2-1.8b-q4_k_m"),
            "independently applied own/subtitle snapshots preserve their actual directions and translation modes");
        var hostileChoices = choices with
        {
            WindowsVersion = privateText, OsArchitecture = privateText, ProcessArchitecture = privateText,
            OwnRecognizer = privateText, OwnSource = privateText, OwnTargets = [privateText], OwnTranslator = privateText,
            IncomingRecognizer = privateText, IncomingSource = privateText, IncomingTarget = privateText, IncomingTranslator = privateText,
            Runtime = privateText, IncomingRuntime = privateText, Microphone = privateText, IncomingAudioMode = privateText, SharedTranslator = false
        };
        var hostileEvidence = new DiagnosticsSnapshot([missingLibrary! with { Evidence = new(privateText, privateText, privateText, privateText, int.MaxValue) }], now)
        { RuntimeEvents = [new(now, privateText, privateText, Code: privateText), new(now, "microphone-host", "capture-levels", Frames: 1, Voiced: 2, Peak: 1)] };
        Check(!DiagnosticsMonitor.BuildSafeSummary(hostileEvidence, false, false, context: hostileChoices).Contains("PRIVATE", StringComparison.Ordinal),
            "hostile context, evidence and impossible numeric events cannot inject report text");
        File.AppendAllText(Path.Combine(root, "foxtrans-local-host.log"), Stamp(now, "OWN VOICE recovered; code=turn-completed; turn=3."));
        var recovered = monitor.Read(now);
        Check(recovered.ActiveOwnIssue(now) is null, "a fully completed valid turn clears prior own-pipeline warnings");
        Check(recovered.Issues.Any(issue => issue.Code == "asr-progress-timeout"), "recovery does not erase diagnostic history");
        var repeatedRead = monitor.Read(now);
        Check(repeatedRead.Issues.SequenceEqual(recovered.Issues), "unchanged log files use cached event results without duplicate counting");
        var simultaneous = Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => monitor.Read(now)))).GetAwaiter().GetResult();
        Check(simultaneous.All(value => value.Issues.SequenceEqual(recovered.Issues)),
            "support export and background refresh can read one monitor concurrently without cache races");
        File.WriteAllText(Path.Combine(root, "localtalk.log"), Stamp(now.AddDays(-8), "Translation failed: old issue"));
        Check(monitor.Read(now).Issues.All(issue => issue.At >= now.AddDays(-7)), "history excludes stale events outside seven days");
        File.WriteAllText(incomingPath, new string('x', DiagnosticsMonitor.TailLimit * 4) + "\n" + Stamp(now, "Speech stalled: " + privateText));
        Check(monitor.Read(now).Issues.Count(issue => issue.Area == DiagnosticArea.Subtitles) == 1, "large log reads are capped and skip partial leading lines");
        File.WriteAllText(incomingPath, Stamp(now, "Source: clean synthetic words"));
        Check(!monitor.Read(now).Issues.Any(issue => issue.Code == "speech-no-progress"), "a truncated or replaced log invalidates cache");

        string language = UiText.Language; var settings = Read<AppSettings>(window, "_settings"); var original = settings.Copy();
        DiagnosticsSnapshot oldSnapshot = Read<DiagnosticsSnapshot>(window, "_diagnosticsSnapshot");
        string savedSettings = File.ReadAllText(AppPaths.SettingsPath);
        try
        {
            Write(window, "_diagnosticsSnapshot", snapshot);
            string[] required = ["Recent issues", "Copy diagnostics", "View detailed logs", "Problems & logs", "Open troubleshooting", "See recent issues", "No recent issues recorded.",
                "Speech recognition", "Audio capture", "Recovered", "Recorded issue", "App", "Diagnostics copied. No conversation text is included.",
                "Couldn't copy diagnostics. Try again.", "{0} · {1} · recorded {2} times", "My voice: {0}"];
            Check(required.All(UiText.Contains) && snapshot.Issues.All(issue => UiText.Contains(issue.Message)), "all recent issue UI and fixed messages are translated");
            Check(new[] { hostFatal, inheritedFatal, missingLibrary, noMicrophone, accessDenied, comDenied, lockedFile, occupiedPort, unavailableProvider, typedAccess }
                .All(issue => issue is not null && UiText.Contains(issue.Message)), "new source-specific and typed failure summaries are translated");
            foreach (string locale in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = locale;
                foreach (var theme in AppearanceTheme.Presets)
                {
                    var appearance = original.Copy(); appearance.UiThemeId = theme.Id;
                    AppearanceTheme.Apply(appearance);
                    var card = (FrameworkElement)Invoke(window, "CreateRecentIssuesCard")!;
                    card.Measure(new Size(570, 1200)); card.Arrange(new Rect(0, 0, 570, card.DesiredSize.Height)); card.UpdateLayout();
                    Check(ReferenceEquals(card.GetValue(Control.ForegroundProperty), DependencyProperty.UnsetValue) ||
                        Nodes(card).OfType<TextBlock>().All(text => ReferenceEquals(text.Foreground, ModernTheme.Text) || ReferenceEquals(text.Foreground, ModernTheme.Muted) || ReferenceEquals(text.Foreground, ModernTheme.Warning) || ReferenceEquals(text.Foreground, ModernTheme.Good)), "recent issues use theme text roles " + locale + "/" + theme.Id);
                    Check(Nodes(card).OfType<TextBlock>().All(text => text.TextWrapping == TextWrapping.Wrap), "localized summaries wrap without horizontal scrolling");
                    var actions = Nodes(card).OfType<Button>().ToArray();
                    Check(actions.Length == 4 && actions.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))), "all issue actions have accessible labels");
                    Check(actions.Single(button => button.Name == "CopySafeDiagnosticsButton").Content?.ToString() == UiText.T("Copy diagnostics"), "copy action is localized immediately");
                    var banner = (Border)Invoke(window, "CreatePipelineIssueBanner")!;
                    Check(banner.Visibility == Visibility.Collapsed, "historical failures cannot warn when engines are stopped");
                    var settingsCard = (FrameworkElement)Invoke(window, "CreateDiagnosticsSettingsCard")!;
                    Check(Nodes(settingsCard).OfType<Button>().Single().Name == "SettingsDiagnosticsButton", "Settings has a discoverable diagnostic shortcut");
                    if (locale == "en" && theme.Id == "coral-night" || locale == "ja" && theme.Id == "paper")
                    {
                        var image = new RenderTargetBitmap(570, Math.Min(1200, (int)Math.Ceiling(card.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                        image.Render(card); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        using var stream = File.Create(Path.Combine(root, "recent-issues-" + theme.Id + "-" + locale + ".png")); encoder.Save(stream);
                    }
                }
            }
            UiText.Language = "en"; AppearanceTheme.Apply(original);
            Invoke(window, "CopySafeDiagnostics");
            Check(Read<TextBlock>(window, "_status").Text != UiText.T("Diagnostics copied. No conversation text is included."), "preview copy never touches the real clipboard");
            Invoke(window, "RefreshDiagnosticsFeedbackAsync");
            Check(Read<DiagnosticsSnapshot>(window, "_diagnosticsSnapshot") == snapshot, "preview refresh never reads live app logs");
            Check(File.ReadAllText(AppPaths.SettingsPath) == savedSettings, "reading issues changes no saved settings");
        }
        finally
        {
            Write(window, "_diagnosticsSnapshot", oldSnapshot); UiText.Language = language;
            AppearanceTheme.Apply(original); ThemeBackground.Apply(original);
        }
        return checks;
    }

    private static T Read<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Write(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Nodes(VisualTreeHelper.GetChild(root, i))) yield return child; }
}

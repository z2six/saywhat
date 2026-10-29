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
        File.AppendAllText(Path.Combine(root, "foxtrans-local-host.log"), Stamp(now, "OWN VOICE recovered; code=turn-completed; turn=3."));
        var recovered = monitor.Read(now);
        Check(recovered.ActiveOwnIssue(now) is null, "a fully completed valid turn clears prior own-pipeline warnings");
        Check(recovered.Issues.Any(issue => issue.Code == "asr-progress-timeout"), "recovery does not erase diagnostic history");
        var repeatedRead = monitor.Read(now);
        Check(repeatedRead.Issues.SequenceEqual(recovered.Issues), "unchanged log files use cached event results without duplicate counting");
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
                    Check(actions.Length == 3 && actions.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))), "all issue actions have accessible labels");
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

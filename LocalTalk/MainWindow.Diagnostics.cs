using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace IncomingTranslator;

internal sealed partial class MainWindow
{
    private readonly DiagnosticsMonitor _diagnosticsMonitor = new(AppPaths.Root);
    private DiagnosticsSnapshot _diagnosticsSnapshot = DiagnosticsSnapshot.Empty;
    private StackPanel? _recentIssuesList;
    private Border? _pipelineIssueBanner;
    private TextBlock? _pipelineIssueText;
    private bool _refreshingDiagnostics;
    private string? _renderedIssueSignature;
    private string? _lastAutomaticSupportIssue;
    private DateTimeOffset _lastAutomaticSupportAt;
    private bool _savingSupportReport;

    private FrameworkElement CreateRecentIssuesCard()
    {
        var panel = Section("Recent issues", "See where translation stopped and whether it recovered. This list contains no conversation text.");
        panel.Name = "RecentIssuesCard";
        _recentIssuesList = new StackPanel { Name = "RecentIssuesList" };
        _renderedIssueSignature = null;
        AutomationProperties.SetLiveSetting(_recentIssuesList, AutomationLiveSetting.Polite);
        panel.Children.Add(_recentIssuesList);
        var actions = new WrapPanel();
        var refresh = ModernTheme.Button("Refresh", async (_, _) => await RefreshDiagnosticsFeedbackAsync());
        refresh.Name = "RefreshDiagnosticsButton";
        actions.Children.Add(refresh);
        var copy = ModernTheme.Button("Copy diagnostics", (_, _) => CopySafeDiagnostics());
        copy.Name = "CopySafeDiagnosticsButton"; actions.Children.Add(copy);
        var detail = ModernTheme.Button("View detailed logs", (_, _) => { _logView?.BringIntoView(); _logView?.Focus(); });
        detail.Name = "ViewDetailedLogsButton"; actions.Children.Add(detail);
        var support = ModernTheme.Button("Save support report…", async (_, _) => await SaveSupportReportAsync());
        support.Name = "SaveSupportReportButton"; actions.Children.Add(support);
        panel.Children.Add(actions);
        panel.Children.Add(ModernTheme.Label("Copied diagnostics exclude speech, names and file paths. Detailed logs may contain conversation text; review them before sharing.", 12, ModernTheme.Muted));
        RenderRecentIssues();
        return ModernTheme.Card(panel);
    }

    private FrameworkElement CreateDiagnosticsSettingsCard()
    {
        var panel = Section("Problems & logs", "See recent problems and copy a summary for support.");
        var button = ModernTheme.Button("Open troubleshooting", (_, _) => Navigate("Diagnostics"));
        button.Name = "SettingsDiagnosticsButton"; panel.Children.Add(button);
        return ModernTheme.Card(panel);
    }

    private FrameworkElement CreatePipelineIssueBanner()
    {
        _pipelineIssueText = ModernTheme.Label("", 13, ModernTheme.Warning);
        _pipelineIssueText.Name = "PipelineIssueText";
        AutomationProperties.SetLiveSetting(_pipelineIssueText, AutomationLiveSetting.Polite);
        var panel = new StackPanel(); panel.Children.Add(_pipelineIssueText);
        var button = ModernTheme.Button("See recent issues", (_, _) => Navigate("Diagnostics"));
        button.Name = "PipelineIssueDetailsButton"; panel.Children.Add(button);
        _pipelineIssueBanner = ModernTheme.Card(panel);
        _pipelineIssueBanner.Name = "PipelineIssueBanner";
        _pipelineIssueBanner.BorderBrush = ModernTheme.Warning;
        _pipelineIssueBanner.Visibility = Visibility.Collapsed;
        RenderPipelineIssue();
        return _pipelineIssueBanner;
    }

    private async Task RefreshDiagnosticsFeedbackAsync()
    {
        if (_preview || _closing || _refreshingDiagnostics) return;
        _refreshingDiagnostics = true;
        try
        {
            // No network/engine probes: local reads are bounded and stay off the UI thread.
            if (_page is "Diagnostics" or "Home" || _runtime.OwnRunning || _runtime.IncomingRunning)
                _diagnosticsSnapshot = await Task.Run(() => _diagnosticsMonitor.Read(DateTimeOffset.UtcNow));
            if (_closing) return;
            await SaveAutomaticSupportReportAsync();
            if (_page == "Diagnostics") RenderRecentIssues();
            RenderPipelineIssue();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Never allow diagnostic feedback to interrupt the translation pipeline.
            DesktopLog.Write("Recent issue summary unavailable: " + error.GetType().Name);
        }
        finally { _refreshingDiagnostics = false; }
    }

    private void RenderRecentIssues()
    {
        if (_recentIssuesList is null) return;
        var issues = _diagnosticsSnapshot.Issues.Where(issue => issue.Code != "subtitle-published").Take(8).ToArray();
        string signature = UiText.Language + "|" + string.Join("|", issues.Select(issue => $"{issue.At:O}/{issue.Area}/{issue.Code}/{issue.Occurrences}"));
        if (signature == _renderedIssueSignature) return;
        _renderedIssueSignature = signature;
        _recentIssuesList.Children.Clear();
        if (issues.Length == 0)
        {
            _recentIssuesList.Children.Add(ModernTheme.Label("No recent issues recorded.", 13, ModernTheme.Muted)); return;
        }
        foreach (DiagnosticIssue issue in issues)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            string area = issue.Area switch { DiagnosticArea.OwnVoice => "My voice", DiagnosticArea.Subtitles => "Live subtitles", _ => "App" };
            panel.Children.Add(ModernTheme.Label(UiText.Format("{0} · {1} · {2}", issue.At.LocalDateTime.ToString("g"), UiText.T(area), UiText.T(issue.Stage)), 12, ModernTheme.Muted, localize: false));
            panel.Children.Add(ModernTheme.Label(issue.Message, 14, issue.Recovered ? ModernTheme.Good : ModernTheme.Warning));
            string outcome = issue.Recovered ? "Recovered" : "Recorded issue";
            panel.Children.Add(ModernTheme.Label(UiText.Format("{0} · {1} · recorded {2} times", UiText.T(outcome), issue.Code, issue.Occurrences), 11, ModernTheme.Muted, localize: false));
            _recentIssuesList.Children.Add(panel);
        }
    }

    private void RenderPipelineIssue()
    {
        if (_badges.TryGetValue("own", out var badge) && _runtime.OwnRunning)
        {
            bool needsAttention = _diagnosticsSnapshot.ActiveOwnIssue(DateTimeOffset.UtcNow) is not null;
            UiLocalization.SetText(badge, needsAttention ? "Needs attention" : "● Translating");
            badge.Foreground = needsAttention ? ModernTheme.Warning : ModernTheme.Good;
        }
        if (_pipelineIssueBanner is null || _pipelineIssueText is null) return;
        string? message = null;
        var incoming = _runtime.IncomingHealth;
        // A process merely existing isn't proof of progress. Use the active recognizer's state,
        // not a previous subtitle or historical warning in a log.
        if (_runtime.IncomingRunning && incoming is { IsWarning: true })
            message = incoming.Stage switch
            {
                "stalled" or "recovering" => "Live subtitles: audio is arriving, but no new words are being recognized. Recovering…",
                "reconnecting" => "Live subtitles: reconnecting speech recognition…",
                "backlog" => "Live subtitles are falling behind. Older work may be skipped to catch up.",
                "translation-error" => "Live subtitles: words were recognized, but their translation failed. See recent issues for details.",
                _ => "Live subtitles need attention. See recent issues for details."
            };
        if (_runtime.OwnRunning && _diagnosticsSnapshot.ActiveOwnIssue(DateTimeOffset.UtcNow) is { } own)
            message = message is null ? UiText.Format("My voice: {0}", UiText.T(own.Message))
                : UiText.T(message) + "\n" + UiText.Format("My voice: {0}", UiText.T(own.Message));
        if (message is null) { _pipelineIssueBanner.Visibility = Visibility.Collapsed; return; }
        UiLocalization.SetText(_pipelineIssueText, message);
        _pipelineIssueBanner.Visibility = Visibility.Visible;
    }

    private string SafeDiagnosticsText()
    {
        var incoming = _runtime.IncomingHealth;
        var applied = _runtime.GetSupportContext(_settings);
        return DiagnosticsMonitor.BuildSafeSummary(_diagnosticsSnapshot, _runtime.OwnRunning, _runtime.IncomingRunning,
            incoming?.Stage, incoming?.RecoveryCount ?? 0, incoming?.DroppedAudioChunks ?? 0,
            DiagnosticSupportContext.Create(applied.Own, applied.Incoming));
    }

    private async Task SaveSupportReportAsync()
    {
        if (_preview || _savingSupportReport || _closing) return;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text report (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true,
            FileName = "SayWhat-support-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".txt"
        };
        if (picker.ShowDialog(this) != true) return;
        _savingSupportReport = true;
        UiLocalization.SetText(_status, "Preparing support report");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            CancellationToken token = timeout.Token;
            _diagnosticsSnapshot = await Task.Run(() => _diagnosticsMonitor.Read(DateTimeOffset.UtcNow), token);
            var checks = await Task.Run(() => SupportReportChecks.InspectFiles(AppContext.BaseDirectory, AppPaths.Root), token);
            var services = await SupportReportChecks.ProbeAsync(_runtime.GetSupportServiceTargets(), token);
            string report = SupportReportChecks.Append(SafeDiagnosticsText(), checks.Concat(services), _oscConnection.State);
            await File.WriteAllTextAsync(picker.FileName, report, token);
            UiLocalization.SetText(_status, "Support report saved. No speech, names or file paths are included.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException)
        { UiLocalization.SetText(_status, "Couldn't save the support report. Try a different folder."); }
        finally { _savingSupportReport = false; }
    }

    private async Task SaveAutomaticSupportReportAsync()
    {
        // Keep one local snapshot for a genuine recent failure. It is never
        // uploaded, and never starts a model, probe, chat test or audio device.
        if (_preview || _closing) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var issue = _diagnosticsSnapshot.Issues.FirstOrDefault(item => !item.Recovered &&
            item.Area == DiagnosticArea.OwnVoice && now >= item.At && now - item.At < TimeSpan.FromMinutes(2));
        if (issue is null) return;
        string signature = issue.Code + ":" + issue.At.UtcTicks;
        if (signature == _lastAutomaticSupportIssue || now - _lastAutomaticSupportAt < TimeSpan.FromSeconds(15)) return;
        string summary = SafeDiagnosticsText(); var osc = _oscConnection.State;
        try
        {
            await Task.Run(() =>
            {
                string report = SupportReportChecks.Append(summary, SupportReportChecks.InspectFiles(AppContext.BaseDirectory, AppPaths.Root), osc);
                string path = Path.Combine(AppPaths.Root, SupportReportChecks.LastFailureFile);
                string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try { File.WriteAllText(temporary, report); File.Move(temporary, path, overwrite: true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            _lastAutomaticSupportIssue = signature; _lastAutomaticSupportAt = now;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { /* Report storage is best effort and never interrupts translation. */ }
    }

    private void CopySafeDiagnostics()
    {
        if (_preview) return; // Inert preview/tests must never modify the user's clipboard.
        try { Clipboard.SetText(SafeDiagnosticsText()); UiLocalization.SetText(_status, "Diagnostics copied. No conversation text is included."); }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or System.Threading.ThreadStateException)
        { UiLocalization.SetText(_status, "Couldn't copy diagnostics. Try again."); }
    }
}

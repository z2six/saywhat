using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

// Presentation checks only. The supplied window must be an unshown preview. No button
// is clicked, setup/backend is invoked, file is written, or audio/model is opened.
internal static class SetupProgressUiTests
{
    internal static int Run(MainWindow window)
    {
        int checks = 0;
        void Check(bool passed, string detail)
        {
            if (!passed) throw new InvalidOperationException("Setup progress UI check: " + detail);
            checks++;
        }

        Check(Read<bool>(window, "_preview"), "an inert preview is required");
        Check(window.Dispatcher.CheckAccess() && !window.IsVisible && !window.IsLoaded,
            "checks run on the owner STA without showing or loading the window");
        RuntimeController runtime = Read<RuntimeController>(window, "_runtime");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "neither translation direction is active");

        var phase = new ProvisionProgress("Speech tools", "Installing speech recognition")
        {
            StepIndex = 2, StepCount = 5,
            Detail = "Preparing the speech tools. This step may take a while."
        };
        SetupProgressText unknown = SetupProgressPresentation.Format(phase, TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(4));
        Check(unknown.Title == "Step 2 of 5 · Installing speech recognition", "setup phase is explicitly numbered");
        Check(unknown.Detail == phase.Detail && unknown.Fraction is null,
            "step two of five is not misrepresented as forty-percent complete");
        Check(!unknown.Detail.Contains('%') && !Regex.IsMatch(unknown.Title + unknown.Detail + unknown.Timing, @"(?i)\bETA\b|\bremaining\b"),
            "unknown work has no invented percentage or estimated completion time");
        Check(unknown.Timing.Contains("Elapsed 1m 10s", StringComparison.Ordinal) &&
            unknown.Timing.Contains("Last update 4s ago", StringComparison.Ordinal), "elapsed time and recent activity are separate");

        var download = new ProvisionProgress("Downloading", "Downloading speech recognition", 250_000_000, 1_000_000_000)
        { StepIndex = 2, StepCount = 5, Detail = "Downloading the required local speech file." };
        SetupProgressText measured = SetupProgressPresentation.Format(download, TimeSpan.FromSeconds(20), TimeSpan.Zero);
        Check(measured.Fraction == .25 && measured.Title.StartsWith("Step 2 of 5", StringComparison.Ordinal),
            "current-download fraction is independent of the setup phase");
        Check(measured.Detail.Contains((250d).ToString("0.0", CultureInfo.CurrentCulture) + " MB", StringComparison.Ordinal) &&
            measured.Detail.Contains((1d).ToString("0.00", CultureInfo.CurrentCulture) + " GB", StringComparison.Ordinal),
            "measured byte counters show received and total size");
        Check(measured.Detail.Contains("Current task:", StringComparison.Ordinal) && measured.Detail.Contains("25", StringComparison.Ordinal) && measured.Detail.Contains('%'),
            "a real percentage is labeled as current-task progress, not overall progress");

        SetupProgressText work = SetupProgressPresentation.Format(phase with { WorkFraction = .6 }, TimeSpan.Zero, TimeSpan.Zero);
        Check(work.Fraction == .6 && work.Detail.Contains("60", StringComparison.Ordinal) && work.Detail.Contains('%'),
            "a measured work fraction is displayed without requiring byte counters");
        Check(SetupProgressPresentation.Format(download with { BytesReceived = -1 }, TimeSpan.Zero, TimeSpan.Zero).Fraction == 0 &&
            SetupProgressPresentation.Format(download with { BytesReceived = 2_000_000_000 }, TimeSpan.Zero, TimeSpan.Zero).Fraction == 1,
            "byte-based task progress is clamped to its valid bounds");
        Check(SetupProgressPresentation.Format(phase with { WorkFraction = double.NaN }, TimeSpan.Zero, TimeSpan.Zero).Fraction is null &&
            SetupProgressPresentation.Format(phase with { WorkFraction = double.PositiveInfinity }, TimeSpan.Zero, TimeSpan.Zero).Fraction is null,
            "invalid fractions cannot animate a fake percentage");
        Check(!SetupProgressPresentation.Format(phase with { StepIndex = 6 }, TimeSpan.Zero, TimeSpan.Zero).Title.StartsWith("Step ", StringComparison.Ordinal) &&
            !SetupProgressPresentation.Format(phase with { StepCount = 0 }, TimeSpan.Zero, TimeSpan.Zero).Title.StartsWith("Step ", StringComparison.Ordinal),
            "invalid phase metadata is not presented as a valid step count");

        SetupProgressText quiet = SetupProgressPresentation.Format(phase, TimeSpan.FromMinutes(7), TimeSpan.FromSeconds(95));
        Check(quiet.Timing.Contains("Elapsed 7m 0s", StringComparison.Ordinal) && quiet.Timing.Contains("no new update for 1m 35s", StringComparison.Ordinal),
            "quiet output still shows elapsed and last-activity time");
        Check(quiet.Timing.StartsWith("In progress · ", StringComparison.Ordinal) && quiet.Timing.Contains("Still working", StringComparison.Ordinal) &&
            !Regex.IsMatch(quiet.Timing, @"(?i)\b(failed|failure|stuck|frozen|timeout)\b|timed out"),
            "a quiet step is not falsely declared broken");
        Check(quiet.Detail == phase.Detail, "quiet activity preserves useful phase-specific details instead of replacing them");
        SetupProgressText quietUnknown = SetupProgressPresentation.Format(phase with { Detail = null }, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30));
        Check(quietUnknown.Fraction is null && quietUnknown.Detail == "Waiting for this step to finish. You can open Troubleshooting for details.",
            "an unknown quiet step gets an actionable detail hint without inventing a percentage");
        Check(SetupProgressPresentation.Format(phase with { Detail = null }, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(29)).Detail == "",
            "the waiting hint appears after the threshold, not immediately");
        checks += RunTaskPhasePresentationChecks();
        Check(SetupProgressPresentation.Duration(TimeSpan.FromSeconds(-4)) == "0s" &&
            SetupProgressPresentation.Duration(TimeSpan.FromSeconds(3661)) == "1h 1m", "timing handles clock skew and long setup steps");

        TextBlock status = Read<TextBlock>(window, "_status");
        TextBlock detail = Read<TextBlock>(window, "_progressDetail");
        TextBlock timing = Read<TextBlock>(window, "_progressTiming");
        ProgressBar progress = Read<ProgressBar>(window, "_progress");
        Button cancel = Read<Button>(window, "_cancel");
        Button saveButton = Read<Button>(window, "_saveButton"), taskTroubleshooting = Read<Button>(window, "_taskTroubleshooting");
        var savedTaskActions = (SaveEnabled: saveButton.IsEnabled, SaveVisibility: saveButton.Visibility,
            HelpEnabled: taskTroubleshooting.IsEnabled, HelpVisibility: taskTroubleshooting.Visibility);
        FrameworkElement content = (FrameworkElement)window.Content;
        string originalPage = Read<string>(window, "_page");
        string settingsBefore = JsonSerializer.Serialize(Read<AppSettings>(window, "_settings"));
        var savedFields = new Dictionary<string, object?>();
        foreach (string field in new[] { "_busy", "_closing", "_finishingSafeShutdown", "_taskGeneration", "_operation", "_currentProgress", "_operationStartedAt", "_lastProgressAt", "_setupActionButton" })
            savedFields[field] = Field(field).GetValue(window);
        var savedUi = Snapshot();
        Size originalSize = content.RenderSize;
        bool timerBefore = Read<DispatcherTimer>(window, "_logTimer").IsEnabled;
        bool taskTimerBefore = Read<DispatcherTimer>(window, "_taskTimer").IsEnabled;
        using var firstOperation = new CancellationTokenSource();
        using var secondOperation = new CancellationTokenSource();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        try
        {
            Check(!timerBefore, "the preview does not start a progress timer");
            Write(window, "_busy", false); Write(window, "_closing", false); window.Navigate("Setup");
            Button setupAction = Read<Button>(window, "_setupActionButton");
            Check(setupAction.Content is string setupLabel && (setupLabel is "Set up SayWhat?" or "Continue setup" or "Check & repair setup") && setupAction.IsEnabled,
                "idle setup exposes its normal start or continue action");
            Write(window, "_busy", true); Write(window, "_closing", false);
            Write(window, "_operation", firstOperation);
            Write(window, "_operationStartedAt", DateTimeOffset.UtcNow.AddSeconds(-70));
            Update(phase);
            Check(status.Text == unknown.Title && detail.Text == phase.Detail, "footer uses the same truthful phase and detail formatter");
            Check(!setupAction.IsEnabled, "active progress disables setup's action button so another setup cannot be started");
            Check(!saveButton.IsEnabled, "actual Save & apply cannot start another restart while a task is running");
            Check(progress.Visibility == Visibility.Collapsed && !progress.IsIndeterminate && progress.Value == 0,
                "unknown work hides the bar instead of showing an endless fake animation");
            Check(detail.Visibility == Visibility.Visible && timing.Visibility == Visibility.Visible,
                "phase detail and elapsed activity stay visible without a percentage");
            var originalActivity = DateTimeOffset.UtcNow.AddSeconds(-12); Write(window, "_lastProgressAt", originalActivity);
            Update(phase);
            Check(Read<DateTimeOffset>(window, "_lastProgressAt") == originalActivity, "duplicate backend progress does not falsely reset the last-activity clock");
            var beforeRuntimeUpdate = Snapshot();
            Invoke(window, "RuntimeChanged"); DrainDispatcher();
            Check(Snapshot() == beforeRuntimeUpdate && status.Text.StartsWith("Step 2 of 5", StringComparison.Ordinal),
                "an asynchronous runtime status callback cannot replace the numbered title while setup is busy");
            Check(AutomationProperties.GetName(progress) == "Current task progress" &&
                AutomationProperties.GetName(detail) == "Current task detail" &&
                AutomationProperties.GetName(timing) == "Task elapsed time and activity", "footer fields retain accessible names");

            Update(download);
            Check(progress.Visibility == Visibility.Visible && !progress.IsIndeterminate && progress.Value == 25,
                "a known task fraction produces a determinate bar");
            Check(detail.Text == measured.Detail, "footer includes byte counters and current-task wording");

            // Progress<T> captures this inert dispatcher. Only its queued progress callback
            // and a sentinel are delivered; no window is shown or Loaded event is raised.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            IProgress<ProvisionProgress> oldCallback = Callback();
            Write(window, "_operation", secondOperation);
            var beforeStale = Snapshot();
            Deliver(oldCallback, phase with { Message = "Stale previous setup callback" });
            Check(Snapshot() == beforeStale, "a previous operation's queued callback cannot overwrite the current footer");
            IProgress<ProvisionProgress> currentCallback = Callback();
            Deliver(currentCallback, phase);
            Check(status.Text == unknown.Title, "the current operation's callback still updates progress");
            IProgress<ProvisionProgress> restartCallback = (IProgress<ProvisionProgress>)Invoke(window, "RuntimeTaskProgress", 3, 3)!;
            Deliver(restartCallback, new("Runtime", "Loading translation model") { StepIndex = 1, StepCount = 99, Detail = "Model: Home · literal model name" });
            Check(status.Text == "Step 3 of 3 · Loading translation model" && detail.Text == "Model: Home · literal model name",
                "runtime model progress stays within the real Save & apply restart phase instead of inventing unrelated completion");
            Check(!saveButton.IsEnabled && progress.Visibility == Visibility.Collapsed && !progress.IsIndeterminate,
                "restart model loading visibly remains in progress without a made-up percentage or second Apply action");
            Deliver(currentCallback, phase);
            IProgress<ProvisionProgress> previousStep = (IProgress<ProvisionProgress>)Invoke(window, "RuntimeTaskProgress", 1, 4)!;
            previousStep.Report(new("Runtime", "Stopping current translations"));
            Update(new("Runtime", "Saving your settings") { StepIndex = 2, StepCount = 4 });
            var beforePreviousStep = Snapshot(); DrainDispatcher();
            Check(Snapshot() == beforePreviousStep, "a queued runtime callback from the previous numbered phase cannot move Apply backwards");
            IProgress<ProvisionProgress> sameStep = (IProgress<ProvisionProgress>)Invoke(window, "RuntimeTaskProgress", 2, 4)!;
            Deliver(sameStep, new("Runtime", "Loading translation model") { Detail = "Model: Home · literal model name" });
            Check(status.Text == "Step 2 of 4 · Loading translation model", "current numbered runtime phase callbacks remain valid");
            IProgress<ProvisionProgress> nextStep = (IProgress<ProvisionProgress>)Invoke(window, "RuntimeTaskProgress", 3, 4)!;
            Deliver(nextStep, new("Runtime", "Applying settings"));
            Check(status.Text == "Step 3 of 4 · Applying settings", "a valid next numbered runtime phase advances Apply");
            Deliver(currentCallback, phase);

            Write(window, "_busy", false);
            var idle = Snapshot(); Update(download);
            Check(Snapshot() == idle, "idle progress updates are ignored after completion");
            Deliver(currentCallback, download);
            Check(Snapshot() == idle, "an already-queued current callback cannot replace a completed status");
            Write(window, "_busy", true); Invoke(window, "RuntimeChanged");
            Write(window, "_busy", false); UiLocalization.SetText(status, "Settings applied. Translation restarted.");
            var completed = Snapshot(); DrainDispatcher();
            Check(Snapshot() == completed, "a runtime notification originating during a task cannot overwrite completion after the busy flag clears");
            Invoke(window, "RuntimeChanged");
            Write(window, "_taskGeneration", Read<int>(window, "_taskGeneration") + 1);
            UiLocalization.SetText(status, "Settings saved.");
            completed = Snapshot(); DrainDispatcher();
            Check(Snapshot() == completed, "a queued idle runtime notification cannot erase a newer task generation's completion");
            Write(window, "_busy", true); Write(window, "_closing", true);
            var closing = Snapshot(); Update(download);
            Check(Snapshot() == closing, "closing the app rejects late progress updates");
            Write(window, "_busy", false);
            Invoke(window, "RuntimeChanged"); DrainDispatcher();
            Check(Snapshot() == closing, "an asynchronous runtime callback cannot overwrite the status while closing, even when no task is busy");
            Write(window, "_busy", true);
            Write(window, "_closing", false);

            Write(window, "_currentProgress", phase);
            Write(window, "_operationStartedAt", DateTimeOffset.UtcNow.AddMinutes(-7));
            Write(window, "_lastProgressAt", DateTimeOffset.UtcNow.AddSeconds(-95));
            Invoke(window, "RefreshProgressTiming");
            Check(timing.Text.Contains("no new update for", StringComparison.Ordinal) && timing.Text.Contains("Still working", StringComparison.Ordinal),
                "the UI clock exposes silent output without declaring setup failure");
            Check(taskTroubleshooting.Name == "TaskTroubleshootingButton" && taskTroubleshooting.Visibility == Visibility.Visible && taskTroubleshooting.IsEnabled,
                "a quiet task keeps an enabled Troubleshooting action available without starting another task");
            var quietActivity = Read<DateTimeOffset>(window, "_lastProgressAt");
            window.Navigate("Diagnostics");
            Check(Read<DateTimeOffset>(window, "_lastProgressAt") == quietActivity && taskTroubleshooting.IsEnabled && !saveButton.IsEnabled,
                "Troubleshooting can be viewed while a task continues, without restarting its clock or enabling another Apply");
            secondOperation.Cancel(); Invoke(window, "RefreshProgressTiming");
            Check(Equals(cancel.Content, "Cancelling…") && !cancel.IsEnabled, "cancellation visibly acknowledges the request without claiming it has already stopped");
            Write(window, "_finishingSafeShutdown", true); Invoke(window, "RefreshProgressTiming");
            Check(Equals(cancel.Content, "Finishing safe shutdown…") && !cancel.IsEnabled,
                "cancellation distinguishes safe cleanup from a claim that models have already stopped");
            Write(window, "_finishingSafeShutdown", false);

            window.Navigate("Setup"); cancel.Visibility = Visibility.Visible;
            foreach (Size size in new[] { new Size(900, 650), new Size(1160, 800) })
            {
                foreach (ProvisionProgress sample in new[] { download, phase with
                {
                    Detail = "Preparing Ubuntu speech tools and compiling the local recognition program. Downloads and package installation can be quiet for a while; translation has not started."
                } })
                {
                    Update(sample);
                    Write(window, "_lastProgressAt", DateTimeOffset.UtcNow.AddSeconds(-35)); Invoke(window, "RefreshProgressTiming");
                    Layout(content, size);
                    Check(content.RenderSize.Width <= size.Width + .5 && content.RenderSize.Height <= size.Height + .5,
                        "setup root stays within " + size);
                    foreach (FrameworkElement element in new FrameworkElement[] { status, detail, timing, progress, cancel, taskTroubleshooting }
                        .Where(element => element.Visibility == Visibility.Visible))
                    {
                        Rect bounds = element.TransformToAncestor(content).TransformBounds(new Rect(new Point(), element.RenderSize));
                        Check(double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && bounds.Width > 0 && bounds.Height > 0 &&
                            bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= size.Width + .5 && bounds.Bottom <= size.Height + .5,
                            element.GetType().Name + " remains fully inside the footer at " + size);
                    }
                    Check(status.TextWrapping == TextWrapping.Wrap && detail.TextWrapping == TextWrapping.Wrap && timing.TextWrapping == TextWrapping.Wrap,
                        "footer text wraps rather than clipping on smaller windows");
                    Check(VisualNodes(content).OfType<ScrollViewer>().All(view => view.ScrollableWidth < 1),
                        "busy setup does not require horizontal scrolling at " + size);
                }
            }
            Check(!window.IsLoaded && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning,
                "progress delivery and layout have not shown the app or started translation");
            Check(JsonSerializer.Serialize(Read<AppSettings>(window, "_settings")) == settingsBefore,
                "presentation checks preserve every in-memory setting");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            foreach ((string field, object? value) in savedFields) Field(field).SetValue(window, value);
            window.Navigate(originalPage);
            status.Text = savedUi.Status; detail.Text = savedUi.Detail; timing.Text = savedUi.Timing;
            status.Visibility = savedUi.StatusVisibility; detail.Visibility = savedUi.DetailVisibility;
            timing.Visibility = savedUi.TimingVisibility; progress.Visibility = savedUi.BarVisibility;
            progress.IsIndeterminate = savedUi.Indeterminate; progress.Value = savedUi.Value;
            cancel.Content = savedUi.CancelContent; cancel.Visibility = savedUi.CancelVisibility; cancel.IsEnabled = savedUi.CancelEnabled;
            saveButton.IsEnabled = savedTaskActions.SaveEnabled; saveButton.Visibility = savedTaskActions.SaveVisibility;
            taskTroubleshooting.IsEnabled = savedTaskActions.HelpEnabled; taskTroubleshooting.Visibility = savedTaskActions.HelpVisibility;
            if (originalSize.Width > 0 && originalSize.Height > 0) Layout(content, originalSize);
        }
        Check(Read<DispatcherTimer>(window, "_logTimer").IsEnabled == timerBefore && Read<DispatcherTimer>(window, "_taskTimer").IsEnabled == taskTimerBefore &&
            JsonSerializer.Serialize(Read<AppSettings>(window, "_settings")) == settingsBefore,
            "the preview's settings and stopped timer remain unchanged after restoration");
        return checks;

        void Update(ProvisionProgress value) => Invoke(window, "UpdateProvisionProgress", value);
        IProgress<ProvisionProgress> Callback() => (IProgress<ProvisionProgress>)Invoke(window, "ProvisionProgress")!;
        FooterSnapshot Snapshot() => new(status.Text, detail.Text, timing.Text, status.Visibility, detail.Visibility,
            timing.Visibility, progress.Visibility, progress.IsIndeterminate, progress.Value, cancel.Content,
            cancel.Visibility, cancel.IsEnabled, Read<ProvisionProgress?>(window, "_currentProgress"),
            Read<DateTimeOffset>(window, "_lastProgressAt"));
        void Deliver(IProgress<ProvisionProgress> callback, ProvisionProgress value)
        {
            callback.Report(value);
            DrainDispatcher();
        }
        void DrainDispatcher()
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static int RunTaskPhasePresentationChecks()
    {
        int checks = 0;
        void Check(bool passed, string reason) { if (!passed) throw new InvalidOperationException("Task progress presentation: " + reason); checks++; }
        string previous = UiText.Language;
        try
        {
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language;
                string[] phases = ["Stopping current translations", "Saving your settings", "Applying settings",
                    "Checking translation tools", "Stopping your voice translator", "Stopping subtitles", "Releasing translation models",
                    "Loading translation model", "Checking the shared translation model", "Starting speech recognition", "Connecting your microphone", "Opening subtitles"];
                for (int index = 0; index < phases.Length; index++)
                {
                    var phase = new ProvisionProgress("Runtime task", phases[index]) { StepIndex = index + 1, StepCount = phases.Length };
                    SetupProgressText active = SetupProgressPresentation.Format(phase, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(2));
                    Check(active.Title == UiText.Format("Step {0} of {1} · {2}", index + 1, phases.Length, UiText.T(phases[index])), "actual runtime phase remains visible in " + language);
                    Check(active.Timing == UiText.Format("In progress · Elapsed {0} · {1}", SetupProgressPresentation.Duration(TimeSpan.FromSeconds(12)),
                        UiText.Format("Last update {0} ago", SetupProgressPresentation.Duration(TimeSpan.FromSeconds(2)))), "active task is explicit, not merely an elapsed clock: " + language);
                    Check(active.Fraction is null && !active.Detail.Contains('%'), "runtime readiness is not presented as a made-up download fraction");
                    SetupProgressText quiet = SetupProgressPresentation.Format(phase, TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(40));
                    Check(quiet.Detail == UiText.T("Waiting for this step to finish. You can open Troubleshooting for details."), "quiet runtime work supplies useful next-step guidance: " + language);
                    Check(quiet.Timing.Contains(UiText.Format("Still working · no new update for {0}", SetupProgressPresentation.Duration(TimeSpan.FromSeconds(40))), StringComparison.Ordinal),
                        "lack of backend output is not misreported as failure: " + language);
                }
                const string modelName = "Home · Hy-MT2 local model";
                var modelPhase = new ProvisionProgress("Runtime task", "Loading translation model") { Detail = "Model: " + modelName };
                SetupProgressText model = SetupProgressPresentation.Format(modelPhase, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(40));
                Check(model.Detail.Contains(modelName, StringComparison.Ordinal), "model names containing UI words are preserved literally: " + language);
                Check(model.Detail == UiText.Format("Model: {0}", modelName), "phase-specific model context is retained and localized during quiet work: " + language);
            }
        }
        finally { UiText.Language = previous; }
        return checks;
    }

    // Optional visual QA output, called only by the explicit offline test runner. The
    // fixture directory must already exist and own that runner's isolated data root.
    internal static int RenderPreview(MainWindow window, string directory)
    {
        if (!Read<bool>(window, "_preview") || !window.Dispatcher.CheckAccess() || window.IsVisible || window.IsLoaded)
            throw new InvalidOperationException("Setup progress rendering requires an unshown preview on its owner STA.");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new ArgumentException("Choose an existing explicit offline fixture directory.", nameof(directory));
        directory = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetFullPath(AppPaths.Root), Path.Combine(directory, "data"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Setup progress preview output must belong to the offline runner's isolated data fixture.");
        RuntimeController runtime = Read<RuntimeController>(window, "_runtime");
        if (runtime.OwnRunning || runtime.IncomingRunning)
            throw new InvalidOperationException("Rendering is not permitted while translation is running.");

        TextBlock status = Read<TextBlock>(window, "_status"), detail = Read<TextBlock>(window, "_progressDetail"),
            timing = Read<TextBlock>(window, "_progressTiming");
        ProgressBar progress = Read<ProgressBar>(window, "_progress");
        Button cancel = Read<Button>(window, "_cancel");
        FrameworkElement content = (FrameworkElement)window.Content;
        Button saveButton = Read<Button>(window, "_saveButton"), taskTroubleshooting = Read<Button>(window, "_taskTroubleshooting");
        var savedTaskActions = (SaveEnabled: saveButton.IsEnabled, SaveVisibility: saveButton.Visibility,
            HelpEnabled: taskTroubleshooting.IsEnabled, HelpVisibility: taskTroubleshooting.Visibility);
        string originalPage = Read<string>(window, "_page");
        string settingsBefore = JsonSerializer.Serialize(Read<AppSettings>(window, "_settings"));
        Size originalSize = content.RenderSize;
        var savedFields = new Dictionary<string, object?>();
        foreach (string field in new[] { "_busy", "_closing", "_finishingSafeShutdown", "_taskGeneration", "_operation", "_currentProgress", "_operationStartedAt", "_lastProgressAt", "_setupActionButton" })
            savedFields[field] = Field(field).GetValue(window);
        var savedUi = new FooterSnapshot(status.Text, detail.Text, timing.Text, status.Visibility, detail.Visibility,
            timing.Visibility, progress.Visibility, progress.IsIndeterminate, progress.Value, cancel.Content,
            cancel.Visibility, cancel.IsEnabled, Read<ProvisionProgress?>(window, "_currentProgress"),
            Read<DateTimeOffset>(window, "_lastProgressAt"));
        using var operation = new CancellationTokenSource();
        int checks = 0;
        try
        {
            window.Navigate("Setup");
            Write(window, "_busy", true); Write(window, "_closing", false); Write(window, "_operation", operation);
            Write(window, "_operationStartedAt", DateTimeOffset.UtcNow.AddSeconds(-260));
            cancel.Content = "Cancel task"; cancel.IsEnabled = true; cancel.Visibility = Visibility.Visible;
            var sample = new ProvisionProgress("Building speech runtime", "Building speech recognition")
            {
                StepIndex = 4, StepCount = 5, WorkFraction = 73d / 184,
                Detail = "Compiling the speech engine · 73 of 184 build tasks"
            };
            Invoke(window, "UpdateProvisionProgress", sample);
            Write(window, "_lastProgressAt", DateTimeOffset.UtcNow.AddSeconds(-38));
            Invoke(window, "RefreshProgressTiming");
            foreach (var image in new[]
            {
                (Size: new Size(1160, 800), File: "setup-progress-example.png"),
                (Size: new Size(900, 650), File: "setup-progress-example-narrow.png")
            })
            {
                Layout(content, image.Size);
                var bitmap = new RenderTargetBitmap((int)image.Size.Width, (int)image.Size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                // These two literal files are the only output; no directory is created or cleaned.
                using var output = File.Create(Path.Combine(directory, image.File)); encoder.Save(output);
                checks++;
            }
            window.Navigate("Languages");
            Invoke(window, "UpdateProvisionProgress", new ProvisionProgress("Applying settings", "Starting speech recognition")
            { StepIndex = 3, StepCount = 3, Detail = "Waiting for speech recognition to be ready." });
            Write(window, "_operationStartedAt", DateTimeOffset.UtcNow.AddSeconds(-50));
            Write(window, "_lastProgressAt", DateTimeOffset.UtcNow.AddSeconds(-35));
            Invoke(window, "RefreshProgressTiming");
            foreach (var image in new[] {
                (Size: new Size(1160, 800), File: "restart-progress-example.png"),
                (Size: new Size(900, 650), File: "restart-progress-example-narrow.png") })
            {
                Layout(content, image.Size);
                var bitmap = new RenderTargetBitmap((int)image.Size.Width, (int)image.Size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, image.File)); encoder.Save(output); checks++;
            }
            if (saveButton.IsEnabled || taskTroubleshooting.Visibility != Visibility.Visible || !taskTroubleshooting.IsEnabled)
                throw new InvalidOperationException("Restart preview must show Apply disabled and Troubleshooting available during quiet work.");
            checks++;
            if (window.IsVisible || window.IsLoaded || runtime.OwnRunning || runtime.IncomingRunning)
                throw new InvalidOperationException("Preview rendering must not activate a window or translation feature.");
            if (JsonSerializer.Serialize(Read<AppSettings>(window, "_settings")) != settingsBefore)
                throw new InvalidOperationException("Preview rendering changed an in-memory setting.");
            checks += 2;
        }
        finally
        {
            foreach ((string field, object? value) in savedFields) Field(field).SetValue(window, value);
            window.Navigate(originalPage);
            status.Text = savedUi.Status; detail.Text = savedUi.Detail; timing.Text = savedUi.Timing;
            status.Visibility = savedUi.StatusVisibility; detail.Visibility = savedUi.DetailVisibility;
            timing.Visibility = savedUi.TimingVisibility; progress.Visibility = savedUi.BarVisibility;
            progress.IsIndeterminate = savedUi.Indeterminate; progress.Value = savedUi.Value;
            cancel.Content = savedUi.CancelContent; cancel.Visibility = savedUi.CancelVisibility; cancel.IsEnabled = savedUi.CancelEnabled;
            saveButton.IsEnabled = savedTaskActions.SaveEnabled; saveButton.Visibility = savedTaskActions.SaveVisibility;
            taskTroubleshooting.IsEnabled = savedTaskActions.HelpEnabled; taskTroubleshooting.Visibility = savedTaskActions.HelpVisibility;
            if (originalSize.Width > 0 && originalSize.Height > 0) Layout(content, originalSize);
        }
        if (JsonSerializer.Serialize(Read<AppSettings>(window, "_settings")) != settingsBefore)
            throw new InvalidOperationException("Preview rendering failed to preserve the original settings.");
        return checks + 1;
    }

    private sealed record FooterSnapshot(string Status, string Detail, string Timing, Visibility StatusVisibility,
        Visibility DetailVisibility, Visibility TimingVisibility, Visibility BarVisibility, bool Indeterminate,
        double Value, object CancelContent, Visibility CancelVisibility, bool CancelEnabled,
        ProvisionProgress? Current, DateTimeOffset LastProgressAt);

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(MainWindow).FullName, name);
    private static T Read<T>(MainWindow window, string name) => (T)Field(name).GetValue(window)!;
    private static void Write(MainWindow window, string name, object? value) => Field(name).SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object[] arguments) =>
        (typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, name)).Invoke(window, arguments);
    private static void Layout(FrameworkElement element, Size size)
    {
        element.Measure(size); element.Arrange(new Rect(new Point(), size)); element.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> VisualNodes(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in VisualNodes(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}

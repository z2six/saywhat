using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

// Inert, never-shown WPF and synthetic feedback hosts only: no microphone,
// playback capture, model, real person file, or translation request is used.
internal static class SpeakerSuggestionOverlayTests
{
    internal static int Run(string directory)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Suggestion overlay: " + detail); checks++; }
        string? fixture = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(!string.IsNullOrWhiteSpace(fixture) && string.Equals(Path.GetFullPath(fixture), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase),
            "requires an isolated desktop-test data folder");
        Directory.CreateDirectory(directory);
        AppSettings original = SettingsStore.Load();
        string originalLanguage = UiText.Language;
        SynchronizationContext? originalContext = SynchronizationContext.Current;
        var windows = new List<SubtitleWindow>();
        string logPath = Path.Combine(AppPaths.Root, "incoming-translator.log");
        byte[]? logBefore = File.Exists(logPath) ? File.ReadAllBytes(logPath) : null;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        SubtitleWindow Window(string mode = "Sentence bubbles")
        {
            var overlay = new SubtitleWindow(); windows.Add(overlay);
            ((AppSettings)typeof(SubtitleWindow).GetField("_settings", flags)!.GetValue(overlay)!).OverlayMode = mode;
            overlay.CorrectSpeakerRequested = (_, _, _, _) => Task.FromResult<SpeakerFeedbackResult?>(null);
            overlay.RefreshSpeakerNames(State());
            return overlay;
        }
        static TranslationDisplay Guess(long id, string person = "mira", string name = "Mira", string? evidence = null) =>
            new(name, "Synthetic source", "A translated sentence appears immediately.", id, DateTimeOffset.UnixEpoch.AddSeconds(id))
            { PersonId = person, EvidenceId = evidence ?? "evidence-" + id, SpeakerRecognitionEpoch = 7 };
        static Button[] Reviews(SubtitleWindow overlay) => Nodes(overlay).OfType<Button>().Where(button => button.Name is
            "ConfirmSpeakerSuggestionButton" or "RejectSpeakerSuggestionButton").ToArray();
        try
        {
            foreach (string mode in new[] { "Sentence bubbles", "Latest sentence", "Speaker cards" })
            {
                var overlay = Window(mode); overlay.Publish(Guess(1));
                Check(overlay.HistoryEntry(1) is { IsManuallyAttributed: false, PersonId: "mira" }, "automatic identity stays tentative in " + mode);
                Check(Nodes(overlay).OfType<TextBlock>().Any(text => text.Text == UiText.Format("Maybe {0}", "Mira")), "tentative name is explicit in " + mode);
                Check(Reviews(overlay).Length == 2 && Reviews(overlay).All(button => button.IsEnabled), "inline yes/no are available in " + mode);
                Check(Nodes(overlay).OfType<Image>().Any(image => image.Source is DrawingImage), "suggested person has their local generated avatar in " + mode);
                Check(Nodes(overlay).OfType<TextBlock>().Any(text => text.Text.Contains("translated sentence", StringComparison.Ordinal)), "translation is never withheld while waiting for feedback in " + mode);
                var host = new Host(State()); Pump(overlay.ReviewSpeakerSuggestionAsync(Guess(1), true, host));
                Check(host.Calls.Count == 1 && host.Calls[0] == ("evidence-1", "mira", true), "confirmation reviews the exact candidate/evidence in " + mode);
                Check(overlay.HistoryEntry(1) is { IsManuallyAttributed: true, PersonId: "mira" } && Reviews(overlay).Length == 0,
                    "confirmed identity hides suggestion controls in " + mode);
                Check(Nodes(overlay).OfType<Button>().Any(button => button.Tag is TranslationDisplay), "manual picker/undo remains available after confirmation in " + mode);
                overlay.Publish(Guess(1) with { Source = "Late revision", At = DateTimeOffset.UnixEpoch.AddSeconds(2) });
                Check(overlay.HistoryEntry(1) is { IsManuallyAttributed: true, PersonId: "mira" } && Reviews(overlay).Length == 0,
                    "late inference cannot undo an explicit confirmation in " + mode);
                overlay.ApplySpeakerCorrection(1, new(false, "Correction undone; synthetic", "mira", "Mira", false)
                    { IsManuallyAttributed = false }, "evidence-1");
                Check(Reviews(overlay).Length == 2 && overlay.HistoryEntry(1) is { IsManuallyAttributed: false }, "undo restores a tentative suggestion rather than claiming confirmation in " + mode);
            }

            var grouping = Window("Speaker cards"); grouping.Publish(Guess(20)); grouping.Publish(Guess(21));
            Check(grouping.VisibleBubbleCount == 2, "two uncertain voices are not combined just because both guessed the same person");
            grouping.ApplySpeakerCorrection(20, Confirm("mira")); grouping.ApplySpeakerCorrection(21, Confirm("mira"));
            Check(grouping.VisibleBubbleCount == 1, "reviewed identities can share the same person card");

            var negative = Window(); negative.Publish(Guess(30)); var negativeHost = new Host(State());
            Pump(negative.ReviewSpeakerSuggestionAsync(Guess(30), false, negativeHost));
            Check(negative.HistoryEntry(30) is { PersonId: null, Speaker: "Unidentified speaker", IsManuallyAttributed: true }, "No changes only this bubble to unknown");
            Check(Reviews(negative).Length == 0 && Nodes(negative).OfType<TextBlock>().Any(text => text.Text == UiText.T("Wrong match remembered · voice example saved")),
                "saved negative voice feedback is explained distinctly");
            negative.Publish(Guess(31));
            Check(negative.HistoryEntry(31) is { IsManuallyAttributed: false, PersonId: "mira" }, "one rejection does not silently label future bubbles");

            var rejected = Window(); rejected.Publish(Guess(40)); var rejectedHost = new Host(State())
            { Review = (_, _, _) => Task.FromResult(new SpeakerFeedbackResult(false, "Synthetic stale rejection", null, "Unidentified speaker", false, false)) };
            Pump(rejected.ReviewSpeakerSuggestionAsync(Guess(40), false, rejectedHost));
            Check(rejected.HistoryEntry(40) is { PersonId: "mira", IsManuallyAttributed: false } && Reviews(rejected).All(button => button.IsEnabled),
                "failed feedback keeps the suggestion and supports retry");
            Check(Nodes(rejected).OfType<TextBlock>().Any(text => text.Text == UiText.T("Couldn't review that suggestion. Please try again.")), "failure is visible beside the affected sentence");
            rejectedHost.Review = null; Pump(rejected.ReviewSpeakerSuggestionAsync(Guess(40), true, rejectedHost));
            Check(rejected.HistoryEntry(40) is { IsManuallyAttributed: true }, "retry can successfully confirm without dropping translation");

            var failedWrite = Window(); failedWrite.Publish(Guess(41)); var failingHost = new Host(State())
            { Review = (_, _, _) => Task.FromException<SpeakerFeedbackResult>(new IOException("Synthetic protected feedback file")) };
            Pump(failedWrite.ReviewSpeakerSuggestionAsync(Guess(41), true, failingHost));
            Check(failedWrite.HistoryEntry(41) is { PersonId: "mira", IsManuallyAttributed: false }
                && Reviews(failedWrite).All(button => button.IsEnabled), "write exception preserves the candidate and restores usable review controls");
            Check(Nodes(failedWrite).OfType<TextBlock>().Any(text => text.Text == UiText.T("Couldn't review that suggestion. Please try again."))
                && File.Exists(logPath) && File.ReadAllText(logPath).Contains("Review person suggestion", StringComparison.Ordinal),
                "write failure is visible without raw errors and recorded in local troubleshooting logs");

            var noEvidence = Window(); noEvidence.Publish(Guess(42) with { EvidenceId = "" });
            var noEvidenceHost = new Host(State()); Pump(noEvidence.ReviewSpeakerSuggestionAsync(Guess(42) with { EvidenceId = "" }, true, noEvidenceHost));
            Check(noEvidenceHost.Calls.Count == 0 && Reviews(noEvidence).All(button => !button.IsEnabled), "an older/missing evidence bubble never invents a voice example");

            var consent = Window(); consent.Publish(Guess(50)); var fresh = new Host(State(false)); int introductions = 0;
            consent.EnsureSpeakerLearningRequested = (_, _, _, _) => { introductions++; return Task.FromResult(false); };
            Pump(consent.ReviewSpeakerSuggestionAsync(Guess(50), true, fresh));
            Check(introductions == 1 && fresh.Calls.Count == 0 && Reviews(consent).All(button => button.IsEnabled), "declining first-example consent leaves Yes suggestion unconfirmed");
            Pump(consent.ReviewSpeakerSuggestionAsync(Guess(50), false, fresh));
            Check(introductions == 2 && fresh.Calls.Count == 1 && fresh.Acknowledgments == 0 && consent.HistoryEntry(50) is { PersonId: null },
                "No still clears the wrong name without silently acknowledging teaching");
            Check(Nodes(consent).OfType<TextBlock>().Any(text => text.Text == UiText.T("Marked as unknown · no voice example saved")), "label-only rejection is not claimed as learned");

            var accepted = Window(); accepted.Publish(Guess(51)); var firstAcceptedHost = new Host(State(false));
            accepted.EnsureSpeakerLearningRequested = async (_, host, _, _) => { await host.AcknowledgeSpeakerLearningAsync(); return true; };
            Pump(accepted.ReviewSpeakerSuggestionAsync(Guess(51), true, firstAcceptedHost));
            Check(firstAcceptedHost.Acknowledgments == 1 && accepted.HistoryEntry(51) is { IsManuallyAttributed: true }, "explicit first-time acknowledgment enables clear-example confirmation");

            var gate = new TaskCompletionSource<SpeakerFeedbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var busy = Window(); busy.Publish(Guess(60)); var slowHost = new Host(State()) { Review = (_, _, _) => gate.Task };
            Task pending = busy.ReviewSpeakerSuggestionAsync(Guess(60), true, slowHost);
            Check(!pending.IsCompleted && Reviews(busy).All(button => !button.IsEnabled), "per-bubble feedback disables duplicate review clicks while writing");
            Pump(busy.ReviewSpeakerSuggestionAsync(Guess(60), false, slowHost));
            Check(slowHost.Calls.Count == 1, "an extra No click does not race a pending Yes");
            busy.Publish(Guess(61, "kai", "Kai"));
            Check(busy.HistoryEntryCount == 2 && Reviews(busy).Count(button => button.IsEnabled) == 2, "new subtitles and another bubble's feedback continue during a slow save");
            busy.Publish(Guess(60, "kai", "Kai", "revised-evidence-60") with { At = DateTimeOffset.UnixEpoch.AddSeconds(62) });
            gate.SetResult(Confirm("mira")); Pump(pending);
            Check(busy.HistoryEntry(60) is { PersonId: "kai", EvidenceId: "revised-evidence-60", IsManuallyAttributed: false }, "late response for an old candidate cannot overwrite a revised bubble");
            Check(Nodes(busy).OfType<TextBlock>().Any(text => text.Text == UiText.T("This suggestion changed. Review the latest name.")), "changed candidate has clear feedback rather than a silent wrong assignment");
            int callsBefore = slowHost.Calls.Count; Pump(busy.ReviewSpeakerSuggestionAsync(Guess(60), true, slowHost));
            Check(slowHost.Calls.Count == callsBefore, "old detached button candidate is rejected before calling the host");

            var layout = Window(); layout.Publish(Guess(71)); layout.Publish(Guess(72, "kai", "Kai"));
            layout.ApplySpeakerCorrection(72, Confirm("kai"));
            foreach (ThemePreset preset in AppearanceTheme.Presets)
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                AppSettings themed = original.Copy(); themed.UiThemeId = preset.Id; AppearanceTheme.ApplyForTests(themed);
                UiText.Language = language; layout.RefreshInterfaceLanguage();
                Check(Nodes(layout).OfType<TextBlock>().Any(text => text.Text == UiText.Format("Maybe {0}", "Mira")), "tentative formatted label updates live in " + preset.Id + "/" + language);
                foreach (Button button in Reviews(layout))
                {
                    string caption = button.Name.StartsWith("Confirm", StringComparison.Ordinal) ? "Confirm this person" : "Not this person";
                    Check(button.ToolTip?.ToString() == UiText.T(caption) && AutomationProperties.GetName(button) == UiText.T(caption), "review hints/accessibility update live in " + preset.Id + "/" + language);
                    Check(ReferenceEquals(button.Foreground, caption == "Confirm this person" ? AppearanceTheme.Good : AppearanceTheme.Warning), "feedback glyph contrast follows shared theme roles");
                }
            FrameworkElement content = layout.TranscriptRoot;
                foreach (Size size in new[] { new Size(560, 430), new Size(960, 540) })
                {
                    content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
                    foreach (Button button in Reviews(layout))
                    {
                        Point point = button.TranslatePoint(new Point(), content);
                        Check(button.ActualWidth >= 24 && button.ActualHeight >= 24 && point.X >= 0 && point.X + button.ActualWidth <= size.Width + .5,
                            "yes/no remain reachable at " + size + " in " + preset.Id + "/" + language);
                    }
                }
                if (preset.Id == "coral-night") Render(content, Path.Combine(directory, "speaker-suggestions-" + language + ".png"));
            }
            Check(windows.All(window => !window.IsVisible && !window.IsLoaded && window.SpeakerFeedbackHost is null),
                "all synthetic review controls remain unshown and cannot start capture/inference");
        }
        finally
        {
            foreach (SubtitleWindow window in windows) window.Close();
            UiText.Language = originalLanguage; AppearanceTheme.Apply(original); ThemeBackground.Apply(original);
            SynchronizationContext.SetSynchronizationContext(originalContext);
            if (logBefore is null) { if (File.Exists(logPath)) File.Delete(logPath); }
            else File.WriteAllBytes(logPath, logBefore);
        }
        return checks;
    }

    private static SpeakerFeedbackState State(bool acknowledged = true) => new(new[]
        { new SpeakerPerson("mira", "Mira", 4), new SpeakerPerson("kai", "Kai", 3) }, acknowledged);
    private static SpeakerFeedbackResult Confirm(string person) => new(true, "Synthetic confirmation", person, person == "mira" ? "Mira" : "Kai", true)
        { SuggestionConfirmed = true };
    private sealed class Host(SpeakerFeedbackState initial) : ISpeakerFeedbackHost
    {
        private SpeakerFeedbackState _state = initial;
        internal List<(string Evidence, string Person, bool Confirmed)> Calls { get; } = new();
        internal Func<string, string, bool, Task<SpeakerFeedbackResult>>? Review;
        internal int Acknowledgments;
        public SpeakerFeedbackState? SpeakerFeedbackSnapshot => _state;
        public Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default) => Task.FromResult(_state);
        public Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default)
        { Acknowledgments++; _state = _state with { InstructionAcknowledged = true }; return Task.CompletedTask; }
        public Task<SpeakerFeedbackResult> ReviewSpeakerSuggestionAsync(string evidenceId, string suggestedPersonId, bool confirmed, CancellationToken token = default)
        {
            Calls.Add((evidenceId, suggestedPersonId, confirmed));
            return Review?.Invoke(evidenceId, suggestedPersonId, confirmed) ?? Task.FromResult(confirmed ? Confirm(suggestedPersonId) :
                new SpeakerFeedbackResult(false, "Synthetic rejection", null, "Unidentified speaker", true)
                { NegativeLearned = _state.InstructionAcknowledged, SuggestionConfirmed = false });
        }
        public Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default) => throw new NotSupportedException();
    }
    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Synthetic suggestion did not settle");
            Drain(); Thread.Yield();
        }
        task.GetAwaiter().GetResult(); Drain();
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Render(FrameworkElement content, string path)
    {
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        if (root is SubtitleWindow overlay)
        {
            foreach (var node in Nodes(overlay.TranscriptRoot)) yield return node;
            foreach (var node in Nodes(overlay.CompactPreview)) yield return node;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency) foreach (DependencyObject node in Nodes(dependency)) yield return node;
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace IncomingTranslator;

// Actual correction-button events with a never-run service and a pending fake
// picker. All evidence is label-only synthetic RAM metadata; no model, audio,
// real person file, visible window or native handle is used.
internal static class CompactPersonCorrectionTests
{
    internal static int Run(string directory)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Compact person correction: " + detail); checks++; }
        string? redirected = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(AppPaths.IsPackagedApplication && !string.IsNullOrWhiteSpace(redirected) &&
            string.Equals(Path.GetFullPath(redirected), Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase),
            "requires the desktop runner's isolated settings root");
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string identitiesPath = Path.Combine(directory, "synthetic-identities.json");
        string peoplePath = Path.Combine(directory, "synthetic-people.json");
        string[] watched = [AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "incoming-translator.log")];
        var filesBefore = watched.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        AppSettings appearance = SettingsStore.Load();
        SynchronizationContext? context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var identifier = new AiSpeakerIdentifier(identitiesPath, peoplePath);
        using var service = new IncomingTranslationService(new AppSettings { UseAiSpeakerRecognition = false },
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, identifier);
        var overlay = new SubtitleWindow();
        SubtitleTextSurface? text = null;
        SubtitleActionSurface? action = null;
        T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, flags)!.GetValue(instance)!;
        HashSet<long> Busy() => Field<HashSet<long>>(overlay, "_speakerReviewBusy");
        HashSet<long> Picking() => Field<HashSet<long>>(overlay, "_personPickerMessages");
        int Lease() => Field<int>(overlay, "_compactSelectionDepth");
        SubtitleActionAnchor[] Anchors() => Field<List<SubtitleActionAnchor>>(overlay, "_compactActions").ToArray();
        Button Log(long id) => Nodes(overlay.TranscriptRoot).OfType<Button>()
            .Single(button => button.Tag is TranslationDisplay sentence && sentence.UtteranceId == id);
        Button Compact(long id) => action!.ActionControls.SelectMany(Nodes).OfType<Button>()
            .Single(button => button.Tag is TranslationDisplay sentence && sentence.UtteranceId == id);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var calls = new List<(Window Owner, FrameworkElement Anchor, ISpeakerFeedbackHost Host, TranslationDisplay Sentence)>();
        TaskCompletionSource<SpeakerFeedbackResult?> pending = NewPending();
        TranslationDisplay Sentence(long id, int revision)
        {
            SpeakerAttribution evidence = identifier.RecordLabelOnlyObservation(new("Synthetic person", 0, false, 0)
                { CanLearn = false, EvidenceStart = DateTimeOffset.UnixEpoch, EvidenceEnd = DateTimeOffset.UnixEpoch.AddSeconds(1) });
            Field<Dictionary<string, long>>(service, "_evidenceBubbles")[evidence.EvidenceId] = id;
            return new("Synthetic person", "Synthetic source " + id + "/" + revision, "Synthetic translation " + id + "/" + revision,
                id, DateTimeOffset.UnixEpoch.AddSeconds(id + revision / 10d)) { EvidenceId = evidence.EvidenceId };
        }
        bool Clean() => Busy().Count == 0 && Picking().Count == 0 && Lease() == 0;
        void Complete(SpeakerFeedbackResult? result)
        {
            pending.SetResult(result);
            PumpUntil(Clean);
            Drain();
        }
        try
        {
            typeof(SubtitleWindow).GetField("_service", flags)!.SetValue(overlay, service);
            overlay.CorrectSpeakerRequested = (owner, anchor, host, sentence) =>
            {
                calls.Add((owner, anchor, host, sentence));
                return pending.Task; // The fake owns no backend mutation/training call.
            };
            AppSettings settings = Field<AppSettings>(overlay, "_settings");
            settings.OverlayMode = "Sentence bubbles"; settings.SubtitlesVisibleMessages = 3;

            TranslationDisplay old = Sentence(101, 0), current = Sentence(101, 1);
            overlay.Publish(old); Button stale = Log(101);
            overlay.Publish(current);
            Check(stale.Tag is TranslationDisplay { EvidenceId: var oldEvidence } && oldEvidence == old.EvidenceId &&
                overlay.HistoryEntry(101)?.EvidenceId == current.EvidenceId,
                "the fixture retains an old button snapshot after the sentence receives new evidence");
            Click(stale);
            Check(calls.Count == 1 && calls[0].Sentence.EvidenceId == current.EvidenceId && calls[0].Sentence.Source == current.Source &&
                ReferenceEquals(calls[0].Host, service) && Busy().Contains(101) && Picking().Contains(101),
                "an old button routes the latest retained sentence/evidence to the common picker exactly once");
            Complete(new(false, "Synthetic picker result", null, "Latest assigned label", true));
            Check(overlay.HistoryEntry(101) is { Speaker: "Latest assigned label", IsManuallyAttributed: true } &&
                overlay.HistoryEntry(101)?.EvidenceId == current.EvidenceId &&
                Field<Dictionary<long, string>>(overlay, "_correctionEvidenceIds")[101] == current.EvidenceId,
                "the successful result stores the selected current evidence for later Undo");

            overlay.Publish(Sentence(202, 0));
            text = new SubtitleTextSurface(overlay, overlay.CompactPreview);
            action = new SubtitleActionSurface(overlay, text); action.SetActions(Anchors());
            Button log = Log(202), compactWhileLog = Compact(202);
            DependencyObject logHeader = LogicalTreeHelper.GetParent(log);
            pending = NewPending(); int before = calls.Count;
            Click(log);
            Check(calls.Count == before + 1 && ReferenceEquals(calls[^1].Anchor, log) && Lease() == 0,
                "the Log button uses the same callback without acquiring a compact-display lease");
            TranslationDisplay duringLog = Sentence(202, 1);
            overlay.Publish(duringLog); overlay.Publish(Sentence(203, 0));
            Check(ReferenceEquals(LogicalTreeHelper.GetParent(log), logHeader) && Nodes(overlay.TranscriptRoot).Contains(log) && !log.IsEnabled &&
                overlay.HistoryEntry(202)?.Source == duringLog.Source && overlay.HistoryEntry(203) is not null,
                "the clicked Log anchor stays attached while newer text and other sentences continue arriving");
            Click(compactWhileLog); Click(log);
            Check(calls.Count == before + 1 && Lease() == 0,
                "compact and Log clicks for the same pending sentence cannot open a second picker");
            Complete(null);
            Check(Clean() && overlay.HistoryEntry(202)?.Source == duringLog.Source &&
                !Field<Dictionary<long, SpeakerFeedbackResult>>(overlay, "_speakerCorrections").ContainsKey(202),
                "canceling a Log picker releases its guards without changing the sentence's label");

            TranslationDisplay compactSentence = Sentence(301, 0);
            overlay.Publish(compactSentence); action.SetActions(Anchors());
            Button compact = Compact(301), sameLog = Log(301);
            DependencyObject[] visibleBefore = ((StackPanel)overlay.CompactPreview).Children.Cast<DependencyObject>().ToArray();
            SubtitleActionAnchor[] anchorsBefore = Anchors();
            Check(ReferenceEquals(Window.GetWindow(compact), action),
                "the actual compact button belongs to the unshown action window that owns its picker");
            pending = NewPending(); before = calls.Count;
            Click(compact);
            Check(calls.Count == before + 1 && ReferenceEquals(calls[^1].Owner, action) && ReferenceEquals(calls[^1].Anchor, compact) &&
                calls[^1].Sentence.EvidenceId == compactSentence.EvidenceId && Lease() == 1 && Busy().Contains(301),
                "a compact click uses its action-window owner, selected evidence and one display lease");
            overlay.Publish(Sentence(302, 0));
            Check(overlay.HistoryEntry(302) is not null && ((StackPanel)overlay.CompactPreview).Children.Cast<DependencyObject>().SequenceEqual(visibleBefore) &&
                Anchors().SequenceEqual(anchorsBefore) && Nodes(overlay.TranscriptRoot).Contains(sameLog),
                "new translations enter history while visible compact bubbles and the pending anchor stay still");
            Click(sameLog); Click(compact);
            Check(calls.Count == before + 1 && Lease() == 1,
                "cross-window duplicate clicks cannot multiply the active compact picker or its lease");
            Complete(new(false, "Synthetic compact picker result", null, "Compact assigned label", true));
            Border newest = ((StackPanel)overlay.CompactPreview).Children.OfType<Border>().First();
            Check(Clean() && Equals(newest.Tag, 302L) && overlay.HistoryEntry(301) is { Speaker: "Compact assigned label", IsManuallyAttributed: true } &&
                Nodes(overlay.CompactPreview).OfType<TextBlock>().Any(label => label.Text == "Compact assigned label"),
                "completion refreshes newest-first compact messages, applies the label and releases every guard");

            overlay.Publish(Sentence(401, 0)); action.SetActions(Anchors());
            Button cancel = Compact(401); visibleBefore = ((StackPanel)overlay.CompactPreview).Children.Cast<DependencyObject>().ToArray();
            pending = NewPending(); before = calls.Count; Click(cancel); overlay.Publish(Sentence(402, 0));
            Check(calls.Count == before + 1 && Lease() == 1 &&
                ((StackPanel)overlay.CompactPreview).Children.Cast<DependencyObject>().SequenceEqual(visibleBefore),
                "a canceled compact selection first preserves its anchor just like a successful one");
            Complete(null);
            Check(Clean() && Equals(((StackPanel)overlay.CompactPreview).Children.OfType<Border>().First().Tag, 402L) &&
                !Field<Dictionary<long, SpeakerFeedbackResult>>(overlay, "_speakerCorrections").ContainsKey(401),
                "canceling compact selection releases its lease and immediately shows newer translations");
            Check(Field<Dictionary<long, SpeakerFeedbackResult>>(service, "_manualSpeakers").Count == 0 &&
                !File.Exists(identitiesPath) && !File.Exists(peoplePath) && identifier.ExtractorLoadAttempts == 0 && !identifier.IsModelLoaded &&
                Field<object?>(service, "_capture") is null && Field<object?>(service, "_socket") is null && !Field<bool>(overlay, "_serviceStarted"),
                "the fake picker is the only mutation boundary; the common UI route never calls reinforcement twice, runs a service or opens audio");
            Check(new[] { (Window)overlay, text, action }.All(window => window is not null && !window.IsLoaded && !window.IsVisible &&
                new WindowInteropHelper(window).Handle == 0), "every test window stays unshown and never creates a native handle");
            Check(filesBefore.All(pair => pair.Value is null ? !File.Exists(pair.Key) : File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "routing and cancellation do not save global fixture preferences, runtime configs or incoming logs");
            return checks;
        }
        finally
        {
            pending.TrySetResult(null); Drain();
            overlay.CorrectSpeakerRequested = null;
            action?.Close(); text?.Close(); overlay.Close();
            AppearanceTheme.Apply(appearance); ThemeBackground.Apply(appearance);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static TaskCompletionSource<SpeakerFeedbackResult?> NewPending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = Stopwatch.StartNew();
        while (!done())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The synthetic person picker did not release its guards.");
            Drain(); Thread.Yield();
        }
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject item in Nodes(dependency)) yield return item;
    }
}

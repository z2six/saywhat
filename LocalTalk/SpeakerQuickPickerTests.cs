using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

// Unshown picker content and fake hosts only. No popup is opened, Window shown,
// voice evidence read, person saved to disk, model loaded, or real host invoked.
internal static class SpeakerQuickPickerTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Quick speaker picker: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Read<T>(string field) => (T)typeof(MainWindow).GetField(field, flags)!.GetValue(window)!;
        Check(Read<bool>("_preview") && !window.IsVisible && !window.IsLoaded && window.Dispatcher.CheckAccess(),
            "requires an unshown inert preview on its creating STA");
        RuntimeController runtime = Read<RuntimeController>("_runtime");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "all translation tools start stopped");
        string root = Path.GetFullPath(AppPaths.Root);
        directory = Path.GetFullPath(directory);
        string? configured = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(AppPaths.IsPackagedApplication && !string.IsNullOrWhiteSpace(configured) &&
            string.Equals(root, Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Directory.GetParent(root)?.FullName, directory, StringComparison.OrdinalIgnoreCase),
            "render output belongs to the redirected desktop-test fixture");
        var settings = Read<AppSettings>("_settings");
        AppSettings original = settings.Copy();
        string settingsBefore = JsonSerializer.Serialize(settings), uiBefore = UiText.Language, pageBefore = Read<string>("_page");
        bool topmostBefore = window.Topmost;
        var files = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(root, "incoming-translator.log"), Path.Combine(root, "speaker-feedback.json") }
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        var views = new List<SpeakerPickerView>();
        TranslationDisplay sentence = new("Synthetic subtitle name", "Synthetic source", "Synthetic translation", 771,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)) { EvidenceId = "frozen-evidence-771" };
        DateTimeOffset recent = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SpeakerPerson[] people = Enumerable.Range(0, 12).Select(index => new SpeakerPerson("person-" + index,
                "Person " + index.ToString("00"), 1) { LastUsedAtUtc = recent.AddMinutes(-index) })
            .Concat(new[] { new SpeakerPerson("han", "陈小明", 2), new SpeakerPerson("japanese", "佐藤ゆき", 2),
                new SpeakerPerson("korean", "민수", 2), new SpeakerPerson("literal", "Home · Settings", 0) }).Reverse().ToArray();
        SpeakerFeedbackState State(bool acknowledged = true) => new(people, acknowledged);
        SpeakerPickerView View(FakeHost host, SpeakerFeedbackState? state, TranslationDisplay? frozen = null)
        { var view = new SpeakerPickerView(host, frozen ?? sentence, state); views.Add(view); return view; }
        string[] VisibleIds(SpeakerPickerView view) => view.PeopleList.Items.OfType<ListBoxItem>()
            .Select(item => ((SpeakerPerson)item.Tag).Id).ToArray();
        void NoMutation(FakeHost host, string detail) => Check(host.Corrections.Count == 0 && host.SaveCalls == 0 &&
            host.Acknowledgments == 0 && host.UndoCalls == 0 && host.DeleteCalls == 0, detail);
        SynchronizationContext? contextBefore = SynchronizationContext.Current;
        // Application.Run normally installs this context. The unshown test runner
        // instead pumps explicitly, so install it before exercising fake awaits.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            UiText.Language = "en";
            var browseHost = new FakeHost(State()); var browse = View(browseHost, State());
            Check(VisibleIds(browse).SequenceEqual(Enumerable.Range(0, 10).Select(index => "person-" + index)),
                "empty search shows exactly the ten most recently manually used people");
            Check(browse.PeopleList.SelectedIndex == -1 && !browse.Completion.IsCompleted,
                "initial population selects and applies nobody");
            foreach (var match in new[] { (Query: "pErSoN 11", Id: "person-11"), (Query: "小明", Id: "han"), (Query: "佐藤", Id: "japanese"), (Query: "민", Id: "korean") })
            {
                browse.Search.Text = match.Query;
                Check(VisibleIds(browse).SequenceEqual(new[] { match.Id }), "search includes all names beyond recents and non-Latin text: " + match.Query);
                Check(browse.PeopleList.SelectedIndex == -1, "filtering never silently chooses a result");
            }
            browse.Search.Text = "missing-name";
            Check(browse.PeopleList.Items.Count == 0 && browse.EmptyState.Visibility == Visibility.Visible &&
                browse.EmptyState.Text == UiText.T("No matching people."), "empty search result has explicit guidance");
            browse.Search.Text = ""; NoMutation(browseHost, "loading/filtering/clearing does not correct or learn");

            var directHost = new FakeHost(State()); var direct = View(directHost, State());
            Pump(direct.SelectAsync("person-11"));
            Check(directHost.Corrections.Count == 1 && directHost.Corrections[0] is { Evidence: "frozen-evidence-771", Person: "person-11", Special: null },
                "one explicit selection applies immediately to the frozen evidence, even outside recents");
            Check(direct.Completion.IsCompletedSuccessfully && direct.Completion.Result is { Applied: true, PersonId: "person-11" } &&
                directHost.Acknowledgments == 0 && directHost.SaveCalls == 0, "known person needs no extra Save, Apply, name write or repeated consent");
            Pump(direct.SelectAsync("person-0")); Pump(direct.ChooseSpecialAsync(SpeakerFeedbackUi.MusicLabel));
            Pump(direct.AddPersonAsync("Must not be added")); Pump(direct.UndoAsync());
            Check(directHost.Corrections.Count == 1 && directHost.SaveCalls == 0 && directHost.UndoCalls == 0 &&
                direct.Completion.Result is { PersonId: "person-11" }, "immediate completion is terminal even before the Popup's asynchronous close continuation runs");

            var gate = new TaskCompletionSource<SpeakerFeedbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var busyHost = new FakeHost(State()) { Correction = _ => gate.Task }; var busy = View(busyHost, State());
            Task pending = busy.SelectAsync("person-0");
            Check(busy.IsBusy && !busy.Search.IsEnabled && !busy.PeopleList.IsEnabled, "accepted work disables duplicate input while pending");
            Pump(busy.SelectAsync("person-1")); Pump(busy.ChooseSpecialAsync(SpeakerFeedbackUi.MusicLabel)); Pump(busy.UndoAsync());
            Check(busyHost.Corrections.Count == 1 && busyHost.UndoCalls == 0, "rapid duplicate actions cannot produce a second write");
            gate.SetResult(Result("person-0")); Pump(pending);
            Check(busy.Completion.Result is { Applied: true, PersonId: "person-0" } && !busy.IsBusy, "one accepted result settles the picker");

            var failHost = new FakeHost(State()) { Correction = _ => Task.FromException<SpeakerFeedbackResult>(new IOException("synthetic failure")) };
            var retry = View(failHost, State()); Pump(retry.SelectAsync("person-2"));
            Check(!retry.Completion.IsCompleted && !retry.IsBusy && retry.PeopleList.IsEnabled && retry.RetryButton.Visibility == Visibility.Visible &&
                retry.Status.Text == UiText.T("Couldn't save that name change. Please try again."), "failed correction retains a retryable picker and safe localized error");
            failHost.Correction = null; retry.RetryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
            Check(retry.Completion.IsCompletedSuccessfully && failHost.Corrections.Count == 2 &&
                failHost.Corrections.All(call => call.Evidence == sentence.EvidenceId && call.Person == "person-2"), "retry reuses exactly the accepted frozen choice");

            var rejectedHost = new FakeHost(State()) { Correction = _ => Task.FromResult(Result("person-3") with { Applied = false }) };
            var rejected = View(rejectedHost, State()); Pump(rejected.SelectAsync("person-3"));
            Check(!rejected.Completion.IsCompleted && rejected.RetryButton.Visibility == Visibility.Visible && rejected.PeopleList.IsEnabled,
                "backend rejection is not mislabeled as a successful applied correction");

            var consentHost = new FakeHost(State(false)); var consent = View(consentHost, State(false));
            Pump(consent.SelectAsync("person-4"));
            Check(consent.LearningPanel.Visibility == Visibility.Visible && consent.ConfirmLearningButton.IsEnabled && !consent.ConfirmLearningButton.IsDefault,
                "first person choice asks inline for explicit clear-single-voice confirmation, never default Enter consent");
            NoMutation(consentHost, "opening first-use consent has no learning or label side effects");
            Pump(consent.ConfirmLearningAsync());
            Check(consentHost.Acknowledgments == 1 && consentHost.Corrections.Count == 1 && consent.Completion.Result is { Applied: true },
                "only explicit consent acknowledges learning and applies the originally selected person");

            foreach (string special in new[] { SpeakerFeedbackUi.MusicLabel, SpeakerFeedbackUi.UnidentifiedLabel })
            {
                var host = new FakeHost(State(false)); var view = View(host, State(false)); Pump(view.ChooseSpecialAsync(special));
                Check(host.Corrections.Single() is { Person: null } call && call.Special == special &&
                    host.Acknowledgments == 0 && host.SaveCalls == 0 && view.LearningPanel.Visibility == Visibility.Collapsed,
                    "label-only category applies immediately and never teaches a person: " + special);
            }
            var undoHost = new FakeHost(State()); var undo = View(undoHost, State()); Pump(undo.UndoAsync());
            Check(undoHost.UndoCalls == 1 && undoHost.LastUndoEvidence == sentence.EvidenceId && undoHost.Corrections.Count == 0 && undo.Completion.Result is { Applied: true },
                "Undo targets exactly the same frozen sentence without a replacement correction");

            var addHost = new FakeHost(State()); var add = View(addHost, State());
            add.AddPersonButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(add.AddPersonPanel.Visibility == Visibility.Visible, "add-person editor is inline rather than a new dialog");
            NoMutation(addHost, "revealing the name editor never saves a person");
            Pump(add.AddPersonAsync("   ")); Check(addHost.SaveCalls == 0 && !add.Completion.IsCompleted, "empty names never reach the host");
            Pump(add.AddPersonAsync("  新しい人  "));
            Check(addHost.SaveCalls == 1 && addHost.SavedNames.Single() == "新しい人" && addHost.Corrections.Single().Person == "created-1" &&
                add.Completion.Result is { Applied: true }, "Add and choose trims and saves the name once, then applies its returned identity immediately");
            var addRetryHost = new FakeHost(State()) { Correction = _ => Task.FromException<SpeakerFeedbackResult>(new IOException("synthetic correction failure after name saved")) };
            var addRetry = View(addRetryHost, State()); Pump(addRetry.AddPersonAsync("Once only"));
            Check(addRetryHost.SaveCalls == 1 && addRetry.RetryButton.Visibility == Visibility.Visible && !addRetry.Completion.IsCompleted,
                "a saved name followed by correction failure remains retryable");
            addRetryHost.Correction = null; addRetry.RetryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
            Check(addRetryHost.SaveCalls == 1 && addRetryHost.Corrections.Count == 2 && addRetryHost.Corrections.All(call => call.Person == "created-1") &&
                addRetry.Completion.IsCompletedSuccessfully, "correction retry retains created identity and never attempts a duplicate name save");

            var readGate = new TaskCompletionSource<SpeakerFeedbackState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deferredHost = new FakeHost(State()) { ReadState = token => readGate.Task.WaitAsync(token) }; var deferred = View(deferredHost, null);
            Task reading = deferred.RefreshAsync(); deferred.Search.Text = "person 11"; deferred.NameInput.Text = "typed name retained";
            Check(!deferred.HasSnapshot && deferredHost.StateReads == 1 && !reading.IsCompleted, "missing cached state loads asynchronously without blocking the initial view");
            readGate.SetResult(State()); Pump(reading);
            Check(deferred.HasSnapshot && deferred.Search.Text == "person 11" && deferred.NameInput.Text == "typed name retained" &&
                VisibleIds(deferred).SequenceEqual(new[] { "person-11" }), "late snapshot preserves typed input and applies its current all-name filter");
            NoMutation(deferredHost, "deferred list population does not automatically apply the selected or filtered name");
            var loadFailHost = new FakeHost(State()) { ReadState = _ => Task.FromException<SpeakerFeedbackState>(new IOException("synthetic unreadable state")) };
            var loadFail = View(loadFailHost, null); Pump(loadFail.RefreshAsync());
            Check(!loadFail.HasSnapshot && loadFail.RetryButton.Visibility == Visibility.Visible &&
                loadFail.Status.Text == UiText.T("Couldn’t load saved people. Try again."), "unavailable state retains an actionable inline retry");

            var readCancelGate = new TaskCompletionSource<SpeakerFeedbackState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelHost = new FakeHost(State()) { ReadState = token => readCancelGate.Task.WaitAsync(token) };
            var cancelSession = CreateSession(window, cancelHost, sentence); var cancelView = SessionView(cancelSession); views.Add(cancelView);
            Task cancelRead = cancelView.RefreshAsync(); OwnerClosed(cancelSession); Pump(cancelRead);
            Check(cancelHost.LastReadToken.IsCancellationRequested && cancelView.Completion.IsCompletedSuccessfully && cancelView.Completion.Result is null,
                "owner-close path cancels pending snapshot work without a mutation or late result");
            NoMutation(cancelHost, "owner close during loading performs no feedback write");
            var committedGate = new TaskCompletionSource<SpeakerFeedbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var committedHost = new FakeHost(State()) { Correction = _ => committedGate.Task };
            var committedSession = CreateSession(window, committedHost, sentence); var committed = SessionView(committedSession); views.Add(committed);
            Task committing = committed.SelectAsync("person-5"); OwnerClosed(committedSession);
            Check(!committed.Completion.IsCompleted && !committedHost.Corrections.Single().Token.CanBeCanceled,
                "owner dismissal does not cancel or prematurely discard an already explicitly accepted write");
            committedGate.SetResult(Result("person-5")); Pump(committing);
            Check(committed.Completion.Result is { Applied: true, PersonId: "person-5" }, "successful commit racing owner close is returned rather than falsely reported as cancelled");

            var keyboardHost = new FakeHost(State()); var keyboard = View(keyboardHost, State());
            Key(keyboard.Search, System.Windows.Input.Key.Enter); NoMutation(keyboardHost, "Enter before deliberate keyboard navigation does not select the first recent person");
            Key(keyboard.Search, System.Windows.Input.Key.Down);
            Check(keyboard.PeopleList.SelectedIndex == 0 && keyboardHost.Corrections.Count == 0, "Down deliberately navigates without applying a correction");
            Key(keyboard.Search, System.Windows.Input.Key.Enter); Drain();
            Check(keyboardHost.Corrections.Single().Person == "person-0" && keyboard.Completion.IsCompletedSuccessfully,
                "Enter applies the explicitly keyboard-selected identity once");
            var escapeHost = new FakeHost(State()); var escape = View(escapeHost, State()); Key(escape, System.Windows.Input.Key.Escape);
            Check(escape.Completion.IsCompletedSuccessfully && escape.Completion.Result is null, "Escape dismisses inert popup content");
            NoMutation(escapeHost, "Escape performs no feedback operation");

            foreach (ThemePreset preset in AppearanceTheme.Presets)
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                AppSettings appearance = original.Copy(); appearance.UiThemeId = preset.Id; appearance.CustomThemeImagePath = "";
                AppearanceTheme.ApplyForTests(appearance); UiText.Language = language;
                var host = new FakeHost(State()); var view = View(host, State());
                Check(ReferenceEquals(view.Background, ModernTheme.Surface) && ReferenceEquals(view.BorderBrush, ModernTheme.Stroke) &&
                    AutomationProperties.GetName(view.Search) == UiText.T("Search people") && view.Search.Focusable,
                    "search uses the selected theme and localized accessible label: " + preset.Id + "/" + language);
                Check(Nodes(view).OfType<TextBlock>().Any(text => text.Text == UiText.T("Select a person")) &&
                    !Nodes(view).OfType<Button>().Any(button => button.Content?.ToString() is "Apply correction" or "Save person"),
                    "picker has a localized direct-choice heading and no redundant Apply step: " + language);
                view.Search.Text = "Home";
                Check(Nodes((ListBoxItem)view.PeopleList.Items[0]).OfType<TextBlock>().Any(text => text.Text == "Home · Settings"), "names remain literal even when they match interface phrases");
                view.Search.Text = "";
                foreach (Size bounds in new[] { new Size(420, 720), new Size(360, 640) })
                {
                    view.Measure(bounds); view.Arrange(new Rect(new Point(), new Size(view.DesiredSize.Width, view.DesiredSize.Height))); view.UpdateLayout();
                    Check(view.ActualWidth <= bounds.Width + .5 && view.ActualHeight <= bounds.Height + .5 && view.PeopleList.ActualHeight <= 220.5,
                        "bounded recents picker fits normal/narrow content and keeps its list scrollable: " + preset.Id + "/" + language);
                }
                if (preset.Id == "coral-night") Render(view, Path.Combine(directory, "speaker-picker-" + language + ".png"));
                NoMutation(host, "themed/localized content never loads inference or changes a name");
                view.Dismiss();
            }
            Check(window.Topmost == topmostBefore && !window.IsVisible && !window.IsLoaded && !runtime.OwnRunning && !runtime.IncomingRunning,
                "picker construction and actions never toggle owner topmost, show a window, or start translation");
        }
        finally
        {
            try
            {
                foreach (SpeakerPickerView view in views) view.Dismiss();
                AppearanceTheme.ApplyForTests(original);
                UiText.Language = uiBefore; window.Navigate(pageBefore);
            }
            finally { SynchronizationContext.SetSynchronizationContext(contextBefore); }
        }
        Check(JsonSerializer.Serialize(settings) == settingsBefore && window.Topmost == topmostBefore,
            "all real dashboard settings and pin state are preserved");
        foreach (var file in files)
            Check(file.Value is null ? !File.Exists(file.Key) : File.Exists(file.Key) && File.ReadAllBytes(file.Key).SequenceEqual(file.Value),
                "fake-host tests never rewrite " + Path.GetFileName(file.Key));
        return checks;
    }

    private static SpeakerFeedbackResult Result(string person) => new(true, "Synthetic applied result", person, "Synthetic person", true);
    private sealed record CorrectionCall(string Evidence, string? Person, string? Special, CancellationToken Token);
    private sealed class FakeHost(SpeakerFeedbackState state) : ISpeakerFeedbackHost
    {
        internal SpeakerFeedbackState State = state;
        internal Func<CancellationToken, Task<SpeakerFeedbackState>>? ReadState;
        internal Func<CorrectionCall, Task<SpeakerFeedbackResult>>? Correction;
        internal List<CorrectionCall> Corrections { get; } = new();
        internal List<string> SavedNames { get; } = new();
        internal int StateReads, SaveCalls, Acknowledgments, UndoCalls, DeleteCalls;
        internal CancellationToken LastReadToken;
        internal string? LastUndoEvidence;
        public SpeakerFeedbackState? SpeakerFeedbackSnapshot => ReadState is null ? State : null;
        public Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default)
        { StateReads++; LastReadToken = token; return ReadState?.Invoke(token) ?? Task.FromResult(State); }
        public Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default)
        {
            SaveCalls++; SavedNames.Add(name);
            if (State.People.Any(person => string.Equals(person.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Task.FromException<SpeakerPerson>(new ArgumentException("Synthetic duplicate name"));
            var person = new SpeakerPerson("created-" + SaveCalls, name, 0);
            State = State with { People = State.People.Append(person).ToArray() };
            return Task.FromResult(person);
        }
        public Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default)
        { DeleteCalls++; throw new InvalidOperationException("Picker must never delete a person"); }
        public Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default)
        { Acknowledgments++; State = State with { InstructionAcknowledged = true }; return Task.CompletedTask; }
        public Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default)
        {
            var call = new CorrectionCall(evidenceId, personId, specialLabel, token); Corrections.Add(call);
            return Correction?.Invoke(call) ?? Task.FromResult(new SpeakerFeedbackResult(personId is not null, "Synthetic applied result", personId,
                specialLabel ?? personId ?? "Synthetic person", true));
        }
        public Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default)
        { UndoCalls++; LastUndoEvidence = evidenceId; return Task.FromResult(new SpeakerFeedbackResult(false, "Synthetic undo", null, "Unidentified speaker", false)); }
    }

    private static object CreateSession(MainWindow owner, ISpeakerFeedbackHost host, TranslationDisplay sentence)
    {
        Type type = typeof(SpeakerQuickPicker).GetNestedType("PickerSession", BindingFlags.NonPublic)!;
        object session = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(Window), typeof(FrameworkElement), typeof(ISpeakerFeedbackHost), typeof(TranslationDisplay)], null)!
            .Invoke([owner, new Button { Width = 32, Height = 32 }, host, sentence]);
        var popup = (Popup)type.GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        if (popup.IsOpen || !ReferenceEquals(popup.PlacementTarget, owner) || popup.StaysOpen)
            throw new InvalidOperationException("Inert picker session must be closed and anchored to its owner, not the replaceable bubble");
        return session;
    }
    private static SpeakerPickerView SessionView(object session) => (SpeakerPickerView)session.GetType()
        .GetProperty("View", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    private static void OwnerClosed(object session) => session.GetType().GetMethod("OwnerClosed", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(session, [null, EventArgs.Empty]);
    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Synthetic picker task did not settle");
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
    private static void Key(UIElement target, System.Windows.Input.Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new InertPresentationSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args); Drain();
    }
    private sealed class InertPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static void Render(FrameworkElement view, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node) foreach (DependencyObject descendant in Nodes(node)) yield return descendant;
    }
}

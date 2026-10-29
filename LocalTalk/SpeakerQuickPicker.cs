using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IncomingTranslator;

/// <summary>A subtitle-local chooser. Opening or searching never changes a name or teaches a voice.</summary>
internal static class SpeakerQuickPicker
{
    private static readonly ConditionalWeakTable<Window, PickerSession> Active = new();

    internal static void LogFailure(string operation, Exception error)
    {
        try { DesktopLog.Write("Speaker quick picker " + operation + " failed: " + error.GetType().Name); }
        catch { /* Diagnostics must not hide the picker or prevent a correction. */ }
    }

    public static async Task<SpeakerFeedbackResult?> ShowAsync(Window owner, FrameworkElement anchor,
        ISpeakerFeedbackHost host, TranslationDisplay sentence)
    {
        if (!owner.Dispatcher.CheckAccess())
            return await owner.Dispatcher.InvokeAsync(() => ShowAsync(owner, anchor, host, sentence)).Task.Unwrap();
        if (Active.TryGetValue(owner, out PickerSession? previous)) previous.Close();
        Active.Remove(owner);
        var session = new PickerSession(owner, anchor, host, sentence);
        Active.Add(owner, session);
        // Show first: an unavailable/slow database must not look like a click did nothing.
        session.Open();
        try { return await session.View.Completion; }
        finally
        {
            session.Close();
            if (Active.TryGetValue(owner, out PickerSession? current) && ReferenceEquals(current, session))
                Active.Remove(owner);
        }
    }

    private sealed class PickerSession
    {
        private readonly Window _owner;
        private readonly Popup _popup;
        private bool _closed;
        internal SpeakerPickerView View { get; }

        internal PickerSession(Window owner, FrameworkElement anchor, ISpeakerFeedbackHost host, TranslationDisplay sentence)
        {
            _owner = owner;
            View = new(host, sentence, ReadSnapshot(host));
            Point position;
            try { position = anchor.TranslatePoint(new Point(), owner); }
            catch (InvalidOperationException) { position = new Point(16, 60); }
            // History rebuilds its bubble controls on every subtitle. Anchor to a frozen owner
            // coordinate, not to that short-lived button, so typing survives those rebuilds.
            _popup = new Popup
            {
                PlacementTarget = owner, Placement = PlacementMode.Bottom,
                PlacementRectangle = new Rect(position, new Size(Math.Max(1, anchor.ActualWidth), Math.Max(1, anchor.ActualHeight))),
                VerticalOffset = 5, AllowsTransparency = true, StaysOpen = false,
                Child = View, Focusable = true
            };
            _popup.Opened += (_, _) => View.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!_closed && _popup.IsOpen) { View.Search.Focus(); Keyboard.Focus(View.Search); }
            }));
            _popup.Closed += (_, _) => Close();
            View.DismissRequested += Close;
            _owner.Closed += OwnerClosed;
        }

        private static SpeakerFeedbackState? ReadSnapshot(ISpeakerFeedbackHost host)
        {
            try { return host.SpeakerFeedbackSnapshot; }
            // The snapshot is only an optional acceleration. A failing provider must still
            // show the picker immediately; its bounded refresh supplies visible retry UI.
            catch (Exception error) when (error is not OutOfMemoryException)
            { LogFailure("snapshot read", error); return null; }
        }

        internal void Open()
        {
            _popup.IsOpen = true;
            if (!View.HasSnapshot) _ = View.RefreshAsync();
        }

        private void OwnerClosed(object? sender, EventArgs args) => Close();
        internal void Close()
        {
            if (_closed) return;
            _closed = true;
            _owner.Closed -= OwnerClosed;
            View.DismissRequested -= Close;
            View.Dismiss();
            _popup.IsOpen = false;
        }
    }
}

/// <summary>Inert, independently testable popup content; only explicit actions call the feedback host.</summary>
internal sealed class SpeakerPickerView : Border
{
    private readonly ISpeakerFeedbackHost _host;
    private readonly string _evidenceId;
    private readonly CancellationTokenSource _readLifetime = new();
    private readonly TaskCompletionSource<SpeakerFeedbackResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StackPanel _peoplePanel = new(), _namePanel = new(), _learningPanel = new();
    private readonly Button _music, _unidentified, _saveName, _back, _decline;
    private readonly TextBlock _listHeading;
    private SpeakerFeedbackState? _state;
    private Choice? _pending;
    private Func<Task>? _retry;
    private bool _dismissed, _busy, _loading, _explicitSelection;
    private int _readGeneration;

    internal TextBox Search { get; } = new() { MinHeight = 36, Padding = new Thickness(8, 4, 8, 4), MaxLength = 128, Margin = new Thickness(0, 0, 0, 6) };
    internal ListBox PeopleList { get; } = new() { MaxHeight = 180, BorderThickness = new Thickness(0), Background = Brushes.Transparent, SelectionMode = SelectionMode.Single, Margin = new Thickness(0, 0, 0, 5) };
    internal TextBox NameInput { get; } = new() { MinHeight = 36, Padding = new Thickness(8, 4, 8, 4), MaxLength = 64, Margin = new Thickness(0, 0, 0, 6) };
    internal Button AddPersonButton { get; }
    internal Button ConfirmLearningButton { get; }
    internal Button UndoButton { get; }
    internal Button RetryButton { get; }
    internal TextBlock Status { get; } = ModernTheme.Label("", 12, ModernTheme.Warning);
    internal TextBlock EmptyState { get; } = ModernTheme.Label("", 12, ModernTheme.Muted);
    internal StackPanel LearningPanel => _learningPanel;
    internal StackPanel AddPersonPanel => _namePanel;
    internal bool IsBusy => _busy;
    internal bool HasSnapshot => _state is not null;
    internal Task<SpeakerFeedbackResult?> Completion => _completion.Task;
    private bool IsFinished => _dismissed || _completion.Task.IsCompleted;
    internal event Action? DismissRequested;

    internal SpeakerPickerView(ISpeakerFeedbackHost host, TranslationDisplay sentence, SpeakerFeedbackState? state = null)
    {
        _host = host;
        _evidenceId = sentence.EvidenceId;
        _state = state is null ? null : Copy(state);
        Width = 340; MaxWidth = 420; Padding = new Thickness(12);
        Background = ModernTheme.Surface; BorderBrush = ModernTheme.Stroke;
        BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(12);
        TextElement.SetForeground(this, ModernTheme.Text);
        TextElement.SetFontFamily(this, new FontFamily("Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic"));
        TextElement.SetFontSize(this, 13);
        var body = new StackPanel(); Child = body;
        body.Children.Add(ModernTheme.Heading("Select a person", 19));
        _peoplePanel.Children.Add(ModernTheme.Label("Search people", 12, ModernTheme.Muted));
        _peoplePanel.Children.Add(Search);
        UiLocalization.Bind(Search, System.Windows.Automation.AutomationProperties.NameProperty, "Search people");
        _listHeading = ModernTheme.Label("Recent people", 11, ModernTheme.Muted);
        _peoplePanel.Children.Add(_listHeading); _peoplePanel.Children.Add(PeopleList); _peoplePanel.Children.Add(EmptyState);
        AddPersonButton = SmallButton("Add a person", (_, _) => ShowNameEntry());
        _peoplePanel.Children.Add(AddPersonButton); body.Children.Add(_peoplePanel);

        _namePanel.Visibility = Visibility.Collapsed;
        _namePanel.Children.Add(ModernTheme.Label("New person's name", 12, ModernTheme.Muted));
        UiLocalization.Bind(NameInput, System.Windows.Automation.AutomationProperties.NameProperty, "New person's name");
        _namePanel.Children.Add(NameInput);
        var nameActions = new WrapPanel();
        _saveName = SmallButton("Add and choose", async (_, _) => await AddPersonAsync(NameInput.Text), true);
        _back = SmallButton("Back to people", (_, _) => ShowPeople());
        nameActions.Children.Add(_saveName); nameActions.Children.Add(_back);
        _namePanel.Children.Add(nameActions); body.Children.Add(_namePanel);

        _learningPanel.Visibility = Visibility.Collapsed;
        _learningPanel.Children.Add(ModernTheme.Label("For this first example, was only this person speaking clearly?", 14));
        _learningPanel.Children.Add(ModernTheme.Label("Choose a clear voice, without another person talking over them, music or a recording. A wrong example can confuse future matches. If this example is too short or unclear, the app will only update the name.", 12, ModernTheme.Muted));
        _learningPanel.Children.Add(ModernTheme.Label("When voice recognition is on, clear examples help the app recognize this person later. Music / recording and Unidentified voice never teach a voice.", 12, ModernTheme.Muted));
        ConfirmLearningButton = SmallButton("Yes, this is a clear single voice", async (_, _) => await ConfirmLearningAsync(), true);
        // Not a default button: neither opening the picker nor Enter constitutes consent.
        _decline = SmallButton("No / cancel", (_, _) => ShowPeople());
        _learningPanel.Children.Add(ConfirmLearningButton); _learningPanel.Children.Add(_decline); body.Children.Add(_learningPanel);

        var categories = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        _music = SmallButton("Music / recording · label only", async (_, _) => await ChooseSpecialAsync(SpeakerFeedbackUi.MusicLabel));
        _unidentified = SmallButton("Unidentified voice · label only", async (_, _) => await ChooseSpecialAsync(SpeakerFeedbackUi.UnidentifiedLabel));
        UndoButton = SmallButton("Undo correction", async (_, _) => await UndoAsync());
        categories.Children.Add(_music); categories.Children.Add(_unidentified); categories.Children.Add(UndoButton); body.Children.Add(categories);
        body.Children.Add(Status);
        RetryButton = SmallButton("Retry", async (_, _) => { if (_retry is not null) await _retry(); });
        RetryButton.Visibility = Visibility.Collapsed; body.Children.Add(RetryButton);
        Search.TextChanged += (_, _) => RenderPeople();
        Search.PreviewKeyDown += NavigateKeys;
        PeopleList.PreviewKeyDown += NavigateKeys;
        NameInput.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter || _namePanel.Visibility != Visibility.Visible || _busy || IsFinished) return;
            args.Handled = true; await AddPersonAsync(NameInput.Text);
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true; DismissRequested?.Invoke(); Dismiss();
        };
        RenderPeople(); UpdateControls(); UiLocalization.ApplyTree(this);
        Loaded += (_, _) => MainWindow.ApplySlimScrollbars(this);
    }

    private static Button SmallButton(string text, RoutedEventHandler action, bool primary = false)
    {
        Button button = ModernTheme.Button(text, action, primary);
        button.MinHeight = 31; button.Padding = new Thickness(10, 5, 10, 5);
        button.Margin = new Thickness(0, 3, 6, 3); return button;
    }

    private static SpeakerFeedbackState Copy(SpeakerFeedbackState state) => new(state.People.ToArray(), state.InstructionAcknowledged);

    internal async Task RefreshAsync()
    {
        if (IsFinished || _loading) return;
        int generation = ++_readGeneration; _loading = true; ClearStatus(); RenderPeople(); UpdateControls();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(_readLifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            SpeakerFeedbackState state = await _host.GetSpeakerFeedbackStateAsync(bounded.Token).WaitAsync(TimeSpan.FromSeconds(5), bounded.Token);
            if (IsFinished || generation != _readGeneration) return;
            _state = Copy(state); RenderPeople();
        }
        catch (Exception) when (IsFinished) { }
        catch (Exception error)
        {
            if (generation != _readGeneration) return;
            SpeakerQuickPicker.LogFailure("people read", error);
            SetError("Couldn’t load saved people. Try again.", RefreshAsync);
        }
        finally { _loading = false; if (!IsFinished) { RenderPeople(); UpdateControls(); } }
    }

    private void RenderPeople()
    {
        if (IsFinished) return;
        string query = Search.Text.Trim();
        IEnumerable<SpeakerPerson> people = _state?.People ?? Array.Empty<SpeakerPerson>();
        people = people.OrderByDescending(person => person.LastUsedAtUtc).ThenBy(person => person.Name, StringComparer.OrdinalIgnoreCase).ThenBy(person => person.Id, StringComparer.Ordinal);
        people = query.Length == 0 ? people.Take(10) : people.Where(person => person.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        _explicitSelection = false; PeopleList.Items.Clear();
        foreach (SpeakerPerson person in people)
        {
            var label = ModernTheme.Label(person.Name, 14, localize: false); label.Margin = new Thickness(0);
            label.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel();
            FrameworkElement avatar = PersonAvatar.Create(person, 28, ModernTheme.Selection, ModernTheme.Accent);
            avatar.Margin = new Thickness(0, 0, 9, 0); DockPanel.SetDock(avatar, Dock.Left); row.Children.Add(avatar); row.Children.Add(label);
            var item = new ListBoxItem { Content = row, Tag = person, Padding = new Thickness(8, 4, 8, 4) };
            UiLocalization.Literal(item); System.Windows.Automation.AutomationProperties.SetName(item, person.Name);
            item.PreviewMouseLeftButtonUp += async (_, args) => { args.Handled = true; await SelectAsync(person.Id); };
            PeopleList.Items.Add(item);
        }
        UiLocalization.SetText(_listHeading, query.Length == 0 ? "Recent people" : "Search people");
        string empty = _loading ? "Loading saved people…" : _state is null ? "" : query.Length == 0 ? "No saved people yet." : "No matching people.";
        UiLocalization.SetText(EmptyState, empty);
        EmptyState.Visibility = PeopleList.Items.Count == 0 && empty.Length != 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleList.Visibility = PeopleList.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void NavigateKeys(object sender, KeyEventArgs args)
    {
        if (IsFinished || _busy || _learningPanel.Visibility == Visibility.Visible) return;
        if ((args.Key is Key.Down or Key.Up) && PeopleList.Items.Count > 0)
        {
            args.Handled = true;
            int index = PeopleList.SelectedIndex;
            PeopleList.SelectedIndex = args.Key == Key.Down ? Math.Min(PeopleList.Items.Count - 1, index + 1) : index < 0 ? PeopleList.Items.Count - 1 : Math.Max(0, index - 1);
            _explicitSelection = true; PeopleList.ScrollIntoView(PeopleList.SelectedItem);
        }
        else if (args.Key == Key.Enter)
        {
            args.Handled = true;
            if (_explicitSelection && PeopleList.SelectedItem is ListBoxItem { Tag: SpeakerPerson person }) await SelectAsync(person.Id);
        }
    }

    internal Task SelectAsync(string personId)
    {
        if (IsFinished || _busy || _state?.People.FirstOrDefault(person => person.Id == personId) is not SpeakerPerson person) return Task.CompletedTask;
        return ChooseAsync(new(person.Id, null, null));
    }

    internal Task ChooseSpecialAsync(string label) => label is SpeakerFeedbackUi.MusicLabel or SpeakerFeedbackUi.UnidentifiedLabel
        ? ChooseAsync(new(null, null, label)) : Task.CompletedTask;

    internal Task AddPersonAsync(string name)
    {
        if (IsFinished || _busy) return Task.CompletedTask;
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
        { SetError("Choose a valid name of up to 64 characters."); return Task.CompletedTask; }
        return ChooseAsync(new(null, name, null));
    }

    private Task ChooseAsync(Choice choice)
    {
        if (IsFinished || _busy) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(_evidenceId))
        { SetError("This older sentence cannot be corrected. Try a newer subtitle."); return Task.CompletedTask; }
        if (choice.Special is null && _state?.InstructionAcknowledged != true)
        {
            _pending = choice; ClearStatus(); _peoplePanel.Visibility = _namePanel.Visibility = Visibility.Collapsed;
            _learningPanel.Visibility = Visibility.Visible; UpdateControls();
            _decline.Focus(); return Task.CompletedTask;
        }
        return ApplyChoiceAsync(choice, acknowledge: false);
    }

    internal Task ConfirmLearningAsync() => !IsFinished && !_busy && _pending is Choice choice
        ? ApplyChoiceAsync(choice, acknowledge: true) : Task.CompletedTask;

    private async Task ApplyChoiceAsync(Choice choice, bool acknowledge)
    {
        if (IsFinished || _busy) return;
        _busy = true; ClearStatus(); UiLocalization.SetText(Status, "Saving correction…"); UpdateControls();
        Choice effectiveChoice = choice;
        try
        {
            // Once explicitly accepted, a click outside must not cancel a disk write or lose
            // its successful result. Dismissal only prevents late changes to the closed UI.
            if (acknowledge)
            {
                await _host.AcknowledgeSpeakerLearningAsync(CancellationToken.None);
                _state = new(_state?.People ?? Array.Empty<SpeakerPerson>(), true);
            }
            string? personId = choice.PersonId;
            if (choice.NewName is not null)
            {
                SpeakerPerson person = await _host.SaveSpeakerPersonAsync(choice.NewName, token: CancellationToken.None);
                personId = person.Id;
                _state = new((_state?.People ?? Array.Empty<SpeakerPerson>()).Where(saved => saved.Id != person.Id).Append(person).ToArray(), true);
                effectiveChoice = new(personId, null, null);
            }
            SpeakerFeedbackResult result = await _host.CorrectSpeakerAsync(_evidenceId, personId, choice.Special, CancellationToken.None);
            if (result.Applied || _dismissed) _completion.TrySetResult(result);
            else SetError("Couldn't save that name change. Please try again.", () => ApplyChoiceAsync(effectiveChoice, acknowledge: false));
        }
        catch (Exception error)
        {
            SpeakerQuickPicker.LogFailure(effectiveChoice.NewName is null ? "correction save" : "person save", error);
            if (_dismissed) _completion.TrySetResult(null);
            else SetError(effectiveChoice.NewName is null ? "Couldn't save that name change. Please try again." : "Couldn’t save that person. Try another name.", effectiveChoice.NewName is null ? () => ApplyChoiceAsync(effectiveChoice, acknowledge: false) : null);
        }
        finally { _busy = false; if (!_dismissed) UpdateControls(); }
    }

    internal async Task UndoAsync()
    {
        if (IsFinished || _busy || string.IsNullOrWhiteSpace(_evidenceId)) return;
        _busy = true; ClearStatus(); UiLocalization.SetText(Status, "Undoing this sentence's correction…"); UpdateControls();
        try
        {
            SpeakerFeedbackResult result = await _host.UndoSpeakerCorrectionAsync(_evidenceId, CancellationToken.None);
            if (result.Applied || _dismissed) _completion.TrySetResult(result);
            else SetError("Couldn't undo that name change. Please try again.", UndoAsync);
        }
        catch (Exception error)
        {
            SpeakerQuickPicker.LogFailure("correction undo", error);
            if (_dismissed) _completion.TrySetResult(null);
            else SetError("Couldn't undo that name change. Please try again.", UndoAsync);
        }
        finally { _busy = false; if (!_dismissed) UpdateControls(); }
    }

    private void ShowNameEntry()
    {
        if (IsFinished || _busy) return;
        _pending = null; ClearStatus(); _learningPanel.Visibility = _peoplePanel.Visibility = Visibility.Collapsed;
        _namePanel.Visibility = Visibility.Visible; NameInput.Text = ""; NameInput.Focus(); UpdateControls();
    }

    private void ShowPeople()
    {
        if (IsFinished || _busy) return;
        _pending = null; ClearStatus(); _learningPanel.Visibility = _namePanel.Visibility = Visibility.Collapsed;
        _peoplePanel.Visibility = Visibility.Visible; Search.Focus(); UpdateControls();
    }

    private void ClearStatus()
    {
        if (IsFinished) return;
        UiLocalization.SetText(Status, ""); RetryButton.Visibility = Visibility.Collapsed; _retry = null;
    }

    private void SetError(string message, Func<Task>? retry = null)
    {
        if (IsFinished) return;
        UiLocalization.SetText(Status, message); _retry = retry;
        RetryButton.Visibility = retry is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateControls()
    {
        bool available = !_busy && !IsFinished;
        Search.IsEnabled = PeopleList.IsEnabled = NameInput.IsEnabled = available;
        AddPersonButton.IsEnabled = _saveName.IsEnabled = _back.IsEnabled = _decline.IsEnabled = available && _state is not null;
        ConfirmLearningButton.IsEnabled = available && _pending is not null;
        _music.IsEnabled = _unidentified.IsEnabled = UndoButton.IsEnabled = available && !string.IsNullOrWhiteSpace(_evidenceId);
        RetryButton.IsEnabled = available;
    }

    internal void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true; ++_readGeneration; _readLifetime.Cancel();
        if (!_busy) _completion.TrySetResult(null);
    }

    private sealed record Choice(string? PersonId, string? NewName, string? Special);
}

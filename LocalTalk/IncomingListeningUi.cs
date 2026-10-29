using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IncomingTranslator;

internal sealed record IncomingSourceChoice(string Id, string Label)
{
    public override string ToString() => UiText.T(Label);
}

internal sealed partial class MainWindow
{
    internal static bool CanChangeIncomingListening(bool busy, bool closing, bool incomingRunning) =>
        !busy && !closing && !incomingRunning;

    private bool CanChangeIncomingListening() => CanChangeIncomingListening(_busy, _closing, _runtime.IncomingRunning);

    internal static string IncomingSourceDisplayName(AppSettings settings) => IncomingListening.SourceMode(settings) switch
    {
        "application" => string.IsNullOrWhiteSpace(settings.IncomingApplicationName)
            ? UiText.T("No application selected") : settings.IncomingApplicationName,
        "desktop" => UiText.T("All playback"),
        _ => "VRChat"
    };

    private Button CreateHomeListeningButton(bool compactSource = false)
    {
        var button = ModernTheme.Button("Choose listening source", (_, _) => Navigate("Listening"));
        button.Name = "HomeListeningSourceButton";
        button.IsEnabled = CanChangeIncomingListening();
        button.ToolTip = UiText.T(button.IsEnabled ? "Choose listening source" : "Stop subtitles before changing what they listen to.");
        ToolTipService.SetShowOnDisabled(button, true);
        if (compactSource)
        {
            var source = ModernTheme.Label("", 12, ModernTheme.Muted);
            source.Name = "HomeListeningSourceLabel"; source.Margin = new Thickness(0);
            UiLocalization.SetFormat(source, "Listening to {0}", IncomingSourceDisplayName(_settings));
            var content = new DockPanel();
            var arrow = ModernTheme.Label("⌄", 14, ModernTheme.Muted, localize: false);
            arrow.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(arrow, Dock.Right);
            content.Children.Add(arrow); content.Children.Add(source);
            button.Content = content; button.MinHeight = 32; button.Padding = new Thickness(10, 5, 10, 5);
            button.Margin = new Thickness(0, 0, 0, 6); button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }
        return button;
    }

    private Border CreateListeningSettingsCard()
    {
        var section = Section("Subtitle listening", "Choose which application to hear, and whether it is one person or a group.");
        var current = ModernTheme.Label("", 13, ModernTheme.Muted);
        UiLocalization.SetFormat(current, "Listening to {0}", IncomingSourceDisplayName(_settings));
        section.Children.Add(current);
        section.Children.Add(ModernTheme.Label(
            (_settings.IncomingSourceLanguage == "Automatic" ? SubtitleSpeechText.T("Any language") : UiText.T(_settings.IncomingSourceLanguage))
            + " · " + SubtitleSpeechText.T(IncomingRecognitionCatalog.Find(_settings.IncomingSpeechEngine).Name), 12, ModernTheme.Muted, localize: false));
        var button = CreateHomeListeningButton(); button.Name = "SettingsListeningSourceButton";
        section.Children.Add(button);
        if (!CanChangeIncomingListening()) section.Children.Add(ModernTheme.Label("Stop subtitles before changing what they listen to.", 12, ModernTheme.Muted));
        return ModernTheme.Card(section);
    }

    internal StackPanel ListeningPage()
    {
        var page = Page("Choose what you hear in subtitles. Your microphone and VRChat chat settings are unchanged.");
        page.Children.Add(CreateSubtitleSpeechCard());
        var source = Section("Listen to", "Only audio played by the selected application is translated. Your microphone is not included.");
        var choices = new[] { new IncomingSourceChoice("vrchat", "VRChat"),
            new IncomingSourceChoice("application", "Another application"), new IncomingSourceChoice("desktop", "All playback") };
        string mode = IncomingListening.SourceMode(_settings);
        var picker = Select(choices, choices.First(choice => choice.Id == mode), choice =>
        {
            if (!CanChangeIncomingListening()) return;
            _settings.IncomingAudioMode = choice.Id;
            _settings.CaptureVrChatOnly = choice.Id == "vrchat";
            RefreshSaveState(); Navigate("Listening");
        });
        picker.Name = "IncomingListeningModePicker";
        source.Children.Add(Field("Audio source", picker));
        if (mode == "application")
        {
            var selected = ModernTheme.Label(IncomingSourceDisplayName(_settings), 16, localize: false);
            selected.Name = "SelectedListeningApplication"; source.Children.Add(selected);
            var choose = ModernTheme.Button("Choose application…", (_, _) => OpenListeningApplicationPicker());
            choose.Name = "ChooseListeningApplicationButton"; source.Children.Add(choose);
            source.Children.Add(ModernTheme.Label("Open the application first. Its calls, voices and other sounds may all be heard.", 12, ModernTheme.Muted));
            source.Children.Add(ModernTheme.Label("If it closes, subtitles wait for it to reopen. Other applications are never used instead.", 12, ModernTheme.Muted));
        }
        else if (mode == "desktop") source.Children.Add(ModernTheme.Label("All playback includes music, browsers and notifications. Choose an application to exclude them.", 13, ModernTheme.Warning));
        source.IsEnabled = CanChangeIncomingListening();
        page.Children.Add(ModernTheme.Card(source));
        var people = Section("Who am I listening to?", "For a one-to-one call, you can assign all subtitles to one saved person.");
        var single = Check("Just one person", _settings.IncomingSinglePerson, value =>
        {
            if (!CanChangeIncomingListening()) return;
            _settings.IncomingSinglePerson = value; RefreshSaveState(); Navigate("Listening");
        });
        single.Name = "IncomingSinglePersonToggle"; people.Children.Add(single);
        if (_settings.IncomingSinglePerson)
        {
            SpeakerPerson? person = _savedPeople.FirstOrDefault(item => item.Id == _settings.IncomingPersonId);
            var name = ModernTheme.Label(person?.Name ?? (string.IsNullOrEmpty(_settings.IncomingPersonId)
                ? UiText.T("No name selected") : UiText.T("Saved person selected")), 16, localize: false);
            name.Name = "IncomingAssignedPersonName"; people.Children.Add(name);
            var actions = new WrapPanel();
            var choose = ModernTheme.Button("Choose from People…", (_, _) => OpenListeningPersonPicker());
            choose.Name = "ChooseListeningPersonButton"; actions.Children.Add(choose);
            var clear = ModernTheme.Button("No name", (_, _) => SetListeningPerson(null));
            clear.Name = "ClearListeningPersonButton"; clear.IsEnabled = !string.IsNullOrEmpty(_settings.IncomingPersonId);
            actions.Children.Add(clear); people.Children.Add(actions);
            people.Children.Add(ModernTheme.Label("All subtitles use this name. Automatic person recognition is skipped, and this choice does not teach a voice example.", 12, ModernTheme.Muted));
        }
        else people.Children.Add(ModernTheme.Label("Use your selected subtitle profile and voice recognition settings to tell people apart.", 12, ModernTheme.Muted));
        people.IsEnabled = CanChangeIncomingListening();
        page.Children.Add(ModernTheme.Card(people));
        page.Children.Add(ModernTheme.Label(CanChangeIncomingListening()
            ? "Choose Save & apply to keep these choices, then start subtitles on Home."
            : "Stop subtitles before changing what they listen to.", 13, ModernTheme.Muted));
        var home = ModernTheme.Button("Go to Home", (_, _) => Navigate("Home")); home.Name = "ListeningHomeButton";
        page.Children.Add(home);
        if (!_preview && !_savedPeopleLoaded && !_savedPeopleLoading) _ = RefreshListeningPeopleAsync();
        return page;
    }

    private async Task RefreshListeningPeopleAsync()
    {
        await RefreshSavedPeopleAsync();
        if (!_closing && !_busy && _page == "Listening") Navigate("Listening");
    }

    internal bool SetListeningApplication(ApplicationAudioChoice application)
    {
        if (!CanChangeIncomingListening() || ApplicationAudioCatalog.CanonicalExecutable(application.Path).Length == 0) return false;
        _settings.IncomingAudioMode = "application";
        _settings.IncomingApplicationPath = application.Path;
        _settings.IncomingApplicationName = application.Name;
        _settings.CaptureVrChatOnly = false;
        RefreshSaveState();
        if (_page == "Listening") Navigate("Listening");
        return true;
    }

    internal bool SetListeningPerson(SpeakerPerson? person)
    {
        if (!CanChangeIncomingListening()) return false;
        _settings.IncomingPersonId = person?.Id ?? "";
        if (person is not null && !_savedPeople.Any(existing => existing.Id == person.Id)) _savedPeople = _savedPeople.Append(person).ToArray();
        RefreshSaveState();
        if (_page == "Listening") Navigate("Listening");
        return true;
    }

    private void OpenListeningApplicationPicker()
    {
        if (!CanChangeIncomingListening()) return;
        Window? dialog = null;
        var view = new ListeningApplicationPicker(ApplicationAudioCatalog.ListRunning, choice =>
        { if (SetListeningApplication(choice)) dialog!.DialogResult = true; });
        dialog = CreateListeningDialog("Choose application", view);
        dialog.Loaded += async (_, _) => { view.FocusSearch(); if (!_preview) await view.RefreshAsync(); };
        dialog.Closed += (_, _) => view.CancelRefresh();
        dialog.ShowDialog();
    }

    private void OpenListeningPersonPicker()
    {
        if (!CanChangeIncomingListening()) return;
        Window? dialog = null;
        var view = new ListeningPersonPicker(_savedPeople, _savedPeopleLoaded || _preview, person =>
        { if (SetListeningPerson(person)) dialog!.DialogResult = true; });
        dialog = CreateListeningDialog("Choose a person", view);
        bool closed = false;
        dialog.Loaded += async (_, _) =>
        {
            view.FocusSearch();
            if (_preview) return;
            try
            {
                await ReadSavedPeopleAsync(_lifetime.Token);
                if (!closed) view.SetPeople(_savedPeople);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            { if (!closed) view.SetError(); DesktopLog.Write("Listening person picker metadata read failed: " + error.GetType().Name); }
        };
        dialog.Closed += (_, _) => closed = true;
        dialog.ShowDialog();
    }

    private Window CreateListeningDialog(string title, FrameworkElement content)
    {
        var body = new DockPanel { Margin = new Thickness(20) };
        var close = ModernTheme.Button("Cancel", (_, _) => Window.GetWindow(body)?.Close());
        close.IsCancel = true; close.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(close, Dock.Bottom); body.Children.Add(close);
        var heading = ModernTheme.Heading(title, 24); DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
        body.Children.Add(content);
        var dialog = new Window { Owner = this, Title = UiText.T(title), Content = body,
            Width = 520, Height = 590, MinWidth = 390, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ModernTheme.Background, Foreground = ModernTheme.Text, FontFamily = FontFamily,
            Topmost = false, ShowInTaskbar = false, Icon = BrandArtwork.WindowIcon, UseLayoutRounding = true };
        dialog.SourceInitialized += (_, _) => AppearanceTheme.ApplyWindowChrome(dialog);
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { dialog.Close(); args.Handled = true; } };
        UiLocalization.ApplyTree(dialog);
        return dialog;
    }
}

// Browsing uses process metadata only. Selecting a row never starts capture or a model.
internal sealed class ListeningApplicationPicker : Grid
{
    private readonly Func<IReadOnlyList<ApplicationAudioChoice>> _read;
    private readonly Action<ApplicationAudioChoice> _choose;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = ModernTheme.Label("Open the application you want to hear, then refresh.", 12, ModernTheme.Muted);
    private IReadOnlyList<ApplicationAudioChoice> _applications = [];
    private bool _loaded, _refreshing;
    private readonly CancellationTokenSource _lifetime = new();
    internal TextBox Search { get; } = new() { Name = "ListeningApplicationSearch", MinHeight = 40, MaxLength = 100, Margin = new Thickness(0, 0, 0, 8) };
    internal Button Refresh { get; }
    internal IReadOnlyList<ApplicationAudioChoice> VisibleApplications { get; private set; } = [];

    internal ListeningApplicationPicker(Func<IReadOnlyList<ApplicationAudioChoice>> read, Action<ApplicationAudioChoice> choose)
    {
        _read = read; _choose = choose; Name = "ListeningApplicationPicker";
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        var heading = new StackPanel();
        heading.Children.Add(ModernTheme.Label("Search applications", 12, ModernTheme.Muted));
        UiLocalization.Bind(Search, AutomationProperties.NameProperty, "Search applications");
        heading.Children.Add(Search);
        Refresh = ModernTheme.Button("Refresh applications", async (_, _) => await RefreshAsync());
        Refresh.Name = "RefreshListeningApplicationsButton"; heading.Children.Add(Refresh); heading.Children.Add(_status);
        Children.Add(heading);
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); Children.Add(scroll);
        Search.TextChanged += (_, _) => Render();
        Search.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Down) { _rows.Children.OfType<Button>().FirstOrDefault()?.Focus(); args.Handled = true; }
            else if (args.Key == Key.Enter && VisibleApplications.FirstOrDefault() is { } first) { _choose(first); args.Handled = true; }
        };
        UiLocalization.ApplyTree(this);
    }

    internal void FocusSearch() { Search.Focus(); Keyboard.Focus(Search); }
    internal void CancelRefresh() => _lifetime.Cancel();
    internal void SetApplications(IReadOnlyList<ApplicationAudioChoice> applications)
    { _applications = applications.ToArray(); _loaded = true; Render(); }

    internal async Task RefreshAsync()
    {
        if (_refreshing || _lifetime.IsCancellationRequested) return;
        _refreshing = true; Refresh.IsEnabled = false; UiLocalization.SetText(_status, "Reading running applications…");
        try
        {
            var choices = await Task.Run(_read, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested) SetApplications(choices);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_lifetime.IsCancellationRequested) UiLocalization.SetText(_status, "Couldn't read applications. Try Refresh applications.");
            DesktopLog.Write("Application picker metadata read failed: " + error.GetType().Name);
        }
        finally { _refreshing = false; Refresh.IsEnabled = true; }
    }

    private void Render()
    {
        string query = Search.Text.Trim();
        VisibleApplications = _applications.Where(app => query.Length == 0 || app.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetFileName(app.Path).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _rows.Children.Clear();
        UiLocalization.SetText(_status, !_loaded ? "Open the application you want to hear, then refresh."
            : _applications.Count == 0 ? "No applications found. Open the application, then refresh."
            : VisibleApplications.Count == 0 ? "No matching applications." : "Choose the application that plays the voices you want to translate.");
        foreach (ApplicationAudioChoice app in VisibleApplications)
        {
            var copy = new StackPanel(); copy.Children.Add(ModernTheme.Label(app.Name, 15, localize: false));
            copy.Children.Add(ModernTheme.Label(System.IO.Path.GetFileName(app.Path), 11, ModernTheme.Muted, localize: false));
            var button = ModernTheme.Button("", (_, _) => _choose(app)); button.Content = copy; button.Tag = app;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Margin = new Thickness(0, 2, 0, 3);
            button.Name = "ListeningApplicationChoice"; UiLocalization.Literal(button);
            AutomationProperties.SetName(button, app.Name); _rows.Children.Add(button);
        }
    }
}

internal sealed class ListeningPersonPicker : Grid
{
    private readonly Action<SpeakerPerson?> _choose;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = ModernTheme.Label("", 12, ModernTheme.Muted);
    private IReadOnlyList<SpeakerPerson> _people;
    private bool _loaded, _failed;
    internal TextBox Search { get; } = new() { Name = "ListeningPersonSearch", MinHeight = 40, MaxLength = 100, Margin = new Thickness(0, 0, 0, 8) };
    internal IReadOnlyList<SpeakerPerson> VisiblePeople { get; private set; } = [];

    internal ListeningPersonPicker(IReadOnlyList<SpeakerPerson> people, bool loaded, Action<SpeakerPerson?> choose)
    {
        _people = people.ToArray(); _loaded = loaded; _choose = choose; Name = "ListeningPersonPicker";
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        var heading = new StackPanel(); heading.Children.Add(ModernTheme.Label("Search people", 12, ModernTheme.Muted));
        UiLocalization.Bind(Search, AutomationProperties.NameProperty, "Search people"); heading.Children.Add(Search);
        var none = ModernTheme.Button("No name", (_, _) => _choose(null)); none.Name = "ListeningNoPersonChoice";
        heading.Children.Add(none); heading.Children.Add(_status); Children.Add(heading);
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); Children.Add(scroll);
        Search.TextChanged += (_, _) => Render();
        Search.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Down) { _rows.Children.OfType<Button>().FirstOrDefault()?.Focus(); args.Handled = true; }
            else if (args.Key == Key.Enter && VisiblePeople.FirstOrDefault() is { } first) { _choose(first); args.Handled = true; }
        };
        Render(); UiLocalization.ApplyTree(this);
    }

    internal void FocusSearch() { Search.Focus(); Keyboard.Focus(Search); }
    internal void SetPeople(IReadOnlyList<SpeakerPerson> people) { _people = people.ToArray(); _loaded = true; _failed = false; Render(); }
    internal void SetError() { _failed = true; Render(); }
    private void Render()
    {
        string query = Search.Text.Trim();
        VisiblePeople = _people.Where(person => query.Length == 0 || person.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(person => person.LastUsedAtUtc).ThenBy(person => person.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        UiLocalization.SetText(_status, _failed ? "Close this chooser and refresh People, then try again."
            : !_loaded ? "Reading saved people…" : _people.Count == 0 ? "Add a person in People, or continue with no name."
            : VisiblePeople.Count == 0 ? "No matching people." : "Choose a name for this conversation.");
        _rows.Children.Clear();
        foreach (SpeakerPerson person in VisiblePeople)
        {
            var row = new DockPanel(); var photo = PersonAvatar.Create(person, 34, ModernTheme.Selection, ModernTheme.Accent);
            photo.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(photo, Dock.Left); row.Children.Add(photo);
            var name = ModernTheme.Label(person.Name, 15, localize: false); name.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(name);
            var button = ModernTheme.Button("", (_, _) => _choose(person)); button.Content = row; button.Tag = person;
            button.Name = "ListeningPersonChoice"; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 2, 0, 3); UiLocalization.Literal(button);
            AutomationProperties.SetName(button, person.Name); _rows.Children.Add(button);
        }
    }
}

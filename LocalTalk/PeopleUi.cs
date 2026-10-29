using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace IncomingTranslator;

/// <summary>Local people metadata only. Construction, browsing and filtering never start recognition.</summary>
internal sealed class PeopleListView : StackPanel
{
    private readonly StackPanel _cards = new();
    private readonly TextBlock _status = ModernTheme.Label("", 13, ModernTheme.Muted);
    private readonly TextBlock _summary = ModernTheme.Label("", 12, ModernTheme.Muted);
    private readonly Action<SpeakerPerson> _rename, _delete;
    private readonly Action<SpeakerPerson, bool> _photo;
    private IReadOnlyList<SpeakerPerson> _people = Array.Empty<SpeakerPerson>();
    private readonly bool _recognitionEnabled;
    private bool _loaded;
    private string? _error;

    internal TextBox Search { get; } = new() { Name = "PeopleSearch", MinHeight = 40, MaxLength = 64, Margin = new Thickness(0, 4, 0, 16),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    internal IReadOnlyList<SpeakerPerson> VisiblePeople { get; private set; } = Array.Empty<SpeakerPerson>();
    internal StackPanel Cards => _cards;

    internal PeopleListView(IReadOnlyList<SpeakerPerson> people, bool loaded, bool recognitionEnabled,
        Action add, Action refresh, Action<SpeakerPerson> rename, Action<SpeakerPerson> delete,
        Action<SpeakerPerson, bool> photo, string? error = null)
    {
        Name = "PeopleList";
        _recognitionEnabled = recognitionEnabled;
        _rename = rename; _delete = delete; _photo = photo;
        Children.Add(ModernTheme.Heading("Your people", 24));
        Children.Add(ModernTheme.Label("Save the people you talk to. Choose their name below a subtitle to help recognize their voice.", 13, ModernTheme.Muted));
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        var addButton = ModernTheme.Button("Add a person", (_, _) => add(), true); addButton.Name = "AddPersonButton";
        actions.Children.Add(addButton);
        var refreshButton = ModernTheme.Button("Refresh people", (_, _) => refresh()); refreshButton.Name = "RefreshPeopleButton";
        actions.Children.Add(refreshButton); Children.Add(actions);
        Children.Add(ModernTheme.Label("Search people", 12, ModernTheme.Muted));
        UiLocalization.Bind(Search, AutomationProperties.NameProperty, "Search people");
        Children.Add(Search); Children.Add(_summary); Children.Add(_status); Children.Add(_cards);
        Children.Add(ModernTheme.Label("Translation counts start with this update. Photos, names and voice examples stay on this device.", 12, ModernTheme.Muted));
        Search.TextChanged += (_, _) => Render();
        _loaded = !loaded; SetPeople(people, loaded, error);
    }

    internal void SetPeople(IReadOnlyList<SpeakerPerson> people, bool loaded = true, string? error = null)
    {
        if (_loaded == loaded && _error == error && _people.SequenceEqual(people)) return;
        _people = people.ToArray(); _loaded = loaded; _error = error; Render();
    }

    private void Render()
    {
        string query = Search.Text.Trim();
        VisiblePeople = _people.Where(person => query.Length == 0 || person.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(person => person.LastUsedAtUtc)
            .ThenBy(person => person.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _cards.Children.Clear();
        UiLocalization.SetFormat(_summary, "{0} saved people", _people.Count);
        _summary.Visibility = _loaded && _people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string? state = _error ?? (!_loaded ? "Reading saved people…" : _people.Count == 0
            ? "No saved people yet. Start by adding someone you talk to." : VisiblePeople.Count == 0 ? "No matching people." : null);
        _status.Foreground = _error is null ? ModernTheme.Muted : ModernTheme.Warning;
        _status.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        UiLocalization.SetText(_status, state ?? "");
        foreach (SpeakerPerson person in VisiblePeople) _cards.Children.Add(CreateCard(person));
        UiLocalization.ApplyTree(this);
    }

    private Border CreateCard(SpeakerPerson person)
    {
        var body = new StackPanel();
        var heading = new DockPanel { LastChildFill = true };
        FrameworkElement avatar = PersonAvatar.Create(person, 96, ModernTheme.Selection, ModernTheme.Accent);
        if (person.AvatarFileName is null)
            avatar.ToolTip = UiText.T(PersonAvatarSeed.Canonical(person.VoiceAvatarSeed) is not null
                ? "Picture generated from a saved voice example. You can choose a photo instead."
                : "Temporary picture. It will change after the first confirmed voice example.");
        avatar.Margin = new Thickness(0, 0, 14, 0); avatar.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(avatar, Dock.Left); heading.Children.Add(avatar);
        var name = ModernTheme.Label(person.Name, 20, localize: false);
        name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 0, 0, 3);
        name.TextWrapping = TextWrapping.Wrap; name.ToolTip = person.Name;
        var introduction = new StackPanel { MinWidth = 0 }; introduction.Children.Add(name);
        string familiarity = Familiarity(person, _recognitionEnabled);
        var known = ModernTheme.Label(familiarity, 12, _recognitionEnabled && person.CurrentModelConfirmedSamples > 0 ? ModernTheme.Good : ModernTheme.Muted);
        known.Margin = new Thickness(0); introduction.Children.Add(known); heading.Children.Add(introduction);
        body.Children.Add(heading);
        var numbers = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        numbers.Children.Add(Metric("Translations", person.TranslationCount));
        numbers.Children.Add(Metric("Confirmed voice examples", person.ConfirmedSamples));
        body.Children.Add(numbers);
        var reviews = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        reviews.Children.Add(Metric("Confirmed suggestions", person.ConfirmedSuggestions, compact: true));
        reviews.Children.Add(Metric("Rejected suggestions", person.RejectedSuggestions, compact: true));
        body.Children.Add(reviews);
        var reviewedSummary = ModernTheme.Label("", 12, ModernTheme.Muted);
        reviewedSummary.Name = "PersonReviewSummary";
        SetReviewedSummary(reviewedSummary, person);
        UiLocalization.Bind(reviewedSummary, ToolTipProperty, "Based only on suggestions you reviewed, not every automatic match.");
        body.Children.Add(reviewedSummary);
        if (_recognitionEnabled && person.CurrentModelConfirmedSamples is int current)
        {
            var currentExamples = ModernTheme.Label("", 12, ModernTheme.Muted);
            UiLocalization.SetFormat(currentExamples, "{0} examples with the current voice model", current);
            body.Children.Add(currentExamples);
        }
        var actions = new WrapPanel();
        var photo = ModernTheme.Button(person.AvatarFileName is null ? "Add photo…" : "Change photo…", (_, _) => _photo(person, false));
        photo.Name = "PersonPhotoButton"; actions.Children.Add(photo);
        if (person.AvatarFileName is not null)
        {
            var remove = ModernTheme.Button("Remove photo", (_, _) => _photo(person, true)); remove.Name = "RemovePersonPhotoButton"; actions.Children.Add(remove);
        }
        var rename = ModernTheme.Button("Rename", (_, _) => _rename(person)); rename.Name = "RenamePersonButton"; actions.Children.Add(rename);
        var delete = ModernTheme.Button("Delete", (_, _) => _delete(person)); delete.Name = "DeletePersonButton"; actions.Children.Add(delete);
        body.Children.Add(actions);
        var card = ModernTheme.Card(body); card.Tag = person.Id; card.Margin = new Thickness(0, 10, 0, 4);
        return card;
    }

    private static StackPanel Metric(string heading, long value, bool compact = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 28, 6), MinWidth = 120 };
        var count = ModernTheme.Label("", compact ? 18 : 23); count.Margin = new Thickness(0);
        UiLocalization.SetFormat(count, "{0:N0}", Math.Max(0, value)); panel.Children.Add(count);
        var label = ModernTheme.Label(heading, 12, ModernTheme.Muted); label.Margin = new Thickness(0); panel.Children.Add(label);
        return panel;
    }

    // Reviewed suggestions are not an accuracy estimate: unanswered guesses are never in this total.
    // Decimal addition keeps an extreme/imported pair of valid Int64 counters from overflowing.
    internal static void SetReviewedSummary(TextBlock summary, SpeakerPerson person)
    {
        long confirmed = Math.Max(0, person.ConfirmedSuggestions);
        long rejected = Math.Max(0, person.RejectedSuggestions);
        decimal reviewed = (decimal)confirmed + rejected;
        if (reviewed == 0) UiLocalization.SetText(summary, "No reviewed suggestions yet.");
        else UiLocalization.SetFormat(summary, "{0:N0} of {1:N0} reviewed suggestions confirmed", confirmed, reviewed);
    }

    // This describes evidence available to the active recognition model, not a confidence/accuracy percentage.
    internal static string Familiarity(SpeakerPerson person, bool recognitionEnabled) => !recognitionEnabled
        ? "Voice recognition is off"
        : person.CurrentModelConfirmedSamples is null ? "Start subtitles to check this voice"
        : person.CurrentModelConfirmedSamples == 0 ? person.ConfirmedSamples > 0
            ? "Needs an example for this voice model" : "No voice examples yet"
        : person.CurrentModelConfirmedSamples < 3 ? "Getting to know this voice" : "Several clear voice examples saved";
}

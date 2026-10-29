using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Inert metadata views only: no shown window, capture, inference, network or real preferences.
internal static class PeopleUiTests
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException("People UI check: " + message);
            checks++;
        }
        string originalLanguage = UiText.Language;
        var originalAppearance = SettingsStore.Load().Copy();
        string[] watched = [AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "speaker-feedback.json")];
        string?[] before = watched.Select(path => File.Exists(path) ? File.ReadAllText(path) : null).ToArray();
        int actions = 0;
        string? selected = null;
        SpeakerPerson[] people = [
            new("mira", "Mira", 4) { TranslationCount = 1234, CurrentModelConfirmedSamples = 4, LastUsedAtUtc = DateTimeOffset.UnixEpoch.AddDays(3) },
            new("kai", "Home · Settings", 2) { TranslationCount = 2, CurrentModelConfirmedSamples = 0, AvatarFileName = new string('a', 32) + ".png", LastUsedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1) },
            new("new", "새 친구 · 新朋友 · 新しい友達", 0) { CurrentModelConfirmedSamples = 0 },
            new("first", "A very long name which should wrap without hiding the photo controls", 1) { CurrentModelConfirmedSamples = 1 }
        ];
        PeopleListView Create(bool enabled = true, bool loaded = true, string? error = null) => new(people, loaded, enabled,
            () => actions++, () => actions++, person => { actions++; selected = "rename:" + person.Id; },
            person => { actions++; selected = "delete:" + person.Id; }, (person, remove) => { actions++; selected = "photo:" + person.Id + ":" + remove; }, error);
        try
        {
            Check(MainWindow.PageDisplayName("Speakers") == "People" && MainWindow.PageDisplayName("Speaker options") == "Voice recognition",
                "visible page names are personal while internal routes stay compatible");
            Check(PeopleListView.Familiarity(people[0], false) == "Voice recognition is off", "off does not claim active recognition or a score");
            Check(PeopleListView.Familiarity(people[0] with { CurrentModelConfirmedSamples = null }, true) == "Start subtitles to check this voice",
                "an unloaded model does not borrow another model's voice familiarity");
            Check(PeopleListView.Familiarity(people[1], true) == "Needs an example for this voice model",
                "examples belonging to another model are not claimed by the current model");
            Check(PeopleListView.Familiarity(people[2], true) == "No voice examples yet" && PeopleListView.Familiarity(people[3], true) == "Getting to know this voice" &&
                PeopleListView.Familiarity(people[0], true) == "Several clear voice examples saved", "familiarity describes clear examples rather than invented accuracy");
            var browse = Create();
            Check(actions == 0 && browse.VisiblePeople.Count == 4 && browse.VisiblePeople[0].Id == "mira", "building the view orders local people without any action");
            browse.Search.Text = "hOmE";
            Check(browse.VisiblePeople.Count == 1 && browse.VisiblePeople[0].Id == "kai", "search uses the complete saved list and ignores case");
            Check(Nodes(browse).OfType<TextBlock>().Any(text => text.Text == "Home · Settings"), "user names remain literal, not interface translations");
            browse.SetPeople(people.Select(person => person.Id == "kai" ? person with { TranslationCount = 5 } : person).ToArray());
            Check(browse.Search.Text == "hOmE" && browse.VisiblePeople.Single().TranslationCount == 5 && actions == 0,
                "background metadata refresh retains the search and cannot start recognition");
            browse.Search.Text = "nobody";
            Check(browse.VisiblePeople.Count == 0 && Nodes(browse).OfType<TextBlock>().Any(text => text.Text == UiText.T("No matching people.")),
                "empty search has useful feedback");
            browse.Search.Text = "Mira";
            Nodes(browse).OfType<Button>().Single(button => button.Name == "RenamePersonButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(actions == 1 && selected == "rename:mira", "rename refers to the exact immutable person ID");
            Nodes(browse).OfType<Button>().Single(button => button.Name == "PersonPhotoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(actions == 2 && selected == "photo:mira:False", "add photo is an explicit local action");
            browse.Search.Text = "Home";
            Nodes(browse).OfType<Button>().Single(button => button.Name == "RemovePersonPhotoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(actions == 3 && selected == "photo:kai:True", "remove photo targets only the selected person");
            Nodes(browse).OfType<Button>().Single(button => button.Name == "DeletePersonButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(actions == 4 && selected == "delete:kai", "delete is an explicit request, never a browsing side effect");
            int beforeLayouts = actions;
            foreach (ThemePreset preset in AppearanceTheme.Presets)
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                var appearance = originalAppearance.Copy(); appearance.UiThemeId = preset.Id; appearance.CustomThemeImagePath = "";
                AppearanceTheme.ApplyForTests(appearance); UiText.Language = language;
                var view = Create();
                Check(AutomationProperties.GetName(view.Search) == UiText.T("Search people") && view.Search.Focusable,
                    "search is keyboard accessible and localized: " + preset.Id + "/" + language);
                Check(Nodes(view).OfType<TextBlock>().Any(text => text.Text == UiText.T("Translations")) &&
                    Nodes(view).OfType<TextBlock>().Any(text => text.Text == UiText.T("Confirmed voice examples")), "both local metrics have translated labels");
                Check(Nodes(view).OfType<TextBlock>().Any(text => text.Text == "Home · Settings"), "names are preserved in " + language);
                foreach (double width in new[] { 360d, 700d })
                {
                    var scroll = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
                    Layout(scroll, new Size(width, 680));
                    Check(scroll.ScrollableWidth < 1 && view.ActualWidth <= width + .5, "people cards fit narrow layouts without horizontal scrolling");
                    Check(view.Cards.Children.OfType<Border>().All(card => card.ActualWidth <= width + .5 && card.ActualHeight > 130), "cards contain wrapped names and usable controls");
                    Check(Nodes(view).OfType<Button>().All(button => button.ActualWidth <= width + .5 && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))),
                        "photo and person controls stay accessible in " + language);
                    scroll.Content = null;
                }
                if (preset.Id == "coral-night" && language == "en" || preset.Id == "paper" && language == "ja")
                    Render(view, Path.Combine(directory, "people-" + preset.Id + "-" + language + ".png"));
            }
            Check(actions == beforeLayouts, "filtering, localization, resizing and rendering never activate the runtime");
            var empty = new PeopleListView([], true, false, () => actions++, () => actions++, _ => actions++, _ => actions++, (_, _) => actions++);
            Check(Nodes(empty).OfType<TextBlock>().Any(text => text.Text == UiText.T("No saved people yet. Start by adding someone you talk to.")), "first use offers a clear empty state");
            var loading = new PeopleListView([], false, false, () => actions++, () => actions++, _ => actions++, _ => actions++, (_, _) => actions++);
            Check(Nodes(loading).OfType<TextBlock>().Any(text => text.Text == UiText.T("Reading saved people…")), "initial metadata read is visible even with no people");
            Check(watched.Select((path, index) => (File.Exists(path) ? File.ReadAllText(path) : null) == before[index]).All(value => value),
                "inert people controls leave preferences, translation settings and saved voices untouched");
        }
        finally { UiText.Language = originalLanguage; AppearanceTheme.ApplyForTests(originalAppearance); }
        checks += PersonPhotoEditorTests.Run(Path.Combine(directory, "photo-editor"));
        return checks;
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency) foreach (DependencyObject descendant in Nodes(dependency)) yield return descendant;
    }
    private static void Layout(FrameworkElement element, Size size)
    { element.Measure(size); element.Arrange(new Rect(new Point(), size)); element.UpdateLayout(); }
    private static void Render(PeopleListView view, string path)
    {
        var scroll = new ScrollViewer { Content = view, Background = ModernTheme.Background, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false, Padding = new Thickness(24) };
        Layout(scroll, new Size(700, 820));
        var bitmap = new RenderTargetBitmap(700, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(scroll);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output); scroll.Content = null;
    }
}

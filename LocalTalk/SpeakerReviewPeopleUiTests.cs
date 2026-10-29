using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Inert metadata and help controls only. These tests do not show a window, capture
// audio, start a model, contact a service, or write any real person's information.
internal static class SpeakerReviewPeopleUiTests
{
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool passed, string detail)
        {
            if (!passed) throw new InvalidOperationException("Reviewed person UI: " + detail);
            checks++;
        }
        string languageBefore = UiText.Language;
        var appearanceBefore = SettingsStore.Load().Copy();
        string[] watched = [AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "speaker-feedback.json")];
        byte[]?[] before = watched.Select(path => File.Exists(path) ? File.ReadAllBytes(path) : null).ToArray();
        int actions = 0;
        SpeakerPerson[] people = [
            new("ringo-review-fixture", "Ringo · リンゴ", 4)
            {
                ConfirmedSuggestions = 18, RejectedSuggestions = 5, TranslationCount = 600,
                CurrentModelConfirmedSamples = 4, VoiceAvatarSeed = new string('a', 64)
            },
            new("fresh-review-fixture", "Fresh friend", 0) { TranslationCount = 500, CurrentModelConfirmedSamples = 0 }
        ];
        PeopleListView Create(bool enabled = true) => new(people, true, enabled,
            () => actions++, () => actions++, _ => actions++, _ => actions++, (_, _) => actions++);
        try
        {
            string[] keys = ["Confirmed suggestions", "Rejected suggestions", "No reviewed suggestions yet.",
                "{0:N0} of {1:N0} reviewed suggestions confirmed",
                "Based only on suggestions you reviewed, not every automatic match.",
                "Maybe {0}", "Confirm this person", "Not this person", "Reviewing this suggestion…",
                "Couldn't review that suggestion. Please try again.", "This suggestion changed. Review the latest name.",
                "Wrong match remembered · voice example saved", "Marked as unknown · no voice example saved",
                "Person updated; a clear voice example was saved and the wrong suggestion was remembered.",
                "This suggestion was already reviewed.",
                "This suggestion is no longer available for review. Choose a person manually instead.",
                "Label corrected only. The bounded voice collection is full; no additional example was stored. Existing examples were preserved.",
                "Suggestion rejected; this clear voice example was remembered as not {0}.",
                "Review a suggested name",
                "Maybe means the name is only a suggestion. ✓ confirms it; ✕ changes it to Unknown person. Guesses never teach themselves.",
                "Clear, reviewed voice examples help remember who someone is—or who they are not. Unclear or mixed audio may only change the name. You can undo a review.",
                "Your choices improve saved voice examples, not the AI model itself."];
            foreach (string key in keys)
            {
                Check(UiText.Contains(key), "new review text has an interface key: " + key);
                foreach (string language in new[] { "zh-CN", "ja", "ko" })
                {
                    UiText.Language = language;
                    Check(UiText.T(key) != key, "review text translated in " + language + ": " + key);
                }
            }
            UiText.Language = "en";
            var view = Create();
            TextBlock Summary(string personId) => Nodes(view.Cards.Children.OfType<Border>().Single(card => card.Tag?.ToString() == personId))
                .OfType<TextBlock>().Single(text => text.Name == "PersonReviewSummary");
            Check(Summary(people[0].Id).Text == "18 of 23 reviewed suggestions confirmed", "only explicit reviews enter the reported fraction");
            Check(Summary(people[1].Id).Text == "No reviewed suggestions yet.", "old translation counts never become invented confirmation history");
            Check(!Nodes(view).OfType<TextBlock>().Any(text => text.Text.Contains('%')), "the page does not claim overall recognition accuracy");
            Check(ReferenceEquals(GeneratedPersonAvatar.Resolve(people[0]), GeneratedPersonAvatar.Resolve(people[0] with
                { ConfirmedSuggestions = 100, RejectedSuggestions = 100 })), "reviews never replace a stable voice-derived picture");
            foreach (string language in new[] { "zh-CN", "ja", "ko", "en" })
            {
                UiText.Language = language;
                Check(Summary(people[0].Id).Text == UiText.Format("{0:N0} of {1:N0} reviewed suggestions confirmed", 18L, 23m),
                    "review summary follows language changes without leaving People: " + language);
                Check(Summary(people[0].Id).ToolTip?.ToString() == UiText.T("Based only on suggestions you reviewed, not every automatic match."),
                    "review scope hint follows language changes: " + language);
                Check(Nodes(view).OfType<TextBlock>().Any(text => text.Text == "Ringo · リンゴ"), "a person's own name stays literal");
            }
            var summary = new TextBlock();
            PeopleListView.SetReviewedSummary(summary, people[0] with { ConfirmedSuggestions = -3, RejectedSuggestions = -7 });
            Check(summary.Text == "No reviewed suggestions yet.", "invalid negative imported values are never displayed as counts");
            PeopleListView.SetReviewedSummary(summary, people[0] with { ConfirmedSuggestions = long.MaxValue, RejectedSuggestions = long.MaxValue });
            Check(summary.Text == UiText.Format("{0:N0} of {1:N0} reviewed suggestions confirmed", long.MaxValue, (decimal)long.MaxValue * 2),
                "large reviewed counts do not overflow or turn negative");
            foreach (ThemePreset preset in AppearanceTheme.Presets)
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                var appearance = appearanceBefore.Copy(); appearance.UiThemeId = preset.Id; appearance.CustomThemeImagePath = "";
                AppearanceTheme.ApplyForTests(appearance); UiText.Language = language;
                var themed = Create();
                foreach (double width in new[] { 360d, 700d })
                {
                    var scroll = new ScrollViewer { Content = themed, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
                    Layout(scroll, new Size(width, 800));
                    Check(scroll.ScrollableWidth < 1 && themed.ActualWidth <= width + .5,
                        "review metrics wrap within the viewport: " + preset.Id + "/" + language + "/" + width.ToString(CultureInfo.InvariantCulture));
                    scroll.Content = null;
                }
                if (preset.Id == "coral-night" && language == "en" || preset.Id == "paper" && language == "ja")
                    Render(themed, Path.Combine(directory, "reviewed-people-" + preset.Id + "-" + language + ".png"));
            }
            var disabled = Create(false);
            Check(Nodes(disabled).OfType<TextBlock>().Any(text => text.Text == UiText.T("Confirmed suggestions")),
                "saved review history remains visible with recognition turned off");
            var guide = new SubtitleQuickGuide(UiText.T, _ => actions++, () => actions++);
            guide.ShowGuide();
            foreach (string key in keys.SkipWhile(key => key != "Review a suggested name"))
                Check(Nodes(guide).OfType<TextBlock>().Any(text => text.Text == UiText.T(key)), "quick guide explains review behavior: " + key);
            guide.CloseGuide();
            Check(actions == 0, "browsing stats, changing language, and reading help never activate or modify recognition");
            Check(watched.Select((path, index) => before[index] is null ? !File.Exists(path)
                : File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(before[index]!)).All(result => result),
                "new stats/help controls leave all existing settings and saved voice data untouched");
        }
        finally { UiText.Language = languageBefore; AppearanceTheme.ApplyForTests(appearanceBefore); }
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
        Layout(scroll, new Size(700, 880));
        var bitmap = new RenderTargetBitmap(700, 880, 96, 96, PixelFormats.Pbgra32); bitmap.Render(scroll);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output); scroll.Content = null;
    }
}

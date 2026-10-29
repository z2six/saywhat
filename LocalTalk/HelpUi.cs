using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace IncomingTranslator;

internal sealed record HelpTopic(string Id, string Heading, string Description, params string[] Pages);

/// <summary>Reading help and following its links only navigates; it never starts a tool.</summary>
internal static class HelpUi
{
    internal const string Introduction = "Choose what you want to do, or find help with a problem. These links open the relevant settings; translation starts only from Home.";
    internal static readonly IReadOnlyList<HelpTopic> Topics = Array.AsReadOnly(new[]
    {
        new HelpTopic("getting-started", "Set up translation",
            "Choose the language you speak and the languages your friends should read, then prepare the recommended models. On Home, press Start to translate your microphone into VRChat chat. Subtitles and People are optional and can be prepared later. To repeat the setup, choose Run setup again at the top of Settings.", "Home", "Settings"),
        new HelpTopic("models", "Choose speech and translation models",
            "On Home, choose speech and translation model cards, then Apply changes or Download & apply. Switching may briefly pause translation; the app handles that. When stopped, Apply & start can save choices and start your voice. Speech models turn voices into words; translators change their language. No separate translation app is required.", "Home", "Models", "Translation"),
        new HelpTopic("slow", "Translation feels slow",
            "For your own voice, try fewer output languages, a shorter finishing pause or optional early messages, then Apply changes. Smaller models use less memory but are not always faster. Stop optional subtitles if you do not need them. Quick skips person recognition; Groups adds waiting. Diagnostics shows which stage takes time.", "Home", "Audio & timing", "Settings", "Diagnostics"),
        new HelpTopic("timing", "Messages arrive too late or split too soon",
            "Choose your finishing pause and early-message option on Home, then Apply changes. The default pause is 1.2 seconds: shorter sends sooner; longer keeps more context. Early messages can be revised while you speak; final-only waits for a finished sentence. Neither mode opens VRChat's keyboard. Recognition and translation still add time.", "Home", "Audio & timing"),
        new HelpTopic("chat", "My voice does not appear in VRChat chat",
            "Enable OSC → Enabled in VRChat's Action Menu and start your voice on Home. Pause when my VRChat mic is muted is on by default. To change it, edit the Home checkbox and select Apply changes. Unmute to capture when it is on; turning it off permits translated chat while VRChat is muted. When pausing for mute, an already-captured sentence can still finish and send if that option is enabled. Also check VRChat's chatbox visibility and safety settings.", "Home", "Audio & timing", "Diagnostics"),
        new HelpTopic("subtitles", "I cannot see subtitles from other people",
            "Choose the listening source and subtitle languages on Home. Search for an application in the listening source picker, then Apply subtitle choices and Start subtitles. Home's person-recognition toggle is staged with these choices too. Check the selected application's volume and the Windows mixer. If it closes, subtitles wait for it; other applications are not substituted. Closing the overlay stops subtitles.", "Home", "Listening", "Diagnostics"),
        new HelpTopic("chinese-subtitles", "Improve Chinese subtitles",
            "On Home, find Optional · live subtitles. Under They speak, choose Chinese, then FireRedASR2 · Fast or FireRedASR2 · Accurate. Choose Download & apply, or Apply subtitle choices if the files are ready. Try Fast first; Accurate needs more processing. For Any language, choose Nemotron. Languages, translators, listening source and sentence timing are all on this card. These choices affect subtitles, not your microphone translator.", "Home", "Listening"),
        new HelpTopic("accuracy", "The translation uses the wrong words",
            "Check I speak and translation languages on Home, then Apply changes. Compare recognized words with the translation: wrong source words need better recognition; poor wording may need another translator or a glossary. Selected models must support the chosen languages. Music and overlapping voices can reduce accuracy.", "Home", "Languages", "Translation", "Models"),
        new HelpTopic("profiles", "Choose a translation profile",
            "These profiles mainly change optional subtitles. Quick skips automatic person recognition. Normal can suggest familiar people without delaying subtitles for a name. Groups tries to recognize separate voices when people talk together, but needs another local download, more memory and extra time. Switch Normal and Groups above subtitles; choose Quick or save custom settings at the top of Settings. You do not need a subtitle profile to start translating your microphone.", "Settings", "Home"),
        new HelpTopic("speakers", "People's names are missing or incorrect",
            "Save people and their photos in People. Automatic person recognition is optional. Maybe marks an unconfirmed name: ✓ confirms it, and ✕ marks it as Unknown person. Guesses never teach themselves. Clear examples you review help remember who someone is—or who they are not. Unclear or mixed speech only changes the message's name. Choose another name with Select a person, or undo a review from the same message. People shows confirmed and rejected suggestions you reviewed, not an overall accuracy score. Saved voice examples help with names, not with separating overlapping words. Groups attempts that separately. Music and recordings can cause mistaken names.", "Speakers", "Speaker options"),
        new HelpTopic("privacy", "Keep conversations private when asking for help",
            "Speech recognition and translation run on your PC. Saved people and voice examples stay here. Setup downloads models and tools, but does not upload microphone recordings. Logs can contain spoken or translated text; review them and remove private conversation text before sharing.", "Diagnostics", "Settings"),
        new HelpTopic("updates", "Update or uninstall while keeping my files",
            "Check for updates in Settings. The update installer keeps your settings and models. The standalone installer also offers repair and uninstall; review its data choices before continuing. Keep downloaded models and personal data unless you intend to remove them.", "Settings"),
        new HelpTopic("appearance", "Change the interface or subtitle window",
            "Use the sidebar's Interface language selector to change the app language without changing translation languages. Choose a light, dark or custom appearance in Settings → Themes. Move or resize subtitles, and use their Always on top button to keep them visible. Closing the main window can quit or keep translation in the tray.", "Themes", "Settings", "Audio & timing")
    });

    internal static StackPanel Build(Action<string> navigate)
    {
        var page = new StackPanel { Name = "HelpPage" };
        var introduction = ModernTheme.Label(Introduction, 14, ModernTheme.Muted);
        introduction.Margin = new Thickness(0, 0, 0, 20);
        page.Children.Add(introduction);
        foreach (HelpTopic topic in Topics)
        {
            var section = new StackPanel();
            var heading = ModernTheme.Heading(topic.Heading, 22);
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            section.Children.Add(heading);
            section.Children.Add(ModernTheme.Label(topic.Description, 14, ModernTheme.Muted));
            var links = new WrapPanel();
            foreach (string target in topic.Pages)
            {
                var link = ModernTheme.Button(MainWindow.PageDisplayName(target), (_, _) => navigate(target));
                link.Tag = target;
                link.Name = "HelpLink_" + topic.Id.Replace('-', '_') + "_" + target.Replace(" & ", "_").Replace(' ', '_');
                links.Children.Add(link);
            }
            section.Children.Add(links);
            page.Children.Add(ModernTheme.Card(section));
        }
        return page;
    }
}

internal enum IntroductionChoice { Dismiss, Setup, Help }

internal static class FirstRunIntroductionUi
{
    internal const string Title = "Welcome to SayWhat?";
    internal const string Description = "Translate your microphone into VRChat chat. Start with your own voice; subtitles are optional.";
    internal const string PrepareTitle = "Prepare once";
    internal const string PrepareDescription = "Keep the recommended models or choose another card. Setup downloads what your voice needs. Later, choose model cards on Home and apply your changes.";
    internal const string LanguagesDescription = "Choose what you speak and one to four languages for VRChat chat. More languages share the same short message. Later changes on Home wait until you apply them.";
    internal const string StartTitle = "Start when you are ready";
    internal const string StartDescription = "Enable OSC in VRChat, then press Start on Home. Apply & start can save pending voice choices and begin translation. Subtitles have their own Start button; Stop all releases model memory.";
    internal const string Reassurance = "Opening this guide keeps translation stopped. You can return to Help from the sidebar at any time.";

    internal static Window Create(Window owner)
    {
        var dialog = new Window
        {
            Name = "FirstRunIntroductionDialog", Owner = owner, Width = 600, Height = 660,
            MinWidth = 440, MinHeight = 420, MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 48),
            Background = ModernTheme.Background, Foreground = ModernTheme.Text,
            FontFamily = owner.FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false, Topmost = false,
            UseLayoutRounding = true, SnapsToDevicePixels = true, Tag = IntroductionChoice.Dismiss
        };
        UiLocalization.Bind(dialog, Window.TitleProperty, Title);
        dialog.SourceInitialized += (_, _) => AppearanceTheme.ApplyWindowChrome(dialog);
        var frame = new Grid { Background = ModernTheme.Background };
        frame.RowDefinitions.Add(new RowDefinition());
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var artwork = ThemeBackground.CreateLayer(); Grid.SetRowSpan(artwork, 2); frame.Children.Add(artwork);
        var content = new StackPanel { Margin = new Thickness(28, 28, 28, 12) };
        content.Children.Add(ModernTheme.Heading(Title, 30));
        content.Children.Add(ModernTheme.Label(Description, 15, ModernTheme.Muted));
        var steps = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        AddStep("Choose your languages", LanguagesDescription);
        AddStep(PrepareTitle, PrepareDescription);
        AddStep(StartTitle, StartDescription);
        content.Children.Add(ModernTheme.Card(steps));
        content.Children.Add(ModernTheme.Label(Reassurance, 13, ModernTheme.Muted));
        var actions = new WrapPanel { Margin = new Thickness(28, 0, 28, 20), Background = ModernTheme.Background };
        AddAction("Set up", IntroductionChoice.Setup, primary: true);
        AddAction("Help", IntroductionChoice.Help);
        AddAction("Got it", IntroductionChoice.Dismiss, cancel: true);
        Grid.SetRow(actions, 1); frame.Children.Add(actions);
        frame.Children.Add(new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Style = (Style)Application.Current.FindResource(typeof(ScrollViewer))
        });
        dialog.Content = frame;
        UiLocalization.ApplyTree(dialog);
        return dialog;

        void AddStep(string title, string description)
        {
            var heading = ModernTheme.Heading(title, 19);
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            steps.Children.Add(heading);
            var detail = ModernTheme.Label(description, 13, ModernTheme.Muted);
            detail.Margin = new Thickness(0, 0, 0, 16); steps.Children.Add(detail);
        }
        void AddAction(string label, IntroductionChoice choice, bool primary = false, bool cancel = false)
        {
            var button = ModernTheme.Button(label, (_, _) => { dialog.Tag = choice; dialog.Close(); }, primary);
            button.Name = "Introduction" + choice + "Button"; button.IsCancel = cancel;
            actions.Children.Add(button);
        }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace IncomingTranslator;

internal enum SubtitleQuickGuideMode { Hidden, Prompt, Guide }

// A lightweight card inside the subtitle window. It never owns a window, captures
// audio, loads a model, or changes recognition settings. The injected save callback
// records only whether the user answered the first-use question.
internal sealed class SubtitleQuickGuide : Border
{
    private readonly Func<string, string> _translate;
    private readonly Action<bool> _savePromptSeen;
    private readonly Action _openSpeakerSettings;
    private readonly Action<Exception>? _saveFailed;
    private readonly TextBlock _title;
    private readonly StackPanel _body;
    private readonly ScrollViewer _scroll;
    private readonly Button _primary;
    private readonly Button _secondary;
    private readonly List<(TextBlock Block, string English)> _copy = new();
    private bool _promptOffered;

    internal SubtitleQuickGuideMode Mode { get; private set; }
    internal event Action? Dismissed;

    internal static IReadOnlyList<string> Phrases { get; } = Array.AsReadOnly(new[]
    {
        "Quick subtitle guide",
        "Want a quick subtitle guide?",
        "See how to read translations and help the app recognize people.",
        "Show guide", "Not now",
        "Read translated voices",
        "Other people's speech appears here as translated chat bubbles. New messages are added below.",
        "Choose what to listen to",
        "On Home, choose the subtitle listening source: VRChat or another application. Only what that application plays is translated; your microphone settings stay separate.",
        "For a one-to-one call, choose Just one person and optionally pick someone from People. Subtitles use that name without guessing or learning their voice.",
        "Choose a subtitle profile",
        "Normal keeps subtitles responsive. Groups tries to recognize separate voices when people talk together, but adds a delay. Switch between them above the subtitles.",
        "Quick is for one-to-one conversations and turns automatic person recognition off. Choose it in Settings → Translation profiles. You can save your own settings there too.",
        "Groups downloads an extra local speech model the first time you choose it. Switching briefly pauses subtitles; your saved people and earlier messages are kept.",
        "Tell voices apart",
        "If Voice recognition is on, local AI tries to tell people apart by their voices. It can make mistakes, especially with songs or people talking together.",
        "Saved voice examples help identify names. They do not separate overlapping speech; Groups tries to recognize those words separately.",
        "Review a suggested name",
        "Maybe means the name is only a suggestion. ✓ confirms it; ✕ changes it to Unknown person. Guesses never teach themselves.",
        "Clear, reviewed voice examples help remember who someone is—or who they are not. Unclear or mixed audio may only change the name. You can undo a review.",
        "Your choices improve saved voice examples, not the AI model itself.",
        "Name a person",
        "Choose Select a person below a message, then pick or add their name. Use a clear example with only that person speaking. When recognition is on, this helps the app recognize them next time.",
        "Saved names and voice examples stay on your device. They aren't uploaded.",
        "You can turn Voice recognition on or off in Settings. Translations still work when it's off.",
        "Open voice settings", "Close"
    });

    internal SubtitleQuickGuide(Func<string, string> translate, Action<bool> savePromptSeen,
        Action openSpeakerSettings, Action<Exception>? saveFailed = null)
    {
        _translate = translate;
        _savePromptSeen = savePromptSeen;
        _openSpeakerSettings = openSpeakerSettings;
        _saveFailed = saveFailed;
        Name = "SubtitleQuickGuide";
        Background = AppearanceTheme.Surface;
        BorderBrush = AppearanceTheme.Accent;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(14, 12, 14, 10);
        Margin = new Thickness(0, 10, 0, 0);
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _title = new TextBlock { Name = "SubtitleGuideTitle", Foreground = AppearanceTheme.Text,
            FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8) };
        _body = new StackPanel();
        _scroll = new ScrollViewer { Name = "SubtitleGuideScroll", Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly, Focusable = false };
        var actions = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        _primary = MakeButton("SubtitleGuidePrimary");
        _primary.Margin = new Thickness(0, 0, 8, 0);
        _primary.BorderBrush = AppearanceTheme.Accent;
        _secondary = MakeButton("SubtitleGuideSecondary");
        _primary.Click += (_, _) =>
        {
            if (Mode == SubtitleQuickGuideMode.Prompt) AnswerPrompt(true);
            else if (Mode == SubtitleQuickGuideMode.Guide)
            {
                CloseGuide();
                _openSpeakerSettings();
            }
        };
        _secondary.Click += (_, _) => Dismiss();
        Grid.SetColumn(_secondary, 1);
        actions.Children.Add(_primary); actions.Children.Add(_secondary);
        Grid.SetRow(_scroll, 1); Grid.SetRow(actions, 2);
        layout.Children.Add(_title); layout.Children.Add(_scroll); layout.Children.Add(actions);
        Child = layout;
    }

    private static Button MakeButton(string name) => new()
    {
        Name = name, MinHeight = 32, Padding = new Thickness(8, 5, 8, 5), Cursor = Cursors.Hand,
        Background = AppearanceTheme.Raised, Foreground = AppearanceTheme.Text,
        BorderBrush = AppearanceTheme.Stroke, BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    internal bool ShowPromptIfNeeded(bool promptSeen)
    {
        if (promptSeen || _promptOffered || Mode != SubtitleQuickGuideMode.Hidden) return false;
        _promptOffered = true;
        Show(SubtitleQuickGuideMode.Prompt);
        return true;
    }

    internal void ShowGuide()
    {
        if (Mode == SubtitleQuickGuideMode.Prompt) AnswerPrompt(true);
        else Show(SubtitleQuickGuideMode.Guide);
    }

    // Escape is the same as choosing Not now when the first-use prompt is visible.
    // A closed guide remains independently accessible through the header '?' button.
    internal void Dismiss()
    {
        if (Mode == SubtitleQuickGuideMode.Prompt) AnswerPrompt(false);
        else CloseGuide();
    }

    internal void CloseGuide()
    {
        if (Mode == SubtitleQuickGuideMode.Hidden) return;
        Mode = SubtitleQuickGuideMode.Hidden;
        Visibility = Visibility.Collapsed;
        Dismissed?.Invoke();
    }

    private void AnswerPrompt(bool showGuide)
    {
        if (Mode != SubtitleQuickGuideMode.Prompt) return;
        // Keep this session usable even if its tiny preference cannot be saved.
        // Next launch may offer the guide again; no app/model settings are rewritten.
        try { _savePromptSeen(true); }
        catch (Exception error)
        {
            try { _saveFailed?.Invoke(error); }
            catch { /* A diagnostic callback cannot break the subtitles. */ }
        }
        if (showGuide) Show(SubtitleQuickGuideMode.Guide);
        else CloseGuide();
    }

    private void Show(SubtitleQuickGuideMode mode)
    {
        bool changed = Mode != mode;
        Mode = mode;
        Visibility = Visibility.Visible;
        if (changed)
        {
            _copy.Clear(); _body.Children.Clear();
            if (mode == SubtitleQuickGuideMode.Prompt)
                AddCopy("See how to read translations and help the app recognize people.");
            else
            {
                AddCopy("Read translated voices", heading: true);
                AddCopy("Other people's speech appears here as translated chat bubbles. New messages are added below.");
                AddCopy("Choose what to listen to", heading: true);
                AddCopy("On Home, choose the subtitle listening source: VRChat or another application. Only what that application plays is translated; your microphone settings stay separate.");
                AddCopy("For a one-to-one call, choose Just one person and optionally pick someone from People. Subtitles use that name without guessing or learning their voice.");
                AddCopy("Choose a subtitle profile", heading: true);
                AddCopy("Normal keeps subtitles responsive. Groups tries to recognize separate voices when people talk together, but adds a delay. Switch between them above the subtitles.");
                AddCopy("Quick is for one-to-one conversations and turns automatic person recognition off. Choose it in Settings → Translation profiles. You can save your own settings there too.");
                AddCopy("Groups downloads an extra local speech model the first time you choose it. Switching briefly pauses subtitles; your saved people and earlier messages are kept.");
                AddCopy("Tell voices apart", heading: true);
                AddCopy("If Voice recognition is on, local AI tries to tell people apart by their voices. It can make mistakes, especially with songs or people talking together.");
                AddCopy("Saved voice examples help identify names. They do not separate overlapping speech; Groups tries to recognize those words separately.");
                AddCopy("Review a suggested name", heading: true);
                AddCopy("Maybe means the name is only a suggestion. ✓ confirms it; ✕ changes it to Unknown person. Guesses never teach themselves.");
                AddCopy("Clear, reviewed voice examples help remember who someone is—or who they are not. Unclear or mixed audio may only change the name. You can undo a review.");
                AddCopy("Your choices improve saved voice examples, not the AI model itself.");
                AddCopy("Name a person", heading: true);
                AddCopy("Choose Select a person below a message, then pick or add their name. Use a clear example with only that person speaking. When recognition is on, this helps the app recognize them next time.");
                AddCopy("Saved names and voice examples stay on your device. They aren't uploaded.");
                AddCopy("You can turn Voice recognition on or off in Settings. Translations still work when it's off.");
            }
            _scroll.ScrollToTop();
        }
        RefreshLanguage();
        _primary.Focus();
    }

    private void AddCopy(string english, bool heading = false)
    {
        var text = new TextBlock { Foreground = heading ? AppearanceTheme.Text : AppearanceTheme.Muted,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = heading ? 12 : 11, LineHeight = 17, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, heading ? 6 : 0, 8, heading ? 3 : 8) };
        _copy.Add((text, english)); _body.Children.Add(text);
    }

    internal void RefreshLanguage()
    {
        _title.Text = _translate(Mode == SubtitleQuickGuideMode.Prompt ? "Want a quick subtitle guide?" : "Quick subtitle guide");
        foreach ((TextBlock block, string english) in _copy) block.Text = _translate(english);
        SetButton(_primary, Mode == SubtitleQuickGuideMode.Prompt ? "Show guide" : "Open voice settings");
        SetButton(_secondary, Mode == SubtitleQuickGuideMode.Prompt ? "Not now" : "Close");
        AutomationProperties.SetName(this, _title.Text);
    }

    private void SetButton(Button button, string english)
    {
        string translated = _translate(english);
        button.Content = new TextBlock { Text = translated, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Foreground = AppearanceTheme.Text, FontSize = 11 };
        button.ToolTip = translated;
        AutomationProperties.SetName(button, translated);
    }
}

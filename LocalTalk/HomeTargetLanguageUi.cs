using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IncomingTranslator;

internal sealed partial class MainWindow
{
    private Popup? _homeTargetLanguagePopup;

    internal static bool CanChangeHomeTargetLanguage(bool incoming, bool busy, bool closing, bool ownRunning, bool incomingRunning) =>
        !busy && !closing && !(incoming ? incomingRunning : ownRunning);

    private bool CanChangeHomeTargetLanguage(bool incoming) => CanChangeHomeTargetLanguage(incoming, _busy, _closing,
        _runtime.OwnRunning, _runtime.IncomingRunning);

    private Button CreateHomeTargetLanguageButton(bool incoming)
    {
        string targets = incoming ? UiText.T(_settings.IncomingTargetLanguage)
            : string.Join(" + ", _settings.EffectiveOwnVoiceTargetLanguages().Select(UiText.T));
        var label = ModernTheme.Label(UiText.Format("Any language → {0}", targets), 18, localize: false);
        label.Margin = new Thickness(0);
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(label);
        var arrow = ModernTheme.Label("⌄", 16, ModernTheme.Muted, localize: false);
        arrow.Margin = new Thickness(10, 0, 0, 0);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(arrow, 1); layout.Children.Add(arrow);
        var button = new Button
        {
            Name = incoming ? "HomeIncomingTargetLanguageButton" : "HomeOwnTargetLanguageButton",
            Content = layout, Background = Brushes.Transparent, Foreground = ModernTheme.Text,
            BorderBrush = ModernTheme.Stroke, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand,
            IsEnabled = CanChangeHomeTargetLanguage(incoming)
        };
        UiLocalization.Literal(button);
        AutomationProperties.SetName(button, UiText.T(incoming ? "Translate their voices to" : "Translate my voice to"));
        AutomationProperties.SetHelpText(button, UiText.T("Stop this translation before changing its language."));
        button.ToolTip = UiText.T(button.IsEnabled ? "Change translation language" : "Stop this translation before changing its language.");
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, _) => OpenHomeTargetLanguagePicker(incoming, button);
        return button;
    }

    // This small popup shares the application's theme and is built only on demand.
    // No model start, health request or download is part of changing a stopped target.
    private void OpenHomeTargetLanguagePicker(bool incoming, Button anchor)
    {
        if (!CanChangeHomeTargetLanguage(incoming)) return;
        CloseHomeTargetLanguagePicker();
        Border content = CreateHomeTargetLanguageMenu(incoming);
        var popup = _homeTargetLanguagePopup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 5,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = content
        };
        content.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            popup.IsOpen = false; anchor.Focus(); args.Handled = true;
        };
        popup.Closed += (_, _) => { if (ReferenceEquals(_homeTargetLanguagePopup, popup)) _homeTargetLanguagePopup = null; };
        popup.IsOpen = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!popup.IsOpen) return;
            var choices = (StackPanel)((ScrollViewer)((StackPanel)content.Child).Children[1]).Content;
            (choices.Children.OfType<Button>().FirstOrDefault(choice => (string?)choice.Tag ==
                (incoming ? _settings.IncomingTargetLanguage : _settings.OwnVoiceTargetLanguage))
                ?? choices.Children.OfType<Button>().FirstOrDefault())?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    internal Border CreateHomeTargetLanguageMenu(bool incoming)
    {
        // Choices describe the applied model, not an unrelated model draft on another page.
        AppSettings choicesSettings = _preview ? _settings : SettingsStore.Load();
        string alias = HomeTranslationAlias(choicesSettings, incoming);
        IReadOnlyList<string> languages = TranslationModelCapabilities.SupportedTargets(alias,
            incoming ? choicesSettings.IncomingSourceLanguage : choicesSettings.OwnVoiceSourceLanguage);
        string current = incoming ? _settings.IncomingTargetLanguage : _settings.OwnVoiceTargetLanguage;
        var body = new StackPanel();
        body.Children.Add(ModernTheme.Label(incoming ? "Translate their voices to" : "Translate my voice to", 12, ModernTheme.Muted));
        var choices = new StackPanel();
        foreach (string language in languages)
        {
            var option = ModernTheme.Button(language, (_, _) => SelectHomeTargetLanguage(incoming, language));
            option.Tag = language;
            option.Margin = new Thickness(0, 1, 0, 1); option.MinHeight = 34;
            option.HorizontalContentAlignment = HorizontalAlignment.Left;
            option.Background = language == current ? ModernTheme.Selection : Brushes.Transparent;
            choices.Children.Add(option);
        }
        body.Children.Add(new ScrollViewer
        {
            Content = choices, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var frame = new Border
        {
            Background = ModernTheme.Surface, BorderBrush = ModernTheme.Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10), MinWidth = 220, Child = body
        };
        UiLocalization.ApplyTree(frame);
        return frame;
    }

    internal bool SelectHomeTargetLanguage(bool incoming, string language)
    {
        if (!CanChangeHomeTargetLanguage(incoming)) { CloseHomeTargetLanguagePicker(); return false; }
        try
        {
            language = AppSettings.CanonicalTranslationLanguage(language)
                ?? throw new ModelSelectionUserException("Choose a language supported by the selected model.");
            // A Home language choice is a draft until its explicit Apply action.
            // Validate against the visible model draft, never save a different
            // persisted selection as a side effect of choosing a language.
            if (!TranslationModelCapabilities.SupportedTargets(HomeTranslationAlias(_settings, incoming),
                incoming ? _settings.IncomingSourceLanguage : _settings.OwnVoiceSourceLanguage).Contains(language))
                throw new ModelSelectionUserException("Choose a language supported by the selected model.");
            if (incoming) _settings.IncomingTargetLanguage = language;
            else
            {
                _settings.OwnVoiceTargetLanguage = language;
                // Keep unrelated unsaved extra-language choices, removing only the duplicate.
                _settings.OwnVoiceAdditionalTargetLanguages = _settings.NormalizedOwnVoiceAdditionalLanguages();
            }
            CloseHomeTargetLanguagePicker();
            Navigate("Home");
            UiLocalization.SetText(_status, "Unsaved changes");
            RefreshSaveState();
            return true;
        }
        catch (Exception error)
        {
            Error("Change translation language", error);
            return false;
        }
    }

    private static string HomeTranslationAlias(AppSettings settings, bool incoming)
    {
        bool separate = incoming && !settings.ShareTranslationModel;
        if (!settings.UseManagedTranslator) return separate ? settings.TranslatorModel : settings.OwnTranslatorModel;
        string id = separate ? settings.IncomingManagedModelId : settings.OwnManagedModelId;
        return LocalTalkProvisioner.ModelCatalog.FirstOrDefault(choice => choice.Id == id)?.ApiAlias ?? id;
    }

    private void CloseHomeTargetLanguagePicker()
    {
        if (_homeTargetLanguagePopup is { } popup) popup.IsOpen = false;
        _homeTargetLanguagePopup = null;
    }
}

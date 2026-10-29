using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

// Profile keys remain stable. Saved names are user text and must never be translated.
internal sealed record TranslationProfileUiChoice(string Id, string Name, string Description, bool IsBuiltIn)
{
    public override string ToString() => IsBuiltIn || Id == TranslationProfiles.UnsavedId ? UiText.T(Name) : Name;
}

internal sealed partial class MainWindow
{
    private ComboBox? _translationProfilesPicker;
    private TextBlock? _translationProfileDescription;
    private TextBlock? _translationProfileState;
    private Button? _translationProfileApplyButton;
    private Button? _translationProfileUpdateButton;
    private Button? _translationProfileRenameButton;
    private Button? _translationProfileDeleteButton;
    private TextBlock? _translationProfileExperimental;
    private FrameworkElement? _groupSpeechModelOptions;
    private bool _refreshingTranslationProfiles;
    private string? _translationProfileNotice;

    private Border CreateTranslationProfilesCard()
    {
        var section = Section("Translation profiles", "Choose a preset, or save your current settings as a profile.");
        section.Name = "TranslationProfilesSection";
        _translationProfilesPicker = new ComboBox { Name = "TranslationProfilePicker", MinHeight = 42, Margin = new Thickness(0, 0, 0, 10) };
        UiLocalization.Bind(_translationProfilesPicker, AutomationProperties.NameProperty, "Translation profile");
        UiLocalization.TranslateProfileLabels(_translationProfilesPicker);
        section.Children.Add(_translationProfilesPicker);
        _translationProfileDescription = ModernTheme.Label("", 13, ModernTheme.Muted);
        section.Children.Add(_translationProfileDescription);
        _translationProfileExperimental = ModernTheme.Label("Experimental", 11, ModernTheme.Warning);
        section.Children.Add(_translationProfileExperimental);
        var groupModels = new StackPanel();
        var speechModels = new[] { new GroupSpeechModelUiChoice(TranslationProfiles.DefaultVibeVoiceModel, "VibeVoice 1.5B"),
            new GroupSpeechModelUiChoice("microsoft/VibeVoice-ASR-Streaming-7B", "VibeVoice 7B") };
        var speechPicker = new ComboBox { Name = "GroupsSpeechModelPicker", MinHeight = 40, ItemsSource = speechModels,
            SelectedItem = speechModels.First(model => model.Id == _settings.VibeVoiceModelId), Margin = new Thickness(0, 0, 0, 8) };
        groupModels.Children.Add(Field("Groups speech model", speechPicker));
        groupModels.Children.Add(ModernTheme.Label("Start with 1.5B. The 7B model needs a larger download and more free memory alongside your game. Both add audio buffering; a larger model is not a speed fix.", 12, ModernTheme.Muted));
        speechPicker.SelectionChanged += (_, _) =>
        {
            if (speechPicker.SelectedItem is not GroupSpeechModelUiChoice model) return;
            _settings.VibeVoiceModelId = model.Id;
            _translationProfileNotice = null;
            RefreshTranslationProfileSelection();
        };
        _groupSpeechModelOptions = new Expander { Header = "Groups model options", Content = groupModels, Margin = new Thickness(0, 2, 0, 10) };
        section.Children.Add(_groupSpeechModelOptions);
        _translationProfileState = ModernTheme.Label("", 12, ModernTheme.Muted);
        _translationProfileState.Name = "TranslationProfileState";
        section.Children.Add(_translationProfileState);

        var actions = new WrapPanel();
        _translationProfileApplyButton = ModernTheme.Button("Apply profile", async (_, _) =>
        {
            if (_translationProfilesPicker?.SelectedItem is not TranslationProfileUiChoice choice) return;
            await ApplyTranslationProfileAsync(choice.Id);
            _translationProfileNotice = null;
            RefreshTranslationProfilesUi();
        }, true);
        _translationProfileApplyButton.Name = "ApplyTranslationProfileButton";
        actions.Children.Add(_translationProfileApplyButton);
        var create = ModernTheme.Button("Save current settings as…", (_, _) => SaveCurrentTranslationProfile());
        create.Name = "CreateTranslationProfileButton";
        create.IsEnabled = !_busy;
        actions.Children.Add(create);
        section.Children.Add(actions);
        section.Children.Add(ModernTheme.Label("Presets keep your chosen translator, languages and appearance. Saved profiles include your current app settings, but not downloaded models or saved people.", 12, ModernTheme.Muted));

        var manage = new WrapPanel();
        _translationProfileUpdateButton = ModernTheme.Button("Update saved profile", (_, _) => UpdateCurrentTranslationProfile());
        _translationProfileUpdateButton.Name = "UpdateTranslationProfileButton";
        manage.Children.Add(_translationProfileUpdateButton);
        _translationProfileRenameButton = ModernTheme.Button("Rename", (_, _) => RenameTranslationProfile());
        _translationProfileRenameButton.Name = "RenameTranslationProfileButton";
        manage.Children.Add(_translationProfileRenameButton);
        _translationProfileDeleteButton = ModernTheme.Button("Delete", (_, _) => DeleteTranslationProfile());
        _translationProfileDeleteButton.Name = "DeleteTranslationProfileButton";
        manage.Children.Add(_translationProfileDeleteButton);
        section.Children.Add(new Expander { Name = "ManageTranslationProfiles", Header = "Manage saved profiles", Content = manage, Margin = new Thickness(0, 6, 0, 0) });
        _translationProfilesPicker.SelectionChanged += (_, _) =>
        {
            if (_refreshingTranslationProfiles) return;
            _translationProfileNotice = null;
            RefreshTranslationProfileSelection();
        };
        RefreshTranslationProfilesUi();
        return ModernTheme.Card(section);
    }

    internal void RefreshTranslationProfilesUi()
    {
        if (_translationProfilesPicker is null) return;
        string selected = (_translationProfilesPicker.SelectedItem as TranslationProfileUiChoice)?.Id ?? _settings.ActiveTranslationProfileId;
        var choices = TranslationProfiles.List(_settings).Select(choice => new TranslationProfileUiChoice(choice.Id, choice.Name, choice.Description, choice.IsBuiltIn)).ToArray();
        _refreshingTranslationProfiles = true;
        try
        {
            _translationProfilesPicker.ItemsSource = choices;
            _translationProfilesPicker.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selected)
                ?? choices.FirstOrDefault(choice => choice.Id == _settings.ActiveTranslationProfileId) ?? choices.FirstOrDefault();
        }
        finally { _refreshingTranslationProfiles = false; }
        RefreshTranslationProfileSelection();
    }

    private void RefreshTranslationProfileSelection()
    {
        if (_translationProfilesPicker?.SelectedItem is not TranslationProfileUiChoice choice || _translationProfileDescription is null || _translationProfileState is null) return;
        UiLocalization.SetText(_translationProfileDescription, choice.Description);
        bool savedProfile = _settings.CustomTranslationProfiles.Any(profile => profile.Id == choice.Id);
        bool current = choice.Id == _settings.ActiveTranslationProfileId;
        bool modified = current && TranslationProfiles.IsModified(_settings);
        bool matchesApplied = current && TranslationProfiles.ConfigurationEquals(_settings, SettingsStore.Load());
        string status = _translationProfileNotice ?? (!current ? "Not applied. Choose Apply profile to use these settings."
            : modified ? "Modified settings. Save a custom profile to keep these changes."
            : matchesApplied ? "Current profile" : "Profile saved. Choose Apply profile to use it.");
        UiLocalization.SetText(_translationProfileState, status);
        _translationProfileState.Foreground = modified || !current || !matchesApplied ? ModernTheme.Warning : ModernTheme.Good;
        if (_translationProfileExperimental is not null) _translationProfileExperimental.Visibility = choice.Id == TranslationProfiles.GroupsId ? Visibility.Visible : Visibility.Collapsed;
        if (_groupSpeechModelOptions is not null) _groupSpeechModelOptions.Visibility = choice.Id == TranslationProfiles.GroupsId || _settings.IncomingSpeechEngine == "vibevoice" ? Visibility.Visible : Visibility.Collapsed;
        if (_translationProfileApplyButton is not null) _translationProfileApplyButton.IsEnabled = !_busy && (!current || modified || !matchesApplied);
        if (_translationProfileUpdateButton is not null) _translationProfileUpdateButton.IsEnabled = !_busy && savedProfile && current && modified;
        if (_translationProfileRenameButton is not null) _translationProfileRenameButton.IsEnabled = !_busy && savedProfile;
        if (_translationProfileDeleteButton is not null) _translationProfileDeleteButton.IsEnabled = !_busy && savedProfile;
    }

    private void SaveCurrentTranslationProfile()
    {
        if (_preview || _busy || _closing) return;
        string? name = PromptTranslationProfileName("Save profile", "Name this set of settings.", "", null);
        if (name is null) return;
        TryChangeTranslationProfileMetadata(() => TranslationProfiles.Create(_settings, name), "Profile saved. Choose Apply profile to use it.");
    }

    private void UpdateCurrentTranslationProfile()
    {
        if (_preview || _busy || _closing || _translationProfilesPicker?.SelectedItem is not TranslationProfileUiChoice choice || choice.IsBuiltIn || choice.Id != _settings.ActiveTranslationProfileId) return;
        if (!Confirm("Replace this saved profile with your current settings?")) return;
        TryChangeTranslationProfileMetadata(() => TranslationProfiles.Update(_settings, choice.Id), "Profile updated. Apply settings to use your changes.");
    }

    private void RenameTranslationProfile()
    {
        if (_preview || _busy || _closing || _translationProfilesPicker?.SelectedItem is not TranslationProfileUiChoice choice || choice.IsBuiltIn) return;
        string? name = PromptTranslationProfileName("Rename profile", "Choose a new name for this profile.", choice.Name, choice.Id);
        if (name is null) return;
        TryChangeTranslationProfileMetadata(() => TranslationProfiles.Rename(_settings, choice.Id, name), "Profile renamed.");
    }

    private void DeleteTranslationProfile()
    {
        if (_preview || _busy || _closing || _translationProfilesPicker?.SelectedItem is not TranslationProfileUiChoice choice || choice.IsBuiltIn) return;
        if (!Confirm("Delete this saved profile? Your current settings, saved people and downloaded models will stay on this PC.")) return;
        TryChangeTranslationProfileMetadata(() => TranslationProfiles.Delete(_settings, choice.Id), "Profile deleted. Your current settings are unchanged.");
    }

    // Editing the profile library must not apply a pending model/language choice or restart
    // an active conversation. The targeted store operation preserves applied settings.
    private void TryChangeTranslationProfileMetadata(Func<AppSettings> operation, string notice)
    {
        AppSettings before = _settings.Copy();
        try
        {
            AppSettings candidate = operation();
            SettingsStore.SaveTranslationProfileMetadata(candidate);
            TranslationProfiles.CopyInto(candidate, _settings);
            _translationProfileNotice = notice == "Profile saved. Choose Apply profile to use it." && TranslationProfiles.ConfigurationEquals(_settings, SettingsStore.Load())
                ? "Profile saved. Your current settings are already active." : notice;
            if (_translationProfilesPicker is not null) _translationProfilesPicker.SelectedItem = null;
            RefreshTranslationProfilesUi();
            RefreshSaveState(reloadSaved: true);
        }
        catch (Exception error)
        {
            TranslationProfiles.CopyInto(before, _settings);
            Error("Save profile", error);
            RefreshSaveState();
        }
    }

    private string? PromptTranslationProfileName(string title, string description, string initial, string? existingId)
    {
        Window dialog = CreateTranslationProfileNameDialog(title, description, initial, existingId);
        return dialog.ShowDialog() == true ? (string?)dialog.Tag : null;
    }

    internal Window CreateTranslationProfileNameDialog(string title, string description, string initial, string? existingId)
    {
        var dialog = new Window { Title = UiText.T(title), Icon = BrandArtwork.WindowIcon, Owner = IsVisible ? this : null,
            Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = ModernTheme.Background, Foreground = ModernTheme.Text,
            ShowInTaskbar = false, Topmost = false };
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(ModernTheme.Heading(title, 25));
        body.Children.Add(ModernTheme.Label(description, 13, ModernTheme.Muted));
        var input = Input(initial); input.Name = "TranslationProfileName"; input.MaxLength = 60;
        UiLocalization.Bind(input, AutomationProperties.NameProperty, "Profile name");
        body.Children.Add(Field("Profile name", input));
        var error = ModernTheme.Label("", 12, ModernTheme.Error); error.Name = "TranslationProfileNameError";
        body.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var save = ModernTheme.Button("Save profile", (_, _) =>
        {
            string name = Regex.Replace(input.Text.Trim(), @"\s+", " ");
            if (name.Length == 0) { UiLocalization.SetText(error, "Enter a profile name."); input.Focus(); return; }
            if (_settings.CustomTranslationProfiles.Any(profile => profile.Id != existingId && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
                || TranslationProfiles.List(_settings).Any(profile => profile.IsBuiltIn &&
                    (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(UiText.T(profile.Name), name, StringComparison.OrdinalIgnoreCase))))
            { UiLocalization.SetText(error, "A profile with this name already exists."); input.Focus(); return; }
            dialog.Tag = name; dialog.DialogResult = true;
        }, true);
        save.Name = "ConfirmTranslationProfileNameButton"; save.IsDefault = true;
        actions.Children.Add(save);
        var cancel = ModernTheme.Button("Cancel", (_, _) => dialog.DialogResult = false); cancel.IsCancel = true;
        actions.Children.Add(cancel); body.Children.Add(actions); dialog.Content = body;
        UiLocalization.ApplyTree(dialog);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.SourceInitialized += (_, _) => AppearanceTheme.ApplyWindowChrome(dialog);
        return dialog;
    }

    private sealed record GroupSpeechModelUiChoice(string Id, string Name)
    { public override string ToString() => Name; }
}

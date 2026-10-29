using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Only unshown controls and synthetic fixtures under DesktopTests' isolated data root.
// No installer, model, translation request, microphone or playback service is started.
internal static class ThemeTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool passed, string reason)
        {
            if (!passed) throw new InvalidOperationException("Theme check: " + reason);
            checks++;
        }
        Check(Read<bool>(window, "_preview") && !window.IsLoaded && !window.IsVisible, "an inert unshown dashboard is required");
        Check(window.Dispatcher.CheckAccess(), "appearance checks stay on the UI dispatcher");
        RuntimeController runtime = Read<RuntimeController>(window, "_runtime");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "translation tools are stopped in the isolated preview");
        string savedSettings = File.ReadAllText(AppPaths.SettingsPath);
        string ownConfig = File.ReadAllText(AppPaths.FastConfigPath), incomingConfig = File.ReadAllText(AppPaths.FoxTransConfigPath);
        string log = Path.Combine(AppPaths.Root, "incoming-translator.log");
        byte[] logBefore = File.Exists(log) ? File.ReadAllBytes(log) : [];
        string previousLanguage = UiText.Language;
        AppSettings dashboardSettings = Read<AppSettings>(window, "_settings");
        string previousPage = Read<string>(window, "_page");
        AppSettings original = dashboardSettings.Copy();
        Directory.CreateDirectory(Path.Combine(directory, "themes"));
        var brushes = typeof(AppearanceTheme).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(SolidColorBrush))
            .ToDictionary(field => field.Name, field => (SolidColorBrush)field.GetValue(null)!);
        int changes = 0; void Changed() => changes++;
        AppearanceTheme.Changed += Changed;
        try
        {
            Check(new AppSettings().UiThemeId == "coral-night", "existing appearance remains the first-run default");
            Check(AppearanceTheme.Presets.Select(item => item.Id).Distinct().Count() == AppearanceTheme.Presets.Count, "preset identifiers are unique");
            Check(AppearanceTheme.Presets.Count >= 8 && AppearanceTheme.Presets.Any(item => !item.IsDark) && AppearanceTheme.Presets.Any(item => item.IsDark), "the presets include distinct dark and light choices");
            Check(AppearanceTheme.TextPalettes.Count >= 5 && AppearanceTheme.TextPalettes.Any(item => item.Id == "auto"), "coordinated text palettes include automatic readability");
            Check(AppearanceTheme.TryColor("#aBcDeF", out Color valid) && valid == Color.FromRgb(171, 205, 239), "six-digit colors are accepted without losing precision");
            foreach (string? invalid in new string?[] { null, "", "red", "#FFF", "#FF112233", "112233", "#12GH00", "#123456 ", "https://example/image.png" })
                Check(!AppearanceTheme.TryColor(invalid, out _), "invalid or alpha color is rejected: " + invalid);
            AppSettings malformed = original.Copy();
            malformed.UiThemeId = "unknown"; malformed.CustomThemeBackground = "not a color";
            malformed.CustomThemeTextPalette = "unknown"; malformed.CustomThemeImageOpacity = double.NaN;
            malformed.CustomThemeImagePath = null!;
            AppearanceTheme.Normalize(malformed);
            Check(malformed.UiThemeId == "coral-night" && malformed.CustomThemeBackground == "#191817" && malformed.CustomThemeTextPalette == "auto", "invalid saved appearance safely returns to defaults");
            Check(malformed.CustomThemeImageOpacity == .16 && malformed.CustomThemeImagePath == "", "invalid image preferences are bounded and normalized");
            malformed.CustomThemeImageOpacity = 2; AppearanceTheme.Normalize(malformed);
            Check(malformed.CustomThemeImageOpacity == .6, "image opacity cannot exceed the readability limit");
            malformed.CustomThemeImageOpacity = -1; AppearanceTheme.Normalize(malformed);
            Check(malformed.CustomThemeImageOpacity == 0, "negative image opacity clamps to zero");

            CheckPersistence(original, savedSettings, ownConfig, incomingConfig, Check);
            CheckImageControls(window, original, Path.Combine(directory, "themes"), Check);

            // Every grayscale crossing, including the difficult middle shades, is checked
            // against every text palette. The user never has to repair an unreadable font.
            foreach (ThemeTextPalette palette in AppearanceTheme.TextPalettes)
            for (int value = 0; value <= 255; value++)
            {
                AppSettings sample = original.Copy(); sample.UiThemeId = "custom";
                sample.CustomThemeTextPalette = palette.Id;
                sample.CustomThemeBackground = $"#{value:X2}{value:X2}{value:X2}";
                AppearanceTheme.ApplyForTests(sample);
                CheckContrast(palette.Id + "/" + sample.CustomThemeBackground, Check);
            }
            foreach (string color in new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#00FFFF", "#FF00FF", "#AA7744", "#428866", "#8877CC" })
            foreach (ThemeTextPalette palette in AppearanceTheme.TextPalettes)
            {
                AppSettings sample = original.Copy(); sample.UiThemeId = "custom";
                sample.CustomThemeTextPalette = palette.Id; sample.CustomThemeBackground = color;
                AppearanceTheme.ApplyForTests(sample); CheckContrast(palette.Id + "/" + color, Check);
            }

            UiText.Language = "en";
            foreach (ThemePreset preset in AppearanceTheme.Presets)
            {
                AppSettings sample = original.Copy(); sample.UiThemeId = preset.Id;
                sample.CustomThemeImagePath = "";
                SettingsStore.SaveAppearancePreference(sample); // Isolated data only: overlays read the same selected appearance.
                CopyAppearance(sample, dashboardSettings);
                AppearanceTheme.Apply(sample);
                ThemeBackground.Apply(sample);
                Check(AppearanceTheme.CurrentPresetId == preset.Id, "the selected preset is applied: " + preset.Id);
                CheckContrast(preset.Id, Check);
                foreach (var pair in brushes)
                {
                    Check(ReferenceEquals(pair.Value, typeof(AppearanceTheme).GetField(pair.Key)!.GetValue(null)), "brush remains stable for open windows: " + pair.Key);
                    Check(!pair.Value.IsFrozen && !pair.Value.CanFreeze, "shared brush cannot be auto-frozen by WPF templates: " + pair.Key);
                }
                Check(ReferenceEquals(ModernTheme.Text, AppearanceTheme.Text) && ReferenceEquals(ModernTheme.Background, AppearanceTheme.Background), "the dashboard and subtitles share one semantic palette");
                foreach (string page in MainWindow.Pages)
                {
                    window.Navigate(page);
                    FrameworkElement content = (FrameworkElement)window.Content;
                    Prepare(content, new Size(1160, 820));
                    Check(!runtime.OwnRunning && !runtime.IncomingRunning, "theme navigation does not start models: " + preset.Id + "/" + page);
                    CheckTextContrast(content, AppearanceTheme.Background.Color, preset.Id + "/" + page, Check);
                    if (page is "Home" or "Settings" or "Themes")
                        Render(content, Path.Combine(directory, "themes", preset.Id + "-" + page.ToLowerInvariant() + ".png"), 1160, 820);
                }

                var fixture = new TranslationDisplay("Home · Alice", "Settings · 今天见！", "A friend's unchanged subtitle.", 12345, DateTimeOffset.UtcNow)
                    { EvidenceId = "inert-theme-evidence" };
                var feedback = new SpeakerFeedbackState(new[] { new SpeakerPerson("inert-theme-person", "Home · Alice", 2) }, false);
                var overlay = new SubtitleWindow();
                overlay.Publish(fixture);
                Check(!overlay.IsLoaded && !overlay.IsVisible && overlay.HistoryEntryCount == 1, "subtitle preview stays inert while showing a synthetic bubble");
                Check(overlay.HistoryEntry(fixture.UtteranceId)?.Source == fixture.Source && overlay.HistoryEntry(fixture.UtteranceId)?.Translation == fixture.Translation,
                    "themes never rewrite spoken text, translations or speaker names");
                var dialogs = new[] {
                    (Window: SpeakerFeedbackUi.CreateCorrectionDialog(window, new InertFeedback(feedback), fixture, feedback), Name: "speaker-correction", Height: 700),
                    (Window: SpeakerFeedbackUi.CreateLearningDialog(window, "Home · Alice"), Name: "speaker-learning", Height: 520),
                    (Window: SpeakerFeedbackUi.CreatePersonDialog(window), Name: "speaker-name", Height: 380),
                    (Window: DesktopLifetime.CreateCloseChoiceDialog(window), Name: "close-choice", Height: 620),
                    (Window: window.CreateSetupConsentDialog(new("Download the translation files and prepare speech recognition. Windows may ask for permission.", 4_000_000_000, true, true)), Name: "setup-consent", Height: 640),
                    (Window: AppearanceTheme.CreateMessageDialog(window, "Literal name: Home · 今天见！", "Synthetic themed confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question), Name: "confirmation", Height: 320),
                    (Window: (Window)overlay, Name: "subtitles", Height: 430),
                    (Window: (Window)new SettingsWindow(sample), Name: "legacy-settings", Height: 740)
                };
                foreach (var dialog in dialogs)
                {
                    Check(!dialog.Window.IsLoaded && !dialog.Window.IsVisible, "every secondary window is an unshown fixture: " + dialog.Name);
                    var body = dialog.Name == "subtitles" ? overlay.TranscriptRoot : (FrameworkElement)dialog.Window.Content;
                    Prepare(body, new Size(dialog.Window.Width, dialog.Height));
                    CheckTextContrast(body, AppearanceTheme.Background.Color, preset.Id + "/" + dialog.Name, Check);
                    Render(body, Path.Combine(directory, "themes", preset.Id + "-" + dialog.Name + ".png"), (int)dialog.Window.Width, dialog.Height);
                    if (dialog.Name == "confirmation")
                        Check(Nodes(body).OfType<TextBlock>().Any(label => label.Text == "Literal name: Home · 今天见！"), "themed confirmations preserve literal names and message contents");
                }
                CheckControlTemplates(preset.Id, Check);
                TranslationDisplay? entryBefore = overlay.HistoryEntry(fixture.UtteranceId);
                TextBlock[] subtitlesBefore = Nodes(overlay.CompactPreview).OfType<TextBlock>().Where(label => label.Text == fixture.Translation).ToArray();
                Color before = AppearanceTheme.Text.Color;
                AppSettings opposite = sample.Copy(); opposite.UiThemeId = AppearanceTheme.IsDark ? "paper" : "ink";
                AppearanceTheme.Apply(opposite);
                Check(AppearanceTheme.Text.Color != before, "changing between light and dark changes actual text color");
                Check(overlay.HistoryEntry(fixture.UtteranceId) == entryBefore && overlay.HistoryEntryCount == 1, "a live theme change preserves existing subtitle history");
                Check(subtitlesBefore.Length > 0 && subtitlesBefore.All(label => ReferenceEquals(label.Foreground, AppearanceTheme.Text)), "existing subtitle text retains the live semantic brush across theme changes");
                Check(Nodes(overlay.TranscriptRoot).OfType<TextBox>().Any(label => label.Text == fixture.Translation && ReferenceEquals(label.Foreground, AppearanceTheme.Text)),
                    "the copyable transcript retains the same live semantic text brush");
                AppearanceTheme.Apply(sample);
                foreach (var dialog in dialogs) dialog.Window.Close();
            }
            Check(changes > 0, "theme changes notify already-open UI consumers");
            Check(File.ReadAllText(AppPaths.FastConfigPath) == ownConfig && File.ReadAllText(AppPaths.FoxTransConfigPath) == incomingConfig, "all appearance previews preserve both live translation configurations");
            Check((File.Exists(log) ? File.ReadAllBytes(log) : []).SequenceEqual(logBefore), "appearance previews do not add audio or inference activity");
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language;
                foreach (string key in AppearanceTheme.Presets.Select(p => p.Name).Concat(AppearanceTheme.TextPalettes.Select(p => p.Name)).Append("Themes"))
                    Check(UiText.Contains(key) && (language == "en" || UiText.T(key) != key), "every appearance choice is available in " + language + ": " + key);
            }
            return checks;
        }
        finally
        {
            AppearanceTheme.Changed -= Changed;
            File.WriteAllText(AppPaths.SettingsPath, savedSettings);
            CopyAppearance(original, dashboardSettings);
            UiText.Language = previousLanguage;
            AppearanceTheme.Apply(original);
            ThemeBackground.Apply(original);
            window.Navigate(previousPage);
        }
    }

    private static void CheckImageControls(MainWindow window, AppSettings original, string directory, Action<bool, string> check)
    {
        string imagePath = Path.GetFullPath(Path.Combine(directory, "synthetic-background.png"));
        byte[] pixels = [40, 80, 160, 255, 160, 80, 40, 255, 80, 160, 40, 255, 160, 40, 80, 255];
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(imagePath)) encoder.Save(file);
        BitmapSource decoded = ThemeBackground.ReadImage(imagePath);
        check(decoded.IsFrozen && decoded.PixelWidth == 2 && decoded.PixelHeight == 2, "local background images decode once into immutable bounded pixels");
        var layer = (Image)ThemeBackground.CreateLayer();
        check(!layer.IsHitTestVisible && !layer.Focusable && layer.Name == "ThemeBackgroundImage", "decorative artwork cannot intercept buttons or keyboard focus");
        var custom = original.Copy(); custom.UiThemeId = "custom"; custom.CustomThemeImagePath = imagePath;
        custom.CustomThemeImageOpacity = .35;
        check(ThemeBackground.Apply(custom) && layer.Source is BitmapSource && layer.Opacity == .35, "custom artwork and opacity update existing layers");
        ImageSource cached = layer.Source;
        custom.CustomThemeImageOpacity = .22;
        check(ThemeBackground.Apply(custom) && ReferenceEquals(cached, layer.Source) && layer.Opacity == .22, "opacity changes reuse cached decoded pixels");
        custom.CustomThemeImagePath = "";
        check(ThemeBackground.Apply(custom) && layer.Source is null, "removing artwork releases the image layer");
        custom.CustomThemeImagePath = Path.GetFullPath(Path.Combine(directory, "missing.png"));
        check(!ThemeBackground.Apply(custom) && layer.Source is null, "missing artwork safely falls back to a solid background");
        foreach (string invalid in new[] { "https://example/image.png", @"\\server\share\image.png", "relative.png", @"C:\synthetic.gif" })
        {
            bool denied = false;
            try { ThemeBackground.ReadImage(invalid); }
            catch (InvalidDataException) { denied = true; }
            check(denied, "background selection refuses nonlocal or unsupported image sources without networking: " + invalid);
        }
        string mismatched = Path.Combine(directory, "synthetic-mismatch.jpg"); File.Copy(imagePath, mismatched, true);
        bool mismatchDenied = false;
        try { ThemeBackground.ReadImage(mismatched); }
        catch (InvalidDataException) { mismatchDenied = true; }
        check(mismatchDenied, "image contents must match the supported extension");
        string oversized = Path.Combine(directory, "synthetic-oversized.png");
        using (var file = File.Create(oversized)) file.SetLength(ThemeBackground.MaximumImageBytes + 1);
        bool sizeDenied = false;
        try { ThemeBackground.ReadImage(oversized); }
        catch (InvalidDataException) { sizeDenied = true; }
        check(sizeDenied, "oversized artwork is rejected before decoding");

        UiText.Language = "en"; window.Navigate("Themes");
        FrameworkElement root = (FrameworkElement)window.Content; Prepare(root, new Size(1160, 820));
        var picker = Nodes(root).OfType<ComboBox>().Single(node => node.Name == "ThemePicker");
        var palette = Nodes(root).OfType<ComboBox>().Single(node => node.Name == "TextPalettePicker");
        var color = Nodes(root).OfType<TextBox>().Single(node => node.Name == "CustomBackgroundColor");
        check(picker.Items.Count == AppearanceTheme.Presets.Count && !picker.IsEditable, "theme selection exposes all fixed presets");
        check(palette.Items.Count == AppearanceTheme.TextPalettes.Count && !palette.IsEditable, "font colors use coordinated predefined palettes, not independent color pickers");
        color.Text = "#123ABC";
        int paper = AppearanceTheme.Presets.Select((item, index) => (item, index)).Single(item => item.item.Id == "paper").index;
        picker.SelectedIndex = paper;
        check(AppearanceTheme.CurrentPresetId == "paper" && Read<string>(window, "_page") == "Themes", "changing the picker applies light appearance immediately without navigation");
        check(color.Text == "#123ABC", "a live theme change preserves in-progress editing state");
        var runtime = Read<RuntimeController>(window, "_runtime");
        check(!runtime.OwnRunning && !runtime.IncomingRunning, "live theme changes do not start or restart translation models");
        check(window.ChangeAppearance(settings => { settings.UiThemeId = "custom"; settings.CustomThemeImagePath = imagePath; settings.CustomThemeImageOpacity = .3; }, true), "custom theme can be applied through the same dashboard pathway");
        check(layer.Source is not null && layer.Opacity == .3, "dashboard changes update already-open decorative layers");
        // This fixture used the internal pathway rather than selecting Custom in its
        // own picker. Rebuild the scene so the screenshot's picker and custom options
        // reflect the updated settings, just as a fresh visit to Themes would.
        window.Navigate("Themes");
        check(Nodes((FrameworkElement)window.Content).OfType<ComboBox>().Single(node => node.Name == "ThemePicker").SelectedItem?.ToString() == "Custom",
            "custom artwork screenshots display the matching selected theme and options");
        Render((FrameworkElement)window.Content, Path.Combine(directory, "custom-image-themes.png"), 1160, 820);
        window.Navigate("Home"); Render((FrameworkElement)window.Content, Path.Combine(directory, "custom-image-home.png"), 1160, 820);
    }

    private static void CheckPersistence(AppSettings original, string savedSettings, string ownConfig, string incomingConfig, Action<bool, string> check)
    {
        JsonObject document = JsonNode.Parse(savedSettings)!.AsObject();
        document["FutureUnknownThemeTestPreference"] = "preserved";
        File.WriteAllText(AppPaths.SettingsPath, document.ToJsonString());
        AppSettings selected = original.Copy(); selected.UiThemeId = "custom"; selected.CustomThemeBackground = "#e8e9ea";
        selected.CustomThemeTextPalette = "warm-dark"; selected.CustomThemeImagePath = "C:\\synthetic fixture\\Home.png"; selected.CustomThemeImageOpacity = .23;
        using (File.Open(AppPaths.FastConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (File.Open(AppPaths.FoxTransConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            SettingsStore.SaveAppearancePreference(selected);
        JsonObject expected = document.DeepClone().AsObject();
        expected[nameof(AppSettings.UiThemeId)] = "custom";
        expected[nameof(AppSettings.CustomThemeBackground)] = "#E8E9EA";
        expected[nameof(AppSettings.CustomThemeTextPalette)] = "warm-dark";
        expected[nameof(AppSettings.CustomThemeImagePath)] = selected.CustomThemeImagePath;
        expected[nameof(AppSettings.CustomThemeImageOpacity)] = .23;
        check(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))), "appearance save changes only its five fields and preserves unknown settings");
        AppSettings loaded = SettingsStore.Load(), copied = loaded.Copy();
        check(AppearanceKey(copied) == AppearanceKey(loaded) && copied.CustomThemeImagePath == selected.CustomThemeImagePath, "all five appearance preferences survive save/load/copy");
        check(loaded.OwnVoiceTargetLanguage == original.OwnVoiceTargetLanguage && loaded.IncomingTargetLanguage == original.IncomingTargetLanguage && loaded.OwnManagedModelId == original.OwnManagedModelId,
            "themes never change language targets or translation models");
        SettingsStore.Save(original.Copy());
        check(AppearanceKey(SettingsStore.Load()) == AppearanceKey(loaded), "stale settings windows cannot undo a newer theme choice");
        check(File.ReadAllText(AppPaths.FastConfigPath) == ownConfig && File.ReadAllText(AppPaths.FoxTransConfigPath) == incomingConfig, "minimal appearance saves never rewrite microphone configuration");
        string before = File.ReadAllText(AppPaths.SettingsPath);
        bool denied = false;
        using (File.Open(AppPaths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            try { SettingsStore.SaveAppearancePreference(original); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { denied = true; }
        check(denied && File.ReadAllText(AppPaths.SettingsPath) == before, "failed atomic appearance save leaves the last settings intact");
        check(!Directory.EnumerateFiles(AppPaths.Root, "vrchat-translation-settings.json.tmp-*").Any(), "failed saves leave no appearance temporary files");
    }

    private static void CheckContrast(string scene, Action<bool, string> check)
    {
        var grounds = new[] { AppearanceTheme.Background, AppearanceTheme.Surface, AppearanceTheme.Raised, AppearanceTheme.Selection };
        var foregrounds = new[] { AppearanceTheme.Text, AppearanceTheme.Muted, AppearanceTheme.Accent, AppearanceTheme.Good, AppearanceTheme.Warning, AppearanceTheme.Error }
            .Concat(AppearanceTheme.Speakers);
        foreach (SolidColorBrush ink in foregrounds)
        foreach (SolidColorBrush ground in grounds)
            check(AppearanceTheme.Contrast(ink.Color, ground.Color) >= 4.5 - .001, "normal-text contrast remains readable: " + scene + " " + ink.Color + "/" + ground.Color);
        foreach (SolidColorBrush cue in new[] { AppearanceTheme.Stroke, AppearanceTheme.ScrollGrip })
        foreach (SolidColorBrush ground in grounds)
            check(AppearanceTheme.Contrast(cue.Color, ground.Color) >= 3 - .001, "boundaries and scrollbar remain visible: " + scene + " " + cue.Color + "/" + ground.Color);
        check(AppearanceTheme.Contrast(AppearanceTheme.Ink.Color, AppearanceTheme.Accent.Color) >= 4.5 - .001, "primary button text remains readable: " + scene);
    }

    private static void CheckControlTemplates(string scene, Action<bool, string> check)
    {
        var controls = new StackPanel();
        var ordinary = ModernTheme.Button("Theme", (_, _) => { });
        var primary = ModernTheme.Button("Theme", (_, _) => { }, true);
        var text = new TextBox { Text = "Unchanged model/path", Width = 300 };
        var combo = new ComboBox { ItemsSource = new[] { "Chinese", "Japanese" }, SelectedIndex = 0 };
        var selected = new ComboBoxItem { Content = "Selected option", IsSelected = true };
        controls.Children.Add(ordinary); controls.Children.Add(primary); controls.Children.Add(text); controls.Children.Add(combo); controls.Children.Add(selected);
        controls.Children.Add(new CheckBox { Content = "Checked option", IsChecked = true });
        controls.Children.Add(new RadioButton { Content = "Selected radio", IsChecked = true });
        controls.Children.Add(new Expander { Header = "Expanded option", IsExpanded = true, Content = ModernTheme.Label("Expanded text") });
        controls.Children.Add(new Slider { Minimum = 0, Maximum = 1, Value = .5 });
        controls.Children.Add(new ProgressBar { Value = 40, Height = 12 });
        var tooltip = new ToolTip { Content = "Tooltip text" };
        ordinary.ToolTip = tooltip;
        var frame = new Border { Background = AppearanceTheme.Background, Child = controls };
        Prepare(frame, new Size(700, 700));
        // A ToolTip is a popup surface, not a child that may be added to a panel.
        // Measure its template without opening the popup or showing any window.
        Prepare(tooltip, new Size(300, 100));
        check(!tooltip.IsOpen, "tooltip preview never opens a native popup: " + scene);
        CheckTextContrast(tooltip, AppearanceTheme.Surface.Color, scene + "/tooltip", check);
        check(ReferenceEquals(ordinary.Foreground, AppearanceTheme.Text) && ReferenceEquals(primary.Foreground, AppearanceTheme.Ink), "both ordinary and primary button text use theme roles: " + scene);
        var primaryLabels = Nodes(primary).OfType<TextBlock>().Where(label => label.Text == "Theme").ToArray();
        var ordinaryLabels = Nodes(ordinary).OfType<TextBlock>().Where(label => label.Text == "Theme").ToArray();
        check(primaryLabels.Length > 0 && primaryLabels.All(label => ReferenceEquals(label.Foreground, AppearanceTheme.Ink)), "generated primary button labels inherit ink rather than global text: " + scene);
        check(ordinaryLabels.Length > 0 && ordinaryLabels.All(label => ReferenceEquals(label.Foreground, AppearanceTheme.Text)), "generated secondary button labels inherit ordinary text: " + scene);
        check(ReferenceEquals(text.Foreground, AppearanceTheme.Text) && ReferenceEquals(text.Background, AppearanceTheme.Raised), "editable text follows dark and light palettes: " + scene);
        check(ReferenceEquals(combo.Foreground, AppearanceTheme.Text) && ReferenceEquals(selected.Foreground, AppearanceTheme.Text), "dropdown display and list options are themed: " + scene);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        check(popup.Child is Border popupFrame && ReferenceEquals(popupFrame.Background, AppearanceTheme.Raised), "closed dropdown popup uses the same live themed background as its options: " + scene);
        check(text.CaretBrush is SolidColorBrush caret && AppearanceTheme.Contrast(caret.Color, AppearanceTheme.Raised.Color) >= 4.5, "text caret is visible: " + scene);
        CheckTextContrast(frame, AppearanceTheme.Background.Color, scene + "/control-templates", check);
        foreach (Control control in Nodes(frame).OfType<Control>())
        {
            if (control.Template is not ControlTemplate template) continue;
            foreach (TriggerBase trigger in template.Triggers)
            {
                IEnumerable<SetterBase> setters = trigger switch { Trigger value => value.Setters.Cast<SetterBase>(), MultiTrigger value => value.Setters.Cast<SetterBase>(), _ => [] };
                foreach (Setter setter in setters.OfType<Setter>())
                    if (setter.Value is SolidColorBrush brush && brush.Color.A > 0)
                        check(IsThemeBrush(brush), "hover/focus/selection template has no stale fixed color: " + scene + "/" + control.GetType().Name);
            }
        }
    }

    private static bool IsThemeBrush(Brush brush) => typeof(AppearanceTheme).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(SolidColorBrush)).Any(field => ReferenceEquals(field.GetValue(null), brush));

    private static void CheckTextContrast(DependencyObject root, Color fallback, string scene, Action<bool, string> check)
    {
        foreach (TextBlock label in Nodes(root).OfType<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(label.Text) || label.Visibility != Visibility.Visible || label.Opacity < .9 || !label.IsEnabled || label.Foreground is not SolidColorBrush ink || ink.Color.A < 255) continue;
            Color ground = BackgroundOf(label, fallback);
            double minimum = label.FontSize >= 24 || (label.FontSize >= 18.66 && label.FontWeight.ToOpenTypeWeight() >= 700) ? 3 : 4.5;
            check(AppearanceTheme.Contrast(ink.Color, ground) >= minimum - .001,
                "visible text contrast: " + scene + " [" + label.Text[..Math.Min(label.Text.Length, 48)] + "] " + ink.Color + "/" + ground);
        }
    }

    private static Color BackgroundOf(DependencyObject node, Color fallback)
    {
        for (DependencyObject? parent = node; parent is not null; parent = Parent(parent))
        {
            // Controls do not paint their Background themselves: a template must use
            // it. Borders/panels on the real visual ancestry do paint it. A ComboBox's
            // toggle shell sits behind the selection in a sibling visual, so its
            // explicitly bound background is also the selection's actual backdrop.
            Brush? brush = parent switch { Border border => border.Background, Panel panel => panel.Background,
                Window window => window.Background, ComboBox combo => combo.Background, _ => null };
            if (brush is SolidColorBrush solid && solid.Color.A == 255) return solid.Color;
        }
        return fallback;
    }
    private static DependencyObject? Parent(DependencyObject node) => node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root); var seen = new HashSet<DependencyObject>();
        while (pending.TryPop(out DependencyObject? node))
        {
            if (!seen.Add(node)) continue; yield return node;
            foreach (object child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject value) pending.Push(value);
            if (node is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
    private static string AppearanceKey(AppSettings settings) => string.Join("|", settings.UiThemeId, settings.CustomThemeBackground, settings.CustomThemeTextPalette, settings.CustomThemeImagePath, settings.CustomThemeImageOpacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    private static void CopyAppearance(AppSettings source, AppSettings target)
    {
        target.UiThemeId = source.UiThemeId; target.CustomThemeBackground = source.CustomThemeBackground;
        target.CustomThemeTextPalette = source.CustomThemeTextPalette; target.CustomThemeImagePath = source.CustomThemeImagePath; target.CustomThemeImageOpacity = source.CustomThemeImageOpacity;
    }
    private static T Read<T>(object instance, string field) => (T)(instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!);
    private static void Prepare(FrameworkElement element, Size size) { element.Measure(size); element.Arrange(new Rect(new Point(), size)); element.UpdateLayout(); }
    private static void Render(FrameworkElement element, string path, int width, int height)
    {
        Prepare(element, new Size(width, height)); var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class InertFeedback(SpeakerFeedbackState state) : ISpeakerFeedbackHost
    {
        public Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default) => Task.FromResult(state);
        private static Task<T> Unexpected<T>() => Task.FromException<T>(new InvalidOperationException("Theme previews never mutate speaker data."));
        public Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default) => Unexpected<SpeakerPerson>();
        public Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default) => Unexpected<object>();
        public Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default) => Unexpected<object>();
        public Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default) => Unexpected<SpeakerFeedbackResult>();
        public Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default) => Unexpected<SpeakerFeedbackResult>();
    }
}

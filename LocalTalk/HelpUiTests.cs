using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Inert factories and preview navigation only: no shown window, model or audio startup.
internal static class HelpUiTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException("Help / introduction check: " + message);
            checks++;
        }
        Check(Read<bool>(window, "_preview") && !window.IsVisible && !window.IsLoaded, "tests require an unshown preview");
        AppSettings settings = Read<AppSettings>(window, "_settings");
        var original = settings.Copy();
        string before = JsonSerializer.Serialize(settings);
        string saved = File.ReadAllText(AppPaths.SettingsPath);
        string ownConfig = File.ReadAllText(AppPaths.FastConfigPath), incomingConfig = File.ReadAllText(AppPaths.FoxTransConfigPath);
        string language = UiText.Language, originalPage = Read<string>(window, "_page");
        var runtime = Read<RuntimeController>(window, "_runtime");
        try
        {
            Check(MainWindow.PrimaryPages.Contains("Help") && MainWindow.Pages.Contains("Help"), "Help has its own primary route");
            Check(!window.TryShowFirstRunIntroduction(), "preview suppresses the first-run modal even when setup is incomplete");
            Check(!Read<bool>(window, "_introductionShown") && !settings.FirstRunIntroductionCompleted, "preview does not acknowledge or consume the introduction");
            Check(HelpUi.Topics.Select(topic => topic.Id).Distinct().Count() == 13, "thirteen distinct outcomes cover own-voice setup, model choice and optional subtitles");
            Check(HelpUi.Topics.SelectMany(topic => topic.Pages).All(MainWindow.Pages.Contains), "every help action targets an existing route");
            Check(HelpUi.Topics.Take(2).Select(topic => topic.Id).SequenceEqual(new[] { "getting-started", "models" }),
                "own-voice setup and visible model choices lead the help page");
            Check(HelpUi.Topics.Single(topic => topic.Id == "getting-started").Description.Contains("Subtitles and People are optional", StringComparison.Ordinal) &&
                !HelpUi.Topics.Single(topic => topic.Id == "getting-started").Description.Contains("start your voice, subtitles, or both", StringComparison.Ordinal),
                "first-time guidance does not require starting or preparing subtitles");
            HelpTopic accuracyHelp = HelpUi.Topics.Single(topic => topic.Id == "accuracy");
            HelpTopic modelHelp = HelpUi.Topics.Single(topic => topic.Id == "models");
            Check(accuracyHelp.Description.Contains("Check I speak and translation languages on Home, then Apply changes", StringComparison.Ordinal) &&
                modelHelp.Description.Contains("Speech models turn voices into words; translators change their language", StringComparison.Ordinal),
                "help explains actual source-language selection and separate recognition/translation choices on Home");
            Check(modelHelp.Description.Contains("choose speech and translation model cards, then Apply changes or Download & apply", StringComparison.Ordinal) &&
                modelHelp.Description.Contains("Switching may briefly pause translation; the app handles that", StringComparison.Ordinal) &&
                modelHelp.Description.Contains("When stopped, Apply & start can save choices and start your voice", StringComparison.Ordinal) &&
                !modelHelp.Description.Contains("Stop your voice translation before switching", StringComparison.Ordinal),
                "model help explains batched choices, downloads and stopped-start saving without requiring a manual engine stop");
            Check(HelpUi.Topics.Single(topic => topic.Id == "timing").Description.Contains("on Home, then Apply changes", StringComparison.Ordinal) &&
                HelpUi.Topics.Single(topic => topic.Id == "slow").Description.Contains("then Apply changes", StringComparison.Ordinal),
                "timing and speed help make applying staged Home choices explicit");
            HelpTopic chatHelp = HelpUi.Topics.Single(topic => topic.Id == "chat");
            Check(chatHelp.Pages.Contains("Home") &&
                chatHelp.Description.Contains("Pause when my VRChat mic is muted is on by default", StringComparison.Ordinal) &&
                chatHelp.Description.Contains("edit the Home checkbox and select Apply changes", StringComparison.Ordinal) &&
                chatHelp.Description.Contains("Unmute to capture when it is on", StringComparison.Ordinal) &&
                chatHelp.Description.Contains("turning it off permits translated chat while VRChat is muted", StringComparison.Ordinal) &&
                chatHelp.Description.Contains("an already-captured sentence can still finish and send if that option is enabled", StringComparison.Ordinal),
                "chat help explains applying the default mute checkbox, capture when unmuted, its off behavior and the captured-sentence option");
            HelpTopic subtitleHelp = HelpUi.Topics.Single(topic => topic.Id == "subtitles");
            Check(subtitleHelp.Pages.Contains("Listening") && subtitleHelp.Description.Contains("Search for an application in the listening source picker", StringComparison.Ordinal) &&
                !subtitleHelp.Description.Contains("VRChat-only capture", StringComparison.Ordinal),
                "subtitle help explains the selected application picker instead of the removed VRChat-only checkbox");
            Check(subtitleHelp.Description.Contains("then Apply subtitle choices and Start subtitles", StringComparison.Ordinal) &&
                subtitleHelp.Description.Contains("Home's person-recognition toggle is staged with these choices too", StringComparison.Ordinal) &&
                subtitleHelp.Description.Contains("selected application's volume and the Windows mixer", StringComparison.Ordinal) &&
                subtitleHelp.Description.Contains("subtitles wait for it; other applications are not substituted", StringComparison.Ordinal) &&
                subtitleHelp.Description.Contains("Closing the overlay stops subtitles", StringComparison.Ordinal),
                "subtitle help explains applying its independent draft, volume checks, waiting for the chosen app and closing behavior");
            HelpTopic chineseHelp = HelpUi.Topics.Single(topic => topic.Id == "chinese-subtitles");
            Check(chineseHelp.Pages.SequenceEqual(new[] { "Home", "Listening" }) &&
                chineseHelp.Description.Contains("Optional · live subtitles", StringComparison.Ordinal) &&
                chineseHelp.Description.Contains("Languages, translators, listening source and sentence timing are all on this card.", StringComparison.Ordinal) &&
                chineseHelp.Description.Contains("Under They speak, choose Chinese", StringComparison.Ordinal) &&
                chineseHelp.Description.Contains("FireRedASR2 · Fast or FireRedASR2 · Accurate", StringComparison.Ordinal) &&
                chineseHelp.Description.Contains("Download & apply, or Apply subtitle choices", StringComparison.Ordinal) &&
                chineseHelp.Description.Contains("For Any language, choose Nemotron", StringComparison.Ordinal) &&
                chineseHelp.Description.Contains("not your microphone translator", StringComparison.Ordinal),
                "Chinese subtitle help gives actual model, language and apply actions while keeping the microphone direction independent");

            string[] introKeys = [FirstRunIntroductionUi.Title, FirstRunIntroductionUi.Description,
                FirstRunIntroductionUi.PrepareTitle, FirstRunIntroductionUi.PrepareDescription,
                "Choose your languages", FirstRunIntroductionUi.LanguagesDescription,
                FirstRunIntroductionUi.StartTitle, FirstRunIntroductionUi.StartDescription,
                FirstRunIntroductionUi.Reassurance, "Set up", "Help", "Got it", "Save introduction preference"];
            string[] onboardingKeys = ["Welcome to SayWhat?", "Your voice", "Prepare your translator", "Your voice translator is ready",
                "Choose Start on Home to send your translated voice to VRChat chat.",
                "Live subtitles and People are optional. You can set them up later.", "Change appearance",
                "Choose what you speak and what appears in VRChat chat.", "I speak",
                "Choosing your language can improve speech recognition. Choose Automatic if you regularly switch languages.",
                "Choose up to four languages. More languages share the same short VRChat message, so longer translations may be shortened.",
                "Your PC is checked locally to suggest a starting model. Hardware details are not sent anywhere.",
                "You can switch models from Home later. A larger model may improve wording but is not always faster.", "Speech recognition",
                "Memory is limited or could not be checked. Start with the smaller translator and close other heavy apps if needed.", "I speak: {0}"];
            var keys = HelpUi.Topics.SelectMany(topic => new[] { topic.Heading, topic.Description }).Concat(introKeys).Concat(onboardingKeys).Append(HelpUi.Introduction).ToArray();
            Check(keys.All(UiText.Contains), "all new user-facing guidance has catalogue entries");
            foreach (string code in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = code;
                Check(code == "en" || keys.All(key => UiText.T(key) != key), "all guidance has a real translation in " + code);
                foreach (string theme in new[] { "coral-night", "paper", "custom" })
                {
                    var appearance = original.Copy(); appearance.UiThemeId = theme;
                    if (theme == "custom") { appearance.CustomThemeBackground = "#DDE5E9"; appearance.CustomThemeTextPalette = "auto"; appearance.CustomThemeImagePath = ""; }
                    AppearanceTheme.Apply(appearance); ThemeBackground.Apply(appearance);
                    window.Navigate("Help");
                    var content = (FrameworkElement)window.Content;
                    Prepare(content, 900, 650);
                    Check(Read<TextBlock>(window, "_pageTitle").Text == UiText.T("Help"), "Help heading follows locale " + code + "/" + theme);
                    Check(Read<Button>(window, "_saveButton").Visibility == Visibility.Collapsed, "reading Help never offers Save & apply");
                    var help = Nodes(content).OfType<StackPanel>().Single(node => node.Name == "HelpPage");
                    var headings = Nodes(help).OfType<TextBlock>().Where(node => AutomationProperties.GetHeadingLevel(node) == AutomationHeadingLevel.Level2).ToArray();
                    Check(headings.Length == HelpUi.Topics.Count && headings.All(node => node.TextWrapping == TextWrapping.Wrap), "help outcomes are accessible wrapping headings");
                    Check(Nodes(content).OfType<ScrollViewer>().All(view => view.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && view.ScrollableWidth < 1), "localized Help fits a narrow window");
                    var navigationViewport = Nodes(content).OfType<ScrollViewer>().Single(view => view.Name == "SidebarNavigationScroll");
                    var sidebarFooter = Nodes(content).OfType<StackPanel>().Single(panel => panel.Name == "SidebarFooter");
                    double navigationBottom = navigationViewport.TranslatePoint(new Point(0, navigationViewport.ActualHeight), content).Y;
                    double footerTop = sidebarFooter.TranslatePoint(new Point(), content).Y;
                    Check(navigationViewport.ViewportHeight > 0 && navigationBottom <= footerTop + .5 && navigationViewport.ClipToBounds,
                        "narrow sidebar navigation is clipped to its own viewport above the fixed footer");
                    var selectedHelp = Read<Dictionary<string, Button>>(window, "_navigation")["Help"];
                    double selectedTop = selectedHelp.TranslatePoint(new Point(), navigationViewport).Y;
                    Check(selectedTop >= -.5 && selectedTop + selectedHelp.ActualHeight <= navigationViewport.ViewportHeight + .5,
                        "selected Help is brought into the narrow navigation viewport");
                    Render(content, Path.Combine(directory, "help-" + code + "-" + theme + ".png"), 900, 650);

                    Window intro = window.CreateFirstRunIntroductionDialog();
                    Check(!intro.IsVisible && !intro.IsLoaded && !intro.Topmost, "introduction factory is inert and non-topmost");
                    Check(ReferenceEquals(intro.Background, AppearanceTheme.Background), "introduction uses the selected theme background");
                    Check(intro.Title == UiText.T(FirstRunIntroductionUi.Title), "introduction title follows locale");
                    var introContent = (FrameworkElement)intro.Content;
                    Prepare(introContent, 600, 620);
                    Button[] actions = Nodes(introContent).OfType<Button>().ToArray();
                    Check(actions.Length == 3 && actions.All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))), "three introduction actions have accessible localized names");
                    Check(actions.Single(button => button.Name == "IntroductionDismissButton").IsCancel, "Escape dismisses the introduction");
                    Check(actions.All(button => !button.IsDefault), "Enter cannot unexpectedly choose a destination");
                    Check(Nodes(introContent).OfType<ScrollViewer>().All(view => view.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && view.ScrollableWidth < 1), "introduction supports longer translations without horizontal scrolling");
                    Render(introContent, Path.Combine(directory, "introduction-" + code + "-" + theme + ".png"), 600, 620);
                }
            }
            UiText.Language = "en";
            AppearanceTheme.Apply(original); ThemeBackground.Apply(original);
            window.Navigate("Help");
            var helpButtons = Nodes((FrameworkElement)window.Content).OfType<Button>().Where(button => button.Name.StartsWith("HelpLink_", StringComparison.Ordinal)).ToArray();
            int linkCount = HelpUi.Topics.Sum(topic => topic.Pages.Length);
            Check(helpButtons.Length == linkCount, "each relevant destination is an actual button");
            foreach (Button button in helpButtons)
            {
                string target = (string)button.Tag;
                Check(AutomationProperties.GetName(button) == MainWindow.PageDisplayName(target), "help link displays a personal label for " + target);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Read<string>(window, "_page") == target, "help link navigates to " + target);
                Check(!runtime.OwnRunning && !runtime.IncomingRunning, "help navigation keeps translation stopped: " + target);
            }
        }
        finally
        {
            AppearanceTheme.Apply(original); ThemeBackground.Apply(original);
            UiText.Language = language; window.Navigate(originalPage);
        }
        Check(JsonSerializer.Serialize(settings) == before, "guide factories and navigation preserve every app preference");
        Check(File.ReadAllText(AppPaths.SettingsPath) == saved && File.ReadAllText(AppPaths.FastConfigPath) == ownConfig && File.ReadAllText(AppPaths.FoxTransConfigPath) == incomingConfig, "guide previews do not write settings or microphone configuration");
        Check(!window.IsVisible && !window.IsLoaded && !runtime.OwnRunning && !runtime.IncomingRunning, "guide tests finish without a modal, audio capture or model startup");
        return checks;
    }

    private static T Read<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Prepare(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
    }
    private static void Render(FrameworkElement element, string path, int width, int height)
    {
        Prepare(element, width, height); var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); encoder.Save(output);
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>(); var seen = new HashSet<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out DependencyObject? node))
        {
            if (!seen.Add(node)) continue; yield return node;
            foreach (object child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject dependency) pending.Push(dependency);
            if (node is Visual) for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) pending.Push(VisualTreeHelper.GetChild(node, index));
        }
    }
}

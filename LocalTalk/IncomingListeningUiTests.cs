using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// Inert process/person fixtures only. Never enumerate live applications, open a
// picker window, capture audio, run a model, or change saved user preferences.
internal static class IncomingListeningUiTests
{
    internal static int Run(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool passed, string message)
        { if (!passed) throw new InvalidOperationException("Incoming listening UI: " + message); checks++; }
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, flags)!;
        var settings = (AppSettings)Field("_settings").GetValue(window)!;
        var runtime = (RuntimeController)Field("_runtime").GetValue(window)!;
        var incomingFlag = typeof(RuntimeController).GetField("_incomingRunning", flags)!;
        Check((bool)Field("_preview").GetValue(window)! && !window.IsVisible && window.Dispatcher.CheckAccess(), "unshown preview is required");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "no live runtime may be used");
        AppSettings before = settings.Copy();
        string originalLanguage = UiText.Language, pageBefore = (string)Field("_page").GetValue(window)!;
        bool busyBefore = (bool)Field("_busy").GetValue(window)!, closingBefore = (bool)Field("_closing").GetValue(window)!;
        var peopleBefore = (IReadOnlyList<SpeakerPerson>)Field("_savedPeople").GetValue(window)!;
        bool peopleLoadedBefore = (bool)Field("_savedPeopleLoaded").GetValue(window)!;
        string[] watched = [AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(AppPaths.Root, "speaker-feedback.json")];
        byte[]?[] saved = watched.Select(path => File.Exists(path) ? File.ReadAllBytes(path) : null).ToArray();
        ApplicationAudioChoice[] apps = [new(@"C:\Apps\Discord\Discord.exe", "Discord", 32),
            new(@"C:\Apps\Steam\bin\steamwebhelper.exe", "Steam", 16), new(@"C:\Apps\Settings\calls.exe", "Settings", 48)];
        SpeakerPerson[] people = [new("friend-a", "Settings", 3) { LastUsedAtUtc = DateTimeOffset.UnixEpoch.AddDays(2) },
            new("friend-b", "Mira", 2) { LastUsedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1) },
            new("friend-c", "新朋友", 1)];
        int metadataReads = 0, selections = 0;
        ApplicationAudioChoice? chosen = null; SpeakerPerson? selectedPerson = null;
        try
        {
            settings.FirstRunSetupCompleted = true; settings.IncomingAudioMode = "vrchat"; settings.CaptureVrChatOnly = true;
            settings.IncomingSinglePerson = false; settings.IncomingPersonId = "";
            Field("_savedPeople").SetValue(window, people); Field("_savedPeopleLoaded").SetValue(window, true);
            var applications = new ListeningApplicationPicker(() => { metadataReads++; return apps; }, app => { chosen = app; selections++; });
            Check(metadataReads == 0 && selections == 0, "constructing the application picker cannot enumerate or start anything");
            applications.SetApplications(apps);
            applications.Search.Text = "dIsC";
            Check(applications.VisibleApplications.Count == 1 && applications.VisibleApplications[0].Name == "Discord", "application search ignores case");
            Nodes(applications).OfType<Button>().Single(button => button.Name == "ListeningApplicationChoice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(chosen == apps[0] && selections == 1 && metadataReads == 0, "explicit application selection returns immutable path metadata only");
            applications.Search.Text = "calls.exe";
            Check(applications.VisibleApplications.Single().Name == "Settings", "search also finds an executable name");
            applications.Search.Text = "unknown";
            Check(applications.VisibleApplications.Count == 0 && Nodes(applications).OfType<TextBlock>().Any(text => text.Text == UiText.T("No matching applications.")), "empty search is explained");
            var persons = new ListeningPersonPicker(people, true, person => { selectedPerson = person; selections++; });
            persons.Search.Text = "miRA";
            Check(persons.VisiblePeople.Count == 1 && persons.VisiblePeople[0].Id == "friend-b", "person picker searches all saved names");
            Nodes(persons).OfType<Button>().Single(button => button.Name == "ListeningPersonChoice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(selectedPerson?.Id == "friend-b" && selections == 2, "person selection refers to stable ID without feedback training");
            Nodes(persons).OfType<Button>().Single(button => button.Name == "ListeningNoPersonChoice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(selectedPerson is null && selections == 3, "a one-person conversation does not require a name");
            persons.SetPeople(people.Concat(Enumerable.Range(0, 30).Select(i => new SpeakerPerson("later-" + i, "Person " + i, 0))).ToArray());
            persons.Search.Text = "Person 29";
            Check(persons.VisiblePeople.Single().Id == "later-29", "search reaches beyond the first ten people");
            for (int mask = 0; mask < 8; mask++)
            {
                bool busy = (mask & 1) != 0, closing = (mask & 2) != 0, running = (mask & 4) != 0;
                Field("_busy").SetValue(window, busy); Field("_closing").SetValue(window, closing); incomingFlag.SetValue(runtime, running);
                window.Navigate("Home");
                bool expected = !busy && !closing && !running;
                Check(MainWindow.CanChangeIncomingListening(busy, closing, running) == expected, "source guard: " + mask);
                Check(Nodes(window).OfType<Button>().Single(button => button.Name == "HomeListeningSourceButton").IsEnabled == expected, "Home source selector guard: " + mask);
                window.Navigate("Listening");
                Check(Nodes(window).OfType<ComboBox>().Single(combo => combo.Name == "IncomingListeningModePicker").IsEnabled == expected &&
                    Nodes(window).OfType<CheckBox>().Single(check => check.Name == "IncomingSinglePersonToggle").IsEnabled == expected, "page choices guard: " + mask);
                if (!expected)
                {
                    string path = settings.IncomingApplicationPath, personId = settings.IncomingPersonId;
                    Check(!window.SetListeningApplication(apps[0]) && !window.SetListeningPerson(people[0]) &&
                        settings.IncomingApplicationPath == path && settings.IncomingPersonId == personId, "stale picker selection rechecks runtime guard");
                }
            }
            Field("_busy").SetValue(window, false); Field("_closing").SetValue(window, false); incomingFlag.SetValue(runtime, false);
            window.Navigate("Listening");
            string ownLanguage = settings.OwnVoiceTargetLanguage, incomingLanguage = settings.IncomingTargetLanguage,
                ownModel = settings.OwnManagedModelId, incomingModel = settings.IncomingManagedModelId;
            bool recognition = settings.UseAiSpeakerRecognition;
            Check(window.SetListeningApplication(apps[0]) && settings.IncomingAudioMode == "application" &&
                settings.IncomingApplicationPath == apps[0].Path && settings.IncomingApplicationName == "Discord", "application choice stages the executable, never PID");
            Check(window.HasPendingSaveChanges && Nodes(window).OfType<Button>().Any(button => button.Content?.ToString() == UiText.T("Save & apply") && button.Visibility == Visibility.Visible), "staged choices expose Save & apply");
            Check(!window.SetListeningApplication(new("relative.exe", "Invalid", 0)), "invalid selections are not staged");
            settings.IncomingSinglePerson = true;
            Check(window.SetListeningPerson(people[0]) && settings.IncomingPersonId == "friend-a", "saved person is assigned by ID");
            Check(settings.UseAiSpeakerRecognition == recognition && settings.OwnVoiceTargetLanguage == ownLanguage &&
                settings.IncomingTargetLanguage == incomingLanguage && settings.OwnManagedModelId == ownModel && settings.IncomingManagedModelId == incomingModel,
                "listening choices never change recognition preference, translation languages, or models");
            window.Navigate("Listening");
            Check(Nodes(window).OfType<TextBlock>().Any(text => text.Name == "IncomingAssignedPersonName" && text.Text == "Settings"), "assigned user name is literal");
            var content = (FrameworkElement)((ContentControl)Field("_content").GetValue(window)!).Content;
            Render(content, Path.Combine(directory, "listening-source-page.png"), 800, 730);
            foreach (ThemePreset theme in AppearanceTheme.Presets)
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                var appearance = before.Copy(); appearance.UiThemeId = theme.Id; appearance.CustomThemeImagePath = "";
                AppearanceTheme.ApplyForTests(appearance); UiText.Language = language;
                var appView = new ListeningApplicationPicker(() => { metadataReads++; return apps; }, _ => selections++);
                appView.SetApplications(apps); appView.Search.Text = "Settings";
                Check(AutomationProperties.GetName(appView.Search) == UiText.T("Search applications") && appView.Search.Focusable,
                    "application search is localized and keyboard accessible: " + theme.Id + "/" + language);
                Check(Nodes(appView).OfType<TextBlock>().Any(text => text.Text == "Settings"), "application names are never translated");
                var peopleView = new ListeningPersonPicker(people, true, _ => selections++); peopleView.Search.Text = "Settings";
                Check(AutomationProperties.GetName(peopleView.Search) == UiText.T("Search people") && peopleView.Search.Focusable &&
                    Nodes(peopleView).OfType<TextBlock>().Any(text => text.Text == "Settings"), "person search preserves names in every interface language");
                foreach (FrameworkElement view in new FrameworkElement[] { appView, peopleView })
                {
                    view.Measure(new Size(360, 380)); view.Arrange(new Rect(0, 0, 360, 380)); view.UpdateLayout();
                    Check(Nodes(view).OfType<ScrollViewer>().All(scroll => scroll.ScrollableWidth < 1), "narrow picker scrolls vertically without horizontal overflow");
                    Check(Nodes(view).OfType<Button>().All(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))), "picker actions have accessible names");
                }
                if (theme.Id == before.UiThemeId && language == "en") Render(appView, Path.Combine(directory, "listening-application-picker.png"), 460, 460);
            }
            Check(metadataReads == 0 && selections == 3, "inert rendering/search did not enumerate, select, or train anything");
            for (int index = 0; index < watched.Length; index++)
                Check(saved[index] is null ? !File.Exists(watched[index]) : File.ReadAllBytes(watched[index]).SequenceEqual(saved[index]!), "staged UI leaves saved data unchanged");
        }
        finally
        {
            TranslationProfiles.CopyInto(before, settings);
            Field("_busy").SetValue(window, busyBefore); Field("_closing").SetValue(window, closingBefore); incomingFlag.SetValue(runtime, false);
            Field("_savedPeople").SetValue(window, peopleBefore); Field("_savedPeopleLoaded").SetValue(window, peopleLoadedBefore);
            AppearanceTheme.ApplyForTests(before); UiText.Language = originalLanguage; window.Navigate(pageBefore); window.RefreshSaveState();
        }
        return checks;
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency) foreach (DependencyObject node in Nodes(dependency)) yield return node;
    }

    private static void Render(FrameworkElement element, string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
}

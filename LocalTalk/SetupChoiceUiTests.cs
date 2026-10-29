using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

// Unshown preview controls only. The desktop runner must first redirect AppPaths
// to its synthetic data directory. No setup, repair, save or runtime button is clicked.
internal static class SetupChoiceUiTests
{
    internal static int Run(MainWindow window)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Setup choices UI: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, flags)
            ?? throw new InvalidOperationException("Missing setup test field: " + name);
        T Get<T>(string name) => (T)Field(name).GetValue(window)!;
        void Set(string name, object? value) => Field(name).SetValue(window, value);
        var refresh = typeof(MainWindow).GetMethod("RefreshSetupAction", flags)
            ?? throw new InvalidOperationException("Missing setup action refresh");
        void Refresh(bool needsDownload) => refresh.Invoke(window, [needsDownload]);
        var settings = Get<AppSettings>("_settings");
        var runtime = Get<RuntimeController>("_runtime");
        Check(Get<bool>("_preview") && !window.IsVisible && window.Dispatcher.CheckAccess(),
            "requires an unshown preview on its UI thread");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "both translation tools begin stopped");
        string? configured = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        string root = Path.GetFullPath(AppPaths.Root);
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check(AppPaths.IsPackagedApplication && !string.IsNullOrWhiteSpace(configured) &&
            string.Equals(root, Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(root, Path.Combine(localData, "SayWhat"), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(root, Path.Combine(localData, "LocalTalk"), StringComparison.OrdinalIgnoreCase),
            "checkpoint fixtures require the runner's redirected, non-live data root");

        AppSettings before = settings.Copy();
        string settingsJsonBefore = JsonSerializer.Serialize(settings);
        SetupSelection savedBefore = Get<SetupSelection>("_savedSetupSelection");
        string pageBefore = Get<string>("_page"), uiBefore = UiText.Language;
        string setupModelBefore = Get<string>("_setupTranslationModelId");
        object? messageBefore = Field("_setupMessage").GetValue(window);
        bool busyBefore = Get<bool>("_busy");
        string checkpoint = Path.Combine(root, "setup-progress.json");
        byte[]? checkpointBefore = File.Exists(checkpoint) ? File.ReadAllBytes(checkpoint) : null;
        var protectedFiles = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath,
            Path.Combine(root, "incoming-translator.log"), Path.Combine(root, "saywhat.log") }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path,
                path => File.Exists(path) ? File.ReadAllBytes(path) : null, StringComparer.OrdinalIgnoreCase);
        Button Action() => Get<Button>("_setupActionButton");
        TextBlock Status() => Get<TextBlock>("_setupChoiceStatus");
        TextBlock Hint() => Get<TextBlock>("_setupActionHint");
        ComboBox Picker(string label) => Nodes(window).OfType<ComboBox>().Single(box =>
            AutomationProperties.GetName(box) == UiText.T(label));
        RadioButton Profile(string label) => Nodes(window).OfType<RadioButton>().Single(box =>
            box.Content?.ToString() == UiText.T(label));
        void Expect(string action, bool enabled, string status, string hint)
        {
            Check(Action().Content?.ToString() == UiText.T(action) && Action().IsEnabled == enabled,
                "primary action: " + action + "/" + enabled + "/" + UiText.Language);
            Button header = Get<Button>("_saveButton");
            Check(header.Visibility == Visibility.Visible && header.Content?.ToString() == Action().Content?.ToString() &&
                header.IsEnabled == Action().IsEnabled, "sticky completed-setup action mirrors the primary label and enabled state");
            Check(Status().Text == UiText.T(status) && Status().Visibility == Visibility.Visible,
                "choice status: " + status + "/" + UiText.Language);
            Check(Hint().Text == UiText.T(hint) && Hint().Visibility == Visibility.Visible,
                "choice hint: " + UiText.Language);
        }
        void Baseline(string model = "hy-mt2-1.8b-q4_k_m")
        {
            settings.FirstRunSetupCompleted = true;
            settings.UseManagedTranslator = settings.ShareTranslationModel = true;
            settings.OwnManagedModelId = settings.IncomingManagedModelId = model;
            settings.ManagedRuntimeId = "llama-vulkan";
            // Explicitly exercise the new recognizer. The runner can begin with
            // migrated legacy settings, whose source field is a translation-only
            // hint rather than an audio-language selector.
            settings.OwnRecognitionEngine = "nemotron";
            settings.IncomingSpeechEngine = settings.IncomingNormalSpeechEngine = "voxtral";
            settings.IncomingNormalSourceLanguage = "Automatic";
            settings.OwnVoiceTargetLanguage = "Chinese"; settings.IncomingTargetLanguage = "English";
            settings.OwnVoiceSourceLanguage = settings.IncomingSourceLanguage = "Automatic";
            settings.OwnVoiceAdditionalLanguagesEnabled = false;
            Set("_savedSetupSelection", SetupSelection.Capture(settings));
            Set("_setupMessage", null); Set("_busy", false);
            window.Navigate("Setup"); Refresh(false);
            Check(Get<Button>("_saveButton").Visibility == Visibility.Visible, "completed setup keeps its sticky header action visible");
        }
        const string savedHint = "Your choices are saved. You can return to Home.";
        const string dirtyHint = "Save to use these choices. Returning Home does not save them.";
        const string downloadHint = "Required translation files will be downloaded before your choices are saved.";
        try
        {
            UiText.Language = "en";
            Baseline();
            Expect("Save & apply", false, "Saved", savedHint);
            Check(BindingOperations.IsDataBound(Action(), ContentControl.ContentProperty) &&
                BindingOperations.IsDataBound(Status(), TextBlock.TextProperty) &&
                BindingOperations.IsDataBound(Hint(), TextBlock.TextProperty), "dynamic action, status and hint remain localized bindings");
            var chooser = Nodes(window).OfType<Expander>().Single(item => item.Header?.ToString() == "Choose a translator");
            Check(chooser.IsExpanded, "completed setup exposes translator choices without another click");
            var other = Nodes(window).OfType<Expander>().Single(item => item.Header?.ToString() == "Other setup options");
            Check(Nodes(other).OfType<Button>().Count(button => button.Content?.ToString() == "Check & repair setup") == 1 &&
                !Nodes(chooser).OfType<Button>().Any(button => button.Content?.ToString() == "Check & repair setup"),
                "repair is a separate secondary action in Other setup options");
            Check(Profile("Fast · recommended").IsChecked == true && Profile("Larger translator").IsChecked == false,
                "saved small translator is selected");

            settings.IncomingManagedModelId = "lfm2-350m-enjp-mt-q4_k_m";
            Refresh(false);
            Expect("Save & apply", false, "Saved", savedHint);
            Check(SetupSelection.Capture(settings) == Get<SetupSelection>("_savedSetupSelection"),
                "a hidden stale incoming model is ignored while the effective model is shared");
            Profile("Larger translator").IsChecked = true;
            Check(settings.OwnManagedModelId == "hy-mt2-7b-q4_k_m" && settings.IncomingManagedModelId == settings.OwnManagedModelId &&
                settings.UseManagedTranslator && settings.ShareTranslationModel && Status().Text == "Unsaved changes",
                "model selection updates both effective IDs and refreshes unsaved status through its callback");
            RuntimeController.RuntimePlan ownPlan = RuntimeController.BuildPlan(settings.Copy(), incoming: false);
            RuntimeController.RuntimePlan incomingPlan = RuntimeController.BuildPlan(settings.Copy(), incoming: true);
            Check(ownPlan.Managed && incomingPlan.Managed && ownPlan.ModelId == "hy-mt2-7b-q4_k_m" &&
                incomingPlan.ModelId == ownPlan.ModelId && incomingPlan.Port == ownPlan.Port,
                "the edited larger selection compiles to one shared runtime plan without launching it");
            Refresh(false); Expect("Save & apply", true, "Unsaved changes", dirtyHint);
            Profile("Fast · recommended").IsChecked = true;
            Refresh(false); Expect("Save & apply", false, "Saved", savedHint);
            Check(RuntimeController.BuildPlan(settings.Copy(), incoming: false).ModelId == "hy-mt2-1.8b-q4_k_m" &&
                RuntimeController.BuildPlan(settings.Copy(), incoming: true).ModelId == "hy-mt2-1.8b-q4_k_m",
                "reverting the model also restores both pure runtime plans");

            settings.OwnManagedModelId = "removed-model";
            Set("_savedSetupSelection", SetupSelection.Capture(settings));
            window.Navigate("Setup");
            Expect("Save & apply", false, "Prepare selected translator", "Choose a translation model in Setup or Advanced.");
            Profile("Fast · recommended").IsChecked = true;
            Check(settings.OwnManagedModelId == "hy-mt2-1.8b-q4_k_m" && Action().IsEnabled && Status().Text == "Unsaved changes",
                "a valid profile repairs an unavailable saved model without invoking setup");
            Baseline(); settings.ManagedRuntimeId = "removed-runtime";
            window.Navigate("Setup");
            Expect("Save & apply", false, "Prepare selected translator", "Choose a supported translation engine in Advanced.");
            Baseline();

            Picker("Translate my voice to").SelectedItem = "Japanese";
            Check(settings.OwnVoiceTargetLanguage == "Japanese" && settings.IncomingTargetLanguage == "English" &&
                Status().Text == "Unsaved changes", "own target dropdown updates only its direction and refreshes the action");
            Picker("Translate their voices to").SelectedItem = "Korean";
            Check(settings.IncomingTargetLanguage == "Korean" && settings.OwnVoiceTargetLanguage == "Japanese" &&
                Status().Text == "Unsaved changes", "incoming target dropdown refreshes the action independently");
            Refresh(false); Expect("Save & apply", true, "Unsaved changes", dirtyHint);
            SetupSelection draft = SetupSelection.Capture(settings);
            window.Navigate("Home"); window.Navigate("Setup"); Refresh(false);
            Check(SetupSelection.Capture(settings) == draft && draft != Get<SetupSelection>("_savedSetupSelection"),
                "navigation retains a dirty draft without marking it saved");
            Expect("Save & apply", true, "Unsaved changes", dirtyHint);
            Picker("Translate my voice to").SelectedItem = "Chinese";
            Picker("Translate their voices to").SelectedItem = "English";
            Refresh(false); Expect("Save & apply", false, "Saved", savedHint);
            Refresh(true); Expect("Download & apply", true, "Prepare selected translator", downloadHint);
            Set("_busy", true); window.Navigate("Setup"); Refresh(true);
            Check(!Action().IsEnabled && !Get<StackPanel>("_setupChoicesPanel").IsEnabled &&
                !Get<ContentControl>("_content").IsEnabled, "a busy operation disables the action and all setup choices");
            Set("_busy", false);

            Baseline("lfm2-350m-enjp-mt-q4_k_m");
            Check(Nodes(window).OfType<ComboBox>().Any(box => AutomationProperties.GetName(box) == "Spoken language · my voice") &&
                Nodes(window).OfType<ComboBox>().Any(box => AutomationProperties.GetName(box) == "Spoken language · their voices") &&
                Picker("Spoken language · my voice").Items.Contains("English") && Picker("Spoken language · my voice").Items.Contains("Japanese") &&
                !Picker("Spoken language · my voice").Items.Contains("Chinese") && !Picker("Spoken language · my voice").Items.Contains("Korean") &&
                !Picker("Translate my voice to").Items.Contains("Chinese") && !Picker("Translate their voices to").Items.Contains("Korean"),
                "Liquid exposes actual supported English/Japanese source choices and restricted targets for both directions");
            Profile("Fast · recommended").IsChecked = true;
            Check(Nodes(window).OfType<ComboBox>().Any(box => AutomationProperties.GetName(box) == "Spoken language · my voice") &&
                Nodes(window).OfType<ComboBox>().Any(box => AutomationProperties.GetName(box) == "Spoken language · their voices") &&
                Picker("Spoken language · my voice").Items.Contains("Chinese") && Picker("Spoken language · my voice").Items.Contains("Korean") &&
                Picker("Spoken language · their voices").Items.Contains("Chinese") && Picker("Spoken language · their voices").Items.Contains("Korean") &&
                Picker("Spoken language · their voices").Items.Contains("Automatic") &&
                Picker("Translate my voice to").Items.Contains("Chinese") && Picker("Translate their voices to").Items.Contains("Korean"),
                "Hy keeps both speech-language selectors visible and expands supported sources and targets");
            Refresh(false); Expect("Save & apply", true, "Unsaved changes", dirtyHint);

            Baseline("translategemma-4b-q4_k_m");
            Picker("Spoken language · my voice").SelectedItem = "English";
            Check(settings.OwnVoiceSourceLanguage == "English" && settings.IncomingSourceLanguage == "Automatic" &&
                Status().Text == "Unsaved changes", "explicit own source dropdown refreshes the draft independently");
            Picker("Spoken language · their voices").SelectedItem = "Chinese";
            Check(settings.IncomingSourceLanguage == "Chinese" && settings.OwnVoiceSourceLanguage == "English" &&
                settings.IncomingSpeechEngine == "firered2-aed" && Status().Text == "Unsaved changes",
                "explicit incoming source selects compatible recognition without changing the own-voice language");
            Refresh(false); Expect("Save & apply", true, "Unsaved changes", dirtyHint);
            Check(!Picker("Spoken language · their voices").Items.Contains("Automatic"),
                "TranslateGemma cannot offer automatic input when its translator needs an explicit source");
            Picker("Spoken language · their voices").SelectedItem = "Automatic";
            Check(settings.IncomingSourceLanguage == "Chinese" && settings.OwnVoiceSourceLanguage == "English",
                "an unsupported automatic selection cannot clear either explicit source");
            Refresh(false); Expect("Save & apply", true, "Unsaved changes", dirtyHint);

            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                Baseline(); window.ChangeInterfaceLanguage(language); Refresh(false);
                Expect("Save & apply", false, "Saved", savedHint);
                Check(Profile("Fast · recommended").IsChecked == true &&
                    Nodes(window).OfType<Expander>().Any(item => item.Header?.ToString() == UiText.T("Other setup options")),
                    "profile and secondary-options labels rebind in " + language);
                Picker("Translate my voice to").SelectedItem = "Japanese";
                Refresh(false); Expect("Save & apply", true, "Unsaved changes", dirtyHint);
                Refresh(true); Expect("Download & apply", true, "Unsaved changes", downloadHint);
                Picker("Translate my voice to").SelectedItem = "Chinese";
                Refresh(true); Expect("Download & apply", true, "Prepare selected translator", downloadHint);
                if (language != "en") Check(Status().Text != "Prepare selected translator" && Hint().Text != downloadHint,
                    "new completed-setup copy has real translations in " + language);

                settings.FirstRunSetupCompleted = false;
                if (File.Exists(checkpoint)) File.Delete(checkpoint);
                window.Navigate("Setup"); Refresh(false);
                Check(Action().Content?.ToString() == UiText.T("Set up SayWhat?") && Action().IsEnabled &&
                    Status().Visibility == Visibility.Collapsed && Hint().Visibility == Visibility.Collapsed,
                    "first run retains Set up semantics without completed-choice status in " + language);
                Check(Get<Button>("_saveButton").Visibility == Visibility.Collapsed,
                    "first run keeps the unrelated sticky Save action hidden in " + language);
                Check(!Nodes(window).OfType<Button>().Any(button => button.Content?.ToString() == UiText.T("Check & repair setup")),
                    "first run does not offer a misleading repair action in " + language);
                File.WriteAllText(checkpoint, "{}"); Refresh(false);
                Check(Action().Content?.ToString() == UiText.T("Continue setup") && Action().IsEnabled,
                    "a synthetic interrupted setup checkpoint retains Continue semantics in " + language);
            }
            Check(!runtime.OwnRunning && !runtime.IncomingRunning, "every selection and localization check kept both runtimes stopped");
        }
        finally
        {
            foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.CanWrite))
                property.SetValue(settings, property.GetValue(before));
            Set("_savedSetupSelection", savedBefore); Set("_busy", busyBefore);
            Set("_setupTranslationModelId", setupModelBefore); Set("_setupMessage", messageBefore);
            if (checkpointBefore is null) { if (File.Exists(checkpoint)) File.Delete(checkpoint); }
            else File.WriteAllBytes(checkpoint, checkpointBefore);
            UiText.Language = uiBefore; window.Navigate(pageBefore);
        }
        Check(JsonSerializer.Serialize(settings) == settingsJsonBefore && Get<SetupSelection>("_savedSetupSelection") == savedBefore,
            "settings and the saved value snapshot are fully restored for subsequent tests");
        Check(Get<string>("_page") == pageBefore && UiText.Language == uiBefore && Get<bool>("_busy") == busyBefore,
            "page, interface language and operation state are restored");
        foreach (var file in protectedFiles)
            Check(file.Value is null ? !File.Exists(file.Key) : File.Exists(file.Key) && File.ReadAllBytes(file.Key).SequenceEqual(file.Value),
                "selection-only previews never rewrite " + Path.GetFileName(file.Key));
        Check(checkpointBefore is null ? !File.Exists(checkpoint) : File.ReadAllBytes(checkpoint).SequenceEqual(checkpointBefore),
            "the isolated checkpoint fixture is restored");
        return checks;
    }

    internal static int RenderPreview(MainWindow window, string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Setup choices preview: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, flags)
            ?? throw new InvalidOperationException("Missing preview field: " + name);
        T Get<T>(string name) => (T)Field(name).GetValue(window)!;
        void Set(string name, object? value) => Field(name).SetValue(window, value);
        Check(Get<bool>("_preview") && !window.IsVisible && !window.IsLoaded && window.Dispatcher.CheckAccess(),
            "requires an unshown, unloaded inert preview on its UI thread");
        var runtime = Get<RuntimeController>("_runtime");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "both translation tools remain stopped");
        string? configured = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        string root = Path.GetFullPath(AppPaths.Root);
        directory = Path.GetFullPath(directory);
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check(AppPaths.IsPackagedApplication && !string.IsNullOrWhiteSpace(configured) &&
            string.Equals(root, Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Directory.GetParent(root)?.FullName, directory, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(root, Path.Combine(localData, "SayWhat"), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(root, Path.Combine(localData, "LocalTalk"), StringComparison.OrdinalIgnoreCase),
            "images are written only into the redirected test fixture");
        var settings = Get<AppSettings>("_settings");
        AppSettings before = settings.Copy();
        string settingsJsonBefore = JsonSerializer.Serialize(settings);
        SetupSelection savedBefore = Get<SetupSelection>("_savedSetupSelection");
        string pageBefore = Get<string>("_page"), uiBefore = UiText.Language;
        string setupModelBefore = Get<string>("_setupTranslationModelId");
        object? messageBefore = Field("_setupMessage").GetValue(window);
        bool busyBefore = Get<bool>("_busy");
        var content = (FrameworkElement)window.Content;
        Size priorSize = content.RenderSize;
        var protectedFiles = new[] { AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath }
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        var metrics = new List<string>();
        void DrainPresentation()
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        void Layout(Size size)
        {
            // Unlike a displayed Window, this root has no normal sizing/render loop.
            // A prior narrow render can leave templated ScrollViewer metrics stale.
            foreach (UIElement element in LayoutNodes(content).OfType<UIElement>())
            { element.InvalidateMeasure(); element.InvalidateArrange(); }
            content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
        }
        try
        {
            settings.FirstRunSetupCompleted = true;
            settings.UseManagedTranslator = settings.ShareTranslationModel = true;
            settings.OwnManagedModelId = settings.IncomingManagedModelId = "hy-mt2-1.8b-q4_k_m";
            settings.ManagedRuntimeId = "llama-vulkan";
            settings.OwnVoiceTargetLanguage = "Chinese"; settings.IncomingTargetLanguage = "English";
            settings.OwnVoiceSourceLanguage = settings.IncomingSourceLanguage = "Automatic";
            settings.OwnVoiceAdditionalLanguagesEnabled = false;
            Set("_savedSetupSelection", SetupSelection.Capture(settings));
            Set("_setupMessage", null); Set("_busy", false);
            UiText.Language = "en";
            settings.OwnVoiceTargetLanguage = "Japanese";
            window.Navigate("Setup");
            typeof(MainWindow).GetMethod("RefreshSetupAction", flags)!.Invoke(window, [false]);
            Button action = Get<Button>("_setupActionButton");
            Check(action.IsEnabled && action.Content?.ToString() == "Save & apply" &&
                Get<TextBlock>("_setupChoiceStatus").Text == "Unsaved changes", "cached dirty choices visibly offer Save & apply");
            foreach (Size size in new[] { new Size(1160, 800), new Size(900, 650) })
            {
                Layout(size); MainWindow.ApplySlimScrollbars(content); DrainPresentation();
                Layout(size); DrainPresentation(); Layout(size);
                ScrollViewer scroll = Nodes(content).OfType<ScrollViewer>().Single(view => ReferenceEquals(view.Content, Get<ContentControl>("_content")));
                Check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth < 1 &&
                    content.ActualWidth <= size.Width + .5, "completed setup has no horizontal overflow at " + size);
                // Expanded model choices may legitimately need vertical scrolling. Make
                // the action visible instead of incorrectly requiring the entire page to fit.
                scroll.ScrollToBottom();
                // ScrollViewer queues its scroll command. An unshown preview has no
                // normal render loop, so drain the same presentation queue as the
                // other inert UI tests before measuring the resulting viewport.
                DrainPresentation(); Layout(size); DrainPresentation(); content.UpdateLayout();
                Rect actionBounds = action.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), action.RenderSize));
                bool lowerActionVisible = action.ActualWidth > 0 && action.ActualHeight >= 40 && actionBounds.Left >= -.5 &&
                    actionBounds.Right <= scroll.ActualWidth + .5 && actionBounds.Top >= -.5 && actionBounds.Bottom <= scroll.ActualHeight + .5;
                Button header = Get<Button>("_saveButton");
                Rect headerBounds = header.TransformToAncestor(content).TransformBounds(new Rect(new Point(), header.RenderSize));
                string detail = FormattableString.Invariant($"{size.Width:0}x{size.Height:0}: actionBounds={actionBounds.X:0.0},{actionBounds.Y:0.0},{actionBounds.Width:0.0},{actionBounds.Height:0.0}; actionSize={action.ActualWidth:0.0}x{action.ActualHeight:0.0}; scrollActual={scroll.ActualWidth:0.0}x{scroll.ActualHeight:0.0}; viewport={scroll.ViewportWidth:0.0}x{scroll.ViewportHeight:0.0}; extent={scroll.ExtentWidth:0.0}x{scroll.ExtentHeight:0.0}; verticalOffset={scroll.VerticalOffset:0.0}; scrollableHeight={scroll.ScrollableHeight:0.0}; scrollableWidth={scroll.ScrollableWidth:0.0}; observedLowerActionVisible={lowerActionVisible}; stickyHeaderBounds={headerBounds.X:0.0},{headerBounds.Y:0.0},{headerBounds.Width:0.0},{headerBounds.Height:0.0}. Lower-button scrolling is diagnostic only in this unshown preview.");
                metrics.Add(detail);
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string name = size.Width == 1160 ? "setup-model-change.png" : "setup-model-change-narrow.png";
                using (var file = File.Create(Path.Combine(directory, name))) encoder.Save(file);
                checks++;
                File.WriteAllLines(Path.Combine(directory, "setup-model-change-layout.txt"), metrics);
                Check(header.Visibility == Visibility.Visible && header.IsEnabled && header.Content?.ToString() == "Save & apply" &&
                    header.ActualWidth > 0 && header.ActualHeight >= 40 && headerBounds.Left >= -.5 && headerBounds.Top >= -.5 &&
                    headerBounds.Right <= size.Width + .5 && headerBounds.Bottom <= size.Height + .5,
                    "the sticky Save & apply action is fully visible without depending on unshown scrolling; " + detail);
            }
            File.WriteAllLines(Path.Combine(directory, "setup-model-change-layout.txt"), metrics);
        }
        finally
        {
            foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.CanWrite))
                property.SetValue(settings, property.GetValue(before));
            Set("_savedSetupSelection", savedBefore); Set("_busy", busyBefore);
            Set("_setupTranslationModelId", setupModelBefore); Set("_setupMessage", messageBefore);
            UiText.Language = uiBefore; window.Navigate(pageBefore);
            if (priorSize.Width > 0 && priorSize.Height > 0)
            { content.Measure(priorSize); content.Arrange(new Rect(new Point(), priorSize)); content.UpdateLayout(); }
        }
        Check(JsonSerializer.Serialize(settings) == settingsJsonBefore && Get<SetupSelection>("_savedSetupSelection") == savedBefore &&
            Get<string>("_page") == pageBefore && UiText.Language == uiBefore && Get<bool>("_busy") == busyBefore,
            "preview restores every settings choice, saved snapshot, page and mutable flag");
        foreach (var file in protectedFiles)
            Check(file.Value is null ? !File.Exists(file.Key) : File.Exists(file.Key) && File.ReadAllBytes(file.Key).SequenceEqual(file.Value),
                "preview never saves " + Path.GetFileName(file.Key));
        Check(!window.IsLoaded && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning,
            "rendering kept all live tools inert");
        return checks;
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node) foreach (DependencyObject descendant in Nodes(node)) yield return descendant;
    }

    private static IEnumerable<DependencyObject> LayoutNodes(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out DependencyObject? node))
        {
            if (!seen.Add(node)) continue;
            yield return node;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject dependency) pending.Push(dependency);
            if (node is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) pending.Push(VisualTreeHelper.GetChild(node, index));
        }
    }
}

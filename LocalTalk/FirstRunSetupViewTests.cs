using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IncomingTranslator;

// Every backend below is a synthetic delegate. These tests never show a window,
// change Windows features, save preferences, download assets, open audio or load models.
internal static class FirstRunSetupViewTests
{
    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool valid, string explanation)
        { if (!valid) throw new InvalidOperationException("Guided setup view: " + explanation); checks++; }
        string languageBefore = UiText.Language;
        var appearanceBefore = SettingsStore.Load().Copy();
        SynchronizationContext? contextBefore = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Directory.CreateDirectory(directory);
        string[] protectedPaths = [AppPaths.SettingsPath, AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath];
        var protectedFiles = protectedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path,
            path => File.Exists(path) ? File.ReadAllBytes(path) : null, StringComparer.OrdinalIgnoreCase);

        try
        {
            int recommendations = 0, inspections = 0, preparations = 0, completions = 0, exits = 0, previews = 0;
            AppSettings? completed = null;
            var seed = new AppSettings { UiLanguage = "en", UseManagedTranslator = false, ShareTranslationModel = false,
                ManagedRuntimeId = "llama-cuda",
                OwnVoiceTargetLanguage = "Chinese", IncomingTargetLanguage = "English", TranslationQuality = "Careful",
                OwnVoiceAdditionalLanguagesEnabled = true, OwnVoiceAdditionalTargetLanguages = ["Japanese", "Korean"],
                OwnVoiceSourceLanguage = "English", IncomingSourceLanguage = "Chinese", UseAiSpeakerRecognition = false };
            string seedBefore = JsonSerializer.Serialize(seed);
            using var view = new FirstRunSetupView(seed,
                _ => { recommendations++; return Task.FromResult<string?>(FirstRunSetupView.SmallModelId); },
                (_, _) => { inspections++; return Task.FromResult(new AutomaticSetupPlan("fake", 2_400_000_000, true, true) { SpeechSetupRequired = true }); },
                (draft, _, progress, _) =>
                {
                    preparations++; Check(draft.OwnManagedModelId == FirstRunSetupView.LargeModelId && draft.ShareTranslationModel && draft.UseManagedTranslator,
                        "chosen model is shared for both translation directions");
                    progress.Report(new("download", "Downloading translation files", 100, 400) { StepIndex = 2, StepCount = 4 });
                    return Task.FromResult(new AutomaticSetupResult(true, false, "fake complete"));
                },
                draft => { completions++; completed = draft; }, () => exits++,
                draft => { previews++; AppearanceTheme.Apply(draft); ThemeBackground.Apply(draft); });
            Check(view.StepIndex == 0 && !view.IsBusy && recommendations == 0 && inspections == 0 && preparations == 0,
                "construction shows language without inspecting hardware or preparing anything");
            Check(!ReferenceEquals(view.Draft, seed) && JsonSerializer.Serialize(seed) == seedBefore,
                "input settings are copied rather than edited");
            Check(!Nodes(view).Any(node => node is Window) && !Nodes(view).OfType<Button>().Any(button =>
                button.Content?.ToString() is "Home" or "Advanced" or "Save & apply"), "dedicated setup contains no dashboard navigation");
            Radio(view, "日本語").IsChecked = true;
            Check(view.Draft.UiLanguage == "ja" && UiText.Language == "ja" && previews == 1,
                "native language labels apply the preview immediately without saving");
            ButtonFor(view, "Exit setup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(exits == 1 && completions == 0 && preparations == 0, "exiting early delegates to host without completing or preparing");

            Nodes(view).OfType<RadioButton>().Single(item => AutomationProperties.GetName(item) == UiText.T("Paper")).IsChecked = true;
            Check(view.Draft.UiThemeId == "paper" && !AppearanceTheme.IsDark && previews == 2,
                "theme changes preview light foreground/background roles");
            Border selectedThemeTile = Nodes(view).OfType<Border>().Single(tile => tile.Name == "SetupThemeTile_paper");
            Check(ReferenceEquals(selectedThemeTile.Background, ModernTheme.Surface) && ReferenceEquals(selectedThemeTile.BorderBrush, ModernTheme.Accent) &&
                selectedThemeTile.CornerRadius.TopLeft == 12 && selectedThemeTile.Child is RadioButton,
                "theme choices are rounded accessible tiles with a selected accent outline");
            Pump(view.AdvanceAsync()); Check(view.StepIndex == 1 && recommendations == 0, "voice languages precede hardware/model choice and appearance is optional");
            Check(!Nodes(view).OfType<ComboBox>().Any(), "spoken and output languages are visible choices, not hidden dropdowns");
            Radio(view, UiText.T("English")).IsChecked = true;
            ToggleLanguage(view, "Korean", false); ToggleLanguage(view, "Thai", true); ToggleLanguage(view, "English", true);
            ToggleLanguage(view, "Spanish", true);
            Check(view.Draft.EffectiveOwnVoiceTargetLanguages().SequenceEqual(["Chinese", "Japanese", "Thai", "English"]) &&
                view.Draft.IncomingTargetLanguage == "English" && view.Draft.OwnVoiceSourceLanguage == "English",
                "up to four output languages including Thai retain order with English speech and independent subtitles");
            Pump(view.AdvanceAsync());
            Check(view.StepIndex == 2 && recommendations == 1 && inspections == 0 && preparations == 0,
                "entering model choice performs one metadata check but no download inspection");
            Check(Nodes(view).OfType<RadioButton>().Count(item => item.GroupName == "FirstRunModel") == 2 &&
                Nodes(view).OfType<RadioButton>().Count(item => item.GroupName == "SetupSpeechRecognition") == OwnRecognitionCatalog.Choices.Count &&
                view.RecommendedModelId == FirstRunSetupView.SmallModelId, "translator and speech recognition choices are shown directly");
            Radio(view, "HY-MT2 7B").IsChecked = true;
            Pump(view.AdvanceAsync());
            Check(view.StepIndex == 2 && inspections == 0 && preparations == 0 && !ButtonFor(view, "Next").IsEnabled,
                "non-recommended choice requires explicit confirmation before preparation");
            ButtonFor(view, "Use my choice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
            Check(view.StepIndex == 2 && inspections == 1 && preparations == 0 && completions == 0,
                "confirmed choice enters the read-only download/permission review");
            Check(ButtonFor(view, "Prepare translation").IsEnabled &&
                !Nodes(view).OfType<Button>().Any(button => button.Content?.ToString() == UiText.T("Use my choice")),
                "accepting a different model dismisses its confirmation and exposes a separate explicit preparation action");
            Check(Nodes(view).OfType<TextBlock>().Any(text => text.Text == UiText.T("Windows may ask for permission to prepare speech recognition.")),
                "Windows permission notice appears before explicit preparation consent");
            Check(!Nodes(view).OfType<TextBlock>().Any(text => text.Text == UiText.T("Preparing speech recognition may need extra downloads and at least 20 GB of free space.")),
                "native speech preparation does not display the legacy Linux build space warning");
            Check(view.Draft.OwnVoiceSourceLanguage == "English" && view.Draft.IncomingSourceLanguage == "Chinese" &&
                view.Draft.TranslationQuality == "Careful" && view.Draft.OwnVoiceAdditionalTargetLanguages.SequenceEqual(["Japanese", "Thai", "English"]),
                "model choice preserves explicit spoken language, advanced quality, and all selected outputs");
            Check(view.Draft.ManagedRuntimeId == "llama-cuda", "running setup again preserves a supported advanced CUDA runtime");
            Pump(view.AdvanceAsync());
            Check(view.StepIndex == 3 && preparations == 1 && completions == 0 && !view.IsBusy,
                "explicit preparation reaches ready but does not enter Home automatically");
            Pump(view.AdvanceAsync()); Pump(view.AdvanceAsync());
            Check(completions == 1 && completed is not null && completed.OwnManagedModelId == FirstRunSetupView.LargeModelId &&
                completed.EffectiveOwnVoiceTargetLanguages().SequenceEqual(["Chinese", "Japanese", "Thai", "English"]),
                "Home completes once with a copy of the final draft");
            Check(JsonSerializer.Serialize(seed) == seedBefore, "finished flow leaves supplied seed settings untouched");

            // Permission and restart notices follow the inspected work, not a
            // blanket assumption that every speech engine changes Windows.
            foreach (string engine in new[] { "nemotron", "voxtral" })
            foreach (bool administrator in new[] { false, true })
            foreach (bool restart in new[] { false, true })
            {
                int reviewInspections = 0, unexpectedPreparations = 0;
                using var review = new FirstRunSetupView(new AppSettings
                    { OwnRecognitionEngine = engine, OwnVoiceSourceLanguage = engine == "voxtral" ? "Automatic" : "English" },
                    _ => Task.FromResult<string?>(FirstRunSetupView.SmallModelId),
                    (_, _) =>
                    {
                        reviewInspections++;
                        return Task.FromResult(new AutomaticSetupPlan("synthetic review", 0, administrator, restart)
                            { SpeechSetupRequired = true });
                    },
                    (_, _, _, _) =>
                    {
                        unexpectedPreparations++;
                        throw new InvalidOperationException("Review-only fixture must never prepare tools.");
                    }, _ => throw new InvalidOperationException("Review-only fixture must never complete setup."), () => { }, _ => { });
                ReachReview(review);
                Check(reviewInspections == 1 && unexpectedPreparations == 0 && !review.IsBusy && review.StepIndex == 2,
                    "inspecting a " + engine + " plan cannot trigger preparation");
                bool Shows(string key) => Nodes(review).OfType<TextBlock>().Any(text => text.Text == UiText.T(key));
                Check(Shows("Windows may ask for permission to prepare speech recognition.") == administrator,
                    "permission notice exactly follows the inspected plan for " + engine);
                Check(Shows("A Windows restart may be needed. You can continue setup afterward.") == restart,
                    "restart notice exactly follows the inspected plan for " + engine);
                Check(Shows("Preparing speech recognition may need extra downloads and at least 20 GB of free space.") == (engine == "voxtral"),
                    "only legacy speech preparation shows Linux build-space requirements");
                Check(ButtonFor(review, "Prepare translation").IsEnabled &&
                    Shows("No microphone is opened and no AI model is loaded during setup."),
                    "review keeps the explicit-start boundary visible before preparation for " + engine);
            }

            // All screens remain themed/localized at the supported narrow window size.
            foreach (string code in new[] { "en", "zh-CN", "ja", "ko" })
            foreach (ThemePreset theme in AppearanceTheme.Presets)
            {
                var visualSeed = new AppSettings { UiLanguage = code, UiThemeId = theme.Id, UseAiSpeakerRecognition = false,
                    OwnVoiceSourceLanguage = "English", OwnVoiceTargetLanguage = "Japanese", OwnVoiceAdditionalLanguagesEnabled = true,
                    OwnVoiceAdditionalTargetLanguages = ["Chinese", "Thai"] };
                UiText.Language = code; AppearanceTheme.Apply(visualSeed); ThemeBackground.Apply(visualSeed);
                using var visual = new FirstRunSetupView(visualSeed,
                    _ => Task.FromResult<string?>(FirstRunSetupView.SmallModelId),
                    (_, _) => Task.FromResult(new AutomaticSetupPlan("fake", 0, false, false)),
                    (_, _, _, _) => Task.FromResult(new AutomaticSetupResult(true, false, "")),
                    _ => { }, () => { }, _ => { });
                for (int screen = 0; screen < 4; screen++)
                {
                    Layout(visual);
                    Check(ReferenceEquals(visual.Background, ModernTheme.Background), "surface uses stable theme brush " + code + "/" + theme.Id);
                    Check(Nodes(visual).OfType<ScrollViewer>().All(scroll => scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth < 1),
                        "no horizontal scrolling on screen " + screen + "/" + code + "/" + theme.Id);
                    Button next = Nodes(visual).OfType<Button>().Single(button => button.Name == "FirstRunNext");
                    Point position = next.TranslatePoint(new Point(), visual);
                    Check(next.ActualWidth >= 100 && position.X >= 0 && position.Y >= 0 &&
                        position.X + next.ActualWidth <= 900.5 && position.Y + next.ActualHeight <= 650.5,
                        "primary action stays visible at narrow size " + screen + "/" + code + "/" + theme.Id);
                    Check(Nodes(visual).OfType<TextBlock>().Where(text => text.Text.Length > 0 && !(bool)text.GetValue(UiLocalization.LiteralProperty))
                        .All(text => System.Windows.Data.BindingOperations.IsDataBound(text, TextBlock.TextProperty)), "functional labels remain localized bindings");
                    if (screen == 1)
                    {
                        CheckBox thai = Nodes(visual).OfType<CheckBox>().Single(box => box.Content?.ToString() == UiText.T("Thai"));
                        Check(thai.IsChecked == true && AutomationProperties.GetName(thai) == UiText.T("Translate my voice to") + ": " + UiText.T("Thai") &&
                            !Nodes(visual).OfType<RadioButton>().Any(radio => radio.GroupName == "SetupSpokenLanguage" && radio.Content?.ToString() == UiText.T("Thai")),
                            "setup offers localized Thai output with English speech, without unsupported Thai speech: " + code + "/" + theme.Id);
                    }
                    if ((theme.Id == "coral-night" && code == "en") || (theme.Id == "paper" && code == "ja"))
                        Render(visual, Path.Combine(directory, $"setup-{screen}-{code}-{theme.Id}.png"));
                    if (screen == 2) Pump(visual.AdvanceAsync()); // Read-only download review within preparation.
                    if (screen < 3) Pump(visual.AdvanceAsync());
                }
            }

            // Cancellation cannot complete setup, and a retry reuses the same host.
            var pending = new TaskCompletionSource<AutomaticSetupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken capturedToken = default;
            int retryCalls = 0, cancelCompletions = 0;
            using var cancelView = BasicView((_, _, progress, token) =>
            {
                retryCalls++; capturedToken = token;
                progress.Report(new("download", "Downloading translation files", 200, 1000));
                if (retryCalls > 1) return Task.FromResult(new AutomaticSetupResult(true, false, ""));
                token.Register(() => pending.TrySetCanceled(token)); return pending.Task;
            }, _ => cancelCompletions++);
            ReachReview(cancelView); Task preparing = cancelView.AdvanceAsync(); Drain();
            Check(cancelView.IsBusy && !preparing.IsCompleted, "in-flight preparation keeps the view busy");
            ProgressBar bar = Nodes(cancelView).OfType<ProgressBar>().Single();
            Check(!bar.IsIndeterminate && Math.Abs(bar.Value - .2) < .001, "progress uses measured download bytes rather than invented percent");
            Check(ButtonFor(cancelView, "Cancel preparation").IsEnabled && !ButtonFor(cancelView, "Back").IsEnabled,
                "cancel remains available while settings/navigation stay frozen");
            cancelView.Cancel(); Check(capturedToken.IsCancellationRequested, "cancel reaches the backend token immediately");
            Pump(preparing);
            Check(!cancelView.IsBusy && cancelView.StepIndex == 2 && cancelCompletions == 0,
                "cancel leaves a resumable review and never completes setup");
            Pump(cancelView.AdvanceAsync());
            Check(cancelView.StepIndex == 3 && retryCalls == 2, "explicit retry resumes through the same backend without losing choices");

            int restartCalls = 0;
            using var restartView = BasicView((_, _, _, _) => Task.FromResult(++restartCalls == 1
                ? new AutomaticSetupResult(false, true, "fake restart") : new AutomaticSetupResult(true, false, "")));
            ReachReview(restartView); Pump(restartView.AdvanceAsync());
            Check(restartView.StepIndex == 2 && ButtonFor(restartView, "Check again").IsEnabled,
                "restart-required flow stays resumable without a fake finish");
            Pump(restartView.AdvanceAsync()); Check(restartView.StepIndex == 3 && restartCalls == 2, "resume rechecks through the setup backend");

            int failedCalls = 0;
            using var failureView = BasicView((_, _, _, _) => ++failedCalls == 1
                ? Task.FromException<AutomaticSetupResult>(new HttpRequestException("private diagnostic must not be displayed"))
                : Task.FromResult(new AutomaticSetupResult(true, false, "")));
            ReachReview(failureView); Pump(failureView.AdvanceAsync());
            Check(!Nodes(failureView).OfType<TextBlock>().Any(text => text.Text.Contains("private diagnostic", StringComparison.Ordinal)) &&
                ButtonFor(failureView, "Try again").IsEnabled, "failure is sanitized and offers an explicit retry");
            Pump(failureView.AdvanceAsync()); Check(failureView.StepIndex == 3 && failedCalls == 2, "download errors can be retried without a restart");

            using var lowMemory = new FirstRunSetupView(new AppSettings { ManagedRuntimeId = "removed-runtime" }, _ => Task.FromResult<string?>(null),
                (_, _) => Task.FromResult(new AutomaticSetupPlan("fake", 0, false, false)),
                (_, _, _, _) => Task.FromResult(new AutomaticSetupResult(true, false, "")), _ => { }, () => { }, _ => { });
            Pump(lowMemory.AdvanceAsync()); Pump(lowMemory.AdvanceAsync());
            Check(Nodes(lowMemory).OfType<TextBlock>().Any(text => text.Text == UiText.T("Memory is limited or could not be checked. Start with the smaller translator and close other heavy apps if needed.")) &&
                !Nodes(lowMemory).OfType<TextBlock>().Any(text => text.Text == UiText.T("Recommended for this PC")),
                "insufficient-memory metadata produces a caution, not an unsupported recommendation badge");
            Pump(lowMemory.AdvanceAsync());
            Check(lowMemory.Draft.ManagedRuntimeId == "llama-vulkan", "an unavailable advanced runtime falls back to the bundled Vulkan default");

            foreach ((string path, byte[]? before) in protectedFiles)
                Check(before is null ? !File.Exists(path) : File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(before),
                    "inert first-run tests leave protected settings/config files unchanged");
        }
        finally
        {
            UiText.Language = languageBefore; AppearanceTheme.Apply(appearanceBefore); ThemeBackground.Apply(appearanceBefore);
            SynchronizationContext.SetSynchronizationContext(contextBefore);
        }
        return checks;
    }

    private static FirstRunSetupView BasicView(
        Func<AppSettings, AutomaticSetupPlan, IProgress<ProvisionProgress>, CancellationToken, Task<AutomaticSetupResult>> prepare,
        Action<AppSettings>? complete = null) => new(new AppSettings { UiLanguage = "en" },
            _ => Task.FromResult<string?>(FirstRunSetupView.SmallModelId),
            (_, _) => Task.FromResult(new AutomaticSetupPlan("fake", 1000, false, false)), prepare,
            complete ?? (_ => { }), () => { }, _ => { });

    private static void ReachReview(FirstRunSetupView view)
    { for (int i = 0; i < 3; i++) Pump(view.AdvanceAsync()); }
    private static void ToggleLanguage(DependencyObject root, string language, bool selected)
    {
        CheckBox item = Nodes(root).OfType<CheckBox>().Single(value => value.Content?.ToString() == UiText.T(language));
        item.IsChecked = selected; item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static RadioButton Radio(DependencyObject root, string name) => Nodes(root).OfType<RadioButton>().Single(item =>
        item.Content?.ToString() == name || AutomationProperties.GetName(item) == name);
    private static Button ButtonFor(DependencyObject root, string english) => Nodes(root).OfType<Button>().Single(button => button.Content?.ToString() == UiText.T(english));
    private static ComboBox Picker(DependencyObject root, string english) => Nodes(root).OfType<ComboBox>().Single(button => AutomationProperties.GetName(button) == UiText.T(english));
    private static void Pump(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Drain();
        if (!task.IsCompleted) throw new TimeoutException("Synthetic setup operation did not finish.");
        task.GetAwaiter().GetResult(); Drain();
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Layout(FrameworkElement element)
    {
        element.InvalidateMeasure(); element.InvalidateArrange();
        element.Measure(new Size(900, 650)); element.Arrange(new Rect(0, 0, 900, 650)); element.UpdateLayout(); Drain();
    }
    private static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(900, 650, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject descendant in Nodes(dependency)) yield return descendant;
    }
}

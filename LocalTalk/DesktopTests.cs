using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

internal static class DesktopTests
{
    public static void Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        // An isolated workspace data directory: never migrate/save the user's live settings.
        Environment.SetEnvironmentVariable(AppBrand.DataEnvironmentVariable, Path.Combine(directory,"data"));
        int checks = 0;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ModernTheme.Apply(app);
            AppPaths.InitializePackagedData();
            var window = new MainWindow(preview:true);
            Require(!window.Topmost,"Dashboard is not always on top");
            Require(SettingsStore.Load().OwnVoiceTargetLanguage == "Chinese","Own voice default remains Chinese");
            Require(SettingsStore.Load().IncomingTargetLanguage == "English","Incoming default remains English");
            Require(SettingsStore.Load().OwnVoiceEndPauseMilliseconds == 1200 && !SettingsStore.Load().OwnVoiceSendEarlyPreviews,
                "New users get a 1.2-second finishing pause and finished sentences by default");
            var legacyDefault = SettingsStore.ReadSettings("{\"OwnVoiceEndPauseMilliseconds\":2400,\"OwnVoiceTargetLanguage\":\"Chinese\",\"IncomingTargetLanguage\":\"English\"}");
            Require(legacyDefault.OwnVoiceEndPauseMilliseconds == 1200 && !legacyDefault.OwnVoiceSendEarlyPreviews &&
                legacyDefault.OwnVoiceTargetLanguage == "Chinese" && legacyDefault.IncomingTargetLanguage == "English",
                "Old default pause migrates on new-version load without changing translation languages");
            Require(SettingsStore.ReadSettings("{\"OwnVoiceEndPauseMilliseconds\":1800}").OwnVoiceEndPauseMilliseconds == 1800,
                "Legacy customized sentence pause is not overwritten");
            Require(SettingsStore.ReadSettings("{\"OwnVoiceEndPauseMilliseconds\":2400,\"OwnVoiceSendEarlyPreviews\":false}") is
                { OwnVoiceEndPauseMilliseconds: 2400, OwnVoiceSendEarlyPreviews: false },
                "Saved final-only choice and longer pause survive later loads");
            var finalOnly = new AppSettings { OwnVoiceSendEarlyPreviews = false, OwnVoiceEndPauseMilliseconds = 2100 };
            Require(finalOnly.Copy() is { OwnVoiceSendEarlyPreviews: false, OwnVoiceEndPauseMilliseconds: 2100 },
                "Runtime snapshots copy both chat delivery preferences");
            Require(SettingsStore.ReadSettings(System.Text.Json.JsonSerializer.Serialize(finalOnly)) is
                { OwnVoiceSendEarlyPreviews: false, OwnVoiceEndPauseMilliseconds: 2100 },
                "Chat delivery settings persist without repeated default migration");
            Require(!File.Exists(Path.Combine(AppPaths.Root,"incoming-translator.log")),"No incoming capture started");
            var unchanged = SettingsStore.Load();
            using (var heldSettings = File.Open(AppPaths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var heldConfig = File.Open(AppPaths.FastConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SettingsStore.Save(unchanged);
                Require(true,"Starting a second feature can save identical settings with the first host reading them");
            }
            checks+=RuntimeController.RunOfflineSelfTests();
            checks+=RuntimeController.RunVoiceFirstOfflineChecks();
            checks+=NativeSpeechProvisionerTests.Run();
            checks+=FireRedSpeechTests.Run();
            checks+=RecognitionTextFilterTests.Run();
            checks+=ChineseSubtitleFilterTests.Run();
            checks+=IncomingChineseOnlyPreferenceTests.Run();
            checks+=Task.Run(() => IncomingSourceFilterPipelineTests.RunAsync(Path.Combine(directory, "incoming-source-filter"))).GetAwaiter().GetResult();
            checks+=Task.Run(() => IncomingTranslationService.RunRecognitionControlOfflineChecksAsync(Path.Combine(directory, "recognition-controls"))).GetAwaiter().GetResult();
            checks+=Task.Run(() => IncomingNativeSpeechTests.RunAsync(Path.Combine(directory, "incoming-native-speech"))).GetAwaiter().GetResult();
            checks+=TranslationProfileTests.Run();
            checks+=ProfileSettingsRuntimeTests.Run();
            checks+=TranslationProfilesUiTests.Run(window);
            checks+=TranslationProfilesUiTests.RenderPreview(window, directory);
            checks+=VibeVoiceRuntimeTests.Run(directory);
            checks+=RuntimeProfiles.RunOfflineChecks();
            checks+=IncomingListeningTests.Run();
            checks+=Task.Run(() => IncomingTranslationService.RunSinglePersonListeningChecksAsync(Path.Combine(directory, "single-person-listening"))).GetAwaiter().GetResult();
            checks+=Task.Run(() => IncomingProfilePipelineTests.RunAsync(Path.Combine(directory, "profile-pipeline"))).GetAwaiter().GetResult();
            checks+=Task.Run(() => GroupSpeechProtocolTests.RunAsync(Path.Combine(directory, "group-routing"))).GetAwaiter().GetResult();
            Progress("Profiles and local group routing");
            checks+=DiagnosticsTests.Run(window, directory);
            checks+=OwnVoiceLanguageTests.Run(window, directory);
            checks+=HomeTargetLanguageUiTests.Run(window, directory);
            checks+=VoiceFirstHomeUiTests.Run(window, directory);
            checks+=IncomingListeningUiTests.Run(window, directory);
            checks+=SubtitleSpeechUiTests.Run(window, directory);
            checks+=HomeSubtitleUiTests.Run(window, directory);
            checks+=IncomingCaptureCapabilityTests.RunUi(window);
            checks+=Task.Run(() => IncomingCaptureCapabilityTests.RunRuntimeAsync(Path.Combine(directory, "windows-capture-capabilities"))).GetAwaiter().GetResult();
            checks+=SaveStateUiTests.Run(window);
            checks+=SpecializedTranslationModelTests.Run(directory);
            checks+=Task.Run(() => TimingDefaultsTests.RunAsync(directory)).GetAwaiter().GetResult();
            checks+=Task.Run(() => VrchatOscConnectionTests.RunAsync()).GetAwaiter().GetResult();
            checks+=OscConnectionUiTests.Run(window);
            checks+=UpdateUiTests.Run(window);
            checks+=AppUpdateChecker.RunOfflineChecks();
            checks+=Task.Run(() => AppUpdateTests.RunOfflineChecksAsync(Path.Combine(directory, "update-fixtures"))).GetAwaiter().GetResult();
            checks+=LocalTalkLaunchOptions.RunOfflineSelfTests();
            checks+=InstallerUiLaunch.RunOfflineChecks();
            checks+=InstallerShutdownOfflineTests.RunAsync().GetAwaiter().GetResult();
            checks+=DesktopLifetime.RunOfflineSelfTests();
            checks+=DesktopLifetimeOfflineTests.RunDispatcherChecks(Path.Combine(directory, "lifetime-fixtures"));
            checks+=SubtitleWindow.RunAlwaysOnTopOfflineSelfTests();
            checks+=SubtitlePinOfflineTests.Run();
            checks+=SubtitleAppearancePreferenceTests.Run();
            checks+=EarlyTranslationPreferenceTests.Run();
            checks+=SubtitleTextSurfaceTests.Run();
            checks+=SubtitleActionSurfaceTests.Run();
            checks+=CompactSubtitleUiTests.Run(directory);
            checks+=CompactPersonCorrectionTests.Run(Path.Combine(directory, "compact-person-correction"));
            checks+=SubtitleWindowControlsTests.Run();
            checks+=SubtitleProfileUiTests.Run();
            checks+=SubtitleQuickGuideTests.Run(directory, window);
            checks+=PersonAvatarTests.Run(Path.Combine(directory, "people-profiles"));
            checks+=PersonAvatarSeedTests.Run(Path.Combine(directory, "avatar-seeds"));
            checks+=GeneratedPersonAvatarTests.Run(Path.Combine(directory, "generated-avatars"));
            checks+=PeopleUiTests.Run(Path.Combine(directory, "people-ui"));
            checks+=SpeakerRecognitionPreferenceTests.Run();
            checks+=Task.Run(() => AiSpeakerIdentifier.RunRecognitionControlOfflineSelfTestsAsync(Path.Combine(directory, "recognition-backend"))).GetAwaiter().GetResult();
            checks+=IncomingTranslationService.RunSpeakerRecognitionOfflineChecksAsync(Path.Combine(directory, "speaker-controls")).GetAwaiter().GetResult();
            checks+=AutomaticSetupOfflineTests.RunAsync().GetAwaiter().GetResult();
            checks+=ManagedSpeechIntegrationTests.Run(Path.Combine(directory, "speech-shortcut-fixtures"));
            checks+=TranslationSelectionPreparation.RunOfflineChecksAsync().GetAwaiter().GetResult();
            checks+=SetupChoiceUiTests.Run(window);
            checks+=SetupChoiceUiTests.RenderPreview(window, directory);
            Progress("Runtime, overlay and setup controls");
            checks+=FirstRunSetupViewTests.Run(Path.Combine(directory, "first-run-setup"));
            checks+=FirstRunTourTests.Run(directory);
            checks+=OnboardingIntegrationTests.Run(window, directory);
            Progress("First-run setup and tour");
            checks+=SpeechSetupProgressTests.RunOfflineSelfTests();
            checks+=RuntimeController.RunProgressOfflineChecks();
            checks+=SetupProgressUiTests.Run(window);
            checks+=SetupProgressUiTests.RenderPreview(window, directory);
            checks+=SubtitleSegmentation.RunSelfTests();
            checks+=SmokeTests.RunFiniteUtteranceChecks();
            checks+=SpeakerFeedbackOfflineTests.RunAsync().GetAwaiter().GetResult();
            checks+=SpeakerFeedbackOfflineTests.RunOverlayChecks();
            checks+=SpeakerSuggestionOverlayTests.Run(Path.Combine(directory, "speaker-suggestions"));
            checks+=SpeakerSuggestionReviewTests.RunAsync(Path.Combine(directory, "speaker-reviews")).GetAwaiter().GetResult();
            checks+=SpeakerReviewPeopleUiTests.Run(Path.Combine(directory, "reviewed-people-ui"));
            checks+=Task.Run(() => SpeakerReviewServiceTests.RunAsync(Path.Combine(directory, "reviewed-service"))).GetAwaiter().GetResult();
            checks+=SpeakerRecencyTests.RunAsync().GetAwaiter().GetResult();
            checks+=SpeakerQuickPickerTests.Run(window, directory);
            checks+=IncomingTranslationService.RunSpeakerContextChecks();
            checks+=Task.Run(() => HardwareAdvisorOfflineTests.RunAsync()).GetAwaiter().GetResult();
            Progress("Speech, settings and person feedback");
            Require(SettingsStore.Load().OwnManagedModelId == "hy-mt2-1.8b-q4_k_m" && SettingsStore.Load().IncomingManagedModelId == "hy-mt2-1.8b-q4_k_m", "New users default to one shared dedicated Hy-MT2 translator");
            Require(!SettingsStore.Load().FirstRunSetupCompleted, "A new installation offers first-time setup");
            Require(SettingsStore.Load().CloseButtonBehavior == "Ask", "First close asks before quitting or hiding");
            Require(MainWindow.PrimaryPages.SequenceEqual(new[] { "Home", "Languages", "Speakers", "Settings", "Setup", "Help", "Advanced" }), "Simple navigation includes Help and keeps technical pages out of the main sidebar");
            Require(UserFacingErrors.Describe(new InvalidOperationException("developer details /opt/example:8081")) == "Couldn't finish this step. Try again or open Troubleshooting for details.", "Raw technical failures do not leak into primary UI");
            Require(UserFacingErrors.Describe(new IncomingListeningSelectionException("Choose an application to translate before starting subtitles.")) ==
                "Choose an application to translate before starting subtitles.", "A missing application selection gives a clear next action instead of a generic setup error");
            const string speechSetupMessage = "Speech recognition setup failed. Open Troubleshooting to see the build error, then choose Continue setup.";
            Require(UserFacingErrors.Describe(new SpeechSetupException(speechSetupMessage,
                new SetupCommandException(23, "offline-helper /opt/example", "synthetic build diagnostic"))) == speechSetupMessage,
                "Speech setup keeps its actionable message without exposing raw command details in the main UI");
            Require(UserFacingErrors.Describe(new SetupCommandException(23, "offline-helper /opt/example", "synthetic build diagnostic")) ==
                "Couldn't finish this step. Try again or open Troubleshooting for details.",
                "Unwrapped command details remain confined to Troubleshooting");
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            checks+=RedesignTests.Run(window);
            checks+=BrandingTests.Run(window);
            var sample = new TranslationDisplay("Speaker 1", "Synthetic source", "Synthetic subtitle", 1, DateTimeOffset.UtcNow) { EvidenceId = "synthetic-evidence" };
            var feedbackState = new SpeakerFeedbackState(new[] { new SpeakerPerson("synthetic-person", "Example friend", 2) }, false);
            Window correction = SpeakerFeedbackUi.CreateCorrectionDialog(window, new InertSpeakerFeedbackHost(feedbackState), sample, feedbackState);
            Window learning = SpeakerFeedbackUi.CreateLearningDialog(window, "Example friend");
            Window naming = SpeakerFeedbackUi.CreatePersonDialog(window);
            Window closingChoice = DesktopLifetime.CreateCloseChoiceDialog(window);
            Window setupConsent = window.CreateSetupConsentDialog(new("Download the translation files and prepare speech recognition. Windows may ask for permission.", 4_000_000_000, true, true));
            foreach (var scene in new[] { (Window: correction, File: "speaker-correction.png", Height: 660),
                (Window: learning, File: "speaker-learning.png", Height: 490), (Window: naming, File: "speaker-name.png", Height: 310),
                (Window: closingChoice, File: "close-choice.png", Height: 600), (Window: setupConsent, File: "setup-consent.png", Height: 620) })
            {
                Require(!scene.Window.Topmost, "Speaker dialogs are not always on top");
                var dialogContent = (FrameworkElement)scene.Window.Content;
                dialogContent.Measure(new Size(scene.Window.Width, scene.Height));
                dialogContent.Arrange(new Rect(0, 0, scene.Window.Width, scene.Height)); dialogContent.UpdateLayout();
                var dialogImage = new RenderTargetBitmap((int)scene.Window.Width, scene.Height, 96, 96, PixelFormats.Pbgra32);
                dialogImage.Render(dialogContent);
                var dialogEncoder = new PngBitmapEncoder(); dialogEncoder.Frames.Add(BitmapFrame.Create(dialogImage));
                using var dialogFile = File.Create(Path.Combine(directory, scene.File)); dialogEncoder.Save(dialogFile);
                checks++;
            }
            Require(TranslationModelPrompts.IsHyMt("tencent/hy-mt2-1.8b"),"Dedicated model is recognized");
            Require(!TranslationModelPrompts.IsHyMt("qwen/qwen3-8b"),"General model retains its own prompt protocol");
            string task=TranslationModelPrompts.HyMtUserMessage("This is a test.","Chinese");
            Require(task.EndsWith("\nThis is a test.") && task.Contains("into Chinese"),"Dedicated prompt has explicit target and source");
            foreach (string page in MainWindow.Pages)
            {
                window.Navigate(page);
                var content=(FrameworkElement)window.Content;
                content.Measure(new Size(1160,800)); content.Arrange(new Rect(0,0,1160,800)); content.UpdateLayout();
                MainWindow.ApplySlimScrollbars(content);content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1160,800,96,96,PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory,page.Replace(" & ","-").ToLowerInvariant()+".png"));
                encoder.Save(file); checks++;
            }
            var previewSettings=(AppSettings)typeof(MainWindow).GetField("_settings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            bool originallyComplete=previewSettings.FirstRunSetupCompleted;
            try
            {
                previewSettings.FirstRunSetupCompleted=true;window.Navigate("Home");
                var readyContent=(FrameworkElement)window.Content;readyContent.Measure(new Size(1160,800));readyContent.Arrange(new Rect(0,0,1160,800));readyContent.UpdateLayout();
                var readyImage=new RenderTargetBitmap(1160,800,96,96,PixelFormats.Pbgra32);readyImage.Render(readyContent);
                var readyEncoder=new PngBitmapEncoder();readyEncoder.Frames.Add(BitmapFrame.Create(readyImage));
                using(var readyFile=File.Create(Path.Combine(directory,"home-ready.png")))readyEncoder.Save(readyFile);checks++;
            }
            finally {previewSettings.FirstRunSetupCompleted=originallyComplete;}
            window.Navigate("Setup");var smallContent=(FrameworkElement)window.Content;smallContent.Measure(new Size(900,650));smallContent.Arrange(new Rect(0,0,900,650));smallContent.UpdateLayout();
            var smallImage=new RenderTargetBitmap(900,650,96,96,PixelFormats.Pbgra32);smallImage.Render(smallContent);
            var smallEncoder=new PngBitmapEncoder();smallEncoder.Frames.Add(BitmapFrame.Create(smallImage));
            using(var smallFile=File.Create(Path.Combine(directory,"setup-narrow.png")))smallEncoder.Save(smallFile);checks++;
            var subtitlePreview = new SubtitleWindow(); // Inert: never shown or connected to audio.
            subtitlePreview.Publish(new TranslationDisplay("Mira", "你好，很高兴认识你。", "Hey! It's nice to meet you.", 101, DateTimeOffset.UtcNow.AddSeconds(-10)));
            subtitlePreview.Publish(new TranslationDisplay("Jin", "这张地图真的很漂亮。", "This world is really beautiful.", 102, DateTimeOffset.UtcNow.AddSeconds(-5)));
            subtitlePreview.Publish(new TranslationDisplay("Mira", "一起去看看吧。", "Let's have a look around together.", 103, DateTimeOffset.UtcNow));
            var subtitleContent = (FrameworkElement)subtitlePreview.Content;
            subtitleContent.Measure(new Size(960, 430));
            subtitleContent.Arrange(new Rect(0, 0, 960, 430)); subtitleContent.UpdateLayout();
            var subtitleImage = new RenderTargetBitmap(960, 430, 96, 96, PixelFormats.Pbgra32);
            subtitleImage.Render(subtitleContent);
            var subtitleEncoder = new PngBitmapEncoder(); subtitleEncoder.Frames.Add(BitmapFrame.Create(subtitleImage));
            using (var subtitleFile = File.Create(Path.Combine(directory, "subtitle-pin.png"))) subtitleEncoder.Save(subtitleFile);
            checks++;
            subtitleContent.Measure(new Size(560,260));subtitleContent.Arrange(new Rect(0,0,560,260));subtitleContent.UpdateLayout();
            var narrowSubtitleImage=new RenderTargetBitmap(560,260,96,96,PixelFormats.Pbgra32);narrowSubtitleImage.Render(subtitleContent);
            var narrowSubtitleEncoder=new PngBitmapEncoder();narrowSubtitleEncoder.Frames.Add(BitmapFrame.Create(narrowSubtitleImage));
            using(var narrowSubtitleFile=File.Create(Path.Combine(directory,"subtitle-narrow.png")))narrowSubtitleEncoder.Save(narrowSubtitleFile);
            checks++;
            checks+=UiTextTests.Run();
            checks+=HelpUiTests.Run(window,directory);
            checks+=UiLanguageUiTests.Run(window,directory);
            checks+=ThemeTests.Run(window,directory);
            checks+=OverlayThemeTests.Run();
            File.WriteAllText(Path.Combine(directory,"results.log"),$"PASS {checks} offline dashboard/layout/default/privacy checks. No model, engine, microphone, audio capture or benchmark was started.\n");
            Environment.ExitCode = 0;
            app.Shutdown();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory,"results.log"),"FAIL: "+error);
            Environment.ExitCode = 1;
        }
        void Require(bool value,string label) { if(!value) throw new InvalidOperationException(label); checks++; }
        void Progress(string phase) => File.AppendAllText(Path.Combine(directory, "progress.log"), $"{DateTimeOffset.Now:HH:mm:ss} PASS {phase} ({checks} checks)\n");
    }

    private sealed class InertSpeakerFeedbackHost(SpeakerFeedbackState state) : ISpeakerFeedbackHost
    {
        public Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default) => Task.FromResult(state);
        public Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default) => throw new InvalidOperationException("Offline preview must not save a person.");
        public Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default) => throw new InvalidOperationException("Offline preview must not delete a person.");
        public Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default) => throw new InvalidOperationException("Offline preview must not acknowledge learning.");
        public Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default) => throw new InvalidOperationException("Offline preview must not correct real voice evidence.");
        public Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default) => throw new InvalidOperationException("Offline preview must not undo real feedback.");
    }
}

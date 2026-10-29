using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace IncomingTranslator;

internal static class IncomingCaptureCapabilityTests
{
    internal static async Task<int> RunRuntimeAsync(string directory)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Windows audio capabilities: " + detail); checks++; }
        foreach (int build in new[] { 19044, 19045, 20347, 20348, 22000 })
        {
            var version = new Version(10, 0, build);
            bool supported = build >= 20348;
            Check(IncomingCaptureCapabilities.SupportsProcessLoopback(version) == supported, "process loopback boundary: " + build);
            foreach (string mode in new[] { "vrchat", "application", "desktop" })
            {
                var selected = new AppSettings { IncomingAudioMode = mode, IncomingApplicationPath = @"C:\Synthetic\Discord.exe" };
                Check(IncomingCaptureCapabilities.SupportsSource(selected, version) == (supported || mode == "desktop"), "explicit source capability: " + build + "/" + mode);
            }
            var preferences = new AppSettings { UseManagedTranslator = true, ShareTranslationModel = false,
                OwnManagedModelId = "hy-mt2-1.8b-q4_k_m", IncomingManagedModelId = "hy-mt2-7b-q4_k_m", IncomingAudioMode = "vrchat" };
            MainWindow.ValidateTranslationSettingsForWindows(preferences, incomingRunning: false, version);
            Check(!preferences.ShareTranslationModel, "global validation preserves the real sharing choice");
            if (supported) continue;

            var unavailable = preferences.Copy(); unavailable.IncomingSpeechEngine = "voxtral";
            string root = Path.Combine(directory, "windows-" + build);
            using var provisioner = new LocalTalkProvisioner(root);
            await using var runtime = new RuntimeController(provisioner, () => version);
            FieldInfo own = typeof(RuntimeController).GetField("_ownRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo incoming = typeof(RuntimeController).GetField("_incomingRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
            own.SetValue(runtime, true); // No processes or model handles exist.
            try
            {
                async Task Reject(Func<Task> action, string detail)
                {
                    bool rejected = false;
                    try { await action(); }
                    catch (IncomingListeningSelectionException error) { rejected = error.Message == IncomingCaptureCapabilities.UnavailableMessage; }
                    Check(rejected && runtime.OwnRunning, detail + "; the synthetic own feature stays running");
                }
                await Reject(() => runtime.StartIncomingAsync(unavailable, () => { }), "Start rejects before missing WSL/assets can be inspected");
                await Reject(() => runtime.CheckGroupSpeechPreparedAsync(unavailable), "Groups inspection rejects before a WSL check helper");
                await Reject(() => runtime.PrepareGroupSpeechAsync(unavailable), "Groups preparation rejects before download helpers");
                incoming.SetValue(runtime, true);
                await Reject(() => runtime.SwitchIncomingProfileAsync(unavailable, () => { }), "an active switch rejects before stopping either feature");
                Check(runtime.IncomingRunning, "rejected switch preserves the synthetic incoming feature");
            }
            finally { own.SetValue(runtime, false); incoming.SetValue(runtime, false); }

            var platform = new NoCapturePlatform();
            var incomingSetup = new AutomaticSetupService(platform, Path.Combine(root, "incoming-setup"), incomingVoiceOnly: true, audioWindowsVersion: () => version);
            async Task RejectSetup(Func<Task> action)
            {
                bool rejected = false;
                try { await action(); } catch (IncomingListeningSelectionException) { rejected = true; }
                Check(rejected && platform.Inspections == 0 && platform.Preparations == 0, "explicit incoming setup rejects before inspection or preparation");
            }
            await RejectSetup(() => incomingSetup.PlanAsync(unavailable));
            await RejectSetup(() => incomingSetup.RunAsync(unavailable, explicitConsent: true));
            var ownOnly = unavailable.Copy(); ownOnly.OwnRecognitionEngine = "nemotron";
            var ownSetup = new AutomaticSetupService(platform, Path.Combine(root, "own-setup"), ownVoiceOnly: true, audioWindowsVersion: () => version);
            AutomaticSetupPlan plan = await ownSetup.PlanAsync(ownOnly);
            Check(platform.Inspections == 1 && platform.Preparations == 0 && !plan.SpeechSetupRequired,
                "own-voice-only planning remains available with default optional VRChat subtitles");

            var restricted = preferences.Copy(); restricted.OwnManagedModelId = "lfm2-350m-enjp-mt-q4_k_m";
            restricted.OwnVoiceSourceLanguage = "English"; restricted.OwnVoiceTargetLanguage = "Japanese";
            restricted.OwnVoiceAdditionalLanguagesEnabled = false; restricted.IncomingSourceLanguage = "Automatic";
            restricted.IncomingAudioMode = "application"; restricted.IncomingApplicationPath = "";
            MainWindow.ValidateTranslationSettingsForWindows(restricted, incomingRunning: false, version);
            restricted.ShareTranslationModel = true;
            MainWindow.ValidateTranslationSettingsForWindows(restricted, incomingRunning: false, version);
            var change = SettingsRuntimeChanges.Between(preferences, restricted, includeIncoming: false);
            Check(change.OwnChanged && !change.IncomingChanged,
                "own runtime planning ignores an unavailable unused Liquid/Automatic incoming pair and invalid app selection");
            restricted.ShareTranslationModel = false;
            var models = new HashSet<string>(); var runtimes = new HashSet<string>(); var prepared = new List<string>();
            var preparation = new TranslationSelectionPreparation(models.Contains, runtimes.Contains,
                (id, _) => { models.Add(id); prepared.Add("model:" + id); return Task.CompletedTask; },
                (id, _) => { runtimes.Add(id); prepared.Add("runtime:" + id); return Task.CompletedTask; }, ownVoiceOnly: true);
            Check(preparation.MissingModelIds(restricted).SequenceEqual(new[] { restricted.OwnManagedModelId }),
                "own-only preparation lists Liquid only, not the unused separate Hy model");
            await preparation.PrepareAsync(restricted);
            Check(prepared.SequenceEqual(new[] { "model:" + restricted.OwnManagedModelId, "runtime:" + restricted.ManagedRuntimeId }) &&
                !restricted.ShareTranslationModel && restricted.IncomingManagedModelId == preferences.IncomingManagedModelId && restricted.IncomingSourceLanguage == "Automatic",
                "mocked preparation downloads only own assets and preserves sharing plus every incoming choice");
            bool activeRejected = false, modernRejected = false;
            try { MainWindow.ValidateTranslationSettingsForWindows(restricted, incomingRunning: true, version); }
            catch (IncomingListeningSelectionException) { activeRejected = true; }
            try { MainWindow.ValidateTranslationSettingsForWindows(restricted, incomingRunning: false, new Version(10, 0, 22000)); }
            catch (IncomingListeningSelectionException) { modernRejected = true; }
            Check(activeRejected && modernRejected, "active incoming and supported Windows still validate/reject the unavailable app selection");
        }
        return checks;
    }

    internal static int RunUi(MainWindow window)
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Windows audio UI: " + detail); checks++; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", flags)!.GetValue(window)!;
        var runtime = (RuntimeController)typeof(MainWindow).GetField("_runtime", flags)!.GetValue(window)!;
        Check((bool)typeof(MainWindow).GetField("_preview", flags)!.GetValue(window)! && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning,
            "requires an isolated, unshown preview");
        AppSettings before = settings.Copy(); Version? versionBefore = window.SubtitleWindowsVersionForPreview;
        string languageBefore = UiText.Language, pageBefore = (string)typeof(MainWindow).GetField("_page", flags)!.GetValue(window)!;
        try
        {
            settings.FirstRunSetupCompleted = true; settings.UseManagedTranslator = true;
            settings.OwnManagedModelId = "hy-mt2-1.8b-q4_k_m"; settings.IncomingManagedModelId = "hy-mt2-1.8b-q4_k_m";
            settings.IncomingSpeechEngine = "nemotron"; settings.IncomingSourceLanguage = "Automatic";
            settings.IncomingTargetLanguage = "English";
            foreach (int build in new[] { 19044, 19045, 20347, 20348, 22000 })
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                bool supported = build >= 20348;
                window.SubtitleWindowsVersionForPreview = new Version(10, 0, build); UiText.Language = language;
                settings.IncomingAudioMode = "vrchat"; settings.CaptureVrChatOnly = true;
                window.Navigate("Home");
                var nodes = Nodes(window).ToArray();
                Check(nodes.OfType<Button>().Where(button => button.Name == "HomeSubtitleAudioSourceChoice" && !Equals(button.Tag, "desktop"))
                    .All(button => button.IsEnabled == supported), "Home gates application/VRChat capture: " + build + "/" + language);
                Check(nodes.OfType<Button>().Single(button => button.Name == "HomeSubtitleAudioSourceChoice" && Equals(button.Tag, "desktop")).IsEnabled &&
                    nodes.OfType<Button>().Single(button => button.Name == "OwnVoiceStartButton").IsEnabled && settings.IncomingAudioMode == "vrchat",
                    "All playback and microphone remain available without a silent source change");
                Check(nodes.OfType<TextBlock>().Any(text => text.Name == "HomeSubtitleCaptureCompatibilityNotice" && text.Text == UiText.T(IncomingCaptureCapabilities.UnavailableMessage)) == !supported,
                    "the Home limitation is visible in the chosen language");
                if (!supported)
                {
                    Check(!nodes.OfType<Button>().Single(button => button.Name == "SubtitlesStartButton").IsEnabled &&
                        !nodes.OfType<Button>().Single(button => button.Name == "ApplyHomeSubtitleChoicesButton").IsEnabled,
                        "unsupported subtitles cannot start or request downloads");
                    nodes.OfType<Button>().Single(button => button.Name == "HomeSubtitleAudioSourceChoice" && Equals(button.Tag, "desktop"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(settings.IncomingAudioMode == "desktop" && !settings.CaptureVrChatOnly,
                        "only an explicit All playback click changes the source");
                }
                settings.IncomingAudioMode = "vrchat"; settings.CaptureVrChatOnly = true; window.Navigate("Listening");
                nodes = Nodes(window).ToArray();
                Check(nodes.OfType<ComboBox>().Single(combo => combo.Name == "IncomingListeningModePicker").IsEnabled == supported,
                    "the older Listening picker cannot select an unsupported process source");
                Check(nodes.OfType<TextBlock>().Any(text => text.Name == "ListeningCaptureCompatibilityNotice") == !supported,
                    "Listening explains its unavailable choices");
                if (!supported)
                {
                    nodes.OfType<Button>().Single(button => button.Name == "ListeningUseAllPlaybackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(settings.IncomingAudioMode == "desktop", "Listening offers an explicit desktop action");
                }
            }
            Check(!runtime.OwnRunning && !runtime.IncomingRunning, "capability previews never start a runtime");
            return checks;
        }
        finally
        {
            window.SubtitleWindowsVersionForPreview = versionBefore; TranslationProfiles.CopyInto(before, settings);
            UiText.Language = languageBefore; window.Navigate(pageBefore);
        }
    }

    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node)
                foreach (DependencyObject descendant in Nodes(node)) yield return descendant;
    }

    private sealed class NoCapturePlatform : IAutomaticSetupPlatform
    {
        internal int Inspections, Preparations;
        public Task<AutomaticSetupSnapshot> InspectAsync(AppSettings settings, CancellationToken token)
        { Inspections++; return Task.FromResult(new AutomaticSetupSnapshot(LocalTalkProvisioner.ModelCatalog.Select(model => model.Id).ToHashSet(),
            LocalTalkProvisioner.RuntimeCatalog.Select(runtime => runtime.Id).ToHashSet(), new HashSet<string>(), null, true, true, false)); }
        public bool IsOwnNativeSpeechReady(AppSettings settings) => true;
        public long PendingOwnNativeSpeechBytes(AppSettings settings) => 0;
        private Task Unexpected() { Preparations++; throw new InvalidOperationException("No preparation is allowed in this fixture."); }
        public Task<SetupPrerequisiteResult> EnsureNativeRuntimeAsync(IProgress<ProvisionProgress>? progress, CancellationToken token) { Unexpected(); throw new InvalidOperationException(); }
        public Task<SetupPrerequisiteResult> EnsureWindowsSpeechAsync(IProgress<ProvisionProgress>? progress, CancellationToken token) { Unexpected(); throw new InvalidOperationException(); }
        public Task EnsureDistributionAsync(string name, IProgress<ProvisionProgress>? progress, CancellationToken token) => Unexpected();
        public Task EnsureModelAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token) => Unexpected();
        public Task EnsureRuntimeAsync(string id, IProgress<ProvisionProgress>? progress, CancellationToken token) => Unexpected();
        public Task<string> EnsureSpeakerModelAsync(AppSettings settings, IProgress<ProvisionProgress>? progress, CancellationToken token) { Unexpected(); throw new InvalidOperationException(); }
        public Task ValidateSpeechAsync(WslSpeechRegistration speech, CancellationToken token) => Unexpected();
        public Task BuildSpeechAsync(string distribution, string backend, IProgress<ProvisionProgress>? progress, CancellationToken token) => Unexpected();
    }
}

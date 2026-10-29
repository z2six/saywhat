using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace IncomingTranslator;

// Offline compatibility and visible-copy checks only. Never show a window, click a
// control, create an engine, change live AppData, or initialize speech recognition.
internal static class BrandingTests
{
    private static readonly string[] RetiredSlogans =
    [
        "Same conversation.", "Say it your way.", "Meet in the middle.",
        "Speak freely.", "Follow along.", "Different languages. A little closer together.",
        "Speak naturally. Read along. Enjoy the company.", "YOUR NEXT CONVERSATION",
        "A few minutes of setup. Many more conversations."
    ];

    internal static int Run(MainWindow window)
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Branding check: " + description);
            checks++;
        }
        Check(AppBrand.DisplayName == "SayWhat?", "display name keeps the question mark");
        Check(AppBrand.FileName == "SayWhat" && AppBrand.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
            "filename identity is valid on Windows");
        Check(Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) == AppBrand.Version, "release identity is consistent");
        Check(AppBrand.IsPackagedAssembly("SayWhat") && AppBrand.IsPackagedAssembly("LocalTalk"),
            "both new and legacy desktop assemblies use packaged data paths");
        Check(Assembly.GetEntryAssembly()?.GetName().Name == AppBrand.FileName,
            "this release actually builds the filename-safe SayWhat desktop assembly");
        Check(!AppBrand.IsPackagedAssembly(null) && !AppBrand.IsPackagedAssembly("VRChatIncomingTranslator")
            && !AppBrand.IsPackagedAssembly("Other"), "unrelated executables are not treated as the desktop app");
        Check(AppBrand.DesktopMutex == "Local\\LocalTalkDesktop", "old and new desktop versions retain one compatible instance guard");
        Check(AppBrand.DataEnvironmentVariable == "SAYWHAT_DATA_ROOT"
            && AppBrand.LegacyDataEnvironmentVariable == "LOCALTALK_DATA_ROOT", "explicit old data-root overrides remain supported");
        Check(AppBrand.SpeechDistributionName == "SayWhat-Ubuntu", "fresh speech installations use the filename-safe brand");
        foreach (string name in new[] { "SayWhat-Ubuntu", "SayWhat-Ubuntu-0123abcd", "LocalTalk-Ubuntu", "LocalTalk-Ubuntu-89abcdef" })
            Check(AppBrand.IsOwnedSpeechDistributionName(name), "registered speech identity remains recognized: " + name);
        foreach (string name in new[] { "Ubuntu", "SayWhat-Ubuntu-other", "LocalTalk-Ubuntu-0123abcd-other", "Other-SayWhat-Ubuntu" })
            Check(!AppBrand.IsOwnedSpeechDistributionName(name), "unrelated distributions are not recognized as managed: " + name);
        Check(Path.GetFileName(DesktopLog.Path) == "saywhat.log", "new desktop diagnostics use the new log filename");
        Check(AppPaths.IsPackagedApplication, "the renamed assembly still uses packaged settings and models");
        checks += CheckDataCompatibility();
        checks += BrandArtworkTests.RunFactories();

        Check(Read<bool>(window, "_preview") && !window.IsLoaded && !window.IsVisible,
            "copy checks require an unshown preview window");
        Check(window.Dispatcher.CheckAccess(), "unshown WPF checks run on the creating dispatcher");
        AppSettings settings = Read<AppSettings>(window, "_settings");
        RuntimeController runtime = Read<RuntimeController>(window, "_runtime");
        string before = JsonSerializer.Serialize(settings);
        string? settingsFile = ReadIfPresent(AppPaths.SettingsPath);
        bool originalComplete = settings.FirstRunSetupCompleted;
        string originalPage = Read<string>(window, "_page");
        Check(!runtime.OwnRunning && !runtime.IncomingRunning, "brand inspection has no active translation feature");
        try
        {
            Check(window.Title.Contains(AppBrand.DisplayName, StringComparison.Ordinal), "dashboard title uses the display brand");
            checks += BrandArtworkTests.CheckMainWindow(window);
            foreach (bool setupComplete in new[] { false, true })
            {
                settings.FirstRunSetupCompleted = setupComplete; // In-memory fixture only; never Save or Persist.
                foreach (string page in MainWindow.Pages)
                {
                    window.Navigate(page);
                    Prepare((FrameworkElement)window.Content, new Size(1160, 800));
                    CheckVisibleCopy(window, page + (setupComplete ? " configured" : " first-run"), Check);
                    Check(Copy(window).Any(text => text.Contains(AppBrand.DisplayName, StringComparison.Ordinal)),
                        page + " retains the named application in its sidebar");
                    if (page == "Home") Check(Nodes(window).OfType<Button>().Any(button => button.Name == "OwnVoiceStartButton") &&
                        Nodes(window).OfType<Image>().Any(image => image.Name == "SidebarBrandLockup"),
                        "voice-first Home retains the approved sidebar brand and a clear own-voice action without a decorative hero banner");
                }
            }
            var state = new SpeakerFeedbackState(new[] { new SpeakerPerson("inert-person", "Example friend", 0) }, false);
            var host = new InertFeedbackHost(state);
            var sentence = new TranslationDisplay("Example friend", "Synthetic source", "Synthetic subtitle", 1, DateTimeOffset.UtcNow)
                { EvidenceId = "inert-evidence" };
            Window consent = window.CreateSetupConsentDialog(new("Prepare local translation tools. No microphone will be opened.", 4_000_000_000, true, true));
            Window closing = DesktopLifetime.CreateCloseChoiceDialog(window);
            Window[] dialogs =
            [
                consent, closing,
                SpeakerFeedbackUi.CreateCorrectionDialog(window, host, sentence, state),
                SpeakerFeedbackUi.CreateLearningDialog(window, "Example friend"),
                SpeakerFeedbackUi.CreatePersonDialog(window)
            ];
            foreach (Window dialog in dialogs)
            {
                Check(!dialog.IsVisible && !dialog.IsLoaded, "dialog copy checks never show or load a window");
                checks += BrandArtworkTests.CheckWindowIcon(dialog);
                Prepare((FrameworkElement)dialog.Content, new Size(dialog.Width, 720));
                CheckVisibleCopy(dialog, dialog.Title, Check);
            }
            Check(consent.Title.Contains(AppBrand.DisplayName, StringComparison.Ordinal)
                && closing.Title.Contains(AppBrand.DisplayName, StringComparison.Ordinal), "setup and close captions use the display brand");
            Check(host.MutationCalls == 0, "constructing correction dialogs does not save names or learn voices");
            var subtitles = new SubtitleWindow();
            Check(!subtitles.IsVisible && !subtitles.IsLoaded && !subtitles.ServiceStopped.IsCompleted,
                "unshown subtitle inspection never starts its service");
            Prepare((FrameworkElement)subtitles.Content, new Size(960, 430));
            CheckVisibleCopy(subtitles, "Subtitles", Check);
            Check(subtitles.Title.StartsWith(AppBrand.DisplayName, StringComparison.Ordinal)
                && Copy(subtitles).Any(text => text.StartsWith(AppBrand.DisplayName, StringComparison.Ordinal)),
                "subtitle caption and visible header use the display brand");
            checks += BrandArtworkTests.CheckSubtitleWindow(subtitles);
        }
        finally
        {
            settings.FirstRunSetupCompleted = originalComplete;
            window.Navigate(originalPage);
        }
        Check(JsonSerializer.Serialize(settings) == before, "renaming and copy checks preserve all settings in memory");
        Check(ReadIfPresent(AppPaths.SettingsPath) == settingsFile, "copy checks do not write persisted settings");
        Check(!window.IsLoaded && !window.IsVisible && !runtime.OwnRunning && !runtime.IncomingRunning,
            "all capture and models remain stopped after branding checks");
        return checks;
    }

    private static int CheckDataCompatibility()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Branding data compatibility check: " + description);
            checks++;
        }
        // All names/files here are tiny synthetic fixtures, never actual user caches or model weights.
        string fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SayWhat-Branding-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fixture);
        try
        {
            string fresh = Path.Combine(fixture, "fresh");
            Check(AppBrand.ResolveDataRoot(fresh) == Path.GetFullPath(Path.Combine(fresh, "SayWhat")), "a fresh install chooses SayWhat");
            Check(!Directory.Exists(fresh), "resolving a root never creates an installation or data directory");
            string appData = Path.Combine(fixture, "appdata");
            string legacy = Path.Combine(appData, "LocalTalk"), current = Path.Combine(appData, "SayWhat");
            Directory.CreateDirectory(Path.Combine(legacy, "models", "speakers"));
            Dictionary<string, string> markers = new()
            {
                ["vrchat-translation-settings.json"] = "synthetic settings retained",
                ["speaker-feedback.json"] = "synthetic named voices retained",
                ["ai-speaker-profiles.json"] = "synthetic anonymous profiles retained",
                ["localtalk-runtime.json"] = "synthetic registered model inventory retained",
                ["setup-progress.json"] = "synthetic setup state retained",
                [Path.Combine("models", "synthetic.gguf")] = "NOT A REAL MODEL",
                [Path.Combine("models", "speakers", "synthetic.onnx")] = "NOT A REAL MODEL",
                ["localtalk.log"] = "synthetic legacy diagnostics retained"
            };
            foreach (var marker in markers) File.WriteAllText(Path.Combine(legacy, marker.Key), marker.Value);
            string[] filesBefore = Directory.GetFiles(legacy, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Check(AppBrand.ResolveDataRoot(appData) == Path.GetFullPath(legacy), "an existing LocalTalk root is reused in place");
            Check(!Directory.Exists(current), "reuse does not copy or create another model cache");
            foreach (var marker in markers)
                Check(File.ReadAllText(Path.Combine(legacy, marker.Key)) == marker.Value, "legacy data remains untouched: " + marker.Key);
            Check(filesBefore.SequenceEqual(Directory.GetFiles(legacy, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)),
                "root resolution does not merge, rename, or remove old files");
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "sentinel.txt"), "separate new installation");
            Check(AppBrand.ResolveDataRoot(appData) == Path.GetFullPath(current), "when both exist the new root wins deterministically");
            Check(File.ReadAllText(Path.Combine(current, "sentinel.txt")) == "separate new installation"
                && File.ReadAllText(Path.Combine(legacy, "speaker-feedback.json")) == markers["speaker-feedback.json"],
                "two installations are never silently merged");
            string explicitNew = Path.Combine(fixture, "explicit-new"), explicitOld = Path.Combine(fixture, "explicit-legacy");
            Check(AppBrand.ResolveDataRoot(appData, explicitNew, explicitOld) == Path.GetFullPath(explicitNew), "a new explicit root has first priority");
            Check(AppBrand.ResolveDataRoot(appData, null, explicitOld) == Path.GetFullPath(explicitOld), "the legacy explicit override remains usable");
            Check(AppBrand.ResolveDataRoot(appData, "  ", explicitOld) == Path.GetFullPath(explicitOld), "an empty new override does not mask the old override");
            string relative = Path.Combine("Branding-relative-" + Guid.NewGuid().ToString("N"), "data");
            Check(AppBrand.ResolveDataRoot(appData, relative) == Path.GetFullPath(relative), "configured roots are fully normalized");
            Check(!Directory.Exists(explicitNew) && !Directory.Exists(explicitOld) && !Directory.Exists(Path.GetFullPath(relative)),
                "explicit resolution is also read-only");
        }
        finally
        {
            // Exact unique synthetic fixture, resolved before recursive cleanup; never an AppData root.
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Path.GetFileName(fixture)));
            if (fixture == expected && Path.GetFileName(fixture).StartsWith("SayWhat-Branding-", StringComparison.Ordinal))
                Directory.Delete(fixture, recursive: true);
        }
        return checks;
    }

    private static void CheckVisibleCopy(Window window, string scene, Action<bool, string> check)
    {
        string[] copy = Copy(window).Select(WithoutCompatibilityLocations).ToArray();
        check(copy.All(text => !text.Contains("LocalTalk", StringComparison.OrdinalIgnoreCase)), scene + " has no old display brand");
        foreach (string slogan in RetiredSlogans)
            check(copy.All(text => !text.Contains(slogan, StringComparison.OrdinalIgnoreCase)), scene + " has no retired slogan: " + slogan);
    }

    private static string WithoutCompatibilityLocations(string text)
    {
        // Existing user/cache paths and the legacy log picker are deliberately not renamed.
        // Editable text (including logs, model paths and user names) is not product copy.
        foreach (string path in new[] { AppPaths.Root, AppPaths.ToolRoot })
            if (!string.IsNullOrEmpty(path)) text = text.Replace(path, "[existing data location]", StringComparison.OrdinalIgnoreCase);
        return text.Replace("localtalk.log", "[legacy log]", StringComparison.OrdinalIgnoreCase);
    }
    private static string[] Copy(Window window)
    {
        var copy = new List<string> { window.Title };
        foreach (DependencyObject node in Nodes(window))
        {
            if (node is TextBox || IsInsideTextBox(node)) continue;
            string? text = node switch
            {
                TextBlock block => block.Text,
                AccessText access => access.Text,
                ContentControl control when control.Content is string caption => caption,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) copy.Add(text);
            if (node is Expander { Header: string header }) copy.Add(header);
            string automation = AutomationProperties.GetName(node);
            if (!string.IsNullOrWhiteSpace(automation)) copy.Add(automation);
            if (node is FrameworkElement { ToolTip: string tooltip }) copy.Add(tooltip);
        }
        return copy.Distinct(StringComparer.Ordinal).ToArray();
    }
    private static bool IsInsideTextBox(DependencyObject node)
    {
        for (DependencyObject? parent = node; parent is Visual; parent = VisualTreeHelper.GetParent(parent))
            if (parent is TextBox) return true;
        return false;
    }
    private static IEnumerable<DependencyObject> Nodes(DependencyObject root)
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
    private static void Prepare(FrameworkElement content, Size size)
    {
        content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
    }
    private static T Read<T>(object instance, string field) => (T)(instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException("Missing branding preview field: " + field));
    private static string? ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private sealed class InertFeedbackHost(SpeakerFeedbackState state) : ISpeakerFeedbackHost
    {
        internal int MutationCalls { get; private set; }
        public Task<SpeakerFeedbackState> GetSpeakerFeedbackStateAsync(CancellationToken token = default) => Task.FromResult(state);
        private Task<T> Unexpected<T>() { MutationCalls++; return Task.FromException<T>(new InvalidOperationException("Offline branding checks must not mutate speaker data.")); }
        public Task<SpeakerPerson> SaveSpeakerPersonAsync(string name, string? personId = null, CancellationToken token = default) => Unexpected<SpeakerPerson>();
        public Task DeleteSpeakerPersonAsync(string personId, CancellationToken token = default) => Unexpected<object>();
        public Task AcknowledgeSpeakerLearningAsync(CancellationToken token = default) => Unexpected<object>();
        public Task<SpeakerFeedbackResult> CorrectSpeakerAsync(string evidenceId, string? personId, string? specialLabel = null, CancellationToken token = default) => Unexpected<SpeakerFeedbackResult>();
        public Task<SpeakerFeedbackResult> UndoSpeakerCorrectionAsync(string evidenceId, CancellationToken token = default) => Unexpected<SpeakerFeedbackResult>();
    }
}

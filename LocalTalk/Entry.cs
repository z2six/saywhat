using System.IO;
using System.Windows;

namespace IncomingTranslator;

internal static class LocalTalkEntry
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Keep developer/offline check failures out of Windows Error Reporting dialogs.
        SetErrorMode(0x0001 | 0x0002);
        if (args.Length==2 && args[0]=="--ui-self-test") { DesktopTests.Run(args[1]); return; }
        if (InstallerShutdownProtocol.TryHandleClientCommand(args, out int installerExitCode)) { Environment.ExitCode=installerExitCode;return; }
        if (InstallerUiLaunch.Prepare(args, out string[] remainingArguments, out int launchExitCode)) { Environment.ExitCode=launchExitCode; return; }
        args=remainingArguments;
        using var mutex=new Mutex(true,AppBrand.DesktopMutex,out bool first);
        if(!first) {
            if(args.Length>0) {DesktopLog.Write("Startup flags were not applied: SayWhat? is already open. No duplicate tools were started.");Environment.ExitCode=2;}
            return;
        }
        var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
        ModernTheme.Apply(app);
        app.DispatcherUnhandledException += (_,e)=> {
            DesktopLog.Write("UI error: "+e.Exception);
            MessageBox.Show(UiText.T("Something went wrong. Try again or open Advanced → Diagnostics for help."),AppBrand.DisplayName,MessageBoxButton.OK,MessageBoxImage.Warning);
            e.Handled=true;
        };
        try {
            var launch=LocalTalkLaunchOptions.Parse(args);
            AppPaths.InitializePackagedData();
            using (var provisioner = new LocalTalkProvisioner())
                ManagedSpeechIntegration.TryHideGeneratedWindowsShortcuts(provisioner.SpeechRegistration, provisioner.DataRoot);
            if (launch.UiLanguage is string language) SettingsStore.SaveUiLanguagePreference(language);
            UiText.Language = SettingsStore.Load().UiLanguage;
            app.Run(new MainWindow(launchOptions:launch));
        }
        catch(Exception error) { DesktopLog.Write("Application startup failed: "+error); MessageBox.Show("SayWhat? couldn't open. No translation tools were started. Try opening it again. If this keeps happening, reinstall the app. Your settings won't be removed.",AppBrand.DisplayName,MessageBoxButton.OK,MessageBoxImage.Error); Environment.ExitCode=1; }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}

internal sealed record LocalTalkLaunchOptions(bool StartAll = false, string? TranslationModelId = null,
    bool DownloadModel = false, WslSpeechRegistration? Speech = null, string? SpeakerModelPath = null, string? UiLanguage = null)
{
    // Pure parsing: no process, download, filesystem mutation or inference occurs here.
    public static LocalTalkLaunchOptions Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool start = false, download = false;
        for (int index = 0; index < args.Count; index++)
        {
            string flag = args[index];
            if (!seen.Add(flag)) throw new ArgumentException("Duplicate startup flag: " + flag);
            if (flag == "--start-all") { start = true; continue; }
            if (flag == "--download-model") { download = true; continue; }
            if (flag is not ("--translation-model" or "--speech-distribution" or "--speech-server" or
                "--speech-model" or "--speech-backend" or "--speaker-model-path" or "--ui-language"))
                throw new ArgumentException("Unknown SayWhat? startup flag: " + flag);
            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("A value is required after " + flag + ".");
            values.Add(flag, args[index]);
        }
        if (!start && (download || values.Keys.Any(key => key != "--ui-language")))
            throw new ArgumentException("Startup setup flags require --start-all. Ordinary launch remains idle.");
        values.TryGetValue("--ui-language", out string? uiLanguage);
        if (uiLanguage is not null && uiLanguage is not ("en" or "zh-CN" or "ja" or "ko"))
            throw new ArgumentException("Interface language must be en, zh-CN, ja or ko.");
        values.TryGetValue("--translation-model", out string? model);
        if (model is not null && !LocalTalkProvisioner.ModelCatalog.Any(choice => choice.Id == model && choice.Role == ProvisionModelRole.Translation))
            throw new ArgumentException("Choose a translation catalogue ID, not a speech model: " + model);
        if (download && model is null) throw new ArgumentException("--download-model requires an explicit --translation-model catalogue ID.");
        string[] speechFlags = ["--speech-distribution", "--speech-server", "--speech-model", "--speech-backend"];
        int supplied = speechFlags.Count(values.ContainsKey);
        if (supplied is not 0 and not 4)
            throw new ArgumentException("Supply all four speech flags together: distribution, server, model and backend.");
        WslSpeechRegistration? speech = null;
        if (supplied == 4)
        {
            string distro = values["--speech-distribution"], server = values["--speech-server"], speechModel = values["--speech-model"], backend = values["--speech-backend"];
            if (!System.Text.RegularExpressions.Regex.IsMatch(distro, "^[A-Za-z0-9_.-]{1,64}$")) throw new ArgumentException("Invalid WSL distribution name.");
            foreach (string path in new[] { server, speechModel })
                if (!path.StartsWith('/') || path.Contains('\0') || path.Contains("..", StringComparison.Ordinal))
                    throw new ArgumentException("Speech paths must be absolute Linux paths without parent traversal.");
            if (backend is not ("cuda" or "vulkan" or "cpu")) throw new ArgumentException("Speech backend must be cuda, vulkan or cpu.");
            speech = new(distro, server, speechModel, backend);
        }
        values.TryGetValue("--speaker-model-path", out string? speaker);
        if (speaker is not null)
        {
            speaker = System.IO.Path.GetFullPath(speaker);
            if (!string.Equals(System.IO.Path.GetExtension(speaker), ".onnx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The existing speaker fingerprint model must be an ONNX file.");
        }
        return new(start, model, download, speech, speaker, uiLanguage);
    }

    internal static int RunOfflineSelfTests()
    {
        int checks = 0;
        void Check(bool result) { if(!result) throw new InvalidOperationException("Startup CLI parsing self-check failed.");checks++; }
        void Reject(params string[] flags) { bool rejected=false;try {Parse(flags);}catch(ArgumentException){rejected=true;}Check(rejected); }
        Check(!Parse([]).StartAll);
        foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            Check(Parse(["--ui-language", language]) is { StartAll: false, DownloadModel: false, Speech: null, UiLanguage: var selected } && selected == language);
        Reject("--ui-language", "not-a-language");
        Reject("--ui-language", "ja", "--translation-model", "hy-mt2-1.8b-q4_k_m");
        Check(Parse(["--start-all"]).StartAll && !Parse(["--start-all"]).DownloadModel);
        var requested = Parse(["--start-all","--translation-model","hy-mt2-1.8b-q4_k_m","--download-model",
            "--speech-distribution","Ubuntu","--speech-server","/opt/voxtral.cpp/server","--speech-model","/opt/voxtral.cpp/model.gguf","--speech-backend","cuda"]);
        Check(requested.DownloadModel && requested.Speech?.Distribution == "Ubuntu" && requested.TranslationModelId == "hy-mt2-1.8b-q4_k_m");
        Reject("--download-model");
        Reject("--start-all","--download-model");
        Reject("--start-all","--translation-model","voxtral-realtime-q4_k_m");
        Reject("--start-all","--speech-distribution","Ubuntu");
        Reject("--start-all","--translation-model");
        Reject("--start-all","--start-all");
        Reject("--start-all","--unknown");
        return checks;
    }
}

internal static class DesktopLog
{
    private static readonly object Gate=new();
    public static event Action<string>? Changed;
    public static string Path => System.IO.Path.Combine(AppPaths.Root,"saywhat.log");
    public static void Write(string message) {
        string line=$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try { lock(Gate) { Directory.CreateDirectory(AppPaths.Root); File.AppendAllText(Path,line+Environment.NewLine); } }
        catch(IOException) { System.Diagnostics.Debug.WriteLine(line); }
        catch(UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(line); }
        Changed?.Invoke(line);
    }
}

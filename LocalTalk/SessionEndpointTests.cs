using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace IncomingTranslator;

internal static class SessionEndpointTests
{
    internal static int Run(MainWindow window)
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Session endpoints: " + name); checks++; }
        string? testRoot = Environment.GetEnvironmentVariable(AppBrand.DataEnvironmentVariable);
        Check(!string.IsNullOrWhiteSpace(testRoot) && Path.GetFullPath(testRoot) == Path.GetFullPath(AppPaths.Root) && !window.IsVisible, "configuration writes require the isolated offline runner");
        var preferences = new AppSettings { UseManagedTranslator = true, OwnTranslatorBaseUrl = "http://localhost:7777/v1", TranslatorBaseUrl = "http://localhost:7778/v1" };
        var session = preferences.Copy();
        session.OwnSpeechEndpoint = "http://127.0.0.1:49161";
        session.IncomingSpeechEndpoint = "http://127.0.0.1:49162";
        session.OwnTranslationEndpoint = "http://127.0.0.1:49163/v1";
        session.IncomingTranslationEndpoint = "http://127.0.0.1:49164/v1";
        Check(SessionEndpoints.OwnSpeech(session).EndsWith(":49161"), "own capture follows the selected speech port");
        Check(SessionEndpoints.SpeechUri(session, "/v1/realtime/transcription", true).AbsoluteUri == "ws://127.0.0.1:49162/v1/realtime/transcription", "Voxtral reconnect uses the selected session port");
        Check(SessionEndpoints.SpeechUri(session, "/v1/audio/transcriptions/realtime", true).Port == 49162, "native reconnect uses the selected session port");
        Check(SessionEndpoints.IncomingTranslation(session) == "http://127.0.0.1:49164/v1", "subtitle requests follow their session translator");
        Check(SessionEndpoints.OwnTranslation(preferences) == preferences.OwnTranslatorBaseUrl, "external preference retains its chosen connection");
        AppSettings externalTls = preferences.Copy(); externalTls.UseManagedTranslator = false; externalTls.OwnTranslatorBaseUrl = "https://localhost:7777/v1";
        Check(SessionEndpoints.OwnTranslation(externalTls) == externalTls.OwnTranslatorBaseUrl, "configured local TLS translators remain supported");
        var refreshed = preferences.Copy(); refreshed.IncomingTargetLanguage = "Korean";
        AppSettings preserved = SessionEndpoints.PreserveRuntime(session, refreshed);
        Check(preserved.IncomingSpeechEndpoint == session.IncomingSpeechEndpoint && preserved.IncomingTranslationEndpoint == session.IncomingTranslationEndpoint && preserved.IncomingTargetLanguage == "Korean", "generic live settings retain current connections");
        string json = JsonSerializer.Serialize(session);
        Check(!json.Contains("4916", StringComparison.Ordinal), "ephemeral addresses never enter preference JSON");
        Check(!TranslationProfiles.Capture(session).ContainsKey(nameof(AppSettings.IncomingSpeechEndpoint)), "custom profiles exclude ephemeral endpoints");
        using (var overlay = new TestOverlay(session))
            Check(overlay.Settings.IncomingSpeechEndpoint == session.IncomingSpeechEndpoint && overlay.Settings.IncomingTranslationEndpoint == session.IncomingTranslationEndpoint,
                "overlay receives the runtime snapshot before its service starts");

        string[] configPaths = [AppPaths.FastConfigPath, AppPaths.FoxTransConfigPath];
        var originals = configPaths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllText(path) : null);
        string? savedSettings = File.Exists(AppPaths.SettingsPath) ? File.ReadAllText(AppPaths.SettingsPath) : null;
        try
        {
            foreach (string path in configPaths)
                File.WriteAllText(path, "{\"audio\":{},\"pipeline\":{\"speech\":{},\"translation\":{},\"realtime\":{}},\"outputs\":[{\"type\":\"vrchat-osc\",\"address\":\"127.0.0.1:9010\"}]}");
            var unusedExternal = preferences.Copy(); unusedExternal.OwnTranslatorBaseUrl = "unused external URL draft";
            SettingsStore.Save(unusedExternal);
            Check(SettingsStore.Load().OwnTranslatorBaseUrl == unusedExternal.OwnTranslatorBaseUrl &&
                JsonNode.Parse(File.ReadAllText(configPaths[0]))!["pipeline"]!["translation"]!["baseUrl"]!.GetValue<string>() == "http://127.0.0.1:1235/v1",
                "inactive external URL choices cannot block managed configuration saves");
            SettingsStore.WriteOwnVoiceConfig(session, session.OwnSpeechEndpoint, session.OwnTranslationEndpoint);
            SettingsStore.Save(refreshed);
            foreach (string path in configPaths)
            {
                JsonNode config = JsonNode.Parse(File.ReadAllText(path))!;
                Check(config["pipeline"]!["speech"]!["baseUrl"]!.GetValue<string>() == session.OwnSpeechEndpoint &&
                    config["pipeline"]!["translation"]!["baseUrl"]!.GetValue<string>() == session.OwnTranslationEndpoint,
                    "independent preference saves retain the ready host connections");
                Check(config["outputs"]![0]!["address"]!.GetValue<string>() == "127.0.0.1:9010", "OSC route remains unchanged");
            }
            AppSettings loaded = SettingsStore.Load();
            Check(loaded.OwnSpeechEndpoint.Length == 0 && loaded.OwnTranslationEndpoint.Length == 0 &&
                loaded.OwnTranslatorBaseUrl == preferences.OwnTranslatorBaseUrl, "saving active tools cannot replace external URL preferences");
            SettingsStore.ClearOwnRuntimeEndpoints();
            SettingsStore.Save(preferences);
            Check(JsonNode.Parse(File.ReadAllText(configPaths[0]))!["pipeline"]!["speech"]!["baseUrl"]!.GetValue<string>() != session.OwnSpeechEndpoint,
                "full stop removes the old session override");
        }
        finally
        {
            SettingsStore.ClearOwnRuntimeEndpoints();
            foreach ((string path, string? original) in originals) { if (original is null) File.Delete(path); else File.WriteAllText(path, original); }
            if (savedSettings is null) File.Delete(AppPaths.SettingsPath); else File.WriteAllText(AppPaths.SettingsPath, savedSettings);
        }

        var field = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalSettings = (AppSettings)field.GetValue(window)!;
        try
        {
            var ui = originalSettings.Copy(); field.SetValue(window, ui);
            var section = (StackPanel)typeof(MainWindow).GetMethod("ConnectionSettingsSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            UiLocalization.ApplyTree(section);
            var automatic = Descendants(section).OfType<CheckBox>().Single(box => Equals(box.Content, UiText.T("Choose local ports automatically (recommended)")));
            var port = Descendants(section).OfType<TextBox>().Single(box => box.Name == "OwnSpeechPort");
            Check(automatic.IsChecked == true && !port.IsEnabled, "automatic is the default and fixed fields stay inactive");
            automatic.IsChecked = false;
            Check(!ui.AutomaticManagedPorts && port.IsEnabled, "advanced users can select fixed connections");
            port.Text = "53210";
            Check(ui.OwnSpeechPort == 53210 && SettingsStore.Load().AutomaticManagedPorts, "connection edits stay staged until Save all");
        }
        finally { field.SetValue(window, originalSettings); }
        var fixedSettings = preferences.Copy(); fixedSettings.AutomaticManagedPorts = false;
        var changed = fixedSettings.Copy(); changed.OwnSpeechPort = 49001;
        Check(SettingsRuntimeChanges.Between(fixedSettings, changed) is { OwnChanged: true, IncomingChanged: false }, "own fixed speech change only affects own direction");
        changed = fixedSettings.Copy(); changed.IncomingSpeechPort = 49002;
        Check(SettingsRuntimeChanges.Between(fixedSettings, changed) is { OwnChanged: false, IncomingChanged: true }, "incoming fixed speech change only affects subtitles");
        var ignored = preferences.Copy(); ignored.OwnSpeechPort = 1; ignored.IncomingSpeechPort = 0;
        Check(SettingsRuntimeChanges.Between(preferences, ignored) is { OwnChanged: false, IncomingChanged: false }, "unused fixed choices do not restart automatic connections");
        checks += Task.Run(() => CheckProtocolAddressesAsync(session, refreshed)).GetAwaiter().GetResult();
        return checks;
    }

    private static async Task<int> CheckProtocolAddressesAsync(AppSettings session, AppSettings refreshed)
    {
        int checks = 0;
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Session protocol: " + message); checks++; }
        using var handler = new EndpointHandler();
        using var service = new IncomingTranslationService(session, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, translationHandler: handler);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        service.UpdateSettings(refreshed);
        AppSettings active = (AppSettings)typeof(IncomingTranslationService).GetMethod("CurrentSettings", flags)!.Invoke(service, null)!;
        Check(active.IncomingSpeechEndpoint == session.IncomingSpeechEndpoint && active.IncomingTranslationEndpoint == session.IncomingTranslationEndpoint, "service live refresh retains resolved addresses");
        var websocket = (Uri)typeof(IncomingTranslationService).GetProperty("VoxtralSocket", flags)!.GetValue(service)!;
        Check(websocket.Port == 49162, "actual reconnect URI follows the ready speech server");
        await (Task)typeof(IncomingTranslationService).GetMethod("WaitForSpeechServerAsync", flags)!.Invoke(service, [CancellationToken.None])!;
        Check(handler.Requests.Single().AbsoluteUri == "http://127.0.0.1:49162/health", "stream-release health probe follows the same speech address");
        string result = await (Task<string>)typeof(IncomingTranslationService).GetMethod("RequestTranslationAsync", flags)!.Invoke(service, ["synthetic words", "Translate", active, CancellationToken.None, false])!;
        Check(result == "synthetic result" && handler.Requests.Last().AbsoluteUri == "http://127.0.0.1:49164/v1/chat/completions", "real request builder uses the resolved translator URL after a settings refresh");
        return checks;
    }

    private sealed class EndpointHandler : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            string body = request.Method == HttpMethod.Get ? "{\"ready\":true,\"busy\":false}" : "{\"choices\":[{\"message\":{\"content\":\"synthetic result\"}}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class TestOverlay : IDisposable
    {
        private readonly SubtitleWindow _window;
        internal AppSettings Settings => (AppSettings)typeof(SubtitleWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_window)!;
        internal TestOverlay(AppSettings settings) { _window = new SubtitleWindow(settings); }
        public void Dispose() => _window.Close();
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        yield return root;
        foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(root))
            if (child is System.Windows.DependencyObject node)
                foreach (var descendant in Descendants(node)) yield return descendant;
    }
}

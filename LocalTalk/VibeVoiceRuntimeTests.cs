namespace IncomingTranslator;

internal static class VibeVoiceRuntimeTests
{
    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Groups runtime: " + message); checks++; }
        void Reject(Action action, string message)
        { try { action(); } catch (ArgumentException) { checks++; return; } throw new InvalidOperationException("Groups runtime accepted " + message); }
        string owner = "0123456789abcdef0123456789abcdef";
        WslSpeechRegistration registration = new("A-Speech-Distro", "/opt/unused/voxtral-server", "/synthetic/model.gguf", "cuda");
        string data = Path.Combine(directory, "groups-inert-data");
        var start = VibeVoiceRuntime.BuildStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId, owner);
        Check(start.CreateNoWindow && !start.UseShellExecute && start.RedirectStandardOutput && start.RedirectStandardError, "no console windows and diagnostics captured");
        Check(start.ArgumentList.Contains(registration.Distribution) && !start.ArgumentList.Contains("Ubuntu"), "uses registered distro, not a hardcoded Ubuntu");
        Check(start.ArgumentList.TakeLast(2).SequenceEqual(new[] { owner, "8082" }), "ownership token and default port are retained");
        Check(!start.ArgumentList.Contains("root"), "preserves imported distribution user");
        Check(VibeVoiceRuntime.BuildStartInfo(registration with { RunAsRoot = true }, data, VibeVoiceRuntime.DefaultModelId, owner).ArgumentList.Contains("root"), "managed distro can run as registered root");
        Check(VibeVoiceRuntime.CreateCheckStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId).ArgumentList.Contains("--check"), "setup inspection is read-only");
        Check(VibeVoiceRuntime.CreatePrepareStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId).ArgumentList.Contains("--prepare"), "prepare is an explicit distinct operation");
        Check(VibeVoiceRuntime.BuildStopStartInfo(registration, owner).ArgumentList.TakeLast(2).SequenceEqual(new[] { owner, "8082" }), "stop is scoped by owner and selected port, not a port sweep");
        foreach (int port in new[] { 1024, 25347, 65535 })
        {
            string expected = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Check(VibeVoiceRuntime.BuildStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId, owner, port).ArgumentList.TakeLast(2).SequenceEqual(new[] { owner, expected }),
                "start passes a non-default safe port as one inert argument");
            Check(VibeVoiceRuntime.BuildStopStartInfo(registration, owner, port).ArgumentList.TakeLast(2).SequenceEqual(new[] { owner, expected }),
                "stop retains the exact selected port with its owner");
        }
        foreach (int port in new[] { -1, 0, 1023, 65536, int.MaxValue })
        {
            Reject(() => VibeVoiceRuntime.BuildStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId, owner, port), "invalid start port");
            Reject(() => VibeVoiceRuntime.BuildStopStartInfo(registration, owner, port), "invalid stop port");
        }
        Check(!Directory.Exists(data), "constructing commands creates no app data or model loads");
        Reject(() => VibeVoiceRuntime.BuildStartInfo(registration, data, "microsoft/VibeVoice-ASR", owner), "offline non-streaming model");
        Reject(() => VibeVoiceRuntime.BuildStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId, "--injected"), "invalid ownership token");
        Reject(() => VibeVoiceRuntime.BuildStartInfo(registration with { Distribution = "Ubuntu;bad" }, data, VibeVoiceRuntime.DefaultModelId, owner), "invalid distribution");
        Reject(() => VibeVoiceRuntime.BuildStartInfo(registration, @"C:\", VibeVoiceRuntime.DefaultModelId, owner), "drive root data");
        foreach (string endpoint in new[] { "https://127.0.0.1:8082", "http://example.com:8082", "http://127.0.0.1:0", "http://127.0.0.1:1023",
            "http://127.0.0.1:65536", "http://user@127.0.0.1:8082", "http://127.0.0.1:8082/remote", "http://127.0.0.1:25347?query=1", "http://127.0.0.1:25347/#fragment" })
            Reject(() => VibeVoiceRuntime.ValidateEndpoint(endpoint), "non-local or malformed endpoint");
        Check(VibeVoiceRuntime.ValidateEndpoint(VibeVoiceRuntime.Endpoint).Port == 8082, "canonical loopback accepted");
        foreach (string endpoint in new[] { "http://127.0.0.1:1024", "http://localhost:25347", "http://[::1]:65535" })
            Check(VibeVoiceRuntime.ValidateEndpoint(endpoint).IsLoopback, "safe non-default IPv4, localhost and IPv6 loopback addresses accepted");
        Check(VibeVoiceRuntime.Models.Count == 2 && VibeVoiceRuntime.Models.All(model => model.Id.Contains("ASR-Streaming")), "only real streaming checkpoints catalogued");
        string receipt = "progress\n{\"prepared\":true,\"model_id\":\"" + VibeVoiceRuntime.DefaultModelId + "\",\"model_path\":\"/synthetic/model\",\"runtime_root\":\"/synthetic/runtime\",\"missing\":[]}\n";
        Check(VibeVoiceRuntime.ParsePreparation(receipt, VibeVoiceRuntime.DefaultModelId).Prepared, "accepts matching successful receipt");
        Check(!VibeVoiceRuntime.ParsePreparation(receipt, VibeVoiceRuntime.Models[1].Id).Prepared, "wrong model never appears prepared");
        Check(!VibeVoiceRuntime.ParsePreparation("no receipt", VibeVoiceRuntime.DefaultModelId).Prepared, "missing receipt fails clearly");
        return checks;
    }
}

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
        Check(start.ArgumentList.Last() == owner, "ownership token is retained");
        Check(!start.ArgumentList.Contains("root"), "preserves imported distribution user");
        Check(VibeVoiceRuntime.BuildStartInfo(registration with { RunAsRoot = true }, data, VibeVoiceRuntime.DefaultModelId, owner).ArgumentList.Contains("root"), "managed distro can run as registered root");
        Check(VibeVoiceRuntime.CreateCheckStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId).ArgumentList.Contains("--check"), "setup inspection is read-only");
        Check(VibeVoiceRuntime.CreatePrepareStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId).ArgumentList.Contains("--prepare"), "prepare is an explicit distinct operation");
        Check(VibeVoiceRuntime.BuildStopStartInfo(registration, owner).ArgumentList.Last() == owner, "stop is scoped by owner, not a port sweep");
        Check(!Directory.Exists(data), "constructing commands creates no app data or model loads");
        Reject(() => VibeVoiceRuntime.BuildStartInfo(registration, data, "microsoft/VibeVoice-ASR", owner), "offline non-streaming model");
        Reject(() => VibeVoiceRuntime.BuildStartInfo(registration, data, VibeVoiceRuntime.DefaultModelId, "--injected"), "invalid ownership token");
        Reject(() => VibeVoiceRuntime.BuildStartInfo(registration with { Distribution = "Ubuntu;bad" }, data, VibeVoiceRuntime.DefaultModelId, owner), "invalid distribution");
        Reject(() => VibeVoiceRuntime.BuildStartInfo(registration, @"C:\", VibeVoiceRuntime.DefaultModelId, owner), "drive root data");
        foreach (string endpoint in new[] { "https://127.0.0.1:8082", "http://example.com:8082", "http://127.0.0.1:8081", "http://user@127.0.0.1:8082", "http://127.0.0.1:8082/remote" })
            Reject(() => VibeVoiceRuntime.ValidateEndpoint(endpoint), "non-local or malformed endpoint");
        Check(VibeVoiceRuntime.ValidateEndpoint(VibeVoiceRuntime.Endpoint).Port == 8082, "canonical loopback accepted");
        Check(VibeVoiceRuntime.Models.Count == 2 && VibeVoiceRuntime.Models.All(model => model.Id.Contains("ASR-Streaming")), "only real streaming checkpoints catalogued");
        string receipt = "progress\n{\"prepared\":true,\"model_id\":\"" + VibeVoiceRuntime.DefaultModelId + "\",\"model_path\":\"/synthetic/model\",\"runtime_root\":\"/synthetic/runtime\",\"missing\":[]}\n";
        Check(VibeVoiceRuntime.ParsePreparation(receipt, VibeVoiceRuntime.DefaultModelId).Prepared, "accepts matching successful receipt");
        Check(!VibeVoiceRuntime.ParsePreparation(receipt, VibeVoiceRuntime.Models[1].Id).Prepared, "wrong model never appears prepared");
        Check(!VibeVoiceRuntime.ParsePreparation("no receipt", VibeVoiceRuntime.DefaultModelId).Prepared, "missing receipt fails clearly");
        return checks;
    }
}

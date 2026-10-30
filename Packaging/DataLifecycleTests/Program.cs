using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// This test program uses only tiny synthetic workspace files. It leaves fixtures
// for inspection: no recursive cleanup, link creation, process killing, live AppData,
// model loading, audio capture, protection-setting changes or installer execution.
internal static class Program
{
    private static readonly string[] Preferences = ["vrchat-translation-settings.json", "own-voice-runtime.json", "config.fast-chinese.jsonc", "config.sentence-mode.jsonc",
        "config.fast-chinese.jsonc.tmp-0123456789abcdef0123456789abcdef", "own-voice-runtime.json.tmp-0123456789abcdef0123456789abcdef"];
    private static readonly string[] History = ["incoming-ai-speaker-profiles.json", "incoming-speaker-feedback.json", "incoming-speaker-profiles.json",
        "saywhat.log", "localtalk.log", "saywhat-support-last-failure.txt", "saywhat-support-last-failure.txt.tmp-0123456789abcdef0123456789abcdef", "incoming-translator.log", "incoming-vr-overlay.log", "foxtrans-local-host.log", "foxtrans-mute-sync.log",
        "foxtrans-osc-sentence.log", "foxtrans-provider-errors.log", "incoming-ai-speaker-profiles.json.before-named-feedback-v1.backup",
        "incoming-ai-speaker-profiles.json.backup-20261005-123456-1234567", "incoming-speaker-feedback.json.tmp-0123456789abcdef0123456789abcdef",
        "incoming-ai-speaker-profiles.json.tmp", "people-photos/0123456789abcdef0123456789abcdef.png",
        "people-photos/0123456789abcdef0123456789abcdef.png.tmp-0123456789abcdef0123456789abcdef"];
    private static readonly string[] Models = ["models/Qwen_Qwen3-1.7B-Q4_K_M.gguf", "models/Qwen3-4B-Q4_K_M.gguf", "models/Qwen3-8B-Q4_K_M.gguf",
        "models/Qwen3-14B-Q4_K_M.gguf", "models/Hy-MT2-1.8B-Q4_K_M.gguf", "models/Hy-MT2-7B-Q4_K_M.gguf",
        "models/translategemma-4b-it-Q4_K_M.gguf", "models/LFM2-350M-ENJP-MT-Q4_K_M.gguf", "models/Voxtral-Mini-4B-Realtime-2602-Q4_K_M.gguf",
        "models/own-speech/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf",
        "models/own-speech/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf.part-0123456789abcdef0123456789abcdef",
        "models/own-speech/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf.partial-0123456789abcdef0123456789abcdef",
        "models/own-speech/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf.download",
        "models/speaker-id/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx", "models/speaker-id/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
        "models/separation/mossformer2-ss-16k.int8.onnx", "models/Hy-MT2-1.8B-Q4_K_M.gguf.part-0123456789abcdef0123456789abcdef",
        "models/Qwen3-8B-Q4_K_M.gguf.partial-0123456789abcdef0123456789abcdef", "models/speaker-id/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx.download"];
    private static readonly string[] Retained = ["runtimes/llama-vulkan/synthetic-runtime.dll", "downloads/synthetic.zip", "custom.log", "private-notes.txt",
        "incoming-speaker-feedback.json.unrecognized", "custom-folder/keep.txt", "setup-progress.json", "config.fast-chinese.jsonc.tmp-unrecognized",
        "incoming-ai-speaker-profiles.json.backup-unrecognized", "models/translation/synthetic.gguf", "models/speaker-id/synthetic.onnx",
        "models/Hy-MT2-1.8B-Q4_K_M.gguf.part-not-a-guid",
        "models/own-speech/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf.part-not-a-guid", "models/own-speech/synthetic-other.gguf",
        "people-photos/keep-original.jpg", "people-photos/not-a-guid.png"];
    private static readonly string[] AllActions = ["ResetPreferences", "ClearHistory", "DeleteCachedModels"];
    private static int _checks, _helperRuns;
    private static string _packaging = "", _fixtures = "", _helper = "", _helperHash = "";

    private static async Task<int> Main()
    {
        try
        {
            DirectoryInfo? folder = new(AppContext.BaseDirectory);
            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "scripts", "manage-install-data.ps1"))) folder = folder.Parent;
            _packaging = folder?.FullName ?? throw new InvalidOperationException("Run this project from the workspace Packaging tree.");
            _helper = Path.Combine(_packaging, "scripts", "manage-install-data.ps1");
            _helperHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_helper)));
            _fixtures = Path.Combine(_packaging, "offline-lifecycle-fixtures", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_fixtures, "receipts"));
            await RunAsync();
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_helper))) == _helperHash, "the helper source remains unchanged through the final check");
            string report = Path.Combine(_fixtures, "results.json");
            File.WriteAllText(report, JsonSerializer.Serialize(new { Status = "Passed", Checks = _checks, HelperRuns = _helperRuns, HelperSha256 = _helperHash,
                Fixtures = _fixtures, Safety = "Tiny synthetic workspace files only. No live data, models, audio, links, process stopping, protection changes or actual installer execution." }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} checks, {_helperRuns} reviewed-helper calls. Report: {report}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Offline lifecycle test failed: " + error.Message);
            if (_fixtures.Length > 0)
            {
                string report = Path.Combine(_fixtures, "results.json");
                File.WriteAllText(report, JsonSerializer.Serialize(new { Status = "Failed", Checks = _checks, HelperRuns = _helperRuns, HelperSha256 = _helperHash, Error = error.Message,
                    Fixtures = _fixtures }, new JsonSerializerOptions { WriteIndented = true }));
                Console.Error.WriteLine("Inspectable fixtures/report: " + report);
            }
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        await IdentityPolicyFixturesAsync();
        foreach (string leaf in new[] { "SayWhat", "LocalTalk" })
        {
            Fixture keep = CreateFixture("keep-" + leaf, leaf);
            string before = Snapshot(keep.Root);
            Outcome kept = await InvokeAsync(keep.Root, []);
            Check(Snapshot(keep.Root) == before, leaf + " no-choice mode preserves all files");
            Check(kept.RemovedFiles == 0, "keep receipt counts zero removals");
            Outcome validated = await InvokeAsync(keep.Root, [.. AllActions, "ValidateOnly"]);
            Check(Snapshot(keep.Root) == before, leaf + " ValidateOnly preserves every selected file");
            Check(validated.RemovedFiles == 0, "validation receipt counts zero removals");
        }
        foreach (string action in new[] { "ResetPreferences", "ClearHistory", "DeleteCachedModels", "All", "LegacyAll" })
        {
            Fixture fixture = CreateFixture("action-" + action, action == "LegacyAll" ? "LocalTalk" : "SayWhat");
            bool reset = action is "ResetPreferences" or "All" or "LegacyAll";
            bool clear = action is "ClearHistory" or "All" or "LegacyAll";
            bool models = action is "DeleteCachedModels" or "All" or "LegacyAll";
            string[] options = action is "All" or "LegacyAll" ? AllActions : [action];
            Outcome result = await InvokeAsync(fixture.Root, options);
            foreach (string name in Preferences) Check(File.Exists(fixture.Path(name)) == !reset, action + " preference independence: " + name);
            foreach (string name in History) Check(File.Exists(fixture.Path(name)) == !clear, action + " history/backup independence: " + name);
            foreach (string name in Models) Check(File.Exists(fixture.Path(name)) == !models, action + " known model/partial independence: " + name);
            Check(result.RemovedFiles == (reset ? Preferences.Length : 0) + (clear ? History.Length : 0) + (models ? Models.Length : 0),
                action + " receipt counts only removed files, not rewritten settings");
            PreserveAssets(fixture);
            Check(result.BackupPath == "" && Directory.GetDirectories(fixture.Root, ".installer-data-change-*", SearchOption.TopDirectoryOnly).Length == 0,
                "successful removal leaves no hidden history backup/transaction");
            if (!reset)
            {
                JsonNode expected = JsonNode.Parse(fixture.OriginalSettings)!;
                if (models) expected["FirstRunSetupCompleted"] = false;
                Check(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(fixture.Path("vrchat-translation-settings.json")))),
                    "cache-only cleanup changes just the setup readiness flag; languages/imports/unknown preferences are preserved");
            }
        }

        Fixture wrongType = CreateFixture("known-file-is-directory");
        File.Delete(wrongType.Path("config.fast-chinese.jsonc")); Directory.CreateDirectory(wrongType.Path("config.fast-chinese.jsonc"));
        await RefusesWithoutChangesAsync(wrongType, AllActions, "state-file directory fails complete preflight before any changes");

        Fixture readOnly = CreateFixture("read-only-file");
        File.SetAttributes(readOnly.Path(Models[0]), File.GetAttributes(readOnly.Path(Models[0])) | FileAttributes.ReadOnly);
        await RefusesWithoutChangesAsync(readOnly, AllActions, "read-only model blocks all selected categories");

        Fixture locked = CreateFixture("locked-file");
        using (FileStream held = File.Open(locked.Path("incoming-speaker-feedback.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await RefusesWithoutChangesAsync(locked, AllActions, "locked history file blocks all selected categories");

        Fixture invalidJson = CreateFixture("invalid-settings-json");
        File.WriteAllText(invalidJson.Path("vrchat-translation-settings.json"), "{invalid fixture JSON");
        await RefusesWithoutChangesAsync(invalidJson, ["DeleteCachedModels"], "invalid settings leave all models and preferences untouched");

        Fixture scalarJson = CreateFixture("scalar-settings-json");
        File.WriteAllText(scalarJson.Path("vrchat-translation-settings.json"), "42");
        await RefusesWithoutChangesAsync(scalarJson, ["DeleteCachedModels"], "non-object settings cannot be silently replaced");

        Fixture badLeaf = CreateFixture("nonstandard-leaf", "NotAppData");
        await RefusesWithoutChangesAsync(badLeaf, AllActions, "non-app-data leaf is rejected before any changes");

        Fixture receiptOverlap = CreateFixture("receipt-overlap");
        string overlapBefore = Snapshot(receiptOverlap.Root);
        await InvokeAsync(receiptOverlap.Root, AllActions, false, false, receiptOverlap.Path("private-notes.txt"));
        Check(Snapshot(receiptOverlap.Root) == overlapBefore, "receipt overlap cannot overwrite data before preflight");

        // Broad REAL paths are validation-only and have no deletion switches whatsoever.
        await InvokeAsync(_packaging, ["ValidateOnly"], false, false);
        await InvokeAsync(Path.GetPathRoot(_packaging)!, ["ValidateOnly"], false, false);

        string missing = Path.Combine(_fixtures, "missing", "SayWhat");
        Outcome missingResult = await InvokeAsync(missing, AllActions);
        Check(!Directory.Exists(missing) && missingResult.RemovedFiles == 0, "missing data root remains missing/no-op");

        Fixture initiating = CreateFixture("shared-install-initiating-account");
        Fixture otherUser = CreateFixture("shared-install-other-account");
        string otherBefore = Snapshot(otherUser.Root);
        await InvokeAsync(initiating.Root, AllActions);
        Check(Snapshot(otherUser.Root) == otherBefore, "an explicit shared-install cleanup changes only the selected synthetic account; another account remains byte-for-byte untouched");
        PreserveAssets(otherUser);

        Fixture bound = CreateFixture("bound-root-refusal");
        string boundBefore = Snapshot(bound.Root);
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            await InvokeAsync(bound.Root, AllActions, false, expectedUserSid: identity.User!.Value);
            Check(Snapshot(bound.Root) == boundBefore, "even the current SID cannot authorize an arbitrary folder named SayWhat outside its real private app-data location");
        }
        await InvokeAsync(bound.Root, AllActions, false, expectedUserSid: "S-1-5-21-1-2-3-99999");
        Check(Snapshot(bound.Root) == boundBefore, "an alternate Windows SID cannot change any selected data");
        await InvokeAsync(bound.Root, AllActions, false, expectedUserSid: "not-a-sid");
        Check(Snapshot(bound.Root) == boundBefore, "a malformed binding fails before any change");
        await InvokeAsync(bound.Root, ["ResetPreferences", "ResolveUserIdentity"], false, false);
        Check(Snapshot(bound.Root) == boundBefore, "identity resolution cannot be combined with destructive choices");

        // Real compatibility gate, not a public bypass or a fake translation process.
        Fixture gate = CreateFixture("closed-state-mutex");
        using (var mutex = new Mutex(false, "Local\\LocalTalkDesktop", out bool created))
        {
            Check(created, "offline gate fixture cannot take over a running desktop");
            // The helper rejects an existing compatibility guard. The test only retains
            // its handle, never owns a thread-affine lock across an asynchronous wait.
            await RefusesWithoutChangesAsync(gate, AllActions, "an existing desktop guard prevents cleanup");
        }
    }

    private static async Task IdentityPolicyFixturesAsync()
    {
        string script = Path.Combine(_packaging, "DataLifecycleTests", "Test-IdentityPolicy.ps1");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-FixtureBase", _fixtures, "-SourceHelper", _helper }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The pure identity policy fixtures could not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        string stdout = await output, stderr = await error;
        File.WriteAllText(Path.Combine(_fixtures, "identity-policy.output.txt"), stdout + stderr);
        Check(process.ExitCode == 0, "pure identity policy fixtures: " + stderr);
        using JsonDocument results = JsonDocument.Parse(stdout);
        foreach (JsonElement check in results.RootElement.GetProperty("Checks").EnumerateArray()) Check(true, check.GetString()!);
    }

    private static async Task RefusesWithoutChangesAsync(Fixture fixture, string[] options, string description)
    {
        string before = Snapshot(fixture.Root);
        Outcome result = await InvokeAsync(fixture.Root, options, false);
        Check(result.RemovedFiles == 0 && result.BackupPath == "", "preflight rejection reports no deletion/hidden backup");
        Check(Snapshot(fixture.Root) == before, description);
        Check(File.ReadAllText(fixture.External) == ExternalBody, "rejected cleanup cannot change the imported external model");
    }

    private const string ExternalBody = "TINY SYNTHETIC EXTERNAL MODEL; preserve this marker";
    private static Fixture CreateFixture(string name, string leaf = "SayWhat")
    {
        string parent = Path.Combine(_fixtures, name), root = Path.Combine(parent, leaf);
        string external = Path.Combine(parent, "external", "imported-model.gguf");
        WriteMarker(external, ExternalBody);
        foreach (string file in Preferences.Concat(History).Concat(Models).Concat(Retained)) WriteMarker(Path.Combine(root, file), MarkerBody(file));
        string settings = JsonSerializer.Serialize(new { SpeakerModelPath = external, OwnVoiceTargetLanguage = "Chinese", IncomingTargetLanguage = "English",
            FirstRunSetupCompleted = true, CustomOption = new { Saved = "keep this", Limit = 7 } });
        string provisioning = JsonSerializer.Serialize(new { ModelReferences = new Dictionary<string, string> { ["external-import"] = external,
            ["cached-reference"] = Path.Combine(root, Models[0]) }, Speech = new { Distribution = "LocalTalk-Ubuntu",
                ServerPath = "/opt/localtalk-voxtral/pinned-localtalk-r2/voxtral-server", ModelPath = "/opt/localtalk-voxtral/models/realtime.gguf", Backend = "cuda" } });
        WriteMarker(Path.Combine(root, "vrchat-translation-settings.json"), settings);
        WriteMarker(Path.Combine(root, "provisioning.json"), provisioning);
        return new Fixture(root, external, settings, provisioning);
    }
    private static string MarkerBody(string path) => "TINY SYNTHETIC CONTENT " + path;
    private static void WriteMarker(string path, string body)
    {
        string resolved = Path.GetFullPath(path);
        Check(resolved.StartsWith(_fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "all fixture writes stay inside the unique workspace test directory");
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllText(resolved, body, new UTF8Encoding(false));
    }
    private static void PreserveAssets(Fixture fixture)
    {
        Check(File.ReadAllText(fixture.External) == ExternalBody, "external imported models are not removed");
        Check(File.ReadAllText(fixture.Path("provisioning.json")) == fixture.OriginalProvisioning, "external model references and existing WSL registration are byte-for-byte unchanged");
        foreach (string path in Retained) Check(File.ReadAllText(fixture.Path(path)) == MarkerBody(path), "unknown file/cache/runtime/setup-checkpoint retained: " + path);
    }
    private static string Snapshot(string root)
    {
        var rows = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("A fixture unexpectedly became a linked path; do not traverse it.");
                string relative = Path.GetRelativePath(root, path);
                if ((attributes & FileAttributes.Directory) != 0) { rows.Add("D|" + relative); pending.Push(path); }
                else rows.Add("F|" + relative + "|" + attributes + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            }
        }
        rows.Sort(StringComparer.Ordinal); return string.Join("\n", rows);
    }

    private static async Task<Outcome> InvokeAsync(string dataRoot, string[] options, bool success = true, bool requireReceipt = true, string? receiptOverride = null, string? expectedUserSid = null)
    {
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_helper))) == _helperHash, "each call uses the recorded unchanged reviewed helper");
        int id = ++_helperRuns;
        string receipt = receiptOverride ?? Path.Combine(_fixtures, "receipts", $"{id:000}.ini");
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _helper, "-DataRoot", dataRoot, "-ReceiptPath", receipt }) start.ArgumentList.Add(arg);
        foreach (string option in options)
        {
            if (!AllActions.Contains(option) && option is not ("ValidateOnly" or "ResolveUserIdentity")) throw new InvalidOperationException("Unknown fixture switch.");
            start.ArgumentList.Add("-" + option);
        }
        if (expectedUserSid is not null)
        {
            foreach (string arg in new[] { "-RequirePrivateUserRoot", "-ExpectedUserSid", expectedUserSid }) start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The reviewed helper could not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException($"Reviewed helper exceeded 30 seconds (PID {process.Id}); no process was killed. Fixtures: {_fixtures}"); }
        string stdout = await output, stderr = await error;
        File.WriteAllText(Path.Combine(_fixtures, "receipts", $"{id:000}.output.txt"), stdout + stderr);
        Check((process.ExitCode == 0) == success, $"helper outcome for {string.Join(",", options)}: exit {process.ExitCode}; {stderr}");
        if (!requireReceipt) return new Outcome(0, "");
        Check(File.Exists(receipt), "helper produced a structured receipt");
        byte[] bytes = File.ReadAllBytes(receipt);
        Check(bytes.Length > 2 && bytes[0] == 255 && bytes[1] == 254, "receipt encoding is UTF-16 with BOM");
        string text = File.ReadAllText(receipt);
        Check(text.StartsWith("[Result]", StringComparison.Ordinal), "receipt has Result section");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int equal = line.IndexOf('='); if (equal >= 0) values.Add(line[..equal], line[(equal + 1)..]);
        }
        foreach (string field in new[] { "Success", "Message", "Details", "BackupPath", "RemovedFiles" }) Check(values.ContainsKey(field), "receipt includes " + field);
        Check(values["Success"] == (success ? "1" : "0"), "receipt success matches process exit");
        Check(!text.Contains("TINY SYNTHETIC CONTENT", StringComparison.Ordinal) && !text.Contains(ExternalBody, StringComparison.Ordinal), "receipt contains no settings/model/history contents");
        Check(int.TryParse(values["RemovedFiles"], out int count) && count >= 0, "receipt has bounded nonnegative file count");
        return new Outcome(count, values["BackupPath"]);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
    }
    private sealed record Fixture(string Root, string External, string OriginalSettings, string OriginalProvisioning)
    {
        internal string Path(string relative) => System.IO.Path.Combine(Root, relative);
    }
    private sealed record Outcome(int RemovedFiles, string BackupPath);
}

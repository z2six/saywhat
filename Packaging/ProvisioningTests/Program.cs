using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IncomingTranslator;

OfflineErrorMode.SuppressDialogs();

if (args.Length == 3 && args[0] == "--bundle-runtime")
{
    // Packaging-only path: verify/extract the official pinned binaries, never execute them.
    ProvisionRuntimeChoice runtime = LocalTalkProvisioner.RuntimeCatalog.Single(item => item.Id == "llama-vulkan");
    ProvisionAsset asset = runtime.Assets.Single();
    string archive = Path.GetFullPath(args[1]), destination = Path.GetFullPath(args[2]);
    if (new FileInfo(archive).Length != asset.SizeBytes ||
        !await LocalTalkProvisioner.VerifyDigestAsync(archive, asset.Sha256, CancellationToken.None))
        throw new InvalidDataException("Bundled runtime archive failed its pinned size/SHA-256 check.");
    if (Directory.Exists(destination)) throw new IOException("Bundled runtime staging destination must be new.");
    Directory.CreateDirectory(destination);
    LocalTalkProvisioner.ExtractVerifiedZip(archive, destination, CancellationToken.None);
    await File.WriteAllTextAsync(Path.Combine(destination, "localtalk-runtime.json"),
        System.Text.Json.JsonSerializer.Serialize(new { runtime.Id, Version = LocalTalkProvisioner.LlamaVersion, Hashes = runtime.Assets.Select(item => item.Sha256) }));
    if (LocalTalkProvisioner.FindPinnedRuntimeExecutable(destination, runtime.Id) is null)
        throw new InvalidDataException("The bundled runtime could not be confirmed.");
    Console.WriteLine("Verified and safely extracted pinned Vulkan runtime. No engine was executed.");
    return;
}

string testRoot = Path.Combine(Path.GetFullPath(args.Length > 0 ? args[0] : AppContext.BaseDirectory), "provisioning-offline-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var checks = new List<string>();
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
try
{
    using var http = new HttpClient(new FakeDownloadHandler());
    using var provisioner = new LocalTalkProvisioner(testRoot, http);
    Check(LocalTalkProvisioner.ModelCatalog.Count == 9 && LocalTalkProvisioner.ModelCatalog[2].Id == "qwen3-8b-q4_k_m", "Catalog preserves Qwen ordering and appends dedicated translation options");
    Check(LocalTalkProvisioner.ModelCatalog.Where(model => model.Id is "translategemma-4b-q4_k_m" or "lfm2-350m-enjp-mt-q4_k_m")
        .All(model => model.RequiresTermsAcceptance && model.SourceRepositoryUrl.StartsWith("https://", StringComparison.Ordinal)),
        "Optional dedicated models retain explicit terms acceptance and source provenance");
    Check(LocalTalkProvisioner.ModelCatalog.All(model => model.Sha256.Length == 64 && model.DownloadUrl.StartsWith("https://", StringComparison.Ordinal)), "Every model has a pinned HTTPS download and SHA256");
    Check(LocalTalkProvisioner.RuntimeCatalog.All(runtime => runtime.Assets.All(asset => asset.Sha256.Length == 64 && asset.DownloadUrl.Contains("/b11401/", StringComparison.Ordinal))), "Native Windows runtime assets are version-and-hash pinned");
    Check(!Directory.Exists(provisioner.ModelsRoot), "Inspection/construction does not create models or start a runtime");
    Check(new ProvisionProgress("Downloading", "fixture", 25, 100).Fraction == 0.25,
        "Byte-based download progress keeps its original received-to-total ratio");
    Check(new ProvisionProgress("Packages", "fixture", 99, 100) { WorkFraction = 0.4 }.Fraction == 0.4,
        "Finite task work progress takes precedence without inventing byte counts");
    Check(new ProvisionProgress("Packages", "fixture") { WorkFraction = -0.5 }.Fraction == 0 &&
        new ProvisionProgress("Packages", "fixture") { WorkFraction = 1.5 }.Fraction == 1,
        "Finite work fractions are safely clamped between zero and one");
    foreach (double nonFinite in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        Check(new ProvisionProgress("Packages", "fixture", 25, 100) { WorkFraction = nonFinite }.Fraction == 0.25 &&
            new ProvisionProgress("Packages", "fixture") { WorkFraction = nonFinite }.Fraction is null,
            "Nonfinite work fractions fall back to real byte progress or indeterminate state");
    Check(new ProvisionProgress("Downloading", "fixture", -1, 100).Fraction == 0 &&
        new ProvisionProgress("Downloading", "fixture", 200, 100).Fraction == 1 &&
        new ProvisionProgress("Downloading", "fixture", 0, 0).Fraction is null,
        "Download byte fractions retain clamping and unknown-total semantics");
    bool refused = false;
    try { await provisioner.DownloadModelAsync("qwen3-8b-q4_k_m", false); } catch (InvalidOperationException) { refused = true; }
    Check(refused && !Directory.Exists(provisioner.ModelsRoot), "Large model download is refused without consent");
    refused = false;
    try { await provisioner.InstallRuntimeAsync("llama-vulkan", false); } catch (InvalidOperationException) { refused = true; }
    Check(refused, "Runtime installation is refused without consent");
    refused = false;
    try { LocalTalkProvisioner.BuildWslFeatureInstallInfo(false); } catch (InvalidOperationException) { refused = true; }
    Check(refused, "Windows feature installation is refused without consent");
    Check(LocalTalkProvisioner.WindowsPathToWsl(@"C:\Models With Spaces\model.gguf") == "/mnt/c/Models With Spaces/model.gguf", "WSL model mapping preserves spaces without shell interpolation");
    string bootstrapJob = "0123456789abcdef0123456789abcdef";
    string bootstrapScript = "/mnt/c/Program Files/SayWhat's tools/bootstrap-voxtral-wsl.sh";
    var bootstrapInfo = LocalTalkProvisioner.BuildWslSpeechBootstrapInfo("SayWhat-Ubuntu", "cuda", bootstrapScript, bootstrapJob);
    Check(bootstrapInfo.ArgumentList.SequenceEqual(new[]
        { "--distribution", "SayWhat-Ubuntu", "--user", "root", "--exec", "/usr/bin/setsid", "--wait", "/bin/bash", bootstrapScript, "cuda", bootstrapJob }),
        "WSL bootstrap runs root setsid --wait before bash so the launcher waits for the build child and preserves its exit status");
    Check(bootstrapInfo.ArgumentList.Contains(bootstrapScript) && string.IsNullOrEmpty(bootstrapInfo.Arguments),
        "Spaced and apostrophe-containing bootstrap paths stay one ArgumentList entry without shell interpolation");
    Check(Path.GetFileName(bootstrapInfo.FileName).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) &&
        !bootstrapInfo.UseShellExecute && bootstrapInfo.CreateNoWindow && bootstrapInfo.RedirectStandardOutput && bootstrapInfo.RedirectStandardError,
        "Bootstrap metadata retains hidden supervised WSL launch and both diagnostic pipes without starting a process");
    var vulkanBootstrapInfo = LocalTalkProvisioner.BuildWslSpeechBootstrapInfo("SayWhat-Ubuntu-owned", "vulkan", bootstrapScript, bootstrapJob);
    Check(vulkanBootstrapInfo.ArgumentList[^2] == "vulkan" && vulkanBootstrapInfo.ArgumentList[^1] == bootstrapJob,
        "Bootstrap passes the selected backend and unique cancellation owner unchanged");
    Check(ThrowsArgument(() => LocalTalkProvisioner.BuildWslSpeechBootstrapInfo("Ubuntu; injected", "cuda", bootstrapScript, bootstrapJob)),
        "Bootstrap rejects malformed distribution names before launch");
    Check(ThrowsArgument(() => LocalTalkProvisioner.BuildWslSpeechBootstrapInfo("SayWhat-Ubuntu", "unknown", bootstrapScript, bootstrapJob)),
        "Bootstrap rejects unknown backends before launch");
    Check(ThrowsArgument(() => LocalTalkProvisioner.BuildWslSpeechBootstrapInfo("SayWhat-Ubuntu", "cuda", "relative/script.sh", bootstrapJob)) &&
        ThrowsArgument(() => LocalTalkProvisioner.BuildWslSpeechBootstrapInfo("SayWhat-Ubuntu", "cuda", "/mnt/c/../script.sh", bootstrapJob)),
        "Bootstrap rejects relative and parent-traversing Linux scripts before launch");
    Check(ThrowsArgument(() => LocalTalkProvisioner.BuildWslSpeechBootstrapInfo("SayWhat-Ubuntu", "cuda", bootstrapScript, "not-a-job")),
        "Bootstrap rejects invalid cancellation owner tokens before launch");
    Check(!Directory.Exists(provisioner.ModelsRoot) && provisioner.SpeechRegistration is null,
        "Bootstrap start-info checks do not create assets or register a speech runtime");

    byte[] data = FakeDownloadHandler.Data;
    string sha = Convert.ToHexString(SHA256.HashData(data));
    string downloaded = Path.Combine(testRoot, "verified.bin");
    await provisioner.DownloadVerifiedAsync(new("verified.bin", data.Length, sha, "https://example.test/asset"), downloaded, null, CancellationToken.None);
    Check(File.ReadAllBytes(downloaded).SequenceEqual(data), "A mock download is atomically committed only after hash verification");
    bool corrupt = false;
    string bad = Path.Combine(testRoot, "bad.bin");
    try { await provisioner.DownloadVerifiedAsync(new("bad.bin", data.Length, new string('0', 64), "https://example.test/asset"), bad, null, CancellationToken.None); }
    catch (InvalidDataException) { corrupt = true; }
    Check(corrupt && !File.Exists(bad) && !Directory.EnumerateFiles(testRoot, "*.part-*").Any(), "Hash failure removes only its own partial and installs nothing");
    using var downloadCancellation = new CancellationTokenSource();
    var cancelProgress = new InlineProgress(progress => { if (progress.BytesReceived > 0) downloadCancellation.Cancel(); });
    string cancelled = Path.Combine(testRoot, "cancelled.bin");
    bool wasCancelled = false;
    try { await provisioner.DownloadVerifiedAsync(new("cancelled.bin", data.Length, sha, "https://example.test/asset"), cancelled, cancelProgress, downloadCancellation.Token); }
    catch (OperationCanceledException) { wasCancelled = true; }
    Check(wasCancelled && !File.Exists(cancelled) && !Directory.EnumerateFiles(testRoot, "*.part-*").Any(), "Cancellation during download removes only the owned partial and never commits it");
    bool preserved = false;
    try { await provisioner.DownloadVerifiedAsync(new("verified.bin", data.Length + 1, sha, "https://example.test/asset"), downloaded, null, CancellationToken.None); }
    catch (InvalidDataException) { preserved = true; }
    Check(preserved && File.ReadAllBytes(downloaded).SequenceEqual(data), "A mismatched existing file is kept unchanged");
    string zipPath = Path.Combine(testRoot, "unsafe.zip");
    using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    using (StreamWriter writer = new(zip.CreateEntry("../escape.exe").Open())) writer.Write("not executable");
    string extraction = Path.Combine(testRoot, "extract");
    Directory.CreateDirectory(extraction);
    bool unsafeRejected = false;
    try { LocalTalkProvisioner.ExtractVerifiedZip(zipPath, extraction, CancellationToken.None); } catch (InvalidDataException) { unsafeRejected = true; }
    Check(unsafeRejected && !File.Exists(Path.Combine(testRoot, "escape.exe")), "ZIP traversal is rejected before extraction escapes the staging directory");
    Check(!LocalTalkProvisioner.IsWithin(Path.GetPathRoot(testRoot)!, testRoot), "Cleanup containment excludes drive roots");
    var fakeHeader = GgufHeader("qwen3");
    string imported = Path.Combine(testRoot, "existing-qwen.gguf");
    await File.WriteAllBytesAsync(imported, fakeHeader);
    await provisioner.RegisterExistingModelAsync("qwen3-4b-q4_k_m", imported);
    Check(provisioner.ResolveModelPath("qwen3-4b-q4_k_m") == imported && !Directory.Exists(provisioner.ModelsRoot), "An existing GGUF is referenced, not copied or loaded");
    var dedicatedHeader = GgufHeader("hunyuan-dense");
    string dedicated = Path.Combine(testRoot, "existing-hy-mt.gguf");
    await File.WriteAllBytesAsync(dedicated, dedicatedHeader);
    await provisioner.RegisterExistingModelAsync("hy-mt2-1.8b-q4_k_m", dedicated);
    Check(provisioner.ResolveModelPath("hy-mt2-1.8b-q4_k_m") == dedicated, "Dedicated translation imports accept only their declared architecture");
    bool wrongArchitecture = false;
    try { await provisioner.RegisterExistingModelAsync("qwen3-8b-q4_k_m", dedicated); } catch (InvalidDataException) { wrongArchitecture = true; }
    Check(wrongArchitecture, "Import validates structured GGUF architecture, not a model-name substring");
    string packagingRoot = args.Length > 0 ? Path.GetFullPath(Path.Combine(args[0], ".."))
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    string stopScript = File.ReadAllText(Path.Combine(packagingRoot, "scripts", "stop-voxtral.sh"));
    Check(stopScript.Contains("$port-$owner.pid", StringComparison.Ordinal) && stopScript.Contains("/proc/$pid/exe", StringComparison.Ordinal) && stopScript.Contains("$birth", StringComparison.Ordinal), "WSL stop script requires per-instance owner token, exact executable and PID birth checks");
    string startScript = File.ReadAllText(Path.Combine(packagingRoot, "scripts", "run-voxtral.sh"));
    Check(startScript.IndexOf("trap cleanup EXIT", StringComparison.Ordinal) < startScript.IndexOf("\"$server\" --model", StringComparison.Ordinal)
        && startScript.Contains("attempt<50", StringComparison.Ordinal), "Speech child cleanup is installed before launch and has a bounded shutdown");
    string bootstrapScriptText = File.ReadAllText(Path.Combine(packagingRoot, "scripts", "bootstrap-voxtral-wsl.sh"));
    const string aptProgressDefinition = "apt_progress=(-q -o APT::Status-Fd=1 -o Dpkg::Use-Pty=0)";
    Check(bootstrapScriptText.Contains(aptProgressDefinition, StringComparison.Ordinal) &&
        bootstrapScriptText.IndexOf("export LC_ALL=C", StringComparison.Ordinal) >= 0 &&
        bootstrapScriptText.IndexOf("export LC_ALL=C", StringComparison.Ordinal) < bootstrapScriptText.IndexOf(aptProgressDefinition, StringComparison.Ordinal),
        "Future bootstrap declares newline APT status, no dpkg pseudo-terminal, and invariant locale before package setup");
    string[] aptCommands = System.Text.RegularExpressions.Regex.Matches(bootstrapScriptText, @"(?m)^\s*apt-get[^\r\n]*$")
        .Select(match => match.Value.Trim()).ToArray();
    Check(aptCommands.Length == 5 && aptCommands.All(line => line.StartsWith("apt-get \"${apt_progress[@]}\" ", StringComparison.Ordinal)),
        "Every bootstrap apt-get call shares the machine-readable progress flags, including CUDA and Vulkan branches");
    string[] buildCommands = System.Text.RegularExpressions.Regex.Matches(bootstrapScriptText, @"(?m)^\s*cmake --build[^\r\n]*$")
        .Select(match => match.Value.Trim()).ToArray();
    Check(buildCommands.SequenceEqual(new[] { "cmake --build \"$root/build-$backend\" --parallel 1" }),
        "Future bootstrap still bounds compilation to one CPU job");
    const string helpCommand = "\"$root/build-$backend/voxtral-server\" --help";
    const string completionCommand = "echo \"LOCALTALK_VOXTRAL_SERVER=$root/build-$backend/voxtral-server\"";
    int helpAt = bootstrapScriptText.IndexOf(helpCommand, StringComparison.Ordinal);
    int completionAt = bootstrapScriptText.IndexOf(completionCommand, StringComparison.Ordinal);
    Check(bootstrapScriptText.Contains("set -euo pipefail", StringComparison.Ordinal) && helpAt >= 0 && completionAt > helpAt &&
        completionAt == bootstrapScriptText.LastIndexOf(completionCommand, StringComparison.Ordinal),
        "The sole bootstrap success marker remains after executable help and is protected by fail-fast shell behavior");
    string[] speechCommands = System.Text.RegularExpressions.Regex.Matches(bootstrapScriptText, "(?m)^\\s*\"\\$root/build-\\$backend/voxtral-server\"[^\\r\\n]*$")
        .Select(match => match.Value.Trim()).ToArray();
    Check(speechCommands.SequenceEqual(new[] { helpCommand }) && !bootstrapScriptText.Contains("--model", StringComparison.Ordinal),
        "Bootstrap can inspect executable help only and never passes a model or starts speech inference");
    string runtimeFixture = Path.Combine(testRoot, "runtime-fixture");
    Directory.CreateDirectory(runtimeFixture);
    await File.WriteAllTextAsync(Path.Combine(runtimeFixture, "llama-server.exe"), "offline fixture, never executed");
    ProvisionRuntimeChoice pinnedRuntime = LocalTalkProvisioner.RuntimeCatalog[0];
    await File.WriteAllTextAsync(Path.Combine(runtimeFixture, "localtalk-runtime.json"),
        System.Text.Json.JsonSerializer.Serialize(new { pinnedRuntime.Id, Version = LocalTalkProvisioner.LlamaVersion, Hashes = pinnedRuntime.Assets.Select(item => item.Sha256) }));
    Check(LocalTalkProvisioner.FindPinnedRuntimeExecutable(runtimeFixture, pinnedRuntime.Id) is not null, "Read-only bundled runtime lookup accepts only the exact catalog manifest");
    Check(LocalTalkProvisioner.FindPinnedRuntimeExecutable(runtimeFixture, "llama-cpu") is null, "Mismatched bundled runtime manifests are rejected without starting a binary");
    int helperPid = 0;
    var helperInfo = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine($PID); [Threading.Thread]::Sleep(30000)" }) helperInfo.ArgumentList.Add(argument);
    bool progressFailed = false;
    try
    {
        await LocalTalkProvisioner.RunCommandAsync(helperInfo, new InlineProgress(value =>
        { helperPid = int.Parse(value.Message); throw new IOException("offline progress fixture"); }), CancellationToken.None, TimeSpan.FromSeconds(5));
    }
    catch (IOException exception) when (exception.Message == "offline progress fixture") { progressFailed = true; }
    bool helperStopped;
    try { using var helper = System.Diagnostics.Process.GetProcessById(helperPid); helperStopped = helper.WaitForExit(2000); }
    catch (ArgumentException) { helperStopped = true; }
    Check(progressFailed && helperPid > 0 && helperStopped, "A failing setup progress callback terminates only its retained harmless helper process");
    System.Diagnostics.Process? callbackCancellationHelper = null;
    bool callbackCancellationObserved = false, callbackCancellationHelperStopped = false;
    try
    {
        await LocalTalkProvisioner.RunCommandAsync(HarmlessHelperInfo("[Console]::WriteLine($PID); [Threading.Thread]::Sleep(30000)"),
            new InlineProgress(value =>
            {
                callbackCancellationHelper = System.Diagnostics.Process.GetProcessById(int.Parse(value.Message));
                _ = callbackCancellationHelper.Handle; // Retain the exact harmless process handle, not just a recyclable PID.
                throw new OperationCanceledException("offline progress cancellation fixture");
            }), CancellationToken.None, TimeSpan.FromSeconds(15)).WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (TimeoutException exception) when (exception.Message.StartsWith("Setup timed out.", StringComparison.Ordinal))
    { callbackCancellationObserved = true; }
    finally
    {
        if (callbackCancellationHelper is not null)
        {
            try
            {
                callbackCancellationHelperStopped = callbackCancellationHelper.WaitForExit(1000);
                if (!callbackCancellationHelperStopped)
                {
                    // Failure cleanup is scoped only to our retained dummy helper.
                    callbackCancellationHelper.Kill(entireProcessTree: false);
                    callbackCancellationHelper.WaitForExit(2000);
                }
            }
            finally { callbackCancellationHelper.Dispose(); }
        }
    }
    Check(callbackCancellationObserved, "An independently cancelled progress callback returns within five seconds instead of awaiting the helper timeout");
    Check(callbackCancellationHelperStopped, "Progress callback cancellation terminates its retained harmless helper before returning");
    var commandOutput = new List<string>();
    bool helperFailureReported = false;
    try
    {
        await LocalTalkProvisioner.RunCommandAsync(HarmlessHelperInfo("[Console]::Error.WriteLine('offline build failure detail'); exit 17"),
            new InlineProgress(value => commandOutput.Add(value.Message)), CancellationToken.None, TimeSpan.FromSeconds(10));
    }
    catch (SetupCommandException exception)
    {
        helperFailureReported = exception.ExitCode == 17 && exception.OutputTail.Contains("offline build failure detail", StringComparison.Ordinal) &&
            exception.Message.Contains("17", StringComparison.Ordinal) && exception.Message.Contains("offline build failure detail", StringComparison.Ordinal) &&
            exception.Command.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase);
    }
    Check(helperFailureReported && commandOutput.Contains("offline build failure detail"),
        "A failed setup helper preserves its nonzero exit code and useful stderr in both error feedback and progress");
    bool quietFailureReported = false;
    try
    {
        await LocalTalkProvisioner.RunCommandAsync(HarmlessHelperInfo("exit 23"), null,
            CancellationToken.None, TimeSpan.FromSeconds(10));
    }
    catch (SetupCommandException exception)
    {
        quietFailureReported = exception.ExitCode == 23 && exception.Message.Contains("23", StringComparison.Ordinal) &&
            exception.Message.Contains("Setup", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(exception.Command);
    }
    Check(quietFailureReported, "A nonzero helper exit without output still produces a meaningful setup error");
    var successProgress = new List<string>();
    await LocalTalkProvisioner.RunCommandAsync(HarmlessHelperInfo("[Threading.Thread]::Sleep(150); [Console]::WriteLine('offline finished'); exit 0"),
        new InlineProgress(value => successProgress.Add(value.Message)), CancellationToken.None, TimeSpan.FromSeconds(10));
    Check(successProgress.SequenceEqual(new[] { "offline finished" }), "Command success waits for the helper to finish and drain its final output");
    string successMarker = "LOCALTALK_VOXTRAL_SERVER=/opt/localtalk-voxtral/" + LocalTalkProvisioner.SpeechBuildIdentity + "/build-cuda/voxtral-server";
    var markerProgress = new List<string>();
    await LocalTalkProvisioner.RunCommandAsync(HarmlessHelperInfo("[Console]::WriteLine('" + successMarker + "'); 1..25 | ForEach-Object { [Console]::WriteLine('after marker ' + $_) }; exit 0"),
        new InlineProgress(value => markerProgress.Add(value.Message)), CancellationToken.None, TimeSpan.FromSeconds(10), requiredOutputLine: successMarker);
    Check(markerProgress.Count == 26 && markerProgress[0] == successMarker && markerProgress[^1] == "after marker 25",
        "The exact bootstrap success marker is remembered even when later output pushes it beyond the twenty-line diagnostic tail");
    foreach (var markerCase in new[]
    {
        (Command: "exit 0", Description: "A zero helper exit without a completion marker is not treated as completed speech setup"),
        (Command: "[Console]::WriteLine('" + successMarker.Replace("build-cuda", "build-vulkan", StringComparison.Ordinal) + "'); exit 0",
            Description: "A completion marker for a different backend is refused"),
        (Command: "[Console]::WriteLine('" + successMarker.Replace(LocalTalkProvisioner.SpeechBuildIdentity, "other-source-revision", StringComparison.Ordinal) + "'); exit 0",
            Description: "A completion marker for a different pinned speech build is refused"),
        (Command: "[Console]::WriteLine('prefix " + successMarker + "'); exit 0", Description: "A marker merely embedded in another output line is refused"),
        (Command: "[Console]::Error.WriteLine('" + successMarker + "'); exit 0", Description: "A completion marker on stderr cannot claim successful speech setup")
    })
    {
        bool rejected = false;
        try
        {
            await LocalTalkProvisioner.RunCommandAsync(HarmlessHelperInfo(markerCase.Command), null,
                CancellationToken.None, TimeSpan.FromSeconds(10), requiredOutputLine: successMarker);
        }
        catch (SpeechSetupException exception) { rejected = !string.IsNullOrWhiteSpace(exception.Message); }
        Check(rejected, markerCase.Description);
    }
    bool markerCannotOverrideFailure = false;
    try
    {
        await LocalTalkProvisioner.RunCommandAsync(HarmlessHelperInfo("[Console]::WriteLine('" + successMarker + "'); [Console]::Error.WriteLine('offline post-marker failure'); exit 23"),
            null, CancellationToken.None, TimeSpan.FromSeconds(10), requiredOutputLine: successMarker);
    }
    catch (SetupCommandException exception)
    {
        markerCannotOverrideFailure = exception.ExitCode == 23 && exception.OutputTail.Contains("offline post-marker failure", StringComparison.Ordinal);
    }
    Check(markerCannotOverrideFailure, "An exact success marker cannot override a later nonzero child exit");
    using var beforeStartCancellation = new CancellationTokenSource();
    beforeStartCancellation.Cancel();
    var neverStartedInfo = HarmlessHelperInfo("exit 0");
    neverStartedInfo.FileName = Path.Combine(testRoot, "nonexistent-never-start.exe");
    bool cancelledBeforeStart = false;
    try { await LocalTalkProvisioner.RunCommandAsync(neverStartedInfo, null, beforeStartCancellation.Token, TimeSpan.FromSeconds(10)); }
    catch (OperationCanceledException) { cancelledBeforeStart = true; }
    Check(cancelledBeforeStart, "An already-cancelled command is rejected before attempting to start even a missing helper executable");
    Console.WriteLine($"PASS: {checks.Count} offline provisioning checks. No network, audio, WSL, or engine was used; harmless CPU helpers validated wait/error/cleanup.");
    foreach (string check in checks) Console.WriteLine("  " + check);
}
catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 1; }

static byte[] GgufHeader(string architecture)
{
    using var memory = new MemoryStream();
    using var writer = new BinaryWriter(memory, Encoding.UTF8, true);
    writer.Write(0x46554747u); writer.Write(3u); writer.Write(1ul); writer.Write(1ul);
    byte[] key = Encoding.UTF8.GetBytes("general.architecture");
    writer.Write((ulong)key.Length); writer.Write(key); writer.Write(8u);
    byte[] value = Encoding.UTF8.GetBytes(architecture);
    writer.Write((ulong)value.Length); writer.Write(value);
    return memory.ToArray();
}

static System.Diagnostics.ProcessStartInfo HarmlessHelperInfo(string command)
{
    var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) info.ArgumentList.Add(argument);
    return info;
}

static bool ThrowsArgument(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
}

internal sealed class FakeDownloadHandler : HttpMessageHandler
{
    internal static readonly byte[] Data = Encoding.UTF8.GetBytes("LocalTalk offline verified-download fixture");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) });
}

internal sealed class InlineProgress(Action<ProvisionProgress> report) : IProgress<ProvisionProgress>
{
    public void Report(ProvisionProgress value) => report(value);
}
internal static class OfflineErrorMode
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
    internal static void SuppressDialogs() { if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002); }
}

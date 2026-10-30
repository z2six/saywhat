using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IncomingTranslator;

// Synthetic archives, receipts and registry records only. These checks never
// launch WSL, inspect the registry, download an image or touch an installed app.
internal static class ManagedWslSetupTests
{
    internal static async Task<int> RunAsync(CancellationToken token = default)
    {
        const long GiB = 1024L * 1024 * 1024;
        string root = Path.Combine(Path.GetTempPath(), "SayWhat-managed-wsl-tests-" + Guid.NewGuid().ToString("N"));
        int checks = 0;
        void Check(bool value, string message)
        {
            token.ThrowIfCancellationRequested();
            if (!value) throw new InvalidOperationException("Managed WSL setup: " + message);
            checks++;
        }
        async Task Reject<T>(Func<Task> action, string message) where T : Exception
        {
            bool rejected = false;
            try { await action(); } catch (T) { rejected = true; }
            Check(rejected, message);
        }
        string Data(string name) => Path.Combine(root, name);
        string ReceiptPath(string data, string name) => Directory.EnumerateFiles(Path.Combine(data, "managed-wsl"), "setup.json", SearchOption.AllDirectories)
            .Single(path => JsonNode.Parse(File.ReadAllText(path))!["Name"]!.GetValue<string>() == name);
        JsonNode ReadReceipt(string data, string name) => JsonNode.Parse(File.ReadAllText(ReceiptPath(data, name)))!;

        string freshData = Data("spaces and 空间");
        using var freshProvisioner = new LocalTalkProvisioner(freshData);
        var fresh = new FakePlatform(freshData);
        var setup = new ManagedWslSetup(freshProvisioner, fresh);
        Check(!Directory.Exists(root) && fresh.Events.Count == 0, "construction performs no platform or filesystem work");
        using (var earlyCancellation = new CancellationTokenSource())
        {
            earlyCancellation.Cancel();
            await Reject<OperationCanceledException>(() => setup.EnsureAsync(AppBrand.SpeechDistributionName, null, earlyCancellation.Token),
                "already cancelled setup does not begin work");
            Check(!Directory.Exists(root) && fresh.Events.Count == 0, "early cancellation creates no receipt, image or command");
        }
        string actual = await setup.EnsureAsync(AppBrand.SpeechDistributionName, null, token);
        Check(actual == AppBrand.SpeechDistributionName && fresh.Events.SequenceEqual(new[] { "Download", "Prepare", "Import", "OwnerCheck" }),
            "the headless image is prepared and imported before the first Linux ownership command");
        Check(fresh.ImportReceiptValid && fresh.OwnerReceiptValid && fresh.Imports.Single().Storage.Contains("spaces and 空间", StringComparison.Ordinal),
            "pre-import intent and post-import GUID receipts exist before their corresponding commands, including spaced Unicode storage paths");
        Check(fresh.Commands.All(command => !command.UseShellExecute && command.CreateNoWindow &&
            command.WindowStyle == ProcessWindowStyle.Hidden && command.RedirectStandardOutput && command.RedirectStandardError &&
            command.Environment["WSL_UTF8"] == "1" && command.StandardOutputEncoding == System.Text.Encoding.UTF8 &&
            command.StandardErrorEncoding == System.Text.Encoding.UTF8),
            "every WSL command is hidden, supervised and explicitly uses UTF-8 for both diagnostic streams");
        JsonNode ready = ReadReceipt(freshData, actual);
        Check(ready["Ready"]!.GetValue<bool>() && ready["RegistryId"]!.GetValue<Guid>() == fresh.Distributions.Single().Id &&
            ready["HeadlessRevision"]!.GetValue<int>() == ManagedWslImage.HeadlessRevision && ready["ImportCompleted"]!.GetValue<bool>(),
            "the completed receipt binds successful import, exact registered identity and headless image revision");
        Check(!File.Exists(fresh.Imports.Single().Tar) && Directory.Exists(fresh.Imports.Single().Storage),
            "successful import removes only its verified prepared tar and preserves the owned storage directory");
        int priorImports = fresh.Imports.Count;
        fresh.Events.Clear();
        Check(await new ManagedWslSetup(freshProvisioner, fresh).EnsureAsync(actual, null, token) == actual &&
            fresh.Imports.Count == priorImports && fresh.Events.SequenceEqual(new[] { "OwnerCheck" }),
            "a matching receipt resumes with an ownership check and no image download, preparation or import");

        string owner = ready["Owner"]!.GetValue<string>();
        ProcessStartInfo ownerInfo = ManagedWslSetup.BuildOwnerCheckInfo(actual, owner);
        Check(ownerInfo.ArgumentList.Take(7).SequenceEqual(new[] { "--distribution", actual, "--user", "root", "--exec", "/bin/sh", "-c" }) &&
            ownerInfo.ArgumentList[^2] == owner && ownerInfo.ArgumentList[^1] == ManagedWslImage.OwnerMarkerPath &&
            ownerInfo.ArgumentList[7].Contains("cat -- \"$2\"; printf .", StringComparison.Ordinal) &&
            ownerInfo.ArgumentList[7].Contains("/usr/bin/stat -c %s -- \"$2\"", StringComparison.Ordinal) &&
            ownerInfo.ArgumentList[7].Contains("printf '%s\\n.' \"$1\"", StringComparison.Ordinal),
            "the first command compares the entire marker including its final LF and passes the owner separately from shell code");
        var nonCImport = ManagedWslSetup.BuildImportInfo(actual, @"D:\Speech tools\语音\distro", @"E:\Prepared images\speech's image.tar");
        Check(nonCImport.ArgumentList.SequenceEqual(new[] { "--import", actual, @"D:\Speech tools\语音\distro",
            @"E:\Prepared images\speech's image.tar", "--version", "2" }) && string.IsNullOrEmpty(nonCImport.Arguments),
            "non-C drives, Unicode, spaces and apostrophes remain exact individual import arguments");
        await Reject<ArgumentException>(() =>
        { ManagedWslSetup.BuildOwnerCheckInfo(actual, owner + "\n"); return Task.CompletedTask; },
            "an owner token with a trailing newline is rejected before command construction");

        string collisionData = Data("legacy-collision");
        using var collisionProvisioner = new LocalTalkProvisioner(collisionData);
        var collision = new FakePlatform(collisionData);
        ManagedWslDistribution legacy = new(Guid.NewGuid(), AppBrand.SpeechDistributionName, Data("foreign-existing-distro"), 2);
        ManagedWslDistribution userDistro = new(Guid.NewGuid(), "Ubuntu", Data("user-ubuntu"), 2);
        collision.Distributions.AddRange([legacy, userDistro]);
        string replacementName = await new ManagedWslSetup(collisionProvisioner, collision).EnsureAsync(legacy.Name, null, token);
        Check(replacementName != legacy.Name && AppBrand.IsOwnedSpeechDistributionName(replacementName) &&
            collision.Distributions.Contains(legacy) && collision.Distributions.Contains(userDistro) &&
            collision.OwnerChecks.All(name => name == replacementName),
            "a legacy checkpoint name without a provenance receipt gets a fresh headless component without launching or modifying either foreign distro");

        foreach (string changed in new[] { "guid", "path", "version", "missing-guid", "headless-revision", "oversized-receipt" })
        {
            string data = Data("replacement-" + changed);
            using var provisioner = new LocalTalkProvisioner(data);
            var platform = new FakePlatform(data);
            var component = new ManagedWslSetup(provisioner, platform);
            string original = await component.EnsureAsync(AppBrand.SpeechDistributionName, null, token);
            ManagedWslDistribution registered = platform.Distributions.Single();
            platform.Distributions[0] = changed switch
            {
                "guid" => registered with { Id = Guid.NewGuid() },
                "path" => registered with { BasePath = Data("foreign-storage") },
                "version" => registered with { Version = 1 },
                _ => registered
            };
            if (changed is "missing-guid" or "headless-revision")
            {
                string path = ReceiptPath(data, original);
                JsonNode receipt = JsonNode.Parse(File.ReadAllText(path))!;
                if (changed == "missing-guid") receipt["RegistryId"] = null;
                else receipt["HeadlessRevision"] = ManagedWslImage.HeadlessRevision - 1;
                File.WriteAllText(path, receipt.ToJsonString());
            }
            else if (changed == "oversized-receipt")
                File.WriteAllText(ReceiptPath(data, original), new string('x', 64 * 1024 + 1));
            ManagedWslDistribution foreign = platform.Distributions[0];
            platform.OwnerChecks.Clear();
            string next = await component.EnsureAsync(original, null, token);
            Check(next != original && platform.Distributions.Contains(foreign) && platform.OwnerChecks.SequenceEqual(new[] { next }) &&
                Directory.Exists(registered.BasePath),
                "a replaced component or uncaptured " + changed + " never passes name-only ownership or loses its existing storage");
        }

        string wrongOwnerData = Data("wrong-owner");
        using var wrongOwnerProvisioner = new LocalTalkProvisioner(wrongOwnerData);
        var wrongOwner = new FakePlatform(wrongOwnerData);
        var wrongOwnerSetup = new ManagedWslSetup(wrongOwnerProvisioner, wrongOwner);
        string wrongOwnerName = await wrongOwnerSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, token);
        ManagedWslDistribution wrongOwnerRegistration = wrongOwner.Distributions.Single();
        wrongOwner.Markers[wrongOwnerName] += "extra line\n";
        string newOwnerName = await wrongOwnerSetup.EnsureAsync(wrongOwnerName, null, token);
        Check(newOwnerName != wrongOwnerName && wrongOwner.Distributions.Contains(wrongOwnerRegistration) &&
            ReadReceipt(wrongOwnerData, wrongOwnerName)["Retired"]!.GetValue<bool>(),
            "an exact registry match with the wrong complete owner marker retires its receipt and leaves the old component intact");

        string interruptedData = Data("interrupted-registered");
        using var interruptedProvisioner = new LocalTalkProvisioner(interruptedData);
        var interrupted = new FakePlatform(interruptedData) { FailAfterRegistrationOnce = true };
        interrupted.Distributions.Add(legacy);
        var interruptedSetup = new ManagedWslSetup(interruptedProvisioner, interrupted);
        await Reject<IOException>(() => interruptedSetup.EnsureAsync(legacy.Name, null, token),
            "an import error is returned without pretending that setup completed");
        string interruptedName = interrupted.Imports.Single().Name;
        JsonNode interruptedReceipt = ReadReceipt(interruptedData, interruptedName);
        Check(interruptedReceipt["RegistryId"] is not null && !interruptedReceipt["Ready"]!.GetValue<bool>() &&
            !interruptedReceipt["ImportCompleted"]!.GetValue<bool>() &&
            File.Exists(interrupted.Imports.Single().Tar) && interrupted.OwnerChecks.Count == 0,
            "an interrupted import retains its captured GUID and archive without starting Linux or deleting its partial storage");
        interrupted.Events.Clear();
        string confirmedReplacement = await interruptedSetup.EnsureAsync(legacy.Name, null, token);
        Check(confirmedReplacement != interruptedName && interrupted.Imports.Count == 2 &&
            interrupted.OwnerChecks.SequenceEqual(new[] { confirmedReplacement }) &&
            Directory.Exists(interrupted.Imports[0].Storage) && File.Exists(interrupted.Imports[0].Tar),
            "retry imports a fresh headless component without ever booting an unconfirmed registered partial import");

        string partialData = Data("interrupted-unregistered");
        using var partialProvisioner = new LocalTalkProvisioner(partialData);
        var partial = new FakePlatform(partialData) { FailBeforeRegistrationOnce = true };
        var partialSetup = new ManagedWslSetup(partialProvisioner, partial);
        await Reject<IOException>(() => partialSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, token),
            "an unregistered partial import remains an error");
        var partialImport = partial.Imports.Single();
        string afterPartial = await partialSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, token);
        Check(afterPartial != partialImport.Name && Directory.Exists(partialImport.Storage) && File.Exists(partialImport.Tar) &&
            partial.Imports[1].Storage != partialImport.Storage,
            "retry uses new storage and name instead of overwriting or deleting an unregistered partial import");

        string preparationData = Data("interrupted-preparation");
        using var preparationProvisioner = new LocalTalkProvisioner(preparationData);
        var preparation = new FakePlatform(preparationData) { FailPreparationOnce = true };
        var preparationSetup = new ManagedWslSetup(preparationProvisioner, preparation);
        await Reject<IOException>(() => preparationSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, token),
            "an interrupted image preparation never starts import");
        string unknownTar = preparation.PreparedTars.Single();
        string unknownBytes = File.ReadAllText(unknownTar);
        string preparedName = await preparationSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, token);
        Check(preparedName == AppBrand.SpeechDistributionName && preparation.PreparedTars.Distinct().Count() == 2 &&
            File.ReadAllText(unknownTar) == unknownBytes,
            "retry prepares into a fresh archive path and preserves an unverified interrupted archive unchanged");

        string cancelledData = Data("cancelled-import");
        using var cancelledProvisioner = new LocalTalkProvisioner(cancelledData);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelled = new FakePlatform(cancelledData) { AfterRegistration = cancellation.Cancel };
        var cancelledSetup = new ManagedWslSetup(cancelledProvisioner, cancelled);
        await Reject<OperationCanceledException>(() => cancelledSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, cancellation.Token),
            "caller cancellation after import registration remains cancellation");
        cancelled.AfterRegistration = null;
        string cancelledName = cancelled.Imports.Single().Name;
        Check(ReadReceipt(cancelledData, cancelledName)["RegistryId"] is not null && cancelled.OwnerChecks.Count == 0 &&
            !ReadReceipt(cancelledData, cancelledName)["ImportCompleted"]!.GetValue<bool>(),
            "cancelled unconfirmed import keeps its provenance and does not run a Linux command");
        string cancelledReplacement = await cancelledSetup.EnsureAsync(cancelledName, null, token);
        Check(cancelledReplacement != cancelledName && cancelled.Imports.Count == 2 &&
            cancelled.OwnerChecks.SequenceEqual(new[] { cancelledReplacement }),
            "retry never boots an import whose command was cancelled before completion was confirmed");

        string completedCancellationData = Data("cancelled-after-confirmed-import");
        using var completedCancellationProvisioner = new LocalTalkProvisioner(completedCancellationData);
        using var afterCompletionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completedCancellation = new FakePlatform(completedCancellationData)
            { AfterSuccessfulImport = afterCompletionCancellation.Cancel };
        completedCancellation.Distributions.Add(legacy);
        var completedCancellationSetup = new ManagedWslSetup(completedCancellationProvisioner, completedCancellation);
        await Reject<OperationCanceledException>(() => completedCancellationSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, afterCompletionCancellation.Token),
            "cancellation after a successful import still prevents its first Linux command");
        completedCancellation.AfterSuccessfulImport = null;
        string completedName = completedCancellation.Imports.Single().Name;
        Check(ReadReceipt(completedCancellationData, completedName)["ImportCompleted"]!.GetValue<bool>() &&
            completedCancellation.OwnerChecks.Count == 0 && await completedCancellationSetup.EnsureAsync(legacy.Name, null, token) == completedName &&
            completedCancellation.Imports.Count == 1,
            "successful import and GUID are checkpointed without caller cancellation so a confirmed component can resume safely");

        string lowData = Data("low-space");
        using var lowProvisioner = new LocalTalkProvisioner(lowData);
        var low = new FakePlatform(lowData) { FreeBytes = 6 * GiB - 1 };
        var lowSetup = new ManagedWslSetup(lowProvisioner, low);
        await Reject<SetupUserException>(() => lowSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, token),
            "less than six GiB on the backing Windows volume rejects image preparation");
        Check(low.Events.Count == 0 && low.Imports.Count == 0, "low disk space starts no image download, preparation, import or Linux command");
        low.FreeBytes = 6 * GiB;
        low.AfterDownload = () => low.FreeBytes = 6 * GiB - 1;
        await Reject<SetupUserException>(() => lowSetup.EnsureAsync(AppBrand.SpeechDistributionName, null, token),
            "free host space is checked again after download and before preparing the larger tar");
        Check(low.Events.SequenceEqual(new[] { "Download" }), "space consumed by image download prevents preparation and import");

        int commandsBeforeCapacity = fresh.Commands.Count;
        foreach ((string backend, long required) in new[] { ("cuda", 20 * GiB), ("vulkan", 8 * GiB), ("cpu", 8 * GiB) })
        {
            fresh.FreeBytes = required - 1;
            await Reject<SetupUserException>(() => { setup.ValidateBuildCapacity(actual, backend); return Task.CompletedTask; },
                "a nearly full Windows backing volume blocks the " + backend + " build before apt can start");
            fresh.FreeBytes = required;
            setup.ValidateBuildCapacity(actual, backend);
            Check(fresh.SpaceChecks[^1] == fresh.Distributions.Single().BasePath && fresh.Commands.Count == commandsBeforeCapacity,
                "the " + backend + " threshold is checked on the registered VHD host path without launching Linux");
        }
        fresh.FreeBytes = 64 * GiB;
        fresh.Distributions[0] = fresh.Distributions[0] with { Id = Guid.NewGuid() };
        await Reject<IOException>(() => { setup.ValidateBuildCapacity(actual, "cuda"); return Task.CompletedTask; },
            "build capacity validation rejects a replaced registry identity even when free space is ample");

        string alteredData = Data("altered-prepared-tar");
        using var alteredProvisioner = new LocalTalkProvisioner(alteredData);
        var altered = new FakePlatform(alteredData);
        altered.AfterRegistration = () => File.AppendAllText(altered.Imports.Single().Tar, "unknown modification");
        await new ManagedWslSetup(alteredProvisioner, altered).EnsureAsync(AppBrand.SpeechDistributionName, null, token);
        Check(File.Exists(altered.Imports.Single().Tar), "cleanup preserves a prepared archive whose bytes changed after import");

        string boundedData = Data("bounded-receipt-scan");
        using var boundedProvisioner = new LocalTalkProvisioner(boundedData);
        var bounded = new FakePlatform(boundedData);
        for (int index = 0; index < 256; index++)
            Directory.CreateDirectory(Path.Combine(boundedData, "managed-wsl", index.ToString("x32")));
        await Reject<IOException>(() => new ManagedWslSetup(boundedProvisioner, bounded).EnsureAsync(AppBrand.SpeechDistributionName, null, token),
            "receipt inspection stops at its 256-directory bound instead of scanning an unbounded local folder");
        Check(bounded.Events.Count == 0 && bounded.Commands.Count == 0, "an overfull receipt directory starts no downloads or Windows/Linux commands");

        Check(new[] { fresh, collision, interrupted, partial, preparation, cancelled, completedCancellation, low, altered, wrongOwner }
            .SelectMany(platform => platform.Commands).All(command => !command.ArgumentList.Any(argument =>
                argument is "--unregister" or "--terminate" or "--shutdown" or "--set-default" or "--set-version" or "--install")),
            "the coordinator never unregisters, terminates, converts or changes default/global WSL state");
        Check(!Directory.EnumerateFiles(root, "*.new-*", SearchOption.AllDirectories).Any(), "receipts leave no temporary checkpoint files after success, failure or cancellation");
        return checks;
    }

    private sealed class FakePlatform(string dataRoot) : IManagedWslSetupPlatform
    {
        internal readonly List<string> Events = [];
        internal readonly List<ProcessStartInfo> Commands = [];
        internal readonly List<ManagedWslDistribution> Distributions = [];
        internal readonly List<(string Name, string Storage, string Tar)> Imports = [];
        internal readonly List<string> PreparedTars = [], OwnerChecks = [], SpaceChecks = [];
        internal readonly Dictionary<string, string> Markers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _preparedOwners = new(StringComparer.OrdinalIgnoreCase);
        private bool _awaitingSuccessfulImportRead;
        internal long FreeBytes = 64L * 1024 * 1024 * 1024;
        internal bool FailAfterRegistrationOnce, FailBeforeRegistrationOnce, FailPreparationOnce;
        internal bool ImportReceiptValid, OwnerReceiptValid;
        internal Action? AfterRegistration, AfterDownload, AfterSuccessfulImport;
        public IReadOnlyList<ManagedWslDistribution> ReadDistributions()
        {
            if (_awaitingSuccessfulImportRead) { _awaitingSuccessfulImportRead = false; AfterSuccessfulImport?.Invoke(); }
            return Distributions.ToArray();
        }
        public long AvailableFreeSpace(string path) { SpaceChecks.Add(path); return FreeBytes; }
        public Task DownloadImageAsync(string destination, IProgress<ProvisionProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Events.Add("Download");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.WriteAllText(destination, "synthetic image fixture; never a real rootfs");
            AfterDownload?.Invoke();
            return Task.CompletedTask;
        }
        public Task PrepareImageAsync(string inputGzip, string outputTar, string owner, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Events.Add("Prepare");
            if (!File.Exists(inputGzip) || File.Exists(outputTar)) throw new IOException("Invalid synthetic preparation paths.");
            PreparedTars.Add(outputTar);
            File.WriteAllText(outputTar, "guiApplications=false\nowner=" + owner + "\n");
            _preparedOwners.Add(outputTar, owner);
            if (FailPreparationOnce) { FailPreparationOnce = false; throw new IOException("offline interrupted image preparation"); }
            return Task.CompletedTask;
        }
        public Task RunCommandAsync(ProcessStartInfo command, IProgress<ProvisionProgress>? progress,
            CancellationToken token, TimeSpan timeout, string? requiredOutputLine = null)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add(command);
            string[] arguments = command.ArgumentList.ToArray();
            if (arguments[0] == "--import")
            {
                Events.Add("Import");
                if (arguments.Length != 6 || arguments[4] != "--version" || arguments[5] != "2" ||
                    Distributions.Any(item => item.Name == arguments[1])) throw new IOException("Invalid or colliding synthetic import.");
                (string name, string storage, string tar) = (arguments[1], arguments[2], arguments[3]);
                if (!LocalTalkProvisioner.IsWithin(storage, Path.Combine(dataRoot, "managed-wsl")))
                    throw new IOException("Synthetic import escaped its isolated data root.");
                Imports.Add((name, storage, tar));
                string owner = _preparedOwners[tar];
                JsonNode receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(storage)!, "setup.json")))!;
                ImportReceiptValid = receipt["Owner"]!.GetValue<string>() == owner && receipt["Name"]!.GetValue<string>() == name &&
                    receipt["BasePath"]!.GetValue<string>() == storage && receipt["RegistryId"] is null && receipt["ImportRequested"]!.GetValue<bool>() &&
                    !receipt["ImportCompleted"]!.GetValue<bool>() &&
                    receipt["HeadlessRevision"]!.GetValue<int>() == ManagedWslImage.HeadlessRevision &&
                    File.ReadAllText(tar).StartsWith("guiApplications=false\n", StringComparison.Ordinal);
                if (!ImportReceiptValid) throw new IOException("Import started without its headless provenance receipt.");
                Directory.CreateDirectory(storage);
                File.WriteAllText(Path.Combine(storage, "synthetic-storage.txt"), "preserve this partial or registered storage");
                if (FailBeforeRegistrationOnce) { FailBeforeRegistrationOnce = false; throw new IOException("offline unregistered partial import"); }
                Distributions.Add(new(Guid.NewGuid(), name, storage, 2));
                Markers[name] = owner + "\n";
                AfterRegistration?.Invoke();
                token.ThrowIfCancellationRequested();
                if (FailAfterRegistrationOnce) { FailAfterRegistrationOnce = false; throw new IOException("offline interrupted registered import"); }
                _awaitingSuccessfulImportRead = true;
            }
            else
            {
                Events.Add("OwnerCheck");
                if (arguments.Length != 11 || arguments[0] != "--distribution" || arguments[2] != "--user" || arguments[3] != "root" ||
                    arguments[4] != "--exec" || arguments[5] != "/bin/sh" || arguments[6] != "-c" || arguments[8] != "saywhat-owner-check" ||
                    arguments[10] != ManagedWslImage.OwnerMarkerPath) throw new IOException("The first Linux command must verify the owner only.");
                string name = arguments[1], owner = arguments[9];
                OwnerChecks.Add(name);
                ManagedWslDistribution registered = Distributions.Single(item => item.Name == name);
                JsonNode receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(registered.BasePath)!, "setup.json")))!;
                OwnerReceiptValid = receipt["RegistryId"]!.GetValue<Guid>() == registered.Id && receipt["BasePath"]!.GetValue<string>() == registered.BasePath &&
                    receipt["ImportCompleted"]!.GetValue<bool>() &&
                    requiredOutputLine == "SAYWHAT_MANAGED_DISTRO_OWNER=" + owner;
                if (!OwnerReceiptValid) throw new IOException("Linux started before its exact registry identity was saved.");
                if (!Markers.TryGetValue(name, out string? marker) || marker != owner + "\n")
                    throw new SetupCommandException(42, "offline-owner-check", "");
            }
            return Task.CompletedTask;
        }
    }
}

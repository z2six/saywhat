using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace IncomingTranslator;

internal sealed record ManagedWslDistribution(Guid Id, string Name, string BasePath, int Version);

// The coordinator's offline fixtures replace every operation that can inspect
// Windows state, download an image or start WSL. Receipt I/O stays in isolated data.
internal interface IManagedWslSetupPlatform
{
    IReadOnlyList<ManagedWslDistribution> ReadDistributions();
    long AvailableFreeSpace(string path);
    Task DownloadImageAsync(string destination, IProgress<ProvisionProgress>? progress, CancellationToken token);
    Task PrepareImageAsync(string inputGzip, string outputTar, string owner, CancellationToken token);
    Task RunCommandAsync(ProcessStartInfo command, IProgress<ProvisionProgress>? progress,
        CancellationToken token, TimeSpan timeout, string? requiredOutputLine = null);
}

// Import an already headless, uniquely marked rootfs. No Linux command is sent to
// an existing name until its registry identity and exact owned storage match.
internal sealed class ManagedWslSetup
{
    private const long GiB = 1024L * 1024 * 1024;
    private const int MaximumReceiptBytes = 64 * 1024;
    private const int MaximumComponentDirectories = 256;
    internal const string PreparationCapacityMessage = "Speech setup needs at least 6 GB of free space on its Windows drive. Free some space, then continue setup.";
    internal const string CudaBuildCapacityMessage = "CUDA speech setup needs at least 20 GB of free space on its Windows drive. Free some space, then continue setup.";
    internal const string OtherBuildCapacityMessage = "Speech setup needs at least 8 GB of free space on its Windows drive. Free some space, then continue setup.";
    private readonly LocalTalkProvisioner _provisioner;
    private readonly IManagedWslSetupPlatform _platform;
    private readonly string _root;
    private static readonly SemaphoreSlim Changes = new(1, 1);
    private static readonly Regex OwnerPattern = new(@"\A[a-f0-9]{32}\z");
    private static readonly Regex TarPattern = new(@"\Aprepared-[a-f0-9]{32}\.tar\z");

    public ManagedWslSetup(LocalTalkProvisioner provisioner, IManagedWslSetupPlatform? platform = null)
    {
        _provisioner = provisioner;
        _platform = platform ?? new WindowsManagedWslSetupPlatform(provisioner);
        _root = Path.Combine(Path.GetFullPath(provisioner.DataRoot), "managed-wsl");
    }

    internal async Task<string> EnsureAsync(string name, IProgress<ProvisionProgress>? progress, CancellationToken token)
    {
        if (!IsManagedName(name)) throw new ArgumentException("Choose a valid managed speech component.", nameof(name));
        await Changes.WaitAsync(token);
        try
        {
            EnsurePlainLocalPath(_root);
            // A failed ownership check retires only our receipt. A new component
            // gets another name and storage folder; the old registration is kept.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<ManagedWslDistribution> registrations = _platform.ReadDistributions();
                Receipt? receipt = FindReceipt(name, registrations);
                if (receipt is null) receipt = await CreateReceiptAsync(name, registrations, token);
                ManagedWslDistribution? existing = registrations.FirstOrDefault(item => Matches(receipt, item));
                bool resumingRegistered = existing is not null;
                if (existing is null)
                {
                    RequireFreeSpace(_root, 6 * GiB, PreparationCapacityMessage);
                    string image = Path.Combine(_provisioner.DataRoot, "downloads", ManagedWslImage.UbuntuAsset.FileName);
                    EnsurePlainLocalPath(image);
                    progress?.Report(new("Preparing speech support", "Downloading the verified speech system image."));
                    await _platform.DownloadImageAsync(image, progress, token);
                    RequireFreeSpace(_root, 6 * GiB, PreparationCapacityMessage);
                    string prepared = Path.Combine(ComponentRoot(receipt.Owner), "prepared-" + Guid.NewGuid().ToString("N") + ".tar");
                    EnsurePlainLocalPath(prepared);
                    // Each preparation uses a new path. An interrupted or unknown
                    // existing tar is never overwritten or assumed to be trusted.
                    receipt.PreparedTar = prepared;
                    receipt.PreparedSha256 = null;
                    await SaveReceiptAsync(receipt, token);
                    progress?.Report(new("Preparing speech support", "Preparing speech tools without desktop applications."));
                    await _platform.PrepareImageAsync(image, prepared, receipt.Owner, token);
                    receipt.PreparedSha256 = await DigestAsync(prepared, token);
                    receipt.ImportRequested = true;
                    await SaveReceiptAsync(receipt, token);
                    EnsurePlainLocalPath(receipt.BasePath);
                    if (Directory.Exists(receipt.BasePath) || File.Exists(receipt.BasePath))
                        throw new IOException("The speech storage folder already exists. Continue setup to create a separate component safely.");
                    // Recheck immediately before import: a name collision cannot
                    // turn into an import or command against somebody else's distro.
                    if (_platform.ReadDistributions().Any(item => SameName(item.Name, receipt.Name)))
                    {
                        receipt.Retired = true;
                        await SaveReceiptAsync(receipt, token);
                        continue;
                    }
                    progress?.Report(new("Preparing speech support", "Installing the prepared speech tools."));
                    // Accept both old and new WSL import implementations: provide
                    // a fresh empty, receipt-owned directory instead of relying on
                    // the CLI to create its installation location.
                    Directory.CreateDirectory(receipt.BasePath);
                    EnsurePlainLocalPath(receipt.BasePath);
                    bool importCompleted = false;
                    try
                    {
                        await _platform.RunCommandAsync(BuildImportInfo(receipt.Name, receipt.BasePath, prepared),
                            progress, token, TimeSpan.FromMinutes(40));
                        importCompleted = true;
                    }
                    finally
                    {
                        // Cancellation can arrive after WSL registers the import.
                        // Record its exact identity and whether the full import
                        // succeeded before returning or propagating cancellation.
                        ManagedWslDistribution? imported = _platform.ReadDistributions().SingleOrDefault(item =>
                            SameName(item.Name, receipt.Name) && item.Version == 2 && SamePath(item.BasePath, receipt.BasePath));
                        if (imported is not null && imported.Id != Guid.Empty)
                        {
                            receipt.RegistryId = imported.Id;
                            // A registered but interrupted import may not contain
                            // wsl.conf yet. Never boot it without confirmed import
                            // completion, even when its GUID/storage match.
                            receipt.ImportCompleted = importCompleted;
                            await SaveReceiptAsync(receipt, CancellationToken.None);
                        }
                    }
                    existing = _platform.ReadDistributions().SingleOrDefault(item => Matches(receipt, item));
                    if (existing is null)
                        throw new IOException("Windows did not confirm the prepared speech component. Continue setup to retry safely.");
                }
                try
                {
                    // This is deliberately the first Linux command, including on
                    // retry. guiApplications=false was placed in the imported tar.
                    await _platform.RunCommandAsync(BuildOwnerCheckInfo(receipt.Name, receipt.Owner), null,
                        token, TimeSpan.FromSeconds(90), OwnerCompletion(receipt.Owner));
                }
                catch (SetupCommandException error) when (error.ExitCode == 42 && resumingRegistered)
                {
                    receipt.Retired = true;
                    await SaveReceiptAsync(receipt, token);
                    continue;
                }
                receipt.Ready = true;
                await SaveReceiptAsync(receipt, token);
                await RemoveVerifiedPreparedTarAsync(receipt, token);
                return receipt.Name;
            }
            throw new IOException("Speech support changed during setup. Continue setup to create a separate component safely.");
        }
        finally { Changes.Release(); }
    }

    internal void ValidateBuildCapacity(string name, string backend)
    {
        if (backend is not "cuda" and not "vulkan" and not "cpu") throw new ArgumentException("Choose cuda, vulkan or cpu.", nameof(backend));
        EnsurePlainLocalPath(_root);
        IReadOnlyList<ManagedWslDistribution> registrations = _platform.ReadDistributions();
        Receipt? receipt = ReadReceipts().FirstOrDefault(item => !item.Retired && item.Ready && SameName(item.Name, name) &&
            registrations.Any(registration => Matches(item, registration)));
        if (receipt is null) throw new IOException("The speech component could not be verified. Continue setup before preparing its speech engine.");
        // Query the volume backing the registered VHD, which can differ from the
        // model/download drive. Linux df alone cannot see a nearly full host disk.
        RequireFreeSpace(receipt.BasePath, (backend == "cuda" ? 20 : 8) * GiB,
            backend == "cuda" ? CudaBuildCapacityMessage : OtherBuildCapacityMessage);
    }

    private Receipt? FindReceipt(string requestedName, IReadOnlyList<ManagedWslDistribution> registrations)
    {
        Receipt[] candidates = ReadReceipts().Where(item => !item.Retired &&
            (SameName(item.Name, requestedName) || SameName(item.RequestedName, requestedName)))
            .OrderByDescending(item => item.UpdatedAt).ToArray();
        Receipt? registered = candidates.FirstOrDefault(item => registrations.Any(registration => Matches(item, registration)));
        if (registered is not null) return registered;
        // A pre-import receipt may resume only while neither a registration nor a
        // partial storage folder exists. A crash with no captured GUID is kept,
        // and a new import is used instead of guessing that the old name is ours.
        return candidates.FirstOrDefault(item => item.RegistryId is null && !item.ImportRequested &&
            !registrations.Any(registration => SameName(registration.Name, item.Name)) &&
            !Directory.Exists(item.BasePath) && !File.Exists(item.BasePath));
    }

    private async Task<Receipt> CreateReceiptAsync(string requestedName, IReadOnlyList<ManagedWslDistribution> registrations, CancellationToken token)
    {
        string name = requestedName;
        if (registrations.Any(item => SameName(item.Name, name)) || ReadReceipts().Any(item => SameName(item.Name, name)))
            do { name = AppBrand.SpeechDistributionName + "-" + Guid.NewGuid().ToString("N")[..8]; }
            while (registrations.Any(item => SameName(item.Name, name)) || ReadReceipts().Any(item => SameName(item.Name, name)));
        string owner;
        do { owner = Guid.NewGuid().ToString("N"); }
        while (Directory.Exists(ComponentRoot(owner)) || File.Exists(ComponentRoot(owner)));
        var receipt = new Receipt
        {
            Owner = owner, Name = name, RequestedName = requestedName,
            BasePath = Path.Combine(ComponentRoot(owner), "distro"), HeadlessRevision = ManagedWslImage.HeadlessRevision
        };
        await SaveReceiptAsync(receipt, token);
        return receipt;
    }

    private IEnumerable<Receipt> ReadReceipts()
    {
        if (!Directory.Exists(_root)) yield break;
        string[] directories = Directory.EnumerateDirectories(_root).Take(MaximumComponentDirectories).ToArray();
        if (directories.Length == MaximumComponentDirectories) throw new IOException("The speech setup receipt directory exceeds its safe inspection limit.");
        foreach (string directory in directories)
        {
            if (!OwnerPattern.IsMatch(Path.GetFileName(directory))) continue;
            if (!IsPlainLocalPath(directory)) continue;
            string path = Path.Combine(directory, "setup.json");
            if (!IsPlainLocalPath(path)) continue;
            Receipt? receipt;
            try { receipt = ReadReceiptFile(path); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { continue; }
            if (receipt is not null && IsValidReceipt(receipt, directory) && IsPlainLocalPath(receipt.BasePath)) yield return receipt;
        }
    }

    private bool IsValidReceipt(Receipt receipt, string directory) => !string.IsNullOrEmpty(receipt.Owner) &&
        !string.IsNullOrEmpty(receipt.Name) && !string.IsNullOrEmpty(receipt.RequestedName) && !string.IsNullOrEmpty(receipt.BasePath) &&
        OwnerPattern.IsMatch(receipt.Owner) &&
        SamePath(directory, ComponentRoot(receipt.Owner)) && IsManagedName(receipt.Name) &&
        IsManagedName(receipt.RequestedName) && receipt.HeadlessRevision == ManagedWslImage.HeadlessRevision &&
        SamePath(receipt.BasePath, Path.Combine(directory, "distro"));

    private static bool Matches(Receipt receipt, ManagedWslDistribution registration) =>
        receipt.ImportCompleted && receipt.RegistryId is Guid id && id != Guid.Empty && registration.Id == id && registration.Version == 2 &&
        SameName(registration.Name, receipt.Name) && SamePath(registration.BasePath, receipt.BasePath);

    private async Task SaveReceiptAsync(Receipt receipt, CancellationToken token)
    {
        string directory = ComponentRoot(receipt.Owner);
        EnsurePlainLocalPath(directory);
        if (!IsValidReceipt(receipt, directory)) throw new IOException("Invalid speech setup receipt.");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "setup.json");
        EnsurePlainLocalPath(path);
        bool replacingOwnedReceipt = File.Exists(path);
        if (replacingOwnedReceipt)
        {
            token.ThrowIfCancellationRequested();
            Receipt? previous = ReadReceiptFile(path);
            if (previous is null || !IsValidReceipt(previous, directory) || previous.Owner != receipt.Owner ||
                !SameName(previous.Name, receipt.Name)) throw new IOException("The speech setup receipt changed unexpectedly.");
        }
        receipt.UpdatedAt = DateTimeOffset.UtcNow;
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt), token);
            File.Move(temporary, path, overwrite: replacingOwnedReceipt);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Receipt? ReadReceiptFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumReceiptBytes) throw new IOException("The speech setup receipt exceeds its safe size limit.");
        return JsonSerializer.Deserialize<Receipt>(input);
    }

    private async Task RemoveVerifiedPreparedTarAsync(Receipt receipt, CancellationToken token)
    {
        if (receipt.PreparedTar is not string path || receipt.PreparedSha256 is not string digest ||
            !TarPattern.IsMatch(Path.GetFileName(path)) || !SamePath(Path.GetDirectoryName(path)!, ComponentRoot(receipt.Owner))) return;
        EnsurePlainLocalPath(path);
        if (!File.Exists(path) || !await LocalTalkProvisioner.VerifyDigestAsync(path, digest, token)) return;
        File.Delete(path); // Only this receipt's exact verified tar, never its VHD or import folder.
    }

    private void RequireFreeSpace(string path, long bytes, string message)
    {
        EnsurePlainLocalPath(path);
        if (_platform.AvailableFreeSpace(path) < bytes)
            throw new SetupUserException(message);
    }

    private string ComponentRoot(string owner) => Path.Combine(_root, owner);
    private static bool IsManagedName(string? name) => name is not null && !name.Contains('\n') && !name.Contains('\r') &&
        AppBrand.IsOwnedSpeechDistributionName(name);
    private static bool SameName(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return string.Equals(NormalizeLocalPath(first), NormalizeLocalPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }
    private static string NormalizeLocalPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && char.IsAsciiLetter(path[4]) && path[5] == ':') path = path[4..];
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new ArgumentException("Speech support requires a local Windows drive.");
        return path;
    }
    private static void EnsurePlainLocalPath(string path)
    {
        string normalized = NormalizeLocalPath(path);
        for (string? ancestor = normalized; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            try
            {
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The speech storage folder is redirected. Choose a local data folder and continue setup.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static bool IsPlainLocalPath(string path)
    {
        try { EnsurePlainLocalPath(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
    private static async Task<string> DigestAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }

    internal static ProcessStartInfo BuildImportInfo(string name, string storage, string preparedTar)
    {
        if (!IsManagedName(name)) throw new ArgumentException("Invalid managed speech component name.", nameof(name));
        _ = NormalizeLocalPath(storage); _ = NormalizeLocalPath(preparedTar);
        return Command("--import", name, storage, preparedTar, "--version", "2");
    }
    internal static ProcessStartInfo BuildOwnerCheckInfo(string name, string owner)
    {
        if (!IsManagedName(name)) throw new ArgumentException("Invalid managed speech component name.", nameof(name));
        if (!OwnerPattern.IsMatch(owner)) throw new ArgumentException("Invalid speech ownership token.", nameof(owner));
        // The byte count also rejects NUL bytes that a shell variable cannot
        // retain. A sentinel preserves the required final LF during substitution.
        const string script = "[ \"$(/usr/bin/stat -c %s -- \"$2\")\" = 33 ] && [ \"$(/bin/cat -- \"$2\"; printf .)\" = \"$(printf '%s\\n.' \"$1\")\" ] || exit 42; printf 'SAYWHAT_MANAGED_DISTRO_OWNER=%s\\n' \"$1\"";
        return Command("--distribution", name, "--user", "root", "--exec", "/bin/sh", "-c", script,
            "saywhat-owner-check", owner, ManagedWslImage.OwnerMarkerPath);
    }
    private static string OwnerCompletion(string owner) => "SAYWHAT_MANAGED_DISTRO_OWNER=" + owner;
    private static ProcessStartInfo Command(params string[] arguments)
    {
        var command = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        command.Environment["WSL_UTF8"] = "1";
        command.StandardOutputEncoding = Encoding.UTF8;
        command.StandardErrorEncoding = Encoding.UTF8;
        foreach (string argument in arguments) command.ArgumentList.Add(argument);
        return command;
    }

    private sealed class Receipt
    {
        public string Owner { get; set; } = "";
        public string Name { get; set; } = "";
        public string RequestedName { get; set; } = "";
        public string BasePath { get; set; } = "";
        public Guid? RegistryId { get; set; }
        public int HeadlessRevision { get; set; }
        public bool ImportRequested { get; set; }
        public bool ImportCompleted { get; set; }
        public bool Ready { get; set; }
        public bool Retired { get; set; }
        public string? PreparedTar { get; set; }
        public string? PreparedSha256 { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}

internal sealed class WindowsManagedWslSetupPlatform(LocalTalkProvisioner provisioner) : IManagedWslSetupPlatform
{
    public IReadOnlyList<ManagedWslDistribution> ReadDistributions()
    {
        var result = new List<ManagedWslDistribution>();
        using RegistryKey? registrations = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (registrations is null) return result;
        foreach (string key in registrations.GetSubKeyNames())
        {
            if (!Guid.TryParse(key, out Guid id)) continue;
            using RegistryKey? item = registrations.OpenSubKey(key);
            if (item?.GetValue("DistributionName") is string name && item.GetValue("BasePath") is string path)
                result.Add(new(id, name, path, item.GetValue("Version") is int version ? version : 0));
        }
        return result;
    }
    public long AvailableFreeSpace(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    public Task DownloadImageAsync(string destination, IProgress<ProvisionProgress>? progress, CancellationToken token) =>
        provisioner.DownloadVerifiedAsync(ManagedWslImage.UbuntuAsset, destination, progress, token);
    public Task PrepareImageAsync(string inputGzip, string outputTar, string owner, CancellationToken token) =>
        ManagedWslImage.PrepareAsync(inputGzip, outputTar, owner, token);
    public Task RunCommandAsync(ProcessStartInfo command, IProgress<ProvisionProgress>? progress,
        CancellationToken token, TimeSpan timeout, string? requiredOutputLine = null) =>
        LocalTalkProvisioner.RunCommandAsync(command, progress, token, timeout, requiredOutputLine);
}

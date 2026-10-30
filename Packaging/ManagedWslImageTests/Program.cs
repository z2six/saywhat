using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncomingTranslator;

if (args.Length != 1 || args[0] is not ("--self-test" or "--verify-pinned-image"))
    throw new ArgumentException("Choose --self-test or --verify-pinned-image.");
bool verifyImage = args[0] == "--verify-pinned-image";
string? runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
if (verifyImage && (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || string.IsNullOrWhiteSpace(runnerTemp)))
    throw new InvalidOperationException("The full pinned image check runs only in GitHub Actions with isolated RUNNER_TEMP storage.");
string temporaryBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runnerTemp ?? Path.GetTempPath()));
string root = Path.Combine(temporaryBase, "saywhat-wsl-image-" + Guid.NewGuid().ToString("N"));
RequireOwnedTemporaryRoot(root, temporaryBase);
Directory.CreateDirectory(root);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
CancellationToken token = timeout.Token;
try
{
    if (!verifyImage)
    {
        int checks = ManagedWslImageTests.Run(root);
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "self-test", checks, passed = true }));
        return;
    }
    ProvisionAsset asset = ManagedWslImage.UbuntuAsset;
    string downloaded = Path.Combine(root, asset.FileName), prepared = Path.Combine(root, "headless-speech.tar");
    string driveRoot = Path.GetPathRoot(root) ?? throw new IOException("Cannot check the isolated test storage drive.");
    if (new DriveInfo(driveRoot).AvailableFreeSpace < ManagedWslImage.PrepareRequiredDiskBytes + asset.SizeBytes)
        throw new IOException("The CI runner has insufficient free space for the pinned image check.");
    await DownloadPinnedAsync(asset, downloaded, token);
    const string owner = "0123456789abcdef0123456789abcdef";
    await ManagedWslImage.PrepareAsync(downloaded, prepared, owner, token);
    RawPaxAudit originalRaw;
    using (FileStream source = File.OpenRead(downloaded))
    using (var gzip = new GZipStream(source, CompressionMode.Decompress))
        originalRaw = InspectRawPax(gzip, token);
    RawPaxAudit preparedRaw;
    using (FileStream source = File.OpenRead(prepared)) preparedRaw = InspectRawPax(source, token);
    string[] expectedCapabilities =
    [
        "0100000200200000000000000000000000000000",
        "00000002CF002C01000000000000000000000000",
        "0100000200148000000000000000000000000000"
    ];
    if (!originalRaw.Capabilities.Order(StringComparer.Ordinal).SequenceEqual(expectedCapabilities.Order(StringComparer.Ordinal)) ||
        preparedRaw.Capabilities.Count != 0 || originalRaw.NonUtf8Capabilities != 2)
        throw new InvalidDataException("The pinned image does not satisfy the exact binary file-capability omission policy.");
    ImageSnapshot original;
    using (FileStream source = File.OpenRead(downloaded))
    using (var gzip = new GZipStream(source, CompressionMode.Decompress))
        original = Inspect(gzip, token);
    ImageSnapshot result;
    using (FileStream source = File.OpenRead(prepared)) result = Inspect(source, token);
    foreach ((string name, EntrySnapshot before) in original.Entries)
    {
        if (!result.Entries.TryGetValue(name, out EntrySnapshot? after))
            throw new InvalidDataException("The prepared image lost an original archive entry.");
        if (before.Format != after.Format || before.Type != after.Type || before.Mode != after.Mode ||
            before.Uid != after.Uid || before.Gid != after.Gid || before.Modified != after.Modified ||
            before.Link != after.Link || before.User != after.User || before.Group != after.Group ||
            before.ExtraAttributes != after.ExtraAttributes)
            throw new InvalidDataException("The prepared image changed original Linux entry metadata.");
        if (name is not ("etc/wsl.conf" or "etc/wsl-distribution.conf") && before.Digest != after.Digest)
            throw new InvalidDataException("The prepared image changed an unrelated file's payload.");
    }
    if (result.Entries.Count < original.Entries.Count || result.Entries.Count > original.Entries.Count + 4 ||
        result.SymbolicLinks == 0 || result.ExecutableFiles == 0 || original.CapabilityEntries != 3 || result.CapabilityEntries != 0)
        throw new InvalidDataException("The prepared image has an unexpected Linux filesystem structure.");
    if (!result.Configurations.TryGetValue("etc/wsl.conf", out string? config) || !AllFalse(config, "general", "guiApplications") ||
        !result.Configurations.TryGetValue("etc/wsl-distribution.conf", out string? distribution) ||
        !AllFalse(distribution, "shortcut", "enabled") || !AllFalse(distribution, "windowsterminal", "enabled") ||
        !result.Configurations.TryGetValue("etc/saywhat-owner", out string? marker) || marker != owner + "\n" ||
        result.Entries["etc/saywhat-owner"].Uid != 0 || result.Entries["etc/saywhat-owner"].Gid != 0)
        throw new InvalidDataException("The prepared image does not prove headless startup and component ownership.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode = "verify-pinned-image", passed = true, compressedBytes = asset.SizeBytes,
        rawTarBytes = ManagedWslImage.UncompressedTarBytes, preparedTarBytes = new FileInfo(prepared).Length,
        entries = result.Entries.Count, files = result.Files, directories = result.Directories,
        symbolicLinks = result.SymbolicLinks, hardLinks = result.HardLinks, executableFiles = result.ExecutableFiles,
        paxEntries = result.PaxEntries, gnuEntries = result.GnuEntries,
        headlessRevision = ManagedWslImage.HeadlessRevision, ownerMarker = true,
        omittedFileCapabilities = originalRaw.Capabilities.Count, sourceBinaryCapabilities = originalRaw.NonUtf8Capabilities,
        retainedPaxAttributesCheckedAsRawUtf8 = true
    }));
}
finally
{
    // This folder contains only this process's synthetic files and raw archives.
    // No archive entry is ever extracted, imported, executed or uploaded.
    RequireOwnedTemporaryRoot(root, temporaryBase);
    if (Directory.Exists(root))
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to clean redirected temporary test storage.");
        Directory.Delete(root, recursive: true);
    }
}

static async Task DownloadPinnedAsync(ProvisionAsset asset, string destination, CancellationToken token)
{
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    using HttpResponseMessage response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength != asset.SizeBytes)
        throw new InvalidDataException("Canonical returned an unexpected pinned image size.");
    await using Stream source = await response.Content.ReadAsStreamAsync(token);
    await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, FileOptions.Asynchronous);
    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] buffer = new byte[131_072];
    long received = 0;
    int read;
    while ((read = await source.ReadAsync(buffer, token)) > 0)
    {
        if (read > asset.SizeBytes - received) throw new InvalidDataException("The pinned image download exceeded its declared size.");
        await output.WriteAsync(buffer.AsMemory(0, read), token);
        digest.AppendData(buffer, 0, read); received += read;
    }
    if (received != asset.SizeBytes || !Convert.ToHexString(digest.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The downloaded Ubuntu image failed its pinned size or SHA-256 check.");
}

static ImageSnapshot Inspect(Stream stream, CancellationToken token)
{
    var result = new ImageSnapshot();
    using var reader = new TarReader(stream, leaveOpen: true);
    TarEntry? entry;
    while ((entry = reader.GetNextEntry(copyData: false)) is not null)
    {
        token.ThrowIfCancellationRequested();
        string name = entry.Name;
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        name = name.TrimEnd('/');
        if (name.Length == 0) name = ".";
        string digest = "";
        if (entry.DataStream is not null)
        {
            if (name is "etc/wsl.conf" or "etc/wsl-distribution.conf" or "etc/saywhat-owner")
            {
                if (entry.Length > ManagedWslImage.MaximumConfigBytes) throw new InvalidDataException("Configuration exceeds its bound.");
                byte[] data = new byte[checked((int)entry.Length)];
                entry.DataStream.ReadExactly(data);
                result.Configurations.Add(name, new UTF8Encoding(false, true).GetString(data));
                digest = Convert.ToHexString(SHA256.HashData(data));
            }
            else digest = Convert.ToHexString(SHA256.HashData(entry.DataStream));
        }
        // Standard PAX fields may be regenerated in a canonical numeric format.
        // Compare their typed metadata above and all retained custom attributes
        // exactly. Raw metadata is independently audited: binary capability
        // values are deliberately omitted, not declared preserved after a
        // lossy string decode; every retained value is valid single-line UTF-8.
        if (entry is PaxTarEntry withCapability && withCapability.ExtendedAttributes.ContainsKey(ManagedWslImage.CapabilityAttribute))
            result.CapabilityEntries++;
        string extra = entry is PaxTarEntry pax ? JsonSerializer.Serialize(pax.ExtendedAttributes
            .Where(attribute => attribute.Key != ManagedWslImage.CapabilityAttribute &&
                attribute.Key is not ("path" or "linkpath" or "size" or "mtime" or "atime" or "ctime" or "uid" or "gid" or "uname" or "gname"))
            .OrderBy(attribute => attribute.Key, StringComparer.Ordinal).ToDictionary(attribute => attribute.Key, attribute => attribute.Value)) : "";
        result.Entries.Add(name, new(entry.Format, entry.EntryType, entry.Mode, entry.Uid, entry.Gid, entry.ModificationTime,
            entry.LinkName, (entry as PosixTarEntry)?.UserName ?? "", (entry as PosixTarEntry)?.GroupName ?? "", extra, digest));
        if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
        {
            result.Files++;
            if ((entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) result.ExecutableFiles++;
        }
        if (entry.EntryType == TarEntryType.Directory) result.Directories++;
        if (entry.EntryType == TarEntryType.SymbolicLink) result.SymbolicLinks++;
        if (entry.EntryType == TarEntryType.HardLink) result.HardLinks++;
        if (entry.Format == TarEntryFormat.Pax) result.PaxEntries++;
        if (entry.Format == TarEntryFormat.Gnu) result.GnuEntries++;
    }
    return result;
}

// Read raw PAX record lengths and bytes independently of System.Formats.Tar's
// string parser. It would otherwise decode the same corrupt binary metadata
// on both sides and let the original regression pass. Never extracts entries.
static RawPaxAudit InspectRawPax(Stream stream, CancellationToken token)
{
    var result = new RawPaxAudit();
    var utf8 = new UTF8Encoding(false, true);
    byte[] header = new byte[512], discard = new byte[131_072];
    int entries = 0;
    void Skip(long count)
    {
        if (stream.CanSeek)
        {
            if (count > stream.Length - stream.Position) throw new InvalidDataException("Truncated raw archive payload.");
            stream.Seek(count, SeekOrigin.Current); return;
        }
        while (count > 0)
        {
            token.ThrowIfCancellationRequested();
            int read = stream.Read(discard, 0, (int)Math.Min(discard.Length, count));
            if (read == 0) throw new InvalidDataException("Truncated raw archive payload.");
            count -= read;
        }
    }
    while (true)
    {
        token.ThrowIfCancellationRequested();
        stream.ReadExactly(header);
        if (header.All(value => value == 0)) break;
        if (++entries > 200_000) throw new InvalidDataException("Too many raw archive headers.");
        string rawSize = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
        if (rawSize.Length == 0 || rawSize.Any(value => value is < '0' or > '7'))
            throw new InvalidDataException("Unsupported raw archive size field.");
        long size = Convert.ToInt64(rawSize, 8);
        if (size > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Raw archive entry exceeds its bound.");
        if (header[156] is (byte)'x' or (byte)'g')
        {
            if (size > 1_048_576) throw new InvalidDataException("Raw PAX metadata exceeds its bound.");
            byte[] data = new byte[checked((int)size)];
            stream.ReadExactly(data);
            int offset = 0;
            while (offset < data.Length)
            {
                ReadOnlySpan<byte> remaining = data.AsSpan(offset);
                int space = remaining.IndexOf((byte)' ');
                if (space is < 1 or > 12 || remaining[..space].ToArray().Any(digit => digit < (byte)'0' || digit > (byte)'9') ||
                    !int.TryParse(Encoding.ASCII.GetString(remaining[..space]), out int recordLength) ||
                    recordLength <= space + 2 || recordLength > remaining.Length || remaining[recordLength - 1] != (byte)'\n')
                    throw new InvalidDataException("Malformed raw PAX record.");
                ReadOnlySpan<byte> record = remaining.Slice(space + 1, recordLength - space - 2);
                int separator = record.IndexOf((byte)'=');
                if (separator <= 0) throw new InvalidDataException("Malformed raw PAX key/value.");
                string key = utf8.GetString(record[..separator]);
                ReadOnlySpan<byte> value = record[(separator + 1)..];
                if (key == ManagedWslImage.CapabilityAttribute)
                {
                    if (header[156] != (byte)'x') throw new InvalidDataException("Unexpected global file-capability record.");
                    result.Capabilities.Add(Convert.ToHexString(value));
                    try { utf8.GetString(value); }
                    catch (DecoderFallbackException) { result.NonUtf8Capabilities++; }
                }
                else
                {
                    // The pinned image has no other binary PAX values. Assert
                    // that fact before relying on the managed string API.
                    utf8.GetString(value);
                    if (value.IndexOfAny((byte)0, (byte)'\n', (byte)'\r') >= 0)
                        throw new InvalidDataException("Retained raw PAX metadata is not single-line text.");
                }
                offset += recordLength;
            }
            Skip((512 - size % 512) % 512);
        }
        else Skip(size + (512 - size % 512) % 512);
    }
    return result;
}

static bool AllFalse(string text, string targetSection, string targetKey)
{
    string section = "";
    int found = 0;
    foreach (string raw in text.Split('\n'))
    {
        string line = raw.Trim();
        if (line.StartsWith('[') && line.Contains(']')) { section = line[1..line.IndexOf(']')].Trim(); continue; }
        int separator = line.IndexOf('=');
        if (separator < 0 || !section.Equals(targetSection, StringComparison.OrdinalIgnoreCase) ||
            !line[..separator].Trim().Equals(targetKey, StringComparison.OrdinalIgnoreCase)) continue;
        string value = line[(separator + 1)..].Split(['#', ';'], 2)[0].Trim();
        if (!value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        found++;
    }
    return found > 0;
}

static void RequireOwnedTemporaryRoot(string root, string temporaryBase)
{
    string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryBase));
    string name = Path.GetFileName(full);
    const string prefix = "saywhat-wsl-image-";
    if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.Ordinal) || !name.StartsWith(prefix, StringComparison.Ordinal) ||
        !Guid.TryParseExact(name[prefix.Length..], "N", out _))
        throw new IOException("Temporary test storage is outside its isolated root.");
    for (string? path = full; path is not null; path = Path.GetDirectoryName(path))
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Temporary test storage is redirected.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}

sealed record EntrySnapshot(TarEntryFormat Format, TarEntryType Type, UnixFileMode Mode, int Uid, int Gid,
    DateTimeOffset Modified, string Link, string User, string Group, string ExtraAttributes, string Digest);
sealed class ImageSnapshot
{
    internal Dictionary<string, EntrySnapshot> Entries { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> Configurations { get; } = new(StringComparer.Ordinal);
    internal int Files, Directories, SymbolicLinks, HardLinks, ExecutableFiles, PaxEntries, GnuEntries, CapabilityEntries;
}
sealed class RawPaxAudit
{
    internal List<string> Capabilities { get; } = [];
    internal int NonUtf8Capabilities;
}

using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

// Prepare the isolated speech distro before its first boot. Never extract a
// Unix filesystem onto Windows, launch Linux, or change the user's WSL settings.
internal static class ManagedWslImage
{
    internal const int HeadlessRevision = 2;
    internal const string CapabilityAttribute = "SCHILY.xattr.security.capability";
    internal const string OwnerMarkerPath = "/etc/saywhat-owner";
    internal const int MaximumConfigBytes = 65_536;
    // The pinned gzip trailer declares this exact raw TAR size (<4 GiB).
    // Allow both raw and rewritten archives plus generous metadata headroom.
    internal const long UncompressedTarBytes = 1_275_596_800;
    internal const long PrepareRequiredDiskBytes = 2L * UncompressedTarBytes + 512L * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private const long MaximumArchiveBytes = 4L * 1024 * 1024 * 1024;
    // Canonical's released Noble WSL image is a gzip-compressed PAX tar, despite
    // its .wsl suffix. Pin release bytes, never a moving daily/current image.
    // https://releases.ubuntu.com/24.04.5/SHA256SUMS (also signed by SHA256SUMS.gpg)
    internal static readonly ProvisionAsset UbuntuAsset = new("ubuntu-24.04.5-wsl-amd64.wsl", 388_975_696,
        "bb415d824822c4b878125729af451a5d18fb13d1cf5cbed9a7393ad64ac6039e",
        "https://releases.ubuntu.com/24.04.5/ubuntu-24.04.5-wsl-amd64.wsl");
    private static readonly UTF8Encoding ConfigEncoding = new(false, true);
    private static readonly Regex SectionLine = new(@"^\s*\[([^\[\]]+)\]\s*(?:[#;].*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyLine = new(@"^(\s*([^=\s#;][^=]*?)\s*=\s*)(.*)$", RegexOptions.CultureInvariant);

    internal static async Task PrepareAsync(string inputGzipPath, string outputTarPath, string owner,
        CancellationToken token = default)
    {
        ValidateOwner(owner);
        token.ThrowIfCancellationRequested();
        string input = Path.GetFullPath(inputGzipPath), output = Path.GetFullPath(outputTarPath);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The prepared speech image must have its own output path.", nameof(outputTarPath));
        RequirePlainPath(input); RequirePlainPath(output);
        // Keep the same read-only handle through verification and rewriting.
        // FileShare.Read prevents another Windows writer from replacing bytes.
        await using FileStream source = new(input, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != UbuntuAsset.SizeBytes ||
            !Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false))
                .Equals(UbuntuAsset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Ubuntu speech image does not match the pinned release. It was not imported.");
        source.Seek(-4, SeekOrigin.End);
        byte[] trailer = new byte[4];
        await source.ReadExactlyAsync(trailer, token).ConfigureAwait(false);
        uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        if (rawSize == 0 || rawSize != UncompressedTarBytes)
            throw new InvalidDataException("The Ubuntu speech image has an unexpected uncompressed size.");
        source.Position = 0;
        token.ThrowIfCancellationRequested();
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("The prepared speech image output already exists.");
        string parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("Choose an image output directory.", nameof(outputTarPath));
        string driveRoot = Path.GetPathRoot(output) ?? throw new IOException("The speech image storage drive could not be checked.");
        if (new DriveInfo(driveRoot).AvailableFreeSpace < PrepareRequiredDiskBytes)
            throw new IOException("Preparing speech support needs at least 3.1 GB of free space on its storage drive. Free some space, then retry setup.");
        Directory.CreateDirectory(parent);
        RequirePlainPath(parent);
        string temporary = Path.Combine(parent, Path.GetFileName(output) + ".partial-" + Guid.NewGuid().ToString("N"));
        string uncompressed = temporary + ".source.tar";
        bool created = false, sourceCreated = false;
        try
        {
            // .NET 8's PAX writer buffers a complete file when handed an entry
            // from a non-seekable gzip stream. A temporary raw TAR lets it copy
            // every payload directly instead. This is archive decompression,
            // never filesystem extraction; no archive filename is opened here.
            await using FileStream raw = new(uncompressed, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            sourceCreated = true;
            using (var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true))
            {
                byte[] buffer = new byte[131_072];
                long decompressed = 0;
                int read;
                while ((read = await gzip.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (read > MaximumArchiveBytes - decompressed)
                        throw new InvalidDataException("The speech image exceeds the supported archive size.");
                    await raw.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    decompressed += read;
                }
                if (decompressed != UncompressedTarBytes)
                    throw new InvalidDataException("The Ubuntu speech image did not decompress to its verified size.");
            }
            await raw.FlushAsync(token).ConfigureAwait(false);
            raw.Position = 0;
            await using (FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131_072, FileOptions.Asynchronous))
            {
                created = true;
                await Task.Run(() =>
                {
                    RewriteTar(raw, destination, owner, token);
                }, token).ConfigureAwait(false);
                await destination.FlushAsync(token).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            RequirePlainPath(output);
            File.Move(temporary, output, overwrite: false);
            created = false;
        }
        finally
        {
            foreach (string path in new[] { created ? temporary : "", sourceCreated ? uncompressed : "" })
            {
                if (path.Length == 0) continue;
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    // Both streams remain open. Output must be seekable. Production supplies
    // seekable input too to avoid TarWriter's buffering of non-seekable PAX data.
    // The production entry point accepts only the exact pinned archive above;
    // this stream seam permits small offline fixtures without real rootfs files.
    internal static void RewriteTar(Stream input, Stream output, string owner, CancellationToken token = default)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output);
        if (ReferenceEquals(input, output) || !input.CanRead || !output.CanWrite || !output.CanSeek)
            throw new ArgumentException("Use separate readable input and writable, seekable output streams.");
        token.ThrowIfCancellationRequested();
        using var boundedInput = new CheckedStream(input, token, MaximumArchiveBytes);
        using var checkedOutput = new CheckedStream(output, token);
        using var reader = new TarReader(boundedInput, leaveOpen: true);
        using var writer = new TarWriter(checkedOutput, TarEntryFormat.Pax, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        bool foundEtc = false, foundConfig = false, foundDistribution = false, foundOwner = false;
        long payloadBytes = 0;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaximumEntries) throw new InvalidDataException("The speech image contains too many archive entries.");
            string name;
            try { name = CanonicalName(entry.Name); }
            catch (InvalidDataException error)
            {
                string displayed = entry.Name.Length <= 256 ? entry.Name : entry.Name[..256] + "…";
                throw new InvalidDataException($"{error.Message} Entry {count} ({entry.Format}/{entry.EntryType}), path {JsonSerializer.Serialize(displayed)}.", error);
            }
            if (name == "." && entry.EntryType != TarEntryType.Directory)
                throw new InvalidDataException("The speech image root directory is redirected.");
            if (!names.Add(name)) throw new InvalidDataException("The speech image contains duplicate archive paths: " + name);
            if (entry.Length < 0 || entry.Length > MaximumArchiveBytes - payloadBytes)
                throw new InvalidDataException("The speech image exceeds the supported archive size.");
            payloadBytes += entry.Length;
            entry = WithoutFileCapabilities(entry);
            if (name == "etc")
            {
                if (entry.EntryType != TarEntryType.Directory) throw new InvalidDataException("The speech image's etc directory is redirected.");
                foundEtc = true;
            }
            if (name.StartsWith("etc/wsl.conf/", StringComparison.Ordinal) ||
                name.StartsWith("etc/wsl-distribution.conf/", StringComparison.Ordinal) ||
                name.StartsWith("etc/saywhat-owner/", StringComparison.Ordinal))
                throw new InvalidDataException("A speech image configuration path is a directory.");
            string? replacement = null;
            if (name == "etc/wsl.conf")
            {
                foundConfig = true;
                replacement = SetFalse(ReadConfig(entry), "general", "guiApplications");
            }
            else if (name == "etc/wsl-distribution.conf")
            {
                foundDistribution = true;
                replacement = SetFalse(SetFalse(ReadConfig(entry), "shortcut", "enabled"), "windowsterminal", "enabled");
            }
            else if (name == "etc/saywhat-owner")
            {
                foundOwner = true;
                if (ReadConfig(entry) != owner + "\n")
                    throw new InvalidDataException("The speech image already belongs to a different component.");
                replacement = owner + "\n";
            }
            if (replacement is null) writer.WriteEntry(entry);
            else
            {
                byte[] bytes = ConfigEncoding.GetBytes(replacement);
                if (bytes.Length > MaximumConfigBytes) throw new InvalidDataException("The prepared speech configuration is too large.");
                using var data = new MemoryStream(bytes, writable: false);
                entry.DataStream = data;
                writer.WriteEntry(entry); // Retain format, Linux mode, ownership and all retained extended attributes.
            }
        }
        token.ThrowIfCancellationRequested();
        // Refuse an empty/truncated input instead of creating a config-only distro.
        if (count == 0) throw new InvalidDataException("The speech image contains no filesystem entries.");
        if (!foundEtc) WriteNew(writer, "etc", null);
        if (!foundConfig) WriteNew(writer, "etc/wsl.conf", "[general]\nguiApplications=false\n");
        if (!foundDistribution) WriteNew(writer, "etc/wsl-distribution.conf", "[shortcut]\nenabled=false\n\n[windowsterminal]\nenabled=false\n");
        if (!foundOwner) WriteNew(writer, "etc/saywhat-owner", owner + "\n");
    }

    private static TarEntry WithoutFileCapabilities(TarEntry entry)
    {
        if (entry is not PaxTarEntry pax || !pax.ExtendedAttributes.ContainsKey(CapabilityAttribute)) return entry;
        // The pinned Canonical image has three executable file-capability grants
        // for ping, snap-confine and gst-ptp-helper. Speech services need none of
        // them. These values are binary, but .NET's PAX API decodes/re-encodes
        // strings as UTF-8; two of those grants contain invalid UTF-8 bytes and
        // cannot survive that round trip. Omit only the exact capability key,
        // rather than restoring malformed privileges or dropping other xattrs.
        // The released image is pinned and independently checked for this exact
        // metadata policy. Do not treat this as a general binary-xattr copier.
        if (entry.EntryType != TarEntryType.RegularFile)
            throw new InvalidDataException("The speech image has unexpected file-capability metadata.");
        return new PaxTarEntry(entry.EntryType, entry.Name,
            pax.ExtendedAttributes.Where(attribute => attribute.Key != CapabilityAttribute))
        {
            Mode = entry.Mode, Uid = entry.Uid, Gid = entry.Gid, ModificationTime = entry.ModificationTime,
            UserName = pax.UserName, GroupName = pax.GroupName, DataStream = entry.DataStream
        };
    }

    private static string ReadConfig(TarEntry entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length > MaximumConfigBytes)
            throw new InvalidDataException("A speech image configuration is redirected or too large: " + entry.Name);
        if (entry.DataStream is null) return "";
        byte[] data = new byte[checked((int)entry.Length)];
        entry.DataStream.ReadExactly(data);
        string text = ConfigEncoding.GetString(data);
        if (text.Contains('\0')) throw new InvalidDataException("The speech image configuration contains invalid text.");
        return text;
    }

    private static string SetFalse(string text, string targetSection, string targetKey)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Contains('\r')) throw new InvalidDataException("The speech image configuration contains invalid line endings.");
        var result = new List<string>();
        bool inTarget = false, sawSection = false, sawKey = false;
        string[] lines = text.Split('\n');
        int length = lines.Length;
        if (length > 0 && lines[^1].Length == 0) length--;
        void FinishSection() { if (inTarget && !sawKey) result.Add(targetKey + "=false"); }
        for (int index = 0; index < length; index++)
        {
            string line = lines[index];
            Match section = SectionLine.Match(line);
            if (section.Success)
            {
                FinishSection();
                inTarget = section.Groups[1].Value.Trim().Equals(targetSection, StringComparison.OrdinalIgnoreCase);
                sawSection |= inTarget;
                sawKey = false;
            }
            else if (line.TrimStart().StartsWith('['))
                throw new InvalidDataException("The speech image configuration contains an ambiguous section.");
            if (inTarget)
            {
                Match key = KeyLine.Match(line);
                if (key.Success && key.Groups[2].Value.Trim().Equals(targetKey, StringComparison.OrdinalIgnoreCase))
                {
                    // Set every occurrence, including duplicate sections/keys, so
                    // no parser ordering can leave GUI integration enabled.
                    string oldValue = key.Groups[3].Value;
                    int comment = oldValue.IndexOfAny(['#', ';']);
                    result.Add(key.Groups[1].Value + "false" + (comment >= 0 ? " " + oldValue[comment..] : ""));
                    sawKey = true;
                    continue;
                }
            }
            result.Add(line);
        }
        FinishSection();
        if (!sawSection)
        {
            if (result.Count > 0 && result[^1].Length != 0) result.Add("");
            result.Add("[" + targetSection + "]"); result.Add(targetKey + "=false");
        }
        return string.Join("\n", result) + "\n";
    }

    private static string CanonicalName(string name)
    {
        // TAR names use POSIX separators. Backslashes are literal filename
        // bytes (for example systemd's \x2d-escaped units), never separators.
        // Entries are streamed into another TAR, never extracted on Windows.
        if (name.Length is 0 or > 4096 || name.Any(char.IsControl) || name.StartsWith('/'))
            throw new InvalidDataException("The speech image contains an unsupported archive path.");
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        name = name.TrimEnd('/');
        if (name is "" or ".") return ".";
        if (name.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("The speech image contains an ambiguous archive path.");
        return name;
    }

    private static void WriteNew(TarWriter writer, string name, string? content)
    {
        var entry = new PaxTarEntry(content is null ? TarEntryType.Directory : TarEntryType.RegularFile, name)
        {
            Uid = 0, Gid = 0, UserName = "root", GroupName = "root", ModificationTime = DateTimeOffset.UnixEpoch,
            Mode = content is null ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
        };
        using var data = content is null ? null : new MemoryStream(ConfigEncoding.GetBytes(content), writable: false);
        if (data is not null) entry.DataStream = data;
        writer.WriteEntry(entry);
    }

    private static void ValidateOwner(string owner)
    {
        if (owner is null || owner.Length != 32 || !owner.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new ArgumentException("A valid speech component owner token is required.", nameof(owner));
    }

    private static void RequirePlainPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("Speech images must use local storage.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The speech image path is redirected.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class CheckedStream(Stream inner, CancellationToken token, long maximumRead = long.MaxValue) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set { token.ThrowIfCancellationRequested(); inner.Position = value; } }
        public override void Flush() { token.ThrowIfCancellationRequested(); inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            int count = inner.Read(buffer);
            _read += count;
            if (_read > maximumRead) throw new InvalidDataException("The speech image exceeds the supported archive size.");
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return inner.Seek(offset, origin); }
        public override void SetLength(long value) { token.ThrowIfCancellationRequested(); inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { token.ThrowIfCancellationRequested(); inner.Write(buffer); }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); } // Caller owns inner.
    }
}

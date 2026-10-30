using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;

namespace IncomingTranslator;

internal static class ManagedWslImageTests
{
    private const string Owner = "0123456789abcdef0123456789abcdef";
    private const UnixFileMode ExecutableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private static readonly DateTimeOffset Stamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    internal static int Run(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Managed WSL image: " + message); checks++; }
        void Refuses(Action action, string message)
        {
            bool refused = false;
            try { action(); }
            catch (InvalidDataException) { refused = true; }
            catch (ArgumentException) { refused = true; }
            Check(refused, message);
        }
        byte[] Rewrite(byte[] source, string owner = Owner)
        {
            using var input = new MemoryStream(source, writable: false);
            using var output = new MemoryStream();
            ManagedWslImage.RewriteTar(input, output, owner);
            Check(input.CanRead && output.CanWrite, "stream rewriting leaves its caller's streams open");
            return output.ToArray();
        }
        foreach (TarEntryFormat format in new[] { TarEntryFormat.Pax, TarEntryFormat.Gnu })
        {
            string longName = "usr/share/" + new string('a', 150) + "/fixture.txt";
            const string escapedUnit = @"usr/lib/systemd/system/system-systemd\x2dcryptsetup.slice";
            const string literalConfig = @"etc\wsl.conf";
            byte[] original = Archive(format, writer =>
            {
                Add(writer, format, TarEntryType.Directory, "./");
                Add(writer, format, TarEntryType.Directory, "./etc");
                Add(writer, format, TarEntryType.RegularFile, "./etc/wsl.conf",
                    "# retained\r\n[BOOT]\r\nsystemd=true\r\n[GeNeRaL]\r\n  GuiApplications = TRUE # retained comment\r\n[gpu]\r\nenabled=true\r\nappendLibPath=true\r\n[network]\r\ngenerateResolvConf=false\r\n");
                Add(writer, format, TarEntryType.RegularFile, "etc/wsl-distribution.conf",
                    "[oobe]\ncommand=/usr/lib/wsl/wsl-setup\ndefaultUid=1000\n[SHORTCUT]\nicon=/usr/share/wsl/ubuntu.ico\nEnabled=true\n[WindowsTerminal]\nProfileTemplate=/usr/share/wsl/terminal-profile.json\n[unrelated]\nenabled=true\n");
                Add(writer, format, TarEntryType.RegularFile, "usr/bin/fixture", "#!/bin/sh\nexit 0\n", mode: ExecutableMode);
                Add(writer, format, TarEntryType.RegularFile, longName, "retained long path");
                Add(writer, format, TarEntryType.RegularFile, escapedUnit, "synthetic systemd unit\n", mode: ExecutableMode);
                Add(writer, format, TarEntryType.RegularFile, literalConfig, "[general]\nguiApplications=true\n");
                PosixTarEntry binary = format == TarEntryFormat.Pax
                    ? new PaxTarEntry(TarEntryType.RegularFile, "usr/lib/fixture.so") : new GnuTarEntry(TarEntryType.RegularFile, "usr/lib/fixture.so");
                using var binaryData = new MemoryStream(new byte[] { 0, 255, 127, 0, 1, 254, 128 }, writable: false);
                binary.DataStream = binaryData; writer.WriteEntry(binary);
                Add(writer, format, TarEntryType.SymbolicLink, "bin", link: "usr/bin");
                Add(writer, format, TarEntryType.SymbolicLink, "usr/share/relative-link", link: "../bin/fixture");
                Add(writer, format, TarEntryType.HardLink, "usr/bin/fixture-hardlink", link: "usr/bin/fixture");
            });
            byte[] unchanged = SHA256.HashData(original);
            Dictionary<string, Snapshot> before = Inspect(original);
            byte[] prepared = Rewrite(original);
            Dictionary<string, Snapshot> after = Inspect(prepared);
            Check(after["."].Type == TarEntryType.Directory && after["."].Mode == before["."].Mode,
                "preserves the relative ./ archive root entry in " + format);
            Check(after.ContainsKey(escapedUnit) && !after.ContainsKey(escapedUnit.Replace('\\', '/')) &&
                after[escapedUnit].Digest == before[escapedUnit].Digest && after[escapedUnit].Mode == ExecutableMode,
                "preserves literal backslashes in systemd unit names, contents and Linux modes in " + format);
            Check(after.ContainsKey(literalConfig) && after[literalConfig].Text == "[general]\nguiApplications=true\n" &&
                after[literalConfig].Digest == before[literalConfig].Digest,
                "a literal etc-backslash-wsl.conf filename remains unrelated to the real per-distro configuration in " + format);
            Snapshot config = after["etc/wsl.conf"];
            Check(config.Text.Contains("[GeNeRaL]\n  GuiApplications = false # retained comment\n", StringComparison.Ordinal), "sets GUI support false while retaining section/key case and comments in " + format);
            foreach (string retained in new[] { "# retained", "[BOOT]\nsystemd=true", "[gpu]\nenabled=true\nappendLibPath=true", "generateResolvConf=false" })
                Check(config.Text.Contains(retained, StringComparison.Ordinal), "retains unrelated boot/GPU/network configuration in " + format);
            Snapshot distro = after["etc/wsl-distribution.conf"];
            foreach (string retained in new[] { "command=/usr/lib/wsl/wsl-setup", "defaultUid=1000", "icon=/usr/share/wsl/ubuntu.ico", "ProfileTemplate=/usr/share/wsl/terminal-profile.json", "[unrelated]\nenabled=true" })
                Check(distro.Text.Contains(retained, StringComparison.Ordinal), "retains distro metadata in " + format);
            Check(distro.Text.Contains("Enabled=false", StringComparison.Ordinal) && distro.Text.Contains("[WindowsTerminal]\nProfileTemplate=/usr/share/wsl/terminal-profile.json\nenabled=false", StringComparison.Ordinal), "hides shell and terminal integration before first boot in " + format);
            foreach (string name in before.Keys)
            {
                Snapshot old = before[name], current = after[name];
                Check(old.Format == current.Format && old.Type == current.Type && old.Mode == current.Mode && old.Uid == current.Uid &&
                    old.Gid == current.Gid && old.Modified == current.Modified && old.Link == current.Link,
                    "preserves Linux tar metadata for " + format + ": " + name);
                if (name is not ("etc/wsl.conf" or "etc/wsl-distribution.conf"))
                    Check(old.Digest == current.Digest, "preserves exact payload bytes for " + format + ": " + name);
            }
            if (format == TarEntryFormat.Pax)
                Check(after["usr/bin/fixture"].Attributes.TryGetValue("SCHILY.xattr.user.saywhat-fixture", out string? value) && value == "retained",
                    "preserves PAX extended attributes");
            Check(after["bin"].Link == "usr/bin" && after["usr/share/relative-link"].Link == "../bin/fixture" &&
                after["usr/bin/fixture-hardlink"].Type == TarEntryType.HardLink, "preserves relative symlinks and hard links without extracting them");
            Check(after["etc/saywhat-owner"].Text == Owner + "\n" && after["etc/saywhat-owner"].Uid == 0 &&
                after["etc/saywhat-owner"].Gid == 0, "records the exact component owner as root-owned metadata");
            Check(SHA256.HashData(original).SequenceEqual(unchanged), "does not change source archive bytes");
            Dictionary<string, Snapshot> repeated = Inspect(Rewrite(prepared));
            Check(repeated.Count == after.Count && repeated["etc/wsl.conf"].Text == config.Text &&
                repeated["etc/wsl-distribution.conf"].Text == distro.Text && repeated["etc/saywhat-owner"].Text == Owner + "\n",
                "same-owner repeat preparation is idempotent in " + format);
        }

        // These are the exact binary file-capability values in the pinned
        // release. Patch raw PAX bytes after writing an equal-length ASCII
        // placeholder: using the string API to create the fixture would itself
        // pre-corrupt the invalid UTF-8 and fail to exercise the original bug.
        foreach ((string path, string hex, bool losesBytes) in new[]
        {
            ("usr/bin/ping", "0100000200200000000000000000000000000000", false),
            ("usr/lib/snapd/snap-confine", "00000002cf002c01000000000000000000000000", true),
            ("usr/lib/x86_64-linux-gnu/gstreamer1.0/gstreamer-1.0/gst-ptp-helper", "0100000200148000000000000000000000000000", true)
        })
        {
            byte[] capability = Convert.FromHexString(hex);
            byte[] archive = RawCapabilityArchive(path, capability);
            byte[] sourceDigest = SHA256.HashData(archive);
            Snapshot original = Inspect(archive)[path];
            Check(Encoding.UTF8.GetBytes(original.Attributes[ManagedWslImage.CapabilityAttribute]).SequenceEqual(capability) != losesBytes,
                "raw binary PAX negative control demonstrates the string round-trip limitation for " + path);
            byte[] rewritten = Rewrite(archive);
            Snapshot prepared = Inspect(rewritten)[path];
            Check(!prepared.Attributes.ContainsKey(ManagedWslImage.CapabilityAttribute),
                "omits the exact executable capability attribute before importing " + path);
            Check(prepared.Attributes["SCHILY.xattr.user.saywhat-fixture"] == "retained 日本語" &&
                prepared.Attributes["SCHILY.xattr.security.capability.extra"] == "retained-near-match" &&
                prepared.Attributes["SCHILY.xattr.user.security.capability"] == "retained-user-attribute",
                "retains unrelated and similarly named PAX attributes for " + path);
            Check(prepared.Digest == original.Digest && prepared.Mode == original.Mode && prepared.Uid == original.Uid &&
                prepared.Gid == original.Gid && prepared.Modified == original.Modified && prepared.User == original.User &&
                prepared.Group == original.Group && prepared.Format == original.Format && prepared.Type == original.Type,
                "capability omission preserves payload, executable mode, ownership, names, timestamp and format for " + path);
            Check(SHA256.HashData(archive).SequenceEqual(sourceDigest), "capability preparation never changes its source archive");
            Snapshot repeated = Inspect(Rewrite(rewritten))[path];
            Check(repeated.Digest == prepared.Digest && repeated.Attributes.OrderBy(item => item.Key, StringComparer.Ordinal)
                .SequenceEqual(prepared.Attributes.OrderBy(item => item.Key, StringComparer.Ordinal)),
                "the capability policy remains idempotent for " + path);
        }

        byte[] missing = Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "usr/share/fixture", "unchanged"));
        var created = Inspect(Rewrite(missing));
        Check(created["etc"].Type == TarEntryType.Directory && created["etc/wsl.conf"].Text == "[general]\nguiApplications=false\n",
            "creates missing etc directory and per-distro GUI configuration");
        Check(created["etc/wsl-distribution.conf"].Text.Contains("[shortcut]\nenabled=false", StringComparison.Ordinal) &&
            created["etc/wsl-distribution.conf"].Text.Contains("[windowsterminal]\nenabled=false", StringComparison.Ordinal), "creates missing desktop integration metadata");
        byte[] duplicateSettings = Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "etc/wsl.conf",
            "[general]\nguiApplications=true\nGuiApplications=true\n[GENERAL]\nguiApplications=true\n"));
        Check(!Inspect(Rewrite(duplicateSettings))["etc/wsl.conf"].Text.Contains("=true", StringComparison.Ordinal), "no duplicate section/key can retain GUI support");
        byte[] noKey = Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "etc/wsl.conf", "[general]\n# retained\n[gpu]\nenabled=true"));
        Check(Inspect(Rewrite(noKey))["etc/wsl.conf"].Text.Contains("[general]\n# retained\nguiApplications=false\n[gpu]\nenabled=true", StringComparison.Ordinal), "inserts missing key in its own section");

        foreach (string name in new[] { "/etc/wsl.conf", "etc/../wsl.conf", "etc//wsl.conf", "etc/./wsl.conf",
            "./../escape", "./etc/../../escape", "../", "./etc/./wsl.conf", "/./", "//" })
            Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, name, "fixture"))), "refuses ambiguous entry path: " + name);
        foreach (string name in new[] { "etc/wsl.conf", "etc/wsl-distribution.conf", "etc/saywhat-owner" })
        {
            foreach (TarEntryType type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink, TarEntryType.Directory })
                Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, type, name, link: "usr/share/fixture"))), "refuses redirected configuration: " + name + "/" + type);
            Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer =>
            {
                Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, name, "");
                Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "./" + name, "");
            })), "refuses duplicate canonical configuration paths: " + name);
        }
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory,
            "usr/share/unexpected-capability", new Dictionary<string, string> { [ManagedWslImage.CapabilityAttribute] = "unexpected" })))),
            "rejects unexpected capability grants on non-file entries instead of changing their type or permissions");
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.SymbolicLink, "etc", link: "usr/etc"))), "refuses redirected etc ancestor");
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.SymbolicLink, ".", link: "elsewhere"))), "refuses redirected root ancestor");
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "etc/wsl.conf/child", "fixture"))), "refuses configuration directory descendants");
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "etc/saywhat-owner", new string('a', 32) + "\n"))), "refuses a foreign owner marker");
        Refuses(() => Rewrite(missing, "BAD-OWNER"), "rejects invalid component ownership before rewriting");
        Refuses(() => Rewrite(missing, Owner + "\n"), "rejects a newline after the owner token");
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "etc/wsl.conf", new string('a', ManagedWslImage.MaximumConfigBytes + 1)))), "bounds configuration memory");
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "etc/wsl.conf", "[general]garbage\nguiApplications=true\n"))), "rejects ambiguous configuration sections");
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, writer => Add(writer, TarEntryFormat.Pax, TarEntryType.RegularFile, "etc/wsl.conf", "[general]\nguiApplications=true\0\n"))), "rejects NUL-containing configuration");
        Refuses(() => Rewrite(Archive(TarEntryFormat.Pax, _ => { })), "refuses a config-only result from an empty archive");

        using (var cancelled = new CancellationTokenSource())
        using (var input = new MemoryStream(missing, writable: false))
        using (var output = new MemoryStream())
        {
            cancelled.Cancel();
            bool stopped = false;
            try { ManagedWslImage.RewriteTar(input, output, Owner, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && output.Length == 0 && input.Position == 0, "pre-cancelled rewriting reads and writes nothing");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var source = new MemoryStream(missing, writable: false))
        using (var input = new CancelOnReadStream(source, cancellation))
        using (var output = new MemoryStream())
        {
            bool stopped = false;
            try { ManagedWslImage.RewriteTar(input, output, Owner, cancellation.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && cancellation.IsCancellationRequested && source.ToArray().SequenceEqual(missing), "cancellation interrupts a streaming input without mutating its bytes");
        }
        string fixtureRoot = Path.Combine(directory, "managed-wsl-image");
        Directory.CreateDirectory(fixtureRoot);
        string badImage = Path.Combine(fixtureRoot, "unverified.wsl"), outputPath = Path.Combine(fixtureRoot, "prepared.tar");
        File.WriteAllBytes(badImage, missing);
        Refuses(() => ManagedWslImage.PrepareAsync(badImage, outputPath, Owner).GetAwaiter().GetResult(), "production preparation refuses an unverified archive");
        Check(!File.Exists(outputPath) && !Directory.EnumerateFiles(fixtureRoot, "*.partial-*").Any() && File.ReadAllBytes(badImage).SequenceEqual(missing),
            "failed verification leaves no import image or temporary image and preserves the source");
        Check(ManagedWslImage.HeadlessRevision == 2 && ManagedWslImage.UbuntuAsset.SizeBytes == 388_975_696 &&
            ManagedWslImage.UbuntuAsset.Sha256.Length == 64 && ManagedWslImage.UbuntuAsset.DownloadUrl.Contains("/24.04.5/", StringComparison.Ordinal),
            "production source pins a released image, exact size and digest");
        Check(ManagedWslImage.UncompressedTarBytes == 1_275_596_800 && ManagedWslImage.PrepareRequiredDiskBytes >= 2L * ManagedWslImage.UncompressedTarBytes,
            "temporary storage budget includes raw and prepared archives");
        return checks;
    }

    private static byte[] Archive(TarEntryFormat format, Action<TarWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, format, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }

    private static byte[] RawCapabilityArchive(string path, byte[] capability)
    {
        const string placeholder = "ABCDEFGHIJKLMNOPQRST";
        if (capability.Length != placeholder.Length) throw new ArgumentException("The binary capability fixture must retain its raw PAX length.");
        byte[] archive = Archive(TarEntryFormat.Pax, writer =>
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, path, new Dictionary<string, string>
            {
                [ManagedWslImage.CapabilityAttribute] = placeholder,
                ["SCHILY.xattr.user.saywhat-fixture"] = "retained 日本語",
                ["SCHILY.xattr.security.capability.extra"] = "retained-near-match",
                ["SCHILY.xattr.user.security.capability"] = "retained-user-attribute"
            })
            {
                Mode = ExecutableMode, Uid = 123, Gid = 456, UserName = "fixture-user", GroupName = "fixture-group", ModificationTime = Stamp
            };
            using var data = new MemoryStream(new byte[] { 0, 255, 127, 0, 254 }, writable: false);
            entry.DataStream = data;
            writer.WriteEntry(entry);
        });
        byte[] marker = Encoding.ASCII.GetBytes(ManagedWslImage.CapabilityAttribute + "=" + placeholder + "\n");
        int offset = archive.AsSpan().IndexOf(marker);
        if (offset < 0 || archive.AsSpan(offset + marker.Length).IndexOf(marker) >= 0)
            throw new InvalidDataException("The raw binary capability fixture has no unique PAX record.");
        capability.AsSpan().CopyTo(archive.AsSpan(offset + ManagedWslImage.CapabilityAttribute.Length + 1));
        return archive;
    }

    private static void Add(TarWriter writer, TarEntryFormat format, TarEntryType type, string name, string? content = null,
        string? link = null, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
    {
        PosixTarEntry entry = format == TarEntryFormat.Pax
            ? new PaxTarEntry(type, name, new Dictionary<string, string> { ["SCHILY.xattr.user.saywhat-fixture"] = "retained" })
            : new GnuTarEntry(type, name);
        entry.Uid = 123; entry.Gid = 456; entry.UserName = "fixture-user"; entry.GroupName = "fixture-group";
        entry.Mode = mode; entry.ModificationTime = Stamp;
        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = link!;
        using var data = content is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        if (data is not null) entry.DataStream = data;
        writer.WriteEntry(entry);
    }

    private sealed record Snapshot(TarEntryFormat Format, TarEntryType Type, UnixFileMode Mode, int Uid, int Gid,
        DateTimeOffset Modified, string Link, string Text, string Digest, IReadOnlyDictionary<string, string> Attributes,
        string User, string Group);

    private static Dictionary<string, Snapshot> Inspect(byte[] archive)
    {
        var result = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        using var stream = new MemoryStream(archive, writable: false);
        using var reader = new TarReader(stream);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: true)) is not null)
        {
            string name = entry.Name;
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            name = name.TrimEnd('/');
            if (name.Length == 0) name = ".";
            using var data = new MemoryStream();
            entry.DataStream?.CopyTo(data);
            byte[] bytes = data.ToArray();
            result.Add(name, new(entry.Format, entry.EntryType, entry.Mode, entry.Uid, entry.Gid, entry.ModificationTime,
                entry.LinkName, Encoding.UTF8.GetString(bytes), Convert.ToHexString(SHA256.HashData(bytes)),
                entry is PaxTarEntry pax ? pax.ExtendedAttributes : new Dictionary<string, string>(),
                (entry as PosixTarEntry)?.UserName ?? "", (entry as PosixTarEntry)?.GroupName ?? ""));
        }
        return result;
    }

    private sealed class CancelOnReadStream(Stream source, CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, Math.Min(count, 512));
            cancellation.Cancel();
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

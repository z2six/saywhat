using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IncomingTranslator;

// Pure local fixtures: no sockets, registry reads, real installers, app shutdown,
// models, microphone/playback capture, or files outside the supplied test root.
internal static class AppUpdateTests
{
    internal static async Task<int> RunOfflineChecksAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        int checks = 0;
        void Check(bool condition, string description)
        { if (!condition) throw new InvalidOperationException("Update check: " + description); checks++; }
        async Task Reject(Func<Task> action, string description, Type? expected = null)
        {
            Exception? failure = null;
            try { await action().ConfigureAwait(false); } catch (Exception error) { failure = error; }
            Check(failure is not null && (expected is null || expected.IsInstanceOfType(failure)), description);
        }
        var executable = new byte[10000];
        for (int index = 0; index < executable.Length; index++) executable[index] = (byte)(index % 251);
        executable[0] = (byte)'M'; executable[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(executable.AsSpan(60, 4), 128);
        executable[128] = (byte)'P'; executable[129] = (byte)'E'; executable[130] = 0; executable[131] = 0;
        string sha = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        string Manifest(string hash, long bytes) => JsonSerializer.Serialize(new
        {
            schemaVersion = 1, ready = true, version = "0.1.15", channel = "preview", sourceLicense = "GPL-3.0-only",
            releaseUrl = AppUpdateChecker.ReleasePrefix + "tag/v0.1.15",
            installer = new { url = AppUpdateChecker.ReleasePrefix + "download/v0.1.15/SayWhat-0.1.15-Setup.exe", sha256 = hash, sizeBytes = bytes },
            source = new { url = AppUpdateChecker.ReleasePrefix + "download/v0.1.15/SayWhat-Corresponding-Source.zip", sha256 = sha },
            distribution = new { url = AppUpdateChecker.ReleasePrefix + "download/v0.1.15/SayWhat-0.1.15-Complete.zip", sha256 = sha }
        });
        string valid = Manifest(sha, executable.Length);
        var info = AppUpdateChecker.Parse(valid, "0.1.14");
        Check(info is { Ready: true, IsNewer: true, Installer.SizeBytes: 10000 }, "ready manifest retains exact installer metadata");
        foreach (string invalid in new[]
        {
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":null"),
            valid.Replace("\"version\":\"0.1.15\"", "\"version\":17"),
            valid.Replace("0.1.15", "00.1.15"),
            valid.Replace("\"sizeBytes\":10000", "\"sizeBytes\":\"10000\""),
            valid.Replace("\"sizeBytes\":10000", "\"sizeBytes\":9999999999"),
            valid.Replace("GPL-3.0-only", "unknown"), valid.Replace("\"preview\"", "\"stable\""),
            valid.Replace("/tag/v0.1.15", "/tag/v0.1.14"),
            valid.Replace("/download/v0.1.15/SayWhat-Corresponding", "/download/v0.1.14/SayWhat-Corresponding"),
            valid.Replace("/SayWhat-0.1.15-Complete.zip", "/../complete.zip"),
            valid.Replace("/SayWhat-0.1.15-Complete.zip", "/folder%2Fcomplete.zip"),
            valid.Replace("/SayWhat-0.1.15-Complete.zip", "/complete.exe"),
            valid.Replace("/SayWhat-0.1.15-Complete.zip", "/complete.zip?token=private"),
            "[]", "null"
        }) await Reject(() => { _ = AppUpdateChecker.Parse(invalid, "0.1.14"); return Task.CompletedTask; }, "malformed metadata rejected", typeof(InvalidDataException));

        var metadata = new FakeHandler((request, _) =>
        {
            Check(request.RequestUri?.AbsoluteUri == AppUpdateChecker.ManifestUrl, "metadata only contacts its fixed origin");
            Check(!request.Headers.Contains("Cookie"), "metadata does not add cookies");
            return Task.FromResult(Bytes(Encoding.UTF8.GetBytes(valid)));
        });
        var checkedInfo = await AppUpdateChecker.CheckAsync(metadata, "0.1.14", CancellationToken.None).ConfigureAwait(false);
        Check(checkedInfo.Installer?.Sha256 == sha && metadata.Requests == 1, "metadata-only path parses without downloading installer");
        await Reject(() => AppUpdateChecker.CheckAsync(new FakeHandler((_, _) => Task.FromResult(Bytes(new byte[32769], knownLength: false))), "0.1.14", CancellationToken.None), "unknown-length metadata body is capped", typeof(InvalidDataException));
        await Reject(() => AppUpdateChecker.CheckAsync(new FakeHandler((_, _) => Task.FromResult(Bytes(new byte[] { 0xc3, 0x28 }))), "0.1.14", CancellationToken.None), "malformed UTF-8 rejected", typeof(InvalidDataException));
        var redirectMetadata = new FakeHandler((_, _) => Task.FromResult(Redirect("https://example.invalid/release.json")));
        await Reject(() => AppUpdateChecker.CheckAsync(redirectMetadata, "0.1.14", CancellationToken.None), "metadata redirects rejected", typeof(IOException));
        Check(redirectMetadata.Requests == 1, "metadata redirect never contacts second origin");
        await Reject(() => AppUpdateChecker.CheckAsync(new FakeHandler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return Bytes([]); }), "0.1.14", CancellationToken.None, TimeSpan.FromMilliseconds(40)), "metadata header deadline", typeof(TimeoutException));
        await Reject(() => AppUpdateChecker.CheckAsync(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) })), "0.1.14", CancellationToken.None, TimeSpan.FromMilliseconds(40)), "metadata complete body deadline", typeof(TimeoutException));
        await Reject(() => AppUpdateChecker.CheckAsync(new FakeHandler(async (_, token) =>
        {
            await Task.Delay(180, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DelayedStream(Encoding.UTF8.GetBytes(valid), 180)) };
        }), "0.1.14", CancellationToken.None, TimeSpan.FromMilliseconds(250)), "one metadata deadline includes headers and body together", typeof(TimeoutException));
        try
        {
            await AppUpdateChecker.CheckAsync(new FakeHandler((_, _) => throw new HttpRequestException("https://example.invalid/?token=private-test")), "0.1.14", CancellationToken.None).ConfigureAwait(false);
            Check(false, "network failure must fail");
        }
        catch (IOException error)
        { Check(!error.ToString().Contains("private-test", StringComparison.Ordinal), "metadata errors never retain raw signed request details"); }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Reject(() => AppUpdateChecker.CheckAsync(new FakeHandler((_, token) =>
            { token.ThrowIfCancellationRequested(); return Task.FromResult(Bytes([])); }), "0.1.14", cancelled.Token), "metadata caller cancellation remains cancellation", typeof(OperationCanceledException));
        }

        string Root(string name) => Path.Combine(directory, name);
        void EmptyStages(string root) => Check(!Directory.Exists(Path.Combine(root, "updates")) ||
            Directory.GetFileSystemEntries(Path.Combine(root, "updates")).Length == 0, "failed/cancelled stage removed");
        var progress = new RecordingProgress();
        foreach (var unavailable in new[] { info with { IsNewer = false }, info with { Ready = false }, info with { Installer = null } })
        {
            string root = Root("unavailable-" + checks);
            var handler = new FakeHandler((_, _) => throw new InvalidOperationException("should not contact network"));
            await Reject(async () => { using var _ = await AppUpdateInstallerService.DownloadAsync(unavailable, null, CancellationToken.None, handler, root).ConfigureAwait(false); },
                "unavailable update cannot download or stage", typeof(InvalidDataException));
            Check(handler.Requests == 0 && !Directory.Exists(root), "unavailable update does not mutate app data");
        }
        string successfulRoot = Root("successful");
        var successfulHandler = new FakeHandler((_, _) => Task.FromResult(Bytes(executable, knownLength: false)));
        string staged;
        using (var download = await AppUpdateInstallerService.DownloadAsync(info, progress, CancellationToken.None, successfulHandler, successfulRoot).ConfigureAwait(false))
        {
            staged = download.FilePath;
            Check(File.Exists(staged) && new FileInfo(staged).Length == executable.Length, "verified installer staged only in owned updates directory");
            Check(Path.GetDirectoryName(download.DirectoryPath) == Path.Combine(successfulRoot, "updates"), "stage stays inside app root");
            Check(progress.Items[0].Stage == "Downloading update" && progress.Items[^1].Stage == "Update ready" &&
                progress.Items.Any(p => p.Stage == "Verifying update"), "honest download and verification stages");
            Check(progress.Items.All(p => p.ReceivedBytes >= 0 && p.ReceivedBytes <= executable.Length && p.TotalBytes == executable.Length), "progress uses actual bounded bytes");
            bool writeDenied = false;
            try { using var writer = File.Open(staged, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { writeDenied = true; }
            Check(writeDenied, "verified lease denies installer replacement before handoff");
        }
        Check(!File.Exists(staged), "abandoned verified installer cleaned"); EmptyStages(successfulRoot);
        using (var callbackFailure = await AppUpdateInstallerService.DownloadAsync(info, new ThrowingProgress(), CancellationToken.None,
            new FakeHandler((_, _) => Task.FromResult(Bytes(executable))), Root("progress-fault")).ConfigureAwait(false))
            Check(File.Exists(callbackFailure.FilePath), "faulty progress callback cannot compromise verification");
        EmptyStages(Root("progress-fault"));

        foreach (var fixture in new[]
        {
            (Name: "checksum", Info: AppUpdateChecker.Parse(Manifest(new string('a', 64), executable.Length), "0.1.14"), Data: executable, Known: false),
            (Name: "short", Info: info, Data: executable[..^1], Known: false),
            (Name: "large", Info: info, Data: executable.Concat(new byte[] { 0 }).ToArray(), Known: false),
            (Name: "declared-short", Info: info, Data: executable[..^1], Known: true),
            (Name: "header", Info: AppUpdateChecker.Parse(Manifest(Convert.ToHexString(SHA256.HashData(new byte[10000])), 10000), "0.1.14"), Data: new byte[10000], Known: false)
        })
        {
            string root = Root(fixture.Name);
            await Reject(async () => { using var _ = await AppUpdateInstallerService.DownloadAsync(fixture.Info, null, CancellationToken.None,
                new FakeHandler((_, _) => Task.FromResult(Bytes(fixture.Data, fixture.Known))), root).ConfigureAwait(false); }, "invalid download never yields launch capability", typeof(InvalidDataException));
            EmptyStages(root);
        }

        string signed = "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?token=private-test";
        var redirected = new FakeHandler((request, _) => Task.FromResult(request.RequestUri?.Host == "github.com" ? Redirect(signed) : Bytes(executable)));
        using (var download = await AppUpdateInstallerService.DownloadAsync(info, null, CancellationToken.None, redirected, Root("allowed-redirect")).ConfigureAwait(false))
            Check(redirected.Requests == 2 && File.Exists(download.FilePath), "approved HTTPS asset redirect succeeds");
        foreach (string forbidden in new[]
        {
            "http://release-assets.githubusercontent.com/github-production-release-asset/1/2",
            "https://release-assets.githubusercontent.com.evil.invalid/github-production-release-asset/1/2",
            "https://release-assets.githubusercontent.com:8443/github-production-release-asset/1/2",
            "https://user:password@release-assets.githubusercontent.com/github-production-release-asset/1/2",
            "https://release-assets.githubusercontent.com/not-a-release/2",
            "https://github.com/someone/other/releases/download/v0.1.15/update.exe",
            "https://127.0.0.1:1234/update.exe", "file:///C:/unrelated.exe"
        })
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(forbidden)));
            string root = Root("forbidden-" + checks);
            await Reject(async () => { using var _ = await AppUpdateInstallerService.DownloadAsync(info, null, CancellationToken.None, handler, root).ConfigureAwait(false); }, "unapproved redirect rejected before request", typeof(InvalidDataException));
            Check(handler.Requests == 1, "unapproved redirect is not contacted"); EmptyStages(root);
        }
        var redirectLoop = new FakeHandler((_, _) => Task.FromResult(Redirect(info.Installer!.Url.AbsoluteUri)));
        await Reject(async () => { using var _ = await AppUpdateInstallerService.DownloadAsync(info, null, CancellationToken.None, redirectLoop, Root("loop")).ConfigureAwait(false); }, "redirect loop bounded", typeof(InvalidDataException));
        Check(redirectLoop.Requests == AppUpdateInstallerService.MaximumRedirects + 1, "redirect request count bounded"); EmptyStages(Root("loop"));

        foreach (bool callerCancellation in new[] { false, true })
        {
            string root = Root(callerCancellation ? "cancelled" : "stalled");
            using var cancelled = new CancellationTokenSource();
            if (callerCancellation) cancelled.CancelAfter(40);
            await Reject(async () => { using var _ = await AppUpdateInstallerService.DownloadAsync(info, null, cancelled.Token,
                new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) })),
                root, TimeSpan.FromMilliseconds(callerCancellation ? 2000 : 40), TimeSpan.FromSeconds(2)).ConfigureAwait(false); },
                "download deadline/cancellation bounded", callerCancellation ? typeof(OperationCanceledException) : typeof(TimeoutException));
            EmptyStages(root);
        }
        foreach (bool stallHeaders in new[] { false, true })
        {
            string root = Root(stallHeaders ? "idle-headers" : "idle-body");
            var handler = new FakeHandler(async (_, token) =>
            {
                if (stallHeaders) await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
            });
            await Reject(async () => { using var _ = await AppUpdateInstallerService.DownloadAsync(info, null, CancellationToken.None, handler,
                root, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(40)).ConfigureAwait(false); }, "download inactivity bounded at both headers and body", typeof(TimeoutException));
            EmptyStages(root);
        }

        string current = Path.Combine(directory, "SayWhat.exe"); await File.WriteAllBytesAsync(current, executable).ConfigureAwait(false);
        var currentRecords = new[] { new AppUpdateRegistration(AppUpdateInstallScope.CurrentUser, directory, "0.1.14", "uninstaller") };
        Check(AppUpdateInstallerService.ResolveInstallScope(currentRecords, current) == AppUpdateInstallScope.CurrentUser, "registered current-user scope preserved");
        Check(AppUpdateInstallerService.ResolveInstallScope([new(AppUpdateInstallScope.AllUsers, directory, "0.1.14", "uninstaller")], current) == AppUpdateInstallScope.AllUsers, "registered all-users scope preserved");
        foreach (var records in new IReadOnlyList<AppUpdateRegistration>[]
        {
            [], [new(AppUpdateInstallScope.CurrentUser, Root("other"), "0.1.14", "uninstaller")],
            [new((AppUpdateInstallScope)99, directory, "0.1.14", "uninstaller")],
            [..currentRecords, new(AppUpdateInstallScope.AllUsers, directory, "0.1.14", "uninstaller")],
            [..currentRecords, new(AppUpdateInstallScope.CurrentUser, directory, "0.1.13", "uninstaller")],
            [..currentRecords, new(AppUpdateInstallScope.CurrentUser, directory, "0.1.14", "different-uninstaller")]
        }) await Reject(() => { _ = AppUpdateInstallerService.ResolveInstallScope(records, current); return Task.CompletedTask; }, "ambiguous/missing/different installation refused", typeof(IOException));
        foreach (AppUpdateInstallScope scope in new[] { AppUpdateInstallScope.CurrentUser, AppUpdateInstallScope.AllUsers })
        foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
        {
            string root = Root("launch-" + scope + "-" + language), launchedPath;
            ProcessStartInfo? start = null;
            using (var download = await AppUpdateInstallerService.DownloadAsync(info, null, CancellationToken.None,
                new FakeHandler((_, _) => Task.FromResult(Bytes(executable))), root).ConfigureAwait(false))
            {
                launchedPath = download.FilePath;
                AppUpdateInstallerService.LaunchInstaller(download, language, current, value => start = value, _ => scope);
                string scopeFlag = scope == AppUpdateInstallScope.AllUsers ? "/ALLUSERS" : "/CURRENTUSER";
                Check(start?.FileName == launchedPath && start.UseShellExecute &&
                    start.Arguments.Contains("/AUTOUPDATE /SILENT /NORESTART " + scopeFlag, StringComparison.Ordinal),
                    "only the verified update is opened with native visible progress and its registered scope");
                Check(start!.Arguments.Contains("/LANG=" + (language == "zh-CN" ? "zh_CN" : language), StringComparison.Ordinal) &&
                    start.Arguments.Contains("/SAYWHATEXE=\"" + current + "\"", StringComparison.Ordinal), "installer language and exact identity passed");
                Check(start.Arguments.Contains("/SP-", StringComparison.Ordinal) &&
                    start.Arguments.Contains("/DIR=\"" + directory + "\"", StringComparison.Ordinal) &&
                    start.Arguments.EndsWith("/SAYWHATTO=" + info.Version, StringComparison.Ordinal),
                    "automatic update skips the startup prompt and pins its existing directory and verified target version");
                Check(!start.Arguments.Contains("/VERYSILENT", StringComparison.OrdinalIgnoreCase) &&
                    !start.Arguments.Contains("/SUPPRESSMSGBOXES", StringComparison.OrdinalIgnoreCase) &&
                    !start.Arguments.Contains("/FORCECLOSEAPPLICATIONS", StringComparison.OrdinalIgnoreCase) &&
                    !start.Arguments.Contains("/NOCANCEL", StringComparison.OrdinalIgnoreCase),
                    "streamlined update preserves visible progress, errors, cancellation and safe owned-app shutdown");
                Check(!start.Arguments.Contains(scope == AppUpdateInstallScope.AllUsers ? "/CURRENTUSER" : "/ALLUSERS", StringComparison.Ordinal),
                    "verified update never changes the registered install scope");
                await Reject(() => { AppUpdateInstallerService.LaunchInstaller(download, language, current, _ => throw new InvalidOperationException("should not call"), _ => scope); return Task.CompletedTask; }, "verified handoff can only launch once", typeof(InvalidOperationException));
            }
            Check(File.Exists(launchedPath), "launch disposal does not delete installer while setup is reading");
            AppUpdateInstallerService.RemoveOwnedStage(Path.GetDirectoryName(launchedPath)!, launchedPath); EmptyStages(root);
        }
        string failedLaunchRoot = Root("failed-launch");
        using (var download = await AppUpdateInstallerService.DownloadAsync(info, null, CancellationToken.None,
            new FakeHandler((_, _) => Task.FromResult(Bytes(executable))), failedLaunchRoot).ConfigureAwait(false))
        {
            await Reject(() => { AppUpdateInstallerService.LaunchInstaller(download, "en", current, _ => throw new IOException("synthetic launcher failure"), _ => AppUpdateInstallScope.CurrentUser); return Task.CompletedTask; }, "launch failure retains no successful handoff", typeof(IOException));
            await Reject(() => { AppUpdateInstallerService.LaunchInstaller(download, "en", current, _ => throw new Win32Exception(1223), _ => AppUpdateInstallScope.CurrentUser); return Task.CompletedTask; },
                "Windows permission refusal is cancellation without successful handoff", typeof(OperationCanceledException));
            await Reject(() => { AppUpdateInstallerService.LaunchInstaller(download, "unknown", current, _ => throw new Exception("should not call"), _ => AppUpdateInstallScope.CurrentUser); return Task.CompletedTask; }, "unsupported installer language rejected", typeof(ArgumentException));
            bool unverifiedLaunch = false;
            foreach (string target in new[] { "SayWhat.exe", Path.Combine(directory, "Unknown.exe"), Path.Combine(directory, "missing", "SayWhat.exe") })
                await Reject(() => { AppUpdateInstallerService.LaunchInstaller(download, "en", target, _ => unverifiedLaunch = true, _ => AppUpdateInstallScope.CurrentUser); return Task.CompletedTask; },
                    "relative, unknown or missing installation cannot launch setup", typeof(IOException));
            await Reject(() => { AppUpdateInstallerService.LaunchInstaller(download, "en", current, _ => unverifiedLaunch = true, _ => (AppUpdateInstallScope)99); return Task.CompletedTask; },
                "unknown install scope cannot launch setup", typeof(IOException));
            Check(!unverifiedLaunch, "invalid handoff never reaches Process.Start");
        }
        EmptyStages(failedLaunchRoot);
        return checks;
    }

    private static HttpResponseMessage Bytes(byte[] data, bool knownLength = true) => new(HttpStatusCode.OK)
    { Content = knownLength ? new ByteArrayContent(data) : new StreamContent(new SegmentedStream(data)) };
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return response;
    }
    private sealed class RecordingProgress : IProgress<AppUpdateProgress>
    { internal List<AppUpdateProgress> Items { get; } = []; public void Report(AppUpdateProgress value) => Items.Add(value); }
    private sealed class ThrowingProgress : IProgress<AppUpdateProgress>
    { public void Report(AppUpdateProgress value) => throw new InvalidOperationException("synthetic callback failure"); }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; token.ThrowIfCancellationRequested(); return reply(request, token); }
    }
    private class SegmentedStream(byte[] bytes) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            count = Math.Min(Math.Min(count, 1024), bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer.AsSpan(offset)); _position += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); int count = Math.Min(Math.Min(buffer.Length, 1024), bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer.Span); _position += count; return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class StalledStream() : SegmentedStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return 0; }
    }
    private sealed class DelayedStream(byte[] bytes, int delay) : SegmentedStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(delay, token).ConfigureAwait(false); return await base.ReadAsync(buffer, token).ConfigureAwait(false); }
    }
}

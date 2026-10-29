using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace IncomingTranslator;

internal sealed record AppUpdateProgress(string Stage, long ReceivedBytes, long TotalBytes);
internal enum AppUpdateInstallScope { CurrentUser, AllUsers }
internal sealed record AppUpdateRegistration(AppUpdateInstallScope Scope, string Directory, string Version, string Uninstaller);

// An opaque capability: it can only be produced after the complete download,
// size, checksum and executable-header checks. A read lease denies writes and
// deletion on Windows until setup is launched or the operation is abandoned.
internal sealed class AppUpdateDownload : IDisposable
{
    private FileStream? _lease;
    private bool _launched;
    internal string FilePath { get; }
    internal string DirectoryPath { get; }
    public string Version { get; }
    internal AppUpdateDownload(string filePath, string directoryPath, string version, FileStream lease)
    { FilePath = filePath; DirectoryPath = directoryPath; Version = version; _lease = lease; }

    internal void Launch(Action<ProcessStartInfo> launch, ProcessStartInfo start)
    {
        lock (this)
        {
            if (_lease is null || _launched) throw new InvalidOperationException("The verified update is no longer available.");
            launch(start);
            _launched = true;
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            _lease?.Dispose(); _lease = null;
            // Setup can still be reading the executable after Process.Start.
            // Retain launched downloads; cancelled/failed handoffs are removed.
            if (!_launched) AppUpdateInstallerService.RemoveOwnedStage(DirectoryPath, FilePath);
        }
    }
}

internal static class AppUpdateInstallerService
{
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(30);
    internal const int MaximumRedirects = 5;

    public static Task<AppUpdateDownload> DownloadAsync(AppUpdateInfo info,
        IProgress<AppUpdateProgress>? progress, CancellationToken token) =>
        DownloadAsync(info, progress, token, AppUpdateChecker.CreateHandler(), AppPaths.Root);

    // Tests inject an in-memory HTTP handler and an isolated app root. This
    // overload never executes an installer; launch is a separate explicit step.
    internal static async Task<AppUpdateDownload> DownloadAsync(AppUpdateInfo info,
        IProgress<AppUpdateProgress>? progress, CancellationToken token, HttpMessageHandler handler,
        string appRoot, TimeSpan? downloadTimeout = null, TimeSpan? inactivityTimeout = null)
    {
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        AppUpdateChecker.ValidateInstaller(info);
        var asset = info.Installer!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(downloadTimeout ?? DownloadTimeout);
        TimeSpan idle = inactivityTimeout ?? InactivityTimeout;
        string root = Path.GetFullPath(appRoot), updates = Path.Combine(root, "updates");
        string stage = Path.Combine(updates, "update-" + info.Version + "-" + Guid.NewGuid().ToString("N"));
        string filename = "SayWhat-" + info.Version + "-Setup.exe";
        string temporary = Path.Combine(stage, filename + ".partial"), destination = Path.Combine(stage, filename);
        FileStream? lease = null;
        bool createdStage = false, completed = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            PrepareStage(root, updates, stage);
            createdStage = true;
            Report(progress, "Downloading update", 0, asset.SizeBytes);
            using var response = await OpenDownloadAsync(http, asset.Url, deadline.Token, idle).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long declared && declared != asset.SizeBytes)
                throw new InvalidDataException("The update download size does not match its release information.");
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[65536];
                long received = 0;
                var lastProgress = Stopwatch.StartNew();
                while (true)
                {
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    readDeadline.CancelAfter(idle);
                    int count = await input.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (received + count > asset.SizeBytes)
                        throw new InvalidDataException("The update download is larger than its release information.");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    received += count;
                    if (lastProgress.ElapsedMilliseconds >= 150 || received == asset.SizeBytes)
                    { Report(progress, "Downloading update", received, asset.SizeBytes); lastProgress.Restart(); }
                }
                if (received != asset.SizeBytes)
                    throw new InvalidDataException("The update download ended before all bytes arrived.");
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            deadline.Token.ThrowIfCancellationRequested();
            Report(progress, "Verifying update", asset.SizeBytes, asset.SizeBytes);
            EnsureOrdinaryDirectory(root); EnsureOrdinaryDirectory(updates); EnsureOrdinaryDirectory(stage);
            if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update staging file is not a regular file.");
            // Never overwrite a pre-existing executable, even inside the cache.
            File.Move(temporary, destination, overwrite: false);
            lease = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (lease.Length != asset.SizeBytes) throw new InvalidDataException("The staged update size changed.");
            byte[] actual = await SHA256.HashDataAsync(lease, deadline.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(asset.Sha256)))
                throw new InvalidDataException("The update checksum did not match. The installer was not opened.");
            await VerifyExecutableHeaderAsync(lease, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var verified = new AppUpdateDownload(destination, stage, info.Version, lease);
            lease = null;
            completed = true;
            Report(progress, "Update ready", asset.SizeBytes, asset.SizeBytes);
            return verified;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The update download stopped making progress or took too long. Try again later."); }
        catch (HttpRequestException)
        { throw new IOException("Couldn't download the update. Check your connection and try again."); }
        finally
        {
            lease?.Dispose();
            if (createdStage && !completed) RemoveOwnedStage(stage, temporary, destination);
        }
    }

    private static async Task<HttpResponseMessage> OpenDownloadAsync(HttpClient http, Uri installer,
        CancellationToken token, TimeSpan inactivity)
    {
        Uri current = installer;
        for (int hop = 0; hop <= MaximumRedirects; hop++)
        {
            if (!IsAllowedDownloadUri(current, installer)) throw new InvalidDataException("The update redirected to an unapproved download location.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersDeadline.CancelAfter(inactivity);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null || hop == MaximumRedirects)
                    throw new InvalidDataException("The update download has an invalid redirect chain.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            { response.Dispose(); throw new IOException("Couldn't retrieve the update installer."); }
            return response;
        }
        throw new InvalidDataException("The update download has too many redirects.");
    }

    internal static bool IsAllowedDownloadUri(Uri uri, Uri installer) => uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath == installer.AbsolutePath && uri.Query.Length == 0
            : uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
              uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal));

    private static void PrepareStage(string root, string updates, string stage)
    {
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || root.Contains('"') ||
            !stage.StartsWith(updates + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Updates require a local app data folder.");
        Directory.CreateDirectory(root); EnsureOrdinaryDirectory(root);
        Directory.CreateDirectory(updates); EnsureOrdinaryDirectory(updates);
        if (Directory.Exists(stage)) throw new IOException("The update staging folder already exists.");
        Directory.CreateDirectory(stage); EnsureOrdinaryDirectory(stage);
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The update folder is not a regular local directory.");
    }

    private static async Task VerifyExecutableHeaderAsync(FileStream input, CancellationToken token)
    {
        if (input.Length < 68) throw new InvalidDataException("The downloaded update is not a Windows installer.");
        input.Position = 0;
        var dos = new byte[64]; await input.ReadExactlyAsync(dos, token).ConfigureAwait(false);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(60, 4));
        if (dos[0] != 'M' || dos[1] != 'Z' || offset < 64 || offset > input.Length - 4)
            throw new InvalidDataException("The downloaded update is not a Windows installer.");
        input.Position = offset;
        var pe = new byte[4]; await input.ReadExactlyAsync(pe, token).ConfigureAwait(false);
        if (!pe.AsSpan().SequenceEqual(new byte[] { (byte)'P', (byte)'E', 0, 0 }))
            throw new InvalidDataException("The downloaded update is not a Windows installer.");
        input.Position = 0;
    }

    public static void LaunchInstaller(AppUpdateDownload download, string uiLanguage, string currentExecutablePath) =>
        LaunchInstaller(download, uiLanguage, currentExecutablePath, start =>
        {
            using var process = Process.Start(start) ?? throw new IOException("The update installer could not be opened.");
        });

    internal static void LaunchInstaller(AppUpdateDownload download, string uiLanguage, string currentExecutablePath,
        Action<ProcessStartInfo> launcher, Func<string, AppUpdateInstallScope>? scopeReader = null)
    {
        if (!Path.IsPathFullyQualified(currentExecutablePath))
            throw new IOException("The current SayWhat installation could not be identified.");
        string executable = Path.GetFullPath(currentExecutablePath);
        string language = uiLanguage switch { "en" => "en", "zh-CN" => "zh_CN", "ja" => "ja", "ko" => "ko",
            _ => throw new ArgumentException("Unsupported installer language.", nameof(uiLanguage)) };
        if (!Path.GetFileName(executable).Equals("SayWhat.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable) || executable.StartsWith("\\\\", StringComparison.Ordinal) ||
            executable.Any(c => c == '"' || char.IsControl(c)))
            throw new IOException("The current SayWhat installation could not be identified.");
        EnsureOrdinaryDirectory(download.DirectoryPath);
        AppUpdateInstallScope scope = (scopeReader ?? ReadInstallScope)(executable);
        if (scope is not (AppUpdateInstallScope.CurrentUser or AppUpdateInstallScope.AllUsers))
            throw new IOException("The registered SayWhat install scope could not be verified.");
        if (!AppUpdateChecker.IsVersion(download.Version))
            throw new InvalidDataException("The verified update version is invalid.");
        var start = new ProcessStartInfo(download.FilePath)
        {
            UseShellExecute = true, WorkingDirectory = download.DirectoryPath,
            Arguments = "/AUTOUPDATE /SILENT /NORESTART " + (scope == AppUpdateInstallScope.AllUsers ? "/ALLUSERS" : "/CURRENTUSER") +
                " /SP- /LANG=" + language + " /DIR=\"" + Path.GetDirectoryName(executable) + "\"" +
                " /SAYWHATEXE=\"" + executable + "\" /SAYWHATTO=" + download.Version
        };
        // /SILENT is native compact progress, not an unattended hidden install.
        // /LANG, /SP- and the registered scope avoid redundant choice dialogs;
        // /DIR and /SAYWHATTO pin this handoff to its exact path and target build.
        // Keep the write-denying lease through Process.Start. The installer owns
        // graceful shutdown only after UAC is accepted, verified scope, progress and
        // normal UAC/errors. No force-close, hidden install, suppressed errors or
        // automatic Windows restart. Only its verified update path reopens idle UI.
        try { download.Launch(launcher, start); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            // Windows declined the launch. The app/engines have not been asked
            // to close, and disposing the unlaunched capability removes staging.
            throw new OperationCanceledException("The Windows permission request was cancelled. Translation was not stopped.", error);
        }
    }

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AB8C1B91-45FC-4F45-B457-420D736752D8}_is1";
    private static AppUpdateInstallScope ReadInstallScope(string executable)
    {
        var records = new List<AppUpdateRegistration>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(UninstallKey, writable: false);
            if (key is null) continue;
            string directory = key.GetValue("Inno Setup: App Path") as string ?? "";
            if (directory.Length == 0) directory = key.GetValue("InstallLocation") as string ?? "";
            records.Add(new(hive == RegistryHive.LocalMachine ? AppUpdateInstallScope.AllUsers : AppUpdateInstallScope.CurrentUser,
                directory, key.GetValue("DisplayVersion") as string ?? "", key.GetValue("UninstallString") as string ?? ""));
        }
        return ResolveInstallScope(records, executable);
    }

    internal static AppUpdateInstallScope ResolveInstallScope(IReadOnlyList<AppUpdateRegistration> records, string executable)
    {
        if (records.Count == 0 || records.Select(r => r.Scope).Distinct().Count() != 1 ||
            records.Any(r => r.Scope is not (AppUpdateInstallScope.CurrentUser or AppUpdateInstallScope.AllUsers)))
            throw new IOException("A single registered SayWhat installation could not be verified. Open the release page to run setup manually.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        string? version = null, uninstaller = null;
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Directory) || !Path.IsPathFullyQualified(record.Directory) ||
                record.Directory.StartsWith("\\\\", StringComparison.Ordinal) || record.Directory.Any(c => c == '"' || char.IsControl(c)) ||
                !Path.GetFullPath(record.Directory).TrimEnd(Path.DirectorySeparatorChar).Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                (version is not null && record.Version.Length > 0 && record.Version != version) ||
                (uninstaller is not null && record.Uninstaller.Length > 0 && !record.Uninstaller.Trim().Equals(uninstaller, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The registered SayWhat installation is inconsistent. Open the release page to run setup manually.");
            if (record.Version.Length > 0) version = record.Version;
            if (record.Uninstaller.Length > 0) uninstaller = record.Uninstaller.Trim();
        }
        return records[0].Scope;
    }

    private static void Report(IProgress<AppUpdateProgress>? progress, string stage, long received, long total)
    { try { progress?.Report(new(stage, received, total)); } catch { /* UI callback failures never bypass verification. */ } }

    internal static void RemoveOwnedStage(string directory, params string[] files)
    {
        // Exact paths in a newly allocated staging directory, never recursive.
        // Extra/unexpected files prevent directory removal rather than being erased.
        try
        {
            if (!Path.GetFileName(directory).StartsWith("update-", StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "updates", StringComparison.Ordinal)) return;
            EnsureOrdinaryDirectory(directory);
            foreach (string file in files)
                if (Path.GetDirectoryName(file) == directory && Path.GetFileName(file).StartsWith("SayWhat-", StringComparison.Ordinal))
                    File.Delete(file);
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

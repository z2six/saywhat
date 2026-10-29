using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

internal sealed record AppUpdateInstaller(Uri Url, string Sha256, long SizeBytes);
internal sealed record AppUpdateInfo(bool Ready, bool IsNewer, string Version, Uri? ReleasePage,
    AppUpdateInstaller? Installer = null);

// A metadata-only check. It never downloads an executable, submits speech,
// changes settings, stops translation, or loads a model.
internal static class AppUpdateChecker
{
    internal const string ManifestUrl = "https://saywhat.pages.dev/release.json";
    internal const string ReleasePrefix = "https://github.com/z2six/saywhat/releases/";
    internal const int MaximumBytes = 32768;
    internal const long MaximumInstallerBytes = 512L * 1024 * 1024;
    internal static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    public static Task<AppUpdateInfo> CheckAsync(CancellationToken token) =>
        CheckAsync(CreateHandler(), AppBrand.Version, token);

    internal static HttpClientHandler CreateHandler() => new()
    { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, MaxResponseHeadersLength = 16 };

    internal static async Task<AppUpdateInfo> CheckAsync(HttpMessageHandler handler, string installedVersion,
        CancellationToken token, TimeSpan? timeout = null)
    {
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? MetadataTimeout);
        try
        {
            // One deadline includes headers AND the complete body; redirects are refused.
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new IOException("Couldn't retrieve update information.");
            if (response.Content.Headers.ContentLength is > MaximumBytes)
                throw new InvalidDataException("Update metadata is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var contents = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (contents.Length + read > MaximumBytes) throw new InvalidDataException("Update metadata is too large.");
                contents.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return Parse(new UTF8Encoding(false, true).GetString(contents.ToArray()), installedVersion);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The update check took too long. Try again later."); }
        catch (HttpRequestException)
        {
            // Never retain request URIs, especially signed redirect tokens, in logs.
            throw new IOException("Couldn't retrieve update information. Check your connection and try again.");
        }
        catch (DecoderFallbackException)
        { throw new InvalidDataException("Invalid update metadata encoding."); }
    }

    internal static AppUpdateInfo Parse(string json, string installedVersion)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Update metadata is too large.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int schemaVersion) || schemaVersion != 1 ||
            !root.TryGetProperty("ready", out var ready) || ready.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Unsupported update metadata.");
        string version = ReadString(root, "version");
        if (!IsVersion(version) || !Version.TryParse(version, out var available) ||
            !IsVersion(installedVersion) || !Version.TryParse(installedVersion, out var installed))
            throw new InvalidDataException("Invalid update version.");
        if (!ready.GetBoolean()) return new(false, false, version, null);
        if (ReadString(root, "channel") != "preview" || ReadString(root, "sourceLicense") != "GPL-3.0-only")
            throw new InvalidDataException("Unsupported release channel or source license.");
        string expectedPage = ReleasePrefix + "tag/v" + version;
        if (ReadString(root, "releaseUrl") != expectedPage)
            throw new InvalidDataException("Unexpected release page.");
        var installer = ReadInstaller(ReadObject(root, "installer"), version);
        ValidateArchive(ReadObject(root, "source"), version);
        if (root.TryGetProperty("distribution", out var distribution)) ValidateArchive(distribution, version);
        return new(true, available > installed, version, new Uri(expectedPage), installer);
    }

    internal static bool IsVersion(string value) => value.Length <= 32 &&
        Regex.IsMatch(value, @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);

    internal static AppUpdateInstaller ReadInstaller(JsonElement asset, string version)
    {
        string expectedUrl = ReleasePrefix + "download/v" + version + "/SayWhat-" + version + "-Setup.exe";
        if (asset.ValueKind != JsonValueKind.Object || ReadString(asset, "url") != expectedUrl ||
            !asset.TryGetProperty("sizeBytes", out var size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt64(out long bytes) || bytes <= 0 || bytes > MaximumInstallerBytes)
            throw new InvalidDataException("Invalid installer metadata.");
        return new(new Uri(expectedUrl), ReadChecksum(asset), bytes);
    }

    internal static void ValidateInstaller(AppUpdateInfo info)
    {
        if (!info.Ready || !info.IsNewer || !IsVersion(info.Version) || info.Installer is not { } asset ||
            info.ReleasePage?.AbsoluteUri != ReleasePrefix + "tag/v" + info.Version ||
            asset.Url.AbsoluteUri != ReleasePrefix + "download/v" + info.Version + "/SayWhat-" + info.Version + "-Setup.exe" ||
            !IsChecksum(asset.Sha256) || asset.SizeBytes <= 0 || asset.SizeBytes > MaximumInstallerBytes)
            throw new InvalidDataException("No verified newer installer is available.");
    }

    private static void ValidateArchive(JsonElement asset, string version)
    {
        string url = ReadString(asset, "url"), prefix = ReleasePrefix + "download/v" + version + "/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal) ||
            !Regex.IsMatch(url[prefix.Length..], @"\A[A-Za-z0-9][A-Za-z0-9._-]*\.zip\z", RegexOptions.CultureInvariant) ||
            url.Contains("..", StringComparison.Ordinal)) throw new InvalidDataException("Invalid source archive metadata.");
        _ = ReadChecksum(asset);
    }
    private static string ReadChecksum(JsonElement asset)
    {
        string hash = ReadString(asset, "sha256");
        if (!IsChecksum(hash)) throw new InvalidDataException("Invalid release checksum.");
        return hash.ToLowerInvariant();
    }
    internal static bool IsChecksum(string hash) => hash.Length == 64 &&
        Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);
    private static JsonElement ReadObject(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw new InvalidDataException("Incomplete update metadata.");
    private static string ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : throw new InvalidDataException("Incomplete update metadata.");

    internal static int RunOfflineChecks()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Offline update manifest check failed."); checks++; }
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string Good(string version) => JsonSerializer.Serialize(new {
            schemaVersion = 1, ready = true, version, channel = "preview", sourceLicense = "GPL-3.0-only",
            releaseUrl = ReleasePrefix + "tag/v" + version,
            installer = new { url = ReleasePrefix + "download/v" + version + "/SayWhat-" + version + "-Setup.exe", sha256 = sha, sizeBytes = 100L },
            source = new { url = ReleasePrefix + "download/v" + version + "/SayWhat-Corresponding-Source.zip", sha256 = sha }
        });
        string good = Good("0.1.14");
        Check(Parse(good, "0.1.13").IsNewer);
        Check(!Parse(good, "0.1.14").IsNewer && !Parse(good, "0.1.15").IsNewer);
        Check(Parse(Good("0.2.0"), "0.1.99").IsNewer);
        Check(Parse("{\"schemaVersion\":1,\"ready\":false,\"version\":\"0.1.14\"}", "0.1.13") is { Ready: false, ReleasePage: null });
        foreach (string invalid in new[] {
            good.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            good.Replace("\"ready\":true", "\"ready\":\"true\""),
            good.Replace("0.1.14", "0.1.14-beta"),
            good.Replace("github.com", "example.org"),
            good.Replace("https://", "http://"),
            good.Replace("/z2six/", "/someone-else/"),
            good.Replace(sha, "invalid-checksum"),
            good.Replace("\"sizeBytes\":100", "\"sizeBytes\":0"),
            good.Replace("/SayWhat-Corresponding-Source.zip", "/SayWhat-Corresponding-Source.exe"),
            good.Replace("/SayWhat-0.1.14-Setup.exe", "/SayWhat-0.1.14-Setup.exe?redirect=other"),
            "{}", "not json"
        })
        {
            bool rejected = false;
            try { Parse(invalid, "0.1.13"); }
            catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
            { rejected = true; }
            Check(rejected);
        }
        return checks;
    }
}

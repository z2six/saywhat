using System.Net;
using System.Text;

namespace IncomingTranslator;

internal static class SupportReportChecksTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        int checks = 0;
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException("Support report: " + text); checks++; }
        var valid = new SupportServiceTarget("own-speech", new("http://127.0.0.1:28080/ready"));
        Check(SupportReportChecks.CanProbe(valid), "only a known owned service has a probe target");
        foreach (string uri in new[] { "https://example.com/ready", "http://127.0.0.1:28080/translate", "http://127.0.0.1:28080/ready?token=private", "http://user:secret@localhost:28080/ready", "http://127.0.0.1:80/ready" })
            Check(!SupportReportChecks.CanProbe(new("own-speech", new(uri))), "external, credentialed or non-health URLs are never contacted");
        var handler = new FixtureHandler();
        var result = await SupportReportChecks.ProbeAsync(new[] { valid, new SupportServiceTarget("own-translation", new("http://127.0.0.1:28081/v1/models")),
            new SupportServiceTarget("PRIVATE-NAME", new("http://127.0.0.1:28080/ready")) }, CancellationToken.None, handler);
        Check(handler.Requests == 2 && result[0].Result == "ready" && result[1].Result == "empty" && result[2].Result == "skipped",
            "fake health checks classify ready/empty and never request an untrusted component");
        string summary = SupportReportChecks.Append("synthetic safe header\n", result.Append(new("PRIVATE", "PRIVATE", 200)), VrchatOscConnectionState.Connected);
        Check(!summary.Contains("PRIVATE", StringComparison.Ordinal) && !summary.Contains("28080", StringComparison.Ordinal) &&
            !summary.Contains("secret", StringComparison.Ordinal) && summary.Contains("not a chat-delivery receipt", StringComparison.Ordinal),
            "response text, URLs, custom labels and injected check fields never reach the report");
        var redirect = await SupportReportChecks.ProbeAsync(new[] { valid }, CancellationToken.None, new FixtureHandler(HttpStatusCode.Redirect));
        Check(redirect.Single().Result == "http-error" && redirect.Single().HttpStatus == 302, "redirects are reported without following a new target");
        var overlong = await SupportReportChecks.ProbeAsync(new[] { valid }, CancellationToken.None, new FixtureHandler(HttpStatusCode.OK, new string('x', 40_000)));
        Check(overlong.Single().Result == "invalid-response", "response bodies are bounded");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool rejected = false;
            try { await SupportReportChecks.ProbeAsync(new[] { valid }, cancelled.Token, new FixtureHandler()); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "caller cancellation remains cancellation");
        }
        string app = Path.Combine(directory, "support-app"), data = Path.Combine(directory, "support-data");
        Directory.CreateDirectory(app); Directory.CreateDirectory(data);
        var files = SupportReportChecks.InspectFiles(app, data);
        Check(files.Count(check => check.Result == "missing") == 6, "missing dependencies and settings are reported without loading anything");
        File.WriteAllText(Path.Combine(data, OwnVoiceRuntimePreferences.LegacyFileName), new string(' ', 70_000));
        Check(SupportReportChecks.InspectFiles(app, data).Single(check => check.Component == "worker-settings").Result == "legacy-over-limit",
            "large legacy libraries expose the actual reader-limit condition");
        File.WriteAllText(Path.Combine(data, OwnVoiceRuntimePreferences.FileName), "{}");
        Check(SupportReportChecks.InspectFiles(app, data).Single(check => check.Component == "worker-settings").Result == "valid",
            "a small worker view bypasses a large unrelated profile library");
        return checks;
    }

    private sealed class FixtureHandler(HttpStatusCode status = HttpStatusCode.OK, string? body = null) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? (request.RequestUri!.AbsolutePath.EndsWith("models", StringComparison.Ordinal)
                    ? "{\"data\":[]}" : "{\"ready\":true,\"transcript\":\"PRIVATE-SPEECH-secret\"}"), Encoding.UTF8, "application/json")
            });
        }
    }
}

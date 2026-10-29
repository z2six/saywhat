using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace IncomingTranslator;

// Only owned, synthetic loopback sockets are bound. No HTTP requests, endpoint
// scans, application processes, recordings, models or capture devices are used.
internal static class ManagedServicePortTests
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool valid, string detail)
        { if (!valid) throw new InvalidOperationException("Managed local connections: " + detail); checks++; }
        void Invalid(Action action, string detail)
        {
            bool rejected = false;
            try { action(); } catch (ManagedPortConfigurationException) { rejected = true; }
            Check(rejected, detail);
        }
        ManagedPortConflictException Conflict(Action action, string detail)
        {
            ManagedPortConflictException? conflict = null;
            try { action(); } catch (ManagedPortConflictException error) { conflict = error; }
            Check(conflict is not null, detail);
            return conflict!;
        }

        var settings = new AppSettings();
        Check(settings.AutomaticManagedPorts && settings.OwnSpeechPort == 8080 && settings.IncomingSpeechPort == 8081 &&
            settings.OwnTranslationPort == 1235 && settings.IncomingTranslationPort == 1236 && settings.GroupsSpeechPort == 8082,
            "new installations choose ports automatically and retain the familiar fixed defaults as optional choices");
        Check(SettingsStore.ReadSettings("{}").AutomaticManagedPorts &&
            !SettingsStore.ReadSettings("""{"AutomaticManagedPorts":false}""").AutomaticManagedPorts,
            "legacy installs gain automatic allocation while an explicit fixed choice survives");
        settings.OwnSpeechEndpoint = "http://127.0.0.1:51001";
        settings.IncomingSpeechEndpoint = "http://127.0.0.1:51002";
        settings.OwnTranslationEndpoint = "http://127.0.0.1:51003/v1";
        settings.IncomingTranslationEndpoint = "http://127.0.0.1:51004/v1";
        AppSettings copied = settings.Copy();
        Check(copied.OwnSpeechEndpoint == settings.OwnSpeechEndpoint && copied.IncomingSpeechEndpoint == settings.IncomingSpeechEndpoint &&
            copied.OwnTranslationEndpoint == settings.OwnTranslationEndpoint && copied.IncomingTranslationEndpoint == settings.IncomingTranslationEndpoint,
            "owned runtime snapshots retain their resolved endpoints");
        string json = JsonSerializer.Serialize(copied);
        Check(!json.Contains(nameof(AppSettings.OwnSpeechEndpoint), StringComparison.Ordinal) &&
            !json.Contains(nameof(AppSettings.IncomingSpeechEndpoint), StringComparison.Ordinal) &&
            !json.Contains(nameof(AppSettings.OwnTranslationEndpoint), StringComparison.Ordinal) &&
            !json.Contains(nameof(AppSettings.IncomingTranslationEndpoint), StringComparison.Ordinal),
            "session addresses never enter preferences or profile serialization");
        AppSettings roundTrip = SettingsStore.ReadSettings(json);
        Check(roundTrip.OwnSpeechEndpoint.Length == 0 && roundTrip.IncomingSpeechEndpoint.Length == 0 &&
            roundTrip.OwnTranslationEndpoint.Length == 0 && roundTrip.IncomingTranslationEndpoint.Length == 0,
            "fresh preference reads have no adopted runtime session");
        AppSettings named = TranslationProfiles.Create(settings, "Connection fixture");
        Check(!named.CustomTranslationProfiles.Single().Snapshot.ContainsKey(nameof(AppSettings.OwnSpeechEndpoint)) &&
            !named.CustomTranslationProfiles.Single().Snapshot.ContainsKey(nameof(AppSettings.IncomingSpeechEndpoint)) &&
            !named.CustomTranslationProfiles.Single().Snapshot.ContainsKey(nameof(AppSettings.OwnTranslationEndpoint)) &&
            !named.CustomTranslationProfiles.Single().Snapshot.ContainsKey(nameof(AppSettings.IncomingTranslationEndpoint)),
            "custom profiles capture configured choices without adopting an owned session");
        named.CustomTranslationProfiles.Single().Snapshot[nameof(AppSettings.OwnSpeechEndpoint)] = "http://127.0.0.1:59999";
        Check(TranslationProfiles.Select(named, named.ActiveTranslationProfileId).OwnSpeechEndpoint.Length == 0 &&
            SettingsStore.ReadSettings("""{"OwnSpeechEndpoint":"http://127.0.0.1:59999"}""").OwnSpeechEndpoint.Length == 0,
            "even an imported profile or settings file cannot supply a claimed runtime endpoint");
        var target = new AppSettings { OwnSpeechEndpoint = "http://127.0.0.1:51010" };
        TranslationProfiles.CopyInto(settings, target);
        Check(target.OwnSpeechEndpoint == "http://127.0.0.1:51010",
            "generic preference copying cannot overwrite a live owned-session endpoint");
        settings.AutomaticManagedPorts = false; settings.OwnSpeechPort = 50101; settings.IncomingSpeechPort = 50102;
        settings.OwnTranslationPort = 50103; settings.IncomingTranslationPort = 50104; settings.GroupsSpeechPort = 50105;
        copied = settings.Copy(); roundTrip = SettingsStore.ReadSettings(JsonSerializer.Serialize(copied));
        Check(!roundTrip.AutomaticManagedPorts && roundTrip.OwnSpeechPort == 50101 && roundTrip.IncomingSpeechPort == 50102 &&
            roundTrip.OwnTranslationPort == 50103 && roundTrip.IncomingTranslationPort == 50104 && roundTrip.GroupsSpeechPort == 50105,
            "all fixed choices copy and round-trip independently from resolved addresses");
        foreach (int port in new[] { int.MinValue, -1, 0, 80, 1023, 65536, int.MaxValue })
            Invalid(() => ManagedPortAllocator.ValidatePort(port, "Your voice translator"), "invalid managed port is rejected before binding");
        foreach (int port in new[] { 1024, 1235, 8080, 65535 })
        { ManagedPortAllocator.ValidatePort(port, "Your voice translator"); Check(true, "valid fixed range is accepted without probing it"); }
        settings.OwnSpeechPort = 0;
        Check(SettingsStore.Normalize(settings.Copy()).OwnSpeechPort == 0,
            "normalization preserves an invalid manual value so it can be explained instead of silently changing connections");
        Invalid(() => ManagedPortAllocator.ValidateConfiguration(settings, ManagedServiceKind.OwnSpeech), "fixed use validates the selected service");
        settings.AutomaticManagedPorts = true; ManagedPortAllocator.ValidateConfiguration(settings);
        Check(true, "automatic mode does not depend on inactive fixed values");
        settings.AutomaticManagedPorts = false; settings.OwnSpeechPort = 50101; settings.IncomingSpeechPort = 50101;
        Invalid(() => ManagedPortAllocator.ValidateConfiguration(settings, ManagedServiceKind.OwnSpeech, ManagedServiceKind.IncomingSpeech),
            "two required services cannot claim one fixed TCP port");
        ManagedPortAllocator.ValidateConfiguration(settings, ManagedServiceKind.OwnSpeech);
        Check(true, "an unused optional subtitle choice does not prevent a valid microphone-only operation");
        settings.IncomingSpeechPort = 50102; settings.UseManagedTranslator = true; settings.ShareTranslationModel = true;
        settings.IncomingTranslationPort = settings.OwnTranslationPort;
        ManagedPortAllocator.ValidateConfiguration(settings);
        Check(true, "a shared translator has one service reservation rather than a false duplicate");
        settings.ShareTranslationModel = false;
        Invalid(() => ManagedPortAllocator.ValidateConfiguration(settings), "separate translator instances need different fixed ports");
        settings.UseManagedTranslator = false; settings.OwnTranslationPort = 0; settings.IncomingTranslationPort = 0;
        settings.OwnTranslatorBaseUrl = "http://127.0.0.1:19501/v1"; settings.TranslatorBaseUrl = "http://127.0.0.1:19502/v1";
        ManagedPortAllocator.ValidateConfiguration(settings);
        Check(settings.OwnTranslatorBaseUrl == "http://127.0.0.1:19501/v1" && settings.TranslatorBaseUrl == "http://127.0.0.1:19502/v1",
            "allocation validation leaves externally supplied URLs untouched and ignores their unused managed-port settings");
        string previousLanguage = UiText.Language;
        try
        {
            foreach (string language in new[] { "en", "zh-CN", "ja", "ko" })
            {
                UiText.Language = language;
                var busy = new ManagedPortConflictException("Your voice translator", 50123);
                var unavailable = new ManagedPortConflictException("Subtitle speech recognition", 0);
                var invalid = new ManagedPortConfigurationException("Choose a local port from {0} to {1} for {2}, or turn on Automatic in Connection settings.", 1024, 65535, "Your voice recognition");
                var duplicate = new ManagedPortConfigurationException("{0} and {1} use the same local port {2}. Choose different ports, or turn on Automatic in Connection settings.", "Your voice translator", "Subtitle translator", 50124);
                Check(UserFacingErrors.Describe(busy).Contains("50123", StringComparison.Ordinal) &&
                    UserFacingErrors.Describe(busy).Contains(UiText.T("Your voice translator"), StringComparison.Ordinal) &&
                    UserFacingErrors.Describe(unavailable).Contains(UiText.T("Subtitle speech recognition"), StringComparison.Ordinal),
                    "busy and automatic-allocation failures use translated templates and service labels: " + language);
                Check(UserFacingErrors.Describe(invalid).Contains("65535", StringComparison.Ordinal) &&
                    UserFacingErrors.Describe(invalid).Contains(UiText.T("Your voice recognition"), StringComparison.Ordinal) &&
                    UserFacingErrors.Describe(duplicate).Contains("50124", StringComparison.Ordinal) &&
                    UserFacingErrors.Describe(duplicate).Contains(UiText.T("Subtitle translator"), StringComparison.Ordinal),
                    "invalid and duplicate fixed choices retain formatted values in the selected language: " + language);
                Check(language == "en" || new Exception[] { busy, unavailable, invalid, duplicate }
                    .All(error => UserFacingErrors.Describe(error) != error.Message && !UserFacingErrors.Describe(error).Contains("{0}", StringComparison.Ordinal)),
                    "all non-English port explanations are translated without dynamic-message lookup or unresolved placeholders");
            }
        }
        finally { UiText.Language = previousLanguage; }

        // Reserve the former fixed default ourselves when free. If an existing
        // service or Windows already holds it, leave that owner untouched. The
        // test never connects to or inspects whoever owns that default port.
        using (Socket? oldDefault = TryBlockOldDefault())
        using (ManagedPortReservation automatic = ManagedPortAllocator.Reserve(true, 8080, "Your voice recognition"))
        {
            Check(automatic.Port != 8080 && automatic.Port is >= 1024 and <= 65535,
                "an occupied/unavailable old 8080 default does not prevent automatic allocation");
            Check(automatic.EndPoint.Address.Equals(IPAddress.Loopback) && !automatic.IsReleased,
                "automatic allocation is an actual exclusive IPv4-loopback bind, not an unreserved port guess");
            int selected = automatic.Port; IPEndPoint mutableCopy = automatic.EndPoint; mutableCopy.Port = 1024;
            Check(automatic.Port == selected, "callers cannot mutate the reservation's recorded port through an endpoint object");
            ManagedPortConflictException conflict = Conflict(() => ManagedPortAllocator.EnsureBindable(selected, "Your voice recognition"),
                "the held reservation prevents a conflicting service from binding");
            Check(conflict.Port == selected && conflict.Service == "Your voice recognition" &&
                UserFacingErrors.Describe(conflict).Contains("Automatic", StringComparison.Ordinal) &&
                !UserFacingErrors.Describe(conflict).Contains("ASR", StringComparison.OrdinalIgnoreCase),
                "the typed busy-port explanation offers connection settings, not unrelated speech-model advice");
            automatic.ReleaseForStart(); automatic.ReleaseForStart(); automatic.Dispose();
            Check(automatic.IsReleased, "handoff and repeated disposal safely release exactly the owned socket");
            ManagedPortAllocator.EnsureBindable(selected, "Your voice recognition");
            Check(true, "a released reservation can be handed to the owned child");
        }

        var leases = new List<ManagedPortReservation>(); int calls = 0;
        int result = await ManagedPortAllocator.ExecuteWithRetryAsync(true, 8080, "Your voice translator", (lease, _) =>
        {
            calls++; leases.Add(lease); Check(!lease.IsReleased, "retry callbacks receive a currently held reservation");
            lease.ReleaseForStart();
            return calls == 1 ? Task.FromException<int>(new ManagedPortConflictException("Your voice translator", lease.Port)) : Task.FromResult(lease.Port);
        });
        Check(calls == 2 && result > 0 && leases.All(lease => lease.IsReleased),
            "a verified lost-bind race can retry, with no old reservation left behind");
        calls = 0; leases.Clear(); bool exhausted = false;
        try
        {
            await ManagedPortAllocator.ExecuteWithRetryAsync<int>(true, 8080, "Your voice translator", (lease, _) =>
            { calls++; leases.Add(lease); lease.ReleaseForStart(); throw new ManagedPortConflictException("Your voice translator", lease.Port); });
        }
        catch (ManagedPortConflictException) { exhausted = true; }
        Check(exhausted && calls == 3 && leases.All(lease => lease.IsReleased), "automatic startup retries are bounded to three and release all sockets");
        calls = 0; leases.Clear(); bool modelFailed = false;
        try
        {
            await ManagedPortAllocator.ExecuteWithRetryAsync<int>(true, 8080, "Your voice translator", (lease, _) =>
            { calls++; leases.Add(lease); throw new InvalidDataException("Synthetic model failure."); });
        }
        catch (InvalidDataException) { modelFailed = true; }
        Check(modelFailed && calls == 1 && leases.Single().IsReleased, "a model error is not mistaken for contention or retried");
        int fixedPort;
        using (ManagedPortReservation lease = ManagedPortAllocator.Reserve(true, 0, "Synthetic fixed port")) fixedPort = lease.Port;
        calls = 0; leases.Clear(); bool fixedFailed = false;
        try
        {
            await ManagedPortAllocator.ExecuteWithRetryAsync<int>(false, fixedPort, "Your voice translator", (lease, _) =>
            { calls++; leases.Add(lease); lease.ReleaseForStart(); throw new ManagedPortConflictException("Your voice translator", lease.Port); });
        }
        catch (ManagedPortConflictException) { fixedFailed = true; }
        Check(fixedFailed && calls == 1 && leases.Single().IsReleased, "fixed mode never quietly chooses a different port");
        calls = 0; using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await ManagedPortAllocator.ExecuteWithRetryAsync(true, 0, "Your voice translator", (_, _) => { calls++; return Task.FromResult(1); }, cancelled.Token); }
        catch (OperationCanceledException) { }
        Check(calls == 0, "pre-cancelled startup does not reserve or launch a service");
        using var duringStart = new CancellationTokenSource(); ManagedPortReservation? cancelledLease = null; bool cancellationObserved = false;
        try
        {
            await ManagedPortAllocator.ExecuteWithRetryAsync<int>(true, 0, "Your voice translator", (lease, _) =>
            { cancelledLease = lease; duringStart.Cancel(); throw new ManagedPortConflictException("Your voice translator", lease.Port); }, duringStart.Token);
        }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved && cancelledLease is { IsReleased: true }, "cancellation wins over a bind conflict and releases its reservation");
        foreach (int attempts in new[] { 0, 4 })
        {
            bool rejected = false;
            try { await ManagedPortAllocator.ExecuteWithRetryAsync(true, 0, "Your voice translator", (_, _) => Task.FromResult(1), maximumAttempts: attempts); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "callers cannot request an unbounded retry loop");
        }

        System.Diagnostics.ProcessStartInfo probe = ManagedPortAllocator.OwnershipProbeInfo();
        Check(probe.FileName == Path.Combine(Environment.SystemDirectory, "netstat.exe") &&
            probe.ArgumentList.SequenceEqual(new[] { "-ano", "-p", "tcp" }) && !probe.UseShellExecute && probe.CreateNoWindow &&
            probe.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden && probe.RedirectStandardOutput && probe.RedirectStandardError,
            "ownership uses only the full-path Windows utility with fixed numeric TCP flags and hidden bounded pipes");
        const string listener = "  TCP    127.0.0.1:51000    0.0.0.0:0    LISTENING    101\r\n";
        foreach (string state in new[] { "LISTENING", "侦听", "リッスン中", "수신 대기", "ABHÖREN" })
            Check(ManagedPortAllocator.ContainsOwnedListener("Localized header\r\n  Proto Local Foreign State PID\r\n" + listener.Replace("LISTENING", state), 51000, 101),
                "numeric ownership proof does not depend on English or one-word localized state names");
        Check(!ManagedPortAllocator.ContainsOwnedListener(listener, 51000, 102) && !ManagedPortAllocator.ContainsOwnedListener(listener, 51001, 101),
            "another process or another port can never satisfy owned-child readiness");
        Check(ManagedPortAllocator.ContainsOwnedListener(listener.Replace("127.0.0.1:51000", "0.0.0.0:51000"), 51000, 101) &&
            !ManagedPortAllocator.ContainsOwnedListener(listener.Replace("127.0.0.1:51000", "192.0.2.1:51000"), 51000, 101),
            "owned wildcard listeners cover loopback, while unrelated interface bindings are not adopted");
        string foreign = listener.Replace("127.0.0.1:51000", "0.0.0.0:51000").Replace("101", "102");
        Check(!ManagedPortAllocator.ContainsOwnedListener(listener + foreign, 51000, 101),
            "an owned row cannot hide a second process sharing the same loopback route");
        Check(!ManagedPortAllocator.ContainsOwnedListener(foreign + listener, 51000, 101), "shared-address rejection is independent of snapshot ordering");
        Check(!ManagedPortAllocator.ContainsOwnedListener(listener.Replace("0.0.0.0:0", "192.0.2.5:80"), 51000, 101) &&
            !ManagedPortAllocator.ContainsOwnedListener(listener.Replace("0.0.0.0:0", "0.0.0.0:1"), 51000, 101),
            "a connected TCP row is not accepted as a listening server regardless of its state text");
        foreach (string malformed in new[] { "", "TCP", listener + "\0", "TCP 127.0.0.1:51000 0.0.0.0:0 101", "UDP 127.0.0.1:51000 0.0.0.0:0 LISTENING 101",
            listener.Replace(":51000", ":65536"), listener.Replace(":51000", ":-1"), listener.Replace("101", "2147483648"),
            listener.Replace("101", "+101"), listener.Replace("101", "１０１"), listener.Replace("127.0.0.1", "localhost"),
            listener.Replace("127.0.0.1", "127.1"), listener.Replace("LISTENING", "a b c d e f"), listener.Replace("LISTENING", new string('x', 65)),
            listener.Replace("127.0.0.1:51000", "[::1]:51000"), new string(' ', ManagedPortAllocator.MaximumOwnershipOutputCharacters + 1) })
            Check(!ManagedPortAllocator.ContainsOwnedListener(malformed, 51000, 101), "malformed, nonnumeric or oversized ownership snapshots fail closed");
        Check(!ManagedPortAllocator.ContainsOwnedListener(listener + "TCP invalid 0.0.0.0:0 LISTENING 102", 51000, 101),
            "a valid first row cannot conceal an incompletely parsed TCP snapshot");
        if (OperatingSystem.IsWindows())
        {
            using ManagedPortReservation lease = ManagedPortAllocator.Reserve(true, 0, "Synthetic ownership fixture");
            int port = lease.Port; lease.ReleaseForStart();
            using var owned = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            owned.Bind(new IPEndPoint(IPAddress.Loopback, port)); owned.Listen(1);
            Check(await ManagedPortAllocator.IsOwnedTcpListenerAsync(port, Environment.ProcessId), "Windows ownership lookup sees only the exact socket fixture owned by this test process");
            Check(!await ManagedPortAllocator.IsOwnedTcpListenerAsync(port, int.MaxValue), "a foreign PID cannot adopt the test listener through a healthy-looking response");
            owned.Dispose();
            Check(!await ManagedPortAllocator.IsOwnedTcpListenerAsync(port, Environment.ProcessId), "closing the test fixture removes its readiness ownership proof");
            using var ownershipCancelled = new CancellationTokenSource(); ownershipCancelled.Cancel();
            bool cancelledProof = false;
            try { await ManagedPortAllocator.IsOwnedTcpListenerAsync(port, Environment.ProcessId, ownershipCancelled.Token); }
            catch (OperationCanceledException) { cancelledProof = true; }
            Check(cancelledProof, "a cancelled ownership check cannot start a utility or produce readiness proof");
        }
        return checks;
    }

    private static Socket? TryBlockOldDefault()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try { socket.Bind(new IPEndPoint(IPAddress.Loopback, 8080)); return socket; }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        { socket.Dispose(); return null; }
        catch { socket.Dispose(); throw; }
    }

}

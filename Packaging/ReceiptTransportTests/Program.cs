using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// No self-elevation, live AppData, cleanup switches, installer/app launches,
// model loading, process termination or recursive deletion. Fixtures are kept.
internal static class Program
{
    private const string IdentityLeaf = "saywhat-data-identity.ini", ActionsLeaf = "saywhat-data-actions.ini";
    private const AccessControlSections AclReadSections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly List<string> Passed = [];
    private static string _packaging = "", _fixture = "", _source = "", _sourceHash = "", _copiedHelper = "", _root = "";
    private static ShellTokenRunner? _runner;
    private static bool _protected, _limitedExisting, _reportReady;

    private static int Main(string[] args)
    {
        _limitedExisting = args.Length == 2 && args[0] == "--limited-existing" && Regex.IsMatch(args[1], "^[a-f0-9]{32}$");
        if (!_limitedExisting && (args.Length > 1 || (args.Length == 1 && args[0] is not ("--protected" or "--default"))))
        {
            Console.Error.WriteLine("Usage: dotnet ReceiptTransportTests.dll [--default|--protected|--limited-existing <32-lowercase-hex fixture GUID>]. Never self-elevates.");
            return 2;
        }
        _protected = args is ["--protected"];
        Exception? failure = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows with an interactive Explorer shell is required.");
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "manage-install-data.ps1"))) directory = directory.Parent;
            _packaging = directory?.FullName ?? throw new InvalidOperationException("Run only from the workspace Packaging tree.");
            _source = Path.Combine(_packaging, "scripts", "manage-install-data.ps1");
            _sourceHash = Hash(_source);
            _fixture = Path.Combine(_packaging, "offline-receipt-transport-fixtures", _limitedExisting ? args[1] : Guid.NewGuid().ToString("N"));
            AssertFixturePath(_fixture, mayBeMissing: !_limitedExisting);
            if (!_limitedExisting)
            {
                if (Directory.Exists(_fixture) || File.Exists(_fixture)) throw new InvalidOperationException("A fixture must be unique and absent.");
                Directory.CreateDirectory(_fixture);
                _reportReady = true;
            }
            _root = _limitedExisting ? Path.Combine(_fixture, "account-a", "SayWhat") : CreateData("account-a");
            string secondRoot = _limitedExisting ? Path.Combine(_fixture, "account-b", "SayWhat") : CreateData("account-b");
            AssertFixturePath(_root);
            AssertFixturePath(secondRoot);
            _runner = new ShellTokenRunner(_fixture, _root);
            if (_protected != _runner.CallerElevated)
                throw new InvalidOperationException(_protected
                    ? "--protected requires an already elevated same-account process. This program never prompts or elevates."
                    : "Default mode must run as the unelevated shell account; use the explicitly authorized --protected mode for elevated tests.");
            Check(_runner.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal), "verified an ordinary local/domain shell-account SID");
            string? existingParent = _limitedExisting ? ValidateExistingPreparation() : null;
            string temp = Path.Combine(_fixture, "runtime-temp");
            AssertFixturePath(temp, mayBeMissing: true);
            Directory.CreateDirectory(temp);
            string before = Snapshot(_root), secondBefore = Snapshot(secondRoot);
            if (_protected) RunProtected();
            else if (_limitedExisting) RunPreparedChannelChecks(existingParent!,
                Value(ReadIni(Path.Combine(existingParent!, IdentityLeaf)), "Transport", "ReceiptNonce"),
                Value(ReadIni(Path.Combine(existingParent!, ActionsLeaf)), "Transport", "ReceiptNonce"), privileged: false);
            else RunDefault();
            Check(Snapshot(_root) == before && Snapshot(secondRoot) == secondBefore, "both synthetic accounts' data stayed byte-for-byte unchanged");
            Check(Hash(_source) == _sourceHash, "reviewed helper source remained unchanged");
            Check(Hash(_copiedHelper) == _sourceHash, "executed helper copy still exactly matches source SHA-256");
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine("Receipt transport test failed: " + error.Message); }
        finally
        {
            if (_reportReady && _fixture.Length > 0 && Directory.Exists(_fixture))
            {
                string report = Path.Combine(_fixture, _limitedExisting ? "limited-results.json" : "results.json");
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    Status = failure is null ? "Passed" : "Failed", Mode = _protected ? "Protected" : _limitedExisting ? "LimitedExisting" : "Default", Checks = Passed.Count,
                    HelperSha256 = _sourceHash, Fixture = _fixture, ShellSid = _runner?.Sid, ShellAccount = _runner?.Account,
                    CallerElevated = _runner?.CallerElevated, Error = failure?.ToString(), Passed,
                    Executions = _runner?.Executions,
                    NotExecuted = _limitedExisting ? "No elevation, preparation, ACL changes, link creation, elevated-fallback test or custom token-broker launch. Original elevated preparation report retained separately." : "",
                    Safety = "Unique workspace fixtures only; metadata/ValidateOnly; no live data, cleanup choices, apps, installers, models, audio, process stopping or recursive cleanup."
                }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                Console.WriteLine($"{(failure is null ? "PASS" : "FAIL")} {Passed.Count} checks. Report: {report}");
            }
            _runner?.Dispose();
        }
        return failure is null ? 0 : 1;
    }

    private static string ValidateExistingPreparation()
    {
        // The CLI accepts a GUID, never an arbitrary path. Before running a child
        // or writing a report, verify this exact already-admin-prepared fixture.
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_fixture, "results.json")));
        var original = document.RootElement;
        Check(original.GetProperty("Mode").GetString() == "Protected" && original.GetProperty("CallerElevated").GetBoolean(),
            "existing fixture has the separately approved elevated preparation report");
        Check(original.GetProperty("HelperSha256").GetString() == _sourceHash && original.GetProperty("ShellSid").GetString() == Runner.Sid,
            "existing fixture's source hash and original shell SID match this limited caller");
        string[] parents = Directory.GetDirectories(_fixture, "is-*.tmp", SearchOption.TopDirectoryOnly);
        Check(parents.Length == 1 && Regex.IsMatch(Path.GetFileName(parents[0]), "^is-[a-f0-9]{10}[.]tmp$"),
            "existing fixture contains exactly one canonical protected temp child");
        string parent = parents[0];
        AssertFixturePath(parent);
        _copiedHelper = Path.Combine(parent, "manage-install-data.ps1");
        AssertFixturePath(_copiedHelper);
        Check(Hash(_copiedHelper) == _sourceHash, "existing protected helper exactly matches the reviewed current source hash");
        AssertProtectedParent(parent);
        foreach (string leaf in new[] { IdentityLeaf, ActionsLeaf })
        {
            string receipt = Path.Combine(parent, leaf);
            AssertReceiptAcl(receipt);
            var values = ReadIni(receipt);
            Check(Value(values, "Transport", "UserSid") == Runner.Sid && Regex.IsMatch(Value(values, "Transport", "ReceiptNonce"), "^[a-fA-F0-9]{32}$"),
                "existing admin-prepared channel contains the correct SID and a valid nonce: " + leaf);
            string success = Value(values, "Result", "Success");
            Check(success is "0" or "1", "existing admin-prepared channel has a known result state: " + leaf);
            if (success == "1")
            {
                Check(Value(values, "Result", "ReceiptNonce") == Value(values, "Transport", "ReceiptNonce"),
                    "repeatable successful channel keeps its original admin-prepared nonce: " + leaf);
                if (leaf == IdentityLeaf)
                    Check(Value(values, "Result", "UserSid") == Runner.Sid && SamePath(Value(values, "Result", "DataRoot"), _root) && Value(values, "Result", "Supported") == "0",
                        "repeatable identity result still identifies only this same synthetic account");
                else Check(Value(values, "Result", "RemovedFiles") == "0", "repeatable action receipt still records zero removals");
            }
        }
        _reportReady = true;
        return parent;
    }

    private static void RunDefault()
    {
        string pinDirectory = Directory.CreateDirectory(Path.Combine(_fixture, "directory-pin")).FullName;
        AssertFixturePath(pinDirectory);
        string pinAcl = new DirectoryInfo(pinDirectory).GetAccessControl(AclReadSections).GetSecurityDescriptorSddlForm(AclReadSections);
        var pin = ShellTokenRunner.ProbeDirectoryPin(pinDirectory);
        Check(pin.Before == 0, "ordinary owned directory allows a DELETE-access handle before pinning");
        Check(pin.During == 32, "nofollow directory handle sharing read/write but not delete denies DELETE access with sharing violation 32");
        Check(pin.After == 0, "DELETE-access handle is available again after the short-lived directory pin closes");
        Check(new DirectoryInfo(pinDirectory).GetAccessControl(AclReadSections).GetSecurityDescriptorSddlForm(AclReadSections) == pinAcl,
            "directory pin changes neither its ACL nor owner and performs no rename/delete");
        _copiedHelper = CopyHelper(_fixture);
        string plain = Directory.CreateDirectory(Path.Combine(_fixture, "plain-receipts")).FullName;
        string identity = Path.Combine(plain, "unprotected-identity.ini");
        Check(Runner.RunLimited(_copiedHelper, "-ResolveUserIdentity", "-ReceiptPath", identity).ExitCode == 0,
            "metadata-only identity works as the real limited shell account in an ordinary fixture");
        var values = ReadIni(identity);
        Check(Value(values, "Result", "Success") == "1", "ordinary identity receipt is successful");
        Check(Value(values, "Result", "UserSid") == Runner.Sid, "ordinary identity receipt records the verified real user SID");
        Check(SamePath(Value(values, "Result", "DataRoot"), _root) && Value(values, "Result", "Supported") == "0",
            "identity uses the explicit synthetic root and does not mistake it for private AppData");
        string actions = Path.Combine(plain, "unprotected-actions.ini");
        Check(Runner.RunLimited(_copiedHelper, "-ValidateOnly", "-DataRoot", _root, "-ReceiptPath", actions).ExitCode == 0,
            "ordinary validation-only helper succeeds without any data choices");
        Check(Value(ReadIni(actions), "Result", "RemovedFiles") == "0", "ordinary validation removes zero files");
        string nonce = Guid.NewGuid().ToString("N");
        foreach (string leaf in new[] { IdentityLeaf, ActionsLeaf, "wrong-name.ini" })
        {
            string receipt = Path.Combine(plain, leaf);
            Check(Runner.RunLimited(_copiedHelper, "-ValidateOnly", "-DataRoot", _root, "-ReceiptPath", receipt,
                "-RequireProtectedReceipt", "-ReceiptNonce", nonce, "-ExpectedUserSid", Runner.Sid).ExitCode != 0,
                "protected transport refuses an ordinary, unprepared parent: " + leaf);
            Check(!File.Exists(receipt), "refused unprepared transport did not create a receipt: " + leaf);
        }
        foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544", "S-1-1-0", "S-1-5-21-1-2-3-9999", "not-a-sid" })
        {
            string receipt = Path.Combine(plain, IdentityLeaf);
            Check(Runner.RunLimited(_copiedHelper, "-ResolveUserIdentity", "-ReceiptPath", receipt, "-RequireProtectedReceipt",
                "-ReceiptNonce", nonce, "-ExpectedUserSid", sid).ExitCode != 0, "protected identity rejects an untrusted expected SID: " + sid);
            Check(!File.Exists(receipt), "untrusted SID cannot create a transport channel: " + sid);
        }
        Check(Runner.RunLimited(_copiedHelper, "-PrepareProtectedReceipt", "-ReceiptPath", Path.Combine(plain, IdentityLeaf),
            "-ReceiptNonce", nonce).ExitCode != 0, "limited account cannot prepare an elevated protected transport");
        Check(!File.Exists(Path.Combine(plain, IdentityLeaf)), "limited preparation refusal leaves the channel absent");
    }

    private static void RunProtected()
    {
        // ACL changes target only paths validated inside this new GUID fixture.
        // The outer fixture/report remains readable/writable by the real user.
        ProtectDirectory(_fixture, new SecurityIdentifier(Runner.Sid), FileSystemRights.Modify);
        string leaf = "is-" + Guid.NewGuid().ToString("N")[..10] + ".tmp";
        string parent = Directory.CreateDirectory(Path.Combine(_fixture, leaf)).FullName;
        ProtectDirectory(parent, new SecurityIdentifier(Runner.Sid), FileSystemRights.ReadAndExecute);
        _copiedHelper = CopyHelper(parent);
        ProtectHelper(_copiedHelper);
        AssertProtectedParent(parent);
        string identity = Path.Combine(parent, IdentityLeaf), actions = Path.Combine(parent, ActionsLeaf);
        string identityNonce = Guid.NewGuid().ToString("N"), actionNonce = Guid.NewGuid().ToString("N");
        Check(identityNonce != actionNonce && Regex.IsMatch(identityNonce, "^[a-f0-9]{32}$") && Regex.IsMatch(actionNonce, "^[a-f0-9]{32}$"),
            "each test channel gets a fresh independent 32-hex nonce");
        Prepare(identity, identityNonce);
        Prepare(actions, actionNonce);
        AssertReceiptAcl(identity);
        AssertReceiptAcl(actions);
        RunPreparedChannelChecks(parent, identityNonce, actionNonce, privileged: true);
    }

    private static void RunPreparedChannelChecks(string parent, string identityNonce, string actionNonce, bool privileged)
    {
        string leaf = Path.GetFileName(parent), identity = Path.Combine(parent, IdentityLeaf), actions = Path.Combine(parent, ActionsLeaf);

        string probe = Path.Combine(_fixture, "Probe-ReceiptAccess.ps1");
        string probeSource = Path.Combine(AppContext.BaseDirectory, "Probe-ReceiptAccess.ps1");
        if (!File.Exists(probe)) File.Copy(probeSource, probe, false);
        Check(Hash(probe) == Hash(probeSource), "limited access probe is an exact copy of the test source");
        Check(Runner.RunLimited(probe, "-FixtureRoot", _fixture, "-ProtectedLeaf", leaf).ExitCode == 0, "real limited token completed the non-mutating access probe");
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_fixture, "access-probe.json"))))
        {
            var result = document.RootElement;
            Check(result.GetProperty("UserSid").GetString() == Runner.Sid && !result.GetProperty("IsAdministrator").GetBoolean(),
                "access probe actually ran as the verified unelevated shell SID");
            foreach (string property in new[] { "ParentRead", "HelperRead", "IdentityRead", "IdentityWrite", "ActionsRead", "ActionsWrite" })
                Check(result.GetProperty(property).GetInt32() == 0, "limited token has only necessary access: " + property);
            foreach (string property in new[] { "ParentAddFile", "ParentAddDirectory", "ParentDeleteChild", "ParentDelete", "ParentWriteDac", "ParentWriteOwner",
                "HelperWrite", "HelperDelete", "HelperWriteDac", "HelperWriteOwner", "IdentityDelete", "IdentityWriteDac", "IdentityWriteOwner",
                "ActionsDelete", "ActionsWriteDac", "ActionsWriteOwner" })
                Check(result.GetProperty(property).GetInt32() == 5, "limited token is denied replacement/ownership access: " + property);
            Check(result.GetProperty("IdentityOpenReadWrite").GetBoolean() && result.GetProperty("ActionsOpenReadWrite").GetBoolean(),
                "exact existing receipt files open for limited read/write");
            Check(result.GetProperty("OldWriteDenied").GetBoolean() && !File.Exists(Path.Combine(parent, "old-unprepared-receipt.ini")),
                "old WriteAllText creation is truly denied by the protected parent ACL");
        }

        Check(LimitedIdentity(identity, identityNonce, Runner.Sid).ExitCode == 0, "prepared identity channel succeeds with real limited token");
        var resolved = ReadIni(identity);
        Check(Value(resolved, "Result", "Success") == "1" && Value(resolved, "Result", "UserSid") == Runner.Sid,
            "reader sees the limited user's successful identity receipt and correct SID");
        Check(SamePath(Value(resolved, "Result", "DataRoot"), _root) && Value(resolved, "Result", "Supported") == "0",
            "protected identity remains synthetic and explicitly unsupported for live cleanup");
        AssertBinding(resolved, identityNonce);
        Check(LimitedActions(actions, actionNonce, Runner.Sid, _root).ExitCode == 0, "prepared validation channel succeeds with real limited token");
        var validated = ReadIni(actions);
        Check(Value(validated, "Result", "Success") == "1" && Value(validated, "Result", "RemovedFiles") == "0",
            "reader sees a successful zero-removal validation receipt");
        AssertBinding(validated, actionNonce);
        AssertReceiptAcl(identity);
        AssertReceiptAcl(actions);
        AssertProtectedParent(parent);

        string inheritedModules = Path.Combine(_fixture, "incompatible-inherited-modules");
        string incompatibleSecurity = Path.Combine(inheritedModules, "Microsoft.PowerShell.Security");
        Directory.CreateDirectory(incompatibleSecurity);
        File.WriteAllText(Path.Combine(incompatibleSecurity, "Microsoft.PowerShell.Security.psd1"),
            "@{ ModuleVersion='99.0'; PowerShellVersion='99.0'; RootModule='Microsoft.PowerShell.Security.psm1'; FunctionsToExport=@('Get-Acl'); CmdletsToExport=@() }", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(incompatibleSecurity, "Microsoft.PowerShell.Security.psm1"),
            "function Get-Acl { throw 'Synthetic inherited security module must never be loaded.' }; Export-ModuleMember -Function Get-Acl", new UTF8Encoding(false));
        string? callerModules = Environment.GetEnvironmentVariable("PSModulePath");
        if (privileged)
        {
            Check(Runner.RunCurrent(_copiedHelper,
                ["-PrepareProtectedReceipt", "-ReceiptPath", identity, "-ReceiptNonce", identityNonce],
                inheritedModules: inheritedModules).ExitCode == 0,
                "elevated metadata preparation also ignores inherited user/runtime modules");
            Check(Value(ReadIni(identity), "Result", "Success") == "0",
                "sanitized elevated preparation leaves only its uncommitted protected receipt");
        }
        Check(Runner.RunLimitedWithInheritedModules(_copiedHelper, inheritedModules,
            "-ResolveUserIdentity", "-ReceiptPath", identity, "-RequireProtectedReceipt", "-RequireOriginalUserToken",
            "-ReceiptNonce", identityNonce, "-ExpectedUserSid", Runner.Sid).ExitCode == 0,
            "protected identity ignores incompatible inherited modules and uses Windows PowerShell built-ins");
        AssertBinding(ReadIni(identity), identityNonce);
        Check(Runner.RunLimitedWithInheritedModules(_copiedHelper, inheritedModules,
            "-ValidateOnly", "-DataRoot", _root, "-ReceiptPath", actions, "-RequireProtectedReceipt", "-RequireOriginalUserToken",
            "-ReceiptNonce", actionNonce, "-ExpectedUserSid", Runner.Sid).ExitCode == 0,
            "protected validation ignores incompatible inherited modules and uses Windows PowerShell built-ins");
        var inheritedModulesActions = ReadIni(actions);
        Check(Value(inheritedModulesActions, "Result", "Success") == "1" && Value(inheritedModulesActions, "Result", "RemovedFiles") == "0",
            "inherited incompatible module validation removes zero synthetic files");
        AssertBinding(inheritedModulesActions, actionNonce);
        Check(Environment.GetEnvironmentVariable("PSModulePath") == callerModules,
            "helper runtime correction changes neither caller nor global PSModulePath");
        AssertReceiptAcl(identity);
        AssertReceiptAcl(actions);
        AssertProtectedParent(parent);

        // Real Inno original-user children may inherit an unwritable protected
        // TEMP/TMP. Earlier fixtures always forced a writable runtime directory
        // and therefore missed module loading failures before receipt completion.
        Check(Runner.RunLimitedWithInheritedTemp(_copiedHelper, parent,
            "-ResolveUserIdentity", "-ReceiptPath", identity, "-RequireProtectedReceipt", "-RequireOriginalUserToken",
            "-ReceiptNonce", identityNonce, "-ExpectedUserSid", Runner.Sid).ExitCode == 0,
            "protected identity succeeds when the limited child inherits protected Inno TEMP/TMP");
        var inheritedIdentity = ReadIni(identity);
        Check(Value(inheritedIdentity, "Result", "Success") == "1" &&
            Value(inheritedIdentity, "Result", "UserSid") == Runner.Sid && SamePath(Value(inheritedIdentity, "Result", "DataRoot"), _root),
            "inherited protected TEMP identity keeps the verified SID and synthetic data root");
        AssertBinding(inheritedIdentity, identityNonce);
        Check(Runner.RunLimitedWithInheritedTemp(_copiedHelper, parent,
            "-ValidateOnly", "-DataRoot", _root, "-ReceiptPath", actions, "-RequireProtectedReceipt", "-RequireOriginalUserToken",
            "-ReceiptNonce", actionNonce, "-ExpectedUserSid", Runner.Sid).ExitCode == 0,
            "protected validation succeeds when the limited child inherits protected Inno TEMP/TMP");
        var inheritedActions = ReadIni(actions);
        Check(Value(inheritedActions, "Result", "Success") == "1" && Value(inheritedActions, "Result", "RemovedFiles") == "0",
            "inherited protected TEMP validation removes zero synthetic files");
        AssertBinding(inheritedActions, actionNonce);
        AssertReceiptAcl(identity);
        AssertReceiptAcl(actions);
        AssertProtectedParent(parent);

        foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544", "S-1-1-0", "S-1-5-21-1-2-3-9999", "not-a-sid" })
            RejectUnchanged(identity, () => LimitedIdentity(identity, identityNonce, sid), "wrong/system/group expected SID is refused: " + sid);
        foreach (string nonce in new[] { Guid.NewGuid().ToString("N"), "not-a-nonce", "0000" })
            RejectUnchanged(identity, () => LimitedIdentity(identity, nonce, Runner.Sid), "missing/malformed/stale nonce is refused: " + nonce);
        RejectUnchanged(identity, () => Runner.RunLimited(_copiedHelper, "-ResolveUserIdentity", "-ReceiptPath", identity,
            "-RequireProtectedReceipt", "-RequireOriginalUserToken", "-ExpectedUserSid", Runner.Sid), "protected identity without nonce is refused");
        RejectUnchanged(identity, () => Runner.RunLimited(_copiedHelper, "-ResolveUserIdentity", "-ReceiptPath", identity,
            "-RequireProtectedReceipt", "-RequireOriginalUserToken", "-ReceiptNonce", identityNonce), "protected identity without expected user SID is refused");
        if (privileged) RejectUnchanged(identity, () => Runner.RunCurrent(_copiedHelper, ["-ResolveUserIdentity", "-ReceiptPath", identity,
                "-RequireProtectedReceipt", "-RequireOriginalUserToken", "-ReceiptNonce", identityNonce, "-ExpectedUserSid", Runner.Sid]),
                "an elevated helper cannot impersonate the original-user protected transport");
        string wrong = Path.Combine(parent, "wrong-receipt.ini");
        Check(LimitedIdentity(wrong, identityNonce, Runner.Sid).ExitCode != 0 && !File.Exists(wrong), "wrong receipt filename cannot create a new channel");
        string missing = Path.Combine(parent, "is-missing", IdentityLeaf);
        Check(LimitedIdentity(missing, identityNonce, Runner.Sid).ExitCode != 0 && !Directory.Exists(Path.GetDirectoryName(missing)),
            "missing/untrusted receipt parent cannot be created by transport");
        RejectUnchanged(identity, () => LimitedActions(identity, identityNonce, Runner.Sid, _root), "identity channel cannot be used as an actions channel");
        RejectUnchanged(actions, () => LimitedIdentity(actions, actionNonce, Runner.Sid), "actions channel cannot be used as an identity channel");
        RejectUnchanged(actions, () => LimitedActions(actions, actionNonce, Runner.Sid, _fixture),
            "broad non-app root is refused with nonzero exit and its contained receipt cannot be overwritten");

        if (privileged)
        {
            // No data switches are present; replay cannot authorize cleanup.
            string fresh = Guid.NewGuid().ToString("N");
            Prepare(actions, fresh);
            RejectUnchanged(actions, () => LimitedActions(actions, actionNonce, Runner.Sid, _root), "old receipt nonce cannot authorize another validation/root");
            Check(LimitedActions(actions, fresh, Runner.Sid, _root).ExitCode == 0, "fresh nonce works after channel re-preparation");
            AssertBinding(ReadIni(actions), fresh);
            TestPreparationRefusals(parent, identityNonce);
            TestReparseRefusals(parent, identity, identityNonce);
        }
        else
        {
            // Existing writable channels may be edited, not created/replaced.
            // Deliberately remove a nonce, prove refusal, then restore the same
            // original admin-prepared binding through an OPEN_EXISTING write.
            byte[] original = File.ReadAllBytes(actions);
            WriteExisting(actions, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[Result]\r\nSuccess=1\r\n")).ToArray());
            RejectUnchanged(actions, () => LimitedActions(actions, actionNonce, Runner.Sid, _root), "malformed nonce-less receipt cannot authorize validation of another root");
            WriteExisting(actions, original);
            Check(LimitedActions(actions, actionNonce, Runner.Sid, _root).ExitCode == 0, "original admin-prepared binding works after safe existing-file restoration");
            AssertBinding(ReadIni(actions), actionNonce);
        }
        Check(Runner.Executions.Where(item => !item.Elevated).All(item => item.UserSid == Runner.Sid),
            "every limited child used the same real shell SID with no elevated fallback");
    }

    private static void WriteExisting(string path, byte[] bytes)
    {
        AssertFixturePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush();
    }

    private static void TestPreparationRefusals(string validParent, string nonce)
    {
        string wrongLeaf = Path.Combine(validParent, "not-a-receipt.ini");
        Check(PrepareCall(wrongLeaf, nonce).ExitCode != 0 && !File.Exists(wrongLeaf), "preparation refuses any filename except its two fixed receipt leaves");
        string wrongParent = Directory.CreateDirectory(Path.Combine(_fixture, "not-an-inno-temp")).FullName;
        ProtectDirectory(wrongParent, new SecurityIdentifier(Runner.Sid), FileSystemRights.ReadAndExecute);
        ProtectHelper(CopyHelper(wrongParent));
        string receipt = Path.Combine(wrongParent, IdentityLeaf);
        Check(PrepareCall(receipt, nonce).ExitCode != 0 && !File.Exists(receipt), "preparation refuses a protected folder with the wrong name");
        string noUser = Directory.CreateDirectory(Path.Combine(_fixture, "is-" + Guid.NewGuid().ToString("N")[..10] + ".tmp")).FullName;
        ProtectDirectory(noUser, null, FileSystemRights.ReadAndExecute);
        ProtectHelper(CopyHelper(noUser));
        receipt = Path.Combine(noUser, IdentityLeaf);
        Check(PrepareCall(receipt, nonce).ExitCode != 0 && !File.Exists(receipt), "parent with only privileged/root SIDs and no original-user RX grant is refused");
        string nonceReceipt = Path.Combine(validParent, IdentityLeaf);
        RejectUnchanged(nonceReceipt, () => PrepareCall(nonceReceipt, "invalid"), "malformed preparation nonce cannot replace a valid channel");
    }

    private static void TestReparseRefusals(string parent, string identity, string nonce)
    {
        string linkParent = Path.Combine(_fixture, "is-" + Guid.NewGuid().ToString("N")[..10] + ".tmp");
        AssertFixturePath(linkParent, mayBeMissing: true);
        Directory.CreateSymbolicLink(linkParent, parent); // both endpoints in this exact new fixture
        Check(File.GetAttributes(linkParent).HasFlag(FileAttributes.ReparsePoint), "fixture contains a real internal directory reparse point");
        string viaLink = Path.Combine(linkParent, IdentityLeaf);
        RejectUnchanged(identity, () => PrepareCall(viaLink, nonce), "preparer refuses a reparse parent before replacing a receipt");
        RejectUnchanged(identity, () => LimitedIdentity(viaLink, nonce, Runner.Sid), "limited helper refuses a receipt reached through a reparse parent");
        string fileParent = Directory.CreateDirectory(Path.Combine(_fixture, "is-" + Guid.NewGuid().ToString("N")[..10] + ".tmp")).FullName;
        ProtectDirectory(fileParent, new SecurityIdentifier(Runner.Sid), FileSystemRights.ReadAndExecute);
        ProtectHelper(CopyHelper(fileParent));
        string fileLink = Path.Combine(fileParent, IdentityLeaf);
        AssertFixturePath(fileLink, mayBeMissing: true);
        File.CreateSymbolicLink(fileLink, identity);
        RejectUnchanged(identity, () => PrepareCall(fileLink, nonce), "preparer refuses an existing reparse receipt without deleting the link or touching its target");
        RejectUnchanged(identity, () => LimitedIdentity(fileLink, nonce, Runner.Sid), "limited helper refuses a reparse receipt file");
        Check(File.GetAttributes(fileLink).HasFlag(FileAttributes.ReparsePoint), "refused receipt link is retained for inspection");
    }

    private static ShellTokenRunner Runner => _runner ?? throw new InvalidOperationException("Shell runner missing.");
    private static ChildExecution LimitedIdentity(string receipt, string nonce, string sid) => Runner.RunLimited(_copiedHelper,
        "-ResolveUserIdentity", "-ReceiptPath", receipt, "-RequireProtectedReceipt", "-RequireOriginalUserToken", "-ReceiptNonce", nonce, "-ExpectedUserSid", sid);
    private static ChildExecution LimitedActions(string receipt, string nonce, string sid, string root) => Runner.RunLimited(_copiedHelper,
        "-ValidateOnly", "-DataRoot", root, "-ReceiptPath", receipt, "-RequireProtectedReceipt", "-RequireOriginalUserToken", "-ReceiptNonce", nonce, "-ExpectedUserSid", sid);
    private static ChildExecution PrepareCall(string receipt, string nonce) => Runner.RunCurrent(_copiedHelper,
        ["-PrepareProtectedReceipt", "-ReceiptPath", receipt, "-ReceiptNonce", nonce]);
    private static void Prepare(string receipt, string nonce)
    {
        Check(PrepareCall(receipt, nonce).ExitCode == 0, "elevated preparation succeeds for the exact channel: " + Path.GetFileName(receipt));
        var values = ReadIni(receipt);
        Check(Value(values, "Transport", "UserSid") == Runner.Sid && Value(values, "Transport", "ReceiptNonce") == nonce,
            "preparer derives the original SID from the protected parent ACL and preserves the nonce");
        Check(Value(values, "Result", "Success") == "0", "prepared channel starts uncommitted, never a stale success");
    }
    private static void AssertBinding(Dictionary<string, string> values, string nonce)
    {
        Check(Value(values, "Transport", "UserSid") == Runner.Sid && Value(values, "Transport", "ReceiptNonce") == nonce,
            "result preserves the verified transport's SID and nonce binding");
        Check(Value(values, "Result", "ReceiptNonce") == nonce, "result echoes the expected fresh nonce for the elevated reader");
    }
    private static void RejectUnchanged(string receipt, Func<ChildExecution> action, string message)
    {
        string before = Hash(receipt);
        Check(action().ExitCode != 0, message);
        Check(Hash(receipt) == before, "refusal cannot mutate the existing receipt: " + message);
    }
    private static void AssertReceiptAcl(string path)
    {
        AssertFixturePath(path);
        var acl = new FileInfo(path).GetAccessControl(AclReadSections);
        Check(OwnerSid(acl) == Admins.Value || OwnerSid(acl) == SystemSid.Value,
            "receipt owner remains Administrators/System, not the ordinary SID with implicit WriteDac");
        Check(acl.AreAccessRulesProtected, "receipt ACL does not inherit user directory rights");
        FileSystemRights rights = UserRights(acl);
        Check((rights & (FileSystemRights.ReadData | FileSystemRights.WriteData)) == (FileSystemRights.ReadData | FileSystemRights.WriteData),
            "receipt grants the exact original user read/write");
        Check((rights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0,
            "receipt's original user has no Delete/WriteDac/WriteOwner grant");
    }
    private static void AssertProtectedParent(string parent)
    {
        var acl = new DirectoryInfo(parent).GetAccessControl(AclReadSections);
        Check(OwnerSid(acl) == Admins.Value && acl.AreAccessRulesProtected, "protected temp has Administrators owner and a non-inheriting ACL");
        FileSystemRights rights = UserRights(acl);
        Check((rights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0,
            "protected temp's ordinary user has no write/delete/ownership grants");
        var helperAcl = new FileInfo(_copiedHelper).GetAccessControl(AclReadSections);
        Check(OwnerSid(helperAcl) == Admins.Value && helperAcl.AreAccessRulesProtected,
            "exact helper copy is Administrators-owned with a protected ACL");
        Check((UserRights(helperAcl) & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0,
            "exact helper copy has no ordinary-user write/delete/ownership grants");
    }
    private static FileSystemRights UserRights(FileSystemSecurity acl) => acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
        .Where(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value == Runner.Sid)
        .Aggregate((FileSystemRights)0, (value, rule) => value | rule.FileSystemRights);
    private static string OwnerSid(FileSystemSecurity acl) => acl.GetOwner(typeof(SecurityIdentifier))?.Value ?? "";
    private static void ProtectDirectory(string path, SecurityIdentifier? user, FileSystemRights rights)
    {
        AssertFixturePath(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(Admins);
        foreach (var sid in new[] { Admins, SystemSid }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (user is not null) acl.AddAccessRule(new FileSystemAccessRule(user, rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    private static void ProtectHelper(string path)
    {
        AssertFixturePath(path);
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(Admins);
        foreach (var sid in new[] { Admins, SystemSid }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(Runner.Sid), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
    private static void AssertFixturePath(string path, bool mayBeMissing = false)
    {
        string full = Path.GetFullPath(path), basePath = Path.GetFullPath(Path.Combine(_packaging, "offline-receipt-transport-fixtures"));
        string fixture = Path.GetFullPath(_fixture);
        if (!Regex.IsMatch(Path.GetFileName(fixture), "^[a-f0-9]{32}$") || !fixture.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !(full.Equals(fixture, StringComparison.OrdinalIgnoreCase) || full.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Path is outside the exact new workspace fixture.");
        if (!mayBeMissing && !File.Exists(full) && !Directory.Exists(full)) throw new InvalidOperationException("Expected fixture target is missing.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Fixture path contains a reparse point; no ACL changes are permitted.");
    }
    private static string CopyHelper(string parent)
    {
        string path = Path.Combine(parent, "manage-install-data.ps1");
        AssertFixturePath(path, mayBeMissing: true);
        File.Copy(_source, path, false);
        Check(Hash(path) == _sourceHash, "new helper copy exactly matches reviewed source SHA-256");
        return path;
    }
    private static string CreateData(string account)
    {
        string root = Path.Combine(_fixture, account, "SayWhat");
        Directory.CreateDirectory(Path.Combine(root, "models"));
        File.WriteAllText(Path.Combine(root, "vrchat-translation-settings.json"), "{\"Fixture\":\"" + account + "\",\"FirstRunSetupCompleted\":true}");
        File.WriteAllText(Path.Combine(root, "saywhat.log"), "synthetic, not a live log\n");
        File.WriteAllText(Path.Combine(root, "models", "Qwen3-4B-Q4_K_M.gguf"), "tiny synthetic bytes, not a model");
        return root;
    }
    private static string Snapshot(string root) => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Hash(path)));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool SamePath(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static Dictionary<string, string> ReadIni(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Check(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE, "receipt is UTF-16LE with BOM for Inno's INI reader");
        string section = "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(path, Encoding.Unicode))
        {
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            int separator = line.IndexOf('=');
            if (separator > 0 && !values.TryAdd(section + "/" + line[..separator], line[(separator + 1)..]))
                throw new InvalidOperationException("Duplicate receipt key cannot be trusted.");
        }
        return values;
    }
    private static string Value(Dictionary<string, string> values, string section, string name) => values.GetValueOrDefault(section + "/" + name, "");
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Passed.Add(message);
    }
}

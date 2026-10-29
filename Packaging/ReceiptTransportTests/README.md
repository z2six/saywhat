# Protected installer receipt tests

This plain .NET 8 console DLL tests the existing `scripts/manage-install-data.ps1` without executing setup, uninstall, or the app. It creates tiny synthetic data and receipts only under a fresh GUID directory in `Packaging/offline-receipt-transport-fixtures` (excluded from release source), and leaves every fixture, report and internal link for inspection. No live AppData, cleanup switches, model loading, audio capture, process stopping, self-elevation or recursive cleanup is permitted.

Build with one SDK slot and run the DLL, never an apphost EXE:

```powershell
dotnet build Packaging/ReceiptTransportTests/ReceiptTransportTests.csproj -c Release -m:1 -p:UseSharedCompilation=false -p:UseAppHost=false --source Packaging/tools/offline-nuget
dotnet Packaging/ReceiptTransportTests/bin/Release/net8.0-windows/ReceiptTransportTests.dll --default
```

Default mode requires the actual unelevated interactive shell account. It checks metadata-only identity, synthetic-root validation and refusal of unprepared channels, wrong filenames/SIDs and limited preparation. A nofollow directory handle sharing read/write but not delete proves that a normally permitted DELETE-access handle fails with sharing violation 32 while the directory is pinned, then works after closing the pin; no rename, deletion, owner or ACL change is performed. Its source helper copy must match SHA-256. Normal closed-app guards remain active; inability to read process metadata is a failure, never bypassed.

## Explicitly authorized protected run

The full test must be started from an **already elevated, same-account** console, using a trusted installed `dotnet.exe`. The test never asks for UAC or elevates itself. An alternate administrator account, unavailable/foreign/elevated Explorer shell, non-NTFS fixture, or unexpected link in a fixture path is refused. A caller supervising one approved UAC launch may run the following DLL with `--protected`; a normal unelevated invocation will fail safely.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' 'C:\VRChat\FoxTrans-v2.0.0-win-x64\Packaging\ReceiptTransportTests\bin\Release\net8.0-windows\ReceiptTransportTests.dll' --protected
```

Only this new fixture receives ACL changes. Its outer folder and report remain accessible to the original user. A child named `is-<10 hex characters>.tmp` reproduces Inno's protected temp: Administrators ownership, protected ACL, Administrators/System full control, exactly one ordinary shell SID with read/execute. The exact helper copy is similarly protected. Elevated preparation creates only `saywhat-data-identity.ini` and `saywhat-data-actions.ini`, each with a fresh 32-hex nonce and uncommitted result. File ownership must remain Administrators/System so the ordinary user cannot gain implicit owner permission changes.

The broker duplicates the verified, same-session Explorer primary token and launches only absolute System32 Windows PowerShell, hidden, without loading a profile. It tries `CreateProcessAsUser` then `CreateProcessWithToken` with the **same limited token**, checks the child's SID/elevation, supplies only synthetic app-root overrides and fixture-local `TEMP`/`TMP`, and waits at most 30 seconds per child. For every test child, `PSModulePath` is scoped to that trusted Windows PowerShell's built-in `Modules` directory, so inherited PowerShell 7 paths cannot break Windows PowerShell 5.1 module auto-loading; no global environment setting changes. There is no elevated fallback or process termination on timeout. The fixed access probe only opens handles, plus the original receipt-write attempt expected to fail; an unexpectedly created tiny sibling is retained as evidence.

Coverage proves old receipt creation is access-denied; both prepared files are readable/writable; the parent/helper cannot be written, deleted, replaced or have permissions/ownership changed by the limited user; receipts cannot be deleted/recreated or have permissions/ownership changed; the elevated reader sees correct identity and zero-change validation results/nonces; and wrong SID/name/nonce, replay, missing channel, privileged-only parent, invalid root, real internal directory/file reparse points and elevated-original-user fallback are refused. Validation has no destructive choices and omits private-root enforcement only for these synthetic fixtures. Production data changes still require private-root verification. Existing same-account elevated standalone uninstall behavior is not changed by these tests.

Each run writes `results.json`; protected runs also write `access-probe.json`. A failed run is not proof of protection. Report full protected coverage as pending until an explicitly approved elevated run passes. These fixtures are deliberately not cleaned up automatically; do not remove broad workspace directories or change any pre-existing ACL to run the tests.

## Limited continuation after successful preparation

If Windows refuses the custom test token broker after both elevated preparations succeeded, the real unelevated shell account may explicitly continue that exact existing fixture. This does **not** elevate, prepare channels, change ACLs, create links, or launch the broker again:

```powershell
dotnet Packaging/ReceiptTransportTests/bin/Release/net8.0-windows/ReceiptTransportTests.dll --limited-existing 0123456789abcdef0123456789abcdef
```

Replace the example GUID with the GUID from your explicitly approved preparation report. The argument must be a lowercase 32-hex fixture GUID, never a path. Before any child runs or report is written, the continuation verifies the original elevated report, matching shell SID/current source SHA-256, exact protected helper copy, canonical non-reparse fixture paths, Administrators-owned protected parent/helper, both secured existing receipt files and original-user SID/nonce bindings. An uncommitted `Success=0` channel or a previously completed `Success=1` channel is accepted; completed results must echo the same nonce and identify only the same synthetic account or zero removals. It runs the access probe, identity, validation and SID/nonce/name/mode/missing-parent/invalid-root negatives through the already verified current limited token. A broad invalid root containing the receipt must fail with nonzero exit and leave the existing receipt unchanged; the receipt alone can never override a failed helper exit. A nonce-less malformed receipt is refused, then restored using **open existing** only. Both synthetic data sets stay unchanged. It writes `limited-results.json` separately; the original administrator preparation/broker-failure report is retained. A passing continuation proves that measured boundary, not the skipped broker, further elevated preparation, elevated-fallback or real reparse tests. Actual Inno setup uses `ExecAsOriginalUser`, not this custom test broker.

Native API references: [CreateProcessWithTokenW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw), [DuplicateTokenEx](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-duplicatetokenex).

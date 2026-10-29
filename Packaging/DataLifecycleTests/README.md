# Offline installer data tests

This small .NET 8 test program calls the existing reviewed `scripts/manage-install-data.ps1` through hidden Windows PowerShell 5.1. It creates only tiny synthetic files under a unique `Packaging/offline-lifecycle-fixtures` directory and leaves them for inspection. It does not use live AppData, execute an installer, load models, capture audio, create links, stop processes, recursively delete a test directory, or change security protection.

```powershell
dotnet build Packaging/DataLifecycleTests/DataLifecycleTests.csproj -c Release -m:1 -p:UseSharedCompilation=false -p:UseAppHost=false
dotnet Packaging/DataLifecycleTests/bin/Release/net8.0-windows/DataLifecycleTests.dll
```

The helper's normal closed-app/process guards remain active. The test fails rather than bypassing those guards when process metadata cannot be read or translation tools are running. Permission to read Windows process metadata may be needed. The program does not request permission or elevate itself.

Coverage includes independent keep/reset/history/cache choices, exact recognized profile backups and model partials (including TranslateGemma and Liquid), external imported-model preservation, retained unknown files/runtime/WSL registration/setup checkpoint, readiness-only settings rewrite after cache deletion, UTF-16 INI receipts, validation-only no changes, and preflight refusal for locks, read-only files, invalid/non-object JSON, wrong file types, unsupported roots, receipt overlap and the compatible desktop guard. Pure functions extracted from the reviewed helper also exercise fresh/legacy/override root selection, same-account and alternate-UAC shell identities, unknown/ambiguous identity and foreign/shared/custom-root rejection using only synthetic directories. Bound-helper negatives check wrong/malformed SID and an arbitrary synthetic SayWhat folder outside the current account's private location. A second synthetic account remains unchanged when the selected account is cleared. Identity-resolution mode cannot be combined with destructive choices.

Actual install/update/uninstall execution and live user data are intentionally untested here. Reparse-point defenses are reviewed in the helper source; this simplified harness does not create junctions or symbolic links. The earlier quarantined PowerShell harness was never executed and is not required or redistributed by these tests. Do not restore it or change security protection to run tests.

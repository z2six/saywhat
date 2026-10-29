# Windows installer build

The installer bundles the Windows app, capture host, verified Vulkan translation
runtime, helpers, notices and matching source. AI model weights are downloaded
through the app and are not bundled. Building an installer does not install it,
change personal data, open audio capture or load models.

For the app build and offline checks, start with
[development](../docs/development.md). Ordinary users should download an existing
[release](https://github.com/z2six/saywhat/releases).

## Prerequisites

- Windows x64 and the app build prerequisites: .NET SDKs 8 and 9, CMake 3.21+
  and Visual Studio 2022 or newer C++ build tools, with a CMake version that
  supports that Visual Studio release.
- PowerShell 7 for the packaging scripts. They use APIs unavailable in Windows
  PowerShell 5.1; `eng/Build.ps1` itself supports 5.1.
- Inno Setup. The release compiler used for the existing previews is 6.7.3.
  Install it from [the official release](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3),
  or provide an existing `ISCC.exe` with `-CompilerPath`. The 6.7.3 installer has
  SHA-256 `9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`.
  No script in this workflow silently installs that compiler.

Build the app and host from source, then fetch the verified runtime archive:

```powershell
.\eng\Build.ps1 -Publish -RunTests
.\eng\Prepare-Packaging.ps1
```

The second command writes only `Packaging/tools/`. It downloads the official
`llama-b11401-bin-win-vulkan-x64.zip`: 33308209 bytes, SHA-256
`4bdbc0b2e79f04ef3e66ecd34a996499401d94501e8387dec0c8963544ef6419`.
An existing file must match those values before it can be reused.

## Create the installer

From the repository root in PowerShell 7:

```powershell
$version = ([xml](Get-Content .\LocalTalk\LocalTalk.csproj -Raw)).Project.PropertyGroup.Version
.\Packaging\Build-Installer.ps1 -Version $version -SkipPublish `
  -AppPublishDirectory "$PWD\artifacts\publish\app" `
  -HostPublishDirectory "$PWD\artifacts\publish\host" `
  -CompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

`-SkipPublish` here reuses the fresh self-contained output from the preceding
source build. The installer script verifies the app version, payload, license
files, pinned runtime and corresponding-source contents before compiling.
It produces `Packaging/dist/SayWhat-<version>-Setup.exe` and a unique
`Packaging/build-*` stage. Version changes must agree in the app project,
`IncomingTranslator/AppBrand.cs`, `Packaging/SayWhat.iss` and the build script's
default. Never relabel an older binary as a new version.

## Validate and export

Record the installer and staged source archive's SHA-256, then run
`Export-Release.ps1` with the selected version, installer, exact build stage and
those two expected hashes. `-ValidateOnly` checks without creating an export.
Without that switch it creates a complete recipient ZIP with notices, matching
source, checksums and provenance. See [release distribution](Release-Distribution.md).

Keep generated stages, fetched tools, test reports and distribution files out
of Git. Do not substitute personal model caches or installed-app folders for
build inputs. The root `.gitignore` and source audit reject that data.

## Installer behavior and acceptance checks

Install, update, repair and uninstall share the registered app identity. Shared
program files and each account's personal data are separate. Data removal is
explicit, scoped to a verified initiating account and unchecked by default.
An update keeps settings/models and requests a graceful shutdown of owned tools.
The updater verifies the installer before its silent update handoff; administrator
consent and errors remain visible. It restarts the app idle, without loading models.

The protected data helper's account, receipt, ACL and no-follow-handle checks
are deliberate security boundaries. Read `InstallerDataFlow.iss`,
`scripts/manage-install-data.ps1` and the fixture projects before changing them.

Offline tests do not certify a fresh-PC installation. Release acceptance also
needs just-me/all-users installation, tray-resident update/uninstall, UAC and
alternate-account paths, cancellation/retry, four interface languages, cached
versus first-time model setup, supported GPU backends and real VRChat use.
These are preview builds and are currently unsigned.

# Development

SayWhat? is a Windows desktop application. This repository contains the current
app source, its FoxTrans-derived capture host, native C capture code, installer
scripts, website and tests. Existing released installers have separate matching
source archives; see [source provenance](../SOURCE-PROVENANCE.md).

## Prerequisites

For the primary app and host:

- Windows 10 21H2 (build 19044) or newer, or Windows 11, x64. Your own voice and
  explicit All playback subtitles can use Windows 10; process-specific playback
  capture requires the Windows 11 client API (build 20348+). Windows 10 support
  is compatibility-preview work, not a claim of verified real-Windows-10 testing.
- .NET SDKs **8 and 9**. The dashboard targets `net8.0-windows`; the capture host
  targets `net9.0`. A machine that builds framework-dependent output also needs
  the corresponding desktop/runtime components to run it; the SDK installers
  provide the development environment. `-Publish` produces self-contained output.
- Visual Studio 2022 or newer with **Desktop development with C++**, including
  a Windows SDK, plus **CMake 3.21+** that supports that Visual Studio release.
  The build selects an installed supported generator; native dependencies are vendored.
- PowerShell 5.1 or newer for `eng/Build.ps1`.
- Node.js 22+ for the dependency-free website and repository checks. CI uses 24.

`FoxTransSource` retains upstream projects and their own SDK 10 configuration.
Building that standalone upstream solution is separate from the primary app
build below. The SayWhat capture project compiles the selected core files
directly and does not require the upstream desktop app or its test solution.

No LM Studio, separate FoxTrans app, cloud API key, downloaded model weights,
WSL installation or GPU is needed to compile and run the offline checks.

## Build

From the repository root:

```powershell
.\eng\Build.ps1 -RunTests
```

The script builds the native library from `FoxTransSource/native`, copies the
generated DLL into its ignored build location, restores NuGet dependencies,
builds both managed projects and assembles their expected helper layout:

```text
artifacts/
  native/                 CMake build and generated capture DLL
  build/app/SayWhat.exe   Framework-dependent dashboard and subtitle app
  build/app/tools/        Owned capture host and OSC/mute helpers
  build/host/             Framework-dependent capture host output
  publish/app/            Self-contained app and tools with -Publish
  publish/host/           Self-contained capture host with -Publish
  test-results/           Isolated checks and rendered test screens
```

For self-contained Windows output:

```powershell
.\eng\Build.ps1 -Publish -RunTests
```

Build and publish outputs stay separate. Reusing one directory can leave a local
`hostfxr.dll` beside a framework-dependent executable, causing .NET to look for
shared frameworks in that directory and fail before the app starts. Old
`artifacts/app` and `artifacts/host` folders are no longer used; no automatic
cleanup deletes them.

Useful options are `-Configuration Debug`, explicit `-DotNetPath`, `-NodePath`
or `-CMakePath`, and `-CMakeGenerator` for an already-configured compatible
toolchain. `-SkipNative` is only an iteration shortcut when the generated native
DLL already exists. It is not clean-source validation and CI does not use it.

You can open [SayWhat.sln](../SayWhat.sln) in Visual Studio after the first native
build. Do not build the unrelated upstream solution by accident. Internal folder
and namespace names such as `LocalTalk` remain for compatibility; the displayed
product and executable are SayWhat? / `SayWhat.exe`.

## Run a development build

After building, run `Start-SayWhat.bat` from this checkout. It opens the idle UI
and uses `artifacts/dev-data` instead of the installed app's preferences. It does
not automatically start translation. Model preparation, downloads and capture
still require explicit actions in the app.

The launcher prefers `artifacts/build/app` and falls back to
`artifacts/publish/app` when no development build exists. Use the path printed
by the build script when you want a particular output.

The installed app and developer copy share single-instance/port conventions.
Close the installed copy before intentionally testing a developer UI; do not
force-stop a live translation session or start competing local servers.

Never point tests at your real settings folder. The developer launcher and
offline test harness provide separate data locations, but launching an EXE
directly without an override uses the normal app data path.

## Offline checks and live acceptance

`-RunTests` runs:

- Dashboard, settings, migration, layout, theme, model-selection, queue, identity,
  update and fake-provisioning checks through `--ui-self-test`.
- Capture-host regressions through its `--self-test` entry point.
- Owned-listener readiness and port handoff through the separate
  [ManagedPortTests project](../Packaging/ManagedPortTests/ManagedPortTests.csproj).
  It uses temporary loopback sockets and two hidden fixture processes, checks
  exact process ownership, and closes those processes when it finishes. Its
  external script and runner are developer test files, excluded from the app.
- Isolated VRChat mute discovery/policy, installer-update and Windows-version source contracts.
- Static website release-gate and eight-language checks.

The harness sets isolated `SAYWHAT_DATA_ROOT`, temporary and report directories.
It does not load speech/translation models, open microphone/playback capture,
send chat to VRChat, install Windows components or modify the installed app.
NuGet restore can access package servers; “offline checks” describes the tested
runtime behavior, not a promise that dependency restore needs no network.

After a source build, run that connection fixture separately with:

```powershell
dotnet run --project .\Packaging\ManagedPortTests\ManagedPortTests.csproj -c Release -- .\artifacts\build\app .\artifacts\test-results\managed-ports-manual
```

Use `artifacts/publish/app` instead when checking a self-contained publish.
The fixture loads the already-built application assembly for its ownership
checks; it does not launch the app UI or translation engines. Reports and data
stay under the supplied isolated test directory.

The CI workflow runs the same source build and offline checks on Windows, plus
the public-source audit. It has read-only permissions and does not deploy,
publish releases or require secrets. To run the source audit locally:

```powershell
node .\eng\Check-PublicSource.mjs --self-test
node .\eng\Check-PublicSource.mjs
```

The second command inspects the **Git index**, including newly staged files.
Stage intended changes before using it as a pre-publication check. It checks
known secret/private-file patterns and relative documentation links; a passing
scan does not replace reviewing the diff.

Real recognition quality, end-to-end delay, overlapping conversation, device
changes, GPU use and first-install/UAC behavior require separate live acceptance
tests. Use consenting test participants and keep recordings or diagnostic
exports local unless they explicitly agree to sharing.

## Finding the right place to change

Read the [codebase guide](codebase-guide.md) before changing pipeline behavior.
Common entry points:

| Change | Start here |
|---|---|
| Home/onboarding/other everyday controls | `LocalTalk/VoiceFirstHome.cs`, `FirstRunSetupView.cs`, `MainWindow.Onboarding.cs` |
| Defaults, migration and persistence | `IncomingTranslator/AppSettings.cs` |
| Runtime ownership, start/stop or model replacement | `LocalTalk/RuntimeController.cs`, `RuntimeProfiles.cs`, `MainWindowProfileRuntime.cs` |
| Own-voice speech and message scheduling | `FoxTransLocalHost/Program.cs`, `FoxTransSource/FoxTrans/Providers` |
| Incoming capture or stalled subtitles | `IncomingTranslator/Program.Fixed.cs`, `IncomingContinuousAudio.cs` and the selected backend partial |
| Person corrections and evidence | `IncomingTranslator/AiSpeakerIdentifier.cs`, `SpeakerFeedback.cs`, `IncomingSpeakerReviews.cs` |
| Speech models and language support | `OwnRecognitionCatalog.cs`, `IncomingRecognitionCatalog.cs` and their provisioners |
| Translation models and request formats | `LocalTalk/Provisioning.cs`, `TranslationModelCapabilities.cs`, `TranslationModelPrompts.cs` |
| Installer/account-data behavior | `Packaging/InstallerDataFlow.iss`, `Packaging/scripts/manage-install-data.ps1` |

When adding a model, validate its real source/target languages independently of
other models. Pin the exact upstream revision, artifact size and SHA-256, retain
the applicable license and do not commit weights. Keep download preparation
separate from inference startup. Test compatibility, missing assets,
cancellation and cleanup without starting a real model in normal CI.

For scheduling changes, test both the final and preview paths. Preserve audio
timestamps, bounded queues, manual identity authority and process ownership.
Do not “fix” a stall by reopening muted capture, trusting any process on a port,
dropping every unfinished sentence or repeatedly replaying already-recognized
audio. Add a focused regression that demonstrates the failed invariant.

For UI changes, retain the four app languages, supported themes, accessibility
names and clear pending/applying/completed states. Internal diagnostics belong in
Troubleshooting; primary controls should explain outcomes in ordinary language.

## Packaging and website

See [the packaging guide](../Packaging/README.md) for PowerShell 7, Inno Setup,
the verified runtime download and installer/export commands. Installer builds
are separate from the default CI workflow and do not automatically install or
upload anything. Keep old releases and their corresponding source unchanged.

Maintainers can manually run the **Unreleased installer candidate** GitHub Actions
workflow for a selected source commit. It builds and tests self-contained native
outputs, prepares the pinned packaging runtime and uses an already-installed Inno
Setup compiler. Its review artifact contains the verified installer/source export,
the exact raw app DLL for antivirus analysis, the source commit and SHA-256
manifest. It does not install the app, publish a GitHub release, deploy the website
or change the update feed. Candidate artifacts expire after 14 days.

The [website guide](../Website/README.md) covers its static source, localization,
offline checks and deployment. Website tests use only Node's built-in APIs; no
package installation is required. Maintainer publishing credentials stay outside
the checkout.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$')]
    [string]$Version,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$StageDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedInstallerSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedSourceSha256,
    [string]$NuGetCachePath = (Join-Path $env:USERPROFILE '.nuget\packages'),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packagingRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$stage = [IO.Path]::GetFullPath($StageDirectory)
$installer = [IO.Path]::GetFullPath($InstallerPath)
if (-not $stage.StartsWith($packagingRoot + '\build-', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a validated Packaging/build-* stage, not a live app/data directory.'
}
if (-not $installer.StartsWith($packagingRoot + '\dist\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($installer) -ne ('SayWhat-' + $Version + '-Setup.exe')) {
    throw 'Installer filename/path must match the selected version under Packaging/dist.'
}
$payload = Join-Path $stage 'payload'
$stagedSource = Join-Path $stage 'source'
$sourceZip = Join-Path $payload 'source\SayWhat-Corresponding-Source.zip'
foreach ($path in @($installer, $sourceZip, (Join-Path $payload 'SayWhat.deps.json'),
    (Join-Path $payload 'tools\FoxTransLocalHost\FoxTrans.LocalHost.deps.json'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing release input: ' + $path) }
}
function Digest([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
if ((Digest $installer) -ne $ExpectedInstallerSha256.ToLowerInvariant()) { throw 'Installer SHA-256 mismatch. Do not relabel or publish unreviewed bytes.' }
if ((Digest $sourceZip) -ne $ExpectedSourceSha256.ToLowerInvariant()) { throw 'Corresponding-source SHA-256 mismatch. Use the source paired with this binary.' }
$privateNames = '(^|[\\/])(preview|data|qa|test-artifacts|bin|obj|publish|[^\\/]*-publish(?:-[^\\/]*)?|[^\\/]*-build(?:-[^\\/]*)?|active|[.]git|[.]agents|[.]codex|dist|offline-[^\\/]*|build-[^\\/]+|release-export-[^\\/]+|[^\\/]*-checks?(?:-[^\\/]*)?|[^\\/]*-backup-[^\\/]*)[\\/]|(^|[\\/])Test-InstallerDataLifecycle[.]ps1$|vrchat-translation-settings[.]json|incoming-speaker-feedback[.]json|incoming-ai-speaker-profiles[.]json|provisioning[.]json|setup-progress[.]json|config[.].*jsonc|[.]log$|[.]gguf$|[.]onnx$|[.]wav$|[.]mp3$|[.]flac$|[.]ogg$|[.]m4a$'
$requiredSource = @('LocalTalk/LocalTalk.csproj','LocalTalk/MainWindow.cs','LocalTalk/RuntimeController.cs',
    'IncomingTranslator/AppSettings.cs','IncomingTranslator/AppearanceTheme.cs','IncomingTranslator/ThemeBackground.cs',
    'FoxTransLocalHost/Program.cs','FoxTransLocalHost/FoxTransLocalHost.csproj','FoxTransLocalHost/README.md',
    'FoxTransSource/LICENSE','FoxTransSource/FoxTrans/FoxTrans.csproj','FoxTransSource/native/CMakeLists.txt',
    'FoxTransSource/native/vendor/libfvad/LICENSE','FoxTransSource/native/vendor/libfvad/AUTHORS',
    'FoxTransSource/native/vendor/libfvad/PATENTS','FoxTransSource/native/vendor/miniaudio/LICENSE',
    'Packaging/Build-Installer.ps1','Packaging/SayWhat.iss')
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.15') {
    $requiredSource += @('LocalTalk/AppUpdateChecker.cs','LocalTalk/AppUpdateInstaller.cs','LocalTalk/AppUpdateTests.cs',
        'LocalTalk/VrchatOscConnection.cs','LocalTalk/VrchatOscConnectionTests.cs',
        'FoxTransSource/FoxTrans/Providers/TranslationModelCapabilities.cs',
        'FoxTransSource/FoxTrans/Providers/TranslationModelPrompts.cs',
        'FoxTransSource/FoxTrans/Providers/SpecializedTextTranslator.cs',
        'FoxTransSource/FoxTrans/Providers/MultiTargetTextTranslator.cs','FoxTransLocalHost/MultiLanguageTests.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.16') {
    $requiredSource += @('LocalTalk/HelpUi.cs','LocalTalk/HelpUiTests.cs','LocalTalk/TimingDefaultsTests.cs',
        'Packaging/InstallerDataFlow.iss','Packaging/scripts/manage-install-data.ps1')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.17') {
    $requiredSource += @('Packaging/ReceiptTransportTests/ReceiptTransportTests.csproj',
        'Packaging/ReceiptTransportTests/Program.cs','Packaging/ReceiptTransportTests/README.md',
        'Packaging/ReceiptTransportTests/ShellTokenRunner.cs','Packaging/ReceiptTransportTests/Probe-ReceiptAccess.ps1')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.18') {
    $requiredSource += @('LocalTalk/SetupSelection.cs','LocalTalk/SetupChoiceUiTests.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.19') {
    $requiredSource += @('LocalTalk/SpeakerQuickPicker.cs','LocalTalk/SpeakerQuickPickerTests.cs',
        'LocalTalk/SpeakerRecencyTests.cs','IncomingTranslator/SpeakerFeedback.cs','IncomingTranslator/AiSpeakerIdentifier.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.20') {
    $requiredSource += @('IncomingTranslator/SubtitleWindowPlacement.cs','LocalTalk/SubtitleWindowControlsTests.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.21') {
    $requiredSource += @('LocalTalk/MainWindow.Onboarding.cs','LocalTalk/FirstRunSetupView.cs',
        'LocalTalk/FirstRunSetupViewTests.cs','LocalTalk/FirstRunTour.cs','LocalTalk/FirstRunTourTests.cs',
        'LocalTalk/OnboardingIntegrationTests.cs','IncomingTranslator/SubtitleQuickGuide.cs','LocalTalk/SubtitleQuickGuideTests.cs',
        'LocalTalk/PeopleUi.cs','LocalTalk/PeopleUiTests.cs','IncomingTranslator/PersonAvatar.cs','IncomingTranslator/PersonAvatarTests.cs',
        'Packaging/Test-UpdateInstallerContract.ps1')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.22') {
    $requiredSource += @('LocalTalk/PersonPhotoEditor.cs','LocalTalk/PersonPhotoEditorTests.cs',
        'LocalTalk/ManagedSpeechIntegration.cs','LocalTalk/ManagedSpeechIntegrationTests.cs',
        'Packaging/scripts/hide-speech-desktop-entries.sh','Packaging/scripts/test-hidden-desktop-entries.sh')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.23') {
    $requiredSource += @('LocalTalk/Diagnostics.cs','LocalTalk/MainWindow.Diagnostics.cs','LocalTalk/DiagnosticsTests.cs',
        'IncomingTranslator/Program.Fixed.cs','IncomingTranslator/SmokeTests.cs','FoxTransLocalHost/Program.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.11') {
    $requiredSource += @('IncomingTranslator/ManagedServicePorts.cs','IncomingTranslator/SessionEndpoints.cs','LocalTalk/RuntimeController.Ports.cs','LocalTalk/MainWindowConnections.cs','LocalTalk/ManagedServicePortTests.cs','LocalTalk/SessionEndpointTests.cs','Packaging/ProvisioningTests/test-managed-ports.py','Packaging/vibevoice/test_ports.py',
        'Packaging/ManagedPortTests/ManagedPortTests.csproj','Packaging/ManagedPortTests/Program.cs','Packaging/ManagedPortTests/fixtures/owned-http.ps1')
}
$privateNames += '|(^|[\\/])people-photos[\\/]'
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.25') {
    $requiredSource += @('LocalTalk/HomeTargetLanguageUi.cs','LocalTalk/HomeTargetLanguageUiTests.cs',
        'LocalTalk/SettingsStore.HomeLanguages.cs','LocalTalk/MainWindow.SaveState.cs','LocalTalk/SaveStateUiTests.cs',
        'IncomingTranslator/PersonAvatarSeed.cs','IncomingTranslator/GeneratedPersonAvatar.cs',
        'LocalTalk/PersonAvatarSeedTests.cs','LocalTalk/GeneratedPersonAvatarTests.cs','Packaging/Test-UpdateInstallerContract.ps1')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.1.26') {
    $requiredSource += @('IncomingTranslator/IncomingSpeakerReviews.cs',
        'LocalTalk/SpeakerSuggestionReviewUi.cs','LocalTalk/SpeakerSuggestionOverlayTests.cs',
        'LocalTalk/SpeakerSuggestionReviewTests.cs','LocalTalk/SpeakerReviewPeopleUiTests.cs','LocalTalk/SpeakerReviewServiceTests.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.0') {
    $requiredSource += @('LocalTalk/VoiceFirstHome.cs','LocalTalk/VoiceFirstText.cs','LocalTalk/VoiceFirstHomeUiTests.cs',
        'LocalTalk/MainWindow.VoiceFirstRuntime.cs','LocalTalk/NativeSpeechProvisioner.cs',
        'LocalTalk/NativeSpeechProvisionerTests.cs','LocalTalk/OwnRecognitionCatalog.cs',
        'FoxTransSource/FoxTrans/Providers/NemotronTranscriber.cs','FoxTransLocalHost/NativeSpeechTests.cs',
        'Packaging/licenses/OpenMDW-1.1.txt')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.2') {
    $requiredSource += @('FoxTrans-VRChatMuteSync.ps1','Packaging/Test-VRChatMuteSync-Discovery.ps1',
        'LocalTalk/Diagnostics.cs','LocalTalk/DiagnosticsTests.cs','LocalTalk/VoiceFirstHomeUiTests.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.3') {
    $requiredSource += @('IncomingTranslator/IncomingRecognitionCatalog.cs','IncomingTranslator/IncomingNativeSpeech.cs',
        'LocalTalk/IncomingNativeSpeechTests.cs','LocalTalk/FireRedSpeechProvisioner.cs','LocalTalk/SherpaSpeechRecognizer.cs',
        'LocalTalk/FireRedIncomingSpeech.cs','LocalTalk/FireRedSpeechTests.cs',
        'LocalTalk/SubtitleSpeechUi.cs','LocalTalk/SubtitleSpeechText.cs','LocalTalk/SubtitleSpeechUiTests.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.4') {
    $requiredSource += @('LocalTalk/HomeSubtitleControls.cs','LocalTalk/HomeSubtitleUiTests.cs',
        'eng/Build.ps1','eng/Prepare-Packaging.ps1','eng/Check-PublicSource.mjs',
        'docs/development.md','docs/codebase-guide.md','README.md','CONTRIBUTING.md','SECURITY.md','SUPPORT.md',
        'CHANGELOG.md','SOURCE-PROVENANCE.md','SayWhat.sln','.github/workflows/ci.yml',
        'Website/public/index.html','Website/public/_headers','Website/public/assets/person-ari.svg','Website/public/assets/person-mina.svg')
}
$reviewedWebsiteImages = @('Website/public/favicon.svg','Website/public/favicon.ico',
    'Website/public/assets/person-ari.svg','Website/public/assets/person-mina.svg',
    'Website/public/assets/saywhat-icon-256.png','Website/public/assets/saywhat-lockup.png')
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.5') {
    $requiredSource += @('IncomingTranslator/IncomingCaptureCapabilities.cs','LocalTalk/IncomingCaptureUi.cs',
        'LocalTalk/IncomingCaptureCapabilityTests.cs','Packaging/Test-WindowsCompatibility.ps1')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.8') {
    $requiredSource += @('IncomingTranslator/IncomingSourceFilter.cs','IncomingTranslator/IncomingSourceFiltering.cs',
        'LocalTalk/ChineseSubtitleFilterTests.cs','LocalTalk/IncomingChineseOnlyPreferenceTests.cs','LocalTalk/IncomingSourceFilterPipelineTests.cs',
        'Packaging/licenses/Unicode-License-V3.txt')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.7') {
    $requiredSource += @('IncomingTranslator/SubtitleActionSurface.cs','LocalTalk/SubtitleActionSurfaceTests.cs',
        'LocalTalk/CompactPersonCorrectionTests.cs')
}
if ([version]($Version -split '-', 2)[0] -ge [version]'0.2.6') {
    $requiredSource += @('IncomingTranslator/SubtitleWindow.Compact.cs','IncomingTranslator/SubtitleTextSurface.cs',
        'LocalTalk/CompactSubtitleUiTests.cs','LocalTalk/SubtitleTextSurfaceTests.cs',
        'LocalTalk/SubtitleAppearancePreferenceTests.cs','LocalTalk/EarlyTranslationPreferenceTests.cs',
        'FoxTransSource/FoxTrans/Providers/RecognitionTextFilter.cs','LocalTalk/RecognitionTextFilterTests.cs','docs/subtitles.md')
}
$archive = [IO.Compression.ZipFile]::OpenRead($sourceZip)
$sha = [Security.Cryptography.SHA256]::Create()
$sourceInventory = [Collections.Generic.List[object]]::new()
$maintainerGrantConfirmed = $false
try {
    foreach ($name in $requiredSource) { if (-not $archive.GetEntry($name)) { throw ('Required source absent: ' + $name) } }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        if ($name -match $privateNames -or $name -match '^Packaging/(?:release-export-|receipt-transport-fixtures/)') { throw ('Private/generated source path: ' + $name) }
        if ($name.EndsWith('/')) { continue }
        if (-not $names.Add($name)) { throw ('Duplicate source path: ' + $name) }
        $relative = $name.Replace('/', '\')
        $original = [IO.Path]::GetFullPath((Join-Path $stagedSource $relative))
        if (-not $original.StartsWith($stagedSource + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $original -PathType Leaf)) { throw ('Invalid/unmatched staged-source path: ' + $name) }
        if ($name -match '[.](png|jpg|jpeg|gif|webp|ico|svg)$' -and
            $name -notin @('LocalTalk/Assets/SayWhat.ico','LocalTalk/Assets/saywhat-mark.png','LocalTalk/Assets/saywhat-icon-256.png','LocalTalk/Assets/saywhat-lockup.png') -and
            $name -notin $reviewedWebsiteImages -and
            $name -notmatch '^FoxTransSource/(brand/|FoxTrans[.]Desktop/Assets/).+[.](png|svg|ico)$') { throw ('Unreviewed source image: ' + $name) }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($hash -ne (Digest $original)) { throw ('Staged source differs from archive: ' + $name) }
        $sourceInventory.Add([pscustomobject]@{ Path = $name; Bytes = $entry.Length; Sha256 = $hash })
    }
    foreach ($language in @('en','zh-CN','ja','ko')) {
        $entry = $archive.GetEntry('Packaging/InstallerDefaults/' + $language + '.json')
        if (-not $entry) { throw ('Missing public language defaults: ' + $language) }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $defaults = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $properties = @($defaults.PSObject.Properties.Name)
        if ($properties.Count -ne 1 -or $properties[0] -ne 'uiLanguage' -or $defaults.uiLanguage -ne $language) { throw ('Unexpected language defaults: ' + $language) }
    }
    $projectReader = [IO.StreamReader]::new($archive.GetEntry('LocalTalk/LocalTalk.csproj').Open())
    try { [xml]$project = $projectReader.ReadToEnd() } finally { $projectReader.Dispose() }
    if ([string]$project.Project.PropertyGroup.Version -ne $Version) { throw 'Archive project version does not match the release.' }
    $declarationEntry = $archive.GetEntry('Packaging/SOURCE-AND-LICENSES.md')
    if ($declarationEntry) {
        if (-not $archive.GetEntry('LICENSE')) { throw 'SayWhat license grant requires the complete root GPL text.' }
        $declarationReader = [IO.StreamReader]::new($declarationEntry.Open())
        try { $declaration = $declarationReader.ReadToEnd() } finally { $declarationReader.Dispose() }
        $payloadDeclaration = Join-Path $payload 'SOURCE-AND-LICENSES.md'
        if ($declaration -notmatch 'SPDX-License-Identifier: GPL-3.0-only' -or
            -not (Test-Path -LiteralPath $payloadDeclaration -PathType Leaf) -or
            (Digest $payloadDeclaration) -ne (Digest (Join-Path $stagedSource 'Packaging\SOURCE-AND-LICENSES.md'))) {
            throw 'Original contribution declaration must match the exact staged source and installed payload.'
        }
        if ((Digest (Join-Path $stagedSource 'LICENSE')) -ne (Digest (Join-Path $payload 'licenses\GPL-3.0.txt'))) {
            throw 'Root and bundled GPL text differ.'
        }
        $maintainerGrantConfirmed = $true
    } elseif ([version]($Version -split '-', 2)[0] -ge [version]'0.1.14') {
        throw 'SayWhat 0.1.14 onward requires the approved original-contribution GPL declaration.'
    }
} finally { $sha.Dispose(); $archive.Dispose() }
$payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
foreach ($file in $payloadFiles) {
    if ($file.FullName -match '(^|[\\/])people-photos[\\/]') { throw 'Personal people photos must not appear in a release payload.' }
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
    if ($relative -match 'vrchat-translation-settings[.]json|incoming-speaker-feedback[.]json|incoming-ai-speaker-profiles[.]json|provisioning[.]json|setup-progress[.]json|config[.].*jsonc|[.]log$|[.]gguf$|[.]onnx$|[.]wav$|[.]mp3$|[.]flac$|[.]ogg$|[.]m4a$') { throw ('Private payload file: ' + $relative) }
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payload files are not release inputs.' }
    if ($file.Extension -match '^[.](png|jpg|jpeg|gif|webp|ico|svg)$' -and
        $relative -notin @('Assets/SayWhat.ico','Assets/saywhat-mark.png','Assets/saywhat-icon-256.png','Assets/saywhat-lockup.png')) { throw ('Unreviewed payload image: ' + $relative) }
}
$requiredSupplemental = @('NAudio-MIT.txt','sherpa-onnx-Apache-2.0.txt','ONNX-Runtime-ThirdPartyNotices.txt',
    'dotnet-System-Numerics-Tensors-MIT.txt','libfvad-BSD-3-Clause.txt','miniaudio-LICENSE.txt','OpenMDW-1.1.txt','README.md')
foreach ($name in $requiredSupplemental) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagingRoot ('licenses\' + $name)) -PathType Leaf)) { throw ('Missing supplemental license: ' + $name) }
}
[void](& (Join-Path $packagingRoot 'Test-ReleaseLicenses.ps1'))
foreach ($name in @('licenses\GPL-3.0.txt','licenses\llama.cpp-MIT.txt','licenses\voxtral.cpp-MIT.txt',
    'licenses\Inno-Setup.txt','licenses\libfvad-PATENTS','licenses\libfvad-AUTHORS',
    'runtimes\llama-vulkan\LICENSE-LLVM-OpenMP','THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw ('Missing bundled notice: ' + $name) }
}
$packageInventory = [Collections.Generic.List[object]]::new()
$packageNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($depsPath in @((Join-Path $payload 'SayWhat.deps.json'), (Join-Path $payload 'tools\FoxTransLocalHost\FoxTrans.LocalHost.deps.json'))) {
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    foreach ($library in $deps.libraries.PSObject.Properties) {
        if ($library.Value.type -notin @('package','runtimepack') -or -not $packageNames.Add($library.Name)) { continue }
        $id, $packageVersion = $library.Name -split '/', 2
        $cacheId = $id -replace '^runtimepack[.]', ''
        $nuspec = Join-Path $NuGetCachePath ($cacheId.ToLowerInvariant() + '\' + $packageVersion + '\' + $cacheId.ToLowerInvariant() + '.nuspec')
        $license = ''; $licenseType = ''; $repository = ''; $commit = ''; $copyright = ''
        if (Test-Path -LiteralPath $nuspec -PathType Leaf) {
            [xml]$metadata = Get-Content -LiteralPath $nuspec -Raw
            $node = $metadata.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
            $licenseNode = $node.SelectSingleNode('*[local-name()="license"]')
            if ($licenseNode) { $license = $licenseNode.InnerText; $licenseType = $licenseNode.GetAttribute('type') }
            $repoNode = $node.SelectSingleNode('*[local-name()="repository"]')
            if ($repoNode) { $repository = $repoNode.GetAttribute('url'); $commit = $repoNode.GetAttribute('commit') }
            $copyrightNode = $node.SelectSingleNode('*[local-name()="copyright"]')
            if ($copyrightNode) { $copyright = $copyrightNode.InnerText }
        }
        if ($repository -and $repository -notmatch '^https://github[.]com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:[.]git)?/?$') { $repository = 'Review source metadata; URL omitted from public manifest.' }
        $packageInventory.Add([pscustomobject]@{ Id = $id; Version = $packageVersion; DeclaredLicense = $license;
            LicenseMetadataType = $licenseType; Copyright = $copyright; Repository = $repository; SourceCommit = $commit })
    }
}
$native = @(foreach ($name in @('onnxruntime.dll','sherpa-onnx-c-api.dll','tools\FoxTransLocalHost\FoxTrans.Native.dll','runtimes\llama-vulkan\llama-server.exe')) {
    $file = Get-Item -LiteralPath (Join-Path $payload $name)
    [pscustomobject]@{ Path = $name.Replace('\','/'); Bytes = $file.Length; Sha256 = Digest $file.FullName;
        ReportedFileVersion = $file.VersionInfo.FileVersion; ReportedProductVersion = $file.VersionInfo.ProductVersion }
})
$existingOmissions = @($requiredSupplemental | Where-Object { -not (Test-Path -LiteralPath (Join-Path $payload ('licenses\' + $_)) -PathType Leaf) })
$summary = [pscustomobject]@{ Version = $Version; InstallerSha256 = Digest $installer; SourceSha256 = Digest $sourceZip;
    SourceFiles = $sourceInventory.Count; PayloadFiles = $payloadFiles.Count; DependencyEntries = $packageInventory.Count;
    SupplementalNotEmbedded = $existingOmissions; Validation = 'PASS'; MaintainerGrantConfirmed = $maintainerGrantConfirmed;
    LegalCertification = $false; Published = $false }
if ($ValidateOnly) { $summary; return }

# A new local output only; no existing release, source archive or installer is overwritten.
$output = Join-Path $packagingRoot ('release-export-' + $Version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
if (Test-Path -LiteralPath $output) { throw 'Export directory already exists.' }
New-Item -ItemType Directory -Path $output | Out-Null
try {
    Copy-Item -LiteralPath $installer -Destination $output
    Copy-Item -LiteralPath $sourceZip -Destination $output
    $exportLicenses = Join-Path $output 'licenses'
    New-Item -ItemType Directory -Path $exportLicenses | Out-Null
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $payload 'licenses') -Recurse -File)) {
        $target = Join-Path $exportLicenses ([IO.Path]::GetRelativePath((Join-Path $payload 'licenses'), $file.FullName))
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $packagingRoot 'licenses') -File)) {
        $target = Join-Path $exportLicenses $file.Name
        if (Test-Path -LiteralPath $target) {
            if ((Digest $target) -ne (Digest $file.FullName)) { throw ('Conflicting notice text: ' + $file.Name) }
        } else { Copy-Item -LiteralPath $file.FullName -Destination $target }
    }
    Copy-Item -LiteralPath (Join-Path $payload 'runtimes\llama-vulkan\LICENSE-LLVM-OpenMP') -Destination (Join-Path $exportLicenses 'llama-runtime-LLVM-OpenMP.txt')
    Copy-Item -LiteralPath (Join-Path $payload 'THIRD-PARTY-NOTICES.md') -Destination $output
    if ($maintainerGrantConfirmed) {
        Copy-Item -LiteralPath (Join-Path $payload 'SOURCE-AND-LICENSES.md') -Destination $output
        Copy-Item -LiteralPath (Join-Path $stagedSource 'LICENSE') -Destination $output
        Copy-Item -LiteralPath (Join-Path $stagedSource 'Packaging\Release-Distribution.md') -Destination (Join-Path $output 'DISTRIBUTION-NOTES.md')
    } else {
        Copy-Item -LiteralPath (Join-Path $packagingRoot 'Release-Distribution.md') -Destination (Join-Path $output 'SOURCE-AND-LICENSES.md')
    }
    $readme = @"
# SayWhat? $Version preview

Local speech translation for VRChat on Windows 10 (21H2+) and Windows 11 x64.
Windows 10 compatibility is a preview and still needs real-PC acceptance testing.

Run SayWhat-$Version-Setup.exe. Setup offers installation for you or all users.
Then open SayWhat?, choose the language you speak and your translation languages,
and prepare the recommended models. Start on Home translates your own voice.
Nemotron and FireRedASR2 recognition run directly on Windows; a supported GPU is
recommended for translation. The alternative Voxtral recognizer and Groups
subtitles use an app-owned Ubuntu environment and may need administrator
approval, WSL2 and a restart.
No separate FoxTrans, LM Studio/Bionic or .NET installation is required.

Your microphone can translate into up to four languages in VRChat chat. Other
voices can appear as separate, optional subtitles. Inference and saved voice
profiles stay on your device.
Capturing only VRChat or another selected application's audio requires Windows 11
on consumer PCs. Windows 10 users can explicitly choose All playback for subtitles;
this includes other applications' audio. Capture never silently falls back to it.
Setup downloads require internet; speech is not sent to a cloud translation API.
Speaker matching does not separate overlapping voices into isolated audio streams.

This is an unsigned preview, not a fresh-PC or long-session stability guarantee.
See SOURCE-AND-LICENSES.md for known checks and component terms.
Original SayWhat? contributions are GPL-3.0-only where declared by the exact
matching source; all third-party licenses and attribution remain unchanged.
Keep this README, source ZIP, licenses, notices and checksums with the download.
SHA256SUMS.txt checks byte integrity; it does not authenticate a publisher.
"@
    [IO.File]::WriteAllText((Join-Path $output 'README.md'), $readme, [Text.UTF8Encoding]::new($false))
    $remainingChecks = @('Native/transitive build provenance and exact optional dependency rights',
        'Exact optional model/conversion/CAM++ and CUDA terms before mirroring weights/runtimes',
        'Fresh-PC installation/update/uninstall/UAC/restart/cancel and long live VRChat tests',
        'Publisher signing and antivirus/reputation review', 'Choose and verify public binary/source download URLs')
    if (-not $maintainerGrantConfirmed) {
        $remainingChecks = @('Maintainer contribution license grant/attribution for this historical build') + $remainingChecks
    }
    $manifest = [ordered]@{
        SchemaVersion = 1; App = 'SayWhat?'; Version = $Version; ReleaseStatus = 'prepared-preview-not-certified';
        CreatedUtc = [DateTime]::UtcNow.ToString('O'); Published = $false; ModelsBundled = $false; UserDataIncluded = $false;
        Installer = @{ File = [IO.Path]::GetFileName($installer); Bytes = (Get-Item -LiteralPath $installer).Length; Sha256 = Digest $installer;
            AuthenticodeStatus = (Get-AuthenticodeSignature -LiteralPath $installer).Status.ToString() };
        CorrespondingSource = @{ File = [IO.Path]::GetFileName($sourceZip); Bytes = (Get-Item -LiteralPath $sourceZip).Length; Sha256 = Digest $sourceZip;
            Origin = 'Exact audited build-stage archive, not current workspace reconstruction'; Files = @($sourceInventory.ToArray()) };
        SourcePolicy = 'Retain complete provided app/FoxTrans source and recipients rights; no private-source exemption claimed';
        MaintainerGrantConfirmed = $maintainerGrantConfirmed;
        OriginalContributionLicense = if ($maintainerGrantConfirmed) { 'GPL-3.0-only' } else { 'Not declared in this historical build' };
        LegalCertification = $false;
        FoxTransCommit = '515802db30f979d9b1dacca786b3c33827dc0b92'; VoxtralCommit = '3d1ed513debc88b4a383ad83e0cd0bcb84889a60';
        SpeechBuildIdentity = '3d1ed513debc88b4a383ad83e0cd0bcb84889a60-localtalk-r2'; LlamaRuntime = 'b11401';
        Dependencies = @($packageInventory.ToArray()); NativeBinaries = $native;
        SupplementalLicensesNotInsideOriginalInstaller = $existingOmissions;
        RemainingChecks = $remainingChecks;
        Files = @()
    }
    $manifest.Files = @(foreach ($file in (Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName)) {
        [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/'); Bytes = $file.Length; Sha256 = Digest $file.FullName }
    })
    [IO.File]::WriteAllText((Join-Path $output 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $lines = @(foreach ($file in (Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName)) {
        (Digest $file.FullName) + '  ' + [IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/')
    })
    [IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
    if ((Digest (Join-Path $output ([IO.Path]::GetFileName($installer)))) -ne $ExpectedInstallerSha256.ToLowerInvariant() -or
        (Digest (Join-Path $output ([IO.Path]::GetFileName($sourceZip)))) -ne $ExpectedSourceSha256.ToLowerInvariant()) { throw 'Exported release hashes changed during copying.' }
    # One recipient archive avoids accidentally sharing the old installer alone.
    # Store existing compressed EXE/source bytes without expensive recompression.
    $distributionZip = $output + '.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($output, $distributionZip, [IO.Compression.CompressionLevel]::NoCompression, $false)
    $distributionHash = Digest $distributionZip
    [IO.File]::WriteAllText(($distributionZip + '.sha256.txt'), ($distributionHash + '  ' + [IO.Path]::GetFileName($distributionZip) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ ExportDirectory = $output; DistributionZip = $distributionZip; DistributionBytes = (Get-Item -LiteralPath $distributionZip).Length;
        DistributionSha256 = $distributionHash; InstallerSha256 = $summary.InstallerSha256; SourceSha256 = $summary.SourceSha256;
        Files = @(Get-ChildItem -LiteralPath $output -Recurse -File).Count; Validation = 'PASS'; ReleaseStatus = $manifest.ReleaseStatus; Published = $false }
} catch {
    Write-Warning ('Export incomplete. Do not publish the partial directory: ' + $output)
    throw
}

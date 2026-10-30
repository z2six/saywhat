param(
    [string]$Version = '0.2.13',
    [string]$CompilerPath,
    [switch]$SkipPublish,
    [string]$AppPublishDirectory,
    [string]$HostPublishDirectory,
    [string]$BundledRuntimeArchive,
    [string]$PackageSource
)

$ErrorActionPreference = 'Stop'
$packagingRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $packagingRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'A .NET SDK is required to build, but is not needed on installed PCs.' }
if ($Version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$') { throw 'Invalid installer version.' }
if (-not $CompilerPath) { $CompilerPath = Join-Path $packagingRoot 'tools\InnoSetup\ISCC.exe' }
if (-not (Test-Path -LiteralPath $CompilerPath)) { throw 'Inno compiler is not present. See Packaging/README.md for its verified local installation.' }
if (-not $BundledRuntimeArchive) { $BundledRuntimeArchive = Join-Path $packagingRoot 'tools\llama-b11401-bin-win-vulkan-x64.zip' }
if (-not (Test-Path -LiteralPath $BundledRuntimeArchive)) { throw 'The pinned official Vulkan runtime archive is required for the installer payload.' }
[xml]$appProject = Get-Content -LiteralPath (Join-Path $projectRoot 'LocalTalk\LocalTalk.csproj') -Raw
if ([string]$appProject.Project.PropertyGroup.Version -ne $Version) {
    throw 'Installer version must match the app project source version. Do not relabel old binaries or sources.'
}

# These are public program defaults, never private preferences or translation targets.
foreach ($uiLanguage in @('en','zh-CN','ja','ko')) {
    $languageTemplate = Join-Path $packagingRoot ('InstallerDefaults\' + $uiLanguage + '.json')
    $languageDefault = Get-Content -LiteralPath $languageTemplate -Raw | ConvertFrom-Json
    if (@($languageDefault.PSObject.Properties).Count -ne 1 -or $languageDefault.uiLanguage -cne $uiLanguage) {
        throw ('Invalid public installer UI-language default: ' + $languageTemplate)
    }
}

$stageName = 'build-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$stage = Join-Path $packagingRoot $stageName
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
if (-not $AppPublishDirectory) { $AppPublishDirectory = Join-Path $stage 'app-publish' }
if (-not $HostPublishDirectory) { $HostPublishDirectory = Join-Path $stage 'host-publish' }

function Invoke-DotnetChecked([string[]]$Arguments) {
    if ($PackageSource -and $Arguments[0] -in @('build','publish','restore')) {
        # Reproducible offline builds can use a populated local package feed/cache;
        # this does not modify the user's global NuGet configuration.
        $Arguments += @(('-p:RestoreSources=' + $PackageSource), '-p:NuGetAudit=false')
    }
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('Build failed: ' + ($Arguments -join ' ')) }
}

if (-not $SkipPublish) {
    # Self-contained Windows builds include the .NET 8 desktop and .NET 9 host runtimes.
    # One build node prevents a packaging step from consuming the gaming PC's CPU.
    Invoke-DotnetChecked @('publish', (Join-Path $projectRoot 'LocalTalk\LocalTalk.csproj'), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:UseSharedCompilation=false', '-m:1', '-o', $AppPublishDirectory)
    Invoke-DotnetChecked @('publish', (Join-Path $projectRoot 'FoxTransLocalHost\FoxTransLocalHost.csproj'), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:UseSharedCompilation=false', '-m:1', '-o', $HostPublishDirectory)
}
foreach ($required in @((Join-Path $AppPublishDirectory 'SayWhat.exe'), (Join-Path $AppPublishDirectory 'coreclr.dll'),
    (Join-Path $HostPublishDirectory 'FoxTrans.LocalHost.exe'), (Join-Path $HostPublishDirectory 'coreclr.dll'),
    (Join-Path $HostPublishDirectory 'FoxTrans.Native.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw ('Required self-contained payload file is missing: ' + $required) }
}
foreach ($obsolete in @('LocalTalk.exe','LocalTalk.dll','LocalTalk.deps.json','LocalTalk.runtimeconfig.json')) {
    if (Test-Path -LiteralPath (Join-Path $AppPublishDirectory $obsolete)) {
        throw ('The app publish directory contains an obsolete LocalTalk assembly. Use a clean SayWhat publish directory: ' + $obsolete)
    }
}
$publishedVersion = (Get-Item -LiteralPath (Join-Path $AppPublishDirectory 'SayWhat.exe')).VersionInfo.ProductVersion
if (-not $publishedVersion -or ($publishedVersion -split '\+', 2)[0] -ne $Version) {
    throw 'Published app version does not match the installer/source version. Use a fresh matching publish.'
}
Copy-Item -Path (Join-Path $AppPublishDirectory '*') -Destination $payload -Recurse -Force
# These exact reviewed public logo assets are part of the installed app and
# installer branding. Never broaden this list to user pictures or previews.
$brandingNames = @('SayWhat.ico', 'saywhat-mark.png', 'saywhat-icon-256.png', 'saywhat-lockup.png')
$brandingDestination = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Path $brandingDestination -Force | Out-Null
foreach ($brandingName in $brandingNames) {
    $brandingSource = Join-Path $projectRoot ('LocalTalk\Assets\' + $brandingName)
    if (-not (Test-Path -LiteralPath $brandingSource -PathType Leaf)) {
        throw ('Required public branding asset is missing: ' + $brandingName)
    }
    Copy-Item -LiteralPath $brandingSource -Destination (Join-Path $brandingDestination $brandingName) -Force
}
$tools = Join-Path $payload 'tools'
$hostDestination = Join-Path $tools 'FoxTransLocalHost'
New-Item -ItemType Directory -Path $hostDestination -Force | Out-Null
Copy-Item -Path (Join-Path $HostPublishDirectory '*') -Destination $hostDestination -Recurse -Force
foreach ($helper in @('FoxTrans-VRChatMuteSync.ps1', 'FoxTrans-OscSentenceProxy.ps1')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $helper) -Destination (Join-Path $tools $helper)
}
$provisioning = Join-Path $payload 'provisioning'
New-Item -ItemType Directory -Path $provisioning -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $packagingRoot 'scripts') -File |
    Where-Object Name -NE 'test-hidden-desktop-entries.sh' |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $provisioning -Force }
$bundledRuntime = Join-Path $payload 'runtimes\llama-vulkan'
Invoke-DotnetChecked @('build', (Join-Path $packagingRoot 'ProvisioningTests\ProvisioningTests.csproj'), '-c', 'Release', '-p:UseSharedCompilation=false', '-m:1')
Invoke-DotnetChecked @((Join-Path $packagingRoot 'ProvisioningTests\bin\Release\net8.0-windows\ProvisioningTests.dll'),
    '--bundle-runtime', $BundledRuntimeArchive, $bundledRuntime)
$licenses = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -Path (Join-Path $packagingRoot 'licenses\*') -Destination $licenses -Force
foreach ($upstreamNotice in @('PATENTS','AUTHORS')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('FoxTransSource\native\vendor\libfvad\' + $upstreamNotice)) -Destination (Join-Path $licenses ('libfvad-' + $upstreamNotice))
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'FoxTransSource\LICENSE') -Destination (Join-Path $licenses 'GPL-3.0.txt')
if ((Get-FileHash -LiteralPath (Join-Path $projectRoot 'LICENSE') -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $projectRoot 'FoxTransSource\LICENSE') -Algorithm SHA256).Hash) {
    throw 'The complete root GPL text must remain identical to the preserved upstream license text.'
}
Copy-Item -LiteralPath (Join-Path $packagingRoot 'SOURCE-AND-LICENSES.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $packagingRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $packagingRoot 'INSTALLATION-NOTES.txt') -Destination $payload

# Complete corresponding source, not a URL-only source promise. Never include
# private configuration, audio, transcripts/logs, model files, git metadata or compiler tools.
$sourceRoot = Join-Path $stage 'source'
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
$sourceExtensions = @('.cs','.csproj','.props','.targets','.sln','.slnx','.md','.c','.h','.cpp','.hpp','.inl','.cmake','.txt','.yml','.yaml','.ps1','.sh','.iss','.isl','.patch','.axaml','.in','.py','.html','.css','.js','.mjs','.cjs','.svg')
$publicInstallerDefaults = @('Packaging\InstallerDefaults\en.json', 'Packaging\InstallerDefaults\zh-CN.json',
    'Packaging\InstallerDefaults\ja.json', 'Packaging\InstallerDefaults\ko.json')
$publicProviderExamples = @('FoxTransSource\examples\audio-llm.example.jsonc',
    'FoxTransSource\examples\whisper.example.jsonc', 'FoxTransSource\examples\voxtral.example.jsonc')
$excludedSourceDirectories = '(^|[\\/])(bin|obj|publish|active|artifacts|node_modules|\.wrangler|models|runtimes|downloads|recordings|captures|[^\\/]*-publish(?:-[^\\/]*)?|[^\\/]*-build(?:-[^\\/]*)?|[^\\/]*-checks?|[^\\/]*-checks?-[^\\/]*|[^\\/]*-backup-[^\\/]*|qa|preview|data|test-artifacts|\.git|\.agents|\.codex|dist|offline-[^\\/]*|build-[^\\/]+|release-export-[^\\/]+|people-photos)[\\/]'
function Get-PublicSourceFiles([string]$Root) {
    # Prune generated/private folders before descending. Filtering a recursive
    # listing afterwards still enters protected test fixtures and old payloads.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        Get-ChildItem -LiteralPath $current -File
        foreach ($directory in Get-ChildItem -LiteralPath $current -Directory) {
            $relativeDirectory = [IO.Path]::GetRelativePath($projectRoot, $directory.FullName) + [IO.Path]::DirectorySeparatorChar
            if ($relativeDirectory -match $excludedSourceDirectories -or
                $relativeDirectory -match '^Packaging[\\/](tools|receipt-transport-fixtures)[\\/]' -or
                ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
            $pending.Push($directory.FullName)
        }
    }
}
foreach ($sourceFolder in @('LocalTalk','IncomingTranslator','FoxTransLocalHost','FoxTransSource','Packaging','eng','docs','Website','.github')) {
    $folder = Join-Path $projectRoot $sourceFolder
    Get-PublicSourceFiles $folder | Where-Object {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $_.FullName)
        $relative -notmatch '^Packaging[\\/](?:offline-receipt-transport-fixtures|receipt-transport-fixtures)[\\/]' -and
        $relative -notmatch '(^|[\\/])people-photos[\\/]' -and
        $relative -notmatch '(^|[\\/])(bin|obj|publish|active|[^\\/]*-publish(?:-[^\\/]*)?|[^\\/]*-build(?:-[^\\/]*)?|[^\\/]*-checks?|[^\\/]*-checks?-[^\\/]*|[^\\/]*-backup-[^\\/]*|qa|preview|data|test-artifacts|\.git|\.agents|\.codex|dist|offline-[^\\/]*|build-[^\\/]+|release-export-[^\\/]+)[\\/]' -and
        $relative -notmatch '^Packaging[\\/]tools[\\/]' -and
        $relative -notmatch '^Packaging[\\/]release-export-' -and
        $_.Name -notmatch '^(Test-InstallerDataLifecycle[.]ps1|vrchat-translation-settings[.]json|provisioning[.]json|setup-progress[.]json|config[.].*[.]jsonc)$' -and
        ($sourceExtensions -contains $_.Extension.ToLowerInvariant() -or $relative -in $publicInstallerDefaults -or $relative -in $publicProviderExamples -or
            ($relative -match '^Website[\\/]public[\\/]' -and $_.Extension -in @('.json','.png','.ico')) -or
            $relative -in @('Website\wrangler.jsonc','Website\public\_headers') -or
            ($relative -in @('LocalTalk\Assets\SayWhat.ico', 'LocalTalk\Assets\saywhat-mark.png', 'LocalTalk\Assets\saywhat-icon-256.png', 'LocalTalk\Assets\saywhat-lockup.png')) -or
            # Public upstream branding/build resources, never user photos or previews.
            ($relative -match '^FoxTransSource[\\/](brand[\\/]|FoxTrans[.]Desktop[\\/]Assets[\\/])' -and $_.Extension -in @('.png','.svg','.ico')) -or
            $_.Name -in @('LICENSE','LICENSE.TXT','PATENTS','AUTHORS','CMakeLists.txt','NOTICE','NOTICE.txt','global.json','packages.lock.json','foxtrans.schema.json','.gitattributes','.gitignore'))
    } | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $_.FullName)
        $target = Join-Path $sourceRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}
foreach ($helper in @('FoxTrans-VRChatMuteSync.ps1','FoxTrans-OscSentenceProxy.ps1')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $helper) -Destination $sourceRoot
}
foreach ($launcher in @('Start-SayWhat.bat','Start-LocalTalk.bat')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $launcher) -Destination $sourceRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $sourceRoot
foreach ($document in @('README.md','CONTRIBUTING.md','SECURITY.md','SUPPORT.md','CHANGELOG.md','SOURCE-PROVENANCE.md','SayWhat.sln','.gitignore','.gitattributes')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $sourceRoot
}
$nativeSourceConvenience = Join-Path $sourceRoot 'FoxTransLocalHost\Native'
New-Item -ItemType Directory -Path $nativeSourceConvenience -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'FoxTransLocalHost\Native\FoxTrans.Native.dll') -Destination $nativeSourceConvenience
$installedSource = Join-Path $payload 'source'
New-Item -ItemType Directory -Path $installedSource -Force | Out-Null
Compress-Archive -Path (Join-Path $sourceRoot '*') -DestinationPath (Join-Path $installedSource 'SayWhat-Corresponding-Source.zip') -CompressionLevel Optimal
$sourceArchive = [IO.Compression.ZipFile]::OpenRead((Join-Path $installedSource 'SayWhat-Corresponding-Source.zip'))
try {
    foreach ($required in @('LICENSE','Packaging/SOURCE-AND-LICENSES.md',
        'LocalTalk/Provisioning.cs','LocalTalk/MainWindow.cs','LocalTalk/RuntimeController.cs',
        'LocalTalk/ManagedWslImage.cs','LocalTalk/ManagedWslImageTests.cs',
        'LocalTalk/ManagedWslSetup.cs','LocalTalk/ManagedWslSetupTests.cs','LocalTalk/WindowsSpeechRequirements.cs',
        'LocalTalk/NativeSubtitleDefaultTests.cs','Packaging/scripts/preflight-voxtral-backend.sh',
        'Packaging/ProvisioningTests/test-bootstrap-packages.sh','Packaging/ProvisioningTests/test-cuda-preflight.sh',
        'Packaging/ManagedWslImageTests/ManagedWslImageTests.csproj','Packaging/ManagedWslImageTests/Program.cs',
        'Packaging/ManagedWslImageTests/ProvisionAsset.cs',
        'Packaging/Test-VRChatMuteSync-Logging.ps1',
        'IncomingTranslator/ManagedServicePorts.cs','IncomingTranslator/SessionEndpoints.cs','LocalTalk/RuntimeController.Ports.cs',
        'LocalTalk/MainWindowConnections.cs','LocalTalk/ManagedServicePortTests.cs','LocalTalk/SessionEndpointTests.cs','Packaging/ProvisioningTests/test-managed-ports.py','Packaging/vibevoice/test_ports.py',
        'Packaging/ManagedPortTests/ManagedPortTests.csproj','Packaging/ManagedPortTests/Program.cs','Packaging/ManagedPortTests/fixtures/owned-http.ps1',
        'LocalTalk/NativeSpeechProvisioner.cs','LocalTalk/NativeSpeechProvisionerTests.cs','LocalTalk/OwnRecognitionCatalog.cs',
        'LocalTalk/FireRedSpeechProvisioner.cs','LocalTalk/SherpaSpeechRecognizer.cs','LocalTalk/FireRedIncomingSpeech.cs','LocalTalk/FireRedSpeechTests.cs',
        'LocalTalk/SubtitleSpeechUi.cs','LocalTalk/SubtitleSpeechText.cs','LocalTalk/SubtitleSpeechUiTests.cs',
        'IncomingTranslator/IncomingRecognitionCatalog.cs','IncomingTranslator/IncomingNativeSpeech.cs','LocalTalk/IncomingNativeSpeechTests.cs',
        'LocalTalk/VoiceFirstHome.cs','LocalTalk/VoiceFirstText.cs','LocalTalk/VoiceFirstHomeUiTests.cs',
        'LocalTalk/HomeSubtitleControls.cs','LocalTalk/HomeSubtitleUiTests.cs',
        'IncomingTranslator/IncomingCaptureCapabilities.cs','LocalTalk/IncomingCaptureUi.cs',
        'LocalTalk/IncomingCaptureCapabilityTests.cs','Packaging/Test-WindowsCompatibility.ps1',
        'IncomingTranslator/SubtitleWindow.Compact.cs','IncomingTranslator/SubtitleTextSurface.cs',
        'IncomingTranslator/SubtitleActionSurface.cs','LocalTalk/SubtitleActionSurfaceTests.cs','LocalTalk/CompactPersonCorrectionTests.cs',
        'IncomingTranslator/IncomingSourceFilter.cs','IncomingTranslator/IncomingSourceFiltering.cs',
        'LocalTalk/ChineseSubtitleFilterTests.cs','LocalTalk/IncomingChineseOnlyPreferenceTests.cs','LocalTalk/IncomingSourceFilterPipelineTests.cs',
        'Packaging/licenses/Unicode-License-V3.txt',
        'LocalTalk/CompactSubtitleUiTests.cs','LocalTalk/SubtitleTextSurfaceTests.cs',
        'LocalTalk/SubtitleAppearancePreferenceTests.cs','LocalTalk/EarlyTranslationPreferenceTests.cs',
        'FoxTransSource/FoxTrans/Providers/RecognitionTextFilter.cs','LocalTalk/RecognitionTextFilterTests.cs','docs/subtitles.md',
        'FoxTransSource/FoxTrans/Providers/NemotronTranscriber.cs','FoxTransLocalHost/NativeSpeechTests.cs',
        'LocalTalk/RuntimeProfiles.cs','LocalTalk/VibeVoiceRuntime.cs','LocalTalk/MainWindowProfileRuntime.cs',
        'LocalTalk/TranslationProfilesUi.cs','IncomingTranslator/TranslationProfiles.cs',
        'IncomingTranslator/IncomingPipelineProfiles.cs','IncomingTranslator/IncomingGroupPipeline.cs',
        'IncomingTranslator/GroupSpeechClient.cs','IncomingTranslator/SubtitleProfiles.cs',
        'IncomingTranslator/IncomingContinuousAudio.cs','IncomingTranslator/IncomingContinuousAudioTests.cs',
        'Packaging/vibevoice/server.py','Packaging/vibevoice/protocol.py','Packaging/vibevoice/prepare.py',
        'Packaging/vibevoice/bootstrap-vibevoice.sh','Packaging/vibevoice/run-vibevoice.sh',
        'Packaging/vibevoice/stop-vibevoice.sh','Packaging/vibevoice/cancel-prepare.sh',
        'LocalTalk/HelpUi.cs','LocalTalk/HelpUiTests.cs','LocalTalk/TimingDefaultsTests.cs',
        'LocalTalk/PersonPhotoEditor.cs','LocalTalk/PersonPhotoEditorTests.cs',
        'LocalTalk/ManagedSpeechIntegration.cs','LocalTalk/ManagedSpeechIntegrationTests.cs',
        'LocalTalk/Diagnostics.cs','LocalTalk/MainWindow.Diagnostics.cs','LocalTalk/DiagnosticsTests.cs',
        'FoxTrans-VRChatMuteSync.ps1','Packaging/Test-VRChatMuteSync-Discovery.ps1',
        'Packaging/scripts/hide-speech-desktop-entries.sh','Packaging/scripts/test-hidden-desktop-entries.sh',
        'LocalTalk/Theme.cs','LocalTalk/ThemeTests.cs','LocalTalk/OverlayThemeTests.cs',
        'LocalTalk/SetupProgressPresentation.cs','LocalTalk/SetupProgressUiTests.cs',
        'IncomingTranslator/AppSettings.cs','IncomingTranslator/AppearanceTheme.cs','IncomingTranslator/ThemeBackground.cs',
        'FoxTransLocalHost/Program.cs','FoxTransSource/LICENSE','FoxTransSource/FoxTrans/FoxTrans.csproj',
        'IncomingTranslator/AppBrand.cs','LocalTalk/Assets/SayWhat.ico','LocalTalk/Assets/saywhat-mark.png',
        'LocalTalk/Assets/saywhat-icon-256.png','LocalTalk/Assets/saywhat-lockup.png','Packaging/SayWhat.iss','Packaging/InstallerDataFlow.iss',
        'Packaging/InstallerLanguage.iss','Packaging/InstallerStrings.iss',
        'Packaging/Languages/ChineseSimplified.isl','Packaging/Languages/Japanese.isl','Packaging/Languages/Korean.isl',
        'Packaging/InstallerDefaults/en.json','Packaging/InstallerDefaults/zh-CN.json','Packaging/InstallerDefaults/ja.json','Packaging/InstallerDefaults/ko.json',
        'Packaging/InstallerNotices/zh-CN.txt','Packaging/InstallerNotices/ja.txt','Packaging/InstallerNotices/ko.txt',
        'Packaging/Languages/README.md','Packaging/licenses/Inno-Setup.txt',
        'Packaging/scripts/manage-install-data.ps1','Start-SayWhat.bat',
        'Packaging/Build-Installer.ps1','Packaging/scripts/run-voxtral.sh',
        'Packaging/Export-Release.ps1','Packaging/Test-ReleaseLicenses.ps1','Packaging/Release-Distribution.md',
        'eng/Build.ps1','eng/Prepare-Packaging.ps1','eng/Check-PublicSource.mjs','docs/development.md','docs/codebase-guide.md',
        'Website/public/index.html','Website/public/_headers','Website/public/assets/person-ari.svg','Website/public/assets/person-mina.svg',
        '.github/workflows/ci.yml','README.md','CONTRIBUTING.md','SECURITY.md','SUPPORT.md','CHANGELOG.md','SOURCE-PROVENANCE.md',
        'LocalTalk/AppUpdateChecker.cs','LocalTalk/AppUpdateInstaller.cs',
        'LocalTalk/VrchatOscConnection.cs','LocalTalk/VrchatOscConnectionTests.cs',
        'FoxTransSource/FoxTrans/Providers/TranslationModelCapabilities.cs',
        'FoxTransSource/FoxTrans/Providers/TranslationModelPrompts.cs',
        'FoxTransSource/FoxTrans/Providers/SpecializedTextTranslator.cs',
        'FoxTransSource/FoxTrans/Providers/MultiTargetTextTranslator.cs','FoxTransLocalHost/MultiLanguageTests.cs',
        'Packaging/licenses/NAudio-MIT.txt','Packaging/licenses/sherpa-onnx-Apache-2.0.txt',
        'Packaging/licenses/ONNX-Runtime-ThirdPartyNotices.txt','Packaging/licenses/dotnet-System-Numerics-Tensors-MIT.txt',
        'Packaging/licenses/libfvad-BSD-3-Clause.txt','Packaging/licenses/miniaudio-LICENSE.txt','Packaging/licenses/OpenMDW-1.1.txt')) {
        if (-not ($sourceArchive.Entries | Where-Object FullName -EQ $required)) { throw ('Corresponding source missing: ' + $required) }
    }
    if ($sourceArchive.Entries | Where-Object { $_.FullName -match '^Packaging/(?:release-export-|receipt-transport-fixtures/)|(^|/)(preview|data|qa|test-artifacts|bin|obj|publish|[^/]*-publish(?:-[^/]*)?|[^/]*-build(?:-[^/]*)?|active|[.]git|[.]agents|[.]codex|dist|offline-[^/]*|build-[^/]+|release-export-[^/]+|[^/]*-checks?|[^/]*-checks?-[^/]*|[^/]*-backup-[^/]*)/|(^|/)Test-InstallerDataLifecycle[.]ps1$|vrchat-translation-settings[.]json|incoming-speaker-feedback[.]json|incoming-ai-speaker-profiles[.]json|provisioning[.]json|setup-progress[.]json|config[.].*jsonc|[.]log$|[.]gguf$|[.]onnx$|[.]wav$|[.]mp3$|[.]flac$|[.]ogg$|[.]m4a$' }) {
        throw 'Private settings, generated artifacts, recordings or weights must not appear in the source archive.'
    }
} finally { $sourceArchive.Dispose() }
if (Get-ChildItem -LiteralPath $payload -Recurse -Directory | Where-Object Name -EQ 'people-photos') {
    throw 'Personal people photos must not appear in the installer payload.'
}
if (Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Name -match '^vrchat-translation-settings[.]json$|^incoming-speaker-feedback[.]json$|^incoming-ai-speaker-profiles[.]json$|^provisioning[.]json$|^setup-progress[.]json$|^config[.].*jsonc$|[.]log$|[.]gguf$|[.]onnx$|[.]wav$|[.]mp3$|[.]flac$|[.]ogg$|[.]m4a$' }) {
    throw 'Private settings, recordings or model weights must not appear in the installer payload.'
}

# Collect notices from the pinned NuGet packages used by this build.
$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
foreach ($package in @('naudio','naudio.core','naudio.wasapi','org.k2fsa.sherpa.onnx','microsoft.ml.onnxruntime.managed',
    'microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
    $packageFolder = Join-Path $nugetRoot $package
    if (-not (Test-Path -LiteralPath $packageFolder)) { continue }
    Get-ChildItem -LiteralPath $packageFolder -Recurse -File | Where-Object Name -Match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)' |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($packageFolder, $_.FullName)
            $target = Join-Path $licenses ($package + '\' + $relative)
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
}

# SPDX-expression NuGet metadata is not a license-text copy. Explicit reviewed
# texts are copied above and mandatory; future native-version changes require review.
& (Join-Path $packagingRoot 'Test-ReleaseLicenses.ps1') -PayloadDirectory $payload | Out-Null

$dist = Join-Path $packagingRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$outputFile = Join-Path $dist ('SayWhat-' + $Version + '-Setup.exe')
if (Test-Path -LiteralPath $outputFile) {
    Move-Item -LiteralPath $outputFile -Destination ($outputFile + '.previous-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
& $CompilerPath '/Q' ('/DVersion=' + $Version) ('/DPayloadDir=' + $payload) ('/DOutputDir=' + $dist) (Join-Path $packagingRoot 'SayWhat.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$digest = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{ Installer=$outputFile; Sha256=$digest; Bytes=(Get-Item -LiteralPath $outputFile).Length; Payload=$payload; SelfContained=$true; VulkanRuntimeBundled=$true; ModelsBundled=$false; OwnSpeechRequiresWsl=$false; OptionalVoxtralRequiresWsl=$true }

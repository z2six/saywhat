<#
.SYNOPSIS
Build SayWhat and its capture host from source, with optional offline checks.
.DESCRIPTION
Requires Windows x64, .NET SDKs 8 and 9, CMake 3.21+, and Visual Studio 2022
or newer with Desktop development with C++ (or a configured CMake generator). NuGet
restore may download build dependencies. This script never downloads AI model
weights, installs Windows features, starts translation, or opens an audio device.
.EXAMPLE
.\eng\Build.ps1 -RunTests
.EXAMPLE
.\eng\Build.ps1 -Publish -RunTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$RunTests,
    [switch]$Publish,
    [switch]$SkipNative,
    [string]$CMakePath = 'cmake',
    [string]$CMakeGenerator = '',
    [string]$DotNetPath = 'dotnet',
    [string]$NodePath = 'node'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Build SayWhat on 64-bit Windows. The desktop app and capture backend target Windows x64.'
}
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
# A framework-dependent apphost can mistake stale self-contained hostfxr.dll
# for its .NET installation and fail before managed tests even start. Keep
# build modes separate instead of deleting or recycling an unknown old folder.
$outputMode = if ($Publish) { 'publish' } else { 'build' }
$managedOutput = Join-Path $artifactsRoot $outputMode
$appOutput = Join-Path $managedOutput 'app'
$hostOutput = Join-Path $managedOutput 'host'
$nativeOutput = Join-Path $artifactsRoot 'native'
$nativeDestination = Join-Path $repoRoot 'FoxTransLocalHost\Native\FoxTrans.Native.dll'

function Resolve-BuildTool([string]$Name, [string]$Help) {
    $command = Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) { throw ($Help + ' Not found: ' + $Name) }
    return $command.Source
}
function Invoke-BuildTool([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('Build/check failed with exit code ' + $LASTEXITCODE + ': ' + [IO.Path]::GetFileName($FilePath)) }
}
function Select-VisualStudioGenerator([string]$CMake) {
    if ($env:CMAKE_GENERATOR) { return $env:CMAKE_GENERATOR }
    $installerRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $vswhere = Join-Path $installerRoot 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Install Visual Studio 2022 or newer C++ build tools, or pass a configured -CMakeGenerator.'
    }
    $versions = @(& $vswhere -all -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion)
    if ($LASTEXITCODE -ne 0 -or $versions.Count -eq 0) { throw 'No Visual Studio C++ toolchain was found.' }
    $capabilitiesText = & $CMake -E capabilities
    if ($LASTEXITCODE -ne 0) { throw 'Could not read CMake generator capabilities.' }
    $capabilities = ($capabilitiesText -join [Environment]::NewLine) | ConvertFrom-Json
    $majors = @($versions | ForEach-Object { if ($_ -match '^(\d+)\.') { [int]$Matches[1] } } | Sort-Object -Descending -Unique)
    foreach ($major in $majors) {
        if ($major -lt 17) { continue }
        $found = @($capabilities.generators | Where-Object { $_.name -match ('^Visual Studio ' + $major + ' ') })
        if ($found.Count -gt 0) { return $found[0].name }
    }
    throw 'This CMake version does not support the installed Visual Studio toolchain. Update CMake or pass a configured -CMakeGenerator.'
}
function Invoke-OwnedTest([string]$FilePath, [string[]]$Arguments) {
    # .NET Framework/PowerShell 5 has no ProcessStartInfo.ArgumentList. Quote
    # arguments using the Windows command-line rules, without invoking a shell.
    $quoted = foreach ($argument in $Arguments) {
        if ($null -eq $argument -or $argument.IndexOf([char]0) -ge 0) { throw 'Invalid test argument.' }
        '"' + ($argument -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $FilePath
    $start.Arguments = $quoted -join ' '
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw ('Could not start isolated test: ' + $FilePath) }
        if (-not $process.WaitForExit(15 * 60 * 1000)) {
            # The exact Process instance was created above; no process-name or
            # port-based cleanup can affect the installed app or other tools.
            try { $process.Kill(); $process.WaitForExit() } catch { }
            throw ('Isolated test exceeded 15 minutes: ' + [IO.Path]::GetFileName($FilePath))
        }
        if ($process.ExitCode -ne 0) { throw ('Isolated test failed with exit code ' + $process.ExitCode + ': ' + [IO.Path]::GetFileName($FilePath)) }
    }
    finally { $process.Dispose() }
}

$dotnet = Resolve-BuildTool $DotNetPath 'Install the .NET 8 and .NET 9 SDKs, or pass -DotNetPath.'
$sdkVersions = @(& $dotnet --list-sdks)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect installed .NET SDKs.' }
foreach ($major in @('8', '9')) {
    if (-not @($sdkVersions | Where-Object { $_ -match ('^' + $major + '\.') }).Count) {
        throw ('The .NET ' + $major + ' SDK is required. Install SDKs 8 and 9 before building.')
    }
}
foreach ($relative in @('LocalTalk\LocalTalk.csproj', 'FoxTransLocalHost\FoxTransLocalHost.csproj',
    'FoxTransSource\native\CMakeLists.txt', 'FoxTrans-VRChatMuteSync.ps1', 'FoxTrans-OscSentenceProxy.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relative) -PathType Leaf)) { throw ('Required source file is missing: ' + $relative) }
}

Push-Location $repoRoot
try {
    if ($SkipNative) {
        if (-not (Test-Path -LiteralPath $nativeDestination -PathType Leaf)) {
            throw '-SkipNative requires an already-built FoxTransLocalHost\Native\FoxTrans.Native.dll. Run a normal build first.'
        }
        Write-Warning 'Reusing the existing native DLL. CI and clean-source validation must build it from source.'
    }
    else {
        $cmake = Resolve-BuildTool $CMakePath 'Install CMake 3.21+ and Visual Studio C++ build tools, or pass -CMakePath.'
        if (-not $CMakeGenerator) { $CMakeGenerator = Select-VisualStudioGenerator $cmake }
        Write-Host ('Native build generator: ' + $CMakeGenerator)
        Write-Host 'Building the native Windows capture library from the vendored C source.'
        $configureArguments = @('-S', (Join-Path $repoRoot 'FoxTransSource\native'), '-B', $nativeOutput, '-G', $CMakeGenerator)
        if ($CMakeGenerator.StartsWith('Visual Studio ', [StringComparison]::OrdinalIgnoreCase)) { $configureArguments += @('-A', 'x64') }
        Invoke-BuildTool $cmake $configureArguments
        Invoke-BuildTool $cmake @('--build', $nativeOutput, '--config', $Configuration, '--parallel', '2')
        $nativeBuilt = Join-Path $nativeOutput 'output\FoxTrans.Native.dll'
        if (-not (Test-Path -LiteralPath $nativeBuilt -PathType Leaf)) { throw 'CMake did not produce FoxTrans.Native.dll.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $nativeDestination) -Force | Out-Null
        Copy-Item -LiteralPath $nativeBuilt -Destination $nativeDestination -Force
    }

    $verb = if ($Publish) { 'publish' } else { 'build' }
    foreach ($project in @(
        @{ Source = 'LocalTalk\LocalTalk.csproj'; Output = $appOutput },
        @{ Source = 'FoxTransLocalHost\FoxTransLocalHost.csproj'; Output = $hostOutput }
    )) {
        $buildArguments = @($verb, (Join-Path $repoRoot $project.Source), '-c', $Configuration, '-r', 'win-x64',
            '-p:UseSharedCompilation=false', '-m:1', '-o', $project.Output)
        if ($Publish) { $buildArguments += @('--self-contained', 'true', '-p:PublishSingleFile=false') }
        else { $buildArguments += '-p:SelfContained=false' }
        Invoke-BuildTool $dotnet $buildArguments
        foreach ($runtimeFile in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
            $present = Test-Path -LiteralPath (Join-Path $project.Output $runtimeFile) -PathType Leaf
            if ($present -ne [bool]$Publish) {
                throw ('Build output has an unexpected runtime layout: ' + $project.Output +
                    '. Keep framework-dependent build and self-contained publish output separate. File: ' + $runtimeFile)
            }
        }
    }

    # The dashboard locates packaged helpers relative to itself. Assemble the
    # same directory relationship for an explicit developer launch, too.
    $toolsOutput = Join-Path $appOutput 'tools'
    $packagedHost = Join-Path $toolsOutput 'FoxTransLocalHost'
    New-Item -ItemType Directory -Path $packagedHost -Force | Out-Null
    Copy-Item -Path (Join-Path $hostOutput '*') -Destination $packagedHost -Recurse -Force
    foreach ($helper in @('FoxTrans-VRChatMuteSync.ps1', 'FoxTrans-OscSentenceProxy.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $helper) -Destination (Join-Path $toolsOutput $helper) -Force
    }

    if ($RunTests) {
        $node = Resolve-BuildTool $NodePath 'Install Node.js 22+ to run the offline website checks, or pass -NodePath.'
        $testRoot = Join-Path $artifactsRoot ('test-results\run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        $testTemp = Join-Path $testRoot 'temp'
        $dashboardReport = Join-Path $testRoot 'dashboard'
        $hostReport = Join-Path $testRoot 'host'
        New-Item -ItemType Directory -Path $testTemp, $hostReport -Force | Out-Null
        $previousEnvironment = @{}
        foreach ($name in @('SAYWHAT_DATA_ROOT', 'LOCALTALK_DATA_ROOT', 'TEMP', 'TMP')) {
            $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }
        try {
            [Environment]::SetEnvironmentVariable('SAYWHAT_DATA_ROOT', (Join-Path $testRoot 'data'), 'Process')
            [Environment]::SetEnvironmentVariable('LOCALTALK_DATA_ROOT', (Join-Path $testRoot 'data'), 'Process')
            [Environment]::SetEnvironmentVariable('TEMP', $testTemp, 'Process')
            [Environment]::SetEnvironmentVariable('TMP', $testTemp, 'Process')
            Write-Host 'Running isolated dashboard and capture-host checks. No AI models, audio devices or live OSC are used.'
            Invoke-OwnedTest (Join-Path $appOutput 'SayWhat.exe') @('--ui-self-test', $dashboardReport)
            $resultFile = Join-Path $dashboardReport 'results.log'
            if (-not (Test-Path -LiteralPath $resultFile -PathType Leaf) -or
                -not ((Get-Content -LiteralPath $resultFile -Raw) -match '^PASS ')) {
                throw ('Dashboard checks did not produce a passing report. See ' + $dashboardReport)
            }
            Get-Content -LiteralPath $resultFile
            Invoke-OwnedTest (Join-Path $hostOutput 'FoxTrans.LocalHost.exe') @('--self-test', '--root', $hostReport)
            # Process/socket handoff fixtures are a separate developer executable,
            # never compiled into or copied beside the distributed application.
            $portTestOutput = Join-Path $managedOutput 'managed-port-tests'
            Invoke-BuildTool $dotnet @('build', (Join-Path $repoRoot 'Packaging\ManagedPortTests\ManagedPortTests.csproj'),
                '-c', $Configuration, '-r', 'win-x64', '-p:SelfContained=false', '-p:UseSharedCompilation=false', '-m:1', '-o', $portTestOutput)
            Invoke-OwnedTest (Join-Path $portTestOutput 'LocalTalk.exe') @($appOutput, (Join-Path $testRoot 'managed-port-readiness'))
            $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            Invoke-BuildTool $windowsPowerShell @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'Packaging\Test-VRChatMuteSync-Discovery.ps1'))
            Invoke-BuildTool $windowsPowerShell @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'Packaging\Test-UpdateInstallerContract.ps1'), '-SourceRoot', $repoRoot)
            Invoke-BuildTool $windowsPowerShell @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'Packaging\Test-WindowsCompatibility.ps1'), '-SourceRoot', $repoRoot)
            Invoke-BuildTool $node @((Join-Path $repoRoot 'Website\check.mjs'))
            Invoke-BuildTool $node @((Join-Path $repoRoot 'Website\i18n-check.mjs'))
        }
        finally {
            foreach ($name in $previousEnvironment.Keys) {
                [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
            }
        }
        Write-Host ('Offline reports: ' + $testRoot)
    }

    Write-Host ('Ready: ' + (Join-Path $appOutput 'SayWhat.exe'))
    Write-Host 'Nothing was installed or started. Launch the app yourself when ready; models download only through its setup controls.'
}
finally { Pop-Location }

<#
.SYNOPSIS
Build and verify an unreleased installer candidate without publishing it.
.DESCRIPTION
Requires a clean Windows checkout, the normal build prerequisites, PowerShell 7
and an existing Inno Setup compiler. Downloads only the pinned packaging runtime;
never installs software, runs an installer, downloads AI weights or starts capture.
The output is for manual review and antivirus analysis, not an app update feed.
#>
[CmdletBinding()]
param(
    [string]$ExpectedCommit = $env:GITHUB_SHA,
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Candidate packaging requires PowerShell 7 on 64-bit Windows.'
}
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$candidateRoot = Join-Path $repoRoot 'artifacts\release-candidate'
if (Test-Path -LiteralPath $candidateRoot) {
    throw 'The candidate output already exists. Use a fresh checkout; existing candidate files are not replaced.'
}

Push-Location -LiteralPath $repoRoot
try {
    $sourceCommit = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Could not identify the source commit.' }
    if ($ExpectedCommit -and ($ExpectedCommit -notmatch '^[a-fA-F0-9]{40}$' -or $sourceCommit -cne $ExpectedCommit.ToLowerInvariant())) {
        throw 'The checked-out source does not match the requested candidate commit.'
    }
    $pendingSource = @(& git status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $pendingSource.Count -ne 0) { throw 'Candidate builds require a clean tracked checkout with no untracked source files.' }
    if (-not $CompilerPath) {
        $compilerCandidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
        )
        $CompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        throw 'Inno Setup was not found in Program Files. Use an existing compiler with -CompilerPath; this workflow does not install it.'
    }
    $CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'LocalTalk\LocalTalk.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$') { throw 'The source project has no valid candidate version.' }

    & node (Join-Path $PSScriptRoot 'Check-PublicSource.mjs') --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Public-source audit fixtures failed.' }
    & node (Join-Path $PSScriptRoot 'Check-PublicSource.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Public-source audit failed.' }

    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -Publish -RunTests
    & (Join-Path $PSScriptRoot 'Prepare-Packaging.ps1')
    $appPublish = Join-Path $repoRoot 'artifacts\publish\app'
    $hostPublish = Join-Path $repoRoot 'artifacts\publish\host'
    & (Join-Path $repoRoot 'Packaging\Build-Installer.ps1') -Version $version -CompilerPath $CompilerPath -SkipPublish `
        -AppPublishDirectory $appPublish -HostPublishDirectory $hostPublish |
        Tee-Object -Variable installerRecords | Out-Host
    $installers = @($installerRecords | Where-Object { $_.PSObject.Properties['Installer'] -and $_.PSObject.Properties['Payload'] })
    if ($installers.Count -ne 1) { throw 'The installer build did not return one complete staging receipt.' }
    $built = $installers[0]
    $sourceZip = Join-Path $built.Payload 'source\SayWhat-Corresponding-Source.zip'
    $stage = Split-Path -Parent $built.Payload
    $installerHash = (Get-FileHash -LiteralPath $built.Installer -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceHash = (Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
    & (Join-Path $repoRoot 'Packaging\Export-Release.ps1') -Version $version -InstallerPath $built.Installer `
        -StageDirectory $stage -ExpectedInstallerSha256 $installerHash -ExpectedSourceSha256 $sourceHash |
        Tee-Object -Variable exportRecords | Out-Host
    $exports = @($exportRecords | Where-Object { $_.PSObject.Properties['ExportDirectory'] -and $_.PSObject.Properties['Validation'] })
    if ($exports.Count -ne 1 -or $exports[0].Validation -cne 'PASS' -or $exports[0].Published -ne $false) {
        throw 'The release exporter did not verify an unpublished binary/source pair.'
    }
    $exported = $exports[0]
    $rawDll = Join-Path $appPublish 'SayWhat.dll'
    $stagedDll = Join-Path $built.Payload 'SayWhat.dll'
    $dllHash = (Get-FileHash -LiteralPath $rawDll -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($dllHash -ne (Get-FileHash -LiteralPath $stagedDll -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'The diagnostic DLL differs from the app DLL included in the installer stage.'
    }

    # Only the verified export and its exact raw app DLL enter this directory.
    # No build tree, test logs, local settings, package cache or model cache does.
    New-Item -ItemType Directory -Path $candidateRoot | Out-Null
    Copy-Item -LiteralPath $exported.ExportDirectory -Destination (Join-Path $candidateRoot 'package') -Recurse
    $diagnostics = Join-Path $candidateRoot 'diagnostics'
    New-Item -ItemType Directory -Path $diagnostics | Out-Null
    Copy-Item -LiteralPath $rawDll -Destination (Join-Path $diagnostics 'SayWhat.dll')
    $copiedInputs = @(
        @{ Path = (Join-Path $candidateRoot ('package\' + [IO.Path]::GetFileName($built.Installer))); Hash = $installerHash },
        @{ Path = (Join-Path $candidateRoot 'package\SayWhat-Corresponding-Source.zip'); Hash = $sourceHash },
        @{ Path = (Join-Path $diagnostics 'SayWhat.dll'); Hash = $dllHash }
    )
    foreach ($inputFile in $copiedInputs) {
        if ((Get-FileHash -LiteralPath $inputFile.Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputFile.Hash) {
            throw 'A candidate input changed while copying. No candidate may be uploaded.'
        }
    }
    $notice = @"
SayWhat? $version - UNRELEASED REVIEW CANDIDATE
Source commit: $sourceCommit

This artifact is for manual review and antivirus analysis. It is not a published
release or a certified installation, and it has not changed the website or the
app update feed. No installer was executed by this build. Keep Windows and
antivirus protections enabled when reviewing it.

package/ contains the verified installer, matching GPL corresponding-source ZIP,
notices, licenses and export checksums. diagnostics/SayWhat.dll is the exact app
DLL from that installer stage, supplied separately for inspection. No AI model
weights, recordings, saved People, settings or developer test logs are included.
SHA256SUMS.txt identifies these bytes; it does not authenticate a publisher.
"@
    [IO.File]::WriteAllText((Join-Path $candidateRoot 'CANDIDATE-README.txt'), $notice, [Text.UTF8Encoding]::new($false))
    $files = @(Get-ChildItem -LiteralPath $candidateRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($candidateRoot, $_.FullName).Replace('\','/'); Bytes = $_.Length;
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{
        SchemaVersion = 1; App = 'SayWhat?'; Version = $version; Status = 'unreleased-review-candidate'; Published = $false;
        SourceCommit = $sourceCommit; CreatedUtc = [DateTime]::UtcNow.ToString('O');
        InstallerSha256 = $installerHash; CorrespondingSourceSha256 = $sourceHash; RawAppDllSha256 = $dllHash;
        InnoCompilerVersion = (Get-Item -LiteralPath $CompilerPath).VersionInfo.ProductVersion;
        ModelsBundled = $false; UserDataIncluded = $false; Files = $files
    }
    [IO.File]::WriteAllText((Join-Path $candidateRoot 'candidate-manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $checksums = @(Get-ChildItem -LiteralPath $candidateRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' +
            [IO.Path]::GetRelativePath($candidateRoot, $_.FullName).Replace('\','/')
    })
    [IO.File]::WriteAllLines((Join-Path $candidateRoot 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
    Write-Host ('Verified unreleased candidate: ' + $candidateRoot)
    Write-Host ('Source commit: ' + $sourceCommit + '; installer SHA-256: ' + $installerHash)
    [pscustomobject]@{ CandidateDirectory = $candidateRoot; Version = $version; SourceCommit = $sourceCommit; Published = $false }
}
finally { Pop-Location }

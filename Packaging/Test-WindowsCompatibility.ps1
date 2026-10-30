<#
.SYNOPSIS
Check the installer OS boundary without executing setup or changing Windows.
.DESCRIPTION
These source-contract checks complement the app's simulated capture-capability
tests. They are not a Windows 10 installation or live inference test.
#>
[CmdletBinding()]
param([string]$SourceRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$compatibilityRoot = [IO.Path]::GetFullPath($SourceRoot)
$installerText = Get-Content -LiteralPath (Join-Path $compatibilityRoot 'Packaging\SayWhat.iss') -Raw
$minimumMatch = [regex]::Matches($installerText, '(?m)^MinVersion=([0-9.]+)\s*$')
if ($minimumMatch.Count -ne 1) { throw 'Expected one unambiguous installer MinVersion.' }
$minimum = [version]$minimumMatch[0].Groups[1].Value
if ($minimum -ne [version]'10.0.17763') { throw 'The base installer must allow Windows 10 1809+ independently of optional WSL/GPU speech and process audio capture.' }
$checks = 1
foreach ($fixture in @(
    @{ Version = '6.3.9600'; Allowed = $false },
    @{ Version = '10.0.14393'; Allowed = $false },
    @{ Version = '10.0.17762'; Allowed = $false },
    @{ Version = '10.0.17763'; Allowed = $true },
    @{ Version = '10.0.18362'; Allowed = $true },
    @{ Version = '10.0.19041'; Allowed = $true },
    @{ Version = '10.0.19043'; Allowed = $true },
    @{ Version = '10.0.19044'; Allowed = $true },
    @{ Version = '10.0.19045'; Allowed = $true },
    @{ Version = '10.0.20348'; Allowed = $true },
    @{ Version = '10.0.22000'; Allowed = $true },
    @{ Version = '10.0.26100'; Allowed = $true }
)) {
    if (([version]$fixture.Version -ge $minimum) -ne $fixture.Allowed) {
        throw ('Unexpected installer OS boundary: ' + $fixture.Version)
    }
    $checks++
}
foreach ($architecture in @('ArchitecturesAllowed=x64compatible','ArchitecturesInstallIn64BitMode=x64compatible')) {
    if (-not $installerText.Contains($architecture)) { throw 'Keep this release restricted to compatible x64 systems.' }
    $checks++
}
Write-Output ('PASS: ' + $checks + ' installer Windows compatibility contract checks; no setup, registry, models or capture were executed.')

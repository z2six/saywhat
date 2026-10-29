<#
.SYNOPSIS
Download the pinned, verified translator runtime used by the Windows installer.
.DESCRIPTION
Writes only Packaging/tools in this checkout. It does not install software or
run downloaded executables. Install Inno Setup separately, or supply ISCC.exe to
Packaging/Build-Installer.ps1 using -CompilerPath.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$toolsRoot = Join-Path $repoRoot 'Packaging\tools'
$fileName = 'llama-b11401-bin-win-vulkan-x64.zip'
$expectedBytes = 33308209L
$expectedHash = '4bdbc0b2e79f04ef3e66ecd34a996499401d94501e8387dec0c8963544ef6419'
$downloadUrl = 'https://github.com/ggml-org/llama.cpp/releases/download/b11401/' + $fileName
$destination = Join-Path $toolsRoot $fileName
function Test-VerifiedArchive([string]$Path) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-Item -LiteralPath $Path).Length -eq $expectedBytes -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expectedHash
}
if (Test-VerifiedArchive $destination) { Write-Host ('Already verified: ' + $fileName); return }
if (Test-Path -LiteralPath $destination) { throw 'The existing archive failed verification. Inspect it before removing it and retrying.' }
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
$temporary = $destination + '.partial-' + [Guid]::NewGuid().ToString('N')
$client = New-Object Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(10)
$deadline = New-Object Threading.CancellationTokenSource ([TimeSpan]::FromMinutes(10))
$response = $null; $inputStream = $null; $outputStream = $null
try {
    Write-Host ('Downloading the pinned Vulkan runtime (' + $expectedBytes + ' bytes). No engine will be started.')
    $response = $client.GetAsync($downloadUrl, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
    $response.EnsureSuccessStatusCode() | Out-Null
    if ($response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $expectedBytes) { throw 'Unexpected download size.' }
    $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $outputStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $buffer = New-Object byte[] 65536; $received = 0L
    while (($count = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
        $received += $count
        if ($received -gt $expectedBytes) { throw 'Download exceeded the pinned size.' }
        $outputStream.Write($buffer, 0, $count)
    }
    $outputStream.Dispose(); $outputStream = $null
    if (-not (Test-VerifiedArchive $temporary)) { throw 'The runtime did not match its pinned size and SHA-256.' }
    [IO.File]::Move($temporary, $destination)
    Write-Host ('Verified: ' + $destination)
    Write-Host 'Inno Setup is a separate developer prerequisite. Nothing has been installed or started.'
}
finally {
    if ($null -ne $outputStream) { $outputStream.Dispose() }
    if ($null -ne $inputStream) { $inputStream.Dispose() }
    if ($null -ne $response) { $response.Dispose() }
    $client.Dispose()
    $deadline.Dispose()
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}

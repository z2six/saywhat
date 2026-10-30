[CmdletBinding()]
param(
    [switch] $AllowHostedCpuInference,
    [string] $ReceiptPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $AllowHostedCpuInference -or $env:GITHUB_ACTIONS -ne 'true' -or
    $env:GITHUB_REPOSITORY -ne 'z2six/saywhat' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    $env:RUNNER_OS -ne 'Windows' -or -not [Environment]::Is64BitProcess) {
    throw 'This explicit CPU inference test runs only on a GitHub-hosted Windows x64 runner.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $ReceiptPath) { $ReceiptPath = Join-Path $repoRoot 'artifacts/test-results/pinned-speech-smoke.json' }
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
$receiptDirectory = Split-Path -Parent $ReceiptPath
New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$workRoot = [IO.Path]::GetFullPath((Join-Path $runnerTemp ('saywhat-pinned-speech-' + [Guid]::NewGuid().ToString('N'))))
if (-not $workRoot.StartsWith($runnerTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid isolated runner path.' }
New-Item -ItemType Directory -Path $workRoot | Out-Null

# Exact app catalog artifacts, not latest URLs. Keep these in sync explicitly:
# a catalog change makes this fixture fail before downloading/running anything.
$assets = @(
    [pscustomobject]@{
        Name = 'nemo-speech-0.2.0-windows-x86_64-vulkan.zip'; Size = [long]19302464
        Sha256 = 'edf15a04ba98740aa0ab75ae4681ac7ad0a488b22b35ce2cbebd10590bb1c1e1'
        Url = 'https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.2.0/nemo-speech-0.2.0-windows-x86_64-vulkan.zip'
    },
    [pscustomobject]@{
        Name = 'nemotron-3.5-asr-streaming-0.6b.q8_0.gguf'; Size = [long]742090464
        Sha256 = '3fc991d3badad7277c11030a7519832cddaf2057aafed6d4b25147e953a070b1'
        Url = 'https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/resolve/ea30d66debe3740a08b573244286791d423d6b3e/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf'
    }
)
$phase = 'catalog-check'
$exitCode = 1
try {
    $catalog = Get-Content -LiteralPath (Join-Path $repoRoot 'LocalTalk/NativeSpeechProvisioner.cs') -Raw
    foreach ($asset in $assets) {
        if (-not $catalog.Contains($asset.Name) -or -not $catalog.Contains($asset.Sha256) -or
            -not $catalog.Contains($asset.Size.ToString('N0', [Globalization.CultureInfo]::InvariantCulture).Replace(',', '_'))) {
            throw 'Pinned smoke artifacts no longer match the app catalog.'
        }
    }
    foreach ($asset in $assets) {
        $phase = if ($asset.Name.EndsWith('.zip')) { 'runtime-download' } else { 'model-download' }
        Write-Host ('Downloading pinned ' + $phase + ' asset; expected bytes=' + $asset.Size)
        $destination = Join-Path $workRoot $asset.Name
        $http = [Net.Http.HttpClient]::new()
        $http.Timeout = [TimeSpan]::FromMinutes(5)
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
        try {
            $response = $http.GetAsync($asset.Url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
            try {
                if (-not $response.IsSuccessStatusCode) { throw 'Pinned artifact download failed.' }
                $input = $response.Content.ReadAsStreamAsync($deadline.Token).GetAwaiter().GetResult()
                $output = [IO.File]::Create($destination)
                try {
                    $buffer = [byte[]]::new(1048576)
                    $total = [long]0
                    while (($read = $input.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                        $total += $read
                        if ($total -gt $asset.Size) { throw 'Pinned download exceeded its expected size.' }
                        $output.Write($buffer, 0, $read)
                    }
                }
                finally { $output.Dispose(); $input.Dispose() }
            }
            finally { $response.Dispose() }
        }
        finally { $deadline.Dispose(); $http.Dispose() }
        if ((Get-Item -LiteralPath $destination).Length -ne $asset.Size -or
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Sha256) {
            throw 'Pinned download failed its size or SHA-256 check.'
        }
        Write-Host ('Verified ' + $phase + ' size and SHA-256.')
    }

    $phase = 'runtime-extraction'
    $runtimeRoot = Join-Path $workRoot 'runtime'
    Expand-Archive -LiteralPath (Join-Path $workRoot $assets[0].Name) -DestinationPath $runtimeRoot
    $executables = @(Get-ChildItem -LiteralPath $runtimeRoot -Filter 'nemo-speech.exe' -File -Recurse)
    if ($executables.Count -ne 1) { throw 'The verified archive does not contain exactly one speech executable.' }
    $executable = $executables[0].FullName
    foreach ($name in @('ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll', 'ggml-vulkan.dll', 'nemo_speech_asr.dll', 'nemo_speech_asr_c.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $executables[0].DirectoryName $name) -PathType Leaf)) {
            throw 'The pinned speech runtime is incomplete.'
        }
    }

    $phase = 'synthetic-file-speech'
    $wavePath = Join-Path $workRoot 'original-synthetic-english.wav'
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    & $windowsPowerShell -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'PinnedSpeechSmoke/New-SyntheticSpeech.ps1') -OutputPath $wavePath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $wavePath -PathType Leaf)) { throw 'Could not create synthetic speech from an installed Windows voice.' }

    $phase = 'probe-build'
    $probeOutput = Join-Path $workRoot 'probe'
    $intermediate = (Join-Path $workRoot 'obj') + [IO.Path]::DirectorySeparatorChar
    & dotnet build (Join-Path $PSScriptRoot 'PinnedSpeechSmoke/PinnedSpeechSmoke.csproj') -c Release -o $probeOutput --nologo "-p:BaseIntermediateOutputPath=$intermediate"
    if ($LASTEXITCODE -ne 0) { throw 'The source-only speech probe could not be built.' }

    # CPU selection is supported by the exact v0.2.0 source:
    # app/serve.cpp parses --device and copies it to asr_config.backend.gpu;
    # app/cli_util.cpp maps 'cpu' to -1. No Vulkan/GPU inference is requested.
    # https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/app/serve.cpp
    # https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/app/cli_util.cpp
    $phase = 'cpu-protocol-inference'
    & dotnet (Join-Path $probeOutput 'PinnedSpeechSmoke.dll') $executable (Join-Path $workRoot $assets[1].Name) $wavePath $ReceiptPath
    $exitCode = $LASTEXITCODE
}
catch {
    # Do not print arbitrary native output, signed redirect URLs or transcripts.
    Write-Host ('Pinned speech smoke failed during ' + $phase + ' (' + $_.Exception.GetType().Name + ').')
}
finally {
    if (Test-Path -LiteralPath $ReceiptPath -PathType Leaf) {
        $receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json
    }
    else {
        $receipt = [pscustomobject]@{
            Schema = 'saywhat-pinned-speech-smoke-v1'; SourceCommit = $env:GITHUB_SHA
            Backend = 'cpu'; Pass = $false; Error = 'fixture_failed'; Phase = $phase; Cases = @()
        }
    }
    $receipt | Add-Member -NotePropertyName VerifiedArtifacts -NotePropertyValue @($assets | Select-Object Name, Size, Sha256) -Force
    $receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ReceiptPath -Encoding utf8
    # The target was resolved and checked against the runner's private temp root
    # before creation. Never delete a caller-provided or app-data directory.
    if ($workRoot.StartsWith($runnerTemp, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode

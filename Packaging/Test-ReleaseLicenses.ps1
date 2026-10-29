[CmdletBinding()]
param([string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$reviewed = [ordered]@{
    'NAudio-MIT.txt' = '7c57209555296e73faa34fe38740aa4a86aa8250d969ab73a6e37de57c36c0a5'
    'sherpa-onnx-Apache-2.0.txt' = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
    'ONNX-Runtime-ThirdPartyNotices.txt' = '0e07b95f3a8d6230037707c5c4a2b554d12c4cb67369669ac255635528ffcee2'
    'dotnet-System-Numerics-Tensors-MIT.txt' = 'cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310'
    'libfvad-BSD-3-Clause.txt' = '78b16d4e803975e0c5d826339c607d0676192a40e2c510c10b2cb4ab4cde416a'
    'miniaudio-LICENSE.txt' = '1d13e0cd56bb6bff8c66eb67084765fbefddcfd931922d2a75c072a9efb7cde9'
    'OpenMDW-1.1.txt' = 'be21b29b1c69af5b4a4e60a6bd7bc933eec374e52be29d1ebb9e6a17a6588491'
}
foreach ($name in $reviewed.Keys) {
    $source = Join-Path $PSScriptRoot ('licenses\' + $name)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $reviewed[$name]) {
        throw ('Reviewed license is absent/changed: ' + $name + '. Review the pinned upstream text/provenance; do not silently omit it.')
    }
    if ($PayloadDirectory) {
        $copy = Join-Path $PayloadDirectory ('licenses\' + $name)
        if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
            (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $reviewed[$name]) {
            throw ('Installer payload lacks the reviewed license: ' + $name)
        }
    }
}
$nativeVersion = ''
$declarationPath = Join-Path $PSScriptRoot 'SOURCE-AND-LICENSES.md'
if (-not (Test-Path -LiteralPath $declarationPath -PathType Leaf) -or
    (Get-Content -LiteralPath $declarationPath -Raw) -notmatch 'SPDX-License-Identifier: GPL-3.0-only') {
    throw 'The approved original SayWhat contribution license declaration is missing.'
}
if ($PayloadDirectory) {
    $payloadDeclaration = Join-Path $PayloadDirectory 'SOURCE-AND-LICENSES.md'
    if (-not (Test-Path -LiteralPath $payloadDeclaration -PathType Leaf) -or
        (Get-FileHash -LiteralPath $payloadDeclaration -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $declarationPath -Algorithm SHA256).Hash) {
        throw 'Installer payload lacks the matching SayWhat GPL-3.0-only declaration.'
    }
    $nativePath = Join-Path $PayloadDirectory 'onnxruntime.dll'
    if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) { throw 'Cannot identify the bundled native ONNX Runtime.' }
    $nativeVersion = (Get-Item -LiteralPath $nativePath).VersionInfo.ProductVersion
    if ($nativeVersion -ne '1.28.2') {
        throw ('Native ONNX Runtime version changed to ' + $nativeVersion + '. Review and update its third-party notices before distribution.')
    }
    foreach ($required in @('GPL-3.0.txt','llama.cpp-MIT.txt','voxtral.cpp-MIT.txt','Inno-Setup.txt','libfvad-PATENTS','libfvad-AUTHORS','README.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PayloadDirectory ('licenses\' + $required)) -PathType Leaf)) { throw ('Required installer notice missing: ' + $required) }
    }
}
[pscustomobject]@{ ReviewedSupplementalTexts = $reviewed.Count; SourceValidation = 'PASS'; EmbeddedValidation = if ($PayloadDirectory) { 'PASS' } else { 'Not requested' }; NativeOnnxVersion = $nativeVersion; MaintainerGrantConfirmed = $true; OriginalContributionLicense = 'GPL-3.0-only'; LegalCertification = $false }

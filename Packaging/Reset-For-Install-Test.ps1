#Requires -Version 5.1
<#
.SYNOPSIS
Back up SayWhat? preferences/history for an installation test, retaining cached assets.
.DESCRIPTION
Without -Apply (or with -WhatIf) this script only reports the plan. It never launches
or stops an app, model, service or WSL distribution. -Apply moves only allowlisted
state files to a timestamped sibling backup; provisioning and model/runtime caches
are retained. The backup includes a restore manifest; no user file is deleted.
Run the installed app normally afterward, not a launcher passing --start-all.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [switch]$Apply,
    [string]$DataRoot = $(
        if (![string]::IsNullOrWhiteSpace($env:SAYWHAT_DATA_ROOT)) { $env:SAYWHAT_DATA_ROOT }
        elseif (![string]::IsNullOrWhiteSpace($env:LOCALTALK_DATA_ROOT)) { $env:LOCALTALK_DATA_ROOT }
        else {
            $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
            $currentRoot = Join-Path $localAppData 'SayWhat'
            $legacyRoot = Join-Path $localAppData 'LocalTalk'
            if (Test-Path -LiteralPath $currentRoot -PathType Container) { $currentRoot }
            elseif (Test-Path -LiteralPath $legacyRoot -PathType Container) { $legacyRoot }
            else { $currentRoot }
        }
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a link/junction in reset or asset paths: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd([char[]]@('\', '/')))
        if (!$parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Get-VerifiedForeignListener($Endpoint, $Owner) {
    # Never exempt a port alone. Only a known independent Windows service, with its
    # registered image and service-control ancestry verified, may remain running.
    # Unknown owners and WSL/app-owned processes always block the reset.
    if (!$Owner -or $Owner.Name -ne 'AsusUpdateCheck.exe') { return $null }
    $expectedImage = [IO.Path]::Combine([Environment]::SystemDirectory, 'AsusUpdateCheck.exe')
    $services = @(Get-CimInstance Win32_Service -Filter ('ProcessId = ' + $Owner.ProcessId) -ErrorAction Stop)
    $service = @($services | Where-Object {
        $_.Name -eq 'AsusUpdateCheck' -and $_.State -eq 'Running' -and
        $_.PathName -and [IO.Path]::GetFullPath($_.PathName.Trim().Trim('"')) -eq $expectedImage
    })
    if ($service.Count -ne 1 -or !(Test-Path -LiteralPath $expectedImage -PathType Leaf)) { return $null }
    if ($Owner.ExecutablePath -and [IO.Path]::GetFullPath($Owner.ExecutablePath) -ne $expectedImage) { return $null }
    $ancestorPid = [int]$Owner.ParentProcessId
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    $serviceControlAncestor = $false
    for ($depth = 0; $depth -lt 8 -and $ancestorPid -gt 0; $depth++) {
        if (!$seen.Add($ancestorPid)) { break }
        $ancestors = @(Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $ancestorPid) -ErrorAction Stop)
        if ($ancestors.Count -ne 1) { break }
        $ancestor = $ancestors[0]
        if ($ancestor.Name -eq 'services.exe') {
            if (!$ancestor.ExecutablePath -or [IO.Path]::GetFullPath($ancestor.ExecutablePath) -eq
                [IO.Path]::Combine([Environment]::SystemDirectory, 'services.exe')) { $serviceControlAncestor = $true }
            break
        }
        # A shell/WSL/app launch chain is not proof of a foreign Windows service.
        if ($ancestor.Name -match '^(SayWhat|LocalTalk|IncomingTranslator|FoxTrans[.].*|wsl.*|powershell|pwsh|llama-server|voxtral-server)[.]exe$') { break }
        $ancestorPid = [int]$ancestor.ParentProcessId
    }
    if (!$serviceControlAncestor) { return $null }
    return [pscustomobject]@{ LocalAddress = $Endpoint.LocalAddress; Port = $Endpoint.LocalPort;
        ProcessId = $Owner.ProcessId; ImageName = $Owner.Name; ServiceName = $service[0].Name;
        RegisteredImage = $expectedImage; Action = 'Preserve unrelated Windows service; do not stop or modify' }
}

function Get-ClosedState {
    $problems = New-Object 'System.Collections.Generic.List[string]'
    $preservedListeners = New-Object 'System.Collections.Generic.List[object]'
    $names = @('SayWhat', 'LocalTalk', 'IncomingTranslator', 'FoxTrans.LocalHost', 'FoxTrans.Cli', 'FoxTrans', 'FoxTrans.Desktop')
    foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
        $problems.Add("Still open: $($process.ProcessName) (PID $($process.Id)).")
    }
    try {
        $helpers = @(Get-CimInstance Win32_Process -Filter "Name = 'wsl.exe' OR Name = 'powershell.exe' OR Name = 'pwsh.exe'" -ErrorAction Stop |
            Where-Object {
                $_.ProcessId -ne $PID -and $_.CommandLine -and
                $_.CommandLine -match 'run-voxtral[.]sh|voxtral-server|FoxTrans-VRChatMuteSync[.]ps1|FoxTrans-OscSentenceProxy[.]ps1'
            })
        foreach ($helper in $helpers) { $problems.Add("Translation helper still open (PID $($helper.ProcessId)).") }
    }
    catch { $problems.Add('Could not verify translation-helper processes. Repeat with permission to read process metadata.') }
    try {
        # OS listener metadata is authoritative and does not connect to any service/model.
        $listeners = @(Get-NetTCPConnection -ErrorAction Stop |
            Where-Object { $_.State -eq 'Listen' -and $_.LocalPort -in @(8080, 8081, 1235, 1236) })
        foreach ($endpoint in $listeners) {
            try {
                $owners = @(Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $endpoint.OwningProcess) -ErrorAction Stop)
                $foreign = if ($owners.Count -eq 1) { Get-VerifiedForeignListener $endpoint $owners[0] } else { $null }
                if ($foreign) { $preservedListeners.Add($foreign) }
                else { $problems.Add("Local TCP port $($endpoint.LocalPort) has an app/WSL/unknown owner (PID $($endpoint.OwningProcess)). The script will not stop it.") }
            }
            catch { $problems.Add("Could not verify the owner of local TCP port $($endpoint.LocalPort).") }
        }
    }
    catch { $problems.Add('Could not verify translation TCP listeners from Windows metadata.') }
    try {
        foreach ($endpoint in @(Get-NetUDPEndpoint -ErrorAction Stop | Where-Object { $_.LocalPort -in @(9001, 9010, 9011) })) {
            $problems.Add("Translation-control UDP port $($endpoint.LocalPort) is in use. The script will not stop its owner.")
        }
    }
    catch { $problems.Add('Could not verify translation-control UDP ports are closed.') }
    return [pscustomobject]@{ Problems = $problems.ToArray(); PreservedForeignListeners = $preservedListeners.ToArray() }
}

if ($DataRoot -notmatch '^[A-Za-z]:[\\/]') { throw 'Use an explicit local drive path for the SayWhat data folder (or legacy LocalTalk folder).' }
$resetRoot = [IO.Path]::GetFullPath($DataRoot).TrimEnd([char[]]@('\', '/'))
if ([IO.Path]::GetFileName($resetRoot) -notin @('SayWhat', 'LocalTalk')) { throw 'The reset target must be a folder named SayWhat or legacy LocalTalk, not a parent/workspace/profile folder.' }
Assert-PlainPath $resetRoot
if (!(Test-Path -LiteralPath $resetRoot -PathType Container)) {
    [pscustomobject]@{ Mode = 'No changes'; DataRoot = $resetRoot; Message = 'No app data folder exists.' }
    return
}
$backupRoot = Join-Path ([IO.Path]::GetDirectoryName($resetRoot)) (
    [IO.Path]::GetFileName($resetRoot) + '.install-test-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Assert-PlainPath $backupRoot

# Literal root-level file names only: never recurse into assets, WSL disks or another app.
$stateNames = @(
    'vrchat-translation-settings.json', 'setup-progress.json',
    'config.fast-chinese.jsonc', 'config.sentence-mode.jsonc',
    'incoming-ai-speaker-profiles.json', 'incoming-speaker-feedback.json', 'incoming-speaker-profiles.json',
    'foxtrans-local-host.log', 'foxtrans-mute-sync.log', 'foxtrans-osc-sentence.log', 'foxtrans-provider-errors.log',
    'incoming-translator.log', 'incoming-vr-overlay.log', 'saywhat.log', 'localtalk.log'
)
$moves = @(foreach ($name in $stateNames) {
    $source = Join-Path $resetRoot $name
    if (Test-Path -LiteralPath $source) {
        Assert-PlainPath $source
        $file = Get-Item -LiteralPath $source -Force
        if ($file.PSIsContainer) { throw "Expected an app state file, not a directory: $source" }
        [pscustomobject]@{ Name = $name; Source = $source; Destination = (Join-Path $backupRoot $name); Length = $file.Length }
    }
})

# Retain the currently imported official CAM++ asset in the default cache if absent.
# Validate the exact bytes both before planning and after copying; never trust only a name.
$speakerName = '3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx'
$speakerBytes = 28281138L
$speakerSha = 'f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11'
$speakerTarget = Join-Path $resetRoot ('models\speaker-id\' + $speakerName)
$speakerCopy = $null
$assetNote = 'Existing model/download/runtime caches and provisioning.json will be left untouched.'
$settingsPath = Join-Path $resetRoot 'vrchat-translation-settings.json'
if (!(Test-Path -LiteralPath $speakerTarget) -and (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    try {
        $oldSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($oldSettings.PSObject.Properties.Name -contains 'SpeakerModelPath' -and $oldSettings.SpeakerModelPath) {
            $candidate = [IO.Path]::GetFullPath([string]$oldSettings.SpeakerModelPath)
            if ($candidate -match '^[A-Za-z]:[\\/]' -and [IO.Path]::GetFileName($candidate) -eq $speakerName -and
                (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                Assert-PlainPath $candidate
                Assert-PlainPath $speakerTarget
                if ((Get-Item -LiteralPath $candidate).Length -eq $speakerBytes -and
                    (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -eq $speakerSha) {
                    $speakerCopy = [pscustomobject]@{ Source = $candidate; Destination = $speakerTarget; Length = $speakerBytes; Sha256 = $speakerSha }
                }
                else { $assetNote += ' The external speaker asset did not match the pinned CAM++ file; it will not be copied.' }
            }
        }
    }
    catch { $assetNote += ' The external speaker asset could not be verified; it will not be copied. Import it later if needed.' }
}

$closedState = Get-ClosedState
$problems = @($closedState.Problems)
$dryRun = !$Apply -or $WhatIfPreference
$plan = [pscustomobject]@{
    Mode = $(if ($dryRun) { 'Dry run - no changes' } else { 'Apply requested' })
    DataRoot = $resetRoot
    BackupDirectory = $backupRoot
    MoveFiles = $moves
    VerifiedSpeakerAssetCopy = $speakerCopy
    Preserved = @('provisioning.json', 'models\', 'downloads\', 'runtimes\', 'all unknown files/folders', 'Ubuntu and other applications')
    ClosedStateProblems = $problems
    PreservedForeignListeners = $closedState.PreservedForeignListeners
    Note = $assetNote
}
$plan
if ($dryRun) { return }
if ($problems.Count) { throw ('Reset refused: close translation tools first. ' + ($problems -join ' ')) }
if (!$moves.Count) { Write-Verbose 'There are no app state files to reset.'; return }
if (!$PSCmdlet.ShouldProcess($resetRoot, "Back up $($moves.Count) specific app state files, retaining cached models and provisioning")) { return }

$mutex = $null
$ownsMutex = $false
$moved = New-Object 'System.Collections.Generic.List[object]'
$manifest = [ordered]@{ FormatVersion = 1; CreatedUtc = [DateTime]::UtcNow.ToString('o'); DataRoot = $resetRoot;
    PlannedMoves = $moves; CompletedMoves = @(); AssetReuse = $speakerCopy; Status = 'Preparing' }
$manifestPath = Join-Path $backupRoot 'restore-manifest.json'
function Save-RestoreManifest {
    $temporaryManifest = $manifestPath + '.new-' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporaryManifest, ($manifest | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
        # PowerShell may coerce a null string argument to an empty path. An explicit
        # sibling journal backup is portable across Windows PowerShell 5.1 and 7.
        if ([IO.File]::Exists($manifestPath)) { [IO.File]::Replace($temporaryManifest, $manifestPath, ($manifestPath + '.previous')) }
        else { [IO.File]::Move($temporaryManifest, $manifestPath) }
    }
    finally { if (Test-Path -LiteralPath $temporaryManifest -PathType Leaf) { Remove-Item -LiteralPath $temporaryManifest } }
}
try {
    # The compatibility mutex also blocks pre-rebrand dashboards from starting.
    # This never starts/stops a process or renames/moves either app data directory.
    $created = $false
    $mutex = [Threading.Mutex]::new($true, 'Local\LocalTalkDesktop', [ref]$created)
    if (!$created) { throw 'Reset refused: the app desktop mutex already exists. Close SayWhat? and older LocalTalk apps first.' }
    $ownsMutex = $true
    $closedState = Get-ClosedState
    $problems = @($closedState.Problems)
    if ($problems.Count) { throw ('Reset refused: ' + ($problems -join ' ')) }
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    Save-RestoreManifest
    if ($speakerCopy) {
        Assert-PlainPath $speakerCopy.Source
        Assert-PlainPath $speakerCopy.Destination
        $speakerDirectory = [IO.Path]::GetDirectoryName($speakerCopy.Destination)
        New-Item -ItemType Directory -Path $speakerDirectory -Force | Out-Null
        $temporaryAsset = $speakerCopy.Destination + '.install-test-copy-' + [Guid]::NewGuid().ToString('N')
        try {
            [IO.File]::Copy($speakerCopy.Source, $temporaryAsset, $false)
            if ((Get-Item -LiteralPath $temporaryAsset).Length -ne $speakerBytes -or
                (Get-FileHash -LiteralPath $temporaryAsset -Algorithm SHA256).Hash -ne $speakerSha) {
                throw 'The copied speaker asset failed verification; settings were not reset.'
            }
            [IO.File]::Move($temporaryAsset, $speakerCopy.Destination) # No overwrite of an existing model.
        }
        finally { if (Test-Path -LiteralPath $temporaryAsset -PathType Leaf) { Remove-Item -LiteralPath $temporaryAsset } }
    }
    foreach ($move in $moves) {
        Assert-PlainPath $move.Source
        if ((Get-Item -LiteralPath $move.Source).Length -ne $move.Length) { throw 'An app state file changed after planning. Stop and inspect the backup before retrying.' }
        Move-Item -LiteralPath $move.Source -Destination $move.Destination
        $moved.Add($move)
        $manifest.CompletedMoves = @($moved.ToArray())
        $manifest.Status = 'Moving state files'
        Save-RestoreManifest
    }
    $manifest.Status = 'Complete'
    Save-RestoreManifest
    Write-Host "Settings/history backed up to $backupRoot. Cached assets and provisioning were preserved."
    Write-Host 'Open the installed app normally for first-run setup. Do not use --start-all for this test.'
}
catch {
    # Restore moved state where safe, but never overwrite a file created by another process.
    for ($index = $moved.Count - 1; $index -ge 0; $index--) {
        $move = $moved[$index]
        try {
            if (!(Test-Path -LiteralPath $move.Source) -and (Test-Path -LiteralPath $move.Destination -PathType Leaf)) {
                Move-Item -LiteralPath $move.Destination -Destination $move.Source
            }
        }
        catch { Write-Warning "A state file remains recoverable in the backup: $($move.Destination)" }
    }
    $manifest.Status = 'Failed - restore attempted without overwriting newer state'
    if (Test-Path -LiteralPath $backupRoot -PathType Container) { try { Save-RestoreManifest } catch { } }
    throw
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}

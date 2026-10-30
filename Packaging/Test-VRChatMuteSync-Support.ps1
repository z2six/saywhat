param([string]$FixtureBase = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'FoxTrans-VRChatMuteSync.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ('Controller syntax failed: ' + ($errors.Message -join '; ')) }
# Definitions only. No controller body, real process, native helper, audio,
# model, network, game or clipboard operation is invoked by this fixture.
foreach ($name in @('Read-OwnVoiceRuntimeSettings', 'Resolve-OwnVoicePauseWhenVrchatMuted',
    'Get-OwnVoicePauseWhenVrchatMuted', 'Get-FinishCapturedSetting', 'Write-ControllerFailure', 'Start-FoxTransClient')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing function $name" }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$script:checks = 0
function Check([bool]$condition, [string]$description) {
    if (-not $condition) { throw ('Controller support fixture: ' + $description) }
    $script:checks++
}
$script:statusLines = [Collections.Generic.List[string]]::new()
function Write-Status([string]$message) { $script:statusLines.Add($message) }
function Send-ProxyMuted([bool]$muted) { }
function Send-LocalControl([string]$address, [bool]$value, [int]$port) { }
function Start-Process {
    param([string]$FilePath, $ArgumentList, [switch]$NoNewWindow, [switch]$PassThru, [string]$RedirectStandardError)
    $script:fakeStarts++
    return [pscustomobject]@{ Id = 7; HasExited = $false; ExitCode = 0 }
}
$DataRoot = Join-Path ([IO.Path]::GetFullPath($FixtureBase)) ('saywhat-controller-support-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($DataRoot)
$legacy = Join-Path $DataRoot 'vrchat-translation-settings.json'
$projection = Join-Path $DataRoot 'own-voice-runtime.json'
$utf8 = [Text.UTF8Encoding]::new($false)
try {
    [IO.File]::WriteAllText($legacy, '{"OwnVoicePauseWhenVrchatMuted":false,"FinishCapturedSentenceOnMute":false}', $utf8)
    Check (-not (Get-OwnVoicePauseWhenVrchatMuted) -and -not (Get-FinishCapturedSetting)) 'legacy preferences remain supported when no projection exists'
    [IO.File]::WriteAllText($legacy, ('{"CustomProfiles":"' + ('x' * 70000) + '"}'), $utf8)
    Check ((Get-OwnVoicePauseWhenVrchatMuted) -and (Get-FinishCapturedSetting)) 'an oversized legacy file remains bounded and fails closed'
    [IO.File]::WriteAllText($projection, '{"OwnVoicePauseWhenVrchatMuted":false,"FinishCapturedSentenceOnMute":false}', $utf8)
    Check (-not (Get-OwnVoicePauseWhenVrchatMuted) -and -not (Get-FinishCapturedSetting)) 'the small projection keeps capture policy independent of a large profile collection'
    [IO.File]::WriteAllText($legacy, '{"OwnVoicePauseWhenVrchatMuted":false,"FinishCapturedSentenceOnMute":false}', $utf8)
    foreach ($invalid in @('[]', '{', '{}', '{"OwnVoicePauseWhenVrchatMuted":"false","FinishCapturedSentenceOnMute":"false"}')) {
        [IO.File]::WriteAllText($projection, $invalid, $utf8)
        Check ((Get-OwnVoicePauseWhenVrchatMuted) -and (Get-FinishCapturedSetting)) 'a present malformed projection cannot revive stale legacy preferences'
    }
    $script:statusLines.Clear()
    Write-ControllerFailure ([UnauthorizedAccessException]::new('PRIVATE path and source must never be copied'))
    Check ($script:statusLines.Count -eq 1 -and $script:statusLines[0] -match '^SAYWHAT_STATUS component=microphone-controller event=failed code=access-denied hresult=0x[0-9A-F]{8}$') 'generic access failure gets only typed fixed metadata, not a microphone-permission claim'
    Check (-not $script:statusLines[0].Contains('PRIVATE')) 'failure evidence excludes exception messages and paths'
    $script:statusLines.Clear()
    Write-ControllerFailure ([Net.Sockets.SocketException]::new(10048))
    Check ($script:statusLines[0] -match '^SAYWHAT_STATUS component=microphone-controller event=failed code=endpoint-unavailable hresult=0x[0-9A-F]{8} native=10048$') 'a typed socket failure retains its actual native code'
    $script:statusLines.Clear()
    $wrapped = [TypeInitializationException]::new('PRIVATE type', [DllNotFoundException]::new('PRIVATE dll'))
    Write-ControllerFailure $wrapped
    Check ($script:statusLines[0] -match 'code=native-library-missing hresult=0x[0-9A-F]{8}$' -and -not $script:statusLines[0].Contains('PRIVATE')) 'a wrapped dependency failure remains identifiable without private type or file names'
    $script:statusLines.Clear()
    $script:hostRestartCount = 0
    $script:fakeStarts = 0
    $script:foxProcess = [pscustomobject]@{ Id = 6; HasExited = $true; ExitCode = -1073741515 }
    $script:isMuted = $false; $script:usesLocalHost = $true; $script:reconfiguring = $false
    $script:lastStart = [DateTime]::MinValue
    $FoxTransPath = Join-Path $DataRoot 'FoxTrans.LocalHost.exe'
    $ConfigPath = Join-Path $DataRoot 'synthetic.jsonc'
    $ProxyLogPath = Join-Path $DataRoot 'proxy.log'
    $HostControlPort = 1
    Start-FoxTransClient
    Check ($script:statusLines.Contains('SAYWHAT_STATUS component=microphone-controller event=host-exited restart=1 exit=-1073741515')) 'a child failing before managed startup retains the actual signed exit code'
    Check ($script:statusLines.Contains('SAYWHAT_STATUS component=microphone-controller event=host-started restart=1') -and $script:fakeStarts -eq 1) 'restart metadata is emitted without changing the existing retry behavior'
    Check ($script:statusLines.FindAll([Predicate[string]]{ param($line) $line.StartsWith('SAYWHAT_STATUS ') }).TrueForAll([Predicate[string]]{ param($line) -not $line.Contains($DataRoot) })) 'lifecycle evidence excludes storage paths and process names'
    Write-Output ('PASS: ' + $script:checks + ' controller support/projection checks; definitions, inert process stubs and isolated files only.')
}
finally {
    foreach ($path in @($projection, $legacy)) { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
    [IO.Directory]::Delete($DataRoot, $false)
}

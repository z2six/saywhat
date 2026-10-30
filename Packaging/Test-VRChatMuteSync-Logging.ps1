param([string]$FixtureBase = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'FoxTrans-VRChatMuteSync.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ('Controller syntax failed: ' + ($parseErrors.Message -join '; ')) }
# Load production definitions only. Never execute the controller body, enumerate
# processes, query VRChat, start capture/models or compile its native helper.
foreach ($name in @('Write-Status', 'Set-MuteState', 'Apply-MutePolicy', 'Send-LocalControl', 'Add-OscString')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing function $name" }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$script:checks = 0
function Check([bool]$condition, [string]$label) {
    if (-not $condition) { throw "FAIL: $label" }
    $script:checks++
}
$script:consoleLines = [System.Collections.Generic.List[string]]::new()
function Write-Host([string]$Object) { $script:consoleLines.Add($Object) }
$fixtureRoot = Join-Path ([IO.Path]::GetFullPath($FixtureBase)) ('saywhat-mute-log-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$LogPath = Join-Path $fixtureRoot 'mute.log'
$listener = $null
$held = $null
try {
    $listener = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
    $listener.Client.ReceiveTimeout = 2000
    $script:fixtureControlPort = ([Net.IPEndPoint]$listener.Client.LocalEndPoint).Port
    Check ($script:fixtureControlPort -notin @(9000, 9001, 9010, 9011)) 'the isolated fixture never uses VRChat or app control ports'
    $script:startCount = 0
    $script:stopCount = 0
    function Start-FoxTransClient {
        $script:startCount++
        Send-LocalControl '/foxtrans/control/muted' $false $script:fixtureControlPort
    }
    function Stop-FoxTransClient {
        $script:stopCount++
        Send-LocalControl '/foxtrans/control/muted' $true $script:fixtureControlPort
    }
    function Receive-FixtureControl([bool]$muted) {
        $sender = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        $packet = $listener.Receive([ref]$sender)
        $tag = [Text.Encoding]::UTF8.GetString($packet).TrimEnd([char]0)
        Check ([Net.IPAddress]::IsLoopback($sender.Address) -and $tag.Contains('/foxtrans/control/muted')) 'only a loopback synthetic control packet is received'
        Check ($tag.EndsWith($(if ($muted) { ',T' } else { ',F' }))) 'the verified mute state reaches the isolated control socket'
    }
    $script:reconfiguring = $false
    $script:vrChatPresent = $true
    $script:followVrChatMute = $true
    $script:muteVerified = $false
    $script:lastVerifiedMuteState = $true
    $script:isMuted = $true
    $script:lastLogWriteWarning = [DateTime]::MinValue
    [IO.File]::WriteAllText($LogPath, 'fixture-before-lock' + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $held = [IO.FileStream]::new($LogPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Set-MuteState $false 'isolated verified fixture'
    Check ($script:muteVerified -and -not $script:lastVerifiedMuteState -and -not $script:isMuted -and $script:startCount -eq 1) 'an exclusively locked log does not prevent the verified unmute transition'
    Receive-FixtureControl $false
    Check (@($script:consoleLines | Where-Object { $_.Contains('code=log-write-unavailable') }).Count -eq 1) 'a blocked log produces one safe fallback warning on stdout'
    Set-MuteState $true 'isolated verified fixture'
    Check ($script:isMuted -and $script:lastVerifiedMuteState -and $script:stopCount -eq 1) 'the next verified mute still closes capture while the log remains locked'
    Receive-FixtureControl $true
    Check (@($script:consoleLines | Where-Object { $_.Contains('code=log-write-unavailable') }).Count -eq 1) 'repeated logging failures do not flood stdout'
    $held.Dispose(); $held = $null
    Write-Status 'recovered UTF8 fixture: 中文 한국어 日本語'
    $written = [IO.File]::ReadAllText($LogPath, [Text.Encoding]::UTF8)
    Check ($written.Contains('fixture-before-lock') -and $written.Contains('recovered UTF8 fixture: 中文 한국어 日本語')) 'logging resumes in UTF8 after the conflicting handle closes without replacing prior data'

    [IO.File]::SetAttributes($LogPath, [IO.FileAttributes]::ReadOnly)
    $script:lastLogWriteWarning = [DateTime]::MinValue
    Set-MuteState $false 'isolated readonly fixture'
    Check (-not $script:isMuted -and $script:startCount -eq 2) 'a read-only log cannot abort the verified unmute transition'
    Receive-FixtureControl $false
    Check (@($script:consoleLines | Where-Object { $_.Contains('code=log-write-unavailable') }).Count -eq 2) 'access denial uses the same bounded stdout fallback'
    [IO.File]::SetAttributes($LogPath, [IO.FileAttributes]::Normal)

    $script:muteVerified = $false
    $script:lastVerifiedMuteState = $false
    Apply-MutePolicy
    Check ($script:isMuted -and $script:stopCount -eq 2) 'loss of mute verification still fails closed; logging recovery grants no permission to capture'
    Receive-FixtureControl $true
    Write-Status 'final fixture event'
    Check ([IO.File]::ReadAllText($LogPath, [Text.Encoding]::UTF8).Contains('final fixture event')) 'later events are appended after read-only protection is removed'
    $validLogPath = $LogPath
    $argumentError = $false
    try {
        $LogPath = $validLogPath + [char]0
        Write-Status 'invalid path fixture'
    }
    catch [ArgumentException] { $argumentError = $true }
    finally { $LogPath = $validLogPath }
    Check $argumentError 'non-I/O programming errors are not hidden by the logging fallback'
}
finally {
    if ($held) { $held.Dispose() }
    if ($listener) { $listener.Dispose() }
    if ([IO.File]::Exists($LogPath)) {
        [IO.File]::SetAttributes($LogPath, [IO.FileAttributes]::Normal)
        [IO.File]::Delete($LogPath)
    }
    [IO.Directory]::Delete($fixtureRoot)
}
Write-Output "PASS: $script:checks isolated log-contention, UTF8 recovery and mute/control checks; no VRChat, processes, audio or models were used."

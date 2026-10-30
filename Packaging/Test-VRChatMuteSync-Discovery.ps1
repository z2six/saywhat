$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'FoxTrans-VRChatMuteSync.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ('Controller syntax failed: ' + ($parseErrors.Message -join '; ')) }
# Extract definitions only: no controller body, process enumeration, HTTP/OSC,
# audio, model, or personal game log is executed by these isolated fixtures.
$names = @('Get-VrChatTcpListeners', 'Get-VrChatCurrentLogHints', 'Get-VrChatQueryCandidates',
    'Test-VrChatHostInfo', 'ConvertFrom-VrChatMuteResponse', 'Test-VrChatCandidateCurrent',
    'Get-InitialMuteState', 'Update-UnverifiedMuteState', 'Set-MuteState', 'Read-OscString',
    'Add-OscString', 'Read-MuteSelf', 'Receive-VrChatOscPacket', 'Receive-PendingVrChatOsc',
    'Resolve-OwnVoicePauseWhenVrchatMuted', 'Read-OwnVoiceRuntimeSettings', 'Get-OwnVoicePauseWhenVrchatMuted', 'Update-MutePolicySetting', 'Apply-MutePolicy',
    'Get-VrChatQueryRemainingMilliseconds', 'ConvertFrom-VrChatQueryJson', 'Update-PendingMuteQuery')
foreach ($name in $names) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing function $name" }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$nativeSource = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('public static class SayWhatMuteSyncTcpV1') }, $true)
if (-not ('SayWhatMuteSyncTcpV1' -as [type])) { Add-Type -TypeDefinition $nativeSource.Value }
$script:checks = 0
function Check([bool]$condition, [string]$label) {
    if (-not $condition) { throw "FAIL: $label" }
    $script:checks++
}
function Json([string]$text) { return $text | ConvertFrom-Json }
Check ((Get-VrChatQueryRemainingMilliseconds 600 0) -eq 600) 'request deadline starts before headers'
Check ((Get-VrChatQueryRemainingMilliseconds 600 550) -eq 50) 'slow headers leave only the remaining body-read budget'
Check ((Get-VrChatQueryRemainingMilliseconds 600 600) -eq 0) 'deadline prohibits starting a body read after the budget expires'
Check ((Get-VrChatQueryRemainingMilliseconds 600 1200) -eq 0) 'deadline clamps over-budget time without granting another read window'
function Snapshot($processes, $listeners, $hints = @()) {
    return [pscustomobject]@{ Processes = @($processes); Listeners = @($listeners); Hints = @($hints) }
}
$game = [pscustomobject]@{ Id = 123; StartedUtcTicks = 1000L }
$game2 = [pscustomobject]@{ Id = 123; StartedUtcTicks = 2000L }
$own = [pscustomobject]@{ Port = 49301; Pid = 123 }
$other = [pscustomobject]@{ Port = 49302; Pid = 456 }
$kernel = [pscustomobject]@{ Port = 49303; Pid = 4 }
$script:snapshot = Snapshot @($game) @($own, $other, $kernel)
$candidates = @(Get-VrChatQueryCandidates $script:snapshot)
Check ($candidates.Count -eq 1 -and $candidates[0].Port -eq 49301) 'empty game log still discovers the current game-owned TCP listener; other processes and unhinted HTTP.sys are excluded'
$kernelSnapshot = Snapshot @($game) @($own, $other, $kernel) @(49303, 49302)
$candidates = @(Get-VrChatQueryCandidates $kernelSnapshot)
Check ($candidates.Count -eq 2 -and $candidates[1].Pid -eq 4) 'only a current hint plus an HTTP.sys listener enables the fallback'
Check (-not (Test-VrChatCandidateCurrent $script:snapshot (Snapshot @($game2) @($own)) $own)) 'PID reuse after a game restart invalidates a query'
Check (-not (Test-VrChatCandidateCurrent $script:snapshot (Snapshot @($game) @($other)) $own)) 'listener ownership must remain current after HTTP completes'
Check (-not (Test-VrChatCandidateCurrent $kernelSnapshot (Snapshot @($game) @($kernel)) $kernel)) 'HTTP.sys fallback still requires the current hint after HTTP'
Check (Test-VrChatCandidateCurrent $kernelSnapshot $kernelSnapshot $kernel) 'current HTTP.sys fallback remains eligible'

$hostInfo = Json '{"NAME":"VRChat-Client-test","OSC_PORT":9000,"OSC_TRANSPORT":"UDP","OSC_IP":"127.0.0.1"}'
Check (Test-VrChatHostInfo $hostInfo) 'verified VRChat HOST_INFO accepted'
$hostArray = @($hostInfo)
Check (-not (Test-VrChatHostInfo $hostArray)) 'HOST_INFO array cannot impersonate its singleton object'
Check ($null -ne (ConvertFrom-VrChatQueryJson '{"NAME":"VRChat","OSC_PORT":9000}')) 'query JSON parser accepts an object root'
foreach ($badRoot in @('[{"NAME":"VRChat","OSC_PORT":9000}]', '[{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[false]}]', '[[{"NAME":"VRChat","OSC_PORT":9000}]]', 'null', 'false', '{broken')) {
    Check ($null -eq (ConvertFrom-VrChatQueryJson $badRoot)) 'malformed or non-object query JSON fails closed before pipeline unwrapping'
}
foreach ($badHost in @(
    '{"NAME":"Other","OSC_PORT":9000}', '{"NAME":"VRChat","OSC_PORT":9001}',
    '{"NAME":"VRChat","OSC_PORT":"9000"}', '{"NAME":"VRChat","OSC_PORT":9000,"OSC_TRANSPORT":"TCP"}',
    '{"NAME":"VRChat","OSC_PORT":9000,"OSC_IP":"192.0.2.1"}', '{"NAME":"VRChat","OSC_PORT":9000,"OSC_IP":false}')) {
    Check (-not (Test-VrChatHostInfo (Json $badHost))) 'invalid HOST_INFO rejected'
}
foreach ($pair in @(@('false', $false), @('true', $true), @('0', $false), @('1', $true))) {
    $mute = ConvertFrom-VrChatMuteResponse (Json ('{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[' + $pair[0] + ']}'))
    Check ($mute -is [bool] -and $mute -eq $pair[1]) 'JSON boolean or integer 0/1 retains its exact mute meaning'
}
foreach ($badMute in @(
    '{"FULL_PATH":"/other","VALUE":[false]}', '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":false}',
    '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":["false"]}', '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[2]}',
    '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[0.5]}', '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[null]}',
    '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[]}', '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[false,true]}')) {
    Check ($null -eq (ConvertFrom-VrChatMuteResponse (Json $badMute))) 'invalid mute payload cannot be coerced into permission to capture'
}
$muteArray = @((Json '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[false]}'))
Check ($null -eq (ConvertFrom-VrChatMuteResponse $muteArray)) 'MuteSelf array cannot impersonate its singleton object'

$script:httpCalls = @()
$script:queryMute = Json '{"FULL_PATH":"/avatar/parameters/MuteSelf","VALUE":[false]}'
$script:wrongHost = $false
$script:changedGame = $false
$script:snapshotCalls = 0
function Get-VrChatDiscoverySnapshot {
    $script:snapshotCalls++
    if ($script:changedGame -and $script:snapshotCalls -gt 1) { return Snapshot @($game2) @($own) }
    return $script:snapshot
}
function Get-VrChatQueryJson([int]$port, [string]$path, [int]$timeoutMilliseconds) {
    $script:httpCalls += "$port$path"
    Check ($port -eq 49301 -and $timeoutMilliseconds -gt 0 -and $timeoutMilliseconds -le 600) 'query is limited to an owned candidate with a bounded deadline'
    if ($path -eq '/?HOST_INFO') {
        if ($script:wrongHost) { return Json '{"NAME":"Other","OSC_PORT":9000}' }
        return $hostInfo
    }
    return $script:queryMute
}
$verified = Get-InitialMuteState
Check ($verified -is [bool] -and -not $verified -and $script:httpCalls.Count -eq 2) 'empty-log startup obtains unmuted state through the owned TCP service'
$script:wrongHost = $true
$script:httpCalls = @()
Check ($null -eq (Get-InitialMuteState) -and $script:httpCalls.Count -eq 1) 'foreign HOST_INFO cannot authorize even reading its mute value'
$script:wrongHost = $false
$script:changedGame = $true
$script:snapshotCalls = 0
Check ($null -eq (Get-InitialMuteState)) 'game identity changing during a query fails closed'

# Log fallback reads synthetic fixture data only. The file selector is mocked,
# so no path under the real user profile is opened.
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('saywhat-mute-fixture-' + [Guid]::NewGuid().ToString('N'))
$fixtureFile = Join-Path $fixtureRoot 'output_log_fixture.txt'
[void][IO.Directory]::CreateDirectory($fixtureRoot)
try {
    [IO.File]::WriteAllText($fixtureFile, 'type OSCQuery on 49303')
    $script:logCreatedTicks = 2000L
    function Get-ChildItem {
        param($LiteralPath, $Filter, $ErrorAction)
        return [pscustomobject]@{ FullName = $fixtureFile; CreationTimeUtc = [DateTime]::new($script:logCreatedTicks); LastWriteTimeUtc = [DateTime]::new(3000L) }
    }
    Check (@(Get-VrChatCurrentLogHints 1000L | Select-Object -Unique).Count -eq 1) 'current synthetic log can supply an HTTP.sys hint'
    $script:logCreatedTicks = 500L
    Check (@(Get-VrChatCurrentLogHints 1000L).Count -eq 0) 'stale log touched by another process is rejected by creation time'
}
finally {
    [IO.File]::Delete($fixtureFile)
    [IO.Directory]::Delete($fixtureRoot)
}

$script:queryCount = 0
$script:retryResult = $null
$script:messages = @()
$script:startCount = 0
$script:stopCount = 0
$script:changedGame = $false
function Set-FakeMuteQueryProof {
    $script:lastMuteQueryProof = [pscustomobject]@{
        Before = $script:snapshot; Candidate = $own; VerifiedTimestamp = [Diagnostics.Stopwatch]::GetTimestamp()
    }
}
function Get-InitialMuteState {
    $script:queryCount++
    $script:lastMuteQueryProof = $null
    if ($null -ne $script:retryResult) { Set-FakeMuteQueryProof }
    return $script:retryResult
}
function Write-Status([string]$message) { $script:messages += $message }
function Start-FoxTransClient { $script:startCount++ }
function Stop-FoxTransClient { $script:stopCount++ }
$script:mockPresent = $true
$script:presenceReadCount = 0
function Update-VrChatPresence {
    $script:presenceReadCount++
    $script:vrChatPresent = $script:mockPresent
    if (-not $script:vrChatPresent) { $script:isMuted = $true; Stop-FoxTransClient }
    return $script:vrChatPresent
}
function Packet([string]$address, [string]$tags) {
    $bytes = [System.Collections.Generic.List[byte]]::new()
    Add-OscString $bytes $address
    Add-OscString $bytes $tags
    return ,$bytes.ToArray()
}
function FakeReceiver($packets, [bool]$continuous = $false) {
    $fake = [pscustomobject]@{ Packets = [Collections.Generic.Queue[byte[]]]::new(); Continuous = $continuous; ReadCount = 0 }
    if ($packets -is [byte[]] -or ($packets.Count -gt 0 -and $packets[0] -is [byte])) { $packets = ,([byte[]]$packets) }
    foreach ($packet in $packets) { $fake.Packets.Enqueue($packet) }
    $fake | Add-Member ScriptProperty Available { if ($this.Packets.Count -gt 0 -or $this.Continuous) { return 1 }; return 0 }
    $fake | Add-Member ScriptMethod Receive {
        param([ref]$remote)
        $this.ReadCount++
        if ($this.Packets.Count -gt 0) { return ,$this.Packets.Dequeue() }
        return ,(Packet '/avatar/parameters/Unrelated' ',T')
    }
    return $fake
}
$script:vrChatPresent = $true
$script:muteVerified = $false
$script:isMuted = $true
$script:reconfiguring = $false
$script:nextMuteQuery = [DateTime]::MinValue
$script:muteQueryDelaySeconds = 2
$script:lastMuteWaitLog = [DateTime]::MinValue
$script:followVrChatMute = $true
$script:lastVerifiedMuteState = $true
$receiver = $null
Update-UnverifiedMuteState
Check ($script:queryCount -eq 1 -and -not $script:muteVerified -and $script:isMuted -and $script:startCount -eq 0) 'unknown startup stays muted and schedules a retry without starting capture'
Check ($script:nextMuteQuery -gt [DateTime]::UtcNow -and $script:muteQueryDelaySeconds -eq 5) 'retry begins after two seconds and then backs off to five'
Update-UnverifiedMuteState
Check ($script:queryCount -eq 1 -and @($script:messages | Where-Object { $_ -like '*code=mute-state-unverified*' }).Count -eq 1) 'query backoff and diagnostic throttling prevent repeated work'
$script:lastMuteWaitLog = [DateTime]::UtcNow.AddSeconds(-31)
Update-UnverifiedMuteState
Check (@($script:messages | Where-Object { $_ -like '*code=mute-state-unverified*' }).Count -eq 2) 'unknown-state feedback refreshes at most every thirty seconds'
$script:retryResult = $false
$script:nextMuteQuery = [DateTime]::MinValue
$receiver = FakeReceiver @((Packet '/avatar/parameters/Unrelated' ',T'), (Packet '/avatar/parameters/MuteSelf' ',T'))
Update-UnverifiedMuteState
Check ($script:muteVerified -and $script:isMuted -and $script:startCount -eq 0) 'queued UDP mute takes priority over an in-flight unmuted HTTP result'
$script:muteVerified = $false
$receiver = FakeReceiver @() $true
$script:nextMuteQuery = [DateTime]::MinValue
Update-UnverifiedMuteState
Check (-not $script:muteVerified -and $script:isMuted -and $script:startCount -eq 0 -and $receiver.ReadCount -eq 128) 'an overwhelming OSC backlog is bounded and cannot authorize capture before pending mute packets are checked'
$finitePackets = [Collections.Generic.List[byte[]]]::new()
for ($i = 0; $i -lt 128; $i++) { $finitePackets.Add((Packet '/avatar/parameters/Unrelated' ',T')) }
$receiver = FakeReceiver ($finitePackets.ToArray())
$script:nextMuteQuery = [DateTime]::MinValue
Update-UnverifiedMuteState
Check ($script:muteVerified -and -not $script:isMuted -and $script:startCount -eq 1 -and $receiver.ReadCount -eq 128) 'ordinary unrelated OSC that drains within the cap cannot starve delayed discovery'
Check (@($script:messages | Where-Object { $_ -like '*code=mute-state-ready*' }).Count -eq 2) 'each unknown-state recovery emits one privacy-safe diagnostic'
$script:muteVerified = $false
$script:isMuted = $true
$finitePackets.Add((Packet '/avatar/parameters/MuteSelf' ',T'))
$receiver = FakeReceiver ($finitePackets.ToArray())
$script:nextMuteQuery = [DateTime]::MinValue
$beforeBacklogStart = $script:startCount
Update-UnverifiedMuteState
Check (-not $script:muteVerified -and $script:isMuted -and $script:startCount -eq $beforeBacklogStart -and $receiver.Packets.Count -eq 1) 'fresh mute at packet one hundred twenty-nine prevents older HTTP unmute from opening capture'
[void](Receive-PendingVrChatOsc)
Check ($script:muteVerified -and $script:lastVerifiedMuteState -and $script:isMuted -and $script:startCount -eq $beforeBacklogStart) 'next bounded drain applies the queued real mute without any intervening unmute'
$receiver = $null
$countAfterRecovery = $script:queryCount
Set-MuteState $true 'VRChat OSC UDP'
$script:nextMuteQuery = [DateTime]::MinValue
Update-UnverifiedMuteState
Check ($script:queryCount -eq $countAfterRecovery -and $script:isMuted) 'verified UDP mute cannot be overwritten by periodic query results'
$script:muteVerified = $false
$script:reconfiguring = $true
Update-UnverifiedMuteState
Check ($script:queryCount -eq $countAfterRecovery) 'freeze/quiesce prevents discovery from resuming capture'
$script:reconfiguring = $false
$script:vrChatPresent = $false
Update-UnverifiedMuteState
Check ($script:queryCount -eq $countAfterRecovery) 'absent game does not trigger discovery'
$script:vrChatPresent = $true
$script:retryResult = $true
Update-UnverifiedMuteState
Check ($script:muteVerified -and $script:isMuted) 'verified muted query also recovers the unknown state without opening capture'

foreach ($payload in @('{}', 'null', '{"OwnVoicePauseWhenVrchatMuted":"false"}', '{"OwnVoicePauseWhenVrchatMuted":0}', '{"OwnVoicePauseWhenVrchatMuted":null}')) {
    Check (Resolve-OwnVoicePauseWhenVrchatMuted (Json $payload)) 'missing or malformed mute policy defaults to synchronization enabled'
}
Check (-not (Resolve-OwnVoicePauseWhenVrchatMuted (Json '{"OwnVoicePauseWhenVrchatMuted":false}'))) 'explicit JSON false disables synchronization'
Check (Resolve-OwnVoicePauseWhenVrchatMuted (Json '{"OwnVoicePauseWhenVrchatMuted":true}')) 'explicit JSON true enables synchronization'
$singletonArray = @([pscustomobject]@{ OwnVoicePauseWhenVrchatMuted = $false })
Check (Resolve-OwnVoicePauseWhenVrchatMuted $singletonArray) 'singleton array cannot impersonate a valid preference object through member enumeration'
$policyFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('saywhat-mute-policy-' + [Guid]::NewGuid().ToString('N'))
$policyFixtureFile = Join-Path $policyFixtureRoot 'vrchat-translation-settings.json'
[void][IO.Directory]::CreateDirectory($policyFixtureRoot)
$DataRoot = $policyFixtureRoot
try {
    Check (Get-OwnVoicePauseWhenVrchatMuted) 'missing preference file retains the safe default'
    [IO.File]::WriteAllText($policyFixtureFile, '{"OwnVoicePauseWhenVrchatMuted":false}')
    Check (-not (Get-OwnVoicePauseWhenVrchatMuted)) 'bounded reader recognizes an explicit false preference'
    [IO.File]::WriteAllText($policyFixtureFile, '{broken')
    Check (Get-OwnVoicePauseWhenVrchatMuted) 'invalid JSON retains the safe default'
    [IO.File]::WriteAllText($policyFixtureFile, '[{"OwnVoicePauseWhenVrchatMuted":false}]')
    Check (Get-OwnVoicePauseWhenVrchatMuted) 'singleton JSON array cannot disable synchronization through pipeline unwrapping'
    [IO.File]::WriteAllText($policyFixtureFile, '[[{"OwnVoicePauseWhenVrchatMuted":false}]]')
    Check (Get-OwnVoicePauseWhenVrchatMuted) 'nested JSON arrays retain the safe default'
    [IO.File]::WriteAllText($policyFixtureFile, (' ' * 65537) + '{"OwnVoicePauseWhenVrchatMuted":false}')
    Check (Get-OwnVoicePauseWhenVrchatMuted) 'oversized preference file cannot disable the gate'
}
finally {
    [IO.File]::Delete($policyFixtureFile)
    [IO.Directory]::Delete($policyFixtureRoot)
}

# All policy/capture actions below remain in-memory fakes. Enumerate every
# combination of actual mute, verification, presence, policy and freeze state.
foreach ($follow in @($true, $false)) {
    foreach ($verified in @($true, $false)) {
        foreach ($actualMute in @($true, $false)) {
            foreach ($present in @($true, $false)) {
                foreach ($frozen in @($true, $false)) {
                    $script:followVrChatMute = $follow
                    $script:muteVerified = $verified
                    $script:lastVerifiedMuteState = $actualMute
                    $script:vrChatPresent = $present
                    $script:reconfiguring = $frozen
                    $script:isMuted = $true
                    $script:startCount = 0
                    Apply-MutePolicy
                    $mayOpen = $present -and -not $frozen -and (-not $follow -or ($verified -and -not $actualMute))
                    Check (($script:startCount -eq 1) -eq $mayOpen) 'policy matrix: only permitted present and unfrozen capture can start'
                    Check ($script:isMuted -eq (-not $mayOpen)) 'policy matrix: effective gate remains separate from actual game mute'
                }
            }
        }
    }
}
$script:policySetting = $false
$script:policyReadCount = 0
function Get-OwnVoicePauseWhenVrchatMuted { $script:policyReadCount++; return $script:policySetting }
$script:vrChatPresent = $true
$script:reconfiguring = $false
$script:muteVerified = $false
$script:lastVerifiedMuteState = $true
$script:isMuted = $true
$script:followVrChatMute = $true
$script:mutePolicyInitialized = $false
$script:lastMutePolicyCheck = [DateTime]::MinValue
$script:startCount = 0
$script:stopCount = 0
$script:messages = @()
Update-MutePolicySetting
Check (-not $script:isMuted -and -not $script:muteVerified -and $script:startCount -eq 1) 'initial explicit off permits present-game capture without claiming mute verification'
Check (@($script:messages | Where-Object { $_ -like '*code=mute-sync-disabled*' }).Count -eq 1) 'policy off emits the distinct mute-sync-disabled recovery'
Update-MutePolicySetting
Check ($script:policyReadCount -eq 1) 'independent settings reader is throttled to five hundred milliseconds'
$script:nextMuteQuery = [DateTime]::MinValue
$script:lastMuteWaitLog = [DateTime]::MinValue
$script:retryResult = $null
Update-UnverifiedMuteState
Check (-not $script:muteVerified -and @($script:messages | Where-Object { $_ -like '*code=mute-state-unverified*' }).Count -eq 0) 'off keeps discovering actual state without publishing a misleading mute-wait issue'
Set-MuteState $true 'VRChat OSC UDP'
Check ($script:muteVerified -and $script:lastVerifiedMuteState -and -not $script:isMuted -and $script:stopCount -eq 0) 'off still records a real UDP mute without pausing or finishing a captured sentence'
$script:policySetting = $true
$script:lastMutePolicyCheck = [DateTime]::MinValue
Update-MutePolicySetting
Check ($script:isMuted -and $script:stopCount -eq 1) 'turning synchronization on immediately applies the known muted state'
$script:policySetting = $false
$script:lastMutePolicyCheck = [DateTime]::MinValue
Update-MutePolicySetting
Check (-not $script:isMuted) 'turning synchronization off opens a present and verified-muted game'
Set-MuteState $false 'VRChat OSC UDP'
$script:policySetting = $true
$script:lastMutePolicyCheck = [DateTime]::MinValue
Update-MutePolicySetting
Check (-not $script:isMuted -and -not $script:lastVerifiedMuteState) 'turning synchronization on retains capture for a verified unmuted game'
$script:policySetting = $false
$script:lastMutePolicyCheck = [DateTime]::MinValue
Update-MutePolicySetting
$script:muteVerified = $false
$script:policySetting = $true
$script:lastMutePolicyCheck = [DateTime]::MinValue
Update-MutePolicySetting
Check ($script:isMuted -and $script:nextMuteQuery -eq [DateTime]::MinValue -and @($script:messages | Where-Object { $_ -like '*code=mute-state-unverified*' }).Count -eq 1) 'turning on with unknown actual state closes capture and immediately reports and retries verification'
$script:policySetting = $false
$script:reconfiguring = $true
$script:lastMutePolicyCheck = [DateTime]::MinValue
$beforeFrozenStart = $script:startCount
Update-MutePolicySetting
Check ($script:isMuted -and $script:startCount -eq $beforeFrozenStart) 'turning off during freeze does not resume capture'
$script:vrChatPresent = $true
Receive-VrChatOscPacket (Packet '/foxtrans/control/unfreeze' ',T') $null $true
Check (-not $script:reconfiguring -and -not $script:isMuted -and $script:startCount -eq $beforeFrozenStart + 1) 'unfreeze re-evaluates the current off policy without requiring a new preference timestamp'
$script:reconfiguring = $true
$script:followVrChatMute = $true
$script:isMuted = $true
Set-MuteState $true 'VRChat OSC UDP'
$beforeMutedUnfreeze = $script:startCount
Receive-VrChatOscPacket (Packet '/foxtrans/control/unfreeze' ',T') $null $true
Check ($script:isMuted -and $script:lastVerifiedMuteState -and $script:startCount -eq $beforeMutedUnfreeze) 'on policy retains a real mute observed during freeze when capture synchronization resumes'
foreach ($followOnUnfreeze in @($true, $false)) {
    $script:followVrChatMute = $followOnUnfreeze
    $script:vrChatPresent = $true
    $script:mockPresent = $false
    $script:muteVerified = $true
    $script:lastVerifiedMuteState = $false
    $script:isMuted = $false
    $script:reconfiguring = $true
    $beforeExitUnfreeze = $script:startCount
    $beforePresenceRead = $script:presenceReadCount
    Receive-VrChatOscPacket (Packet '/foxtrans/control/unfreeze' ',T') $null $true
    Check (-not $script:vrChatPresent -and $script:isMuted -and $script:startCount -eq $beforeExitUnfreeze -and $script:presenceReadCount -eq $beforePresenceRead + 1) 'unfreeze refreshes presence before either policy can reopen a game that exited during the transaction'
}
$script:mockPresent = $true
$script:reconfiguring = $false
$script:vrChatPresent = $false
$script:followVrChatMute = $false
$beforeAbsentStart = $script:startCount
Apply-MutePolicy
Check ($script:isMuted -and $script:startCount -eq $beforeAbsentStart) 'off never resumes capture while the game is absent'

# Changing policy while a query is in flight must not let its old result
# override the newest policy or a later real UDP mute.
$script:vrChatPresent = $true
$script:followVrChatMute = $false
$script:muteVerified = $false
$script:isMuted = $false
$script:policySetting = $true
$script:lastMutePolicyCheck = [DateTime]::MinValue
$script:nextMuteQuery = [DateTime]::MinValue
$receiver = FakeReceiver @((Packet '/avatar/parameters/MuteSelf' ',T'))
Check ($receiver.Packets.Count -eq 1 -and $receiver.Packets.Peek().Length -gt 4) "single mute packet fixture is preserved (count=$($receiver.Packets.Count); bytes=$($receiver.Packets.Peek().Length))"
function Get-InitialMuteState { $script:queryCount++; Update-MutePolicySetting; Set-FakeMuteQueryProof; return $false }
Update-UnverifiedMuteState
Check ($script:followVrChatMute -and $script:muteVerified -and $script:lastVerifiedMuteState -and $script:isMuted) 'on toggle plus fresh UDP mute wins over an older unmuted HTTP result'
$receiver = $null
$script:followVrChatMute = $true
$script:muteVerified = $false
$script:isMuted = $true
$script:policySetting = $false
$script:lastMutePolicyCheck = [DateTime]::MinValue
$script:nextMuteQuery = [DateTime]::MinValue
function Get-InitialMuteState { $script:queryCount++; Update-MutePolicySetting; Set-FakeMuteQueryProof; return $true }
Update-UnverifiedMuteState
Check (-not $script:followVrChatMute -and $script:muteVerified -and $script:lastVerifiedMuteState -and -not $script:isMuted) 'off toggle in flight retains actual muted state while allowing the explicitly chosen capture policy'

foreach ($backlogSize in @(129, 256)) {
    $script:vrChatPresent = $true
    $script:reconfiguring = $false
    $script:followVrChatMute = $true
    $script:muteVerified = $false
    $script:isMuted = $true
    $script:pendingMuteQuery = $null
    $script:nextMuteQuery = [DateTime]::MinValue
    $receiver = FakeReceiver @()
    $script:queryBacklogSize = $backlogSize
    function Get-InitialMuteState {
        $script:queryCount++
        Set-FakeMuteQueryProof
        # Each hypothetical HTTP retry would build another backlog. A retained
        # result must complete the following bounded drain without re-querying.
        for ($i = 0; $i -lt $script:queryBacklogSize; $i++) {
            $receiver.Packets.Enqueue((Packet '/avatar/parameters/Unrelated' ',T'))
        }
        return $false
    }
    $beforeBacklogQuery = $script:queryCount
    Update-UnverifiedMuteState
    Check (-not $script:muteVerified -and $script:isMuted -and $null -ne $script:pendingMuteQuery) 'finite backlog retains the validated query and stays closed until pending packets drain'
    Update-UnverifiedMuteState
    Check ($script:muteVerified -and -not $script:isMuted -and $script:queryCount -eq $beforeBacklogQuery + 1 -and $receiver.Packets.Count -eq 0) 'finite backlog larger than the cap drains and recovers without repeatedly fetching HTTP'
}

foreach ($invalidatingEvent in @('freeze', 'absence', 'pid-change', 'listener-change', 'expiry')) {
    $script:snapshot = Snapshot @($game) @($own, $other, $kernel)
    Set-FakeMuteQueryProof
    $script:pendingMuteQuery = [pscustomobject]@{ Muted = $false; Proof = $script:lastMuteQueryProof }
    $script:vrChatPresent = $true
    $script:reconfiguring = $false
    $script:followVrChatMute = $true
    $script:muteVerified = $false
    $script:isMuted = $true
    $script:nextMuteQuery = [DateTime]::UtcNow.AddSeconds(5)
    $receiver = $null
    if ($invalidatingEvent -eq 'freeze') { $script:reconfiguring = $true }
    if ($invalidatingEvent -eq 'absence') { $script:vrChatPresent = $false }
    if ($invalidatingEvent -eq 'pid-change') { $script:snapshot = Snapshot @($game2) @($own) }
    if ($invalidatingEvent -eq 'listener-change') { $script:snapshot = Snapshot @($game) @($other) }
    if ($invalidatingEvent -eq 'expiry') {
        $script:pendingMuteQuery.Proof.VerifiedTimestamp = [Diagnostics.Stopwatch]::GetTimestamp() - [Diagnostics.Stopwatch]::Frequency * 3
    }
    $beforeInvalidStart = $script:startCount
    $beforeInvalidQuery = $script:queryCount
    Update-UnverifiedMuteState
    Check ($null -eq $script:pendingMuteQuery -and -not $script:muteVerified -and $script:isMuted -and $script:startCount -eq $beforeInvalidStart -and $script:queryCount -eq $beforeInvalidQuery) "pending HTTP result is invalidated safely by $invalidatingEvent"
}
$script:snapshot = Snapshot @($game) @($own, $other, $kernel)
$script:vrChatPresent = $true
$script:reconfiguring = $false
$script:muteVerified = $false
$script:isMuted = $true
Set-FakeMuteQueryProof
$script:pendingMuteQuery = [pscustomobject]@{ Muted = $false; Proof = $script:lastMuteQueryProof }
$script:nextMuteQuery = [DateTime]::UtcNow.AddSeconds(5)
$receiver = FakeReceiver @((Packet '/avatar/parameters/MuteSelf' ',T'))
$beforePendingMuteStart = $script:startCount
Update-UnverifiedMuteState
Check ($null -eq $script:pendingMuteQuery -and $script:muteVerified -and $script:lastVerifiedMuteState -and $script:isMuted -and $script:startCount -eq $beforePendingMuteStart) 'fresh UDP mute clears a retained HTTP unmute before it can apply'
$script:muteVerified = $false
$script:isMuted = $true
Set-FakeMuteQueryProof
$script:pendingMuteQuery = [pscustomobject]@{ Muted = $false; Proof = $script:lastMuteQueryProof }
$receiver = FakeReceiver @()
$script:injectMuteDuringMetadata = $true
function Get-VrChatDiscoverySnapshot {
    if ($script:injectMuteDuringMetadata) {
        $script:injectMuteDuringMetadata = $false
        $receiver.Packets.Enqueue((Packet '/avatar/parameters/MuteSelf' ',T'))
    }
    return $script:snapshot
}
Update-UnverifiedMuteState
Check (-not $script:muteVerified -and $script:isMuted -and $null -ne $script:pendingMuteQuery -and $script:startCount -eq $beforePendingMuteStart) 'a mute arriving during the final identity check defers the retained HTTP result again'
Update-UnverifiedMuteState
Check ($script:muteVerified -and $script:lastVerifiedMuteState -and $script:isMuted -and $null -eq $script:pendingMuteQuery -and $script:startCount -eq $beforePendingMuteStart) 'the following bounded drain applies that newer mute before any pending unmute'
Write-Host "PASS: $script:checks isolated mute discovery, validation, ownership, backoff, freeze, and recovery checks; no live queries or audio."

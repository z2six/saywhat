param(
    [string]$FoxTransPath = $(if (Test-Path "$PSScriptRoot\FoxTransLocalHost\publish\FoxTrans.LocalHost.exe") { "$PSScriptRoot\FoxTransLocalHost\publish\FoxTrans.LocalHost.exe" } else { "$PSScriptRoot\FoxTrans.Cli.exe" }),
    [string]$ConfigPath = "$PSScriptRoot\config.fast-chinese.jsonc",
    [int]$VrChatOutputPort = 9001,
    [int]$SentenceProxyPort = 9010,
    [string]$LogPath = "C:\VRChat\foxtrans-mute-sync.log",
    [string]$ProxyLogPath = "C:\VRChat\foxtrans-osc-sentence.log",
    [int]$UnmutedIdleRestartSeconds = 0,
    [int]$HostControlPort = 9011,
    [string]$DataRoot = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
$script:foxProcess = $null
$script:isMuted = $true
$script:lastStart = [DateTime]::MinValue
$script:lastProxyUnmute = [DateTime]::MinValue
$script:lastFoxActivity = [DateTime]::UtcNow
$script:lastProxyLength = 0L
$script:configStamp = (Get-Item -LiteralPath $ConfigPath).LastWriteTimeUtc
$script:usesLocalHost = [IO.Path]::GetFileNameWithoutExtension($FoxTransPath) -eq 'FoxTrans.LocalHost'
$script:reconfiguring = $false
$script:vrChatPresent = $null
$script:lastVrChatCheck = [DateTime]::MinValue
$script:muteVerified = $false
$script:nextMuteQuery = [DateTime]::MinValue
$script:muteQueryDelaySeconds = 2
$script:lastMuteWaitLog = [DateTime]::MinValue
$script:lastVerifiedMuteState = $true
$script:followVrChatMute = $true
$script:mutePolicyInitialized = $false
$script:lastMutePolicyCheck = [DateTime]::MinValue
$script:lastMuteQueryProof = $null
$script:pendingMuteQuery = $null
$script:lastLogWriteWarning = [DateTime]::MinValue
$script:hostRestartCount = 0

function Send-LocalControl([string]$address, [bool]$value, [int]$port) {
    $buffer = [System.Collections.Generic.List[byte]]::new()
    Add-OscString $buffer $address
    Add-OscString $buffer $(if ($value) { ',T' } else { ',F' })
    $client = [Net.Sockets.UdpClient]::new()
    try { [void]$client.Send($buffer.ToArray(), $buffer.Count, [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $port)) }
    finally { $client.Dispose() }
}

function Get-FinishCapturedSetting {
    $settings = Read-OwnVoiceRuntimeSettings
    if ($null -ne $settings -and $settings.PSObject.Properties['FinishCapturedSentenceOnMute'] -and
        $settings.FinishCapturedSentenceOnMute -is [bool]) { return $settings.FinishCapturedSentenceOnMute }
    return $true
}

function Read-OwnVoiceRuntimeSettings {
    $settingsStream = $null
    try {
        $path = Join-Path $DataRoot 'own-voice-runtime.json'
        # A present but malformed projection must not revive a stale full file.
        if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path $DataRoot 'vrchat-translation-settings.json' }
        $settingsStream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $buffer = New-Object byte[] 65537
        $length = 0
        while ($length -lt $buffer.Length) {
            $count = $settingsStream.Read($buffer, $length, $buffer.Length - $length)
            if ($count -eq 0) { break }
            $length += $count
        }
        if ($length -gt 65536) { return $null }
        $json = [Text.UTF8Encoding]::new($false, $true).GetString($buffer, 0, $length).TrimStart([char]0xFEFF)
        if (-not $json.TrimStart().StartsWith('{')) { return $null }
        return ($json | ConvertFrom-Json)
    }
    catch { return $null }
    finally { if ($settingsStream) { $settingsStream.Dispose() } }
}

function Write-ControllerFailure([Exception]$exception) {
    $cause = $exception.GetBaseException()
    $code = 'controller-failed'
    if ($cause -is [DllNotFoundException]) { $code = 'native-library-missing' }
    elseif ($cause -is [BadImageFormatException] -or $cause -is [EntryPointNotFoundException]) { $code = 'native-library-invalid' }
    elseif ($cause -is [UnauthorizedAccessException]) { $code = 'access-denied' }
    elseif ($cause -is [Net.Sockets.SocketException]) { $code = 'endpoint-unavailable' }
    $line = 'SAYWHAT_STATUS component=microphone-controller event=failed code=' + $code +
        ' hresult=0x' + $cause.HResult.ToString('X8', [Globalization.CultureInfo]::InvariantCulture)
    if ($cause -is [ComponentModel.Win32Exception]) {
        $line += ' native=' + $cause.NativeErrorCode.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    Write-Status $line
}

function Write-Status([string]$message) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Write-Host "[$timestamp] $message"
    # Logging must never stop capture or a verified mute-state transition.
    # Other readers (editors, diagnostics or security software) may hold the
    # log temporarily. Share normal reads/writes and tolerate only file I/O or
    # access failures; programming errors must still reach the controller.
    try {
        $logStream = [IO.FileStream]::new($LogPath, [IO.FileMode]::Append, [IO.FileAccess]::Write,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes("[$timestamp] $message" + [Environment]::NewLine)
            $logStream.Write($bytes, 0, $bytes.Length)
            $logStream.Flush()
        }
        finally { $logStream.Dispose() }
    }
    catch [IO.IOException], [UnauthorizedAccessException] {
        $now = [DateTime]::UtcNow
        if ($now - $script:lastLogWriteWarning -ge [TimeSpan]::FromSeconds(30)) {
            $script:lastLogWriteWarning = $now
            # The desktop app separately retains stdout. Never recurse into
            # the same unavailable file while reporting its failure.
            Write-Host "[$timestamp] WARNING own-voice: code=log-write-unavailable; The local log could not be updated. Voice translation continues."
        }
    }
}

function Add-OscString([System.Collections.Generic.List[byte]]$buffer, [string]$value) {
    $buffer.AddRange([Text.Encoding]::UTF8.GetBytes($value))
    $buffer.Add(0)
    while (($buffer.Count % 4) -ne 0) { $buffer.Add(0) }
}

function Send-ProxyReset {
    $buffer = [System.Collections.Generic.List[byte]]::new()
    Add-OscString $buffer "/foxtrans/control/reset"
    Add-OscString $buffer ",T"
    $client = [Net.Sockets.UdpClient]::new()
    try {
        [void]$client.Send(
            $buffer.ToArray(),
            $buffer.Count,
            [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $SentenceProxyPort))
    }
    finally { $client.Dispose() }
}

function Send-ProxyMuted([bool]$muted) {
    $buffer = [System.Collections.Generic.List[byte]]::new()
    Add-OscString $buffer "/foxtrans/control/muted"
    Add-OscString $buffer $(if ($muted) { ",T" } else { ",F" })
    $client = [Net.Sockets.UdpClient]::new()
    try {
        [void]$client.Send($buffer.ToArray(), $buffer.Count,
            [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $SentenceProxyPort))
    }
    finally { $client.Dispose() }
    if (-not $muted) { $script:lastProxyUnmute = [DateTime]::UtcNow }
}

function Start-FoxTransClient {
    if ($script:reconfiguring) { return }
    if ($script:isMuted -and -not $script:usesLocalHost) { return }
    if ($script:foxProcess -and -not $script:foxProcess.HasExited) {
        # Reassert actual state if the proxy was restarted while the microphone client stayed alive.
        if ([DateTime]::UtcNow - $script:lastProxyUnmute -ge [TimeSpan]::FromSeconds(1)) {
            Send-ProxyMuted $script:isMuted
            if ($script:usesLocalHost) { Send-LocalControl '/foxtrans/control/muted' $script:isMuted $HostControlPort }
            $script:lastProxyUnmute = [DateTime]::UtcNow
        }
        return
    }
    if ($script:foxProcess -and $script:foxProcess.HasExited) {
        if ($script:hostRestartCount -lt [int]::MaxValue) { $script:hostRestartCount++ }
        Write-Status ('SAYWHAT_STATUS component=microphone-controller event=host-exited restart=' +
            $script:hostRestartCount.ToString([Globalization.CultureInfo]::InvariantCulture) + ' exit=' +
            $script:foxProcess.ExitCode.ToString([Globalization.CultureInfo]::InvariantCulture))
        Write-Status "FoxTrans exited with code $($script:foxProcess.ExitCode); reconnecting the provider."
        $script:foxProcess = $null
    }
    if ((Get-Date) - $script:lastStart -lt [TimeSpan]::FromSeconds(1)) { return }
    $script:lastStart = Get-Date
    Send-ProxyMuted $script:isMuted
    $arguments = @("run", "--config", ('"' + $ConfigPath + '"'))
    if ($script:usesLocalHost) { $arguments += @('--root', ('"' + $DataRoot + '"'), '--control-port', $HostControlPort) }
    $script:foxProcess = Start-Process -FilePath $FoxTransPath -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardError (Join-Path $DataRoot 'foxtrans-provider-errors.log')
    Write-Status ('SAYWHAT_STATUS component=microphone-controller event=host-started restart=' +
        $script:hostRestartCount.ToString([Globalization.CultureInfo]::InvariantCulture))
    $script:lastFoxActivity = [DateTime]::UtcNow
    $proxyInfo = Get-Item -LiteralPath $ProxyLogPath -ErrorAction SilentlyContinue
    $script:lastProxyLength = if ($proxyInfo) { $proxyInfo.Length } else { 0L }
    if ($script:usesLocalHost) {
        Send-LocalControl '/foxtrans/control/muted' $script:isMuted $HostControlPort
        Write-Status "Capture-gate host started (PID $($script:foxProcess.Id)); muted=$($script:isMuted). Actual PCM silence controls completion."
    } else { Write-Status "VRChat mic is UNMUTED: FoxTrans started (PID $($script:foxProcess.Id))." }
}

function Test-FoxTransWatchdog {
    if ($script:reconfiguring) { return }
    # Quiet microphones do not indicate a broken stream. The server now recovers decoder EOS
    # in place; process failures still reconnect through Start-FoxTransClient.
    if ($UnmutedIdleRestartSeconds -le 0) { return }
    if ($script:isMuted -or -not $script:foxProcess -or $script:foxProcess.HasExited) { return }

    $proxyInfo = Get-Item -LiteralPath $ProxyLogPath -ErrorAction SilentlyContinue
    if ($proxyInfo -and $proxyInfo.Length -ne $script:lastProxyLength) {
        $script:lastProxyLength = $proxyInfo.Length
        $script:lastFoxActivity = [DateTime]::UtcNow
        return
    }

    if ([DateTime]::UtcNow - $script:lastFoxActivity -lt [TimeSpan]::FromSeconds($UnmutedIdleRestartSeconds)) {
        return
    }

    $oldPid = $script:foxProcess.Id
    Stop-Process -Id $oldPid -Force -ErrorAction SilentlyContinue
    try { $script:foxProcess.WaitForExit(2000) | Out-Null } catch {}
    $script:foxProcess = $null
    Send-ProxyReset
    Write-Status "FoxTrans watchdog recycled a silent realtime session (old PID $oldPid)."
    Start-Sleep -Milliseconds 250
    Start-FoxTransClient
}

function Stop-FoxTransClient([string]$reason = 'VRChat mic is MUTED') {
    # Block the proxy before process shutdown can wait or a late provider result can arrive.
    Send-ProxyMuted $true
    if ($script:usesLocalHost -and $reason -eq 'VRChat mic is MUTED' -and $script:foxProcess -and -not $script:foxProcess.HasExited) {
        Send-LocalControl '/foxtrans/control/drain' (Get-FinishCapturedSetting) $SentenceProxyPort
        Send-LocalControl '/foxtrans/control/muted' $true $HostControlPort
        Write-Status 'MUTED finishing captured tail; microphone capture pauses immediately. Host logs MUTED done after true ASR final and translation finish.'
        return
    }
    Send-ProxyReset
    if ($script:foxProcess -and -not $script:foxProcess.HasExited) {
        Stop-Process -Id $script:foxProcess.Id -Force -ErrorAction SilentlyContinue
        try { $script:foxProcess.WaitForExit(2000) | Out-Null } catch {}
        Write-Status "${reason}: FoxTrans stopped; microphone and translation are idle."
    }
    $script:foxProcess = $null
    Send-ProxyMuted $true
    Send-ProxyReset
}

function Resolve-OwnVoicePauseWhenVrchatMuted($settings) {
    # Missing, malformed and older preferences always retain the safe default.
    if ($settings -is [System.Management.Automation.PSCustomObject] -and $settings.PSObject.Properties['OwnVoicePauseWhenVrchatMuted'] -and
        $settings.OwnVoicePauseWhenVrchatMuted -is [bool]) {
        return $settings.OwnVoicePauseWhenVrchatMuted
    }
    return $true
}

function Get-OwnVoicePauseWhenVrchatMuted {
    return Resolve-OwnVoicePauseWhenVrchatMuted (Read-OwnVoiceRuntimeSettings)
}

function Update-MutePolicySetting {
    if ([DateTime]::UtcNow - $script:lastMutePolicyCheck -lt [TimeSpan]::FromMilliseconds(500)) { return }
    $script:lastMutePolicyCheck = [DateTime]::UtcNow
    # Independent of the generated capture config timestamp: changing only this
    # preference must take effect without restarting helpers or loaded models.
    $follow = Get-OwnVoicePauseWhenVrchatMuted
    $changed = -not $script:mutePolicyInitialized -or $script:followVrChatMute -ne $follow
    $script:mutePolicyInitialized = $true
    $script:followVrChatMute = $follow
    if ($changed -and $follow -and -not $script:muteVerified) {
            $script:nextMuteQuery = [DateTime]::MinValue
            $script:muteQueryDelaySeconds = 2
            $script:lastMuteWaitLog = [DateTime]::MinValue
    }
    Apply-MutePolicy
    if ($changed -and -not $follow) {
        Write-Status 'OWN VOICE recovered; code=mute-sync-disabled; VRChat microphone muting is ignored while the game is running.'
    }
    elseif ($changed -and -not $script:muteVerified -and $script:vrChatPresent -and -not $script:reconfiguring) {
        Write-Status 'ERROR own-voice: code=mute-state-unverified; microphone is closed while verified VRChat mute state is unavailable.'
        $script:lastMuteWaitLog = [DateTime]::UtcNow
    }
}

function Apply-MutePolicy {
    # A frozen settings transaction owns capture until its quiesce/resume
    # handshake completes. A policy change must never start it in the meantime.
    if ($script:reconfiguring) { return }
    $muted = -not $script:vrChatPresent -or
        ($script:followVrChatMute -and (-not $script:muteVerified -or $script:lastVerifiedMuteState))
    if ($script:isMuted -eq $muted) {
        if (-not $muted) { Start-FoxTransClient }
        return
    }
    $script:isMuted = $muted
    if ($muted) { Stop-FoxTransClient } else { Start-FoxTransClient }
}

function Set-MuteState([bool]$muted, [string]$source) {
    # Record the actual game state even while synchronization is disabled.
    # This lets re-enabling it immediately restore the correct capture gate.
    $script:pendingMuteQuery = $null
    if (-not $script:muteVerified -and $script:followVrChatMute) {
        Write-Status 'OWN VOICE recovered; code=mute-state-ready; verified VRChat mute state is available.'
    }
    $changed = -not $script:muteVerified -or $script:lastVerifiedMuteState -ne $muted
    $script:lastVerifiedMuteState = $muted
    $script:muteVerified = $true
    if ($changed) { Write-Status "MuteSelf=$muted received from $source." }
    Apply-MutePolicy
}

function Read-OscString([byte[]]$packet, [ref]$offset) {
    if ($offset.Value -lt 0 -or $offset.Value -ge $packet.Length) { return $null }
    $end = $offset.Value
    while ($end -lt $packet.Length -and $packet[$end] -ne 0) { $end++ }
    if ($end -ge $packet.Length) { return $null }
    $value = [Text.Encoding]::UTF8.GetString($packet, $offset.Value, $end - $offset.Value)
    $offset.Value = ($end + 4) -band (-bnot 3)
    return $value
}

function Read-MuteSelf([byte[]]$packet) {
    $offset = 0
    $address = Read-OscString $packet ([ref]$offset)
    $tags = Read-OscString $packet ([ref]$offset)
    if ($address -ne "/avatar/parameters/MuteSelf" -or [string]::IsNullOrEmpty($tags)) { return $null }
    if ($tags.Contains("T")) { return $true }
    if ($tags.Contains("F")) { return $false }
    return $null
}

function Receive-VrChatOscPacket([byte[]]$packet, $remote, [bool]$vrChatRunning) {
    $controlOffset = 0
    $controlAddress = Read-OscString $packet ([ref]$controlOffset)
    $controlTags = Read-OscString $packet ([ref]$controlOffset)
    if ($controlAddress -eq '/foxtrans/control/freeze' -and $controlTags -eq ',s') {
        $nonce = Read-OscString $packet ([ref]$controlOffset)
        $parsedNonce = [Guid]::Empty
        if ([Guid]::TryParse($nonce, [ref]$parsedNonce)) {
            $script:reconfiguring = $true
            $script:pendingMuteQuery = $null
            $hasHost = $script:usesLocalHost -and $script:foxProcess -and -not $script:foxProcess.HasExited
            $reply = [System.Collections.Generic.List[byte]]::new()
            Add-OscString $reply '/foxtrans/control/frozen'
            Add-OscString $reply $(if ($hasHost) { ',sT' } else { ',sF' })
            Add-OscString $reply $nonce
            [void]$receiver.Send($reply.ToArray(), $reply.Count, $remote)
            Write-Status 'Settings switch: new capture starts are paused while the captured sentence finishes.'
        }
        return
    }
    if ($controlAddress -eq '/foxtrans/control/unfreeze' -and $controlTags -eq ',T') {
        $script:reconfiguring = $false
        Write-Status 'Settings switch canceled; microphone mute synchronization resumed.'
        # Presence checks were paused during the settings transaction. Do not
        # reopen capture from its cached game state if the game exited meanwhile.
        $script:lastVrChatCheck = [DateTime]::MinValue
        [void](Update-VrChatPresence)
        Apply-MutePolicy
        return
    }
    $mute = Read-MuteSelf $packet
    if ($vrChatRunning -and $null -ne $mute) { Set-MuteState ([bool]$mute) 'VRChat OSC UDP' }
}

function Receive-PendingVrChatOsc {
    # Bound work even if unrelated avatar OSC arrives continuously. Valid mute
    # and freeze packets retain the same handler/acknowledgment as the main loop.
    for ($i = 0; $receiver -and $i -lt 128 -and $receiver.Available -gt 0; $i++) {
        $pendingRemote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        $pending = $receiver.Receive([ref]$pendingRemote)
        Receive-VrChatOscPacket $pending $pendingRemote ([bool]$script:vrChatPresent)
    }
    return -not $receiver -or $receiver.Available -eq 0
}

function Get-VrChatTcpListeners {
    # Read only the IPv4 listening table. Never probe arbitrary local ports.
    # This helper is compatible with Windows PowerShell 5.1 / .NET Framework.
    if (-not ('SayWhatMuteSyncTcpV1' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class SayWhatMuteSyncTcpV1 {
    public sealed class Listener { public int Port; public int Pid; }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr p, ref uint size, bool order, uint family, int kind, uint reserved);
    public static Listener[] Read() {
        uint size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0) != 122) return new Listener[0];
        for (int attempt = 0; attempt < 3; attempt++) {
            if (size < 4 || size > 4 * 1024 * 1024) return new Listener[0];
            uint capacity = size;
            IntPtr data = Marshal.AllocHGlobal((int)capacity);
            try {
                uint result = GetExtendedTcpTable(data, ref size, false, 2, 3, 0);
                if (result == 122) continue;
                if (result != 0) return new Listener[0];
                int count = Marshal.ReadInt32(data);
                if (count < 0 || count > (capacity - 4) / 24) return new Listener[0];
                var ports = new List<Listener>();
                for (int i = 0; i < count; i++) {
                    int offset = 4 + i * 24;
                    uint address = unchecked((uint)Marshal.ReadInt32(data, offset + 4));
                    // Include wildcard listeners, reachable on loopback, as well as 127/8.
                    if (address != 0 && (address & 255) != 127) continue;
                    uint encoded = unchecked((uint)Marshal.ReadInt32(data, offset + 8));
                    int port = (int)(((encoded & 255) << 8) | ((encoded >> 8) & 255));
                    int pid = Marshal.ReadInt32(data, offset + 20);
                    if (port > 0 && pid > 0) ports.Add(new Listener { Port = port, Pid = pid });
                }
                return ports.ToArray();
            } finally { Marshal.FreeHGlobal(data); }
        }
        return new Listener[0];
    }
}
'@
    }
    return [SayWhatMuteSyncTcpV1]::Read()
}

function Get-VrChatCurrentLogHints([long]$startedUtcTicks) {
    $logDirectory = Join-Path $env:USERPROFILE 'AppData\LocalLow\VRChat\VRChat'
    # HTTP.sys can own a game's HTTP listener. Only that case uses log hints,
    # and only a log created and written during the current game process.
    $latest = Get-ChildItem -LiteralPath $logDirectory -Filter 'output_log*.txt' -ErrorAction SilentlyContinue |
        Where-Object { $_.CreationTimeUtc.Ticks -ge $startedUtcTicks -and $_.LastWriteTimeUtc.Ticks -ge $startedUtcTicks } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $latest) { return @() }
    $logStream = $null
    try {
        $logStream = [IO.FileStream]::new($latest.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $head = New-Object byte[] ([int][Math]::Min($logStream.Length, 524288))
        [void]$logStream.Read($head, 0, $head.Length)
        $tail = New-Object byte[] ([int][Math]::Min($logStream.Length, 65536))
        [void]$logStream.Seek(-$tail.Length, [IO.SeekOrigin]::End)
        [void]$logStream.Read($tail, 0, $tail.Length)
        $text = [Text.Encoding]::UTF8.GetString($head) + "`n" + [Text.Encoding]::UTF8.GetString($tail)
        $matches = [regex]::Matches($text, '\btype OSCQuery on ([0-9]{1,5})\b')
        for ($i = $matches.Count - 1; $i -ge 0; $i--) {
            $port = [int]$matches[$i].Groups[1].Value
            if ($port -gt 0 -and $port -le 65535) { $port }
        }
    }
    catch { return @() }
    finally { if ($logStream) { $logStream.Dispose() } }
}

function Get-VrChatDiscoverySnapshot {
    $games = @(Get-Process -Name 'VRChat' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if (-not $_.HasExited) { [pscustomobject]@{ Id = $_.Id; StartedUtcTicks = $_.StartTime.ToUniversalTime().Ticks } }
        } catch { }
    })
    $listeners = @()
    $hints = @()
    if ($games.Count -gt 0) {
        $listeners = @(Get-VrChatTcpListeners)
        if (@($listeners | Where-Object { $_.Pid -eq 4 }).Count -gt 0) {
            $started = ($games | Sort-Object StartedUtcTicks -Descending | Select-Object -First 1).StartedUtcTicks
            $hints = @(Get-VrChatCurrentLogHints ([long]$started) | Select-Object -Unique -First 4)
        }
    }
    return [pscustomobject]@{ Processes = $games; Listeners = $listeners; Hints = $hints }
}

function Get-VrChatQueryCandidates($snapshot) {
    $ids = @($snapshot.Processes | ForEach-Object { $_.Id })
    $owned = @($snapshot.Listeners | Where-Object { $ids -contains $_.Pid } | Sort-Object Port)
    $kernel = @($snapshot.Listeners | Where-Object { $_.Pid -eq 4 -and $snapshot.Hints -contains $_.Port })
    return @($owned + $kernel | Select-Object -First 4)
}

function Test-VrChatHostInfo($info) {
    if ($info -isnot [System.Management.Automation.PSCustomObject] -or $info.NAME -isnot [string] -or -not $info.NAME.StartsWith('VRChat', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if (($info.OSC_PORT -isnot [int] -and $info.OSC_PORT -isnot [long]) -or $info.OSC_PORT -ne 9000) { return $false }
    if ($null -ne $info.OSC_TRANSPORT -and ($info.OSC_TRANSPORT -isnot [string] -or $info.OSC_TRANSPORT -ine 'UDP')) { return $false }
    if ($null -ne $info.OSC_IP) {
        $address = $null
        if ($info.OSC_IP -isnot [string] -or -not [Net.IPAddress]::TryParse($info.OSC_IP, [ref]$address) -or
            (-not [Net.IPAddress]::IsLoopback($address) -and -not $address.Equals([Net.IPAddress]::Any))) { return $false }
    }
    return $true
}

function ConvertFrom-VrChatMuteResponse($state) {
    if ($state -isnot [System.Management.Automation.PSCustomObject] -or $state.FULL_PATH -isnot [string] -or $state.FULL_PATH -cne '/avatar/parameters/MuteSelf' -or
        $state.VALUE -isnot [array] -or $state.VALUE.Count -ne 1) { return $null }
    $value = $state.VALUE[0]
    if ($value -is [bool]) { return $value }
    if (($value -is [int] -or $value -is [long]) -and ($value -eq 0 -or $value -eq 1)) { return $value -eq 1 }
    return $null
}

function Get-VrChatQueryRemainingMilliseconds([int]$budgetMilliseconds, [long]$elapsedMilliseconds) {
    return [int][Math]::Max(0L, [long]$budgetMilliseconds - $elapsedMilliseconds)
}

function ConvertFrom-VrChatQueryJson([string]$json) {
    # A root array must not unwrap into a seemingly valid HOST_INFO/MuteSelf.
    if ([string]::IsNullOrWhiteSpace($json) -or -not $json.TrimStart([char]0xFEFF).TrimStart().StartsWith('{')) { return $null }
    try {
        $parsed = $json | ConvertFrom-Json
        if ($parsed -is [System.Management.Automation.PSCustomObject]) { return $parsed }
    } catch { }
    return $null
}

function Get-VrChatQueryJson([int]$port, [string]$path, [int]$timeoutMilliseconds) {
    $response = $null
    $stream = $null
    $output = [IO.MemoryStream]::new()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $request = [Net.HttpWebRequest]::Create("http://127.0.0.1:$port$path")
        $request.Proxy = $null
        $request.AllowAutoRedirect = $false
        $request.Timeout = $timeoutMilliseconds
        $request.ReadWriteTimeout = $timeoutMilliseconds
        $response = $request.GetResponse()
        if ([int]$response.StatusCode -ne 200 -or $response.ContentLength -gt 16384) { return $null }
        $stream = $response.GetResponseStream()
        $buffer = New-Object byte[] 2048
        while ($true) {
            $remaining = Get-VrChatQueryRemainingMilliseconds $timeoutMilliseconds $timer.ElapsedMilliseconds
            if ($remaining -le 0) { return $null }
            if ($stream.CanTimeout) { $stream.ReadTimeout = $remaining }
            $count = $stream.Read($buffer, 0, $buffer.Length)
            if ($count -eq 0) { break }
            if ($output.Length + $count -gt 16384 -or $timer.ElapsedMilliseconds -ge $timeoutMilliseconds) { return $null }
            $output.Write($buffer, 0, $count)
        }
        if ($timer.ElapsedMilliseconds -ge $timeoutMilliseconds) { return $null }
        return ConvertFrom-VrChatQueryJson ([Text.Encoding]::UTF8.GetString($output.ToArray()))
    }
    catch { return $null }
    finally {
        if ($stream) { $stream.Dispose() }
        if ($response) { $response.Dispose() }
        $output.Dispose()
    }
}

function Test-VrChatCandidateCurrent($before, $after, $candidate) {
    $sameGames = @($before.Processes | Where-Object {
        $game = $_
        @($after.Processes | Where-Object { $_.Id -eq $game.Id -and $_.StartedUtcTicks -eq $game.StartedUtcTicks }).Count -gt 0
    })
    if ($sameGames.Count -eq 0) { return $false }
    if ($candidate.Pid -eq 4) {
        if ($after.Hints -notcontains $candidate.Port) { return $false }
    }
    elseif (@($sameGames | Where-Object { $_.Id -eq $candidate.Pid }).Count -eq 0) { return $false }
    return @($after.Listeners | Where-Object { $_.Pid -eq $candidate.Pid -and $_.Port -eq $candidate.Port }).Count -gt 0
}

function Get-InitialMuteState {
    $script:lastMuteQueryProof = $null
    try {
        $snapshot = Get-VrChatDiscoverySnapshot
        $timer = [Diagnostics.Stopwatch]::StartNew()
        foreach ($candidate in @(Get-VrChatQueryCandidates $snapshot)) {
            $remaining = 1800 - [int]$timer.ElapsedMilliseconds
            if ($remaining -le 0) { break }
            $info = Get-VrChatQueryJson $candidate.Port '/?HOST_INFO' ([Math]::Min(600, $remaining))
            if (-not (Test-VrChatHostInfo $info)) { continue }
            $remaining = 1800 - [int]$timer.ElapsedMilliseconds
            if ($remaining -le 0) { break }
            $state = Get-VrChatQueryJson $candidate.Port '/avatar/parameters/MuteSelf' ([Math]::Min(600, $remaining))
            $muted = ConvertFrom-VrChatMuteResponse $state
            if ($null -eq $muted) { continue }
            if (Test-VrChatCandidateCurrent $snapshot (Get-VrChatDiscoverySnapshot) $candidate) {
                # Keep the public Boolean/null API. Only in-memory metadata is
                # retained; this read-only query never sends capture controls.
                $script:lastMuteQueryProof = [pscustomobject]@{
                    Before = $snapshot; Candidate = $candidate; VerifiedTimestamp = [Diagnostics.Stopwatch]::GetTimestamp()
                }
                return $muted
            }
        }
    } catch { }
    return $null
}

function Update-PendingMuteQuery {
    $pending = $script:pendingMuteQuery
    if (-not $pending) { return }
    $expired = [Diagnostics.Stopwatch]::GetTimestamp() - $pending.Proof.VerifiedTimestamp -ge [Diagnostics.Stopwatch]::Frequency * 2
    if ($script:reconfiguring -or -not $script:vrChatPresent -or $script:muteVerified -or $expired) {
        $script:pendingMuteQuery = $null
        return
    }
    $drained = $true
    if ($receiver) { $drained = Receive-PendingVrChatOsc }
    if ($script:reconfiguring -or -not $script:vrChatPresent -or $script:muteVerified) {
        $script:pendingMuteQuery = $null
        return
    }
    if (-not $drained) { return }
    $expired = [Diagnostics.Stopwatch]::GetTimestamp() - $pending.Proof.VerifiedTimestamp -ge [Diagnostics.Stopwatch]::Frequency * 2
    if ($expired) { $script:pendingMuteQuery = $null; return }
    $fresh = Get-VrChatDiscoverySnapshot
    $expired = [Diagnostics.Stopwatch]::GetTimestamp() - $pending.Proof.VerifiedTimestamp -ge [Diagnostics.Stopwatch]::Frequency * 2
    if ($expired -or -not (Test-VrChatCandidateCurrent $pending.Proof.Before $fresh $pending.Proof.Candidate)) {
        $script:pendingMuteQuery = $null
        return
    }
    # Endpoint metadata can take time to read. A newly queued mute still has
    # priority; retain this short-lived result until the next bounded drain.
    if ($receiver -and $receiver.Available -gt 0) { return }
    $script:pendingMuteQuery = $null
    Set-MuteState $pending.Muted 'verified VRChat OSCQuery state'
}

function Update-UnverifiedMuteState {
    if ($script:reconfiguring -or -not $script:vrChatPresent -or $script:muteVerified) {
        $script:pendingMuteQuery = $null
        return
    }
    if ($script:pendingMuteQuery) {
        Update-PendingMuteQuery
    }
    elseif ([DateTime]::UtcNow -ge $script:nextMuteQuery) {
        $verified = Get-InitialMuteState
        if ($null -ne $verified -and $script:lastMuteQueryProof) {
            $script:pendingMuteQuery = [pscustomobject]@{ Muted = $verified; Proof = $script:lastMuteQueryProof }
            Update-PendingMuteQuery
        }
        elseif ($receiver) { [void](Receive-PendingVrChatOsc) }
        # UDP state or a freeze received while HTTP was in flight wins. Merely
        # receiving unrelated avatar traffic must not prevent discovery.
        if ($script:muteVerified -or $script:reconfiguring) { return }
        $script:nextMuteQuery = [DateTime]::UtcNow.AddSeconds($script:muteQueryDelaySeconds)
        $script:muteQueryDelaySeconds = 5
    }
    if ($script:muteVerified -or $script:reconfiguring) { return }
    if ($script:followVrChatMute -and [DateTime]::UtcNow - $script:lastMuteWaitLog -ge [TimeSpan]::FromSeconds(30)) {
        Write-Status 'ERROR own-voice: code=mute-state-unverified; microphone is closed while verified VRChat mute state is unavailable.'
        $script:lastMuteWaitLog = [DateTime]::UtcNow
    }
}

function Update-VrChatPresence {
    if ([DateTime]::UtcNow - $script:lastVrChatCheck -lt [TimeSpan]::FromMilliseconds(500)) { return [bool]$script:vrChatPresent }
    $script:lastVrChatCheck = [DateTime]::UtcNow
    $present = @(Get-Process -Name 'VRChat' -ErrorAction SilentlyContinue).Count -gt 0
    $changed = $null -ne $script:vrChatPresent -and $present -ne [bool]$script:vrChatPresent
    $first = $null -eq $script:vrChatPresent
    $script:vrChatPresent = $present
    if (-not $present -and ($changed -or $first)) {
        $script:pendingMuteQuery = $null
        $script:muteVerified = $false
        $script:nextMuteQuery = [DateTime]::MinValue
        $script:muteQueryDelaySeconds = 2
        $script:lastMuteWaitLog = [DateTime]::MinValue
        $script:isMuted = $true
        Stop-FoxTransClient 'VRChat exited or is not running'
        Write-Status 'VRChat is absent: microphone host stopped; waiting safely without capture.'
    }
    elseif ($present -and $changed) {
        $script:pendingMuteQuery = $null
        $script:muteVerified = $false
        $script:nextMuteQuery = [DateTime]::MinValue
        $script:muteQueryDelaySeconds = 2
        $script:lastMuteWaitLog = [DateTime]::MinValue
        $script:isMuted = $true
        Send-ProxyMuted $true
        Send-ProxyReset
        # Discard stale packets from the previous process before trusting its return.
        if ($receiver) {
            $staleSender = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
            while ($receiver.Available -gt 0) { [void]$receiver.Receive([ref]$staleSender) }
        }
        $verified = Get-InitialMuteState
        $drained = $true
        if ($receiver) { $drained = Receive-PendingVrChatOsc }
        if ($script:muteVerified -or $script:reconfiguring) { return $present }
        if ($null -ne $verified -and $drained) { Set-MuteState $verified 'VRChat return OSCQuery state' }
        else {
            if ($null -ne $verified -and $script:lastMuteQueryProof) {
                $script:pendingMuteQuery = [pscustomobject]@{ Muted = $verified; Proof = $script:lastMuteQueryProof }
            }
            $script:nextMuteQuery = [DateTime]::UtcNow.AddSeconds(2)
            $script:muteQueryDelaySeconds = 5
            Write-Status 'VRChat returned: mute is unverified; staying muted while discovery retries or fresh MuteSelf arrives.'
        }
    }
    return $present
}

$receiver = $null
try {
    if (-not (Test-Path -LiteralPath $FoxTransPath)) { throw "Missing FoxTrans CLI: $FoxTransPath" }
    if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "Missing FoxTrans config: $ConfigPath" }
    $receiver = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $VrChatOutputPort))
    $receiver.Client.ReceiveTimeout = 500
    Send-ProxyMuted $true
    Write-Status "VRChat mute sync is listening on 127.0.0.1:$VrChatOutputPort."
    Write-Host "Muted = microphone closed; finish only already-captured speech if enabled. Unmuted = microphone running."
    Write-Host "Keep this window open. Press Ctrl+C to stop."

    [void](Update-VrChatPresence)
    Update-MutePolicySetting
    Update-UnverifiedMuteState
    if ($script:followVrChatMute -and -not $script:muteVerified) {
        Write-Status "Initial mute state is unknown; staying safely muted while discovery retries or fresh MuteSelf arrives."
        Stop-FoxTransClient
    }

    $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
    while ($true) {
        $vrChatRunning = if ($script:reconfiguring) { [bool]$script:vrChatPresent } else { Update-VrChatPresence }
        Update-MutePolicySetting
        Apply-MutePolicy
        try {
            $packet = $receiver.Receive([ref]$remote)
            Receive-VrChatOscPacket $packet $remote $vrChatRunning
        }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -ne [Net.Sockets.SocketError]::TimedOut) { throw }
        }
        Update-UnverifiedMuteState
        $configInfo = Get-Item -LiteralPath $ConfigPath
        if ($configInfo.LastWriteTimeUtc -ne $script:configStamp) {
            $script:configStamp = $configInfo.LastWriteTimeUtc
            if ($script:usesLocalHost) {
                if (-not $script:reconfiguring) { Send-LocalControl '/foxtrans/control/reload' $true $HostControlPort }
                Write-Status 'Saved options detected; live sentence timing is refreshed without interrupting capture. Other choices are applied by the app.'
            }
            else {
                Stop-FoxTransClient 'Translation options changed'
                Write-Status 'Reloading microphone target language and sentence timing from options.'
            }
        }
        if ($script:vrChatPresent -and (-not $script:isMuted -or $script:usesLocalHost)) {
            Start-FoxTransClient
            Test-FoxTransWatchdog
        }
    }
}
catch {
    Write-ControllerFailure $_.Exception
    Write-Status "FATAL: $($_.Exception.Message)"
    Write-Host $_.Exception.ToString() -ForegroundColor Red
    exit 1
}
finally {
    # A coordinated settings switch has already stopped capture and awaited its
    # real final. Keep the independent output proxy's queued final alive; the
    # replacement controller will reassert the actual VRChat mute state.
    if ($receiver -and -not $script:reconfiguring) { Send-ProxyMuted $true }
    if ($script:foxProcess -and -not $script:foxProcess.HasExited) {
        Stop-Process -Id $script:foxProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($receiver) {
        if (-not $script:reconfiguring) { Send-ProxyReset }
        $receiver.Dispose()
    }
}

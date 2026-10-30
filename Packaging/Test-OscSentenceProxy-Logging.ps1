<#
.SYNOPSIS
Exercise the actual OSC proxy with unavailable logs and synthetic loopback chat.
.DESCRIPTION
Starts only the real proxy in hidden Windows PowerShell 5.1, using temporary data
and ephemeral UDP ports. The mock VRChat receiver verifies UTF-8, message text,
and OSC submission flags. No model, audio device, game or fixed OSC port is used.
Add-Type in the child compiles the proxy; this is a hosted Windows regression,
not a source-only check.
#>
[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$TestRoot = [IO.Path]::GetTempPath()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:checks = 0
function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw ('OSC proxy logging regression: ' + $Description) }
    $script:checks++
}

$proxyScript = Join-Path ([IO.Path]::GetFullPath($SourceRoot)) 'FoxTrans-OscSentenceProxy.ps1'
$windowsPowerShell = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
Check (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf) 'Windows PowerShell 5.1 is available'
Check (Test-Path -LiteralPath $proxyScript -PathType Leaf) 'the actual proxy script is available'
$fixtureBase = [IO.Path]::GetFullPath($TestRoot)
$fixturePrefix = $fixtureBase.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $fixtureBase ('saywhat-osc-log-' + [Guid]::NewGuid().ToString('N'))))
Check ($fixtureRoot.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) 'temporary fixture storage is inside the supplied test directory'
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$logPath = Join-Path $fixtureRoot 'proxy.log'
$isolatedData = Join-Path $fixtureRoot 'app-data'
[void][IO.Directory]::CreateDirectory($isolatedData)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$proxy = $null
$invalidProxy = $null
$logLock = $null
$receiver = $null
$sender = $null
$stdoutTask = $null
$stderrTask = $null

function Add-OscString([IO.Stream]$Stream, [string]$Value) {
    $bytes = $utf8.GetBytes($Value)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.WriteByte(0)
    while (($Stream.Length -band 3) -ne 0) { $Stream.WriteByte(0) }
}
function New-OscPacket([string]$Address, [string]$Tags, [string[]]$Strings = @()) {
    $stream = [IO.MemoryStream]::new()
    try {
        Add-OscString $stream $Address
        Add-OscString $stream $Tags
        foreach ($value in $Strings) { Add-OscString $stream $value }
        return ,$stream.ToArray()
    }
    finally { $stream.Dispose() }
}
function Send-Osc([string]$Address, [string]$Tags, [string[]]$Strings = @()) {
    [byte[]]$packet = New-OscPacket $Address $Tags $Strings
    [void]$sender.Send($packet, $packet.Length)
}
function Read-OscString([byte[]]$Packet, [ref]$Offset) {
    $start = [int]$Offset.Value
    $end = $start
    while ($end -lt $Packet.Length -and $Packet[$end] -ne 0) { $end++ }
    if ($start -lt 0 -or $end -ge $Packet.Length) { throw 'A received OSC string was incomplete.' }
    $value = $utf8.GetString($Packet, $start, $end - $start)
    $Offset.Value = ($end + 4) -band -4
    if ($Offset.Value -gt $Packet.Length) { throw 'A received OSC string had invalid padding.' }
    return $value
}
function New-ProxyStartInfo([int]$InputPort, [int]$OutputPort) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $windowsPowerShell
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = $fixtureRoot
    # Windows filenames cannot contain a quote; neither file argument ends in a
    # slash. These absolute, fixture-owned paths are the only quoted arguments.
    $info.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $proxyScript +
        '" -ListenPort ' + $InputPort + ' -TargetPort ' + $OutputPort + ' -LogPath "' + $logPath + '" -FinalGraceMs 0'
    $info.EnvironmentVariables['SAYWHAT_DATA_ROOT'] = $isolatedData
    return $info
}
function Stop-RetainedProcess($Process) {
    if ($null -eq $Process) { return }
    if (-not $Process.HasExited) { $Process.Kill() }
    if (-not $Process.WaitForExit(5000)) { throw 'The retained fixture proxy did not exit.' }
}
function Wait-Ready {
    $nonce = [Guid]::NewGuid().ToString('N')
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        Check (-not $proxy.HasExited) 'the proxy remains alive while acknowledging a readiness barrier'
        $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        try {
            Send-Osc '/foxtrans/control/flush' ',s' @($nonce)
            $packet = $sender.Receive([ref]$remote)
            $offset = 0
            $address = Read-OscString $packet ([ref]$offset)
            $tags = Read-OscString $packet ([ref]$offset)
            if ($address -eq '/foxtrans/control/flushed' -and $tags -eq ',s' -and
                (Read-OscString $packet ([ref]$offset)) -eq $nonce) {
                Check ($remote.Port -eq $listenPort) 'readiness is acknowledged by the proxy input socket'
                return
            }
        }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -notin @([Net.Sockets.SocketError]::TimedOut, [Net.Sockets.SocketError]::ConnectionReset)) { throw }
        }
        Start-Sleep -Milliseconds 75
    }
    throw 'The actual proxy did not acknowledge readiness within 25 seconds.'
}
function Assert-CapturedChat([string]$Text, [string]$Stage) {
    $identity = [Guid]::NewGuid().ToString('N')
    Send-Osc '/foxtrans/control/captured-start' ',s' @($identity)
    Send-Osc '/foxtrans/control/captured-final' ',ss' @($identity, $Text)
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while ([DateTime]::UtcNow -lt $deadline) {
        Check (-not $proxy.HasExited) ($Stage + ': the proxy keeps running')
        $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        try { $packet = $receiver.Receive([ref]$remote) }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -eq [Net.Sockets.SocketError]::TimedOut) { continue }
            throw
        }
        $offset = 0
        $address = Read-OscString $packet ([ref]$offset)
        $tags = Read-OscString $packet ([ref]$offset)
        if ($address -eq '/chatbox/typing') { continue }
        Check ($address -ceq '/chatbox/input') ($Stage + ': the output uses the VRChat chat address')
        Check ($tags -ceq ',sTF') ($Stage + ': chat is submitted immediately without a notification')
        $actual = Read-OscString $packet ([ref]$offset)
        Check ($actual -ceq $Text) ($Stage + ': all English, Japanese and Korean text arrives as exact UTF-8')
        Check ($offset -eq $packet.Length) ($Stage + ': the packet has no unexpected trailing payload')
        Check (($actual.Split([char]10)).Count -eq 3) ($Stage + ': all three translation lines are retained')
        return
    }
    throw ('No submitted chat packet arrived while testing ' + $Stage + '.')
}
function Wait-LogText([string]$Expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            if ([IO.File]::ReadAllText($logPath, $utf8).Contains($Expected)) {
                Check $true 'file logging resumes after access is restored'
                return
            }
        }
        catch [IO.IOException], [UnauthorizedAccessException] { }
        Start-Sleep -Milliseconds 50
    }
    throw 'File logging did not resume after access was restored.'
}
function Lock-Log {
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { return [IO.File]::Open($logPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] { Start-Sleep -Milliseconds 25 }
    }
    throw 'Could not exclusively lock the fixture log between proxy entries.'
}
function Translation([string]$English) {
    # Keep this file ASCII: Windows PowerShell 5.1 may decode UTF-8 without a BOM
    # as an ANSI code page. The assertions must check real Unicode, not mojibake.
    $japanese = -join (@(0x3053, 0x3093, 0x306B, 0x3061, 0x306F, 0x3002) | ForEach-Object { [char]$_ })
    $korean = -join (@(0xC548, 0xB155, 0xD558, 0xC138, 0xC694, 0x002E) | ForEach-Object { [char]$_ })
    return $English + "`n" + $japanese + "`n" + $korean
}

try {
    $receiver = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
    $receiver.Client.ReceiveTimeout = 150
    $targetPort = ([Net.IPEndPoint]$receiver.Client.LocalEndPoint).Port
    $reservation = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
    try { $listenPort = ([Net.IPEndPoint]$reservation.Client.LocalEndPoint).Port }
    finally { $reservation.Dispose() }
    Check ($listenPort -ne $targetPort -and $listenPort -notin @(9000, 9001, 9010, 9011, 8080, 1234) -and
        $targetPort -notin @(9000, 9001, 9010, 9011, 8080, 1234)) 'both sockets use separate ephemeral ports rather than live application ports'
    $sender = [Net.Sockets.UdpClient]::new()
    $sender.Connect([Net.IPAddress]::Loopback, $listenPort)
    $sender.Client.ReceiveTimeout = 250
    $logLock = [IO.File]::Open($logPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $proxyAge = [Diagnostics.Stopwatch]::StartNew()
    $proxy = [Diagnostics.Process]::Start((New-ProxyStartInfo $listenPort $targetPort))
    $stdoutTask = $proxy.StandardOutput.ReadToEndAsync()
    $stderrTask = $proxy.StandardError.ReadToEndAsync()
    Wait-Ready
    Send-Osc '/foxtrans/control/muted' ',F'
    Assert-CapturedChat (Translation 'Hello during the startup lock.') 'startup log lock'
    Check ($logLock.Length -eq 0) 'the startup lock actually prevented file logging'
    $logLock.Dispose(); $logLock = $null
    Assert-CapturedChat (Translation 'Hello after the startup lock.') 'startup recovery'
    Wait-LogText 'Hello after the startup lock.'

    $logLock = Lock-Log
    $lockedLength = $logLock.Length
    Assert-CapturedChat (Translation 'Hello during a later lock.') 'later log lock'
    Check ($logLock.Length -eq $lockedLength) 'the later lock prevents log writes while chat still passes'
    $logLock.Dispose(); $logLock = $null
    Assert-CapturedChat (Translation 'Hello after the later lock.') 'later recovery'
    Wait-LogText 'Hello after the later lock.'
    [IO.File]::SetAttributes($logPath, [IO.FileAttributes]::ReadOnly)
    $readOnlyLength = ([IO.FileInfo]::new($logPath)).Length
    Assert-CapturedChat (Translation 'Hello with a read-only log.') 'read-only log'
    Wait-Ready
    Check (([IO.FileInfo]::new($logPath)).Length -eq $readOnlyLength) 'the read-only log rejects writes without stopping chat'
    [IO.File]::SetAttributes($logPath, [IO.FileAttributes]::Normal)
    Assert-CapturedChat (Translation 'Hello after all log failures.') 'final recovery'
    Wait-LogText 'Hello after all log failures.'
    Check (-not $proxy.HasExited) 'the actual proxy survived every unavailable-log case'
    Stop-RetainedProcess $proxy
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    Check ($stdout.Contains('FoxTrans OSC sentence mode is ready.')) 'startup remains visible through supervised output'
    $warnings = [regex]::Matches($stdout, 'WARNING OSC: code=log-write-unavailable;').Count
    $maximumWarnings = 1 + [int][Math]::Floor($proxyAge.Elapsed.TotalSeconds / 30)
    Check ($warnings -ge 1 -and $warnings -le $maximumWarnings) 'unavailable-log warnings are rate-limited instead of emitted for every packet'
    Check ($stdout.Contains('OSC file logging is available again.')) 'recovery is reported through supervised output'
    Check ([string]::IsNullOrWhiteSpace($stderr)) 'successful forwarding has no unhandled diagnostic errors'

    # Failures in real transport arguments remain fatal; best-effort logging
    # must not disguise programmer/configuration errors as a healthy proxy.
    $invalidProxy = [Diagnostics.Process]::Start((New-ProxyStartInfo -1 $targetPort))
    $invalidStdout = $invalidProxy.StandardOutput.ReadToEndAsync()
    $invalidStderr = $invalidProxy.StandardError.ReadToEndAsync()
    Check ($invalidProxy.WaitForExit(20000)) 'invalid port validation returns promptly'
    $invalidOutput = $invalidStdout.GetAwaiter().GetResult() + $invalidStderr.GetAwaiter().GetResult()
    Check ($invalidProxy.ExitCode -ne 0 -and $invalidOutput.Contains('ArgumentOutOfRangeException')) 'invalid port arguments still produce a nonzero error'
    Write-Output ('PASS: ' + $script:checks + ' actual OSC proxy logging/UTF-8/submission checks; synthetic loopback only, no model, audio or VRChat.')
}
finally {
    if ($null -ne $logLock) { $logLock.Dispose() }
    foreach ($process in @($proxy, $invalidProxy)) {
        if ($null -ne $process) {
            try { Stop-RetainedProcess $process }
            finally { $process.Dispose() }
        }
    }
    if ($null -ne $sender) { $sender.Dispose() }
    if ($null -ne $receiver) { $receiver.Dispose() }
    # Only exact fixture-owned paths are removed. No recursive delete or user
    # data path is involved, including when a regression leaves extra files.
    if ([IO.File]::Exists($logPath)) {
        [IO.File]::SetAttributes($logPath, [IO.FileAttributes]::Normal)
        [IO.File]::Delete($logPath)
    }
    if ([IO.Directory]::Exists($isolatedData)) { [IO.Directory]::Delete($isolatedData, $false) }
    if ([IO.Directory]::Exists($fixtureRoot)) { [IO.Directory]::Delete($fixtureRoot, $false) }
}

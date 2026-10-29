param([ValidateRange(0,65535)][int]$Port, [string]$ReadyPath, [string]$RequestsPath)
$ErrorActionPreference = 'Stop'
# Offline CI fixture only; not installed, embedded in, or invoked by the app.
$until = [DateTime]::UtcNow.AddSeconds(20)
if ($Port -eq 0) {
    [IO.File]::WriteAllText($ReadyPath, 'idle')
    while ([DateTime]::UtcNow -lt $until) { [Threading.Thread]::Sleep(25) }
    exit 0
}
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$listener.Start()
try {
    [IO.File]::WriteAllText($ReadyPath, 'listening')
    while ([DateTime]::UtcNow -lt $until) {
        if (-not $listener.Pending()) { [Threading.Thread]::Sleep(20); continue }
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $stream.ReadTimeout = 1000
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
            for ($lineIndex = 0; $lineIndex -lt 40; $lineIndex++) {
                $line = $reader.ReadLine()
                if ([String]::IsNullOrEmpty($line)) { break }
            }
            [IO.File]::AppendAllText($RequestsPath, "request`n")
            $body = '{"ready":true}'
            $response = "HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n$body"
            $bytes = [Text.Encoding]::ASCII.GetBytes($response)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
        } finally { $client.Dispose() }
    }
} finally { $listener.Stop() }

<#
.SYNOPSIS
Discovers and calls a ParallelSystems Live Session enabled in Revit.
.EXAMPLE
./Invoke-RevitLiveSession.ps1 -List
.EXAMPLE
./Invoke-RevitLiveSession.ps1 -Command session.info
#>
[CmdletBinding(DefaultParameterSetName = 'Invoke')]
param(
    [Parameter(ParameterSetName = 'List', Mandatory)] [switch] $List,
    [Parameter(ParameterSetName = 'Invoke')] [int] $RevitProcessId,
    [Parameter(ParameterSetName = 'Invoke')] [string] $Command = 'session.info',
    [Parameter(ParameterSetName = 'Invoke')] [hashtable] $Arguments = @{},
    [Parameter(ParameterSetName = 'Invoke')] [ValidateRange(20, 3600)] [int] $TimeoutSeconds = 35,
    [string] $DiscoveryDirectory = (Join-Path $env:LOCALAPPDATA 'ParallelSystems/LiveSessions')
)
$ErrorActionPreference = 'Stop'
$sessions = @(if (Test-Path -LiteralPath $DiscoveryDirectory) {
    foreach ($file in Get-ChildItem -LiteralPath $DiscoveryDirectory -Filter '*.json' -File) {
        try {
            $session = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $process = Get-Process -Id $session.processId -ErrorAction Stop
            if ($process.ProcessName -ne 'Revit') { continue }
            if ($session.protocolVersion -ne 1 -or $session.host -ne '127.0.0.1' -or
                $session.port -lt 1 -or $session.port -gt 65535) { continue }
            # Process.StartTime may be unavailable from a restricted terminal.
            # PowerShell 7 may also deserialize ISO timestamps to DateTime already.
            $started = if ($session.startedUtc -is [DateTime]) {
                [DateTimeOffset]::new($session.startedUtc)
            } else {
                [DateTimeOffset]::Parse([string]$session.startedUtc, [Globalization.CultureInfo]::InvariantCulture)
            }
            try { $processStart = $process.StartTime } catch { $processStart = $null }
            if ($null -ne $processStart -and $processStart.ToUniversalTime() -gt $started.UtcDateTime) { continue }
            $session
        } catch { continue }
    }
})
if ($List) {
    # Never print the authentication token in session listings.
    $sessions | Select-Object processId, revitVersion, host, port, startedUtc
    return
}
if ($RevitProcessId) { $sessions = @($sessions | Where-Object processId -eq $RevitProcessId) }
if ($sessions.Count -eq 0) { throw 'No enabled Revit session found. Turn Live Session ON in ParallelSystems > Settings.' }
if ($sessions.Count -gt 1) { throw 'Multiple Revit sessions are enabled. Use -List, then specify -RevitProcessId.' }
$session = $sessions[0]
$client = [System.Net.Sockets.TcpClient]::new()
try {
    $connect = $client.ConnectAsync('127.0.0.1', [int]$session.port)
    if (-not $connect.Wait(5000)) { throw 'Connection timed out. Check that Live Session is still ON.' }
    $null = $connect.GetAwaiter().GetResult()
    $stream = $client.GetStream()
    $stream.ReadTimeout = $TimeoutSeconds * 1000
    $stream.WriteTimeout = 5000
    $request = @{ token = $session.token; command = $Command; args = $Arguments } | ConvertTo-Json -Compress -Depth 16
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($request + "`n")
    if ($bytes.Length -gt 65536) { throw 'Request exceeds 64 KiB.' }
    $stream.Write($bytes, 0, $bytes.Length)
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
    $responseText = $reader.ReadLine()
    if (-not $responseText) { throw 'Revit closed the connection without a response. Check the toggle and reconnect.' }
    $response = $responseText | ConvertFrom-Json
    if (-not $response.ok) { throw ('{0}: {1}' -f $response.error.code, $response.error.message) }
    $response.result
} finally { $client.Dispose() }

$ErrorActionPreference = 'Stop'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
$sessionId = (Get-Process -Id $PID).SessionId
$name = "axiom-status-v1-${sid}-s$sessionId"

function ReadExact($pipe, [int]$count) {
    $buffer = [byte[]]::new($count)
    $offset = 0
    $limit = [System.Threading.CancellationTokenSource]::new(5000)
    try {
        while ($offset -lt $count) {
            $read = $pipe.ReadAsync($buffer, $offset, $count - $offset, $limit.Token).GetAwaiter().GetResult()
            if ($read -eq 0) { throw 'Pipe closed before rejection response.' }
            $offset += $read
        }
    }
    finally { $limit.Dispose() }
    return $buffer
}

function Probe([string]$scenario, $changes, [bool]$oversize = $false) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $name,
        [System.IO.Pipes.PipeDirection]::InOut,
        ([System.IO.Pipes.PipeOptions]::Asynchronous -bor [System.IO.Pipes.PipeOptions]::CurrentUserOnly))
    try {
        $pipe.Connect(3000)
        $writer = [System.IO.BinaryWriter]::new($pipe, [System.Text.Encoding]::UTF8, $true)
        if ($oversize) { $writer.Write([int]4097) }
        else {
            $request = @{
                Major = 1; Minor = 0; Schema = 1; Type = 'observe'; RequestId = $scenario
                Scope = 'machine'; Service = 'EventLog'; DeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(10)
            }
            foreach ($key in $changes.Keys) { $request[$key] = $changes[$key] }
            $payload = [System.Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Compress))
            $writer.Write([int]$payload.Length)
            $writer.Write($payload)
        }
        $writer.Flush()
        $size = [System.BitConverter]::ToInt32((ReadExact $pipe 4), 0)
        if ($size -lt 1 -or $size -gt 4096) { throw "Bad reply frame: $size" }
        $reply = [System.Text.Encoding]::UTF8.GetString((ReadExact $pipe $size)) | ConvertFrom-Json
        if ($reply.Type -ne 'error' -or $reply.Observing -or -not $reply.Error) {
            throw "$scenario was not rejected"
        }
        "$scenario`: $($reply.Error)"
    }
    finally { $pipe.Dispose() }
}

Probe 'major' @{ Major = 2 }
Probe 'scope' @{ Scope = 'session' }
Probe 'service' @{ Service = 'Spooler' }
Probe 'operation' @{ Type = 'stop' }
Probe 'deadline' @{ DeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(-1) }
Probe 'oversize' @{} $true

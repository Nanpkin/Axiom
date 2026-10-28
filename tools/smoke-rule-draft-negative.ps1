$ErrorActionPreference = 'Stop'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
$sessionId = (Get-Process -Id $PID).SessionId
$name = "axiom-rule-drafts-v1-${sid}-s$sessionId"

function ReadExact($pipe, [int]$count) {
    $buffer = [byte[]]::new($count)
    $offset = 0
    $limit = [System.Threading.CancellationTokenSource]::new(5000)
    try {
        while ($offset -lt $count) {
            $read = $pipe.ReadAsync($buffer, $offset, $count - $offset, $limit.Token).GetAwaiter().GetResult()
            if ($read -eq 0) { throw 'Pipe closed before reply.' }
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
        if ($oversize) { $writer.Write([int]65537) }
        else {
            $request = @{
                Major = 1; Minor = 0; Schema = 1; Type = 'list'; RequestId = $scenario
                Scope = 'user'; DeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(10).ToString('O')
            }
            foreach ($key in $changes.Keys) { $request[$key] = $changes[$key] }
            $payload = [System.Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Compress))
            $writer.Write([int]$payload.Length)
            $writer.Write($payload)
        }
        $writer.Flush()
        $size = [System.BitConverter]::ToInt32((ReadExact $pipe 4), 0)
        if ($size -lt 1 -or $size -gt 65536) { throw "Bad reply frame: $size" }
        $reply = [System.Text.Encoding]::UTF8.GetString((ReadExact $pipe $size)) | ConvertFrom-Json
        if ($scenario -eq 'valid') {
            if ($reply.Type -ne 'list' -or $null -eq $reply.Items) { throw "Valid list rejected: $($reply | ConvertTo-Json -Compress)" }
        }
        elseif ($reply.Type -ne 'error' -or -not $reply.ErrorCode) {
            throw "$scenario was not rejected"
        }
        "$scenario`: $($reply.Type) $($reply.ErrorCode)"
    }
    finally { $pipe.Dispose() }
}

Probe 'valid' @{}
Probe 'major' @{ Major = 2 }
Probe 'scope' @{ Scope = 'machine' }
Probe 'operation' @{ Type = 'execute' }
Probe 'deadline' @{ DeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O') }
Probe 'forged-identity' @{ CallerSid = 'S-1-5-18' }
Probe 'reply-fields' @{ ErrorCode = 'Saved' }
Probe 'oversize' @{} $true

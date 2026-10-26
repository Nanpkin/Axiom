$ErrorActionPreference = 'Stop'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
$sessionId = (Get-Process -Id $PID).SessionId
$name = "axiom-status-v1-${sid}-s$sessionId"
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $name,
    [System.IO.Pipes.PipeDirection]::InOut,
    ([System.IO.Pipes.PipeOptions]::Asynchronous -bor [System.IO.Pipes.PipeOptions]::CurrentUserOnly))
try {
    $pipe.Connect(3000)
    $writer = [System.IO.BinaryWriter]::new($pipe, [System.Text.Encoding]::UTF8, $true)
    function Send([string]$operation) {
        $payload = [System.Text.Encoding]::UTF8.GetBytes((@{
            Major = 1; Minor = 0; Schema = 1; Type = $operation
            RequestId = $operation; Scope = 'machine'; Service = 'EventLog'
            DeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(10)
        } | ConvertTo-Json -Compress))
        $writer.Write([int]$payload.Length)
        $writer.Write($payload)
        $writer.Flush()
    }
    function Receive {
        function ReadExact([int]$count) {
            $buffer = [byte[]]::new($count)
            $offset = 0
            $limit = [System.Threading.CancellationTokenSource]::new(5000)
            try {
                while ($offset -lt $count) {
                    $read = $pipe.ReadAsync($buffer, $offset, $count - $offset, $limit.Token).GetAwaiter().GetResult()
                    if ($read -eq 0) { throw 'Pipe closed.' }
                    $offset += $read
                }
            }
            finally { $limit.Dispose() }
            return $buffer
        }
        $size = [System.BitConverter]::ToInt32((ReadExact 4), 0)
        if ($size -lt 1 -or $size -gt 4096) { throw "Unexpected frame size: $size" }
        $json = [System.Text.Encoding]::UTF8.GetString((ReadExact $size))
        return ($json | ConvertFrom-Json)
    }
    Send 'observe'
    $observed = $false
    for ($i = 0; $i -lt 4; $i++) {
        $message = Receive
        $message | ConvertTo-Json -Compress
        if ($message.State -eq 'running' -or $message.State -eq 'stopped' -or
            $message.State -eq 'paused') { $observed = $true; break }
    }
    if (-not $observed) { throw 'No known SCM sample received.' }
    Send 'pause'
    for ($i = 0; $i -lt 4; $i++) {
        $message = Receive
        $message | ConvertTo-Json -Compress
        if ($message.RequestId -eq 'pause') {
            if ($message.Observing) { throw 'Pause left display demand active.' }
            break
        }
    }
}
finally { $pipe.Dispose() }

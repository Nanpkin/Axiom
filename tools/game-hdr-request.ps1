param(
    [ValidateSet('status', 'save', 'delete', 'shutdown')][string]$Type = 'status',
    [string]$Id,
    [long]$Version = 0,
    [string]$Title,
    [string]$ProcessName,
    [string[]]$ProcessNames,
    [string]$DisplayPath,
    [bool]$HdrEnabled = $false,
    [bool]$Paused = $false
)
$ErrorActionPreference = 'Stop'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', "axiom-game-hdr-host-v1-$sid", [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::Asynchronous -bor [System.IO.Pipes.PipeOptions]::CurrentUserOnly)
try {
    $pipe.Connect(3000)
    $request = @{ Version = 1; Type = $Type; RequestId = [guid]::NewGuid().ToString('N'); DeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(10) }
    if ($Type -eq 'save') {
        if (-not $Id) { $Id = [guid]::NewGuid().ToString('D') }
        if ($ProcessNames.Count -gt 0) { $ProcessName = $ProcessNames[0] }
        $request.Rule = @{ Id = $Id; Version = $Version; Title = $Title; ProcessName = $ProcessName
            DisplayPath = $DisplayPath; HdrEnabled = $HdrEnabled; Paused = $Paused }
        if ($ProcessNames.Count -gt 0) { $request.Rule.ProcessNamesText = $ProcessNames -join "`n" }
    }
    if ($Type -eq 'delete') { $request.Id = $Id; $request.ExpectedVersion = $Version }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 10 -Compress))
    $prefix = [BitConverter]::GetBytes([int]$bytes.Length)
    $pipe.Write($prefix, 0, 4)
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
    $size = [byte[]]::new(4)
    $offset = 0
    while ($offset -lt 4) { $count = $pipe.Read($size, $offset, 4 - $offset); if ($count -eq 0) { throw 'Pipe closed.' }; $offset += $count }
    $length = [BitConverter]::ToInt32($size, 0)
    if ($length -lt 1 -or $length -gt 65536) { throw 'Invalid reply size.' }
    $reply = [byte[]]::new($length)
    $offset = 0
    while ($offset -lt $length) { $count = $pipe.Read($reply, $offset, $length - $offset); if ($count -eq 0) { throw 'Pipe closed.' }; $offset += $count }
    [System.Text.Encoding]::UTF8.GetString($reply) | ConvertFrom-Json
}
finally { $pipe.Dispose() }

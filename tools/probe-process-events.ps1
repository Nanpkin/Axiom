$ErrorActionPreference = 'Stop'

$startId = 'AxiomProcessStartProbe'
$stopId = 'AxiomProcessStopProbe'
$process = $null

try {
    Register-WmiEvent -Query "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'powershell.exe'" -SourceIdentifier $startId | Out-Null
    Register-WmiEvent -Query "SELECT * FROM Win32_ProcessStopTrace WHERE ProcessName = 'powershell.exe'" -SourceIdentifier $stopId | Out-Null

    $process = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 2' -WindowStyle Hidden -PassThru
    $started = Wait-Event -SourceIdentifier $startId -Timeout 10
    if ($null -eq $started -or $started.SourceEventArgs.NewEvent.ProcessID -ne $process.Id) {
        throw "No matching process-start notification for PID $($process.Id)."
    }
    Remove-Event -EventIdentifier $started.EventIdentifier

    $process.WaitForExit(10000) | Out-Null
    $stopped = Wait-Event -SourceIdentifier $stopId -Timeout 10
    if ($null -eq $stopped -or $stopped.SourceEventArgs.NewEvent.ProcessID -ne $process.Id) {
        throw "No matching process-stop notification for PID $($process.Id)."
    }
    Remove-Event -EventIdentifier $stopped.EventIdentifier
    Write-Output "Process start and stop notifications received for controlled PID $($process.Id)."
}
finally {
    Unregister-Event -SourceIdentifier $startId -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $stopId -ErrorAction SilentlyContinue
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill() }
    if ($null -ne $process) { $process.Dispose() }
}

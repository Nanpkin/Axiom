$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$probe = Join-Path $root 'artifacts\0008-probe\Axiom.SessionAgent.exe'
$output = Join-Path $root 'artifacts\0008-window-events.txt'
$observer = $null
$helper = $null
try {
    $observer = Start-Process -FilePath $probe -ArgumentList '--probe-window-events' -WindowStyle Hidden -RedirectStandardOutput $output -PassThru
    Start-Sleep -Seconds 1
    $helper = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot 'probe-window-helper.ps1')`"") -WindowStyle Hidden -PassThru
    $helper.WaitForExit(10000) | Out-Null
    $observer.WaitForExit(20000) | Out-Null
    if (-not $observer.HasExited) { throw 'Window-event observer did not exit.' }
    $events = Get-Content -LiteralPath $output
    if ($events -notcontains "Window process hint: $($helper.Id)") {
        throw "No window-process hint for controlled PID $($helper.Id). Observed: $($events -join ', ')"
    }
    Write-Output "Window-process hint received for controlled PID $($helper.Id)."
    Write-Output ($events -join [Environment]::NewLine)
}
finally {
    if ($null -ne $helper -and -not $helper.HasExited) { $helper.Kill() }
    if ($null -ne $observer -and -not $observer.HasExited) { $observer.Kill() }
    if ($null -ne $helper) { $helper.Dispose() }
    if ($null -ne $observer) { $observer.Dispose() }
}

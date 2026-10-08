$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$probe = Join-Path $root 'artifacts\0008-probe\Axiom.SessionAgent.exe'
$output = Join-Path $root 'artifacts\0008-verified-process.txt'
$powershell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$observer = $null
$helper = $null
try {
    $observer = Start-Process -FilePath $probe -ArgumentList @('--probe-process', "`"$powershell`"") -WindowStyle Hidden -RedirectStandardOutput $output -PassThru
    Start-Sleep -Seconds 1
    $helper = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot 'probe-window-helper.ps1')`"") -WindowStyle Hidden -PassThru
    $helper.WaitForExit(10000) | Out-Null
    $observer.WaitForExit(20000) | Out-Null
    if (-not $observer.HasExited) { throw 'Verified-process observer did not exit.' }
    $events = Get-Content -LiteralPath $output
    if (@($events -match "Verified process: $($helper.Id) running=True").Count -eq 0) {
        throw "No verified start for controlled PID $($helper.Id). Observed: $($events -join ', ')"
    }
    if (@($events -match "Verified process: $($helper.Id) running=False").Count -eq 0) {
        throw "No verified exit for controlled PID $($helper.Id). Observed: $($events -join ', ')"
    }
    Write-Output "Verified start and exit for controlled PID $($helper.Id)."
    Write-Output ($events -join [Environment]::NewLine)

    $helper.Dispose()
    $helper = $null
    $observer.Dispose()
    $observer = $null

    $negativeOutput = Join-Path $root 'artifacts\0008-verified-process-negative.txt'
    $wrongPath = Join-Path $root 'artifacts\0008-probe\powershell.exe'
    $observer = Start-Process -FilePath $probe -ArgumentList @('--probe-process', "`"$wrongPath`"") -WindowStyle Hidden -RedirectStandardOutput $negativeOutput -PassThru
    Start-Sleep -Seconds 1
    $helper = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot 'probe-window-helper.ps1')`"") -WindowStyle Hidden -PassThru
    $helper.WaitForExit(10000) | Out-Null
    $observer.WaitForExit(20000) | Out-Null
    if (-not $observer.HasExited) { throw 'Negative observer did not exit.' }
    $negativeEvents = @(Get-Content -LiteralPath $negativeOutput)
    if ($negativeEvents.Count -ne 0) {
        throw "Same-name process from another path was accepted: $($negativeEvents -join ', ')"
    }
    Write-Output "Same executable name at an unapproved path was ignored for PID $($helper.Id)."
}
finally {
    if ($null -ne $helper -and -not $helper.HasExited) { $helper.Kill() }
    if ($null -ne $observer -and -not $observer.HasExited) { $observer.Kill() }
    if ($null -ne $helper) { $helper.Dispose() }
    if ($null -ne $observer) { $observer.Dispose() }
}

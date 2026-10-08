$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$shell = (Get-Process -Id $PID).Path
$dotnet = Join-Path $PSScriptRoot 'dotnet.ps1'
$ui = Join-Path $PSScriptRoot 'run-status-ui.ps1'
$hostExe = Join-Path $repo 'src/Axiom.ServiceHost/bin/Release/net10.0-windows10.0.26100.0/Axiom.ServiceHost.exe'

Push-Location $repo
try {
    $running = Get-Process -Name Axiom.ServiceHost -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $hostExe } | Select-Object -First 1
    if ($running) {
        & $shell -NoProfile -ExecutionPolicy Bypass -File $ui
        if ($LASTEXITCODE -ne 0) { throw 'UI startup failed.' }
        Write-Host 'Axiom host is already running; opened another UI window.'
        return
    }

    Write-Host 'Preparing Axiom status preview...'
    $restoreLog = & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet restore Axiom.slnx --locked-mode --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) { $restoreLog | Out-Host; throw 'Locked restore failed.' }
    Write-Host 'Dependencies ready.'
    $buildLog = & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet build Axiom.slnx -c Release --no-restore --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) { $buildLog | Out-Host; throw 'Release build failed.' }
    Write-Host 'Release build ready.'

    & $shell -NoProfile -ExecutionPolicy Bypass -File $ui
    if ($LASTEXITCODE -ne 0) { throw 'UI startup failed.' }
    Write-Host 'Axiom host will run in this window. Press Enter here to exit it.'
    & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet run --project src/Axiom.ServiceHost/Axiom.ServiceHost.csproj -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Axiom host exited with an error.' }
}
finally { Pop-Location }

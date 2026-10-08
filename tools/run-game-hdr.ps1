param([switch]$Stop)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$request = Join-Path $PSScriptRoot 'game-hdr-request.ps1'
if ($Stop) {
    $reply = & $request -Type shutdown
    if ($reply.Type -ne 'stopping') {
        if ($reply.Error) { throw $reply.Error }
        throw '后台停止未确认。'
    }
    Write-Host 'Axiom 游戏 HDR 后台正在停止。'
    return
}

Push-Location $repo
try {
    & (Join-Path $PSScriptRoot 'dotnet.ps1') restore Axiom.slnx --locked-mode --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw '依赖检查失败。' }
    & (Join-Path $PSScriptRoot 'dotnet.ps1') build Axiom.slnx -c Release --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }

    $framework = 'net10.0-windows10.0.26100.0'
    $hostExe = Join-Path $repo "src/Axiom.ServiceHost/bin/Release/$framework/Axiom.ServiceHost.exe"
    $agentExe = Join-Path $repo "src/Axiom.SessionAgent/bin/Release/$framework/Axiom.SessionAgent.exe"
    $uiExe = Join-Path $repo "src/Axiom.WinUI/bin/Release/$framework/win-x64/Axiom.WinUI.exe"
    foreach ($name in @('Axiom.ServiceHost', 'Axiom.SessionAgent')) {
        $running = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        if ($running.Count -gt 0) {
            $expected = if ($name -eq 'Axiom.ServiceHost') { $hostExe } else { $agentExe }
            if (@($running | Where-Object { $_.Path -and $_.Path -ne $expected }).Count -gt 0) {
                throw "检测到另一份 $name 正在运行；请先关闭旧预览，再启动当前版本。"
            }
        }
    }
    if (-not (Get-Process -Name Axiom.ServiceHost -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $hostExe -ArgumentList '--background' -WindowStyle Hidden | Out-Null
    }
    if (-not (Get-Process -Name Axiom.SessionAgent -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $agentExe -ArgumentList '--run' -WindowStyle Hidden | Out-Null
    }
    $connected = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            $reply = & $request -Type status 2>$null
            if ($reply.Type -eq 'status') { $connected = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $connected) { throw '后台尚未准备好；请检查 Axiom.ServiceHost 和 Axiom.SessionAgent 是否仍在运行。' }
    if (-not (Get-Process -Name Axiom.WinUI -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $uiExe | Out-Null
        Write-Host '游戏 HDR 设置已打开。关闭界面后，后台仍继续监控。'
    }
    else { Write-Host '界面已打开；后台现在独立运行，关闭界面后仍继续监控。' }
    Write-Host '停止后台：运行 tools/run-game-hdr.ps1 -Stop'
}
finally { Pop-Location }

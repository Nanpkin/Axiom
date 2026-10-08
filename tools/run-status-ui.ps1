$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $repo 'artifacts/tooling/dotnet'
if (Test-Path (Join-Path $localDotnet 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localDotnet
    $env:PATH = "$localDotnet;$env:PATH"
}
$app = Join-Path $repo 'src/Axiom.WinUI/bin/Release/net10.0-windows10.0.26100.0/win-x64/Axiom.WinUI.exe'
if (-not (Test-Path $app)) { throw 'Build Axiom.slnx -c Release first.' }
Start-Process -FilePath $app

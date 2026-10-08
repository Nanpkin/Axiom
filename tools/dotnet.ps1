# Scope all SDK/cache environment changes to this child PowerShell process.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $repo 'artifacts/tooling/cli'
$env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$localSdk = Join-Path $repo 'artifacts/tooling/dotnet/dotnet.exe'
$systemSdk = Get-Command dotnet -ErrorAction SilentlyContinue
$sdk = if ($systemSdk) { $systemSdk.Source } elseif (Test-Path $localSdk) { $localSdk } else { throw 'Install the SDK version specified in global.json.' }
$env:DOTNET_ROOT = Split-Path $sdk -Parent
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
& $sdk @args
exit $LASTEXITCODE

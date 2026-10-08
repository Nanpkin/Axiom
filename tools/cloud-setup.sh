#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# Run in an isolated Linux cloud checkout; install the repository-pinned SDK only.
sdk_version="$(python3 -c 'import json; print(json.load(open("global.json"))["sdk"]["version"])')"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | cut -d ' ' -f 1 | grep -Fxq "$sdk_version"; then
    installer="$(mktemp)"
    trap 'rm -f "$installer"' EXIT
    curl --fail --location --retry 3 https://dot.net/v1/dotnet-install.sh --output "$installer"
    bash "$installer" --version "$sdk_version" --install-dir "$DOTNET_ROOT"
fi
dotnet restore tests/Axiom.Domain.Tests/Axiom.Domain.Tests.csproj --locked-mode -p:NuGetAudit=false
dotnet restore tests/Axiom.Engine.Tests/Axiom.Engine.Tests.csproj --locked-mode -p:NuGetAudit=false
printf '%s\n' 'Core dependencies ready. Windows UI and native integration require a Windows runner.'

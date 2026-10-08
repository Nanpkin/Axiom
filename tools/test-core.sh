#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
filter=''
while IFS= read -r suite || [[ -n "$suite" ]]; do
    suite="${suite%$'\r'}"
    [[ -z "$suite" ]] && continue
    filter+="${filter:+|}FullyQualifiedName~Axiom.Engine.Tests.$suite."
done < tools/core-test-suites.txt
[[ -n "$filter" ]] || { printf '%s\n' 'Empty core suite list' >&2; exit 1; }
dotnet test tests/Axiom.Domain.Tests/Axiom.Domain.Tests.csproj -c Release -p:RestoreLockedMode=true -p:NuGetAudit=false /m:1 /nr:false
dotnet test tests/Axiom.Engine.Tests/Axiom.Engine.Tests.csproj -c Release -p:RestoreLockedMode=true -p:NuGetAudit=false /m:1 /nr:false --filter "$filter"

[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# Explicitly platform-independent suites. Windows integration is a separate gate.
$suites = Get-Content (Join-Path $PSScriptRoot 'core-test-suites.txt') | Where-Object { $_ }
$filter = ($suites | ForEach-Object { "FullyQualifiedName~Axiom.Engine.Tests.$_." }) -join '|'
foreach ($project in @('Axiom.Domain.Tests','Axiom.Engine.Tests')) {
    $arguments = @('test', (Join-Path $repo "tests/$project/$project.csproj"), '-c','Release',
        '-p:RestoreLockedMode=true','-p:NuGetAudit=false','/m:1','/nr:false')
    if ($project -eq 'Axiom.Engine.Tests') { $arguments += @('--filter', $filter) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Core checks failed: $project" }
}

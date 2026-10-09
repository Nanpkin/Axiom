$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-validation.ps1')
$repository = Split-Path $PSScriptRoot -Parent
if ($env:AXIOM_RELEASE_EVENT -eq 'push') {
    if (!$env:AXIOM_RELEASE_TAG.StartsWith('v', [StringComparison]::Ordinal)) { throw 'Release tags must start with v.' }
    $version = Get-AxiomReleaseVersion $env:AXIOM_RELEASE_TAG.Substring(1)
} elseif ($env:AXIOM_RELEASE_EVENT -eq 'workflow_dispatch') {
    $version = Get-AxiomReleaseVersion $env:AXIOM_RELEASE_VERSION
} else { throw 'Unsupported release event.' }
$commit = Get-AxiomReleaseCommit $repository $version
if (!$env:GITHUB_OUTPUT) { throw 'GitHub output file is required.' }
[IO.File]::AppendAllLines($env:GITHUB_OUTPUT, [string[]]@("version=$version", "tag=v$version", "sha=$commit"))

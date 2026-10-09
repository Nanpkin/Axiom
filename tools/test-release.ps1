$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-validation.ps1')
$checks = 0
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw 'An invalid release fixture was accepted.' }
    $script:checks++
}
foreach ($file in @('release-validation.ps1', 'resolve-release.ps1', 'publish-github-release.ps1', 'publish-package.ps1', 'test-release.ps1')) {
    $tokens = $null; $errors = $null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -ne 0) { throw "Release script syntax failed: $file" }; $checks++
}
foreach ($invalid in @('', 'v0.2.0', '01.2.0', '0.2', '0.2.0-..', '0.2.0/other', '0.2.0;echo unsafe', "0.2.0`nsha=unsafe", '65536.0.0', ('0.2.0-' + ('a' * 81)))) {
    Assert-Rejected { Get-AxiomReleaseVersion $invalid }
}
$version = '0.2.0-preview.20261009.1'
if ((Get-AxiomReleaseVersion $version) -cne $version) { throw 'Valid preview version was rejected.' }; $checks++
$root = Join-Path ([IO.Path]::GetTempPath()) ('axiom-release-test-' + [Guid]::NewGuid().ToString('N'))
$savedGitConfig = $env:GIT_CONFIG_GLOBAL
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    # Isolate synthetic commit identity from any real repository or personal global configuration.
    $env:GIT_CONFIG_GLOBAL = Join-Path $root 'empty-gitconfig'
    $repository = Join-Path $root 'repo'; New-Item -ItemType Directory -Path $repository | Out-Null
    & git -C $repository init --initial-branch=main | Out-Null
    & git -C $repository config user.name 'Axiom fixture'
    & git -C $repository config user.email 'axiom-fixture@example.invalid'
    & git -C $repository -c commit.gpgsign=false commit --allow-empty -m 'synthetic main' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create a synthetic main commit.' }
    $commit = & git -C $repository rev-parse HEAD
    & git -C $repository update-ref refs/remotes/origin/main $commit
    & git -C $repository -c tag.gpgsign=false tag -a "v$version" -m 'synthetic release'
    if ((Get-AxiomReleaseCommit $repository $version) -cne $commit) { throw 'Annotated version tag did not resolve to its exact commit.' }; $checks++
    Assert-Rejected { Get-AxiomReleaseCommit $repository '0.2.1-preview.missing' }
    & git -C $repository switch -c synthetic-feature 2>$null
    & git -C $repository -c commit.gpgsign=false commit --allow-empty -m 'synthetic unmerged change' | Out-Null
    & git -C $repository tag v0.2.1-preview.unmerged
    Assert-Rejected { Get-AxiomReleaseCommit $repository '0.2.1-preview.unmerged' }

    $stage = Join-Path $root 'stage'; $assets = Join-Path $root 'assets'
    New-Item -ItemType Directory -Path $stage, $assets | Out-Null
    $info = [ordered]@{ schemaVersion = 1; version = $version; sourceCommit = $commit; sourceDirty = $false; target = 'win-x64'; builtAtUtc = '2026-10-09T00:00:00+00:00' }
    $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'BUILD-INFO.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $stage 'BUILD-INFO.json') -Destination $assets
    'synthetic installer, never executed' | Set-Content -LiteralPath (Join-Path $assets "Axiom-$version-win-x64-setup.exe")
    $zipPath = Join-Path $assets "Axiom-$version-win-x64-portable.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath)
    function Update-FixtureChecksums {
        $hashes = Get-ChildItem -LiteralPath $assets -File | Where-Object Extension -In @('.zip', '.exe', '.json') | ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
        }
        [IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS.txt'), [string[]]$hashes)
    }
    Update-FixtureChecksums
    if (@(Assert-AxiomReleaseAssets $assets $version $commit).Count -ne 4) { throw 'Valid fixture assets were rejected.' }; $checks++
    Assert-Rejected { Assert-AxiomReleaseAssets $assets $version ('a' * 40) }
    Assert-Rejected { Assert-AxiomReleaseAssets $assets '0.2.1-preview.other' $commit }
    Add-Content -LiteralPath (Join-Path $assets "Axiom-$version-win-x64-setup.exe") -Value 'synthetic tamper'
    Assert-Rejected { Assert-AxiomReleaseAssets $assets $version $commit }
    Update-FixtureChecksums
    Add-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Value ([IO.File]::ReadAllLines((Join-Path $assets 'SHA256SUMS.txt'))[0])
    Assert-Rejected { Assert-AxiomReleaseAssets $assets $version $commit }
    Update-FixtureChecksums
    $info.sourceDirty = $true; $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $assets 'BUILD-INFO.json') -Encoding utf8
    Update-FixtureChecksums
    Assert-Rejected { Assert-AxiomReleaseAssets $assets $version $commit }
    $info.sourceDirty = $false; $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $assets 'BUILD-INFO.json') -Encoding utf8
    $info.sourceCommit = 'b' * 40; $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'BUILD-INFO.json') -Encoding utf8
    Remove-Item -LiteralPath $zipPath; [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath)
    Update-FixtureChecksums
    Assert-Rejected { Assert-AxiomReleaseAssets $assets $version $commit }
    'synthetic unexpected artifact' | Set-Content -LiteralPath (Join-Path $assets 'unexpected.txt')
    Assert-Rejected { Assert-AxiomReleaseAssets $assets $version $commit }
} finally {
    $env:GIT_CONFIG_GLOBAL = $savedGitConfig
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "Release guard checks passed: $checks"
# Expected git rejection cases leave LASTEXITCODE=1. Clear it only after every assertion succeeds,
# so GitHub's dot-sourced pwsh wrapper reports the result of this test rather than the last negative fixture.
$global:LASTEXITCODE = 0

[CmdletBinding()]
param([switch]$Deep, [switch]$KeepStage)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'artifact-paths.ps1')
$buildLock = [IO.File]::Open((Join-Path $AxiomRepoRoot 'artifacts/build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$freed = 0L
$runningPaths = @(Get-Process | ForEach-Object { try { $_.Path } catch { } } | Where-Object { $_ })
function Remove-GeneratedDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = $AxiomRepoRoot + [IO.Path]::DirectorySeparatorChar
    if (!$full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside workspace: $full" }
    if (!(Test-Path -LiteralPath $full)) { return }
    if ($runningPaths | Where-Object {$_.StartsWith($full + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)}) { Write-Output "In use; preserved: $full"; return }
    $parent = $full
    while ($parent.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point: $parent" }
        $parent = Split-Path $parent -Parent
    }
    if (Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}) { throw "Nested reparse point: $full" }
    $script:freed += (Get-ChildItem -LiteralPath $full -File -Recurse -Force | Measure-Object Length -Sum).Sum
    Remove-Item -LiteralPath $full -Recurse -Force
}
try {
# Preserve sources, screenshots, logs and user data; never touch the current preview/release.
foreach ($name in @('preview-publish','distribution-build','package-build','package-stage')) {
    if ($KeepStage -and $name -eq 'package-stage') { continue }
    $path = Assert-AxiomArtifactPath (Join-Path $AxiomRepoRoot "artifacts/$name")
    Remove-GeneratedDirectory $path
}
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $AxiomRepoRoot 'artifacts') -Directory) {
    if ($directory.Name -notmatch '(-smoke$|^tray-diagnostic$|^tray-probe$)') { continue }
    $null = Assert-AxiomArtifactPath $directory.FullName
    foreach ($generated in @(Get-ChildItem -LiteralPath $directory.FullName -Directory -Recurse | Where-Object {$_.Name -in @('bin','obj')} | Sort-Object {$_.FullName.Length} -Descending)) {
        Remove-GeneratedDirectory $generated.FullName
    }
    foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -File -Recurse) {
        if ($file.Extension -in @('.exe','.dll','.pdb','.winmd','.xbf','.pri','.mui','.so','.lib','.exp') -or $file.Name -match '\.(deps|runtimeconfig)\.json$') {
            $null = Assert-AxiomArtifactPath $file.FullName
            $freed += $file.Length; Remove-Item -LiteralPath $file.FullName -Force
        }
    }
}
if ($Deep) {
    foreach ($area in @('src','tests','benchmarks')) {
        foreach ($project in Get-ChildItem -LiteralPath (Join-Path $AxiomRepoRoot $area) -Directory) {
            foreach ($name in @('bin','obj')) { Remove-GeneratedDirectory (Join-Path $project.FullName $name) }
        }
    }
    # The optional SDK is redundant only when the exact pinned SDK is installed system-wide.
    $sdk = Get-Command dotnet -ErrorAction SilentlyContinue
    $required = (Get-Content -LiteralPath (Join-Path $AxiomRepoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($sdk -and ((& $sdk.Source --list-sdks) -match "^$([Regex]::Escape($required)) ")) {
        Remove-GeneratedDirectory (Join-Path $AxiomRepoRoot 'artifacts/tooling/dotnet')
    }
    # Old cache can be removed after migration; don't delete a cache still containing packages.
    $oldCache = Join-Path $AxiomRepoRoot 'artifacts/tooling/packages'
    if ((Test-Path -LiteralPath $oldCache) -and !(Get-ChildItem -LiteralPath $oldCache -File -Recurse -Force)) { Remove-GeneratedDirectory $oldCache }
}
Write-Output ("Cleaned {0:N1} MiB. Preview, release, source, evidence and user configuration preserved." -f ($freed / 1MB))
} finally { $buildLock.Dispose() }

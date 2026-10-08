$AxiomRepoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
function Assert-AxiomArtifactPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = (Join-Path $AxiomRepoRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar
    if (!$full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside artifact directory: $full" }
    $current = $full
    while ($current.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse point: $current" }
        $current = Split-Path $current -Parent
    }
    if ((Get-Item -LiteralPath (Join-Path $AxiomRepoRoot 'artifacts') -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Artifact root is a reparse point.' }
    return $full
}
function Remove-AxiomArtifactDirectory([string]$Path) {
    $full = Assert-AxiomArtifactPath $Path
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

# Shared by CI resolution, final upload, and synthetic release checks. This file performs no work when imported.
function Get-AxiomReleaseVersion([string]$Value) {
    if ($Value.Length -gt 80 -or $Value -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[A-Za-z0-9]+([.-][A-Za-z0-9]+)*)?\z') {
        throw 'Version must be a bounded numeric x.y.z with an optional ASCII prerelease suffix.'
    }
    foreach ($part in ($Value.Split('-')[0].Split('.'))) {
        $number = 0
        if (![int]::TryParse($part, [ref]$number) -or $number -gt 65535) { throw 'Numeric version components exceed the Windows assembly version range.' }
    }
    return $Value
}

function Get-AxiomReleaseCommit([string]$Repository, [string]$Version) {
    $validated = Get-AxiomReleaseVersion $Version
    $tag = "v$validated"
    $commit = & git -C $Repository rev-parse --verify --end-of-options "$tag^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '\A[a-f0-9]{40}\z') { throw 'The version tag must already exist and point to a commit.' }
    & git -C $Repository merge-base --is-ancestor $commit refs/remotes/origin/main
    if ($LASTEXITCODE -ne 0) { throw 'The version tag commit must belong to origin/main.' }
    return $commit
}

function Assert-AxiomReleaseAssets([string]$Directory, [string]$Version, [string]$SourceCommit) {
    $Version = Get-AxiomReleaseVersion $Version
    if ($SourceCommit -cnotmatch '\A[a-f0-9]{40}\z') { throw 'Expected source commit must be a complete lowercase SHA.' }
    $names = @("Axiom-$Version-win-x64-portable.zip", "Axiom-$Version-win-x64-setup.exe", 'BUILD-INFO.json', 'SHA256SUMS.txt')
    $files = @(Get-ChildItem -LiteralPath $Directory -Force)
    if ($files.Count -ne 4 -or @($files | Where-Object { !$_.PSIsContainer -and $_.Name -cin $names }).Count -ne 4) { throw 'Release must contain exactly portable ZIP, installer EXE, BUILD-INFO.json, and SHA256SUMS.txt.' }
    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $Directory 'SHA256SUMS.txt'))) {
        if ($line -cnotmatch '^([a-f0-9]{64})  (.+)$' -or $Matches[2] -cnotin $names[0..2] -or $hashes.ContainsKey($Matches[2])) { throw 'Checksums contain invalid, duplicate, or unexpected entries.' }
        $hashes[$Matches[2]] = $Matches[1]
    }
    if ($hashes.Count -ne 3) { throw 'Checksums must cover all three release payload files.' }
    foreach ($name in $names[0..2]) {
        if ((Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hashes[$name]) { throw "Release checksum differs: $name" }
    }
    $info = Get-Content -LiteralPath (Join-Path $Directory 'BUILD-INFO.json') -Raw | ConvertFrom-Json
    if ($info.schemaVersion -ne 1 -or $info.version -cne $Version -or $info.sourceCommit -cne $SourceCommit -or
        $info.sourceDirty -isnot [bool] -or $info.sourceDirty -or $info.target -cne 'win-x64') { throw 'Build information differs from the expected clean version-tag commit.' }
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory $names[0]))
    try {
        $entries = @($zip.Entries | Where-Object FullName -CEQ 'BUILD-INFO.json')
        if ($entries.Count -ne 1) { throw 'Portable ZIP must have one root BUILD-INFO.json.' }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { $inside = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($inside.sourceDirty -isnot [bool]) { throw 'Portable ZIP source cleanliness must be a boolean.' }
        foreach ($property in @('schemaVersion', 'version', 'sourceCommit', 'sourceDirty', 'target', 'builtAtUtc')) {
            if ($inside.$property -cne $info.$property) { throw 'Portable ZIP provenance differs from the release build information.' }
        }
    } finally { $zip.Dispose() }
    return $names
}

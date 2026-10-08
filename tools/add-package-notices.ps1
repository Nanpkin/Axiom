param([Parameter(Mandatory)][string]$PackageDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'artifact-paths.ps1')
$package = Assert-AxiomArtifactPath $PackageDir
$cache = Join-Path $env:USERPROFILE '.nuget/packages'
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$notices = [Collections.Generic.List[string]]::new()
$notices.Add('Third-party components distributed with Axiom; license terms remain those of their respective authors.')
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
if ($sdk) {
    foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
        $file = Join-Path (Split-Path $sdk.Source -Parent) $name
        if (Test-Path -LiteralPath $file) {
            $target = Join-Path $package 'licenses/dotnet'
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Copy-Item -LiteralPath $file -Destination $target -Force
        }
    }
}
foreach ($deps in Get-ChildItem -LiteralPath (Join-Path $package 'app') -Filter '*.deps.json') {
    $data = Get-Content -LiteralPath $deps.FullName -Raw | ConvertFrom-Json
    foreach ($library in $data.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or !$names.Add($library.Name)) { continue }
        $path = Join-Path $cache $library.Name.ToLowerInvariant()
        $metadata = Get-ChildItem -LiteralPath $path -Filter '*.nuspec' -ErrorAction Stop | Select-Object -First 1
        $xml = [xml](Get-Content -LiteralPath $metadata.FullName -Raw)
        $notices.Add($library.Name + ' | license: ' + $xml.package.metadata.license.InnerText + ' | ' + $xml.package.metadata.projectUrl)
        $licenses = Get-ChildItem -LiteralPath $path -File | Where-Object {$_.Name -match '^(license|licence|thirdpartynotices)(\.[a-z]+)?$'}
        foreach ($file in $licenses) {
            $target = Join-Path $package ('licenses/' + $library.Name.Replace('/','-'))
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
    }
}
$compilerLicense = Join-Path $AxiomRepoRoot 'artifacts/tooling/inno-setup/License.txt'
if (Test-Path -LiteralPath $compilerLicense) {
    $target = Join-Path $package 'licenses/Inno-Setup'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $compilerLicense -Destination $target -Force
}
[IO.File]::WriteAllLines((Join-Path $package 'THIRD-PARTY-NOTICES.txt'), $notices)

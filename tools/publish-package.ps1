[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$')][string]$Version = '0.1.0-preview.20261007',
    [switch]$Preview,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'artifact-paths.ps1')
New-Item -ItemType Directory -Path (Join-Path $AxiomRepoRoot 'artifacts') -Force | Out-Null
$buildLock = [IO.File]::Open((Join-Path $AxiomRepoRoot 'artifacts/build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$stage = Join-Path $AxiomRepoRoot 'artifacts/package-stage'
$scratch = Join-Path $AxiomRepoRoot 'artifacts/package-build'
$release = Join-Path $AxiomRepoRoot 'artifacts/release'
Remove-AxiomArtifactDirectory $stage
Remove-AxiomArtifactDirectory $scratch
New-Item -ItemType Directory -Path (Join-Path $stage 'app') -Force | Out-Null
function Merge-Publish([string]$Source, [string]$Destination) {
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart([char[]]'\/')
        if ($file.Extension -eq '.pdb') { continue }
        $target = Join-Path $Destination $relative
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Different dependency versions: $relative" }
        } else {
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}
try {
    foreach ($project in @('Axiom.Launcher','Axiom.ServiceHost','Axiom.SessionAgent','Axiom.WinUI')) {
        Remove-AxiomArtifactDirectory $scratch
        $arguments = @('publish', (Join-Path $AxiomRepoRoot "src/$project/$project.csproj"), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $scratch,
            '-p:AxiomDistribution=true', '-p:OS=Windows_NT', '-p:WindowsAppSDKSelfContained=true', '-p:NuGetAudit=false',
            '-p:NuGetLockFilePath=obj/distribution.packages.lock.json', '-p:RestoreLockedMode=false', '-p:DebugType=None', '-p:DebugSymbols=false',
            "-p:Version=$Version", '--ignore-failed-sources', '/m:1', '/nr:false')
        & (Join-Path $PSScriptRoot 'dotnet.ps1') @arguments
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
        if ($project -eq 'Axiom.Launcher') {
            Copy-Item -LiteralPath (Join-Path $scratch 'Axiom.exe') -Destination $stage
        } else { Merge-Publish $scratch (Join-Path $stage 'app') }
    }
    foreach ($relative in @('Axiom.exe','app/Axiom.ServiceHost.exe','app/Axiom.SessionAgent.exe','app/Axiom.WinUI.exe','app/coreclr.dll','app/hostfxr.dll','app/Microsoft.ui.xaml.dll','app/Axiom.WinUI.pri','app/App.xbf')) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $relative))) { throw "Incomplete distribution: $relative" }
    }
    if (Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object {$_.Extension -in @('.db','.pdb')}) { throw 'Distribution contains data or debug symbols.' }
    Copy-Item -LiteralPath (Join-Path $AxiomRepoRoot 'packaging/使用说明.txt') -Destination $stage
    & (Join-Path $PSScriptRoot 'add-package-notices.ps1') -PackageDir $stage
    if ($Preview) {
        if (Get-Process Axiom,Axiom.ServiceHost,Axiom.SessionAgent,Axiom.WinUI -ErrorAction SilentlyContinue) { throw 'Build ready in artifacts/package-stage. Stop Axiom safely before replacing the preview.' }
        $output = Join-Path $AxiomRepoRoot 'artifacts/Axiom-preview'
        Remove-AxiomArtifactDirectory $output
        Move-Item -LiteralPath $stage -Destination $output
        Write-Output "Preview ready: $output/Axiom.exe"
    } else {
        Remove-AxiomArtifactDirectory $release
        New-Item -ItemType Directory -Path $release | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = Join-Path $release "Axiom-$Version-win-x64-portable.zip"
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        if (!$SkipInstaller) {
            $compiler = & (Join-Path $PSScriptRoot 'get-inno-setup.ps1')
            & $compiler "/DAppVersion=$Version" "/DPackageDir=$stage" "/DReleaseDir=$release" (Join-Path $AxiomRepoRoot 'packaging/Axiom.iss')
            if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
        }
        $hashes = Get-ChildItem -LiteralPath $release -File | Where-Object {$_.Extension -in @('.zip','.exe')} | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name }
        [IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), [string[]]$hashes)
        Write-Output "Release files ready: $release"
        Remove-AxiomArtifactDirectory $stage
    }
} finally { Remove-AxiomArtifactDirectory $scratch; $buildLock.Dispose() }

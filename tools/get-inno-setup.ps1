# Build dependency only. Official compiler in portable mode, not registered in Windows.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'artifact-paths.ps1')
$compilerRoot = Join-Path $AxiomRepoRoot 'artifacts/tooling/inno-setup'
$compiler = Join-Path $compilerRoot 'ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $download = Join-Path $AxiomRepoRoot 'artifacts/tooling/innosetup-6.7.3.exe'
    New-Item -ItemType Directory -Path (Split-Path $download -Parent) -Force | Out-Null
    try {
        Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download -UseBasicParsing
        $signature = Get-AuthenticodeSignature -LiteralPath $download
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw 'Inno Setup publisher verification failed.' }
        $process = Start-Process -FilePath $download -WindowStyle Hidden -ArgumentList @('/PORTABLE=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + $compilerRoot + '"')) -Wait -PassThru
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Portable compiler preparation failed.' }
    } finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force } }
}
Write-Output $compiler

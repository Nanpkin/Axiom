param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (!$outputPath.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Native output must stay inside the repository.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Visual Studio x64 C++ build tools are required for startup window protection.' }
$variables = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
# Only this build child inherits the compiler environment. No machine settings change.
$environmentScript = Join-Path $outputPath 'compiler-environment.cmd'
[IO.File]::WriteAllLines($environmentScript, @('@echo off', ('call "' + $variables + '" >nul'), 'if errorlevel 1 exit /b 1', 'set'))
$environmentLines = & cmd /d /c "call `"$environmentScript`""
if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize the x64 compiler environment.' }
foreach ($line in $environmentLines) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $separator), $line.Substring($separator + 1), 'Process') }
}
$nativeSource = Join-Path $repo 'src/Axiom.Providers.Windows/Native/StartupWindowGuard.cpp'
& cl /nologo /W4 /WX /EHsc /std:c++17 /DUNICODE /MT /O2 /LD $nativeSource "/Fo:$outputPath/StartupWindowGuard.obj" "/Fe:$outputPath/Axiom.StartupWindowGuard.dll" /link user32.lib dwmapi.lib comctl32.lib
if ($LASTEXITCODE -ne 0) { throw 'Startup window protection compilation failed.' }

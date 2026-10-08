@echo off
if not exist "%~dp0artifacts\Axiom-preview\Axiom.exe" (
  echo Axiom.exe is missing. Keep the complete Axiom-preview folder.
  pause
  exit /b 1
)
start "" "%~dp0artifacts\Axiom-preview\Axiom.exe"

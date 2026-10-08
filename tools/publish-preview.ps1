# Same payload as both release channels; never replace a running preview.
& (Join-Path $PSScriptRoot 'publish-package.ps1') -Preview @args

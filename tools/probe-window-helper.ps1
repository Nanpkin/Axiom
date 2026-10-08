param([int]$Seconds = 2)
Add-Type -AssemblyName System.Windows.Forms
$form = [System.Windows.Forms.Form]::new()
try {
    $form.ShowInTaskbar = $false
    $null = $form.Handle
    Start-Sleep -Seconds $Seconds
}
finally { $form.Dispose() }

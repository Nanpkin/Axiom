[CmdletBinding()]
param([string]$GitDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$gitArgs = @('-C', $repo)
if ($GitDirectory) { $gitArgs += @("--git-dir=$GitDirectory", "--work-tree=$repo") }
$files = @(& git @gitArgs -c core.quotepath=false ls-files)
if ($LASTEXITCODE -ne 0 -or !$files.Count) { throw 'Cannot enumerate repository files.' }
$blockedPaths = '(^|/)(artifacts|bin|obj|\.codex|\.agents|\.dotnet)/|OWNER-CODEX-HANDBOOK|\.(db|db-wal|db-shm|pfx|p12|pem|key|log|trx|dmp|etl)$|(^|/)\.env($|\.)|docs/exec-plans/completed/00(?:[01][0-9]|2[0-8])-|docs/exec-plans/archived/000[48]-'
$patterns = @('(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b)[^\\/\s]+',
    '(?i)\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{24,})',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')
$userName = [Environment]::UserName
if ($userName -notin @('root','runner','runneradmin','SYSTEM','Administrator') -and $userName.Length -gt 2) {
    $patterns += '(?i)\b' + [Regex]::Escape($userName) + '\b'
}
$issues = [Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    if ($file -match $blockedPaths) { $issues.Add("Excluded file is tracked: $file"); continue }
    if ([IO.Path]::GetExtension($file) -in @('.png','.ico')) { continue }
    $content = [IO.File]::ReadAllText((Join-Path $repo $file))
    foreach ($pattern in $patterns) {
        if ([Regex]::IsMatch($content,$pattern)) { $issues.Add("Review sensitive content: $file"); break }
    }
}
if ($issues.Count) { $issues | Write-Output; throw 'Source privacy check failed; no content values were printed.' }
Write-Output "Privacy patterns passed for $($files.Count) tracked files. Review is still required for personal prose and binary metadata."

[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$SourceCommit)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-validation.ps1')
$repository = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $repository 'artifacts/release'
$Version = Get-AxiomReleaseVersion $Version
$tag = "v$Version"
if ((Get-AxiomReleaseCommit $repository $Version) -cne $SourceCommit) { throw 'Version tag no longer matches the checked package commit.' }
$names = @(Assert-AxiomReleaseAssets $directory $Version $SourceCommit)
if ($env:GITHUB_REPOSITORY -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z') { throw 'A GitHub repository is required.' }
$repo = $env:GITHUB_REPOSITORY
$versionMarker = "<!-- axiom-preview-release: $Version -->"
$sourceMarker = "<!-- axiom-source-commit: $SourceCommit -->"
$notes = @"
$versionMarker
$sourceMarker
Windows 11 x64 私有阶段预览版。下载 setup.exe 安装，或解压 portable.zip 启动 Axiom.exe。

可配置时间、程序、前台应用、窗口、系统、电源、Wi-Fi、USB和默认音频设备等检测，组合提醒、程序/窗口、音量、亮度、电源方案、锁屏、HDR和条件期间保持唤醒等动作。功能和限制以此提交的[交接说明](https://github.com/$repo/blob/$SourceCommit/docs/product/current-state.md)为准；示例规则默认暂停。

本版本从提交 $SourceCommit 重新通过核心、Windows 原生回归和完整应用构建后生成。校验和见 SHA256SUMS.txt，包内来源见 BUILD-INFO.json。GitHub 的 Source code 下载项是源码。

未配置代码签名；硬件相关能力仍需对应设备实机确认。不自动更新或替换本机 Axiom；更新前先安全停止后台，用户配置保留。
"@
$notesFile = Join-Path ([IO.Path]::GetTempPath()) ("axiom-release-" + [Guid]::NewGuid().ToString('N') + '.md')
try {
    [IO.File]::WriteAllText($notesFile, $notes, [Text.UTF8Encoding]::new($false))
    # Enumerating releases distinguishes an absent tag from authorization/network failures; do not treat any CLI failure as absence.
    $raw = & gh api --paginate --slurp "repos/$repo/releases?per_page=100"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing GitHub releases.' }
    $existing = @($raw | ConvertFrom-Json | ForEach-Object { $_ } | Where-Object tag_name -CEQ $tag)
    if ($existing.Count -gt 1) { throw 'Multiple releases unexpectedly use the version tag.' }
    if ($existing.Count -eq 1) {
        $release = $existing[0]
        if (!$release.draft) { throw 'This version has already been published; create a new version tag instead of overwriting it.' }
        if (!$release.body.Contains($versionMarker) -or !$release.body.Contains($sourceMarker)) { throw 'Existing draft does not belong to this verified version and commit.' }
        if (@($release.assets | Where-Object name -CNotIn $names).Count -ne 0) { throw 'Existing draft contains unexpected assets; it will not be modified.' }
    } else {
        & gh release create $tag --repo $repo --verify-tag --draft --prerelease --target $SourceCommit --title "Axiom $Version" --notes-file $notesFile
        if ($LASTEXITCODE -ne 0) { throw 'Cannot create the preview draft.' }
    }
    $assets = @($names | ForEach-Object { Join-Path $directory $_ })
    & gh release upload $tag @assets --repo $repo --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed; the release remains a draft and may be retried with fresh verified packages.' }
    $raw = & gh release view $tag --repo $repo --json assets,isDraft,body
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify uploaded draft assets.' }
    $uploaded = $raw | ConvertFrom-Json
    if (!$uploaded.isDraft -or @($uploaded.assets).Count -ne 4 -or @($uploaded.assets | Where-Object name -CNotIn $names).Count -ne 0) { throw 'The draft must contain exactly four expected assets before publishing.' }
    foreach ($asset in $uploaded.assets) {
        if ($asset.size -ne (Get-Item -LiteralPath (Join-Path $directory $asset.name)).Length) { throw 'Uploaded asset size differs from the checked package.' }
    }
    & gh release edit $tag --repo $repo --draft=false --prerelease --latest=false --notes-file $notesFile
    if ($LASTEXITCODE -ne 0) { throw 'Final preview publication failed; the draft may be retried.' }
} finally { Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue }

<#
.SYNOPSIS
    Writes the release notes used by the rolling GBTGAME.ME build.

.PARAMETER OutputPath
    Destination markdown file. Relative paths are resolved against the repository root.

.PARAMETER UpstreamSha
    Short SHA of the upstream commit that was merged into this build.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent directory of this script.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = 'release-notes.md',
    [string]$UpstreamSha = '',
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

$root = $RepoRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}

$brandingPath = Join-Path $root 'branding/branding.json'
$branding = ConvertFrom-Json ([System.IO.File]::ReadAllText($brandingPath, [System.Text.Encoding]::UTF8))

if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $root $OutputPath
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# $($branding.product)")
$lines.Add('')
$lines.Add("- **作者**：$($branding.author)（$($branding.note)）")
$lines.Add("- **论坛**：[$($branding.forum)]($($branding.forumUrl))")
$lines.Add("- **TG 群组**：[$($branding.telegram)]($($branding.telegramUrl))")
$lines.Add('')

if (-not [string]::IsNullOrWhiteSpace($UpstreamSha)) {
    $lines.Add("- 上游提交：[$UpstreamSha](https://github.com/k1tbyte/Wand-Enhancer/commit/$UpstreamSha)")
}
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA) -and $env:GITHUB_SHA.Length -ge 7) {
    $lines.Add("- 本仓库提交：$($env:GITHUB_SHA.Substring(0, 7))")
}
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
    $lines.Add("- 源码：https://github.com/$($env:GITHUB_REPOSITORY)")
}
$lines.Add("- 构建时间：$([DateTime]::UtcNow.AddHours(8).ToString('yyyy-MM-dd HH:mm'))（UTC+8）")
$lines.Add('')
$lines.Add('同步自 [k1tbyte/Wand-Enhancer](https://github.com/k1tbyte/Wand-Enhancer)，自动合并上游更新并重新应用本分支的品牌信息。')
$lines.Add('')
$lines.Add('> 本构建未签名，Windows SmartScreen 可能提示“未知发布者”，选择“更多信息 → 仍要运行”即可。')
$lines.Add('包含支持 CLI 的 `WandEnhancer.exe` 与内置同版 CLI 的 `WandSetup.exe`。')
$lines.Add('把原版 `Wand-12.68.0.exe` 放到 `WandSetup.exe` 旁边，双击安装助手并点击“安装”。')
$lines.Add('备份、卸载清理、静默安装、应用修改和启动自动执行。自定义位置或系统级旧版需先手动卸载。')
$lines.Add('详见 [CLI 与安装说明](https://github.com/nodesire7/Wand-Enhancer/blob/master/Fork/README.md)。')
$lines.Add('> 下载后可用 `SHA256SUMS.txt` 校验文件完整性：`certutil -hashfile WandSetup.exe SHA256`。')

$text = ($lines -join "`n") + "`n"
$encoding = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutputPath, $text, $encoding)

Write-Host "Release notes written to $OutputPath"

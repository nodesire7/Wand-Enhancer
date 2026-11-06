param([switch]$Check)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root 'Directory.Build.targets'
$text = [IO.File]::ReadAllText($path)
$hook = '<Import Project="$(MSBuildThisFileDirectory)Fork\Fork.targets" Condition="Exists(''$(MSBuildThisFileDirectory)Fork\Fork.targets'')" />'
if (-not $text.Contains($hook)) {
    if ($Check) { throw 'Fork CLI hook is missing. Run scripts/reapply-fork.ps1.' }
    if (-not $text.Contains('</Project>')) { throw 'Cannot locate Directory.Build.targets import anchor.' }
    $text = $text.Replace('</Project>', "  $hook`r`n</Project>")
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
$readmePath = Join-Path $root 'README.md'
$readme = [IO.File]::ReadAllText($readmePath)
$notice = '本 GBTGAME 分支提供支持 CLI 的 EXE 与一键安装助手，详见 [中文使用与构建说明](Fork/README.md)。'
if (-not $readme.Contains($notice)) {
    if ($Check) { throw 'Fork documentation link is missing.' }
    $readme = $notice + "`r`n`r`n" + $readme
    [IO.File]::WriteAllText($readmePath, $readme, [Text.UTF8Encoding]::new($false))
}
Write-Host 'Fork CLI hook and documentation link are present.'

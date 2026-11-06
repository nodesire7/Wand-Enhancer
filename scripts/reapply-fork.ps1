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
$watcherPath = Join-Path $root 'WandEnhancer\Core\Services\WatcherAutostart.cs'
$watcher = [IO.File]::ReadAllText($watcherPath)
$anchor = $watcher.IndexOf('private static void StartDetached(')
if ($anchor -lt 0) { throw 'Cannot locate the detached watcher startup hook.' }
$tail = $watcher.Substring($anchor)
if ($tail.Contains('UseShellExecute = false,')) {
    if ($Check) { throw 'Watcher startup must detach from redirected CLI pipes.' }
    $watcher = $watcher.Substring(0, $anchor) + $tail.Replace('UseShellExecute = false,', 'UseShellExecute = true,')
    $bytes = [IO.File]::ReadAllBytes($watcherPath)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191
    [IO.File]::WriteAllText($watcherPath, $watcher, [Text.UTF8Encoding]::new($hasBom))
} elseif (-not $tail.Contains('UseShellExecute = true,')) { throw 'Unrecognized watcher startup implementation; review CLI pipe isolation.' }
Write-Host 'Fork CLI, documentation and detached watcher hooks are present.'

param(
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [string]$WandInstallerPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$enhancer = Join-Path $repoRoot "WandEnhancer\bin\$Configuration\WandEnhancer.exe"
if (-not (Test-Path -LiteralPath $enhancer)) { throw 'Build WandEnhancer with build.ps1 first.' }
$package = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Fork\Installer\package.json') | ConvertFrom-Json
if ($WandInstallerPath) {
    $WandInstallerPath = (Resolve-Path -LiteralPath $WandInstallerPath).Path
    if ((Get-FileHash -LiteralPath $WandInstallerPath -Algorithm SHA256).Hash -ne $package.sha256) {
        throw 'Wand installer SHA256 does not match Fork/Installer/package.json.'
    }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsPath) { throw 'Visual Studio MSBuild is required.' }
$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
$installerDir = Join-Path $repoRoot 'Fork\Installer'
$generatedDir = Join-Path $installerDir "obj\$Configuration"
[IO.Directory]::CreateDirectory($generatedDir) | Out-Null
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($enhancer).FileVersion
$attributes = @"
using System.Reflection;
[assembly: AssemblyTitle("GBTGAME Wand 安装助手")]
[assembly: AssemblyProduct("GBTGAME Wand 安装助手")]
[assembly: AssemblyCompany("GBTGAME.ME")]
[assembly: AssemblyVersion("$version")]
[assembly: AssemblyFileVersion("$version")]
"@
[IO.File]::WriteAllText((Join-Path $generatedDir 'AssemblyInfo.cs'), $attributes, [Text.UTF8Encoding]::new($true))
$buildArgs = @((Join-Path $installerDir 'WandInstaller.csproj'), '/t:Rebuild', '/nologo', '/v:minimal', "/p:Configuration=$Configuration")
if ($WandInstallerPath) { $buildArgs += "/p:WandInstallerPath=$WandInstallerPath" }
& $msbuild @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$output = Join-Path $repoRoot "dist\$Configuration"
[IO.Directory]::CreateDirectory($output) | Out-Null
Copy-Item -LiteralPath $enhancer -Destination (Join-Path $output 'WandEnhancer.exe') -Force
Copy-Item -LiteralPath (Join-Path $installerDir "bin\$Configuration\WandSetup.exe") -Destination (Join-Path $output 'WandSetup.exe') -Force
$instruction = if ($WandInstallerPath) {
    '双击 WandSetup.exe，点击[安装]。原版 Wand 安装包和本次构建的 CLI 均已内置。'
} else {
    "把原版 $($package.fileName) 放在 WandSetup.exe 旁边，双击 WandSetup.exe，点击[安装]。CLI 已内置。"
}
$instruction += "`r`n安装前关闭 Wand 并保存进度。安装助手会备份 Roaming 配置和相关注册表，卸载清理当前用户的默认安装，再安装、应用修改并启动。`r`n备份与日志：%LOCALAPPDATA%\GBTGAME\WandSetup`r`n自定义位置和系统级旧版需先手动卸载。`r`nCLI 说明见 Fork/README.md。"
[IO.File]::WriteAllText((Join-Path $output '使用说明.txt'), $instruction, [Text.UTF8Encoding]::new($true))
$hashes = @('WandSetup.exe', 'WandEnhancer.exe') | ForEach-Object {
    $fileHash = (Get-FileHash -LiteralPath (Join-Path $output $_) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$fileHash  $_"
}
$hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
$hashes[1] | Set-Content -LiteralPath (Join-Path $output 'WandEnhancer.exe.sha256') -Encoding ascii
Write-Host "Distribution ready: $output"

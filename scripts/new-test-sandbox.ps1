param(
    [string]$OutputDirectory,
    [string]$SetupPath,
    [string]$WandInstallerPath,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $SetupPath) { $SetupPath = Join-Path $repo 'dist\Release\WandSetup.exe' }
$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
$assembly = [Reflection.Assembly]::LoadFile($SetupPath)
if ($null -eq $assembly.GetManifestResourceInfo('WandInstallerPayload') -and -not $WandInstallerPath) {
    throw 'Use the full installer build or provide -WandInstallerPath for the lightweight build.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('.tmp\sandbox-' + [guid]::NewGuid().ToString('N')) }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$inputDirectory = Join-Path $OutputDirectory 'input'
$resultDirectory = Join-Path $OutputDirectory 'results'
foreach ($directory in @($inputDirectory, $resultDirectory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
Copy-Item -LiteralPath $SetupPath -Destination (Join-Path $inputDirectory 'WandSetup.exe') -Force
Copy-Item -LiteralPath (Join-Path $repo 'dist\Release\WandEnhancer.exe') -Destination (Join-Path $inputDirectory 'WandEnhancer.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sandbox-test.ps1') -Destination (Join-Path $inputDirectory 'sandbox-test.ps1') -Force
if ($WandInstallerPath) {
    $package = Get-Content -Raw -LiteralPath (Join-Path $repo 'Fork\Installer\package.json') | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $WandInstallerPath -Algorithm SHA256).Hash -ne $package.sha256) { throw 'Original installer SHA256 mismatch.' }
    Copy-Item -LiteralPath $WandInstallerPath -Destination (Join-Path $inputDirectory $package.fileName) -Force
}
$inputXml = [Security.SecurityElement]::Escape($inputDirectory)
$resultXml = [Security.SecurityElement]::Escape($resultDirectory)
$config = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\WandTest\Input</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$resultXml</HostFolder><SandboxFolder>C:\WandTest\Output</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WandTest\Input\sandbox-test.ps1</Command></LogonCommand>
</Configuration>
"@
$configPath = Join-Path $OutputDirectory 'Wand-Acceptance.wsb'
[IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($true))
if ($Launch) {
    $sandbox = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
    if (-not (Test-Path -LiteralPath $sandbox)) { throw "Windows Sandbox is not installed. Prepared test: $configPath" }
    Start-Process -FilePath $sandbox -ArgumentList ('"' + $configPath + '"') -WindowStyle Hidden
}
Write-Host "Sandbox acceptance file: $configPath"
Write-Host "Results will be saved to: $resultDirectory"

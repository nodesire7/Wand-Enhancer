param(
    [Parameter(Mandatory = $true)][string]$BundleDirectory,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -BundleDirectory $BundleDirectory -OutputPath $OutputPath
    if ($LASTEXITCODE -ne 0) { throw 'Real package test failed.' }
    exit 0
}
$repo = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $BundleDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $source 'Wand.exe')) -or -not (Test-Path -LiteralPath (Join-Path $source 'resources\app.asar'))) { throw 'Expected an extracted pristine app folder.' }
if (-not $OutputPath) { $OutputPath = Join-Path $repo '.tmp\real-package-results.json' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('wand-real-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'WandEnhancer\bin\Release\WandEnhancer.exe'))
$results = [Collections.Generic.List[object]]::new()
function Hashes([string]$directory) {
    $values = @{}
    Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object { $values[$_.FullName.Substring($directory.Length)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    return $values
}
try {
    foreach ($strategy in @('Static', 'Supervised')) {
        $fixture = Join-Path $scratch "$strategy\app-12.68.0"
        [IO.Directory]::CreateDirectory($fixture) | Out-Null
        Get-ChildItem -LiteralPath $source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $fixture -Recurse -Force }
        $before = Hashes $fixture
        $install = [Activator]::CreateInstance($assembly.GetType('WandEnhancer.Models.WeModConfig'))
        $install.RootDirectory = $fixture
        $install.ExecutableName = 'Wand.exe'
        # Keep name-based process stopping and watcher cleanup away from the real Wand.
        $install.BrandName = 'fixture-' + [guid]::NewGuid().ToString('N')
        $config = [Activator]::CreateInstance($assembly.GetType('WandEnhancer.Models.PatchConfig'))
        $patchType = $assembly.GetType('WandEnhancer.Models.EPatchType')
        $config.PatchTypes = [Activator]::CreateInstance([Collections.Generic.HashSet``1].MakeGenericType($patchType))
        foreach ($name in @('ActivatePro', 'DisableUpdates', 'DevToolsOnF12', 'RemoteWebPanelPreview')) { [void]$config.PatchTypes.Add([Enum]::Parse($patchType, $name)) }
        $config.Strategy = [Enum]::Parse($assembly.GetType('WandEnhancer.Models.EPatchStrategy'), $strategy)
        $customScript = Join-Path $scratch ("user-$strategy.js")
        [IO.File]::WriteAllText($customScript, 'globalThis.__wandSetupOptionsFixture = true;')
        $config.CustomScriptPaths.Add($customScript)
        $loggerType = [Action``2].MakeGenericType([string], $assembly.GetType('WandEnhancer.Core.ELogType'))
        $logger = { param($message, $level) Write-Host "$level $message" } -as $loggerType
        $type = $assembly.GetType('WandEnhancer.Core.Enhancer')
        $engine = [Activator]::CreateInstance($type, @($install, $logger, $config))
        $bundleCount = 0
        try {
            $engine.Patch()
            if (-not $type.GetMethod('IsPatched').Invoke($null, @([string]$fixture))) { throw 'Patch state not confirmed.' }
            $unpacked = Join-Path $fixture 'resources\app.asar.unpacked'
            $bundles = @(Get-ChildItem -LiteralPath $unpacked -Filter '*.js' -File)
            foreach ($bundle in $bundles) {
                & node --check $bundle.FullName
                if ($LASTEXITCODE -ne 0) { throw "JavaScript syntax failed: $($bundle.Name)" }
            }
            $bundleCount = $bundles.Count
            if (-not (Test-Path -LiteralPath (Join-Path $unpacked 'remote-panel\bridge.cjs'))) { throw 'Remote bridge was not deployed.' }
            $injectedScript = Join-Path $unpacked ("remote-panel\renderer-scripts\user-$strategy.js")
            if (-not (Test-Path -LiteralPath $injectedScript) -or [IO.File]::ReadAllText($injectedScript) -ne 'globalThis.__wandSetupOptionsFixture = true;') { throw 'Selected custom script was not injected.' }
            & node --check $injectedScript
            if ($LASTEXITCODE -ne 0) { throw 'Custom script syntax failed.' }
            & node --check (Join-Path $unpacked 'remote-panel\bridge.cjs')
            if ($LASTEXITCODE -ne 0) { throw 'Remote bridge syntax failed.' }
        } finally { $engine.Restore() }
        $after = Hashes $fixture
        if ($before.Count -ne $after.Count) { throw 'Restore changed the original file count.' }
        foreach ($name in $before.Keys) { if ($after[$name] -ne $before[$name]) { throw "Restore SHA256 mismatch: $name" } }
        $results.Add(@{ strategy = $strategy; filesRestored = $before.Count; javascriptBundles = $bundleCount; customScript = $true; patches = @('pro', 'disable-updates', 'devtools', 'remote-panel'); success = $true })
        Write-Host "$strategy passed: four patch options, JS syntax, remote bridge and $($before.Count) restored file hashes."
    }
    @{ success = $true; results = @($results); sourceVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'Wand.exe')).ProductVersion } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
} finally {
    $absolute = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($absolute.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $absolute -Leaf).StartsWith('wand-real-test-')) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}

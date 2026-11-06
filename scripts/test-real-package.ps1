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
function Verify-Content($install, $config, [bool]$expected, [string]$label) {
    $verifier = $assembly.GetType('WandEnhancer.Core.Patching.Content.PatchVerification')
    $result = $verifier.GetMethod('Verify').Invoke($null, @($install, $config))
    if ($result.Verified -ne $expected) { throw "$label expected $expected`: $($result.Message)" }
    Write-Host "Verification regression passed: $label"
}
function Verify-Cli([string]$fixture, [string]$configPath, [int]$expectedCode) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($assembly.Location)
    $process.StartInfo.Arguments = '--cli verify --path "' + $fixture + '" --config "' + $configPath + '"'
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.CreateNoWindow = $true
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Content verification CLI timed out.' }
        if (-not $output.Wait(5000)) { throw 'Content verification CLI output did not close.' }
        $reply = $output.Result | ConvertFrom-Json
        if ($process.ExitCode -ne $expectedCode -or $reply.data.verified -ne ($expectedCode -eq 0)) { throw "Unexpected content verification CLI result: $($output.Result)" }
        Write-Host "Actual CLI verification returned expected code $expectedCode."
    } finally { $process.Dispose() }
}
function Test-ArchiveTamper([string]$archive, [string]$entryName, $install, $config) {
    $disk = $assembly.GetType('AsarSharp.AsarFileSystem.Disk')
    $fs = $disk.GetMethod('ReadFilesystemSync').Invoke($null, @($archive))
    $entry = $fs.GetFile($entryName, $false, 0)
    if ($entry.Unpacked) { throw 'Regression requires an actual packed entry.' }
    $offset = 8L + $fs.GetHeaderSize() + [long]$entry.Offset
    $stream = [IO.File]::Open($archive, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
    try {
        $stream.Position = $offset
        $original = $stream.ReadByte()
        $stream.Position = $offset
        $stream.WriteByte($original -bxor 1)
    } finally { $stream.Dispose() }
    try { Verify-Content $install $config $false "tampered packed $entryName while staging stays correct" }
    finally {
        $stream = [IO.File]::OpenWrite($archive)
        try { $stream.Position = $offset; $stream.WriteByte($original) } finally { $stream.Dispose() }
    }
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
        $configFile = Join-Path $scratch "$strategy-config.json"
        [IO.File]::WriteAllText($configFile, (@{ Strategy = [int]$config.Strategy; PatchTypes = @($config.PatchTypes | ForEach-Object { [int]$_ }); CustomScriptPaths = @($customScript); AutoApplyAfterUpdate = $false } | ConvertTo-Json))
        $loggerType = [Action``2].MakeGenericType([string], $assembly.GetType('WandEnhancer.Core.ELogType'))
        $logger = { param($message, $level) Write-Host "$level $message" } -as $loggerType
        $type = $assembly.GetType('WandEnhancer.Core.Enhancer')
        $engine = [Activator]::CreateInstance($type, @($install, $logger, $config))
        $bundleCount = 0
        try {
            $engine.Patch()
            if (-not $type.GetMethod('IsPatched').Invoke($null, @([string]$fixture))) { throw 'Patch state not confirmed.' }
            Verify-Content $install $config $true 'all selected options and launch target'
            Verify-Cli $fixture $configFile 0
            $config.PatchTypes.Remove([Enum]::Parse($patchType, 'DevToolsOnF12')) | Out-Null
            Verify-Content $install $config $false 'different requested selection'
            [void]$config.PatchTypes.Add([Enum]::Parse($patchType, 'DevToolsOnF12'))
            $archive = Join-Path $fixture 'resources\app.asar'
            $patchedArchive = Join-Path $scratch "$strategy-patched.asar"
            Copy-Item -LiteralPath $archive -Destination $patchedArchive
            Copy-Item -LiteralPath ($archive + '.backup') -Destination $archive -Force
            try {
                Verify-Content $install $config $false 'pristine archive with correct patched staging and complete backups'
                Verify-Cli $fixture $configFile 5
            }
            finally { Copy-Item -LiteralPath $patchedArchive -Destination $archive -Force }
            Test-ArchiveTamper $archive 'index.js' $install $config
            Test-ArchiveTamper $archive 'remote-panel/bridge.cjs' $install $config
            Test-ArchiveTamper $archive ("remote-panel/renderer-scripts/user-$strategy.js") $install $config
            $auxiliary = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'resources\app.asar.unpacked\static\unpacked\auxiliary') -Filter '*AuxiliaryService.exe')[0]
            $patchedAuxiliary = Join-Path $scratch "$strategy-auxiliary.exe"
            Copy-Item -LiteralPath $auxiliary.FullName -Destination $patchedAuxiliary
            Copy-Item -LiteralPath (Join-Path $fixture ('resources\app.asar.unpacked.backup\static\unpacked\auxiliary\' + $auxiliary.Name)) -Destination $auxiliary.FullName -Force
            try { Verify-Content $install $config $false 'unmodified auxiliary service' }
            finally { Copy-Item -LiteralPath $patchedAuxiliary -Destination $auxiliary.FullName -Force }
            if ($strategy -eq 'Static') {
                $fuseType = $assembly.GetType('WandEnhancer.Core.Patching.Shared.ElectronFuseWire')
                $fuse = $fuseType.GetMethod('FindStateFileOffset').Invoke($null, @([string](Join-Path $fixture 'Wand.exe')))
                $stream = [IO.File]::OpenWrite((Join-Path $fixture 'Wand.exe'))
                try { $stream.Position = $fuse; $stream.WriteByte([byte][char]'1') } finally { $stream.Dispose() }
                try { Verify-Content $install $config $false 'static executable enablement removed' }
                finally {
                    $stream = [IO.File]::OpenWrite((Join-Path $fixture 'Wand.exe'))
                    try { $stream.Position = $fuse; $stream.WriteByte([byte][char]'r') } finally { $stream.Dispose() }
                }
            } else {
                $launcher = Join-Path (Split-Path $fixture -Parent) 'Wand.exe'
                $launcherCopy = Join-Path $scratch 'supervised-launcher.exe'
                Copy-Item -LiteralPath $launcher -Destination $launcherCopy
                [IO.File]::WriteAllText($launcher, 'replaced launcher')
                try { Verify-Content $install $config $false 'supervised launcher replaced' }
                finally { Copy-Item -LiteralPath $launcherCopy -Destination $launcher -Force }
            }
            Verify-Content $install $config $true 'restored exact runtime content after corruption checks'
            $unpacked = Join-Path $fixture 'resources\app.asar.unpacked'
            & node (Join-Path $PSScriptRoot 'tests\pro-startup-real.cjs') $unpacked
            if ($LASTEXITCODE -ne 0) { throw 'Actual bundle cached account startup failed.' }
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
        $results.Add(@{ strategy = $strategy; filesRestored = $before.Count; javascriptBundles = $bundleCount; customScript = $true; contentCorruptionChecks = 7; actualClientRuntimeTested = $false; patches = @('pro', 'disable-updates', 'devtools', 'remote-panel'); success = $true })
        Write-Host "$strategy passed: four patch options, JS syntax, remote bridge and $($before.Count) restored file hashes."
    }
    @{ success = $true; results = @($results); sourceVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'Wand.exe')).ProductVersion } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
} finally {
    $absolute = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($absolute.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $absolute -Leaf).StartsWith('wand-real-test-')) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}

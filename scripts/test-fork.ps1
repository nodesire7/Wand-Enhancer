param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "WandEnhancer\bin\$Configuration\WandEnhancer.exe"
$installer = Join-Path $root "Fork\Installer\bin\$Configuration\WandSetup.exe"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('wand-cli-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Invoke-Cli([string[]]$CliArgs, [int]$Code = 0) {
    # Every test passes --path explicitly when it could resolve an installation.
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($exe)
    $process.StartInfo.Arguments = '--cli ' + [string]::Join(' ', @($CliArgs | ForEach-Object {
        '"' + [regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    }))
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) { $process.Kill(); throw 'CLI timed out or opened a UI.' }
        if (-not $output.Wait(5000)) { throw 'CLI output pipe remained open after exit.' }
        Assert ($process.ExitCode -eq $Code) "CLI $CliArgs expected $Code, got $($process.ExitCode): $($output.Result)"
        $reply = $output.Result | ConvertFrom-Json
        Assert ($reply.schemaVersion -eq 1) 'CLI schema'
        Assert ($reply.success -eq ($Code -eq 0)) 'CLI success flag'
        return $reply
    } finally { $process.Dispose() }
}
try {
    Assert ((Invoke-Cli @('version')).data.cliVersion -eq 1) 'CLI capability'
    [void](Invoke-Cli @('help'))
    [void](Invoke-Cli @('unknown') 2)
    [void](Invoke-Cli @('patch', '--path') 2)
    [void](Invoke-Cli @('patch', '--path', $scratch, '--patches', 'invalid') 2)
    [void](Invoke-Cli @('patch', '--path', $scratch, '--auto-apply') 2)
    [void](Invoke-Cli @('status', '--patches', 'pro', '--path', $scratch) 2)
    [void](Invoke-Cli @('status', '--path', $scratch, '--path', $scratch) 2)
    [void](Invoke-Cli @('status', '--path', $scratch) 3)

    $install = Join-Path $scratch '中文 路径\app-12.68.0'
    $resources = Join-Path $install 'resources'
    [IO.Directory]::CreateDirectory($resources) | Out-Null
    $source = Join-Path $scratch 'Fixture.cs'
    [IO.File]::WriteAllText($source, 'using System.Reflection; [assembly:AssemblyVersion("12.68.0.0")] [assembly:AssemblyFileVersion("12.68.0.0")] class Fixture { static void Main() { System.Threading.Thread.Sleep(30000); } }')
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /target:winexe "/out:$install\Wand.exe" $source
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    [IO.File]::WriteAllText((Join-Path $resources 'app.asar'), 'fixture archive')
    $status = Invoke-Cli @('status', '--path', $install, '--expected-version', '12.68.0')
    Assert ($status.data.patched -eq $false) 'Fresh fixture status'
    Assert ($status.data.path -eq $install) 'Unicode and spaces preserved'
    [void](Invoke-Cli @('status', '--path', (Join-Path $install 'Wand.exe')))
    [void](Invoke-Cli @('status', '--path', (Split-Path $install -Parent)))
    [void](Invoke-Cli @('patch', '--path', $install, '--expected-version', '99.0.0') 4)
    Assert ([IO.File]::ReadAllText((Join-Path $resources 'app.asar')) -eq 'fixture archive') 'Mismatch must not modify files'
    Assert (-not (Test-Path (Join-Path $resources '.incomplete-patch'))) 'Mismatch must not start patch pipeline'
    [IO.File]::WriteAllText((Join-Path $resources 'app.asar.backup'), 'backup')
    [IO.Directory]::CreateDirectory((Join-Path $resources 'app.asar.unpacked.backup')) | Out-Null
    Assert ((Invoke-Cli @('status', '--path', $install)).data.patched -eq $true) 'Patched fixture status'
    [IO.File]::WriteAllText((Join-Path $resources '.incomplete-patch'), '')
    Assert ((Invoke-Cli @('status', '--path', $install)).data.patched -eq $false) 'Incomplete fixture status'

    try {
        [void](Invoke-Cli @('launch', '--path', $install, '--strategy', 'static'))
        # The fake client outlives the CLI. Its output pipe must already be closed.
    } finally {
        foreach ($child in [Diagnostics.Process]::GetProcessesByName('Wand')) {
            try {
                if ($child.MainModule.FileName -eq (Join-Path $install 'Wand.exe')) {
                    $child.Kill()
                    [void]$child.WaitForExit(5000)
                }
            } catch [System.ComponentModel.Win32Exception] { } finally { $child.Dispose() }
        }
    }

    $assembly = [Reflection.Assembly]::LoadFile($installer)
    $engine = $assembly.GetType('WandEnhancer.Fork.Installer.InstallEngine', $true)
    $flags = [Reflection.BindingFlags]'NonPublic,Static'
    $under = $engine.GetMethod('IsUnder', $flags)
    Assert ($under.Invoke($null, @('C:\Apps\Wand\Wand.exe', 'C:\Apps\Wand'))) 'Child containment'
    Assert (-not $under.Invoke($null, @('C:\Apps\WandOther\Wand.exe', 'C:\Apps\Wand'))) 'Sibling prefix must be rejected'
    Assert (-not $under.Invoke($null, @('C:\Apps\Wand\..\Other\Wand.exe', 'C:\Apps\Wand'))) 'Traversal must be rejected'
    $guard = $engine.GetMethod('GuardTree', $flags)
    $guard.Invoke($null, [object[]]@([string](Join-Path $scratch 'Wand'), [string]$scratch, 'Wand'))
    $rejected = $false
    try { $guard.Invoke($null, @($scratch, $scratch, 'Wand')) } catch { $rejected = $true }
    Assert $rejected 'Deletion guard must reject parent directory'
    $junction = Join-Path $scratch 'Wand'
    New-Item -ItemType Junction -Path $junction -Target $resources | Out-Null
    try {
        $rejected = $false
        try { $guard.Invoke($null, @($junction, $scratch, 'Wand')) } catch { $rejected = $true }
        Assert $rejected 'Deletion guard must reject junction'
    } finally { [IO.Directory]::Delete($junction) }
    $embedded = $assembly.GetManifestResourceStream('WandEnhancerPayload')
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $embeddedHash = [BitConverter]::ToString($sha.ComputeHash($embedded)).Replace('-', '') }
        finally { $sha.Dispose() }
    } finally { $embedded.Dispose() }
    Assert ($embeddedHash -eq (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash) 'Installer must embed the exact CLI from this build'
    & (Join-Path $PSScriptRoot 'reapply-fork.ps1') -Check
    if ($LASTEXITCODE -ne 0) { throw 'Fork hook validation failed.' }
    Write-Host 'Fork tests passed: CLI protocol, validation, Unicode paths, version guard, patch state, detached launch, deletion boundaries, junction refusal, embedded CLI identity.'
} finally {
    $absolute = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($absolute.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $absolute -Leaf).StartsWith('wand-cli-test-')) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}

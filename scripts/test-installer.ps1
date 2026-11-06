param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root '.tmp\installer-tests' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$assembly = Join-Path $root 'Fork\Installer\bin\Release\WandSetup.exe'
Copy-Item -LiteralPath $assembly -Destination (Join-Path $OutputDirectory 'WandSetup.exe') -Force
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object {
    $path = Join-Path $framework $_
    if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path $framework "WPF\$_" }
    "/reference:$path"
}
$testExe = Join-Path $OutputDirectory 'WandInstaller.Tests.exe'
& $csc /nologo /noconfig /nostdlib+ /target:exe "/out:$testExe" "/reference:$assembly" @references (Join-Path $PSScriptRoot 'tests\installer.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer test compilation failed.' }
& $testExe $OutputDirectory (Join-Path $root 'WandEnhancer\bin\Release\WandEnhancer.exe')
if ($LASTEXITCODE -ne 0) { throw 'Installer tests failed.' }

function Invoke-Check([string]$ExePath, [int]$ExpectedCode) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($ExePath, '--check')
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Installer check timed out.' }
        if (-not $output.Wait(5000)) { throw 'Installer check output remained open.' }
        if ($process.ExitCode -ne $ExpectedCode) { throw "Unexpected installer code $($process.ExitCode): $($output.Result)" }
        return ($output.Result | ConvertFrom-Json)
    } finally { $process.Dispose() }
}
$loaded = [Reflection.Assembly]::LoadFile($assembly)
if ($null -eq $loaded.GetManifestResourceInfo('WandInstallerPayload')) {
    $caseDirectory = Join-Path $OutputDirectory ('payload-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
    $caseExe = Join-Path $caseDirectory 'WandSetup.exe'
    Copy-Item -LiteralPath $assembly -Destination $caseExe
    $missing = Invoke-Check $caseExe 1
    if ($missing.success) { throw 'Missing installer accepted.' }
    $package = Get-Content -Raw -LiteralPath (Join-Path $root 'Fork\Installer\package.json') | ConvertFrom-Json
    [IO.File]::WriteAllText((Join-Path $caseDirectory $package.fileName), 'invalid payload')
    $corrupt = Invoke-Check $caseExe 1
    if ($corrupt.success -or $corrupt.message -notmatch 'SHA256') { throw 'Corrupted installer was not rejected by the integrity gate.' }
    Write-Host 'PASS lightweight installer missing and corrupted payload checks'
}
$mutex = [Threading.Mutex]::new($true, 'Local\GBTGAME.WandSetup')
try { $busy = Invoke-Check $assembly 6; if ($busy.success) { throw 'Busy installer accepted a second operation.' } }
finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
Write-Host 'PASS installer process mutex and JSON error responses'
$proxy = [Diagnostics.Process]::new()
$proxyDirectory = Join-Path $OutputDirectory ('standalone-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($proxyDirectory) | Out-Null
$proxyExe = Join-Path $proxyDirectory 'WandSetup.exe'
Copy-Item -LiteralPath $assembly -Destination $proxyExe
$proxy.StartInfo = [Diagnostics.ProcessStartInfo]::new($proxyExe, '--cli version')
$proxy.StartInfo.WorkingDirectory = $proxyDirectory
$proxy.StartInfo.UseShellExecute = $false
$proxy.StartInfo.CreateNoWindow = $true
$proxy.StartInfo.RedirectStandardOutput = $true
$proxy.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    [void]$proxy.Start()
    $output = $proxy.StandardOutput.ReadToEndAsync()
    if (-not $proxy.WaitForExit(20000)) { $proxy.Kill(); throw 'Embedded CLI proxy timed out.' }
    if (-not $output.Wait(5000)) { throw 'Embedded CLI output remained open.' }
    $reply = $output.Result | ConvertFrom-Json
    if ($proxy.ExitCode -ne 0 -or -not $reply.success -or $reply.data.cliVersion -ne 1) { throw 'Single EXE CLI proxy failed.' }
    Write-Host 'PASS standalone setup calls its embedded CLI without an adjacent enhancer EXE'
} finally { $proxy.Dispose() }
Write-Host "Installer test report and state captures: $OutputDirectory"

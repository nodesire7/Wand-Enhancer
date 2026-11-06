param([string]$OutputDirectory = 'C:\WandTest\Output')
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $PSScriptRoot -ne 'C:\WandTest\Input') {
    throw 'This destructive acceptance test is restricted to the generated Windows Sandbox environment.'
}
$checks = [Collections.Generic.List[string]]::new()
$success = $false
$failure = $null
$log = Join-Path $OutputDirectory 'acceptance.log'
Start-Transcript -Path $log -Force | Out-Null
function Assert($condition, [string]$message) { if (-not $condition) { throw $message } }
function Invoke-TestExe([string]$Name, [string[]]$Arguments) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSScriptRoot $Name))
    $process.StartInfo.Arguments = [string]::Join(' ', @($Arguments | ForEach-Object { '"' + [regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }))
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorText = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(1200000)) { $process.Kill(); throw "$Name timed out" }
        if (-not $output.Wait(10000)) { throw "$Name held its output pipe open" }
        Assert ($process.ExitCode -eq 0) "$Name failed: $($output.Result) $($errorText.Result)"
        $reply = $output.Result | ConvertFrom-Json
        Assert $reply.success "$Name reported failure"
        return $reply
    } finally { $process.Dispose() }
}
try {
    [void](Invoke-TestExe 'WandSetup.exe' @('--check'))
    $checks.Add('preflight')
    [void](Invoke-TestExe 'WandSetup.exe' @('--install', '--no-launch'))
    $root = Join-Path $env:LOCALAPPDATA 'Wand'
    $state = Invoke-TestExe 'WandSetup.exe' @('--cli', 'status', '--path', $root, '--expected-version', '12.68.0')
    Assert $state.data.patched 'Fresh install is not patched'
    Assert (@(Get-Process Wand -ErrorAction SilentlyContinue).Count -eq 0) 'Wand started before the explicitly requested launch'
    $checks.Add('fresh install, static patch and no premature first launch')

    $roaming = Join-Path $env:APPDATA 'Wand'
    [IO.Directory]::CreateDirectory($roaming) | Out-Null
    [IO.File]::WriteAllText((Join-Path $roaming 'sandbox-old-settings.txt'), 'preserve-me')
    New-Item 'HKCU:\Software\Wand' -Force | Out-Null
    Set-ItemProperty 'HKCU:\Software\Wand' -Name SandboxOldValue -Value 'preserve-me'
    $keepFile = Join-Path $root 'launcher-keep.txt'
    [IO.File]::WriteAllText($keepFile, 'keep-existing-install')
    $backupRoot = Join-Path $env:LOCALAPPDATA 'GBTGAME\WandSetup\backups'
    $backupCount = @(Get-ChildItem -LiteralPath $backupRoot -Directory).Count
    [void](Invoke-TestExe 'WandSetup.exe' @('--start', '--no-launch'))
    Assert ((Get-Content -Raw -LiteralPath $keepFile) -eq 'keep-existing-install') 'Launcher replaced the existing installation'
    Assert (Test-Path -LiteralPath (Join-Path $roaming 'sandbox-old-settings.txt')) 'Launcher removed user configuration'
    Assert ((Get-ItemProperty 'HKCU:\Software\Wand' -Name SandboxOldValue).SandboxOldValue -eq 'preserve-me') 'Launcher changed user registry settings'
    Assert (@(Get-ChildItem -LiteralPath $backupRoot -Directory).Count -eq $backupCount) 'Launcher unexpectedly performed installation backup'
    Assert (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'GBTGAME\WandSetup\installation.json')) 'Installation state was not recorded'
    $checks.Add('repeat launcher preserves install, user files and registry without reinstall')
    $reinstall = Invoke-TestExe 'WandSetup.exe' @('--install', '--no-launch')
    Assert (Test-Path -LiteralPath (Join-Path $reinstall.backupPath 'Roaming\Wand\sandbox-old-settings.txt')) 'Old settings backup missing'
    Assert (-not (Test-Path -LiteralPath (Join-Path $roaming 'sandbox-old-settings.txt'))) 'Old settings were not cleaned'
    $snapshots = Get-ChildItem -LiteralPath $reinstall.backupPath -Filter '*settings.json' | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }
    Assert (($snapshots -join '') -match 'SandboxOldValue') 'Registry snapshot missing'
    Assert (-not (Get-ItemProperty 'HKCU:\Software\Wand' -Name SandboxOldValue -ErrorAction SilentlyContinue)) 'Old registry value remained'
    $checks.Add('old installation removal, configuration backup and registry cleanup')

    [void](Invoke-TestExe 'WandSetup.exe' @('--cli', 'restore', '--path', $root))
    $state = Invoke-TestExe 'WandSetup.exe' @('--cli', 'status', '--path', $root)
    Assert (-not $state.data.patched) 'Restore left a patched installation'
    $checks.Add('CLI restore')
    [void](Invoke-TestExe 'WandSetup.exe' @('--cli', 'patch', '--path', $root, '--expected-version', '12.68.0'))
    [void](Invoke-TestExe 'WandSetup.exe' @('--cli', 'launch', '--path', $root, '--strategy', 'static'))
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $running = @(Get-Process Wand -ErrorAction SilentlyContinue)
        if ($running.Count -gt 0) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert ($running.Count -gt 0) 'Wand did not remain running after launch'
    Start-Sleep -Seconds 8
    Assert (@(Get-Process Wand -ErrorAction SilentlyContinue).Count -gt 0) 'Wand exited shortly after launch'
    $checks.Add('CLI reapply and application startup')
    $success = $true
} catch { $failure = $_.Exception.ToString(); Write-Host $failure -ForegroundColor Red }
finally {
    $result = @{ success = $success; checks = @($checks); error = $failure; testedAt = [DateTime]::UtcNow.ToString('o'); environment = 'Windows Sandbox' }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
if (-not $success) { exit 1 }
Write-Host 'Acceptance passed. Results are saved to the mapped host output directory.' -ForegroundColor Green

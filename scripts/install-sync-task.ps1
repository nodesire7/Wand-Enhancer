<#
.SYNOPSIS
    Registers the Windows scheduled task that dispatches the upstream sync workflow.

.DESCRIPTION
    GitHub's own schedule delivery is unreliable for some accounts, so this task pings
    `gh workflow run sync-upstream.yml` every N minutes. Merging, branding, building and the
    rolling release still happen on GitHub.

    Removing it again: schtasks /Delete /TN "WandEnhancer Upstream Sync" /F
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'WandEnhancer Upstream Sync',
    [int]$IntervalMinutes = 30
)

$ErrorActionPreference = 'Stop'

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$triggerScript = Join-Path $PSScriptRoot 'trigger-sync.ps1'
if (-not (Test-Path -LiteralPath $triggerScript)) {
    throw "Missing $triggerScript"
}

$action = '"{0}" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{1}"' -f $powershell, $triggerScript
$arguments = @(
    '/Create'
    '/TN', $TaskName
    '/TR', $action
    '/SC', 'MINUTE'
    '/MO', $IntervalMinutes
    '/F'
)

& schtasks.exe @arguments
if ($LASTEXITCODE -ne 0) {
    throw "schtasks failed with exit code $LASTEXITCODE"
}

Write-Host "Registered '$TaskName' to dispatch the sync every $IntervalMinutes minutes."
Write-Host "Log: $env:LOCALAPPDATA\WandEnhancer-upstream-sync.log"

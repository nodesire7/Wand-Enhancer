<#
.SYNOPSIS
    Asks GitHub Actions to run the upstream sync workflow.

.DESCRIPTION
    GitHub's own schedule delivery is unreliable for some accounts (verified on this fork), so an
    optional Windows scheduled task runs this script instead. It only triggers the existing
    workflow: the merge, branding reapply, build and rolling release all happen on GitHub.

    Requires a logged-in GitHub CLI for the account that owns the fork (`gh auth status`).

.PARAMETER Repository
    owner/repo of the fork.

.PARAMETER Workflow
    Workflow file to dispatch.
#>
[CmdletBinding()]
param(
    [string]$Repository = 'nodesire7/Wand-Enhancer',
    [string]$Workflow = 'sync-upstream.yml'
)

$ErrorActionPreference = 'Stop'
$logPath = Join-Path $env:LOCALAPPDATA 'WandEnhancer-upstream-sync.log'

function Write-SyncLog {
    param([string]$Message)

    $line = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -Path $logPath -Value $line -Encoding UTF8
}

function Resolve-Gh {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

try {
    $gh = Resolve-Gh
    if (-not $gh) {
        throw 'GitHub CLI (gh) was not found.'
    }

    # A stale machine-wide GITHUB_TOKEN on some PCs outranks the gh keyring login and makes every
    # call fail with 401. The scheduled task runs in that environment, so drop the override and
    # let gh use the account from `gh auth login` instead.
    Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:\GITHUB_TOKEN -ErrorAction SilentlyContinue

    # gh writes progress to stderr; capture both streams so the scheduled run keeps a log.
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $gh workflow run $Workflow --repo $Repository 2>&1 | ForEach-Object { Write-SyncLog "gh: $_" }
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorAction

    if ($exitCode -ne 0) {
        throw "gh workflow run exited with code $exitCode."
    }

    Write-SyncLog "Triggered $Workflow on $Repository."
    exit 0
}
catch {
    Write-SyncLog "Failed: $($_.Exception.Message)"
    exit 1
}

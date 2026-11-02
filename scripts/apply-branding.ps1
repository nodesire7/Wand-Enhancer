<#
.SYNOPSIS
    Applies the GBTGAME.ME fork build information to the repository.

.DESCRIPTION
    The fork keeps its branding in branding/branding.json and two files that upstream does not
    have (WandEnhancer/Branding/*). A few upstream files still need small, deterministic edits:
    App.xaml.cs needs the runtime hook and Properties/AssemblyInfo.cs carries the exe metadata.
    Re-running this script after `git merge upstream/master` puts those edits back, so the
    sync workflow never needs to hand-resolve a branding conflict.

    Run with -Check to verify a build is branded without modifying anything (used by CI).

.PARAMETER Check
    Verify that branding is applied; exit 1 when anything is missing or stale.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent directory of this script.
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

function Resolve-RepoRoot {
    param([string]$Value)

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return [System.IO.Path]::GetFullPath($Value)
    }

    return [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}

function Read-SourceFile {
    param([string]$Path)

    if (-not [System.IO.File]::Exists($Path)) {
        throw "Missing file: $Path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $newLine = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }

    return [pscustomobject]@{
        Path = $Path
        Text = $text
        HasBom = $hasBom
        NewLine = $newLine
    }
}

function Write-SourceFile {
    param(
        [string]$Path,
        [string]$Text,
        [bool]$HasBom
    )

    $encoding = New-Object System.Text.UTF8Encoding($HasBom)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

function ConvertTo-NewLine {
    param(
        [string]$Text,
        [string]$NewLine
    )

    $normalized = $Text.Replace("`r`n", "`n")
    if ($NewLine -eq "`r`n") {
        return $normalized.Replace("`n", "`r`n")
    }

    return $normalized
}

function ConvertTo-CSharpString {
    param([string]$Value)

    return $Value.Replace('\', '\\').Replace('"', '\"')
}

function Add-BrandingHook {
    param($File)

    $text = $File.Text
    if ($text -match 'BrandingUi\.Apply\(') {
        return $text
    }

    $anchor = [System.Text.RegularExpressions.Regex]::Match(
        $text,
        '(?m)^(?<indent>[ \t]*)this\.MainWindow\.Show\(\);')
    if (-not $anchor.Success) {
        throw "App.xaml.cs: cannot find 'this.MainWindow.Show();' to insert the branding hook before."
    }

    $hook = $anchor.Groups['indent'].Value +
        'WandEnhancer.Branding.BrandingUi.Apply(this.MainWindow);' + $File.NewLine
    return $text.Insert($anchor.Index, $hook)
}

function Update-AssemblyAttribute {
    param(
        [string]$Text,
        [string]$Attribute,
        [string]$Value,
        [string]$NewLine
    )

    $pattern = '(?m)^(?<indent>[ \t]*)\[assembly: ' +
        [System.Text.RegularExpressions.Regex]::Escape($Attribute) + '\(\"[^\"]*\"\)\]'
    $replacement = [System.Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $match.Groups['indent'].Value + '[assembly: ' + $Attribute + '("' + $Value + '")]'
    }

    if ([System.Text.RegularExpressions.Regex]::IsMatch($Text, $pattern)) {
        return [System.Text.RegularExpressions.Regex]::Replace($Text, $pattern, $replacement)
    }

    $anchor = [System.Text.RegularExpressions.Regex]::Match(
        $Text,
        '(?m)^(?<indent>[ \t]*)\[assembly: AssemblyVersion')
    if (-not $anchor.Success) {
        throw "AssemblyInfo.cs: cannot find AssemblyVersion to append $Attribute before."
    }

    $line = $anchor.Groups['indent'].Value + '[assembly: ' + $Attribute + '("' + $Value + '")]' + $NewLine
    return $Text.Insert($anchor.Index, $line)
}

$root = Resolve-RepoRoot $RepoRoot
$brandingPath = Join-Path $root 'branding/branding.json'
if (-not [System.IO.File]::Exists($brandingPath)) {
    throw "Missing branding data: $brandingPath"
}

$branding = ConvertFrom-Json ([System.IO.File]::ReadAllText($brandingPath, [System.Text.Encoding]::UTF8))

foreach ($name in @('brand', 'author', 'note', 'product', 'forum', 'forumUrl', 'telegram', 'telegramUrl')) {
    $value = $branding.$name
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "branding/branding.json: '$name' must not be empty."
    }
}

foreach ($name in @('forumUrl', 'telegramUrl')) {
    if ($branding.$name -notmatch '^https?://') {
        throw "branding/branding.json: '$name' must be an http(s) URL."
    }
}

$description = "$($branding.author) $($branding.note) | 论坛 $($branding.forum) | TG $($branding.telegram)"
$copyright = "Copyright © 2025 k1tbyte · $($branding.note) $($branding.author) ($($branding.brand))"

$generatedText = @"
// <auto-generated>
// Generated by scripts/apply-branding.ps1 from branding/branding.json.
// Change branding.json and re-run the script; do not edit this file by hand.
// </auto-generated>
namespace WandEnhancer.Branding
{
    internal static class BrandingInfo
    {
        public const string Brand = "$(ConvertTo-CSharpString $branding.brand)";
        public const string Author = "$(ConvertTo-CSharpString $branding.author)";
        public const string Note = "$(ConvertTo-CSharpString $branding.note)";
        public const string Product = "$(ConvertTo-CSharpString $branding.product)";
        public const string Forum = "$(ConvertTo-CSharpString $branding.forum)";
        public const string ForumUrl = "$(ConvertTo-CSharpString $branding.forumUrl)";
        public const string Telegram = "$(ConvertTo-CSharpString $branding.telegram)";
        public const string TelegramUrl = "$(ConvertTo-CSharpString $branding.telegramUrl)";

        public const string Badge = Brand + " · " + Author + " · " + Note;
        public const string Credit = "Made with ❤️ by k1tbyte · " + Note + " " + Author;
        public const string Contacts = Forum + " · " + Telegram;
        public const string Tooltip = Author + " · " + Note + "\n" + Forum + "\n" + Telegram + "\n" + "点击打开论坛";
        public const string LogLine = Brand + " " + Note + "版 · " + Author + " · " + Forum + " · " + Telegram;
    }
}
"@
$generatedText = ConvertTo-NewLine $generatedText "`r`n"

$appPath = Join-Path $root 'WandEnhancer/App.xaml.cs'
$app = Read-SourceFile $appPath
$appText = Add-BrandingHook $app

$assemblyInfoPath = Join-Path $root 'WandEnhancer/Properties/AssemblyInfo.cs'
$assemblyInfo = Read-SourceFile $assemblyInfoPath
$assemblyInfoText = $assemblyInfo.Text
$assemblyInfoText = Update-AssemblyAttribute $assemblyInfoText 'AssemblyTitle' $branding.product $assemblyInfo.NewLine
$assemblyInfoText = Update-AssemblyAttribute $assemblyInfoText 'AssemblyDescription' $description $assemblyInfo.NewLine
$assemblyInfoText = Update-AssemblyAttribute $assemblyInfoText 'AssemblyCompany' $branding.brand $assemblyInfo.NewLine
$assemblyInfoText = Update-AssemblyAttribute $assemblyInfoText 'AssemblyProduct' $branding.product $assemblyInfo.NewLine
$assemblyInfoText = Update-AssemblyAttribute $assemblyInfoText 'AssemblyCopyright' $copyright $assemblyInfo.NewLine

$targets = @(
    [pscustomobject]@{
        RelativePath = 'WandEnhancer/Branding/BrandingInfo.g.cs'
        Text = $generatedText
        HasBom = $true
    },
    [pscustomobject]@{
        RelativePath = 'WandEnhancer/App.xaml.cs'
        Text = $appText
        HasBom = $app.HasBom
    },
    [pscustomobject]@{
        RelativePath = 'WandEnhancer/Properties/AssemblyInfo.cs'
        Text = $assemblyInfoText
        HasBom = $assemblyInfo.HasBom
    }
)

$stale = New-Object System.Collections.Generic.List[string]
foreach ($target in $targets) {
    $path = Join-Path $root $target.RelativePath
    $current = $null
    if ([System.IO.File]::Exists($path)) {
        $current = Read-SourceFile $path
    }

    # Preserve the newline style of the file on disk for the two upstream files; the generated
    # file is always CRLF like the rest of the C# sources.
    $expectedText = $target.Text
    if ($null -ne $current -and $target.RelativePath -ne 'WandEnhancer/Branding/BrandingInfo.g.cs') {
        $expectedText = ConvertTo-NewLine $expectedText $current.NewLine
    }

    if ($null -eq $current -or $current.Text -ne $expectedText) {
        $stale.Add($target.RelativePath)
        if (-not $Check) {
            Write-SourceFile $path $expectedText $target.HasBom
        }
    }
}

if ($Check) {
    if ($stale.Count -gt 0) {
        Write-Host 'Branding is out of date:'
        foreach ($path in $stale) {
            Write-Host "  - $path"
        }
        Write-Host 'Run ./scripts/apply-branding.ps1 to reapply it.'
        exit 1
    }

    Write-Host 'Branding is up to date.'
    exit 0
}

if ($stale.Count -eq 0) {
    Write-Host 'Branding is already up to date.'
    exit 0
}

Write-Host 'Branding updated:'
foreach ($path in $stale) {
    Write-Host "  - $path"
}

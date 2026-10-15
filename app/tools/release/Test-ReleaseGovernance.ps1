[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseTag,

    [string]$RepositoryRoot,

    [string]$ReleaseNotesPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Release.Common.ps1")

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-WinShotRepositoryRoot
}
$repositoryPath = [System.IO.Path]::GetFullPath($RepositoryRoot)
$version = Get-ProjectReleaseVersion -RepositoryRoot $repositoryPath
$baseVersion = $version.Split('-', 2)[0]
$versionParts = $baseVersion.Split('.')
$fourPartVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).0"
[xml]$buildProperties = Get-Content -LiteralPath (
    Join-Path $repositoryPath "Directory.Build.props") -Raw
$expectedVersionFields = [ordered]@{
    Version = $version
    AssemblyVersion = $fourPartVersion
    FileVersion = $fourPartVersion
    InformationalVersion = $version
}
foreach ($versionField in $expectedVersionFields.GetEnumerator()) {
    $nodes = @($buildProperties.SelectNodes(
        "/Project/PropertyGroup/$($versionField.Key)"))
    if ($nodes.Count -ne 1 -or
        [string]$nodes[0].InnerText -cne [string]$versionField.Value) {
        throw "Directory.Build.props release version fields must be unique and exactly synchronized."
    }
}

$appInfoSource = Get-Content -LiteralPath (
    Join-Path $repositoryPath "src\WinShot.App\AppInfo.cs") -Raw
$appVersionMatches = [regex]::Matches(
    $appInfoSource,
    'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"\s*;',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
$appMajorMatches = [regex]::Matches(
    $appInfoSource,
    'public\s+const\s+int\s+Major\s*=\s*([0-9]+)\s*;',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
if ($appVersionMatches.Count -ne 1 -or
    $appVersionMatches[0].Groups[1].Value -cne $version -or
    $appMajorMatches.Count -ne 1 -or
    [int]$appMajorMatches[0].Groups[1].Value -ne [int]$versionParts[0]) {
    throw "AppInfo release Version and paid Major must be unique and exactly synchronized."
}

$expectedTag = "v$version"
if ($ReleaseTag.Length -gt 128 -or
    $ReleaseTag -cnotmatch
        '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$' -or
    $ReleaseTag -cne $expectedTag) {
    throw "Release tag must exactly match the reviewed product version as 'v<version>'."
}

$expectedFileName = "$expectedTag.md"
if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $ReleaseNotesPath = Join-Path $repositoryPath "packaging\release-notes\$expectedFileName"
}
$notesPath = [System.IO.Path]::GetFullPath($ReleaseNotesPath)
if ([System.IO.Path]::GetFileName($notesPath) -cne $expectedFileName) {
    throw "Release-notes file name must exactly match the release tag."
}
if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
    throw "Versioned release notes are missing."
}
$notesFile = Get-Item -LiteralPath $notesPath
if ($notesFile.Length -lt 128 -or $notesFile.Length -gt (32 * 1024)) {
    throw "Release notes must contain 128 through 32768 bytes."
}

try {
    [byte[]]$bytes = [System.IO.File]::ReadAllBytes($notesPath)
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        throw "Release notes must be UTF-8 without a byte-order mark."
    }
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $text = $utf8.GetString($bytes)
}
catch {
    if ($_.Exception.Message -ceq
        "Release notes must be UTF-8 without a byte-order mark.") {
        throw
    }
    throw "Release notes are not valid bounded UTF-8."
}

if ($text -notmatch "`n$" -or $text -match "`r(?!`n)") {
    throw "Release notes must use complete LF or CRLF lines and end with a newline."
}
$normalized = $text.Replace("`r`n", "`n")
$content = $normalized.Substring(0, $normalized.Length - 1)
[string[]]$lines = $content.Split(
    [string[]]@("`n"),
    [System.StringSplitOptions]::None)
if ($lines.Count -gt 64) {
    throw "Release notes exceed the 64-line boundary."
}

foreach ($line in $lines) {
    if ($line.Length -gt 300 -or $line -cne $line.TrimEnd()) {
        throw "Release-note lines must be at most 300 characters without trailing whitespace."
    }
    foreach ($character in $line.ToCharArray()) {
        $category = [System.Globalization.CharUnicodeInfo]::GetUnicodeCategory($character)
        if ($category -in @(
            [System.Globalization.UnicodeCategory]::Control,
            [System.Globalization.UnicodeCategory]::Format,
            [System.Globalization.UnicodeCategory]::Surrogate,
            [System.Globalization.UnicodeCategory]::PrivateUse)) {
            throw "Release notes contain a forbidden control or invisible character."
        }
    }
}

if ($normalized.Contains("<") -or
    $normalized.Contains(">") -or
    $normalized.Contains([char]0x60) -or
    $normalized -match '(?i)!\[' -or
    $normalized -match '\]\s*\(' -or
    $normalized -match '(?m)^\s*\[[^\]]+\]\s*:' -or
    $normalized -match '(?i)\b(?:https?|file|javascript|data):' -or
    $normalized -match '(?i)\bwww\.' -or
    $normalized -match '(?i)(?:^|[\s(])(?:[A-Z]:\\|\\\\)' -or
    $normalized -match '(?i)-----BEGIN [A-Z ]*PRIVATE KEY-----' -or
    $normalized -match '(?i)\b(?:gh[opsu]_|github_pat_|sk-(?:proj-)?|AKIA)[A-Za-z0-9_-]+' -or
    $normalized -match '(?i)\b(?:TODO|TBD|FIXME|CHANGEME)\b' -or
    $normalized.Contains("{{") -or
    $normalized.Contains("}}")) {
    throw "Release notes contain unsafe Markdown, a path, a placeholder, or secret-shaped text."
}
if ($normalized -match
    '(?i)\b(?:signed|unsigned|signature|authenticode|timestamped|notarized|certified)\b' -or
    $normalized -match '(?i)\bverified\s+publisher\b' -or
    $normalized -match '(?i)\bproduction[- ]ready\b' -or
    $normalized -match '(?i)\b(?:buy|download)\s+now\b') {
    throw "Release notes cannot make signing, publisher-trust, availability, or readiness claims."
}

$expectedPrefix = @(
    "# WinShot $version",
    "",
    "Status: Engineering draft",
    "Release date: Unscheduled",
    "")
if ($lines.Count -lt $expectedPrefix.Count) {
    throw "Release notes are missing the reviewed metadata header."
}
for ($index = 0; $index -lt $expectedPrefix.Count; $index++) {
    if ($lines[$index] -cne $expectedPrefix[$index]) {
        throw "Release notes must contain the exact reviewed version, status, and date header."
    }
}

$expectedSections = @(
    "## Highlights",
    "## Reliability and privacy",
    "## Known limitations")
$lineIndex = $expectedPrefix.Count
$bulletSet = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$bulletCount = 0
foreach ($section in $expectedSections) {
    if ($lineIndex -ge $lines.Count -or $lines[$lineIndex] -cne $section) {
        throw "Release notes must contain the exact reviewed sections in order."
    }
    $lineIndex++
    $sectionBulletCount = 0
    while ($lineIndex -lt $lines.Count -and
        $lines[$lineIndex].StartsWith("- ", [System.StringComparison]::Ordinal)) {
        $bullet = $lines[$lineIndex].Substring(2)
        if ($bullet.Length -lt 10 -or
            $bullet.Length -gt 240 -or
            $bullet -cne $bullet.Trim() -or
            $bullet[-1] -notin @('.', '!', '?') -or
            -not $bulletSet.Add($bullet)) {
            throw "Release-note bullets must be unique, bounded, trimmed sentences."
        }
        $sectionBulletCount++
        $bulletCount++
        if ($sectionBulletCount -gt 12) {
            throw "A release-note section exceeds the 12-bullet boundary."
        }
        $lineIndex++
    }
    if ($sectionBulletCount -lt 1) {
        throw "Every reviewed release-note section requires at least one bullet."
    }
    if ($section -cne $expectedSections[-1]) {
        if ($lineIndex -ge $lines.Count -or $lines[$lineIndex] -cne "") {
            throw "Release-note sections must be separated by one blank line."
        }
        $lineIndex++
    }
}
if ($lineIndex -ne $lines.Count) {
    throw "Release notes contain unexpected trailing structure."
}

[pscustomobject]@{
    Result = "PASS_RELEASE_GOVERNANCE"
    Version = $version
    ReleaseTag = $ReleaseTag
    ReleaseNotesFile = $expectedFileName
    ReleaseNotesSha256 = Get-Sha256Hex -Path $notesPath
    Status = "ENGINEERING_DRAFT"
    SectionCount = $expectedSections.Count
    BulletCount = $bulletCount
    PublicReleaseApproved = $false
}

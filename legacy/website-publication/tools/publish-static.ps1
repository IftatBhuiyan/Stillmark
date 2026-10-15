[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Destination
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$allowlistPath = Join-Path $PSScriptRoot "publish-allowlist.txt"
$destinationPath = if ([IO.Path]::IsPathRooted($Destination)) {
  [IO.Path]::GetFullPath($Destination)
} else {
  [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Destination))
}
$entries = @(Get-Content -LiteralPath $allowlistPath | ForEach-Object {
  $entry = ($_ -replace "#.*$", "").Trim()
  if ($entry) { $entry }
})

if (-not $entries.Count) {
  throw "The publication allowlist is empty."
}

foreach ($entry in $entries) {
  $sourcePath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $entry))
  $relativeSource = [IO.Path]::GetRelativePath($repositoryRoot, $sourcePath)
  if ($relativeSource.StartsWith("..")) {
    throw "Allowlist path escapes the repository: $entry"
  }
  if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Allowlist source is missing: $entry"
  }
}

if (Test-Path -LiteralPath $destinationPath) {
  $existing = @(Get-ChildItem -LiteralPath $destinationPath -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($destinationPath, $_.FullName).Replace("\", "/")
  })
  $unexpected = @($existing | Where-Object { $_ -notin $entries })
  if ($unexpected.Count) {
    throw "Destination contains files outside the publication allowlist: $($unexpected -join ', ')"
  }
} else {
  New-Item -ItemType Directory -Path $destinationPath | Out-Null
}

foreach ($entry in $entries) {
  $sourcePath = Join-Path $repositoryRoot $entry
  $targetPath = Join-Path $destinationPath $entry
  $targetDirectory = Split-Path -Parent $targetPath
  New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
  Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
}

Write-Output "Published $($entries.Count) allowlisted files to $destinationPath"

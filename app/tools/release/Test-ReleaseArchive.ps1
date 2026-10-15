[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [string]$DotnetPath = (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Release.Common.ps1")

Assert-SafeReleaseVersion -Version $ExpectedVersion
$repositoryRoot = Get-WinShotRepositoryRoot
$configuration = Get-ReleaseConfiguration -RepositoryRoot $repositoryRoot
$limits = $configuration.validationLimits
$archiveFullPath = [System.IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $archiveFullPath -PathType Leaf)) {
    throw "Release archive '$archiveFullPath' does not exist."
}
$archiveFile = Get-Item -LiteralPath $archiveFullPath
if ($archiveFile.Length -gt [long]$limits.maximumArchiveBytes) {
    throw "Release archive exceeds the $($limits.maximumArchiveBytes)-byte compressed-size limit."
}

$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
$tempLeaf = "WinShotReleaseValidation-$([Guid]::NewGuid().ToString('N'))"
$tempRoot = Join-Path $tempBase $tempLeaf
$tempCreated = $false
$result = $null

try {
    [System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    $tempCreated = $true
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archiveStream = [System.IO.File]::Open(
        $archiveFullPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $archiveStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false,
            [System.Text.UTF8Encoding]::new($false))
        try {
            if ($archive.Entries.Count -gt [int]$limits.maximumFileCount) {
                throw "Release archive exceeds the $($limits.maximumFileCount)-entry limit."
            }

            $entriesByPath = [System.Collections.Generic.Dictionary[string,System.IO.Compression.ZipArchiveEntry]]::new(
                [System.StringComparer]::Ordinal)
            $caseInsensitivePaths = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase)
            [long]$totalBytes = 0
            foreach ($entry in $archive.Entries) {
                $relative = [string]$entry.FullName
                if ([string]::IsNullOrWhiteSpace($relative) -or
                    $relative.StartsWith('/') -or $relative.Contains('\') -or
                    $relative.Contains(':') -or $relative.Contains("`0") -or
                    $relative.EndsWith('/') -or
                    $relative.Length -gt [int]$limits.maximumPathCharacters) {
                    throw "Release archive contains an unsafe or unsupported entry path."
                }
                $segments = $relative.Split('/')
                foreach ($segment in $segments) {
                    $deviceStem = [System.IO.Path]::GetFileNameWithoutExtension($segment)
                    $deviceStem = $deviceStem.TrimEnd([char[]]@(' ', '.'))
                    if ([string]::IsNullOrWhiteSpace($segment) -or
                        $segment -eq '.' -or $segment -eq '..' -or
                        $segment.EndsWith(' ') -or $segment.EndsWith('.') -or
                        $deviceStem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                        throw "Release archive entry '$relative' contains an unsafe path segment."
                    }
                }

                $unixFileType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
                if ($unixFileType -eq 0xA000) {
                    throw "Release archive entry '$relative' is a symbolic link."
                }
                if ($entriesByPath.ContainsKey($relative) -or
                    -not $caseInsensitivePaths.Add($relative)) {
                    throw "Release archive contains duplicate or case-colliding entry '$relative'."
                }
                if ($entry.Length -lt 0 -or
                    $entry.Length -gt [long]$limits.maximumFileBytes) {
                    throw "Release archive entry '$relative' exceeds the per-file size limit."
                }
                if ($totalBytes -gt ([long]$limits.maximumTotalBytes - $entry.Length)) {
                    throw "Release archive exceeds the bounded uncompressed-size limit."
                }
                $totalBytes += $entry.Length
                $entriesByPath.Add($relative, $entry)
            }

            $sortedPaths = Get-OrdinalSortedStrings -Values @($entriesByPath.Keys)
            foreach ($relative in $sortedPaths) {
                $entry = $entriesByPath[$relative]
                $destination = [System.IO.Path]::GetFullPath(
                    (Join-Path $tempRoot $relative.Replace('/', '\')))
                $tempPrefix = [System.IO.Path]::GetFullPath($tempRoot).TrimEnd('\') + '\'
                if (-not $destination.StartsWith(
                    $tempPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "Release archive entry resolves outside the owned validation directory."
                }

                $parent = [System.IO.Path]::GetDirectoryName($destination)
                [System.IO.Directory]::CreateDirectory($parent) | Out-Null
                $entryStream = $entry.Open()
                try {
                    $output = [System.IO.File]::Open(
                        $destination,
                        [System.IO.FileMode]::CreateNew,
                        [System.IO.FileAccess]::Write,
                        [System.IO.FileShare]::None)
                    try {
                        [byte[]]$buffer = New-Object byte[] 81920
                        [long]$written = 0
                        while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            if ($written -gt ($entry.Length - $read)) {
                                throw "Release archive entry '$relative' expanded beyond its declared size."
                            }
                            $output.Write($buffer, 0, $read)
                            $written += $read
                        }
                        if ($written -ne $entry.Length) {
                            throw "Release archive entry '$relative' did not match its declared size."
                        }
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $archiveStream.Dispose()
    }

    $validationScript = Join-Path $PSScriptRoot "Test-ReleaseArtifact.ps1"
    $directoryValidation = & $validationScript `
        -ArtifactDirectory $tempRoot `
        -ExpectedVersion $ExpectedVersion `
        -DotnetPath $DotnetPath
    Assert-ReleaseZipMatchesDirectory `
        -ArtifactDirectory $tempRoot `
        -ArchivePath $archiveFullPath

    $result = [pscustomobject]@{
        Archive = $archiveFullPath
        ArchiveSha256 = Get-Sha256Hex -Path $archiveFullPath
        Version = $ExpectedVersion
        FileCount = $directoryValidation.FileCount
        TotalBytes = $directoryValidation.TotalBytes
        ExecutableSignature = $directoryValidation.ExecutableSignature
        Result = "PASS_UNSIGNED_ARCHIVE"
        TemporaryDirectoryRemoved = $true
    }
}
finally {
    if ($tempCreated) {
        $resolvedTemp = [System.IO.Path]::GetFullPath($tempRoot)
        $resolvedParent = [System.IO.Path]::GetDirectoryName($resolvedTemp)
        $resolvedLeaf = [System.IO.Path]::GetFileName($resolvedTemp)
        if (-not [string]::Equals(
            $resolvedParent,
            $tempBase,
            [System.StringComparison]::OrdinalIgnoreCase) -or
            $resolvedLeaf -notmatch '^WinShotReleaseValidation-[0-9a-f]{32}$') {
            throw "Refusing to remove a validation directory outside the GUID-owned temp scope."
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}

$result

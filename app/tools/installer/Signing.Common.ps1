function Get-SignatureEkus {
    param([Parameter(Mandatory = $true)]$Certificate)

    $values = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($extension in $Certificate.Extensions) {
        if ($extension -isnot
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            continue
        }
        foreach ($oid in $extension.EnhancedKeyUsages) {
            if ([string]::IsNullOrWhiteSpace($oid.Value) -or
                -not $values.Add($oid.Value)) {
                throw "Signing certificate '$($Certificate.Subject)' has an invalid or duplicate EKU."
            }
        }
    }
    Write-Output -NoEnumerate $values
}

function Assert-SignedReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$PublisherSubject,
        [Parameter(Mandatory = $true)][string]$SubscriberIdentityEku,
        [Parameter(Mandatory = $true)][string]$SignToolPath
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "Signed release artifact '$([System.IO.Path]::GetFileName($Path))' is not validly Authenticode-signed."
    }
    if ($signature.SignerCertificate.Subject -cne $PublisherSubject) {
        throw "Signed release artifact '$([System.IO.Path]::GetFileName($Path))' has an unexpected publisher subject."
    }
    $ekuValues = Get-SignatureEkus -Certificate $signature.SignerCertificate
    foreach ($requiredEku in @(
            '1.3.6.1.5.5.7.3.3',
            '1.3.6.1.4.1.311.97.1.0',
            $SubscriberIdentityEku)) {
        if (-not $ekuValues.Contains($requiredEku)) {
            throw "Signed release artifact '$([System.IO.Path]::GetFileName($Path))' is missing a required signing EKU."
        }
    }
    $subscriberEkus = @($ekuValues | Where-Object {
        $_.StartsWith('1.3.6.1.4.1.311.97.', [System.StringComparison]::Ordinal) -and
        $_ -cne '1.3.6.1.4.1.311.97.1.0'
    })
    if ($subscriberEkus.Count -ne 1 -or $subscriberEkus[0] -cne $SubscriberIdentityEku) {
        throw "Signed release artifact '$([System.IO.Path]::GetFileName($Path))' has an ambiguous subscriber identity EKU."
    }
    & $SignToolPath verify '/pa' '/all' '/tw' $Path | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool verification failed for '$([System.IO.Path]::GetFileName($Path))'."
    }
}

function Expand-SafeZipArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    $destinationRoot = ([System.IO.Path]::GetFullPath($DestinationPath)).TrimEnd('\') + '\'
    [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
    $seenEntries = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $entryName = $entry.FullName.Replace('/', '\')
            if ([string]::IsNullOrWhiteSpace($entryName) -or $entryName.EndsWith('\')) {
                continue
            }
            if ([System.IO.Path]::IsPathRooted($entryName) -or
                $entryName -match '(^|\\)[.]{2}(\\|$)' -or
                $entryName -match '^[A-Za-z]:') {
                throw "Package archive contains an unsafe path '$($entry.FullName)'."
            }
            if (-not $seenEntries.Add($entryName)) {
                throw "Package archive contains a duplicate path '$($entry.FullName)'."
            }
            $destination = [System.IO.Path]::GetFullPath(
                (Join-Path $DestinationPath $entryName))
            if (-not $destination.StartsWith(
                    $destinationRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Package archive path '$($entry.FullName)' escapes the verification directory."
            }
            [System.IO.Directory]::CreateDirectory(
                ([System.IO.Path]::GetDirectoryName($destination))) | Out-Null
            $entryInput = $entry.Open()
            try {
                $output = [System.IO.File]::Open(
                    $destination,
                    [System.IO.FileMode]::CreateNew,
                    [System.IO.FileAccess]::Write,
                    [System.IO.FileShare]::None)
                try {
                    $entryInput.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                }
            }
            finally {
                $entryInput.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}


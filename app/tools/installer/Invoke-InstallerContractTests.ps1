[CmdletBinding()]
param(
    [string]$ContractPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path (Split-Path -Parent $PSScriptRoot) ".."))
$validatorPath = Join-Path $PSScriptRoot "Test-InstallerContract.ps1"
if ([string]::IsNullOrWhiteSpace($ContractPath)) {
    $ContractPath = Join-Path $repositoryRoot `
        "packaging\installer\installer-contract.json"
}
$ContractPath = [System.IO.Path]::GetFullPath($ContractPath)
$contractText = [System.IO.File]::ReadAllText(
    $ContractPath,
    [System.Text.UTF8Encoding]::new($false))
$contractLineSeparator = if ($contractText.Contains("`r`n")) {
    "`r`n"
}
else {
    "`n"
}

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ExpectedMessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedMessagePattern) {
            throw "Expected '$Name' to fail for the intended reason."
        }
        Write-Output "PASS_EXPECTED_REFUSAL: $Name"
        return
    }
    throw "Expected '$Name' to fail closed."
}

function Assert-ProductionPublisherRejectsUnsignedTestPayload {
    $payload = Join-Path $temporaryRoot 'unsigned-test-package-payload'
    [void][System.IO.Directory]::CreateDirectory($payload)
    [System.IO.File]::WriteAllBytes(
        (Join-Path $payload 'WinShot.exe'),
        [byte[]]@(0))
    [System.IO.File]::WriteAllText(
        (Join-Path $payload 'UNSIGNED-TEST-ARTIFACT.txt'),
        'Fixture only; this must never be repackaged as production.',
        [System.Text.UTF8Encoding]::new($false))

    $globalJsonPath = Join-Path $repositoryRoot 'global.json'
    $publisherPath = Join-Path $repositoryRoot `
        'tools\installer\Publish-VelopackProduction.ps1'
    $childPowerShell = (Get-Command powershell.exe `
        -CommandType Application -ErrorAction Stop).Source
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $childPowerShell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $publisherPath `
            -PayloadDirectory $payload `
            -Version '1.0.0' `
            -ReleasedAtUtc ([DateTimeOffset]::UtcNow.ToString('o')) `
            -AzureTrustedSignFile $globalJsonPath `
            -CatalogSigningKeyPath $globalJsonPath `
            -ExpectedPublisherSubject 'CN=Fixture' `
            -ExpectedSubscriberIdentityEku '1.3.6.1.4.1.311.97.123' 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $combinedOutput = $output -join [Environment]::NewLine
    if ($exitCode -eq 0 -or
        $combinedOutput -notmatch
            'engineering or unsigned-test marker is in the payload') {
        throw 'Production Velopack publisher did not reject the unsigned test marker.'
    }
    Write-Output 'PASS_EXPECTED_REFUSAL: production package rejects unsigned test payload'
}

function Write-TamperedContract {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$OldText,
        [Parameter(Mandatory = $true)][string]$NewText
    )

    $text = [System.IO.File]::ReadAllText(
        $Source,
        [System.Text.UTF8Encoding]::new($false))
    $matches = [regex]::Matches(
        $text,
        [regex]::Escape($OldText),
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if ($matches.Count -ne 1) {
        throw "Self-test mutation target must occur exactly once."
    }
    [System.IO.File]::WriteAllText(
        $Destination,
        $text.Replace($OldText, $NewText),
        [System.Text.UTF8Encoding]::new($false))
}

function Join-TestBytes {
    param([Parameter(Mandatory = $true)][object[]]$Parts)

    $stream = [System.IO.MemoryStream]::new()
    try {
        foreach ($part in $Parts) {
            $bytes = [byte[]]$part
            $stream.Write($bytes, 0, $bytes.Length)
        }
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function ConvertTo-TestDerLength {
    param([Parameter(Mandatory = $true)][int]$Length)

    if ($Length -lt 0) {
        throw "Test DER length cannot be negative."
    }
    if ($Length -lt 128) {
        return ,[byte[]]@([byte]$Length)
    }
    $octets = [System.Collections.Generic.List[byte]]::new()
    $remaining = $Length
    while ($remaining -gt 0) {
        $octets.Insert(0, [byte]($remaining -band 0xFF))
        $remaining = $remaining -shr 8
    }
    $result = [System.Collections.Generic.List[byte]]::new()
    $result.Add([byte](0x80 -bor $octets.Count))
    $result.AddRange($octets)
    return ,$result.ToArray()
}

function New-TestDerElement {
    param(
        [Parameter(Mandatory = $true)][byte]$Tag,
        [Parameter(Mandatory = $true)][byte[]]$Content
    )

    return ,(Join-TestBytes -Parts @(
        [byte[]]@($Tag),
        (ConvertTo-TestDerLength -Length $Content.Length),
        $Content
    ))
}

function New-TestDerInteger {
    param([Parameter(Mandatory = $true)][byte[]]$UnsignedValue)

    $start = 0
    while ($start -lt ($UnsignedValue.Length - 1) -and
        $UnsignedValue[$start] -eq 0) {
        $start++
    }
    $length = $UnsignedValue.Length - $start
    $trimmed = New-Object byte[] $length
    [Array]::Copy($UnsignedValue, $start, $trimmed, 0, $length)
    $content = if (($UnsignedValue[$start] -band 0x80) -ne 0) {
        Join-TestBytes -Parts @([byte[]]@(0), $trimmed)
    }
    else {
        $trimmed
    }
    return ,(New-TestDerElement -Tag 0x02 `
        -Content $content)
}

function ConvertTo-TestSubjectPublicKeyInfo {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.RSAParameters]$Parameters
    )

    $algorithmIdentifier = [Convert]::FromBase64String(
        "MA0GCSqGSIb3DQEBAQUA")
    $rsaPublicKey = New-TestDerElement -Tag 0x30 -Content (
        Join-TestBytes -Parts @(
            (New-TestDerInteger -UnsignedValue $Parameters.Modulus),
            (New-TestDerInteger -UnsignedValue $Parameters.Exponent)
        ))
    $bitString = New-TestDerElement -Tag 0x03 -Content (
        Join-TestBytes -Parts @([byte[]]@(0), $rsaPublicKey))
    return ,(New-TestDerElement -Tag 0x30 -Content (
        Join-TestBytes -Parts @($algorithmIdentifier, $bitString)))
}

function New-TestReleaseKey {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $rsa = [System.Security.Cryptography.RSACng]::new(3072)
    $publicKeyPath = Join-Path $Directory "$Name.spki.der"
    $spki = ConvertTo-TestSubjectPublicKeyInfo `
        -Parameters $rsa.ExportParameters($false)
    [System.IO.File]::WriteAllBytes($publicKeyPath, $spki)
    return [pscustomobject]@{
        Rsa = $rsa
        PublicKeyPath = $publicKeyPath
        Fingerprint = (Get-FileHash -LiteralPath $publicKeyPath `
            -Algorithm SHA256).Hash
    }
}

function Write-TrustedTestContract {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Fingerprint,
        [string]$Version = "1.0.0",
        [string]$Channel = "stable-v1"
    )

    $contract = [System.IO.File]::ReadAllText(
        $Source,
        [System.Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
    $contract.payload.releaseAttestation.implementationApproved = $true
    $contract.payload.releaseAttestation.expectedVersion = $Version
    $contract.payload.releaseAttestation.expectedChannel = $Channel
    $contract.payload.releaseAttestation.publicKeySha256 = $Fingerprint
    $contract.updates.candidateTrust.descriptorSignature.publicKeySha256 =
        $Fingerprint
    [System.IO.File]::WriteAllText(
        $Destination,
        ($contract | ConvertTo-Json -Depth 100),
        [System.Text.UTF8Encoding]::new($false))
}

function Write-TestReleaseManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadDirectory,
        [string]$Version = "1.0.0",
        [string]$Nonce = "original"
    )

    $manifest = [ordered]@{
        schemaVersion = 1
        application = "WinShot"
        version = $Version
        runtimeIdentifier = "win-x64"
        packageKind = "SIGNED_PRODUCTION"
        selfContained = $true
        trimmed = $false
        singleFile = $false
        sourceProvenance = [ordered]@{
            workingTreeDirty = $false
            buildInputsDirty = $false
            testNonce = $Nonce
        }
        commercialConfiguration = [ordered]@{
            applicationMajor = 1
        }
        files = @()
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $PayloadDirectory "release-manifest.json"),
        ($manifest | ConvertTo-Json -Depth 10),
        [System.Text.UTF8Encoding]::new($false))
}

function Get-TestPayloadFiles {
    param([Parameter(Mandatory = $true)][string]$PayloadDirectory)

    $resolved = [System.IO.Path]::GetFullPath($PayloadDirectory).TrimEnd("\")
    return @(Get-ChildItem -LiteralPath $resolved -File -Recurse |
        Where-Object {
            $relative = $_.FullName.Substring($resolved.Length + 1).Replace("\", "/")
            $relative -cne "activation-release-attestation.v1" -and
            $relative -cne "activation-release-attestation.sig"
        })
}

function Write-TestChecksums {
    param([Parameter(Mandatory = $true)][string]$PayloadDirectory)

    $resolved = [System.IO.Path]::GetFullPath($PayloadDirectory).TrimEnd("\")
    $files = @(Get-TestPayloadFiles -PayloadDirectory $resolved |
        Where-Object { $_.Name -cne "SHA256SUMS" })
    $records = @($files | ForEach-Object {
        $relative = $_.FullName.Substring($resolved.Length + 1).Replace("\", "/")
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $relative"
    })
    [Array]::Sort([string[]]$records, [System.StringComparer]::Ordinal)
    [System.IO.File]::WriteAllText(
        (Join-Path $resolved "SHA256SUMS"),
        (($records -join "`n") + "`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Write-TestAttestation {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadDirectory,
        [Parameter(Mandatory = $true)][System.Security.Cryptography.RSA]$Rsa,
        [string]$Version = "1.0.0",
        [string]$Channel = "stable-v1",
        [int]$PaidMajor = 1
    )

    $resolved = [System.IO.Path]::GetFullPath($PayloadDirectory).TrimEnd("\")
    $files = @(Get-TestPayloadFiles -PayloadDirectory $resolved)
    $filesByPath =
        [System.Collections.Generic.Dictionary[string, object]]::new(
            [System.StringComparer]::Ordinal)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($resolved.Length + 1).Replace("\", "/")
        $filesByPath.Add($relative, $file)
    }
    $sortedPaths = [string[]]@($filesByPath.Keys)
    [Array]::Sort($sortedPaths, [System.StringComparer]::Ordinal)
    $sortedRecords = @($sortedPaths | ForEach-Object {
        $file = $filesByPath[$_]
        "file`t$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)" +
        "`t$($file.Length)`t$_"
    })
    $manifestHash = (Get-FileHash -LiteralPath (
        Join-Path $resolved "release-manifest.json") -Algorithm SHA256).Hash
    $lines = @(
        "WINSHOT-ACTIVATION-RELEASE-ATTESTATION-V1",
        "package-id`tPROVISIONAL.WinShot",
        "version`t$Version",
        "channel`t$Channel",
        "runtime-identifier`twin-x64",
        "paid-major`t$PaidMajor",
        "release-manifest-sha256`t$manifestHash",
        "file-count`t$($sortedRecords.Length)"
    ) + $sortedRecords
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        (($lines -join "`n") + "`n"))
    $attestationPath = Join-Path $resolved "activation-release-attestation.v1"
    [System.IO.File]::WriteAllBytes($attestationPath, $bytes)
    $signature = $Rsa.SignData(
        $bytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pss)
    [System.IO.File]::WriteAllBytes(
        (Join-Path $resolved "activation-release-attestation.sig"),
        $signature)
}

function New-TestPayload {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][System.Security.Cryptography.RSA]$Rsa
    )

    [void][System.IO.Directory]::CreateDirectory($Directory)
    [System.IO.File]::WriteAllBytes(
        (Join-Path $Directory "WinShot.exe"),
        [System.Text.Encoding]::ASCII.GetBytes("bounded-test-app"))
    [System.IO.File]::WriteAllBytes(
        (Join-Path $Directory "WinShot.Core.dll"),
        [System.Text.Encoding]::ASCII.GetBytes("bounded-test-core"))
    Write-TestReleaseManifest -PayloadDirectory $Directory
    Write-TestChecksums -PayloadDirectory $Directory
    Write-TestAttestation -PayloadDirectory $Directory -Rsa $Rsa
}

if (-not (Test-Path -LiteralPath $validatorPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $ContractPath -PathType Leaf)) {
    throw "Installer contract self-test inputs are missing."
}

$temporaryParent = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()).TrimEnd("\")
$temporaryRoot = Join-Path $temporaryParent (
    "WinShotInstallerContractTests-" + [Guid]::NewGuid().ToString("N"))
[void][System.IO.Directory]::CreateDirectory($temporaryRoot)
$primaryReleaseKey = $null
$secondaryReleaseKey = $null

try {
    & $validatorPath -Mode Authoring -ContractPath $ContractPath

    Invoke-ExpectedFailure -Name "current contract production gate" `
        -ExpectedMessagePattern "every readiness, implementation, signing, and approval gate" `
        -Action {
        & $validatorPath -Mode Production -ContractPath $ContractPath
    }

    $primaryReleaseKey = New-TestReleaseKey -Directory $temporaryRoot `
        -Name "primary-release"
    $secondaryReleaseKey = New-TestReleaseKey -Directory $temporaryRoot `
        -Name "secondary-release"
    $trustedContract = Join-Path $temporaryRoot "trusted-inventory-contract.json"
    Write-TrustedTestContract -Source $ContractPath `
        -Destination $trustedContract `
        -Fingerprint $primaryReleaseKey.Fingerprint
    $trustedPayload = Join-Path $temporaryRoot "trusted-payload"
    New-TestPayload -Directory $trustedPayload -Rsa $primaryReleaseKey.Rsa
    & $validatorPath -Mode InventoryTrustTest `
        -ContractPath $trustedContract `
        -PayloadDirectory $trustedPayload `
        -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath

    Invoke-ExpectedFailure -Name "unapproved release attestation" `
        -ExpectedMessagePattern "implementation is not approved" -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $ContractPath `
            -PayloadDirectory $trustedPayload `
            -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath
    }

    $injectedDllPayload = Join-Path $temporaryRoot "injected-dll-payload"
    Copy-Item -LiteralPath $trustedPayload -Destination $injectedDllPayload `
        -Recurse
    [System.IO.File]::WriteAllBytes(
        (Join-Path $injectedDllPayload "injected.dll"),
        [System.Text.Encoding]::ASCII.GetBytes("unattested-dll"))
    Invoke-ExpectedFailure -Name "injected DLL outside signed inventory" `
        -ExpectedMessagePattern "inventory count" -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $trustedContract `
            -PayloadDirectory $injectedDllPayload `
            -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath
    }

    $injectedExePayload = Join-Path $temporaryRoot "injected-exe-payload"
    Copy-Item -LiteralPath $trustedPayload -Destination $injectedExePayload `
        -Recurse
    [System.IO.File]::WriteAllBytes(
        (Join-Path $injectedExePayload "injected.exe"),
        [System.Text.Encoding]::ASCII.GetBytes("unattested-exe"))
    Invoke-ExpectedFailure -Name "injected EXE outside signed inventory" `
        -ExpectedMessagePattern "inventory count" -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $trustedContract `
            -PayloadDirectory $injectedExePayload `
            -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath
    }

    $regeneratedPayload = Join-Path $temporaryRoot "regenerated-local-metadata"
    Copy-Item -LiteralPath $trustedPayload -Destination $regeneratedPayload `
        -Recurse
    [System.IO.File]::WriteAllBytes(
        (Join-Path $regeneratedPayload "WinShot.exe"),
        [System.Text.Encoding]::ASCII.GetBytes("locally-replaced-app"))
    Write-TestReleaseManifest -PayloadDirectory $regeneratedPayload `
        -Nonce "attacker-regenerated"
    Write-TestChecksums -PayloadDirectory $regeneratedPayload
    Invoke-ExpectedFailure `
        -Name "regenerated checksums and unsigned release manifest" `
        -ExpectedMessagePattern "inventory does not match|bind the exact release manifest" `
        -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $trustedContract `
            -PayloadDirectory $regeneratedPayload `
            -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath
    }

    Invoke-ExpectedFailure -Name "wrong release public key" `
        -ExpectedMessagePattern "public key fingerprint" -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $trustedContract `
            -PayloadDirectory $trustedPayload `
            -ReleaseAttestationPublicKeyPath $secondaryReleaseKey.PublicKeyPath
    }

    $wrongFingerprintContract = Join-Path $temporaryRoot `
        "wrong-fingerprint-contract.json"
    Write-TrustedTestContract -Source $ContractPath `
        -Destination $wrongFingerprintContract `
        -Fingerprint $secondaryReleaseKey.Fingerprint
    Invoke-ExpectedFailure -Name "wrong approved release-key fingerprint" `
        -ExpectedMessagePattern "public key fingerprint" -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $wrongFingerprintContract `
            -PayloadDirectory $trustedPayload `
            -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath
    }

    $wrongChannelPayload = Join-Path $temporaryRoot "wrong-channel-payload"
    Copy-Item -LiteralPath $trustedPayload -Destination $wrongChannelPayload `
        -Recurse
    Write-TestAttestation -PayloadDirectory $wrongChannelPayload `
        -Rsa $primaryReleaseKey.Rsa -Channel "beta-v1"
    Invoke-ExpectedFailure -Name "authorized signature with wrong channel" `
        -ExpectedMessagePattern "identity does not match" -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $trustedContract `
            -PayloadDirectory $wrongChannelPayload `
            -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath
    }

    $wrongVersionPayload = Join-Path $temporaryRoot "wrong-version-payload"
    Copy-Item -LiteralPath $trustedPayload -Destination $wrongVersionPayload `
        -Recurse
    Write-TestAttestation -PayloadDirectory $wrongVersionPayload `
        -Rsa $primaryReleaseKey.Rsa -Version "2.0.0"
    Invoke-ExpectedFailure -Name "authorized signature with wrong version" `
        -ExpectedMessagePattern "identity does not match" -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $trustedContract `
            -PayloadDirectory $wrongVersionPayload `
            -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath
    }

    $unsignedEvidencePayload = Join-Path $temporaryRoot "unsigned-evidence-payload"
    Copy-Item -LiteralPath $trustedPayload -Destination $unsignedEvidencePayload `
        -Recurse
    [System.IO.File]::WriteAllBytes(
        (Join-Path $unsignedEvidencePayload "activation-release-attestation.sig"),
        [byte[]]@())
    Invoke-ExpectedFailure -Name "unsigned activation evidence" `
        -ExpectedMessagePattern "signature size" -Action {
        & $validatorPath -Mode InventoryTrustTest `
            -ContractPath $trustedContract `
            -PayloadDirectory $unsignedEvidencePayload `
            -ReleaseAttestationPublicKeyPath $primaryReleaseKey.PublicKeyPath
    }

    $startupTamper = Join-Path $temporaryRoot "startup-opt-out.json"
    Write-TamperedContract -Source $ContractPath -Destination $startupTamper `
        -OldText '"preserveUserOptOut": true' `
        -NewText '"preserveUserOptOut": false'
    Invoke-ExpectedFailure -Name "startup opt-out weakening" `
        -ExpectedMessagePattern "startup\.preserveUserOptOut" -Action {
        & $validatorPath -Mode Authoring -ContractPath $startupTamper
    }

    $descriptorAlgorithmTamper = Join-Path $temporaryRoot "descriptor-algorithm.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $descriptorAlgorithmTamper `
        -OldText ('"productionPackageKind": "SIGNED_PRODUCTION",' +
            $contractLineSeparator +
            '        "signatureAlgorithm": "RSA-PSS-SHA256"') `
        -NewText ('"productionPackageKind": "SIGNED_PRODUCTION",' +
            "`n" +
            '        "signatureAlgorithm": "RSA-PKCS1-SHA256"')
    Invoke-ExpectedFailure -Name "weakened update descriptor signature" `
        -ExpectedMessagePattern "descriptorSignature\.signatureAlgorithm" -Action {
        & $validatorPath -Mode Authoring -ContractPath $descriptorAlgorithmTamper
    }

    $platformEvidenceTamper = Join-Path $temporaryRoot "platform-evidence.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $platformEvidenceTamper `
        -OldText '"requirePlatformAuthenticityEvidence": true' `
        -NewText '"requirePlatformAuthenticityEvidence": false'
    Invoke-ExpectedFailure -Name "missing platform authenticity evidence" `
        -ExpectedMessagePattern "requirePlatformAuthenticityEvidence" -Action {
        & $validatorPath -Mode Authoring -ContractPath $platformEvidenceTamper
    }

    $quiescenceTamper = Join-Path $temporaryRoot "handoff-quiescence.json"
    Write-TamperedContract -Source $ContractPath -Destination $quiescenceTamper `
        -OldText '"retainQuiescenceUntilParentExitOrConfirmedAbort": true' `
        -NewText '"retainQuiescenceUntilParentExitOrConfirmedAbort": false'
    Invoke-ExpectedFailure -Name "handoff releases quiescence early" `
        -ExpectedMessagePattern "retainQuiescenceUntilParentExitOrConfirmedAbort" `
        -Action {
        & $validatorPath -Mode Authoring -ContractPath $quiescenceTamper
    }

    $commitBarrierTamper = Join-Path $temporaryRoot "handoff-commit-barrier.json"
    Write-TamperedContract -Source $ContractPath -Destination $commitBarrierTamper `
        -OldText '"parentShutdownOnlyAfterCommitAccepted": true' `
        -NewText '"parentShutdownOnlyAfterCommitAccepted": false'
    Invoke-ExpectedFailure -Name "handoff exits before commit acceptance" `
        -ExpectedMessagePattern "parentShutdownOnlyAfterCommitAccepted" -Action {
        & $validatorPath -Mode Authoring -ContractPath $commitBarrierTamper
    }

    $candidateBindingTamper = Join-Path $temporaryRoot "handoff-candidate-binding.json"
    Write-TamperedContract -Source $ContractPath -Destination $candidateBindingTamper `
        -OldText '"bindExactVerifiedCandidate": true' `
        -NewText '"bindExactVerifiedCandidate": false'
    Invoke-ExpectedFailure -Name "handoff drops exact candidate binding" `
        -ExpectedMessagePattern "bindExactVerifiedCandidate" -Action {
        & $validatorPath -Mode Authoring -ContractPath $candidateBindingTamper
    }

    $restartRestoreTamper = Join-Path $temporaryRoot "handoff-restart-restore.json"
    Write-TamperedContract -Source $ContractPath -Destination $restartRestoreTamper `
        -OldText '"restoreApplicationRestartOnConfirmedAbort": true' `
        -NewText '"restoreApplicationRestartOnConfirmedAbort": false'
    Invoke-ExpectedFailure -Name "handoff does not restore recovery" `
        -ExpectedMessagePattern "restoreApplicationRestartOnConfirmedAbort" -Action {
        & $validatorPath -Mode Authoring -ContractPath $restartRestoreTamper
    }

    $healthBoundaryTamper = Join-Path $temporaryRoot "preexit-health-check.json"
    Write-TamperedContract -Source $ContractPath -Destination $healthBoundaryTamper `
        -OldText '"preExitCommitIsActivationHealthCheck": false' `
        -NewText '"preExitCommitIsActivationHealthCheck": true'
    Invoke-ExpectedFailure -Name "pre-exit commit treated as activation health" `
        -ExpectedMessagePattern "preExitCommitIsActivationHealthCheck" -Action {
        & $validatorPath -Mode Authoring -ContractPath $healthBoundaryTamper
    }

    $timestampUriTamper = Join-Path $temporaryRoot "unapproved-timestamp-uri.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $timestampUriTamper `
        -OldText '"timestampUri": "http://timestamp.acs.microsoft.com/"' `
        -NewText '"timestampUri": "http://timestamp.example.com/"'
    Invoke-ExpectedFailure -Name "unapproved plaintext timestamp endpoint" `
        -ExpectedMessagePattern "signing[.]timestampUri" -Action {
        & $validatorPath -Mode Authoring -ContractPath $timestampUriTamper
    }

    $profileTypeTamper = Join-Path $temporaryRoot "private-trust-profile.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $profileTypeTamper `
        -OldText '"certificateProfileType": "PublicTrust"' `
        -NewText '"certificateProfileType": "PrivateTrust"'
    Invoke-ExpectedFailure -Name "non-public signing profile" `
        -ExpectedMessagePattern "signing[.]certificateProfileType" -Action {
        & $validatorPath -Mode Authoring -ContractPath $profileTypeTamper
    }

    $genericIdentityTamper = Join-Path $temporaryRoot "generic-signing-identity.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $genericIdentityTamper `
        -OldText '"expectedSubscriberIdentityEku": "UNSET"' `
        -NewText '"expectedSubscriberIdentityEku": "1.3.6.1.4.1.311.97.1.0"'
    Invoke-ExpectedFailure -Name "generic Artifact Signing marker as identity" `
        -ExpectedMessagePattern "expectedSubscriberIdentityEku" -Action {
        & $validatorPath -Mode Authoring -ContractPath $genericIdentityTamper
    }

    $privateIdentityTamper = Join-Path $temporaryRoot "private-signing-identity.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $privateIdentityTamper `
        -OldText '"expectedSubscriberIdentityEku": "UNSET"' `
        -NewText '"expectedSubscriberIdentityEku": "1.3.6.1.4.1.311.97.1.3.1.29433.35007"'
    Invoke-ExpectedFailure -Name "private-trust EKU in Public Trust profile" `
        -ExpectedMessagePattern "expectedSubscriberIdentityEku" -Action {
        & $validatorPath -Mode Authoring -ContractPath $privateIdentityTamper
    }

    $rootCollisionTamper = Join-Path $temporaryRoot "install-data-root-collision.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $rootCollisionTamper `
        -OldText '"packageId": "PROVISIONAL.WinShot"' `
        -NewText '"packageId": "WinShot"'
    Invoke-ExpectedFailure -Name "install and data root collision" `
        -ExpectedMessagePattern "roots must remain distinct" -Action {
        & $validatorPath -Mode Authoring -ContractPath $rootCollisionTamper
    }

    $pointerPathTamper = Join-Path $temporaryRoot "active-pointer-path.json"
    Write-TamperedContract -Source $ContractPath -Destination $pointerPathTamper `
        -OldText '"activeVersionPointer": "{InstallRoot}\\active-version"' `
        -NewText '"activeVersionPointer": "{InstallRoot}\\current"'
    Invoke-ExpectedFailure -Name "active pointer path substitution" `
        -ExpectedMessagePattern "install\.paths\.activeVersionPointer" -Action {
        & $validatorPath -Mode Authoring -ContractPath $pointerPathTamper
    }

    $pointerAtomicityTamper = Join-Path $temporaryRoot "active-pointer-nonatomic.json"
    Write-TamperedContract -Source $ContractPath -Destination $pointerAtomicityTamper `
        -OldText '"activationPointerAtomicCreateOrReplace": true' `
        -NewText '"activationPointerAtomicCreateOrReplace": false'
    Invoke-ExpectedFailure -Name "non-atomic active pointer" `
        -ExpectedMessagePattern "activationPointerAtomicCreateOrReplace" -Action {
        & $validatorPath -Mode Authoring -ContractPath $pointerAtomicityTamper
    }

    $pointerLeaseTamper = Join-Path $temporaryRoot "active-pointer-early-release.json"
    Write-TamperedContract -Source $ContractPath -Destination $pointerLeaseTamper `
        -OldText '"holdActivationPointerThroughProcessCreation": true' `
        -NewText '"holdActivationPointerThroughProcessCreation": false'
    Invoke-ExpectedFailure -Name "active pointer released before process creation" `
        -ExpectedMessagePattern "holdActivationPointerThroughProcessCreation" -Action {
        & $validatorPath -Mode Authoring -ContractPath $pointerLeaseTamper
    }

    $versionScanTamper = Join-Path $temporaryRoot "launcher-version-scan.json"
    Write-TamperedContract -Source $ContractPath -Destination $versionScanTamper `
        -OldText '"scanVersionDirectories": false' `
        -NewText '"scanVersionDirectories": true'
    Invoke-ExpectedFailure -Name "launcher scans version directories" `
        -ExpectedMessagePattern "scanVersionDirectories" -Action {
        & $validatorPath -Mode Authoring -ContractPath $versionScanTamper
    }

    $fallbackTamper = Join-Path $temporaryRoot "launcher-automatic-fallback.json"
    Write-TamperedContract -Source $ContractPath -Destination $fallbackTamper `
        -OldText '"automaticVersionFallback": false' `
        -NewText '"automaticVersionFallback": true'
    Invoke-ExpectedFailure -Name "launcher automatically selects fallback" `
        -ExpectedMessagePattern "automaticVersionFallback" -Action {
        & $validatorPath -Mode Authoring -ContractPath $fallbackTamper
    }

    $broadRootTamper = Join-Path $temporaryRoot "broad-data-root.json"
    Write-TamperedContract -Source $ContractPath -Destination $broadRootTamper `
        -OldText '"localDataRoot": "{LocalAppData}\\{DataDirectoryName}"' `
        -NewText '"localDataRoot": "{LocalAppData}"'
    Invoke-ExpectedFailure -Name "broad application-data root" `
        -ExpectedMessagePattern "dataOwnership\.roots\.localDataRoot" -Action {
        & $validatorPath -Mode Authoring -ContractPath $broadRootTamper
    }

    $recoverableMediaTamper = Join-Path $temporaryRoot "delete-recovery-media.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $recoverableMediaTamper `
        -OldText '"recoverableMedia": "RetainByDefault"' `
        -NewText '"recoverableMedia": "Remove"'
    Invoke-ExpectedFailure -Name "silent recovery-media deletion" `
        -ExpectedMessagePattern "uninstall\.data\.recoverableMedia" -Action {
        & $validatorPath -Mode Authoring -ContractPath $recoverableMediaTamper
    }

    $duplicatePropertyTamper = Join-Path $temporaryRoot "duplicate-property.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $duplicatePropertyTamper `
        -OldText '"recoverableMedia": "RetainByDefault"' `
        -NewText ('"recoverableMedia": "Remove",' +
            [Environment]::NewLine +
            '      "recoverable\u004dedia": "RetainByDefault"')
    Invoke-ExpectedFailure -Name "duplicate JSON property" `
        -ExpectedMessagePattern "duplicate property name" -Action {
        & $validatorPath -Mode Authoring -ContractPath $duplicatePropertyTamper
    }

    $saveTraversalTamper = Join-Path $temporaryRoot "save-directory-traversal.json"
    Write-TamperedContract -Source $ContractPath -Destination $saveTraversalTamper `
        -OldText '"traverseOnUninstall": false' `
        -NewText '"traverseOnUninstall": true'
    Invoke-ExpectedFailure -Name "user save-directory traversal" `
        -ExpectedMessagePattern "dataOwnership\.userSelectedOutput\.traverseOnUninstall" `
        -Action {
        & $validatorPath -Mode Authoring -ContractPath $saveTraversalTamper
    }

    $startupOwnershipTamper = Join-Path $temporaryRoot "unowned-startup-removal.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $startupOwnershipTamper `
        -OldText '"removeOnlyWhenInstallerRecordedCommandMatches": true' `
        -NewText '"removeOnlyWhenInstallerRecordedCommandMatches": false'
    Invoke-ExpectedFailure -Name "unowned startup-value removal" `
        -ExpectedMessagePattern "removeOnlyWhenInstallerRecordedCommandMatches" `
        -Action {
        & $validatorPath -Mode Authoring -ContractPath $startupOwnershipTamper
    }

    $trialKeyTamper = Join-Path $temporaryRoot "recursive-trial-key-removal.json"
    Write-TamperedContract -Source $ContractPath -Destination $trialKeyTamper `
        -OldText '"deleteContainingKeyOnlyWhenEmpty": true' `
        -NewText '"deleteContainingKeyOnlyWhenEmpty": false'
    Invoke-ExpectedFailure -Name "unsafe trial registry-key removal" `
        -ExpectedMessagePattern "deleteContainingKeyOnlyWhenEmpty" -Action {
        & $validatorPath -Mode Authoring -ContractPath $trialKeyTamper
    }

    $privateDataDefaultTamper = Join-Path $temporaryRoot "default-private-removal.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $privateDataDefaultTamper `
        -OldText '"defaultSelected": false' `
        -NewText '"defaultSelected": true'
    Invoke-ExpectedFailure -Name "private-data removal selected by default" `
        -ExpectedMessagePattern "explicitPrivateDataRemoval\.defaultSelected" `
        -Action {
        & $validatorPath -Mode Authoring -ContractPath $privateDataDefaultTamper
    }

    $providerDeactivationTamper = Join-Path $temporaryRoot "automatic-deactivation.json"
    Write-TamperedContract -Source $ContractPath `
        -Destination $providerDeactivationTamper `
        -OldText '"automaticLicenseDeactivation": false' `
        -NewText '"automaticLicenseDeactivation": true'
    Invoke-ExpectedFailure -Name "automatic provider deactivation" `
        -ExpectedMessagePattern "uninstall\.automaticLicenseDeactivation" -Action {
        & $validatorPath -Mode Authoring -ContractPath $providerDeactivationTamper
    }

    $captureTamper = Join-Path $temporaryRoot "capture-deletion.json"
    Write-TamperedContract -Source $ContractPath -Destination $captureTamper `
        -OldText '"userSelectedOutput": "NeverDelete"' `
        -NewText '"userSelectedOutput": "Remove"'
    Invoke-ExpectedFailure -Name "user capture deletion" `
        -ExpectedMessagePattern "uninstall\.data\.userSelectedOutput" -Action {
        & $validatorPath -Mode Authoring -ContractPath $captureTamper
    }

    $unsignedTamper = Join-Path $temporaryRoot "unsigned-sale.json"
    Write-TamperedContract -Source $ContractPath -Destination $unsignedTamper `
        -OldText '"unsignedEngineeringArtifactsAllowed": false' `
        -NewText '"unsignedEngineeringArtifactsAllowed": true'
    Invoke-ExpectedFailure -Name "unsigned engineering payload" `
        -ExpectedMessagePattern "payload\.unsignedEngineeringArtifactsAllowed" `
        -Action {
        & $validatorPath -Mode Authoring -ContractPath $unsignedTamper
    }

    $elevationTamper = Join-Path $temporaryRoot "elevation.json"
    Write-TamperedContract -Source $ContractPath -Destination $elevationTamper `
        -OldText '"elevation": "Never"' `
        -NewText '"elevation": "Required"'
    Invoke-ExpectedFailure -Name "unexpected elevation" `
        -ExpectedMessagePattern "install\.elevation" -Action {
        & $validatorPath -Mode Authoring -ContractPath $elevationTamper
    }

    $resignTamper = Join-Path $temporaryRoot "third-party-resign.json"
    Write-TamperedContract -Source $ContractPath -Destination $resignTamper `
        -OldText '"signatureDisposition": "PreserveUpstreamSignatureState"' `
        -NewText '"signatureDisposition": "PublisherResign"'
    Invoke-ExpectedFailure -Name "third-party publisher re-sign" `
        -ExpectedMessagePattern "thirdPartyDependencies\.signatureDisposition" `
        -Action {
        & $validatorPath -Mode Authoring -ContractPath $resignTamper
    }

    $unknownTamper = Join-Path $temporaryRoot "unknown-property.json"
    Write-TamperedContract -Source $ContractPath -Destination $unknownTamper `
        -OldText '"contractKind": "WinShotInstallerUpdateContract",' `
        -NewText ('"contractKind": "WinShotInstallerUpdateContract",' +
            [Environment]::NewLine + '  "unreviewedBypass": true,')
    Invoke-ExpectedFailure -Name "unknown contract property" `
        -ExpectedMessagePattern "root.*missing or unexpected properties" -Action {
        & $validatorPath -Mode Authoring -ContractPath $unknownTamper
    }

    Assert-ProductionPublisherRejectsUnsignedTestPayload

    Write-Output "PASS_INSTALLER_CONTRACT_SELF_TESTS"
}
finally {
    if ($null -ne $primaryReleaseKey) {
        $primaryReleaseKey.Rsa.Dispose()
    }
    if ($null -ne $secondaryReleaseKey) {
        $secondaryReleaseKey.Rsa.Dispose()
    }
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTemporaryRoot.StartsWith(
            ($temporaryParent + "\WinShotInstallerContractTests-"),
            [System.StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}

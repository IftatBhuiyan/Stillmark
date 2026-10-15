[CmdletBinding()]
param(
    [ValidateSet("Authoring", "InventoryTrustTest", "Production")]
    [string]$Mode = "Authoring",
    [string]$ContractPath,
    [string]$InstallerPath,
    [string]$UpdaterPath,
    [string]$StableLauncherPath,
    [string]$PayloadDirectory,
    [string]$ReleaseAttestationPublicKeyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path (Split-Path -Parent $PSScriptRoot) ".."))
if ([string]::IsNullOrWhiteSpace($ContractPath)) {
    $ContractPath = Join-Path $repositoryRoot `
        "packaging\installer\installer-contract.json"
}

function Assert-ContractCondition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ExactContractValue {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Expected -is [string]) {
        if ($Actual -isnot [string] -or $Actual -cne $Expected) {
            throw "Installer contract '$Name' must be exactly '$Expected'."
        }
        return
    }

    $expectedType = $Expected.GetType()
    $integerTypes = @(
        [sbyte].FullName,
        [byte].FullName,
        [int16].FullName,
        [uint16].FullName,
        [int32].FullName,
        [uint32].FullName,
        [int64].FullName,
        [uint64].FullName
    )
    if ($integerTypes -contains $expectedType.FullName) {
        $actualType = if ($null -ne $Actual) { $Actual.GetType() } else { $null }
        if ($null -eq $actualType -or
            $integerTypes -notcontains $actualType.FullName -or
            [decimal]$Actual -ne [decimal]$Expected) {
            throw "Installer contract '$Name' has an invalid integer value."
        }
        return
    }

    if ($Actual -isnot $expectedType -or -not $Actual.Equals($Expected)) {
        throw "Installer contract '$Name' has an invalid value."
    }
}

function Assert-BooleanContractValue {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Value -isnot [bool]) {
        throw "Installer contract '$Name' must be a boolean."
    }
}

function Assert-BoundedContractText {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$MaximumLength = 256
    )

    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "Installer contract '$Name' must be non-empty text."
    }
    if ($Value.Length -gt $MaximumLength) {
        throw "Installer contract '$Name' exceeds $MaximumLength characters."
    }
    if ($Value.IndexOfAny([char[]]"`0`r`n") -ge 0) {
        throw "Installer contract '$Name' contains a forbidden control character."
    }
}

function Assert-OrdinalContractArray {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        $Actual,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $actualValues = @($Actual)
    if ($actualValues.Count -ne $Expected.Count) {
        throw "Installer contract '$Name' must contain exactly $($Expected.Count) entries."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($actualValues[$index] -isnot [string] -or
            $actualValues[$index] -cne $Expected[$index]) {
            throw "Installer contract '$Name' entry $index is invalid or out of order."
        }
    }
}

function Get-OrdinalSortedContractStrings {
    param([Parameter(Mandatory = $true)][string[]]$Values)

    $sorted = [string[]]@($Values)
    [Array]::Sort($sorted, [System.StringComparer]::Ordinal)
    return $sorted
}

function Assert-ExactContractProperties {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        throw "Installer contract '$Name' must be a JSON object."
    }
    $actual = @($Value.PSObject.Properties.Name | Sort-Object -CaseSensitive)
    $expectedSorted = @($Expected | Sort-Object -CaseSensitive)
    if (($actual -join "`n") -cne ($expectedSorted -join "`n")) {
        throw "Installer contract '$Name' has missing or unexpected properties."
    }
}

function Assert-ExactFilesystemOwnershipGroup {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ExpectedRoot,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedFiles,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedDirectories
    )

    Assert-ExactContractProperties $Value @(
        "root",
        "files",
        "directories"
    ) $Name
    Assert-ExactContractValue $Value.root $ExpectedRoot "$Name.root"
    Assert-OrdinalContractArray $Value.files $ExpectedFiles "$Name.files"
    Assert-OrdinalContractArray $Value.directories $ExpectedDirectories `
        "$Name.directories"
}

function Test-PlaceholderContractValue {
    param([Parameter(Mandatory = $true)][string]$Value)

    return $Value -match "(?i)^(?:UNSET|TBD|CHANGE[_-]?ME)$" -or
        $Value -match "(?i)PROVISIONAL"
}

function Assert-ProductionText {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$MaximumLength = 256,
        [string]$Pattern
    )

    Assert-BoundedContractText -Value $Value -Name $Name -MaximumLength $MaximumLength
    if (Test-PlaceholderContractValue -Value $Value) {
        throw "Installer contract '$Name' still contains a placeholder."
    }
    if (-not [string]::IsNullOrWhiteSpace($Pattern) -and $Value -cnotmatch $Pattern) {
        throw "Installer contract '$Name' has an invalid production format."
    }
}

function Assert-ProductionHttpsUri {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    Assert-ProductionText -Value $Value -Name $Name -MaximumLength 512
    $uri = $null
    if (-not [System.Uri]::TryCreate(
            $Value,
            [System.UriKind]::Absolute,
            [ref]$uri) -or
        $uri.Scheme -cne "https" -or
        [string]::IsNullOrWhiteSpace($uri.Host) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        $uri.IsLoopback) {
        throw "Installer contract '$Name' must be a specific non-loopback HTTPS URI without credentials or a fragment."
    }
}

function Assert-ProductionRfc3161TimestampUri {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Value -ceq "http://timestamp.acs.microsoft.com/") {
        return
    }
    Assert-ProductionHttpsUri -Value $Value -Name $Name
}

function Get-ContractSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        try {
            return [BitConverter]::ToString(
                $sha.ComputeHash($stream)).Replace("-", "")
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
}

function Get-BytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha.ComputeHash($Bytes)).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

function Assert-NoDuplicateJsonProperties {
    param([Parameter(Mandatory = $true)][string]$Json)

    if (-not ("WinShot.ContractValidation.StrictJsonProperties" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinShot.ContractValidation
{
    public static class StrictJsonProperties
    {
        public static void AssertNoDuplicates(string json)
        {
            int index = 0;
            ParseValue(json, ref index, 0);
            SkipWhitespace(json, ref index);
            if (index != json.Length)
                throw new FormatException("Unexpected content after the JSON value.");
        }

        private static void ParseValue(string json, ref int index, int depth)
        {
            if (depth > 64)
                throw new FormatException("JSON nesting exceeds the validation boundary.");
            SkipWhitespace(json, ref index);
            if (index >= json.Length)
                throw new FormatException("Unexpected end of JSON.");

            char current = json[index];
            if (current == '{')
                ParseObject(json, ref index, depth + 1);
            else if (current == '[')
                ParseArray(json, ref index, depth + 1);
            else if (current == '"')
                ParseString(json, ref index);
            else if (current == 't')
                ParseLiteral(json, ref index, "true");
            else if (current == 'f')
                ParseLiteral(json, ref index, "false");
            else if (current == 'n')
                ParseLiteral(json, ref index, "null");
            else
                ParseNumber(json, ref index);
        }

        private static void ParseObject(string json, ref int index, int depth)
        {
            index++;
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}'))
                return;

            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                SkipWhitespace(json, ref index);
                string name = ParseString(json, ref index);
                if (!names.Add(name))
                    throw new InvalidDataException("Duplicate JSON property name.");
                SkipWhitespace(json, ref index);
                Expect(json, ref index, ':');
                ParseValue(json, ref index, depth);
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}'))
                    return;
                Expect(json, ref index, ',');
            }
        }

        private static void ParseArray(string json, ref int index, int depth)
        {
            index++;
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, ']'))
                return;

            while (true)
            {
                ParseValue(json, ref index, depth);
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, ']'))
                    return;
                Expect(json, ref index, ',');
            }
        }

        private static string ParseString(string json, ref int index)
        {
            Expect(json, ref index, '"');
            StringBuilder value = new StringBuilder();
            while (index < json.Length)
            {
                char current = json[index++];
                if (current == '"')
                    return value.ToString();
                if (current < 0x20)
                    throw new FormatException("Unescaped control character in JSON string.");
                if (current != '\\')
                {
                    value.Append(current);
                    continue;
                }

                if (index >= json.Length)
                    throw new FormatException("Incomplete JSON escape.");
                char escaped = json[index++];
                switch (escaped)
                {
                    case '"': value.Append('"'); break;
                    case '\\': value.Append('\\'); break;
                    case '/': value.Append('/'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u':
                        value.Append(ParseUnicodeEscape(json, ref index));
                        break;
                    default:
                        throw new FormatException("Unsupported JSON escape.");
                }
            }

            throw new FormatException("Unterminated JSON string.");
        }

        private static char ParseUnicodeEscape(string json, ref int index)
        {
            if (index + 4 > json.Length)
                throw new FormatException("Incomplete JSON Unicode escape.");
            int value = 0;
            for (int offset = 0; offset < 4; offset++)
            {
                char digit = json[index++];
                value <<= 4;
                if (digit >= '0' && digit <= '9')
                    value += digit - '0';
                else if (digit >= 'A' && digit <= 'F')
                    value += digit - 'A' + 10;
                else if (digit >= 'a' && digit <= 'f')
                    value += digit - 'a' + 10;
                else
                    throw new FormatException("Invalid JSON Unicode escape.");
            }
            return (char)value;
        }

        private static void ParseLiteral(
            string json,
            ref int index,
            string literal)
        {
            if (index + literal.Length > json.Length ||
                !json.Substring(index, literal.Length).Equals(
                    literal,
                    StringComparison.Ordinal))
            {
                throw new FormatException("Invalid JSON literal.");
            }
            index += literal.Length;
        }

        private static void ParseNumber(string json, ref int index)
        {
            int start = index;
            if (TryConsume(json, ref index, '-'))
            {
                if (index >= json.Length)
                    throw new FormatException("Invalid JSON number.");
            }

            if (TryConsume(json, ref index, '0'))
            {
                if (index < json.Length && Char.IsDigit(json[index]))
                    throw new FormatException("Invalid leading zero in JSON number.");
            }
            else
            {
                RequireDigit(json, ref index);
                while (index < json.Length && Char.IsDigit(json[index]))
                    index++;
            }

            if (TryConsume(json, ref index, '.'))
            {
                RequireDigit(json, ref index);
                while (index < json.Length && Char.IsDigit(json[index]))
                    index++;
            }

            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length &&
                    (json[index] == '+' || json[index] == '-'))
                {
                    index++;
                }
                RequireDigit(json, ref index);
                while (index < json.Length && Char.IsDigit(json[index]))
                    index++;
            }

            if (index == start)
                throw new FormatException("Invalid JSON value.");
        }

        private static void RequireDigit(string json, ref int index)
        {
            if (index >= json.Length || !Char.IsDigit(json[index]))
                throw new FormatException("JSON number requires a digit.");
            index++;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length)
            {
                char current = json[index];
                if (current != ' ' && current != '\t' &&
                    current != '\r' && current != '\n')
                {
                    return;
                }
                index++;
            }
        }

        private static bool TryConsume(string json, ref int index, char expected)
        {
            if (index < json.Length && json[index] == expected)
            {
                index++;
                return true;
            }
            return false;
        }

        private static void Expect(string json, ref int index, char expected)
        {
            if (!TryConsume(json, ref index, expected))
                throw new FormatException("Unexpected JSON token.");
        }
    }
}
'@
    }

    try {
        [WinShot.ContractValidation.StrictJsonProperties]::AssertNoDuplicates($Json)
    }
    catch {
        $rootException = $_.Exception
        while ($null -ne $rootException.InnerException) {
            $rootException = $rootException.InnerException
        }
        if ($rootException -is [System.IO.InvalidDataException]) {
            throw "Installer contract JSON contains a duplicate property name."
        }
        throw "Installer contract is not valid JSON."
    }
}

function Get-CheckedInstallerContract {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Installer contract does not exist."
    }
    $file = Get-Item -LiteralPath $resolved
    if ($file.Length -le 0 -or $file.Length -gt 131072) {
        throw "Installer contract size is outside the 1-131072 byte validation boundary."
    }

    $bytes = [System.IO.File]::ReadAllBytes($resolved)
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        throw "Installer contract must be UTF-8 without a byte-order marker."
    }
    try {
        $raw = [System.Text.UTF8Encoding]::new(
            $false,
            $true).GetString($bytes)
    }
    catch {
        throw "Installer contract is not well-formed UTF-8."
    }
    try {
        $contract = $raw | ConvertFrom-Json
    }
    catch {
        throw "Installer contract is not valid JSON."
    }
    Assert-NoDuplicateJsonProperties -Json $raw
    if ($null -eq $contract) {
        throw "Installer contract is empty."
    }

    return [pscustomobject]@{
        Path = $resolved
        Value = $contract
        Sha256 = Get-ContractSha256 -Path $resolved
    }
}

function Assert-ContractStructure {
    param([Parameter(Mandatory = $true)]$Contract)

    Assert-ExactContractProperties $Contract @(
        "schemaVersion",
        "contractKind",
        "readiness",
        "identity",
        "install",
        "startup",
        "dataOwnership",
        "prerequisites",
        "updates",
        "uninstall",
        "payload",
        "signing",
        "approvals"
    ) "root"
    Assert-ExactContractProperties $Contract.readiness @(
        "productionApproved",
        "publicDistributionAllowed"
    ) "readiness"
    Assert-ExactContractProperties $Contract.identity @(
        "approved",
        "applicationName",
        "executableName",
        "packageId",
        "dataDirectoryName",
        "publisherDisplayName",
        "startupValueName"
    ) "identity"
    Assert-ExactContractProperties $Contract.install @(
        "scope",
        "architecture",
        "elevation",
        "technology",
        "implementationApproved",
        "paths",
        "stableLauncherContract"
    ) "install"
    Assert-ExactContractProperties $Contract.install.paths @(
        "installRoot",
        "stableLauncher",
        "activeVersionPointer",
        "versionedPayload",
        "updateStaging"
    ) "install.paths"
    Assert-ExactContractProperties $Contract.install.stableLauncherContract @(
        "installerOwned",
        "survivesVersionReplacement",
        "shortcutsTargetStableLauncher",
        "startupTargetsStableLauncher",
        "activateOnlyAfterValidation",
        "windowsGuiX64",
        "executionLevel",
        "requireValidAuthenticode",
        "requireMatchingPublisherIdentity",
        "activationPointerFormat",
        "activationPointerMaximumBytes",
        "activationPointerAtomicCreateOrReplace",
        "activationPointerFlushedBeforeReplace",
        "holdActivationPointerThroughProcessCreation",
        "scanVersionDirectories",
        "automaticVersionFallback",
        "versionPayloadsImmutable",
        "retainReferencedPayloadUntilLauncherRelease",
        "shellExecute",
        "inheritHandles"
    ) "install.stableLauncherContract"
    Assert-ExactContractProperties $Contract.startup @(
        "registryHive",
        "registryKey",
        "valueName",
        "command",
        "preferenceOwner",
        "preserveUserOptOut",
        "missingRegistrationIsConsent",
        "unconfiguredAction",
        "disabledAction",
        "enabledAction",
        "installAction",
        "updateAction",
        "uninstallAction"
    ) "startup"
    Assert-ExactContractProperties $Contract.dataOwnership @(
        "roots",
        "filesystem",
        "registry",
        "userSelectedOutput"
    ) "dataOwnership"
    Assert-ExactContractProperties $Contract.dataOwnership.roots @(
        "roamingDataRoot",
        "localDataRoot",
        "mustRemainDistinctFromInstallRoot",
        "canonicalExactRootRequired",
        "rejectEnvironmentRoot",
        "rejectReparsePoints",
        "followReparsePoints"
    ) "dataOwnership.roots"
    Assert-ExactContractProperties $Contract.dataOwnership.filesystem @(
        "settings",
        "licenseAndTrialState",
        "managedHistory",
        "clipboardBacking",
        "recoverableMedia",
        "privacySafeLogs",
        "crashRecoveryState"
    ) "dataOwnership.filesystem"
    foreach ($group in @(
        "settings",
        "licenseAndTrialState",
        "managedHistory",
        "clipboardBacking",
        "recoverableMedia",
        "privacySafeLogs",
        "crashRecoveryState")) {
        Assert-ExactContractProperties `
            $Contract.dataOwnership.filesystem.$group @(
                "root",
                "files",
                "directories"
            ) "dataOwnership.filesystem.$group"
    }
    Assert-ExactContractProperties $Contract.dataOwnership.registry @(
        "startup",
        "trial"
    ) "dataOwnership.registry"
    Assert-ExactContractProperties $Contract.dataOwnership.registry.startup @(
        "hive",
        "key",
        "valueName",
        "removeOnlyWhenInstallerRecordedCommandMatches",
        "deleteContainingKey"
    ) "dataOwnership.registry.startup"
    Assert-ExactContractProperties $Contract.dataOwnership.registry.trial @(
        "hive",
        "key",
        "valueName",
        "removeOnlyWithExplicitPrivateDataChoice",
        "deleteContainingKeyOnlyWhenEmpty"
    ) "dataOwnership.registry.trial"
    Assert-ExactContractProperties $Contract.dataOwnership.userSelectedOutput @(
        "source",
        "isProductOwnedRoot",
        "traverseOnUninstall",
        "deleteRecordingWorkspacesOnUninstall",
        "recordingWorkspacePrefix"
    ) "dataOwnership.userSelectedOutput"
    Assert-ExactContractProperties $Contract.prerequisites @(
        "dotnet",
        "visualCppRuntime",
        "mediaFoundation"
    ) "prerequisites"
    Assert-ExactContractProperties $Contract.prerequisites.dotnet @(
        "delivery",
        "externalRuntimeRequired"
    ) "prerequisites.dotnet"
    Assert-ExactContractProperties $Contract.prerequisites.visualCppRuntime @(
        "requiredFor",
        "architecture",
        "delivery",
        "elevation",
        "sourceUri",
        "sha256",
        "expectedSignerSubject",
        "missingBehavior"
    ) "prerequisites.visualCppRuntime"
    Assert-ExactContractProperties $Contract.prerequisites.mediaFoundation @(
        "requiredFor",
        "delivery",
        "bundleSystemDlls",
        "requestElevation",
        "missingBehavior",
        "restartBehavior"
    ) "prerequisites.mediaFoundation"
    Assert-ExactContractProperties $Contract.updates @(
        "technology",
        "implementationApproved",
        "transport",
        "networkFailureBehavior",
        "channels",
        "candidateTrust",
        "staging",
        "apply",
        "rollback"
    ) "updates"
    Assert-ExactContractProperties $Contract.updates.channels @(
        "separatePerPaidMajor",
        "stableManifestUri",
        "betaManifestUri"
    ) "updates.channels"
    Assert-ExactContractProperties $Contract.updates.candidateTrust @(
        "requireSha256",
        "requireValidAuthenticode",
        "requireExactPublisher",
        "requireRfc3161Timestamp",
        "requireMatchingChannel",
        "requireMatchingArchitecture",
        "requireEntitledPaidMajor",
        "descriptorSignature"
    ) "updates.candidateTrust"
    Assert-ExactContractProperties `
        $Contract.updates.candidateTrust.descriptorSignature @(
            "implementationApproved",
            "schemaVersion",
            "descriptorFileName",
            "signatureFileName",
            "releaseManifestFileName",
            "productionPackageKind",
            "signatureAlgorithm",
            "minimumRsaBits",
            "maximumDescriptorBytes",
            "capabilityLifetimeSeconds",
            "requireExactStagingInventory",
            "requireReleaseManifestIdentityBinding",
            "requirePlatformAuthenticityEvidence",
            "publicKeySha256"
        ) "updates.candidateTrust.descriptorSignature"
    Assert-ExactContractProperties $Contract.updates.staging @(
        "path",
        "activateOnlyAfterCompleteValidation",
        "neverExecuteFromDownloadLocation"
    ) "updates.staging"
    Assert-ExactContractProperties $Contract.updates.apply @(
        "deferWhileCaptureActive",
        "deferWhileRecordingActive",
        "deferWhileTranscodeActive",
        "requireExplicitRestartToApply",
        "unregisterApplicationRestartImmediatelyBeforeHandoff",
        "handoffExecutableMustBeSignedAndInstallerOwned",
        "shellExecuteUntrustedArguments",
        "handoff"
    ) "updates.apply"
    Assert-ExactContractProperties $Contract.updates.apply.handoff @(
        "retainQuiescenceUntilParentExitOrConfirmedAbort",
        "parentShutdownOnlyAfterCommitAccepted",
        "restoreApplicationRestartOnConfirmedAbort",
        "twoPhaseCommitRequired",
        "bindExactVerifiedCandidate",
        "maximumWaitSeconds",
        "transport",
        "protocolVersion",
        "implementationApproved"
    ) "updates.apply.handoff"
    Assert-ExactContractProperties $Contract.updates.rollback @(
        "activation",
        "failedActivation",
        "keepPreviousSignedFullPackage",
        "postActivationPolicy",
        "allowUnverifiedDowngrade",
        "activationHealthCheckOwnedByUpdater",
        "preExitCommitIsActivationHealthCheck",
        "implementationApproved"
    ) "updates.rollback"
    Assert-ExactContractProperties $Contract.uninstall @(
        "removeInstallerOwnedBinaries",
        "removeShortcuts",
        "removeUpdaterState",
        "removeOwnedStartupValue",
        "automaticLicenseDeactivation",
        "data",
        "explicitPrivateDataRemoval"
    ) "uninstall"
    Assert-ExactContractProperties $Contract.uninstall.data @(
        "settings",
        "licenseAndTrialState",
        "managedHistory",
        "clipboardBacking",
        "recoverableMedia",
        "privacySafeLogs",
        "crashRecoveryState",
        "userSelectedOutput",
        "privateAppDataRemoval",
        "deleteOutsideDeclaredProductRoots"
    ) "uninstall.data"
    Assert-ExactContractProperties $Contract.uninstall.explicitPrivateDataRemoval @(
        "defaultSelected",
        "retainedGroupsRemoved",
        "removeOnlyDeclaredOwnedData",
        "requiresApplicationStopped",
        "requiresRecoveryWarning",
        "requiresLicenseDeactivationWarning",
        "removesTrialRegistryValue",
        "neverContactsLicenseProvider",
        "neverDeletesUserSelectedOutput"
    ) "uninstall.explicitPrivateDataRemoval"
    Assert-ExactContractProperties $Contract.payload @(
        "unsignedEngineeringArtifactsAllowed",
        "keyGeneratorAllowed",
        "privateSigningMaterialAllowed",
        "debugOrTestPayloadAllowed",
        "productionManifestPackageKind",
        "requiredMetadata",
        "releaseAttestation",
        "publisherSignatureTargets",
        "thirdPartyDependencies"
    ) "payload"
    Assert-ExactContractProperties $Contract.payload.releaseAttestation @(
        "implementationApproved",
        "schemaVersion",
        "attestationKind",
        "attestationFileName",
        "signatureFileName",
        "canonicalFormat",
        "canonicalHeaderOrder",
        "inventoryRecordFields",
        "inventoryPathOrder",
        "inventoryExclusions",
        "publicKeyFileFormat",
        "signatureAlgorithm",
        "minimumRsaBits",
        "maximumAttestationBytes",
        "maximumSignatureBytes",
        "expectedVersion",
        "expectedChannel",
        "expectedRuntimeIdentifier",
        "expectedPaidMajor",
        "requireExactPackageIdentity",
        "requireExactReleaseManifestSha256",
        "requireExactPayloadInventory",
        "requireUpdateDescriptorKeyReuse",
        "publicKeySha256"
    ) "payload.releaseAttestation"
    Assert-ExactContractProperties $Contract.signing @(
        "approved",
        "provider",
        "accountName",
        "certificateProfileName",
        "certificateProfileType",
        "expectedPublisherSubject",
        "expectedSubscriberIdentityEku",
        "rfc3161EvidenceImplementationApproved",
        "timestampUri"
    ) "signing"
    Assert-ExactContractProperties $Contract.approvals @(
        "identity",
        "installerTechnology",
        "signing",
        "prerequisiteBootstrap",
        "updateChannels",
        "uninstallAndRetention",
        "legalPayload"
    ) "approvals"

    Assert-ContractCondition `
        -Condition ([int64]$Contract.schemaVersion -eq 3) `
        -Message "Installer contract schemaVersion must be 3."
    Assert-ExactContractValue $Contract.contractKind `
        "WinShotInstallerUpdateContract" "contractKind"

    Assert-BooleanContractValue $Contract.readiness.productionApproved `
        "readiness.productionApproved"
    Assert-BooleanContractValue $Contract.readiness.publicDistributionAllowed `
        "readiness.publicDistributionAllowed"
    if ($Contract.readiness.publicDistributionAllowed -and
        -not $Contract.readiness.productionApproved) {
        throw "Public distribution cannot be allowed before production approval."
    }

    Assert-BooleanContractValue $Contract.identity.approved "identity.approved"
    Assert-BoundedContractText $Contract.identity.applicationName `
        "identity.applicationName" 64
    Assert-BoundedContractText $Contract.identity.executableName `
        "identity.executableName" 64
    Assert-BoundedContractText $Contract.identity.packageId "identity.packageId" 128
    Assert-BoundedContractText $Contract.identity.dataDirectoryName `
        "identity.dataDirectoryName" 64
    Assert-BoundedContractText $Contract.identity.publisherDisplayName `
        "identity.publisherDisplayName" 256
    Assert-BoundedContractText $Contract.identity.startupValueName `
        "identity.startupValueName" 128
    foreach ($identityValue in @(
        $Contract.identity.applicationName,
        $Contract.identity.executableName,
        $Contract.identity.dataDirectoryName,
        $Contract.identity.startupValueName)) {
        if ($identityValue -notmatch "^[A-Za-z0-9][A-Za-z0-9 ._-]*$") {
            throw "Installer contract contains an unsafe display, executable, or startup identity."
        }
    }
    if ($Contract.identity.packageId -notmatch "^[A-Za-z][A-Za-z0-9.-]+$") {
        throw "Installer contract identity.packageId has an unsafe format."
    }
    if ($Contract.identity.packageId.Equals(
            $Contract.identity.dataDirectoryName,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer contract install and application-data roots must remain distinct."
    }

    Assert-ExactContractValue $Contract.install.scope "PerUser" "install.scope"
    Assert-ExactContractValue $Contract.install.architecture "x64" "install.architecture"
    Assert-ExactContractValue $Contract.install.elevation "Never" "install.elevation"
    Assert-BoundedContractText $Contract.install.technology "install.technology" 64
    Assert-BooleanContractValue $Contract.install.implementationApproved `
        "install.implementationApproved"
    Assert-ExactContractValue $Contract.install.paths.installRoot `
        "{LocalAppData}\{PackageId}" "install.paths.installRoot"
    Assert-ExactContractValue $Contract.install.paths.stableLauncher `
        "{InstallRoot}\{ExecutableName}.exe" "install.paths.stableLauncher"
    Assert-ExactContractValue $Contract.install.paths.activeVersionPointer `
        "{InstallRoot}\active-version" "install.paths.activeVersionPointer"
    Assert-ExactContractValue $Contract.install.paths.versionedPayload `
        "{InstallRoot}\versions\{Version}\{ExecutableName}.exe" `
        "install.paths.versionedPayload"
    Assert-ExactContractValue $Contract.install.paths.updateStaging `
        "{InstallRoot}\updates\staging\{Version}" "install.paths.updateStaging"
    foreach ($property in @(
        "installerOwned",
        "survivesVersionReplacement",
        "shortcutsTargetStableLauncher",
        "startupTargetsStableLauncher",
        "activateOnlyAfterValidation",
        "windowsGuiX64",
        "requireValidAuthenticode",
        "requireMatchingPublisherIdentity",
        "activationPointerAtomicCreateOrReplace",
        "activationPointerFlushedBeforeReplace",
        "holdActivationPointerThroughProcessCreation",
        "versionPayloadsImmutable",
        "retainReferencedPayloadUntilLauncherRelease")) {
        Assert-ExactContractValue `
            $Contract.install.stableLauncherContract.$property $true `
            "install.stableLauncherContract.$property"
    }
    Assert-ExactContractValue `
        $Contract.install.stableLauncherContract.executionLevel `
        "AsInvoker" "install.stableLauncherContract.executionLevel"
    Assert-ExactContractValue `
        $Contract.install.stableLauncherContract.activationPointerFormat `
        "CanonicalAsciiSafeVersionSegment" `
        "install.stableLauncherContract.activationPointerFormat"
    Assert-ExactContractValue `
        $Contract.install.stableLauncherContract.activationPointerMaximumBytes `
        128 "install.stableLauncherContract.activationPointerMaximumBytes"
    foreach ($property in @(
        "scanVersionDirectories",
        "automaticVersionFallback",
        "shellExecute",
        "inheritHandles")) {
        Assert-ExactContractValue `
            $Contract.install.stableLauncherContract.$property $false `
            "install.stableLauncherContract.$property"
    }

    Assert-ExactContractValue $Contract.startup.registryHive "HKCU" `
        "startup.registryHive"
    Assert-ExactContractValue $Contract.startup.registryKey `
        "Software\Microsoft\Windows\CurrentVersion\Run" "startup.registryKey"
    Assert-ExactContractValue $Contract.startup.valueName "{StartupValueName}" `
        "startup.valueName"
    Assert-ExactContractValue $Contract.startup.command `
        "`"{StableLauncher}`" --tray" "startup.command"
    Assert-ExactContractValue $Contract.startup.preferenceOwner `
        "ApplicationPersistedPreference" "startup.preferenceOwner"
    Assert-ExactContractValue $Contract.startup.preserveUserOptOut $true `
        "startup.preserveUserOptOut"
    Assert-ExactContractValue $Contract.startup.missingRegistrationIsConsent $false `
        "startup.missingRegistrationIsConsent"
    Assert-ExactContractValue $Contract.startup.unconfiguredAction "NoMutation" `
        "startup.unconfiguredAction"
    Assert-ExactContractValue $Contract.startup.disabledAction "RemoveOwnedValue" `
        "startup.disabledAction"
    Assert-ExactContractValue $Contract.startup.enabledAction `
        "WriteExpectedOwnedValue" "startup.enabledAction"
    Assert-ExactContractValue $Contract.startup.installAction "NoMutation" `
        "startup.installAction"
    Assert-ExactContractValue $Contract.startup.updateAction `
        "RepairOnlyWhenPersistedEnabled" "startup.updateAction"
    Assert-ExactContractValue $Contract.startup.uninstallAction "RemoveOwnedValue" `
        "startup.uninstallAction"

    Assert-ExactContractValue $Contract.dataOwnership.roots.roamingDataRoot `
        "{RoamingAppData}\{DataDirectoryName}" `
        "dataOwnership.roots.roamingDataRoot"
    Assert-ExactContractValue $Contract.dataOwnership.roots.localDataRoot `
        "{LocalAppData}\{DataDirectoryName}" `
        "dataOwnership.roots.localDataRoot"
    foreach ($property in @(
        "mustRemainDistinctFromInstallRoot",
        "canonicalExactRootRequired",
        "rejectEnvironmentRoot",
        "rejectReparsePoints")) {
        Assert-ExactContractValue $Contract.dataOwnership.roots.$property $true `
            "dataOwnership.roots.$property"
    }
    Assert-ExactContractValue $Contract.dataOwnership.roots.followReparsePoints `
        $false "dataOwnership.roots.followReparsePoints"

    Assert-ExactFilesystemOwnershipGroup `
        $Contract.dataOwnership.filesystem.settings `
        "dataOwnership.filesystem.settings" `
        "{RoamingDataRoot}" `
        @(
            "settings.json",
            "settings.json.tmp",
            "settings.json.corrupt.json",
            "settings.json.corrupt.json.tmp"
        ) @()
    Assert-ExactFilesystemOwnershipGroup `
        $Contract.dataOwnership.filesystem.licenseAndTrialState `
        "dataOwnership.filesystem.licenseAndTrialState" `
        "{LocalDataRoot}" `
        @(
            "license.dat",
            "license.dat.{GuidN}.tmp",
            "trial.dat"
        ) @()
    Assert-ExactFilesystemOwnershipGroup `
        $Contract.dataOwnership.filesystem.managedHistory `
        "dataOwnership.filesystem.managedHistory" `
        "{LocalDataRoot}" `
        @(
            "history.json",
            "history.json.{GuidN}.tmp"
        ) @("History")
    Assert-ExactFilesystemOwnershipGroup `
        $Contract.dataOwnership.filesystem.clipboardBacking `
        "dataOwnership.filesystem.clipboardBacking" `
        "{LocalDataRoot}" @() @("Clipboard")
    Assert-ExactFilesystemOwnershipGroup `
        $Contract.dataOwnership.filesystem.recoverableMedia `
        "dataOwnership.filesystem.recoverableMedia" `
        "{LocalDataRoot}" @() @("tmp")
    Assert-ExactFilesystemOwnershipGroup `
        $Contract.dataOwnership.filesystem.privacySafeLogs `
        "dataOwnership.filesystem.privacySafeLogs" `
        "{LocalDataRoot}" @("error.log", "error.1.log") @()
    Assert-ExactFilesystemOwnershipGroup `
        $Contract.dataOwnership.filesystem.crashRecoveryState `
        "dataOwnership.filesystem.crashRecoveryState" `
        "{LocalDataRoot}" `
        @(
            "recovery-state.json",
            "recovery-state.json.{GuidN}.tmp"
        ) @()

    Assert-ExactContractValue $Contract.dataOwnership.registry.startup.hive `
        "HKCU" "dataOwnership.registry.startup.hive"
    Assert-ExactContractValue $Contract.dataOwnership.registry.startup.key `
        "Software\Microsoft\Windows\CurrentVersion\Run" `
        "dataOwnership.registry.startup.key"
    Assert-ExactContractValue $Contract.dataOwnership.registry.startup.valueName `
        "{StartupValueName}" "dataOwnership.registry.startup.valueName"
    Assert-ExactContractValue `
        $Contract.dataOwnership.registry.startup.removeOnlyWhenInstallerRecordedCommandMatches `
        $true `
        "dataOwnership.registry.startup.removeOnlyWhenInstallerRecordedCommandMatches"
    Assert-ExactContractValue `
        $Contract.dataOwnership.registry.startup.deleteContainingKey `
        $false "dataOwnership.registry.startup.deleteContainingKey"

    Assert-ExactContractValue $Contract.dataOwnership.registry.trial.hive `
        "HKCU" "dataOwnership.registry.trial.hive"
    Assert-ExactContractValue $Contract.dataOwnership.registry.trial.key `
        "Software\{DataDirectoryName}" "dataOwnership.registry.trial.key"
    Assert-ExactContractValue $Contract.dataOwnership.registry.trial.valueName `
        "Tk" "dataOwnership.registry.trial.valueName"
    Assert-ExactContractValue `
        $Contract.dataOwnership.registry.trial.removeOnlyWithExplicitPrivateDataChoice `
        $true `
        "dataOwnership.registry.trial.removeOnlyWithExplicitPrivateDataChoice"
    Assert-ExactContractValue `
        $Contract.dataOwnership.registry.trial.deleteContainingKeyOnlyWhenEmpty `
        $true "dataOwnership.registry.trial.deleteContainingKeyOnlyWhenEmpty"

    Assert-ExactContractValue $Contract.dataOwnership.userSelectedOutput.source `
        "ApplicationPersistedSaveDirectory" `
        "dataOwnership.userSelectedOutput.source"
    foreach ($property in @(
        "isProductOwnedRoot",
        "traverseOnUninstall",
        "deleteRecordingWorkspacesOnUninstall")) {
        Assert-ExactContractValue `
            $Contract.dataOwnership.userSelectedOutput.$property $false `
            "dataOwnership.userSelectedOutput.$property"
    }
    Assert-ExactContractValue `
        $Contract.dataOwnership.userSelectedOutput.recordingWorkspacePrefix `
        ".winshot-recording-" `
        "dataOwnership.userSelectedOutput.recordingWorkspacePrefix"

    Assert-ExactContractValue $Contract.prerequisites.dotnet.delivery `
        "BundledSelfContained" "prerequisites.dotnet.delivery"
    Assert-ExactContractValue $Contract.prerequisites.dotnet.externalRuntimeRequired `
        $false "prerequisites.dotnet.externalRuntimeRequired"
    Assert-ExactContractValue $Contract.prerequisites.visualCppRuntime.requiredFor `
        "RecordingOnly" "prerequisites.visualCppRuntime.requiredFor"
    Assert-ExactContractValue $Contract.prerequisites.visualCppRuntime.architecture `
        "x64" "prerequisites.visualCppRuntime.architecture"
    Assert-ExactContractValue $Contract.prerequisites.visualCppRuntime.delivery `
        "OfficialCentralRedistributableBootstrapper" `
        "prerequisites.visualCppRuntime.delivery"
    Assert-ExactContractValue $Contract.prerequisites.visualCppRuntime.elevation `
        "OnlyAfterExplicitUserConsentForOfficialRedistributable" `
        "prerequisites.visualCppRuntime.elevation"
    foreach ($property in @("sourceUri", "sha256", "expectedSignerSubject")) {
        Assert-BoundedContractText `
            $Contract.prerequisites.visualCppRuntime.$property `
            "prerequisites.visualCppRuntime.$property" 512
    }
    Assert-ExactContractValue `
        $Contract.prerequisites.visualCppRuntime.missingBehavior `
        "ContinueInstallWithRecordingUnavailableAndActionableNotice" `
        "prerequisites.visualCppRuntime.missingBehavior"
    Assert-ExactContractValue $Contract.prerequisites.mediaFoundation.requiredFor `
        "RecordingOnly" "prerequisites.mediaFoundation.requiredFor"
    Assert-ExactContractValue $Contract.prerequisites.mediaFoundation.delivery `
        "WindowsOptionalFeatureGuidance" "prerequisites.mediaFoundation.delivery"
    Assert-ExactContractValue $Contract.prerequisites.mediaFoundation.bundleSystemDlls `
        $false "prerequisites.mediaFoundation.bundleSystemDlls"
    Assert-ExactContractValue $Contract.prerequisites.mediaFoundation.requestElevation `
        $false "prerequisites.mediaFoundation.requestElevation"
    Assert-ExactContractValue $Contract.prerequisites.mediaFoundation.missingBehavior `
        "ContinueInstallWithRecordingUnavailableAndActionableNotice" `
        "prerequisites.mediaFoundation.missingBehavior"
    Assert-ExactContractValue $Contract.prerequisites.mediaFoundation.restartBehavior `
        "NeverForceRestart" "prerequisites.mediaFoundation.restartBehavior"

    Assert-BoundedContractText $Contract.updates.technology "updates.technology" 64
    Assert-BooleanContractValue $Contract.updates.implementationApproved `
        "updates.implementationApproved"
    Assert-ExactContractValue $Contract.updates.transport "HttpsOnly" "updates.transport"
    Assert-ExactContractValue $Contract.updates.networkFailureBehavior `
        "NeverBlockCaptureOrApplicationStartup" "updates.networkFailureBehavior"
    Assert-ExactContractValue $Contract.updates.channels.separatePerPaidMajor $true `
        "updates.channels.separatePerPaidMajor"
    Assert-BoundedContractText $Contract.updates.channels.stableManifestUri `
        "updates.channels.stableManifestUri" 512
    Assert-BoundedContractText $Contract.updates.channels.betaManifestUri `
        "updates.channels.betaManifestUri" 512
    foreach ($property in @(
        "requireSha256",
        "requireValidAuthenticode",
        "requireExactPublisher",
        "requireRfc3161Timestamp",
        "requireMatchingChannel",
        "requireMatchingArchitecture",
        "requireEntitledPaidMajor")) {
        Assert-ExactContractValue $Contract.updates.candidateTrust.$property $true `
            "updates.candidateTrust.$property"
    }
    $descriptorSignature = $Contract.updates.candidateTrust.descriptorSignature
    Assert-BooleanContractValue $descriptorSignature.implementationApproved `
        "updates.candidateTrust.descriptorSignature.implementationApproved"
    Assert-ExactContractValue $descriptorSignature.schemaVersion 1 `
        "updates.candidateTrust.descriptorSignature.schemaVersion"
    Assert-ExactContractValue $descriptorSignature.descriptorFileName `
        "update-candidate.json" `
        "updates.candidateTrust.descriptorSignature.descriptorFileName"
    Assert-ExactContractValue $descriptorSignature.signatureFileName `
        "update-candidate.sig" `
        "updates.candidateTrust.descriptorSignature.signatureFileName"
    Assert-ExactContractValue $descriptorSignature.releaseManifestFileName `
        "release-manifest.json" `
        "updates.candidateTrust.descriptorSignature.releaseManifestFileName"
    Assert-ExactContractValue $descriptorSignature.productionPackageKind `
        "SIGNED_PRODUCTION" `
        "updates.candidateTrust.descriptorSignature.productionPackageKind"
    Assert-ExactContractValue $descriptorSignature.signatureAlgorithm `
        "RSA-PSS-SHA256" `
        "updates.candidateTrust.descriptorSignature.signatureAlgorithm"
    Assert-ExactContractValue $descriptorSignature.minimumRsaBits 3072 `
        "updates.candidateTrust.descriptorSignature.minimumRsaBits"
    Assert-ExactContractValue $descriptorSignature.maximumDescriptorBytes 32768 `
        "updates.candidateTrust.descriptorSignature.maximumDescriptorBytes"
    Assert-ExactContractValue $descriptorSignature.capabilityLifetimeSeconds 600 `
        "updates.candidateTrust.descriptorSignature.capabilityLifetimeSeconds"
    foreach ($property in @(
        "requireExactStagingInventory",
        "requireReleaseManifestIdentityBinding",
        "requirePlatformAuthenticityEvidence")) {
        Assert-ExactContractValue $descriptorSignature.$property $true `
            "updates.candidateTrust.descriptorSignature.$property"
    }
    Assert-BoundedContractText $descriptorSignature.publicKeySha256 `
        "updates.candidateTrust.descriptorSignature.publicKeySha256" 64
    Assert-ExactContractValue $Contract.updates.staging.path "{UpdateStaging}" `
        "updates.staging.path"
    Assert-ExactContractValue `
        $Contract.updates.staging.activateOnlyAfterCompleteValidation $true `
        "updates.staging.activateOnlyAfterCompleteValidation"
    Assert-ExactContractValue $Contract.updates.staging.neverExecuteFromDownloadLocation `
        $true "updates.staging.neverExecuteFromDownloadLocation"
    foreach ($property in @(
        "deferWhileCaptureActive",
        "deferWhileRecordingActive",
        "deferWhileTranscodeActive",
        "requireExplicitRestartToApply",
        "unregisterApplicationRestartImmediatelyBeforeHandoff",
        "handoffExecutableMustBeSignedAndInstallerOwned")) {
        Assert-ExactContractValue $Contract.updates.apply.$property $true `
            "updates.apply.$property"
    }
    Assert-ExactContractValue $Contract.updates.apply.shellExecuteUntrustedArguments `
        $false "updates.apply.shellExecuteUntrustedArguments"
    foreach ($property in @(
        "retainQuiescenceUntilParentExitOrConfirmedAbort",
        "parentShutdownOnlyAfterCommitAccepted",
        "restoreApplicationRestartOnConfirmedAbort",
        "twoPhaseCommitRequired",
        "bindExactVerifiedCandidate")) {
        Assert-ExactContractValue $Contract.updates.apply.handoff.$property $true `
            "updates.apply.handoff.$property"
    }
    Assert-ExactContractValue $Contract.updates.apply.handoff.maximumWaitSeconds 30 `
        "updates.apply.handoff.maximumWaitSeconds"
    Assert-BoundedContractText $Contract.updates.apply.handoff.transport `
        "updates.apply.handoff.transport" 64
    Assert-BoundedContractText $Contract.updates.apply.handoff.protocolVersion `
        "updates.apply.handoff.protocolVersion" 64
    Assert-BooleanContractValue `
        $Contract.updates.apply.handoff.implementationApproved `
        "updates.apply.handoff.implementationApproved"
    Assert-ExactContractValue $Contract.updates.rollback.activation `
        "AtomicActiveVersionPointerSwitchAfterHealthCheck" `
        "updates.rollback.activation"
    Assert-ExactContractValue $Contract.updates.rollback.failedActivation `
        "RestorePriorActiveVersionPointer" "updates.rollback.failedActivation"
    Assert-ExactContractValue `
        $Contract.updates.rollback.keepPreviousSignedFullPackage $true `
        "updates.rollback.keepPreviousSignedFullPackage"
    Assert-ExactContractValue $Contract.updates.rollback.postActivationPolicy `
        "SignedHigherVersionForwardFixOnly" "updates.rollback.postActivationPolicy"
    Assert-ExactContractValue $Contract.updates.rollback.allowUnverifiedDowngrade `
        $false "updates.rollback.allowUnverifiedDowngrade"
    Assert-ExactContractValue `
        $Contract.updates.rollback.activationHealthCheckOwnedByUpdater $true `
        "updates.rollback.activationHealthCheckOwnedByUpdater"
    Assert-ExactContractValue `
        $Contract.updates.rollback.preExitCommitIsActivationHealthCheck $false `
        "updates.rollback.preExitCommitIsActivationHealthCheck"
    Assert-BooleanContractValue $Contract.updates.rollback.implementationApproved `
        "updates.rollback.implementationApproved"

    foreach ($property in @(
        "removeInstallerOwnedBinaries",
        "removeShortcuts",
        "removeUpdaterState",
        "removeOwnedStartupValue")) {
        Assert-ExactContractValue $Contract.uninstall.$property $true `
            "uninstall.$property"
    }
    Assert-ExactContractValue $Contract.uninstall.automaticLicenseDeactivation $false `
        "uninstall.automaticLicenseDeactivation"
    Assert-ExactContractValue $Contract.uninstall.data.settings "RetainByDefault" `
        "uninstall.data.settings"
    Assert-ExactContractValue $Contract.uninstall.data.licenseAndTrialState `
        "RetainByDefault" "uninstall.data.licenseAndTrialState"
    Assert-ExactContractValue $Contract.uninstall.data.managedHistory `
        "RetainByDefault" "uninstall.data.managedHistory"
    Assert-ExactContractValue $Contract.uninstall.data.clipboardBacking `
        "Remove" "uninstall.data.clipboardBacking"
    Assert-ExactContractValue $Contract.uninstall.data.recoverableMedia `
        "RetainByDefault" "uninstall.data.recoverableMedia"
    Assert-ExactContractValue $Contract.uninstall.data.privacySafeLogs `
        "Remove" "uninstall.data.privacySafeLogs"
    Assert-ExactContractValue $Contract.uninstall.data.crashRecoveryState `
        "Remove" "uninstall.data.crashRecoveryState"
    Assert-ExactContractValue $Contract.uninstall.data.userSelectedOutput `
        "NeverDelete" "uninstall.data.userSelectedOutput"
    Assert-ExactContractValue $Contract.uninstall.data.privateAppDataRemoval `
        "SeparateExplicitUncheckedChoice" "uninstall.data.privateAppDataRemoval"
    Assert-ExactContractValue $Contract.uninstall.data.deleteOutsideDeclaredProductRoots `
        $false "uninstall.data.deleteOutsideDeclaredProductRoots"
    Assert-ExactContractValue `
        $Contract.uninstall.explicitPrivateDataRemoval.defaultSelected `
        $false "uninstall.explicitPrivateDataRemoval.defaultSelected"
    Assert-OrdinalContractArray `
        $Contract.uninstall.explicitPrivateDataRemoval.retainedGroupsRemoved @(
            "settings",
            "licenseAndTrialState",
            "managedHistory",
            "recoverableMedia"
        ) "uninstall.explicitPrivateDataRemoval.retainedGroupsRemoved"
    foreach ($property in @(
        "removeOnlyDeclaredOwnedData",
        "requiresApplicationStopped",
        "requiresRecoveryWarning",
        "requiresLicenseDeactivationWarning",
        "removesTrialRegistryValue",
        "neverContactsLicenseProvider",
        "neverDeletesUserSelectedOutput")) {
        Assert-ExactContractValue `
            $Contract.uninstall.explicitPrivateDataRemoval.$property $true `
            "uninstall.explicitPrivateDataRemoval.$property"
    }

    foreach ($property in @(
        "unsignedEngineeringArtifactsAllowed",
        "keyGeneratorAllowed",
        "privateSigningMaterialAllowed",
        "debugOrTestPayloadAllowed")) {
        Assert-ExactContractValue $Contract.payload.$property $false "payload.$property"
    }
    Assert-ExactContractValue $Contract.payload.productionManifestPackageKind `
        "SIGNED_PRODUCTION" "payload.productionManifestPackageKind"
    Assert-OrdinalContractArray $Contract.payload.requiredMetadata @(
        "activation-release-attestation.v1",
        "activation-release-attestation.sig",
        "EULA.txt",
        "PRIVACY-NOTICE.txt",
        "RELEASE-NOTES.txt",
        "SBOM.cdx.json",
        "SHA256SUMS",
        "THIRD-PARTY-NOTICES.txt",
        "release-manifest.json"
    ) "payload.requiredMetadata"
    $releaseAttestation = $Contract.payload.releaseAttestation
    Assert-BooleanContractValue $releaseAttestation.implementationApproved `
        "payload.releaseAttestation.implementationApproved"
    Assert-ExactContractValue $releaseAttestation.schemaVersion 1 `
        "payload.releaseAttestation.schemaVersion"
    Assert-ExactContractValue $releaseAttestation.attestationKind `
        "WinShotActivationReleaseAttestation" `
        "payload.releaseAttestation.attestationKind"
    Assert-ExactContractValue $releaseAttestation.attestationFileName `
        "activation-release-attestation.v1" `
        "payload.releaseAttestation.attestationFileName"
    Assert-ExactContractValue $releaseAttestation.signatureFileName `
        "activation-release-attestation.sig" `
        "payload.releaseAttestation.signatureFileName"
    Assert-ExactContractValue $releaseAttestation.canonicalFormat `
        "StrictUtf8NoBomLfFinalLfTabDelimitedV1" `
        "payload.releaseAttestation.canonicalFormat"
    Assert-OrdinalContractArray $releaseAttestation.canonicalHeaderOrder @(
        "WINSHOT-ACTIVATION-RELEASE-ATTESTATION-V1",
        "package-id",
        "version",
        "channel",
        "runtime-identifier",
        "paid-major",
        "release-manifest-sha256",
        "file-count"
    ) "payload.releaseAttestation.canonicalHeaderOrder"
    Assert-OrdinalContractArray $releaseAttestation.inventoryRecordFields @(
        "file",
        "sha256",
        "bytes",
        "path"
    ) "payload.releaseAttestation.inventoryRecordFields"
    Assert-ExactContractValue $releaseAttestation.inventoryPathOrder `
        "Ordinal" "payload.releaseAttestation.inventoryPathOrder"
    Assert-OrdinalContractArray $releaseAttestation.inventoryExclusions @(
        "activation-release-attestation.v1",
        "activation-release-attestation.sig"
    ) "payload.releaseAttestation.inventoryExclusions"
    Assert-ExactContractValue $releaseAttestation.publicKeyFileFormat `
        "DERSubjectPublicKeyInfo" `
        "payload.releaseAttestation.publicKeyFileFormat"
    Assert-ExactContractValue $releaseAttestation.signatureAlgorithm `
        "RSA-PSS-SHA256" "payload.releaseAttestation.signatureAlgorithm"
    Assert-ExactContractValue $releaseAttestation.minimumRsaBits 3072 `
        "payload.releaseAttestation.minimumRsaBits"
    Assert-ExactContractValue $releaseAttestation.maximumAttestationBytes 4194304 `
        "payload.releaseAttestation.maximumAttestationBytes"
    Assert-ExactContractValue $releaseAttestation.maximumSignatureBytes 1024 `
        "payload.releaseAttestation.maximumSignatureBytes"
    Assert-BoundedContractText $releaseAttestation.expectedVersion `
        "payload.releaseAttestation.expectedVersion" 128
    Assert-BoundedContractText $releaseAttestation.expectedChannel `
        "payload.releaseAttestation.expectedChannel" 128
    Assert-ExactContractValue $releaseAttestation.expectedRuntimeIdentifier `
        "win-x64" "payload.releaseAttestation.expectedRuntimeIdentifier"
    Assert-ExactContractValue $releaseAttestation.expectedPaidMajor 1 `
        "payload.releaseAttestation.expectedPaidMajor"
    foreach ($property in @(
        "requireExactPackageIdentity",
        "requireExactReleaseManifestSha256",
        "requireExactPayloadInventory",
        "requireUpdateDescriptorKeyReuse")) {
        Assert-ExactContractValue $releaseAttestation.$property $true `
            "payload.releaseAttestation.$property"
    }
    Assert-BoundedContractText $releaseAttestation.publicKeySha256 `
        "payload.releaseAttestation.publicKeySha256" 64
    if (-not (Test-PlaceholderContractValue -Value $releaseAttestation.expectedVersion) -and
        $releaseAttestation.expectedVersion -cnotmatch
            "^[1-9][0-9]*[.][0-9]+[.][0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$") {
        throw "Installer contract 'payload.releaseAttestation.expectedVersion' has an invalid release version."
    }
    if (-not (Test-PlaceholderContractValue -Value $releaseAttestation.expectedVersion) -and
        [int]($releaseAttestation.expectedVersion.Split(".")[0]) -ne
            $releaseAttestation.expectedPaidMajor) {
        throw "Installer contract release attestation version must match expectedPaidMajor."
    }
    if (-not (Test-PlaceholderContractValue -Value $releaseAttestation.expectedChannel) -and
        $releaseAttestation.expectedChannel -cnotmatch
            "^[a-z0-9][a-z0-9.-]{0,63}$") {
        throw "Installer contract 'payload.releaseAttestation.expectedChannel' has an invalid channel."
    }
    if (-not (Test-PlaceholderContractValue -Value $releaseAttestation.publicKeySha256) -and
        $releaseAttestation.publicKeySha256 -cnotmatch "^[0-9A-F]{64}$") {
        throw "Installer contract 'payload.releaseAttestation.publicKeySha256' has an invalid fingerprint."
    }
    if (-not (Test-PlaceholderContractValue -Value $releaseAttestation.publicKeySha256) -and
        -not (Test-PlaceholderContractValue -Value $descriptorSignature.publicKeySha256) -and
        $releaseAttestation.publicKeySha256 -cne $descriptorSignature.publicKeySha256) {
        throw "Installer contract release attestation and update descriptor must reuse the exact approved release key."
    }
    Assert-OrdinalContractArray $Contract.payload.publisherSignatureTargets @(
        "WinShot.Capture.dll",
        "WinShot.Core.dll",
        "WinShot.Interop.dll",
        "WinShot.Licensing.dll",
        "WinShot.dll",
        "WinShot.exe",
        "WinShot.Startup.exe"
    ) "payload.publisherSignatureTargets"
    $thirdPartyDependencies = @($Contract.payload.thirdPartyDependencies)
    if ($thirdPartyDependencies.Count -ne 1) {
        throw "Installer contract must contain exactly one current third-party dependency entry."
    }
    $recorderDependency = $thirdPartyDependencies[0]
    Assert-ExactContractProperties $recorderDependency @(
        "approved",
        "payloadFile",
        "componentName",
        "sourcePackageId",
        "sourcePackageVersion",
        "sourcePackageSha256",
        "payloadSha256",
        "trustDecision",
        "signatureDisposition",
        "expectedSignerSubject",
        "expectedLeafThumbprint",
        "license",
        "noticeFile",
        "authorship"
    ) "payload.thirdPartyDependencies[0]"
    Assert-BooleanContractValue $recorderDependency.approved `
        "payload.thirdPartyDependencies.approved"
    Assert-ExactContractValue $recorderDependency.payloadFile `
        "ScreenRecorderLib.dll" "payload.thirdPartyDependencies.payloadFile"
    Assert-ExactContractValue $recorderDependency.componentName `
        "ScreenRecorderLib" "payload.thirdPartyDependencies.componentName"
    Assert-ExactContractValue $recorderDependency.sourcePackageId `
        "ScreenRecorderLib" "payload.thirdPartyDependencies.sourcePackageId"
    Assert-ExactContractValue $recorderDependency.sourcePackageVersion `
        "6.3.0" "payload.thirdPartyDependencies.sourcePackageVersion"
    foreach ($property in @(
        "sourcePackageSha256",
        "payloadSha256",
        "trustDecision",
        "expectedSignerSubject",
        "expectedLeafThumbprint")) {
        Assert-BoundedContractText $recorderDependency.$property `
            "payload.thirdPartyDependencies.$property" 512
    }
    Assert-ExactContractValue $recorderDependency.signatureDisposition `
        "PreserveUpstreamSignatureState" `
        "payload.thirdPartyDependencies.signatureDisposition"
    Assert-ExactContractValue $recorderDependency.license `
        "MIT" "payload.thirdPartyDependencies.license"
    Assert-ExactContractValue $recorderDependency.noticeFile `
        "THIRD-PARTY-NOTICES.txt" "payload.thirdPartyDependencies.noticeFile"
    Assert-ExactContractValue $recorderDependency.authorship `
        "ThirdPartyNotWinShotOwned" "payload.thirdPartyDependencies.authorship"

    Assert-BooleanContractValue $Contract.signing.approved "signing.approved"
    Assert-ExactContractValue $Contract.signing.provider "AzureArtifactSigning" `
        "signing.provider"
    Assert-ExactContractValue $Contract.signing.certificateProfileType `
        "PublicTrust" "signing.certificateProfileType"
    foreach ($property in @(
        "accountName",
        "certificateProfileName",
        "certificateProfileType",
        "expectedPublisherSubject",
        "expectedSubscriberIdentityEku",
        "timestampUri")) {
        Assert-BoundedContractText $Contract.signing.$property "signing.$property" 512
    }
    Assert-BooleanContractValue `
        $Contract.signing.rfc3161EvidenceImplementationApproved `
        "signing.rfc3161EvidenceImplementationApproved"
    if (-not (Test-PlaceholderContractValue `
            -Value $Contract.signing.expectedSubscriberIdentityEku) -and
        $Contract.signing.expectedSubscriberIdentityEku -cnotmatch
            "^1[.]3[.]6[.]1[.]4[.]1[.]311[.]97[.](?!1[.]0$)(?!1[.]3[.]1[.])(?!1[.]4[.]1[.])[0-9]+([.][0-9]+)+$") {
        throw "Installer contract 'signing.expectedSubscriberIdentityEku' has an invalid Artifact Signing identity."
    }
    if (-not (Test-PlaceholderContractValue -Value $Contract.signing.timestampUri)) {
        Assert-ProductionRfc3161TimestampUri `
            $Contract.signing.timestampUri "signing.timestampUri"
    }

    foreach ($property in @(
        "identity",
        "installerTechnology",
        "signing",
        "prerequisiteBootstrap",
        "updateChannels",
        "uninstallAndRetention",
        "legalPayload")) {
        Assert-BooleanContractValue $Contract.approvals.$property "approvals.$property"
    }
}

function Assert-ProductionContract {
    param([Parameter(Mandatory = $true)]$Contract)

    foreach ($approval in @(
        $Contract.readiness.productionApproved,
        $Contract.readiness.publicDistributionAllowed,
        $Contract.identity.approved,
        $Contract.install.implementationApproved,
        $Contract.updates.implementationApproved,
        $Contract.updates.apply.handoff.implementationApproved,
        $Contract.updates.rollback.implementationApproved,
        $Contract.signing.approved,
        $Contract.approvals.identity,
        $Contract.approvals.installerTechnology,
        $Contract.approvals.signing,
        $Contract.approvals.prerequisiteBootstrap,
        $Contract.approvals.updateChannels,
        $Contract.approvals.uninstallAndRetention,
        $Contract.approvals.legalPayload)) {
        if ($approval -ne $true) {
            throw "Production validation refused: every readiness, implementation, signing, and approval gate must be explicitly true."
        }
    }

    Assert-ProductionText $Contract.identity.applicationName `
        "identity.applicationName" 64 "^[A-Za-z0-9][A-Za-z0-9 ._-]*$"
    Assert-ProductionText $Contract.identity.executableName `
        "identity.executableName" 64 "^[A-Za-z0-9][A-Za-z0-9._-]*$"
    Assert-ProductionText $Contract.identity.packageId `
        "identity.packageId" 128 "^[A-Za-z][A-Za-z0-9.-]+$"
    Assert-ProductionText $Contract.identity.dataDirectoryName `
        "identity.dataDirectoryName" 64 "^[A-Za-z0-9][A-Za-z0-9 ._-]*$"
    Assert-ProductionText $Contract.identity.publisherDisplayName `
        "identity.publisherDisplayName" 256
    Assert-ProductionText $Contract.identity.startupValueName `
        "identity.startupValueName" 128 "^[A-Za-z0-9][A-Za-z0-9 ._-]*$"
    Assert-ProductionText $Contract.install.technology "install.technology" 64
    Assert-ProductionText $Contract.updates.technology "updates.technology" 64

    Assert-ProductionHttpsUri `
        $Contract.prerequisites.visualCppRuntime.sourceUri `
        "prerequisites.visualCppRuntime.sourceUri"
    Assert-ProductionText $Contract.prerequisites.visualCppRuntime.sha256 `
        "prerequisites.visualCppRuntime.sha256" 64 "^[0-9A-F]{64}$"
    Assert-ProductionText `
        $Contract.prerequisites.visualCppRuntime.expectedSignerSubject `
        "prerequisites.visualCppRuntime.expectedSignerSubject" 256
    Assert-ProductionHttpsUri $Contract.updates.channels.stableManifestUri `
        "updates.channels.stableManifestUri"
    Assert-ProductionHttpsUri $Contract.updates.channels.betaManifestUri `
        "updates.channels.betaManifestUri"
    if ($Contract.updates.candidateTrust.descriptorSignature.implementationApproved -ne $true) {
        throw "Production validation refused: update descriptor signature implementation is not approved."
    }
    Assert-ProductionText $Contract.updates.apply.handoff.transport `
        "updates.apply.handoff.transport" 64 "^[A-Za-z0-9][A-Za-z0-9._-]*$"
    Assert-ProductionText $Contract.updates.apply.handoff.protocolVersion `
        "updates.apply.handoff.protocolVersion" 64 "^[A-Za-z0-9][A-Za-z0-9._-]*$"
    Assert-ProductionText `
        $Contract.updates.candidateTrust.descriptorSignature.publicKeySha256 `
        "updates.candidateTrust.descriptorSignature.publicKeySha256" `
        64 "^[0-9A-F]{64}$"
    $releaseAttestation = $Contract.payload.releaseAttestation
    if ($releaseAttestation.implementationApproved -ne $true) {
        throw "Production validation refused: release attestation implementation is not approved."
    }
    Assert-ProductionText $releaseAttestation.expectedVersion `
        "payload.releaseAttestation.expectedVersion" 128 `
        "^[1-9][0-9]*[.][0-9]+[.][0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$"
    Assert-ProductionText $releaseAttestation.expectedChannel `
        "payload.releaseAttestation.expectedChannel" 64 `
        "^[a-z0-9][a-z0-9.-]{0,63}$"
    Assert-ProductionText $releaseAttestation.publicKeySha256 `
        "payload.releaseAttestation.publicKeySha256" 64 "^[0-9A-F]{64}$"
    if ($releaseAttestation.publicKeySha256 -cne
        $Contract.updates.candidateTrust.descriptorSignature.publicKeySha256) {
        throw "Production release attestation must reuse the exact approved update descriptor release key."
    }

    Assert-ProductionText $Contract.signing.accountName `
        "signing.accountName" 128 "^[A-Za-z0-9][A-Za-z0-9_-]*$"
    Assert-ProductionText $Contract.signing.certificateProfileName `
        "signing.certificateProfileName" 128 "^[A-Za-z0-9][A-Za-z0-9_-]*$"
    Assert-ExactContractValue $Contract.signing.certificateProfileType `
        "PublicTrust" "signing.certificateProfileType"
    Assert-ProductionText $Contract.signing.expectedPublisherSubject `
        "signing.expectedPublisherSubject" 512
    Assert-ProductionText $Contract.signing.expectedSubscriberIdentityEku `
        "signing.expectedSubscriberIdentityEku" 256 `
        "^1[.]3[.]6[.]1[.]4[.]1[.]311[.]97[.](?!1[.]0$)(?!1[.]3[.]1[.])(?!1[.]4[.]1[.])[0-9]+([.][0-9]+)+$"
    if ($Contract.signing.rfc3161EvidenceImplementationApproved -ne $true) {
        throw "Production validation refused: rigorous RFC3161 signature evidence validation is not approved."
    }
    Assert-ProductionRfc3161TimestampUri `
        $Contract.signing.timestampUri "signing.timestampUri"

    $recorderDependency = @($Contract.payload.thirdPartyDependencies)[0]
    if ($recorderDependency.approved -ne $true) {
        throw "Production validation refused: the third-party recorder dependency trust decision is not approved."
    }
    Assert-ProductionText $recorderDependency.sourcePackageSha256 `
        "payload.thirdPartyDependencies.sourcePackageSha256" 64 "^[0-9A-F]{64}$"
    Assert-ProductionText $recorderDependency.payloadSha256 `
        "payload.thirdPartyDependencies.payloadSha256" 64 "^[0-9A-F]{64}$"
    if ($recorderDependency.trustDecision -cnotin @(
            "ReviewedUnsigned",
            "ValidUpstreamAuthenticode")) {
        throw "Production third-party trustDecision must be ReviewedUnsigned or ValidUpstreamAuthenticode."
    }
    if ($recorderDependency.trustDecision -ceq "ReviewedUnsigned") {
        Assert-ExactContractValue $recorderDependency.expectedSignerSubject `
            "UNSET" "payload.thirdPartyDependencies.expectedSignerSubject"
        Assert-ExactContractValue $recorderDependency.expectedLeafThumbprint `
            "UNSET" "payload.thirdPartyDependencies.expectedLeafThumbprint"
    }
    else {
        Assert-ProductionText $recorderDependency.expectedSignerSubject `
            "payload.thirdPartyDependencies.expectedSignerSubject" 512
        Assert-ProductionText $recorderDependency.expectedLeafThumbprint `
            "payload.thirdPartyDependencies.expectedLeafThumbprint" 40 `
            "^[0-9A-F]{40}$"
    }
}

function Resolve-ExistingInstallerFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$AllowedExtensions
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Production validation requires $Name."
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Production $Name does not exist."
    }
    if ([System.IO.Path]::GetExtension($resolved) -cnotin $AllowedExtensions) {
        throw "Production $Name has an unsupported file extension."
    }
    return $resolved
}

function Assert-TrustedInstallerSignature {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Signing
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "Production signature validation failed for '$([System.IO.Path]::GetFileName($Path))'."
    }
    if ($signature.SignerCertificate.Subject -cne $Signing.expectedPublisherSubject) {
        throw "Production signature publisher does not match the approved contract."
    }
    $ekuValues = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($extension in $signature.SignerCertificate.Extensions) {
        if ($extension -isnot
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            continue
        }
        foreach ($oid in $extension.EnhancedKeyUsages) {
            if ([string]::IsNullOrWhiteSpace($oid.Value) -or
                -not $ekuValues.Add($oid.Value)) {
                throw "Production signature contains an invalid or duplicate EKU."
            }
        }
    }
    foreach ($requiredEku in @(
        "1.3.6.1.5.5.7.3.3",
        "1.3.6.1.4.1.311.97.1.0",
        $Signing.expectedSubscriberIdentityEku)) {
        if (-not $ekuValues.Contains($requiredEku)) {
            throw "Production signature is missing the approved Artifact Signing identity EKU."
        }
    }
    $subscriberEkus = @($ekuValues | Where-Object {
        $_.StartsWith(
            "1.3.6.1.4.1.311.97.",
            [System.StringComparison]::Ordinal) -and
        $_ -cne "1.3.6.1.4.1.311.97.1.0"
    })
    if ($subscriberEkus.Count -ne 1 -or
        $subscriberEkus[0] -cne $Signing.expectedSubscriberIdentityEku) {
        throw "Production signature has an ambiguous Artifact Signing subscriber identity."
    }

    throw ("Production signature validation refused: this PowerShell boundary does not " +
        "yet parse and cryptographically bind the primary PE signature to an RFC3161 token.")
}

function Assert-StableLauncherBinary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Contract
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 512 -or $bytes.Length -gt 1048576 -or
        $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "Production stable launcher is not a bounded MZ executable."
    }
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 96 -ge $bytes.Length -or
        $bytes[$peOffset] -ne 0x50 -or
        $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or
        $bytes[$peOffset + 3] -ne 0) {
        throw "Production stable launcher has an invalid PE header."
    }
    if ([System.BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) {
        throw "Production stable launcher is not x64."
    }
    $optionalHeader = $peOffset + 24
    if ([System.BitConverter]::ToUInt16($bytes, $optionalHeader) -ne 0x20B -or
        [System.BitConverter]::ToUInt16($bytes, $optionalHeader + 68) -ne 2) {
        throw "Production stable launcher must be a Windows GUI PE32+ image."
    }
    $manifestText = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($manifestText -cnotmatch
        '<requestedExecutionLevel level="asInvoker" uiAccess="false"') {
        throw "Production stable launcher must embed exact asInvoker execution policy."
    }
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($version.ProductName -cne $Contract.identity.applicationName) {
        throw "Production stable launcher product metadata does not match the contract."
    }
}

function Get-BoundedPayloadFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd("\")
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw "Production payload directory does not exist."
    }
    $rootItem = Get-Item -LiteralPath $resolvedRoot
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Production payload root cannot be a reparse point."
    }

    $files = @(Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse)
    if ($files.Count -le 0 -or $files.Count -gt 4096) {
        throw "Production payload file count is outside the 1-4096 boundary."
    }
    [long]$totalBytes = 0
    foreach ($file in $files) {
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Production payload cannot contain a reparse point."
        }
        $relative = $file.FullName.Substring($resolvedRoot.Length + 1).Replace("\", "/")
        if ($relative.Length -gt 512 -or $relative.StartsWith("/") -or
            $relative -match "(^|/)\.\.(/|$)") {
            throw "Production payload contains an unsafe or overlong path."
        }
        $totalBytes += $file.Length
        if ($file.Length -gt 536870912 -or $totalBytes -gt 1073741824) {
            throw "Production payload exceeds the bounded size policy."
        }
        if ($relative -match "(?i)(^|/)(?:WinShot\.)?KeyGen(?:\.|/|$)|" +
            "(^|/)(?:tests?|TestResults|Debug)(/|$)|" +
            "\.(?:pem|pfx|p12|snk|cs|csproj|sln|pdb)$|" +
            "(^|/)UNSIGNED-TEST-ARTIFACT\.txt$") {
            throw "Production payload contains forbidden development, test, key, or unsigned material."
        }
    }

    return [pscustomobject]@{
        Root = $resolvedRoot
        Files = $files
        TotalBytes = $totalBytes
    }
}

function Read-StrictDerElement {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)]$Cursor,
        [Parameter(Mandatory = $true)][int]$Boundary
    )

    if ($Cursor.Offset -ge $Boundary) {
        throw "Release attestation public key has truncated DER."
    }
    $tag = [int]$Bytes[$Cursor.Offset]
    $Cursor.Offset++
    if ($Cursor.Offset -ge $Boundary) {
        throw "Release attestation public key has truncated DER length."
    }
    $firstLength = [int]$Bytes[$Cursor.Offset]
    $Cursor.Offset++
    if (($firstLength -band 0x80) -eq 0) {
        $length = $firstLength
    }
    else {
        $lengthBytes = $firstLength -band 0x7F
        if ($lengthBytes -le 0 -or $lengthBytes -gt 4 -or
            $Cursor.Offset + $lengthBytes -gt $Boundary -or
            $Bytes[$Cursor.Offset] -eq 0) {
            throw "Release attestation public key has non-canonical DER length."
        }
        [long]$longLength = 0
        for ($index = 0; $index -lt $lengthBytes; $index++) {
            $longLength = ($longLength -shl 8) -bor
                [int]$Bytes[$Cursor.Offset + $index]
        }
        $Cursor.Offset += $lengthBytes
        if ($longLength -lt 128 -or $longLength -gt [int]::MaxValue) {
            throw "Release attestation public key has non-canonical DER length."
        }
        $length = [int]$longLength
    }
    if ($length -lt 0 -or $Cursor.Offset + $length -gt $Boundary) {
        throw "Release attestation public key has an out-of-bounds DER element."
    }
    $element = [pscustomobject]@{
        Tag = $tag
        ContentOffset = [int]$Cursor.Offset
        ContentLength = $length
        EndOffset = [int]($Cursor.Offset + $length)
    }
    $Cursor.Offset = $element.EndOffset
    return $element
}

function Get-StrictRsaParametersFromSpki {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    if ($Bytes.Length -lt 64 -or $Bytes.Length -gt 8192) {
        throw "Release attestation public key size is outside the bounded policy."
    }
    $cursor = [pscustomobject]@{ Offset = 0 }
    $outer = Read-StrictDerElement -Bytes $Bytes -Cursor $cursor `
        -Boundary $Bytes.Length
    if ($outer.Tag -ne 0x30 -or $outer.EndOffset -ne $Bytes.Length) {
        throw "Release attestation public key must be one exact DER SubjectPublicKeyInfo."
    }

    $innerCursor = [pscustomobject]@{ Offset = $outer.ContentOffset }
    $algorithm = Read-StrictDerElement -Bytes $Bytes -Cursor $innerCursor `
        -Boundary $outer.EndOffset
    $expectedAlgorithm = [Convert]::FromBase64String(
        "MA0GCSqGSIb3DQEBAQUA")
    $actualAlgorithm = New-Object byte[] (
        $algorithm.EndOffset - ($algorithm.ContentOffset - 2))
    $algorithmStart = $algorithm.ContentOffset - 2
    if ($Bytes[$algorithmStart + 1] -ge 0x80) {
        throw "Release attestation public key algorithm identifier is not canonical."
    }
    [Array]::Copy(
        $Bytes,
        $algorithmStart,
        $actualAlgorithm,
        0,
        $actualAlgorithm.Length)
    if ([Convert]::ToBase64String($actualAlgorithm) -cne
        [Convert]::ToBase64String($expectedAlgorithm)) {
        throw "Release attestation public key must use rsaEncryption with NULL parameters."
    }

    $bitString = Read-StrictDerElement -Bytes $Bytes -Cursor $innerCursor `
        -Boundary $outer.EndOffset
    if ($bitString.Tag -ne 0x03 -or $bitString.ContentLength -lt 2 -or
        $Bytes[$bitString.ContentOffset] -ne 0 -or
        $innerCursor.Offset -ne $outer.EndOffset) {
        throw "Release attestation public key has an invalid DER bit string."
    }

    $rsaCursor = [pscustomobject]@{ Offset = $bitString.ContentOffset + 1 }
    $rsaSequence = Read-StrictDerElement -Bytes $Bytes -Cursor $rsaCursor `
        -Boundary $bitString.EndOffset
    if ($rsaSequence.Tag -ne 0x30 -or
        $rsaSequence.EndOffset -ne $bitString.EndOffset) {
        throw "Release attestation public key has an invalid RSA sequence."
    }
    $parameterCursor = [pscustomobject]@{ Offset = $rsaSequence.ContentOffset }
    $modulusElement = Read-StrictDerElement -Bytes $Bytes `
        -Cursor $parameterCursor -Boundary $rsaSequence.EndOffset
    $exponentElement = Read-StrictDerElement -Bytes $Bytes `
        -Cursor $parameterCursor -Boundary $rsaSequence.EndOffset
    if ($modulusElement.Tag -ne 0x02 -or $exponentElement.Tag -ne 0x02 -or
        $parameterCursor.Offset -ne $rsaSequence.EndOffset) {
        throw "Release attestation public key has invalid RSA parameters."
    }

    $integerBytes = @()
    foreach ($element in @($modulusElement, $exponentElement)) {
        if ($element.ContentLength -le 0) {
            throw "Release attestation public key contains an empty DER integer."
        }
        $start = $element.ContentOffset
        $length = $element.ContentLength
        if (($Bytes[$start] -band 0x80) -ne 0) {
            throw "Release attestation public key contains a negative DER integer."
        }
        if ($Bytes[$start] -eq 0) {
            if ($length -le 1 -or ($Bytes[$start + 1] -band 0x80) -eq 0) {
                throw "Release attestation public key contains a non-canonical DER integer."
            }
            $start++
            $length--
        }
        $value = New-Object byte[] $length
        [Array]::Copy($Bytes, $start, $value, 0, $length)
        $integerBytes += ,$value
    }
    $modulus = [byte[]]$integerBytes[0]
    $exponent = [byte[]]$integerBytes[1]
    if (($modulus[0] -band 0x80) -eq 0) {
        throw "Release attestation public key modulus is below its declared bit size."
    }
    if ($exponent.Length -gt 4) {
        throw "Release attestation public key has an unsupported exponent."
    }
    [uint64]$exponentValue = 0
    foreach ($octet in $exponent) {
        $exponentValue = ($exponentValue -shl 8) -bor [uint64]$octet
    }
    if ($exponentValue -lt 65537 -or ($exponentValue -band 1) -eq 0) {
        throw "Release attestation public key has an unsafe exponent."
    }

    $parameters = [System.Security.Cryptography.RSAParameters]::new()
    $parameters.Modulus = $modulus
    $parameters.Exponent = $exponent
    return $parameters
}

function Resolve-ReleaseAttestationPublicKey {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Release attestation validation requires ReleaseAttestationPublicKeyPath."
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Release attestation public key does not exist."
    }
    $item = Get-Item -LiteralPath $resolved
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release attestation public key cannot be a reparse point."
    }
    return $resolved
}

function Assert-ReleaseAttestationContractReady {
    param([Parameter(Mandatory = $true)]$Contract)

    $configuration = $Contract.payload.releaseAttestation
    if ($configuration.implementationApproved -ne $true) {
        throw "Release attestation validation refused: implementation is not approved."
    }
    Assert-ProductionText $configuration.expectedVersion `
        "payload.releaseAttestation.expectedVersion" 128 `
        "^[1-9][0-9]*[.][0-9]+[.][0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$"
    Assert-ProductionText $configuration.expectedChannel `
        "payload.releaseAttestation.expectedChannel" 64 `
        "^[a-z0-9][a-z0-9.-]{0,63}$"
    Assert-ProductionText $configuration.publicKeySha256 `
        "payload.releaseAttestation.publicKeySha256" 64 "^[0-9A-F]{64}$"
    if ($configuration.publicKeySha256 -cne
        $Contract.updates.candidateTrust.descriptorSignature.publicKeySha256) {
        throw "Release attestation must reuse the exact approved update descriptor release key."
    }
}

function Assert-SignedReleaseAttestation {
    param(
        [Parameter(Mandatory = $true)]$Contract,
        [Parameter(Mandatory = $true)]$Payload,
        [Parameter(Mandatory = $true)][string]$PublicKeyPath
    )

    $configuration = $Contract.payload.releaseAttestation
    $attestationPath = Join-Path $Payload.Root $configuration.attestationFileName
    $signaturePath = Join-Path $Payload.Root $configuration.signatureFileName
    foreach ($required in @($attestationPath, $signaturePath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Release attestation or detached signature is missing."
        }
        if (((Get-Item -LiteralPath $required).Attributes -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release attestation evidence cannot be a reparse point."
        }
    }
    $attestationBytes = [System.IO.File]::ReadAllBytes($attestationPath)
    $signatureBytes = [System.IO.File]::ReadAllBytes($signaturePath)
    if ($attestationBytes.Length -le 0 -or
        $attestationBytes.Length -gt $configuration.maximumAttestationBytes) {
        throw "Release attestation size is outside the bounded policy."
    }
    if ($signatureBytes.Length -le 0 -or
        $signatureBytes.Length -gt $configuration.maximumSignatureBytes) {
        throw "Release attestation signature size is outside the bounded policy."
    }

    $resolvedPublicKey = Resolve-ReleaseAttestationPublicKey -Path $PublicKeyPath
    $publicKeyBytes = [System.IO.File]::ReadAllBytes($resolvedPublicKey)
    if ((Get-BytesSha256 -Bytes $publicKeyBytes) -cne
        $configuration.publicKeySha256) {
        throw "Release attestation public key fingerprint does not match the approved contract."
    }
    $rsaParameters = Get-StrictRsaParametersFromSpki -Bytes $publicKeyBytes
    $rsa = [System.Security.Cryptography.RSACng]::new()
    try {
        $rsa.ImportParameters($rsaParameters)
        if ($rsa.KeySize -lt $configuration.minimumRsaBits -or
            $signatureBytes.Length -ne [int]($rsa.KeySize / 8)) {
            throw "Release attestation RSA key or signature size is outside policy."
        }
        if (-not $rsa.VerifyData(
                $attestationBytes,
                $signatureBytes,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pss)) {
            throw "Release attestation detached signature is invalid."
        }
    }
    finally {
        $rsa.Dispose()
    }

    if ($attestationBytes.Length -ge 3 -and
        $attestationBytes[0] -eq 0xEF -and
        $attestationBytes[1] -eq 0xBB -and
        $attestationBytes[2] -eq 0xBF) {
        throw "Release attestation must not contain a UTF-8 BOM."
    }
    if ($attestationBytes[$attestationBytes.Length - 1] -ne 0x0A) {
        throw "Release attestation must end with one LF."
    }
    foreach ($octet in $attestationBytes) {
        if (($octet -lt 0x20 -and $octet -ne 0x09 -and $octet -ne 0x0A) -or
            $octet -eq 0x0D -or $octet -eq 0x7F) {
            throw "Release attestation must use strict LF-only UTF-8."
        }
    }
    try {
        $text = [System.Text.UTF8Encoding]::new($false, $true).GetString(
            $attestationBytes)
    }
    catch {
        throw "Release attestation is not well-formed UTF-8."
    }
    $lines = @($text.Substring(0, $text.Length - 1).Split([char]0x0A))
    if ($lines.Count -lt 8 -or
        $lines[0] -cne "WINSHOT-ACTIVATION-RELEASE-ATTESTATION-V1") {
        throw "Release attestation header is invalid."
    }

    $expectedHeaders = @(
        @("package-id", $Contract.identity.packageId),
        @("version", $configuration.expectedVersion),
        @("channel", $configuration.expectedChannel),
        @("runtime-identifier", $configuration.expectedRuntimeIdentifier),
        @("paid-major", [string]$configuration.expectedPaidMajor),
        @("release-manifest-sha256", $null),
        @("file-count", $null)
    )
    $parsedHeaders = @{}
    for ($index = 0; $index -lt $expectedHeaders.Count; $index++) {
        $parts = @($lines[$index + 1].Split([char]0x09))
        if ($parts.Count -ne 2 -or
            $parts[0] -cne $expectedHeaders[$index][0] -or
            [string]::IsNullOrEmpty($parts[1])) {
            throw "Release attestation canonical header order or shape is invalid."
        }
        if ($null -ne $expectedHeaders[$index][1] -and
            $parts[1] -cne $expectedHeaders[$index][1]) {
            throw "Release attestation identity does not match the approved contract."
        }
        $parsedHeaders[$parts[0]] = $parts[1]
    }
    if ($parsedHeaders["release-manifest-sha256"] -cnotmatch "^[0-9A-F]{64}$") {
        throw "Release attestation manifest fingerprint is invalid."
    }
    [int]$declaredFileCount = 0
    if ($parsedHeaders["file-count"] -cnotmatch "^[1-9][0-9]*$" -or
        -not [int]::TryParse(
            $parsedHeaders["file-count"],
            [System.Globalization.NumberStyles]::None,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$declaredFileCount) -or
        $declaredFileCount -le 0 -or $declaredFileCount -gt 4094) {
        throw "Release attestation file count is invalid."
    }

    $expectedFiles = @($Payload.Files | Where-Object {
        $relative = $_.FullName.Substring($Payload.Root.Length + 1).Replace("\", "/")
        $relative -cnotin @($configuration.inventoryExclusions)
    })
    if ($declaredFileCount -ne $expectedFiles.Count -or
        $lines.Count -ne (8 + $declaredFileCount)) {
        throw "Release attestation inventory count does not match the complete payload."
    }
    $expectedByPath =
        [System.Collections.Generic.Dictionary[string, object]]::new(
            [System.StringComparer]::Ordinal)
    foreach ($file in $expectedFiles) {
        $relative = $file.FullName.Substring($Payload.Root.Length + 1).Replace("\", "/")
        if ($expectedByPath.ContainsKey($relative)) {
            throw "Release attestation payload contains an ordinal duplicate path."
        }
        $expectedByPath.Add($relative, $file)
    }
    $expectedPaths = @(Get-OrdinalSortedContractStrings -Values @($expectedByPath.Keys))
    for ($index = 0; $index -lt $declaredFileCount; $index++) {
        $parts = @($lines[$index + 8].Split([char]0x09))
        if ($parts.Count -ne 4 -or $parts[0] -cne "file" -or
            $parts[1] -cnotmatch "^[0-9A-F]{64}$" -or
            [string]::IsNullOrEmpty($parts[3]) -or
            $parts[3] -cne $expectedPaths[$index] -or
            $parts[3].Contains("\") -or $parts[3].Contains(":") -or
            $parts[3].StartsWith("/") -or
            $parts[3] -match "(^|/)\.\.(/|$)" -or
            $parts[3].IndexOfAny([char[]]"`0`r`n`t") -ge 0) {
            throw "Release attestation inventory path, order, or hash is invalid."
        }
        [long]$declaredBytes = 0
        if ($parts[2] -cnotmatch "^(?:0|[1-9][0-9]*)$" -or
            -not [long]::TryParse(
                $parts[2],
                [System.Globalization.NumberStyles]::None,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$declaredBytes) -or $declaredBytes -lt 0) {
            throw "Release attestation inventory byte length is invalid."
        }
        $file = $expectedByPath[$parts[3]]
        if ($declaredBytes -ne $file.Length -or
            $parts[1] -cne (Get-ContractSha256 -Path $file.FullName)) {
            throw "Release attestation inventory does not match the complete payload."
        }
    }

    $manifestPath = Join-Path $Payload.Root "release-manifest.json"
    $manifestSha256 = Get-ContractSha256 -Path $manifestPath
    if ($parsedHeaders["release-manifest-sha256"] -cne $manifestSha256) {
        throw "Release attestation does not bind the exact release manifest."
    }
    try {
        $manifestText = [System.IO.File]::ReadAllText(
            $manifestPath,
            [System.Text.UTF8Encoding]::new($false, $true))
        Assert-NoDuplicateJsonProperties -Json $manifestText
        $manifest = $manifestText | ConvertFrom-Json
    }
    catch {
        throw "Release attestation references an invalid release manifest."
    }
    if ($manifest.application -cne $Contract.identity.applicationName -or
        $manifest.version -cne $configuration.expectedVersion -or
        $manifest.runtimeIdentifier -cne $configuration.expectedRuntimeIdentifier -or
        $manifest.packageKind -cne $Contract.payload.productionManifestPackageKind -or
        [int]$manifest.commercialConfiguration.applicationMajor -ne
            $configuration.expectedPaidMajor) {
        throw "Release attestation and release manifest identities do not match."
    }
}

function Assert-ChecksumInventory {
    param(
        [Parameter(Mandatory = $true)]$Payload,
        [Parameter(Mandatory = $true)]$ReleaseAttestation
    )

    $checksumPath = Join-Path $Payload.Root "SHA256SUMS"
    $excludedPaths = @("SHA256SUMS") +
        @($ReleaseAttestation.inventoryExclusions)
    $lines = @([System.IO.File]::ReadAllLines(
        $checksumPath,
        [System.Text.UTF8Encoding]::new($false)) |
        Where-Object { $_.Length -gt 0 })
    if ($lines.Count -ne ($Payload.Files.Count - $excludedPaths.Count)) {
        throw "SHA256SUMS must cover every production content file outside the signed trust envelope."
    }

    $unsortedExpectedPaths = @($Payload.Files |
        Where-Object {
            $relative = $_.FullName.Substring(
                $Payload.Root.Length + 1).Replace("\", "/")
            $relative -cnotin $excludedPaths
        } |
        ForEach-Object {
            $_.FullName.Substring($Payload.Root.Length + 1).Replace("\", "/")
        })
    $expectedPaths = @(Get-OrdinalSortedContractStrings `
        -Values $unsortedExpectedPaths)
    $actualPaths = @()
    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($line in $lines) {
        if ($line -cnotmatch "^([0-9A-F]{64})  ([^\\].*)$") {
            throw "SHA256SUMS contains a malformed entry."
        }
        $expectedHash = $Matches[1]
        $relative = $Matches[2]
        if ($relative.StartsWith("/") -or $relative.Contains(":") -or
            $relative -match "(^|/)\.\.(/|$)" -or -not $seen.Add($relative)) {
            throw "SHA256SUMS contains an unsafe or duplicate path."
        }
        $actualPaths += $relative
        $fullPath = [System.IO.Path]::GetFullPath(
            (Join-Path $Payload.Root $relative.Replace("/", "\")))
        if (-not $fullPath.StartsWith(
                ($Payload.Root + "\"),
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "SHA256SUMS references a missing or external path."
        }
        if ((Get-ContractSha256 -Path $fullPath) -cne $expectedHash) {
            throw "SHA256SUMS hash mismatch for a production payload file."
        }
    }
    if (($actualPaths -join "`n") -cne ($expectedPaths -join "`n")) {
        throw "SHA256SUMS paths must exactly match the ordinal production payload inventory."
    }
}

function Assert-ThirdPartyDependencyTrust {
    param(
        [Parameter(Mandatory = $true)]$Dependency,
        [Parameter(Mandatory = $true)][string]$PayloadRoot
    )

    $path = Join-Path $PayloadRoot $Dependency.payloadFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Production payload is missing an approved third-party dependency."
    }
    if ((Get-ContractSha256 -Path $path) -cne $Dependency.payloadSha256) {
        throw "Production third-party payload hash does not match the approved contract."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($Dependency.trustDecision -ceq "ReviewedUnsigned") {
        if ($signature.Status -ne
            [System.Management.Automation.SignatureStatus]::NotSigned) {
            throw "Reviewed unsigned third-party payload changed signature state."
        }
        return
    }

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "Third-party payload does not have the approved valid upstream signature state."
    }
    if ($signature.SignerCertificate.Subject -cne $Dependency.expectedSignerSubject -or
        $signature.SignerCertificate.Thumbprint -cne
            $Dependency.expectedLeafThumbprint.ToUpperInvariant()) {
        throw "Third-party payload signer does not match the approved upstream identity."
    }
}

function Assert-ProductionPayload {
    param(
        [Parameter(Mandatory = $true)]$Contract,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$PublicKeyPath
    )

    $payload = Get-BoundedPayloadFiles -Root $Root
    foreach ($relative in @($Contract.payload.requiredMetadata)) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload.Root $relative) -PathType Leaf)) {
            throw "Production payload is missing required metadata '$relative'."
        }
    }
    Assert-SignedReleaseAttestation -Contract $Contract -Payload $payload `
        -PublicKeyPath $PublicKeyPath

    $manifestPath = Join-Path $payload.Root "release-manifest.json"
    try {
        $manifestText = [System.IO.File]::ReadAllText(
            $manifestPath,
            [System.Text.UTF8Encoding]::new($false, $true))
        Assert-NoDuplicateJsonProperties -Json $manifestText
        $manifest = $manifestText | ConvertFrom-Json
    }
    catch {
        throw "Production release manifest is not valid JSON."
    }
    if ($manifest.packageKind -cne $Contract.payload.productionManifestPackageKind -or
        $manifest.application -cne $Contract.identity.applicationName -or
        $manifest.runtimeIdentifier -cne "win-x64" -or
        $manifest.selfContained -ne $true -or
        $manifest.trimmed -ne $false -or
        $manifest.singleFile -ne $false) {
        throw "Production release manifest identity or package shape does not match the contract."
    }
    if ($manifest.sourceProvenance.workingTreeDirty -ne $false -or
        $manifest.sourceProvenance.buildInputsDirty -ne $false) {
        throw "Production release manifest must describe clean source and build inputs."
    }

    Assert-ChecksumInventory -Payload $payload `
        -ReleaseAttestation $Contract.payload.releaseAttestation
    foreach ($relative in @($Contract.payload.publisherSignatureTargets)) {
        $target = Join-Path $payload.Root $relative
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "Production payload is missing a required WinShot publisher-signature target."
        }
        Assert-TrustedInstallerSignature -Path $target -Signing $Contract.signing
    }
    foreach ($dependency in @($Contract.payload.thirdPartyDependencies)) {
        Assert-ThirdPartyDependencyTrust `
            -Dependency $dependency -PayloadRoot $payload.Root
    }

    return $payload
}

$checkedContract = Get-CheckedInstallerContract -Path $ContractPath
$contract = $checkedContract.Value
Assert-ContractStructure -Contract $contract

if ($Mode -eq "Authoring") {
    Write-Output "PASS_INSTALLER_CONTRACT_AUTHORING"
    Write-Output "ContractSha256: $($checkedContract.Sha256)"
    Write-Output "ProductionApproved: $($contract.readiness.productionApproved)"
    return
}

if ($Mode -eq "InventoryTrustTest") {
    Assert-ReleaseAttestationContractReady -Contract $contract
    $payloadEvidence = Get-BoundedPayloadFiles -Root $PayloadDirectory
    Assert-SignedReleaseAttestation -Contract $contract `
        -Payload $payloadEvidence `
        -PublicKeyPath $ReleaseAttestationPublicKeyPath
    Write-Output "PASS_INSTALLER_INVENTORY_TRUST_TEST"
    Write-Output "ContractSha256: $($checkedContract.Sha256)"
    Write-Output "PayloadFiles: $($payloadEvidence.Files.Count)"
    return
}

Assert-ProductionContract -Contract $contract
$resolvedInstaller = Resolve-ExistingInstallerFile `
    -Path $InstallerPath -Name "InstallerPath" `
    -AllowedExtensions @(".exe", ".msi", ".msix", ".msixbundle")
$resolvedUpdater = Resolve-ExistingInstallerFile `
    -Path $UpdaterPath -Name "UpdaterPath" -AllowedExtensions @(".exe")
$resolvedStableLauncher = Resolve-ExistingInstallerFile `
    -Path $StableLauncherPath -Name "StableLauncherPath" `
    -AllowedExtensions @(".exe")
if ([string]::Equals(
        $resolvedInstaller,
        $resolvedUpdater,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Production installer and updater must be distinct signed artifacts."
}
foreach ($other in @($resolvedInstaller, $resolvedUpdater)) {
    if ([string]::Equals(
            $resolvedStableLauncher,
            $other,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production stable launcher must be distinct from installer and updater artifacts."
    }
}
Assert-TrustedInstallerSignature -Path $resolvedInstaller -Signing $contract.signing
Assert-TrustedInstallerSignature -Path $resolvedUpdater -Signing $contract.signing
Assert-StableLauncherBinary -Path $resolvedStableLauncher -Contract $contract
Assert-TrustedInstallerSignature `
    -Path $resolvedStableLauncher -Signing $contract.signing
$payloadEvidence = Assert-ProductionPayload -Contract $contract `
    -Root $PayloadDirectory `
    -PublicKeyPath $ReleaseAttestationPublicKeyPath

Write-Output "PASS_INSTALLER_CONTRACT_PRODUCTION"
Write-Output "ContractSha256: $($checkedContract.Sha256)"
Write-Output "StableLauncherSha256: $((Get-FileHash -LiteralPath $resolvedStableLauncher -Algorithm SHA256).Hash)"
Write-Output "PayloadFiles: $($payloadEvidence.Files.Count)"
Write-Output "PayloadBytes: $($payloadEvidence.TotalBytes)"

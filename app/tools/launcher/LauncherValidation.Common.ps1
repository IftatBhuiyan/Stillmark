Set-StrictMode -Version Latest

function Get-LauncherSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-LauncherOrdinalStrings {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Values
    )

    $copy = [string[]]@($Values)
    [Array]::Sort($copy, [System.StringComparer]::Ordinal)
    return $copy
}

function Get-LauncherOrdinalRecords {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Records,

        [Parameter(Mandatory = $true)]
        [string]$PropertyName
    )

    $keys = [string[]]@(
        foreach ($record in @($Records)) {
            [string]$record.$PropertyName
        }
    )
    $map = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $keys.Count; $index++) {
        if ($map.ContainsKey($keys[$index])) {
            throw "Ordinal record sort contains duplicate key '$($keys[$index])'."
        }
        $map.Add($keys[$index], $Records[$index])
    }
    [Array]::Sort($keys, [System.StringComparer]::Ordinal)
    return @($keys | ForEach-Object { $map[$_] })
}

function Assert-LauncherExactProperties {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        throw "$Context must be a JSON object."
    }
    $actual = [string[]]@($Value.PSObject.Properties.Name)
    $actual = Get-LauncherOrdinalStrings -Values $actual
    $expectedSorted = Get-LauncherOrdinalStrings -Values $Expected
    if (($actual -join "`n") -cne ($expectedSorted -join "`n")) {
        throw "$Context has missing or unexpected properties."
    }
}

function Assert-NoDuplicateLauncherJsonProperties {
    param([Parameter(Mandatory = $true)][string]$Json)

    if (-not ("WinShot.LauncherTooling.StrictJsonProperties" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinShot.LauncherTooling
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
        [WinShot.LauncherTooling.StrictJsonProperties]::AssertNoDuplicates($Json)
    }
    catch {
        $rootException = $_.Exception
        while ($null -ne $rootException.InnerException) {
            $rootException = $rootException.InnerException
        }
        if ($rootException -is [System.IO.InvalidDataException]) {
            throw "Launcher JSON contains a duplicate property name."
        }
        throw "Launcher JSON is not valid."
    }
}

function Read-LauncherStrictJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Context,
        [int]$MaximumBytes = 1048576
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Context is unavailable."
    }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -le 0 -or $bytes.Length -gt $MaximumBytes) {
        throw "$Context size is outside the validation boundary."
    }
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        throw "$Context must be UTF-8 without a byte-order marker."
    }
    try {
        $raw = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    catch {
        throw "$Context is not well-formed UTF-8."
    }
    Assert-NoDuplicateLauncherJsonProperties -Json $raw
    try {
        $value = $raw | ConvertFrom-Json
    }
    catch {
        throw "$Context is not valid JSON."
    }
    if ($null -eq $value) {
        throw "$Context is empty."
    }
    return [pscustomobject]@{
        Raw = $raw
        Value = $value
    }
}

function Resolve-LauncherDotnetPath {
    param(
        [string]$DotnetPath,
        [Parameter(Mandatory = $true)][string]$ExpectedSdkVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedHostSha256
    )

    $candidatePaths = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($DotnetPath)) {
        $candidatePaths.Add([System.IO.Path]::GetFullPath($DotnetPath))
    }
    else {
        if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            $candidatePaths.Add(
                (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'))
        }
        $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $command -and
            -not [string]::IsNullOrWhiteSpace([string]$command.Source)) {
            $candidatePaths.Add([string]$command.Source)
        }
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidatePaths) {
        $resolved = [System.IO.Path]::GetFullPath($candidate)
        if (-not $seen.Add($resolved) -or
            -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            continue
        }
        if ((Get-LauncherSha256 -Path $resolved) -cne $ExpectedHostSha256) {
            continue
        }
        $versionOutput = @(& $resolved --version 2>$null)
        if ($LASTEXITCODE -ne 0 -or $versionOutput.Count -ne 1 -or
            [string]$versionOutput[0] -cne $ExpectedSdkVersion) {
            continue
        }
        $compiler = Join-Path (Split-Path -Parent $resolved) (
            "sdk\$ExpectedSdkVersion\Roslyn\bincore\csc.dll")
        if (Test-Path -LiteralPath $compiler -PathType Leaf) {
            return $resolved
        }
    }

    throw "Stable-launcher provenance cannot resolve the recorded pinned dotnet host."
}

function Assert-StableLauncherBuildEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactRoot,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [ValidateSet('Authoring', 'Production')][string]$Mode = 'Authoring',
        [string]$DotnetPath
    )

    $launcherPath = Join-Path $ArtifactRoot 'WinShot.exe'
    $metadataPath = Join-Path $ArtifactRoot 'launcher-build.json'
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        throw "Stable-launcher executable is unavailable."
    }

    $bytes = [System.IO.File]::ReadAllBytes($launcherPath)
    if ($bytes.Length -lt 512 -or $bytes.Length -gt 1048576) {
        throw "Stable-launcher executable length is outside the 512-byte to 1-MiB engineering bound."
    }
    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "Stable-launcher executable is not an MZ image."
    }
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 96 -ge $bytes.Length) {
        throw "Stable-launcher PE header offset is invalid."
    }
    if ($bytes[$peOffset] -ne 0x50 -or
        $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or
        $bytes[$peOffset + 3] -ne 0) {
        throw "Stable-launcher executable has no PE signature."
    }
    if ([System.BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) {
        throw "Stable-launcher executable is not x64."
    }
    $optionalHeader = $peOffset + 24
    if ([System.BitConverter]::ToUInt16($bytes, $optionalHeader) -ne 0x20B) {
        throw "Stable-launcher executable is not PE32+."
    }
    if ([System.BitConverter]::ToUInt16($bytes, $optionalHeader + 68) -ne 2) {
        throw "Stable-launcher executable is not a Windows GUI subsystem image."
    }

    $checkedMetadata = Read-LauncherStrictJson `
        -Path $metadataPath `
        -Context 'Stable-launcher build metadata' `
        -MaximumBytes 131072
    $metadata = $checkedMetadata.Value
    Assert-LauncherExactProperties $metadata @(
        'schemaVersion',
        'artifactKind',
        'packageId',
        'version',
        'architecture',
        'subsystem',
        'clrContract',
        'minimumWindowsContract',
        'sdkVersion',
        'launcherSha256',
        'deterministicRebuildSha256',
        'inputs'
    ) 'Stable-launcher build metadata'
    $sdkConfiguration = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw |
        ConvertFrom-Json
    $expectedSdkVersion = [string]$sdkConfiguration.sdk.version
    if (($metadata.schemaVersion -isnot [int] -and
            $metadata.schemaVersion -isnot [long]) -or
        [long]$metadata.schemaVersion -ne 1 -or
        $metadata.artifactKind -isnot [string] -or
        [string]$metadata.artifactKind -cne 'UNSIGNED_STABLE_LAUNCHER_ENGINEERING' -or
        $metadata.packageId -isnot [string] -or
        [string]$metadata.packageId -cne 'PROVISIONAL.WinShot' -or
        $metadata.version -isnot [string] -or
        [string]$metadata.version -cne '1.0.0' -or
        $metadata.architecture -isnot [string] -or
        [string]$metadata.architecture -cne 'x64' -or
        $metadata.subsystem -isnot [string] -or
        [string]$metadata.subsystem -cne 'WindowsGui' -or
        $metadata.clrContract -isnot [string] -or
        [string]$metadata.clrContract -cne 'InBoxNetFramework4Clr' -or
        $metadata.minimumWindowsContract -isnot [string] -or
        [string]$metadata.minimumWindowsContract -cne
            'Windows10Version1809WithNetFramework472' -or
        $metadata.sdkVersion -isnot [string] -or
        [string]$metadata.sdkVersion -cne $expectedSdkVersion) {
        throw "Stable-launcher build metadata is invalid."
    }
    foreach ($hashName in @('launcherSha256', 'deterministicRebuildSha256')) {
        if ($metadata.$hashName -isnot [string] -or
            [string]$metadata.$hashName -cnotmatch '^[A-F0-9]{64}$') {
            throw "Stable-launcher build metadata hash '$hashName' is invalid."
        }
    }

    $expectedInputPaths = [string[]]@(
        'src/WinShot.Launcher/LauncherIdentity.cs',
        'src/WinShot.Launcher/LauncherContracts.cs',
        'src/WinShot.Launcher/LauncherRuntime.cs',
        'src/WinShot.Launcher/LauncherReleaseAttestationVerifier.cs',
        'src/WinShot.Launcher/WindowsLauncherTrustVerifier.cs',
        'src/WinShot.Launcher/Program.cs',
        'src/WinShot.Launcher/AssemblyInfo.cs',
        'src/WinShot.Launcher/app.manifest',
        'src/WinShot.App/Assets/winshot.ico',
        'csc.dll',
        'dotnet.exe',
        'mscorlib.dll',
        'System.dll',
        'System.Core.dll'
    )
    $declaredInputs = @($metadata.inputs)
    if ($metadata.inputs -isnot [System.Array] -or
        $declaredInputs.Count -ne $expectedInputPaths.Count) {
        throw "Stable-launcher build metadata inputs have an invalid count or shape."
    }
    $declaredByPath =
        [System.Collections.Generic.Dictionary[string, object]]::new(
            [System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $declaredInputs.Count; $index++) {
        $entry = $declaredInputs[$index]
        Assert-LauncherExactProperties $entry @('path', 'sha256') (
            "Stable-launcher input entry $index")
        if ($entry.path -isnot [string] -or
            [string]$entry.path -cne $expectedInputPaths[$index] -or
            $entry.sha256 -isnot [string] -or
            [string]$entry.sha256 -cnotmatch '^[A-F0-9]{64}$' -or
            $declaredByPath.ContainsKey([string]$entry.path)) {
            throw "Stable-launcher build metadata input entry $index is invalid or out of order."
        }
        $declaredByPath.Add([string]$entry.path, $entry)
    }

    $resolvedDotnet = Resolve-LauncherDotnetPath `
        -DotnetPath $DotnetPath `
        -ExpectedSdkVersion ([string]$metadata.sdkVersion) `
        -ExpectedHostSha256 ([string]$declaredByPath['dotnet.exe'].sha256)
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $actualInputs = [ordered]@{
        'src/WinShot.Launcher/LauncherIdentity.cs' =
            (Join-Path $RepositoryRoot 'src\WinShot.Launcher\LauncherIdentity.cs')
        'src/WinShot.Launcher/LauncherContracts.cs' =
            (Join-Path $RepositoryRoot 'src\WinShot.Launcher\LauncherContracts.cs')
        'src/WinShot.Launcher/LauncherRuntime.cs' =
            (Join-Path $RepositoryRoot 'src\WinShot.Launcher\LauncherRuntime.cs')
        'src/WinShot.Launcher/LauncherReleaseAttestationVerifier.cs' =
            (Join-Path $RepositoryRoot (
                'src\WinShot.Launcher\LauncherReleaseAttestationVerifier.cs'))
        'src/WinShot.Launcher/WindowsLauncherTrustVerifier.cs' =
            (Join-Path $RepositoryRoot 'src\WinShot.Launcher\WindowsLauncherTrustVerifier.cs')
        'src/WinShot.Launcher/Program.cs' =
            (Join-Path $RepositoryRoot 'src\WinShot.Launcher\Program.cs')
        'src/WinShot.Launcher/AssemblyInfo.cs' =
            (Join-Path $RepositoryRoot 'src\WinShot.Launcher\AssemblyInfo.cs')
        'src/WinShot.Launcher/app.manifest' =
            (Join-Path $RepositoryRoot 'src\WinShot.Launcher\app.manifest')
        'src/WinShot.App/Assets/winshot.ico' =
            (Join-Path $RepositoryRoot 'src\WinShot.App\Assets\winshot.ico')
        'csc.dll' = (Join-Path (Split-Path -Parent $resolvedDotnet) (
            "sdk\$($metadata.sdkVersion)\Roslyn\bincore\csc.dll"))
        'dotnet.exe' = $resolvedDotnet
        'mscorlib.dll' = (Join-Path $frameworkRoot 'mscorlib.dll')
        'System.dll' = (Join-Path $frameworkRoot 'System.dll')
        'System.Core.dll' = (Join-Path $frameworkRoot 'System.Core.dll')
    }
    foreach ($inputPath in $expectedInputPaths) {
        $actualPath = [string]$actualInputs[$inputPath]
        if (-not (Test-Path -LiteralPath $actualPath -PathType Leaf) -or
            (Get-LauncherSha256 -Path $actualPath) -cne
                [string]$declaredByPath[$inputPath].sha256) {
            throw "Stable-launcher build input provenance mismatch for '$inputPath'."
        }
    }

    $actualHash = Get-LauncherSha256 -Path $launcherPath
    if ([string]$metadata.launcherSha256 -cne $actualHash -or
        [string]$metadata.deterministicRebuildSha256 -cne $actualHash) {
        throw "Stable-launcher hash does not match deterministic build metadata."
    }
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($launcherPath)
    if ($version.ProductName -cne 'WinShot' -or
        $version.CompanyName -cne 'Iftat Bhuiyan' -or
        $version.FileVersion -cne '1.0.0.1') {
        throw "Stable-launcher version metadata is invalid."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $launcherPath
    if ($Mode -ceq 'Authoring') {
        if ([string]$signature.Status -cne 'NotSigned') {
            throw "Authoring launcher must have exact NotSigned status."
        }
    }
    elseif ([string]$signature.Status -cne 'Valid') {
        throw "Production launcher must have a valid Authenticode signature."
    }

    return [pscustomobject]@{
        LauncherPath = $launcherPath
        MetadataPath = $metadataPath
        LauncherSha256 = $actualHash
        DotnetPath = $resolvedDotnet
    }
}

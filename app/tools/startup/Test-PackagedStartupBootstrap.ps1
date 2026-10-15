[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [ValidateSet('Authoring', 'Production')]
    [string]$Mode = 'Authoring',

    [Parameter(Mandatory = $true)]
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifactRoot = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$bootstrapPath = Join-Path $artifactRoot 'WinShot.Startup.exe'
$metadataPath = Join-Path $artifactRoot 'startup-build.json'
$checksumsPath = Join-Path $artifactRoot 'SHA256SUMS'
$expectedNames = @(
    'SHA256SUMS',
    'startup-build.json',
    'WinShot.Startup.exe'
)
$actualNames = @(
    Get-ChildItem -LiteralPath $artifactRoot -Force |
        Sort-Object -Property Name |
        ForEach-Object Name
)
if ($actualNames.Count -ne $expectedNames.Count) {
    throw "Startup-bootstrap artifact has an unexpected item count."
}
foreach ($name in $expectedNames) {
    if ($actualNames -cnotcontains $name) {
        throw "Startup-bootstrap artifact is missing '$name'."
    }
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw |
    ConvertFrom-Json
$sdkConfiguration = Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw |
    ConvertFrom-Json
$expectedSdkVersion = [string]$sdkConfiguration.sdk.version
$expectedProperties = @(
    'schemaVersion',
    'artifactKind',
    'packageId',
    'version',
    'architecture',
    'subsystem',
    'clrContract',
    'minimumWindowsContract',
    'sdkVersion',
    'bootstrapSha256',
    'deterministicRebuildSha256',
    'inputs'
)
$actualProperties = @($metadata.PSObject.Properties.Name)
if ($actualProperties.Count -ne $expectedProperties.Count) {
    throw "Startup-bootstrap build evidence has an unexpected property count."
}
foreach ($property in $expectedProperties) {
    if ($actualProperties -cnotcontains $property) {
        throw "Startup-bootstrap build evidence is missing '$property'."
    }
}
if ([int]$metadata.schemaVersion -ne 1 -or
    [string]$metadata.artifactKind -cne
        'UNSIGNED_PACKAGED_STARTUP_BOOTSTRAP_ENGINEERING' -or
    [string]$metadata.packageId -cne 'PROVISIONAL.WinShot' -or
    [string]$metadata.version -cne '1.0.0' -or
    [string]$metadata.architecture -cne 'x64' -or
    [string]$metadata.subsystem -cne 'WindowsGui' -or
    [string]$metadata.clrContract -cne 'InBoxNetFramework4Clr' -or
    [string]$metadata.minimumWindowsContract -cne
        'Windows10Version1809WithNetFramework472' -or
    [string]$metadata.sdkVersion -cne $expectedSdkVersion) {
    throw "Startup-bootstrap build evidence has an unexpected contract."
}

$bootstrapHash = (
    Get-FileHash -LiteralPath $bootstrapPath -Algorithm SHA256
).Hash
if ([string]$metadata.bootstrapSha256 -cne $bootstrapHash -or
    [string]$metadata.deterministicRebuildSha256 -cne $bootstrapHash) {
    throw "Startup-bootstrap hashes are not exact deterministic evidence."
}
$checksumText = [System.IO.File]::ReadAllText(
    $checksumsPath,
    [System.Text.Encoding]::ASCII)
if ($checksumText -cne "$bootstrapHash  WinShot.Startup.exe`n") {
    throw "Startup-bootstrap checksum file is not exact canonical content."
}

$dotnet = [System.IO.Path]::GetFullPath($DotnetPath)
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf) -or
    (& $dotnet --version).Trim() -cne $expectedSdkVersion) {
    throw "Startup-bootstrap validation requires repository-pinned SDK $expectedSdkVersion."
}
$sdkRoot = Join-Path (
    Split-Path -Parent $dotnet) "sdk\$expectedSdkVersion"
$expectedInputPaths = @(
    'src/WinShot.Startup/PackagedStartupBootstrapContracts.cs',
    'src/WinShot.Startup/PackagedStartupBootstrapRuntime.cs',
    'src/WinShot.Startup/WindowsPackagedStartupBootstrapPlatform.cs',
    'src/WinShot.Startup/Program.cs',
    'src/WinShot.Startup/AssemblyInfo.cs',
    'src/WinShot.Startup/app.manifest',
    'src/WinShot.App/Assets/winshot.ico',
    'csc.dll',
    'dotnet.exe',
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll'
)
$inputEntries = @($metadata.inputs)
if ($inputEntries.Count -ne $expectedInputPaths.Count) {
    throw "Startup-bootstrap build evidence has an unexpected input count."
}
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$externalInputPaths = @{
    'csc.dll' = Join-Path $sdkRoot 'Roslyn\bincore\csc.dll'
    'dotnet.exe' = $dotnet
    'mscorlib.dll' = Join-Path $frameworkRoot 'mscorlib.dll'
    'System.dll' = Join-Path $frameworkRoot 'System.dll'
    'System.Core.dll' = Join-Path $frameworkRoot 'System.Core.dll'
}
for ($index = 0; $index -lt $expectedInputPaths.Count; $index++) {
    $entry = $inputEntries[$index]
    $expectedRelative = $expectedInputPaths[$index]
    if ([string]$entry.path -cne $expectedRelative) {
        throw "Startup-bootstrap input order/path is not canonical."
    }
    $inputPath = if ($externalInputPaths.ContainsKey($expectedRelative)) {
        $externalInputPaths[$expectedRelative]
    }
    else {
        Join-Path $repositoryRoot $expectedRelative.Replace('/', '\')
    }
    $actualInputHash = (
        Get-FileHash -LiteralPath $inputPath -Algorithm SHA256
    ).Hash
    if ([string]$entry.sha256 -cne $actualInputHash) {
        throw "Startup-bootstrap input '$expectedRelative' is stale or changed."
    }
}

$bytes = [System.IO.File]::ReadAllBytes($bootstrapPath)
if ($bytes.Length -lt 512) {
    throw "Startup-bootstrap PE is unexpectedly small."
}
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
if ($peOffset -lt 64 -or $peOffset + 96 -ge $bytes.Length -or
    $bytes[$peOffset] -ne 0x50 -or
    $bytes[$peOffset + 1] -ne 0x45) {
    throw "Startup-bootstrap has no valid PE signature."
}
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
if ($machine -ne 0x8664) {
    throw "Startup-bootstrap is not an x64 PE."
}
$optionalHeader = $peOffset + 24
$subsystem = [BitConverter]::ToUInt16($bytes, $optionalHeader + 68)
if ($subsystem -ne 2) {
    throw "Startup-bootstrap is not a Windows GUI executable."
}

$fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    $bootstrapPath)
if ($fileInfo.FileDescription -cne 'WinShot Packaged Startup' -or
    $fileInfo.ProductName -cne 'WinShot' -or
    -not $fileInfo.FileVersion.StartsWith('1.0.0.0')) {
    throw "Startup-bootstrap version metadata is not exact."
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapPath
if ($Mode -eq 'Authoring' -and
    $signature.Status -ne
        [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw "Authoring startup bootstrap must be exactly unsigned."
}
if ($Mode -eq 'Production' -and
    $signature.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Production startup bootstrap must have a valid signature."
}

$sourceText = @(
    Get-ChildItem -LiteralPath (
        Join-Path $repositoryRoot 'src\WinShot.Startup') -Filter '*.cs' |
        Sort-Object -Property Name |
        ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Raw
        }
) -join "`n"
foreach ($forbidden in @(
    'ShellExecute',
    'Process\.Start',
    'MessageBox',
    'Microsoft\.Win32\.Registry',
    'HttpClient',
    'active-version',
    'cmd\.exe',
    'powershell'
)) {
    if ($sourceText -match $forbidden) {
        throw "Startup-bootstrap source contains forbidden surface '$forbidden'."
    }
}
if ($sourceText -notmatch 'CreateProcessW' -or
    $sourceText -notmatch 'GetCurrentPackagePath2' -or
    $sourceText -notmatch 'GetCurrentPackageFullName' -or
    $sourceText -notmatch 'FILE_FLAG_OPEN_REPARSE_POINT|FileFlagOpenReparsePoint') {
    throw "Startup-bootstrap source is missing a required native boundary."
}
$manifestText = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'src\WinShot.Startup\app.manifest') -Raw
if ($manifestText -notmatch
        'requestedExecutionLevel level="asInvoker" uiAccess="false"') {
    throw "Startup-bootstrap source manifest is not asInvoker/uiAccess=false."
}

$probe = Start-Process `
    -FilePath $bootstrapPath `
    -WindowStyle Hidden `
    -PassThru `
    -Wait
if ($probe.ExitCode -ne 2) {
    throw "Unpackaged zero-argument bootstrap probe must return NotPackaged (2)."
}
$argumentProbe = Start-Process `
    -FilePath $bootstrapPath `
    -ArgumentList '--tray' `
    -WindowStyle Hidden `
    -PassThru `
    -Wait
if ($argumentProbe.ExitCode -ne 1) {
    throw "Bootstrap argument refusal probe must return InvalidArguments (1)."
}

Write-Host "Packaged startup bootstrap validation passed ($Mode): $artifactRoot"

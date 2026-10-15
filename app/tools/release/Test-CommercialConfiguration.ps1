[CmdletBinding()]
param(
    [ValidateSet("Authoring", "Production")]
    [string]$Mode = "Authoring",
    [string]$ConfigurationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Release.Common.ps1")

$repositoryRoot = Get-WinShotRepositoryRoot
if ([string]::IsNullOrWhiteSpace($ConfigurationPath)) {
    $ConfigurationPath = Join-Path $repositoryRoot "packaging\commercial-config.json"
}
$configPath = [System.IO.Path]::GetFullPath($ConfigurationPath)
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Commercial configuration is missing."
}
$configFile = Get-Item -LiteralPath $configPath
if ($configFile.Length -lt 2 -or $configFile.Length -gt (32 * 1024)) {
    throw "Commercial configuration must contain 2 through 32768 bytes."
}
try {
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "Commercial configuration is not valid bounded JSON."
}

function Assert-ExactProperties {
    param([object]$Object, [string[]]$Expected, [string]$Context)
    $actual = Get-OrdinalSortedStrings -Values @($Object.PSObject.Properties.Name)
    $wanted = Get-OrdinalSortedStrings -Values $Expected
    if (($actual -join "`n") -cne ($wanted -join "`n")) {
        throw "$Context must contain the exact reviewed property set."
    }
}
function Assert-PositiveInteger {
    param([object]$Value, [string]$Context)
    if (($Value -isnot [long] -and $Value -isnot [int]) -or [long]$Value -le 0) {
        throw "$Context must be a positive integer."
    }
}
function Assert-Boolean {
    param([object]$Value, [string]$Context)
    if ($Value -isnot [bool]) { throw "$Context must be a Boolean." }
}

Assert-ExactProperties $config @("schemaVersion", "contractRevision", "applicationMajor", "licenseMode", "provider", "storefront") "Commercial configuration"
if ($config.schemaVersion -ne 1 -or [string]$config.licenseMode -cne "offline-signed") {
    throw "Only the reviewed Stillmark account and Stripe license mode is supported."
}
Assert-PositiveInteger $config.contractRevision "contractRevision"
Assert-PositiveInteger $config.applicationMajor "applicationMajor"
$projectVersion = Get-ProjectReleaseVersion -RepositoryRoot $repositoryRoot
if ([int]$config.applicationMajor -ne [int]$projectVersion.Split('.')[0]) {
    throw "Commercial applicationMajor must equal the product major."
}
if ($null -ne $config.provider) { throw "The Stillmark Stripe contract must not include a legacy provider configuration." }
Assert-ExactProperties $config.storefront @("checkoutApproved", "buyerTermsApproved", "primaryVariantId", "checkoutUrl", "accountUrl") "storefront"
Assert-Boolean $config.storefront.checkoutApproved "checkoutApproved"
Assert-Boolean $config.storefront.buyerTermsApproved "buyerTermsApproved"
if ([string]$config.storefront.accountUrl -cne "https://getstillmark.com/manage-license") {
    throw "The account URL must be the exact Stillmark license-manager URL."
}
if ([bool]$config.storefront.checkoutApproved -ne [bool]$config.storefront.buyerTermsApproved) {
    throw "Checkout and buyer-terms approvals must move together."
}
if ([bool]$config.storefront.checkoutApproved) {
    if ($null -ne $config.storefront.primaryVariantId -or
        [string]$config.storefront.checkoutUrl -cne "https://getstillmark.com/pricing") {
        throw "Stripe checkout must use the exact Stillmark pricing URL and no provider variant."
    }
} elseif ($null -ne $config.storefront.primaryVariantId -or
    -not [string]::IsNullOrEmpty([string]$config.storefront.checkoutUrl)) {
    throw "Disabled checkout cannot retain a primary variant or URL."
}
if ($Mode -eq "Production" -and -not [bool]$config.storefront.checkoutApproved) {
    throw "Production requires approved Stripe checkout and buyer terms."
}

[pscustomobject]@{
    Result = "PASS_COMMERCIAL_CONFIGURATION"
    Mode = $Mode.ToUpperInvariant()
    ConfigurationPath = $configPath
    ConfigurationSha256 = Get-Sha256Hex -Path $configPath
    ContractRevision = [int]$config.contractRevision
    ApplicationMajor = [int]$config.applicationMajor
    LicenseMode = [string]$config.licenseMode
    Provider = $null
    CheckoutApproved = [bool]$config.storefront.checkoutApproved
    BuyerTermsApproved = [bool]$config.storefront.buyerTermsApproved
    PrimaryVariantId = $null
}

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$validator = Join-Path $PSScriptRoot "Test-CommercialConfiguration.ps1"
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
$tempRoot = Join-Path $tempBase "WinShotCommercialConfigTests-$([Guid]::NewGuid().ToString('N'))"
$created = $false

function Write-Configuration([object]$Configuration, [string]$Name) {
    $path = Join-Path $tempRoot $Name
    [System.IO.File]::WriteAllText($path, (($Configuration | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
    return $path
}
function Assert-Refused([scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch [regex]::Escape($Expected)) { throw "Expected refusal containing '$Expected'." }
        return
    }
    throw "Expected commercial configuration refusal containing '$Expected'."
}
function Copy-Configuration([object]$Configuration) { return $Configuration | ConvertTo-Json -Depth 8 | ConvertFrom-Json }

try {
    [System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    $created = $true
    $base = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\..\packaging\commercial-config.json") -Raw | ConvertFrom-Json
    $offline = Copy-Configuration $base
    $offline.storefront.checkoutApproved = $false
    $offline.storefront.buyerTermsApproved = $false
    $offline.storefront.checkoutUrl = ""
    $offlinePath = Write-Configuration $offline "offline.json"
    Assert-Refused { & $validator -Mode Production -ConfigurationPath $offlinePath } "Production requires approved Stripe checkout"

    $stripe = Copy-Configuration $base
    $stripe.storefront.checkoutApproved = $true
    $stripe.storefront.buyerTermsApproved = $true
    $stripe.storefront.checkoutUrl = "https://getstillmark.com/pricing"
    $stripePath = Write-Configuration $stripe "valid-stripe.json"
    $authoring = & $validator -Mode Authoring -ConfigurationPath $stripePath
    $production = & $validator -Mode Production -ConfigurationPath $stripePath
    if ($authoring.Result -ne "PASS_COMMERCIAL_CONFIGURATION" -or $production.Result -ne "PASS_COMMERCIAL_CONFIGURATION") {
        throw "Valid Stripe account checkout did not pass both commercial configuration modes."
    }

    $legacy = Copy-Configuration $stripe
    $legacy.licenseMode = "unsupported-provider"
    $legacyPath = Write-Configuration $legacy "removed-provider.json"
    Assert-Refused { & $validator -Mode Authoring -ConfigurationPath $legacyPath } "Only the reviewed Stillmark account and Stripe license mode"

    $unknown = Copy-Configuration $stripe
    $unknown | Add-Member -NotePropertyName unknown -NotePropertyValue $true
    $unknownPath = Write-Configuration $unknown "unknown.json"
    Assert-Refused { & $validator -Mode Authoring -ConfigurationPath $unknownPath } "exact reviewed property set"

    $mixed = Copy-Configuration $stripe
    $mixed.storefront.buyerTermsApproved = $false
    $mixedPath = Write-Configuration $mixed "mixed.json"
    Assert-Refused { & $validator -Mode Authoring -ConfigurationPath $mixedPath } "must move together"

    $badUrl = Copy-Configuration $stripe
    $badUrl.storefront.checkoutUrl = "https://evil.example/pricing"
    $badUrlPath = Write-Configuration $badUrl "bad-url.json"
    Assert-Refused { & $validator -Mode Authoring -ConfigurationPath $badUrlPath } "exact Stillmark pricing URL"

    [pscustomobject]@{
        Result = "PASS_COMMERCIAL_CONFIGURATION_TESTS"
        PositiveModes = 2
        RefusalCases = 5
        TemporaryDirectoryRemoved = $true
    }
}
finally {
    if ($created) {
        $resolved = [System.IO.Path]::GetFullPath($tempRoot)
        if ([System.IO.Path]::GetDirectoryName($resolved) -cne $tempBase -or [System.IO.Path]::GetFileName($resolved) -notmatch '^WinShotCommercialConfigTests-[0-9a-f]{32}$') {
            throw "Refusing to remove a commercial-test directory outside the GUID-owned scope."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Performance.Common.ps1')

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        $Expected,

        [Parameter(Mandatory = $true)]
        $Actual,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function New-TestSample {
    param(
        [int]$Index,
        [string]$Phase,
        [double]$PrivateBytes,
        [double]$WorkingSetBytes,
        [double]$HandleCount,
        [double]$ThreadCount,
        [double]$GdiObjectCount,
        [double]$UserObjectCount,
        [double]$CpuPercent,
        [bool]$Responding = $true
    )

    return [pscustomobject]@{
        SampleIndex = $Index
        Phase = $Phase
        PrivateBytes = $PrivateBytes
        WorkingSetBytes = $WorkingSetBytes
        HandleCount = $HandleCount
        ThreadCount = $ThreadCount
        GdiObjectCount = $GdiObjectCount
        UserObjectCount = $UserObjectCount
        CpuPercent = $CpuPercent
        Responding = $Responding
    }
}

$medianOdd = Get-WinShotMedian -Values @(9, 1, 5)
Assert-Equal -Expected 5 -Actual $medianOdd -Message 'Odd median failed.'

$medianEven = Get-WinShotMedian -Values @(1, 9, 5, 3)
Assert-Equal -Expected 4 -Actual $medianEven -Message 'Even median failed.'

$outlierGrowth = Get-WinShotRobustGrowth -Values @(10, 10, 1000, 12, 12, 12) -WindowSize 3
Assert-Equal -Expected 2 -Actual $outlierGrowth.Growth -Message 'Median windows should ignore a single baseline outlier.'

$budgets = @{
    MaxPrivateBytesGrowth = 10
    MaxWorkingSetBytesGrowth = 10
    MaxHandleCountGrowth = 10
    MaxThreadCountGrowth = 10
    MaxGdiObjectCountGrowth = 10
    MaxUserObjectCountGrowth = 10
    MaxAverageCpuPercent = 10
    MaxUnresponsiveSamples = 0
}

$passingSamples = @(
    New-TestSample -Index 0 -Phase 'Warmup' -PrivateBytes 9999 -WorkingSetBytes 9999 -HandleCount 9999 -ThreadCount 9999 -GdiObjectCount 9999 -UserObjectCount 9999 -CpuPercent 99
    New-TestSample -Index 1 -Phase 'Measurement' -PrivateBytes 10 -WorkingSetBytes 10 -HandleCount 10 -ThreadCount 10 -GdiObjectCount 10 -UserObjectCount 10 -CpuPercent 1
    New-TestSample -Index 2 -Phase 'Measurement' -PrivateBytes 10 -WorkingSetBytes 10 -HandleCount 10 -ThreadCount 10 -GdiObjectCount 10 -UserObjectCount 10 -CpuPercent 1
    New-TestSample -Index 3 -Phase 'Measurement' -PrivateBytes 1000 -WorkingSetBytes 1000 -HandleCount 1000 -ThreadCount 1000 -GdiObjectCount 1000 -UserObjectCount 1000 -CpuPercent 1
    New-TestSample -Index 4 -Phase 'Measurement' -PrivateBytes 12 -WorkingSetBytes 12 -HandleCount 12 -ThreadCount 12 -GdiObjectCount 12 -UserObjectCount 12 -CpuPercent 1
    New-TestSample -Index 5 -Phase 'Measurement' -PrivateBytes 12 -WorkingSetBytes 12 -HandleCount 12 -ThreadCount 12 -GdiObjectCount 12 -UserObjectCount 12 -CpuPercent 1
    New-TestSample -Index 6 -Phase 'Measurement' -PrivateBytes 12 -WorkingSetBytes 12 -HandleCount 12 -ThreadCount 12 -GdiObjectCount 12 -UserObjectCount 12 -CpuPercent 1
)
$passingResult = Test-WinShotResidentBudgets -Samples $passingSamples -WindowSize 3 -MinimumAnalysisSamples 6 -Budgets $budgets
Assert-Equal -Expected $true -Actual $passingResult.SufficientData -Message 'Passing data should be sufficient.'
Assert-Equal -Expected $true -Actual $passingResult.OverallPassed -Message 'Robust passing sample set should pass.'

$failingSamples = @()
for ($index = 0; $index -lt 6; $index++) {
    $value = if ($index -lt 3) { 10 } else { 30 }
    $failingSamples += New-TestSample -Index $index -Phase 'Measurement' -PrivateBytes $value -WorkingSetBytes $value -HandleCount $value -ThreadCount $value -GdiObjectCount $value -UserObjectCount $value -CpuPercent 1
}
$failingResult = Test-WinShotResidentBudgets -Samples $failingSamples -WindowSize 3 -MinimumAnalysisSamples 6 -Budgets $budgets
Assert-Equal -Expected $false -Actual $failingResult.OverallPassed -Message 'Sustained resource growth above budget should fail.'

$insufficientResult = Test-WinShotResidentBudgets -Samples $failingSamples[0..3] -WindowSize 3 -MinimumAnalysisSamples 6 -Budgets $budgets
Assert-Equal -Expected $false -Actual $insufficientResult.SufficientData -Message 'Too few samples must be rejected.'
Assert-Equal -Expected $false -Actual $insufficientResult.OverallPassed -Message 'Insufficient evidence must not pass.'

$measureSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Measure-WinShotResident.ps1') -Raw
$forbiddenMutationPatterns = @(
    '\bStart-Process\b',
    '\bStop-Process\b',
    '\.Kill\s*\(',
    '\[Diagnostics\.Process\]::Start\s*\('
)
foreach ($pattern in $forbiddenMutationPatterns) {
    if ($measureSource -match $pattern) {
        throw "Measurement script contains forbidden process mutation pattern: $pattern"
    }
}

$workloadPath = Join-Path $PSScriptRoot 'Invoke-WinShotDashboardWorkload.ps1'
$workloadSource = Get-Content -LiteralPath $workloadPath -Raw
$workloadForbiddenPatterns = @(
    '\bStart-Process\b',
    '\bStop-Process\b',
    '\.Kill\s*\(',
    '\.CloseMainWindow\s*\(',
    '\btaskkill(?:\.exe)?\b',
    '\bRegistry\b',
    '\bSettings\b',
    '\bSet-ItemProperty\b',
    '\bNew-ItemProperty\b',
    '\bRemove-ItemProperty\b',
    '\bSendKeys\b',
    '\bSendInput\b',
    '\bSendMessage\b',
    '\.Arguments\s*=',
    '\.ArgumentList\b'
)
foreach ($pattern in $workloadForbiddenPatterns) {
    if ($workloadSource -match $pattern) {
        throw "Dashboard workload contains forbidden mutation pattern: $pattern"
    }
}

if ($workloadSource -notmatch '\[Diagnostics\.Process\]::Start\s*\(\$startInfo\)') {
    throw 'Dashboard workload must launch its helper from the validated ProcessStartInfo.'
}
if ($workloadSource -notmatch 'FileName\s*=\s*\$normalizedExpectedPath') {
    throw 'Dashboard workload helper must launch the exact validated executable path.'
}
if ($workloadSource -notmatch 'UseShellExecute\s*=\s*\$false') {
    throw 'Dashboard workload helper must disable shell execution.'
}
if ($workloadSource -notmatch 'GetWindowThreadProcessId') {
    throw 'Dashboard workload must verify window ownership before posting WM_CLOSE.'
}
if ($workloadSource -notmatch 'IsWindowVisible') {
    throw 'Dashboard workload must distinguish the resident hidden-to-tray state.'
}
if ($workloadSource -notmatch 'PostMessage') {
    throw 'Dashboard workload must post WM_CLOSE instead of forcing process termination.'
}

$parseTokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $workloadPath,
    [ref]$parseTokens,
    [ref]$parseErrors)
Assert-Equal -Expected 0 -Actual @($parseErrors).Count -Message 'Dashboard workload syntax validation failed.'

$currentHost = Get-Process -Id $PID
$currentHostPath = $currentHost.MainModule.FileName
$mismatchedStartTicks = $currentHost.StartTime.ToUniversalTime().Ticks + 1
$identityFailureOutput = @(
    & $currentHostPath -NoProfile -ExecutionPolicy Bypass -File $workloadPath `
        -ProcessId $currentHost.Id `
        -ExecutablePath $currentHostPath `
        -StartTimeUtcTicks $mismatchedStartTicks `
        -CycleCount 1 2>&1
)
$identityFailureExitCode = $LASTEXITCODE
Assert-Equal -Expected 2 -Actual $identityFailureExitCode -Message 'Start-time mismatch must fail closed.'
Assert-Equal -Expected 1 -Actual $identityFailureOutput.Count -Message 'Failure output must be one summary line.'
$identityFailureSummary = $identityFailureOutput[0] | ConvertFrom-Json
Assert-Equal -Expected $false -Actual $identityFailureSummary.OverallPassed -Message 'Identity mismatch must not pass.'
Assert-Equal -Expected 0 -Actual $identityFailureSummary.CompletedCycleCount -Message 'Identity mismatch must launch no workload cycle.'
Assert-Equal -Expected 'ResidentStartTimeMismatch' -Actual $identityFailureSummary.FailureCode -Message 'Identity mismatch failure code changed.'
$summaryPropertyNames = @($identityFailureSummary.PSObject.Properties.Name) -join ','
Assert-Equal `
    -Expected 'SchemaVersion,EvidenceKind,RequestedCycleCount,CompletedCycleCount,ActualDurationMilliseconds,HelperExitLatency,WindowOpenLatency,WindowCloseLatency,FailureCode,OverallPassed' `
    -Actual $summaryPropertyNames `
    -Message 'Dashboard workload summary schema exposed or omitted fields.'
if ($identityFailureOutput[0] -match [regex]::Escape($currentHostPath)) {
    throw 'Dashboard workload failure summary exposed the executable path.'
}

$performanceDotnet = Resolve-WinShotPinnedDotnet
$regionHarness = Join-Path `
    $PSScriptRoot `
    'region-effects\Test-RegionEffectHarness.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $regionHarness `
    -DotnetPath $performanceDotnet
if ($LASTEXITCODE -ne 0) {
    throw "The region-effect harness gate failed with exit code $LASTEXITCODE."
}

$editorOutputHarness = Join-Path `
    $PSScriptRoot `
    'editor-output\Test-EditorOutputHarness.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $editorOutputHarness `
    -DotnetPath $performanceDotnet
if ($LASTEXITCODE -ne 0) {
    throw "The editor-output harness gate failed with exit code $LASTEXITCODE."
}

'Performance harness tests passed.'

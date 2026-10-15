Set-StrictMode -Version Latest

function Get-WinShotPinnedSdkVersion {
    $repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $globalJsonPath = Join-Path $repositoryRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw "The repository SDK pin was not found at '$globalJsonPath'."
    }

    $configuration = Get-Content -LiteralPath $globalJsonPath -Raw |
        ConvertFrom-Json
    $version = [string]$configuration.sdk.version
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw 'The repository global.json does not contain a valid exact SDK version.'
    }

    return $version
}

function Resolve-WinShotPinnedDotnet {
    $expectedVersion = Get-WinShotPinnedSdkVersion
    $localDotnet = Join-Path `
        ([Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)) `
        "Microsoft\dotnet-winshot-$expectedVersion\dotnet.exe"
    $candidates = @($localDotnet)
    try {
        $candidates += (Get-Command dotnet -ErrorAction Stop).Source
    }
    catch {
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $versionOutput = @(& $candidate --version 2>$null)
        $versionExitCode = $LASTEXITCODE
        $version = if ($versionOutput.Count -eq 1) {
            ([string]$versionOutput[0]).Trim()
        }
        else {
            ''
        }
        if ($versionExitCode -eq 0 -and $version -ceq $expectedVersion) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    throw "The performance gates require the exact SDK pinned in global.json ($expectedVersion)."
}

function Get-WinShotMedian {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [double[]]$Values
    )

    if ($Values.Count -eq 0) {
        throw 'At least one value is required.'
    }

    [double[]]$sorted = $Values | Sort-Object
    $middle = [int][Math]::Floor($sorted.Count / 2)

    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-WinShotRobustGrowth {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [double[]]$Values,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 100000)]
        [int]$WindowSize
    )

    if ($Values.Count -lt ($WindowSize * 2)) {
        throw "At least $($WindowSize * 2) values are required for non-overlapping analysis windows."
    }

    [double[]]$baselineValues = $Values[0..($WindowSize - 1)]
    [double[]]$tailValues = $Values[($Values.Count - $WindowSize)..($Values.Count - 1)]
    $baseline = Get-WinShotMedian -Values $baselineValues
    $tail = Get-WinShotMedian -Values $tailValues

    return [pscustomobject][ordered]@{
        Baseline = $baseline
        Tail = $tail
        Growth = $tail - $baseline
    }
}

function Test-WinShotResidentBudgets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Samples,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 100000)]
        [int]$WindowSize,

        [Parameter(Mandatory = $true)]
        [ValidateRange(2, 100000)]
        [int]$MinimumAnalysisSamples,

        [Parameter(Mandatory = $true)]
        [hashtable]$Budgets
    )

    [object[]]$measurementSamples = @($Samples | Where-Object { $_.Phase -eq 'Measurement' })
    $requiredSamples = [Math]::Max($MinimumAnalysisSamples, $WindowSize * 2)

    if ($measurementSamples.Count -lt $requiredSamples) {
        return [pscustomobject][ordered]@{
            SufficientData = $false
            RequiredSampleCount = $requiredSamples
            MeasurementSampleCount = $measurementSamples.Count
            WindowSize = $WindowSize
            Checks = @()
            OverallPassed = $false
        }
    }

    $metricSpecifications = @(
        [pscustomobject]@{ Name = 'PrivateBytesGrowth'; Property = 'PrivateBytes'; BudgetKey = 'MaxPrivateBytesGrowth' },
        [pscustomobject]@{ Name = 'WorkingSetBytesGrowth'; Property = 'WorkingSetBytes'; BudgetKey = 'MaxWorkingSetBytesGrowth' },
        [pscustomobject]@{ Name = 'HandleCountGrowth'; Property = 'HandleCount'; BudgetKey = 'MaxHandleCountGrowth' },
        [pscustomobject]@{ Name = 'ThreadCountGrowth'; Property = 'ThreadCount'; BudgetKey = 'MaxThreadCountGrowth' },
        [pscustomobject]@{ Name = 'GdiObjectCountGrowth'; Property = 'GdiObjectCount'; BudgetKey = 'MaxGdiObjectCountGrowth' },
        [pscustomobject]@{ Name = 'UserObjectCountGrowth'; Property = 'UserObjectCount'; BudgetKey = 'MaxUserObjectCountGrowth' }
    )

    $checks = [System.Collections.Generic.List[object]]::new()

    foreach ($metric in $metricSpecifications) {
        [double[]]$values = @($measurementSamples | ForEach-Object { [double]($_.($metric.Property)) })
        $growth = Get-WinShotRobustGrowth -Values $values -WindowSize $WindowSize
        $budget = [double]$Budgets[$metric.BudgetKey]
        $checks.Add([pscustomobject][ordered]@{
            Name = $metric.Name
            Method = 'TailMedianMinusBaselineMedian'
            Baseline = $growth.Baseline
            Tail = $growth.Tail
            Observed = $growth.Growth
            Budget = $budget
            Passed = ($growth.Growth -le $budget)
        })
    }

    [double[]]$cpuValues = @(
        $measurementSamples |
            Where-Object { $null -ne $_.CpuPercent } |
            ForEach-Object { [double]$_.CpuPercent }
    )
    $averageCpu = if ($cpuValues.Count -gt 0) {
        ($cpuValues | Measure-Object -Average).Average
    }
    else {
        0.0
    }
    $checks.Add([pscustomobject][ordered]@{
        Name = 'AverageCpuPercent'
        Method = 'ArithmeticMeanPostWarmup'
        Baseline = $null
        Tail = $null
        Observed = [double]$averageCpu
        Budget = [double]$Budgets['MaxAverageCpuPercent']
        Passed = ([double]$averageCpu -le [double]$Budgets['MaxAverageCpuPercent'])
    })

    $unresponsiveCount = @($measurementSamples | Where-Object { -not [bool]$_.Responding }).Count
    $checks.Add([pscustomobject][ordered]@{
        Name = 'UnresponsiveSampleCount'
        Method = 'CountPostWarmup'
        Baseline = $null
        Tail = $null
        Observed = $unresponsiveCount
        Budget = [int]$Budgets['MaxUnresponsiveSamples']
        Passed = ($unresponsiveCount -le [int]$Budgets['MaxUnresponsiveSamples'])
    })

    return [pscustomobject][ordered]@{
        SufficientData = $true
        RequiredSampleCount = $requiredSamples
        MeasurementSampleCount = $measurementSamples.Count
        WindowSize = $WindowSize
        Checks = @($checks)
        OverallPassed = (@($checks | Where-Object { -not $_.Passed }).Count -eq 0)
    }
}

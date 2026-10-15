[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Get-Location).Path,
    [switch]$SelfTest,
    [ValidateRange(1024, 16777216)]
    [long]$MaximumTextFileBytes = 4194304
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function New-SecretRegex {
    param(
        [Parameter(Mandatory = $true)]
        [System.Reflection.ConstructorInfo]$Constructor,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)]
        [System.Text.RegularExpressions.RegexOptions]$Options
    )

    $constructorArguments = New-Object object[] 2
    $constructorArguments[0] = $Pattern
    $constructorArguments[1] = $Options
    return $Constructor.Invoke($constructorArguments)
}

function Get-GitTrackedPaths {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $git = (Get-Command git -ErrorAction Stop).Source
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $git
    $startInfo.Arguments = 'ls-files -z --'
    $startInfo.WorkingDirectory = $RootPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $startInfo.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'git could not be started.'
        }
        $output = $process.StandardOutput.ReadToEnd()
        $null = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git ls-files failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }

    return @(
        $output.Split(
            [char[]]@([char]0),
            [System.StringSplitOptions]::RemoveEmptyEntries)
    )
}

function Get-SecretRules {
    $options = [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    $singleLineOptions = $options -bor
        [System.Text.RegularExpressions.RegexOptions]::Singleline
    $regexConstructor =
        [System.Text.RegularExpressions.Regex].GetConstructor(
            [type[]]@(
                [string],
                [System.Text.RegularExpressions.RegexOptions]))
    if ($null -eq $regexConstructor) {
        throw 'The required regular-expression constructor is unavailable.'
    }
    $rules = New-Object System.Collections.Generic.List[object]

    foreach ($keyType in @(
            'PRIVATE KEY',
            'ENCRYPTED PRIVATE KEY',
            'RSA PRIVATE KEY',
            'DSA PRIVATE KEY',
            'EC PRIVATE KEY',
            'OPENSSH PRIVATE KEY',
            'PGP PRIVATE KEY BLOCK'
        )) {
        # Split the boundary words so this scanner cannot match its own source.
        $beginBoundary = '-----BE' + 'GIN ' +
            [Regex]::Escape($keyType) + '-----'
        $endBoundary = '-----E' + 'ND ' +
            [Regex]::Escape($keyType) + '-----'
        $rules.Add([PSCustomObject]@{
                Id = 'CompletePemPrivateKey'
                Regex = New-SecretRegex `
                    $regexConstructor `
                    ($beginBoundary + '.+?' + $endBoundary) `
                    $singleLineOptions
            })
    }

    $rules.Add([PSCustomObject]@{
            Id = 'GitHubClassicToken'
            Regex = New-SecretRegex `
                $regexConstructor `
                '(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{36}(?![A-Za-z0-9])' `
                $options
        })
    $rules.Add([PSCustomObject]@{
            Id = 'GitHubFineGrainedToken'
            Regex = New-SecretRegex `
                $regexConstructor `
                '(?<![A-Za-z0-9_])github_pat_[A-Za-z0-9_]{70,255}(?![A-Za-z0-9_])' `
                $options
        })
    $rules.Add([PSCustomObject]@{
            Id = 'AwsAccessKeyId'
            Regex = New-SecretRegex `
                $regexConstructor `
                '(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])' `
                $options
        })
    $rules.Add([PSCustomObject]@{
            Id = 'AwsSecretAccessKeyAssignment'
            Regex = New-SecretRegex `
                $regexConstructor `
                '(?i)(?:aws_secret_access_key|secretAccessKey)\s*[''":=]+\s*[''"]?[A-Za-z0-9/+=]{40}(?![A-Za-z0-9/+=])' `
                $options
        })
    $rules.Add([PSCustomObject]@{
            Id = 'StripeSecretToken'
            Regex = New-SecretRegex `
                $regexConstructor `
                '(?<![A-Za-z0-9])(?:sk|rk)_(?:live|test)_[A-Za-z0-9]{16,}(?![A-Za-z0-9])' `
                $options
        })
    $rules.Add([PSCustomObject]@{
            Id = 'StripeWebhookSecret'
            Regex = New-SecretRegex `
                $regexConstructor `
                '(?<![A-Za-z0-9])whsec_[A-Za-z0-9]{16,}(?![A-Za-z0-9])' `
                $options
        })
    $rules.Add([PSCustomObject]@{
            Id = 'JwtBearerToken'
            Regex = New-SecretRegex `
                $regexConstructor `
                '(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{10,}(?![A-Za-z0-9_-])' `
                $options
        })

    return $rules.ToArray()
}

function Add-Finding {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Findings,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RuleId
    )

    $Findings.Add([PSCustomObject]@{
            Path = $Path
            RuleId = $RuleId
        })
}

function Read-BoundedUtf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$MaximumBytes
    )

    $stream = New-Object System.IO.FileStream(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        if ($stream.Length -gt $MaximumBytes) {
            return [PSCustomObject]@{
                Status = 'TooLarge'
                Text = $null
            }
        }

        $length = [int]$stream.Length
        $bytes = New-Object byte[] $length
        $offset = 0
        while ($offset -lt $length) {
            $read = $stream.Read($bytes, $offset, $length - $offset)
            if ($read -eq 0) {
                return [PSCustomObject]@{
                    Status = 'ChangedDuringRead'
                    Text = $null
                }
            }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) {
            return [PSCustomObject]@{
                Status = 'ChangedDuringRead'
                Text = $null
            }
        }

        $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
        return [PSCustomObject]@{
            Status = 'Read'
            Text = $utf8.GetString($bytes)
        }
    }
    catch [System.Text.DecoderFallbackException] {
        return [PSCustomObject]@{
            Status = 'InvalidUtf8'
            Text = $null
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-TrackedSourceSecretScan {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][long]$MaximumBytes
    )

    $resolvedRoot = (Resolve-Path -LiteralPath $RootPath -ErrorAction Stop).Path
    $rootPrefix = $resolvedRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $forbiddenExtensions = @(
        '.key', '.p8', '.p12', '.pfx', '.pk8', '.pkcs12', '.ppk',
        '.pvk', '.snk', '.jks', '.keystore', '.kdbx'
    )
    $binaryExtensions = @(
        '.7z', '.a', '.avi', '.bmp', '.class', '.db', '.dll', '.doc',
        '.docx', '.dylib', '.eot', '.exe', '.flac', '.gif', '.gz',
        '.ico', '.iso', '.jar', '.jpeg', '.jpg', '.lib', '.m4a',
        '.mkv', '.mov', '.mp3', '.mp4', '.o', '.obj', '.ogg', '.otf',
        '.pdf', '.png', '.ppt', '.pptx', '.psd', '.rar', '.so',
        '.sqlite', '.tar', '.tif', '.tiff', '.ttf', '.wav', '.webm',
        '.webp', '.woff', '.woff2', '.xls', '.xlsx', '.xz', '.zip'
    )
    $rules = @(Get-SecretRules)
    $findings = New-Object System.Collections.Generic.List[object]

    foreach ($relativePath in @(Get-GitTrackedPaths -RootPath $resolvedRoot)) {
        $fullPath = [System.IO.Path]::GetFullPath(
            (Join-Path $resolvedRoot $relativePath))
        if (-not $fullPath.StartsWith(
                $rootPrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            Add-Finding $findings $relativePath 'UnsafeTrackedPath'
            continue
        }

        try {
            $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        }
        catch {
            Add-Finding $findings $relativePath 'UnreadableTrackedEntry'
            continue
        }
        if (-not ($item -is [System.IO.FileInfo])) {
            Add-Finding $findings $relativePath 'TrackedEntryNotRegular'
            continue
        }
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $linkTypeProperty = $item.PSObject.Properties['LinkType']
            $targetProperty = $item.PSObject.Properties['Target']
            $hasLinkType = $null -ne $linkTypeProperty -and
                -not [string]::IsNullOrWhiteSpace(
                    [string]$linkTypeProperty.Value)
            $hasLinkTarget = $null -ne $targetProperty -and
                @($targetProperty.Value).Count -gt 0
            if ($hasLinkType -or $hasLinkTarget) {
                Add-Finding $findings $relativePath 'TrackedReparsePoint'
                continue
            }
            # OneDrive Files On-Demand marks hydrated regular files as reparse points without a
            # link type or target. They remain bounded regular files and must be scanned normally.
        }

        $extension = [System.IO.Path]::GetExtension($fullPath).ToLowerInvariant()
        if ($forbiddenExtensions -contains $extension) {
            Add-Finding $findings $relativePath 'ForbiddenSecretFileExtension'
            continue
        }
        if ($binaryExtensions -contains $extension) {
            continue
        }

        try {
            $readResult = Read-BoundedUtf8Text `
                -Path $fullPath `
                -MaximumBytes $MaximumBytes
        }
        catch {
            Add-Finding $findings $relativePath 'UnreadableTrackedText'
            continue
        }
        if ($readResult.Status -ne 'Read') {
            $readRule = 'TrackedTextChangedDuringScan'
            switch ($readResult.Status) {
                'TooLarge' {
                    $readRule = 'TrackedTextExceedsScanLimit'
                }
                'InvalidUtf8' {
                    $readRule = 'TrackedTextIsNotUtf8'
                }
            }
            Add-Finding $findings $relativePath $readRule
            continue
        }

        foreach ($rule in $rules) {
            if ($rule.Regex.IsMatch($readResult.Text)) {
                Add-Finding $findings $relativePath $rule.Id
            }
        }
    }

    return $findings.ToArray()
}

function Add-GitTrackedFile {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    & git -c core.autocrlf=false -C $RootPath add -- $RelativePath 2>$null |
        Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Self-test git add failed with exit code $LASTEXITCODE."
    }
}

function New-SecretScanTestRepository {
    param(
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $path = Join-Path $ParentPath $Name
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    & git -C $path init -q 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Self-test git init failed with exit code $LASTEXITCODE."
    }
    return $path
}

function Assert-Finding {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Findings,
        [Parameter(Mandatory = $true)][string]$RuleId,
        [Parameter(Mandatory = $true)][string]$CaseName
    )

    if (@($Findings | Where-Object { $_.RuleId -eq $RuleId }).Count -eq 0) {
        throw "Source-secret self-test '$CaseName' did not produce rule '$RuleId'."
    }
}

function Invoke-SourceSecretSelfTest {
    $tempParent = [System.IO.Path]::GetFullPath(
        (Join-Path ([System.IO.Path]::GetTempPath()) (
            'WinShotSecretScanTests-' + [Guid]::NewGuid().ToString('N'))))
    New-Item -ItemType Directory -Path $tempParent | Out-Null
    try {
        $safeRepo = New-SecretScanTestRepository $tempParent 'safe'
        [System.IO.File]::WriteAllText(
            (Join-Path $safeRepo 'README.md'),
            'No credentials are present in this tracked source fixture.')
        [System.IO.File]::WriteAllText(
            (Join-Path $safeRepo 'ignored.png'),
            ('gh' + 'p_' + ('A' * 36)))
        [System.IO.File]::WriteAllText(
            (Join-Path $safeRepo 'untracked.txt'),
            ('sk_' + 'live_' + ('B' * 24)))
        Add-GitTrackedFile $safeRepo 'README.md'
        Add-GitTrackedFile $safeRepo 'ignored.png'
        $safeFindings = @(
            Invoke-TrackedSourceSecretScan $safeRepo $MaximumTextFileBytes)
        if ($safeFindings.Count -ne 0) {
            throw 'Source-secret self-test rejected the safe tracked fixture.'
        }

        $selfRepo = New-SecretScanTestRepository $tempParent 'self-source'
        Copy-Item -LiteralPath $PSCommandPath -Destination (
            Join-Path $selfRepo 'Test-SourceSecrets.ps1')
        Add-GitTrackedFile $selfRepo 'Test-SourceSecrets.ps1'
        $selfFindings = @(
            Invoke-TrackedSourceSecretScan $selfRepo $MaximumTextFileBytes)
        if ($selfFindings.Count -ne 0) {
            throw 'Source-secret scanner matched its own tracked source.'
        }

        $missingRepo = New-SecretScanTestRepository $tempParent 'missing-entry'
        [System.IO.File]::WriteAllText(
            (Join-Path $missingRepo 'missing.txt'),
            'This tracked fixture is removed before scanning.')
        Add-GitTrackedFile $missingRepo 'missing.txt'
        Remove-Item -LiteralPath (Join-Path $missingRepo 'missing.txt') -Force
        Assert-Finding `
            @(Invoke-TrackedSourceSecretScan $missingRepo $MaximumTextFileBytes) `
            'UnreadableTrackedEntry' `
            'missing tracked entry'

        $cases = @(
            [PSCustomObject]@{
                Name = 'private extension'
                File = 'credential.pfx'
                Content = ''
                Rule = 'ForbiddenSecretFileExtension'
            },
            [PSCustomObject]@{
                Name = 'complete private key block'
                File = 'credential.txt'
                Content = (
                    ('-----BE' + 'GIN PRIVATE KEY-----') + "`n" +
                    ('Q' * 48) + "`n" +
                    ('-----E' + 'ND PRIVATE KEY-----'))
                Rule = 'CompletePemPrivateKey'
            },
            [PSCustomObject]@{
                Name = 'GitHub token'
                File = 'credential.txt'
                Content = ('gh' + 'p_' + ('C' * 36))
                Rule = 'GitHubClassicToken'
            },
            [PSCustomObject]@{
                Name = 'GitHub fine-grained token'
                File = 'credential.txt'
                Content = ('github_' + 'pat_' + ('C' * 80))
                Rule = 'GitHubFineGrainedToken'
            },
            [PSCustomObject]@{
                Name = 'AWS access key'
                File = 'credential.txt'
                Content = ('AK' + 'IA' + ('D' * 16))
                Rule = 'AwsAccessKeyId'
            },
            [PSCustomObject]@{
                Name = 'AWS secret assignment'
                File = 'credential.txt'
                Content = (
                    ('aws_' + 'secret_access_key = ') + ('D' * 40))
                Rule = 'AwsSecretAccessKeyAssignment'
            },
            [PSCustomObject]@{
                Name = 'Stripe secret'
                File = 'credential.txt'
                Content = ('sk_' + 'test_' + ('E' * 24))
                Rule = 'StripeSecretToken'
            },
            [PSCustomObject]@{
                Name = 'Stripe webhook secret'
                File = 'credential.txt'
                Content = ('wh' + 'sec_' + ('E' * 24))
                Rule = 'StripeWebhookSecret'
            },
            [PSCustomObject]@{
                Name = 'JWT bearer'
                File = 'credential.txt'
                Content = (
                    ('ey' + 'J' + ('F' * 8)) + '.' +
                    ('G' * 12) + '.' +
                    ('H' * 20))
                Rule = 'JwtBearerToken'
            }
        )

        $index = 0
        foreach ($case in $cases) {
            $index++
            $repo = New-SecretScanTestRepository $tempParent (
                'mutation-' + $index)
            [System.IO.File]::WriteAllText(
                (Join-Path $repo $case.File),
                $case.Content)
            Add-GitTrackedFile $repo $case.File
            $findings = @(
                Invoke-TrackedSourceSecretScan $repo $MaximumTextFileBytes)
            Assert-Finding $findings $case.Rule $case.Name
        }

        $oversizeRepo = New-SecretScanTestRepository $tempParent 'oversize'
        [System.IO.File]::WriteAllText(
            (Join-Path $oversizeRepo 'large.txt'),
            ('I' * 1025))
        Add-GitTrackedFile $oversizeRepo 'large.txt'
        Assert-Finding `
            @(Invoke-TrackedSourceSecretScan $oversizeRepo 1024) `
            'TrackedTextExceedsScanLimit' `
            'oversized text'

        $encodingRepo = New-SecretScanTestRepository $tempParent 'encoding'
        [System.IO.File]::WriteAllBytes(
            (Join-Path $encodingRepo 'invalid.txt'),
            [byte[]]@(0xFF, 0xFE, 0x00, 0x01))
        Add-GitTrackedFile $encodingRepo 'invalid.txt'
        Assert-Finding `
            @(Invoke-TrackedSourceSecretScan $encodingRepo $MaximumTextFileBytes) `
            'TrackedTextIsNotUtf8' `
            'invalid UTF-8'
    }
    finally {
        $expectedPrefix = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::GetTempPath()).TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        if ($tempParent.StartsWith(
                $expectedPrefix,
                [System.StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $tempParent)) {
            Remove-Item -LiteralPath $tempParent -Recurse -Force
        }
    }

    Write-Host 'Source-secret scanner self-tests passed.'
}

if ($SelfTest) {
    Invoke-SourceSecretSelfTest
    return
}

$scanFindings = @(
    Invoke-TrackedSourceSecretScan `
        -RootPath $RepositoryRoot `
        -MaximumBytes $MaximumTextFileBytes)
if ($scanFindings.Count -gt 0) {
    foreach ($finding in $scanFindings) {
        $safePath = $finding.Path -replace '[\x00-\x1F\x7F]', '?'
        Write-Host (
            "Source-secret finding: rule={0}; trackedFile={1}" -f
            $finding.RuleId,
            $safePath)
    }
    throw ((
            'Tracked-source secret validation failed with {0} finding(s). ' +
            'Matched credential content was intentionally not printed.') -f
        $scanFindings.Count)
}

Write-Host 'Tracked-source secret validation passed.'

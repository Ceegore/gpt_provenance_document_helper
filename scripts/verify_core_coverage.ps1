<#
.SYNOPSIS
    Coverage gate for AssetProvenanceHelper.Core headless library.
.DESCRIPTION
    Runs tests/AssetProvenanceHelper.Core.Tests with code coverage collection (with SAC-aware retry),
    evaluates lines, branches, and methods against code-coverage-core-baseline.json,
    and enforces minimum 80% line coverage, 75% branch coverage, plus an uncovered-count ratchet.
    Also verifies every production .cs file in Core appears in the Cobertura report.
#>

param(
    [string]$CoverageDir = "artifacts/coverage-core",
    [int]$SettleSeconds = 0,
    [switch]$UpdateBaseline,
    [switch]$NoRunTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $baselinePath = Join-Path $repoRoot "code-coverage-core-baseline.json"
    $coreSourceRoot = Join-Path $repoRoot "src/AssetProvenanceHelper.Core"

    # 1. Run Core tests with coverage if requested
    if (-not $NoRunTests) {
        $testProject = "tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj"
        $maxAttempts = 3
        $attempt = 1
        $success = $false

        while ($attempt -le $maxAttempts) {
            if ($SettleSeconds -gt 0) {
                Write-Host "Waiting ${SettleSeconds}s for assembly to settle before running tests..." -ForegroundColor DarkGray
                Start-Sleep -Seconds $SettleSeconds
            }

            Write-Host "Running AssetProvenanceHelper.Core.Tests with coverage (attempt $attempt/$maxAttempts)..." -ForegroundColor Cyan
            $stamp = [Guid]::NewGuid().ToString('N')
            $outFile = Join-Path $env:TEMP "aph-core-cov-$stamp.out.log"
            $errFile = Join-Path $env:TEMP "aph-core-cov-$stamp.err.log"

            $proc = Start-Process -FilePath 'dotnet' `
                                  -ArgumentList "test `"$testProject`" -c Release --no-build --collect:`"XPlat Code Coverage`" --results-directory `"$CoverageDir`"" `
                                  -NoNewWindow -PassThru `
                                  -RedirectStandardOutput $outFile `
                                  -RedirectStandardError $errFile
            try { $null = $proc.Handle } catch { }
            $proc.WaitForExit()

            $outText = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { "" }
            $errText = if (Test-Path $errFile) { Get-Content $errFile -Raw } else { "" }
            try { Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue } catch { }

            $output = $outText + "`n" + $errText
            Write-Host $output

            if ($output -match "0x800711C7") {
                Write-Warning "SAC environment block (0x800711C7) detected during test execution. Backing off 35s..."
                Start-Sleep -Seconds 35
                $attempt++
                continue
            }

            if ($proc.ExitCode -ne 0) {
                Write-Error "Core tests execution failed with exit code $($proc.ExitCode)."
                exit 1
            }

            $success = $true
            break
        }

        if (-not $success) {
            Write-Warning "SAC block persisted after $maxAttempts attempts. Exiting with environment block code 42."
            exit 42
        }
    }

    # 2. Locate freshest valid coverage.cobertura.xml
    $covFiles = Get-ChildItem -Path $CoverageDir -Filter "coverage.cobertura.xml" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending

    $validCovPath = $null
    $covXml = $null
    foreach ($f in $covFiles) {
        try {
            [xml]$candidateXml = Get-Content $f.FullName
            if ([int]$candidateXml.coverage.'lines-covered' -gt 0) {
                $validCovPath = $f.FullName
                $covXml = $candidateXml
                break
            }
        }
        catch { }
    }

    if (-not $validCovPath -or -not $covXml) {
        Write-Error "No valid coverage.cobertura.xml found under $CoverageDir"
        exit 1
    }

    Write-Host "Using Core coverage report: $validCovPath" -ForegroundColor Cyan

    # 3. Collect production classes (exclude obj/ paths)
    $allClasses = $covXml.SelectNodes("//class")
    $productionClasses = $allClasses | Where-Object {
        $_.filename -notmatch '^obj[\\/]' -and $_.filename -notmatch '[\\\/]obj[\\\/]'
    }

    # 4. Exact line / branch / method counters (production only)
    $linesTotal = 0
    $linesCovered = 0
    $methodsTotal = 0
    $methodsCovered = 0
    $uncoveredMethodsList = New-Object System.Collections.Generic.List[string]

    foreach ($cls in $productionClasses) {
        $linesNode = $cls.SelectNodes("lines/line")
        foreach ($line in $linesNode) {
            $linesTotal++
            if ([int]$line.hits -gt 0) { $linesCovered++ }
        }

        $methodNodes = $cls.SelectNodes("methods/method")
        foreach ($m in $methodNodes) {
            $methodsTotal++
            $lr = [double]$m.'line-rate'
            if ($lr -gt 0) {
                $methodsCovered++
            }
            else {
                $uncoveredMethodsList.Add("$($cls.filename) :: $($cls.name).$($m.name)$($m.signature)")
            }
        }
    }

    $branchesTotal = [int]$covXml.coverage.'branches-valid'
    $branchesCovered = [int]$covXml.coverage.'branches-covered'

    $lineRate = if ($linesTotal -gt 0) { [double]$linesCovered / $linesTotal } else { 0.0 }
    $branchRate = if ($branchesTotal -gt 0) { [double]$branchesCovered / $branchesTotal } else { 0.0 }
    $methodRate = if ($methodsTotal -gt 0) { [double]$methodsCovered / $methodsTotal } else { 0.0 }

    Write-Host ""
    Write-Host "== AssetProvenanceHelper.Core Coverage Summary ==" -ForegroundColor Cyan
    Write-Host ("Lines:    {0} / {1} ({2:P2})" -f $linesCovered, $linesTotal, $lineRate)
    Write-Host ("Branches: {0} / {1} ({2:P2})" -f $branchesCovered, $branchesTotal, $branchRate)
    Write-Host ("Methods:  {0} / {1} ({2:P2})" -f $methodsCovered, $methodsTotal, $methodRate)

    if ($uncoveredMethodsList.Count -gt 0) {
        Write-Host ""
        Write-Host "UNCOVERED METHODS ($($uncoveredMethodsList.Count)):" -ForegroundColor Yellow
        $uncoveredMethodsList | ForEach-Object { Write-Host "  - $_" }
    }

    # 5. Dynamic production file inventory vs. the report
    $noExecPath = Join-Path $repoRoot "code-coverage-core-no-executable-code.json"
    $noExecList = @()
    if (Test-Path $noExecPath) {
        $noExecJson = Get-Content $noExecPath -Raw | ConvertFrom-Json
        $noExecList = @($noExecJson.files)
    }

    $sources = @($covXml.coverage.sources.source)
    $reportedFullPaths = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($cls in $productionClasses) {
        $fn = $cls.filename
        foreach ($src in $sources) {
            $candidate = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($src, $fn))
            if (Test-Path $candidate) {
                $null = $reportedFullPaths.Add($candidate)
                break
            }
        }
    }

    $inventory = Get-ChildItem -Path $coreSourceRoot -Recurse -Filter "*.cs" |
        Where-Object { $_.FullName -notmatch '[\\\/](bin|obj)[\\\/]' }

    $failures = New-Object System.Collections.Generic.List[string]

    foreach ($item in $inventory) {
        $relPath = $item.FullName.Substring($coreSourceRoot.Length + 1).Replace('\', '/')
        if ($reportedFullPaths.Contains($item.FullName)) {
            if ($noExecList -contains $relPath) {
                $failures.Add("File listed in code-coverage-core-no-executable-code.json now has instrumented code and must be removed from that list: $relPath")
            }
        }
        else {
            if ($noExecList -notcontains $relPath) {
                $failures.Add("Core production file with no coverage entry and not in code-coverage-core-no-executable-code.json: $relPath")
            }
        }
    }

    # 6. Update baseline or verify against it
    if ($UpdateBaseline) {
        $newBaseline = [ordered]@{
            lines = $linesCovered
            totalLines = $linesTotal
            branches = $branchesCovered
            totalBranches = $branchesTotal
            methods = $methodsCovered
            totalMethods = $methodsTotal
            minLineRate = 0.80
            minBranchRate = 0.75
        }
        $newBaseline | ConvertTo-Json -Depth 4 | Set-Content $baselinePath -Encoding UTF8
        Write-Host "Core coverage baseline updated: $baselinePath" -ForegroundColor Green
        exit 0
    }

    if (-not (Test-Path $baselinePath)) {
        Write-Error "Baseline file not found: $baselinePath. Run with -UpdateBaseline to initialize."
        exit 1
    }

    $baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
    $minLineRate = if ($baseline.minLineRate) { [double]$baseline.minLineRate } else { 0.80 }
    $minBranchRate = if ($baseline.minBranchRate) { [double]$baseline.minBranchRate } else { 0.75 }

    if ($lineRate -lt $minLineRate) {
        $failures.Add(("Line coverage {0:P2} is below required threshold {1:P2}." -f $lineRate, $minLineRate))
    }

    if ($branchRate -lt $minBranchRate) {
        $failures.Add(("Branch coverage {0:P2} is below required threshold {1:P2}." -f $branchRate, $minBranchRate))
    }

    $uncoveredLines = $linesTotal - $linesCovered
    $baselineUncoveredLines = $baseline.totalLines - $baseline.lines
    if ($uncoveredLines -gt $baselineUncoveredLines) {
        $failures.Add("Uncovered lines increased: $baselineUncoveredLines -> $uncoveredLines")
    }

    $uncoveredBranches = $branchesTotal - $branchesCovered
    $baselineUncoveredBranches = $baseline.totalBranches - $baseline.branches
    if ($uncoveredBranches -gt $baselineUncoveredBranches) {
        $failures.Add("Uncovered branches increased: $baselineUncoveredBranches -> $uncoveredBranches")
    }

    # Method ratchet (only if baseline contains method counts)
    if ($null -ne $baseline.totalMethods -and [int]$baseline.totalMethods -gt 0) {
        $uncoveredMethodsCount = $methodsTotal - $methodsCovered
        $baselineUncoveredMethods = [int]$baseline.totalMethods - [int]$baseline.methods
        if ($uncoveredMethodsCount -gt $baselineUncoveredMethods) {
            $failures.Add("Uncovered methods increased: $baselineUncoveredMethods -> $uncoveredMethodsCount")
        }
    }

    if ($failures.Count -gt 0) {
        Write-Host ""
        Write-Host "Core Coverage Gate FAILED:" -ForegroundColor Red
        foreach ($f in $failures) {
            Write-Host "  - $f" -ForegroundColor Red
        }
        exit 1
    }

    Write-Host ""
    Write-Host "Core Coverage Gate PASSED (>= 80% lines, >= 75% branches, ratchet satisfied)." -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}

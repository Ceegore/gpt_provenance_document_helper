<#
.SYNOPSIS
    Synthetic regression tests for Test-CoverageRatchet (scripts/CoverageRatchet.ps1),
    covering the exact scenarios that found the covered-only-comparison defect:
    the old gate compared covered counts only, so total (and therefore
    uncovered code) could grow freely without ever tripping it.

.DESCRIPTION
    Run directly: powershell -File scripts/verify_coverage.ratchet.tests.ps1
    Exits non-zero if any scenario's actual result doesn't match its expected
    result, so this can also run as its own CI step if desired.
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "CoverageRatchet.ps1")

$scenarios = @(
    @{
        Name = "1. Same covered, increased total (new untested branches) -> must FAIL"
        Args = @{
            CurrentLinesCovered = 8513; CurrentLinesTotal = 9316
            CurrentBranchesCovered = 2450; CurrentBranchesTotal = 2885   # +10 branches, 0 newly covered
            CurrentMethodsCovered = 463; CurrentMethodsTotal = 463
            BaselineLinesCovered = 8513; BaselineLinesTotal = 9316
            BaselineBranchesCovered = 2450; BaselineBranchesTotal = 2875
            BaselineMethodsCovered = 463; BaselineMethodsTotal = 463
        }
        ExpectFailure = $true
    },
    @{
        Name = "2. Covered increased, but total grew proportionally more (rate drops) -> must FAIL"
        Args = @{
            CurrentLinesCovered = 8513; CurrentLinesTotal = 9316
            CurrentBranchesCovered = 2460; CurrentBranchesTotal = 2900   # +10 covered but +25 total: uncovered 425 -> 440
            CurrentMethodsCovered = 463; CurrentMethodsTotal = 463
            BaselineLinesCovered = 8513; BaselineLinesTotal = 9316
            BaselineBranchesCovered = 2450; BaselineBranchesTotal = 2875
            BaselineMethodsCovered = 463; BaselineMethodsTotal = 463
        }
        ExpectFailure = $true
    },
    @{
        Name = "3. New entirely-uncovered method -> must FAIL"
        Args = @{
            CurrentLinesCovered = 8513; CurrentLinesTotal = 9316
            CurrentBranchesCovered = 2450; CurrentBranchesTotal = 2875
            CurrentMethodsCovered = 463; CurrentMethodsTotal = 464       # new method, uncovered
            BaselineLinesCovered = 8513; BaselineLinesTotal = 9316
            BaselineBranchesCovered = 2450; BaselineBranchesTotal = 2875
            BaselineMethodsCovered = 463; BaselineMethodsTotal = 463
        }
        ExpectFailure = $true
    },
    @{
        Name = "4. Genuinely improved coverage (new covered branch, same total) -> must PASS"
        Args = @{
            CurrentLinesCovered = 8513; CurrentLinesTotal = 9316
            CurrentBranchesCovered = 2451; CurrentBranchesTotal = 2875   # one more branch covered
            CurrentMethodsCovered = 463; CurrentMethodsTotal = 463
            BaselineLinesCovered = 8513; BaselineLinesTotal = 9316
            BaselineBranchesCovered = 2450; BaselineBranchesTotal = 2875
            BaselineMethodsCovered = 463; BaselineMethodsTotal = 463
        }
        ExpectFailure = $false
    },
    @{
        Name = "5. Code deletion that removes only covered code (uncovered count unchanged) -> must PASS"
        Args = @{
            CurrentLinesCovered = 8503; CurrentLinesTotal = 9306         # -10 lines, all previously covered
            CurrentBranchesCovered = 2450; CurrentBranchesTotal = 2875
            CurrentMethodsCovered = 463; CurrentMethodsTotal = 463
            BaselineLinesCovered = 8513; BaselineLinesTotal = 9316
            BaselineBranchesCovered = 2450; BaselineBranchesTotal = 2875
            BaselineMethodsCovered = 463; BaselineMethodsTotal = 463
        }
        ExpectFailure = $false
    },
    @{
        Name = "6. Method coverage regressed from 100% even if raw uncovered count logic alone would pass -> must FAIL"
        Args = @{
            CurrentLinesCovered = 8513; CurrentLinesTotal = 9316
            CurrentBranchesCovered = 2450; CurrentBranchesTotal = 2875
            CurrentMethodsCovered = 462; CurrentMethodsTotal = 463       # one existing method un-covered
            BaselineLinesCovered = 8513; BaselineLinesTotal = 9316
            BaselineBranchesCovered = 2450; BaselineBranchesTotal = 2875
            BaselineMethodsCovered = 463; BaselineMethodsTotal = 463
        }
        ExpectFailure = $true
    }
)

$failedScenarios = 0

foreach ($scenario in $scenarios) {
    $params = $scenario.Args
    $result = Test-CoverageRatchet @params
    $actualFailure = $result.Count -gt 0

    if ($actualFailure -eq $scenario.ExpectFailure) {
        Write-Host "PASS: $($scenario.Name)" -ForegroundColor Green
        if ($actualFailure) {
            $result | ForEach-Object { Write-Host "  - $_" }
        }
    }
    else {
        $failedScenarios++
        Write-Host "FAIL: $($scenario.Name)" -ForegroundColor Red
        Write-Host "  Expected failure = $($scenario.ExpectFailure), actual failure = $actualFailure" -ForegroundColor Red
        $result | ForEach-Object { Write-Host "  - $_" }
    }
}

if ($failedScenarios -gt 0) {
    Write-Host ""
    Write-Host "$failedScenarios scenario(s) did not match expectations." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "All ratchet scenarios behaved as expected." -ForegroundColor Green

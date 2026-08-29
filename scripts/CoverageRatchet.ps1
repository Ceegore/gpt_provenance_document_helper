<#
.SYNOPSIS
    Pure coverage-ratchet comparison, extracted so it can be unit-tested
    directly (see verify_coverage.ratchet.tests.ps1) without needing a real
    Cobertura report or the repo's actual baseline file.

.DESCRIPTION
    Ratchets on UNCOVERED counts, not covered counts. Comparing only
    covered-vs-covered lets total grow freely alongside it: e.g. covered
    stays at 2450 while total grows from 2875 to 2885 (ten new, entirely
    untested branches) - a covered-only check never fires because covered
    never decreased, even though coverage strictly regressed. Uncovered
    count (total - covered) catches exactly that, and needs no
    floating-point rate/percentage comparison at all.

    Deleting code is not specially cased: deleting covered code leaves
    uncovered counts unchanged (passes); deleting uncovered code lowers them
    (passes with more headroom); deleting a mix nets out by the same
    uncovered-count rule as any other change. No change that leaves
    uncovered counts unchanged or lower can fail this gate, regardless of
    why the totals moved.
#>

function Test-CoverageRatchet {
    param(
        [Parameter(Mandatory)] [int]$CurrentLinesCovered,
        [Parameter(Mandatory)] [int]$CurrentLinesTotal,
        [Parameter(Mandatory)] [int]$CurrentBranchesCovered,
        [Parameter(Mandatory)] [int]$CurrentBranchesTotal,
        [Parameter(Mandatory)] [int]$CurrentMethodsCovered,
        [Parameter(Mandatory)] [int]$CurrentMethodsTotal,
        [Parameter(Mandatory)] [int]$BaselineLinesCovered,
        [Parameter(Mandatory)] [int]$BaselineLinesTotal,
        [Parameter(Mandatory)] [int]$BaselineBranchesCovered,
        [Parameter(Mandatory)] [int]$BaselineBranchesTotal,
        [Parameter(Mandatory)] [int]$BaselineMethodsCovered,
        [Parameter(Mandatory)] [int]$BaselineMethodsTotal
    )

    $failures = New-Object System.Collections.Generic.List[string]

    $baselineUncoveredLines    = $BaselineLinesTotal    - $BaselineLinesCovered
    $baselineUncoveredBranches = $BaselineBranchesTotal - $BaselineBranchesCovered
    $baselineUncoveredMethods  = $BaselineMethodsTotal  - $BaselineMethodsCovered

    $currentUncoveredLines    = $CurrentLinesTotal    - $CurrentLinesCovered
    $currentUncoveredBranches = $CurrentBranchesTotal - $CurrentBranchesCovered
    $currentUncoveredMethods  = $CurrentMethodsTotal  - $CurrentMethodsCovered

    if ($currentUncoveredLines -gt $baselineUncoveredLines) {
        $failures.Add("Uncovered lines increased: $baselineUncoveredLines -> $currentUncoveredLines")
    }
    if ($currentUncoveredBranches -gt $baselineUncoveredBranches) {
        $failures.Add("Uncovered branches increased: $baselineUncoveredBranches -> $currentUncoveredBranches")
    }
    if ($currentUncoveredMethods -gt $baselineUncoveredMethods) {
        $failures.Add("Uncovered methods increased: $baselineUncoveredMethods -> $currentUncoveredMethods")
    }

    # Method coverage is 100% as of this baseline. Hold that line explicitly
    # rather than relying solely on the raw count comparison above, so a
    # future baseline update can never quietly reintroduce an uncovered
    # method without a human noticing the drop from 100%.
    if ($baselineUncoveredMethods -eq 0 -and $currentUncoveredMethods -gt 0) {
        $failures.Add("Method coverage regressed from 100%: $CurrentMethodsCovered / $CurrentMethodsTotal")
    }

    return $failures
}

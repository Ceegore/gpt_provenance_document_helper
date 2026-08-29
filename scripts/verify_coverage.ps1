<#
.SYNOPSIS
    Coverage gate with an honest denominator: dynamic production file
    inventory, exact integer counters (no percentage rounding), a ratchet
    against a committed baseline, and enforcement that every
    [ExcludeFromCodeCoverage] site is an enumerated, justified exception.

.DESCRIPTION
    Replaces the old gate's three blind spots:
      1. It checked a hardcoded 13-file list; new files were never noticed.
         This one walks src/AssetProvenanceHelper/**/*.cs and fails if any
         file with executable code is absent from the report.
      2. It never checked method coverage at all. This one does.
      3. It compared rounded percentages against a fixed 90/85 threshold,
         so a single new uncovered branch was one bad day away from
         breaking CI, and a small percentage drop could hide under
         rounding. This one ratchets on exact covered/total integer
         counts and fails on any decrease.

.PARAMETER CoverageDir
    Directory to search (recursively) for coverage.cobertura.xml.

.PARAMETER UpdateBaseline
    Rewrite code-coverage-baseline.json with the current counters. Only use this
    when the numbers have genuinely improved (or the production file
    inventory has legitimately grown) - never to silently accept a
    regression.
#>

param(
    [string]$CoverageDir = "artifacts/coverage",
    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "CoverageRatchet.ps1")
Push-Location $repoRoot

try {
    $srcRoot = Join-Path $repoRoot "src/AssetProvenanceHelper"
    $baselinePath = Join-Path $repoRoot "code-coverage-baseline.json"
    $exclusionsPath = Join-Path $repoRoot "code-coverage-exclusions.json"
    $noExecPath = Join-Path $repoRoot "code-coverage-no-executable-code.json"

    # ---------------------------------------------------------------
    # 1. Locate the freshest coverage.cobertura.xml
    # ---------------------------------------------------------------
    $covFiles = Get-ChildItem -Path $CoverageDir -Filter "coverage.cobertura.xml" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending

    if (-not $covFiles -or $covFiles.Count -eq 0) {
        Write-Error "coverage.cobertura.xml not found under $CoverageDir"
        exit 1
    }

    $covPath = $covFiles[0].FullName
    Write-Host "Using coverage report: $covPath"
    [xml]$covXml = Get-Content $covPath

    $allClasses = $covXml.SelectNodes("//class")
    # Generated build output (e.g. the WinForms ApplicationConfiguration
    # source generator's output under obj/) is not part of the checked-in
    # production denominator.
    $productionClasses = $allClasses | Where-Object {
        $_.filename -notmatch '^obj[\\/]' -and $_.filename -notmatch '[\\/]obj[\\/]'
    }

    # ---------------------------------------------------------------
    # 2. Exact line / branch / method counters (production only)
    # ---------------------------------------------------------------
    $linesTotal = 0
    $linesCovered = 0
    $methodsTotal = 0
    $methodsCovered = 0
    $uncoveredMethods = New-Object System.Collections.Generic.List[string]

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
                $uncoveredMethods.Add("$($cls.filename) :: $($cls.name).$($m.name)$($m.signature)")
            }
        }
    }

    $branchesTotal = [int]$covXml.coverage.'branches-valid'
    $branchesCovered = [int]$covXml.coverage.'branches-covered'

    Write-Host ""
    Write-Host "== Coverage counters (production only, generated code excluded) ==" -ForegroundColor Cyan
    Write-Host ("Lines:    {0} / {1} ({2:P2})" -f $linesCovered, $linesTotal, ($linesCovered / [Math]::Max($linesTotal,1)))
    Write-Host ("Branches: {0} / {1} ({2:P2})" -f $branchesCovered, $branchesTotal, ($branchesCovered / [Math]::Max($branchesTotal,1)))
    Write-Host ("Methods:  {0} / {1} ({2:P2})" -f $methodsCovered, $methodsTotal, ($methodsCovered / [Math]::Max($methodsTotal,1)))

    if ($uncoveredMethods.Count -gt 0) {
        Write-Host ""
        Write-Host "UNCOVERED METHODS ($($uncoveredMethods.Count)):" -ForegroundColor Yellow
        $uncoveredMethods | ForEach-Object { Write-Host "  - $_" }
    }

    # ---------------------------------------------------------------
    # 3. Dynamic production file inventory vs. the report
    # ---------------------------------------------------------------
    $inventory = Get-ChildItem -Path $srcRoot -Recurse -Filter "*.cs" |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName.Substring($srcRoot.Length + 1).Replace('\', '/') } |
        Sort-Object

    $reportedFiles = $productionClasses |
        ForEach-Object { $_.filename.Replace('\', '/') } |
        Select-Object -Unique

    $noExecList = @()
    if (Test-Path $noExecPath) {
        $noExecJson = Get-Content $noExecPath -Raw | ConvertFrom-Json
        $noExecList = @($noExecJson.files)
    }

    $missing = $inventory | Where-Object { $reportedFiles -notcontains $_ }
    $unexpectedlyMissing = $missing | Where-Object { $noExecList -notcontains $_ }
    $staleNoExecEntries = $noExecList | Where-Object { $reportedFiles -contains $_ }

    $gateFailures = New-Object System.Collections.Generic.List[string]

    if ($unexpectedlyMissing.Count -gt 0) {
        foreach ($f in $unexpectedlyMissing) {
            $gateFailures.Add("Production file with no coverage entry and not in code-coverage-no-executable-code.json: $f")
        }
    }

    if ($staleNoExecEntries.Count -gt 0) {
        foreach ($f in $staleNoExecEntries) {
            $gateFailures.Add("File listed in code-coverage-no-executable-code.json now has instrumented code and must be removed from that list: $f")
        }
    }

    # ---------------------------------------------------------------
    # 4. Every [ExcludeFromCodeCoverage] site must be an enumerated,
    #    justified exception.
    # ---------------------------------------------------------------
    $allowedExclusions = @()
    if (Test-Path $exclusionsPath) {
        $exclusionsJson = Get-Content $exclusionsPath -Raw | ConvertFrom-Json
        $allowedExclusions = @($exclusionsJson.allowed)
    }

    $foundExclusions = New-Object System.Collections.Generic.List[string]
    $sourceFiles = Get-ChildItem -Path $srcRoot -Recurse -Filter "*.cs" |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

    foreach ($file in $sourceFiles) {
        $lines = Get-Content $file.FullName
        $ns = $null
        $className = $null

        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]

            if ($line -match '^\s*namespace\s+([\w\.]+)\s*;') {
                $ns = $Matches[1]
                continue
            }

            if ($line -match '(?:partial\s+)?(?:sealed\s+)?class\s+(\w+)') {
                $className = $Matches[1]
                continue
            }

            if ($line -match 'ExcludeFromCodeCoverage') {
                # Scan forward for the member signature this attribute decorates.
                for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                    $memberLine = $lines[$j]
                    if ($memberLine -match '^\s*\[') { continue }  # another attribute
                    if ($memberLine -match '\bclass\s+') { break }

                    # Expression-bodied or auto-property: "... Name => ..." / "... Name { get" with no '(' before it.
                    if ($memberLine -match '^\s*(?:public|private|internal|protected)[^(]*\s(\w+)\s*(?:=>|\{)') {
                        $memberName = "get_$($Matches[1])"
                        $foundExclusions.Add("$ns.$className.$memberName")
                        break
                    }

                    # Method: the identifier immediately before the first '('.
                    if ($memberLine -match '^\s*(?:public|private|internal|protected)[^(]*?(\w+)\s*\(') {
                        $memberName = $Matches[1]
                        $foundExclusions.Add("$ns.$className.$memberName")
                        break
                    }
                }
            }
        }
    }

    $unlisted = $foundExclusions | Where-Object { $allowedExclusions -notcontains $_ } | Select-Object -Unique
    if ($unlisted.Count -gt 0) {
        foreach ($u in $unlisted) {
            $gateFailures.Add("[ExcludeFromCodeCoverage] site not listed in code-coverage-exclusions.json: $u")
        }
    }

    $staleAllowed = $allowedExclusions | Where-Object { $foundExclusions -notcontains $_ }
    if ($staleAllowed.Count -gt 0) {
        foreach ($s in $staleAllowed) {
            $gateFailures.Add("code-coverage-exclusions.json lists '$s' but no matching [ExcludeFromCodeCoverage] site was found - remove the stale entry")
        }
    }

    # ---------------------------------------------------------------
    # 5. Ratchet against the committed baseline
    # ---------------------------------------------------------------
    $current = [ordered]@{
        lines            = $linesCovered
        totalLines       = $linesTotal
        branches         = $branchesCovered
        totalBranches    = $branchesTotal
        methods          = $methodsCovered
        totalMethods     = $methodsTotal
    }

    if ($UpdateBaseline) {
        $current | ConvertTo-Json | Set-Content $baselinePath
        Write-Host ""
        Write-Host "Baseline updated: $baselinePath" -ForegroundColor Green
    }
    elseif (Test-Path $baselinePath) {
        $baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json

        $ratchetFailures = Test-CoverageRatchet `
            -CurrentLinesCovered $linesCovered -CurrentLinesTotal $linesTotal `
            -CurrentBranchesCovered $branchesCovered -CurrentBranchesTotal $branchesTotal `
            -CurrentMethodsCovered $methodsCovered -CurrentMethodsTotal $methodsTotal `
            -BaselineLinesCovered $baseline.lines -BaselineLinesTotal $baseline.totalLines `
            -BaselineBranchesCovered $baseline.branches -BaselineBranchesTotal $baseline.totalBranches `
            -BaselineMethodsCovered $baseline.methods -BaselineMethodsTotal $baseline.totalMethods

        foreach ($failure in $ratchetFailures) {
            $gateFailures.Add($failure)
        }

        Write-Host ""
        Write-Host "== Ratchet vs. baseline ($baselinePath) - uncovered counts must not increase ==" -ForegroundColor Cyan
        Write-Host ("Lines:    {0} -> {1} uncovered" -f ($baseline.totalLines - $baseline.lines), ($linesTotal - $linesCovered))
        Write-Host ("Branches: {0} -> {1} uncovered" -f ($baseline.totalBranches - $baseline.branches), ($branchesTotal - $branchesCovered))
        Write-Host ("Methods:  {0} -> {1} uncovered" -f ($baseline.totalMethods - $baseline.methods), ($methodsTotal - $methodsCovered))
    }
    else {
        Write-Host ""
        Write-Host "No code-coverage-baseline.json found - run with -UpdateBaseline to create one." -ForegroundColor Yellow
    }

    # ---------------------------------------------------------------
    # 6. Per-file table, ranked by branch-rate ascending (worst first)
    # ---------------------------------------------------------------
    Write-Host ""
    Write-Host "== Per-file coverage (ascending branch-rate) ==" -ForegroundColor Cyan
    $productionClasses |
        Sort-Object { [double]$_.'branch-rate' } |
        Select-Object -Unique filename, name, @{n='line-rate';e={[double]$_.'line-rate'}}, @{n='branch-rate';e={[double]$_.'branch-rate'}} |
        Format-Table -AutoSize |
        Out-String |
        Write-Host

    # ---------------------------------------------------------------
    # Verdict
    # ---------------------------------------------------------------
    if ($gateFailures.Count -gt 0) {
        Write-Host ""
        Write-Host "COVERAGE GATE FAILED:" -ForegroundColor Red
        $gateFailures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        exit 1
    }

    Write-Host ""
    Write-Host "Coverage gate passed." -ForegroundColor Green
}
finally {
    Pop-Location
}

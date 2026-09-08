<#
.SYNOPSIS
    Fails when the release assets documented in AGENTS.md disagree with the ones
    .github/workflows/release.yml actually produces.

.DESCRIPTION
    AGENTS.md advertised a self-contained "-win-x64.zip" and a
    "-framework-dependent.zip" for several releases after the pipeline had been
    reduced to a single apphost-free archive. Nothing caught it, because no test
    reads that table.

    This compares SETS, not prose: the archive names the workflow builds versus
    the backticked asset names in the AGENTS.md release table. An earlier
    version of this script grepped whole files for the stale names and silently
    passed, because release.yml mentions "-win-x64.zip" in a comment and the
    AGENTS.md correction note names it too.
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $repoRoot '.github/workflows/release.yml'
$agentsPath = Join-Path $repoRoot 'AGENTS.md'

$workflowLines = Get-Content $workflowPath
$agentsLines = Get-Content $agentsPath

# What the workflow actually packages: the archive it zips, plus the checksum
# file it writes. Comments are excluded so prose can never satisfy the check.
$produced = [System.Collections.Generic.HashSet[string]]::new()
foreach ($line in $workflowLines) {
    if ($line.TrimStart().StartsWith('#')) { continue }
    foreach ($m in [regex]::Matches($line, '([A-Za-z0-9_.$<>{}-]+\.(?:zip|txt))')) {
        $name = $m.Groups[1].Value -replace '\$version|\$\{\{[^}]*\}\}', '<ver>'
        if ($name -match '^(AssetProvenanceHelper|SHA256SUMS)') { [void]$produced.Add($name) }
    }
}

# What AGENTS.md claims, taken only from table rows so surrounding explanation
# is ignored.
$documented = [System.Collections.Generic.HashSet[string]]::new()
foreach ($line in $agentsLines) {
    if (-not $line.StartsWith('|')) { continue }
    foreach ($m in [regex]::Matches($line, '`([A-Za-z0-9_.<>-]+\.(?:zip|txt))`')) {
        [void]$documented.Add($m.Groups[1].Value)
    }
}

if ($produced.Count -eq 0) { throw "Could not determine any release asset from $workflowPath." }
if ($documented.Count -eq 0) { throw "Could not find a release asset table in $agentsPath." }

$missing = @($documented | Where-Object { -not $produced.Contains($_) })
$undocumented = @($produced | Where-Object { -not $documented.Contains($_) })

if ($missing.Count -gt 0 -or $undocumented.Count -gt 0) {
    Write-Host ("workflow produces : " + (($produced | Sort-Object) -join ', '))
    Write-Host ("AGENTS.md documents: " + (($documented | Sort-Object) -join ', '))
    foreach ($m in $missing) { Write-Host "FAIL: AGENTS.md documents '$m', which the release workflow does not produce." }
    foreach ($u in $undocumented) { Write-Host "FAIL: the release workflow produces '$u', which AGENTS.md does not document." }
    throw 'Release asset documentation does not match the release workflow.'
}

# Deliberately no static check that -p:UseAppHost=false is still passed. Such a
# check cannot fail honestly here: the workflow's own guard mentions the flag in
# its error message, so any grep for it matches even once the flag is gone. The
# workflow already enforces this properly at runtime by counting .exe files in
# the prepared package and throwing if there are any, which is the real gate.

Write-Host ("Release asset documentation matches release.yml: " + (($produced | Sort-Object) -join ', '))

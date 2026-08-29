<#
.SYNOPSIS
    Runs the same checks CI runs, from a clean tree, so a "green" local result
    means what it claims to mean.

.DESCRIPTION
    A warm/incremental build or a working tree with local-only line-ending
    normalization can pass locally while a fresh CI checkout fails (this has
    happened twice: once via a warm-build CS8625 masking, once via CRLF vs LF
    raw string literals). This script refuses to run against a dirty tree and
    always builds --no-incremental, so its result is directly comparable to CI.
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    Write-Host '== SHA / tree ==' -ForegroundColor Cyan
    git rev-parse HEAD
    $dirty = git status --porcelain=v1
    if ($dirty) {
        Write-Host $dirty
        throw 'Working tree is dirty. CI tests a clean checkout - commit or stash before running this.'
    }
    Write-Host 'Tree is clean.'

    Write-Host "`n== dotnet --info ==" -ForegroundColor Cyan
    dotnet --version

    Write-Host "`n== clean build (Debug, -warnaserror) ==" -ForegroundColor Cyan
    dotnet clean AssetProvenanceHelper.sln -c Debug | Out-Null
    dotnet build AssetProvenanceHelper.sln -c Debug --no-incremental -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }

    Write-Host "`n== clean build (Release, -warnaserror) ==" -ForegroundColor Cyan
    dotnet clean AssetProvenanceHelper.sln -c Release | Out-Null
    dotnet build AssetProvenanceHelper.sln -c Release --no-incremental -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    Write-Host "`n== Debug tests ==" -ForegroundColor Cyan
    dotnet test AssetProvenanceHelper.sln -c Debug --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Debug tests failed.' }

    Write-Host "`n== Release tests ==" -ForegroundColor Cyan
    dotnet test AssetProvenanceHelper.sln -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }

    Write-Host "`n== RecoveryCritical ==" -ForegroundColor Cyan
    dotnet test AssetProvenanceHelper.sln -c Release --no-build --filter "Category=RecoveryCritical"
    if ($LASTEXITCODE -ne 0) { throw 'RecoveryCritical tests failed.' }

    Write-Host "`nALL CHECKS PASSED (clean tree, clean build, -warnaserror, Debug+Release tests, RecoveryCritical)." -ForegroundColor Green
}
finally {
    Pop-Location
}

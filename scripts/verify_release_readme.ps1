[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PublishDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish directory does not exist: $PublishDir"
}

$readmePath = Join-Path $PublishDir 'README.txt'
$launcherPath = Join-Path $PublishDir 'Start AI Asset Provenance Helper.cmd'

if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
    throw "Release package is missing README.txt: $readmePath"
}

if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "Release package is missing its launcher: $launcherPath"
}

$readme = Get-Content -LiteralPath $readmePath -Raw
$requiredText = @(
    'INSTALL',
    '.NET 10 Desktop Runtime (x64)',
    'dotnet "C:\Tools\AssetProvenanceHelper\AssetProvenanceHelper.dll"',
    'Get-ChildItem "C:\Tools\AssetProvenanceHelper" -Recurse | Unblock-File',
    'Start AI Asset Provenance Helper.cmd',
    'WHY SO COMPLICATED?',
    'Smart App Control',
    'is not code-signed',
    'contains no .exe',
    'Microsoft''s signed dotnet host',
    'The application is identical either way.'
)

$missing = @($requiredText | Where-Object { -not $readme.Contains([string]$_) })
if ($missing.Count -gt 0) {
    throw "Release README.txt is missing mandatory Smart App Control installation guidance: $($missing -join '; ')"
}

Write-Host "Release README installation and Smart App Control guidance verified: $readmePath"

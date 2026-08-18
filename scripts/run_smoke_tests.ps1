# Smoke test harness for published win-x64 deployment
param(
    [string]$PublishDir = "artifacts/publish",
    [string]$LogOutputDir = "artifacts",
    [bool]$CreateArchive = $true
)

$ErrorActionPreference = "Stop"

Write-Host "=== Starting win-x64 Published Deployment Smoke Test ==="

$exePath = Join-Path $PublishDir "AssetProvenanceHelper.exe"
if (-not (Test-Path $exePath)) {
    throw "Published executable not found at: $exePath"
}

$exeHash = (Get-FileHash $exePath -Algorithm SHA256).Hash
Write-Host "Executable SHA-256: $exeHash"

# 1. Verify templates directory and contents
$templateDir = Join-Path $PublishDir "templates"
$refTemplate = Join-Path $templateDir "reference.md"
$finalTemplate = Join-Path $templateDir "final.md"

if (-not (Test-Path $refTemplate)) {
    throw "Reference template missing at: $refTemplate"
}
if (-not (Test-Path $finalTemplate)) {
    throw "Final template missing at: $finalTemplate"
}
Write-Host "Templates verified: reference.md, final.md present."

# 2. Verify core runtime dependencies
$coreAssemblies = @(
    "AssetProvenanceHelper.dll",
    "System.Windows.Forms.dll",
    "System.Text.Json.dll",
    "System.Security.Cryptography.dll"
)
foreach ($dll in $coreAssemblies) {
    $dllPath = Join-Path $PublishDir $dll
    if (-not (Test-Path $dllPath)) {
        throw "Core runtime assembly missing: $dllPath"
    }
}
Write-Host "Core runtime assemblies verified in publish directory."

# 3. Real Process Startup, Window Title & Graceful Shutdown Smoke Test
Write-Host "Testing process startup and window creation of $exePath ..."
$proc = Start-Process -FilePath $exePath -PassThru

# Allow a cold self-contained Windows launch to create its main window.
$timeoutMs = 15000
$elapsedMs = 0
$windowTitle = ""
$hasWindow = $false

while ($elapsedMs -lt $timeoutMs) {
    Start-Sleep -Milliseconds 250
    $elapsedMs += 250
    $proc.Refresh()
    if ($proc.HasExited) {
        break
    }
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
        $windowTitle = $proc.MainWindowTitle
        $hasWindow = $true
        break
    }
}

if ($proc.HasExited) {
    throw "Process exited prematurely with exit code: $($proc.ExitCode)"
}

# Assert: main window must have been created
if (-not $hasWindow) {
    # Clean up the process before failing
    try { $proc.Kill() } catch { }
    throw "Main window was not created within $timeoutMs ms timeout."
}

# Assert: window title must match expected value
$expectedTitle = "AI Asset Provenance Helper"
if ($windowTitle -ne $expectedTitle) {
    try { $proc.Kill() } catch { }
    throw "Unexpected main window title: expected '$expectedTitle', got '$windowTitle'"
}

Write-Host "Process running (PID: $($proc.Id)), Main Window Title: '$windowTitle'"

$gracefulShutdown = $false
# Attempt graceful shutdown
$proc.CloseMainWindow() | Out-Null
if ($proc.WaitForExit(3000)) {
    $gracefulShutdown = $true
    Write-Host "Process cleanly exited via CloseMainWindow."
} else {
    Write-Host "Process did not exit cleanly within 3s, terminating via Kill()..."
    $proc.Kill()
    $proc.WaitForExit(2000)
}

# Assert: graceful shutdown is mandatory
if (-not $gracefulShutdown) {
    throw "Application required forced termination instead of clean shutdown."
}

# 4. Release Archive Creation & Verification
$archivePath = $null
$archiveHash = $null
if ($CreateArchive) {
    $archiveName = "AssetProvenanceHelper-v1.0.0-win-x64.zip"
    $archivePath = Join-Path $LogOutputDir $archiveName
    Write-Host "Compressing publish directory to $archivePath ..."
    Compress-Archive -Path "$PublishDir/*" -DestinationPath $archivePath -Force
    $archiveHash = (Get-FileHash $archivePath -Algorithm SHA256).Hash
    Write-Host "Archive created. SHA-256: $archiveHash"
}

# Resolve commit and actions metadata
$commitSha = $env:GITHUB_SHA
if (-not $commitSha) {
    try {
        $commitSha = (git rev-parse HEAD).Trim()
    } catch {
        $commitSha = "LOCAL_BUILD"
    }
}
$actionsRunId = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { "LOCAL_RUN" }

$smokeResults = [ordered]@{
    Timestamp = (Get-Date).ToString("o")
    CommitSha = $commitSha
    ActionsRunId = $actionsRunId
    ArtifactName = "ci-artifacts"
    PublishDir = (Resolve-Path $PublishDir).Path
    ExecutablePath = (Resolve-Path $exePath).Path
    ExecutableSha256 = $exeHash
    ReleaseArchive = if ($archivePath) { (Resolve-Path $archivePath).Path } else { $null }
    ReleaseArchiveSha256 = $archiveHash
    WindowsVersion = [System.Environment]::OSVersion.VersionString
    DotNetRuntime = "8.0 (net8.0-windows self-contained)"
    TemplatesVerified = $true
    CoreAssembliesVerified = $true
    ProcessStartupVerified = $true
    MainWindowCreated = $hasWindow
    MainWindowTitle = $windowTitle
    GracefulShutdownVerified = $gracefulShutdown
    Status = "PASS"
}

if (-not (Test-Path $LogOutputDir)) {
    New-Item -ItemType Directory -Path $LogOutputDir -Force | Out-Null
}

$logPath = Join-Path $LogOutputDir "smoke-test-log.json"
$smokeResults | ConvertTo-Json -Depth 4 | Set-Content $logPath -Encoding utf8
Write-Host "Smoke test harness completed successfully. Log written to: $logPath"

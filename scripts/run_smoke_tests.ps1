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
$finalNoRefTemplate = Join-Path $templateDir "final_no_reference.md"

if (-not (Test-Path $refTemplate)) {
    throw "Reference template missing at: $refTemplate"
}
if (-not (Test-Path $finalTemplate)) {
    throw "Final template missing at: $finalTemplate"
}
if (-not (Test-Path $finalNoRefTemplate)) {
    throw "Final no-reference template missing at: $finalNoRefTemplate"
}
Write-Host "Templates verified: reference.md, final.md, final_no_reference.md present."

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
$absExePath = (Resolve-Path $exePath).Path
$workDir = Split-Path -Parent $absExePath
Write-Host "Testing process startup and window creation of $absExePath ..."
$proc = Start-Process -FilePath $absExePath -WorkingDirectory $workDir -PassThru

# Wait up to 15 seconds for main window to appear
$timeoutMs = 15000
$expectedTitle = "AI Asset Provenance Helper"

$win32Helper = @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class SmokeWin32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static List<string> GetProcessWindows(uint targetPid) {
        var titles = new List<string>();
        EnumWindows((hWnd, lParam) => {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == targetPid) {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                string t = sb.ToString();
                if (!string.IsNullOrEmpty(t)) {
                    titles.Add(t);
                }
            }
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    public static bool CloseWindowByTitle(uint targetPid, string expectedTitle) {
        bool closed = false;
        EnumWindows((hWnd, lParam) => {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == targetPid) {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                if (sb.ToString() == expectedTitle) {
                    PostMessage(hWnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                    closed = true;
                }
            }
            return true;
        }, IntPtr.Zero);
        return closed;
    }
}
"@
Add-Type -TypeDefinition $win32Helper -ErrorAction SilentlyContinue

while ($elapsedMs -lt $timeoutMs) {
    Start-Sleep -Milliseconds 250
    $elapsedMs += 250
    try {
        $p = [System.Diagnostics.Process]::GetProcessById($proc.Id)
        $p.Refresh()
        if ($p.HasExited) {
            break
        }
        if (-not [string]::IsNullOrEmpty($p.MainWindowTitle)) {
            $windowTitle = $p.MainWindowTitle
            $hasWindow = $true
            $proc = $p
            break
        }
        $titles = [SmokeWin32]::GetProcessWindows([uint32]$proc.Id)
        if ($titles -contains $expectedTitle) {
            $windowTitle = $expectedTitle
            $hasWindow = $true
            $proc = $p
            break
        }
    } catch {
        # Process handle lookup retry
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
if ($windowTitle -ne $expectedTitle) {
    try { $proc.Kill() } catch { }
    throw "Unexpected main window title: expected '$expectedTitle', got '$windowTitle'"
}

Write-Host "Process running (PID: $($proc.Id)), Main Window Title: '$windowTitle'"

# Verify application icon extraction / presence
$iconVerified = $false
try {
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($absExePath)
    if ($icon -ne $null -and $icon.Width -gt 0) {
        $iconVerified = $true
        Write-Host "Application icon extracted and verified: $($icon.Width)x$($icon.Height)"
        $icon.Dispose()
    }
} catch {
    Write-Warning "Could not verify application icon: $_"
}

if (-not $iconVerified) {
    throw "Application icon could not be extracted from published executable."
}

$gracefulShutdown = $false
# Attempt graceful shutdown
[SmokeWin32]::CloseWindowByTitle([uint32]$proc.Id, $expectedTitle) | Out-Null
$proc.CloseMainWindow() | Out-Null
if ($proc.WaitForExit(5000)) {
    $gracefulShutdown = $true
    Write-Host "Process cleanly exited via CloseMainWindow."
} else {
    Write-Host "Process did not exit cleanly within 5s, terminating via Kill()..."
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
$productVersion = (Get-Item $exePath).VersionInfo.ProductVersion
if (-not $productVersion) {
    throw "Could not determine product version from executable."
}
$productVersion = $productVersion.Split('+')[0]
Write-Host "Executable ProductVersion: $productVersion"

if ($CreateArchive) {
    $archiveName = "AssetProvenanceHelper-v$($productVersion)-win-x64.zip"
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
    StartupElapsedMs = $elapsedMs
    MainWindowCreated = $hasWindow
    MainWindowTitle = $windowTitle
    IconVerified = $iconVerified
    GracefulShutdownVerified = $gracefulShutdown
    Status = "PASS"
}

if (-not (Test-Path $LogOutputDir)) {
    New-Item -ItemType Directory -Path $LogOutputDir -Force | Out-Null
}

$logPath = Join-Path $LogOutputDir "smoke-test-log.json"
$smokeResults | ConvertTo-Json -Depth 4 | Set-Content $logPath -Encoding utf8
Write-Host "Smoke test harness completed successfully. Log written to: $logPath"

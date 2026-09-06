# SAC-safe smoke test: launches the app through the SIGNED `dotnet` host
# (dotnet AssetProvenanceHelper.dll) instead of the self-contained, unsigned
# native apphost exe. This avoids Windows Smart App Control (SAC) blocking an
# unsigned executable on a dev machine, while still exercising real process
# startup, main-window creation, and graceful shutdown.
#
# Point -AppDir at any framework-dependent output that contains
# AssetProvenanceHelper.dll + AssetProvenanceHelper.runtimeconfig.json plus the
# templates/, provider_templates/, examples/ content directories. After
# `dotnet build -c Release`, the default below already satisfies that.
#
# See AGENTS.md for why this exists. For the self-contained release/CI smoke
# test, use scripts/run_smoke_tests.ps1 instead.

param(
    [string]$AppDir = "src/AssetProvenanceHelper/bin/Release/net10.0-windows",
    [string]$LogOutputDir = "artifacts"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$elapsedMs = 0
$windowTitle = ""
$hasWindow = $false

Write-Host "=== Starting SAC-safe (dotnet host) Deployment Smoke Test ==="

# --- Locate the signed dotnet host and the managed entry DLL ---------------
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $dotnet) {
    throw "The 'dotnet' host was not found on PATH. Install the .NET 10 SDK/runtime."
}
$dotnetPath = $dotnet.Source
Write-Host "Signed host: $dotnetPath"

if (-not (Test-Path -LiteralPath $AppDir)) {
    throw "App directory not found: $AppDir. Run 'dotnet build -c Release' first, or pass -AppDir."
}

$dllPath = Join-Path $AppDir "AssetProvenanceHelper.dll"
if (-not (Test-Path -LiteralPath $dllPath)) {
    throw "Managed entry assembly not found at: $dllPath"
}
$runtimeConfig = Join-Path $AppDir "AssetProvenanceHelper.runtimeconfig.json"
if (-not (Test-Path -LiteralPath $runtimeConfig)) {
    throw "runtimeconfig.json not found at: $runtimeConfig (is this a framework-dependent build output?)"
}

# --- Invariant: no mutable runtime state must ship alongside the app -------
$mutableStateFileNames = @(
    "settings.json",
    "session.json",
    "reference-replacement.json",
    "recent-documents.json",
    "request-progress.json"
)
$unexpectedMutableStateFiles = @(
    $mutableStateFileNames |
        Where-Object { Test-Path -LiteralPath (Join-Path $AppDir $_) }
)
if ($unexpectedMutableStateFiles.Count -gt 0) {
    throw "App directory contains mutable runtime state that must not ship: $($unexpectedMutableStateFiles -join ', ')"
}
Write-Host "App directory contains no mutable runtime state."

$dllHash = (Get-FileHash $dllPath -Algorithm SHA256).Hash
Write-Host "Entry assembly SHA-256: $dllHash"

# --- 1. Verify content directories -----------------------------------------
$templateDir = Join-Path $AppDir "templates"
$refTemplate = Join-Path $templateDir "reference.md"
$finalTemplate = Join-Path $templateDir "final.md"
$finalNoRefTemplate = Join-Path $templateDir "final_no_reference.md"
if (-not (Test-Path $refTemplate)) { throw "Reference template missing at: $refTemplate" }
if (-not (Test-Path $finalTemplate)) { throw "Final template missing at: $finalTemplate" }
if (-not (Test-Path $finalNoRefTemplate)) { throw "Final no-reference template missing at: $finalNoRefTemplate" }
Write-Host "Templates verified: reference.md, final.md, final_no_reference.md present."

$providerTemplateDir = Join-Path $AppDir "provider_templates"
$chatGptTemplate = Join-Path $providerTemplateDir "ChatGPT.md"
$providerTemplateExample = Join-Path $providerTemplateDir "_TEMPLATE.md"
if (-not (Test-Path $chatGptTemplate)) { throw "ChatGPT provider template missing at: $chatGptTemplate" }
if (-not (Test-Path $providerTemplateExample)) { throw "Provider template example missing at: $providerTemplateExample" }

$examplesDir = Join-Path $AppDir "examples"
$requestManifestTemplate = Join-Path $examplesDir "asset_request_manifest_template.json"
$requestConversionPrompt = Join-Path $examplesDir "asset_request_conversion_prompt.txt"
$pixelExactManifestTemplate = Join-Path $examplesDir "pixel_exact_manifest_template.json"
if (-not (Test-Path $requestManifestTemplate)) { throw "Request Manifest template missing at: $requestManifestTemplate" }
if (-not (Test-Path $requestConversionPrompt)) { throw "Request conversion prompt missing at: $requestConversionPrompt" }
if (-not (Test-Path $pixelExactManifestTemplate)) { throw "Pixel-Exact manifest template missing at: $pixelExactManifestTemplate" }
Write-Host "Provider/request/Pixel-Exact support files verified."

# --- 2. Real process startup, window title & graceful shutdown -------------
# NOTE: launched via the signed dotnet host, so the process Windows evaluates is
# dotnet.exe (SAC-trusted). The WinForms main window belongs to that same PID.
$absDllPath = (Resolve-Path $dllPath).Path
$workDir = (Resolve-Path $AppDir).Path
Write-Host "Launching via signed host: dotnet `"$absDllPath`" ..."
$proc = Start-Process -FilePath $dotnetPath -ArgumentList $absDllPath -WorkingDirectory $workDir -PassThru

$timeoutMs = 20000
$expectedTitle = "AI Asset Provenance Helper"

$win32Helper = @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class SmokeSafeWin32 {
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
        # Only the WinForms window counts. Launching via dotnet.exe also creates
        # the host's own console window, whose title is the dotnet.exe path, so we
        # must match the exact expected title rather than accept any MainWindowTitle.
        if ($p.MainWindowTitle -eq $expectedTitle) {
            $windowTitle = $p.MainWindowTitle
            $hasWindow = $true
            $proc = $p
            break
        }
        $titles = [SmokeSafeWin32]::GetProcessWindows([uint32]$proc.Id)
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
    throw "Process exited prematurely with exit code: $($proc.ExitCode). " +
        "If this machine has SAC on, confirm you launched via the dotnet host (this script) " +
        "and not the self-contained apphost exe."
}

if (-not $hasWindow) {
    try { $proc.Kill() } catch { }
    throw "Main window was not created within $timeoutMs ms timeout."
}

if ($windowTitle -ne $expectedTitle) {
    try { $proc.Kill() } catch { }
    throw "Unexpected main window title: expected '$expectedTitle', got '$windowTitle'"
}

Write-Host "Process running (PID: $($proc.Id)), Main Window Title: '$windowTitle'"

# --- 3. Best-effort icon verification --------------------------------------
# The icon is embedded in the apphost exe (if present in the dir). Reading a
# file's icon does not execute it, so this is SAC-safe. Non-fatal.
$iconVerified = $false
$appHostExe = Join-Path $AppDir "AssetProvenanceHelper.exe"
if (Test-Path -LiteralPath $appHostExe) {
    try {
        Add-Type -AssemblyName System.Drawing
        $icon = [System.Drawing.Icon]::ExtractAssociatedIcon((Resolve-Path $appHostExe).Path)
        if ($icon -ne $null -and $icon.Width -gt 0) {
            $iconVerified = $true
            Write-Host "Application icon extracted and verified: $($icon.Width)x$($icon.Height)"
            $icon.Dispose()
        }
    } catch {
        Write-Warning "Could not verify application icon (non-fatal): $_"
    }
} else {
    Write-Host "No apphost exe present in $AppDir; skipping icon verification (non-fatal)."
}

# --- 4. Graceful shutdown ---------------------------------------------------
$gracefulShutdown = $false
# Post WM_CLOSE to the WinForms window specifically (not the dotnet console
# window) so the app runs its normal shutdown path.
$closedForm = [SmokeSafeWin32]::CloseWindowByTitle([uint32]$proc.Id, $expectedTitle)
if (-not $closedForm) {
    Write-Warning "Could not locate the main window to post WM_CLOSE; will fall back to Kill()."
}
if ($proc.WaitForExit(5000)) {
    $gracefulShutdown = $true
    Write-Host "Process cleanly exited after closing the main window."
} else {
    Write-Host "Process did not exit cleanly within 5s, terminating via Kill()..."
    $proc.Kill()
    $proc.WaitForExit(2000)
}

if (-not $gracefulShutdown) {
    throw "Application required forced termination instead of clean shutdown."
}

# --- 5. Log ----------------------------------------------------------------
$productInformationalVersion = (Get-Item $dllPath).VersionInfo.ProductVersion
$commitSha = $env:GITHUB_SHA
if (-not $commitSha) {
    try { $commitSha = (git rev-parse HEAD).Trim() } catch { $commitSha = "LOCAL_BUILD" }
}

$smokeResults = [ordered]@{
    Timestamp = (Get-Date).ToString("o")
    Mode = "sac-safe-dotnet-host"
    DotnetHost = $dotnetPath
    CommitSha = $commitSha
    AppDir = $workDir
    EntryAssembly = $absDllPath
    EntryAssemblySha256 = $dllHash
    WindowsVersion = [System.Environment]::OSVersion.VersionString
    ProductVersion = $productInformationalVersion
    TemplatesVerified = $true
    ContentFilesVerified = $true
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
$logPath = Join-Path $LogOutputDir "smoke-test-sac-safe-log.json"
$smokeResults | ConvertTo-Json -Depth 4 | Set-Content $logPath -Encoding utf8
Write-Host "SAC-safe smoke test completed successfully. Log written to: $logPath"

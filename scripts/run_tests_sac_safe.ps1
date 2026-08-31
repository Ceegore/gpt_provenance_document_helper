<#
.SYNOPSIS
    Runs the test suite in a way that survives Windows Smart App Control /
    Application Control (0x800711C7) blocking freshly built assemblies, and
    that never reports an environment block as if it were a test failure.

.DESCRIPTION
    THE PROBLEM THIS SOLVES
    -----------------------
    On a SAC-enabled machine, `dotnet test` intermittently fails with:

        Could not load file or assembly '...\AssetProvenanceHelper.dll'.
        An application control policy has blocked this file. (0x800711C7)

    The blocked file is ALWAYS the product assembly (AssetProvenanceHelper.dll)
    - never the test assembly, never a NuGet dependency. See
    docs/sac-test-execution.md for the evidence.

    The failure mode is what actually costs time: vstest surfaces the block as
    hundreds of ordinary-looking test failures plus a "test host process
    crashed" message. That is indistinguishable from a real regression, so an
    agent or developer chases a phantom bug. This script makes the distinction
    explicit and machine-checkable.

    THE TRIGGER IS RATE-BASED, NOT CONTENT-BASED
    --------------------------------------------
    A freshly built assembly is not blocked because of what it contains. It is
    blocked while its reputation is unresolved, and the block is provoked by
    running MANY test-host launches back-to-back against repeatedly rebuilt
    binaries. Single runs, and filtered runs, are reliable.

    Accordingly this script:
      1. lets a fresh build "settle" before the first test-host launch,
      2. runs one cheap canary test first instead of paying for the full suite,
      3. detects a block via the locale-independent 0x800711C7 code AND via the
         Windows CodeIntegrity event log,
      4. backs off and retries instead of failing,
      5. exits with a DISTINCT code so a block can never be mistaken for a
         real test failure.

    OUTPUT AND HANG SAFETY
    ----------------------
    `dotnet test` block-buffers its stdout whenever stdout is not a console
    (e.g. when piped through Tee-Object), which makes a long run look frozen.
    This script therefore runs dotnet via Start-Process with redirected output
    and tails those files itself, so progress is visible as it happens. Every
    run is also bounded by a hard timeout, so the script can never sit silent
    forever - if a run exceeds its budget it is killed and reported as a
    timeout.

.PARAMETER Configuration
    Debug or Release. Default: Release.

.PARAMETER Filter
    Optional VSTest --filter expression. Targeted filtered runs are the
    recommended default during development; they are fast and do not provoke
    the block.

.PARAMETER SettleSeconds
    Seconds to wait before the first test-host launch, letting a just-built
    assembly resolve. Default 20. Use -SettleSeconds 0 when nothing was rebuilt.

.PARAMETER MaxAttempts
    How many times to retry after an environment block. Default 3.

.PARAMETER BackoffSeconds
    Base wait after a detected block; grows linearly per attempt. Default 30.

.PARAMETER CanaryTimeoutSeconds
    Hard timeout for the cheap canary run. Default 240.

.PARAMETER SuiteTimeoutSeconds
    Hard timeout for the real run. Default 1200 (20 min).

.PARAMETER SkipCanary
    Skip the cheap canary probe and go straight to the real run.

.EXAMPLE
    powershell -File scripts/run_tests_sac_safe.ps1
    powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~FeatureV14"
    powershell -File scripts/run_tests_sac_safe.ps1 -SettleSeconds 0

.NOTES
    EXIT CODES
      0   tests ran and passed
      1   tests ran and genuinely FAILED  (a real result - investigate the code)
      42  environment block (SAC/0x800711C7) persisted after all retries.
          NOT a product defect. Nothing was proven about the code.
      43  a run exceeded its timeout and was killed, or its exit code could
          not be read. Inconclusive.
      44  the --filter matched no tests. Usage problem, not a failure.
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Filter,

    [ValidateRange(0, 600)]
    [int]$SettleSeconds = 20,

    [ValidateRange(1, 10)]
    [int]$MaxAttempts = 3,

    [ValidateRange(1, 600)]
    [int]$BackoffSeconds = 30,

    [ValidateRange(30, 3600)]
    [int]$CanaryTimeoutSeconds = 240,

    [ValidateRange(30, 7200)]
    [int]$SuiteTimeoutSeconds = 1200,

    [switch]$SkipCanary
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

# Locale-independent signature. The message text is localized (German boxes say
# "Anwendungssteuerungsrichtlinie"), but the HRESULT never is.
$BlockCode = '0x800711C7'
$ExitEnvironmentBlock = 42
$ExitTimeout = 43
$ExitNoTests = 44

function Write-Step {
    param([string]$Text, [string]$Color = 'Gray')
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Text) -ForegroundColor $Color
}

function Start-Countdown {
    <# A visible, interruptible wait so the script never looks frozen. #>
    param([int]$Seconds, [string]$Reason)

    if ($Seconds -le 0) { return }
    Write-Step "$Reason - waiting ${Seconds}s" 'DarkGray'
    $remaining = $Seconds
    while ($remaining -gt 0) {
        $chunk = [Math]::Min(10, $remaining)
        Start-Sleep -Seconds $chunk
        $remaining = $remaining - $chunk
        if ($remaining -gt 0) { Write-Step "  ...${remaining}s remaining" 'DarkGray' }
    }
}

function Read-NewText {
    <# Reads bytes appended to a file since $Position (which it advances). #>
    param([string]$Path, [ref]$Position)

    if (-not (Test-Path $Path)) { return '' }

    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
                                     [System.IO.FileAccess]::Read,
                                     [System.IO.FileShare]::ReadWrite)
    }
    catch {
        return ''
    }

    try {
        if ($fs.Length -le $Position.Value) { return '' }
        $count = [int]($fs.Length - $Position.Value)
        [void]$fs.Seek($Position.Value, [System.IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] $count
        $read = $fs.Read($buffer, 0, $count)
        $Position.Value = $Position.Value + $read
        return [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
    }
    finally {
        $fs.Dispose()
    }
}

function ConvertTo-ArgumentString {
    <# Start-Process joins ArgumentList without quoting; do it explicitly. #>
    param([string[]]$Arguments)

    $parts = @()
    foreach ($a in $Arguments) {
        if ($a -match '[\s"]') { $parts += '"' + ($a -replace '"', '\"') + '"' }
        else { $parts += $a }
    }
    return ($parts -join ' ')
}

function Test-CodeIntegrityBlockedSince {
    <#
        Authoritative, locale-independent secondary detector: ask Windows
        whether it actually refused to load one of our assemblies during the
        run window. Best-effort - never let a logging problem fail the run.
    #>
    param([DateTime]$Since)

    try {
        $events = Get-WinEvent -FilterHashtable @{
            LogName   = 'Microsoft-Windows-CodeIntegrity/Operational'
            Id        = 3033, 3077
            StartTime = $Since
        } -ErrorAction Stop
    }
    catch {
        return $false
    }

    foreach ($e in $events) {
        if ($e.Message -match 'AssetProvenanceHelper') { return $true }
    }

    return $false
}

function Invoke-TestRun {
    <#
        Runs dotnet test once, streaming its output live and enforcing a hard
        timeout. Returns @{ Blocked; TimedOut; ExitCode }.
    #>
    param(
        [string[]]$DotnetArgs,
        [string]$Label,
        [int]$TimeoutSeconds
    )

    $stamp   = [Guid]::NewGuid().ToString('N')
    $outFile = Join-Path $env:TEMP "sac-test-$stamp.out.log"
    $errFile = Join-Path $env:TEMP "sac-test-$stamp.err.log"

    Write-Step "run [$Label] starting (timeout ${TimeoutSeconds}s)" 'DarkCyan'
    $started = (Get-Date).AddSeconds(-2)

    try {
        $proc = Start-Process -FilePath 'dotnet' `
                              -ArgumentList (ConvertTo-ArgumentString $DotnetArgs) `
                              -NoNewWindow -PassThru `
                              -RedirectStandardOutput $outFile `
                              -RedirectStandardError $errFile

        # Start-Process -PassThru returns a Process whose ExitCode is NOT
        # readable after exit unless the native handle was cached while the
        # process was still alive. Without this, ExitCode comes back $null and
        # every run looks like a failure. Touch .Handle to cache it.
        try { $null = $proc.Handle } catch { }

        $pos = [ref]([long]0)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $lastBeat = 0
        $timedOut = $false

        while (-not $proc.HasExited) {
            Start-Sleep -Milliseconds 500

            $chunk = Read-NewText -Path $outFile -Position $pos
            if ($chunk -ne '') { Write-Host $chunk.TrimEnd() }

            $elapsed = [int]$sw.Elapsed.TotalSeconds

            # Heartbeat so a long-but-healthy run never looks frozen.
            if (($elapsed - $lastBeat) -ge 30) {
                $lastBeat = $elapsed
                Write-Step "  [$Label] still running (${elapsed}s / ${TimeoutSeconds}s)" 'DarkGray'
            }

            if ($elapsed -gt $TimeoutSeconds) {
                $timedOut = $true
                Write-Warning "[$Label] exceeded ${TimeoutSeconds}s - killing process tree."
                & taskkill /T /F /PID $proc.Id 2>&1 | Out-Null
                break
            }
        }

        if (-not $timedOut) { $proc.WaitForExit() }

        # Flush whatever is left in both streams.
        $tail = Read-NewText -Path $outFile -Position $pos
        if ($tail -ne '') { Write-Host $tail.TrimEnd() }

        $errText = ''
        if (Test-Path $errFile) { $errText = Get-Content $errFile -Raw }
        if ($null -eq $errText) { $errText = '' }
        if ($errText.Trim() -ne '') { Write-Host $errText.TrimEnd() -ForegroundColor DarkYellow }

        $code = 0
        if ($timedOut) {
            $code = 124
        }
        else {
            $raw = $proc.ExitCode
            if ($null -eq $raw) {
                # Should not happen now that the handle is cached, but never
                # let an unknown exit code masquerade as success OR as failure.
                Write-Warning "[$Label] exit code could not be read; treating as inconclusive."
                $code = -1
            }
            else {
                $code = [int]$raw
            }
        }

        $outText = ''
        if (Test-Path $outFile) { $outText = Get-Content $outFile -Raw }
        if ($null -eq $outText) { $outText = '' }
        $allText = $outText + "`n" + $errText

        $blocked = $false
        if ($allText -match [regex]::Escape($BlockCode)) { $blocked = $true }
        if (-not $blocked -and $code -ne 0) {
            if (Test-CodeIntegrityBlockedSince -Since $started) { $blocked = $true }
        }

        # "no test matches the filter" is a usage mistake, not a defect.
        $noTests = $false
        if ($allText -match 'Kein Test entspricht' -or $allText -match 'No test matches') {
            $noTests = $true
        }

        Write-Step "run [$Label] finished: exit=$code blocked=$blocked timedOut=$timedOut" 'DarkCyan'
        return @{ Blocked = $blocked; TimedOut = $timedOut; ExitCode = $code; NoTests = $noTests }
    }
    finally {
        foreach ($f in @($outFile, $errFile)) {
            if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
        }
    }
}

try {
    Write-Host '== SAC-safe test run ==' -ForegroundColor Cyan
    Write-Host "Configuration : $Configuration"
    if ($Filter) { Write-Host "Filter        : $Filter" }
    Write-Host "Settle        : ${SettleSeconds}s   Attempts: $MaxAttempts   Backoff: ${BackoffSeconds}s"
    Write-Host "Timeouts      : canary ${CanaryTimeoutSeconds}s / suite ${SuiteTimeoutSeconds}s"
    Write-Host "Live output is streamed below; a heartbeat prints every 30s." -ForegroundColor DarkGray

    Start-Countdown -Seconds $SettleSeconds -Reason 'Letting freshly built assemblies settle'

    $baseArgs = @('test', 'AssetProvenanceHelper.sln', '-c', $Configuration, '--no-build')
    if ($Filter) { $baseArgs += @('--filter', $Filter) }

    # A single cheap test proves the product assembly is loadable by the test
    # host. Paying ~15s here beats discovering a block 2 minutes into the suite.
    $canaryArgs = @('test', 'AssetProvenanceHelper.sln', '-c', $Configuration, '--no-build',
                    '--filter', 'FullyQualifiedName~ValidationServiceTests')

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Host ''
        Write-Step "-- attempt $attempt of $MaxAttempts --" 'Cyan'

        if (-not $SkipCanary) {
            $canary = Invoke-TestRun -DotnetArgs $canaryArgs -Label 'canary' -TimeoutSeconds $CanaryTimeoutSeconds

            if ($canary.TimedOut) {
                Write-Warning 'Canary timed out. Treating as inconclusive.'
                exit $ExitTimeout
            }

            if ($canary.Blocked) {
                Write-Warning "Canary hit a code-integrity block ($BlockCode)."
                if ($attempt -lt $MaxAttempts) {
                    Start-Countdown -Seconds ($BackoffSeconds * $attempt) -Reason 'Backing off'
                }
                continue
            }

            if ($canary.ExitCode -ne 0) {
                Write-Warning 'Canary tests FAILED for a non-SAC reason. Continuing to the full run so you get the real report.'
            }
        }

        $run = Invoke-TestRun -DotnetArgs $baseArgs -Label 'suite' -TimeoutSeconds $SuiteTimeoutSeconds

        if ($run.TimedOut) {
            Write-Host ''
            Write-Host 'RUN TIMED OUT - inconclusive.' -ForegroundColor Yellow
            Write-Host 'A hung test (e.g. a real modal dialog with no seam installed) is the usual cause.'
            exit $ExitTimeout
        }

        if ($run.Blocked) {
            Write-Warning "Suite hit a code-integrity block ($BlockCode). Results from this attempt are MEANINGLESS."
            if ($attempt -lt $MaxAttempts) {
                Start-Countdown -Seconds ($BackoffSeconds * $attempt) -Reason 'Backing off'
            }
            continue
        }

        if ($run.NoTests) {
            Write-Host ''
            Write-Host 'NO TESTS MATCHED THE FILTER - nothing was run.' -ForegroundColor Yellow
            if ($Filter) { Write-Host "Filter was: $Filter" -ForegroundColor Yellow }
            Write-Host 'This is a filter/usage problem, not a test failure and not a SAC block.' -ForegroundColor Yellow
            exit $ExitNoTests
        }

        if ($run.ExitCode -eq 0) {
            Write-Host ''
            Write-Host 'TESTS PASSED (no code-integrity interference).' -ForegroundColor Green
            exit 0
        }

        if ($run.ExitCode -lt 0) {
            Write-Host ''
            Write-Host 'INCONCLUSIVE - exit code unavailable.' -ForegroundColor Yellow
            exit $ExitTimeout
        }

        Write-Host ''
        Write-Host 'TESTS FAILED - and this is a REAL result, not a SAC block.' -ForegroundColor Red
        Write-Host 'Investigate the failures above as genuine defects.' -ForegroundColor Red
        exit 1
    }

    Write-Host ''
    Write-Host '=======================================================================' -ForegroundColor Yellow
    Write-Host ' ENVIRONMENT BLOCK - NOT A PRODUCT DEFECT' -ForegroundColor Yellow
    Write-Host '=======================================================================' -ForegroundColor Yellow
    Write-Host "Smart App Control blocked AssetProvenanceHelper.dll ($BlockCode) on every"
    Write-Host "one of $MaxAttempts attempts. NOTHING was proven about the code - do not"
    Write-Host 'report these as test failures, and do not "fix" anything based on them.'
    Write-Host ''
    Write-Host 'What actually works (see docs/sac-test-execution.md):'
    Write-Host '  * Wait a few minutes, then re-run. The block is transient.'
    Write-Host '  * Prefer targeted runs:  -Filter "FullyQualifiedName~<TestClass>"'
    Write-Host '  * Avoid long chains of rebuild+full-suite cycles; that is the trigger.'
    Write-Host '  * Never disable SAC/Defender to work around this.'
    exit $ExitEnvironmentBlock
}
finally {
    Pop-Location
}

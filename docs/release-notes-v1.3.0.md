## Asset Provenance Helper v1.3.0

This release ships the Provider Templates / Request Queue / Direct Mode feature set
(pending since before this bug-fix cycle began) together with a QA hardening pass
responding to two independent audits: an internal bug-bash (`docs/audits/_bugRun1.md`)
and a follow-up zero-exception coverage audit (`docs/audits/gaa1.md`).

### Fixes from the internal bug-bash (`_bugRun1.md`, B-01 through B-11)

- **B-01**: fixed `CS8625` nullable-array errors that only surfaced on a clean/CI build,
  not a warm local one, and were silently masking a red CI.
- **B-02 / B-06 / B-07**: removed a real-timer race and a dependency on the physical OS
  mouse cursor from the prompt-overlay tests via a new `CursorPositionProvider` seam;
  fixed an `Environment.TickCount` 32-bit overflow in the test harness.
- **B-03**: the "tests run non-parallel by design" invariant is now actually enforced
  (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`), not silently
  bypassed.
- **B-04**: fixed a test-workspace disposal-ordering bug that leaked temp directories,
  and hardened cleanup to surface leaks instead of swallowing them.
- **B-08**: stopped re-reading the ambient clock as a fallback in two provenance
  timestamp paths.
- **B-10 / B-11**: removed stale build artifacts; normalized thread-join timeouts.

### Fixes from the independent zero-exception audit (`gaa1.md`)

- Fixed two provider-template tests that depended on the checkout's line-ending mode
  (CRLF vs LF) rather than on the actual template-rendering behavior; added
  `.gitattributes` so `.cs`/`.md` files check out with consistent line endings on every
  machine regardless of local git configuration.
- Removed 9 of 12 `[ExcludeFromCodeCoverage]` exclusions that were hiding code already
  exercised by hundreds of existing tests (`MainForm.Layout.cs`, `MainForm.Designer.cs`).
  Made `Program.cs`'s startup orchestration testable via behavior-preserving seams and
  added direct tests for normal startup, the already-running branch, and the
  startup-exception path. The 3 remaining exclusions (plus two structurally-forced ones
  in `Program.cs`, each wrapping a single blocking modal call or the WinForms message
  loop) are now an enumerated, gate-enforced allowlist (`code-coverage-exclusions.json`).
- Replaced the coverage gate: instead of a hardcoded 13-file list and a rounded 90%/85%
  threshold, it dynamically inventories every production `.cs` file, fails if any file
  with executable code is missing from the report, checks method coverage (previously
  unchecked entirely), and ratchets on exact covered/total counts
  (`code-coverage-baseline.json`) instead of rounded percentages.
- Closed all 4 previously fully-uncovered production methods, and closed
  `HelpOverlayControl`'s own `KeyDown` handling (0% → full branch coverage).
- Widened the mutation-testing scope from ~6 files to the full production tree.
- Added `.github/workflows/zero-exception-audit.yml`: a manually-dispatched,
  non-fail-fast run of every QA phase that aggregates results at the end instead of
  stopping at the first failure, so one early failure never hides whether everything
  else would have passed.

Coverage after this pass: **463/463 methods (100%)**, 91.4% lines, 85.2% branches -
up from 447/451 methods, 90.6% lines, 85.1% branches at the start of the audit.
Closing the remaining line/branch gaps (concentrated in the transaction, rollback, and
recovery paths of `MainForm.MainWorkflow.cs`, `MainForm.Recovery.cs`, and the
`AssetProcessorService.*` partial classes) is tracked as ongoing work - see
`docs/plans/_fixPlan_gaa1.md` for the prioritized list and the handoff prompt for
further independent review.

### Verification

- Source commit: `9addf11a9ab5f7051e6d6249831f76c56e78fa30`
- Verified from a **clean working tree** via `scripts/verify_like_ci.ps1`
  (not a warm/incremental build - see `AGENTS.md` for why that distinction matters):
  - Debug build with `-warnaserror`: 0 warnings, 0 errors.
  - Release build with `-warnaserror`: 0 warnings, 0 errors.
  - Debug tests: 1008 passed, 0 failed.
  - Release tests: 1008 passed, 0 failed.
  - `Category=RecoveryCritical`: 161 passed, 0 failed.
- Windows CI (build/test/flakiness/publish/smoke + coverage gate): see the Actions run
  linked from this release's commit.

### Known limitation

The application executable is **unsigned**. Windows Smart App Control blocks the
self-contained published `.exe` by default; run via `dotnet AssetProvenanceHelper.dll`
(the signed host) or via `scripts/run_smoke_tests_sac_safe.ps1` to verify startup on a
SAC-enabled machine. This is an environment constraint, not a product defect - see
`AGENTS.md` for the full explanation.

### Upgrade

Extract the ZIP into a clean directory rather than overlaying a previous version. User
settings and recovery state remain in `%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper`.

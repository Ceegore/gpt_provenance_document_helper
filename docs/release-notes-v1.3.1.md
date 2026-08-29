## Asset Provenance Helper v1.3.1

This release fixes every issue from an independent verification audit of v1.3.0
(`docs/audits/a.md`): both `DEFECT` findings and all 10 ranked `RISK` items, plus a
real test-infrastructure regression discovered while fixing them.

### Fixes

**DEFECT 1 (MEDIUM-HIGH)** - `ProgramStartupTests` resolved the real per-user
`%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper` state directory even with other test
seams in place, risking a durably-written legacy-migration marker on a real user's
machine if the suite ran before the app's first real launch. Added
`AppBootstrap.StateDirectoryOverride` / `Program.BaseDirectoryOverride` seams and
rewrote the tests to isolate every run, with new integration tests proving state
directory creation, legacy-file copying, existing-state-wins, and
marker-prevents-reimport - all exercised through `Program.RunApplication()` itself.

**DEFECT 2 (MEDIUM)** - the coverage ratchet compared only *covered* counts, so
total (and therefore uncovered) code could grow freely without ever tripping it.
Extracted the comparison into `Test-CoverageRatchet`, which now ratchets on
*uncovered* counts plus an explicit check that method coverage cannot regress from
its current 100%, with 6 synthetic scenarios locking in the fix.

**RISK 1-5** (transaction/rollback/recovery edge cases) - added targeted tests for:
the foreign-file rollback reconciliation decision in `MainForm`'s Main-commit
workflow; the central hash-owned move primitive's race-condition re-checks; legacy
provenance byte-authority failure paths (corrupt/unreadable backup, corrupt
canonical, neither present); the replacement commit-forward recovery error arms
(previously entirely unexercised - reached only via startup recovery of an
interrupted journal, never via the live commit path); and Main's staging-authority
negative paths (independently corrupted staged image/ingame/provenance).

**RISK 6-9** (test-quality gaps) - rewrote four tests that executed real code but
asserted far less than their names claimed: a Phase-3 cancellation test that only
checked "some IOException occurred" instead of exact survivor state and full
recovery; a "save throws after provenance restore" test that actually failed at an
earlier, different step; a "recovery" test that persisted state but never invoked
the actual recovery entry point; and an "atomic save" test a naive
`File.WriteAllText` implementation would have passed just as easily.

**RISK 10** - `zero-exception-audit.yml`'s prerequisite steps (checkout/setup/restore)
were ordinary fail-fast steps, so a Restore failure would skip every later
independent audit phase - defeating that workflow's whole stated purpose. Made them
non-fail-fast too, consistent with every other phase in that workflow.

### A regression found while fixing DEFECT 1

Two of the new isolated-directory tests initially popped real, blocking `MessageBox`
dialogs on screen: `MainForm.MessageBoxProvider` and `Program.MessageProvider` are
separate seams from the ones already installed for that test, and the isolated test
directory naturally has no `templates/` folder and a deliberately non-JSON legacy
settings fixture. Fixed by seaming both by default in the shared test helper.

A **second, pre-existing** instance of the same class of bug was found separately,
by running the actual full test suite end-to-end rather than individual test
classes in isolation: a test invoking the "Open Downloads Folder" feature without
seaming `MainForm.OpenFolderProvider` triggered a real `Process.Start(explorer.exe)`
against a test workspace's temp folder - which genuinely existed at the moment of
the call, but was deleted moments later when the workspace was disposed, before
Explorer finished rendering. Fixed the same way, matching the pattern already used
everywhere else in the suite for this exact seam.

### Verification

- Verified from a **clean working tree** via `scripts/verify_like_ci.ps1`:
  Debug and Release builds with `-warnaserror` (0 warnings, 0 errors each), full
  test suite 1026/1026 passed, `Category=RecoveryCritical` passed, with no
  test-triggered dialogs anywhere in the run.
- `scripts/verify_coverage.ratchet.tests.ps1`: all 6 synthetic ratchet scenarios
  behave as expected.
- Coverage gate: passes; 463/463 methods still at 100%.
- Windows CI (build/test/flakiness/publish/smoke + coverage gate): both jobs green
  on the exact merged commit - see the Actions run linked from this release.

### Known limitation

The application executable is **unsigned**. Windows Smart App Control blocks the
self-contained published `.exe` by default; run via `dotnet AssetProvenanceHelper.dll`
(the signed host) or via `scripts/run_smoke_tests_sac_safe.ps1` to verify startup on
a SAC-enabled machine. This is an environment constraint, not a product defect - see
`AGENTS.md` for the full explanation.

### Upgrade

Extract the ZIP into a clean directory rather than overlaying v1.3.0. User settings
and recovery state remain in `%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper`.

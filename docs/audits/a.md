# Independent Verification Audit — Ceegore/gpt_provenance_document_helper v1.3.0

## 1. Verdict

I audited the `v1.3.0` tag at commit **`620f7356493c0bcc00b9229db4426b210b90b1ff`**; the annotated tag resolves to that commit.

**Verdict: 2 `DEFECT`, 10 `RISK`, 6 `NOT VERIFIABLE HERE`.**

The most important conclusion is that I **did not find a presently demonstrable data-loss defect in the production transaction/rollback/recovery state machines**. The long sequence of earlier fixes around Reference creation, Reference replacement, Main promotion, cancellation, legacy recovery, reparse-point safety, and hash ownership is materially present in the current code.

The two demonstrated defects I did find are in the verification/startup-test infrastructure:

| Category | Count | Highest severity |
|---|---:|---:|
| `DEFECT` | **2** | **MEDIUM-HIGH** |
| `RISK` | **10** | **HIGH-value coverage gap** |
| `NOT VERIFIABLE HERE` | **6** | Not scored |

The exact-commit CI evidence is also much better than during `gaa1`: the Coverage Gate completed successfully; Debug build/tests, Release build/tests and `RecoveryCritical` all completed successfully. At the last read, however, the **20× loop was still running**, and publish/package/smoke steps were **pending**. They are therefore **not failures** and I do not count them as such.

The committed coverage baseline for this release is **8513/9316 lines, 2450/2875 branches, and 463/463 methods**.

---

## 2. `DEFECT` findings

### `DEFECT` 1 — MEDIUM-HIGH — `ProgramStartupTests` modifies the real per-user migration state

**Files:**  
`tests/AssetProvenanceHelper.Tests/ProgramStartupTests.cs:45-72`  
`src/AssetProvenanceHelper/Program.cs:81-83`  
`src/AssetProvenanceHelper/Services/AppBootstrap.cs:82-86, 92-178, 261-275`

#### Mechanism

`RunApplication_NormalStartup_ConstructsMainFormAndInvokesRunProvider()` correctly replaces the real mutex, WinForms initializer, and `Application.Run`, but it does **not** replace the application's state directory. It calls the real:

```text
Program.RunApplication()
```

which then does:

```text
stateDirectory = AppBootstrap.GetStateDirectory()
Directory.CreateDirectory(stateDirectory)
AppBootstrap.MigrateLegacyState(baseDirectory, stateDirectory)
```

`GetStateDirectory()` resolves to the actual user's:

```text
%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper
```

and `CreateContext()` independently resolves that real directory again.

The migration routine creates a durable `.legacy-state-migration-complete` marker even when the test's `AppContext.BaseDirectory` contains no legacy state.

#### Concrete failure scenario

1. A developer/user has a pre-v1.3 portable installation containing an old `session.json`, `settings.json`, or replacement journal.
2. They have **not yet launched v1.3**, so the real LocalAppData state directory has not performed legacy migration.
3. They run the test suite first.
4. `ProgramStartupTests` executes `RunApplication()` against the testhost output directory but the **real LocalAppData state directory**.
5. `MigrateLegacyState()` finds nothing useful in the testhost directory and nevertheless commits the migration-complete marker.
6. The real v1.3 application is then launched from the old portable directory.
7. Migration sees the previously written marker and returns immediately.
8. The real legacy recovery/session state is never imported.

The old files themselves are not deleted, but the application can silently stop recognizing an interrupted legacy session. For a program specifically designed to preserve recovery authority, that is a concrete behavioral failure caused by the test suite.

#### Test-quality consequence

The remediation requirement was to test:

- normal startup;
- existing mutex;
- startup exception;
- state-directory creation;
- legacy migration.

Normal startup, mutex and exception paths now have meaningful tests. State-directory creation and migration are **executed but not isolated/asserted at the `Program` integration boundary**. Separate workspace-based `AppBootstrap` migration tests are useful, but they do not make this real-profile mutation safe.

#### Suggested fix

Introduce a state-directory seam, preferably inside `AppBootstrap`, for example:

```text
internal static Func<string>? StateDirectoryProvider;
```

and have `GetStateDirectory()` use it when present. Every `ProgramStartupTests` test should point it at a `TestWorkspace` directory and reset it in `finally`.

Then add integration assertions that:

- the temporary state directory is created;
- legacy files from a temporary `baseDirectory` are copied;
- existing stable state wins;
- the marker prevents a second import;
- **nothing under the real LocalAppData location is touched**.

---

### `DEFECT` 2 — MEDIUM — coverage ratchet can be passed while coverage regresses

**File:** `scripts/verify_coverage.ps1:239-246`

#### Mechanism

The script collects both covered and total counters:

```text
lines / totalLines
branches / totalBranches
methods / totalMethods
```

but its baseline gate compares only:

```powershell
$linesCovered    -lt $baseline.lines
$branchesCovered -lt $baseline.branches
$methodsCovered  -lt $baseline.methods
```

It never compares the totals, uncovered counts, or exact ratios.

The baseline itself explicitly contains both covered and total values, making the omission particularly clear.

#### Concrete failure scenario

Current branch coverage is:

```text
2450 / 2875
```

A change adds ten new executable, entirely untested branches.

The result becomes:

```text
2450 / 2885
```

The branch coverage rate has regressed, but:

```powershell
2450 -lt 2450
```

is false, so the ratchet passes.

The same applies to lines. It can even occur for methods: adding an entirely uncovered production method can leave `methodsCovered == 463` while `totalMethods` becomes 464, yet the gate still passes.

The dynamic source-file inventory does not prevent this because the new code can be in an already-instrumented file.

#### Suggested fix

Ratchet on uncovered counts or exact fractions without rounding. For example:

```text
currentUncoveredLines    <= baselineUncoveredLines
currentUncoveredBranches <= baselineUncoveredBranches
currentUncoveredMethods  <= baselineUncoveredMethods
```

For the current method baseline, additionally require:

```text
methodsCovered == totalMethods
```

because the release currently has 100% method coverage.

Even stronger: compare fractions by integer cross-multiplication so no floating-point/rounding issue exists.

Add script-level synthetic tests for at least:

1. same covered / increased total → **must fail**;
2. increased covered / proportionally larger total that lowers rate → **must fail**;
3. new uncovered method → **must fail**;
4. genuinely improved coverage → pass;
5. code deletion → explicitly defined policy rather than accidental behavior.

This is exactly the kind of gate that can currently be satisfied without maintaining the standard it says it enforces.

---

## 3. `RISK` findings

I am deliberately not padding this section to 20 items. These are the ten gaps I consider materially worth spending engineering time on.

### 1. Main failure reconciliation has a mostly-uncovered special-case branch

**`MainForm.MainWorkflow.cs:393-409`**

This branch recognizes the case where rollback reports a failure because a foreign/pre-existing root Main file exists, but **none of the transaction's own final/ingame/temp artefacts exist**. It then resets Main metadata instead of treating the foreign file as a critical rollback failure.

Cobertura shows the compound decision at approximately `402` essentially unexercised.

Why it matters: a mistake here can either falsely close the app as CRITICAL or, worse after a future refactor, wrongly classify transaction output as foreign/vice versa.

**What would confirm/refute it:** construct a Reference session with a foreign root-Main collision and no final/ingame/temp artifacts; force rollback reconciliation and assert the foreign bytes are untouched and Main metadata is reset. Then add near-miss cases with each single transaction artifact present and assert fail-closed behavior.

---

### 2. The final race branches of the central forward-move primitive are not both exercised

**`AssetProcessorService.FileOps.cs:427-433`**

`MoveHashOwnedFileWithoutOverwrite()` deliberately re-checks safety after its race hook and has explicit branches for:

- source disappearing after initial validation;
- destination appearing after initial validation.

Those are high-value branches because virtually all canonical promotion eventually depends on this primitive.

**What would confirm/refute it:** inject the existing hook so one test deletes/moves the source and another creates a destination between the first check and final mutation. Assert no overwrite, no mutation of the foreign destination, and deterministic failure.

---

### 3. Legacy OLD-provenance authority failure paths are thinly covered

**`AssetProcessorService.Reference.cs:828-859`**

Current legacy recovery correctly hydrates a missing raw provenance hash from either the backup or canonical provenance. The happy repair is good, but the less-covered branches are precisely the cases where the candidate is corrupt/unreadable or the authority cannot be established.

Current code now clones journal sessions before hydration and durably writes the resulting hash back before destructive recovery, fixing the old `bugs15` aliasing defect.

**What would confirm/refute it:** real legacy-null-hash journals with corrupt backup, unreadable backup, corrupt canonical, and neither candidate present; assert zero destructive mutation and preservation of the journal.

---

### 4. Replacement commit-forward error branches deserve explicit phase tests

**`MainForm.Recovery.cs:843-870`**

The current state machine sensibly chooses rollback versus commit-forward from both replacement phase and durable session authority. The error arms around exact NEW validation, backup cleanup and journal finalization are less exercised than the normal commit-forward path.

**What would confirm/refute it:** inject one failure at a time after `SessionSwitched`/`CleanupPending`: NEW exact-validation failure, backup deletion failure, journal deletion failure. Assert which authority remains durable and exactly which journal/backup files remain.

---

### 5. Main's final staging-authority negative paths are individually valuable

**`AssetProcessorService.Main.cs:1092-1152`**

The design is healthy: before canonical promotion, Main rechecks path safety and each deterministic staged file's authority. But many failure arms have only one side covered.

**What would confirm/refute it:** independently corrupt the staged Main, ingame copy, and provenance after their first verification; independently introduce a reparse/path change. Each case should result in **zero new canonical promotion** and leave enough journal state for safe recovery.

---

### 6. Cancellation's partial Phase-3 deletion behavior is not asserted strongly enough

**Production:** `SessionService.cs:410-485`  
**Test:** `ComprehensiveCoverageTests.cs:331-358`

The production logic is currently recoverable: if deletion of one deterministic cancellation temp fails, it records the error, attempts the other, throws while leaving phase `FilesRenamed`, and a later cancellation/recovery can finish idempotently.

But the test mainly checks that an `IOException` with the expected wording occurs.

**What would confirm/refute it:** after the injected first failure, assert the exact survivor/deleted files and durable phase, then call cancellation/recovery a second time and prove complete cleanup without touching any unrelated file.

---

### 7. One cancellation test does not test what its name claims

**`ComprehensiveCoverageTests.cs:309-329`**

`SessionService_Cancel_SaveThrowsAfterProvenanceRestore_ThrowsAggregateException` merely locks the Reference image and asserts an `IOException`.

That forces an early file-move failure; it does not specifically demonstrate a **save failure after provenance restore**, nor does it meaningfully assert restored state.

This is a textbook example of a test that can execute code and still give more assurance than its assertions justify.

**What would confirm/refute it:** use the existing cancellation phase-saving hook to throw at the exact intended save boundary; assert provenance restored, Reference location/hash, cancellation phase, durable session contents and temp-file state.

---

### 8. `REG_004` is named as a recovery test but never performs recovery

**`RegressionTests.cs:122-156`**

`REG_004_CompletedAsset_WithFinalProvenance_IdentifiableDuringRecovery`:

- creates the completed asset;
- manually marks `IsMainCommitting`;
- saves it;
- asserts the output and active session exist.

It never invokes startup recovery. It would still pass if a regression in the recovery routine subsequently deleted that completed asset.

There are other genuine recovery tests, so this is not evidence that recovery as a whole is untested; it is a weak/misleading individual test.

**What would confirm/refute it:** run the actual recovery entry point with the prepared persisted state and assert the completed asset is retained while the stale active journal is retired.

---

### 9. `REG_020` does not actually establish atomic settings persistence

**`RegressionTests.cs:278-303`**

`REG_020_SettingsService_SavesAtomicallyViaTempFile` saves a settings object and reloads it. A naïve direct `File.WriteAllText(settings.json, ...)` implementation would satisfy this test just as easily.

Other tests do exercise temp cleanup, so again this is not “no testing”; the name is simply stronger than the assertion.

**What would confirm/refute it:** inject a failure after the new temporary file has been durably written but before promotion and assert that the previous `settings.json` remains byte-for-byte intact.

---

### 10. The “zero-exception” workflow is not fully non-fail-fast at prerequisite level

**`.github/workflows/zero-exception-audit.yml`**

The audit phases themselves use `continue-on-error`, but checkout/setup/restore are ordinary fail-fast prerequisite steps. Therefore a Restore failure can still cause the later independent audit phases to be skipped.

This does **not** create a false-green result—the prerequisite failure still makes the job fail—so I do not call it a gate-bypass defect. But it can defeat the workflow's stated diagnostic goal of showing every phase's result in one run.

**What would confirm/refute it:** deliberately force Restore to fail in a disposable workflow branch and verify which later phases are `skipped`. If the intended guarantee is literally “every independently runnable phase reports a result”, the workflow needs prerequisite/result handling designed around that requirement.

---

## 4. `NOT VERIFIABLE HERE`

These are explicitly **not counted against the verdict**.

1. **Actual interactive WinForms execution.** I did not attempt to treat Smart App Control preventing unsigned-binary launch as a software defect.

2. **Final result of the exact-commit 20× flakiness loop.** At my last read it was still running. Debug, Release, and `RecoveryCritical` had passed; the loop itself had neither passed nor failed yet.

3. **Exact-commit `win-x64` publish and packaged startup smoke.** They were still `pending`, i.e. **not run**, at the audit cutoff—not failed.

4. **Full widened Stryker mutation result.** The current configuration now targets essentially the whole production source tree except `MainForm.Designer.cs`, but I cannot execute mutation testing here and do not have a completed new-scope result to substitute for one.

5. **Manual GUI/display behavior:** DPI scaling, 1366×768 usability, real modal file/folder dialogs, drag/drop, clipboard interaction, focus behavior and actual rendered layout.

6. **True power-loss/process-kill filesystem durability.** The ordering can be audited statically and the fault-injection suite is extensive, but proving how `Flush(true)`, rename and filesystem persistence behave under an actual machine/process interruption requires an executable Windows test environment.

Again: none of those six is a `DEFECT`.

---

## 5. Regression matrix

### Versus `_bugRun1.md`

| Prior finding | v1.3.0 status | Audit result |
|---|---|---|
| B-01 clean-build nullable/warnaserror failure | **FIXED** | Exact-commit Debug and Release warn-as-error builds completed successfully in current CI. |
| B-02 timer race | **FIXED structurally** | Cursor-position seam replaces dependence on real cursor/timer behavior in the affected tests. |
| B-03 parallelization not actually disabled | **FIXED** | Assembly now contains `[CollectionBehavior(DisableTestParallelization = true)]`; runner config is copied to output. |
| B-04 leaked `TestWorkspace` | **FIXED materially** | `Dispose()` retries and finally throws instead of silently swallowing a leaked root. |
| B-05 weak coverage gate | **PARTIAL / NEW DEFECT** | Old rounded/hardcoded mechanism was replaced, but the new ratchet can still accept increased uncovered code: `DEFECT` 2. |
| B-06 `TickCount` overflow helper | **FIXED in reviewed code** | Old timing helper removed/replaced. |
| B-07 physical mouse dependency | **FIXED structurally** | Cursor provider seam now exists. |
| B-08 ambient second timestamp | **FIXED** | Durable/captured processing timestamp is propagated through reviewed Main/recovery paths. |
| B-09 culture restoration | **FIXED** | Culture-changing regression tests restore both cultures in `finally`. |
| B-10 stale build outputs | **FIXED in committed tree** | No stale tracked `bin/obj` net8 build tree is present in the v1.3.0 recursive inventory. |
| B-11 short thread joins | **FIXED in reviewed helpers** | Current STA/test helpers use the 30-second form. |

### Versus `gaa1.md`

| `gaa1` issue | v1.3.0 status |
|---|---|
| CRLF/LF provider-template test blocker | **FIXED.** |
| Renderer suspected of newline mutation | **REFUTED.** Renderer performs tag replacement on original template content; no newline normalization is introduced. |
| Semantic assertions checkout-dependent | **FIXED.** They normalize CRLF→LF before semantic comparison. |
| No explicit LF/CRLF preservation contract tests | **FIXED.** Both explicit LF and CRLF templates are constructed and tested. |
| Missing repo EOL policy | **FIXED.** `*.cs`, `*.md`, JSON/YAML/TXT are explicitly LF; PowerShell and solution policy is explicit separately. |
| Coverage exclusions incompletely enumerated | **FIXED enumeration.** Historical count was **12**, not 3. |
| 9 unjustified broad exclusions | **REMOVED.** |
| Program excluded wholesale | **FIXED.** Program is instrumented and has behavioral startup tests. |
| Four completely uncovered production methods | **FIXED.** Current report has **463/463 methods covered**. |
| Critical files have incomplete branch coverage | **STILL TRUE, but `RISK`, not `DEFECT`.** Specific dangerous branches are ranked above. |
| Narrow mutation scope | **FIXED structurally.** Current Stryker scope is `**/*.cs` except `MainForm.Designer.cs`; actual new-scope mutation outcome remains `NOT VERIFIABLE HERE`. |
| Skipped CI phases described as failed | **Corrected in this audit.** Current pending/not-run phases are explicitly not scored as failures. |

### Coverage-exclusion enumeration

The current allowlist has exactly **6 methods**, not 3:

1. `MainForm.BrowseDownloadFolderWithDialog`
2. `MainForm.BrowseAssetRootWithDialog`
3. `MainForm.PickManifestPathWithDialog`
4. `Program.InitializeApplicationConfigurationForReal`
5. `Program.RunApplicationForReal`
6. `Program.ShowMessageBoxForReal`

The first three are the original real modal file/folder dialog fallbacks. The other three are newly extracted single-call real-platform fallbacks around `ApplicationConfiguration.Initialize`, `Application.Run`, and `MessageBox.Show`.

The source confirms those are narrow wrapper methods rather than chunks of business logic.

The coverage verifier cross-checks discovered `[ExcludeFromCodeCoverage]` methods against the allowlist in both directions, and the exact-commit Coverage Gate passed. I therefore found **6 current sites / 6 allowlisted / 0 demonstrated unlisted sites**.

I consider the set minimal enough for this codebase. `ApplicationConfiguration.Initialize` could theoretically be exercised in a disposable child process, but excluding a one-call process-global fallback while testing all surrounding behavior through the seam is not dishonest coverage suppression.

---

## 6. What is genuinely healthy

Several mechanisms are now unusually good for a small desktop utility and should **not** be simplified away during the next cleanup.

**The CRLF repair is correct.** `.gitattributes` establishes deterministic source/template line endings, semantic tests normalize where newline style is irrelevant, and separate tests explicitly prove both LF and CRLF preservation. The renderer itself simply substitutes tags into `snapshot.Content`, so it remains line-ending-preserving.

**Initial Reference is now a real write-ahead transaction.** Durable Prepared authority exists before canonical mutation; source/provenance are staged deterministically, hash checked, path/reparse checked, then promoted with no-overwrite authority checks. A crash before staging, after staging, or after one canonical promotion leaves a state that the journal can reason about.

**Reference replacement has a credible state machine.** The current ordering is effectively:

```text
Prepared
→ OldBackupPending
→ OldBackedUp
→ NewPromotionPending
→ NewPromoted
→ SessionSwitchPending
→ SessionSwitched
→ CleanupPending
→ journal deletion
```

Recovery considers both phase **and the durable `session.json` authority**, which is the correct way to resolve a crash around the session-switch commit point.

**The old legacy-hash bugs remain fixed.** `TransactionFromJournal()` now clones the OLD/NEW sessions before hydrating old provenance authority, and the hydrated raw hash is written back to the persisted journal before destructive recovery.

**Main rollback uses raw provenance-byte authority.** It calls the exact provenance verifier, obtains the raw SHA-256 for the file actually verified, and passes that authority into the hash-owned deletion primitive. This fixes the historical BOM/current-template reconstruction class of defects.

**Destructive filesystem operations fail closed.** The strongest current pattern is:

```text
prove expected path
→ prove non-reparse hierarchy
→ prove expected bytes/hash
→ race hook / final boundary
→ re-check path safety
→ re-check byte ownership
→ mutate
```

Unknown or changed files are generally preserved rather than “cleaned up”.

**Cancellation is intentionally resumable.** `Prepared` and `FilesRenamed` are durable states; deterministic temp names plus hash ownership allow cancellation to continue after crashes and partial Phase-3 deletion.

**Session and journal writes use durable temporary-file promotion.** They do not simply overwrite recovery authority in-place.

**Single-instance/recovery state is no longer install-directory scoped.** The stable user state moved to LocalAppData and the mutex is user-scoped rather than tied to the executable directory. That closes one of the important old `vv1` architectural defects. The new problem is specifically that `ProgramStartupTests` need a seam so they do not touch that now-correct production directory.

**The coverage system is substantially improved despite `DEFECT` 2.** Dynamic production-file inventory, bidirectional exclusion validation, explicit no-executable-code exceptions, per-file reporting and method accounting are all worth keeping. The fix needed is narrow: make the baseline ratchet measure *coverage regression*, not merely decreases in covered counters.

### Bottom line

I would characterize `v1.3.0` as:

**Production transaction/recovery architecture: statically healthy, with no new demonstrated data-loss defect found.**

**Verification infrastructure: not yet clean**, because the coverage ratchet is mathematically bypassable and startup tests can mutate/suppress real migration state.

Those are both targeted fixes. I would fix those two items, add the top 5–7 risk tests above, then rerun the existing exact-commit Windows gates rather than redesigning any of the current transaction machinery.

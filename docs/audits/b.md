# Independent Verification Audit — `Ceegore/gpt_provenance_document_helper` v1.3.1

**Repository:** `https://github.com/Ceegore/gpt_provenance_document_helper`  
**Release:** `v1.3.1`  
**Branch:** `main`  
**Audited tag/main commit:** `11b747e04044ff6c4fe0b3edc333f9e2a4d867aa`  
**Parent merge commit containing the code/test changes:** `8787969ed729a5a5a6d2be6071e8ef308e38ca1c`  
**Prior audit read first:** `docs/audits/a.md`  
**PR reviewed:** #3

---

## 1. Findings enumerated before counts

### `DEFECT`

**D1 — MEDIUM — The normal test suite still writes to the real Windows clipboard in three queue tests.**  
Production route: `src/AssetProvenanceHelper/MainForm.RequestQueue.cs:250-303,337-360`.  
Concrete test instances:

1. `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidUiTests.cs:1021-1109` — `Queue_MouseUpOnEmptyAreaDoesNothing`
2. `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidUiTests.cs:1177-1245` — `Queue_ProgressSaveFailureDoesNotBreakCompletion`
3. `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidUiTests.cs:1419-1471` — `Queue_RealClipboardWriteWhenNoWriterHook`

The third is deliberate; the first two are incidental. All three make the ordinary suite non-hermetic. The third test also calls `Clipboard.Clear()` in `finally`, so it can erase a developer's pre-existing clipboard contents. In the first two, a transient OS clipboard failure can additionally fall through to a real `MessageBox.Show`, because no `MainForm.MessageBoxProvider` is installed for that path.

### `RISK`

**R1 — MEDIUM — The revised REG_020 test still does not prove that the failure occurs after the durable temp write and before promotion.**  
Test: `tests/AssetProvenanceHelper.Tests/RegressionTests.cs:355-415`.  
Production: `src/AssetProvenanceHelper/Services/SettingsService.cs:86-145`.

The production implementation is currently correct and atomic-looking, but the regression test can still pass if `SettingsService.Save()` regresses to direct `File.WriteAllText(settingsPath, ...)`: the exclusive lock on `settings.json` would make the direct write throw before changing the file, leaving the old bytes intact and leaving no temp file — exactly what the test currently asserts.

**R2 — LOW-MEDIUM — The six synthetic coverage-ratchet regression scenarios are committed but are not wired into normal CI or the zero-exception workflow.**  
Synthetic scenarios: `scripts/verify_coverage.ratchet.tests.ps1:16-99`.  
Normal coverage job: `.github/workflows/ci.yml:119-151`.  
The current ratchet implementation itself is correct. The risk is maintenance: a future regression in `Test-CoverageRatchet` can survive normal CI if current repository coverage happens not to exercise the broken comparison behavior.

### `NOT VERIFIABLE HERE`

**N1 — Widened Stryker mutation score/survivor inventory.** The scope is widened correctly, but no completed widened-scope mutation result is available to me and I cannot execute Stryker here.

**N2 — Real interactive WinForms behavior.** DPI/layout, actual modal dialogs, focus, drag/drop, real clipboard usability, and rendered behavior require an executable Windows GUI environment.

**N3 — True abrupt-power-loss/process-kill durability.** Static ordering and fault injection are strong, but actual Windows filesystem persistence semantics around `Flush(true)` and rename/promotion require a real interruption test.

### Counts

- **`DEFECT`: 1**
- **`RISK`: 2**
- **`NOT VERIFIABLE HERE`: 3**

`NOT VERIFIABLE HERE` items are not scored against the verdict.

---

# 2. Verdict

## Overall verdict

**v1.3.1 materially fixes the findings from `a.md`. I found no new demonstrated production data-loss defect in the Reference, replacement, Main, cancellation, rollback, or startup-recovery state machines.**

The two prior `DEFECT` findings are genuinely fixed:

- startup tests no longer resolve/write the real per-user LocalAppData state directory;
- the coverage ratchet now fails on increased uncovered code instead of comparing covered counters only.

Nine of the ten prior `RISK` findings are convincingly closed by the new targeted tests. The former RISK 9 test was substantially improved, but its new failure injection still does not distinguish the intended temp-write-then-promotion implementation from a naïve direct write, so I retain that point as **R1** rather than marking it fully closed.

The most important new finding is not in production transaction logic; it is another instance of exactly the **test-suite OS-side-effect class** called out in the verification request. The full suite still has three queue tests that can write to the real Windows clipboard, including one test that explicitly clears it.

That means the v1.3.1 suite is much better isolated than v1.3.0, but it is **not yet fully hermetic with respect to user/OS state**.

---

# 3. `DEFECT` — real Windows clipboard mutation remains in the normal test suite

## D1 — MEDIUM — Three queue tests reach the real Windows clipboard

### Production mechanism

`MainForm` exposes an instance seam:

```csharp
internal Action<string>? ClipboardWriter { get; set; }
```

and queue activation eventually calls:

`src/AssetProvenanceHelper/MainForm.RequestQueue.cs:337-360`

Conceptually:

```csharp
private void TryCopyPromptToClipboard(string prompt)
{
    try
    {
        if (ClipboardWriter is not null)
        {
            ClipboardWriter(prompt);
            return;
        }

        Clipboard.SetText(prompt);
    }
    catch (Exception)
    {
        ShowMessageBox(...);
    }
}
```

`HandleRequestQueueItemActivate()` calls `TryCopyPromptToClipboard(item.Prompt)` after binding the selected request:

`src/AssetProvenanceHelper/MainForm.RequestQueue.cs:250-303`

Therefore any test that activates a pending Request without first installing `form.ClipboardWriter` reaches the real process/user clipboard.

The production fallback is legitimate application behavior. The defect is that the **ordinary automated test suite exercises that fallback against real user state**.

---

## Instance 1 — `Queue_MouseUpOnEmptyAreaDoesNothing`

**File:** `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidUiTests.cs:1021-1109`

The test:

- creates a normal production-form instance;
- installs only `MainForm.OpenFileDialogProvider`;
- does **not** install `form.ClipboardWriter`;
- first tests a click on empty queue space;
- then deliberately sends a real click to row 0 to prove the row activates.

That second click routes:

```text
HandleRequestQueueMouseUp
→ HandleRequestQueueItemActivate
→ TryCopyPromptToClipboard
→ Clipboard.SetText("p")
```

The clipboard write is unrelated to what this test is trying to prove. It is an accidental OS side effect.

### Concrete failure

State:

1. developer has important text/image data on the clipboard;
2. developer runs the full suite;
3. this test activates the queue row;
4. `ClipboardWriter` is null;
5. production calls `Clipboard.SetText("p")`.

Result:

- the developer's clipboard contents are replaced by `"p"`.

That is a concrete wrong side effect from running a test.

### Additional modal-dialog failure mode

If the system clipboard is temporarily busy/unavailable, `Clipboard.SetText()` can throw.

`TryCopyPromptToClipboard` catches that exception and calls `ShowMessageBox`.

The real fallback in `MainForm` is at approximately:

`src/AssetProvenanceHelper/MainForm.cs:462-484`

This test does not install `MainForm.MessageBoxProvider`, so a clipboard access failure can turn into a **real blocking `MessageBox.Show`**, the same class of full-suite failure already found while fixing PR #3.

---

## Instance 2 — `Queue_ProgressSaveFailureDoesNotBreakCompletion`

**File:** `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidUiTests.cs:1177-1245`

This test constructs a deliberately broken request-progress path, imports a manifest, and invokes:

```text
HandleRequestQueueItemActivate
```

directly.

It installs `MainForm.OpenFileDialogProvider` but does not install `form.ClipboardWriter`.

The activation therefore writes the Request prompt `"p"` to the real clipboard before the test continues into the progress-save failure path.

Again, clipboard behavior is not part of this test's purpose.

It also has the same conditional real-MessageBox path if the OS clipboard operation throws.

---

## Instance 3 — `Queue_RealClipboardWriteWhenNoWriterHook`

**File:** `tests/AssetProvenanceHelper.Tests/UpgradeV13ParanoidUiTests.cs:1419-1471`

This one is explicit:

```csharp
// No ClipboardWriter hook: real WinForms clipboard is used.
Assert.Null(form.ClipboardWriter);
```

It activates the queue item and then asserts:

```csharp
Assert.True(Clipboard.ContainsText());
Assert.Equal("clipboard prompt", Clipboard.GetText());
```

Finally:

```csharp
Clipboard.Clear();
```

### Concrete failure

If the developer starts with clipboard content `X`:

1. the test overwrites `X` with `"clipboard prompt"`;
2. the `finally` block clears the clipboard;
3. the developer's original `X` is not restored.

This is a direct user-state mutation caused by the standard test suite.

The fact that it is intentional does not make the ordinary suite hermetic.

It is comparable to a test intentionally launching Explorer merely to verify `Process.Start`: it can be a valid manual/integration test, but it should not silently modify interactive OS state during every unit/regression run.

---

## Why the green 20× CI loop does not refute this defect

The completed CI run proves the clipboard was usable enough on that runner for the tests to finish. It does not prove that the tests are isolated.

A test can:

- modify external state,
- pass every assertion,
- run successfully 20 times,

and still be a defective automated test.

Indeed, this exact class of defect is why the pre-existing Explorer launch was important: success/failure of the test is separate from whether it should have launched a real OS process in the first place.

---

## Recommended fix

The simplest targeted fix is:

1. Set `form.ClipboardWriter = ...` in both incidental tests:
   - `Queue_MouseUpOnEmptyAreaDoesNothing`
   - `Queue_ProgressSaveFailureDoesNotBreakCompletion`

2. Remove the real clipboard mutation from the normal suite:
   - either rewrite `Queue_RealClipboardWriteWhenNoWriterHook` to exercise an injectable platform adapter;
   - or move it into an explicitly manual/OS-integration category that is not part of ordinary Debug/Release/20× runs.

3. Prefer a **default side-effect guard** in `UpgradeV13ParanoidUiTests.CreateProductionForm()` or a common UI-test fixture. For example:
   - default `ClipboardWriter` to a recorder/no-op;
   - default `MessageBoxProvider` to throw a test failure on an unexpected dialog;
   - default `OpenFolderProvider` to throw or record;
   - default dialog providers similarly.

Then individual tests opt into the side effect they intend to exercise.

This is safer than relying on every future test author remembering every seam.

A particularly useful pattern is:

```text
normal UI test factory
→ all OS-output seams are fail-closed/recording by default
→ a test explicitly overrides exactly the seam it is testing
```

That would have detected:

- the old Explorer bug,
- the missing startup MessageBox seams,
- these clipboard leaks,

immediately and deterministically.

---

# 4. `RISK` — test quality

## R1 — REG_020 still cannot distinguish atomic temp promotion from a direct locked write

**Test:** `tests/AssetProvenanceHelper.Tests/RegressionTests.cs:355-415`  
**Production:** `src/AssetProvenanceHelper/Services/SettingsService.cs:86-145`

### What improved

The old v1.3.0 test did only:

```text
Save
→ Load
→ compare values
```

A direct `File.WriteAllText(settings.json, ...)` implementation would pass.

v1.3.1 adds a much stronger test:

- save old settings;
- capture the exact old bytes;
- hold `settings.json` open with `FileShare.None`;
- call `Save(replacement)`;
- require an exception;
- release the lock;
- assert the old file is byte-for-byte unchanged;
- reload and verify old values;
- assert no temp files remain.

Those are good assertions.

### Why it still does not prove the named contract

The test comment says the lock forces failure:

> after the new content is already durably written to the temp file

That is true for the **current implementation**.

But the test itself never observes that boundary.

Consider this regressed implementation:

```csharp
File.WriteAllText(_settingsPath, json);
```

Under the same `FileShare.None` lock:

1. direct `File.WriteAllText` attempts to open the locked destination;
2. it throws before modifying it;
3. old bytes remain exactly intact;
4. reload returns old values;
5. there is no temp file to leave behind.

Every current assertion still passes.

So the new test proves:

> “A failed write while the destination is exclusively locked does not change the old file.”

It does **not** prove:

> “The implementation wrote/flushed a separate temporary file and failed specifically at the promotion boundary.”

That is the exact distinction the prior audit was targeting.

### Why this is `RISK`, not `DEFECT`

The production code currently does the right thing:

1. unique temp path;
2. `FileStream(..., CreateNew, Write, FileShare.None)`;
3. write JSON;
4. `writer.Flush()`;
5. `stream.Flush(true)`;
6. `File.Move(tempPath, settingsPath, overwrite: true)`;
7. cleanup temp on failure.

I cannot demonstrate a current production failure.

The weakness is that the regression test would not catch one important future regression.

### What would confirm/refute the risk

Add a test seam at the exact boundary:

```text
after temp stream is flushed
before File.Move(temp, settingsPath)
```

For example:

```csharp
internal Action<string>? OnAfterSettingsTempFlushedBeforePromote;
```

The test should:

- assert the hook is reached;
- assert the temp file exists at that moment;
- optionally assert it contains the complete new representation;
- throw from the hook or fail the promotion immediately afterward;
- then assert old `settings.json` is byte-for-byte intact;
- assert the temp file is cleaned afterward.

A direct-write implementation cannot satisfy “the temp-flushed boundary was reached,” so this would finally lock down the actual atomic-save mechanism.

---

# 5. `RISK` — coverage-ratchet regression tests are not automated by CI

## R2 — Six good synthetic scenarios exist, but normal CI does not invoke them

**Ratchet implementation:** `scripts/CoverageRatchet.ps1:1-74`  
**Synthetic tests:** `scripts/verify_coverage.ratchet.tests.ps1:16-99`  
**Normal coverage job:** `.github/workflows/ci.yml:119-151`

### Current implementation is correct

`Test-CoverageRatchet` now computes:

```text
baseline uncovered = baseline total - baseline covered
current uncovered  = current total - current covered
```

for:

- lines;
- branches;
- methods.

It fails when current uncovered count increases.

It also explicitly protects the current 100% method-coverage state.

This fixes the concrete v1.3.0 defect.

The committed six synthetic scenarios are also sensible:

1. same covered / higher total → fail;
2. covered rises but uncovered rises → fail;
3. new uncovered method → fail;
4. genuine coverage improvement → pass;
5. deletion of covered code with unchanged uncovered count → pass per declared policy;
6. method coverage drops from 100% → fail.

### Automation gap

Normal `ci.yml` runs:

```text
dotnet test with coverage
→ pwsh scripts/verify_coverage.ps1
```

but it does not run:

```text
pwsh scripts/verify_coverage.ratchet.tests.ps1
```

`verify_coverage.ps1` dot-sources `CoverageRatchet.ps1`, but it does not invoke the synthetic regression test script.

The manually-dispatched zero-exception workflow likewise does not contain a synthetic-ratchet-test phase.

The release notes state that the six scenarios were run successfully during the v1.3.1 verification, which is useful evidence for this release. The issue is future enforcement.

### Concrete fragility

Suppose a future edit accidentally changes:

```powershell
$currentUncoveredBranches -gt $baselineUncoveredBranches
```

back to a comparison of covered counts.

If the repository's real coverage on that particular PR happens to be equal to or better than the current baseline, the normal coverage job can still pass.

The six synthetic negative cases that would immediately expose the regression are present in the repository, but CI does not execute them.

### What would confirm/refute the risk

Add one cheap CI step, for example before the real coverage collection:

```yaml
- name: Coverage ratchet unit scenarios
  shell: pwsh
  run: pwsh scripts/verify_coverage.ratchet.tests.ps1
```

It should also be included in `zero-exception-audit.yml`.

This is a very low-cost improvement because the script needs no application launch and no coverage collection.

---

# 6. Prior audit regression matrix

## Prior `DEFECT` findings

| `a.md` finding | v1.3.1 status | Verification |
|---|---|---|
| DEFECT 1 — startup tests mutate real per-user migration state | **FIXED** | `AppBootstrap.StateDirectoryOverride` and `Program.BaseDirectoryOverride` are present; `ProgramStartupTests` uses workspace-scoped base/state directories and resets seams in `finally`. |
| DEFECT 2 — covered-count-only coverage ratchet | **FIXED** | `Test-CoverageRatchet` now uses uncovered counts and explicit 100% method protection. |

### DEFECT 1 verification in detail

`src/AssetProvenanceHelper/Services/AppBootstrap.cs:52-96` now contains minimal overrides for:

- mutex-name derivation;
- state-directory resolution.

`src/AssetProvenanceHelper/Program.cs:7-33,55-115` now contains:

- `ApplicationConfigurationInitializer`;
- `ApplicationRunProvider`;
- `MessageProvider`;
- `BaseDirectoryOverride`.

`tests/AssetProvenanceHelper.Tests/ProgramStartupTests.cs:43-98` centralizes the startup test seams and resets them.

The integration tests now exercise through `Program.RunApplication()` itself and verify:

- normal startup;
- state-directory creation;
- legacy-file copying;
- existing stable state wins;
- migration marker prevents re-import;
- already-running branch;
- startup exception;
- `Main()` delegation.

The important fix is not merely that `GetStateDirectory()` has a seam; it is that the `Program` integration tests actually install it.

I found no remaining `ProgramStartupTests` path that writes `%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper`.

### Note on mutexes

`ProgramStartupTests` still causes `Program.RunApplication()` to create a real Windows named mutex, but the *name* is replaced by a per-test GUID-based `Local\AssetProvenanceHelperTest_...` name.

That is an intentional, short-lived kernel-object integration and does not touch persistent user state or contend with the production mutex. I do not score it as a defect.

---

## Prior `RISK` findings

| Prior risk | v1.3.1 status | Result |
|---|---|---|
| RISK 1 — Main foreign-root reconciliation branch | **CLOSED** | Targeted exact-state test added, including foreign bytes preserved and metadata reset/fail-closed near-miss behavior. |
| RISK 2 — forward-move race branches | **CLOSED** | Tests now inject source disappearance and destination appearance at the final race boundary. |
| RISK 3 — legacy OLD provenance authority failures | **CLOSED** | Targeted corrupt/unreadable/missing authority tests added with preservation assertions. |
| RISK 4 — replacement commit-forward recovery error arms | **CLOSED** | Startup-recovery tests exercise the actual `FinishReplacementCommit` paths rather than the live replacement path. |
| RISK 5 — Main staging-authority negative paths | **CLOSED** | Staged Main/ingame/provenance corruption cases are independently exercised before canonical promotion. |
| RISK 6 — weak Phase-3 cancellation deletion test | **CLOSED** | Exact survivor state, durable phase, and retry completion are now asserted. |
| RISK 7 — mislabeled save-after-provenance-restore test | **CLOSED** | Exact move/save hooks now force the claimed double-failure boundary and assert durable state. |
| RISK 8 — REG_004 never ran recovery | **CLOSED** | Test now invokes actual `RecoverSessionOnStartup` and checks completed asset survival + session retirement. |
| RISK 9 — atomic settings save test too weak | **IMPROVED, BUT RESIDUAL RISK REMAINS** | Byte-for-byte old-state and cleanup assertions are good, but the failure injection still cannot prove the temp-flushed-before-promotion boundary. See R1. |
| RISK 10 — zero-exception prerequisites fail-fast | **CLOSED** | Checkout/setup/restore and later phases use non-fail-fast handling with `if: always()` and explicit aggregation. |

So, of the ten prior risks:

- **9 are convincingly closed**;
- **1 is materially improved but not fully locked down as a regression test**.

---

# 7. Specific verification of the newly added high-risk transaction tests

The new tests are not merely line-coverage filler. The important additions generally assert state transitions and preservation semantics.

## Main rollback/reconciliation

The added test for the foreign root-Main scenario checks the semantic distinction identified previously:

```text
foreign pre-existing root Main
+ none of this transaction's final/ingame/temp artifacts
→ do not overwrite/delete foreign file
→ reset Main commit metadata
→ retain Reference authority
```

The near-miss cases preserve fail-closed behavior when transaction artifacts do exist.

This is the right level of assertion for a recovery branch.

## Hash-owned forward move race

The new race tests inject mutation after the primitive's earlier validation but before final move authority.

They verify both:

```text
source disappears
```

and:

```text
destination appears
```

without allowing unknown destination overwrite.

This directly closes the earlier gap.

## Legacy OLD provenance authority

The v1.3.1 tests now cover the failure arms rather than only successful legacy hash hydration.

This is particularly important because legacy `ReferenceProvenanceHash == null` is exactly where recovery has to derive byte authority before destructive action.

The current tests maintain the desired rule:

```text
cannot establish exact OLD provenance ownership
→ preserve files/journal
→ do not invent authority
```

## Replacement commit-forward

The new REG_206/207/208 family reaches startup recovery with durable replacement journal/session state rather than testing only the live replacement route.

That matters because `FinishReplacementCommit` is a recovery-only path.

The new tests cover:

- NEW exact validation failure;
- backup cleanup failure;
- journal deletion failure.

This is a meaningful improvement over merely forcing errors somewhere in replacement code.

## Main staging authority

The staged Main, ingame copy, and provenance are now independently corrupted between staging and the authority gate.

Assertions verify no canonical promotion occurs.

This is exactly the right anti-TOCTOU/fail-closed contract.

---

# 8. Test-quality review of prior weak tests

## Cancellation Phase 3

The rewritten test no longer stops at:

```text
Assert.Throws<IOException>
```

It asserts:

- which deterministic temp survives;
- which one is deleted independently;
- durable phase remains `FilesRenamed`;
- a later retry succeeds;
- session/temp cleanup completes.

That is a good state-machine test.

## Save failure after provenance restore

The rewritten test uses:

- `SessionService.OnBeforeCancelFileMoveHook`;
- `SessionService.OnBeforeSaveSessionHook`;

to make the intended sequence deterministic.

It then checks:

- canonical provenance restored;
- temp provenance absent;
- reference image remained canonical because its move failed before mutation;
- fresh persisted session remains at the pre-failure durable phase;
- wrapped/aggregate exception semantics.

This now matches the test's name.

## REG_004 recovery

The test now invokes the real recovery entry point and asserts both halves of the safety contract:

```text
completed canonical asset survives
AND
stale session record is retired
```

This closes the prior false-confidence gap.

## REG_020 atomic save

As described in R1, this is the only one of the four where I would not yet say the test proves its exact named mechanism.

---

# 9. OS-level side-effect audit

The verification request specifically asked for another sweep of tests that can fall through test seams into the real OS.

The relevant production surfaces are:

| Side effect | Production seam/fallback | Current audit result |
|---|---|---|
| Startup MessageBox | `Program.MessageProvider` → real `MessageBox.Show` | Startup tests now seam it. No remaining demonstrated startup dialog leak. |
| MainForm MessageBox | `MainForm.MessageBoxProvider` → real `MessageBox.Show` | Generally seamed on tested error paths; clipboard tests can still reach real fallback if clipboard access throws. |
| Folder browser | `FolderBrowserDialogProvider` → real `FolderBrowserDialog.ShowDialog` | Seamed in automated tests; real wrappers remain narrow coverage exclusions. |
| Manifest file dialog | `OpenFileDialogProvider` / `PickManifestPathWithDialog` | Seamed in queue/import tests. |
| Choose-image dialog | `OpenFileDialogProvider` → real `OpenFileDialog.ShowDialog` | Reviewed UI tests use the provider when invoking file choice. |
| Explorer/process launch | `OpenFolderProvider` → real `Process.Start(...)` | The reported `MainFormUiTests` bug is fixed. No additional concrete process-launch leak found in reviewed callers. |
| Clipboard read/paste | `ClipboardProvider` → real clipboard read | Paste tests use the provider. |
| Clipboard write on queue activation | `ClipboardWriter` → real `Clipboard.SetText` | **DEFECT D1: three tests still reach real clipboard.** |
| Cursor position | cursor-position seam → real cursor | Existing overlay tests use the seam where deterministic position matters. |
| Two-choice modal dialog | `TwoChoiceDialog.CustomChoiceProvider` → real `ShowDialog` | Recovery/UI tests that require a choice use the provider. |
| Per-user app state | `AppBootstrap.StateDirectoryOverride` | Program startup tests now use workspace state. |
| Legacy source directory | `Program.BaseDirectoryOverride` | Program startup migration tests now use workspace state. |
| Test filesystem | `TestWorkspace` | Test-created mutable data is rooted under `%TEMP%\AssetProvenanceHelperTests\<GUID>` in reviewed service/workflow tests. |

### Explorer regression specifically

The previously reported `MainFormUiTests` path is genuinely repaired.

The affected test now installs:

```csharp
MainForm.OpenFolderProvider = path => ...
```

around:

- `OpenFolder`;
- `OpenDownloads`;
- `OpenAssetFolder`;

and resets it in `finally`.

That prevents the old:

```text
test calls Open Downloads
→ Process.Start(explorer.exe)
→ TestWorkspace is deleted
→ Explorer finishes navigating to vanished directory
→ real OS error UI
```

sequence.

I did not find another concrete `Process.Start` leak from the reviewed test callers.

### Clipboard is the remaining concrete sibling

The clipboard defect is especially useful because it shows why side-effect guarding should be centralized rather than repaired test-by-test.

PR #3 fixed:

- one missing `MainForm.MessageBoxProvider`;
- one missing `Program.MessageProvider`;
- one missing `OpenFolderProvider`;

but two tests still accidentally miss `ClipboardWriter`, and a third deliberately bypasses it.

The structural improvement is therefore not just “add three missing assignments”; it is to make the normal UI test fixture **fail closed by default** for all OS-output seams.

---

# 10. Coverage and exclusions

## Current measured coverage

The completed Coverage Gate log reports:

- **Lines:** `8545 / 9319` = **91.69%**
- **Branches:** `2474 / 2881` = **85.87%**
- **Methods:** `463 / 463` = **100.00%**

The committed baseline is:

- lines: `8516 / 9319`
- branches: `2454 / 2881`
- methods: `463 / 463`

Therefore current CI improved uncovered counts relative to the baseline:

- uncovered lines: `803 → 774`
- uncovered branches: `427 → 407`
- uncovered methods: `0 → 0`

I do **not** flag the remaining line/branch gaps as new findings, per the request. The release is not at literal 100% line/branch coverage and does not claim to be.

---

## Coverage ratchet

`CoverageRatchet.ps1` now checks uncovered counts.

For each metric:

```text
baselineUncovered = baselineTotal - baselineCovered
currentUncovered = currentTotal - currentCovered
```

and rejects:

```text
currentUncovered > baselineUncovered
```

For methods, when the baseline has zero uncovered methods, it explicitly prevents any regression from 100%.

This directly closes the prior demonstrated bypass:

```text
2450 / 2875
→ 2450 / 2885
```

because uncovered branches increase from 425 to 435.

I found no equivalent covered-count-only bypass in the current implementation.

The declared code-deletion policy is also explicit, rather than accidental.

---

## Coverage exclusions — exact enumeration

`code-coverage-exclusions.json` still contains exactly **six** methods:

1. `AssetProvenanceHelper.MainForm.BrowseDownloadFolderWithDialog`
2. `AssetProvenanceHelper.MainForm.BrowseAssetRootWithDialog`
3. `AssetProvenanceHelper.MainForm.PickManifestPathWithDialog`
4. `AssetProvenanceHelper.Program.InitializeApplicationConfigurationForReal`
5. `AssetProvenanceHelper.Program.RunApplicationForReal`
6. `AssetProvenanceHelper.Program.ShowMessageBoxForReal`

### Assessment

**6 listed / 6 intended narrow fallback wrappers / 0 demonstrated broader business-logic exclusions.**

The three `Program` exclusions are especially clean:

- `ApplicationConfiguration.Initialize()`;
- `Application.Run(form)`;
- `MessageBox.Show(...)`.

The MainForm dialog methods contain the minimal real modal-dialog construction/show behavior and then feed the selected result into logic that is exercised through the matching providers.

I did not find an exclusion that hides transaction, recovery, validation, persistence, or state-machine logic.

The gate also checks exclusion inventory bidirectionally:

- source exclusion not in allowlist → fail;
- stale allowlist entry with no source exclusion → fail.

The exact completed Coverage Gate passed.

### Conclusion on exclusions

The current exclusion set remains **minimal and honest enough** for the stated policy.

No new exclusion-related `DEFECT` or `RISK` is reported.

---

# 11. CI evidence

Existing completed PR CI run reviewed:

**Run ID:** `33258923845`

Both jobs completed successfully.

## Coverage Gate

All steps succeeded:

- checkout;
- setup;
- restore;
- Release build;
- coverage test run;
- coverage verification;
- artifact upload.

Coverage-run test result:

```text
Passed: 1026
Failed: 0
Skipped: 0
Total: 1026
```

Release build:

```text
0 warnings
0 errors
```

Coverage gate:

```text
passed
```

## Windows Build & Test

All steps succeeded:

- restore;
- Debug build with warnings as errors;
- Debug tests;
- Release build with warnings as errors;
- Release tests;
- `RecoveryCritical`;
- **20× full-suite flakiness loop**;
- publish preparation;
- **win-x64 publish**;
- **published package integrity/process startup smoke**;
- artifact upload.

This closes the three items that were pending at the cutoff of `a.md`.

They are **verified green**, not `NOT VERIFIABLE HERE`.

### CI/tree precision

The PR workflow tested GitHub's synthetic merge commit:

`386c3e7035cb3392385fee26587123e95996c586`

Its tree SHA is:

`8abef6f1ebd7ddc113aa4248f0f24aef3594d6ea`

The final merged PR commit:

`8787969ed729a5a5a6d2be6071e8ef308e38ca1c`

has the **same tree SHA**:

`8abef6f1ebd7ddc113aa4248f0f24aef3594d6ea`

The tag/main commit `11b747e...` subsequently adds only the v1.3.1 release-notes document.

So the completed CI run did test the same production/test tree that was merged for v1.3.1.

---

# 12. `NOT VERIFIABLE HERE`

## N1 — widened mutation testing

Current:

`tests/AssetProvenanceHelper.Tests/stryker-config.json`

mutates:

```json
"mutate": [
  "**/*.cs",
  "!**/MainForm.Designer.cs"
]
```

This is structurally the widened scope requested after `gaa1`.

What is still unknown is the resulting:

- mutation score;
- survivor count;
- survivor locations;
- equivalent/time-out/error counts.

I cannot run Stryker in this environment.

Therefore this remains:

**`NOT VERIFIABLE HERE`**

and is not counted as a failure.

---

## N2 — interactive WinForms behavior

Not independently executable here:

- real modal dialogs;
- DPI and display scaling;
- focus behavior;
- actual drag/drop;
- actual clipboard UX;
- window layout at target resolutions;
- interactive help/prompt overlays as rendered;
- real Explorer navigation.

The code paths and seams can be inspected, but visual/interactive behavior itself requires Windows GUI execution.

This is not a defect.

---

## N3 — real abrupt durability

The persistence and transaction code generally follows strong ordering:

```text
write temp
→ flush
→ durable state/journal
→ rename/promote
→ cleanup
```

and the tests simulate many crash boundaries with hooks.

What cannot be proven statically is the exact behavior of the Windows filesystem under:

- power loss;
- hard process kill;
- storage cache behavior;
- rename persistence at the physical durability boundary.

That remains `NOT VERIFIABLE HERE`.

---

# 13. Production transaction/recovery safety — second-round result

I re-traced the areas previously considered highest-value for data-loss safety:

- `AssetProcessorService.Reference.cs`
- `AssetProcessorService.Main.cs`
- `AssetProcessorService.FileOps.cs`
- `SessionService.cs`
- `MainForm.Recovery.cs`
- `MainForm.MainWorkflow.cs`
- `MainForm.ReferenceWorkflow.cs`

and compared the v1.3.1 tests with the specific gaps from `a.md`.

## Result

**No new concrete production data-loss mechanism was demonstrated.**

The important properties remain intact:

### Initial Reference

- durable transaction/session authority is established before destructive/canonical mutation;
- deterministic staging is used;
- staged bytes and provenance are validated;
- canonical promotion is no-overwrite/fail-closed;
- partial states remain recoverable.

### Reference replacement

The durable phase model remains coherent:

```text
Prepared
→ OldBackupPending
→ OldBackedUp
→ NewPromotionPending
→ NewPromoted
→ SessionSwitchPending
→ SessionSwitched
→ CleanupPending
→ journal removal
```

Recovery uses both journal phase and durable session authority to decide rollback vs commit-forward.

The historical legacy-provenance-hash hydration fix remains present.

### Main

- `PrepareMainCommit` establishes durable authority before Main processing;
- deterministic Main/ingame/provenance staging remains;
- staging authority is rechecked before canonical promotion;
- partial completion is distinguished from fully exact completed output;
- rollback uses verified byte/hash ownership rather than filename-only ownership.

The new negative staging tests strengthen this substantially.

### Cancellation

- cancellation remains phase-based and resumable;
- deterministic temp names remain;
- file ownership is hash-verified;
- partial Phase-3 deletion remains retryable;
- new tests now prove the exact survivor/durable state rather than only exception type.

### Recovery

- unknown/foreign files are generally preserved;
- destructive operations are conditioned on path and byte authority;
- replacement commit-forward error handling is now directly exercised through startup recovery;
- Main foreign-root reconciliation is now directly tested.

Nothing in the v1.3.1 changes weakens these properties.

---

# 14. Healthy mechanisms worth preserving

## 1. Startup state isolation is now correctly designed

Moving persistent user state to LocalAppData was the right production design in v1.3.0.

v1.3.1 correctly fixes the **tests**, not the production state location.

Do not revert the LocalAppData architecture merely to make tests easier.

The new `StateDirectoryOverride`/`BaseDirectoryOverride` seams are the correct solution.

## 2. Coverage gate architecture is substantially better

Keep:

- dynamic production file inventory;
- generated-code filtering;
- exact integer counters;
- method enumeration;
- bidirectional exclusion validation;
- uncovered-count ratchet;
- per-file reporting.

Only R2 remains: run the already-written synthetic ratchet tests automatically.

## 3. Recovery tests increasingly assert authority, not just exceptions

This is the strongest improvement in PR #3.

The new tests generally ask:

```text
which exact bytes survive?
which exact path changes?
what durable phase remains?
does retry complete?
is unknown data preserved?
```

That is much more valuable than increasing coverage by asserting only that an exception occurred.

## 4. Fail-closed file handling remains strong

The production code repeatedly uses the right pattern:

```text
expected path
→ reparse/path safety
→ expected byte/hash authority
→ race boundary
→ re-check authority
→ mutate
```

That is appropriate for a provenance/recovery utility.

## 5. Zero-exception workflow now matches its stated diagnostic purpose better

Prerequisite failures no longer automatically prevent all later phases from attempting to report.

The final aggregator retains visibility across phases.

---

# 15. Recommended remediation order

## Priority 1 — fix D1

Make the normal UI test factory safe by default.

At minimum:

- seam `ClipboardWriter` in the two incidental queue tests;
- remove the real-clipboard test from ordinary suite execution.

Preferably:

- introduce a shared “no real OS side effects” test setup that installs safe/fail-fast providers for clipboard, MessageBox, folder opening, file/folder dialogs, and two-choice dialogs.

This is the best defense against a repeat of the exact bugs already encountered.

## Priority 2 — strengthen REG_020

Add a boundary hook after temp flush and before destination promotion.

Make the test prove that it reached that exact boundary before injecting failure.

## Priority 3 — automate the six ratchet scenarios

Add:

```text
pwsh scripts/verify_coverage.ratchet.tests.ps1
```

to normal CI and the zero-exception workflow.

This is cheap and protects a gate that now carries real policy weight.

## Priority 4 — run widened Stryker when resources allow

The next truly new assurance signal is mutation testing over the expanded scope.

Given the current 100% method coverage but ~91.7% line/~85.9% branch coverage, mutation survivors will be more informative than merely adding low-value lines to chase a round percentage.

---

# 16. Final assessment

## Release quality

The v1.3.1 delta is a **real improvement**, not a superficial test-count increase.

The two previous defects are fixed, the most dangerous transaction/recovery test gaps have been directly addressed, all previously pending normal CI phases are green, and coverage improved while maintaining 100% method coverage.

I do **not** find evidence that the release's production transaction/recovery machinery currently contains a new concrete data-loss bug.

## Remaining findings

The outstanding concrete defect is the suite itself still touching real interactive OS state:

**three Request-queue tests write the real Windows clipboard, and one clears it.**

That should be fixed because it is the same category of test isolation failure that already produced real MessageBox and Explorer incidents during PR #3.

The two remaining risks are narrower:

- the atomic-settings regression test still does not prove the exact temp/promotion boundary it claims;
- the new ratchet's six synthetic regression scenarios are not automated by CI.

### Final scorecard

| Category | Count |
|---|---:|
| `DEFECT` | **1** |
| `RISK` | **2** |
| `NOT VERIFIABLE HERE` | **3** |

**Production data-loss defects demonstrated in this round: 0.**  
**Test-suite OS-side-effect defects demonstrated in this round: 1 root defect, with 3 concrete instances.**


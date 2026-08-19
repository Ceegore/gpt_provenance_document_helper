# AI Asset Provenance Helper — Eighth Paranoid Retest & Repair Guide

**File:** `bugs8.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `e6236fb4102972e1777bb032c6c5375aa45d4dad`  
**Previous audited commit:** `1845bb96f4f443d7c7f5b6f6418039579a5333a5`  
**Previous audit:** `bugs7.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — all headline R7 defects are materially repaired, but three narrower transaction/recovery defects remain.**

The repository is now very close to a clean source-level acceptance.

The latest commit correctly implements the major `bugs7.md` requirements:

- initial Reference uses deterministic transaction staging paths;
- the Reference source is staged before canonical promotion;
- pre-rendered verified provenance is reused rather than re-rendered later;
- `DateTimeOffset.EqualsExact()` is used for Prepared timestamp authority;
- asset/reference reparse state is checked after lazy folder creation;
- staged Reference image/provenance are hash-verified before promotion;
- Prepared recovery understands deterministic initial-Reference temp artifacts;
- one-promoted/one-temp initial Reference state is covered by RecoveryCritical tests;
- Cancel detaches `_currentSession` and switches to Idle immediately after durable `SessionService.Cancel()` completion;
- post-cancel UI work is isolated;
- post-preflight source/template drift tests were added;
- same-instant/different-offset timestamp authority is tested;
- replacement post-commit UI behavior and the prior R7 test gaps were addressed.

Do not redesign the new initial-Reference staging architecture.

The fresh independent audit found three remaining issues:

1. **R8-001 — MEDIUM-HIGH:** transaction provenance staging still calls `WriteTextAtomic()`, which creates a second **random, unjournaled `.__write_<GUID>.tmp` file** inside the asset/reference directory. A hard crash can leave this file behind permanently even after recovery removes the journal and all deterministic transaction artifacts.
2. **R8-002 — MEDIUM:** `FinalizeLiveReplacementRollback()` persists OLD state and deletes the replacement journal, but then performs throwable UI work inside the same call. A post-rollback UI exception can escape back into `HandleReplaceReference()`'s outer transaction catch and invoke `RollbackReferenceReplacement()` a second time.
3. **R8-003 — MEDIUM-LOW:** replacement temp image/provenance are verified when they are created, but `PromoteNewReference()` does not re-verify their hashes/content immediately before canonical promotion, despite a meaningful interval containing journal writes and OLD backup mutation.

These are focused fixes. No broad recovery-state-machine rewrite is recommended.

---

# 0.2 Current repository state

Current `main`:

```text
e6236fb4102972e1777bb032c6c5375aa45d4dad
```

Commit message:

```text
Fix bugs7.md: initial reference staging crash-atomicity,
cancel UI detachment, and test hardening (R7-001..R7-004)
```

Parent:

```text
1845bb96f4f443d7c7f5b6f6418039579a5333a5
```

Changed implementation/test files include:

```text
MainForm.ReferenceWorkflow.cs
MainForm.cs
Models/AssetSession.cs
Services/AssetProcessorService.Reference.cs
Services/AssetProcessorService.cs
Services/ValidationService.Session.cs
Bugs3ParanoidTests.cs
ChangeV11MainFormTests.cs
RegressionTests.cs
```

---

# 0.3 CI / execution evidence

The configured CI remains structurally strong.

The connected GitHub status surface currently returns no legacy status entries for this exact SHA, and the available workflow-run wrapper exposes only PR-triggered runs, not the direct `main` push.

The local analysis container does not have the required Windows/.NET runtime.

Therefore exact Windows execution remains **deferred evidence, not a blocker by itself**.

The FAIL verdict is caused only by the concrete source-level issues below.

---

# 1. `bugs7.md` retest

| R7 ID | Status | Eighth-pass result |
|---|---|---|
| R7-001 initial Reference crash-atomicity | **FIXED at canonical-output level** | deterministic temp image/provenance are staged and verified before promotion |
| R7-001 duplicate provenance render | **FIXED** | preflight now returns the verified provenance and the same string is staged |
| R7-001 timestamp exactness | **FIXED** | uses `EqualsExact()` |
| R7-002 Cancel durable/UI boundary | **FIXED** | current authority is detached immediately after durable Cancel |
| R7-003 post-create reparse hardening | **FIXED** | paths revalidated after folder creation and again before promotion |
| R7-004 test gaps | **SUBSTANTIALLY FIXED** | offset, post-preflight drift, staged recovery, Cancel and replacement UI tests added |

The remaining findings below are not a request to undo those repairs.

---

# 2. Current finding summary

| ID | Severity | Area | Summary |
|---|---|---|---|
| R8-001 | **MEDIUM-HIGH** | provenance staging | deterministic transaction temp is implemented using a second random unjournaled temp file |
| R8-002 | **MEDIUM** | replacement rollback/UI boundary | durable rollback completion can still escape through UI failure and trigger a second rollback |
| R8-003 | **MEDIUM-LOW** | replacement promotion authority | staged replacement bytes are not re-verified immediately before canonical promotion |

---

# 3. R8-001 — MEDIUM-HIGH — `WriteTextAtomic()` defeats deterministic transaction staging

## 3.1 New initial-Reference staging design is correct in principle

Initial Reference now derives deterministic transaction paths from:

```text
ReferenceTransactionId
```

For example:

```text
<asset>/reference/.__reference_<id>.png
<asset>/reference/.__reference_provenance_<id>.tmp
```

That is the correct architecture.

Prepared recovery can derive those paths from durable session authority.

## 3.2 But provenance staging calls `WriteTextAtomic()`

Current initial Reference code does:

```csharp
WriteTextAtomic(
    tempProvenancePath,
    verifiedProvenance);
```

The target path is already a transaction staging path.

However `WriteTextAtomic()` internally creates another temporary path:

```csharp
var tempPath =
    Path.Combine(
        directory,
        $".__write_{Guid.NewGuid():N}.tmp");
```

It then writes to that random path and finally moves it to the deterministic target.

Therefore the actual disk sequence is:

```text
durable Prepared journal
deterministic temp image
random .__write_<unknown GUID>.tmp
move random file -> deterministic temp provenance
```

The random inner path is:

- not stored in the Prepared session;
- not derivable from `ReferenceTransactionId`;
- not validated by `ValidatePreparedReferenceSession()`;
- not checked or removed by `RollbackReference()`;
- not part of replacement-journal validation;
- not part of final documented asset trees.

---

# 4. R8-001A — hard crash leaves permanent hidden asset file

Timeline:

```text
T0 Prepared session.json is durable

T1 deterministic temp image is written and verified

T2 WriteTextAtomic(deterministicTempProvenance, P1)

T3 WriteTextAtomic creates:
   <reference>/.__write_RANDOM.tmp

T4 part/all of P1 is written to random temp

--- HARD CRASH HERE ---
```

Disk:

```text
session.json = Prepared
deterministic temp image = exact H1
deterministic temp provenance = absent
random .__write_RANDOM.tmp = partial/full tool-created data
canonical Reference = absent
canonical provenance = absent
```

Startup:

```text
Prepared exact output fails
RollbackReference() sees deterministic temp image -> deletes exact H1
RollbackReference() sees deterministic temp provenance absent
canonical files absent
reference folder is NOT empty because random .__write_RANDOM.tmp remains
folder cleanup silently leaves non-empty folder
session.json is then deleted
```

Final state:

```text
no active recovery journal
no canonical asset
hidden tool-created random temp remains in asset/reference
```

The application has now lost the authority needed to identify that random file.

This is a real crash-created orphan, not an external-tamper scenario.

---

# 5. R8-001B — same defect exists in Reference replacement

`CreateReplacementTempFiles()` also calls:

```csharp
WriteTextAtomic(
    transaction.TempNewProvenancePath,
    newProvenance);
```

The replacement journal knows only:

```text
TempNewReferencePath
TempNewProvenancePath
BackupReferencePath
BackupProvenancePath
```

It does not know:

```text
.__write_RANDOM.tmp
```

A hard crash during the inner atomic write can therefore leave an unjournaled hidden provenance temp inside a live Reference folder even after replacement recovery succeeds.

---

# 6. Required R8-001 fix

Do not use a nested atomic-write helper when the target itself is already a journaled transaction temp.

Add a helper specifically for writing a **reserved deterministic staging path**:

```csharp
private static void WriteTextDurablyToReservedPath(
    string path,
    string content)
{
    using var stream =
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

    using var writer =
        new StreamWriter(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false));

    writer.Write(content);
    writer.Flush();
    stream.Flush(true);
}
```

Use it for:

```text
initial Reference deterministic temp provenance
replacement deterministic temp provenance
```

The target is already temporary. There is no reason to add a random inner staging layer.

If a hard crash occurs during the direct deterministic-temp write:

```text
the only partial file is at a path derived from durable transaction authority
```

which is exactly what the transaction design needs.

## 6.1 Alternative

If an additional write-then-rename layer is desired, its path must also be deterministic, e.g.:

```text
.__reference_provenance_<id>.writing
.__new_provenance_<id>.writing
```

and it must be:

```text
validated
journal-authoritative
recovered
tested
```

The direct deterministic staging write is simpler.

---

# 7. R8-001 recovery policy for partial deterministic provenance

A hard crash can leave a partial deterministic temp provenance.

Two acceptable policies:

## Conservative

```text
hash != ReferenceProvenanceHash
=> preserve deterministic temp
=> preserve journal
=> fail closed
```

This matches the current unknown-content rule.

## Transaction-reserved cleanup

If the design formally declares the deterministic temp path exclusively owned by the already-durable transaction after collision preflight:

```text
partial deterministic transaction temp
=> safe to delete as incomplete transaction-owned staging output
```

This gives better automatic recovery but requires the ownership rule to be explicit.

Either is superior to an untracked random file.

---

# 8. R8-001 mandatory tests

## 8.1 Structural no-random-temp test

Add a test-only hook to the durable reserved-path writer:

```csharp
[ThreadStatic]
internal static Action<string>?
    OnReservedTextStagingOpenedHook;
```

Invoke after opening/writing begins.

Test:

```text
InitialReference_ProvenanceStaging_UsesOnlyDeterministicTransactionPaths
```

At the hook:

```text
enumerate <asset>/reference
assert no path starts with ".__write_"
assert the open/written path == GetReferenceTempProvenancePath()
```

Then throw to simulate interruption.

## 8.2 Replacement equivalent

```text
Replacement_ProvenanceStaging_UsesOnlyDeterministicTransactionPaths
```

## 8.3 Recovery after deterministic partial provenance

Create:

```text
Prepared journal
deterministic temp image H1
partial deterministic provenance
```

Verify chosen policy explicitly:

```text
fail closed + journal preserved
OR
transaction-temp cleanup + journal removed
```

No random `.__write_*` file may remain.

---

# 9. R8-002 — MEDIUM — durable replacement rollback still performs throwable UI work inside the transaction finalizer

`bugs6.md` fixed the successful replacement Save-New failure path so it no longer deliberately rolled back twice.

However the helper:

```csharp
FinalizeLiveReplacementRollback(tx)
```

still combines:

```text
durable rollback finalization
in-memory/UI refresh
```

---

# 10. Current `FinalizeLiveReplacementRollback()`

Current order:

```text
1. Save OLD session
2. Delete replacement journal
3. _currentSession = OLD
4. lblReference.Text = ...
5. SetSelectedImage(...)
6. _state = ReferenceReady
7. ApplyState()
8. return true
```

Steps 1 and 2 are the durable rollback completion boundary.

After step 2:

```text
OLD files are restored
OLD session.json is authoritative
replacement journal is gone
```

The replacement transaction is finished.

But steps 4–7 can throw.

---

# 11. R8-002 concrete second-rollback path

Example: Save(NewSession) fails.

Current caller path:

```text
RollbackReferenceReplacement(tx)
    -> success

FinalizeLiveReplacementRollback(tx)
    -> Save OLD succeeds
    -> Delete replacement journal succeeds
    -> UI operation throws
```

That exception escapes the nested Save-New catch into `HandleReplaceReference()`'s outer catch.

The outer catch sees:

```csharp
transaction != null
&& !transaction.IsCommitted
```

and executes:

```csharp
RollbackReferenceReplacement(transaction)
```

again.

So a post-durable-rollback UI failure re-enters transaction rollback.

This violates the same boundary rule that was correctly applied to successful Main, Reference and Cancel flows:

> once durable transaction finalization succeeds, later UI errors must never re-enter destructive reconciliation.

---

# 12. Why this matters even if rollback is mostly idempotent

The second rollback often sees the already-restored OLD canonical state and may succeed harmlessly.

But this is still wrong because:

- rollback is invoked more than once;
- external state may change between invocations;
- error messages can claim a journal was preserved when it was already deleted;
- the code can re-run ownership checks/mutations after the transaction has finished;
- the `R5_004 ... RollsBackExactlyOnce` guarantee is not protected against **post-finalization UI failure**.

This is a correctness/state-machine defect, not merely a message wording issue.

---

# 13. Required R8-002 refactor

Split durable rollback finalization from UI.

Example:

```csharp
private bool FinalizeLiveReplacementRollback(
    ReferenceReplacementTransaction tx)
{
    try
    {
        _sessionService.Save(
            tx.OldSession);
    }
    catch (Exception ex)
    {
        ShowError(
            "CRITICAL: Replacement files were rolled back, "
            + "but the OLD session could not be persisted.",
            ex);
        Close();
        return false;
    }

    try
    {
        _sessionService.DeleteReplacementJournal();
    }
    catch (Exception ex)
    {
        ShowError(
            "CRITICAL: OLD state was restored, "
            + "but the replacement journal could not be removed.",
            ex);
        Close();
        return false;
    }

    // Durable rollback commit point.
    _currentSession =
        tx.OldSession;

    _state =
        UiState.ReferenceReady;

    CompleteReplacementRollbackUiAfterDurableCommit(
        tx.OldSession);

    return true;
}
```

Then:

```csharp
private void CompleteReplacementRollbackUiAfterDurableCommit(
    AssetSession oldSession)
{
    try
    {
        OnReplacementRollbackDurableCommitHook?.Invoke();

        lblReference.Text =
            $"Saved reference: {oldSession.ReferenceFilename}";

        SetSelectedImage(
            ImageSlot.Reference,
            null);

        ApplyState();
    }
    catch (Exception uiEx)
    {
        try
        {
            ShowMessageBox(
                "The previous Reference was restored successfully, "
                + "but the interface could not be refreshed."
                + Environment.NewLine
                + Environment.NewLine
                + uiEx.Message,
                "Post-Rollback UI Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
        }

        Close();
    }
}
```

**Critical rule:** the post-durable UI helper must not throw back into the transaction catch.

---

# 14. R8-002 mandatory test

```text
Replacement_SaveNewFailure_PostRollbackUiThrows_RollbackRunsExactlyOnce
```

Setup:

```text
OLD stable Reference
replacement tx
inject Save(NewSession) failure
count RollbackReferenceReplacement calls
inject UI failure after:
    Save OLD
    Delete journal
    _currentSession/state detached to OLD
```

Assert:

```text
rollback count == 1
replacement journal absent
session.json == OLD
OLD Reference exact
OLD provenance exact
NEW canonical absent
form closes or remains safe
```

Also test the same durable rollback helper from:

```text
new-output validation failure
outer replacement failure after transaction exists
```

One shared helper test can be enough if all paths use it.

---

# 15. R8-003 — MEDIUM-LOW — replacement temps are not re-verified immediately before promotion

Current replacement sequence:

```text
CreateReplacementTempFiles()
    verify temp Reference hash
    verify rendered provenance hash
    write temp provenance

save OldBackupPending journal
BackupOldReference()
save OldBackedUp journal
save NewPromotionPending journal

PromoteNewReference()
    validate transaction PATHS
    move temp Reference -> canonical
    move temp provenance -> canonical
    validate canonical output
```

`PromoteNewReference()` does **not** re-check:

```text
TempNewReferencePath hash == NewSession.ReferenceHash
TempNewProvenancePath hash == NewSession.ReferenceProvenanceHash
```

immediately before the moves.

There is a meaningful interval between initial temp verification and promotion.

---

# 16. R8-003 failure scenario

```text
T0 temp Reference H1 + temp provenance P1 verified

T1 OLD canonical moved to backups

T2 NewPromotionPending journal durable

T3 temp Reference externally changes to H2
   or temp provenance changes to P2

T4 PromoteNewReference()
   path validation succeeds
   H2/P2 moved to canonical slots

--- CRASH BEFORE post-move exact validation ---
```

Recovery is conservative and will not delete unknown H2/P2.

That prevents unsafe deletion, but the normal promotion boundary could have rejected the changed staging artifacts **before any new canonical mutation**.

The same source/template-drift philosophy already used elsewhere should apply to staged transaction files.

---

# 17. Required R8-003 fix

At the start of:

```csharp
PromoteNewReference()
```

after path/reparse validation and before either `File.Move`:

```csharp
if (!File.Exists(
        transaction.TempNewReferencePath))
{
    throw new IOException(
        "Replacement temp Reference is missing.");
}

var tempRefHash =
    ComputeSha256(
        transaction.TempNewReferencePath);

if (!string.Equals(
        tempRefHash,
        transaction.NewSession.ReferenceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException(
        "Replacement temp Reference no longer matches "
        + "Prepared ReferenceHash.");
}

if (!File.Exists(
        transaction.TempNewProvenancePath))
{
    throw new IOException(
        "Replacement temp provenance is missing.");
}

var tempProvHash =
    ComputeSha256(
        transaction.TempNewProvenancePath);

if (!string.Equals(
        tempProvHash,
        transaction.NewSession.ReferenceProvenanceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException(
        "Replacement temp provenance no longer matches "
        + "Prepared ReferenceProvenanceHash.");
}
```

Use stored hashes as authority.

Do not re-render the template at promotion time.

Then move both files.

---

# 18. R8-003 test

```text
Replacement_TempTamperedAfterBackup_BeforePromotion_RejectsBeforeCanonicalMutation
```

Steps:

```text
create OLD
create replacement tx
create verified temp files
backup OLD
tamper TempNewReferencePath or TempNewProvenancePath
call PromoteNewReference()
```

Assert:

```text
throws before either NEW canonical destination is created
OLD backups remain available
rollback restores OLD cleanly
```

Use two theories/cases:

```text
tampered temp image
tampered temp provenance
```

Mark:

```csharp
[Trait("Category", "RecoveryCritical")]
```

---

# 19. Fresh checks that passed

The eighth pass did **not** find a new defect in these repaired areas:

```text
[PASS] initial Reference exact timestamp authority
[PASS] initial Reference preflight source hash freeze
[PASS] initial Reference pre-rendered provenance reuse
[PASS] deterministic initial Reference temp path derivation
[PASS] deterministic temp-path parent validation
[PASS] post-create asset/reference reparse validation
[PASS] staged Reference image hash verification
[PASS] staged Reference provenance hash verification
[PASS] initial Reference canonical promotion from verified stage
[PASS] one-promoted/one-temp Prepared startup rollback
[PASS] exact-complete Prepared startup commit-forward
[PASS] foreign canonical Prepared state remains fail-closed
[PASS] Main post-commit UI isolation
[PASS] Reference post-stable-save UI isolation
[PASS] Replacement successful post-commit UI isolation
[PASS] Cancel durable/UI isolation
[PASS] replacement source authority freeze
[PASS] replacement deterministic temp/backup path confinement
[PASS] replacement reparse/path revalidation
[PASS] exact provenance hash-first ownership
[PASS] custom unknown image extensions rejected
[PASS] smoke/coverage/20x CI configuration retained
```

Do not reopen these without new evidence.

---

# 20. Recommended repair order

## Phase 1 — deterministic provenance staging

Fix R8-001 first.

This removes the last unjournaled asset-folder mutation in the new staging design.

Apply the same helper to:

```text
initial Reference
Reference replacement
```

## Phase 2 — replacement rollback durable/UI boundary

Fix R8-002.

This should be small and mirror the already-successful:

```text
CompleteMainUiAfterDurableCommit
CompleteReferenceUiAfterDurableCommit
CompleteCancelUiAfterDurableCommit
```

pattern.

## Phase 3 — pre-promotion temp revalidation

Fix R8-003.

No state-machine redesign required.

---

# 21. Static verification after repair

## No transaction code should call nested random writer

```powershell
rg -n "WriteTextAtomic" `
  src/AssetProvenanceHelper
```

Expected production transaction uses:

```text
0 calls from initial Reference staging
0 calls from replacement staging
```

If `WriteTextAtomic` remains for unrelated non-transaction uses, verify those separately.

---

## No random `.__write_` inside journaled asset staging

```powershell
rg -n "__write_" `
  src/AssetProvenanceHelper
```

If the generic helper remains:

```text
journaled Reference/replacement code must not call it
```

---

## Replacement rollback boundary

```powershell
rg -n "FinalizeLiveReplacementRollback|RollbackReferenceReplacement|DeleteReplacementJournal|ApplyState" `
  src/AssetProvenanceHelper/MainForm.ReferenceWorkflow.cs
```

Manual invariant:

```text
Save OLD
Delete journal
set _currentSession OLD
set state ReferenceReady
---------------- durable boundary
guarded UI only
```

No UI exception may propagate into an outer catch that can invoke rollback.

---

## Replacement promotion authority

```powershell
rg -n "PromoteNewReference|TempNewReferencePath|TempNewProvenancePath|ReferenceProvenanceHash|ReferenceHash" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Required immediately before canonical `File.Move`:

```text
temp ref hash == NewSession.ReferenceHash
temp provenance hash == NewSession.ReferenceProvenanceHash
```

---

# 22. Required execution gate

After repair:

```powershell
dotnet --info
```

Expected SDK:

```text
8.0.418
```

Then:

```powershell
dotnet restore AssetProvenanceHelper.sln
```

## Debug

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Debug `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Debug `
  --no-build
```

## Release

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Release `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Release `
  --no-build
```

## RecoveryCritical

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical" `
  --logger "console;verbosity=detailed"
```

Acceptance:

```text
0 failed
0 skipped RecoveryCritical tests
```

## 20x Release

```powershell
for ($i = 1; $i -le 20; $i++)
{
    dotnet test AssetProvenanceHelper.sln `
      -c Release `
      --no-build `
      --logger "console;verbosity=minimal"

    if ($LASTEXITCODE -ne 0)
    {
        throw "Release flakiness run $i failed."
    }
}
```

Expected:

```text
20/20 PASS
```

## Publish / smoke

```powershell
dotnet publish `
  src/AssetProvenanceHelper/AssetProvenanceHelper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish

pwsh scripts/run_smoke_tests.ps1 `
  -PublishDir artifacts/publish `
  -LogOutputDir artifacts
```

## Coverage

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts/coverage
```

---

# 23. Final Definition of Done

## Transaction staging

```text
[ ] journaled Reference provenance never uses random inner temp
[ ] journaled replacement provenance never uses random inner temp
[ ] all transaction staging paths deterministic from durable authority
[ ] crash cannot leave unidentifiable tool temp inside asset tree
```

## Replacement rollback boundary

```text
[ ] OLD session save completes
[ ] replacement journal delete completes
[ ] in-memory OLD authority detached/set
[ ] post-rollback UI guarded separately
[ ] post-rollback UI exception cannot call rollback again
[ ] rollback count remains exactly 1
```

## Replacement promotion

```text
[ ] temp Reference re-hashed immediately before promotion
[ ] temp provenance re-hashed immediately before promotion
[ ] mismatch rejects before either NEW canonical mutation
```

## Tests

```text
[ ] initial provenance staging deterministic-path test
[ ] replacement provenance staging deterministic-path test
[ ] deterministic partial-provenance recovery test
[ ] post-rollback UI failure exactly-once test
[ ] replacement temp-image tamper-before-promotion test
[ ] replacement temp-provenance tamper-before-promotion test
```

## Execution

```text
[ ] Debug warnings-as-errors PASS
[ ] Debug tests PASS
[ ] Release warnings-as-errors PASS
[ ] Release tests PASS
[ ] RecoveryCritical PASS
[ ] 20/20 Release PASS
[ ] self-contained publish PASS
[ ] smoke PASS
[ ] coverage PASS
```

---

# 24. Final eighth-pass conclusion

The `bugs7.md` repair is successful at the architectural level.

The initial Reference flow now has the correct core model:

```text
durable Prepared authority
-> deterministic staging
-> verify
-> canonical promotion
```

and Cancel now has the correct durable/UI separation.

The remaining defects are smaller:

```text
R8-001:
the deterministic provenance stage is itself implemented through
an untracked random inner temp file.

R8-002:
durable replacement rollback finalization still lets UI exceptions
escape into a transaction catch that can invoke rollback again.

R8-003:
replacement staged bytes are not re-verified immediately before promotion.
```

After these focused fixes, another final paranoid audit is appropriate.

**Current acceptance state: FAIL — three narrow known transaction/recovery defects remain.**

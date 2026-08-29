# AI Asset Provenance Helper — Ninth Paranoid Retest & Repair Guide

**File:** `bugs9.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `452f06621d8014efe050a058422bc40aa98f6870`  
**Previous audited commit:** `e6236fb4102972e1777bb032c6c5375aa45d4dad`  
**Previous audit:** `bugs8.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — all three R8 defects are materially fixed, but the independent fresh pass found one remaining cross-workflow transaction-authority class that is still weaker in Main and initial Reference than in replacement.**

This is the strongest repository revision audited so far.

The `bugs8.md` commit is real and correct in the areas it claims to repair:

- journaled Reference/replacement provenance now writes directly to deterministic reserved transaction paths;
- the random nested `.__write_<GUID>.tmp` problem is removed from those transaction paths;
- replacement rollback durable state is separated from later UI work;
- post-rollback UI failure is tested to keep rollback invocation exactly once;
- replacement temp Reference and provenance hashes are rechecked immediately before canonical promotion;
- tampered replacement staging is rejected before NEW canonical mutation.

Those changes should be retained.

The new audit found a consistency gap:

> **Replacement now has the correct “final staging authority gate immediately before canonical promotion,” while Main and initial Reference still rely on earlier staging checks.**

That leaves a final TOCTOU/crash window in those workflows. Unknown external modifications are still preserved correctly, but the application can unnecessarily promote such modified staging bytes into canonical paths before discovering the mismatch.

The fresh audit also found two low-severity test/UX issues.

No replacement redesign is needed.

---

# 0.2 Current repository state

Current `main`:

```text
452f06621d8014efe050a058422bc40aa98f6870
```

Commit:

```text
Fix bugs8.md: deterministic provenance staging without inner random files,
replacement rollback durable/UI separation, and pre-promotion revalidation
(R8-001..R8-003)
```

Parent:

```text
e6236fb4102972e1777bb032c6c5375aa45d4dad
```

Changed files in this commit:

```text
bugs8.md
src/AssetProvenanceHelper/MainForm.ReferenceWorkflow.cs
src/AssetProvenanceHelper/MainForm.cs
src/AssetProvenanceHelper/Services/AssetProcessorService.FileOps.cs
src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
src/AssetProvenanceHelper/Services/AssetProcessorService.cs
tests/AssetProvenanceHelper.Tests/Bugs3ParanoidTests.cs
```

---

# 0.3 CI / execution evidence

The connected commit-status surface currently exposes:

```text
statuses: []
```

for this SHA.

The available commit workflow-run wrapper also returns:

```text
workflow_runs: []
```

and is limited to PR-triggered runs.

The current analysis environment does not provide the required Windows/.NET execution stack.

As in the established workflow:

> missing exact Windows/.NET execution evidence is **deferred verification, not by itself a blocker**.

The FAIL verdict below is based on current source-level issues.

---

# 1. Full `bugs8.md` retest

| R8 item | Status | Ninth-pass conclusion |
|---|---|---|
| R8-001 random inner provenance staging temp | **FIXED** | transaction staging now uses `WriteTextDurablyToReservedPath()` directly on deterministic temp paths |
| R8-002 replacement rollback post-durable UI re-entry | **FIXED** | durable OLD finalization precedes guarded UI helper; callers stop after close |
| R8-003 replacement temp authority before promotion | **FIXED** | temp image/provenance are re-hashed immediately before NEW canonical moves |

## 1.1 R8-001 details

Current helper:

```csharp
internal static void WriteTextDurablyToReservedPath(
    string path,
    string content)
{
    if (File.Exists(path))
    {
        throw new IOException(
            $"Target staging file already exists: {path}");
    }

    var directory =
        Path.GetDirectoryName(path)
        ?? throw new InvalidOperationException(...);

    Directory.CreateDirectory(directory);

    using var stream =
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

    OnReservedTextStagingOpenedHook?.Invoke(path);

    using var writer =
        new StreamWriter(
            stream,
            new UTF8Encoding(false));

    writer.Write(content);
    writer.Flush();
    stream.Flush(true);
}
```

This has the desired property:

```text
the only transaction provenance staging path is the deterministic
journal-authoritative path itself
```

Initial Reference and replacement both use it.

**R8-001 PASS.**

## 1.2 R8-002 details

Current live rollback finalizer now has this shape:

```text
Save OLD session
Delete replacement journal

---- durable rollback commit point ----

_currentSession = OLD
_state = ReferenceReady

guarded CompleteReplacementRollbackUiAfterDurableCommit()
```

The UI helper catches its own exception and closes.

Call sites test:

```text
!FinalizeLiveReplacementRollback(...)
|| IsDisposed
```

before attempting further UI reporting.

The new RecoveryCritical test injects post-durable rollback UI failure and asserts:

```text
rollback count == 1
replacement journal absent
OLD session restored
OLD files restored
NEW canonical absent
```

**R8-002 PASS.**

## 1.3 R8-003 details

`PromoteNewReference()` now:

```text
validates transaction/path/reparse authority
requires temp Reference exists
hashes temp Reference
requires hash == NewSession.ReferenceHash
requires temp provenance exists
hashes temp provenance
requires hash == NewSession.ReferenceProvenanceHash

ONLY THEN:

move temp Reference -> NEW canonical
move temp provenance -> NEW canonical
```

The new image/provenance tamper theory verifies rejection before NEW canonical mutation.

**R8-003 PASS.**

---

# 2. Current finding summary

| ID | Severity | Area | Summary |
|---|---|---|---|
| R9-001 | **MEDIUM-HIGH** | Main transaction | Main stages and verifies artifacts, but does not re-verify all staged authority immediately before canonical promotion |
| R9-002 | **MEDIUM** | initial Reference | initial Reference similarly lacks a final raw-byte staging authority gate immediately before first canonical move |
| R9-003 | **LOW** | startup status | new `AddStatus()` handle guard drops constructor-time template validation status |
| R9-004 | **LOW** | test assurance | two tests named as UI-failure tests do not actually inject the claimed failure |
| R9-005 | **LOW** | recovery tests | deterministic partial-provenance crash state is not explicitly tested |

---

# 3. R9-001 — MEDIUM-HIGH — Main does not re-verify staged authority immediately before canonical promotion

This is the most important remaining issue.

The replacement flow now uses the strongest pattern:

```text
prepare durable authority
stage
verify stage
do intervening transaction work
FINAL reverify stage
promote canonical
```

Main currently does:

```text
prepare durable authority
stage
verify each stage when created
do later staging/provenance work
promote canonical
validate canonical afterward
```

The missing final gate leaves a TOCTOU window.

---

# 4. Current Main flow

The durable Main session contains:

```text
IsMainCommitting = true
MainTransactionId
MainFilename
MainHash
MainProvenanceHash
MainPrompt
MainProcessedAt
```

`ProcessMainImage()` currently performs:

```text
1. validate active journal/session
2. source hash -> require MainHash
3. source -> deterministic temp Main
4. validate temp Main
5. hash temp Main -> require MainHash
6. temp Main -> deterministic temp ingame
7. hash temp ingame -> require MainHash
8. render final provenance
9. rendered provenance hash -> require MainProvenanceHash
10. write deterministic temp provenance
11. move temp provenance -> canonical final provenance
12. move temp Main -> canonical root Main
13. move temp ingame -> canonical ingame
14. validate complete canonical asset
```

The stage checks are individually correct.

The issue is their timing.

---

# 5. R9-001A — temp Main can change after its check and before promotion

Example:

```text
T0 durable MainHash = H1

T1 temp Main copied
T2 temp Main hash == H1
T3 temp ingame copied
T4 temp ingame hash == H1

T5 provenance rendering/writing begins

T6 external process changes temp Main:
   H1 -> H2

T7 provenance promoted canonical

T8 temp Main H2 promoted canonical root Main

--- HARD CRASH HERE ---
```

Durable journal:

```text
MainHash = H1
```

Disk:

```text
root Main = H2
final provenance = P1
ingame may still be temp H1
```

Startup rollback correctly sees:

```text
root Main != journal MainHash
```

and refuses to delete the unknown H2.

That is good fail-closed behavior.

The avoidable defect is that the application promoted H2 after a previous H1 check instead of rejecting it while it was still only a staging artifact.

---

# 6. R9-001B — temp ingame has the same gap

After:

```csharp
var ingameHash =
    ComputeSha256(tempIngamePath);
```

and:

```text
ingameHash == MainHash
```

the method still renders/writes provenance before the canonical moves.

An external modification can therefore change the temp ingame file after its check.

Later:

```csharp
File.Move(
    tempIngamePath,
    ingameDestination,
    overwrite: false);
```

promotes whatever bytes currently occupy the staging path.

A crash before complete-asset validation can leave canonical ingame content that the durable journal refuses to delete.

---

# 7. R9-001C — Main temp provenance is not raw-hash validated before promotion

Main renders provenance and correctly verifies the rendered in-memory string against:

```text
session.MainProvenanceHash
```

It then writes:

```csharp
using (var stream =
    new FileStream(
        tempProvenancePath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None))
using (var writer =
    new StreamWriter(
        stream,
        new UTF8Encoding(false)))
{
    writer.Write(provenance);
}
```

Then immediately:

```csharp
File.Move(
    tempProvenancePath,
    finalProvenance,
    overwrite: false);
```

Missing before promotion:

```text
ComputeSha256(tempProvenancePath)
==
session.MainProvenanceHash
```

Therefore the durable journal authority is proven for the **intended string**, but not for the **actual bytes currently occupying the staging path immediately before promotion**.

This is weaker than the newly corrected replacement flow.

---

# 8. R9-001D — Main provenance staging is not explicitly durable

The Main provenance writer relies on normal `StreamWriter` / `FileStream` disposal.

It does not execute:

```csharp
stream.Flush(true);
```

The new transaction staging helper already supplies the appropriate primitive:

```csharp
WriteTextDurablyToReservedPath(...)
```

Main's temp provenance path is already deterministic from:

```text
MainTransactionId
```

so Main should use the same helper.

This makes the transaction staging policy uniform:

```text
all journaled provenance staging:
FileMode.CreateNew
UTF-8 no BOM
writer.Flush()
stream.Flush(true)
deterministic reserved path
```

---

# 9. R9-001E — Main needs one final path/reparse gate too

Main checks ingame reparse state after folder creation.

However a meaningful amount of work occurs after that check:

```text
copy/hash temp Main
copy/hash temp ingame
render provenance
write provenance
```

Before first canonical promotion, revalidate:

```text
AssetFolder not reparse
ingame folder not reparse
all canonical/temp parent relationships still exact
```

This aligns Main with the conservative path model already used for Reference/replacement.

---

# 10. Required R9-001 repair

Introduce one explicit final staging-authority gate immediately before the **first canonical File.Move**.

Example:

```csharp
private void RequireMainStagingAuthority(
    AssetSession session,
    string tempMainPath,
    string tempIngamePath,
    string tempProvenancePath)
{
    if (!session.IsMainCommitting)
    {
        throw new InvalidOperationException(
            "No active Main transaction exists.");
    }

    if (string.IsNullOrWhiteSpace(
            session.MainHash))
    {
        throw new InvalidDataException(
            "MainHash is missing.");
    }

    if (string.IsNullOrWhiteSpace(
            session.MainProvenanceHash))
    {
        throw new InvalidDataException(
            "MainProvenanceHash is missing.");
    }

    if (!File.Exists(tempMainPath))
    {
        throw new IOException(
            "Main staging image is missing.");
    }

    var tempMainHash =
        ComputeSha256(tempMainPath);

    if (!string.Equals(
            tempMainHash,
            session.MainHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Main staging image no longer matches "
            + "the durable MainHash.");
    }

    if (!File.Exists(tempIngamePath))
    {
        throw new IOException(
            "Ingame staging image is missing.");
    }

    var tempIngameHash =
        ComputeSha256(tempIngamePath);

    if (!string.Equals(
            tempIngameHash,
            session.MainHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Ingame staging image no longer matches "
            + "the durable MainHash.");
    }

    if (!File.Exists(tempProvenancePath))
    {
        throw new IOException(
            "Main staging provenance is missing.");
    }

    var tempProvHash =
        ComputeSha256(tempProvenancePath);

    if (!string.Equals(
            tempProvHash,
            session.MainProvenanceHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Main staging provenance no longer matches "
            + "the durable MainProvenanceHash.");
    }

    var pathValidation =
        ValidationService
            .ValidateSessionPathsForDestructiveOperation(
                session);

    if (!pathValidation.IsValid)
    {
        throw new InvalidDataException(
            string.Join(
                Environment.NewLine,
                pathValidation.Errors));
    }

    if (ValidationService.IsReparsePoint(
            session.AssetFolder))
    {
        throw new IOException(
            "Asset folder became a reparse point "
            + "before Main promotion.");
    }

    var ingameFolder =
        session.GetIngameFolderPath();

    if (ValidationService.IsReparsePoint(
            ingameFolder))
    {
        throw new IOException(
            "Ingame folder became a reparse point "
            + "before Main promotion.");
    }
}
```

Call it immediately before:

```csharp
File.Move(
    tempProvenancePath,
    finalProvenance,
    overwrite: false);
```

and before **any** canonical Main mutation.

---

# 11. Replace Main provenance writer

Instead of:

```csharp
using (...)
{
    writer.Write(provenance);
}
```

use:

```csharp
WriteTextDurablyToReservedPath(
    tempProvenancePath,
    provenance);

tempProvenanceCreatedByThisCall = true;
```

Then:

```csharp
RequireMainStagingAuthority(
    session,
    tempMainPath,
    tempIngamePath,
    tempProvenancePath);
```

Then canonical promotion.

---

# 12. Add a deterministic final-gate test hook

Useful test hook:

```csharp
[ThreadStatic]
internal static Action<AssetSession>?
    OnBeforeMainStagingAuthorityGate;
```

Place:

```text
after all staging is complete
before RequireMainStagingAuthority()
```

This lets tests mutate a temp artifact at the last possible safe moment.

Do **not** put the hook after the authority gate; tests need to prove the gate rejects changes.

---

# 13. Mandatory R9-001 tests

Mark all:

```csharp
[Trait("Category", "RecoveryCritical")]
```

## 13.1 Temp Main tamper

```text
Main_TempMainTamperedBeforePromotion_RejectsBeforeCanonicalMutation
```

Flow:

```text
prepare + persist Main journal
stage all artifacts
hook mutates GetMainTempImagePath()
final gate runs
throws
```

Assert:

```text
root Main canonical absent
final provenance canonical absent
ingame canonical absent
active journal preserved or safely reconciled by caller
```

## 13.2 Temp ingame tamper

```text
Main_TempIngameTamperedBeforePromotion_RejectsBeforeCanonicalMutation
```

Assert the same.

## 13.3 Temp provenance tamper

```text
Main_TempProvenanceTamperedBeforePromotion_RejectsBeforeCanonicalMutation
```

The modification should be byte-level and produce a different SHA-256.

Assert no canonical outputs.

## 13.4 Reparse before promotion

```text
Main_IngameBecomesReparseBeforePromotion_RejectsBeforeCanonicalMutation
```

Use the existing `FileAttributesProvider` hook.

## 13.5 Run in both workflows

Preferred theory dimension:

```text
ReferenceAssisted
NoReference
```

because both share `ProcessMainImage()`.

At minimum prove:

```text
one ReferenceAssisted tamper case
one NoReference tamper case
```

---

# 14. R9-002 — MEDIUM — initial Reference also lacks a final raw staging gate

Initial Reference is now structurally much safer than in previous revisions.

Current flow:

```text
Prepared authority preflight
stage temp image
hash temp image == ReferenceHash
write deterministic durable temp provenance
text-decode/re-encode hash check
reparse check
move temp image canonical
move temp provenance canonical
exact canonical validation
```

The remaining gap is again:

```text
the checks are not the final operation before promotion
```

---

# 15. R9-002A — temp image can change during provenance staging

Timeline:

```text
T0 temp image H1 verified

T1 provenance staging starts
T2 provenance staging finishes

T3 external process changes temp image:
   H1 -> H2

T4 reparse check passes

T5 File.Move(temp image H2 -> canonical)

--- CRASH ---
```

Prepared journal expects H1.

Startup correctly refuses to delete H2.

Again, this is safe failure handling but avoidable canonical contamination.

---

# 16. R9-002B — provenance check should hash raw file bytes

Current initial Reference provenance stage validation effectively does:

```csharp
File.ReadAllText(tempProvenancePath)
-> UTF8 encode the resulting string
-> SHA-256
```

The durable stored authority is the SHA-256 of exact UTF-8 bytes.

Use:

```csharp
ComputeSha256(
    tempProvenancePath)
```

instead.

Why:

```text
ownership authority is byte-level
```

not:

```text
decoded-text semantic equivalence
```

For example, a UTF-8 BOM can be consumed by text decoding and then omitted on re-encoding.

A byte-modified file should never pass a byte-hash transaction authority gate.

---

# 17. R9-002C — provenance can change after its current check

Current order:

```text
hash provenance
check reparse
move image
move provenance
```

That leaves a small but real interval after provenance verification.

The stronger rule is:

> perform one final image + provenance raw-byte gate as the last non-mutation step before the first canonical promotion.

---

# 18. Required R9-002 fix

After all staging and path/reparse checks:

```csharp
private void RequireInitialReferenceStagingAuthority(
    AssetSession session,
    string tempImagePath,
    string tempProvenancePath)
{
    if (!File.Exists(tempImagePath))
    {
        throw new IOException(
            "Initial Reference staging image is missing.");
    }

    var imageHash =
        ComputeSha256(tempImagePath);

    if (!string.Equals(
            imageHash,
            session.ReferenceHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Initial Reference staging image "
            + "no longer matches Prepared ReferenceHash.");
    }

    if (!File.Exists(tempProvenancePath))
    {
        throw new IOException(
            "Initial Reference staging provenance is missing.");
    }

    var provenanceHash =
        ComputeSha256(tempProvenancePath);

    if (!string.Equals(
            provenanceHash,
            session.ReferenceProvenanceHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Initial Reference staging provenance "
            + "no longer matches Prepared "
            + "ReferenceProvenanceHash.");
    }
}
```

Order:

```text
stage image
stage provenance
validate paths/reparse
FINAL raw stage authority gate
move image canonical
move provenance canonical
exact canonical validation
```

---

# 19. R9-002 tests

Add:

```text
InitialReference_TempImageTamperedImmediatelyBeforePromotion_NoCanonicalMutation
InitialReference_TempProvenanceTamperedImmediatelyBeforePromotion_NoCanonicalMutation
```

Suggested hook:

```csharp
[ThreadStatic]
internal static Action<AssetSession>?
    OnBeforeInitialReferenceStagingAuthorityGate;
```

Test tampering occurs in the hook.

The final authority gate then rejects it.

Also add byte-level provenance case:

```text
prepend UTF-8 BOM to deterministic temp provenance
```

Expected:

```text
raw SHA mismatch
reject before canonical move
```

---

# 20. Why R9-001 and R9-002 are not redundant paranoia

The repository explicitly adopts this safety principle:

```text
Unknown or externally modified files must be preserved.
Fail closed instead of deleting them.
```

That means whenever externally changed staging is promoted canonical, later recovery intentionally refuses cleanup.

Therefore the correct complement is:

```text
re-verify staging authority as late as possible,
immediately before canonical mutation
```

Replacement now does this.

Initial Reference and Main should use the same invariant.

Uniform rule:

```text
ALL workflows:

durable transaction authority
-> deterministic stage
-> prepare all artifacts
-> final path/reparse gate
-> final raw SHA authority gate
-> canonical promotion
-> exact final validation
```

---

# 21. R9-003 — LOW — startup template status is now silently dropped

The R8 patch hardened UI helpers by adding:

```csharp
private void AddStatus(
    string message)
{
    if (IsDisposed
        || !IsHandleCreated
        || txtStatusHistory.IsDisposed)
    {
        return;
    }

    ...
}
```

But the constructor does:

```text
InitializeComponent()
LoadSettingsIntoUi()
WireEvents()
ValidateTemplatesAtStartup()
ApplyState()
Shown += RecoverSessionOnStartup
```

`ValidateTemplatesAtStartup()` calls:

```text
AddStatus("Templates validated.")
```

or:

```text
AddStatus("Template validation failed.")
```

At that point, a normal WinForms form handle usually has not yet been created.

Therefore:

```text
!IsHandleCreated == true
```

and the startup status entry is discarded.

The actual invalid-template message still appears because `ShowValidationError()` proceeds to `ShowMessageBox()`.

This is not a transaction-safety issue.

It is a small status-history regression introduced by the UI hardening.

---

# 22. R9-003 fix options

Preferred:

```text
do not make AddStatus depend on the FORM handle
```

Accessing a WinForms TextBox property before handle creation is normal.

For post-close safety, this is sufficient:

```csharp
if (IsDisposed
    || txtStatusHistory.IsDisposed
    || Disposing)
{
    return;
}
```

Alternatively buffer pre-handle status lines and append them from `OnShown`.

Simplest small-tool solution:

```csharp
private void AddStatus(
    string message)
{
    if (IsDisposed
        || Disposing
        || txtStatusHistory.IsDisposed)
    {
        return;
    }

    ...
}
```

Retain the `IsDisposed` protection added for post-close rollback/UI flows.

---

# 23. R9-003 tests

Add:

```text
Startup_ValidTemplates_StatusContainsTemplatesValidated
```

Test:

```text
construct form
Show()
inspect status history
contains "Templates validated."
```

Also useful:

```text
Startup_InvalidTemplates_StatusContainsValidationFailure
```

The second test may require a deliberately invalid template workspace.

---

# 24. R9-004 — LOW — two tests still overclaim their injected failure

This is a test-quality issue, not a newly proven production bug.

## 24.1 NoReference status failure test

Current test is named:

```text
NoReference_JournalSaved_PostSaveStatusFailure_ContinuesCommit
```

but it does not actually make:

```csharp
AddStatus(
    "No-reference Main session saved.")
```

throw.

It executes a normal successful commit.

Therefore the test name claims failure-path evidence that the body does not provide.

### Fix

Add a narrowly scoped hook:

```csharp
[ThreadStatic]
internal static Action?
    OnNoReferenceJournalSavedBeforeStatus;
```

or a status provider if you prefer.

Inject:

```csharp
throw new InvalidOperationException(
    "Simulated post-journal status failure.");
```

Then assert the Main commit completes and journal is removed.

Or rename the current test to describe normal completion and add a true failure-injection test separately.

---

# 25. R9-004B — replacement post-commit UI test

Current test:

```text
Replacement_PostCommitUiFailure_DoesNotRollbackOrRecreateOldReference
```

does not cause a post-commit UI call to throw.

Its `MessageBoxProvider` merely observes a `"Post-Commit UI Error"` caption if such an error already occurs.

Nothing actually triggers that catch.

### Fix

Add:

```csharp
[ThreadStatic]
internal static Action?
    OnReplacementDurableCommitUiHook;
```

at the start of:

```csharp
CompleteReplacementUiAfterDurableCommit()
```

Test hook throws.

Then assert:

```text
replacement journal absent
NEW stable session persists
NEW image persists
NEW provenance persists
OLD is not restored
form closes safely
```

This mirrors the strong existing tests for:

```text
Main post-commit UI
Reference post-stable UI
Cancel post-commit UI
replacement rollback post-durable UI
```

---

# 26. R9-005 — LOW — deterministic partial-provenance crash state still lacks a direct test

`bugs8.md` explicitly required a test for interruption while writing a deterministic transaction provenance staging path.

Current staging-path tests prove:

```text
the path is deterministic
no .__write_* file appears
successful write completes
```

They do not materialize:

```text
partial deterministic provenance
```

and run startup recovery.

The source currently appears conservatively safe:

```text
partial temp provenance hash != durable expected hash
=> rollback cannot prove ownership
=> preserve file
=> preserve journal
=> fail closed
```

That is an explicitly acceptable policy.

But it should be locked with a test.

---

# 27. R9-005 tests

## Initial Reference

Create:

```text
valid Prepared session.json
valid deterministic temp image H1
partial deterministic temp provenance
no canonical files
```

Run startup recovery.

Expected conservative policy:

```text
session.json still exists
partial temp provenance still exists
no canonical Reference
no canonical provenance
app recovery fails closed
```

Name:

```text
PreparedReference_PartialDeterministicProvenance_PreservesJournalAndFailsClosed
```

## Replacement

Create a valid Prepared replacement journal with:

```text
OLD stable canonical intact
valid TempNewReference
partial TempNewProvenance
```

Run startup recovery.

Expected:

```text
OLD canonical preserved
replacement journal preserved OR safely rolled back only if
partial reserved staging is explicitly defined as transaction-owned
no unknown file deleted
```

Name:

```text
Replacement_PartialDeterministicProvenance_FailsClosedWithoutOldMutation
```

---

# 28. Fresh areas rechecked and currently clean

The ninth pass did not find a new source defect in:

```text
[PASS] replacement deterministic staging paths
[PASS] replacement final pre-promotion image hash gate
[PASS] replacement final pre-promotion provenance hash gate
[PASS] replacement OLD backup ownership
[PASS] replacement rollback exactly-once durable/UI separation
[PASS] replacement successful post-commit durable/UI separation in source
[PASS] initial Prepared source authority preflight
[PASS] initial Prepared provenance pre-render authority
[PASS] exact Reference timestamp authority
[PASS] initial deterministic Reference temp paths
[PASS] initial startup recovery of known deterministic temp states
[PASS] Cancel durable/UI separation
[PASS] Main durable journal before output mutation
[PASS] Main source-vs-journal hash gate
[PASS] Main initial temp image hash check
[PASS] Main temp ingame hash check
[PASS] Main rendered provenance-vs-journal hash check
[PASS] Main rollback exact canonical/temp ownership
[PASS] unknown extensions policy
[PASS] settings compatibility
[PASS] public Project input removal
[PASS] README final directory trees
[PASS] smoke/coverage/20x CI structure
```

The Main items marked PASS are the **existing earlier gates**.

R9-001 asks for an additional late gate, not replacement of those checks.

---

# 29. Recommended repair order

## Phase 1 — Main final staging gate

Implement R9-001 first.

This is the highest-value remaining fix because Main is the final asset commit path.

## Phase 2 — initial Reference final raw gate

Implement R9-002 using the same invariant.

Consider a shared small helper pattern where reasonable, but do not over-abstract.

## Phase 3 — tests and startup status

Fix R9-003/R9-004/R9-005 together.

---

# 30. Static verification after repair

## Main final gate

```powershell
rg -n `
  "RequireMainStagingAuthority|MainProvenanceHash|GetMainTempImagePath|GetMainTempIngamePath|GetMainTempProvenancePath|File\.Move" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs
```

Manual order must be:

```text
stage all
durably write provenance
final hash/path/reparse gate
File.Move canonical
```

No canonical move before the final gate.

---

## Main provenance writer

```powershell
rg -n `
  "WriteTextDurablyToReservedPath|new FileStream|StreamWriter" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs
```

Required:

```text
journaled Main temp provenance uses
WriteTextDurablyToReservedPath
```

---

## Initial Reference final gate

```powershell
rg -n `
  "RequireInitialReferenceStagingAuthority|tempImagePath|tempProvenancePath|ComputeSha256|File\.Move" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Required order:

```text
final raw hash image
final raw hash provenance
then first initial-Reference File.Move
```

---

## Avoid text re-encode ownership checks

For transaction SHA authority:

```powershell
rg -n `
  "ReadAllText\(tempProvenancePath\)|GetBytes\(File\.ReadAllText" `
  src/AssetProvenanceHelper
```

Initial Reference final authority should use:

```text
ComputeSha256(file path)
```

---

# 31. Required Windows execution gate

Once source repairs are complete:

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
  --no-build `
  --logger "trx;LogFileName=debug.trx"
```

## Release

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Release `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Release `
  --no-build `
  --logger "trx;LogFileName=release.trx"
```

## RecoveryCritical

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical" `
  --logger "trx;LogFileName=recovery-critical.trx"
```

Expected:

```text
0 failed
0 skipped RecoveryCritical tests
```

## 20x Release flakiness

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

## Self-contained publish

```powershell
dotnet publish `
  src/AssetProvenanceHelper/AssetProvenanceHelper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish
```

## Smoke

```powershell
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

# 32. Definition of Done for next round

## R8 preservation

```text
[ ] deterministic Reference/replacement provenance staging remains
[ ] no random inner transaction temp returns
[ ] replacement rollback UI boundary remains exactly-once
[ ] replacement final temp hash gate remains
```

## Main

```text
[ ] Main temp provenance written durably to deterministic reserved path
[ ] final temp Main raw hash == MainHash immediately before canonical promotion
[ ] final temp ingame raw hash == MainHash immediately before canonical promotion
[ ] final temp provenance raw hash == MainProvenanceHash immediately before canonical promotion
[ ] final Main path/reparse validation immediately before promotion
[ ] no canonical Main artifact is moved before those checks
```

## Initial Reference

```text
[ ] final temp image raw hash == ReferenceHash immediately before promotion
[ ] final temp provenance raw hash == ReferenceProvenanceHash immediately before promotion
[ ] no text-decode/re-encode SHA authority comparison
[ ] no canonical Reference artifact moves before final stage gate
```

## UI / tests

```text
[ ] constructor template status is not silently dropped
[ ] true NoReference post-journal status failure is injected/tested
[ ] true replacement post-commit UI failure is injected/tested
[ ] partial deterministic provenance crash state is explicitly tested
```

## Execution

```text
[ ] Debug build warnings-as-errors PASS
[ ] Debug tests PASS
[ ] Release build warnings-as-errors PASS
[ ] Release tests PASS
[ ] RecoveryCritical PASS
[ ] 20/20 Release PASS
[ ] win-x64 self-contained publish PASS
[ ] smoke PASS
[ ] coverage PASS
```

---

# 33. Final ninth-pass conclusion

The `bugs8.md` repair succeeded.

The replacement workflow now demonstrates the transaction invariant that should become universal:

```text
durable authority
-> deterministic staging
-> final raw-byte staging check
-> canonical promotion
-> exact final validation
```

Main and initial Reference are already close, but their final staging checks still occur too early.

That is the remaining material source-level gap.

The repository should **not** be accepted as zero-known-defect yet.

**Current acceptance state: FAIL — R8 is fully closed, but R9-001 and R9-002 remain material transaction-authority gaps; R9-003 through R9-005 are low-severity cleanup/test items.**

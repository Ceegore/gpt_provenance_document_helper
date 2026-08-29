# AI Asset Provenance Helper — Fourteenth Paranoid Retest & Repair Guide

**File:** `bugs14.md`  
**Audit date:** 2026-08-20  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `e4e57cfb9bbaa857999746ea21acbb5130b67062`  
**Previous audited commit:** `f651cc1be1ca0e2a9513dbbbe68ba975eb88f660`  
**Previous audit:** `bugs13.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — the R13 repair is materially real, but the independent fresh pass found three source-level blockers and one regression-test coverage defect.**

This revision correctly implements most of `bugs13.md`:

- managed delete helpers now re-check path safety after the deletion race hook;
- managed restore helpers now re-check path safety after the restore race hook;
- empty-directory cleanup now re-checks path safety after its hook;
- Main and Reference rollback callers supply current session path-safety callbacks;
- replacement cleanup/rollback callers supply replacement-transaction path-safety callbacks;
- Cancel now has hash-owned move/delete/restore helpers;
- Cancel helpers re-check path safety after their dedicated race hooks and re-hash immediately before mutation;
- replacement creation now materializes an OLD provenance raw hash for legacy stable sessions;
- recovery authority matching explicitly tolerates old/new schema hash presence differences.

Those changes should be retained.

The fresh pass found:

| ID | Severity | Area | Summary |
|---|---:|---|---|
| **R14-001** | **HIGH** | forward multi-file promotion / backup | rollback/delete/restore is now per-file authoritative, but forward Main/Reference promotions and OLD backup still perform several direct `File.Move` operations from one earlier authority gate; Main has an existing deterministic `OnMainPromotedHook` exactly between two promotions, so staged bytes or path safety can change and `ingame` is still promoted |
| **R14-002** | **HIGH** | legacy provenance hash materialization | legacy exact rendered-text validation and raw SHA-256 materialization are two separate reads; if the provenance changes between them, the second read can “bless” foreign bytes as the new durable hash, after which later hash-only exact validation accepts those foreign bytes |
| **R14-003** | **HIGH** | persisted legacy replacement-journal recovery | old replacement journals may legitimately contain `OldSession.ReferenceProvenanceHash == null`; journal validation accepts this, `TransactionFromJournal()` does not hydrate it, and commit/rollback passes the null value into helpers which `ThrowIfNull` before their catch blocks — potentially after earlier Phase-B mutation |
| **R14-004** | **LOW-MEDIUM** | regression tests | the new legacy test hydrates and saves `tx.OldSession` before startup recovery, so it does not test a real persisted legacy journal with a null OLD provenance hash; it also does not test cleanup-forward from `CleanupPending`, and no direct restore-hook path-race test was found |

The true acceptance blockers are **R14-001, R14-002, and R14-003**.

---

# 0.2 Current repository state

Current `main`:

```text
e4e57cfb9bbaa857999746ea21acbb5130b67062
```

Commit message:

```text
Fix issues in bugs13.md: Add path safety delegates on destructive ops,
legacy provenance hash materialization, and cancellation race hooks
```

Parent:

```text
f651cc1be1ca0e2a9513dbbbe68ba975eb88f660
```

The commit is one revision ahead of the previous audit.

---

# 0.3 CI / execution evidence

Connected GitHub status currently exposes:

```text
statuses: []
workflow_runs: []
```

The current audit environment does not provide the required Windows/.NET execution stack.

Per the established project rule:

```text
missing exact Windows/.NET execution evidence is deferred verification;
it is not by itself a blocker
```

The FAIL verdict is caused entirely by current source-level findings.

---

# 1. `bugs13.md` retest matrix

| R13 item | Fourteenth-pass result |
|---|---|
| R13-001 delete helper path check after hook | **FIXED** |
| R13-001 delete helper fresh byte check | **FIXED** |
| R13-001 restore helper path check after hook | **FIXED** |
| R13-001 restore helper fresh byte check | **FIXED** |
| R13-001 second destination-existence check after restore hook | **FIXED** |
| R13-001 directory helper path recheck after hook | **FIXED** |
| R13-001 Main callers pass safety callback | **FIXED** |
| R13-001 Reference callers pass safety callback | **FIXED** |
| R13-001 replacement callers pass safety callback | **FIXED** |
| R13-002 live legacy stable session raw hash materialization exists | **FIXED structurally, but snapshot binding is incomplete — R14-002** |
| R13-002 replacement OLD transaction carries non-null hash in newly created transactions | **FIXED** |
| R13-002 startup compatibility for hash-presence mismatch | **FIXED baseline** |
| R13-002 pre-R13 replacement-journal recovery | **NOT FIXED — R14-003** |
| R13-003 Cancel move helper | **FIXED** |
| R13-003 Cancel delete helper | **FIXED** |
| R13-003 Cancel restore helper | **FIXED** |
| R13-003 path gate after Cancel mutation hook | **FIXED** |
| R13-003 fresh SHA at Cancel mutation boundary | **FIXED** |
| required real legacy null-journal test | **MISSING / false assurance — R14-004** |

---

# 2. R13-001 retest — PASS for rollback/destructive helpers

Current delete helper now enforces:

```text
file exists
race hook
current path safety
file still exists
fresh SHA-256
expected hash comparison
File.Delete
```

Current restore helper now enforces:

```text
backup exists
destination absent
race hook
current path safety
backup still exists
destination still absent
fresh SHA-256
expected hash comparison
File.Move
```

Current empty-directory helper now enforces:

```text
directory exists
empty
race hook
current path safety
directory still exists
still empty
not reparse
Directory.Delete
```

This is materially the architecture requested in `bugs13.md`.

Main, Reference, replacement cleanup, and replacement rollback now pass appropriate path-safety delegates into the managed helpers.

**R13-001 PASS for those paths.**

The fresh R14-001 issue is separate: the **forward promotion** code still uses direct multi-step `File.Move` sequences instead of the newly hardened per-file authority model.

---

# 3. R13-003 retest — PASS

Cancel now contains explicit:

```text
OnBeforeCancelFileMoveHook
OnBeforeCancelFileDeleteHook
OnBeforeCancelRestoreHook
```

and the corresponding helpers enforce:

```text
hook
EnsureCancelPathsAreSafe
fresh existence/collision state
fresh SHA
File.Move / File.Delete
```

This closes the direct Cancel mutation-boundary defect from R13.

The new reparse tests also exercise:

```text
move hook
delete hook
restore hook
```

at least at the path-safety level.

**R13-003 PASS structurally.**

A separate legacy snapshot issue still applies when Cancel has to derive a raw provenance hash from a legacy null-hash session. That is included in R14-002.

---

# 4. R14-001 — HIGH — forward multi-file commit still lacks per-file final authority

The previous rounds hardened rollback and cleanup into per-file authority operations.

The forward commit paths did not receive the same treatment.

This now creates an asymmetry:

```text
rollback:
    hook
    path authority
    byte authority
    mutation

forward commit:
    one aggregate staging gate
    move file A
    hook / time / external activity
    move file B
    move file C
```

That is no longer sufficient given the repository's explicit race/fail-closed model.

---

# 5. R14-001A — deterministic Main proof already exists in production code

Current Main flow:

```text
OnBeforeMainStagingAuthorityGate
RequireMainStagingAuthority

File.Move(temp provenance -> final provenance)

File.Move(temp Main -> root Main)

OnMainPromotedHook

File.Move(temp ingame -> canonical ingame)

OnIngamePromotedHook

ValidateCompleteAsset
```

This is a deterministic test boundary.

`OnMainPromotedHook` runs **after** the aggregate staging/path authority gate and **before** `ingame` canonical promotion.

There is no:

```text
path re-check
temp ingame hash re-check
destination collision re-check
```

between the hook and the `File.Move`.

---

# 6. Deterministic R14-001A byte-authority failure

Test:

```csharp
AssetProcessorService.OnMainPromotedHook =
    _ =>
    {
        File.WriteAllBytes(
            session.GetMainTempIngamePath(),
            foreignImageBytes);
    };
```

Sequence:

```text
T0 RequireMainStagingAuthority proves:
   temp Main == MainHash
   temp ingame == MainHash
   temp provenance == expected hash
   paths safe

T1 provenance promoted

T2 root Main promoted

T3 OnMainPromotedHook changes temp ingame
   MainHash -> FOREIGN_HASH

T4 direct File.Move(
      tempIngame,
      ingameDestination)

T5 foreign bytes become canonical ingame output

T6 ValidateCompleteAsset detects mismatch

T7 rollback begins
```

Rollback then correctly refuses to delete the foreign canonical ingame file because the new R13 helper sees:

```text
actual hash != MainHash
```

So the repository preserves the unknown file.

That is good rollback behavior, but it does **not** erase the forward-commit defect:

```text
unknown externally modified staging bytes were promoted
to a canonical production path
```

The transaction is left incomplete and requires manual inspection.

---

# 7. Deterministic R14-001B path-authority failure

Use the same existing hook:

```csharp
AssetProcessorService.OnMainPromotedHook =
    _ =>
    {
        ValidationService.FileAttributesProvider =
            path =>
            {
                if (ValidationService.PathsEqual(
                        path,
                        session.GetIngameFolderPath()))
                {
                    return
                        FileAttributes.Directory
                        | FileAttributes.ReparsePoint;
                }

                return File.GetAttributes(path);
            };
    };
```

After the hook the safety abstraction says:

```text
ingame hierarchy is unsafe
```

But there is no call to the validator before:

```csharp
File.Move(
    tempIngamePath,
    ingameDestination,
    overwrite: false);
```

So the canonical move still occurs.

The later complete-asset validation may fail, but the write has already happened.

This violates the intended rule:

```text
reject unsafe reparse-point destination directories
```

at the actual forward mutation boundary.

---

# 8. R14-001C — final provenance and root Main have the same architectural weakness

The current aggregate Main staging gate happens before:

```text
provenance move
root Main move
ingame move
```

Only one of those transitions currently has an explicit inter-step hook, but all three are independent filesystem mutations.

A robust transaction should not rely on:

```text
all three sources and all destination parents stayed unchanged
since one earlier aggregate check
```

Each canonical promotion should have its own:

```text
current path authority
current source byte authority
current destination collision check
move
```

---

# 9. R14-001D — initial Reference uses the same multi-file pattern

Current initial Reference promotion:

```text
RequireInitialReferenceStagingAuthority

File.Move(
    tempImagePath,
    referenceDestination)

File.Move(
    tempProvenancePath,
    referenceProvenance)
```

The helper proves both files and paths before the first move.

There is no second per-file gate before moving the provenance.

If the temp provenance or Reference hierarchy changes after the image move but before the provenance move, stale authority is used.

---

# 10. R14-001E — replacement promotion uses the same multi-file pattern

Current replacement promotion:

```text
hash temp Reference
hash temp provenance

OnBeforeReplacementFinalPathGate

RequireSafeReferenceReplacementTransaction

File.Move(
    TempNewReferencePath,
    canonical Reference)

File.Move(
    TempNewProvenancePath,
    canonical provenance)
```

The path gate is final only for the **first** move.

It is not final for the second move.

---

# 11. R14-001F — OLD backup uses the same multi-file pattern

Current OLD backup:

```text
final OLD image SHA
final OLD provenance exact ownership

File.Move(
    OLD image,
    backup image)

File.Move(
    OLD provenance,
    backup provenance)
```

Again, the second mutation is based on authority captured before the first mutation.

If the OLD provenance changes between the two moves, unknown provenance can be moved into the transaction backup slot.

Rollback later may refuse it, but the OLD canonical file has already been destructively handled.

---

# 12. Required R14-001 architecture

Do not add another aggregate gate.

Create one reusable **hash-owned move/promotion primitive** and use it for every managed forward `File.Move`.

Example:

```csharp
private void MoveHashOwnedFileWithoutOverwrite(
    string sourcePath,
    string destinationPath,
    string expectedHash,
    string description,
    Func<ValidationResult> validatePathSafety)
{
    ArgumentNullException.ThrowIfNull(sourcePath);
    ArgumentNullException.ThrowIfNull(destinationPath);
    ArgumentNullException.ThrowIfNull(expectedHash);
    ArgumentNullException.ThrowIfNull(description);
    ArgumentNullException.ThrowIfNull(validatePathSafety);

    if (!File.Exists(sourcePath))
    {
        throw new IOException(
            $"{description} source is missing: {sourcePath}");
    }

    if (File.Exists(destinationPath))
    {
        throw new IOException(
            $"{description} destination already exists: "
            + destinationPath);
    }

    OnBeforeHashOwnedMoveHook?.Invoke(
        sourcePath,
        destinationPath);

    var pathSafety =
        validatePathSafety();

    if (!pathSafety.IsValid)
    {
        throw new InvalidDataException(
            $"{description} path safety changed before move: "
            + string.Join("; ", pathSafety.Errors));
    }

    if (!File.Exists(sourcePath))
    {
        throw new IOException(
            $"{description} source disappeared before move.");
    }

    if (File.Exists(destinationPath))
    {
        throw new IOException(
            $"{description} destination appeared before move.");
    }

    var actualHash =
        ComputeSha256(sourcePath);

    if (!string.Equals(
            actualHash,
            expectedHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            $"{description} source changed before move.");
    }

    File.Move(
        sourcePath,
        destinationPath,
        overwrite: false);
}
```

---

# 13. Required R14-001 Main conversion

Replace:

```text
File.Move temp provenance -> final provenance
File.Move temp Main -> root Main
File.Move temp ingame -> ingame
```

with three calls to the same helper.

Expected hashes:

```text
temp provenance:
    session.MainProvenanceHash

temp Main:
    session.MainHash

temp ingame:
    session.MainHash
```

Path callback:

```csharp
() =>
    ValidateSessionDestructivePathSafety(
        session)
```

This means that if `OnMainPromotedHook` changes temp ingame bytes or the ingame hierarchy, the next helper refuses promotion.

---

# 14. Existing Main hook compatibility

Keep:

```csharp
OnMainPromotedHook
OnIngamePromotedHook
```

for regression tests.

The important change is that the next file's helper must re-establish authority after any preceding hook.

Suggested order:

```text
move final provenance safely

move root Main safely

OnMainPromotedHook

move ingame safely

OnIngamePromotedHook

final complete validation
```

This makes the existing hook a genuine test of the next move's boundary.

---

# 15. Required R14-001 initial Reference conversion

Use the same move helper:

```text
temp Reference
-> canonical Reference
expected ReferenceHash

temp provenance
-> canonical provenance
expected ReferenceProvenanceHash
```

Path callback:

```csharp
() =>
    ValidateSessionDestructivePathSafety(
        session)
```

Add an inter-step hook or use the generic move hook.

---

# 16. Required R14-001 replacement conversion

Use the move helper for:

```text
TempNewReferencePath
-> NewSession.ReferenceDestinationPath

TempNewProvenancePath
-> NewSession.ReferenceProvenancePath
```

Expected hashes:

```text
NewSession.ReferenceHash
NewSession.ReferenceProvenanceHash
```

Path callback:

```csharp
() =>
    _validationService
        .ValidateReferenceReplacementTransaction(
            transaction)
```

---

# 17. Required R14-001 OLD backup conversion

Use the same per-file helper for:

```text
OLD canonical Reference
-> BackupReferencePath

OLD canonical provenance
-> BackupProvenancePath
```

Expected hashes:

```text
OldSession.ReferenceHash
OldSession.ReferenceProvenanceHash
```

After R14-002/R14-003, the OLD provenance hash must always be a proven raw hash before this mutator begins.

---

# 18. Mandatory R14-001 tests

All:

```csharp
[Trait("Category", "RecoveryCritical")]
```

## Existing deterministic Main byte boundary

```text
ProcessMain_OnMainPromotedHook_TempIngameChanges_NoCanonicalIngamePromotion
```

Assert:

```text
hook invoked
ProcessMainImage fails
canonical ingame was never created
foreign temp ingame remains/preserved according to rollback authority
journal remains active if rollback incomplete
```

---

## Existing deterministic Main path boundary

```text
ProcessMain_OnMainPromotedHook_IngameBecomesReparse_NoCanonicalIngamePromotion
```

At the hook:

```text
FileAttributesProvider reports ingame as ReparsePoint
```

Assert:

```text
ingame canonical destination does not exist
operation fails closed
```

---

## Generic forward move byte test

```text
MoveHashOwnedFile_SourceChangesAtHook_NoMove
```

---

## Generic forward move path test

```text
MoveHashOwnedFile_PathBecomesReparseAtHook_NoMove
```

---

## Initial Reference second-artifact test

```text
ProcessReference_ProvenanceChangesBeforeItsMove_NoCanonicalProvenance
```

---

## Replacement second-artifact test

```text
PromoteNewReference_ProvenanceChangesBeforeItsMove_NoCanonicalProvenance
```

---

## OLD backup second-artifact test

```text
BackupOldReference_ProvenanceChangesBeforeItsMove_NoBackupProvenance
```

The already-moved image may remain in deterministic backup form for recovery; journal state must remain reconcilable.

---

# 19. R14-002 — HIGH — legacy raw hash materialization is not bound to the exact text snapshot that was validated

This is the most subtle fresh issue.

The R13 design requirement was correct:

```text
legacy exact rendered-text validation
-> materialize raw SHA-256
-> use raw SHA for all destructive operations
```

The current implementation performs the first two steps with separate reads separated by unrelated work.

That leaves an authority gap.

---

# 20. Current replacement creation sequence

Current code first runs:

```csharp
var exactOld =
    _validationService
        .ValidateExactReferenceOutput(
            oldSession,
            _templateService);
```

For a legacy session with:

```text
ReferenceProvenanceHash == null
```

this eventually performs exact rendered-text comparison.

Then the method continues with:

```text
new image validation
new paths
collision checks
transaction id
new source SHA
new provenance render
new provenance SHA
```

Only later does it do:

```csharp
var oldProvHash =
    !string.IsNullOrWhiteSpace(
        oldSession.ReferenceProvenanceHash)
        ? oldSession.ReferenceProvenanceHash
        : ComputeSha256(
            oldSession.ReferenceProvenancePath);
```

Those are two independent observations of the file.

---

# 21. Deterministic conceptual R14-002 failure

Initial state:

```text
legacy provenance raw bytes H1
decoded text = EXACT_EXPECTED_TEXT
ReferenceProvenanceHash = null
```

Sequence:

```text
T0 ValidateExactReferenceOutput reads H1
T1 legacy text comparison passes

T2 external process replaces provenance with H2
   decoded text != expected

T3 ComputeSha256 reads H2

T4 oldSessionAuthority.ReferenceProvenanceHash = H2
```

Now the transaction has converted:

```text
foreign H2
```

into:

```text
authoritative OLD provenance SHA
```

---

# 22. Why later validation does not repair R14-002

After hash materialization, `OldSession.ReferenceProvenanceHash` is non-null.

Current `ValidateExactReferenceProvenanceOwnership()` then switches behavior:

```text
if hash exists:
    SHA-256 equality only

else:
    legacy rendered-text fallback
```

Therefore later `BackupOldReference()` exact provenance validation sees:

```text
actual raw hash H2
stored raw hash H2
```

and accepts it.

The original semantic proof:

```text
decoded text was exact
```

has been disconnected from the raw hash which is now trusted.

---

# 23. Why this violates the authority

The authoritative safety rule requires:

```text
A provenance file may only be destructively handled
after exact rendered-text ownership verification.
```

For legacy sessions, H2 was **never** exact-rendered-text verified.

Only H1 was.

Yet H2 can become the durable raw authority and later be moved/backed up/restored.

---

# 24. R14-002 also affects legacy Cancel materialization

Current Cancel has the same pattern:

```text
ValidateExactReferenceProvenanceOwnership(
    legacy null-hash session)

if expectedProvHash is null:
    expectedProvHash =
        ComputeSha256(
            provenance path)

MoveHashOwnedCancelFile(...)
```

If the file changes between:

```text
exact rendered-text validation
```

and:

```text
raw hash capture
```

the move helper will faithfully verify the newly captured foreign hash and then move it.

Phase 3 may later detect semantic mismatch, but the unknown provenance has already been destructively moved from its canonical location.

---

# 25. Required R14-002 model — one snapshot, two proofs

For legacy provenance, derive:

```text
exact rendered-text ownership
raw SHA-256 authority
```

from the **same byte snapshot**.

Do not:

```text
read as text
close file
later read again for hash
```

---

# 26. Recommended helper

Introduce a helper with semantics similar to:

```csharp
private string GetVerifiedReferenceProvenanceRawHash(
    AssetSession session,
    string path)
{
    if (!File.Exists(path))
    {
        throw new IOException(
            $"Reference provenance does not exist: {path}");
    }

    var raw =
        File.ReadAllBytes(path);

    var actualText =
        DecodeUtf8LikeExistingReader(raw);

    var generationDate =
        session.ReferenceProcessedAt
            .ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);

    var expectedText =
        _templateService.RenderReference(
            session.ReferenceFilename,
            session.ProjectName,
            generationDate);

    if (!string.Equals(
            actualText,
            expectedText,
            StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "Legacy Reference provenance does not exactly "
            + "match expected tool-generated provenance.");
    }

    return Convert
        .ToHexString(
            SHA256.HashData(raw))
        .ToLowerInvariant();
}
```

The exact decoding implementation should preserve current legacy behavior, including BOM compatibility if that is intentionally supported.

---

# 27. Better ValidationService API

Prefer centralizing the semantics rather than duplicating template logic:

```csharp
public ValidationResult
    TryGetExactReferenceProvenanceRawHash(
        AssetSession session,
        string provenancePath,
        TemplateService templateService,
        out string? verifiedRawHash)
```

Contract:

```text
modern session with stored hash:
    verify actual raw hash == stored hash
    return stored/actual hash

legacy session:
    read one raw snapshot
    decode exact expected text from that snapshot
    hash the same raw snapshot
    return hash
```

Callers must use only the returned hash.

---

# 28. Snapshot semantics after helper returns

The file may still change after the snapshot.

That is okay **if** the next actual move/delete/restore uses the R14-001/R13 per-file helper:

```text
snapshot exact text + raw hash H1
...
per-file move boundary:
    path safe
    fresh source SHA must still equal H1
    move
```

If the file changed to H2 after the snapshot:

```text
fresh move-boundary SHA != H1
-> fail closed
```

This correctly binds semantic ownership to the bytes actually moved.

---

# 29. Required R14-002 replacement creation change

Replace:

```csharp
var oldProvHash =
    oldSession.ReferenceProvenanceHash
    ?? ComputeSha256(
        oldSession.ReferenceProvenancePath);
```

with:

```text
modern stored hash:
    verify current exact raw authority

legacy null hash:
    obtain exact-text + raw-hash from one snapshot
```

Then populate transaction OLD authority from that verified hash.

---

# 30. Required R14-002 Cancel change

Whenever:

```text
session.ReferenceProvenanceHash == null
```

Cancel must obtain:

```text
verified raw provenance hash
```

from the same exact-text snapshot helper.

Do this for:

```text
canonical provenance
cancel temp provenance
restore authority
delete authority
```

Do not compute an unqualified raw hash after a prior separate text validation.

---

# 31. Mandatory R14-002 tests

Add a deterministic materialization race hook:

```csharp
[ThreadStatic]
internal static Action<string>?
    OnAfterLegacyProvenanceTextVerifiedBeforeHashMaterialization;
```

or place the hook inside a testable snapshot helper before returning the verified raw hash.

The strongest architecture avoids a two-read boundary entirely, so the preferred test is:

```text
legacy helper returns hash of the same byte[] whose decoded text was validated
```

Still add end-to-end tests.

---

## Replacement creation race

```text
LegacyReplacement_ProvenanceChangesBetweenSemanticProofAndMaterialization_NotBlessed
```

Expected:

```text
foreign bytes never become OldSession.ReferenceProvenanceHash authority
replacement fails before journal/mutation
```

---

## Cancel race

```text
LegacyCancel_ProvenanceChangesBeforeRawAuthority_NoMove
```

Expected:

```text
canonical provenance remains
cancel temp provenance absent
session remains recoverable
```

---

## BOM/raw-byte compatibility

```text
LegacyProvenance_ExactTextWithBom_MaterializesActualRawHash
```

Expected:

```text
semantic exactness passes if legacy behavior supports BOM
returned hash == SHA-256 of actual raw BOM-containing file
subsequent boundary hash check succeeds only for those same bytes
```

---

# 32. R14-003 — HIGH — old persisted replacement journals can still carry null OLD provenance hash and cannot safely recover

R13 fixed **newly created** replacement transactions.

It did not migrate/reconcile already persisted replacement journals from older versions.

That matters because replacement journals are specifically durable crash-recovery state.

---

# 33. Why an old journal can legitimately have a null OLD hash

`AssetSession.ReferenceProvenanceHash` is nullable and older sessions may omit it.

Before the R13 fix, replacement transaction creation preserved the original OLD session authority.

Therefore a valid pre-R13 replacement journal can contain:

```text
OldSession.ReferenceProvenanceHash = null
```

while still containing exact old provenance bytes on disk.

This is exactly the kind of additive/default-compatible persisted state the application should recover safely.

---

# 34. Current journal validator accepts the state

`ValidateReferenceReplacementJournal()` validates:

```text
phase
transaction id
OldSession/NewSession path structure
workflow mode
asset root/folder/project relationships
deterministic backup/temp paths
reparse safety
```

It does **not** require:

```text
OldSession.ReferenceProvenanceHash != null
```

So a pre-R13 journal reaches recovery.

That is reasonable for compatibility — but the transaction must then be hydrated safely before mutation.

---

# 35. Current `TransactionFromJournal()` does not hydrate it

Current recovery reconstructs:

```csharp
return new ReferenceReplacementTransaction
{
    TransactionId = journal.TransactionId,
    OldSession = journal.OldSession,
    NewSession = journal.NewSession,
    BackupReferencePath = journal.BackupReferencePath,
    BackupProvenancePath = journal.BackupProvenancePath,
    TempNewReferencePath = journal.TempNewReferencePath,
    TempNewProvenancePath = journal.TempNewProvenancePath
};
```

No raw OLD provenance hash is materialized.

---

# 36. Current commit-forward failure

`CommitReferenceReplacement()` eventually calls:

```csharp
TryDeleteHashOwnedFileWithError(
    transaction.BackupProvenancePath,
    transaction.OldSession.ReferenceProvenanceHash!,
    ...);
```

For an old journal:

```text
expectedHash == null
```

The null-forgiving operator:

```csharp
!
```

does nothing at runtime.

---

# 37. Helper behavior makes this an exception, not a clean ValidationResult

Current helper begins with:

```csharp
ArgumentNullException.ThrowIfNull(
    expectedHash);
```

and this occurs before its internal `try`.

Therefore:

```text
null OLD provenance hash
-> ArgumentNullException
```

rather than:

```text
ValidationResult.Failure
```

This can escape the cleanup call unexpectedly.

---

# 38. Current rollback failure can occur after earlier Phase-B mutation

Rollback is worse.

Phase A can accept legacy OLD provenance because:

```text
ReferenceProvenanceHash == null
```

activates legacy rendered-text validation.

Then Phase B may:

```text
delete current NEW provenance
```

using a valid NEW/current hash.

After that it attempts:

```csharp
TryRestoreHashOwnedFileWithError(
    BackupProvenancePath,
    OldCanonicalProvenancePath,
    OldSession.ReferenceProvenanceHash!,
    ...)
```

The null hash throws before the helper's catch block.

Possible result:

```text
NEW canonical provenance already deleted
OLD backup provenance still exists
replacement journal still exists
recovery aborted
```

On the next startup the same null authority remains and recovery can repeat the failure.

This is a durable recovery deadlock requiring manual intervention.

---

# 39. Phase examples affected by R14-003

At minimum:

```text
Prepared
OldBackupPending
OldBackedUp
NewPromotionPending
NewPromoted
SessionSwitchPending
SessionSwitched
CleanupPending
```

must all be considered.

The OLD provenance may be located at different paths depending on phase:

```text
canonical OLD provenance
backup OLD provenance
```

Recovery materialization must therefore be phase/state aware.

---

# 40. Required R14-003 architecture

Before any replacement journal is handed to a mutator:

```text
if OldSession.ReferenceProvenanceHash is missing:
    locate the authoritative OLD provenance candidate
    prove legacy exact rendered-text ownership from one byte snapshot
    derive raw SHA from that same snapshot
    assign hash to in-memory journal/transaction OLD authority
```

Then optionally persist the upgraded journal before mutation.

---

# 41. Preferred phase-aware provenance candidate selection

Use fail-closed rules.

## Early phases

For:

```text
Prepared
OldBackupPending
```

prefer:

```text
OldSession.ReferenceProvenancePath
```

because OLD canonical should still exist.

---

## Post-backup phases

For:

```text
OldBackedUp
NewPromotionPending
NewPromoted
SessionSwitchPending
SessionSwitched
CleanupPending
```

prefer:

```text
BackupProvenancePath
```

when it exists.

If both canonical and backup exist:

```text
validate the phase's allowed partial state
do not arbitrarily choose one
```

If neither exists:

```text
fail closed
```

---

# 42. Safer implementation location

A robust approach is to make transaction methods self-defensive:

```csharp
private ValidationResult
    EnsureOldProvenanceByteAuthority(
        ReferenceReplacementTransaction tx)
```

Call it at the beginning of:

```text
BackupOldReference
CommitReferenceReplacement
RollbackReferenceReplacement
```

For journal recovery, also call it before any phase mutation.

This means even a direct internal call with a legacy/null OLD hash is safe.

---

# 43. Persist upgraded authority when recovering a journal

After successfully deriving the raw OLD hash from exact legacy provenance:

```text
journal.OldSession.ReferenceProvenanceHash = verifiedHash
```

Prefer durably saving the upgraded journal before the first subsequent filesystem mutation.

That provides the same write-ahead authority principle used elsewhere.

If journal save fails:

```text
do not mutate asset files
preserve original journal
fail closed
```

---

# 44. Mandatory R14-003 tests

These must load serialized legacy state with the hash **missing/null**.

Do not construct a modern transaction and then save its already hydrated `OldSession`.

---

## OldBackedUp rollback recovery

```text
LegacyReplacementJournal_OldBackedUp_NullOldProvHash_RollsBack
```

Arrange:

```text
journal OldSession.ReferenceProvenanceHash = null
backup old image exists
backup old provenance exact legacy text exists
canonical old files absent
```

Run startup recovery.

Expected:

```text
OLD files restored
OLD provenance raw hash safely materialized
journal removed after complete rollback
no exception
```

---

## NewPromotionPending rollback recovery

```text
LegacyReplacementJournal_NewPromotionPending_NullOldProvHash_RollsBack
```

---

## NewPromoted with OLD durable session

```text
LegacyReplacementJournal_NewPromoted_OldDurableNullHash_RollsBack
```

This exercises the null-vs-hydrated authority match.

---

## CleanupPending commit-forward

```text
LegacyReplacementJournal_CleanupPending_NullOldProvHash_CommitsForward
```

Arrange:

```text
NewSession is durable
NEW canonical output exact
OLD backup provenance exact legacy text
OLD hash missing in journal
```

Expected:

```text
backup image deleted
backup provenance deleted
journal removed
NEW session remains
```

---

## Corrupted OLD backup

```text
LegacyReplacementJournal_NullOldProvHash_CorruptBackup_FailsClosed
```

Expected:

```text
no backup deletion
no restore
journal remains
```

---

# 45. R14-004 — LOW-MEDIUM — current regression tests do not actually cover the most important legacy state

Current test:

```text
ReferenceReplacement_LegacySessionNullProvHash_CommittedAndRecovered
```

does begin with:

```text
oldSession.ReferenceProvenanceHash = null
```

That part is useful.

However it then creates a **new** transaction.

The new R13 code immediately materializes:

```text
tx.OldSession.ReferenceProvenanceHash != null
```

The test explicitly asserts that.

Then it saves:

```csharp
sessionService.Save(
    tx.OldSession);
```

So startup recovery receives a **hydrated modern OLD session**, not the original legacy null-hash durable session.

The journal is also generated from the hydrated transaction.

Therefore the test cannot detect R14-003.

---

# 46. The legacy test name is stronger than its coverage

The name says:

```text
CommittedAndRecovered
```

but the setup uses:

```text
ReferenceReplacementPhase.NewPromotionPending
```

and startup recovery rolls the transaction back to OLD.

It does not exercise:

```text
SessionSwitched
CleanupPending
commit-forward cleanup
```

for a legacy OLD provenance hash.

Add explicit tests rather than expanding the existing name further.

---

# 47. Missing direct restore-helper path test

The production restore helper was correctly updated to run path validation after:

```text
OnBeforeRestoreFileHook
```

but the audit did not find a direct RecoveryCritical test using that hook.

Add:

```text
ReplacementRollback_OnBeforeRestoreFileHook_ReferenceFolderBecomesReparse_NoRestore
```

Assert:

```text
hook invoked
backup remains
canonical OLD destination remains absent
rollback invalid
```

This guards the specific R13-001 repair.

---

# 48. Missing Cancel byte-race tests

Current new Cancel tests exercise reparse/path changes.

Also add byte changes:

```text
Cancel_OnBeforeCancelFileMoveHook_ProvenanceBytesChange_NoMove
Cancel_OnBeforeCancelFileMoveHook_ReferenceBytesChange_NoMove
Cancel_OnBeforeCancelFileDeleteHook_TempBytesChange_NoDelete
Cancel_OnBeforeCancelRestoreHook_TempProvenanceBytesChange_NoRestore
```

These should be straightforward because the source helpers already perform fresh SHA checks.

---

# 49. Fresh clean areas verified this pass

No additional defect was found in:

```text
[PASS] hash-owned delete helper post-hook path validation
[PASS] hash-owned delete helper post-hook SHA verification
[PASS] hash-owned restore helper post-hook path validation
[PASS] hash-owned restore helper post-hook SHA verification
[PASS] restore destination re-check after hook
[PASS] empty-directory post-hook path validation
[PASS] empty-directory second emptiness check
[PASS] Main rollback path callbacks
[PASS] Reference rollback path callbacks
[PASS] replacement cleanup path callbacks
[PASS] replacement rollback delete path callbacks
[PASS] replacement rollback restore path callbacks
[PASS] Cancel move path authority after hook
[PASS] Cancel move fresh SHA
[PASS] Cancel delete path authority after hook
[PASS] Cancel delete fresh SHA
[PASS] Cancel restore path authority after hook
[PASS] Cancel restore fresh SHA
[PASS] full hierarchy Cancel folder-cleanup gate
[PASS] current modern provenance raw hash semantics
[PASS] no reintroduction of decoded-text equality for modern destructive helpers
[PASS] destination overwrite remains false for managed moves
[PASS] source image selection is still copied, not moved
```

---

# 50. Recommended implementation order

## Phase 1 — R14-002 snapshot-bound legacy authority

Implement:

```text
exact semantic provenance + raw SHA
from one byte snapshot
```

Use it in:

```text
replacement creation
Cancel legacy authority
journal recovery hydration
```

Do this first because R14-003 depends on a trustworthy materialization primitive.

---

## Phase 2 — R14-003 journal hydration

Before any legacy replacement-journal mutation:

```text
hydrate missing OLD raw provenance hash safely
persist upgraded journal if possible
```

Add full phase matrix tests.

---

## Phase 3 — R14-001 per-file forward move primitive

Convert managed forward operations:

```text
Main provenance promotion
Main root image promotion
Main ingame promotion

initial Reference image promotion
initial Reference provenance promotion

replacement OLD image backup
replacement OLD provenance backup

replacement NEW image promotion
replacement NEW provenance promotion
```

to one hash-owned/path-safe move helper.

---

## Phase 4 — R14-004 regression completeness

Add the missing:

```text
legacy null journal
cleanup-forward
restore-hook
Cancel byte-race
```

tests.

---

# 51. Static verification after repair

## Find all managed direct file moves

```powershell
rg -n `
  "File\.Move\(" `
  src/AssetProvenanceHelper
```

Manual classification:

```text
Allowed:
    settings/session/journal atomic file replacement where
    asset-path ownership semantics do not apply

Must use managed hash/path move primitive:
    Main canonical asset moves
    Reference canonical asset moves
    replacement backup moves
    replacement promotion moves
    Cancel managed asset moves
```

---

# 52. No old-provenance null-forgiving use at destructive boundary

```powershell
rg -n `
  "OldSession\.ReferenceProvenanceHash!" `
  src/AssetProvenanceHelper
```

Expected after repair:

```text
zero unsafe uses
```

If any remain, there must be an immediately preceding invariant proving non-null transaction byte authority.

---

# 53. Legacy raw hash derivation

```powershell
rg -n `
  "ReferenceProvenanceHash.*ComputeSha256|ComputeSha256.*ReferenceProvenance" `
  src/AssetProvenanceHelper
```

No legacy hash should be obtained from:

```text
a second unqualified read
after an earlier text validation
```

It must come from the same byte snapshot whose decoded content was proven exact.

---

# 54. Forward authority hook coverage

```powershell
rg -n `
  "OnMainPromotedHook|OnIngamePromotedHook|OnBeforeHashOwnedMoveHook" `
  src tests
```

Required:

```text
late byte change tests
late reparse/path change tests
hookInvoked assertions
```

---

# 55. Replacement journal compatibility tests

```powershell
rg -n `
  "LegacyReplacementJournal|CleanupPending.*NullOldProvHash|OldBackedUp.*NullOldProvHash" `
  tests
```

Require explicit tests for rollback and commit-forward.

---

# 56. Required Windows execution gate after repair

When an appropriate Windows/.NET environment is available:

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

---

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

---

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

---

## RecoveryCritical

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical" `
  --logger "console;verbosity=detailed"
```

Expected:

```text
0 failed
0 skipped RecoveryCritical
```

---

## 20× Release

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

---

## Publish

```powershell
dotnet publish `
  src/AssetProvenanceHelper/AssetProvenanceHelper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish
```

---

## Smoke

```powershell
pwsh scripts/run_smoke_tests.ps1 `
  -PublishDir artifacts/publish `
  -LogOutputDir artifacts
```

---

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

# 57. Definition of Done for the next audit

## Preserve R13 repairs

```text
[ ] delete helper still validates path after hook
[ ] delete helper still validates SHA after hook
[ ] restore helper still validates path after hook
[ ] restore helper still validates SHA after hook
[ ] restore destination is rechecked after hook
[ ] directory cleanup still revalidates path after hook
[ ] Cancel move/delete/restore helpers remain
[ ] Cancel helpers retain path + byte authority
```

---

## R14-001

```text
[ ] one reusable managed hash-owned move helper exists
[ ] Main provenance uses it
[ ] Main root image uses it
[ ] Main ingame uses it
[ ] initial Reference image uses it
[ ] initial Reference provenance uses it
[ ] OLD backup image uses it
[ ] OLD backup provenance uses it
[ ] replacement NEW image uses it
[ ] replacement NEW provenance uses it
[ ] OnMainPromotedHook temp-ingame mutation cannot promote foreign bytes
[ ] OnMainPromotedHook reparse change blocks ingame move
```

---

## R14-002

```text
[ ] legacy text exactness and raw hash come from one byte snapshot
[ ] replacement creation uses snapshot-bound raw hash
[ ] Cancel legacy authority uses snapshot-bound raw hash
[ ] no second unqualified raw-hash read can bless changed bytes
[ ] BOM/raw-byte compatibility is explicitly tested
```

---

## R14-003

```text
[ ] old replacement journal with null OLD provenance hash is accepted safely
[ ] OLD hash is hydrated before any mutation
[ ] hydration uses exact legacy semantic proof
[ ] hydrated authority is persisted before mutation where practical
[ ] OldBackedUp recovery works
[ ] NewPromotionPending recovery works
[ ] NewPromoted/old durable recovery works
[ ] CleanupPending commit-forward works
[ ] corrupted legacy OLD backup fails closed
[ ] no helper receives null expectedHash
```

---

## R14-004

```text
[ ] real serialized null-hash journal tests exist
[ ] cleanup-forward legacy test exists
[ ] restore-hook reparse test exists
[ ] Cancel byte-mutation tests exist
[ ] every race test asserts its hook was reached
```

---

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

# 58. Final fourteenth-pass conclusion

The R13 repair is **not superficial**. It substantially improves destructive mutation safety.

The remaining defects are now concentrated in two architectural seams:

```text
1. forward multi-file commit still uses stale aggregate authority;
2. legacy semantic provenance authority is converted to raw hash
   without binding both proofs to the same byte snapshot.
```

Those two seams combine in persisted replacement recovery, where a pre-R13 journal can still carry a null OLD provenance hash and reach helpers that require a non-null hash.

The converged architecture should be:

```text
LEGACY PROVENANCE:
    one byte snapshot
    -> exact rendered-text proof
    -> raw SHA authority

EVERY MANAGED FILE MOVE / DELETE / RESTORE:
    race hook
    -> current full path authority
    -> current exact raw byte authority
    -> mutation

PERSISTED TRANSACTION RECOVERY:
    hydrate missing byte authority safely
    -> persist upgraded authority
    -> mutate
```

Once those rules are applied uniformly to both forward and rollback directions, the transaction model should finally stop reopening the same race class at adjacent boundaries.

**Current acceptance state: FAIL — R14-001, R14-002, and R14-003 remain source-level blockers.**

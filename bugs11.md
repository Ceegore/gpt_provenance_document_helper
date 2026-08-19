# AI Asset Provenance Helper — Eleventh Paranoid Retest & Repair Guide

**File:** `bugs11.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `6c0fc0a2f3d4be86cb1dbc9cdf70499275990bac`  
**Previous audited commit:** `859482fd08e0fdec5c6cd99fcc20ce181f042a5b`  
**Previous audit:** `bugs10.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — all R10 findings are materially repaired, but the fresh independent pass found two remaining destructive-safety classes and one test-assurance gap.**

This is the strongest repository revision audited so far.

The `bugs10.md` repair itself is real and should be retained:

- Main local exception cleanup now revalidates path/reparse safety before deleting anything.
- Initial Reference local exception cleanup does the same.
- Unsafe local rollback now returns fail-closed instead of deleting through a reparse hierarchy.
- Main and initial Reference final staging authority gates are present.
- Replacement now performs a second final transaction/path gate after staging hashes.
- Main R9 tests now assert the actual root Main, actual `ingame/<AssetName>.<ext>`, and actual final provenance paths.
- Zero-delete tests for unsafe Main/Reference local rollback were added.

The fresh pass found:

| ID | Severity | Area | Summary |
|---|---:|---|---|
| **R11-001** | **HIGH** | local provenance rollback ownership | Main and initial Reference local cleanup still use decoded text equality rather than the persisted provenance SHA-256, so byte-modified provenance can be misclassified as tool-owned and deleted |
| **R11-002** | **HIGH** | persisted rollback/cancel destructive safety | persisted rollback/cleanup/cancel methods validate reparse/path safety too early, then perform lengthy verification before destructive mutation without one final path gate |
| **R11-003** | **LOW** | test assurance | the new replacement “after final hash” reparse test does not actually inject the reparse change after the hash work |

The source-level blockers are R11-001 and R11-002.

---

# 0.2 Current repository state

Current `main`:

```text
6c0fc0a2f3d4be86cb1dbc9cdf70499275990bac
```

Commit message:

```text
Fix all issues from bugs10.md (R10-001 - R10-003)
```

Parent:

```text
859482fd08e0fdec5c6cd99fcc20ce181f042a5b
```

The commit is one change ahead of the previous audited state.

---

# 0.3 CI / execution evidence

The connected GitHub status surface currently returns:

```text
statuses: []
```

for the audited SHA.

The available workflow-run wrapper also returns:

```text
workflow_runs: []
```

and does not expose a direct push-to-main run here.

The current execution environment still does not provide the required Windows/.NET toolchain.

Per the established acceptance rule for this project:

> missing exact local Windows/.NET execution evidence is a deferred limitation, not a blocker by itself.

The FAIL verdict below is based on current source-level defects.

---

# 1. `bugs10.md` retest

| R10 item | Result |
|---|---|
| R10-001 Main local rollback path gate | **FIXED materially** |
| R10-001 initial Reference local rollback path gate | **FIXED materially** |
| R10-001 zero-delete fail-closed tests | **FIXED baseline** |
| R10-002 initial Reference final path/reparse gate after hashes | **FIXED** |
| R10-002 replacement second final path/reparse gate after hashes | **FIXED** |
| R10-003 Main test canonical paths | **FIXED** |

---

# 2. R10-001 retest details — PASS

Current Main local catch begins with:

```text
ValidateSessionPathsForDestructiveOperation(session)
AssetFolder reparse check
IngameFolder reparse check
```

If unsafe, it immediately throws:

```text
AssetProcessingException
RollbackComplete = false
```

before the local cleanup list is created.

That is the requested fail-closed boundary.

Current initial Reference local catch similarly performs:

```text
ValidateSessionPathsForDestructiveOperation(session)
AssetFolder reparse check
ReferenceFolder reparse check
```

and aborts automatic rollback before any local delete if unsafe.

The new RecoveryCritical tests count file/directory delete attempts and require:

```text
deleteFileCount == 0
deleteDirectoryCount == 0
journal remains
staging remains
canonical outputs absent
```

This materially closes R10-001.

---

# 3. R10-002 retest details — PASS in source

## Initial Reference

`RequireInitialReferenceStagingAuthority()` now performs:

```text
temp image exists
temp image raw SHA == ReferenceHash

temp provenance exists
temp provenance raw SHA == ReferenceProvenanceHash

ValidateSessionPathsForDestructiveOperation(session)

AssetFolder not reparse
ReferenceFolder not reparse
```

Only then do the canonical moves begin.

This is the correct requested ordering:

```text
final hashes
-> final path/reparse gate
-> first canonical File.Move
```

## Replacement

`PromoteNewReference()` now performs:

```text
RequireSafeReferenceReplacementTransaction

temp Reference SHA == NewSession.ReferenceHash
temp provenance SHA == NewSession.ReferenceProvenanceHash

RequireSafeReferenceReplacementTransaction AGAIN

File.Move NEW Reference canonical
File.Move NEW provenance canonical
```

This materially closes R10-002.

---

# 4. R10-003 retest details — PASS

The Main final-gate tests now use:

```csharp
Path.Combine(
    session.AssetFolder,
    session.MainFilename!)
```

for root Main,

```csharp
session.GetIngameImagePath()
```

for canonical ingame,

and:

```csharp
Path.Combine(
    session.AssetFolder,
    AppConstants.FinalProvenanceFileName)
```

for final provenance.

The previous false-positive paths:

```text
ingame/main.png
main.md
```

are gone from those assertions.

---

# 5. R11-001 — HIGH — local provenance rollback uses text equality instead of the durable SHA-256 authority

This is the strongest fresh finding.

The application now intentionally stores and uses exact provenance hashes:

```text
ReferenceProvenanceHash
MainProvenanceHash
```

The exact validators correctly use those hashes first.

For example:

```text
ValidateExactReferenceProvenanceOwnership
-> ComputeSha256(file)
-> compare ReferenceProvenanceHash
```

and:

```text
ValidateExactFinalProvenanceOwnership
-> ComputeSha256(file)
-> compare MainProvenanceHash
```

That is the correct ownership model.

However the local exception cleanup still uses:

```csharp
TryVerifyTextFileOwnership(...)
```

for provenance deletion.

That helper reads the file as text and compares the decoded string.

This is weaker than the transaction authority and can delete byte-modified external content.

---

# 6. Current weak helper

Current helper:

```csharp
private static bool TryVerifyTextFileOwnership(
    string path,
    string expectedContent)
{
    try
    {
        if (!File.Exists(path))
            return false;

        var currentContent =
            File.ReadAllText(
                path,
                new UTF8Encoding(false));

        return string.Equals(
            currentContent,
            expectedContent,
            StringComparison.Ordinal);
    }
    catch
    {
        return false;
    }
}
```

This proves only:

```text
decoded text == expected decoded text
```

It does **not** prove:

```text
file bytes == transaction-owned bytes
```

---

# 7. Concrete byte-equivalent modification

A simple example is adding a UTF-8 BOM to the beginning of a provenance file.

Original transaction-owned bytes:

```text
EF? no
<UTF-8 body>
```

Modified bytes:

```text
EF BB BF
<same UTF-8 body>
```

The SHA-256 changes.

The final transaction hash gate correctly sees:

```text
actual SHA != persisted provenance hash
```

and rejects the file as modified.

But `File.ReadAllText(...)` may decode the BOM away as an encoding marker and return the same text.

Therefore:

```text
raw hash says "modified / unknown"
text helper says "ours"
```

and the local catch can delete the modified file.

The same class also applies to a differently encoded text file that decodes to the same string.

The application has already chosen byte-exact SHA authority, so local rollback must use the same authority.

---

# 8. R11-001A — initial Reference temp provenance

Current sequence:

```text
WriteTextDurablyToReservedPath(temp provenance)
hook/external process changes provenance bytes
RequireInitialReferenceStagingAuthority
-> raw SHA mismatch
-> throws
```

That part is correct.

Then the local catch executes:

```csharp
if (tempProvenanceWritten
    && File.Exists(tempProvenancePath))
{
    if (TryVerifyTextFileOwnership(
            tempProvenancePath,
            verifiedProvenance))
    {
        TryDeleteFileWithError(...);
    }
}
```

This can delete a byte-modified temp provenance whose decoded text still equals `verifiedProvenance`.

That violates:

```text
Unknown or externally modified files must be preserved.
Fail closed instead of deleting them.
```

---

# 9. R11-001B — initial Reference canonical provenance

The same helper is used when:

```text
provenancePromoted == true
```

If the canonical provenance is externally modified after promotion but before final exact validation completes, the final exact validator rejects it.

The catch then uses decoded text equality and can delete it anyway.

Again:

```text
exact validator rejects byte authority
local cleanup downgrades to semantic text equality
```

The ownership models disagree.

---

# 10. R11-001C — Main temp provenance

Main has the same problem:

```csharp
if (tempProvenanceCreatedByThisCall)
{
    if (File.Exists(tempProvenancePath))
    {
        if (provenance is not null
            && TryVerifyTextFileOwnership(
                tempProvenancePath,
                provenance))
        {
            TryDeleteFileWithError(...);
        }
    }
}
```

A BOM-only byte modification can be rejected by the new raw final SHA gate and then deleted by the local catch.

This specifically weakens the R9/R10 provenance hardening.

---

# 11. R11-001D — Main canonical final provenance

Main canonical provenance is promoted before root Main and ingame.

If later work or final validation fails after an external byte-only modification, the catch executes:

```csharp
if (provenanceWritten)
{
    if (provenance is not null
        && TryVerifyTextFileOwnership(
            finalProvenance,
            provenance))
    {
        TryDeleteFileWithError(...);
    }
}
```

This can erase the externally modified canonical provenance.

---

# 12. Required R11-001 fix

For all current v1.1 transactions, delete provenance only after verifying the persisted hash authority.

## Initial Reference

Replace:

```csharp
TryVerifyTextFileOwnership(
    tempProvenancePath,
    verifiedProvenance)
```

with:

```csharp
TryVerifyFileHashOwnership(
    tempProvenancePath,
    session.ReferenceProvenanceHash)
```

Replace canonical provenance cleanup similarly:

```csharp
TryVerifyFileHashOwnership(
    referenceProvenance,
    session.ReferenceProvenanceHash)
```

## Main

Replace temp provenance cleanup with:

```csharp
TryVerifyFileHashOwnership(
    tempProvenancePath,
    session.MainProvenanceHash!)
```

Replace canonical final provenance cleanup with:

```csharp
TryVerifyFileHashOwnership(
    finalProvenance,
    session.MainProvenanceHash!)
```

The active Main transaction already requires `MainProvenanceHash` before promotion.

---

# 13. Legacy handling

Do not weaken legacy compatibility globally.

If `TryVerifyTextFileOwnership` remains necessary for a true legacy path that has no persisted hash, keep it only behind an explicit legacy fallback.

Example principle:

```text
new transaction with hash:
    hash ownership only

legacy persisted state with no hash:
    exact rendered-text fallback where explicitly required
```

Do **not** let current journaled v1.1 transactions silently fall back to decoded text comparison.

Preferred cleanup:

```text
remove TryVerifyTextFileOwnership from Main/Reference transaction catches
```

If no production use remains, delete the helper.

---

# 14. R11-001 mandatory tests

Mark these:

```csharp
[Trait("Category", "RecoveryCritical")]
```

## Test 1

```text
InitialReference_TempProvenanceBomModified_PreservesUnknownTemp
```

Flow:

```text
Prepared session durable
temp image staged
temp provenance staged
hook prepends UTF-8 BOM
raw final SHA gate rejects
```

Assert:

```text
canonical Reference absent
canonical provenance absent
modified temp provenance STILL EXISTS
Prepared journal still exists when exercised through MainForm
no local provenance delete occurred
```

Use the existing delete-attempt hook if useful.

## Test 2

```text
Main_TempProvenanceBomModified_PreservesUnknownTemp
```

Assert:

```text
root Main absent
ingame canonical absent
final provenance absent
modified temp provenance still exists
active Main journal remains if rollback is incomplete
```

## Test 3

```text
Main_FinalProvenanceBomModifiedAfterPromotion_PreservesCanonical
```

Use an existing post-promotion hook, for example after root Main promotion:

```text
OnMainPromotedHook
```

At the hook:

```text
prepend BOM to canonical final provenance
```

Final validation must fail.

Assert:

```text
modified final provenance still exists
tool does not delete it
journal remains
rollback reported incomplete/fail-closed
```

## Test 4

Optional but recommended:

```text
Reference_CanonicalProvenanceByteModifiedAfterPromotion_PreservesCanonical
```

Add a narrow test hook after provenance promotion/before final validation if needed.

---

# 15. R11-002 — HIGH — persisted destructive rollback/cancel methods still lack a final path/reparse gate immediately before mutation

R10 correctly fixed the **local mutator catch** safety gate.

The fresh pass checked the persisted recovery APIs themselves.

Several still use this shape:

```text
path safety validation
-> lengthy ownership/hash verification
-> destructive mutation
```

without one final path/reparse validation after verification and immediately before mutation.

This recreates the same race class that R10 fixed elsewhere.

The issue affects at least:

```text
RollbackMain
RollbackReference
RollbackReferenceReplacement
CommitReferenceReplacement / CleanupReplacementBackups
SessionService.Cancel
```

---

# 16. Why this matters

A path-safe check is not permanently authoritative.

Example:

```text
T0 path is normal directory
T1 ValidateSessionPathsForDestructiveOperation == valid
T2 hash several image/provenance files
T3 another process moves/replaces destination directory with junction
T4 code begins File.Delete / File.Move using the same string paths
```

At T4 the path may resolve somewhere different from what was validated at T1.

The application explicitly models reparse races, so a final mutation gate should occur after expensive verification work.

This is the same principle now correctly used for Main/Reference promotion.

---

# 17. R11-002A — RollbackMain

Current RollbackMain performs:

```text
ValidateSessionPathsForDestructiveOperation(session)
metadata validation
hash canonical Main
hash ingame
exact final provenance ownership
hash temp Main
hash temp ingame
exact temp provenance ownership
check ingame reparse
DELETE files
```

It does not rerun the full destructive-path validator after all of those reads.

It rechecks only:

```text
ingame folder reparse
```

immediately before deletion.

Missing final checks include at least:

```text
AssetRootFolder reparse
AssetFolder reparse
full confinement relationships
```

A late asset-folder reparse can therefore escape the first check.

## Required fix

Immediately before:

```csharp
var errors = new List<string>();
```

and before the first `TryDeleteFileWithError`:

```csharp
var finalPathSafety =
    ValidationService
        .ValidateSessionPathsForDestructiveOperation(
            session);

if (!finalPathSafety.IsValid)
{
    return finalPathSafety;
}

if (ValidationService.IsReparsePoint(
        session.AssetFolder))
{
    return ValidationResult.Failure(
        "Asset folder became a reparse point "
        + "before Main rollback. No files were deleted.");
}

var ingameFolder =
    session.GetIngameFolderPath();

if (Directory.Exists(ingameFolder)
    && ValidationService.IsReparsePoint(
        ingameFolder))
{
    return ValidationResult.Failure(
        "Ingame folder became a reparse point "
        + "before Main rollback. No files were deleted.");
}
```

Then mutate.

---

# 18. R11-002B — RollbackReference

RollbackReference currently starts with ownership/path validation.

But while processing deterministic temps it already performs mutation:

```text
hash temp image
if hash matches -> delete temp image immediately

then:
hash temp provenance
possibly fail later
```

This means path safety can become stale before temp deletion, and verification/mutation are interleaved.

Preferred repair:

## Phase A — verification only

Collect:

```text
canonical ownership valid
temp image path valid
temp image hash valid if present
temp provenance path valid
temp provenance hash valid if present
all directory ownership flags valid
```

Do **not** delete anything.

## Final safety gate

Rerun:

```text
ValidateSessionPathsForDestructiveOperation(session)
AssetFolder reparse check
ReferenceFolder reparse check
```

## Phase B — mutation only

Then delete:

```text
temp provenance
temp image
canonical provenance
canonical image
empty tool-created reference folder
empty tool-created asset folder
```

This gives the same two-phase model already used by replacement rollback.

---

# 19. R11-002C — RollbackReferenceReplacement

Current structure is already mostly excellent:

```text
Phase A1 ownership
Phase A2 ownership
Phase A3 current destination verification
Phase A4 provenance verification
if verification errors -> FAIL CLOSED
Phase B mutation
```

The missing piece is simply:

```text
repeat transaction/path/reparse validation
```

between Phase A and Phase B.

Add:

```csharp
var finalTransactionSafety =
    _validationService
        .ValidateReferenceReplacementTransaction(
            transaction);

if (!finalTransactionSafety.IsValid)
{
    return finalTransactionSafety;
}
```

immediately before:

```csharp
// Phase B: Execution / Mutation
```

If desired, also call:

```csharp
RequireSafeReferenceReplacementTransaction(...)
```

through a non-throwing wrapper.

No state-machine redesign is needed.

---

# 20. R11-002D — CommitReferenceReplacement / CleanupReplacementBackups

Current cleanup does:

```text
ValidateReferenceReplacementTransaction
validate NEW canonical output
validate exact NEW provenance
hash OLD backup Reference
validate OLD backup provenance
DELETE backup files
```

The first path validation can become stale during the later hashes.

Immediately before:

```csharp
TryDeleteFileWithError(
    transaction.BackupReferencePath,
    errors);
```

rerun:

```csharp
ValidateReferenceReplacementTransaction(transaction)
```

and fail closed if no longer safe.

This matters because backup paths are also inside the Reference hierarchy.

---

# 21. R11-002E — SessionService.Cancel Phase 2

Cancel currently calls:

```csharp
EnsureCancelPathsAreSafe(session)
```

once at the beginning.

It then:

```text
persists Prepared
re-verifies provenance ownership
moves provenance to cancel temp
runs OnCancelProvenanceMovedHook
re-verifies Reference hash
moves Reference to cancel temp
persists FilesRenamed
```

The content re-verification is good.

But path safety is not revalidated immediately before the moves.

The hook boundary is especially important:

```text
OnCancelProvenanceMovedHook
```

can represent external filesystem change before the Reference move.

Required:

```text
before provenance File.Move:
    EnsureCancelPathsAreSafe(session)

after OnCancelProvenanceMovedHook,
before Reference File.Move:
    EnsureCancelPathsAreSafe(session)
```

The validator supports Prepared recovery state, so use the existing safety semantics rather than inventing a new path checker.

---

# 22. R11-002F — SessionService.Cancel Phase 3

Phase 3 currently:

```text
hash temp Reference
exact temp provenance validation
File.Delete temp provenance
File.Delete temp Reference
folder cleanup
Delete session journal
```

Add one final:

```csharp
EnsureCancelPathsAreSafe(session);
```

after the ownership checks and immediately before the first temp delete.

Also re-check the target folders before empty-directory deletion, especially after:

```text
OnBeforeFolderCleanupHook
```

because that hook is an explicit mutation/test boundary.

If the folder became a reparse point:

```text
do not delete directory
preserve session state
fail closed
```

---

# 23. R11-002 mandatory RecoveryCritical tests

## RollbackMain

```text
RollbackMain_AssetFolderBecomesReparseAfterOwnershipVerification_ZeroDeletes
RollbackMain_IngameBecomesReparseAfterOwnershipVerification_ZeroDeletes
```

Add a hook immediately before the final rollback path gate.

Assert:

```text
ValidationResult invalid
file delete count == 0
directory delete count == 0
journal remains
all exact-owned files remain
```

## RollbackReference

```text
RollbackReference_ReferenceFolderBecomesReparseAfterVerification_ZeroDeletes
RollbackReference_AssetFolderBecomesReparseAfterVerification_ZeroDeletes
```

Also prove the refactor is truly two-phase:

```text
unknown temp provenance
=> known temp image not deleted before failure
```

Recommended test:

```text
RollbackReference_UnknownTempProvenance_DoesNotPartiallyDeleteKnownTempImage
```

## RollbackReferenceReplacement

```text
ReplacementRollback_ReparseChangesAfterPhaseA_ZeroMutation
```

Count:

```text
delete attempts == 0
restore/move attempts == 0
```

A new hook at:

```text
after verificationErrors.Count == 0
before final safety gate / Phase B
```

is appropriate.

## Replacement cleanup

```text
ReplacementCleanup_ReparseChangesAfterBackupVerification_ZeroDeletes
```

## Cancel Phase 2

```text
Cancel_ReparseChangesAfterPreparedBeforeProvenanceMove_NoMove
Cancel_ReparseChangesAfterProvenanceMoveBeforeReferenceMove_NoReferenceMove
```

The second test can use:

```text
OnCancelProvenanceMovedHook
```

## Cancel Phase 3

```text
Cancel_FilesRenamed_ReparseChangesAfterOwnershipVerification_NoDelete
Cancel_FolderBecomesReparseAtFolderCleanup_NoDirectoryDelete
```

---

# 24. R11-003 — LOW — replacement “after final hash” test does not inject after the hash

Current test:

```text
Replacement_ReparseChangesAfterFinalHash_NoCanonicalMutation
```

sets:

```csharp
ValidationService.FileAttributesProvider = ...
```

**before** calling:

```csharp
processor.PromoteNewReference(tx)
```

But `PromoteNewReference()` begins with:

```csharp
RequireSafeReferenceReplacementTransaction(transaction);
```

Therefore the test can fail at the **first** path gate, before either final hash is computed.

It does not prove the newly added **second** gate.

The source second gate is present and appears correct.

This is a test-assurance gap, not a proven production defect.

---

# 25. R11-003 fix

Add a hook precisely here:

```text
temp Reference hash verified
temp provenance hash verified
HOOK
second RequireSafeReferenceReplacementTransaction
first canonical File.Move
```

Example:

```csharp
[ThreadStatic]
internal static Action<ReferenceReplacementTransaction>?
    OnBeforeReplacementFinalPathGate;
```

Then:

```csharp
OnBeforeReplacementFinalPathGate?.Invoke(
    transaction);

RequireSafeReferenceReplacementTransaction(
    transaction);
```

Test:

```text
Replacement_ReparseChangesAfterFinalHash_NoCanonicalMutation
```

sets the reparse provider from that hook.

Assert:

```text
NEW canonical absent
OLD backup state unchanged
temp NEW files remain
no delete/restore attempt occurs inside PromoteNewReference
```

---

# 26. Fresh areas rechecked and currently clean

No additional source defect was found in:

```text
[PASS] R10 Main local unsafe-path catch
[PASS] R10 initial Reference local unsafe-path catch
[PASS] R10 zero-delete local cleanup tests
[PASS] Main final staging raw hash authority
[PASS] Main final promotion path gate
[PASS] initial Reference final raw hash authority
[PASS] initial Reference final promotion path gate
[PASS] replacement dual path gate around final hash work
[PASS] replacement temp image/provenance exact hash authority
[PASS] replacement rollback state machine phase classification
[PASS] replacement durable/UI rollback boundary
[PASS] replacement post-commit UI isolation
[PASS] Main durable/UI boundary
[PASS] Reference durable/UI boundary
[PASS] Cancel durable/UI boundary at MainForm level
[PASS] exact Reference provenance validator hash-first behavior
[PASS] exact final provenance validator hash-first behavior
[PASS] deterministic transaction staging names
[PASS] no random nested provenance temp in journaled artifact transactions
[PASS] corrected Main canonical-path assertions
[PASS] startup template status behavior
[PASS] partial deterministic provenance recovery baseline
```

---

# 27. Recommended repair order

## Phase 1 — R11-001

Replace all current v1.1 local provenance cleanup text checks with persisted hash ownership.

This is small and directly prevents deletion of byte-modified provenance.

## Phase 2 — R11-002

Apply a uniform final destructive-path gate to persisted rollback/cancel mutation phases.

Recommended invariant:

```text
ownership verification
-> FINAL path/reparse safety
-> mutation
```

## Phase 3 — R11-003

Add the precise post-hash replacement test hook.

---

# 28. Universal ownership rule after R11

For current transactions:

```text
IMAGE:
    delete only if SHA == persisted image hash

PROVENANCE:
    delete only if SHA == persisted provenance hash

PATH:
    destructively touch only after a current path/reparse safety gate
```

Do not mix:

```text
raw-byte authority for validation
decoded-text authority for deletion
```

---

# 29. Universal rollback rule after R11

All rollback/cancel/cleanup methods should follow:

```text
Phase A:
    validate transaction/session metadata
    verify all files that may be deleted/restored
    perform NO destructive mutation

Phase B preflight:
    rerun full path confinement
    rerun reparse checks

Phase C:
    delete/move/restore only exact-owned files
```

If any Phase B check fails:

```text
zero mutation
preserve journal
fail closed
```

---

# 30. Static verification after repair

## No weak provenance cleanup in active transactions

```powershell
rg -n `
  "TryVerifyTextFileOwnership" `
  src/AssetProvenanceHelper
```

Preferred result:

```text
0 production transaction usages
```

If retained, every remaining use must be explicitly legacy-only.

---

## Main local provenance cleanup

```powershell
rg -n `
  "MainProvenanceHash|tempProvenancePath|finalProvenance|TryVerifyFileHashOwnership" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs
```

Required:

```text
temp provenance cleanup -> MainProvenanceHash
canonical provenance cleanup -> MainProvenanceHash
```

---

## Reference local provenance cleanup

```powershell
rg -n `
  "ReferenceProvenanceHash|tempProvenancePath|referenceProvenance|TryVerifyFileHashOwnership" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Required:

```text
temp provenance cleanup -> ReferenceProvenanceHash
canonical provenance cleanup -> ReferenceProvenanceHash
```

---

## Rollback final gates

```powershell
rg -n `
  "RollbackMain|RollbackReference|RollbackReferenceReplacement|CommitReferenceReplacement|ValidateSessionPathsForDestructiveOperation|ValidateReferenceReplacementTransaction|TryDeleteFileWithError|TryRestoreFileWithError" `
  src/AssetProvenanceHelper/Services
```

Manual order:

```text
all ownership verification
final safety gate
first destructive helper
```

---

## Cancel final gates

```powershell
rg -n `
  "EnsureCancelPathsAreSafe|File\.Move|File\.Delete|OnCancelProvenanceMovedHook|OnBeforeFolderCleanupHook" `
  src/AssetProvenanceHelper/Services/SessionService.cs
```

Required:

```text
path safety immediately before Phase 2 mutation
path safety again after hook boundary
path safety immediately before Phase 3 deletion
reparse-safe folder cleanup
```

---

# 31. Required Windows execution gate after repair

When a Windows/.NET environment is available:

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

Expected:

```text
0 failed
0 skipped RecoveryCritical
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

## Publish

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

# 32. Definition of Done for the next pass

## Preserve R10

```text
[ ] Main unsafe local catch remains zero-delete fail-closed
[ ] initial Reference unsafe local catch remains zero-delete fail-closed
[ ] Main final hash -> path -> promotion ordering remains
[ ] initial Reference final hash -> path -> promotion ordering remains
[ ] replacement second path gate remains
[ ] corrected canonical Main test paths remain
```

## R11-001

```text
[ ] Main temp provenance cleanup uses MainProvenanceHash
[ ] Main canonical provenance cleanup uses MainProvenanceHash
[ ] initial Reference temp provenance cleanup uses ReferenceProvenanceHash
[ ] initial Reference canonical provenance cleanup uses ReferenceProvenanceHash
[ ] byte-modified/BOM provenance is preserved, not deleted
[ ] no current v1.1 transaction cleanup silently falls back to text equality
```

## R11-002

```text
[ ] RollbackMain final full path/reparse gate before first delete
[ ] RollbackReference verification-only Phase A
[ ] RollbackReference final full path/reparse gate before first delete
[ ] RollbackReferenceReplacement final transaction safety gate before Phase B
[ ] CommitReferenceReplacement final transaction safety gate before backup delete
[ ] Cancel final path gate before provenance move
[ ] Cancel final path gate before Reference move after hook boundary
[ ] Cancel final path gate before FilesRenamed deletion
[ ] Cancel folder cleanup rejects reparse path
```

## R11-003

```text
[ ] replacement post-hash pre-path hook exists
[ ] test injects reparse from that exact hook
[ ] test proves second final gate, not first gate
```

## Execution

```text
[ ] Debug build warnings-as-errors PASS
[ ] Debug tests PASS
[ ] Release build warnings-as-errors PASS
[ ] Release tests PASS
[ ] RecoveryCritical PASS
[ ] 20/20 Release PASS
[ ] self-contained publish PASS
[ ] smoke PASS
[ ] coverage PASS
```

---

# 33. Final eleventh-pass conclusion

The `bugs10.md` repair succeeded.

The remaining problems are not regressions in the R10 fix itself.

They are two deeper consistency gaps uncovered by applying the same safety model everywhere:

1. **Ownership authority must remain byte-exact during cleanup.**
   A provenance file rejected by its SHA-256 authority must never later become deletable merely because its decoded text happens to match.

2. **Path/reparse authority must be current at the destructive boundary.**
   Persisted rollback/cancel APIs should not validate paths, spend significant time hashing files, and then mutate without one final safety gate.

The desired invariant is now clear and uniform:

```text
verify exact bytes
verify exact paths
verify current reparse state
then mutate
```

**Current acceptance state: FAIL — R10 is closed, but R11-001 and R11-002 remain HIGH destructive-safety defects; R11-003 is a test-assurance gap.**

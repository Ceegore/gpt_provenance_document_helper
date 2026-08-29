# AI Asset Provenance Helper — Twelfth Paranoid Retest & Repair Guide

**File:** `bugs12.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `32ae24f1c7456e2944dac1f1a91c307973d7bdfd`  
**Previous audited commit:** `6c0fc0a2f3d4be86cb1dbc9cdf70499275990bac`  
**Previous audit:** `bugs11.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — the R11 repair is materially real, but the independent fresh pass found one HIGH mutation-authority race, one MEDIUM-HIGH replacement-backup gate gap, one MEDIUM Cancel folder-cleanup path gap, and one LOW false-assurance test defect.**

This is the strongest repository revision audited so far.

The current commit genuinely repairs the intended R11 findings:

- active Main and initial Reference provenance cleanup now uses SHA-256 ownership rather than decoded-text equality;
- the weak `TryVerifyTextFileOwnership()` helper was removed from production transaction cleanup;
- `RollbackMain()` now has a final full path/reparse gate after ownership verification;
- `RollbackReference()` was refactored into verification-first / final-path-gate / mutation;
- `RollbackReferenceReplacement()` now has a final transaction/path gate between Phase A and Phase B;
- replacement backup cleanup now has a final transaction/path gate before deleting backups;
- Cancel revalidates path safety before the provenance move, before the Reference move, and before Phase-3 deletion;
- replacement promotion now has a true post-hash hook before its second path gate;
- BOM/byte-modification provenance tests were added.

Those changes should be retained.

The fresh pass found:

| ID | Severity | Area | Summary |
|---|---:|---|---|
| **R12-001** | **HIGH** | rollback/cleanup mutation authority | the new final path hooks run **after byte ownership verification**, but the final gate rechecks only paths; the hook/external process can modify bytes after Phase A and those now-unknown bytes are still deleted or restored |
| **R12-002** | **MEDIUM-HIGH** | replacement OLD backup | `BackupOldReference()` still verifies path/bytes too early and performs two canonical-to-backup moves without a final combined authority gate after the expensive hash/provenance checks |
| **R12-003** | **MEDIUM** | Cancel folder cleanup | after `OnBeforeFolderCleanupHook`, Cancel checks only AssetFolder/ReferenceFolder themselves, not the full hierarchy; an AssetRoot reparse change can escape the post-hook check |
| **R12-004** | **LOW** | RecoveryCritical tests | both new `RollbackMain_*BecomesReparseAfterOwnershipVerification` tests create an invalid temp provenance, so `RollbackMain()` can return before the final-path hook is ever invoked |

The true acceptance blocker is R12-001.

---

# 0.2 Current repository state

Current `main`:

```text
32ae24f1c7456e2944dac1f1a91c307973d7bdfd
```

Commit:

```text
Fix issues in bugs11.md: SHA-256 hash ownership and two-phase rollback gates
```

Parent:

```text
6c0fc0a2f3d4be86cb1dbc9cdf70499275990bac
```

The commit modifies only the expected R11 repair surface plus `bugs11.md`.

---

# 0.3 CI / execution evidence

Connected GitHub status for the SHA currently exposes:

```text
statuses: []
```

The available commit workflow-run wrapper returns:

```text
workflow_runs: []
```

and does not expose a direct push-to-main Actions run.

The present analysis environment does not provide the required Windows/.NET execution environment.

Per the established project acceptance rule:

> missing exact Windows/.NET execution evidence is deferred verification and is not, by itself, a blocker.

The FAIL verdict below is caused by current source-level findings.

---

# 1. Full `bugs11.md` retest

| R11 item | Twelfth-pass result |
|---|---|
| R11-001 Main temp provenance SHA ownership | **FIXED** |
| R11-001 Main canonical provenance SHA ownership | **FIXED** |
| R11-001 initial Reference temp provenance SHA ownership | **FIXED** |
| R11-001 initial Reference canonical provenance SHA ownership | **FIXED** |
| R11-001 weak text helper removed | **FIXED** |
| R11-002 RollbackMain final path gate | **FIXED structurally** |
| R11-002 RollbackReference two-phase structure | **FIXED structurally** |
| R11-002 RollbackReference final path gate | **FIXED structurally** |
| R11-002 replacement rollback final path gate | **FIXED structurally** |
| R11-002 replacement cleanup final path gate | **FIXED structurally** |
| R11-002 Cancel Phase-2 path gates | **FIXED baseline** |
| R11-002 Cancel Phase-3 pre-delete path gate | **FIXED baseline** |
| R11-002 Cancel post-folder-hook full hierarchy gate | **PARTIAL — see R12-003** |
| R11-003 replacement post-hash hook/test | **FIXED** |

---

# 2. R11-001 retest — PASS

Current Main local exception cleanup derives exact provenance authority:

```csharp
var expectedProvHash =
    session.MainProvenanceHash
    ?? ...
```

and cleanup checks:

```csharp
TryVerifyFileHashOwnership(
    finalProvenance,
    expectedProvHash)
```

and:

```csharp
TryVerifyFileHashOwnership(
    tempProvenancePath,
    expectedProvHash)
```

before deletion.

Current initial Reference likewise checks:

```csharp
TryVerifyFileHashOwnership(
    tempProvenancePath,
    expectedProvHash)
```

and:

```csharp
TryVerifyFileHashOwnership(
    referenceProvenance,
    expectedProvHash)
```

The old decoded-text helper has been deleted from the shared file helper.

The new BOM tests exercise the byte-level distinction.

**R11-001 PASS.**

---

# 3. R11-002 retest — structurally improved

## RollbackMain

Current order is:

```text
initial path validation
metadata validation
canonical/temp ownership verification
OnBeforeRollbackMainFinalPathGate
FINAL full path validation
final AssetFolder/Ingame reparse checks
mutation
```

This is the intended R11 structure.

## RollbackReference

Current order is:

```text
Phase A:
  canonical ownership validation
  temp path/hash validation
  NO deletion

OnBeforeRollbackReferenceFinalPathGate

FINAL path/reparse gate

Phase B:
  delete temps
  delete canonical Reference artifacts
  cleanup empty tool-created folders
```

This corrects the previous partial-delete problem.

## Replacement rollback

Current structure is:

```text
Phase A1..A4 verification
if errors -> fail closed

OnBeforeRollbackReferenceReplacementFinalPathGate

ValidateReferenceReplacementTransaction AGAIN

Phase B mutation
```

This is the requested R11 architecture.

## Replacement cleanup

Current cleanup now verifies backups, runs:

```text
OnBeforeReplacementCleanupFinalPathGate
ValidateReferenceReplacementTransaction
```

and only then deletes backups.

## Cancel

Current code runs `EnsureCancelPathsAreSafe()`:

```text
before provenance move
before Reference move
before Phase-3 delete
```

This is a meaningful improvement.

The fresh issue is that **path authority is refreshed, but byte authority is not refreshed after the new race hooks**. That is R12-001.

---

# 4. R11-003 retest — PASS

Current replacement promotion contains:

```csharp
var tempRefHash = ...
require expected

var tempProvHash = ...
require expected

OnBeforeReplacementFinalPathGate?.Invoke(
    transaction);

RequireSafeReferenceReplacementTransaction(
    transaction);

File.Move(...)
```

The test now enables the reparse provider from that exact hook.

Therefore the test genuinely exercises the second post-hash path gate rather than the earlier pre-hash gate.

**R11-003 PASS.**

---

# 5. R12-001 — HIGH — final path hooks create an explicit byte-authority gap before destructive mutation

This is the strongest fresh finding.

The new R11 architecture intentionally introduces hooks at this boundary:

```text
all byte ownership verified
HOOK
final path validation
destructive mutation
```

The problem is that the final gate checks **path authority only**.

If the hook represents an external process that changes the bytes rather than the path, the mutation phase uses stale ownership evidence.

This is no longer merely a theoretical nanosecond race.

The repository now contains explicit test hooks at the exact location where it can be reproduced deterministically.

---

# 6. R12-001A — RollbackMain can delete bytes modified after ownership verification

Current `RollbackMain()` verifies:

```text
root Main SHA == MainHash
ingame SHA == MainHash
final provenance exact authority
temp Main SHA == MainHash
temp ingame SHA == MainHash
temp provenance exact authority
```

Then it runs:

```csharp
OnBeforeRollbackMainFinalPathGate?.Invoke(
    session);
```

Then it validates:

```text
paths
reparse state
```

Then it deletes files with:

```csharp
TryDeleteFileWithError(...)
```

But `TryDeleteFileWithError()` performs:

```csharp
if (File.Exists(path))
{
    OnBeforeDeleteFileHook?.Invoke(path);
    File.Delete(path);
}
```

There is no ownership re-check after `OnBeforeRollbackMainFinalPathGate`.

There is also no ownership check inside the delete helper.

---

# 7. Deterministic failure scenario for RollbackMain

Use the existing final-path hook:

```csharp
AssetProcessorService
    .OnBeforeRollbackMainFinalPathGate =
    session =>
    {
        File.WriteAllBytes(
            rootMainPath,
            foreignValidImageBytes);
    };
```

Sequence:

```text
T0 root Main hash == MainHash
T1 RollbackMain verifies it as tool-owned

T2 OnBeforeRollbackMainFinalPathGate runs
T3 hook replaces root Main with foreign bytes H2

T4 final path/reparse gate passes
   because the path is still safe

T5 TryDeleteFileWithError(rootMainPath)
T6 File.Delete(rootMainPath)
```

Result:

```text
foreign H2 deleted
```

This directly violates `_changePlan2.md`:

```text
A promoted file may only be removed during rollback
after exact ownership verification.

Unknown or externally modified files must be preserved;
fail closed instead of deleting them.
```

The exact ownership verification at T1 is stale by T6.

---

# 8. R12-001B — provenance has the same gap

The same hook can prepend a BOM or replace the final provenance after exact hash verification.

The final path gate will not notice.

Phase B deletes the modified provenance because:

```text
TryDeleteFileWithError
```

does not receive an expected hash.

This partially reopens the same safety property that R11 correctly strengthened.

R11 fixed:

```text
text equality -> hash equality
```

but only at Phase A.

The hash must remain authoritative at the destructive operation.

---

# 9. R12-001C — RollbackReference has the same gap

Current `RollbackReference()` now cleanly performs Phase A.

That is good.

But:

```csharp
OnBeforeRollbackReferenceFinalPathGate?.Invoke(
    session);
```

runs **after** temp/canonical ownership has been verified.

The hook can modify:

```text
temp image
temp provenance
canonical Reference
canonical Reference provenance
```

without changing any path.

The final gate sees safe paths.

Phase B then deletes the changed artifact.

---

# 10. R12-001D — replacement rollback can restore unknown backup bytes

This case is even more serious because the operation is not merely deletion.

Current replacement rollback Phase A verifies:

```text
BackupReferencePath SHA == OldSession.ReferenceHash
BackupProvenancePath exact old provenance
current destinations match old/new authority
```

Then:

```csharp
OnBeforeRollbackReferenceReplacementFinalPathGate
```

runs.

Then the code only reruns:

```csharp
ValidateReferenceReplacementTransaction(
    transaction)
```

which validates transaction/path structure, not the already-verified backup content.

Then Phase B can execute:

```csharp
TryRestoreFileWithError(
    transaction.BackupReferencePath,
    transaction.OldSession.ReferenceDestinationPath,
    ...)
```

`TryRestoreFileWithError()` does not hash the backup.

Therefore:

```text
Phase A verifies OLD backup H1
hook changes backup to H2
path gate passes
Phase B restores H2 into canonical OLD location
```

Unknown bytes can become the canonical restored Reference.

That is a HIGH integrity defect.

---

# 11. R12-001E — replacement cleanup can delete an externally modified backup

`CommitReferenceReplacement()` similarly does:

```text
verify backup image/provenance authority

OnBeforeReplacementCleanupFinalPathGate

validate transaction paths

TryDeleteFileWithError(backup Reference)
TryDeleteFileWithError(backup provenance)
```

The hook can change backup bytes after verification.

The path gate still succeeds.

The now-unknown backup is deleted.

---

# 12. R12-001F — `OnBeforeDeleteFileHook` itself exposes a second deterministic race

Even if the final-path hooks are fixed, the shared helper currently contains:

```csharp
OnBeforeDeleteFileHook?.Invoke(path);
File.Delete(path);
```

Any test can therefore modify the file in the hook after every caller's last ownership check.

This is an excellent test injection boundary, but the current ordering makes the helper unsafe by construction.

The hook should represent:

```text
external mutation immediately before destructive operation
```

and the implementation must re-establish authority **after the hook**.

---

# 13. Required R12-001 architecture

Introduce ownership-aware destructive helpers.

Do not use:

```csharp
verify somewhere above
...
TryDeleteFileWithError(path)
```

for managed asset files.

Instead make ownership part of the destructive helper itself.

---

# 14. Copy-ready hash-owned deletion helper

Recommended shape:

```csharp
private bool TryDeleteHashOwnedFileWithError(
    string path,
    string expectedHash,
    string description,
    ICollection<string> errors)
{
    try
    {
        if (!File.Exists(path))
        {
            return true;
        }

        // Test/external-race boundary must occur BEFORE
        // the final ownership verification.
        OnBeforeDeleteFileHook?.Invoke(path);

        if (!File.Exists(path))
        {
            return true;
        }

        var actualHash =
            ComputeSha256(path);

        if (!string.Equals(
                actualHash,
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{description} at '{path}' changed "
                + "before deletion. File preserved.");

            return false;
        }

        File.Delete(path);

        return true;
    }
    catch (Exception ex)
    {
        errors.Add(
            $"Could not delete {description} "
            + $"'{path}': {ex.Message}");

        return false;
    }
}
```

This closes the deterministic hook gap.

---

# 15. Stronger Windows implementation

For the strongest practical ownership-to-delete guarantee, perform the final hash while holding a handle that denies writers.

Recommended pattern:

```csharp
using var stream =
    new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete);

var hash =
    SHA256.HashData(stream);

var actualHash =
    Convert
        .ToHexString(hash)
        .ToLowerInvariant();

if (!string.Equals(
        actualHash,
        expectedHash,
        StringComparison.OrdinalIgnoreCase))
{
    ...
    return false;
}

// The open handle denies new write sharing.
// FileShare.Delete allows path deletion while held.
File.Delete(path);
```

Validate this behavior on the target Windows/.NET environment.

If moving/deleting an open file proves incompatible on the exact target, retain the simpler post-hook rehash as the minimum required repair.

---

# 16. Provenance deletion

For current v1.1 state:

```text
Reference provenance
-> ReferenceProvenanceHash

Main provenance
-> MainProvenanceHash
```

For a truly legacy state without stored provenance hash, derive the expected raw UTF-8-no-BOM bytes from the exact rendered authority and hash those bytes.

Do not downgrade current transactions to decoded-text equality.

---

# 17. Ownership-aware restore helper

Current:

```csharp
TryRestoreFileWithError(
    backupPath,
    destinationPath,
    description,
    errors)
```

must not trust an earlier Phase-A hash.

Use:

```csharp
private bool TryRestoreHashOwnedFileWithError(
    string backupPath,
    string destinationPath,
    string expectedHash,
    string description,
    ICollection<string> errors)
{
    try
    {
        if (!File.Exists(backupPath))
        {
            errors.Add(
                $"{description} backup is missing: "
                + backupPath);

            return false;
        }

        if (File.Exists(destinationPath))
        {
            errors.Add(
                $"Could not restore {description}: "
                + "destination already exists: "
                + destinationPath);

            return false;
        }

        OnBeforeRestoreFileHook?.Invoke(
            backupPath,
            destinationPath);

        if (!File.Exists(backupPath))
        {
            errors.Add(
                $"{description} backup disappeared "
                + "before restore.");

            return false;
        }

        var actualHash =
            ComputeSha256(backupPath);

        if (!string.Equals(
                actualHash,
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{description} backup changed "
                + "before restore. Backup preserved.");

            return false;
        }

        File.Move(
            backupPath,
            destinationPath,
            overwrite: false);

        return true;
    }
    catch (Exception ex)
    {
        errors.Add(
            $"Could not restore {description}: "
            + ex.Message);

        return false;
    }
}
```

Add:

```csharp
[ThreadStatic]
internal static Action<string, string>?
    OnBeforeRestoreFileHook;
```

For provenance use the appropriate persisted provenance hash.

---

# 18. Better Phase-B rule

The final destructive phase should not mean:

```text
Phase A proved ownership once
therefore Phase B may blindly mutate
```

It should mean:

```text
Phase A:
    prove the transaction is globally reconcilable

Final path gate:
    prove paths are still safe

Phase B:
    before each destructive file operation,
    re-prove that specific file's exact ownership
```

This preserves both properties:

```text
global all-or-nothing safety decision
and
fresh per-file mutation authority
```

---

# 19. R12-001 required tests

All should be:

```csharp
[Trait("Category", "RecoveryCritical")]
```

---

## Test A — RollbackMain root Main changed at final-path hook

```text
RollbackMain_RootMainChangesAfterPhaseA_PreservesForeignFile
```

Setup a valid Main rollback state.

At:

```csharp
OnBeforeRollbackMainFinalPathGate
```

rewrite root Main to different valid image bytes.

Expected:

```text
RollbackMain invalid
foreign root Main still exists
root Main not deleted
journal/active Main metadata preserved
```

This test must fail on current source.

---

## Test B — RollbackMain provenance changes after Phase A

```text
RollbackMain_ProvenanceChangesAfterPhaseA_PreservesForeignFile
```

At final-path hook prepend BOM or write foreign content.

Expected:

```text
provenance still exists
rollback invalid
no later cleanup deletes it
```

---

## Test C — RollbackReference temp changes after Phase A

```text
RollbackReference_TempImageChangesAfterPhaseA_PreservesUnknownTemp
```

At:

```csharp
OnBeforeRollbackReferenceFinalPathGate
```

modify the known temp image.

Expected:

```text
temp remains
rollback invalid
no canonical deletion occurs after failure
```

---

## Test D — replacement backup changes after Phase A

```text
ReplacementRollback_BackupReferenceChangesAfterPhaseA_NotRestored
```

At:

```csharp
OnBeforeRollbackReferenceReplacementFinalPathGate
```

rewrite:

```text
BackupReferencePath
```

with different valid image bytes.

Expected:

```text
RollbackReferenceReplacement invalid
tampered backup preserved
tampered backup NOT restored canonical
no unknown bytes promoted to old canonical path
```

This is the highest-value R12 test.

---

## Test E — replacement backup provenance changes after Phase A

Same pattern for:

```text
BackupProvenancePath
```

Use BOM-only byte change as a precise raw-byte test.

Expected:

```text
backup provenance preserved
not restored
rollback invalid
```

---

## Test F — cleanup backup changes at final-path hook

```text
ReplacementCleanup_BackupChangesAfterVerification_PreservesBackup
```

At:

```csharp
OnBeforeReplacementCleanupFinalPathGate
```

modify the backup image/provenance.

Expected:

```text
CommitReferenceReplacement invalid
modified backup still exists
no delete
transaction.IsCommitted == false
```

---

## Test G — exact delete helper race

Use:

```csharp
OnBeforeDeleteFileHook
```

to modify the target file.

Expected:

```text
helper rehashes after hook
detects changed bytes
preserves file
```

This locks the actual operation boundary rather than only higher-level ordering.

---

# 20. R12-002 — MEDIUM-HIGH — `BackupOldReference()` still lacks a final combined authority gate

R11 strengthened promotion/rollback/cleanup but one replacement mutation remains weaker:

```csharp
BackupOldReference(
    ReferenceReplacementTransaction transaction)
```

Current structure:

```text
RequireSafeReferenceReplacementTransaction

require OLD canonical files exist

hash OLD Reference
verify OLD provenance exact ownership

File.Move OLD Reference -> backup
File.Move OLD provenance -> backup
```

The transaction/path check occurs **before** the potentially expensive file hash and provenance verification.

There is no second final path gate before the first `File.Move`.

There is also no fresh byte verification after a race hook because there is no corresponding final authority hook.

---

# 21. Why R12-002 matters

This operation removes the stable OLD canonical files from their normal names.

A race can occur:

```text
T0 reference folder safe
T1 RequireSafe... passes

T2 hash OLD image
T3 validate OLD provenance

T4 folder hierarchy becomes reparse
or OLD image/provenance changes

T5 File.Move old canonical -> backup
```

The journal is durable (`OldBackupPending`), so recovery is conservative, but the mutator itself should not move bytes through an authority state it no longer proves.

---

# 22. Required `BackupOldReference()` repair

Add:

```csharp
[ThreadStatic]
internal static Action<
    ReferenceReplacementTransaction>?
    OnBeforeBackupOldReferenceFinalAuthorityGate;
```

Then use:

```text
initial transaction/path validation

OLD image ownership
OLD provenance ownership

HOOK

FINAL transaction/path validation
FINAL OLD image raw hash
FINAL OLD provenance raw hash

move OLD image -> backup
move OLD provenance -> backup
```

Example:

```csharp
OnBeforeBackupOldReferenceFinalAuthorityGate
    ?.Invoke(transaction);

RequireSafeReferenceReplacementTransaction(
    transaction);

var finalOldRefHash =
    ComputeSha256(
        transaction
            .OldSession
            .ReferenceDestinationPath);

if (!string.Equals(
        finalOldRefHash,
        transaction.OldSession.ReferenceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException(
        "OLD Reference changed before backup.");
}

var finalOldProvValidation =
    _validationService
        .ValidateExactReferenceProvenanceOwnership(
            transaction.OldSession,
            transaction
                .OldSession
                .ReferenceProvenancePath,
            _templateService);

if (!finalOldProvValidation.IsValid)
{
    throw new InvalidDataException(
        "OLD Reference provenance changed "
        + "before backup.");
}
```

Then move.

For maximum consistency, move via ownership-aware helpers as described in R12-001.

---

# 23. R12-002 tests

```text
BackupOldReference_PathBecomesReparseAfterOwnership_NoMove
BackupOldReference_ImageChangesAfterOwnership_NoMove
BackupOldReference_ProvenanceChangesAfterOwnership_NoMove
```

Each test must assert:

```text
OLD canonical image remains
OLD canonical provenance remains
backup image absent
backup provenance absent
```

for a failure before the first move.

---

# 24. R12-003 — MEDIUM — Cancel folder cleanup does not revalidate the full hierarchy after its explicit hook

Current Phase 3:

```text
verify temp files
EnsureCancelPathsAreSafe(session)

delete temp provenance
delete temp Reference

OnBeforeFolderCleanupHook

check:
    AssetFolder itself not reparse
    ReferenceFolder itself not reparse

delete empty ReferenceFolder
delete empty AssetFolder
delete session
```

The post-hook check is incomplete.

---

# 25. Parent reparse scenario

`ValidateSessionPathsForDestructiveOperation()` / `EnsureCancelPathsAreSafe()` includes the trusted AssetRoot hierarchy.

But after:

```csharp
OnBeforeFolderCleanupHook?.Invoke();
```

the code does **not** call that full validator again.

It checks only:

```csharp
IsReparsePoint(session.AssetFolder)
IsReparsePoint(referenceFolder)
```

Imagine:

```text
D:\gameassets\gamename
```

is the Asset Root.

After the hook, another process replaces:

```text
D:\gameassets\gamename
```

itself with a junction.

Then:

```text
D:\gameassets\gamename\asset
```

may resolve to an external target directory.

Checking only the child path's own attributes does not reliably prove that no parent component is a reparse point.

The full validator exists specifically to protect this hierarchy.

---

# 26. Required R12-003 repair

Replace the post-hook partial check with:

```csharp
OnBeforeFolderCleanupHook?.Invoke();

EnsureCancelPathsAreSafe(session);
```

Then optionally retain the direct checks as belt-and-suspenders:

```csharp
if (ValidationService.IsReparsePoint(
        session.AssetFolder)
    || ...)
{
    ...
}
```

The key requirement is:

```text
full hierarchy revalidation after the hook
```

not merely child attributes.

Because the session is in `FilesRenamed` and the temp files may already be absent, ensure `EnsureCancelPathsAreSafe()` accepts the legitimate post-file-delete cleanup state.

If it does not, split path-only validation from ownership-state validation:

```csharp
EnsureCancelPathHierarchyIsSafe(session)
EnsureCancelFilesOwnedForPhase(session)
```

and call the path-only helper after the folder-cleanup hook.

---

# 27. R12-003 test

Current test only makes:

```text
session.AssetFolder
```

appear reparse.

Add:

```text
Cancel_AssetRootBecomesReparseAtFolderCleanup_NoDirectoryDelete
```

At:

```csharp
OnBeforeFolderCleanupHook
```

make:

```csharp
FileAttributesProvider(path)
```

return:

```text
Directory | ReparsePoint
```

when:

```text
PathsEqual(path, session.AssetRootFolder)
```

Expected:

```text
Cancel throws/fails closed
session journal remains
no ReferenceFolder delete attempt
no AssetFolder delete attempt
```

Add a directory-delete hook counter if needed.

---

# 28. R12-004 — LOW — both new RollbackMain final-path tests can pass before the hook

The tests:

```text
RollbackMain_AssetFolderBecomesReparseAfterOwnershipVerification_ZeroDeletes

RollbackMain_IngameBecomesReparseAfterOwnershipVerification_ZeroDeletes
```

currently create:

```csharp
File.WriteAllText(
    session.GetMainTempProvenancePath(),
    "PROVENANCE");
```

But the prepared Main session already contains:

```text
MainProvenanceHash
```

for the exact rendered final provenance.

Therefore:

```text
SHA("PROVENANCE")
!=
session.MainProvenanceHash
```

`RollbackMain()` verifies temp provenance **before**:

```csharp
OnBeforeRollbackMainFinalPathGate
```

and can return:

```text
invalid temp provenance
```

before the hook executes.

The test still sees:

```text
result invalid
delete count == 0
temps remain
```

and passes for the wrong reason.

---

# 29. Required R12-004 test fix

Simplest option:

**Do not create temp provenance at all.**

For a final path gate test, it is not required.

Use:

```csharp
Directory.CreateDirectory(
    session.GetIngameFolderPath());

File.Copy(
    mainSource,
    session.GetMainTempImagePath());

File.Copy(
    mainSource,
    session.GetMainTempIngamePath());
```

Then add:

```csharp
var hookInvoked = false;

AssetProcessorService
    .OnBeforeRollbackMainFinalPathGate =
    s =>
    {
        hookInvoked = true;
        ...
    };
```

And assert:

```csharp
Assert.True(
    hookInvoked,
    "Test must reach the final rollback path gate.");
```

If temp provenance is desired, write the real expected provenance bytes and verify its SHA matches `session.MainProvenanceHash` before invoking rollback.

---

# 30. Add hook-reach assertions generally

Every race-injection test should prove its intended hook ran.

Pattern:

```csharp
var hookInvoked = false;

SomeHook = ... =>
{
    hookInvoked = true;
    ...
};

...
Assert.True(hookInvoked);
```

Apply to new tests for:

```text
RollbackMain final gate
RollbackReference final gate
replacement rollback final gate
replacement cleanup final gate
replacement post-hash promotion gate
Cancel hook boundaries
```

This prevents another false-assurance test from silently failing earlier.

---

# 31. Fresh checks that passed

No new issue was found in:

```text
[PASS] R11 active provenance cleanup uses SHA authority
[PASS] weak decoded-text cleanup helper removed
[PASS] initial Reference BOM temp preservation
[PASS] Main BOM temp preservation
[PASS] Main promoted provenance BOM preservation
[PASS] RollbackReference no longer deletes known temp before discovering unknown later temp
[PASS] replacement promotion second post-hash path gate
[PASS] replacement post-hash test now uses the correct hook
[PASS] replacement rollback final path gate is present
[PASS] replacement cleanup final path gate is present
[PASS] Cancel path gate before provenance move
[PASS] Cancel path gate before Reference move
[PASS] Cancel path gate before Phase-3 temp deletion
[PASS] deterministic Reference/Main staging authority retained
[PASS] durable journal boundaries retained
[PASS] exact persisted provenance validators remain hash-first
[PASS] corrected Main canonical-path tests retained
[PASS] no new redundant persisted ingame fields
[PASS] no new overwrite=true canonical artifact writes
```

---

# 32. Preferred final transaction model

The repository has converged on a clear model.

Use it consistently.

## Before production mutation

```text
durable journal
deterministic paths
stage
verify raw hashes
verify path hierarchy
promote
```

## Before destructive recovery mutation

```text
Phase A:
    verify transaction globally reconcilable

race/test hook

Final authority gate:
    full path hierarchy
    exact byte ownership

Phase B:
    for each destructive file operation:
        fresh per-file ownership
        delete/move/restore
```

The critical distinction is:

```text
"path authority" and "byte authority"
must both be fresh.
```

---

# 33. Static checks after repair

## Generic blind delete helper

```powershell
rg -n `
  "TryDeleteFileWithError\(" `
  src/AssetProvenanceHelper/Services
```

Every managed-asset call must be reviewed.

Preferred:

```text
managed image/provenance deletion:
    ownership-aware helper

non-authoritative internal temp cleanup only:
    generic helper allowed
```

---

## Restore helper

```powershell
rg -n `
  "TryRestoreFileWithError|TryRestoreHashOwnedFileWithError" `
  src/AssetProvenanceHelper/Services
```

Required:

```text
replacement rollback restores
must reverify backup ownership at mutation boundary
```

---

## BackupOldReference

```powershell
rg -n `
  "BackupOldReference|RequireSafeReferenceReplacementTransaction|ComputeSha256|ValidateExactReferenceProvenanceOwnership|File\.Move" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Required order:

```text
initial path gate
ownership verification
hook
FINAL path gate
FINAL ownership verification
first move
```

---

## Cancel folder cleanup

```powershell
rg -n `
  "OnBeforeFolderCleanupHook|EnsureCancelPathsAreSafe|Directory\.Delete" `
  src/AssetProvenanceHelper/Services/SessionService.cs
```

Required:

```text
OnBeforeFolderCleanupHook
FULL path hierarchy validation
Directory.Delete
```

---

## False-positive test

```powershell
rg -n `
  '"PROVENANCE"|OnBeforeRollbackMainFinalPathGate|hookInvoked' `
  tests/AssetProvenanceHelper.Tests/Bugs3ParanoidTests.cs
```

Required:

```text
RollbackMain final-gate tests:
  no invalid fake provenance
  Assert.True(hookInvoked)
```

---

# 34. Required Windows execution gate after repair

When the exact Windows/.NET environment is available:

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

# 35. Definition of Done for the next pass

## Preserve R11

```text
[ ] provenance local cleanup remains hash-based
[ ] weak text helper remains removed
[ ] RollbackMain final path gate remains
[ ] RollbackReference two-phase structure remains
[ ] replacement rollback final path gate remains
[ ] replacement cleanup final path gate remains
[ ] Cancel pre-mutation path gates remain
[ ] replacement post-hash promotion hook/gate remains
```

## R12-001

```text
[ ] managed file deletion re-verifies exact ownership after race hook
[ ] root Main modified after Phase A is preserved
[ ] ingame modified after Phase A is preserved
[ ] provenance modified after Phase A is preserved
[ ] Reference temp/canonical modified after Phase A is preserved
[ ] replacement backup modified after Phase A is NOT restored
[ ] replacement cleanup does not delete modified backup
[ ] restore helper re-verifies exact backup authority at mutation boundary
```

## R12-002

```text
[ ] BackupOldReference has final race hook
[ ] final path/reparse gate after ownership checks
[ ] final OLD image SHA gate
[ ] final OLD provenance authority gate
[ ] no first backup move until all final authority passes
```

## R12-003

```text
[ ] Cancel folder cleanup reruns full hierarchy validation after hook
[ ] AssetRoot reparse after hook blocks folder deletion
[ ] journal remains on unsafe cleanup
```

## R12-004

```text
[ ] RollbackMain final-path tests use valid Phase-A state
[ ] no fake invalid "PROVENANCE" temp blocks hook
[ ] tests assert final-path hook was invoked
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

# 36. Final twelfth-pass conclusion

The `bugs11.md` repair is real.

The repository has now correctly separated:

```text
byte ownership
and
path ownership
```

in many places.

The remaining blocker is that they are not always **fresh at the same destructive boundary**.

The most important current sequence is:

```text
verify bytes
HOOK / external change
verify only paths
delete or restore
```

That can delete unknown bytes or restore unknown bytes into a canonical location.

The required final rule is:

```text
global Phase-A verification
race boundary
FULL path authority
FRESH byte authority
mutation
```

and managed destructive helpers should themselves enforce the final per-file ownership check.

**Current acceptance state: FAIL — R11 is materially fixed, but R12-001 remains a HIGH safety defect; R12-002 and R12-003 are narrower authority gaps; R12-004 is a false-assurance test defect.**

# AI Asset Provenance Helper — Thirteenth Paranoid Retest & Repair Guide

**File:** `bugs13.md`  
**Audit date:** 2026-08-20  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `f651cc1be1ca0e2a9513dbbbe68ba975eb88f660`  
**Previous audited commit:** `32ae24f1c7456e2944dac1f1a91c307973d7bdfd`  
**Previous audit:** `bugs12.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — all four R12 findings are materially repaired, but the fresh pass found two current safety/compatibility blockers and one additional mutation-boundary hardening gap.**

Current `main` genuinely implements the requested `bugs12.md` work:

- per-file hash-owned delete helper added;
- per-file hash-owned restore helper added;
- Main and Reference rollback now use those ownership-aware delete helpers;
- replacement rollback uses ownership-aware delete/restore helpers;
- replacement cleanup uses hash-owned backup deletion;
- `BackupOldReference()` has a final authority hook, a second path/reparse validation, a second image SHA check, and a second exact provenance check;
- Cancel reruns full path safety after `OnBeforeFolderCleanupHook`;
- the RollbackMain late-reparse tests now create valid provenance and explicitly assert their final-path hooks actually ran;
- byte-change-at-delete-hook testing exists.

Do not undo those repairs.

The independent fresh pass found:

| ID | Severity | Area | Summary |
|---|---:|---|---|
| **R13-001** | **HIGH** | per-file destructive helper path authority | `TryDeleteHashOwnedFileWithError()` and `TryRestoreHashOwnedFileWithError()` re-check bytes after their race hooks, but do **not** re-check confinement/reparse safety after those hooks; a path hierarchy can become unsafe at the exact destructive boundary and the helper still hashes/deletes/restores through it |
| **R13-002** | **HIGH** | legacy compatibility / replacement recovery | legacy stable sessions with `ReferenceProvenanceHash == null` are explicitly accepted by exact provenance validation, but replacement cleanup/rollback now pass `OldSession.ReferenceProvenanceHash!` into hash-owned helpers; replacement can therefore enter successfully and later become permanently cleanup/rollback-incomplete |
| **R13-003** | **MEDIUM-HIGH** | Cancel per-file destructive authority | Cancel Phase 2/3 still performs direct `File.Move` / `File.Delete` after earlier ownership checks; it does not use mutation-boundary ownership-aware helpers, so exact byte ownership can become stale immediately before the destructive call |

The source-level blockers are **R13-001** and **R13-002**.

---

# 0.2 Current repository state

Current `main`:

```text
f651cc1be1ca0e2a9513dbbbe68ba975eb88f660
```

Commit message:

```text
Fix issues in bugs12.md: byte-level ownership enforcement and authority gates
```

Parent:

```text
32ae24f1c7456e2944dac1f1a91c307973d7bdfd
```

`main` is one commit ahead of the previous audit and changes only the expected R12 repair surface plus `bugs12.md`.

---

# 0.3 CI / execution evidence

Connected GitHub status currently exposes:

```text
statuses: []
workflow_runs: []
```

for the audited SHA.

The local execution environment available to this audit has no:

```text
dotnet
pwsh
csc
msbuild
```

Per the established project rule, this exact-environment limitation is deferred verification and is **not** itself a blocker.

The FAIL verdict is based on source-level findings below.

---

# 1. Full `bugs12.md` retest

| R12 item | Thirteenth-pass result |
|---|---|
| R12-001 hash-owned deletion helper | **FIXED materially** |
| R12-001 hash-owned restore helper | **FIXED materially** |
| R12-001 Main byte changes after Phase A | **FIXED** |
| R12-001 Reference temp byte changes after Phase A | **FIXED** |
| R12-001 replacement backup image change | **FIXED** |
| R12-001 replacement backup provenance change | **FIXED** |
| R12-001 replacement cleanup backup change | **FIXED** |
| R12-001 delete-hook byte-race test | **FIXED** |
| R12-002 `BackupOldReference` final authority hook | **FIXED** |
| R12-002 final transaction/path gate | **FIXED** |
| R12-002 final OLD image SHA gate | **FIXED** |
| R12-002 final OLD provenance authority gate | **FIXED** |
| R12-003 Cancel full hierarchy validation after folder-cleanup hook | **FIXED** |
| R12-003 AssetRoot reparse test | **FIXED** |
| R12-004 RollbackMain false-assurance provenance setup | **FIXED** |
| R12-004 hook-reach assertions | **FIXED** |

---

# 2. R12-001 retest — PASS at byte-authority level

Current shared deletion helper now runs:

```text
target exists
OnBeforeDeleteFileHook
target still exists
SHA-256(target)
compare expected hash
File.Delete
```

That correctly closes the deterministic byte-change race which existed in the previous commit.

Current restore helper similarly runs:

```text
backup exists
destination absent
OnBeforeRestoreFileHook
backup still exists
SHA-256(backup)
compare expected hash
File.Move
```

Main, Reference, replacement rollback, and replacement cleanup now use these helpers for managed images/provenance.

The new tests also prove:

```text
byte change at hook
-> helper refuses deletion
-> file remains
```

This is real progress.

A separate **path-authority** gap remains and is R13-001.

---

# 3. R12-002 retest — PASS

`BackupOldReference()` now performs:

```text
initial replacement transaction/path validation

OLD image SHA
OLD provenance exact ownership

OnBeforeBackupOldReferenceFinalAuthorityGate

replacement transaction/path validation AGAIN

OLD image SHA AGAIN
OLD provenance exact ownership AGAIN

File.Move OLD image -> backup
File.Move OLD provenance -> backup
```

The added tests cover:

```text
path becomes reparse after initial ownership
OLD image changes after initial ownership
OLD provenance changes after initial ownership
```

and require no backup mutation before the final authority checks pass.

This materially closes R12-002.

---

# 4. R12-003 retest — PASS

Cancel Phase 3 now executes:

```text
OnBeforeFolderCleanupHook
EnsureCancelPathsAreSafe(session)
direct child reparse checks
folder cleanup
```

The new AssetRoot test makes:

```text
AssetRootFolder -> ReparsePoint
```

from the folder-cleanup hook and requires fail-closed behavior.

This closes the previous parent-chain gap.

---

# 5. R12-004 retest — PASS

The RollbackMain late-reparse tests now:

- construct provenance bytes matching the current durable `MainProvenanceHash`;
- set `hookInvoked = true` inside `OnBeforeRollbackMainFinalPathGate`;
- assert `hookInvoked`.

They no longer pass merely because invalid temp provenance caused an earlier return.

---

# 6. R13-001 — HIGH — hash-owned helpers refresh byte authority but not path authority after the destructive-boundary hook

This is the strongest fresh finding.

The current per-file delete helper has this shape:

```csharp
if (!File.Exists(path))
{
    return true;
}

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
    ...
    return false;
}

File.Delete(path);
```

This proves the file's bytes after the hook.

It does **not** prove that the string path still resolves through the trusted non-reparse hierarchy after the hook.

---

# 7. Why this is a new gap rather than R12 repeating

R12 correctly required:

```text
global Phase-A verification
race boundary
full path authority
fresh byte authority
mutation
```

The current higher-level callers generally do:

```text
Phase A
higher-level final path gate
Phase B
per-file helper
```

But the per-file helper itself contains a **later race hook**:

```text
higher-level final path gate
-> OnBeforeDeleteFileHook
-> hash
-> delete
```

So the actual sequence is:

```text
path authority proven
HOOK
byte authority proven
delete
```

Path authority is now stale again.

The final mutation boundary still does not have both authorities fresh at the same point.

---

# 8. Deterministic R13-001 reproduction

The existing test infrastructure can prove this without a real junction.

Example:

```csharp
AssetProcessorService.OnBeforeDeleteFileHook =
    path =>
    {
        ValidationService.FileAttributesProvider =
            candidate =>
            {
                if (ValidationService.PathsEqual(
                        candidate,
                        session.AssetFolder))
                {
                    return FileAttributes.Directory
                         | FileAttributes.ReparsePoint;
                }

                return File.GetAttributes(candidate);
            };
    };
```

Call:

```csharp
processor.RollbackMain(session);
```

Higher-level final path validation occurs **before** `OnBeforeDeleteFileHook`.

Then the helper:

```text
hook marks AssetFolder unsafe
does not call path validator
hashes file
deletes file
```

The simulated safety layer now reports the hierarchy unsafe, but the per-file helper never asks.

That is exactly the same reparse-race class the repository has repeatedly chosen to defend against.

---

# 9. R13-001A — Main rollback

`RollbackMain()` now correctly performs a final path gate before Phase B.

However each Phase-B file deletion later calls the hash-owned helper.

The delete helper invokes:

```csharp
OnBeforeDeleteFileHook
```

after that final gate.

Therefore:

```text
final Main path gate safe
delete hook makes AssetFolder/Ingame unsafe
helper checks only bytes
File.Delete
```

is possible.

This affects:

```text
final provenance
root Main
ingame Main
temp Main
temp ingame
temp provenance
```

---

# 10. R13-001B — Reference rollback

Same issue:

```text
RollbackReference final path gate safe
Phase B begins
delete hook makes ReferenceFolder unsafe
hash still matches
helper deletes through now-unsafe hierarchy
```

Affected files:

```text
Reference temp provenance
Reference temp image
canonical Reference provenance
canonical Reference image
```

---

# 11. R13-001C — replacement cleanup

Current cleanup sequence:

```text
backup ownership verification
OnBeforeReplacementCleanupFinalPathGate
ValidateReferenceReplacementTransaction
TryDeleteHashOwnedFileWithError(backup Reference)
TryDeleteHashOwnedFileWithError(backup provenance)
```

The helper's `OnBeforeDeleteFileHook` runs after the transaction safety gate.

If it changes reparse state, the backup deletion still proceeds.

---

# 12. R13-001D — replacement rollback restore

This is the most important path variant.

Current restore helper:

```text
backup exists
destination absent
OnBeforeRestoreFileHook
backup exists
backup SHA matches
File.Move backup -> canonical
```

It does not revalidate:

```text
AssetRootFolder
AssetFolder
ReferenceFolder
backup confinement
destination confinement
```

after the hook.

So the hook can make the Reference hierarchy unsafe and the helper can still restore through it.

---

# 13. Required R13-001 repair principle

At every managed destructive helper:

```text
race/test hook
FULL path safety
FRESH byte authority
destructive operation
```

Do not rely only on the caller's earlier path validation.

---

# 14. Recommended helper design

The cleanest minimal change is to let ownership-aware helpers receive a path-safety callback.

Example:

```csharp
private bool TryDeleteHashOwnedFileWithError(
    string path,
    string expectedHash,
    string description,
    Func<ValidationResult> validatePathSafety,
    ICollection<string> errors)
{
    try
    {
        if (!File.Exists(path))
        {
            return true;
        }

        OnBeforeDeleteFileHook?.Invoke(path);

        var safety =
            validatePathSafety();

        if (!safety.IsValid)
        {
            errors.Add(
                $"{description} at '{path}' was preserved "
                + "because path safety changed before deletion: "
                + string.Join("; ", safety.Errors));

            return false;
        }

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
                $"{description} at '{path}' changed before deletion. "
                + "File preserved.");

            return false;
        }

        File.Delete(path);
        return true;
    }
    catch (Exception ex)
    {
        errors.Add(
            $"Could not delete {description} '{path}': {ex.Message}");
        return false;
    }
}
```

---

# 15. Main / Reference path callbacks

For session-bound operations:

```csharp
ValidationResult ValidateSessionDestructivePathSafety(
    AssetSession session)
{
    return ValidationService
        .ValidateSessionPathsForDestructiveOperation(
            session);
}
```

Use:

```csharp
() =>
    ValidationService
        .ValidateSessionPathsForDestructiveOperation(
            session)
```

inside Main/Reference deletion helpers.

Retain explicit AssetFolder / ingame / reference reparse checks if they add clarity.

---

# 16. Replacement path callback

For replacement rollback/cleanup:

```csharp
() =>
    _validationService
        .ValidateReferenceReplacementTransaction(
            transaction)
```

This validates both old/new session hierarchy plus deterministic temp/backup paths.

Use the same callback for restore.

---

# 17. Restore helper

Required shape:

```csharp
private bool TryRestoreHashOwnedFileWithError(
    string backupPath,
    string destinationPath,
    string expectedHash,
    string description,
    Func<ValidationResult> validatePathSafety,
    ICollection<string> errors)
{
    try
    {
        if (!File.Exists(backupPath))
        {
            ...
            return false;
        }

        if (File.Exists(destinationPath))
        {
            ...
            return false;
        }

        OnBeforeRestoreFileHook?.Invoke(
            backupPath,
            destinationPath);

        var safety =
            validatePathSafety();

        if (!safety.IsValid)
        {
            errors.Add(
                $"{description} was not restored because "
                + "path safety changed before restore: "
                + string.Join("; ", safety.Errors));

            return false;
        }

        if (!File.Exists(backupPath))
        {
            ...
            return false;
        }

        if (File.Exists(destinationPath))
        {
            ...
            return false;
        }

        var actualHash =
            ComputeSha256(backupPath);

        if (!string.Equals(
                actualHash,
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            ...
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
        ...
        return false;
    }
}
```

Note the second destination-existence check after the hook.

`overwrite:false` already prevents replacement, but explicit recheck produces clearer fail-closed diagnostics.

---

# 18. Directory helper

`TryDeleteEmptyDirectoryWithError()` currently has:

```text
directory exists
enumerate empty
OnBeforeDeleteDirectoryHook
Directory.Delete
```

At minimum, callers performing safety-critical cleanup should revalidate the path hierarchy after `OnBeforeDeleteDirectoryHook`.

Recommended:

```text
hook
path safety
directory exists
still empty
not reparse
Directory.Delete
```

This is less severe than file restore/delete but should follow the same invariant.

---

# 19. Mandatory R13-001 tests

All:

```csharp
[Trait("Category", "RecoveryCritical")]
```

## Main

```text
RollbackMain_DeleteHookMakesAssetFolderReparse_PreservesAllFiles
RollbackMain_DeleteHookMakesIngameReparse_PreservesTarget
```

At `OnBeforeDeleteFileHook`:

```text
make current hierarchy reparse via FileAttributesProvider
```

Assert:

```text
rollback invalid
target still exists
no subsequent managed deletions occur after safety failure
Main metadata remains active
```

## Reference

```text
RollbackReference_DeleteHookMakesReferenceFolderReparse_PreservesTarget
```

Assert:

```text
Reference target remains
rollback invalid
```

## Replacement cleanup

```text
ReplacementCleanup_DeleteHookMakesReferenceFolderReparse_PreservesBackups
```

Assert:

```text
backup image exists
backup provenance exists
transaction.IsCommitted == false
```

## Replacement rollback restore

```text
ReplacementRollback_RestoreHookMakesReferenceFolderReparse_NoRestore
```

At:

```csharp
OnBeforeRestoreFileHook
```

make reference folder report reparse.

Assert:

```text
backup remains
canonical target absent
rollback invalid
```

## Directory cleanup

```text
RollbackReference_DirectoryDeleteHookMakesReferenceFolderReparse_NoDirectoryDelete
```

---

# 20. R13-002 — HIGH — legacy session replacement is now inconsistent with the additive/default-compatible strategy

This is an independent current blocker.

`AssetSession.ReferenceProvenanceHash` is nullable:

```csharp
public string? ReferenceProvenanceHash { get; set; }
```

Therefore an older persisted session may legitimately deserialize with:

```text
ReferenceProvenanceHash == null
```

The code deliberately supports this.

---

# 21. Exact validator explicitly contains a legacy fallback

`ValidateExactReferenceProvenanceOwnership()` does:

```text
if ReferenceProvenanceHash exists:
    raw SHA-256 comparison
else:
    render expected provenance
    read actual text
    exact ordinal comparison
```

The source comment even says:

```text
Legacy-session fallback only
```

So a legacy session can legitimately pass exact Reference ownership validation without the new hash field.

---

# 22. Replacement creation accepts the legacy session

`CreateReferenceReplacementTransaction()` first validates the old Reference output.

Because the exact validator has the legacy fallback:

```text
valid old legacy provenance
ReferenceProvenanceHash == null
-> accepted
```

Then the replacement transaction stores:

```csharp
OldSession = oldSession
```

unchanged.

Only `NewSession` receives:

```csharp
ReferenceProvenanceHash = newProvHash
```

No hash is materialized into `OldSession`.

---

# 23. Cleanup then requires a hash which the accepted legacy session does not have

Current `CommitReferenceReplacement()` deletes the OLD provenance backup with:

```csharp
TryDeleteHashOwnedFileWithError(
    transaction.BackupProvenancePath,
    transaction.OldSession.ReferenceProvenanceHash!,
    "backup reference provenance",
    errors);
```

The null-forgiving operator:

```csharp
!
```

only affects compile-time nullable analysis.

It does not create a hash at runtime.

For a legacy session:

```text
expectedHash == null
```

The helper computes:

```text
actualHash = real 64-character SHA
```

and then:

```text
actualHash != null
```

so it refuses deletion.

Result:

```text
backup provenance remains
errors.Count > 0
transaction.IsCommitted == false
CleanupPending cannot complete
```

---

# 24. Rollback has the symmetric failure

Current replacement rollback restores OLD provenance using:

```csharp
TryRestoreHashOwnedFileWithError(
    transaction.BackupProvenancePath,
    transaction.OldSession.ReferenceProvenancePath,
    transaction.OldSession.ReferenceProvenanceHash!,
    "old reference provenance",
    errors);
```

Again, for a legacy session the expected hash is null.

The exact OLD provenance may have been validated correctly via the legacy rendered-text fallback earlier.

But the restore helper later refuses it because there is no stored hash.

Therefore both directions can break:

```text
commit-forward cleanup
rollback-to-old
```

---

# 25. Why this is a production compatibility defect

The authority explicitly requires:

```text
Use additive/default-compatible model changes.
```

Adding a nullable hash field is compatible only if old persisted sessions still remain operational.

The current state is:

```text
legacy session loads
legacy exact validation succeeds
replacement starts
OLD files are backed up
NEW files may be promoted

then:
cleanup/rollback requires a field old state never had
```

That is worse than rejecting replacement at preflight because it can enter a durable transaction that later cannot reconcile automatically.

---

# 26. Required R13-002 architecture

Do **not** weaken the new hash-owned destructive helper.

Instead materialize byte authority for the legacy OLD provenance **before the first replacement mutation**.

---

# 27. Recommended solution A — clone and hydrate OLD transaction authority

After:

```text
ValidateExactReferenceOutput(oldSession)
```

succeeds, derive the actual raw hash:

```csharp
var oldProvHash =
    !string.IsNullOrWhiteSpace(
        oldSession.ReferenceProvenanceHash)
        ? oldSession.ReferenceProvenanceHash
        : ComputeSha256(
            oldSession.ReferenceProvenancePath);
```

Important:

```text
compute this raw hash only AFTER legacy exact rendered-text
ownership has been proven
```

Then clone the old session into the transaction authority:

```csharp
var oldTransactionSession =
    DeepClone(oldSession);

oldTransactionSession.ReferenceProvenanceHash =
    oldProvHash;
```

Use:

```csharp
OldSession = oldTransactionSession;
```

Do not silently mutate the caller's live stable session unless that is intentional.

The replacement journal then carries complete byte authority.

---

# 28. Recommended solution B — explicit transaction field

Alternatively add:

```csharp
public string OldReferenceProvenanceHash { get; set; }
```

to `ReferenceReplacementTransaction` / journal.

At transaction creation:

```text
if old session hash exists:
    copy it
else:
    exact legacy text validation
    compute actual SHA
    store transaction hash
```

Then all replacement backup deletion/restore uses:

```text
transaction.OldReferenceProvenanceHash
```

This makes the distinction between:

```text
legacy stable session schema
replacement transaction byte authority
```

very clear.

Either solution is acceptable.

---

# 29. Do not solve R13-002 by falling back to text equality during deletion

Do **not** change:

```text
hash-owned delete/restore
```

back to:

```text
read text and compare
```

That would reopen the BOM / byte-difference defect already fixed in R11.

Correct order:

```text
legacy exact rendered-text validation once
-> derive raw SHA-256
-> use raw SHA-256 for every destructive transaction operation
```

---

# 30. Mandatory R13-002 tests

## Legacy replacement commit-forward

```text
LegacyReferenceSession_WithoutProvenanceHash_ReplacementCommitSucceeds
```

Build a valid stable Reference session.

Serialize JSON with:

```text
ReferenceProvenanceHash property omitted
```

Load it.

Assert:

```text
loaded.ReferenceProvenanceHash == null
ValidateExactReferenceOutput == valid
```

Then perform:

```text
CreateReferenceReplacementTransaction
CreateReplacementTempFiles
BackupOldReference
PromoteNewReference
CommitReferenceReplacement
```

Expected:

```text
commit valid
backup Reference absent
backup provenance absent
transaction.IsCommitted == true
NEW canonical Reference valid
```

## Legacy replacement rollback

```text
LegacyReferenceSession_WithoutProvenanceHash_ReplacementRollbackRestoresOld
```

Flow:

```text
load legacy old
prepare replacement
backup old
simulate failure before/after promotion
rollback
```

Expected:

```text
rollback valid
OLD image restored
OLD provenance restored
exact OLD provenance valid
```

## Legacy journal recovery

```text
LegacyReferenceSession_ReplacementCleanupPending_StartupRecoveryCompletes
```

This is especially valuable because it proves the durable transaction can recover after restart.

---

# 31. R13-003 — MEDIUM-HIGH — Cancel still has stale byte authority at direct destructive calls

The new ownership-aware helpers live in `AssetProcessorService`.

`SessionService.Cancel()` still performs direct mutation.

This creates a smaller but real version of the same mutation-boundary problem.

---

# 32. Cancel Phase 2 provenance move

Current flow:

```text
ValidateExactReferenceProvenanceOwnership
EnsureCancelPathsAreSafe
File.Move canonical provenance -> cancel temp
```

Byte authority is checked before the path gate.

A process can modify the provenance after exact validation but before `File.Move`.

There is no final raw hash check tied to the move.

---

# 33. Cancel Phase 2 Reference move

Current flow:

```text
SHA-256 canonical Reference
EnsureCancelPathsAreSafe
File.Move canonical Reference -> cancel temp
```

Again:

```text
byte authority
path authority
move
```

rather than:

```text
path authority
fresh byte authority
move
```

The window is narrow but it is the same explicit safety model the rest of the repository now hardens against.

---

# 34. Cancel failure restore

When Reference move fails after provenance was already moved:

```text
ValidateExactReferenceProvenanceOwnership(temp provenance)
File.Move temp provenance -> canonical provenance
```

There is no second path gate between validation and restore and no ownership-aware restore helper.

---

# 35. Cancel Phase 3 delete

Current Phase 3:

```text
hash cancel-temp Reference
exact cancel-temp provenance validation
EnsureCancelPathsAreSafe

File.Delete(temp provenance)
File.Delete(temp Reference)
```

So either temp can change after verification and before deletion.

Unlike `AssetProcessorService`, Cancel does not have a per-file helper which re-hashes immediately before the destructive operation.

---

# 36. Recommended R13-003 repair

Do not duplicate ad-hoc logic.

Extract a narrow reusable destructive file primitive into a service/helper that both:

```text
AssetProcessorService
SessionService
```

can use.

For example:

```text
SafeFileMutation
    DeleteHashOwned(...)
    MoveHashOwned(...)
    RestoreHashOwned(...)
```

Each method should accept:

```text
source/target path
expected raw SHA
path-safety callback
test race hook
```

and enforce:

```text
hook
path safety
fresh hash
destructive operation
```

For provenance, derive/use the persisted hash when available; for legacy Cancel, perform the same one-time exact-text-to-raw-hash materialization principle as R13-002.

---

# 37. R13-003 tests

Add explicit test hooks to SessionService at the final per-file mutation boundary.

Suggested:

```csharp
[ThreadStatic]
internal static Action<string>?
    OnBeforeCancelFileMoveHook;

[ThreadStatic]
internal static Action<string>?
    OnBeforeCancelFileDeleteHook;

[ThreadStatic]
internal static Action<string, string>?
    OnBeforeCancelRestoreHook;
```

Tests:

```text
Cancel_ProvenanceChangesAtMoveBoundary_NoMove
Cancel_ReferenceChangesAtMoveBoundary_NoMove
Cancel_TempProvenanceChangesAtDeleteBoundary_PreservesTemp
Cancel_TempReferenceChangesAtDeleteBoundary_PreservesTemp
Cancel_RestoreProvenanceChangesAtRestoreBoundary_NoRestore
Cancel_PathBecomesReparseAtDeleteBoundary_NoDelete
```

---

# 38. Fresh areas rechecked and currently clean

No additional defect was found in:

```text
[PASS] R12 hash-owned Main local cleanup
[PASS] R12 hash-owned Reference local cleanup
[PASS] R12 hash-owned RollbackMain
[PASS] R12 hash-owned RollbackReference
[PASS] replacement cleanup byte recheck
[PASS] replacement rollback backup image byte recheck
[PASS] replacement rollback backup provenance byte recheck
[PASS] direct helper byte-change test
[PASS] BackupOldReference final path gate
[PASS] BackupOldReference final image SHA
[PASS] BackupOldReference final provenance exact authority
[PASS] BackupOldReference late path/image/provenance tests
[PASS] Cancel full hierarchy check after folder-cleanup hook
[PASS] Cancel AssetRoot late-reparse test
[PASS] RollbackMain final-path hook reach assertions
[PASS] RollbackReference final-path hook reach assertions
[PASS] replacement rollback/cleanup hook reach assertions
[PASS] initial Reference staging authority
[PASS] Main staging authority
[PASS] replacement promotion post-hash gate
[PASS] deterministic transaction filenames
[PASS] current v1.1 provenance hash authority
[PASS] no decoded-text fallback in current v1.1 destructive helpers
```

---

# 39. Recommended repair order

## Phase 1 — R13-002 legacy replacement authority

Fix this first because it can create a durable transaction that becomes unreconcilable.

At replacement creation:

```text
validate legacy old
materialize old raw provenance SHA
persist it in transaction authority
```

Then add commit + rollback + startup recovery tests.

## Phase 2 — R13-001 path-safe per-file helpers

Extend delete/restore helpers so the race hook is followed by:

```text
path validation
byte validation
mutation
```

Then add reparse-at-delete/restore-hook tests.

## Phase 3 — R13-003 Cancel shared safe mutation primitives

Move Cancel's direct managed asset mutations onto the same invariant.

---

# 40. Static verification commands after repair

## No unsafe hash-owned call without current path callback

```powershell
rg -n `
  "TryDeleteHashOwnedFileWithError|TryRestoreHashOwnedFileWithError" `
  src/AssetProvenanceHelper
```

Manual requirement:

```text
each managed call must provide/reach a path-safety validation
after the race hook
```

---

## Legacy provenance hash usage

```powershell
rg -n `
  "OldSession\.ReferenceProvenanceHash!|ReferenceProvenanceHash!" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Any `!` on a legacy-capable OLD provenance authority requires proof that the hash has been materialized earlier in the replacement transaction.

---

## Transaction creation

```powershell
rg -n `
  "CreateReferenceReplacementTransaction|OldSession =|ReferenceProvenanceHash" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Required:

```text
legacy old session
-> exact ownership validation
-> raw hash materialized
-> transaction Old authority contains hash
```

---

## Cancel direct mutation

```powershell
rg -n `
  "File\.Move|File\.Delete|Cancel\(" `
  src/AssetProvenanceHelper/Services/SessionService.cs
```

Every managed Reference/provenance move/delete should have:

```text
fresh path authority
fresh byte authority
```

at the mutation boundary.

---

# 41. Required Windows execution gate after repair

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

# 42. Definition of Done for the next pass

## Preserve R12

```text
[ ] hash-owned deletion remains
[ ] hash-owned restore remains
[ ] byte race hook is before final hash
[ ] BackupOldReference second path gate remains
[ ] BackupOldReference second image/provenance authority remains
[ ] Cancel full hierarchy post-folder-hook validation remains
[ ] R12 hook-reach assertions remain
```

## R13-001

```text
[ ] delete helper revalidates path safety after delete hook
[ ] restore helper revalidates path safety after restore hook
[ ] directory cleanup revalidates path safety after directory hook
[ ] Main delete-hook reparse test passes
[ ] Reference delete-hook reparse test passes
[ ] replacement cleanup delete-hook reparse test passes
[ ] replacement restore-hook reparse test passes
```

## R13-002

```text
[ ] legacy stable session without ReferenceProvenanceHash remains accepted
[ ] replacement transaction materializes OLD raw provenance hash
[ ] commit-forward works for legacy OLD session
[ ] rollback works for legacy OLD session
[ ] CleanupPending startup recovery works for legacy OLD session
[ ] no destructive text-equality fallback is reintroduced
```

## R13-003

```text
[ ] Cancel provenance move uses fresh byte + path authority
[ ] Cancel Reference move uses fresh byte + path authority
[ ] Cancel failure restore uses fresh byte + path authority
[ ] Cancel Phase-3 provenance delete uses fresh byte + path authority
[ ] Cancel Phase-3 Reference delete uses fresh byte + path authority
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

# 43. Final thirteenth-pass conclusion

The `bugs12.md` repair is genuine and materially improves the repository.

The remaining architecture is now very close to the desired invariant, but two gaps still prevent a source-level clean verdict:

1. The per-file mutation helpers now refresh **bytes** after their hook but not **path safety** after that same hook.
2. Replacement still accepts legacy provenance via its explicit compatibility fallback but later assumes the legacy OLD session has a modern provenance hash.

The correct final model is:

```text
legacy compatibility:
    validate legacy exact content once
    materialize raw transaction hash

destructive operation:
    hook / race boundary
    full current path authority
    fresh exact byte authority
    mutation
```

**Current acceptance state: FAIL — R12 is materially closed, but R13-001 and R13-002 remain blockers.**

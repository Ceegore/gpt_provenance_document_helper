# AI Asset Provenance Helper — Second Paranoid Retest & Repair Guide

**File:** `bugs2.md`  
**Audit date:** 2026-08-18  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `2732ae1213815231157424a2590dad343612caa3`  
**Latest commit message:** `Fix all issues from bugs1(1).md (BUG-001 through BUG-024) and harden test suite`  
**Previous audit:** `bugs1.md` / committed copy `bugs1(1).md`  
**Rework authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — do not declare the tool zero-defect or fully accepted yet.**

The repair commit fixes a large majority of the previously reported issues. This retest confirms real improvements in:

- `ingame` path/reparse validation;
- failed-Main reconciliation;
- NoReference journal-deletion handling;
- extensionless Asset Name validation;
- image magic-byte validation;
- UI sizing;
- independent validation visuals;
- help-overlay modality;
- Ctrl+R replacement;
- button-sized drop targets;
- provenance retention wording;
- README file-tree corrections;
- deterministic `IngameFilename` derivation;
- status diagnostics;
- Refresh enumeration error handling;
- longer smoke startup timeout;
- provenance digest fields.

However, the repair introduced or left unresolved several **crash/recovery defects**. The most serious are:

1. the persisted Reference-replacement journal is currently treated as trusted destructive instructions and can make recovery delete or overwrite paths without first proving path confinement and ownership;
2. Reference-replacement journal phases are written **after** `PrepareReferenceReplacement()` has already performed the canonical filesystem mutations, so the durable phase can describe a state that never matches disk;
3. the new prepared-Reference session is persisted before writes, but startup performs normal completed-session validation **before** it checks `ReferenceCommitPhase.Prepared`, making the new prepared-session recovery branch unreachable during the exact crash windows it is supposed to handle;
4. a valid completed Main can be deleted during recovery if the Reference provenance was modified in a way that still passes the weak precheck;
5. a valid completed Main can be classified as incomplete and rolled back after a template change because `ValidateCompleteAsset()` ignores the stored `MainProvenanceHash` and reconstructs provenance from the current template.

These are release blockers because they concern durable state, destructive rollback, and recoverability.

---

# 0.2 Execution limitation

This was a fresh source/state-machine/test/CI audit of the current `main` commit.

A real WinForms execution pass was **not** run from this environment because:

- the application targets `net8.0-windows`;
- the repository pins .NET SDK `8.0.418`;
- the available analysis runtime is not the required Windows/.NET environment;
- the connected GitHub status surface did not expose a successful status for the audited commit.

This limitation is **not** being used as a blocker by itself. Static and structural defects are still provable and are documented below.

After the fixes in this document, the Windows Debug/Release/20×/publish/smoke/manual-DPI gates in section 14 must still be executed before final acceptance.

---

# 1. Status of every previous `bugs1.md` finding

| Previous ID | Retest status | Result |
|---|---|---|
| BUG-001 `ingame` destructive path/reparse validation | **FIXED structurally** | `ingame`, Main/final/temp paths now participate in destructive-path validation. |
| BUG-002 failed-Main reconciliation helper | **FIXED** | `TryReconcileFailedMainCommit()` now exists and fail-closes on unreconciled state. |
| BUG-003 NoReference journal deletion failure swallowed | **FIXED** | deletion failure is surfaced as critical and the app closes. |
| BUG-004 complete-asset final provenance substring check | **PARTIAL** | exact text comparison was added, but it ignores durable `MainProvenanceHash`; see R2-005. |
| BUG-005 Reference provenance only weakly checked before Main | **FIXED for normal Main start** | Main now calls `ValidateExactReferenceOutput()`. Recovery still has a destructive edge; see R2-004. |
| BUG-006 initial Reference not hard-crash safe | **NOT FIXED end-to-end** | prepared journal exists, but startup rejects it before prepared recovery; see R2-003. |
| BUG-007 Reference replacement not persistent | **NOT FIXED safely** | a journal exists, but phase ordering and recovery validation are unsafe; see R2-001/R2-002. |
| BUG-008 1366×768 target impossible | **STATIC FIX LANDED** | min/default sizing is now 960×680 / 1100×740. Actual DPI matrix still needs Windows execution. |
| BUG-009 Reference accepts image extension as Asset Name | **FIXED** | unified `ValidateAssetName()` is used. |
| BUG-010 button-sized Drop control missing | **FUNCTIONALLY FIXED** | separate drop buttons exist. Text is `Drop File`, not the literal requested `Drop file here`. |
| BUG-011 Ctrl+R cannot Replace Reference | **FIXED** | Ctrl+R now dispatches by UI state. |
| BUG-012 unrelated validation visual gets cleared | **PARTIAL** | normal text/selection changes improved; Paste/Clear still clear unrelated Main state; see R2-007. |
| BUG-013 help overlay does not block underlying UI | **FIXED** | main content is disabled while overlay is visible. |
| BUG-014 persisted redundant `IngameFilename` | **FIXED** | value is derived through `GetIngameFilename()`. |
| BUG-015 `ProcessMainImage()` can write without active journal | **NOT FIXED** | it silently creates an in-memory transaction and continues; see R2-006. |
| BUG-016 reference record says retained=no | **FIXED** | template now says `Reference file retained: yes`. |
| BUG-017 README wrong tree/shortcuts | **FIXED materially** | canonical filenames and shortcuts were corrected. Minor “toggle help” wording remains. |
| BUG-018 CI/tests omit important new surfaces | **PARTIAL** | coverage list improved and a fix suite was added, but dangerous crash-state tests remain missing; see R2-008. |
| BUG-019 fake image with image extension accepted | **FIXED to signature level** | PNG/JPEG/WebP magic bytes are checked. |
| BUG-020 Refresh enumeration errors can escape | **FIXED** | common I/O/access exceptions are handled in the UI path. |
| BUG-021 ownership depends entirely on mutable templates | **PARTIAL** | provenance hashes were added, but complete-asset validation still reconstructs current-template text; see R2-005. |
| BUG-022 smoke timeout too short | **PARTIAL/FIXED core issue** | timeout is 15 s. Icon verification remains warning-only; see R2-009. |
| BUG-023 legacy selection wrappers/aliases remain | **FIXED** | active legacy wrappers/aliases are removed. |
| BUG-024 missing status diagnostics | **MOSTLY FIXED** | key statuses were added. |

**Bottom line:** the repair round is substantial, but the claim “BUG-001 through BUG-024 fixed” is not yet correct.

---

# 2. Current defect summary

| ID | Severity | Area | Summary |
|---|---|---|---|
| R2-001 | **CRITICAL** | replacement recovery | `reference-replacement.json` paths/phases are not validated before destructive recovery; recovery can delete/overwrite untrusted paths |
| R2-002 | **HIGH** | replacement durability | replacement journal is persisted only after `PrepareReferenceReplacement()` has already mutated canonical files; phase journal can contradict disk |
| R2-003 | **HIGH** | initial Reference recovery | `ReferenceCommitPhase.Prepared` recovery is unreachable because normal `ValidateSession()` runs first |
| R2-004 | **HIGH** | recovery/data loss | subtle Reference-provenance tampering can cause a valid completed Main to be rolled back/deleted |
| R2-005 | **HIGH** | provenance/recovery | `ValidateCompleteAsset()` ignores stored `MainProvenanceHash`; template changes can cause deletion of a valid completed Main |
| R2-006 | **MEDIUM** | service API safety | public Main/Reference convenience paths can still perform writes without a durable pre-write journal |
| R2-007 | **MEDIUM** | validation UX | Paste/Clear Prompt still clear unrelated Main-image validation state |
| R2-008 | **MEDIUM** | test quality | new “BUG-001 through BUG-024” test suite does not actually exercise the dangerous crash/recovery states |
| R2-009 | **LOW** | smoke gate | app-icon verification remains warning-only and startup duration is not recorded |
| R2-010 | **LOW** | exact UI spec | button text is `Drop File`, not the requested `Drop file here` |

**Required before zero-defect acceptance:** R2-001 through R2-008.  
R2-009/R2-010 should be repaired in the same pass because they are small.

---

# 3. R2-001 — CRITICAL — replacement journal is trusted as a destructive instruction file

## 3.1 Evidence

Affected files:

```text
src/AssetProvenanceHelper/MainForm.Recovery.cs
src/AssetProvenanceHelper/Services/SessionService.cs
src/AssetProvenanceHelper/Services/ValidationService.Session.cs
src/AssetProvenanceHelper/Models/ReferenceReplacementJournal.cs
```

Startup loads:

```csharp
journal = _sessionService.LoadReplacementJournal();
```

`LoadReplacementJournal()` only deserializes JSON. It does not prove that:

- `OldSession.AssetFolder` is the exact child of its root;
- `NewSession.AssetFolder` is the same asset;
- backup paths are deterministic paths generated from the transaction ID;
- temp paths are deterministic children of `reference/`;
- Reference canonical paths are confined to `reference/`;
- the phase is a defined enum value;
- the journal TransactionId is a valid 32-character hex identifier;
- the root/asset/reference folders have not become reparse points.

Recovery then performs mutations such as:

```csharp
File.Delete(journal.TempNewReferencePath);
File.Delete(journal.TempNewProvenancePath);

File.Move(
    journal.BackupProvenancePath,
    journal.OldSession.ReferenceProvenancePath,
    overwrite: true);

File.Move(
    journal.BackupReferencePath,
    journal.OldSession.ReferenceDestinationPath,
    overwrite: true);
```

Other branches delete:

```text
BackupReferencePath
BackupProvenancePath
NewSession.ReferenceDestinationPath
NewSession.ReferenceProvenancePath
```

without proving every target belongs to this transaction immediately before deletion.

## 3.2 Why this is CRITICAL

The replacement journal is a normal local JSON file.

If it is corrupt, partially hand-edited, restored from an inconsistent backup, or deliberately tampered with, the recovery routine can treat arbitrary path strings as authorized deletion/overwrite targets.

This violates the tool’s strongest existing invariant:

> A persisted session/journal is not itself proof of ownership. Paths must be deterministic, confined, and content ownership must be proven before destructive mutation.

The use of `overwrite: true` is especially dangerous. A foreign file that appears at a canonical destination after a crash must never be overwritten merely because a journal expects a file there.

## 3.3 Required architecture

Add a strict validator:

```csharp
public ValidationResult ValidateReferenceReplacementJournal(
    ReferenceReplacementJournal journal)
```

The validator must prove **all** of the following before any recovery mutation:

1. `journal != null`.
2. `ReferenceReplacementPhase` is defined.
3. `TransactionId` is exactly 32 hex characters.
4. both sessions are `ReferenceAssisted`.
5. both sessions have the same:
   - normalized `AssetRootFolder`;
   - `AssetFolderName`;
   - normalized `AssetFolder`;
   - `ProjectName`.
6. `AssetFolder == AssetRootFolder + AssetFolderName`.
7. Reference folder is exactly:

```text
<AssetFolder>/reference
```

8. old/new Reference filenames contain filenames only.
9. old/new canonical Reference paths are exactly under the Reference folder.
10. old/new provenance path is exactly the fixed Reference provenance path.
11. backup paths are exactly:

```text
<OldReferencePath>.<TransactionId>.old
<ReferenceProvenancePath>.<TransactionId>.old
```

12. temp paths are exactly:

```text
<ReferenceFolder>/.__new_reference_<TransactionId><new extension>
<ReferenceFolder>/.__new_provenance_<TransactionId>.tmp
```

13. existing Root, Asset and Reference folders are not reparse points.
14. no path escapes by normalization.

## 3.4 Copy-ready validator skeleton

```csharp
public ValidationResult ValidateReferenceReplacementJournal(
    ReferenceReplacementJournal journal)
{
    ArgumentNullException.ThrowIfNull(journal);

    var errors = new List<string>();

    if (!Enum.IsDefined(typeof(ReferenceReplacementPhase), journal.Phase))
    {
        errors.Add($"Unknown replacement phase '{journal.Phase}'.");
    }

    if (string.IsNullOrWhiteSpace(journal.TransactionId)
        || journal.TransactionId.Length != 32
        || journal.TransactionId.Any(c => !Uri.IsHexDigit(c)))
    {
        errors.Add(
            "Replacement TransactionId must be exactly 32 hexadecimal characters.");
    }

    if (journal.OldSession is null || journal.NewSession is null)
    {
        errors.Add("Replacement journal OldSession/NewSession is missing.");
        return ValidationResult.Failure(errors);
    }

    var oldSession = journal.OldSession;
    var newSession = journal.NewSession;

    var oldPathValidation =
        ValidateSessionPathsForDestructiveOperation(oldSession);

    if (!oldPathValidation.IsValid)
    {
        errors.AddRange(
            oldPathValidation.Errors.Select(
                e => "OldSession: " + e));
    }

    var newPathValidation =
        ValidateSessionPathsForDestructiveOperation(newSession);

    if (!newPathValidation.IsValid)
    {
        errors.AddRange(
            newPathValidation.Errors.Select(
                e => "NewSession: " + e));
    }

    if (oldSession.WorkflowMode != AssetWorkflowMode.ReferenceAssisted
        || newSession.WorkflowMode != AssetWorkflowMode.ReferenceAssisted)
    {
        errors.Add(
            "Reference replacement journal must contain ReferenceAssisted sessions.");
    }

    if (!PathsEqual(
            oldSession.AssetRootFolder,
            newSession.AssetRootFolder))
    {
        errors.Add("Old/New AssetRootFolder mismatch.");
    }

    if (!string.Equals(
            oldSession.AssetFolderName,
            newSession.AssetFolderName,
            StringComparison.Ordinal))
    {
        errors.Add("Old/New AssetFolderName mismatch.");
    }

    if (!PathsEqual(
            oldSession.AssetFolder,
            newSession.AssetFolder))
    {
        errors.Add("Old/New AssetFolder mismatch.");
    }

    if (!string.Equals(
            oldSession.ProjectName,
            newSession.ProjectName,
            StringComparison.Ordinal))
    {
        errors.Add("Old/New ProjectName mismatch.");
    }

    if (errors.Count != 0)
    {
        return ValidationResult.Failure(errors);
    }

    var referenceFolder =
        NormalizePath(
            Path.Combine(
                oldSession.AssetFolder,
                AppConstants.ReferenceFolderName));

    if (Directory.Exists(referenceFolder)
        && IsReparsePoint(referenceFolder))
    {
        errors.Add(
            "Replacement reference folder is a reparse point.");
    }

    var expectedBackupReference =
        NormalizePath(
            oldSession.ReferenceDestinationPath
            + "."
            + journal.TransactionId
            + ".old");

    var expectedBackupProvenance =
        NormalizePath(
            oldSession.ReferenceProvenancePath
            + "."
            + journal.TransactionId
            + ".old");

    var newExtension =
        Path.GetExtension(newSession.ReferenceFilename);

    var expectedTempReference =
        NormalizePath(
            Path.Combine(
                referenceFolder,
                $".__new_reference_{journal.TransactionId}{newExtension}"));

    var expectedTempProvenance =
        NormalizePath(
            Path.Combine(
                referenceFolder,
                $".__new_provenance_{journal.TransactionId}.tmp"));

    if (!PathsEqual(
            journal.BackupReferencePath,
            expectedBackupReference))
    {
        errors.Add(
            "BackupReferencePath does not match deterministic transaction path.");
    }

    if (!PathsEqual(
            journal.BackupProvenancePath,
            expectedBackupProvenance))
    {
        errors.Add(
            "BackupProvenancePath does not match deterministic transaction path.");
    }

    if (!PathsEqual(
            journal.TempNewReferencePath,
            expectedTempReference))
    {
        errors.Add(
            "TempNewReferencePath does not match deterministic transaction path.");
    }

    if (!PathsEqual(
            journal.TempNewProvenancePath,
            expectedTempProvenance))
    {
        errors.Add(
            "TempNewProvenancePath does not match deterministic transaction path.");
    }

    return errors.Count == 0
        ? ValidationResult.Success()
        : ValidationResult.Failure(errors);
}
```

## 3.5 Never overwrite an unknown canonical destination

Replace patterns like:

```csharp
File.Move(
    backup,
    destination,
    overwrite: true);
```

with a helper that:

1. validates backup ownership;
2. if destination is absent, restores;
3. if destination exists and already exactly matches the intended restored object, deletes only the owned backup;
4. otherwise fails closed.

Example:

```csharp
private void RestoreReferenceImageFailClosed(
    string backupPath,
    string destinationPath,
    string expectedHash)
{
    if (!File.Exists(backupPath))
    {
        return;
    }

    var backupHash =
        ValidationService.ComputeSha256(backupPath);

    if (!string.Equals(
            backupHash,
            expectedHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Reference backup hash does not match journal authority.");
    }

    if (File.Exists(destinationPath))
    {
        var destinationHash =
            ValidationService.ComputeSha256(destinationPath);

        if (!string.Equals(
                destinationHash,
                expectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Destination '{destinationPath}' contains unknown content. "
                + "Refusing to overwrite it.");
        }

        // Destination already is the desired old reference.
        File.Delete(backupPath);
        return;
    }

    File.Move(
        backupPath,
        destinationPath,
        overwrite: false);
}
```

Equivalent provenance restoration should use the stored provenance digest.

## 3.6 Unknown phase must fail closed

Current recovery switch needs a `default`.

```csharp
default:
    throw new InvalidDataException(
        $"Unknown ReferenceReplacementPhase value: {(int)journal.Phase}");
```

Do not silently continue normal session recovery.

## 3.7 Regression tests

Add:

```csharp
[Fact]
public void ReplacementRecovery_TamperedPreparedTempPath_FailsClosed()
{
    // Create a valid journal, then replace TempNewReferencePath
    // with a foreign file outside the asset tree.
    // Invoke replacement recovery.
    // Assert:
    // - foreign file remains byte-identical;
    // - replacement journal remains;
    // - app enters critical/closed recovery path.
}
```

```csharp
[Fact]
public void ReplacementRecovery_TamperedBackupPath_FailsClosed()
{
    // Point BackupReferencePath outside managed reference folder.
    // Assert no external file deletion/move.
}
```

```csharp
[Fact]
public void ReplacementRecovery_UnknownPhase_FailsClosed()
{
    // Serialize numeric phase 999.
    // Assert journal remains and no normal session mutation follows.
}
```

```csharp
[Fact]
public void ReplacementRecovery_ForeignCanonicalDestination_IsNeverOverwritten()
{
    // Valid old backup, but place foreign bytes at old canonical path.
    // Assert recovery refuses overwrite and preserves both files.
}
```

```csharp
[Fact]
public void ReplacementRecovery_ReferenceFolderReparse_FailsClosed()
{
    // Windows-only junction/symlink test.
    // Assert no file behind junction is deleted or overwritten.
}
```

---

# 4. R2-002 — HIGH — replacement phases are persisted after the mutations already happened

## 4.1 Current flow

`HandleReplaceReference()` currently calls:

```csharp
transaction =
    _assetProcessorService.PrepareReferenceReplacement(...);
```

Only **after that method returns** does it write:

```csharp
SaveReplacementJournal(Prepared);
SaveReplacementJournal(OldBackedUp);
SaveReplacementJournal(NewPromoted);
```

But `PrepareReferenceReplacement()` already does all of these operations before it returns:

```text
copy new temp reference
write new temp provenance
move old Reference -> backup
move old provenance -> backup
promote new Reference -> canonical
promote new provenance -> canonical
build/validate NewSession
return transaction
```

Therefore the first durable `Prepared` phase is not a write-ahead journal at all.

## 4.2 Concrete crash scenarios

### Crash A — after old files moved, before `PrepareReferenceReplacement()` returns

Disk:

```text
old files in .old backups
new temp files present
session.json = old
replacement journal = DOES NOT EXIST
```

There is no durable recovery authority.

### Crash B — immediately after `PrepareReferenceReplacement()` returns

Disk:

```text
new canonical files promoted
old backups still present
session.json = old
replacement journal = DOES NOT EXIST
```

Again, no durable replacement authority.

### Crash C — after `Prepared` journal save, before `OldBackedUp` save

Disk is already `NewPromoted`, but journal says:

```text
Prepared
```

Current `Prepared` recovery only deletes temp paths and deletes the journal.

That recovery interpretation is false.

### Crash D — after `OldBackedUp` journal save

Disk is already `NewPromoted`, journal says `OldBackedUp`.

Current recovery can restore old files with overwrite and leave promoted new files orphaned.

## 4.3 Required design: real write-ahead phases

Do not keep a method named “Prepare” that performs the entire mutation.

Split metadata preparation from phase execution.

Recommended enum:

```csharp
public enum ReferenceReplacementPhase
{
    Prepared = 0,
    OldBackupPending = 1,
    OldBackedUp = 2,
    NewPromotionPending = 3,
    NewPromoted = 4,
    SessionSwitchPending = 5,
    SessionSwitched = 6,
    CleanupPending = 7
}
```

### Required sequence

```text
1. Validate old output + new source.
2. Generate transaction ID.
3. Derive all canonical/temp/backup paths.
4. Hash new source.
5. Render/hash new provenance in memory.
6. Build OldSession/NewSession journal authority.
7. SAVE phase Prepared.
8. Create/verify temp new files.
9. SAVE OldBackupPending.
10. Move old canonicals to deterministic backups.
11. SAVE OldBackedUp.
12. SAVE NewPromotionPending.
13. Promote new canonicals.
14. SAVE NewPromoted.
15. SAVE SessionSwitchPending.
16. Save NewSession to session.json.
17. SAVE SessionSwitched.
18. Verify NewSession exact output.
19. SAVE CleanupPending.
20. Verify and delete old backups.
21. Delete replacement journal.
```

The “Pending” phases are intentional: they make the durable record describe the **next authorized mutation** before it occurs.

Recovery for a Pending phase must tolerate both:

- mutation not yet started;
- mutation completed but crash occurred before the following phase save.

## 4.4 Recommended API split

Replace:

```csharp
PrepareReferenceReplacement(...)
```

with:

```csharp
ReferenceReplacementTransaction CreateReferenceReplacementTransaction(
    AssetSession oldSession,
    IReadOnlyCollection<string> acceptedExtensions,
    string newSourceImagePath,
    DateTimeOffset processedAt);
```

This method must perform **no filesystem mutation** except reading/hashing the source.

Then:

```csharp
void CreateReplacementTempFiles(
    ReferenceReplacementTransaction transaction,
    IReadOnlyCollection<string> acceptedExtensions);

void BackupOldReference(
    ReferenceReplacementTransaction transaction);

void PromoteNewReference(
    ReferenceReplacementTransaction transaction);

ValidationResult CleanupReplacementBackups(
    ReferenceReplacementTransaction transaction);
```

The UI/workflow or a dedicated coordinator persists the phase before/after each operation.

## 4.5 Do not delete the journal blindly in outer catch

Current pattern:

```csharp
catch (Exception ex)
{
    if (transaction != null && !transaction.IsCommitted)
    {
        try
        {
            _sessionService.DeleteReplacementJournal();
        }
        catch
        {
        }
    }

    ShowError(...);
}
```

This is unsafe. If an exception occurs after a canonical mutation, the journal is exactly what startup needs.

Required rule:

> Once a replacement journal has been durably created, it is deleted only after either (a) verified full commit + cleanup, or (b) verified full rollback.

On an unresolved exception:

```csharp
Show critical error;
Close application;
Preserve journal.
```

## 4.6 Mandatory crash tests

Create an injectable phase hook and tests for **every boundary**:

```text
ReplacementCrash_AfterPreparedSave
ReplacementCrash_AfterTempReferenceCopy
ReplacementCrash_AfterTempProvenanceWrite
ReplacementCrash_AfterOldBackupIntentSave
ReplacementCrash_AfterOldReferenceMove
ReplacementCrash_AfterOldProvenanceMove
ReplacementCrash_AfterOldBackedUpSave
ReplacementCrash_AfterPromotionIntentSave
ReplacementCrash_AfterNewReferencePromote
ReplacementCrash_AfterNewProvenancePromote
ReplacementCrash_AfterNewPromotedSave
ReplacementCrash_AfterSessionSwitchIntent
ReplacementCrash_AfterSessionJsonSave
ReplacementCrash_AfterSessionSwitchedSave
ReplacementCrash_AfterOneBackupDelete
ReplacementCrash_AfterCleanupPending
```

Every test should instantiate a fresh application/recovery object after the injected interruption.

---

# 5. R2-003 — HIGH — prepared Reference recovery is unreachable

## 5.1 Good change that landed

Initial Reference now correctly prepares a session and saves it before writing:

```text
CreateReferenceSession(...)
Save(preparedSession)
ProcessReference(...)
Save(completedSession)
```

That is the right architecture direction.

## 5.2 The startup ordering bug

Startup currently performs:

```csharp
session = _sessionService.Load();

var validation =
    _validationService.ValidateSession(session);

if (!validation.IsValid)
{
    // prompt to delete only session record or exit
    return;
}
```

Only later does it call:

```csharp
RecoverReferenceAssistedSession(session);
```

And only inside that method is:

```csharp
if (session.ReferenceCommitPhase
    == ReferenceCommitPhase.Prepared)
{
    ...
}
```

A freshly saved prepared Reference session intentionally exists **before**:

```text
AssetFolder
Reference image
Reference provenance
```

Normal `ValidateSession()` requires those completed artifacts.

Therefore the prepared journal is rejected as “Invalid unfinished session” before the intended prepared-session recovery code can run.

## 5.3 Dangerous effect

Hard crash after:

```text
Save(preparedSession)
```

but before any output:

- startup offers delete session record;
- prepared recovery is never reached.

Hard crash after only the image was copied:

- startup rejects the session;
- deleting only `session.json` orphans a managed image/folder.

Hard crash after both outputs exist but before final session save:

- normal session validation may still reject because the phase is prepared/inconsistent;
- intended exact completion promotion can be bypassed.

## 5.4 Required startup ordering

Branch on the durable transaction type **before normal completed-state validation**.

Recommended:

```csharp
private void RecoverSessionOnStartup()
{
    if (!RecoverReferenceReplacementJournalIfPresent())
    {
        return;
    }

    if (!_sessionService.Exists())
    {
        return;
    }

    var session = LoadSessionOrHandleBroken();
    if (session is null)
    {
        return;
    }

    if (session.ReferenceCommitPhase
        == ReferenceCommitPhase.Prepared)
    {
        RecoverPreparedReferenceSession(session);
        return;
    }

    if (session.CancelPhase != CancelPhase.None)
    {
        RecoverCancellation(session);
        return;
    }

    // Only normal stable sessions reach full ValidateSession.
    var validation =
        _validationService.ValidateSession(session);

    if (!validation.IsValid)
    {
        HandleInvalidStableSession(session, validation);
        return;
    }

    ...
}
```

## 5.5 Add a prepared-session structural validator

Do not use the completed-session validator.

```csharp
public ValidationResult ValidatePreparedReferenceSession(
    AssetSession session)
{
    var errors = new List<string>();

    if (session.WorkflowMode
        != AssetWorkflowMode.ReferenceAssisted)
    {
        errors.Add(
            "Prepared Reference session has wrong WorkflowMode.");
    }

    if (session.ReferenceCommitPhase
        != ReferenceCommitPhase.Prepared)
    {
        errors.Add(
            "Prepared Reference session has wrong phase.");
    }

    if (string.IsNullOrWhiteSpace(
            session.ReferenceTransactionId)
        || session.ReferenceTransactionId.Length != 32
        || session.ReferenceTransactionId.Any(
            c => !Uri.IsHexDigit(c)))
    {
        errors.Add(
            "Prepared Reference transaction ID is invalid.");
    }

    var pathValidation =
        ValidateSessionPathsForDestructiveOperation(session);

    if (!pathValidation.IsValid)
    {
        errors.AddRange(pathValidation.Errors);
    }

    if (string.IsNullOrWhiteSpace(session.ReferenceHash)
        || session.ReferenceHash.Length != 64)
    {
        errors.Add(
            "Prepared ReferenceHash is missing.");
    }

    if (string.IsNullOrWhiteSpace(
            session.ReferenceProvenanceHash)
        || session.ReferenceProvenanceHash.Length != 64)
    {
        errors.Add(
            "Prepared ReferenceProvenanceHash is missing.");
    }

    return errors.Count == 0
        ? ValidationResult.Success()
        : ValidationResult.Failure(errors);
}
```

This validator must allow:

```text
AssetFolder absent
Reference folder absent
Reference image absent
Reference provenance absent
```

because those are valid Prepared states.

## 5.6 Normal exception bug in `HandleReference`

There is another issue in the same path.

`createdSession` is assigned only if:

```csharp
ProcessReference(...)
```

returns successfully.

If `ProcessReference()` throws after the prepared journal was saved:

```csharp
createdSession == null
```

so the catch does not reliably remove/reconcile the prepared journal.

The app then remains interactive.

If ProcessReference rollback was incomplete, continuing to use the application can later overwrite the durable journal that is needed for recovery.

### Required pattern

Keep the prepared session outside the try:

```csharp
AssetSession? preparedSession = null;

try
{
    preparedSession =
        _assetProcessorService.CreateReferenceSession(...);

    _sessionService.Save(preparedSession);

    var completed =
        _assetProcessorService.ProcessReference(
            preparedSession,
            settings,
            sourceImage,
            now);

    _sessionService.Save(completed);

    ...
}
catch (Exception ex)
{
    if (preparedSession is not null)
    {
        var reconciliation =
            ReconcilePreparedReferenceFailure(
                preparedSession);

        if (!reconciliation.IsValid)
        {
            ShowCritical(...);
            Close();
            return;
        }
    }

    ShowError(...);
}
```

If rollback and journal cleanup are not provably complete, **close** and preserve the journal.

## 5.7 Regression matrix

```text
PreparedReference_CrashBeforeAnyDirectory
PreparedReference_CrashAfterAssetFolderCreated
PreparedReference_CrashAfterReferenceFolderCreated
PreparedReference_CrashAfterImageCopy
PreparedReference_CrashAfterProvenanceWrite
PreparedReference_CrashAfterExactValidationBeforeStableSave
PreparedReference_NormalException_RollbackAndJournalCleanup
PreparedReference_RollbackIncomplete_ClosesAndPreservesJournal
PreparedReference_TamperedImage_FailsClosed
PreparedReference_TamperedProvenance_FailsClosed
PreparedReference_ReparseReferenceFolder_FailsClosed
```

---

# 6. R2-004 — HIGH — subtle Reference-provenance tampering can delete a valid completed Main

## 6.1 Current recovery logic

For an active Reference-assisted Main journal, recovery first runs:

```csharp
var refBaselineValidation =
    _validationService.ValidateReferenceOutput(session);
```

`ValidateReferenceOutput()` uses Reference provenance **substring** checks.

A file modified by appending:

```text
TAMPERED
```

still contains:

```text
Asset ID
Project
Generation date
```

and therefore passes the baseline.

Recovery then calls `ValidateCompleteAsset()`.

That method performs exact Reference provenance ownership and fails.

The recovery code treats any failed complete-asset validation as an incomplete Main and calls:

```csharp
RollbackMain(...)
```

`RollbackMain()` can then delete:

```text
root Main
ingame Main
final provenance
```

even though all three Main outputs are valid and owned.

## 6.2 Why this is wrong

Reference-provenance corruption and incomplete-Main state are different failure classes.

A suspicious Reference file must cause:

```text
FAIL CLOSED
PRESERVE MAIN OUTPUTS
PRESERVE JOURNAL
STOP/ASK USER
```

It must not automatically convert into:

```text
DELETE VALID MAIN OUTPUTS
```

## 6.3 Minimal safe fix

Use exact Reference baseline before complete-Main classification:

```csharp
var refBaselineValidation =
    _validationService.ValidateExactReferenceOutput(
        session,
        _templateService);

if (!refBaselineValidation.IsValid)
{
    ShowMessageBox(
        "The Reference provenance is inconsistent or modified. "
        + "Main output files were preserved and no rollback was attempted."
        + Environment.NewLine
        + Environment.NewLine
        + string.Join(
            Environment.NewLine,
            refBaselineValidation.Errors),
        "Reference integrity problem",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);

    Close();
    return;
}
```

Only once the exact Reference baseline is trusted should Main completeness determine whether `RollbackMain()` is appropriate.

Also replace the later stable-resume:

```csharp
ValidateReferenceOutput(session)
```

with exact validation.

## 6.4 Regression test

```csharp
[Fact]
public void Recovery_CompletedMain_ReferenceProvenanceAppended_PreservesMain()
{
    // 1. Create Reference.
    // 2. Prepare/persist Main transaction.
    // 3. Complete Main but leave session journal.
    // 4. Append "\nTAMPERED" to Reference provenance while preserving
    //    all required substrings.
    // 5. Invoke startup recovery.
    //
    // Assert:
    // - root Main still exists and hash matches MainHash;
    // - ingame Main still exists and hash matches MainHash;
    // - final provenance still exists and matches MainProvenanceHash;
    // - session/recovery authority is preserved;
    // - Reference corruption is reported;
    // - no Main rollback occurred.
}
```

This test is materially different from a test that replaces the entire Reference provenance with text missing all tokens.

---

# 7. R2-005 — HIGH — complete-asset validation ignores the durable Main provenance digest

## 7.1 Improvement that landed

`AssetSession` now contains:

```csharp
ReferenceProvenanceHash
MainProvenanceHash
```

`ValidateExactFinalProvenanceOwnership()` can verify `MainProvenanceHash`.

That is exactly the right direction.

## 7.2 Remaining defect

`ValidateCompleteAsset()` does not delegate final provenance ownership to that digest-aware method.

Instead it renders:

```csharp
templateService.RenderFinal(...)
```

or:

```csharp
templateService.RenderFinalNoReference(...)
```

using the **current template file** and compares the on-disk final provenance text to the newly rendered current text.

## 7.3 Data-loss scenario

1. Main commit writes valid final provenance.
2. `MainProvenanceHash` is stored in the durable journal.
3. Application crashes after output completion but before `session.json` is deleted.
4. Before next startup, template is updated/repaired.
5. Startup runs `ValidateCompleteAsset()`.
6. Stored valid final provenance does not exactly equal newly rendered current template.
7. Asset is classified as incomplete.
8. Recovery calls `RollbackMain()`.
9. `RollbackMain()` correctly proves the old final provenance through `MainProvenanceHash`.
10. Because ownership is proven, it deletes the otherwise valid final provenance + Main images.

The stronger ownership digest therefore paradoxically makes the destructive rollback more likely to succeed after the weaker completion classifier rejects the asset.

## 7.4 Required fix

Delete the current-template rendering block from `ValidateCompleteAsset()`.

Use:

```csharp
var finalOwnership =
    ValidateExactFinalProvenanceOwnership(
        session,
        finalProvenancePath,
        templateService);

if (!finalOwnership.IsValid)
{
    errors.AddRange(finalOwnership.Errors);
}
```

`ValidateExactFinalProvenanceOwnership()` should implement this order:

```text
1. if persisted MainProvenanceHash exists:
   - hash actual file;
   - compare digest;
   - return;
2. otherwise:
   - legacy fallback;
   - render current template;
   - exact text compare.
```

Do **not** render the current template before checking whether a stored digest exists.

Same improvement should be applied to Reference exact ownership:

```csharp
if (ReferenceProvenanceHash exists)
{
    verify digest first;
    return;
}

// only legacy fallback renders current template
```

## 7.5 Copy-ready final ownership structure

```csharp
public ValidationResult ValidateExactFinalProvenanceOwnership(
    AssetSession session,
    string finalProvenancePath,
    TemplateService templateService)
{
    if (!File.Exists(finalProvenancePath))
    {
        return ValidationResult.Failure(
            $"Final provenance file does not exist: {finalProvenancePath}");
    }

    if (!string.IsNullOrWhiteSpace(
            session.MainProvenanceHash))
    {
        try
        {
            var actualHash =
                ComputeSha256(finalProvenancePath);

            return string.Equals(
                    actualHash,
                    session.MainProvenanceHash,
                    StringComparison.OrdinalIgnoreCase)
                ? ValidationResult.Success()
                : ValidationResult.Failure(
                    "Final provenance SHA-256 hash does not match "
                    + "stored MainProvenanceHash.");
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                $"Could not verify final provenance hash: {ex.Message}");
        }
    }

    // Legacy-session fallback only.
    if (string.IsNullOrWhiteSpace(session.MainFilename)
        || !session.MainProcessedAt.HasValue)
    {
        return ValidationResult.Failure(
            "Legacy Main provenance authority is incomplete.");
    }

    string expected;

    try
    {
        var date =
            session.MainProcessedAt.Value
                .ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

        expected = session.WorkflowMode switch
        {
            AssetWorkflowMode.ReferenceAssisted =>
                templateService.RenderFinal(
                    session.MainFilename,
                    session.ReferenceFilename,
                    session.ProjectName,
                    date,
                    session.MainPrompt ?? string.Empty),

            AssetWorkflowMode.NoReference =>
                templateService.RenderFinalNoReference(
                    session.MainFilename,
                    session.ProjectName,
                    date,
                    session.MainPrompt ?? string.Empty),

            _ => throw new InvalidDataException(
                $"Unknown workflow mode: {session.WorkflowMode}")
        };
    }
    catch (Exception ex)
    {
        return ValidationResult.Failure(
            "Could not reconstruct legacy final provenance: "
            + ex.Message);
    }

    var actual =
        File.ReadAllText(
            finalProvenancePath,
            Encoding.UTF8);

    return string.Equals(
            actual,
            expected,
            StringComparison.Ordinal)
        ? ValidationResult.Success()
        : ValidationResult.Failure(
            "Legacy final provenance content does not match.");
}
```

## 7.6 Regression tests

```text
CompletedNoRef_TemplateChangedBeforeRecovery_StillClassifiedComplete
CompletedReferenceMain_FinalTemplateChangedBeforeRecovery_StillClassifiedComplete
CompletedMain_MainProvenanceHashMismatch_FailsClosed
LegacyMainWithoutProvenanceHash_UsesCurrentTemplateFallback
ReferenceProvenanceHash_PersistsAcrossTemplateChange
MissingCurrentTemplate_WithStoredProvenanceHash_StillVerifiesOwnedFile
```

---

# 8. R2-006 — MEDIUM — production service methods still permit unjournaled writes

## 8.1 Main

`ProcessMainImage()` currently contains logic equivalent to:

```csharp
if (!session.IsMainCommitting)
{
    session.IsMainCommitting = true;
    session.MainFilename = ...;
    session.MainPrompt = ...;
    session.MainProcessedAt = ...;
    session.MainHash = ...;
    session.MainTransactionId = ...;
    ...
}
```

It then proceeds to filesystem writes.

That creates transaction metadata only **in memory**.

The MainForm path correctly persists Main metadata before calling the service, but the public service contract still allows a caller to bypass the crash-safety invariant.

## 8.2 Reference

The convenience overload:

```csharp
ProcessReference(
    AppSettings settings,
    string assetFolderName,
    string sourceImagePath,
    DateTimeOffset processedAt)
```

does:

```text
CreateReferenceSession
ProcessReference
```

without a durable `_sessionService.Save(preparedSession)` between them.

Again, MainForm uses the safer path, but the public production method remains a bypass.

## 8.3 Required rule

Methods that perform managed canonical writes should require a pre-existing durable transaction authority.

### Main

Replace implicit transaction creation with:

```csharp
if (!session.IsMainCommitting)
{
    throw new InvalidOperationException(
        "ProcessMainImage requires a prepared and durably persisted Main transaction.");
}
```

Validate all metadata before writes.

Optionally add:

```csharp
public AssetSession PrepareMainCommit(...)
```

which performs no writes and returns the journal state for the caller to save.

### Reference

Remove/internalize the crash-unsafe convenience overload, or make it test-only.

Preferred production API:

```text
CreateReferenceSession()      // no output mutation
caller Save(session)
ProcessReference(session, ...) // output mutation
```

## 8.4 Tests

```csharp
[Fact]
public void ProcessMainImage_WithoutPreparedTransaction_RefusesBeforeWrites()
{
    ...
}
```

```csharp
[Fact]
public void ProcessReference_RequiresPreparedSession()
{
    ...
}
```

Static guard:

```powershell
rg -n "ProcessReference\(.*AppSettings" src
```

Expected: no public crash-unsafe convenience implementation.

---

# 9. R2-007 — MEDIUM — Prompt Paste/Clear can still erase unrelated Main-image error state

## 9.1 Improvement that landed

The ordinary event path now separates:

```text
ClearPromptValidation()
ClearMainValidationVisuals()
```

which is correct.

## 9.2 Remaining branches

The Clear Prompt button still performs:

```csharp
txtPrompt.Clear();
ClearMainValidationVisuals();
```

Paste Clipboard sets prompt text and then calls:

```csharp
ClearMainValidationVisuals();
```

If Main image is missing/invalid and already outlined red, editing only the Prompt should not erase that unrelated error.

## 9.3 Minimal fix

Clear:

```csharp
btnClearPrompt.Click += (_, _) =>
{
    txtPrompt.Clear();
    // TextChanged already clears only Prompt validation.
};
```

Paste:

```csharp
txtPrompt.Text = text;
// TextChanged already calls ClearPromptValidation().
```

Remove the explicit `ClearMainValidationVisuals()` calls.

## 9.4 Tests

```text
MissingMainThenPastePrompt_MainBorderRemainsRed
MissingMainThenClearPrompt_MainBorderRemainsRed
MissingPromptThenChooseMain_PromptBorderRemainsRed
BothInvalid_FixPromptOnly_MainStillInvalidAndCtaRemainsError
BothInvalid_FixMainOnly_PromptStillInvalidAndCtaRemainsError
```

---

# 10. R2-008 — MEDIUM — current “comprehensive fix tests” do not prove the dangerous fixes

## 10.1 Problem

A new file is documented as:

```text
Dedicated regression and verification test suite for BUG-001 through BUG-024.
```

But several tests verify only a proxy, not the defect.

### BUG-001 test

It checks that a nonexistent outside Asset Root is invalid.

It does **not** test:

```text
valid asset root
valid asset session
ingame replaced by a junction/symlink
RollbackMain
external matching-hash target
no deletion outside managed tree
```

### BUG-002 test

It invokes `AssetProcessorService` directly and checks that a foreign Main destination is preserved.

It does not execute:

```text
MainForm -> failed ProcessMainImage -> TryReconcileFailedMainCommit
```

### BUG-006 test

It only verifies:

```text
ReferenceCommitPhase.Prepared serializes/deserializes
```

It does not invoke startup recovery.

This allowed R2-003 to survive.

### BUG-007 test

It only:

```text
Save journal
Load journal
change enum
Save journal
Load journal
delete journal
```

It does not simulate a single real replacement filesystem phase.

This allowed R2-001/R2-002 to survive.

## 10.2 Required test strategy

Tests must validate **observable safety properties**, not merely presence of the new enum/property/helper.

For each persistent transaction:

```text
Prepare authoritative journal
materialize exact disk state at a phase boundary
restart/recreate services/form
invoke startup recovery
verify exact files/hashes/journal result
```

## 10.3 Mandatory replacement matrix

```text
ReplacementRecovery_Prepared_NoFiles
ReplacementRecovery_Prepared_TempsCreated
ReplacementRecovery_OldBackupPending_NoMoveYet
ReplacementRecovery_OldBackupPending_OneOldMoved
ReplacementRecovery_OldBackupPending_BothOldMoved
ReplacementRecovery_OldBackedUp
ReplacementRecovery_NewPromotionPending_NoPromote
ReplacementRecovery_NewPromotionPending_OnePromoted
ReplacementRecovery_NewPromotionPending_BothPromoted
ReplacementRecovery_NewPromoted_BeforeSessionSwitch
ReplacementRecovery_SessionSwitchPending_OldSessionStillActive
ReplacementRecovery_SessionSwitchPending_NewSessionAlreadySaved
ReplacementRecovery_SessionSwitched
ReplacementRecovery_CleanupPending_BothBackups
ReplacementRecovery_CleanupPending_OneBackupAlreadyDeleted
ReplacementRecovery_TamperedBackup
ReplacementRecovery_TamperedNewCanonical
ReplacementRecovery_ForeignDestination
ReplacementRecovery_TamperedJournalPath
ReplacementRecovery_UnknownPhase
ReplacementRecovery_ReparseReferenceFolder
```

## 10.4 Mandatory Main recovery matrix

```text
MainRecovery_NoWrites
MainRecovery_TempMainOnly
MainRecovery_TempMainAndTempIngame
MainRecovery_FinalProvenancePromotedOnly
MainRecovery_RootMainPromoted
MainRecovery_IngamePromoted
MainRecovery_AllOutputsComplete
MainRecovery_CompleteThenTemplateChanged
MainRecovery_CompleteThenReferenceProvenanceAppended
MainRecovery_ForeignRootMain
MainRecovery_ForeignIngame
MainRecovery_ForeignFinalProvenance
MainRecovery_IngameReparse
```

## 10.5 Mandatory initial Reference recovery matrix

See section 5.7.

## 10.6 CI improvement

The existing CI structure is good:

```text
Debug warn-as-error
Debug tests
Release warn-as-error
Release tests
20x Release loop
self-contained win-x64 publish
smoke
coverage
```

Keep it.

Do not treat class presence in Cobertura as proof of transaction-path coverage.

For the critical state-machine tests, add an explicit filter step:

```powershell
dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical"
```

Mark the critical tests:

```csharp
[Trait("Category", "RecoveryCritical")]
```

This gives a readable CI gate specifically for the destructive recovery contract.

---

# 11. R2-009 — LOW — smoke gate still has two small acceptance weaknesses

The startup timeout was correctly raised to 15 seconds.

Two small gaps remain.

## 11.1 Icon verification is warning-only

Current behavior:

```powershell
catch {
    Write-Warning "Could not verify application icon: $_"
}
```

If the icon is an explicit release requirement, smoke should fail:

```powershell
if (-not $iconVerified) {
    throw "Application icon could not be extracted from published executable."
}
```

## 11.2 Record startup duration

Store actual elapsed startup milliseconds in the smoke JSON:

```powershell
StartupElapsedMs = $elapsedMs
```

This makes future cold-start regressions visible.

---

# 12. R2-010 — LOW — exact Drop button wording

The requested UI wording was:

```text
Drop file here
```

Current buttons say:

```text
Drop File
```

The functionality is correct.

If exact requirement compliance is desired, change both to:

```csharp
CreateButton("Drop file here");
```

No architectural consequence.

---

# 13. Additional hardening checks

These are not independent blockers if R2-001–R2-008 are implemented, but include them in the repair pass.

## 13.1 Recovery helper must stop caller after critical replacement failure

Current top-level pattern is conceptually:

```csharp
RecoverReferenceReplacementJournalIfPresent();

// continues with session.json recovery
```

The replacement helper can call:

```csharp
Close();
```

but return `void`.

Make the recovery result explicit:

```csharp
private bool RecoverReferenceReplacementJournalIfPresent()
```

Return:

```text
true  = safe to continue normal session recovery
false = critical recovery failed / application closing
```

Then:

```csharp
if (!RecoverReferenceReplacementJournalIfPresent())
{
    return;
}
```

Never run normal session recovery after a failed destructive replacement recovery in the same call stack.

## 13.2 Hash-first provenance verification

For both Reference and Main provenance:

```text
stored digest exists -> verify digest first
legacy digest absent -> reconstruct current-template text
```

Do not require the current template to be loadable before verifying a stored digest.

## 13.3 Exact Reference validation in stable recovery

Replace remaining recovery/cancel prechecks where practical:

```csharp
ValidateReferenceOutput(...)
```

with:

```csharp
ValidateExactReferenceOutput(..., _templateService)
```

Cancellation already re-verifies exact ownership inside `SessionService.Cancel`, so this is mostly consistency/UX.

## 13.4 Never use `overwrite:true` in destructive recovery

Static gate after repair:

```powershell
rg -n "overwrite:\s*true" src/AssetProvenanceHelper
```

Every result must be individually justified.

For recovery of user-visible/canonical files, expected result should be **zero**.

Atomic settings/session replacement may legitimately use overwrite semantics for the internal journal file itself; document those exceptions.

---

# 14. Required final test execution after repair

## 14.1 Static searches

```powershell
rg -n "Firefox" src tests README.md
# expected: 0 user-visible/browser-specific dependency matches

rg -n "_latestImagePath|_manualSelectionPath|ResolveImageSelection|IngameFilename" src
# expected: 0 obsolete active implementation matches

rg -n "ProcessMainImage" src/AssetProvenanceHelper
# inspect every call: durable Main journal must already exist

rg -n "ProcessReference" src/AssetProvenanceHelper
# inspect every mutating call: Prepared journal must already be persisted

rg -n "overwrite:\s*true" src/AssetProvenanceHelper
# no canonical recovery overwrite

rg -n "ValidateReferenceOutput\(" src/AssetProvenanceHelper/MainForm.Recovery.cs
# expected: use exact Reference output in destructive decision paths

rg -n "ValidateCompleteAsset" src tests
# every call provides digest-aware TemplateService path

rg -n "DeleteReplacementJournal" src/AssetProvenanceHelper
# inspect each deletion: only after verified commit or verified rollback
```

## 14.2 Build/test

```powershell
dotnet --info
dotnet tool restore
dotnet restore AssetProvenanceHelper.sln

dotnet build AssetProvenanceHelper.sln `
  -c Debug `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Debug `
  --no-build

dotnet build AssetProvenanceHelper.sln `
  -c Release `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Release `
  --no-build
```

## 14.3 Critical recovery-only gate

```powershell
dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical" `
  --logger "console;verbosity=detailed"
```

Expected:

```text
0 failed
0 skipped critical recovery tests
```

## 14.4 Flakiness

```powershell
for ($i = 1; $i -le 20; $i++) {
    Write-Host "Release test loop $i/20"

    dotnet test AssetProvenanceHelper.sln `
      -c Release `
      --no-build `
      --logger "console;verbosity=minimal"

    if ($LASTEXITCODE -ne 0) {
        throw "Flakiness run $i failed."
    }
}
```

Acceptance:

```text
20/20 PASS
```

## 14.5 Publish/smoke

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

Verify:

```text
exe launches
window title exact
templates all present
icon mandatory
graceful shutdown
dynamic v1.1.0 archive
smoke JSON created
StartupElapsedMs recorded
```

## 14.6 Coverage

```powershell
dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts/coverage
```

Do not accept “coverage file exists” as proof of state-machine testing. The explicit RecoveryCritical filter remains mandatory.

---

# 15. Manual UI acceptance matrix

Run with the actual published executable.

| Display | Scale | Expected |
|---|---:|---|
| 1366×768 | 100% | all core controls reachable without main-window scrolling |
| 1920×1080 | 100% | no clipping/overlap |
| 1920×1080 | 125% | no clipping/overlap |
| 1920×1080 | 150% | no inaccessible core controls |
| 2560×1440 | 125% | no excessive spacing/layout breakage |

For each:

```text
[ ] header name/version/logo/help visible
[ ] Settings compact
[ ] Asset Name + No reference mode visible
[ ] Reference/Main cards fit
[ ] Drop file here button visible in both cards
[ ] Status remains lowest section
[ ] no main-form scrollbar
[ ] long filenames ellipsize rather than expand layout
[ ] NoReference hides Reference and Main fills space
[ ] red borders persist independently
[ ] CTA pulses a bounded number of times
[ ] help blocks underlying controls
[ ] Esc closes help
```

---

# 16. Manual end-to-end workflows

## 16.1 Reference-assisted success

```text
1. Set Asset Root.
2. Enter onboarding1.
3. Choose/drop Reference.
4. Click Reference.
5. Verify:
   <root>/onboarding1/reference/<original reference filename>
   <root>/onboarding1/reference/license.txt — AI Reference Asset.md
6. Choose/drop Main.
7. Enter Final Prompt.
8. Click Main Image.
9. Verify:
   <root>/onboarding1/<original main filename>
   <root>/onboarding1/license.txt — Final AI-Generated Asset.md
   <root>/onboarding1/ingame/onboarding1.<same extension>
10. SHA256(root Main) == SHA256(ingame Main)
11. source download files still exist.
```

## 16.2 NoReference success

```text
1. Enable No reference mode.
2. Reference card disappears.
3. Enter onboarding2.
4. Select Main.
5. Enter Prompt.
6. Commit.
7. Verify:
   no reference/ folder
   original main filename at root
   fixed final provenance filename at root
   ingame/onboarding2.<ext>
```

## 16.3 Existing destination

Test:

```text
empty existing asset folder
folder with unrelated file
folder with final provenance
folder with same root Main filename
folder with ingame/<asset> same extension
folder with ingame/<asset> different supported extension
```

No existing file may be silently overwritten.

## 16.4 Reference replacement

Test:

```text
same extension / different filename
different supported extension
same filename / different bytes
new source identical bytes
cancel dialog
successful replacement
replacement after Main candidate already selected
replacement with Final Prompt already entered
```

Successful replacement must clear stale Main candidate and Prompt.

## 16.5 Tampering

Modify one file at a time:

```text
Reference image
Reference provenance
root Main
ingame Main
final provenance
session.json
replacement journal
cancel temp
Main temp
replacement backup
replacement temp
```

Expected rule:

> Unknown or modified content is preserved; destructive operation fails closed.

---

# 17. Repair order for a weaker implementation model

Use this exact order.

## Phase 1 — protect recovery from untrusted replacement journal

Implement:

```text
ValidateReferenceReplacementJournal
deterministic path checks
enum validation
reparse checks
ownership checks
no overwrite:true
explicit recovery return result
```

Run only new R2-001 tests.

Do not continue until they pass.

## Phase 2 — redesign replacement journal phase ordering

Split mutation-free transaction preparation from actual phases.

Implement write-ahead phase persistence.

Run full replacement crash matrix.

## Phase 3 — repair prepared Reference startup ordering

Move `ReferenceCommitPhase.Prepared` dispatch before normal ValidateSession.

Add prepared-session structural validator.

Repair normal-exception reconciliation.

Run full initial-Reference crash matrix.

## Phase 4 — separate Reference-integrity failure from incomplete-Main rollback

Use exact Reference baseline before destructive Main recovery.

Add appended-tamper completed-Main test.

## Phase 5 — make Main provenance digest authoritative

Change complete-asset final ownership to stored digest first.

Add template-change recovery tests.

## Phase 6 — close crash-unsafe service bypasses

Require pre-prepared Main/Reference transaction authority in mutating services.

Adapt tests through explicit preparation helpers.

## Phase 7 — UI/test/smoke cleanup

Fix Prompt Paste/Clear visual state.

Strengthen dedicated critical tests.

Make icon smoke mandatory.

Match Drop wording if strict compliance is desired.

## Phase 8 — full final gate

Run:

```text
Debug
Release
RecoveryCritical
20× Release
publish
smoke
coverage
manual workflow matrix
manual DPI matrix
```

---

# 18. Definition of Done

Do not declare the project issue-free until every box is true.

## Durable safety

```text
[ ] replacement journal is structurally/path validated before any mutation
[ ] replacement recovery never follows arbitrary journal paths
[ ] replacement recovery never overwrite:true over unknown canonical files
[ ] every replacement canonical mutation has durable write-ahead authority
[ ] unknown replacement phase fails closed
[ ] failed replacement recovery prevents normal session recovery from continuing
[ ] Prepared Reference recovery is reached before stable-session validation
[ ] normal failed Reference cannot leave a stale journal while UI keeps operating
[ ] Main mutating service refuses a non-journaled transaction
[ ] Reference mutating service refuses a non-journaled transaction
```

## Provenance/data preservation

```text
[ ] exact Reference integrity failure never causes deletion of otherwise valid completed Main outputs
[ ] MainProvenanceHash is authoritative when present
[ ] ReferenceProvenanceHash is authoritative when present
[ ] current templates are used only as legacy fallback when digest is absent
[ ] template change cannot cause a valid completed asset to be rolled back
[ ] modified/foreign files are never silently overwritten or deleted
```

## Product requirements

```text
[ ] Project input absent
[ ] browser-neutral Image Download Folder
[ ] independent Reference/Main selection
[ ] Reference/Main Refresh independent
[ ] Reference/Main Choose independent
[ ] Reference/Main Drop independent
[ ] button-sized Drop file here controls
[ ] extensionless Asset Name
[ ] root Main keeps original filename
[ ] ingame copy uses Asset Name + original extension
[ ] NoReference produces no Reference folder
[ ] Final Prompt explicit
[ ] CTA error pulse bounded
[ ] validation fields clear independently
[ ] header/logo/version/help
[ ] help blocks underlying UI
[ ] Made by CeeGore shown
[ ] status is lowest section
[ ] no main window scrollbar in target layouts
```

## Automated verification

```text
[ ] Debug build warnings-as-errors PASS
[ ] Debug tests PASS
[ ] Release build warnings-as-errors PASS
[ ] Release tests PASS
[ ] RecoveryCritical tests PASS
[ ] 20/20 Release loop PASS
[ ] self-contained win-x64 publish PASS
[ ] smoke PASS
[ ] icon gate PASS
[ ] coverage artifact PASS
```

## Manual verification

```text
[ ] Reference-assisted workflow PASS
[ ] NoReference workflow PASS
[ ] replacement workflow PASS
[ ] cancel workflow PASS
[ ] crash-recovery matrix spot-check PASS
[ ] 1366×768 @100% PASS
[ ] 1920×1080 @100% PASS
[ ] 1920×1080 @125% PASS
[ ] 1920×1080 @150% PASS
[ ] 2560×1440 @125% PASS
```

---

# 19. Final retest conclusion

The latest repair commit is **meaningfully better** than the version audited in `bugs1.md`.

It should **not** be reverted or rewritten wholesale.

The correct next action is a targeted repair of the remaining recovery architecture:

```text
R2-001 replacement journal trust/path safety
R2-002 replacement write-ahead ordering
R2-003 Prepared Reference recovery ordering
R2-004 exact Reference failure must preserve completed Main
R2-005 provenance digest must drive completion recovery
R2-006 remove unjournaled mutating-service bypasses
R2-007 independent validation cleanup
R2-008 real crash-state tests
```

After those are repaired and the full Windows test gate completes with zero failures, another paranoid retest is appropriate.

**Current acceptance state: FAIL — remaining known defects exist.**

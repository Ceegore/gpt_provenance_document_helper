# AI Asset Provenance Helper — Third Paranoid Retest & Repair Guide

**File:** `bugs3.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `dce5737b96d179c84624d1e420970de18e26cefc`  
**Latest commit message:** `fix: R2-001 through R2-010 - recovery hardening and journal atomicity`  
**Previous audits:** `bugs1.md`, `bugs2.md`  
**Original rework authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Acceptance result

**FAIL — the tool is materially improved, but zero-defect acceptance is still premature.**

The newest repair round is much better than the previous one. It correctly implements several important architectural changes rather than merely patching symptoms:

- Reference replacement now has explicit write-ahead phases.
- replacement transaction construction is separated from the canonical mutation methods used by `MainForm`;
- replacement journal structure/path validation was added;
- prepared Reference recovery is dispatched before normal stable-session validation;
- active Main recovery now exact-validates the Reference before deleting Main output;
- stored Reference/Main provenance hashes are now authoritative before current-template fallback;
- `ProcessMainImage()` now refuses `IsMainCommitting == false`;
- Prompt validation state no longer clears Main-image validation on Paste/Clear;
- the publish smoke test now requires an extractable application icon;
- the `RecoveryCritical` CI filter exists;
- the Drop controls now say `Drop file here`.

These fixes are real and should be preserved.

However, a phase-by-phase reconstruction of the **actual files on disk** finds remaining defects. The most important issue is that the new `RecoveryCritical` tests do not construct the disk state represented by each phase; they mostly change the phase enum while leaving the old Reference fully intact. As a result, all tests can be green while genuine crash states are still unrecoverable.

The current blockers are concentrated in:

1. real Reference-replacement crash recovery;
2. same-filename Reference replacement recovery;
3. destructive ownership during replacement recovery;
4. initial Reference normal-exception reconciliation;
5. Main cleanup/reconciliation edge cases;
6. insufficient recovery tests.

---

# 0.2 Important test-execution distinction

The audited commit message reports:

```text
Full Release suite: 492/492 x20 consecutive runs - zero flakes
Smoke test: icon 32x32 verified, window title correct, clean exit
```

This is useful evidence from the implementation run, but this audit could not independently reproduce that Windows run from the current analysis environment.

The current repository also contains the expected CI structure:

```text
Debug build/test
Release build/test
RecoveryCritical filter
20x Release loop
win-x64 publish
smoke
coverage
```

This report therefore makes two separate statements:

### Static/source verdict

**FAIL — known defects remain.**

### Reported implementation-run verdict

The commit claims the existing suite passed, but the current tests do not exercise the real crash states described below. A green 492-test run therefore does not invalidate these findings.

---

# 1. Retest of every `bugs2.md` item

| Prior item | Current status | Retest conclusion |
|---|---|---|
| R2-001 replacement journal structural/path safety | **PARTIAL** | deterministic path validation is present, but recovery still performs several destructive deletes without proving current content ownership |
| R2-002 replacement write-ahead ordering | **PARTIAL / architecture landed** | phase ordering is much better, but recovery logic for real partial-promotion states is wrong |
| R2-003 prepared Reference startup ordering | **PARTIAL** | startup ordering is fixed; normal `HandleReference()` exception reconciliation and empty-directory cleanup are still defective |
| R2-004 exact Reference failure preserves Main | **FIXED** | active Main recovery now exact-validates Reference and fails closed |
| R2-005 provenance digest drives completion | **FIXED materially** | Reference/Main digest-first ownership is now present and `ValidateCompleteAsset()` delegates final provenance ownership correctly |
| R2-006 unjournaled Main mutation | **FIXED for `ProcessMainImage`** | direct Main mutation now refuses unprepared state; one crash-unsafe public replacement convenience method still remains |
| R2-007 independent validation cleanup | **FIXED** | Paste/Clear Prompt no longer explicitly clear Main image validation |
| R2-008 real crash-state tests | **NOT FIXED sufficiently** | `RecoveryCritical` exists, but the most important phase test does not create phase-appropriate files |
| R2-009 smoke icon/startup telemetry | **FIXED structurally** | icon is mandatory and startup elapsed time is recorded |
| R2-010 exact Drop wording | **FIXED** | both controls use `Drop file here` |

The most important conclusion is:

> The implementation architecture is now close, but the recovery state machine still needs one more targeted repair.

---

# 2. Current defect summary

| ID | Severity | Area | Summary |
|---|---|---|---|
| R3-001 | **HIGH** | replacement recovery | real `NewPromotionPending`, `NewPromoted`, and `SessionSwitchPending` rollback states can fail because old provenance is restored before the known-new provenance occupying the same fixed path is removed |
| R3-002 | **HIGH** | replacement recovery | same-filename Reference replacement is falsely classified as “session already switched” because recovery compares only `ReferenceFilename` |
| R3-003 | **HIGH** | replacement finalization | replacement journal is deleted before checking whether backup cleanup succeeded; a crash in that window can discard rollback/cleanup authority |
| R3-004 | **HIGH** | initial Reference | `HandleReference()` still loses the prepared-session variable on `ProcessReference()` failure and can leave a durable recovery journal while the UI remains usable |
| R3-005 | **MEDIUM** | destructive ownership | replacement recovery still blindly deletes several temp/canonical/backup files after structural validation instead of proving current ownership |
| R3-006 | **MEDIUM** | prepared Reference recovery | crash after tool-created directories but before either Reference file leaves empty `<asset>/reference` directories behind |
| R3-007 | **MEDIUM** | Main preflight/recovery | ordinary Reference-assisted Main collisions at foreign final provenance or same-path ingame asset can become a false CRITICAL rollback failure |
| R3-008 | **MEDIUM** | Main completion | session deletion failure can successfully roll Main back once and then immediately attempt a second rollback, producing a false CRITICAL close |
| R3-009 | **MEDIUM** | stable Reference recovery | normal stable session resume still uses weak substring Reference validation rather than digest/exact validation |
| R3-010 | **MEDIUM** | validation robustness | prepared/journal validators accept missing current-version authority hashes and can throw on malformed paths rather than returning `ValidationResult.Failure` |
| R3-011 | **MEDIUM** | service API | public `PrepareReferenceReplacement()` remains a crash-unsafe mutation convenience path without durable journaling |
| R3-012 | **MEDIUM** | tests / false assurance | the current `RecoveryCritical` phase test does not materialize any of the phase-specific filesystem states it claims to test |
| R3-013 | **LOW-MEDIUM** | Main provenance TOCTOU | rendered final provenance hash is rewritten in memory after journal persistence instead of being checked against the persisted authority |
| R3-014 | **LOW** | service consistency | `PrepareMainCommit()` validates against default extensions instead of the caller/settings extension set |

**Blocking recommendation:** repair R3-001 through R3-012 before final zero-known-defect acceptance.  
R3-013/R3-014 are small enough to include in the same repair pass.

---

# 3. R3-001 — HIGH — real partial/new promotion states cannot always roll back

## 3.1 Core invariant

The Reference provenance file has one fixed canonical path:

```text
<AssetFolder>/reference/license.txt — AI Reference Asset.md
```

The old and new Reference sessions therefore share the same provenance destination path.

During replacement:

```text
old provenance canonical
    -> old provenance backup

new provenance temp
    -> same canonical provenance path
```

This means a real crash after new provenance promotion leaves:

```text
canonical provenance = NEW
backup provenance    = OLD
```

That is a valid and recoverable state.

## 3.2 Current recovery ordering

For rollback-oriented phases such as:

```text
NewPromotionPending
NewPromoted when session.json is still old
SessionSwitchPending when session.json is still old
```

recovery calls the fail-closed old-provenance restore helper first.

Conceptually:

```csharp
RestoreReferenceProvenanceFailClosed(
    oldBackup,
    canonicalProvenance,
    oldSession,
    templateService);
```

The helper correctly sees:

```text
backup = exact OLD provenance
destination = exact NEW provenance
```

and refuses to overwrite the destination because it does not match OLD.

That helper is behaving correctly.

The **caller is wrong**: it needs to prove/delete the known NEW canonical first, then restore OLD.

## 3.3 Real crash states

### State A — `NewPromotionPending`, crash before any promotion

```text
backup old reference      = yes
backup old provenance     = yes
temp new reference        = yes
temp new provenance       = yes
new canonical reference   = no
canonical provenance      = no
session.json              = OLD
journal                   = NewPromotionPending
```

Rollback should succeed.

### State B — `NewPromotionPending`, crash after new Reference promotion only

```text
backup old reference      = yes
backup old provenance     = yes
new canonical reference   = yes
temp new provenance       = yes
canonical provenance      = no
session.json              = OLD
journal                   = NewPromotionPending
```

Rollback should:

```text
verify new canonical hash == NewSession.ReferenceHash
delete new canonical
restore old reference
restore old provenance
delete owned temp provenance
save old session
delete journal
```

Current bespoke recovery can fail for same-filename Reference and leaves temp residue.

### State C — `NewPromotionPending`, crash after both new files promoted

```text
backup old reference      = yes
backup old provenance     = yes
new canonical reference   = yes
canonical provenance      = NEW
session.json              = OLD
journal                   = NewPromotionPending
```

This state currently fails when recovery tries to restore OLD provenance over known NEW provenance.

### State D — `NewPromoted`, session still OLD

This is the normal disk state immediately after:

```text
PromoteNewReference()
SaveReplacementJournal(NewPromoted)
```

and before successful session switching.

It has the same old/new provenance collision.

## 3.4 Best repair: stop duplicating rollback logic in MainForm

The processor already has a substantially safer routine:

```csharp
RollbackReferenceReplacement(
    ReferenceReplacementTransaction transaction)
```

That routine:

- proves old backup image ownership;
- proves old backup provenance ownership;
- recognizes current canonical Reference as either OLD or NEW;
- recognizes current canonical provenance as either OLD or NEW;
- deletes the known NEW state before restoring OLD;
- handles same-filename Reference specially;
- cleans transaction temps only when ownership is proven;
- fails closed if content is unknown.

Use it instead of manually calling:

```text
RestoreReferenceProvenanceFailClosed
RestoreReferenceImageFailClosed
File.Delete
File.Delete
...
```

## 3.5 Copy-ready journal -> transaction helper

```csharp
private static ReferenceReplacementTransaction
    TransactionFromJournal(
        ReferenceReplacementJournal journal)
{
    ArgumentNullException.ThrowIfNull(journal);

    return new ReferenceReplacementTransaction
    {
        TransactionId =
            journal.TransactionId,

        OldSession =
            journal.OldSession,

        NewSession =
            journal.NewSession,

        BackupReferencePath =
            journal.BackupReferencePath,

        BackupProvenancePath =
            journal.BackupProvenancePath,

        TempNewReferencePath =
            journal.TempNewReferencePath,

        TempNewProvenancePath =
            journal.TempNewProvenancePath
    };
}
```

## 3.6 Copy-ready rollback coordinator

```csharp
private bool RollBackReplacementJournal(
    ReferenceReplacementJournal journal)
{
    var transaction =
        TransactionFromJournal(journal);

    var rollback =
        _assetProcessorService
            .RollbackReferenceReplacement(
                transaction);

    if (!rollback.IsValid)
    {
        ShowMessageBox(
            "CRITICAL: The interrupted Reference replacement "
            + "could not be safely rolled back."
            + Environment.NewLine
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                rollback.Errors)
            + Environment.NewLine
            + Environment.NewLine
            + "The replacement journal was preserved.",
            "Critical Replacement Recovery Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        Close();
        return false;
    }

    try
    {
        _sessionService.Save(
            journal.OldSession);

        _sessionService
            .DeleteReplacementJournal();
    }
    catch (Exception ex)
    {
        ShowError(
            "CRITICAL: Replacement files were rolled back, "
            + "but the old durable session/journal state "
            + "could not be finalized.",
            ex);

        Close();
        return false;
    }

    AddStatus(
        "Interrupted Reference replacement "
        + "was rolled back to the previous Reference.");

    return true;
}
```

## 3.7 Improve processor rollback temp semantics

Before using processor rollback as the authoritative recovery primitive, fix this subtle behavior:

Current temp cleanup roughly does:

```csharp
if (temp exists)
{
    if (hash matches)
    {
        delete temp;
    }
    // otherwise preserve without returning failure
}
```

For a **durable transaction rollback**, an unknown deterministic temp file means cleanup was not complete.

Change to:

```csharp
if (File.Exists(
        transaction.TempNewReferencePath))
{
    try
    {
        var hash =
            ComputeSha256(
                transaction.TempNewReferencePath);

        if (!string.Equals(
                hash,
                transaction.NewSession.ReferenceHash,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "Replacement temp Reference no longer "
                + "matches NewSession.ReferenceHash. "
                + "Unknown file was preserved.");
        }
        else
        {
            TryDeleteFileWithError(
                transaction.TempNewReferencePath,
                errors);
        }
    }
    catch (Exception ex)
    {
        errors.Add(
            "Could not verify replacement temp Reference: "
            + ex.Message);
    }
}
```

Do the same for temporary provenance.

Only return rollback success when:

```text
old canonical state restored
AND
all tool-owned transaction files are gone
OR
known intentionally retained cleanup is explicitly represented
```

---

# 4. R3-002 — HIGH — same-filename replacement can destroy rollback authority

## 4.1 Current decision

In `NewPromoted` recovery the code determines that session switching already happened with a check equivalent to:

```csharp
currentSession != null
&& currentSession.ReferenceFilename
   == journal.NewSession.ReferenceFilename
```

`SessionSwitchPending` uses the same idea.

## 4.2 Why filename is not an authority

Replacing:

```text
reference/ref.png
```

with a newer AI-generated:

```text
ref.png
```

is valid and expected.

Then:

```text
OldSession.ReferenceFilename == "ref.png"
NewSession.ReferenceFilename == "ref.png"
```

even though these differ in:

```text
ReferenceHash
ReferenceProvenanceHash
ReferenceProcessedAt
ReferenceSourcePath
```

## 4.3 Dangerous crash

After new files are promoted:

```text
disk canonical Reference = NEW
disk canonical provenance = NEW
backup OLD exists
session.json = OLD
journal = NewPromoted
```

For a same-filename replacement:

```csharp
oldSession.ReferenceFilename
==
newSession.ReferenceFilename
```

Current recovery concludes:

```text
sessionSwitched = true
```

It then:

```text
exact-validates NEW files
deletes OLD backups
deletes replacement journal
```

but leaves:

```text
session.json = OLD
```

The next stable session validation will detect a hash mismatch, but rollback authority has already been destroyed.

This is a **high-severity durability bug**.

## 4.4 Required full-authority comparison

Add one central helper.

```csharp
private static bool
    MatchesReferenceAuthority(
        AssetSession? actual,
        AssetSession expected)
{
    if (actual is null)
    {
        return false;
    }

    return
        actual.WorkflowMode
            == expected.WorkflowMode

        && string.Equals(
            actual.ProjectName,
            expected.ProjectName,
            StringComparison.Ordinal)

        && ValidationService.PathsEqual(
            actual.AssetRootFolder,
            expected.AssetRootFolder)

        && string.Equals(
            actual.AssetFolderName,
            expected.AssetFolderName,
            StringComparison.Ordinal)

        && ValidationService.PathsEqual(
            actual.AssetFolder,
            expected.AssetFolder)

        && string.Equals(
            actual.ReferenceFilename,
            expected.ReferenceFilename,
            StringComparison.Ordinal)

        && ValidationService.PathsEqual(
            actual.ReferenceDestinationPath,
            expected.ReferenceDestinationPath)

        && ValidationService.PathsEqual(
            actual.ReferenceProvenancePath,
            expected.ReferenceProvenancePath)

        && string.Equals(
            actual.ReferenceHash,
            expected.ReferenceHash,
            StringComparison.OrdinalIgnoreCase)

        && string.Equals(
            actual.ReferenceProvenanceHash,
            expected.ReferenceProvenanceHash,
            StringComparison.OrdinalIgnoreCase)

        && actual.ReferenceProcessedAt
            .EqualsExact(
                expected.ReferenceProcessedAt);
}
```

## 4.5 Required phase decision

```csharp
var current =
    _sessionService.Exists()
        ? _sessionService.Load()
        : null;

var matchesOld =
    MatchesReferenceAuthority(
        current,
        journal.OldSession);

var matchesNew =
    MatchesReferenceAuthority(
        current,
        journal.NewSession);

if (matchesOld == matchesNew)
{
    // both false = unknown/corrupt
    // both true  = authority definition insufficient
    FailClosedPreserveJournal();
    return false;
}
```

For:

```text
Prepared
OldBackupPending
OldBackedUp
NewPromotionPending
```

expected durable session authority is OLD.

For:

```text
SessionSwitched
CleanupPending
```

expected durable session authority is NEW.

For the boundary phases:

```text
NewPromoted
SessionSwitchPending
```

either OLD or NEW can be legitimate depending on the exact crash boundary, so use the full comparison and choose rollback/commit accordingly.

---

# 5. R3-003 — HIGH — replacement journal is deleted before cleanup result is known

## 5.1 Current normal completion sequence

The workflow currently does:

```csharp
var cleanup =
    _assetProcessorService
        .CleanupReplacementBackups(
            transaction);

_sessionService
    .DeleteReplacementJournal();

if (!cleanup.IsValid)
{
    ...
}
```

The durable recovery authority is deleted **before** the cleanup result is inspected.

## 5.2 Why this matters

`CleanupReplacementBackups()` can return failure because:

```text
new output no longer exact
old backup image hash changed
old backup provenance changed
backup deletion failed / locked
```

The method intentionally preserves unsafe/unknown files.

The journal is needed to describe:

```text
old authority
new authority
transaction ID
backup paths
temp paths
CleanupPending state
```

## 5.3 Crash window

Example:

```text
session.json = NEW
new canonical files = valid
one old backup = still present
cleanup returned failure due locked backup
journal = deleted
process crashes before warning/repair branch
```

The next startup has no durable transaction record for that backup.

More dangerous:

```text
cleanup returns failure because new canonical output
was modified between validation and cleanup
journal is deleted
crash occurs before in-memory rollback branch
```

Old backups may still exist, but recovery authority is lost.

## 5.4 Required ordering

```csharp
var cleanup =
    _assetProcessorService
        .CleanupReplacementBackups(
            transaction);

if (!cleanup.IsValid)
{
    ShowMessageBox(
        "Reference replacement reached cleanup, "
        + "but cleanup could not be proven complete."
        + Environment.NewLine
        + Environment.NewLine
        + string.Join(
            Environment.NewLine,
            cleanup.Errors)
        + Environment.NewLine
        + Environment.NewLine
        + "The CleanupPending journal was preserved.",
        "Replacement cleanup incomplete",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);

    Close();
    return;
}

// Only now:
_sessionService
    .DeleteReplacementJournal();
```

This converts a hard-to-reason in-memory warning branch into deterministic startup recovery.

## 5.5 `CleanupPending` startup recovery

Do not manually:

```csharp
File.Delete(backupReference);
File.Delete(backupProvenance);
```

Instead:

```csharp
private bool FinishReplacementCommit(
    ReferenceReplacementJournal journal)
{
    var current =
        _sessionService.Exists()
            ? _sessionService.Load()
            : null;

    if (!MatchesReferenceAuthority(
            current,
            journal.NewSession))
    {
        return FailReplacementRecovery(
            "session.json does not match NewSession authority.");
    }

    var exactNew =
        _validationService
            .ValidateExactReferenceOutput(
                journal.NewSession,
                _templateService);

    if (!exactNew.IsValid)
    {
        return FailReplacementRecovery(
            string.Join(
                Environment.NewLine,
                exactNew.Errors));
    }

    var transaction =
        TransactionFromJournal(journal);

    var cleanup =
        _assetProcessorService
            .CleanupReplacementBackups(
                transaction);

    if (!cleanup.IsValid)
    {
        return FailReplacementRecovery(
            string.Join(
                Environment.NewLine,
                cleanup.Errors));
    }

    try
    {
        _sessionService
            .DeleteReplacementJournal();
    }
    catch (Exception ex)
    {
        ShowError(
            "Replacement cleanup succeeded but "
            + "the journal could not be deleted.",
            ex);

        Close();
        return false;
    }

    AddStatus(
        "Interrupted Reference replacement "
        + "cleanup completed.");

    return true;
}
```

---

# 6. R3-004 — HIGH — initial Reference exception path still does not reconcile the prepared journal

## 6.1 Current code shape

`HandleReference()` currently uses:

```csharp
AssetSession? createdSession = null;

try
{
    var preparedSession =
        CreateReferenceSession(...);

    Save(preparedSession);

    createdSession =
        ProcessReference(
            preparedSession,
            ...);

    ...
}
catch
{
    if (createdSession != null
        && createdSession.ReferenceCommitPhase
            == ReferenceCommitPhase.Prepared)
    {
        ...
    }
}
```

## 6.2 The problem

If:

```csharp
ProcessReference(...)
```

throws, assignment to `createdSession` never completes.

Therefore:

```text
createdSession == null
```

in the outer catch.

But a durable:

```text
session.json with ReferenceCommitPhase.Prepared
```

already exists.

## 6.3 Two subcases

### ProcessReference rollback succeeded

The journal is stale and should be deleted.

Current UI can remain open with the stale journal.

### ProcessReference rollback was incomplete

This is more important.

The prepared journal is now the only durable authority describing:

```text
intended asset folder
intended Reference paths
Reference hash
provenance hash
ownership flags
transaction
```

The UI must not continue accepting a new asset and overwrite that journal.

## 6.4 Required repair

Keep the **prepared** session outside the try.

```csharp
AssetSession? preparedSession = null;

try
{
    ...

    preparedSession =
        _assetProcessorService
            .CreateReferenceSession(
                settings,
                folderName,
                sourceImage,
                now);

    _sessionService.Save(
        preparedSession);

    var completedSession =
        _assetProcessorService
            .ProcessReference(
                preparedSession,
                settings,
                sourceImage,
                now);

    _sessionService.Save(
        completedSession);

    _currentSession =
        completedSession;

    ...
}
catch (Exception ex)
{
    if (preparedSession is null)
    {
        ShowError(
            "Reference processing failed.",
            ex);

        return;
    }

    ValidationResult rollback;

    try
    {
        rollback =
            _assetProcessorService
                .RollbackReference(
                    preparedSession);
    }
    catch (Exception rollbackEx)
    {
        ShowError(
            "CRITICAL: Reference processing failed "
            + "and the prepared transaction could "
            + "not be safely reconciled.",
            rollbackEx);

        Close();
        return;
    }

    if (!rollback.IsValid)
    {
        ShowMessageBox(
            "CRITICAL: Reference processing failed "
            + "and rollback could not be proven complete."
            + Environment.NewLine
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                rollback.Errors)
            + Environment.NewLine
            + Environment.NewLine
            + "The prepared session journal was preserved.",
            "Critical Reference Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        Close();
        return;
    }

    try
    {
        _sessionService.Delete();
    }
    catch (Exception deleteEx)
    {
        ShowError(
            "Reference output rollback succeeded, "
            + "but the prepared session journal "
            + "could not be deleted.",
            deleteEx);

        Close();
        return;
    }

    ShowError(
        "Reference processing failed. "
        + "The prepared transaction was rolled back safely.",
        ex);
}
```

## 6.5 Do not blindly delete on a rollback failure

Critical rule:

```text
rollback uncertain => preserve journal => close
```

Never:

```text
rollback uncertain => delete session => continue
```

---

# 7. R3-005 — MEDIUM — replacement recovery still deletes files without current ownership proof

The new journal validator proves:

```text
paths are deterministic
paths stay inside expected tree
folder boundaries are not reparse points
phase/transaction ID are structurally valid
```

That is excellent.

But **path authority is not content ownership**.

Examples still found in recovery include direct deletes equivalent to:

```csharp
File.Delete(journal.TempNewReferencePath);
File.Delete(journal.TempNewProvenancePath);
File.Delete(journal.NewSession.ReferenceDestinationPath);
File.Delete(journal.BackupReferencePath);
File.Delete(journal.BackupProvenancePath);
```

after only structural journal validation or unrelated new-output validation.

A file can be modified/replaced after a crash.

## 7.1 Required ownership rules

Before deleting:

### temp Reference

```text
SHA256 == NewSession.ReferenceHash
```

### temp provenance

```text
SHA256 == NewSession.ReferenceProvenanceHash
```

### new canonical Reference

```text
SHA256 == NewSession.ReferenceHash
```

### new canonical provenance

```text
SHA256 == NewSession.ReferenceProvenanceHash
```

### old backup Reference

```text
SHA256 == OldSession.ReferenceHash
```

### old backup provenance

```text
SHA256 == OldSession.ReferenceProvenanceHash
or legacy exact-template fallback
```

If not:

```text
preserve file
preserve journal
fail closed
```

## 7.2 Best solution

Do not add six more delete helpers in `MainForm`.

Reuse:

```text
RollbackReferenceReplacement()
CleanupReplacementBackups()
```

after strengthening their temp-file failure result as described in R3-001.

That keeps destructive ownership in one service layer.

---

# 8. R3-006 — MEDIUM — prepared Reference crash leaves empty tool-created folders

## 8.1 Current prepared recovery

If neither Reference file exists:

```csharp
if (!refExists && !provExists)
{
    _sessionService.Delete();
    return;
}
```

## 8.2 Crash state

`ProcessReference()` creates:

```text
<asset>/
<asset>/reference/
```

before copying the Reference image.

A crash can happen after directory creation but before image copy.

Then:

```text
ReferenceDestinationPath absent
ReferenceProvenancePath absent
WasAssetFolderCreatedByTool = true
WasReferenceFolderCreatedByTool = true
```

Current startup recovery deletes `session.json` but leaves:

```text
<asset>/reference/
```

behind.

The next attempt sees an existing destination and prompts unnecessarily.

## 8.3 Minimal fix

Remove the special “both absent -> delete journal” path.

Call:

```csharp
var rollback =
    _assetProcessorService
        .RollbackReference(session);
```

even when both files are absent.

`RollbackReference()` already knows:

```text
WasReferenceFolderCreatedByTool
WasAssetFolderCreatedByTool
```

and will remove empty tool-created directories while preserving pre-existing directories.

Then delete `session.json` only if rollback succeeds.

---

# 9. R3-007 — MEDIUM — normal Main destination collisions can become false CRITICAL failures

## 9.1 Scope

This affects Reference-assisted Main because its transaction is persisted before the processor’s destination collision checks.

NoReference already performs substantial preflight before journal creation.

## 9.2 Example: foreign final provenance already exists

Flow:

```text
Reference ready
user selects Main
UI writes active Main journal
ProcessMainImage sees:
  final provenance already exists
throws IOException
```

No Main temp or output file was created by this attempt.

Reconciliation calls:

```csharp
RollbackMain()
```

Rollback sees the foreign final provenance and correctly refuses to delete it because it does not match `MainProvenanceHash`.

But `TryReconcileFailedMainCommit()` interprets that rollback failure as critical.

The application closes even though the transaction never mutated any managed Main output.

## 9.3 Same issue: exact ingame collision

If:

```text
ingame/<AssetName>.<same extension>
```

already exists with foreign bytes, rollback ownership correctly fails, but the collision was only a preflight rejection.

## 9.4 Best fix: preflight before journal persistence

Add:

```csharp
public ValidationResult ValidateMainDestinationAvailability(
    AssetSession session,
    IReadOnlyCollection<string> acceptedExtensions,
    string sourceImagePath)
```

The method performs **no mutation**.

Copy-ready implementation shape:

```csharp
public ValidationResult
    ValidateMainDestinationAvailability(
        AssetSession session,
        IReadOnlyCollection<string> acceptedExtensions,
        string sourceImagePath)
{
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(acceptedExtensions);

    var errors =
        new List<string>();

    var mainFilename =
        Path.GetFileName(
            sourceImagePath);

    var rootMain =
        Path.Combine(
            session.AssetFolder,
            mainFilename);

    var finalProvenance =
        Path.Combine(
            session.AssetFolder,
            AppConstants.FinalProvenanceFileName);

    var ingameFolder =
        session.GetIngameFolderPath();

    if (File.Exists(rootMain))
    {
        errors.Add(
            $"Main image destination already exists: {rootMain}");
    }

    if (File.Exists(finalProvenance))
    {
        errors.Add(
            $"Final provenance already exists: {finalProvenance}");
    }

    if (Directory.Exists(ingameFolder)
        && ValidationService.IsReparsePoint(
            ingameFolder))
    {
        errors.Add(
            "Ingame folder is a reparse point.");
    }

    if (Directory.Exists(ingameFolder))
    {
        foreach (var path in
                 Directory.EnumerateFiles(
                     ingameFolder,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var ext =
                Path.GetExtension(path);

            var stem =
                Path.GetFileNameWithoutExtension(path);

            if (acceptedExtensions.Contains(
                    ext,
                    StringComparer.OrdinalIgnoreCase)
                && string.Equals(
                    stem,
                    session.AssetFolderName,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"An ingame asset variant already exists: {path}");
            }
        }
    }

    return errors.Count == 0
        ? ValidationResult.Success()
        : ValidationResult.Failure(errors);
}
```

Call it from `HandleReferenceAssistedMainImage()` **before**:

```text
session.IsMainCommitting = true
_sessionService.Save(session)
```

Keep the same checks inside `ProcessMainImage()` as race protection.

---

# 10. R3-008 — MEDIUM — session deletion failure triggers a second rollback

## 10.1 Current normal success/error path

After successful Main output:

```csharp
try
{
    _sessionService.Delete();
}
catch
{
    var rollback =
        _assetProcessorService.RollbackMain(...);

    if (rollback.IsValid)
    {
        throw new IOException(...);
    }
}
```

`RollbackMain()` succeeds and calls:

```csharp
session.ResetMainCommitMetadata();
```

The thrown IOException is caught by the outer generic Main catch, which then calls:

```csharp
TryReconcileFailedMainCommit(...)
```

That calls:

```csharp
RollbackMain(...)
```

**again**.

The second call sees:

```text
IsMainCommitting == false
```

and returns:

```text
No active Main commit exists for rollback.
```

The UI can then report a false CRITICAL error and close.

## 10.2 Correct handling

A successful rollback after session-delete failure is already the reconciliation.

Do not throw it into the generic processor-failure branch.

### Reference-assisted

```csharp
catch (Exception deleteException)
{
    var rollback =
        _assetProcessorService
            .RollbackMain(
                session,
                committedFilename);

    if (!rollback.IsValid)
    {
        ShowCriticalAndClose(...);
        return;
    }

    try
    {
        // Replaces the still-durable active Main
        // journal with the recovered Reference session.
        _sessionService.Save(session);

        _currentSession = session;
        _state = UiState.ReferenceReady;

        ApplyState();

        ShowError(
            "The asset could not be finalized because "
            + "session.json could not be removed. "
            + "Main outputs were rolled back and the "
            + "Reference session was restored.",
            deleteException);

        return;
    }
    catch (Exception saveException)
    {
        ShowError(
            "CRITICAL: Main output rollback succeeded "
            + "but the recovered Reference session "
            + "could not be persisted.",
            saveException);

        Close();
        return;
    }
}
```

### NoReference

No stable NoReference session should remain.

After rollback:

```csharp
try
{
    _sessionService.Delete();

    _currentSession = null;
    _state = UiState.Idle;
    ApplyState();
    return;
}
catch (Exception retryDeleteException)
{
    // Durable file still contains the original active
    // Main transaction. Leave it untouched for startup.
    ShowError(
        "CRITICAL: Main outputs were rolled back, "
        + "but the NoReference journal could not "
        + "be deleted.",
        retryDeleteException);

    Close();
    return;
}
```

---

# 11. R3-009 — MEDIUM — stable Reference startup still uses weak validation

After recovery of Main or when resuming a normal unfinished Reference session, the code still calls:

```csharp
ValidateReferenceOutput(session)
```

That method includes substring-oriented Reference provenance validation.

A provenance file can have valid required fields plus appended/modified text and pass this weaker gate.

Normal Main processing later uses exact validation, so this is not presently destructive, but startup incorrectly tells the user the session is valid and resumes it.

## Required fix

Use:

```csharp
var resumeValidation =
    _validationService
        .ValidateExactReferenceOutput(
            session,
            _templateService);
```

for stable resumed Reference sessions.

The digest-first behavior now makes this robust even if the current template has changed.

---

# 12. R3-010 — MEDIUM — validators need stronger current-version authority and exception safety

## 12.1 Prepared Reference hash fields are optional

Current prepared-session validation roughly says:

```csharp
if (!string.IsNullOrWhiteSpace(ReferenceHash)
    && invalidHash)
{
    error;
}
```

and the same for:

```text
ReferenceProvenanceHash
```

A current-version `CreateReferenceSession()` always writes both.

A session with:

```text
ReferenceCommitPhase.Prepared
ReferenceHash = ""
ReferenceProvenanceHash = ""
```

should not be considered a valid current prepared transaction.

## 12.2 Required current Prepared authority

Require:

```text
WorkflowMode == ReferenceAssisted
ReferenceCommitPhase == Prepared
valid 32-hex ReferenceTransactionId
nonempty ProjectName
valid AssetRoot/AssetName/AssetFolder relationship
valid safe Reference filename/path
valid 64-hex ReferenceHash
valid 64-hex ReferenceProvenanceHash
ReferenceProcessedAt != default
CancelPhase == None
no Main commit metadata
```

## 12.3 Copy-ready hash helper

```csharp
private static bool IsSha256Hex(
    string? value)
{
    return !string.IsNullOrWhiteSpace(value)
        && value.Length == 64
        && value.All(
            Uri.IsHexDigit);
}
```

Then:

```csharp
if (!IsSha256Hex(
        session.ReferenceHash))
{
    errors.Add(
        "Prepared ReferenceHash is missing or invalid.");
}

if (!IsSha256Hex(
        session.ReferenceProvenanceHash))
{
    errors.Add(
        "Prepared ReferenceProvenanceHash is missing or invalid.");
}
```

## 12.4 Malformed replacement paths can throw

`ValidateReferenceReplacementJournal()` calls path helpers that can ultimately invoke:

```csharp
Path.GetFullPath(...)
```

on untrusted JSON strings.

Some malformed values can throw.

Recovery calls structural validation before entering its later phase-mutation `try`.

The outcome is still non-destructive, but a malformed journal can crash the startup event instead of producing the intended fail-closed critical message.

## Required pattern

Wrap untrusted journal structural validation:

```csharp
public ValidationResult
    ValidateReferenceReplacementJournal(
        ReferenceReplacementJournal journal)
{
    ArgumentNullException.ThrowIfNull(journal);

    try
    {
        return ValidateReferenceReplacementJournalCore(
            journal);
    }
    catch (Exception ex)
    {
        return ValidationResult.Failure(
            "Replacement journal contains invalid "
            + "or unusable path metadata: "
            + ex.Message);
    }
}
```

Do the same for prepared-session path validation.

Never let malformed persisted user data escape as an unhandled exception from a validator.

---

# 13. R3-011 — MEDIUM — crash-unsafe public replacement convenience method remains

The new production UI correctly uses:

```text
CreateReferenceReplacementTransaction()
Save journal
CreateReplacementTempFiles()
Save phase
BackupOldReference()
Save phase
PromoteNewReference()
...
```

But the processor still exposes:

```csharp
public ReferenceReplacementTransaction
    PrepareReferenceReplacement(...)
```

which internally performs:

```text
create transaction
create temp files
backup old files
promote new files
return
```

without any durable replacement journal.

This is the exact crash-safety bypass that the new architecture was created to eliminate.

## Required fix

Make it:

```csharp
internal
```

if legacy tests need it, or delete it.

Preferred tests should use a helper in the test assembly:

```csharp
internal static class
    ReplacementTestExtensions
{
    public static
        ReferenceReplacementTransaction
        MaterializeReplacementForTest(
            this AssetProcessorService processor,
            AssetSession oldSession,
            IReadOnlyCollection<string> extensions,
            string source,
            DateTimeOffset processedAt)
    {
        var tx =
            processor.CreateReferenceReplacementTransaction(
                oldSession,
                extensions,
                source,
                processedAt);

        processor.CreateReplacementTempFiles(
            tx,
            extensions);

        processor.BackupOldReference(tx);

        processor.PromoteNewReference(tx);

        return tx;
    }
}
```

The unsafe convenience must not remain part of the public production API.

---

# 14. R3-012 — MEDIUM — `RecoveryCritical` is still giving false confidence

## 14.1 Current phase test

The test named approximately:

```text
R2_002_InterruptionRecovery_HandlesAllEightPhases
```

does this for each enum:

```text
create stable OLD Reference
create in-memory replacement transaction
save journal with selected enum value
invoke recovery
```

It does **not** create:

```text
temp files
backups
partial backup
partial promotion
new canonical files
new session.json
partial cleanup
```

## 14.2 Consequence

All eight phases are tested against almost the same disk state.

For example, a `NewPromotionPending` test should contain old backups and potentially one or both promoted new files.

The existing test leaves the old Reference in its normal canonical place.

The test therefore cannot detect R3-001.

## 14.3 `SessionSwitched` / `CleanupPending` test weakness

For phases requiring NEW canonical state, the current test can enter fail-closed recovery and still pass because it does not assert:

```text
recovery success
journal removed
session authority
expected files
hashes
no residue
no critical close
```

Looping over enum values is not a state-machine test.

## 14.4 Mandatory state materialization helper

Add test helpers that explicitly create each crash boundary.

Example:

```csharp
private static void
    MaterializeOldBackedUp(
        AssetProcessorService processor,
        ReferenceReplacementTransaction tx,
        IReadOnlyCollection<string> extensions)
{
    processor.CreateReplacementTempFiles(
        tx,
        extensions);

    processor.BackupOldReference(
        tx);
}
```

```csharp
private static void
    MaterializeNewReferencePromotedOnly(
        ReferenceReplacementTransaction tx)
{
    File.Move(
        tx.TempNewReferencePath,
        tx.NewSession.ReferenceDestinationPath,
        overwrite: false);

    // leave temp provenance in place
}
```

```csharp
private static void
    MaterializeNewPromoted(
        AssetProcessorService processor,
        ReferenceReplacementTransaction tx,
        IReadOnlyCollection<string> extensions)
{
    MaterializeOldBackedUp(
        processor,
        tx,
        extensions);

    processor.PromoteNewReference(
        tx);
}
```

## 14.5 Mandatory replacement crash tests

At minimum:

```text
Replacement_Prepared_NoTemps
Replacement_Prepared_TempReferenceOnly
Replacement_Prepared_BothTemps

Replacement_OldBackupPending_NoOldMove
Replacement_OldBackupPending_ReferenceMovedOnly
Replacement_OldBackupPending_BothOldMoved

Replacement_OldBackedUp_BothTempsPresent

Replacement_NewPromotionPending_NoPromote
Replacement_NewPromotionPending_ReferencePromotedOnly
Replacement_NewPromotionPending_BothPromoted

Replacement_NewPromoted_DifferentFilename_OldSession
Replacement_NewPromoted_SameFilename_OldSession

Replacement_SessionSwitchPending_DifferentFilename_OldSession
Replacement_SessionSwitchPending_SameFilename_OldSession
Replacement_SessionSwitchPending_NewSessionAlreadySaved

Replacement_SessionSwitched_NewSession
Replacement_CleanupPending_BothBackups
Replacement_CleanupPending_OneBackupAlreadyDeleted
```

Each test must assert:

```text
expected OLD or NEW session authority
expected canonical Reference hash
expected canonical provenance hash
replacement journal removed only on full success
no unexpected .old files
no unexpected .__new_* files
no foreign file deleted
```

## 14.6 Same-filename test is mandatory

```csharp
[Fact]
[Trait("Category", "RecoveryCritical")]
public void
    NewPromoted_SameFilename_OldSession_RollsBackToOldAuthority()
{
    // OLD source: Downloads/old/ref.png
    // NEW source: another directory/ref.png
    // Same filename, different bytes.

    // Materialize:
    // - backups OLD
    // - canonical NEW
    // - session.json OLD
    // - journal NewPromoted

    // Recover.

    // Assert:
    // session.json == OLD hash/provenance hash/time
    // canonical ref hash == OLD
    // canonical provenance hash == OLD
    // backups gone
    // temps gone
    // journal gone
}
```

## 14.7 Prepared Reference directory cleanup test

```csharp
[Fact]
[Trait("Category", "RecoveryCritical")]
public void
    PreparedReference_DirectoriesOnly_RemovesToolOwnedEmptyDirectories()
{
    // Create prepared session.
    // Save journal.
    // Manually create AssetFolder and reference/ only.
    // No Reference image/provenance.
    // Recover.
    //
    // Assert session deleted.
    // Assert tool-owned reference dir absent.
    // Assert tool-owned asset dir absent.
}
```

## 14.8 Main session-delete failure test

Inject `_sessionService.Delete()` failure after successful Main commit.

Assert:

```text
Main outputs rolled back exactly once
Reference session saved/reset
no false second RollbackMain
no CRITICAL close if Reference Save succeeds
```

---

# 15. R3-013 — LOW-MEDIUM — Main provenance authority can drift between journal save and render

A new Main journal persists:

```text
MainProvenanceHash = hash of rendered provenance
```

Later `ProcessMainImage()` renders provenance again.

At the end it assigns:

```csharp
session.MainProvenanceHash =
    hashOfActuallyRenderedProvenance;
```

If the template changes between:

```text
journal save
and
ProcessMainImage provenance render
```

the durable journal and actual output authority diverge.

Normal completion deletes the journal, so this is mainly a crash/TOCTOU issue.

## Required fix

After rendering:

```csharp
var renderedHash =
    Convert.ToHexString(
        SHA256.HashData(
            new UTF8Encoding(false)
                .GetBytes(provenance)))
    .ToLowerInvariant();

if (!string.Equals(
        renderedHash,
        session.MainProvenanceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException(
        "Final provenance content changed after "
        + "the Main transaction journal was prepared. "
        + "No canonical Main output was committed.");
}
```

Do not overwrite the prepared authority hash with a different value.

On success:

```csharp
session.MainProvenanceHash = renderedHash;
```

is harmless but redundant; prefer an assertion and no mutation.

---

# 16. R3-014 — LOW — `PrepareMainCommit()` extension policy differs from caller settings

`PrepareMainCommit()` currently validates using:

```csharp
AppConstants.DefaultImageExtensions
```

while actual Main processing accepts:

```csharp
acceptedExtensions
```

from settings/caller.

The default UI uses the default list, so this is not currently a normal product failure, but the service contract is inconsistent.

## Preferred signature

```csharp
public AssetSession PrepareMainCommit(
    AssetSession session,
    IReadOnlyCollection<string> acceptedExtensions,
    string sourceImagePath,
    string prompt,
    DateTimeOffset processedAt)
```

Use:

```csharp
_validationService.ValidateImageFile(
    sourceImagePath,
    acceptedExtensions);
```

Update test helpers accordingly.

---

# 17. Recommended replacement recovery architecture

This is the single most important simplification.

## 17.1 Principle

`MainForm.Recovery` should decide:

```text
ROLL BACK
or
COMMIT FORWARD
```

It should not individually implement file deletion/restoration.

The processor already owns destructive details.

## 17.2 Phase decision table

| Durable phase | Valid current session authority | Action |
|---|---|---|
| Prepared | OLD | rollback |
| OldBackupPending | OLD | rollback |
| OldBackedUp | OLD | rollback |
| NewPromotionPending | OLD | rollback |
| NewPromoted | OLD | rollback |
| NewPromoted | NEW | commit forward |
| SessionSwitchPending | OLD | rollback |
| SessionSwitchPending | NEW | commit forward |
| SessionSwitched | NEW | commit forward |
| CleanupPending | NEW | commit forward |
| any phase | neither OLD nor NEW | fail closed |
| any stable-only phase | wrong authority | fail closed |

## 17.3 Copy-ready high-level recovery

```csharp
private bool
    RecoverReferenceReplacementJournalIfPresent()
{
    if (!_sessionService
            .ReplacementJournalExists())
    {
        return true;
    }

    ReferenceReplacementJournal journal;

    try
    {
        journal =
            _sessionService
                .LoadReplacementJournal()
            ?? throw new InvalidDataException(
                "Replacement journal is empty.");
    }
    catch (Exception ex)
    {
        return FailReplacementRecovery(
            "Replacement journal could not be read.",
            ex);
    }

    ValidationResult structural;

    try
    {
        structural =
            _validationService
                .ValidateReferenceReplacementJournal(
                    journal);
    }
    catch (Exception ex)
    {
        return FailReplacementRecovery(
            "Replacement journal validation threw unexpectedly.",
            ex);
    }

    if (!structural.IsValid)
    {
        return FailReplacementRecovery(
            string.Join(
                Environment.NewLine,
                structural.Errors));
    }

    AssetSession? current = null;

    if (_sessionService.Exists())
    {
        try
        {
            current =
                _sessionService.Load();
        }
        catch (Exception ex)
        {
            return FailReplacementRecovery(
                "session.json could not be read while "
                + "a replacement journal exists.",
                ex);
        }
    }

    var oldAuthority =
        MatchesReferenceAuthority(
            current,
            journal.OldSession);

    var newAuthority =
        MatchesReferenceAuthority(
            current,
            journal.NewSession);

    if (oldAuthority && newAuthority)
    {
        return FailReplacementRecovery(
            "Old and New replacement authorities "
            + "are not distinguishable.");
    }

    switch (journal.Phase)
    {
        case ReferenceReplacementPhase.Prepared:
        case ReferenceReplacementPhase.OldBackupPending:
        case ReferenceReplacementPhase.OldBackedUp:
        case ReferenceReplacementPhase.NewPromotionPending:
            if (!oldAuthority)
            {
                return FailReplacementRecovery(
                    "Durable session does not match OLD "
                    + "authority for rollback phase.");
            }

            return RollBackReplacementJournal(
                journal);

        case ReferenceReplacementPhase.NewPromoted:
        case ReferenceReplacementPhase.SessionSwitchPending:
            if (oldAuthority)
            {
                return RollBackReplacementJournal(
                    journal);
            }

            if (newAuthority)
            {
                return FinishReplacementCommit(
                    journal);
            }

            return FailReplacementRecovery(
                "Boundary phase has neither OLD nor NEW "
                + "durable session authority.");

        case ReferenceReplacementPhase.SessionSwitched:
        case ReferenceReplacementPhase.CleanupPending:
            if (!newAuthority)
            {
                return FailReplacementRecovery(
                    "Durable session does not match NEW "
                    + "authority for commit phase.");
            }

            return FinishReplacementCommit(
                journal);

        default:
            return FailReplacementRecovery(
                $"Unknown replacement phase: {(int)journal.Phase}");
    }
}
```

This design directly eliminates:

```text
same-filename ambiguity
restore-before-delete ordering bug
blind per-phase deletes
partial-promotion special cases in the form
duplicated destructive logic
```

---

# 18. Repair order for a weaker implementation model

Implement in this exact order.

## Phase 1 — replace bespoke replacement rollback with service rollback

Files:

```text
MainForm.Recovery.cs
AssetProcessorService.Reference.cs
```

Tasks:

```text
[ ] add TransactionFromJournal
[ ] strengthen temp cleanup failure semantics
[ ] add RollBackReplacementJournal
[ ] use RollbackReferenceReplacement for rollback phases
```

Run only new replacement rollback tests.

## Phase 2 — full session authority comparison

Tasks:

```text
[ ] add MatchesReferenceAuthority
[ ] remove ReferenceFilename-only switched checks
[ ] handle same-filename old/new correctly
[ ] require OLD/NEW authority appropriate to phase
```

Run same-filename crash tests first.

## Phase 3 — commit-forward cleanup

Tasks:

```text
[ ] add FinishReplacementCommit
[ ] reuse CleanupReplacementBackups
[ ] verify current session == NewSession
[ ] delete replacement journal only after cleanup success
[ ] preserve journal + close on incomplete cleanup
```

## Phase 4 — initial Reference exception/recovery cleanup

Tasks:

```text
[ ] keep preparedSession outside try
[ ] reconcile every ProcessReference exception
[ ] call RollbackReference even when both output files absent
[ ] remove tool-created empty directories
[ ] preserve journal/close when rollback cannot be proven
```

## Phase 5 — Main polish

Tasks:

```text
[ ] preflight Reference-assisted destinations before Main journal save
[ ] fix session-delete-failure double rollback
[ ] compare rendered provenance hash against prepared hash
[ ] use caller extension set in PrepareMainCommit
```

## Phase 6 — validator hardening

Tasks:

```text
[ ] require Prepared ReferenceHash
[ ] require Prepared ReferenceProvenanceHash
[ ] require ProjectName/processedAt/current-version fields
[ ] convert malformed path exceptions to ValidationResult.Failure
[ ] exact-validate stable Reference resume
```

## Phase 7 — remove unsafe service escape hatch

```text
[ ] internalize/delete public PrepareReferenceReplacement
[ ] migrate tests to explicit test helper
```

## Phase 8 — real RecoveryCritical matrix

Do not finish until actual disk-state tests exist.

---

# 19. Required post-repair static searches

```powershell
rg -n "ReferenceFilename.*==|ReferenceFilename.*Equals|Equals\(.*ReferenceFilename" `
  src/AssetProvenanceHelper/MainForm.Recovery.cs
```

Expected:

```text
no filename-only authority decisions
```

---

```powershell
rg -n "File\.Delete\(" `
  src/AssetProvenanceHelper/MainForm.Recovery.cs
```

Expected:

Prefer **zero transaction-file deletes** in replacement recovery. Destructive replacement cleanup should be delegated to processor ownership-checked methods.

---

```powershell
rg -n "DeleteReplacementJournal" `
  src/AssetProvenanceHelper
```

Review every result.

Required invariant:

```text
journal deletion only after:
verified rollback
OR
verified commit + verified cleanup
```

---

```powershell
rg -n "PrepareReferenceReplacement" `
  src
```

Expected:

```text
no public crash-unsafe production method
```

---

```powershell
rg -n "ValidateReferenceOutput\(session\)" `
  src/AssetProvenanceHelper/MainForm.Recovery.cs
```

Expected:

```text
stable Reference recovery uses exact/digest validation
```

---

```powershell
rg -n "MainProvenanceHash\s*=" `
  src/AssetProvenanceHelper
```

Inspect all assignments.

Required invariant:

```text
prepared Main authority is not silently changed after durable journal save
```

---

# 20. Full automated test gate

After repair:

```powershell
dotnet --info
```

Verify SDK:

```text
8.0.418
```

Do not modify `global.json` merely because another machine lacks that SDK.

Then:

```powershell
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

## Recovery-critical gate

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

## 20x flakiness gate

```powershell
for ($i = 1; $i -le 20; $i++)
{
    Write-Host "Release run $i/20"

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

Acceptance:

```text
20/20 pass
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

Verify smoke JSON includes:

```text
CommitSha
ExecutableSha256
Product version
Templates verified
window created
correct window title
IconVerified = true
GracefulShutdownVerified = true
StartupElapsedMs
archive/hash
Status = PASS
```

---

# 21. Manual crash/recovery matrix

The final implementation should be manually spot-checked with copied test workspaces or debug hooks.

## Reference creation

```text
[ ] crash before directories
[ ] crash after AssetFolder creation
[ ] crash after reference/ creation
[ ] crash after image copy
[ ] crash after provenance write
[ ] crash after exact validation
[ ] crash after stable session save
```

Expected:

```text
either exact completed Reference
OR exact rollback to pre-operation state
never orphaned tool-owned empty dirs
never unknown deletion
```

## Replacement

```text
[ ] Prepared no temps
[ ] Prepared temp ref only
[ ] Prepared both temps
[ ] OldBackupPending old ref moved only
[ ] OldBackupPending both old files moved
[ ] OldBackedUp
[ ] NewPromotionPending nothing promoted
[ ] NewPromotionPending ref promoted only
[ ] NewPromotionPending both promoted
[ ] NewPromoted with old session
[ ] NewPromoted same filename with old session
[ ] SessionSwitchPending old session
[ ] SessionSwitchPending same filename old session
[ ] SessionSwitchPending new session already written
[ ] SessionSwitched
[ ] CleanupPending both backups
[ ] CleanupPending one backup already deleted
```

## Main

```text
[ ] root Main collision
[ ] final provenance collision
[ ] same-extension ingame collision
[ ] different-extension ingame collision
[ ] source changes after prepared hash
[ ] final template changes after journal save
[ ] session Delete fails after successful commit
[ ] Reference session Save fails after rollback
[ ] NoReference session Delete retry fails
```

---

# 22. Final Definition of Done

Do not claim the project is final until every item below is true.

## Replacement transaction

```text
[ ] every canonical mutation has a prior durable phase
[ ] real disk state exists for every phase test
[ ] same-filename replacement recovery works
[ ] no filename-only authority comparison exists
[ ] rollback phases use ownership-checked processor rollback
[ ] commit phases use ownership-checked processor cleanup
[ ] no unknown temp/canonical/backup is deleted
[ ] cleanup failure preserves CleanupPending journal
[ ] replacement journal is deleted only after full rollback/commit
[ ] malformed journal fails closed without unhandled exception
```

## Reference creation

```text
[ ] prepared session exists before first managed mutation
[ ] ProcessReference exception always reconciles prepared journal
[ ] rollback uncertainty closes app and preserves journal
[ ] directories-only crash removes tool-created empty directories
[ ] pre-existing asset/reference directories are never deleted
```

## Main

```text
[ ] normal destination collision rejected before durable Main journal
[ ] processor repeats preflight after journal for race safety
[ ] session deletion failure causes exactly one rollback
[ ] Reference session is restored durably after rollback
[ ] NoReference never stores invalid idle session
[ ] rendered provenance hash equals prepared journal authority
```

## Provenance

```text
[ ] ReferenceProvenanceHash authoritative when present
[ ] MainProvenanceHash authoritative when present
[ ] current templates only legacy fallback
[ ] stable Reference resume uses exact/digest validation
[ ] template change cannot delete a valid completed asset
```

## UI / functional rework

```text
[ ] Project input absent
[ ] browser-neutral Image Download Folder
[ ] independent Reference/Main selection
[ ] independent Refresh/Choose/Drop
[ ] Drop file here buttons
[ ] extensionless Asset Name
[ ] root Main original filename
[ ] ingame AssetName + same extension
[ ] NoReference no Reference output
[ ] explicit Final Prompt
[ ] bounded CTA pulse
[ ] independent red validation fields
[ ] header/logo/version
[ ] help overlay blocks background UI
[ ] Made by CeeGore
[ ] no main-window scrolling at required targets
```

## Automated

```text
[ ] Debug warn-as-error build PASS
[ ] Debug full suite PASS
[ ] Release warn-as-error build PASS
[ ] Release full suite PASS
[ ] RecoveryCritical real-state matrix PASS
[ ] Release 20/20 PASS
[ ] win-x64 publish PASS
[ ] smoke PASS
[ ] coverage gate PASS
```

## Manual

```text
[ ] 1366×768 @100%
[ ] 1920×1080 @100%
[ ] 1920×1080 @125%
[ ] 1920×1080 @150%
[ ] 2560×1440 @125%
[ ] Reference-assisted E2E
[ ] NoReference E2E
[ ] replacement E2E
[ ] cancel E2E
[ ] crash-state spot checks
```

---

# 23. Final conclusion

The current code is **significantly closer to acceptance** than it was in either `bugs1.md` or `bugs2.md`.

Most ordinary behavior and many safety primitives are now correctly implemented.

The remaining issue is not that the architecture needs another rewrite. In fact, the safest repair is the opposite:

> **Delete duplicate recovery file-manipulation logic from `MainForm` and reuse the processor’s existing ownership-checked `RollbackReferenceReplacement()` and `CleanupReplacementBackups()` as the authoritative mutation primitives.**

Then add a full OLD-vs-NEW session authority comparison and test the actual files represented by every phase.

That should resolve the largest remaining class of defects with less code and fewer independent safety implementations.

**Current acceptance state: FAIL — known recovery defects remain.**

A further paranoid retest should be performed after R3-001 through R3-012 have landed and the real-state `RecoveryCritical` suite passes.

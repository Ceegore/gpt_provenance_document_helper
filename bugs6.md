# AI Asset Provenance Helper — Sixth Paranoid Retest & Repair Guide

**File:** `bugs6.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `f1271f993986bb26eafe61339ffc3a66765df3bd`  
**Previous audited commit:** `e6f6381af006616d0663be5337e712cffa53cb7d`  
**Previous audit:** `bugs5.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — all R5 findings are materially repaired, but the independent fresh pass found two remaining transaction-boundary defects.**

This is the cleanest repository revision audited so far.

The R5 repair commit successfully addresses the previous checklist:

- deterministic replacement temp paths are now validated and parent-confined;
- malformed replacement transaction paths fail validation rather than reaching deletion;
- raw file/replacement/Main/Reference mutators were internalized;
- replacement forward mutators re-run transaction/path/reparse validation before mutation;
- Save-NewSession replacement failure now performs one rollback/finalization path;
- overlapping replacement-journal tests now use real deterministic transactions;
- `NewPromotionPending` with both NEW files already promoted now has a dedicated recovery test;
- unknown configured image extensions are rejected;
- unknown image extensions no longer pass magic-byte validation automatically.

Those fixes should be retained.

The fresh audit found two defects that were not part of `bugs5.md`:

1. **R6-001 — HIGH:** initial Reference creation persists a Prepared hash/provenance authority but does not require the source/template bytes used by `ProcessReference()` to still match that durable authority **before canonical mutation**. A hard crash in the resulting drift window can leave canonical files that the prepared journal correctly refuses to delete, stranding automatic recovery.
2. **R6-002 — MEDIUM:** post-commit UI work remains inside transaction failure catches. If UI/status/message work throws **after the durable commit point**, the code can treat a successful transaction as failed and destructively roll it back.

A small test-quality gap is also recorded as R6-003.

No additional broad state-machine redesign is recommended.

---

# 0.2 Current repository state

Current `main`:

```text
f1271f993986bb26eafe61339ffc3a66765df3bd
```

Commit:

```text
fix(r5): address all defects R5-001 through R5-007 from bugs5.md
```

Parent:

```text
e6f6381af006616d0663be5337e712cffa53cb7d
```

The R5 commit modifies the expected areas:

```text
bugs5.md
scripts/run_smoke_tests.ps1
MainForm.ReferenceWorkflow.cs
AssetProcessorService.FileOps.cs
AssetProcessorService.Main.cs
AssetProcessorService.Reference.cs
SessionService.cs
ValidationService.Session.cs
ValidationService.cs
Bugs3ParanoidTests.cs
```

---

# 0.3 Execution evidence limitation

The connected GitHub status surface currently exposes:

```text
statuses: []
```

for:

```text
f1271f993986bb26eafe61339ffc3a66765df3bd
```

The available commit workflow-run wrapper likewise exposes no run for this SHA.

Therefore this audit does **not** claim an independently observed GitHub-hosted Windows CI pass.

That exact-environment limitation is **deferred execution evidence, not a source-level blocker by itself**.

The FAIL verdict is caused by the source-level defects below.

---

# 1. Full R5 retest

| R5 ID | Status | Sixth-pass conclusion |
|---|---|---|
| R5-001 replacement temp path confinement | **FIXED** | transaction validator now checks deterministic backup + temp paths and exact Reference-folder parent |
| R5-002 raw mutation API/durability boundary | **FIXED to accepted small-scope solution** | raw mutation methods are internal; tests assert they are not public |
| R5-003 forward replacement revalidation | **FIXED** | `CreateReplacementTempFiles`, `BackupOldReference`, `PromoteNewReference` call shared transaction safety validation |
| R5-004 Save-New failure double rollback | **FIXED in source** | failure now rolls back once, finalizes OLD once, and returns |
| R5-005 invalid overlap tests | **FIXED** | tests now construct real valid replacement transactions and mutate only cloned current `session.json` |
| R5-006 missing both-promoted crash state | **FIXED** | dedicated `NewPromotionPending + both promoted + OLD session` recovery test exists |
| R5-007 unknown image extension policy | **FIXED** | settings reject unknown extensions and magic-byte fallback is false |

No R5 item needs another architectural repair.

---

# 2. Current finding summary

| ID | Severity | Area | Summary |
|---|---|---|---|
| R6-001 | **HIGH** | initial Reference Prepared authority | `ProcessReference()` does not freeze source/provenance bytes to the already-durable Prepared session before canonical writes |
| R6-002 | **MEDIUM** | transaction/UI commit boundary | post-commit UI exceptions can route into destructive rollback logic after a transaction is already durably complete |
| R6-003 | **LOW** | test assertion quality | `R5_004_SaveNewSessionFailure_RollsBackExactlyOnce` does not actually assert exactly-one rollback/finalization |

---

# 3. R6-001 — HIGH — initial Reference Prepared authority can drift before canonical mutation

## 3.1 Correct existing write-ahead design

Initial Reference creation now has a proper durable Prepared session concept.

`CreateReferenceSession()` computes and stores:

```text
ReferenceCommitPhase = Prepared
ReferenceTransactionId
ReferenceSourcePath
ReferenceDestinationPath
ReferenceFilename
ReferenceProvenancePath
ReferenceHash
ReferenceProvenanceHash
ReferenceProcessedAt
```

`MainForm.HandleReference()` then does:

```text
CreateReferenceSession()
Save(preparedSession)
ProcessReference(preparedSession, ...)
Save(completedSession)
```

The intended invariant is therefore clear:

> once the Prepared session is durable, its Reference hash/provenance hash are transaction authority.

That authority must control all later mutation.

## 3.2 Current `ProcessReference()` violates that invariant

Current `ProcessReference()`:

```text
1. checks Prepared phase
2. checks path safety
3. creates asset/reference directories
4. computes a NEW local sourceHash from sourceImagePath
5. copies sourceImagePath directly to canonical ReferenceDestinationPath
6. validates copied image
7. checks canonical hash == NEW local sourceHash
8. renders provenance from current template + passed processedAt
9. writes canonical provenance
10. only then calls ValidateExactReferenceOutput(session)
```

It does **not**, before mutation, require:

```text
current source hash == session.ReferenceHash
sourceImagePath == session.ReferenceSourcePath
processedAt == session.ReferenceProcessedAt
rendered provenance hash == session.ReferenceProvenanceHash
```

This is inconsistent with the replacement flow, which was correctly hardened against exactly this class of authority drift.

---

# 4. R6-001A — source changes after Prepared journal is saved

## 4.1 Crash sequence

Assume:

```text
Prepared ReferenceHash = H1
```

Timeline:

```text
T0 CreateReferenceSession()
   hashes source -> H1

T1 Save(session.json)
   durable Prepared authority = H1

T2 selected source changes externally
   current source = H2

T3 ProcessReference()
   computes local sourceHash = H2

T4 ProcessReference()
   creates asset/reference folders

T5 ProcessReference()
   copies H2 directly to canonical ReferenceDestinationPath

--- HARD CRASH HERE ---
```

Disk after crash:

```text
session.json = Prepared, expects H1
canonical Reference image = H2
canonical provenance = absent
```

## 4.2 Startup recovery

Startup correctly recognizes the Prepared session first.

Exact output validation fails.

Recovery then calls:

```text
RollbackReference(session)
```

Rollback correctly uses durable `session.ReferenceHash = H1` as deletion ownership authority.

Canonical image is H2.

Therefore rollback refuses to delete it as unknown content.

Result:

```text
Prepared journal preserved
H2 canonical preserved
automatic recovery fails closed
application closes
manual intervention required
```

Fail-closed deletion is correct.

The defect is that normal production flow was allowed to create an H2 canonical output under an H1 journal in the first place.

---

# 5. R6-001B — Reference provenance/template drift

`CreateReferenceSession()` computes:

```text
ReferenceProvenanceHash = P1
```

from the current Reference template and Prepared timestamp.

But `ProcessReference()` later renders provenance again from:

```text
current template
passed processedAt
session.ProjectName
session.ReferenceFilename
```

without requiring the rendered hash to equal P1 before writing it.

Timeline:

```text
T0 Prepared provenance hash = P1
T1 session.json saved
T2 reference template changes -> renders P2
T3 Reference image H1 copied successfully
T4 canonical Reference provenance P2 written

--- HARD CRASH HERE ---
```

Startup:

```text
Prepared journal expects P1
image can match H1
provenance P2 does not match Prepared authority
```

`RollbackReference()` correctly refuses to delete P2 because exact provenance ownership cannot be proven from the durable journal.

Again:

```text
automatic recovery becomes stranded
manual intervention required
```

The same issue exists if a caller supplies a `processedAt` different from:

```text
session.ReferenceProcessedAt
```

---

# 6. R6-001C — duplicated arguments permit authority mismatch

The prepared session already contains:

```text
ReferenceSourcePath
ReferenceProcessedAt
ReferenceFilename
ProjectName
ReferenceHash
ReferenceProvenanceHash
```

Yet `ProcessReference()` still receives:

```csharp
string sourceImagePath,
DateTimeOffset processedAt
```

as separate arguments.

This permits an internal caller to pass values inconsistent with the journal.

Even though MainForm currently passes the same variables, the transaction method should not accept duplicate mutable authority when the durable session is the SSOT.

---

# 7. Required R6-001 repair

## 7.1 Preferred API

Reduce the mutator to:

```csharp
internal AssetSession ProcessReference(
    AssetSession session,
    AppSettings settings)
```

Do not accept separate source/timestamp authority.

Use:

```text
session.ReferenceSourcePath
session.ReferenceProcessedAt
session.ReferenceFilename
session.ProjectName
session.ReferenceHash
session.ReferenceProvenanceHash
```

exclusively.

If changing the signature causes unnecessary churn, retain the arguments but require exact equality before mutation.

---

# 8. Required pre-mutation authority gate

Before **Directory.CreateDirectory** and before **any canonical/temp mutation**:

```csharp
private void
    RequirePreparedReferenceAuthority(
        AssetSession session,
        AppSettings settings,
        string sourceImagePath,
        DateTimeOffset processedAt)
{
    if (session.ReferenceCommitPhase
        != ReferenceCommitPhase.Prepared)
    {
        throw new InvalidOperationException(
            "Reference session is not Prepared.");
    }

    if (!ValidationService.PathsEqual(
            sourceImagePath,
            session.ReferenceSourcePath))
    {
        throw new InvalidOperationException(
            "Reference source path does not match "
            + "the Prepared session authority.");
    }

    if (!processedAt.EqualsExact(
            session.ReferenceProcessedAt))
    {
        throw new InvalidOperationException(
            "Reference processedAt does not match "
            + "the Prepared session authority.");
    }

    var sourceValidation =
        _validationService.ValidateImageFile(
            session.ReferenceSourcePath,
            settings.AcceptedExtensions);

    if (!sourceValidation.IsValid)
    {
        throw new InvalidDataException(
            string.Join(
                Environment.NewLine,
                sourceValidation.Errors));
    }

    var currentSourceHash =
        ComputeSha256(
            session.ReferenceSourcePath);

    if (!string.Equals(
            currentSourceHash,
            session.ReferenceHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new IOException(
            "Reference source changed after "
            + "the Prepared session was persisted.");
    }

    var generationDate =
        session.ReferenceProcessedAt
            .ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);

    var provenance =
        _templateService.RenderReference(
            session.ReferenceFilename,
            session.ProjectName,
            generationDate);

    var provenanceHash =
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                new System.Text.UTF8Encoding(false)
                    .GetBytes(provenance)))
        .ToLowerInvariant();

    if (!string.Equals(
            provenanceHash,
            session.ReferenceProvenanceHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Reference provenance changed after "
            + "the Prepared session was persisted.");
    }
}
```

Call this before:

```csharp
Directory.CreateDirectory(assetFolder);
Directory.CreateDirectory(referenceFolder);
```

so authority drift has zero filesystem side effects.

---

# 9. Use Prepared authority during the copy too

After preflight, do not change authority to a new local hash.

Bad conceptual pattern:

```text
sourceHash = hash current source
copy
compare destination to sourceHash
```

Required:

```text
Prepared session.ReferenceHash remains authority throughout
```

After copy:

```csharp
var copiedHash =
    ComputeSha256(
        referenceDestination);

if (!string.Equals(
        copiedHash,
        session.ReferenceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new IOException(
        "Copied Reference does not match "
        + "Prepared ReferenceHash.");
}
```

Render provenance **once before mutation**, verify its hash against:

```text
session.ReferenceProvenanceHash
```

and write that already-verified string.

Do not render it a second time later.

---

# 10. Optional stronger source-copy locking

For maximal crash/TOCTOU resilience, avoid:

```text
hash source
close source
open source again for File.Copy
```

where possible.

A stronger helper can hold the source file open with sharing that prevents write/delete while copying:

```csharp
using var sourceStream =
    new FileStream(
        session.ReferenceSourcePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read);

using var destinationStream =
    new FileStream(
        referenceDestination,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None);

sourceStream.CopyTo(
    destinationStream);

destinationStream.Flush(true);
```

Then verify the destination hash against Prepared `ReferenceHash`.

This is optional if the current before/after hash checks are retained, but it narrows the source-race window further.

---

# 11. R6-001 mandatory tests

Mark these:

```csharp
[Trait("Category", "RecoveryCritical")]
```

## 11.1 Source drift before ProcessReference

```csharp
[Fact]
public void
    InitialReference_Prepared_SourceChangesBeforeProcess_NoMutation()
{
    using var workspace =
        new TestWorkspace();

    var processor =
        workspace.CreateAssetProcessor();

    var settings =
        workspace.CreateSettings();

    var sessionService =
        workspace.CreateSessionService();

    var source =
        workspace.CreateImage(
            "reference.png",
            new byte[] { 1, 2, 3 });

    var prepared =
        processor.CreateReferenceSession(
            settings,
            "r6_source_drift",
            source,
            DateTimeOffset.Now);

    sessionService.Save(
        prepared);

    // Change source after durable Prepared authority exists.
    File.WriteAllBytes(
        source,
        TestWorkspace.EnsureMagicBytes(
            source,
            new byte[] { 9, 9, 9 }));

    Assert.Throws<IOException>(
        () =>
            processor.ProcessReference(
                prepared,
                settings,
                source,
                prepared.ReferenceProcessedAt));

    Assert.False(
        Directory.Exists(
            prepared.AssetFolder),
        "Authority drift must be rejected before folder creation.");

    Assert.True(
        sessionService.Exists(),
        "Prepared journal remains durable.");
}
```

Adjust helper calls to actual test utility signatures.

---

# 12. Template drift test

```text
InitialReference_Prepared_TemplateChangesBeforeProcess_NoMutation
```

Steps:

```text
CreateReferenceSession -> P1
Save Prepared
modify reference.md
call ProcessReference
expect InvalidDataException before directory creation
Prepared journal remains
no canonical image
no canonical provenance
```

---

# 13. Timestamp/argument mismatch test

```text
InitialReference_Prepared_ProcessedAtMismatch_NoMutation
```

If the duplicate `processedAt` parameter remains:

```text
prepared at T1
call ProcessReference(..., T2)
expect failure before filesystem mutation
```

If parameter is removed, this test becomes unnecessary because the API itself eliminates the mismatch class.

---

# 14. Recovery tests for foreign drift states

Even after production creation is fixed, startup should remain fail-closed for manually corrupted states.

Keep/add:

```text
PreparedReference_ForeignCanonicalImage_PreservesAndCloses
PreparedReference_ForeignCanonicalProvenance_PreservesAndCloses
```

These are not expected normal-production states after the repair, but they prove recovery never deletes unknown files.

---

# 15. R6-002 — MEDIUM — UI exceptions after durable commit can trigger destructive rollback

The transaction/recovery code is now careful about disk authority, but the GUI still mixes:

```text
transactional work
durable commit/finalization
UI status/reset work
```

inside the same broad `try/catch`.

This means an exception after durable commit can be mistaken for a transactional failure.

---

# 16. R6-002A — Main completion can be rolled back after session.json was already deleted

Current `ExecuteMainCommit()` has one broad outer `try`.

Inside it:

```text
ProcessMainImage()
Delete session.json
set _lastCompletedAssetFolderPath
set _currentSession = null
set state Idle
clear controls
clear selections
AddStatus(...)
ApplyState()
ShowMessageBox("Asset completed successfully.")
```

The generic outer:

```csharp
catch (Exception ex)
```

then calls:

```text
TryReconcileFailedMainCommit()
```

which calls:

```text
RollbackMain()
```

## 16.1 Durable commit point

Once both are true:

```text
complete Main asset validated
session.json successfully deleted
```

the transaction is durably complete.

No later UI exception should be permitted to reinterpret it as an incomplete transaction.

## 16.2 Concrete failure

Timeline:

```text
T0 ProcessMainImage completes:
   root Main present
   final provenance present
   ingame present
   hashes exact

T1 SessionService.Delete succeeds
   no active transaction journal remains
   durable commit complete

T2 a UI operation throws:
   AddStatus
   ApplyState
   ShowMessageBox
   provider/test hook
   disposal/race/WinForms exception

T3 broad catch treats this as Main processing failure

T4 TryReconcileFailedMainCommit()

T5 RollbackMain(session,...)
   verifies tool-owned complete outputs
   deletes them
   resets Main metadata

T6 Reference-assisted flow saves Reference session again
```

A UI-only failure after commit has now destroyed a successfully completed asset.

No user-selected source is lost, but product output is incorrectly undone.

---

# 17. Deterministic R6-002 Main regression test

The form already has an injectable:

```csharp
MessageBoxProvider
```

Use it to throw **only** for the final successful completion message:

```csharp
MainForm.MessageBoxProvider =
    (_, message, caption, _, _) =>
    {
        if (caption == "Asset Complete")
        {
            throw new InvalidOperationException(
                "Simulated post-commit UI failure.");
        }
    };
```

Run a Reference-assisted Main completion.

Required assertions:

```text
session.json remains deleted
root Main still exists
final provenance still exists
ingame still exists
root/ingame hashes still match MainHash
Reference still exists
no new Reference session journal is created
```

Current code is expected to fail this test because it routes the UI exception into Main rollback.

---

# 18. R6-002B — initial Reference can also rollback after stable session save

Current `HandleReference()` has one broad `try`.

It performs:

```text
Create Prepared
Save Prepared
ProcessReference
Save completed stable Reference session
_currentSession = completed
_state = ReferenceReady
label update
selection clear
AddStatus x3
ApplyState
```

Any exception after:

```text
Save(completedSession)
```

still enters the same catch.

The catch then calls:

```text
RollbackReference(preparedSession)
Delete session.json
```

Note that `preparedSession` and `completedSession` are the same mutated object in the normal processor flow.

So after the stable session is durably saved, a later GUI exception can still cause:

```text
canonical Reference image deleted
canonical Reference provenance deleted
stable session.json deleted
```

This is the same commit-boundary mistake.

---

# 19. Required Reference commit boundary

The transaction try should end at:

```csharp
_sessionService.Save(
    completedSession);
```

After that, disk/session authority is stable.

Post-commit UI should be separate:

```csharp
_currentSession =
    completedSession;

_state =
    UiState.ReferenceReady;

try
{
    lblReference.Text =
        $"Saved reference: {completedSession.ReferenceFilename}";

    SetSelectedImage(
        ImageSlot.Reference,
        null);

    AddStatus(...);
    ApplyState();
}
catch (Exception uiException)
{
    // DO NOT rollback stable Reference.
    // Keep completed session + files.
    // Try to report; close if GUI cannot safely continue.
    TryReportPostCommitUiError(
        "Reference was saved successfully, "
        + "but the interface could not be refreshed.",
        uiException);
}
```

The key rule:

> after stable session save succeeds, no UI exception may call `RollbackReference()`.

---

# 20. R6-002C — NoReference initialization has the same category of boundary

`HandleNoReferenceMainImage()` currently groups:

```text
CreateNoReferenceMainSession
Save active journal
AddStatus("No-reference Main session saved.")
```

inside one `try`.

If `AddStatus()` throws after `Save(session)`:

```text
durable active NoReference journal exists
method catches and returns
form remains conceptually Idle
Main mutation never runs
```

That creates a live durable journal that the current UI does not adopt as `_currentSession`.

It will be recovered on restart, but while the form remains open another action could overwrite/reuse session state.

This is lower-probability than the destructive Main/Reference case, but the same rule solves it:

```text
durable transaction state changes must be separated from non-authoritative UI/status work
```

After saving the NoReference journal:

```text
either proceed to ExecuteMainCommit
or close if the GUI cannot continue safely
```

Do not simply return while leaving an active durable journal behind.

---

# 21. Required R6-002 design rule

For every transaction:

```text
PREPARE
PERSIST AUTHORITY
MUTATE
VALIDATE
FINALIZE DURABLE STATE
------------------------- durable boundary
UPDATE UI
REPORT SUCCESS
```

Exceptions before the line may invoke rollback/recovery.

Exceptions after the line must **never** invoke transaction rollback.

---

# 22. Suggested Main refactor

Conceptual structure:

```csharp
private void ExecuteMainCommit(...)
{
    string committedFilename;

    try
    {
        committedFilename =
            _assetProcessorService.ProcessMainImage(...);

        try
        {
            _sessionService.Delete();
        }
        catch (Exception deleteException)
        {
            HandleMainSessionDeleteFailure(
                session,
                committedFilename,
                deleteException);
            return;
        }
    }
    catch (AssetProcessingException ape)
        when (!ape.RollbackComplete)
    {
        ...
        return;
    }
    catch (Exception ex)
    {
        if (TryReconcileFailedMainCommit(...))
        {
            ShowError(...);
        }
        return;
    }

    // DURABLE COMMIT POINT:
    // complete outputs exist and active session journal is gone.

    CompleteMainUiAfterDurableCommit(
        session,
        committedFilename);
}
```

Then:

```csharp
private void CompleteMainUiAfterDurableCommit(
    AssetSession session,
    string committedFilename)
{
    _lastCompletedAssetFolderPath =
        session.AssetFolder;

    _currentSession =
        null;

    _state =
        UiState.Idle;

    try
    {
        txtPrompt.Clear();
        txtAssetFolderName.Clear();
        lblReference.Text =
            "Saved reference: none";

        SetSelectedImage(
            ImageSlot.Reference,
            null);

        SetSelectedImage(
            ImageSlot.Main,
            null);

        ClearValidationVisuals();

        AddStatus(
            $"Main image copied: {committedFilename}");

        AddStatus(
            "Ingame copy created.");

        AddStatus(
            "Final provenance created.");

        AddStatus(
            "Asset completed.");

        ApplyState();

        ShowMessageBox(
            "Asset completed successfully.",
            "Asset Complete",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
    catch (Exception ex)
    {
        // Never roll back committed asset.
        // Best effort report/close.
        try
        {
            ShowMessageBox(
                "The asset was completed successfully, "
                + "but the interface could not be refreshed."
                + Environment.NewLine
                + Environment.NewLine
                + ex.Message,
                "Post-Commit UI Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // UI itself is unreliable.
        }

        Close();
    }
}
```

Closing after an unrecoverable UI error is safer than rolling back a durable asset.

---

# 23. Suggested Reference refactor

Use explicit transaction completion state:

```csharp
AssetSession? preparedSession =
    null;

AssetSession? stableSession =
    null;

try
{
    preparedSession =
        ...;

    _sessionService.Save(
        preparedSession);

    stableSession =
        _assetProcessorService.ProcessReference(...);

    _sessionService.Save(
        stableSession);
}
catch (Exception ex)
{
    // Only pre-stable-save failures arrive here.
    ReconcileReferenceTransaction(
        preparedSession,
        ex);

    return;
}

// Stable session is durable here.
// No rollback below this line.

_currentSession =
    stableSession;

_state =
    UiState.ReferenceReady;

try
{
    ...
}
catch (Exception uiEx)
{
    // Preserve files/session.
    // Report/close only.
}
```

---

# 24. R6-002 mandatory tests

Mark transaction tests:

```csharp
[Trait("Category", "RecoveryCritical")]
```

## Main

```text
Main_PostCommitSuccessMessageThrows_DoesNotRollbackCompletedAsset
Main_PostCommitUiFailure_DoesNotRecreateReferenceSession
```

Minimum deterministic version can use `MessageBoxProvider`.

## Reference

Add a test hook at the stable commit boundary if needed:

```csharp
[ThreadStatic]
internal static Action<AssetSession>?
    OnReferenceStableSessionSavedHook;
```

Invoke immediately after:

```csharp
_sessionService.Save(completedSession);
```

Then test:

```text
Reference_PostStableSaveUiFailure_DoesNotRollbackReference
```

Or refactor UI finalization into an internal method that can be invoked with a throwing hook/provider.

## NoReference

```text
NoReference_JournalSaved_PostSaveUiFailure_DoesNotLeaveUsableUntrackedTransaction
```

Expected acceptable outcomes:

```text
Main commit continues
OR
form closes with durable journal preserved for startup recovery
```

Not acceptable:

```text
form remains usable as Idle while active session.json exists
```

---

# 25. R6-003 — LOW — “RollsBackExactlyOnce” test does not assert exactly once

Current test:

```text
R5_004_SaveNewSessionFailure_RollsBackExactlyOnce
```

injects a `Save(NewSession)` failure and checks that an error message reports restoration.

That is useful, but it does not actually observe:

```text
rollback call count == 1
```

or prove:

```text
journal deletion occurs only after OLD session save
```

The source currently implements the intended single path, so this is not a production defect.

Still, the test name overstates what it proves.

## Fix options

Either rename:

```text
SaveNewSessionFailure_RestoresOldSessionAndReturns
```

or instrument the rollback/finalization boundary and assert once.

A practical hook:

```csharp
[ThreadStatic]
internal static Action?
    OnRollbackReferenceReplacementInvoked;
```

Then:

```text
count == 1
```

Alternatively assert a side effect that would fail on a second rollback.

---

# 26. Additional fresh-pass checks that passed

The sixth audit did **not** find a new defect in these areas:

```text
[PASS] deterministic replacement backup paths
[PASS] deterministic replacement temp paths
[PASS] exact temp parent confinement
[PASS] malformed transaction path validation
[PASS] replacement Reference-folder reparse rejection
[PASS] forward replacement revalidation at mutation boundary
[PASS] replacement source hash freeze
[PASS] replacement provenance hash freeze
[PASS] exact OLD replacement preflight
[PASS] same-filename OLD/NEW authority distinction
[PASS] active Main/cancel/prepared-Reference overlap fail-close source logic
[PASS] corrected valid overlap test fixtures
[PASS] cleanup journal ordering
[PASS] rollback post-validation exactness
[PASS] raw mutator visibility reduction
[PASS] Main prepared source hash freeze
[PASS] Main prepared provenance hash freeze
[PASS] unknown image extension rejection
[PASS] PNG/JPEG/WebP signature validation
[PASS] cancellation exact ownership checks
[PASS] destructive root/asset/reference/ingame path confinement
[PASS] NoReference journal-before-write structure
[PASS] stable Reference startup exact validation
[PASS] smoke requires icon
[PASS] smoke requires graceful shutdown
[PASS] self-contained publish path
[PASS] v1.1.0 product version remains configured
```

Do not reopen these without a specific failing test.

---

# 27. Main source authority comparison — confirmed already good

The fresh pass specifically checked Main because R6-001 exists in initial Reference.

Main already does the correct equivalent:

```text
PrepareMainCommit hashes source -> session.MainHash
session is persisted
ProcessMainImage hashes current source
requires current source hash == session.MainHash
copies to deterministic temp
requires temp hash == session.MainHash
renders final provenance
requires rendered provenance hash == session.MainProvenanceHash
only then promotes canonical outputs
```

Therefore do **not** rewrite Main source authority handling.

R6-002 concerns only its **post-commit UI boundary**, not its byte authority.

---

# 28. Static searches after repair

## Initial Reference prepared authority

```powershell
rg -n "ProcessReference\(" `
  src/AssetProvenanceHelper `
  tests/AssetProvenanceHelper.Tests
```

Review production signature.

Preferred:

```text
ProcessReference(AssetSession, AppSettings)
```

No duplicate `sourceImagePath` / `processedAt` transaction authority.

---

```powershell
rg -n "ReferenceHash|ReferenceProvenanceHash" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Required in initial Reference mutation path:

```text
current source == session.ReferenceHash BEFORE Directory.CreateDirectory/copy
rendered provenance == session.ReferenceProvenanceHash BEFORE write
copied destination == session.ReferenceHash
```

---

## Post-commit rollback boundaries

```powershell
rg -n "_sessionService\.Delete\(\)|RollbackMain|TryReconcileFailedMainCommit" `
  src/AssetProvenanceHelper/MainForm.MainWorkflow.cs
```

Manual invariant:

```text
generic catch that can invoke RollbackMain
must end before successful session delete's post-commit UI work
```

---

```powershell
rg -n "_sessionService\.Save\(completedSession\)|RollbackReference|ApplyState|AddStatus" `
  src/AssetProvenanceHelper/MainForm.ReferenceWorkflow.cs
```

Manual invariant:

```text
after stable completed session Save,
UI exceptions must never reach RollbackReference
```

---

# 29. Required Windows execution gate

After source/test repair:

```powershell
dotnet --info
```

Expected SDK:

```text
8.0.418
```

Then:

```powershell
dotnet tool restore
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
0 skipped RecoveryCritical
```

## 20x Release flakiness loop

```powershell
for ($i = 1; $i -le 20; $i++)
{
    Write-Host "Release test run $i/20"

    dotnet test AssetProvenanceHelper.sln `
      -c Release `
      --no-build `
      --logger "console;verbosity=minimal"

    if ($LASTEXITCODE -ne 0)
    {
        throw "Flakiness loop failed on run $i."
    }
}
```

Acceptance:

```text
20/20 PASS
```

---

# 30. Publish / smoke / coverage

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

Verify:

```text
[ ] EXE present
[ ] three templates present
[ ] core runtime assemblies present
[ ] main window appears within 15 seconds
[ ] exact window title
[ ] application icon extractable
[ ] graceful CloseMainWindow shutdown
[ ] no forced termination required
[ ] ProductVersion resolves
[ ] versioned archive created
[ ] EXE SHA-256 recorded
[ ] archive SHA-256 recorded
[ ] StartupElapsedMs recorded
[ ] smoke status PASS
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

Keep the existing required production-class coverage-presence gate.

---

# 31. Final manual regression pass

## Initial Reference

```text
[ ] normal Reference works
[ ] source source-file unchanged after save
[ ] source changes after selection but before CTA -> current selection validation behavior sensible
[ ] source changes after Prepared save -> rejects before folder creation
[ ] template changes after Prepared save -> rejects before folder creation
[ ] no canonical Reference with hash different from Prepared authority can be produced
[ ] stable session Save completes before post-commit UI
[ ] UI failure after stable Save preserves Reference files/session
```

## Replacement

```text
[ ] different filename success
[ ] same filename success
[ ] source drift rejects before old backup
[ ] template drift rejects before old backup
[ ] every crash phase recovers
[ ] external transaction temp path rejected
[ ] reparse change before each mutator rejected
```

## Main

```text
[ ] Reference-assisted Main success
[ ] NoReference Main success
[ ] Main source drift before processing rejects
[ ] template drift before canonical promotion rejects
[ ] root + ingame hashes identical
[ ] session deletion failure rollback correct
[ ] success-message/UI failure after session deletion DOES NOT rollback asset
```

## Cancellation

```text
[ ] Prepared cancel recovery
[ ] FilesRenamed recovery
[ ] provenance tamper fail-close
[ ] Reference tamper fail-close
[ ] unknown temp preserved
```

---

# 32. Definition of Done

## Initial Reference authority

```text
[ ] Prepared ReferenceHash is immutable transaction authority
[ ] Prepared ReferenceProvenanceHash is immutable transaction authority
[ ] source path comes from / exactly matches Prepared session
[ ] timestamp comes from / exactly matches Prepared session
[ ] current source hash verified before any directory/file mutation
[ ] rendered provenance hash verified before any directory/file mutation
[ ] copied canonical Reference hash checked directly against Prepared hash
[ ] crash cannot create a normal-flow H2/P2 canonical file under H1/P1 journal
```

## Durable/UI boundary

```text
[ ] Main rollback catches end before post-commit UI
[ ] Main UI failure after successful session delete preserves completed asset
[ ] Reference rollback catches end before post-stable-save UI
[ ] Reference UI failure after stable Save preserves Reference asset/session
[ ] NoReference cannot remain usable as Idle with an untracked active durable journal
```

## Tests

```text
[ ] initial Reference source-drift test
[ ] initial Reference template-drift test
[ ] initial Reference timestamp mismatch test if duplicate arg remains
[ ] foreign prepared Reference recovery preservation tests
[ ] Main post-commit UI failure preservation test
[ ] Reference post-stable-save UI failure preservation test
[ ] NoReference post-journal UI failure safety test
[ ] R5 Save-New test renamed or truly proves one rollback
```

## Execution

```text
[ ] Debug build PASS
[ ] Debug tests PASS
[ ] Release build PASS
[ ] Release tests PASS
[ ] RecoveryCritical PASS
[ ] 20/20 full Release PASS
[ ] self-contained publish PASS
[ ] smoke PASS
[ ] coverage PASS
```

---

# 33. Final sixth-pass conclusion

The R5 hardening round is successful.

**All seven R5 findings are materially repaired.**

The repository is no longer blocked by replacement temp-path confinement, raw public mutators, replacement mutation reparse checks, Save-New double rollback, false overlap tests, the missing both-promoted state, or arbitrary extension acceptance.

The fresh independent pass nevertheless found two remaining defects:

```text
R6-001 HIGH:
initial Reference creation does not freeze its canonical writes
to the already-durable Prepared hash/provenance authority.

R6-002 MEDIUM:
transaction catches extend beyond the durable commit point,
so UI-only exceptions can trigger rollback of successful work.
```

These are focused fixes.

Do not redesign replacement recovery again.

After R6-001/R6-002 are repaired and the targeted tests are added, another final paranoid audit is warranted.

**Current acceptance state: FAIL — two new known transaction-boundary defects remain.**

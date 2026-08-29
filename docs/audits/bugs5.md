# AI Asset Provenance Helper — Fifth Paranoid Retest & Repair Guide

**File:** `bugs5.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `e6f6381af006616d0663be5337e712cffa53cb7d`  
**Previous audited commit:** `e3ef7aa9345b7daa96de51be5f2d9a1b2cf3c3f0`  
**Previous audit:** `bugs4.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — the main R4 product-path defects were repaired, but a small set of remaining safety/API/test defects still prevents a defensible zero-known-defect acceptance.**

This is again materially better than the preceding revision.

The new commit implements the important R4 fixes in the actual source:

- replacement NEW source bytes are checked against `NewSession.ReferenceHash` before temp creation continues;
- replacement NEW provenance is checked against `NewSession.ReferenceProvenanceHash`;
- OLD Reference output is exact-validated before replacement transaction creation;
- live rollback finalization now has an explicit helper and no longer contains the previous empty/swallowed finalization catch;
- successful cleanup treats replacement-journal deletion failure as critical and closes;
- replacement recovery now requires stable Reference authority and refuses active Main/cancel/prepared-Reference state;
- commit-forward recovery reuses the already-loaded durable session instead of reloading it;
- Main ingame-directory enumeration failures are converted to validation failures;
- rollback post-validation now uses exact Reference validation;
- many missing phase-state tests were added;
- the test object-aliasing bug from the prior round was corrected.

Those repairs should be preserved.

However, two R4 items were not actually implemented, and the deeper review found a concrete destructive-path hole at the raw replacement service boundary.

The remaining work is now small and focused.

---

# 0.2 Current repository state

Current `main`:

```text
e6f6381af006616d0663be5337e712cffa53cb7d
```

Commit:

```text
Fix all defects from bugs4.md (R4-001 through R4-010)
```

Compared with `e3ef7aa...`, the commit changes:

```text
bugs4.md
MainForm.Recovery.cs
MainForm.ReferenceWorkflow.cs
AssetProcessorService.Reference.cs
SessionService.cs
ValidationService.Session.cs
Bugs3ParanoidTests.cs
```

No `AssetProcessorService.Main.cs` or `ValidationService.cs` implementation change occurred in this repair, which is material to R4-006 and R4-010 below.

---

# 0.3 Execution evidence

The connected GitHub combined-status surface currently exposes:

```text
statuses: []
```

for:

```text
e6f6381af006616d0663be5337e712cffa53cb7d
```

The available commit workflow-run wrapper likewise exposes no run for this SHA.

The current analysis environment also has no `dotnet` executable.

Therefore:

- the static/source audit below is complete to the extent available from repository source;
- the Windows/.NET execution gate remains deferred;
- that missing exact environment is **not** itself a blocker;
- the remaining source-level findings below are enough to keep the current verdict at FAIL.

---

# 1. Retest of every `bugs4.md` item

| R4 item | Current status | Fifth-pass conclusion |
|---|---|---|
| R4-001 Prepared NEW authority drift | **FIXED materially** | source and rendered provenance are compared to persisted transaction hashes before accepted temp output |
| R4-002 exact OLD Reference preflight | **FIXED** | `CreateReferenceReplacementTransaction()` now exact-validates old Reference output |
| R4-003 swallowed live finalization errors | **FIXED materially / one cleanup opportunity remains** | explicit finalization helper added; cleanup journal-delete failure closes; one redundant Save-New failure path should still be simplified |
| R4-004 overlapping transaction authority | **FIXED in source, NOT PROVEN by the new tests** | stable-authority gate exists, but the tests create structurally-invalid replacement journals and therefore fail before exercising this gate |
| R4-005 ingame enumeration exception | **FIXED** | enumeration is exception-bounded and has injectable failure tests |
| R4-006 mutating service durability boundary | **NOT FIXED** | raw mutating methods remain public/caller-trusted; no durable authority verification was added |
| R4-007 RecoveryCritical completeness | **PARTIAL** | much better matrix; one critical state remains missing and the overlap tests are false assurance |
| R4-008 weak rollback post-check | **FIXED** | exact Reference post-validation now used |
| R4-009 recovery second-load race | **FIXED** | already-loaded durable session is passed into commit-forward helper |
| R4-010 arbitrary extension signature policy | **NOT FIXED** | no implementation change; unknown accepted extensions still bypass magic-byte validation |

---

# 2. Current defect summary

| ID | Severity | Area | Summary |
|---|---|---|---|
| R5-001 | **HIGH-MEDIUM** | destructive path confinement | `ValidateReferenceReplacementTransaction()` does not validate transaction temp paths, while rollback may delete them |
| R5-002 | **MEDIUM** | raw mutation API / durability | Main/Reference/replacement mutators remain public and cannot prove the required journal was persisted |
| R5-003 | **MEDIUM** | replacement forward mutation | raw replacement forward methods do not re-run transaction/path/reparse validation before mutating |
| R5-004 | **MEDIUM** | live replacement failure flow | Save-NewSession failure deletes the replacement journal before unified OLD finalization and then intentionally rolls back a second time |
| R5-005 | **MEDIUM** | tests / false assurance | R4-004 overlapping-journal tests are structurally invalid and do not exercise the stable-authority logic |
| R5-006 | **LOW-MEDIUM** | crash matrix | `NewPromotionPending` with both NEW files already promoted is still not a dedicated phase-state test |
| R5-007 | **LOW / policy** | image validation | arbitrary configured extensions still pass magic-byte validation automatically |

**Blocking recommendation:** fix R5-001 through R5-005 before zero-known-defect acceptance.  
Add R5-006 in the same repair.  
Resolve R5-007 explicitly rather than continuing to claim it was fixed.

---

# 3. R5-001 — HIGH-MEDIUM — replacement temp paths are not confined by the transaction validator

## 3.1 Current validator

`ValidateReferenceReplacementTransaction()` validates:

```text
TransactionId
OldSession destructive paths
NewSession destructive paths
same root
same asset folder
same project
same provenance path
BackupReferencePath deterministic
BackupProvenancePath deterministic
```

It does **not** validate:

```text
TempNewReferencePath
TempNewProvenancePath
```

against the deterministic transaction paths.

## 3.2 Why this is a destructive safety defect

`RollbackReferenceReplacement()` later does:

```text
if TempNewReferencePath exists
    compute hash
    if hash == NewSession.ReferenceHash
        delete TempNewReferencePath

if TempNewProvenancePath exists
    exact-validate against NewSession provenance
    if exact
        delete TempNewProvenancePath
```

This means a caller can construct an otherwise-valid transaction but set:

```text
TempNewReferencePath = C:\some\outside\file.png
```

where that outside file happens to have the same SHA-256 bytes as the NEW Reference.

The transaction validator passes because it never checks the temp path.

Rollback then classifies that outside file as transaction-owned by content and deletes it.

The same structural problem exists for `TempNewProvenancePath`.

The current MainForm path is safer because:

- live transactions are constructed deterministically by the processor;
- recovery first validates the replacement journal, whose journal validator *does* validate deterministic temp paths.

But the public processor rollback service itself is not fail-closed.

## 3.3 Required fix

Extend `ValidateReferenceReplacementTransaction()` with the same deterministic temp-path rules already present in `ValidateReferenceReplacementJournal()`.

Copy-ready logic:

```csharp
var referenceFolder =
    NormalizePath(
        Path.Combine(
            transaction.OldSession.AssetFolder,
            AppConstants.ReferenceFolderName));

var newExtension =
    Path.GetExtension(
        transaction.NewSession.ReferenceFilename);

var expectedTempReference =
    NormalizePath(
        Path.Combine(
            referenceFolder,
            $".__new_reference_{transaction.TransactionId}{newExtension}"));

var expectedTempProvenance =
    NormalizePath(
        Path.Combine(
            referenceFolder,
            $".__new_provenance_{transaction.TransactionId}.tmp"));

if (!PathsEqual(
        transaction.TempNewReferencePath,
        expectedTempReference))
{
    errors.Add(
        "Transaction TempNewReferencePath does not match "
        + "the deterministic transaction temp path.");
}

if (!PathsEqual(
        transaction.TempNewProvenancePath,
        expectedTempProvenance))
{
    errors.Add(
        "Transaction TempNewProvenancePath does not match "
        + "the deterministic transaction temp path.");
}
```

Also explicitly require the exact parent:

```csharp
RequireExactParent(
    transaction.TempNewReferencePath,
    referenceFolder,
    "Replacement temporary Reference",
    errors);

RequireExactParent(
    transaction.TempNewProvenancePath,
    referenceFolder,
    "Replacement temporary provenance",
    errors);
```

Because the transaction object is an in-memory boundary rather than persisted untrusted JSON, a thrown path exception is not currently destructive. Still, for consistency, wrap malformed paths and return failure rather than throwing if practical.

## 3.4 Mandatory regression test

```csharp
[Fact]
[Trait("Category", "RecoveryCritical")]
public void
    R5_001_RollbackReplacement_ExternalTempPath_IsRejectedAndPreserved()
{
    using var workspace =
        new TestWorkspace();

    var processor =
        workspace.CreateAssetProcessor();

    var settings =
        workspace.CreateSettings();

    var oldSource =
        workspace.CreateImage(
            "old.png",
            new byte[] { 1, 2, 3 });

    var oldSession =
        processor.ProcessReference(
            settings,
            "r5_external_temp",
            oldSource,
            DateTimeOffset.Now);

    var newSource =
        workspace.CreateImage(
            "new.png",
            new byte[] { 4, 5, 6 });

    var tx =
        processor.CreateReferenceReplacementTransaction(
            oldSession,
            settings.AcceptedExtensions,
            newSource,
            DateTimeOffset.Now);

    var outsideFolder =
        Path.Combine(
            workspace.Root,
            "OUTSIDE");

    Directory.CreateDirectory(
        outsideFolder);

    var outsideFile =
        Path.Combine(
            outsideFolder,
            "do-not-delete.png");

    File.Copy(
        newSource,
        outsideFile);

    tx.TempNewReferencePath =
        outsideFile;

    var result =
        processor.RollbackReferenceReplacement(
            tx);

    Assert.False(
        result.IsValid);

    Assert.True(
        File.Exists(outsideFile),
        "External matching file must never be deleted.");
}
```

Add the equivalent provenance test.

---

# 4. R5-002 — MEDIUM — raw mutating APIs still do not enforce durable journal authority

This is the unresolved R4-006 item.

## 4.1 Main

The service remains publicly callable as:

```csharp
public string ProcessMainImage(...)
```

It checks:

```text
session.IsMainCommitting == true
```

and reports:

```text
ProcessMainImage requires a prepared and durably persisted Main transaction.
```

But the processor has no access to `SessionService` and therefore cannot know whether the session was actually persisted.

A caller can still do:

```text
PrepareMainCommit()
ProcessMainImage()
```

without:

```text
SessionService.Save()
```

and mutate canonical files.

## 4.2 Initial Reference

Likewise:

```csharp
public AssetSession ProcessReference(
    AssetSession session,
    ...)
```

only proves that the in-memory session says:

```text
ReferenceCommitPhase.Prepared
```

It cannot prove `session.json` contains that authority.

## 4.3 Replacement primitives

These forward mutators are public as well:

```text
CreateReplacementTempFiles
BackupOldReference
PromoteNewReference
CleanupReplacementBackups
RollbackReferenceReplacement
```

The normal MainForm orchestration does the correct write-ahead sequence, but the raw service contract remains crash-unsafe.

## 4.4 Minimal acceptable repair for this application

Because this is a single application rather than a reusable public SDK, the smallest good repair is:

1. make raw mutating processor methods `internal`;
2. keep the safe orchestration in `MainForm`/one coordinator;
3. add reflection tests proving the mutators are not public;
4. add one source-level comment stating that the journal Save immediately preceding each mutator is part of the call contract.

For stronger enforcement, introduce a coordinator that owns:

```text
SessionService
AssetProcessorService
ValidationService
```

and performs:

```text
prepare
persist
verify persisted authority
mutate
advance phase
```

That is architecturally strongest, but a larger change.

For this project, **internalizing raw mutators plus complete production integration tests is an acceptable small-scope fix**.

## 4.5 Reflection tests

At minimum assert these are not public production API:

```text
ProcessMainImage
ProcessReference(AssetSession,...)
CreateReplacementTempFiles
BackupOldReference
PromoteNewReference
RollbackReferenceReplacement
CleanupReplacementBackups
```

The test assembly already accesses internal helpers, so this is practical.

---

# 5. R5-003 — MEDIUM — forward replacement mutation methods do not revalidate transaction/path/reparse safety

Even after R5-001 is fixed, the forward methods should not trust that a transaction constructed earlier is still path-safe.

Current sequence:

```text
CreateReferenceReplacementTransaction()
Save Prepared journal
CreateReplacementTempFiles()
Save OldBackupPending
BackupOldReference()
...
PromoteNewReference()
```

`CreateReferenceReplacementTransaction()` validates the OLD session at construction time.

But the following mutating methods do not begin with:

```csharp
ValidateReferenceReplacementTransaction(transaction)
```

and therefore do not re-check managed folder/reparse safety at the mutation boundary.

This matters because the safety model explicitly treats reparse points as unsafe.

A reference directory that was safe during transaction construction could be changed before one of the later mutation steps.

## 5.1 Required helper

```csharp
private void
    RequireSafeReferenceReplacementTransaction(
        ReferenceReplacementTransaction transaction)
{
    var validation =
        _validationService
            .ValidateReferenceReplacementTransaction(
                transaction);

    if (!validation.IsValid)
    {
        throw new InvalidDataException(
            string.Join(
                Environment.NewLine,
                validation.Errors));
    }
}
```

Call it as the first operation in:

```text
CreateReplacementTempFiles
BackupOldReference
PromoteNewReference
```

`RollbackReferenceReplacement` and `CommitReferenceReplacement` already validate the transaction.

## 5.2 Post-create reparse check

After creating/writing any managed directory that could have been absent, re-check reparse status before copying/promoting.

For replacement, the Reference folder normally already exists, but the validation should still be at the mutation boundary.

## 5.3 Required test hook

Reuse the existing `ValidationService.FileAttributesProvider` to simulate a Reference folder becoming a reparse point between transaction construction and mutation.

Test:

```text
create transaction while folder is normal
persist Prepared
make FileAttributesProvider report Reference folder as ReparsePoint
call CreateReplacementTempFiles
expect failure
assert no temp files
assert OLD canonical files untouched
```

---

# 6. R5-004 — MEDIUM — Save-NewSession failure path deletes journal before unified OLD finalization

The major R4-003 bug is fixed, but one branch still follows an unnecessarily dangerous sequence.

## 6.1 Current special catch

When:

```csharp
_sessionService.Save(
    transaction.NewSession)
```

fails, the code:

```text
1. RollbackReferenceReplacement()
2. DeleteReplacementJournal()
3. throw IOException
4. outer catch
5. RollbackReferenceReplacement() AGAIN
6. FinalizeLiveReplacementRollback()
   - Save OldSession
   - DeleteReplacementJournal again
```

This has two problems:

- it deletes the recovery journal before the common durable OLD finalization path;
- it deliberately performs rollback twice.

The current atomic `SessionService.Save()` makes this likely safe in ordinary failure cases, but it is not the clean fail-safe state machine used elsewhere.

## 6.2 Required simplification

Replace the inner Save-NewSession catch with:

```csharp
catch (Exception saveException)
{
    var rollback =
        _assetProcessorService
            .RollbackReferenceReplacement(
                transaction);

    if (!rollback.IsValid)
    {
        ShowMessageBox(
            "CRITICAL: Replacement session could not be saved "
            + "and the old Reference could not be fully restored."
            + Environment.NewLine
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                rollback.Errors),
            "Critical replacement error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        Close();
        return;
    }

    if (!FinalizeLiveReplacementRollback(
            transaction))
    {
        return;
    }

    ShowError(
        "Could not save replacement session. "
        + "The previous Reference was restored.",
        saveException);

    return;
}
```

Do **not**:

```text
delete journal
throw
rollback again
```

This gives every rollback path exactly one finalization routine.

## 6.3 Required test

Inject failure into `SessionService.Save(NewSession)` itself if possible.

Then assert:

```text
rollback called once
OLD canonical Reference exact
OLD canonical provenance exact
session.json == OLD
replacement journal removed only after OLD save
form remains usable only after successful finalization
```

If direct call-count instrumentation is undesirable, infer single rollback from a hook that would fail on a second rollback.

---

# 7. R5-005 — MEDIUM — R4-004 tests do not test R4-004

The source fix is present:

```csharp
IsStableReferenceAuthority(...)
```

and `MatchesReferenceAuthority()` now rejects:

```text
active Main
active cancel
prepared Reference creation
```

That is good.

But the new tests intended to prove it construct the replacement journal approximately as:

```csharp
var rawTx =
    new ReferenceReplacementTransaction
    {
        TransactionId = Guid.NewGuid().ToString("N"),
        OldSession = session,
        NewSession = session,
        BackupReferencePath =
            session.ReferenceDestinationPath + ".bak",
        BackupProvenancePath =
            session.ReferenceProvenancePath + ".bak",
        TempNewReferencePath =
            session.ReferenceDestinationPath + ".tmp",
        TempNewProvenancePath =
            session.ReferenceProvenancePath + ".tmp"
    };
```

These paths are **not** valid replacement transaction paths.

The journal validator expects:

```text
<old-reference>.<TransactionId>.old

<old-provenance>.<TransactionId>.old

<reference-folder>/.__new_reference_<TransactionId><new-extension>

<reference-folder>/.__new_provenance_<TransactionId>.tmp
```

Therefore startup recovery rejects the journal during structural validation.

The test would still pass if `IsStableReferenceAuthority()` were completely removed.

That is false assurance.

## 7.1 Correct test construction

First create a legitimate stable replacement transaction:

```csharp
var tx =
    processor.CreateReferenceReplacementTransaction(
        stableOldSession,
        settings.AcceptedExtensions,
        ref2,
        DateTimeOffset.Now);

sessionService.SaveReplacementJournal(
    tx.ToJournal(
        ReferenceReplacementPhase.Prepared));
```

Then create a **deep clone** of `tx.OldSession` for `session.json` and modify only that durable current session:

### Active Main

```csharp
var current =
    CloneSession(
        tx.OldSession);

current.IsMainCommitting = true;
current.MainFilename = "main.png";
current.MainPrompt = "prompt";
current.MainProcessedAt = DateTimeOffset.Now;
current.MainHash = new string('a', 64);
current.MainProvenanceHash = new string('b', 64);
current.MainTransactionId = Guid.NewGuid().ToString("N");

sessionService.Save(current);
```

The replacement journal stays fully valid and contains stable OLD/NEW snapshots.

Startup must now reach the stable-authority check and fail closed there.

### Cancel

Clone OLD and set:

```text
CancelPhase = Prepared
CancellationId = valid id
```

### Prepared Reference

Clone OLD and set:

```text
ReferenceCommitPhase = Prepared
ReferenceTransactionId = valid id
```

Do not modify the journal snapshots.

## 7.2 Assertions

For all three:

```text
replacement journal still exists
session.json still exists
session.json active transaction fields unchanged
Reference image unchanged
Reference provenance unchanged
no backup/temp mutation
```

Optionally capture the critical message and assert it contains:

```text
Durable session does not match OLD authority
```

rather than a deterministic-path error.

---

# 8. R5-006 — LOW-MEDIUM — one important crash state remains missing

The phase matrix is now much stronger.

The new suite explicitly covers:

```text
Prepared / temp Reference only
OldBackupPending / no move
OldBackupPending / Reference moved only
OldBackupPending / both old files moved
NewPromotionPending / no promotion
SessionSwitchPending / OLD / different filename
SessionSwitchPending / OLD / same filename
SessionSwitched / NEW
CleanupPending / one backup missing
```

Prior tests also cover:

```text
NewPromotionPending / Reference promoted only
NewPromoted / OLD
CleanupPending / both backups
```

Still add the exact state:

```text
journal = NewPromotionPending
OLD backups = both present
NEW canonical Reference = promoted
NEW canonical provenance = promoted
session.json = OLD
```

This is distinct from `NewPromoted` because the durable phase says promotion may have been only partially attempted.

Recovery currently maps `NewPromotionPending` directly to rollback, so it should work.

The missing test is small but worth having because this exact state was one of the historical failure modes.

---

# 9. R5-007 — LOW / explicit product policy — arbitrary extension signature bypass remains unchanged

This is unresolved R4-010.

Current behavior remains:

```text
AcceptedExtensions allows arbitrary syntactically valid extension
ValidateImageFile accepts that extension
HasValidMagicBytes() returns true for unknown extension
```

The existing test still deliberately accepts:

```text
.customimg
```

Therefore arbitrary nonempty bytes can be treated as a valid image if the extension is configured.

## 9.1 Recommended v1.1 policy

For this utility, use the simple policy:

```text
Supported image extensions:
.png
.webp
.jpg
.jpeg
```

Do not treat arbitrary extensions as supported image formats.

## 9.2 Validation

In `ValidateProcessingSettings()`:

```csharp
foreach (var ext in settings.AcceptedExtensions)
{
    if (!AppConstants.DefaultImageExtensions.Contains(
            ext,
            StringComparer.OrdinalIgnoreCase))
    {
        errors.Add(
            $"Unsupported image extension configured: {ext}");
    }
}
```

In `HasValidMagicBytes()`:

```csharp
return false;
```

for an unknown extension rather than:

```csharp
return true;
```

Then update/remove the test that expects `.customimg` to be accepted.

If arbitrary formats are intentionally desired later, add real validators per format.

---

# 10. Additional source observations

## 10.1 R4-001 implementation is good

`CreateReplacementTempFiles()` now:

```text
hashes current source
compares against transaction ReferenceHash
copies
validates image
hashes temp
compares temp against transaction ReferenceHash
renders provenance
hashes rendered provenance
compares against transaction ReferenceProvenanceHash
writes temp provenance
```

This correctly freezes NEW authority.

## 10.2 R4-002 implementation is good

`CreateReferenceReplacementTransaction()` now runs:

```text
ValidateSession(old)
ValidateExactReferenceOutput(old)
```

before constructing NEW authority.

This prevents tampered OLD provenance from entering the replacement state machine.

## 10.3 R4-003 main fix is good

`FinalizeLiveReplacementRollback()` now makes:

```text
Save OLD
then delete journal
then resume OLD in memory
```

the normal live rollback finalization order.

Journal deletion failure closes.

Successful replacement cleanup journal deletion failure also closes.

Keep this.

## 10.4 R4-004 source fix is good

Recovery now rejects current sessions that are not a stable Reference session.

Only the tests need correction.

## 10.5 R4-005 implementation is good

`ValidateMainDestinationAvailability()` catches:

```text
UnauthorizedAccessException
IOException
ArgumentException
SecurityException
```

and exposes an injectable enumeration hook.

Keep this.

## 10.6 R4-008 and R4-009 are good

Rollback ends with exact Reference validation.

Commit-forward uses the already-loaded current session instead of a second unsheltered load.

Keep both.

---

# 11. Repair order

Use this order.

## Phase 1 — destructive path confinement

Fix:

```text
R5-001
R5-003
```

Do this first.

Add deterministic temp-path validation to the transaction validator and call that validator at every forward mutation boundary.

## Phase 2 — mutation API boundary

Fix:

```text
R5-002
```

At minimum internalize raw mutators and test visibility.

If desired, introduce a coordinator for hard persistence proof.

## Phase 3 — simplify live failure path

Fix:

```text
R5-004
```

Every rollback should use one common finalization path exactly once.

## Phase 4 — repair false tests

Fix:

```text
R5-005
R5-006
```

Do not claim RecoveryCritical proof until valid-journal overlap tests exist.

## Phase 5 — image format policy

Resolve:

```text
R5-007
```

Recommended: only PNG/WebP/JPEG for v1.1.

---

# 12. Mandatory new/updated tests

All safety tests:

```csharp
[Trait("Category", "RecoveryCritical")]
```

## Path confinement

```text
RollbackReplacement_ExternalTempReferencePath_RejectsAndPreserves
RollbackReplacement_ExternalTempProvenancePath_RejectsAndPreserves
CreateReplacementTemps_NonDeterministicTempPath_Rejects
```

## Forward safety/reparse

```text
CreateReplacementTemps_ReferenceFolderBecomesReparse_Rejects
BackupOldReference_ReferenceFolderBecomesReparse_Rejects
PromoteNewReference_ReferenceFolderBecomesReparse_Rejects
```

## Durability/API

```text
ProcessMainImage_IsNotPublic
ProcessReferencePreparedMutator_IsNotPublic
ReplacementForwardMutators_AreNotPublic
RollbackReplacement_IsNotPublic
```

Or, if a coordinator is implemented:

```text
MainMutation_WithoutPersistedAuthority_Rejects
ReferenceMutation_WithoutPersistedAuthority_Rejects
ReplacementMutation_WithoutExpectedJournalPhase_Rejects
```

## Save-New failure

```text
SaveNewSessionFailure_RollsBackExactlyOnce
SaveNewSessionFailure_SavesOldBeforeJournalDelete
SaveNewSessionFailure_FinalizationFailure_ClosesAndPreservesAuthority
```

## Valid overlapping transaction tests

```text
ValidReplacementJournalPlusActiveMain_FailsStableAuthorityCheck
ValidReplacementJournalPlusCancelPrepared_FailsStableAuthorityCheck
ValidReplacementJournalPlusPreparedReference_FailsStableAuthorityCheck
```

## Crash matrix

```text
NewPromotionPending_BothNewFilesPromoted_OldSession_RollsBack
```

## Image formats

```text
Settings_CustomUnknownExtension_Rejected
Image_UnknownConfiguredExtension_Rejected
```

---

# 13. Static searches after repair

```powershell
# Raw mutation visibility
rg -n "public .*ProcessMainImage|public .*ProcessReference|public .*CreateReplacementTempFiles|public .*BackupOldReference|public .*PromoteNewReference|public .*RollbackReferenceReplacement" `
  src/AssetProvenanceHelper/Services
```

Expected if using the internalization solution:

```text
0 public raw mutation entry points
```

---

```powershell
# Every forward replacement mutator should validate transaction safety
rg -n "CreateReplacementTempFiles|BackupOldReference|PromoteNewReference" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Manual inspection:

```text
each method begins with transaction validation
```

---

```powershell
# Unknown image formats must not pass automatically
rg -n "return true;" `
  src/AssetProvenanceHelper/Services/ValidationService.cs
```

Inspect `HasValidMagicBytes`.

Unknown extension fallback should not be `true`.

---

```powershell
# No manual bad replacement-journal fixtures
rg -n "\.bak|ReferenceDestinationPath \+ \"\.tmp\"|ReferenceProvenancePath \+ \"\.tmp\"" `
  tests/AssetProvenanceHelper.Tests
```

No overlap-authority test may rely on structurally invalid fake transaction paths.

---

# 14. Required Windows execution gate

The environment limitation does not block static repair, but final release verification should run:

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

Recovery suite:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical" `
  --logger "console;verbosity=detailed"
```

20x:

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
        throw "Flakiness run $i failed."
    }
}
```

Publish:

```powershell
dotnet publish `
  src/AssetProvenanceHelper/AssetProvenanceHelper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish
```

Smoke:

```powershell
pwsh scripts/run_smoke_tests.ps1 `
  -PublishDir artifacts/publish `
  -LogOutputDir artifacts
```

Coverage:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts/coverage
```

Acceptance:

```text
Debug PASS
Release PASS
RecoveryCritical PASS
20/20 PASS
publish PASS
smoke PASS
coverage PASS
```

---

# 15. Final Definition of Done

## Replacement safety

```text
[ ] transaction validator checks deterministic backup paths
[ ] transaction validator checks deterministic temp paths
[ ] temp paths have exact Reference-folder parent
[ ] forward replacement mutators revalidate transaction safety
[ ] rollback cannot delete an external matching temp file
[ ] Reference reparse-point changes fail closed
```

## Durability

```text
[ ] raw mutators are not public caller-trusted APIs
OR
[ ] processor/coordinator proves persisted transaction authority before mutation

[ ] Save-New failure uses one rollback finalization path
[ ] no journal is deleted before the chosen stable authority is finalized
```

## Tests

```text
[ ] valid replacement journal used in overlap tests
[ ] active Main overlap test reaches stable-authority branch
[ ] cancel overlap test reaches stable-authority branch
[ ] prepared-Reference overlap test reaches stable-authority branch
[ ] NewPromotionPending + both promoted test exists
[ ] unknown extension policy tested
```

## Execution

```text
[ ] Debug build/test PASS
[ ] Release build/test PASS
[ ] RecoveryCritical PASS
[ ] 20/20 PASS
[ ] self-contained publish PASS
[ ] smoke PASS
[ ] coverage PASS
```

---

# 16. Final fifth-pass conclusion

The implementation is now **very close**.

The prior high-impact recovery-state defects are substantially repaired. No additional state-machine rewrite is needed.

The remaining blocking work is concentrated in three concepts:

```text
1. confine and revalidate raw replacement mutation paths;
2. close the remaining caller-trusted mutation API/durability boundary;
3. replace structurally-invalid overlap tests with real valid-journal tests.
```

The custom-extension policy is still unresolved but is straightforward to settle.

After these changes, another full paranoid retest is warranted.

**Current acceptance: FAIL — remaining known defects exist.**

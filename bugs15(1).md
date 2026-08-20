# AI Asset Provenance Helper — Fifteenth Paranoid Retest & Repair Guide

**File:** `bugs15.md`  
**Audit date:** 2026-08-20  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `5e9a8ebf7119ad683b717d0df98f2a7db346321d`  
**Previous audited commit:** `e4e57cfb9bbaa857999746ea21acbb5130b67062`  
**Previous audit:** `bugs14.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — the R14 implementation is substantial and most requested architecture is now correct, but one HIGH crash-recovery defect, one RecoveryCritical test-suite defect, and one legacy Main compatibility defect remain.**

The new commit genuinely fixes the main R14 architecture:

- a reusable hash-owned/path-safe forward move primitive now exists;
- Main provenance, Main root image, and ingame promotion use it;
- initial Reference image + provenance promotion use it;
- replacement OLD image + provenance backup use it;
- replacement NEW image + provenance promotion use it;
- legacy Reference provenance semantic proof and raw SHA now come from one byte snapshot;
- Cancel uses the same one-snapshot legacy provenance authority;
- legacy replacement-journal recovery now attempts to hydrate missing OLD provenance SHA authority;
- explicit legacy journal tests were added;
- direct restore-hook and Cancel byte-race tests were added.

Do **not** undo those changes.

Fresh findings:

| ID | Severity | Area | Summary |
|---|---:|---|---|
| **R15-001** | **HIGH** | legacy replacement-journal recovery durability | `TransactionFromJournal()` aliases `journal.OldSession`; `EnsureOldProvenanceByteAuthority()` mutates that exact object, so the later “hash changed?” comparison is always false and the upgraded hash is never durably written before cleanup/rollback |
| **R15-002** | **MEDIUM-HIGH** | RecoveryCritical tests | two newly added Cancel byte-race tests assert exception type/message behavior that current `SessionService.Cancel()` cannot produce; the mandatory suite should fail if actually executed |
| **R15-003** | **MEDIUM** | legacy Main rollback | `RollbackMain()` accepts legacy exact provenance with `MainProvenanceHash == null`, but later derives a no-BOM rendered hash instead of using the raw hash of the already verified legacy snapshot; exact legacy BOM provenance therefore cannot be automatically rolled back |

The primary source-level blocker is **R15-001**.  
R15-002 separately prevents a clean mandatory RecoveryCritical run.

---

# 0.2 Current repository state

Current `main`:

```text
5e9a8ebf7119ad683b717d0df98f2a7db346321d
```

Commit:

```text
Fix all issues in bugs14.md (R14-001 through R14-004) and add 20 paranoid tests
```

Parent:

```text
e4e57cfb9bbaa857999746ea21acbb5130b67062
```

Connected GitHub currently exposes:

```text
statuses: []
workflow_runs: []
```

Missing exact Windows/.NET execution evidence is deferred verification and is **not** the reason for this FAIL.

---

# 1. `bugs14.md` retest matrix

| R14 requirement | Result |
|---|---|
| reusable hash-owned forward move primitive | **FIXED** |
| forward move hook before final path/hash authority | **FIXED** |
| destination collision re-check after hook | **FIXED** |
| Main provenance forward move uses helper | **FIXED** |
| Main root image forward move uses helper | **FIXED** |
| Main ingame forward move uses helper | **FIXED** |
| initial Reference image forward move uses helper | **FIXED** |
| initial Reference provenance forward move uses helper | **FIXED** |
| replacement OLD image backup uses helper | **FIXED** |
| replacement OLD provenance backup uses helper | **FIXED** |
| replacement NEW image promotion uses helper | **FIXED** |
| replacement NEW provenance promotion uses helper | **FIXED** |
| legacy provenance semantic proof + raw hash from same byte snapshot | **FIXED** |
| BOM raw-hash materialization test | **ADDED** |
| legacy Cancel uses snapshot-bound hash | **FIXED** |
| legacy OLD journal hash hydration logic exists | **FIXED structurally** |
| hydrated OLD authority persisted before mutation | **BROKEN by aliasing — R15-001** |
| OldBackedUp legacy journal test | **ADDED** |
| NewPromotionPending legacy journal test | **ADDED** |
| NewPromoted legacy journal test | **ADDED** |
| CleanupPending legacy journal test | **ADDED** |
| corrupt legacy backup fail-closed test | **ADDED** |
| direct restore-hook reparse test | **ADDED** |
| Cancel byte-race tests | **ADDED, two assertions are inconsistent — R15-002** |

---

# 2. R14-001 retest — PASS

The new shared forward helper now enforces:

```text
source exists
destination absent
race/test hook
current path safety
source still exists
destination still absent
fresh SHA-256
expected SHA comparison
File.Move(overwrite:false)
```

This is the correct mutation-boundary design.

Main now promotes:

```text
temp provenance -> final provenance
temp Main       -> root Main
temp ingame     -> canonical ingame
```

through that helper. The third move therefore receives fresh authority after `OnMainPromotedHook`.

Initial Reference, replacement OLD backup, and replacement NEW promotion have also been converted to the same primitive.

No direct managed `File.Move(...)` remains in the current Main or Reference processor paths reviewed in this audit.

---

# 3. R14-002 retest — PASS

The new Reference provenance validator now supports a one-snapshot legacy path:

```text
read raw bytes once
decode that same byte[]
compare decoded text exactly against rendered expected text
SHA-256 that same byte[]
return verified raw hash
```

This correctly binds semantic provenance ownership to raw byte authority.

The modern path still validates the current raw hash against the durable stored hash.

A corresponding final/Main provenance helper also exists.

Cancel now uses the verified one-snapshot Reference raw hash rather than separately validating text and later hashing another read.

---

# 4. R15-001 — HIGH — hydrated legacy journal authority is not persisted because recovery aliases the journal session object

This is the strongest fresh defect.

The recovery implementation intends to do the right thing:

```text
load legacy journal
hydrate missing OLD provenance raw hash
persist the upgraded journal
then perform rollback or cleanup mutation
```

However `TransactionFromJournal()` currently assigns:

```csharp
OldSession = journal.OldSession,
NewSession = journal.NewSession,
```

These are mutable object references, not copies.

Therefore:

```csharp
ReferenceEquals(
    transaction.OldSession,
    journal.OldSession)
```

is true.

---

# 5. The intended upgrade check is defeated by aliasing

`EnsureOldProvenanceByteAuthority(transaction)` does:

```csharp
transaction.OldSession.ReferenceProvenanceHash =
    verifiedHash;
```

Because `transaction.OldSession` and `journal.OldSession` are the same object, the journal is simultaneously changed **in memory**.

Recovery then checks:

```csharp
if (
    journal.OldSession.ReferenceProvenanceHash
    !=
    transaction.OldSession.ReferenceProvenanceHash)
{
    journal.OldSession.ReferenceProvenanceHash =
        transaction.OldSession.ReferenceProvenanceHash;

    _sessionService.SaveReplacementJournal(
        journal);
}
```

But after hydration both expressions read the same property from the same object.

The comparison is always:

```text
H1 != H1
```

which is false.

The upgraded hash is therefore **never written back to disk**.

---

# 6. Why R15-001 creates a real crash-recovery failure

A one-shot recovery can still finish successfully. The problem occurs at the exact crash boundary the durable journal is supposed to handle.

Deterministic scenario:

```text
session.json:
    durable NewSession

replacement journal:
    phase = CleanupPending
    OldSession.ReferenceProvenanceHash = null

disk:
    exact NEW canonical output exists
    OLD image backup exists
    OLD provenance backup exists
```

First startup:

```text
1. recovery identifies NewSession authority

2. TransactionFromJournal aliases journal.OldSession

3. EnsureOldProvenanceByteAuthority validates OLD backup provenance
   and sets ReferenceProvenanceHash = H1 in memory

4. comparison intended to persist the upgrade is false
   because journal and transaction share the same object

5. CleanupReplacementBackups deletes OLD backup image

6. CleanupReplacementBackups deletes OLD backup provenance

7. DeleteReplacementJournal begins
```

Now inject the already-supported failure:

```csharp
SessionService.OnBeforeReplacementJournalDeleteHook =
    () => throw new IOException(
        "Simulated replacement journal delete failure.");
```

State after this startup:

```text
NEW canonical output is valid
OLD backups are gone
replacement journal still exists ON DISK
persisted OldSession.ReferenceProvenanceHash is still null
```

---

# 7. Second startup can no longer establish OLD authority

Second startup loads the still-null journal.

`EnsureOldProvenanceByteAuthority()` tries:

```text
candidate 1:
    BackupProvenancePath
```

but cleanup already removed it.

It then tries:

```text
candidate 2:
    OldSession.ReferenceProvenancePath
```

The canonical provenance path is shared between OLD and NEW Reference state.

After successful replacement it contains NEW provenance, not OLD provenance.

Legacy exact OLD semantic validation fails.

The application can therefore get stuck preserving a CleanupPending journal for a replacement which is already correctly committed and cleaned.

This is precisely why the hydrated raw authority needed to be durably written **before** destructive cleanup.

---

# 8. Required R15-001 fix

At minimum, capture the durable state before hydration:

```csharp
var oldHashWasMissing =
    string.IsNullOrWhiteSpace(
        journal.OldSession.ReferenceProvenanceHash);

var transaction =
    TransactionFromJournal(
        journal);

var authority =
    _assetProcessorService
        .EnsureOldProvenanceByteAuthority(
            transaction);

if (!authority.IsValid)
{
    ...
}

if (
    oldHashWasMissing
    &&
    !string.IsNullOrWhiteSpace(
        transaction.OldSession.ReferenceProvenanceHash))
{
    journal.OldSession.ReferenceProvenanceHash =
        transaction.OldSession.ReferenceProvenanceHash;

    try
    {
        _sessionService.SaveReplacementJournal(
            journal);
    }
    catch (Exception ex)
    {
        return FailReplacementRecovery(
            "Could not persist upgraded replacement "
            + "journal before mutation.",
            ex);
    }
}
```

Do this before both:

```text
RollbackReferenceReplacement(...)
CleanupReplacementBackups(...)
```

---

# 9. Stronger R15-001 fix: stop sharing mutable session objects

Also make `TransactionFromJournal()` create independent snapshots of `OldSession` and `NewSession`.

A dedicated clone helper is preferable to hidden JSON round-tripping.

The transaction should not mutate the object used to decide whether the durable journal changed.

This also makes future recovery code easier to reason about.

---

# 10. Mandatory R15-001 two-startup test

Add:

```text
LegacyReplacementJournal_CleanupPending_
HydratedHashPersistsBeforeCleanupMutation
```

Arrange:

```text
CleanupPending
NewSession durable
persisted OLD provenance hash = null
OLD backup image exact
OLD backup provenance exact
NEW canonical output exact
```

Set:

```csharp
SessionService.OnBeforeReplacementJournalDeleteHook =
    () => throw new IOException(
        "Simulated journal-delete failure.");
```

Run startup recovery once.

Expected:

```text
replacement journal still exists
OLD backups have been cleaned
```

Then **reload the journal from disk**:

```csharp
var persisted =
    sessionService.LoadReplacementJournal();

Assert.NotNull(
    persisted!
        .OldSession
        .ReferenceProvenanceHash);
```

This should fail on the current commit.

Clear the delete hook and run startup recovery a second time.

Expected:

```text
second recovery succeeds
replacement journal is deleted
NEW canonical output remains exact
```

---

# 11. Mandatory R15-001 pre-mutation persistence failure test

Add:

```text
LegacyReplacementJournal_OldBackedUp_
HydrationSaveFailure_NoRollbackMutation
```

Force the save of the upgraded journal to fail.

Expected:

```text
no backup restored
no backup deleted
no canonical mutation
legacy journal remains
recovery fails closed
```

This proves the ordering:

```text
derive missing byte authority
persist authority
ONLY THEN mutate filesystem
```

---

# 12. R15-002 — MEDIUM-HIGH — two new RecoveryCritical Cancel tests assert behavior the code does not produce

The production mutation-boundary checks are correct.

The test expectations are not.

Both tests are tagged:

```text
Category=RecoveryCritical
```

so the mandatory suite should not currently be expected to pass.

---

# 13. R15-002A — provenance move byte race expects the wrong error message

Current test:

```text
Cancel_OnBeforeCancelFileMoveHook_
ProvenanceBytesChange_NoMove
```

changes provenance bytes from:

```csharp
OnBeforeCancelFileMoveHook
```

The earlier semantic/snapshot validation has already succeeded by then.

`MoveHashOwnedCancelFile()` subsequently computes the fresh SHA and throws:

```csharp
InvalidDataException(
    $"Cancel {description} at '{sourcePath}' "
    + "hash changed before move.");
```

For this path, the actual message is equivalent to:

```text
Cancel reference provenance at '...' hash changed before move.
```

The test currently expects:

```text
Reference provenance on disk does not match
```

That message belongs to an earlier validation path and cannot be produced by the selected injection point.

Required assertion:

```csharp
var ex =
    Assert.Throws<InvalidDataException>(
        () => sessionService.Cancel(session));

Assert.Contains(
    "hash changed before move",
    ex.Message,
    StringComparison.OrdinalIgnoreCase);
```

Keep assertions that:

```text
hook ran
canonical provenance remains
cancel temp provenance was not created
session remains recoverable
```

---

# 14. R15-002B — Reference move byte race expects the wrong exception type and wrapper

Current test:

```text
Cancel_OnBeforeCancelFileMoveHook_
ReferenceBytesChange_NoMove
```

lets provenance move successfully, then modifies the Reference bytes at the Reference move hook.

The move helper throws:

```text
InvalidDataException:
Cancel reference image at '...' hash changed before move.
```

The surrounding Cancel catch then restores the already-moved provenance.

Because no restore-failure hook is installed in this test, restoration succeeds.

Cancel resets:

```text
CancelPhase = None
CancellationId = null
```

and saves the reconciled session.

The catch then executes a bare:

```csharp
throw;
```

so it rethrows the **original `InvalidDataException`**.

The current test instead expects:

```csharp
Assert.Throws<IOException>(...)
```

and:

```text
Cancel failed during reference image rename
```

That wrapper is used when provenance restoration itself becomes untrusted or fails, not when restoration succeeds.

Required expectation:

```csharp
var ex =
    Assert.Throws<InvalidDataException>(
        () => sessionService.Cancel(session));

Assert.Contains(
    "reference image",
    ex.Message,
    StringComparison.OrdinalIgnoreCase);

Assert.Contains(
    "hash changed before move",
    ex.Message,
    StringComparison.OrdinalIgnoreCase);
```

Also assert the real reconciliation state:

```text
Reference image remains canonical
provenance is restored canonical
cancel temp Reference absent
cancel temp provenance absent
CancelPhase == None
CancellationId == null
```

Do **not** change correct production behavior to satisfy the current incorrect tests.

---

# 15. R15-003 — MEDIUM — legacy Main rollback does not use the verified raw provenance hash

The new ValidationService already contains the right primitive:

```text
TryGetExactFinalProvenanceRawHash(...)
```

`RollbackMain()` still discards that raw authority in the legacy case.

---

# 16. Current legacy Main rollback sequence

A legacy active Main session can have:

```text
MainProvenanceHash == null
```

while retaining all other valid Main transaction metadata.

Phase A calls:

```csharp
ValidateExactFinalProvenanceOwnership(
    session,
    provenancePath,
    templateService);
```

For a legacy exact provenance file with a UTF-8 BOM, semantic validation can succeed.

However later code computes:

```csharp
expectedProvHash =
    session.MainProvenanceHash
    ??
    SHA256(
        UTF8_NO_BOM(
            RenderExpectedProvenance()));
```

That synthetic hash is not necessarily the raw hash of the already verified legacy file.

---

# 17. Exact BOM example

On disk:

```text
BOM + exact rendered final provenance
```

Semantic validation:

```text
PASS
```

because BOM-aware decoding yields the exact expected text.

Actual raw hash:

```text
SHA256(BOM + UTF-8 text bytes)
```

Current fallback hash:

```text
SHA256(UTF-8 text bytes without BOM)
```

These hashes differ.

`TryDeleteHashOwnedFileWithError()` then correctly refuses deletion.

The rollback therefore fails even though Phase A proved this is an exact legacy tool-owned provenance file.

This is safe, but not fully additive/default-compatible.

---

# 18. Required R15-003 fix

For every existing Main provenance file, obtain the exact raw hash from the validator itself.

For final provenance:

```csharp
string? finalProvHash = null;

if (File.Exists(provenancePath))
{
    var result =
        _validationService
            .TryGetExactFinalProvenanceRawHash(
                session,
                provenancePath,
                _templateService,
                out finalProvHash);

    if (!result.IsValid
        || string.IsNullOrWhiteSpace(finalProvHash))
    {
        return ValidationResult.Failure(...);
    }
}
```

For temp provenance, do the same independently:

```csharp
string? tempProvHash = null;

if (File.Exists(tempProv))
{
    var result =
        _validationService
            .TryGetExactFinalProvenanceRawHash(
                session,
                tempProv,
                _templateService,
                out tempProvHash);

    if (!result.IsValid
        || string.IsNullOrWhiteSpace(tempProvHash))
    {
        return ValidationResult.Failure(...);
    }
}
```

Then pass those verified raw hashes into the delete helper.

Do not synthesize one no-BOM fallback hash for both files.

---

# 19. Mandatory R15-003 regression test

Add:

```text
RollbackMain_LegacyNullProvenanceHash_
ExactBomProvenance_RollsBack
```

Arrange a valid active Main transaction with:

```text
MainProvenanceHash = null
final provenance = UTF-8 BOM + exact rendered final provenance
```

First verify:

```csharp
Assert.True(
    validationService
        .ValidateExactFinalProvenanceOwnership(
            session,
            provenancePath,
            templateService)
        .IsValid);
```

Then:

```csharp
var rollback =
    processor.RollbackMain(
        session);
```

Expected:

```text
rollback succeeds
tool-owned legacy provenance removed
owned Main outputs reconciled
Main transaction metadata reset
```

Also retain a separate corrupted legacy provenance test which must fail closed.

---

# 20. Test-quality note

The test:

```text
LegacyReplacement_
ProvenanceChangesBetweenSemanticProofAndMaterialization_
NotBlessed
```

currently tampers the file **before** `CreateReferenceReplacementTransaction()`.

It proves:

```text
already-corrupt legacy provenance is rejected
```

rather than literally simulating a change between semantic proof and hash materialization.

Because the implementation now uses one byte snapshot, the old internal “between” race no longer exists — which is correct.

A clearer name would be:

```text
LegacyReplacement_CorruptProvenance_
NotMaterializedAsAuthority
```

This naming issue is not a blocker.

---

# 21. Fresh areas verified clean

No additional defect was found in:

```text
[PASS] hash-owned forward move helper
[PASS] source existence recheck after move hook
[PASS] destination collision recheck after move hook
[PASS] path safety after move hook
[PASS] fresh source SHA after move hook
[PASS] overwrite:false on managed forward moves

[PASS] Main provenance promotion
[PASS] Main root image promotion
[PASS] Main ingame promotion
[PASS] initial Reference image promotion
[PASS] initial Reference provenance promotion
[PASS] replacement OLD image backup
[PASS] replacement OLD provenance backup
[PASS] replacement NEW image promotion
[PASS] replacement NEW provenance promotion

[PASS] modern Reference raw provenance authority
[PASS] legacy Reference one-snapshot semantic + raw authority
[PASS] BOM Reference raw-hash materialization
[PASS] Cancel legacy provenance materialization
[PASS] Cancel move path + byte boundary
[PASS] Cancel delete path + byte boundary
[PASS] Cancel restore path + byte boundary

[PASS] replacement cleanup hash-owned deletion
[PASS] replacement rollback hash-owned deletion
[PASS] replacement rollback hash-owned restore
[PASS] restore destination recheck
[PASS] directory cleanup post-hook path check

[PASS] legacy OldBackedUp one-run recovery path
[PASS] legacy NewPromotionPending one-run recovery path
[PASS] legacy NewPromoted authority matching
[PASS] legacy CleanupPending one-run success path
[PASS] corrupt legacy OLD backup fail-closed behavior
```

---

# 22. Recommended repair order

## Phase 1 — R15-001

Fix the recovery alias/durable-upgrade bug first.

Required invariant:

```text
missing durable OLD byte authority
-> derive safely
-> persist safely
-> only then perform rollback/cleanup mutation
```

Add the two-startup CleanupPending retry test.

## Phase 2 — R15-002

Correct the two contradictory RecoveryCritical assertions.

Production behavior should remain unchanged.

## Phase 3 — R15-003

Use `TryGetExactFinalProvenanceRawHash()` in legacy Main rollback and add BOM rollback coverage.

---

# 23. Static checks after repair

## Recovery aliasing

```powershell
rg -n `
  "OldSession = journal\.OldSession|NewSession = journal\.NewSession" `
  src/AssetProvenanceHelper
```

If direct aliasing remains, recovery must not use post-hydration object equality to decide whether the durable journal changed.

---

## Durable OLD authority upgrade

```powershell
rg -n `
  "EnsureOldProvenanceByteAuthority|SaveReplacementJournal" `
  src/AssetProvenanceHelper/MainForm.Recovery.cs
```

Required order:

```text
capture durable hash presence
hydrate
persist upgrade
rollback/cleanup
```

---

## Cleanup retry coverage

```powershell
rg -n `
  "HydratedHashPersists|CleanupPending.*JournalDelete|second startup" `
  tests
```

A two-startup durability test is mandatory.

---

## Cancel byte-race expectations

```powershell
rg -n `
  "ProvenanceBytesChange_NoMove|ReferenceBytesChange_NoMove" `
  tests/AssetProvenanceHelper.Tests/Bugs3ParanoidTests.cs
```

Expected current mutation-boundary error:

```text
InvalidDataException
hash changed before move
```

---

## Legacy Main provenance authority

```powershell
rg -n `
  "MainProvenanceHash \?\?|TryGetExactFinalProvenanceRawHash" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs
```

`RollbackMain()` must not use a rendered no-BOM synthetic hash for legacy exact provenance raw bytes.

---

# 24. Required Windows execution gate

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

Debug:

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Debug `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Debug `
  --no-build
```

Release:

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Release `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Release `
  --no-build
```

RecoveryCritical:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical" `
  --logger "console;verbosity=detailed"
```

Required:

```text
0 failed
0 skipped
```

20× Release:

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

---

# 25. Definition of Done for the next audit

## Preserve R14

```text
[ ] MoveHashOwnedFileWithoutOverwrite remains
[ ] path validation remains after move hook
[ ] source SHA remains after move hook
[ ] destination is rechecked after move hook
[ ] all Main canonical moves use helper
[ ] all initial Reference canonical moves use helper
[ ] all replacement backup/promotion moves use helper
[ ] legacy Reference provenance uses one byte snapshot
[ ] Cancel legacy provenance uses one byte snapshot
[ ] R13 delete/restore safety remains
```

## R15-001

```text
[ ] recovery no longer hides journal upgrade through object aliasing
[ ] missing OLD provenance hash is detected before hydration
[ ] hydrated raw hash is durably saved before cleanup mutation
[ ] hydrated raw hash is durably saved before rollback mutation
[ ] failed upgraded-journal save causes zero asset mutation
[ ] CleanupPending + journal-delete failure leaves upgraded journal
[ ] second startup finalizes already-cleaned replacement
```

## R15-002

```text
[ ] provenance byte-race test expects mutation-boundary hash error
[ ] Reference byte-race test expects original InvalidDataException
[ ] Reference byte-race test verifies provenance was restored
[ ] both tests assert their hook was reached
[ ] RecoveryCritical suite contains no contradictory expectations
```

## R15-003

```text
[ ] RollbackMain obtains verified raw hash for legacy final provenance
[ ] RollbackMain obtains verified raw hash for legacy temp provenance
[ ] exact legacy BOM final provenance can be rolled back
[ ] foreign/tampered legacy provenance is still preserved
[ ] modern MainProvenanceHash semantics are unchanged
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

# 26. Final fifteenth-pass conclusion

The R14 implementation is a meaningful improvement and is close to convergence.

The forward mutation model is now consistently:

```text
hook
current path authority
fresh byte authority
no-overwrite mutation
```

and legacy Reference provenance authority is correctly tied to one byte snapshot.

The remaining HIGH issue is narrower but real:

```text
recovery successfully derives missing legacy OLD authority,
but fails to persist it because journal and transaction
share the same mutable OldSession object.
```

That creates a deterministic CleanupPending retry deadlock if cleanup succeeds but journal deletion fails.

In addition, two new RecoveryCritical tests contradict the current intended Cancel behavior, and legacy Main rollback still does not use the raw-hash authority helper that now exists specifically for legacy provenance.

**Current acceptance state: FAIL — R15-001 is the source-level blocker; R15-002 also prevents a clean mandatory RecoveryCritical run.**


---

# 27. IMPLEMENTATION HANDOFF FOR A WEAKER MODEL — FOLLOW THIS LITERALLY

This section is intentionally redundant and prescriptive.

**Do not redesign the solution. Do not reopen already fixed R1-R14 issues. Do not replace the safety model with a simpler one. Implement the following edits exactly, then run the exact tests listed below.**

Current authoritative source baseline:

```text
repository: Ceegore/gpt_provenance_document_helper
branch:     main
commit:     5e9a8ebf7119ad683b717d0df98f2a7db346321d
```

Only three remaining implementation items are in scope:

```text
R15-001  HIGH
    Make legacy replacement-journal hash hydration DURABLE
    before rollback/cleanup mutates files.

R15-002  MEDIUM-HIGH
    Correct two RecoveryCritical test expectations.
    Production Cancel behavior is already correct for these cases.

R15-003  MEDIUM
    Make legacy Main rollback use the exact raw provenance hash
    returned from ValidationService instead of a synthesized no-BOM hash.
```

Do not add unrelated features.

---

# 28. FILES TO EDIT

The weaker implementation model should normally need to modify only:

```text
src/AssetProvenanceHelper/MainForm.Recovery.cs

src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs

tests/AssetProvenanceHelper.Tests/Bugs3ParanoidTests.cs
```

Normally **do not modify** these already-correct R14 files unless a compile error proves a small signature adjustment is required:

```text
src/AssetProvenanceHelper/Services/AssetProcessorService.FileOps.cs
src/AssetProvenanceHelper/Services/ValidationService.Session.cs
src/AssetProvenanceHelper/Services/SessionService.cs
src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

In particular, preserve:

```text
MoveHashOwnedFileWithoutOverwrite(...)
TryDeleteHashOwnedFileWithError(...)
TryRestoreHashOwnedFileWithError(...)
TryGetExactReferenceProvenanceRawHash(...)
TryGetExactFinalProvenanceRawHash(...)
MoveHashOwnedCancelFile(...)
DeleteHashOwnedCancelFile(...)
RestoreHashOwnedCancelFile(...)
```

They are part of the repaired architecture.

---

# 29. FORBIDDEN SHORTCUTS

Do **not** solve the remaining issues by doing any of the following:

```text
DO NOT make hash checks optional.

DO NOT delete unknown files merely because their paths look correct.

DO NOT reintroduce decoded-text equality as the destructive-operation
ownership check for modern sessions.

DO NOT remove the legacy compatibility path.

DO NOT catch and ignore recovery errors.

DO NOT delete a replacement journal before filesystem reconciliation
is complete.

DO NOT perform rollback/cleanup before a newly derived legacy hash has
been durably persisted.

DO NOT weaken reparse-point checks.

DO NOT change production Cancel behavior just to satisfy the two
incorrect tests in R15-002.

DO NOT replace File.Move(... overwrite:false) with overwrite:true
for managed asset files.

DO NOT mutate the user's selected source image.

DO NOT reset transaction metadata on an incomplete/failed rollback.

DO NOT invent a new schema/versioning framework for these three fixes.
```

---

# 30. PATCH A — R15-001 — FIX REPLACEMENT-JOURNAL ALIASING AND DURABILITY

## 30.1 File

Edit:

```text
src/AssetProvenanceHelper/MainForm.Recovery.cs
```

---

# 31. PATCH A1 — ADD AN EXACT `AssetSession` CLONE HELPER

Add this private static helper near `TransactionFromJournal()`.

Use **all current persisted fields** exactly as shown below.

```csharp
private static AssetSession CloneAssetSessionForRecovery(
    AssetSession source)
{
    ArgumentNullException.ThrowIfNull(source);

    return new AssetSession
    {
        WorkflowMode =
            source.WorkflowMode,

        ReferenceCommitPhase =
            source.ReferenceCommitPhase,

        ReferenceTransactionId =
            source.ReferenceTransactionId,

        ProjectName =
            source.ProjectName,

        AssetRootFolder =
            source.AssetRootFolder,

        AssetFolderName =
            source.AssetFolderName,

        AssetFolder =
            source.AssetFolder,

        ReferenceSourcePath =
            source.ReferenceSourcePath,

        ReferenceDestinationPath =
            source.ReferenceDestinationPath,

        ReferenceFilename =
            source.ReferenceFilename,

        ReferenceProvenancePath =
            source.ReferenceProvenancePath,

        ReferenceHash =
            source.ReferenceHash,

        ReferenceProvenanceHash =
            source.ReferenceProvenanceHash,

        ReferenceProcessedAt =
            source.ReferenceProcessedAt,

        WasAssetFolderCreatedByTool =
            source.WasAssetFolderCreatedByTool,

        WasReferenceFolderCreatedByTool =
            source.WasReferenceFolderCreatedByTool,

        WasIngameFolderCreatedByTool =
            source.WasIngameFolderCreatedByTool,

        IsMainCommitting =
            source.IsMainCommitting,

        MainFilename =
            source.MainFilename,

        MainProvenanceHash =
            source.MainProvenanceHash,

        MainPrompt =
            source.MainPrompt,

        MainProcessedAt =
            source.MainProcessedAt,

        MainHash =
            source.MainHash,

        CancelPhase =
            source.CancelPhase,

        CancellationId =
            source.CancellationId,

        MainTransactionId =
            source.MainTransactionId
    };
}
```

Why every field is copied:

```text
Recovery authority must be an independent snapshot,
but it must remain semantically identical to the serialized session.

Omitting a field can create false authority mismatches
or incorrect temp-path calculations later.
```

---

# 32. PATCH A2 — REPLACE `TransactionFromJournal()`

Find the current implementation that contains:

```csharp
OldSession = journal.OldSession,
NewSession = journal.NewSession,
```

Replace the entire method with:

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
            CloneAssetSessionForRecovery(
                journal.OldSession),

        NewSession =
            CloneAssetSessionForRecovery(
                journal.NewSession),

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

After this change, this must be true:

```text
ReferenceEquals(
    transaction.OldSession,
    journal.OldSession)
== false
```

and:

```text
ReferenceEquals(
    transaction.NewSession,
    journal.NewSession)
== false
```

Do not use JSON serialize/deserialize for this clone.

---

# 33. PATCH A3 — ADD ONE DURABLE-HYDRATION HELPER

Still in:

```text
MainForm.Recovery.cs
```

add:

```csharp
private bool EnsureOldProvenanceAuthorityIsDurable(
    ReferenceReplacementJournal journal,
    ReferenceReplacementTransaction transaction,
    string recoveryOperation)
{
    ArgumentNullException.ThrowIfNull(journal);
    ArgumentNullException.ThrowIfNull(transaction);
    ArgumentNullException.ThrowIfNull(recoveryOperation);

    var durableHashWasMissing =
        string.IsNullOrWhiteSpace(
            journal
                .OldSession
                .ReferenceProvenanceHash);

    var authorityResult =
        _assetProcessorService
            .EnsureOldProvenanceByteAuthority(
                transaction);

    if (!authorityResult.IsValid)
    {
        return FailReplacementRecovery(
            $"Could not establish byte authority for {recoveryOperation}:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                authorityResult.Errors));
    }

    if (!durableHashWasMissing)
    {
        return true;
    }

    var hydratedHash =
        transaction
            .OldSession
            .ReferenceProvenanceHash;

    if (string.IsNullOrWhiteSpace(
            hydratedHash))
    {
        return FailReplacementRecovery(
            $"Could not establish durable OLD provenance hash authority for {recoveryOperation}.");
    }

    journal
        .OldSession
        .ReferenceProvenanceHash =
        hydratedHash;

    try
    {
        _sessionService
            .SaveReplacementJournal(
                journal);
    }
    catch (Exception ex)
    {
        return FailReplacementRecovery(
            $"Could not persist upgraded replacement journal before {recoveryOperation}.",
            ex);
    }

    return true;
}
```

Critical ordering enforced by this helper:

```text
1. Determine whether DISK authority was missing.
2. Derive exact raw OLD provenance hash.
3. Copy the hash into the journal object.
4. Durably save replacement journal.
5. Return true.
6. Only caller may then mutate asset files.
```

---

# 34. PATCH A4 — REPLACE THE AUTHORITY BLOCK IN `RollBackReplacementJournal()`

Current code conceptually does:

```csharp
var transaction =
    TransactionFromJournal(journal);

var authorityResult =
    _assetProcessorService
        .EnsureOldProvenanceByteAuthority(
            transaction);

...

if (
    journal.OldSession.ReferenceProvenanceHash
    !=
    transaction.OldSession.ReferenceProvenanceHash)
{
    ...
}
```

Delete that entire old hydration/comparison/save block.

The beginning of the method must become:

```csharp
private bool RollBackReplacementJournal(
    ReferenceReplacementJournal journal)
{
    var transaction =
        TransactionFromJournal(
            journal);

    if (!EnsureOldProvenanceAuthorityIsDurable(
            journal,
            transaction,
            "replacement rollback"))
    {
        return false;
    }

    var rollback =
        _assetProcessorService
            .RollbackReferenceReplacement(
                transaction);

    if (!rollback.IsValid)
    {
        ShowMessageBox(
            "CRITICAL: The interrupted Reference replacement could not be safely rolled back."
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
            "CRITICAL: Replacement files were rolled back, but the old durable session/journal state could not be finalized.",
            ex);

        Close();
        return false;
    }

    AddStatus(
        "Interrupted Reference replacement was rolled back to the previous Reference.");

    return true;
}
```

Important:

```text
The saved final OLD session is still journal.OldSession.

Because the durable-hydration helper copies the verified raw hash
back into journal.OldSession before rollback, the final saved OLD
session will also retain that hash.
```

---

# 35. PATCH A5 — REPLACE THE AUTHORITY BLOCK IN `FinishReplacementCommit()`

Keep its existing:

```text
MatchesReferenceAuthority(...)
ValidateExactReferenceOutput(NewSession)
```

preconditions.

After:

```csharp
var transaction =
    TransactionFromJournal(
        journal);
```

replace the old hydration/comparison block with:

```csharp
if (!EnsureOldProvenanceAuthorityIsDurable(
        journal,
        transaction,
        "replacement cleanup"))
{
    return false;
}
```

Then retain:

```csharp
var cleanup =
    _assetProcessorService
        .CleanupReplacementBackups(
            transaction);
```

and its existing error handling.

The full critical ordering must be:

```text
validate durable NewSession
validate exact NEW canonical output

create independent transaction snapshot

derive missing OLD raw authority
persist upgraded journal
IF SAVE FAILS -> RETURN FALSE, NO CLEANUP

only then:
    CleanupReplacementBackups(transaction)

only after successful cleanup:
    DeleteReplacementJournal()
```

---

# 36. PATCH A6 — DO NOT ADD A SPECIAL CASE FOR MISSING BACKUPS

Do **not** change `EnsureOldProvenanceByteAuthority()` to accept:

```text
null hash + no OLD backup + no OLD canonical
```

That would weaken authority.

The correct retry behavior is:

```text
the hash is persisted BEFORE the backups disappear,
therefore later retries no longer need the old file
to reconstruct authority.
```

This is why PATCH A3-A5 are required.

---

# 37. PATCH A7 — EXACT R15-001 TEST 1: TWO-STARTUP CLEANUP RETRY

File:

```text
tests/AssetProvenanceHelper.Tests/Bugs3ParanoidTests.cs
```

Add:

```csharp
[Fact]
[Trait("Category", "RecoveryCritical")]
public void LegacyReplacementJournal_CleanupPending_
    HydratedHashPersistsBeforeCleanupMutation()
{
    using var workspace =
        new TestWorkspace();

    var processor =
        workspace.CreateAssetProcessor();

    var settings =
        workspace.CreateSettings();

    var sessionService =
        workspace.CreateSessionService();

    var ref1 =
        workspace.CreateImage(
            "ref1.png",
            new byte[]
            {
                1,
                2,
                3
            });

    var oldSession =
        processor.ProcessReference(
            settings,
            "asset_r15_cleanup_retry",
            ref1,
            DateTimeOffset.Now);

    sessionService.Save(
        oldSession);

    var ref2 =
        workspace.CreateImage(
            "ref2.png",
            new byte[]
            {
                4,
                5,
                6
            });

    var tx =
        processor
            .CreateReferenceReplacementTransaction(
                oldSession,
                settings.AcceptedExtensions,
                ref2,
                DateTimeOffset.Now);

    processor.CreateReplacementTempFiles(
        tx,
        settings.AcceptedExtensions);

    processor.BackupOldReference(
        tx);

    processor.PromoteNewReference(
        tx);

    sessionService.Save(
        tx.NewSession);

    var journal =
        tx.ToJournal(
            ReferenceReplacementPhase
                .CleanupPending);

    journal
        .OldSession
        .ReferenceProvenanceHash =
        null;

    sessionService
        .SaveReplacementJournal(
            journal);

    var deleteAttempts = 0;

    SessionService
        .OnBeforeReplacementJournalDeleteHook =
        () =>
        {
            deleteAttempts++;

            if (deleteAttempts == 1)
            {
                throw new IOException(
                    "Simulated replacement journal delete failure.");
            }
        };

    try
    {
        RunStartupRecovery(
            workspace,
            settings,
            processor,
            sessionService);

        Assert.True(
            sessionService
                .ReplacementJournalExists(),
            "Journal must remain after simulated deletion failure.");

        Assert.False(
            File.Exists(
                tx.BackupReferencePath),
            "OLD image backup should already be cleaned.");

        Assert.False(
            File.Exists(
                tx.BackupProvenancePath),
            "OLD provenance backup should already be cleaned.");

        var persistedAfterFirstRecovery =
            sessionService
                .LoadReplacementJournal();

        Assert.NotNull(
            persistedAfterFirstRecovery);

        Assert.False(
            string.IsNullOrWhiteSpace(
                persistedAfterFirstRecovery!
                    .OldSession
                    .ReferenceProvenanceHash),
            "Hydrated OLD provenance hash MUST be durable before backup cleanup.");

        SessionService
            .OnBeforeReplacementJournalDeleteHook =
            null;

        RunStartupRecovery(
            workspace,
            settings,
            processor,
            sessionService);

        Assert.False(
            sessionService
                .ReplacementJournalExists(),
            "Second startup must finish cleanup using the durable hydrated authority.");

        var finalSession =
            sessionService.Load();

        Assert.NotNull(
            finalSession);

        Assert.Equal(
            tx.NewSession.ReferenceHash,
            finalSession!
                .ReferenceHash);

        Assert.Equal(
            tx.NewSession.ReferenceFilename,
            finalSession
                .ReferenceFilename);

        Assert.True(
            File.Exists(
                tx.NewSession
                    .ReferenceDestinationPath));

        Assert.True(
            File.Exists(
                tx.NewSession
                    .ReferenceProvenancePath));
    }
    finally
    {
        SessionService
            .OnBeforeReplacementJournalDeleteHook =
            null;
    }
}
```

This is the most important new regression test.

---

# 38. PATCH A8 — EXACT R15-001 TEST 2: UPGRADED JOURNAL SAVE FAILURE MUST CAUSE ZERO ROLLBACK MUTATION

Add:

```csharp
[Fact]
[Trait("Category", "RecoveryCritical")]
public void LegacyReplacementJournal_OldBackedUp_
    HydrationSaveFailure_NoRollbackMutation()
{
    using var workspace =
        new TestWorkspace();

    var processor =
        workspace.CreateAssetProcessor();

    var settings =
        workspace.CreateSettings();

    var sessionService =
        workspace.CreateSessionService();

    var ref1 =
        workspace.CreateImage(
            "ref1.png",
            new byte[]
            {
                1,
                2,
                3
            });

    var oldSession =
        processor.ProcessReference(
            settings,
            "asset_r15_hydration_save_fail",
            ref1,
            DateTimeOffset.Now);

    sessionService.Save(
        oldSession);

    var ref2 =
        workspace.CreateImage(
            "ref2.png",
            new byte[]
            {
                4,
                5,
                6
            });

    var tx =
        processor
            .CreateReferenceReplacementTransaction(
                oldSession,
                settings.AcceptedExtensions,
                ref2,
                DateTimeOffset.Now);

    processor.CreateReplacementTempFiles(
        tx,
        settings.AcceptedExtensions);

    processor.BackupOldReference(
        tx);

    var journal =
        tx.ToJournal(
            ReferenceReplacementPhase
                .OldBackedUp);

    journal
        .OldSession
        .ReferenceProvenanceHash =
        null;

    sessionService
        .SaveReplacementJournal(
            journal);

    Assert.False(
        File.Exists(
            oldSession
                .ReferenceDestinationPath));

    Assert.False(
        File.Exists(
            oldSession
                .ReferenceProvenancePath));

    Assert.True(
        File.Exists(
            tx.BackupReferencePath));

    Assert.True(
        File.Exists(
            tx.BackupProvenancePath));

    SessionService
        .OnReplacementPhaseSavingHook =
        (phase, _) =>
        {
            if (phase ==
                ReferenceReplacementPhase
                    .OldBackedUp)
            {
                throw new IOException(
                    "Simulated upgraded-journal save failure.");
            }
        };

    try
    {
        RunStartupRecovery(
            workspace,
            settings,
            processor,
            sessionService);

        Assert.True(
            sessionService
                .ReplacementJournalExists(),
            "Journal must remain after hydration save failure.");

        Assert.True(
            File.Exists(
                tx.BackupReferencePath),
            "Rollback must NOT restore/delete backup image before hydrated authority is durable.");

        Assert.True(
            File.Exists(
                tx.BackupProvenancePath),
            "Rollback must NOT restore/delete backup provenance before hydrated authority is durable.");

        Assert.False(
            File.Exists(
                oldSession
                    .ReferenceDestinationPath),
            "Canonical OLD image must remain absent because rollback mutation must not start.");

        Assert.False(
            File.Exists(
                oldSession
                    .ReferenceProvenancePath),
            "Canonical OLD provenance must remain absent because rollback mutation must not start.");

        var persisted =
            sessionService
                .LoadReplacementJournal();

        Assert.NotNull(
            persisted);

        Assert.True(
            string.IsNullOrWhiteSpace(
                persisted!
                    .OldSession
                    .ReferenceProvenanceHash),
            "Failed upgrade save must not be reported as durable.");
    }
    finally
    {
        SessionService
            .OnReplacementPhaseSavingHook =
            null;
    }
}
```

Important:

```text
Install OnReplacementPhaseSavingHook only AFTER the initial legacy
journal has already been saved, otherwise the test would block its
own setup.
```

---

# 39. PATCH A9 — ADD A DIRECT NO-ALIAS UNIT TEST

This test is cheap and prevents the exact bug from returning.

Because `TransactionFromJournal()` is private, use reflection:

```csharp
[Fact]
[Trait("Category", "RecoveryCritical")]
public void TransactionFromJournal_UsesIndependentSessionSnapshots()
{
    using var workspace =
        new TestWorkspace();

    var processor =
        workspace.CreateAssetProcessor();

    var settings =
        workspace.CreateSettings();

    var ref1 =
        workspace.CreateImage(
            "ref1.png",
            new byte[]
            {
                1,
                2,
                3
            });

    var oldSession =
        processor.ProcessReference(
            settings,
            "asset_r15_no_alias",
            ref1,
            DateTimeOffset.Now);

    var ref2 =
        workspace.CreateImage(
            "ref2.png",
            new byte[]
            {
                4,
                5,
                6
            });

    var tx =
        processor
            .CreateReferenceReplacementTransaction(
                oldSession,
                settings.AcceptedExtensions,
                ref2,
                DateTimeOffset.Now);

    var journal =
        tx.ToJournal(
            ReferenceReplacementPhase
                .Prepared);

    var method =
        typeof(MainForm)
            .GetMethod(
                "TransactionFromJournal",
                BindingFlags.NonPublic
                | BindingFlags.Static);

    Assert.NotNull(method);

    var recoveredTx =
        (ReferenceReplacementTransaction)
        method!.Invoke(
            null,
            new object[]
            {
                journal
            })!;

    Assert.False(
        ReferenceEquals(
            journal.OldSession,
            recoveredTx.OldSession));

    Assert.False(
        ReferenceEquals(
            journal.NewSession,
            recoveredTx.NewSession));

    var journalOldHash =
        journal
            .OldSession
            .ReferenceProvenanceHash;

    recoveredTx
        .OldSession
        .ReferenceProvenanceHash =
        new string(
            'a',
            64);

    Assert.Equal(
        journalOldHash,
        journal
            .OldSession
            .ReferenceProvenanceHash);
}
```

If reflection visibility makes this awkward, this test may instead be placed in a test-visible helper layer, but **do not remove the independent-snapshot production fix**.

---

# 40. PATCH B — R15-002 — FIX ONLY THE INCORRECT TEST EXPECTATIONS

## 40.1 Production code rule

For R15-002:

```text
DO NOT MODIFY SessionService.Cancel()
unless compilation proves a separate defect.

The tested production behavior is already the intended behavior:
fresh byte authority detects mutation at the exact move boundary.
```

---

# 41. PATCH B1 — FIX `Cancel_OnBeforeCancelFileMoveHook_ProvenanceBytesChange_NoMove`

Locate this test.

Keep its arrangement and hook.

Replace the exception/message assertion with:

```csharp
var ex =
    Assert.Throws<InvalidDataException>(
        () =>
            sessionService.Cancel(
                session));

Assert.True(
    hookInvoked,
    "OnBeforeCancelFileMoveHook must be invoked.");

Assert.Contains(
    "reference provenance",
    ex.Message,
    StringComparison.OrdinalIgnoreCase);

Assert.Contains(
    "hash changed before move",
    ex.Message,
    StringComparison.OrdinalIgnoreCase);

Assert.True(
    File.Exists(
        session.ReferenceProvenancePath),
    "Modified canonical provenance must remain preserved.");

Assert.False(
    File.Exists(
        session.GetCancelTempProvenancePath()),
    "Cancel temp provenance must not be created after the boundary hash check fails.");

Assert.True(
    sessionService.Exists(),
    "Cancellation journal/session must remain available for recovery.");
```

Do not expect:

```text
"Reference provenance on disk does not match"
```

because mutation occurs after that earlier semantic validation.

---

# 42. PATCH B2 — FIX `Cancel_OnBeforeCancelFileMoveHook_ReferenceBytesChange_NoMove`

Keep its setup and hook.

Replace:

```csharp
Assert.Throws<IOException>(...)
```

with:

```csharp
var ex =
    Assert.Throws<InvalidDataException>(
        () =>
            sessionService.Cancel(
                session));
```

Then assert:

```csharp
Assert.True(
    hookInvoked,
    "OnBeforeCancelFileMoveHook must be invoked.");

Assert.Contains(
    "reference image",
    ex.Message,
    StringComparison.OrdinalIgnoreCase);

Assert.Contains(
    "hash changed before move",
    ex.Message,
    StringComparison.OrdinalIgnoreCase);

Assert.True(
    File.Exists(
        session.ReferenceDestinationPath),
    "Modified Reference image must remain at the canonical path and must not be moved.");

Assert.True(
    File.Exists(
        session.ReferenceProvenancePath),
    "Previously moved provenance must be restored after the Reference move fails.");

Assert.False(
    File.Exists(
        session.GetCancelTempReferencePath()),
    "Cancel temp Reference must not be created.");

Assert.False(
    File.Exists(
        session.GetCancelTempProvenancePath()),
    "Cancel temp provenance must be removed by successful restoration.");

Assert.Equal(
    CancelPhase.None,
    session.CancelPhase);

Assert.Null(
    session.CancellationId);
```

Also verify the durable session reset:

```csharp
var persisted =
    sessionService.Load();

Assert.NotNull(
    persisted);

Assert.Equal(
    CancelPhase.None,
    persisted!.CancelPhase);

Assert.Null(
    persisted.CancellationId);
```

Do not expect:

```text
"Cancel failed during reference image rename"
```

in this test because provenance restoration succeeds and the original `InvalidDataException` is rethrown.

---

# 43. PATCH B3 — KEEP THE RESTORE-FAILURE TEST AS THE WRAPPER-MESSAGE TEST

The separate test which deliberately makes:

```text
Reference move fail
AND
provenance restore fail
```

should continue to expect a wrapper such as:

```text
restoring reference provenance also failed
```

That is the correct place to test the wrapper behavior.

Do not merge the two scenarios.

---

# 44. PATCH C — R15-003 — LEGACY MAIN ROLLBACK MUST USE VERIFIED RAW PROVENANCE HASHES

## 44.1 File

Edit:

```text
src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs
```

Method:

```text
RollbackMain(...)
```

---

# 45. PATCH C1 — REPLACE FINAL-PROVENANCE PHASE-A VALIDATION

Current code has a block conceptually like:

```csharp
if (File.Exists(provenancePath))
{
    var provValidation =
        _validationService
            .ValidateExactFinalProvenanceOwnership(
                session,
                provenancePath,
                _templateService);

    if (!provValidation.IsValid)
    {
        return ValidationResult.Failure(...);
    }
}
```

Replace it with:

```csharp
string? finalProvenanceRawHash =
    null;

if (File.Exists(
        provenancePath))
{
    var provValidation =
        _validationService
            .TryGetExactFinalProvenanceRawHash(
                session,
                provenancePath,
                _templateService,
                out finalProvenanceRawHash);

    if (!provValidation.IsValid
        || string.IsNullOrWhiteSpace(
            finalProvenanceRawHash))
    {
        return ValidationResult.Failure(
            $"Final provenance on disk does not match session state "
            + $"({string.Join("; ", provValidation.Errors)}). "
            + "Refusing to delete unknown file.");
    }
}
```

This is correct for both:

```text
modern sessions:
    returns actual hash only if it matches durable MainProvenanceHash

legacy sessions:
    exact semantic proof + raw hash from same byte snapshot
```

---

# 46. PATCH C2 — REPLACE TEMP-PROVENANCE PHASE-A VALIDATION

Find the current:

```csharp
if (!string.IsNullOrWhiteSpace(tempProv)
    && File.Exists(tempProv))
{
    var tempProvValidation =
        _validationService
            .ValidateExactFinalProvenanceOwnership(
                session,
                tempProv,
                _templateService);

    ...
}
```

Replace with:

```csharp
string? tempProvenanceRawHash =
    null;

if (!string.IsNullOrWhiteSpace(
        tempProv)
    && File.Exists(
        tempProv))
{
    var tempProvValidation =
        _validationService
            .TryGetExactFinalProvenanceRawHash(
                session,
                tempProv,
                _templateService,
                out tempProvenanceRawHash);

    if (!tempProvValidation.IsValid
        || string.IsNullOrWhiteSpace(
            tempProvenanceRawHash))
    {
        return ValidationResult.Failure(
            $"Main temp provenance at '{tempProv}' "
            + "does not match session state "
            + $"({string.Join("; ", tempProvValidation.Errors)}). "
            + "Refusing to delete unknown file.");
    }
}
```

---

# 47. PATCH C3 — DELETE THE SYNTHETIC `expectedProvHash` FALLBACK

Delete this logic from `RollbackMain()`:

```csharp
var expectedProvHash =
    session.MainProvenanceHash
    ?? (
        session.MainPrompt is not null
        && session.MainProcessedAt.HasValue
            ? Convert.ToHexString(
                SHA256.HashData(
                    new UTF8Encoding(false)
                        .GetBytes(
                            /* rendered provenance */)))
                .ToLowerInvariant()
            : string.Empty);
```

For `RollbackMain()`, this synthetic fallback is no longer needed.

Do not use a rendered-text hash as a substitute for a verified raw legacy file hash.

---

# 48. PATCH C4 — USE `finalProvenanceRawHash` AT FINAL-PROVENANCE DELETE

Replace:

```csharp
TryDeleteHashOwnedFileWithError(
    provenancePath,
    expectedProvHash,
    "Final provenance",
    () =>
        ValidateSessionDestructivePathSafety(
            session),
    errors);
```

with:

```csharp
if (File.Exists(
        provenancePath))
{
    if (string.IsNullOrWhiteSpace(
            finalProvenanceRawHash))
    {
        return ValidationResult.Failure(
            "Final provenance exists but verified raw hash authority is missing.");
    }

    TryDeleteHashOwnedFileWithError(
        provenancePath,
        finalProvenanceRawHash,
        "Final provenance",
        () =>
            ValidateSessionDestructivePathSafety(
                session),
        errors);
}
```

If the existing surrounding `if (File.Exists(provenancePath))` already exists, do not nest a duplicate block; just insert the null guard and substitute the hash variable.

---

# 49. PATCH C5 — USE `tempProvenanceRawHash` AT TEMP-PROVENANCE DELETE

Replace:

```csharp
TryDeleteHashOwnedFileWithError(
    tempProv,
    expectedProvHash,
    "Main temp provenance",
    () =>
        ValidateSessionDestructivePathSafety(
            session),
    errors);
```

with:

```csharp
if (!string.IsNullOrWhiteSpace(
        tempProv)
    && File.Exists(
        tempProv))
{
    if (string.IsNullOrWhiteSpace(
            tempProvenanceRawHash))
    {
        return ValidationResult.Failure(
            "Main temp provenance exists but verified raw hash authority is missing.");
    }

    TryDeleteHashOwnedFileWithError(
        tempProv,
        tempProvenanceRawHash,
        "Main temp provenance",
        () =>
            ValidateSessionDestructivePathSafety(
                session),
        errors);
}
```

Again, preserve existing outer structure if already present.

---

# 50. PATCH C6 — IMPORTANT LEGACY ENCODING RULE

Do not normalize the legacy file before deletion.

Do not rewrite BOM provenance to no-BOM.

Correct behavior is:

```text
1. Read actual raw bytes.
2. Decode them with existing BOM-aware semantic validation.
3. Prove semantic exactness.
4. Hash the SAME raw bytes.
5. At deletion boundary, re-hash current file.
6. Delete only if current raw hash still equals the verified raw hash.
```

That preserves exact byte ownership.

---

# 51. PATCH C7 — ADD EXACT LEGACY MAIN BOM ROLLBACK TEST

File:

```text
tests/AssetProvenanceHelper.Tests/Bugs3ParanoidTests.cs
```

Add:

```csharp
[Fact]
[Trait("Category", "RecoveryCritical")]
public void RollbackMain_LegacyNullProvenanceHash_
    ExactBomProvenance_RollsBack()
{
    using var workspace =
        new TestWorkspace();

    var processor =
        workspace.CreateAssetProcessor();

    var settings =
        workspace.CreateSettings();

    var sessionService =
        workspace.CreateSessionService();

    var templateService =
        workspace.CreateTemplateService();

    var validationService =
        workspace.CreateValidationService();

    var refImage =
        workspace.CreateImage(
            "ref.png",
            new byte[]
            {
                1,
                2,
                3
            });

    var session =
        processor.ProcessReference(
            settings,
            "asset_r15_legacy_main_bom",
            refImage,
            DateTimeOffset.Now);

    var mainImage =
        workspace.CreateImage(
            "main.png",
            new byte[]
            {
                4,
                5,
                6
            });

    var processedAt =
        DateTimeOffset.Now;

    session =
        processor.PrepareMainCommit(
            session,
            settings.AcceptedExtensions,
            mainImage,
            "legacy BOM prompt",
            processedAt);

    session.MainProvenanceHash =
        null;

    sessionService.Save(
        session);

    var finalProvenancePath =
        Path.Combine(
            session.AssetFolder,
            AppConstants
                .FinalProvenanceFileName);

    var generationDate =
        processedAt.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

    var exactText =
        templateService.RenderFinal(
            session.MainFilename!,
            session.ReferenceFilename,
            session.ProjectName,
            generationDate,
            session.MainPrompt!);

    File.WriteAllText(
        finalProvenancePath,
        exactText,
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier:
                true));

    var exactBeforeRollback =
        validationService
            .ValidateExactFinalProvenanceOwnership(
                session,
                finalProvenancePath,
                templateService);

    Assert.True(
        exactBeforeRollback.IsValid,
        string.Join(
            Environment.NewLine,
            exactBeforeRollback.Errors));

    var rollback =
        processor.RollbackMain(
            session);

    Assert.True(
        rollback.IsValid,
        string.Join(
            Environment.NewLine,
            rollback.Errors));

    Assert.False(
        File.Exists(
            finalProvenancePath),
        "Exact legacy BOM provenance should be deleted as tool-owned.");

    Assert.False(
        session.IsMainCommitting);

    Assert.Null(
        session.MainTransactionId);

    Assert.Null(
        session.MainFilename);

    Assert.Null(
        session.MainHash);

    Assert.Null(
        session.MainProvenanceHash);
}
```

This test intentionally does not require root Main or ingame output to exist.

It isolates the legacy-provenance rollback authority.

---

# 52. PATCH C8 — ADD CORRUPTED LEGACY MAIN PROVENANCE FAIL-CLOSED TEST

Add:

```csharp
[Fact]
[Trait("Category", "RecoveryCritical")]
public void RollbackMain_LegacyNullProvenanceHash_
    CorruptProvenance_PreservesFile()
{
    using var workspace =
        new TestWorkspace();

    var processor =
        workspace.CreateAssetProcessor();

    var settings =
        workspace.CreateSettings();

    var refImage =
        workspace.CreateImage(
            "ref.png",
            new byte[]
            {
                1,
                2,
                3
            });

    var session =
        processor.ProcessReference(
            settings,
            "asset_r15_legacy_main_corrupt",
            refImage,
            DateTimeOffset.Now);

    var mainImage =
        workspace.CreateImage(
            "main.png",
            new byte[]
            {
                4,
                5,
                6
            });

    session =
        processor.PrepareMainCommit(
            session,
            settings.AcceptedExtensions,
            mainImage,
            "legacy corrupt prompt",
            DateTimeOffset.Now);

    session.MainProvenanceHash =
        null;

    var provenancePath =
        Path.Combine(
            session.AssetFolder,
            AppConstants
                .FinalProvenanceFileName);

    File.WriteAllText(
        provenancePath,
        "FOREIGN PROVENANCE",
        new UTF8Encoding(false));

    var rollback =
        processor.RollbackMain(
            session);

    Assert.False(
        rollback.IsValid);

    Assert.True(
        File.Exists(
            provenancePath),
        "Unknown provenance must be preserved.");

    Assert.True(
        session.IsMainCommitting,
        "Failed rollback must retain active Main transaction metadata.");
}
```

---

# 53. IMPLEMENTATION ORDER — DO NOT CHANGE THIS ORDER

The weaker model should execute in this exact sequence:

```text
STEP 1
    Edit MainForm.Recovery.cs:
        add CloneAssetSessionForRecovery
        replace TransactionFromJournal
        add EnsureOldProvenanceAuthorityIsDurable
        update RollBackReplacementJournal
        update FinishReplacementCommit

STEP 2
    Add the three R15-001 tests:
        two-startup CleanupPending durability
        upgrade-save failure no-mutation
        no-alias snapshot test

STEP 3
    Fix ONLY the assertions in the two R15-002 Cancel tests.

STEP 4
    Edit AssetProcessorService.Main.cs:
        capture finalProvenanceRawHash
        capture tempProvenanceRawHash
        remove synthetic expectedProvHash fallback in RollbackMain
        use the verified per-file raw hashes for deletion

STEP 5
    Add the two R15-003 tests:
        exact BOM legacy rollback succeeds
        corrupt legacy provenance is preserved

STEP 6
    Compile.

STEP 7
    Run RecoveryCritical tests.

STEP 8
    Fix ONLY genuine compile/test defects caused by these edits.

STEP 9
    Run complete Debug + Release tests.

STEP 10
    Run 20x Release, publish, smoke, coverage.

STEP 11
    Do not declare success unless every available required test is green.
```

---

# 54. EXPECTED MODIFIED-FILE SET

After the repair, the expected `git diff --name-only` should normally be:

```text
src/AssetProvenanceHelper/MainForm.Recovery.cs
src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs
tests/AssetProvenanceHelper.Tests/Bugs3ParanoidTests.cs
```

`bugs15.md` itself may also be present as documentation.

If many unrelated production files change, stop and review scope.

---

# 55. EXACT STATIC CHECKS BEFORE COMPILATION

Run:

```powershell
rg -n `
  "OldSession = journal\.OldSession|NewSession = journal\.NewSession" `
  src/AssetProvenanceHelper/MainForm.Recovery.cs
```

Expected:

```text
0 matches
```

Run:

```powershell
rg -n `
  "CloneAssetSessionForRecovery|EnsureOldProvenanceAuthorityIsDurable" `
  src/AssetProvenanceHelper/MainForm.Recovery.cs
```

Expected:

```text
both helpers present
```

Run:

```powershell
rg -n `
  "ReferenceProvenanceHash != transaction\.OldSession\.ReferenceProvenanceHash" `
  src/AssetProvenanceHelper/MainForm.Recovery.cs
```

Expected:

```text
0 matches
```

Run:

```powershell
rg -n `
  "MainProvenanceHash \?\?" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs
```

Interpret carefully:

```text
There may still be valid non-RollbackMain uses elsewhere.

Inside RollbackMain there must no longer be a synthesized
rendered no-BOM fallback used for destructive provenance deletion.
```

Run:

```powershell
rg -n `
  "TryGetExactFinalProvenanceRawHash" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs
```

Expected:

```text
RollbackMain uses it for final provenance
and for temp provenance
```

---

# 56. EXACT RECOVERYCRITICAL TEST COMMAND

Run:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --filter "Category=RecoveryCritical" `
  --logger "console;verbosity=detailed"
```

Required:

```text
Failed:  0
Skipped: 0
```

If a newly fixed Cancel test fails:

```text
inspect the real thrown exception.

Do not immediately change production code.

First verify whether the test still expects the wrong earlier
validation path instead of the final mutation-boundary path.
```

---

# 57. TARGETED TEST FILTERS FOR FAST ITERATION

After compilation, run the R15 tests first.

Suggested:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~LegacyReplacementJournal_CleanupPending_HydratedHashPersistsBeforeCleanupMutation"
```

Then:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~LegacyReplacementJournal_OldBackedUp_HydrationSaveFailure_NoRollbackMutation"
```

Then:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~TransactionFromJournal_UsesIndependentSessionSnapshots"
```

Then:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~Cancel_OnBeforeCancelFileMoveHook_ProvenanceBytesChange_NoMove"
```

Then:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~Cancel_OnBeforeCancelFileMoveHook_ReferenceBytesChange_NoMove"
```

Then:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~RollbackMain_LegacyNullProvenanceHash"
```

All must pass before running the whole suite.

---

# 58. REQUIRED FULL VALIDATION AFTER TARGETED TESTS PASS

Run:

```powershell
dotnet restore AssetProvenanceHelper.sln
```

Then:

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Debug `
  --no-restore `
  -warnaserror
```

Then:

```powershell
dotnet test AssetProvenanceHelper.sln `
  -c Debug `
  --no-build
```

Then:

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Release `
  --no-restore `
  -warnaserror
```

Then:

```powershell
dotnet test AssetProvenanceHelper.sln `
  -c Release `
  --no-build
```

Then the RecoveryCritical command again.

Then:

```powershell
for ($i = 1; $i -le 20; $i++)
{
    Write-Host "Release test pass $i/20"

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

Required:

```text
20/20 green
```

Then:

```powershell
dotnet publish `
  src/AssetProvenanceHelper/AssetProvenanceHelper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish
```

Then:

```powershell
pwsh scripts/run_smoke_tests.ps1 `
  -PublishDir artifacts/publish `
  -LogOutputDir artifacts
```

Then:

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts/coverage
```

---

# 59. MANUAL CODE-REVIEW CHECKLIST AFTER TESTS

The implementing model must explicitly verify all of the following from source after tests pass:

```text
[ ] TransactionFromJournal does not alias OldSession.

[ ] TransactionFromJournal does not alias NewSession.

[ ] A missing legacy OLD provenance hash is detected BEFORE hydration.

[ ] A hydrated legacy OLD provenance hash is persisted BEFORE rollback.

[ ] A hydrated legacy OLD provenance hash is persisted BEFORE cleanup.

[ ] If that persistence fails, rollback/cleanup is not called.

[ ] CleanupPending can be retried after backups are already gone,
    because the OLD raw hash is now durable.

[ ] The two Cancel byte-race tests expect the final hash-boundary error,
    not an earlier semantic-validation error.

[ ] Cancel production logic was not weakened.

[ ] RollbackMain no longer derives destructive legacy provenance authority
    from freshly rendered no-BOM bytes.

[ ] RollbackMain uses the exact raw hash returned from
    TryGetExactFinalProvenanceRawHash.

[ ] Final and temp provenance obtain independent raw hashes.

[ ] BOM exact legacy provenance is accepted and removable.

[ ] Corrupt/foreign legacy provenance is preserved.

[ ] Modern MainProvenanceHash behavior is unchanged.

[ ] R14 forward hash-owned moves remain intact.

[ ] R13 delete/restore hash-owned operations remain intact.

[ ] Reparse protections remain intact.

[ ] No overwrite:true was introduced for managed asset files.

[ ] No source image is moved/deleted.

[ ] No journal is removed before reconciliation is complete.
```

---

# 60. IF A TEST FAILS — DECISION TREE FOR THE WEAKER MODEL

Use this order.

## 60.1 Compile error in `CloneAssetSessionForRecovery`

Do:

```text
Compare AssetSession.cs property-for-property.

Add any current persisted property that was missed.

Do not remove existing fields from AssetSession.
```

---

## 60.2 R15-001 two-startup test fails because persisted hash is null

Cause is almost certainly:

```text
hydration happened only in transaction memory
or
SaveReplacementJournal was skipped.
```

Check:

```text
durableHashWasMissing is captured BEFORE hydration
and
SaveReplacementJournal executes BEFORE CleanupReplacementBackups.
```

---

## 60.3 R15-001 first startup mutates backups even though upgrade save failed

This is a blocker.

Required call order is wrong.

The code must be:

```text
EnsureOldProvenanceAuthorityIsDurable
IF false -> return
THEN rollback/cleanup
```

Never:

```text
rollback/cleanup
then save upgraded journal
```

---

## 60.4 Second CleanupPending startup fails because OLD backup is missing

Do **not** make missing backups recreate authority.

Check the persisted journal.

It should already contain:

```text
OldSession.ReferenceProvenanceHash = verified SHA-256
```

If not, R15-001 is still unfixed.

---

## 60.5 Cancel provenance byte-race test throws `InvalidDataException`

That is expected.

Assert:

```text
hash changed before move
```

Do not change production to throw a different type.

---

## 60.6 Cancel Reference byte-race test throws `InvalidDataException`

That is expected when provenance restoration succeeds.

Verify:

```text
provenance restored
CancelPhase None
CancellationId null
```

Do not wrap it into IOException merely to satisfy an old test.

---

## 60.7 Legacy Main BOM test fails during Phase-A semantic validation

Check:

```text
MainFilename
MainPrompt
MainProcessedAt
WorkflowMode
ReferenceFilename for ReferenceAssisted mode
```

and make sure the test rendered provenance using exactly those values.

Do not weaken the semantic validator.

---

## 60.8 Legacy Main BOM test passes validation but rollback refuses deletion

Check that:

```text
TryGetExactFinalProvenanceRawHash
```

returned a raw hash and that the same variable is passed to:

```text
TryDeleteHashOwnedFileWithError
```

Do not compute another hash from rendered text.

---

# 61. SUCCESS OUTPUT REQUIRED FROM THE IMPLEMENTING MODEL

When implementation is complete, the weaker model should report exactly:

```text
1. Commit/base SHA used.

2. Files changed.

3. R15-001:
   - clone/alias fix implemented
   - durable pre-mutation hydration save implemented
   - two-startup retry test result
   - failed-upgrade-save/no-mutation test result

4. R15-002:
   - two tests corrected
   - production Cancel behavior unchanged

5. R15-003:
   - RollbackMain now uses verified raw final/temp provenance hashes
   - BOM legacy rollback test result
   - corrupt provenance preservation test result

6. Targeted R15 tests:
   PASS/FAIL with exact counts.

7. RecoveryCritical:
   PASS/FAIL with exact counts.

8. Debug build/test:
   PASS/FAIL.

9. Release build/test:
   PASS/FAIL.

10. 20x Release:
    PASS count, must be 20/20.

11. Publish:
    PASS/FAIL.

12. Smoke:
    PASS/FAIL.

13. Coverage:
    PASS/FAIL / produced artifact path if available.

14. Any unavailable exact environment check:
    explicitly marked DEFERRED, not silently omitted.
```

Do not claim:

```text
production-ready
zero defects
final acceptance
```

unless the full available validation passes and a subsequent independent audit also finds no source-level defects.

---

# 62. COPY-READY IMPLEMENTER PROMPT

Use the following prompt with the weaker coding model together with this `bugs15.md`:

```text
ROLE
You are the implementation model repairing the final known defects in
Ceegore/gpt_provenance_document_helper.

AUTHORITY
Use bugs15.md as the exact repair specification.
The implementation-grade handoff begins at section 27.
Do not reinterpret or simplify its safety model.

BASELINE
Start from main at:
5e9a8ebf7119ad683b717d0df98f2a7db346321d
or a descendant containing exactly those fixes.

SCOPE
Implement only R15-001, R15-002, and R15-003 plus their required tests.

MANDATORY EXECUTION ORDER
1. Read bugs15.md sections 27-62 completely.
2. Inspect the current source before editing.
3. Implement PATCH A exactly.
4. Add PATCH A tests.
5. Implement PATCH B by changing only the two incorrect tests.
6. Implement PATCH C exactly.
7. Add PATCH C tests.
8. Build.
9. Run targeted R15 tests.
10. Run all RecoveryCritical tests.
11. Run full Debug and Release tests.
12. Run 20x Release tests.
13. Run self-contained publish, smoke, and coverage when the environment supports them.
14. Re-read the modified code against the section-59 checklist.
15. Report exact results.

CRITICAL SAFETY RULES
- Durable journal before destructive mutation.
- Exact hash ownership at mutation boundary.
- Exact provenance ownership.
- Preserve unknown/external modified files.
- Reject unsafe reparse destination hierarchy.
- No overwrite of managed canonical asset files.
- Never move/delete the selected source image.
- Do not weaken working R13/R14 safety primitives.
- Do not change production Cancel behavior merely to satisfy bad tests.

R15-001 REQUIRED RESULT
A legacy null-hash CleanupPending journal must be upgraded on disk before
backup cleanup. If journal deletion then fails, the next startup must
still complete cleanup successfully even though OLD backups are already gone.

R15-002 REQUIRED RESULT
The two Cancel byte-race tests must assert the actual final
mutation-boundary InvalidDataException/hash-changed behavior.
Production Cancel logic remains unchanged.

R15-003 REQUIRED RESULT
RollbackMain must use raw hashes returned by
TryGetExactFinalProvenanceRawHash for legacy final/temp provenance.
An exact UTF-8-BOM legacy provenance must roll back successfully.
Foreign provenance must remain preserved.

NO SHORTCUTS
Do not replace hash ownership with text equality.
Do not ignore errors.
Do not delete journals early.
Do not broaden scope.

STOP CONDITION
You are finished only when all available required tests pass.
If the exact Windows/.NET environment is unavailable, complete all
possible static/source work and clearly mark only those runtime checks
as DEFERRED. Do not treat unavailable tooling alone as a source blocker.

OUTPUT
Return:
- files changed
- concise explanation of each R15 fix
- exact test/build counts
- any deferred checks
- no unsupported claim of final acceptance
```

---

# 63. FINAL NOTE TO THE IMPLEMENTER

The remaining work is **not** another architectural redesign.

The intended end-state is already clear:

```text
legacy semantic proof
    -> exact raw hash from same snapshot

missing durable transaction authority
    -> persist authority
    -> only then mutate

every managed file mutation
    -> current path safety
    -> current byte authority
    -> no-overwrite mutation
```

Implement the three remaining repairs exactly as specified.

Do not reopen solved issues.


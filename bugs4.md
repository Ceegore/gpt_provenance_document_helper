# AI Asset Provenance Helper — Fourth Paranoid Retest

**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `e3ef7aa9345b7daa96de51be5f2d9a1b2cf3c3f0`  
**Implementation parent:** `43c3f5745d348d0aae4efacfda3ef4886b9e7071`  
**Previous audit:** `bugs3.md`  
**Authority:** `_changePlan2.md`

## Verdict

**FAIL — the R3 repair is largely successful, but remaining known defects still prevent zero-defect acceptance.**

This is the strongest implementation version audited so far. The central replacement recovery redesign landed correctly: startup now chooses rollback vs commit-forward, uses the processor's ownership-checked rollback/cleanup methods, distinguishes same-filename OLD/NEW References by hashes/provenance/timestamp, keeps `CleanupPending` until cleanup succeeds, exact-validates stable Reference recovery, preflights Main collisions before journaling, and fixes the Main session-delete double-rollback path. The new tests also materialize several real crash states rather than merely switching enum values.

The remaining defects are narrower. They concern authority drift between Prepared and mutation, live-operation finalization failures, overlapping durable transactions, and a few missing failure-state tests.

## R3 status

| R3 item | Status |
|---|---|
| R3-001 partial/new promotion rollback | FIXED materially |
| R3-002 same-filename authority | FIXED materially |
| R3-003 cleanup journal ordering | FIXED |
| R3-004 prepared Reference exception reconciliation | FIXED |
| R3-005 direct recovery deletes | FIXED materially |
| R3-006 directories-only prepared crash | FIXED |
| R3-007 Main collision preflight | FIXED baseline |
| R3-008 Main delete-failure double rollback | FIXED baseline |
| R3-009 stable exact Reference recovery | FIXED |
| R3-010 persisted-validator robustness | FIXED materially |
| R3-011 unsafe public replacement convenience | FIXED |
| R3-012 real crash-state tests | PARTIAL |
| R3-013 Main provenance TOCTOU | FIXED materially |
| R3-014 caller extension plumbing | FIXED |

## Remaining findings

| ID | Severity | Summary |
|---|---|---|
| R4-001 | **HIGH** | Replacement source/provenance is not checked against the durable Prepared authority before temp creation/backing up OLD |
| R4-002 | **HIGH** | OLD Reference provenance is not exact-validated before replacement journal/temp work begins |
| R4-003 | **HIGH** | Live replacement outer catch swallows durable OLD-session/journal finalization failures after rollback |
| R4-004 | **HIGH** | Replacement OLD/NEW authority comparison ignores another active Main/cancel/Reference transaction |
| R4-005 | **MEDIUM** | Main destination preflight can throw from `Directory.EnumerateFiles()` |
| R4-006 | **MEDIUM** | Public mutation services still cannot prove that the prepared transaction was actually persisted |
| R4-007 | **MEDIUM** | RecoveryCritical matrix remains incomplete; one authority test mutates the same OldSession object it intends to contrast |
| R4-008 | LOW-MEDIUM | Replacement rollback post-check still uses weak `ValidateReferenceOutput()` |
| R4-009 | LOW | Commit-forward recovery re-loads `session.json` without a local failure boundary |
| R4-010 | LOW/policy | Arbitrary configured extensions bypass format-signature checks |

---

# R4-001 — HIGH — Prepared replacement authority can drift

`CreateReferenceReplacementTransaction()` computes durable authority:

```text
NewSession.ReferenceHash
NewSession.ReferenceProvenanceHash
```

and `MainForm` then saves phase `Prepared`.

But `CreateReplacementTempFiles()` currently computes the *current* source hash, copies the current source, verifies only that the copy equals that current source, then renders provenance from the current template. It does not require those bytes to equal the already-persisted `NewSession` hashes.

### Failure sequence

```text
T0 create transaction -> ReferenceHash = H1
T1 save Prepared
T2 source changes -> H2
T3 CreateReplacementTempFiles accepts H2 because source H2 == temp H2
T4 OLD is backed up
T5 H2 is promoted
T6 exact NewSession validation expects H1 -> FAIL
T7 rollback refuses to delete H2 because H2 is not NewSession-owned
```

A simple pre-mutation source change becomes a post-promotion recovery dead end.

Template drift has the same shape: Prepared provenance hash P1, current template renders P2, P2 is promoted, exact validation fails, rollback correctly refuses to delete unknown P2.

### Required fix

Before copy:

```csharp
var sourceHash =
    ComputeSha256(transaction.NewSession.ReferenceSourcePath);

if (!string.Equals(
        sourceHash,
        transaction.NewSession.ReferenceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new IOException(
        "Replacement Reference source changed after the Prepared transaction was created.");
}
```

After copy:

```csharp
var tempHash =
    ComputeSha256(transaction.TempNewReferencePath);

if (!string.Equals(
        tempHash,
        transaction.NewSession.ReferenceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new IOException(
        "Replacement temp Reference does not match Prepared ReferenceHash.");
}
```

Before provenance write:

```csharp
var provenance = _templateService.RenderReference(
    transaction.NewSession.ReferenceFilename,
    transaction.NewSession.ProjectName,
    transaction.NewSession.ReferenceProcessedAt
        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

var provenanceHash = Convert.ToHexString(
    SHA256.HashData(
        new UTF8Encoding(false).GetBytes(provenance)))
    .ToLowerInvariant();

if (!string.Equals(
        provenanceHash,
        transaction.NewSession.ReferenceProvenanceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException(
        "Replacement provenance changed after the Prepared transaction was created.");
}
```

Only after both temp files match durable authority may `OldBackupPending` be persisted.

### Required tests

```text
Replacement_Prepared_SourceChangesBeforeTempCopy_NoOldMutation
Replacement_Prepared_TemplateChangesBeforeTempProvenance_NoOldMutation
Replacement_Prepared_TempReferenceAuthorityMismatch_NoOldMutation
Replacement_Prepared_TempProvenanceAuthorityMismatch_NoOldMutation
```

---

# R4-002 — HIGH — OLD Reference exact integrity is checked too late

Replacement transaction creation currently calls:

```csharp
ValidateSession(oldSession)
```

but not:

```csharp
ValidateExactReferenceOutput(oldSession, _templateService)
```

`ValidateSession()` proves the Reference image hash and structure, but does not exact-verify Reference provenance. The weaker provenance check is substring-based.

Therefore an externally appended/modified old provenance file can pass transaction creation. The app then saves `Prepared`, creates NEW temps, saves `OldBackupPending`, and only in `BackupOldReference()` discovers that OLD provenance is untrusted. Rollback then cannot prove the canonical OLD provenance belongs to the session and fails closed, leaving journal/temps and forcing manual recovery.

This should have been a simple preflight rejection.

### Required fix

In `CreateReferenceReplacementTransaction()`:

```csharp
var exactOld =
    _validationService.ValidateExactReferenceOutput(
        oldSession,
        _templateService);

if (!exactOld.IsValid)
{
    throw new InvalidDataException(
        "Current Reference output is inconsistent or modified and cannot be replaced."
        + Environment.NewLine
        + string.Join(Environment.NewLine, exactOld.Errors));
}
```

Expected behavior:

```text
tampered OLD Reference -> reject before replacement journal
no temps
no backups
no canonical mutation
app remains usable
```

### Required tests

```text
OldReferenceProvenanceAppended_ReplacementNeverJournals
OldReferenceImageTampered_ReplacementNeverJournals
```

---

# R4-003 — HIGH — live replacement finalization failures are swallowed

The outer catch in `HandleReplaceReference()` still contains:

```csharp
if (rollback.IsValid)
{
    try
    {
        _sessionService.Save(transaction.OldSession);
        _sessionService.DeleteReplacementJournal();
    }
    catch
    {
        // swallowed
    }
}
```

This is unsafe.

### Dangerous example

A failure occurs while saving the `SessionSwitched` replacement phase. At that point:

```text
files = NEW
session.json = NEW
journal = SessionSwitchPending
OLD backups exist
```

Outer catch successfully rolls files back to OLD. Then `Save(OldSession)` fails and the failure is swallowed:

```text
files = OLD
session.json = NEW
journal = SessionSwitchPending
in-memory session = OLD
form remains usable
```

Startup later classifies `session.json` as NEW authority and tries commit-forward, but files are OLD and rollback backups may already be consumed.

### Required rule

After a successful filesystem rollback, the app may continue only if all of these succeed:

```text
Save OLD stable session
Delete replacement journal
set in-memory OLD session/state
```

Otherwise preserve remaining authority and **close**.

Suggested helper:

```csharp
private bool FinalizeLiveReplacementRollback(
    ReferenceReplacementTransaction tx)
{
    try
    {
        _sessionService.Save(tx.OldSession);
    }
    catch (Exception ex)
    {
        ShowError(
            "CRITICAL: Replacement files were rolled back, but the OLD session could not be persisted.",
            ex);
        Close();
        return false;
    }

    try
    {
        _sessionService.DeleteReplacementJournal();
    }
    catch (Exception ex)
    {
        ShowError(
            "CRITICAL: OLD state was restored, but the replacement journal could not be removed.",
            ex);
        Close();
        return false;
    }

    _currentSession = tx.OldSession;
    _state = UiState.ReferenceReady;
    ApplyState();
    return true;
}
```

Also: if cleanup succeeded (`transaction.IsCommitted == true`) but deleting the replacement journal fails, do not simply show an error while leaving `_currentSession` stale. Close, or reload/prove NEW authority before continuing.

### Required tests

```text
SessionSwitchedPhaseSaveFails_OldSessionSaveFails_Closes
RollbackSucceeds_JournalDeleteFails_Closes
CleanupSucceeds_JournalDeleteFails_ClosesOrReloadsNew
NoReplacementFinalizationExceptionIsSwallowed
```

---

# R4-004 — HIGH — replacement recovery can overwrite another active transaction

`MatchesReferenceAuthority()` now correctly compares stable Reference identity using:

```text
mode
project
root/folder
Reference filename/path
Reference hash
Reference provenance hash
Reference timestamp
```

But it ignores:

```text
IsMainCommitting + Main transaction fields
CancelPhase + CancellationId
ReferenceCommitPhase + ReferenceTransactionId
```

A stale replacement journal can therefore coexist with an active Main journal whose Reference fields still match OLD. Because replacement recovery runs first, it can classify the active Main session as OLD replacement authority and later save `journal.OldSession`, overwriting the active Main journal and orphaning Main transaction files.

### Required stable-authority gate

```csharp
private static bool IsStableReferenceAuthority(
    AssetSession? session)
{
    return session is not null
        && session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted
        && !session.IsMainCommitting
        && session.CancelPhase == CancelPhase.None
        && string.IsNullOrWhiteSpace(session.CancellationId)
        && session.ReferenceCommitPhase == ReferenceCommitPhase.None
        && string.IsNullOrWhiteSpace(session.ReferenceTransactionId);
}
```

Require this for both `actual` and expected OLD/NEW snapshots in `MatchesReferenceAuthority()`.

If a replacement journal and another active transaction coexist:

```text
preserve session.json
preserve replacement journal
preserve asset files
fail closed
close
```

### Required tests

```text
ReplacementJournalPlusActiveMain_FailsClosedPreservesMainJournal
ReplacementJournalPlusCancelPrepared_FailsClosed
ReplacementJournalPlusPreparedReference_FailsClosed
```

---

# R4-005 — MEDIUM — Main preflight enumeration can throw

`ValidateMainDestinationAvailability()` directly enumerates:

```csharp
Directory.EnumerateFiles(
    ingameFolder,
    "*",
    SearchOption.TopDirectoryOnly)
```

without catching filesystem exceptions.

`HandleReferenceAssistedMainImage()` calls this validator outside its later preparation try/catch.

Therefore access/IO failures can escape the UI action.

### Required fix

Convert enumeration failures into validation errors:

```csharp
try
{
    foreach (var path in Directory.EnumerateFiles(
                 ingameFolder,
                 "*",
                 SearchOption.TopDirectoryOnly))
    {
        ...
    }
}
catch (UnauthorizedAccessException ex)
{
    errors.Add("Could not inspect ingame folder: " + ex.Message);
}
catch (IOException ex)
{
    errors.Add("Could not inspect ingame folder: " + ex.Message);
}
```

Prefer an injectable test enumerator instead of manipulating ACLs.

Required tests:

```text
IngameEnumerationUnauthorized_ReturnsValidationFailure
IngameEnumerationIOException_ReturnsValidationFailure
```

---

# R4-006 — MEDIUM — mutation services cannot prove journal durability

The production UI correctly performs:

```text
prepare transaction
save session
mutate files
```

But `ProcessMainImage()` itself only verifies `IsMainCommitting`, and `ProcessReference(preparedSession, ...)` only verifies the prepared phase. Neither can prove the state was actually persisted.

The test helper explicitly does:

```text
PrepareMainCommit()
ProcessMainImage()
```

without a `SessionService.Save()`.

So the service error text says “durably persisted”, but that contract is caller-trusted rather than enforced.

This is primarily architectural, not a currently observed MainForm bug.

Preferred resolution:

1. coordinator owns `SessionService` + processor and performs prepare/save/mutate; or
2. internalize raw mutation methods; or
3. verify the currently persisted session matches the transaction before first mutation.

Tests modeling production should persist between prepare and mutate.

---

# R4-007 — MEDIUM — RecoveryCritical is better, but incomplete

Real phase states are now tested for several important boundaries. That is a major improvement.

Still missing explicit first-class cases include:

```text
Prepared / temp Reference only
OldBackupPending / no move
OldBackupPending / old Reference moved only
OldBackupPending / both OLD moved
NewPromotionPending / no promote
NewPromotionPending / both NEW promoted
SessionSwitchPending / OLD session / different filename
SessionSwitchPending / OLD session / same filename
SessionSwitched / NEW session
CleanupPending / one backup already deleted
```

Also still missing:

```text
source changes after Prepared
template changes after Prepared
OLD exact provenance tampered before replacement
Save(OldSession) fails after successful live rollback
replacement journal deletion fails after live rollback
replacement journal coexists with active Main/cancel
Main ingame enumeration throws
```

### Test aliasing defect

A same-filename/tampered-authority test currently does:

```csharp
var tamperedSession = session;
tamperedSession.ReferenceHash = new string('f', 64);
```

But `tx.OldSession` points at the same `session` object. This changes the journal's OLD authority too.

Deep-clone before tampering:

```csharp
var persistedTampered =
    JsonSerializer.Deserialize<AssetSession>(
        JsonSerializer.Serialize(tx.OldSession))!;

persistedTampered.ReferenceHash =
    new string('f', 64);

sessionService.Save(persistedTampered);
```

Then the journal retains the original OLD authority.

---

# R4-008 — LOW-MEDIUM — exact post-rollback assertion

After `RollbackReferenceReplacement()` restores OLD state, it currently finishes with:

```csharp
ValidateReferenceOutput(transaction.OldSession)
```

Use the stronger final invariant:

```csharp
ValidateExactReferenceOutput(
    transaction.OldSession,
    _templateService)
```

Earlier verification is already strong, so this is a small hardening change, but it aligns the final assertion with the cryptographic recovery model.

---

# R4-009 — LOW — second session load can escape recovery boundary

`RecoverReferenceReplacementJournalIfPresent()` loads current `session.json` safely. `FinishReplacementCommit()` then loads it again without its own try/catch.

If external modification occurs between reads, the second load can escape the intended `FailReplacementRecovery()` flow.

Prefer passing the already-read session into `FinishReplacementCommit()` or wrap the second load.

---

# R4-010 — LOW / explicit policy — custom extensions

Defaults are:

```text
.png
.webp
.jpg
.jpeg
```

and those receive magic-byte validation.

Unknown configured extensions pass `HasValidMagicBytes()` automatically.

If custom formats are deliberately supported, this is a policy choice and needs a format-validator contract.

If v1.1 only supports PNG/WebP/JPEG, constrain `AcceptedExtensions` to those formats. That is the simplest zero-defect policy.

---

# Checks that passed in this retest

The fourth pass found no new regression in:

```text
Project input removal
optional Download Folder for manual selection
Reference/Main selection separation
NoReference journal-before-write
root Main original filename
ingame deterministic name + extension
ingame variant collision logic
Reference/Main provenance hash ownership
template-change completion recovery
Main/ingame rollback ownership
reparse-point managed-folder protection
prepared Reference startup ordering
same-filename replacement OLD/NEW distinction
CleanupPending retention
stable exact Reference recovery
Drop file here controls
Prompt/Main validation independence
compact form dimensions from prior fix
mandatory icon smoke
RecoveryCritical CI filter
```

Do not reopen these in the next repair without a failing test.

---

# Repair order

1. **R4-001 + R4-002:** freeze NEW authority and exact-preflight OLD before any risky replacement work.
2. **R4-003:** remove every swallowed live replacement finalization error.
3. **R4-004:** reject overlapping transaction authorities.
4. **R4-005:** contain Main preflight I/O errors.
5. **R4-006:** resolve raw mutating-service durability boundary.
6. **R4-007:** add the remaining real-state/failure-state tests.
7. **R4-008 + R4-009:** small recovery hardening.
8. **R4-010:** explicitly choose supported-format policy.

---

# Required final gate after repairs

```powershell
dotnet --info
dotnet tool restore
dotnet restore AssetProvenanceHelper.sln

dotnet build AssetProvenanceHelper.sln -c Debug --no-restore -warnaserror
dotnet test AssetProvenanceHelper.sln -c Debug --no-build

dotnet build AssetProvenanceHelper.sln -c Release --no-restore -warnaserror
dotnet test AssetProvenanceHelper.sln -c Release --no-build

dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical" `
  --logger "console;verbosity=detailed"
```

Then 20x:

```powershell
for ($i = 1; $i -le 20; $i++) {
    dotnet test AssetProvenanceHelper.sln `
      -c Release `
      --no-build `
      --logger "console;verbosity=minimal"

    if ($LASTEXITCODE -ne 0) {
        throw "Flakiness run $i failed."
    }
}
```

Publish/smoke:

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

Acceptance:

```text
Debug PASS
Release PASS
RecoveryCritical PASS
20/20 PASS
publish PASS
smoke PASS
coverage PASS
manual DPI matrix PASS
```

---

# Definition of Done

```text
[ ] OLD Reference exact before replacement Prepared journal
[ ] NEW source hash locked to Prepared ReferenceHash
[ ] NEW provenance locked to Prepared provenance hash
[ ] no OLD backup before both NEW temps match Prepared authority
[ ] no swallowed replacement rollback-finalization failures
[ ] journal deletion failure cannot leave a usable inconsistent form
[ ] replacement recovery rejects active Main/cancel/Reference transaction overlap
[ ] Main destination enumeration failures become normal validation failures
[ ] real phase matrix complete
[ ] live failure-finalization matrix complete
[ ] no test aliases accidentally mutate journal authority
[ ] Debug/Release/RecoveryCritical/20x all pass
[ ] publish/smoke/coverage pass
```

## Final conclusion

The R3 repair was successful in its main architectural goal. Another replacement-state-machine rewrite is **not** warranted.

The remaining work is a focused authority/finalization hardening pass. Once R4-001 through R4-007 are fixed and the corresponding real-state tests pass, another paranoid retest should have a realistic chance of reaching a clean static/structural verdict.

**Current acceptance state: FAIL — remaining known defects exist.**

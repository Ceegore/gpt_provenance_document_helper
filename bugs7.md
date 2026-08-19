# AI Asset Provenance Helper — Seventh Paranoid Retest & Repair Guide

**File:** `bugs7.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `1845bb96f4f443d7c7f5b6f6418039579a5333a5`  
**Previous audited commit:** `f1271f993986bb26eafe61339ffc3a66765df3bd`  
**Previous audit:** `bugs6.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — the R6 UI-boundary work is substantially correct, but initial Reference creation is still not fully crash-safe/authority-frozen, and Cancel still has a post-commit UI-state boundary defect.**

This revision is again materially stronger.

The commit:

```text
1845bb96f4f443d7c7f5b6f6418039579a5333a5
fix(r6): freeze initial reference prepared authority and isolate post-commit UI boundaries
```

does implement important R6 repairs:

- initial Reference now verifies the selected source path against the Prepared session;
- initial Reference now verifies the current source hash against `ReferenceHash` before folder creation;
- initial Reference now verifies the currently rendered provenance hash against `ReferenceProvenanceHash` before folder creation;
- Main successful completion now ends the rollback-capable transaction scope before post-commit UI work;
- initial Reference stable-session save now ends the rollback-capable scope before post-commit UI work;
- Reference replacement success UI was also moved outside the transaction catch;
- NoReference no longer abandons a durable active journal merely because the status-line update fails;
- the R5 rollback-count test now actually counts `RollbackReferenceReplacement()` calls.

Those fixes should remain.

However, R6-001 is only **partially** closed.

The new preflight proves authority at one point in time, but `ProcessReference()` still:

```text
copies the source directly to the canonical Reference destination
re-renders provenance later instead of reusing the verified string
writes provenance after that second render
```

This leaves a time-of-check/time-of-use and hard-crash window that can still produce canonical files not provably owned by the durable Prepared journal.

The fresh audit also found that interactive cancellation still mixes its durable commit with UI/status work.

---

# 0.2 Current repository state

Current `main` at the end of this audit:

```text
1845bb96f4f443d7c7f5b6f6418039579a5333a5
```

Parent:

```text
f1271f993986bb26eafe61339ffc3a66765df3bd
```

Files changed by the R6 repair:

```text
bugs6.md
src/AssetProvenanceHelper/MainForm.MainWorkflow.cs
src/AssetProvenanceHelper/MainForm.ReferenceWorkflow.cs
src/AssetProvenanceHelper/MainForm.cs
src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
src/AssetProvenanceHelper/Services/AssetProcessorService.cs
tests/AssetProvenanceHelper.Tests/Bugs3ParanoidTests.cs
```

---

# 0.3 Execution evidence

The connected GitHub combined-status surface exposes no legacy commit statuses for this SHA.

The available connector workflow-run helper is limited to pull-request-triggered runs and therefore does not expose the direct `main` push run.

The current analysis environment has neither:

```text
dotnet
pwsh
```

installed.

Therefore:

- Windows/.NET execution remains a deferred verification gate;
- that limitation is **not** a blocker by itself;
- the FAIL verdict below is based on concrete source-level findings.

---

# 1. Retest of `bugs6.md`

| R6 item | Status | Seventh-pass result |
|---|---|---|
| R6-001 initial Reference Prepared authority | **PARTIAL** | preflight added, but canonical copy/re-render TOCTOU and crash window remain |
| R6-002 Main post-commit UI rollback | **FIXED** | Main durable success now exits rollback-capable scope before UI |
| R6-002 initial Reference post-stable UI rollback | **FIXED** | stable session save is now the durable boundary; UI failure preserves asset/session |
| R6-002 NoReference post-journal UI abandonment | **FIXED in source** | status update is best-effort and commit continues |
| R6-003 rollback-count test | **FIXED** | explicit rollback invocation hook + `Assert.Equal(1, rollbackCount)` |

The R6 fixes above should not be reopened.

---

# 2. Current defect summary

| ID | Severity | Area | Summary |
|---|---|---|---|
| R7-001 | **HIGH** | initial Reference crash/authority atomicity | Prepared authority is checked before mutation, but source is still copied directly to canonical output and provenance is re-rendered after preflight |
| R7-002 | **MEDIUM** | Cancel durable/UI boundary | successful `SessionService.Cancel()` is followed by throwable UI/status work before in-memory state is safely detached |
| R7-003 | **MEDIUM-LOW** | initial Reference path TOCTOU | destination reparse safety is checked before lazy folder creation but not revalidated after the created folders exist |
| R7-004 | **LOW** | tests | timestamp-offset, mid-operation drift, NoReference UI-failure, and replacement post-commit paths are not fully proven |

---

# 3. R7-001 — HIGH — initial Reference still writes canonical output before the Prepared transaction is unambiguously proven

## 3.1 What is fixed

The new helper:

```csharp
RequirePreparedReferenceAuthority(...)
```

correctly verifies before folder creation:

```text
ReferenceCommitPhase == Prepared
source path matches ReferenceSourcePath
provided processedAt compares to ReferenceProcessedAt
image validates
current source hash == ReferenceHash
current rendered provenance hash == ReferenceProvenanceHash
```

This fixes the simple drift case:

```text
save H1/P1
source changes to H2 before ProcessReference starts
```

and:

```text
save H1/P1
template changes to P2 before ProcessReference starts
```

The new tests correctly cover those simple pre-call cases.

## 3.2 What remains

After this preflight succeeds, the method currently does approximately:

```text
Directory.CreateDirectory(asset)
Directory.CreateDirectory(reference)

File.Copy(source, CANONICAL_REFERENCE)

validate canonical image
hash canonical image == session.ReferenceHash

RenderReference(...) AGAIN
WriteTextAtomic(CANONICAL_PROVENANCE, newly rendered text)

ValidateExactReferenceOutput()
```

The preflight-rendered provenance string is discarded.

The source file is also reopened by `File.Copy()` after its preflight hash was calculated.

That means the durable Prepared authority is not the only byte source controlling mutation.

---

# 4. R7-001A — hard crash during direct canonical image copy

The most important remaining issue does not even require template drift.

Prepared authority:

```text
ReferenceHash = H1
ReferenceProvenanceHash = P1
ReferenceCommitPhase = Prepared
session.json durably saved
```

`ProcessReference()` then executes:

```csharp
CopyFileWithoutOverwrite(
    actualSourcePath,
    referenceDestination);
```

where:

```text
referenceDestination = the FINAL canonical Reference path
```

`File.Copy` is not a transactional/atomic promotion from the application's perspective.

A hard process termination, machine shutdown, storage error, or interrupted copy can leave:

```text
canonical reference file exists
canonical reference bytes != H1
```

before this line ever runs:

```csharp
var hash = ComputeSha256(referenceDestination);
```

Startup recovery then behaves conservatively:

```text
Prepared journal expects H1
canonical file hash is partial/different
RollbackReference refuses to delete unknown file
journal is preserved
application closes
```

Fail-closed deletion is correct.

The defect is allowing an incompletely copied file to occupy the **canonical managed output path**.

Replacement and Main already use staging/temp paths before canonical promotion.

Initial Reference should follow the same principle.

---

# 5. R7-001B — source can change after the preflight hash but before/during `File.Copy`

The new preflight does:

```text
hash source -> H1
require H1 == session.ReferenceHash
```

Then the source is reopened later by:

```text
File.Copy(source, canonical)
```

An external process can alter the selected image between those operations.

Possible destination:

```text
H2
or a mixed/partial copy
```

Normal non-crash behavior eventually detects:

```text
canonical hash != Prepared H1
```

but the catch can no longer prove that the canonical file belongs to the Prepared transaction and therefore preserves it.

That means even without a hard crash, a sufficiently unlucky source mutation during the copy can force:

```text
incomplete rollback
canonical foreign/partial file preserved
prepared journal preserved
form closes
manual intervention
```

The replacement flow avoids canonical corruption here because drift first lands in a temp path.

Initial Reference should do the same.

---

# 6. R7-001C — the verified provenance string is discarded and the template is read again

`RequirePreparedReferenceAuthority()` renders provenance and checks its hash against:

```text
session.ReferenceProvenanceHash
```

but returns `void`.

Later `ProcessReference()` executes:

```csharp
provenance =
    _templateService.RenderReference(
        referenceFilename,
        session.ProjectName,
        generationDate);
```

again.

`TemplateService.RenderReference()` reads `reference.md` from disk on each call.

Therefore:

```text
preflight render = P1
preflight hash matches Prepared P1
image copy occurs
template changes
second render = P2
P2 is written to canonical provenance
```

If the process stays alive, final exact validation detects this and the local catch can normally remove P2.

But there is a crash window after the atomic provenance move and before exact validation:

```text
canonical image = H1
canonical provenance = P2
session.json expects P1
CRASH
```

Startup exact ownership correctly rejects P2 and refuses destructive cleanup.

Again the system becomes manually stranded even though the state arose from the normal production mutation path.

---

# 7. R7-001D — timestamp equality is not exact

The new code uses:

```csharp
if (processedAt != session.ReferenceProcessedAt)
```

For `DateTimeOffset`, normal equality represents equality of the **instant**, not necessarily equality of the original offset/clock representation.

The project already uses:

```csharp
EqualsExact(...)
```

for Main transaction timestamp authority.

Initial Reference should do the same.

Why it matters:

```text
Prepared:
2026-08-19 23:30 +02:00

caller:
same instant expressed as 2026-08-20 02:30 +05:00
```

Normal `DateTimeOffset` equality can consider these the same instant.

But provenance uses:

```csharp
processedAt.ToString("yyyy-MM-dd")
```

so the generation date can differ:

```text
2026-08-19
vs
2026-08-20
```

The current test uses:

```text
now.AddMinutes(5)
```

which proves only a different instant.

It does not prove offset-exact authority.

---

# 8. Required R7-001 architecture

Do not add a large new replacement-style phase machine.

A simple Prepared + deterministic staging model is enough.

## 8.1 Derive deterministic initial-Reference temp paths

Use the already-durable:

```text
ReferenceTransactionId
```

For example:

```csharp
public string GetReferenceTempImagePath()
{
    if (string.IsNullOrWhiteSpace(ReferenceTransactionId))
    {
        return string.Empty;
    }

    return Path.Combine(
        AssetFolder,
        AppConstants.ReferenceFolderName,
        $".__reference_{ReferenceTransactionId}"
        + Path.GetExtension(ReferenceFilename)
        + ".tmp");
}

public string GetReferenceTempProvenancePath()
{
    if (string.IsNullOrWhiteSpace(ReferenceTransactionId))
    {
        return string.Empty;
    }

    return Path.Combine(
        AssetFolder,
        AppConstants.ReferenceFolderName,
        $".__reference_provenance_{ReferenceTransactionId}.tmp");
}
```

Do not persist redundant path fields if the paths are fully deterministic from existing session authority.

## 8.2 Validate deterministic temp parents

Prepared Reference validation must require:

```text
ReferenceTransactionId valid
temp image exact parent = <asset>/reference
temp provenance exact parent = <asset>/reference
```

No path supplied by external session data may override these derived paths.

## 8.3 Pre-render once

Change authority preflight to return the exact verified provenance bytes/string.

Example:

```csharp
private string RequirePreparedReferenceAuthority(
    AssetSession session,
    AppSettings settings,
    string sourceImagePath,
    DateTimeOffset processedAt)
{
    ...

    if (!processedAt.EqualsExact(
            session.ReferenceProcessedAt))
    {
        throw new InvalidOperationException(
            "Reference processedAt does not exactly match "
            + "the Prepared session authority.");
    }

    ...

    var provenance =
        _templateService.RenderReference(
            session.ReferenceFilename,
            session.ProjectName,
            session.ReferenceProcessedAt.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture));

    var provenanceHash =
        ComputeUtf8Sha256(provenance);

    if (!string.Equals(
            provenanceHash,
            session.ReferenceProvenanceHash,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "Reference provenance changed after "
            + "the Prepared session was persisted.");
    }

    return provenance;
}
```

After this, **never call `RenderReference()` again for this transaction**.

## 8.4 Stage image

Required order:

```text
Prepared session already durable
preflight source + pre-render provenance
create folders
revalidate path/reparse safety
copy source -> deterministic temp image
validate temp image signature
hash temp image == Prepared ReferenceHash
```

No canonical image exists yet.

## 8.5 Stage provenance

Write the already-verified provenance string to:

```text
deterministic temp provenance
```

Then:

```text
hash temp provenance == Prepared ReferenceProvenanceHash
```

No second template read.

## 8.6 Promote only verified temp files

Once both staged artifacts match Prepared authority:

```text
move temp Reference -> canonical Reference
move temp provenance -> canonical provenance
```

with:

```text
overwrite: false
```

Then exact-validate final output.

A crash before promotion cannot corrupt canonical output.

A crash after one promotion is recoverable because the durable Prepared session knows the exact canonical hashes.

---

# 9. Initial Reference recovery after staging

Prepared startup recovery should reason about:

```text
temp image
temp provenance
canonical image
canonical provenance
```

All paths are deterministic from `ReferenceTransactionId`.

Recommended behavior:

### Neither canonical exists

```text
if no temps:
    clean empty tool-created dirs if allowed
    delete Prepared journal

if temps exist:
    verify deterministic paths
    clean only provably transaction-owned temps
    otherwise preserve + fail closed
```

### Canonical image only

If image hash == `ReferenceHash`:

```text
delete exact-owned image
clean exact-owned temps
clean empty dirs
delete journal
```

If not:

```text
preserve
close
```

### Canonical provenance only

If provenance hash == `ReferenceProvenanceHash`:

```text
delete exact-owned provenance
clean temps
delete journal
```

Else fail closed.

### Both canonical exist

If exact H1/P1:

```text
finish commit:
ReferenceCommitPhase = None
ReferenceTransactionId = null
save stable session
```

Else fail closed / rollback only exact-owned artifacts.

The current conservative ownership policy remains intact.

---

# 10. R7-001 mandatory tests

All:

```csharp
[Trait("Category", "RecoveryCritical")]
```

## 10.1 Exact timestamp offset

```csharp
[Fact]
public void
    InitialReference_Prepared_SameInstantDifferentOffset_IsRejected()
{
    var preparedAt =
        new DateTimeOffset(
            2026, 8, 19,
            23, 30, 0,
            TimeSpan.FromHours(2));

    var sameInstantDifferentOffset =
        preparedAt.ToOffset(
            TimeSpan.FromHours(5));

    Assert.True(
        preparedAt == sameInstantDifferentOffset);

    Assert.False(
        preparedAt.EqualsExact(
            sameInstantDifferentOffset));

    // Create + persist Prepared using preparedAt.
    // Call ProcessReference with sameInstantDifferentOffset.
    // Expect failure before folder/temp/canonical mutation.
}
```

## 10.2 Template drift after preflight

Add a narrowly scoped test hook immediately after Prepared authority preflight:

```csharp
[ThreadStatic]
internal static Action?
    OnPreparedReferenceAuthorityVerifiedHook;
```

Test:

```text
Prepared P1
preflight P1 succeeds
hook modifies reference.md -> P2
continue transaction
assert canonical provenance still equals P1
```

With correct implementation, the pre-rendered P1 string is reused.

## 10.3 Source changes after preflight

At the same hook:

```text
Prepared source H1
preflight H1 succeeds
hook changes source -> H2
copy to temp
temp hash != H1
transaction fails
canonical Reference never exists
```

Required assertion:

```text
File.Exists(ReferenceDestinationPath) == false
```

This is stronger than merely detecting H2 after canonical copy.

## 10.4 Interrupted staged copy recovery

Create a Prepared journal plus:

```text
partial deterministic temp image
no canonical image
no canonical provenance
```

Run startup recovery.

Expected result must match the chosen ownership policy:

```text
either safe transaction-temp cleanup + journal removal
or explicit fail-closed preservation
```

But **canonical paths must remain untouched/absent**.

## 10.5 One promoted / one temp

Create:

```text
canonical Reference = exact H1
temp provenance = exact P1
canonical provenance absent
Prepared journal
```

Startup must reconcile deterministically without deleting unknown files.

---

# 11. R7-002 — MEDIUM — successful Cancel is still mixed with throwable UI work

`HandleCancel()` currently does:

```csharp
try
{
    _sessionService.Cancel(_currentSession);

    AddStatus("Current asset session cancelled.");

    _currentSession = null;
    _state = UiState.Idle;

    txtPrompt.Clear();
    ...
    ApplyState();
}
catch (Exception ex)
{
    ShowError(
        "Could not cancel current asset safely.",
        ex);
}
```

This has the same category of durable/UI-boundary problem that R6 correctly fixed for Main and initial Reference.

---

# 12. What `SessionService.Cancel()` means on successful return

On successful completion, `SessionService.Cancel()` has already:

```text
verified exact Reference ownership
renamed via crash-journal phases
deleted exact tool-owned Reference temp image
deleted exact tool-owned provenance temp
attempted tool-created empty-folder cleanup
deleted session.json
```

Therefore return from:

```csharp
_sessionService.Cancel(...)
```

is the **durable cancellation commit point**.

No later UI exception can undo cancellation.

---

# 13. Concrete stale-state failure

Timeline:

```text
T0 current form state = ReferenceReady
   _currentSession = S

T1 SessionService.Cancel(S) succeeds
   reference image deleted
   reference provenance deleted
   session.json deleted

T2 AddStatus("Current asset session cancelled.") throws

T3 HandleCancel catch runs
   ShowError(...)
   form is NOT necessarily closed

T4 _currentSession is still S
   _state is still ReferenceReady
```

The form can remain usable while its in-memory state says:

```text
Reference exists
Cancel is available
Main can be attempted
```

but the actual Reference files/session are gone.

At minimum the next processor validation fails.

At worst later UI flows are operating against an already-cancelled stale session.

The durable cancellation is not lost, but the live application state is inconsistent.

---

# 14. Required R7-002 fix

End the service transaction scope immediately after Cancel returns:

```csharp
private void HandleCancel()
{
    ...

    var cancelledSession = _currentSession;

    try
    {
        _sessionService.Cancel(
            cancelledSession);
    }
    catch (Exception ex)
    {
        ShowError(
            "Could not cancel current asset safely.",
            ex);
        return;
    }

    // DURABLE CANCEL COMMIT POINT.
    // The service outputs/session are gone.
    // Detach authority before any throwable UI/status work.
    _currentSession = null;
    _state = UiState.Idle;

    CompleteCancelUiAfterDurableCommit();
}
```

Then:

```csharp
private void CompleteCancelUiAfterDurableCommit()
{
    try
    {
        AddStatus(
            "Current asset session cancelled.");

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
        ApplyState();
    }
    catch (Exception uiEx)
    {
        try
        {
            ShowMessageBox(
                "The asset session was cancelled successfully, "
                + "but the interface could not be refreshed."
                + Environment.NewLine
                + Environment.NewLine
                + uiEx.Message,
                "Post-Cancel UI Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
        }

        // In-memory authority is already detached.
        // Close rather than leave a partially refreshed UI.
        Close();
    }
}
```

Key invariant:

> after `SessionService.Cancel()` succeeds, `_currentSession` must be detached before any user-interface work that can throw.

---

# 15. R7-002 tests

Add a test hook at the durable boundary:

```csharp
[ThreadStatic]
internal static Action?
    OnCancelDurableCommitHook;
```

Invoke after:

```csharp
_sessionService.Cancel(...)
```

and after:

```text
_currentSession = null
_state = Idle
```

but before normal UI refresh.

Alternatively inject a throwable status/UI provider.

Test:

```text
Cancel_PostCommitUiFailure_DoesNotLeaveReferenceReadySession
```

Assertions:

```text
session.json absent
Reference image absent
Reference provenance absent
_currentSession == null
_state == Idle
form is closed OR UI is successfully Idle
```

Also add:

```text
Cancel_PostCommitUiFailure_DoesNotRecreateSession
```

---

# 16. R7-003 — MEDIUM-LOW — initial Reference destination reparse safety is not rechecked after lazy folder creation

Before mutation, current code correctly calls:

```csharp
ValidateSessionPathsForDestructiveOperation(session)
```

If the asset/reference folders already exist, their reparse attributes are checked.

However, for the normal new-asset case they do not yet exist.

Then:

```csharp
Directory.CreateDirectory(assetFolder);
Directory.CreateDirectory(referenceFolder);

CopyFileWithoutOverwrite(...);
```

There is no post-create path validation before the copy.

This leaves a narrow destination TOCTOU window where another process could replace:

```text
<asset>
or
<asset>/reference
```

with a junction/symlink between validation/creation and mutation.

This is not a normal benign-user scenario, so severity is below the transaction defects.

But it is inconsistent with the repository's explicit conservative reparse-point safety model.

## Required hardening

After folder creation and before writing staged files:

```csharp
Directory.CreateDirectory(assetFolder);

if (ValidationService.IsReparsePoint(assetFolder))
{
    throw new IOException(
        "Asset folder became a reparse point.");
}

Directory.CreateDirectory(referenceFolder);

var postCreatePaths =
    ValidationService
        .ValidateSessionPathsForDestructiveOperation(
            session);

if (!postCreatePaths.IsValid)
{
    throw new InvalidDataException(
        string.Join(
            Environment.NewLine,
            postCreatePaths.Errors));
}
```

Revalidate again immediately before canonical promotion if staging performs meaningful work between folder creation and promotion.

Use the existing `FileAttributesProvider` test hook to simulate the folder becoming a reparse point after initial preflight.

---

# 17. R7-004 — LOW — test coverage still overstates a few guarantees

These do not independently block the product once source fixes are made, but repair them in the same round.

## 17.1 Timestamp test misses same-instant/different-offset case

Current:

```text
prepared at now
call at now.AddMinutes(5)
```

Add `ToOffset()` test described above.

## 17.2 Source/template drift tests only mutate before `ProcessReference()`

They prove:

```text
pre-call drift is rejected
```

They do not prove:

```text
post-preflight source drift cannot reach canonical
post-preflight template drift cannot change canonical provenance
```

Add the authority-verified hook.

## 17.3 NoReference post-save UI test does not inject a post-save UI failure

The current test named:

```text
NoReference_JournalSaved_PostSaveUiFailure_DoesNotLeaveUsableUntrackedTransaction
```

sets a no-op `MessageBoxProvider` and exercises successful normal completion.

It does not make:

```csharp
AddStatus("No-reference Main session saved.")
```

throw.

The source implementation is already safe because that AddStatus call is in a best-effort catch.

But the test name claims stronger evidence than it supplies.

Either:

```text
rename the test to describe normal completion
```

or add a real post-save status hook/failure injection.

## 17.4 Add replacement post-commit UI regression test

The R6 refactor also moved replacement UI outside the transaction catch.

Add:

```text
Replacement_PostCommitUiFailure_DoesNotRollbackOrRecreateOldReference
```

Assertions:

```text
replacement journal absent
session.json == NEW stable session
NEW Reference image exists
NEW exact provenance exists
OLD backups absent
OLD Reference is not restored
```

This locks in the new structure.

---

# 18. R6 fixes that are confirmed good

Do not reopen these without a failing test.

## Main successful commit boundary

Current shape is now correct:

```text
try:
    ProcessMainImage
    Delete session
catch:
    rollback/reconcile

---------------- durable commit ----------------

CompleteMainUiAfterDurableCommit
```

The post-commit UI helper catches UI failures and never invokes Main rollback.

**PASS.**

## Initial Reference stable-session boundary

Current shape is now:

```text
try:
    Create Prepared
    Save Prepared
    Process Reference
    Save stable completed session
catch:
    rollback prepared transaction

---------------- durable commit ----------------

CompleteReferenceUiAfterDurableCommit
```

A hook-induced UI error leaves the stable Reference/session intact.

**PASS.**

## Replacement success UI boundary

The replacement journal is deleted before:

```text
CompleteReplacementUiAfterDurableCommit
```

and post-commit UI errors do not invoke rollback.

**PASS in source.**

## NoReference post-journal status behavior

After the NoReference active session is durably saved:

```csharp
try
{
    AddStatus(...);
}
catch
{
    // best effort
}

ExecuteMainCommit(...);
```

The active durable journal is therefore not abandoned merely because a status update fails.

**PASS in source.**

## R5 rollback count

The rollback hook is invoked in:

```text
RollbackReferenceReplacement
```

and the test now asserts:

```text
rollbackCount == 1
```

**PASS.**

---

# 19. Recommended repair order

## Phase 1 — finish initial Reference transaction correctness

Fix R7-001 first:

```text
exact timestamp authority
pre-render provenance once
deterministic temp image
deterministic temp provenance
validate both temps
atomic promote verified temps
Prepared recovery understands deterministic temps
```

This is the only HIGH item.

## Phase 2 — Cancel UI boundary

Fix R7-002:

```text
SessionService.Cancel
-> durable commit
-> immediately detach _currentSession/state
-> UI refresh in separate guarded helper
```

## Phase 3 — destination reparse hardening

Fix R7-003 during the same Reference staging refactor.

## Phase 4 — tests

Fix R7-004.

---

# 20. Static verification after repair

## Initial Reference direct canonical copy must be gone

```powershell
rg -n "CopyFileWithoutOverwrite\(.*referenceDestination|File\.Copy\(.*referenceDestination" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Expected:

```text
0 direct source -> canonical initial Reference copy paths
```

The source should go to a transaction temp first.

---

## No duplicate Reference render after authority preflight

```powershell
rg -n "RenderReference" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Inspect initial Reference path.

Required:

```text
one Prepared-authority render result is reused
```

Replacement has its own legitimate render.

---

## Exact timestamp

```powershell
rg -n "processedAt.*ReferenceProcessedAt|ReferenceProcessedAt.*processedAt" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Required:

```text
EqualsExact
```

Do not rely on normal `DateTimeOffset ==` / `!=`.

---

## Cancel boundary

```powershell
rg -n "SessionService\.Cancel|_sessionService\.Cancel|AddStatus|_currentSession = null" `
  src/AssetProvenanceHelper/MainForm.ReferenceWorkflow.cs
```

Manual order:

```text
Cancel service succeeds
_currentSession = null
_state = Idle
then UI/status helper
```

---

# 21. Required Windows execution gate

After the source repair:

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

## Flakiness

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

# 22. Final acceptance checklist

## Initial Reference

```text
[ ] Prepared journal durable before Reference output mutation
[ ] exact timestamp uses EqualsExact
[ ] source path is Prepared authority
[ ] source hash is Prepared authority
[ ] provenance is rendered once and hash-verified before mutation
[ ] source copies to deterministic transaction temp, not canonical
[ ] temp image validates and hashes to ReferenceHash
[ ] pre-rendered provenance writes to deterministic temp
[ ] temp provenance hashes to ReferenceProvenanceHash
[ ] destination folders revalidated after creation
[ ] canonical image appears only via verified temp promotion
[ ] canonical provenance appears only via verified temp promotion
[ ] Prepared recovery handles temp/canonical combinations
[ ] crash during image staging cannot leave partial canonical image
[ ] template change after preflight cannot change canonical provenance
```

## Durable/UI boundaries

```text
[ ] Main successful commit boundary remains isolated
[ ] Reference stable-save boundary remains isolated
[ ] Replacement successful commit boundary remains isolated
[ ] NoReference saved-journal status remains best-effort
[ ] Cancel service completion immediately detaches current in-memory session
[ ] Cancel UI failure cannot leave ReferenceReady stale authority
```

## Tests

```text
[ ] same-instant/different-offset Reference timestamp test
[ ] source changes after authority preflight test
[ ] template changes after authority preflight test
[ ] interrupted staged Reference-copy recovery test
[ ] one-promoted/one-temp Prepared Reference recovery test
[ ] Cancel post-commit UI failure test
[ ] NoReference test either injects real UI failure or is renamed
[ ] replacement post-commit UI failure test
```

## Execution

```text
[ ] Debug build warnings-as-errors PASS
[ ] Debug tests PASS
[ ] Release build warnings-as-errors PASS
[ ] Release tests PASS
[ ] RecoveryCritical PASS
[ ] 20/20 full Release PASS
[ ] self-contained win-x64 publish PASS
[ ] smoke PASS
[ ] coverage PASS
```

---

# 23. Final seventh-pass conclusion

The R6 commit is a genuine improvement.

**R6-002 is fixed for Main and initial Reference. R6-003 is fixed.**

However, R6-001 is not fully closed because initial Reference still uses a preflight-then-direct-canonical-copy design.

The most important remaining repair is:

```text
Prepared authority
-> deterministic staging
-> verify staged bytes
-> atomic canonical promotion
```

rather than:

```text
Prepared authority preflight
-> direct canonical copy
-> verify afterward
```

The separate interactive Cancel UI/state boundary should also be corrected.

After those focused fixes, run another full paranoid pass.

**Current acceptance state: FAIL — one HIGH transaction-atomicity issue and one MEDIUM cancellation-state issue remain.**

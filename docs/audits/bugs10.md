# AI Asset Provenance Helper — Tenth Paranoid Retest & Repair Guide

**File:** `bugs10.md`  
**Audit date:** 2026-08-19  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `859482fd08e0fdec5c6cd99fcc20ce181f042a5b`  
**Previous audited commit:** `452f06621d8014efe050a058422bc40aa98f6870`  
**Previous audit:** `bugs9.md`  
**Authority:** `_changePlan2.md`

---

# 0. Executive verdict

## 0.1 Result

**FAIL — all R9 items are materially implemented, but one important destructive-path rollback defect and two narrower hardening/test defects remain.**

This is the strongest revision audited so far.

The new `main` commit genuinely implements the `bugs9.md` requests:

- Main provenance now uses deterministic durable reserved-path staging;
- Main performs a final raw SHA-256 gate over temp Main, temp ingame, and temp provenance before canonical promotion;
- Main re-runs session path/reparse validation at that final gate;
- initial Reference performs final raw SHA-256 checks over both staging files;
- constructor-time status is no longer suppressed by `!IsHandleCreated`;
- NoReference post-journal UI failure is actually injected;
- replacement post-commit UI failure is actually injected;
- deterministic partial-provenance recovery tests were added.

Do **not** undo those changes.

The independent fresh pass found the following current defects:

| ID | Severity | Area | Summary |
|---|---:|---|---|
| **R10-001** | **HIGH** | Main + initial Reference rollback safety | local exception cleanup can still delete through a path hierarchy that has just been detected as a reparse point |
| **R10-002** | **MEDIUM** | final promotion gate | initial Reference and replacement do not re-check reparse/path safety *after* their final staging hash work and immediately before the first canonical move |
| **R10-003** | **LOW-MEDIUM** | RecoveryCritical tests | new Main pre-promotion tests assert two incorrect canonical paths, allowing false-positive passes for ingame/provenance regressions |

The source-level blocker is R10-001.

---

# 0.2 Current repository state

Current `main`:

```text
859482fd08e0fdec5c6cd99fcc20ce181f042a5b
```

Commit message:

```text
Fix all issues from bugs9.md (R9-001 - R9-005)
```

Parent:

```text
452f06621d8014efe050a058422bc40aa98f6870
```

The changed implementation/test files are exactly the expected R9 repair surface:

```text
MainForm.MainWorkflow.cs
MainForm.ReferenceWorkflow.cs
MainForm.cs
AssetProcessorService.Main.cs
AssetProcessorService.Reference.cs
AssetProcessorService.cs
Bugs3ParanoidTests.cs
selected existing regression/UI tests
```

---

# 0.3 CI / execution evidence

The connected GitHub status surface currently exposes:

```text
statuses: []
```

for this SHA.

The available commit workflow-run wrapper returns:

```text
workflow_runs: []
```

and does not expose the direct push-to-main run.

The current analysis container has no usable:

```text
dotnet
pwsh
csc
msbuild
```

As already accepted for this workflow:

> missing exact Windows/.NET execution evidence is a deferred limitation, not a blocker by itself.

The FAIL verdict is caused by the source-level findings below.

---

# 1. `bugs9.md` retest

| R9 item | Result |
|---|---|
| R9-001 Main final staging authority | **FIXED materially** |
| R9-001 Main durable temp provenance | **FIXED** |
| R9-001 Main final path/reparse gate | **FIXED baseline** |
| R9-002 initial Reference raw staging authority | **FIXED materially** |
| R9-003 startup status suppression | **FIXED** |
| R9-004 real NoReference UI-failure injection | **FIXED** |
| R9-004 real replacement post-commit UI-failure injection | **FIXED** |
| R9-005 partial deterministic provenance recovery tests | **FIXED baseline** |

---

# 2. R9-001 retest details — PASS

Current Main staging sequence now correctly includes:

```text
source validation
durable Main journal already present
temp Main write + hash
temp ingame write + hash
render provenance
durable deterministic temp provenance write
OnBeforeMainStagingAuthorityGate test hook
RequireMainStagingAuthority()
canonical moves
exact complete-asset validation
```

`RequireMainStagingAuthority()` verifies:

```text
IsMainCommitting
MainHash present
MainProvenanceHash present
temp Main exists
temp Main SHA == MainHash
temp ingame exists
temp ingame SHA == MainHash
temp provenance exists
temp provenance SHA == MainProvenanceHash
ValidateSessionPathsForDestructiveOperation(session)
AssetFolder not reparse
ingame folder not reparse
```

Only after that does canonical promotion begin.

This is the correct R9 architecture.

---

# 3. R9-002 retest details — PASS at byte-authority level

Initial Reference now stages:

```text
deterministic temp image
deterministic durable temp provenance
```

Then calls:

```text
RequireInitialReferenceStagingAuthority(
    session,
    tempImagePath,
    tempProvenancePath)
```

before either canonical `File.Move`.

That helper uses raw file SHA-256:

```text
temp image SHA == ReferenceHash
temp provenance SHA == ReferenceProvenanceHash
```

The earlier text-decode/re-encode SHA comparison was removed from the final authority decision.

This closes the R9 byte-authority defect.

A narrower reparse-order issue remains and is R10-002 below.

---

# 4. R9-003 / R9-004 / R9-005 retest — PASS baseline

## Startup status

`AddStatus()` now guards:

```csharp
IsDisposed
Disposing
txtStatusHistory.IsDisposed
```

and no longer discards constructor-time status merely because the form handle has not yet been created.

## NoReference failure injection

A real test hook now executes immediately after the durable NoReference journal save and before the status message. The test throws from that hook and verifies commit continuation.

## Replacement post-commit failure injection

A real `OnReplacementDurableCommitUiHook` is invoked inside the post-durable UI helper. The test injects an exception there and proves NEW state remains durable.

## Partial provenance recovery

Explicit deterministic partial-provenance states now exist in RecoveryCritical tests for both initial Reference and replacement.

These changes are valuable and should remain.

---

# 5. R10-001 — HIGH — local rollback can delete through a path hierarchy already known to be unsafe

This is the main remaining blocker.

The project has an explicit safety invariant:

```text
Continue rejecting unsafe reparse-point destination directories.
Unknown or externally modified files must be preserved.
Fail closed instead of deleting them.
```

The persisted rollback/recovery APIs generally honor this by running:

```csharp
ValidateSessionPathsForDestructiveOperation(...)
```

before destructive work.

However the **local catch cleanup inside the mutators themselves** does not.

This affects:

```text
ProcessReference()
ProcessMainImage()
```

---

# 6. Why this is now concrete

The R9 Main gate intentionally performs a late safety check:

```text
hash staging files
ValidateSessionPathsForDestructiveOperation
check AssetFolder reparse
check ingame reparse
```

If that detects:

```text
AssetFolder became reparse
or
ingame folder became reparse
```

it throws.

That is correct.

But control immediately enters the local `catch`.

The local `catch` then proceeds to:

```text
File.Exists(path)
ComputeSha256(path)
TryDeleteFileWithError(path)
```

for temp/promoted artifacts.

`TryDeleteFileWithError()` is just:

```csharp
if (File.Exists(path))
{
    File.Delete(path);
}
```

It does not validate confinement or reparse state.

So the code can make this transition:

```text
detect unsafe reparse path
-> throw
-> immediately perform destructive cleanup through that unsafe path
```

That defeats the purpose of detecting the reparse point.

---

# 7. R10-001A — Main concrete sequence

Start with a valid active Main transaction:

```text
session.json durable
AssetFolder safe
ingame safe
temp Main staged
temp ingame staged
temp provenance staged
```

Then the destination hierarchy changes:

```text
ingame becomes a junction/reparse point
```

The final Main authority gate observes:

```text
IsReparsePoint(ingameFolder) == true
```

and throws.

Now `ProcessMainImage()` local catch sees:

```text
tempCopied == true
tempIngameCopied == true
tempProvenanceCreatedByThisCall == true
```

and may execute:

```text
TryVerifyFileHashOwnership(tempIngamePath, expectedHash)
TryDeleteFileWithError(tempIngamePath, ...)
```

If that deterministic path resolves through the junction to a matching file outside the intended tree, the tool can delete it.

Even if such a state requires another local process to race the operation, this codebase explicitly protects against reparse-path attacks/races. Therefore this must fail closed.

---

# 8. R10-001B — initial Reference has the same local-cleanup problem

`ProcessReference()` performs a late reparse check before promotion.

If it detects:

```text
asset folder reparse
reference folder reparse
```

it throws inside the transaction `try`.

Its local catch then handles:

```text
temp provenance
temp image
promoted provenance
promoted image
tool-created directories
```

using direct path-based ownership checks and delete helpers.

Again, there is no new destructive-path validation before those deletes.

So the operation can:

```text
correctly detect unsafe destination hierarchy
then destructively touch that same unsafe hierarchy
```

---

# 9. R10-001C — this is different from normal unknown-file preservation

Hash ownership alone is insufficient here.

Normally:

```text
path proven safe
+ hash/content ownership proven
= deletion permitted
```

But with a reparse point:

```text
the same string path may resolve somewhere outside the trusted asset tree
```

Therefore:

```text
matching hash
```

does **not** establish safe destination ownership.

Confinement must be proven first.

The project already encodes this ordering in:

```csharp
ValidateSessionPathsForDestructiveOperation(...)
```

The local rollback blocks are bypassing that ordering.

---

# 10. Required R10-001 behavior

Before **any destructive local exception cleanup**, verify the current path hierarchy again.

If the hierarchy is no longer safe:

```text
DO NOT DELETE ANY FILE
DO NOT DELETE ANY DIRECTORY
DO NOT RESTORE/MOVE ANY FILE
preserve durable journal
report rollback incomplete
close/fail closed
```

Recommended helper:

```csharp
private ValidationResult ValidateLocalRollbackPathSafety(
    AssetSession session)
{
    var validation =
        ValidationService
            .ValidateSessionPathsForDestructiveOperation(
                session);

    if (!validation.IsValid)
    {
        return validation;
    }

    return ValidationResult.Success();
}
```

But the important part is how it is used.

---

# 11. Required Main catch structure

At the start of the local `catch`:

```csharp
catch (Exception primaryException)
{
    var pathSafety =
        ValidationService
            .ValidateSessionPathsForDestructiveOperation(
                session);

    if (!pathSafety.IsValid)
    {
        throw new AssetProcessingException(
            "Main processing failed and local rollback was not attempted "
            + "because the destination hierarchy is no longer safe."
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                pathSafety.Errors),
            primaryException,
            rollbackComplete: false);
    }

    ...
}
```

Also explicitly require:

```text
AssetFolder not reparse
ingame folder not reparse
```

if the shared validator does not already prove the exact needed chain.

Then and only then run:

```text
TryVerify...
TryDelete...
TryDeleteEmptyDirectory...
```

This will make MainForm hit its existing:

```csharp
catch (AssetProcessingException ape)
    when (!ape.RollbackComplete)
```

path, preserve the journal, and close.

That is exactly the desired fail-closed behavior.

---

# 12. Required initial Reference catch structure

Do the same before the local Reference cleanup.

Example:

```csharp
catch (Exception primaryException)
{
    var rollbackPathSafety =
        ValidationService
            .ValidateSessionPathsForDestructiveOperation(
                session);

    if (!rollbackPathSafety.IsValid)
    {
        throw new IOException(
            "Reference processing failed and automatic rollback "
            + "was not attempted because the destination hierarchy "
            + "is no longer safe."
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                rollbackPathSafety.Errors),
            primaryException);
    }

    ...
}
```

Do **not** delete temp/canonical files before this gate.

The outer MainForm recovery can then invoke the normal ownership/path-validated `RollbackReference()`, which will also fail closed and preserve the Prepared journal while the hierarchy remains unsafe.

An even cleaner option is to introduce a small Reference-specific processing exception carrying:

```text
rollbackComplete
```

but that is not required if the existing caller behavior remains correct.

---

# 13. R10-001 mandatory tests

These tests must verify **no destructive attempt**, not merely “no canonical output”.

Add a test hook in the file-delete helper:

```csharp
[ThreadStatic]
internal static Action<string>?
    OnBeforeDeleteFileHook;
```

Invoke immediately before each transaction `File.Delete`.

If a separate directory deletion helper exists, add:

```csharp
OnBeforeDeleteDirectoryHook
```

as well.

## Test 1

```text
Main_FinalGateDetectsIngameReparse_PerformsZeroLocalDeletes
```

Setup:

```text
valid durable Main transaction
all deterministic staging exists
```

At:

```text
OnBeforeMainStagingAuthorityGate
```

make:

```text
FileAttributesProvider(ingameFolder)
=> ReparsePoint
```

Count delete attempts.

Expected:

```text
ProcessMainImage throws rollback-incomplete/fail-closed
delete file count == 0
delete directory count == 0
session journal remains durable
canonical outputs absent
staging artifacts preserved
```

## Test 2

```text
Main_FinalGateDetectsAssetFolderReparse_PerformsZeroLocalDeletes
```

Same assertions.

## Test 3

```text
InitialReference_DetectsReferenceFolderReparse_PerformsZeroLocalDeletes
```

Expected:

```text
Prepared journal remains
no local destructive cleanup is attempted
form/recovery closes fail-closed
```

## Test 4

```text
InitialReference_DetectsAssetFolderReparse_PerformsZeroLocalDeletes
```

---

# 14. R10-002 — MEDIUM — final reparse/path check is still too early in initial Reference and replacement

R9 correctly strengthened Main:

```text
final staging hashes
then path/reparse validation
then canonical move
```

That is the best current pattern.

Initial Reference and replacement are slightly weaker.

---

# 15. R10-002A — initial Reference ordering

Current order is:

```text
stage image
stage provenance

reparse check
OnBeforeInitialReferenceStagingAuthorityGate
hash temp image
hash temp provenance
File.Move image canonical
File.Move provenance canonical
```

The problem is:

```text
reparse check happens before the final hash work
```

Hashing a large image can take meaningful time.

The path hierarchy can change during that interval.

The final helper:

```text
RequireInitialReferenceStagingAuthority
```

currently only checks:

```text
file exists
raw image hash
raw provenance hash
```

It does not rerun:

```text
ValidateSessionPathsForDestructiveOperation
asset-folder reparse
reference-folder reparse
```

after the hashes.

---

# 16. Required initial Reference order

Use:

```text
stage image
stage provenance

optional early path/reparse check

OnBeforeInitialReferenceStagingAuthorityGate

final raw image SHA
final raw provenance SHA

FINAL path/confinement validation
FINAL asset/reference reparse validation

File.Move image canonical
File.Move provenance canonical
```

The final path/reparse validation should be the last safety decision before the first canonical move.

Implement either:

```csharp
RequireInitialReferenceStagingAuthority(...)
{
    hash checks...

    var paths =
        ValidateSessionPathsForDestructiveOperation(
            session);

    if (!paths.IsValid)
    {
        throw ...
    }

    var referenceFolder =
        Path.Combine(
            session.AssetFolder,
            AppConstants.ReferenceFolderName);

    if (IsReparsePoint(session.AssetFolder)
        || IsReparsePoint(referenceFolder))
    {
        throw ...
    }
}
```

or call a separate final path helper immediately after the hash helper.

---

# 17. R10-002B — replacement has the same ordering gap

Current replacement promotion begins with:

```csharp
RequireSafeReferenceReplacementTransaction(transaction);
```

Then:

```text
hash temp Reference
hash temp provenance
File.Move temp Reference
File.Move temp provenance
```

`RequireSafeReferenceReplacementTransaction()` includes the current path/reparse validation, but it runs **before** the final hashes.

For consistency with Main, run the safe-transaction/path validation **again after both hashes** and immediately before the first canonical move.

Example:

```csharp
var tempRefHash = ...
require H1

var tempProvHash = ...
require P1

// Final confinement/reparse gate after hash work:
RequireSafeReferenceReplacementTransaction(
    transaction);

File.Move(...);
File.Move(...);
```

No state-machine redesign is necessary.

---

# 18. R10-002 tests

Add a late hook **after final hash checks but before final path gate**, or structure the existing hook so it occurs immediately before the final path gate.

Required:

```text
InitialReference_ReparseChangesAfterFinalHash_NoCanonicalMutation
Replacement_ReparseChangesAfterFinalHash_NoCanonicalMutation
```

The tests should verify:

```text
no canonical NEW promotion
journal preserved or safe rollback according to transaction state
no delete through unsafe reparse path
```

These can be combined with R10-001 fail-closed deletion tests.

---

# 19. R10-003 — LOW-MEDIUM — R9 Main tests check the wrong canonical ingame/provenance paths

The new Main final-gate tests contain:

```csharp
Assert.False(
    File.Exists(
        Path.Combine(
            session.GetIngameFolderPath(),
            Path.GetFileName(mainSource))),
    "Ingame main must not exist");
```

But v1.1 canonical ingame naming is:

```text
<AssetName>.<source extension>
```

not:

```text
<source filename>
```

For example:

```text
AssetName = asset_r9_tamper_main_assisted
source    = main.png
```

Actual canonical ingame path is:

```text
ingame/asset_r9_tamper_main_assisted.png
```

The test checks:

```text
ingame/main.png
```

which is normally absent even if the real canonical ingame file incorrectly exists.

---

# 20. R10-003 provenance assertion is also wrong

The same tests contain:

```csharp
Assert.False(
    File.Exists(
        Path.Combine(
            session.AssetFolder,
            $"{Path.GetFileNameWithoutExtension(mainSource)}.md")),
    "Main provenance must not exist");
```

But canonical final provenance is:

```text
license.txt — Final AI-Generated Asset.md
```

The test checks:

```text
main.md
```

which is not the product's final provenance path.

Therefore two of the three canonical-absence assertions can pass trivially.

---

# 21. Why R10-003 matters

The source currently appears to implement R9-001 correctly.

This finding does **not** prove a current canonical-promotion bug.

But these tests are supposed to lock the new safety invariant.

With the wrong paths:

- a future regression that promotes tampered ingame bytes but later deletes root Main could pass;
- a future regression that promotes tampered final provenance but later deletes root Main could pass.

That is exactly the regression class these tests were added to prevent.

---

# 22. Required R10-003 fix

Every Main final-gate test should derive canonical paths from product authority:

```csharp
var rootMain =
    Path.Combine(
        session.AssetFolder,
        session.MainFilename!);

var ingame =
    session.GetIngameImagePath();

var finalProvenance =
    Path.Combine(
        session.AssetFolder,
        AppConstants.FinalProvenanceFileName);
```

Then:

```csharp
Assert.False(
    File.Exists(rootMain));

Assert.False(
    File.Exists(ingame));

Assert.False(
    File.Exists(finalProvenance));
```

Do not reconstruct managed paths independently if the model already exposes the canonical derivation.

---

# 23. Strengthen R10-003 assertions further

For the three tamper cases:

```text
temp Main tampered
temp ingame tampered
temp provenance tampered
```

assert:

```text
root canonical absent
actual ingame canonical absent
actual final provenance absent
durable active journal still exists if rollback is intentionally incomplete
tampered unknown temp remains preserved when appropriate
```

This proves both:

```text
no canonical mutation
and
fail-closed ownership preservation
```

---

# 24. Fresh checks that passed

No new issue was found in these areas:

```text
[PASS] R9 Main temp Main SHA gate
[PASS] R9 Main temp ingame SHA gate
[PASS] R9 Main temp provenance SHA gate
[PASS] Main deterministic durable provenance staging
[PASS] Main exact DateTimeOffset authority
[PASS] Main source-vs-journal hash authority
[PASS] initial Reference temp image raw SHA gate
[PASS] initial Reference temp provenance raw SHA gate
[PASS] initial Reference exact source/timestamp authority
[PASS] replacement pre-promotion temp image SHA gate
[PASS] replacement pre-promotion temp provenance SHA gate
[PASS] replacement rollback exactly-once UI boundary
[PASS] replacement successful post-commit UI boundary
[PASS] Cancel durable/UI boundary
[PASS] Main post-commit UI boundary
[PASS] Reference post-stable UI boundary
[PASS] startup template status visibility
[PASS] NoReference actual post-journal UI-failure injection
[PASS] replacement actual post-commit UI-failure injection
[PASS] partial deterministic provenance tests now present
[PASS] destructive persisted rollback APIs validate path confinement first
[PASS] README product tree remains consistent
[PASS] CI configuration from prior pass remains structurally intact
```

---

# 25. Recommended repair order

## Phase 1 — R10-001

Fix unsafe local exception cleanup first.

This is the only HIGH source-level issue and the true acceptance blocker.

## Phase 2 — R10-002

Make final promotion path/reparse gates consistent across:

```text
Main
initial Reference
replacement
```

Uniform invariant:

```text
durable authority
-> deterministic staging
-> final hashes
-> FINAL confinement/reparse check
-> canonical promotion
```

## Phase 3 — R10-003

Repair the test assertions so the safety suite actually locks the correct paths.

---

# 26. Preferred universal transaction invariant

After R10, all three production mutation flows should follow:

```text
1. durable journal authority
2. deterministic staging
3. validate signatures/content where relevant
4. final raw byte hashes against journal
5. final path confinement check
6. final reparse check
7. canonical promotion
8. exact final validation
```

And on any exception:

```text
A. validate path confinement/reparse AGAIN before local cleanup

B. if unsafe:
   no destructive cleanup
   preserve journal
   fail closed

C. if safe:
   exact ownership verification
   cleanup only tool-owned artifacts
```

This is the missing symmetry.

---

# 27. Static checks after repair

## Local cleanup gate

```powershell
rg -n `
  "catch \(Exception primaryException\)|TryDeleteFileWithError|TryDeleteEmptyDirectoryWithError|ValidateSessionPathsForDestructiveOperation" `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Main.cs `
  src/AssetProvenanceHelper/Services/AssetProcessorService.Reference.cs
```

Manual rule:

```text
inside each mutator catch:
ValidateSessionPathsForDestructiveOperation
MUST occur before first TryDelete*/File.Delete/Directory.Delete
```

---

## Final promotion path gate

```powershell
rg -n `
  "RequireMainStagingAuthority|RequireInitialReferenceStagingAuthority|PromoteNewReference|RequireSafeReferenceReplacementTransaction|File\.Move" `
  src/AssetProvenanceHelper/Services
```

Required:

```text
Main:
  final hashes
  path/reparse
  first File.Move

Initial Reference:
  final hashes
  path/reparse
  first File.Move

Replacement:
  final hashes
  path/reparse
  first File.Move
```

---

## Wrong test paths

```powershell
rg -n `
  "GetIngameFolderPath\(\).*Path\.GetFileName\(mainSource\)|GetFileNameWithoutExtension\(mainSource\).*\.md" `
  tests/AssetProvenanceHelper.Tests
```

Expected:

```text
0 matches in Main canonical-absence tests
```

Use:

```text
session.GetIngameImagePath()
AppConstants.FinalProvenanceFileName
```

instead.

---

# 28. Required Windows execution gate after repair

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

# 29. Definition of Done for the next pass

## Preserve R9 fixes

```text
[ ] Main deterministic durable provenance staging remains
[ ] Main final three-artifact SHA gate remains
[ ] initial Reference final raw SHA gate remains
[ ] AddStatus startup fix remains
[ ] real UI failure hooks remain
[ ] partial provenance tests remain
```

## R10-001

```text
[ ] Main local exception cleanup validates current destructive path safety first
[ ] initial Reference local exception cleanup validates current destructive path safety first
[ ] unsafe path => zero local delete/move/restore attempts
[ ] unsafe path => durable journal preserved
[ ] unsafe path => fail closed
```

## R10-002

```text
[ ] Main final hash -> path/reparse -> move ordering retained
[ ] initial Reference final hash -> path/reparse -> move ordering implemented
[ ] replacement final hash -> path/reparse -> move ordering implemented
```

## R10-003

```text
[ ] tests use session.GetIngameImagePath()
[ ] tests use AppConstants.FinalProvenanceFileName
[ ] tests assert actual root Main canonical path
[ ] tampered temp remains preserved where rollback intentionally fails closed
```

## Execution

```text
[ ] Debug warnings-as-errors PASS
[ ] Debug tests PASS
[ ] Release warnings-as-errors PASS
[ ] Release tests PASS
[ ] RecoveryCritical PASS
[ ] 20/20 Release PASS
[ ] publish PASS
[ ] smoke PASS
[ ] coverage PASS
```

---

# 30. Final tenth-pass conclusion

The `bugs9.md` repair was successful.

The repository is not failing because the R9 changes were wrong; they are good.

The remaining blocker is a deeper consistency rule that the late reparse checks made visible:

```text
detecting an unsafe path is not enough
if the local catch then deletes through that same path
```

The correct universal rule is:

```text
before canonical mutation:
  final hashes
  final path/reparse gate

before rollback mutation:
  path/reparse gate AGAIN
  then exact ownership checks
  then deletion
```

Once R10-001 is repaired, R10-002 and R10-003 are small follow-up hardening tasks.

**Current acceptance state: FAIL — one HIGH destructive-path rollback defect remains, plus one MEDIUM final-gate ordering issue and one LOW-MEDIUM false-assurance test issue.**

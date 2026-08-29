# AI Asset Provenance Helper — Paranoid Post-Rework Audit & Repair Guide

**File:** `bugs1.md`  
**Audit date:** 2026-08-18  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `main`  
**Audited commit:** `1e49f9a5be4f6935a9aa0edad67da4cb4af5a093`  
**Implementation under audit:** v1.1.0 rework  
**Authority for requested rework:** `_changePlan2.md`  
**Plan SHA-256:** `0814f9369a6a94853702f8f3a696b29f46dcede6bc132f432d6671349468187e`  
**Plan size:** 3,739 lines / 93,847 bytes

---

# 0. Executive verdict

## 0.1 Acceptance result

**FAIL — the rework must not be treated as fully accepted yet.**

A large portion of the requested v1.1.0 rework did land correctly, but a paranoid source/transaction/recovery audit found several defects that can produce one or more of the following:

- unsafe rollback through an `ingame` reparse-point/junction;
- loss of crash-recovery state;
- orphaned directories/files after failed operations;
- a stale active journal being silently left behind while the UI continues;
- a tampered provenance file being accepted as a “complete” asset;
- reference provenance tampering being carried into a final asset;
- non-crash-atomic Reference creation/replacement;
- an explicit Asset Name requirement being bypassed in Reference mode;
- the UI being physically too tall for the mandatory 1366×768 acceptance target;
- explicit UI requirements such as the button-sized drop target not being implemented as specified;
- tests that pass the happy path while omitting the dangerous path that the plan explicitly required them to cover.

The implementation therefore needs another repair round followed by a complete test pass.

## 0.2 What *did* land correctly

The audit confirms the following major rework items are present and directionally correct:

- `ProjectName` was removed from `AppSettings` and the visible Project input is gone.
- Legacy settings containing `ProjectName` remain loadable because the new settings model ignores the obsolete JSON member.
- New sessions derive the provenance Project label from `Asset Root Folder`.
- “Firefox Download Folder” was replaced with the browser-neutral “Image Download Folder”.
- Image discovery no longer prefers filenames beginning with `ChatGPT Image`; newest supported image wins.
- Image Download Folder is no longer globally required for manual Choose/Drop processing.
- Reference and Main selections have independent backing fields.
- Main CTA does not implicitly refresh the download folder.
- NoReference mode exists and does not fabricate Reference fields.
- NoReference precommit can be journaled before its target asset folder exists.
- Main output retains the original source filename at asset root.
- A canonical `ingame/<AssetName>.<ext>` copy is created.
- Main/root and ingame hashes are checked.
- Existing supported `ingame/<AssetName>.*` variants are preflighted.
- Reference replacement clears Main candidate + Final Prompt after a successful replacement.
- Final Prompt is explicit; the old automatic clipboard-on-submit behavior is gone.
- Header, version, app icon, `?` help control, colors, status history and NoReference visibility behavior are present.
- Application version is `1.1.0`.
- Release archive naming is now derived from executable ProductVersion instead of hard-coded `v1.0.0`.
- CI includes Debug/Release warn-as-error builds, tests, the 20-run Release loop, publish, smoke and coverage collection.

These are real improvements. The defects below are not a recommendation to rewrite the application; the safest approach remains **targeted repair of the current implementation**.

---

# 1. Audit method and execution limitation

This audit used four layers:

1. **Plan-to-code trace:** each material `_changePlan2.md` requirement was traced into the current v1.1.0 source.
2. **Transaction-state audit:** Reference, replacement, Main, NoReference, cancel and startup-recovery paths were traced across success, ordinary exceptions, rollback failure and hard-crash boundaries.
3. **Security/path audit:** canonical path derivation, reparse-point checks, exact-ownership checks and destructive operations were checked for path-escape and TOCTOU weaknesses.
4. **Test-suite audit:** the new v1.1 tests and CI gates were inspected for false assurance and missing failure cases.

### Environment limitation

The available execution container is Linux and currently has **no `dotnet` executable installed**. The repository pins .NET SDK `8.0.418` with roll-forward disabled and the application targets Windows Forms (`net8.0-windows`). A real Windows/.NET 8.0.418 runtime run therefore could not be executed in this environment.

This is **not** treated as a reason to stop the audit. All statically provable issues are documented below, and the exact Windows execution gates that must be run after repair are included in this file.

Do **not** claim that Debug/Release tests, the 20× flakiness loop, the self-contained publish or GUI display matrix have passed until they have actually run on Windows with SDK 8.0.418.

---

# 2. Severity model

- **CRITICAL** — destructive safety / path escape / possible deletion outside intended managed tree.
- **HIGH** — recovery or provenance-integrity failure, hard-crash atomicity failure, durable-state loss, or major explicit requirement failure.
- **MEDIUM** — functional requirement miss, misleading documentation/provenance, substantial UX/test weakness, or robustness defect.
- **LOW** — cleanup/maintainability/diagnostic mismatch that should be fixed before final acceptance but is not normally data-destructive.

---

# 3. Defect summary

| ID | Severity | Area | Summary |
|---|---|---|---|
| BUG-001 | **CRITICAL** | destructive rollback | `ingame` is not validated as a reparse-point boundary by `ValidateSessionPathsForDestructiveOperation()` |
| BUG-002 | **HIGH** | Main failure recovery | Required failed-Main reconciliation helper from plan §31.1 was not implemented |
| BUG-003 | **HIGH** | journal durability | NoReference session deletion failure is swallowed after an ordinary Main failure |
| BUG-004 | **HIGH** | provenance integrity | “Complete asset” validation uses substring checks instead of exact final provenance ownership |
| BUG-005 | **HIGH** | provenance integrity | Reference provenance is also accepted by substring checks before final Main completion/recovery |
| BUG-006 | **HIGH** | Reference crash safety | Initial Reference processing writes files before a durable Reference journal exists |
| BUG-007 | **HIGH** | Reference replacement | Replacement transaction exists only in memory; hard crash can orphan backups/new files or lose rollback state |
| BUG-008 | **HIGH** | UI compatibility | Mandatory 1366×768 no-scroll target is impossible because form MinimumSize height is 800 and default height is 920 |
| BUG-009 | **MEDIUM** | Asset naming | Reference workflow/UI use `ValidateAssetFolderName()` instead of `ValidateAssetName()`; image extensions are accepted |
| BUG-010 | **MEDIUM** | explicit UI requirement | Button-sized “Drop file here” control alongside Refresh/Choose/Open is missing |
| BUG-011 | **MEDIUM** | keyboard | Ctrl+R no longer performs Replace Reference in `ReferenceReady` state |
| BUG-012 | **MEDIUM** | validation UX | changing Prompt clears Main-image error state and vice versa; unrelated invalid fields lose their red outline |
| BUG-013 | **MEDIUM** | help overlay | Help overlay does not explicitly disable underlying application content as required by the plan |
| BUG-014 | **MEDIUM** | session schema | redundant persisted `IngameFilename` contradicts the plan and introduces another corruption/compatibility surface |
| BUG-015 | **MEDIUM** | service safety | `ProcessMainImage()` still allows a Reference session with no active Main journal to perform writes |
| BUG-016 | **MEDIUM** | provenance record | Reference template says `Reference file retained: no` although the tool copies and retains it |
| BUG-017 | **MEDIUM** | documentation | README documents wrong provenance filenames and nonexistent/misdescribed shortcuts |
| BUG-018 | **MEDIUM** | test/CI gate | CI coverage-presence list was not updated for important new production code; several mandatory plan tests are absent |
| BUG-019 | **MEDIUM** | file validation | any non-empty file with `.png/.jpg/.webp` extension is accepted as an image; format signature is not validated |
| BUG-020 | **MEDIUM** | robustness | download-folder enumeration exceptions can escape the Refresh event path and crash the UI |
| BUG-021 | **MEDIUM** | exact ownership longevity | exact ownership is reconstructed from the *current* template rather than a stored provenance digest |
| BUG-022 | **LOW** | smoke stability | publish smoke startup timeout was reduced to 5 seconds, increasing cold-start flakiness risk |
| BUG-023 | **LOW** | plan cleanup | legacy `ResolveImageSelection()`/shared-control aliases remain although the final plan required their removal |
| BUG-024 | **LOW** | status/diagnostics | several status messages required by plan §32 were not added |

**Blocking recommendation:** repair BUG-001 through BUG-018 before claiming the rework accepted. BUG-019 through BUG-024 should also be resolved in the same round if the objective remains a paranoid, zero-known-defect release.

---

# 4. BUG-001 — CRITICAL — `ingame` reparse point is not part of destructive-path validation

## Evidence

Affected file:

```text
src/AssetProvenanceHelper/Services/ValidationService.Paths.cs
```

`ValidateSessionPathsForDestructiveOperation()` currently validates:

- Asset Root;
- Asset Folder;
- for Reference mode, Reference folder and canonical Reference paths.

But for `NoReference` it returns success immediately after checking only Root + Asset Folder:

```csharp
if (session.WorkflowMode == AssetWorkflowMode.NoReference)
{
    return ValidationResult.Success();
}
```

There is **no `ingame` reparse-point check** in this destructive validator.

`RollbackMain()` depends on this validator before computing/deleting the canonical ingame path.

## Why this is dangerous

`ingame` is a fixed managed child and is a destructive-operation boundary. If it is replaced by a junction/symlink between runs, the logical path:

```text
<asset>\ingame\<AssetName>.png
```

can resolve outside the asset directory.

The hash check is valuable but is **not a substitute for path confinement**. A file outside the asset tree that happens to have the journaled hash can still be considered owned and deleted through the link.

This directly contradicts `_changePlan2.md` §0.1 item 4 and the non-negotiable safety rule that `ingame` must participate in reparse/path checks.

## Required repair

Extend destructive validation to cover **all Main paths** for both workflow modes:

```text
AssetRootFolder
AssetFolder
root Main file
final provenance
ingame folder
canonical ingame file
Main temp image
Main temp provenance
Main temp ingame file
```

Every derived path must be proven to have the exact expected parent.

### Copy-ready helper pattern

```csharp
private static bool IsSafeFilename(string? filename) =>
    !string.IsNullOrWhiteSpace(filename)
    && string.Equals(
        Path.GetFileName(filename),
        filename,
        StringComparison.Ordinal);

private static void RequireExactParent(
    string path,
    string expectedParent,
    string description,
    ICollection<string> errors)
{
    try
    {
        var normalized = NormalizePath(path);
        var parent = Path.GetDirectoryName(normalized);

        if (parent is null || !PathsEqual(parent, expectedParent))
        {
            errors.Add(
                $"{description} is not inside the expected directory '{expectedParent}'.");
        }
    }
    catch (Exception ex)
    {
        errors.Add($"{description} path is invalid: {ex.Message}");
    }
}
```

Add this block before any early `NoReference` return — preferably remove the early return entirely:

```csharp
var ingameFolder =
    NormalizePath(
        Path.Combine(
            actualAssetFolder,
            AppConstants.IngameFolderName));

if (Directory.Exists(ingameFolder) && IsReparsePoint(ingameFolder))
{
    errors.Add(
        "Session ingame folder is a reparse point and cannot be operated on safely.");
}

if (session.IsMainCommitting)
{
    if (!IsSafeFilename(session.MainFilename))
    {
        errors.Add("Session MainFilename is unsafe.");
    }
    else
    {
        var rootMain =
            NormalizePath(
                Path.Combine(
                    actualAssetFolder,
                    session.MainFilename!));

        RequireExactParent(
            rootMain,
            actualAssetFolder,
            "Root Main image",
            errors);

        var finalProvenance =
            NormalizePath(
                Path.Combine(
                    actualAssetFolder,
                    AppConstants.FinalProvenanceFileName));

        RequireExactParent(
            finalProvenance,
            actualAssetFolder,
            "Final provenance",
            errors);

        var ingameFilename =
            AssetNaming.BuildIngameFilename(
                session.AssetFolderName,
                session.MainFilename!);

        var ingamePath =
            NormalizePath(
                Path.Combine(
                    ingameFolder,
                    ingameFilename));

        RequireExactParent(
            ingamePath,
            ingameFolder,
            "Ingame image",
            errors);

        var tempMain = session.GetMainTempImagePath();
        var tempProv = session.GetMainTempProvenancePath();
        var tempIngame = session.GetMainTempIngamePath();

        if (!string.IsNullOrWhiteSpace(tempMain))
        {
            RequireExactParent(
                tempMain,
                actualAssetFolder,
                "Temporary Main image",
                errors);
        }

        if (!string.IsNullOrWhiteSpace(tempProv))
        {
            RequireExactParent(
                tempProv,
                actualAssetFolder,
                "Temporary Main provenance",
                errors);
        }

        if (!string.IsNullOrWhiteSpace(tempIngame))
        {
            RequireExactParent(
                tempIngame,
                ingameFolder,
                "Temporary ingame image",
                errors);
        }
    }
}
```

Return success only after Main + Reference-specific checks are complete.

### TOCTOU hardening

Also repeat the `ingame` reparse-point check **immediately before destructive mutation** in `RollbackMain()`, after ownership hashes were checked but before deletions begin:

```csharp
var ingameFolder = session.GetIngameFolderPath();

if (Directory.Exists(ingameFolder)
    && ValidationService.IsReparsePoint(ingameFolder))
{
    return ValidationResult.Failure(
        "Ingame folder became a reparse point before rollback. No Main files were deleted.");
}
```

Do the same immediately before promotion in `ProcessMainImage()` after `Directory.CreateDirectory(ingameFolder)`.

## Mandatory tests

```csharp
[Fact]
public void DestructiveValidation_RejectsIngameReparsePoint()
{
    using var workspace = new TestWorkspace();
    var processor = workspace.CreateAssetProcessor();
    var settings = workspace.CreateSettings();

    var reference = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
    var session = processor.ProcessReference(
        settings,
        "asset1",
        reference,
        DateTimeOffset.Now);

    var main = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
    session.IsMainCommitting = true;
    session.MainFilename = "main.png";
    session.MainPrompt = "prompt";
    session.MainProcessedAt = DateTimeOffset.Now;
    session.MainHash = processor.ComputeSha256(main);
    session.MainTransactionId = Guid.NewGuid().ToString("N");

    var ingame = session.GetIngameFolderPath();
    Directory.CreateDirectory(ingame);

    var previous = ValidationService.FileAttributesProvider;
    try
    {
        ValidationService.FileAttributesProvider = path =>
            ValidationService.PathsEqual(path, ingame)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : File.GetAttributes(path);

        var result =
            ValidationService.ValidateSessionPathsForDestructiveOperation(session);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Contains("ingame", StringComparison.OrdinalIgnoreCase)
              && e.Contains("reparse", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        ValidationService.FileAttributesProvider = previous;
    }
}
```

Also add a real Windows junction/symlink integration test where permissions allow it.

---

# 5. BUG-002 — HIGH — required failed-Main reconciliation helper was omitted

## Evidence

`_changePlan2.md` §31.1 explicitly required ordinary Main failures to be reconciled through `RollbackMain()` before UI/session state is changed.

Current file:

```text
src/AssetProvenanceHelper/MainForm.MainWorkflow.cs
```

Instead, ordinary exceptions do this:

```text
NoReference       -> delete session record directly
ReferenceAssisted -> ResetMainCommitMetadata() + save session
```

The code does **not** call `RollbackMain()` in that ordinary-failure branch.

## Consequences

This produces multiple side effects:

1. `ProcessMainImage()` creates `AssetFolder` / `ingame` **before** entering its main internal try/catch. If directory creation partly succeeds and the next operation throws, those directories are not reconciled.
2. Internal rollback removes files, but it deliberately does not own all outer directory/session cleanup semantics.
3. In Reference mode an empty tool-created `ingame` folder can remain after a failed Main attempt.
4. A later Cancel may delete the Reference files but be unable to remove the Asset folder because the orphan `ingame` directory remains.
5. NoReference mode has no Reference state to return to, so the journal must be removed only *after* exact rollback reconciliation has succeeded.

## Required repair

Implement the plan’s reconciliation helper and use it for ordinary processing failures.

### Copy-ready implementation

```csharp
private bool TryReconcileFailedMainCommit(
    AssetSession session,
    bool noReferenceMode)
{
    ValidationResult rollback;

    try
    {
        rollback =
            _assetProcessorService.RollbackMain(
                session,
                session.MainFilename);
    }
    catch (Exception ex)
    {
        ShowError(
            "CRITICAL: Failed Main transaction could not be safely reconciled.",
            ex);
        Close();
        return false;
    }

    if (!rollback.IsValid)
    {
        ShowMessageBox(
            "CRITICAL: Failed Main transaction could not be fully rolled back.\n\n"
            + string.Join(Environment.NewLine, rollback.Errors),
            "Critical Main rollback error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        Close();
        return false;
    }

    if (!noReferenceMode)
    {
        try
        {
            _sessionService.Save(session);
            _currentSession = session;
            _state = UiState.ReferenceReady;
            ApplyState();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(
                "CRITICAL: Main rollback succeeded, but the restored Reference session could not be saved.",
                ex);
            Close();
            return false;
        }
    }

    try
    {
        _sessionService.Delete();
        _currentSession = null;
        _state = UiState.Idle;
        ApplyState();
        return true;
    }
    catch (Exception ex)
    {
        // Do not save the reset in-memory NoReference object. The durable
        // journal still contains the active transaction and is the only
        // reliable recovery authority for the next startup.
        ShowError(
            "CRITICAL: Main outputs were rolled back, but the no-reference session journal could not be removed.",
            ex);
        Close();
        return false;
    }
}
```

### Important control-flow rule

Do **not** use this helper for:

```csharp
catch (AssetProcessingException ex) when (!ex.RollbackComplete)
```

In that case preserve the active journal, make no extra destructive attempt, show CRITICAL and close.

### Also move directory creation inside the processor transaction

In `ProcessMainImage()` move:

```csharp
Directory.CreateDirectory(session.AssetFolder);
Directory.CreateDirectory(ingameFolder);
```

inside the same guarded try/catch that owns rollback flags, or guarantee the outer reconciliation helper always handles every exception after the durable Main journal exists.

Prefer doing both.

## Mandatory tests

Inject failure at each boundary:

```text
before asset folder creation
after asset folder creation
before ingame folder creation
after ingame folder creation
after temp Main copy
after temp ingame copy
after final provenance promotion
after root Main promotion
after ingame promotion
```

For Reference mode, after each clean failure assert:

```text
session.json exists
session.IsMainCommitting == false
Reference image/provenance still exist
no root Main
no final provenance
no ingame asset
no tool-created empty ingame folder
UI can return ReferenceReady
```

For NoReference mode, after each clean failure assert:

```text
session.json absent
no tool-created root Main/provenance/ingame
no tool-created empty asset folder
UI returns Idle
```

---

# 6. BUG-003 — HIGH — NoReference journal deletion failure is silently swallowed

## Evidence

Current ordinary failure branch contains effectively:

```csharp
try
{
    _sessionService.Delete();
}
catch
{
    // Non-critical session cleanup error
}
_currentSession = null;
```

This is **not non-critical**.

## Failure mode

If `session.json` cannot be deleted:

- the durable journal still says an active NoReference Main transaction exists;
- the in-memory `_currentSession` is cleared;
- the UI continues as if idle;
- a later operation can overwrite `session.json`, destroying the only recovery record;
- the next startup may see a state that no longer corresponds to the user-visible workflow.

This directly contradicts `_changePlan2.md` §31.1.

## Required repair

Use BUG-002’s reconciliation helper.

A failed delete after NoReference rollback must be treated as:

```text
CRITICAL
preserve on-disk active journal
close application
recover on next startup
```

Never swallow the exception.

## Mandatory test

Add a test hook around `SessionService.Delete()` or an injectable file operation.

Expected assertion:

```text
Delete throws
_currentSession is NOT silently treated as safe idle state
application requests close / critical path executes
session.json remains
no new session overwrite is possible in the same UI lifetime
```

---

# 7. BUG-004 — HIGH — completion/recovery does not enforce exact final provenance

## Evidence

There is already a good exact validator:

```csharp
ValidateExactFinalProvenanceOwnership(...)
```

But `ValidateCompleteAsset()` calls:

```csharp
ValidateFinalProvenanceContent(...)
```

and that method only checks whether the text **contains**:

```text
Asset ID
Reference filename (Reference mode)
Project
Generation date
Prompt
```

It does not require the file to exactly equal the tool-rendered provenance.

## Failure mode

A file such as:

```text
<correct provenance>

TAMPERED OR INCORRECT EXTRA RIGHTS TEXT
```

still passes `ValidateCompleteAsset()`.

Startup recovery can then conclude “asset is complete” and delete the active session journal even though the provenance document has been modified.

This is especially serious because the tool’s purpose is provenance documentation.

## Required repair

Create an exact complete-asset validator that receives `TemplateService`.

### Copy-ready helper

```csharp
public ValidationResult ValidateExactReferenceOutput(
    AssetSession session,
    TemplateService templateService)
{
    var normal = ValidateReferenceOutput(session);
    if (!normal.IsValid)
    {
        return normal;
    }

    return ValidateExactReferenceProvenanceOwnership(
        session,
        session.ReferenceProvenancePath,
        templateService);
}
```

Change complete validation signature to include TemplateService:

```csharp
public ValidationResult ValidateCompleteAsset(
    AssetSession session,
    string mainImagePath,
    string finalProvenancePath,
    string mainFilename,
    string mainGenerationDate,
    string prompt,
    TemplateService templateService,
    string? expectedMainHash = null)
```

Then replace the substring-only final gate with:

```csharp
var exactFinal =
    ValidateExactFinalProvenanceOwnership(
        session,
        finalProvenancePath,
        templateService);

if (!exactFinal.IsValid)
{
    errors.AddRange(exactFinal.Errors);
}
```

For Reference mode also use:

```csharp
var exactReference =
    ValidateExactReferenceOutput(
        session,
        templateService);
```

Update every caller:

```text
AssetProcessorService.ProcessMainImage
MainForm recovery
all direct tests
```

## Also strengthen MainPrompt session validation

Current session validation rejects only `null` MainPrompt. A whitespace-only prompt should never be a valid active journal because the real workflow forbids it.

Replace:

```csharp
if (session.MainPrompt is null)
```

with:

```csharp
if (string.IsNullOrWhiteSpace(session.MainPrompt))
{
    errors.Add(
        "Session IsMainCommitting is true but MainPrompt is missing or blank.");
}
```

## Mandatory tests

```csharp
[Fact]
public void CompleteAsset_RejectsFinalProvenanceWithExtraTamperedText()
{
    // Create a fully completed asset, then append text to final provenance.
    // Exact completion must fail.
}

[Fact]
public void Recovery_DoesNotDeleteJournalForTamperedFinalProvenance()
{
    // Simulate crash after complete output before session deletion,
    // mutate provenance while retaining all expected substrings,
    // run startup recovery,
    // assert journal is preserved / asset is not accepted as complete.
}

[Theory]
[InlineData("")]
[InlineData("   ")]
public void ActiveMainSession_RejectsBlankPrompt(string prompt)
{
    // Construct active journal and assert ValidateSession fails.
}
```

---

# 8. BUG-005 — HIGH — Reference provenance can be tampered and still used for final completion

## Evidence

`ValidateReferenceOutput()` calls `ValidateReferenceProvenanceContent()`, which also performs only substring checks.

`HandleReferenceAssistedMainImage()` validates only:

```csharp
_validationService.ValidateSession(session)
```

before it creates the Main journal.

`ValidateSession()` verifies the Reference image hash/path but does not require exact Reference provenance text.

## Failure mode

After Reference is saved, modify the reference provenance while retaining these three lines:

```text
Asset ID: <ref>
Project: <project>
Generation date: <date>
```

Then complete Main.

The current workflow can still accept the Reference session, generate final provenance pointing to it, validate completion and delete the session journal.

## Required repair

Before Main journal preparation in Reference mode:

```csharp
var referenceValidation =
    _validationService.ValidateExactReferenceOutput(
        session,
        _templateService);

if (!referenceValidation.IsValid)
{
    ShowValidationError(
        "Reference provenance is inconsistent or modified",
        referenceValidation);
    return;
}
```

Also make `ProcessMainImage()` independently fail closed for Reference mode:

```csharp
if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted)
{
    var referenceValidation =
        _validationService.ValidateExactReferenceOutput(
            session,
            _templateService);

    if (!referenceValidation.IsValid)
    {
        throw new InvalidDataException(
            string.Join(
                Environment.NewLine,
                referenceValidation.Errors));
    }
}
```

This prevents a future caller from bypassing MainForm safety.

## Mandatory test

```csharp
[Fact]
public void MainCommit_RejectsReferenceProvenanceThatOnlyPassesSubstringChecks()
{
    // Save valid Reference.
    // Append a tampered line or alter a rights paragraph without changing
    // Asset ID / Project / date.
    // Prepare Main.
    // ProcessMainImage must refuse before writing Main outputs.
}
```

---

# 9. BUG-006 — HIGH — initial Reference is exception-safe but not hard-crash-safe

## Evidence

Current sequence is:

```text
ProcessReference()
  -> create asset/reference folders
  -> copy Reference
  -> write Reference provenance
  -> return AssetSession
MainForm
  -> SessionService.Save(createdSession)
```

The durable `session.json` is written **after filesystem mutation**.

The `try/catch` rollback is good for managed exceptions, but it cannot help on:

```text
power loss
process kill
OS crash
machine restart
hard termination between processor return and session save
```

## Crash windows

### Window A

```text
asset/reference folders created
no session.json
```

### Window B

```text
Reference copied
no provenance
no session.json
```

### Window C

```text
Reference + provenance complete
no session.json
```

On restart the tool has no recovery authority. A retry hits collisions.

## Additional managed-exception issue

If `SessionService.Save()` itself fails and `RollbackReference()` is incomplete, `HandleReference()` currently throws into its outer generic catch and merely shows an error. It does **not** force a critical shutdown despite having no durable recovery record.

## Required architecture

Journal Reference intent **before the first managed output mutation**, exactly as Main does.

Recommended minimal model extension:

```csharp
public enum ReferenceCommitPhase
{
    None = 0,
    Prepared = 1
}
```

Add to `AssetSession`:

```csharp
public ReferenceCommitPhase ReferenceCommitPhase { get; set; }
public string? ReferenceTransactionId { get; set; }
public string? ReferenceProvenanceHash { get; set; }
```

Preparation should:

1. validate settings/name/source;
2. derive every canonical path;
3. hash source;
4. render exact Reference provenance in memory;
5. hash rendered provenance bytes;
6. create a `ReferenceAssisted` session with `ReferenceCommitPhase.Prepared`;
7. save it atomically;
8. only then create directories/copy/write.

After successful output validation:

```csharp
session.ReferenceCommitPhase = ReferenceCommitPhase.None;
session.ReferenceTransactionId = null;
_sessionService.Save(session);
```

Startup recovery for `Prepared` should reconcile:

| Disk state | Action |
|---|---|
| no managed output | remove journal |
| only exact owned partial output | delete it safely, cleanup owned empty folders, remove journal |
| full exact Reference output | promote journal to normal ReferenceReady and save |
| unknown/tampered output | preserve journal, CRITICAL, close |

## Immediate safety patch if full journal refactor is deferred

At minimum, if Reference session save fails and rollback is incomplete:

```csharp
ShowMessageBox(
    "CRITICAL: Reference files were created, the recovery journal could not be saved, and rollback was incomplete. Close the application and inspect the reported paths before continuing.",
    "Critical Reference Error",
    MessageBoxButtons.OK,
    MessageBoxIcon.Error);
Close();
return;
```

This does not solve hard-crash atomicity, but prevents the current UI from continuing after a known unrecoverable managed failure.

---

# 10. BUG-007 — HIGH — Reference replacement transaction is not persisted

## Evidence

`ReferenceReplacementTransaction` is only an in-memory object containing:

```text
TransactionId
OldSession
NewSession
BackupReferencePath
BackupProvenancePath
IsCommitted
```

`PrepareReferenceReplacement()` already performs filesystem mutation before the new session is saved:

```text
copy new temp
move old Reference -> .old backup
move old provenance -> .old backup
promote new Reference
promote new provenance
return transaction object
```

Then MainForm saves `transaction.NewSession` and later deletes backups.

There is no persistent replacement journal.

## Hard-crash windows

### Window A — after old backup / new promotion, before NewSession save

`session.json` still represents the old Reference but its canonical files have moved/replaced.

Startup validation sees an inconsistent old session and cannot reconstruct the in-memory replacement transaction ID/backups.

### Window B — after NewSession save, before backup cleanup

Startup can resume the new session, but old `.old` backup files remain forever because the transaction ID is not in durable state.

### Window C — during backup cleanup

One backup may be deleted while the other remains; there is still no journal to reconcile the residue.

## Required repair

Persist the replacement transaction **before the first canonical mutation**.

Recommended separate journal:

```text
reference-replacement.json
```

### Copy-ready model

```csharp
public enum ReferenceReplacementPhase
{
    Prepared = 0,
    OldBackedUp = 1,
    NewPromoted = 2,
    SessionSwitched = 3
}

public sealed class ReferenceReplacementJournal
{
    public required string TransactionId { get; init; }
    public required AssetSession OldSession { get; init; }
    public required AssetSession NewSession { get; init; }
    public required string BackupReferencePath { get; init; }
    public required string BackupProvenancePath { get; init; }
    public ReferenceReplacementPhase Phase { get; set; }
}
```

Persist with the same atomic-temp-and-move semantics as `SessionService.Save()`.

### Required sequence

```text
1. prepare new temp bytes/provenance in memory / safe temp paths
2. create replacement journal Phase=Prepared
3. atomically save replacement journal
4. move old canonical files to exact backups
5. save Phase=OldBackedUp
6. promote new canonical files
7. save Phase=NewPromoted
8. save NewSession to session.json
9. save Phase=SessionSwitched
10. verify new exact output
11. delete exact verified old backups
12. delete replacement journal
```

### Startup ordering

Before normal `session.json` recovery:

```csharp
RecoverReferenceReplacementJournalIfPresent();
RecoverSessionOnStartup();
```

### Recovery policy

- `Prepared`: no canonical mutation should have occurred; remove owned temps + replacement journal.
- `OldBackedUp`: restore exact old backups, remove exact owned new temps, keep OldSession.
- `NewPromoted`: if session.json is still OldSession, roll back to old; if session switch can be proven, finish new promotion.
- `SessionSwitched`: verify exact NewSession output, remove exact verified backups, delete replacement journal.
- any unknown/tampered state: fail closed; preserve files and journal; CRITICAL + close.

## Mandatory tests

Tests must construct/persist each phase and invoke startup recovery:

```text
ReplacementRecovery_Prepared
ReplacementRecovery_OldBackedUp
ReplacementRecovery_NewPromoted_BeforeSessionSwitch
ReplacementRecovery_SessionSwitched_BeforeBackupDelete
ReplacementRecovery_OneBackupAlreadyDeleted
ReplacementRecovery_TamperedBackup_FailsClosed
ReplacementRecovery_TamperedNewReference_FailsClosed
```

Do not rely only on exception-injection tests; the defect is specifically about process death between calls.

---

# 11. BUG-008 — HIGH — mandatory 1366×768 GUI target cannot fit

## Evidence

`MainForm.Designer.cs` currently sets:

```csharp
MinimumSize = new Size(820, 800);
Size = new Size(950, 920);
AutoScroll = false;
```

The plan’s mandatory acceptance matrix includes:

```text
1366×768 @ 100% -> all main content visible; no main scrollbar
```

A form whose **minimum outer height is 800 pixels cannot fit on a 768-pixel display**, even before accounting for Windows taskbar/work area and title bar.

At 125%/150% scaling the effective pressure is worse.

## Required repair

Start from the plan target:

```csharp
MinimumSize = new Size(960, 680);
Size = new Size(1100, 740);
AutoScroll = false;
```

But simply changing two numbers is not sufficient. Compact the card/status layout so 680 logical pixels is genuinely usable.

Recommended adjustments:

- remove the large percentage-height drag/drop surfaces; use the required button-sized Drop controls (BUG-010);
- status history target height around 80–90 logical pixels instead of 110;
- keep Header about 44–50;
- Settings two compact rows;
- Current Asset one compact row;
- eliminate unnecessary vertical card padding;
- Main prompt gets the remaining flexible height.

## Mandatory GUI matrix

Manually verify **both modes**:

| Display | Scaling | Requirement |
|---|---:|---|
| 1366×768 | 100% | complete workflow visible, no main scrollbar |
| 1920×1080 | 100% | no clipping/overlap |
| 1920×1080 | 125% | no clipping/overlap |
| 1920×1080 | 150% | usable, no main scrollbar |
| 2560×1440 | 125% | balanced, no pathological stretching |

Also test:

```text
long Asset Root path
long selected filename
long saved Reference filename
NoReference expanded Main card
Help overlay
Windows taskbar at standard size
```

Add structural tests asserting the configured logical minimum/default sizes so this regression cannot silently return.

---

# 12. BUG-009 — MEDIUM — image extensions are still accepted as Asset Name in Reference mode

## Evidence

A correct method exists:

```csharp
ValidateAssetName(name, acceptedExtensions)
```

It rejects a supported image extension while still allowing `asset.v2`.

However:

```text
MainForm.ValidationUi.ValidateReferenceActionUi()
MainForm.ValidationUi.ValidateMainActionUi() [NoReference UI check]
AssetProcessorService.ProcessReference()
```

still use `ValidateAssetFolderName()` in important places.

`ProcessReference()` therefore allows:

```text
Asset Name = onboarding1.png
```

Then a `.jpg` Main can produce:

```text
ingame/onboarding1.png.jpg
```

## Required repair

In `ValidateReferenceActionUi()`:

```csharp
var nameValidation =
    _validationService.ValidateAssetName(
        txtAssetFolderName.Text,
        _settings.AcceptedExtensions);
```

In NoReference branch of `ValidateMainActionUi()` use the same method.

In `ProcessReference()` replace:

```csharp
ValidateAssetFolderName(assetFolderName)
```

with:

```csharp
ValidateAssetName(
    assetFolderName,
    settings.AcceptedExtensions)
```

Do **not** change legacy session structural validation to blindly reject all dotted names; `asset.v2` remains valid.

## Mandatory tests

```csharp
[Theory]
[InlineData("onboarding1.png")]
[InlineData("onboarding1.PNG")]
[InlineData("onboarding1.jpg")]
[InlineData("onboarding1.jpeg")]
[InlineData("onboarding1.webp")]
public void Reference_RejectsAssetNameEndingInImageExtension(string name)
{
    // ProcessReference must fail before creating any directory.
}

[Fact]
public void Reference_AcceptsNonImageDotSuffix()
{
    // asset.v2 remains valid.
}

[Fact]
public void ReferenceUi_HighlightsAssetNameEndingInImageExtension()
{
}
```

---

# 13. BUG-010 — MEDIUM — required button-sized Drop control was not implemented

## Evidence

The explicit requirement and `_changePlan2.md` card spec require this control row:

```text
[Refresh] [Choose File...] [Drop file here] [Open Image Folder]
```

Current cards instead use a large percentage-height Label drop surface and buttons:

```text
Refresh
Choose File...
Open Downloads
```

The existing UI test only verifies that `lblReferenceDrop`/`lblMainDrop` exist; it does not verify the requested button-sized placement.

## Required repair

Use a true button-sized drop target in each FlowLayoutPanel.

### Copy-ready helper

```csharp
private static Button CreateDropButton(string name)
{
    return new Button
    {
        Name = name,
        Text = "Drop file here",
        AutoSize = true,
        Padding = new Padding(8, 3, 8, 3),
        UseVisualStyleBackColor = true,
        AllowDrop = true,
        TabStop = false
    };
}
```

Reference:

```csharp
btnDropReference = CreateDropButton("btnDropReference");
btnDropReference.DragEnter += ImageDrop_DragEnter;
btnDropReference.DragDrop += (_, e) =>
    ImageDrop_DragDrop(ImageSlot.Reference, e);

refButtons.Controls.Add(btnRefreshReference);
refButtons.Controls.Add(btnChooseReference);
refButtons.Controls.Add(btnDropReference);
refButtons.Controls.Add(btnOpenDownloadsReference);
```

Main equivalent:

```csharp
btnDropMain = CreateDropButton("btnDropMain");
btnDropMain.DragEnter += ImageDrop_DragEnter;
btnDropMain.DragDrop += (_, e) =>
    ImageDrop_DragDrop(ImageSlot.Main, e);
```

The large drop area can be removed, which also helps BUG-008.

Keep a compact bordered selected-file display to retain red error highlighting.

## Tests

```csharp
[Fact]
public void ReferenceCard_HasButtonSizedDropControlInActionRow()
{
}

[Fact]
public void MainCard_HasButtonSizedDropControlInActionRow()
{
}

[Fact]
public void ReferenceDropButton_AcceptsExactlyOneValidImage()
{
}

[Fact]
public void MainDropButton_DoesNotChangeReferenceSelection()
{
}
```

---

# 14. BUG-011 — MEDIUM — Ctrl+R does not Replace Reference

## Evidence

Plan §33 requires:

```text
Ctrl+R -> Reference / Replace Reference
```

Current handler only executes Ctrl+R when:

```csharp
_state == UiState.Idle && !chkNoReference.Checked
```

It does nothing in `ReferenceReady`.

## Required repair

```csharp
if (e.KeyCode == Keys.R)
{
    if (_state == UiState.ReferenceReady)
    {
        e.SuppressKeyPress = true;
        HandleReplaceReference();
        return;
    }

    if (_state == UiState.Idle && !chkNoReference.Checked)
    {
        e.SuppressKeyPress = true;
        HandleReference();
        return;
    }
}
```

Help-visible short-circuit remains first, and NoReference Idle must still ignore Ctrl+R.

## Tests

```text
CtrlR_Idle_PerformsReference
CtrlR_ReferenceReady_PerformsReplacement
CtrlR_NoReference_DoesNothing
CtrlR_HelpVisible_DoesNothing
```

---

# 15. BUG-012 — MEDIUM — validation visuals clear unrelated errors

## Evidence

`txtPrompt.TextChanged` calls:

```csharp
ClearMainValidationVisuals();
```

That method clears **both**:

```text
pnlMainImageHost
pnlPromptHost
```

Likewise `SetSelectedImage(ImageSlot.Main, ...)` calls the same helper.

## Failure mode

1. Submit with missing Main image + missing Prompt.
2. Both become red.
3. Type into Prompt only.
4. Main-image red border disappears even though Main image is still missing.

The plan says: **when the relevant input changes, clear that field’s red state**.

## Required repair

Split helpers:

```csharp
private void ClearReferenceImageValidation()
{
    HighlightField(pnlReferenceImageHost, false);
    StopPulseFor(btnReference);
}

private void ClearMainImageValidation()
{
    HighlightField(pnlMainImageHost, false);
    StopPulseFor(btnMainImage);
}

private void ClearPromptValidation()
{
    HighlightField(pnlPromptHost, false);
    StopPulseFor(btnMainImage);
}
```

Wire:

```csharp
txtPrompt.TextChanged += (_, _) => ClearPromptValidation();
```

and `SetSelectedImage(Main, ...)` only clears `pnlMainImageHost`.

Do the equivalent for Reference.

## Test

```csharp
[Fact]
public void EditingPrompt_DoesNotClearMissingMainImageError()
{
}
```

---

# 16. BUG-013 — MEDIUM — help overlay does not explicitly disable underlying content

## Evidence

Plan requirement:

```text
while Help is visible underlying content must not receive tab/click actions
```

Current `ShowHelpOverlay()` only calls:

```csharp
helpOverlay.ShowOverlay();
```

The main content TableLayoutPanel is a local variable in `InitializeComponent()` and is never disabled.

MainForm does intercept Ctrl+R/Ctrl+M while help is visible, which is good, but this does not establish that Tab/access-key/focus navigation can never reach underlying controls.

## Required repair

Promote the root content panel to a field:

```csharp
private TableLayoutPanel pnlMainContent = null!;
```

Create it instead of local `mainPanel`.

MainForm methods:

```csharp
private void ShowHelpOverlay()
{
    pnlMainContent.Enabled = false;
    helpOverlay.ShowOverlay();
}

private void HideHelpOverlay()
{
    helpOverlay.HideOverlay();
    pnlMainContent.Enabled = true;
    btnHelp.Focus();
}
```

Give `HelpOverlayControl` a close event:

```csharp
public event EventHandler? CloseRequested;

private void RequestClose()
{
    CloseRequested?.Invoke(this, EventArgs.Empty);
}
```

Use it for Close/Esc instead of hiding itself without re-enabling content.

## Tests

```text
HelpVisible_DisablesMainContent
HelpClose_ReEnablesMainContent
HelpVisible_CtrlRDoesNothing
HelpVisible_CtrlMDoesNothing
HelpVisible_TabCannotFocusUnderlyingControls
```

---

# 17. BUG-014 — MEDIUM — redundant `IngameFilename` persisted contrary to plan

## Evidence

`_changePlan2.md` explicitly says:

```text
Do not store a redundant IngameFilename/IngamePath JSON property;
derive it deterministically from existing journal metadata.
```

Current `AssetSession` persists `IngameFilename` and then validates it against the value that can already be derived from:

```text
AssetFolderName
MainFilename
```

Tests even assert the redundant field.

## Why remove it

Every redundant journal field creates another corrupt/mismatched state to validate and migrate.

The canonical value is deterministic:

```csharp
AssetNaming.BuildIngameFilename(
    AssetFolderName,
    MainFilename)
```

## Required repair

Remove:

```csharp
public string? IngameFilename { get; set; }
```

Use:

```csharp
public string GetIngameFilename()
{
    if (string.IsNullOrWhiteSpace(AssetFolderName)
        || string.IsNullOrWhiteSpace(MainFilename))
    {
        return string.Empty;
    }

    return AssetNaming.BuildIngameFilename(
        AssetFolderName,
        MainFilename);
}
```

Remove assignments and redundant validation.

Compatibility note: existing v1.1 JSON containing `IngameFilename` remains readable because `System.Text.Json` ignores the now-unknown member by default.

Add a migration test that writes a v1.1-style JSON with `IngameFilename`, loads it after property removal and verifies `GetIngameFilename()` returns the same deterministic path.

---

# 18. BUG-015 — MEDIUM — `ProcessMainImage()` can write without an active Main journal

## Evidence

Current method binds metadata **only if**:

```csharp
session.IsMainCommitting
```

but does not require it.

Several tests call:

```csharp
var session = processor.ProcessReference(...);
processor.ProcessMainImage(session, ...);
```

without preparing/persisting Main commit metadata.

That means the public service itself permits a non-journaled write path even though the UI correctly journals Main before writes.

## Required repair

Fail closed:

```csharp
if (!session.IsMainCommitting)
{
    throw new InvalidOperationException(
        "ProcessMainImage requires an active, pre-journaled Main transaction.");
}
```

Then make the binding checks unconditional.

Every test that calls `ProcessMainImage()` directly must prepare realistic active Main metadata first.

### Test helper

```csharp
private static void PrepareMainCommit(
    AssetProcessorService processor,
    AssetSession session,
    string source,
    string prompt,
    DateTimeOffset at)
{
    session.IsMainCommitting = true;
    session.MainFilename = Path.GetFileName(source);
    session.MainPrompt = prompt;
    session.MainProcessedAt = at;
    session.MainHash = processor.ComputeSha256(source);
    session.MainTransactionId = Guid.NewGuid().ToString("N");
    session.WasIngameFolderCreatedByTool =
        !Directory.Exists(session.GetIngameFolderPath());
}
```

The application UI must still save `session.json` **before** calling `ProcessMainImage()`.

## Test

```csharp
[Fact]
public void ProcessMainImage_RejectsReferenceSessionWithoutActiveMainJournal()
{
}
```

---

# 19. BUG-016 — MEDIUM — Reference provenance says retained file is not retained

## Evidence

Production `templates/reference.md` contains:

```text
Reference file retained: no
```

But the Reference workflow deliberately copies and retains the Reference image under:

```text
<AssetName>/reference/<original filename>
```

This is a factual contradiction inside the provenance record.

## Required repair

Change the production template to:

```text
Reference file retained: yes
```

If the intended semantics were “original external download retained elsewhere”, rename the field to make that meaning explicit. As currently worded, `yes` matches the generated package.

Update exact provenance tests/snapshots accordingly.

---

# 20. BUG-017 — MEDIUM — README contains incorrect output filenames and shortcuts

## Incorrect trees

README currently documents:

```text
final.md
reference.md
final_no_reference.md
```

Runtime actually writes:

```text
license.txt — Final AI-Generated Asset.md
reference/license.txt — AI Reference Asset.md
```

Both workflow modes use the **same final provenance filename**; only the template content differs.

## Incorrect keyboard claims

README lists:

```text
Ctrl+Q / Alt+F4 -> Exit
F1 / ? -> Toggle Help Overlay
```

Current code has no Ctrl+Q handler. `?` is a button, not a keyboard shortcut, and F1 does not toggle while the overlay is already visible.

## Other misleading wording

README says “immediate previews”, while the cards currently show selected filename/timestamp rather than an image preview.

## Required README tree

Reference-assisted:

```text
<AssetRootFolder>/
└── <AssetName>/
    ├── <sourceMainFilename>.<ext>
    ├── license.txt — Final AI-Generated Asset.md
    ├── ingame/
    │   └── <AssetName>.<ext>
    └── reference/
        ├── <sourceReferenceFilename>.<ext>
        └── license.txt — AI Reference Asset.md
```

NoReference:

```text
<AssetRootFolder>/
└── <AssetName>/
    ├── <sourceMainFilename>.<ext>
    ├── license.txt — Final AI-Generated Asset.md
    └── ingame/
        └── <AssetName>.<ext>
```

Document only shortcuts actually implemented after BUG-011 is repaired.

---

# 21. BUG-018 — MEDIUM — CI/test gate missed new production surfaces and mandatory failure tests

## Evidence

CI’s coverage-presence check still enumerates mostly old top-level classes:

```text
MainForm.cs
AppBootstrap.cs
AssetProcessorService.cs
SessionService.cs
ValidationService.cs
SettingsService.cs
TemplateService.cs
ImageFinderService.cs
TwoChoiceDialog.cs
```

The plan explicitly required adding `AssetNaming` and other meaningful new production classes/surfaces.

Important new files are therefore not guaranteed by the CI presence gate, including examples such as:

```text
Services/AssetNaming.cs
Services/ValidationService.Paths.cs
Services/ValidationService.Session.cs
Services/AssetProcessorService.Main.cs
MainForm.MainWorkflow.cs
MainForm.Recovery.cs
```

## False-assurance examples in current tests

### Example 1 — exact final ownership exists but completion does not use it

Tests prove `ValidateExactFinalProvenanceOwnership()` rejects tampering, but no test proves `ValidateCompleteAsset()` rejects the same tampering.

### Example 2 — NoReference destructive validator test is too weak

The current NoReference test asserts that destructive validation works without a Reference filename, but it does not construct or validate the canonical `ingame` boundary. This allowed BUG-001 to survive.

### Example 3 — UI structural test checks `AutoScroll == false`, not whether content fits

A form with `MinimumSize.Height == 800` can pass `AutoScrollFalse` while still failing the mandatory 1366×768 target.

### Example 4 — drop test checks existence, not required placement/size

A large drop surface satisfies “control exists” but not the user’s explicit button-sized control requirement.

## Required CI coverage-presence update

At minimum add:

```powershell
$requiredProductionClasses = @(
  'AssetProvenanceHelper.MainForm',
  'AssetProvenanceHelper.AppBootstrap',
  'AssetProvenanceHelper.Services.AssetProcessorService',
  'AssetProvenanceHelper.Services.SessionService',
  'AssetProvenanceHelper.Services.ValidationService',
  'AssetProvenanceHelper.Services.SettingsService',
  'AssetProvenanceHelper.Services.TemplateService',
  'AssetProvenanceHelper.Services.ImageFinderService',
  'AssetProvenanceHelper.Services.AssetNaming',
  'AssetProvenanceHelper.Dialogs.TwoChoiceDialog'
)
```

Because partial-class source paths may be represented differently by Coverlet, do not rely only on class-name presence. Add targeted tests for the partial files/methods themselves.

## Mandatory new regression suite

Add these before acceptance:

```text
DestructiveValidation_RejectsIngameReparsePoint
RollbackMain_RefusesIngameJunction
FailedReferenceMain_ReconcilesViaRollbackMain
FailedNoReferenceMain_ReconcilesViaRollbackMain
FailedNoReferenceMain_SessionDeleteFailure_IsCritical
CompleteAsset_RejectsTamperedFinalProvenance
MainCommit_RejectsTamperedReferenceProvenance
Reference_RejectsImageExtensionInAssetName
CtrlR_ReferenceReady_ReplacesReference
Validation_PromptEditDoesNotClearMainImageError
HelpVisible_DisablesUnderlyingContent
Form_MinimumHeightFits1366x768Target
DropControls_AreButtonSizedAndInActionRows
ProcessMainImage_RequiresActiveMainJournal
ReferenceReplacement_RecoveryEachPersistentPhase
ReferenceCreation_RecoveryEachPersistentPhase
```

---

# 22. BUG-019 — MEDIUM — `ValidateImageFile()` does not validate image format

## Evidence

Current validation checks:

```text
path non-empty
file exists
extension is allowed
file length > 0
file can be opened
```

A text file renamed to `fake.png` passes.

Current tests reinforce this because `TestWorkspace.CreateImage()` writes arbitrary bytes such as `{1,2,3,4}`.

## Why this matters

The tool creates provenance records asserting that a selected artifact is an image. A corrupt or renamed non-image file should be rejected before it becomes canonical production output.

## Minimal dependency-free repair

Validate known signatures for the supported formats.

### Copy-ready signature check

```csharp
private static bool HasSupportedImageSignature(
    string path,
    string extension)
{
    Span<byte> header = stackalloc byte[12];

    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read);

    var read = stream.Read(header);

    return extension.ToLowerInvariant() switch
    {
        ".png" =>
            read >= 8
            && header[0] == 0x89
            && header[1] == 0x50
            && header[2] == 0x4E
            && header[3] == 0x47
            && header[4] == 0x0D
            && header[5] == 0x0A
            && header[6] == 0x1A
            && header[7] == 0x0A,

        ".jpg" or ".jpeg" =>
            read >= 3
            && header[0] == 0xFF
            && header[1] == 0xD8
            && header[2] == 0xFF,

        ".webp" =>
            read >= 12
            && header[0] == (byte)'R'
            && header[1] == (byte)'I'
            && header[2] == (byte)'F'
            && header[3] == (byte)'F'
            && header[8] == (byte)'W'
            && header[9] == (byte)'E'
            && header[10] == (byte)'B'
            && header[11] == (byte)'P',

        _ => false
    };
}
```

Call it after extension validation. Add:

```csharp
errors.Add(
    $"File does not contain a valid {extension} image signature: {path}");
```

### Important test migration

`TestWorkspace.CreateImage()` must begin writing format-appropriate header bytes; otherwise the test suite is testing “arbitrary bytes with an image extension”, not real supported inputs.

Do not use `System.Drawing.Image.FromStream()` as the only validator because native WebP support is not equivalent across the supported environment.

---

# 23. BUG-020 — MEDIUM — Refresh can propagate directory enumeration exceptions

## Evidence

`ImageFinderService.FindLatestImage()` calls `Directory.EnumerateFiles()` directly.

`RefreshImageSelection()` calls it without a surrounding exception handler.

A folder can exist yet enumeration can still fail because of:

```text
UnauthorizedAccessException
IOException
transient network/share errors
directory disappearing between Exists and EnumerateFiles
```

A WinForms event handler should not allow those exceptions to escape to the application message loop.

## Required repair

At UI boundary:

```csharp
string? latest;

try
{
    latest = _imageFinderService.FindLatestImage(settings);
}
catch (Exception ex) when (
    ex is IOException
    || ex is UnauthorizedAccessException)
{
    HighlightField(pnlDownloadFolderHost, true);
    ShowError(
        "Could not scan the Image Download Folder.",
        ex);
    return;
}
```

Also catch `DirectoryNotFoundException` through `IOException` when the folder disappears after validation.

Test with an injectable enumeration hook if manipulating Windows ACLs in unit tests would be unreliable.

---

# 24. BUG-021 — MEDIUM — exact provenance ownership depends on mutable template files

## Evidence

`ValidateExactReferenceProvenanceOwnership()` and `ValidateExactFinalProvenanceOwnership()` reconstruct expected text by reading/rendering the **current template**.

If a template changes while a session is still active — for example after app update, manual template edit, or version migration — an unchanged tool-owned provenance file can no longer be proven as owned.

Cancellation/rollback may then fail closed and trap the session.

## Recommended repair

Persist a cryptographic digest of the exact rendered provenance bytes in the journal at creation/precommit time.

Suggested fields:

```csharp
public string? ReferenceProvenanceHash { get; set; }
public string? MainProvenanceHash { get; set; }
```

Use SHA-256 over UTF-8 without BOM — exactly the bytes the writer emits.

Ownership validation order:

```text
new sessions with stored hash -> compare file SHA-256 to stored provenance hash
legacy sessions without hash -> fall back to current exact-template reconstruction
```

This preserves legacy compatibility while making future recovery independent of mutable template text.

This repair pairs naturally with BUG-006’s prewrite Reference journal and BUG-015’s stricter Main preparation.

---

# 25. BUG-022 — LOW — smoke startup timeout was reduced to 5 seconds

The published self-contained Windows smoke test now allows only:

```powershell
$timeoutMs = 5000
```

Self-contained cold start on a busy GitHub-hosted Windows VM can legitimately exceed 5 seconds. The older safety margin was larger.

Use at least:

```powershell
$timeoutMs = 15000
```

and keep the 250 ms poll interval.

Also record actual startup milliseconds in `smoke-test-log.json` so regressions are visible.

Add icon verification because the plan explicitly included the app icon in release acceptance:

```powershell
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon((Resolve-Path $exePath).Path)
if ($null -eq $icon) {
    throw "Published executable has no extractable application icon."
}
$icon.Dispose()
```

---

# 26. BUG-023 — LOW — final plan cleanup/static-search requirement did not land

Plan final static search required:

```powershell
rg -n "_latestImagePath|_manualSelectionPath|ResolveImageSelection" src
```

Expected: zero.

The first two shared fields are correctly gone, but `MainForm.ImageSelection.cs` still contains a legacy:

```csharp
private string? ResolveImageSelection()
{
    return GetSelectedImage(ImageSlot.Reference);
}
```

There are also legacy aliases such as:

```text
btnRefresh
btnChooseFile
btnOpenDownloads
lblLatestImage
lblLatestTimestamp
lblManualSelection
```

These aliases are not required for runtime behavior and preserve the old single-slot vocabulary in a dual-slot codebase.

Remove them and update old reflection-based tests to assert current controls directly.

Do this only after the functional fixes so cleanup does not obscure transactional changes.

---

# 27. BUG-024 — LOW — required status diagnostics are incomplete

Plan §32 asked for explicit status lines such as:

```text
Ingame copy created: <AssetName.ext>
No-reference Main transaction prepared.
Main candidate and prompt cleared because the Reference changed.
```

Current success path logs Main image + final provenance + asset completion but omits the explicit ingame copy line; NoReference preparation and replacement-clear diagnostics are also absent.

Add them. These are useful when diagnosing exactly which stage occurred before a failure.

Example:

```csharp
AddStatus($"Main image copied: {committedFilename}");
AddStatus($"Ingame copy created: {session.GetIngameFilename()}");
AddStatus("Final provenance created.");
AddStatus("Asset completed.");
```

---

# 28. Plan-to-implementation acceptance matrix

| Requirement | Result | Notes |
|---|---|---|
| Remove Project input | **PASS** | visible input removed |
| Remove Project from AppSettings | **PASS** | old JSON remains compatible |
| Derived Project label | **PASS** | derived from Asset Root |
| Rename Firefox folder wording | **PASS** | browser-neutral label/dialog |
| Newest supported image independent of ChatGPT filename | **PASS** | old filename priority removed |
| Download folder optional for Choose/Drop | **PASS** | processing settings allow blank download |
| Asset Name without image extension | **FAIL/PARTIAL** | NoReference service correct; Reference/UI bypass validator |
| Reference creates `<asset>/reference` and provenance | **PASS** | normal path correct |
| Root Main preserves original filename | **PASS** | implemented |
| `ingame/<AssetName>.<ext>` | **PASS normal path** | rollback path safety incomplete |
| Same bytes/hash root + ingame | **PASS normal path** | implementation checks hashes |
| Reject other ingame extension variant | **PASS normal preflight** | race hardening still possible |
| NoReference has no Reference folder/fields | **PASS normal path** | journaling mode exists |
| NoReference journal before folder creation | **PASS** | mode-aware session validation supports it |
| Separate Reference/Main selections | **PASS** | independent backing fields |
| Separate Refresh/Choose | **PASS** | implemented |
| Button-sized Drop alongside actions | **FAIL** | large drop area substituted |
| Main CTA no implicit Refresh | **PASS** | selected slot only |
| explicit Paste Clipboard only | **PASS** | no automatic clipboard prompt |
| Reference replacement clears Main + Prompt | **PASS** | success paths clear them |
| Ctrl+R = Reference/Replace | **FAIL/PARTIAL** | works only in Idle |
| NoReference hides Reference card | **PASS** | Main expands |
| red field outlines | **PASS/PARTIAL** | overbroad clearing bug |
| bounded CTA pulse | **PASS** | ~8 ticks @175 ms |
| top settings/current asset/cards/status grouping | **PASS directionally** | layout exists |
| no main-window scrolling | **FAIL acceptance target** | min form height exceeds 768 display |
| header product/version | **PASS** | `AppInfo` used |
| SVG + app icon | **PASS** | SVG + ICO committed |
| `?` help | **PASS/PARTIAL** | overlay present; underlying content not explicitly disabled |
| “Made by CeeGore” | **PASS** | present |
| workflow-mode-aware destructive validation | **FAIL** | `ingame` omitted |
| failed-Main reconciliation helper | **FAIL** | plan §31.1 not implemented |
| exact final completion | **FAIL** | substring complete gate |
| legacy v1 Main without ingame rolls back | **PASS directionally** | recovery treats missing ingame as incomplete |
| no redundant IngameFilename | **FAIL** | persisted property added |
| dynamic release archive | **PASS** | ProductVersion used |
| smoke verifies 3 templates | **PASS** | all templates checked |
| CI presence updated for new code | **FAIL/PARTIAL** | AssetNaming/new partial surfaces absent |
| README exact final tree | **FAIL** | wrong provenance filenames |

---

# 29. Additional paranoid race/edge-case checks

These are not all separate blockers, but they must be covered while repairing the above.

## 29.1 Ingame variant race

`FindExistingIngameVariants()` runs before final promotion. Another process can create a different-extension variant after preflight.

The exact destination promotion is protected by `overwrite:false`, but a *different* extension could coexist.

For a hobby single-user app this is low probability, but a paranoid final check should re-scan supported variants immediately before promoting the canonical ingame file.

## 29.2 Reparse swap after preflight

Even after BUG-001, recheck the fixed `ingame` directory immediately before copy/promotion/deletion. A single early check cannot fully protect against a path swapped later in the transaction.

## 29.3 Reference session save failure after output

Until BUG-006 is implemented, treat incomplete rollback after session-save failure as CRITICAL and close. Do not continue in the same process.

## 29.4 Existing asset folder with user files

Keep current safe rule:

- never recursively delete a preexisting Asset folder;
- never recursively delete `ingame`;
- only delete exact owned files after hash/content verification;
- only remove a directory if this session created it and it is empty.

## 29.5 Source files

Every new test must continue asserting source files remain after success and rollback/failure attempts unless the user manually deleted them. Do not introduce `File.Move` for source inputs.

---

# 30. Recommended repair order for a weaker implementation model

Do **not** fix all files simultaneously. Use this sequence so each phase remains reviewable.

## PHASE R1 — destructive path safety

Files:

```text
ValidationService.Paths.cs
AssetProcessorService.Main.cs
ChangeV11NoReferenceTests.cs / new safety tests
```

Implement BUG-001 only.

Gate:

```text
all existing path/security tests
new ingame reparse tests
no destructive operation crosses expected parent
```

## PHASE R2 — exact provenance gates

Files:

```text
ValidationService.Session.cs
AssetProcessorService.Main.cs
MainForm.MainWorkflow.cs
MainForm.Recovery.cs
provenance tests
```

Implement BUG-004 + BUG-005 + blank MainPrompt validation.

Gate:

```text
exact Reference tamper rejected
exact Final tamper rejected
startup never deletes journal for modified provenance
```

## PHASE R3 — Main failure reconciliation

Files:

```text
MainForm.MainWorkflow.cs
AssetProcessorService.Main.cs
SessionService test hooks if necessary
failure-injection tests
```

Implement BUG-002 + BUG-003.

Gate every failure boundary.

## PHASE R4 — Reference crash journal

Implement BUG-006.

Do not mix replacement yet.

Gate hard-crash-state simulations.

## PHASE R5 — persistent Reference replacement transaction

Implement BUG-007.

Gate every replacement phase/recovery state.

## PHASE R6 — naming + session cleanup

Implement:

```text
BUG-009
BUG-014
BUG-015
BUG-023
```

Run full test suite after the session schema changes.

## PHASE R7 — compact GUI + requested controls

Implement:

```text
BUG-008
BUG-010
BUG-011
BUG-012
BUG-013
BUG-024
```

Run automated structural tests plus full manual display matrix.

## PHASE R8 — provenance/docs/robustness

Implement:

```text
BUG-016
BUG-017
BUG-019
BUG-020
BUG-021
```

If BUG-019 is adopted, update `TestWorkspace.CreateImage()` and rerun **every** test because many historical tests currently use arbitrary bytes as fake images.

## PHASE R9 — CI/release hardening

Implement:

```text
BUG-018
BUG-022
```

Then run the complete final gate below.

---

# 31. Required transaction-state test matrix

## 31.1 Reference create

For each boundary simulate process restart/state reconciliation:

| Boundary | Expected result |
|---|---|
| journal saved, no folder | remove clean prepared journal |
| asset folder created | remove owned empty folder + journal |
| reference folder created | remove owned empty folders + journal |
| Reference copied | exact hash rollback or promote if full output later exists |
| provenance written | if both exact outputs exist, resume ReferenceReady |
| final Reference session save | normal ReferenceReady |

Unknown/tampered file => fail closed.

## 31.2 Reference replacement

Test the persistent phases from BUG-007.

## 31.3 Main ReferenceAssisted

Failure points:

```text
journal saved
asset/ingame folder preparation
source hash
Main temp copy
Main temp validation
Main temp ingame copy
provenance temp write
final provenance promotion
root Main promotion
ingame promotion
complete validation
session deletion
```

Every clean failure returns exact ReferenceReady state.

## 31.4 Main NoReference

Same points, but clean rollback returns exact Idle state and removes the no-ref journal.

## 31.5 Cancel

Retain all existing prepared/files-renamed cancellation tests and add a regression after failed Main to guarantee there is no leftover owned `ingame` directory preventing Asset folder cleanup.

---

# 32. Required filesystem invariants after every automated failure test

Use a helper that enumerates the target tree and asserts exact expected paths.

### ReferenceReady expected tree

```text
<asset>/reference/<ref filename>
<asset>/reference/license.txt — AI Reference Asset.md
```

No Main-related files or temps.

### Completed expected tree

```text
<asset>/<original Main filename>
<asset>/license.txt — Final AI-Generated Asset.md
<asset>/ingame/<AssetName>.<ext>
```

plus Reference subtree only in ReferenceAssisted mode.

### Clean NoReference rollback expected tree

If the tool created the asset directory and it contained nothing else:

```text
<asset> does not exist
```

If the directory preexisted:

```text
all preexisting user content preserved
all tool-owned Main/provenance/ingame/temp outputs absent
```

---

# 33. Suggested reusable test helpers

## 33.1 Prepare an active Reference-assisted Main journal

```csharp
internal static void PrepareActiveMainJournal(
    AssetProcessorService processor,
    AssetSession session,
    string mainSource,
    string prompt,
    DateTimeOffset processedAt)
{
    session.IsMainCommitting = true;
    session.MainFilename = Path.GetFileName(mainSource);
    session.MainPrompt = prompt;
    session.MainProcessedAt = processedAt;
    session.MainHash = processor.ComputeSha256(mainSource);
    session.MainTransactionId = Guid.NewGuid().ToString("N");
    session.WasIngameFolderCreatedByTool =
        !Directory.Exists(session.GetIngameFolderPath());
}
```

After BUG-014, do not set `IngameFilename`.

## 33.2 Assert no Main residue

```csharp
internal static void AssertNoMainResidue(
    AssetSession session)
{
    Assert.False(
        File.Exists(
            Path.Combine(
                session.AssetFolder,
                AppConstants.FinalProvenanceFileName)));

    if (!string.IsNullOrWhiteSpace(session.MainFilename))
    {
        Assert.False(
            File.Exists(
                Path.Combine(
                    session.AssetFolder,
                    session.MainFilename)));
    }

    var ingame = session.GetIngameImagePath();
    if (!string.IsNullOrWhiteSpace(ingame))
    {
        Assert.False(File.Exists(ingame));
    }

    var tempMain = session.GetMainTempImagePath();
    var tempProv = session.GetMainTempProvenancePath();
    var tempIngame = session.GetMainTempIngamePath();

    if (!string.IsNullOrWhiteSpace(tempMain)) Assert.False(File.Exists(tempMain));
    if (!string.IsNullOrWhiteSpace(tempProv)) Assert.False(File.Exists(tempProv));
    if (!string.IsNullOrWhiteSpace(tempIngame)) Assert.False(File.Exists(tempIngame));
}
```

Call before resetting Main metadata if the helper relies on the transaction ID for temp paths.

## 33.3 Exact tree assertion

Prefer an exact normalized set comparison instead of a few `File.Exists` assertions. This catches unexpected leftovers such as `.old` or `.tmp` files.

```csharp
internal static void AssertTreeEquals(
    string root,
    params string[] expectedRelativePaths)
{
    var actual = Directory.Exists(root)
        ? Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray()
        : Array.Empty<string>();

    var expected = expectedRelativePaths
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Assert.Equal(expected, actual);
}
```

Remember: directories are included by `EnumerateFileSystemEntries`, so include expected directory entries or create a separate files-only helper.

---

# 34. Required manual workflows after repairs

## 34.1 Reference-assisted — Refresh

1. Asset Root valid.
2. Image Download Folder valid.
3. Asset Name `onboarding1`.
4. Refresh Reference.
5. Reference CTA.
6. verify source remains in download folder.
7. verify exact Reference subtree/provenance.
8. Refresh Main independently.
9. enter Final Prompt manually.
10. Main CTA.
11. verify root Main source filename preserved.
12. verify `ingame/onboarding1.ext`.
13. compare SHA-256.
14. verify exact final provenance.
15. verify `session.json` deleted.

## 34.2 Reference-assisted — Choose File

Repeat with Image Download Folder blank.

Both Reference and Main must work.

## 34.3 Reference-assisted — Drop

Repeat using the button-sized Drop control for each slot.

## 34.4 Reference replacement

1. save Reference A;
2. select Main candidate + enter prompt;
3. select Reference B;
4. Replace Reference;
5. verify Main candidate and prompt cleared;
6. force-close at every persistent replacement phase in dedicated recovery tests;
7. normal completion with fresh Main.

## 34.5 NoReference

1. blank Image Download Folder;
2. enable No reference mode;
3. Reference UI fully hidden;
4. Asset Name;
5. Main via Choose;
6. Final Prompt;
7. Main CTA;
8. no `reference` folder created;
9. root Main + final provenance + ingame exact;
10. session deleted.

Repeat via Drop and Refresh.

## 34.6 Existing destination

Test both workflows with:

```text
existing empty folder
existing folder containing unrelated user file
existing final provenance collision
existing root Main collision
existing ingame same-extension collision
existing ingame different-extension collision
```

No existing user file may be overwritten or deleted.

## 34.7 Tamper tests

Before Main / before recovery:

```text
modify Reference image bytes
modify Reference provenance only
append harmless-looking line to Reference provenance
modify root Main
modify ingame Main
append line to final provenance
replace ingame folder with reparse point
```

Every destructive path must fail closed on unknown state.

---

# 35. Final static source searches

Run from repository root after repair:

```powershell
rg -n "Firefox" .
```

Expected: zero.

```powershell
rg -n "txtProject" src tests
```

Expected: zero.

```powershell
rg -n "_latestImagePath|_manualSelectionPath|ResolveImageSelection" src
```

Expected: zero.

```powershell
rg -n "IngameFilename" src tests
```

Expected after BUG-014: only legacy migration-test JSON/string fixtures if deliberately retained.

```powershell
rg -n "AssetProvenanceHelper-v1\.0\.0|v1\.0\.0" .
```

Expected: zero obsolete release hard-coding.

```powershell
rg -n "final\.md|reference\.md|final_no_reference\.md" README.md
```

Expected: only descriptions of **template source files**, never claims that these are canonical generated output filenames.

```powershell
rg -n "Ctrl\+Q|Ctrl \+ Q" src README.md
```

Expected: zero unless Ctrl+Q is intentionally implemented and tested.

---

# 36. Full Windows build/test gate

Run on Windows with the pinned SDK from `global.json`:

```powershell
dotnet --version
# MUST be 8.0.418

dotnet tool restore

dotnet restore AssetProvenanceHelper.sln

dotnet build AssetProvenanceHelper.sln `
  -c Debug `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Debug `
  --no-build `
  --logger "console;verbosity=normal"

dotnet build AssetProvenanceHelper.sln `
  -c Release `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Release `
  --no-build `
  --logger "console;verbosity=normal"
```

## 36.1 Flakiness loop

```powershell
for ($i = 1; $i -le 20; $i++) {
    Write-Host "=== Release flakiness run $i / 20 ==="

    dotnet test AssetProvenanceHelper.sln `
      -c Release `
      --no-build `
      --logger "console;verbosity=minimal"

    if ($LASTEXITCODE -ne 0) {
        throw "Flakiness run $i failed."
    }
}
```

Acceptance: **20/20 pass**.

## 36.2 Publish

```powershell
Remove-Item artifacts/publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish `
  src/AssetProvenanceHelper/AssetProvenanceHelper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/publish
```

## 36.3 Smoke

```powershell
pwsh scripts/run_smoke_tests.ps1 `
  -PublishDir artifacts/publish `
  -LogOutputDir artifacts
```

Verify:

```text
exe starts
main window appears
window title exact
3 templates present
icon present
graceful CloseMainWindow works
dynamic v1.1.0 archive created
smoke JSON says PASS
```

## 36.4 Coverage

```powershell
dotnet test `
  tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts/coverage
```

Do not accept “coverage file exists” as enough. Inspect that newly repaired transaction/recovery classes have meaningful line/branch execution.

---

# 37. Definition of Done for the repair round

The next implementation is accepted only when **all** of the following are true:

```text
[ ] BUG-001 fixed; ingame + temp Main paths are validated and reparse-safe.
[ ] BUG-002 fixed; ordinary Main failures reconcile through RollbackMain.
[ ] BUG-003 fixed; no NoReference journal deletion failure is swallowed.
[ ] BUG-004 fixed; complete final provenance is exact, not substring-only.
[ ] BUG-005 fixed; Reference provenance must be exact before Main/recovery acceptance.
[ ] BUG-006 fixed or explicitly implemented with a durable Reference prewrite journal.
[ ] BUG-007 fixed with persistent Reference-replacement transaction/recovery.
[ ] 1366×768 @100% passes without main-window scrolling/clipping.
[ ] Reference and NoReference both enforce extensionless Asset Name.
[ ] button-sized Drop file here controls exist in both action rows.
[ ] Ctrl+R performs Reference/Replace according to current state.
[ ] validation errors clear only when their own input changes.
[ ] Help prevents interaction with underlying content.
[ ] redundant IngameFilename is removed from active session schema.
[ ] ProcessMainImage refuses a non-journaled Main call.
[ ] Reference provenance retention wording is factually correct.
[ ] README matches exact runtime trees/shortcuts.
[ ] CI coverage-presence/test matrix includes new production surfaces.
[ ] image signature validation decision is resolved and tests reflect real files.
[ ] Refresh cannot crash on ordinary folder enumeration I/O errors.
[ ] provenance ownership survives template changes via stored digest, or this is explicitly deferred with a documented compatibility limitation.
[ ] smoke timeout/icon checks hardened.
[ ] legacy single-slot wrappers removed.
[ ] status history identifies ingame/recovery stages.
[ ] all new targeted regression tests pass.
[ ] all existing historical regression/security tests still pass.
[ ] Debug build passes with -warnaserror.
[ ] Debug tests pass.
[ ] Release build passes with -warnaserror.
[ ] Release tests pass.
[ ] Release tests pass 20/20 in flakiness loop.
[ ] self-contained win-x64 publish passes.
[ ] smoke test passes.
[ ] coverage collection/gates pass.
[ ] full manual GUI matrix passes.
[ ] Reference E2E passes via Refresh, Choose, Drop.
[ ] Main E2E passes via Refresh, Choose, Drop.
[ ] Reference replacement E2E + crash-phase recovery pass.
[ ] NoReference E2E + crash-phase recovery pass.
[ ] cancellation/restart/recovery/tamper tests pass.
[ ] no source input is moved/deleted.
[ ] no unknown/preexisting user file is overwritten/deleted.
[ ] no stale .tmp/.old/session transaction residue remains after clean success/failure.
```

---

# 38. Final recommendation

Do **not** discard the current v1.1.0 implementation. Most of the structural rework is usable and the existing safety code provides a strong base.

The correct next step is a **targeted repair release** centered on:

1. path confinement for `ingame`;
2. exact provenance validation;
3. durable reconciliation of Main failures;
4. durable Reference/replacement crash journals;
5. explicit UI requirement corrections;
6. stronger adversarial tests that validate real state transitions instead of only happy-path structure.

After those repairs, run the complete Windows gate in §36 and the manual matrices in §§31–34. Only a full issue-free pass should be considered final acceptance.

# AI Asset Provenance Helper — Rework Implementation Plan v2

**File:** `_changePlan2.md`  
**Supersedes:** `_changePlan1.md`  
**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Current application:** .NET 8 / Windows Forms / v1.0.0  
**Target application version:** **v1.1.0**  
**Purpose:** implementation-ready rework plan for weaker implementation models, with explicit compatibility, transaction, recovery, GUI, testing, and packaging rules.

---

# 0. Audit verdict on `_changePlan1.md`

`_changePlan1.md` had the correct overall direction, but it was **not yet safe to hand to a weak implementation model unchanged**. A deeper pass against the repository found several hidden contradictions and under-specified transitions that could have produced regressions despite the plan sounding complete.

This v2 resolves them.

## 0.1 Important defects found in v1 and fixed here

1. **No-reference precommit was internally impossible as specified.**  
   The proposed `CreateNoReferenceSession()` saved a session before creating the asset folder, but the existing `ValidateSession()` requires `session.AssetFolder` to exist. `ProcessMainImage()` starts by validating the session, so the proposed flow would fail before doing useful work.  
   **Fix:** introduce mode-aware Main-commit validation that permits an absent asset directory only for a journaled `NoReference` Main transaction whose paths are otherwise safe. The directory is then created *after* `session.json` is durable.

2. **Manual file selection was accidentally still dependent on Image Download Folder.**  
   v1 treated Image Download Folder as globally required. That means `Choose File...` or drag/drop would still fail if the download folder were empty, unavailable, or intentionally unused.  
   **Fix:** Image Download Folder is required only for `Refresh` / `Open Image Folder`; manually selected or dropped files can be processed without it.

3. **`Project` was not actually removed cleanly.**  
   v1 kept `AppSettings.ProjectName` as a hidden compatibility setting. That leaves a stale, user-invisible setting and does not fully satisfy “remove this field.”  
   **Fix:** remove `ProjectName` from **AppSettings** completely. Old `settings.json` files containing it remain loadable because unknown JSON members are ignored. Keep `AssetSession.ProjectName` only because active/legacy reference sessions and exact provenance ownership checks need it. New sessions derive the provenance project label deterministically from the Asset Root folder name; the user never enters it.

4. **No-reference rollback was not fully connected to the current destructive-path validator.**  
   Existing `ValidateSessionPathsForDestructiveOperation()` assumes a Reference filename/path. A no-reference Main rollback would therefore fail or require unsafe special casing.  
   **Fix:** make destructive path validation workflow-mode-aware and explicitly validate `asset`, root Main, final provenance, `ingame`, and deterministic temp paths for NoReference mode.

5. **Clean failure behavior for NoReference mode was missing.**  
   A Reference-assisted Main failure can return to the saved Reference session. A NoReference Main failure has no Reference state to resume.  
   **Fix:** after a fully verified successful rollback of a NoReference Main failure, delete the no-reference session journal and return to Idle. Preserve and close on incomplete/untrusted rollback, matching the existing conservative safety model.

6. **Reference replacement could create incorrect provenance relationships.**  
   If a Main image and prompt are already selected, then the Reference is replaced, the old Main selection can now be committed while the final provenance points at the new Reference.  
   **Fix:** after every successful Reference replacement, clear the Main image selection **and** Final Prompt. Require a fresh Main selection/prompt.

7. **`ingame` collision checking was too narrow.**  
   Checking only `ingame/onboarding1.jpg` misses an existing `ingame/onboarding1.png`, leaving multiple production variants for one logical asset.  
   **Fix:** before Main commit, reject any supported-image `ingame/<AssetName>.*` variant, regardless of extension, unless it is the exact deterministic file belonging to the active recovery transaction.

8. **The folder creation moment was ambiguous.**  
   “From Asset Root + Asset Name all production folders are automatically created” could be interpreted as filesystem mutation while typing.  
   **Fix:** paths are deterministically known immediately, but folders are created **lazily by the relevant CTA**. Reference creates `<asset>/reference`; Main creates `<asset>/ingame`. No-reference never creates `reference`.

9. **Input error-to-control mapping was under-specified.**  
   Parsing validation error strings to decide which textbox becomes red would be brittle.  
   **Fix:** use action-specific UI validation methods with explicit control associations. Never infer a GUI field by parsing error text.

10. **The help requirement asked for an overlay, but v1 substituted a separate modal window.**  
    **Fix:** implement a real in-form `HelpOverlayControl`, docked over the application content, with `?`, close button, and Esc handling.

11. **Branding introduced an avoidable runtime PNG asset.**  
    **Fix:** keep SVG as canonical design source and compile a single `.ico`. Use the form/application icon converted to a bitmap for the header; no separate runtime logo PNG is required.

12. **A complete Main asset was not defined strongly enough.**  
    With the new workflow, root Main + provenance is no longer sufficient.  
    **Fix:** “complete” now means root Main + exact final provenance + `ingame` copy + all expected hashes, plus Reference artifacts in Reference-assisted mode.

13. **Existing-folder behavior for NoReference mode was not defined.**  
    **Fix:** reuse one explicit “Use Existing Folder / Cancel” policy for both workflows, then let processor preflight reject any managed-path collision. Never silently overwrite.

14. **Reference/Main candidate lifecycle was incomplete.**  
    **Fix:** after saving a Reference, clear the Reference *candidate* selection while separately displaying the saved Reference. A replacement requires a newly selected candidate. After asset completion clear Main candidate, prompt, and Asset Name.

15. **Version/package tests needed stronger integration.**  
    **Fix:** package naming derives from the compiled product version, smoke test verifies all three templates and app icon/window startup, and new production classes are added to coverage-presence checks where appropriate.

**Result:** this v2 is the authoritative plan for the rework. Do not implement from v1.

---

# 1. Non-negotiable product behavior

The implementation model must treat this section as authority.

## 1.1 Persistent settings

The application has only two user-configurable persistent path settings:

```text
Image Download Folder
Asset Root Folder
```

There is **no Project input**.

`Image Download Folder` may be blank if the user only uses `Choose File...` or drag/drop.

`Asset Root Folder` is required for every provenance-writing operation.

## 1.2 Per-asset inputs

For every new asset:

```text
Asset Name
No reference mode [checkbox]
```

Example Asset Name:

```text
onboarding1
```

The Asset Name:

- is entered **without** an image extension;
- becomes the asset directory name;
- becomes the filename stem of the production copy under `ingame`;
- must be a single safe Windows filename/folder segment;
- may contain a non-image dot suffix such as `asset.v2`;
- must not be a reserved Windows device name.

## 1.3 Reference-assisted final tree

Input:

```text
Asset Root Folder = D:\gameassets\gamename
Asset Name        = onboarding1
Reference source  = ChatGPT ref.png
Main source       = ChatGPT final.jpg
```

Final tree:

```text
D:\gameassets\gamename\
└── onboarding1\
    ├── ChatGPT final.jpg
    ├── license.txt — Final AI-Generated Asset.md
    ├── reference\
    │   ├── ChatGPT ref.png
    │   └── license.txt — AI Reference Asset.md
    └── ingame\
        └── onboarding1.jpg
```

Rules:

- Reference source is copied, never moved.
- Main source is copied, never moved.
- root Main keeps the source filename exactly.
- `ingame` copy changes only the stem to Asset Name.
- the source extension is preserved exactly (`.jpg`, `.JPG`, `.webp`, etc.).
- root Main and `ingame` Main must be byte-identical and SHA-256-identical.

## 1.4 No-reference final tree

```text
D:\gameassets\gamename\
└── onboarding1\
    ├── ChatGPT final.jpg
    ├── license.txt — Final AI-Generated Asset.md
    └── ingame\
        └── onboarding1.jpg
```

There must be no tool-created:

```text
onboarding1\reference
```

NoReference mode uses a provenance template that does not pretend a reference asset exists.

## 1.5 Lazy folder creation

Typing Asset Name or choosing Asset Root **does not touch the filesystem**.

Folders are created only as transaction work begins:

```text
Reference CTA -> create asset folder if needed + reference folder
Main CTA      -> create asset folder if needed + ingame folder
```

This avoids empty folders and makes NoReference behavior unambiguous.

---

# 2. Safety invariants — do not weaken these

The current repository contains strong rollback/recovery safeguards. All changes must preserve them.

1. Never move or delete the source image selected by the user.
2. Never silently overwrite any canonical provenance/image output.
3. Continue hashing Reference and Main bytes with SHA-256.
4. A promoted file may only be removed during rollback after exact ownership verification.
5. A provenance file may only be destructively handled after exact rendered-text ownership verification.
6. Continue rejecting unsafe reparse-point destination directories.
7. Main transaction journal state must be durable **before the first Main output mutation**.
8. NoReference mode must use the same crash-journal principle.
9. Unknown or externally modified files must be preserved; fail closed instead of deleting them.
10. `ingame` is part of the Main transaction, not a post-processing convenience step.
11. `ingame` canonical content must be included in completion/recovery verification.
12. NoReference mode must not fabricate empty Reference fields and then pass Reference validation.
13. An existing `ingame` directory must be rejected if it is a junction/symlink/reparse point.
14. Main submission must use the explicitly displayed Main selection. It must not silently Refresh during the CTA handler.
15. Reference submission/replacement must use the explicitly displayed Reference candidate.
16. Main submission must never silently consume arbitrary clipboard text. Clipboard is used only through the visible Paste action.
17. Reference replacement invalidates Main candidate + prompt and clears both.
18. Existing production variants `ingame/<AssetName>.<supported-image-extension>` block a new commit regardless of extension.
19. NoReference precommit may legitimately have no asset folder yet; this is valid only while its Main journal is active and all path relationships are safe.
20. If a NoReference Main failure rolls back cleanly, remove `session.json`; if rollback is incomplete/untrusted, preserve the session and close with a critical error.

---

# 3. Compatibility strategy

Do not introduce a broad schema migration framework. Use additive/default-compatible model changes.

## 3.1 Remove Project from AppSettings completely

Current `AppSettings.ProjectName` must be deleted.

New model:

```csharp
using AssetProvenanceHelper;

namespace AssetProvenanceHelper.Models;

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = string.Empty;

    public string AssetRootFolder { get; set; } = string.Empty;

    public List<string> AcceptedExtensions { get; set; } =
        AppConstants.DefaultImageExtensions.ToList();
}
```

Old settings such as:

```json
{
  "ProjectName": "OldGame",
  "DownloadFolder": "C:\\Users\\Me\\Downloads",
  "AssetRootFolder": "D:\\gameassets\\OldGame",
  "AcceptedExtensions": [".png", ".webp", ".jpg", ".jpeg"]
}
```

must still load. `System.Text.Json` ignores the now-unknown `ProjectName` property by default.

Add an explicit regression test for this.

## 3.2 Keep AssetSession.ProjectName

Do **not** remove:

```csharp
AssetSession.ProjectName
```

It is part of the exact provenance/recovery state for legacy and active reference sessions.

New sessions derive it from Asset Root Folder; old sessions keep their persisted value unchanged.

## 3.3 Derive new-session project label

Add:

```text
src/AssetProvenanceHelper/Services/AssetNaming.cs
```

Copy-ready implementation:

```csharp
namespace AssetProvenanceHelper.Services;

public static class AssetNaming
{
    public static string DeriveProjectLabel(
        string assetRootFolder)
    {
        if (string.IsNullOrWhiteSpace(assetRootFolder))
        {
            return string.Empty;
        }

        var normalized =
            ValidationService.NormalizePath(assetRootFolder);

        var directory =
            new DirectoryInfo(normalized);

        if (!string.IsNullOrWhiteSpace(directory.Name))
        {
            return directory.Name;
        }

        return normalized;
    }

    public static string BuildIngameFilename(
        string assetName,
        string mainFilename)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            throw new ArgumentException(
                "Asset name must not be empty.",
                nameof(assetName));
        }

        if (string.IsNullOrWhiteSpace(mainFilename))
        {
            throw new ArgumentException(
                "Main filename must not be empty.",
                nameof(mainFilename));
        }

        return assetName
            + Path.GetExtension(mainFilename);
    }
}
```

`ProcessReference()` itself must call `DeriveProjectLabel()`. Do not rely on MainForm to populate a hidden value.

NoReference session creation does the same.

## 3.4 WorkflowMode compatibility

Add:

```csharp
public enum AssetWorkflowMode
{
    ReferenceAssisted = 0,
    NoReference = 1
}
```

And:

```csharp
public AssetWorkflowMode WorkflowMode { get; set; }
    = AssetWorkflowMode.ReferenceAssisted;
```

**`ReferenceAssisted` must remain numeric zero.**

Old v1.0 `session.json` files contain no WorkflowMode and therefore deserialize safely as ReferenceAssisted.

## 3.5 Legacy Main commit without ingame

A v1.0 session may have:

```text
IsMainCommitting = true
root Main exists
final provenance exists
no ingame file (v1.0 did not create one)
```

v1.1 must treat this as an **incomplete v1.1 Main transaction**:

1. verify legacy Reference baseline;
2. verify/delete exact owned Main/provenance via normal rollback;
3. reset Main commit metadata;
4. save the Reference session;
5. allow the user to recommit Main under v1.1, creating `ingame`.

Do not declare the old Main complete simply because root Main + provenance exist.

## 3.6 Completed v1.0 assets

Completed assets have no active session. Do not migrate or modify them automatically.

---

# 4. Validation architecture

The current single `ValidateSettings()` mixes unrelated concerns. Split its semantics without duplicating security rules.

## 4.1 Processing settings

Create:

```csharp
public ValidationResult ValidateProcessingSettings(
    AppSettings settings)
```

It validates:

```text
Asset Root Folder non-empty
Asset Root Folder exists
Asset Root Folder not a reparse point
AcceptedExtensions valid/non-empty
```

If DownloadFolder is non-empty and exists, continue applying the current “Download vs AssetRoot cannot be same/nested” safety check.

Do **not** require DownloadFolder to be populated for processing.

## 4.2 Download-folder action validation

Create:

```csharp
public ValidationResult ValidateDownloadFolder(
    AppSettings settings)
```

Copy-ready:

```csharp
public ValidationResult ValidateDownloadFolder(
    AppSettings settings)
{
    ArgumentNullException.ThrowIfNull(settings);

    if (string.IsNullOrWhiteSpace(settings.DownloadFolder))
    {
        return ValidationResult.Failure(
            "Image Download Folder must not be empty for Refresh/Open Folder.");
    }

    if (!Directory.Exists(settings.DownloadFolder))
    {
        return ValidationResult.Failure(
            $"Image Download Folder does not exist: {settings.DownloadFolder}");
    }

    return ValidationResult.Success();
}
```

`Refresh` and `Open Image Folder` call this.

`Choose File...`, drag/drop, Reference CTA, and Main CTA do not require a valid download folder if a valid selection already exists.

## 4.3 Keep compatibility wrapper if useful

If many existing tests/services call `ValidateSettings()`, retain it as:

```csharp
public ValidationResult ValidateSettings(
    AppSettings settings) =>
    ValidateProcessingSettings(settings);
```

This minimizes churn while giving the GUI the specific DownloadFolder validator it needs.

## 4.4 Asset Name validation

Add:

```csharp
public ValidationResult ValidateAssetName(
    string name,
    IReadOnlyCollection<string> acceptedExtensions)
{
    var baseValidation =
        ValidateAssetFolderName(name);

    var errors =
        baseValidation.Errors.ToList();

    if (!string.IsNullOrWhiteSpace(name))
    {
        var extension =
            Path.GetExtension(name);

        if (!string.IsNullOrWhiteSpace(extension)
            && acceptedExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(
                "Asset Name must be entered without an image file extension.");
        }
    }

    return errors.Count == 0
        ? ValidationResult.Success()
        : ValidationResult.Failure(errors);
}
```

Test matrix:

```text
onboarding1       PASS
main_menu_bg      PASS
asset.v2          PASS
asset.v2.final    PASS
onboarding1.png   FAIL
onboarding1.PNG   FAIL
onboarding1.webp  FAIL
onboarding1.jpeg  FAIL
bad/name          FAIL
bad:name          FAIL
CON               FAIL
NUL.foo           FAIL
folder.           FAIL
folder<space>     FAIL
```

## 4.5 Mode-aware session validation

Refactor `ValidateSession()` into logical helpers without changing the public entry point:

```text
ValidateSessionCommon(...)
ValidateReferenceSessionState(...)
ValidateNoReferenceSessionState(...)
ValidateMainCommitMetadata(...)
```

ReferenceAssisted keeps the existing behavior.

NoReference rules:

```text
WorkflowMode == NoReference
ProjectName required (derived label stored in session)
AssetRootFolder safe/existing
AssetFolderName valid
AssetFolder path == root + name
CancelPhase must be None
CancellationId must be empty
ReferenceSourcePath empty
ReferenceDestinationPath empty
ReferenceFilename empty
ReferenceProvenancePath empty
ReferenceHash empty
ReferenceProcessedAt default
IsMainCommitting must be true while session.json exists
MainFilename/MainPrompt/MainProcessedAt/MainHash/MainTransactionId valid
```

**AssetFolder existence rule:**

For NoReference + active Main journal:

- if AssetFolder exists: validate it is safe and not a reparse point;
- if AssetFolder does not exist: allow it only if `WasAssetFolderCreatedByTool == true` and the normalized expected path is a direct child of AssetRootFolder.

This is the key fix that makes journal-before-write possible.

## 4.6 Mode-aware destructive path validation

Refactor `ValidateSessionPathsForDestructiveOperation()`.

Common checks:

```text
AssetRootFolder non-empty/safe
AssetFolderName safe
AssetFolder == root/name
AssetFolder direct child of root
root not a reparse point
asset folder, if it exists, not a reparse point
```

ReferenceAssisted additionally validates all Reference paths exactly as today.

NoReference must **not** require ReferenceFilename or Reference paths.

For Main rollback, also verify canonical Main/ingame/temp paths are children of the expected folders before deletion.

Never simply skip path validation because WorkflowMode is NoReference.

---

# 5. Model additions for Main/ingame transaction

## 5.1 AppConstants

Add:

```csharp
public const string IngameFolderName =
    "ingame";
```

## 5.2 AssetSession additions

Add:

```csharp
public AssetWorkflowMode WorkflowMode { get; set; }
    = AssetWorkflowMode.ReferenceAssisted;

public bool WasIngameFolderCreatedByTool { get; set; }
```

Add helpers:

```csharp
public string GetIngameFolderPath()
{
    if (string.IsNullOrWhiteSpace(AssetFolder))
    {
        return string.Empty;
    }

    return Path.Combine(
        AssetFolder,
        AppConstants.IngameFolderName);
}

public string GetIngameFilename()
{
    if (string.IsNullOrWhiteSpace(AssetFolderName)
        || string.IsNullOrWhiteSpace(MainFilename))
    {
        return string.Empty;
    }

    return AssetFolderName
        + Path.GetExtension(MainFilename);
}

public string GetIngameImagePath()
{
    var folder = GetIngameFolderPath();
    var filename = GetIngameFilename();

    if (string.IsNullOrWhiteSpace(folder)
        || string.IsNullOrWhiteSpace(filename))
    {
        return string.Empty;
    }

    return Path.Combine(folder, filename);
}

public string GetMainTempIngamePath()
{
    if (string.IsNullOrWhiteSpace(MainTransactionId)
        || string.IsNullOrWhiteSpace(MainFilename))
    {
        return string.Empty;
    }

    return Path.Combine(
        GetIngameFolderPath(),
        $".main-ingame-{MainTransactionId}{Path.GetExtension(MainFilename)}");
}
```

Do not store a redundant `IngameFilename`/`IngamePath` JSON property; derive it deterministically from existing journal metadata.

---

# 6. Template/provenance changes

## 6.1 Keep current Reference-assisted templates compatible

Do **not** remove `{{PROJECT}}` from `reference.md` or `final.md` in this release.

Reason:

- exact ownership validation renders expected provenance;
- unfinished v1.0 sessions can survive upgrade only if their current Reference-assisted template contract remains compatible;
- the user no longer has to enter Project because new session ProjectName is derived from Asset Root.

Thus “remove Project field” means remove the **user input/settings field**, not destroy provenance/recovery compatibility.

## 6.2 New NoReference template

Add:

```text
src/AssetProvenanceHelper/templates/final_no_reference.md
```

Use factual wording. The checkbox itself represents the user's assertion that the final generation did not use a reference image.

Recommended copy-ready template:

```markdown
# AI ASSET RIGHTS / PROVENANCE RECORD

Asset ID: {{FINAL_FILENAME}}\
Asset role: Final production asset\
Project: {{PROJECT}}

Generator: OpenAI ChatGPT\
Generation date: {{GENERATION_DATE}}

Generation workflow:\
AI image generation without a reference image recorded for the final generation.

Reference image used for the final generation:\
No.

Rights basis for final asset:\
The final image is Output generated by OpenAI ChatGPT. Under the applicable OpenAI Europe Terms of Use, as between the user and OpenAI and to the extent permitted by applicable law, the user owns the Output and OpenAI assigns to the user its right, title, and interest, if any, in that Output.

Prompt: "{{PROMPT}}"\
Reference file retained: not applicable\
Generation conversation retained: no

Final use:\
Commercial video game asset

Store asset: no

Human review: yes\
IP / trademark review: yes\
Release approved: yes\
Status: approved

Applicable terms record:\
OpenAI Europe Terms of Use\
Version/updated date: 2026-01-16

Important:\
This file documents provenance and the contractual rights basis recorded for the asset. It is not a license certificate issued by OpenAI and does not constitute a warranty that the generated material is unique, copyright-protected, or free of all possible third-party rights.
```

Do not silently rewrite the existing legal wording in the two v1.0 templates during this feature work.

## 6.3 TemplateService

Change constructor compatibly:

```csharp
public TemplateService(
    string referenceTemplatePath,
    string finalTemplatePath,
    string? finalNoReferenceTemplatePath = null)
```

New tokens:

```csharp
private static readonly string[] FinalNoReferenceTokens =
{
    "{{FINAL_FILENAME}}",
    "{{PROJECT}}",
    "{{GENERATION_DATE}}",
    "{{PROMPT}}"
};
```

New renderer:

```csharp
public string RenderFinalNoReference(
    string finalFilename,
    string project,
    string generationDate,
    string prompt)
{
    if (string.IsNullOrWhiteSpace(_finalNoReferenceTemplatePath))
    {
        throw new InvalidOperationException(
            "No-reference template path is not configured.");
    }

    var template =
        LoadValidatedTemplate(
            _finalNoReferenceTemplatePath,
            FinalNoReferenceTokens);

    var values =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{FINAL_FILENAME}}"] = finalFilename,
            ["{{PROJECT}}"] = project,
            ["{{GENERATION_DATE}}"] = generationDate,
            ["{{PROMPT}}"] = prompt
        };

    return RenderSinglePass(template, values);
}
```

`ValidateTemplates()`:

- always validates reference + final;
- validates no-reference template when its path is configured;
- production bootstrap always configures it;
- legacy unit tests using a two-path TemplateService may remain valid while being incrementally migrated.

## 6.4 AppBootstrap

Add:

```csharp
public required string FinalNoReferenceTemplatePath { get; init; }
```

and:

```csharp
public static string GetFinalNoReferenceTemplatePath(
    string baseDirectory) =>
    Path.Combine(
        baseDirectory,
        "templates",
        "final_no_reference.md");
```

Production construction:

```csharp
var templateService =
    new TemplateService(
        referenceTemplatePath,
        finalTemplatePath,
        finalNoReferenceTemplatePath);
```

---

# 7. NoReference Main journal creation

Add a pure precommit builder to `AssetProcessorService` (or a small `AssetSessionFactory`; prefer keeping it in AssetProcessorService for this release to avoid unnecessary service proliferation):

```csharp
public AssetSession CreateNoReferenceMainSession(
    AppSettings settings,
    string assetName,
    string sourceImagePath,
    string prompt,
    DateTimeOffset processedAt)
```

It performs **validation and hashing but no output mutation**.

Algorithm:

1. validate processing settings;
2. validate Asset Name;
3. validate source image;
4. require non-empty prompt;
5. compute normalized asset folder path;
6. enforce direct-child relationship to AssetRoot;
7. reject an existing asset folder if it is a reparse point;
8. compute ingame folder + production filename;
9. preflight canonical collisions;
10. compute source SHA-256;
11. return active journal session.

Copy-ready session core:

```csharp
var assetFolder =
    ValidationService.NormalizePath(
        Path.Combine(
            settings.AssetRootFolder,
            assetName));

var ingameFolder =
    ValidationService.NormalizePath(
        Path.Combine(
            assetFolder,
            AppConstants.IngameFolderName));

var mainFilename =
    Path.GetFileName(sourceImagePath);

return new AssetSession
{
    WorkflowMode =
        AssetWorkflowMode.NoReference,

    ProjectName =
        AssetNaming.DeriveProjectLabel(
            settings.AssetRootFolder),

    AssetRootFolder =
        settings.AssetRootFolder,

    AssetFolderName =
        assetName,

    AssetFolder =
        assetFolder,

    ReferenceSourcePath = string.Empty,
    ReferenceDestinationPath = string.Empty,
    ReferenceFilename = string.Empty,
    ReferenceProvenancePath = string.Empty,
    ReferenceHash = string.Empty,
    ReferenceProcessedAt = default,

    WasAssetFolderCreatedByTool =
        !Directory.Exists(assetFolder),

    WasReferenceFolderCreatedByTool =
        false,

    WasIngameFolderCreatedByTool =
        !Directory.Exists(ingameFolder),

    IsMainCommitting =
        true,

    MainFilename =
        mainFilename,

    MainPrompt =
        prompt,

    MainProcessedAt =
        processedAt,

    MainHash =
        ComputeSha256(sourceImagePath),

    MainTransactionId =
        Guid.NewGuid().ToString("N")
};
```

MainForm saves this session using existing atomic `SessionService.Save()` **before** calling `ProcessMainImage()`.

---

# 8. Existing-folder policy

Use one helper in MainForm for starting a new logical asset:

```csharp
private bool ConfirmExistingAssetFolderIfNeeded(
    string assetFolder)
```

Behavior:

```text
folder absent  -> continue
folder exists  -> prompt "Use Existing Folder" / "Cancel"
reparse point  -> reject before prompt
```

After confirmation, processor preflight remains authoritative and must still reject canonical collisions.

Never overwrite based only on the user's “Use Existing” answer.

Managed collisions that must fail:

```text
reference provenance already exists for new Reference save
same Reference destination already exists
final provenance already exists
root Main destination already exists
ingame production variant exists
active deterministic temp destination exists unexpectedly
```

Unrelated files may coexist in a user-confirmed existing asset folder.

---

# 9. `ingame` collision policy

Before Main writes, inspect:

```text
<AssetFolder>\ingame
```

If it exists, reject if reparse point.

Reject **any supported image** whose filename stem equals Asset Name case-insensitively, not merely the exact new extension.

Example for Asset Name `onboarding1`:

```text
onboarding1.png   BLOCK
onboarding1.jpg   BLOCK
onboarding1.JPEG  BLOCK
onboarding1.webp  BLOCK
onboarding1.txt   does not count as a production-image collision
other.jpg         does not count as this asset's collision
```

Suggested helper:

```csharp
private static IReadOnlyList<string> FindExistingIngameVariants(
    string ingameFolder,
    string assetName,
    IReadOnlyCollection<string> acceptedExtensions)
{
    if (!Directory.Exists(ingameFolder))
    {
        return Array.Empty<string>();
    }

    return Directory
        .EnumerateFiles(
            ingameFolder,
            "*",
            SearchOption.TopDirectoryOnly)
        .Where(path =>
        {
            var extension = Path.GetExtension(path);
            var stem = Path.GetFileNameWithoutExtension(path);

            return acceptedExtensions.Contains(
                    extension,
                    StringComparer.OrdinalIgnoreCase)
                && string.Equals(
                    stem,
                    assetName,
                    StringComparison.OrdinalIgnoreCase);
        })
        .ToList();
}
```

Call preflight before the first Main filesystem write.

Recovery code is allowed to recognize its own exact deterministic temp/canonical path through session metadata; normal new commits are not.

---

# 10. Main transaction extension for ingame

The current Main processor is already transactional. Extend it; do not replace it.

## 10.1 Preflight

Before mutations determine:

```text
mainFilename
rootMainDestination
finalProvenance
ingameFolder
ingameFilename
ingameDestination
tempMainPath
tempProvenancePath
tempIngamePath
```

Validate all paths.

For NoReference, create asset/ingame directories only after the durable session journal exists.

For ReferenceAssisted, the asset folder already exists because Reference is saved; `ingame` may not.

## 10.2 Recommended commit sequence

Inside `ProcessMainImage()`:

```text
1. validate session/mode
2. validate prompt/source image
3. verify active journal metadata exactly matches call arguments
4. preflight root/provenance/ingame collisions
5. create asset folder if needed
6. create ingame folder if needed
7. hash source
8. copy source -> temp Main
9. validate temp Main image
10. hash temp Main; require source hash match
11. Reference mode only: require Main hash != ReferenceHash
12. copy temp Main -> temp ingame
13. hash temp ingame; require Main hash match
14. render mode-appropriate final provenance
15. write temp provenance
16. promote provenance -> canonical final provenance
17. promote temp Main -> canonical root Main
18. promote temp ingame -> canonical ingame Main
19. validate complete asset including ingame
20. return mainFilename
```

The order deliberately makes `ingame` the last canonical image promotion. A crash after root Main but before ingame is therefore detected as incomplete and safely rolled back.

Do not copy from the *source* independently to ingame. Copy from verified temp Main (or copy source and independently compare hash); using temp Main makes the 1:1 relationship easier to reason about.

## 10.3 Mode-appropriate provenance

```csharp
provenance =
    session.WorkflowMode switch
    {
        AssetWorkflowMode.ReferenceAssisted =>
            _templateService.RenderFinal(
                mainFilename,
                session.ReferenceFilename,
                session.ProjectName,
                generationDate,
                prompt),

        AssetWorkflowMode.NoReference =>
            _templateService.RenderFinalNoReference(
                mainFilename,
                session.ProjectName,
                generationDate,
                prompt),

        _ =>
            throw new InvalidDataException(
                $"Unsupported workflow mode: {session.WorkflowMode}")
    };
```

## 10.4 Reference duplicate check

Only ReferenceAssisted mode performs:

```csharp
if (string.Equals(
        mainHash,
        session.ReferenceHash,
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "The selected main image is identical to the reference image.");
}
```

NoReference has no ReferenceHash.

---

# 11. Main rollback extension

`RollbackMain()` must handle:

```text
canonical final provenance
canonical root Main
canonical ingame Main
temp provenance
temp Main
temp ingame
empty ingame folder if tool-created
empty asset folder if NoReference + tool-created
```

## 11.1 Ownership rules

Before deleting canonical root Main:

```text
SHA-256 must equal session.MainHash
```

Before deleting canonical ingame:

```text
path must equal deterministic session.GetIngameImagePath()
SHA-256 must equal session.MainHash
```

Before deleting temp Main/temp ingame:

```text
path must equal deterministic transaction path
SHA-256 must equal session.MainHash
```

Before deleting final/temp provenance:

```text
exact text must equal the renderer for the current WorkflowMode and session metadata
```

Any mismatch -> preserve file and return failure.

## 11.2 Directory cleanup

After verified owned files are removed:

```text
if WasIngameFolderCreatedByTool:
    delete ingame only if empty

if WorkflowMode == NoReference
and WasAssetFolderCreatedByTool:
    delete asset folder only if empty
```

Reference-assisted rollback does not delete the asset folder because the valid saved Reference remains.

## 11.3 Absent directories

A journaled NoReference session may exist before the tool created its asset folder. Rollback with zero output files and absent directories is a valid clean rollback.

Do not make directory existence itself a rollback error in that specific state.

---

# 12. Complete-asset validation

Refactor `ValidateCompleteAsset()` so a v1.1 Main is complete only if all expected outputs are valid.

## ReferenceAssisted

Require:

```text
valid Reference session/output
root Main exists and SHA == MainHash
final provenance exists and matches RenderFinal exactly
ingame Main exists and SHA == MainHash
root Main SHA == ingame SHA
```

## NoReference

Require:

```text
valid NoReference journal state
root Main exists and SHA == MainHash
final provenance exists and matches RenderFinalNoReference exactly
ingame Main exists and SHA == MainHash
root Main SHA == ingame SHA
reference folder/provenance is not required
```

Do not require `reference` directory absence during validation if the asset folder pre-existed and the user chose “Use Existing”; merely guarantee that the tool did not create/use Reference artifacts in NoReference mode. Unrelated pre-existing content must not make the tool destructive.

---

# 13. Exact final provenance ownership

`ValidateExactFinalProvenanceOwnership()` must switch renderer by mode.

Copy-ready core:

```csharp
expectedText =
    session.WorkflowMode switch
    {
        AssetWorkflowMode.ReferenceAssisted =>
            templateService.RenderFinal(
                session.MainFilename,
                session.ReferenceFilename,
                session.ProjectName,
                generationDate,
                session.MainPrompt ?? string.Empty),

        AssetWorkflowMode.NoReference =>
            templateService.RenderFinalNoReference(
                session.MainFilename,
                session.ProjectName,
                generationDate,
                session.MainPrompt ?? string.Empty),

        _ =>
            throw new InvalidDataException(
                $"Unsupported WorkflowMode: {session.WorkflowMode}")
    };
```

Likewise, `ValidateFinalProvenanceContent()` must not require `ReferenceFilename` in NoReference mode.

---

# 14. NoReference crash recovery state machine

Implement a dedicated recovery helper rather than forcing NoReference through Reference recovery.

```csharp
private void RecoverNoReferenceSession(
    AssetSession session)
```

## 14.1 Valid complete Main

If `ValidateCompleteAsset()` succeeds:

```text
prompt: completed asset session found
option: Delete Session Record / Exit
```

On delete:

```text
delete session.json
_lastCompletedAssetFolderPath = session.AssetFolder
return to Idle
```

## 14.2 Incomplete Main

Call `RollbackMain()`.

If rollback succeeds:

```text
delete session.json
add status "Interrupted no-reference Main transaction rolled back."
return to Idle
```

There is no Reference session to resume.

## 14.3 Untrusted/incomplete rollback

If rollback fails ownership/path verification:

```text
show CRITICAL recovery error
preserve session.json
preserve unknown files
close app
```

## 14.4 Crash immediately after journal save

Expected state:

```text
session.json exists
asset folder may not exist
no canonical outputs
```

This must validate as a legitimate NoReference active transaction and cleanly roll back/delete the journal.

Add a direct regression test.

---

# 15. Reference replacement behavior

Keep the existing transactional replacement implementation.

After a successful replacement:

```csharp
SetSelectedImage(ImageSlot.Reference, null);
SetSelectedImage(ImageSlot.Main, null);
txtPrompt.Clear();
```

Display saved Reference using session metadata, not candidate state:

```text
Saved reference: <session.ReferenceFilename>
```

Reference CTA becomes:

```text
Replace Reference
```

A replacement requires a new explicit Reference candidate. The old candidate must not remain actionable.

Add tests:

```text
ReferenceReplacement_ClearsMainSelection
ReferenceReplacement_ClearsPrompt
ReferenceReplacement_ClearsReferenceCandidate
ReferenceReplacement_PreservesSavedReferenceDisplay
```

---

# 16. Independent image-selection architecture

Remove shared:

```csharp
_latestImagePath
_manualSelectionPath
ResolveImageSelection()
RefreshLatestImage()
```

Replace with:

```csharp
private enum ImageSlot
{
    Reference,
    Main
}

private string? _referenceImagePath;
private string? _mainImagePath;
```

## 16.1 Copy-ready slot accessors

```csharp
private string? GetSelectedImage(
    ImageSlot slot) =>
    slot switch
    {
        ImageSlot.Reference => _referenceImagePath,
        ImageSlot.Main => _mainImagePath,
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };

private void SetSelectedImage(
    ImageSlot slot,
    string? path)
{
    switch (slot)
    {
        case ImageSlot.Reference:
            _referenceImagePath = path;
            UpdateImageSlotUi(ImageSlot.Reference, path);
            break;

        case ImageSlot.Main:
            _mainImagePath = path;
            UpdateImageSlotUi(ImageSlot.Main, path);
            break;

        default:
            throw new ArgumentOutOfRangeException(nameof(slot));
    }
}
```

## 16.2 Refresh

```csharp
private void RefreshImageSelection(
    ImageSlot slot)
```

Rules:

1. validate DownloadFolder specifically;
2. if invalid: red-outline DownloadFolder field + status/message; leave existing slot selection unchanged;
3. call ImageFinderService;
4. if no image: clear that slot and show `No image found`;
5. if image: validate and set only that slot.

Never modify the other slot.

## 16.3 Choose File

```csharp
private void ChooseImageFile(
    ImageSlot slot)
```

Use one `OpenFileDialog` implementation.

If DownloadFolder exists, set `InitialDirectory` to it; otherwise leave default.

Manual file may be anywhere readable.

Validate via existing `ValidateImageFile()`.

## 16.4 Drag/drop

Use one pair of methods:

```csharp
private void ImageDrop_DragEnter(
    ImageSlot slot,
    DragEventArgs e)

private void ImageDrop_DragDrop(
    ImageSlot slot,
    DragEventArgs e)
```

Exactly one file.

Pass it through the same image validator as Choose File.

Do not copy a dropped file merely by dropping it. Drop changes only the selected source path.

## 16.5 CTA behavior

Reference CTA reads only:

```csharp
GetSelectedImage(ImageSlot.Reference)
```

Main CTA reads only:

```csharp
GetSelectedImage(ImageSlot.Main)
```

No implicit Refresh in either CTA.

---

# 17. Browser-neutral latest-image discovery

`ImageFinderService` currently prioritizes filenames starting with `ChatGPT Image`, even when another supported image is newer.

Remove that browser/provider-specific heuristic.

Final selection:

```csharp
return allCandidates
    .OrderByDescending(file => file.LastWriteTimeUtc)
    .ThenByDescending(file => file.CreationTimeUtc)
    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
    .First()
    .FullName;
```

Rename visible strings:

```text
Firefox Download Folder -> Image Download Folder
Select Firefox download folder -> Select image download folder
Open Downloads -> Open Image Folder
```

Final repository search:

```powershell
rg -n "Firefox" .
```

Expected: zero intentional matches.

---

# 18. GUI specification

The main window itself must not scroll.

Internal multiline fields (Final Prompt / Status) may have their own scrollbars.

## 18.1 Window

Target default client experience approximately:

```text
1100 × 700
```

Suggested minimum:

```text
980 × 640
```

Do not hardcode a size larger than the current screen working area. On startup, clamp initial bounds to `Screen.FromControl(this).WorkingArea` if necessary.

Use normal WinForms DPI/font autoscaling; verify manually at 100/125/150% scaling.

## 18.2 Root layout

```text
Header        ~54 px
Settings      AutoSize/fixed compact
Current Asset remaining space
Status        ~100 px
```

Root:

```csharp
AutoScroll = false;
Dock = DockStyle.Fill;
```

Name the root/control groups so UI tests can find them:

```text
mainLayout
grpSettings
grpCurrentAsset
grpReference
grpMainImage
grpStatus
pnlHelpOverlay
```

## 18.3 Header

```text
[icon] AI Asset Provenance Helper        v1.1.0 [?]
```

Keep OS window title stable:

```text
AI Asset Provenance Helper
```

Do not put version into OS title; existing smoke expectations and user shell recognition remain stable.

## 18.4 Settings group

```text
Image Download Folder [........................] [Browse]
Asset Root Folder      [........................] [Browse]
```

Both textboxes save on `Leave` and on FormClosing.

Browse changes save immediately.

## 18.5 Current Asset top row

```text
Asset Name [...........................] [ ] No reference mode
```

Under it, two card columns:

```text
Reference | Main Image
```

Reference hidden + Main expanded to 100% in NoReference mode.

## 18.6 Reference card

Contents:

```text
Selected candidate: <filename or none>
Modified: <timestamp or ->
[Refresh] [Choose File...] [Drop file here] [Open Image Folder]
Saved reference: <session reference or none>
[REFERENCE / REPLACE REFERENCE]
```

## 18.7 Main card

Contents:

```text
Selected: <filename or none>
Modified: <timestamp or ->
[Refresh] [Choose File...] [Drop file here] [Open Image Folder]

Final Prompt
[multiline textbox]
[Paste Clipboard] [Clear]

[MAIN IMAGE]
```

## 18.8 Status group

Lowest group:

```text
status history
[Open Asset Folder] [Cancel Current Asset]
```

Status can have its own vertical scrollbar.

Long filenames/paths use `AutoEllipsis = true` and ToolTip for full path; they must never expand the window.

---

# 19. NoReference GUI state

Checkbox:

```text
No reference mode
```

Default unchecked.

## When checked in Idle

```text
clear Reference candidate
clear Reference validation visuals
hide grpReference
set Reference column width 0%
set Main column width 100%
Ctrl+R disabled/no-op
Main CTA available
```

## When unchecked

```text
Reference column 50%
Main column 50%
show grpReference
```

## Active Reference session

```text
checkbox unchecked
checkbox disabled
Asset Name locked
Asset Root locked
Reference group visible
```

After Reference completion/cancel:

```text
checkbox enabled again
```

The checkbox does not need to be stored in settings.json.

It may remain in its last Idle selection after an asset completes for faster repeated work.

---

# 20. Action-specific required fields

Do not use one global “everything required” validator.

## 20.1 Refresh

Requires only:

```text
Image Download Folder valid/existing
```

## 20.2 Reference CTA

Requires:

```text
Asset Root valid
Asset Name valid
Reference candidate selected + valid
all Reference/final templates valid
```

DownloadFolder not required.

## 20.3 Replace Reference CTA

Requires:

```text
valid active Reference session
new Reference candidate selected + valid
```

## 20.4 Main CTA — ReferenceAssisted

Requires:

```text
valid active Reference session
Main candidate selected + valid
Final Prompt non-empty
```

Asset Root/Asset Name are locked and taken from the session; do not rebuild session paths from mutable UI text.

## 20.5 Main CTA — NoReference

Requires:

```text
Asset Root valid
Asset Name valid
Main candidate selected + valid
Final Prompt non-empty
```

DownloadFolder not required.

---

# 21. Red validation outlines and CTA pulse

Do not parse error strings to identify fields.

Create explicit visual-host panels:

```text
pnlDownloadFolderBorder
pnlAssetRootBorder
pnlAssetNameBorder
pnlPromptBorder
pnlReferenceSelectionBorder
pnlMainSelectionBorder
```

Textboxes use `BorderStyle.None` inside a 2px host panel.

Copy-ready helper:

```csharp
private static Panel CreateFieldHost(
    TextBox textBox)
{
    var host =
        new Panel
        {
            Padding = new Padding(2),
            BackColor = UiTheme.Border,
            Dock = DockStyle.Fill
        };

    textBox.BorderStyle = BorderStyle.None;
    textBox.Dock = DockStyle.Fill;

    host.Controls.Add(textBox);

    return host;
}
```

Error:

```csharp
host.BackColor = UiTheme.Error;
```

Normal:

```csharp
host.BackColor = UiTheme.Border;
```

Image-selection border hosts use the same concept around the selected-file display.

## 21.1 Explicit UI validation methods

Implement:

```text
ValidateReferenceActionUi()
ValidateMainActionUi()
ValidateRefreshUi()
```

Each method knows which controls it owns. Do not infer fields from service error text.

## 21.2 Focus

After invalid submit, focus the first missing/invalid editable field in visual order.

## 21.3 CTA pulse

Reference/Main CTAs are normally accent colored.

On invalid submit:

```text
turn red
pulse between Error and ErrorPulse about 8 times at ~175 ms
finish solid red
```

Stop after a bounded period. No endless flashing.

Create Timer through the components container so it is disposed with the form.

When relevant input changes, clear that field's red state and restore CTA accent.

---

# 22. Prompt behavior

Remove the current automatic “clipboard contains text -> Paste and Continue?” branch from Main submission.

Final Prompt is explicit provenance evidence and must be consciously present in its textbox.

Keep visible buttons:

```text
Paste Clipboard
Clear
```

Main CTA with empty prompt:

```text
red prompt outline
red/pulsing Main CTA
focus prompt
no processing
```

Clipboard failures remain caught as today.

---

# 23. Header/version/theme

Add:

```text
src/AssetProvenanceHelper/Ui/AppInfo.cs
src/AssetProvenanceHelper/Ui/UiTheme.cs
```

## AppInfo.cs

```csharp
namespace AssetProvenanceHelper.Ui;

internal static class AppInfo
{
    public const string ProductName =
        "AI Asset Provenance Helper";

    public static string Version =>
        typeof(AppInfo)
            .Assembly
            .GetName()
            .Version?
            .ToString(3)
        ?? "dev";
}
```

## UiTheme.cs

```csharp
using System.Drawing;

namespace AssetProvenanceHelper.Ui;

internal static class UiTheme
{
    public static readonly Color ReferenceAccent =
        Color.FromArgb(30, 145, 126);

    public static readonly Color MainAccent =
        Color.FromArgb(83, 99, 208);

    public static readonly Color Error =
        Color.FromArgb(196, 47, 55);

    public static readonly Color ErrorPulse =
        Color.FromArgb(232, 84, 92);

    public static readonly Color Border =
        Color.FromArgb(202, 208, 217);

    public static readonly Color GroupBackground =
        Color.FromArgb(248, 249, 251);
}
```

CTA style:

```csharp
button.UseVisualStyleBackColor = false;
button.FlatStyle = FlatStyle.Flat;
button.FlatAppearance.BorderSize = 0;
button.ForeColor = Color.White;
button.Font = new Font(button.Font, FontStyle.Bold);
button.Height = 38;
```

---

# 24. Logo and application icon

Canonical source:

```text
src/AssetProvenanceHelper/Assets/app-logo.svg
```

Copy-ready SVG:

```svg
<svg xmlns="http://www.w3.org/2000/svg"
     width="64"
     height="64"
     viewBox="0 0 64 64"
     fill="none">
  <defs>
    <linearGradient id="bg" x1="8" y1="7" x2="57" y2="58" gradientUnits="userSpaceOnUse">
      <stop stop-color="#5D63E6"/>
      <stop offset="0.52" stop-color="#3D8DD8"/>
      <stop offset="1" stop-color="#27B99B"/>
    </linearGradient>
    <linearGradient id="line" x1="17" y1="28" x2="47" y2="40" gradientUnits="userSpaceOnUse">
      <stop stop-color="#6F75EC"/>
      <stop offset="1" stop-color="#26AE98"/>
    </linearGradient>
  </defs>

  <rect x="3" y="3" width="58" height="58" rx="15" fill="url(#bg)"/>
  <rect x="12" y="13" width="40" height="31" rx="6" fill="white" fill-opacity="0.96"/>
  <circle cx="42" cy="22" r="4" fill="#FFBE55"/>
  <path d="M17 38L25 29L31 35L36 30L47 39"
        stroke="url(#line)" stroke-width="3.2" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="19" cy="51" r="3.3" fill="white"/>
  <circle cx="32" cy="51" r="3.3" fill="white"/>
  <path d="M22.3 51H28.7" stroke="white" stroke-width="2.5" stroke-linecap="round"/>
  <path d="M40 49L44 53L52 44" stroke="white" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
```

Generate and commit:

```text
src/AssetProvenanceHelper/Assets/app-logo.ico
```

Do not require image conversion software at runtime or CI.

Project:

```xml
<Version>1.1.0</Version>
<ApplicationIcon>Assets\app-logo.ico</ApplicationIcon>
```

Use the compiled form/application icon for the header:

```csharp
picLogo.Image = Icon?.ToBitmap();
```

Dispose the created bitmap with the form.

No separate runtime PNG is necessary.

---

# 25. True in-form Help overlay

Add:

```text
src/AssetProvenanceHelper/Ui/HelpOverlayControl.cs
```

Implement as `UserControl`:

```text
Dock = Fill
Visible = false
solid neutral overlay background
centered bordered content panel
Close button top-right of overlay content
```

MainForm adds it last so it can `BringToFront()`.

Header `?`:

```csharp
private void ShowHelpOverlay()
{
    helpOverlay.Visible = true;
    helpOverlay.BringToFront();
    helpOverlay.FocusCloseButton();
}
```

Close/Esc hides it.

While Help is visible:

- Ctrl+R/Ctrl+M must not trigger processing;
- underlying content should not receive tab/click actions;
- easiest implementation: disable `mainContentPanel`, leaving overlay outside it.

Required text categories:

```text
About
Basic workflow
Reference-assisted workflow
No reference mode
Local-file behavior
Keyboard shortcuts
Legal/disclaimer information
Made by CeeGore
```

Recommended legal text:

```text
This tool creates internal provenance documentation. It is not legal advice
and does not determine or guarantee copyright ownership, copyrightability,
uniqueness, non-infringement, trademark clearance, commercial-use eligibility,
or acceptance by a store/platform.

Generation-provider terms and applicable laws can change. Review the terms
that applied to the generation workflow and verify the generated provenance
record before relying on it.

Use No reference mode only when no reference image was supplied for the final
generation.
```

Footer exact display:

```text
Made by CeeGore
```

---

# 26. MainForm state rules

Keep the existing steady states if possible:

```text
Idle
ReferenceReady
```

NoReference commit is synchronous and journal-driven; it does not need a long-lived third UI state.

## Idle / Reference mode

```text
Asset Root editable
Asset Name editable
NoReference checkbox enabled
Reference CTA enabled when templates valid
Main CTA disabled until Reference session exists
```

## ReferenceReady

```text
Asset Root locked
Asset Name locked
NoReference checkbox unchecked + disabled
Reference CTA = Replace Reference
Main CTA enabled when templates valid
Cancel Current Asset enabled
```

## Idle / NoReference checked

```text
Reference group hidden
Asset Root editable
Asset Name editable
Main CTA enabled when templates valid
Cancel Current Asset disabled
```

Download folder remains editable in every steady state because changing where the next generated Main is downloaded does not change session ownership.

---

# 27. MainForm completion/reset rules

## Reference save success

```text
_currentSession = created session
state = ReferenceReady
saved Reference label = session.ReferenceFilename
clear Reference candidate
keep Main candidate empty
keep prompt empty
```

## Reference replacement success

```text
_currentSession = replacement session
saved Reference label updated
clear Reference candidate
clear Main candidate
clear Final Prompt
```

## Main complete — either mode

```text
_lastCompletedAssetFolderPath = session.AssetFolder
_currentSession = null
state = Idle
clear Asset Name
clear Main candidate
clear Reference candidate
clear Final Prompt
saved Reference label = none
Refresh is NOT invoked automatically into a slot
```

Do not automatically select whatever file remains newest after completion; the next asset begins with explicit empty candidates.

NoReference checkbox may keep its Idle checked/unchecked state.

## Reference cancel

```text
rollback/cancel reference transaction safely
_currentSession = null
state = Idle
clear Asset Name
clear both candidates
clear prompt
saved Reference label = none
```

---

# 28. Recommended source-file split

Do a mechanical split first, then features. This reduces weak-model edit risk.

Use method names as authoritative anchors; line numbers below are approximate because they shift after each phase.

## MainForm

Current file is ~50 KB and contains unrelated concerns.

Target:

```text
MainForm.cs                       ~250-350 lines
  constructor
  event wiring
  high-level state
  action orchestration
  status/open-folder basics

MainForm.ImageSelection.cs        ~250-350 lines
  ImageSlot
  Refresh/Choose/drop
  selection state + display

MainForm.ReferenceWorkflow.cs     ~300-450 lines
  HandleReference
  HandleReplaceReference
  HandleCancel

MainForm.MainWorkflow.cs          ~300-450 lines
  HandleMainImage
  ExecuteMainCommit
  common Main failure/success logic

MainForm.Recovery.cs              ~350-500 lines
  startup recovery
  Reference-assisted recovery
  NoReference recovery

MainForm.ValidationUi.cs          ~200-350 lines
  UI field errors
  CTA pulse
  action-specific UI validation

MainForm.Designer.cs              ~250-400 lines
  control declarations
  InitializeComponent root

MainForm.Layout.cs                ~300-450 lines
  BuildHeader
  BuildSettingsGroup
  BuildCurrentAssetGroup
  BuildReferenceGroup
  BuildMainGroup
  BuildStatusGroup
  control factory helpers
```

Do not exceed ~500 lines in newly created files unless there is a concrete reason.

## AssetProcessorService

Mechanical partial split is recommended because current file is ~68 KB.

```text
AssetProcessorService.cs              constructor + shared hooks/hash helpers
AssetProcessorService.Reference.cs    ProcessReference/replacement/rollback Reference
AssetProcessorService.Main.cs         CreateNoReferenceMainSession/ProcessMainImage/RollbackMain
AssetProcessorService.FileOps.cs      atomic copy/write/delete/ownership helper primitives
```

Make class declaration `partial` in all parts.

Perform the split with zero behavioral edits and run the full suite before adding ingame behavior.

## ValidationService

Optional but recommended while touching session/path logic:

```text
ValidationService.cs              common/basic image/settings/name validation
ValidationService.Session.cs      session + provenance validation
ValidationService.Paths.cs        NormalizePath/reparse/destructive path helpers
```

Again: mechanical split first; behavior second.

## Tests

Do **not** add new features to the huge existing RegressionTests file unless modifying an existing regression case.

Add focused new files:

```text
ChangeV11SettingsTests.cs
ChangeV11NamingTests.cs
ChangeV11ImageSelectionTests.cs
ChangeV11IngameTests.cs
ChangeV11NoReferenceTests.cs
ChangeV11RecoveryTests.cs
ChangeV11MainFormTests.cs
ChangeV11PackagingTests.cs   (only if process/file tests are appropriate)
```

---

# 29. Implementation phases for a weaker model

Every phase ends in a test gate. Do not combine phases because one “looks small.”

---

## PHASE 00 — Baseline and mechanical source split

### Files

```text
MainForm*.cs
AssetProcessorService*.cs
ValidationService*.cs (optional split)
```

### Work

1. Record current branch/commit.
2. Run Debug + Release tests.
3. Mechanically split partial classes by method boundaries.
4. No behavior/text changes.
5. Build/test again.

### Gate

```powershell
dotnet restore AssetProvenanceHelper.sln
dotnet build AssetProvenanceHelper.sln -c Debug --no-restore -warnaserror
dotnet test AssetProvenanceHelper.sln -c Debug --no-build
dotnet build AssetProvenanceHelper.sln -c Release --no-restore -warnaserror
dotnet test AssetProvenanceHelper.sln -c Release --no-build
```

If exact local SDK 8.0.418 is unavailable, **do not alter `global.json` merely to make the local environment pass**. Record the local environment limitation and use the repository CI for the exact pinned runtime check.

---

## PHASE 01 — Remove Project setting + generic download discovery

### Modify

```text
Models/AppSettings.cs
Services/SettingsService.cs
Services/ValidationService.cs
Services/AssetNaming.cs       NEW
Services/ImageFinderService.cs
MainForm.cs
MainForm.Designer.cs/Layout.cs
MainForm.Recovery.cs
README.md (only obvious terminology; full docs later)
TestWorkspace.cs
SettingsServiceTests.cs
ImageFinderServiceTests.cs
MainFormUiTests.cs
existing tests that directly construct AppSettings.ProjectName
```

### Exact behavior

- delete `AppSettings.ProjectName`;
- old settings with ProjectName load safely;
- remove txtProject and recovery assignment to it;
- processing validation no longer requires Project or DownloadFolder;
- add download-folder-specific validator;
- new Reference sessions derive `session.ProjectName` from Asset Root basename;
- remove ChatGPT filename preference;
- rename Firefox UI text.

### Tests

```text
LegacySettingsJson_WithProjectName_Loads
SavedSettings_DoesNotWriteProjectName
ProcessingSettings_AllowsEmptyDownloadFolder
RefreshValidation_RejectsEmptyDownloadFolder
ManualProcessing_CanProceedWithEmptyDownloadFolder
ProjectTextbox_DoesNotExist
NewReferenceSession_ProjectLabelDerivedFromAssetRoot
NewestSupportedImage_WinsRegardlessOfFilename
```

### Static search

```powershell
rg -n "txtProject|Firefox" src tests README.md
```

Expected `txtProject`/Firefox: zero after test updates.

`ProjectName` intentionally remains in AssetSession/provenance-session tests.

---

## PHASE 02 — WorkflowMode + AssetName + ingame path model

### Modify

```text
AppConstants.cs
Models/AssetSession.cs
Services/AssetNaming.cs
Services/ValidationService*.cs
```

### Add

```text
AssetWorkflowMode
IngameFolderName
WasIngameFolderCreatedByTool
GetIngame* helpers
ValidateAssetName
mode-aware ValidateSession skeleton
mode-aware destructive path validation
```

Do not yet modify Main file writing.

### Tests

```text
LegacySession_WithoutWorkflowMode_DefaultsReferenceAssisted
AssetName validation matrix
IngameFilename_PreservesOriginalExtensionCase
IngamePath_IsInsideAssetFolder
NoReferenceSessionValidation_AllowsAbsentAssetFolderOnlyForActiveJournal
NoReferenceSessionValidation_RejectsReferenceFields
NoReferenceDestructivePathValidation_DoesNotRequireReferenceFilename
```

---

## PHASE 03 — NoReference template + exact provenance validation

### Modify/add

```text
templates/final_no_reference.md             NEW
Services/TemplateService.cs
Services/AppBootstrap.cs
Services/ValidationService.Session.cs
TestWorkspace.cs
TemplateServiceTests.cs
```

### Tests

```text
ProductionTemplates_ValidateAllThree
RenderFinalNoReference_ReplacesAllTokens
RenderFinalNoReference_PreservesPromptLiterally
UnknownNoReferenceToken_Fails
ExactNoReferenceFinalProvenanceOwnership_Succeeds
ExactNoReferenceFinalProvenanceOwnership_RejectsModifiedFile
```

No UI work yet.

---

## PHASE 04 — Main/ingame transaction + NoReference processor

### Modify

```text
AssetProcessorService.Main.cs
AssetProcessorService.FileOps.cs
ValidationService.Session.cs
```

### Implement

```text
CreateNoReferenceMainSession
Main preflight for ingame variants
create ingame directory
copy/hash temp ingame
promote ingame canonical file
mode-specific final template
extended rollback
extended ValidateCompleteAsset
```

### Failure hooks

Add test-only ThreadStatic hooks consistent with current style:

```csharp
[ThreadStatic]
internal static Action<string>? OnIngameTempCopiedHook;

[ThreadStatic]
internal static Action<string>? OnIngamePromotedHook;
```

Do not add production behavior depending on hooks.

### Required tests

```text
Main_CreatesIngameCopy
Main_IngameCopyIsByteIdentical
Main_PreservesRootFilename
Main_PreservesSourceDownload
Main_PreservesExtensionCase
Main_RejectsExactIngameCollision
Main_RejectsDifferentExtensionVariantCollision
Main_RejectsIngameReparsePoint
Main_RollbackRemovesOwnedIngame
Main_RollbackPreservesModifiedIngame
Main_RollbackRemovesToolCreatedEmptyIngameFolder
NoReference_CreatesNoReferenceFolder
NoReference_MainCreatesRootAndIngame
NoReference_UsesNoReferenceTemplate
NoReference_CleanFailureDeletesOnlyOwnedOutputs
NoReference_PreWriteJournalStateCanRollbackWithNoAssetFolder
```

---

## PHASE 05 — Independent Reference/Main selection

### Modify

```text
MainForm.ImageSelection.cs
MainForm.ReferenceWorkflow.cs
MainForm.MainWorkflow.cs
MainForm.Layout.cs
```

### Implement

```text
ImageSlot enum
two independent path fields
two Refresh controls
two Choose File controls
two Drop zones
two selected file displays
no implicit refresh from CTA
```

### Important transition

Successful Reference save:

```text
Reference candidate cleared
saved Reference display populated
```

Successful Reference replacement:

```text
Reference candidate cleared
Main candidate cleared
prompt cleared
```

### Tests

```text
ReferenceRefresh_DoesNotChangeMain
MainRefresh_DoesNotChangeReference
ReferenceChoose_DoesNotChangeMain
MainChoose_DoesNotChangeReference
ReferenceDrop_DoesNotChangeMain
MainDrop_DoesNotChangeReference
CTA_DoesNotImplicitlyRefresh
ReferenceReplacement_ClearsMainAndPrompt
```

---

## PHASE 06 — GUI redesign, validation visuals, NoReference checkbox

### Modify

```text
MainForm.Designer.cs
MainForm.Layout.cs
MainForm.ValidationUi.cs
MainForm.cs
Ui/AppInfo.cs           NEW
Ui/UiTheme.cs           NEW
```

### Implement

```text
non-scrolling root
header
Settings group
Current Asset group
Reference/Main cards
Status group
Asset Name label
No reference checkbox
special CTA colors
red field hosts
bounded CTA pulse
```

### Tests

```text
MainRoot_AutoScrollFalse
ProjectControlAbsent
SettingsGroupExists
ReferenceAndMainGroupsExist
StatusIsLastRootSection
SeparateRefreshChooseDropControlsExist
NoReference_HidesReferenceGroup
NoReference_ExpandsMainColumn
ActiveReference_DisablesNoReference
MissingAssetName_HighlightsAssetName
MissingPrompt_HighlightsPrompt
MissingReference_HighlightsReferenceSelection
MissingMain_HighlightsMainSelection
```

Do not use screenshot pixel comparisons as primary automated tests.

---

## PHASE 07 — MainForm Main orchestration + recovery

### Modify

```text
MainForm.MainWorkflow.cs
MainForm.Recovery.cs
MainForm.ReferenceWorkflow.cs
```

### Implement

- Reference-assisted existing commit uses Main slot + prompt;
- before Reference Main writes, persist `WasIngameFolderCreatedByTool` with Main journal metadata;
- NoReference creates/saves journal before output mutation;
- common ExecuteMainCommit success/error handling;
- clean NoReference failure deletes session;
- mode-specific startup recovery;
- v1.0 in-progress Main without ingame rolls back to ReferenceReady;
- no-reference journal saved before folder creation recovers cleanly.

### Tests

```text
ReferenceMain_CompletesAndDeletesSession
NoReferenceMain_CompletesAndDeletesSession
NoReferenceMain_CleanFailureDeletesSession
NoReferenceMain_UntrustedRollbackPreservesSession
Recovery_NoReference_PreWriteCrash
Recovery_NoReference_AfterRootMainBeforeIngame
Recovery_NoReference_AfterIngameBeforeSessionDelete
Recovery_LegacyReferenceMainWithoutIngame_RollsBackToReference
Recovery_CompleteV11RequiresIngame
```

---

## PHASE 08 — Help overlay + branding + version/package

### Add/modify

```text
Ui/HelpOverlayControl.cs                    NEW
Assets/app-logo.svg                         NEW
Assets/app-logo.ico                         NEW/generated once
AssetProvenanceHelper.csproj
MainForm.Layout.cs
MainForm.cs
scripts/run_smoke_tests.ps1
.github/workflows/ci.yml
```

### Version

```xml
<Version>1.1.0</Version>
<ApplicationIcon>Assets\app-logo.ico</ApplicationIcon>
```

### Dynamic release archive

In smoke script derive from executable ProductVersion; strip optional `+metadata`.

Example:

```powershell
$productVersion =
    (Get-Item $exePath).VersionInfo.ProductVersion

if (-not $productVersion) {
    throw "Could not determine product version from executable."
}

$productVersion =
    $productVersion.Split('+')[0]

$archiveName =
    "AssetProvenanceHelper-v$($productVersion)-win-x64.zip"
```

CI upload:

```yaml
artifacts/AssetProvenanceHelper-v*-win-x64.zip
```

Smoke must verify:

```text
templates/reference.md
templates/final.md
templates/final_no_reference.md
main process starts
expected stable window title
clean CloseMainWindow shutdown
compiled ProductVersion == 1.1.0
```

### Help tests

```text
HelpButtonExists
HelpOverlayInitiallyHidden
HelpButtonShowsOverlay
CloseHidesOverlay
EscHidesOverlay
HelpVisibleSuppressesCtrlRAndCtrlM
HelpContainsMadeByCeeGore
```

---

## PHASE 09 — Documentation + full audit

### README

Document actual copy behavior, not moves.

Reference tree and NoReference tree must both be shown.

Explicitly say:

```text
Downloaded source files are copied. They are not moved or deleted by a normal save operation.
```

Document:

```text
Image Download Folder is optional when choosing/dropping a source manually.
Asset Root Folder is required for processing.
Asset Name is entered without extension.
No reference mode means no reference image was used for the final generation.
```

### Final test sequence

Run full repository CI-equivalent checks.

---

# 30. Main MainForm orchestration skeleton

The weaker model should converge both workflows into one commit helper rather than duplicate the existing long exception block.

Suggested shape:

```csharp
private void HandleMainImage()
{
    ClearMainValidationVisuals();

    if (!ValidateMainActionUi())
    {
        PulseInvalidCta(btnMainImage);
        return;
    }

    var sourceImage =
        GetSelectedImage(ImageSlot.Main)!;

    var prompt =
        txtPrompt.Text;

    var processedAt =
        DateTimeOffset.Now;

    if (_state == UiState.ReferenceReady)
    {
        ExecuteReferenceAssistedMain(
            sourceImage,
            prompt,
            processedAt);

        return;
    }

    if (chkNoReference.Checked)
    {
        ExecuteNoReferenceMain(
            sourceImage,
            prompt,
            processedAt);

        return;
    }

    ShowMessageBox(
        "Save a Reference first or enable No reference mode.",
        "Main Image",
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);
}
```

NoReference preparation:

```csharp
private void ExecuteNoReferenceMain(
    string sourceImage,
    string prompt,
    DateTimeOffset processedAt)
{
    var settings =
        ReadSettingsFromUi();

    var assetName =
        txtAssetFolderName.Text;

    var assetFolder =
        Path.Combine(
            settings.AssetRootFolder,
            assetName);

    if (!ConfirmExistingAssetFolderIfNeeded(assetFolder))
    {
        return;
    }

    AssetSession session;

    try
    {
        session =
            _assetProcessorService
                .CreateNoReferenceMainSession(
                    settings,
                    assetName,
                    sourceImage,
                    prompt,
                    processedAt);

        _sessionService.Save(session);
    }
    catch (Exception ex)
    {
        ShowError(
            "Could not prepare no-reference Main transaction.",
            ex);
        return;
    }

    _currentSession = session;

    ExecuteMainCommit(
        session,
        sourceImage,
        prompt,
        processedAt);
}
```

Reference-assisted preparation must preserve the current “persist transaction metadata before writes” logic and additionally persist `WasIngameFolderCreatedByTool`.

---

# 31. Main failure behavior matrix

| Mode | Failure/rollback outcome | Required UI/session result |
|---|---|---|
| ReferenceAssisted | no write or rollback complete | reset Main metadata, save Reference session, remain ReferenceReady |
| ReferenceAssisted | rollback incomplete/untrusted | preserve journal, CRITICAL, close |
| NoReference | no write or rollback complete | delete session journal, return Idle |
| NoReference | rollback incomplete/untrusted | preserve journal, CRITICAL, close |
| Either | completion succeeded but session delete fails | rollback complete asset if exact ownership can be proven; otherwise CRITICAL + preserve journal + close |

Implement this table literally. Do not use one generic “reset metadata and save” branch for both modes.

---

# 31.1 Required failed-Main reconciliation helper

Do not rely only on the internal `ProcessMainImage()` catch cleanup. v1.1 adds an `ingame` directory/output and NoReference has different post-failure state semantics. After any ordinary Main processing exception for which the processor reports/guarantees that its immediate rollback was not already known to be incomplete, reconcile the active journal through `RollbackMain()` before deciding the UI state.

Recommended orchestration helper:

```csharp
private bool TryReconcileFailedMainCommit(
    AssetSession session,
    bool noReferenceMode)
{
    ValidationResult rollback;

    try
    {
        rollback =
            _assetProcessorService
                .RollbackMain(
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
        // The durable on-disk journal still contains the original active Main
        // metadata because RollbackMain only mutates the in-memory session.
        // Leave it for startup recovery; do not save an invalid NoReference idle session.
        ShowError(
            "CRITICAL: Main outputs were rolled back, but the no-reference session journal could not be removed.",
            ex);
        Close();
        return false;
    }
}
```

If `ProcessMainImage()` throws `AssetProcessingException` with `RollbackComplete == false`, **do not** call the ordinary reconciliation helper and do not attempt extra destructive cleanup. Preserve the active journal, show a critical error, and close exactly as the current safety model does.

### Completion succeeded but deleting session.json fails

Keep the current protective concept: a completed asset is not considered safely finalized while its active journal cannot be removed.

1. call `RollbackMain()` to remove the exact completed Main/provenance/ingame outputs;
2. ReferenceAssisted: save the reset Reference session and continue ReferenceReady;
3. NoReference: retry deleting `session.json`;
4. if that deletion still fails, **do not save the in-memory reset NoReference session**; leave the durable original active journal on disk and close. Startup recovery can then observe the active journal with no outputs and safely reconcile it.

Add direct tests for the two session-delete-failure branches.

---

# 32. Status messages

Keep existing useful messages and add the new production copy.

Reference save:

```text
Reference copied: <filename>
Reference provenance created.
Reference session saved.
```

Reference replacement:

```text
Reference replaced: <filename>
Main candidate and prompt cleared because the Reference changed.
```

Main:

```text
Main image copied: <original filename>
Ingame copy created: <AssetName.ext>
Final provenance created.
Asset completed.
```

NoReference additionally:

```text
No-reference Main transaction prepared.
```

Recovery should explicitly say whether it:

```text
completed leftover session cleanup
rolled back interrupted Main
resumed Reference session
```

---

# 33. Keyboard shortcuts

Preserve current semantics:

```text
Ctrl+R -> Reference / Replace Reference
Ctrl+M -> Main Image
```

Rules:

```text
Ctrl+R does nothing in NoReference mode
Ctrl+M works in ReferenceReady or NoReference Idle
Both do nothing while Help overlay is visible
```

Do not change Ctrl+R to mean Refresh in this release.

---

# 34. High-value automated tests to add

The following set is mandatory; more tests are welcome.

## Settings/migration

```text
LegacySettings_WithProjectName_Loads
NewSettings_SaveOmitsProjectName
EmptyDownloadFolder_IsAllowedForProcessing
EmptyDownloadFolder_BlocksRefreshOnly
AssetRootStillRequired
```

## Naming

```text
DeriveProjectLabel_FromNormalAssetRoot
AssetNameWithoutExtensionAccepted
AssetNameWithSupportedImageExtensionRejected
AssetNameWithNonImageDotAccepted
IngameFilenamePreservesSourceExtensionCase
```

## Discovery

```text
NewestSupportedImageSelectedRegardlessOfChatGPTPrefix
UnsupportedFilesIgnored
EmptyFolderReturnsNull
```

## Reference

```text
Reference_CreatesExpectedTree
Reference_DoesNotDeleteSource
Reference_ProvenanceUsesDerivedProjectLabel
Reference_SuccessClearsCandidate
Replacement_ClearsMainCandidateAndPrompt
```

## Ingame

```text
Main_CreatesIngameFolder
Main_CreatesRenamedIngameCopy
Main_IngameHashEqualsRootMainHash
Main_IngameBytesEqualRootMainBytes
Main_PreservesOriginalRootFilename
Main_PreservesSourceDownload
Main_RejectsExistingSameExtensionProductionFile
Main_RejectsExistingDifferentExtensionProductionFile
Main_RejectsIngameReparsePoint
```

## NoReference

```text
NoReference_PrecommitDoesNotCreateAssetFolder
NoReference_PrecommitSessionValidWithoutAssetFolder
NoReference_CommitCreatesAssetAndIngameFolders
NoReference_DoesNotCreateReferenceFolder
NoReference_UsesNoReferenceFinalTemplate
NoReference_ProvenanceContainsExactPrompt
NoReference_ProvenanceDoesNotRequireReferenceFilename
```

## Rollback/recovery

```text
RollbackMain_RemovesOwnedIngame
RollbackMain_PreservesModifiedIngame
RollbackMain_RemovesToolCreatedEmptyIngameFolder
RollbackNoReference_RemovesToolCreatedEmptyAssetFolder
RollbackNoReference_PreservesPreexistingAssetFolder
Recovery_NoReferenceImmediatelyAfterJournalSave
Recovery_NoReferenceAfterTempMain
Recovery_NoReferenceAfterRootMain
Recovery_NoReferenceAfterIngamePromotion
Recovery_NoReferenceAfterCompleteBeforeSessionDelete
Recovery_LegacyReferenceMainWithoutIngameReturnsReferenceReady
```

## Selection

```text
ReferenceRefreshDoesNotChangeMain
MainRefreshDoesNotChangeReference
ReferenceChooseDoesNotChangeMain
MainChooseDoesNotChangeReference
ReferenceDropDoesNotChangeMain
MainDropDoesNotChangeReference
DropRejectsMultipleFiles
DropRejectsUnsupportedFile
CTAUsesDisplayedSelectionWithoutImplicitRefresh
```

## UI

```text
NoProjectControl
ImageDownloadFolderLabelPresent
RootAutoScrollFalse
AllRequiredGroupsExist
ReferenceAndMainHaveIndependentRefreshButtons
ReferenceAndMainHaveIndependentChooseButtons
ReferenceAndMainHaveIndependentDropZones
NoReferenceHidesReferenceGroup
NoReferenceExpandsMainGroup
ActiveReferenceDisablesNoReference
EmptyAssetNameShowsErrorVisual
EmptyPromptShowsErrorVisual
MissingImageShowsSlotErrorVisual
HeaderShowsProductNameAndVersion
HelpButtonExists
```

---

# 35. Copy-ready core ingame test

```csharp
[Fact]
public void MainImage_CreatesByteIdenticalRenamedIngameCopy()
{
    using var workspace =
        new TestWorkspace();

    var settings =
        workspace.CreateSettings();

    var processor =
        workspace.CreateAssetProcessor();

    var reference =
        workspace.CreateImage(
            "reference.png",
            new byte[] { 1, 2, 3, 4 });

    var session =
        processor.ProcessReference(
            settings,
            "onboarding1",
            reference,
            DateTimeOffset.Now);

    var main =
        workspace.CreateImage(
            "ChatGPT final.jpg",
            new byte[] { 10, 20, 30, 40, 50 });

    processor.ProcessMainImage(
        session,
        settings.AcceptedExtensions,
        main,
        "final generation prompt",
        DateTimeOffset.Now);

    var rootMain =
        Path.Combine(
            session.AssetFolder,
            "ChatGPT final.jpg");

    var ingame =
        Path.Combine(
            session.AssetFolder,
            AppConstants.IngameFolderName,
            "onboarding1.jpg");

    Assert.True(File.Exists(rootMain));
    Assert.True(File.Exists(ingame));
    Assert.True(File.Exists(main));

    Assert.Equal(
        File.ReadAllBytes(rootMain),
        File.ReadAllBytes(ingame));
}
```

---

# 36. Copy-ready old-settings migration test

```csharp
[Fact]
public void LegacySettings_WithProjectName_LoadsAfterProjectRemoval()
{
    using var workspace =
        new TestWorkspace();

    var json = $$"""
    {
      "ProjectName": "OldGame",
      "DownloadFolder": "{{workspace.Downloads.Replace("\\", "\\\\")}}",
      "AssetRootFolder": "{{workspace.Assets.Replace("\\", "\\\\")}}",
      "AcceptedExtensions": [".png", ".webp", ".jpg", ".jpeg"]
    }
    """;

    File.WriteAllText(
        workspace.SettingsPath,
        json);

    var loaded =
        workspace
            .CreateSettingsService()
            .Load();

    Assert.Equal(workspace.Downloads, loaded.DownloadFolder);
    Assert.Equal(workspace.Assets, loaded.AssetRootFolder);
    Assert.Contains(".png", loaded.AcceptedExtensions);
}
```

Also serialize the loaded settings again and assert the new JSON no longer contains `ProjectName`.

---

# 37. Copy-ready no-reference prewrite recovery test concept

Implement using actual services rather than mocking filesystem semantics:

```csharp
[Fact]
public void NoReference_JournalCanExistBeforeAssetFolder()
{
    using var workspace =
        new TestWorkspace();

    var settings =
        workspace.CreateSettings();

    var processor =
        workspace.CreateAssetProcessor();

    var sessionService =
        workspace.CreateSessionService();

    var main =
        workspace.CreateImage(
            "main.webp",
            new byte[] { 8, 7, 6, 5 });

    var targetFolder =
        Path.Combine(
            workspace.Assets,
            "onboarding1");

    Assert.False(Directory.Exists(targetFolder));

    var session =
        processor.CreateNoReferenceMainSession(
            settings,
            "onboarding1",
            main,
            "prompt",
            DateTimeOffset.Now);

    sessionService.Save(session);

    Assert.True(sessionService.Exists());
    Assert.False(Directory.Exists(targetFolder));

    var validation =
        workspace
            .CreateValidationService()
            .ValidateSession(session);

    Assert.True(
        validation.IsValid,
        string.Join(Environment.NewLine, validation.Errors));
}
```

Then separately exercise MainForm recovery or RollbackMain to ensure the journal can be removed cleanly without creating the folder.

---

# 38. Manual GUI acceptance matrix

Automated WinForms structural tests are not sufficient.

| Display | Scaling | Required result |
|---|---:|---|
| 1366×768 | 100% | all main content visible; no main scrollbar |
| 1920×1080 | 100% | compact, balanced layout |
| 1920×1080 | 125% | no clipping/overlap |
| 1920×1080 | 150% | usable; no main scrollbar |
| 2560×1440 | 125% | no excessive stretched controls |

Verify in both:

```text
Reference mode
NoReference mode
```

Check long filenames and long paths.

No labels/buttons may disappear because a path widened a column.

---

# 39. Manual end-to-end workflows

## Reference-assisted

1. configure Asset Root;
2. optionally configure Image Download Folder;
3. Asset Name `onboarding1`;
4. select Reference by Refresh;
5. Reference CTA;
6. verify source remains;
7. verify reference tree;
8. create/select Main separately;
9. paste prompt;
10. Main CTA;
11. verify root Main retains source name;
12. verify ingame `onboarding1.ext`;
13. compare file hashes;
14. verify final provenance prompt + saved Reference;
15. verify session deleted.

Repeat Reference selection via:

```text
Choose File
Drop file here
```

Repeat Main selection via both methods.

## NoReference

1. enable No reference mode;
2. verify Reference controls fully hidden;
3. Asset Name;
4. Main choose/drop/refresh;
5. prompt;
6. Main CTA;
7. verify no tool-created reference folder;
8. verify root Main;
9. verify ingame renamed copy;
10. verify no-reference provenance;
11. verify session deleted.

## Reference replacement

1. save Reference A;
2. select Main candidate and type prompt;
3. select Reference B;
4. Replace Reference;
5. verify Main selection and prompt were cleared;
6. select fresh Main generated from Reference B;
7. finish asset.

---

# 40. CI/release acceptance

Run existing quality pipeline plus new tests.

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

Flakiness loop:

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

Add `AssetNaming` and any other non-trivial new production service/class to the CI coverage-presence list. Pure layout/theme classes may remain excluded if intentionally marked and behavior is exercised through UI tests.

---

# 41. Final static searches

Run after implementation.

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
rg -n "ProjectName" src tests
```

Expected intentional categories only:

```text
AssetSession compatibility/session state
Reference/new-session derived project assignment
provenance render/validation
legacy/session tests
```

There must be no `AppSettings.ProjectName` reference.

```powershell
rg -n "v1\.0\.0|AssetProvenanceHelper-v1\.0\.0" .
```

Expected: zero active release/package references. Historical changelog text would be the only acceptable exception if added later.

```powershell
rg -n "final_no_reference|NoReference|IngameFolderName" src tests
```

Expected: multiple intentional matches.

---

# 42. Weak-model implementation rules

These instructions are mandatory when handing a phase to a weaker model.

1. Implement **one phase only** per run.
2. Read the named files before editing them.
3. Treat method-name anchors as authoritative; approximate line numbers are navigation hints only.
4. Do not delete existing BUG-R* safety checks unless this plan explicitly replaces the check with an equivalent mode-aware version.
5. Do not simplify rollback/recovery because tests are difficult.
6. Do not replace atomic file writes with ordinary direct writes.
7. Do not introduce overwrite=true for canonical asset files.
8. Do not move source images.
9. Do not invent a database/config framework.
10. Do not introduce a new UI framework; stay on WinForms/.NET 8.
11. Do not add image thumbnails or unrelated features.
12. Do not alter the pinned .NET SDK version just because a local agent lacks it.
13. Do not rewrite giant regression test files wholesale. Add focused v1.1 test files and only minimally repair old tests that no longer compile because Project settings were removed.
14. Run phase tests before proceeding.
15. If a test fails, determine whether the implementation or an intentionally obsolete assertion is wrong; do not simply weaken the assertion.
16. Preserve exact provenance ownership verification.
17. Preserve fail-closed behavior for ambiguous files.
18. At phase end, summarize files changed, tests added, commands run, and any environment-limited check not executed.

---

# 43. Definition of Done

The rework is accepted only when all items are true.

## Settings/naming

```text
[ ] Project textbox is gone.
[ ] AppSettings.ProjectName is gone.
[ ] Legacy settings containing ProjectName still load.
[ ] Newly saved settings omit ProjectName.
[ ] New AssetSession.ProjectName is derived from Asset Root folder name.
[ ] Old AssetSession.ProjectName remains recoverable.
[ ] Image Download Folder wording is browser-neutral.
[ ] Image Download Folder may be empty for manual choose/drop workflows.
[ ] Refresh clearly rejects an absent/unavailable Image Download Folder.
[ ] Asset Name is required for a new asset.
[ ] Asset Name rejects supported image extensions.
```

## File workflow

```text
[ ] Reference source is copied, not moved.
[ ] Main source is copied, not moved.
[ ] Reference creates <asset>/reference lazily.
[ ] Main creates <asset>/ingame lazily.
[ ] root Main keeps the exact source filename.
[ ] ingame Main is named AssetName + source extension.
[ ] ingame Main is byte/SHA identical to root Main.
[ ] stale ingame variants with another supported extension block commit.
[ ] no canonical output is silently overwritten.
```

## Reference/Main selection

```text
[ ] Reference and Main have independent state.
[ ] Each has Refresh.
[ ] Each has Choose File.
[ ] Each has Drop file here.
[ ] Each has its own selected-file display.
[ ] Reference actions never change Main selection except successful replacement intentionally clears it.
[ ] Main actions never change Reference candidate/saved Reference.
[ ] CTAs do not implicitly Refresh.
[ ] successful Reference replacement clears Main candidate + Final Prompt.
```

## NoReference

```text
[ ] checkbox exists.
[ ] Reference controls are fully hidden when active.
[ ] Ctrl+R does not act in NoReference mode.
[ ] NoReference Main needs no Reference session.
[ ] journal is durable before first output mutation.
[ ] prewrite journal with absent asset folder validates/recover safely.
[ ] NoReference creates no tool-owned reference artifacts.
[ ] NoReference provenance uses dedicated template.
[ ] clean NoReference failure removes journal and returns Idle.
[ ] untrusted NoReference rollback preserves journal/files and closes safely.
```

## Recovery/safety

```text
[ ] root Main/provenance/ingame all participate in completion validation.
[ ] ingame rollback verifies deterministic path + MainHash.
[ ] temp ingame rollback verifies ownership.
[ ] exact final provenance validation is workflow-mode-aware.
[ ] destructive path validation is workflow-mode-aware.
[ ] legacy v1.0 Reference sessions recover.
[ ] v1.0 in-progress Main without ingame rolls back to ReferenceReady.
[ ] completed v1.0 assets with no session remain untouched.
```

## GUI

```text
[ ] main window does not scroll.
[ ] Settings group is top content group.
[ ] Current Asset group is second.
[ ] Reference/Main are visually boxed.
[ ] Status is lowest group.
[ ] Reference/Main CTAs visually differ from normal buttons.
[ ] invalid required fields get red outline.
[ ] invalid CTA turns red and pulses for a bounded duration.
[ ] focus moves to first invalid editable field.
[ ] header contains icon + product name + assembly-derived v1.1.0.
[ ] ? button opens a true in-form overlay.
[ ] overlay contains legal/general information and Made by CeeGore.
[ ] Help blocks underlying shortcuts while open.
[ ] application/window executable uses colorful icon.
```

## QA/release

```text
[ ] README matches actual workflow.
[ ] Debug build passes with warnings as errors.
[ ] Debug tests pass.
[ ] Release build passes with warnings as errors.
[ ] Release tests pass.
[ ] 20/20 repeated Release tests pass.
[ ] coverage generation/gate passes.
[ ] self-contained win-x64 publish passes.
[ ] smoke test verifies all three templates.
[ ] smoke test verifies stable window startup/title/shutdown.
[ ] package name derives from compiled v1.1.0 rather than hardcoded v1.0.0.
[ ] manual Reference workflow passes via Refresh, Choose, and Drop.
[ ] manual NoReference workflow passes via Refresh, Choose, and Drop.
[ ] manual Reference replacement invalidation flow passes.
[ ] DPI/layout matrix passes without main-window scrolling/clipping.
[ ] final static searches contain no obsolete Firefox/txtProject/shared-selection/release-name references.
```

---

# 44. Final implementation judgment

With the corrections in this v2, the rework is well-bounded and can be implemented safely by weaker models **without leaving product-design decisions for implementation time**.

The central strategy is:

```text
preserve the existing transactional safety core
        +
remove Project from user settings cleanly
        +
make DownloadFolder optional for manual selection
        +
make Reference/Main source state fully independent
        +
make ingame an atomic Main output
        +
model NoReference as a real workflow mode
        +
make validation/recovery explicitly mode-aware
        +
rebuild the WinForms layout around those domain rules
```

Do not re-open these architectural choices during implementation unless an actual repository contradiction is proven by tests. If an implementation phase uncovers such a contradiction, preserve safety first, document the evidence, and repair the plan/implementation narrowly rather than improvising a new workflow.

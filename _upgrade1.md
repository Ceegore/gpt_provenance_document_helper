# _upgrade1.md

**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Current product version:** `1.2.1`  
**Target version:** `1.3.0`  
**Target framework:** `.NET 10 / Windows Forms`  
**Document purpose:** authoritative, implementation-ready upgrade specification for a weak implementation model  
**Priority:** correctness and preservation of existing transaction/recovery guarantees before refactoring, elegance, or code reduction

---

# 0. STATUS OF THIS DOCUMENT

This document **supersedes the previous v1.3 implementation plan in this conversation**.

The previous plan had the right product direction but still left too many decisions to the implementation model and contained several integration problems that only become visible when the proposed changes are checked against the current repository.

The repository has now been inspected specifically around:

- `MainForm` construction and state handling;
- `MainForm.Layout.cs`;
- `MainForm.MainWorkflow.cs`;
- `MainForm.ReferenceWorkflow.cs`;
- `MainForm.ImageSelection.cs`;
- `MainForm.ValidationUi.cs`;
- `MainForm.Recovery.cs`;
- `AssetSession`;
- `AssetProcessorService.Main.cs`;
- `AssetProcessorService.Reference.cs`;
- `ValidationService`;
- `ValidationService.Session.cs`;
- `ValidationService.Paths.cs`;
- `TemplateService`;
- `SessionService`;
- `SettingsService`;
- `AppBootstrap`;
- `Program`;
- `TestWorkspace`;
- current UI tests;
- current publish/smoke-test infrastructure.

The current application already contains strong transaction, hash, rollback, path-safety, reparse-point and recovery protections. The upgrade must **reuse those protections instead of building parallel workflows around them**.

---

# 1. NON-NEGOTIABLE IMPLEMENTATION RULE

The implementation model must follow this rule:

> **When no Request Manifest is loaded and Direct mode is OFF, existing v1.2.1 workflows must continue to behave like v1.2.1 except for the deliberate Provider-template output change for newly started v1.3 assets and the fixed recent-document display.**

Do **not** rewrite the existing file transaction architecture.

Do **not** introduce a second Main processor.

Do **not** introduce a second Reference processor.

Do **not** bypass `AssetProcessorService`.

Do **not** bypass `SessionService`.

Do **not** weaken exact-provenance/hash checks.

Do **not** make Direct mode itself write files.

Direct mode is only an **input-selection/orchestration layer** around the existing processors.

---

# 2. AUDIT OF THE PREVIOUS PLAN — ERRORS AND AMBIGUITIES THAT MUST BE CORRECTED

This section is important. The implementation model must understand why some parts of the previous plan are deliberately replaced.

## 2.1 Critical problem: arbitrary Provider templates conflict with current hard-coded provenance validation

The previous plan said that Provider templates could have arbitrary structure, but the current validation system contains methods such as:

```text
ValidateReferenceProvenanceContent(...)
ValidateFinalProvenanceContent(...)
```

which currently expect literal text such as:

```text
Asset ID:
Project:
Generation date:
```

in the generated file. Therefore an otherwise-valid arbitrary Provider template would fail current validation merely because it used different headings or a different layout.

**Correction in this plan:** Provider-based v1.3 sessions use exact rendered-template/hash validation and **do not use the hard-coded legacy heading checks**. Legacy sessions continue using the old validation path.

This is release-critical.

---

## 2.2 Previous date proposal was inconsistent with current provenance semantics

The previous plan proposed an ISO date/time such as:

```text
2026-08-26T00:34:12+02:00
```

for `<<<DATE>>>`.

The current processors consistently render provenance dates as:

```csharp
processedAt.ToString(
    "yyyy-MM-dd",
    CultureInfo.InvariantCulture)
```

for Reference and Main.

Changing this unnecessarily increases migration/recovery complexity.

**Final decision:**

```text
<<<DATE>>> = yyyy-MM-dd
```

Example:

```text
2026-08-26
```

No time-of-day is introduced in v1.3.

---

## 2.3 Previous MainForm constructor proposal would cause unnecessary test churn

The repository contains many tests constructing:

```csharp
new MainForm(
    settings,
    settingsService,
    imageFinder,
    templateService,
    validationService,
    assetProcessor,
    sessionService)
```

directly.

Blindly making three new constructor parameters mandatory would force changes across many existing tests.

**Correction:**

append optional service parameters:

```csharp
ProviderTemplateCatalogService? providerTemplateCatalogService = null,
RecentDocumentHistoryService? recentDocumentHistoryService = null,
RequestProgressService? requestProgressService = null
```

Production passes them explicitly.

Existing tests remain source-compatible.

When these optional services are `null`, the form runs in compatibility/test mode for the new optional functionality.

---

## 2.4 Do not change `AssetSession.SchemaVersion` default from `2`

The current `AssetSession` has:

```csharp
public int SchemaVersion { get; set; } = 2;
```

and a very large number of tests and helper-created sessions rely on default construction.

Changing the property default to `3` would cause manually created test/legacy sessions without a Provider snapshot to suddenly be considered v1.3 sessions.

**Final decision:**

Keep:

```csharp
public int SchemaVersion { get; set; } = 2;
```

unchanged.

New production sessions created with a Provider snapshot explicitly set:

```csharp
SchemaVersion = 3
```

Legacy/test sessions without Provider snapshots remain schema 2.

---

## 2.5 Provider metadata must survive Reference Replacement

`CreateReferenceReplacementTransaction()` currently constructs new `AssetSession` objects manually and explicitly copies fields.

If the implementation merely adds:

```csharp
ProviderTemplate
SourceRequestKey
```

to `AssetSession` without changing this code, Reference Replacement will silently lose those fields.

**Correction:** both replacement `OldSession` authority and replacement `NewSession` must explicitly preserve:

```text
SchemaVersion
ProviderTemplate
SourceRequestKey
```

as specified later.

---

## 2.6 Request progress was not sufficiently scoped

The previous plan proposed a global list of completed Request keys.

That could cause a later, unrelated Request document containing the same item to appear completed unexpectedly.

**Correction:**

Progress is scoped to a **ManifestFingerprint**.

`request-progress.json` stores:

```text
ManifestFingerprint
CompletedRequestKeys
```

If an imported manifest has a different semantic fingerprint, its initial completed set is empty.

---

## 2.7 A queue-bound Reference session must survive application restart

Suppose:

```text
Request A selected
→ Reference A processed
→ app closes
→ app restarts
```

The existing `session.json` recovers the Reference, but an in-memory `_activeRequest` would be lost.

Without additional session metadata, the application could no longer reliably associate the recovered Reference with Request A.

**Correction:**

add nullable:

```csharp
public string? SourceRequestKey { get; set; }
```

to `AssetSession`.

A queue-originated Reference session persists this Request key.

A manual session keeps it `null`.

---

## 2.8 Queue completion cannot rely only on `_activeRequest`

If a Reference session is recovered after restart, `_activeRequest` may not yet exist.

Completion therefore uses:

1. `_activeRequest?.RequestKey`, if bound;
2. otherwise `_currentSession.SourceRequestKey`, where applicable.

The Request key is durable session metadata.

---

## 2.9 User edits could make a queue item falsely complete

Example:

```text
Queue Request A selected
Prompt A copied into field
user manually edits prompt to Prompt B
Main succeeds
```

It would be incorrect to mark Request A complete.

**Correction:**

Queue binding has explicit invalidation.

If Asset Name or Prompt deviates from the selected Request item through normal user editing:

```text
_activeRequest = null
```

The asset can still be processed manually, but the Request item is **not** marked Done.

The user may re-select the Request item to restore the exact Request fields.

---

## 2.10 Reference Replacement currently clears Prompt

After successful Reference Replacement, current code deliberately clears Main candidate and Prompt.

For a queue-bound asset this would leave the queue locked by an active Reference session while removing the prescribed Request prompt.

**Correction:**

- manual workflow: preserve existing behavior and clear Prompt;
- queue-bound workflow: clear Main image candidate but immediately restore the exact Request prompt under a programmatic-binding guard.

---

## 2.11 Direct mode hotkeys were underspecified

Current `Ctrl+M` only works for:

```text
ReferenceReady
or
NoReference
```

and `Ctrl+R` starts Reference processing.

In Direct Reference mode:

- `Ctrl+R` must **not** process Reference;
- `Ctrl+M` must invoke the same Direct orchestrator as the Main Image button even from Reference-assisted Idle.

This plan defines the exact changes.

---

## 2.12 Provider availability must not block recovered legacy sessions

If a user deletes all custom Provider templates but an old v1.2 Reference session exists, that session can still be completed using legacy templates stored with the application.

Therefore:

```text
No valid Provider templates
```

blocks **starting a new v1.3 asset**, but does **not** block completing/recovering an existing legacy session.

---

## 2.13 Custom Markdown parsing for the Request Manifest is unnecessary and fragile

The previous plan defined custom markers such as:

```text
<<<ASSET_START>>>
<<<PROMPT_START>>>
```

This introduces delimiter-collision handling and a custom parser even though the application already uses `System.Text.Json`.

**Correction: Request Manifest format is JSON.**

This is more deterministic for a weak implementation model and removes an entire custom grammar.

The Provider templates remain `.md`; only Request import becomes JSON.

---

## 2.14 Imported manifests must reject unknown fields

A third-party AI could output:

```json
{
  "file": "...",
  "dimensions": "...",
  "description": "..."
}
```

instead of the required schema.

Silently ignoring unknown members makes malformed manifests dangerous.

**Correction:** JSON deserialization uses:

```csharp
JsonUnmappedMemberHandling.Disallow
```

and exact property names.

---

## 2.15 Successful import must be atomic

Never partially import:

```text
149 valid records
1 invalid record
```

The outcome is:

```text
0 records imported
```

with an error identifying the failing item.

---

## 2.16 Recent Documents must not be implemented by truncating the existing debug/action log

Current `AddStatus()` records many internal messages:

```text
Templates validated
Reference copied
Ingame copy created
Asset completed
...
```

into `txtStatusHistory`.

The requested UI has different semantics:

> exactly the most recent maximum three **generated provenance documents**.

**Correction:**

Keep `txtStatusHistory` as a hidden compatibility/debug control so old code/tests do not need wholesale changes.

Add a new visible:

```text
lvRecentDocuments
```

for the requested three-document view.

---

## 2.17 Current empty History UI has a layout problem as well

The current status GroupBox uses an auto-sized outer group with an inner first row defined as `Percent, 100%`. This is a poor combination for an auto-sized bottom row and can collapse the visible history area.

The new status group must have a defined/minimum height.

---

## 2.18 Provider output must be exact template substitution only

No application-generated text is automatically appended to a Provider template.

If the template says:

```markdown
Hello <<<FILENAME>>>
```

the resulting provenance document is only the rendered form of that template.

The program must not secretly append:

```text
legal disclaimer
OpenAI terms
provider notes
helper notes
```

unless those are part of the selected `.md` template.

---

# 3. FINAL PRODUCT DEFINITION

v1.3 adds five user-visible capabilities:

1. **AI Provider Template dropdown**
2. **Recent provenance documents (max 3)**
3. **Prompt Preview with full hover overlay**
4. **Asset Request Manifest + right-side queue**
5. **Direct mode**

Everything else remains existing application behavior.

---

# 4. PROVIDER TEMPLATE SYSTEM — FINAL SPECIFICATION

## 4.1 Directory

Provider templates live only in:

```text
<application directory>\provider_templates\
```

Example published tree:

```text
AssetProvenanceHelper.exe
provider_templates\
    ChatGPT.md
    _TEMPLATE.md
templates\
    reference.md
    final.md
    final_no_reference.md
examples\
    asset_request_manifest_template.json
    asset_request_conversion_prompt.txt
```

The old `templates\` directory remains.

It is needed for v1.2/legacy session compatibility.

---

# 5. PROVIDER FILE DISCOVERY

On application startup, scan:

```text
provider_templates\*.md
```

with:

```text
SearchOption.TopDirectoryOnly
```

Do not recurse.

Rules:

1. File must end in `.md`.
2. Filename beginning with `_` is ignored deliberately.
3. File must not be a reparse point.
4. File must be valid UTF-8.
5. Maximum decoded template size: **256 KiB UTF-8**.
6. Template must satisfy the mandatory tag rules below.
7. Invalid template is skipped.
8. One bad template does not prevent the application from starting.
9. Dropdown order is case-insensitive alphabetical by display name.
10. Provider display name is filename without `.md`.

Example:

```text
provider_templates\ChatGPT.md
```

becomes:

```text
ChatGPT
```

No provider ID file is required.

No JSON provider file exists.

---

# 6. EXACT PROVIDER TAG SET

There are **exactly nine supported Provider tags in v1.3**.

Every selectable template must contain **all nine at least once**.

```text
<<<PROVIDER>>>
<<<DATE>>>
<<<FILENAME>>>
<<<ASSET_NAME>>>
<<<PROJECT>>>
<<<ROLE>>>
<<<WORKFLOW>>>
<<<REFERENCE_FILENAME>>>
<<<PROMPT>>>
```

There are no optional additional `<<<...>>>` fields in v1.3.

This is deliberate: a weak implementation model should not decide what is mandatory.

---

# 7. WHY ALL NINE TAGS ARE REQUIRED

The old provenance documents already capture most of these concepts dynamically:

- asset file;
- project;
- recorded date;
- reference relationship;
- prompt;
- asset role/workflow.

The new free-form system must not allow a user to accidentally create a template that omits all useful provenance information merely because its prose format is arbitrary.

Arbitrary **layout** is allowed.

Arbitrary omission of the core dynamic data is not.

---

# 8. EXACT PROVIDER TAG VALUES

## 8.1 Reference document

Given:

```text
Provider template: ChatGPT.md
Asset Name: asset_ui_screen_settings
Project: Roswell
Reference file: reference-image.png
Reference processed at: 2026-08-26
```

values are:

| Tag | Value |
|---|---|
| `<<<PROVIDER>>>` | `ChatGPT` |
| `<<<DATE>>>` | `2026-08-26` |
| `<<<FILENAME>>>` | `reference-image.png` |
| `<<<ASSET_NAME>>>` | `asset_ui_screen_settings` |
| `<<<PROJECT>>>` | `Roswell` |
| `<<<ROLE>>>` | `Intermediate reference image` |
| `<<<WORKFLOW>>>` | `reference-assisted` |
| `<<<REFERENCE_FILENAME>>>` | `reference-image.png` |
| `<<<PROMPT>>>` | `not recorded` |

Important:

**Do not put the Final Prompt into Reference provenance.**

The helper does not currently collect a separate Reference-generation prompt.

The truthful value is:

```text
not recorded
```

---

## 8.2 Reference-assisted Final document

| Tag | Value |
|---|---|
| `<<<PROVIDER>>>` | selected Provider display name |
| `<<<DATE>>>` | Main processed date, `yyyy-MM-dd` |
| `<<<FILENAME>>>` | actual Main source filename copied to asset root |
| `<<<ASSET_NAME>>>` | `AssetFolderName` |
| `<<<PROJECT>>>` | `session.ProjectName` |
| `<<<ROLE>>>` | `Final production asset` |
| `<<<WORKFLOW>>>` | `reference-assisted` |
| `<<<REFERENCE_FILENAME>>>` | saved Reference filename |
| `<<<PROMPT>>>` | exact Final Prompt |

---

## 8.3 No-reference Final document

| Tag | Value |
|---|---|
| `<<<PROVIDER>>>` | selected Provider display name |
| `<<<DATE>>>` | Main processed date, `yyyy-MM-dd` |
| `<<<FILENAME>>>` | actual Main source filename |
| `<<<ASSET_NAME>>>` | Asset Name |
| `<<<PROJECT>>>` | derived project label |
| `<<<ROLE>>>` | `Final production asset` |
| `<<<WORKFLOW>>>` | `no-reference` |
| `<<<REFERENCE_FILENAME>>>` | `not recorded` |
| `<<<PROMPT>>>` | exact Final Prompt |

---

# 9. TAG REPLACEMENT RULES

All tags are:

- case-sensitive;
- replaced literally;
- may appear multiple times;
- always replaced;
- never escaped;
- processed in a **single pass**.

Examples that are invalid:

```text
<<<date>>>
<<<Prompt>>>
<<<MODEL>>>
<<<SEED>>>
```

Unknown tags make the template invalid.

---

# 10. SINGLE-PASS REPLACEMENT IS MANDATORY

Suppose Final Prompt is:

```text
Please paint the literal text <<<DATE>>> in the image.
```

Template:

```text
Date: <<<DATE>>>

Prompt:
<<<PROMPT>>>
```

Correct result:

```text
Date: 2026-08-26

Prompt:
Please paint the literal text <<<DATE>>> in the image.
```

Incorrect result:

```text
Prompt:
Please paint the literal text 2026-08-26 in the image.
```

Therefore never implement sequential:

```csharp
text.Replace("<<<PROMPT>>>", prompt)
    .Replace("<<<DATE>>>", date);
```

Use one Regex replacement over the original template.

---

# 11. TEMPLATE ORIGINAL MUST NEVER BE MODIFIED

The implementation is conceptually:

```text
read template
→ hold copy in memory
→ substitute tags in copy
→ write rendered provenance document
```

Do not edit:

```text
provider_templates\ChatGPT.md
```

Do not copy then mutate it in place.

Do not write state into Provider templates.

---

# 12. COPY-READY PROVIDER CONSTANTS

Add to `AppConstants.cs`:

```csharp
public const string ProviderTemplateFolderName =
    "provider_templates";

public const string DefaultProviderTemplateFileName =
    "ChatGPT.md";

public const string RecentDocumentsFileName =
    "recent-documents.json";

public const string RequestProgressFileName =
    "request-progress.json";

public const string ReferenceRoleLabel =
    "Intermediate reference image";

public const string FinalRoleLabel =
    "Final production asset";

public const string ReferenceAssistedWorkflowLabel =
    "reference-assisted";

public const string NoReferenceWorkflowLabel =
    "no-reference";

public const string NotRecordedValue =
    "not recorded";
```

Do not change the existing provenance filenames.

---

# 13. NEW FILE — `Models/ProviderTemplateSnapshot.cs`

Use this complete file:

```csharp
namespace AssetProvenanceHelper.Models;

public sealed class ProviderTemplateSnapshot
{
    public string FileName { get; set; } =
        string.Empty;

    public string DisplayName { get; set; } =
        string.Empty;

    public string ContentSha256 { get; set; } =
        string.Empty;

    public string Content { get; set; } =
        string.Empty;

    public ProviderTemplateSnapshot Clone()
    {
        return new ProviderTemplateSnapshot
        {
            FileName =
                FileName,

            DisplayName =
                DisplayName,

            ContentSha256 =
                ContentSha256,

            Content =
                Content
        };
    }
}
```

---

# 14. NEW FILE — `Models/ProviderTemplateDefinition.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public sealed class ProviderTemplateDefinition
{
    public required string FileName { get; init; }

    public required string DisplayName { get; init; }

    public required string FullPath { get; init; }

    public required string ContentSha256 { get; init; }

    public required string Content { get; init; }

    public bool IsSessionSnapshot { get; init; }

    public ProviderTemplateSnapshot CreateSnapshot()
    {
        return new ProviderTemplateSnapshot
        {
            FileName =
                FileName,

            DisplayName =
                DisplayName,

            ContentSha256 =
                ContentSha256,

            Content =
                Content
        };
    }

    public static ProviderTemplateDefinition FromSnapshot(
        ProviderTemplateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ProviderTemplateDefinition
        {
            FileName =
                snapshot.FileName,

            DisplayName =
                snapshot.DisplayName
                + " (session snapshot)",

            FullPath =
                string.Empty,

            ContentSha256 =
                snapshot.ContentSha256,

            Content =
                snapshot.Content,

            IsSessionSnapshot =
                true
        };
    }

    public override string ToString()
    {
        return DisplayName;
    }
}
```

---

# 15. NEW FILE — `Models/ProviderCatalogResult.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public sealed class ProviderCatalogResult
{
    public List<ProviderTemplateDefinition> Templates { get; }
        = new();

    public List<string> Errors { get; }
        = new();

    public bool HasUsableTemplates =>
        Templates.Count > 0;
}
```

---

# 16. NEW FILE — `Services/ProviderTemplateRules.cs`

This is the **single source of truth** for Provider template rules.

Do not duplicate the tag list elsewhere.

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public static class ProviderTemplateRules
{
    public const int MaxTemplateBytes =
        256 * 1024;

    public static readonly IReadOnlyList<string> RequiredTags =
        new[]
        {
            "<<<PROVIDER>>>",
            "<<<DATE>>>",
            "<<<FILENAME>>>",
            "<<<ASSET_NAME>>>",
            "<<<PROJECT>>>",
            "<<<ROLE>>>",
            "<<<WORKFLOW>>>",
            "<<<REFERENCE_FILENAME>>>",
            "<<<PROMPT>>>"
        };

    private static readonly HashSet<string> SupportedTags =
        new(
            RequiredTags,
            StringComparer.Ordinal);

    private static readonly Regex AnyTagRegex =
        new(
            @"<<<[^<>\r\n]+>>>",
            RegexOptions.Compiled
            | RegexOptions.CultureInvariant);

    public static ValidationResult ValidateContent(
        string fileName,
        string content)
    {
        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(content))
        {
            errors.Add(
                $"Provider template '{fileName}' is empty.");

            return ValidationResult.Failure(errors);
        }

        var utf8Length =
            Encoding.UTF8.GetByteCount(content);

        if (utf8Length > MaxTemplateBytes)
        {
            errors.Add(
                $"Provider template '{fileName}' exceeds the {MaxTemplateBytes} byte UTF-8 limit.");
        }

        foreach (var requiredTag in RequiredTags)
        {
            if (!content.Contains(
                    requiredTag,
                    StringComparison.Ordinal))
            {
                errors.Add(
                    $"Provider template '{fileName}' is missing required tag {requiredTag}.");
            }
        }

        foreach (Match match in AnyTagRegex.Matches(content))
        {
            if (!SupportedTags.Contains(match.Value))
            {
                errors.Add(
                    $"Provider template '{fileName}' contains unsupported tag {match.Value}.");
            }
        }

        var withoutRecognizedTags =
            AnyTagRegex.Replace(
                content,
                string.Empty);

        if (withoutRecognizedTags.Contains(
                "<<<",
                StringComparison.Ordinal)
            || withoutRecognizedTags.Contains(
                ">>>",
                StringComparison.Ordinal))
        {
            errors.Add(
                $"Provider template '{fileName}' contains malformed <<<...>>> tag delimiters.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public static ValidationResult ValidateSnapshot(
        ProviderTemplateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(snapshot.FileName))
        {
            errors.Add(
                "Provider snapshot FileName is missing.");
        }
        else
        {
            if (!string.Equals(
                    Path.GetFileName(snapshot.FileName),
                    snapshot.FileName,
                    StringComparison.Ordinal))
            {
                errors.Add(
                    "Provider snapshot FileName must contain only a filename.");
            }

            if (!string.Equals(
                    Path.GetExtension(snapshot.FileName),
                    ".md",
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    "Provider snapshot FileName must use .md.");
            }
        }

        if (string.IsNullOrWhiteSpace(snapshot.DisplayName))
        {
            errors.Add(
                "Provider snapshot DisplayName is missing.");
        }

        var contentValidation =
            ValidateContent(
                snapshot.FileName,
                snapshot.Content);

        if (!contentValidation.IsValid)
        {
            errors.AddRange(
                contentValidation.Errors);
        }

        var actualHash =
            ComputeContentSha256(
                snapshot.Content);

        if (string.IsNullOrWhiteSpace(
                snapshot.ContentSha256)
            || snapshot.ContentSha256.Length != 64
            || snapshot.ContentSha256.Any(
                c => !Uri.IsHexDigit(c)))
        {
            errors.Add(
                "Provider snapshot ContentSha256 is missing or invalid.");
        }
        else if (!string.Equals(
                     actualHash,
                     snapshot.ContentSha256,
                     StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "Provider snapshot content does not match ContentSha256.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public static string ComputeContentSha256(
        string content)
    {
        var bytes =
            new UTF8Encoding(false)
                .GetBytes(content);

        return Convert
            .ToHexString(
                SHA256.HashData(bytes))
            .ToLowerInvariant();
    }
}
```

---

# 17. NEW FILE — `Services/ProviderTemplateCatalogService.cs`

Use this implementation.

```csharp
using System.Text;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class ProviderTemplateCatalogService
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    private readonly string _templateDirectory;

    public ProviderTemplateCatalogService(
        string templateDirectory)
    {
        _templateDirectory =
            templateDirectory;
    }

    public string TemplateDirectory =>
        _templateDirectory;

    public ProviderCatalogResult Load()
    {
        var result =
            new ProviderCatalogResult();

        if (!Directory.Exists(
                _templateDirectory))
        {
            result.Errors.Add(
                $"Provider template directory does not exist: {_templateDirectory}");

            return result;
        }

        string[] files;

        try
        {
            files =
                Directory
                    .EnumerateFiles(
                        _templateDirectory,
                        "*.md",
                        SearchOption.TopDirectoryOnly)
                    .OrderBy(
                        Path.GetFileName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
        catch (Exception ex)
        {
            result.Errors.Add(
                $"Could not scan provider template directory: {ex.Message}");

            return result;
        }

        var seenDisplayNames =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var fileName =
                Path.GetFileName(path);

            if (fileName.StartsWith(
                    "_",
                    StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var info =
                    new FileInfo(path);

                if ((info.Attributes
                     & FileAttributes.ReparsePoint) != 0)
                {
                    result.Errors.Add(
                        $"Provider template '{fileName}' is a reparse point and was ignored.");

                    continue;
                }

                if (info.Length <= 0)
                {
                    result.Errors.Add(
                        $"Provider template '{fileName}' is empty.");

                    continue;
                }

                if (info.Length
                    > ProviderTemplateRules.MaxTemplateBytes + 3)
                {
                    result.Errors.Add(
                        $"Provider template '{fileName}' exceeds the size limit.");

                    continue;
                }

                var raw =
                    File.ReadAllBytes(path);

                var content =
                    DecodeUtf8(raw);

                var validation =
                    ProviderTemplateRules.ValidateContent(
                        fileName,
                        content);

                if (!validation.IsValid)
                {
                    result.Errors.AddRange(
                        validation.Errors);

                    continue;
                }

                var displayName =
                    Path.GetFileNameWithoutExtension(
                        fileName);

                if (!seenDisplayNames.Add(
                        displayName))
                {
                    result.Errors.Add(
                        $"Provider display name '{displayName}' is duplicated. File '{fileName}' was ignored.");

                    continue;
                }

                result.Templates.Add(
                    new ProviderTemplateDefinition
                    {
                        FileName =
                            fileName,

                        DisplayName =
                            displayName,

                        FullPath =
                            info.FullName,

                        ContentSha256 =
                            ProviderTemplateRules
                                .ComputeContentSha256(
                                    content),

                        Content =
                            content,

                        IsSessionSnapshot =
                            false
                    });
            }
            catch (Exception ex)
            {
                result.Errors.Add(
                    $"Could not load provider template '{fileName}': {ex.Message}");
            }
        }

        result.Templates.Sort(
            (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(
                    left.DisplayName,
                    right.DisplayName));

        return result;
    }

    private static string DecodeUtf8(
        byte[] raw)
    {
        var offset = 0;

        if (raw.Length >= 3
            && raw[0] == 0xEF
            && raw[1] == 0xBB
            && raw[2] == 0xBF)
        {
            offset = 3;
        }

        if (raw.Length >= 2
            && ((raw[0] == 0xFF
                 && raw[1] == 0xFE)
                || (raw[0] == 0xFE
                    && raw[1] == 0xFF)))
        {
            throw new InvalidDataException(
                "Provider templates must be saved as UTF-8, not UTF-16.");
        }

        return StrictUtf8.GetString(
            raw,
            offset,
            raw.Length - offset);
    }
}
```

---

# 18. NEW FILE — `Models/ProviderRenderContext.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public sealed class ProviderRenderContext
{
    public string Provider { get; init; } =
        string.Empty;

    public string Date { get; init; } =
        string.Empty;

    public string Filename { get; init; } =
        string.Empty;

    public string AssetName { get; init; } =
        string.Empty;

    public string Project { get; init; } =
        string.Empty;

    public string Role { get; init; } =
        string.Empty;

    public string Workflow { get; init; } =
        string.Empty;

    public string ReferenceFilename { get; init; } =
        string.Empty;

    public string Prompt { get; init; } =
        string.Empty;
}
```

---

# 19. NEW FILE — `Services/ProviderTemplateRenderer.cs`

```csharp
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public static class ProviderTemplateRenderer
{
    private static readonly Regex TagRegex =
        new(
            @"<<<[^<>\r\n]+>>>",
            RegexOptions.Compiled
            | RegexOptions.CultureInvariant);

    public static string Render(
        ProviderTemplateSnapshot snapshot,
        ProviderRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        var snapshotValidation =
            ProviderTemplateRules.ValidateSnapshot(
                snapshot);

        if (!snapshotValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(
                    Environment.NewLine,
                    snapshotValidation.Errors));
        }

        var values =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["<<<PROVIDER>>>"] =
                    context.Provider,

                ["<<<DATE>>>"] =
                    context.Date,

                ["<<<FILENAME>>>"] =
                    context.Filename,

                ["<<<ASSET_NAME>>>"] =
                    context.AssetName,

                ["<<<PROJECT>>>"] =
                    context.Project,

                ["<<<ROLE>>>"] =
                    context.Role,

                ["<<<WORKFLOW>>>"] =
                    context.Workflow,

                ["<<<REFERENCE_FILENAME>>>"] =
                    context.ReferenceFilename,

                ["<<<PROMPT>>>"] =
                    context.Prompt
            };

        return TagRegex.Replace(
            snapshot.Content,
            match =>
            {
                if (!values.TryGetValue(
                        match.Value,
                        out var value))
                {
                    throw new InvalidDataException(
                        $"Unsupported provider template tag {match.Value}.");
                }

                return value;
            });
    }
}
```

---

# 20. REQUIRED TEST FOR SINGLE-PASS BEHAVIOR

Add:

```csharp
[Fact]
public void ProviderRenderer_DoesNotProcessTagsInsideInsertedPrompt()
{
    var template =
        """
        Date: <<<DATE>>>
        Provider: <<<PROVIDER>>>
        File: <<<FILENAME>>>
        Asset: <<<ASSET_NAME>>>
        Project: <<<PROJECT>>>
        Role: <<<ROLE>>>
        Workflow: <<<WORKFLOW>>>
        Reference: <<<REFERENCE_FILENAME>>>
        Prompt: <<<PROMPT>>>
        """;

    var snapshot =
        new ProviderTemplateSnapshot
        {
            FileName = "Test.md",
            DisplayName = "Test",
            Content = template,
            ContentSha256 =
                ProviderTemplateRules.ComputeContentSha256(
                    template)
        };

    var result =
        ProviderTemplateRenderer.Render(
            snapshot,
            new ProviderRenderContext
            {
                Provider = "Test",
                Date = "2026-08-26",
                Filename = "main.png",
                AssetName = "asset1",
                Project = "project1",
                Role = "Final production asset",
                Workflow = "no-reference",
                ReferenceFilename = "not recorded",
                Prompt =
                    "Keep literal <<<DATE>>> and <<<PROVIDER>>>."
            });

    Assert.Contains(
        "Date: 2026-08-26",
        result);

    Assert.Contains(
        "Keep literal <<<DATE>>> and <<<PROVIDER>>>.",
        result);
}
```

---

# 21. EXACT BUILT-IN `_TEMPLATE.md`

Create:

```text
src/AssetProvenanceHelper/provider_templates/_TEMPLATE.md
```

with:

```markdown
# AI ASSET PROVENANCE RECORD

Provider: <<<PROVIDER>>>

Asset file: <<<FILENAME>>>
Asset name: <<<ASSET_NAME>>>
Asset role: <<<ROLE>>>
Project: <<<PROJECT>>>

Helper record date: <<<DATE>>>
Recorded workflow: <<<WORKFLOW>>>
Reference asset: <<<REFERENCE_FILENAME>>>

## Prompt

<<<PROMPT>>>

## Provider / workflow notes

Replace this section with any provider-specific information that you want to retain.

The AI Asset Provenance Helper does not add extra provider fields.
It only replaces the predefined <<<...>>> fields in this file.
```

This file starts with `_`, so it must never appear in the dropdown.

---

# 22. EXACT BUILT-IN `ChatGPT.md`

Create:

```text
src/AssetProvenanceHelper/provider_templates/ChatGPT.md
```

with:

```markdown
# AI ASSET PROVENANCE RECORD

Provider: <<<PROVIDER>>>

Asset file: <<<FILENAME>>>
Asset name: <<<ASSET_NAME>>>
Asset role: <<<ROLE>>>
Project: <<<PROJECT>>>

Helper record date: <<<DATE>>>
Recorded workflow: <<<WORKFLOW>>>
Reference asset: <<<REFERENCE_FILENAME>>>

## Recorded prompt

<<<PROMPT>>>

## Generation/provider declaration

The selected provenance template identifies the generation provider as ChatGPT / OpenAI.

Generation conversation retained: not recorded
Third-party visual input: not recorded
Human review: not recorded
IP / trademark review: not recorded
Release approval: draft
Status: unapproved

## Important

This record identifies files handled by the AI Asset Provenance Helper.
It is not a provider-issued provenance certificate, legal advice, a license,
or a warranty of rights, uniqueness, copyright protection, or third-party clearance.
```

---

# 23. `TemplateService` — DO NOT REMOVE LEGACY API

Keep existing:

```csharp
RenderReference(...)
RenderFinal(...)
RenderFinalNoReference(...)
ValidateTemplates()
```

unchanged for legacy compatibility.

Add these two new methods.

```csharp
public string RenderReferenceForSession(
    AssetSession session,
    string referenceFilename,
    DateTimeOffset processedAt)
{
    ArgumentNullException.ThrowIfNull(session);

    var generationDate =
        processedAt.ToString(
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);

    if (session.ProviderTemplate is null)
    {
        return RenderReference(
            referenceFilename,
            session.ProjectName,
            generationDate);
    }

    return ProviderTemplateRenderer.Render(
        session.ProviderTemplate,
        new ProviderRenderContext
        {
            Provider =
                session.ProviderTemplate.DisplayName,

            Date =
                generationDate,

            Filename =
                referenceFilename,

            AssetName =
                session.AssetFolderName,

            Project =
                session.ProjectName,

            Role =
                AppConstants.ReferenceRoleLabel,

            Workflow =
                AppConstants.ReferenceAssistedWorkflowLabel,

            ReferenceFilename =
                referenceFilename,

            Prompt =
                AppConstants.NotRecordedValue
        });
}

public string RenderFinalForSession(
    AssetSession session,
    string mainFilename,
    string prompt,
    DateTimeOffset processedAt)
{
    ArgumentNullException.ThrowIfNull(session);

    var generationDate =
        processedAt.ToString(
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);

    if (session.ProviderTemplate is null)
    {
        return session.WorkflowMode switch
        {
            AssetWorkflowMode.ReferenceAssisted =>
                RenderFinal(
                    mainFilename,
                    session.ReferenceFilename,
                    session.ProjectName,
                    generationDate,
                    prompt),

            AssetWorkflowMode.NoReference =>
                RenderFinalNoReference(
                    mainFilename,
                    session.ProjectName,
                    generationDate,
                    prompt),

            _ =>
                throw new InvalidDataException(
                    $"Unsupported workflow mode: {session.WorkflowMode}")
        };
    }

    var workflow =
        session.WorkflowMode switch
        {
            AssetWorkflowMode.ReferenceAssisted =>
                AppConstants.ReferenceAssistedWorkflowLabel,

            AssetWorkflowMode.NoReference =>
                AppConstants.NoReferenceWorkflowLabel,

            _ =>
                throw new InvalidDataException(
                    $"Unsupported workflow mode: {session.WorkflowMode}")
        };

    var referenceFilename =
        session.WorkflowMode
        == AssetWorkflowMode.ReferenceAssisted
            ? session.ReferenceFilename
            : AppConstants.NotRecordedValue;

    return ProviderTemplateRenderer.Render(
        session.ProviderTemplate,
        new ProviderRenderContext
        {
            Provider =
                session.ProviderTemplate.DisplayName,

            Date =
                generationDate,

            Filename =
                mainFilename,

            AssetName =
                session.AssetFolderName,

            Project =
                session.ProjectName,

            Role =
                AppConstants.FinalRoleLabel,

            Workflow =
                workflow,

            ReferenceFilename =
                referenceFilename,

            Prompt =
                prompt
        });
}
```

---

# 24. `AssetSession` — EXACT ADDITIONS

Keep:

```csharp
public int SchemaVersion { get; set; } = 2;
```

Do **not** change the default.

Add after `WorkflowMode`:

```csharp
public ProviderTemplateSnapshot? ProviderTemplate { get; set; }

public string? SourceRequestKey { get; set; }
```

No other Provider fields are needed in `AssetSession`.

---

# 25. SESSION VERSION RULES

## Legacy session

```text
SchemaVersion <= 2
ProviderTemplate == null
```

uses old templates.

## New v1.3 Provider session

```text
SchemaVersion = 3
ProviderTemplate != null
```

uses Provider snapshot.

## Invalid

```text
SchemaVersion >= 3
ProviderTemplate == null
```

is invalid.

---

# 26. VALIDATION OF NEW SESSION METADATA

Add this helper to `ValidationService.Session.cs`:

```csharp
private static void ValidateUpgradeV13Metadata(
    AssetSession session,
    ICollection<string> errors)
{
    if (session.SchemaVersion >= 3
        && session.ProviderTemplate is null)
    {
        errors.Add(
            "SchemaVersion 3+ session is missing ProviderTemplate.");
    }

    if (session.ProviderTemplate is not null)
    {
        var providerValidation =
            ProviderTemplateRules.ValidateSnapshot(
                session.ProviderTemplate);

        if (!providerValidation.IsValid)
        {
            foreach (var error in providerValidation.Errors)
            {
                errors.Add(
                    "ProviderTemplate: " + error);
            }
        }
    }

    if (!string.IsNullOrWhiteSpace(
            session.SourceRequestKey)
        && !IsSha256Hex(
            session.SourceRequestKey))
    {
        errors.Add(
            "SourceRequestKey must be a 64-character SHA-256 hexadecimal value.");
    }
}
```

Call it from:

```text
ValidateSessionCommon(...)
ValidatePreparedReferenceSessionCore(...)
```

before returning.

It must also be applied to both Old/New sessions during replacement-journal validation.

---

# 27. CRITICAL VALIDATION CHANGE FOR ARBITRARY PROVIDER MARKDOWN

## Legacy path

If:

```csharp
session.ProviderTemplate is null
```

keep existing hard-coded provenance content checks unchanged.

## Provider path

If:

```csharp
session.ProviderTemplate is not null
```

do **not** search for:

```text
Asset ID:
Project:
Generation date:
```

because the custom Provider template may use any wording.

Instead validate:

1. file exists;
2. stored provenance hash exists;
3. actual raw file hash equals stored provenance hash;
4. exact rendered content equals content expected from the session snapshot.

---

# 28. ADD GENERIC PROVIDER EXACT-OUTPUT HELPER

Add to `ValidationService.Session.cs`:

```csharp
private ValidationResult TryGetExactProviderProvenanceRawHash(
    string provenancePath,
    string expectedText,
    string? expectedStoredHash,
    string description,
    out string? verifiedRawHash)
{
    verifiedRawHash = null;

    if (!File.Exists(provenancePath))
    {
        return ValidationResult.Failure(
            $"{description} file does not exist: {provenancePath}");
    }

    if (!IsSha256Hex(expectedStoredHash))
    {
        return ValidationResult.Failure(
            $"{description} stored SHA-256 authority is missing or invalid.");
    }

    byte[] rawBytes;

    try
    {
        rawBytes =
            File.ReadAllBytes(
                provenancePath);
    }
    catch (Exception ex)
    {
        return ValidationResult.Failure(
            $"Could not read {description}: {ex.Message}");
    }

    string actualText;

    try
    {
        using var reader =
            new StreamReader(
                new MemoryStream(rawBytes),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);

        actualText =
            reader.ReadToEnd();
    }
    catch (Exception ex)
    {
        return ValidationResult.Failure(
            $"Could not decode {description}: {ex.Message}");
    }

    if (!string.Equals(
            actualText,
            expectedText,
            StringComparison.Ordinal))
    {
        return ValidationResult.Failure(
            $"{description} content does not exactly match the Provider template snapshot and session values.");
    }

    var actualRawHash =
        Convert
            .ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    rawBytes))
            .ToLowerInvariant();

    if (!string.Equals(
            actualRawHash,
            expectedStoredHash,
            StringComparison.OrdinalIgnoreCase))
    {
        return ValidationResult.Failure(
            $"{description} SHA-256 hash does not match stored session authority.");
    }

    verifiedRawHash =
        actualRawHash;

    return ValidationResult.Success();
}
```

---

# 29. MODIFY `TryGetExactReferenceProvenanceRawHash`

At the beginning, after existence validation and before legacy hash shortcut:

```csharp
if (session.ProviderTemplate is not null)
{
    string expectedText;

    try
    {
        expectedText =
            templateService.RenderReferenceForSession(
                session,
                session.ReferenceFilename,
                session.ReferenceProcessedAt);
    }
    catch (Exception ex)
    {
        return ValidationResult.Failure(
            $"Could not render expected Provider Reference provenance: {ex.Message}");
    }

    return TryGetExactProviderProvenanceRawHash(
        provenancePath,
        expectedText,
        session.ReferenceProvenanceHash,
        "Reference provenance",
        out verifiedRawHash);
}
```

Then leave the existing legacy branch below it.

---

# 30. MODIFY `TryGetExactFinalProvenanceRawHash`

Before the existing legacy stored-hash/legacy-rendering path:

```csharp
if (session.ProviderTemplate is not null)
{
    if (string.IsNullOrWhiteSpace(
            session.MainFilename)
        || !session.MainProcessedAt.HasValue)
    {
        return ValidationResult.Failure(
            "Provider Main provenance authority is incomplete.");
    }

    string expectedText;

    try
    {
        expectedText =
            templateService.RenderFinalForSession(
                session,
                session.MainFilename,
                session.MainPrompt ?? string.Empty,
                session.MainProcessedAt.Value);
    }
    catch (Exception ex)
    {
        return ValidationResult.Failure(
            $"Could not render expected Provider Final provenance: {ex.Message}");
    }

    return TryGetExactProviderProvenanceRawHash(
        finalProvenancePath,
        expectedText,
        session.MainProvenanceHash,
        "Final provenance",
        out verifiedRawHash);
}
```

Leave the current legacy fallback intact afterward.

---

# 31. MODIFY `ValidateReferenceProvenanceContent`

At the beginning after file existence:

```csharp
if (session.ProviderTemplate is not null)
{
    if (!IsSha256Hex(
            session.ReferenceProvenanceHash))
    {
        return ValidationResult.Failure(
            "Provider Reference provenance hash authority is missing.");
    }

    try
    {
        var actualHash =
            ComputeSha256(
                provenancePath);

        return string.Equals(
                actualHash,
                session.ReferenceProvenanceHash,
                StringComparison.OrdinalIgnoreCase)
            ? ValidationResult.Success()
            : ValidationResult.Failure(
                "Provider Reference provenance hash does not match session authority.");
    }
    catch (Exception ex)
    {
        return ValidationResult.Failure(
            $"Could not hash Provider Reference provenance: {ex.Message}");
    }
}
```

Then existing legacy text-search logic remains.

---

# 32. MODIFY `ValidateFinalProvenanceContent`

Same principle:

```csharp
if (session.ProviderTemplate is not null)
{
    if (!IsSha256Hex(
            session.MainProvenanceHash))
    {
        return ValidationResult.Failure(
            "Provider Final provenance hash authority is missing.");
    }

    try
    {
        var actualHash =
            ComputeSha256(
                finalProvenancePath);

        return string.Equals(
                actualHash,
                session.MainProvenanceHash,
                StringComparison.OrdinalIgnoreCase)
            ? ValidationResult.Success()
            : ValidationResult.Failure(
                "Provider Final provenance hash does not match session authority.");
    }
    catch (Exception ex)
    {
        return ValidationResult.Failure(
            $"Could not hash Provider Final provenance: {ex.Message}");
    }
}
```

Legacy path remains unchanged.

---

# 33. `CreateReferenceSession` SIGNATURE

Change only by appending optional arguments:

```csharp
public AssetSession CreateReferenceSession(
    AppSettings settings,
    string assetFolderName,
    string sourceImagePath,
    DateTimeOffset processedAt,
    ProviderTemplateSnapshot? providerTemplate = null,
    string? sourceRequestKey = null)
```

Existing tests/callers therefore still compile.

---

# 34. `CreateReferenceSession` CREATION ORDER

Replace the old immediate provenance-render-and-return literal with:

1. derive all existing paths/hashes;
2. construct `AssetSession`;
3. attach Provider snapshot;
4. render provenance from that session;
5. calculate provenance hash;
6. return session.

Essential pattern:

```csharp
var session =
    new AssetSession
    {
        SchemaVersion =
            providerTemplate is null
                ? 2
                : 3,

        WorkflowMode =
            AssetWorkflowMode.ReferenceAssisted,

        ProviderTemplate =
            providerTemplate?.Clone(),

        SourceRequestKey =
            sourceRequestKey,

        ReferenceCommitPhase =
            ReferenceCommitPhase.Prepared,

        ReferenceTransactionId =
            Guid.NewGuid().ToString("N"),

        ProjectName =
            projectLabel,

        AssetRootFolder =
            settings.AssetRootFolder,

        AssetFolderName =
            assetFolderName,

        AssetFolder =
            assetFolder,

        ReferenceSourcePath =
            sourceImagePath,

        ReferenceDestinationPath =
            referenceDestination,

        ReferenceFilename =
            referenceFilename,

        ReferenceProvenancePath =
            referenceProvenance,

        ReferenceHash =
            sourceHash,

        ReferenceProcessedAt =
            processedAt,

        WasAssetFolderCreatedByTool =
            !assetFolderExisted,

        WasReferenceFolderCreatedByTool =
            !referenceFolderExisted
    };

var provenance =
    _templateService.RenderReferenceForSession(
        session,
        referenceFilename,
        processedAt);

session.ReferenceProvenanceHash =
    Convert
        .ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                new UTF8Encoding(false)
                    .GetBytes(provenance)))
        .ToLowerInvariant();

return session;
```

---

# 35. MODIFY `RequirePreparedReferenceAuthority`

Replace:

```csharp
_templateService.RenderReference(...)
```

with:

```csharp
_templateService.RenderReferenceForSession(
    session,
    session.ReferenceFilename,
    session.ReferenceProcessedAt)
```

No other transaction logic changes.

---

# 36. `CreateNoReferenceMainSession` SIGNATURE

Append:

```csharp
ProviderTemplateSnapshot? providerTemplate = null,
string? sourceRequestKey = null
```

so final signature becomes:

```csharp
public AssetSession CreateNoReferenceMainSession(
    AppSettings settings,
    string assetName,
    string sourceImagePath,
    string prompt,
    DateTimeOffset processedAt,
    ProviderTemplateSnapshot? providerTemplate = null,
    string? sourceRequestKey = null)
```

---

# 37. NO-REFERENCE SESSION CREATION ORDER

Construct the session first with:

```csharp
SchemaVersion =
    providerTemplate is null
        ? 2
        : 3,

ProviderTemplate =
    providerTemplate?.Clone(),

SourceRequestKey =
    sourceRequestKey,
```

and all current fields.

Then:

```csharp
var provenance =
    _templateService.RenderFinalForSession(
        session,
        mainFilename,
        prompt,
        processedAt);
```

and set:

```csharp
session.MainProvenanceHash = ...
```

Do not change Main transaction/path logic.

---

# 38. `PrepareMainCommit` CHANGE

Replace the current old-template switch with:

```csharp
var provText =
    _templateService.RenderFinalForSession(
        session,
        mainFilename,
        prompt,
        processedAt);
```

Everything else in `PrepareMainCommit` remains as-is.

---

# 39. `ProcessMainImage` CHANGE

Replace the current:

```csharp
provenance = session.WorkflowMode switch
{
    ...
};
```

with:

```csharp
provenance =
    _templateService.RenderFinalForSession(
        session,
        mainFilename,
        prompt,
        processedAt);
```

Do not alter staging/promotion/hash/rollback code around it.

---

# 40. REFERENCE REPLACEMENT — REQUIRED PROVIDER PRESERVATION

In:

```text
CreateReferenceReplacementTransaction(...)
```

both manually-created session literals must receive the new metadata.

For `oldSessionAuthority` add:

```csharp
SchemaVersion =
    oldSession.SchemaVersion,

ProviderTemplate =
    oldSession.ProviderTemplate?.Clone(),

SourceRequestKey =
    oldSession.SourceRequestKey,
```

For `newSession` add:

```csharp
SchemaVersion =
    oldSession.SchemaVersion,

ProviderTemplate =
    oldSession.ProviderTemplate?.Clone(),

SourceRequestKey =
    oldSession.SourceRequestKey,
```

Do not use the currently selected dropdown Provider.

Reference Replacement remains part of the same original asset/session.

---

# 41. REFERENCE REPLACEMENT RENDERING

Replace both replacement Reference renders with:

```csharp
_templateService.RenderReferenceForSession(
    newSession,
    newSession.ReferenceFilename,
    newSession.ReferenceProcessedAt)
```

or, when `transaction.NewSession` already exists:

```csharp
_templateService.RenderReferenceForSession(
    transaction.NewSession,
    transaction.NewSession.ReferenceFilename,
    transaction.NewSession.ReferenceProcessedAt)
```

---

# 42. REFERENCE REPLACEMENT VALIDATION

During:

```text
ValidateReferenceReplacementTransaction
ValidateReferenceReplacementJournal
```

Old and New sessions must agree on Provider/request authority.

Add comparison:

```csharp
private static bool ProviderSnapshotsEquivalent(
    ProviderTemplateSnapshot? left,
    ProviderTemplateSnapshot? right)
{
    if (left is null && right is null)
    {
        return true;
    }

    if (left is null || right is null)
    {
        return false;
    }

    return string.Equals(
               left.FileName,
               right.FileName,
               StringComparison.OrdinalIgnoreCase)
        && string.Equals(
               left.DisplayName,
               right.DisplayName,
               StringComparison.Ordinal)
        && string.Equals(
               left.ContentSha256,
               right.ContentSha256,
               StringComparison.OrdinalIgnoreCase)
        && string.Equals(
               left.Content,
               right.Content,
               StringComparison.Ordinal);
}
```

Then:

```csharp
if (!ProviderSnapshotsEquivalent(
        oldSession.ProviderTemplate,
        newSession.ProviderTemplate))
{
    errors.Add(
        "Old/New ProviderTemplate snapshots do not match.");
}

if (!string.Equals(
        oldSession.SourceRequestKey,
        newSession.SourceRequestKey,
        StringComparison.Ordinal))
{
    errors.Add(
        "Old/New SourceRequestKey values do not match.");
}
```

Also run `ValidateUpgradeV13Metadata()` for both sessions.

---

# 43. SETTINGS MODEL

Extend `AppSettings` with exactly:

```csharp
public string SelectedProviderTemplateFileName { get; set; }
    = AppConstants.DefaultProviderTemplateFileName;

public bool DirectModeEnabled { get; set; }
    = false;
```

Nothing else.

Do not put Request Manifest path in settings.

Do not auto-reopen a Request Manifest at startup.

---

# 44. SETTINGS NORMALIZATION

In `SettingsService.Normalize()`:

```csharp
settings.SelectedProviderTemplateFileName ??=
    AppConstants.DefaultProviderTemplateFileName;

if (string.IsNullOrWhiteSpace(
        settings.SelectedProviderTemplateFileName)
    || !string.Equals(
        Path.GetFileName(
            settings.SelectedProviderTemplateFileName),
        settings.SelectedProviderTemplateFileName,
        StringComparison.Ordinal)
    || !string.Equals(
        Path.GetExtension(
            settings.SelectedProviderTemplateFileName),
        ".md",
        StringComparison.OrdinalIgnoreCase))
{
    settings.SelectedProviderTemplateFileName =
        AppConstants.DefaultProviderTemplateFileName;
}
```

`DirectModeEnabled` needs no normalization.

Old JSON loads with defaults.

---

# 45. PROVIDER DROPDOWN GUI

Add to the existing Settings group as a third row.

Controls:

```csharp
private ComboBox cmbProvider = null!;
private Label lblProviderWarning = null!;
```

Use:

```csharp
cmbProvider =
    new ComboBox
    {
        Name =
            "cmbProvider",

        DropDownStyle =
            ComboBoxStyle.DropDownList,

        Dock =
            DockStyle.Fill
    };
```

Add Provider row:

```text
AI Generation Provider    [ ChatGPT ▼ ]    warning
```

`lblProviderWarning`:

- invisible if zero Provider errors;
- orange if errors;
- text:

```text
1 template ignored
```

or:

```text
3 templates ignored
```

Tooltip contains full errors.

---

# 46. PROVIDER FALLBACK ORDER

At application startup:

1. Load valid templates.
2. Try `settings.SelectedProviderTemplateFileName`.
3. Else try `ChatGPT.md`.
4. Else choose first alphabetically.
5. Else no Provider selected.

If fallback happens, update `_settings.SelectedProviderTemplateFileName` in memory.

Normal FormClosing `SaveSettingsSafe()` persists it.

---

# 47. NO VALID PROVIDERS

If no valid Provider template exists:

### Idle / new asset

Disable:

```text
Reference CTA
Main Image CTA
```

for starting new assets.

Show warning next to Provider dropdown.

### Recovered active legacy session

Do **not** block Main completion.

### Recovered active v1.3 Provider session

Do **not** block Main completion because Provider snapshot is in `session.json`.

---

# 48. MAINTAIN MAINFORM CONSTRUCTOR COMPATIBILITY

Change signature to:

```csharp
public MainForm(
    AppSettings settings,
    SettingsService settingsService,
    ImageFinderService imageFinderService,
    TemplateService templateService,
    ValidationService validationService,
    AssetProcessorService assetProcessorService,
    SessionService sessionService,
    ProviderTemplateCatalogService? providerTemplateCatalogService = null,
    RecentDocumentHistoryService? recentDocumentHistoryService = null,
    RequestProgressService? requestProgressService = null)
```

Assign nullable fields.

Existing tests continue to compile.

Production `Program.cs` supplies all three.

---

# 49. COMPATIBILITY BEHAVIOR WHEN OPTIONAL PROVIDER SERVICE IS NULL

Add:

```csharp
private bool CanStartNewAssetWithProvider =>
    _providerTemplateCatalogService is null
    || _selectedProvider is not null;
```

When catalog service is null:

- do not require Provider selection;
- new sessions created by old tests pass `null` snapshot;
- resulting test sessions remain schema 2/legacy.

This keeps existing UI tests stable while production always uses the Provider catalog.

---

# 50. GETTING A SNAPSHOT FOR A NEW PRODUCTION ASSET

Add:

```csharp
private ProviderTemplateSnapshot? GetProviderSnapshotForNewAsset()
{
    if (_providerTemplateCatalogService is null)
    {
        return null;
    }

    return _selectedProvider?.CreateSnapshot();
}
```

Do not read the Provider file again when the user clicks Reference/Main.

Use the Provider content loaded at application startup.

This matches the requirement:

> Provider templates appear automatically at program start.

---

# 51. RECOVERED SESSION PROVIDER DISPLAY

If a recovered session has a Provider snapshot:

1. search current dropdown templates for same:
   - filename;
   - `ContentSha256`.
2. if found, select it;
3. if not found:
   - temporarily add `ProviderTemplateDefinition.FromSnapshot(...)`;
   - select it;
   - provider dropdown remains disabled while session is active.

Therefore the UI does not misleadingly show ChatGPT when the active recovered session is actually a deleted/changed Gemini snapshot.

After asset completion/cancel:

```text
reload normal catalog
remove temporary snapshot entry
restore normal provider availability
```

---

# 52. PROVIDER DROPDOWN LOCKING

When:

```text
_state == ReferenceReady
```

set:

```csharp
cmbProvider.Enabled = false;
```

Otherwise:

```csharp
cmbProvider.Enabled =
    _providerCatalog?.HasUsableTemplates == true;
```

No Provider switch is allowed between Reference and Main.

---

# 53. REQUEST MANIFEST — FINAL FORMAT

The previous custom Markdown grammar is discarded.

Use JSON.

File example:

```text
asset_request_manifest.json
```

Exact structure:

```json
{
  "manifestVersion": 1,
  "assets": [
    {
      "filename": "asset_ui_screen_settings.webp",
      "resolution": "1920x1080",
      "prompt": "Complete exact generation prompt here."
    },
    {
      "filename": "enemy_armored.png",
      "resolution": "512x512",
      "prompt": "Another complete exact generation prompt."
    }
  ]
}
```

These are the only allowed fields.

---

# 54. MANIFEST RULES

Top-level:

```text
manifestVersion
assets
```

Asset:

```text
filename
resolution
prompt
```

No unknown properties are permitted.

No comments.

No trailing commas.

No Markdown code fences.

Encoding:

```text
UTF-8
```

Maximum file size:

```text
32 MiB
```

Maximum assets:

```text
5000
```

Maximum decoded Prompt:

```text
1,000,000 characters per asset
```

---

# 55. EXACT RESOLUTION FORMAT

Accepted input:

```text
1920x1080
1920×1080
1920 x 1080
1920 × 1080
```

Stored normalized form:

```text
1920x1080
```

Both dimensions must be:

```text
1..100000
```

Resolution is Request metadata only in v1.3.

The downloaded image is **not** blocked based on dimension mismatch in this release.

---

# 56. REQUEST FILENAME RULES

Valid:

```text
asset_ui_screen_settings.webp
enemy_armored.png
asset.version2.webp
```

Invalid:

```text
assets/ui/foo.webp
C:\images\foo.webp
..\foo.webp
foo
foo.txt
```

Allowed extensions come from the application's accepted image extensions.

Derived Asset Name:

```csharp
Path.GetFileNameWithoutExtension(filename)
```

Example:

```text
asset.version2.webp
→ asset.version2
```

The derived Asset Name must also pass existing:

```text
ValidateAssetName(...)
```

rules.

---

# 57. REQUEST FILENAME DOES NOT HAVE TO MATCH BROWSER DOWNLOAD NAME

Example Request:

```text
filename = asset_ui_screen_settings.webp
```

Browser may download:

```text
ChatGPT Image Aug 26 2026.png
```

That is allowed.

The Request filename is used to derive:

```text
Asset Name = asset_ui_screen_settings
```

The existing Main processor continues using the actual downloaded Main filename for the root copy and actual extension for the `ingame` copy.

---

# 58. NEW FILE — `Models/AssetRequestItem.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public sealed class AssetRequestItem
{
    public required string FileName { get; init; }

    public required string AssetName { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required string Resolution { get; init; }

    public required string Prompt { get; init; }

    public required string RequestKey { get; init; }

    public bool IsCompleted { get; set; }
}
```

---

# 59. NEW FILE — `Models/AssetRequestManifest.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public sealed class AssetRequestManifest
{
    public int Version { get; init; }

    public required string SourcePath { get; init; }

    public required string ManifestFingerprint { get; init; }

    public required IReadOnlyList<AssetRequestItem> Items { get; init; }
}
```

---

# 60. NEW FILE — `Services/AssetRequestManifestService.cs`

Use `System.Text.Json`.

Suggested complete implementation:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class AssetRequestManifestService
{
    private const long MaxManifestBytes =
        32L * 1024L * 1024L;

    private const int MaxAssets =
        5000;

    private const int MaxPromptCharacters =
        1_000_000;

    private static readonly Regex ResolutionRegex =
        new(
            @"^\s*(?<w>[0-9]+)\s*[x×]\s*(?<h>[0-9]+)\s*$",
            RegexOptions.Compiled
            | RegexOptions.CultureInvariant);

    private readonly ValidationService _validationService;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            PropertyNameCaseInsensitive =
                false,

            AllowTrailingCommas =
                false,

            ReadCommentHandling =
                JsonCommentHandling.Disallow,

            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow
        };

    public AssetRequestManifestService(
        ValidationService validationService)
    {
        _validationService =
            validationService;
    }

    public AssetRequestManifest Load(
        string path,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        ArgumentNullException.ThrowIfNull(
            acceptedExtensions);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                "Request Manifest path is empty.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Request Manifest does not exist.",
                path);
        }

        var info =
            new FileInfo(path);

        if (info.Length <= 0)
        {
            throw new InvalidDataException(
                "Request Manifest is empty.");
        }

        if (info.Length > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"Request Manifest exceeds the {MaxManifestBytes} byte limit.");
        }

        ManifestDto? dto;

        try
        {
            var json =
                File.ReadAllText(
                    path,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true));

            dto =
                JsonSerializer.Deserialize<ManifestDto>(
                    json,
                    _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Request Manifest JSON is invalid: {ex.Message}",
                ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException(
                "Request Manifest could not be deserialized.");
        }

        if (dto.ManifestVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported manifestVersion {dto.ManifestVersion}. Expected 1.");
        }

        if (dto.Assets is null
            || dto.Assets.Count == 0)
        {
            throw new InvalidDataException(
                "Request Manifest contains no assets.");
        }

        if (dto.Assets.Count > MaxAssets)
        {
            throw new InvalidDataException(
                $"Request Manifest contains more than {MaxAssets} assets.");
        }

        var items =
            new List<AssetRequestItem>();

        var filenames =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        for (var index = 0;
             index < dto.Assets.Count;
             index++)
        {
            var raw =
                dto.Assets[index];

            var itemNumber =
                index + 1;

            try
            {
                var fileName =
                    ValidateFilename(
                        raw.Filename,
                        acceptedExtensions);

                if (!filenames.Add(fileName))
                {
                    throw new InvalidDataException(
                        $"Duplicate filename '{fileName}'.");
                }

                var (width, height, normalizedResolution) =
                    ParseResolution(
                        raw.Resolution);

                if (raw.Prompt is null
                    || string.IsNullOrWhiteSpace(
                        raw.Prompt))
                {
                    throw new InvalidDataException(
                        "prompt is missing or blank.");
                }

                if (raw.Prompt.Length
                    > MaxPromptCharacters)
                {
                    throw new InvalidDataException(
                        $"prompt exceeds {MaxPromptCharacters} characters.");
                }

                var assetName =
                    Path.GetFileNameWithoutExtension(
                        fileName);

                var assetValidation =
                    _validationService.ValidateAssetName(
                        assetName,
                        acceptedExtensions);

                if (!assetValidation.IsValid)
                {
                    throw new InvalidDataException(
                        string.Join(
                            "; ",
                            assetValidation.Errors));
                }

                var requestKey =
                    ComputeRequestKey(
                        fileName,
                        normalizedResolution,
                        raw.Prompt);

                items.Add(
                    new AssetRequestItem
                    {
                        FileName =
                            fileName,

                        AssetName =
                            assetName,

                        Width =
                            width,

                        Height =
                            height,

                        Resolution =
                            normalizedResolution,

                        Prompt =
                            raw.Prompt,

                        RequestKey =
                            requestKey
                    });
            }
            catch (Exception ex)
                when (ex is InvalidDataException
                      or ArgumentException)
            {
                throw new InvalidDataException(
                    $"Asset #{itemNumber}: {ex.Message}",
                    ex);
            }
        }

        var manifestFingerprint =
            ComputeManifestFingerprint(
                items);

        return new AssetRequestManifest
        {
            Version =
                1,

            SourcePath =
                Path.GetFullPath(path),

            ManifestFingerprint =
                manifestFingerprint,

            Items =
                items
        };
    }

    private static string ValidateFilename(
        string? value,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                "filename is missing or blank.");
        }

        if (!string.Equals(
                Path.GetFileName(value),
                value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "filename must contain only a leaf filename, not a path.");
        }

        if (value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "filename contains control characters.");
        }

        var extension =
            Path.GetExtension(value);

        if (string.IsNullOrWhiteSpace(extension)
            || !acceptedExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"filename uses unsupported image extension '{extension}'.");
        }

        return value;
    }

    private static (
        int Width,
        int Height,
        string Normalized)
        ParseResolution(
            string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                "resolution is missing or blank.");
        }

        var match =
            ResolutionRegex.Match(value);

        if (!match.Success)
        {
            throw new InvalidDataException(
                $"resolution '{value}' is invalid.");
        }

        if (!int.TryParse(
                match.Groups["w"].Value,
                out var width)
            || !int.TryParse(
                match.Groups["h"].Value,
                out var height))
        {
            throw new InvalidDataException(
                $"resolution '{value}' contains invalid numbers.");
        }

        if (width < 1
            || width > 100_000
            || height < 1
            || height > 100_000)
        {
            throw new InvalidDataException(
                "resolution dimensions must each be between 1 and 100000.");
        }

        return (
            width,
            height,
            $"{width}x{height}");
    }

    internal static string ComputeRequestKey(
        string fileName,
        string normalizedResolution,
        string prompt)
    {
        var normalizedPrompt =
            NormalizeLineEndings(prompt);

        var material =
            fileName.ToLowerInvariant()
            + "\n"
            + normalizedResolution
            + "\n"
            + normalizedPrompt;

        return ComputeSha256(
            material);
    }

    internal static string ComputeManifestFingerprint(
        IEnumerable<AssetRequestItem> items)
    {
        var keys =
            items
                .Select(item => item.RequestKey)
                .OrderBy(
                    key => key,
                    StringComparer.Ordinal)
                .ToArray();

        var material =
            "manifestVersion=1\n"
            + string.Join(
                "\n",
                keys);

        return ComputeSha256(
            material);
    }

    private static string NormalizeLineEndings(
        string value)
    {
        return value
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
            .Replace(
                '\r',
                '\n');
    }

    private static string ComputeSha256(
        string value)
    {
        var bytes =
            Encoding.UTF8.GetBytes(value);

        return Convert
            .ToHexString(
                SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed class ManifestDto
    {
        [JsonPropertyName("manifestVersion")]
        public int ManifestVersion { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetDto>? Assets { get; set; }
    }

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed class AssetDto
    {
        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("resolution")]
        public string? Resolution { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }
    }
}
```


---

# 61. MANIFEST IMPORT IS ATOMIC

Implementation order:

```text
choose file
↓
parse entire file into local variable
↓
validate every item
↓
compute fingerprint
↓
ONLY THEN replace current queue
```

Never:

```text
read item
→ add to ListView
→ read next item
```

because an error halfway through would leave partial state.

---

# 62. EXACT SHIPPED MANIFEST TEMPLATE

Create:

```text
src/AssetProvenanceHelper/examples/asset_request_manifest_template.json
```

Content:

```json
{
  "manifestVersion": 1,
  "assets": [
    {
      "filename": "asset_ui_screen_settings.webp",
      "resolution": "1920x1080",
      "prompt": "Place the complete exact generation prompt here."
    },
    {
      "filename": "enemy_armored.png",
      "resolution": "512x512",
      "prompt": "Place the complete exact generation prompt here."
    }
  ]
}
```

---

# 63. EXACT SHIPPED THIRD-PARTY-AI CONVERSION PROMPT

Create:

```text
src/AssetProvenanceHelper/examples/asset_request_conversion_prompt.txt
```

Content:

```text
Convert the asset-request document I provide into the exact JSON structure
required by AI Asset Provenance Helper.

OUTPUT RULES

1. Output ONLY valid JSON.
2. Do not wrap the JSON in Markdown code fences.
3. Use exactly this top-level structure:
   {
     "manifestVersion": 1,
     "assets": [...]
   }

4. Every requested asset must become exactly one object in "assets".

5. Every asset object must contain exactly these three properties:
   "filename"
   "resolution"
   "prompt"

6. Do not add any other properties.

7. "filename":
   - Use only the final leaf filename.
   - Never include a folder path.
   - Example source:
     assets/ui118/backdrops/loc_gas_station/day.webp
   - Required output:
     day.webp

8. "resolution":
   - Normalize to WIDTHxHEIGHT.
   - Example:
     1920x1080

9. "prompt":
   - Copy the complete generation prompt.
   - Do not summarize it.
   - Do not improve it.
   - Do not rewrite it.
   - Do not translate it.
   - Do not shorten it.
   - Do not omit any line or technical specification.
   - Preserve the prompt's content exactly, using valid JSON escaping.

10. Do not omit any requested asset.

11. Do not invent information that is missing.

12. The order of assets should remain the same as in the source document.

EXACT FORMAT EXAMPLE

{
  "manifestVersion": 1,
  "assets": [
    {
      "filename": "example.webp",
      "resolution": "1920x1080",
      "prompt": "Exact complete prompt here."
    }
  ]
}

Now convert the supplied asset-request document.
```

---

# 64. REQUEST PROGRESS PERSISTENCE

State file:

```text
%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper\request-progress.json
```

Do not place it in publish/install directory.

---

# 65. NEW FILE — `Models/RequestProgressState.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public sealed class RequestProgressState
{
    public int SchemaVersion { get; set; } =
        1;

    public string ManifestFingerprint { get; set; } =
        string.Empty;

    public List<string> CompletedRequestKeys { get; set; } =
        new();
}
```

---

# 66. NEW FILE — `Services/RequestProgressService.cs`

Use atomic save.

```csharp
using System.Text;
using System.Text.Json;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class RequestProgressService
{
    private readonly string _path;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = true
        };

    public RequestProgressService(
        string path)
    {
        _path =
            path;
    }

    public HashSet<string> LoadForManifest(
        string manifestFingerprint)
    {
        if (!File.Exists(_path))
        {
            return new HashSet<string>(
                StringComparer.Ordinal);
        }

        var json =
            File.ReadAllText(
                _path,
                Encoding.UTF8);

        var state =
            JsonSerializer.Deserialize<RequestProgressState>(
                json,
                _jsonOptions)
            ?? throw new InvalidDataException(
                "request-progress.json could not be deserialized.");

        if (!string.Equals(
                state.ManifestFingerprint,
                manifestFingerprint,
                StringComparison.Ordinal))
        {
            return new HashSet<string>(
                StringComparer.Ordinal);
        }

        return new HashSet<string>(
            state.CompletedRequestKeys
                .Where(
                    key =>
                        !string.IsNullOrWhiteSpace(key)),
            StringComparer.Ordinal);
    }

    public void Save(
        string manifestFingerprint,
        IEnumerable<string> completedKeys)
    {
        var state =
            new RequestProgressState
            {
                ManifestFingerprint =
                    manifestFingerprint,

                CompletedRequestKeys =
                    completedKeys
                        .Distinct(
                            StringComparer.Ordinal)
                        .OrderBy(
                            key => key,
                            StringComparer.Ordinal)
                        .ToList()
            };

        var directory =
            Path.GetDirectoryName(_path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        var json =
            JsonSerializer.Serialize(
                state,
                _jsonOptions);

        var tempPath =
            _path
            + "."
            + Guid.NewGuid().ToString("N")
            + ".tmp";

        try
        {
            using (
                var stream =
                    new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
            using (
                var writer =
                    new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(
                tempPath,
                _path,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }
}
```

---

# 67. PROGRESS SEMANTICS

When manifest imported:

```text
fingerprint matches request-progress.json
→ restore matching completed keys

fingerprint differs
→ start with zero completed
```

Do not automatically modify/save progress merely because a different manifest was imported.

Save only after first actual completion.

---

# 68. DO NOT AUTO-LOAD THE LAST MANIFEST AT STARTUP

This is a deliberate simplicity/safety decision.

At startup:

```text
Request Queue = empty
```

User explicitly imports a manifest.

If the same manifest is re-imported:

```text
completed state is restored
```

from its semantic fingerprint.

---

# 69. RECENT DOCUMENT HISTORY

User requirement:

> max. last 3 erzeugten Dokumente.

Interpretation is exact:

- Reference provenance creation counts as one document.
- Reference provenance replacement counts as a newly generated document.
- Final provenance creation counts as one document.
- Ordinary status messages do not count.
- No more than three rows exist.
- Newest is first.

---

# 70. NEW FILE — `Models/RecentDocumentEntry.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public enum ProvenanceDocumentKind
{
    Reference = 0,
    Final = 1
}

public sealed class RecentDocumentEntry
{
    public string Path { get; set; } =
        string.Empty;

    public string AssetName { get; set; } =
        string.Empty;

    public ProvenanceDocumentKind Kind { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
```

---

# 71. NEW FILE — `Models/RecentDocumentHistoryState.cs`

```csharp
namespace AssetProvenanceHelper.Models;

public sealed class RecentDocumentHistoryState
{
    public int SchemaVersion { get; set; } =
        1;

    public List<RecentDocumentEntry> Entries { get; set; } =
        new();
}
```

---

# 72. RECENT HISTORY SERVICE RULES

`Record(entry)`:

```text
load current
↓
remove any existing same path, case-insensitive normalized
↓
insert new at index 0
↓
Take(3)
↓
atomic save
```

Reference Replacement therefore moves that Reference document to the top instead of creating a duplicate row.

---

# 73. CANCELLATION RULE

If a Reference session is cancelled and its generated Reference provenance is removed by the existing cancellation workflow:

```text
remove history entries under that asset folder
```

Do this **only after durable cancellation succeeds**.

Do not remove History merely because user opens cancellation dialog.

---

# 74. STATUS UI COMPATIBILITY

Keep existing:

```csharp
private TextBox txtStatusHistory;
```

for internal `AddStatus()` compatibility.

Make it hidden.

Add:

```csharp
private ListView lvRecentDocuments;
```

as the visible three-document UI.

This avoids changing dozens of existing `AddStatus()` calls.

---

# 75. STATUS GROUP EXACT LAYOUT

`BuildStatusGroup()`:

- `AutoSize = false`;
- `Height = 145`;
- `MinimumSize = new Size(0, 135)`.

Rows:

```text
0 → recent documents ListView, ~80px/fill
1 → hidden txtStatusHistory, absolute 0
2 → action buttons, AutoSize
```

`lvRecentDocuments`:

```text
View = Details
FullRowSelect = true
MultiSelect = false
HeaderStyle = Nonclickable
```

Columns:

```text
Time        75
Type        80
Asset       220
Document    remaining
```

Example:

```text
00:34:12   Final       asset_ui_screen_settings   license.txt — Final AI-Generated Asset.md
00:33:45   Reference   asset_ui_screen_settings   license.txt — AI Reference Asset.md
00:30:01   Final       asset_ui_screen_main       license.txt — Final AI-Generated Asset.md
```

Tooltip = complete path.

---

# 76. HISTORY RECORDING COMMIT POINTS

## Reference

Record only inside/after:

```text
CompleteReferenceUiAfterDurableCommit
```

using:

```text
session.ReferenceProvenancePath
session.ReferenceProcessedAt
```

## Reference Replacement

Record only after replacement journal is deleted and new Reference is durable.

## Final

Record only inside:

```text
CompleteMainUiAfterDurableCommit
```

using:

```text
Path.Combine(
    session.AssetFolder,
    AppConstants.FinalProvenanceFileName)

session.MainProcessedAt
```

---

# 77. POST-COMMIT BOOKKEEPING FAILURE RULE

If:

```text
asset successfully committed
```

but:

```text
recent-documents.json save fails
```

never roll back the asset.

Likewise Request progress failure never rolls back a completed asset.

Those files are UI/bookkeeping state, not transaction authority.

---

# 78. PROMPT PREVIEW — EXACT BEHAVIOR

Add above `txtPrompt`:

```text
Prompt Preview
[ first 100 characters... ]
```

Control:

```csharp
private Label lblPromptPreview = null!;
```

Empty prompt:

```text
No prompt stored.
```

in gray.

---

# 79. EXACT 100-CHARACTER RULE

Use .NET string length.

For prompt length:

```text
0     → No prompt stored.
1-100 → exact first N chars
101+  → first 100 chars + ...
```

For display only:

```text
\r
\n
\t
```

become spaces.

Do not collapse other spaces.

Do not change `txtPrompt.Text`.

---

# 80. COPY-READY PREVIEW HELPER

```csharp
internal static string BuildPromptPreview(
    string? prompt)
{
    if (string.IsNullOrEmpty(prompt))
    {
        return "No prompt stored.";
    }

    var wasTruncated =
        prompt.Length > 100;

    var slice =
        wasTruncated
            ? prompt[..100]
            : prompt;

    var display =
        slice
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

    return wasTruncated
        ? display + "..."
        : display;
}
```

---

# 81. PROMPT OVERLAY

Create:

```text
Ui/PromptPreviewOverlayControl.cs
```

It is a floating in-form control, not a message box.

Size:

```text
700 × 320
```

Minimum usable if form smaller:

```text
width = min(700, form client width - 40)
height = min(320, form client height - 40)
```

Contains:

```text
Full Prompt
read-only multiline TextBox
vertical + horizontal scroll as needed
Close button
```

The TextBox receives **exact original `txtPrompt.Text`**.

---

# 82. PROMPT OVERLAY SHOW/HIDE

`lblPromptPreview.MouseEnter`:

```text
show overlay immediately
```

Start a 100 ms timer.

Every tick:

```text
if cursor is over lblPromptPreview
OR cursor is over overlay rectangle
    keep open
else
    close
```

This avoids flickering while moving from preview label to overlay.

Escape closes it.

Updating Prompt while overlay open updates overlay text.

---

# 83. RIGHT-SIDE REQUEST QUEUE — OVERALL LAYOUT

Do not rebuild existing left-side UI.

Wrap it.

Current `pnlMainContent` remains left-hand content.

Add outer:

```csharp
private TableLayoutPanel pnlWorkspace = null!;
```

Columns:

```text
left  = Percent 100
right = Absolute 380px
```

New default Form size:

```text
1500 × 800
```

New minimum:

```text
1240 × 700
```

If desired, exact minimum may be 1280×700; choose **1240×700 for this plan** and do not improvise.

---

# 84. REQUEST QUEUE CONTROLS

Add:

```csharp
private GroupBox grpRequestQueue = null!;
private Button btnImportRequest = null!;
private Label lblRequestSource = null!;
private ListView lvRequestQueue = null!;
private Label lblRequestProgress = null!;
```

ListView:

```text
Status
Asset
Resolution
```

Do not display full Prompt in the list.

Prompt appears in the existing Final Prompt field after selection.

---

# 85. QUEUE ORDER

Preserve manifest order exactly.

Do not reorder:

- Done first;
- Pending first;
- alphabetically;
- by resolution.

Done rows stay in original list position.

---

# 86. QUEUE COLORS AND TEXT

Pending:

```text
Status = Pending
normal background
```

Done:

```text
Status = Done
light green background
```

Do not rely on green alone.

Active Pending Request:

```text
bold font
```

Do not give it another strong background color.

---

# 87. IMPORT BEHAVIOR

Button:

```text
Import Request...
```

Open filter:

```text
JSON files (*.json)|*.json|All files (*.*)|*.*
```

On successful import:

1. replace current manifest;
2. clear `_activeRequest`;
3. clear Asset Name;
4. clear Prompt;
5. clear Main candidate;
6. clear Reference candidate if there is no active Reference session;
7. load progress for fingerprint;
8. populate queue;
9. select no item automatically;
10. do not touch clipboard.

---

# 88. IMPORT FAILURE

If parsing fails:

- current queue remains untouched;
- current active Request remains untouched;
- Asset Name remains untouched;
- Prompt remains untouched;
- no progress state is modified.

Message example:

```text
Request Manifest could not be imported.

Asset #37: resolution '1920 by 1080' is invalid.

No Request Queue changes were applied.
```

---

# 89. IMPORT WHILE AN ACTIVE REFERENCE SESSION EXISTS

## Same-run queue already loaded

Disable `Import Request...`.

## Recovered queue-originated session and no manifest currently loaded

Allow Import only because the user may need to restore its queue association.

The imported manifest is accepted only if it contains:

```text
session.SourceRequestKey
```

If not:

```text
reject manifest
leave current state untouched
```

Message:

```text
The active recovered asset belongs to a Request that is not present in this manifest.
Import was cancelled.
```

## Manual Reference session

`SourceRequestKey == null`

Disable Request import until session is completed/cancelled.

---

# 90. REQUEST ACTIVATION

Single-click a Pending row.

Perform exactly:

```text
_activeRequest = item
Asset Name = item.AssetName
Final Prompt = item.Prompt
Prompt Preview updates
Clipboard = item.Prompt
row becomes active/bold
```

Do not:

- change Provider;
- change No reference mode;
- change Direct mode;
- select images;
- process files.

---

# 91. COMPLETED ROW CLICK

Clicking a `Done` row:

- may select the row visually;
- does **not** change Asset Name;
- does **not** change Prompt;
- does **not** copy clipboard;
- does **not** reactivate it.

No reprocessing from a green item by simple click.

---

# 92. CLIPBOARD FAILURE

If clipboard write fails:

- Request still activates;
- fields remain populated;
- show warning;
- do not clear Request.

Exact message:

```text
The Request was loaded successfully, but its Prompt could not be copied to the clipboard.

You can still use the Prompt shown in Final Prompt.
```

---

# 93. CLIPBOARD WRITER TEST HOOK

The application already has `ClipboardProvider` for clipboard reads.

Add:

```csharp
[DesignerSerializationVisibility(
    DesignerSerializationVisibility.Hidden)]
internal Action<string>? ClipboardWriter { get; set; }
```

Use test hook before WinForms clipboard.

---

# 94. REQUEST-BINDING DRIFT PROTECTION

Fields that define binding:

```text
Asset Name
Final Prompt
```

Introduce:

```csharp
private bool _settingRequestBoundFields;
```

Activation:

```csharp
_settingRequestBoundFields = true;

try
{
    txtAssetFolderName.Text =
        item.AssetName;

    txtPrompt.Text =
        item.Prompt;
}
finally
{
    _settingRequestBoundFields = false;
}
```

---

# 95. USER EDIT INVALIDATES REQUEST BINDING

Add:

```csharp
private void CheckActiveRequestBinding()
{
    if (_settingRequestBoundFields
        || _activeRequest is null)
    {
        return;
    }

    var stillMatches =
        string.Equals(
            txtAssetFolderName.Text.Trim(),
            _activeRequest.AssetName,
            StringComparison.Ordinal)
        && string.Equals(
            txtPrompt.Text,
            _activeRequest.Prompt,
            StringComparison.Ordinal);

    if (stillMatches)
    {
        return;
    }

    _activeRequest =
        null;

    RefreshRequestQueueVisuals();
}
```

Call from:

```text
txtAssetFolderName.TextChanged
txtPrompt.TextChanged
```

after existing validation UI clearing.

Result:

The manually edited asset can still be processed, but it is no longer considered completion of the imported Request.

---

# 96. ACTIVE REFERENCE SESSION REQUEST SWITCH RULE

While `_state == ReferenceReady`:

A Request may be selected only if:

```text
target.RequestKey == _currentSession.SourceRequestKey
```

No other Request can be activated.

This handles both:

- same-run active queue;
- recovered session.

Message for another Request:

```text
Finish or cancel the current reference-assisted asset before selecting another Request.
```

---

# 97. STORE REQUEST KEY IN SESSIONS

When `HandleReference()` creates a session, pass:

```csharp
_activeRequest?.RequestKey
```

as `sourceRequestKey`.

When `HandleNoReferenceMainImage()` creates the no-reference session, also pass it.

Manual workflow:

```text
_activeRequest == null
→ SourceRequestKey = null
```

---

# 98. RECOVERY OF QUEUE-BOUND REFERENCE

When a Reference session is recovered:

```text
_currentSession.SourceRequestKey != null
```

store no fabricated Request object yet.

If manifest later imported and contains that key:

1. bind matching item;
2. set Asset Name from session;
3. set Prompt from Request;
4. do **not** automatically copy clipboard on import;
5. mark matching row active;
6. only that row may be re-selected.

If user explicitly clicks that row afterward, clipboard copy occurs normally.

---

# 99. QUEUE COMPLETION POINT

A Request is Done **only after Main durable completion**.

Not after:

```text
selection
clipboard copy
Refresh
Reference
Main start
Main staging
```

Only after:

```text
CompleteMainUiAfterDurableCommit(...)
```

has been reached.

---

# 100. WHICH REQUEST KEY TO COMPLETE

Capture before UI fields are cleared.

Determine:

```csharp
var completedRequestKey =
    _activeRequest?.RequestKey
    ?? session.SourceRequestKey;
```

But only mark Done if:

1. key is not null;
2. current imported manifest exists;
3. manifest contains that key.

If manifest is not loaded after recovery:

- asset completes normally;
- progress cannot be visually updated;
- no error.

If same manifest is later imported, it will only show Done if progress was persisted.

Therefore for recovered sessions with SourceRequestKey and no loaded manifest, persist completion against the most recent known fingerprint only if fingerprint authority exists. Since it does not, **do not invent one**.

Simpler final rule:

> Request progress persistence is updated only when the matching manifest is currently loaded.

`SourceRequestKey` exists primarily to safely rebind a recovered session before completion.

---

# 101. REFERENCE REPLACEMENT + REQUEST PROMPT

Current manual behavior clears Main + Prompt after Reference replacement. Preserve for manual work.

For queue-bound asset:

```csharp
SetSelectedImage(
    ImageSlot.Main,
    null);

if (_activeRequest is null)
{
    txtPrompt.Clear();
}
else
{
    _settingRequestBoundFields = true;

    try
    {
        txtPrompt.Text =
            _activeRequest.Prompt;
    }
    finally
    {
        _settingRequestBoundFields = false;
    }
}
```

Do not automatically copy Prompt again.

---

# 102. CANCEL + REQUEST

After successful durable cancellation:

```text
Request remains Pending
```

Then:

```text
_activeRequest = null
```

Clear current manual fields as existing flow does.

Unlock queue.

User can click the same Pending Request again to restart.

Also remove cancelled Reference provenance from Recent Documents.

---

# 103. DIRECT MODE — FINAL DEFINITION

Checkbox:

```text
Direct mode
```

placed next to:

```text
No reference mode
```

Persistent via:

```text
settings.DirectModeEnabled
```

Default:

```text
false
```

---

# 104. DIRECT MODE DOES NOT MEAN FILE WATCHING

Do not add:

```text
FileSystemWatcher
automatic browser monitoring
clipboard monitoring
timers polling Downloads
background threads
```

Direct mode acts only when the user clicks:

```text
Main Image
```

or presses:

```text
Ctrl+M
```

---

# 105. DIRECT MODE BUTTON RULES

When Direct mode is ON:

```text
Refresh Reference → visible, disabled
Refresh Main      → visible, disabled
```

Keep:

```text
Choose File...
Drop file here
Open Downloads
```

visible and available.

However help text must clearly say:

> Main Image in Direct mode performs a fresh automatic Download-folder selection and therefore can replace a manually selected candidate.

This removes ambiguity.

---

# 106. DIRECT MODE STATE MATRIX

| Workflow | State | Direct | Reference CTA | Main CTA |
|---|---|---:|---:|---:|
| Reference-assisted | Idle | OFF | enabled | disabled |
| Reference-assisted | ReferenceReady | OFF | Replace enabled | enabled |
| No-reference | Idle | OFF | hidden | enabled |
| Reference-assisted | Idle | ON | disabled but visible | enabled |
| Reference-assisted | ReferenceReady | ON | disabled but visible | enabled |
| No-reference | Idle | ON | hidden | enabled |

No other interpretation is allowed.

---

# 107. MAIN BUTTON ENTRY POINT

Do not contaminate existing `HandleMainImage()` with Direct orchestration.

Change button event from:

```csharp
btnMainImage.Click +=
    (_, _) => HandleMainImage();
```

to:

```csharp
btnMainImage.Click +=
    (_, _) => HandleMainImageEntryPoint();
```

Add:

```csharp
private void HandleMainImageEntryPoint()
{
    if (!chkDirectMode.Checked)
    {
        HandleMainImage();
        return;
    }

    HandleDirectMainImage();
}
```

---

# 108. DIRECT NO-REFERENCE FLOW

Main click:

```text
validate Download Folder
↓
Find newest valid image
↓
SetSelectedImage(Main)
↓
run existing HandleMainImage()
```

Never process old currently displayed candidate without fresh automatic refresh.

---

# 109. `ImageFinderService` EXTENSION

Do not change ordering semantics.

Current ordering is:

1. LastWriteTimeUtc descending;
2. CreationTimeUtc descending;
3. filename.

Add:

```csharp
public IReadOnlyList<string> FindLatestImages(
    AppSettings settings,
    int count)
{
    ArgumentNullException.ThrowIfNull(settings);

    if (count <= 0)
    {
        throw new ArgumentOutOfRangeException(
            nameof(count));
    }

    if (string.IsNullOrWhiteSpace(
            settings.DownloadFolder)
        || !Directory.Exists(
            settings.DownloadFolder))
    {
        return Array.Empty<string>();
    }

    var allowed =
        new HashSet<string>(
            settings.AcceptedExtensions,
            StringComparer.OrdinalIgnoreCase);

    return Directory
        .EnumerateFiles(
            settings.DownloadFolder,
            "*",
            SearchOption.TopDirectoryOnly)
        .Select(
            path => new FileInfo(path))
        .Where(
            file =>
                allowed.Contains(
                    file.Extension))
        .OrderByDescending(
            file =>
                file.LastWriteTimeUtc)
        .ThenByDescending(
            file =>
                file.CreationTimeUtc)
        .ThenBy(
            file =>
                file.Name,
            StringComparer.OrdinalIgnoreCase)
        .Take(count)
        .Select(
            file =>
                file.FullName)
        .ToArray();
}
```

Then old:

```csharp
FindLatestImage(...)
```

may delegate:

```csharp
return FindLatestImages(
        settings,
        1)
    .FirstOrDefault();
```

Old behavior must remain covered by regression test.

---

# 110. DIRECT REFERENCE-ASSISTED FLOW

The user-defined ordering is authoritative:

```text
Reference downloaded first
Main downloaded second
```

Therefore on Main click:

```text
newest image      = Main
second-newest     = Reference
```

No heuristic matching by Request filename.

No resolution matching.

No filename matching.

---

# 111. DIRECT REFERENCE PREFLIGHT

Before calling Reference processor:

1. validate Download Folder;
2. collect latest 2 images;
3. require exactly at least 2;
4. paths must differ;
5. validate Reference file;
6. validate Main file;
7. only then set both visible candidates;
8. only then begin Reference transaction.

Thus if only one image exists:

```text
zero asset output
zero new session
zero new folder mutation
```

---

# 112. COPY-READY PAIR SELECTION

```csharp
private bool TrySelectDirectReferencePair()
{
    var downloadValidation =
        _validationService.ValidateDownloadFolder(
            txtDownloadFolder.Text);

    if (!downloadValidation.IsValid)
    {
        HighlightField(
            pnlDownloadFolderHost,
            true);

        ShowValidationError(
            "Direct mode requires a valid Image Download Folder.",
            downloadValidation);

        return false;
    }

    var settings =
        new AppSettings
        {
            DownloadFolder =
                txtDownloadFolder.Text,

            AcceptedExtensions =
                _settings.AcceptedExtensions.ToList()
        };

    IReadOnlyList<string> latest;

    try
    {
        latest =
            _imageFinderService
                .FindLatestImages(
                    settings,
                    2);
    }
    catch (Exception ex)
    {
        ShowError(
            "Could not scan the Image Download Folder.",
            ex);

        return false;
    }

    if (latest.Count < 2)
    {
        ShowMessageBox(
            "Direct reference mode requires two downloaded images.\n\n"
            + "Download the Reference image first and the Main image second.",
            "Two images required",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        return false;
    }

    var main =
        latest[0];

    var reference =
        latest[1];

    if (ValidationService.PathsEqual(
            main,
            reference))
    {
        ShowMessageBox(
            "Reference and Main resolved to the same file.",
            "Invalid Direct selection",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        return false;
    }

    var referenceValidation =
        _validationService.ValidateImageFile(
            reference,
            _settings.AcceptedExtensions);

    if (!referenceValidation.IsValid)
    {
        ShowValidationError(
            "Direct Reference image is invalid.",
            referenceValidation);

        return false;
    }

    var mainValidation =
        _validationService.ValidateImageFile(
            main,
            _settings.AcceptedExtensions);

    if (!mainValidation.IsValid)
    {
        ShowValidationError(
            "Direct Main image is invalid.",
            mainValidation);

        return false;
    }

    SetSelectedImage(
        ImageSlot.Reference,
        reference);

    SetSelectedImage(
        ImageSlot.Main,
        main);

    return true;
}
```

---

# 113. DIRECT ORCHESTRATOR — EXACT BEHAVIOR

```csharp
private void HandleDirectMainImage()
{
    if (chkNoReference.Checked)
    {
        if (!TryAutoSelectLatestMain())
        {
            return;
        }

        HandleMainImage();
        return;
    }

    if (_state == UiState.ReferenceReady)
    {
        // Existing Reference is already durable.
        // Retry/continuation refreshes only Main.
        if (!TryAutoSelectLatestMain())
        {
            return;
        }

        HandleMainImage();
        return;
    }

    if (!TrySelectDirectReferencePair())
    {
        return;
    }

    HandleReference();

    if (IsDisposed
        || _currentSession is null
        || _state != UiState.ReferenceReady)
    {
        return;
    }

    // Main candidate selected by pair preflight is still held.
    HandleMainImage();
}
```

---

# 114. MAIN-ONLY AUTO SELECT

```csharp
private bool TryAutoSelectLatestMain()
{
    var validation =
        _validationService.ValidateDownloadFolder(
            txtDownloadFolder.Text);

    if (!validation.IsValid)
    {
        HighlightField(
            pnlDownloadFolderHost,
            true);

        ShowValidationError(
            "Direct mode requires a valid Image Download Folder.",
            validation);

        return false;
    }

    var settings =
        new AppSettings
        {
            DownloadFolder =
                txtDownloadFolder.Text,

            AcceptedExtensions =
                _settings.AcceptedExtensions.ToList()
        };

    string? latest;

    try
    {
        latest =
            _imageFinderService.FindLatestImage(
                settings);
    }
    catch (Exception ex)
    {
        ShowError(
            "Could not scan the Image Download Folder.",
            ex);

        return false;
    }

    if (string.IsNullOrWhiteSpace(latest))
    {
        SetSelectedImage(
            ImageSlot.Main,
            null);

        ShowMessageBox(
            "No supported image was found in the Image Download Folder.",
            "No Main image found",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        return false;
    }

    var imageValidation =
        _validationService.ValidateImageFile(
            latest,
            _settings.AcceptedExtensions);

    if (!imageValidation.IsValid)
    {
        SetSelectedImage(
            ImageSlot.Main,
            null);

        ShowValidationError(
            "Latest image is invalid.",
            imageValidation);

        return false;
    }

    SetSelectedImage(
        ImageSlot.Main,
        latest);

    return true;
}
```

---

# 115. DIRECT REFERENCE SUCCESS + MAIN FAILURE

This must be treated as:

```text
Reference succeeded
Main failed
```

not as one giant transaction.

Reference remains.

State:

```text
_currentSession != null
_state == ReferenceReady
Request still Pending
```

Next Main click:

```text
state == ReferenceReady
→ refresh ONLY latest Main
→ do not select a new Reference
```

This is mandatory.

---

# 116. DIRECT MODE HOTKEYS

## Ctrl+R

Current Reference shortcut must be disabled in Direct mode.

Change condition to require:

```csharp
!chkDirectMode.Checked
```

## Ctrl+M

Route to:

```csharp
HandleMainImageEntryPoint()
```

and allow if:

```text
NoReference
OR ReferenceReady
OR Direct Reference-assisted Idle
```

Pseudo-condition:

```csharp
if (e.KeyCode == Keys.M)
{
    var canMain =
        _state == UiState.ReferenceReady
        || chkNoReference.Checked
        || (chkDirectMode.Checked
            && _state == UiState.Idle);

    if (canMain)
    {
        e.SuppressKeyPress = true;
        HandleMainImageEntryPoint();
    }
}
```

---

# 117. `ApplyState()` RULE

Do not scatter control state changes in multiple handlers.

After existing workflow-state logic, apply:

```csharp
var direct =
    chkDirectMode.Checked;

btnRefreshReference.Enabled =
    !direct;

btnRefreshMain.Enabled =
    !direct;
```

For Provider:

```csharp
cmbProvider.Enabled =
    !referenceReady
    && _providerCatalog?.HasUsableTemplates == true;
```

For Direct Reference Idle:

```csharp
if (!noReference
    && !referenceReady
    && direct)
{
    btnReference.Enabled = false;

    btnMainImage.Enabled =
        _templatesValid
        && CanStartNewAssetWithProvider;
}
```

For new normal/reference asset:

Provider availability is required.

For ReferenceReady:

Provider availability from current catalog is irrelevant; session authority governs.

---

# 118. NO-REFERENCE CHECKBOX + DIRECT

Changing `No reference mode` while Idle remains allowed.

It does not clear Direct mode.

Changing Direct mode does not change No-reference mode.

Both settings are independent.

---

# 119. REQUEST QUEUE + DIRECT MODE

Queue does not control Direct mode.

Manifest contains no:

```text
directMode
referenceMode
provider
```

Those remain user-selected global/current workflow controls.

This is deliberate.

---

# 120. REQUEST QUEUE + PROVIDER

Queue item activation does not change Provider.

The selected Provider at the moment a **new session begins** becomes the Provider snapshot.


---

# 121. MAINFORM PARTIAL FILES TO ADD

Add:

```text
MainForm.ProviderTemplates.cs
MainForm.RequestQueue.cs
MainForm.DirectMode.cs
MainForm.PromptPreview.cs
MainForm.RecentDocuments.cs
```

Do not put these features into `MainForm.cs` wholesale.

---

# 122. BOOTSTRAP ADDITIONS

Add to `AppBootstrapContext`:

```csharp
public required string ProviderTemplateDirectory { get; init; }

public required string RecentDocumentsPath { get; init; }

public required string RequestProgressPath { get; init; }

public required ProviderTemplateCatalogService
    ProviderTemplateCatalogService { get; init; }

public required RecentDocumentHistoryService
    RecentDocumentHistoryService { get; init; }

public required RequestProgressService
    RequestProgressService { get; init; }
```

---

# 123. BOOTSTRAP PATH HELPERS

Add:

```csharp
public static string GetProviderTemplateDirectory(
    string baseDirectory) =>
    Path.Combine(
        baseDirectory,
        AppConstants.ProviderTemplateFolderName);

public static string GetRecentDocumentsPath(
    string stateDirectory) =>
    Path.Combine(
        stateDirectory,
        AppConstants.RecentDocumentsFileName);

public static string GetRequestProgressPath(
    string stateDirectory) =>
    Path.Combine(
        stateDirectory,
        AppConstants.RequestProgressFileName);
```

Provider templates = install content.

History/progress = mutable user state.

---

# 124. DO NOT ADD NEW FILES TO LEGACY MIGRATION

`MigrateLegacyState()` currently migrates old:

```text
settings.json
session.json
reference-replacement.json
```

into stable LocalAppData state.

Do not add recent/progress files to legacy migration because they did not exist in old versions.

---

# 125. `Program.cs`

Pass three new services.

Do not introduce DI framework.

Current application explicitly constructs services and injects them into `MainForm`; preserve that architecture.

---

# 126. HELP OVERLAY — PROVIDER TEXT

Append:

```text
AI GENERATION PROVIDERS

AI Generation Provider templates are loaded when this application starts.

Provider template folder:

<application folder>\provider_templates\

Each selectable .md file represents one Provider.

Example:

ChatGPT.md   -> ChatGPT
Gemini.md    -> Gemini

Files whose filename begins with "_" are helper/template files and are not
shown in the Provider dropdown.

TO ADD A PROVIDER

1. Open the provider_templates folder.
2. Copy _TEMPLATE.md.
3. Rename the copy, for example:
   Gemini.md
4. Edit the Markdown file however you want.
5. Keep ALL required fields exactly as written:
   <<<PROVIDER>>>
   <<<DATE>>>
   <<<FILENAME>>>
   <<<ASSET_NAME>>>
   <<<PROJECT>>>
   <<<ROLE>>>
   <<<WORKFLOW>>>
   <<<REFERENCE_FILENAME>>>
   <<<PROMPT>>>
6. Save the file as UTF-8.
7. Restart AI Asset Provenance Helper.

After restart the new Provider automatically appears in the dropdown if the
template is valid.

The Markdown text, headings, paragraphs and provider-specific explanatory
content can otherwise be arranged freely.

The application never asks for Provider-specific runtime fields.
It does not ask for model, seed, API key, account, subscription, generation ID
or any other Provider-specific metadata.

For Reference provenance, <<<PROMPT>>> becomes "not recorded" because this
helper does not collect a separate Reference-generation Prompt.

An unsupported or malformed <<<...>>> field makes only that Provider template
invalid. It does not prevent the application from starting.

The original Provider template file is never modified. The helper creates a
rendered copy for each provenance output and replaces the predefined tags in
that copy.
```

---

# 127. HELP OVERLAY — REQUEST IMPORT TEXT

Append:

```text
ASSET REQUEST IMPORT

A prepared Request Manifest can be imported into the Request Queue on the
right side of the application.

The exact JSON template is included at:

<application folder>\examples\asset_request_manifest_template.json

A ready-to-use instruction for converting an existing asset-request document
with another AI is included at:

<application folder>\examples\asset_request_conversion_prompt.txt

Every requested asset contains exactly:

filename
resolution
prompt

When you click a Pending Request:

- Asset Name is filled automatically.
- Final Prompt is filled automatically.
- The complete Prompt is copied to the clipboard.

The Request remains Pending until the Main Image has been successfully
committed by this helper.

A Done Request is shown with the word Done and a green background.

Request progress is restored when the same semantic Manifest is imported
again.
```

---

# 128. HELP OVERLAY — DIRECT MODE TEXT

Append:

```text
DIRECT MODE

Direct mode removes the manual Refresh click.

When Direct mode is enabled, the Refresh buttons remain visible but disabled.

NO-REFERENCE

1. Prepare/select the asset and Prompt.
2. Generate the image in the browser.
3. Download the image.
4. Return to the helper.
5. Click Main Image.

The helper automatically selects the newest supported image in the configured
Image Download Folder and then runs the normal Main Image workflow.

REFERENCE-ASSISTED

1. Prepare/select the asset and Prompt.
2. Generate/download the Reference image FIRST.
3. Generate/download the final Main image SECOND.
4. Return to the helper.
5. Click Main Image.

The helper selects:

second-newest supported image = Reference
newest supported image        = Main

Both candidates are validated before Reference processing begins.

The Reference button remains visible but disabled while Direct mode is active.

If Reference succeeds but Main fails, the Reference remains saved. Generate
and download a new Main image and click Main Image again. On that retry only
Main is refreshed.
```

---

# 129. VERSION

Change:

```xml
<Version>1.2.1</Version>
```

to:

```xml
<Version>1.3.0</Version>
```

`AppInfo.Version` already derives its visible value from assembly version, so no separate hard-coded UI version update is necessary.

---

# 130. PROJECT FILE PACKAGING

Extend `AssetProvenanceHelper.csproj`:

```xml
<ItemGroup>
  <None Update="templates\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </None>

  <None Update="provider_templates\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </None>

  <None Update="examples\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </None>
</ItemGroup>
```

Do not delete the old template packaging.

---

# 131. SMOKE TEST UPDATE

The current smoke test verifies all three old templates, runtime dependencies, application startup, window creation, icon, clean shutdown and archive creation.

After legacy template checks add:

```powershell
# Verify v1.3 provider templates
$providerTemplateDir = Join-Path $PublishDir "provider_templates"
$chatGptTemplate = Join-Path $providerTemplateDir "ChatGPT.md"
$providerTemplateExample = Join-Path $providerTemplateDir "_TEMPLATE.md"

if (-not (Test-Path $chatGptTemplate)) {
    throw "ChatGPT provider template missing at: $chatGptTemplate"
}

if (-not (Test-Path $providerTemplateExample)) {
    throw "Provider template example missing at: $providerTemplateExample"
}

# Verify v1.3 request examples
$examplesDir = Join-Path $PublishDir "examples"
$requestManifestTemplate =
    Join-Path $examplesDir "asset_request_manifest_template.json"
$requestConversionPrompt =
    Join-Path $examplesDir "asset_request_conversion_prompt.txt"

if (-not (Test-Path $requestManifestTemplate)) {
    throw "Request Manifest template missing at: $requestManifestTemplate"
}

if (-not (Test-Path $requestConversionPrompt)) {
    throw "Request conversion prompt missing at: $requestConversionPrompt"
}

Write-Host "v1.3 provider/request support files verified."
```

Also extend mutable-state check:

```powershell
$mutableStateFileNames = @(
    "settings.json",
    "session.json",
    "reference-replacement.json",
    "recent-documents.json",
    "request-progress.json"
)
```

Those two new state files must never ship inside publish output.

---

# 132. TESTWORKSPACE — REQUIRED ADDITIONS

Add properties:

```csharp
public string ProviderTemplates { get; }

public string Examples { get; }

public string RecentDocumentsPath =>
    Path.Combine(
        Root,
        AppConstants.RecentDocumentsFileName);

public string RequestProgressPath =>
    Path.Combine(
        Root,
        AppConstants.RequestProgressFileName);

public string ChatGptProviderTemplatePath =>
    Path.Combine(
        ProviderTemplates,
        "ChatGPT.md");
```

Constructor:

```csharp
ProviderTemplates =
    Path.Combine(
        Root,
        "provider_templates");

Examples =
    Path.Combine(
        Root,
        "examples");

Directory.CreateDirectory(
    ProviderTemplates);

Directory.CreateDirectory(
    Examples);

WriteValidProviderTemplate();
```

---

# 133. TESTWORKSPACE VALID PROVIDER TEMPLATE

Add:

```csharp
public void WriteValidProviderTemplate(
    string fileName = "ChatGPT.md")
{
    var content =
        """
        Provider: <<<PROVIDER>>>
        Date: <<<DATE>>>
        File: <<<FILENAME>>>
        Asset: <<<ASSET_NAME>>>
        Project: <<<PROJECT>>>
        Role: <<<ROLE>>>
        Workflow: <<<WORKFLOW>>>
        Reference: <<<REFERENCE_FILENAME>>>
        Prompt:
        <<<PROMPT>>>
        """;

    File.WriteAllText(
        Path.Combine(
            ProviderTemplates,
            fileName),
        content,
        new UTF8Encoding(false));
}
```

Helpers:

```csharp
public ProviderTemplateCatalogService
    CreateProviderTemplateCatalogService() =>
        new(
            ProviderTemplates);

public RecentDocumentHistoryService
    CreateRecentDocumentHistoryService() =>
        new(
            RecentDocumentsPath);

public RequestProgressService
    CreateRequestProgressService() =>
        new(
            RequestProgressPath);
```

---

# 134. TEST FILES TO ADD

Prefer new focused files instead of massively extending the already-large regression test files.

Add:

```text
UpgradeV13ProviderTemplateTests.cs
UpgradeV13ProviderSessionTests.cs
UpgradeV13RequestManifestTests.cs
UpgradeV13RequestProgressTests.cs
UpgradeV13RecentDocumentsTests.cs
UpgradeV13PromptPreviewTests.cs
UpgradeV13DirectModeTests.cs
UpgradeV13MainFormTests.cs
UpgradeV13LegacyCompatibilityTests.cs
```

---

# 135. PROVIDER TEMPLATE TEST MATRIX

Required tests:

- valid template loads;
- `_TEMPLATE.md` ignored;
- missing `PROVIDER` rejected;
- missing `DATE` rejected;
- missing `FILENAME` rejected;
- missing `ASSET_NAME` rejected;
- missing `PROJECT` rejected;
- missing `ROLE` rejected;
- missing `WORKFLOW` rejected;
- missing `REFERENCE_FILENAME` rejected;
- missing `PROMPT` rejected;
- unknown `<<<MODEL>>>` rejected;
- lowercase `<<<date>>>` rejected;
- malformed `<<<DATE>>` rejected;
- duplicate required tags allowed;
- arbitrary Markdown headings allowed;
- UTF-8 BOM allowed;
- UTF-16 rejected;
- invalid UTF-8 rejected;
- oversized template rejected;
- reparse-point template rejected where test platform permits;
- one bad file does not suppress good file;
- alphabetical dropdown order deterministic;
- Provider content hash deterministic.

---

# 136. PROVIDER RENDER TEST MATRIX

Required:

- Reference maps all 9 values;
- Reference Prompt = `not recorded`;
- Final ref-assisted exact Prompt;
- Final ref-assisted Reference filename;
- NoReference Reference filename = `not recorded`;
- Date exact `yyyy-MM-dd`;
- literal Provider tags inside inserted Prompt remain literal;
- same tag appearing twice is replaced twice;
- original template content is unchanged after render.

---

# 137. PROVIDER SESSION TEST MATRIX

Required:

```text
new Reference with provider
→ SchemaVersion 3
→ snapshot exists

new NoReference with provider
→ SchemaVersion 3
→ snapshot exists

legacy creation without provider optional arg
→ SchemaVersion 2
→ snapshot null
```

Then:

```text
delete Provider file after Reference
→ saved session Main still succeeds

modify Provider file after Reference
→ Main still uses original snapshot
```

---

# 138. REFERENCE REPLACEMENT PROVIDER TESTS

Required:

1. create v1.3 Reference with Provider A;
2. create replacement;
3. assert OldSession snapshot = A;
4. assert NewSession snapshot = A;
5. assert SourceRequestKey preserved;
6. replacement provenance uses A;
7. current dropdown selection must not affect replacement.

---

# 139. LEGACY VALIDATION TEST

Create a schema-2 session and old-style provenance.

Assert:

```text
ValidateExactReferenceOutput
```

still works.

Do not require Provider tags.

This is essential.

---

# 140. ARBITRARY TEMPLATE VALIDATION TEST

Provider template:

```markdown
## Completely Custom Heading

foo <<<PROJECT>>>

date=<<<DATE>>>

PROVIDER [[[ <<<PROVIDER>>> ]]]

FILE <<<FILENAME>>>

asset <<<ASSET_NAME>>>

ROLE <<<ROLE>>>

MODE <<<WORKFLOW>>>

REF <<<REFERENCE_FILENAME>>>

PROMPT
<<<PROMPT>>>
```

It must:

- render;
- write;
- pass exact validation.

This specifically proves that hard-coded `Asset ID:` etc. no longer leaks into Provider-session validation.

---

# 141. MANIFEST TEST MATRIX

Required:

- one valid asset;
- multiple assets;
- 150 assets;
- 5000 assets;
- 5001 rejected;
- missing manifestVersion;
- version 0 rejected;
- version 2 rejected;
- empty assets rejected;
- unknown top-level field rejected;
- unknown asset field rejected;
- missing filename;
- missing resolution;
- missing prompt;
- empty prompt;
- unsupported extension;
- path-containing filename;
- duplicate filename case-insensitive;
- Windows reserved-derived Asset Name rejected through existing validator;
- `1920x1080`;
- `1920×1080`;
- spaced x;
- zero dimension rejected;
- >100000 rejected;
- prompt line endings preserved in stored Prompt;
- CRLF/LF normalized for Request key;
- same semantic request produces same key;
- changed Prompt changes key;
- changed Resolution changes key;
- changed Filename changes key;
- manifest formatting differences do not change fingerprint;
- manifest item reorder does not change fingerprint;
- changed semantic item changes fingerprint.

---

# 142. REQUEST PROGRESS TEST MATRIX

- missing state → empty;
- same fingerprint restores keys;
- different fingerprint returns empty;
- save atomic;
- duplicate keys deduplicated;
- corrupt state reported/handled by UI without startup failure;
- Done Request stays Done after re-import of same manifest.

---

# 143. RECENT DOCUMENT TEST MATRIX

Start:

```text
none
```

record A:

```text
A
```

record B:

```text
B
A
```

record C:

```text
C
B
A
```

record D:

```text
D
C
B
```

Record B again:

```text
B
D
C
```

No duplicates.

Also:

- persists restart;
- Reference and Final kinds preserved;
- cancellation removes matching Reference history;
- history save failure does not roll back completed asset;
- no ordinary `AddStatus()` message appears in recent-document list.

---

# 144. PROMPT PREVIEW TESTS

```text
empty
1 char
99 chars
100 chars
101 chars
1000 chars
CRLF
tab
Unicode
```

At 100:

```text
no ...
```

At 101:

```text
exact first 100 + ...
```

Overlay receives exact full Prompt.

---

# 145. REQUEST QUEUE TEST MATRIX

- successful import populates queue;
- failed import leaves previous queue;
- queue order = manifest order;
- Done row text = Done;
- Done row green;
- Pending row normal;
- click Pending populates exact Asset Name;
- click Pending populates exact Prompt;
- click Pending calls ClipboardWriter;
- Clipboard failure retains active Request;
- click Done does not modify fields;
- user editing Prompt invalidates `_activeRequest`;
- user editing Asset Name invalidates `_activeRequest`;
- re-click same Pending restores binding;
- ReferenceReady blocks unrelated Request;
- ReferenceReady permits its own SourceRequestKey;
- Main failure leaves Request Pending;
- Main success marks matching Request Done;
- manual asset with no Request does not affect queue.

---

# 146. DIRECT MODE TEST MATRIX

## Direct false

Main button must not automatically replace manually selected Main candidate.

This is the key regression test.

## Direct NoReference

Newest supported Download is selected.

## Direct Reference

Given chronological files:

```text
old.png
reference.png
main.png
```

expect:

```text
reference.png = Reference
main.png      = Main
```

## One available candidate

Expect:

```text
no transaction
no new session
no asset folder mutation
```

## Reference succeeds/Main fails

Expect:

```text
Reference files remain
session ReferenceReady
Request Pending
```

Then add new Main.

Click again.

Expect:

```text
Reference unchanged
only Main refreshed
```

---

# 147. HOTKEY TESTS

Direct OFF:

```text
Ctrl+R → Reference
Ctrl+M → normal existing rules
```

Direct ON Reference Idle:

```text
Ctrl+R → nothing
Ctrl+M → Direct pair orchestrator
```

Direct ON ReferenceReady:

```text
Ctrl+M → auto-select Main only
```

---

# 148. PROVIDER AVAILABILITY TESTS

No provider catalog service in old test constructor:

```text
old tests still work using schema 2
```

Production catalog empty + Idle:

```text
new asset CTAs disabled
```

Production catalog empty + recovered schema2 ReferenceReady:

```text
Main enabled
```

Production catalog empty + recovered schema3 Provider snapshot:

```text
Main enabled
```

---

# 149. EXACT IMPLEMENTATION PHASES FOR A WEAK MODEL

The model must execute phases in order.

Do not combine phases unless explicitly stated.

---

## PHASE 000 — baseline only

Do:

```bash
dotnet restore
dotnet build AssetProvenanceHelper.sln
dotnet test AssetProvenanceHelper.sln
```

Run smoke test using current documented repository procedure.

Record failing baseline tests before changes.

No product code changes.

Commit:

```text
chore: record v1.2.1 upgrade baseline
```

---

## PHASE 001 — characterization tests

Add tests for:

```text
FindLatestImage ordering
normal Reference state
normal NoReference state
Ctrl+R/Ctrl+M
legacy TemplateService
schema-2 sessions
Main durable completion
Reference durable completion
```

No product behavior change.

Commit:

```text
test: characterize v1.2.1 behavior before v1.3 upgrade
```

Run full tests.

Stop on failure.

---

## PHASE 002 — constants + additive models

Add only:

```text
ProviderTemplateSnapshot
ProviderTemplateDefinition
ProviderCatalogResult
ProviderRenderContext
new AppConstants
AppSettings two new fields
AssetSession two new fields
```

Keep `SchemaVersion = 2`.

No Provider rendering yet.

Tests:

```text
old settings still load
new settings defaults
old session serialization
new fields round-trip
```

Commit:

```text
feat: add v1.3 provider and workflow state models
```

---

## PHASE 003 — Provider rules/catalog

Add:

```text
ProviderTemplateRules
ProviderTemplateCatalogService
```

Add Provider tests.

Do not touch processor.

Commit:

```text
feat: add markdown provider template catalog
```

Full tests.

---

## PHASE 004 — Provider renderer

Add:

```text
ProviderTemplateRenderer
```

and rendering tests including single-pass Prompt test.

No processor integration.

Commit:

```text
feat: add single-pass provider template renderer
```

---

## PHASE 005 — TemplateService bridge

Add:

```text
RenderReferenceForSession
RenderFinalForSession
```

Legacy methods untouched.

Tests for:

```text
schema2 → legacy
snapshot → provider
```

Commit:

```text
feat: add session-aware provenance rendering
```

---

## PHASE 006 — session metadata validation

Add:

```text
ValidateUpgradeV13Metadata
```

to normal and prepared session validation.

Do not change provenance content validation yet.

Commit:

```text
feat: validate provider snapshots in v1.3 sessions
```

---

## PHASE 007 — provider-aware exact provenance validation

Implement sections 27–32 of this document.

This phase must prove arbitrary headings work.

Commit:

```text
feat: validate arbitrary provider provenance by exact snapshot ownership
```

Run **all** recovery/security tests.

Do not continue if any fail.

---

## PHASE 008 — Reference processor Provider integration

Only modify Reference creation/authority rendering.

Do not modify Main in same commit.

Add Provider args to `CreateReferenceSession`.

Commit:

```text
feat: render reference provenance from provider snapshot
```

Run all Reference and recovery tests.

---

## PHASE 009 — Main processor Provider integration

Modify:

```text
CreateNoReferenceMainSession
PrepareMainCommit
ProcessMainImage
```

only for rendering/provider/session fields.

Do not refactor transaction code.

Commit:

```text
feat: render final provenance from provider snapshot
```

Run all Main/rollback/recovery tests.

---

## PHASE 010 — Reference Replacement Provider preservation

Modify replacement copies/rendering/journal validation.

Tests must include old/new snapshot equality.

Commit:

```text
feat: preserve provider authority across reference replacement
```

---

## PHASE 011 — Provider files + packaging

Add:

```text
provider_templates\ChatGPT.md
provider_templates\_TEMPLATE.md
```

Update csproj.

Do not add GUI yet.

Commit:

```text
build: package provider template files
```

---

## PHASE 012 — Provider GUI

Add dropdown + warning.

Use optional MainForm service parameter.

Production Bootstrap/Program passes service.

Old tests remain compatible.

Commit:

```text
feat: add AI provider template selection
```

---

## PHASE 013 — Recent Documents service

Add models/service/tests.

No GUI yet.

Commit:

```text
feat: add persistent recent provenance history
```

---

## PHASE 014 — Recent Documents UI

Add visible ListView.

Keep hidden `txtStatusHistory`.

Connect durable commit points and cancel.

Commit:

```text
fix: show last three generated provenance documents
```

This fixes the originally reported empty status section.

---

## PHASE 015 — Prompt Preview helper

Add preview label + truncation tests.

No hover overlay yet.

Commit:

```text
feat: add final prompt preview
```

---

## PHASE 016 — Prompt hover overlay

Add overlay control and timer.

Commit:

```text
feat: show full prompt on preview hover
```

---

## PHASE 017 — Manifest models/parser

Add:

```text
AssetRequestItem
AssetRequestManifest
AssetRequestManifestService
```

Add JSON template + conversion prompt.

No queue yet.

Commit:

```text
feat: add deterministic asset request manifest parser
```

---

## PHASE 018 — Request progress

Add:

```text
RequestProgressState
RequestProgressService
```

Tests.

Commit:

```text
feat: persist request manifest progress
```

---

## PHASE 019 — outer workspace + queue visual only

Wrap current UI and add right queue.

Do not make rows manipulate fields yet.

Commit:

```text
feat: add request queue workspace
```

Perform manual visual launch at:

```text
1500x800
1240x700
```

---

## PHASE 020 — manifest import

Connect Import button.

Atomic import only.

Commit:

```text
feat: import asset request manifests
```

---

## PHASE 021 — Request activation + Clipboard

Connect Pending single-click.

Add binding guard.

Commit:

```text
feat: populate asset fields from request queue
```

---

## PHASE 022 — SourceRequestKey integration

Pass Request key into Reference and NoReference sessions.

Implement recovery rebind rules.

Commit:

```text
feat: persist request identity through asset sessions
```

---

## PHASE 023 — queue completion

Mark Done only after Main durable commit.

Do not alter transaction rollback paths.

Commit:

```text
feat: complete request items after durable main commit
```

---

## PHASE 024 — Direct checkbox/state only

Add checkbox and persisted setting.

Modify ApplyState/button disabling.

No auto-selection yet.

Commit:

```text
feat: add direct mode UI state
```

---

## PHASE 025 — Main entry point + Direct NoReference

Add:

```text
HandleMainImageEntryPoint
TryAutoSelectLatestMain
```

Normal path remains unchanged.

Commit:

```text
feat: automate main refresh in direct no-reference mode
```

---

## PHASE 026 — ImageFinder pair API

Add `FindLatestImages`.

Regression-test old method.

Commit:

```text
feat: support ordered latest image selection
```

---

## PHASE 027 — Direct Reference pair

Add pair preflight/orchestrator.

Commit:

```text
feat: automate reference and main selection in direct mode
```

Run failure/retry tests.

---

## PHASE 028 — Direct hotkeys

Adjust Ctrl+R/Ctrl+M.

Commit:

```text
feat: align keyboard shortcuts with direct mode
```

---

## PHASE 029 — queue + replacement/cancel edge cases

Implement:

```text
replacement prompt restore
cancel unbind
import restrictions while active
recovered session manifest rebind
```

Commit:

```text
fix: preserve request integrity across reference session transitions
```

---

## PHASE 030 — help

Insert exact help text from this document.

Commit:

```text
docs: document provider templates request import and direct mode
```

---

## PHASE 031 — packaging/smoke/version

Version 1.3.0.

Update smoke test.

Verify publish tree.

Commit:

```text
build: prepare v1.3.0 package validation
```

---

## PHASE 032 — full regression

Run:

```bash
dotnet clean AssetProvenanceHelper.sln
dotnet restore AssetProvenanceHelper.sln
dotnet build AssetProvenanceHelper.sln -c Release
dotnet test AssetProvenanceHelper.sln -c Release
```

Then publish/smoke using repository's existing publish process.

No code cleanup during this phase.

Only bug fixes proven necessary by failures.

---

# 150. FILE CHANGE MAP

## New files

```text
src/AssetProvenanceHelper/Models/ProviderTemplateSnapshot.cs
src/AssetProvenanceHelper/Models/ProviderTemplateDefinition.cs
src/AssetProvenanceHelper/Models/ProviderCatalogResult.cs
src/AssetProvenanceHelper/Models/ProviderRenderContext.cs

src/AssetProvenanceHelper/Models/AssetRequestItem.cs
src/AssetProvenanceHelper/Models/AssetRequestManifest.cs
src/AssetProvenanceHelper/Models/RequestProgressState.cs
src/AssetProvenanceHelper/Models/RecentDocumentEntry.cs
src/AssetProvenanceHelper/Models/RecentDocumentHistoryState.cs

src/AssetProvenanceHelper/Services/ProviderTemplateRules.cs
src/AssetProvenanceHelper/Services/ProviderTemplateCatalogService.cs
src/AssetProvenanceHelper/Services/ProviderTemplateRenderer.cs
src/AssetProvenanceHelper/Services/AssetRequestManifestService.cs
src/AssetProvenanceHelper/Services/RequestProgressService.cs
src/AssetProvenanceHelper/Services/RecentDocumentHistoryService.cs

src/AssetProvenanceHelper/MainForm.ProviderTemplates.cs
src/AssetProvenanceHelper/MainForm.RequestQueue.cs
src/AssetProvenanceHelper/MainForm.DirectMode.cs
src/AssetProvenanceHelper/MainForm.PromptPreview.cs
src/AssetProvenanceHelper/MainForm.RecentDocuments.cs

src/AssetProvenanceHelper/Ui/PromptPreviewOverlayControl.cs

src/AssetProvenanceHelper/provider_templates/ChatGPT.md
src/AssetProvenanceHelper/provider_templates/_TEMPLATE.md

src/AssetProvenanceHelper/examples/asset_request_manifest_template.json
src/AssetProvenanceHelper/examples/asset_request_conversion_prompt.txt
```

## Existing files deliberately modified

```text
AppConstants.cs
Models/AppSettings.cs
Models/AssetSession.cs

Services/SettingsService.cs
Services/TemplateService.cs
Services/ImageFinderService.cs
Services/AssetProcessorService.Main.cs
Services/AssetProcessorService.Reference.cs
Services/ValidationService.Session.cs
Services/AppBootstrap.cs

MainForm.Designer.cs
MainForm.Layout.cs
MainForm.cs
MainForm.MainWorkflow.cs
MainForm.ReferenceWorkflow.cs
MainForm.ValidationUi.cs
MainForm.Recovery.cs

Ui/HelpOverlayControl.cs

Program.cs
AssetProvenanceHelper.csproj

scripts/run_smoke_tests.ps1

tests/.../TestWorkspace.cs
```

---

# 151. FILES THAT MUST NOT BE BROADLY REFACTORED

Treat as sensitive:

```text
AssetProcessorService.Main.cs
AssetProcessorService.Reference.cs
SessionService.cs
ValidationService.Session.cs
MainForm.Recovery.cs
```

Changes there must be narrowly scoped.

Example good change:

```text
replace one legacy render call with session-aware render call
```

Example forbidden change:

```text
provider integration
+
rename every method
+
rewrite rollback
+
move transaction functions
+
change session semantics
```

---

# 152. PROHIBITED SCOPE CREEP

The implementation model must **not** introduce:

- WPF;
- MAUI;
- Avalonia;
- MVVM framework;
- dependency-injection framework;
- database;
- SQLite;
- YAML parser;
- Markdown parser package;
- third-party template engine;
- image conversion;
- resize;
- browser automation;
- OpenAI API;
- Gemini API;
- Provider API calls;
- FileSystemWatcher;
- download-folder background monitoring;
- clipboard polling;
- filesystem deletion of browser downloads;
- automatic overwriting;
- cloud sync;
- telemetry;
- auto-update system;
- new legal claims.

---

# 153. FINAL MANUAL USER WORKFLOWS

## Existing manual no-reference

```text
No reference mode ON
Direct OFF
Asset Name manually entered
Prompt manually entered
select/Refresh Main
Main Image
```

---

## Existing manual Reference

```text
No reference OFF
Direct OFF
Asset Name
select/Refresh Reference
Reference
select/Refresh Main
Prompt
Main Image
```

---

## Queue + manual refresh no-reference

```text
Import Request
click Pending item
Prompt copied
browser generate/download
Refresh Main
Main Image
Done
```

---

## Queue + Direct no-reference

```text
Import Request
click Pending item
Prompt copied
browser generate/download
Main Image
automatic newest-image selection
Done
```

---

## Queue + manual Reference

```text
Import Request
click Pending item
Prompt copied
browser Reference/download
Refresh Reference
Reference
browser Main/download
Refresh Main
Main Image
Done
```

---

## Queue + Direct Reference

```text
Import Request
click Pending item
Prompt copied
browser Reference/download
browser Main/download
Main Image
second-newest → Reference
newest → Main
Reference durable commit
Main durable commit
Done
```

---

# 154. FINAL ACCEPTANCE CRITERIA

v1.3.0 is not complete until **all** are true:

- [ ] Existing v1.2.1 settings load.
- [ ] Direct mode defaults false for old settings.
- [ ] Legacy template files remain shipped.
- [ ] Legacy schema-2 unfinished sessions recover.
- [ ] Schema-2 sessions do not require Provider snapshots.
- [ ] New production sessions use schema 3.
- [ ] Schema-3 sessions contain a Provider snapshot.
- [ ] Provider snapshot contains full template content.
- [ ] Provider snapshot survives restart.
- [ ] Provider snapshot survives Reference Replacement.
- [ ] Deleted Provider file does not break active session.
- [ ] Modified Provider file does not change active session.
- [ ] Provider templates are `.md`.
- [ ] Provider layout may be arbitrary.
- [ ] All nine required tags are mandatory.
- [ ] Unknown tags invalidate only that template.
- [ ] `_*.md` templates are ignored in dropdown.
- [ ] Provider scanning occurs at startup.
- [ ] No Provider-specific runtime fields exist.
- [ ] No model/seed/account/API information is requested.
- [ ] Provider substitution is single-pass.
- [ ] Tags inside inserted Prompt remain literal.
- [ ] Reference Prompt renders `not recorded`.
- [ ] Provider Reference exact validation supports arbitrary headings.
- [ ] Provider Final exact validation supports arbitrary headings.
- [ ] Legacy hard-coded semantic validation remains available for legacy documents.
- [ ] Provider dropdown sits in Settings.
- [ ] Provider is locked during ReferenceReady.
- [ ] Missing current Provider falls back deterministically.
- [ ] No valid Providers blocks only new assets.
- [ ] Recovered Provider session can finish without current Provider file.
- [ ] Status section visibly renders recent documents.
- [ ] Maximum recent documents is exactly 3.
- [ ] New document displaces oldest.
- [ ] Duplicate Reference path moves to newest position.
- [ ] Normal AddStatus lines do not appear in Recent Documents.
- [ ] Cancel removes cancelled Reference document from recent history.
- [ ] History failure never rolls back an asset.
- [ ] Prompt Preview displays first 100 characters.
- [ ] Character 101 adds `...`.
- [ ] Preview line breaks display as spaces.
- [ ] Prompt itself remains unchanged.
- [ ] Hover overlay shows complete exact Prompt.
- [ ] Request Manifest is strict JSON.
- [ ] Only manifestVersion/assets are accepted at top level.
- [ ] Only filename/resolution/prompt are accepted per asset.
- [ ] Unknown Manifest fields are rejected.
- [ ] Invalid Manifest never partially replaces queue.
- [ ] Filename must be leaf filename.
- [ ] Filename extension must be supported.
- [ ] Asset Name derives deterministically from filename.
- [ ] Resolution is normalized.
- [ ] Resolution does not block downloaded image dimensions in v1.3.
- [ ] Manifest order is preserved in queue.
- [ ] Manifest fingerprint is semantic, not formatting-dependent.
- [ ] Progress is scoped to ManifestFingerprint.
- [ ] Same manifest restores Done rows.
- [ ] Different manifest starts with no inherited Done rows.
- [ ] Pending click fills Asset Name.
- [ ] Pending click fills exact Prompt.
- [ ] Pending click copies exact Prompt.
- [ ] Clipboard failure does not cancel Request activation.
- [ ] Done click does not reactivate an asset.
- [ ] Editing Request-bound Prompt unbinds Request.
- [ ] Editing Request-bound Asset Name unbinds Request.
- [ ] SourceRequestKey survives Reference session.
- [ ] Unrelated queue item cannot replace an active Reference association.
- [ ] Request becomes Done only after Main durable commit.
- [ ] Main failure leaves Request Pending.
- [ ] Manual assets do not alter queue progress.
- [ ] Cancelled queue asset remains Pending.
- [ ] Reference Replacement restores queue Prompt.
- [ ] Direct checkbox persists.
- [ ] Direct ON disables visible Refresh buttons.
- [ ] Direct OFF preserves old Refresh behavior.
- [ ] Direct NoReference chooses newest image on Main click.
- [ ] Direct Reference chooses second-newest as Reference.
- [ ] Direct Reference chooses newest as Main.
- [ ] Both Direct candidates are validated before Reference mutation.
- [ ] Fewer than 2 candidates creates no Reference output.
- [ ] Direct Reference button remains visible but disabled.
- [ ] Reference success/Main failure preserves Reference.
- [ ] Retry after that refreshes only Main.
- [ ] Ctrl+R does nothing in Direct mode.
- [ ] Ctrl+M invokes Direct mode correctly.
- [ ] Browser download source files remain untouched.
- [ ] Existing collision protection remains.
- [ ] Existing reparse-point protection remains.
- [ ] Existing Main hash verification remains.
- [ ] Existing ingame byte-identity check remains.
- [ ] Existing Reference rollback remains.
- [ ] Existing Main rollback remains.
- [ ] Existing replacement journal recovery remains.
- [ ] Publish contains ChatGPT.md.
- [ ] Publish contains _TEMPLATE.md.
- [ ] Publish contains Request Manifest example.
- [ ] Publish contains conversion prompt.
- [ ] Publish contains all three legacy templates.
- [ ] Publish contains no mutable state files.
- [ ] Product version is 1.3.0.
- [ ] Debug build passes.
- [ ] Release build passes.
- [ ] Entire test suite passes.
- [ ] Published application smoke test passes.
- [ ] Existing application icon/window startup smoke checks still pass.

---

# 155. IMPLEMENTATION MODEL FINAL INSTRUCTION

The implementation model should be given this final instruction together with this document:

> Implement `_upgrade1.md` phase-by-phase and treat it as authoritative. Do not make product-design decisions that are not explicitly requested. Do not simplify, replace, or broadly refactor the existing transaction, rollback, recovery, path-validation or hashing architecture. After every phase, build and run the relevant tests; after every commit, run the full test suite. If a proposed change conflicts with an existing safety invariant, preserve the existing safety invariant and stop/report the conflict instead of inventing a workaround. New Provider sessions use snapshot-based arbitrary Markdown provenance; legacy sessions remain on the existing templates. Direct mode only selects/orchestrates existing workflows. Request Queue state is auxiliary and must never become authority for destructive filesystem operations.

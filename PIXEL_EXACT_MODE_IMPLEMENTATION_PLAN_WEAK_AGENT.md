# Implementation Plan — Pixel-Exact Mode + Queue-driven Variant Auto-Detection

**Repository:** `https://github.com/Ceegore/gpt_provenance_document_helper`  
**Baseline verified against:** `main` at commit `3b42ccb0781d81e1d2ee33f992e16f8bcdbeb3a1` (2026-09-05)  
**Target application:** `.NET 10` WinForms, `AssetProvenanceHelper`  
**Plan purpose:** implementation by a weak coding model with as few architectural decisions as possible  
**Feature scope:** new Pixel-Exact workflow, automatic Pixel-Exact count detection, automatic Variants count detection, queue auto-advance within a Pixel-Exact series, safe restart/cross-manifest continuation, prompt/example updates, tests and documentation.

---

## 0. Mandatory execution rules for the implementing model

Follow these rules literally.

1. **Do not redesign the application.** Make the smallest safe extension to the current architecture.
2. **Do not replace the existing Variants implementation.** It remains a separate feature.
3. **Do not add fields to manifest asset objects.** Keep `manifestVersion: 2` and exactly:
   - `filename`
   - `resolution`
   - `alpha`
   - `prompt`
4. Put the new machine-readable workflow information **inside `prompt`**.
5. Preserve the existing invariant:
   > A Request becomes `Done` only after that asset's Main commit is durable.
6. Never mark several queue rows `Done` merely because a Pixel-Exact batch started.
7. Pixel-Exact output assets use the **real queue asset names**. Never append `A`, `B`, `C`, etc.
8. Never make Pixel-Exact and Variants active at the same time.
9. Do not silently guess a phase mapping when metadata is contradictory or ambiguous.
10. Do not silently fall back to “newest images” after a Pixel-Exact batch has already been staged. Resume from the durable staged copies.
11. Do not auto-process anything merely because a new manifest was imported. Resume is prepared automatically, but an explicit user click on **Main Image** is still the write trigger.
12. Do not weaken existing recovery/session logic.
13. Do not suppress or bypass existing asset validation.
14. Do not treat a warm local test run as final verification. Follow `AGENTS.md`.
15. Keep new source files comfortably below the repository's practical file-size limits. Prefer focused partial-class/service files.
16. First implement pure parser/state services and their tests; only then wire UI/orchestration.
17. At every phase, keep the application compiling.
18. Do not add a new external NuGet dependency for this feature.

---

# 1. What problem this feature solves

The existing tool has a useful **Variants** workflow:

- one active queue prompt,
- N downloaded images,
- N assets derived from one base asset name,
- asset suffixes `A`, `B`, `C`, ...
- one queue Request becomes `Done` only after the entire variants batch succeeds.

That is **not** the desired workflow for pixel-exact scene phases.

A Pixel-Exact series is structurally different:

1. Phase 1 is generated first and accepted as the master/reference image.
2. The tool processes that phase normally.
3. The queue advances to one collection prompt such as `Ref4`.
4. That prompt generates the remaining four states of the exact same scene.
5. The user downloads those four images.
6. Pixel-Exact mode consumes the four images **oldest-first**.
7. Output 1 is committed against the active `Ref4` queue row.
8. The tool then switches to the next matching `AusRef4` queue row and commits output 2.
9. This repeats until all four output rows are durably committed and individually marked `Done`.
10. No `A/B/C/D` naming is used. Every row already has its own final filename/asset name.

Example 5-phase set:

```text
Queue row 1: Phase 1 / master       -> PROZESSMARKER: Einzeln
Queue row 2: Phase 2 / collection   -> PROZESSMARKER: Ref4
Queue row 3: Phase 3                -> PROZESSMARKER: AusRef4
Queue row 4: Phase 4                -> PROZESSMARKER: AusRef4
Queue row 5: Phase 5                -> PROZESSMARKER: AusRef4
```

The new Pixel-Exact dropdown therefore represents the number of images that the **current collection prompt produces and that the tool will consume now**:

```text
SERIENGROESSE=5
NEXT=Ref4
Ref4
=> Pixel phases dropdown = 4
```

This definition is binding. Do **not** make the dropdown show 5 and then subtract one internally.

---

# 2. Verified current repository behavior

The implementation must be based on the current code, not an imagined older version.

## 2.1 Relevant current files

```text
src/AssetProvenanceHelper/
  AppConstants.cs
  MainForm.cs
  MainForm.Designer.cs
  MainForm.Layout.cs
  MainForm.MainWorkflow.cs
  MainForm.DirectMode.cs
  MainForm.ReferenceWorkflow.cs
  MainForm.RequestQueue.cs
  MainForm.Variants.cs
  Program.cs

  Models/
    AppSettings.cs
    AssetRequestItem.cs
    AssetSession.cs

  Services/
    AppBootstrap.cs
    AssetRequestManifestService.cs
    GeneratedImageStagingService.cs
    ImageFinderService.cs
    RequestProgressService.cs
    RequestQueueStateService.cs

  examples/
    asset_request_conversion_prompt.txt
    asset_request_manifest_template.json

tests/AssetProvenanceHelper.Tests/
  FeatureV14VariantsAndKeepSettingsTests.cs
  RequestQueuePersistenceUiTests.cs
  RequestQueueStateServiceTests.cs
  TestWorkspace.cs
  ...
```

## 2.2 Existing Variants behavior that must remain intact

Current `MainForm.Variants.cs`:

- `cmbVariants.SelectedIndex` encodes the number:
  - index `0` = `none`
  - indices `1..10` = variant count.
- `TryResolveVariantMainImages(N)` chooses the N newest accepted images and reverses them so processing order is oldest-first.
- `HandleVariantBatch(N)` uses **one active prompt** for all images.
- target asset names are generated using `AssetNaming.BuildVariantAssetName(...)`.
- one queue request is completed only after full batch success.
- partial success leaves that one Request Pending.
- reference-assisted variants replicate the reference into every variant asset folder.

Do not change those semantics except for queue-driven **automatic count selection**.

## 2.3 Existing queue behavior that matters

Current queue activation:

- assigns `_activeRequest`,
- assigns `txtAssetFolderName`,
- assigns `txtPrompt`,
- copies the prompt to clipboard,
- may load a staged API candidate,
- blocks switching away from an unrelated active reference session.

Current completion:

- obtains the active request key,
- updates API generation job state if applicable,
- sets the queue item `IsCompleted = true`,
- saves request progress,
- refreshes the queue,
- clears `_activeRequest`.

There is currently **no queue auto-advance**.

## 2.4 Existing manifest format must stay v2

The manifest importer:

- only accepts manifest versions 1 and 2,
- uses `JsonUnmappedMemberHandling.Disallow`,
- rejects unknown asset properties,
- computes the v2 Request key from:
  - filename
  - normalized resolution
  - alpha
  - complete prompt
- computes manifest fingerprint from request keys.

Therefore adding `seriesId`, `phase`, `workflow`, etc. as JSON sibling properties would force a broad v3 migration across importer, fingerprints, persisted queues, tests, API generation and docs.

That is unnecessary.

**Decision:** workflow metadata remains part of `prompt`.

## 2.5 Existing cross-manifest limitation

The active queue contains exactly one imported manifest.

`RequestProgressService` is keyed to the current manifest fingerprint.

The current queue is replaced when another manifest is imported.

However the real asset request data contains series that can cross file boundaries, for example:

- gas-station series: Teil 01 -> Teil 02
- motel series: Teil 02 -> Teil 03
- school/center series: Teil 03 -> Teil 04

Therefore Pixel-Exact needs a **separate durable series/batch journal**. It must not rely on the current queue remaining loaded for the entire series.

---

# 3. Final user-visible behavior

## 3.1 New checkbox

Add to **Current Asset** mode row:

```text
[ ] Pixel-exact mode
```

Control name:

```csharp
chkPixelExact
```

Pixel-Exact is explicitly opt-in. Queue metadata may configure its count, but **must not automatically turn the checkbox on**.

## 3.2 New dropdown

Visible only while Pixel-Exact is enabled:

```text
Pixel phases [4 v]
```

Controls:

```csharp
lblPixelExactCount
cmbPixelExactCount
```

Allowed values:

```text
1
2
3
...
10
```

Constant:

```csharp
public const int MaxPixelExactOutputCount = 10;
```

The value means:

> number of non-master phase images produced by the current collection prompt and consumed by this Pixel-Exact batch.

For `Ref4`, show `4`.

## 3.3 Automatic Pixel-Exact count

On queue activation:

- `Ref4` -> automatically select 4.
- `AusRef4` -> select 4.
- canonical Pixel-Exact metadata with `BUNDLE=4` -> select 4.
- seed metadata with `NEXT=Ref4` or `BUNDLE=4` -> select 4.
- if no machine-readable value is found, leave the user's current manual Pixel-Exact count unchanged.

If explicit metadata is found but contradictory, show a non-destructive warning/status and refuse Pixel-Exact execution until the prompt is corrected.

## 3.4 Automatic Variants count

On queue activation:

Priority:

1. canonical metadata `MODE=VARIANTS; COUNT=N`
2. `PROZESSMARKER: VariantenN`
3. tightly constrained legacy prompt fallback:
   - `Erzeuge exakt N ... Varianten`
   - `Generate exactly N ... variants`

When a variants count is recognized:

```text
cmbVariants.SelectedIndex = N
```

When the prompt is explicitly recognized as a non-Variants workflow (`Einzeln`, `RefN`, `AusRefN`, canonical Pixel-Exact/Single):

```text
cmbVariants.SelectedIndex = 0
```

When the prompt is unknown:

- do not overwrite a manually selected variants count.

Manual user adjustment after activation remains possible.

## 3.5 Mutual exclusion

Pixel-Exact and Variants must never execute together.

Rules:

- checking Pixel-Exact:
  - set Variants to `none`,
  - disable Variants dropdown while Pixel-Exact remains checked.
- manually selecting Variants > 0:
  - uncheck Pixel-Exact.
- auto-detecting a Variants prompt:
  - set the variants count,
  - uncheck Pixel-Exact if it was checked.
- activating a Pixel-Exact prompt:
  - set Variants to `none`,
  - do **not** automatically check Pixel-Exact.

This is intentionally asymmetric: Pixel-Exact remains explicit opt-in.

## 3.6 Direct mode interaction

While Pixel-Exact is checked:

- disable `chkDirectMode`,
- force Direct mode off.

Reason: Pixel-Exact already owns multi-image selection/order. Letting Direct mode also select image pairs/batches would create two competing resolvers.

Do not delete Direct mode logic. It resumes normally after Pixel-Exact is unchecked.

## 3.7 API Candidate interaction

V1 Pixel-Exact is for the download-folder workflow.

If `_activeApiCandidateMetadata != null`:

- Pixel-Exact execution is blocked.
- show:
  > Pixel-Exact mode currently processes ordered images from the Image Download Folder and cannot be combined with a staged API Candidate. Commit/unload the API Candidate or turn Pixel-Exact off.

Do not silently unload a staged API candidate.

---

# 4. Canonical prompt metadata contract

The existing markers are useful but insufficient for safe cross-manifest continuation because `Ref4` alone does not identify which `AusRef4` rows belong to which series.

Add one canonical machine-readable line inside the prompt.

## 4.1 General grammar

Exactly one optional line:

```text
PROZESSMETA: KEY=VALUE; KEY=VALUE; KEY=VALUE
```

Rules:

- ASCII keys.
- `;` separates key/value pairs.
- exactly one `=` per pair.
- keys are case-insensitive when parsing.
- values are trimmed.
- unknown keys are ignored by the current parser and must **not** affect behavior; the original prompt text itself remains unchanged.
- duplicate recognized keys are invalid.
- multiple `PROZESSMETA:` lines are invalid.
- canonical metadata wins over legacy inference, but canonical/legacy contradictions are invalid.

## 4.2 Stable series ID

Pixel-Exact requires:

```text
SERIE=<stable-id>
```

Constraints:

```text
[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}
```

Examples:

```text
gas_station_daycycle_001
motel_room_window_weather_014
school_hallway_lightcycle_007
```

The ID must remain identical across all rows of one scene series, even if the series crosses source JSON files.

## 4.3 Seed/master prompt

Example:

```text
...full generation prompt...

PROZESSMETA: MODE=PIXEL_EXACT; SERIE=gas_station_daycycle_001; ROLLE=SEED; PHASE=1; GESAMT=5; BUNDLE=4
SERIENGROESSE=5; NEXT=Ref4. PROZESSMARKER: Einzeln
```

Required canonical keys:

```text
MODE=PIXEL_EXACT
SERIE=<id>
ROLLE=SEED
PHASE=1
GESAMT=<total>
BUNDLE=<total-1>
```

Legacy line is retained for human readability/backward compatibility.

## 4.4 Collection prompt / first generated output

Example:

```text
...one prompt that requests all four remaining exact-scene states...

PROZESSMETA: MODE=PIXEL_EXACT; SERIE=gas_station_daycycle_001; ROLLE=REF; PHASE=2; GESAMT=5; BUNDLE=4; OUTPUT=1
PROZESSMARKER: Ref4
```

Required:

```text
ROLLE=REF
OUTPUT=1
PHASE=2
BUNDLE=4
GESAMT=5
```

This queue row is the target asset for the first generated image after the master.

## 4.5 Remaining output rows

Phase 3:

```text
PROZESSMETA: MODE=PIXEL_EXACT; SERIE=gas_station_daycycle_001; ROLLE=AUSREF; PHASE=3; GESAMT=5; BUNDLE=4; OUTPUT=2
PROZESSMARKER: AusRef4
```

Phase 4:

```text
PROZESSMETA: MODE=PIXEL_EXACT; SERIE=gas_station_daycycle_001; ROLLE=AUSREF; PHASE=4; GESAMT=5; BUNDLE=4; OUTPUT=3
PROZESSMARKER: AusRef4
```

Phase 5:

```text
PROZESSMETA: MODE=PIXEL_EXACT; SERIE=gas_station_daycycle_001; ROLLE=AUSREF; PHASE=5; GESAMT=5; BUNDLE=4; OUTPUT=4
PROZESSMARKER: AusRef4
```

Binding invariant:

```text
PHASE == OUTPUT + 1
GESAMT == BUNDLE + 1
OUTPUT in 1..BUNDLE
```

For `ROLLE=REF`:

```text
OUTPUT == 1
```

For `ROLLE=AUSREF`:

```text
OUTPUT >= 2
```

## 4.6 Variants prompt

Canonical example:

```text
...full prompt...

PROZESSMETA: MODE=VARIANTS; COUNT=2
PROZESSMARKER: Varianten2
```

## 4.7 Normal single prompt

Optional canonical metadata:

```text
PROZESSMETA: MODE=SINGLE
PROZESSMARKER: Einzeln
```

Do not require all existing normal prompts to be migrated.

---

# 5. Backward compatibility policy

Existing documents may only contain legacy text such as:

```text
SERIENGROESSE=5; NEXT=Ref4. PROZESSMARKER: Einzeln
```

or:

```text
PROZESSMARKER: Ref4
```

or:

```text
PROZESSMARKER: AusRef4
```

Support them as follows.

## 5.1 Legacy count detection

Supported:

- `RefN` -> Pixel count N.
- `AusRefN` -> Pixel count N.
- seed `NEXT=RefN` -> Pixel count N.
- old variants prose -> variants N.

## 5.2 Legacy same-manifest Pixel-Exact processing

Allowed only if all N target rows can be proven locally:

- active row is `RefN`,
- it is output 1,
- the next N-1 pending rows are contiguous,
- each next row is `AusRefN`,
- no contradictory `SERIENGROESSE`/`NEXT` appears.

Process them in queue order.

## 5.3 Legacy cross-manifest processing

Do **not** guess.

If the current manifest ends before all N outputs are mapped and no canonical `SERIE/OUTPUT` metadata exists:

- stage nothing,
- commit nothing,
- show:
  > This legacy Pixel-Exact series crosses a manifest boundary, but its prompts do not contain stable SERIE/OUTPUT metadata. Add canonical PROZESSMETA metadata before processing so phases cannot be assigned to the wrong assets.

This prevents a catastrophic wrong-series match when several `Ref4/AusRef4` groups exist.

---

# 6. New model and service files

Create:

```text
src/AssetProvenanceHelper/Models/QueuePromptWorkflowMetadata.cs
src/AssetProvenanceHelper/Models/PixelExactBatchState.cs
src/AssetProvenanceHelper/Services/QueuePromptWorkflowParser.cs
src/AssetProvenanceHelper/Services/PixelExactBatchStateService.cs
src/AssetProvenanceHelper/MainForm.PixelExact.cs
src/AssetProvenanceHelper/MainForm.QueueWorkflowDetection.cs
```

Tests:

```text
tests/AssetProvenanceHelper.Tests/QueuePromptWorkflowParserTests.cs
tests/AssetProvenanceHelper.Tests/PixelExactBatchStateServiceTests.cs
tests/AssetProvenanceHelper.Tests/PixelExactWorkflowTests.cs
tests/AssetProvenanceHelper.Tests/QueueWorkflowAutoDetectionTests.cs
```

Do not put the entire feature into `MainForm.cs`.

---

# 7. Copy-ready model: QueuePromptWorkflowMetadata.cs

Create this file.

```csharp
namespace AssetProvenanceHelper.Models;

public enum QueuePromptWorkflowKind
{
    Unknown = 0,
    Single = 1,
    Variants = 2,
    PixelExactSeed = 3,
    PixelExactRef = 4,
    PixelExactOutput = 5,
    Invalid = 6
}

public sealed record QueuePromptWorkflowMetadata
{
    public QueuePromptWorkflowKind Kind { get; init; }

    public bool HasCanonicalMetadata { get; init; }

    public string? SeriesId { get; init; }

    public int? VariantCount { get; init; }

    /// <summary>
    /// Number of non-master images generated by the collection prompt.
    /// Ref4 => 4.
    /// </summary>
    public int? PixelOutputCount { get; init; }

    public int? TotalPhases { get; init; }

    public int? Phase { get; init; }

    public int? OutputIndex { get; init; }

    public string? LegacyMarker { get; init; }

    public IReadOnlyList<string> Errors { get; init; } =
        Array.Empty<string>();

    public bool IsValid =>
        Kind != QueuePromptWorkflowKind.Invalid
        && Errors.Count == 0;

    public bool IsPixelExact =>
        Kind is QueuePromptWorkflowKind.PixelExactSeed
            or QueuePromptWorkflowKind.PixelExactRef
            or QueuePromptWorkflowKind.PixelExactOutput;
}
```

---

# 8. Copy-ready parser: QueuePromptWorkflowParser.cs

Create the parser as a pure service. Do not access WinForms from this class.

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class QueuePromptWorkflowParser
{
    private static readonly Regex MetaLineRegex = new(
        @"(?im)^[ \t]*PROZESSMETA:[ \t]*(?<body>[^\r\n]*)[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarkerRegex = new(
        @"(?i)\bPROZESSMARKER\s*:\s*(?<marker>Einzeln|Ref(?<ref>[0-9]+)|AusRef(?<ausref>[0-9]+)|Varianten(?<variants>[0-9]+))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SeriesSizeRegex = new(
        @"(?i)\bSERIENGROESSE\s*=\s*(?<count>[0-9]+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NextRefRegex = new(
        @"(?i)\bNEXT\s*=\s*Ref(?<count>[0-9]+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Deliberately anchored close to prompt start. Do not match arbitrary prose
    // merely mentioning "2 variants".
    private static readonly Regex LegacyGermanVariantsRegex = new(
        @"(?is)\A\s*Erzeuge\s+exakt\s+(?<count>[0-9]+)\b.{0,120}?\bVarianten\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyEnglishVariantsRegex = new(
        @"(?is)\A\s*Generate\s+exactly\s+(?<count>[0-9]+)\b.{0,120}?\bvariants\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SeriesIdRegex = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public QueuePromptWorkflowMetadata Parse(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Unknown();
        }

        var errors = new List<string>();
        var metaMatches = MetaLineRegex.Matches(prompt);

        if (metaMatches.Count > 1)
        {
            return Invalid("Prompt contains more than one PROZESSMETA line.");
        }

        Dictionary<string, string>? canonical = null;
        if (metaMatches.Count == 1)
        {
            canonical = ParseCanonicalPairs(
                metaMatches[0].Groups["body"].Value,
                errors);

            if (errors.Count > 0)
            {
                return Invalid(errors);
            }
        }

        var markerMatches = MarkerRegex.Matches(prompt);
        if (markerMatches.Count > 1)
        {
            var distinctMarkers = markerMatches
                .Select(m => m.Groups["marker"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (distinctMarkers.Length > 1)
            {
                return Invalid(
                    "Prompt contains contradictory PROZESSMARKER values: "
                    + string.Join(", ", distinctMarkers));
            }
        }

        var marker = markerMatches.Count > 0
            ? markerMatches[0]
            : null;

        int? markerRef = ParseOptionalGroup(marker, "ref");
        int? markerAusRef = ParseOptionalGroup(marker, "ausref");
        int? markerVariants = ParseOptionalGroup(marker, "variants");

        int? seriesSize = ParseSingleIntMatch(
            SeriesSizeRegex,
            prompt,
            "count",
            errors,
            "SERIENGROESSE");

        int? nextRef = ParseSingleIntMatch(
            NextRefRegex,
            prompt,
            "count",
            errors,
            "NEXT=RefN");

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        if (canonical is not null)
        {
            return ParseCanonical(
                canonical,
                marker?.Groups["marker"].Value,
                markerRef,
                markerAusRef,
                markerVariants,
                seriesSize,
                nextRef);
        }

        if (markerVariants is not null)
        {
            if (!InRange(markerVariants.Value))
            {
                return Invalid("Varianten count is outside 1..10.");
            }

            return new QueuePromptWorkflowMetadata
            {
                Kind = QueuePromptWorkflowKind.Variants,
                VariantCount = markerVariants,
                LegacyMarker = marker!.Groups["marker"].Value
            };
        }

        if (markerRef is not null)
        {
            if (!InRange(markerRef.Value))
            {
                return Invalid("Ref count is outside 1..10.");
            }

            return new QueuePromptWorkflowMetadata
            {
                Kind = QueuePromptWorkflowKind.PixelExactRef,
                PixelOutputCount = markerRef,
                LegacyMarker = marker!.Groups["marker"].Value
            };
        }

        if (markerAusRef is not null)
        {
            if (!InRange(markerAusRef.Value))
            {
                return Invalid("AusRef count is outside 1..10.");
            }

            return new QueuePromptWorkflowMetadata
            {
                Kind = QueuePromptWorkflowKind.PixelExactOutput,
                PixelOutputCount = markerAusRef,
                LegacyMarker = marker!.Groups["marker"].Value
            };
        }

        if (marker is not null
            && marker.Groups["marker"].Value.Equals(
                "Einzeln",
                StringComparison.OrdinalIgnoreCase))
        {
            if (nextRef is not null)
            {
                if (!InRange(nextRef.Value))
                {
                    return Invalid("NEXT Ref count is outside 1..10.");
                }

                if (seriesSize is not null
                    && seriesSize.Value != nextRef.Value + 1)
                {
                    return Invalid(
                        "Legacy seed metadata is inconsistent: "
                        + "SERIENGROESSE must equal NEXT Ref count + 1.");
                }

                return new QueuePromptWorkflowMetadata
                {
                    Kind = QueuePromptWorkflowKind.PixelExactSeed,
                    PixelOutputCount = nextRef,
                    TotalPhases = seriesSize,
                    Phase = 1,
                    LegacyMarker = "Einzeln"
                };
            }

            return new QueuePromptWorkflowMetadata
            {
                Kind = QueuePromptWorkflowKind.Single,
                LegacyMarker = "Einzeln"
            };
        }

        var legacyVariants = TryParseLegacyVariants(prompt);
        if (legacyVariants is not null)
        {
            return new QueuePromptWorkflowMetadata
            {
                Kind = QueuePromptWorkflowKind.Variants,
                VariantCount = legacyVariants
            };
        }

        return Unknown();
    }

    private static QueuePromptWorkflowMetadata ParseCanonical(
        IReadOnlyDictionary<string, string> values,
        string? legacyMarker,
        int? markerRef,
        int? markerAusRef,
        int? markerVariants,
        int? legacySeriesSize,
        int? legacyNextRef)
    {
        var errors = new List<string>();

        if (!values.TryGetValue("MODE", out var modeRaw))
        {
            return Invalid("PROZESSMETA is missing MODE.");
        }

        var mode = modeRaw.Trim().ToUpperInvariant();

        if (mode == "VARIANTS")
        {
            var count = RequiredInt(values, "COUNT", errors);
            if (count is not null && !InRange(count.Value))
            {
                errors.Add("COUNT is outside 1..10.");
            }

            if (markerVariants is not null
                && count is not null
                && markerVariants != count)
            {
                errors.Add("PROZESSMETA COUNT contradicts PROZESSMARKER VariantenN.");
            }

            if (legacyMarker is not null
                && markerVariants is null)
            {
                errors.Add(
                    "MODE=VARIANTS requires PROZESSMARKER: VariantenN when a "
                    + "legacy marker is present.");
            }

            if (markerRef is not null || markerAusRef is not null)
            {
                errors.Add("VARIANTS metadata contradicts Ref/AusRef marker.");
            }

            return errors.Count > 0
                ? Invalid(errors)
                : new QueuePromptWorkflowMetadata
                {
                    Kind = QueuePromptWorkflowKind.Variants,
                    HasCanonicalMetadata = true,
                    VariantCount = count,
                    LegacyMarker = legacyMarker
                };
        }

        if (mode == "SINGLE")
        {
            if (markerRef is not null
                || markerAusRef is not null
                || markerVariants is not null)
            {
                errors.Add("SINGLE metadata contradicts PROZESSMARKER.");
            }

            return errors.Count > 0
                ? Invalid(errors)
                : new QueuePromptWorkflowMetadata
                {
                    Kind = QueuePromptWorkflowKind.Single,
                    HasCanonicalMetadata = true,
                    LegacyMarker = legacyMarker
                };
        }

        if (mode != "PIXEL_EXACT")
        {
            return Invalid($"Unsupported PROZESSMETA MODE '{modeRaw}'.");
        }

        var seriesId = RequiredString(values, "SERIE", errors);
        if (seriesId is not null && !SeriesIdRegex.IsMatch(seriesId))
        {
            errors.Add(
                "SERIE must match [A-Za-z0-9][A-Za-z0-9._-]{0,127}.");
        }

        var role = RequiredString(values, "ROLLE", errors)?
            .ToUpperInvariant();
        var phase = RequiredInt(values, "PHASE", errors);
        var total = RequiredInt(values, "GESAMT", errors);
        var bundle = RequiredInt(values, "BUNDLE", errors);

        if (bundle is not null && !InRange(bundle.Value))
        {
            errors.Add("BUNDLE is outside 1..10.");
        }

        if (total is not null
            && bundle is not null
            && total != bundle + 1)
        {
            errors.Add("GESAMT must equal BUNDLE + 1.");
        }

        if (legacySeriesSize is not null
            && total is not null
            && legacySeriesSize != total)
        {
            errors.Add("GESAMT contradicts SERIENGROESSE.");
        }

        QueuePromptWorkflowKind kind;
        int? output = null;

        switch (role)
        {
            case "SEED":
                kind = QueuePromptWorkflowKind.PixelExactSeed;

                if (legacyMarker is not null
                    && !legacyMarker.Equals(
                        "Einzeln",
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "ROLLE=SEED requires PROZESSMARKER: Einzeln when a "
                        + "legacy marker is present.");
                }

                if (phase != 1)
                {
                    errors.Add("SEED must use PHASE=1.");
                }

                if (legacyNextRef is not null
                    && bundle is not null
                    && legacyNextRef != bundle)
                {
                    errors.Add("BUNDLE contradicts NEXT=RefN.");
                }

                if (markerRef is not null
                    || markerAusRef is not null
                    || markerVariants is not null)
                {
                    errors.Add("SEED contradicts its PROZESSMARKER.");
                }
                break;

            case "REF":
                kind = QueuePromptWorkflowKind.PixelExactRef;
                output = RequiredInt(values, "OUTPUT", errors);

                if (legacyMarker is not null
                    && markerRef is null)
                {
                    errors.Add(
                        "ROLLE=REF requires PROZESSMARKER: RefN when a "
                        + "legacy marker is present.");
                }

                if (output != 1)
                {
                    errors.Add("ROLLE=REF must use OUTPUT=1.");
                }

                if (phase != 2)
                {
                    errors.Add("ROLLE=REF must use PHASE=2.");
                }

                if (markerRef is not null
                    && bundle is not null
                    && markerRef != bundle)
                {
                    errors.Add("BUNDLE contradicts PROZESSMARKER RefN.");
                }

                if (markerAusRef is not null || markerVariants is not null)
                {
                    errors.Add("ROLLE=REF contradicts its PROZESSMARKER.");
                }
                break;

            case "AUSREF":
                kind = QueuePromptWorkflowKind.PixelExactOutput;
                output = RequiredInt(values, "OUTPUT", errors);

                if (legacyMarker is not null
                    && markerAusRef is null)
                {
                    errors.Add(
                        "ROLLE=AUSREF requires PROZESSMARKER: AusRefN when a "
                        + "legacy marker is present.");
                }

                if (output is not null && output < 2)
                {
                    errors.Add("ROLLE=AUSREF must use OUTPUT>=2.");
                }

                if (output is not null
                    && phase is not null
                    && phase != output + 1)
                {
                    errors.Add("PHASE must equal OUTPUT + 1.");
                }

                if (output is not null
                    && bundle is not null
                    && output > bundle)
                {
                    errors.Add("OUTPUT is larger than BUNDLE.");
                }

                if (markerAusRef is not null
                    && bundle is not null
                    && markerAusRef != bundle)
                {
                    errors.Add("BUNDLE contradicts PROZESSMARKER AusRefN.");
                }

                if (markerRef is not null || markerVariants is not null)
                {
                    errors.Add("ROLLE=AUSREF contradicts its PROZESSMARKER.");
                }
                break;

            default:
                return Invalid(
                    $"Unsupported PIXEL_EXACT ROLLE '{role ?? "(missing)"}'.");
        }

        return errors.Count > 0
            ? Invalid(errors)
            : new QueuePromptWorkflowMetadata
            {
                Kind = kind,
                HasCanonicalMetadata = true,
                SeriesId = seriesId,
                PixelOutputCount = bundle,
                TotalPhases = total,
                Phase = phase,
                OutputIndex = output,
                LegacyMarker = legacyMarker
            };
    }

    private static Dictionary<string, string> ParseCanonicalPairs(
        string body,
        ICollection<string> errors)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var rawPart in body.Split(';'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            var equals = part.IndexOf('=');
            if (equals <= 0 || equals == part.Length - 1)
            {
                errors.Add($"Invalid PROZESSMETA pair '{part}'.");
                continue;
            }

            if (part.IndexOf('=', equals + 1) >= 0)
            {
                errors.Add($"PROZESSMETA pair contains multiple '=': '{part}'.");
                continue;
            }

            var key = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim();

            if (!result.TryAdd(key, value))
            {
                errors.Add($"Duplicate PROZESSMETA key '{key}'.");
            }
        }

        return result;
    }

    private static int? ParseSingleIntMatch(
        Regex regex,
        string prompt,
        string group,
        ICollection<string> errors,
        string label)
    {
        var matches = regex.Matches(prompt);
        if (matches.Count == 0)
        {
            return null;
        }

        var parsed = matches
            .Select(m => ParseInt(m.Groups[group].Value))
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .Distinct()
            .ToArray();

        if (parsed.Length != 1)
        {
            errors.Add($"Prompt contains contradictory {label} values.");
            return null;
        }

        return parsed[0];
    }

    private static int? ParseOptionalGroup(Match? match, string group)
    {
        if (match is null || !match.Groups[group].Success)
        {
            return null;
        }

        return ParseInt(match.Groups[group].Value);
    }

    private static int? TryParseLegacyVariants(string prompt)
    {
        foreach (var regex in new[]
                 {
                     LegacyGermanVariantsRegex,
                     LegacyEnglishVariantsRegex
                 })
        {
            var match = regex.Match(prompt);
            if (!match.Success)
            {
                continue;
            }

            var count = ParseInt(match.Groups["count"].Value);
            if (count is not null && InRange(count.Value))
            {
                return count;
            }
        }

        return null;
    }

    private static int? RequiredInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        ICollection<string> errors)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            errors.Add($"PROZESSMETA is missing {key}.");
            return null;
        }

        var parsed = ParseInt(raw);
        if (parsed is null)
        {
            errors.Add($"PROZESSMETA {key} is not a valid integer.");
        }

        return parsed;
    }

    private static string? RequiredString(
        IReadOnlyDictionary<string, string> values,
        string key,
        ICollection<string> errors)
    {
        if (!values.TryGetValue(key, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"PROZESSMETA is missing {key}.");
            return null;
        }

        return value.Trim();
    }

    private static int? ParseInt(string value) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

    private static bool InRange(int value) =>
        value is >= 1 and <= AppConstants.MaxPixelExactOutputCount;

    private static QueuePromptWorkflowMetadata Unknown() =>
        new()
        {
            Kind = QueuePromptWorkflowKind.Unknown
        };

    private static QueuePromptWorkflowMetadata Invalid(
        params string[] errors) =>
        Invalid((IReadOnlyList<string>)errors);

    private static QueuePromptWorkflowMetadata Invalid(
        IReadOnlyList<string> errors) =>
        new()
        {
            Kind = QueuePromptWorkflowKind.Invalid,
            Errors = errors.ToArray()
        };
}
```

### Parser implementation note

The weak model must not “improve” this by scanning arbitrary numbers from the prompt.

The entire point of conservative parsing is to avoid interpreting prose such as:

```text
five lamps
phase shift 2
4 doors
two variants of lighting are visible in-scene
```

as workflow instructions.

---

# 9. Pixel-Exact durable state model

Pixel-Exact needs state beyond the current manifest because a series can cross manifest files and because the selected downloaded images must remain bound to their output indices across restart/failure.

Create:

```text
Models/PixelExactBatchState.cs
```

Recommended copy-ready shape:

```csharp
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Models;

public enum PixelExactOutputCommitState
{
    Staged = 0,
    CommitInProgress = 1,
    AssetCommitted = 2,
    QueueCompleted = 3
}

public sealed class PixelExactStagedOutput
{
    public int OutputIndex { get; set; }

    public int Phase { get; set; }

    public string OriginalSourcePath { get; set; } = string.Empty;

    public string StagedPath { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public PixelExactOutputCommitState State { get; set; } =
        PixelExactOutputCommitState.Staged;

    public string? ManifestFingerprint { get; set; }

    public string? RequestKey { get; set; }

    public string? AssetName { get; set; }

    public string? AssetFolderPath { get; set; }

    public DateTimeOffset? AssetCommittedAtUtc { get; set; }
}

public sealed class PixelExactBatchState
{
    public int SchemaVersion { get; set; } = 1;

    public string SeriesId { get; set; } = string.Empty;

    public int TotalPhases { get; set; }

    public int BundleCount { get; set; }

    /// <summary>
    /// True only when SERIE/OUTPUT identity came from canonical PROZESSMETA.
    /// False means this state is a same-manifest manual/legacy batch and MUST
    /// NOT be resumed across a manifest boundary by guessing.
    /// </summary>
    public bool HasCanonicalSeriesIdentity { get; set; }

    /// <summary>
    /// The seed/master has been committed and recorded.
    /// </summary>
    public bool SeedCommitted { get; set; }

    public string? SeedManifestFingerprint { get; set; }

    public string? SeedRequestKey { get; set; }

    public string? MasterAssetName { get; set; }

    public string? MasterReferencePath { get; set; }

    public string? MasterReferenceSha256 { get; set; }

    public DateTimeOffset? MasterProcessedAt { get; set; }

    public ProviderTemplateSnapshot? MasterProviderTemplate { get; set; }

    /// <summary>
    /// Timestamp used for all outputs staged in one collection response.
    /// </summary>
    public DateTimeOffset? BundleProcessedAt { get; set; }

    public List<PixelExactStagedOutput> Outputs { get; set; } = new();

    public bool Completed { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } =
        DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } =
        DateTimeOffset.UtcNow;
}
```

The verified current `ProviderTemplateSnapshot` has public get/set properties
(`FileName`, `DisplayName`, `ContentSha256`, `Content`) and is directly suitable
for `System.Text.Json` persistence. Use the existing model; do not invent a
second provider snapshot type and do not serialize service instances.

---

# 10. PixelExactBatchStateService responsibilities

Create:

```text
Services/PixelExactBatchStateService.cs
```

State location:

```text
%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper\pixel-exact-batch-state.json
```

Staging location:

```text
%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper\pixel-exact\<series-id>\<batch-id>\
```

Add constants:

```csharp
public const string PixelExactBatchStateFileName =
    "pixel-exact-batch-state.json";

public const string PixelExactStagingFolderName =
    "pixel-exact";

public const int MaxPixelExactOutputCount = 10;
```

## 10.1 Required service API

Implement approximately:

```csharp
public sealed class PixelExactBatchStateService
{
    public PixelExactBatchStateService(
        string statePath,
        string stagingRoot);

    public string StatePath { get; }

    public string StagingRoot { get; }

    public bool HasPendingState { get; }

    public PixelExactBatchState? Load();

    public void Save(PixelExactBatchState state);

    public PixelExactBatchState CreateSeedState(
        QueuePromptWorkflowMetadata metadata,
        string manifestFingerprint,
        AssetRequestItem seedRequest,
        AssetSession committedSeedSession,
        string committedSeedFilename,
        DateTimeOffset processedAt);

    public PixelExactBatchState CreateCollectionState(
        QueuePromptWorkflowMetadata metadata,
        AssetRequestManifest manifest,
        AssetRequestItem activeRefRequest);

    public PixelExactBatchState CreateManualLocalCollectionState(
        AssetRequestManifest manifest,
        AssetRequestItem activeRequest,
        int outputCount);

    public PixelExactBatchState StageBundle(
        PixelExactBatchState state,
        IReadOnlyList<string> orderedSourceImages,
        DateTimeOffset processedAt);

    public void ValidateStagedAuthority(
        PixelExactBatchState state);

    public void ClearCompletedState();

    public void DiscardPendingState();
}
```

## 10.2 Atomic write rules

Copy the repository's existing durable-write style.

Every JSON state write:

1. serialize to temp file in same directory,
2. use UTF-8 without BOM,
3. flush writer,
4. `stream.Flush(true)`,
5. move/replace final path,
6. best-effort delete temp in `finally`.

Do not write directly to the final state file.

## 10.3 Staging rules

When a RefN batch starts:

1. resolve exactly N candidate images,
2. validate all files first,
3. order oldest-first,
4. create a unique staging directory,
5. copy each source non-destructively,
6. calculate SHA-256 from staged bytes,
7. create output mapping:
   - staged output 1 -> phase 2
   - output 2 -> phase 3
   - ...
8. save state only after all copies succeed.
9. if staging fails, delete the newly created staging directory and leave the prior durable seed state intact.

Never move/delete the user's download.

## 10.4 Resume validation

Before any staged output is used:

- staged file exists,
- extension remains accepted,
- SHA-256 matches state,
- output indices are unique and exactly `1..BundleCount`,
- phases are exactly `OutputIndex + 1`,
- no staged path escapes `StagingRoot`.

If any check fails:

- fail closed,
- do not pick a replacement from Downloads,
- do not advance queue,
- show a recoverable error.

## 10.5 Completion-state cleanup

Safe cleanup order:

1. set `Completed = true`,
2. atomically save state,
3. delete staging directory best-effort,
4. delete state file.

On startup, if a state loads with `Completed=true`:

- finish cleanup,
- return no pending batch.

This avoids turning a crash during cleanup into a false “missing staged image” error.

---

# 11. Bootstrap wiring

Modify:

```text
AppConstants.cs
Services/AppBootstrap.cs
Program.cs
MainForm.cs
tests/.../TestWorkspace.cs
```

## 11.1 AppBootstrapContext

Add:

```csharp
public required string PixelExactBatchStatePath { get; init; }

public required string PixelExactStagingPath { get; init; }

public required PixelExactBatchStateService PixelExactBatchStateService
{
    get;
    init;
}
```

Add path helpers:

```csharp
public static string GetPixelExactBatchStatePath(
    string stateDirectory) =>
    Path.Combine(
        stateDirectory,
        AppConstants.PixelExactBatchStateFileName);

public static string GetPixelExactStagingPath(
    string stateDirectory) =>
    Path.Combine(
        stateDirectory,
        AppConstants.PixelExactStagingFolderName);
```

Create the service in `CreateContext`.

## 11.2 MainForm constructor

Append the new optional dependency at the **end** of the parameter list to minimize test breakage:

```csharp
PixelExactBatchStateService? pixelExactBatchStateService = null
```

Field:

```csharp
private readonly PixelExactBatchStateService _pixelExactBatchStateService;
```

Assignment:

```csharp
_pixelExactBatchStateService =
    pixelExactBatchStateService
    ?? new PixelExactBatchStateService(
        AppBootstrap.GetPixelExactBatchStatePath(
            AppBootstrap.GetStateDirectory()),
        AppBootstrap.GetPixelExactStagingPath(
            AppBootstrap.GetStateDirectory()));
```

Do not insert it in the middle of existing optional constructor arguments.

## 11.3 Program.cs

Pass:

```csharp
context.PixelExactBatchStateService
```

as the new final MainForm constructor argument.

## 11.4 TestWorkspace

Add:

```csharp
public string PixelExactBatchStatePath =>
    Path.Combine(
        Root,
        AppConstants.PixelExactBatchStateFileName);

public string PixelExactStagingPath =>
    Path.Combine(
        Root,
        AppConstants.PixelExactStagingFolderName);

public PixelExactBatchStateService
    CreatePixelExactBatchStateService() =>
    new(
        PixelExactBatchStatePath,
        PixelExactStagingPath);
```

Tests must never touch real LocalAppData.

---

# 12. UI changes

## 12.1 MainForm.Designer.cs declarations

Add:

```csharp
private CheckBox chkPixelExact = null!;
private ComboBox cmbPixelExactCount = null!;
private Label lblPixelExactCount = null!;
```

## 12.2 MainForm.Layout.cs

In `BuildCurrentAssetGroup`, after Keep Settings and before Variants is a sensible location.

Create:

```csharp
chkPixelExact = new CheckBox
{
    Name = "chkPixelExact",
    Text = "Pixel-exact mode",
    AutoSize = true,
    Anchor = AnchorStyles.Left,
    Margin = new Padding(14, 0, 0, 0)
};

_toolTip.SetToolTip(
    chkPixelExact,
    "Processes several generated states of the exact same scene against "
    + "consecutive queue Requests. Each phase keeps its own queue asset name; "
    + "no A/B/C variant suffixes are created.");
```

Create count label/dropdown:

```csharp
lblPixelExactCount = new Label
{
    Name = "lblPixelExactCount",
    Text = "Pixel phases",
    AutoSize = true,
    Anchor = AnchorStyles.Left,
    Margin = new Padding(0)
};

cmbPixelExactCount = new ComboBox
{
    Name = "cmbPixelExactCount",
    DropDownStyle = ComboBoxStyle.DropDownList,
    Anchor = AnchorStyles.Left,
    Margin = new Padding(4, 0, 0, 0),
    Width = 60
};

for (var i = 1;
     i <= AppConstants.MaxPixelExactOutputCount;
     i++)
{
    cmbPixelExactCount.Items.Add(
        i.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
}

cmbPixelExactCount.SelectedIndex = 0;

_toolTip.SetToolTip(
    cmbPixelExactCount,
    "Number of non-master phase images generated by the current collection "
    + "prompt. Example: a five-phase series uses Ref4, therefore this value is 4.");
```

Wrap label + dropdown in its own non-wrapping FlowLayoutPanel, exactly as Variants already does.

Set:

```csharp
pixelExactCountFlow.Visible = false;
```

If the local variable cannot later be accessed, visibility can instead be controlled on `lblPixelExactCount` and `cmbPixelExactCount` individually.

## 12.3 State helpers

Add fields:

```csharp
private bool _settingWorkflowSelectors;
private readonly QueuePromptWorkflowParser _queuePromptWorkflowParser =
    new();
```

Helper:

```csharp
private int GetSelectedPixelExactOutputCount() =>
    cmbPixelExactCount.SelectedIndex + 1;

private void SetPixelExactOutputCount(int count)
{
    if (count is < 1 or > AppConstants.MaxPixelExactOutputCount)
    {
        throw new ArgumentOutOfRangeException(nameof(count));
    }

    _settingWorkflowSelectors = true;
    try
    {
        cmbPixelExactCount.SelectedIndex = count - 1;
    }
    finally
    {
        _settingWorkflowSelectors = false;
    }
}
```

## 12.4 WireEvents

Add:

```csharp
chkPixelExact.CheckedChanged += (_, _) =>
    OnPixelExactModeChanged();

cmbVariants.SelectedIndexChanged += (_, _) =>
    OnVariantsSelectionChanged();

cmbPixelExactCount.SelectedIndexChanged += (_, _) =>
{
    if (!_settingWorkflowSelectors)
    {
        AddStatus(
            $"Pixel-Exact phase count set manually to "
            + $"{GetSelectedPixelExactOutputCount()}.");
    }
};
```

## 12.5 Checkbox handler

```csharp
private void OnPixelExactModeChanged()
{
    if (_settingWorkflowSelectors)
    {
        return;
    }

    if (_state == UiState.ReferenceReady)
    {
        _settingWorkflowSelectors = true;
        try
        {
            chkPixelExact.Checked = false;
        }
        finally
        {
            _settingWorkflowSelectors = false;
        }

        return;
    }

    if (chkPixelExact.Checked)
    {
        _settingWorkflowSelectors = true;
        try
        {
            ResetVariantSelectionToNone();
            chkDirectMode.Checked = false;
        }
        finally
        {
            _settingWorkflowSelectors = false;
        }

        if (_activeRequest is not null)
        {
            ApplyQueueWorkflowAutodetection(_activeRequest);
        }
    }

    ApplyState();
}
```

## 12.6 Variants handler

```csharp
private void OnVariantsSelectionChanged()
{
    if (_settingWorkflowSelectors)
    {
        return;
    }

    if (GetSelectedVariantCount() <= 0)
    {
        return;
    }

    if (chkPixelExact.Checked)
    {
        _settingWorkflowSelectors = true;
        try
        {
            chkPixelExact.Checked = false;
        }
        finally
        {
            _settingWorkflowSelectors = false;
        }

        ApplyState();
    }
}
```

## 12.7 ApplyState

Update mode state:

```csharp
var pixelExact = chkPixelExact.Checked && !referenceReady;
```

Rules:

```csharp
chkPixelExact.Enabled = !referenceReady;

lblPixelExactCount.Visible = pixelExact;
cmbPixelExactCount.Visible = pixelExact;

cmbPixelExactCount.Enabled =
    pixelExact
    && !referenceReady
    && !_pixelExactBatchStateService.HasPendingState;

cmbVariants.Enabled =
    !referenceReady
    && !pixelExact;

chkDirectMode.Enabled =
    !referenceReady
    && !pixelExact;
```

Do not reset the Pixel-Exact count in `ApplyState`.

---

# 13. Queue workflow auto-detection

Create:

```text
MainForm.QueueWorkflowDetection.cs
```

Core method:

```csharp
private void ApplyQueueWorkflowAutodetection(
    AssetRequestItem item)
{
    var metadata =
        _queuePromptWorkflowParser.Parse(item.Prompt);

    if (!metadata.IsValid)
    {
        AddStatus(
            $"Workflow metadata for '{item.AssetName}' is invalid: "
            + string.Join("; ", metadata.Errors));
        return;
    }

    _settingWorkflowSelectors = true;

    try
    {
        switch (metadata.Kind)
        {
            case QueuePromptWorkflowKind.Variants:
                if (metadata.VariantCount is int variants)
                {
                    chkPixelExact.Checked = false;
                    cmbVariants.SelectedIndex = variants;
                }
                break;

            case QueuePromptWorkflowKind.PixelExactSeed:
            case QueuePromptWorkflowKind.PixelExactRef:
            case QueuePromptWorkflowKind.PixelExactOutput:
                ResetVariantSelectionToNone();

                if (metadata.PixelOutputCount is int pixelCount)
                {
                    SetPixelExactOutputCount(pixelCount);
                }
                break;

            case QueuePromptWorkflowKind.Single:
                ResetVariantSelectionToNone();
                break;

            case QueuePromptWorkflowKind.Unknown:
                // Preserve manual selector values.
                break;
        }
    }
    finally
    {
        _settingWorkflowSelectors = false;
    }

    ApplyState();
}
```

### Important event-loop fix

The copy-ready helper above nests `SetPixelExactOutputCount`, which also toggles `_settingWorkflowSelectors`.

Implement `SetPixelExactOutputCount` so it preserves the previous flag rather than blindly restoring `false`:

```csharp
private void SetPixelExactOutputCount(int count)
{
    if (count is < 1 or > AppConstants.MaxPixelExactOutputCount)
    {
        throw new ArgumentOutOfRangeException(nameof(count));
    }

    var old = _settingWorkflowSelectors;
    _settingWorkflowSelectors = true;
    try
    {
        cmbPixelExactCount.SelectedIndex = count - 1;
    }
    finally
    {
        _settingWorkflowSelectors = old;
    }
}
```

Use the same “preserve old flag” pattern whenever selectors are modified programmatically.

## 13.1 Call site

In `HandleRequestQueueItemActivate`, immediately after the request-bound text fields are assigned and before API candidate handling:

```csharp
ApplyQueueWorkflowAutodetection(item);
```

Do not put parser logic directly in the click handler.

## 13.2 Recovered session bind

At the end of `BindRecoveredSessionRequest`, also call:

```csharp
ApplyQueueWorkflowAutodetection(item);
```

---

# 14. Refactor queue binding to support internal phase switches

The existing `HandleRequestQueueItemActivate` mixes:

- safety validation,
- UI binding,
- clipboard copying,
- staged API candidate loading,
- visual refresh.

Pixel-Exact needs to bind the next phase without:
- showing a modal selection warning,
- copying each `AusRefN` prompt over the clipboard while no generation is needed,
- loading API candidates.

Create a small internal binding helper rather than duplicating field assignment.

Recommended signature:

```csharp
private void BindRequestFields(
    AssetRequestItem item,
    bool copyPromptToClipboard)
{
    _activeRequest = item;

    _settingRequestBoundFields = true;
    try
    {
        txtAssetFolderName.Text = item.AssetName;
        txtPrompt.Text = item.Prompt;
    }
    finally
    {
        _settingRequestBoundFields = false;
    }

    UpdatePromptPreview();
    ApplyQueueWorkflowAutodetection(item);

    if (copyPromptToClipboard)
    {
        TryCopyPromptToClipboard(item.Prompt);
    }

    RefreshRequestQueueVisuals();
}
```

Then replace the equivalent field-assignment section in the existing activation method with:

```csharp
BindRequestFields(
    item,
    copyPromptToClipboard: true);
```

Keep existing API Candidate load logic after this helper.

For Pixel-Exact phase-to-phase transitions call:

```csharp
BindRequestFields(
    nextItem,
    copyPromptToClipboard: false);
```

For the transition from committed Seed to the collection `RefN` prompt call:

```csharp
BindRequestFields(
    collectionItem,
    copyPromptToClipboard: true);
```

That one copy is desired because the user now needs to paste the collection prompt into the image generator.

---

# 15. Pixel-Exact state machine

Implement the state machine explicitly.

```text
OFF
  |
  | user checks Pixel-exact
  v
READY

READY + Seed row + phase1 commit
  |
  v
SEED_RECORDED
  |
  | auto-bind collection RefN and copy prompt
  v
WAITING_FOR_COLLECTION_IMAGES
  |
  | user generates/downloads N images, presses Main Image
  v
PREFLIGHT
  |
  | all safe
  v
STAGED
  |
  v
OUTPUT_1_COMMIT
  |
  | durable
  v
OUTPUT_1_QUEUE_DONE
  |
  v
OUTPUT_2_COMMIT
  |
  ...
  v
ALL_OUTPUTS_DONE
  |
  v
CLEAR_PIXEL_STATE
```

Cross-manifest:

```text
OUTPUT_K_QUEUE_DONE
  |
  | next canonical target not in current manifest
  v
WAITING_FOR_NEXT_MANIFEST
  |
  | user imports next manifest
  | exact SERIE + OUTPUT match found
  v
READY_TO_RESUME
  |
  | user presses Main Image
  v
OUTPUT_K+1_COMMIT
```

No automatic writes happen on manifest import.

---

# 16. Main Image dispatch order

Modify `HandleMainImageEntryPoint()` before API/Direct logic.

New order:

```csharp
private void HandleMainImageEntryPoint()
{
    if (chkPixelExact.Checked)
    {
        if (HandlePixelExactEntryPoint())
        {
            return;
        }

        // HandlePixelExactEntryPoint returns false only when the current
        // request is not a Pixel-Exact request and normal processing should
        // continue.
    }

    if (_activeApiCandidateMetadata is not null)
    {
        HandleMainImage();
        return;
    }

    if (!chkDirectMode.Checked)
    {
        HandleMainImage();
        return;
    }

    HandleDirectMainImage();
}
```

Do not put Pixel-Exact dispatch inside `HandleMainImage` after variants, because Direct mode calls a different path before that.

---

# 17. Seed/master handling

A seed row is still one ordinary asset commit.

Pixel-Exact must not invent a special asset writer for phase 1.

## 17.1 Before normal seed commit

When Pixel-Exact is checked and active request parses as a valid canonical `PixelExactSeed`:

- allow normal Main processing.
- capture enough context to recognize the post-commit seed.

## 17.2 After durable seed commit

Inside `CompleteMainUiAfterDurableCommit`, before request completion clears `_activeRequest`, capture:

```csharp
var completedRequest = _activeRequest;
var workflowMetadata =
    completedRequest is null
        ? null
        : _queuePromptWorkflowParser.Parse(
            completedRequest.Prompt);
```

After the Main asset is durable but before input fields are reset:

If:

```text
chkPixelExact.Checked
AND metadata == canonical PixelExactSeed
AND current manifest != null
```

create/update the seed state.

Master reference path:

```csharp
Path.Combine(
    session.AssetFolder,
    committedFilename)
```

Hash that committed copy, not the mutable download path.

Store:

- series ID,
- total phases,
- bundle count,
- seed request/fingerprint,
- master asset name,
- committed master path/hash,
- `processedAt`,
- cloned provider template snapshot.

Then perform existing queue completion.

**Important ordering constraint:** do **not** bind the collection row yet. The current
`CompleteMainUiAfterDurableCommit` calls `ResetAssetInputFieldsAfterDurableAction()`
after Request completion. If the Ref row were bound before that reset, its freshly
loaded Asset Name/Prompt would be cleared again when Keep Settings is off.

Required order inside `CompleteMainUiAfterDurableCommit`:

```text
1. capture active seed request + parsed seed metadata
2. asset is already durably committed
3. persist seed Pixel-Exact state
4. CompleteActiveRequestAfterMainCommit(seed)
5. normal ResetAssetInputFieldsAfterDurableAction()
6. normal recent-document/provider/status work
7. NOW resolve and BindRequestFields(collection RefN, copyPromptToClipboard: true)
8. ApplyState()
9. show the normal seed-complete UI message
```

If seed-state persistence fails after the asset itself is durable, **never roll back
or delete the completed asset**. Complete the normal Request bookkeeping, show a
clear Pixel-Exact continuation error, and require the user to re-establish the
series state manually/no-reference rather than pretending the asset failed.

After the reset, search current manifest for the one pending canonical row:

```text
same SERIE
ROLLE=REF
OUTPUT=1
BUNDLE matches
```

If exactly one exists:

- bind it,
- copy collection prompt to clipboard,
- set Pixel count from BUNDLE,
- status:
  > Pixel-Exact master committed. Collection prompt loaded for 4 remaining phases.

If none exists:

- preserve seed state,
- status:
  > Pixel-Exact master committed. Import the manifest containing this series' Ref4 collection prompt to continue.

If more than one exists:

- do not guess,
- show metadata error.

## 17.3 Legacy seed

If only legacy:

```text
SERIENGROESSE=5; NEXT=Ref4. PROZESSMARKER: Einzeln
```

auto-advance to the **immediately following** pending `Ref4` row only when it is in the same manifest.

Do not persist a cross-manifest identity for legacy seed because it has no stable `SERIE`.

---

# 17A. Starting Pixel-Exact directly on a Ref row, and true manual-count fallback

The feature must remain useful even when the user enables Pixel-Exact only after
phase 1 has already been processed.

## 17A.1 Canonical Ref row with no saved seed state

If the active request is canonical `ROLLE=REF` and no Pixel state exists:

- **No reference mode:** allowed.
  - call `CreateCollectionState(...)`,
  - use `SERIE/GESAMT/BUNDLE/OUTPUT` from the active prompt,
  - set `SeedCommitted=false`,
  - set `HasCanonicalSeriesIdentity=true`,
  - stage/process the bundle normally.
- **Reference-assisted mode:** not allowed because there is no durable authority
  proving which committed image is the master reference.
  - do not guess from newest downloads,
  - do not search asset folders by similar filename,
  - explain that the user must either use No reference mode for this collection
    or process/re-establish the canonical seed authority first.

This means the common user flow “phase1 already done; now turn Pixel-Exact on at
Ref4” works immediately in No reference mode.

## 17A.2 Why a manually editable dropdown must have real behavior

The user explicitly requires manual phase-count selection. Therefore Pixel-Exact
must not become unusable merely because a historical prompt has no parseable
workflow marker.

When all of the following are true:

```text
Pixel-Exact checkbox = ON
active queue Request exists
parser result = Unknown
user selected N manually
current manifest contains at least N pending rows starting at active row
```

allow **Manual Local Pixel-Exact**.

Safety rules:

1. current active row = output 1.
2. next N-1 queue rows in literal queue order = outputs 2..N.
3. all N rows must be pending.
4. all N destinations and selected images must pass full preflight.
5. create a durable state with:
   ```text
   HasCanonicalSeriesIdentity=false
   SeriesId="manual-" + first 16 chars of active RequestKey
   BundleCount=N
   TotalPhases=N+1
   ```
6. this mode is restricted to the **currently loaded manifest**.
7. if fewer than N rows are available, abort before staging/committing.
8. never resume this manual state into another manifest.
9. show a status line:
   > Manual Pixel-Exact mapping: N consecutive queue Requests. Cross-manifest continuation is disabled because no canonical SERIE/OUTPUT metadata exists.

This is also the fallback for genuinely old manifests that predate
`PROZESSMARKER`, while remaining fail-closed at manifest boundaries.

## 17A.3 Recognized legacy RefN

Recognized legacy `RefN/AusRefN` remains preferable to fully manual mode:

- count comes from RefN,
- same-manifest contiguous marker validation is required,
- `HasCanonicalSeriesIdentity=false`,
- no cross-manifest continuation.

# 18. Resolving and staging collection images

Implement in `MainForm.PixelExact.cs`.

## 18.1 Entry preconditions

Collection batch start requires:

- Pixel-Exact checked.
- `_activeRequest != null`.
- active prompt parses as valid `PixelExactRef`.
- count in dropdown is 1..10.
- `_activeApiCandidateMetadata == null`.
- no unrelated live reference session.
- valid Downloads folder.
- enough accepted images.
- no existing conflicting pending Pixel-Exact state from a different series.
- if explicit parsed `BUNDLE=N`, dropdown must equal N.

If dropdown differs from explicit prompt metadata:

**block**, do not confirm-and-guess.

Message:

```text
The active prompt declares Ref4/BUNDLE=4 but Pixel phases is set to 3.
To prevent phase-to-asset misalignment, Pixel-Exact will not start.
Restore the dropdown to 4 or correct the prompt metadata.
```

Manual dropdown is for prompts with no detectable count, not for contradicting authoritative metadata.

## 18.2 Image resolution

Reuse the current ImageFinderService ordering policy.

Call:

```csharp
_imageFinderService.FindLatestImages(settings, count)
```

It returns newest-first.

Reverse once:

```csharp
var ordered = latest.Reverse().ToArray();
```

Now:

```text
ordered[0] = output 1 / phase 2
ordered[1] = output 2 / phase 3
...
```

Validate each with `ValidationService.ValidateImageFile`.

## 18.3 Preview

Update Main selected label before commit with explicit mapping:

```text
Pixel-Exact: 4 images
Phase 2 <- image-a.webp
Phase 3 <- image-b.webp
Phase 4 <- image-c.webp
Phase 5 <- image-d.webp
```

Put full mapping in tooltip if label would be too long.

## 18.4 Timestamp ambiguity

Because image ordering is safety-critical, detect obvious ordering ambiguity.

If two chosen files have identical:

```text
LastWriteTimeUtc
AND CreationTimeUtc
```

do not silently rely on filename tie-break to determine phase order.

Block and instruct:

```text
Pixel-Exact cannot prove the order of two downloaded images because their
filesystem timestamps are identical. Download the phase images again in phase
order, or select/process them manually with Pixel-Exact off.
```

This stricter behavior is specific to Pixel-Exact. Do not change Variants.

---

# 19. Mapping queue targets

## 19.1 Canonical mapping

For a state:

```text
SERIE=S
BUNDLE=N
```

output index K maps only to a queue item whose canonical metadata satisfies:

```text
SERIE == S
BUNDLE == N
OUTPUT == K
PHASE == K + 1
```

Additionally:

```text
K == 1 -> ROLLE=REF
K >= 2 -> ROLLE=AUSREF
```

Exactly one match is required in the current manifest.

Ignore completed matches only for reconciliation; never use another series.

## 19.2 Legacy same-manifest mapping

For `RefN` without canonical series metadata:

- identify its current queue index.
- output 1 = active RefN row.
- outputs 2..N = the next N-1 rows.
- every row must be pending and parse as `AusRefN`.
- if sequence ends or another marker intervenes -> abort before first write.

## 19.3 Cross-manifest canonical continuation

If output K is not in current manifest:

- stop after previous output was safely finalized,
- keep staged output K..N,
- keep durable Pixel state,
- show:
  > Pixel-Exact series `<id>` is waiting for phase `<K+1>` (output `<K>`). Import the manifest containing that series/output to continue.

## 19.4 Cross-manifest legacy continuation

Forbidden as described earlier.

---

# 20. Per-output transaction order

This order is important.

For every output:

1. verify staged image hash.
2. locate exactly one target request.
3. ensure target request is not already completed, unless reconciling.
4. ensure target asset destination is safe.
5. bind target request fields **without clipboard copy**.
6. save Pixel state:
   ```text
   output.State = CommitInProgress
   manifest fingerprint
   request key
   asset name
   ```
7. perform **one** normal asset transaction.
8. if asset commit fails:
   - stop immediately,
   - do not mark request Done,
   - leave staged images untouched,
   - leave current output as `CommitInProgress`,
   - existing `session.json` recovery remains authoritative for any incomplete asset transaction.
9. if asset commit succeeds:
   - update Pixel state:
     ```text
     AssetCommitted
     asset folder
     timestamp
     ```
   - save atomically.
10. call existing queue completion for **that one request**.
11. update Pixel state to:
    ```text
    QueueCompleted
    ```
12. save atomically.
13. move to output K+1.

Never mark future outputs completed early.

---

# 21. No-reference output commit

This is the simplest path and should be implemented/tested first.

For each bound target:

```csharp
var ok = CommitNoReferenceAsset(
    settings,
    target.AssetName,
    staged.StagedPath,
    target.Prompt,
    state.BundleProcessedAt ?? DateTimeOffset.Now,
    suppressUiCompletion: true);
```

Because `_activeRequest` is already target, the generated `AssetSession.SourceRequestKey` is correct.

After `ok`:

```csharp
CompleteActiveRequestAfterMainCommit(
    new AssetSession
    {
        SourceRequestKey = target.RequestKey
    });
```

Then record recent final document exactly as Variants does.

Do not call `CompleteMainUiAfterDurableCommit` for each output; that would generate one success dialog per phase and clear fields at the wrong time.

---

# 22. Reference-assisted Pixel-Exact outputs

The first accepted phase is the reference actually used to generate later exact-scene phases. The tool can therefore correctly provenance that relation.

This support is required for a complete implementation.

## 22.1 Requirement

Reference-assisted automatic bundle processing requires a canonical seed state containing:

- committed master/reference path,
- master SHA-256,
- master processed time,
- master provider snapshot.

If that authority is missing:

- do not fabricate it,
- block reference-assisted Pixel-Exact,
- user may either:
  - return and process the seed with Pixel-Exact enabled, or
  - use No reference mode for this batch.

## 22.2 Create a reference session per target

Mirror the safe pattern already used by `HandleVariantBatch`.

For every output target:

```csharp
var prepared = _assetProcessorService.CreateReferenceSession(
    settings,
    target.AssetName,
    state.MasterReferencePath!,
    state.MasterProcessedAt!.Value,
    state.MasterProviderTemplate?.Clone(),
    target.RequestKey);

_sessionService.Save(prepared);

var targetSession = _assetProcessorService.ProcessReference(
    prepared,
    settings,
    state.MasterReferencePath!,
    state.MasterProcessedAt!.Value);

_sessionService.Save(targetSession);

_currentSession = targetSession;
_state = UiState.ReferenceReady;

RecordRecentDocument(
    ProvenanceDocumentKind.Reference,
    targetSession.ReferenceProvenancePath,
    targetSession.AssetFolderName,
    state.MasterProcessedAt.Value);
```

Then:

```csharp
_assetProcessorService.PrepareMainCommit(
    targetSession,
    settings.AcceptedExtensions,
    staged.StagedPath,
    target.Prompt,
    state.BundleProcessedAt!.Value);

_sessionService.Save(targetSession);

var ok = ExecuteMainCommit(
    targetSession,
    staged.StagedPath,
    target.Prompt,
    state.BundleProcessedAt.Value,
    suppressUiCompletion: true);
```

On success:

```csharp
_currentSession = null;
_state = UiState.Idle;
```

Then individually complete the queue row.

## 22.3 Failure behavior

If reference creation fails:

- run the same best-effort `RollbackReference` pattern used by Variants,
- do not advance the output,
- do not mark the queue row done,
- leave staged images/state.

If Main commit fails and existing recovery leaves `_state == ReferenceReady`:

- stop the Pixel batch,
- do not programmatically switch queue items,
- show that the recovered reference-assisted asset must be reconciled/cancelled before Pixel-Exact continues.

---

# 23. Handling post-commit crash windows

There are two durable systems:

1. `session.json` + asset transaction recovery.
2. Pixel-Exact orchestration journal.

Do not pretend they are one atomic database transaction.

Use explicit state transitions.

## 23.1 `CommitInProgress` on restart

On startup:

- let existing `RecoverSessionOnStartup()` run first.
- do not auto-retry this output.
- if a recovered reference/session is active, Pixel-Exact stays paused.
- if no session remains and no recorded `AssetCommitted` state exists, user can retry the same staged image explicitly.

Never choose a new download.

## 23.2 `AssetCommitted` but queue not completed

This means the asset transaction was durable but the queue bookkeeping was interrupted.

When the matching manifest is loaded:

- verify stored request key and asset folder/final provenance path still exist,
- mark that request Done using a new narrowly scoped helper:
  ```csharp
  CompleteRequestByKeyAfterKnownDurableCommit(requestKey)
  ```
- do not recreate the asset.

## 23.3 `QueueCompleted`

Advance to next output.

---

# 24. Queue completion refactor

Keep `CompleteActiveRequestAfterMainCommit` behavior intact for normal flows.

Extract the durable bookkeeping body into:

```csharp
private void CompleteRequestByKeyAfterKnownDurableCommit(
    string completedRequestKey)
```

Then:

```csharp
private void CompleteActiveRequestAfterMainCommit(
    AssetSession session)
{
    var completedRequestKey =
        _activeRequest?.RequestKey
        ?? session.SourceRequestKey;

    _activeRequest = null;
    _activeApiCandidateMetadata = null;

    if (string.IsNullOrWhiteSpace(completedRequestKey))
    {
        return;
    }

    CompleteRequestByKeyAfterKnownDurableCommit(
        completedRequestKey);
}
```

The extracted helper must contain the current behavior:

- require current manifest,
- update existing GenerationJob to `Committed`,
- find exact queue item,
- mark `IsCompleted`,
- add `_completedRequestKeys`,
- save `RequestProgressService`,
- refresh queue/progress.

This allows safe reconciliation without faking an active UI selection.

Do not change the meaning of normal completion.

---

# 25. Pixel-Exact orchestrator outline

Create `MainForm.PixelExact.cs`.

The method structure should be:

```csharp
private bool HandlePixelExactEntryPoint();

private bool HandlePixelExactSeedEntry(
    QueuePromptWorkflowMetadata metadata);

private bool HandlePixelExactCollectionEntry(
    AssetRequestItem active,
    QueuePromptWorkflowMetadata metadata);

private bool ContinuePendingPixelExactBatch(
    PixelExactBatchState state);

private IReadOnlyList<string>? TryResolvePixelExactImages(
    int count);

private IReadOnlyList<AssetRequestItem>?
    TryResolvePixelExactTargetsForCurrentManifest(
        PixelExactBatchState state);

private AssetRequestItem?
    FindCanonicalPixelExactTarget(
        PixelExactBatchState state,
        int outputIndex);

private bool CommitPixelExactOutput(
    PixelExactBatchState state,
    PixelExactStagedOutput staged,
    AssetRequestItem target);

private bool CommitPixelExactNoReferenceOutput(...);

private bool CommitPixelExactReferenceAssistedOutput(...);

private void FinishPixelExactBatch(
    PixelExactBatchState state);

private void PausePixelExactForNextManifest(
    PixelExactBatchState state);

private void TryPreparePixelExactResumeAfterManifestImport();

private void TryAutoAdvanceSeedToCollection(
    PixelExactBatchState seedState);
```

## 25.1 `HandlePixelExactEntryPoint` return contract

Return `true` when the click was handled/blocked by Pixel-Exact.

Return `false` only when Pixel-Exact should genuinely defer to the ordinary
single-asset flow (most importantly a recognized seed that is intentionally
committed normally). When Pixel-Exact is ON and the parser returns `Unknown`,
the handler must attempt the safe **Manual Local Pixel-Exact** path from §17A
instead of silently falling through.

Pseudo-implementation:

```csharp
private bool HandlePixelExactEntryPoint()
{
    if (_activeRequest is null)
    {
        return false;
    }

    var metadata =
        _queuePromptWorkflowParser.Parse(
            _activeRequest.Prompt);

    if (!metadata.IsValid)
    {
        ShowMessageBox(
            string.Join(
                Environment.NewLine,
                metadata.Errors),
            "Invalid Pixel-Exact metadata",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return true;
    }

    if (metadata.Kind == QueuePromptWorkflowKind.Unknown)
    {
        HandleManualLocalPixelExactCollection(
            GetSelectedPixelExactOutputCount());
        return true;
    }

    if (!metadata.IsPixelExact)
    {
        return false;
    }

    if (_activeApiCandidateMetadata is not null)
    {
        ShowMessageBox(
            "Pixel-Exact cannot be combined with a staged API Candidate.",
            "Pixel-Exact unavailable",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return true;
    }

    if (metadata.Kind ==
        QueuePromptWorkflowKind.PixelExactSeed)
    {
        // Phase 1 remains a normal single-asset commit.
        // Let the ordinary Main path run; post-commit hook records seed.
        return false;
    }

    if (metadata.Kind ==
        QueuePromptWorkflowKind.PixelExactRef)
    {
        HandlePixelExactCollectionEntry(
            _activeRequest,
            metadata);
        return true;
    }

    if (metadata.Kind ==
        QueuePromptWorkflowKind.PixelExactOutput)
    {
        var state =
            _pixelExactBatchStateService.Load();

        if (state is null)
        {
            ShowMessageBox(
                "This AusRef row belongs to a Pixel-Exact collection, "
                + "but no staged Pixel-Exact batch is available. "
                + "Activate the corresponding Ref row first.",
                "Pixel-Exact batch missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return true;
        }

        ContinuePendingPixelExactBatch(state);
        return true;
    }

    return false;
}
```

The implementing model must integrate this with the seed post-commit hook described earlier.

---

# 26. Target preflight before first write

For a same-manifest canonical batch, preflight as much as possible before output 1 commits.

At minimum validate:

- all currently available expected target rows are unique,
- output numbers are monotonic/complete until the first manifest-boundary gap,
- prompts are internally consistent,
- target rows are pending,
- asset names validate,
- destination folders do not already exist,
- all selected source images validate,
- staged copies/hashes validate.

For cross-manifest targets not yet loaded, no destination preflight is possible. Validate them when their manifest is imported and before their commit.

This differs intentionally from Variants, whose entire set is known under one request and can be fully preflighted upfront.

---

# 27. Import behavior with a pending Pixel-Exact batch

Modify `HandleImportRequest`.

Existing import validation remains atomic.

After the new manifest has been applied/restored and normal queue state is ready:

```csharp
TryPreparePixelExactResumeAfterManifestImport();
```

This method:

1. loads pending Pixel state,
2. validates state/staged hashes,
3. if state has no staged outputs yet:
   - looks for same canonical `SERIE`, role `REF`, output 1,
   - if exactly one -> bind and copy prompt,
   - otherwise leave imported queue normally.
4. if state has staged outputs:
   - determine first output not `QueueCompleted`,
   - search for exact canonical series/output match,
   - if exactly one pending match:
     - bind it,
     - do **not** copy prompt,
     - set Pixel count,
     - status:
       > Pixel-Exact series `<id>` is ready to resume at phase X. Press Main Image to continue.
5. never auto-commit on import.

Wrong manifest:

- is still allowed to load.
- do not consume anything.
- status says the pending series was not found.

This avoids trapping the user in one manifest.

---

# 28. Clear Queue behavior

Current `HandleClearRequestQueue` immediately deletes queue/progress state.

With a pending Pixel-Exact batch this could orphan staged phase images.

Before clearing:

```csharp
if (_pixelExactBatchStateService.HasPendingState)
{
    var result = ShowConfirmDialog(
        "A Pixel-Exact series is still pending. Clearing the Request Queue "
        + "can detach its staged phase images from the current workflow."
        + Environment.NewLine + Environment.NewLine
        + "Discard the pending Pixel-Exact batch and clear the queue?",
        "Pending Pixel-Exact series",
        MessageBoxButtons.OKCancel,
        MessageBoxIcon.Warning);

    if (result != DialogResult.OK)
    {
        return;
    }

    _pixelExactBatchStateService.DiscardPendingState();
}
```

Then continue existing clear behavior.

Do not silently discard the Pixel journal.

---

# 29. Startup behavior

After normal queue restore and after existing session recovery is available:

- inspect Pixel-Exact state.
- do not write assets automatically.
- validate state structure.
- if completed cleanup journal -> clean it.
- if pending:
  - show status:
    ```text
    Pending Pixel-Exact series restored: <series>, output K of N.
    ```
  - if current restored manifest has the matching target and no active recovered reference session:
    - bind it for resume.
- if staged hashes are invalid:
  - show error,
  - keep state for forensic/manual recovery,
  - do not replace images.

Because `RecoverSessionOnStartup()` is invoked from `Shown`, run the final Pixel-Exact resume preparation **after** that recovery call, not before.

Recommended:

```csharp
Shown += (_, _) =>
{
    RecoverSessionOnStartup();
    TryPreparePixelExactResumeAfterStartupRecovery();
    CheckAndStartBatchMonitoring();
};
```

---

# 30. Keep Settings behavior

Pixel-Exact queue binding must be authoritative over Keep Settings.

Existing Keep Settings may preserve text/Variants after a normal asset commit.

For an automatic Pixel phase transition:

1. normal suppressed commit avoids per-asset UI completion,
2. explicitly bind the next target after the durable previous completion,
3. therefore next queue target overwrites asset name/prompt regardless of Keep Settings.

Rules:

- Keep Settings does not prevent queue auto-detection.
- Keep Settings does not preserve a stale Variants value against a recognized Pixel prompt.
- Pixel count stays bound to the collection during an active durable batch.
- while staged batch is pending, disable Pixel count to prevent remapping staged images.

---

# 30A. Pixel-Exact mode persistence policy

Do **not** add `PixelExactEnabled` to `AppSettings` in this implementation.

The checkbox is intentionally transient, like a safety-sensitive operation mode:

- app restart returns Pixel-Exact checkbox to OFF,
- the durable pending batch journal is still restored,
- UI status tells the user a Pixel-Exact series is pending,
- the user explicitly re-enables Pixel-Exact before continuing writes.

The stored batch mapping survives; only automatic write-mode activation does not.

The Pixel count itself also does not need global settings persistence because a
recognized queue prompt reconfigures it and a pending batch carries its own
authoritative `BundleCount`.

# 31. Refresh Main behavior

Current Variants has custom Main Refresh behavior.

Add equivalent Pixel behavior.

In the Main refresh entry path:

```text
if Pixel-Exact checked
AND active request is RefN
=> preview N candidate images in phase order
=> do not commit
```

Suggested label:

```text
Selected: 4 Pixel-Exact phases (phase2.webp -> phase5.webp)
```

Tooltip should show one line per phase.

Do not alter Reference refresh.

---

# 32. Reference JSON / example changes

## 32.1 Do not change manifest schema

Keep:

```json
{
  "manifestVersion": 2,
  "assets": [
    {
      "filename": "...",
      "resolution": "...",
      "alpha": "...",
      "prompt": "..."
    }
  ]
}
```

## 32.2 Add a dedicated example file

Create:

```text
src/AssetProvenanceHelper/examples/pixel_exact_manifest_template.json
```

Use a full, valid 5-phase example plus one variants example.

Example skeleton:

```json
{
  "manifestVersion": 2,
  "assets": [
    {
      "filename": "scene_phase_01.webp",
      "resolution": "768x1024",
      "alpha": "not_required",
      "prompt": "Generate exactly one master scene. ...\n\nPROZESSMETA: MODE=PIXEL_EXACT; SERIE=example_scene_daycycle_001; ROLLE=SEED; PHASE=1; GESAMT=5; BUNDLE=4\nSERIENGROESSE=5; NEXT=Ref4. PROZESSMARKER: Einzeln"
    },
    {
      "filename": "scene_phase_02.webp",
      "resolution": "768x1024",
      "alpha": "not_required",
      "prompt": "Using the approved master reference, generate exactly four complete separate images for phases 2-5. ...\n\nPROZESSMETA: MODE=PIXEL_EXACT; SERIE=example_scene_daycycle_001; ROLLE=REF; PHASE=2; GESAMT=5; BUNDLE=4; OUTPUT=1\nPROZESSMARKER: Ref4"
    },
    {
      "filename": "scene_phase_03.webp",
      "resolution": "768x1024",
      "alpha": "not_required",
      "prompt": "Pixel-Exact output mapping placeholder for phase 3.\n\nPROZESSMETA: MODE=PIXEL_EXACT; SERIE=example_scene_daycycle_001; ROLLE=AUSREF; PHASE=3; GESAMT=5; BUNDLE=4; OUTPUT=2\nPROZESSMARKER: AusRef4"
    },
    {
      "filename": "scene_phase_04.webp",
      "resolution": "768x1024",
      "alpha": "not_required",
      "prompt": "Pixel-Exact output mapping placeholder for phase 4.\n\nPROZESSMETA: MODE=PIXEL_EXACT; SERIE=example_scene_daycycle_001; ROLLE=AUSREF; PHASE=4; GESAMT=5; BUNDLE=4; OUTPUT=3\nPROZESSMARKER: AusRef4"
    },
    {
      "filename": "scene_phase_05.webp",
      "resolution": "768x1024",
      "alpha": "not_required",
      "prompt": "Pixel-Exact output mapping placeholder for phase 5.\n\nPROZESSMETA: MODE=PIXEL_EXACT; SERIE=example_scene_daycycle_001; ROLLE=AUSREF; PHASE=5; GESAMT=5; BUNDLE=4; OUTPUT=4\nPROZESSMARKER: AusRef4"
    },
    {
      "filename": "variant_example.webp",
      "resolution": "768x1024",
      "alpha": "not_required",
      "prompt": "Erzeuge exakt 2 leicht unterschiedliche Varianten als 2 getrennte Einzelbilder. ...\n\nPROZESSMETA: MODE=VARIANTS; COUNT=2\nPROZESSMARKER: Varianten2"
    }
  ]
}
```

The `AusRef` prompts remain real prompt strings because every queue row requires a prompt and its Request key includes it. If the production source document already has richer phase-specific mapping text, preserve that text; only append the canonical metadata footer.

---

# 33. Update asset_request_conversion_prompt.txt

Keep the exact four-property rule.

Add after current prompt rule 10:

```text
10a. WORKFLOW METADATA INSIDE "prompt" — CRITICAL:
    - Lines beginning with "PROZESSMETA:", "SERIENGROESSE=", or containing
      "PROZESSMARKER:" / "NEXT=RefN" are part of the prompt.
    - Preserve them exactly.
    - Never move them into new JSON properties.
    - Never delete, summarize, translate, reorder, or normalize them.
    - Never invent workflow metadata that is not present in the source.
    - In particular, do not convert SERIE, PHASE, OUTPUT, BUNDLE, GESAMT,
      PROZESSMARKER, RefN, or AusRefN into sibling JSON fields.
```

Also extend the exact-format example to mention:

```text
The helper recognizes workflow metadata only when it remains inside the prompt.
```

Do not ask the conversion model to infer missing series IDs. The source asset-request generation step should create them.

---

# 34. Source prompt-generation requirement

Any upstream process that creates the asset-request source document must now obey:

### For Pixel-Exact series

- generate one stable `SERIE` ID.
- add it to every row in that series.
- calculate:
  ```text
  BUNDLE = GESAMT - 1
  ```
- seed:
  ```text
  PHASE=1
  ```
- Ref:
  ```text
  PHASE=2
  OUTPUT=1
  ```
- each AusRef:
  ```text
  PHASE=OUTPUT+1
  ```
- keep all legacy markers as additional human-readable compatibility lines.

### For old Variants requests

Append:

```text
PROZESSMETA: MODE=VARIANTS; COUNT=N
PROZESSMARKER: VariantenN
```

The tool still supports the current leading prose fallback, but canonical metadata is preferred.

---

# 34A. Mandatory migration of the current production Teil01–Teil04 JSON series

The currently described production data already contains legacy structures such
as `Einzeln`, `Ref4`, `Ref3`, `Ref2` and `AusRefN`, including series that cross
Teil-file boundaries.

Before relying on **cross-manifest** Pixel-Exact continuation, migrate those
production prompts to canonical metadata.

Do not change:

- filename,
- resolution,
- alpha,
- visual-generation instructions,
- intended queue order.

Only append/normalize the workflow footer.

For every series:

1. assign one deterministic stable `SERIE` ID;
2. use the same ID in every row, including rows in the next Teil file;
3. set seed `PHASE=1`;
4. set Ref row `OUTPUT=1; PHASE=2`;
5. enumerate AusRef rows `OUTPUT=2..N`;
6. enforce `PHASE=OUTPUT+1`;
7. enforce `GESAMT=BUNDLE+1`;
8. retain the existing `PROZESSMARKER` line.

**Important:** because manifest v2 Request keys include the complete prompt,
adding `PROZESSMETA` intentionally changes Request keys and therefore the
manifest fingerprint. Perform this metadata migration before using those files
for production progress tracking, or explicitly accept that old completion
bookkeeping for the pre-migration fingerprint will not carry over.

Acceptance for each migrated Teil file and for the combined cross-file set:

```text
valid JSON
manifestVersion=2
exactly four fields per asset
all filenames unique per manifest
all canonical Pixel rows parse with zero errors
all SERIE groups have exactly one SEED and one REF
OUTPUT indices are unique and continuous 1..BUNDLE
PHASE indices are continuous 1..GESAMT
cross-file continuation preserves the same SERIE/BUNDLE/GESAMT
no unrelated row shares a SERIE ID
legacy marker and canonical metadata agree
```

Do not automatically invent series IDs at runtime. This is source-data
normalization, not a fuzzy matching task.

# 35. Tests — parser

Create `QueuePromptWorkflowParserTests.cs`.

Required tests:

```text
Parse_Empty_IsUnknown
Parse_UnrelatedNumbers_IsUnknown
Parse_SingleMarker_IsSingle
Parse_LegacySeed_Ref4_ReturnsPixelSeedCount4Total5
Parse_LegacySeed_InconsistentSeriesSize_IsInvalid
Parse_Ref2_ReturnsPixelRef2
Parse_Ref3_ReturnsPixelRef3
Parse_Ref4_ReturnsPixelRef4
Parse_AusRef4_ReturnsPixelOutput4
Parse_Varianten2_ReturnsVariants2
Parse_LegacyGermanVariantsAtStart_Returns2
Parse_LegacyEnglishVariantsAtStart_Returns2
Parse_VariantMentionInsideSceneProse_DoesNotTrigger
Parse_CanonicalSeed_ReturnsAllFields
Parse_CanonicalRef_ReturnsOutput1Phase2
Parse_CanonicalAusRef_ReturnsOutput2Phase3
Parse_CanonicalVariant_ReturnsCount
Parse_CanonicalVariant_WithEinzelnMarker_IsInvalid
Parse_CanonicalDuplicateKey_IsInvalid
Parse_MultipleMetaLines_IsInvalid
Parse_RefMarkerContradictsBundle_IsInvalid
Parse_AusRefMarkerContradictsBundle_IsInvalid
Parse_TotalNotBundlePlusOne_IsInvalid
Parse_PhaseNotOutputPlusOne_IsInvalid
Parse_SeedPhaseNot1_IsInvalid
Parse_RefOutputNot1_IsInvalid
Parse_CanonicalRef_WithEinzelnMarker_IsInvalid
Parse_CanonicalAusRef_WithEinzelnMarker_IsInvalid
Parse_CanonicalSeed_WithRefMarker_IsInvalid
Parse_InvalidSeriesId_IsInvalid
Parse_CountZero_IsInvalid
Parse_Count11_IsInvalid
```

Do not only test happy paths.

---

# 36. Tests — selector auto-detection

Create `QueueWorkflowAutoDetectionTests.cs` or add focused tests to the existing Variants class.

Required:

1. activate queue row with old:
   ```text
   Erzeuge exakt 2 leicht unterschiedliche Varianten...
   ```
   -> `cmbVariants.SelectedIndex == 2`.

2. canonical:
   ```text
   MODE=VARIANTS; COUNT=4
   ```
   -> 4.

3. activate Ref4:
   - Variants becomes none.
   - Pixel count becomes 4.
   - Pixel checkbox remains whatever explicit user state requires; it must not be auto-enabled.

4. activate AusRef3:
   -> Pixel count 3.

5. activate legacy seed:
   ```text
   SERIENGROESSE=5; NEXT=Ref4. PROZESSMARKER: Einzeln
   ```
   -> Pixel count 4.

6. unknown prompt:
   - manually set Variants 3,
   - activate unknown row,
   - still 3.

7. known Single:
   - manually set Variants 3,
   - activate Single row,
   - becomes none.

8. invalid contradictory metadata:
   - no crash,
   - no wrong auto-value,
   - warning/status emitted.

9. manually choose Variants > 0 while Pixel-Exact checked:
   - Pixel-Exact unchecks.

10. check Pixel-Exact while Variants = 3:
    - Variants -> none.
    - Direct -> off.

---

# 37. Tests — state service

Create `PixelExactBatchStateServiceTests.cs`.

Required:

```text
SaveLoad_RoundTripsState
Save_IsAtomicAndLeavesNoTempFile
CreateSeedState_HashesCommittedMaster
StageBundle_CopiesWithoutMovingSources
StageBundle_MapsOldestFirstToOutputIndices
StageBundle_CreatesUniqueStagingFiles
StageBundle_FailureLeavesSeedStateIntact
Load_CorruptJson_ThrowsClearInvalidDataException
Load_UnsupportedSchemaVersion_IsRejected
Validate_MissingStagedImage_FailsClosed
Validate_ModifiedStagedImageHash_FailsClosed
Validate_PathOutsideStagingRoot_FailsClosed
Validate_DuplicateOutputIndex_Fails
Validate_NonContiguousOutputs_Fails
DiscardPendingState_RemovesJournalAndStaging
CompleteCleanup_IsCrashTolerant
```

Add test seams/hooks only if necessary to simulate atomic-write failure. Follow the repository convention for test seams.

---

# 38. Tests — same-manifest Pixel-Exact workflow

Create `PixelExactWorkflowTests.cs`.

Run WinForms tests on STA using the same pattern as existing MainForm tests.

## 38.1 Core Ref4 happy path

Manifest:

```text
seed phase1
Ref4 phase2
AusRef4 phase3
AusRef4 phase4
AusRef4 phase5
```

Test setup:

- canonical metadata.
- seed already completed/state recorded or create state.
- create four downloaded test images with strictly increasing timestamps.
- No reference mode.
- Pixel-Exact checked.
- activate Ref4.
- Main Image.

Assert:

- phase2 target uses oldest of selected four.
- phase3 uses next.
- phase4 uses next.
- phase5 uses newest.
- exact queue asset names are used.
- no folder ends in `A`, `B`, `C`, `D` unless that was genuinely part of source asset name.
- every corresponding queue row is `Done`.
- exactly four final provenance documents exist.
- every final provenance prompt matches that row's queue prompt.
- Pixel state cleared after output4.
- downloaded originals still exist.
- staged temporary copies are cleaned after successful completion.

## 38.2 No Variants leakage

Set Variants 3 before activating Ref4.

Assert queue activation:
- Variants resets none.
- no `AssetNaming.BuildVariantAssetName` effect.
- four queue targets, not three A/B/C assets.

## 38.3 Ref2 and Ref3

Separate tests:
- Ref2 -> 2 outputs.
- Ref3 -> 3 outputs.

Do not only test Ref4.

## 38.4 Start directly on canonical RefN without seed state

With No reference mode:

- no prior Pixel state,
- activate canonical Ref3,
- Pixel-Exact ON,
- three ordered downloads,
- press Main.

Assert collection state is created from the Ref metadata and all three outputs
commit correctly.

With reference-assisted mode and no seed authority:

- assert operation is blocked before any destination write.

## 38.5 Fully manual local mode

Use a manifest whose prompts contain no workflow metadata.

- Pixel-Exact ON.
- manually choose 3.
- activate first of three consecutive pending queue rows.
- press Main.

Assert all three process in queue order and `HasCanonicalSeriesIdentity=false`.

Repeat with only two rows remaining while count=3:

- assert zero writes and zero Done rows.

---

# 39. Tests — mid-batch failure and retry

Inject failure on output 2.

Expected:

```text
output1 asset durable
output1 queue Done
output2 not Done
output3+ not attempted
state remains
staged files remain
next output remains output2
```

Retry:

- do not scan new Downloads.
- mutate/add newer files in Downloads before retry.
- verify retry still uses the exact staged output2 hash/path.
- complete remaining outputs.
- state cleanup only after all done.

This is a critical acceptance test.

---

# 40. Tests — cross-manifest continuation

This must exist because the real data contains split series.

## 40.1 Canonical Ref4 spanning two manifests

Manifest A:

```text
seed
Ref4 output1
AusRef4 output2
```

Manifest B:

```text
AusRef4 output3
AusRef4 output4
...other unrelated rows...
```

Process Manifest A:

- four generated images are staged at Ref4 start.
- output1 Done.
- output2 Done.
- no output3 target in manifest.
- batch pauses.
- staged outputs 3 and 4 remain.
- state records next output 3.

Import unrelated manifest:

- no image is consumed.
- no auto-write occurs.

Import Manifest B:

- exact same `SERIE`,
- output3 found.
- UI binds it for resume.
- no commit yet.

Press Main Image:

- output3 uses original staged image #3.
- output4 uses original staged image #4.
- both rows Done.
- state cleared.

## 40.2 Wrong same Ref count must not match

Pending:

```text
SERIE=gas_station_001; BUNDLE=4; next OUTPUT=3
```

Imported manifest contains:

```text
SERIE=motel_009; BUNDLE=4; OUTPUT=3
```

Assert:

- no binding,
- no write,
- pending state unchanged.

## 40.3 Legacy cross-manifest abort

Ref4 legacy in Manifest A with only output1/output2 rows.

Assert before first output:
- no asset created,
- no row Done,
- no downloads staged permanently,
- clear error demanding canonical metadata.

---

# 41. Tests — restart/recovery

Required:

1. stage Ref4.
2. commit output1.
3. simulate form/app restart.
4. restore current queue + Pixel state.
5. assert output2 is next.
6. add newer downloads.
7. assert staged output2 still wins.

Also:

```text
CommitInProgress + recovered reference session
=> Pixel auto-resume blocked
=> request not switched
```

And:

```text
AssetCommitted state + matching pending queue row
=> reconcile queue Done without writing asset again
```

---

# 42. Tests — queue import/clear behavior

Required:

```text
Import_WithPendingPixelState_PreparesExactResume
Import_WrongManifest_DoesNotDestroyPendingState
Import_DoesNotAutoCommit
ClearQueue_WithPendingPixelState_Cancel_PreservesEverything
ClearQueue_WithPendingPixelState_Confirm_DiscardsPixelStateThenClearsQueue
```

Use `ConfirmBoxProvider` seam. Never show real modal UI in tests.

---

# 43. Tests — reference-assisted Pixel-Exact

Required:

1. canonical seed committed while Pixel-Exact enabled.
2. seed committed Main is captured as master reference.
3. Ref2 collection with two images.
4. reference-assisted Pixel processing.
5. each output asset contains:
   - copied reference,
   - reference provenance,
   - final Main,
   - final provenance.
6. both output references have identical SHA-256 to master committed seed.
7. each target's final provenance uses its own prompt.
8. each queue row Done only after its Main is durable.
9. failure during replicated reference creation:
   - no false Done,
   - rollback attempted,
   - remaining staged images preserved.

---

# 44. Tests — existing regressions

At minimum run focused existing classes after feature tests:

```text
FeatureV14VariantsAndKeepSettingsTests
RequestQueuePersistenceUiTests
RequestQueueStateServiceTests
ProgramStartupTests
```

Pay special attention to:

- Variants oldest-first ordering.
- reference-assisted Variants.
- partial Variants Request stays Pending.
- Keep Settings.
- queue import.
- startup queue restore.
- staged API candidate activation.
- Direct mode.

No existing test should be weakened merely to make Pixel-Exact pass.

---

# 45. UI/layout tests

At minimum assert:

- `chkPixelExact` exists.
- Pixel count label/dropdown hidden while off.
- visible while on.
- mode row still wraps at supported minimum width.
- Variants label/dropdown remain grouped.
- no controls clip at `MinimumSize`.
- reference-active state disables Pixel-Exact.
- Pixel pending batch locks its count.
- Direct disabled while Pixel-Exact active.

Do not increase the minimum window width to “solve” clipping.

---

# 46. Manual acceptance scenario

Run this manually after automated tests.

## Scenario A — 5-phase scene

1. Import a manifest with canonical 5-phase set.
2. Enable `Pixel-exact mode`.
3. Activate phase1 seed.
4. Generate/download phase1.
5. process it normally.
6. verify:
   - seed row becomes Done,
   - `Ref4` row activates,
   - collection prompt is copied,
   - Pixel dropdown shows `4`.
7. generate four complete images from the collection prompt.
8. download them **in phase order**.
9. click Main Image once.
10. verify:
    - phase2 asset created,
    - phase2 row Done,
    - phase3 target internally selected and committed,
    - same for phases4/5,
    - no per-phase success modal interrupts the batch,
    - final one summary appears,
    - no A/B/C/D suffixes.
11. inspect all four asset folders/provenance docs.

## Scenario B — cross-file

Repeat with outputs split between two manifests.

Verify:
- the app safely pauses,
- staged files survive,
- importing next file binds exact continuation,
- no output is accidentally assigned to another Ref4 series.

## Scenario C — variants auto count

Activate an old variants request beginning:

```text
Erzeuge exakt 2 leicht unterschiedliche Varianten...
```

Verify dropdown becomes 2 automatically and existing Variants behavior remains unchanged.

---

# 47. Status and dialog wording

Use concrete wording. Avoid generic “something went wrong”.

Recommended status messages:

```text
Pixel-Exact detected: Ref4 -> 4 phase images.
Pixel-Exact master committed: gas_station_daycycle_001.
Pixel-Exact collection prompt loaded: 4 remaining phases.
Pixel-Exact staged 4 images for gas_station_daycycle_001.
Pixel-Exact phase 2 committed: <asset>.
Pixel-Exact phase 3 committed: <asset>.
Pixel-Exact paused: waiting for output 3 in another manifest.
Pixel-Exact resume ready: phase 4 / output 3.
Pixel-Exact series complete: 4 of 4 collection outputs committed.
Variants auto-detected from queue prompt: 2.
```

Completion summary:

```text
Pixel-Exact Complete

Series: gas_station_daycycle_001
Collection outputs: 4 of 4
Queue Requests completed: 4
Staged images: cleaned
```

Partial summary:

```text
Pixel-Exact Paused

Series: gas_station_daycycle_001
Completed: 2 of 4 collection outputs
Next: output 3 / phase 4
Reason: matching queue Request is not in the current manifest.

The remaining generated images are safely staged.
Import the manifest containing the next series/output and continue.
```

---

# 48. README update

Document:

- what Pixel-Exact means.
- that it is distinct from Variants.
- RefN count semantics.
- canonical marker example.
- phase download order.
- same-manifest behavior.
- cross-manifest continuation.
- durable staging/restart behavior.
- API Candidate incompatibility in v1.
- Direct mode mutual exclusion.
- manual dropdown fallback.
- variants auto-detection.
- legacy marker limits.

Include one compact comparison table:

| Feature | Variants | Pixel-Exact |
|---|---|---|
| One prompt creates several images | yes | yes |
| All outputs use one queue Request | yes | no |
| Queue auto-advances | no | yes, inside series |
| Asset names | derived A/B/C | queue names |
| Reference relation | same variant reference logic | master scene can be reused |
| Cross-manifest | not applicable | supported with canonical metadata |
| Count auto-read from queue | new | new |

---

# 49. Versioning

Current project version on the verified baseline is `1.5.1`.

This is a user-visible workflow feature, so after all tests pass bump to:

```xml
<Version>1.6.0</Version>
```

Only do the version bump at the final feature phase, not during early implementation.

Add/update release notes according to repository convention.

---

# 50. Implementation phases and hard gates

## Phase 1 — Parser only

Changes:

```text
AppConstants.cs
Models/QueuePromptWorkflowMetadata.cs
Services/QueuePromptWorkflowParser.cs
QueuePromptWorkflowParserTests.cs
```

Gate:

- build.
- all parser tests pass.
- no UI changes yet.

## Phase 2 — Selector UI + auto-detection

Changes:

```text
MainForm.Designer.cs
MainForm.Layout.cs
MainForm.cs
MainForm.QueueWorkflowDetection.cs
MainForm.RequestQueue.cs
QueueWorkflowAutoDetectionTests.cs
```

Gate:

- Pixel checkbox/dropdown behavior correct.
- legacy Variants auto-detection works.
- all existing Variants focused tests pass.

## Phase 3 — Durable Pixel state/staging

Changes:

```text
Models/PixelExactBatchState.cs
Services/PixelExactBatchStateService.cs
AppConstants.cs
AppBootstrap.cs
Program.cs
TestWorkspace.cs
PixelExactBatchStateServiceTests.cs
```

Gate:

- atomic persistence tests.
- hash/path validation.
- no writes to real LocalAppData from tests.

## Phase 4 — Same-manifest no-reference RefN orchestration

Changes:

```text
MainForm.PixelExact.cs
MainForm.MainWorkflow.cs
MainForm.DirectMode.cs
MainForm.RequestQueue.cs
PixelExactWorkflowTests.cs
```

Gate:

- Ref2/Ref3/Ref4 happy paths.
- exact queue names.
- per-row Done.
- no variants suffixes.
- mid-batch failure/retry from staged images.

## Phase 5 — seed auto-advance

Implement:

- seed state capture.
- Ref collection auto-bind.
- collection prompt clipboard copy.

Gate:

- one click completes seed,
- correct RefN is active immediately after,
- count auto-set.

## Phase 6 — cross-manifest continuation

Implement import/resume and clear-queue guard.

Gate:

- canonical series split across two manifests passes.
- wrong same-sized series cannot match.
- legacy split fails before writes.

## Phase 7 — reference-assisted Pixel-Exact

Implement master-reference reuse per output.

Gate:

- reference SHA matches master.
- per-output reference/final provenance valid.
- failure recovery safe.

## Phase 8 — examples/docs/version

Changes:

```text
examples/asset_request_conversion_prompt.txt
examples/pixel_exact_manifest_template.json
README.md
AssetProvenanceHelper.csproj
release notes
```

Gate:

- examples import successfully.
- docs match actual UI/behavior.

## Phase 9 — final verification

Run repository-prescribed verification.

Do not skip.

---

# 51. Test execution instructions

Read `AGENTS.md` first.

Use targeted tests while iterating.

Example:

```powershell
dotnet build AssetProvenanceHelper.sln -c Debug --no-restore -warnaserror
```

For a targeted class, use the repository SAC-safe test runner when Windows Smart App Control interferes.

Example shape:

```powershell
powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~QueuePromptWorkflowParserTests"
```

Then:

```powershell
powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~PixelExactBatchStateServiceTests"
powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~PixelExactWorkflowTests"
powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~FeatureV14VariantsAndKeepSettingsTests"
powershell -File scripts/run_tests_sac_safe.ps1 -Filter "FullyQualifiedName~RequestQueuePersistenceUiTests"
```

If the SAC-safe runner reports the repository-defined environment-block exit (`42`), report it as an environment block, not a product test failure.

Final verification from a clean tree:

```powershell
powershell -File scripts/verify_like_ci.ps1
```

Also run the repository's coverage workflow/rachet exactly as documented in `AGENTS.md`/scripts.

Do not claim final success from:

- `--no-build` against stale binaries,
- a dirty-tree warm test,
- only the newly added tests,
- only manual testing.

---

# 52. Required assertions before declaring implementation complete

The implementing model must explicitly verify every item.

## Data contract

- [ ] manifest remains v2.
- [ ] asset object still has exactly four allowed fields.
- [ ] metadata remains inside prompt.
- [ ] existing v1/v2 manifests still import.

## Parsing

- [ ] Ref2/3/4 detected.
- [ ] AusRef detected.
- [ ] seed NEXT detected.
- [ ] canonical metadata detected.
- [ ] contradictions rejected.
- [ ] arbitrary prose numbers do not trigger.
- [ ] legacy variants detected.
- [ ] canonical variants detected.

## UI

- [ ] new checkbox present.
- [ ] Pixel dropdown only visible when mode on.
- [ ] Ref4 auto-selects 4.
- [ ] Variants prompt auto-selects N.
- [ ] manual fallback remains.
- [ ] Pixel and Variants mutually exclusive.
- [ ] Pixel and Direct mutually exclusive.
- [ ] reference-active session locks mode changes.

## Pixel processing

- [ ] oldest downloaded candidate -> output1/phase2.
- [ ] next -> output2/phase3.
- [ ] exact queue asset names.
- [ ] no A/B suffixing.
- [ ] target prompt changes per queue row.
- [ ] every row Done only after its own durable Main.
- [ ] one failure stops future writes.
- [ ] retry uses staged hash, not new Downloads.
- [ ] downloads are copied, never moved/deleted.

## Cross-manifest

- [ ] staged images survive manifest replacement.
- [ ] stable SERIE + OUTPUT mapping.
- [ ] wrong series cannot match.
- [ ] import never auto-writes.
- [ ] legacy ambiguous split fails closed.
- [ ] Clear Queue cannot silently orphan pending batch.

## Recovery

- [ ] corrupt state fails closed.
- [ ] missing staged image fails closed.
- [ ] modified staged image fails hash validation.
- [ ] `CommitInProgress` does not auto-duplicate.
- [ ] `AssetCommitted` can reconcile queue bookkeeping.
- [ ] completed state cleanup is crash-tolerant.

## Regression

- [ ] existing Variants happy paths pass.
- [ ] Variants partial failure semantics unchanged.
- [ ] Direct mode unchanged outside Pixel.
- [ ] staged API Candidate workflow unchanged outside Pixel.
- [ ] Keep Settings unchanged outside queue-authoritative auto-configuration.
- [ ] Reference workflow unchanged outside Pixel.
- [ ] request queue persistence still restores.

---

# 53. Things the implementing model must NOT do

Do not:

- add `seriesId` fields to manifest JSON objects.
- bump manifest to v3.
- repurpose `cmbVariants` as the Pixel selector.
- call Pixel images “variants” in code or UI.
- process Ref4 into `assetA`, `assetB`, ...
- mark the Ref4 request only once after all phase assets.
- use one collection prompt for every output's provenance if the queue row has its own prompt.
- assume all series live in one JSON file.
- infer cross-file series identity from `Ref4` alone.
- infer series identity from filename similarity.
- infer series identity from semantic scene words.
- use random or alphabetical file order as silent phase authority.
- delete user downloads after staging.
- auto-resume writes at startup/import.
- make queue import impossible merely because a Pixel batch waits for another manifest.
- silently discard Pixel state on Clear Queue.
- add a second unrelated queue persistence system.
- rewrite existing transaction/recovery services.
- break or weaken `FeatureV14VariantsAndKeepSettingsTests`.
- add real MessageBox calls in automated tests.
- write tests against real `%LOCALAPPDATA%`.
- claim pixel-level image similarity checking is being implemented. This feature orchestrates provenance/queue mapping; actual pixel similarity is the generation model's responsibility.

---

# 54. Suggested internal comments

Use comments only where they protect invariants.

Good comments:

```csharp
// Pixel-Exact differs from Variants: every generated image belongs to a
// different queue Request and therefore must complete that Request separately.
```

```csharp
// Do not rescan Downloads after staging. The staged hash/path is the durable
// authority for phase-to-output mapping across retries and manifest changes.
```

```csharp
// Cross-manifest continuation is allowed only with canonical SERIE + OUTPUT
// metadata. RefN alone is intentionally insufficient to identify a series.
```

```csharp
// Request completion remains after the asset's durable Main commit.
```

Avoid comments that merely restate syntax.

---

# 55. Example end-to-end canonical series

This is the exact mental model the implementation must satisfy.

```text
A) active queue row:
   desert_lab_dawn.webp
   MODE=PIXEL_EXACT
   SERIE=desert_lab_daycycle_001
   ROLLE=SEED
   PHASE=1
   GESAMT=5
   BUNDLE=4
   PROZESSMARKER: Einzeln

B) user generates master dawn image.

C) user clicks Main Image.
   -> dawn asset commits
   -> dawn Request Done
   -> committed dawn image recorded as master authority
   -> queue auto-binds desert_lab_morning.webp
   -> collection prompt copied
   -> Pixel dropdown = 4

D) collection row:
   desert_lab_morning.webp
   ROLLE=REF
   PHASE=2
   OUTPUT=1
   BUNDLE=4
   PROZESSMARKER: Ref4

E) user generates four complete images:
   morning
   noon
   evening
   night

F) user downloads in that phase order.

G) user clicks Main Image once.

H) helper stages:
   output1 <- morning
   output2 <- noon
   output3 <- evening
   output4 <- night

I) helper commits:
   output1 -> desert_lab_morning -> Request Done
   output2 -> desert_lab_noon    -> Request Done
   output3 -> desert_lab_evening -> Request Done
   output4 -> desert_lab_night   -> Request Done

J) no A/B/C/D derived names exist.

K) batch state is cleaned.
```

---

# 56. Why this architecture is the preferred solution

This design deliberately reuses the current application's strongest properties:

- existing per-asset durable transaction/recovery stays authoritative,
- existing queue completion invariant stays intact,
- existing request keys/fingerprints already bind prompt metadata,
- existing manifest schema remains compatible,
- existing ImageFinder ordering is reused,
- existing Variants remains unchanged,
- new parsing is isolated and testable,
- new cross-manifest batch state is small and purpose-specific,
- staged hashes prevent retries from silently consuming different downloads,
- canonical stable series/output metadata makes cross-file mapping deterministic.

The main alternative — “just modify HandleVariantBatch to advance the queue” — is rejected because Variants fundamentally represents many outputs of **one request**, while Pixel-Exact represents many **different requests** with different final names/prompts/completion states.

The other tempting alternative — add new JSON fields and manifest v3 — is rejected because the current importer intentionally rejects unknown fields and the prompt itself is already part of the cryptographic request identity.

---

# 57. Final definition of done

The feature is complete only when all of the following are simultaneously true:

1. User can enable Pixel-Exact with a checkbox.
2. Pixel dropdown appears only then.
3. RefN/metadata automatically set the correct count.
4. Existing Variants count auto-fills from current queue prompt.
5. Phase1 is committed normally.
6. A canonical seed automatically advances to its collection prompt.
7. One Main click on RefN processes the current set's generated outputs sequentially.
8. Every output binds to the correct separate queue row.
9. Every output uses its queue-defined asset name and prompt.
10. Every output Request becomes Done only after its own durable commit.
11. No A/B variant naming appears.
12. Partial failure is safely resumable.
13. Restart does not remap images.
14. Cross-manifest canonical series are resumable.
15. Ambiguous legacy cross-manifest series fail before writes.
16. Pixel state cannot be silently orphaned by Clear Queue.
17. Reference-assisted use can provenance the committed master as reference.
18. Existing Variants/Direct/API/queue/recovery tests remain green.
19. updated example prompts/JSON demonstrate the canonical contract.
20. clean-tree CI-equivalent verification succeeds.

Only after all 20 conditions pass should the implementation be called finished.

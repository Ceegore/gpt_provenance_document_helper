# API Batch Automation – Post-Repair Audit Results & Remaining-Fix Plan

**Repository:** `Ceegore/gpt_provenance_document_helper`  
**Audited branch:** `feature/api-batch-automation`  
**Audited HEAD:** `f4f579b60a1330259ae6fe7239e36bdc7153d363`  
**Parent / pre-repair API commit:** `240b8d384332f06fde8c2504b41647974e0873d6`  
**Audit date:** 2026-09-03  
**Primary product contract:** `IMPLEMENTIERUNGSKONZEPT_API_BATCH_AUTOMATION.md`  
**Repair contract:** `API_BATCH_AUTOMATION_REPAIR_PLAN_WEAK_AGENT.md`

---

# 0. Executive verdict

## Verdict: **NOT READY TO MERGE YET**

The repair commit substantially improved the implementation. Several important fixes landed correctly:

- Direct generation now captures an immutable run snapshot.
- Manifest import is blocked during Direct generation / Batch submission.
- Batch submission is split into upload and create phases.
- `ProviderInputFileId` is persisted before remote batch creation.
- Direct API candidates take precedence over **legacy Direct mode**.
- Provider raw output is written before normalization.
- `x-request-id` is propagated through Direct candidate/job metadata.
- model-specific connection testing is implemented.
- Settings no longer auto-populates the stored key into the textbox.
- JobStore bulk APIs were added.
- terminal Batch processing now considers `error_file_id` as well as `output_file_id`.
- preflight now stops a paid run when a genuine local manifest error exists.

However, the audit found multiple remaining correctness, recovery, provenance and cost-safety defects.

### Severity summary

| Severity | Count | Merge impact |
|---|---:|---|
| **P0 – release blocker / duplicate remote work risk** | 2 | Must fix before any real Batch usage |
| **P1 – serious correctness/recovery/provenance issue** | 8 | Must fix before merge |
| **P2 – significant spec/test/performance/quality gap** | 10 | Strongly recommended before merge |
| **P3 – hardening / maintainability / misleading state** | 4 | Fix in same repair pass if practical |

The two most important remaining failures are:

1. **Batch result `custom_id` validation is still not fail-closed.**
2. **If the remote Batch is created but saving its remote Batch ID fails, the user can immediately submit the same items again.**

---

# 1. Verification limitations

This audit was performed against the exact committed GitHub source at:

```text
f4f579b60a1330259ae6fe7239e36bdc7153d363
```

Important verification facts:

- The branch HEAD is still exactly that commit at the end of this audit.
- There is currently **no open pull request** for the branch.
- There are **no GitHub Actions workflow runs associated with this HEAD**.
- The branch has no required status checks.
- The committed `docs/audits/api-repair-baseline.md` records successful tests for the **pre-repair commit** `240b8d3...`, not for the post-repair commit.
- I could therefore perform a thorough source/test/static audit, but I cannot truthfully claim that the final post-repair full Debug/Release/coverage/mutation/flakiness suite has passed.

This is a verification blocker, not proof that the code fails to build.

Before merge, the final fix commit must be run through the full test matrix in Section 14.

---

# 2. Repair-plan compliance matrix

| Repair item | Result | Notes |
|---|---|---|
| R-001 immutable Direct run context / manifest race | ✅ **Landed** | Direct uses `ApiGenerationRunSnapshot`; import blocked while Direct/Batch local mutation active. |
| R-002 Batch upload/create durability checkpoints | ✅ **Landed** | Upload and create are split; input file ID is saved before CreateBatch. |
| R-003 remote Batch created but local ID save fails | ❌ **Partial / unsafe** | Same method does not auto-resubmit, but items remain `BatchQueued` and preflight does not treat that state as active, allowing immediate duplicate manual submission. |
| R-004 API Candidate beats legacy Direct | ⚠️ **Partial** | Direct guard landed; **Variants still bypass it** because `HandleMainImage()` checks variants before API candidate verification. |
| R-005 raw provider output before normalization | ⚠️ **Partial** | Ordering landed, but production failure paths overwrite the rich `Normalizing` record with the old `itemRecord`, losing raw recovery metadata. Restart recovery also ignores `Normalizing`. |
| R-006 complete preflight / no silent local-error skipping | ⚠️ **Mostly landed** | Real local errors now block paid action. But Ready-with-missing-file becomes eligible for regeneration, `BatchQueued` is omitted, unknown-alpha warning is missing, and preflight still does one full state load per item. |
| R-007 error-file-only Batch result ingest | ✅ **Landed** | Poller calls result download if either output or error file exists. |
| R-008 unknown/duplicate `custom_id` fail closed | ❌ **Did not land correctly** | Implementation logs/continues and mutates valid rows before discovering invalid IDs. Tests explicitly approve this wrong behavior. |
| R-009 Direct `x-request-id` propagation | ✅ **Landed** | HTTP header reaches provider candidate, job and metadata. |
| R-010 global Direct 401/403/404 stops run | ✅ **Landed with minor UX issue** | Cancellation is triggered; final status still says “completed”. |
| R-011 Ready Candidate full integrity verification | ⚠️ **Partial** | Verification exists at load and commit, but path containment and metadata/hash requirements are incomplete. |
| R-012 stored secret not auto-populated in textbox | ✅ **Landed** | Placeholder/configured UX implemented; blank OK preserves existing secret. |
| R-013 model-specific connection test | ✅ **Landed** | Generic `/models` fallback removed. |
| R-014 Direct retry setting controls production behavior | ⚠️ **Wired, semantics wrong** | Code adds `+1` to configured value, conflicting with repair plan / `MaxAttempts` wording. New tests approve the off-by-one interpretation. |
| R-015 Core coverage / mutation integration | ⚠️ **Partial** | New gates exist, but Core coverage lacks dynamic file inventory/method coverage and accepts branch threshold 73%; Stryker break threshold is only 65%. No run exists for HEAD. |
| R-016 bulk JobStore I/O | ⚠️ **Partial** | Queue rendering and initial bulk writes improved; `ApiPreflightService` still calls `GetItem()` per request, causing repeated complete JSON loads. |

---

# 3. Confirmed remaining issues

---

## F-001 — P0 — Batch `custom_id` validation is **not fail-closed**

### Affected file

```text
src/AssetProvenanceHelper/Services/BatchIngestionService.cs
```

### Current behavior

For each downloaded Batch result:

```csharp
var itemRecord = batchItems.FirstOrDefault(...);

if (itemRecord == null)
{
    Trace.TraceWarning(...);
    unknownCustomIds.Add(output.CustomId);
    continue;
}

if (!handledCustomIds.Add(output.CustomId))
{
    Trace.TraceWarning(...);
    duplicateCustomIds.Add(output.CustomId);
    continue;
}
```

Valid rows are processed immediately.

Therefore this input:

```text
row 1: valid custom_id A
row 2: UNKNOWN-ID
```

produces:

```text
A → staged and Ready
UNKNOWN-ID → warning
```

The result set was not validated before local mutation.

A duplicate behaves similarly:

```text
first A → processed
second A → ignored
```

### Why this is wrong

The repair plan explicitly required:

```text
validate the entire result set first
→ unknown custom_id = fail closed
→ duplicate custom_id = fail closed
→ only then mutate local items
```

This is an integrity boundary. Batch result mapping is the authority that connects a remote image to a local asset request.

### The new tests are wrong too

The new `BatchIngestionTests` encode the weak implementation instead of the repair contract:

```text
UnknownCustomId → summary contains unknown ID
DuplicateCustomId → “process only first”
```

Those tests must be replaced.

### Required fix

Add a pure validation pass before any staging or JobStore mutation.

#### Copy-ready helper

```csharp
private static IReadOnlyDictionary<string, GenerationItemRecord>
    ValidateBatchResultMapping(
        IReadOnlyList<GenerationItemRecord> batchItems,
        IReadOnlyList<BatchItemOutput> outputs)
{
    var expected =
        batchItems.ToDictionary(
            item => item.CustomId,
            StringComparer.Ordinal);

    var seen =
        new HashSet<string>(
            StringComparer.Ordinal);

    foreach (var output in outputs)
    {
        if (string.IsNullOrWhiteSpace(output.CustomId))
        {
            throw new InvalidDataException(
                "Batch result contains an empty custom_id.");
        }

        if (!seen.Add(output.CustomId))
        {
            throw new InvalidDataException(
                $"Batch result contains duplicate custom_id '{output.CustomId}'.");
        }

        if (!expected.ContainsKey(output.CustomId))
        {
            throw new InvalidDataException(
                $"Batch result contains unknown custom_id '{output.CustomId}'.");
        }
    }

    return expected;
}
```

At the start of `IngestResults(...)`:

```csharp
var batchItems =
    _jobStore.GetItemsForBatch(
        batch.LocalBatchId);

IReadOnlyDictionary<string, GenerationItemRecord> expected;

try
{
    expected =
        ValidateBatchResultMapping(
            batchItems,
            downloadResult.Items);
}
catch (InvalidDataException ex)
{
    var unresolved =
        batchItems
            .Where(item =>
                item.Status != GenerationItemStatus.Ready
                && item.Status != GenerationItemStatus.Committed)
            .Select(item =>
                item with
                {
                    Status =
                        GenerationItemStatus.UncertainAfterInterruption,
                    ErrorCode =
                        "batch_result_mapping_invalid",
                    ErrorMessage =
                        ex.Message,
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                })
            .ToList();

    _jobStore.UpsertItems(unresolved);

    _jobStore.UpsertBatch(
        batch with
        {
            Status =
                status.Status,
            ProviderOutputFileId =
                status.OutputFileId,
            ProviderErrorFileId =
                status.ErrorFileId,
            ErrorMessage =
                ex.Message,
            UpdatedAtUtc =
                DateTimeOffset.UtcNow,
            CompletedAtUtc =
                DateTimeOffset.UtcNow
        });

    throw;
}
```

After validation, lookup becomes:

```csharp
var itemRecord =
    expected[output.CustomId];
```

### Required tests

Replace the current permissive tests with:

```text
BatchResults_UnknownCustomId_FailsBeforeAnyCandidateMutation
BatchResults_DuplicateCustomId_FailsBeforeAnyCandidateMutation
BatchResults_EmptyCustomId_FailsBeforeAnyCandidateMutation
BatchResults_CustomIdCaseChanged_IsUnknown
BatchResults_ValidOutOfOrder_StillMapsCorrectly
```

Critical assertion:

```csharp
Assert.DoesNotContain(
    jobStore.Load().Items,
    item => item.Status == GenerationItemStatus.Ready);

Assert.Empty(
    Directory.GetFiles(
        staging.BaseStagingPath,
        "*.png",
        SearchOption.AllDirectories));
```

---

## F-002 — P0 — Remote Batch may be submitted twice after local Batch-ID persistence failure

### Affected files

```text
src/AssetProvenanceHelper/MainForm.ApiGeneration.cs
src/AssetProvenanceHelper/Services/ApiPreflightService.cs
src/AssetProvenanceHelper/MainForm.ApiGenerationUi.cs
```

### What was fixed

The code correctly does:

```text
upload
→ save ProviderInputFileId
→ CreateBatch
→ save ProviderBatchId
```

and it does not automatically call `CreateBatchAsync()` twice in the same method.

### Remaining failure

Before upload, items are persisted as:

```csharp
Status = GenerationItemStatus.BatchQueued
```

If:

```text
CreateBatch succeeds remotely
→ ProviderBatchId save fails locally
```

the catch only warns the user.

The durable item records remain:

```text
BatchQueued
```

But `ApiPreflightService.IsJobActiveOrInFlight(...)` does **not** include `BatchQueued`.

The queue UI also does not render `BatchQueued`.

Therefore:

```text
remote Batch is already running
→ local remote-ID save fails
→ method returns
→ user clicks Queue Production Batch again
→ preflight considers the BatchQueued items eligible
→ same assets can be remotely submitted again
```

This is exactly the duplicate-cost condition R-003 was meant to prevent.

### Required fix A — treat `BatchQueued` as active

```csharp
public static bool IsJobActiveOrInFlight(
    GenerationItemRecord job)
{
    return job.Status is
        GenerationItemStatus.DirectInFlight
        or GenerationItemStatus.QueuedDirect
        or GenerationItemStatus.DirectRateLimited
        or GenerationItemStatus.BatchPreparing
        or GenerationItemStatus.BatchQueued
        or GenerationItemStatus.BatchSubmitted
        or GenerationItemStatus.BatchRunning
        or GenerationItemStatus.Preparing
        or GenerationItemStatus.Normalizing
        or GenerationItemStatus.Downloading;
}
```

### Required fix B — show `BatchQueued`

In `GetRequestItemVisualStatus(...)`:

```csharp
if (job.Status is
    GenerationItemStatus.BatchQueued
    or GenerationItemStatus.BatchSubmitted)
{
    return ("Batch queued", BatchRowBackColor);
}
```

### Required fix C — best-effort mark affected items uncertain when remote ID cannot be persisted

Inside `catch (Exception persistBatchIdEx)`:

```csharp
try
{
    var uncertainItems =
        batchItemsToQueue
            .Select(item =>
                item with
                {
                    Status =
                        GenerationItemStatus.UncertainAfterInterruption,
                    ErrorCode =
                        "remote_batch_id_persistence_failed",
                    ErrorMessage =
                        $"OpenAI created remote batch '{result.ProviderBatchId}', "
                        + "but the local Batch ID could not be persisted. "
                        + "Do not resubmit automatically.",
                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                })
            .ToList();

    _generationJobStore.UpsertItems(
        uncertainItems);
}
catch
{
    // BatchQueued was already durably stored before remote mutation.
    // Preflight MUST treat it as active so even this secondary
    // persistence failure cannot allow an immediate duplicate submission.
}
```

The existing warning should be upgraded from `Warning` to a critical/error message:

```text
OpenAI accepted remote batch: <id>

Local recovery state could not be saved.

DO NOT submit these requests again until the remote batch has been checked.
```

### Required test

```text
Batch_RemoteCreated_BatchIdSaveFails_SecondButtonClickDoesNotCreateSecondRemoteBatch
```

Test algorithm:

1. import one-item manifest;
2. fake upload returns `file_123`;
3. fake CreateBatch returns `batch_456`;
4. inject failure only when trying to save `ProviderBatchId`;
5. first Queue Batch click;
6. assert `CreateBatchCallCount == 1`;
7. click Queue Batch again;
8. assert **still** `CreateBatchCallCount == 1`;
9. assert row is `Batch queued` or `Uncertain`, never `Pending`.

---

## F-003 — P1 — Raw-before-normalize landed, but production failures discard the raw recovery authority

### Affected files

```text
src/AssetProvenanceHelper/MainForm.ApiGeneration.cs
src/AssetProvenanceHelper/Services/BatchIngestionService.cs
```

### Current good sequence

Direct now does:

```text
provider success
→ SaveRawCandidate
→ persist Normalizing + raw path
→ normalize
→ CompleteCandidate
→ Ready
```

That part is correct.

### Remaining bug

The Direct catch uses the **original** `itemRecord`:

```csharp
catch (Exception ex)
{
    _generationJobStore.UpsertItem(
        itemRecord with
        {
            Status = FailedPermanent,
            ...
        });
}
```

`itemRecord` does not contain:

```text
CandidateId
ProviderRawPath
RawSha256
ProviderRequestId
```

So a normalization/final-staging failure can overwrite the richer `Normalizing` state and lose the only durable pointer to the already-paid raw output.

Batch ingestion has the same pattern:

```csharp
_jobStore.UpsertItem(
    itemRecord with
    {
        Status = FailedPermanent,
        ErrorCode = "normalization_error"
    });
```

### Consequence

The raw `.raw.png` remains on disk, but the job record forgets how to find/use it.

A later Generate Now can create another paid request.

### Required fix

Maintain a current record variable.

#### Direct example

```csharp
var currentRecord =
    itemRecord;

try
{
    using var permit =
        await rateLimiter
            .AcquireAsync(token)
            .ConfigureAwait(false);

    acquiredPermit = true;

    currentRecord =
        currentRecord with
        {
            Status =
                GenerationItemStatus.DirectInFlight,
            UpdatedAtUtc =
                DateTimeOffset.UtcNow
        };

    _generationJobStore.UpsertItem(
        currentRecord);

    var candidate =
        await _imageGenerationProvider
            .GenerateAsync(
                spec,
                apiKey,
                token)
            .ConfigureAwait(false);

    var rawSha =
        ComputeSha256(candidate.RawBytes);

    var rawPath =
        _stagingService.SaveRawCandidate(
            run.ManifestFingerprint,
            item.RequestKey,
            candidate.CandidateId,
            candidate.RawBytes);

    currentRecord =
        currentRecord with
        {
            Status =
                GenerationItemStatus.Normalizing,
            CandidateId =
                candidate.CandidateId,
            ProviderRawPath =
                rawPath,
            RawSha256 =
                rawSha,
            ProviderRequestId =
                candidate.ProviderRequestId,
            UpdatedAtUtc =
                DateTimeOffset.UtcNow
        };

    _generationJobStore.UpsertItem(
        currentRecord);

    // normalize and complete...
}
catch (Exception ex)
{
    var providerOutputWasPersisted =
        !string.IsNullOrWhiteSpace(
            currentRecord.ProviderRawPath);

    _generationJobStore.UpsertItem(
        currentRecord with
        {
            Status =
                providerOutputWasPersisted
                    ? GenerationItemStatus.FailedRetryable
                    : GenerationItemStatus.FailedPermanent,
            ErrorCode =
                providerOutputWasPersisted
                    ? "local_candidate_processing_failed"
                    : "direct_failed",
            ErrorMessage =
                ex.Message,
            UpdatedAtUtc =
                DateTimeOffset.UtcNow
        });
}
```

Important: a locally retryable record with a saved raw provider output must **not** become automatically eligible for a new remote generation. See F-004.

### Tests

The current `ApiStagingDurabilityTests` manually simulate preserving the richer record. They do not prove the production Direct worker does so.

Add actual worker/controller tests:

```text
Direct_NormalizationFails_JobRetainsCandidateIdRawPathRawHashAndRequestId
Direct_FinalPromoteFails_JobRetainsRawRecoveryAuthority
Batch_NormalizationFails_JobRetainsRawRecoveryAuthority
Batch_FinalPromoteFails_JobRetainsRawRecoveryAuthority
```

---

## F-004 — P1 — Crash during `Normalizing` produces a permanently stuck item

### Affected files

```text
src/AssetProvenanceHelper.Core/Generation/GenerationJobStore.cs
src/AssetProvenanceHelper/Services/ApiPreflightService.cs
```

### Current behavior

Preflight considers:

```csharp
GenerationItemStatus.Normalizing
```

active.

But startup recovery only converts:

```text
DirectInFlight → Uncertain
BatchPreparing/BatchQueued/... → Uncertain
```

It does **not** handle `Normalizing`.

Therefore:

```text
provider raw file successfully saved
→ job persisted as Normalizing
→ app crashes
→ restart
→ job stays Normalizing
→ UI says Generating
→ preflight always skips it
→ no worker exists
→ item is stuck forever
```

### Best fix

Do not remote-regenerate.

Use the already saved raw provider file to recover locally.

### Recommended implementation

Add:

```text
LocalCandidateRecoveryService
```

in the WinForms/app layer because normalization uses `System.Drawing`.

#### Recovery eligibility

```csharp
private static bool CanRecoverLocally(
    GenerationItemRecord job)
{
    return job.Status is
        GenerationItemStatus.Normalizing
        or GenerationItemStatus.FailedRetryable
        && !string.IsNullOrWhiteSpace(job.CandidateId)
        && !string.IsNullOrWhiteSpace(job.ProviderRawPath)
        && File.Exists(job.ProviderRawPath);
}
```

Prefer explicit parentheses in production:

```csharp
return
    (job.Status == GenerationItemStatus.Normalizing
     || (job.Status == GenerationItemStatus.FailedRetryable
         && string.Equals(
             job.ErrorCode,
             "local_candidate_processing_failed",
             StringComparison.Ordinal)))
    && !string.IsNullOrWhiteSpace(job.CandidateId)
    && !string.IsNullOrWhiteSpace(job.ProviderRawPath)
    && File.Exists(job.ProviderRawPath);
```

#### Recovery flow

```text
read existing raw bytes
→ verify raw SHA if present
→ validate PNG + provider dimensions
→ compute ImageSizePlan
→ remove only incomplete final/metadata for same candidate if necessary
→ normalize locally
→ write final + metadata
→ mark Ready
→ zero provider HTTP
```

Call recovery:

1. on application startup after JobStore recovery;
2. immediately before Direct/Batch paid preflight.

Then re-run preflight.

### If local recovery itself fails

Keep:

```text
FailedRetryable + ErrorCode=local_candidate_processing_failed
```

but preflight must explicitly exclude any job that has saved provider raw output.

Never treat it as a normal remote-generation retry.

### Required tests

```text
Restart_NormalizingWithRaw_AutoRecoversToReady_ZeroProviderCalls
Restart_NormalizingMissingRaw_BecomesUncertain_NotEligible
LocalProcessingFailure_GenerateAgain_RetriesLocallyNotRemotely
CrashAfterNormalizedWriteBeforeReady_RecoversWithoutRemoteCall
BatchNormalizingCrash_RecoversLocally
```

---

## F-005 — P1 — Ready job with missing staged image becomes silently eligible for another paid generation

### Affected file

```text
src/AssetProvenanceHelper/Services/ApiPreflightService.cs
```

### Current code

It only recognizes Ready if:

```csharp
job.Status == Ready
&& job.StagedOutputPath not empty
&& File.Exists(job.StagedOutputPath)
```

If status is Ready but the local file has disappeared:

```text
Ready + missing file
→ Ready branch not taken
→ not active
→ not uncertain
→ request becomes Eligible
```

Generate Now can therefore create a second paid result without explicitly telling the user the first Ready Candidate was locally lost/corrupted.

### Required fix

Any `Ready` state must stop remote generation.

```csharp
if (job.Status == GenerationItemStatus.Ready)
{
    alreadyReadyCount++;

    if (string.IsNullOrWhiteSpace(job.StagedOutputPath)
        || !File.Exists(job.StagedOutputPath))
    {
        errors.Add(
            new ApiPreflightIssue(
                item.RequestKey,
                item.FileName,
                "ready_candidate_missing",
                "This request is recorded as Ready, but its staged candidate "
                + "is missing. Remote regeneration is blocked until the local "
                + "candidate state is explicitly resolved."));
    }

    continue;
}
```

Prefer running `CandidateVerificationService` from a higher-level preflight coordinator for all Ready records.

### Tests

```text
Preflight_ReadyMissingFile_IsErrorAndNotEligible
Preflight_ReadyMissingMetadata_IsErrorAndNotEligible
Preflight_ReadyHashMismatch_IsErrorAndNotEligible
GenerateNow_ReadyMissingFile_PerformsZeroProviderCalls
```

---

## F-006 — P1 — Candidate verification is present but not strict enough

### Affected file

```text
src/AssetProvenanceHelper/Services/CandidateVerificationService.cs
```

### Problem A — path containment uses unsafe prefix comparison

Current pattern:

```csharp
fullStagedPath.StartsWith(
    expectedDir,
    StringComparison.OrdinalIgnoreCase)
```

If expected directory is:

```text
C:\...\request123
```

this path also passes:

```text
C:\...\request123_evil\candidate.png
```

### Problem B — normalized SHA is optional

The verifier only compares hashes if strings are non-empty.

A metadata file with missing `NormalizedSha256` can therefore skip the final-file integrity check.

`GeneratedImageStagingService.LoadMetadata()` also defaults missing fields to empty strings, making truncated metadata appear more valid than it is.

### Problem C — important metadata identity is not compared

Verifier should compare at least:

```text
metadata.CandidateId == job.CandidateId
metadata.CustomId == job.CustomId
metadata.Model == job.Model
metadata target resolution == job target
metadata provider resolution == job generation dimensions
metadata mode matches Direct/Batch
metadata NormalizedSha256 == job NormalizedSha256
```

### Problem D — raw output integrity is not checked

If `ProviderRawPath` exists, verify:

```text
actual raw SHA == job.RawSha256 == metadata.RawSha256
```

### Problem E — Direct trusts provider-supplied `candidate.RawSha256`

The app should compute its own SHA from `candidate.RawBytes`.

### Required helper

```csharp
private static bool IsValidSha256(
    string? value)
{
    return
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 64
        && value.All(Uri.IsHexDigit);
}
```

### Exact path verification

Best: expose exact expected final path from staging service.

```csharp
public string GetFinalCandidatePath(
    string manifestFingerprint,
    string requestKey,
    string candidateId)
{
    var safeCandidateId =
        SanitizePathSegment(candidateId);

    return Path.Combine(
        GetItemDirectory(
            manifestFingerprint,
            requestKey),
        $"{safeCandidateId}.png");
}
```

Verifier:

```csharp
var expectedPath =
    Path.GetFullPath(
        _stagingService.GetFinalCandidatePath(
            job.ManifestFingerprint,
            job.RequestKey,
            job.CandidateId));

var actualPath =
    Path.GetFullPath(
        job.StagedOutputPath);

if (!string.Equals(
        expectedPath,
        actualPath,
        StringComparison.OrdinalIgnoreCase))
{
    return Fail(
        "staged_path_invalid",
        "Staged candidate path does not match the candidate's expected path.");
}
```

### Required normalized-hash validation

```csharp
if (!IsValidSha256(job.NormalizedSha256)
    || !IsValidSha256(metadata.NormalizedSha256))
{
    return Fail(
        "normalized_hash_missing",
        "Candidate normalized SHA-256 is missing or invalid.");
}

if (!string.Equals(
        job.NormalizedSha256,
        metadata.NormalizedSha256,
        StringComparison.OrdinalIgnoreCase))
{
    return Fail(
        "metadata_hash_mismatch",
        "Job and metadata normalized SHA-256 values do not match.");
}

var actualFinalSha =
    ComputeSha256File(actualPath);

if (!string.Equals(
        actualFinalSha,
        job.NormalizedSha256,
        StringComparison.OrdinalIgnoreCase))
{
    return Fail(
        "file_hash_mismatch",
        "Staged candidate bytes do not match the recorded SHA-256.");
}
```

### Required tests

```text
Candidate_SiblingPrefixPath_IsRejected
Candidate_EmptyJobHash_IsRejected
Candidate_MissingMetadataHash_IsRejected
Candidate_MalformedHash_IsRejected
Candidate_CustomIdMismatch_IsRejected
Candidate_ModelMismatch_IsRejected
Candidate_TargetResolutionMismatch_IsRejected
Candidate_ProviderResolutionMismatch_IsRejected
Candidate_RawHashMismatch_IsRejected
Candidate_ExactValidBundle_Passes
```

---

## F-007 — P1 — API Candidate precedence is bypassed by Variants

### Affected files

```text
src/AssetProvenanceHelper/MainForm.DirectMode.cs
src/AssetProvenanceHelper/MainForm.MainWorkflow.cs
src/AssetProvenanceHelper/MainForm.Variants.cs
```

### Direct guard did land

This is correct:

```csharp
if (_activeApiCandidateMetadata is not null)
{
    HandleMainImage();
    return;
}
```

### But `HandleMainImage()` begins with:

```csharp
var variantCount =
    GetSelectedVariantCount();

if (variantCount > 0)
{
    HandleVariantBatch(
        variantCount);
    return;
}
```

That happens **before** API Candidate verification.

`HandleVariantBatch` then resolves N images from Downloads and does:

```csharp
SetSelectedImage(
    ImageSlot.Main,
    mains[i - 1]);
```

which clears active API metadata.

Result:

```text
API Candidate Ready
+ Variants > none
→ API Candidate ignored
→ download-folder variant images committed instead
```

This is the same class of wrong-image risk as the Direct-mode issue, through a different legacy entry path.

### Best minimal fix

API automation does not create multiple API variants in the current release.

Therefore when an API Candidate is bound:

- Variants must not be used for that commit.
- Do not modify the legacy Variants workflow for non-API assets.

At `HandleMainImage()`:

```csharp
var hasApiCandidate =
    _activeApiCandidateMetadata is not null;

var variantCount =
    GetSelectedVariantCount();

if (hasApiCandidate && variantCount > 0)
{
    ShowMessageBox(
        "A staged API Candidate is active. "
        + "Variants applies to the legacy download-folder workflow and cannot "
        + "be combined with this API Candidate. Set Variants to 'none' or "
        + "unload the API Candidate first.",
        "Variants unavailable for API Candidate",
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);

    return;
}

if (variantCount > 0)
{
    HandleVariantBatch(
        variantCount);

    return;
}
```

Optionally, when loading Ready:

```csharp
ResetVariantSelectionToNone();
```

Do **not** alter legacy variants when no API Candidate is active.

### Tests

```text
ApiCandidate_VariantsSelected_DoesNotCommitDownloads
ApiCandidate_VariantsSelected_DoesNotClearApiMetadata
NoApiCandidate_VariantsBehaviorUnchanged
```

---

## F-008 — P1 — Editing the request-bound Prompt/Asset Name leaves the API image selected

### Affected file

```text
src/AssetProvenanceHelper/MainForm.RequestQueue.cs
```

### Current code

When text no longer matches the request:

```csharp
_activeRequest = null;
_activeApiCandidateMetadata = null;

RefreshRequestQueueVisuals();
```

It does **not** clear the selected Main image.

### Consequence

Workflow:

```text
Ready API Candidate loaded
→ generated from Prompt A
→ user edits prompt to Prompt B
→ request binding and API metadata cleared
→ staged image remains selected
→ user commits it as a manual/no-reference image
→ final provenance can record Prompt B for an image generated from Prompt A
```

The original queue request also remains unfinished because `_activeRequest` was cleared.

### Required fix

```csharp
var hadApiCandidate =
    _activeApiCandidateMetadata is not null;

_activeRequest = null;
_activeApiCandidateMetadata = null;

if (hadApiCandidate)
{
    SetSelectedImage(
        ImageSlot.Main,
        null);
}

RefreshRequestQueueVisuals();
```

### Tests

```text
ReadyApiCandidate_EditPrompt_ClearsMetadataAndMainImage
ReadyApiCandidate_EditAssetName_ClearsMetadataAndMainImage
ManualNonApiImage_EditPrompt_OldManualBehaviorUnchanged
```

---

## F-009 — P1 — Provider dropdown can contradict API provenance

### Affected file

```text
src/AssetProvenanceHelper/MainForm.MainWorkflow.cs
```

Ready activation selects:

```text
OpenAI API.md
```

but the user can later select another provider template.

`CommitNoReferenceAsset()` currently calls:

```csharp
GetProviderSnapshotForNewAsset()
```

which uses the **current UI provider selection**.

Then API metadata is added to that session.

Possible final provenance:

```text
top-level provider template: ChatGPT
API Provider: OpenAI
Mode: direct
Provider Request ID: req_...
```

That is internally contradictory.

### Minimal required fix

When `_activeApiCandidateMetadata != null`, use the OpenAI API template explicitly, independent of dropdown.

Helper:

```csharp
private ProviderTemplateSnapshot
    GetOpenAiApiProviderSnapshot()
{
    var catalog =
        _providerTemplateCatalogService.Load();

    var definition =
        catalog.Templates.SingleOrDefault(
            template =>
                string.Equals(
                    template.FileName,
                    "OpenAI API.md",
                    StringComparison.OrdinalIgnoreCase));

    if (definition is null)
    {
        throw new InvalidOperationException(
            "OpenAI API provider template is missing or invalid.");
    }

    return definition.CreateSnapshot();
}
```

Commit:

```csharp
var providerSnapshot =
    _activeApiCandidateMetadata is not null
        ? GetOpenAiApiProviderSnapshot()
        : GetProviderSnapshotForNewAsset();

session =
    _assetProcessorService
        .CreateNoReferenceMainSession(
            settings,
            assetName,
            sourceImage,
            prompt,
            processedAt,
            providerSnapshot,
            _activeRequest?.RequestKey);
```

The optional stronger H-01 solution is to snapshot the API provider template at generation time. The minimal fix above is enough to prevent dropdown contradiction.

### Tests

```text
ReadyApiCandidate_ProviderDropdownChanged_CommitStillUsesOpenAiApiTemplate
ManualImage_ProviderDropdownStillControlsProvider
```

---

## F-010 — P2 — Provider output/error file IDs are not persisted into final Batch record

### Affected file

```text
src/AssetProvenanceHelper/Services/BatchIngestionService.cs
```

`GenerationBatchRecord` has:

```csharp
ProviderOutputFileId
ProviderErrorFileId
```

but `IngestResults(...)` final `UpsertBatch(...)` does not assign:

```csharp
status.OutputFileId
status.ErrorFileId
```

### Why it matters

The original Batch-state design includes these IDs for recovery/audit.

### Fix

```csharp
_jobStore.UpsertBatch(
    batch with
    {
        Status =
            status.Status,
        ProviderOutputFileId =
            status.OutputFileId,
        ProviderErrorFileId =
            status.ErrorFileId,
        CompletedCount =
            status.CompletedCount,
        FailedCount =
            status.FailedCount,
        CompletedAtUtc =
            DateTimeOffset.UtcNow,
        UpdatedAtUtc =
            DateTimeOffset.UtcNow,
        ErrorMessage =
            combinedError
    });
```

Prefer storing IDs immediately after terminal status retrieval, before download.

### Tests

```text
BatchIngestion_PersistsOutputFileId
BatchIngestion_PersistsErrorFileId
BatchIngestion_PersistsBothFileIds
```

---

## F-011 — P2 — `DirectRetryAttempts` is wired with an off-by-one interpretation

### Affected file

```text
src/AssetProvenanceHelper.Core/Generation/Providers/OpenAi/OpenAiImageGenerationProvider.cs
```

Current code effectively does:

```csharp
new RetryPolicy(
    spec.RetryAttempts.Value + 1)
```

The repair plan defined the desired end-to-end tests as:

```text
setting 1 → exactly 1 HTTP attempt
setting 3 → at most 3 HTTP attempts
```

and the original retry section describes:

```text
MaxAttempts = 3
```

### New tests currently approve the opposite behavior

They assert:

```text
configured 1 → 2 total attempts
configured 2 → 3 total attempts
```

### Best correction

Remove ambiguity in both code and UI.

Rename UI label:

```text
Max direct API attempts:
```

Keep stored property name for backward compatibility if desired.

Map directly:

```csharp
var effectivePolicy =
    spec.RetryAttempts.HasValue
        ? new RetryPolicy(
            Math.Max(
                1,
                spec.RetryAttempts.Value))
        : null;
```

### Tests

```text
DirectRetryAttempts_1_PerformsExactlyOneHttpAttempt
DirectRetryAttempts_3_PerformsAtMostThreeHttpAttempts
SettingsChangedMidRun_CurrentRunKeepsCapturedAttemptLimit
```

If the product intentionally wants **3 retries + initial request**, keep current code but then rename the setting to `DirectRetriesAfterInitialRequest` and update the original product contract. Do not keep the current ambiguous mismatch.

---

## F-012 — P2 — Preflight still performs a full JobStore read once per request

### Affected file

```text
src/AssetProvenanceHelper/Services/ApiPreflightService.cs
```

Current loop:

```csharp
foreach (var item in pendingItems)
{
    var job =
        _jobStore.GetItem(
            manifestFingerprint,
            item.RequestKey);
}
```

`GetItem()` loads/deserializes the complete `generation-jobs.json`.

So a 1000-item preflight still performs approximately:

```text
1000 full state-file reads/deserializations
```

The queue rendering bulk optimization landed; preflight did not adopt it.

### Fix

```csharp
var jobsByRequestKey =
    _jobStore
        .GetItemsForManifest(
            manifestFingerprint)
        .ToDictionary(
            item => item.RequestKey,
            StringComparer.Ordinal);

foreach (var item in pendingItems)
{
    jobsByRequestKey.TryGetValue(
        item.RequestKey,
        out var job);

    // existing logic...
}
```

### Test seam

Add an internal load hook or refactor `LoadCore()`:

```csharp
internal static Action?
    OnAfterLoadCoreForTests;
```

Test:

```text
Preflight_1000Items_LoadsJobStateExactlyOnce
```

Do not use a fragile wall-clock threshold.

---

## F-013 — P2 — Core coverage gate is weaker than the intended quality gate

### Affected files

```text
scripts/verify_core_coverage.ps1
code-coverage-core-baseline.json
```

### Issue A — no dynamic production-file inventory

The script reads aggregate Cobertura counts but does not enumerate:

```text
src/AssetProvenanceHelper.Core/**/*.cs
```

and ensure every executable production file exists in the coverage report.

A new Core file can therefore potentially be outside the denominator.

### Issue B — no method coverage enforcement

The existing app gate tracks methods. Core gate only tracks line and branch totals.

### Issue C — branch threshold mismatch

Script documentation says:

```text
>= 75% branches
```

but committed baseline contains:

```json
"minBranchRate": 0.73
```

and the script trusts that value.

### Required fix

Reuse the existing app coverage-gate design:

```text
dynamic Core source inventory
exact line counts
exact branch counts
method counts
uncovered-count ratchet
enumerated exclusions
```

At minimum enforce:

```json
{
  "minLineRate": 0.80,
  "minBranchRate": 0.75
}
```

Do not lower the threshold just to accept the current result.

### Test

Add script self-tests:

```text
new Core .cs file absent from Cobertura → gate fails
uncovered method added → gate fails
branch rate 74.9% → gate fails
branch rate 75.0% → allowed if ratchet also passes
```

---

## F-014 — P2 — Core mutation gate threshold was weakened to 65%

### Affected file

```text
tests/AssetProvenanceHelper.Core.Tests/stryker-config.json
```

Current:

```json
"break": 65
```

Repair plan example:

```json
"break": 80
```

### Fix

Use:

```json
"thresholds": {
  "high": 90,
  "low": 80,
  "break": 80
}
```

If current mutation score cannot reach 80, add tests. Do not lower the gate.

Also move Core-only tests such as `GenerationJobStoreBulkTests` into:

```text
tests/AssetProvenanceHelper.Core.Tests/
```

when they do not need WinForms.

That ensures the dedicated Core test project and Core coverage gate actually exercise the new Core behavior.

---

## F-015 — P2 — ProtectedData package was added to the wrong project and at the wrong version

### Current state

`AssetProvenanceHelper.Core.csproj` contains:

```xml
<PackageReference
    Include="System.Security.Cryptography.ProtectedData"
    Version="9.0.2" />
```

The WinForms app project contains no direct package reference.

### Contract

The plan says:

- Windows-specific DPAPI remains in the app project.
- Core stays headless/provider/state logic.
- planned package line is version 10.0.x / 10.0.11.

### Fix

Remove from Core:

```xml
<PackageReference
    Include="System.Security.Cryptography.ProtectedData"
    Version="9.0.2" />
```

Add to:

```text
src/AssetProvenanceHelper/AssetProvenanceHelper.csproj
```

using the repository-approved .NET 10 package version:

```xml
<ItemGroup>
  <PackageReference
    Include="System.Security.Cryptography.ProtectedData"
    Version="10.0.11" />
</ItemGroup>
```

Then run restore/build with warnings-as-errors.

### Tests

```text
Core project restores without ProtectedData dependency
App DpapiSecretStore tests pass
published win-x64 package contains required dependency
```

---

## F-016 — P2 — API buttons are enabled when no API key is configured

### Affected file

```text
src/AssetProvenanceHelper/MainForm.RequestQueue.cs
```

Current:

```csharp
var canRunApi =
    _currentManifest is not null
    && !apiMutationActive;
```

The original WinForms test matrix requires:

```text
Generate disabled without manifest/key
```

The handler later shows a warning, but the button-state contract is not implemented.

### Fix

```csharp
private bool HasOpenAiApiKeyConfigured()
{
    try
    {
        return
            !string.IsNullOrWhiteSpace(
                _secretStore.LoadSecret(
                    SettingsDialog.OpenAiApiKeySecretName));
    }
    catch
    {
        return false;
    }
}
```

State:

```csharp
var canRunApi =
    _currentManifest is not null
    && !apiMutationActive
    && HasOpenAiApiKeyConfigured();

btnGenerateNow.Enabled =
    canRunApi;

btnQueueProductionBatch.Enabled =
    canRunApi;
```

Tooltip without key:

```text
Configure an OpenAI API key in Settings first.
```

### Tests

```text
ApiButtons_NoManifest_Disabled
ApiButtons_ManifestButNoKey_Disabled
ApiButtons_ManifestAndKey_Enabled
ApiButtons_ReferenceReady_Disabled
ApiButtons_DirectRunActive_Disabled
ApiButtons_BatchSubmitting_Disabled
```

---

## F-017 — P2 — `alpha=unknown` warning required by the original plan is missing

### Affected file

```text
src/AssetProvenanceHelper/Services/ApiPreflightService.cs
```

`warnings` is created but never populated.

Original MVP behavior:

```text
alpha required → blocked
alpha not_required → allowed
alpha unknown → allowed + preflight warning
```

### Fix

```csharp
if (item.Alpha == AlphaRequirement.Unknown)
{
    warnings.Add(
        new ApiPreflightIssue(
            item.RequestKey,
            item.FileName,
            "alpha_requirement_unknown",
            "Alpha requirement is unknown. "
            + "GPT-Image-2 generation in this release uses opaque output."));
}
```

Then keep the item eligible.

Confirmation must show e.g.:

```text
Alpha unknown: 17
These requests will be generated as opaque output.
```

### Tests

Replace:

```text
Preflight_UnknownAlpha_Eligible
```

with:

```text
Preflight_UnknownAlpha_EligibleAndWarned
```

---

## F-018 — P2 — Raw provider PNG format / reasonable file-size validation is incomplete

### Affected file

```text
src/AssetProvenanceHelper/Services/ImageNormalizationService.cs
```

Current checks:

```text
non-empty
Image.FromStream succeeds
dimensions match
```

Missing from the original raw validation:

```text
expected provider format = PNG
reasonable byte-size ceiling
```

A JPEG with matching dimensions is accepted, then stored as:

```text
<candidate>.raw.png
```

even though its bytes are not PNG.

### Fix

Add PNG signature validation before decode:

```csharp
private static readonly byte[] PngSignature =
[
    0x89, 0x50, 0x4E, 0x47,
    0x0D, 0x0A, 0x1A, 0x0A
];

private static void ValidateProviderPng(
    byte[] rawBytes)
{
    if (rawBytes.Length < PngSignature.Length
        || !rawBytes
            .AsSpan(
                0,
                PngSignature.Length)
            .SequenceEqual(
                PngSignature))
    {
        throw new InvalidDataException(
            "Provider output is not a PNG image.");
    }
}
```

Use a documented defensive maximum, e.g. centralized constant:

```csharp
public const int MaxProviderImageBytes =
    100 * 1024 * 1024;
```

If a different maximum is preferred, keep it centralized and test it.

### Tests

```text
Normalization_JpegBytesWithCorrectDimensions_IsRejected
Normalization_CorruptPngSignature_IsRejected
Normalization_OversizedProviderPayload_IsRejected
Normalization_ValidPng_Passes
```

---

## F-019 — P2 — API provenance template still omits explicit planned fields

### Affected file

```text
src/AssetProvenanceHelper/provider_templates/OpenAI API.md
```

The original plan explicitly calls for:

```text
Generation channel: OpenAI API
Provider
Model
Generation mode
Provider request ID
Batch ID
Provider resolution
Final normalized resolution
Post-processing: deterministic crop/resize by helper
Raw output SHA-256
```

Current template has most data, but not an explicit:

```text
Generation channel
Post-processing
```

and labels Target Resolution rather than clearly stating Final normalized resolution.

### Minimal fix

No new tag is needed for fixed MVP facts:

```markdown
## Automated API Generation Details

Generation channel: OpenAI API
API Provider: <<<API_PROVIDER>>>
Model: <<<API_MODEL>>>
Generation mode: <<<API_MODE>>>
Provider Request ID: <<<API_PROVIDER_REQUEST_ID>>>
Batch ID: <<<API_BATCH_ID>>>
Provider resolution: <<<API_PROVIDER_RESOLUTION>>>
Final normalized resolution: <<<API_TARGET_RESOLUTION>>>
Post-processing: deterministic center-crop/resize by AI Asset Provenance Helper
Raw output SHA-256: <<<API_RAW_SHA256>>>
Final normalized SHA-256: <<<API_NORMALIZED_SHA256>>>
Candidate ID: <<<API_CANDIDATE_ID>>>
Custom ID: <<<API_CUSTOM_ID>>>
Generated At (UTC): <<<API_CREATED_AT_UTC>>>
```

### Test

Render a Direct and Batch provenance document and assert every planned field/label exists once.

---

## F-020 — P2 — DPAPI corruption/decryption failures are silently treated as an empty secret store

### Affected file

```text
src/AssetProvenanceHelper/Services/DpapiSecretStore.cs
```

Current:

```csharp
catch
{
    return new Dictionary<string, string>(
        StringComparer.Ordinal);
}
```

A corrupted or undecryptable secret file looks identical to “no key configured”.

A later Save can overwrite the store.

### Fix

Only treat **file absent** as empty.

Corruption/decryption failure should throw a clear exception.

```csharp
private Dictionary<string, string>
    LoadEncryptedDictionary()
{
    if (!File.Exists(_storagePath))
    {
        return new Dictionary<string, string>(
            StringComparer.Ordinal);
    }

    try
    {
        var encryptedBytes =
            File.ReadAllBytes(
                _storagePath);

        if (encryptedBytes.Length == 0)
        {
            throw new InvalidDataException(
                "Secret store exists but is empty.");
        }

        var decryptedBytes =
            ProtectedData.Unprotect(
                encryptedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

        var json =
            Encoding.UTF8.GetString(
                decryptedBytes);

        return
            JsonSerializer.Deserialize<
                Dictionary<string, string>>(json)
            ?? throw new InvalidDataException(
                "Secret store could not be deserialized.");
    }
    catch (Exception ex)
    {
        throw new InvalidDataException(
            $"Secret store '{_storagePath}' could not be decrypted or parsed. "
            + "It was left unchanged.",
            ex);
    }
}
```

### Tests

```text
SecretStore_CorruptFile_LoadThrowsAndDoesNotDelete
SecretStore_CorruptFile_SaveDoesNotOverwrite
SecretStore_WrongDpapiContext_LoadFailsExplicitly
SecretStore_MissingFile_IsEmpty
```

---

## F-021 — P2 — exhausted transient Direct failures are recorded as `FailedPermanent`

### Affected file

```text
src/AssetProvenanceHelper/MainForm.ApiGeneration.cs
```

Any non-global exception falls into:

```csharp
Status = FailedPermanent
```

even if it is:

```text
429 after max attempts
503 after max attempts
temporary network failure after max attempts
```

The domain already has:

```text
FailedRetryable
```

### Fix

Add classification:

```csharp
private static bool IsRetryableDirectFailure(
    Exception ex)
{
    if (ex is OpenAiApiException api)
    {
        return RetryPolicy
            .IsRetryableStatusCode(
                api.StatusCode);
    }

    return RetryPolicy
        .IsRetryableException(
            ex);
}
```

Then:

```csharp
var retryable =
    IsRetryableDirectFailure(ex);

Status =
    retryable
        ? GenerationItemStatus.FailedRetryable
        : GenerationItemStatus.FailedPermanent;
```

This is status truthfulness. Do not automatically send it again in the same run.

### Tests

```text
Direct_429Exhausted_StatusFailedRetryable
Direct_503Exhausted_StatusFailedRetryable
Direct_400_StatusFailedPermanent
Direct_PromptUserError_StatusFailedPermanent
```

---

# 4. Lower-priority remaining issues

---

## F-022 — P3 — global-error run still logs “Direct API generation completed.”

After `Task.WhenAll(tasks)` the code always adds:

```text
Direct API generation completed.
```

even if a global 401/403/404 halted the run.

### Fix

```csharp
if (Volatile.Read(ref globalErrorSignaled) == 0)
{
    SafeInvoke(
        () => AddStatus(
            "Direct API generation completed."));
}
else
{
    SafeInvoke(
        () => AddStatus(
            "Direct API generation halted due to a global provider error."));
}
```

---

## F-023 — P3 — Batch output/error download is all-or-nothing

`DownloadBatchResultsAsync` loads output and error files, then parses.

If:

```text
output file download succeeds
error file download fails
```

the already-downloaded successful output is discarded for that poll.

The poll retries later, so this is not immediate data loss, but the original design says available provider result files should be ingested promptly.

### Better future implementation

Return a result envelope with independent file errors:

```csharp
public sealed record BatchDownloadedFiles(
    string? OutputContent,
    Exception? OutputDownloadError,
    string? ErrorContent,
    Exception? ErrorDownloadError);
```

Parse whichever file was successfully obtained.

Do this after the P0/P1 fixes.

---

## F-024 — P3 — old `SubmitBatchAsync` remains on provider interface

The repair correctly added:

```text
UploadBatchInputFileAsync
CreateBatchAsync
```

but kept the old combined:

```csharp
SubmitBatchAsync(...)
```

A future call site can accidentally bypass the required local durability checkpoint again.

### Recommendation

After updating all current tests/fakes, remove or obsolete the combined method.

If retained for compatibility, make it clearly unsafe for controller use:

```csharp
[Obsolete(
    "Controller code must use UploadBatchInputFileAsync + CreateBatchAsync "
    + "so ProviderInputFileId can be persisted before remote Batch creation.")]
```

---

## F-025 — P3 — final-image + metadata completion is not a bundle transaction

`CompleteCandidate` writes:

```text
final PNG
→ metadata JSON
```

If metadata writing fails after final image promotion, the final file remains orphaned.

This does not create a false Ready state because the job is not set Ready, but it complicates local recovery.

### Fix with local recovery work

Add:

```csharp
DeleteIncompleteFinalArtifacts(
    manifestFingerprint,
    requestKey,
    candidateId)
```

which deletes only:

```text
<candidate>.png
<candidate>.metadata.json
```

inside the exact expected staging directory, never `.raw.png`.

Use it before re-normalizing an interrupted local candidate.

---

# 5. Regression review of existing functionality

## 5.1 Manual Webchat / Request Queue

### Static verdict

**No confirmed pure-manual regression found.**

The existing manifest import, Request activation, prompt copy and Main commit paths remain present.

New import blocking only applies while:

```text
Direct API generation active
or
Batch submission active
```

which is intended.

### Still required dynamically

Run all existing manual Request Queue tests because no CI run exists for the audited HEAD.

---

## 5.2 Legacy Direct mode

### Static verdict

The API-specific Direct guard is correctly conditional:

```csharp
if (_activeApiCandidateMetadata is not null)
{
    HandleMainImage();
    return;
}
```

When no API Candidate exists, old Direct logic remains.

A dedicated new test also exists for this behavior.

### Remaining integration regression

API + Variants bypass is F-007.

---

## 5.3 Reference-Assisted workflow

### Static verdict

No confirmed regression found.

API automation remains blocked in `ReferenceReady`.

The new Candidate verification block only executes when:

```text
_activeApiCandidateMetadata != null
AND _activeRequest != null
AND _currentManifest != null
```

Normal Reference workflow should not enter that branch.

Still run all Reference/recovery tests before merge.

---

## 5.4 Keep Settings

No repair code directly redesigned Keep Settings.

No confirmed regression found from the source diff.

Must still run the existing Keep Settings suite.

---

## 5.5 Variants

The existing Variants implementation itself was not rewritten.

However API Candidate + Variants is currently unsafe, F-007.

Legacy Variants with no API Candidate should remain unchanged and needs an explicit regression test after fixing F-007.

---

## 5.6 Existing durable Main transaction / rollback

The durable Main commit and rollback machinery was not materially redesigned.

The repair added API Candidate verification before entering it.

For non-API assets, the old transaction path is effectively unchanged.

Still run:

```text
Main durable commit tests
rollback tests
RecoveryCritical tests
Reference rollback tests
NoReference rollback tests
```

because no post-repair CI evidence exists.

---

# 6. Additional test-quality findings

Several new tests give false confidence because they assert the current weak implementation instead of the repair contract.

## T-001 — Batch fail-closed tests are wrong

Current tests approve:

```text
unknown → log
duplicate → process first
```

Replace with no-mutation fail-closed tests from F-001.

## T-002 — Retry-setting tests approve the off-by-one behavior

Current tests approve:

```text
setting 1 → 2 total attempts
setting 2 → 3 total attempts
```

Align tests to the decided contract.

## T-003 — Staging durability test manually preserves state instead of testing production failure handling

The test manually does:

```csharp
jobStore.UpsertItem(
    normalizingRecord with
    {
        Status = FailedPermanent,
        ...
    });
```

and then confirms the raw path remains.

That does not exercise `RunDirectGenerationAsync`'s actual catch path, which writes from the old `itemRecord`.

Add real controller/worker failure injection tests.

## T-004 — Candidate verification suite misses integrity-boundary cases

Missing:

```text
prefix-sibling path
empty hash
missing hash
metadata custom-id mismatch
metadata model mismatch
raw hash mismatch
```

## T-005 — Core-only bulk JobStore tests are in the WinForms test project

Move them to:

```text
tests/AssetProvenanceHelper.Core.Tests
```

so the SAC-free Core suite and Core coverage denominator exercise them.

---

# 7. Best remaining-fix implementation order for a weak model

Do not fix these in arbitrary order.

---

## Phase A — P0 Batch cost/integrity fixes

### A1. `BatchQueued` active + UI status

Files:

```text
ApiPreflightService.cs
MainForm.ApiGenerationUi.cs
```

Tests first:

```text
Preflight_BatchQueued_NotEligible
BatchQueued_RowShowsBatchQueued
```

### A2. Remote Batch-ID save failure blocks second submission

Test first:

```text
Batch_RemoteCreated_BatchIdSaveFails_SecondClickDoesNotResubmit
```

Then apply F-002 code.

### A3. Batch result prevalidation

Tests first:

```text
UnknownCustomId_NoMutation
DuplicateCustomId_NoMutation
EmptyCustomId_NoMutation
```

Then apply F-001 validator.

### Acceptance Phase A

```text
zero duplicate CreateBatch calls
zero Ready candidates from an invalidly mapped result set
```

---

## Phase B — paid provider-output recovery

### B1. Preserve current rich job record

Modify Direct and Batch paths to keep:

```text
CandidateId
ProviderRawPath
RawSha256
ProviderRequestId
```

on local failures.

### B2. Compute raw SHA locally

Always:

```csharp
var rawSha =
    Convert
        .ToHexString(
            SHA256.HashData(
                candidate.RawBytes))
        .ToLowerInvariant();
```

If provider candidate also supplied a hash, optionally verify it:

```csharp
if (!string.IsNullOrWhiteSpace(
        candidate.RawSha256)
    && !string.Equals(
        candidate.RawSha256,
        rawSha,
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException(
        "Provider candidate raw SHA-256 does not match the received bytes.");
}
```

### B3. Add local recovery service

Implement F-004.

### B4. Preflight must prefer local recovery over remote retry

Pseudo-flow:

```text
Generate Now clicked
→ recover local candidates for manifest
→ reload JobStore snapshot
→ paid preflight
→ confirmation
→ remote starts
```

### Acceptance Phase B

A paid provider output that exists in `.raw.png` must never require a second provider generation solely because local normalization/staging was interrupted.

---

## Phase C — Candidate integrity

Implement F-005 and F-006 together.

### Recommended order

1. expose exact staging candidate paths;
2. strict metadata validation;
3. strict SHA validation;
4. raw SHA validation;
5. Ready-missing candidate blocks remote preflight;
6. re-run all candidate activation/commit tests.

---

## Phase D — API / legacy UI integration truthfulness

Implement:

```text
F-007 Variants guard
F-008 prompt/name edit unload
F-009 provider authority
F-016 key button state
F-017 alpha unknown warning
F-019 provenance labels
```

### Critical regression rule

Every change must have a paired test proving:

```text
legacy behavior unchanged when no API Candidate exists
```

Examples:

```text
NoApiCandidate_VariantsBehaviorUnchanged
ManualImage_ProviderSelectionStillWorks
ManualRequest_EditPromptBehaviorUnchanged
```

---

## Phase E — status semantics + Batch metadata

Implement:

```text
F-010 Output/Error file IDs
F-011 retry attempt semantics
F-021 FailedRetryable classification
F-022 halted-run status message
```

---

## Phase F — performance / package / quality gates

Implement:

```text
F-012 preflight one-load snapshot
F-013 Core coverage inventory/methods/75% branches
F-014 mutation break=80
F-015 ProtectedData moved to app project / .NET 10 version
T-005 Core tests moved to Core test project
```

Then run all gates.

---

## Phase G — optional hardening

Only after all mandatory tests pass:

```text
F-020 strict DPAPI corruption handling
F-023 independent output/error downloads
F-024 remove combined SubmitBatchAsync
F-025 incomplete final cleanup
generation-time OpenAI API provider template snapshot
```

---

# 8. Copy-ready improved `ApiPreflightService` core logic

A weak model can use this as the target shape.

```csharp
public ApiPreflightResult Preflight(
    string manifestFingerprint,
    IReadOnlyList<AssetRequestItem> items,
    IReadOnlyCollection<string> completedRequestKeys)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(
        manifestFingerprint);

    ArgumentNullException.ThrowIfNull(
        items);

    ArgumentNullException.ThrowIfNull(
        completedRequestKeys);

    var jobsByRequestKey =
        _jobStore
            .GetItemsForManifest(
                manifestFingerprint)
            .ToDictionary(
                job => job.RequestKey,
                StringComparer.Ordinal);

    var pendingItems =
        items
            .Where(item =>
                !item.IsCompleted
                && !completedRequestKeys.Contains(
                    item.RequestKey))
            .ToList();

    var eligible =
        new List<AssetRequestItem>();

    var blockedAlpha =
        new List<AssetRequestItem>();

    var errors =
        new List<ApiPreflightIssue>();

    var warnings =
        new List<ApiPreflightIssue>();

    var alreadyReadyCount = 0;
    var inFlightCount = 0;
    var uncertainCount = 0;

    foreach (var item in pendingItems)
    {
        jobsByRequestKey.TryGetValue(
            item.RequestKey,
            out var job);

        if (job is not null)
        {
            if (job.Status ==
                GenerationItemStatus.Ready)
            {
                alreadyReadyCount++;

                if (string.IsNullOrWhiteSpace(
                        job.StagedOutputPath)
                    || !File.Exists(
                        job.StagedOutputPath))
                {
                    errors.Add(
                        new ApiPreflightIssue(
                            item.RequestKey,
                            item.FileName,
                            "ready_candidate_missing",
                            "Request is recorded as Ready but the staged candidate is missing."));
                }

                continue;
            }

            if (job.Status ==
                GenerationItemStatus.UncertainAfterInterruption)
            {
                uncertainCount++;
                continue;
            }

            if (IsJobActiveOrInFlight(
                    job))
            {
                inFlightCount++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(
                    job.ProviderRawPath)
                && File.Exists(
                    job.ProviderRawPath))
            {
                errors.Add(
                    new ApiPreflightIssue(
                        item.RequestKey,
                        item.FileName,
                        "local_candidate_recovery_required",
                        "A provider result is already stored locally and must be recovered "
                        + "before any new remote generation is allowed."));

                continue;
            }
        }

        if (string.IsNullOrWhiteSpace(
                item.Prompt))
        {
            errors.Add(
                new ApiPreflightIssue(
                    item.RequestKey,
                    item.FileName,
                    "empty_prompt",
                    "Asset prompt cannot be empty."));

            continue;
        }

        try
        {
            _ =
                ImageSizePlanner.Plan(
                    item.Width,
                    item.Height);
        }
        catch (Exception ex)
        {
            errors.Add(
                new ApiPreflightIssue(
                    item.RequestKey,
                    item.FileName,
                    "invalid_generation_size",
                    ex.Message));

            continue;
        }

        if (item.Alpha ==
            AlphaRequirement.Required)
        {
            blockedAlpha.Add(
                item);

            continue;
        }

        if (item.Alpha ==
            AlphaRequirement.Unknown)
        {
            warnings.Add(
                new ApiPreflightIssue(
                    item.RequestKey,
                    item.FileName,
                    "alpha_requirement_unknown",
                    "Alpha requirement is unknown; this GPT-Image-2 MVP will generate opaque output."));
        }

        eligible.Add(
            item);
    }

    return new ApiPreflightResult(
        Eligible:
            eligible,
        BlockedAlpha:
            blockedAlpha,
        Errors:
            errors,
        Warnings:
            warnings,
        TotalPendingCount:
            pendingItems.Count,
        AlreadyReadyCount:
            alreadyReadyCount,
        InFlightCount:
            inFlightCount,
        UncertainCount:
            uncertainCount);
}
```

Remember to add:

```csharp
or GenerationItemStatus.BatchQueued
```

to `IsJobActiveOrInFlight`.

---

# 9. Copy-ready high-value test: duplicate remote Batch prevention

```csharp
[Fact]
public void BatchIdPersistFailure_SecondSubmissionIsBlocked()
{
    RunOnSta(() =>
    {
        using var workspace =
            new TestWorkspace();

        var jobStore =
            new GenerationJobStore(
                Path.Combine(
                    _tempDir,
                    "jobs.json"));

        var provider =
            new RecordingBatchProvider
            {
                InputFileId =
                    "file_123",
                RemoteBatchId =
                    "batch_456"
            };

        using var form =
            CreateForm(
                workspace,
                provider,
                jobStore);

        ImportSingleOpaqueRequest(
            form);

        GenerationJobStore.OnBeforeSaveCoreForTests =
            state =>
            {
                if (state.Batches.Any(
                        batch =>
                            batch.ProviderBatchId ==
                            "batch_456"))
                {
                    throw new IOException(
                        "Simulated persistence failure");
                }
            };

        try
        {
            ClickQueueBatch(form);

            Assert.Equal(
                1,
                provider.CreateBatchCallCount);

            // This is the critical assertion:
            ClickQueueBatch(form);

            Assert.Equal(
                1,
                provider.CreateBatchCallCount);
        }
        finally
        {
            GenerationJobStore.OnBeforeSaveCoreForTests =
                null;
        }

        var item =
            jobStore
                .Load()
                .Items
                .Single();

        Assert.Contains(
            item.Status,
            new[]
            {
                GenerationItemStatus.BatchQueued,
                GenerationItemStatus.UncertainAfterInterruption
            });
    });
}
```

---

# 10. Copy-ready high-value test: fail-closed Batch mapping

```csharp
[Fact]
public void UnknownCustomId_FailsBeforeAnyResultMutation()
{
    var batch =
        SeedTwoItemBatch();

    var firstKnown =
        SuccessfulOutput(
            customId: batch.CustomIdA);

    var foreign =
        SuccessfulOutput(
            customId: "foreign-id");

    Assert.Throws<InvalidDataException>(
        () =>
            _service.IngestResults(
                batch.Record,
                CompletedStatus(),
                new BatchDownloadResult(
                    batch.Record.ProviderBatchId!,
                    [firstKnown, foreign])));

    var stored =
        _jobStore.GetItemsForBatch(
            batch.Record.LocalBatchId);

    Assert.DoesNotContain(
        stored,
        item =>
            item.Status ==
            GenerationItemStatus.Ready);

    Assert.Empty(
        Directory.Exists(
            _stagingService.BaseStagingPath)
            ? Directory.GetFiles(
                _stagingService.BaseStagingPath,
                "*.png",
                SearchOption.AllDirectories)
            : []);
}
```

Duplicate variant:

```csharp
[Fact]
public void DuplicateCustomId_FailsBeforeAnyResultMutation()
{
    var batch =
        SeedOneItemBatch();

    var output =
        SuccessfulOutput(
            batch.CustomIdA);

    Assert.Throws<InvalidDataException>(
        () =>
            _service.IngestResults(
                batch.Record,
                CompletedStatus(),
                new BatchDownloadResult(
                    batch.Record.ProviderBatchId!,
                    [output, output])));

    Assert.DoesNotContain(
        _jobStore.GetItemsForBatch(
            batch.Record.LocalBatchId),
        item =>
            item.Status ==
            GenerationItemStatus.Ready);
}
```

---

# 11. Copy-ready high-value test: Ready missing file never re-bills

```csharp
[Fact]
public void ReadyMissingFile_GenerateNowDoesNotCallProvider()
{
    RunOnSta(() =>
    {
        var provider =
            new CountingFakeProvider();

        var jobStore =
            new GenerationJobStore(
                Path.Combine(
                    _tempDir,
                    "jobs.json"));

        using var form =
            CreateApiForm(
                provider,
                jobStore);

        var request =
            ImportSingleOpaqueRequest(
                form);

        jobStore.UpsertItem(
            CreateReadyJob(
                request,
                stagedOutputPath:
                    Path.Combine(
                        _tempDir,
                        "missing.png")));

        ClickGenerateNow(
            form);

        Assert.Equal(
            0,
            provider.GenerateCount);

        var stored =
            jobStore.GetItem(
                request.ManifestFingerprint,
                request.RequestKey);

        Assert.Equal(
            GenerationItemStatus.Ready,
            stored!.Status);
    });
}
```

The UI may report the local candidate problem, but it must not silently create another remote request.

---

# 12. Final regression matrix after fixes

Run all of these.

## Existing/manual

```text
V1 manifest import
V1 request-key byte identity
manual prompt copy
manual Main image selection
manual NoReference commit
manual Reference commit
Reference replacement
Main rollback/recovery
recent documents
provider templates
Keep Settings
Direct mode
Direct + Reference
Variants no-reference
Variants reference-assisted
Variants partial failure
```

## API Direct

```text
alpha required = block before HTTP
alpha unknown = eligible + warning
opaque = eligible
invalid local request = entire paid run blocked
run snapshot immutable
manifest import blocked during run
global 401/403/404 halts new starts
429/5xx exhausted = FailedRetryable
raw saved before normalize
local normalize failure preserves raw authority
Normalizing restart recovers locally
Ready missing/corrupt does not re-bill
request ID end-to-end
Ready != Done
API + Direct uses API Candidate
API + Variants cannot replace API Candidate
prompt/name edit unloads Candidate
provider dropdown cannot falsify API provider
commit re-verifies Candidate
```

## API Batch

```text
Preparing/local state before remote upload
ProviderInputFileId saved before CreateBatch
BatchQueued is active/non-eligible
ProviderBatchId immediately saved
Batch-ID save failure prevents second submission
restart BatchQueued -> safe recovery state
error-only result file
output-only result file
output + error
out-of-order custom IDs
unknown ID fail closed
duplicate ID fail closed
missing result item uncertain
expired partial success retained
raw before normalize
local normalization recovery
ProviderOutputFileId persisted
ProviderErrorFileId persisted
remote Batch resumes polling after restart
```

## Security

```text
settings.json no API key
generation-jobs.json no API key
candidate metadata no API key
provenance no API key
batch JSONL no API key
stored key not auto-populated
blank OK preserves key
explicit Delete removes key
corrupt DPAPI state not silently overwritten
```

---

# 13. Full commands required before final PASS

## 13.1 Clean repository

```powershell
git status --short
git diff --check
```

Expected:

```text
clean tree
no whitespace errors
```

## 13.2 Restore

```powershell
dotnet restore AssetProvenanceHelper.sln
```

## 13.3 Debug build + tests

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Debug `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Debug `
  --no-build `
  --logger "trx;LogFileName=debug.trx"
```

## 13.4 Release build + tests

```powershell
dotnet build AssetProvenanceHelper.sln `
  -c Release `
  --no-restore `
  -warnaserror

dotnet test AssetProvenanceHelper.sln `
  -c Release `
  --no-build `
  --logger "trx;LogFileName=release.trx"
```

## 13.5 RecoveryCritical

Tag all new crash/billing-boundary tests:

```csharp
[Trait("Category", "RecoveryCritical")]
```

Run:

```powershell
dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --filter "Category=RecoveryCritical"
```

Do the same for Core if recovery tests live there.

## 13.6 Core coverage

```powershell
dotnet test tests/AssetProvenanceHelper.Core.Tests/AssetProvenanceHelper.Core.Tests.csproj `
  -c Release `
  --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts/coverage-core

pwsh scripts/verify_core_coverage.ps1 -NoRunTests
```

## 13.7 Existing app coverage

```powershell
dotnet test tests/AssetProvenanceHelper.Tests/AssetProvenanceHelper.Tests.csproj `
  -c Release `
  --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts/coverage

pwsh scripts/verify_coverage.ps1
```

## 13.8 Mutation

```powershell
dotnet tool restore

Push-Location tests/AssetProvenanceHelper.Tests
dotnet stryker
Pop-Location

Push-Location tests/AssetProvenanceHelper.Core.Tests
dotnet stryker
Pop-Location
```

Both must meet configured gates.

## 13.9 Flakiness

Only after all deterministic suites are green:

```powershell
for ($i = 1; $i -le 20; $i++) {
    dotnet test AssetProvenanceHelper.sln `
      -c Release `
      --no-build `
      --logger "console;verbosity=minimal"

    if ($LASTEXITCODE -ne 0) {
        throw "Flakiness run $i failed"
    }
}
```

## 13.10 Publish / smoke

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

## 13.11 Secret scan

At minimum search generated artifacts and repo for a known fake test secret:

```powershell
$needle = "sk-test-secret-12345"

Get-ChildItem -Recurse -File |
  Select-String -SimpleMatch $needle
```

Expected: only explicit test source literals where intentionally defined; never generated runtime state, logs, TRX, publish state or provenance output.

---

# 14. GitHub verification before merge

The audited HEAD currently has no associated workflow runs and no open PR.

After all fixes:

1. push fix commit;
2. open PR against `main`;
3. require CI;
4. inspect every job, not only combined green state;
5. run mutation workflow manually if it is not part of normal PR CI;
6. do not merge while any required gate is missing/neutral/skipped unexpectedly.

Required evidence:

```text
Windows Build & Test: green
Coverage Gate: green
Core Coverage Gate: green
RecoveryCritical: green
20x flakiness: green
publish/smoke: green
Core mutation: >= configured break threshold
App mutation: >= configured break threshold
```

---

# 15. Weak-agent implementation prompt

Give the following prompt to the fixing agent together with:

```text
IMPLEMENTIERUNGSKONZEPT_API_BATCH_AUTOMATION.md
API_BATCH_AUTOMATION_REPAIR_PLAN_WEAK_AGENT.md
API_BATCH_AUTOMATION_POST_REPAIR_AUDIT_RESULTS.md
```

## Copy-ready prompt

```text
You are fixing the current feature/api-batch-automation branch of:

Ceegore/gpt_provenance_document_helper

AUTHORITIES, IN THIS ORDER:

1. IMPLEMENTIERUNGSKONZEPT_API_BATCH_AUTOMATION.md
2. API_BATCH_AUTOMATION_REPAIR_PLAN_WEAK_AGENT.md
3. API_BATCH_AUTOMATION_POST_REPAIR_AUDIT_RESULTS.md

The current audited commit was:
f4f579b60a1330259ae6fe7239e36bdc7153d363

Do NOT reimplement the whole feature.

Do NOT change these already-correct product decisions:
- gpt-image-2 alpha=required is blocked before HTTP.
- no transparent-background generation in this MVP.
- Direct default starts/minute = 5.
- Direct default max concurrency = 5.
- HTTP timeout = 3 minutes.
- PNG-based MVP staging/normalization.
- no automatic >500 remote Batch partitioning.
- no mandatory Paused state.
- no mandatory Stop-New-Starts button.
- existing Main durable commit remains the only Done authority.
- manual webchat / Reference / Direct / Keep Settings / Variants must remain intact.

WORK PHASE BY PHASE.
DO NOT MAKE A BIG-BANG COMMIT.

BEFORE EACH FIX:
1. Read the exact production file.
2. Read the exact existing tests.
3. Add a test that fails with the current bug.
4. Confirm the test is logically testing production behavior, not a hand-written imitation.
5. Apply the smallest robust fix.
6. Run targeted tests.
7. Run warning-as-error build.
8. Inspect diff before moving on.

MANDATORY FIX ORDER:

PHASE A – P0 BATCH SAFETY
A1. Add BatchQueued to ApiPreflightService.IsJobActiveOrInFlight.
A2. Render BatchQueued as “Batch queued”.
A3. After remote Batch-ID persistence failure, best-effort mark affected items Uncertain.
A4. Add a test that clicks Queue Batch AGAIN after the injected Batch-ID save failure and proves CreateBatch was called exactly once.
A5. Rewrite BatchIngestion custom-id behavior:
    - validate ALL result IDs before any candidate/item mutation;
    - unknown ID = fail closed;
    - duplicate ID = fail closed;
    - empty ID = fail closed;
    - exact StringComparer.Ordinal;
    - no valid result row becomes Ready when mapping validation fails.
A6. Replace existing tests that currently approve “log unknown / process first duplicate”.

PHASE B – PAID RAW OUTPUT RECOVERY
B1. Direct and Batch processing must maintain a currentRecord variable.
B2. Once raw output is saved, NEVER overwrite the record using the old pre-generation itemRecord.
B3. Preserve CandidateId, ProviderRawPath, RawSha256 and ProviderRequestId on local failures.
B4. Compute raw SHA locally from received bytes.
B5. Add local recovery for Normalizing / local-processing-failed jobs with an existing raw provider file.
B6. Before any paid preflight, attempt deterministic local recovery first.
B7. A job with a locally saved provider raw result is NEVER automatically eligible for another remote generation.
B8. Add crash/failure tests against the REAL Direct controller and REAL Batch ingestion path.

PHASE C – READY CANDIDATE INTEGRITY
C1. Ready state always blocks remote generation even if staged file is missing.
C2. Missing/corrupt Ready candidate produces a local error, not a new paid request.
C3. CandidateVerification must use an exact expected candidate path, not StartsWith directory prefix.
C4. Require valid 64-hex normalized hashes.
C5. Compare job hash, metadata hash and actual file hash.
C6. Compare CandidateId, CustomId, Model, mode and target/provider resolutions.
C7. If raw file exists, verify raw hash too.
C8. Add all missing integrity tests.

PHASE D – API/LEGACY INTEGRATION
D1. Active API Candidate + Variants > none must not run HandleVariantBatch.
D2. When bound Prompt/Asset Name changes, clear BOTH API metadata and selected API Main image.
D3. API Candidate commit must use OpenAI API.md regardless of provider dropdown changes.
D4. Manual provider selection remains unchanged when no API Candidate is active.
D5. API buttons disabled without API key.
D6. alpha=unknown remains eligible but produces a preflight warning.
D7. Add explicit Generation channel / Final normalized resolution / Post-processing labels to OpenAI API provenance.

PHASE E – STATUS / METADATA
E1. Persist ProviderOutputFileId and ProviderErrorFileId.
E2. Resolve DirectRetryAttempts ambiguity by using the original MaxAttempts contract:
    configured 1 => 1 total HTTP attempt
    configured 3 => at most 3 total attempts.
    Rename UI label to “Max direct API attempts” if useful.
E3. Exhausted 429/5xx/network transient errors => FailedRetryable.
E4. A globally halted Direct run must not log “completed”.

PHASE F – PERFORMANCE / QUALITY GATES
F1. ApiPreflightService loads JobStore state once using GetItemsForManifest.
F2. Add deterministic test proving one state load for a large preflight.
F3. Core coverage gate must dynamically inventory src/AssetProvenanceHelper.Core/**/*.cs.
F4. Add method coverage / ratchet like the existing app gate.
F5. Core branch minimum must be >=75%, not 73%.
F6. Core Stryker break threshold = 80.
F7. Move Core-only JobStore bulk tests into Core.Tests.
F8. Move ProtectedData PackageReference out of Core and into the WinForms app project.
F9. Use the repository-approved .NET 10 ProtectedData version (plan: 10.0.11).

OPTIONAL AFTER EVERYTHING ABOVE:
- explicit DPAPI corruption errors;
- independent output/error Batch-file ingestion;
- remove/obsolete combined SubmitBatchAsync;
- incomplete final-candidate cleanup;
- generation-time OpenAI API provider-template snapshot.

TEST RULES:
- no real OpenAI API calls;
- fake provider / fake HttpMessageHandler only;
- no SAC/Defender changes;
- every billing/recovery boundary requires failure injection;
- do not update coverage baseline merely to make a failing gate green.

AFTER EACH PHASE OUTPUT:

PHASE:
FILES CHANGED:
TESTS ADDED/CHANGED:
TARGETED COMMANDS RUN:
RESULT:
REMAINING ISSUES:
SPEC DEVIATIONS:

If SPEC DEVIATIONS is not empty, STOP and explain.

FINAL ACCEPTANCE:
- git diff --check
- clean restore
- Debug warning-free build + full tests
- Release warning-free build + full tests
- RecoveryCritical
- Core coverage
- App coverage
- Core mutation
- App mutation
- 20x flakiness
- publish/smoke
- fake-secret leak scan

Do not claim PASS unless all actually executed required checks are green.
```

---

# 16. Final acceptance prompt

After the weak model has fixed everything, run this prompt with a fresh reviewer/model:

```text
Audit the current feature/api-batch-automation branch from zero.

Read fully:
1. IMPLEMENTIERUNGSKONZEPT_API_BATCH_AUTOMATION.md
2. API_BATCH_AUTOMATION_REPAIR_PLAN_WEAK_AGENT.md
3. API_BATCH_AUTOMATION_POST_REPAIR_AUDIT_RESULTS.md
4. all production files touched by the API implementation
5. all related tests
6. CI/coverage/mutation scripts

Do not trust commit messages.
Do not trust tests merely because they are green.
Check whether tests assert the specification.

Re-test every previous finding F-001 through F-025.

In particular prove:

BATCH COST SAFETY
- ProviderInputFileId durable before CreateBatch.
- ProviderBatchId durable immediately after CreateBatch.
- BatchQueued is never considered a fresh eligible request.
- injected Batch-ID persistence failure + second UI click cannot create a second remote Batch.
- unknown/duplicate/empty custom IDs cause zero result-specific candidate mutation.

PAID OUTPUT RECOVERY
- raw bytes are durable before local normalization.
- production failure paths retain raw recovery metadata.
- Normalizing restart cannot get stuck.
- a saved raw provider result is locally recovered without another provider call.
- Ready missing/corrupt candidate cannot silently re-bill.

CANDIDATE INTEGRITY
- exact path.
- final SHA required.
- metadata SHA required.
- actual SHA.
- CandidateId.
- CustomId.
- Model.
- mode.
- target/provider dimensions.
- raw SHA when raw exists.
- verification again immediately before Main commit.

LEGACY INTEGRATION
- active API Candidate wins over Direct.
- active API Candidate cannot fall into legacy Variants.
- editing Prompt/Asset Name unloads the API image.
- changing provider dropdown cannot falsify API provenance.
- no API Candidate => old Direct/Variants/provider workflows unchanged.

SPEC
- alpha required blocked before HTTP.
- alpha unknown allowed + warning.
- Ready != Done.
- Done only after durable Main commit.
- API key never in normal state/log/provenance/metadata/JSONL.

PERFORMANCE / QUALITY
- 1000-item preflight performs one JobStore state load.
- Core source inventory is complete in coverage.
- Core branch threshold >=75.
- mutation break >=80.
- correct ProtectedData project/version.
- no CI job missing.

Then execute:
- Debug full build/tests
- Release full build/tests
- RecoveryCritical
- coverage
- mutation
- 20x flakiness
- publish/smoke
- git diff --check
- secret scan

If any problem exists:
1. add a reproducing test;
2. fix minimally;
3. rerun affected tests;
4. restart the entire audit.

Only when nothing remains, output exactly:

PASS_ZERO_DEFECT_API_BATCH_AUTOMATION

followed by the executed command/result evidence.
```

---

# 17. Final merge checklist

Do not merge until every box is checked:

- [ ] F-001 fail-closed `custom_id`
- [ ] F-002 duplicate remote Batch impossible after Batch-ID persistence failure
- [ ] F-003 raw recovery metadata preserved by real production paths
- [ ] F-004 Normalizing crash recovery
- [ ] F-005 Ready-missing candidate cannot re-bill
- [ ] F-006 strict Candidate verification
- [ ] F-007 API + Variants guard
- [ ] F-008 prompt/name edit unloads API image
- [ ] F-009 truthful API provider template authority
- [ ] F-010 Batch output/error IDs persisted
- [ ] F-011 retry semantics resolved
- [ ] F-012 preflight one-load performance
- [ ] F-013 robust Core coverage gate
- [ ] F-014 Core mutation break >=80
- [ ] F-015 ProtectedData dependency corrected
- [ ] F-016 API buttons require key
- [ ] F-017 alpha unknown warning
- [ ] F-018 raw PNG validation
- [ ] F-019 provenance field completeness
- [ ] F-020 secret-store corruption behavior
- [ ] F-021 retryable status truthfulness
- [ ] F-022 halted run message
- [ ] F-023 or documented deferral
- [ ] F-024 or documented deferral
- [ ] F-025 or covered by local recovery cleanup
- [ ] all existing manual/Reference/Direct/KeepSettings/Variants tests green
- [ ] no post-repair regression
- [ ] Debug green
- [ ] Release green
- [ ] RecoveryCritical green
- [ ] Core coverage green
- [ ] app coverage green
- [ ] Core mutation green
- [ ] app mutation green
- [ ] 20x flakiness green
- [ ] publish/smoke green
- [ ] no real paid test calls
- [ ] no secret leaks
- [ ] PR CI exists and is green

---

# 18. Bottom line

The repair commit was **meaningful and mostly headed in the correct direction**. It should not be reverted.

The remaining work is concentrated around:

```text
Batch mapping authority
remote Batch duplicate prevention
local paid-output recovery
Ready Candidate integrity
API/Variants/request-binding integration
quality-gate correctness
```

Fix those areas incrementally with the tests above.

The branch should then be re-audited from the exact new HEAD and only merged after actual CI evidence exists.

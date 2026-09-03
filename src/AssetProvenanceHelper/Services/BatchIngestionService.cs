using System.Diagnostics;
using System.Security.Cryptography;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed record BatchIngestionSummary(
    int SuccessCount,
    int FailureCount,
    int HandledCount,
    IReadOnlyList<string> UnknownCustomIds,
    IReadOnlyList<string> DuplicateCustomIds,
    IReadOnlyList<string> MissingCustomIds);

public sealed class BatchIngestionService
{
    private readonly GenerationJobStore _jobStore;
    private readonly GeneratedImageStagingService _stagingService;

    public BatchIngestionService(GenerationJobStore jobStore, GeneratedImageStagingService stagingService)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _stagingService = stagingService ?? throw new ArgumentNullException(nameof(stagingService));
    }

    public BatchIngestionSummary IngestResults(
        GenerationBatchRecord batch,
        BatchStatusResult status,
        BatchDownloadResult downloadResult)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(downloadResult);

        var batchItems = _jobStore.GetItemsForBatch(batch.LocalBatchId);
        var handledCustomIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateCustomIds = new List<string>();
        var unknownCustomIds = new List<string>();
        var missingCustomIds = new List<string>();

        var successCount = 0;
        var failCount = 0;

        foreach (var output in downloadResult.Items)
        {
            var itemRecord = batchItems.FirstOrDefault(i =>
                string.Equals(i.CustomId, output.CustomId, StringComparison.OrdinalIgnoreCase));

            // Rule 3: Unknown custom_id in batch output - log warning, never silently ignore
            if (itemRecord == null)
            {
                Trace.TraceWarning($"[BatchIngestion] Unknown custom_id '{output.CustomId}' in batch output for batch '{batch.LocalBatchId}'.");
                unknownCustomIds.Add(output.CustomId);
                continue;
            }

            // Rule 4: Duplicate custom_id in output - process only the first occurrence, log subsequent
            if (!handledCustomIds.Add(output.CustomId))
            {
                Trace.TraceWarning($"[BatchIngestion] Duplicate custom_id '{output.CustomId}' in batch output for batch '{batch.LocalBatchId}'. Ignoring duplicate occurrence.");
                duplicateCustomIds.Add(output.CustomId);
                continue;
            }

            // Rule 1: Successful item with image bytes
            if (output.IsSuccess && output.ImageBytes != null && output.ImageBytes.Length > 0)
            {
                var candidateId = Guid.NewGuid().ToString("N");
                var rawSha = Convert.ToHexString(SHA256.HashData(output.ImageBytes)).ToLowerInvariant();

                string rawPath;
                try
                {
                    rawPath = _stagingService.SaveRawCandidate(
                        batch.ManifestFingerprint,
                        itemRecord.RequestKey,
                        candidateId,
                        output.ImageBytes);

                    _jobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.Normalizing,
                        CandidateId = candidateId,
                        ProviderRawPath = rawPath,
                        RawSha256 = rawSha,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                }
                catch (Exception rawEx)
                {
                    _jobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.FailedPermanent,
                        ErrorCode = "raw_save_error",
                        ErrorMessage = rawEx.Message,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                    failCount++;
                    continue;
                }

                try
                {
                    var plan = ImageSizePlanner.Plan(itemRecord.TargetWidth, itemRecord.TargetHeight);
                    var normResult = ImageNormalizationService.Normalize(output.ImageBytes, plan);

                    var metadata = new ApiCandidateMetadata(
                        CandidateId: candidateId,
                        Provider: batch.ProviderId,
                        Model: batch.Model,
                        Mode: "batch",
                        CustomId: output.CustomId,
                        TargetResolution: $"{itemRecord.TargetWidth}x{itemRecord.TargetHeight}",
                        ProviderResolution: $"{plan.GenerationWidth}x{plan.GenerationHeight}",
                        RawSha256: rawSha,
                        NormalizedSha256: normResult.NormalizedSha256,
                        NormalizedImagePath: string.Empty,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        ProviderRequestId: output.ProviderRequestId,
                        BatchId: !string.IsNullOrEmpty(batch.ProviderBatchId) ? batch.ProviderBatchId : batch.LocalBatchId);

                    var normalizedPath = _stagingService.CompleteCandidate(
                        batch.ManifestFingerprint,
                        itemRecord.RequestKey,
                        candidateId,
                        normResult.NormalizedBytes,
                        metadata);

                    _jobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.Ready,
                        CandidateId = candidateId,
                        ProviderRawPath = rawPath,
                        StagedOutputPath = normalizedPath,
                        RawSha256 = rawSha,
                        NormalizedSha256 = normResult.NormalizedSha256,
                        ProviderRequestId = output.ProviderRequestId,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                    successCount++;
                }
                catch (Exception normEx)
                {
                    _jobStore.UpsertItem(itemRecord with
                    {
                        Status = GenerationItemStatus.FailedPermanent,
                        ErrorCode = "normalization_error",
                        ErrorMessage = normEx.Message,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                    failCount++;
                }
            }
            else
            {
                // Rule 1: Item failed remotely (error_file or error item) - mark FailedPermanent with code & message
                _jobStore.UpsertItem(itemRecord with
                {
                    Status = GenerationItemStatus.FailedPermanent,
                    ErrorCode = output.ErrorCode ?? "batch_item_failed",
                    ErrorMessage = output.ErrorMessage ?? "Batch item failed on remote provider.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
                failCount++;
            }
        }

        // Rule 5: Items that never appeared in any result file
        foreach (var bItem in batchItems)
        {
            if (!handledCustomIds.Contains(bItem.CustomId) &&
                bItem.Status != GenerationItemStatus.Ready &&
                bItem.Status != GenerationItemStatus.Committed)
            {
                missingCustomIds.Add(bItem.CustomId);
                _jobStore.UpsertItem(bItem with
                {
                    Status = GenerationItemStatus.UncertainAfterInterruption,
                    ErrorCode = "missing_from_batch_results",
                    ErrorMessage = $"Item was submitted in batch '{batch.ProviderBatchId}' but did not appear in results after batch reached status '{status.Status}'.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }

        var extraErrors = new List<string>();
        if (unknownCustomIds.Count > 0)
        {
            extraErrors.Add($"Unknown custom IDs in batch results: {string.Join(", ", unknownCustomIds.Take(5))}");
        }
        if (duplicateCustomIds.Count > 0)
        {
            extraErrors.Add($"Duplicate custom IDs in batch results: {string.Join(", ", duplicateCustomIds.Take(5))}");
        }

        var combinedError = extraErrors.Count > 0
            ? string.Join("; ", extraErrors)
            : batch.ErrorMessage;

        _jobStore.UpsertBatch(batch with
        {
            Status = status.Status,
            CompletedCount = status.CompletedCount,
            FailedCount = status.FailedCount,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ErrorMessage = combinedError
        });

        return new BatchIngestionSummary(
            SuccessCount: successCount,
            FailureCount: failCount,
            HandledCount: handledCustomIds.Count,
            UnknownCustomIds: unknownCustomIds,
            DuplicateCustomIds: duplicateCustomIds,
            MissingCustomIds: missingCustomIds);
    }

    public void HandleDownloadInterruption(GenerationBatchRecord batch, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(exception);

        // Rule 2: If download of batch results (or error file) fails:
        // Ingestion stops for unclear items; partial successes remain; status UncertainAfterInterruption, no blind ready.
        var batchItems = _jobStore.GetItemsForBatch(batch.LocalBatchId);
        foreach (var bItem in batchItems)
        {
            if (bItem.Status is GenerationItemStatus.BatchSubmitted or GenerationItemStatus.BatchRunning or GenerationItemStatus.Downloading)
            {
                _jobStore.UpsertItem(bItem with
                {
                    Status = GenerationItemStatus.UncertainAfterInterruption,
                    ErrorCode = "batch_results_download_failed",
                    ErrorMessage = $"Batch completed remotely, but downloading results failed: {exception.Message}",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }

        _jobStore.UpsertBatch(batch with
        {
            Status = "DownloadingFailed",
            ErrorMessage = $"Failed to download batch results: {exception.Message}",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }
}

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
    IReadOnlyList<string> MissingCustomIds,
    bool NeedsRemoteResultRedownload = false);

public sealed class BatchIngestionService
{
    private readonly GenerationJobStore _jobStore;
    private readonly GeneratedImageStagingService _stagingService;

    public BatchIngestionService(GenerationJobStore jobStore, GeneratedImageStagingService stagingService)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _stagingService = stagingService ?? throw new ArgumentNullException(nameof(stagingService));
    }

    private static IReadOnlyDictionary<string, GenerationItemRecord> ValidateBatchResultMapping(
        IReadOnlyList<GenerationItemRecord> batchItems,
        IReadOnlyList<BatchItemOutput> outputs)
    {
        var expected = new Dictionary<string, GenerationItemRecord>(StringComparer.Ordinal);
        foreach (var item in batchItems)
        {
            if (!expected.TryAdd(item.CustomId, item))
            {
                throw new InvalidDataException($"Local batch items contain duplicate custom_id '{item.CustomId}'.");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var output in outputs)
        {
            if (string.IsNullOrWhiteSpace(output.CustomId))
            {
                throw new InvalidDataException("Batch result contains an empty custom_id.");
            }

            if (!seen.Add(output.CustomId))
            {
                throw new InvalidDataException($"Batch result contains duplicate custom_id '{output.CustomId}'.");
            }

            if (!expected.ContainsKey(output.CustomId))
            {
                throw new InvalidDataException($"Batch result contains unknown custom_id '{output.CustomId}'.");
            }
        }

        return expected;
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

        IReadOnlyDictionary<string, GenerationItemRecord> expected;
        try
        {
            expected = ValidateBatchResultMapping(batchItems, downloadResult.Items);
        }
        catch (InvalidDataException ex)
        {
            var unresolved = batchItems
                .Where(item =>
                    item.Status != GenerationItemStatus.Ready
                    && item.Status != GenerationItemStatus.Committed)
                .Select(item =>
                    item with
                    {
                        Status = GenerationItemStatus.UncertainAfterInterruption,
                        ErrorCode = "batch_result_mapping_invalid",
                        ErrorMessage = ex.Message,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    })
                .ToList();

            _jobStore.UpsertItems(unresolved);

            _jobStore.UpsertBatch(batch with
            {
                Status = status.Status,
                ProviderOutputFileId = status.OutputFileId,
                ProviderErrorFileId = status.ErrorFileId,
                ErrorMessage = ex.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            });

            throw;
        }

        var handledCustomIds = new HashSet<string>(StringComparer.Ordinal);
        var missingCustomIds = new List<string>();

        var successCount = 0;
        var failCount = 0;
        var needsRemoteResultRedownload = false;

        foreach (var output in downloadResult.Items)
        {
            var currentRecord = expected[output.CustomId];
            handledCustomIds.Add(output.CustomId);

            if (currentRecord.Status is GenerationItemStatus.Ready or GenerationItemStatus.Committed)
            {
                successCount++;
                continue;
            }

            // Rule 1: Successful item with image bytes
            if (output.IsSuccess && output.ImageBytes != null && output.ImageBytes.Length > 0)
            {
                var candidateId = Guid.NewGuid().ToString("N");
                var rawSha = Convert.ToHexString(SHA256.HashData(output.ImageBytes)).ToLowerInvariant();
                var receivedAtUtc = DateTimeOffset.UtcNow;

                currentRecord = currentRecord with
                {
                    ProviderOutputReceived = true,
                    ProviderOutputReceivedAtUtc = receivedAtUtc,
                    ProviderRequestId = output.ProviderRequestId,
                    UpdatedAtUtc = receivedAtUtc
                };
                _jobStore.UpsertItem(currentRecord);

                string rawPath;
                try
                {
                    rawPath = _stagingService.SaveRawCandidate(
                        batch.ManifestFingerprint,
                        currentRecord.RequestKey,
                        candidateId,
                        output.ImageBytes);

                    currentRecord = currentRecord with
                    {
                        Status = GenerationItemStatus.Normalizing,
                        CandidateId = candidateId,
                        ProviderRawPath = rawPath,
                        RawSha256 = rawSha,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                    _jobStore.UpsertItem(currentRecord);
                }
                catch (Exception rawEx)
                {
                    needsRemoteResultRedownload = true;

                    _jobStore.UpsertItem(currentRecord with
                    {
                        Status = GenerationItemStatus.FailedRetryable,
                        ErrorCode = "batch_result_raw_persist_failed",
                        ErrorMessage = $"Provider Batch result was received, but local raw persistence failed: {rawEx.Message}",
                        ProviderRequestId = output.ProviderRequestId,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                    failCount++;
                    continue;
                }

                try
                {
                    var plan = ImageSizePlanner.Plan(currentRecord.TargetWidth, currentRecord.TargetHeight);
                    var normResult = ImageNormalizationService.Normalize(output.ImageBytes, plan);

                    var metadata = new ApiCandidateMetadata(
                        CandidateId: candidateId,
                        Provider: batch.ProviderId,
                        Model: batch.Model,
                        Mode: "batch",
                        CustomId: output.CustomId,
                        TargetResolution: $"{currentRecord.TargetWidth}x{currentRecord.TargetHeight}",
                        ProviderResolution: $"{plan.GenerationWidth}x{plan.GenerationHeight}",
                        RawSha256: rawSha,
                        NormalizedSha256: normResult.NormalizedSha256,
                        NormalizedImagePath: string.Empty,
                        CreatedAtUtc: receivedAtUtc,
                        ProviderRequestId: output.ProviderRequestId,
                        BatchId: !string.IsNullOrWhiteSpace(batch.ProviderBatchId) ? batch.ProviderBatchId : null);

                    var normalizedPath = _stagingService.CompleteCandidate(
                        batch.ManifestFingerprint,
                        currentRecord.RequestKey,
                        candidateId,
                        normResult.NormalizedBytes,
                        metadata);

                    currentRecord = currentRecord with
                    {
                        Status = GenerationItemStatus.Ready,
                        CandidateId = candidateId,
                        ProviderRawPath = rawPath,
                        StagedOutputPath = normalizedPath,
                        RawSha256 = rawSha,
                        NormalizedSha256 = normResult.NormalizedSha256,
                        GenerationWidth = plan.GenerationWidth,
                        GenerationHeight = plan.GenerationHeight,
                        ProviderRequestId = output.ProviderRequestId,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                    _jobStore.UpsertItem(currentRecord);
                    successCount++;
                }
                catch (Exception normEx)
                {
                    _jobStore.UpsertItem(currentRecord with
                    {
                        Status = GenerationItemStatus.FailedRetryable,
                        ErrorCode = "local_candidate_processing_failed",
                        ErrorMessage = normEx.Message,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    });
                    failCount++;
                }
            }
            else
            {
                // Rule 1: Item failed remotely (error_file or error item) - mark FailedPermanent with code & message
                _jobStore.UpsertItem(currentRecord with
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

        var localStatus = needsRemoteResultRedownload
            ? "IngestionFailed"
            : status.Status;

        _jobStore.UpsertBatch(batch with
        {
            Status = localStatus,
            ProviderOutputFileId = status.OutputFileId,
            ProviderErrorFileId = status.ErrorFileId,
            CompletedCount = status.CompletedCount,
            FailedCount = status.FailedCount,
            CompletedAtUtc = needsRemoteResultRedownload
                ? null
                : DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ErrorMessage = needsRemoteResultRedownload
                ? "Remote Batch finished, but one or more result files could not be persisted locally. Result ingestion will retry."
                : batch.ErrorMessage
        });

        return new BatchIngestionSummary(
            SuccessCount: successCount,
            FailureCount: failCount,
            HandledCount: handledCustomIds.Count,
            UnknownCustomIds: [],
            DuplicateCustomIds: [],
            MissingCustomIds: missingCustomIds,
            NeedsRemoteResultRedownload: needsRemoteResultRedownload);
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

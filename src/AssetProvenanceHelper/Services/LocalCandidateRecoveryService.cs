using System.Security.Cryptography;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class LocalCandidateRecoveryService
{
    private readonly GenerationJobStore _jobStore;
    private readonly GeneratedImageStagingService _stagingService;

    public LocalCandidateRecoveryService(
        GenerationJobStore jobStore,
        GeneratedImageStagingService stagingService)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _stagingService = stagingService ?? throw new ArgumentNullException(nameof(stagingService));
    }

    public static bool CanRecoverLocally(GenerationItemRecord job)
    {
        return (job.Status == GenerationItemStatus.Normalizing
                || (job.Status == GenerationItemStatus.FailedRetryable
                    && string.Equals(job.ErrorCode, "local_candidate_processing_failed", StringComparison.Ordinal)))
               && !string.IsNullOrWhiteSpace(job.CandidateId)
               && !string.IsNullOrWhiteSpace(job.ProviderRawPath)
               && File.Exists(job.ProviderRawPath);
    }

    public int RecoverAllForManifest(string manifestFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFingerprint);

        var items = _jobStore.GetItemsForManifest(manifestFingerprint);
        var recoveredCount = 0;

        foreach (var item in items)
        {
            if (CanRecoverLocally(item))
            {
                if (TryRecoverCandidate(item))
                {
                    recoveredCount++;
                }
            }
            else if (item.Status == GenerationItemStatus.Normalizing)
            {
                _jobStore.UpsertItem(item with
                {
                    Status = GenerationItemStatus.UncertainAfterInterruption,
                    ErrorCode = "normalizing_raw_missing",
                    ErrorMessage = "Process was normalizing candidate but raw provider output file was not found on disk.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
            else if (item.Status == GenerationItemStatus.FailedRetryable &&
                     string.Equals(item.ErrorCode, "local_candidate_processing_failed", StringComparison.Ordinal))
            {
                _jobStore.UpsertItem(item with
                {
                    Status = GenerationItemStatus.UncertainAfterInterruption,
                    ErrorCode = "recovery_raw_missing",
                    ErrorMessage = "Candidate was pending local recovery retry but raw provider output file was not found on disk.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }

        return recoveredCount;
    }

    public bool TryRecoverCandidate(GenerationItemRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!CanRecoverLocally(job))
        {
            return false;
        }

        try
        {
            var rawBytes = File.ReadAllBytes(job.ProviderRawPath!);
            if (rawBytes.Length == 0)
            {
                throw new InvalidDataException("Raw provider candidate file is empty.");
            }

            var actualRawSha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(job.RawSha256)
                && !string.Equals(job.RawSha256, actualRawSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Raw provider candidate file hash does not match recorded RawSha256.");
            }

            var plan = ImageSizePlanner.Plan(job.TargetWidth, job.TargetHeight);

            _stagingService.DeleteIncompleteFinalArtifacts(
                job.ManifestFingerprint,
                job.RequestKey,
                job.CandidateId!);

            var normResult = ImageNormalizationService.Normalize(rawBytes, plan);

            var metadata = new ApiCandidateMetadata(
                CandidateId: job.CandidateId!,
                Provider: job.ProviderId,
                Model: job.Model,
                Mode: job.Mode == GenerationMode.Batch ? "batch" : "direct",
                CustomId: job.CustomId,
                TargetResolution: $"{job.TargetWidth}x{job.TargetHeight}",
                ProviderResolution: $"{plan.GenerationWidth}x{plan.GenerationHeight}",
                RawSha256: actualRawSha,
                NormalizedSha256: normResult.NormalizedSha256,
                NormalizedImagePath: string.Empty,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                ProviderRequestId: job.ProviderRequestId,
                BatchId: job.BatchId);

            var normalizedPath = _stagingService.CompleteCandidate(
                job.ManifestFingerprint,
                job.RequestKey,
                job.CandidateId!,
                normResult.NormalizedBytes,
                metadata);

            _jobStore.UpsertItem(job with
            {
                Status = GenerationItemStatus.Ready,
                StagedOutputPath = normalizedPath,
                RawSha256 = actualRawSha,
                NormalizedSha256 = normResult.NormalizedSha256,
                ErrorCode = null,
                ErrorMessage = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            return true;
        }
        catch (Exception ex)
        {
            _jobStore.UpsertItem(job with
            {
                Status = GenerationItemStatus.FailedRetryable,
                ErrorCode = "local_candidate_processing_failed",
                ErrorMessage = $"Local candidate recovery failed: {ex.Message}",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            return false;
        }
    }
}

using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class ApiPreflightService
{
    private readonly GenerationJobStore _jobStore;

    public ApiPreflightService(GenerationJobStore jobStore)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    }

    public static bool IsJobActiveOrInFlight(GenerationItemRecord job)
    {
        return job.Status is GenerationItemStatus.DirectInFlight
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

    public ApiPreflightResult Preflight(
        string manifestFingerprint,
        IReadOnlyList<AssetRequestItem> items,
        IReadOnlyCollection<string> completedRequestKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFingerprint);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(completedRequestKeys);

        var jobsByRequestKey = _jobStore
            .GetItemsForManifest(manifestFingerprint)
            .ToDictionary(job => job.RequestKey, StringComparer.Ordinal);

        var pendingItems = items
            .Where(i => !i.IsCompleted && !completedRequestKeys.Contains(i.RequestKey))
            .ToList();

        var eligible = new List<AssetRequestItem>();
        var blockedAlpha = new List<AssetRequestItem>();
        var errors = new List<ApiPreflightIssue>();
        var warnings = new List<ApiPreflightIssue>();

        var alreadyReadyCount = 0;
        var inFlightCount = 0;
        var uncertainCount = 0;

        foreach (var item in pendingItems)
        {
            jobsByRequestKey.TryGetValue(item.RequestKey, out var job);

            if (job != null)
            {
                if (job.Status == GenerationItemStatus.Ready)
                {
                    alreadyReadyCount++;

                    if (string.IsNullOrWhiteSpace(job.StagedOutputPath) || !File.Exists(job.StagedOutputPath))
                    {
                        errors.Add(new ApiPreflightIssue(
                            item.RequestKey,
                            item.FileName,
                            "ready_candidate_missing",
                            "Request is recorded as Ready but the staged candidate is missing."));
                    }

                    continue;
                }

                if (job.Status == GenerationItemStatus.UncertainAfterInterruption)
                {
                    uncertainCount++;
                    continue;
                }

                if (IsJobActiveOrInFlight(job))
                {
                    inFlightCount++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(job.ProviderRawPath) && File.Exists(job.ProviderRawPath))
                {
                    errors.Add(new ApiPreflightIssue(
                        item.RequestKey,
                        item.FileName,
                        "local_candidate_recovery_required",
                        "A provider result is already stored locally and must be recovered before any new remote generation is allowed."));
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(item.Prompt))
            {
                errors.Add(new ApiPreflightIssue(
                    item.RequestKey,
                    item.FileName,
                    "empty_prompt",
                    "Asset prompt cannot be empty."));
                continue;
            }

            try
            {
                _ = ImageSizePlanner.Plan(item.Width, item.Height);
            }
            catch (Exception ex)
            {
                errors.Add(new ApiPreflightIssue(
                    item.RequestKey,
                    item.FileName,
                    "invalid_generation_size",
                    ex.Message));
                continue;
            }

            if (item.Alpha == AlphaRequirement.Required)
            {
                blockedAlpha.Add(item);
                continue;
            }

            if (item.Alpha == AlphaRequirement.Unknown)
            {
                warnings.Add(new ApiPreflightIssue(
                    item.RequestKey,
                    item.FileName,
                    "alpha_requirement_unknown",
                    "Alpha requirement is unknown; this GPT-Image-2 MVP will generate opaque output."));
            }

            eligible.Add(item);
        }

        return new ApiPreflightResult(
            Eligible: eligible,
            BlockedAlpha: blockedAlpha,
            Errors: errors,
            Warnings: warnings,
            TotalPendingCount: pendingItems.Count,
            AlreadyReadyCount: alreadyReadyCount,
            InFlightCount: inFlightCount,
            UncertainCount: uncertainCount);
    }
}

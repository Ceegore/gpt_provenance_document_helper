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
            var job = _jobStore.GetItem(manifestFingerprint, item.RequestKey);
            if (job != null)
            {
                if (job.Status == GenerationItemStatus.Ready && !string.IsNullOrEmpty(job.StagedOutputPath) && File.Exists(job.StagedOutputPath))
                {
                    alreadyReadyCount++;
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

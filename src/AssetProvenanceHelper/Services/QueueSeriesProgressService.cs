using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

/// <summary>Summarizes only canonical Pixel-Exact series. Legacy/manual rows are
/// intentionally excluded because their queue order is not a stable series ID.</summary>
public sealed class QueueSeriesProgressService
{
    private readonly QueuePromptWorkflowParser _parser;

    public QueueSeriesProgressService(QueuePromptWorkflowParser parser)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    public IReadOnlyList<PixelExactSeriesProgress> Summarize(
        IEnumerable<AssetRequestItem> requests,
        ISet<string> completedRequestKeys)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(completedRequestKeys);

        return requests
            .Select(request => new { Request = request, Workflow = _parser.Parse(request.Prompt) })
            .Where(item => item.Workflow.IsPixelExact
                && item.Workflow.HasCanonicalMetadata
                && !string.IsNullOrWhiteSpace(item.Workflow.SeriesId))
            .GroupBy(item => item.Workflow.SeriesId!, StringComparer.Ordinal)
            .Select(group => new PixelExactSeriesProgress(
                group.Key,
                group.Count(item => item.Request.IsCompleted || completedRequestKeys.Contains(item.Request.RequestKey)),
                group.Count()))
            .OrderBy(item => item.SeriesId, StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed record PixelExactSeriesProgress(string SeriesId, int CompletedPhases, int TotalPhases)
{
    public bool IsOpen => CompletedPhases < TotalPhases;
}

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
    public int? PixelOutputCount { get; init; }
    public int? TotalPhases { get; init; }
    public int? Phase { get; init; }
    public int? OutputIndex { get; init; }
    public string? CollectionOrigin { get; init; }
    public string? ReferenceOrigin { get; init; }
    public string? LegacyMarker { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool IsValid => Kind != QueuePromptWorkflowKind.Invalid && Errors.Count == 0;
    public bool IsPixelExact => Kind is QueuePromptWorkflowKind.PixelExactSeed
        or QueuePromptWorkflowKind.PixelExactRef
        or QueuePromptWorkflowKind.PixelExactOutput;
}

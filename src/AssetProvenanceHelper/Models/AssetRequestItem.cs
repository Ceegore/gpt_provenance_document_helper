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
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

    public Core.Generation.AlphaRequirement Alpha { get; init; } =
        Core.Generation.AlphaRequirement.Unknown;

    public bool IsCompleted { get; set; }
}

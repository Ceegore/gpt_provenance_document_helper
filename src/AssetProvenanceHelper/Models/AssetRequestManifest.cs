namespace AssetProvenanceHelper.Models;

public sealed class AssetRequestManifest
{
    public int Version { get; init; }

    public required string SourcePath { get; init; }

    public required string ManifestFingerprint { get; init; }

    public required IReadOnlyList<AssetRequestItem> Items { get; init; }
}
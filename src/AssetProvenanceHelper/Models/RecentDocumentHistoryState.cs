namespace AssetProvenanceHelper.Models;

public sealed class RecentDocumentHistoryState
{
    public int SchemaVersion { get; set; } =
        1;

    public List<RecentDocumentEntry> Entries { get; set; } =
        new();
}
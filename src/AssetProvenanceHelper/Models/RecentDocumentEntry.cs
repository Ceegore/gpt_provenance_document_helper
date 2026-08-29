namespace AssetProvenanceHelper.Models;

public enum ProvenanceDocumentKind
{
    Reference = 0,
    Final = 1
}

public sealed class RecentDocumentEntry
{
    public string Path { get; set; } =
        string.Empty;

    public string AssetName { get; set; } =
        string.Empty;

    public ProvenanceDocumentKind Kind { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
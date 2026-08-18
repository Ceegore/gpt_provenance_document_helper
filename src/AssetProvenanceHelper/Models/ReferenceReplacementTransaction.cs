namespace AssetProvenanceHelper.Models;

public sealed class ReferenceReplacementTransaction
{
    public required string TransactionId { get; init; }

    public required AssetSession OldSession { get; init; }

    public required AssetSession NewSession { get; init; }

    public required string BackupReferencePath { get; init; }

    public required string BackupProvenancePath { get; init; }

    public bool IsCommitted { get; internal set; }
}

namespace AssetProvenanceHelper.Models;

public sealed class ReferenceReplacementTransaction
{
    public required string TransactionId { get; init; }

    public required AssetSession OldSession { get; init; }

    public required AssetSession NewSession { get; init; }

    public required string BackupReferencePath { get; init; }

    public required string BackupProvenancePath { get; init; }

    public string TempNewReferencePath { get; set; } = string.Empty;

    public string TempNewProvenancePath { get; set; } = string.Empty;

    public bool IsCommitted { get; internal set; }

    public ReferenceReplacementJournal ToJournal(ReferenceReplacementPhase phase) =>
        new()
        {
            TransactionId = TransactionId,
            Phase = phase,
            OldSession = OldSession,
            NewSession = NewSession,
            BackupReferencePath = BackupReferencePath,
            BackupProvenancePath = BackupProvenancePath,
            TempNewReferencePath = TempNewReferencePath,
            TempNewProvenancePath = TempNewProvenancePath
        };
}

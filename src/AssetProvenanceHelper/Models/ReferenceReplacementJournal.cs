namespace AssetProvenanceHelper.Models;

public enum ReferenceReplacementPhase
{
    Prepared = 0,
    OldBackupPending = 1,
    OldBackedUp = 2,
    NewPromotionPending = 3,
    NewPromoted = 4,
    SessionSwitchPending = 5,
    SessionSwitched = 6,
    CleanupPending = 7
}

public sealed class ReferenceReplacementJournal
{
    public string TransactionId { get; set; } = string.Empty;

    public ReferenceReplacementPhase Phase { get; set; } = ReferenceReplacementPhase.Prepared;

    public AssetSession OldSession { get; set; } = new();

    public AssetSession NewSession { get; set; } = new();

    public string BackupReferencePath { get; set; } = string.Empty;

    public string BackupProvenancePath { get; set; } = string.Empty;

    public string TempNewReferencePath { get; set; } = string.Empty;

    public string TempNewProvenancePath { get; set; } = string.Empty;
}

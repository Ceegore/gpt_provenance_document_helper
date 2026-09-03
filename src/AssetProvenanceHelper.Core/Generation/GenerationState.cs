namespace AssetProvenanceHelper.Core.Generation;

public sealed class GenerationState
{
    public int SchemaVersion { get; set; } = 1;
    public List<GenerationItemRecord> Items { get; set; } = [];
    public List<GenerationBatchRecord> Batches { get; set; } = [];
}

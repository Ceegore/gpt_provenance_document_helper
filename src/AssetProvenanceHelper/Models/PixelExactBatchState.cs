namespace AssetProvenanceHelper.Models;

public enum PixelExactOutputCommitState
{
    Staged = 0,
    CommitInProgress = 1,
    AssetCommitted = 2,
    QueueCompleted = 3
}

public sealed class PixelExactStagedOutput
{
    public int OutputIndex { get; set; }
    public int Phase { get; set; }
    public string OriginalSourcePath { get; set; } = string.Empty;
    public string StagedPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public PixelExactOutputCommitState State { get; set; } = PixelExactOutputCommitState.Staged;
    public string? ManifestFingerprint { get; set; }
    public string? RequestKey { get; set; }
    public string? AssetName { get; set; }
    public AssetSession? ExpectedCommitSession { get; set; }
    public string? AssetFolderPath { get; set; }
    public DateTimeOffset? AssetCommittedAtUtc { get; set; }
}

public sealed class PixelExactBatchState
{
    public int SchemaVersion { get; set; } = 1;
    public string SeriesId { get; set; } = string.Empty;
    public bool HasCanonicalSeriesIdentity { get; set; }
    public int TotalPhases { get; set; }
    public int BundleCount { get; set; }
    public string? CollectionOrigin { get; set; }
    public string? ReferenceOrigin { get; set; }
    public string? BatchId { get; set; }
    public bool SeedCommitted { get; set; }
    public bool SeedQueueCompleted { get; set; }
    public string? SeedManifestFingerprint { get; set; }
    public string? SeedRequestKey { get; set; }
    public AssetSession? SeedExpectedSession { get; set; }
    public string? MasterAssetName { get; set; }
    public string? MasterReferencePath { get; set; }
    public string? MasterReferenceSha256 { get; set; }
    public DateTimeOffset? MasterProcessedAt { get; set; }
    public ProviderTemplateSnapshot? MasterProviderTemplate { get; set; }
    public string? CollectionManifestFingerprint { get; set; }
    public string? CollectionRequestKey { get; set; }
    public string? CollectionGenerationPrompt { get; set; }
    public string? CollectionGenerationPromptSha256 { get; set; }
    public ProviderTemplateSnapshot? BundleProviderTemplate { get; set; }
    public List<PixelExactStagedOutput> Outputs { get; set; } = new();
    public bool Completed { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

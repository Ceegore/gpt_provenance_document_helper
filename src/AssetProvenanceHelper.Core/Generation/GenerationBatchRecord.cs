namespace AssetProvenanceHelper.Core.Generation;

public sealed record GenerationBatchRecord(
    string LocalBatchId,
    string ManifestFingerprint,
    string ProviderId,
    string Model,
    string Quality,
    IReadOnlyList<string> RequestKeys,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int SchemaVersion = 1,
    string? ProviderInputFileId = null,
    string? ProviderBatchId = null,
    string? ProviderOutputFileId = null,
    string? ProviderErrorFileId = null,
    int SubmittedCount = 0,
    int CompletedCount = 0,
    int FailedCount = 0,
    string? ErrorMessage = null,
    DateTimeOffset? CompletedAtUtc = null);

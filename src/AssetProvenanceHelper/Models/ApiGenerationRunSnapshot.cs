namespace AssetProvenanceHelper.Models;

public sealed record ApiGenerationRunSnapshot(
    string ManifestFingerprint,
    string ProviderId,
    string Model,
    string Quality,
    int DirectStartsPerMinute,
    int DirectMaxConcurrency,
    int DirectRetryAttempts,
    DateTimeOffset CreatedAtUtc);

namespace AssetProvenanceHelper.Models;

public sealed record ApiCandidateMetadata(
    string CandidateId,
    string Provider,
    string Model,
    string Mode,
    string CustomId,
    string TargetResolution,
    string ProviderResolution,
    string RawSha256,
    string NormalizedSha256,
    string NormalizedImagePath,
    DateTimeOffset CreatedAtUtc,
    string? ProviderRequestId = null,
    string? BatchId = null);

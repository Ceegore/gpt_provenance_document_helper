namespace AssetProvenanceHelper.Core.Generation;

public sealed record ImageGenerationCandidate(
    string CandidateId,
    string CustomId,
    byte[] RawBytes,
    string RawSha256,
    int ProviderWidth,
    int ProviderHeight,
    string? ProviderRequestId = null,
    string? BatchId = null);

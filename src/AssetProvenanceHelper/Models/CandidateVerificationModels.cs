namespace AssetProvenanceHelper.Models;

public sealed record VerifiedApiCandidate(
    string ImagePath,
    ApiCandidateMetadata Metadata);

public sealed record CandidateVerificationResult(
    bool IsValid,
    VerifiedApiCandidate? Candidate,
    string? ErrorCode,
    string? ErrorMessage);

namespace AssetProvenanceHelper.Core.Generation;

public sealed record ImageGenerationSpec(
    string ManifestFingerprint,
    string RequestKey,
    string AssetName,
    string FileName,
    string Prompt,
    int TargetWidth,
    int TargetHeight,
    AlphaRequirement AlphaRequirement,
    string ProviderId,
    string Model,
    string Quality,
    int GenerationWidth,
    int GenerationHeight,
    string CustomId);

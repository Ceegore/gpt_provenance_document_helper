namespace AssetProvenanceHelper.Core.Generation;

public sealed record ProviderCapabilities(
    bool SupportsTextToImage,
    bool SupportsBatch,
    bool SupportsTransparentBackground,
    bool SupportsReferenceImages,
    bool SupportsArbitrarySize);

namespace AssetProvenanceHelper.Core.Generation;

public sealed record ImageSizePlan(
    int TargetWidth,
    int TargetHeight,
    int GenerationWidth,
    int GenerationHeight,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    bool RequiresResize);

namespace AssetProvenanceHelper.Core.Generation;

public static class ImageSizePlanner
{
    public const int MinPixels = 655_360;
    public const int MaxPixels = 8_294_400;
    public const int MaxEdge = 3840;
    public const int RasterMultiple = 16;
    public const double MaxAspectRatio = 3.0;

    public static ImageSizePlan Plan(int targetWidth, int targetHeight)
    {
        if (targetWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth), targetWidth, "Target width must be greater than zero.");
        }

        if (targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetHeight), targetHeight, "Target height must be greater than zero.");
        }

        var longEdge = Math.Max(targetWidth, targetHeight);
        var shortEdge = Math.Min(targetWidth, targetHeight);
        var ratio = (double)longEdge / shortEdge;

        if (ratio > MaxAspectRatio + 0.0001)
        {
            throw new ArgumentException($"Aspect ratio {ratio:F2}:1 exceeds the maximum allowed ratio of {MaxAspectRatio}:1.");
        }

        double baseW = targetWidth;
        double baseH = targetHeight;
        long targetPixels = (long)targetWidth * targetHeight;

        if (targetPixels < MinPixels)
        {
            var scale = Math.Sqrt((double)MinPixels / targetPixels);
            baseW = targetWidth * scale;
            baseH = targetHeight * scale;
        }

        var wGen = (int)Math.Ceiling(baseW / RasterMultiple) * RasterMultiple;
        var hGen = (int)Math.Ceiling(baseH / RasterMultiple) * RasterMultiple;

        var targetAspect = (double)targetWidth / targetHeight;

        while ((long)wGen * hGen < MinPixels)
        {
            var currentAspect = (double)wGen / hGen;
            if (currentAspect > targetAspect)
            {
                hGen += RasterMultiple;
            }
            else
            {
                wGen += RasterMultiple;
            }
        }

        if (wGen > MaxEdge || hGen > MaxEdge)
        {
            throw new ArgumentException($"Calculated generation dimensions {wGen}x{hGen} exceed maximum edge of {MaxEdge}px.");
        }

        if ((long)wGen * hGen > MaxPixels)
        {
            throw new ArgumentException($"Calculated generation dimensions {wGen}x{hGen} ({((long)wGen * hGen)} pixels) exceed maximum allowed {MaxPixels} pixels.");
        }

        int cropX;
        int cropY;
        int cropWidth;
        int cropHeight;

        var genAspect = (double)wGen / hGen;

        if (Math.Abs(genAspect - targetAspect) < 0.000001)
        {
            cropX = 0;
            cropY = 0;
            cropWidth = wGen;
            cropHeight = hGen;
        }
        else if (genAspect > targetAspect)
        {
            // Canvas is wider than target aspect -> crop width
            cropHeight = hGen;
            cropWidth = Math.Clamp((int)Math.Round(cropHeight * targetAspect), 1, wGen);
            cropX = Math.Clamp((wGen - cropWidth) / 2, 0, wGen - cropWidth);
            cropY = 0;
        }
        else
        {
            // Canvas is taller than target aspect -> crop height
            cropWidth = wGen;
            cropHeight = Math.Clamp((int)Math.Round(cropWidth / targetAspect), 1, hGen);
            cropX = 0;
            cropY = Math.Clamp((hGen - cropHeight) / 2, 0, hGen - cropHeight);
        }

        var requiresResize = cropWidth != targetWidth || cropHeight != targetHeight;

        return new ImageSizePlan(
            TargetWidth: targetWidth,
            TargetHeight: targetHeight,
            GenerationWidth: wGen,
            GenerationHeight: hGen,
            CropX: cropX,
            CropY: cropY,
            CropWidth: cropWidth,
            CropHeight: cropHeight,
            RequiresResize: requiresResize);
    }
}

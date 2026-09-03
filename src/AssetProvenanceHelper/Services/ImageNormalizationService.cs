using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using AssetProvenanceHelper.Core.Generation;

namespace AssetProvenanceHelper.Services;

public sealed record ImageNormalizationResult(
    byte[] NormalizedBytes,
    string RawSha256,
    string NormalizedSha256,
    int NormalizedWidth,
    int NormalizedHeight);

public static class ImageNormalizationService
{
    public static ImageNormalizationResult Normalize(byte[] rawBytes, ImageSizePlan plan)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        ArgumentNullException.ThrowIfNull(plan);

        if (rawBytes.Length == 0)
        {
            throw new ArgumentException("Raw image bytes cannot be empty.", nameof(rawBytes));
        }

        var rawSha256 = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

        using var memoryStream = new MemoryStream(rawBytes);
        using var rawImage = Image.FromStream(memoryStream, useEmbeddedColorManagement: false, validateImageData: true);

        if (rawImage.Width != plan.GenerationWidth || rawImage.Height != plan.GenerationHeight)
        {
            throw new InvalidOperationException($"Raw image dimensions {rawImage.Width}x{rawImage.Height} do not match expected provider resolution {plan.GenerationWidth}x{plan.GenerationHeight}.");
        }

        // 1. Crop to target aspect ratio
        using var cropped = new Bitmap(plan.CropWidth, plan.CropHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;

            var srcRect = new Rectangle(plan.CropX, plan.CropY, plan.CropWidth, plan.CropHeight);
            var destRect = new Rectangle(0, 0, plan.CropWidth, plan.CropHeight);
            g.DrawImage(rawImage, destRect, srcRect, GraphicsUnit.Pixel);
        }

        Bitmap finalBitmap;
        if (plan.RequiresResize)
        {
            finalBitmap = new Bitmap(plan.TargetWidth, plan.TargetHeight, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(finalBitmap);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;

            var destRect = new Rectangle(0, 0, plan.TargetWidth, plan.TargetHeight);
            var srcRect = new Rectangle(0, 0, plan.CropWidth, plan.CropHeight);
            g.DrawImage(cropped, destRect, srcRect, GraphicsUnit.Pixel);
        }
        else
        {
            finalBitmap = (Bitmap)cropped.Clone();
        }

        byte[] normalizedBytes;
        using (finalBitmap)
        {
            if (finalBitmap.Width != plan.TargetWidth || finalBitmap.Height != plan.TargetHeight)
            {
                throw new InvalidOperationException($"Normalized image dimensions {finalBitmap.Width}x{finalBitmap.Height} do not match target resolution {plan.TargetWidth}x{plan.TargetHeight}.");
            }

            using var outStream = new MemoryStream();
            finalBitmap.Save(outStream, ImageFormat.Png);
            normalizedBytes = outStream.ToArray();
        }

        using (var verifyStream = new MemoryStream(normalizedBytes))
        using (var verifyImage = Image.FromStream(verifyStream, useEmbeddedColorManagement: false, validateImageData: true))
        {
            if (verifyImage.Width != plan.TargetWidth || verifyImage.Height != plan.TargetHeight)
            {
                throw new InvalidOperationException($"Re-loaded normalized PNG dimensions {verifyImage.Width}x{verifyImage.Height} do not match target resolution {plan.TargetWidth}x{plan.TargetHeight}.");
            }
        }

        var normalizedSha256 = Convert.ToHexString(SHA256.HashData(normalizedBytes)).ToLowerInvariant();

        return new ImageNormalizationResult(
            NormalizedBytes: normalizedBytes,
            RawSha256: rawSha256,
            NormalizedSha256: normalizedSha256,
            NormalizedWidth: plan.TargetWidth,
            NormalizedHeight: plan.TargetHeight);
    }
}

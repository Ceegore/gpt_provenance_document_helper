using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiStagingDurabilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GeneratedImageStagingService _stagingService;

    public ApiStagingDurabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_stage_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _stagingService = new GeneratedImageStagingService(_tempDir);
    }

    public void Dispose()
    {
        GeneratedImageStagingService.OnBeforeCandidatePromoteForTests = null;
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private static byte[] CreateTestPng(int width, int height, Color color)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [Fact]
    public void RawSave_HappensBeforeNormalization_AndRawFileExists()
    {
        var rawBytes = CreateTestPng(816, 816, Color.Purple);
        var normBytes = CreateTestPng(512, 512, Color.Purple);
        var candId = "cand-order-1";

        // Save Raw
        var rawPath = _stagingService.SaveRawCandidate("fp1", "k1", candId, rawBytes);

        Assert.True(File.Exists(rawPath));
        var savedRawBytes = File.ReadAllBytes(rawPath);
        Assert.Equal(rawBytes, savedRawBytes);

        // Raw file exists BEFORE CompleteCandidate is called
        var finalPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), $"{candId}.png");
        Assert.False(File.Exists(finalPath));

        var metadata = new ApiCandidateMetadata(
            CandidateId: candId,
            Provider: "OpenAI",
            Model: "gpt-image-2",
            Mode: "direct",
            CustomId: "aph-custom",
            TargetResolution: "512x512",
            ProviderResolution: "816x816",
            RawSha256: "raw-sha",
            NormalizedSha256: "norm-sha",
            NormalizedImagePath: string.Empty,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var completedPath = _stagingService.CompleteCandidate("fp1", "k1", candId, normBytes, metadata);

        Assert.Equal(finalPath, completedPath);
        Assert.True(File.Exists(completedPath));
        Assert.True(File.Exists(rawPath), "Raw path must still exist after candidate completion.");
    }

    [Fact]
    public void CandidateCollision_DoesNotOverwriteRaw()
    {
        var raw1 = new byte[] { 1, 2, 3, 4 };
        var raw2 = new byte[] { 5, 6, 7, 8 };
        var candId = "cand-collision";

        var path1 = _stagingService.SaveRawCandidate("fp1", "k1", candId, raw1);
        Assert.True(File.Exists(path1));

        // Second write with identical candidate ID must throw IOException, not overwrite
        var ex = Assert.Throws<IOException>(() =>
            _stagingService.SaveRawCandidate("fp1", "k1", candId, raw2));

        Assert.Contains("Destination already exists", ex.Message);
        Assert.Equal(raw1, File.ReadAllBytes(path1));
    }

    [Fact]
    public void CandidateCollision_DoesNotOverwriteFinal()
    {
        var raw = CreateTestPng(816, 816, Color.Blue);
        var norm1 = CreateTestPng(512, 512, Color.Blue);
        var norm2 = CreateTestPng(512, 512, Color.Red);
        var candId = "cand-final-collision";

        var metadata = new ApiCandidateMetadata(
            CandidateId: candId,
            Provider: "OpenAI",
            Model: "gpt-image-2",
            Mode: "direct",
            CustomId: "custom",
            TargetResolution: "512x512",
            ProviderResolution: "816x816",
            RawSha256: "raw-sha",
            NormalizedSha256: "norm-sha",
            NormalizedImagePath: string.Empty,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        _stagingService.SaveRawCandidate("fp1", "k1", candId, raw);
        var finalPath = _stagingService.CompleteCandidate("fp1", "k1", candId, norm1, metadata);

        Assert.True(File.Exists(finalPath));

        var ex = Assert.Throws<IOException>(() =>
            _stagingService.CompleteCandidate("fp1", "k1", candId, norm2, metadata));

        Assert.Contains("Destination already exists", ex.Message);
    }

    [Fact]
    public void PromotionHookThrows_RawStillExists_ItemNeverReady()
    {
        var jobStorePath = Path.Combine(_tempDir, "jobs.json");
        var jobStore = new GenerationJobStore(jobStorePath);
        var candId = "cand-fail-promote";
        var rawBytes = CreateTestPng(816, 816, Color.Orange);

        var itemRecord = new GenerationItemRecord(
            ManifestFingerprint: "fp1",
            RequestKey: "k1",
            AssetName: "item1",
            FileName: "item1.png",
            Mode: GenerationMode.Direct,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "custom-k1",
            Status: GenerationItemStatus.DirectInFlight,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        jobStore.UpsertItem(itemRecord);

        // Save raw candidate first
        var rawPath = _stagingService.SaveRawCandidate("fp1", "k1", candId, rawBytes);
        var normalizingRecord = itemRecord with
        {
            Status = GenerationItemStatus.Normalizing,
            CandidateId = candId,
            ProviderRawPath = rawPath,
            RawSha256 = "raw-sha",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        jobStore.UpsertItem(normalizingRecord);

        // Set hook to simulate promote failure
        GeneratedImageStagingService.OnBeforeCandidatePromoteForTests = _ =>
            throw new InvalidOperationException("Disk failure before promote");

        var metadata = new ApiCandidateMetadata(
            CandidateId: candId,
            Provider: "OpenAI",
            Model: "gpt-image-2",
            Mode: "direct",
            CustomId: "custom-k1",
            TargetResolution: "512x512",
            ProviderResolution: "816x816",
            RawSha256: "raw-sha",
            NormalizedSha256: "norm-sha",
            NormalizedImagePath: string.Empty,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        try
        {
            _stagingService.CompleteCandidate("fp1", "k1", candId, [1, 2, 3], metadata);
        }
        catch
        {
            jobStore.UpsertItem(normalizingRecord with
            {
                Status = GenerationItemStatus.FailedPermanent,
                ErrorCode = "promotion_failed",
                ErrorMessage = "Simulated failure",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        // Verify:
        // 1. Raw file STILL exists!
        Assert.True(File.Exists(rawPath), "Raw bytes must remain on disk even if promote fails.");

        // 2. Final normalized image does NOT exist
        var finalPath = Path.Combine(_stagingService.GetItemDirectory("fp1", "k1"), $"{candId}.png");
        Assert.False(File.Exists(finalPath));

        // 3. Item in job store is NEVER Ready
        var savedItem = jobStore.GetItem("fp1", "k1");
        Assert.NotNull(savedItem);
        Assert.NotEqual(GenerationItemStatus.Ready, savedItem.Status);
        Assert.Equal(GenerationItemStatus.FailedPermanent, savedItem.Status);
        Assert.Equal(rawPath, savedItem.ProviderRawPath);
    }
}

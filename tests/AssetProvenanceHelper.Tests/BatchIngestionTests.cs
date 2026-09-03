using System.Drawing;
using System.Drawing.Imaging;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class BatchIngestionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GenerationJobStore _jobStore;
    private readonly GeneratedImageStagingService _stagingService;
    private readonly BatchIngestionService _ingestionService;

    public BatchIngestionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_batch_ingest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _jobStore = new GenerationJobStore(Path.Combine(_tempDir, "jobs.json"));
        _stagingService = new GeneratedImageStagingService(Path.Combine(_tempDir, "staging"));
        _ingestionService = new BatchIngestionService(_jobStore, _stagingService);
    }

    public void Dispose()
    {
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

    private (GenerationBatchRecord Batch, GenerationItemRecord FirstItem, GenerationItemRecord SecondItem) SetupBatchWithTwoItems()
    {
        var localBatchId = "batch-local-1";
        var fp = "fp-manifest";

        var item1 = new GenerationItemRecord(
            ManifestFingerprint: fp,
            RequestKey: "k1",
            AssetName: "asset1",
            FileName: "asset1.png",
            Mode: GenerationMode.Batch,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-custom-k1",
            Status: GenerationItemStatus.BatchSubmitted,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            BatchId: localBatchId);

        var item2 = new GenerationItemRecord(
            ManifestFingerprint: fp,
            RequestKey: "k2",
            AssetName: "asset2",
            FileName: "asset2.png",
            Mode: GenerationMode.Batch,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            TargetWidth: 512,
            TargetHeight: 512,
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-custom-k2",
            Status: GenerationItemStatus.BatchSubmitted,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            BatchId: localBatchId);

        _jobStore.UpsertItem(item1);
        _jobStore.UpsertItem(item2);

        var batch = new GenerationBatchRecord(
            LocalBatchId: localBatchId,
            ManifestFingerprint: fp,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            RequestKeys: ["k1", "k2"],
            Status: "Submitted",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            ProviderInputFileId: "file-in-1",
            ProviderBatchId: "batch-prov-1",
            SubmittedCount: 2);

        _jobStore.UpsertBatch(batch);

        return (batch, item1, item2);
    }

    [Fact]
    public void BatchIngestion_ErrorFileParsed_MarksItemFailedPermanent()
    {
        var (batch, item1, item2) = SetupBatchWithTwoItems();
        var imgBytes = CreateTestPng(816, 816, Color.Green);

        var status = new BatchStatusResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Status: "completed",
            OutputFileId: "file-out-1",
            ErrorFileId: "file-err-1",
            TotalCount: 2,
            CompletedCount: 1,
            FailedCount: 1);

        var downloadResult = new BatchDownloadResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Items:
            [
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: imgBytes, StatusCode: 200, ErrorCode: null, ErrorMessage: null),
                new BatchItemOutput(item2.CustomId, IsSuccess: false, ImageBytes: null, StatusCode: 400, ErrorCode: "invalid_prompt", ErrorMessage: "Safety violation")
            ]);

        var summary = _ingestionService.IngestResults(batch, status, downloadResult);

        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(1, summary.FailureCount);

        var updatedItem1 = _jobStore.GetItem(item1.ManifestFingerprint, item1.RequestKey);
        var updatedItem2 = _jobStore.GetItem(item2.ManifestFingerprint, item2.RequestKey);

        Assert.NotNull(updatedItem1);
        Assert.Equal(GenerationItemStatus.Ready, updatedItem1.Status);
        Assert.True(File.Exists(updatedItem1.StagedOutputPath));

        Assert.NotNull(updatedItem2);
        Assert.Equal(GenerationItemStatus.FailedPermanent, updatedItem2.Status);
        Assert.Equal("invalid_prompt", updatedItem2.ErrorCode);
        Assert.Equal("Safety violation", updatedItem2.ErrorMessage);
    }

    [Fact]
    public void BatchIngestion_ErrorFileDownloadFails_StopsWithoutBlindReady()
    {
        var (batch, item1, item2) = SetupBatchWithTwoItems();

        // Simulate that item1 was already completed/ready from a partial ingestion beforehand
        _jobStore.UpsertItem(item1 with
        {
            Status = GenerationItemStatus.Ready,
            StagedOutputPath = "some/valid/path.png"
        });

        // Error file download fails with exception
        var exception = new HttpRequestException("HTTP 500 when fetching error_file_id");
        _ingestionService.HandleDownloadInterruption(batch, exception);

        var updatedItem1 = _jobStore.GetItem(item1.ManifestFingerprint, item1.RequestKey);
        var updatedItem2 = _jobStore.GetItem(item2.ManifestFingerprint, item2.RequestKey);

        Assert.NotNull(updatedItem1);
        // Item1 was already Ready, so partial success remains preserved!
        Assert.Equal(GenerationItemStatus.Ready, updatedItem1.Status);

        Assert.NotNull(updatedItem2);
        // Item2 was in-flight, so it MUST transition to UncertainAfterInterruption (NOT blind-ready)
        Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, updatedItem2.Status);
        Assert.Equal("batch_results_download_failed", updatedItem2.ErrorCode);

        var updatedBatch = _jobStore.GetBatch(batch.LocalBatchId);
        Assert.NotNull(updatedBatch);
        Assert.Equal("DownloadingFailed", updatedBatch.Status);
    }

    [Fact]
    public void BatchIngestion_UnknownCustomId_ThrowsOrLogsError()
    {
        var (batch, item1, _) = SetupBatchWithTwoItems();
        var imgBytes = CreateTestPng(816, 816, Color.Blue);

        var status = new BatchStatusResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Status: "completed",
            OutputFileId: "file-out-1",
            ErrorFileId: null,
            TotalCount: 2,
            CompletedCount: 2,
            FailedCount: 0);

        var downloadResult = new BatchDownloadResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Items:
            [
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: imgBytes, StatusCode: 200, ErrorCode: null, ErrorMessage: null),
                new BatchItemOutput("aph-unrecognized-id-999", IsSuccess: true, ImageBytes: imgBytes, StatusCode: 200, ErrorCode: null, ErrorMessage: null)
            ]);

        var summary = _ingestionService.IngestResults(batch, status, downloadResult);

        Assert.Contains("aph-unrecognized-id-999", summary.UnknownCustomIds);

        var updatedBatch = _jobStore.GetBatch(batch.LocalBatchId);
        Assert.NotNull(updatedBatch);
        Assert.Contains("Unknown custom IDs", updatedBatch.ErrorMessage);
    }

    [Fact]
    public void BatchIngestion_DuplicateCustomId_ProcessesOnlyFirst()
    {
        var (batch, item1, _) = SetupBatchWithTwoItems();
        var img1 = CreateTestPng(816, 816, Color.Green);
        var img2 = CreateTestPng(816, 816, Color.Red);

        var status = new BatchStatusResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Status: "completed",
            OutputFileId: "file-out-1",
            ErrorFileId: null,
            TotalCount: 2,
            CompletedCount: 2,
            FailedCount: 0);

        var downloadResult = new BatchDownloadResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Items:
            [
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: img1, StatusCode: 200, ErrorCode: null, ErrorMessage: null),
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: img2, StatusCode: 200, ErrorCode: null, ErrorMessage: null)
            ]);

        var summary = _ingestionService.IngestResults(batch, status, downloadResult);

        // Only first occurrence processed
        Assert.Equal(1, summary.SuccessCount);
        Assert.Contains(item1.CustomId, summary.DuplicateCustomIds);

        var updatedBatch = _jobStore.GetBatch(batch.LocalBatchId);
        Assert.NotNull(updatedBatch);
        Assert.Contains("Duplicate custom IDs", updatedBatch.ErrorMessage);
    }

    [Fact]
    public void BatchIngestion_MissingCustomId_MarksUncertainNotReady()
    {
        var (batch, item1, item2) = SetupBatchWithTwoItems();
        var img1 = CreateTestPng(816, 816, Color.Cyan);

        var status = new BatchStatusResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Status: "completed",
            OutputFileId: "file-out-1",
            ErrorFileId: null,
            TotalCount: 2,
            CompletedCount: 1,
            FailedCount: 0);

        // Output only contains item1; item2 never appeared anywhere in results!
        var downloadResult = new BatchDownloadResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Items:
            [
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: img1, StatusCode: 200, ErrorCode: null, ErrorMessage: null)
            ]);

        var summary = _ingestionService.IngestResults(batch, status, downloadResult);

        Assert.Equal(1, summary.SuccessCount);
        Assert.Contains(item2.CustomId, summary.MissingCustomIds);

        var updatedItem1 = _jobStore.GetItem(item1.ManifestFingerprint, item1.RequestKey);
        var updatedItem2 = _jobStore.GetItem(item2.ManifestFingerprint, item2.RequestKey);

        Assert.NotNull(updatedItem1);
        Assert.Equal(GenerationItemStatus.Ready, updatedItem1.Status);

        Assert.NotNull(updatedItem2);
        // Item2 must NEVER remain BatchSubmitted or become Ready; it must be UncertainAfterInterruption
        Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, updatedItem2.Status);
        Assert.Equal("missing_from_batch_results", updatedItem2.ErrorCode);
    }
}

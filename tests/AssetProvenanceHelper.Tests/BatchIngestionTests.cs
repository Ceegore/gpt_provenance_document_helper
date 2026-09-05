using System.Drawing;
using System.Drawing.Imaging;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;
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
        GeneratedImageStagingService.OnBeforeSaveRawForTests = null;
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
    public void BatchResults_UnknownCustomId_FailsBeforeAnyCandidateMutation()
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

        var ex = Assert.Throws<InvalidDataException>(() =>
            _ingestionService.IngestResults(batch, status, downloadResult));
        Assert.Contains("aph-unrecognized-id-999", ex.Message);

        var stored = _jobStore.GetItemsForBatch(batch.LocalBatchId);
        Assert.DoesNotContain(stored, item => item.Status == GenerationItemStatus.Ready);
        Assert.All(stored, item => Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, item.Status));

        var files = Directory.Exists(_stagingService.BaseStagingPath)
            ? Directory.GetFiles(_stagingService.BaseStagingPath, "*.png", SearchOption.AllDirectories)
            : [];
        Assert.Empty(files);
    }

    [Fact]
    public void BatchResults_DuplicateCustomId_FailsBeforeAnyCandidateMutation()
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

        var ex = Assert.Throws<InvalidDataException>(() =>
            _ingestionService.IngestResults(batch, status, downloadResult));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);

        var stored = _jobStore.GetItemsForBatch(batch.LocalBatchId);
        Assert.DoesNotContain(stored, item => item.Status == GenerationItemStatus.Ready);
        Assert.All(stored, item => Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, item.Status));
    }

    [Fact]
    public void BatchResults_EmptyCustomId_FailsBeforeAnyCandidateMutation()
    {
        var (batch, item1, _) = SetupBatchWithTwoItems();
        var img = CreateTestPng(816, 816, Color.Green);

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
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: img, StatusCode: 200, ErrorCode: null, ErrorMessage: null),
                new BatchItemOutput("   ", IsSuccess: true, ImageBytes: img, StatusCode: 200, ErrorCode: null, ErrorMessage: null)
            ]);

        var ex = Assert.Throws<InvalidDataException>(() =>
            _ingestionService.IngestResults(batch, status, downloadResult));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);

        var stored = _jobStore.GetItemsForBatch(batch.LocalBatchId);
        Assert.DoesNotContain(stored, item => item.Status == GenerationItemStatus.Ready);
    }

    [Fact]
    public void BatchResults_CustomIdCaseChanged_IsUnknown()
    {
        var (batch, item1, _) = SetupBatchWithTwoItems();
        var img = CreateTestPng(816, 816, Color.Green);

        var status = new BatchStatusResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Status: "completed",
            OutputFileId: "file-out-1",
            ErrorFileId: null,
            TotalCount: 1,
            CompletedCount: 1,
            FailedCount: 0);

        // Case-changed custom ID should NOT match because StringComparer.Ordinal is required
        var upperCustomId = item1.CustomId.ToUpperInvariant();
        Assert.NotEqual(item1.CustomId, upperCustomId);

        var downloadResult = new BatchDownloadResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Items:
            [
                new BatchItemOutput(upperCustomId, IsSuccess: true, ImageBytes: img, StatusCode: 200, ErrorCode: null, ErrorMessage: null)
            ]);

        Assert.Throws<InvalidDataException>(() =>
            _ingestionService.IngestResults(batch, status, downloadResult));

        var stored = _jobStore.GetItemsForBatch(batch.LocalBatchId);
        Assert.DoesNotContain(stored, item => item.Status == GenerationItemStatus.Ready);
    }

    [Fact]
    public void BatchResults_ValidOutOfOrder_StillMapsCorrectly()
    {
        var (batch, item1, item2) = SetupBatchWithTwoItems();
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

        // Order reversed: item2 first, then item1
        var downloadResult = new BatchDownloadResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Items:
            [
                new BatchItemOutput(item2.CustomId, IsSuccess: true, ImageBytes: img2, StatusCode: 200, ErrorCode: null, ErrorMessage: null),
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: img1, StatusCode: 200, ErrorCode: null, ErrorMessage: null)
            ]);

        var summary = _ingestionService.IngestResults(batch, status, downloadResult);
        Assert.Equal(2, summary.SuccessCount);

        var updated1 = _jobStore.GetItem(item1.ManifestFingerprint, item1.RequestKey)!;
        var updated2 = _jobStore.GetItem(item2.ManifestFingerprint, item2.RequestKey)!;

        Assert.Equal(GenerationItemStatus.Ready, updated1.Status);
        Assert.Equal(GenerationItemStatus.Ready, updated2.Status);
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

    [Fact]
    public void BatchIngestion_LocalDuplicateCustomId_ThrowsInvalidDataAndMarksUncertain()
    {
        var localBatchId = "batch-local-dup";
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
            CustomId: "dup-custom-id",
            Status: GenerationItemStatus.BatchSubmitted,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            BatchId: localBatchId);

        var item2 = item1 with
        {
            RequestKey = "k2",
            AssetName = "asset2",
            FileName = "asset2.png",
            CustomId = "dup-custom-id"
        };

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
            SubmittedCount: 2,
            CompletedCount: 0,
            FailedCount: 0,
            ProviderBatchId: "batch_remote_dup");

        _jobStore.UpsertBatch(batch);
        _jobStore.UpsertItems([item1, item2]);

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
                new BatchItemOutput("dup-custom-id", IsSuccess: true, ImageBytes: CreateTestPng(816, 816, Color.Cyan), StatusCode: 200, ErrorCode: null, ErrorMessage: null)
            ]);

        var ex = Assert.Throws<InvalidDataException>(() =>
            _ingestionService.IngestResults(batch, status, downloadResult));

        Assert.Contains("duplicate custom_id", ex.Message);

        var items = _jobStore.GetItemsForBatch(localBatchId);
        Assert.All(items, item =>
        {
            Assert.Equal(GenerationItemStatus.UncertainAfterInterruption, item.Status);
            Assert.Equal("batch_result_mapping_invalid", item.ErrorCode);
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Batch_ResultRawWriteFails_BatchRemainsLocallyActiveAndRetryable()
    {
        var (batch, item1, item2) = SetupBatchWithTwoItems();
        var img1 = CreateTestPng(816, 816, Color.Cyan);
        var img2 = CreateTestPng(816, 816, Color.Magenta);

        var status = new BatchStatusResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Status: "completed",
            OutputFileId: "file-out-123",
            ErrorFileId: null,
            TotalCount: 2,
            CompletedCount: 2,
            FailedCount: 0);

        var downloadResult = new BatchDownloadResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Items:
            [
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: img1, StatusCode: 200, ErrorCode: null, ErrorMessage: null, ProviderRequestId: "req-1"),
                new BatchItemOutput(item2.CustomId, IsSuccess: true, ImageBytes: img2, StatusCode: 200, ErrorCode: null, ErrorMessage: null, ProviderRequestId: "req-2")
            ]);

        // Fail raw write only for item2
        GeneratedImageStagingService.OnBeforeSaveRawForTests = rawPath =>
        {
            if (rawPath.Contains(item2.RequestKey))
            {
                throw new IOException("Simulated disk error on item2 raw write");
            }
        };

        try
        {
            var summary = _ingestionService.IngestResults(batch, status, downloadResult);

            // Ingestion summary assertions
            Assert.True(summary.NeedsRemoteResultRedownload);
            Assert.Equal(1, summary.SuccessCount);
            Assert.Equal(1, summary.FailureCount);

            // Item1 should be Ready
            var updatedItem1 = _jobStore.GetItem(item1.ManifestFingerprint, item1.RequestKey)!;
            Assert.Equal(GenerationItemStatus.Ready, updatedItem1.Status);
            Assert.True(File.Exists(updatedItem1.ProviderRawPath));
            Assert.True(File.Exists(updatedItem1.StagedOutputPath));

            // Item2 should be FailedRetryable, NOT FailedPermanent
            var updatedItem2 = _jobStore.GetItem(item2.ManifestFingerprint, item2.RequestKey)!;
            Assert.Equal(GenerationItemStatus.FailedRetryable, updatedItem2.Status);
            Assert.Equal("batch_result_raw_persist_failed", updatedItem2.ErrorCode);
            Assert.Equal("req-2", updatedItem2.ProviderRequestId);

            // Batch record assertions
            var updatedBatch = _jobStore.GetBatch(batch.LocalBatchId)!;
            Assert.Equal("IngestionFailed", updatedBatch.Status);
            Assert.Null(updatedBatch.CompletedAtUtc); // Must remain null so it's not marked terminal
            Assert.Equal("file-out-123", updatedBatch.ProviderOutputFileId);

            // Verify GetActiveBatches still includes this batch
            var activeBatches = _jobStore.GetActiveBatches();
            Assert.Contains(activeBatches, b => b.LocalBatchId == batch.LocalBatchId);
        }
        finally
        {
            GeneratedImageStagingService.OnBeforeSaveRawForTests = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Batch_Reingestion_AlreadyReadyItemIsIdempotentlySkipped_AndDiskRecoverySucceeds()
    {
        var (batch, item1, item2) = SetupBatchWithTwoItems();
        var img1 = CreateTestPng(816, 816, Color.Cyan);
        var img2 = CreateTestPng(816, 816, Color.Magenta);

        var status = new BatchStatusResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Status: "completed",
            OutputFileId: "file-out-123",
            ErrorFileId: null,
            TotalCount: 2,
            CompletedCount: 2,
            FailedCount: 0);

        var downloadResult = new BatchDownloadResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Items:
            [
                new BatchItemOutput(item1.CustomId, IsSuccess: true, ImageBytes: img1, StatusCode: 200, ErrorCode: null, ErrorMessage: null, ProviderRequestId: "req-1"),
                new BatchItemOutput(item2.CustomId, IsSuccess: true, ImageBytes: img2, StatusCode: 200, ErrorCode: null, ErrorMessage: null, ProviderRequestId: "req-2")
            ]);

        // Attempt 1: fail item2 raw write
        GeneratedImageStagingService.OnBeforeSaveRawForTests = rawPath =>
        {
            if (rawPath.Contains(item2.RequestKey))
            {
                throw new IOException("Simulated disk error");
            }
        };

        try
        {
            var summary1 = _ingestionService.IngestResults(batch, status, downloadResult);
            Assert.True(summary1.NeedsRemoteResultRedownload);

            var ready1Before = _jobStore.GetItem(item1.ManifestFingerprint, item1.RequestKey)!;
            var updatedUtcBefore = ready1Before.UpdatedAtUtc;
            var stagedPathBefore = ready1Before.StagedOutputPath;

            // Attempt 2: disk recovered (clear hook)
            GeneratedImageStagingService.OnBeforeSaveRawForTests = null;

            var batchBeforeSecondPoll = _jobStore.GetBatch(batch.LocalBatchId)!;
            var summary2 = _ingestionService.IngestResults(batchBeforeSecondPoll, status, downloadResult);

            Assert.False(summary2.NeedsRemoteResultRedownload);
            Assert.Equal(2, summary2.SuccessCount);
            Assert.Equal(0, summary2.FailureCount);

            // Item1 was idempotently skipped: UpdatedAtUtc and StagedOutputPath unchanged
            var ready1After = _jobStore.GetItem(item1.ManifestFingerprint, item1.RequestKey)!;
            Assert.Equal(GenerationItemStatus.Ready, ready1After.Status);
            Assert.Equal(stagedPathBefore, ready1After.StagedOutputPath);
            Assert.Equal(updatedUtcBefore, ready1After.UpdatedAtUtc);

            // Item2 is now Ready
            var ready2After = _jobStore.GetItem(item2.ManifestFingerprint, item2.RequestKey)!;
            Assert.Equal(GenerationItemStatus.Ready, ready2After.Status);
            Assert.True(File.Exists(ready2After.ProviderRawPath));
            Assert.True(File.Exists(ready2After.StagedOutputPath));

            // Batch is now completed and has CompletedAtUtc set
            var finalBatch = _jobStore.GetBatch(batch.LocalBatchId)!;
            Assert.Equal("completed", finalBatch.Status);
            Assert.NotNull(finalBatch.CompletedAtUtc);

            // Active batches no longer includes this batch
            var activeBatches = _jobStore.GetActiveBatches();
            Assert.DoesNotContain(activeBatches, b => b.LocalBatchId == batch.LocalBatchId);
        }
        finally
        {
            GeneratedImageStagingService.OnBeforeSaveRawForTests = null;
        }
    }

    private sealed class DuplicateJsonlHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _outputJsonl;

        public DuplicateJsonlHttpMessageHandler(string outputJsonl)
        {
            _outputJsonl = outputJsonl;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("files/") && uri.EndsWith("/content"))
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(_outputJsonl, System.Text.Encoding.UTF8, "application/jsonl")
                };
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task BatchProvider_DuplicateRawJsonl_IngestionCreatesZeroReadyCandidates()
    {
        var (batch, item1, item2) = SetupBatchWithTwoItems();
        var rawB64 = Convert.ToBase64String(CreateTestPng(816, 816, Color.Red));

        // Duplicate custom_id pointing to item1.CustomId
        var duplicateJsonl =
            $"{{\"id\":\"req_1\",\"custom_id\":\"{item1.CustomId}\",\"response\":{{\"status_code\":200,\"request_id\":\"req-1\",\"body\":{{\"data\":[{{\"b64_json\":\"{rawB64}\"}}]}}}},\"error\":null}}\n" +
            $"{{\"id\":\"req_2\",\"custom_id\":\"{item1.CustomId}\",\"response\":{{\"status_code\":200,\"request_id\":\"req-2\",\"body\":{{\"data\":[{{\"b64_json\":\"{rawB64}\"}}]}}}},\"error\":null}}";

        var handler = new DuplicateJsonlHttpMessageHandler(duplicateJsonl);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var apiClient = new OpenAiApiClient(httpClient);
        var provider = new OpenAiImageGenerationProvider(apiClient);

        var status = new BatchStatusResult(
            ProviderBatchId: batch.ProviderBatchId!,
            Status: "completed",
            OutputFileId: "file-dup-output",
            ErrorFileId: null,
            TotalCount: 2,
            CompletedCount: 2,
            FailedCount: 0);

        // Act & Assert: DownloadBatchResultsAsync throws InvalidDataException due to duplicate custom_id in raw JSONL
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.DownloadBatchResultsAsync(status, "test-api-key"));

        Assert.Contains("duplicate custom_id", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Prove zero candidates were staged on disk
        var stagingDir = _stagingService.BaseStagingPath;
        if (Directory.Exists(stagingDir))
        {
            var stagedFiles = Directory.GetFiles(stagingDir, "*.*", SearchOption.AllDirectories);
            Assert.Empty(stagedFiles);
        }

        // Prove zero jobs are Ready, and jobs/batch state remained unchanged
        var currentItem1 = _jobStore.GetItem(item1.ManifestFingerprint, item1.RequestKey)!;
        var currentItem2 = _jobStore.GetItem(item2.ManifestFingerprint, item2.RequestKey)!;

        Assert.Equal(GenerationItemStatus.BatchSubmitted, currentItem1.Status);
        Assert.Equal(GenerationItemStatus.BatchSubmitted, currentItem2.Status);
        Assert.Null(currentItem1.StagedOutputPath);
        Assert.Null(currentItem2.StagedOutputPath);
        Assert.Null(currentItem1.ProviderRawPath);
        Assert.Null(currentItem2.ProviderRawPath);

        var currentBatch = _jobStore.GetBatch(batch.LocalBatchId)!;
        Assert.Equal(batch.Status, currentBatch.Status);
        Assert.Null(currentBatch.CompletedAtUtc);
    }
}

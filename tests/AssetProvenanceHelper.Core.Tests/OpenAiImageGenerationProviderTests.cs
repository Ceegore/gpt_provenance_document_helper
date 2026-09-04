using System.Net;
using System.Text;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class OpenAiImageGenerationProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class MinimalCustomProvider : IImageGenerationProvider
    {
        public string ProviderId => "custom";
        public ProviderCapabilities GetCapabilities(string model) => new(true, false, false, false, false);
        public Task<ImageGenerationCandidate> GenerateAsync(ImageGenerationSpec spec, string apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImageGenerationCandidate("cand-1", "custom-1", new byte[1], "sha", 1, 1));

#pragma warning disable CS0618
        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
#pragma warning restore CS0618

        public Task<BatchStatusResult> GetBatchStatusAsync(string providerBatchId, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BatchDownloadResult> DownloadBatchResultsAsync(BatchStatusResult status, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public async Task DefaultInterfaceMethods_ThrowNotImplementedException()
    {
        IImageGenerationProvider provider = new MinimalCustomProvider();

        await Assert.ThrowsAsync<NotImplementedException>(() =>
            provider.UploadBatchInputFileAsync([], "key"));

        await Assert.ThrowsAsync<NotImplementedException>(() =>
            provider.CreateBatchAsync("file-1", "key"));
    }

    [Fact]
    public void GetCapabilities_ReturnsExpectedSettings()
    {
        var provider = new OpenAiImageGenerationProvider();
        var caps = provider.GetCapabilities("gpt-image-2");

        Assert.True(caps.SupportsTextToImage);
        Assert.True(caps.SupportsBatch);
        Assert.False(caps.SupportsTransparentBackground);
        Assert.True(caps.SupportsReferenceImages);
        Assert.True(caps.SupportsArbitrarySize);
    }

    [Fact]
    public async Task GenerateAsync_WithAlphaRequired_ThrowsInvalidOperationException()
    {
        var provider = new OpenAiImageGenerationProvider();
        var spec = new ImageGenerationSpec("fp", "rk", "a", "a.png", "p", 512, 512, AlphaRequirement.Required, "OpenAI", "gpt-image-2", "medium", 816, 816, "c1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GenerateAsync(spec, "sk-test"));
    }

    [Fact]
    public async Task UploadBatchInputFileAsync_And_CreateBatchAsync_ExecuteSuccessfully()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/files")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"file-upload-123\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.RequestUri?.AbsolutePath == "/v1/batches")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"batch-remote-456\",\"status\":\"validating\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(http);
        var provider = new OpenAiImageGenerationProvider(client);

        var spec = new ImageGenerationSpec("fp", "rk", "a", "a.png", "p", 512, 512, AlphaRequirement.NotRequired, "OpenAI", "gpt-image-2", "medium", 816, 816, "c1");

        var fileId = await provider.UploadBatchInputFileAsync([spec], "sk-test");
        Assert.Equal("file-upload-123", fileId);

        var batchResult = await provider.CreateBatchAsync(fileId, "sk-test");
        Assert.Equal("batch-remote-456", batchResult.ProviderBatchId);
        Assert.Equal("file-upload-123", batchResult.ProviderInputFileId);
    }

    [Fact]
    public async Task GetBatchStatusAsync_ParsesStatusAndTimestampsAndCounts()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/batches/batch-123")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "id": "batch-123",
                        "status": "completed",
                        "output_file_id": "file-out-1",
                        "error_file_id": "file-err-1",
                        "completed_at": 1700000000,
                        "expires_at": 1700086400,
                        "request_counts": {
                            "total": 5,
                            "completed": 4,
                            "failed": 1
                        }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(http);
        var provider = new OpenAiImageGenerationProvider(client);

        var status = await provider.GetBatchStatusAsync("batch-123", "sk-test");

        Assert.Equal("batch-123", status.ProviderBatchId);
        Assert.Equal("completed", status.Status);
        Assert.Equal("file-out-1", status.OutputFileId);
        Assert.Equal("file-err-1", status.ErrorFileId);
        Assert.Equal(5, status.TotalCount);
        Assert.Equal(4, status.CompletedCount);
        Assert.Equal(1, status.FailedCount);
        Assert.NotNull(status.CompletedAtUtc);
        Assert.NotNull(status.ExpiresAtUtc);
    }

    [Fact]
    public async Task GetBatchStatusAsync_HandlesNullOptionalFields()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/batches/batch-minimal")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "id": "batch-minimal",
                        "status": "in_progress"
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(http);
        var provider = new OpenAiImageGenerationProvider(client);

        var status = await provider.GetBatchStatusAsync("batch-minimal", "sk-test");

        Assert.Equal("batch-minimal", status.ProviderBatchId);
        Assert.Equal("in_progress", status.Status);
        Assert.Null(status.OutputFileId);
        Assert.Null(status.ErrorFileId);
        Assert.Equal(0, status.TotalCount);
        Assert.Equal(0, status.CompletedCount);
        Assert.Equal(0, status.FailedCount);
        Assert.Null(status.CompletedAtUtc);
        Assert.Null(status.ExpiresAtUtc);
    }

    [Fact]
    public async Task DownloadBatchResultsAsync_DownloadsOutputAndErrorFiles()
    {
        var pngBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var b64 = Convert.ToBase64String(pngBytes);

        var handler = new StubHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/v1/files/file-out/content")
            {
                var line = $"{{\"id\":\"batch_req_1\",\"custom_id\":\"aph-k1\",\"response\":{{\"status_code\":200,\"body\":{{\"data\":[{{\"b64_json\":\"{b64}\"}}]}}}}}}\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(line, Encoding.UTF8, "application/json")
                };
            }
            if (req.RequestUri?.AbsolutePath == "/v1/files/file-err/content")
            {
                var line = "{\"id\":\"batch_req_2\",\"custom_id\":\"aph-k2\",\"error\":{\"message\":\"quota exceeded\",\"code\":\"insufficient_quota\"}}\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(line, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(http);
        var provider = new OpenAiImageGenerationProvider(client);

        var completedBatch = new BatchStatusResult(
            ProviderBatchId: "batch-1",
            Status: "completed",
            OutputFileId: "file-out",
            ErrorFileId: "file-err",
            TotalCount: 2,
            CompletedCount: 1,
            FailedCount: 1,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: null);

        var downloadResult = await provider.DownloadBatchResultsAsync(completedBatch, "sk-test");

        Assert.Equal("batch-1", downloadResult.ProviderBatchId);
        Assert.Equal(2, downloadResult.Items.Count);
        Assert.Contains(downloadResult.Items, i => i.CustomId == "aph-k1" && i.IsSuccess);
        Assert.Contains(downloadResult.Items, i => i.CustomId == "aph-k2" && !i.IsSuccess);
    }
}

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

        public Task<BatchSubmissionResult> SubmitBatchAsync(IReadOnlyList<ImageGenerationSpec> specs, string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

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
}

using System.Net;
using System.Text;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class OpenAiApiClientTests
{
    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }

    [Fact]
    public async Task GenerateImageAsync_Success_ReturnsValidResponse()
    {
        var rawB64 = Convert.ToBase64String(new byte[] { 10, 20, 30 });
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("Bearer test-key", req.Headers.Authorization?.ToString());
            Assert.Equal("/v1/images/generations", req.RequestUri?.AbsolutePath);

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"created":123456,"data":[{"b64_json":"{{rawB64}}"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var request = new OpenAiImageGenerationRequest(
            Model: "gpt-image-2",
            Prompt: "test",
            Size: "816x816",
            Quality: "medium",
            N: 1,
            OutputFormat: "png",
            Background: "opaque");

        var result = await client.GenerateImageAsync(request, "test-key");

        Assert.NotNull(result.Data);
        Assert.Single(result.Data);
        Assert.Equal(rawB64, result.Data[0].B64Json);
    }

    [Fact]
    public async Task GenerateImageAsync_400Error_ThrowsOpenAiApiExceptionWithoutRetrying()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            callCount++;
            var resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":{"message":"Prompt too long","type":"invalid_request_error","code":"invalid_prompt"}}""",
                    Encoding.UTF8,
                    "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var request = new OpenAiImageGenerationRequest(
            Model: "gpt-image-2",
            Prompt: "too long",
            Size: "816x816",
            Quality: "medium",
            N: 1,
            OutputFormat: "png",
            Background: "opaque");

        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() => client.GenerateImageAsync(request, "test-key"));
        Assert.Equal(1, callCount);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("invalid_prompt", ex.ErrorCode);
        Assert.Contains("Prompt too long", ex.Message);
    }

    [Fact]
    public async Task UploadBatchFileAsync_Success_ReturnsFileId()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("/v1/files", req.RequestUri?.AbsolutePath);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"file-123","object":"file","bytes":100,"created_at":1000,"filename":"batch.jsonl","purpose":"batch"}""",
                    Encoding.UTF8,
                    "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var file = await client.UploadBatchFileAsync(new byte[] { 1, 2, 3 }, "batch.jsonl", "test-key");

        Assert.Equal("file-123", file.Id);
    }

    [Fact]
    public async Task CreateBatchAsync_Success_ReturnsBatchId()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("/v1/batches", req.RequestUri?.AbsolutePath);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"batch-abc","status":"validating","endpoint":"/v1/images/generations"}""",
                    Encoding.UTF8,
                    "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var batch = await client.CreateBatchAsync("file-123", "test-key");

        Assert.Equal("batch-abc", batch.Id);
        Assert.Equal("validating", batch.Status);
    }

    [Fact]
    public async Task GenerateImageAsync_HtmlGatewayError_ExtractsTitle()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(
                    "<html><head><title>502 Bad Gateway</title></head><body><center>cloudflare</center></body></html>",
                    Encoding.UTF8,
                    "text/html")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient, new RetryPolicy(1));

        var request = new OpenAiImageGenerationRequest(
            Model: "gpt-image-2",
            Prompt: "test",
            Size: "816x816",
            Quality: "medium",
            N: 1,
            OutputFormat: "png",
            Background: "opaque");

        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() => client.GenerateImageAsync(request, "test-key"));
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Equal("html_gateway_error", ex.ErrorType);
        Assert.Contains("502 Bad Gateway", ex.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_QueriesModelEndpoint_ReturnsSuccess()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("/v1/models/gpt-image-2", req.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"gpt-image-2\"}", Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var success = await client.TestConnectionAsync("test-key", "gpt-image-2");
        Assert.True(success);
    }

    [Fact]
    public async Task DownloadFileContentAsync_Success_ReturnsStringContent()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("/v1/files/f1/content", req.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("file payload line 1\nfile payload line 2")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var content = await client.GetFileContentAsync("f1", "test-key");
        Assert.Equal("file payload line 1\nfile payload line 2", content);
    }

    [Fact]
    public async Task GetBatchAsync_Success_ReturnsBatch()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.AbsolutePath == "/v1/batches/b1")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"b1\",\"status\":\"in_progress\"}", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var retrieved = await client.GetBatchAsync("b1", "test-key");
        Assert.Equal("b1", retrieved.Id);
        Assert.Equal("in_progress", retrieved.Status);
    }
}

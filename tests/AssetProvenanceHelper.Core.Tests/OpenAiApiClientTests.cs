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

    [Fact]
    public async Task UploadBatchFileAsync_Error_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Invalid file format\",\"type\":\"invalid_request_error\",\"code\":\"invalid_file\"}}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.UploadBatchFileAsync(new byte[] { 1, 2, 3 }, "batch.jsonl", "key"));
        Assert.Equal("invalid_file", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateBatchAsync_Error_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"File not found\",\"type\":\"invalid_request_error\",\"code\":\"file_not_found\"}}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.CreateBatchAsync("file-bad", "key"));
        Assert.Equal("file_not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task GetFileContentAsync_Error_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":{\"message\":\"File missing\",\"type\":\"invalid_request_error\",\"code\":\"file_missing\"}}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GetFileContentAsync("file-404", "key"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task GetBatchAsync_Error_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Batch not found\",\"type\":\"invalid_request_error\",\"code\":\"batch_not_found\"}}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GetBatchAsync("b-404", "key"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }



    [Fact]
    public async Task GenerateImageAsync_EmptyDataResponse_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient, new RetryPolicy(1));

        var request = new OpenAiImageGenerationRequest("gpt-image-2", "prompt", "816x816", "medium", 1, "png", "opaque");
        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() => client.GenerateImageAsync(request, "key"));
        Assert.Equal("empty_response", ex.ErrorCode);
    }

    [Fact]
    public async Task GenerateImageAsync_RetriesOn503_ThenSucceeds()
    {
        var attempts = 0;
        var rawB64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var handler = new FakeHttpMessageHandler(req =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"Server busy\"}}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"created":123,"data":[{"b64_json":"{{rawB64}}"}]}""", Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient, new RetryPolicy(2));

        var request = new OpenAiImageGenerationRequest("gpt-image-2", "prompt", "816x816", "medium", 1, "png", "opaque");
        var result = await client.GenerateImageAsync(request, "key");
        Assert.Equal(2, attempts);
        Assert.NotNull(result.Data);
        Assert.Equal(rawB64, result.Data[0].B64Json);
    }

    [Fact]
    public async Task ArgumentValidation_ThrowsOnNullOrWhitespace()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var request = new OpenAiImageGenerationRequest("gpt-image-2", "prompt", "816x816", "medium", 1, "png", "opaque");

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GenerateImageAsync(null!, "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateImageAsync(request, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateImageAsync(request, "   "));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.UploadBatchFileAsync(null!, "batch.jsonl", "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UploadBatchFileAsync([1], "", "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UploadBatchFileAsync([1], "file", ""));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateBatchAsync("", "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateBatchAsync("file", ""));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetBatchAsync("", "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetBatchAsync("b", ""));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFileContentAsync("", "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFileContentAsync("f", ""));

        await Assert.ThrowsAsync<ArgumentException>(() => client.TestConnectionAsync("", "gpt-image-2"));
    }

    [Fact]
    public void Dispose_OwnsClient_DisposesInternalClient()
    {
        var client = new OpenAiApiClient();
        client.Dispose();
        client.Dispose(); // Multiple dispose is safe
    }

    [Fact]
    public async Task GenerateImageAsync_HttpRequestException_RetriesAndSucceeds()
    {
        var attempts = 0;
        var rawB64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var handler = new FakeHttpMessageHandler(req =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("Network glitch");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"created":123,"data":[{"b64_json":"{{rawB64}}"}]}""", Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient, new RetryPolicy(2));

        var req = new OpenAiImageGenerationRequest("gpt-image-2", "prompt", "816x816", "medium", 1, "png", "opaque");
        var res = await client.GenerateImageAsync(req, "key");
        Assert.Equal(2, attempts);
        Assert.NotNull(res.Data);
    }

    [Fact]
    public async Task GenerateImageAsync_ExceededMaxRetries_ThrowsHttpRequestException()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            throw new HttpRequestException("Persistent network drop");
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient, new RetryPolicy(2));

        var req = new OpenAiImageGenerationRequest("gpt-image-2", "prompt", "816x816", "medium", 1, "png", "opaque");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateImageAsync(req, "key"));
        Assert.Equal("Persistent network drop", ex.Message);
    }

    [Fact]
    public async Task GenerateImageAsync_MissingDataField_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient, new RetryPolicy(1));

        var req = new OpenAiImageGenerationRequest("gpt-image-2", "prompt", "816x816", "medium", 1, "png", "opaque");
        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() => client.GenerateImageAsync(req, "key"));
        Assert.Equal("empty_response", ex.ErrorCode);
    }

    [Fact]
    public async Task GenerateImageAsync_CapturesRequestIdFromHeader()
    {
        var rawB64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var handler = new FakeHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"created":123,"data":[{"b64_json":"{{rawB64}}"}]}""", Encoding.UTF8, "application/json")
            };
            resp.Headers.Add("x-request-id", "req-unique-987");
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient, new RetryPolicy(1));

        var req = new OpenAiImageGenerationRequest("gpt-image-2", "prompt", "816x816", "medium", 1, "png", "opaque");
        var res = await client.GenerateImageAsync(req, "key");
        Assert.Equal("req-unique-987", res.RequestId);
    }

    [Fact]
    public async Task CreateBatchAsync_SendsExpectedPayload()
    {
        var handler = new FakeHttpMessageHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("/v1/images/generations", body);
            Assert.Contains("24h", body);
            Assert.Contains("file-inp-456", body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"batch-456\",\"status\":\"validating\",\"endpoint\":\"/v1/images/generations\"}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var batch = await client.CreateBatchAsync("file-inp-456", "key");
        Assert.Equal("batch-456", batch.Id);
        Assert.Equal("validating", batch.Status);
    }

    [Fact]
    public async Task UploadBatchFileAsync_SendsMultipartWithPurpose()
    {
        var handler = new FakeHttpMessageHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("purpose", body);
            Assert.Contains("input_requests.jsonl", body);
            Assert.Contains("\nbatch\n", body.Replace("\r\n", "\n"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"file-up-123\",\"bytes\":10,\"purpose\":\"batch\"}", Encoding.UTF8, "application/json")
            };
        });


        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var file = await client.UploadBatchFileAsync(new byte[] { 1, 2, 3 }, "input_requests.jsonl", "key");
        Assert.Equal("file-up-123", file.Id);
    }


    [Fact]
    public async Task TestConnectionAsync_ErrorStatus_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Invalid key\",\"type\":\"invalid_request_error\"}}", Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() => client.TestConnectionAsync("bad-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Invalid key", ex.Message);
    }

    [Fact]
    public async Task UploadBatchFileAsync_NullJson_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        }));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);
        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() => client.UploadBatchFileAsync(new byte[] { 1 }, "file.jsonl", "key"));
        Assert.Equal("deserialization_failed", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateBatchAsync_NullJson_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        }));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);
        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() => client.CreateBatchAsync("file-1", "key"));
        Assert.Equal("deserialization_failed", ex.ErrorCode);
    }

    [Fact]
    public async Task GetBatchAsync_NullJson_ThrowsOpenAiApiException()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        }));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);
        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() => client.GetBatchAsync("b-1", "key"));
        Assert.Equal("deserialization_failed", ex.ErrorCode);
    }

    [Fact]
    public void Constructor_CustomHttpClient_PreservesProperties()
    {
        using var customHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(42) };
        using var client = new OpenAiApiClient(customHttp);
        Assert.Equal(new Uri("https://api.openai.com/v1/"), customHttp.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(42), customHttp.Timeout);

        // Disposing OpenAiApiClient should not dispose external HttpClient
        client.Dispose();
        Assert.NotNull(customHttp.BaseAddress);
    }

    [Fact]
    public async Task UploadBatchFileAsync_NullOrEmptyArguments_ThrowsArgumentException()
    {
        using var client = new OpenAiApiClient(new HttpClient { BaseAddress = new Uri("https://api.openai.com/v1/") });
        await Assert.ThrowsAsync<ArgumentNullException>("jsonlBytes", () => client.UploadBatchFileAsync(null!, "file.jsonl", "key"));
        await Assert.ThrowsAsync<ArgumentException>("fileName", () => client.UploadBatchFileAsync(new byte[1], "", "key"));
        await Assert.ThrowsAsync<ArgumentException>("fileName", () => client.UploadBatchFileAsync(new byte[1], "   ", "key"));
    }

    [Fact]
    public async Task Dispose_OwnedHttpClient_DisposedCorrectly()
    {
        var client = new OpenAiApiClient();
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.TestConnectionAsync("key"));
    }
}

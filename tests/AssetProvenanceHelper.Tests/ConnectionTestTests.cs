using System.Net;
using System.Text;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Tests;

public sealed class ConnectionTestTests
{
    private sealed class TrackingHandlerStub : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> CapturedRequests = [];
        public HttpStatusCode StatusCodeToReturn = HttpStatusCode.OK;
        public string ResponseJson = "{\"id\":\"gpt-image-2\",\"object\":\"model\"}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            var res = new HttpResponseMessage(StatusCodeToReturn)
            {
                Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        }
    }

    [Fact]
    public async Task ConnectionTest_QueriesSpecificModelEndpoint()
    {
        var handler = new TrackingHandlerStub
        {
            StatusCodeToReturn = HttpStatusCode.OK,
            ResponseJson = "{\"id\":\"gpt-image-2\",\"object\":\"model\"}"
        };

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var result = await client.TestConnectionAsync("sk-test-key-123", "gpt-image-2");

        Assert.True(result);
        var request = Assert.Single(handler.CapturedRequests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/models/gpt-image-2", request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test-key-123", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ConnectionTest_OnModelNotFound_ReportsModelUnavailable()
    {
        var handler = new TrackingHandlerStub
        {
            StatusCodeToReturn = HttpStatusCode.NotFound,
            ResponseJson = "{\"error\":{\"message\":\"The model 'gpt-image-2' does not exist\",\"type\":\"invalid_request_error\",\"code\":\"model_not_found\"}}"
        };

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.TestConnectionAsync("sk-test-key-123", "gpt-image-2"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("model_not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task ConnectionTest_NeverCallsImageGenerationEndpoint()
    {
        var handler = new TrackingHandlerStub
        {
            StatusCodeToReturn = HttpStatusCode.OK,
            ResponseJson = "{\"id\":\"gpt-image-2\",\"object\":\"model\"}"
        };

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client = new OpenAiApiClient(httpClient);

        await client.TestConnectionAsync("sk-test-key-123", "gpt-image-2");

        Assert.NotEmpty(handler.CapturedRequests);
        foreach (var req in handler.CapturedRequests)
        {
            Assert.NotEqual(HttpMethod.Post, req.Method);
            Assert.DoesNotContain("images/generations", req.RequestUri?.ToString() ?? string.Empty);
        }
    }
}

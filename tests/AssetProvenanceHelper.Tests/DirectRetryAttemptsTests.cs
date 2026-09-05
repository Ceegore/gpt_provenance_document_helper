using System.Net;
using System.Text;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Tests;

public sealed class DirectRetryAttemptsTests
{
    private sealed class CountingHandlerStub : HttpMessageHandler
    {
        public int RequestCount;
        public HttpStatusCode StatusCodeToReturn = HttpStatusCode.ServiceUnavailable;
        public string? SuccessJson;
        public int SuccessOnAttempt = int.MaxValue;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount >= SuccessOnAttempt && SuccessJson != null)
            {
                var okRes = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SuccessJson, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(okRes);
            }

            var errRes = new HttpResponseMessage(StatusCodeToReturn)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Server unavailable\"}}", Encoding.UTF8, "application/json")
            };
            errRes.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return Task.FromResult(errRes);
        }
    }

    [Fact]
    public async Task RetryPolicy_UsesConfiguredMaxAttempts()
    {
        var sampleBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var successJson = $$"""
        {
            "created": 1700000000,
            "data": [
                {
                    "b64_json": "{{sampleBase64}}"
                }
            ]
        }
        """;

        var handler = new CountingHandlerStub
        {
            SuccessJson = successJson,
            SuccessOnAttempt = 4 // Succeeds on attempt 4
        };

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var policy = new RetryPolicy(maxAttempts: 4);

        Assert.Equal(4, policy.MaxAttempts);

        var client = new OpenAiApiClient(httpClient, policy);
        var request = new OpenAiImageGenerationRequest(
            Model: "gpt-image-2",
            Prompt: "test",
            Size: "1024x1024",
            Quality: "medium",
            N: 1,
            OutputFormat: "png",
            Background: "opaque");

        var response = await client.GenerateImageAsync(request, "sk-test", retryPolicy: policy);

        Assert.NotNull(response);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task DirectGeneration_RespectsDirectRetryAttemptsSetting()
    {
        var sampleBase64 = Convert.ToBase64String(new byte[] { 4, 5, 6 });
        var successJson = $$"""
        {
            "created": 1700000000,
            "data": [
                {
                    "b64_json": "{{sampleBase64}}"
                }
            ]
        }
        """;

        // Scenario 1: RetryAttempts = 3 -> total attempts = 3
        var handler1 = new CountingHandlerStub
        {
            SuccessJson = successJson,
            SuccessOnAttempt = 999 // Always fails with 503
        };

        using var httpClient1 = new HttpClient(handler1) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client1 = new OpenAiApiClient(httpClient1);
        var provider1 = new OpenAiImageGenerationProvider(client1);

        var specWith3Attempts = new ImageGenerationSpec(
            ManifestFingerprint: "fp",
            RequestKey: "k1",
            AssetName: "asset",
            FileName: "asset.png",
            Prompt: "test",
            TargetWidth: 512,
            TargetHeight: 512,
            AlphaRequirement: AlphaRequirement.NotRequired,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "custom-1",
            RetryAttempts: 3); // Max direct API attempts = 3

        await Assert.ThrowsAnyAsync<Exception>(() => provider1.GenerateAsync(specWith3Attempts, "sk-test"));
        Assert.Equal(3, handler1.RequestCount);

        // Scenario 2: RetryAttempts = 1 -> total attempts = 1
        var handler2 = new CountingHandlerStub
        {
            SuccessJson = successJson,
            SuccessOnAttempt = 999 // Always fails with 503
        };

        using var httpClient2 = new HttpClient(handler2) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var client2 = new OpenAiApiClient(httpClient2);
        var provider2 = new OpenAiImageGenerationProvider(client2);

        var specWith1Attempt = new ImageGenerationSpec(
            ManifestFingerprint: "fp",
            RequestKey: "k1",
            AssetName: "asset",
            FileName: "asset.png",
            Prompt: "test",
            TargetWidth: 512,
            TargetHeight: 512,
            AlphaRequirement: AlphaRequirement.NotRequired,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "custom-1",
            RetryAttempts: 1); // Max direct API attempts = 1

        await Assert.ThrowsAnyAsync<Exception>(() => provider2.GenerateAsync(specWith1Attempt, "sk-test"));
        Assert.Equal(1, handler2.RequestCount);
    }
}

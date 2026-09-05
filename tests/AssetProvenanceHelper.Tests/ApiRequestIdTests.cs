using System.Net;
using System.Text;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiRequestIdTests
{
    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task DirectGeneration_ExtractsXRequestId()
    {
        var expectedRequestId = "req_openai_abc123xyz";
        var sampleBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var responseJson = $$"""
        {
            "created": 1700000000,
            "data": [
                {
                    "b64_json": "{{sampleBase64}}"
                }
            ]
        }
        """;

        var handler = new DelegatingHandlerStub(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
            res.Headers.Add("x-request-id", expectedRequestId);
            return res;
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var apiClient = new OpenAiApiClient(httpClient);
        var provider = new OpenAiImageGenerationProvider(apiClient);

        var spec = new ImageGenerationSpec(
            ManifestFingerprint: "fp",
            RequestKey: "k1",
            AssetName: "asset",
            FileName: "asset.png",
            Prompt: "A cute cat",
            TargetWidth: 512,
            TargetHeight: 512,
            AlphaRequirement: AlphaRequirement.NotRequired,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "custom-k1");

        var candidate = await provider.GenerateAsync(spec, "sk-test-key");

        Assert.NotNull(candidate);
        Assert.Equal(expectedRequestId, candidate.ProviderRequestId);
    }

    [Fact]
    public void BatchResult_ExtractsXRequestId()
    {
        var expectedRequestId = "req_batch_line_999";
        var sampleBase64 = Convert.ToBase64String(new byte[] { 5, 6, 7, 8 });
        var outputJsonl = "{\"id\":\"batch_req_1\",\"custom_id\":\"aph-custom-k1\",\"response\":{\"status_code\":200,\"request_id\":\"" +
            expectedRequestId +
            "\",\"body\":{\"created\":1700000000,\"data\":[{\"b64_json\":\"" +
            sampleBase64 +
            "\"}]}},\"error\":null}";

        var items = OpenAiBatchResultParser.ParseResults(outputJsonl, null);

        var item = Assert.Single(items);
        Assert.True(item.IsSuccess);
        Assert.Equal(expectedRequestId, item.ProviderRequestId);
    }

    [Fact]
    public void ProvenanceDoc_ContainsXRequestId_WhenPresent()
    {
        const string templateContent = """
        # Provenance
        Provider: <<<PROVIDER>>>
        Date: <<<DATE>>>
        File: <<<FILENAME>>>
        Asset: <<<ASSET_NAME>>>
        Project: <<<PROJECT>>>
        Role: <<<ROLE>>>
        Workflow: <<<WORKFLOW>>>
        Ref: <<<REFERENCE_FILENAME>>>
        Prompt: <<<PROMPT>>>
        ReqId: <<<API_PROVIDER_REQUEST_ID>>>
        """;

        var snapshot = new ProviderTemplateSnapshot
        {
            FileName = "OpenAI API.md",
            DisplayName = "OpenAI API",
            Content = templateContent,
            ContentSha256 = ProviderTemplateRules.ComputeContentSha256(templateContent)
        };

        var context = new ProviderRenderContext
        {
            Provider = "OpenAI API",
            Date = "2026-09-03",
            Filename = "hero.png",
            AssetName = "hero",
            Project = "Game",
            Role = "Background",
            Workflow = "Direct",
            ReferenceFilename = "none",
            Prompt = "forest",
            ApiProviderRequestId = "req_provenance_live_555"
        };

        var rendered = ProviderTemplateRenderer.Render(snapshot, context);

        Assert.Contains("ReqId: req_provenance_live_555", rendered);
    }

    [Fact]
    public void ProvenanceDoc_OmitsXRequestId_WhenMissing()
    {
        const string templateContent = """
        # Provenance
        Provider: <<<PROVIDER>>>
        Date: <<<DATE>>>
        File: <<<FILENAME>>>
        Asset: <<<ASSET_NAME>>>
        Project: <<<PROJECT>>>
        Role: <<<ROLE>>>
        Workflow: <<<WORKFLOW>>>
        Ref: <<<REFERENCE_FILENAME>>>
        Prompt: <<<PROMPT>>>
        ReqId: <<<API_PROVIDER_REQUEST_ID>>>
        """;

        var snapshot = new ProviderTemplateSnapshot
        {
            FileName = "OpenAI API.md",
            DisplayName = "OpenAI API",
            Content = templateContent,
            ContentSha256 = ProviderTemplateRules.ComputeContentSha256(templateContent)
        };

        var context = new ProviderRenderContext
        {
            Provider = "OpenAI API",
            Date = "2026-09-03",
            Filename = "hero.png",
            AssetName = "hero",
            Project = "Game",
            Role = "Background",
            Workflow = "Direct",
            ReferenceFilename = "none",
            Prompt = "forest",
            ApiProviderRequestId = string.Empty
        };

        var rendered = ProviderTemplateRenderer.Render(snapshot, context);

        Assert.Contains($"ReqId: {AppConstants.NotRecordedValue}", rendered);
        Assert.DoesNotContain("req_", rendered);
    }
}

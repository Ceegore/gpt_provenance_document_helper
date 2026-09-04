using System.Net;
using System.Text;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class GenerationCapabilityTests
{
    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ResponseFactory != null)
            {
                return Task.FromResult(ResponseFactory(request));
            }

            var rawB64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"created":123456,"data":[{"b64_json":"{{rawB64}}"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public void OpenAiProvider_Capabilities_ReportsTransparentBackgroundFalse()
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
    public async Task GenerateAsync_AlphaRequired_BlockedBeforeHttp()
    {
        var handler = new CountingHttpMessageHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = OpenAiApiClient.DefaultBaseUri };
        var client = new OpenAiApiClient(httpClient);
        var provider = new OpenAiImageGenerationProvider(client);

        var spec = new ImageGenerationSpec(
            ManifestFingerprint: "fp",
            RequestKey: "rk",
            AssetName: "transparent_sprite",
            FileName: "sprite.png",
            Prompt: "A transparent sprite",
            TargetWidth: 512,
            TargetHeight: 512,
            AlphaRequirement: AlphaRequirement.Required,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp-rk");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GenerateAsync(spec, "fake-key"));

        Assert.Contains("transparent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GenerateAsync_AlphaNotRequired_Allowed()
    {
        var handler = new CountingHttpMessageHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = OpenAiApiClient.DefaultBaseUri };
        var client = new OpenAiApiClient(httpClient);
        var provider = new OpenAiImageGenerationProvider(client);

        var spec = new ImageGenerationSpec(
            ManifestFingerprint: "fp",
            RequestKey: "rk",
            AssetName: "opaque_sprite",
            FileName: "sprite.png",
            Prompt: "An opaque sprite",
            TargetWidth: 512,
            TargetHeight: 512,
            AlphaRequirement: AlphaRequirement.NotRequired,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp-rk");

        var candidate = await provider.GenerateAsync(spec, "fake-key");

        Assert.NotNull(candidate);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GenerateAsync_AlphaUnknown_Allowed()
    {
        var handler = new CountingHttpMessageHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = OpenAiApiClient.DefaultBaseUri };
        var client = new OpenAiApiClient(httpClient);
        var provider = new OpenAiImageGenerationProvider(client);

        var spec = new ImageGenerationSpec(
            ManifestFingerprint: "fp",
            RequestKey: "rk",
            AssetName: "unknown_alpha_sprite",
            FileName: "sprite.png",
            Prompt: "Sprite with unknown alpha",
            TargetWidth: 512,
            TargetHeight: 512,
            AlphaRequirement: AlphaRequirement.Unknown,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp-rk");

        var candidate = await provider.GenerateAsync(spec, "fake-key");

        Assert.NotNull(candidate);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SubmitBatchAsync_AlphaRequired_BlockedBeforeHttp()
    {
        var handler = new CountingHttpMessageHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = OpenAiApiClient.DefaultBaseUri };
        var client = new OpenAiApiClient(httpClient);
        var provider = new OpenAiImageGenerationProvider(client);

        var spec = new ImageGenerationSpec(
            ManifestFingerprint: "fp",
            RequestKey: "rk",
            AssetName: "transparent_sprite",
            FileName: "sprite.png",
            Prompt: "A transparent sprite",
            TargetWidth: 512,
            TargetHeight: 512,
            AlphaRequirement: AlphaRequirement.Required,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-fp-rk");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.UploadBatchInputFileAsync(new[] { spec }, "fake-key"));

        Assert.Contains("transparent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }
}

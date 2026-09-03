using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class GenerationCapabilityTests
{
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
        var provider = new OpenAiImageGenerationProvider();

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
    }

    [Fact]
    public async Task SubmitBatchAsync_AlphaRequired_BlockedBeforeHttp()
    {
        var provider = new OpenAiImageGenerationProvider();

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
            provider.SubmitBatchAsync(new[] { spec }, "fake-key"));

        Assert.Contains("transparent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

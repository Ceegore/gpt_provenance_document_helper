using System.Text;
using System.Text.Json;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class OpenAiBatchJsonlBuilderTests
{
    [Fact]
    public void Build_ValidSpecs_ProducesValidJsonlLines()
    {
        var specs = new[]
        {
            new ImageGenerationSpec(
                ManifestFingerprint: "fp1234567890",
                RequestKey: "rk1234567890",
                AssetName: "hero",
                FileName: "hero.png",
                Prompt: "A hero with sword",
                TargetWidth: 512,
                TargetHeight: 512,
                AlphaRequirement: AlphaRequirement.NotRequired,
                ProviderId: "OpenAI",
                Model: "gpt-image-2",
                Quality: "medium",
                GenerationWidth: 816,
                GenerationHeight: 816,
                CustomId: "aph-fp1234567890-rk1234567890"),
            new ImageGenerationSpec(
                ManifestFingerprint: "fp1234567890",
                RequestKey: "rk0987654321",
                AssetName: "castle",
                FileName: "castle.png",
                Prompt: "A medieval castle",
                TargetWidth: 1920,
                TargetHeight: 1080,
                AlphaRequirement: AlphaRequirement.Unknown,
                ProviderId: "OpenAI",
                Model: "gpt-image-2",
                Quality: "medium",
                GenerationWidth: 1920,
                GenerationHeight: 1088,
                CustomId: "aph-fp1234567890-rk0987654321")
        };

        var bytes = OpenAiBatchJsonlBuilder.Build(specs);
        var jsonl = Encoding.UTF8.GetString(bytes);

        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        var doc1 = JsonDocument.Parse(lines[0]);
        Assert.Equal("aph-fp1234567890-rk1234567890", doc1.RootElement.GetProperty("custom_id").GetString());
        Assert.Equal("POST", doc1.RootElement.GetProperty("method").GetString());
        Assert.Equal("/v1/images/generations", doc1.RootElement.GetProperty("url").GetString());
        Assert.Equal("816x816", doc1.RootElement.GetProperty("body").GetProperty("size").GetString());
        Assert.Equal(1, doc1.RootElement.GetProperty("body").GetProperty("n").GetInt32());

        var doc2 = JsonDocument.Parse(lines[1]);
        Assert.Equal("aph-fp1234567890-rk0987654321", doc2.RootElement.GetProperty("custom_id").GetString());
        Assert.Equal("1920x1088", doc2.RootElement.GetProperty("body").GetProperty("size").GetString());
    }

    [Fact]
    public void Build_WithAlphaRequired_ThrowsInvalidOperationException()
    {
        var specs = new[]
        {
            new ImageGenerationSpec(
                ManifestFingerprint: "fp123",
                RequestKey: "rk123",
                AssetName: "ghost",
                FileName: "ghost.png",
                Prompt: "A transparent ghost",
                TargetWidth: 512,
                TargetHeight: 512,
                AlphaRequirement: AlphaRequirement.Required,
                ProviderId: "OpenAI",
                Model: "gpt-image-2",
                Quality: "medium",
                GenerationWidth: 816,
                GenerationHeight: 816,
                CustomId: "aph-fp123-rk123")
        };

        Assert.Throws<InvalidOperationException>(() => OpenAiBatchJsonlBuilder.Build(specs));
    }

    [Fact]
    public void Build_DuplicateCustomId_ThrowsArgumentException()
    {
        var spec1 = new ImageGenerationSpec(
            ManifestFingerprint: "fp123",
            RequestKey: "rk1",
            AssetName: "a",
            FileName: "a.png",
            Prompt: "p",
            TargetWidth: 512,
            TargetHeight: 512,
            AlphaRequirement: AlphaRequirement.NotRequired,
            ProviderId: "OpenAI",
            Model: "gpt-image-2",
            Quality: "medium",
            GenerationWidth: 816,
            GenerationHeight: 816,
            CustomId: "aph-dup-1");

        var spec2 = spec1 with { RequestKey = "rk2", AssetName = "b", FileName = "b.png" };

        Assert.Throws<ArgumentException>(() => OpenAiBatchJsonlBuilder.Build(new[] { spec1, spec2 }));
    }
}

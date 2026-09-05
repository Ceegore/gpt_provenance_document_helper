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
        Assert.Equal("png", doc1.RootElement.GetProperty("body").GetProperty("output_format").GetString());
        Assert.Equal("opaque", doc1.RootElement.GetProperty("body").GetProperty("background").GetString());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var doc2 = JsonDocument.Parse(lines[1]);
        Assert.Equal("aph-fp1234567890-rk0987654321", doc2.RootElement.GetProperty("custom_id").GetString());
        Assert.Equal("1920x1088", doc2.RootElement.GetProperty("body").GetProperty("size").GetString());
        Assert.Equal("png", doc2.RootElement.GetProperty("body").GetProperty("output_format").GetString());
        Assert.Equal("opaque", doc2.RootElement.GetProperty("body").GetProperty("background").GetString());
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
    public void Build_NullSpecs_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => OpenAiBatchJsonlBuilder.Build(null!));
    }

    [Fact]
    public void Build_EmptySpecs_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => OpenAiBatchJsonlBuilder.Build(Array.Empty<ImageGenerationSpec>()));
        Assert.Contains("Cannot build batch JSONL with zero specifications", ex.Message);
    }

    [Fact]
    public void Build_EmptyCustomId_ThrowsArgumentException()
    {
        var spec = new ImageGenerationSpec("fp", "rk", "a", "a.png", "p", 512, 512, AlphaRequirement.NotRequired, "OpenAI", "gpt-image-2", "medium", 816, 816, "");
        var ex = Assert.Throws<ArgumentException>(() => OpenAiBatchJsonlBuilder.Build(new[] { spec }));
        Assert.Contains("Every specification must have a valid CustomId", ex.Message);
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

        var ex = Assert.Throws<ArgumentException>(() => OpenAiBatchJsonlBuilder.Build(new[] { spec1, spec2 }));
        Assert.Contains("Duplicate CustomId detected", ex.Message);
    }

    [Fact]
    public void Build_MismatchedModel_ThrowsArgumentException()
    {
        var spec1 = new ImageGenerationSpec("fp", "rk1", "a", "a.png", "p", 512, 512, AlphaRequirement.NotRequired, "OpenAI", "gpt-image-2", "medium", 816, 816, "c1");
        var spec2 = new ImageGenerationSpec("fp", "rk2", "b", "b.png", "p", 512, 512, AlphaRequirement.NotRequired, "OpenAI", "other-model", "medium", 816, 816, "c2");

        var ex = Assert.Throws<ArgumentException>(() => OpenAiBatchJsonlBuilder.Build(new[] { spec1, spec2 }));
        Assert.Contains("All batch items must use the same model", ex.Message);
    }
}

#nullable enable
using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13RequestManifestTests
{
    private static string WriteManifest(
        string root,
        string json,
        string fileName = "manifest.json")
    {
        var path =
            Path.Combine(
                root,
                fileName);

        File.WriteAllText(
            path,
            json,
            new UTF8Encoding(false));

        return path;
    }

    private static AssetRequestManifest Load(
        TestWorkspace workspace,
        string json,
        string fileName = "manifest.json")
    {
        var path =
            WriteManifest(
                workspace.Root,
                json,
                fileName);

        return new AssetRequestManifestService(
                workspace.CreateValidationService())
            .Load(
                path,
                workspace.CreateSettings().AcceptedExtensions);
    }

    [Fact]
    public void OneValidAssetLoads()
    {
        using var workspace = new TestWorkspace();

        var manifest =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    {
                      "filename": "asset_ui_screen_settings.webp",
                      "resolution": "1920x1080",
                      "prompt": "Complete exact generation prompt here."
                    }
                  ]
                }
                """);

        Assert.Equal(1, manifest.Version);
        Assert.Single(manifest.Items);
        Assert.Equal("asset_ui_screen_settings", manifest.Items[0].AssetName);
        Assert.Equal("1920x1080", manifest.Items[0].Resolution);
        Assert.Equal(1920, manifest.Items[0].Width);
        Assert.Equal(1080, manifest.Items[0].Height);
        Assert.Equal(64, manifest.Items[0].RequestKey.Length);
    }

    [Fact]
    public void MultipleAssetsPreserveOrder()
    {
        using var workspace = new TestWorkspace();

        var manifest =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "a.png", "resolution": "10x10", "prompt": "p1" },
                    { "filename": "b.png", "resolution": "20x20", "prompt": "p2" },
                    { "filename": "c.png", "resolution": "30x30", "prompt": "p3" }
                  ]
                }
                """);

        Assert.Equal(
            new[] { "a", "b", "c" },
            manifest.Items
                .Select(item => item.AssetName)
                .ToArray());
    }

    [Fact]
    public void OneHundredFiftyAssetsLoad()
    {
        using var workspace = new TestWorkspace();

        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"manifestVersion\": 1,");
        builder.AppendLine("  \"assets\": [");
        for (var i = 0; i < 150; i++)
        {
            builder.AppendLine(
                $"    {{ \"filename\": \"asset_{i}.png\", \"resolution\": \"512x512\", \"prompt\": \"prompt {i}\" }}{(i < 149 ? "," : string.Empty)}");
        }
        builder.AppendLine("  ]");
        builder.AppendLine("}");

        var manifest =
            Load(
                workspace,
                builder.ToString());

        Assert.Equal(150, manifest.Items.Count);
    }

    [Fact]
    public void FiveThousandAssetsLoad()
    {
        using var workspace = new TestWorkspace();

        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"manifestVersion\": 1,");
        builder.AppendLine("  \"assets\": [");
        for (var i = 0; i < 5000; i++)
        {
            builder.AppendLine(
                $"    {{ \"filename\": \"asset_{i}.png\", \"resolution\": \"512x512\", \"prompt\": \"prompt {i}\" }}{(i < 4999 ? "," : string.Empty)}");
        }
        builder.AppendLine("  ]");
        builder.AppendLine("}");

        var manifest =
            Load(
                workspace,
                builder.ToString());

        Assert.Equal(5000, manifest.Items.Count);
    }

    [Fact]
    public void FiveThousandOneAssetsRejected()
    {
        using var workspace = new TestWorkspace();

        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"manifestVersion\": 1,");
        builder.AppendLine("  \"assets\": [");
        for (var i = 0; i < 5001; i++)
        {
            builder.AppendLine(
                $"    {{ \"filename\": \"asset_{i}.png\", \"resolution\": \"512x512\", \"prompt\": \"prompt {i}\" }}{(i < 5000 ? "," : string.Empty)}");
        }
        builder.AppendLine("  ]");
        builder.AppendLine("}");

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    builder.ToString()));
    }

    [Fact]
    public void MissingManifestVersionRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void VersionZeroRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 0,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void VersionThreeRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 3,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void EmptyAssetsRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": []
                    }
                    """));
    }

    [Fact]
    public void UnknownTopLevelFieldRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p" }
                      ],
                      "extra": true
                    }
                    """));
    }

    [Fact]
    public void UnknownAssetFieldRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p", "seed": 42 }
                      ]
                    }
                    """));
    }

    [Fact]
    public void MissingFilenameRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void MissingResolutionRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void MissingPromptRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void EmptyPromptRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "   " }
                      ]
                    }
                    """));
    }

    [Fact]
    public void UnsupportedExtensionRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.txt", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void PathContainingFilenameRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "assets/ui/a.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void DuplicateFilenameCaseInsensitiveRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p1" },
                        { "filename": "A.PNG", "resolution": "10x10", "prompt": "p2" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void ReservedDeviceAssetNameRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "CON.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Theory]
    [InlineData("1920x1080")]
    [InlineData("1920×1080")]
    [InlineData("1920 x 1080")]
    [InlineData("1920 × 1080")]
    [InlineData("  1920  x  1080  ")]
    public void ResolutionVariantsNormalize(string input)
    {
        using var workspace = new TestWorkspace();

        var manifest =
            Load(
                workspace,
                $$"""
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "a.png", "resolution": "{{input}}", "prompt": "p" }
                  ]
                }
                """);

        Assert.Equal("1920x1080", manifest.Items[0].Resolution);
    }

    [Fact]
    public void ZeroDimensionRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "0x1080", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void OversizedDimensionRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "100001x1080", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void InvalidResolutionRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "1920 by 1080", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void PromptLineEndingsPreservedInStoredPrompt()
    {
        using var workspace = new TestWorkspace();

        var manifest =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "a.png", "resolution": "10x10", "prompt": "line1\r\nline2\r\nline3" }
                  ]
                }
                """);

        Assert.Equal(
            "line1\r\nline2\r\nline3",
            manifest.Items[0].Prompt);
    }

    [Fact]
    public void CrlfAndLfProduceSameRequestKey()
    {
        var keyCrlf =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "10x10",
                "line1\r\nline2");

        var keyLf =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "10x10",
                "line1\nline2");

        Assert.Equal(keyCrlf, keyLf);
    }

    [Fact]
    public void SameSemanticRequestProducesSameKey()
    {
        var first =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "1920x1080",
                "prompt");

        var second =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "1920x1080",
                "prompt");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ChangedPromptChangesKey()
    {
        var first =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "1920x1080",
                "prompt");

        var second =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "1920x1080",
                "prompt2");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ChangedResolutionChangesKey()
    {
        var first =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "1920x1080",
                "prompt");

        var second =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "2560x1440",
                "prompt");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ChangedFilenameChangesKey()
    {
        var first =
            AssetRequestManifestService.ComputeRequestKey(
                "a.png",
                "1920x1080",
                "prompt");

        var second =
            AssetRequestManifestService.ComputeRequestKey(
                "b.png",
                "1920x1080",
                "prompt");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void FormattingDifferencesDoNotChangeFingerprint()
    {
        using var workspace = new TestWorkspace();

        var compact =
            Load(
                workspace,
                """
                {"manifestVersion":1,"assets":[{"filename":"a.png","resolution":"10x10","prompt":"p"}]}
                """);

        var pretty =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    {
                      "filename": "a.png",
                      "resolution": "10x10",
                      "prompt": "p"
                    }
                  ]
                }
                """,
                "manifest2.json");

        Assert.Equal(
            compact.ManifestFingerprint,
            pretty.ManifestFingerprint);
    }

    [Fact]
    public void ItemReorderDoesNotChangeFingerprint()
    {
        using var workspace = new TestWorkspace();

        var forward =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "a.png", "resolution": "10x10", "prompt": "p1" },
                    { "filename": "b.png", "resolution": "20x20", "prompt": "p2" }
                  ]
                }
                """);

        var reversed =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "b.png", "resolution": "20x20", "prompt": "p2" },
                    { "filename": "a.png", "resolution": "10x10", "prompt": "p1" }
                  ]
                }
                """,
                "manifest2.json");

        Assert.Equal(
            forward.ManifestFingerprint,
            reversed.ManifestFingerprint);
    }

    [Fact]
    public void ChangedSemanticItemChangesFingerprint()
    {
        using var workspace = new TestWorkspace();

        var first =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "a.png", "resolution": "10x10", "prompt": "p1" }
                  ]
                }
                """);

        var second =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "a.png", "resolution": "10x10", "prompt": "p2" }
                  ]
                }
                """,
                "manifest2.json");

        Assert.NotEqual(
            first.ManifestFingerprint,
            second.ManifestFingerprint);
    }

    [Fact]
    public void MissingFileThrowsFileNotFound()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<FileNotFoundException>(
            () =>
                new AssetRequestManifestService(
                        workspace.CreateValidationService())
                    .Load(
                        Path.Combine(
                            workspace.Root,
                            "missing.json"),
                        workspace.CreateSettings().AcceptedExtensions));
    }

    [Fact]
    public void TrailingCommaRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p", }
                      ]
                    }
                    """));
    }

    [Fact]
    public void CommentsRejected()
    {
        using var workspace = new TestWorkspace();

        Assert.Throws<InvalidDataException>(
            () =>
                Load(
                    workspace,
                    """
                    {
                      // comment
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """));
    }

    [Fact]
    public void AssetNameDerivesFromFilename()
    {
        using var workspace = new TestWorkspace();

        var manifest =
            Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "asset.version2.webp", "resolution": "10x10", "prompt": "p" }
                  ]
                }
                """);

        Assert.Equal("asset.version2", manifest.Items[0].AssetName);
    }

    [Fact]
    public void AnimationFrameTemplate_IsImportedAsWindowsSafeSequenceName()
    {
        using var workspace = new TestWorkspace();

        var manifest = Load(
            workspace,
            """
            {
              "manifestVersion": 1,
              "assets": [
                {
                  "filename": "anim_117_cine_checkpoint_{frame:03d}.png",
                  "resolution": "512x288",
                  "prompt": "Generate the complete ordered frame sequence."
                }
              ]
            }
            """);

        var item = Assert.Single(manifest.Items);
        Assert.Equal("anim_117_cine_checkpoint_frames.png", item.FileName);
        Assert.Equal("anim_117_cine_checkpoint_frames", item.AssetName);
        Assert.True(workspace.CreateValidationService().ValidateAssetName(
            item.AssetName,
            workspace.CreateSettings().AcceptedExtensions).IsValid);
    }

    [Fact]
    public void OtherInvalidFilenameCharacters_AreStillRejected()
    {
        using var workspace = new TestWorkspace();

        var exception = Assert.Throws<InvalidDataException>(
            () => Load(
                workspace,
                """
                {
                  "manifestVersion": 1,
                  "assets": [
                    { "filename": "not:an-animation.png", "resolution": "10x10", "prompt": "p" }
                  ]
                }
                """));

        Assert.Contains("invalid Windows filename characters", exception.Message);
    }
}

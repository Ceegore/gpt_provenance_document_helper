using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ApiManifestV2Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly ValidationService _validationService;
    private readonly AssetRequestManifestService _manifestService;
    private readonly string[] _acceptedExtensions = [".png", ".webp", ".jpg", ".jpeg"];

    public ApiManifestV2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aph_manifest_v2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _validationService = new ValidationService();
        _manifestService = new AssetRequestManifestService(_validationService);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore
        }
    }

    [Fact]
    public void Load_ManifestV1_ParsedSuccessfullyAndDefaultsToUnknownAlpha()
    {
        var path = Path.Combine(_tempDir, "manifest_v1.json");
        File.WriteAllText(path, """
        {
          "manifestVersion": 1,
          "assets": [
            {
              "filename": "backdrop.webp",
              "resolution": "1920x1080",
              "prompt": "Epic landscape"
            }
          ]
        }
        """);

        var manifest = _manifestService.Load(path, _acceptedExtensions);

        Assert.Equal(1, manifest.Version);
        Assert.Single(manifest.Items);
        var item = manifest.Items[0];
        Assert.Equal("backdrop.webp", item.FileName);
        Assert.Equal(AlphaRequirement.Unknown, item.Alpha);
        Assert.NotNull(item.RequestKey);
    }

    [Fact]
    public void Load_ManifestV2_ParsesAlphaRequirementsCorrectly()
    {
        var path = Path.Combine(_tempDir, "manifest_v2.json");
        File.WriteAllText(path, """
        {
          "manifestVersion": 2,
          "assets": [
            {
              "filename": "bg.webp",
              "resolution": "1920x1080",
              "alpha": "not_required",
              "prompt": "Epic landscape"
            },
            {
              "filename": "hero.png",
              "resolution": "512x512",
              "alpha": "required",
              "prompt": "Hero character sprite"
            },
            {
              "filename": "item.png",
              "resolution": "256x256",
              "alpha": "unknown",
              "prompt": "Magic potion"
            },
            {
              "filename": "icon.png",
              "resolution": "128x128",
              "prompt": "Spell icon without explicit alpha"
            }
          ]
        }
        """);

        var manifest = _manifestService.Load(path, _acceptedExtensions);

        Assert.Equal(2, manifest.Version);
        Assert.Equal(4, manifest.Items.Count);

        Assert.Equal(AlphaRequirement.NotRequired, manifest.Items[0].Alpha);
        Assert.Equal(AlphaRequirement.Required, manifest.Items[1].Alpha);
        Assert.Equal(AlphaRequirement.Unknown, manifest.Items[2].Alpha);
        Assert.Equal(AlphaRequirement.Unknown, manifest.Items[3].Alpha);
    }

    [Fact]
    public void Load_ManifestV2_InvalidAlpha_ThrowsInvalidDataException()
    {
        var path = Path.Combine(_tempDir, "manifest_v2_invalid.json");
        File.WriteAllText(path, """
        {
          "manifestVersion": 2,
          "assets": [
            {
              "filename": "bg.webp",
              "resolution": "1920x1080",
              "alpha": "invalid_alpha_value",
              "prompt": "Epic landscape"
            }
          ]
        }
        """);

        var ex = Assert.Throws<InvalidDataException>(() => _manifestService.Load(path, _acceptedExtensions));
        Assert.Contains("Unsupported alpha value", ex.Message);
    }

    [Fact]
    public void Load_ManifestV1_WithAlphaProperty_ThrowsInvalidDataException()
    {
        var path = Path.Combine(_tempDir, "manifest_v1_with_alpha.json");
        File.WriteAllText(path, """
        {
          "manifestVersion": 1,
          "assets": [
            {
              "filename": "bg.webp",
              "resolution": "1920x1080",
              "alpha": "required",
              "prompt": "Epic landscape"
            }
          ]
        }
        """);

        var ex = Assert.Throws<InvalidDataException>(() => _manifestService.Load(path, _acceptedExtensions));
        Assert.Contains("not supported in manifestVersion 1", ex.Message);
    }

    [Fact]
    public void ComputeRequestKeyV2_DifferentAlpha_YieldsDifferentKeys()
    {
        var key1 = AssetRequestManifestService.ComputeRequestKeyV2("test.png", "512x512", "prompt", AlphaRequirement.Required);
        var key2 = AssetRequestManifestService.ComputeRequestKeyV2("test.png", "512x512", "prompt", AlphaRequirement.NotRequired);
        var key3 = AssetRequestManifestService.ComputeRequestKeyV2("test.png", "512x512", "prompt", AlphaRequirement.Unknown);

        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, key3);
        Assert.NotEqual(key2, key3);
    }
}

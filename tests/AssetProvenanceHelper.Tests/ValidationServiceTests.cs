using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ValidationServiceTests
{
    [Fact]
    public void ValidSettings_Pass()
    {
        using var workspace =
            new TestWorkspace();

        var service =
            new ValidationService();

        var result =
            service.ValidateSettings(
                workspace.CreateSettings());

        Assert.True(
            result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("COM1")]
    [InlineData("COM1.test")]
    [InlineData("CONIN$")]
    [InlineData("folder.")]
    [InlineData("folder ")]
    public void InvalidAssetFolderNames_AreRejected(
        string name)
    {
        var service =
            new ValidationService();

        var result =
            service.ValidateAssetFolderName(
                name);

        Assert.False(
            result.IsValid);
    }

    [Theory]
    [InlineData("final4_optimized")]
    [InlineData("location_clocktower_02")]
    [InlineData("asset.v2")]
    public void ValidAssetFolderNames_AreAccepted(
        string name)
    {
        var service =
            new ValidationService();

        var result =
            service.ValidateAssetFolderName(
                name);

        Assert.True(
            result.IsValid);
    }

    [Fact]
    public void EmptyImage_IsRejected()
    {
        using var workspace =
            new TestWorkspace();

        var path =
            Path.Combine(
                workspace.Downloads,
                "empty.png");

        File.WriteAllBytes(
            path,
            Array.Empty<byte>());

        var service =
            new ValidationService();

        var result =
            service.ValidateImageFile(
                path,
                new[]
                {
                    ".png"
                });

        Assert.False(
            result.IsValid);
    }

    [Fact]
    public void UnsupportedImageExtension_IsRejected()
    {
        using var workspace =
            new TestWorkspace();

        var path =
            Path.Combine(
                workspace.Downloads,
                "image.bmp");

        File.WriteAllBytes(
            path,
            new byte[]
            {
                1
            });

        var service =
            new ValidationService();

        var result =
            service.ValidateImageFile(
                path,
                new[]
                {
                    ".png"
                });

        Assert.False(
            result.IsValid);
    }
}

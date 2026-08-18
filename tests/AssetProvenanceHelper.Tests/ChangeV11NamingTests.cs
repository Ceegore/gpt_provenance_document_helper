using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class ChangeV11NamingTests
{
    [Theory]
    [InlineData(@"D:\gameassets\gamename", "gamename")]
    [InlineData(@"D:\gameassets\My Project", "My Project")]
    [InlineData(@"C:\Assets\SpellQuake\", "SpellQuake")]
    [InlineData(@"/var/assets/rpg", "rpg")]
    public void DeriveProjectLabel_FromNormalAssetRoot(string assetRoot, string expectedLabel)
    {
        var result = AssetNaming.DeriveProjectLabel(assetRoot);
        Assert.Equal(expectedLabel, result);
    }

    [Fact]
    public void DeriveProjectLabel_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AssetNaming.DeriveProjectLabel(""));
        Assert.Equal(string.Empty, AssetNaming.DeriveProjectLabel("   "));
    }

    [Fact]
    public void NewReferenceSession_ProjectLabelDerivedFromAssetRoot()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "onboarding1", refSource, DateTimeOffset.Now);

        var expectedProject = new DirectoryInfo(workspace.Assets).Name;
        Assert.Equal(expectedProject, session.ProjectName);

        var provText = File.ReadAllText(session.ReferenceProvenancePath);
        Assert.Contains($"Project: {expectedProject}", provText);
    }

    [Fact]
    public void BuildIngameFilename_PreservesExtension()
    {
        Assert.Equal("onboarding1.png", AssetNaming.BuildIngameFilename("onboarding1", "ChatGPT Image final.png"));
        Assert.Equal("onboarding1.JPG", AssetNaming.BuildIngameFilename("onboarding1", "photo.JPG"));
        Assert.Equal("menu_bg.webp", AssetNaming.BuildIngameFilename("menu_bg", "test.webp"));
    }

    [Fact]
    public void BuildIngameFilename_ThrowsOnEmptyInputs()
    {
        Assert.Throws<ArgumentException>(() => AssetNaming.BuildIngameFilename("", "test.png"));
        Assert.Throws<ArgumentException>(() => AssetNaming.BuildIngameFilename("asset", ""));
    }
}

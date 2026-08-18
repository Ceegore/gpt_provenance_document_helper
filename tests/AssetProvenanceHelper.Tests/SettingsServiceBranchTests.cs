using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class SettingsServiceBranchTests
{
    [Fact]
    public void Load_CorruptedSettingsFile_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(settingsPath, "{ invalid json data ...", Encoding.UTF8);

        var service = new SettingsService(settingsPath);
        Assert.Throws<InvalidDataException>(() => service.Load());
    }

    [Fact]
    public void Load_SettingsWithEmptyExtensions_FallsBackToDefaultExtensions()
    {
        using var workspace = new TestWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(settingsPath, "{\"ProjectName\":\"Test\",\"AcceptedExtensions\":[]}", Encoding.UTF8);

        var service = new SettingsService(settingsPath);
        var loaded = service.Load();

        Assert.NotNull(loaded);
        Assert.Contains(".png", loaded.AcceptedExtensions);
        Assert.Contains(".jpg", loaded.AcceptedExtensions);
    }

    [Fact]
    public void Save_NullSettings_ThrowsArgumentNullException()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateSettingsService();

        Action act = () => service.Save(null!);
        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public void NormalizeExtension_HandlesLeadingDotsAndSpaces()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateSettingsService();

        var settings = new AppSettings
        {
            ProjectName = "Test",
            DownloadFolder = workspace.Downloads,
            AssetRootFolder = workspace.Assets,
            AcceptedExtensions = new List<string> { " png ", ".JPG", "webp" }
        };

        service.Save(settings);
        var loaded = service.Load();

        Assert.Contains(".png", loaded.AcceptedExtensions);
        Assert.Contains(".jpg", loaded.AcceptedExtensions);
        Assert.Contains(".webp", loaded.AcceptedExtensions);
    }
}

using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void MissingSettings_ReturnsDefaults()
    {
        using var workspace =
            new TestWorkspace();

        var service =
            new SettingsService(
                workspace.SettingsPath);

        var settings =
            service.Load();

        Assert.NotNull(
            settings);

        Assert.NotNull(
            settings.AcceptedExtensions);

        Assert.Contains(
            ".png",
            settings.AcceptedExtensions);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsValues()
    {
        using var workspace =
            new TestWorkspace();

        var service =
            new SettingsService(
                workspace.SettingsPath);

        var settings =
            workspace.CreateSettings();

        settings.ProjectName =
            "SpëllQuäke 日本語";

        service.Save(
            settings);

        var loaded =
            service.Load();

        Assert.Equal(
            settings.ProjectName,
            loaded.ProjectName);

        Assert.Equal(
            settings.DownloadFolder,
            loaded.DownloadFolder);

        Assert.Equal(
            settings.AssetRootFolder,
            loaded.AssetRootFolder);
    }

    [Fact]
    public void Save_NormalizesExtensions()
    {
        using var workspace =
            new TestWorkspace();

        var service =
            new SettingsService(
                workspace.SettingsPath);

        var settings =
            workspace.CreateSettings();

        settings.AcceptedExtensions =
            new List<string>
            {
                "PNG",
                ".WEBP",
                "jpg"
            };

        service.Save(
            settings);

        var loaded =
            service.Load();

        Assert.Contains(
            ".png",
            loaded.AcceptedExtensions);

        Assert.Contains(
            ".webp",
            loaded.AcceptedExtensions);

        Assert.Contains(
            ".jpg",
            loaded.AcceptedExtensions);
    }
}

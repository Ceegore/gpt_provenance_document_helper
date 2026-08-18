using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class ImageFinderServiceTests
{
    [Fact]
    public void EmptyFolder_ReturnsNull()
    {
        using var workspace =
            new TestWorkspace();

        var service =
            new ImageFinderService();

        var result =
            service.FindLatestImage(
                workspace.CreateSettings());

        Assert.Null(
            result);
    }

    [Fact]
    public void UnsupportedExtensions_AreIgnored()
    {
        using var workspace =
            new TestWorkspace();

        File.WriteAllText(
            Path.Combine(
                workspace.Downloads,
                "something.txt"),
            "x");

        var service =
            new ImageFinderService();

        var result =
            service.FindLatestImage(
                workspace.CreateSettings());

        Assert.Null(
            result);
    }

    [Fact]
    public void NewestSupportedImage_IsSelected()
    {
        using var workspace =
            new TestWorkspace();

        var oldFile =
            workspace.CreateImage(
                "old.png");

        var newFile =
            workspace.CreateImage(
                "new.png");

        File.SetLastWriteTimeUtc(
            oldFile,
            DateTime.UtcNow.AddMinutes(-2));

        File.SetLastWriteTimeUtc(
            newFile,
            DateTime.UtcNow);

        var service =
            new ImageFinderService();

        var result =
            service.FindLatestImage(
                workspace.CreateSettings());

        Assert.Equal(
            Path.GetFullPath(newFile),
            Path.GetFullPath(result!));
    }

    [Fact]
    public void NewestSupportedImage_WinsRegardlessOfChatGPTFilename()
    {
        using var workspace =
            new TestWorkspace();

        var chatGpt =
            workspace.CreateImage(
                "ChatGPT Image old.png");

        var unrelated =
            workspace.CreateImage(
                "newer.png");

        File.SetLastWriteTimeUtc(
            chatGpt,
            DateTime.UtcNow.AddMinutes(-10));

        File.SetLastWriteTimeUtc(
            unrelated,
            DateTime.UtcNow);

        var service =
            new ImageFinderService();

        var result =
            service.FindLatestImage(
                workspace.CreateSettings());

        Assert.Equal(
            Path.GetFullPath(unrelated),
            Path.GetFullPath(result!));
    }

    [Fact]
    public void FallsBackToNormalImage_WhenNoChatGptImageExists()
    {
        using var workspace =
            new TestWorkspace();

        var file =
            workspace.CreateImage(
                "normal.webp");

        var service =
            new ImageFinderService();

        var result =
            service.FindLatestImage(
                workspace.CreateSettings());

        Assert.Equal(
            Path.GetFullPath(file),
            Path.GetFullPath(result!));
    }
}

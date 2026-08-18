using AssetProvenanceHelper;

namespace AssetProvenanceHelper.Tests;

public sealed class SessionServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTripSession()
    {
        using var workspace =
            new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var source =
            workspace.CreateImage(
                "reference.png");

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                source,
                DateTimeOffset.Now);

        var service =
            workspace.CreateSessionService();

        service.Save(
            session);

        Assert.True(
            service.Exists());

        var loaded =
            service.Load();

        Assert.NotNull(
            loaded);

        Assert.Equal(
            session.ProjectName,
            loaded!.ProjectName);

        Assert.Equal(
            session.AssetFolder,
            loaded.AssetFolder);

        Assert.Equal(
            session.ReferenceHash,
            loaded.ReferenceHash);
    }

    [Fact]
    public void Delete_RemovesSessionFile()
    {
        using var workspace =
            new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var source =
            workspace.CreateImage(
                "reference.png");

        var session =
            processor.ProcessReference(
                workspace.CreateSettings(),
                "asset1",
                source,
                DateTimeOffset.Now);

        var service =
            workspace.CreateSessionService();

        service.Save(
            session);

        service.Delete();

        Assert.False(
            service.Exists());
    }

    [Fact]
    public void Cancel_RemovesReferenceButPreservesUnrelatedFiles()
    {
        using var workspace =
            new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var source =
            workspace.CreateImage(
                "reference.png");

        var session =
            processor.ProcessReference(
                workspace.CreateSettings(),
                "asset1",
                source,
                DateTimeOffset.Now);

        var unrelated =
            Path.Combine(
                session.AssetFolder,
                "unrelated.txt");

        File.WriteAllText(
            unrelated,
            "keep");

        var ingame =
            Path.Combine(
                session.AssetFolder,
                "ingame");

        Directory.CreateDirectory(
            ingame);

        var ingameFile =
            Path.Combine(
                ingame,
                "keep.txt");

        File.WriteAllText(
            ingameFile,
            "keep");

        var service =
            workspace.CreateSessionService();

        service.Save(
            session);

        service.Cancel(
            session);

        Assert.False(
            File.Exists(
                session.ReferenceDestinationPath));

        Assert.False(
            File.Exists(
                session.ReferenceProvenancePath));

        Assert.True(
            File.Exists(
                unrelated));

        Assert.True(
            File.Exists(
                ingameFile));

        Assert.False(
            service.Exists());
    }

    [Fact]
    public void Cancel_RejectsTamperedAssetFolder()
    {
        using var workspace =
            new TestWorkspace();

        var processor =
            workspace.CreateAssetProcessor();

        var source =
            workspace.CreateImage(
                "reference.png");

        var session =
            processor.ProcessReference(
                workspace.CreateSettings(),
                "asset1",
                source,
                DateTimeOffset.Now);

        session.AssetFolder =
            Path.Combine(
                workspace.Root,
                "SomeOtherFolder");

        var service =
            workspace.CreateSessionService();

        Assert.Throws<InvalidDataException>(
            () =>
                service.Cancel(
                    session));
    }
}

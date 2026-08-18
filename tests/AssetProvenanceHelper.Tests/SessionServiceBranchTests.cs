using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class SessionServiceBranchTests
{
    [Fact]
    public void Load_NonExistentFile_ReturnsNull()
    {
        using var workspace = new TestWorkspace();
        var service = new SessionService(Path.Combine(workspace.Root, "nonexistent_session.json"));
        Assert.Null(service.Load());
    }

    [Fact]
    public void Load_CorruptJson_ThrowsException()
    {
        using var workspace = new TestWorkspace();
        var sessionFile = Path.Combine(workspace.Root, "corrupt_session.json");
        File.WriteAllText(sessionFile, "{ this is not valid json }", Encoding.UTF8);

        var service = new SessionService(sessionFile);
        Assert.ThrowsAny<Exception>(() => service.Load());
    }

    [Fact]
    public void Cancel_NullSession_ThrowsArgumentNullException()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateSessionService();
        Assert.Throws<ArgumentNullException>(() => service.Cancel(null!));
    }

    [Fact]
    public void Cancel_UnsafePaths_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateSessionService();

        var dangerousSession = new AssetSession
        {
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "unsafe.dll",
            ReferenceDestinationPath = @"C:\Windows\System32\unsafe.dll",
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", AppConstants.ReferenceProvenanceFileName)
        };

        Assert.Throws<InvalidDataException>(() => service.Cancel(dangerousSession));
    }
}

using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class ChangeV11IngameTests
{
    [Fact]
    public void ProcessMainImage_CreatesRootMainAndIngameCopy()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "onboarding1", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("ChatGPT Image 2026-08-18.png", new byte[] { 4, 5, 6 });
        var result = processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "Draw a magical castle",
            DateTimeOffset.Now);

        Assert.Equal("ChatGPT Image 2026-08-18.png", result);
        Assert.Equal("onboarding1.png", session.IngameFilename);
        Assert.Equal("ChatGPT Image 2026-08-18.png", session.MainFilename);

        var rootMainPath = Path.Combine(session.AssetFolder, "ChatGPT Image 2026-08-18.png");
        var ingameMainPath = Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "onboarding1.png");
        Assert.True(File.Exists(rootMainPath));
        Assert.True(File.Exists(ingameMainPath));
    }

    [Fact]
    public void ProcessMainImage_ProvenanceContainsRootAssetId_AndPrompt_AndReference()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "onboarding1", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("ChatGPT Image 2026-08-18.png", new byte[] { 4, 5, 6 });
        processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "Draw a magical castle with dragons",
            DateTimeOffset.Now);

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        Assert.True(File.Exists(finalProvPath));

        var text = File.ReadAllText(finalProvPath, Encoding.UTF8);
        Assert.Contains("Asset ID: ChatGPT Image 2026-08-18.png", text);
        Assert.Contains($"Reference asset: {session.ReferenceFilename}", text);
        Assert.Contains($"Project: {session.ProjectName}", text);
        Assert.Contains("Draw a magical castle with dragons", text);
    }

    [Fact]
    public void RollbackMain_DeletesRootMainAndIngameFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "onboarding1", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("ChatGPT Image 2026-08-18.png", new byte[] { 4, 5, 6 });
        var mainFilename = processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "A test prompt",
            DateTimeOffset.Now);

        var rootMainPath = Path.Combine(session.AssetFolder, mainFilename);
        var ingamePath = Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "onboarding1.png");
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        Assert.True(File.Exists(rootMainPath));
        Assert.True(File.Exists(ingamePath));
        Assert.True(File.Exists(finalProvPath));

        var rollback = processor.RollbackMain(session, mainFilename);
        Assert.True(rollback.IsValid, string.Join(Environment.NewLine, rollback.Errors));

        Assert.False(File.Exists(rootMainPath));
        Assert.False(File.Exists(ingamePath));
        Assert.False(File.Exists(finalProvPath));
        Assert.False(session.IsMainCommitting);
    }
}

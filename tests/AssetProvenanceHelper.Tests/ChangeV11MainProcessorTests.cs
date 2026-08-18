using System.Globalization;
using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class ChangeV11MainProcessorTests
{
    [Fact]
    public void CreateNoReferenceMainSession_SucceedsWithValidInputs()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var sourcePath = workspace.CreateImage("hero.png", new byte[] { 1, 2, 3, 4, 5 });
        var processedAt = DateTimeOffset.Now;

        var session = processor.CreateNoReferenceMainSession(
            settings,
            "hero_asset",
            sourcePath,
            "A heroic knight",
            processedAt);

        Assert.Equal(AssetWorkflowMode.NoReference, session.WorkflowMode);
        Assert.Equal("Assets", session.ProjectName);
        Assert.Equal("hero_asset", session.AssetFolderName);
        Assert.Equal(Path.Combine(workspace.Assets, "hero_asset"), session.AssetFolder);
        Assert.True(session.IsMainCommitting);
        Assert.Equal("hero.png", session.MainFilename);
        Assert.Equal("hero_asset.png", session.GetIngameFilename());
        Assert.Equal("A heroic knight", session.MainPrompt);
        Assert.Equal(processedAt, session.MainProcessedAt);
        Assert.False(string.IsNullOrEmpty(session.MainHash));
        Assert.False(string.IsNullOrEmpty(session.MainTransactionId));
        Assert.Empty(session.ReferenceFilename);
        Assert.True(session.WasAssetFolderCreatedByTool);
        Assert.True(session.WasIngameFolderCreatedByTool);
    }

    [Fact]
    public void CreateNoReferenceMainSession_PreflightsExistingCanonicalFiles()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var assetFolder = Path.Combine(workspace.Assets, "hero_asset");
        Directory.CreateDirectory(assetFolder);
        File.WriteAllText(Path.Combine(assetFolder, AppConstants.FinalProvenanceFileName), "Existing");

        var sourcePath = workspace.CreateImage("hero.png", new byte[] { 1, 2, 3 });

        var ex = Assert.Throws<IOException>(() =>
            processor.CreateNoReferenceMainSession(
                settings,
                "hero_asset",
                sourcePath,
                "Prompt",
                DateTimeOffset.Now));

        Assert.Contains("Final provenance already exists", ex.Message);
    }

    [Fact]
    public void CreateNoReferenceMainSession_PreflightsExistingIngameVariants()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ingameFolder = Path.Combine(workspace.Assets, "hero_asset", AppConstants.IngameFolderName);
        Directory.CreateDirectory(ingameFolder);
        File.WriteAllBytes(Path.Combine(ingameFolder, "hero_asset.jpg"), new byte[] { 9, 9 });

        var sourcePath = workspace.CreateImage("hero.png", new byte[] { 1, 2, 3 });

        var ex = Assert.Throws<IOException>(() =>
            processor.CreateNoReferenceMainSession(
                settings,
                "hero_asset",
                sourcePath,
                "Prompt",
                DateTimeOffset.Now));

        Assert.Contains("An ingame asset variant already exists", ex.Message);
    }

    [Fact]
    public void ProcessMainImage_NoReference_CreatesRootAndIngameAndProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var sourcePath = workspace.CreateImage("ChatGPT final.png", new byte[] { 10, 20, 30, 40 });
        var processedAt = DateTimeOffset.UtcNow;

        var session = processor.CreateNoReferenceMainSession(
            settings,
            "onboarding1",
            sourcePath,
            "Epic landscape",
            processedAt);

        var resultName = processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            sourcePath,
            "Epic landscape",
            processedAt);

        Assert.Equal("ChatGPT final.png", resultName);

        var rootMain = Path.Combine(session.AssetFolder, "ChatGPT final.png");
        var ingameMain = Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "onboarding1.png");
        var provPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        Assert.True(File.Exists(rootMain), "Root main must exist");
        Assert.True(File.Exists(ingameMain), "Ingame main must exist");
        Assert.True(File.Exists(provPath), "Final provenance must exist");

        var rootBytes = File.ReadAllBytes(rootMain);
        var ingameBytes = File.ReadAllBytes(ingameMain);
        Assert.Equal(rootBytes, ingameBytes);

        var provText = File.ReadAllText(provPath, Encoding.UTF8);
        Assert.Contains("Asset ID: ChatGPT final.png", provText);
        Assert.Contains("Project: Assets", provText);
        Assert.Contains("Prompt: \"Epic landscape\"", provText);
        Assert.DoesNotContain("Reference asset:", provText);
    }

    [Fact]
    public void ProcessMainImage_ReferenceAssisted_CreatesRootAndIngameAndProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(
            settings,
            "onboarding1",
            refSource,
            DateTimeOffset.UtcNow);

        var mainSource = workspace.CreateImage("ChatGPT main.png", new byte[] { 4, 5, 6, 7 });
        var processedAt = DateTimeOffset.UtcNow;

        session.IsMainCommitting = true;
        session.MainFilename = "ChatGPT main.png";
        session.MainPrompt = "Reference-based prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = processor.ComputeSha256(mainSource);
        session.MainTransactionId = Guid.NewGuid().ToString("N");

        var resultName = processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "Reference-based prompt",
            processedAt);

        Assert.Equal("ChatGPT main.png", resultName);

        var rootMain = Path.Combine(session.AssetFolder, "ChatGPT main.png");
        var ingameMain = Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "onboarding1.png");
        var provPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        Assert.True(File.Exists(rootMain));
        Assert.True(File.Exists(ingameMain));
        Assert.True(File.Exists(provPath));

        Assert.Equal(File.ReadAllBytes(rootMain), File.ReadAllBytes(ingameMain));

        var provText = File.ReadAllText(provPath, Encoding.UTF8);
        Assert.Contains("Asset ID: ChatGPT main.png", provText);
        Assert.Contains("Reference asset: ref.png", provText);
    }

    [Fact]
    public void RollbackMain_NoReference_DeletesAssetFolderIfCreatedByTool()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var sourcePath = workspace.CreateImage("main.png", new byte[] { 1, 2, 3, 4 });
        var processedAt = DateTimeOffset.UtcNow;

        var session = processor.CreateNoReferenceMainSession(
            settings,
            "clean_me",
            sourcePath,
            "Prompt",
            processedAt);

        processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            sourcePath,
            "Prompt",
            processedAt);

        Assert.True(Directory.Exists(session.AssetFolder));

        var rollbackResult = processor.RollbackMain(session);
        Assert.True(rollbackResult.IsValid, string.Join(Environment.NewLine, rollbackResult.Errors));

        Assert.False(Directory.Exists(session.AssetFolder), "Asset folder should be deleted because it was tool-created in NoReference mode");
    }

    [Fact]
    public void RollbackMain_ReferenceAssisted_PreservesAssetFolderAndReference()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(
            settings,
            "preserve_ref",
            refSource,
            DateTimeOffset.UtcNow);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6, 7 });
        var processedAt = DateTimeOffset.UtcNow;

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = processor.ComputeSha256(mainSource);
        session.MainTransactionId = Guid.NewGuid().ToString("N");

        processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "Prompt",
            processedAt);

        var rollbackResult = processor.RollbackMain(session);
        Assert.True(rollbackResult.IsValid, string.Join(Environment.NewLine, rollbackResult.Errors));

        Assert.True(Directory.Exists(session.AssetFolder), "Asset folder should be preserved");
        Assert.True(File.Exists(session.ReferenceDestinationPath), "Reference image must be preserved");
        Assert.True(File.Exists(session.ReferenceProvenancePath), "Reference provenance must be preserved");
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")), "Root main must be deleted");
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "preserve_ref.png")), "Ingame main must be deleted");
    }
}

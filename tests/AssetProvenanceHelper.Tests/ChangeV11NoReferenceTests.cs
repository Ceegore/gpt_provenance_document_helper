using System.Globalization;
using System.Text;
using System.Text.Json;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public sealed class ChangeV11NoReferenceTests
{
    [Fact]
    public void ProductionTemplates_ValidateAllThree()
    {
        var baseDir = AppContext.BaseDirectory;
        var refPath = AppBootstrap.GetReferenceTemplatePath(baseDir);
        var finalPath = AppBootstrap.GetFinalTemplatePath(baseDir);
        var noRefPath = AppBootstrap.GetFinalNoReferenceTemplatePath(baseDir);

        Assert.True(File.Exists(refPath), $"Reference template missing at {refPath}");
        Assert.True(File.Exists(finalPath), $"Final template missing at {finalPath}");
        Assert.True(File.Exists(noRefPath), $"No-reference template missing at {noRefPath}");

        var service = new TemplateService(refPath, finalPath, noRefPath);
        var validation = service.ValidateTemplates();

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public void RenderFinalNoReference_ReplacesAllTokens()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateTemplateService();

        var rendered = service.RenderFinalNoReference(
            "hero.png",
            "GameProject",
            "2026-08-18",
            "Draw a courageous knight");

        Assert.Contains("Asset ID: hero.png", rendered);
        Assert.Contains("Project: GameProject", rendered);
        Assert.Contains("Generation date: 2026-08-18", rendered);
        Assert.Contains("Prompt: \"Draw a courageous knight\"", rendered);
        Assert.DoesNotContain("{{FINAL_FILENAME}}", rendered);
        Assert.DoesNotContain("{{PROJECT}}", rendered);
        Assert.DoesNotContain("{{GENERATION_DATE}}", rendered);
        Assert.DoesNotContain("{{PROMPT}}", rendered);
    }

    [Fact]
    public void RenderFinalNoReference_PreservesPromptLiterally()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateTemplateService();

        var promptWithSpecialChars = "A knight with {{PROJECT}} and \nnewlines and quotes \"hello\" & symbols <tag>";
        var rendered = service.RenderFinalNoReference(
            "hero.png",
            "MyGame",
            "2026-08-18",
            promptWithSpecialChars);

        Assert.Contains(promptWithSpecialChars, rendered);
    }

    [Fact]
    public void UnknownNoReferenceToken_Fails()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(workspace.FinalNoReferenceTemplatePath, "Asset ID: {{FINAL_FILENAME}}\n{{UNKNOWN_TOKEN}}");

        var service = workspace.CreateTemplateService();
        var validation = service.ValidateTemplates();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("UNKNOWN_TOKEN"));
    }

    [Fact]
    public void ExactNoReferenceFinalProvenanceOwnership_Succeeds()
    {
        using var workspace = new TestWorkspace();
        var validator = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var processedAt = DateTimeOffset.Now;
        var session = new AssetSession
        {
            WorkflowMode = AssetWorkflowMode.NoReference,
            ProjectName = "MyProject",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "test_asset",
            AssetFolder = Path.Combine(workspace.Assets, "test_asset"),
            IsMainCommitting = true,
            MainFilename = "source.png",
            MainPrompt = "A brave wizard",
            MainProcessedAt = processedAt,
            MainHash = new string('a', 64),
            MainTransactionId = "0123456789abcdef0123456789abcdef"
        };

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        Directory.CreateDirectory(session.AssetFolder);

        var provContent = templateService.RenderFinalNoReference(
            session.MainFilename,
            session.ProjectName,
            processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            session.MainPrompt);

        File.WriteAllText(finalProvPath, provContent, Encoding.UTF8);

        var result = validator.ValidateExactFinalProvenanceOwnership(session, finalProvPath, templateService);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ExactNoReferenceFinalProvenanceOwnership_RejectsModifiedFile()
    {
        using var workspace = new TestWorkspace();
        var validator = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var processedAt = DateTimeOffset.Now;
        var session = new AssetSession
        {
            WorkflowMode = AssetWorkflowMode.NoReference,
            ProjectName = "MyProject",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "test_asset",
            AssetFolder = Path.Combine(workspace.Assets, "test_asset"),
            IsMainCommitting = true,
            MainFilename = "source.png",
            MainPrompt = "A brave wizard",
            MainProcessedAt = processedAt,
            MainHash = new string('a', 64),
            MainTransactionId = "0123456789abcdef0123456789abcdef"
        };

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        Directory.CreateDirectory(session.AssetFolder);

        var provContent = templateService.RenderFinalNoReference(
            session.MainFilename,
            session.ProjectName,
            processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            session.MainPrompt) + "\nTAMPERED CONTENT";

        File.WriteAllText(finalProvPath, provContent, Encoding.UTF8);

        var result = validator.ValidateExactFinalProvenanceOwnership(session, finalProvPath, templateService);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not exactly match expected tool-generated provenance"));
    }

    [Fact]
    public void LegacySession_WithoutWorkflowMode_DefaultsReferenceAssisted()
    {
        var json = """
        {
            "ProjectName": "MyProj",
            "AssetRootFolder": "D:\\Assets",
            "AssetFolderName": "Asset1",
            "AssetFolder": "D:\\Assets\\Asset1",
            "ReferenceSourcePath": "D:\\Downloads\\ref.png",
            "ReferenceDestinationPath": "D:\\Assets\\Asset1\\reference\\ref.png",
            "ReferenceFilename": "ref.png",
            "ReferenceProvenancePath": "D:\\Assets\\Asset1\\reference\\license.txt — AI Reference Asset.md",
            "ReferenceHash": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "ReferenceProcessedAt": "2026-08-18T12:00:00+00:00"
        }
        """;

        var session = JsonSerializer.Deserialize<AssetSession>(json);
        Assert.NotNull(session);
        Assert.Equal(AssetWorkflowMode.ReferenceAssisted, session.WorkflowMode);
    }

    [Fact]
    public void NoReferenceSessionValidation_AllowsAbsentAssetFolderOnlyForActiveJournal()
    {
        using var workspace = new TestWorkspace();
        var validator = workspace.CreateValidationService();

        var assetFolder = Path.Combine(workspace.Assets, "absent_asset");
        Assert.False(Directory.Exists(assetFolder));

        var session = new AssetSession
        {
            WorkflowMode = AssetWorkflowMode.NoReference,
            ProjectName = "Assets",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "absent_asset",
            AssetFolder = assetFolder,
            WasAssetFolderCreatedByTool = true,
            IsMainCommitting = true,
            MainFilename = "main.png",
            MainPrompt = "A scenic view",
            MainProcessedAt = DateTimeOffset.Now,
            MainHash = new string('1', 64),
            MainTransactionId = "0123456789abcdef0123456789abcdef"
        };

        var result = validator.ValidateSession(session);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

        // If WasAssetFolderCreatedByTool is false, absent folder fails
        session.WasAssetFolderCreatedByTool = false;
        var failResult = validator.ValidateSession(session);
        Assert.False(failResult.IsValid);
        Assert.Contains(failResult.Errors, e => e.Contains("AssetFolder does not exist"));
    }

    [Fact]
    public void NoReferenceSessionValidation_RejectsReferenceFields()
    {
        using var workspace = new TestWorkspace();
        var validator = workspace.CreateValidationService();

        var session = new AssetSession
        {
            WorkflowMode = AssetWorkflowMode.NoReference,
            ProjectName = "Assets",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            WasAssetFolderCreatedByTool = true,
            IsMainCommitting = true,
            MainFilename = "main.png",
            MainPrompt = "Prompt",
            MainProcessedAt = DateTimeOffset.Now,
            MainHash = new string('1', 64),
            MainTransactionId = "0123456789abcdef0123456789abcdef",
            ReferenceFilename = "ref.png" // Illegal in NoReference
        };

        var result = validator.ValidateSession(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ReferenceFilename must be empty"));
    }

    [Fact]
    public void NoReferenceDestructivePathValidation_DoesNotRequireReferenceFilename()
    {
        using var workspace = new TestWorkspace();

        var session = new AssetSession
        {
            WorkflowMode = AssetWorkflowMode.NoReference,
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_noref",
            AssetFolder = Path.Combine(workspace.Assets, "asset_noref")
        };

        var result = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }
}

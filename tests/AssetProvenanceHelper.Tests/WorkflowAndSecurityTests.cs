using System.Text;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class WorkflowAndSecurityTests
{
    [Fact]
    public void TEST_R01_ReferenceSource_RemainsIntactAndUnchanged()
    {
        using var workspace =
            new TestWorkspace();

        var sourceBytes =
            new byte[] { 10, 20, 30, 40, 50 };

        var sourcePath =
            workspace.CreateImage("ChatGPT Image test.png", sourceBytes);

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var hashBefore =
            processor.ComputeSha256(sourcePath);

        var session =
            processor.ProcessReference(
                settings,
                "asset_folder",
                sourcePath,
                DateTimeOffset.Now);

        var hashAfter =
            processor.ComputeSha256(sourcePath);

        Assert.True(File.Exists(sourcePath));
        Assert.Equal(hashBefore, hashAfter);
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.Equal(hashBefore, session.ReferenceHash);
    }

    [Fact]
    public void TEST_R02_ExistingProvenance_IsNotOverwritten()
    {
        using var workspace =
            new TestWorkspace();

        var sourcePath =
            workspace.CreateImage("source.png");

        var assetFolder =
            Path.Combine(workspace.Assets, "asset_existing_prov");

        var referenceFolder =
            Path.Combine(assetFolder, AppConstants.ReferenceFolderName);

        Directory.CreateDirectory(referenceFolder);

        var provenancePath =
            Path.Combine(referenceFolder, AppConstants.ReferenceProvenanceFileName);

        const string existingContent = "EXISTING PROVENANCE CONTENT DO NOT OVERWRITE";
        File.WriteAllText(provenancePath, existingContent, Encoding.UTF8);

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        Assert.Throws<IOException>(
            () =>
                processor.ProcessReference(
                    settings,
                    "asset_existing_prov",
                    sourcePath,
                    DateTimeOffset.Now));

        Assert.Equal(existingContent, File.ReadAllText(provenancePath, Encoding.UTF8));
    }

    [Fact]
    public void TEST_R03_ExistingAssetFolderContent_RemainsIntact()
    {
        using var workspace =
            new TestWorkspace();

        var sourcePath =
            workspace.CreateImage("source.png");

        var assetFolder =
            Path.Combine(workspace.Assets, "asset_existing");

        Directory.CreateDirectory(assetFolder);

        var existingFile =
            Path.Combine(assetFolder, "existing.txt");

        File.WriteAllText(existingFile, "preexisting content", Encoding.UTF8);

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset_existing",
                sourcePath,
                DateTimeOffset.Now);

        Assert.True(File.Exists(existingFile));
        Assert.Equal("preexisting content", File.ReadAllText(existingFile, Encoding.UTF8));
        Assert.False(session.WasAssetFolderCreatedByTool);
    }

    [Fact]
    public void TEST_M01_EmptyPrompt_IsRejected()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var mainSource =
            workspace.CreateImage("main.png", new byte[] { 2 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                refSource,
                DateTimeOffset.Now);

        Assert.Throws<ArgumentException>(
            () =>
                processor.ProcessMainPrepared(
                    session,
                    settings.AcceptedExtensions,
                    mainSource,
                    "",
                    DateTimeOffset.Now));

        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
    }

    [Fact]
    public void TEST_M02_WhitespacePrompt_IsRejected()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var mainSource =
            workspace.CreateImage("main.png", new byte[] { 2 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                refSource,
                DateTimeOffset.Now);

        Assert.Throws<ArgumentException>(
            () =>
                processor.ProcessMainPrepared(
                    session,
                    settings.AcceptedExtensions,
                    mainSource,
                    "   \t\r\n   ",
                    DateTimeOffset.Now));

        Assert.True(File.Exists(session.ReferenceDestinationPath));
    }

    [Fact]
    public void TEST_M03_UnicodePrompt_IsPreservedExactly()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var mainSource =
            workspace.CreateImage("main.png", new byte[] { 2 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                refSource,
                DateTimeOffset.Now);

        const string unicodePrompt = "größer – schöner 日本語 😀 特殊文字 & \"quotes\" 'single'";

        var filename =
            processor.ProcessMainPrepared(
                session,
                settings.AcceptedExtensions,
                mainSource,
                unicodePrompt,
                DateTimeOffset.Now);

        var finalProvPath =
            Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        var provText =
            File.ReadAllText(finalProvPath, Encoding.UTF8);

        Assert.Contains(unicodePrompt, provText);
    }

    [Fact]
    public void TEST_M04_MultilinePrompt_IsPreservedExactly()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var mainSource =
            workspace.CreateImage("main.png", new byte[] { 2 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                refSource,
                DateTimeOffset.Now);

        const string multilinePrompt = "Zeile 1: bitte 4 Varianten\nZeile 2: mehr Kontrast\r\nZeile 3: final";

        var filename =
            processor.ProcessMainPrepared(
                session,
                settings.AcceptedExtensions,
                mainSource,
                multilinePrompt,
                DateTimeOffset.Now);

        var finalProvPath =
            Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        var provText =
            File.ReadAllText(finalProvPath, Encoding.UTF8);

        Assert.Contains(multilinePrompt, provText);
    }

    [Fact]
    public void TEST_M05_MainDestinationAlreadyExists_IsRejectedWithoutOverwrite()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var mainSource =
            workspace.CreateImage("main.png", new byte[] { 2 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                refSource,
                DateTimeOffset.Now);

        var mainDestPath =
            Path.Combine(session.AssetFolder, "main.png");

        File.WriteAllBytes(mainDestPath, new byte[] { 99, 98, 97 });

        Assert.Throws<IOException>(
            () =>
                processor.ProcessMainPrepared(
                    session,
                    settings.AcceptedExtensions,
                    mainSource,
                    "prompt",
                    DateTimeOffset.Now));

        Assert.Equal(new byte[] { 99, 98, 97 }, File.ReadAllBytes(mainDestPath));
    }

    [Fact]
    public void TEST_M06_FinalProvenanceAlreadyExists_IsRejectedWithoutOverwrite()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var mainSource =
            workspace.CreateImage("main.png", new byte[] { 2 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset1",
                refSource,
                DateTimeOffset.Now);

        var finalProvPath =
            Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        File.WriteAllText(finalProvPath, "DO NOT OVERWRITE", Encoding.UTF8);

        Assert.Throws<IOException>(
            () =>
                processor.ProcessMainPrepared(
                    session,
                    settings.AcceptedExtensions,
                    mainSource,
                    "prompt",
                    DateTimeOffset.Now));

        Assert.Equal("DO NOT OVERWRITE", File.ReadAllText(finalProvPath, Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
    }

    [Fact]
    public void TEST_C01_Cancel_RemovesToolCreatedEmptyFolder()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "new_asset_folder",
                refSource,
                DateTimeOffset.Now);

        Assert.True(session.WasAssetFolderCreatedByTool);
        Assert.True(session.WasReferenceFolderCreatedByTool);

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);
        sessionService.Cancel(session);

        Assert.False(Directory.Exists(session.AssetFolder));
        Assert.False(sessionService.Exists());
    }

    [Fact]
    public void TEST_C02_Cancel_PreservesPreExistingAssetFolder()
    {
        using var workspace =
            new TestWorkspace();

        var existingFolder =
            Path.Combine(workspace.Assets, "pre_existing_folder");

        Directory.CreateDirectory(existingFolder);

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "pre_existing_folder",
                refSource,
                DateTimeOffset.Now);

        Assert.False(session.WasAssetFolderCreatedByTool);

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);
        sessionService.Cancel(session);

        Assert.True(Directory.Exists(existingFolder));
        Assert.False(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void TEST_C03_Cancel_PreservesIngameFolderAndFiles()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "asset_with_ingame",
                refSource,
                DateTimeOffset.Now);

        var ingameDir =
            Path.Combine(session.AssetFolder, "ingame");

        Directory.CreateDirectory(ingameDir);

        var ingameFile =
            Path.Combine(ingameDir, "foo.png");

        File.WriteAllBytes(ingameFile, new byte[] { 7, 8, 9 });

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);
        sessionService.Cancel(session);

        Assert.True(Directory.Exists(session.AssetFolder));
        Assert.True(Directory.Exists(ingameDir));
        Assert.True(File.Exists(ingameFile));
        Assert.False(File.Exists(session.ReferenceDestinationPath));
    }

    [Fact]
    public void TEST_REC01_ValidRecovery_SessionIsValid()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "recovery_asset",
                refSource,
                DateTimeOffset.Now);

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);

        var loaded =
            sessionService.Load();

        Assert.NotNull(loaded);

        var validationService =
            workspace.CreateValidationService();

        var result =
            validationService.ValidateSession(loaded!);

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void TEST_REC02_ReferenceFileMissing_RecoveryFailsValidation()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "recovery_asset",
                refSource,
                DateTimeOffset.Now);

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);

        File.Delete(session.ReferenceDestinationPath);

        var loaded =
            sessionService.Load();

        var validationService =
            workspace.CreateValidationService();

        var result =
            validationService.ValidateSession(loaded!);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("reference image does not exist"));
    }

    [Fact]
    public void TEST_REC03_ReferenceProvenanceMissing_RecoveryFailsValidation()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "recovery_asset",
                refSource,
                DateTimeOffset.Now);

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);

        File.Delete(session.ReferenceProvenancePath);

        var loaded =
            sessionService.Load();

        var validationService =
            workspace.CreateValidationService();

        var result =
            validationService.ValidateSession(loaded!);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("reference provenance does not exist"));
    }

    [Fact]
    public void TEST_REC04_CorruptedSessionJson_ThrowsInvalidDataException()
    {
        using var workspace =
            new TestWorkspace();

        var sessionService =
            workspace.CreateSessionService();

        File.WriteAllText(workspace.SessionPath, "{THIS IS BROKEN JSON", Encoding.UTF8);

        Assert.Throws<InvalidDataException>(
            () => sessionService.Load());
    }

    [Fact]
    public void TEST_REC05_ReferenceFileModifiedAfterSessionSave_FailsValidationDueToHashDrift()
    {
        using var workspace =
            new TestWorkspace();

        var refSource =
            workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var processor =
            workspace.CreateAssetProcessor();

        var settings =
            workspace.CreateSettings();

        var session =
            processor.ProcessReference(
                settings,
                "recovery_asset",
                refSource,
                DateTimeOffset.Now);

        var sessionService =
            workspace.CreateSessionService();

        sessionService.Save(session);

        // Tamper with the bytes of the destination reference file
        File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 9, 9, 9, 9 });

        var loaded =
            sessionService.Load();

        var validationService =
            workspace.CreateValidationService();

        var result =
            validationService.ValidateSession(loaded!);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ReferenceHash does not match"));
    }

    [Fact]
    public void Phase33_GoldenTest_RealReferenceTemplate()
    {
        var baseDir = AppContext.BaseDirectory;
        var refTemplatePath = Path.Combine(baseDir, "templates", "reference.md");
        var finalTemplatePath = Path.Combine(baseDir, "templates", "final.md");

        var templateService =
            new TemplateService(refTemplatePath, finalTemplatePath);

        var validation =
            templateService.ValidateTemplates();

        Assert.True(validation.IsValid, string.Join(", ", validation.Errors));

        var rendered =
            templateService.RenderReference(
                "reference-test.png",
                "SpellQuake",
                "2026-08-17");

        Assert.Contains("Asset ID: reference-test.png", rendered);
        Assert.Contains("Asset role: Intermediate reference image", rendered);
        Assert.Contains("Project: SpellQuake", rendered);
        Assert.Contains("Helper record date: 2026-08-17", rendered);
        Assert.Contains("Generation date/time: not recorded", rendered);
        Assert.Contains("Human review: not recorded", rendered);
        Assert.Contains("Status: unapproved", rendered);
    }

    [Fact]
    public void Phase33_GoldenTest_RealFinalTemplate()
    {
        var baseDir = AppContext.BaseDirectory;
        var refTemplatePath = Path.Combine(baseDir, "templates", "reference.md");
        var finalTemplatePath = Path.Combine(baseDir, "templates", "final.md");

        var templateService =
            new TemplateService(refTemplatePath, finalTemplatePath);

        var validation =
            templateService.ValidateTemplates();

        Assert.True(validation.IsValid, string.Join(", ", validation.Errors));

        const string prompt = "bitte gib mir 4 varianten davon";

        var rendered =
            templateService.RenderFinal(
                "final-test.png",
                "reference-test.png",
                "SpellQuake",
                "2026-08-17",
                prompt);

        Assert.Contains("Asset ID: final-test.png", rendered);
        Assert.Contains("Asset role: Final production asset", rendered);
        Assert.Contains("Project: SpellQuake", rendered);
        Assert.Contains("Helper record date: 2026-08-17", rendered);
        Assert.Contains("Reference asset:", rendered);
        Assert.Contains("reference-test.png", rendered);
        Assert.Contains("Prompt: \"bitte gib mir 4 varianten davon\"", rendered);
        Assert.Contains("Reference file retained: yes", rendered);
        Assert.Contains("Generation date/time: not recorded", rendered);
        Assert.Contains("Human review: not recorded", rendered);
        Assert.Contains("Status: unapproved", rendered);
    }
}

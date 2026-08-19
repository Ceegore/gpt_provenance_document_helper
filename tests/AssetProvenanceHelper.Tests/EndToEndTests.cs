using System.Text;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// End-to-end workflow tests and edge case bug hunters.
/// </summary>
public sealed class EndToEndTests
{
    // ──────────────────────────────────────────────────────
    //  FULL WORKFLOW: Reference → Main → Verify
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_FullWorkflow_Reference_Then_Main_ProducesCompleteAsset()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        // Step 1: Create a reference image in "Downloads"
        var refBytes = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x01 };
        var refSource = workspace.CreateImage("ChatGPT Image Mar 24, 2026, 02_03_01 PM.png", refBytes);
        var refSourceHashBefore = processor.ComputeSha256(refSource);

        // Step 2: Process reference
        var timestamp = new DateTimeOffset(2026, 8, 17, 14, 30, 0, TimeSpan.FromHours(2));
        var session = processor.ProcessReference(settings, "hero_portrait_01", refSource, timestamp);

        // Verify reference outputs
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
        Assert.True(File.Exists(refSource), "Source must not be deleted from Downloads");
        Assert.Equal(refSourceHashBefore, processor.ComputeSha256(refSource));
        Assert.Equal(refSourceHashBefore, session.ReferenceHash);

        // Step 3: Save session
        sessionService.Save(session);
        Assert.True(sessionService.Exists());

        // Step 4: Validate session is valid
        var loaded = sessionService.Load()!;
        var sessionValid = validationService.ValidateSession(loaded);
        Assert.True(sessionValid.IsValid, string.Join(", ", sessionValid.Errors));

        // Step 5: Create main image
        var mainBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x02 };
        var mainSource = workspace.CreateImage("ChatGPT Image Mar 24, 2026, 02_10_05 PM.png", mainBytes);
        var mainSourceHashBefore = processor.ComputeSha256(mainSource);

        const string prompt = "bitte gib mir 4 varianten davon, mit mehr kontrast und wärmeren farben";

        // Step 6: Process main image
        var mainTimestamp = new DateTimeOffset(2026, 8, 17, 14, 35, 0, TimeSpan.FromHours(2));
        var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, mainSource, prompt, mainTimestamp);

        // Step 7: Delete session (simulating app completing the asset)
        sessionService.Delete();
        Assert.False(sessionService.Exists());

        // Step 8: Verify all final outputs
        var mainDestPath = Path.Combine(session.AssetFolder, mainFilename);
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        Assert.True(File.Exists(mainDestPath));
        Assert.True(File.Exists(finalProvPath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
        Assert.True(File.Exists(mainSource), "Main source must not be deleted from Downloads");
        Assert.Equal(mainSourceHashBefore, processor.ComputeSha256(mainSource));

        // Step 9: Verify provenance content integrity
        var refProvContent = File.ReadAllText(session.ReferenceProvenancePath, Encoding.UTF8);
        Assert.Contains(session.ReferenceFilename, refProvContent);
        Assert.Contains(session.ProjectName, refProvContent);
        Assert.Contains("2026-08-17", refProvContent);

        var finalProvContent = File.ReadAllText(finalProvPath, Encoding.UTF8);
        Assert.Contains(mainFilename, finalProvContent);
        Assert.Contains(session.ReferenceFilename, finalProvContent);
        Assert.Contains(session.ProjectName, finalProvContent);
        Assert.Contains(prompt, finalProvContent);
        Assert.Contains("2026-08-17", finalProvContent);

        var templateService = workspace.CreateTemplateService();

        // Step 10: Verify the complete asset passes validation
        var completeValidation = validationService.ValidateCompleteAsset(
            session, mainDestPath, finalProvPath, mainFilename, "2026-08-17", prompt, templateService);
        Assert.True(completeValidation.IsValid, string.Join(", ", completeValidation.Errors));
    }

    [Fact]
    public void E2E_FullWorkflow_WithReplaceReference_Then_Main()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        // Step 1: Initial reference
        var ref1 = workspace.CreateImage("ref_v1.png", new byte[] { 1 });
        var session = processor.ProcessReference(settings, "asset1", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var oldRefFilename = session.ReferenceFilename;
        var oldRefHash = session.ReferenceHash;

        // Step 2: Replace reference with a different image
        var ref2 = workspace.CreateImage("ref_v2.png", new byte[] { 2 });
        var transaction = processor.PrepareReferenceReplacement(
            session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Commit the replacement
        sessionService.Save(transaction.NewSession);
        var commit = processor.CommitReferenceReplacement(transaction);
        Assert.True(commit.IsValid);

        session = transaction.NewSession;
        Assert.NotEqual(oldRefHash, session.ReferenceHash);
        Assert.NotEqual(oldRefFilename, session.ReferenceFilename);

        // Step 3: Process main image
        var main = workspace.CreateImage("main.png", new byte[] { 3 });
        var mainFilename = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, main, "final version prompt", DateTimeOffset.Now);

        sessionService.Delete();

        // Step 4: Verify everything
        var mainDest = Path.Combine(session.AssetFolder, mainFilename);
        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        Assert.True(File.Exists(mainDest));
        Assert.True(File.Exists(finalProv));
        Assert.True(File.Exists(session.ReferenceDestinationPath));

        var finalText = File.ReadAllText(finalProv, Encoding.UTF8);
        Assert.Contains("ref_v2.png", finalText); // New reference filename
        Assert.Contains("final version prompt", finalText);
    }

    [Fact]
    public void E2E_ReferenceCancel_ThenNewReference_Succeeds()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        // Step 1: Create reference
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1 });
        var session1 = processor.ProcessReference(settings, "asset1", ref1, DateTimeOffset.Now);
        sessionService.Save(session1);

        // Step 2: Cancel it
        sessionService.Cancel(session1);
        Assert.False(sessionService.Exists());
        Assert.False(Directory.Exists(session1.AssetFolder)); // Was created by tool, should be removed

        // Step 3: Create a new reference with same asset folder name
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 2 });
        var session2 = processor.ProcessReference(settings, "asset1", ref2, DateTimeOffset.Now);
        sessionService.Save(session2);

        Assert.True(File.Exists(session2.ReferenceDestinationPath));
        Assert.True(sessionService.Exists());

        // Step 4: Complete it with main image
        var main = workspace.CreateImage("main.png", new byte[] { 3 });
        var mainFilename = processor.ProcessMainPrepared(
            session2, settings.AcceptedExtensions, main, "prompt text", DateTimeOffset.Now);
        sessionService.Delete();

        Assert.True(File.Exists(Path.Combine(session2.AssetFolder, mainFilename)));
    }

    // ──────────────────────────────────────────────────────
    //  SESSION RECOVERY END-TO-END
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_SessionRecovery_LoadAndComplete()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        // Simulate first "run": create reference, save session, then "crash"
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "recovered_asset", refSource, DateTimeOffset.Now);

        var sessionService1 = workspace.CreateSessionService();
        sessionService1.Save(session);

        // Simulate second "run": load session, validate, then complete
        var sessionService2 = workspace.CreateSessionService();
        Assert.True(sessionService2.Exists());

        var loaded = sessionService2.Load()!;
        var validationService = workspace.CreateValidationService();
        var validation = validationService.ValidateSession(loaded);
        Assert.True(validation.IsValid, string.Join(", ", validation.Errors));

        // Complete with main image using the recovered session
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var mainFilename = processor.ProcessMainPrepared(
            loaded, settings.AcceptedExtensions, mainSource, "recovered prompt", DateTimeOffset.Now);

        sessionService2.Delete();

        Assert.True(File.Exists(Path.Combine(loaded.AssetFolder, mainFilename)));
        Assert.False(sessionService2.Exists());
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Multiple consecutive operations
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_MultipleConsecutiveAssets_WorkCorrectly()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        for (int i = 0; i < 3; i++)
        {
            var refBytes = new byte[] { (byte)(i * 10 + 1), (byte)(i * 10 + 2) };
            var refSource = workspace.CreateImage($"ref_{i}.png", refBytes);
            var session = processor.ProcessReference(settings, $"asset_{i}", refSource, DateTimeOffset.Now);
            sessionService.Save(session);

            var mainBytes = new byte[] { (byte)(i * 10 + 5), (byte)(i * 10 + 6) };
            var mainSource = workspace.CreateImage($"main_{i}.png", mainBytes);
            var mainFilename = processor.ProcessMainPrepared(
                session, settings.AcceptedExtensions, mainSource, $"prompt for asset {i}", DateTimeOffset.Now);
            sessionService.Delete();

            // Verify asset is complete
            Assert.True(File.Exists(Path.Combine(session.AssetFolder, mainFilename)));
            Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
            Assert.False(sessionService.Exists());
        }
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: WEBP and JPEG extension support
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_WebpFiles_AreHandledCorrectly()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("reference.webp", new byte[] { 1, 2 });
        var session = processor.ProcessReference(settings, "webp_asset", refSource, DateTimeOffset.Now);

        Assert.Equal("reference.webp", session.ReferenceFilename);
        Assert.True(File.Exists(session.ReferenceDestinationPath));

        var mainSource = workspace.CreateImage("main.webp", new byte[] { 3, 4 });
        var mainFilename = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);

        Assert.Equal("main.webp", mainFilename);
        Assert.Equal("webp_asset.webp", session.GetIngameFilename());
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, "main.webp")));
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "webp_asset.webp")));
    }

    [Fact]
    public void E2E_JpegFiles_AreHandledCorrectly()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("reference.jpeg", new byte[] { 1, 2 });
        var session = processor.ProcessReference(settings, "jpeg_asset", refSource, DateTimeOffset.Now);

        Assert.Equal("reference.jpeg", session.ReferenceFilename);

        var mainSource = workspace.CreateImage("main.jpg", new byte[] { 3, 4 });
        var mainFilename = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);

        Assert.Equal("main.jpg", mainFilename);
        Assert.Equal("jpeg_asset.jpg", session.GetIngameFilename());
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, "main.jpg")));
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "jpeg_asset.jpg")));
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Very long prompt
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_VeryLongPrompt_IsPreserved()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1 });
        var session = processor.ProcessReference(settings, "long_prompt_asset", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 2 });

        // Build a very long prompt (5000+ chars)
        var longPrompt = string.Join("\n", Enumerable.Range(0, 500).Select(i =>
            $"Zeile {i}: bitte ändere den Kontrast und die Farben – spezifisch für Iteration #{i}"));

        var mainFilename = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, mainSource, longPrompt, DateTimeOffset.Now);

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var text = File.ReadAllText(finalProvPath, Encoding.UTF8);
        Assert.Contains(longPrompt, text);
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Asset folder name with dots
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_AssetFolderName_WithDots_WorksCorrectly()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        // "asset.v2.final" has dots but doesn't end with a dot
        var folderValidation = validationService.ValidateAssetFolderName("asset.v2.final");
        Assert.True(folderValidation.IsValid);

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1 });
        var session = processor.ProcessReference(settings, "asset.v2.final", refSource, DateTimeOffset.Now);

        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.Equal("asset.v2.final", session.AssetFolderName);
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Special characters in filenames
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_SpecialCharactersInImageFilename_WorkCorrectly()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        // ChatGPT-style filename with date, time, commas, and spaces
        var refSource = workspace.CreateImage(
            "ChatGPT Image Mar 24, 2026, 02_03_01 PM.png",
            new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset1", refSource, DateTimeOffset.Now);

        Assert.Equal("ChatGPT Image Mar 24, 2026, 02_03_01 PM.png", session.ReferenceFilename);
        Assert.True(File.Exists(session.ReferenceDestinationPath));

        // Provenance must contain the exact filename
        var provText = File.ReadAllText(session.ReferenceProvenancePath, Encoding.UTF8);
        Assert.Contains("ChatGPT Image Mar 24, 2026, 02_03_01 PM.png", provText);
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Prompt containing template tokens
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_PromptContainingTemplateTokens_DoesNotCauseRecursiveSubstitution()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1 });
        var session = processor.ProcessReference(settings, "token_asset", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 2 });

        // Adversarial prompt containing template tokens
        const string adversarialPrompt =
            "{{PROJECT}} should be replaced with {{FINAL_FILENAME}} and {{PROMPT}} recursion test";

        var mainFilename = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, mainSource, adversarialPrompt, DateTimeOffset.Now);

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var text = File.ReadAllText(finalProvPath, Encoding.UTF8);

        // The literal text of the adversarial prompt must be in the output
        Assert.Contains(adversarialPrompt, text);

        // Must also still contain the correct project name
        Assert.Contains($"Project: {session.ProjectName}", text);
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: ReplaceReference with same filename
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_ReplaceReference_WithSameFilename_WorksCorrectly()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 1 });
        var session = processor.ProcessReference(settings, "asset1", ref1, DateTimeOffset.Now);

        var oldHash = session.ReferenceHash;

        // Replace with a different file that has the same filename
        var ref2 = workspace.CreateImage("ref.png", new byte[] { 2 });
        var transaction = processor.PrepareReferenceReplacement(
            session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // New session should have different hash but same filename
        Assert.Equal("ref.png", transaction.NewSession.ReferenceFilename);
        Assert.NotEqual(oldHash, transaction.NewSession.ReferenceHash);

        processor.CommitReferenceReplacement(transaction);

        // Verify the new reference file has the new content
        var destBytes = File.ReadAllBytes(transaction.NewSession.ReferenceDestinationPath);
        Assert.Equal(File.ReadAllBytes(ref2), destBytes);
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: ReplaceReference rollback restores old data
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_ReplaceReference_Rollback_RestoresOriginalData()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1Bytes = new byte[] { 10, 20, 30 };
        var ref1 = workspace.CreateImage("ref1.png", ref1Bytes);
        var session = processor.ProcessReference(settings, "asset1", ref1, DateTimeOffset.Now);
        var originalHash = session.ReferenceHash;

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50 });
        var transaction = processor.PrepareReferenceReplacement(
            session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Rollback instead of commit
        var result = processor.RollbackReferenceReplacement(transaction);
        Assert.True(result.IsValid);

        // Old reference should be back
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.Equal(originalHash, processor.ComputeSha256(session.ReferenceDestinationPath));

        // New reference should be gone
        Assert.False(File.Exists(transaction.NewSession.ReferenceDestinationPath));
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Cancel with main image already completed
    //  (main artifacts survive cancel since cancel only
    //  removes reference, and main is already done)
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_SessionSaveAfterMainImage_SessionDeleted()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1 });
        var session = processor.ProcessReference(settings, "asset1", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 2 });
        var mainFilename = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);

        // Now delete session (simulating normal completion)
        sessionService.Delete();
        Assert.False(sessionService.Exists());

        // All asset files should still exist
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, mainFilename)));
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Settings with null extensions
    // ──────────────────────────────────────────────────────

    [Fact]
    public void SettingsService_HandlesNullExtensions()
    {
        using var workspace = new TestWorkspace();

        var service = new SettingsService(workspace.SettingsPath);

        // Write JSON with null AcceptedExtensions
        File.WriteAllText(workspace.SettingsPath,
            """{"ProjectName":"test","DownloadFolder":"x","AssetRootFolder":"y","AcceptedExtensions":null}""",
            Encoding.UTF8);

        var loaded = service.Load();
        Assert.NotNull(loaded.AcceptedExtensions);
        Assert.True(loaded.AcceptedExtensions.Count > 0);
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Settings with empty extensions list
    // ──────────────────────────────────────────────────────

    [Fact]
    public void SettingsService_HandlesEmptyExtensions()
    {
        using var workspace = new TestWorkspace();

        var service = new SettingsService(workspace.SettingsPath);

        // Write JSON with empty AcceptedExtensions
        File.WriteAllText(workspace.SettingsPath,
            """{"ProjectName":"test","DownloadFolder":"x","AssetRootFolder":"y","AcceptedExtensions":[]}""",
            Encoding.UTF8);

        var loaded = service.Load();
        Assert.NotNull(loaded.AcceptedExtensions);
        // Empty list after normalization — blueprint says use defaults when null
        // but empty list stays empty. Verify it doesn't throw at least.
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: ImageFinderService with multiple ChatGPT images
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ImageFinder_MultipleChatGptImages_SelectsNewest()
    {
        using var workspace = new TestWorkspace();

        var img1 = workspace.CreateImage("ChatGPT Image old.png");
        var img2 = workspace.CreateImage("ChatGPT Image new.png");
        var img3 = workspace.CreateImage("ChatGPT Image newest.png");

        File.SetLastWriteTimeUtc(img1, DateTime.UtcNow.AddMinutes(-30));
        File.SetLastWriteTimeUtc(img2, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(img3, DateTime.UtcNow);

        var service = new ImageFinderService();
        var result = service.FindLatestImage(workspace.CreateSettings());

        Assert.Equal(Path.GetFullPath(img3), Path.GetFullPath(result!));
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: ImageFinderService with identical timestamps
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ImageFinder_IdenticalTimestamps_FallsBackToFilename()
    {
        using var workspace = new TestWorkspace();

        var timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var imgA = workspace.CreateImage("aaa.png");
        var imgZ = workspace.CreateImage("zzz.png");

        File.SetLastWriteTimeUtc(imgA, timestamp);
        File.SetCreationTimeUtc(imgA, timestamp);
        File.SetLastWriteTimeUtc(imgZ, timestamp);
        File.SetCreationTimeUtc(imgZ, timestamp);

        var service = new ImageFinderService();
        var result = service.FindLatestImage(workspace.CreateSettings());

        // With ThenBy filename ascending, "aaa.png" should be selected
        Assert.Equal(Path.GetFullPath(imgA), Path.GetFullPath(result!));
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Validation of reserved names with extensions
    // ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("NUL")]
    [InlineData("NUL.txt")]
    [InlineData("PRN")]
    [InlineData("PRN.test")]
    [InlineData("AUX")]
    [InlineData("LPT1")]
    [InlineData("LPT1.png")]
    [InlineData("COM9")]
    [InlineData("CONOUT$")]
    public void Validation_ReservedNames_AreRejected(string name)
    {
        var service = new ValidationService();
        var result = service.ValidateAssetFolderName(name);
        Assert.False(result.IsValid);
    }

    // ──────────────────────────────────────────────────────
    //  EDGE CASE: Validation of valid special names
    // ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("my_con_folder")]
    [InlineData("containsCON")]
    [InlineData("CONNECTION")]
    [InlineData("ICON")]
    [InlineData("PRN_backup")]
    public void Validation_NamesContainingReservedSubstrings_AreAccepted(string name)
    {
        var service = new ValidationService();
        var result = service.ValidateAssetFolderName(name);
        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    // ──────────────────────────────────────────────────────
    //  BUG HUNT: RollbackMain then re-process main
    // ──────────────────────────────────────────────────────

    [Fact]
    public void E2E_RollbackMain_ThenReprocessMain_Succeeds()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1 });
        var session = processor.ProcessReference(settings, "asset1", refSource, DateTimeOffset.Now);

        // First main image
        var main1 = workspace.CreateImage("main1.png", new byte[] { 2 });
        var mainFilename1 = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, main1, "first prompt", DateTimeOffset.Now);

        // Rollback
        var rollback = processor.RollbackMain(session, mainFilename1);
        Assert.True(rollback.IsValid);

        // Second main image with different bytes
        var main2 = workspace.CreateImage("main2.png", new byte[] { 3 });
        var mainFilename2 = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, main2, "second prompt", DateTimeOffset.Now);

        // Verify only second main exists
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, mainFilename2)));
        Assert.Equal(processor.ComputeSha256(main2), processor.ComputeSha256(Path.Combine(session.AssetFolder, mainFilename2)));

        var finalProv = File.ReadAllText(
            Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName), Encoding.UTF8);
        Assert.Contains("second prompt", finalProv);
        Assert.DoesNotContain("first prompt", finalProv);
    }

    // ──────────────────────────────────────────────────────
    //  BUG HUNT: Hash comparison is case-insensitive
    // ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeSha256_ReturnsLowercaseHex()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var path = workspace.CreateImage("test.png", new byte[] { 1, 2, 3 });

        var hash = processor.ComputeSha256(path);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c)));
    }

    // ──────────────────────────────────────────────────────
    //  BUG HUNT: Template rendering idempotency
    // ──────────────────────────────────────────────────────

    [Fact]
    public void TemplateRendering_IsIdempotent()
    {
        using var workspace = new TestWorkspace();

        var service = workspace.CreateTemplateService();

        var render1 = service.RenderReference("file.png", "Project", "2026-01-01");
        var render2 = service.RenderReference("file.png", "Project", "2026-01-01");

        Assert.Equal(render1, render2);
    }

    // ──────────────────────────────────────────────────────
    //  BUG HUNT: Session with empty-string properties
    // ──────────────────────────────────────────────────────

    [Fact]
    public void SessionValidation_RejectsEmptySession()
    {
        var service = new ValidationService();
        var emptySession = new AssetSession();

        var result = service.ValidateSession(emptySession);
        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count > 0);
    }

    // ──────────────────────────────────────────────────────
    //  BUG HUNT: SettingsService Unicode round-trip
    // ──────────────────────────────────────────────────────

    [Fact]
    public void SettingsService_UnicodeRoundTrip()
    {
        using var workspace = new TestWorkspace();

        var service = new SettingsService(workspace.SettingsPath);

        var unicodeAssetPath = Path.Combine(workspace.Root, "Spëll Quäke 日本語 😀");
        var settings = new AppSettings
        {
            DownloadFolder = workspace.Downloads,
            AssetRootFolder = unicodeAssetPath,
            AcceptedExtensions = new List<string> { ".png" }
        };

        service.Save(settings);
        var loaded = service.Load();

        Assert.Equal(settings.AssetRootFolder, loaded.AssetRootFolder);
    }

    // ──────────────────────────────────────────────────────
    //  BUG HUNT: WriteTextAtomic cleans up temp on failure
    // ──────────────────────────────────────────────────────

    [Fact]
    public void WriteTextAtomic_CleansUpTempOnExistingTarget()
    {
        using var workspace = new TestWorkspace();

        var processor = workspace.CreateAssetProcessor();
        var targetPath = Path.Combine(workspace.Root, "existing.md");

        // Pre-create the target
        File.WriteAllText(targetPath, "existing content", Encoding.UTF8);

        // Attempt to write - should throw IOException
        Assert.Throws<IOException>(() =>
            processor.WriteTextAtomic(targetPath, "new content"));

        // Original content must be unchanged
        Assert.Equal("existing content", File.ReadAllText(targetPath, Encoding.UTF8));

        // No temp files should remain
        var tempFiles = Directory.GetFiles(workspace.Root, ".__write_*.tmp");
        Assert.Empty(tempFiles);
    }
}

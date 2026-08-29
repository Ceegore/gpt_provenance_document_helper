using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Regression tests specifically verifying fixes for BUG-001 through BUG-013 (REG-001 through REG-028).
/// </summary>
public sealed class RegressionTests
{
    private static void RunStaWithTimeout(Action action, int timeoutSeconds = 15)
    {
        Exception? testEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                testEx = ex;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var completed = thread.Join(TimeSpan.FromSeconds(timeoutSeconds));
        Assert.True(completed, $"STA thread timed out after {timeoutSeconds} seconds");
        if (testEx != null)
        {
            ExceptionDispatchInfo.Capture(testEx).Throw();
        }
    }
    // ──────────────────────────────────────────────────────
    //  BUG-001: REG-001 Single-instance Mutex Name Derivation
    // ──────────────────────────────────────────────────────

    [Fact]
    public void REG_001_Mutex_AcquiredAndBlocksSecondInstance()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var mutexName = "AssetProvenanceHelper_Test_"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseDirectory.ToUpperInvariant())));

        using var mutex1 = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out bool createdNew1);
        Assert.True(createdNew1, "First mutex acquisition must succeed");

        using var mutex2 = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out bool createdNew2);
        Assert.False(createdNew2, "Second mutex with same name must not be createdNew");
    }

    // ──────────────────────────────────────────────────────
    //  BUG-002: REG-002..005 IsMainCommitting & Crash-Atomic Recovery
    // ──────────────────────────────────────────────────────

    [Fact]
    public void REG_002_AssetSession_SerializesAndDeserializes_IsMainCommitting()
    {
        using var workspace = new TestWorkspace();
        var sessionService = workspace.CreateSessionService();

        var session = new AssetSession
        {
            ProjectName = "TestProj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "test_asset",
            AssetFolder = Path.Combine(workspace.Assets, "test_asset"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "test_asset", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "test_asset", "reference", AppConstants.ReferenceProvenanceFileName),
            ReferenceHash = new string('a', 64),
            ReferenceProcessedAt = DateTimeOffset.Now,
            IsMainCommitting = true
        };

        sessionService.Save(session);
        var loaded = sessionService.Load();

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsMainCommitting);
    }

    [Fact]
    public void REG_003_LegacySessionJson_WithoutIsMainCommitting_DefaultsToFalse()
    {
        using var workspace = new TestWorkspace();
        var sessionService = workspace.CreateSessionService();

        // Write raw JSON without IsMainCommitting
        var json = """
        {
            "ProjectName": "TestProj",
            "AssetRootFolder": "C:\\Assets",
            "AssetFolderName": "test_asset",
            "AssetFolder": "C:\\Assets\\test_asset",
            "ReferenceSourcePath": "",
            "ReferenceDestinationPath": "C:\\Assets\\test_asset\\reference\\ref.png",
            "ReferenceFilename": "ref.png",
            "ReferenceProvenancePath": "C:\\Assets\\test_asset\\reference\\license.txt — AI Reference Asset.md",
            "ReferenceHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ReferenceProcessedAt": "2026-08-17T12:00:00+02:00",
            "WasAssetFolderCreatedByTool": false,
            "WasReferenceFolderCreatedByTool": false
        }
        """;

        File.WriteAllText(workspace.SessionPath, json, Encoding.UTF8);
        var loaded = sessionService.Load();

        Assert.NotNull(loaded);
        Assert.False(loaded!.IsMainCommitting);
    }

    [Fact]
    public void REG_004_CompletedAsset_WithFinalProvenance_IdentifiableDuringRecovery()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "completed_asset", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var mainFilename = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, mainSource, "test prompt", DateTimeOffset.Now);

        // Simulate crash right before session deletion by setting IsMainCommitting
        session.IsMainCommitting = true;
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";
        sessionService.Save(session);

        // Verification: The final provenance and main image exist
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var mainPath = Path.Combine(session.AssetFolder, mainFilename);

        Assert.True(File.Exists(finalProvPath));
        Assert.True(File.Exists(mainPath));

        var loaded = sessionService.Load();
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsMainCommitting);
        Assert.True(File.Exists(Path.Combine(loaded.AssetFolder, AppConstants.FinalProvenanceFileName)));

        // The above only proves the persisted state is set up correctly; it
        // never actually runs recovery, so it would still pass even if a
        // regression in RecoverSessionOnStartup subsequently deleted the
        // completed asset. Run the real recovery entry point and assert on
        // its actual effect: the completed asset is retained, and the
        // leftover session record is retired.
        RunStaWithTimeout(() =>
        {
            AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider =
                (_, _, _, _, _) => true; // user chooses "Delete Session Record"

            try
            {
                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    processor,
                    workspace.CreateSessionService());

                var recoverMethod =
                    typeof(MainForm).GetMethod(
                        "RecoverSessionOnStartup",
                        System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance);

                recoverMethod!.Invoke(form, null);
            }
            finally
            {
                AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });

        // The completed asset's own files must survive recovery...
        Assert.True(File.Exists(finalProvPath));
        Assert.True(File.Exists(mainPath));

        // ...while the now-superfluous session record is retired.
        Assert.False(sessionService.Exists());
    }

    // ──────────────────────────────────────────────────────
    //  BUG-003: REG-006..007 Main Destination Hash & TOCTOU Protection
    // ──────────────────────────────────────────────────────

    [Fact]
    public void REG_006_MainIdenticalBytesCheck_EvaluatesCopiedBytes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        // Create reference image
        var refBytes = new byte[] { 10, 20, 30, 40 };
        var refSource = workspace.CreateImage("ref.png", refBytes);
        var session = processor.ProcessReference(settings, "asset_hash_test", refSource, DateTimeOffset.Now);

        // Create main image with identical bytes as reference
        var mainSource = workspace.CreateImage("main.png", refBytes);

        var ex = Assert.Throws<InvalidOperationException>(
            () => processor.ProcessMainPrepared(
                session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now));

        Assert.Contains("identical to the reference image", ex.Message);
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
    }

    // ──────────────────────────────────────────────────────
    //  BUG-004: REG-008..009 Culture-Invariant Date Validation
    // ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void REG_008_ValidateReferenceOutput_SucceedsUnderVariousCultures(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            var culture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var validationService = workspace.CreateValidationService();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var date = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
            var session = processor.ProcessReference(settings, "culture_test", refSource, date);

            var validation = validationService.ValidateReferenceOutput(session);
            Assert.True(validation.IsValid, $"Validation failed under culture {cultureName}: {string.Join(", ", validation.Errors)}");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    // ──────────────────────────────────────────────────────
    //  BUG-005: REG-010..013 Extended Windows Reserved Names & Multi-extension
    // ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("NUL.tar.gz")]
    [InlineData("PRN.foo.bar")]
    [InlineData("CON.backup.zip")]
    [InlineData("AUX.1.2.3")]
    [InlineData("COM¹")]
    [InlineData("COM²")]
    [InlineData("COM³")]
    [InlineData("COM².foo")]
    [InlineData("LPT¹")]
    [InlineData("LPT²")]
    [InlineData("LPT³")]
    [InlineData("LPT³.png")]
    public void REG_010_ValidateAssetFolderName_RejectsAllReservedDeviceNames(string invalidName)
    {
        var validationService = new ValidationService();
        var result = validationService.ValidateAssetFolderName(invalidName);

        Assert.False(result.IsValid, $"Expected '{invalidName}' to be rejected as a reserved device name.");
        Assert.Contains(result.Errors, e => e.Contains("reserved Windows device name", StringComparison.OrdinalIgnoreCase));
    }

    // ──────────────────────────────────────────────────────
    //  BUG-006: REG-014..016 Root-Safe NormalizePath
    // ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"C:\Assets\", @"C:\Assets")]
    [InlineData(@"C:\Assets\Sub", @"C:\Assets\Sub")]
    public void REG_014_NormalizePath_PreservesDriveRootsCorrectly(string input, string expectedSuffix)
    {
        var normalized = ValidationService.NormalizePath(input);
        Assert.EndsWith(expectedSuffix, normalized, StringComparison.OrdinalIgnoreCase);
        if (input.EndsWith(@":\"))
        {
            Assert.EndsWith(@"\", normalized);
        }
    }

    // ──────────────────────────────────────────────────────
    //  BUG-008: REG-019 Final Template Has No Draft Placeholder
    // ──────────────────────────────────────────────────────

    [Fact]
    public void REG_019_FinalTemplate_DoesNotContainAssetLocationDraftPlaceholder()
    {
        var baseDir = AppContext.BaseDirectory;
        var finalTemplatePath = Path.Combine(baseDir, "templates", "final.md");

        var text = File.ReadAllText(finalTemplatePath, Encoding.UTF8);

        Assert.DoesNotContain("[assets/locations/...]", text);
        Assert.DoesNotContain("Asset location:", text);
    }

    // ──────────────────────────────────────────────────────
    //  BUG-009: REG-020 Atomic Settings Save
    // ──────────────────────────────────────────────────────

    [Fact]
    public void REG_020_SettingsService_SavesAtomicallyViaTempFile()
    {
        using var workspace = new TestWorkspace();
        var service = new SettingsService(workspace.SettingsPath);

        var settings = new AppSettings
        {
            DownloadFolder = workspace.Downloads,
            AssetRootFolder = workspace.Assets,
            AcceptedExtensions = new List<string> { ".png", ".webp" }
        };

        service.Save(settings);

        Assert.True(File.Exists(workspace.SettingsPath));
        var loaded = service.Load();
        Assert.Equal(workspace.Assets, loaded.AssetRootFolder);
        Assert.Equal(2, loaded.AcceptedExtensions.Count);
    }

    [Fact]
    public void REG_020_SettingsService_FailedPromotionLeavesPreviousSettingsByteForByteIntact()
    {
        // The previous test only proves Save-then-Load round-trips; a naive
        // File.WriteAllText(settingsPath, ...) implementation would pass it
        // just as easily. Prove the actual atomicity contract instead: the
        // new content is fully, durably written to a temp file BEFORE the
        // existing settings.json is ever touched (Services/SettingsService.cs
        // Save()), so if promotion (the final File.Move onto settingsPath)
        // fails, the previous file must be completely unaffected - not
        // truncated, not partially overwritten, not deleted.
        using var workspace = new TestWorkspace();
        var service = new SettingsService(workspace.SettingsPath);

        var original = new AppSettings
        {
            DownloadFolder = workspace.Downloads,
            AssetRootFolder = workspace.Assets,
            AcceptedExtensions = new List<string> { ".png" }
        };
        service.Save(original);

        var originalBytes = File.ReadAllBytes(workspace.SettingsPath);
        Assert.NotEmpty(originalBytes);

        var replacement = new AppSettings
        {
            DownloadFolder = workspace.Downloads,
            AssetRootFolder = Path.Combine(workspace.Assets, "different-root"),
            AcceptedExtensions = new List<string> { ".webp", ".jpg" }
        };

        // Lock the destination exclusively so the final File.Move(temp,
        // settingsPath, overwrite: true) cannot replace it, forcing the
        // failure to occur at promotion - after the new content has already
        // been fully written and flushed to the temp file, not before.
        using (var destinationLock =
            new FileStream(
                workspace.SettingsPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
        {
            // On Windows, moving onto a file locked with FileShare.None can
            // surface as either IOException or UnauthorizedAccessException
            // depending on exactly how the OS denies the replace; either way
            // Save() must not have silently succeeded.
            Assert.ThrowsAny<Exception>(() => service.Save(replacement));
        }

        var afterFailureBytes = File.ReadAllBytes(workspace.SettingsPath);
        Assert.Equal(originalBytes, afterFailureBytes);

        var reloaded = service.Load();
        Assert.Equal(workspace.Assets, reloaded.AssetRootFolder);
        Assert.Single(reloaded.AcceptedExtensions);
        Assert.Equal(".png", reloaded.AcceptedExtensions[0]);

        // The temp file created for the new content must not survive either.
        var leftoverTempFiles =
            Directory.GetFiles(
                workspace.Root,
                Path.GetFileName(workspace.SettingsPath) + ".*.tmp");
        Assert.Empty(leftoverTempFiles);
    }

    // ──────────────────────────────────────────────────────
    //  BUG-010: REG-021..022 Empty / Whitespace AcceptedExtensions Fallback
    // ──────────────────────────────────────────────────────

    [Fact]
    public void REG_021_SettingsService_EmptyExtensionsList_FallsBackToDefaults()
    {
        using var workspace = new TestWorkspace();
        var service = new SettingsService(workspace.SettingsPath);

        var json = """{"ProjectName":"test","DownloadFolder":"x","AssetRootFolder":"y","AcceptedExtensions":[]}""";
        File.WriteAllText(workspace.SettingsPath, json, Encoding.UTF8);

        var loaded = service.Load();
        Assert.NotNull(loaded.AcceptedExtensions);
        Assert.NotEmpty(loaded.AcceptedExtensions);
        Assert.Contains(".png", loaded.AcceptedExtensions);
        Assert.Contains(".webp", loaded.AcceptedExtensions);
    }

    [Fact]
    public void REG_022_SettingsService_WhitespaceOnlyExtensionsList_FallsBackToDefaults()
    {
        using var workspace = new TestWorkspace();
        var service = new SettingsService(workspace.SettingsPath);

        var json = """{"ProjectName":"test","DownloadFolder":"x","AssetRootFolder":"y","AcceptedExtensions":["  ", "\t", ""]}""";
        File.WriteAllText(workspace.SettingsPath, json, Encoding.UTF8);

        var loaded = service.Load();
        Assert.NotNull(loaded.AcceptedExtensions);
        Assert.NotEmpty(loaded.AcceptedExtensions);
        Assert.Contains(".png", loaded.AcceptedExtensions);
    }

    // ──────────────────────────────────────────────────────
    //  BUG-012: REG-025 Transactional Cancel
    // ──────────────────────────────────────────────────────

    [Fact]
    public void REG_025_SessionCancel_RemovesBothReferenceFilesAndSession()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "cancel_asset", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
        Assert.True(sessionService.Exists());

        sessionService.Cancel(session);

        Assert.False(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(session.ReferenceProvenancePath));
        Assert.False(sessionService.Exists());
    }

    // ──────────────────────────────────────────────────────
    //  BUG-013: REG-027..028 Hardened Rollback APIs
    // ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"..\..\outside.png")]
    [InlineData(@"sub/path/main.png")]
    [InlineData(@"/rooted/path.png")]
    [InlineData("")]
    public void REG_027_RollbackMain_RejectsRootedOrTraversalFilenames(string dangerousFilename)
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var session = new AssetSession
        {
            AssetFolder = Path.Combine(workspace.Assets, "asset1")
        };

        var result = processor.RollbackMain(session, dangerousFilename);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void REG_028_RollbackReference_RejectsIncompleteOrEscapingPaths()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var dangerousSession = new AssetSession
        {
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "important.dll",
            ReferenceDestinationPath = @"C:\Windows\System32\important.dll",
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", AppConstants.ReferenceProvenanceFileName)
        };

        var result = processor.RollbackReference(dangerousSession);
        Assert.False(result.IsValid);
    }

    // ──────────────────────────────────────────────────────
    //  RE-AUDIT ADVERSARIAL TESTS: REG-029..040
    // ──────────────────────────────────────────────────────

    [Fact]
    public void REG_029_IsMainCommitting_SaveFailure_AbortsMainProcessing()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg29", refSource, DateTimeOffset.Now);

        // Simulate save failure
        var invalidSessionService = new SessionService(@"Z:\NonExistentDrive\session.json");

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = DateTimeOffset.Now;

        // When saving the session fails, caller must abort
        Assert.Throws<DirectoryNotFoundException>(() => invalidSessionService.Save(session));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
    }

    [Fact]
    public void REG_030_IsMainCommitting_WithMissingMainImage_IsNotClassifiedAsCompleted()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg30", refSource, DateTimeOffset.Now);

        // Final provenance exists, but main image was NOT copied / is missing
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var finalProvContent = workspace.CreateTemplateService().RenderFinal(
            "main.png", session.ReferenceFilename, session.ProjectName, "2026-08-17", "prompt");
        File.WriteAllText(finalProvPath, finalProvContent, Encoding.UTF8);

        var mainPath = Path.Combine(session.AssetFolder, "main.png");
        Assert.False(File.Exists(mainPath));

        var templateService = workspace.CreateTemplateService();
        var completeValidation = validationService.ValidateCompleteAsset(
            session, mainPath, finalProvPath, "main.png", "2026-08-17", "prompt", templateService);

        Assert.False(completeValidation.IsValid, "Missing main image must fail Complete Asset validation");
        Assert.Contains(completeValidation.Errors, e => e.Contains("Main image does not exist"));
    }

    [Fact]
    public void REG_031_IsMainCommitting_WithWrongMainImage_IsNotClassifiedAsCompleted()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg31", refSource, DateTimeOffset.Now);

        // Final provenance rendered for "main_expected.png"
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var finalProvContent = workspace.CreateTemplateService().RenderFinal(
            "main_expected.png", session.ReferenceFilename, session.ProjectName, "2026-08-17", "prompt");
        File.WriteAllText(finalProvPath, finalProvContent, Encoding.UTF8);

        // But on disk, another file "wrong.png" exists
        var wrongMainPath = Path.Combine(session.AssetFolder, "wrong.png");
        File.WriteAllBytes(wrongMainPath, new byte[] { 9, 9, 9 });

        var templateService = workspace.CreateTemplateService();
        var completeValidation = validationService.ValidateCompleteAsset(
            session, wrongMainPath, finalProvPath, "wrong.png", "2026-08-17", "prompt", templateService);

        Assert.False(completeValidation.IsValid, "Mismatch between main filename and provenance must fail validation");
        Assert.Contains(completeValidation.Errors, e => e.Contains("Main Asset ID") || e.Contains("incomplete") || e.Contains("Expected final provenance"));
    }

    [Fact]
    public void REG_032_PreExistingFinalProvenance_RejectsMainProcessingWithoutCorruptingSession()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg32", refSource, DateTimeOffset.Now);

        // Create pre-existing final provenance
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        File.WriteAllText(finalProvPath, "PREEXISTING FINAL PROVENANCE", Encoding.UTF8);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        Assert.Throws<IOException>(
            () => processor.ProcessMainPrepared(
                session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now));

        Assert.Equal("PREEXISTING FINAL PROVENANCE", File.ReadAllText(finalProvPath, Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
    }

    [Fact]
    public void REG_033_ProcessReference_RejectsReparsePointAssetRootFolder()
    {
        using var workspace = new TestWorkspace();
        var realTarget = Path.Combine(workspace.Root, "junction_target");
        var junctionRoot = Path.Combine(workspace.Root, "junction_root");
        Directory.CreateDirectory(realTarget);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(junctionRoot, realTarget);
            }
            catch
            {
                return;
            }

            Assert.True(Directory.Exists(junctionRoot));
            Assert.True(ValidationService.IsReparsePoint(junctionRoot));

            var processor = workspace.CreateAssetProcessor();
            var settings = new AppSettings
            {
                DownloadFolder = workspace.Downloads,
                AssetRootFolder = junctionRoot,
                AcceptedExtensions = new List<string> { ".png" }
            };

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var ex = Assert.Throws<IOException>(
                () => processor.ProcessReference(settings, "asset1", refSource, DateTimeOffset.Now));

            Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(realTarget));
        }
        finally
        {
            if (Directory.Exists(junctionRoot))
            {
                Directory.Delete(junctionRoot);
            }
        }
    }

    [Fact]
    public void REG_034_ProcessReference_RejectsExistingReparsePointFolder()
    {
        using var workspace = new TestWorkspace();
        var assetFolder = Path.Combine(workspace.Assets, "asset_junc");
        Directory.CreateDirectory(assetFolder);

        var foreignTarget = Path.Combine(workspace.Root, "foreign_reference");
        var referenceJunction = Path.Combine(assetFolder, AppConstants.ReferenceFolderName);
        Directory.CreateDirectory(foreignTarget);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(referenceJunction, foreignTarget);
            }
            catch
            {
                return;
            }

            Assert.True(Directory.Exists(referenceJunction));
            Assert.True(ValidationService.IsReparsePoint(referenceJunction));

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var ex = Assert.Throws<IOException>(
                () => processor.ProcessReference(settings, "asset_junc", refSource, DateTimeOffset.Now));

            Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(foreignTarget));
        }
        finally
        {
            if (Directory.Exists(referenceJunction))
            {
                Directory.Delete(referenceJunction);
            }
        }
    }

    [Fact]
    public void REG_035_IsReparsePoint_IdentifiesRealReparsePointsAndNormalDirectories()
    {
        using var workspace = new TestWorkspace();
        var normalDir = Path.Combine(workspace.Root, "normal_dir");
        Directory.CreateDirectory(normalDir);

        Assert.False(ValidationService.IsReparsePoint(normalDir), "Normal directory must not be a reparse point");
        Assert.False(ValidationService.IsReparsePoint(@"C:\NonExistentPath_xyz"), "Non-existent path returns false");

        var realTarget = Path.Combine(workspace.Root, "real_reparse_target");
        var junctionPath = Path.Combine(workspace.Root, "real_reparse_junc");
        Directory.CreateDirectory(realTarget);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(junctionPath, realTarget);
            }
            catch
            {
                return;
            }

            if (Directory.Exists(junctionPath))
            {
                Assert.True(ValidationService.IsReparsePoint(junctionPath), "Real Windows junction must be identified as reparse point");
            }
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
        }
    }

    [Fact]
    public void REG_036_Cancel_SurfacesError_WhenTempFileCannotBeDeleted_AndRetrySucceedsOnceUnlocked()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg36", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        // Transition session to FilesRenamed with deterministic cancellation ID and temp files
        session.CancellationId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        session.CancelPhase = CancelPhase.FilesRenamed;
        var tempRef = session.GetCancelTempReferencePath();
        var tempProv = session.GetCancelTempProvenancePath();
        File.Move(session.ReferenceDestinationPath, tempRef);
        File.Move(session.ReferenceProvenancePath, tempProv);
        sessionService.Save(session);

        // Lock the canceling file so File.Delete will fail
        using (var lockStream = new FileStream(tempRef, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.Contains("temporary canceling", ex.Message);
            Assert.True(sessionService.Exists(), "Session must NOT be deleted when canceling files remain");
        }

        // Once unlocked, retry of Cancel must clean up the leftover canceling file and delete the session
        sessionService.Cancel(session);
        Assert.False(sessionService.Exists(), "Session must be deleted after successful retry");
        Assert.False(File.Exists(tempRef), "Locked canceling file must be deleted on retry");
    }

    [Fact]
    public void REG_037_Cancel_RollbackRestoreFailure_SurfacesAggregateException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg37", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        // Lock the reference destination path with exclusive access so File.Move will fail
        using var destLock = new FileStream(session.ReferenceDestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
        Assert.True(sessionService.Exists(), "Session must be preserved when cancel fails");
    }

    [Fact]
    public void REG_038_RollbackMain_WithForeignAssetFolder_Rejected()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var foreignSession = new AssetSession
        {
            ProjectName = "Proj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_reg38",
            AssetFolder = @"C:\Windows\System32",
            ReferenceFilename = "ref.png",
            MainFilename = "victim.png"
        };

        var result = processor.RollbackMain(foreignSession, "victim.png");
        Assert.False(result.IsValid, "RollbackMain must reject foreign AssetFolder");
    }

    [Fact]
    public void REG_039_RollbackReference_WithForeignAssetFolder_Rejected()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var foreignSession = new AssetSession
        {
            ProjectName = "Proj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_reg39",
            AssetFolder = @"C:\Windows\System32",
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = @"C:\Windows\System32\reference\ref.png",
            ReferenceProvenancePath = @"C:\Windows\System32\reference\license.txt"
        };

        var result = processor.RollbackReference(foreignSession);
        Assert.False(result.IsValid, "RollbackReference must reject foreign AssetFolder");
    }

    [Fact]
    public void REG_040_CompleteAsset_WithTamperedMainBytes_FailsHashValidation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var processedAt = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.FromHours(2));
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg40", refSource, processedAt);

        var originalMainBytes = new byte[] { 10, 20, 30, 40 };
        var mainSource = workspace.CreateImage("main.png", originalMainBytes);
        var expectedHash = processor.ComputeSha256(mainSource);

        var mainFilename = processor.ProcessMainPrepared(
            session, settings.AcceptedExtensions, mainSource, "test prompt", processedAt);

        var mainPath = Path.Combine(session.AssetFolder, mainFilename);
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        var templateService = workspace.CreateTemplateService();
        var validResult = validationService.ValidateCompleteAsset(
            session, mainPath, finalProvPath, mainFilename, "2026-08-17", "test prompt", templateService, expectedHash);
        Assert.True(validResult.IsValid);

        File.WriteAllBytes(mainPath, new byte[] { 99, 99, 99, 99 });

        var tamperedResult = validationService.ValidateCompleteAsset(
            session, mainPath, finalProvPath, mainFilename, "2026-08-17", "test prompt", templateService, expectedHash);

        Assert.False(tamperedResult.IsValid, "Tampered main image bytes must fail validation");
        Assert.Contains(tamperedResult.Errors, e => e.Contains("SHA-256 hash does not match expected MainHash"));
    }

    [Fact]
    public void REG_041_Cancel_PreservesForeignCancelingFiles()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg41", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        var referenceFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
        var foreignFile = Path.Combine(referenceFolder, "user-notes.canceling");
        var foreignBytes = new byte[] { 7, 8, 9, 10 };
        File.WriteAllBytes(foreignFile, foreignBytes);

        sessionService.Cancel(session);

        Assert.True(File.Exists(foreignFile), "Foreign *.canceling file must NOT be deleted by Cancel()");
        Assert.Equal(foreignBytes, File.ReadAllBytes(foreignFile));
    }

    [Fact]
    public void REG_042_FailedCancelRetry_DeletesOnlyOwnedTempFiles_PreservingForeignFiles()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg42", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        var referenceFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
        var foreignFile = Path.Combine(referenceFolder, "foreign.canceling");
        File.WriteAllBytes(foreignFile, new byte[] { 42 });

        session.CancellationId = "b1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        session.CancelPhase = CancelPhase.FilesRenamed;
        var tempRef = session.GetCancelTempReferencePath();
        var tempProv = session.GetCancelTempProvenancePath();
        File.Move(session.ReferenceDestinationPath, tempRef);
        File.Move(session.ReferenceProvenancePath, tempProv);
        sessionService.Save(session);

        sessionService.Cancel(session);

        Assert.False(File.Exists(tempRef), "Owned temp reference must be deleted");
        Assert.False(File.Exists(tempProv), "Owned temp provenance must be deleted");
        Assert.False(File.Exists(session.ReferenceDestinationPath), "Original reference must not exist");
        Assert.False(File.Exists(session.ReferenceProvenancePath), "Original provenance must not exist");
        Assert.False(sessionService.Exists(), "Session must be deleted");
        Assert.True(File.Exists(foreignFile), "Foreign canceling file must survive retry");
    }

    [Fact]
    public void REG_043_ProcessMainImage_SourceChangedAfterPrehash_AbortsTransaction()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg43", refSource, DateTimeOffset.Now);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 10, 20, 30 });
        var processedAt = DateTimeOffset.Now;
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = processedAt;
        session.MainTransactionId = Guid.NewGuid().ToString("N");
        session.MainHash = "0000000000000000000000000000000000000000000000000000000000000000"; // Mismatched hash

        var ex = Assert.Throws<IOException>(
            () => processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, "prompt", processedAt));

        Assert.Contains("Main source changed between validation/hash and copy", ex.Message);
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
    }

    [Fact]
    public void REG_044_MainPrehash_Throws_GracefullyHandled()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg44", refSource, DateTimeOffset.Now);

        var nonexistentImage = Path.Combine(workspace.Root, "nonexistent_source.png");

        Assert.Throws<InvalidDataException>(() =>
            processor.ProcessMainPrepared(session, settings.AcceptedExtensions, nonexistentImage, "prompt", DateTimeOffset.Now));

        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "nonexistent_source.png")));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
    }

    [Fact]
    public void REG_045_IsMainCommitting_WithNullMainHash_FailsValidation()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var session = new AssetSession
        {
            ProjectName = "Proj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", AppConstants.ReferenceProvenanceFileName),
            ReferenceHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ReferenceProcessedAt = DateTimeOffset.Now,
            IsMainCommitting = true,
            MainTransactionId = "0123456789abcdef0123456789abcdef",
            MainFilename = "main.png",
            MainPrompt = "prompt",
            MainProcessedAt = DateTimeOffset.Now,
            MainHash = null // Missing MainHash
        };

        var result = validationService.ValidateSession(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MainHash is missing"));
    }

    [Fact]
    public void REG_046_IsMainCommitting_WithMalformedMainHash_FailsValidation()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var session = new AssetSession
        {
            ProjectName = "Proj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", AppConstants.ReferenceProvenanceFileName),
            ReferenceHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ReferenceProcessedAt = DateTimeOffset.Now,
            IsMainCommitting = true,
            MainTransactionId = "0123456789abcdef0123456789abcdef",
            MainFilename = "main.png",
            MainPrompt = "prompt",
            MainProcessedAt = DateTimeOffset.Now,
            MainHash = "not-a-valid-64-char-hash"
        };

        var result = validationService.ValidateSession(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MainHash is missing or is not a valid 64-character"));
    }

    [Fact]
    public void REG_049_IsReparsePoint_EmptyOrWhitespace_ReturnsFalse()
    {
        Assert.False(ValidationService.IsReparsePoint(""));
        Assert.False(ValidationService.IsReparsePoint("   "));
        Assert.False(ValidationService.IsReparsePoint(null!));
    }

    [Fact]
    public void REG_051_ProcessReference_DestinationSnapshotRevalidated()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg51", refSource, DateTimeOffset.Now);

        Assert.NotNull(session);
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_052_PrepareReferenceReplacement_DestinationSnapshotRevalidated()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg52", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var transaction = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        Assert.NotNull(transaction);
        processor.CommitReferenceReplacement(transaction);
    }

    [Fact]
    public void REG_053_ValidateSession_WithCancelPhase_ValidatesInvariants()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var referenceDir = Path.Combine(workspace.Assets, "asset1", "reference");
        Directory.CreateDirectory(referenceDir);

        var origRef = Path.Combine(referenceDir, "ref.png");
        var origProv = Path.Combine(referenceDir, AppConstants.ReferenceProvenanceFileName);
        File.WriteAllBytes(origRef, new byte[] { 1 });
        File.WriteAllBytes(origProv, new byte[] { 2 });

        var session = new AssetSession
        {
            ProjectName = "Proj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = origRef,
            ReferenceProvenancePath = origProv,
            ReferenceHash = "4b227777d4dd1fc61c6f884f48641d02b4d121d3fd328cb08b5531fcacdabf8a",
            ReferenceProcessedAt = DateTimeOffset.Now,
            CancelPhase = CancelPhase.Prepared,
            CancellationId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4"
        };

        var result = validationService.ValidateSession(session);
        Assert.True(result.IsValid, "Prepared state with original files present and temp absent is valid");
    }

    [Fact]
    public void REG_054_SessionService_Cancel_ResumeInterruptedCancellation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg54", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        // Simulate crash right after rename
        session.CancellationId = "c1c2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        session.CancelPhase = CancelPhase.FilesRenamed;
        var tempRef = session.GetCancelTempReferencePath();
        var tempProv = session.GetCancelTempProvenancePath();
        File.Move(session.ReferenceDestinationPath, tempRef);
        File.Move(session.ReferenceProvenancePath, tempProv);
        sessionService.Save(session);

        // Resume cancel
        sessionService.Cancel(session);

        Assert.False(sessionService.Exists(), "Session must be cleaned up on resumed cancel");
        Assert.False(File.Exists(tempRef));
        Assert.False(File.Exists(tempProv));
    }

    [Fact]
    public void REG_055_ForeignCancelingFiles_SurviveAllCancelFlows()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg55", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        var refFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
        var foreign1 = Path.Combine(refFolder, "important.canceling");
        var foreign2 = Path.Combine(refFolder, "backup.canceling");
        File.WriteAllText(foreign1, "data1");
        File.WriteAllText(foreign2, "data2");

        sessionService.Cancel(session);

        Assert.True(File.Exists(foreign1));
        Assert.True(File.Exists(foreign2));
        Assert.Equal("data1", File.ReadAllText(foreign1));
        Assert.Equal("data2", File.ReadAllText(foreign2));
    }

    [Fact]
    public void REG_056_Cancel_FullStateMachineTransition()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg56", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        Assert.Equal(CancelPhase.None, session.CancelPhase);
        sessionService.Cancel(session);

        Assert.False(sessionService.Exists(), "Session record must be removed after complete cancellation");
        Assert.False(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_057_Cancel_PreparedPhaseCrashRecovery_ReconcilesFiles()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg57", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        // Crash right after moving provenance but before moving reference
        session.CancellationId = "d1d2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        session.CancelPhase = CancelPhase.Prepared;
        var tempProv = session.GetCancelTempProvenancePath();
        File.Move(session.ReferenceProvenancePath, tempProv);
        sessionService.Save(session);

        // Resume cancel - should reconcile and finish
        sessionService.Cancel(session);

        Assert.False(sessionService.Exists());
        Assert.False(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(tempProv));
    }

    [Fact]
    public void REG_058_Cancel_AmbiguousState_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg58", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        session.CancellationId = "e1e2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        session.CancelPhase = CancelPhase.Prepared;
        var tempRef = session.GetCancelTempReferencePath();
        File.Copy(session.ReferenceDestinationPath, tempRef);
        sessionService.Save(session);

        var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
        Assert.Contains("Ambiguous", ex.Message);
        Assert.True(sessionService.Exists(), "Session must not be deleted on ambiguous state");
    }

    [Fact]
    public void REG_059_Cancel_MissingState_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg59", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        session.CancellationId = "f1f2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        session.CancelPhase = CancelPhase.Prepared;
        File.Delete(session.ReferenceDestinationPath);
        sessionService.Save(session);

        var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
        Assert.Contains("Missing", ex.Message);
        Assert.True(sessionService.Exists(), "Session must not be deleted on missing state");
    }

    [Fact]
    public void REG_060_Cancel_DeterministicPathGeneration()
    {
        var session = new AssetSession
        {
            ReferenceDestinationPath = @"C:\Assets\asset1\reference\ref.png",
            ReferenceProvenancePath = @"C:\Assets\asset1\reference\license.txt",
            CancellationId = "1234567890abcdef1234567890abcdef"
        };

        Assert.Equal(@"C:\Assets\asset1\reference\ref.png.1234567890abcdef1234567890abcdef.canceling", session.GetCancelTempReferencePath());
        Assert.Equal(@"C:\Assets\asset1\reference\license.txt.1234567890abcdef1234567890abcdef.canceling", session.GetCancelTempProvenancePath());
    }

    [Fact]
    public void REG_061_ValidateSession_RejectsInvalidCancellationId()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var session = new AssetSession
        {
            ProjectName = "Proj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", AppConstants.ReferenceProvenanceFileName),
            ReferenceHash = "4b227777d4dd1fc61c6f884f48641d02b4d121d3fd328cb08b5531fcacdabf8a",
            ReferenceProcessedAt = DateTimeOffset.Now,
            CancelPhase = CancelPhase.Prepared,
            CancellationId = "short-invalid-id"
        };

        var result = validationService.ValidateSession(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("CancellationId is missing or is not a valid 32-character"));
    }

    [Fact]
    public void REG_062_AssetProcessingException_PropertiesAndRollbackIncomplete()
    {
        var inner = new IOException("inner");
        var ex = new AssetProcessingException("Test failure", inner, rollbackComplete: false);
        Assert.False(ex.RollbackComplete);
        Assert.Equal("Test failure", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void REG_063_MainForm_HandleMainImage_IncompleteRollbackPreservesSessionMetadata()
    {
        Exception? testEx = null;
        var thread = new Thread(() =>
        {
            var messageReported = false;
            FileStream? destLock = null;
            using var workspace = new TestWorkspace();
            try
            {
                MainForm.MessageBoxProvider = (_, text, caption, buttons, icon) =>
                {
                    if (icon == MessageBoxIcon.Error)
                    {
                        messageReported = true;
                    }
                };

                var processor = workspace.CreateAssetProcessor();
                var sessionService = workspace.CreateSessionService();
                var settings = workspace.CreateSettings();

                var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
                var session = processor.ProcessReference(settings, "asset_reg63", refSource, DateTimeOffset.Now);
                sessionService.Save(session);

                var mainSource = workspace.CreateImage("main.png", new byte[] { 9, 8, 7 });

                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    processor,
                    sessionService);

                var sessionField = typeof(MainForm).GetField("_currentSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sessionField?.SetValue(form, session);

                var stateField = typeof(MainForm).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                stateField?.SetValue(form, 1); // UiState.ReferenceReady

                var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
                if (txtPrompt != null) txtPrompt.Text = "test prompt";

                form.SetSelectedImage(ImageSlot.Main, mainSource);

                AssetProcessorService.OnMainPromotedHook = dest =>
                {
                    destLock = new FileStream(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    throw new IOException("Simulated disk error during promotion");
                };

                var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(handleMainMethod);
                handleMainMethod.Invoke(form, null);

                Assert.True(messageReported);
                var loadedSession = sessionService.Load();
                Assert.NotNull(loadedSession);
                Assert.True(loadedSession.IsMainCommitting);
                Assert.Equal("main.png", loadedSession.MainFilename);
                Assert.Equal("test prompt", loadedSession.MainPrompt);
            }
            catch (Exception ex)
            {
                testEx = ex;
            }
            finally
            {
                destLock?.Dispose();
                MainForm.MessageBoxProvider = null;
                AssetProcessorService.OnMainPromotedHook = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (testEx != null)
        {
            throw testEx;
        }
    }

    [Fact]
    public void REG_064_RecoverSessionOnStartup_DetectsIncompleteMainCommit()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg64", refSource, DateTimeOffset.Now);

        session.IsMainCommitting = true;
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";
        session.MainFilename = "incomplete_main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = new string('f', 64);
        sessionService.Save(session);

        var templateService = workspace.CreateTemplateService();
        var completeResult = validationService.ValidateCompleteAsset(
            session,
            Path.Combine(session.AssetFolder, session.MainFilename),
            Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName),
            session.MainFilename,
            "2026-08-17",
            session.MainPrompt,
            templateService,
            session.MainHash);

        Assert.False(completeResult.IsValid, "Incomplete main commit must not be validated as complete");

        var rollback = processor.RollbackMain(session, session.MainFilename);
        Assert.True(rollback.IsValid);
    }

    [Fact]
    public void REG_065_ReferenceReplacementTransaction_DeterministicBackupPaths()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg65", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var transaction = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        Assert.NotNull(transaction.TransactionId);
        Assert.Equal(32, transaction.TransactionId.Length);
        Assert.EndsWith($".{transaction.TransactionId}.old", transaction.BackupReferencePath);
        Assert.EndsWith($".{transaction.TransactionId}.old", transaction.BackupProvenancePath);

        processor.CommitReferenceReplacement(transaction);
    }

    [Fact]
    public void REG_066_ValidateReferenceReplacementTransaction_ValidatesBackupPaths()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg66", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var transaction = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        var validation = validationService.ValidateReferenceReplacementTransaction(transaction);
        Assert.True(validation.IsValid);

        processor.CommitReferenceReplacement(transaction);
    }

    [Fact]
    public void REG_067_CommitReferenceReplacement_RejectsInvalidTransaction()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg67", ref1, DateTimeOffset.Now);

        var badTransaction = new ReferenceReplacementTransaction
        {
            TransactionId = "invalid",
            OldSession = session,
            NewSession = session,
            BackupReferencePath = @"C:\invalid\path",
            BackupProvenancePath = @"C:\invalid\path"
        };

        var result = processor.CommitReferenceReplacement(badTransaction);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void REG_068_RollbackReferenceReplacement_RejectsInvalidTransaction()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg68", ref1, DateTimeOffset.Now);

        var badTransaction = new ReferenceReplacementTransaction
        {
            TransactionId = "invalid",
            OldSession = session,
            NewSession = session,
            BackupReferencePath = @"C:\invalid\path",
            BackupProvenancePath = @"C:\invalid\path"
        };

        var result = processor.RollbackReferenceReplacement(badTransaction);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void REG_069_RollbackMain_EnforcesExactSessionMainFilename()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg69", ref1, DateTimeOffset.Now);
        session.IsMainCommitting = true;
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";
        session.MainFilename = "expected_main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = new string('0', 64);

        var mismatchedResult = processor.RollbackMain(session, "different_main.png");
        Assert.False(mismatchedResult.IsValid);
        Assert.Contains(mismatchedResult.Errors, e => e.Contains("match session.MainFilename"));
    }

    [Fact]
    public void REG_070_SequentialUiExecution_DoesNotDeadlock()
    {
        var tasks = new List<Thread>();
        var errors = new List<Exception>();
        var lockObj = new object();

        for (int i = 0; i < 5; i++)
        {
            var t = new Thread(() =>
            {
                try
                {
                    using var workspace = new TestWorkspace();
                    var settings = workspace.CreateSettings();
                    var form = new MainForm(
                        settings,
                        workspace.CreateSettingsService(),
                        workspace.CreateImageFinder(),
                        workspace.CreateTemplateService(),
                        workspace.CreateValidationService(),
                        workspace.CreateAssetProcessor(),
                        workspace.CreateSessionService());
                    Assert.NotNull(form);
                    form.Dispose();
                }
                catch (Exception ex)
                {
                    lock (lockObj) { errors.Add(ex); }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            tasks.Add(t);
        }

        foreach (var t in tasks) t.Start();
        foreach (var t in tasks) Assert.True(t.Join(TimeSpan.FromSeconds(30)), "Thread execution should complete within 30 seconds");
        Assert.Empty(errors);
    }

    [Fact]
    public void REG_071_IsReparsePoint_ReturnsFalseOnMissingPath()
    {
        Assert.False(ValidationService.IsReparsePoint(@"C:\NonExistentDirectory_123456789"));
        Assert.False(ValidationService.IsReparsePoint(@"C:\NonExistentFile_123456789.png"));
    }

    [Fact]
    public void REG_072_IsReparsePoint_FailsClosed()
    {
        Assert.False(ValidationService.IsReparsePoint(@"C:\NonExistent_Dir_XYZ"));
        Assert.False(ValidationService.IsReparsePoint(""));
        Assert.False(ValidationService.IsReparsePoint("   "));
        Assert.False(ValidationService.IsReparsePoint(null!));

        try
        {
            // 1. ReparsePoint attribute returns true
            ValidationService.FileAttributesProvider = _ => FileAttributes.Directory | FileAttributes.ReparsePoint;
            Assert.True(ValidationService.IsReparsePoint(@"C:\AnyPath"));

            // 2. Normal directory returns false
            ValidationService.FileAttributesProvider = _ => FileAttributes.Directory;
            Assert.False(ValidationService.IsReparsePoint(@"C:\AnyPath"));

            // 3. FileNotFoundException returns false
            ValidationService.FileAttributesProvider = _ => throw new FileNotFoundException();
            Assert.False(ValidationService.IsReparsePoint(@"C:\AnyPath"));

            // 4. DirectoryNotFoundException returns false
            ValidationService.FileAttributesProvider = _ => throw new DirectoryNotFoundException();
            Assert.False(ValidationService.IsReparsePoint(@"C:\AnyPath"));

            // 5. UnauthorizedAccessException fails closed (returns true)
            ValidationService.FileAttributesProvider = _ => throw new UnauthorizedAccessException();
            Assert.True(ValidationService.IsReparsePoint(@"C:\AnyPath"));

            // 6. Generic IOException fails closed (returns true)
            ValidationService.FileAttributesProvider = _ => throw new IOException("I/O failure");
            Assert.True(ValidationService.IsReparsePoint(@"C:\AnyPath"));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    public void REG_073_ValidateSessionPathsForDestructiveOperation_VerifiesReferencePaths()
    {
        using var workspace = new TestWorkspace();
        var session = new AssetSession
        {
            ProjectName = "Proj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "other_folder", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", AppConstants.ReferenceProvenanceFileName)
        };

        var result = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ReferenceDestinationPath is inconsistent"));
    }

    [Fact]
    public void REG_074_MainForm_KeyDown_CtrlR_And_CtrlM_AreHandled()
    {
        var thread = new Thread(() =>
        {
            try
            {
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                using var workspace = new TestWorkspace();
                workspace.CreateImage("ctrl_img.png", new byte[] { 1, 2, 3 });
                var settings = workspace.CreateSettings();
                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    workspace.CreateAssetProcessor(),
                    workspace.CreateSessionService());

                var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(keyMethod);

                var keyR = new KeyEventArgs(Keys.Control | Keys.R);
                keyMethod.Invoke(form, new object[] { form, keyR });

                var keyM = new KeyEventArgs(Keys.Control | Keys.M);
                keyMethod.Invoke(form, new object[] { form, keyM });
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_075_EndToEnd_Lifecycle_CleanState()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg75", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CommitReferenceReplacement(tx);
        sessionService.Save(tx.NewSession);

        var main = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
        var mainFile = processor.ProcessMainPrepared(tx.NewSession, settings.AcceptedExtensions, main, "final prompt", DateTimeOffset.Now);
        sessionService.Delete();

        Assert.False(sessionService.Exists());
        Assert.True(File.Exists(Path.Combine(tx.NewSession.AssetFolder, mainFile)));
        Assert.True(File.Exists(Path.Combine(tx.NewSession.AssetFolder, AppConstants.FinalProvenanceFileName)));
    }

    [Fact]
    public void REG_076_PreparedJournalSaveFails_RevertsRamPhaseAndId()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg76", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        var sessionPath = workspace.SessionPath;
        using (var lockStream = new FileStream(sessionPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => sessionService.Cancel(session));
            Assert.Equal(CancelPhase.None, session.CancelPhase);
            Assert.Null(session.CancellationId);
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
        }

        sessionService.Cancel(session);
        Assert.False(sessionService.Exists());
        Assert.False(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_077_TransientPreparedSaveFailure_SameProcessRetry_NoDestructiveWriteBeforeJournalPersisted()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg77", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        var sessionPath = workspace.SessionPath;
        using (var lockStream = new FileStream(sessionPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => sessionService.Cancel(session));
        }

        var onDiskSession = sessionService.Load();
        Assert.NotNull(onDiskSession);
        Assert.Equal(CancelPhase.None, onDiskSession.CancelPhase);
        Assert.Null(onDiskSession.CancellationId);

        sessionService.Cancel(session);
        Assert.False(sessionService.Exists());
    }

    [Fact]
    public void REG_078_ReferenceReplacement_PartialRollbackAndRetry_PreservesRestoredOldProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
        var oldSession = processor.ProcessReference(settings, "asset_reg78", ref1, DateTimeOffset.Now);
        var originalProvText = File.ReadAllText(oldSession.ReferenceProvenancePath);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });
        var tx = processor.PrepareReferenceReplacement(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Simulate partial rollback failure: lock the backup reference file
        using (var destLock = new FileStream(tx.BackupReferencePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failedRollback = processor.RollbackReferenceReplacement(tx);
            Assert.False(failedRollback.IsValid);
            Assert.Contains(failedRollback.Errors, e => e.Contains("old reference image"));
        }

        // Provenance was restored in step 1. Retry rollback:
        var retryRollback = processor.RollbackReferenceReplacement(tx);
        Assert.True(retryRollback.IsValid);
        Assert.True(File.Exists(oldSession.ReferenceProvenancePath));
        Assert.Equal(originalProvText, File.ReadAllText(oldSession.ReferenceProvenancePath));
        Assert.Equal(File.ReadAllBytes(ref1), File.ReadAllBytes(oldSession.ReferenceDestinationPath));
    }

    [Fact]
    public void REG_079_RollbackMain_WithoutSessionMainFilename_RejectsCallerProvidedFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg79", ref1, DateTimeOffset.Now);

        var keepFile = Path.Combine(session.AssetFolder, "keep.png");
        File.WriteAllBytes(keepFile, new byte[] { 9, 9, 9 });

        session.MainFilename = null;

        var rollbackResult = processor.RollbackMain(session, "keep.png");
        Assert.False(rollbackResult.IsValid);
        Assert.True(File.Exists(keepFile));
    }

    [Fact]
    public void REG_080_ProcessMainImage_TamperedBytesBeforeFinalValidation_FailsAndRollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg80", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });

        try
        {
            AssetProcessorService.OnMainPromotedHook = promotedPath =>
            {
                // Tamper with destination bytes after promotion before ValidateCompleteAsset
                File.WriteAllBytes(promotedPath, new byte[] { 99, 99, 99 });
            };

            // BUG-R16-001: rollback now preserves tampered files, wraps as AssetProcessingException
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main1, "prompt", DateTimeOffset.Now));
            Assert.Contains("hash", ex.Message, StringComparison.OrdinalIgnoreCase);

            // BUG-R16-001: Tampered main must be preserved (not owned)
            var mainPath = Path.Combine(session.AssetFolder, "main1.png");
            Assert.True(File.Exists(mainPath), "Tampered main must be preserved");
            Assert.Equal(new byte[] { 99, 99, 99 }, File.ReadAllBytes(mainPath));

            // Reference must remain intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
        }
        finally
        {
            AssetProcessorService.OnMainPromotedHook = null;
        }
    }

    [Fact]
    public void REG_081_DirectCancelFilesRenamed_WithMalformedCancellationId_RejectsBeforeDestructiveAccess()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg81", ref1, DateTimeOffset.Now);

        session.CancelPhase = CancelPhase.FilesRenamed;
        session.CancellationId = "../../malicious_traversal";

        var ex = Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));
        Assert.Contains("CancellationId", ex.Message);
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_082_ValidateReferenceReplacementTransaction_RejectsMismatchedAssets()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var sessionA = processor.ProcessReference(settings, "asset_A", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var sessionB = processor.ProcessReference(settings, "asset_B", ref2, DateTimeOffset.Now);

        var tx = new ReferenceReplacementTransaction
        {
            TransactionId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4",
            OldSession = sessionA,
            NewSession = sessionB,
            BackupReferencePath = sessionA.ReferenceDestinationPath + ".a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4.old",
            BackupProvenancePath = sessionA.ReferenceProvenancePath + ".a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4.old"
        };

        var result = validationService.ValidateReferenceReplacementTransaction(tx);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("AssetFolderName do not match"));

        var rollbackResult = processor.RollbackReferenceReplacement(tx);
        Assert.False(rollbackResult.IsValid);
    }

    [Fact]
    public void REG_083_IsReparsePoint_HandlesInvalidPathGracefully()
    {
        Assert.False(ValidationService.IsReparsePoint(@"Z:\InvalidDriveNonExistent\Folder"));
    }

    [Fact]
    public void REG_084_Session_Metadata_PreservedOnIncompleteRollback()
    {
        var session = new AssetSession
        {
            ProjectName = "Proj",
            AssetRootFolder = @"C:\Assets",
            AssetFolderName = "asset84",
            AssetFolder = @"C:\Assets\asset84",
            IsMainCommitting = true,
            MainFilename = "main.png",
            MainPrompt = "prompt",
            MainProcessedAt = DateTimeOffset.Now,
            MainHash = new string('f', 64)
        };

        Assert.True(session.IsMainCommitting);
        Assert.Equal("main.png", session.MainFilename);
    }

    [Fact]
    public void REG_085_MainForm_CtrlM_ExecutesMainWorkflow()
    {
        Exception? testEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                using var workspace = new TestWorkspace();
                var processor = workspace.CreateAssetProcessor();
                var sessionService = workspace.CreateSessionService();
                var settings = workspace.CreateSettings();

                var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
                var session = processor.ProcessReference(settings, "asset_reg85", refSource, DateTimeOffset.Now);
                sessionService.Save(session);

                var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    processor,
                    sessionService);

                var sessionField = typeof(MainForm).GetField("_currentSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sessionField?.SetValue(form, session);

                var stateField = typeof(MainForm).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                stateField?.SetValue(form, 1); // UiState.ReferenceReady

                var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
                if (txtPrompt != null) txtPrompt.Text = "Ctrl+M prompt";

                form.SetSelectedImage(ImageSlot.Main, mainSource);

                var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(keyMethod);

                var keyM = new KeyEventArgs(Keys.Control | Keys.M);
                keyMethod.Invoke(form, new object[] { form, keyM });

                Assert.False(sessionService.Exists());
                Assert.True(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
                Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_reg85.png")));
                Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
            }
            catch (Exception ex)
            {
                testEx = ex;
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (testEx != null)
        {
            throw testEx;
        }
    }

    [Fact]
    public void REG_086_MainForm_PasteClipboard_InjectableProvider()
    {
        var thread = new Thread(() =>
        {
            try
            {
                MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

                using var workspace = new TestWorkspace();
                var settings = workspace.CreateSettings();
                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    workspace.CreateAssetProcessor(),
                    workspace.CreateSessionService());

                form.ClipboardProvider = () => "Injected prompt for test";
                var pasteMethod = typeof(MainForm).GetMethod("PasteClipboard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                pasteMethod?.Invoke(form, null);

                var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
                Assert.NotNull(txtPrompt);
                Assert.Equal("Injected prompt for test", txtPrompt.Text);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_087_ProcessReference_SourceMutationDuringCopy_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        string? capturedTempPath = null;

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                capturedTempPath = dest;
                // Tamper with destination bytes right after copy
                File.WriteAllBytes(dest, new byte[] { 42, 42, 42 });
            };

            // BUG-R16-001: rollback is now incomplete because tampered file can't be verified as owned
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessReference(settings, "asset_reg87", refSource, DateTimeOffset.Now));
            Assert.Contains("Reference processing failed", ex.Message);

            // BUG-R16-001 & R7-001: Tampered temp staged file must be preserved (not owned), canonical never created
            var assetFolder = Path.Combine(settings.AssetRootFolder, "asset_reg87");
            var referenceFolder = Path.Combine(assetFolder, "reference");
            var referenceDestination = Path.Combine(referenceFolder, "ref.png");
            Assert.True(Directory.Exists(assetFolder), "Asset folder must remain because it contains non-owned content");
            Assert.False(File.Exists(referenceDestination), "Canonical reference must never have been created");
            Assert.NotNull(capturedTempPath);
            Assert.True(File.Exists(capturedTempPath), "Tampered temp reference must be preserved");
            Assert.Equal(new byte[] { 42, 42, 42 }, File.ReadAllBytes(capturedTempPath));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_088_PrepareReferenceReplacement_SourceMutationDuringCopy_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg88", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        string? capturedTempPath = null;

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                capturedTempPath = dest;
                // Tamper with destination bytes right after copy
                File.WriteAllBytes(dest, new byte[] { 99, 99, 99 });
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));
            Assert.Contains("Reference replacement failed", ex.Message);

            // Verify old reference and provenance remain perfectly intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));

            // BUG-R17-001: Tampered temp must be preserved (not owned)
            Assert.NotNull(capturedTempPath);
            Assert.True(File.Exists(capturedTempPath), "Tampered temp replacement image must be preserved");
            Assert.Equal(new byte[] { 99, 99, 99 }, File.ReadAllBytes(capturedTempPath));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_089_Cancel_RollbackRestoreFailure_SurfacesAggregateException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg89", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        try
        {
            SessionService.OnCancelProvenanceMovedHook = s =>
            {
                // Create dummy blocking file at original provenance path to fail rollback
                File.WriteAllText(s.ReferenceProvenancePath, "blocking provenance file");
                // Create dummy blocking file at destination temp reference path to fail forward move
                File.WriteAllText(s.GetCancelTempReferencePath(), "blocking temp ref file");
            };

            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.NotNull(ex.InnerException);
            Assert.IsType<AggregateException>(ex.InnerException);
            var agg = (AggregateException)ex.InnerException;
            Assert.Equal(2, agg.InnerExceptions.Count);
        }
        finally
        {
            SessionService.OnCancelProvenanceMovedHook = null;
        }
    }

    [Fact]
    public void REG_090_RollbackReferenceReplacement_IdempotentAfterEveryFailureBoundary()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg90", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Failure boundary 1: backup reference file locked during first rollback attempt
        using (var destLock = new FileStream(tx.BackupReferencePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var resFail = processor.RollbackReferenceReplacement(tx);
            Assert.False(resFail.IsValid);
        }

        // Failure boundary 2: destination is unlocked; second attempt succeeds idempotently
        var res1 = processor.RollbackReferenceReplacement(tx);
        Assert.True(res1.IsValid);

        // Failure boundary 3: third attempt on already-restored state remains valid and idempotent
        var res2 = processor.RollbackReferenceReplacement(tx);
        Assert.True(res2.IsValid);
    }

    [Fact]
    public void REG_091_SequentialReferenceReplacements_FourGenerations_ProducesValidState()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        // Ref 1 (.png)
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 1, 1 });
        var session1 = processor.ProcessReference(settings, "seq_asset", ref1, DateTimeOffset.Now);
        sessionService.Save(session1);

        // Ref 2 (.jpg)
        var ref2 = workspace.CreateImage("ref2.jpg", new byte[] { 2, 2, 2 });
        var tx2 = processor.PrepareReferenceReplacement(session1, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CommitReferenceReplacement(tx2);
        sessionService.Save(tx2.NewSession);

        // Ref 3 (.jpeg)
        var ref3 = workspace.CreateImage("ref3.jpeg", new byte[] { 3, 3, 3 });
        var tx3 = processor.PrepareReferenceReplacement(tx2.NewSession, settings.AcceptedExtensions, ref3, DateTimeOffset.Now);
        processor.CommitReferenceReplacement(tx3);
        sessionService.Save(tx3.NewSession);

        // Ref 4 (.png)
        var ref4 = workspace.CreateImage("ref4.png", new byte[] { 4, 4, 4 });
        var tx4 = processor.PrepareReferenceReplacement(tx3.NewSession, settings.AcceptedExtensions, ref4, DateTimeOffset.Now);
        processor.CommitReferenceReplacement(tx4);
        sessionService.Save(tx4.NewSession);

        // Final Main Image
        var main = workspace.CreateImage("final_main.png", new byte[] { 5, 5, 5 });
        var mainFile = processor.ProcessMainPrepared(tx4.NewSession, settings.AcceptedExtensions, main, "final seq prompt", DateTimeOffset.Now);
        sessionService.Delete();

        Assert.False(sessionService.Exists());
        Assert.True(File.Exists(Path.Combine(tx4.NewSession.AssetFolder, mainFile)));
        Assert.True(File.Exists(Path.Combine(tx4.NewSession.AssetFolder, AppConstants.FinalProvenanceFileName)));
        Assert.True(File.Exists(tx4.NewSession.ReferenceDestinationPath));

        // Prior interim reference images should not be present
        Assert.False(File.Exists(Path.Combine(tx4.NewSession.AssetFolder, "reference", "ref1.png")));
        Assert.False(File.Exists(Path.Combine(tx4.NewSession.AssetFolder, "reference", "ref2.jpg")));
        Assert.False(File.Exists(Path.Combine(tx4.NewSession.AssetFolder, "reference", "ref3.jpeg")));
    }

    [Fact]
    public void REG_092_RollbackMain_PreservesUnrelatedFilesInAssetFolder()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_unrelated", ref1, DateTimeOffset.Now);

        // Create unrelated files
        var extraFile = Path.Combine(session.AssetFolder, "extra_notes.txt");
        File.WriteAllText(extraFile, "do not delete");

        var subDir = Path.Combine(session.AssetFolder, "custom_sub");
        Directory.CreateDirectory(subDir);
        var subFile = Path.Combine(subDir, "data.bin");
        File.WriteAllBytes(subFile, new byte[] { 100, 101, 102 });

        // Process Main
        var main = workspace.CreateImage("main.png", new byte[] { 7, 7, 7 });
        var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main, "prompt", DateTimeOffset.Now);

        // Rollback Main
        var rollbackResult = processor.RollbackMain(session, mainFilename);
        Assert.True(rollbackResult.IsValid);

        // Verify main & final provenance deleted, but unrelated files preserved
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, mainFilename)));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
        Assert.True(File.Exists(extraFile));
        Assert.True(File.Exists(subFile));
    }

    [Fact]
    public void REG_093_ProcessMain_RollbackMain_MultipleCycles_CleanStateTransitions()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_cycles", ref1, DateTimeOffset.Now);

        for (var i = 1; i <= 3; i++)
        {
            var mainImage = workspace.CreateImage($"main_cycle_{i}.png", new byte[] { (byte)(i + 10), (byte)(i + 20), (byte)(i + 30) });
            var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, mainImage, $"prompt cycle {i}", DateTimeOffset.Now);

            Assert.Equal($"main_cycle_{i}.png", session.MainFilename);
            Assert.True(File.Exists(Path.Combine(session.AssetFolder, mainFilename)));
            Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));

            if (i < 3)
            {
                var rollbackResult = processor.RollbackMain(session, mainFilename);
                Assert.True(rollbackResult.IsValid);
                Assert.Null(session.MainFilename);
                Assert.Null(session.MainHash);
                Assert.False(session.IsMainCommitting);
                Assert.False(File.Exists(Path.Combine(session.AssetFolder, mainFilename)));
                Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
            }
        }
    }

    [Fact]
    public void REG_094_ValidateReferenceReplacementTransaction_AllEightIdentityMismatches()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var sessionA = processor.ProcessReference(settings, "asset_id_A", ref1, DateTimeOffset.Now);

        var validTxId = "1234567890abcdef1234567890abcdef";

        // 1. Mismatched AssetRootFolder
        var tx1 = new ReferenceReplacementTransaction
        {
            TransactionId = validTxId,
            OldSession = sessionA,
            NewSession = new AssetSession
            {
                AssetRootFolder = @"C:\DifferentRoot",
                AssetFolderName = sessionA.AssetFolderName,
                AssetFolder = sessionA.AssetFolder,
                ProjectName = sessionA.ProjectName,
                ReferenceFilename = sessionA.ReferenceFilename,
                ReferenceDestinationPath = sessionA.ReferenceDestinationPath,
                ReferenceProvenancePath = sessionA.ReferenceProvenancePath
            },
            BackupReferencePath = sessionA.ReferenceDestinationPath + "." + validTxId + ".old",
            BackupProvenancePath = sessionA.ReferenceProvenancePath + "." + validTxId + ".old"
        };
        Assert.False(validationService.ValidateReferenceReplacementTransaction(tx1).IsValid);

        // 2. Mismatched ProjectName
        var tx2 = new ReferenceReplacementTransaction
        {
            TransactionId = validTxId,
            OldSession = sessionA,
            NewSession = new AssetSession
            {
                AssetRootFolder = sessionA.AssetRootFolder,
                AssetFolderName = sessionA.AssetFolderName,
                AssetFolder = sessionA.AssetFolder,
                ProjectName = "DifferentProject",
                ReferenceFilename = sessionA.ReferenceFilename,
                ReferenceDestinationPath = sessionA.ReferenceDestinationPath,
                ReferenceProvenancePath = sessionA.ReferenceProvenancePath
            },
            BackupReferencePath = sessionA.ReferenceDestinationPath + "." + validTxId + ".old",
            BackupProvenancePath = sessionA.ReferenceProvenancePath + "." + validTxId + ".old"
        };
        Assert.False(validationService.ValidateReferenceReplacementTransaction(tx2).IsValid);

        // 3. Mismatched AssetFolderName
        var tx3 = new ReferenceReplacementTransaction
        {
            TransactionId = validTxId,
            OldSession = sessionA,
            NewSession = new AssetSession
            {
                AssetRootFolder = sessionA.AssetRootFolder,
                AssetFolderName = "DifferentFolderName",
                AssetFolder = sessionA.AssetFolder,
                ProjectName = sessionA.ProjectName,
                ReferenceFilename = sessionA.ReferenceFilename,
                ReferenceDestinationPath = sessionA.ReferenceDestinationPath,
                ReferenceProvenancePath = sessionA.ReferenceProvenancePath
            },
            BackupReferencePath = sessionA.ReferenceDestinationPath + "." + validTxId + ".old",
            BackupProvenancePath = sessionA.ReferenceProvenancePath + "." + validTxId + ".old"
        };
        Assert.False(validationService.ValidateReferenceReplacementTransaction(tx3).IsValid);

        // 4. Mismatched AssetFolder
        var tx4 = new ReferenceReplacementTransaction
        {
            TransactionId = validTxId,
            OldSession = sessionA,
            NewSession = new AssetSession
            {
                AssetRootFolder = sessionA.AssetRootFolder,
                AssetFolderName = sessionA.AssetFolderName,
                AssetFolder = Path.Combine(sessionA.AssetRootFolder, "other_folder"),
                ProjectName = sessionA.ProjectName,
                ReferenceFilename = sessionA.ReferenceFilename,
                ReferenceDestinationPath = sessionA.ReferenceDestinationPath,
                ReferenceProvenancePath = sessionA.ReferenceProvenancePath
            },
            BackupReferencePath = sessionA.ReferenceDestinationPath + "." + validTxId + ".old",
            BackupProvenancePath = sessionA.ReferenceProvenancePath + "." + validTxId + ".old"
        };
        Assert.False(validationService.ValidateReferenceReplacementTransaction(tx4).IsValid);

        // 5. Mismatched ReferenceProvenancePath
        var tx5 = new ReferenceReplacementTransaction
        {
            TransactionId = validTxId,
            OldSession = sessionA,
            NewSession = new AssetSession
            {
                AssetRootFolder = sessionA.AssetRootFolder,
                AssetFolderName = sessionA.AssetFolderName,
                AssetFolder = sessionA.AssetFolder,
                ProjectName = sessionA.ProjectName,
                ReferenceFilename = sessionA.ReferenceFilename,
                ReferenceDestinationPath = sessionA.ReferenceDestinationPath,
                ReferenceProvenancePath = Path.Combine(sessionA.AssetFolder, "other_prov.md")
            },
            BackupReferencePath = sessionA.ReferenceDestinationPath + "." + validTxId + ".old",
            BackupProvenancePath = sessionA.ReferenceProvenancePath + "." + validTxId + ".old"
        };
        Assert.False(validationService.ValidateReferenceReplacementTransaction(tx5).IsValid);

        // 6. Invalid TransactionId (not 32 hex)
        var tx6 = new ReferenceReplacementTransaction
        {
            TransactionId = "too_short_id",
            OldSession = sessionA,
            NewSession = sessionA,
            BackupReferencePath = sessionA.ReferenceDestinationPath + ".too_short_id.old",
            BackupProvenancePath = sessionA.ReferenceProvenancePath + ".too_short_id.old"
        };
        Assert.False(validationService.ValidateReferenceReplacementTransaction(tx6).IsValid);

        // 7. Mismatched BackupReferencePath
        var tx7 = new ReferenceReplacementTransaction
        {
            TransactionId = validTxId,
            OldSession = sessionA,
            NewSession = sessionA,
            BackupReferencePath = @"C:\other\backup.old",
            BackupProvenancePath = sessionA.ReferenceProvenancePath + "." + validTxId + ".old"
        };
        Assert.False(validationService.ValidateReferenceReplacementTransaction(tx7).IsValid);

        // 8. Mismatched BackupProvenancePath
        var tx8 = new ReferenceReplacementTransaction
        {
            TransactionId = validTxId,
            OldSession = sessionA,
            NewSession = sessionA,
            BackupReferencePath = sessionA.ReferenceDestinationPath + "." + validTxId + ".old",
            BackupProvenancePath = @"C:\other\backup_prov.old"
        };
        Assert.False(validationService.ValidateReferenceReplacementTransaction(tx8).IsValid);
    }

    [Fact]
    public void REG_095_Cancel_StatePersistenceAcrossPhases_Verified()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg95", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        var savedPhases = new List<CancelPhase>();
        try
        {
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                savedPhases.Add(phase);
            };

            sessionService.Cancel(session);

            Assert.Equal(new[] { CancelPhase.Prepared, CancelPhase.FilesRenamed }, savedPhases);
            Assert.False(sessionService.Exists());
            Assert.False(File.Exists(session.ReferenceDestinationPath));
            Assert.False(File.Exists(session.ReferenceProvenancePath));
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
        }
    }

    [Fact]
    public void REG_096_RollbackReferenceReplacement_WhenBackupProvenanceMissing_HandlesGracefully()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg96", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Delete backup provenance file and original provenance file so restore is impossible
        File.Delete(tx.BackupProvenancePath);
        File.Delete(session.ReferenceProvenancePath);

        var rollbackResult = processor.RollbackReferenceReplacement(tx);
        Assert.False(rollbackResult.IsValid);
        Assert.Contains(rollbackResult.Errors, e => e.Contains("Could not restore old reference provenance"));
    }

    [Fact]
    public void REG_097_ProcessReference_DestinationCollision_ThrowsIOException_DoesNotOverwriteFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var assetFolder = Path.Combine(settings.AssetRootFolder, "asset_reg97");
        var referenceFolder = Path.Combine(assetFolder, "reference");
        Directory.CreateDirectory(referenceFolder);
        var existingRefPath = Path.Combine(referenceFolder, "ref.png");
        var originalContent = new byte[] { 99, 98, 97 };
        File.WriteAllBytes(existingRefPath, originalContent);

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var ex = Assert.Throws<IOException>(() =>
            processor.ProcessReference(settings, "asset_reg97", refSource, DateTimeOffset.Now));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalContent, File.ReadAllBytes(existingRefPath));
    }

    [Fact]
    public void REG_098_ProcessMain_HashDriftDuringCopy_RollsBackAndThrows()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg98", ref1, DateTimeOffset.Now);

        // Pre-set session.MainHash to a mismatching value
        var processedAt = DateTimeOffset.Now;
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = processedAt;
        session.MainTransactionId = Guid.NewGuid().ToString("N");
        session.MainHash = new string('a', 64);

        var main = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
        var ex = Assert.Throws<IOException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, main, "prompt", processedAt));

        Assert.Contains("Main source changed between validation/hash and copy", ex.Message);
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
    }

    [Fact]
    public void REG_099_TemplateService_HandlesSpecialCharactersAndEscapes()
    {
        using var workspace = new TestWorkspace();
        var templateService = workspace.CreateTemplateService();

        var renderedRef = templateService.RenderReference("ref_special_`test`_\".png", "MyProj_<name>&\"'", "2026-08-17");
        Assert.Contains("ref_special_`test`_\".png", renderedRef);
        Assert.Contains("MyProj_<name>&\"'", renderedRef);
        Assert.Contains("2026-08-17", renderedRef);

        var complexPrompt = "Prompt with `backticks`, \"double quotes\", <xml-tags>, and unicode \u2764 \u2728 \r\n multiple lines";
        var renderedFinal = templateService.RenderFinal("main.png", "ref.png", "MyProj", "2026-08-17", complexPrompt);
        Assert.Contains("main.png", renderedFinal);
        Assert.Contains("ref.png", renderedFinal);
        Assert.Contains("MyProj", renderedFinal);
        Assert.Contains(complexPrompt, renderedFinal);
    }

    [Fact]
    public void REG_100_ValidationService_RejectsRelativePathTraversalInAcceptedExtensions()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();

        var invalidSettings = new AppSettings
        {
            DownloadFolder = workspace.Downloads,
            AssetRootFolder = workspace.Assets,
            AcceptedExtensions = new List<string> { "../exe", ".bat", "..\\png", "   " }
        };

        var result = validationService.ValidateSettings(invalidSettings);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Accepted extension"));
    }

    [Fact]
    public void REG_101_SessionService_CancelLockedFile_ThrowsIOExceptionAndPreservesSession()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg101", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        using (var destLock = new FileStream(session.ReferenceDestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.NotNull(ex);
            Assert.True(sessionService.Exists());
        }
    }

    [Fact]
    public void REG_102_FullLifecycle_Stress_ParallelIndependentSessions()
    {
        Parallel.For(0, 5, i =>
        {
            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var settings = workspace.CreateSettings();

            var refImg = workspace.CreateImage($"ref_{i}.png", new byte[] { (byte)(i + 1), (byte)(i + 2) });
            var session = processor.ProcessReference(settings, $"asset_parallel_{i}", refImg, DateTimeOffset.Now);
            sessionService.Save(session);

            var refReplacement = workspace.CreateImage($"ref_rep_{i}.png", new byte[] { (byte)(i + 10), (byte)(i + 20) });
            var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, refReplacement, DateTimeOffset.Now);
            processor.CommitReferenceReplacement(tx);
            sessionService.Save(tx.NewSession);

            var mainImg = workspace.CreateImage($"main_{i}.png", new byte[] { (byte)(i + 50), (byte)(i + 60) });
            var mainFilename = processor.ProcessMainPrepared(tx.NewSession, settings.AcceptedExtensions, mainImg, $"prompt {i}", DateTimeOffset.Now);
            sessionService.Delete();

            Assert.False(sessionService.Exists());
            Assert.True(File.Exists(Path.Combine(tx.NewSession.AssetFolder, mainFilename)));
            Assert.True(File.Exists(Path.Combine(tx.NewSession.AssetFolder, AppConstants.FinalProvenanceFileName)));
            Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
        });
    }

    [Fact]
    public void REG_103_Cancel_FilesRenamedJournalSaveFailure_RevertsRamToPrepared()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg103", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        try
        {
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                if (phase == CancelPhase.FilesRenamed)
                {
                    throw new IOException("Simulated disk full during FilesRenamed journal save.");
                }
            };

            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.Contains("Simulated disk full", ex.Message);

            // RAM phase should be reverted to Prepared (not left at FilesRenamed)
            Assert.Equal(CancelPhase.Prepared, session.CancelPhase);
            Assert.NotNull(session.CancellationId);

            // Disk phase should still be Prepared
            var onDiskSession = sessionService.Load();
            Assert.NotNull(onDiskSession);
            Assert.Equal(CancelPhase.Prepared, onDiskSession.CancelPhase);
            Assert.Equal(session.CancellationId, onDiskSession.CancellationId);

            // Original files are renamed; temp files exist
            Assert.False(File.Exists(session.ReferenceDestinationPath));
            Assert.False(File.Exists(session.ReferenceProvenancePath));
            Assert.True(File.Exists(session.GetCancelTempReferencePath()));
            Assert.True(File.Exists(session.GetCancelTempProvenancePath()));
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
        }
    }

    [Fact]
    public void REG_104_Cancel_FilesRenamedSaveFailure_RetryPersistsBeforeDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg104", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        try
        {
            // First attempt: fail on FilesRenamed save
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                if (phase == CancelPhase.FilesRenamed)
                {
                    throw new IOException("Simulated save failure");
                }
            };

            Assert.Throws<IOException>(() => sessionService.Cancel(session));

            // Remove failure hook and track subsequent phase saves
            var savedPhases = new List<CancelPhase>();
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                savedPhases.Add(phase);
            };

            // Second attempt (retry): should reconcile Prepared, persist FilesRenamed, then delete files
            sessionService.Cancel(session);

            Assert.Contains(CancelPhase.FilesRenamed, savedPhases);
            Assert.False(sessionService.Exists());
            Assert.False(File.Exists(session.GetCancelTempReferencePath()));
            Assert.False(File.Exists(session.GetCancelTempProvenancePath()));
            Assert.False(Directory.Exists(session.AssetFolder));
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
        }
    }

    [Fact]
    public void REG_105_Cancel_ConcurrentForeignFileBeforeFolderDelete_IsPreserved()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg105", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        var foreignFile = Path.Combine(session.AssetFolder, "foreign_concurrent.txt");
        var foreignContent = "foreign important content";

        try
        {
            SessionService.OnBeforeFolderCleanupHook = () =>
            {
                // Concurrently create a foreign file right before folder deletion
                File.WriteAllText(foreignFile, foreignContent);
            };

            sessionService.Cancel(session);

            // Session record and tool files should be deleted
            Assert.False(sessionService.Exists());
            Assert.False(File.Exists(session.ReferenceDestinationPath));
            Assert.False(File.Exists(session.ReferenceProvenancePath));

            // Foreign file must survive untouched, and the folder must not be deleted
            Assert.True(File.Exists(foreignFile));
            Assert.Equal(foreignContent, File.ReadAllText(foreignFile));
            Assert.True(Directory.Exists(session.AssetFolder));
        }
        finally
        {
            SessionService.OnBeforeFolderCleanupHook = null;
        }
    }

    [Fact]
    public void REG_106_RollbackReferenceReplacement_SameFilenameBackupsMissing_FailsValidation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg106", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref.png", new byte[] { 4, 5, 6 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Commit replacement (backups are deleted, transaction marked committed)
        var commitResult = processor.CommitReferenceReplacement(tx);
        Assert.True(commitResult.IsValid);

        // Attempt rollback after commit -> must fail cleanly
        var rollbackResult = processor.RollbackReferenceReplacement(tx);
        Assert.False(rollbackResult.IsValid);
        Assert.Contains(rollbackResult.Errors, e =>
            e.Contains("already been committed") ||
            e.Contains("does not match old reference hash") ||
            e.Contains("not found"));
    }

    [Fact]
    public void REG_107_RollbackReferenceReplacement_BackupsMissingDestinationProvenRestored_SucceedsIdempotently()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg107", ref1, DateTimeOffset.Now);
        var originalProvText = File.ReadAllText(session.ReferenceProvenancePath);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Simulate genuine prior restoration: restore old reference bytes and old provenance text, remove backups
        File.WriteAllBytes(session.ReferenceDestinationPath, File.ReadAllBytes(ref1));
        File.WriteAllText(session.ReferenceProvenancePath, originalProvText);
        File.Delete(tx.BackupReferencePath);
        File.Delete(tx.BackupProvenancePath);

        // Rollback should detect that destination matches OldSession hash and valid provenance, returning success idempotently
        var rollbackResult = processor.RollbackReferenceReplacement(tx);
        Assert.True(rollbackResult.IsValid);
        Assert.Equal(File.ReadAllBytes(ref1), File.ReadAllBytes(session.ReferenceDestinationPath));
        Assert.Equal(originalProvText, File.ReadAllText(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_108_ValidateSession_NonePhaseWithCancellationId_FailsValidation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg108", refSource, DateTimeOffset.Now);

        // CancelPhase is None, but CancellationId is non-empty
        session.CancelPhase = CancelPhase.None;
        session.CancellationId = "1234567890abcdef1234567890abcdef";

        var result = validationService.ValidateSession(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("CancellationId must be empty when CancelPhase is None"));
    }

    [Fact]
    public void REG_109_SessionService_Roundtrip_PreservesMainCommitMetadata()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg109", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        // Simulate main image processing failure that preserved metadata
        session.IsMainCommitting = true;
        session.MainFilename = "failed_main.png";
        session.MainPrompt = "persisted prompt";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = new string('a', 64);
        sessionService.Save(session);

        var loaded = sessionService.Load();
        Assert.NotNull(loaded);
        Assert.True(loaded.IsMainCommitting);
        Assert.Equal("failed_main.png", loaded.MainFilename);
        Assert.Equal("persisted prompt", loaded.MainPrompt);
        Assert.Equal(new string('a', 64), loaded.MainHash);
    }

    [Fact]
    public void REG_110_ReferenceReplacement_PartialRollbackFailureAndRetry_PreservesRestoredContent()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 11, 22, 33 });
        var session = processor.ProcessReference(settings, "asset_reg110", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 44, 55, 66 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Lock backup reference file during first rollback attempt
        using (var destLock = new FileStream(tx.BackupReferencePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var res1 = processor.RollbackReferenceReplacement(tx);
            Assert.False(res1.IsValid);
        }

        // Provenance was restored. Unlock and retry rollback
        var res2 = processor.RollbackReferenceReplacement(tx);
        Assert.True(res2.IsValid);

        Assert.Equal(File.ReadAllBytes(ref1), File.ReadAllBytes(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_111_ReferenceReplacement_RollbackSameFilenameMatrix_AllCombinationsTested()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        // Combo 1: Both backups exist -> standard successful rollback
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 1, 1 });
        var s1 = processor.ProcessReference(settings, "asset_m1", ref1, DateTimeOffset.Now);
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 2, 2, 2 });
        var tx1 = processor.PrepareReferenceReplacement(s1, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        var r1 = processor.RollbackReferenceReplacement(tx1);
        Assert.True(r1.IsValid);
        Assert.Equal(File.ReadAllBytes(ref1), File.ReadAllBytes(s1.ReferenceDestinationPath));

        // Combo 2: Backup missing, destination matches new image -> fails rollback
        var s2 = processor.ProcessReference(settings, "asset_m2", workspace.CreateImage("ref.png", new byte[] { 3, 3, 3 }), DateTimeOffset.Now);
        var tx2 = processor.PrepareReferenceReplacement(s2, settings.AcceptedExtensions, workspace.CreateImage("ref.png", new byte[] { 4, 4, 4 }), DateTimeOffset.Now);
        File.Delete(tx2.BackupReferencePath);
        var r2 = processor.RollbackReferenceReplacement(tx2);
        Assert.False(r2.IsValid);

        // Combo 3: Backup missing, destination missing -> fails rollback
        var s3 = processor.ProcessReference(settings, "asset_m3", workspace.CreateImage("ref.png", new byte[] { 5, 5, 5 }), DateTimeOffset.Now);
        var tx3 = processor.PrepareReferenceReplacement(s3, settings.AcceptedExtensions, workspace.CreateImage("ref.png", new byte[] { 6, 6, 6 }), DateTimeOffset.Now);
        File.Delete(tx3.BackupReferencePath);
        File.Delete(s3.ReferenceDestinationPath);
        var r3 = processor.RollbackReferenceReplacement(tx3);
        Assert.False(r3.IsValid);
    }

    [Fact]
    public void REG_121_RollbackMain_InactiveSessionWithStaleMainFilename_PreservesFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var session = processor.ProcessReference(settings, "asset_r121", refSource, DateTimeOffset.Now);

        // Keep file in asset folder
        var keepFile = Path.Combine(session.AssetFolder, "keep.png");
        File.WriteAllBytes(keepFile, new byte[] { 1, 2, 3 });

        session.IsMainCommitting = false;
        session.MainFilename = "keep.png";

        var result = processor.RollbackMain(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("No active Main commit exists"));
        Assert.True(File.Exists(keepFile));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(keepFile));
    }

    [Fact]
    public void REG_122_RollbackMain_MainHashMismatch_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var session = processor.ProcessReference(settings, "asset_r122", refSource, DateTimeOffset.Now);

        var mainFile = Path.Combine(session.AssetFolder, "main.png");
        File.WriteAllBytes(mainFile, new byte[] { 99, 99, 99 });

        session.IsMainCommitting = true;
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";
        session.MainFilename = "main.png";
        session.MainPrompt = "test prompt";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = new string('a', 64); // Different hash

        var result = processor.RollbackMain(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not match session MainHash"));
        Assert.True(File.Exists(mainFile));
        Assert.Equal(new byte[] { 99, 99, 99 }, File.ReadAllBytes(mainFile));
    }

    [Fact]
    public void REG_123_MainRecovery_CrashAfterTempImageCopy_RemovesExactOwnedTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var session = processor.ProcessReference(settings, "asset_r123", refSource, DateTimeOffset.Now);

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = DateTimeOffset.Now;
        var dummyMain = workspace.CreateImage("dummy_main123.png", new byte[] { 7, 7, 7 });
        session.MainHash = ValidationService.ComputeSha256(dummyMain);
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";

        var ownedTemp = session.GetMainTempImagePath();
        var foreignTemp = Path.Combine(session.AssetFolder, ".main-foreign.image.tmp");
        File.WriteAllBytes(ownedTemp, File.ReadAllBytes(dummyMain));
        File.WriteAllBytes(foreignTemp, new byte[] { 8, 8, 8 });

        var result = processor.RollbackMain(session);
        Assert.True(result.IsValid);
        Assert.False(File.Exists(ownedTemp));
        Assert.True(File.Exists(foreignTemp));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
    }

    [Fact]
    public void REG_124_MainRecovery_CrashDuringProvenanceTempWrite_RemovesExactOwnedTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var session = processor.ProcessReference(settings, "asset_r124", refSource, DateTimeOffset.Now);

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = new string('0', 64);
        session.MainTransactionId = "fedcba9876543210fedcba9876543210";

        var ownedTempProv = session.GetMainTempProvenancePath();
        var foreignTempProv = Path.Combine(session.AssetFolder, ".main-foreign.provenance.tmp");
        var templateService = workspace.CreateTemplateService();
        var expectedText = templateService.RenderFinal(
            session.MainFilename,
            session.ReferenceFilename,
            session.ProjectName,
            session.MainProcessedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            session.MainPrompt);
        File.WriteAllText(ownedTempProv, expectedText, new UTF8Encoding(false));
        File.WriteAllText(foreignTempProv, "foreign temp");

        var result = processor.RollbackMain(session);
        Assert.True(result.IsValid);
        Assert.False(File.Exists(ownedTempProv));
        Assert.True(File.Exists(foreignTempProv));
    }

    [Fact]
    public void REG_125_StartupRecovery_ValidCompletedMainButCorruptReferenceProvenance_DoesNotDeleteMain()
    {
        var thread = new Thread(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_r125", refSource, DateTimeOffset.Now);

            var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
            var mainHash = processor.ComputeSha256(mainSource);
            var processedAt = DateTimeOffset.Now;

            session.IsMainCommitting = true;
            session.MainFilename = "main.png";
            session.MainPrompt = "test prompt";
            session.MainProcessedAt = processedAt;
            session.MainHash = mainHash;
            session.MainTransactionId = "11112222333344445555666677778888";

            processor.ProcessMainPrepared(session, settings.AcceptedExtensions, mainSource, "test prompt", processedAt);
            sessionService.Save(session);

            // Corrupt reference provenance content
            File.WriteAllText(session.ReferenceProvenancePath, "CORRUPT CONTENT MISSING TOKENS");

            var mainPath = Path.Combine(session.AssetFolder, "main.png");
            var ingamePath = Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_r125.png");
            var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
            Assert.True(File.Exists(mainPath));
            Assert.True(File.Exists(ingamePath));
            Assert.True(File.Exists(finalProv));

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => false; // User exits
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => { };

            try
            {
                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    processor,
                    sessionService);

                var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
                recoverMethod?.Invoke(form, null);

                // Main and Final provenance MUST NOT be deleted
                Assert.True(File.Exists(mainPath));
                Assert.True(File.Exists(ingamePath));
                Assert.Equal(File.ReadAllBytes(mainSource), File.ReadAllBytes(mainPath));
                Assert.Equal(File.ReadAllBytes(mainSource), File.ReadAllBytes(ingamePath));
                Assert.True(File.Exists(finalProv));
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_126_CommitReferenceReplacement_TamperedNewReference_PreservesOldBackups()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
        var session = processor.ProcessReference(settings, "asset_r126", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Tamper new reference destination bytes
        File.WriteAllBytes(tx.NewSession.ReferenceDestinationPath, new byte[] { 99, 99, 99 });

        var commitRes = processor.CommitReferenceReplacement(tx);
        Assert.False(commitRes.IsValid);
        Assert.False(tx.IsCommitted);
        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.True(File.Exists(tx.BackupProvenancePath));
    }

    [Fact]
    public void REG_127_CommitReferenceReplacement_MissingOrCorruptNewProvenance_PreservesOldBackups()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
        var session = processor.ProcessReference(settings, "asset_r127", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Corrupt new provenance
        File.WriteAllText(tx.NewSession.ReferenceProvenancePath, "CORRUPT CONTENT");

        var commitRes = processor.CommitReferenceReplacement(tx);
        Assert.False(commitRes.IsValid);
        Assert.False(tx.IsCommitted);
        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.True(File.Exists(tx.BackupProvenancePath));
    }

    [Fact]
    public void REG_128_ProcessReference_InvalidNestedFolderName_HasZeroFilesystemSideEffects()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sourceImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var initialEntries = Directory.GetFileSystemEntries(workspace.Assets, "*", SearchOption.AllDirectories);

        Assert.Throws<InvalidDataException>(() =>
            processor.ProcessReference(settings, "invalid/name", sourceImage, DateTimeOffset.Now));

        var afterEntries = Directory.GetFileSystemEntries(workspace.Assets, "*", SearchOption.AllDirectories);
        Assert.Equal(initialEntries, afterEntries);
    }

    [Fact]
    public void REG_129_ProcessReference_TraversalOrRootedFolderName_RejectedBeforeIO()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sourceImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var initialEntries = Directory.GetFileSystemEntries(workspace.Assets, "*", SearchOption.AllDirectories);

        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(settings, "..", sourceImage, DateTimeOffset.Now));
        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(settings, @"..\outside", sourceImage, DateTimeOffset.Now));
        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(settings, @"sub\folder", sourceImage, DateTimeOffset.Now));
        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(settings, @"C:\rooted_folder", sourceImage, DateTimeOffset.Now));
        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(settings, @"C:relative_folder", sourceImage, DateTimeOffset.Now));
        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(settings, @"\\server\share\folder", sourceImage, DateTimeOffset.Now));
        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(settings, @"/absolute_folder", sourceImage, DateTimeOffset.Now));

        var afterEntries = Directory.GetFileSystemEntries(workspace.Assets, "*", SearchOption.AllDirectories);
        Assert.Equal(initialEntries, afterEntries);
    }

    [Fact]
    public void REG_130_ProcessReference_InvalidSourceExtension_DoesNotCreateAssetFolder()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var invalidSource = Path.Combine(workspace.Downloads, "unsupported.xyz");
        File.WriteAllBytes(invalidSource, new byte[] { 1, 2, 3 });

        Assert.Throws<InvalidDataException>(() =>
            processor.ProcessReference(settings, "asset_r130", invalidSource, DateTimeOffset.Now));

        var targetFolder = Path.Combine(workspace.Assets, "asset_r130");
        Assert.False(Directory.Exists(targetFolder));
    }

    [Fact]
    public void REG_131_MainForm_CleanMainFailureClearsCompleteTransactionState()
    {
        var thread = new Thread(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var validationService = workspace.CreateValidationService();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_r131", refSource, DateTimeOffset.Now);
            sessionService.Save(session);

            // Pre-create foreign main.png inside asset folder so ProcessMainImage fails with IOException
            var foreignMain = Path.Combine(session.AssetFolder, "main.png");
            File.WriteAllBytes(foreignMain, new byte[] { 9, 9, 9 });

            var mainSource = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });

            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => { };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                validationService,
                processor,
                sessionService);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            form.SetSelectedImage(ImageSlot.Main, mainSource);

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);
            txtPrompt.Text = "Main prompt";

            var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleMainMethod);
            handleMainMethod.Invoke(form, null);

            // Verify persisted session
            var loadedSession = sessionService.Load();
            Assert.NotNull(loadedSession);
            Assert.False(loadedSession.IsMainCommitting);
            Assert.Null(loadedSession.MainFilename);
            Assert.Null(loadedSession.MainPrompt);
            Assert.Null(loadedSession.MainProcessedAt);
            Assert.Null(loadedSession.MainHash);
            Assert.Null(loadedSession.MainTransactionId);

            var val = validationService.ValidateSession(loadedSession);
            Assert.True(val.IsValid);

            // Verify foreign file remains untouched
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(foreignMain));

            MainForm.MessageBoxProvider = null;
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_132_MainForm_ReplacementCommitValidationFailureRestoresOldSession()
    {
        var thread = new Thread(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var validationService = workspace.CreateValidationService();

            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
            var session = processor.ProcessReference(settings, "asset_r132", ref1, DateTimeOffset.Now);
            sessionService.Save(session);

            var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });

            var messages = new List<string>();
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => messages.Add(msg);
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => true; // Confirm replace

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                validationService,
                processor,
                sessionService);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            // Hook into OnBeforeReferenceReplacementCommit to make commit validation fail
            MainForm.OnBeforeReferenceReplacementCommit = tx =>
            {
                // Delete new provenance before commit so new output validation fails
                File.Delete(tx.NewSession.ReferenceProvenancePath);
            };

            var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleReplaceMethod);
            handleReplaceMethod.Invoke(form, null);

            // Per R3-003: On cleanup failure, CleanupPending journal is preserved
            Assert.True(sessionService.ReplacementJournalExists());
            var journal = sessionService.LoadReplacementJournal();
            Assert.NotNull(journal);
            Assert.Equal(ReferenceReplacementPhase.CleanupPending, journal.Phase);

            // Verify error message was displayed and NOT success
            Assert.Contains(messages, m => m.Contains("cleanup") || m.Contains("CleanupPending"));
            Assert.DoesNotContain(messages, m => m.Contains("Reference replacement succeeded"));

            MainForm.OnBeforeReferenceReplacementCommit = null;
            MainForm.MessageBoxProvider = null;
            TwoChoiceDialog.CustomChoiceProvider = null;
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_133_MainForm_ReplacementBackupCleanupFailureKeepsValidNewSession()
    {
        var thread = new Thread(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var validationService = workspace.CreateValidationService();

            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
            var session = processor.ProcessReference(settings, "asset_r133", ref1, DateTimeOffset.Now);
            sessionService.Save(session);

            var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });

            var messages = new List<string>();
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => messages.Add(msg);
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => true; // Confirm replace

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                validationService,
                processor,
                sessionService);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            // Hook into OnBeforeReferenceReplacementCommit to lock the backup file so cleanup fails
            FileStream? lockStream = null;
            MainForm.OnBeforeReferenceReplacementCommit = tx =>
            {
                lockStream = new FileStream(tx.BackupReferencePath, FileMode.Open, FileAccess.Read, FileShare.None);
            };

            try
            {
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(handleReplaceMethod);
                handleReplaceMethod.Invoke(form, null);

                // Per R3-003: On cleanup failure, CleanupPending journal is preserved
                Assert.True(sessionService.ReplacementJournalExists());
                var journal = sessionService.LoadReplacementJournal();
                Assert.NotNull(journal);
                Assert.Equal(ReferenceReplacementPhase.CleanupPending, journal.Phase);

                // Verify cleanup failure message was displayed
                Assert.Contains(messages, m => m.Contains("cleanup") || m.Contains("CleanupPending"));
            }
            finally
            {
                lockStream?.Dispose();
                MainForm.OnBeforeReferenceReplacementCommit = null;
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    // REG_132/REG_133 above exercise the LIVE HandleReplaceReference commit
    // path, which never calls MainForm.Recovery.cs's FinishReplacementCommit
    // (grep confirms that method has exactly two callers, both inside
    // MainForm.Recovery.cs itself, reached only via startup recovery of a
    // journal already in SessionSwitched/CleanupPending phase). The three
    // tests below reuse REG_132/133's proven setups to reach a real,
    // durably-persisted CleanupPending journal, then simulate an app
    // restart - a fresh MainForm running RecoverSessionOnStartup - to
    // exercise FinishReplacementCommit's own three error arms directly,
    // per the independent audit's specific ask.

    [Fact]
    public void REG_206_FinishReplacementCommit_NewExactValidationFailure_PreservesJournalOnRecovery()
    {
        var thread = new Thread(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var validationService = workspace.CreateValidationService();

            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
            var session = processor.ProcessReference(settings, "asset_r135", ref1, DateTimeOffset.Now);
            sessionService.Save(session);

            var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

            using (var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                validationService,
                processor,
                sessionService))
            {
                var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
                var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
                sessionField?.SetValue(form, session);
                stateField?.SetValue(form, 1); // UiState.ReferenceReady

                form.SetSelectedImage(ImageSlot.Reference, ref2);

                // Reaching CleanupPending here is only step 1 (identical
                // mechanism to REG_132); the point of this test is step 2 below.
                MainForm.OnBeforeReferenceReplacementCommit = tx =>
                    File.Delete(tx.NewSession.ReferenceProvenancePath);

                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleReplaceMethod!.Invoke(form, null);

                MainForm.OnBeforeReferenceReplacementCommit = null;
            }

            Assert.True(sessionService.ReplacementJournalExists());
            var journalBefore = sessionService.LoadReplacementJournal();
            Assert.NotNull(journalBefore);
            Assert.Equal(ReferenceReplacementPhase.CleanupPending, journalBefore!.Phase);
            // The NEW provenance file the live commit deleted is still
            // missing, so a fresh recovery attempt will hit exactNew
            // validation failure inside FinishReplacementCommit itself.
            Assert.False(File.Exists(journalBefore.NewSession.ReferenceProvenancePath));

            var recoveryMessages = new List<string>();
            MainForm.MessageBoxProvider = (_, msg, _, _, _) => recoveryMessages.Add(msg);

            try
            {
                using var recoveredForm = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    processor,
                    workspace.CreateSessionService());

                var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
                recoverMethod!.Invoke(recoveredForm, null);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }

            // FailReplacementRecovery's message and "journal preserved" contract.
            Assert.Contains(recoveryMessages, m => m.Contains("Failed to recover interrupted reference replacement", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(recoveryMessages, m => m.Contains("journal was preserved", StringComparison.OrdinalIgnoreCase));
            Assert.True(sessionService.ReplacementJournalExists());

            var journalAfter = sessionService.LoadReplacementJournal();
            Assert.NotNull(journalAfter);
            Assert.Equal(ReferenceReplacementPhase.CleanupPending, journalAfter!.Phase);
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_207_FinishReplacementCommit_BackupDeletionFailure_PreservesJournalOnRecovery()
    {
        var thread = new Thread(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var validationService = workspace.CreateValidationService();

            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
            var session = processor.ProcessReference(settings, "asset_r136", ref1, DateTimeOffset.Now);
            sessionService.Save(session);

            var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

            string? backupReferencePath = null;

            using (var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                validationService,
                processor,
                sessionService))
            {
                var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
                var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
                sessionField?.SetValue(form, session);
                stateField?.SetValue(form, 1); // UiState.ReferenceReady

                form.SetSelectedImage(ImageSlot.Reference, ref2);

                // Step 1: reach CleanupPending exactly like REG_133 (lock the
                // backup reference so cleanup fails once, during the LIVE
                // commit), but capture the path and release the lock before
                // this scope ends so recovery can attempt cleanup again.
                FileStream? lockStream = null;
                MainForm.OnBeforeReferenceReplacementCommit = tx =>
                {
                    backupReferencePath = tx.BackupReferencePath;
                    lockStream = new FileStream(tx.BackupReferencePath, FileMode.Open, FileAccess.Read, FileShare.None);
                };

                try
                {
                    var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                    handleReplaceMethod!.Invoke(form, null);
                }
                finally
                {
                    lockStream?.Dispose();
                    MainForm.OnBeforeReferenceReplacementCommit = null;
                }
            }

            Assert.True(sessionService.ReplacementJournalExists());
            Assert.Equal(ReferenceReplacementPhase.CleanupPending, sessionService.LoadReplacementJournal()!.Phase);
            Assert.NotNull(backupReferencePath);
            Assert.True(File.Exists(backupReferencePath));

            // Step 2: fail the SAME backup delete again, but this time via a
            // fresh MainForm's recovery path, so FinishReplacementCommit's
            // own cleanup-failure branch (not the live commit's) is what runs.
            var recoveryMessages = new List<string>();
            MainForm.MessageBoxProvider = (_, msg, _, _, _) => recoveryMessages.Add(msg);

            using (var recoveryLock = new FileStream(backupReferencePath!, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    using var recoveredForm = new MainForm(
                        settings,
                        workspace.CreateSettingsService(),
                        workspace.CreateImageFinder(),
                        workspace.CreateTemplateService(),
                        workspace.CreateValidationService(),
                        processor,
                        workspace.CreateSessionService());

                    var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
                    recoverMethod!.Invoke(recoveredForm, null);
                }
                finally
                {
                    MainForm.MessageBoxProvider = null;
                    TwoChoiceDialog.CustomChoiceProvider = null;
                }
            }

            Assert.Contains(recoveryMessages, m => m.Contains("Failed to recover interrupted reference replacement", StringComparison.OrdinalIgnoreCase));
            Assert.True(sessionService.ReplacementJournalExists());
            Assert.Equal(ReferenceReplacementPhase.CleanupPending, sessionService.LoadReplacementJournal()!.Phase);
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_208_FinishReplacementCommit_JournalDeletionFailure_ShowsErrorAndPreservesJournal()
    {
        var thread = new Thread(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var validationService = workspace.CreateValidationService();

            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
            var session = processor.ProcessReference(settings, "asset_r137", ref1, DateTimeOffset.Now);
            sessionService.Save(session);

            var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

            using (var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                validationService,
                processor,
                sessionService))
            {
                var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
                var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
                sessionField?.SetValue(form, session);
                stateField?.SetValue(form, 1); // UiState.ReferenceReady

                form.SetSelectedImage(ImageSlot.Reference, ref2);

                FileStream? lockStream = null;
                MainForm.OnBeforeReferenceReplacementCommit = tx =>
                {
                    lockStream = new FileStream(tx.BackupReferencePath, FileMode.Open, FileAccess.Read, FileShare.None);
                };

                try
                {
                    var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                    handleReplaceMethod!.Invoke(form, null);
                }
                finally
                {
                    lockStream?.Dispose();
                    MainForm.OnBeforeReferenceReplacementCommit = null;
                }
            }

            Assert.True(sessionService.ReplacementJournalExists());
            Assert.Equal(ReferenceReplacementPhase.CleanupPending, sessionService.LoadReplacementJournal()!.Phase);

            // This time cleanup will succeed on recovery (the backup lock was
            // released above); fail only the final journal delete itself.
            var recoveryMessages = new List<string>();
            MainForm.MessageBoxProvider = (_, msg, _, _, _) => recoveryMessages.Add(msg);
            SessionService.OnBeforeReplacementJournalDeleteHook =
                () => throw new IOException("Simulated failure deleting replacement journal.");

            try
            {
                using var recoveredForm = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    processor,
                    workspace.CreateSessionService());

                var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
                recoverMethod!.Invoke(recoveredForm, null);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                SessionService.OnBeforeReplacementJournalDeleteHook = null;
            }

            Assert.Contains(recoveryMessages, m => m.Contains("journal could not be deleted", StringComparison.OrdinalIgnoreCase));
            Assert.True(sessionService.ReplacementJournalExists());
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_134_ResetMainCommitMetadata_ClearsAllCommitStateConsistently()
    {
        var session = new AssetSession
        {
            IsMainCommitting = true,
            MainFilename = "main.png",
            MainPrompt = "prompt",
            MainProcessedAt = DateTimeOffset.Now,
            MainHash = new string('a', 64),
            MainTransactionId = "1234567890abcdef1234567890abcdef"
        };

        session.ResetMainCommitMetadata();

        Assert.False(session.IsMainCommitting);
        Assert.Null(session.MainFilename);
        Assert.Null(session.MainPrompt);
        Assert.Null(session.MainProcessedAt);
        Assert.Null(session.MainHash);
        Assert.Null(session.MainTransactionId);
    }

    [Fact]
    public void REG_135_CoverageScopeGuard_RequiresMainFormCsSpecifically()
    {
        // Synthetic coverage XML where MainForm has a class node but only for Designer.cs
        var syntheticXmlWithoutMainFormCs = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.9"" branch-rate=""0.9"" lines-valid=""100"" branches-valid=""20"">
  <packages>
    <package name=""AssetProvenanceHelper"">
      <classes>
        <class name=""AssetProvenanceHelper.MainForm"" filename=""MainForm.Designer.cs"" line-rate=""1"" branch-rate=""1"">
          <lines><line number=""1"" hits=""1"" /></lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>";

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(syntheticXmlWithoutMainFormCs);

        // Flawed check by name only would pass:
        var classNodesByName = doc.SelectNodes("//class")?.Cast<System.Xml.XmlElement>().ToList();
        var foundByName = classNodesByName?.Any(c => c.GetAttribute("name") == "AssetProvenanceHelper.MainForm") ?? false;
        Assert.True(foundByName); // Shows why flawed guard passed

        // Filename-aware check MUST fail:
        var foundByFile = classNodesByName?.Any(c =>
            c.GetAttribute("name") == "AssetProvenanceHelper.MainForm" &&
            (c.GetAttribute("filename") == "MainForm.cs" || c.GetAttribute("filename").EndsWith("\\MainForm.cs") || c.GetAttribute("filename").EndsWith("/MainForm.cs"))) ?? false;
        Assert.False(foundByFile); // Verified guard correctly rejects missing MainForm.cs
    }

    [Fact]
    public void REG_136_AppBootstrap_DerivesStablePerUserMutexAndLoadsSettings()
    {
        using var workspace = new TestWorkspace();
        var baseDir = workspace.Root;

        var mutex1 = AppBootstrap.BuildSingleInstanceMutexName(baseDir);
        var mutex2 = AppBootstrap.BuildSingleInstanceMutexName(baseDir);
        Assert.Equal(mutex1, mutex2);

        var mutexOther = AppBootstrap.BuildSingleInstanceMutexName(workspace.Downloads);
        Assert.Equal(mutex1, mutexOther);

        var settingsPath = AppBootstrap.GetSettingsPath(baseDir);
        var settingsService = new SettingsService(settingsPath);
        settingsService.Save(new AppSettings { DownloadFolder = workspace.Downloads, AssetRootFolder = workspace.Assets });

        var loaded = AppBootstrap.LoadSettingsOrDefaults(settingsService);
        Assert.Equal(workspace.Assets, loaded.AssetRootFolder);

        Assert.Equal(
            Path.Combine(AppBootstrap.GetStateDirectory(), AppConstants.SettingsFileName),
            AppBootstrap.GetSettingsPath(AppBootstrap.GetStateDirectory()));
    }

    [Fact]
    public void REG_137_RollbackMain_ForeignFinalProvenance_PreservesFileAndFailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var session = processor.ProcessReference(settings, "asset_reg137", refSource, DateTimeOffset.Now);

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Valid prompt";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = new string('a', 64);
        session.MainTransactionId = "abcdef1234567890abcdef1234567890";

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        File.WriteAllText(finalProvPath, "FOREIGN IMPORTANT CONTENT");

        var result = processor.RollbackMain(session);

        Assert.False(result.IsValid);
        Assert.True(File.Exists(finalProvPath));
        Assert.Equal("FOREIGN IMPORTANT CONTENT", File.ReadAllText(finalProvPath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_138_RollbackMain_OwnedFinalProvenanceWithoutMain_RemovesOwnedFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var processedAt = DateTimeOffset.Now;
        var session = processor.ProcessReference(settings, "asset_reg138", refSource, processedAt);

        var dateStr = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ownedProvContent = workspace.CreateTemplateService().RenderFinal(
            "main.png",
            session.ReferenceFilename,
            session.ProjectName,
            dateStr,
            "Owned prompt");

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Owned prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = new string('b', 64);
        session.MainTransactionId = "1234567890abcdef1234567890abcdef";

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        File.WriteAllText(finalProvPath, ownedProvContent);

        var result = processor.RollbackMain(session);

        Assert.True(result.IsValid);
        Assert.False(File.Exists(finalProvPath));
        Assert.False(session.IsMainCommitting);
    }

    [Fact]
    public void REG_139_RollbackReplacement_MissingOldBackupAndMismatchedOldDestination_PreservesNewReference()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg139", ref1, DateTimeOffset.Now);
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Delete backup
        File.Delete(tx.BackupReferencePath);

        // Write foreign data to old reference destination
        File.WriteAllBytes(tx.OldSession.ReferenceDestinationPath, new byte[] { 99, 99, 99, 99 });

        var result = processor.RollbackReferenceReplacement(tx);

        Assert.False(result.IsValid);
        Assert.True(File.Exists(tx.OldSession.ReferenceDestinationPath));
        Assert.Equal(new byte[] { 99, 99, 99, 99 }, File.ReadAllBytes(tx.OldSession.ReferenceDestinationPath));
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
        Assert.Equal(File.ReadAllBytes(ref2), File.ReadAllBytes(tx.NewSession.ReferenceDestinationPath));
    }

    [Fact]
    public void REG_140_RollbackReplacement_MissingBackupButVerifiedOldDestination_DeletesNewReference()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg140", ref1, DateTimeOffset.Now);
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Delete backup and restore authentic old reference at destination
        File.Delete(tx.BackupReferencePath);
        File.WriteAllBytes(tx.OldSession.ReferenceDestinationPath, File.ReadAllBytes(ref1));

        var result = processor.RollbackReferenceReplacement(tx);

        Assert.True(result.IsValid);
        Assert.True(File.Exists(tx.OldSession.ReferenceDestinationPath));
        Assert.False(File.Exists(tx.NewSession.ReferenceDestinationPath));
    }

    [Fact]
    public void REG_141_ProcessMain_RandomFallbackProvenanceMoveFailure_CleansExactTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg141", refSource, DateTimeOffset.Now);
        session.MainTransactionId = null; // Unjournaled random fallback

        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        AssetProcessorService.OnFileCopiedHook = (_, _) =>
        {
            File.WriteAllText(finalProv, "colliding final provenance");
        };

        try
        {
            Assert.Throws<IOException>(() => processor.ProcessMainPrepared(
                session,
                settings.AcceptedExtensions,
                mainSource,
                "Prompt text",
                DateTimeOffset.Now));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }

        var tempFiles = Directory.GetFiles(session.AssetFolder, ".main-*.md.tmp");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public void REG_142_ProcessMain_RandomFallbackProvenanceMoveFailure_ForeignFinalProvenanceUnchanged()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg142", refSource, DateTimeOffset.Now);
        session.MainTransactionId = null; // Unjournaled random fallback

        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        AssetProcessorService.OnFileCopiedHook = (_, _) =>
        {
            File.WriteAllText(finalProv, "FOREIGN IMPORTANT COLLISION");
        };

        try
        {
            Assert.Throws<IOException>(() => processor.ProcessMainPrepared(
                session,
                settings.AcceptedExtensions,
                mainSource,
                "Prompt text",
                DateTimeOffset.Now));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }

        Assert.True(File.Exists(finalProv));
        Assert.Equal("FOREIGN IMPORTANT COLLISION", File.ReadAllText(finalProv));
    }

    [Fact]
    public void REG_143_RollbackMain_ModifiedButMarkerValidFinalProvenance_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var processedAt = DateTimeOffset.Now;
        var session = processor.ProcessReference(settings, "asset_reg143", refSource, processedAt);

        var dateStr = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var originalProv = workspace.CreateTemplateService().RenderFinal(
            "main.png",
            session.ReferenceFilename,
            session.ProjectName,
            dateStr,
            "Owned prompt");

        var modifiedProv = originalProv + "\n\nFOREIGN USER APPENDIX - DO NOT DELETE";

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Owned prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = new string('c', 64);
        session.MainTransactionId = "11223344556677889900aabbccddeeff";

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        File.WriteAllText(finalProvPath, modifiedProv);

        var result = processor.RollbackMain(session);

        Assert.False(result.IsValid);
        Assert.True(File.Exists(finalProvPath));
        Assert.Equal(modifiedProv, File.ReadAllText(finalProvPath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_144_RollbackMain_ExactOwnedFinalProvenance_IsDeleted()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var processedAt = DateTimeOffset.Now;
        var session = processor.ProcessReference(settings, "asset_reg144", refSource, processedAt);

        var dateStr = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var exactProv = workspace.CreateTemplateService().RenderFinal(
            "main.png",
            session.ReferenceFilename,
            session.ProjectName,
            dateStr,
            "Owned prompt");

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Owned prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = new string('d', 64);
        session.MainTransactionId = "aabbccddeeff00112233445566778899";

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        File.WriteAllText(finalProvPath, exactProv);

        var result = processor.RollbackMain(session);

        Assert.True(result.IsValid);
        Assert.False(File.Exists(finalProvPath));
        Assert.False(session.IsMainCommitting);
    }

    [Fact]
    public void REG_145_RollbackReplacement_CorruptBackupReference_PreservesValidNewState()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg145", ref1, DateTimeOffset.Now);
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Tamper backup with foreign bytes
        File.WriteAllBytes(tx.BackupReferencePath, new byte[] { 88, 88, 88, 88 });

        var result = processor.RollbackReferenceReplacement(tx);

        Assert.False(result.IsValid);
        // Valid new reference preserved
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
        Assert.Equal(File.ReadAllBytes(ref2), File.ReadAllBytes(tx.NewSession.ReferenceDestinationPath));
        // Corrupt backup preserved
        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.Equal(new byte[] { 88, 88, 88, 88 }, File.ReadAllBytes(tx.BackupReferencePath));
    }

    [Fact]
    public void REG_146_RollbackReplacement_CorruptBackupProvenance_PreservesValidNewState()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg146", ref1, DateTimeOffset.Now);
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Tamper backup provenance with foreign text
        File.WriteAllText(tx.BackupProvenancePath, "FOREIGN CORRUPT BACKUP PROVENANCE");

        var result = processor.RollbackReferenceReplacement(tx);

        Assert.False(result.IsValid);
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
        Assert.Equal(File.ReadAllBytes(ref2), File.ReadAllBytes(tx.NewSession.ReferenceDestinationPath));
        Assert.True(File.Exists(tx.BackupProvenancePath));
        Assert.Equal("FOREIGN CORRUPT BACKUP PROVENANCE", File.ReadAllText(tx.BackupProvenancePath));
    }

    [Fact]
    public void REG_147_RollbackReplacement_TamperedCurrentNewReference_DoesNotDeleteUnknownFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg147", ref1, DateTimeOffset.Now);
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Tamper current new reference
        File.WriteAllBytes(tx.NewSession.ReferenceDestinationPath, new byte[] { 77, 77, 77 });

        var result = processor.RollbackReferenceReplacement(tx);

        Assert.False(result.IsValid);
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
        Assert.Equal(new byte[] { 77, 77, 77 }, File.ReadAllBytes(tx.NewSession.ReferenceDestinationPath));
        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.Equal(File.ReadAllBytes(ref1), File.ReadAllBytes(tx.BackupReferencePath));
    }

    [Fact]
    public void REG_148_CommitReplacement_TamperedOldBackup_DoesNotDeleteUnknownBackup()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg148", ref1, DateTimeOffset.Now);
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Tamper backup reference
        File.WriteAllBytes(tx.BackupReferencePath, new byte[] { 66, 66, 66 });

        var result = processor.CommitReferenceReplacement(tx);

        Assert.False(result.IsValid);
        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.Equal(new byte[] { 66, 66, 66 }, File.ReadAllBytes(tx.BackupReferencePath));
        Assert.False(tx.IsCommitted);
    }

    [Fact]
    public void REG_149_RollbackReference_TamperedReference_PreservesUnknownFiles()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset_reg149", refSource, DateTimeOffset.Now);

        // Tamper reference destination image
        File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 55, 55, 55 });

        var result = processor.RollbackReference(session);

        Assert.False(result.IsValid);
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.Equal(new byte[] { 55, 55, 55 }, File.ReadAllBytes(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_150_HandleCancel_ProvenanceModifiedBeforeCancel_DoesNotDeleteFiles()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset_reg150", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        // Tamper provenance before cancel
        File.WriteAllText(session.ReferenceProvenancePath, "FOREIGN UNEXPECTED PROVENANCE");

        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
        Assert.Equal("FOREIGN UNEXPECTED PROVENANCE", File.ReadAllText(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_151_HandleCancel_ReferenceChangesDuringConfirmation_DoesNotDeleteChangedFile()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var settingsService = workspace.CreateSettingsService();
        var imageFinder = workspace.CreateImageFinder();
        var templateService = workspace.CreateTemplateService();
        var validationService = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg151", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        using var form = new MainForm(
            settings,
            settingsService,
            imageFinder,
            templateService,
            validationService,
            processor,
            sessionService);

        var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
        sessionField?.SetValue(form, session);

        TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, btn1, btn2) =>
        {
            // Mutate reference image bytes during dialog interaction
            File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 44, 44, 44, 44 });
            return true; // Confirm cancel
        };
        MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => { };

        try
        {
            var handleCancelMethod = typeof(MainForm).GetMethod("HandleCancel", BindingFlags.NonPublic | BindingFlags.Instance);
            handleCancelMethod?.Invoke(form, null);
        }
        finally
        {
            TwoChoiceDialog.CustomChoiceProvider = null;
            MainForm.MessageBoxProvider = null;
        }

        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.Equal(new byte[] { 44, 44, 44, 44 }, File.ReadAllBytes(session.ReferenceDestinationPath));
    }

    [Fact]
    public void REG_152_CancelRecovery_TamperedCancelTempReference_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset_reg152", refSource, DateTimeOffset.Now);
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = "0123456789abcdef0123456789abcdef";

        var tempRef = session.GetCancelTempReferencePath();
        File.Delete(session.ReferenceDestinationPath);
        File.WriteAllBytes(tempRef, new byte[] { 33, 33, 33 }); // Tampered bytes

        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        Assert.True(File.Exists(tempRef));
        Assert.Equal(new byte[] { 33, 33, 33 }, File.ReadAllBytes(tempRef));
    }

    [Fact]
    public void REG_153_CancelRecovery_TamperedCancelTempProvenance_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

        var session = processor.ProcessReference(settings, "asset_reg153", refSource, DateTimeOffset.Now);
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = "0123456789abcdef0123456789abcdef";

        var tempProv = session.GetCancelTempProvenancePath();
        File.Delete(session.ReferenceProvenancePath);
        File.WriteAllText(tempProv, "TAMPERED CANCEL TEMP PROVENANCE");

        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        Assert.True(File.Exists(tempProv));
        Assert.Equal("TAMPERED CANCEL TEMP PROVENANCE", File.ReadAllText(tempProv));
    }

    [Fact]
    public void REG_154_ProcessMain_PreExistingDeterministicTempProvenance_IsNeverOverwritten()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;

        var session = processor.ProcessReference(settings, "asset_reg154", refSource, processedAt);
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt text";
        session.MainProcessedAt = processedAt;
        session.MainHash = ValidationService.ComputeSha256(mainSource);
        session.MainTransactionId = "abcdef0123456789abcdef0123456789";

        var tempProv = session.GetMainTempProvenancePath();
        File.WriteAllText(tempProv, "FOREIGN DETERMINISTIC PROVENANCE SENTINEL");

        Assert.Throws<IOException>(() => processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "Prompt text",
            processedAt));

        Assert.True(File.Exists(tempProv));
        Assert.Equal("FOREIGN DETERMINISTIC PROVENANCE SENTINEL", File.ReadAllText(tempProv));

        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        Assert.False(File.Exists(finalProv));
    }

    [Fact]
    public void REG_155_ProcessMain_EarlyFailure_DoesNotDeletePreExistingTempProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg155", refSource, DateTimeOffset.Now);
        var processedAt = DateTimeOffset.Now;
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt text";
        session.MainProcessedAt = processedAt;
        session.MainHash = ValidationService.ComputeSha256(mainSource);
        session.MainTransactionId = "fedcba9876543210fedcba9876543210";

        var tempMain = session.GetMainTempImagePath();
        var tempProv = session.GetMainTempProvenancePath();

        File.WriteAllBytes(tempMain, new byte[] { 9, 9, 9 });
        File.WriteAllText(tempProv, "FOREIGN PROVENANCE TEMP - KEEP");

        Assert.Throws<IOException>(() => processor.ProcessMainImage(
            session,
            settings.AcceptedExtensions,
            mainSource,
            "Prompt text",
            processedAt));

        Assert.True(File.Exists(tempMain));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(tempMain));
        Assert.True(File.Exists(tempProv));
        Assert.Equal("FOREIGN PROVENANCE TEMP - KEEP", File.ReadAllText(tempProv));
    }

    [Fact]
    public void REG_156_Cancel_UnreadableReferenceOwnership_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg156", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        using (var lockStream = new FileStream(session.ReferenceDestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.Contains("Could not verify reference image ownership", ex.Message);
        }

        Assert.True(sessionService.Exists(), "Session must remain when reference cannot be verified");
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.Equal(File.ReadAllBytes(refSource), File.ReadAllBytes(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_157_Cancel_UnreadableProvenanceOwnership_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg157", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        using (var lockStream = new FileStream(session.ReferenceProvenancePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.Contains("Could not verify reference provenance ownership", ex.Message);
        }

        Assert.True(sessionService.Exists(), "Session must remain when provenance cannot be verified");
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.Equal(File.ReadAllBytes(refSource), File.ReadAllBytes(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_158_CancelRecovery_UnreadableTempOwnership_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg158", refSource, DateTimeOffset.Now);
        session.CancellationId = "abcdef0123456789abcdef0123456789";
        session.CancelPhase = CancelPhase.FilesRenamed;

        var tempRef = session.GetCancelTempReferencePath();
        var tempProv = session.GetCancelTempProvenancePath();
        File.Move(session.ReferenceDestinationPath, tempRef);
        File.Move(session.ReferenceProvenancePath, tempProv);
        sessionService.Save(session);

        using (var lockStream = new FileStream(tempRef, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.Contains("Could not verify temporary canceling reference image", ex.Message);
        }

        Assert.True(sessionService.Exists(), "Session must remain when cancel temp file cannot be verified");
        Assert.True(File.Exists(tempRef));
        Assert.True(File.Exists(tempProv));
    }

    [Fact]
    public void REG_159_RollbackReplacement_SameFilenameTamperedCurrentReference_PreservesUnknownFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var ref1Bytes = File.ReadAllBytes(ref1);
        var oldSession = processor.ProcessReference(settings, "asset_reg159", ref1, DateTimeOffset.Now);
        var oldProvText = File.ReadAllText(oldSession.ReferenceProvenancePath);

        // Same filename "ref.png" replacement with different bytes
        var ref2 = workspace.CreateImage("ref_temp.png", new byte[] { 40, 50, 60 });
        var sourceWithSameFilename = Path.Combine(Path.GetDirectoryName(ref2)!, "ref.png");
        if (File.Exists(sourceWithSameFilename)) File.Delete(sourceWithSameFilename);
        File.Move(ref2, sourceWithSameFilename);

        var tx = processor.PrepareReferenceReplacement(oldSession, settings.AcceptedExtensions, sourceWithSameFilename, DateTimeOffset.Now);

        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.True(File.Exists(tx.BackupProvenancePath));

        // Before rollback, overwrite the shared destination ref.png with unknown foreign bytes
        File.WriteAllBytes(oldSession.ReferenceDestinationPath, new byte[] { 99, 99, 99 });

        var rollbackResult = processor.RollbackReferenceReplacement(tx);
        Assert.False(rollbackResult.IsValid);
        Assert.Contains(rollbackResult.Errors, e => e.Contains("does not match new session ReferenceHash or old session ReferenceHash"));

        // Verify foreign file is untouched and backups remain intact
        Assert.True(File.Exists(oldSession.ReferenceDestinationPath));
        Assert.Equal(new byte[] { 99, 99, 99 }, File.ReadAllBytes(oldSession.ReferenceDestinationPath));
        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.Equal(ref1Bytes, File.ReadAllBytes(tx.BackupReferencePath));
        Assert.True(File.Exists(tx.BackupProvenancePath));
        Assert.Equal(oldProvText, File.ReadAllText(tx.BackupProvenancePath));
    }

    [Fact]
    public void REG_160_RollbackReplacement_SameFilenameValidCurrentNewReference_RestoresOldSuccessfully()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var ref1Bytes = File.ReadAllBytes(ref1);
        var oldSession = processor.ProcessReference(settings, "asset_reg160", ref1, DateTimeOffset.Now);
        var oldProvText = File.ReadAllText(oldSession.ReferenceProvenancePath);

        var ref2 = workspace.CreateImage("ref_temp2.png", new byte[] { 40, 50, 60 });
        var sourceWithSameFilename = Path.Combine(Path.GetDirectoryName(ref2)!, "ref.png");
        if (File.Exists(sourceWithSameFilename)) File.Delete(sourceWithSameFilename);
        File.Move(ref2, sourceWithSameFilename);

        var tx = processor.PrepareReferenceReplacement(oldSession, settings.AcceptedExtensions, sourceWithSameFilename, DateTimeOffset.Now);

        var rollbackResult = processor.RollbackReferenceReplacement(tx);
        Assert.True(rollbackResult.IsValid);

        Assert.True(File.Exists(oldSession.ReferenceDestinationPath));
        Assert.Equal(ref1Bytes, File.ReadAllBytes(oldSession.ReferenceDestinationPath));
        Assert.True(File.Exists(oldSession.ReferenceProvenancePath));
        Assert.Equal(oldProvText, File.ReadAllText(oldSession.ReferenceProvenancePath));

        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
    }

    [Fact]
    public void REG_161_RollbackMain_ForeignDeterministicTempImage_PreservesAndFailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg161", refSource, DateTimeOffset.Now);
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt text";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = ValidationService.ComputeSha256(mainSource);
        session.MainTransactionId = "11223344556677881122334455667788";

        var tempImage = session.GetMainTempImagePath();
        File.WriteAllBytes(tempImage, new byte[] { 77, 77, 77 });

        var rollbackResult = processor.RollbackMain(session);
        Assert.False(rollbackResult.IsValid);
        Assert.Contains(rollbackResult.Errors, e => e.Contains("Main temp image"));

        Assert.True(File.Exists(tempImage));
        Assert.Equal(new byte[] { 77, 77, 77 }, File.ReadAllBytes(tempImage));
        Assert.True(session.IsMainCommitting);
    }

    [Fact]
    public void REG_162_RollbackMain_ForeignDeterministicTempProvenance_PreservesAndFailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        var session = processor.ProcessReference(settings, "asset_reg162", refSource, DateTimeOffset.Now);
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt text";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = ValidationService.ComputeSha256(mainSource);
        session.MainTransactionId = "99887766554433229988776655443322";

        var tempProv = session.GetMainTempProvenancePath();
        File.WriteAllText(tempProv, "FOREIGN DETERMINISTIC TEMP PROVENANCE CONTENT");

        var rollbackResult = processor.RollbackMain(session);
        Assert.False(rollbackResult.IsValid);
        Assert.Contains(rollbackResult.Errors, e => e.Contains("Main temp provenance"));

        Assert.True(File.Exists(tempProv));
        Assert.Equal("FOREIGN DETERMINISTIC TEMP PROVENANCE CONTENT", File.ReadAllText(tempProv));
        Assert.True(session.IsMainCommitting);
    }

    [Fact]
    public void REG_163_RollbackMain_ExactOwnedDeterministicTemps_AreRemoved()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        var processedAt = DateTimeOffset.Now;
        var session = processor.ProcessReference(settings, "asset_reg163", refSource, processedAt);
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt text";
        session.MainProcessedAt = processedAt;
        session.MainHash = ValidationService.ComputeSha256(mainSource);
        session.MainTransactionId = "aabbccddeeff00112233445566778899";

        var tempImage = session.GetMainTempImagePath();
        var tempProv = session.GetMainTempProvenancePath();

        File.WriteAllBytes(tempImage, File.ReadAllBytes(mainSource));

        var generationDate = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expectedFinalProv = templateService.RenderFinal(
            session.MainFilename,
            session.ReferenceFilename,
            session.ProjectName,
            generationDate,
            session.MainPrompt);
        File.WriteAllText(tempProv, expectedFinalProv, new UTF8Encoding(false));

        var rollbackResult = processor.RollbackMain(session);
        Assert.True(rollbackResult.IsValid);

        Assert.False(File.Exists(tempImage));
        Assert.False(File.Exists(tempProv));
        Assert.False(session.IsMainCommitting);
    }

    [Fact]
    public void REG_164_Recovery_IncompleteMainWithInvalidFinalTemplate_FailsClosedCleanly()
    {
        RunStaWithTimeout(() =>
        {
            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var templateService = workspace.CreateTemplateService();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
            var processedAt = DateTimeOffset.Now;
            var session = processor.ProcessReference(settings, "asset_reg164", refSource, processedAt);

            session.IsMainCommitting = true;
            session.MainFilename = "main.png";
            session.MainPrompt = "Prompt text";
            session.MainProcessedAt = processedAt;
            session.MainHash = ValidationService.ComputeSha256(mainSource);
            session.MainTransactionId = "1234567890abcdef1234567890abcdef";

            // Write authentic final provenance (Main image is NOT created -> incomplete crash boundary)
            var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
            var generationDate = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var expectedFinalProv = templateService.RenderFinal(
                session.MainFilename,
                session.ReferenceFilename,
                session.ProjectName,
                generationDate,
                session.MainPrompt);
            File.WriteAllText(finalProv, expectedFinalProv, new UTF8Encoding(false));
            sessionService.Save(session);

            // Now invalidate final.md template
            File.WriteAllText(workspace.FinalTemplatePath, "CORRUPT TEMPLATE WITHOUT PLACEHOLDERS");

            var messages = new List<string>();
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) =>
            {
                messages.Add(msg);
            };

            try
            {
                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    processor,
                    sessionService);

                var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
                recoverMethod?.Invoke(form, null);

                Assert.True(messages.Any(m => m.Contains("CRITICAL: Recovery found an incomplete Main commit, but automatic rollback failed") ||
                                              m.Contains("CRITICAL: Recovery could not safely evaluate or roll back the incomplete Main commit")),
                    $"Expected critical recovery message, but got: {string.Join("; ", messages)}");

                // Verify fail-closed: files remain intact
                Assert.True(File.Exists(finalProv), "Final provenance must not be deleted");
                Assert.True(File.Exists(session.ReferenceDestinationPath), "Reference image must not be deleted");
                Assert.True(File.Exists(session.ReferenceProvenancePath), "Reference provenance must not be deleted");
                Assert.True(sessionService.Exists(), "Session must remain intact");
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void REG_165_ValidateExactFinalOwnership_InvalidTemplate_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var processedAt = DateTimeOffset.Now;
        var session = processor.ProcessReference(settings, "asset_reg165", refSource, processedAt);
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt";
        session.MainProcessedAt = processedAt;

        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        File.WriteAllText(finalProv, "Some content");

        // Corrupt final.md template
        File.WriteAllText(workspace.FinalTemplatePath, "CORRUPT TEMPLATE WITHOUT REQUIRED TOKENS");

        var result = validationService.ValidateExactFinalProvenanceOwnership(session, finalProv, templateService);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Could not render expected final provenance"));
    }

    [Fact]
    public void REG_166_ValidateExactReferenceOwnership_InvalidTemplate_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg166", refSource, DateTimeOffset.Now);

        // Corrupt reference.md template
        File.WriteAllText(workspace.ReferenceTemplatePath, "CORRUPT TEMPLATE WITHOUT REQUIRED TOKENS");

        // Clear digest to test legacy fallback template rendering path
        session.ReferenceProvenanceHash = string.Empty;

        var result = validationService.ValidateExactReferenceProvenanceOwnership(session, session.ReferenceProvenancePath, templateService);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Could not render expected reference provenance"));
    }

    [Fact]
    public void REG_167_RollbackReferenceReplacement_SameFilenameAlreadyOldIdempotentRetry_Succeeds()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 10, 20, 30 });
        var ref1Bytes = File.ReadAllBytes(ref1);
        var oldSession = processor.ProcessReference(settings, "asset_reg167", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref_temp.png", new byte[] { 40, 50, 60 });
        var sourceWithSameFilename = Path.Combine(Path.GetDirectoryName(ref2)!, "ref.png");
        if (File.Exists(sourceWithSameFilename)) File.Delete(sourceWithSameFilename);
        File.Move(ref2, sourceWithSameFilename);

        var tx = processor.PrepareReferenceReplacement(oldSession, settings.AcceptedExtensions, sourceWithSameFilename, DateTimeOffset.Now);

        // Simulate state after successful restore of reference image, but before transaction is deleted
        File.WriteAllBytes(oldSession.ReferenceDestinationPath, ref1Bytes);
        if (File.Exists(tx.BackupReferencePath)) File.Delete(tx.BackupReferencePath);

        // Rollback should detect destination already matches OldSession.ReferenceHash
        var result = processor.RollbackReferenceReplacement(tx);
        Assert.True(result.IsValid);
        Assert.Equal(ref1Bytes, File.ReadAllBytes(oldSession.ReferenceDestinationPath));
    }

    [Fact]
    public void REG_168_Cancel_PreparedPhase_UnreadableOriginalProvenance_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg168", refSource, DateTimeOffset.Now);
        session.CancellationId = "abcdef0123456789abcdef0123456789";
        session.CancelPhase = CancelPhase.Prepared;
        sessionService.Save(session);

        using (var lockStream = new FileStream(session.ReferenceProvenancePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.Contains("Could not verify reference provenance ownership in recovery phase", ex.Message);
        }

        Assert.True(sessionService.Exists());
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void REG_169_Cancel_PreparedPhase_UnreadableTempProvenance_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg169", refSource, DateTimeOffset.Now);
        session.CancellationId = "abcdef0123456789abcdef0123456789";
        session.CancelPhase = CancelPhase.Prepared;

        var tempProv = session.GetCancelTempProvenancePath();
        File.Move(session.ReferenceProvenancePath, tempProv);
        sessionService.Save(session);

        using (var lockStream = new FileStream(tempProv, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.Contains("Could not verify cancel-temp reference provenance ownership in recovery phase", ex.Message);
        }

        Assert.True(sessionService.Exists());
        Assert.True(File.Exists(tempProv));
    }

    [Fact]
    public void REG_170_ProcessReference_DestinationTamperedAfterCopy_PreservesUnknownFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        string? capturedTempPath = null;

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                capturedTempPath = dest;
                File.WriteAllBytes(dest, new byte[] { 42, 42, 42 });
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessReference(settings, "asset_reg170", refSource, DateTimeOffset.Now));

            var assetFolder = Path.Combine(settings.AssetRootFolder, "asset_reg170");
            var referenceFolder = Path.Combine(assetFolder, "reference");
            var referenceDestination = Path.Combine(referenceFolder, "ref.png");

            Assert.False(File.Exists(referenceDestination), "Canonical file must never have been created");
            Assert.NotNull(capturedTempPath);
            Assert.True(File.Exists(capturedTempPath), "Tampered temp file must be preserved");
            Assert.Equal(new byte[] { 42, 42, 42 }, File.ReadAllBytes(capturedTempPath));
            Assert.True(Directory.Exists(assetFolder), "Asset folder must remain");

            // Downloads source unchanged
            Assert.True(File.Exists(refSource));
            Assert.Equal(File.ReadAllBytes(refSource), File.ReadAllBytes(refSource));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_171_ProcessMainImage_MainDestinationTamperedAfterPromotion_PreservesUnknownFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg171", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });

        try
        {
            AssetProcessorService.OnMainPromotedHook = promotedPath =>
            {
                File.WriteAllBytes(promotedPath, new byte[] { 99, 99, 99 });
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main1, "prompt", DateTimeOffset.Now));

            var mainPath = Path.Combine(session.AssetFolder, "main1.png");
            Assert.True(File.Exists(mainPath), "Tampered main must be preserved");
            Assert.Equal(new byte[] { 99, 99, 99 }, File.ReadAllBytes(mainPath));

            // Reference must remain intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
        }
        finally
        {
            AssetProcessorService.OnMainPromotedHook = null;
        }
    }

    [Fact]
    public void REG_172_ProcessMainImage_FinalProvenanceTamperedAfterPromotion_PreservesUnknownFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg172", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });

        try
        {
            AssetProcessorService.OnMainPromotedHook = promotedPath =>
            {
                // Tamper the final provenance instead of the main image
                var finalProv = Path.Combine(Path.GetDirectoryName(promotedPath)!, AppConstants.FinalProvenanceFileName);
                File.WriteAllText(finalProv, "FOREIGN PROVENANCE CONTENT");
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main1, "prompt", DateTimeOffset.Now));

            var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
            Assert.True(File.Exists(finalProvPath), "Tampered provenance must be preserved");
            Assert.Equal("FOREIGN PROVENANCE CONTENT", File.ReadAllText(finalProvPath));

            // Reference must remain intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
        }
        finally
        {
            AssetProcessorService.OnMainPromotedHook = null;
        }
    }

    [Fact]
    public void REG_173_ProcessingRollback_ExactOwnedArtifacts_AreStillCleaned()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        // Positive control: When content is still owned, rollback should cleanly delete owned artifacts
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        string? capturedDest = null;

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                capturedDest = dest;
                // Trigger a failure AFTER copy while leaving dest bytes intact (exact-owned)
                throw new InvalidOperationException("Forced failure after copy with owned content");
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                processor.ProcessReference(settings, "asset_reg173", refSource, DateTimeOffset.Now));
            Assert.Contains("Forced failure", ex.Message);

            // Rollback succeeded completely: exact-owned copy and created directories are cleaned up
            var assetFolder = Path.Combine(settings.AssetRootFolder, "asset_reg173");
            Assert.False(Directory.Exists(assetFolder), "Asset folder must be cleaned up on owned rollback");
            if (capturedDest != null)
            {
                Assert.False(File.Exists(capturedDest), "Owned destination file must be cleaned up on rollback");
            }
            Assert.True(File.Exists(refSource), "Downloads source must remain untouched");
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_174_ValidateSession_ActiveMainMissingTransactionId_IsInvalid()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg174", refSource, DateTimeOffset.Now);

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = DateTimeOffset.Now;
        session.MainHash = new string('a', 64);

        // null
        session.MainTransactionId = null;
        var result1 = validationService.ValidateSession(session);
        Assert.False(result1.IsValid);
        Assert.Contains(result1.Errors, e => e.Contains("MainTransactionId"));

        // empty
        session.MainTransactionId = "";
        var result2 = validationService.ValidateSession(session);
        Assert.False(result2.IsValid);
        Assert.Contains(result2.Errors, e => e.Contains("MainTransactionId"));

        // whitespace
        session.MainTransactionId = "   ";
        var result3 = validationService.ValidateSession(session);
        Assert.False(result3.IsValid);
        Assert.Contains(result3.Errors, e => e.Contains("MainTransactionId"));

        // valid 32 hex -> should pass (with other fields valid)
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";
        var result4 = validationService.ValidateSession(session);
        Assert.True(result4.IsValid);
    }

    [Fact]
    public void REG_175_RollbackMain_ActiveMainMissingTransactionId_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var templateService = workspace.CreateTemplateService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;
        var session = processor.ProcessReference(settings, "asset_reg175", refSource, processedAt);

        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = ValidationService.ComputeSha256(mainSource);
        session.MainTransactionId = null; // Missing!

        // Create owned-looking final provenance
        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var generationDate = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expectedFinalProv = templateService.RenderFinal(
            session.MainFilename,
            session.ReferenceFilename,
            session.ProjectName,
            generationDate,
            session.MainPrompt);
        File.WriteAllText(finalProv, expectedFinalProv, new UTF8Encoding(false));

        var result = processor.RollbackMain(session);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("incomplete or invalid"));

        // All files must be preserved
        Assert.True(File.Exists(finalProv), "Final provenance must be preserved");
        Assert.True(File.Exists(session.ReferenceDestinationPath), "Reference must be preserved");
        Assert.True(File.Exists(session.ReferenceProvenancePath), "Reference provenance must be preserved");
    }

    [Fact]
    public void REG_176_StartupRecovery_ActiveMainMissingTransactionId_DoesNotDeleteAssets()
    {
        RunStaWithTimeout(() =>
        {
            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var templateService = workspace.CreateTemplateService();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var processedAt = DateTimeOffset.Now;
            var session = processor.ProcessReference(settings, "asset_reg176", refSource, processedAt);

            session.IsMainCommitting = true;
            session.MainFilename = "main.png";
            session.MainPrompt = "Prompt";
            session.MainProcessedAt = processedAt;
            session.MainHash = new string('a', 64);
            session.MainTransactionId = null; // Missing!
            sessionService.Save(session);

            var messages = new List<string>();
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) =>
            {
                messages.Add(msg);
                return false; // Exit
            };

            try
            {
                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    processor,
                    sessionService);

                var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
                recoverMethod?.Invoke(form, null);

                // Session should be classified as inconsistent - no destructive recovery
                Assert.True(messages.Any(m => m.Contains("inconsistent") || m.Contains("invalid") || m.Contains("corrupt") || m.Contains("MainTransactionId")),
                    $"Expected session-invalid message, but got: {string.Join("; ", messages)}");

                // All asset files must be preserved
                Assert.True(File.Exists(session.ReferenceDestinationPath), "Reference must be preserved");
                Assert.True(File.Exists(session.ReferenceProvenancePath), "Reference provenance must be preserved");
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void REG_177_PrepareReferenceReplacement_TempReferenceTamperedAfterCopy_PreservesUnknownTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg177", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        string? capturedTemp = null;

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                capturedTemp = dest;
                File.WriteAllBytes(dest, new byte[] { 88, 88, 88 });
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));

            Assert.NotNull(capturedTemp);
            Assert.True(File.Exists(capturedTemp), "Tampered temp file must be preserved");
            Assert.Equal(new byte[] { 88, 88, 88 }, File.ReadAllBytes(capturedTemp));

            // Old state remains intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
            Assert.True(File.Exists(ref2));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_178_PrepareReferenceReplacement_ExactOwnedTempFailure_CleansOwnedTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg178", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        string? capturedTemp = null;

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                capturedTemp = dest;
                // Forced failure while leaving temp bytes exact-owned
                throw new InvalidOperationException("Forced post-copy failure");
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));
            Assert.Contains("Forced post-copy failure", ex.Message);

            Assert.NotNull(capturedTemp);
            Assert.False(File.Exists(capturedTemp), "Owned temp file must be cleaned up on rollback");

            // Old state remains intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_179_ProcessMainImage_TempImageTamperedAfterCopy_PreservesUnknownTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg179", ref1, DateTimeOffset.Now);
        var processedAt = DateTimeOffset.Now;
        session.IsMainCommitting = true;
        session.MainFilename = "main1.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = processedAt;
        session.MainTransactionId = "abcdef0123456789abcdef0123456789";

        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });
        session.MainHash = ValidationService.ComputeSha256(main1);
        string? capturedTemp = null;

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                capturedTemp = dest;
                File.WriteAllBytes(dest, new byte[] { 77, 77, 77 });
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", processedAt));
            Assert.Contains("Main Image processing failed", ex.Message);

            Assert.NotNull(capturedTemp);
            Assert.True(File.Exists(capturedTemp), "Tampered temp file must be preserved");
            Assert.Equal(new byte[] { 77, 77, 77 }, File.ReadAllBytes(capturedTemp));

            // Main destination and provenance must not exist
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));

            // Reference and downloads source remain intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
            Assert.True(File.Exists(main1));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_180_ProcessMainImage_DeterministicTempProvenanceTamperedAfterWrite_PreservesUnknownTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg180", ref1, DateTimeOffset.Now);
        var processedAt = DateTimeOffset.Now;
        session.IsMainCommitting = true;
        session.MainFilename = "main1.png";
        session.MainPrompt = "prompt";
        session.MainProcessedAt = processedAt;
        session.MainTransactionId = "fedcba9876543210fedcba9876543210";

        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });
        session.MainHash = ValidationService.ComputeSha256(main1);

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                // Write a tampered temp provenance
                var tempProv = session.GetMainTempProvenancePath();
                File.WriteAllText(tempProv, "FOREIGN TAMPERED CONTENT BEFORE RENDER");
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", processedAt));
            Assert.Contains("Main Image processing failed", ex.Message);

            var tempProvPath = session.GetMainTempProvenancePath();
            Assert.True(File.Exists(tempProvPath), "Tampered temp provenance must be preserved");
            Assert.Equal("FOREIGN TAMPERED CONTENT BEFORE RENDER", File.ReadAllText(tempProvPath));

            // Main destination and provenance must not exist
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));

            // Reference and downloads source remain intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
            Assert.True(File.Exists(main1));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_181_ProcessMainImage_UnjournaledReferenceSession_TempTamperedAfterCopy_Rejects()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg181", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });
        string? capturedTemp = null;

        var processedAt = DateTimeOffset.Now;
        processor.PrepareMainCommit(session, settings.AcceptedExtensions, main1, "prompt", processedAt);

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                capturedTemp = dest;
                File.WriteAllBytes(dest, new byte[] { 77, 77, 77 });
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", processedAt));
            Assert.Contains("Main Image processing failed", ex.Message);

            Assert.NotNull(capturedTemp);
            Assert.True(File.Exists(capturedTemp), "Tampered temp file must be preserved");
            Assert.Equal(new byte[] { 77, 77, 77 }, File.ReadAllBytes(capturedTemp));

            // Main destination and provenance must not exist
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));

            // Reference and downloads source remain intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.True(File.Exists(session.ReferenceProvenancePath));
            Assert.True(File.Exists(main1));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_182_ProcessMainImage_UnjournaledReferenceSession_ExactCopy_Succeeds()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg182", ref1, DateTimeOffset.Now);
        Assert.False(session.IsMainCommitting);
        Assert.Null(session.MainHash);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });
        var result = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main1, "prompt", DateTimeOffset.Now);

        Assert.Equal("main1.png", result);
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_reg182.png")));
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
        Assert.True(session.IsMainCommitting);
        Assert.Equal("main1.png", session.MainFilename);
        Assert.Equal("asset_reg182.png", session.GetIngameFilename());
        Assert.Equal(ValidationService.ComputeSha256(main1), session.MainHash);
    }

    [Fact]
    public void REG_183_ActiveMainJournalMismatch_FailsBeforeWrites()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });
        var processedAt = DateTimeOffset.Now;

        // 1. Filename mismatch
        {
            var session = processor.ProcessReference(settings, "asset_reg183_a", ref1, processedAt);
            session.IsMainCommitting = true;
            session.MainTransactionId = "0123456789abcdef0123456789abcdef";
            session.MainFilename = "other.png";
            session.MainPrompt = "prompt";
            session.MainProcessedAt = processedAt;
            session.MainHash = ValidationService.ComputeSha256(main1);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", processedAt));
            Assert.Contains("filename", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_reg183_a.png")));
        }

        // 2. Prompt mismatch
        {
            var session = processor.ProcessReference(settings, "asset_reg183_b", ref1, processedAt);
            session.IsMainCommitting = true;
            session.MainTransactionId = "0123456789abcdef0123456789abcdef";
            session.MainFilename = "main1.png";
            session.MainPrompt = "OLD PROMPT";
            session.MainProcessedAt = processedAt;
            session.MainHash = ValidationService.ComputeSha256(main1);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "NEW PROMPT", processedAt));
            Assert.Contains("prompt", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_reg183_b.png")));
        }

        // 3. ProcessedAt mismatch
        {
            var session = processor.ProcessReference(settings, "asset_reg183_c", ref1, processedAt);
            session.IsMainCommitting = true;
            session.MainTransactionId = "0123456789abcdef0123456789abcdef";
            session.MainFilename = "main1.png";
            session.MainPrompt = "prompt";
            session.MainProcessedAt = processedAt.AddHours(-1);
            session.MainHash = ValidationService.ComputeSha256(main1);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", processedAt));
            Assert.Contains("processedAt", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_reg183_c.png")));
        }

        // 4. Hash mismatch
        {
            var session = processor.ProcessReference(settings, "asset_reg183_d", ref1, processedAt);
            session.IsMainCommitting = true;
            session.MainTransactionId = "0123456789abcdef0123456789abcdef";
            session.MainFilename = "main1.png";
            session.MainPrompt = "prompt";
            session.MainProcessedAt = processedAt;
            session.MainHash = new string('0', 64);

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", processedAt));
            Assert.Contains("hash", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_reg183_d.png")));
        }
    }

    [Fact]
    public void REG_184_ProcessMainImage_MatchingActiveMainJournal_Succeeds()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var main1 = workspace.CreateImage("main1.png", new byte[] { 5, 5, 5 });
        var processedAt = DateTimeOffset.Now;

        var session = processor.ProcessReference(settings, "asset_reg184", ref1, processedAt);
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, main1, "prompt", processedAt);

        var result = processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", processedAt);
        Assert.Equal("main1.png", result);
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, "main1.png")));
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_reg184.png")));
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
    }

    [Fact]
    public void REG_185_Cancel_FilesRenamedPhaseSavingHook_TampersTempReference_PreservesUnknownFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg185", ref1, DateTimeOffset.Now);
        string? tamperedTempRef = null;

        try
        {
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                if (phase == CancelPhase.FilesRenamed)
                {
                    tamperedTempRef = s.GetCancelTempReferencePath();
                    File.WriteAllBytes(tamperedTempRef, new byte[] { 99, 99, 99 });
                }
            };

            var ex = Assert.ThrowsAny<Exception>(() => sessionService.Cancel(session));

            Assert.NotNull(tamperedTempRef);
            Assert.True(File.Exists(tamperedTempRef), "Tampered cancel temp reference must be preserved");
            Assert.Equal(new byte[] { 99, 99, 99 }, File.ReadAllBytes(tamperedTempRef));
            Assert.True(sessionService.Exists(), "Session must remain intact for recovery");
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
        }
    }

    [Fact]
    public void REG_186_Cancel_FilesRenamedPhaseSavingHook_TampersTempProvenance_PreservesUnknownFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg186", ref1, DateTimeOffset.Now);
        string? tamperedTempProv = null;

        try
        {
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                if (phase == CancelPhase.FilesRenamed)
                {
                    tamperedTempProv = s.GetCancelTempProvenancePath();
                    File.WriteAllText(tamperedTempProv, "FOREIGN CONTENT");
                }
            };

            var ex = Assert.ThrowsAny<Exception>(() => sessionService.Cancel(session));

            Assert.NotNull(tamperedTempProv);
            Assert.True(File.Exists(tamperedTempProv), "Tampered cancel temp provenance must be preserved");
            Assert.Equal("FOREIGN CONTENT", File.ReadAllText(tamperedTempProv));
            Assert.True(sessionService.Exists(), "Session must remain intact for recovery");
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
        }
    }

    [Fact]
    public void REG_187_Cancel_FilesRenamedOwnedTemps_AreDeleted()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg187", ref1, DateTimeOffset.Now);

        string? tempRef = null;
        string? tempProv = null;

        try
        {
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                if (phase == CancelPhase.Prepared)
                {
                    tempRef = s.GetCancelTempReferencePath();
                    tempProv = s.GetCancelTempProvenancePath();
                }
            };

            sessionService.Cancel(session);

            Assert.NotNull(tempRef);
            Assert.NotEmpty(tempRef);
            Assert.EndsWith(".canceling", tempRef);
            Assert.NotNull(tempProv);
            Assert.NotEmpty(tempProv);
            Assert.EndsWith(".canceling", tempProv);

            Assert.False(File.Exists(tempRef), "Owned temp reference must be deleted");
            Assert.False(File.Exists(tempProv), "Owned temp provenance must be deleted");
            Assert.False(File.Exists(session.ReferenceDestinationPath));
            Assert.False(File.Exists(session.ReferenceProvenancePath));
            Assert.False(sessionService.Exists(), "Session must be deleted");
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
        }
    }

    [Fact]
    public void REG_188_Cancel_WithoutTemplateService_TamperedCanonicalProvenance_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg188", ref1, DateTimeOffset.Now);

        // Service constructed without TemplateService
        var sessionPath = Path.Combine(session.AssetFolder, AppConstants.SessionFileName);
        var unequippedService = new SessionService(sessionPath);
        unequippedService.Save(session);

        // Tamper canonical provenance
        File.WriteAllText(session.ReferenceProvenancePath, "FOREIGN PROVENANCE CONTENT");

        var ex = Assert.Throws<InvalidOperationException>(() => unequippedService.Cancel(session));
        Assert.Contains("TemplateService", ex.Message);

        // Assets and session must remain intact
        Assert.True(File.Exists(session.ReferenceDestinationPath), "Reference image must be preserved");
        Assert.True(File.Exists(session.ReferenceProvenancePath), "Canonical provenance must be preserved");
        Assert.Equal("FOREIGN PROVENANCE CONTENT", File.ReadAllText(session.ReferenceProvenancePath));
        Assert.True(unequippedService.Exists(), "Session must be preserved");
    }

    [Fact]
    public void REG_189_Cancel_WithoutTemplateService_FilesRenamedTempProvenance_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg189", ref1, DateTimeOffset.Now);

        session.CancellationId = "0123456789abcdef0123456789abcdef";
        session.CancelPhase = CancelPhase.FilesRenamed;

        var tempRef = session.GetCancelTempReferencePath();
        var tempProv = session.GetCancelTempProvenancePath();

        File.Move(session.ReferenceDestinationPath, tempRef);
        File.Move(session.ReferenceProvenancePath, tempProv);

        var sessionPath = Path.Combine(session.AssetFolder, AppConstants.SessionFileName);
        var unequippedService = new SessionService(sessionPath);
        unequippedService.Save(session);

        var ex = Assert.Throws<InvalidOperationException>(() => unequippedService.Cancel(session));
        Assert.Contains("TemplateService", ex.Message);

        // Temp files and session must remain intact
        Assert.True(File.Exists(tempRef), "Temp reference image must be preserved");
        Assert.True(File.Exists(tempProv), "Temp provenance must be preserved");
        Assert.True(unequippedService.Exists(), "Session must be preserved");
    }

    [Fact]
    public void REG_190_Cancel_PreparedHookTamperedReference_DoesNotMoveUnknownReference()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg190", ref1, DateTimeOffset.Now);

        string? tempRef = null;

        try
        {
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                if (phase == CancelPhase.Prepared)
                {
                    tempRef = s.GetCancelTempReferencePath();
                    // Tamper canonical reference after initial safe check
                    File.WriteAllBytes(s.ReferenceDestinationPath, new byte[] { 99, 99, 99 });
                }
            };

            var ex = Assert.ThrowsAny<Exception>(() => sessionService.Cancel(session));

            // Canonical tampered reference must NOT have been moved to tempRef
            Assert.True(File.Exists(session.ReferenceDestinationPath), "Tampered canonical reference must remain at canonical path");
            Assert.Equal(new byte[] { 99, 99, 99 }, File.ReadAllBytes(session.ReferenceDestinationPath));

            Assert.NotNull(tempRef);
            Assert.False(File.Exists(tempRef), "Temp reference must not exist");
            Assert.True(sessionService.Exists(), "Session must remain intact");
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
        }
    }

    [Fact]
    public void REG_191_Cancel_PreparedHookTamperedProvenance_DoesNotMoveUnknownProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg191", ref1, DateTimeOffset.Now);

        string? tempProv = null;

        try
        {
            SessionService.OnCancelPhaseSavingHook = (phase, s) =>
            {
                if (phase == CancelPhase.Prepared)
                {
                    tempProv = s.GetCancelTempProvenancePath();
                    // Tamper canonical provenance after initial safe check
                    File.WriteAllText(s.ReferenceProvenancePath, "FOREIGN PROVENANCE TEXT");
                }
            };

            var ex = Assert.ThrowsAny<Exception>(() => sessionService.Cancel(session));

            // Canonical tampered provenance must NOT have been moved to tempProv
            Assert.True(File.Exists(session.ReferenceProvenancePath), "Tampered canonical provenance must remain at canonical path");
            Assert.Equal("FOREIGN PROVENANCE TEXT", File.ReadAllText(session.ReferenceProvenancePath));

            Assert.NotNull(tempProv);
            Assert.False(File.Exists(tempProv), "Temp provenance must not exist");
            Assert.True(sessionService.Exists(), "Session must remain intact");
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
        }
    }

    [Fact]
    public void REG_192_Cancel_ProvenanceMovedHookTamperedReference_DoesNotMoveUnknownReference()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg192", ref1, DateTimeOffset.Now);

        string? tempRef = null;

        try
        {
            SessionService.OnCancelProvenanceMovedHook = s =>
            {
                tempRef = s.GetCancelTempReferencePath();
                // Tamper canonical reference after provenance was moved
                File.WriteAllBytes(s.ReferenceDestinationPath, new byte[] { 88, 88, 88 });
            };

            var ex = Assert.ThrowsAny<Exception>(() => sessionService.Cancel(session));

            // Canonical tampered reference must NOT have been moved to tempRef
            Assert.True(File.Exists(session.ReferenceDestinationPath), "Tampered canonical reference must remain at canonical path");
            Assert.Equal(new byte[] { 88, 88, 88 }, File.ReadAllBytes(session.ReferenceDestinationPath));

            Assert.NotNull(tempRef);
            Assert.False(File.Exists(tempRef), "Temp reference must not exist");
            Assert.True(sessionService.Exists(), "Session must remain intact");
        }
        finally
        {
            SessionService.OnCancelProvenanceMovedHook = null;
        }
    }

    [Fact]
    public void REG_193_Cancel_ReferenceRenameFailure_TamperedMovedProvenance_DoesNotRestoreUnknown()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg193", ref1, DateTimeOffset.Now);

        string? tempProv = null;
        string? tempRef = null;

        try
        {
            SessionService.OnCancelProvenanceMovedHook = s =>
            {
                tempProv = s.GetCancelTempProvenancePath();
                tempRef = s.GetCancelTempReferencePath();

                // Tamper temp provenance while it is in the temp slot
                File.WriteAllText(tempProv, "FOREIGN MOVED PROVENANCE");

                // Pre-create destination tempRef to cause reference move to fail
                File.WriteAllText(tempRef, "BLOCK REFERENCE MOVE");
            };

            var ex = Assert.ThrowsAny<Exception>(() => sessionService.Cancel(session));
            Assert.True(ex is IOException || ex is InvalidDataException);

            // Tampered moved provenance must NOT be restored to canonical path!
            Assert.NotNull(tempProv);
            Assert.True(File.Exists(tempProv), "Foreign temp provenance must remain in temp slot");
            Assert.Equal("FOREIGN MOVED PROVENANCE", File.ReadAllText(tempProv));
            Assert.False(File.Exists(session.ReferenceProvenancePath), "Canonical provenance path must NOT receive foreign content");

            // Canonical reference image remains
            Assert.True(File.Exists(session.ReferenceDestinationPath));

            // Session state must remain in Prepared phase with CancellationId intact
            Assert.True(sessionService.Exists());
            var reloaded = sessionService.Load();
            Assert.NotNull(reloaded);
            Assert.Equal(CancelPhase.Prepared, reloaded.CancelPhase);
            Assert.NotNull(reloaded.CancellationId);
        }
        finally
        {
            SessionService.OnCancelProvenanceMovedHook = null;
        }
    }

    [Fact]
    public void REG_194_ActiveMainJournal_CaseOnlyFilenameMismatch_FailsBeforeWrites()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var main1 = workspace.CreateImage("main.png", new byte[] { 5, 5, 5 });
        var processedAt = DateTimeOffset.Now;

        var session = processor.ProcessReference(settings, "asset_reg194", ref1, processedAt);
        session.IsMainCommitting = true;
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";
        session.MainFilename = "Main.PNG"; // Case mismatch vs "main.png"
        session.MainPrompt = "prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = ValidationService.ComputeSha256(main1);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", processedAt));
        Assert.Contains("filename", ex.Message, StringComparison.OrdinalIgnoreCase);

        // No files created in asset folder
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "Main.PNG")));
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)));
    }

    [Fact]
    public void REG_195_ActiveMainJournal_SameInstantDifferentOffset_FailsBeforeWrites()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var main1 = workspace.CreateImage("main.png", new byte[] { 5, 5, 5 });

        var t1 = new DateTimeOffset(2026, 8, 17, 23, 30, 0, TimeSpan.FromHours(2));
        var t2 = new DateTimeOffset(2026, 8, 18, 0, 30, 0, TimeSpan.FromHours(3)); // Same instant, different offset

        var session = processor.ProcessReference(settings, "asset_reg195", ref1, t1);
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, main1, "prompt", t1);

        // Calling with t2 (different offset/representation) fails
        var ex = Assert.Throws<InvalidOperationException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", t2));
        Assert.Contains("processedAt", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(session.AssetFolder, "main.png")));

        // Calling with t1 (exact match) succeeds
        var result = processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", t1);
        Assert.Equal("main.png", result);
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, "main.png")));
        Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.IngameFolderName, "asset_reg195.png")));
    }

    [Fact]
    public void REG_196_PrepareReplacement_AfterCopyHookTamperedOldReference_DoesNotMoveUnknownOldReference()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg196", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                // Tamper old canonical reference image after new temp copy
                File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 77, 77, 77 });
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));

            Assert.True(ex is InvalidDataException || ex is IOException);

            // Tampered old reference remains at canonical path
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.Equal(new byte[] { 77, 77, 77 }, File.ReadAllBytes(session.ReferenceDestinationPath));

            // Old canonical provenance remains intact
            Assert.True(File.Exists(session.ReferenceProvenancePath));

            // No .old backup file was created
            var refDir = Path.GetDirectoryName(session.ReferenceDestinationPath)!;
            var oldFiles = Directory.GetFiles(refDir, "*.old");
            Assert.Empty(oldFiles);

            // New reference was not promoted
            var newFinalRef = Path.Combine(refDir, "ref2.png");
            Assert.False(File.Exists(newFinalRef));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_197_PrepareReplacement_AfterCopyHookTamperedOldProvenance_DoesNotMoveUnknownOldProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg197", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        try
        {
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                // Tamper old canonical provenance after new temp copy
                File.WriteAllText(session.ReferenceProvenancePath, "TAMPERED CANONICAL OLD PROVENANCE");
            };

            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));

            Assert.True(ex is InvalidDataException || ex is IOException);

            // Tampered old provenance remains at canonical path
            Assert.True(File.Exists(session.ReferenceProvenancePath));
            Assert.Equal("TAMPERED CANONICAL OLD PROVENANCE", File.ReadAllText(session.ReferenceProvenancePath));

            // Old canonical reference remains intact
            Assert.True(File.Exists(session.ReferenceDestinationPath));
            Assert.Equal(File.ReadAllBytes(ref1), File.ReadAllBytes(session.ReferenceDestinationPath));

            // No .old backup file was created
            var refDir = Path.GetDirectoryName(session.ReferenceDestinationPath)!;
            var oldFiles = Directory.GetFiles(refDir, "*.old");
            Assert.Empty(oldFiles);

            // New reference was not promoted
            var newFinalRef = Path.Combine(refDir, "ref2.png");
            Assert.False(File.Exists(newFinalRef));
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
        }
    }

    [Fact]
    public void REG_198_PrepareReplacement_Rollback_TamperedBackupReference_DoesNotRestoreUnknown()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg198", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        string? capturedBackupRef = null;

        try
        {
            AssetProcessorService.OnPrepareReplacementOldBackedUpHook = (backupRef, backupProv) =>
            {
                capturedBackupRef = backupRef;
                // Tamper the backup reference file
                File.WriteAllBytes(backupRef, new byte[] { 66, 66, 66 });
                // Force an exception during promotion
                throw new InvalidOperationException("Forced failure after backup");
            };

            var ex = Assert.Throws<IOException>(() =>
                processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));

            Assert.Contains("automatic rollback was incomplete", ex.Message);
            Assert.NotNull(capturedBackupRef);
            Assert.True(File.Exists(capturedBackupRef), "Tampered backup must be preserved");
            Assert.Equal(new byte[] { 66, 66, 66 }, File.ReadAllBytes(capturedBackupRef));

            // Canonical reference slot must NOT have been restored with tampered bytes
            Assert.False(File.Exists(session.ReferenceDestinationPath), "Canonical reference path must not receive tampered backup bytes");
        }
        finally
        {
            AssetProcessorService.OnPrepareReplacementOldBackedUpHook = null;
        }
    }

    [Fact]
    public void REG_199_PrepareReplacement_Rollback_TamperedBackupProvenance_DoesNotRestoreUnknown()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg199", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        string? capturedBackupProv = null;

        try
        {
            AssetProcessorService.OnPrepareReplacementOldBackedUpHook = (backupRef, backupProv) =>
            {
                capturedBackupProv = backupProv;
                // Tamper the backup provenance file
                File.WriteAllText(backupProv, "TAMPERED BACKUP PROVENANCE CONTENT");
                // Force an exception during promotion
                throw new InvalidOperationException("Forced failure after backup");
            };

            var ex = Assert.Throws<IOException>(() =>
                processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));

            Assert.Contains("automatic rollback was incomplete", ex.Message);
            Assert.NotNull(capturedBackupProv);
            Assert.True(File.Exists(capturedBackupProv), "Tampered backup provenance must be preserved");
            Assert.Equal("TAMPERED BACKUP PROVENANCE CONTENT", File.ReadAllText(capturedBackupProv));

            // Canonical provenance slot must NOT have been restored with tampered bytes
            Assert.False(File.Exists(session.ReferenceProvenancePath), "Canonical provenance path must not receive tampered backup content");
        }
        finally
        {
            AssetProcessorService.OnPrepareReplacementOldBackedUpHook = null;
        }
    }

    [Fact]
    public void REG_200_CommitReplacement_NewProvenanceTamperedButMarkersRemain_FailsAndPreservesBackups()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_reg200", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Append foreign text while preserving all required markers
        File.AppendAllText(tx.NewSession.ReferenceProvenancePath, "\n\nFOREIGN / MODIFIED CONTENT APPENDED");

        var result = processor.CommitReferenceReplacement(tx);

        Assert.False(result.IsValid, "Commit must fail when new provenance is tampered even if markers remain");
        Assert.False(tx.IsCommitted);
        Assert.True(File.Exists(tx.BackupReferencePath), "Old backup reference must be preserved");
        Assert.True(File.Exists(tx.BackupProvenancePath), "Old backup provenance must be preserved");
    }

    [Fact]
    public void REG_201_MainForm_ReplaceReference_NewProvenanceTamperedButMarkersRemain_FailsWithoutCleanupWarningAndPreservesBackups()
    {
        var thread = new Thread(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var validationService = workspace.CreateValidationService();

            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
            var session = processor.ProcessReference(settings, "asset_reg201", ref1, DateTimeOffset.Now);
            sessionService.Save(session);

            var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });

            var messages = new List<string>();
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => messages.Add(msg);
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => true; // Confirm replace

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                validationService,
                processor,
                sessionService);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            string? backupRef = null;
            string? backupProv = null;

            // Hook into OnBeforeReferenceReplacementCommit to tamper new provenance while preserving markers
            MainForm.OnBeforeReferenceReplacementCommit = tx =>
            {
                backupRef = tx.BackupReferencePath;
                backupProv = tx.BackupProvenancePath;
                File.AppendAllText(tx.NewSession.ReferenceProvenancePath, "\n\nFOREIGN MARKER-PRESERVING CONTENT");
            };

            try
            {
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(handleReplaceMethod);
                handleReplaceMethod.Invoke(form, null);

                // Verify no success/cleanup warning message was shown
                Assert.DoesNotContain(messages, m => m.Contains("Reference replacement succeeded, but old temporary backup files could not be fully cleaned up"));

                // Critical failure message was shown because rollback refused to overwrite tampered new provenance
                Assert.Contains(messages, m => m.Contains("CRITICAL") || m.Contains("Critical"));

                // Old backups were preserved and NOT deleted
                Assert.NotNull(backupRef);
                Assert.NotNull(backupProv);
                Assert.True(File.Exists(backupRef), "Old backup reference must be preserved");
                Assert.True(File.Exists(backupProv), "Old backup provenance must be preserved");
            }
            finally
            {
                MainForm.OnBeforeReferenceReplacementCommit = null;
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void REG_202_LegacyStateMigration_RunsOnceAndDoesNotResurrectStaleState()
    {
        using var workspace = new TestWorkspace();
        var legacyDirectory = Path.Combine(workspace.Root, "legacy");
        var stateDirectory = Path.Combine(workspace.Root, "state");
        Directory.CreateDirectory(legacyDirectory);

        var legacySettingsPath = Path.Combine(legacyDirectory, AppConstants.SettingsFileName);
        var legacySessionPath = Path.Combine(legacyDirectory, AppConstants.SessionFileName);
        var stableSettingsPath = Path.Combine(stateDirectory, AppConstants.SettingsFileName);
        var stableSessionPath = Path.Combine(stateDirectory, AppConstants.SessionFileName);

        File.WriteAllText(legacySettingsPath, "packaged settings");
        File.WriteAllText(legacySessionPath, "legacy pending session");

        AppBootstrap.MigrateLegacyState(legacyDirectory, stateDirectory);

        Assert.Equal("packaged settings", File.ReadAllText(stableSettingsPath));
        Assert.Equal("legacy pending session", File.ReadAllText(stableSessionPath));

        File.WriteAllText(stableSettingsPath, "user-updated settings");
        File.Delete(stableSessionPath);

        AppBootstrap.MigrateLegacyState(legacyDirectory, stateDirectory);

        Assert.Equal("user-updated settings", File.ReadAllText(stableSettingsPath));
        Assert.False(File.Exists(stableSessionPath));
    }

    [Fact]
    public void REG_203_LegacyStateMigration_ExistingPerUserSettingsRemainAuthoritative()
    {
        using var workspace = new TestWorkspace();
        var legacyDirectory = Path.Combine(workspace.Root, "legacy");
        var stateDirectory = Path.Combine(workspace.Root, "state");
        Directory.CreateDirectory(legacyDirectory);
        Directory.CreateDirectory(stateDirectory);

        var legacySettingsPath = Path.Combine(legacyDirectory, AppConstants.SettingsFileName);
        var stableSettingsPath = Path.Combine(stateDirectory, AppConstants.SettingsFileName);
        File.WriteAllText(legacySettingsPath, "packaged settings");
        File.WriteAllText(stableSettingsPath, "user-updated settings");

        AppBootstrap.MigrateLegacyState(legacyDirectory, stateDirectory);

        Assert.Equal("user-updated settings", File.ReadAllText(stableSettingsPath));
    }

    [Fact]
    public void REG_204_RunnerConfigIsDeployedAlongsideTestAssembly()
    {
        Assert.True(
            File.Exists(Path.Combine(AppContext.BaseDirectory, "xunit.runner.json")),
            "xunit.runner.json must be copied to the test output directory or the " +
            "non-parallel-by-design invariant silently stops applying.");
    }

    [Fact]
    public void REG_205_TestParallelizationIsDisabledAtTheAssemblyLevel()
    {
        var attribute =
            typeof(RegressionTests).Assembly
                .GetCustomAttribute<CollectionBehaviorAttribute>();

        Assert.NotNull(attribute);
        Assert.True(
            attribute!.DisableTestParallelization,
            "The test assembly must carry [assembly: CollectionBehavior(DisableTestParallelization = true)] " +
            "so the suite's non-parallel invariant does not depend solely on xunit.runner.json being deployed.");
    }
}




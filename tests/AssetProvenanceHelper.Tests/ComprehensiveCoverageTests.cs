using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using AssetProvenanceHelper.Ui;
using Xunit;

namespace AssetProvenanceHelper.Tests;

public class ComprehensiveCoverageTests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA thread timed out");
        if (error != null)
        {
            throw error;
        }
    }

    #region 1. Models & Exceptions

    [Fact]
    public void Models_AssetSession_CancellationTempPathHelpers_HandleNullAndEmpty()
    {
        var session = new AssetSession
        {
            CancellationId = null,
            ReferenceDestinationPath = @"C:\Assets\ref.png",
            ReferenceProvenancePath = @"C:\Assets\reference.md"
        };

        Assert.Equal(string.Empty, session.GetCancelTempReferencePath());
        Assert.Equal(string.Empty, session.GetCancelTempProvenancePath());

        session.CancellationId = "abc123";
        session.ReferenceDestinationPath = "";
        session.ReferenceProvenancePath = "";

        Assert.Equal(string.Empty, session.GetCancelTempReferencePath());
        Assert.Equal(string.Empty, session.GetCancelTempProvenancePath());
    }

    [Fact]
    public void Models_AssetProcessingException_Constructors()
    {
        var ex1 = new AssetProcessingException("Test message", rollbackComplete: true);
        Assert.Equal("Test message", ex1.Message);
        Assert.True(ex1.RollbackComplete);

        var inner = new Exception("Inner");
        var ex2 = new AssetProcessingException("Test 2", inner, rollbackComplete: false);
        Assert.Equal("Test 2", ex2.Message);
        Assert.Same(inner, ex2.InnerException);
        Assert.False(ex2.RollbackComplete);
    }

    #endregion

    #region 2. ImageFinderService

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ImageFinderService_InvalidDownloadFolder_ReturnsNull(string? folder)
    {
        var finder = new ImageFinderService();
        var settings = new AppSettings
        {
            DownloadFolder = folder!,
            AssetRootFolder = @"C:\Assets",
            AcceptedExtensions = new List<string> { ".png" }
        };
        var result = finder.FindLatestImage(settings);
        Assert.Null(result);
    }

    [Fact]
    public void ImageFinderService_NonExistentDownloadFolder_ReturnsNull()
    {
        var finder = new ImageFinderService();
        var settings = new AppSettings
        {
            DownloadFolder = @"C:\NonExistent_Downloads_12345",
            AssetRootFolder = @"C:\Assets",
            AcceptedExtensions = new List<string> { ".png" }
        };
        var result = finder.FindLatestImage(settings);
        Assert.Null(result);
    }

    #endregion

    #region 3. SettingsService

    [Fact]
    public void SettingsService_Load_NullJsonContent_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(settingsPath, "null");

        var service = new SettingsService(settingsPath);
        var ex = Assert.Throws<InvalidDataException>(() => service.Load());
        Assert.Contains("settings.json could not be deserialized", ex.Message);
    }

    [Fact]
    public void SettingsService_Save_FailureCleansUpTempFile()
    {
        using var workspace = new TestWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(settingsPath, "{}");

        var service = new SettingsService(settingsPath);
        var defaults = service.CreateDefaults();

        // Lock settings file so atomic move throws and catch block deletes temp file
        using (var lockStream = new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => service.Save(defaults));
        }

        // TEST-R8-001 & TEST-R9-002: Assert no temporary files were left behind
        Assert.Empty(Directory.GetFiles(workspace.Root, "settings.json.*.tmp"));
    }

    #endregion

    #region 4. TemplateService

    [Fact]
    public void TemplateService_Render_UnknownToken_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var refTpl = Path.Combine(workspace.Root, "ref.md");
        File.WriteAllText(refTpl, "{{REFERENCE_FILENAME}} {{PROJECT}} {{GENERATION_DATE}} {{UNKNOWN_TOKEN}}");
        var finalTpl = Path.Combine(workspace.Root, "final.md");
        File.WriteAllText(finalTpl, "{{FINAL_FILENAME}} {{REFERENCE_FILENAME}} {{PROJECT}} {{GENERATION_DATE}} {{PROMPT}}");

        var service = new TemplateService(refTpl, finalTpl);
        var ex = Assert.Throws<InvalidDataException>(() => service.RenderReference("ref.png", "proj", "2026-01-01"));
        Assert.Contains("UNKNOWN_TOKEN", ex.Message);
    }

    [Fact]
    public void TemplateService_RenderFinal_UnknownToken_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var refTpl = Path.Combine(workspace.Root, "ref.md");
        File.WriteAllText(refTpl, "{{REFERENCE_FILENAME}} {{PROJECT}} {{GENERATION_DATE}}");
        var finalTpl = Path.Combine(workspace.Root, "final.md");
        File.WriteAllText(finalTpl, "{{FINAL_FILENAME}} {{REFERENCE_FILENAME}} {{PROJECT}} {{GENERATION_DATE}} {{PROMPT}} {{UNKNOWN_FINAL_TOKEN}}");

        var service = new TemplateService(refTpl, finalTpl);
        var ex = Assert.Throws<InvalidDataException>(() => service.RenderFinal("main.png", "ref.png", "proj", "2026-01-01", "prompt"));
        Assert.Contains("UNKNOWN_FINAL_TOKEN", ex.Message);
    }

    [Fact]
    public void TemplateService_ValidateTemplates_MissingPlaceholders_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var refTpl = Path.Combine(workspace.Root, "ref.md");
        File.WriteAllText(refTpl, "Missing all tokens");
        var finalTpl = Path.Combine(workspace.Root, "final.md");
        File.WriteAllText(finalTpl, "Missing all tokens");

        var service = new TemplateService(refTpl, finalTpl);
        var res = service.ValidateTemplates();
        Assert.False(res.IsValid);
        Assert.Contains(res.Errors, e => e.Contains("Template"));
    }

    [Fact]
    public void TemplateService_ValidateTemplates_UnreadableTemplate_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var refTpl = Path.Combine(workspace.Root, "ref.md");
        File.WriteAllText(refTpl, "{{REFERENCE_FILENAME}} {{PROJECT}} {{GENERATION_DATE}}");
        var finalTpl = Path.Combine(workspace.Root, "final.md");
        File.WriteAllText(finalTpl, "{{FINAL_FILENAME}} {{REFERENCE_FILENAME}} {{PROJECT}} {{GENERATION_DATE}} {{PROMPT}}");

        var service = new TemplateService(refTpl, finalTpl);

        using (var lockStream = new FileStream(refTpl, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var res = service.ValidateTemplates();
            Assert.False(res.IsValid);
            Assert.Contains(res.Errors, e => e.Contains("Could not read template"));
        }
    }

    #endregion

    #region 5. SessionService

    [Fact]
    public void SessionService_Load_NullJsonContent_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var sessionPath = Path.Combine(workspace.Root, "session.json");
        File.WriteAllText(sessionPath, "null");

        var service = new SessionService(sessionPath);
        var ex = Assert.Throws<InvalidDataException>(() => service.Load());
        Assert.Contains("session.json could not be deserialized", ex.Message);
    }

    [Fact]
    public void SessionService_Save_FailureCleansUpTempFile()
    {
        using var workspace = new TestWorkspace();
        var sessionPath = Path.Combine(workspace.Root, "session.json");
        File.WriteAllText(sessionPath, "{}");

        var service = new SessionService(sessionPath);
        var session = new AssetSession
        {
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", "reference.md"),
            ReferenceProcessedAt = DateTimeOffset.Now,
            ReferenceHash = new string('0', 64),
            ProjectName = "proj"
        };

        using (var lockStream = new FileStream(sessionPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => service.Save(session));
        }

        // TEST-R8-001 & TEST-R9-002: Assert no temporary files were left behind
        Assert.Empty(Directory.GetFiles(workspace.Root, "session.json.*.tmp"));
    }

    [Fact]
    public void SessionService_Cancel_InvalidCancellationIdOnResume_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_cov_cancel_id", refSource, DateTimeOffset.Now);

        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = "invalid_short_id";

        var ex = Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));
        Assert.Contains("CancellationId", ex.Message);
    }

    [Fact]
    public void SessionService_Cancel_AmbiguousAndMissingProvenanceFileStates_ThrowsIOException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_cov_cancel_ambig", refSource, DateTimeOffset.Now);

        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = new string('a', 32);

        // Case 1: Both original and temp provenance exist
        File.WriteAllText(session.GetCancelTempProvenancePath(), "temp");
        var ex1 = Assert.Throws<IOException>(() => sessionService.Cancel(session));
        Assert.Contains("Ambiguous provenance file state", ex1.Message);

        // Case 2: Neither original nor temp provenance exists
        File.Delete(session.ReferenceProvenancePath);
        File.Delete(session.GetCancelTempProvenancePath());
        var ex2 = Assert.Throws<IOException>(() => sessionService.Cancel(session));
        Assert.Contains("Missing provenance file", ex2.Message);
    }

    [Fact]
    public void SessionService_Cancel_SaveThrowsAfterProvenanceRestore_ThrowsAggregateException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_cov_cancel_agg", refSource, DateTimeOffset.Now);
        sessionService.Save(session);

        // Lock reference destination so move to temp fails
        using (var refLock = new FileStream(session.ReferenceDestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.NotNull(ex);
        }
    }

    [Fact]
    public void SessionService_Cancel_Phase3DeletingTempProvenanceFails_ThrowsIOException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_cov_cancel_del_prov", refSource, DateTimeOffset.Now);

        session.CancelPhase = CancelPhase.FilesRenamed;
        session.CancellationId = new string('b', 32);
        File.Move(session.ReferenceDestinationPath, session.GetCancelTempReferencePath());
        File.Move(session.ReferenceProvenancePath, session.GetCancelTempProvenancePath());
        sessionService.Save(session);

        // Lock temp provenance file during Phase 3 deletion
        using (var provLock = new FileStream(session.GetCancelTempProvenancePath(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => sessionService.Cancel(session));
            Assert.Contains("temporary canceling", ex.Message);
        }
    }

    [Fact]
    public void SessionService_ValidateCancelPaths_AllEdgeCases()
    {
        using var workspace = new TestWorkspace();
        var sessionService = workspace.CreateSessionService();

        var valid = new AssetSession
        {
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Assets, "asset1"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", "reference.md"),
            CancelPhase = CancelPhase.None,
            CancellationId = null
        };

        // 1. Invalid CancelPhase enum
        valid.CancelPhase = (CancelPhase)99;
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(valid));
        valid.CancelPhase = CancelPhase.None;

        // 2. CancellationId not null on CancelPhase.None
        valid.CancellationId = "abc";
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(valid));
        valid.CancellationId = null;

        // 3. ReferenceFilename with unsafe path
        valid.ReferenceFilename = "../ref.png";
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(valid));
        valid.ReferenceFilename = "ref.png";

        // 4. AssetFolder mismatch
        valid.AssetFolder = Path.Combine(workspace.Assets, "other_folder");
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(valid));
        valid.AssetFolder = Path.Combine(workspace.Assets, "asset1");

        // 5. ReferenceDestinationPath escapes reference folder
        valid.ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "ref.png");
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(valid));
        valid.ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "reference", "ref.png");

        // 6. ReferenceProvenancePath inconsistent
        valid.ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", "wrong.md");
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(valid));
        valid.ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset1", "reference", "reference.md");

        // 7. CancellationId with traversal characters escaping reference folder
        valid.CancelPhase = CancelPhase.Prepared;
        valid.CancellationId = new string('c', 32);
        valid.ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset1", "reference", "sub", "ref.png");
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(valid));
    }

    #endregion

    #region 6. ValidationService

    [Fact]
    public void ValidationService_ValidateSettings_EmptyPathsAndNesting()
    {
        var service = new ValidationService();

        var emptySettings = new AppSettings
        {
            DownloadFolder = "",
            AssetRootFolder = "",
            AcceptedExtensions = new List<string> { ".png" }
        };
        var r1 = service.ValidateSettings(emptySettings);
        Assert.False(r1.IsValid);
        Assert.Contains(r1.Errors, e => e.Contains("Asset Root Folder must not be empty"));

        using var workspace = new TestWorkspace();
        var nestedDownloadDir = Path.Combine(workspace.Assets, "downloads_inside_assets");
        Directory.CreateDirectory(nestedDownloadDir);

        var nestedSettings = new AppSettings
        {
            DownloadFolder = nestedDownloadDir,
            AssetRootFolder = workspace.Assets,
            AcceptedExtensions = new List<string> { ".png" }
        };
        var r2 = service.ValidateSettings(nestedSettings);
        Assert.False(r2.IsValid);
        Assert.Contains(r2.Errors, e => e.Contains("Download Folder cannot be inside the Asset Root Folder"));
    }

    [Fact]
    public void ValidationService_ValidateSettings_ReparsePointRootFolder_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var service = new ValidationService();

        var settings = new AppSettings
        {
            DownloadFolder = workspace.Downloads,
            AssetRootFolder = workspace.Assets,
            AcceptedExtensions = new List<string> { ".png" }
        };

        try
        {
            ValidationService.FileAttributesProvider = path => FileAttributes.ReparsePoint | FileAttributes.Directory;
            var r = service.ValidateSettings(settings);
            Assert.False(r.IsValid);
            Assert.Contains(r.Errors, e => e.Contains("reparse point"));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    public void ValidationService_ValidateImageFile_EmptyAndLockedFile()
    {
        var service = new ValidationService();
        var r1 = service.ValidateImageFile("", new List<string> { ".png" });
        Assert.False(r1.IsValid);
        Assert.Contains(r1.Errors, e => e.Contains("Image path must not be empty"));

        using var workspace = new TestWorkspace();
        var img = workspace.CreateImage("locked.png", new byte[] { 1, 2, 3 });
        using (var lockStream = new FileStream(img, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r2 = service.ValidateImageFile(img, new List<string> { ".png" });
            Assert.False(r2.IsValid);
            Assert.Contains(r2.Errors, e => e.Contains("cannot be opened for reading"));
        }
    }

    [Fact]
    public void ValidationService_ValidateSession_AllUncoveredBranches()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_val_branches", refSource, DateTimeOffset.Now);

        // 1. Missing AssetRootFolder
        var badSession = new AssetSession
        {
            AssetRootFolder = Path.Combine(workspace.Root, "non_existent_root"),
            AssetFolderName = "asset1",
            AssetFolder = Path.Combine(workspace.Root, "non_existent_root", "asset1"),
            ReferenceFilename = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Root, "non_existent_root", "asset1", "reference", "ref.png"),
            ReferenceProvenancePath = Path.Combine(workspace.Root, "non_existent_root", "asset1", "reference", "reference.md"),
            ReferenceProcessedAt = DateTimeOffset.Now,
            ReferenceHash = new string('0', 64),
            ProjectName = "proj"
        };
        var r1 = service.ValidateSession(badSession);
        Assert.False(r1.IsValid);
        Assert.Contains(r1.Errors, e => e.Contains("AssetRootFolder does not exist"));

        // 2. Invalid AssetFolderName
        session.AssetFolderName = "invalid/name";
        var r2 = service.ValidateSession(session);
        Assert.False(r2.IsValid);
        Assert.Contains(r2.Errors, e => e.Contains("Session AssetFolderName is invalid"));
        session.AssetFolderName = "asset_val_branches";

        // 3. FilesRenamed phase with existing reference or provenance
        session.CancelPhase = CancelPhase.FilesRenamed;
        session.CancellationId = new string('f', 32);
        var r3 = service.ValidateSession(session);
        Assert.False(r3.IsValid);
        Assert.Contains(r3.Errors, e => e.Contains("original reference file still exists"));
        Assert.Contains(r3.Errors, e => e.Contains("original provenance file still exists"));
        session.CancelPhase = CancelPhase.None;
        session.CancellationId = null;

        // 4. Unrecognized CancelPhase
        session.CancelPhase = (CancelPhase)88;
        session.CancellationId = new string('8', 32);
        var r4 = service.ValidateSession(session);
        Assert.False(r4.IsValid);
        Assert.Contains(r4.Errors, e => e.Contains("is unrecognized"));
        session.CancelPhase = CancelPhase.None;
        session.CancellationId = null;

        // 5. IsMainCommitting missing properties
        session.IsMainCommitting = true;
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";
        session.MainFilename = null;
        session.MainPrompt = null;
        session.MainProcessedAt = null;
        var r5 = service.ValidateSession(session);
        Assert.False(r5.IsValid);
        Assert.Contains(r5.Errors, e => e.Contains("MainFilename is missing"));
        Assert.Contains(r5.Errors, e => e.Contains("MainPrompt is missing"));
        Assert.Contains(r5.Errors, e => e.Contains("MainProcessedAt is missing"));
        session.IsMainCommitting = false;
        session.MainTransactionId = null;

        // 6. AssetFolder not direct child
        session.AssetFolder = Path.Combine(workspace.Assets, "sub", "asset_val_branches");
        var r6 = service.ValidateSession(session);
        Assert.False(r6.IsValid);
        Assert.Contains(r6.Errors, e => e.Contains("not a direct child"));
        session.AssetFolder = Path.Combine(workspace.Assets, "asset_val_branches");

        // 7. Reference paths inconsistent / escaping
        session.ReferenceDestinationPath = Path.Combine(workspace.Assets, "ref.png");
        session.ReferenceProvenancePath = Path.Combine(workspace.Assets, "ref.md");
        var r7 = service.ValidateSession(session);
        Assert.False(r7.IsValid);
        Assert.True(r7.Errors.Count > 0);
        session.ReferenceDestinationPath = Path.Combine(session.AssetFolder, "reference", "ref.png");
        session.ReferenceProvenancePath = Path.Combine(session.AssetFolder, "reference", "reference.md");

        // 8. Reference hash computation error on locked reference image
        using (var lockStream = new FileStream(session.ReferenceDestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r8 = service.ValidateSession(session);
            Assert.False(r8.IsValid);
            Assert.True(r8.Errors.Count > 0);
        }

        // 9. Reparse point on AssetFolder
        try
        {
            ValidationService.FileAttributesProvider = path => FileAttributes.ReparsePoint | FileAttributes.Directory;
            var r9 = service.ValidateSession(session);
            Assert.False(r9.IsValid);
            Assert.Contains(r9.Errors, e => e.Contains("reparse point"));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    public void ValidationService_ValidateReferenceProvenanceContent_EdgeCases()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_val_prov", refSource, DateTimeOffset.Now);

        // Non-existent provenance file
        var r1 = service.ValidateReferenceProvenanceContent(session, Path.Combine(workspace.Root, "non_existent.md"));
        Assert.False(r1.IsValid);
        Assert.Contains(r1.Errors, e => e.Contains("does not exist"));

        // Unreadable provenance file
        using (var lockStream = new FileStream(session.ReferenceProvenancePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r2 = service.ValidateReferenceProvenanceContent(session, session.ReferenceProvenancePath);
            Assert.False(r2.IsValid);
            Assert.Contains(r2.Errors, e => e.Contains("Could not read reference provenance"));
        }
    }

    [Fact]
    public void ValidationService_ValidateCompleteAsset_EdgeCases()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_val_complete", refSource, DateTimeOffset.Now);

        var mainImg = workspace.CreateImage("main.png", new byte[] { 9, 9, 9 });
        var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, mainImg, "main prompt", DateTimeOffset.Now);
        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var mainDest = Path.Combine(session.AssetFolder, mainFilename);

        var templateService = workspace.CreateTemplateService();

        // 1. Unreadable final provenance file
        using (var lockStream = new FileStream(finalProv, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r1 = service.ValidateCompleteAsset(session, mainDest, finalProv, mainFilename, "2026-01-01", "main prompt", templateService, session.MainHash);
            Assert.False(r1.IsValid);
            Assert.Contains(r1.Errors, e => e.Contains("Could not read final provenance") || e.Contains("Could not compute final provenance hash"));
        }

        // 2. Unreadable main image file for hash verification
        using (var lockStream = new FileStream(mainDest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r2 = service.ValidateCompleteAsset(session, mainDest, finalProv, mainFilename, "2026-01-01", "main prompt", templateService, session.MainHash);
            Assert.False(r2.IsValid);
            Assert.Contains(r2.Errors, e => e.Contains("Could not compute Main image SHA-256 hash"));
        }
    }

    [Fact]
    public void ValidationService_ValidateReferenceReplacementTransaction_EdgeCases()
    {
        using var workspace = new TestWorkspace();
        var service = workspace.CreateValidationService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_val_tx", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.PrepareReferenceReplacement(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // 1. Mismatched AssetRootFolder
        tx.NewSession.AssetRootFolder = Path.Combine(workspace.Root, "different_root");
        var r1 = service.ValidateReferenceReplacementTransaction(tx);
        Assert.False(r1.IsValid);
        Assert.True(r1.Errors.Count > 0);
        tx.NewSession.AssetRootFolder = oldSession.AssetRootFolder;

        // 2. Unsafe ReferenceFilename in OldSession
        tx.OldSession.ReferenceFilename = "../unsafe.png";
        var r2 = service.ValidateReferenceReplacementTransaction(tx);
        Assert.False(r2.IsValid);
        Assert.Contains(r2.Errors, e => e.Contains("ReferenceFilename contains an unsafe path"));
        tx.OldSession.ReferenceFilename = "ref1.png";

        // 3. AssetFolder not a direct child
        tx.OldSession.AssetFolder = Path.Combine(workspace.Assets, "sub", "asset_val_tx");
        var r3 = service.ValidateReferenceReplacementTransaction(tx);
        Assert.False(r3.IsValid);
        Assert.Contains(r3.Errors, e => e.Contains("AssetFolder"));
    }

    #endregion

    #region 7. AssetProcessorService

    [Fact]
    public void AssetProcessorService_ProcessReference_InvalidSessionOrCopiedImage_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        // 1. Invalid characters in assetFolderName
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(settings, "invalid/name", refSource, DateTimeOffset.Now));

        // 2. Copied reference image validation fails (e.g. extension not accepted)
        var invalidExtSettings = new AppSettings
        {
            DownloadFolder = workspace.Downloads,
            AssetRootFolder = workspace.Assets,
            AcceptedExtensions = new List<string> { ".jpg" }
        };
        Assert.Throws<InvalidDataException>(() => processor.ProcessReference(invalidExtSettings, "asset_bad_ext", refSource, DateTimeOffset.Now));
    }

    [Fact]
    public void AssetProcessorService_ProcessReference_ReparsePointAssetFolder_ThrowsIOException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var assetFolder = Path.Combine(workspace.Assets, "asset_reparse");
        Directory.CreateDirectory(assetFolder);

        try
        {
            ValidationService.FileAttributesProvider = path => FileAttributes.ReparsePoint | FileAttributes.Directory;
            var ex = Assert.Throws<IOException>(() => processor.ProcessReference(settings, "asset_reparse", refSource, DateTimeOffset.Now));
            Assert.Contains("reparse point", ex.Message);
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    public void AssetProcessorService_RollbackReference_EdgeCases()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_rb_ref", refSource, DateTimeOffset.Now);

        // 1. Reference paths escape reference folder
        session.ReferenceDestinationPath = Path.Combine(workspace.Assets, "escaped.png");
        var r1 = processor.RollbackReference(session);
        Assert.False(r1.IsValid);
        Assert.True(r1.Errors.Count > 0);
        session.ReferenceDestinationPath = Path.Combine(session.AssetFolder, "reference", "ref.png");

        // 2. Clean rollback deletes reference files and created folders
        session.WasReferenceFolderCreatedByTool = true;
        session.WasAssetFolderCreatedByTool = true;
        var r2 = processor.RollbackReference(session);
        Assert.True(r2.IsValid);
        Assert.False(Directory.Exists(session.AssetFolder));
    }

    [Fact]
    public void AssetProcessorService_ProcessMainImage_InvalidSessionOrImage_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_main_bad", refSource, DateTimeOffset.Now);

        // 1. Session is invalid (corrupt session path)
        session.AssetFolderName = "invalid/name";
        var mainImg = workspace.CreateImage("main.png", new byte[] { 9, 9, 9 });
        Assert.Throws<InvalidDataException>(() => processor.ProcessMainPrepared(session, settings.AcceptedExtensions, mainImg, "prompt", DateTimeOffset.Now));
        session.AssetFolderName = "asset_main_bad";

        // 2. Main image validation fails (disallowed extension)
        Assert.Throws<InvalidDataException>(() => processor.ProcessMainPrepared(session, new List<string> { ".jpg" }, mainImg, "prompt", DateTimeOffset.Now));
    }

    [Fact]
    public void AssetProcessorService_RollbackMain_EscapedMainFilename_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_rb_main_esc", refSource, DateTimeOffset.Now);


        var r = processor.RollbackMain(session, "../escaped_main.png");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("match") || e.Contains("invalid") || e.Contains("escapes") || e.Contains("exists"));
    }

    [Fact]
    public void AssetProcessorService_PrepareReferenceReplacement_InvalidOldSessionOrNewImage_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_prep_bad", ref1, DateTimeOffset.Now);

        // 1. Invalid old session
        session.AssetFolderName = "invalid/name";
        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        Assert.Throws<InvalidDataException>(() => processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));
        session.AssetFolderName = "asset_prep_bad";

        // 2. Disallowed extension on new reference
        Assert.Throws<InvalidDataException>(() => processor.PrepareReferenceReplacement(session, new List<string> { ".jpg" }, ref2, DateTimeOffset.Now));
    }

    [Fact]
    public void AssetProcessorService_RollbackReferenceReplacement_InvalidTransactionOrBackupFailures()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_rb_tx_err", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.PrepareReferenceReplacement(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Invalid transaction (unsafe path)
        tx.OldSession.ReferenceFilename = "../unsafe.png";
        var r1 = processor.RollbackReferenceReplacement(tx);
        Assert.False(r1.IsValid);
        Assert.True(r1.Errors.Count > 0);
        tx.OldSession.ReferenceFilename = "ref1.png";

        // Delete backup files to trigger missing backup checks
        File.Delete(tx.BackupReferencePath);
        File.Delete(tx.BackupProvenancePath);
        var r2 = processor.RollbackReferenceReplacement(tx);
        Assert.False(r2.IsValid);
        Assert.True(r2.Errors.Count > 0);
    }

    #endregion

    #region 8. TwoChoiceDialog (STA Window Lifecycles)

    [Fact]
    public void TwoChoiceDialog_ShowChoice_PrimaryAndSecondaryReturnsExpected()
    {
        var owner = new Form();

        TwoChoiceDialog.CustomChoiceProvider = (w, t, m, p, s) => true;
        Assert.True(TwoChoiceDialog.ShowChoice(owner, "Title", "Message", "OK", "Cancel"));

        TwoChoiceDialog.CustomChoiceProvider = (w, t, m, p, s) => false;
        Assert.False(TwoChoiceDialog.ShowChoice(owner, "Title", "Message", "OK", "Cancel"));

        TwoChoiceDialog.CustomChoiceProvider = null;
    }

    [Fact]
    public void TwoChoiceDialog_RealDialog_PrimaryButtonClick_SetsOkAndTrue()
    {
        RunOnSta(() =>
        {
            using var dialog = new TwoChoiceDialog("Test Title", "Test Message", "Primary OK", "Secondary Cancel");
            dialog.Show();
            var acceptBtn = dialog.AcceptButton as Button;
            Assert.NotNull(acceptBtn);

            acceptBtn.PerformClick();

            var primaryField = typeof(TwoChoiceDialog).GetField("_primarySelected", BindingFlags.NonPublic | BindingFlags.Instance);
            var isPrimary = (bool)(primaryField?.GetValue(dialog) ?? false);
            Assert.True(isPrimary);
        });
    }

    [Fact]
    public void TwoChoiceDialog_RealDialog_SecondaryButtonClick_SetsCancelAndFalse()
    {
        RunOnSta(() =>
        {
            using var dialog = new TwoChoiceDialog("Test Title", "Test Message", "Primary OK", "Secondary Cancel");
            dialog.Show();
            var cancelBtn = dialog.CancelButton as Button;
            Assert.NotNull(cancelBtn);

            cancelBtn.PerformClick();

            var primaryField = typeof(TwoChoiceDialog).GetField("_primarySelected", BindingFlags.NonPublic | BindingFlags.Instance);
            var isPrimary = (bool)(primaryField?.GetValue(dialog) ?? false);
            Assert.False(isPrimary);
        });
    }

    [Fact]
    public void TwoChoiceDialog_ShowChoice_RealDialog_InvokesShowDialog()
    {
        RunOnSta(() =>
        {
            using var owner = new Form();
            TwoChoiceDialog.CustomChoiceProvider = null;

            var timer = new System.Windows.Forms.Timer { Interval = 20 };
            timer.Tick += (s, e) =>
            {
                foreach (Form openForm in Application.OpenForms)
                {
                    if (openForm is TwoChoiceDialog dlg)
                    {
                        timer.Stop();
                        (dlg.AcceptButton as Button)?.PerformClick();
                        break;
                    }
                }
            };
            timer.Start();

            var result = TwoChoiceDialog.ShowChoice(owner, "Title", "Message", "OK", "Cancel");
            timer.Dispose();
            Assert.True(result);
        });
    }

    #endregion

    #region 9. MainForm UI Lifecycle & Edge Cases

    [Fact]
    public void MainForm_HandleMainImage_NoActiveSession_ShowsWarning()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var messages = new List<string>();
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleMainMethod);

            var mainImg = workspace.CreateImage("main_test.png", new byte[] { 1, 2, 3 });
            form.SetSelectedImage(ImageSlot.Main, mainImg);
            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);
            txtPrompt.Text = "Prompt text";

            handleMainMethod.Invoke(form, null);
            Assert.Contains(messages, m => m.Contains("No active reference session"));

            MainForm.MessageBoxProvider = null;
        });
    }

    [Fact]
    public void MainForm_HandleMainImage_NoUsableImage_ShowsWarning()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var messages = new List<string>();
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleMainMethod);

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_main_no_img", refSource, DateTimeOffset.Now);
            sessionService.Save(session);
            File.Delete(refSource); // Ensure download directory is empty

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            handleMainMethod.Invoke(form, null);
            Assert.True(sessionService.Exists()); // Still active

            var pnlHost = typeof(MainForm).GetField("pnlMainImageHost", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as Panel;
            Assert.NotNull(pnlHost);
            Assert.Equal(UiTheme.Error, pnlHost.BackColor);

            MainForm.MessageBoxProvider = null;
        });
    }

    [Fact]
    public void MainForm_HandleMainImage_ValidState_CompletesSessionSuccessfully()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var messages = new List<string>();
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleMainMethod);

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_main_ok", refSource, DateTimeOffset.Now);
            sessionService.Save(session);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            var mainImg = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
            form.SetSelectedImage(ImageSlot.Main, mainImg);

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);
            txtPrompt.Text = "Main prompt text";

            handleMainMethod.Invoke(form, null);
            Assert.False(sessionService.Exists()); // Session completed and deleted

            MainForm.MessageBoxProvider = null;
        });
    }

    [Fact]
    public void MainForm_HandleCancel_InconsistentSession_ShowsValidationError()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var messages = new List<string>();
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_cancel_inv", refSource, DateTimeOffset.Now);

            // Invalidate session paths
            session.ReferenceDestinationPath = Path.Combine(workspace.Assets, "escaped.png");
            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            var handleCancelMethod = typeof(MainForm).GetMethod("HandleCancel", BindingFlags.NonPublic | BindingFlags.Instance);
            handleCancelMethod?.Invoke(form, null);

            Assert.True(messages.Count > 0);
            MainForm.MessageBoxProvider = null;
        });
    }

    [Fact]
    public void MainForm_StartupRecovery_BrokenSessionJson_UserDeletesRecord()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var sessionFile = Path.Combine(workspace.Root, "session.json");
            File.WriteAllText(sessionFile, "{ not valid json }}}");

            var sessionService = new SessionService(sessionFile);
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => true; // user chooses to delete

            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                sessionService);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
            recoverMethod?.Invoke(form, null);

            Assert.False(sessionService.Exists());
        });
    }

    [Fact]
    public void MainForm_StartupRecovery_BrokenSessionJson_UserExits()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var sessionFile = Path.Combine(workspace.Root, "session.json");
            File.WriteAllText(sessionFile, "{ not valid json }}}");

            var sessionService = new SessionService(sessionFile);
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => false; // user chooses to exit

            using var form = new MainForm(
                workspace.CreateSettings(),
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                sessionService);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
            recoverMethod?.Invoke(form, null);

            Assert.True(sessionService.Exists());
        });
    }

    [Fact]
    public void MainForm_StartupRecovery_InvalidUnfinishedSession_UserDeletesVsExits()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var sessionService = workspace.CreateSessionService();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_rec_inv", refSource, DateTimeOffset.Now);

            // Invalidate session by deleting reference image
            File.Delete(session.ReferenceDestinationPath);
            sessionService.Save(session);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);

            // User chooses Exit
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => false;
            using var form1 = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);
            recoverMethod?.Invoke(form1, null);
            Assert.True(sessionService.Exists());

            // User chooses Delete
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => true;
            using var form2 = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);
            recoverMethod?.Invoke(form2, null);
            Assert.False(sessionService.Exists());
        });
    }

    [Fact]
    public void MainForm_StartupRecovery_InterruptedCancellation_ResumesSuccessfully()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var sessionService = workspace.CreateSessionService();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_rec_cancel", refSource, DateTimeOffset.Now);

            session.CancelPhase = CancelPhase.Prepared;
            session.CancellationId = new string('c', 32);
            File.Move(session.ReferenceDestinationPath, session.GetCancelTempReferencePath());
            File.Move(session.ReferenceProvenancePath, session.GetCancelTempProvenancePath());
            sessionService.Save(session);

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

            Assert.False(sessionService.Exists());
            Assert.False(File.Exists(session.GetCancelTempReferencePath()));
        });
    }

    [Fact]
    public void MainForm_StartupRecovery_CompletedAsset_UserDeletesVsExits()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var sessionService = workspace.CreateSessionService();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_rec_comp", refSource, DateTimeOffset.Now);

            var processedAt = DateTimeOffset.Now;
            var mainSource = workspace.CreateImage("main.png", new byte[] { 7, 7, 7 });
            var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, mainSource, "main prompt", processedAt);
            
            session.IsMainCommitting = true;
            session.MainTransactionId = "0123456789abcdef0123456789abcdef";
            session.MainFilename = mainFilename;
            session.MainPrompt = "main prompt";
            session.MainProcessedAt = processedAt;
            session.MainHash = processor.ComputeSha256(Path.Combine(session.AssetFolder, mainFilename));
            sessionService.Save(session); // Simulate crash right before session deletion

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);

            // User chooses Delete
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => true;
            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            recoverMethod?.Invoke(form, null);
            Assert.False(sessionService.Exists());
        });
    }

    [Fact]
    public void MainForm_StartupRecovery_IncompleteMainCommit_RollsBackCleanly()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var sessionService = workspace.CreateSessionService();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_rec_incomp", refSource, DateTimeOffset.Now);

            // Incomplete main commit state
            session.IsMainCommitting = true;
            session.MainTransactionId = "0123456789abcdef0123456789abcdef";
            session.MainFilename = "incomplete_main.png";
            session.MainPrompt = "prompt";
            session.MainProcessedAt = DateTimeOffset.Now;
            session.MainHash = new string('9', 64);
            sessionService.Save(session);

            // User resumes reference session when prompted
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, p, s) => true;

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

            var loaded = sessionService.Load();
            Assert.NotNull(loaded);
            Assert.False(loaded.IsMainCommitting);
            Assert.Null(loaded.MainFilename);
        });
    }

    [Fact]
    public void MainForm_StartupRecovery_StandardSession_UserCancelsVsResumes()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var sessionService = workspace.CreateSessionService();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_rec_std", refSource, DateTimeOffset.Now);
            sessionService.Save(session);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            recoverMethod?.Invoke(form, null);
            Assert.True(sessionService.Exists());

            var currentSession = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as AssetSession;
            Assert.NotNull(currentSession);
            Assert.Equal("asset_rec_std", currentSession.AssetFolderName);
        });
    }

    [Fact]
    public void MainForm_ClipboardPaste_EmptyClipboardAndNonText()
    {
        RunOnSta(() =>
        {
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

            var pasteMethod = typeof(MainForm).GetMethod("PasteClipboard", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(pasteMethod);

            // Empty clipboard via provider
            form.ClipboardProvider = () => null;
            var msgShown = false;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { msgShown = true; };

            pasteMethod.Invoke(form, null);
            Assert.True(msgShown);

            // Text clipboard via provider
            form.ClipboardProvider = () => "Hello clipboard";
            pasteMethod.Invoke(form, null);
            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.Equal("Hello clipboard", txtPrompt?.Text);

            MainForm.MessageBoxProvider = null;
        });
    }

    [Fact]
    public void MainForm_OpenDownloads_NonExistentFolder_ShowsWarning()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            settings.DownloadFolder = Path.Combine(workspace.Root, "non_existent_downloads");

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var openDownloadsMethod = typeof(MainForm).GetMethod("OpenDownloads", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(openDownloadsMethod);

            var msgShown = false;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { msgShown = true; };

            openDownloadsMethod.Invoke(form, null);
            Assert.True(msgShown);

            MainForm.MessageBoxProvider = null;
        });
    }

    [Fact]
    public void MainForm_DragDropEvents_FilesVsNonFiles()
    {
        RunOnSta(() =>
        {
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

            var dragEnterMethod = typeof(MainForm).GetMethod("ImageDrop_DragEnter", BindingFlags.NonPublic | BindingFlags.Instance);
            var dragDropMethod = typeof(MainForm).GetMethod("ImageDrop_DragDrop", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(dragEnterMethod);
            Assert.NotNull(dragDropMethod);

            var img = workspace.CreateImage("drag.png", new byte[] { 1, 2, 3 });

            // FileDrop DataObject
            var dataObj = new DataObject(DataFormats.FileDrop, new string[] { img });
            var dragEvent = new DragEventArgs(dataObj, 0, 0, 0, DragDropEffects.None, DragDropEffects.Copy);

            dragEnterMethod.Invoke(form, new object[] { form, dragEvent });
            Assert.Equal(DragDropEffects.Copy, dragEvent.Effect);

            dragDropMethod.Invoke(form, new object[] { ImageSlot.Reference, dragEvent });
            Assert.Equal(img, form.GetSelectedImage(ImageSlot.Reference));

            // Non-file DataObject
            var textDataObj = new DataObject(DataFormats.Text, "some text");
            var dragEventText = new DragEventArgs(textDataObj, 0, 0, 0, DragDropEffects.None, DragDropEffects.None);
            dragEnterMethod.Invoke(form, new object[] { form, dragEventText });
            Assert.Equal(DragDropEffects.None, dragEventText.Effect);
        });
    }

    [Fact]
    public void MainForm_MenuAndButtonEvents_AllCovered()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            // Buttons present in form
            var btnRefresh = form.Controls.Find("btnRefreshReference", true).FirstOrDefault() as Button
                ?? form.Controls.Find("btnRefresh", true).FirstOrDefault() as Button;
            var btnClearPrompt = form.Controls.Find("btnClearPrompt", true).FirstOrDefault() as Button;
            var btnOpenAssetFolder = form.Controls.Find("btnOpenAssetFolder", true).FirstOrDefault() as Button;
            Assert.NotNull(btnRefresh);
            Assert.NotNull(btnClearPrompt);
            Assert.NotNull(btnOpenAssetFolder);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            btnRefresh.PerformClick();
            btnClearPrompt.PerformClick();
            btnOpenAssetFolder.PerformClick();

            // Clear manual selection
            form.SetSelectedImage(ImageSlot.Reference, null);

            // Status helpers
            var addStatusMethod = typeof(MainForm).GetMethod("AddStatus", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            addStatusMethod?.Invoke(form, new object[] { "Test status message" });

            // Error display helper
            var showErrorMethod = typeof(MainForm).GetMethod("ShowError", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string), typeof(Exception) }, null);
            showErrorMethod?.Invoke(form, new object[] { "Test error title", new Exception("Test ex") });

            // Validation error display helper
            var showValErrorMethod = typeof(MainForm).GetMethod("ShowValidationError", BindingFlags.NonPublic | BindingFlags.Instance);
            showValErrorMethod?.Invoke(form, new object[] { "Val title", ValidationResult.Failure("Val error") });

            // Handle cancel in Idle state (no session)
            var handleCancelMethod = typeof(MainForm).GetMethod("HandleCancel", BindingFlags.NonPublic | BindingFlags.Instance);
            handleCancelMethod?.Invoke(form, null);

            MainForm.MessageBoxProvider = null;
        });
    }

    [Fact]
    public void MainForm_BrowseAndOpenActions_AllCovered()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => { };

            // 1. BrowseDownloadFolder - selected and cancelled
            var browseDownloadMethod = typeof(MainForm).GetMethod("BrowseDownloadFolder", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(browseDownloadMethod);

            MainForm.FolderBrowserDialogProvider = (owner, initial) => workspace.Downloads;
            browseDownloadMethod.Invoke(form, null);
            var txtDownload = form.Controls.Find("txtDownloadFolder", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtDownload);
            Assert.Equal(workspace.Downloads, txtDownload.Text);

            MainForm.FolderBrowserDialogProvider = (owner, initial) => null;
            browseDownloadMethod.Invoke(form, null);

            // 2. BrowseAssetRoot - selected and cancelled
            var browseAssetMethod = typeof(MainForm).GetMethod("BrowseAssetRoot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(browseAssetMethod);

            MainForm.FolderBrowserDialogProvider = (owner, initial) => workspace.Assets;
            browseAssetMethod.Invoke(form, null);
            var txtAssetRoot = form.Controls.Find("txtAssetRoot", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtAssetRoot);
            Assert.Equal(workspace.Assets, txtAssetRoot.Text);

            MainForm.FolderBrowserDialogProvider = (owner, initial) => null;
            browseAssetMethod.Invoke(form, null);

            // 3. ChooseFile - valid, invalid, and cancelled
            var validImg = workspace.CreateImage("choose.png", new byte[] { 1, 2, 3 });
            MainForm.OpenFileDialogProvider = (owner, initial) => validImg;
            form.ChooseImageFile(ImageSlot.Reference);

            var invalidImg = Path.Combine(workspace.Downloads, "bad.xyz");
            File.WriteAllBytes(invalidImg, new byte[] { 1, 2, 3 });
            MainForm.OpenFileDialogProvider = (owner, initial) => invalidImg;
            form.ChooseImageFile(ImageSlot.Reference);

            MainForm.OpenFileDialogProvider = (owner, initial) => null;
            form.ChooseImageFile(ImageSlot.Reference);

            // 4. OpenDownloads - exists and does not exist
            var openDownloadsMethod = typeof(MainForm).GetMethod("OpenDownloads", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(openDownloadsMethod);

            string? openedPath = null;
            MainForm.OpenFolderProvider = path => openedPath = path;

            txtDownload.Text = workspace.Downloads;
            openDownloadsMethod.Invoke(form, null);
            Assert.Equal(workspace.Downloads, openedPath);

            txtDownload.Text = @"Z:\NonExistentDownloadsFolder";
            openDownloadsMethod.Invoke(form, null);

            // 5. OpenAssetFolder - with active session and completed asset
            var openAssetFolderMethod = typeof(MainForm).GetMethod("OpenAssetFolder", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(openAssetFolderMethod);

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_open_test", refSource, DateTimeOffset.Now);
            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);

            openAssetFolderMethod.Invoke(form, null);
            Assert.Equal(session.AssetFolder, openedPath);

            // 6. PasteClipboard - valid text, empty text, and error
            var pasteMethod = typeof(MainForm).GetMethod("PasteClipboard", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(pasteMethod);

            form.ClipboardProvider = () => "Pasted text content";
            pasteMethod.Invoke(form, null);
            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);
            Assert.Equal("Pasted text content", txtPrompt.Text);

            form.ClipboardProvider = () => "";
            pasteMethod.Invoke(form, null);

            form.ClipboardProvider = () => throw new Exception("Simulated clipboard error");
            pasteMethod.Invoke(form, null);

            // Clean up test providers
            MainForm.FolderBrowserDialogProvider = null;
            MainForm.OpenFileDialogProvider = null;
            MainForm.OpenFolderProvider = null;
            MainForm.MessageBoxProvider = null;
        });
    }

    [Fact]
    public void AppBootstrap_AllMethods_Covered()
    {
        using var workspace = new TestWorkspace();
        var baseDir = workspace.Root;

        var mutex1 = AppBootstrap.BuildSingleInstanceMutexName(baseDir);
        var mutex2 = AppBootstrap.BuildSingleInstanceMutexName(baseDir);
        Assert.Equal(mutex1, mutex2);

        var mutexOther = AppBootstrap.BuildSingleInstanceMutexName(workspace.Downloads);
        Assert.NotEqual(mutex1, mutexOther);

        Assert.Equal(Path.Combine(baseDir, "settings.json"), AppBootstrap.GetSettingsPath(baseDir));
        Assert.Equal(Path.Combine(baseDir, "session.json"), AppBootstrap.GetSessionPath(baseDir));
        Assert.Equal(Path.Combine(baseDir, "templates", "reference.md"), AppBootstrap.GetReferenceTemplatePath(baseDir));
        Assert.Equal(Path.Combine(baseDir, "templates", "final.md"), AppBootstrap.GetFinalTemplatePath(baseDir));

        // Settings load success
        var settingsService = new SettingsService(AppBootstrap.GetSettingsPath(baseDir));
        settingsService.Save(new AppSettings { DownloadFolder = workspace.Downloads, AssetRootFolder = workspace.Assets });
        var loaded = AppBootstrap.LoadSettingsOrDefaults(settingsService);
        Assert.Equal(workspace.Downloads, loaded.DownloadFolder);

        // Settings load failure with warning callback
        var corruptFile = Path.Combine(baseDir, "corrupt_settings.json");
        File.WriteAllText(corruptFile, "{ invalid json content");
        var corruptSettingsService = new SettingsService(corruptFile);
        string? warningMsg = null;
        var fallback = AppBootstrap.LoadSettingsOrDefaults(corruptSettingsService, (msg, title) => warningMsg = msg);
        Assert.NotNull(warningMsg);
        Assert.NotNull(fallback);

        // CreateContext
        var ctx = AppBootstrap.CreateContext(baseDir);
        Assert.NotNull(ctx);
        Assert.NotNull(ctx.SettingsService);
        Assert.NotNull(ctx.SessionService);
        Assert.NotNull(ctx.TemplateService);
        Assert.NotNull(ctx.ValidationService);
        Assert.NotNull(ctx.ImageFinderService);
        Assert.NotNull(ctx.AssetProcessorService);
    }

    [Fact]
    public void MainForm_HandleReference_ErrorBranches_Covered()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var handleRefMethod = typeof(MainForm).GetMethod("HandleReference", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleRefMethod);

            // Invalid settings
            var txtAssetRoot = form.Controls.Find("txtAssetRoot", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtAssetRoot);
            var originalAssetRoot = txtAssetRoot.Text;
            txtAssetRoot.Text = "";
            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => { };
            handleRefMethod.Invoke(form, null);

            // Invalid folder name
            txtAssetRoot.Text = originalAssetRoot;
            var txtFolderName = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtFolderName);
            txtFolderName.Text = "../invalid";
            handleRefMethod.Invoke(form, null);

            // No usable image found
            txtFolderName.Text = "valid_folder";
            form.SetSelectedImage(ImageSlot.Reference, null);
            handleRefMethod.Invoke(form, null);

            // Existing destination folder - user cancels
            var existingTarget = Path.Combine(settings.AssetRootFolder, "existing_folder");
            Directory.CreateDirectory(existingTarget);
            txtFolderName.Text = "existing_folder";
            var validImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            form.SetSelectedImage(ImageSlot.Reference, validImg);

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => false; // Cancel
            handleRefMethod.Invoke(form, null);

            MainForm.MessageBoxProvider = null;
            TwoChoiceDialog.CustomChoiceProvider = null;
        });
    }

    [Fact]
    public void MainForm_HandleReplaceReference_ValidationAndAbortBranches_Covered()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => { };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleReplaceMethod);

            // 1. Session is null -> returns early
            handleReplaceMethod.Invoke(form, null);

            // Set up active session
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_rep_branches", refSource, DateTimeOffset.Now);
            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);

            // 2. User cancels dialog with valid replacement candidate
            var repl1 = workspace.CreateImage("repl1.png", new byte[] { 4, 5, 6 });
            form.SetSelectedImage(ImageSlot.Reference, repl1);
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => false;
            handleReplaceMethod.Invoke(form, null);

            // 3. User confirms but no candidate selected
            form.SetSelectedImage(ImageSlot.Reference, null);
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => true;
            handleReplaceMethod.Invoke(form, null);

            // 4. Invalid replacement image
            var invalidImg = Path.Combine(workspace.Downloads, "bad.xyz");
            File.WriteAllBytes(invalidImg, new byte[] { 1, 2, 3 });
            form.SetSelectedImage(ImageSlot.Reference, invalidImg);
            handleReplaceMethod.Invoke(form, null);

            MainForm.MessageBoxProvider = null;
            TwoChoiceDialog.CustomChoiceProvider = null;
        });
    }

    [Fact]
    public void MainForm_HandleMainImage_AllValidationAndPasteBranches_Covered()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleMainMethod);

            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => { };

            // 1. Session is null
            handleMainMethod.Invoke(form, null);

            // 2. Session is invalid
            var invalidSession = new AssetSession { AssetFolderName = "" };
            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, invalidSession);
            handleMainMethod.Invoke(form, null);

            // 3. Valid session but no source image found
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_main_paste_test", refSource, DateTimeOffset.Now);
            sessionField?.SetValue(form, session);
            File.Delete(refSource);
            handleMainMethod.Invoke(form, null);

            // 4. Invalid source image
            var invalidSource = Path.Combine(workspace.Downloads, "bad_ext.xyz");
            File.WriteAllBytes(invalidSource, new byte[] { 1, 2, 3 });
            form.SetSelectedImage(ImageSlot.Main, invalidSource);
            handleMainMethod.Invoke(form, null);

            // 5. Valid image, prompt empty -> Rejected without auto-paste
            var validMain = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
            form.SetSelectedImage(ImageSlot.Main, validMain);
            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);
            txtPrompt.Text = "";
            handleMainMethod.Invoke(form, null);
            Assert.NotNull(sessionField?.GetValue(form));

            // 6. Paste clipboard explicitly
            form.ClipboardProvider = () => "Pasted prompt content";
            var pasteMethod = typeof(MainForm).GetMethod("PasteClipboard", BindingFlags.NonPublic | BindingFlags.Instance);
            pasteMethod?.Invoke(form, null);
            Assert.Equal("Pasted prompt content", txtPrompt.Text);

            // 7. Complete with valid image and prompt
            handleMainMethod.Invoke(form, null);
            Assert.Null(sessionField?.GetValue(form));

            MainForm.MessageBoxProvider = null;
            TwoChoiceDialog.CustomChoiceProvider = null;
        });
    }

    [Fact]
    public void MainForm_HandleCancel_AllBranches_Covered()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var handleCancelMethod = typeof(MainForm).GetMethod("HandleCancel", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handleCancelMethod);

            MainForm.MessageBoxProvider = (owner, msg, title, btns, icon) => { };

            // 1. Session is null -> returns
            handleCancelMethod.Invoke(form, null);

            // 2. Session is invalid -> shows validation warning
            var invalidSession = new AssetSession { AssetFolderName = "" };
            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, invalidSession);
            handleCancelMethod.Invoke(form, null);

            // 3. Valid session, user chooses "Keep Working"
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_cancel_test", refSource, DateTimeOffset.Now);
            sessionField?.SetValue(form, session);

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => false; // Keep working
            handleCancelMethod.Invoke(form, null);
            Assert.NotNull(sessionField?.GetValue(form));

            // 4. Valid session, user confirms cancel
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => true; // Cancel asset
            handleCancelMethod.Invoke(form, null);
            Assert.Null(sessionField?.GetValue(form));

            MainForm.MessageBoxProvider = null;
            TwoChoiceDialog.CustomChoiceProvider = null;
        });
    }

    [Fact]
    public void MainForm_RecoverSessionOnStartup_AdditionalFaultBranches_Covered()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            // Broken session.json - user clicks Exit
            File.WriteAllText(Path.Combine(workspace.Root, AppConstants.SessionFileName), "{ broken json");
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => false; // Exit

            using var form1 = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
            recoverMethod?.Invoke(form1, null);

            // Invalid session - user clicks Delete Record
            var invalidSession = new AssetSession
            {
                ProjectName = "P",
                AssetRootFolder = workspace.Assets,
                AssetFolderName = "asset_inv",
                AssetFolder = Path.Combine(workspace.Assets, "asset_inv"),
                ReferenceSourcePath = "non_existent.png",
                ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset_inv", "reference", "ref.png"),
                ReferenceFilename = "ref.png",
                ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset_inv", "reference", "reference.md"),
                ReferenceHash = new string('0', 64),
                ReferenceProcessedAt = DateTimeOffset.Now
            };
            sessionService.Save(invalidSession);

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => true; // Delete record

            using var form2 = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            recoverMethod?.Invoke(form2, null);
            Assert.False(sessionService.Exists());

            // Invalid session - user clicks Exit
            sessionService.Save(invalidSession);
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => false; // Exit

            using var form3 = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            recoverMethod?.Invoke(form3, null);

            // Interrupted cancellation phase
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var cancelSession = processor.ProcessReference(settings, "asset_interrupted_cancel", refSource, DateTimeOffset.Now);
            cancelSession.CancelPhase = CancelPhase.Prepared;
            cancelSession.CancellationId = "abcdef1234567890abcdef1234567890";
            sessionService.Save(cancelSession);

            using var form4 = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            recoverMethod?.Invoke(form4, null);
            Assert.False(sessionService.Exists());

            TwoChoiceDialog.CustomChoiceProvider = null;
        });
    }

    [Fact]
    public void SessionService_Cancel_IntegrityAndFaultPaths_Covered()
    {
        using var workspace = new TestWorkspace();
        var sessionService = workspace.CreateSessionService();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_sess_cov", refSource, DateTimeOffset.Now);

        // ReferenceFilename unsafe path
        session.ReferenceFilename = @"sub\ref.png";
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        // AssetFolder does not match root + name
        session.ReferenceFilename = "ref.png";
        session.AssetFolder = Path.Combine(workspace.Assets, "other_folder");
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        // AssetFolder not direct child
        session.AssetFolderName = @"sub\nested";
        session.AssetFolder = Path.Combine(workspace.Assets, @"sub\nested");
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        // ReferenceDestinationPath inconsistent
        session.AssetFolderName = "asset_sess_cov";
        session.AssetFolder = Path.Combine(workspace.Assets, "asset_sess_cov");
        session.ReferenceDestinationPath = Path.Combine(workspace.Assets, "other.png");
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        // ReferenceProvenancePath inconsistent
        session.ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset_sess_cov", "reference", "ref.png");
        session.ReferenceProvenancePath = Path.Combine(workspace.Assets, "other.md");
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        // Invalid CancelPhase
        session.ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset_sess_cov", "reference", "reference.md");
        session.CancelPhase = (CancelPhase)99;
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        // CancelPhase.None with non-null CancellationId
        session.CancelPhase = CancelPhase.None;
        session.CancellationId = "abc";
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        // CancelPhase.Prepared with invalid CancellationId
        session.CancelPhase = CancelPhase.Prepared;
        session.CancellationId = "bad_hex_id";
        Assert.Throws<InvalidDataException>(() => sessionService.Cancel(session));

        // DirectoryNotFoundException resilience during Cancel
        var refSource2 = workspace.CreateImage("ref2.png", new byte[] { 1, 2, 3 });
        var session2 = processor.ProcessReference(settings, "asset_sess_cancel_dnf", refSource2, DateTimeOffset.Now);
        sessionService.Save(session2);
        
        SessionService.OnBeforeFolderCleanupHook = () =>
        {
            var refDir = Path.Combine(session2.AssetFolder, AppConstants.ReferenceFolderName);
            if (Directory.Exists(session2.AssetFolder))
            {
                Directory.Delete(session2.AssetFolder, true);
            }
        };

        try
        {
            sessionService.Cancel(session2); // should gracefully handle DirectoryNotFoundException during folder cleanup
            Assert.False(sessionService.Exists());
        }
        finally
        {
            SessionService.OnBeforeFolderCleanupHook = null;
        }
    }

    [Fact]
    public void ValidationService_SessionPathEscapes_Covered()
    {
        using var workspace = new TestWorkspace();

        var session = new AssetSession
        {
            ProjectName = "P",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_val_cov",
            AssetFolder = Path.Combine(workspace.Assets, "sub", "asset_val_cov"), // non-direct child
            ReferenceSourcePath = "ref.png",
            ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset_val_cov", "reference", "ref.png"),
            ReferenceFilename = "ref.png",
            ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset_val_cov", "reference", "reference.md"),
            ReferenceHash = new string('0', 64),
            ReferenceProcessedAt = DateTimeOffset.Now
        };

        var val = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
        Assert.False(val.IsValid);
        Assert.Contains(val.Errors, e => e.Contains("direct child") || e.Contains("does not match"));
    }

    [Fact]
    public void TemplateService_UnexpectedToken_ThrowsInvalidDataException()
    {
        var renderSinglePass = typeof(TemplateService).GetMethod("RenderSinglePass", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(renderSinglePass);

        var dict = new Dictionary<string, string> { ["{{VALID_TOKEN}}"] = "val" };
        var ex = Assert.Throws<TargetInvocationException>(() =>
            renderSinglePass.Invoke(null, new object[] { "Test {{UNKNOWN_TOKEN}} here", dict }));

        Assert.IsType<InvalidDataException>(ex.InnerException);
    }

    [Fact]
    public void ValidateExactFinalProvenanceOwnership_EdgeCases()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var session = new AssetSession
        {
            AssetFolder = workspace.Assets,
            AssetFolderName = "test",
            ProjectName = "proj",
            ReferenceFilename = "ref.png",
            ReferenceProcessedAt = DateTimeOffset.Now
        };

        // File does not exist
        var missingRes = validationService.ValidateExactFinalProvenanceOwnership(session, Path.Combine(workspace.Assets, "nonexistent.md"), templateService);
        Assert.False(missingRes.IsValid);

        // Metadata incomplete
        var tempFile = Path.Combine(workspace.Assets, "test.md");
        File.WriteAllText(tempFile, "hello");
        var incompleteRes = validationService.ValidateExactFinalProvenanceOwnership(session, tempFile, templateService);
        Assert.False(incompleteRes.IsValid);
        Assert.Contains(incompleteRes.Errors, e => e.Contains("incomplete"));
    }

    [Fact]
    public void ValidateExactReferenceProvenanceOwnership_FileDoesNotExist()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();
        var session = new AssetSession
        {
            AssetFolder = workspace.Assets,
            AssetFolderName = "test",
            ProjectName = "proj",
            ReferenceFilename = "ref.png",
            ReferenceProcessedAt = DateTimeOffset.Now
        };

        var missingRes = validationService.ValidateExactReferenceProvenanceOwnership(session, Path.Combine(workspace.Assets, "nonexistent.md"), templateService);
        Assert.False(missingRes.IsValid);
    }

    [Fact]
    public void RollbackMain_ReadFailure_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        var processedAt = DateTimeOffset.Now;
        var session = processor.ProcessReference(settings, "asset_rb_readfail", refSource, processedAt);
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = ValidationService.ComputeSha256(mainSource);
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";

        var mainPath = Path.Combine(session.AssetFolder, "main.png");
        File.WriteAllBytes(mainPath, new byte[] { 4, 5, 6 });

        using (var lockStream = new FileStream(mainPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var res = processor.RollbackMain(session);
            Assert.False(res.IsValid);
            Assert.Contains(res.Errors, e => e.Contains("Could not compute Main image SHA-256"));
        }
    }

    [Fact]
    public void RollbackMain_TempImageReadFailure_ReturnsFailure()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

        var processedAt = DateTimeOffset.Now;
        var session = processor.ProcessReference(settings, "asset_rb_tempreadfail", refSource, processedAt);
        session.IsMainCommitting = true;
        session.MainFilename = "main.png";
        session.MainPrompt = "Prompt";
        session.MainProcessedAt = processedAt;
        session.MainHash = ValidationService.ComputeSha256(mainSource);
        session.MainTransactionId = "0123456789abcdef0123456789abcdef";

        var tempImage = session.GetMainTempImagePath();
        File.WriteAllBytes(tempImage, new byte[] { 4, 5, 6 });

        using (var lockStream = new FileStream(tempImage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var res = processor.RollbackMain(session);
            Assert.False(res.IsValid);
            Assert.Contains(res.Errors, e => e.Contains("Could not compute Main temp image SHA-256"));
        }
    }

    [Fact]
    public void RollbackReferenceReplacement_ReadFailures_ReturnFailure()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 10, 20, 30 });
        var oldSession = processor.ProcessReference(settings, "asset_rb_readfail_tx", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 40, 50, 60 });
        var tx = processor.PrepareReferenceReplacement(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Lock backup reference -> Phase A2 catches exception
        using (var lockStream = new FileStream(tx.BackupReferencePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var res = processor.RollbackReferenceReplacement(tx);
            Assert.False(res.IsValid);
            Assert.Contains(res.Errors, e => e.Contains("Could not verify backup old reference image"));
        }

        // Lock new reference -> Phase A3 catches exception
        using (var lockStream = new FileStream(tx.NewSession.ReferenceDestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var res = processor.RollbackReferenceReplacement(tx);
            Assert.False(res.IsValid);
            Assert.Contains(res.Errors, e => e.Contains("Could not verify current new reference image"));
        }
    }

    #endregion
}

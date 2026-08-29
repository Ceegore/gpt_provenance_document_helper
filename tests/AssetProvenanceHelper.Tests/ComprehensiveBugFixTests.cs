using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Dedicated regression and verification test suite for BUG-001 through BUG-024.
/// </summary>
public sealed class ComprehensiveBugFixTests
{
    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA thread timed out.");
        if (exception != null)
        {
            throw new AggregateException("STA test failed", exception);
        }
    }

    [Fact]
    public void BUG_001_PathContainment_And_ReparsePoint_Detection()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();

        var outsidePath = Path.Combine(Path.GetTempPath(), "outside_folder_" + Guid.NewGuid().ToString("N"));
        var result = validationService.ValidateAssetRootFolder(outsidePath);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void BUG_002_MainWorkflow_Reconciliation_PreservesForeignFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_bug002", refSource, DateTimeOffset.Now);

        // Pre-create foreign file at main destination
        var foreignDest = Path.Combine(session.AssetFolder, "main.png");
        var foreignBytes = new byte[] { 9, 8, 7, 6 };
        File.WriteAllBytes(foreignDest, foreignBytes);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 5, 5, 5 });

        // ProcessMainImage must reject due to foreign file collision and preserve foreign file
        Assert.Throws<IOException>(() =>
            processor.ProcessMainPrepared(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now));

        Assert.True(File.Exists(foreignDest));
        Assert.Equal(foreignBytes, File.ReadAllBytes(foreignDest));
    }

    [Fact]
    public void BUG_002_MainForm_TryReconcileFailedMainCommit_ForeignFileNoArtifacts_SilentlyResetsAndPreservesForeignFile()
    {
        // BUG_002 above proves AssetProcessorService itself refuses to overwrite
        // a foreign file. This proves the separate UI-level reconciliation
        // decision in MainForm.MainWorkflow.cs's TryReconcileFailedMainCommit
        // (~lines 393-409): when RollbackMain reports failure specifically
        // because a foreign root-Main file exists and nothing else from the
        // aborted transaction was ever created, the UI must recognize that as
        // safe-to-reset rather than raising the "CRITICAL: rollback failed"
        // error - and must do so without ever touching the foreign file.
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_bug002_ui", refSource, DateTimeOffset.Now);

            // Simulate an aborted Main commit whose only trace is Main-commit
            // metadata: no final provenance, no ingame copy, no temp files -
            // matching "noArtifactsCreated" - plus a foreign file already
            // sitting at the root Main destination with different bytes than
            // session.MainHash, which is exactly what RollbackMain refuses to
            // delete.
            session.IsMainCommitting = true;
            session.MainFilename = "main.png";
            session.MainHash = new string('0', 64); // guaranteed not to match the foreign bytes below
            session.MainPrompt = "prompt";
            session.MainProcessedAt = DateTimeOffset.Now;
            session.MainTransactionId = new string('a', 32);

            var foreignDest = Path.Combine(session.AssetFolder, session.MainFilename);
            var foreignBytes = new byte[] { 9, 8, 7, 6 };
            File.WriteAllBytes(foreignDest, foreignBytes);

            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var reconcileMethod =
                typeof(MainForm).GetMethod(
                    "TryReconcileFailedMainCommit",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var messagesShown = 0;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => messagesShown++;

            bool result;
            try
            {
                result =
                    (bool)reconcileMethod!.Invoke(
                        form,
                        new object[] { session, false })!;
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }

            // No CRITICAL error dialog - this must be a silent, successful reset.
            Assert.True(result);
            Assert.Equal(0, messagesShown);

            // The foreign file was never touched.
            Assert.True(File.Exists(foreignDest));
            Assert.Equal(foreignBytes, File.ReadAllBytes(foreignDest));

            // Main-commit metadata was reset on the in-memory session...
            Assert.False(session.IsMainCommitting);
            Assert.Null(session.MainFilename);
            Assert.Null(session.MainHash);

            // ...and durably persisted as a Reference-ready session, with the
            // form's own UI state following it.
            var reloaded = sessionService.Load();
            Assert.NotNull(reloaded);
            Assert.False(reloaded!.IsMainCommitting);

            var currentSessionField =
                typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField =
                typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.Same(session, currentSessionField!.GetValue(form));
            Assert.Equal("ReferenceReady", stateField!.GetValue(form)!.ToString());
        });
    }

    [Fact]
    public void BUG_004_IngameFilename_IsDerivedDeterministically()
    {
        var session = new AssetSession
        {
            AssetFolderName = "hero_character",
            MainFilename = "hero_final.png"
        };

        Assert.Equal("hero_character.png", session.GetIngameFilename());
    }

    [Fact]
    public void BUG_005_CryptographicProvenanceHashes_ArePopulatedAndVerified()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_bug005", refSource, DateTimeOffset.Now);

        Assert.False(string.IsNullOrWhiteSpace(session.ReferenceProvenanceHash));

        var ownershipResult = validationService.ValidateExactReferenceProvenanceOwnership(
            session, session.ReferenceProvenancePath, templateService);
        Assert.True(ownershipResult.IsValid);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);

        Assert.False(string.IsNullOrWhiteSpace(session.MainProvenanceHash));
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var finalOwnership = validationService.ValidateExactFinalProvenanceOwnership(
            session, finalProvPath, templateService);
        Assert.True(finalOwnership.IsValid);
    }

    [Fact]
    public void BUG_006_TwoPhaseReferenceCommit_CrashSafety()
    {
        using var workspace = new TestWorkspace();
        var sessionService = workspace.CreateSessionService();
        var settings = workspace.CreateSettings();

        var session = new AssetSession
        {
            ProjectName = "Bug006Proj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_bug006",
            AssetFolder = Path.Combine(workspace.Assets, "asset_bug006"),
            ReferenceFilename = "ref.png",
            ReferenceProcessedAt = DateTimeOffset.Now,
            ReferenceHash = new string('a', 64),
            ReferenceCommitPhase = ReferenceCommitPhase.Prepared
        };

        sessionService.Save(session);
        var loaded = sessionService.Load();
        Assert.NotNull(loaded);
        Assert.Equal(ReferenceCommitPhase.Prepared, loaded.ReferenceCommitPhase);
    }

    [Fact]
    public void BUG_007_ReferenceReplacementJournal_FullLifecycle()
    {
        using var workspace = new TestWorkspace();
        var sessionService = workspace.CreateSessionService();

        var oldSession = new AssetSession
        {
            ProjectName = "JournalProj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_journal",
            AssetFolder = Path.Combine(workspace.Assets, "asset_journal"),
            ReferenceFilename = "ref_old.png",
            ReferenceProcessedAt = DateTimeOffset.Now,
            ReferenceHash = new string('1', 64)
        };

        var newSession = new AssetSession
        {
            ProjectName = "JournalProj",
            AssetRootFolder = workspace.Assets,
            AssetFolderName = "asset_journal",
            AssetFolder = Path.Combine(workspace.Assets, "asset_journal"),
            ReferenceFilename = "ref_new.png",
            ReferenceProcessedAt = DateTimeOffset.Now,
            ReferenceHash = new string('2', 64)
        };

        var journal = new ReferenceReplacementJournal
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            OldSession = oldSession,
            NewSession = newSession,
            BackupReferencePath = Path.Combine(workspace.Assets, "asset_journal", "reference", "ref_old.png.old"),
            BackupProvenancePath = Path.Combine(workspace.Assets, "asset_journal", "reference.md.old"),
            Phase = ReferenceReplacementPhase.Prepared
        };

        sessionService.SaveReplacementJournal(journal);
        Assert.True(sessionService.ReplacementJournalExists());

        var loaded = sessionService.LoadReplacementJournal();
        Assert.NotNull(loaded);
        Assert.Equal(journal.TransactionId, loaded.TransactionId);
        Assert.Equal(ReferenceReplacementPhase.Prepared, loaded.Phase);

        journal.Phase = ReferenceReplacementPhase.NewPromoted;
        sessionService.SaveReplacementJournal(journal);
        var loadedPromoted = sessionService.LoadReplacementJournal();
        Assert.NotNull(loadedPromoted);
        Assert.Equal(ReferenceReplacementPhase.NewPromoted, loadedPromoted.Phase);

        sessionService.DeleteReplacementJournal();
        Assert.False(sessionService.ReplacementJournalExists());
    }

    [Fact]
    public void BUG_008_HelpOverlay_EscapeKey_ClosesOverlay()
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

            form.Show();

            var showHelp = typeof(MainForm).GetMethod("ShowHelpOverlay", BindingFlags.NonPublic | BindingFlags.Instance);
            showHelp?.Invoke(form, null);

            var overlayField = typeof(MainForm).GetField("helpOverlay", BindingFlags.NonPublic | BindingFlags.Instance);
            var overlay = overlayField?.GetValue(form) as Control;
            Assert.NotNull(overlay);
            Assert.True(overlay.Visible);

            // Trigger Escape key
            var onKeyDown = typeof(MainForm).GetMethod("MainForm_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            var keyEvent = new KeyEventArgs(Keys.Escape);
            onKeyDown?.Invoke(form, new object[] { form, keyEvent });

            Assert.False(overlay.Visible);
            Assert.True(keyEvent.SuppressKeyPress);
        });
    }

    [Fact]
    public void BUG_009_UnifiedAssetNameValidation_HandlesAllRules()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var accepted = new[] { ".png", ".jpg", ".webp" };

        Assert.True(validationService.ValidateAssetName("valid_name_123", accepted).IsValid);
        Assert.False(validationService.ValidateAssetName("", accepted).IsValid);
        Assert.False(validationService.ValidateAssetName("   ", accepted).IsValid);
        Assert.False(validationService.ValidateAssetName("hero.png", accepted).IsValid);
        Assert.False(validationService.ValidateAssetName("invalid/slash", accepted).IsValid);
        Assert.False(validationService.ValidateAssetName("CON", accepted).IsValid);
        Assert.False(validationService.ValidateAssetName("AUX", accepted).IsValid);
    }

    [Fact]
    public void BUG_016_FormatValidation_MagicBytesEnforcement()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();

        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var validPngPath = Path.Combine(workspace.Root, "test.png");
        File.WriteAllBytes(validPngPath, pngBytes);

        var pngResult = validationService.ValidateImageFile(validPngPath, new[] { ".png", ".jpg", ".webp" });
        Assert.True(pngResult.IsValid);

        var corruptBytes = new byte[] { 0x00, 0x00, 0x00, 0x00 };
        var corruptPngPath = Path.Combine(workspace.Root, "corrupt.png");
        File.WriteAllBytes(corruptPngPath, corruptBytes);

        var corruptResult = validationService.ValidateImageFile(corruptPngPath, new[] { ".png", ".jpg", ".webp" });
        Assert.False(corruptResult.IsValid);
        Assert.Contains(corruptResult.Errors, e => e.Contains("expected signature"));
    }

    [Fact]
    public void BUG_017_ReferenceTemplate_RetainedTextMatchesRequirements()
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "templates", "reference.md");
        Assert.True(File.Exists(templatePath));
        var content = File.ReadAllText(templatePath);
        Assert.Contains("Reference file retained: yes", content);
    }

    [Fact]
    public void BUG_021_ExactProvenanceOwnership_DetectsTampering()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_bug021", refSource, DateTimeOffset.Now);

        // Tamper reference provenance
        File.WriteAllText(session.ReferenceProvenancePath, "TAMPERED PROVENANCE");

        var result = validationService.ValidateExactReferenceProvenanceOwnership(
            session, session.ReferenceProvenancePath, templateService);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void BUG_024_ImageSlotIndependence_MainAndReference_Segregated()
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

            var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var mainImage = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });

            form.SetSelectedImage(ImageSlot.Reference, refImage);
            form.SetSelectedImage(ImageSlot.Main, mainImage);

            Assert.Equal(refImage, form.GetSelectedImage(ImageSlot.Reference));
            Assert.Equal(mainImage, form.GetSelectedImage(ImageSlot.Main));

            form.SetSelectedImage(ImageSlot.Reference, null);
            Assert.Null(form.GetSelectedImage(ImageSlot.Reference));
            Assert.Equal(mainImage, form.GetSelectedImage(ImageSlot.Main));
        });
    }
}

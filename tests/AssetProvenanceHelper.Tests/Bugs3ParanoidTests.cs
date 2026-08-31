using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

[Trait("Category", "RecoveryCritical")]
public sealed class Bugs3ParanoidTests
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
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            thread.Interrupt();
            throw new TimeoutException("STA thread timed out.");
        }

        if (error != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private static void RunStartupRecovery(
        TestWorkspace workspace,
        AppSettings settings,
        AssetProcessorService processor,
        SessionService sessionService)
    {
        var replPhaseSavingHook = SessionService.OnReplacementPhaseSavingHook;
        var replJournalDeleteHook = SessionService.OnBeforeReplacementJournalDeleteHook;
        var cancelPhaseSavingHook = SessionService.OnCancelPhaseSavingHook;
        var cancelFileMoveHook = SessionService.OnBeforeCancelFileMoveHook;
        var cancelFileDeleteHook = SessionService.OnBeforeCancelFileDeleteHook;
        var cancelRestoreHook = SessionService.OnBeforeCancelRestoreHook;
        var folderCleanupHook = SessionService.OnBeforeFolderCleanupHook;
        var fileAttrsProvider = ValidationService.FileAttributesProvider;

        RunOnSta(() =>
        {
            SessionService.OnReplacementPhaseSavingHook = replPhaseSavingHook;
            SessionService.OnBeforeReplacementJournalDeleteHook = replJournalDeleteHook;
            SessionService.OnCancelPhaseSavingHook = cancelPhaseSavingHook;
            SessionService.OnBeforeCancelFileMoveHook = cancelFileMoveHook;
            SessionService.OnBeforeCancelFileDeleteHook = cancelFileDeleteHook;
            SessionService.OnBeforeCancelRestoreHook = cancelRestoreHook;
            SessionService.OnBeforeFolderCleanupHook = folderCleanupHook;
            ValidationService.FileAttributesProvider = fileAttrsProvider;

            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

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
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
                SessionService.OnReplacementPhaseSavingHook = null;
                SessionService.OnBeforeReplacementJournalDeleteHook = null;
                SessionService.OnCancelPhaseSavingHook = null;
                SessionService.OnBeforeCancelFileMoveHook = null;
                SessionService.OnBeforeCancelFileDeleteHook = null;
                SessionService.OnBeforeCancelRestoreHook = null;
                SessionService.OnBeforeFolderCleanupHook = null;
                ValidationService.FileAttributesProvider = null;
            }
        });
    }

    [Fact]
    public void R3_001_RollbackReferenceReplacement_ForeignTempReference_FailsAndPreservesTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_001_foreign_ref", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);

        // Tamper temp reference image with foreign bytes
        File.WriteAllBytes(tx.TempNewReferencePath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 99, 99 });

        var rollback = processor.RollbackReferenceReplacement(tx);

        Assert.False(rollback.IsValid);
        Assert.Contains(rollback.Errors, e => e.Contains("replacement temp Reference", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(tx.TempNewReferencePath), "Foreign temp file must be preserved");
    }

    [Fact]
    public void R3_001_RollbackReferenceReplacement_ForeignTempProvenance_FailsAndPreservesTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_001_foreign_prov", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);

        // Tamper temp provenance with foreign content
        File.WriteAllText(tx.TempNewProvenancePath, "FOREIGN UNRELATED PROVENANCE");

        var rollback = processor.RollbackReferenceReplacement(tx);

        Assert.False(rollback.IsValid);
        Assert.Contains(rollback.Errors, e => e.Contains("replacement temp provenance", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(tx.TempNewProvenancePath), "Foreign temp file must be preserved");
    }

    [Fact]
    public void R3_002_SameFilename_OldAuthorityCheckedStrictly_FailsIfOldSessionTampered()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_002_tampered_old", ref1, DateTimeOffset.Now);

        var otherDir = Path.Combine(workspace.Root, "other_source");
        Directory.CreateDirectory(otherDir);
        var ref2 = Path.Combine(otherDir, "ref.png");
        var tempPng = workspace.CreateImage("temp.png", new byte[] { 4, 5, 6, 7 });
        File.Copy(tempPng, ref2, overwrite: true);

        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        // Deep clone before tampering so tx.OldSession retains original authority
        var persistedTampered = System.Text.Json.JsonSerializer.Deserialize<AssetSession>(
            System.Text.Json.JsonSerializer.Serialize(tx.OldSession))!;
        persistedTampered.ReferenceHash = new string('f', 64);
        sessionService.Save(persistedTampered);

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.NewPromoted));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        // Recovery must fail closed because session.json matches neither Old nor New authority
        Assert.True(sessionService.ReplacementJournalExists(), "Journal must be preserved when authority matches neither Old nor New");
    }

    [Fact]
    public void R3_003_HandleReplaceReference_CleanupLocked_LeavesCleanupPendingJournalForRecovery()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_003_lock_rec", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        FileStream? lockStream = null;

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            MainForm.OnBeforeReferenceReplacementCommit = tx =>
            {
                lockStream = new FileStream(tx.BackupReferencePath, FileMode.Open, FileAccess.Read, FileShare.None);
            };

            try
            {
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleReplaceMethod?.Invoke(form, null);

                Assert.True(sessionService.ReplacementJournalExists(), "CleanupPending journal must be preserved");
                var journal = sessionService.LoadReplacementJournal();
                Assert.NotNull(journal);
                Assert.Equal(ReferenceReplacementPhase.CleanupPending, journal.Phase);
            }
            finally
            {
                lockStream?.Dispose();
                MainForm.OnBeforeReferenceReplacementCommit = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });

        // Now run startup recovery with unlocked backup
        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists(), "Journal must be deleted after successful commit-forward recovery");
        var finalSession = sessionService.Load()!;
        Assert.Equal("ref2.png", finalSession.ReferenceFilename);
    }

    [Fact]
    public void R3_004_HandleReference_ProcessReferenceThrows_RollsBackPreparedSessionAndDeletesJournal()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var txtFolder = (TextBox)typeof(MainForm).GetField("txtAssetFolderName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            txtFolder.Text = "asset_r3_004_throw";

            form.SetSelectedImage(ImageSlot.Reference, ref1);

            // Hook into OnFileCopiedHook to simulate failure during ProcessReference
            AssetProcessorService.OnFileCopiedHook = (src, dest) =>
            {
                throw new IOException("Simulated disk write failure during ProcessReference.");
            };

            try
            {
                var handleRefMethod = typeof(MainForm).GetMethod("HandleReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleRefMethod?.Invoke(form, null);

                // Prepared session was rolled back and deleted
                Assert.False(sessionService.Exists(), "Prepared session journal must be deleted after rollback");

                var assetDir = Path.Combine(settings.AssetRootFolder, "asset_r3_004_throw");
                Assert.False(Directory.Exists(assetDir), "Tool-created asset folder must be rolled back");
            }
            finally
            {
                AssetProcessorService.OnFileCopiedHook = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R3_006_PreparedReference_ExistingParentFolder_PreservesPreexistingFolder()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.CreateReferenceSession(settings, "asset_r3_006_preexisting", ref1, DateTimeOffset.Now);

        // Pre-existing asset directory
        session.WasAssetFolderCreatedByTool = false;
        session.WasReferenceFolderCreatedByTool = true;
        sessionService.Save(session);

        Directory.CreateDirectory(session.AssetFolder);
        var refFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
        Directory.CreateDirectory(refFolder);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.Exists());
        Assert.False(Directory.Exists(refFolder), "Tool-created reference folder must be deleted");
        Assert.True(Directory.Exists(session.AssetFolder), "Pre-existing asset folder must be preserved");
    }

    [Fact]
    public void R3_007_HandleMainImage_RootMainDestinationCollision_AbortsBeforeJournalPersisted()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_007_abort", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 7, 8, 9 });

        // Pre-create destination main image in asset folder
        var destMain = Path.Combine(session.AssetFolder, "main1.png");
        File.WriteAllBytes(destMain, new byte[] { 99, 99, 99 });

        var messages = new List<string>();

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, msg, _, _, _) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Main, main1);
            var txtPrompt = (TextBox)typeof(MainForm).GetField("txtPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            txtPrompt.Text = "valid prompt";

            try
            {
                var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
                handleMainMethod?.Invoke(form, null);

                // Validation error shown
                Assert.Contains(messages, m => m.Contains("already exists") || m.Contains("unavailable"));

                // Session.json must NOT have IsMainCommitting = true
                var loaded = sessionService.Load()!;
                Assert.False(loaded.IsMainCommitting, "Journal must not be updated to committing state on preflight failure");
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R3_008_MainSessionDeleteFailure_NoReferenceMode_RollsBackAndCleansUp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var main1 = workspace.CreateImage("main1.png", new byte[] { 7, 8, 9 });
        var processedAt = DateTimeOffset.Now;

        var session = processor.CreateNoReferenceMainSession(
            settings,
            "asset_r3_008_noref",
            main1,
            "test prompt",
            processedAt);

        sessionService.Save(session);

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => false;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            var deleteAttempts = 0;
            SessionService.OnBeforeSessionDeleteHook = () =>
            {
                deleteAttempts++;
                if (deleteAttempts == 1)
                {
                    throw new IOException("Simulated session deletion failure.");
                }
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

                typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, session);

                var executeMethod = typeof(MainForm).GetMethod("ExecuteMainCommit", BindingFlags.NonPublic | BindingFlags.Instance);
                executeMethod?.Invoke(form, new object[] { session, main1, "test prompt", processedAt, false });

                // Main outputs rolled back
                var rootMain = Path.Combine(session.AssetFolder, "main1.png");
                var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
                Assert.False(File.Exists(rootMain), "Main output must be rolled back");
                Assert.False(File.Exists(finalProv), "Final provenance must be rolled back");

                // In NoReference mode, session.json was deleted on retry
                Assert.False(sessionService.Exists(), "NoReference session should be cleaned up on retry delete");
            }
            finally
            {
                SessionService.OnBeforeSessionDeleteHook = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R3_009_ResumeReference_TamperedProvenance_FailsExactValidationAndPromptsDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_009_tampered_prov", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        // Tamper provenance by appending text so exact hash check fails
        File.AppendAllText(session.ReferenceProvenancePath, "\n\nTAMPERED TEXT");

        var dialogShown = false;

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) =>
            {
                dialogShown = true;
                return true; // Delete corrupt record
            };
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

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

                Assert.True(dialogShown, "Corrupt session dialog must be shown when exact provenance check fails");
                Assert.False(sessionService.Exists(), "Corrupt session record should be deleted");
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R3_010_ValidatePreparedReferenceSession_MalformedPaths_ReturnsFailureNotThrows()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.CreateReferenceSession(settings, "asset_r3_010_malformed", ref1, DateTimeOffset.Now);

        session.ReferenceDestinationPath = "C:\\invalid\0path\\ref.png";

        var result = validationService.ValidatePreparedReferenceSession(session);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("invalid") || e.Contains("unusable") || e.Contains("path"));
    }

    [Fact]
    public void R3_010_ValidateReferenceReplacementJournal_MalformedPaths_ReturnsFailureNotThrows()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_010_jour_malformed", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        var journal = tx.ToJournal(ReferenceReplacementPhase.Prepared);
        journal.BackupReferencePath = "C:\\invalid\0path\\ref.old";

        var result = validationService.ValidateReferenceReplacementJournal(journal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("invalid") || e.Contains("unusable") || e.Contains("path"));
    }

    [Fact]
    public void R3_011_PrepareReferenceReplacement_IsInternal()
    {
        var method = typeof(AssetProcessorService).GetMethod(
            "PrepareReferenceReplacement",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(method.IsAssembly, "PrepareReferenceReplacement must be internal (IsAssembly)");
        Assert.False(method.IsPublic, "PrepareReferenceReplacement must NOT be public");
    }

    [Fact]
    public void R3_014_PrepareMainCommit_CustomExtensions_AcceptsCustomAndRejectsDefault()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var customExts = new[] { ".webp" };

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_014_ext", ref1, DateTimeOffset.Now);

        var customImage = workspace.CreateImage("main.webp", TestWorkspace.GetValidImageBytesForExtension("main.webp"));
        var standardPng = workspace.CreateImage("main.png", new byte[] { 40, 50, 60 });
        var now = DateTimeOffset.Now;

        // Accepting custom extension
        var preparedSession = processor.PrepareMainCommit(session, customExts, customImage, "prompt", now);
        Assert.True(preparedSession.IsMainCommitting);
        Assert.Equal("main.webp", preparedSession.MainFilename);

        // Rejecting standard .png when custom extension set is passed
        session.ResetMainCommitMetadata();
        Assert.Throws<InvalidDataException>(() =>
            processor.PrepareMainCommit(session, customExts, standardPng, "prompt", now));
    }

    [Fact]
    public void R4_007_Replacement_Prepared_TempReferenceOnly_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_prep_refonly", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Only create temp reference, no temp provenance
        File.Copy(ref2, tx.TempNewReferencePath);
        Assert.True(File.Exists(tx.TempNewReferencePath));
        Assert.False(File.Exists(tx.TempNewProvenancePath));

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.Prepared));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.TempNewReferencePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
    }

    [Fact]
    public void R4_007_Replacement_OldBackupPending_NoMove_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_obp_nomove", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        // Phase is OldBackupPending but no move performed yet
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.OldBackupPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.TempNewReferencePath));
        Assert.False(File.Exists(tx.TempNewProvenancePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void R4_007_Replacement_OldBackupPending_ReferenceMovedOnly_RestoresOld()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_obp_refmoved", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        // Move old reference only to backup
        File.Move(session.ReferenceDestinationPath, tx.BackupReferencePath);

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.OldBackupPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void R4_007_Replacement_OldBackupPending_BothMoved_RestoresOld()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_obp_bothmoved", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.OldBackupPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void R4_007_Replacement_NewPromotionPending_NoPromote_RestoresOld()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_npp_nopromote", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.NewPromotionPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
    }

    [Fact]
    public void R4_007_Replacement_SessionSwitchPending_OldSessionDifferentFilename_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_ssp_diff", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        sessionService.Save(tx.OldSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.SessionSwitchPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(tx.NewSession.ReferenceDestinationPath));
        var loaded = sessionService.Load()!;
        Assert.Equal("ref1.png", loaded.ReferenceFilename);
    }

    [Fact]
    public void R4_007_Replacement_SessionSwitchPending_OldSessionSameFilename_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_ssp_same", ref1, DateTimeOffset.Now);

        var otherDir = Path.Combine(workspace.Root, "other_dir_ssp");
        Directory.CreateDirectory(otherDir);
        var ref2 = Path.Combine(otherDir, "ref.png");
        var tempPng = workspace.CreateImage("temp_ssp.png", new byte[] { 4, 5, 6, 7 });
        File.Copy(tempPng, ref2, overwrite: true);

        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        sessionService.Save(tx.OldSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.SessionSwitchPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
        var loaded = sessionService.Load()!;
        Assert.Equal(session.ReferenceHash, loaded.ReferenceHash);
        Assert.Equal(session.ReferenceProvenanceHash, loaded.ReferenceProvenanceHash);
    }

    [Fact]
    public void R4_007_Replacement_SessionSwitched_NewSession_CommitsForward()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_switched_new", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        sessionService.Save(tx.NewSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.SessionSwitched));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
        var loaded = sessionService.Load()!;
        Assert.Equal("ref2.png", loaded.ReferenceFilename);
    }

    [Fact]
    public void R4_007_Replacement_CleanupPending_OneBackupDeleted_CompletesCleanup()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_007_cleanup_one", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        sessionService.Save(tx.NewSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.CleanupPending));

        // Delete one backup file prior to crash
        File.Delete(tx.BackupReferencePath);
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.True(File.Exists(tx.BackupProvenancePath));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupProvenancePath));
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
    }

    [Fact]
    public void R4_001_Replacement_Prepared_SourceChangesBeforeTempCopy_Throws()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_001_src_drift", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Modify source image after transaction was created
        File.WriteAllBytes(ref2, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 99, 88, 77 });

        var ex = Assert.Throws<IOException>(() =>
            processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions));

        Assert.Contains("Replacement Reference source changed", ex.Message);
    }

    [Fact]
    public void R4_001_Replacement_Prepared_TemplateChangesBeforeTempProvenance_Throws()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_001_tmpl_drift", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Modify template file after transaction was created
        File.WriteAllText(workspace.ReferenceTemplatePath, """
            # AI ASSET RIGHTS / PROVENANCE RECORD - MODIFIED

            Asset ID: {{REFERENCE_FILENAME}}
            Project: {{PROJECT}}
            Generation date: {{GENERATION_DATE}}

            MODIFIED_MARKER
            """);

        var ex = Assert.Throws<InvalidDataException>(() =>
            processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions));

        Assert.Contains("Replacement provenance changed after the Prepared transaction was created", ex.Message);
    }

    [Fact]
    public void R4_002_OldReferenceProvenanceAppended_ReplacementNeverJournals()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_002_tampered_prov", ref1, DateTimeOffset.Now);

        // Tamper old reference provenance before replacement
        File.AppendAllText(session.ReferenceProvenancePath, "\n\nFOREIGN MODIFICATION");

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var ex = Assert.Throws<InvalidDataException>(() =>
            processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));

        Assert.Contains("Current Reference output is inconsistent or modified and cannot be replaced", ex.Message);
    }

    [Fact]
    public void R4_002_OldReferenceImageTampered_ReplacementNeverJournals()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_002_tampered_img", ref1, DateTimeOffset.Now);

        // Tamper old reference image before replacement
        File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 99, 99 });

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var ex = Assert.Throws<InvalidDataException>(() =>
            processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));

        Assert.Contains("ReferenceHash does not match", ex.Message);
    }

    [Fact]
    public void R4_003_SessionSwitchedPhaseSaveFails_OldSessionSaveFails_Closes()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_003_fail_save", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var messages = new List<string>();

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, msg, _, _, _) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            FileStream? sessionLock = null;
            SessionService.OnReplacementPhaseSavingHook = (phase, j) =>
            {
                if (phase == ReferenceReplacementPhase.SessionSwitched)
                {
                    // Lock session file so Save(OldSession) in FinalizeLiveReplacementRollback fails
                    sessionLock = new FileStream(sessionService.SessionFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    throw new IOException("Simulated disk error during SessionSwitched phase saving.");
                }
            };

            try
            {
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleReplaceMethod?.Invoke(form, null);

                Assert.Contains(messages, m => m.Contains("could not be persisted") || m.Contains("CRITICAL"));
            }
            finally
            {
                sessionLock?.Dispose();
                SessionService.OnReplacementPhaseSavingHook = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R4_003_RollbackSucceeds_JournalDeleteFails_Closes()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_003_jdel_fail", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var messages = new List<string>();

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, msg, _, _, _) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            SessionService.OnReplacementPhaseSavingHook = (phase, j) =>
            {
                if (phase == ReferenceReplacementPhase.SessionSwitched)
                {
                    throw new IOException("Simulated error during SessionSwitched phase saving.");
                }
            };

            SessionService.OnBeforeReplacementJournalDeleteHook = () =>
            {
                throw new IOException("Simulated journal delete failure after rollback.");
            };

            try
            {
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleReplaceMethod?.Invoke(form, null);

                Assert.Contains(messages, m => m.Contains("replacement journal could not be removed") || m.Contains("CRITICAL"));
            }
            finally
            {
                SessionService.OnReplacementPhaseSavingHook = null;
                SessionService.OnBeforeReplacementJournalDeleteHook = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R4_003_CleanupSucceeds_JournalDeleteFails_Closes()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_003_clean_jdel_fail", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var messages = new List<string>();

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, msg, _, _, _) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            MainForm.OnBeforeReferenceReplacementCommit = tx =>
            {
                SessionService.OnBeforeReplacementJournalDeleteHook = () =>
                {
                    throw new IOException("Simulated failure deleting journal after successful cleanup.");
                };
            };

            try
            {
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleReplaceMethod?.Invoke(form, null);

                Assert.Contains(messages, m => m.Contains("replacement journal could not be removed") || m.Contains("CRITICAL"));
            }
            finally
            {
                MainForm.OnBeforeReferenceReplacementCommit = null;
                SessionService.OnBeforeReplacementJournalDeleteHook = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R4_004_ReplacementJournalPlusActiveMain_FailsClosedPreservesMainJournal()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_004_active_main", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        // Create a real valid replacement transaction with deterministic paths
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.Prepared));

        // Deep-clone tx.OldSession for the durable session on disk and set IsMainCommitting = true
        var current = System.Text.Json.JsonSerializer.Deserialize<AssetSession>(
            System.Text.Json.JsonSerializer.Serialize(tx.OldSession))!;
        current.IsMainCommitting = true;
        current.MainFilename = "main.png";
        current.MainPrompt = "prompt";
        current.MainProcessedAt = DateTimeOffset.Now;
        current.MainHash = new string('a', 64);
        current.MainProvenanceHash = new string('b', 64);
        current.MainTransactionId = Guid.NewGuid().ToString("N");
        sessionService.Save(current);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        // Must fail closed: both journals preserved because durable session is not stable Reference authority
        Assert.True(sessionService.ReplacementJournalExists(), "Replacement journal must be preserved");
        Assert.True(sessionService.Exists(), "Main session journal must be preserved");
        var loaded = sessionService.Load()!;
        Assert.True(loaded.IsMainCommitting, "Main committing state must NOT be overwritten");
    }

    [Fact]
    public void R4_004_ReplacementJournalPlusCancelPrepared_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_004_active_cancel", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.Prepared));

        var current = System.Text.Json.JsonSerializer.Deserialize<AssetSession>(
            System.Text.Json.JsonSerializer.Serialize(tx.OldSession))!;
        current.CancelPhase = CancelPhase.Prepared;
        current.CancellationId = Guid.NewGuid().ToString("N");
        sessionService.Save(current);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.True(sessionService.ReplacementJournalExists());
        Assert.True(sessionService.Exists());
    }

    [Fact]
    public void R4_004_ReplacementJournalPlusPreparedReference_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_004_active_prep_ref", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.Prepared));

        var current = System.Text.Json.JsonSerializer.Deserialize<AssetSession>(
            System.Text.Json.JsonSerializer.Serialize(tx.OldSession))!;
        current.ReferenceCommitPhase = ReferenceCommitPhase.Prepared;
        current.ReferenceTransactionId = Guid.NewGuid().ToString("N");
        sessionService.Save(current);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.True(sessionService.ReplacementJournalExists());
        Assert.True(sessionService.Exists());
    }

    [Fact]
    public void R4_005_IngameEnumerationUnauthorized_ReturnsValidationFailure()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_005_unauth", ref1, DateTimeOffset.Now);

        var ingameFolder = session.GetIngameFolderPath();
        Directory.CreateDirectory(ingameFolder);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 7, 8, 9 });

        ValidationService.EnumerateFilesInFolderHook = path =>
        {
            throw new UnauthorizedAccessException("Access to ingame folder is denied.");
        };

        try
        {
            var result = validationService.ValidateMainDestinationAvailability(session, settings.AcceptedExtensions, main1);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("Could not inspect ingame folder") || e.Contains("denied"));
        }
        finally
        {
            ValidationService.EnumerateFilesInFolderHook = null;
        }
    }

    [Fact]
    public void R4_005_IngameEnumerationIOException_ReturnsValidationFailure()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r4_005_ioex", ref1, DateTimeOffset.Now);

        var ingameFolder = session.GetIngameFolderPath();
        Directory.CreateDirectory(ingameFolder);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 7, 8, 9 });

        ValidationService.EnumerateFilesInFolderHook = path =>
        {
            throw new IOException("I/O device error while reading ingame folder.");
        };

        try
        {
            var result = validationService.ValidateMainDestinationAvailability(session, settings.AcceptedExtensions, main1);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("Could not inspect ingame folder") || e.Contains("I/O device error"));
        }
        finally
        {
            ValidationService.EnumerateFilesInFolderHook = null;
        }
    }

    [Fact]
    public void R5_001_RollbackReplacement_ExternalTempReferencePath_RejectsAndPreserves()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var oldSource = workspace.CreateImage("old.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "r5_external_temp_ref", oldSource, DateTimeOffset.Now);

        var newSource = workspace.CreateImage("new.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, newSource, DateTimeOffset.Now);

        var outsideFolder = Path.Combine(workspace.Root, "OUTSIDE_REF");
        Directory.CreateDirectory(outsideFolder);
        var outsideFile = Path.Combine(outsideFolder, "do-not-delete.png");
        File.Copy(newSource, outsideFile);

        tx.TempNewReferencePath = outsideFile;

        var result = processor.RollbackReferenceReplacement(tx);
        Assert.False(result.IsValid);
        Assert.True(File.Exists(outsideFile), "External matching file must never be deleted.");
    }

    [Fact]
    public void R5_001_RollbackReplacement_ExternalTempProvenancePath_RejectsAndPreserves()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var oldSource = workspace.CreateImage("old.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "r5_external_temp_prov", oldSource, DateTimeOffset.Now);

        var newSource = workspace.CreateImage("new.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, newSource, DateTimeOffset.Now);

        var outsideFolder = Path.Combine(workspace.Root, "OUTSIDE_PROV");
        Directory.CreateDirectory(outsideFolder);
        var outsideFile = Path.Combine(outsideFolder, "do-not-delete.tmp");
        File.WriteAllText(outsideFile, "SOME CONTENT");

        tx.TempNewProvenancePath = outsideFile;

        var result = processor.RollbackReferenceReplacement(tx);
        Assert.False(result.IsValid);
        Assert.True(File.Exists(outsideFile), "External matching provenance file must never be deleted.");
    }

    [Fact]
    public void R5_002_RawMutators_AreInternalNotPublic()
    {
        var rawMutators = new[]
        {
            ("ProcessMainImage", new[] { typeof(AssetSession), typeof(IReadOnlyCollection<string>), typeof(string), typeof(string), typeof(DateTimeOffset) }),
            ("ProcessReference", new[] { typeof(AssetSession), typeof(AppSettings), typeof(string), typeof(DateTimeOffset) }),
            ("CreateReplacementTempFiles", new[] { typeof(ReferenceReplacementTransaction), typeof(IReadOnlyCollection<string>) }),
            ("BackupOldReference", new[] { typeof(ReferenceReplacementTransaction) }),
            ("PromoteNewReference", new[] { typeof(ReferenceReplacementTransaction) }),
            ("RollbackReferenceReplacement", new[] { typeof(ReferenceReplacementTransaction) }),
            ("CleanupReplacementBackups", new[] { typeof(ReferenceReplacementTransaction) }),
            ("CommitReferenceReplacement", new[] { typeof(ReferenceReplacementTransaction) }),
            ("RollbackMain", new[] { typeof(AssetSession), typeof(string) }),
            ("RollbackReference", new[] { typeof(AssetSession) }),
            ("CopyFileWithoutOverwrite", new[] { typeof(string), typeof(string) }),
            ("WriteTextAtomic", new[] { typeof(string), typeof(string) }),
        };

        foreach (var (methodName, parameterTypes) in rawMutators)
        {
            var method = typeof(AssetProcessorService).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: parameterTypes,
                modifiers: null);

            Assert.NotNull(method);
            Assert.True(method.IsAssembly, $"{methodName} must be internal (IsAssembly).");
            Assert.False(method.IsPublic, $"{methodName} must NOT be public.");
        }
    }

    [Fact]
    public void R5_003_CreateReplacementTemps_ReferenceFolderBecomesReparse_Rejects()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r5_003_temp_reparse", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        var referenceFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);

        ValidationService.FileAttributesProvider = path =>
        {
            if (ValidationService.PathsEqual(path, referenceFolder))
            {
                return FileAttributes.Directory | FileAttributes.ReparsePoint;
            }
            return File.GetAttributes(path);
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions));

            Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(tx.TempNewReferencePath));
            Assert.True(File.Exists(session.ReferenceDestinationPath));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    public void R5_003_BackupOldReference_ReferenceFolderBecomesReparse_Rejects()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r5_003_bak_reparse", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);

        var referenceFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);

        ValidationService.FileAttributesProvider = path =>
        {
            if (ValidationService.PathsEqual(path, referenceFolder))
            {
                return FileAttributes.Directory | FileAttributes.ReparsePoint;
            }
            return File.GetAttributes(path);
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                processor.BackupOldReference(tx));

            Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(session.ReferenceDestinationPath));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    public void R5_003_PromoteNewReference_ReferenceFolderBecomesReparse_Rejects()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r5_003_prom_reparse", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var referenceFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);

        ValidationService.FileAttributesProvider = path =>
        {
            if (ValidationService.PathsEqual(path, referenceFolder))
            {
                return FileAttributes.Directory | FileAttributes.ReparsePoint;
            }
            return File.GetAttributes(path);
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                processor.PromoteNewReference(tx));

            Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    public void R5_004_SaveNewSessionFailure_RollsBackExactlyOnce()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r5_004_save_new_fail", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var messages = new List<string>();

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, msg, _, _, _) => messages.Add(msg);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            // Hook Save to fail when saving NewSession (ref2.png)
            var saveCallCount = 0;
            SessionService.OnBeforeSaveSessionHook = s =>
            {
                if (string.Equals(s.ReferenceFilename, "ref2.png", StringComparison.OrdinalIgnoreCase) && ++saveCallCount == 1)
                {
                    throw new IOException("Simulated Save(NewSession) failure.");
                }
            };

            var rollbackCount = 0;
            AssetProcessorService.OnRollbackReferenceReplacementInvoked = tx =>
            {
                rollbackCount++;
            };

            try
            {
                // Perform live replacement
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleReplaceMethod?.Invoke(form, null);

                // Check message reported previous reference was restored
                Assert.Contains(messages, m => m.Contains("previous Reference was restored") || m.Contains("previous reference was restored") || m.Contains("CRITICAL"));
                Assert.Equal(1, rollbackCount);
            }
            finally
            {
                SessionService.OnBeforeSaveSessionHook = null;
                AssetProcessorService.OnRollbackReferenceReplacementInvoked = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R5_006_Replacement_NewPromotionPending_BothPromoted_RestoresOld()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r5_006_npp_bothpromoted", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        sessionService.Save(tx.OldSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.NewPromotionPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists(), "Journal must be removed after successful rollback");
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));
        var loaded = sessionService.Load()!;
        Assert.Equal("ref1.png", loaded.ReferenceFilename);
        Assert.Equal(session.ReferenceHash, loaded.ReferenceHash);
    }

    [Fact]
    public void R5_007_Settings_CustomUnknownExtension_Rejected()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();

        settings.AcceptedExtensions.Add(".customimg");

        var result = validationService.ValidateProcessingSettings(settings);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Unsupported image extension configured") || e.Contains(".customimg"));
    }

    [Fact]
    public void R5_007_Image_UnknownConfiguredExtension_Rejected()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();

        var customPath = workspace.CreateImage("sample.customimg", new byte[] { 10, 20, 30, 40 });

        var result = validationService.ValidateImageFile(customPath, new[] { ".customimg" });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("header does not match expected signature") || e.Contains("signature"));
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_Prepared_SourceChangesBeforeProcess_NoMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r6_source_drift", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        // Change source after durable Prepared authority exists
        File.WriteAllBytes(source, TestWorkspace.EnsureMagicBytes(source, new byte[] { 9, 9, 9 }));

        Assert.Throws<IOException>(() =>
            processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));

        Assert.False(Directory.Exists(prepared.AssetFolder), "Authority drift must be rejected before folder creation.");
        Assert.True(sessionService.Exists(), "Prepared journal remains durable.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_Prepared_TemplateChangesBeforeProcess_NoMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r6_template_drift", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        // Modify reference.md after durable Prepared authority exists
        var templateFile = Path.Combine(workspace.Root, "templates", "reference.md");
        File.WriteAllText(templateFile, "# ALTERED TEMPLATE CONTENT {{ProjectName}}");

        Assert.Throws<InvalidDataException>(() =>
            processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));

        Assert.False(Directory.Exists(prepared.AssetFolder), "Template drift must be rejected before folder creation.");
        Assert.True(sessionService.Exists(), "Prepared journal remains durable.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_Prepared_SameInstantDifferentOffset_IsRejected()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var preparedAt = new DateTimeOffset(2026, 8, 19, 23, 30, 0, TimeSpan.FromHours(2));
        var sameInstantDifferentOffset = preparedAt.ToOffset(TimeSpan.FromHours(5));

        Assert.True(preparedAt == sameInstantDifferentOffset);
        Assert.False(preparedAt.EqualsExact(sameInstantDifferentOffset));

        var prepared = processor.CreateReferenceSession(settings, "r7_offset_mismatch", source, preparedAt);
        sessionService.Save(prepared);

        Assert.Throws<InvalidOperationException>(() =>
            processor.ProcessReference(prepared, settings, source, sameInstantDifferentOffset));

        Assert.False(Directory.Exists(prepared.AssetFolder), "Timestamp mismatch must be rejected before folder creation.");
        Assert.True(sessionService.Exists(), "Prepared journal remains durable.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_Prepared_TemplateChangesAfterPreflight_ReusesPreflightProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r7_template_hook_test", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var templateFile = Path.Combine(workspace.Root, "templates", "reference.md");

        AssetProcessorService.OnPreparedReferenceAuthorityVerifiedHook = () =>
        {
            // Tamper template file after preflight authority verified
            File.WriteAllText(templateFile, "# TAMPERED TEMPLATE {{ProjectName}}");
        };

        try
        {
            var completed = processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt);
            Assert.True(File.Exists(completed.ReferenceProvenancePath));

            var writtenProv = File.ReadAllText(completed.ReferenceProvenancePath);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    new System.Text.UTF8Encoding(false).GetBytes(writtenProv))).ToLowerInvariant();

            Assert.Equal(prepared.ReferenceProvenanceHash, hash);
            Assert.DoesNotContain("TAMPERED", writtenProv);
        }
        finally
        {
            AssetProcessorService.OnPreparedReferenceAuthorityVerifiedHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_Prepared_SourceChangesAfterPreflight_RejectsAtTempStage()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r7_source_hook_test", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        AssetProcessorService.OnPreparedReferenceAuthorityVerifiedHook = () =>
        {
            // Change source after authority verification
            File.WriteAllBytes(source, TestWorkspace.EnsureMagicBytes(source, new byte[] { 99, 88, 77 }));
        };

        try
        {
            Assert.Throws<IOException>(() =>
                processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));

            Assert.False(File.Exists(prepared.ReferenceDestinationPath), "Canonical reference must NEVER exist on staging mismatch");
            Assert.False(File.Exists(prepared.ReferenceProvenancePath), "Canonical provenance must NEVER exist on staging mismatch");
        }
        finally
        {
            AssetProcessorService.OnPreparedReferenceAuthorityVerifiedHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void PreparedReference_InterruptedStagedCopy_CleansTempsAndDeletesJournal()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r7_interrupted_staging", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        // Create staging files matching expected transaction ownership
        Directory.CreateDirectory(Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName));
        File.Copy(source, prepared.GetReferenceTempImagePath());

        var generationDate = prepared.ReferenceProcessedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var prov = workspace.CreateTemplateService().RenderReference(prepared.ReferenceFilename, prepared.ProjectName, generationDate);
        File.WriteAllText(prepared.GetReferenceTempProvenancePath(), prov);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.Exists(), "Prepared journal should be deleted after rollback of temps");
        Assert.False(File.Exists(prepared.GetReferenceTempImagePath()), "Temp image should be cleaned up");
        Assert.False(File.Exists(prepared.GetReferenceTempProvenancePath()), "Temp provenance should be cleaned up");
        Assert.False(File.Exists(prepared.ReferenceDestinationPath), "Canonical destination was never created");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void PreparedReference_OnePromotedOneTemp_RollsBackExactOwnedFilesAndDeletesJournal()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r7_one_promoted_one_temp", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        // Canonical image promoted, temp provenance still in temp
        Directory.CreateDirectory(Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName));
        File.Copy(source, prepared.ReferenceDestinationPath);

        var generationDate = prepared.ReferenceProcessedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var prov = workspace.CreateTemplateService().RenderReference(prepared.ReferenceFilename, prepared.ProjectName, generationDate);
        File.WriteAllText(prepared.GetReferenceTempProvenancePath(), prov);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.Exists(), "Prepared journal should be rolled back");
        Assert.False(File.Exists(prepared.ReferenceDestinationPath), "Canonical image should be rolled back");
        Assert.False(File.Exists(prepared.GetReferenceTempProvenancePath()), "Temp provenance should be cleaned up");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void PreparedReference_ForeignCanonicalImage_PreservesAndCloses()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r6_foreign_image", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        // Create reference folder and put foreign image there
        Directory.CreateDirectory(Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName));
        File.WriteAllBytes(prepared.ReferenceDestinationPath, TestWorkspace.EnsureMagicBytes(prepared.ReferenceDestinationPath, new byte[] { 99, 99, 99 }));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.True(sessionService.Exists(), "Prepared journal must be preserved on foreign file");
        Assert.True(File.Exists(prepared.ReferenceDestinationPath), "Foreign file must not be deleted");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void PreparedReference_ForeignCanonicalProvenance_PreservesAndCloses()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r6_foreign_prov", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        Directory.CreateDirectory(Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName));
        File.WriteAllText(prepared.ReferenceProvenancePath, "FOREIGN PROVENANCE CONTENT");

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.True(sessionService.Exists(), "Prepared journal must be preserved on foreign file");
        Assert.True(File.Exists(prepared.ReferenceProvenancePath), "Foreign provenance file must not be deleted");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_PostCommitSuccessMessageThrows_DoesNotRollbackCompletedAsset()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r6_main_ui_throw", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 4, 5, 6 });

        RunOnSta(() =>
        {
            MainForm.MessageBoxProvider = (_, message, caption, _, _) =>
            {
                if (caption == "Asset Complete")
                {
                    throw new InvalidOperationException("Simulated post-commit UI failure.");
                }
            };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Main, main1);
            var promptBox = typeof(MainForm).GetField("txtPrompt", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as TextBox;
            if (promptBox != null) promptBox.Text = "prompt text";

            try
            {
                var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
                handleMainMethod?.Invoke(form, null);

                Assert.False(sessionService.Exists(), "session.json must remain deleted after durable commit");
                Assert.True(File.Exists(Path.Combine(session.AssetFolder, "main1.png")), "Root main image must exist");
                Assert.True(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)), "Final provenance must exist");
                Assert.True(File.Exists(Path.Combine(session.GetIngameFolderPath(), "asset_r6_main_ui_throw.png")), "Ingame copy must exist");
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_PostCommitUiFailure_DoesNotRecreateReferenceSession()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r6_main_no_recreate", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 4, 5, 6 });

        RunOnSta(() =>
        {
            MainForm.MessageBoxProvider = (_, message, caption, _, _) =>
            {
                if (caption == "Asset Complete")
                {
                    throw new InvalidOperationException("Simulated post-commit UI failure.");
                }
            };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Main, main1);
            var promptBox = typeof(MainForm).GetField("txtPrompt", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as TextBox;
            if (promptBox != null) promptBox.Text = "prompt text";

            try
            {
                var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
                handleMainMethod?.Invoke(form, null);

                Assert.False(sessionService.Exists(), "Reference session must NOT be re-persisted upon post-commit UI failure");
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Reference_PostStableSaveUiFailure_DoesNotRollbackReference()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

        RunOnSta(() =>
        {
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.OnReferenceStableSessionSavedHook = s =>
            {
                throw new InvalidOperationException("Simulated UI failure after stable session save.");
            };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var folderBox = typeof(MainForm).GetField("txtAssetFolderName", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as TextBox;
            if (folderBox != null) folderBox.Text = "asset_r6_ref_post_ui_throw";

            form.SetSelectedImage(ImageSlot.Reference, ref1);

            try
            {
                var handleRefMethod = typeof(MainForm).GetMethod("HandleReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleRefMethod?.Invoke(form, null);

                Assert.True(sessionService.Exists(), "Stable Reference session must remain on disk");
                var loaded = sessionService.Load()!;
                Assert.Equal(ReferenceCommitPhase.None, loaded.ReferenceCommitPhase);
                Assert.True(File.Exists(loaded.ReferenceDestinationPath), "Reference image must not be rolled back");
                Assert.True(File.Exists(loaded.ReferenceProvenancePath), "Reference provenance must not be rolled back");
            }
            finally
            {
                MainForm.OnReferenceStableSessionSavedHook = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_PostCommitUiFailure_DoesNotLeaveReferenceReadySession()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r7_cancel_ui_throw", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        RunOnSta(() =>
        {
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true; // confirm cancel

            MainForm.OnCancelDurableCommitHook = () =>
            {
                throw new InvalidOperationException("Simulated UI failure after durable cancel commit.");
            };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            try
            {
                var handleCancelMethod = typeof(MainForm).GetMethod("HandleCancel", BindingFlags.NonPublic | BindingFlags.Instance);
                handleCancelMethod?.Invoke(form, null);

                Assert.False(sessionService.Exists(), "session.json must remain deleted");
                Assert.False(File.Exists(session.ReferenceDestinationPath), "Reference image must be deleted");
                Assert.False(File.Exists(session.ReferenceProvenancePath), "Reference provenance must be deleted");

                var currentSessionVal = sessionField?.GetValue(form);
                var currentStateVal = stateField?.GetValue(form);
                Assert.Null(currentSessionVal);
                Assert.Equal("Idle", currentStateVal?.ToString());
            }
            finally
            {
                MainForm.OnCancelDurableCommitHook = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_PostCommitUiFailure_DoesNotRecreateSession()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r7_cancel_no_recreate", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        RunOnSta(() =>
        {
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

            MainForm.OnCancelDurableCommitHook = () =>
            {
                throw new InvalidOperationException("Simulated UI failure after durable cancel commit.");
            };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            try
            {
                var handleCancelMethod = typeof(MainForm).GetMethod("HandleCancel", BindingFlags.NonPublic | BindingFlags.Instance);
                handleCancelMethod?.Invoke(form, null);

                Assert.False(sessionService.Exists(), "Cancelled session must not be recreated upon UI error");
            }
            finally
            {
                MainForm.OnCancelDurableCommitHook = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void NoReference_JournalSaved_PostSaveStatusFailure_ContinuesCommit()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var main1 = workspace.CreateImage("main1.png", new byte[] { 10, 20, 30 });

        RunOnSta(() =>
        {
            var statusHookInvoked = false;
            MainForm.OnNoReferenceJournalSavedBeforeStatusHook = () =>
            {
                statusHookInvoked = true;
                throw new InvalidOperationException("Simulated post-journal status failure.");
            };

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var chkNoRef = typeof(MainForm).GetField("chkNoReference", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as CheckBox;
            if (chkNoRef != null) chkNoRef.Checked = true;

            var folderBox = typeof(MainForm).GetField("txtAssetFolderName", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as TextBox;
            if (folderBox != null) folderBox.Text = "asset_r7_noref_status_throw";

            form.SetSelectedImage(ImageSlot.Main, main1);
            var promptBox = typeof(MainForm).GetField("txtPrompt", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as TextBox;
            if (promptBox != null) promptBox.Text = "prompt text";

            try
            {
                var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
                handleMainMethod?.Invoke(form, null);

                Assert.True(statusHookInvoked);
                Assert.False(sessionService.Exists(), "session.json must be removed on complete commit");
                Assert.True(File.Exists(Path.Combine(settings.AssetRootFolder, "asset_r7_noref_status_throw", "main1.png")));
            }
            finally
            {
                MainForm.OnNoReferenceJournalSavedBeforeStatusHook = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Replacement_PostCommitUiFailure_DoesNotRollbackOrRecreateOldReference()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r7_replace_post_ui_throw", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        RunOnSta(() =>
        {
            var uiErrorShown = false;
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, message, caption, _, _) =>
            {
                if (caption == "Post-Commit UI Error")
                {
                    uiErrorShown = true;
                }
            };

            MainForm.OnReplacementDurableCommitUiHook = () =>
            {
                throw new InvalidOperationException("Simulated post-commit UI failure.");
            };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            try
            {
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleReplaceMethod?.Invoke(form, null);

                Assert.True(uiErrorShown, "Post-Commit UI Error must be reported");
                Assert.False(sessionService.ReplacementJournalExists(), "Replacement journal must remain deleted");
                Assert.True(sessionService.Exists(), "New session must remain on disk");
                var loaded = sessionService.Load()!;
                Assert.Equal("ref2.png", loaded.ReferenceFilename);
                Assert.True(File.Exists(loaded.ReferenceDestinationPath), "New reference image must exist");
                Assert.True(File.Exists(loaded.ReferenceProvenancePath), "New reference provenance must exist");
            }
            finally
            {
                MainForm.OnReplacementDurableCommitUiHook = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void R7_003_ProcessReference_AssetFolderBecomesReparse_Rejects()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r7_reparse_test", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        try
        {
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, prepared.AssetFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };

            Assert.Throws<IOException>(() =>
                processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));
        }
        finally
        {
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_ProvenanceStaging_UsesOnlyDeterministicTransactionPaths()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "r8_det_prov_staging", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var hookInvoked = false;
        AssetProcessorService.OnReservedTextStagingOpenedHook = path =>
        {
            hookInvoked = true;
            Assert.True(ValidationService.PathsEqual(path, prepared.GetReferenceTempProvenancePath()));
            var refDir = Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName);
            if (Directory.Exists(refDir))
            {
                var files = Directory.GetFiles(refDir);
                Assert.DoesNotContain(files, f => Path.GetFileName(f).StartsWith(".__write_"));
            }
        };

        try
        {
            var completed = processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt);
            Assert.True(hookInvoked);
            Assert.True(File.Exists(completed.ReferenceProvenancePath));
        }
        finally
        {
            AssetProcessorService.OnReservedTextStagingOpenedHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Replacement_ProvenanceStaging_UsesOnlyDeterministicTransactionPaths()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "r8_det_replace_staging", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        var hookInvoked = false;
        AssetProcessorService.OnReservedTextStagingOpenedHook = path =>
        {
            hookInvoked = true;
            Assert.True(ValidationService.PathsEqual(path, tx.TempNewProvenancePath));
            var refDir = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
            if (Directory.Exists(refDir))
            {
                var files = Directory.GetFiles(refDir);
                Assert.DoesNotContain(files, f => Path.GetFileName(f).StartsWith(".__write_"));
            }
        };

        try
        {
            processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
            Assert.True(hookInvoked);
            Assert.True(File.Exists(tx.TempNewProvenancePath));
        }
        finally
        {
            AssetProcessorService.OnReservedTextStagingOpenedHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Replacement_SaveNewFailure_PostRollbackUiThrows_RollbackRunsExactlyOnce()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r8_save_new_ui_throw", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        RunOnSta(() =>
        {
            var rollbackCount = 0;
            AssetProcessorService.OnRollbackReferenceReplacementInvoked = _ =>
            {
                rollbackCount++;
            };

            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            MainForm.OnReplacementRollbackDurableCommitHook = () =>
            {
                throw new InvalidOperationException("Simulated UI failure after durable rollback commit.");
            };

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.SetSelectedImage(ImageSlot.Reference, ref2);

            SessionService.OnBeforeSaveSessionHook = s =>
            {
                if (s.ReferenceFilename == "ref2.png")
                {
                    throw new IOException("Injected Save NewSession failure.");
                }
            };

            try
            {
                var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
                handleReplaceMethod?.Invoke(form, null);

                Assert.Equal(1, rollbackCount);
                Assert.False(sessionService.ReplacementJournalExists(), "Replacement journal must be deleted");
                Assert.True(sessionService.Exists(), "OLD session must be restored");
                var loaded = sessionService.Load()!;
                Assert.Equal("ref1.png", loaded.ReferenceFilename);
                Assert.True(File.Exists(session.ReferenceDestinationPath), "OLD reference image must exist");
                Assert.True(File.Exists(session.ReferenceProvenancePath), "OLD reference provenance must exist");
                Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName, "ref2.png")), "NEW reference image must not exist");
            }
            finally
            {
                SessionService.OnBeforeSaveSessionHook = null;
                MainForm.OnReplacementRollbackDurableCommitHook = null;
                AssetProcessorService.OnRollbackReferenceReplacementInvoked = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Theory]
    [InlineData("image")]
    [InlineData("provenance")]
    [Trait("Category", "RecoveryCritical")]
    public void Replacement_TempTamperedAfterBackup_BeforePromotion_RejectsBeforeCanonicalMutation(string tamperTarget)
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r8_tamper_promote_" + tamperTarget, ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        if (tamperTarget == "image")
        {
            File.WriteAllBytes(tx.TempNewReferencePath, TestWorkspace.EnsureMagicBytes(tx.TempNewReferencePath, new byte[] { 99, 99, 99 }));
        }
        else
        {
            File.WriteAllText(tx.TempNewProvenancePath, "TAMPERED TEMP PROVENANCE CONTENT");
        }

        Assert.Throws<InvalidDataException>(() =>
            processor.PromoteNewReference(tx));

        // NEW canonical destination must not exist
        if (!ValidationService.PathsEqual(oldSession.ReferenceDestinationPath, tx.NewSession.ReferenceDestinationPath))
        {
            Assert.False(File.Exists(tx.NewSession.ReferenceDestinationPath), "NEW canonical image must not exist on tamper");
        }

        // Backups remain available
        Assert.True(File.Exists(tx.BackupReferencePath), "Backup reference image must remain");
        Assert.True(File.Exists(tx.BackupProvenancePath), "Backup reference provenance must remain");

        // Rollback restores OLD cleanly and preserves unknown temp
        var rollback = processor.RollbackReferenceReplacement(tx);
        Assert.True(File.Exists(oldSession.ReferenceDestinationPath), "OLD reference must be restored");
        Assert.True(File.Exists(oldSession.ReferenceProvenancePath), "OLD provenance must be restored");
        Assert.True(File.Exists(tamperTarget == "image" ? tx.TempNewReferencePath : tx.TempNewProvenancePath), "Tampered temp file must be preserved");
    }

    [Fact]
    public void Startup_ValidTemplates_StatusContainsTemplatesValidated()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();
        var sessionService = workspace.CreateSessionService();

        RunOnSta(() =>
        {
            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var _ = form.Handle;

            var txtStatus = typeof(MainForm).GetField("txtStatusHistory", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as TextBox;
            Assert.NotNull(txtStatus);
            Assert.Contains("Templates validated.", txtStatus.Text);
        });
    }

    [Theory]
    [InlineData(AssetWorkflowMode.ReferenceAssisted)]
    [InlineData(AssetWorkflowMode.NoReference)]
    [Trait("Category", "RecoveryCritical")]
    public void Main_TempMainTamperedBeforePromotion_RejectsBeforeCanonicalMutation(AssetWorkflowMode workflowMode)
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        AssetSession session;
        string mainSource;

        if (workflowMode == AssetWorkflowMode.ReferenceAssisted)
        {
            var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            session = processor.ProcessReference(settings, "asset_r9_tamper_main_assisted", refImg, DateTimeOffset.Now);
            sessionService.Save(session);
            mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
            session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
            sessionService.Save(session);
        }
        else
        {
            mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
            session = processor.CreateNoReferenceMainSession(settings, "asset_r9_tamper_main_noref", mainSource, "prompt", DateTimeOffset.Now);
            sessionService.Save(session);
        }

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
        {
            var tempMain = s.GetMainTempImagePath();
            File.WriteAllBytes(tempMain, TestWorkspace.EnsureMagicBytes(tempMain, new byte[] { 99, 99, 99 }));
        };

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, session.MainPrompt!, session.MainProcessedAt!.Value));
            Assert.True(ex is InvalidDataException || ex is AssetProcessingException || ex is IOException);

            Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)), "Root main must not exist");
            Assert.False(File.Exists(session.GetIngameImagePath()), "Ingame main must not exist");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)), "Main provenance must not exist");
        }
        finally
        {
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_TempIngameTamperedBeforePromotion_RejectsBeforeCanonicalMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r9_tamper_ingame", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
        {
            var tempIngame = s.GetMainTempIngamePath();
            File.WriteAllBytes(tempIngame, TestWorkspace.EnsureMagicBytes(tempIngame, new byte[] { 99, 99, 99 }));
        };

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, session.MainPrompt!, session.MainProcessedAt!.Value));
            Assert.True(ex is InvalidDataException || ex is AssetProcessingException || ex is IOException);

            Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)), "Root main must not exist");
            Assert.False(File.Exists(session.GetIngameImagePath()), "Ingame main must not exist");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)), "Main provenance must not exist");
        }
        finally
        {
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_TempProvenanceTamperedBeforePromotion_RejectsBeforeCanonicalMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r9_tamper_prov", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
        {
            var tempProv = s.GetMainTempProvenancePath();
            File.WriteAllText(tempProv, "TAMPERED PROVENANCE BYTES");
        };

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, session.MainPrompt!, session.MainProcessedAt!.Value));
            Assert.True(ex is InvalidDataException || ex is AssetProcessingException || ex is IOException);

            Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)), "Root main must not exist");
            Assert.False(File.Exists(session.GetIngameImagePath()), "Ingame main must not exist");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)), "Main provenance must not exist");
        }
        finally
        {
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_IngameBecomesReparseBeforePromotion_RejectsBeforeCanonicalMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r9_reparse_ingame", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
        {
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, s.GetIngameFolderPath()))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, session.MainPrompt!, session.MainProcessedAt!.Value));
            Assert.True(ex is InvalidDataException || ex is AssetProcessingException || ex is IOException);

            Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)), "Root main must not exist");
            Assert.False(File.Exists(session.GetIngameImagePath()), "Ingame main must not exist");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)), "Main provenance must not exist");
        }
        finally
        {
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_TempImageTamperedImmediatelyBeforePromotion_NoCanonicalMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r9_tamper_init_img", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = s =>
        {
            var tempImg = s.GetReferenceTempImagePath();
            File.WriteAllBytes(tempImg, TestWorkspace.EnsureMagicBytes(tempImg, new byte[] { 99, 99, 99 }));
        };

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));
            Assert.True(ex is InvalidDataException || ex is AssetProcessingException || ex is IOException);

            Assert.False(File.Exists(prepared.ReferenceDestinationPath), "Canonical reference image must not exist");
            Assert.False(File.Exists(prepared.ReferenceProvenancePath), "Canonical reference provenance must not exist");
        }
        finally
        {
            AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = null;
        }
    }

    [Theory]
    [InlineData("corrupted")]
    [InlineData("utf8bom")]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_TempProvenanceTamperedImmediatelyBeforePromotion_NoCanonicalMutation(string mode)
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r9_tamper_init_prov_" + mode, source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = s =>
        {
            var tempProv = s.GetReferenceTempProvenancePath();
            if (mode == "utf8bom")
            {
                var content = File.ReadAllBytes(tempProv);
                var bom = new byte[] { 0xEF, 0xBB, 0xBF };
                var withBom = bom.Concat(content).ToArray();
                File.WriteAllBytes(tempProv, withBom);
            }
            else
            {
                File.WriteAllText(tempProv, "CORRUPTED PROVENANCE DATA");
            }
        };

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));
            Assert.True(ex is InvalidDataException || ex is AssetProcessingException || ex is IOException);

            Assert.False(File.Exists(prepared.ReferenceDestinationPath), "Canonical reference image must not exist");
            Assert.False(File.Exists(prepared.ReferenceProvenancePath), "Canonical reference provenance must not exist");
        }
        finally
        {
            AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void PreparedReference_PartialDeterministicProvenance_PreservesJournalAndFailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r9_partial_init_prov", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var refFolder = Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName);
        Directory.CreateDirectory(refFolder);

        // Valid temp image
        File.Copy(source, prepared.GetReferenceTempImagePath());

        // Partial temp provenance
        File.WriteAllText(prepared.GetReferenceTempProvenancePath(), "PARTIAL PROVENANCE INCOMPLETE");

        var rollback = processor.RollbackReference(prepared);

        Assert.False(rollback.IsValid, "Rollback must fail closed when provenance is partial/unknown");
        Assert.True(File.Exists(prepared.GetReferenceTempProvenancePath()), "Partial temp provenance must be preserved");
        Assert.False(File.Exists(prepared.ReferenceDestinationPath), "No canonical reference");
        Assert.False(File.Exists(prepared.ReferenceProvenancePath), "No canonical provenance");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Replacement_PartialDeterministicProvenance_FailsClosedWithoutOldMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r9_partial_repl_prov", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        // Partially overwrite temp provenance
        File.WriteAllText(tx.TempNewProvenancePath, "PARTIAL PROVENANCE");

        var rollback = processor.RollbackReferenceReplacement(tx);

        Assert.False(rollback.IsValid, "Rollback must fail closed when replacement temp provenance hash is unknown");
        Assert.True(File.Exists(oldSession.ReferenceDestinationPath), "OLD reference image must be restored");
        Assert.True(File.Exists(oldSession.ReferenceProvenancePath), "OLD reference provenance must be restored");
        Assert.True(File.Exists(tx.TempNewProvenancePath), "Partial temp provenance must be preserved");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_FinalGateDetectsIngameReparse_PerformsZeroLocalDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r10_ingame_reparse_zero_delete", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        var deleteFileCount = 0;
        var deleteDirectoryCount = 0;

        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;
        AssetProcessorService.OnBeforeDeleteDirectoryHook = _ => deleteDirectoryCount++;

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
        {
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, s.GetIngameFolderPath()))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var ex = Assert.Throws<AssetProcessingException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, session.MainPrompt!, session.MainProcessedAt!.Value));
            Assert.False(ex.RollbackComplete, "Rollback must not be completed on unsafe path");

            Assert.Equal(0, deleteFileCount);
            Assert.Equal(0, deleteDirectoryCount);
            Assert.True(sessionService.Exists(), "Journal must remain durable");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)), "Root main must not exist");
            Assert.False(File.Exists(session.GetIngameImagePath()), "Ingame main must not exist");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)), "Final provenance must not exist");
            Assert.True(File.Exists(session.GetMainTempImagePath()), "Temp main must be preserved");
            Assert.True(File.Exists(session.GetMainTempIngamePath()), "Temp ingame must be preserved");
            Assert.True(File.Exists(session.GetMainTempProvenancePath()), "Temp provenance must be preserved");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeDeleteDirectoryHook = null;
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_FinalGateDetectsAssetFolderReparse_PerformsZeroLocalDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r10_asset_reparse_zero_delete", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        var deleteFileCount = 0;
        var deleteDirectoryCount = 0;

        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;
        AssetProcessorService.OnBeforeDeleteDirectoryHook = _ => deleteDirectoryCount++;

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
        {
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, s.AssetFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var ex = Assert.Throws<AssetProcessingException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, session.MainPrompt!, session.MainProcessedAt!.Value));
            Assert.False(ex.RollbackComplete, "Rollback must not be completed on unsafe path");

            Assert.Equal(0, deleteFileCount);
            Assert.Equal(0, deleteDirectoryCount);
            Assert.True(sessionService.Exists(), "Journal must remain durable");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)), "Root main must not exist");
            Assert.False(File.Exists(session.GetIngameImagePath()), "Ingame main must not exist");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)), "Final provenance must not exist");
            Assert.True(File.Exists(session.GetMainTempImagePath()), "Temp main must be preserved");
            Assert.True(File.Exists(session.GetMainTempIngamePath()), "Temp ingame must be preserved");
            Assert.True(File.Exists(session.GetMainTempProvenancePath()), "Temp provenance must be preserved");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeDeleteDirectoryHook = null;
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_DetectsReferenceFolderReparse_PerformsZeroLocalDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r10_init_ref_reparse_zero_delete", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var deleteFileCount = 0;
        var deleteDirectoryCount = 0;

        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;
        AssetProcessorService.OnBeforeDeleteDirectoryHook = _ => deleteDirectoryCount++;

        AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = s =>
        {
            ValidationService.FileAttributesProvider = path =>
            {
                var refFolder = Path.Combine(s.AssetFolder, AppConstants.ReferenceFolderName);
                if (ValidationService.PathsEqual(path, refFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));
            Assert.Contains("destination hierarchy is no longer safe", ex.Message);

            Assert.Equal(0, deleteFileCount);
            Assert.Equal(0, deleteDirectoryCount);
            Assert.True(sessionService.Exists(), "Journal must remain durable");
            Assert.False(File.Exists(prepared.ReferenceDestinationPath), "Canonical reference must not exist");
            Assert.False(File.Exists(prepared.ReferenceProvenancePath), "Canonical provenance must not exist");
            Assert.True(File.Exists(prepared.GetReferenceTempImagePath()), "Temp image must be preserved");
            Assert.True(File.Exists(prepared.GetReferenceTempProvenancePath()), "Temp provenance must be preserved");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeDeleteDirectoryHook = null;
            AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_DetectsAssetFolderReparse_PerformsZeroLocalDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r10_init_asset_reparse_zero_delete", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var deleteFileCount = 0;
        var deleteDirectoryCount = 0;

        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;
        AssetProcessorService.OnBeforeDeleteDirectoryHook = _ => deleteDirectoryCount++;

        AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = s =>
        {
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, s.AssetFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));
            Assert.Contains("destination hierarchy is no longer safe", ex.Message);

            Assert.Equal(0, deleteFileCount);
            Assert.Equal(0, deleteDirectoryCount);
            Assert.True(sessionService.Exists(), "Journal must remain durable");
            Assert.False(File.Exists(prepared.ReferenceDestinationPath), "Canonical reference must not exist");
            Assert.False(File.Exists(prepared.ReferenceProvenancePath), "Canonical provenance must not exist");
            Assert.True(File.Exists(prepared.GetReferenceTempImagePath()), "Temp image must be preserved");
            Assert.True(File.Exists(prepared.GetReferenceTempProvenancePath()), "Temp provenance must be preserved");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeDeleteDirectoryHook = null;
            AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Replacement_ReparseChangesAfterFinalHash_NoCanonicalMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r10_repl_reparse_after_hash", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var deleteFileCount = 0;
        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;

        // Make reference folder a reparse point precisely at the hook after staging hashes
        AssetProcessorService.OnBeforeReplacementFinalPathGate = _ =>
        {
            ValidationService.FileAttributesProvider = path =>
            {
                var refFolder = Path.Combine(oldSession.AssetFolder, AppConstants.ReferenceFolderName);
                if (ValidationService.PathsEqual(path, refFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                processor.PromoteNewReference(tx));

            Assert.Equal(0, deleteFileCount);
            Assert.True(File.Exists(tx.BackupReferencePath), "OLD backup reference image must be intact");
            Assert.True(File.Exists(tx.BackupProvenancePath), "OLD backup reference provenance must be intact");
            Assert.True(File.Exists(tx.TempNewReferencePath), "NEW temp reference image must be intact");
            Assert.True(File.Exists(tx.TempNewProvenancePath), "NEW temp reference provenance must be intact");

            // NEW canonical destination must not exist
            if (!ValidationService.PathsEqual(oldSession.ReferenceDestinationPath, tx.NewSession.ReferenceDestinationPath))
            {
                Assert.False(File.Exists(tx.NewSession.ReferenceDestinationPath), "NEW canonical image must not exist on reparse detection");
            }
        }
        finally
        {
            AssetProcessorService.OnBeforeReplacementFinalPathGate = null;
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void InitialReference_TempProvenanceBomModified_PreservesUnknownTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r11_ref_prov_bom", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = s =>
        {
            var tempProv = s.GetReferenceTempProvenancePath();
            var currentBytes = File.ReadAllBytes(tempProv);
            var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(currentBytes).ToArray();
            File.WriteAllBytes(tempProv, bomBytes);
        };

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));
            Assert.True(ex is InvalidDataException || ex is IOException);

            Assert.True(sessionService.Exists(), "Prepared journal must remain intact");
            Assert.False(File.Exists(prepared.ReferenceDestinationPath), "Canonical reference must not exist");
            Assert.False(File.Exists(prepared.ReferenceProvenancePath), "Canonical provenance must not exist");
            Assert.True(File.Exists(prepared.GetReferenceTempProvenancePath()), "Modified temp provenance must be preserved");
        }
        finally
        {
            AssetProcessorService.OnBeforeInitialReferenceStagingAuthorityGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_TempProvenanceBomModified_PreservesUnknownTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r11_main_prov_bom", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        AssetProcessorService.OnBeforeMainStagingAuthorityGate = s =>
        {
            var tempProv = s.GetMainTempProvenancePath();
            var currentBytes = File.ReadAllBytes(tempProv);
            var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(currentBytes).ToArray();
            File.WriteAllBytes(tempProv, bomBytes);
        };

        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, session.MainPrompt!, session.MainProcessedAt!.Value));
            Assert.True(ex is InvalidDataException || ex is AssetProcessingException || ex is IOException);

            Assert.True(sessionService.Exists(), "Main journal must remain intact");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, session.MainFilename!)), "Root main must not exist");
            Assert.False(File.Exists(session.GetIngameImagePath()), "Ingame main must not exist");
            Assert.False(File.Exists(Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName)), "Final provenance must not exist");
            Assert.True(File.Exists(session.GetMainTempProvenancePath()), "Modified temp provenance must be preserved");
        }
        finally
        {
            AssetProcessorService.OnBeforeMainStagingAuthorityGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Main_FinalProvenanceBomModifiedAfterPromotion_PreservesCanonical()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r11_main_promoted_prov_bom", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        AssetProcessorService.OnMainPromotedHook = _ =>
        {
            // Prepend BOM to promoted final provenance
            var currentBytes = File.ReadAllBytes(finalProvPath);
            var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(currentBytes).ToArray();
            File.WriteAllBytes(finalProvPath, bomBytes);
        };

        try
        {
            var ex = Assert.Throws<AssetProcessingException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainSource, session.MainPrompt!, session.MainProcessedAt!.Value));
            Assert.False(ex.RollbackComplete, "Rollback must fail closed when promoted provenance was modified");

            Assert.True(sessionService.Exists(), "Journal must remain intact");
            Assert.True(File.Exists(finalProvPath), "Modified canonical provenance must be preserved on disk");
        }
        finally
        {
            AssetProcessorService.OnMainPromotedHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackMain_AssetFolderBecomesReparseAfterOwnershipVerification_ZeroDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r11_rbmain_asset_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        // Stage temp files with valid provenance
        Directory.CreateDirectory(session.GetIngameFolderPath());
        File.Copy(mainSource, session.GetMainTempImagePath());
        File.Copy(mainSource, session.GetMainTempIngamePath());
        var templateService = workspace.CreateTemplateService();
        var provText = templateService.RenderFinal(session.MainFilename!, session.ReferenceFilename, session.ProjectName, session.MainProcessedAt!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), session.MainPrompt!);
        File.WriteAllText(session.GetMainTempProvenancePath(), provText, new UTF8Encoding(false));

        var deleteFileCount = 0;
        var deleteDirectoryCount = 0;
        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;
        AssetProcessorService.OnBeforeDeleteDirectoryHook = _ => deleteDirectoryCount++;

        AssetProcessorService.OnBeforeRollbackMainFinalPathGate = s =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, s.AssetFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var result = processor.RollbackMain(session);
            Assert.True(hookInvoked, "RollbackMain must reach final path gate.");
            Assert.False(result.IsValid, "RollbackMain must fail on reparse point");

            Assert.Equal(0, deleteFileCount);
            Assert.Equal(0, deleteDirectoryCount);
            Assert.True(File.Exists(session.GetMainTempImagePath()), "Temp main must remain");
            Assert.True(File.Exists(session.GetMainTempIngamePath()), "Temp ingame must remain");
            Assert.True(File.Exists(session.GetMainTempProvenancePath()), "Temp provenance must remain");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeDeleteDirectoryHook = null;
            AssetProcessorService.OnBeforeRollbackMainFinalPathGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackMain_IngameBecomesReparseAfterOwnershipVerification_ZeroDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r11_rbmain_ingame_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        // Stage temp files with valid provenance
        Directory.CreateDirectory(session.GetIngameFolderPath());
        File.Copy(mainSource, session.GetMainTempImagePath());
        File.Copy(mainSource, session.GetMainTempIngamePath());
        var templateService = workspace.CreateTemplateService();
        var provText = templateService.RenderFinal(session.MainFilename!, session.ReferenceFilename, session.ProjectName, session.MainProcessedAt!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), session.MainPrompt!);
        File.WriteAllText(session.GetMainTempProvenancePath(), provText, new UTF8Encoding(false));

        var deleteFileCount = 0;
        var deleteDirectoryCount = 0;
        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;
        AssetProcessorService.OnBeforeDeleteDirectoryHook = _ => deleteDirectoryCount++;

        AssetProcessorService.OnBeforeRollbackMainFinalPathGate = s =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, s.GetIngameFolderPath()))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var result = processor.RollbackMain(session);
            Assert.True(hookInvoked, "RollbackMain must reach final path gate.");
            Assert.False(result.IsValid, "RollbackMain must fail on reparse point");

            Assert.Equal(0, deleteFileCount);
            Assert.Equal(0, deleteDirectoryCount);
            Assert.True(File.Exists(session.GetMainTempImagePath()), "Temp main must remain");
            Assert.True(File.Exists(session.GetMainTempIngamePath()), "Temp ingame must remain");
            Assert.True(File.Exists(session.GetMainTempProvenancePath()), "Temp provenance must remain");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeDeleteDirectoryHook = null;
            AssetProcessorService.OnBeforeRollbackMainFinalPathGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackReference_ReferenceFolderBecomesReparseAfterVerification_ZeroDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r11_rbref_ref_reparse", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var refFolder = Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName);
        Directory.CreateDirectory(refFolder);

        File.Copy(source, prepared.GetReferenceTempImagePath());
        File.Copy(source, prepared.GetReferenceTempProvenancePath());
        prepared.ReferenceProvenanceHash = processor.ComputeSha256(prepared.GetReferenceTempProvenancePath());
        sessionService.Save(prepared);

        var deleteFileCount = 0;
        var deleteDirectoryCount = 0;
        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;
        AssetProcessorService.OnBeforeDeleteDirectoryHook = _ => deleteDirectoryCount++;

        AssetProcessorService.OnBeforeRollbackReferenceFinalPathGate = s =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, refFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var result = processor.RollbackReference(prepared);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "RollbackReference must fail on reparse point");

            Assert.Equal(0, deleteFileCount);
            Assert.Equal(0, deleteDirectoryCount);
            Assert.True(File.Exists(prepared.GetReferenceTempImagePath()), "Temp image must remain");
            Assert.True(File.Exists(prepared.GetReferenceTempProvenancePath()), "Temp provenance must remain");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeDeleteDirectoryHook = null;
            AssetProcessorService.OnBeforeRollbackReferenceFinalPathGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackReference_AssetFolderBecomesReparseAfterVerification_ZeroDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r11_rbref_asset_reparse", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var refFolder = Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName);
        Directory.CreateDirectory(refFolder);

        File.Copy(source, prepared.GetReferenceTempImagePath());
        File.Copy(source, prepared.GetReferenceTempProvenancePath());
        prepared.ReferenceProvenanceHash = processor.ComputeSha256(prepared.GetReferenceTempProvenancePath());
        sessionService.Save(prepared);

        var deleteFileCount = 0;
        var deleteDirectoryCount = 0;
        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;
        AssetProcessorService.OnBeforeDeleteDirectoryHook = _ => deleteDirectoryCount++;

        AssetProcessorService.OnBeforeRollbackReferenceFinalPathGate = s =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, s.AssetFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var result = processor.RollbackReference(prepared);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "RollbackReference must fail on reparse point");

            Assert.Equal(0, deleteFileCount);
            Assert.Equal(0, deleteDirectoryCount);
            Assert.True(File.Exists(prepared.GetReferenceTempImagePath()), "Temp image must remain");
            Assert.True(File.Exists(prepared.GetReferenceTempProvenancePath()), "Temp provenance must remain");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeDeleteDirectoryHook = null;
            AssetProcessorService.OnBeforeRollbackReferenceFinalPathGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackReference_UnknownTempProvenance_DoesNotPartiallyDeleteKnownTempImage()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r11_rbref_unknown_prov", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var refFolder = Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName);
        Directory.CreateDirectory(refFolder);

        // Valid temp image
        File.Copy(source, prepared.GetReferenceTempImagePath());

        // Unknown / tampered temp provenance
        File.WriteAllText(prepared.GetReferenceTempProvenancePath(), "UNKNOWN PROVENANCE CONTENT");

        var deleteFileCount = 0;
        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;

        try
        {
            var result = processor.RollbackReference(prepared);
            Assert.False(result.IsValid, "RollbackReference must fail when temp provenance is unknown");

            Assert.Equal(0, deleteFileCount);
            Assert.True(File.Exists(prepared.GetReferenceTempImagePath()), "Valid temp image must NOT be partially deleted in Phase A");
            Assert.True(File.Exists(prepared.GetReferenceTempProvenancePath()), "Unknown temp provenance must be preserved");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ReplacementRollback_ReparseChangesAfterPhaseA_ZeroMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r11_repl_rb_reparse", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var deleteFileCount = 0;
        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;

        AssetProcessorService.OnBeforeRollbackReferenceReplacementFinalPathGate = t =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                var refFolder = Path.Combine(oldSession.AssetFolder, AppConstants.ReferenceFolderName);
                if (ValidationService.PathsEqual(path, refFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var result = processor.RollbackReferenceReplacement(tx);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "RollbackReferenceReplacement must fail when path becomes reparse point");

            Assert.Equal(0, deleteFileCount);
            Assert.True(File.Exists(tx.BackupReferencePath), "Backup reference image must remain");
            Assert.True(File.Exists(tx.BackupProvenancePath), "Backup reference provenance must remain");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeRollbackReferenceReplacementFinalPathGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ReplacementCleanup_ReparseChangesAfterBackupVerification_ZeroDeletes()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r11_repl_cleanup_reparse", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        var deleteFileCount = 0;
        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = _ => deleteFileCount++;

        AssetProcessorService.OnBeforeReplacementCleanupFinalPathGate = t =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                var refFolder = Path.Combine(oldSession.AssetFolder, AppConstants.ReferenceFolderName);
                if (ValidationService.PathsEqual(path, refFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var result = processor.CommitReferenceReplacement(tx);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "Cleanup/Commit must fail when reference folder is a reparse point");

            Assert.Equal(0, deleteFileCount);
            Assert.True(File.Exists(tx.BackupReferencePath), "Backup reference must remain");
            Assert.True(File.Exists(tx.BackupProvenancePath), "Backup provenance must remain");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            AssetProcessorService.OnBeforeReplacementCleanupFinalPathGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_ReparseChangesAfterPreparedBeforeProvenanceMove_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r11_cancel_prov_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnCancelPhaseSavingHook = (phase, s) =>
        {
            if (phase == CancelPhase.Prepared)
            {
                hookInvoked = true;
                ValidationService.FileAttributesProvider = path =>
                {
                    if (ValidationService.PathsEqual(path, s.AssetFolder))
                    {
                        return FileAttributes.Directory | FileAttributes.ReparsePoint;
                    }
                    return File.GetAttributes(path);
                };
            }
        };

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                sessionService.Cancel(session));

            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.True(File.Exists(session.ReferenceDestinationPath), "Canonical reference must not be moved");
            Assert.True(File.Exists(session.ReferenceProvenancePath), "Canonical provenance must not be moved");
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_ReparseChangesAfterProvenanceMoveBeforeReferenceMove_NoReferenceMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r11_cancel_ref_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnCancelProvenanceMovedHook = s =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, s.AssetFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            Assert.Throws<IOException>(() =>
                sessionService.Cancel(session));

            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.True(File.Exists(session.ReferenceDestinationPath), "Canonical reference image must not be moved");
        }
        finally
        {
            SessionService.OnCancelProvenanceMovedHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_FilesRenamed_ReparseChangesAfterOwnershipVerification_NoDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r11_cancel_filesrenamed_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnCancelPhaseSavingHook = (phase, s) =>
        {
            if (phase == CancelPhase.FilesRenamed)
            {
                hookInvoked = true;
                ValidationService.FileAttributesProvider = path =>
                {
                    if (ValidationService.PathsEqual(path, s.AssetFolder))
                    {
                        return FileAttributes.Directory | FileAttributes.ReparsePoint;
                    }
                    return File.GetAttributes(path);
                };
            }
        };

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                sessionService.Cancel(session));

            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.True(sessionService.Exists(), "Session journal must remain intact");
            Assert.True(File.Exists(session.GetCancelTempReferencePath()), "Cancel temp reference must be preserved");
            Assert.True(File.Exists(session.GetCancelTempProvenancePath()), "Cancel temp provenance must be preserved");
        }
        finally
        {
            SessionService.OnCancelPhaseSavingHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_FolderBecomesReparseAtFolderCleanup_NoDirectoryDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r11_cancel_folder_cleanup_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnBeforeFolderCleanupHook = () =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, session.AssetFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                sessionService.Cancel(session));
            Assert.Contains("reparse point", ex.Message);

            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.True(sessionService.Exists(), "Session journal must remain intact when folder cleanup fails");
        }
        finally
        {
            SessionService.OnBeforeFolderCleanupHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackMain_RootMainChangesAfterPhaseA_PreservesForeignFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r12_rbmain_root_change", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        var rootMain = Path.Combine(session.AssetFolder, session.MainFilename!);
        var foreignBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 88, 99 };
        File.Copy(mainSource, rootMain);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeRollbackMainFinalPathGate = s =>
        {
            hookInvoked = true;
            File.WriteAllBytes(rootMain, foreignBytes);
        };

        try
        {
            var result = processor.RollbackMain(session);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "RollbackMain must fail when root main changes after Phase A.");
            Assert.True(File.Exists(rootMain), "Modified root main file must be preserved.");
            Assert.Equal(foreignBytes, File.ReadAllBytes(rootMain));
            Assert.True(session.IsMainCommitting, "Metadata must remain in committing state.");
        }
        finally
        {
            AssetProcessorService.OnBeforeRollbackMainFinalPathGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackMain_ProvenanceChangesAfterPhaseA_PreservesForeignFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r12_rbmain_prov_change", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainSource, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        var provPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var originalProv = workspace.CreateTemplateService().RenderFinal(session.MainFilename!, session.ReferenceFilename, session.ProjectName, session.MainProcessedAt!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), session.MainPrompt!);
        File.WriteAllText(provPath, originalProv, new UTF8Encoding(false));

        var hookInvoked = false;
        AssetProcessorService.OnBeforeRollbackMainFinalPathGate = s =>
        {
            hookInvoked = true;
            var currentBytes = File.ReadAllBytes(provPath);
            var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(currentBytes).ToArray();
            File.WriteAllBytes(provPath, bomBytes);
        };

        try
        {
            var result = processor.RollbackMain(session);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "RollbackMain must fail when provenance changes after Phase A.");
            Assert.True(File.Exists(provPath), "Modified provenance must be preserved.");
            Assert.True(session.IsMainCommitting, "Metadata must remain in committing state.");
        }
        finally
        {
            AssetProcessorService.OnBeforeRollbackMainFinalPathGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackReference_TempImageChangesAfterPhaseA_PreservesUnknownTemp()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("reference.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r12_rbref_temp_change", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var refFolder = Path.Combine(prepared.AssetFolder, AppConstants.ReferenceFolderName);
        Directory.CreateDirectory(refFolder);

        File.Copy(source, prepared.GetReferenceTempImagePath());
        File.Copy(source, prepared.GetReferenceTempProvenancePath());
        prepared.ReferenceProvenanceHash = processor.ComputeSha256(prepared.GetReferenceTempProvenancePath());
        sessionService.Save(prepared);

        var foreignBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 77, 88 };
        var hookInvoked = false;

        AssetProcessorService.OnBeforeRollbackReferenceFinalPathGate = s =>
        {
            hookInvoked = true;
            File.WriteAllBytes(prepared.GetReferenceTempImagePath(), foreignBytes);
        };

        try
        {
            var result = processor.RollbackReference(prepared);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "RollbackReference must fail when temp image changes after Phase A.");
            Assert.True(File.Exists(prepared.GetReferenceTempImagePath()), "Modified temp image must remain.");
            Assert.Equal(foreignBytes, File.ReadAllBytes(prepared.GetReferenceTempImagePath()));
        }
        finally
        {
            AssetProcessorService.OnBeforeRollbackReferenceFinalPathGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ReplacementRollback_BackupReferenceChangesAfterPhaseA_NotRestored()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r12_repl_rb_ref_change", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var foreignBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 42, 43 };
        var hookInvoked = false;

        AssetProcessorService.OnBeforeRollbackReferenceReplacementFinalPathGate = t =>
        {
            hookInvoked = true;
            File.WriteAllBytes(tx.BackupReferencePath, foreignBytes);
        };

        try
        {
            var result = processor.RollbackReferenceReplacement(tx);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "RollbackReferenceReplacement must fail when backup reference changes.");
            Assert.True(File.Exists(tx.BackupReferencePath), "Tampered backup must be preserved.");
            Assert.Equal(foreignBytes, File.ReadAllBytes(tx.BackupReferencePath));
            Assert.False(File.Exists(tx.OldSession.ReferenceDestinationPath), "Tampered backup must NOT be restored to canonical path.");
        }
        finally
        {
            AssetProcessorService.OnBeforeRollbackReferenceReplacementFinalPathGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ReplacementRollback_BackupProvenanceChangesAfterPhaseA_NotRestored()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r12_repl_rb_prov_change", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var hookInvoked = false;

        AssetProcessorService.OnBeforeRollbackReferenceReplacementFinalPathGate = t =>
        {
            hookInvoked = true;
            var currentBytes = File.ReadAllBytes(tx.BackupProvenancePath);
            var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(currentBytes).ToArray();
            File.WriteAllBytes(tx.BackupProvenancePath, bomBytes);
        };

        try
        {
            var result = processor.RollbackReferenceReplacement(tx);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "RollbackReferenceReplacement must fail when backup provenance changes.");
            Assert.True(File.Exists(tx.BackupProvenancePath), "Tampered backup provenance must be preserved.");
            Assert.False(File.Exists(tx.OldSession.ReferenceProvenancePath), "Tampered backup provenance must NOT be restored.");
        }
        finally
        {
            AssetProcessorService.OnBeforeRollbackReferenceReplacementFinalPathGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ReplacementCleanup_BackupChangesAfterVerification_PreservesBackup()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r12_repl_cleanup_change", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        var foreignBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 11, 22 };
        var hookInvoked = false;

        AssetProcessorService.OnBeforeReplacementCleanupFinalPathGate = t =>
        {
            hookInvoked = true;
            File.WriteAllBytes(tx.BackupReferencePath, foreignBytes);
        };

        try
        {
            var result = processor.CommitReferenceReplacement(tx);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(result.IsValid, "CommitReferenceReplacement must fail when backup changed.");
            Assert.True(File.Exists(tx.BackupReferencePath), "Modified backup must remain.");
            Assert.False(tx.IsCommitted, "Transaction must not be marked committed.");
        }
        finally
        {
            AssetProcessorService.OnBeforeReplacementCleanupFinalPathGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void TryDeleteHashOwnedFileWithError_FileChangesAtHook_PreservesFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var testFile = Path.Combine(workspace.Root, "owned_test.bin");
        var initialBytes = new byte[] { 10, 20, 30 };
        File.WriteAllBytes(testFile, initialBytes);
        var expectedHash = processor.ComputeSha256(testFile);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = path =>
        {
            if (ValidationService.PathsEqual(path, testFile))
            {
                hookInvoked = true;
                File.WriteAllBytes(testFile, new byte[] { 99, 98, 97 });
            }
        };

        try
        {
            var errors = new List<string>();
            var method = typeof(AssetProcessorService).GetMethod(
                "TryDeleteHashOwnedFileWithError",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var deleted = (bool)method.Invoke(processor, new object[] { testFile, expectedHash, "Test file", (Func<ValidationResult>)(() => ValidationResult.Success()), errors })!;

            Assert.True(hookInvoked, "OnBeforeDeleteFileHook must be invoked.");
            Assert.False(deleted, "Helper must refuse to delete modified file.");
            Assert.True(File.Exists(testFile), "File must be preserved.");
            Assert.Single(errors);
            Assert.Contains("changed before deletion", errors[0]);
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void BackupOldReference_PathBecomesReparseAfterOwnership_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r12_backup_reparse", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeBackupOldReferenceFinalAuthorityGate = t =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, oldSession.AssetFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                processor.BackupOldReference(tx));

            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.True(File.Exists(tx.OldSession.ReferenceDestinationPath), "Old canonical image must remain.");
            Assert.True(File.Exists(tx.OldSession.ReferenceProvenancePath), "Old canonical provenance must remain.");
            Assert.False(File.Exists(tx.BackupReferencePath), "Backup image must NOT exist.");
            Assert.False(File.Exists(tx.BackupProvenancePath), "Backup provenance must NOT exist.");
        }
        finally
        {
            AssetProcessorService.OnBeforeBackupOldReferenceFinalAuthorityGate = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void BackupOldReference_ImageChangesAfterOwnership_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r12_backup_img_change", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);

        var foreignBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 55, 66 };
        var hookInvoked = false;
        AssetProcessorService.OnBeforeBackupOldReferenceFinalAuthorityGate = t =>
        {
            hookInvoked = true;
            File.WriteAllBytes(tx.OldSession.ReferenceDestinationPath, foreignBytes);
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                processor.BackupOldReference(tx));
            Assert.Contains("OLD Reference changed before backup", ex.Message);

            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.True(File.Exists(tx.OldSession.ReferenceDestinationPath), "Old canonical image must remain.");
            Assert.True(File.Exists(tx.OldSession.ReferenceProvenancePath), "Old canonical provenance must remain.");
            Assert.False(File.Exists(tx.BackupReferencePath), "Backup image must NOT exist.");
            Assert.False(File.Exists(tx.BackupProvenancePath), "Backup provenance must NOT exist.");
        }
        finally
        {
            AssetProcessorService.OnBeforeBackupOldReferenceFinalAuthorityGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void BackupOldReference_ProvenanceChangesAfterOwnership_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r12_backup_prov_change", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeBackupOldReferenceFinalAuthorityGate = t =>
        {
            hookInvoked = true;
            var currentBytes = File.ReadAllBytes(tx.OldSession.ReferenceProvenancePath);
            var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(currentBytes).ToArray();
            File.WriteAllBytes(tx.OldSession.ReferenceProvenancePath, bomBytes);
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                processor.BackupOldReference(tx));
            Assert.Contains("OLD Reference provenance changed before backup", ex.Message);

            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.True(File.Exists(tx.OldSession.ReferenceDestinationPath), "Old canonical image must remain.");
            Assert.True(File.Exists(tx.OldSession.ReferenceProvenancePath), "Old canonical provenance must remain.");
            Assert.False(File.Exists(tx.BackupReferencePath), "Backup image must NOT exist.");
            Assert.False(File.Exists(tx.BackupProvenancePath), "Backup provenance must NOT exist.");
        }
        finally
        {
            AssetProcessorService.OnBeforeBackupOldReferenceFinalAuthorityGate = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_AssetRootBecomesReparseAtFolderCleanup_NoDirectoryDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r12_cancel_root_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnBeforeFolderCleanupHook = () =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, session.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                sessionService.Cancel(session));
            Assert.Contains("reparse point", ex.Message);

            Assert.True(hookInvoked, "OnBeforeFolderCleanupHook must be invoked.");
            Assert.True(sessionService.Exists(), "Session journal must remain intact when folder cleanup fails.");
        }
        finally
        {
            SessionService.OnBeforeFolderCleanupHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ProcessMain_Cleanup_AssetRootBecomesReparse_NoFileDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r13_pm_cleanup_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainImg = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainImg, "prompt", processedAt);
        sessionService.Save(session);

        var hookInvoked = false;
        AssetProcessorService.OnIngameTempCopiedHook = path =>
        {
            throw new IOException("Simulated failure after ingame temp copy.");
        };

        AssetProcessorService.OnBeforeDeleteFileHook = path =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, session.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var ex = Assert.Throws<AssetProcessingException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainImg, "prompt", processedAt));

            Assert.True(hookInvoked, "OnBeforeDeleteFileHook must be invoked during catch rollback.");
            Assert.Contains("automatic rollback was incomplete", ex.Message);
            Assert.True(File.Exists(session.GetMainTempIngamePath()), "Ingame temp file must be preserved when path safety fails.");
        }
        finally
        {
            AssetProcessorService.OnIngameTempCopiedHook = null;
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackMain_AssetRootBecomesReparse_NoFileDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r13_rm_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainImg = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainImg, "prompt", processedAt);
        sessionService.Save(session);

        var ingameFolder = session.GetIngameFolderPath();
        Directory.CreateDirectory(ingameFolder);
        var ingameDest = Path.Combine(ingameFolder, "asset_r13_rm_reparse.png");
        File.Copy(mainImg, ingameDest);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = path =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, session.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var result = processor.RollbackMain(session, "main.png");
            Assert.False(result.IsValid, "RollbackMain must fail when path safety fails during deletion.");
            Assert.True(hookInvoked, "OnBeforeDeleteFileHook must be invoked.");
            Assert.True(File.Exists(ingameDest), "Ingame file must be preserved.");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ProcessReference_Cleanup_AssetRootBecomesReparse_NoFileDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var prepared = processor.CreateReferenceSession(settings, "asset_r13_pr_cleanup_reparse", source, DateTimeOffset.Now);
        sessionService.Save(prepared);

        var hookInvoked = false;
        AssetProcessorService.OnFileCopiedHook = (src, dest) =>
        {
            if (dest.Contains(AppConstants.ReferenceFolderName) && !dest.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                // Inject failure right after staging copy
                throw new IOException("Simulated failure during staging copy.");
            }
        };

        AssetProcessorService.OnBeforeDeleteFileHook = path =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, prepared.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                processor.ProcessReference(prepared, settings, source, prepared.ReferenceProcessedAt));

            Assert.True(hookInvoked, "OnBeforeDeleteFileHook must be invoked.");
            Assert.Contains("automatic rollback was incomplete", ex.Message);
        }
        finally
        {
            AssetProcessorService.OnFileCopiedHook = null;
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackReference_AssetRootBecomesReparse_NoFileDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r13_rr_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = path =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, session.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var result = processor.RollbackReference(session);
            Assert.False(result.IsValid, "RollbackReference must fail when path safety fails during deletion.");
            Assert.True(hookInvoked, "OnBeforeDeleteFileHook must be invoked.");
            Assert.True(File.Exists(session.ReferenceDestinationPath), "Reference image must be preserved.");
            Assert.True(File.Exists(session.ReferenceProvenancePath), "Reference provenance must be preserved.");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void CommitReferenceReplacement_AssetRootBecomesReparse_NoFileDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r13_commit_reparse", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = path =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, oldSession.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var result = processor.CommitReferenceReplacement(tx);
            Assert.False(result.IsValid, "CommitReferenceReplacement must fail when path safety fails.");
            Assert.False(tx.IsCommitted, "Transaction must not be marked committed.");
            Assert.True(hookInvoked, "OnBeforeDeleteFileHook must be invoked.");
            Assert.True(File.Exists(tx.BackupReferencePath), "Backup reference must be preserved.");
            Assert.True(File.Exists(tx.BackupProvenancePath), "Backup provenance must be preserved.");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackReferenceReplacement_AssetRootBecomesReparse_NoFileDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r13_rollback_reparse", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeDeleteFileHook = path =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, oldSession.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var result = processor.RollbackReferenceReplacement(tx);
            Assert.False(result.IsValid, "RollbackReferenceReplacement must fail when path safety fails.");
            Assert.True(hookInvoked, "OnBeforeDeleteFileHook must be invoked.");
            Assert.True(File.Exists(tx.BackupReferencePath), "Backup reference must remain.");
            Assert.True(File.Exists(tx.BackupProvenancePath), "Backup provenance must remain.");
        }
        finally
        {
            AssetProcessorService.OnBeforeDeleteFileHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ReferenceReplacement_LegacySessionNullProvHash_CommittedAndRecovered()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r13_legacy_session", ref1, DateTimeOffset.Now);

        // Make it a legacy session where ReferenceProvenanceHash is null
        oldSession.ReferenceProvenanceHash = null;
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        // Materialization check
        Assert.NotNull(tx.OldSession.ReferenceProvenanceHash);
        Assert.NotEmpty(tx.OldSession.ReferenceProvenanceHash);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        sessionService.Save(tx.OldSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.NewPromotionPending));

        // Test startup recovery with legacy durable session
        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists(), "Journal should be removed after recovery rollback.");
        Assert.True(File.Exists(oldSession.ReferenceDestinationPath), "Old reference image restored.");
        Assert.True(File.Exists(oldSession.ReferenceProvenancePath), "Old reference provenance restored.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_OnBeforeCancelFileMoveHook_AssetRootBecomesReparse_Abort()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r13_cancel_move_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnBeforeCancelFileMoveHook = path =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, session.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                sessionService.Cancel(session));
            Assert.Contains("reparse point", ex.Message);
            Assert.True(hookInvoked, "OnBeforeCancelFileMoveHook must be invoked.");
            Assert.True(File.Exists(session.ReferenceDestinationPath), "Reference image must not be moved.");
            Assert.True(File.Exists(session.ReferenceProvenancePath), "Reference provenance must not be moved.");
        }
        finally
        {
            SessionService.OnBeforeCancelFileMoveHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_OnBeforeCancelFileDeleteHook_AssetRootBecomesReparse_Abort()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r13_cancel_delete_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnBeforeCancelFileDeleteHook = path =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, session.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                sessionService.Cancel(session));
            Assert.Contains("Cancel partially failed", ex.Message);
            Assert.True(hookInvoked, "OnBeforeCancelFileDeleteHook must be invoked.");
            Assert.True(sessionService.Exists(), "Session journal must remain intact.");
        }
        finally
        {
            SessionService.OnBeforeCancelFileDeleteHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_OnBeforeCancelRestoreHook_AssetRootBecomesReparse_Abort()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r13_cancel_restore_reparse", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var moveCount = 0;
        var restoreHookInvoked = false;

        SessionService.OnBeforeCancelFileMoveHook = path =>
        {
            moveCount++;
            if (moveCount == 2)
            {
                // Throw on moving reference image so restore of provenance triggers
                throw new IOException("Simulated reference move failure.");
            }
        };

        SessionService.OnBeforeCancelRestoreHook = (src, dest) =>
        {
            restoreHookInvoked = true;
            ValidationService.FileAttributesProvider = p =>
            {
                if (ValidationService.PathsEqual(p, session.AssetRootFolder))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(p);
            };
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                sessionService.Cancel(session));
            Assert.Contains("restoring reference provenance also failed", ex.Message);
            Assert.True(restoreHookInvoked, "OnBeforeCancelRestoreHook must be invoked.");
        }
        finally
        {
            SessionService.OnBeforeCancelFileMoveHook = null;
            SessionService.OnBeforeCancelRestoreHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ProcessMain_OnMainPromotedHook_TempIngameChanges_NoCanonicalIngamePromotion()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r14_pm_ingame_byte_race", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainImg = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainImg, "prompt", processedAt);
        sessionService.Save(session);

        var foreignBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 99, 88 };
        var hookInvoked = false;

        AssetProcessorService.OnMainPromotedHook = path =>
        {
            hookInvoked = true;
            File.WriteAllBytes(session.GetMainTempIngamePath(), foreignBytes);
        };

        try
        {
            var ex = Assert.Throws<AssetProcessingException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainImg, "prompt", processedAt));

            Assert.True(hookInvoked, "OnMainPromotedHook must be invoked.");
            var ingameDest = Path.Combine(session.GetIngameFolderPath(), $"{session.AssetFolderName}.png");
            Assert.False(File.Exists(ingameDest), "Canonical ingame destination must NOT be promoted.");
            Assert.True(session.IsMainCommitting, "Session must remain in committing state when promotion fails.");
        }
        finally
        {
            AssetProcessorService.OnMainPromotedHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ProcessMain_OnMainPromotedHook_IngameBecomesReparse_NoCanonicalIngamePromotion()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r14_pm_ingame_reparse_race", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var mainImg = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainImg, "prompt", processedAt);
        sessionService.Save(session);

        var hookInvoked = false;
        AssetProcessorService.OnMainPromotedHook = p =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, session.GetIngameFolderPath()))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var ex = Assert.Throws<AssetProcessingException>(() =>
                processor.ProcessMainImage(session, settings.AcceptedExtensions, mainImg, "prompt", processedAt));

            Assert.True(hookInvoked, "OnMainPromotedHook must be invoked.");
            var ingameDest = Path.Combine(session.GetIngameFolderPath(), $"{session.AssetFolderName}.png");
            Assert.False(File.Exists(ingameDest), "Canonical ingame destination must NOT be promoted into reparse point.");
        }
        finally
        {
            AssetProcessorService.OnMainPromotedHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void MoveHashOwnedFile_SourceChangesAtHook_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var src = Path.Combine(workspace.Root, "src.bin");
        var dest = Path.Combine(workspace.Root, "dest.bin");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        var hash = processor.ComputeSha256(src);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeHashOwnedMoveHook = (s, d) =>
        {
            hookInvoked = true;
            File.WriteAllBytes(src, new byte[] { 9, 9, 9 });
        };

        try
        {
            var method = typeof(AssetProcessorService).GetMethod(
                "MoveHashOwnedFileWithoutOverwrite",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var ex = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(processor, new object[] { src, dest, hash, "Test file", (Func<ValidationResult>)(() => ValidationResult.Success()) }));

            Assert.IsType<InvalidDataException>(ex.InnerException);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(File.Exists(dest), "Destination must not be created.");
            Assert.True(File.Exists(src), "Source file must remain intact.");
        }
        finally
        {
            AssetProcessorService.OnBeforeHashOwnedMoveHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void MoveHashOwnedFile_PathBecomesReparseAtHook_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();

        var src = Path.Combine(workspace.Root, "src.bin");
        var dest = Path.Combine(workspace.Root, "dest.bin");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        var hash = processor.ComputeSha256(src);

        var hookInvoked = false;
        var pathSafetyFailed = false;

        AssetProcessorService.OnBeforeHashOwnedMoveHook = (s, d) =>
        {
            hookInvoked = true;
            pathSafetyFailed = true;
        };

        try
        {
            var method = typeof(AssetProcessorService).GetMethod(
                "MoveHashOwnedFileWithoutOverwrite",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var ex = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(processor, new object[] {
                    src,
                    dest,
                    hash,
                    "Test file",
                    (Func<ValidationResult>)(() => pathSafetyFailed ? ValidationResult.Failure("Simulated path safety failure at hook.") : ValidationResult.Success())
                }));

            Assert.IsType<InvalidDataException>(ex.InnerException);
            Assert.True(hookInvoked, "Hook must be invoked.");
            Assert.False(File.Exists(dest), "Destination must not be created.");
        }
        finally
        {
            AssetProcessorService.OnBeforeHashOwnedMoveHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ProcessReference_ProvenanceChangesBeforeItsMove_NoCanonicalProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.CreateReferenceSession(settings, "asset_r14_pr_prov_race", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeHashOwnedMoveHook = (src, dest) =>
        {
            if (dest.EndsWith(AppConstants.ReferenceProvenanceFileName, StringComparison.OrdinalIgnoreCase))
            {
                hookInvoked = true;
                File.WriteAllBytes(src, new byte[] { 0xEF, 0xBB, 0xBF, 99, 99 });
            }
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                processor.ProcessReference(session, settings, refImg, session.ReferenceProcessedAt));

            Assert.True(hookInvoked, "Hook must be invoked on provenance move.");
            Assert.False(File.Exists(session.ReferenceProvenancePath), "Canonical provenance must NOT be created with foreign bytes.");
        }
        finally
        {
            AssetProcessorService.OnBeforeHashOwnedMoveHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void PromoteNewReference_ProvenanceChangesBeforeItsMove_NoCanonicalProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_repl_prov_promo_race", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeHashOwnedMoveHook = (src, dest) =>
        {
            if (ValidationService.PathsEqual(src, tx.TempNewProvenancePath))
            {
                hookInvoked = true;
                File.WriteAllBytes(src, new byte[] { 0xEF, 0xBB, 0xBF, 88, 88 });
            }
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                processor.PromoteNewReference(tx));

            Assert.True(hookInvoked, "Hook must be invoked on temp new provenance move.");
            Assert.False(File.Exists(tx.NewSession.ReferenceProvenancePath), "New canonical provenance must NOT be promoted.");
        }
        finally
        {
            AssetProcessorService.OnBeforeHashOwnedMoveHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void BackupOldReference_ProvenanceChangesBeforeItsMove_NoBackupProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_repl_old_backup_prov_race", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeHashOwnedMoveHook = (src, dest) =>
        {
            if (ValidationService.PathsEqual(src, tx.OldSession.ReferenceProvenancePath))
            {
                hookInvoked = true;
                File.WriteAllBytes(src, new byte[] { 0xEF, 0xBB, 0xBF, 77, 77 });
            }
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                processor.BackupOldReference(tx));

            Assert.True(hookInvoked, "Hook must be invoked on old provenance move.");
            Assert.False(File.Exists(tx.BackupProvenancePath), "Backup provenance must NOT be created with foreign bytes.");
        }
        finally
        {
            AssetProcessorService.OnBeforeHashOwnedMoveHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyProvenance_ExactTextWithBom_MaterializesActualRawHash()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validator = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r14_legacy_bom", refImg, DateTimeOffset.Now);

        // Overwrite provenance with exact rendered text with UTF-8 BOM
        var rawText = templateService.RenderReference(session.ReferenceFilename, session.ProjectName, session.ReferenceProcessedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        File.WriteAllText(session.ReferenceProvenancePath, rawText, new UTF8Encoding(true));

        var actualRawHash = processor.ComputeSha256(session.ReferenceProvenancePath);

        session.ReferenceProvenanceHash = null; // simulate legacy session

        var result = validator.TryGetExactReferenceProvenanceRawHash(session, session.ReferenceProvenancePath, templateService, out var materializedHash);

        Assert.True(result.IsValid, "Legacy provenance with BOM must pass exact text validation.");
        Assert.NotNull(materializedHash);
        Assert.Equal(actualRawHash, materializedHash);
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyReplacement_ProvenanceChangesBetweenSemanticProofAndMaterialization_NotBlessed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_legacy_snapshot_proof", ref1, DateTimeOffset.Now);

        // Make legacy session
        oldSession.ReferenceProvenanceHash = null;
        sessionService.Save(oldSession);

        // Tamper with old provenance
        File.WriteAllText(oldSession.ReferenceProvenancePath, "corrupted provenance content", new UTF8Encoding(false));

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });

        var ex = Assert.Throws<InvalidDataException>(() =>
            processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now));

        Assert.Contains("Reference output is inconsistent", ex.Message);
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyCancel_ProvenanceChangesBeforeRawAuthority_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r14_legacy_cancel_race", refImg, DateTimeOffset.Now);

        // Make legacy session
        session.ReferenceProvenanceHash = null;
        sessionService.Save(session);

        // Tamper provenance before cancel
        File.WriteAllText(session.ReferenceProvenancePath, "tampered legacy provenance", new UTF8Encoding(false));

        var ex = Assert.Throws<InvalidDataException>(() =>
            sessionService.Cancel(session));

        Assert.Contains("Reference provenance on disk does not match", ex.Message);
        Assert.True(File.Exists(session.ReferenceProvenancePath), "Canonical provenance must remain.");
        Assert.False(File.Exists(session.GetCancelTempProvenancePath()), "Cancel temp provenance must not exist.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyReplacementJournal_OldBackedUp_NullOldProvHash_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_j_oldbackedup_nullhash", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        // Explicitly set OldSession.ReferenceProvenanceHash = null in journal
        var journal = tx.ToJournal(ReferenceReplacementPhase.OldBackedUp);
        journal.OldSession.ReferenceProvenanceHash = null;
        sessionService.SaveReplacementJournal(journal);

        // Execute startup recovery
        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists(), "Journal must be deleted after successful rollback.");
        Assert.True(File.Exists(oldSession.ReferenceDestinationPath), "Old reference image restored.");
        Assert.True(File.Exists(oldSession.ReferenceProvenancePath), "Old reference provenance restored.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyReplacementJournal_NewPromotionPending_NullOldProvHash_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_j_newpromo_nullhash", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var journal = tx.ToJournal(ReferenceReplacementPhase.NewPromotionPending);
        journal.OldSession.ReferenceProvenanceHash = null;
        sessionService.SaveReplacementJournal(journal);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists(), "Journal must be deleted after rollback.");
        Assert.True(File.Exists(oldSession.ReferenceDestinationPath), "Old reference image restored.");
        Assert.True(File.Exists(oldSession.ReferenceProvenancePath), "Old reference provenance restored.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyReplacementJournal_NewPromoted_OldDurableNullHash_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_j_newpromoted_nullhash", ref1, DateTimeOffset.Now);

        // Durable session in session.json is OldSession with ReferenceProvenanceHash = null
        oldSession.ReferenceProvenanceHash = null;
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        var journal = tx.ToJournal(ReferenceReplacementPhase.NewPromoted);
        journal.OldSession.ReferenceProvenanceHash = null;
        sessionService.SaveReplacementJournal(journal);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists(), "Journal must be deleted after rollback.");
        Assert.True(File.Exists(oldSession.ReferenceDestinationPath), "Old reference image restored.");
        Assert.True(File.Exists(oldSession.ReferenceProvenancePath), "Old reference provenance restored.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyReplacementJournal_CleanupPending_NullOldProvHash_CommitsForward()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_j_cleanuppending_nullhash", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        // Session was switched to NewSession in session.json
        sessionService.Save(tx.NewSession);

        var journal = tx.ToJournal(ReferenceReplacementPhase.CleanupPending);
        journal.OldSession.ReferenceProvenanceHash = null; // simulate legacy journal
        sessionService.SaveReplacementJournal(journal);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists(), "Journal must be deleted after cleanup commit.");
        Assert.False(File.Exists(tx.BackupReferencePath), "Backup image must be deleted.");
        Assert.False(File.Exists(tx.BackupProvenancePath), "Backup provenance must be deleted.");
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath), "New reference image exists.");
        Assert.True(File.Exists(tx.NewSession.ReferenceProvenancePath), "New reference provenance exists.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyReplacementJournal_NullOldProvHash_CorruptBackup_FailsClosed()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_j_corruptbackup_nullhash", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        // Corrupt the backup provenance
        File.WriteAllText(tx.BackupProvenancePath, "corrupted backup provenance", new UTF8Encoding(false));

        var journal = tx.ToJournal(ReferenceReplacementPhase.OldBackedUp);
        journal.OldSession.ReferenceProvenanceHash = null;
        sessionService.SaveReplacementJournal(journal);

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.True(sessionService.ReplacementJournalExists(), "Journal must be preserved when backup is corrupt.");
        Assert.True(File.Exists(tx.BackupReferencePath), "Backup reference image must remain.");
        Assert.True(File.Exists(tx.BackupProvenancePath), "Backup provenance must remain.");
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void ReplacementRollback_OnBeforeRestoreFileHook_ReferenceFolderBecomesReparse_NoRestore()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r14_repl_rb_restore_reparse", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var hookInvoked = false;
        AssetProcessorService.OnBeforeRestoreFileHook = (src, dest) =>
        {
            hookInvoked = true;
            ValidationService.FileAttributesProvider = path =>
            {
                if (ValidationService.PathsEqual(path, Path.GetDirectoryName(dest) ?? ""))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            };
        };

        try
        {
            var result = processor.RollbackReferenceReplacement(tx);
            Assert.True(hookInvoked, "OnBeforeRestoreFileHook must be invoked.");
            Assert.False(result.IsValid, "Rollback must fail when destination parent is reparse.");
            Assert.True(File.Exists(tx.BackupReferencePath), "Backup reference image must remain.");
            Assert.True(File.Exists(tx.BackupProvenancePath), "Backup provenance must remain.");
        }
        finally
        {
            AssetProcessorService.OnBeforeRestoreFileHook = null;
            ValidationService.FileAttributesProvider = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_OnBeforeCancelFileMoveHook_ProvenanceBytesChange_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r14_cancel_prov_bytes_race", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnBeforeCancelFileMoveHook = path =>
        {
            if (path.EndsWith(AppConstants.ReferenceProvenanceFileName, StringComparison.OrdinalIgnoreCase))
            {
                hookInvoked = true;
                File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, 101, 102 });
            }
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                sessionService.Cancel(session));

            Assert.True(hookInvoked, "OnBeforeCancelFileMoveHook must be invoked.");
            Assert.Contains("reference provenance", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hash changed before move", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(session.ReferenceProvenancePath), "Modified canonical provenance must remain preserved.");
            Assert.False(File.Exists(session.GetCancelTempProvenancePath()), "Cancel temp provenance must not be created after the boundary hash check fails.");
            Assert.True(sessionService.Exists(), "Cancellation journal/session must remain available for recovery.");
        }
        finally
        {
            SessionService.OnBeforeCancelFileMoveHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_OnBeforeCancelFileMoveHook_ReferenceBytesChange_NoMove()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r14_cancel_ref_bytes_race", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnBeforeCancelFileMoveHook = path =>
        {
            if (path.EndsWith(session.ReferenceFilename, StringComparison.OrdinalIgnoreCase))
            {
                hookInvoked = true;
                File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 103, 104 });
            }
        };

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                sessionService.Cancel(session));

            Assert.True(hookInvoked, "OnBeforeCancelFileMoveHook must be invoked.");
            Assert.Contains("reference image", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hash changed before move", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(session.ReferenceDestinationPath), "Modified Reference image must remain at the canonical path and must not be moved.");
            Assert.True(File.Exists(session.ReferenceProvenancePath), "Previously moved provenance must be restored after the Reference move fails.");
            Assert.False(File.Exists(session.GetCancelTempReferencePath()), "Cancel temp Reference must not be created.");
            Assert.False(File.Exists(session.GetCancelTempProvenancePath()), "Cancel temp provenance must be removed by successful restoration.");
            Assert.Equal(CancelPhase.None, session.CancelPhase);
            Assert.Null(session.CancellationId);

            var persisted = sessionService.Load();
            Assert.NotNull(persisted);
            Assert.Equal(CancelPhase.None, persisted!.CancelPhase);
            Assert.Null(persisted.CancellationId);
        }
        finally
        {
            SessionService.OnBeforeCancelFileMoveHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_OnBeforeCancelFileDeleteHook_TempBytesChange_NoDelete()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r14_cancel_del_bytes_race", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var hookInvoked = false;
        SessionService.OnBeforeCancelFileDeleteHook = path =>
        {
            hookInvoked = true;
            File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 105, 106 });
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                sessionService.Cancel(session));

            Assert.True(hookInvoked, "OnBeforeCancelFileDeleteHook must be invoked.");
            Assert.Contains("Cancel partially failed", ex.Message);
            Assert.True(sessionService.Exists(), "Session journal must remain intact.");
        }
        finally
        {
            SessionService.OnBeforeCancelFileDeleteHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void Cancel_OnBeforeCancelRestoreHook_TempProvenanceBytesChange_NoRestore()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var refImg = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r14_cancel_restore_bytes_race", refImg, DateTimeOffset.Now);
        sessionService.Save(session);

        var moveCount = 0;
        var restoreHookInvoked = false;

        SessionService.OnBeforeCancelFileMoveHook = path =>
        {
            moveCount++;
            if (moveCount == 2)
            {
                // Throw on moving reference image so restore of provenance triggers
                throw new IOException("Simulated reference move failure.");
            }
        };

        SessionService.OnBeforeCancelRestoreHook = (src, dest) =>
        {
            restoreHookInvoked = true;
            File.WriteAllBytes(src, new byte[] { 0xEF, 0xBB, 0xBF, 107, 108 });
        };

        try
        {
            var ex = Assert.Throws<IOException>(() =>
                sessionService.Cancel(session));

            Assert.True(restoreHookInvoked, "OnBeforeCancelRestoreHook must be invoked.");
            Assert.Contains("restoring reference provenance also failed", ex.Message);
        }
        finally
        {
            SessionService.OnBeforeCancelFileMoveHook = null;
            SessionService.OnBeforeCancelRestoreHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyReplacementJournal_CleanupPending_HydratedHashPersistsBeforeCleanupMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r15_cleanup_retry", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);
        processor.PromoteNewReference(tx);

        sessionService.Save(tx.NewSession);

        var journal = tx.ToJournal(ReferenceReplacementPhase.CleanupPending);
        journal.OldSession.ReferenceProvenanceHash = null;
        sessionService.SaveReplacementJournal(journal);

        var deleteAttempts = 0;
        SessionService.OnBeforeReplacementJournalDeleteHook = () =>
        {
            deleteAttempts++;
            if (deleteAttempts == 1)
            {
                throw new IOException("Simulated replacement journal delete failure.");
            }
        };

        try
        {
            RunStartupRecovery(workspace, settings, processor, sessionService);

            Assert.True(sessionService.ReplacementJournalExists(), "Journal must remain after simulated deletion failure.");
            Assert.False(File.Exists(tx.BackupReferencePath), "OLD image backup should already be cleaned.");
            Assert.False(File.Exists(tx.BackupProvenancePath), "OLD provenance backup should already be cleaned.");

            var persistedAfterFirstRecovery = sessionService.LoadReplacementJournal();
            Assert.NotNull(persistedAfterFirstRecovery);
            Assert.False(string.IsNullOrWhiteSpace(persistedAfterFirstRecovery!.OldSession.ReferenceProvenanceHash),
                "Hydrated OLD provenance hash MUST be durable before backup cleanup.");

            SessionService.OnBeforeReplacementJournalDeleteHook = null;

            RunStartupRecovery(workspace, settings, processor, sessionService);

            Assert.False(sessionService.ReplacementJournalExists(), "Second startup must finish cleanup using the durable hydrated authority.");

            var finalSession = sessionService.Load();
            Assert.NotNull(finalSession);
            Assert.Equal(tx.NewSession.ReferenceHash, finalSession!.ReferenceHash);
            Assert.Equal(tx.NewSession.ReferenceFilename, finalSession.ReferenceFilename);
            Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
            Assert.True(File.Exists(tx.NewSession.ReferenceProvenancePath));
        }
        finally
        {
            SessionService.OnBeforeReplacementJournalDeleteHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void LegacyReplacementJournal_OldBackedUp_HydrationSaveFailure_NoRollbackMutation()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r15_hydration_save_fail", ref1, DateTimeOffset.Now);
        sessionService.Save(oldSession);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        processor.BackupOldReference(tx);

        var journal = tx.ToJournal(ReferenceReplacementPhase.OldBackedUp);
        journal.OldSession.ReferenceProvenanceHash = null;
        sessionService.SaveReplacementJournal(journal);

        Assert.False(File.Exists(oldSession.ReferenceDestinationPath));
        Assert.False(File.Exists(oldSession.ReferenceProvenancePath));
        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.True(File.Exists(tx.BackupProvenancePath));

        SessionService.OnReplacementPhaseSavingHook = (phase, _) =>
        {
            if (phase == ReferenceReplacementPhase.OldBackedUp)
            {
                throw new IOException("Simulated upgraded-journal save failure.");
            }
        };

        try
        {
            RunStartupRecovery(workspace, settings, processor, sessionService);

            Assert.True(sessionService.ReplacementJournalExists(), "Journal must remain after hydration save failure.");
            Assert.True(File.Exists(tx.BackupReferencePath), "Rollback must NOT restore/delete backup image before hydrated authority is durable.");
            Assert.True(File.Exists(tx.BackupProvenancePath), "Rollback must NOT restore/delete backup provenance before hydrated authority is durable.");
            Assert.False(File.Exists(oldSession.ReferenceDestinationPath), "Canonical OLD image must remain absent because rollback mutation must not start.");
            Assert.False(File.Exists(oldSession.ReferenceProvenancePath), "Canonical OLD provenance must remain absent because rollback mutation must not start.");

            var persisted = sessionService.LoadReplacementJournal();
            Assert.NotNull(persisted);
            Assert.True(string.IsNullOrWhiteSpace(persisted!.OldSession.ReferenceProvenanceHash), "Failed upgrade save must not be reported as durable.");
        }
        finally
        {
            SessionService.OnReplacementPhaseSavingHook = null;
        }
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void TransactionFromJournal_UsesIndependentSessionSnapshots()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var oldSession = processor.ProcessReference(settings, "asset_r15_no_alias", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(oldSession, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);
        var journal = tx.ToJournal(ReferenceReplacementPhase.Prepared);

        var method = typeof(MainForm).GetMethod("TransactionFromJournal", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var recoveredTx = (ReferenceReplacementTransaction)method!.Invoke(null, new object[] { journal })!;

        Assert.False(ReferenceEquals(journal.OldSession, recoveredTx.OldSession));
        Assert.False(ReferenceEquals(journal.NewSession, recoveredTx.NewSession));

        var journalOldHash = journal.OldSession.ReferenceProvenanceHash;
        recoveredTx.OldSession.ReferenceProvenanceHash = new string('a', 64);

        Assert.Equal(journalOldHash, journal.OldSession.ReferenceProvenanceHash);
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackMain_LegacyNullProvenanceHash_ExactBomProvenance_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();
        var templateService = workspace.CreateTemplateService();
        var validationService = workspace.CreateValidationService();

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r15_legacy_main_bom", refImage, DateTimeOffset.Now);

        var mainImage = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        var processedAt = DateTimeOffset.Now;

        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainImage, "legacy BOM prompt", processedAt);
        session.MainProvenanceHash = null;
        sessionService.Save(session);

        var finalProvenancePath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var generationDate = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var exactText = templateService.RenderFinal(session.MainFilename!, session.ReferenceFilename, session.ProjectName, generationDate, session.MainPrompt!);

        File.WriteAllText(finalProvenancePath, exactText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var exactBeforeRollback = validationService.ValidateExactFinalProvenanceOwnership(session, finalProvenancePath, templateService);
        Assert.True(exactBeforeRollback.IsValid, string.Join(Environment.NewLine, exactBeforeRollback.Errors));

        var rollback = processor.RollbackMain(session);
        Assert.True(rollback.IsValid, string.Join(Environment.NewLine, rollback.Errors));
        Assert.False(File.Exists(finalProvenancePath), "Exact legacy BOM provenance should be deleted as tool-owned.");
        Assert.False(session.IsMainCommitting);
        Assert.Null(session.MainTransactionId);
        Assert.Null(session.MainFilename);
        Assert.Null(session.MainHash);
        Assert.Null(session.MainProvenanceHash);
    }

    [Fact]
    [Trait("Category", "RecoveryCritical")]
    public void RollbackMain_LegacyNullProvenanceHash_CorruptProvenance_PreservesFile()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r15_legacy_main_corrupt", refImage, DateTimeOffset.Now);

        var mainImage = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
        session = processor.PrepareMainCommit(session, settings.AcceptedExtensions, mainImage, "legacy corrupt prompt", DateTimeOffset.Now);
        session.MainProvenanceHash = null;

        var provenancePath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        File.WriteAllText(provenancePath, "FOREIGN PROVENANCE", new UTF8Encoding(false));

        var rollback = processor.RollbackMain(session);
        Assert.False(rollback.IsValid);
        Assert.True(File.Exists(provenancePath), "Unknown provenance must be preserved.");
        Assert.True(session.IsMainCommitting, "Failed rollback must retain active Main transaction metadata.");
    }
}


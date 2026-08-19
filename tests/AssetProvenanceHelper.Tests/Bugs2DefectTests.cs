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
public sealed class Bugs2DefectTests
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

    private static void MaterializeOldBackedUp(
        AssetProcessorService processor,
        ReferenceReplacementTransaction tx,
        IReadOnlyCollection<string> extensions)
    {
        processor.CreateReplacementTempFiles(tx, extensions);
        processor.BackupOldReference(tx);
    }

    private static void MaterializeNewReferencePromotedOnly(
        ReferenceReplacementTransaction tx)
    {
        File.Move(
            tx.TempNewReferencePath,
            tx.NewSession.ReferenceDestinationPath,
            overwrite: false);
    }

    private static void MaterializeNewPromoted(
        AssetProcessorService processor,
        ReferenceReplacementTransaction tx,
        IReadOnlyCollection<string> extensions)
    {
        MaterializeOldBackedUp(processor, tx, extensions);
        processor.PromoteNewReference(tx);
    }

    private static void RunStartupRecovery(
        TestWorkspace workspace,
        AppSettings settings,
        AssetProcessorService processor,
        SessionService sessionService)
    {
        RunOnSta(() =>
        {
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
            }
        });
    }

    [Fact]
    public void R2_001_ValidateReplacementJournal_RejectsInvalidJsonAndPathTraversal()
    {
        using var workspace = new TestWorkspace();
        var validationService = workspace.CreateValidationService();
        var settings = workspace.CreateSettings();
        var processor = workspace.CreateAssetProcessor();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_001", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        var journal = tx.ToJournal(ReferenceReplacementPhase.Prepared);

        // Valid journal validates successfully
        var validResult = validationService.ValidateReferenceReplacementJournal(journal);
        Assert.True(validResult.IsValid, string.Join(Environment.NewLine, validResult.Errors));

        // Unknown phase
        journal.Phase = (ReferenceReplacementPhase)99;
        var invalidPhaseResult = validationService.ValidateReferenceReplacementJournal(journal);
        Assert.False(invalidPhaseResult.IsValid);
        Assert.Contains(invalidPhaseResult.Errors, e => e.Contains("unknown", StringComparison.OrdinalIgnoreCase));

        // Path traversal in backup path
        journal.Phase = ReferenceReplacementPhase.Prepared;
        journal.BackupReferencePath = Path.Combine(session.AssetFolder, "..", "escaped.bak");
        var escapeResult = validationService.ValidateReferenceReplacementJournal(journal);
        Assert.False(escapeResult.IsValid);
        Assert.Contains(escapeResult.Errors, e => e.Contains("deterministic", StringComparison.OrdinalIgnoreCase) || e.Contains("inside", StringComparison.OrdinalIgnoreCase) || e.Contains("parent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void R3_001_Replacement_Prepared_NoTemps_RollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_001_prep_notemps", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.Prepared));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.True(sessionService.Exists());
        var loaded = sessionService.Load()!;
        Assert.Equal(session.ReferenceHash, loaded.ReferenceHash);
        Assert.True(File.Exists(session.ReferenceDestinationPath));
    }

    [Fact]
    public void R3_001_Replacement_Prepared_BothTemps_CleansTempsAndRollsBack()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_001_prep_temps", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        processor.CreateReplacementTempFiles(tx, settings.AcceptedExtensions);
        Assert.True(File.Exists(tx.TempNewReferencePath));
        Assert.True(File.Exists(tx.TempNewProvenancePath));

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.Prepared));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.TempNewReferencePath));
        Assert.False(File.Exists(tx.TempNewProvenancePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
    }

    [Fact]
    public void R3_001_Replacement_OldBackedUp_RestoresOldReferenceAndProvenance()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_001_oldbackedup", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        MaterializeOldBackedUp(processor, tx, settings.AcceptedExtensions);
        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.True(File.Exists(tx.BackupProvenancePath));
        Assert.False(File.Exists(session.ReferenceDestinationPath));

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.OldBackedUp));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));

        var loaded = sessionService.Load()!;
        Assert.Equal(session.ReferenceHash, loaded.ReferenceHash);
    }

    [Fact]
    public void R3_001_Replacement_NewPromotionPending_ReferencePromotedOnly_DeletesNewAndRestoresOld()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_001_promoteref", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        MaterializeOldBackedUp(processor, tx, settings.AcceptedExtensions);
        MaterializeNewReferencePromotedOnly(tx);

        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
        Assert.True(File.Exists(tx.TempNewProvenancePath));

        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.NewPromotionPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.NewSession.ReferenceDestinationPath));
        Assert.False(File.Exists(tx.TempNewProvenancePath));
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.True(File.Exists(session.ReferenceProvenancePath));

        var loaded = sessionService.Load()!;
        Assert.Equal(session.ReferenceHash, loaded.ReferenceHash);
    }

    [Fact]
    public void R3_002_NewPromoted_DifferentFilename_OldSession_RollsBackToOldAuthority()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_002_diffname", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        MaterializeNewPromoted(processor, tx, settings.AcceptedExtensions);
        sessionService.Save(tx.OldSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.NewPromoted));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.True(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(tx.NewSession.ReferenceDestinationPath));

        var loaded = sessionService.Load()!;
        Assert.Equal(session.ReferenceHash, loaded.ReferenceHash);
        Assert.Equal("ref1.png", loaded.ReferenceFilename);
    }

    [Fact]
    public void R3_002_NewPromoted_SameFilename_OldSession_RollsBackToOldAuthority()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_002_samename", ref1, DateTimeOffset.Now);

        var otherDir = Path.Combine(workspace.Root, "other_dir");
        Directory.CreateDirectory(otherDir);
        var ref2 = Path.Combine(otherDir, "ref.png");
        var tempPng = workspace.CreateImage("temp_ref.png", new byte[] { 4, 5, 6, 7 });
        File.Copy(tempPng, ref2, overwrite: true);

        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        MaterializeNewPromoted(processor, tx, settings.AcceptedExtensions);
        sessionService.Save(tx.OldSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.NewPromoted));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));

        var loaded = sessionService.Load()!;
        Assert.Equal(session.ReferenceHash, loaded.ReferenceHash);
        Assert.Equal(session.ReferenceProvenanceHash, loaded.ReferenceProvenanceHash);
        Assert.True(session.ReferenceProcessedAt.EqualsExact(loaded.ReferenceProcessedAt));

        var canonicalHash = ValidationService.ComputeSha256(session.ReferenceDestinationPath);
        Assert.Equal(session.ReferenceHash, canonicalHash);
    }

    [Fact]
    public void R3_002_SessionSwitchPending_NewSessionAlreadySaved_CommitsForward()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_002_switch_new", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        MaterializeNewPromoted(processor, tx, settings.AcceptedExtensions);
        sessionService.Save(tx.NewSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.SessionSwitchPending));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));

        var loaded = sessionService.Load()!;
        Assert.Equal(tx.NewSession.ReferenceHash, loaded.ReferenceHash);
        Assert.Equal("ref2.png", loaded.ReferenceFilename);
    }

    [Fact]
    public void R3_003_CleanupPending_BothBackups_CleansBackupsAndDeletesJournal()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_003_cleanup", ref1, DateTimeOffset.Now);

        var ref2 = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
        var tx = processor.CreateReferenceReplacementTransaction(session, settings.AcceptedExtensions, ref2, DateTimeOffset.Now);

        MaterializeNewPromoted(processor, tx, settings.AcceptedExtensions);
        sessionService.Save(tx.NewSession);
        sessionService.SaveReplacementJournal(tx.ToJournal(ReferenceReplacementPhase.CleanupPending));

        Assert.True(File.Exists(tx.BackupReferencePath));
        Assert.True(File.Exists(tx.BackupProvenancePath));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.ReplacementJournalExists());
        Assert.False(File.Exists(tx.BackupReferencePath));
        Assert.False(File.Exists(tx.BackupProvenancePath));
        Assert.True(File.Exists(tx.NewSession.ReferenceDestinationPath));
    }

    [Fact]
    public void R3_006_PreparedReference_DirectoriesOnly_RemovesToolOwnedEmptyDirectories()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.CreateReferenceSession(settings, "asset_r3_006_dirs_only", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        Directory.CreateDirectory(session.AssetFolder);
        var refFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
        Directory.CreateDirectory(refFolder);

        Assert.True(Directory.Exists(session.AssetFolder));
        Assert.True(Directory.Exists(refFolder));
        Assert.False(File.Exists(session.ReferenceDestinationPath));
        Assert.False(File.Exists(session.ReferenceProvenancePath));

        RunStartupRecovery(workspace, settings, processor, sessionService);

        Assert.False(sessionService.Exists());
        Assert.False(Directory.Exists(refFolder), "Tool-created reference directory should be removed");
        Assert.False(Directory.Exists(session.AssetFolder), "Tool-created asset directory should be removed");
    }

    [Fact]
    public void R3_007_ValidateMainDestinationAvailability_RejectsCollisions()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_007_collision", ref1, DateTimeOffset.Now);

        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        File.WriteAllText(finalProv, "Pre-existing foreign provenance");

        var main1 = workspace.CreateImage("main1.png", new byte[] { 7, 8, 9 });

        var result = validationService.ValidateMainDestinationAvailability(session, settings.AcceptedExtensions, main1);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Final provenance already exists"));
    }

    [Fact]
    public void R3_008_MainSessionDeleteFailure_RollsBackOnceAndRestoresReferenceSession()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_008_delfail", ref1, DateTimeOffset.Now);
        sessionService.Save(session);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 7, 8, 9 });
        var processedAt = DateTimeOffset.Now;

        processor.PrepareMainCommit(session, settings.AcceptedExtensions, main1, "test prompt", processedAt);
        sessionService.Save(session);

        RunOnSta(() =>
        {
            var shownErrorCount = 0;
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => false;
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { shownErrorCount++; };

            var deleteAttempt = 0;
            SessionService.OnBeforeSessionDeleteHook = () =>
            {
                deleteAttempt++;
                if (deleteAttempt == 1)
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
                executeMethod?.Invoke(form, new object[] { session, main1, "test prompt", processedAt });

                // Main outputs must have been rolled back
                var rootMain = Path.Combine(session.AssetFolder, "main1.png");
                var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
                Assert.False(File.Exists(rootMain), "Main output must be rolled back");
                Assert.False(File.Exists(finalProv), "Final provenance must be rolled back");

                // Reference files must remain intact
                Assert.True(File.Exists(session.ReferenceDestinationPath), "Reference image must remain intact");
                Assert.True(File.Exists(session.ReferenceProvenancePath), "Reference provenance must remain intact");

                // Session was saved back to clean reference session
                Assert.True(sessionService.Exists());
                var restoredSession = sessionService.Load()!;
                Assert.False(restoredSession.IsMainCommitting);
                Assert.Null(restoredSession.MainFilename);
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
    public void R3_010_ValidatePreparedReferenceSession_RequiresSha256HexAndFields()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.CreateReferenceSession(settings, "asset_r3_010", ref1, DateTimeOffset.Now);

        var valid = validationService.ValidatePreparedReferenceSession(session);
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Errors));

        // Missing ReferenceHash
        session.ReferenceHash = "";
        var invalidHash = validationService.ValidatePreparedReferenceSession(session);
        Assert.False(invalidHash.IsValid);

        // Invalid ReferenceProvenanceHash
        session.ReferenceHash = new string('a', 64);
        session.ReferenceProvenanceHash = "invalid_hash";
        var invalidProvHash = validationService.ValidatePreparedReferenceSession(session);
        Assert.False(invalidProvHash.IsValid);
    }

    [Fact]
    public void R3_013_ProcessMainImage_ProvenanceHashDrift_ThrowsInvalidDataException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_013_drift", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main1.png", new byte[] { 7, 8, 9 });
        var now = DateTimeOffset.Now;

        processor.PrepareMainCommit(session, settings.AcceptedExtensions, main1, "original prompt", now);

        // Mutate prepared MainProvenanceHash to simulate drift
        session.MainProvenanceHash = new string('0', 64);

        var ex = Assert.Throws<InvalidDataException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "original prompt", now));

        Assert.Contains("Final provenance content changed", ex.Message);
    }

    [Fact]
    public void R2_004_StartupRecovery_MainCompleted_TamperedReferencePreservesMainOutputs()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var sessionService = workspace.CreateSessionService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_004", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
        var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main1, "prompt", DateTimeOffset.Now);
        sessionService.Save(session);

        File.WriteAllBytes(session.ReferenceDestinationPath, new byte[] { 99, 99, 99 });

        var rootMain = Path.Combine(session.AssetFolder, mainFilename);
        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        Assert.True(File.Exists(rootMain));
        Assert.True(File.Exists(finalProv));

        RunOnSta(() =>
        {
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => false;
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

                Assert.True(File.Exists(rootMain), "Main output must be preserved when reference is tampered");
                Assert.True(File.Exists(finalProv), "Final provenance must be preserved when reference is tampered");
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void R2_005_ValidateCompleteAsset_ValidatesViaMainProvenanceHashWhenTemplateChanged()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();
        var validationService = workspace.CreateValidationService();
        var templateService = workspace.CreateTemplateService();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_005", ref1, DateTimeOffset.Now);

        var main1 = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });
        var mainFilename = processor.ProcessMainPrepared(session, settings.AcceptedExtensions, main1, "original prompt", DateTimeOffset.Now);

        var rootMain = Path.Combine(session.AssetFolder, mainFilename);
        var finalProv = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);

        File.WriteAllText(workspace.FinalTemplatePath, "UPDATED TEMPLATE {{AssetId}} {{GenerationDate}} {{Prompt}}");

        var result = validationService.ValidateCompleteAsset(
            session,
            rootMain,
            finalProv,
            mainFilename,
            session.MainProcessedAt!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "original prompt",
            templateService,
            session.MainHash);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void R2_006_ProcessMainImage_WithoutPreparedTransaction_ThrowsInvalidOperationException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_006", ref1, DateTimeOffset.Now);

        Assert.False(session.IsMainCommitting);

        var main1 = workspace.CreateImage("main.png", new byte[] { 7, 8, 9 });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            processor.ProcessMainImage(session, settings.AcceptedExtensions, main1, "prompt", DateTimeOffset.Now));

        Assert.Contains("ProcessMainImage requires a prepared and durably persisted Main transaction", ex.Message);
    }

    [Fact]
    public void R2_006_PrepareMainCommit_IdenticalImageToReference_ThrowsInvalidOperationException()
    {
        using var workspace = new TestWorkspace();
        var processor = workspace.CreateAssetProcessor();
        var settings = workspace.CreateSettings();

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r2_006_identical", ref1, DateTimeOffset.Now);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            processor.PrepareMainCommit(session, settings.AcceptedExtensions, ref1, "prompt", DateTimeOffset.Now));

        Assert.Contains("identical to the reference image", ex.Message);
    }

    [Fact]
    public void R2_010_DropButtons_HaveExpectedText()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.CreateSettings();

        RunOnSta(() =>
        {
            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService());

            var btnDropReference = (Button)typeof(MainForm).GetField("btnDropReference", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            var btnDropMain = (Button)typeof(MainForm).GetField("btnDropMain", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;

            Assert.Equal("Drop file here", btnDropReference.Text);
            Assert.Equal("Drop file here", btnDropMain.Text);
        });
    }
}

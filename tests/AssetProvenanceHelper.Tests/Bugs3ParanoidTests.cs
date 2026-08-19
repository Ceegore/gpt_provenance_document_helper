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
        Assert.Contains(rollback.Errors, e => e.Contains("Replacement temp Reference no longer matches"));
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
        Assert.Contains(rollback.Errors, e => e.Contains("Replacement temp provenance does not match"));
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

        // Save tampered OldSession to disk where hash doesn't match
        var tamperedSession = session;
        tamperedSession.ReferenceHash = new string('f', 64);
        sessionService.Save(tamperedSession);

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
                executeMethod?.Invoke(form, new object[] { session, main1, "test prompt", processedAt });

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

        var customExts = new[] { ".customimg" };

        var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });
        var session = processor.ProcessReference(settings, "asset_r3_014_ext", ref1, DateTimeOffset.Now);

        var customImage = workspace.CreateImage("main.customimg", new byte[] { 10, 20, 30 });
        var standardPng = workspace.CreateImage("main.png", new byte[] { 40, 50, 60 });
        var now = DateTimeOffset.Now;

        // Accepting custom extension
        var preparedSession = processor.PrepareMainCommit(session, customExts, customImage, "prompt", now);
        Assert.True(preparedSession.IsMainCommitting);
        Assert.Equal("main.customimg", preparedSession.MainFilename);

        // Rejecting standard .png when custom extension set is passed
        session.ResetMainCommitMetadata();
        Assert.Throws<InvalidDataException>(() =>
            processor.PrepareMainCommit(session, customExts, standardPng, "prompt", now));
    }
}

#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Final paranoid sweep over the replacement-journal recovery failure paths
/// and the remaining cancellation recovery branches.
/// </summary>
public class UpgradeV13ParanoidJournalTests
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
    }

    private static MainForm CreateForm(
        TestWorkspace workspace,
        SessionService? sessionService = null,
        AssetProcessorService? processor = null)
    {
        return new MainForm(
            workspace.CreateSettings(),
            workspace.CreateSettingsService(),
            workspace.CreateImageFinder(),
            workspace.CreateTemplateService(),
            workspace.CreateValidationService(),
            processor ?? workspace.CreateAssetProcessor(),
            sessionService ?? workspace.CreateSessionService(),
            workspace.CreateProviderTemplateCatalogService(),
            workspace.CreateRecentDocumentHistoryService(),
            workspace.CreateRequestProgressService());
    }

    private static MethodInfo RecoverMethod =>
        typeof(MainForm).GetMethod(
            "RecoverSessionOnStartup",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void JournalRecovery_UnreadableJournalFailsClosed()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.WriteAllText(
                Path.Combine(
                    workspace.Root,
                    AppConstants.ReferenceReplacementFileName),
                "{ broken !!!");

            using var form = CreateForm(workspace);

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption.Contains(
                            "Critical Replacement Recovery Error",
                            StringComparison.Ordinal);
                };

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(errorShown);
                Assert.True(form.IsDisposed);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void JournalRecovery_StructurallyInvalidJournalFailsClosed()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var journal =
                new ReferenceReplacementJournal
                {
                    TransactionId = "bad",
                    Phase = ReferenceReplacementPhase.Prepared,
                    OldSession = new AssetSession(),
                    NewSession = new AssetSession()
                };

            var sessionService = workspace.CreateSessionService();
            sessionService.SaveReplacementJournal(journal);

            using var form = CreateForm(workspace, sessionService);

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption.Contains(
                            "Critical Replacement Recovery Error",
                            StringComparison.Ordinal);
                };

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(errorShown);
                Assert.True(form.IsDisposed);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void JournalRecovery_BoundaryPhaseWithoutAuthorityFailsClosed()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var oldSession = processor.CreateReferenceSession(
                settings,
                "asset_boundary",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(oldSession, settings, refSource, oldSession.ReferenceProcessedAt);

            var newSource = workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 });
            var transaction = processor.CreateReferenceReplacementTransaction(
                oldSession,
                settings.AcceptedExtensions,
                newSource,
                DateTimeOffset.Now);

            // Boundary phase (NewPromoted) but NO durable session at all.
            var sessionService = workspace.CreateSessionService();
            sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.NewPromoted));

            using var form = CreateForm(workspace, sessionService, processor);

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption.Contains(
                            "Critical Replacement Recovery Error",
                            StringComparison.Ordinal);
                };

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(errorShown);
                Assert.True(form.IsDisposed);
                Assert.True(sessionService.ReplacementJournalExists());
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void JournalRecovery_UnknownPhaseFailsClosed()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var oldSession = processor.CreateReferenceSession(
                settings,
                "asset_unknown_phase",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(oldSession, settings, refSource, oldSession.ReferenceProcessedAt);

            var transaction = processor.CreateReferenceReplacementTransaction(
                oldSession,
                settings.AcceptedExtensions,
                workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 }),
                DateTimeOffset.Now);

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(oldSession);
            sessionService.SaveReplacementJournal(
                transaction.ToJournal((ReferenceReplacementPhase)42));

            using var form = CreateForm(workspace, sessionService, processor);

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption.Contains(
                            "Critical Replacement Recovery Error",
                            StringComparison.Ordinal);
                };

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(errorShown);
                Assert.True(form.IsDisposed);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void JournalRecovery_SessionUnreadableWhileJournalExistsFailsClosed()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var oldSession = processor.CreateReferenceSession(
                settings,
                "asset_unreadable",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(oldSession, settings, refSource, oldSession.ReferenceProcessedAt);

            var transaction = processor.CreateReferenceReplacementTransaction(
                oldSession,
                settings.AcceptedExtensions,
                workspace.CreateImage("ref2.png", new byte[] { 4, 5, 6 }),
                DateTimeOffset.Now);

            var sessionService = workspace.CreateSessionService();

            // Corrupt session.json alongside the journal.
            File.WriteAllText(
                workspace.SessionPath,
                "{ corrupt !!!");

            sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.OldBackedUp));

            using var form = CreateForm(workspace, sessionService, processor);

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption.Contains(
                            "Critical Replacement Recovery Error",
                            StringComparison.Ordinal);
                };

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(errorShown);
                Assert.True(form.IsDisposed);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_ResumeValidationFailureDeletesRecord()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_resume_fail",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, refSource, session.ReferenceProcessedAt);

            // Tamper with the provenance so exact validation fails.
            File.WriteAllText(
                session.ReferenceProvenancePath,
                "TAMPERED");

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, sessionService, processor);

            TwoChoiceDialog.CustomChoiceProvider =
                (_, title, _, _, _) =>
                    title == "Inconsistent reference session";

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.False(sessionService.Exists());
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_ResumeValidationFailureExitCloses()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_resume_exit",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, refSource, session.ReferenceProcessedAt);

            File.WriteAllText(
                session.ReferenceProvenancePath,
                "TAMPERED");

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, sessionService, processor);

            TwoChoiceDialog.CustomChoiceProvider =
                (_, _, _, _, _) => false;

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(form.IsDisposed);
                Assert.True(sessionService.Exists());
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void Cancel_SaveFailureAfterPreparedRevertsPhase()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_cancel_savefail",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, refSource, session.ReferenceProcessedAt);

            var service = workspace.CreateSessionService();

            // Lock the session path so the Prepared-phase Save fails.
            SessionService.OnBeforeSaveSessionHook =
                _ => throw new IOException("injected save failure");

            try
            {
                Assert.Throws<IOException>(
                    () => service.Cancel(session));

                // Phase reverted in memory.
                Assert.Equal(CancelPhase.None, session.CancelPhase);
                Assert.Null(session.CancellationId);

                // Original files untouched.
                Assert.True(File.Exists(session.ReferenceDestinationPath));
                Assert.True(File.Exists(session.ReferenceProvenancePath));
            }
            finally
            {
                SessionService.OnBeforeSaveSessionHook = null;
            }
        });
    }

    [Fact]
    public void Cancel_FilesRenamedSaveFailureKeepsPhase()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_cancel_phasefail",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, refSource, session.ReferenceProcessedAt);

            // Provenance already moved; reference still at origin.
            session.CancelPhase = CancelPhase.Prepared;
            session.CancellationId = new string('3', 32);

            var tempProv = session.GetCancelTempProvenancePath();
            Directory.CreateDirectory(Path.GetDirectoryName(tempProv)!);
            File.Move(session.ReferenceProvenancePath, tempProv);

            // Fail the FilesRenamed Save.
            SessionService.OnBeforeSaveSessionHook =
                _ => throw new IOException("injected save failure");

            var service = workspace.CreateSessionService();

            try
            {
                Assert.Throws<IOException>(
                    () => service.Cancel(session));

                // Phase reverted to Prepared so recovery can retry.
                Assert.Equal(CancelPhase.Prepared, session.CancelPhase);
                Assert.True(File.Exists(tempProv));
            }
            finally
            {
                SessionService.OnBeforeSaveSessionHook = null;
            }
        });
    }
}

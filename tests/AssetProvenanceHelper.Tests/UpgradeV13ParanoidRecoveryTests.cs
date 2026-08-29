#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Paranoid verification of the crash-recovery and workflow reconciliation
/// branches. Every scenario drives the real recovery entry point.
/// </summary>
public class UpgradeV13ParanoidRecoveryTests
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
        AppSettings? settings = null,
        SessionService? sessionService = null,
        AssetProcessorService? processor = null)
    {
        return new MainForm(
            settings ?? workspace.CreateSettings(),
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

    private static bool FormIsClosed(MainForm form) =>
        form.IsDisposed;

    [Fact]
    public void Recovery_BrokenSessionFileDeleteRecord()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.WriteAllText(
                workspace.SessionPath,
                "{ broken json !!!");

            using var form = CreateForm(workspace);

                        

            TwoChoiceDialog.CustomChoiceProvider =
                (_, title, _, _, _) => title == "Broken session file";

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.False(FormIsClosed(form));
                Assert.False(File.Exists(workspace.SessionPath));

                var status =
                    form.Controls.Find("txtStatusHistory", true)
                        .FirstOrDefault() as TextBox;

                Assert.Contains(
                    "Broken session record deleted",
                    status!.Text,
                    StringComparison.Ordinal);
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_BrokenSessionFileExitClosesForm()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.WriteAllText(
                workspace.SessionPath,
                "{ broken json !!!");

            using var form = CreateForm(workspace);

                        

            TwoChoiceDialog.CustomChoiceProvider =
                (_, title, _, _, _) => false;

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(FormIsClosed(form));
                Assert.True(File.Exists(workspace.SessionPath));
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_BrokenSessionDeleteFailsClosesForm()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.WriteAllText(
                workspace.SessionPath,
                "{ broken json !!!");

            // Lock the session file so Delete fails.
            using var handle =
                new FileStream(
                    workspace.SessionPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);

using var form = CreateForm(workspace);

            TwoChoiceDialog.CustomChoiceProvider =
                (_, title, _, _, _) => title == "Broken session file";

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(FormIsClosed(form));
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_InterruptedCancellationResumed()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_cancel_resume2",
                source,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, source, session.ReferenceProcessedAt);

            // Persist a session stuck in the FilesRenamed cancel phase.
            session.CancelPhase = CancelPhase.FilesRenamed;
            session.CancellationId = new string('4', 32);

            var tempProv = session.GetCancelTempProvenancePath();
            var tempRef = session.GetCancelTempReferencePath();

            Directory.CreateDirectory(Path.GetDirectoryName(tempProv)!);
            File.Move(session.ReferenceProvenancePath, tempProv);
            File.Move(session.ReferenceDestinationPath, tempRef);

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, settings, sessionService);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                RecoverMethod.Invoke(form, null);

                // Cancellation finished: everything is gone.
                Assert.False(File.Exists(tempProv));
                Assert.False(File.Exists(tempRef));
                Assert.False(sessionService.Exists());
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_InterruptedCancellationFailureClosesForm()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_cancel_fail",
                source,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, source, session.ReferenceProcessedAt);

            // Corrupt cancel state: provenance moved but tampered.
            session.CancelPhase = CancelPhase.Prepared;
            session.CancellationId = new string('4', 32);

            var tempProv = session.GetCancelTempProvenancePath();
            Directory.CreateDirectory(Path.GetDirectoryName(tempProv)!);
            File.Move(session.ReferenceProvenancePath, tempProv);
            File.WriteAllText(tempProv, "TAMPERED");

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, settings, sessionService);

                        

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.True(FormIsClosed(form));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_InvalidSessionDeleteRecord()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var source = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_invalid_rec",
                source,
                DateTimeOffset.Now);

            // Persist an invalid session: missing processed time and files.
            session.ReferenceProcessedAt = default;
            session.ReferenceHash = "bad";

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, settings, sessionService);

TwoChoiceDialog.CustomChoiceProvider =
                (_, title, _, _, _) =>
                    title == "Invalid unfinished session"
                    || title == "Corrupt prepared session";

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
    public void Recovery_PreparedReferenceInvalidStructureDeleteRecord()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var session = new AssetSession
            {
                SchemaVersion = 2,
                WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
                ReferenceCommitPhase = ReferenceCommitPhase.Prepared,
                ProjectName = "P",
                AssetRootFolder = workspace.Assets,
                AssetFolderName = "asset_prep",
                AssetFolder = Path.Combine(workspace.Assets, "asset_prep"),
                ReferenceProcessedAt = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
                ReferenceHash = "bad",
                ReferenceProvenanceHash = "also-bad",
                ReferenceDestinationPath = Path.Combine(workspace.Assets, "asset_prep", "reference", "ref.png"),
                ReferenceProvenancePath = Path.Combine(workspace.Assets, "asset_prep", "reference", AppConstants.ReferenceProvenanceFileName)
            };

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, sessionService: sessionService);

            TwoChoiceDialog.CustomChoiceProvider =
                (_, title, _, _, _) => title == "Corrupt prepared session";

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
    public void Recovery_CompleteNoReferenceLeftoverRecordDeleted()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var source = workspace.CreateImage("main.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateNoReferenceMainSession(
                settings,
                "asset_leftover",
                source,
                "prompt",
                DateTimeOffset.Now);

            processor.ProcessMainImage(
                session,
                settings.AcceptedExtensions,
                source,
                "prompt",
                session.MainProcessedAt!.Value);

            // Persist the leftover journal after completion.
            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, settings, sessionService, processor);

            TwoChoiceDialog.CustomChoiceProvider =
                (_, title, _, _, _) => title == "Completed asset session";

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.False(sessionService.Exists());

                // Asset outputs must still exist.
                Assert.True(
                    File.Exists(
                        Path.Combine(
                            session.AssetFolder,
                            "main.png")));

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            session.AssetFolder,
                            AppConstants.FinalProvenanceFileName)));
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_IncompleteNoReferenceRolledBack()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var source = workspace.CreateImage("main.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateNoReferenceMainSession(
                settings,
                "asset_incomplete",
                source,
                "prompt",
                DateTimeOffset.Now);

            // Persist the journal but never run the commit.
            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, settings, sessionService, processor);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                RecoverMethod.Invoke(form, null);

                Assert.False(sessionService.Exists());
                Assert.False(
                    Directory.Exists(
                        Path.Combine(
                            settings.AssetRootFolder,
                            "asset_incomplete")));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_InterruptedMainCommitRolledBackAndReferenceResumed()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_main_crash",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, refSource, session.ReferenceProcessedAt);

            // Begin a Main commit that never finished.
            var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
            processor.PrepareMainCommit(
                session,
                settings.AcceptedExtensions,
                mainSource,
                "prompt",
                DateTimeOffset.Now);

            // Some staged files exist but nothing was promoted.
            var tempMain = session.GetMainTempImagePath();
            Directory.CreateDirectory(session.AssetFolder);
            File.Copy(mainSource, tempMain);

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(session);

            using var form = CreateForm(workspace, settings, sessionService, processor);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                RecoverMethod.Invoke(form, null);

                // Main rolled back; reference session resumed.
                var current =
                    typeof(MainForm).GetField(
                        "_currentSession",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as AssetSession;

                Assert.NotNull(current);
                Assert.Equal(
                    AssetWorkflowMode.ReferenceAssisted,
                    current!.WorkflowMode);

                Assert.False(File.Exists(tempMain));
                Assert.True(sessionService.Exists());
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_ReplacementJournalRollsBackToOld()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var oldSession = processor.CreateReferenceSession(
                settings,
                "asset_rr_rec",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(oldSession, settings, refSource, oldSession.ReferenceProcessedAt);

            // Prepare a replacement and persist the journal at OldBackedUp.
            var newSource = workspace.CreateImage("ref2.png", new byte[] { 7, 8, 9 });
            var transaction = processor.CreateReferenceReplacementTransaction(
                oldSession,
                settings.AcceptedExtensions,
                newSource,
                DateTimeOffset.Now);

processor.CreateReplacementTempFiles(transaction, settings.AcceptedExtensions);
            processor.BackupOldReference(transaction);

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(oldSession);
            sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.OldBackedUp));

            using var form = CreateForm(workspace, settings, sessionService, processor);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                RecoverMethod.Invoke(form, null);

                // Old reference restored, journal deleted, session persisted.
                Assert.False(sessionService.ReplacementJournalExists());
                Assert.True(sessionService.Exists());
                Assert.True(File.Exists(oldSession.ReferenceDestinationPath));
                Assert.True(File.Exists(oldSession.ReferenceProvenancePath));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Recovery_ReplacementJournalCommitsForward()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var oldSession = processor.CreateReferenceSession(
                settings,
                "asset_rr_fwd",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(oldSession, settings, refSource, oldSession.ReferenceProcessedAt);

            var newSource = workspace.CreateImage("ref2.png", new byte[] { 7, 8, 9 });
            var transaction = processor.CreateReferenceReplacementTransaction(
                oldSession,
                settings.AcceptedExtensions,
                newSource,
                DateTimeOffset.Now);

            processor.CreateReplacementTempFiles(transaction, settings.AcceptedExtensions);
            processor.BackupOldReference(transaction);
            processor.PromoteNewReference(transaction);

            var sessionService = workspace.CreateSessionService();
            sessionService.Save(transaction.NewSession);
            sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.SessionSwitched));

            using var form = CreateForm(workspace, settings, sessionService, processor);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                RecoverMethod.Invoke(form, null);

                // New reference kept, journal deleted, session remains.
                Assert.False(sessionService.ReplacementJournalExists());
                Assert.True(sessionService.Exists());
                Assert.True(File.Exists(transaction.NewSession.ReferenceDestinationPath));
                Assert.True(File.Exists(transaction.NewSession.ReferenceProvenancePath));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    // ---------- Workflow failure reconciliation ----------

    [Fact]
    public void MainWorkflow_NoActiveReferenceSessionMessage()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var mainImg = workspace.CreateImage("main.png", new byte[] { 1, 2, 3 });
            form.SetSelectedImage(ImageSlot.Main, mainImg);

            var txtAssetName =
                form.Controls.Find("txtAssetFolderName", true)
                    .FirstOrDefault() as TextBox;

            txtAssetName!.Text = "asset_no_session";

            var txtPrompt =
                form.Controls.Find("txtPrompt", true)
                    .FirstOrDefault() as TextBox;

            txtPrompt!.Text = "prompt";

            var messageShown = false;

            MainForm.MessageBoxProvider =
                (_, text, caption, _, _) =>
                {
                    messageShown =
                        caption == "Main Image"
                        && text.Contains("No active reference session", StringComparison.Ordinal);
                };

            try
            {
                var handleMain =
                    typeof(MainForm).GetMethod(
                        "HandleMainImage",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                handleMain!.Invoke(form, null);

                Assert.True(messageShown);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void NoReference_ExistingDestinationCancelledByUser()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var mainImg = workspace.CreateImage("main.png", new byte[] { 1, 2, 3 });

            Directory.CreateDirectory(
                Path.Combine(
                    settings.AssetRootFolder,
                    "asset_exists"));

            using var form = CreateForm(workspace, settings);

            var chkNoReference =
                form.Controls.Find("chkNoReference", true)
                    .FirstOrDefault() as CheckBox;

            chkNoReference!.Checked = true;

            form.SetSelectedImage(ImageSlot.Main, mainImg);

            var txtAssetName =
                form.Controls.Find("txtAssetFolderName", true)
                    .FirstOrDefault() as TextBox;

            txtAssetName!.Text = "asset_exists";

            var txtPrompt =
                form.Controls.Find("txtPrompt", true)
                    .FirstOrDefault() as TextBox;

            txtPrompt!.Text = "prompt";

            // User declines to use the existing folder.
            TwoChoiceDialog.CustomChoiceProvider =
                (_, _, _, _, _) => false;

            try
            {
                var handleMain =
                    typeof(MainForm).GetMethod(
                        "HandleMainImage",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                handleMain!.Invoke(form, null);

                // No session was created.
                Assert.False(File.Exists(Path.Combine(settings.AssetRootFolder, "asset_exists", "main.png")));
            }
            finally
            {
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void MainWorkflow_SessionDeleteFailureRollsBackMainAndRestoresReference()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_del_fail",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, refSource, session.ReferenceProcessedAt);

            var sessionService = workspace.CreateSessionService();

            using var form = CreateForm(workspace, settings, sessionService, processor);

            var sessionField =
                typeof(MainForm).GetField(
                    "_currentSession",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var stateField =
                typeof(MainForm).GetField(
                    "_state",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            var mainSource = workspace.CreateImage("main.png", new byte[] { 4, 5, 6 });
            form.SetSelectedImage(ImageSlot.Main, mainSource);

            var txtPrompt =
                form.Controls.Find("txtPrompt", true)
                    .FirstOrDefault() as TextBox;

            txtPrompt!.Text = "prompt";

            // Make session deletion fail: lock session.json.
            using var handle =
                new FileStream(
                    workspace.SessionPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                var handleMain =
                    typeof(MainForm).GetMethod(
                        "HandleMainImage",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                handleMain!.Invoke(form, null);

                // Main outputs rolled back; reference session restored.
                var current =
                    sessionField?.GetValue(form) as AssetSession;

                Assert.NotNull(current);
                Assert.Equal(session.ReferenceHash, current!.ReferenceHash);

                Assert.False(
                    File.Exists(
                        Path.Combine(
                            session.AssetFolder,
                            "main.png")));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void MainWorkflow_MainProcessingFailureReconcilesReferenceSession()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_proc_fail",
                refSource,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, refSource, session.ReferenceProcessedAt);

            var sessionService = workspace.CreateSessionService();

            using var form = CreateForm(workspace, settings, sessionService, processor);

            var sessionField =
                typeof(MainForm).GetField(
                    "_currentSession",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var stateField =
                typeof(MainForm).GetField(
                    "_state",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            var txtPrompt =
                form.Controls.Find("txtPrompt", true)
                    .FirstOrDefault() as TextBox;

            txtPrompt!.Text = "prompt";

            // Main identical to reference -> PrepareMainCommit fails.
            form.SetSelectedImage(ImageSlot.Main, refSource);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                var handleMain =
                    typeof(MainForm).GetMethod(
                        "HandleMainImage",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                handleMain!.Invoke(form, null);

                // Session remains the durable reference session.
                var current =
                    sessionField?.GetValue(form) as AssetSession;

                Assert.NotNull(current);
                Assert.Equal(session.ReferenceHash, current!.ReferenceHash);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void PasteClipboard_EmptyClipboardShowsMessage()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            // Hook returns null (empty clipboard).
            form.ClipboardProvider = () => null;

            var messageShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    messageShown =
                        caption == "Paste Clipboard";
                };

            try
            {
                var paste =
                    typeof(MainForm).GetMethod(
                        "PasteClipboard",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                paste!.Invoke(form, null);

                Assert.True(messageShown);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void PasteClipboard_RealClipboardFallback()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            Clipboard.SetText("real clipboard text");

            try
            {
                var paste =
                    typeof(MainForm).GetMethod(
                        "PasteClipboard",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                paste!.Invoke(form, null);

                var txtPrompt =
                    form.Controls.Find("txtPrompt", true)
                        .FirstOrDefault() as TextBox;

                Assert.Equal("real clipboard text", txtPrompt!.Text);
            }
            finally
            {
                Clipboard.Clear();
            }
        });
    }

    [Fact]
    public void OpenDownloads_InvalidFolderHighlightsAndShowsMessage()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var txtDownload =
                form.Controls.Find("txtDownloadFolder", true)
                    .FirstOrDefault() as TextBox;

            txtDownload!.Text =
                Path.Combine(
                    workspace.Root,
                    "not-a-folder");

            var messageShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    messageShown =
                        caption == "Open Image Folder";
                };

            try
            {
                var openDownloads =
                    typeof(MainForm).GetMethod(
                        "OpenDownloads",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                openDownloads!.Invoke(form, null);

                Assert.True(messageShown);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void OpenAssetFolder_WithNoPathDoesNothing()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var opened = false;

            MainForm.OpenFolderProvider = _ => opened = true;

            try
            {
                var openFolder =
                    typeof(MainForm).GetMethod(
                        "OpenAssetFolder",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                openFolder!.Invoke(form, null);

                Assert.False(opened);
            }
            finally
            {
                MainForm.OpenFolderProvider = null;
            }
        });
    }

    [Fact]
    public void OpenAssetFolder_OpensCompletedAssetFolder()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var assetFolder =
                Path.Combine(
                    workspace.Assets,
                    "asset_open");

            Directory.CreateDirectory(assetFolder);

            var lastCompletedField =
                typeof(MainForm).GetField(
                    "_lastCompletedAssetFolderPath",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            lastCompletedField?.SetValue(form, assetFolder);

            string? opened = null;

            MainForm.OpenFolderProvider = path => opened = path;

            try
            {
                var openFolder =
                    typeof(MainForm).GetMethod(
                        "OpenAssetFolder",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                openFolder!.Invoke(form, null);

                Assert.Equal(assetFolder, opened);
            }
            finally
            {
                MainForm.OpenFolderProvider = null;
            }
        });
    }
}

#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Final paranoid branch sweep over the remaining uncovered UI paths.
/// </summary>
public class UpgradeV13ParanoidBranchTests
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
        AppSettings? settings = null)
    {
        return new MainForm(
            settings ?? workspace.CreateSettings(),
            workspace.CreateSettingsService(),
            workspace.CreateImageFinder(),
            workspace.CreateTemplateService(),
            workspace.CreateValidationService(),
            workspace.CreateAssetProcessor(),
            workspace.CreateSessionService(),
            workspace.CreateProviderTemplateCatalogService(),
            workspace.CreateRecentDocumentHistoryService(),
            workspace.CreateRequestProgressService());
    }

    private static T FindControl<T>(MainForm form, string name)
        where T : Control
    {
        var control =
            form.Controls.Find(name, true)
                .FirstOrDefault();

        Assert.NotNull(control);
        return Assert.IsType<T>(control);
    }

    // ---------- Image selection ----------

    [Fact]
    public void ImageSelection_RefreshWithBadDownloadFolderShowsError()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var txtDownload =
                FindControl<TextBox>(form, "txtDownloadFolder");

            txtDownload.Text =
                Path.Combine(
                    workspace.Root,
                    "missing");

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, _, _, _) => errorShown = true;

            try
            {
                var refresh =
                    typeof(MainForm).GetMethod(
                        "RefreshImageSelection",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                refresh!.Invoke(form, new object[] { ImageSlot.Reference });

                Assert.True(errorShown);
                Assert.Null(form.GetSelectedImage(ImageSlot.Reference));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void ImageSelection_RefreshEmptyFolderClearsSelection()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var previous = workspace.CreateImage("old.png", new byte[] { 1 });
            form.SetSelectedImage(ImageSlot.Main, previous);

            // Empty the Downloads folder.
            File.Delete(previous);

            var refresh =
                typeof(MainForm).GetMethod(
                    "RefreshImageSelection",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            refresh!.Invoke(form, new object[] { ImageSlot.Main });

            Assert.Null(form.GetSelectedImage(ImageSlot.Main));
        });
    }

    [Fact]
    public void ImageSelection_ChooseViaDialogProviderCancelled()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            MainForm.OpenFileDialogProvider = (_, _) => null;

            try
            {
                var choose =
                    typeof(MainForm).GetMethod(
                        "ChooseImageFile",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                choose!.Invoke(form, new object[] { ImageSlot.Reference });

                Assert.Null(form.GetSelectedImage(ImageSlot.Reference));
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ImageSelection_ChooseInvalidFileShowsError()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var badFile =
                Path.Combine(
                    workspace.Root,
                    "bad.png");

            File.WriteAllBytes(badFile, new byte[] { 0, 1, 2 });

            MainForm.OpenFileDialogProvider = (_, _) => badFile;

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, _, _, _) => errorShown = true;

            try
            {
                var choose =
                    typeof(MainForm).GetMethod(
                        "ChooseImageFile",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                choose!.Invoke(form, new object[] { ImageSlot.Main });

                Assert.True(errorShown);
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void ImageSelection_DropMultipleFilesShowsMessage()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var messageShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    messageShown =
                        caption == "Invalid drop";
                };

            try
            {
                var drop =
                    typeof(MainForm).GetMethod(
                        "ImageDrop_DragDrop",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var data =
                    new DataObject(
                        DataFormats.FileDrop,
                        new[] { "a.png", "b.png" });

                var drag =
                    new DragEventArgs(
                        data,
                        0,
                        0,
                        0,
                        DragDropEffects.Copy,
                        DragDropEffects.None);

                drop!.Invoke(
                    form,
                    new object[]
                    {
                        ImageSlot.Main,
                        drag
                    });

                Assert.True(messageShown);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    // ---------- Validation UI ----------

    [Fact]
    public void ValidationUi_ValidateRefreshUiBadFolder()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var txtDownload =
                FindControl<TextBox>(form, "txtDownloadFolder");

            txtDownload.Text = "C:\\missing-folder-xyz";

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, _, _, _) => errorShown = true;

            try
            {
                var validate =
                    typeof(MainForm).GetMethod(
                        "ValidateRefreshUi",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var ok = (bool)(validate!.Invoke(form, new object[] { ImageSlot.Reference }) ?? false);

                Assert.False(ok);
                Assert.True(errorShown);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void ValidationUi_MainActionNoReferenceRequiresNameAndImage()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var chkNoReference =
                FindControl<CheckBox>(form, "chkNoReference");

            chkNoReference.Checked = true;

            // No asset name, no image, no prompt.
            var validate =
                typeof(MainForm).GetMethod(
                    "ValidateMainActionUi",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var ok = (bool)(validate!.Invoke(form, null) ?? false);

            Assert.False(ok);

            // The missing Main image candidate must be highlighted.
            var host =
                typeof(MainForm).GetField(
                    "pnlMainImageHost",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form) as Panel;

            Assert.Equal(UiTheme.Error, host!.BackColor);

            var promptHost =
                typeof(MainForm).GetField(
                    "pnlPromptHost",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form) as Panel;

            Assert.Equal(UiTheme.Error, promptHost!.BackColor);
        });
    }

    // ---------- MainForm base ----------

    [Fact]
    public void MainForm_BrowseDownloadFolderViaProvider()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            MainForm.FolderBrowserDialogProvider =
                (_, _) => workspace.Downloads;

            try
            {
                var browse =
                    typeof(MainForm).GetMethod(
                        "BrowseDownloadFolder",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                browse!.Invoke(form, null);

                var txtDownload =
                    FindControl<TextBox>(form, "txtDownloadFolder");

                Assert.Equal(workspace.Downloads, txtDownload.Text);

                // Settings persisted by the browse flow.
                Assert.True(File.Exists(workspace.SettingsPath));
            }
            finally
            {
                MainForm.FolderBrowserDialogProvider = null;
            }
        });
    }

    [Fact]
    public void MainForm_BrowseAssetRootViaProviderCancelled()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            MainForm.FolderBrowserDialogProvider = (_, _) => null;

            try
            {
                var browse =
                    typeof(MainForm).GetMethod(
                        "BrowseAssetRoot",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                browse!.Invoke(form, null);

                var txtRoot =
                    FindControl<TextBox>(form, "txtAssetRoot");

                // Unchanged.
                Assert.Equal(workspace.Assets, txtRoot.Text);
            }
            finally
            {
                MainForm.FolderBrowserDialogProvider = null;
            }
        });
    }

    [Fact]
    public void MainForm_KeyDownF1ShowsHelp()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            form.Show();

            var keyDown =
                typeof(MainForm).GetMethod(
                    "MainForm_KeyDown",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var f1 = new KeyEventArgs(Keys.F1);
            keyDown!.Invoke(form, new object[] { form, f1 });

            var help =
                form.Controls.Find("helpOverlay", true)
                    .FirstOrDefault() as HelpOverlayControl;

            Assert.NotNull(help);
            Assert.True(help!.Visible);
        });
    }

    [Fact]
    public void MainForm_KeyDownCtrlOOpensAssetFolder()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateForm(workspace);

            var folder = Path.Combine(workspace.Assets, "asset_open");
            Directory.CreateDirectory(folder);

            var lastCompleted =
                typeof(MainForm).GetField(
                    "_lastCompletedAssetFolderPath",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            lastCompleted?.SetValue(form, folder);

            string? opened = null;
            MainForm.OpenFolderProvider = path => opened = path;

            try
            {
                var keyDown =
                    typeof(MainForm).GetMethod(
                        "MainForm_KeyDown",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var ctrlO = new KeyEventArgs(Keys.Control | Keys.O);
                keyDown!.Invoke(form, new object[] { form, ctrlO });

                Assert.Equal(folder, opened);
            }
            finally
            {
                MainForm.OpenFolderProvider = null;
            }
        });
    }

    [Fact]
    public void MainForm_SaveSettingsFailureShowsError()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            // Settings path parent is a file -> save fails.
            var blocker =
                Path.Combine(
                    workspace.Root,
                    "settings-blocker");

            File.WriteAllText(blocker, "x");

            var brokenSettings =
                new SettingsService(
                    Path.Combine(
                        blocker,
                        AppConstants.SettingsFileName));

            using var form = new MainForm(
                workspace.CreateSettings(),
                brokenSettings,
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption == "Error";
                };

            try
            {
                var save =
                    typeof(MainForm).GetMethod(
                        "SaveSettingsSafe",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                save!.Invoke(form, null);

                Assert.True(errorShown);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    // ---------- Reference workflow failure paths ----------

    [Fact]
    public void ReferenceWorkflow_FailureRollsBackAndShowsError()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            using var form = CreateForm(workspace, settings);

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_ref_fail";

            form.SetSelectedImage(ImageSlot.Reference, refImage);

            // Inject a failure during the reference copy.
            AssetProcessorService.OnFileCopiedHook =
                (_, _) => throw new IOException("injected reference failure");

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption == "Error";
                };

            try
            {
                var handleReference =
                    typeof(MainForm).GetMethod(
                        "HandleReference",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                handleReference!.Invoke(form, null);

                Assert.True(errorShown);

                // Rolled back: no session, no folder.
                var session =
                    typeof(MainForm).GetField(
                        "_currentSession",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form);

                Assert.Null(session);
                Assert.False(
                    Directory.Exists(
                        Path.Combine(
                            settings.AssetRootFolder,
                            "asset_ref_fail")));
            }
            finally
            {
                AssetProcessorService.OnFileCopiedHook = null;
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void Cancel_InvalidSessionShowsValidationError()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();
            var refImage = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateReferenceSession(
                settings,
                "asset_cancel_invalid",
                refImage,
                DateTimeOffset.Now);

            processor.ProcessReference(session, settings, refImage, session.ReferenceProcessedAt);

            using var form = CreateForm(workspace, settings);

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

            // Corrupt the reference file so session validation fails.
            File.WriteAllBytes(
                session.ReferenceDestinationPath,
                new byte[] { 9, 9, 9 });

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption.Contains(
                            "inconsistent",
                            StringComparison.OrdinalIgnoreCase);
                };

            try
            {
                var handleCancel =
                    typeof(MainForm).GetMethod(
                        "HandleCancel",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                handleCancel!.Invoke(form, null);

                Assert.True(errorShown);
                Assert.True(File.Exists(session.ReferenceDestinationPath));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void MainWorkflow_NoReferenceDeleteFailureRetries()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var mainSource = workspace.CreateImage("main.png", new byte[] { 1, 2, 3 });

            var session = processor.CreateNoReferenceMainSession(
                settings,
                "asset_nr_del",
                mainSource,
                "prompt",
                DateTimeOffset.Now);

            // Lock session.json so the post-commit Delete fails once.
            using var handle =
                new FileStream(
                    workspace.SessionPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);

            var sessionService = workspace.CreateSessionService();

            using var form = CreateForm(workspace, settings);

            var sessionField =
                typeof(MainForm).GetField(
                    "_currentSession",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            sessionField?.SetValue(form, session);

            var txtPrompt =
                FindControl<TextBox>(form, "txtPrompt");

            txtPrompt.Text = "prompt";

            form.SetSelectedImage(ImageSlot.Main, mainSource);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            try
            {
                var execute =
                    typeof(MainForm).GetMethod(
                        "ExecuteMainCommit",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                execute!.Invoke(
                    form,
                    new object[]
                    {
                        session,
                        mainSource,
                        "prompt",
                        DateTimeOffset.Now
                    });

                // NoReference delete failure -> rollback and delete retry.
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
    public void HelpOverlayControl_OwnKeyDownEscape_HidesAndSuppressesKeyPress()
    {
        RunOnSta(() =>
        {
            using var overlay = new HelpOverlayControl();
            var closeRequested = false;
            overlay.CloseRequested += (_, _) => closeRequested = true;

            overlay.ShowOverlay();
            Assert.True(overlay.Visible);

            var onKeyDown =
                typeof(Control).GetMethod(
                    "OnKeyDown",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var escape = new KeyEventArgs(Keys.Escape);
            onKeyDown!.Invoke(overlay, new object[] { escape });

            Assert.False(overlay.Visible);
            Assert.True(escape.SuppressKeyPress);
            Assert.True(closeRequested);
        });
    }

    [Fact]
    public void HelpOverlayControl_OwnKeyDownOtherKey_LeavesOverlayOpen()
    {
        RunOnSta(() =>
        {
            using var overlay = new HelpOverlayControl();
            var closeRequested = false;
            overlay.CloseRequested += (_, _) => closeRequested = true;

            overlay.ShowOverlay();
            Assert.True(overlay.Visible);

            var onKeyDown =
                typeof(Control).GetMethod(
                    "OnKeyDown",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            var other = new KeyEventArgs(Keys.A);
            onKeyDown!.Invoke(overlay, new object[] { other });

            Assert.True(overlay.Visible);
            Assert.False(other.SuppressKeyPress);
            Assert.False(closeRequested);
        });
    }
}
// [build marker]


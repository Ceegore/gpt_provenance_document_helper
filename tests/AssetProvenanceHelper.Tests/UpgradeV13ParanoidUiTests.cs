#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Paranoid UI-level verification driving the REAL event handlers and
/// controls (not just reflection on private state). Everything here verifies
/// behavior a real user would trigger.
/// </summary>
public class UpgradeV13ParanoidUiTests
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

    private static T FindControl<T>(MainForm form, string name)
        where T : Control
    {
        var control =
            form.Controls.Find(name, true)
                .FirstOrDefault();

        Assert.NotNull(control);
        return Assert.IsType<T>(control);
    }

    private static MainForm CreateProductionForm(
        TestWorkspace workspace,
        AppSettings? settings = null,
        RecentDocumentHistoryService? history = null,
        RequestProgressService? progress = null)
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
            history ?? workspace.CreateRecentDocumentHistoryService(),
            progress ?? workspace.CreateRequestProgressService());
    }

    private static string WriteManifest(
        TestWorkspace workspace,
        string json,
        string fileName = "manifest.json")
    {
        var path =
            Path.Combine(
                workspace.Root,
                fileName);

        File.WriteAllText(path, json);
        return path;
    }

    // ==================== Prompt preview ====================

    [Fact]
    public void PromptPreview_DoesNotCreateHoverOverlay()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            form.Show();

            FindControl<TextBox>(form, "txtPrompt").Text = new string('x', 500);
            var preview = FindControl<Label>(form, "lblPromptPreview");

            var onMouseEnter = typeof(Label).GetMethod(
                "OnMouseEnter", BindingFlags.NonPublic | BindingFlags.Instance);
            onMouseEnter!.Invoke(preview, new object[] { EventArgs.Empty });

            Assert.Empty(form.Controls.Find("promptOverlay", true));
            Assert.Null(typeof(MainForm).GetMethod(
                "ShowPromptOverlay", BindingFlags.NonPublic | BindingFlags.Instance));
        });
    }

    // ==================== Direct mode branches ====================

    [Fact]
    public void DirectNoReference_InvalidDownloadFolderFailsBeforeSelection()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            form.Show();

            var txtDownload =
                FindControl<TextBox>(form, "txtDownloadFolder");

            txtDownload.Text =
                Path.Combine(
                    workspace.Root,
                    "does-not-exist");

            var chkNoReference =
                FindControl<CheckBox>(form, "chkNoReference");

            var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkNoReference.Checked = true;
            chkDirect.Checked = true;

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, _, _, _) => errorShown = true;

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                entry!.Invoke(form, null);

                Assert.True(errorShown);
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));

                var host =
                    typeof(MainForm).GetField(
                        "pnlDownloadFolderHost",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as Panel;

                Assert.NotNull(host);
                Assert.Equal(UiTheme.Error, host!.BackColor);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void DirectNoReference_EmptyDownloadFolderShowsMessage()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            form.Show();

            var chkNoReference =
                FindControl<CheckBox>(form, "chkNoReference");

            var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkNoReference.Checked = true;
            chkDirect.Checked = true;

            var messageShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    messageShown =
                        caption == "No Main image found";
                };

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                entry!.Invoke(form, null);

                Assert.True(messageShown);
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void DirectNoReference_InvalidLatestImageRejected()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            // A .png file with wrong magic bytes: invalid image.
            File.WriteAllBytes(
                Path.Combine(
                    workspace.Downloads,
                    "bad.png"),
                new byte[] { 0x00, 0x11, 0x22, 0x33 });

            using var form = CreateProductionForm(workspace);

            form.Show();

            var chkNoReference =
                FindControl<CheckBox>(form, "chkNoReference");

            var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkNoReference.Checked = true;
            chkDirect.Checked = true;

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption == "Latest image is invalid.";
                };

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                entry!.Invoke(form, null);

                Assert.True(errorShown);
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void DirectReference_InvalidMainCandidateRejected()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var goodRef =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            File.SetLastWriteTimeUtc(
                goodRef,
                DateTime.UtcNow.AddMinutes(-2));

            var badMain =
                Path.Combine(
                    workspace.Downloads,
                    "main.png");

            File.WriteAllBytes(
                badMain,
                new byte[] { 0x00, 0x01, 0x02, 0x03 });

            File.SetLastWriteTimeUtc(
                badMain,
                DateTime.UtcNow);

            using var form = CreateProductionForm(workspace);

            form.Show();

            var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkDirect.Checked = true;

            var errorShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    errorShown =
                        caption == "Direct Main image is invalid.";
                };

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                entry!.Invoke(form, null);

                Assert.True(errorShown);
                Assert.Null(form.GetSelectedImage(ImageSlot.Reference));
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));

                // No asset folder mutation.
                Assert.False(
                    Directory.Exists(
                        Path.Combine(
                            workspace.Assets,
                            "asset")));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void DirectReference_PairOkButReferenceCancelledByUser()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            workspace.CreateImage("reference.png", new byte[] { 1 });
            workspace.CreateImage("main.png", new byte[] { 2 });

            using var form = CreateProductionForm(workspace, settings);

            form.Show();

var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkDirect.Checked = true;

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_direct_cancel";

            // Pre-create the destination so the "Existing destination"
            // confirmation dialog appears during Reference processing.
            Directory.CreateDirectory(
                Path.Combine(
                    settings.AssetRootFolder,
                    "asset_direct_cancel"));

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };

            // User cancels the "Existing destination" dialog.
            TwoChoiceDialog.CustomChoiceProvider =
                (_, _, _, _, _) => false;

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

entry!.Invoke(form, null);

                // No session, no reference artifacts inside the pre-existing
                // destination folder.
                var session =
                    typeof(MainForm).GetField(
                        "_currentSession",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form);

                Assert.Null(session);

                var targetFolder =
                    Path.Combine(
                        settings.AssetRootFolder,
                        "asset_direct_cancel");

                Assert.Empty(
                    Directory.GetFiles(
                        targetFolder,
                        "*",
                        SearchOption.AllDirectories));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void DirectReferenceReady_RetryRefreshesOnlyMain()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

var processor = workspace.CreateAssetProcessor();

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.ProcessReference(
                    settings,
                    "asset_direct_retry2",
                    refImage,
                    DateTimeOffset.Now);

            // Remove the reference file so Downloads is empty for the retry.
            File.Delete(refImage);

            using var form = CreateProductionForm(workspace, settings);

            form.Show();

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

            var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkDirect.Checked = true;

            var txtPrompt =
                FindControl<TextBox>(form, "txtPrompt");

            txtPrompt.Text = "prompt";

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_direct_retry2";

            // Empty Downloads: auto-select fails, Reference stays intact.
            var messageShown = false;

            MainForm.MessageBoxProvider =
                (_, _, caption, _, _) =>
                {
                    messageShown =
                        caption == "No Main image found";
                };

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                entry!.Invoke(form, null);

                Assert.True(messageShown);

                // Reference remains the durable session.
                var current =
                    sessionField?.GetValue(form) as AssetSession;

                Assert.NotNull(current);
                Assert.Equal(
                    session.ReferenceHash,
                    current!.ReferenceHash);

                var stateValue =
                    stateField?.GetValue(form);

                Assert.Equal(1, (int)stateValue!);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    // ==================== Request Queue guards ====================

    [Fact]
    public void Queue_ImportCancelledByDialogLeavesQueueUntouched()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            // Dialog returns null (user cancels).
            MainForm.OpenFileDialogProvider = (_, _) => null;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                Assert.Empty(lv.Items);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_ImportRejectedForManualReferenceSession()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.ProcessReference(
                    settings,
                    "asset_manual_import",
                    refImage,
                    DateTimeOffset.Now);

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "other.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace, settings);

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

            var applyState =
                typeof(MainForm).GetMethod(
                    "ApplyState",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            applyState!.Invoke(form, null);

            var rejected = false;

            MainForm.MessageBoxProvider =
                (_, text, caption, _, _) =>
                {
                    rejected =
                        caption == "Import rejected"
                        && text.Contains(
                            "not bound to a Request",
                            StringComparison.Ordinal);
                };

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                Assert.True(rejected);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                Assert.Empty(lv.Items);

                // Import button must be disabled for a manual session.
                var btnImport =
                    FindControl<Button>(form, "btnImportRequest");

                Assert.False(btnImport.Enabled);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_ImportRejectedWhenRecoveredKeyAbsentFromManifest()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "expected.webp",
                    "1920x1080",
                    "expected prompt");

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.CreateReferenceSession(
                    settings,
                    "asset_recovered_key",
                    refImage,
                    DateTimeOffset.Now,
                    providerTemplate: null,
                    sourceRequestKey: requestKey);

            processor.ProcessReference(
                session,
                settings,
                refImage,
                session.ReferenceProcessedAt);

            // Manifest does NOT contain the session's Request key.
            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "different.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace, settings);

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

            var rejected = false;

            MainForm.MessageBoxProvider =
                (_, text, caption, _, _) =>
                {
                    rejected =
                        caption == "Import rejected"
                        && text.Contains(
                            "not present in this manifest",
                            StringComparison.Ordinal);
                };

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                Assert.True(rejected);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                Assert.Empty(lv.Items);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_CorruptProgressDoesNotBreakImport()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.WriteAllText(
                workspace.RequestProgressPath,
                "{ corrupt !!!");

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "ok.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                Assert.Single(lv.Items);
                Assert.Equal("Pending", lv.Items[0].SubItems[0].Text);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_MouseUpOnEmptyAreaDoesNothing()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            // This test is about mouse-hit-testing on the queue list, not
            // clipboard behavior, but activating a real row (below) falls
            // through to HandleRequestQueueItemActivate -> TryCopyPromptToClipboard,
            // which writes the real Windows clipboard unless this instance
            // seam is installed - clobbering whatever the developer running
            // the suite had copied.
            form.ClipboardWriter = _ => { };

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                var mouseUp =
                    typeof(MainForm).GetMethod(
                        "HandleRequestQueueMouseUp",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                // Click far outside any row.
                mouseUp!.Invoke(
                    form,
                    new object[]
                    {
                        new MouseEventArgs(
                            MouseButtons.Left,
                            1,
                            lv.Width + 200,
                            lv.Height + 200,
                            0)
                    });

var activeRequest =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as AssetRequestItem;

                Assert.Null(activeRequest);

                // Real click on the row activates it.
                var rowBounds =
                    lv.GetItemRect(0);

                mouseUp.Invoke(
                    form,
                    new object[]
                    {
                        new MouseEventArgs(
                            MouseButtons.Left,
                            1,
                            rowBounds.Left + 10,
                            rowBounds.Top + 5,
                            0)
                    });

                activeRequest =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as AssetRequestItem;

                Assert.NotNull(activeRequest);
                Assert.Equal("a", activeRequest!.AssetName);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_CompletionWithUnknownKeyIsNoop()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                var complete =
                    typeof(MainForm).GetMethod(
                        "CompleteActiveRequestAfterMainCommit",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                complete!.Invoke(
                    form,
                    new object[]
                    {
                        new AssetSession
                        {
                            SourceRequestKey =
                                new string('b', 64)
                        }
                    });

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                Assert.Equal("Pending", lv.Items[0].SubItems[0].Text);

                // Progress file must not exist (nothing completed).
                Assert.False(File.Exists(workspace.RequestProgressPath));
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_ProgressSaveFailureDoesNotBreakCompletion()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            // Progress path parent is a file -> saves fail.
            var blocker =
                Path.Combine(
                    workspace.Root,
                    "progress-blocker");

            File.WriteAllText(blocker, "x");

            var brokenProgress =
                new RequestProgressService(
                    Path.Combine(
                        blocker,
                        AppConstants.RequestProgressFileName));

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p" }
                      ]
                    }
                    """);

            using var form =
                CreateProductionForm(
                    workspace,
                    progress: brokenProgress);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            // This test is about a broken progress-persistence path not
            // breaking completion, not clipboard behavior, but activation
            // (below) always tries to copy the prompt to the clipboard -
            // seam it so the real OS clipboard is never touched.
            form.ClipboardWriter = _ => { };

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                var activate =
                    typeof(MainForm).GetMethod(
                        "HandleRequestQueueItemActivate",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                activate!.Invoke(form, new object[] { lv.Items[0] });

                var complete =
                    typeof(MainForm).GetMethod(
                        "CompleteActiveRequestAfterMainCommit",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                complete!.Invoke(
                    form,
                    new object[]
                    {
                        new AssetSession { }
                    });

                // Completion succeeded visually despite the broken store.
                Assert.Equal("Done", lv.Items[0].SubItems[0].Text);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_ImportDisabledAfterRecoveredSessionRebind()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "asset_rebind.webp",
                    "1920x1080",
                    "rebind prompt");

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.CreateReferenceSession(
                    settings,
                    "asset_rebind",
                    refImage,
                    DateTimeOffset.Now,
                    providerTemplate: null,
                    sourceRequestKey: requestKey);

            processor.ProcessReference(
                session,
                settings,
                refImage,
                session.ReferenceProcessedAt);

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "asset_rebind.webp", "resolution": "1920x1080", "prompt": "rebind prompt" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace, settings);

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

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                // Queue loaded while ReferenceReady: import must now be disabled.
                var btnImport =
                    FindControl<Button>(form, "btnImportRequest");

                Assert.False(btnImport.Enabled);

                // Binding applied.
                var activeRequest =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as AssetRequestItem;

                Assert.NotNull(activeRequest);
                Assert.Equal(requestKey, activeRequest!.RequestKey);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void Queue_BindingSurvivesUnchangedReentry()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "p1" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            form.ClipboardWriter = _ => { };
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                var activate =
                    typeof(MainForm).GetMethod(
                        "HandleRequestQueueItemActivate",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                activate!.Invoke(form, new object[] { lv.Items[0] });

                // Re-entering the identical values must not invalidate.
                var txtPrompt =
                    FindControl<TextBox>(form, "txtPrompt");

                txtPrompt.Text = "p1";

                var activeRequest =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form);

                Assert.NotNull(activeRequest);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact(Skip =
        "Deliberately exercises the real Windows clipboard fallback " +
        "(no ClipboardWriter installed) to prove TryCopyPromptToClipboard's " +
        "no-seam path reaches the actual OS API. That is exactly why it " +
        "cannot run as part of the ordinary suite: it clobbers whatever the " +
        "developer/CI runner had on the clipboard, and its finally-block " +
        "Clipboard.Clear() does not restore the original content. Run it " +
        "manually (remove Skip) when specifically verifying this fallback; " +
        "do not re-enable it for Debug/Release/20x/RecoveryCritical runs.")]
    public void Queue_RealClipboardWriteWhenNoWriterHook()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "a.png", "resolution": "10x10", "prompt": "clipboard prompt" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            // No ClipboardWriter hook: real WinForms clipboard is used.
            Assert.Null(form.ClipboardWriter);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                var activate =
                    typeof(MainForm).GetMethod(
                        "HandleRequestQueueItemActivate",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                activate!.Invoke(form, new object[] { lv.Items[0] });

                Assert.True(Clipboard.ContainsText());
                Assert.Equal("clipboard prompt", Clipboard.GetText());
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
                Clipboard.Clear();
            }
        });
    }

    [Fact]
    public void Queue_DoneRowInManifestIsNotReboundOnImport()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "asset_done_rebind.webp",
                    "1920x1080",
                    "done prompt");

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.CreateReferenceSession(
                    settings,
                    "asset_done_rebind",
                    refImage,
                    DateTimeOffset.Now,
                    providerTemplate: null,
                    sourceRequestKey: requestKey);

            processor.ProcessReference(
                session,
                settings,
                refImage,
                session.ReferenceProcessedAt);

            // Progress already marks the Request as Done.
            var progress =
                workspace.CreateRequestProgressService();

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "asset_done_rebind.webp", "resolution": "1920x1080", "prompt": "done prompt" }
                      ]
                    }
                    """);

            var manifest =
                new AssetRequestManifestService(
                        workspace.CreateValidationService())
                    .Load(
                        manifestPath,
                        settings.AcceptedExtensions);

            progress.Save(
                manifest.ManifestFingerprint,
                new[] { requestKey });

            using var form = CreateProductionForm(
                workspace,
                settings,
                progress: progress);

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

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var import =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                import!.Invoke(form, null);

                // Done row restored; the recovered session is NOT rebound.
                var activeRequest =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form);

                Assert.Null(activeRequest);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                Assert.Equal("Done", lv.Items[0].SubItems[0].Text);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    // ==================== Provider selection ====================

    [Fact]
    public void Provider_SettingsFallbackToChatGptWhenMissing()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var settings =
                workspace.CreateSettings();

            settings.SelectedProviderTemplateFileName =
                "DoesNotExist.md";

            using var form = CreateProductionForm(workspace, settings);

            var cmb =
                FindControl<ComboBox>(form, "cmbProvider");

            Assert.Single(cmb.Items);
            Assert.Equal("ChatGPT", cmb.Items[0]!.ToString());

            var settingsField =
                typeof(MainForm).GetField(
                    "_settings",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form) as AppSettings;

            Assert.NotNull(settingsField);
            Assert.Equal(
                AppConstants.DefaultProviderTemplateFileName,
                settingsField!.SelectedProviderTemplateFileName);
        });
    }

    [Fact]
    public void Provider_OneInvalidTemplateShowsSingleWarning()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.WriteAllText(
                Path.Combine(
                    workspace.ProviderTemplates,
                    "Broken.md"),
                "no tags here");

            using var form = CreateProductionForm(workspace);
            form.Show();

            var warning =
                FindControl<Label>(form, "lblProviderWarning");

            Assert.True(warning.Visible);
            Assert.Equal("1 template ignored", warning.Text);
        });
    }

    [Fact]
    public void Provider_MultipleInvalidTemplatesShowCount()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.WriteAllText(
                Path.Combine(
                    workspace.ProviderTemplates,
                    "Broken1.md"),
                "no tags here");

            File.WriteAllText(
                Path.Combine(
                    workspace.ProviderTemplates,
                    "Broken2.md"),
                "also no tags");

            using var form = CreateProductionForm(workspace);
            form.Show();

            var warning =
                FindControl<Label>(form, "lblProviderWarning");

            Assert.Equal("2 templates ignored", warning.Text);
        });
    }

    [Fact]
    public void Provider_RecoveredMatchingSnapshotSelectsCatalogEntry()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var catalog =
                workspace.CreateProviderTemplateCatalogService()
                    .Load();

            var provider =
                catalog.Templates.Single();

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.CreateReferenceSession(
                    settings,
                    "asset_match_provider",
                    refImage,
                    DateTimeOffset.Now,
                    provider.CreateSnapshot());

            processor.ProcessReference(
                session,
                settings,
                refImage,
                session.ReferenceProcessedAt);

            using var form = CreateProductionForm(workspace, settings);

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

            var bind =
                typeof(MainForm).GetMethod(
                    "BindRecoveredSessionProvider",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            bind!.Invoke(form, null);

            var cmb =
                FindControl<ComboBox>(form, "cmbProvider");

            // Matches the catalog template: no "(session snapshot)" entry.
            Assert.Single(cmb.Items);
            Assert.Equal("ChatGPT", cmb.Items[0]!.ToString());

            var snapshotProviders =
                typeof(MainForm).GetField(
                    "_sessionSnapshotProviders",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form) as List<ProviderTemplateDefinition>;

            Assert.Empty(snapshotProviders!);
        });
    }

    // ==================== Recent documents UI ====================

    [Fact]
    public void RecentDocs_CorruptHistoryDoesNotBlockStartup()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.WriteAllText(
                workspace.RecentDocumentsPath,
                "{ corrupt !!!");

            using var form = CreateProductionForm(workspace);

            var lv =
                FindControl<ListView>(form, "lvRecentDocuments");

            Assert.Empty(lv.Items);
        });
    }

    [Fact]
    public void RecentDocs_TooltipShowsFullPathOnHover()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var service =
                workspace.CreateRecentDocumentHistoryService();

            var entryPath =
                Path.Combine(
                    workspace.Assets,
                    "asset_a",
                    AppConstants.FinalProvenanceFileName);

            service.Record(
                new RecentDocumentEntry
                {
                    Path = entryPath,
                    AssetName = "asset_a",
                    Kind = ProvenanceDocumentKind.Final,
                    RecordedAt = new DateTimeOffset(
                        2026,
                        8,
                        27,
                        10,
                        30,
                        0,
                        TimeSpan.Zero)
                });

            var refresh =
                typeof(MainForm).GetMethod(
                    "RefreshRecentDocumentsUi",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            refresh!.Invoke(form, new object[] { service.Load() });

            var lv =
                FindControl<ListView>(form, "lvRecentDocuments");

            var row =
                lv.GetItemRect(0);

            var updateTooltip =
                typeof(MainForm).GetMethod(
                    "UpdateRecentDocumentTooltip",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            updateTooltip!.Invoke(
                form,
                new object[]
                {
                    new MouseEventArgs(
                        MouseButtons.None,
                        0,
                        row.Left + 10,
                        row.Top + 5,
                        0)
                });

            var toolTipField =
                typeof(MainForm).GetField(
                    "_toolTip",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(form) as ToolTip;

            Assert.NotNull(toolTipField);

            var tooltipText =
                toolTipField!.GetToolTip(lv);

            Assert.Equal(entryPath, tooltipText);
        });
    }
}

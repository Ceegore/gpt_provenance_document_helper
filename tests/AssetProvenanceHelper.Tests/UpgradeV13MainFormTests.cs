#nullable enable
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13MainFormTests
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error != null)
        {
            throw new AggregateException(error);
        }
    }

    private static MainForm CreateProductionForm(
        TestWorkspace workspace,
        AppSettings? settings = null)
    {
        var form = new MainForm(
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

        return form;
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

    [Fact]
    public void ProductionFormHasProviderDropdownWithChatGpt()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var cmb = FindControl<ComboBox>(form, "cmbProvider");
            Assert.Single(cmb.Items);
            Assert.Equal("ChatGPT", cmb.Items[0]!.ToString());
        });
    }

    [Fact]
    public void ProviderDropdownLockedInReferenceReady()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var processor = workspace.CreateAssetProcessor();
            var session = processor.ProcessReference(settings, "asset_provider_lock", ref1, DateTimeOffset.Now);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            var applyState = typeof(MainForm).GetMethod("ApplyState", BindingFlags.NonPublic | BindingFlags.Instance);
            applyState?.Invoke(form, null);

            var cmb = FindControl<ComboBox>(form, "cmbProvider");
            Assert.False(cmb.Enabled);
        });
    }

    [Fact]
    public void ImportPopulatesQueueInManifestOrder()
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
                        { "filename": "zeta.png", "resolution": "10x10", "prompt": "p1" },
                        { "filename": "alpha.png", "resolution": "20x20", "prompt": "p2" },
                        { "filename": "mid.png", "resolution": "30x30", "prompt": "p3" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                Assert.Equal(3, lv.Items.Count);
                Assert.Equal("Pending", lv.Items[0].SubItems[0].Text);
                Assert.Equal("zeta", lv.Items[0].SubItems[1].Text);
                Assert.Equal("alpha", lv.Items[1].SubItems[1].Text);
                Assert.Equal("mid", lv.Items[2].SubItems[1].Text);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void FailedImportLeavesPreviousQueueUntouched()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var goodManifest =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "good.png", "resolution": "10x10", "prompt": "p1" }
                      ]
                    }
                    """,
                    "good.json");

            var badManifest =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "good.png", "resolution": "10x10", "prompt": "p1" },
                        { "filename": "bad.txt", "resolution": "10x10", "prompt": "p2" }
                      ]
                    }
                    """,
                    "bad.json");

            using var form = CreateProductionForm(workspace);

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.OpenFileDialogProvider = (_, _) => goodManifest;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                Assert.Single(lv.Items);

                MainForm.OpenFileDialogProvider = (_, _) => badManifest;
                importMethod?.Invoke(form, null);

                // Queue must remain the good one.
                Assert.Single(lv.Items);
                Assert.Equal("good", lv.Items[0].SubItems[1].Text);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ClickPendingPopulatesFieldsAndCopiesClipboard()
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
                        { "filename": "asset_ui.webp", "resolution": "1920x1080", "prompt": "exact prompt text" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            string? copied = null;
            form.ClipboardWriter = prompt => copied = prompt;

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                var activateMethod = typeof(MainForm).GetMethod("HandleRequestQueueItemActivate", BindingFlags.NonPublic | BindingFlags.Instance);
                activateMethod?.Invoke(form, new object[] { lv.Items[0] });

                var txtAssetName = FindControl<TextBox>(form, "txtAssetFolderName");
                var txtPrompt = FindControl<TextBox>(form, "txtPrompt");

                Assert.Equal("asset_ui", txtAssetName.Text);
                Assert.Equal("exact prompt text", txtPrompt.Text);
                Assert.Equal("exact prompt text", copied);

                var activeRequest = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as AssetRequestItem;
                Assert.NotNull(activeRequest);
                Assert.Equal("asset_ui", activeRequest.AssetName);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ClipboardFailureRetainsActiveRequest()
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
                        { "filename": "asset_ui.webp", "resolution": "1920x1080", "prompt": "exact prompt text" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            var messageShown = false;
            MainForm.MessageBoxProvider = (_, text, _, _, _) =>
            {
                messageShown = text.Contains("could not be copied", StringComparison.OrdinalIgnoreCase);
            };

            form.ClipboardWriter = _ => throw new InvalidOperationException("clipboard blocked");

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                var activateMethod = typeof(MainForm).GetMethod("HandleRequestQueueItemActivate", BindingFlags.NonPublic | BindingFlags.Instance);
                activateMethod?.Invoke(form, new object[] { lv.Items[0] });

                Assert.True(messageShown);

                var activeRequest = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as AssetRequestItem;
                Assert.NotNull(activeRequest);

                var txtPrompt = FindControl<TextBox>(form, "txtPrompt");
                Assert.Equal("exact prompt text", txtPrompt.Text);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ClickDoneDoesNotModifyFields()
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
                        { "filename": "done_asset.png", "resolution": "10x10", "prompt": "p1" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            var copiedCount = 0;
            form.ClipboardWriter = _ => copiedCount++;

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                // Activate pending, then simulate Main completion.
                var lv = FindControl<ListView>(form, "lvRequestQueue");
                var activateMethod = typeof(MainForm).GetMethod("HandleRequestQueueItemActivate", BindingFlags.NonPublic | BindingFlags.Instance);
                activateMethod?.Invoke(form, new object[] { lv.Items[0] });

                var activeRequest = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as AssetRequestItem;

                var manifestField = typeof(MainForm).GetField("_currentManifest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as AssetRequestManifest;

                var item = manifestField!.Items.Single();
                var completeMethod = typeof(MainForm).GetMethod("CompleteActiveRequestAfterMainCommit", BindingFlags.NonPublic | BindingFlags.Instance);
                completeMethod?.Invoke(form, new object[] { new AssetSession { SourceRequestKey = item.RequestKey } });

                Assert.True(item.IsCompleted);

                var txtPrompt = FindControl<TextBox>(form, "txtPrompt");
                txtPrompt.Text = "manually set";

                var txtAssetName = FindControl<TextBox>(form, "txtAssetFolderName");
                txtAssetName.Text = "manually set name";

                // Clicking the Done row must not modify anything or copy.
                activateMethod?.Invoke(form, new object[] { lv.Items[0] });

                Assert.Equal("manually set", txtPrompt.Text);
                Assert.Equal("manually set name", txtAssetName.Text);
                Assert.Equal(1, copiedCount);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void EditingPromptInvalidatesActiveRequest()
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
                        { "filename": "asset_ui.webp", "resolution": "1920x1080", "prompt": "original prompt" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            form.ClipboardWriter = _ => { };

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                var activateMethod = typeof(MainForm).GetMethod("HandleRequestQueueItemActivate", BindingFlags.NonPublic | BindingFlags.Instance);
                activateMethod?.Invoke(form, new object[] { lv.Items[0] });

                var txtPrompt = FindControl<TextBox>(form, "txtPrompt");
                txtPrompt.Text = "edited prompt";

                var activeRequest = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);
                Assert.Null(activeRequest);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void EditingAssetNameInvalidatesActiveRequest()
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
                        { "filename": "asset_ui.webp", "resolution": "1920x1080", "prompt": "original prompt" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);

            form.ClipboardWriter = _ => { };

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                var activateMethod = typeof(MainForm).GetMethod("HandleRequestQueueItemActivate", BindingFlags.NonPublic | BindingFlags.Instance);
                activateMethod?.Invoke(form, new object[] { lv.Items[0] });

                var txtAssetName = FindControl<TextBox>(form, "txtAssetFolderName");
                txtAssetName.Text = "edited_name";

                var activeRequest = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);
                Assert.Null(activeRequest);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ReferenceReadyBlocksUnrelatedRequest()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var processor = workspace.CreateAssetProcessor();
            var session = processor.ProcessReference(settings, "asset_blocked", ref1, DateTimeOffset.Now);

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "other.png", "resolution": "10x10", "prompt": "p1" }
                      ]
                    }
                    """);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            var warned = false;
            MainForm.MessageBoxProvider = (_, text, caption, _, _) =>
            {
                warned = caption == "Import rejected"
                    && text.Contains("not bound to a Request", StringComparison.Ordinal);
            };

            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                // A manual Reference session must not accept an import at all;
                // the queue remains untouched and no Request can be activated.
                Assert.True(warned);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                Assert.Empty(lv.Items);

                var activeRequest = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);
                Assert.Null(activeRequest);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ReferenceReadyPermitsItsOwnSourceRequestKey()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "asset_own.webp",
                    "1920x1080",
                    "own prompt");

            var session = processor.CreateReferenceSession(
                settings,
                "asset_own",
                ref1,
                DateTimeOffset.Now,
                providerTemplate: null,
                sourceRequestKey: requestKey);

            processor.ProcessReference(session, settings, ref1, session.ReferenceProcessedAt);

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "asset_own.webp", "resolution": "1920x1080", "prompt": "own prompt" }
                      ]
                    }
                    """);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.ClipboardWriter = _ => { };
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                // Recovered-session binding must activate the matching row.
                var activeRequest = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as AssetRequestItem;
                Assert.NotNull(activeRequest);
                Assert.Equal(requestKey, activeRequest.RequestKey);

                var txtPrompt = FindControl<TextBox>(form, "txtPrompt");
                Assert.Equal("own prompt", txtPrompt.Text);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void MainSuccessMarksMatchingRequestDone()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "asset_done.webp",
                    "1920x1080",
                    "done prompt");

            var session = processor.CreateReferenceSession(
                settings,
                "asset_done",
                ref1,
                DateTimeOffset.Now,
                providerTemplate: null,
                sourceRequestKey: requestKey);

            processor.ProcessReference(session, settings, ref1, session.ReferenceProcessedAt);

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "asset_done.webp", "resolution": "1920x1080", "prompt": "done prompt" }
                      ]
                    }
                    """);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            form.ClipboardWriter = _ => { };
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                var completeMethod = typeof(MainForm).GetMethod("CompleteActiveRequestAfterMainCommit", BindingFlags.NonPublic | BindingFlags.Instance);
                completeMethod?.Invoke(form, new object[] { session });

                var manifestField = typeof(MainForm).GetField("_currentManifest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as AssetRequestManifest;
                Assert.True(manifestField!.Items.Single().IsCompleted);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                Assert.Equal("Done", lv.Items[0].SubItems[0].Text);

                // Progress must be persisted.
                var progress = workspace.CreateRequestProgressService();
                var restored = progress.LoadForManifest(manifestField.ManifestFingerprint);
                Assert.Contains(requestKey, restored);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ManualAssetWithoutRequestDoesNotAffectQueue()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var processor = workspace.CreateAssetProcessor();
            var session = processor.ProcessReference(settings, "asset_manual", ref1, DateTimeOffset.Now);

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "other.png", "resolution": "10x10", "prompt": "p1" }
                      ]
                    }
                    """);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            // Import happens while idle, before any session exists.
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                // The user then processes a MANUAL asset (no active Request).
                var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
                var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
                sessionField?.SetValue(form, session);
                stateField?.SetValue(form, 1);

                // Manual session (no SourceRequestKey, no active Request):
                // completing it must not mark anything Done.
                var completeMethod = typeof(MainForm).GetMethod("CompleteActiveRequestAfterMainCommit", BindingFlags.NonPublic | BindingFlags.Instance);
                completeMethod?.Invoke(form, new object[] { session });

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                Assert.Equal("Pending", lv.Items[0].SubItems[0].Text);

                // No progress file may exist for an untouched queue.
                Assert.False(File.Exists(workspace.RequestProgressPath));
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void NoValidProvidersBlocksNewAssetsButNotRecoveredSession()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            // Remove the only valid provider template.
            File.Delete(workspace.ChatGptProviderTemplatePath);

            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var processor = workspace.CreateAssetProcessor();
            var session = processor.ProcessReference(settings, "asset_recovered", ref1, DateTimeOffset.Now);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            form.Show();

            // Idle: new-asset CTAs disabled because no Provider is available.
            var btnReference = FindControl<Button>(form, "btnReference");
            var btnMainImage = FindControl<Button>(form, "btnMainImage");
            Assert.False(btnReference.Enabled);
            Assert.False(btnMainImage.Enabled);

            var lblWarning = FindControl<Label>(form, "lblProviderWarning");
            Assert.True(lblWarning.Visible);

            // Recovered schema-2 ReferenceReady session: Main must be enabled.
            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            var applyState = typeof(MainForm).GetMethod("ApplyState", BindingFlags.NonPublic | BindingFlags.Instance);
            applyState?.Invoke(form, null);

            Assert.True(btnMainImage.Enabled);
        });
    }

    [Fact]
    public void RecoveredProviderSessionFinishesWithoutProviderFile()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            var processor = workspace.CreateAssetProcessor();
            var settings = workspace.CreateSettings();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var catalog = workspace.CreateProviderTemplateCatalogService().Load();
            var provider = catalog.Templates.Single();

            var session = processor.CreateReferenceSession(
                settings,
                "asset_recovered_v13",
                ref1,
                DateTimeOffset.Now,
                provider.CreateSnapshot());

            processor.ProcessReference(session, settings, ref1, session.ReferenceProcessedAt);

            // Delete the provider file after the session exists.
            File.Delete(workspace.ChatGptProviderTemplatePath);

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);
            stateField?.SetValue(form, 1);

            var bindProviderMethod = typeof(MainForm).GetMethod("BindRecoveredSessionProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            bindProviderMethod?.Invoke(form, null);

            var applyState = typeof(MainForm).GetMethod("ApplyState", BindingFlags.NonPublic | BindingFlags.Instance);
            applyState?.Invoke(form, null);

            var btnMainImage = FindControl<Button>(form, "btnMainImage");
            Assert.True(btnMainImage.Enabled);

            // Session snapshot must be shown in the dropdown (temporary entry).
            var cmb = FindControl<ComboBox>(form, "cmbProvider");
            Assert.Contains(
                cmb.Items.Cast<object>().Select(i => i.ToString()),
                text => text!.Contains("(session snapshot)", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void DirectModeDisablesRefreshButtons()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var chkDirect = FindControl<CheckBox>(form, "chkDirectMode");
            chkDirect.Checked = true;

            var btnRefreshReference = FindControl<Button>(form, "btnRefreshReference");
            var btnRefreshMain = FindControl<Button>(form, "btnRefreshMain");

            Assert.False(btnRefreshReference.Enabled);
            Assert.False(btnRefreshMain.Enabled);
        });
    }

    [Fact]
    public void DirectModeDoesNotReplaceManuallySelectedCandidateWhenOff()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var manual = workspace.CreateImage("manual_main.png", new byte[] { 1, 2, 3 });

            // Simulate a manual selection, then a newer file appears in Downloads.
            form.SetSelectedImage(ImageSlot.Main, manual);

            var newer = workspace.CreateImage("newer_main.png", new byte[] { 4, 5, 6 });

            // Direct mode OFF: the entry point must use the manual candidate.
            var entryMethod = typeof(MainForm).GetMethod("HandleMainImageEntryPoint", BindingFlags.NonPublic | BindingFlags.Instance);
            var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);

            // Track which Main image the workflow would have used by
            // observing the selected candidate after invocation. Validation
            // fails (no asset name), but the selection must not have changed.
            var selectedBefore = form.GetSelectedImage(ImageSlot.Main);

            // Direct is off by default.
            var chkDirect = FindControl<CheckBox>(form, "chkDirectMode");
            Assert.False(chkDirect.Checked);

            entryMethod?.Invoke(form, null);

            var selectedAfter = form.GetSelectedImage(ImageSlot.Main);
            Assert.Equal(selectedBefore, selectedAfter);
            Assert.Equal(Path.GetFullPath(manual), Path.GetFullPath(selectedAfter!));
        });
    }

    [Fact]
    public void DirectNoReferenceChoosesNewestImageOnMainClick()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();

            var old = workspace.CreateImage("old.png", new byte[] { 1 });
            var newest = workspace.CreateImage("newest.png", new byte[] { 2 });

            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(newest, DateTime.UtcNow);

            using var form = CreateProductionForm(workspace, settings);

            var chkNoReference = FindControl<CheckBox>(form, "chkNoReference");
            var chkDirect = FindControl<CheckBox>(form, "chkDirectMode");
            chkNoReference.Checked = true;
            chkDirect.Checked = true;

            var txtAssetName = FindControl<TextBox>(form, "txtAssetFolderName");
            txtAssetName.Text = "asset_direct_nr";
            var txtPrompt = FindControl<TextBox>(form, "txtPrompt");
            txtPrompt.Text = "prompt";

            // No image selected yet.
            Assert.Null(form.GetSelectedImage(ImageSlot.Main));

            var selectMethod = typeof(MainForm).GetMethod("TryAutoSelectLatestMain", BindingFlags.NonPublic | BindingFlags.Instance);
            var selected = (bool)(selectMethod?.Invoke(form, null) ?? false);

            Assert.True(selected);
            Assert.Equal(
                Path.GetFullPath(newest),
                Path.GetFullPath(form.GetSelectedImage(ImageSlot.Main)!));
        });
    }

    [Fact]
    public void DirectReferenceChoosesSecondNewestAsReference()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var old = workspace.CreateImage("old.png", new byte[] { 1 });
            var reference = workspace.CreateImage("reference.png", new byte[] { 2 });
            var main = workspace.CreateImage("main.png", new byte[] { 3 });

            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-30));
            File.SetLastWriteTimeUtc(reference, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(main, DateTime.UtcNow);

            using var form = CreateProductionForm(workspace, settings);

            var chkDirect = FindControl<CheckBox>(form, "chkDirectMode");
            chkDirect.Checked = true;

            var selectMethod = typeof(MainForm).GetMethod("TrySelectDirectReferencePair", BindingFlags.NonPublic | BindingFlags.Instance);
            var selected = selectMethod?.Invoke(form, new object[] { 1 }) is not null;

            Assert.True(selected);
            Assert.Equal(
                Path.GetFullPath(reference),
                Path.GetFullPath(form.GetSelectedImage(ImageSlot.Reference)!));
            Assert.Equal(
                Path.GetFullPath(main),
                Path.GetFullPath(form.GetSelectedImage(ImageSlot.Main)!));
        });
    }

    [Fact]
    public void DirectReferenceWithOneCandidateCreatesNoTransaction()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var processor = workspace.CreateAssetProcessor();

            workspace.CreateImage("only.png", new byte[] { 1 });

            using var form = CreateProductionForm(workspace, settings);

            var chkDirect = FindControl<CheckBox>(form, "chkDirectMode");
            chkDirect.Checked = true;

            var txtAssetName = FindControl<TextBox>(form, "txtAssetFolderName");
            txtAssetName.Text = "asset_direct_one";

            var sessionBefore =
                typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);

            var handled = false;
            MainForm.MessageBoxProvider = (_, text, caption, _, _) =>
            {
                handled = caption.Contains("Two images required", StringComparison.Ordinal);
            };

            try
            {
                var orchestrator = typeof(MainForm).GetMethod("HandleDirectMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
                orchestrator?.Invoke(form, null);

                Assert.True(handled);

                var sessionAfter =
                    typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);

                Assert.Null(sessionAfter);
                Assert.False(Directory.Exists(Path.Combine(settings.AssetRootFolder, "asset_direct_one")));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void CtrlRDoesNothingInDirectMode()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            using var form = CreateProductionForm(workspace, settings);

            var chkDirect = FindControl<CheckBox>(form, "chkDirectMode");
            chkDirect.Checked = true;

            var txtAssetName = FindControl<TextBox>(form, "txtAssetFolderName");
            txtAssetName.Text = "asset_hotkey";

            var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);

            var ctrlR = new KeyEventArgs(Keys.Control | Keys.R);
            keyMethod?.Invoke(form, new object[] { form, ctrlR });

            var session =
                typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);

            Assert.Null(session);
        });
    }

    [Fact]
    public void CtrlMActivatesDirectPairOrchestrator()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            workspace.CreateImage("reference.png", new byte[] { 2 });
            workspace.CreateImage("main.png", new byte[] { 3 });

            using var form = CreateProductionForm(workspace, settings);

            var chkDirect = FindControl<CheckBox>(form, "chkDirectMode");
            chkDirect.Checked = true;

            var txtAssetName = FindControl<TextBox>(form, "txtAssetFolderName");
            txtAssetName.Text = "asset_hotkey_direct";
            var txtPrompt = FindControl<TextBox>(form, "txtPrompt");
            txtPrompt.Text = "prompt";

            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            Dialogs.TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

            try
            {
                var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);

                var ctrlM = new KeyEventArgs(Keys.Control | Keys.M);
                keyMethod?.Invoke(form, new object[] { form, ctrlM });

                // The Direct pair orchestrator must complete the full asset.
                var assetFolder =
                    Path.Combine(
                        settings.AssetRootFolder,
                        "asset_hotkey_direct");

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            "reference",
                            "reference.png")));

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            "main.png")));

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            AppConstants.FinalProvenanceFileName)));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                Dialogs.TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void RecentDocumentsUiShowsOnlyDocuments()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var service = workspace.CreateRecentDocumentHistoryService();

            var now =
                new DateTimeOffset(
                    2026,
                    8,
                    26,
                    12,
                    0,
                    0,
                    TimeSpan.Zero);

            service.Record(
                new RecentDocumentEntry
                {
                    Path = Path.Combine(workspace.Assets, "a", AppConstants.FinalProvenanceFileName),
                    AssetName = "a",
                    Kind = ProvenanceDocumentKind.Final,
                    RecordedAt = now
                });

            var lv = FindControl<ListView>(form, "lvRecentDocuments");

            var refreshMethod = typeof(MainForm).GetMethod("RefreshRecentDocumentsUi", BindingFlags.NonPublic | BindingFlags.Instance);
            refreshMethod?.Invoke(form, new object[] { service.Load() });

            Assert.Single(lv.Items);
            Assert.Equal("Final", lv.Items[0].SubItems[1].Text);
            Assert.Equal("a", lv.Items[0].SubItems[2].Text);

            // Status history must not appear in recent documents.
            var txtStatus = FindControl<TextBox>(form, "txtStatusHistory");
            txtStatus.Text = "some internal status message";

            Assert.Single(lv.Items);
        });
    }

    [Fact]
    public void PromptPreviewLabelUpdatesFromPrompt()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var lbl = FindControl<Label>(form, "lblPromptPreview");
            var txtPrompt = FindControl<TextBox>(form, "txtPrompt");

            Assert.Equal("No prompt stored.", lbl.Text);

            txtPrompt.Text =
                new string('a', 150);

            Assert.Equal(
                new string('a', 100) + "...",
                lbl.Text);
        });
    }

    [Fact]
    public void FormSizeAndWorkspaceLayoutApplied()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);

            var workspaceControl = form.Controls.Find("pnlWorkspace", true).FirstOrDefault();
            Assert.NotNull(workspaceControl);

            var grpQueue = form.Controls.Find("grpRequestQueue", true).FirstOrDefault();
            Assert.NotNull(grpQueue);

            var btnImport = form.Controls.Find("btnImportRequest", true).FirstOrDefault();
            Assert.NotNull(btnImport);
        });
    }

    [Fact]
    public void RequestQueueActionsWrapAtNormalHeightAndStatusHistoryIsUsable()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            form.Show();
            form.PerformLayout();

            var importButton = FindControl<Button>(form, "btnImportRequest");
            var actionHost = Assert.IsType<FlowLayoutPanel>(importButton.Parent);
            Assert.True(actionHost.WrapContents);

            foreach (var button in actionHost.Controls.OfType<Button>())
            {
                Assert.InRange(button.Height, 20, 45);
                Assert.True(button.Right <= actionHost.ClientSize.Width);
                Assert.True(button.Bottom <= actionHost.ClientSize.Height);
            }

            var status = FindControl<GroupBox>(form, "grpStatus");
            var history = FindControl<ListView>(form, "lvRecentDocuments");
            Assert.True(status.Height >= 190);
            Assert.True(history.Height >= 120);
        });
    }

    [Fact]
    public void ReducedHeightUsesScrollingInsteadOfClippingWorkflow()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            form.Size = new Size(1100, 600);
            form.Show();
            form.PerformLayout();

            var workspaceControl = FindControl<TableLayoutPanel>(form, "pnlWorkspace");
            Assert.True(form.AutoScroll);
            Assert.True(workspaceControl.AutoScroll);
            Assert.True(workspaceControl.Height >= 880);
            Assert.True(form.VerticalScroll.Visible || workspaceControl.VerticalScroll.Visible);
        });
    }

    [Fact]
    public void VariantsLabelAndSelectorShareTheModeControlBaseline()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            using var form = CreateProductionForm(workspace);
            form.Show();
            form.PerformLayout();

            var label = FindControl<Label>(form, "lblVariants");
            var selector = FindControl<ComboBox>(form, "cmbVariants");
            Assert.InRange(
                Math.Abs(
                    (label.Top + (label.Height / 2))
                    - (selector.Top + (selector.Height / 2))),
                0,
                2);
        });
    }

    [Fact]
    public void CompleteActiveRequestAfterMainCommit_ResetsActiveRequestAndApiCandidateMetadata()
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
                        { "filename": "asset_ui.webp", "resolution": "1920x1080", "prompt": "original prompt" }
                      ]
                    }
                    """);

            using var form = CreateProductionForm(workspace);
            form.ClipboardWriter = _ => { };
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod = typeof(MainForm).GetMethod("HandleImportRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                importMethod?.Invoke(form, null);

                var lv = FindControl<ListView>(form, "lvRequestQueue");
                var activateMethod = typeof(MainForm).GetMethod("HandleRequestQueueItemActivate", BindingFlags.NonPublic | BindingFlags.Instance);
                activateMethod?.Invoke(form, new object[] { lv.Items[0] });

                var activeRequestBefore = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);
                Assert.NotNull(activeRequestBefore);

                var manifestField = typeof(MainForm).GetField("_currentManifest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form) as AssetRequestManifest;
                var item = manifestField!.Items.Single();

                var completeMethod = typeof(MainForm).GetMethod("CompleteActiveRequestAfterMainCommit", BindingFlags.NonPublic | BindingFlags.Instance);
                completeMethod?.Invoke(form, new object[] { new AssetSession { SourceRequestKey = item.RequestKey } });

                Assert.True(item.IsCompleted);

                var activeRequestAfter = typeof(MainForm).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);
                Assert.Null(activeRequestAfter);

                var activeApiMetadata = typeof(MainForm).GetField("_activeApiCandidateMetadata", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(form);
                Assert.Null(activeApiMetadata);
            }
            finally
            {
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }
}

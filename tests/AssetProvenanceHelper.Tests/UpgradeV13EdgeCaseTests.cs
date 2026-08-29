#nullable enable
using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public class UpgradeV13EdgeCaseTests
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
        string json)
    {
        var path =
            Path.Combine(
                workspace.Root,
                "manifest.json");

        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void MainFailureLeavesRequestPending()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "asset_fail.webp",
                    "1920x1080",
                    "fail prompt");

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.CreateReferenceSession(
                    settings,
                    "asset_fail",
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
                        { "filename": "asset_fail.webp", "resolution": "1920x1080", "prompt": "fail prompt" }
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

            form.ClipboardWriter = _ => { };
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                importMethod?.Invoke(form, null);

                var activeRequest =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as AssetRequestItem;

                Assert.NotNull(activeRequest);

                // Main commit fails because the Main source is byte-identical
                // to the Reference (hash equality is rejected by PrepareMainCommit).
                var identicalMain =
                    workspace.CreateImage(
                        "identical.png",
                        new byte[] { 1, 2, 3 });

                File.SetLastWriteTimeUtc(
                    identicalMain,
                    DateTime.UtcNow);

                form.SetSelectedImage(ImageSlot.Main, identicalMain);

                var txtPrompt =
                    FindControl<TextBox>(form, "txtPrompt");

                txtPrompt.Text = "fail prompt";

                var handleMain =
                    typeof(MainForm).GetMethod(
                        "HandleMainImage",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                handleMain?.Invoke(form, null);

                // Request must remain Pending and the Reference session intact.
                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                Assert.Equal("Pending", lv.Items[0].SubItems[0].Text);

                var currentSession =
                    sessionField?.GetValue(form) as AssetSession;

                Assert.NotNull(currentSession);
                Assert.Equal(
                    session.ReferenceHash,
                    currentSession.ReferenceHash);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void ReferenceSucceedsMainFailsPreservesReferenceAndRequestPending()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            workspace.CreateImage("reference.png", new byte[] { 2 });
            workspace.CreateImage("main.png", new byte[] { 3 });

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

            var chkDirect =
                FindControl<CheckBox>(form, "chkDirectMode");

            chkDirect.Checked = true;

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_direct_retry";

            var txtPrompt =
                FindControl<TextBox>(form, "txtPrompt");

            txtPrompt.Text = "prompt";

            var messages = new List<string>();

            MainForm.MessageBoxProvider =
                (_, text, caption, _, _) =>
                {
                    messages.Add(caption + ": " + text);
                };

            TwoChoiceDialog.CustomChoiceProvider =
                (_, _, _, _, _) => true;

            // Inject a failure during the Main copy stage only.
            AssetProcessorService.OnFileCopiedHook =
                (source, _) =>
                {
                    if (Path.GetFileName(source) == "main.png")
                    {
                        throw new IOException("injected main failure");
                    }
                };

            try
            {
                var entry =
                    typeof(MainForm).GetMethod(
                        "HandleMainImageEntryPoint",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                entry?.Invoke(form, null);

                var assetFolder =
                    Path.Combine(
                        settings.AssetRootFolder,
                        "asset_direct_retry");

                // Reference artifacts remain durable.
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
                            "reference",
                            AppConstants.ReferenceProvenanceFileName)));

                // Main failed: no root main image, no final provenance.
                Assert.False(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            "main.png")));

                Assert.False(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            AppConstants.FinalProvenanceFileName)));

                // Session remains in ReferenceReady state.
                var stateField =
                    typeof(MainForm).GetField(
                        "_state",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.Equal(1, (int)stateField!.GetValue(form)!);

                // Retry with a fresh valid Main: only Main is refreshed and
                // the asset completes.
                var retryMain =
                    workspace.CreateImage(
                        "retry_main.png",
                        new byte[] { 9, 9, 9 });

                File.SetLastWriteTimeUtc(
                    retryMain,
                    DateTime.UtcNow);

                AssetProcessorService.OnFileCopiedHook = null;
                messages.Clear();

                entry?.Invoke(form, null);

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            "retry_main.png")));

                Assert.True(
                    File.Exists(
                        Path.Combine(
                            assetFolder,
                            AppConstants.FinalProvenanceFileName)));
            }
            finally
            {
                AssetProcessorService.OnFileCopiedHook = null;
                MainForm.MessageBoxProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }

    [Fact]
    public void HotkeyProviderGuardBlocksNewAssetWithoutProvider()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();

            File.Delete(workspace.ChatGptProviderTemplatePath);

            var settings = workspace.CreateSettings();

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                workspace.CreateAssetProcessor(),
                workspace.CreateSessionService(),
                workspace.CreateProviderTemplateCatalogService(),
                workspace.CreateRecentDocumentHistoryService(),
                workspace.CreateRequestProgressService());

            form.SetSelectedImage(ImageSlot.Reference, refImage);

            var txtAssetName =
                FindControl<TextBox>(form, "txtAssetFolderName");

            txtAssetName.Text = "asset_no_provider";

            var warned = false;

            MainForm.MessageBoxProvider =
                (_, text, _, _, _) =>
                {
                    warned =
                        text.Contains(
                            "No valid AI Generation Provider template",
                            StringComparison.Ordinal);
                };

            try
            {
                // Ctrl+R must not create a schema-2 legacy session when the
                // production catalog is empty.
                var keyMethod =
                    typeof(MainForm).GetMethod(
                        "MainForm_KeyDown",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var ctrlR =
                    new KeyEventArgs(Keys.Control | Keys.R);

                keyMethod?.Invoke(form, new object[] { form, ctrlR });

                Assert.True(warned);

                var session =
                    typeof(MainForm).GetField(
                        "_currentSession",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form);

                Assert.Null(session);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
            }
        });
    }

    [Fact]
    public void ReplacementRestoresQueuePrompt()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "asset_replace.webp",
                    "1920x1080",
                    "replace prompt");

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.CreateReferenceSession(
                    settings,
                    "asset_replace",
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
                        { "filename": "asset_replace.webp", "resolution": "1920x1080", "prompt": "replace prompt" }
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

            form.ClipboardWriter = _ => { };
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;

            try
            {
                var importMethod =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                importMethod?.Invoke(form, null);

                var txtPrompt =
                    FindControl<TextBox>(form, "txtPrompt");

                Assert.Equal("replace prompt", txtPrompt.Text);

                // Simulate a completed replacement with a queue-bound session.
                var replacementSource =
                    workspace.CreateImage(
                        "reference2.png",
                        new byte[] { 7, 7, 7 });

                var transaction =
                    processor.CreateReferenceReplacementTransaction(
                        session,
                        settings.AcceptedExtensions,
                        replacementSource,
                        DateTimeOffset.Now);

                var replaceMethod =
                    typeof(MainForm).GetMethod(
                        "CompleteReplacementUiAfterDurableCommit",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                replaceMethod?.Invoke(form, new object[] { transaction });

                // Queue-bound: the Request Prompt must be restored.
                Assert.Equal("replace prompt", txtPrompt.Text);

                // Main candidate must be cleared.
                Assert.Null(form.GetSelectedImage(ImageSlot.Main));
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
            }
        });
    }

    [Fact]
    public void CancelUnbindsRequestAndRemovesHistory()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();

            var processor = workspace.CreateAssetProcessor();

            var requestKey =
                AssetRequestManifestService.ComputeRequestKey(
                    "asset_cancel.webp",
                    "1920x1080",
                    "cancel prompt");

            var refImage =
                workspace.CreateImage(
                    "reference.png",
                    new byte[] { 1, 2, 3 });

            var session =
                processor.CreateReferenceSession(
                    settings,
                    "asset_cancel",
                    refImage,
                    DateTimeOffset.Now,
                    providerTemplate: null,
                    sourceRequestKey: requestKey);

            processor.ProcessReference(
                session,
                settings,
                refImage,
                session.ReferenceProcessedAt);

            // Record a recent Reference document for the session first.
            var history =
                workspace.CreateRecentDocumentHistoryService();

            history.Record(
                new RecentDocumentEntry
                {
                    Path = session.ReferenceProvenancePath,
                    AssetName = session.AssetFolderName,
                    Kind = ProvenanceDocumentKind.Reference,
                    RecordedAt = DateTimeOffset.Now
                });

            var manifestPath =
                WriteManifest(
                    workspace,
                    """
                    {
                      "manifestVersion": 1,
                      "assets": [
                        { "filename": "asset_cancel.webp", "resolution": "1920x1080", "prompt": "cancel prompt" }
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
                history,
                workspace.CreateRequestProgressService());

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

            form.ClipboardWriter = _ => { };
            MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
            MainForm.OpenFileDialogProvider = (_, _) => manifestPath;
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;

            try
            {
                var importMethod =
                    typeof(MainForm).GetMethod(
                        "HandleImportRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                importMethod?.Invoke(form, null);

                var activeRequest =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form) as AssetRequestItem;

                Assert.NotNull(activeRequest);

                var cancelMethod =
                    typeof(MainForm).GetMethod(
                        "HandleCancel",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                cancelMethod?.Invoke(form, null);

                // Request unbinds and remains Pending.
                var after =
                    typeof(MainForm).GetField(
                        "_activeRequest",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(form);

                Assert.Null(after);

                var lv =
                    FindControl<ListView>(form, "lvRequestQueue");

                Assert.Equal("Pending", lv.Items[0].SubItems[0].Text);

                // Cancelled Reference document removed from history.
                var entries = history.Load();
                Assert.Empty(entries);
            }
            finally
            {
                MainForm.MessageBoxProvider = null;
                MainForm.OpenFileDialogProvider = null;
                TwoChoiceDialog.CustomChoiceProvider = null;
            }
        });
    }
}
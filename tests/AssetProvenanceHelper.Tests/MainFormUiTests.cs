using System.Reflection;
using System.Windows.Forms;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using Xunit;

namespace AssetProvenanceHelper.Tests;

[CollectionDefinition("MainFormUiCollection", DisableParallelization = true)]
public class MainFormUiTestCollection
{
}

[Collection("MainFormUiCollection")]
public class MainFormUiTests
{
    private static readonly object UiLock = new();

    private static void RunOnSta(Action action)
    {
        lock (UiLock)
        {
            Exception? ex = null;
            var thread = new Thread(() =>
            {
                try
                {
                    MainForm.MessageBoxProvider = (_, _, _, _, _) => { };
                    AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) => true;
                    action();
                }
                catch (Exception e)
                {
                    ex = e;
                }
                finally
                {
                    MainForm.MessageBoxProvider = null;
                    AssetProvenanceHelper.Dialogs.TwoChoiceDialog.CustomChoiceProvider = null;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(30)))
            {
                thread.Interrupt();
                throw new TimeoutException("STA UI thread test execution timed out after 30 seconds.");
            }
            if (ex is not null)
            {
                throw ex;
            }
        }
    }

    [Fact]
    public void MainForm_Initialization_LoadsSettingsAndSetsInitialState()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            Assert.NotNull(form);
            Assert.Equal("AI Asset Provenance Helper", form.Text);
        });
    }

    [Fact]
    public void MainForm_Controls_ReflectSettingsValues()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            var txtAssetRoot = form.Controls.Find("txtAssetRoot", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtAssetRoot);
            Assert.Equal(workspace.Assets, txtAssetRoot.Text);

            var txtProject = form.Controls.Find("txtProject", true).FirstOrDefault() as TextBox;
            Assert.Null(txtProject);
        });
    }

    [Fact]
    public void MainForm_SaveSettings_PersistsUpdatedValuesOnLeave()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            var txtAssetRoot = form.Controls.Find("txtAssetRoot", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtAssetRoot);
            txtAssetRoot.Text = Path.Combine(workspace.Root, "UpdatedAssets");

            var saveMethod = typeof(MainForm).GetMethod("SaveSettingsSafe", BindingFlags.NonPublic | BindingFlags.Instance);
            saveMethod?.Invoke(form, null);

            var loadedSettings = settingsService.Load();
            Assert.Equal(Path.Combine(workspace.Root, "UpdatedAssets"), loadedSettings.AssetRootFolder);
        });
    }

    [Fact]
    public void MainForm_RefreshLatestImage_DetectsNewImageInDownloads()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            workspace.CreateImage("new_download.png", new byte[] { 1, 2, 3, 4 });

            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            form.RefreshImageSelection(ImageSlot.Reference);

            var lblLatestImage = form.Controls.Find("lblReferenceSelectedImage", true).FirstOrDefault() as Label
                ?? form.Controls.Find("lblLatestImage", true).FirstOrDefault() as Label;
            Assert.NotNull(lblLatestImage);
            Assert.Contains("new_download.png", lblLatestImage.Text);
        });
    }

    [Fact]
    public void MainForm_AssetFolderName_CanBeManuallySpecified()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            var txtFolder = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtFolder);

            txtFolder.Text = "CustomAssetName_123";
            Assert.Equal("CustomAssetName_123", txtFolder.Text);
        });
    }

    [Fact]
    public void MainForm_HandleReference_WorkflowTransitionsToReady()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var refImg = workspace.CreateImage("ref_test.png", new byte[] { 1, 2, 3 });

            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            form.SetSelectedImage(ImageSlot.Reference, refImg);

            var txtFolder = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtFolder);
            txtFolder.Text = "asset_ui_test";

            // Trigger reference processing
            var handleRefMethod = typeof(MainForm).GetMethod("HandleReference", BindingFlags.NonPublic | BindingFlags.Instance);
            handleRefMethod?.Invoke(form, null);

            var lblRef = form.Controls.Find("lblReference", true).FirstOrDefault() as Label;
            Assert.NotNull(lblRef);
            Assert.Contains("ref_test.png", lblRef.Text);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var currentSession = sessionField?.GetValue(form) as AssetSession;
            Assert.NotNull(currentSession);
            Assert.Equal("asset_ui_test", currentSession.AssetFolderName);
        });
    }

    [Fact]
    public void MainForm_HandleMainImage_CompletesAssetAndClearsSession()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var refImg = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            form.SetSelectedImage(ImageSlot.Reference, refImg);

            var txtFolder = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            txtFolder!.Text = "asset_main_ui_test";

            var handleRefMethod = typeof(MainForm).GetMethod("HandleReference", BindingFlags.NonPublic | BindingFlags.Instance);
            handleRefMethod?.Invoke(form, null);

            // Create distinct main image in downloads
            var mainImg = workspace.CreateImage("main1.png", new byte[] { 10, 20, 30 });
            form.SetSelectedImage(ImageSlot.Main, mainImg);

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            txtPrompt!.Text = "test UI prompt";

            var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
            handleMainMethod?.Invoke(form, null);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var currentSession = sessionField?.GetValue(form) as AssetSession;
            Assert.Null(currentSession);
            Assert.False(sessionService.Exists());
        });
    }

    [Fact]
    public void MainForm_HandleReplaceReference_ReplacesReferenceImage()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var ref1 = workspace.CreateImage("ref1.png", new byte[] { 1, 2, 3 });

            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            form.SetSelectedImage(ImageSlot.Reference, ref1);

            var txtFolder = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            txtFolder!.Text = "asset_replace_ui_test";

            var handleRefMethod = typeof(MainForm).GetMethod("HandleReference", BindingFlags.NonPublic | BindingFlags.Instance);
            handleRefMethod?.Invoke(form, null);

            // Create replacement image
            var refReplaced = workspace.CreateImage("ref_replaced.png", new byte[] { 7, 8, 9 });
            form.SetSelectedImage(ImageSlot.Reference, refReplaced);

            Dialogs.TwoChoiceDialog.CustomChoiceProvider = (owner, title, msg, p, s) => true;

            var handleReplaceMethod = typeof(MainForm).GetMethod("HandleReplaceReference", BindingFlags.NonPublic | BindingFlags.Instance);
            handleReplaceMethod?.Invoke(form, null);

            Dialogs.TwoChoiceDialog.CustomChoiceProvider = null;

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var currentSession = sessionField?.GetValue(form) as AssetSession;
            Assert.NotNull(currentSession);
            Assert.Equal("ref_replaced.png", currentSession.ReferenceFilename);
        });
    }

    [Fact]
    public void MainForm_PasteClipboardAndClearPrompt()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            Assert.NotNull(txtPrompt);

            txtPrompt.Text = "Custom prompt text";
            Assert.Equal("Custom prompt text", txtPrompt.Text);

            form.ClipboardProvider = () => "My injected prompt from clipboard";
            var pasteMethod = typeof(MainForm).GetMethod("PasteClipboard", BindingFlags.NonPublic | BindingFlags.Instance);
            pasteMethod?.Invoke(form, null);

            Assert.Equal("My injected prompt from clipboard", txtPrompt.Text);

            txtPrompt.Clear();
            Assert.Empty(txtPrompt.Text);
        });
    }

    [Fact]
    public void MainForm_KeyDown_CtrlR_And_CtrlM_TriggersWorkflows()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var shortcutImg = workspace.CreateImage("shortcut_image.png", new byte[] { 1, 2, 3 });

            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            form.SetSelectedImage(ImageSlot.Reference, shortcutImg);

            var txtFolder = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            txtFolder!.Text = "shortcut_asset";

            var keyMethod = typeof(MainForm).GetMethod("MainForm_KeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            
            // 1. Trigger Ctrl+R (Reference)
            var ctrlR = new KeyEventArgs(Keys.Control | Keys.R);
            keyMethod?.Invoke(form, new object[] { form, ctrlR });

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var currentSession = sessionField?.GetValue(form) as AssetSession;
            Assert.NotNull(currentSession);
            Assert.Equal("shortcut_asset", currentSession.AssetFolderName);

            // 2. Prepare Main Image and trigger Ctrl+M (Main)
            var mainImg = workspace.CreateImage("main_shortcut.png", new byte[] { 4, 5, 6 });
            form.SetSelectedImage(ImageSlot.Main, mainImg);

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            txtPrompt!.Text = "shortcut prompt";

            var ctrlM = new KeyEventArgs(Keys.Control | Keys.M);
            keyMethod?.Invoke(form, new object[] { form, ctrlM });

            currentSession = sessionField?.GetValue(form) as AssetSession;
            Assert.Null(currentSession);
            Assert.False(sessionService.Exists());
        });
    }

    [Fact]
    public void MainForm_HandleCancel_CancelsReferenceSession()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            workspace.CreateImage("cancel_image.png", new byte[] { 1, 2, 3 });

            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            var txtFolder = form.Controls.Find("txtAssetFolderName", true).FirstOrDefault() as TextBox;
            txtFolder!.Text = "cancel_asset";

            form.RefreshImageSelection(ImageSlot.Reference);

            var handleRefMethod = typeof(MainForm).GetMethod("HandleReference", BindingFlags.NonPublic | BindingFlags.Instance);
            handleRefMethod?.Invoke(form, null);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var currentSession = sessionField?.GetValue(form) as AssetSession;
            Assert.NotNull(currentSession);

            var handleCancelMethod = typeof(MainForm).GetMethod("HandleCancel", BindingFlags.NonPublic | BindingFlags.Instance);
            handleCancelMethod?.Invoke(form, null);

            var afterCancelSession = sessionField?.GetValue(form) as AssetSession;
            Assert.Null(afterCancelSession);
            Assert.False(sessionService.Exists());
        });
    }

    [Fact]
    public void MainForm_RecoverSessionOnStartup_RecoversPendingSession()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            var refSource = workspace.CreateImage("rec_ref.png", new byte[] { 1, 2, 3 });
            var session = assetProcessor.ProcessReference(settings, "rec_asset", refSource, DateTimeOffset.Now);
            sessionService.Save(session);

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            var recoverMethod = typeof(MainForm).GetMethod("RecoverSessionOnStartup", BindingFlags.NonPublic | BindingFlags.Instance);
            recoverMethod?.Invoke(form, null);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            var currentSession = sessionField?.GetValue(form) as AssetSession;
            Assert.NotNull(currentSession);
            Assert.Equal("rec_ref.png", currentSession.ReferenceFilename);
        });
    }

    [Fact]
    public void MainForm_ErrorAndNavigationMethods_ExecuteSafely()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            var showValidation = typeof(MainForm).GetMethod("ShowValidationError", BindingFlags.NonPublic | BindingFlags.Instance);
            showValidation?.Invoke(form, new object[] { "Validation Error", ValidationResult.Failure("Validation error message") });

            var showError = typeof(MainForm).GetMethod("ShowError", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string), typeof(Exception) }, null);
            showError?.Invoke(form, new object[] { "Error message", new InvalidOperationException("Test inner") });

            // Without this seam, OpenFolder falls through to a real
            // Process.Start(explorer.exe) against a TestWorkspace temp
            // folder. Even the "nonexistent path" case can surface as a real
            // shell error dialog rather than a clean catchable exception; the
            // Downloads case is worse because the folder DOES exist at the
            // moment of the call, so Explorer genuinely opens it, then shows
            // "path not available" once the workspace is disposed out from
            // under it moments later - a real dialog blocking whatever
            // machine runs the suite.
            var openedPaths = new List<string>();
            MainForm.OpenFolderProvider = path => openedPaths.Add(path);

            try
            {
                var openFolder = typeof(MainForm).GetMethod("OpenFolder", BindingFlags.NonPublic | BindingFlags.Instance);
                openFolder?.Invoke(form, new object[] { @"C:\NonExistent_Folder_12345" });

                var openDownloads = typeof(MainForm).GetMethod("OpenDownloads", BindingFlags.NonPublic | BindingFlags.Instance);
                openDownloads?.Invoke(form, null);

                var openAsset = typeof(MainForm).GetMethod("OpenAssetFolder", BindingFlags.NonPublic | BindingFlags.Instance);
                openAsset?.Invoke(form, null);
            }
            finally
            {
                MainForm.OpenFolderProvider = null;
            }
        });
    }

    [Fact]
    public void MainForm_DragDrop_SetsManualSelection()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var droppedFile = workspace.CreateImage("dropped.png", new byte[] { 5, 6, 7 });

            var settings = workspace.CreateSettings();
            var settingsService = workspace.CreateSettingsService();
            var imageFinder = workspace.CreateImageFinder();
            var templateService = workspace.CreateTemplateService();
            var validationService = workspace.CreateValidationService();
            var assetProcessor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();

            using var form = new MainForm(
                settings,
                settingsService,
                imageFinder,
                templateService,
                validationService,
                assetProcessor,
                sessionService);

            var dataObj = new DataObject(DataFormats.FileDrop, new[] { droppedFile });
            var dragEnterMethod = typeof(MainForm).GetMethod("ImageDrop_DragEnter", BindingFlags.NonPublic | BindingFlags.Instance);
            var dragDropMethod = typeof(MainForm).GetMethod("ImageDrop_DragDrop", BindingFlags.NonPublic | BindingFlags.Instance);

            var dragEventArgs = new DragEventArgs(dataObj, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy);
            dragEnterMethod?.Invoke(form, new object[] { form, dragEventArgs });
            dragDropMethod?.Invoke(form, new object[] { ImageSlot.Reference, dragEventArgs });

            var selectedImage = form.GetSelectedImage(ImageSlot.Reference);
            Assert.Equal(droppedFile, selectedImage);
        });
    }

    [Fact]
    public void TwoChoiceDialog_CustomChoiceProvider()
    {
        RunOnSta(() =>
        {
            using var form = new Form();
            _ = form.Handle;

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, primary, secondary) => true;
            var choice1 = TwoChoiceDialog.ShowChoice(form, "Title", "Message", "P", "S");
            Assert.True(choice1);

            TwoChoiceDialog.CustomChoiceProvider = (owner, title, message, primary, secondary) => false;
            var choice2 = TwoChoiceDialog.ShowChoice(form, "Title", "Message", "P", "S");
            Assert.False(choice2);

            TwoChoiceDialog.CustomChoiceProvider = null;
        });
    }

    [Fact]
    public void MainForm_HandleCancel_WhenCancelThrows_ReportsErrorWithoutCrashing()
    {
        RunOnSta(() =>
        {
            var messageReported = false;
            MainForm.MessageBoxProvider = (_, text, caption, buttons, icon) =>
            {
                if (icon == MessageBoxIcon.Error)
                {
                    messageReported = true;
                }
            };

            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_cancel_err", refSource, DateTimeOffset.Now);
            sessionService.Save(session);

            FileStream? destLock = null;
            TwoChoiceDialog.CustomChoiceProvider = (_, _, _, _, _) =>
            {
                // Lock destination file after validation has passed so Cancel throws IOException
                destLock = new FileStream(session.ReferenceDestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            };

            try
            {
                using var form = new MainForm(
                    settings,
                    workspace.CreateSettingsService(),
                    workspace.CreateImageFinder(),
                    workspace.CreateTemplateService(),
                    workspace.CreateValidationService(),
                    workspace.CreateAssetProcessor(),
                    sessionService);

                var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
                sessionField?.SetValue(form, session);

                var handleCancelMethod = typeof(MainForm).GetMethod("HandleCancel", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(handleCancelMethod);

                handleCancelMethod.Invoke(form, null);
                Assert.True(messageReported, "Error message box should be displayed when Cancel throws");
            }
            finally
            {
                destLock?.Dispose();
            }
        });
    }

    [Fact]
    public void MainForm_HandleMainImage_IncompleteRollbackException_PreservesPersistedMetadata()
    {
        RunOnSta(() =>
        {
            var messageReported = false;
            MainForm.MessageBoxProvider = (_, text, caption, buttons, icon) =>
            {
                if (icon == MessageBoxIcon.Error)
                {
                    messageReported = true;
                }
            };

            using var workspace = new TestWorkspace();
            var processor = workspace.CreateAssetProcessor();
            var sessionService = workspace.CreateSessionService();
            var settings = workspace.CreateSettings();

            var refSource = workspace.CreateImage("ref.png", new byte[] { 1, 2, 3 });
            var session = processor.ProcessReference(settings, "asset_main_incomplete", refSource, DateTimeOffset.Now);
            sessionService.Save(session);

            var mainSource = workspace.CreateImage("main.png", new byte[] { 9, 8, 7 });

            using var form = new MainForm(
                settings,
                workspace.CreateSettingsService(),
                workspace.CreateImageFinder(),
                workspace.CreateTemplateService(),
                workspace.CreateValidationService(),
                processor,
                sessionService);

            var sessionField = typeof(MainForm).GetField("_currentSession", BindingFlags.NonPublic | BindingFlags.Instance);
            sessionField?.SetValue(form, session);

            var stateField = typeof(MainForm).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            stateField?.SetValue(form, 1); // UiState.ReferenceReady

            var txtPrompt = form.Controls.Find("txtPrompt", true).FirstOrDefault() as TextBox;
            if (txtPrompt != null) txtPrompt.Text = "Custom main prompt";

            form.SetSelectedImage(ImageSlot.Main, mainSource);

            FileStream? destLock = null;
            try
            {
                // Inject fault in OnMainPromotedHook and lock destination so ProcessMainImage rollback is incomplete
                AssetProcessorService.OnMainPromotedHook = dest =>
                {
                    destLock = new FileStream(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    throw new IOException("Simulated disk failure after main promotion");
                };

                var handleMainMethod = typeof(MainForm).GetMethod("HandleMainImage", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(handleMainMethod);

                handleMainMethod.Invoke(form, null);

                Assert.True(messageReported, "Error dialog should be displayed");

                var reloadedSession = sessionService.Load();
                Assert.NotNull(reloadedSession);
                Assert.True(reloadedSession.IsMainCommitting, "IsMainCommitting should remain true");
                Assert.Equal("main.png", reloadedSession.MainFilename);
                Assert.Equal("Custom main prompt", reloadedSession.MainPrompt);
                Assert.NotNull(reloadedSession.MainHash);
            }
            finally
            {
                destLock?.Dispose();
                AssetProcessorService.OnMainPromotedHook = null;
            }
        });
    }
}

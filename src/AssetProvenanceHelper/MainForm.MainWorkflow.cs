#nullable enable
using System.Diagnostics;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private void HandleMainImage()
    {
        if (!ValidateMainActionUi())
        {
            return;
        }

        var isNoReference = chkNoReference.Checked || (_currentSession?.WorkflowMode == AssetWorkflowMode.NoReference);
        var sourceImage = GetSelectedImage(ImageSlot.Main)!;
        var prompt = txtPrompt.Text;
        var processedAt = DateTimeOffset.Now;
        var settings = ReadSettingsFromUi();

        if (isNoReference && _currentSession is null)
        {
            HandleNoReferenceMainImage(settings, sourceImage, prompt, processedAt);
        }
        else if (_currentSession is not null)
        {
            HandleReferenceAssistedMainImage(settings, sourceImage, prompt, processedAt);
        }
        else
        {
            ShowMessageBox(
                "No active reference session exists.",
                "Main Image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void HandleNoReferenceMainImage(
        AppSettings settings,
        string sourceImage,
        string prompt,
        DateTimeOffset processedAt)
    {
        var assetName = txtAssetFolderName.Text.Trim();
        var targetAssetFolder = Path.Combine(settings.AssetRootFolder, assetName);

        if (Directory.Exists(targetAssetFolder))
        {
            var useExisting = TwoChoiceDialog.ShowChoice(
                this,
                "Existing destination",
                "The destination folder already exists.\n\nUse existing folder?",
                "Use Existing",
                "Cancel");

            if (!useExisting)
            {
                return;
            }
        }

        AssetSession session;
        try
        {
            session = _assetProcessorService.CreateNoReferenceMainSession(
                settings,
                assetName,
                sourceImage,
                prompt,
                processedAt);

            _sessionService.Save(session);
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not initialize and save no-reference asset session.",
                ex);
            return;
        }

        ExecuteMainCommit(session, sourceImage, prompt, processedAt);
    }

    private void HandleReferenceAssistedMainImage(
        AppSettings settings,
        string sourceImage,
        string prompt,
        DateTimeOffset processedAt)
    {
        var session = _currentSession!;

        var sessionValidation = _validationService.ValidateSession(session);
        if (!sessionValidation.IsValid)
        {
            ShowValidationError("Invalid reference session", sessionValidation);
            return;
        }

        string sourceImageHash;
        try
        {
            sourceImageHash = _assetProcessorService.ComputeSha256(sourceImage);
        }
        catch (Exception ex)
        {
            ShowError("Could not read the selected Main image.", ex);
            return;
        }

        var mainFilename = Path.GetFileName(sourceImage);
        var ingameFilename = AssetNaming.BuildIngameFilename(session.AssetFolderName, sourceImage);

        session.IsMainCommitting = true;
        session.MainFilename = mainFilename;
        session.IngameFilename = ingameFilename;
        session.MainPrompt = prompt;
        session.MainProcessedAt = processedAt;
        session.MainHash = sourceImageHash;
        session.MainTransactionId = Guid.NewGuid().ToString("N");
        session.WasIngameFolderCreatedByTool = !Directory.Exists(session.GetIngameFolderPath());

        try
        {
            _sessionService.Save(session);
        }
        catch (Exception saveEx)
        {
            session.ResetMainCommitMetadata();
            ShowError(
                "Could not update session state before Main Image processing. Operation was aborted.",
                saveEx);
            return;
        }

        ExecuteMainCommit(session, sourceImage, prompt, processedAt);
    }

    private void ExecuteMainCommit(
        AssetSession session,
        string sourceImage,
        string prompt,
        DateTimeOffset processedAt)
    {
        try
        {
            var committedFilename = _assetProcessorService.ProcessMainImage(
                session,
                _settings.AcceptedExtensions,
                sourceImage,
                prompt,
                processedAt);

            try
            {
                _sessionService.Delete();
            }
            catch (Exception deleteException)
            {
                var rollback = _assetProcessorService.RollbackMain(
                    session,
                    committedFilename);

                if (!rollback.IsValid)
                {
                    ShowMessageBox(
                        "CRITICAL: Main Image was created, session deletion failed, and automatic rollback was incomplete.\n\n"
                        + string.Join(Environment.NewLine, rollback.Errors),
                        "Critical completion error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();
                    return;
                }

                throw new IOException(
                    "Main Image was rolled back because session.json could not be removed.",
                    deleteException);
            }

            _lastCompletedAssetFolderPath = session.AssetFolder;
            _currentSession = null;
            _state = UiState.Idle;

            txtPrompt.Clear();
            txtAssetFolderName.Clear();
            lblReference.Text = "Saved reference: none";

            SetSelectedImage(ImageSlot.Reference, null);
            SetSelectedImage(ImageSlot.Main, null);
            ClearValidationVisuals();

            AddStatus($"Main image copied: {committedFilename}");
            AddStatus("Final provenance created.");
            AddStatus("Asset completed.");

            ApplyState();

            ShowMessageBox(
                "Asset completed successfully.",
                "Asset Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (AssetProcessingException ape) when (!ape.RollbackComplete)
        {
            ShowMessageBox(
                "CRITICAL: Main Image processing failed and automatic rollback was incomplete.\n\n" + ape.Message,
                "Critical Main Processing Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            Close();
        }
        catch (Exception ex)
        {
            if (session.WorkflowMode == AssetWorkflowMode.NoReference)
            {
                // Clean failure for NoReference deletes temporary session record
                try
                {
                    _sessionService.Delete();
                }
                catch
                {
                    // Non-critical session cleanup error
                }
                _currentSession = null;
            }
            else
            {
                // Reference-assisted reset committing state
                session.ResetMainCommitMetadata();
                try
                {
                    _sessionService.Save(session);
                }
                catch (Exception saveEx)
                {
                    ShowError(
                        "CRITICAL: Main Image processing failed, but updated session could not be saved.",
                        saveEx);
                    Close();
                    return;
                }
            }

            ShowError("Main Image processing failed.", ex);
            ApplyState();
        }
    }

    private void PasteClipboard()
    {
        try
        {
            string? text = null;
            if (ClipboardProvider is not null)
            {
                text = ClipboardProvider();
            }
            else
            {
                if (!Clipboard.ContainsText())
                {
                    ShowMessageBox(
                        "Clipboard does not contain text.",
                        "Paste Clipboard",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                text = Clipboard.GetText();
            }

            if (string.IsNullOrEmpty(text))
            {
                ShowMessageBox(
                    "Clipboard does not contain text.",
                    "Paste Clipboard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            txtPrompt.Text = text;
            ClearMainValidationVisuals();
        }
        catch (Exception ex)
        {
            ShowError("Could not access clipboard.", ex);
        }
    }

    private void OpenDownloads()
    {
        var path = txtDownloadFolder.Text;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            HighlightField(pnlDownloadFolderHost, true);
            txtDownloadFolder.Focus();
            ShowMessageBox(
                "Image download folder does not exist.",
                "Open Image Folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        HighlightField(pnlDownloadFolderHost, false);
        OpenFolder(path);
    }

    private void OpenAssetFolder()
    {
        string? path = _currentSession?.AssetFolder ?? _lastCompletedAssetFolderPath;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        OpenFolder(path);
    }

    private void OpenFolder(string path)
    {
        if (OpenFolderProvider is not null)
        {
            OpenFolderProvider(path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError($"Could not open folder '{path}'.", ex);
        }
    }
}

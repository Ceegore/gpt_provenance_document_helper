#nullable enable
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private void HandleReference()
    {
        if (!ValidateReferenceActionUi())
        {
            return;
        }

        AssetSession? createdSession = null;

        try
        {
            var settings = ReadSettingsFromUi();
            var folderName = txtAssetFolderName.Text.Trim();
            var sourceImage = GetSelectedImage(ImageSlot.Reference)!;

            var targetAssetFolder = Path.Combine(settings.AssetRootFolder, folderName);

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

            createdSession = _assetProcessorService.ProcessReference(
                settings,
                folderName,
                sourceImage,
                DateTimeOffset.Now);

            try
            {
                _sessionService.Save(createdSession);
            }
            catch (Exception saveException)
            {
                var rollback = _assetProcessorService.RollbackReference(createdSession);

                if (!rollback.IsValid)
                {
                    throw new IOException(
                        "Could not save session and reference rollback was incomplete."
                        + Environment.NewLine
                        + string.Join(Environment.NewLine, rollback.Errors),
                        saveException);
                }

                throw;
            }

            _currentSession = createdSession;
            _state = UiState.ReferenceReady;

            lblReference.Text = $"Saved reference: {createdSession.ReferenceFilename}";
            SetSelectedImage(ImageSlot.Reference, null);

            AddStatus($"Reference copied: {createdSession.ReferenceFilename}");
            AddStatus("Reference provenance created.");
            AddStatus("Reference session saved.");

            ApplyState();
        }
        catch (Exception ex)
        {
            ShowError("Reference processing failed.", ex);
        }
    }

    private void HandleReplaceReference()
    {
        if (_currentSession is null)
        {
            return;
        }

        var source = GetSelectedImage(ImageSlot.Reference);
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            HighlightField(pnlReferenceImageHost, true);
            btnChooseReference.Focus();
            StartCtaPulse(btnReference, UiTheme.ReferenceAccent);
            ShowMessageBox(
                "Select a new reference candidate image before replacing.",
                "Replace Reference",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var imgValidation = _validationService.ValidateImageFile(source, _settings.AcceptedExtensions);
        if (!imgValidation.IsValid)
        {
            HighlightField(pnlReferenceImageHost, true);
            btnChooseReference.Focus();
            StartCtaPulse(btnReference, UiTheme.ReferenceAccent);
            ShowValidationError("Invalid replacement image", imgValidation);
            return;
        }

        var confirmed = TwoChoiceDialog.ShowChoice(
            this,
            "Replace Reference",
            "Replace the current reference image?",
            "Replace",
            "Cancel");

        if (!confirmed)
        {
            return;
        }

        ReferenceReplacementTransaction? transaction = null;

        try
        {
            transaction = _assetProcessorService.PrepareReferenceReplacement(
                _currentSession,
                _settings.AcceptedExtensions,
                source,
                DateTimeOffset.Now);

            try
            {
                _sessionService.Save(transaction.NewSession);
            }
            catch (Exception saveException)
            {
                var rollback = _assetProcessorService.RollbackReferenceReplacement(transaction);

                if (!rollback.IsValid)
                {
                    ShowMessageBox(
                        "CRITICAL: Replacement session could not be saved and the old reference could not be fully restored.\n\n"
                        + string.Join(Environment.NewLine, rollback.Errors),
                        "Critical replacement error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();
                    return;
                }

                throw new IOException(
                    "Could not save replacement session. The previous reference was restored.",
                    saveException);
            }

            OnBeforeReferenceReplacementCommit?.Invoke(transaction);

            var cleanup = _assetProcessorService.CommitReferenceReplacement(transaction);

            if (!cleanup.IsValid)
            {
                var newValidation = _validationService.ValidateReferenceOutput(transaction.NewSession);
                var exactNewProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                    transaction.NewSession,
                    transaction.NewSession.ReferenceProvenancePath,
                    _templateService);

                if (!newValidation.IsValid || !exactNewProvValidation.IsValid)
                {
                    var rollback = _assetProcessorService.RollbackReferenceReplacement(transaction);
                    if (!rollback.IsValid)
                    {
                        ShowMessageBox(
                            "CRITICAL: Reference replacement failed, new reference output was invalid, and previous reference could not be fully restored.\n\n"
                            + string.Join(Environment.NewLine, rollback.Errors),
                            "Critical Replacement Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);

                        Close();
                        return;
                    }

                    try
                    {
                        _sessionService.Save(transaction.OldSession);
                    }
                    catch (Exception saveEx)
                    {
                        ShowError(
                            "CRITICAL: Reference replacement failed, previous reference files were restored, but old session record could not be saved.",
                            saveEx);

                        Close();
                        return;
                    }

                    _currentSession = transaction.OldSession;
                    lblReference.Text = $"Saved reference: {_currentSession.ReferenceFilename}";
                    SetSelectedImage(ImageSlot.Reference, null);
                    ApplyState();

                    var combinedErrors = cleanup.Errors.ToList();
                    if (!exactNewProvValidation.IsValid)
                    {
                        combinedErrors.AddRange(exactNewProvValidation.Errors);
                    }

                    ShowMessageBox(
                        "Reference replacement failed because the new reference output was invalid. The previous reference state was restored.\n\n"
                        + string.Join(Environment.NewLine, combinedErrors.Distinct()),
                        "Reference Replacement Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    return;
                }

                _currentSession = transaction.NewSession;
                lblReference.Text = $"Saved reference: {_currentSession.ReferenceFilename}";
                SetSelectedImage(ImageSlot.Reference, null);
                SetSelectedImage(ImageSlot.Main, null);
                txtPrompt.Clear();

                AddStatus($"Reference replaced: {_currentSession.ReferenceFilename}");
                AddStatus("Reference provenance updated.");
                AddStatus("Reference session updated.");

                ShowMessageBox(
                    "Reference replacement succeeded, but old temporary backup files could not be fully cleaned up.\n\n"
                    + string.Join(Environment.NewLine, cleanup.Errors),
                    "Replacement cleanup warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                ApplyState();
                return;
            }

            _currentSession = transaction.NewSession;
            lblReference.Text = $"Saved reference: {_currentSession.ReferenceFilename}";
            SetSelectedImage(ImageSlot.Reference, null);
            SetSelectedImage(ImageSlot.Main, null);
            txtPrompt.Clear();

            AddStatus($"Reference replaced: {_currentSession.ReferenceFilename}");
            AddStatus("Reference provenance updated.");
            AddStatus("Reference session updated.");

            ApplyState();
        }
        catch (Exception ex)
        {
            ShowError("Reference replacement failed.", ex);
        }
    }

    private void HandleCancel()
    {
        if (_currentSession is null)
        {
            return;
        }

        var sessionValidation = _validationService.ValidateSession(_currentSession);
        if (!sessionValidation.IsValid)
        {
            ShowValidationError(
                "Current session is inconsistent. No asset files were deleted.",
                sessionValidation);
            return;
        }

        var refValidation = _validationService.ValidateReferenceOutput(_currentSession);
        if (!refValidation.IsValid)
        {
            ShowValidationError(
                "Current reference artifacts are inconsistent or modified. No asset files were deleted.",
                refValidation);
            return;
        }

        var confirmed = TwoChoiceDialog.ShowChoice(
            this,
            "Cancel current asset",
            "Cancel current asset?\n\nThe reference files created during this session will be removed.",
            "Cancel Asset",
            "Keep Working");

        if (!confirmed)
        {
            return;
        }

        try
        {
            _sessionService.Cancel(_currentSession);

            AddStatus("Current asset session cancelled.");

            _currentSession = null;
            _state = UiState.Idle;

            txtPrompt.Clear();
            txtAssetFolderName.Clear();
            lblReference.Text = "Saved reference: none";

            SetSelectedImage(ImageSlot.Reference, null);
            SetSelectedImage(ImageSlot.Main, null);
            ClearValidationVisuals();

            ApplyState();
        }
        catch (Exception ex)
        {
            ShowError("Could not cancel current asset safely.", ex);
        }
    }
}

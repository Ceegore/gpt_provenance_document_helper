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

        AssetSession? preparedSession = null;
        AssetSession? completedSession = null;

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

            var now = DateTimeOffset.Now;
            preparedSession = _assetProcessorService.CreateReferenceSession(
                settings,
                folderName,
                sourceImage,
                now);

            _sessionService.Save(preparedSession);

            completedSession = _assetProcessorService.ProcessReference(
                preparedSession,
                settings,
                sourceImage,
                now);

            _sessionService.Save(completedSession);
        }
        catch (Exception ex)
        {
            if (preparedSession is null)
            {
                ShowError("Reference processing failed.", ex);
                return;
            }

            ValidationResult rollback;
            try
            {
                rollback = _assetProcessorService.RollbackReference(preparedSession);
            }
            catch (Exception rollbackEx)
            {
                ShowError(
                    "CRITICAL: Reference processing failed and the prepared transaction could not be safely reconciled.",
                    rollbackEx);

                Close();
                return;
            }

            if (!rollback.IsValid)
            {
                ShowMessageBox(
                    "CRITICAL: Reference processing failed and rollback could not be proven complete."
                    + Environment.NewLine
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, rollback.Errors)
                    + Environment.NewLine
                    + Environment.NewLine
                    + "The prepared session journal was preserved.",
                    "Critical Reference Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                Close();
                return;
            }

            try
            {
                _sessionService.Delete();
            }
            catch (Exception deleteEx)
            {
                ShowError(
                    "Reference output rollback succeeded, but the prepared session journal could not be deleted.",
                    deleteEx);

                Close();
                return;
            }

            ShowError(
                "Reference processing failed. The prepared transaction was rolled back safely.",
                ex);

            return;
        }

        // DURABLE COMMIT POINT: completedSession is safely persisted.
        CompleteReferenceUiAfterDurableCommit(completedSession);
    }

    private void CompleteReferenceUiAfterDurableCommit(
        AssetSession completedSession)
    {
        _currentSession = completedSession;
        _state = UiState.ReferenceReady;

        try
        {
            OnReferenceStableSessionSavedHook?.Invoke(completedSession);

            lblReference.Text = $"Saved reference: {completedSession.ReferenceFilename}";
            SetSelectedImage(ImageSlot.Reference, null);

            AddStatus($"Reference copied: {completedSession.ReferenceFilename}");
            AddStatus("Reference provenance created.");
            AddStatus("Reference session saved.");

            ApplyState();
        }
        catch (Exception uiException)
        {
            try
            {
                ShowMessageBox(
                    "Reference was saved successfully, but the interface could not be refreshed."
                    + Environment.NewLine
                    + Environment.NewLine
                    + uiException.Message,
                    "Post-Commit UI Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }

            Close();
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
            var now = DateTimeOffset.Now;

            // 1. Create transaction in memory without filesystem mutations
            transaction = _assetProcessorService.CreateReferenceReplacementTransaction(
                _currentSession,
                _settings.AcceptedExtensions,
                source,
                now);

            // 2. Write-ahead Phase: Prepared
            _sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.Prepared));

            // 3. Create temp new files
            _assetProcessorService.CreateReplacementTempFiles(
                transaction,
                _settings.AcceptedExtensions);

            // 4. Write-ahead Phase: OldBackupPending
            _sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.OldBackupPending));

            // 5. Move old files to backup paths
            _assetProcessorService.BackupOldReference(transaction);

            // 6. Write-ahead Phase: OldBackedUp
            _sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.OldBackedUp));

            // 7. Write-ahead Phase: NewPromotionPending
            _sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.NewPromotionPending));

            // 8. Promote new temp files to canonical destinations
            _assetProcessorService.PromoteNewReference(transaction);

            // 9. Write-ahead Phase: NewPromoted
            _sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.NewPromoted));

            // 10. Write-ahead Phase: SessionSwitchPending
            _sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.SessionSwitchPending));

            // 11. Save NewSession to session.json
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
                        "CRITICAL: Replacement session could not be saved and the old Reference could not be fully restored."
                        + Environment.NewLine
                        + Environment.NewLine
                        + string.Join(Environment.NewLine, rollback.Errors),
                        "Critical replacement error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();
                    return;
                }

                if (!FinalizeLiveReplacementRollback(transaction) || IsDisposed)
                {
                    return;
                }

                ShowError(
                    "Could not save replacement session. The previous Reference was restored.",
                    saveException);

                return;
            }

            // 12. Write-ahead Phase: SessionSwitched
            _sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.SessionSwitched));

            // 13. Verify NewSession exact output
            var newValidation = _validationService.ValidateExactReferenceOutput(transaction.NewSession, _templateService);
            if (!newValidation.IsValid)
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

                if (!FinalizeLiveReplacementRollback(transaction) || IsDisposed)
                {
                    return;
                }

                ShowMessageBox(
                    "Reference replacement failed because the new reference output was invalid. The previous reference state was restored.\n\n"
                    + string.Join(Environment.NewLine, newValidation.Errors),
                    "Reference Replacement Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return;
            }

            // 14. Write-ahead Phase: CleanupPending
            _sessionService.SaveReplacementJournal(
                transaction.ToJournal(ReferenceReplacementPhase.CleanupPending));

            OnBeforeReferenceReplacementCommit?.Invoke(transaction);

            // 15. Delete backup files
            var cleanup = _assetProcessorService.CleanupReplacementBackups(transaction);

            if (!cleanup.IsValid)
            {
                ShowMessageBox(
                    "CRITICAL: Reference replacement reached cleanup, but cleanup could not be proven complete."
                    + Environment.NewLine
                    + Environment.NewLine
                    + string.Join(
                        Environment.NewLine,
                        cleanup.Errors)
                    + Environment.NewLine
                    + Environment.NewLine
                    + "The CleanupPending journal was preserved.",
                    "Critical replacement cleanup error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                Close();
                return;
            }

            // 16. Delete replacement journal upon complete success
            try
            {
                _sessionService.DeleteReplacementJournal();
            }
            catch (Exception deleteJournalEx)
            {
                ShowError(
                    "CRITICAL: Reference replacement succeeded on disk, but the replacement journal could not be removed.",
                    deleteJournalEx);
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            if (transaction != null && !transaction.IsCommitted)
            {
                // Attempt rollback to preserve invariants
                try
                {
                    var rollback = _assetProcessorService.RollbackReferenceReplacement(transaction);
                    if (rollback.IsValid)
                    {
                        if (FinalizeLiveReplacementRollback(transaction) && !IsDisposed)
                        {
                            ShowError("Reference replacement encountered an error and previous reference was restored.", ex);
                        }
                        return;
                    }
                    else
                    {
                        ShowMessageBox(
                            "CRITICAL: Reference replacement encountered an error and automatic rollback was incomplete.\n\n"
                            + string.Join(Environment.NewLine, rollback.Errors),
                            "Critical Replacement Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        Close();
                        return;
                    }
                }
                catch
                {
                    // Preserve journal on unresolved error
                    ShowError("Reference replacement failed and journal was preserved for recovery.", ex);
                    Close();
                    return;
                }
            }
            ShowError("Reference replacement failed.", ex);
            return;
        }

        // DURABLE COMMIT POINT: New reference is active and replacement journal is deleted.
        CompleteReplacementUiAfterDurableCommit(transaction);
    }

    private void CompleteReplacementUiAfterDurableCommit(
        ReferenceReplacementTransaction transaction)
    {
        _currentSession = transaction.NewSession;

        try
        {
            OnReplacementDurableCommitUiHook?.Invoke();

            lblReference.Text = $"Saved reference: {_currentSession.ReferenceFilename}";
            SetSelectedImage(ImageSlot.Reference, null);
            SetSelectedImage(ImageSlot.Main, null);
            txtPrompt.Clear();
            ClearValidationVisuals();

            AddStatus($"Reference replaced: {_currentSession.ReferenceFilename}");
            AddStatus("Reference provenance updated.");
            AddStatus("Reference session updated.");
            AddStatus("Main candidate and prompt cleared because the Reference changed.");

            ApplyState();
        }
        catch (Exception uiEx)
        {
            try
            {
                ShowMessageBox(
                    "Reference replacement succeeded, but the interface could not be refreshed."
                    + Environment.NewLine
                    + Environment.NewLine
                    + uiEx.Message,
                    "Post-Commit UI Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }

            Close();
        }
    }

    private bool FinalizeLiveReplacementRollback(
        ReferenceReplacementTransaction tx)
    {
        try
        {
            _sessionService.Save(tx.OldSession);
        }
        catch (Exception ex)
        {
            ShowError(
                "CRITICAL: Replacement files were rolled back, but the OLD session could not be persisted.",
                ex);
            Close();
            return false;
        }

        try
        {
            _sessionService.DeleteReplacementJournal();
        }
        catch (Exception ex)
        {
            ShowError(
                "CRITICAL: OLD state was restored, but the replacement journal could not be removed.",
                ex);
            Close();
            return false;
        }

        // Durable rollback commit point.
        _currentSession = tx.OldSession;
        _state = UiState.ReferenceReady;

        CompleteReplacementRollbackUiAfterDurableCommit(tx.OldSession);

        return true;
    }

    private void CompleteReplacementRollbackUiAfterDurableCommit(
        AssetSession oldSession)
    {
        try
        {
            OnReplacementRollbackDurableCommitHook?.Invoke();

            lblReference.Text = $"Saved reference: {oldSession.ReferenceFilename}";
            SetSelectedImage(ImageSlot.Reference, null);
            ApplyState();
        }
        catch (Exception uiEx)
        {
            try
            {
                ShowMessageBox(
                    "The previous Reference was restored successfully, but the interface could not be refreshed."
                    + Environment.NewLine
                    + Environment.NewLine
                    + uiEx.Message,
                    "Post-Rollback UI Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }

            Close();
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

        var cancelledSession = _currentSession;

        try
        {
            _sessionService.Cancel(cancelledSession);
        }
        catch (Exception ex)
        {
            ShowError("Could not cancel current asset safely.", ex);
            return;
        }

        // DURABLE CANCEL COMMIT POINT.
        // The service outputs/session are gone.
        // Detach authority before any throwable UI/status work.
        _currentSession = null;
        _state = UiState.Idle;

        CompleteCancelUiAfterDurableCommit();
    }

    private void CompleteCancelUiAfterDurableCommit()
    {
        try
        {
            OnCancelDurableCommitHook?.Invoke();

            AddStatus("Current asset session cancelled.");

            txtPrompt.Clear();
            txtAssetFolderName.Clear();
            lblReference.Text = "Saved reference: none";

            SetSelectedImage(ImageSlot.Reference, null);
            SetSelectedImage(ImageSlot.Main, null);
            ClearValidationVisuals();

            ApplyState();
        }
        catch (Exception uiEx)
        {
            try
            {
                ShowMessageBox(
                    "The asset session was cancelled successfully, but the interface could not be refreshed."
                    + Environment.NewLine
                    + Environment.NewLine
                    + uiEx.Message,
                    "Post-Cancel UI Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }

            Close();
        }
    }
}

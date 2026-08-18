#nullable enable
using System.Globalization;
using System.Windows.Forms;
using AssetProvenanceHelper.Dialogs;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

partial class MainForm
{
    private void RecoverSessionOnStartup()
    {
        if (!_sessionService.Exists())
        {
            return;
        }

        AssetSession? session;

        try
        {
            session = _sessionService.Load();
        }
        catch (Exception ex)
        {
            var deleteRecord = TwoChoiceDialog.ShowChoice(
                this,
                "Broken session file",
                "The existing session.json could not be parsed.\n\n"
                + ex.Message
                + "\n\n"
                + "Because its asset paths cannot be trusted, no asset files will be deleted automatically.\n\n"
                + "Delete only the broken session record?",
                "Delete Session Record",
                "Exit");

            if (deleteRecord)
            {
                try
                {
                    _sessionService.Delete();
                    AddStatus("Broken session record deleted. Asset files were left untouched.");
                }
                catch (Exception deleteException)
                {
                    ShowError("Could not delete broken session record.", deleteException);
                    Close();
                }
            }
            else
            {
                Close();
            }

            return;
        }

        if (session is null)
        {
            return;
        }

        var validation = _validationService.ValidateSession(session);

        if (!validation.IsValid)
        {
            var deleteRecord = TwoChoiceDialog.ShowChoice(
                this,
                "Invalid unfinished session",
                "An unfinished session exists, but it is inconsistent:\n\n"
                + string.Join(Environment.NewLine, validation.Errors)
                + "\n\n"
                + "Because the recorded asset state is no longer trusted, no asset files will be deleted automatically.\n\n"
                + "Delete only the session record?",
                "Delete Session Record",
                "Exit");

            if (!deleteRecord)
            {
                Close();
                return;
            }

            try
            {
                _sessionService.Delete();
                AddStatus("Invalid session record deleted. Asset files were left untouched.");
            }
            catch (Exception ex)
            {
                ShowError("Could not delete invalid session record.", ex);
                Close();
            }

            return;
        }

        // Interrupted cancellation recovery
        if (session.CancelPhase != CancelPhase.None)
        {
            try
            {
                _sessionService.Cancel(session);
                AddStatus("Interrupted cancellation was resumed and completed successfully.");
            }
            catch (Exception ex)
            {
                ShowError("CRITICAL: Resuming interrupted cancellation failed.", ex);
                Close();
            }

            return;
        }

        if (session.WorkflowMode == AssetWorkflowMode.NoReference)
        {
            RecoverNoReferenceSession(session);
            return;
        }

        RecoverReferenceAssistedSession(session);
    }

    private void RecoverNoReferenceSession(AssetSession session)
    {
        var mainFilename = session.MainFilename ?? string.Empty;
        var mainImagePath = Path.Combine(session.AssetFolder, mainFilename);
        var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var mainDateStr = (session.MainProcessedAt ?? DateTimeOffset.Now)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var completeValidation = _validationService.ValidateCompleteAsset(
            session,
            mainImagePath,
            finalProvPath,
            mainFilename,
            mainDateStr,
            session.MainPrompt ?? string.Empty,
            session.MainHash);

        if (completeValidation.IsValid)
        {
            var deleteRecord = TwoChoiceDialog.ShowChoice(
                this,
                "Completed asset session",
                $"An asset session was interrupted after completion.\n\nAsset:\n{session.AssetFolderName}\n\nThe complete asset (main image and final provenance) is valid and intact.\n\nDelete the leftover session record?",
                "Delete Session Record",
                "Exit");

            if (deleteRecord)
            {
                try
                {
                    _sessionService.Delete();
                    _lastCompletedAssetFolderPath = session.AssetFolder;
                    AddStatus($"Leftover session record for completed asset '{session.AssetFolderName}' deleted.");
                    ApplyState();
                }
                catch (Exception ex)
                {
                    ShowError("Could not delete completed session record.", ex);
                    Close();
                }
            }
            else
            {
                Close();
            }

            return;
        }

        // Incomplete NoReference Main commit -> Rollback
        ValidationResult rollback;
        try
        {
            rollback = _assetProcessorService.RollbackMain(session, session.MainFilename);
        }
        catch (Exception ex)
        {
            ShowMessageBox(
                "CRITICAL: Recovery could not safely evaluate or roll back the incomplete Main commit.\n\n"
                + ex.Message + "\n\nNo asset files were deleted automatically.",
                "Critical Recovery Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            Close();
            return;
        }

        if (!rollback.IsValid)
        {
            ShowMessageBox(
                "CRITICAL: Recovery found an incomplete No-Reference Main commit, but automatic rollback failed.\n\n"
                + string.Join(Environment.NewLine, rollback.Errors),
                "Critical Recovery Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            Close();
            return;
        }

        try
        {
            _sessionService.Delete();
            AddStatus("Interrupted no-reference Main transaction rolled back.");
            _currentSession = null;
            _state = UiState.Idle;
            ApplyState();
        }
        catch (Exception ex)
        {
            ShowError("Could not delete session record after rollback.", ex);
            Close();
        }
    }

    private void RecoverReferenceAssistedSession(AssetSession session)
    {
        if (session.IsMainCommitting && !string.IsNullOrWhiteSpace(session.MainFilename))
        {
            var refBaselineValidation = _validationService.ValidateReferenceOutput(session);
            if (!refBaselineValidation.IsValid)
            {
                var deleteCorruptSession = TwoChoiceDialog.ShowChoice(
                    this,
                    "Inconsistent asset session",
                    $"An active asset session contains invalid reference data and cannot be safely resumed or rolled back.\n\nAsset:\n{session.AssetFolderName}\n\nValidation errors:\n{string.Join(Environment.NewLine, refBaselineValidation.Errors)}\n\nDelete this session record? (No asset files will be deleted)",
                    "Delete Session Record",
                    "Exit");

                if (deleteCorruptSession)
                {
                    try
                    {
                        _sessionService.Delete();
                        AddStatus($"Corrupt session record for '{session.AssetFolderName}' deleted.");
                        ApplyState();
                    }
                    catch (Exception ex)
                    {
                        ShowError("Could not delete invalid session record.", ex);
                        Close();
                    }
                }
                else
                {
                    Close();
                }

                return;
            }

            var mainImagePath = Path.Combine(session.AssetFolder, session.MainFilename);
            var finalProvPath = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
            var mainDateStr = (session.MainProcessedAt ?? session.ReferenceProcessedAt)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var completeValidation = _validationService.ValidateCompleteAsset(
                session,
                mainImagePath,
                finalProvPath,
                session.MainFilename,
                mainDateStr,
                session.MainPrompt ?? string.Empty,
                session.MainHash);

            if (completeValidation.IsValid)
            {
                var deleteRecord = TwoChoiceDialog.ShowChoice(
                    this,
                    "Completed asset session",
                    $"An asset session was interrupted after completion.\n\nAsset:\n{session.AssetFolderName}\n\nThe complete asset (main image and final provenance) is valid and intact.\n\nDelete the leftover session record?",
                    "Delete Session Record",
                    "Exit");

                if (deleteRecord)
                {
                    try
                    {
                        _sessionService.Delete();
                        _lastCompletedAssetFolderPath = session.AssetFolder;
                        AddStatus($"Leftover session record for completed asset '{session.AssetFolderName}' deleted.");
                        ApplyState();
                    }
                    catch (Exception ex)
                    {
                        ShowError("Could not delete completed session record.", ex);
                        Close();
                    }
                }
                else
                {
                    Close();
                }

                return;
            }
            else
            {
                ValidationResult rollback;
                try
                {
                    rollback = _assetProcessorService.RollbackMain(session, session.MainFilename);
                }
                catch (Exception ex)
                {
                    ShowMessageBox(
                        "CRITICAL: Recovery could not safely evaluate or roll back the incomplete Main commit.\n\n"
                        + ex.Message + "\n\nNo asset files were deleted automatically.",
                        "Critical Recovery Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();
                    return;
                }

                if (!rollback.IsValid)
                {
                    ShowMessageBox(
                        "CRITICAL: Recovery found an incomplete Main commit, but automatic rollback failed.\n\n"
                        + string.Join(Environment.NewLine, rollback.Errors),
                        "Critical Recovery Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    Close();
                    return;
                }

                session.ResetMainCommitMetadata();

                try
                {
                    _sessionService.Save(session);
                    AddStatus("Incomplete Main commit rolled back. Reference session resumed.");
                }
                catch (Exception ex)
                {
                    ShowError("CRITICAL: Incomplete Main commit was rolled back, but could not update session.json.", ex);
                    Close();
                    return;
                }
            }
        }

        var resumeValidation = _validationService.ValidateReferenceOutput(session);
        if (!resumeValidation.IsValid)
        {
            var deleteCorruptSession = TwoChoiceDialog.ShowChoice(
                this,
                "Inconsistent reference session",
                $"An unfinished reference session exists, but its reference artifacts are missing or invalid:\n\n{string.Join(Environment.NewLine, resumeValidation.Errors)}\n\nDelete this session record? (No asset files will be deleted)",
                "Delete Session Record",
                "Exit");

            if (deleteCorruptSession)
            {
                try
                {
                    _sessionService.Delete();
                    AddStatus($"Corrupt session record for '{session.AssetFolderName}' deleted.");
                    ApplyState();
                }
                catch (Exception ex)
                {
                    ShowError("Could not delete invalid session record.", ex);
                    Close();
                }
            }
            else
            {
                Close();
            }

            return;
        }

        _currentSession = session;
        _state = UiState.ReferenceReady;

        txtAssetRoot.Text = session.AssetRootFolder;
        txtAssetFolderName.Text = session.AssetFolderName;
        lblReference.Text = $"Saved reference: {session.ReferenceFilename}";

        SetSelectedImage(ImageSlot.Reference, null);
        SetSelectedImage(ImageSlot.Main, null);

        AddStatus($"Resumed reference session for '{session.AssetFolderName}'.");
        ApplyState();
    }
}

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
        RecoverReferenceReplacementJournalIfPresent();

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
            _templateService,
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
        if (session.ReferenceCommitPhase == ReferenceCommitPhase.Prepared)
        {
            var exactOutput = _validationService.ValidateExactReferenceOutput(session, _templateService);
            if (exactOutput.IsValid)
            {
                session.ReferenceCommitPhase = ReferenceCommitPhase.None;
                session.ReferenceTransactionId = null;
                try
                {
                    _sessionService.Save(session);
                }
                catch (Exception ex)
                {
                    ShowError("Could not update recovered reference session.", ex);
                    Close();
                    return;
                }

                _currentSession = session;
                _state = UiState.ReferenceReady;
                txtAssetRoot.Text = session.AssetRootFolder;
                txtAssetFolderName.Text = session.AssetFolderName;
                lblReference.Text = $"Saved reference: {session.ReferenceFilename}";
                SetSelectedImage(ImageSlot.Reference, null);
                SetSelectedImage(ImageSlot.Main, null);
                AddStatus($"Interrupted Reference creation for '{session.AssetFolderName}' was completed and recovered.");
                ApplyState();
                return;
            }

            var refExists = File.Exists(session.ReferenceDestinationPath);
            var provExists = File.Exists(session.ReferenceProvenancePath);

            if (!refExists && !provExists)
            {
                try
                {
                    _sessionService.Delete();
                    AddStatus($"Unfinished Reference creation journal for '{session.AssetFolderName}' removed.");
                    _currentSession = null;
                    _state = UiState.Idle;
                    ApplyState();
                    return;
                }
                catch (Exception ex)
                {
                    ShowError("Could not delete reference creation journal.", ex);
                    Close();
                    return;
                }
            }

            var rollback = _assetProcessorService.RollbackReference(session);
            if (rollback.IsValid)
            {
                try
                {
                    _sessionService.Delete();
                    AddStatus($"Unfinished Reference creation for '{session.AssetFolderName}' was rolled back.");
                    _currentSession = null;
                    _state = UiState.Idle;
                    ApplyState();
                    return;
                }
                catch (Exception ex)
                {
                    ShowError("Could not delete reference journal after rollback.", ex);
                    Close();
                    return;
                }
            }
            else
            {
                ShowMessageBox(
                    "CRITICAL: Unfinished Reference creation could not be safely rolled back.\n\n"
                    + string.Join(Environment.NewLine, rollback.Errors),
                    "Critical Reference Recovery Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Close();
                return;
            }
        }

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
                _templateService,
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

    private void RecoverReferenceReplacementJournalIfPresent()
    {
        if (!_sessionService.ReplacementJournalExists())
        {
            return;
        }

        ReferenceReplacementJournal? journal;
        try
        {
            journal = _sessionService.LoadReplacementJournal();
        }
        catch (Exception ex)
        {
            ShowMessageBox(
                "CRITICAL: The replacement journal could not be read.\n\n" + ex.Message,
                "Critical Replacement Recovery Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
            return;
        }

        if (journal is null)
        {
            _sessionService.DeleteReplacementJournal();
            return;
        }

        try
        {
            switch (journal.Phase)
            {
                case ReferenceReplacementPhase.Prepared:
                    if (File.Exists(journal.TempNewReferencePath))
                    {
                        File.Delete(journal.TempNewReferencePath);
                    }
                    if (File.Exists(journal.TempNewProvenancePath))
                    {
                        File.Delete(journal.TempNewProvenancePath);
                    }
                    _sessionService.DeleteReplacementJournal();
                    AddStatus("Unfinished reference replacement (Phase Prepared) was rolled back.");
                    break;

                case ReferenceReplacementPhase.OldBackedUp:
                    if (File.Exists(journal.BackupProvenancePath))
                    {
                        var provValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                            journal.OldSession,
                            journal.BackupProvenancePath,
                            _templateService);

                        if (!provValidation.IsValid)
                        {
                            throw new InvalidDataException(
                                $"Backup reference provenance does not match old session state: {string.Join("; ", provValidation.Errors)}");
                        }

                        File.Move(journal.BackupProvenancePath, journal.OldSession.ReferenceProvenancePath, overwrite: true);
                    }

                    if (File.Exists(journal.BackupReferencePath))
                    {
                        var hash = ValidationService.ComputeSha256(journal.BackupReferencePath);
                        if (!string.Equals(hash, journal.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "Backup reference image hash does not match old session state.");
                        }

                        File.Move(journal.BackupReferencePath, journal.OldSession.ReferenceDestinationPath, overwrite: true);
                    }

                    if (File.Exists(journal.TempNewReferencePath))
                    {
                        File.Delete(journal.TempNewReferencePath);
                    }
                    if (File.Exists(journal.TempNewProvenancePath))
                    {
                        File.Delete(journal.TempNewProvenancePath);
                    }

                    _sessionService.Save(journal.OldSession);
                    _sessionService.DeleteReplacementJournal();
                    AddStatus("Interrupted reference replacement (Phase OldBackedUp) was restored to previous reference.");
                    break;

                case ReferenceReplacementPhase.NewPromoted:
                    var currentSession = _sessionService.Exists() ? _sessionService.Load() : null;
                    var sessionSwitched = currentSession != null &&
                        string.Equals(currentSession.ReferenceFilename, journal.NewSession.ReferenceFilename, StringComparison.Ordinal);

                    if (sessionSwitched)
                    {
                        var exactNew = _validationService.ValidateExactReferenceOutput(journal.NewSession, _templateService);
                        if (!exactNew.IsValid)
                        {
                            throw new InvalidDataException("New reference files are invalid or modified: " + string.Join("; ", exactNew.Errors));
                        }

                        if (File.Exists(journal.BackupReferencePath)) File.Delete(journal.BackupReferencePath);
                        if (File.Exists(journal.BackupProvenancePath)) File.Delete(journal.BackupProvenancePath);

                        _sessionService.DeleteReplacementJournal();
                        AddStatus("Interrupted reference replacement (Phase NewPromoted) completed.");
                    }
                    else
                    {
                        var oldProvValid = _validationService.ValidateExactReferenceProvenanceOwnership(
                            journal.OldSession,
                            journal.BackupProvenancePath,
                            _templateService);

                        if (!oldProvValid.IsValid)
                        {
                            throw new InvalidDataException("Backup reference provenance is corrupted or modified.");
                        }

                        if (File.Exists(journal.NewSession.ReferenceProvenancePath))
                        {
                            File.Delete(journal.NewSession.ReferenceProvenancePath);
                        }
                        if (File.Exists(journal.NewSession.ReferenceDestinationPath))
                        {
                            File.Delete(journal.NewSession.ReferenceDestinationPath);
                        }

                        File.Move(journal.BackupProvenancePath, journal.OldSession.ReferenceProvenancePath, overwrite: true);
                        File.Move(journal.BackupReferencePath, journal.OldSession.ReferenceDestinationPath, overwrite: true);

                        _sessionService.Save(journal.OldSession);
                        _sessionService.DeleteReplacementJournal();
                        AddStatus("Interrupted reference replacement (Phase NewPromoted) was rolled back to previous reference.");
                    }
                    break;

                case ReferenceReplacementPhase.SessionSwitched:
                    var exactValidation = _validationService.ValidateExactReferenceOutput(journal.NewSession, _templateService);
                    if (!exactValidation.IsValid)
                    {
                        throw new InvalidDataException("New reference files are invalid or modified: " + string.Join("; ", exactValidation.Errors));
                    }

                    if (File.Exists(journal.BackupReferencePath)) File.Delete(journal.BackupReferencePath);
                    if (File.Exists(journal.BackupProvenancePath)) File.Delete(journal.BackupProvenancePath);

                    _sessionService.DeleteReplacementJournal();
                    AddStatus("Interrupted reference replacement (Phase SessionSwitched) cleanup completed.");
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowMessageBox(
                "CRITICAL: Failed to recover interrupted reference replacement.\n\n"
                + ex.Message
                + "\n\nReplacement journal was preserved.",
                "Critical Replacement Recovery Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }
}

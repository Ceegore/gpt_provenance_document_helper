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
        // R2-001: Validate and recover replacement journal FIRST, before any session work
        if (!RecoverReferenceReplacementJournalIfPresent())
        {
            return;
        }

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

        // R2-003: Check ReferenceCommitPhase.Prepared BEFORE running full ValidateSession,
        // which would reject in-progress sessions whose files may not yet exist on disk.
        if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted
            && session.ReferenceCommitPhase == ReferenceCommitPhase.Prepared)
        {
            RecoverPreparedReferenceSession(session);
            return;
        }

        // Interrupted cancellation recovery — validate lightly (files may be mid-rename)
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

    /// <summary>
    /// R2-003: Handles recovery for sessions in ReferenceCommitPhase.Prepared.
    /// Called BEFORE full ValidateSession so sessions with absent files can be recovered.
    /// </summary>
    private void RecoverPreparedReferenceSession(AssetSession session)
    {
        var preparedValidation = _validationService.ValidatePreparedReferenceSession(session);
        if (!preparedValidation.IsValid)
        {
            var deleteRecord = TwoChoiceDialog.ShowChoice(
                this,
                "Corrupt prepared session",
                "A prepared reference session exists but its structural metadata is invalid:\n\n"
                + string.Join(Environment.NewLine, preparedValidation.Errors)
                + "\n\nDelete only the session record? (No asset files will be deleted)",
                "Delete Session Record",
                "Exit");

            if (deleteRecord)
            {
                try
                {
                    _sessionService.Delete();
                    AddStatus("Corrupt prepared session record deleted.");
                    ApplyState();
                }
                catch (Exception ex)
                {
                    ShowError("Could not delete corrupt prepared session record.", ex);
                    Close();
                }
            }
            else
            {
                Close();
            }

            return;
        }

        // Route to the existing prepared-phase recovery logic
        RecoverReferenceAssistedSession(session);
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
            // R2-004: Use EXACT reference validation before deciding whether to roll back Main.
            // If Reference integrity cannot be proven exactly, fail closed and preserve all Main outputs.
            var refBaselineValidation = _validationService.ValidateExactReferenceOutput(session, _templateService);
            if (!refBaselineValidation.IsValid)
            {
                ShowMessageBox(
                    "CRITICAL: An active Main commit session has a Reference whose exact integrity cannot be verified.\n\n"
                    + "Asset: " + session.AssetFolderName + "\n\n"
                    + "Validation errors:\n" + string.Join(Environment.NewLine, refBaselineValidation.Errors)
                    + "\n\nAll asset files have been preserved. Please inspect the asset folder before continuing.",
                    "Critical Reference Integrity Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                Close();
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

    /// <summary>
    /// R2-001: Validates the replacement journal structure before any recovery mutation,
    /// then recovers/completes the interrupted replacement transaction.
    /// Returns false if the application should close due to an unrecoverable error.
    /// </summary>
    private bool RecoverReferenceReplacementJournalIfPresent()
    {
        if (!_sessionService.ReplacementJournalExists())
        {
            return true;
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
            return false;
        }

        if (journal is null)
        {
            _sessionService.DeleteReplacementJournal();
            return true;
        }

        // R2-001: Validate the journal structure BEFORE trusting any paths
        var journalValidation = _validationService.ValidateReferenceReplacementJournal(journal);
        if (!journalValidation.IsValid)
        {
            ShowMessageBox(
                "CRITICAL: The replacement journal failed structural validation and cannot be trusted.\n\n"
                + string.Join(Environment.NewLine, journalValidation.Errors)
                + "\n\nThe journal has been preserved. Please inspect the asset folder before deleting it manually.",
                "Critical Replacement Recovery Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
            return false;
        }

        try
        {
            switch (journal.Phase)
            {
                case ReferenceReplacementPhase.Prepared:
                    // No canonical mutations occurred; only temp files may exist
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

                case ReferenceReplacementPhase.CleanupPending:
                    var exactCleanup = _validationService.ValidateExactReferenceOutput(journal.NewSession, _templateService);
                    if (!exactCleanup.IsValid)
                    {
                        throw new InvalidDataException("New reference files are invalid or modified: " + string.Join("; ", exactCleanup.Errors));
                    }

                    if (File.Exists(journal.BackupReferencePath)) File.Delete(journal.BackupReferencePath);
                    if (File.Exists(journal.BackupProvenancePath)) File.Delete(journal.BackupProvenancePath);

                    _sessionService.DeleteReplacementJournal();
                    AddStatus("Interrupted reference replacement (Phase CleanupPending) cleanup completed.");
                    break;

                case ReferenceReplacementPhase.OldBackupPending:
                    // Backup was about to start; may be partial. Restore any backups, delete temps.
                    RestoreReferenceImageFailClosed(
                        journal.BackupReferencePath,
                        journal.OldSession.ReferenceDestinationPath,
                        journal.OldSession.ReferenceHash);

                    RestoreReferenceProvenanceFailClosed(
                        journal.BackupProvenancePath,
                        journal.OldSession.ReferenceProvenancePath,
                        journal.OldSession,
                        _templateService);

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
                    AddStatus("Interrupted reference replacement (Phase OldBackupPending) was restored to previous reference.");
                    break;

                case ReferenceReplacementPhase.OldBackedUp:
                    // Old files are in backup paths; new temp files may exist; restore old.
                    RestoreReferenceProvenanceFailClosed(
                        journal.BackupProvenancePath,
                        journal.OldSession.ReferenceProvenancePath,
                        journal.OldSession,
                        _templateService);

                    RestoreReferenceImageFailClosed(
                        journal.BackupReferencePath,
                        journal.OldSession.ReferenceDestinationPath,
                        journal.OldSession.ReferenceHash);

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

                case ReferenceReplacementPhase.NewPromotionPending:
                    // Promotion was about to start; may be partial. Restore old from backups.
                    RestoreReferenceProvenanceFailClosed(
                        journal.BackupProvenancePath,
                        journal.OldSession.ReferenceProvenancePath,
                        journal.OldSession,
                        _templateService);

                    RestoreReferenceImageFailClosed(
                        journal.BackupReferencePath,
                        journal.OldSession.ReferenceDestinationPath,
                        journal.OldSession.ReferenceHash);

                    // Clean up any partially promoted new files
                    if (File.Exists(journal.NewSession.ReferenceDestinationPath))
                    {
                        File.Delete(journal.NewSession.ReferenceDestinationPath);
                    }
                    if (File.Exists(journal.NewSession.ReferenceProvenancePath)
                        && !ValidationService.PathsEqual(
                            journal.NewSession.ReferenceProvenancePath,
                            journal.OldSession.ReferenceProvenancePath))
                    {
                        File.Delete(journal.NewSession.ReferenceProvenancePath);
                    }

                    _sessionService.Save(journal.OldSession);
                    _sessionService.DeleteReplacementJournal();
                    AddStatus("Interrupted reference replacement (Phase NewPromotionPending) was restored to previous reference.");
                    break;

                case ReferenceReplacementPhase.NewPromoted:
                    var currentSessionForNewPromoted = _sessionService.Exists() ? _sessionService.Load() : null;
                    var sessionSwitchedForNewPromoted = currentSessionForNewPromoted != null &&
                        string.Equals(currentSessionForNewPromoted.ReferenceFilename, journal.NewSession.ReferenceFilename, StringComparison.Ordinal);

                    if (sessionSwitchedForNewPromoted)
                    {
                        // Session already switched to new; commit forward
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
                        // Session not yet switched; roll back to old
                        RestoreReferenceProvenanceFailClosed(
                            journal.BackupProvenancePath,
                            journal.OldSession.ReferenceProvenancePath,
                            journal.OldSession,
                            _templateService);

                        RestoreReferenceImageFailClosed(
                            journal.BackupReferencePath,
                            journal.OldSession.ReferenceDestinationPath,
                            journal.OldSession.ReferenceHash);

                        // Clean up new promoted files
                        if (File.Exists(journal.NewSession.ReferenceDestinationPath))
                        {
                            File.Delete(journal.NewSession.ReferenceDestinationPath);
                        }

                        _sessionService.Save(journal.OldSession);
                        _sessionService.DeleteReplacementJournal();
                        AddStatus("Interrupted reference replacement (Phase NewPromoted) was rolled back to previous reference.");
                    }
                    break;

                case ReferenceReplacementPhase.SessionSwitchPending:
                    var currentSessionForSwitch = _sessionService.Exists() ? _sessionService.Load() : null;
                    var alreadySwitched = currentSessionForSwitch != null &&
                        string.Equals(currentSessionForSwitch.ReferenceFilename, journal.NewSession.ReferenceFilename, StringComparison.Ordinal);

                    if (alreadySwitched)
                    {
                        // Session.json was written before journal phase updated; commit forward
                        var exactNew2 = _validationService.ValidateExactReferenceOutput(journal.NewSession, _templateService);
                        if (!exactNew2.IsValid)
                        {
                            throw new InvalidDataException("New reference files are invalid or modified: " + string.Join("; ", exactNew2.Errors));
                        }
                        if (File.Exists(journal.BackupReferencePath)) File.Delete(journal.BackupReferencePath);
                        if (File.Exists(journal.BackupProvenancePath)) File.Delete(journal.BackupProvenancePath);
                        _sessionService.DeleteReplacementJournal();
                        AddStatus("Interrupted reference replacement (Phase SessionSwitchPending, session already updated) completed.");
                    }
                    else
                    {
                        // Session not yet switched; roll back
                        RestoreReferenceProvenanceFailClosed(
                            journal.BackupProvenancePath,
                            journal.OldSession.ReferenceProvenancePath,
                            journal.OldSession,
                            _templateService);

                        RestoreReferenceImageFailClosed(
                            journal.BackupReferencePath,
                            journal.OldSession.ReferenceDestinationPath,
                            journal.OldSession.ReferenceHash);

                        if (File.Exists(journal.NewSession.ReferenceDestinationPath))
                        {
                            File.Delete(journal.NewSession.ReferenceDestinationPath);
                        }

                        _sessionService.Save(journal.OldSession);
                        _sessionService.DeleteReplacementJournal();
                        AddStatus("Interrupted reference replacement (Phase SessionSwitchPending) was rolled back to previous reference.");
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

                default:
                    // R2-001: Unknown phase must fail closed — never silently continue
                    throw new InvalidDataException(
                        $"Unknown ReferenceReplacementPhase value: {(int)journal.Phase}");
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
            return false;
        }

        return true;
    }

    /// <summary>
    /// R2-001: Fail-closed reference IMAGE restoration.
    /// Verifies backup hash ownership before restoring. If destination already contains the
    /// expected image (idempotent recovery), deletes backup only. Never overwrites unknown content.
    /// </summary>
    private static void RestoreReferenceImageFailClosed(
        string backupPath,
        string destinationPath,
        string expectedHash)
    {
        if (!File.Exists(backupPath))
        {
            return;
        }

        var backupHash = ValidationService.ComputeSha256(backupPath);

        if (!string.Equals(backupHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Reference backup image at '{backupPath}' hash does not match journal authority. Refusing to restore.");
        }

        if (File.Exists(destinationPath))
        {
            var destinationHash = ValidationService.ComputeSha256(destinationPath);

            if (string.Equals(destinationHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                // Destination already is the desired old reference; clean up backup only
                File.Delete(backupPath);
                return;
            }

            throw new InvalidDataException(
                $"Destination '{destinationPath}' contains unknown content (hash mismatch). Refusing to overwrite it.");
        }

        File.Move(backupPath, destinationPath, overwrite: false);
    }

    /// <summary>
    /// R2-001: Fail-closed reference PROVENANCE restoration.
    /// Verifies backup provenance ownership via exact ownership check before restoring.
    /// Never overwrites unknown content.
    /// </summary>
    private void RestoreReferenceProvenanceFailClosed(
        string backupPath,
        string destinationPath,
        AssetSession oldSession,
        TemplateService templateService)
    {
        if (!File.Exists(backupPath))
        {
            return;
        }

        var provValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
            oldSession,
            backupPath,
            templateService);

        if (!provValidation.IsValid)
        {
            throw new InvalidDataException(
                $"Reference backup provenance at '{backupPath}' does not match old session state: "
                + string.Join("; ", provValidation.Errors));
        }

        if (File.Exists(destinationPath))
        {
            var destValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                oldSession,
                destinationPath,
                templateService);

            if (destValidation.IsValid)
            {
                // Destination already is the desired old provenance; clean up backup only
                File.Delete(backupPath);
                return;
            }

            throw new InvalidDataException(
                $"Destination provenance '{destinationPath}' contains unknown content. Refusing to overwrite it.");
        }

        File.Move(backupPath, destinationPath, overwrite: false);
    }
}


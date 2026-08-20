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

        var resumeValidation = _validationService.ValidateExactReferenceOutput(session, _templateService);
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
    /// R3-001/R3-002/R3-003/R3-005: Validates the replacement journal structure and session authority,
    /// then either rolls back or commits forward using ownership-checked processor methods.
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
            journal = _sessionService.LoadReplacementJournal()
                ?? throw new InvalidDataException("Replacement journal is empty.");
        }
        catch (Exception ex)
        {
            return FailReplacementRecovery("Replacement journal could not be read.", ex);
        }

        ValidationResult structural;
        try
        {
            structural = _validationService.ValidateReferenceReplacementJournal(journal);
        }
        catch (Exception ex)
        {
            return FailReplacementRecovery("Replacement journal validation threw unexpectedly.", ex);
        }

        if (!structural.IsValid)
        {
            return FailReplacementRecovery(
                string.Join(Environment.NewLine, structural.Errors));
        }

        AssetSession? current = null;
        if (_sessionService.Exists())
        {
            try
            {
                current = _sessionService.Load();
            }
            catch (Exception ex)
            {
                return FailReplacementRecovery(
                    "session.json could not be read while a replacement journal exists.",
                    ex);
            }
        }

        var oldAuthority = MatchesReferenceAuthority(current, journal.OldSession);
        var newAuthority = MatchesReferenceAuthority(current, journal.NewSession);

        if (oldAuthority && newAuthority)
        {
            return FailReplacementRecovery(
                "Old and New replacement authorities are not distinguishable.");
        }

        switch (journal.Phase)
        {
            case ReferenceReplacementPhase.Prepared:
            case ReferenceReplacementPhase.OldBackupPending:
            case ReferenceReplacementPhase.OldBackedUp:
            case ReferenceReplacementPhase.NewPromotionPending:
                if (!oldAuthority)
                {
                    return FailReplacementRecovery(
                        "Durable session does not match OLD authority for rollback phase.");
                }

                return RollBackReplacementJournal(journal);

            case ReferenceReplacementPhase.NewPromoted:
            case ReferenceReplacementPhase.SessionSwitchPending:
                if (oldAuthority)
                {
                    return RollBackReplacementJournal(journal);
                }

                if (newAuthority)
                {
                    return FinishReplacementCommit(journal, current);
                }

                return FailReplacementRecovery(
                    "Boundary phase has neither OLD nor NEW durable session authority.");

            case ReferenceReplacementPhase.SessionSwitched:
            case ReferenceReplacementPhase.CleanupPending:
                if (!newAuthority)
                {
                    return FailReplacementRecovery(
                        "Durable session does not match NEW authority for commit phase.");
                }

                return FinishReplacementCommit(journal, current);

            default:
                return FailReplacementRecovery(
                    $"Unknown replacement phase: {(int)journal.Phase}");
        }
    }

    private static bool IsStableReferenceAuthority(
        AssetSession? session)
    {
        return session is not null
            && session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted
            && !session.IsMainCommitting
            && session.CancelPhase == CancelPhase.None
            && string.IsNullOrWhiteSpace(session.CancellationId)
            && session.ReferenceCommitPhase == ReferenceCommitPhase.None
            && string.IsNullOrWhiteSpace(session.ReferenceTransactionId);
    }

    private static bool MatchesReferenceAuthority(
        AssetSession? actual,
        AssetSession expected)
    {
        if (!IsStableReferenceAuthority(actual) || !IsStableReferenceAuthority(expected))
        {
            return false;
        }

        var provHashMatches = string.Equals(
            actual!.ReferenceProvenanceHash,
            expected.ReferenceProvenanceHash,
            StringComparison.OrdinalIgnoreCase)
            || (actual.ReferenceProvenanceHash is null && expected.ReferenceProvenanceHash is not null)
            || (actual.ReferenceProvenanceHash is not null && expected.ReferenceProvenanceHash is null);

        return
            actual.WorkflowMode == expected.WorkflowMode
            && string.Equals(
                actual.ProjectName,
                expected.ProjectName,
                StringComparison.Ordinal)
            && ValidationService.PathsEqual(
                actual.AssetRootFolder,
                expected.AssetRootFolder)
            && string.Equals(
                actual.AssetFolderName,
                expected.AssetFolderName,
                StringComparison.Ordinal)
            && ValidationService.PathsEqual(
                actual.AssetFolder,
                expected.AssetFolder)
            && string.Equals(
                actual.ReferenceFilename,
                expected.ReferenceFilename,
                StringComparison.Ordinal)
            && ValidationService.PathsEqual(
                actual.ReferenceDestinationPath,
                expected.ReferenceDestinationPath)
            && ValidationService.PathsEqual(
                actual.ReferenceProvenancePath,
                expected.ReferenceProvenancePath)
            && string.Equals(
                actual.ReferenceHash,
                expected.ReferenceHash,
                StringComparison.OrdinalIgnoreCase)
            && provHashMatches
            && actual.ReferenceProcessedAt.EqualsExact(expected.ReferenceProcessedAt);
    }

    private static ReferenceReplacementTransaction TransactionFromJournal(
        ReferenceReplacementJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        return new ReferenceReplacementTransaction
        {
            TransactionId = journal.TransactionId,
            OldSession = journal.OldSession,
            NewSession = journal.NewSession,
            BackupReferencePath = journal.BackupReferencePath,
            BackupProvenancePath = journal.BackupProvenancePath,
            TempNewReferencePath = journal.TempNewReferencePath,
            TempNewProvenancePath = journal.TempNewProvenancePath
        };
    }

    private bool RollBackReplacementJournal(
        ReferenceReplacementJournal journal)
    {
        var transaction = TransactionFromJournal(journal);

        var authorityResult = _assetProcessorService.EnsureOldProvenanceByteAuthority(transaction);
        if (!authorityResult.IsValid)
        {
            return FailReplacementRecovery(
                $"Could not establish byte authority for replacement rollback:\n{string.Join(Environment.NewLine, authorityResult.Errors)}");
        }

        if (journal.OldSession.ReferenceProvenanceHash != transaction.OldSession.ReferenceProvenanceHash)
        {
            journal.OldSession.ReferenceProvenanceHash = transaction.OldSession.ReferenceProvenanceHash;
            try
            {
                _sessionService.SaveReplacementJournal(journal);
            }
            catch (Exception ex)
            {
                return FailReplacementRecovery("Could not persist upgraded replacement journal before rollback.", ex);
            }
        }

        var rollback = _assetProcessorService.RollbackReferenceReplacement(transaction);

        if (!rollback.IsValid)
        {
            ShowMessageBox(
                "CRITICAL: The interrupted Reference replacement could not be safely rolled back."
                + Environment.NewLine
                + Environment.NewLine
                + string.Join(Environment.NewLine, rollback.Errors)
                + Environment.NewLine
                + Environment.NewLine
                + "The replacement journal was preserved.",
                "Critical Replacement Recovery Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            Close();
            return false;
        }

        try
        {
            _sessionService.Save(journal.OldSession);
            _sessionService.DeleteReplacementJournal();
        }
        catch (Exception ex)
        {
            ShowError(
                "CRITICAL: Replacement files were rolled back, but the old durable session/journal state could not be finalized.",
                ex);

            Close();
            return false;
        }

        AddStatus("Interrupted Reference replacement was rolled back to the previous Reference.");
        return true;
    }

    private bool FinishReplacementCommit(
        ReferenceReplacementJournal journal,
        AssetSession? current)
    {
        if (!MatchesReferenceAuthority(current, journal.NewSession))
        {
            return FailReplacementRecovery("session.json does not match NewSession authority.");
        }

        var exactNew = _validationService.ValidateExactReferenceOutput(
            journal.NewSession,
            _templateService);

        if (!exactNew.IsValid)
        {
            return FailReplacementRecovery(
                string.Join(Environment.NewLine, exactNew.Errors));
        }

        var transaction = TransactionFromJournal(journal);

        var authorityResult = _assetProcessorService.EnsureOldProvenanceByteAuthority(transaction);
        if (!authorityResult.IsValid)
        {
            return FailReplacementRecovery(
                $"Could not establish byte authority for replacement cleanup:\n{string.Join(Environment.NewLine, authorityResult.Errors)}");
        }

        if (journal.OldSession.ReferenceProvenanceHash != transaction.OldSession.ReferenceProvenanceHash)
        {
            journal.OldSession.ReferenceProvenanceHash = transaction.OldSession.ReferenceProvenanceHash;
            try
            {
                _sessionService.SaveReplacementJournal(journal);
            }
            catch (Exception ex)
            {
                return FailReplacementRecovery("Could not persist upgraded replacement journal before cleanup.", ex);
            }
        }

        var cleanup = _assetProcessorService.CleanupReplacementBackups(transaction);

        if (!cleanup.IsValid)
        {
            return FailReplacementRecovery(
                string.Join(Environment.NewLine, cleanup.Errors));
        }

        try
        {
            _sessionService.DeleteReplacementJournal();
        }
        catch (Exception ex)
        {
            ShowError(
                "Replacement cleanup succeeded but the journal could not be deleted.",
                ex);

            Close();
            return false;
        }

        AddStatus("Interrupted Reference replacement cleanup completed.");
        return true;
    }

    private bool FailReplacementRecovery(string message, Exception? ex = null)
    {
        var detail = ex is not null
            ? $"{message}\n\n{ex.Message}"
            : message;

        ShowMessageBox(
            "CRITICAL: Failed to recover interrupted reference replacement.\n\n"
            + detail
            + "\n\nReplacement journal was preserved.",
            "Critical Replacement Recovery Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        Close();
        return false;
    }
}


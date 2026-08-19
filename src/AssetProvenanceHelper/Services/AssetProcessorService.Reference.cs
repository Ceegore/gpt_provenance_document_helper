using System.Globalization;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed partial class AssetProcessorService
{
    public AssetSession CreateReferenceSession(
        AppSettings settings,
        string assetFolderName,
        string sourceImagePath,
        DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (ValidationService.IsReparsePoint(settings.AssetRootFolder))
        {
            throw new IOException(
                $"Asset root folder is a reparse point (junction or symbolic link): {settings.AssetRootFolder}");
        }

        var settingsValidation = _validationService.ValidateSettings(settings);
        if (!settingsValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(Environment.NewLine, settingsValidation.Errors));
        }

        var folderValidation = _validationService.ValidateAssetName(assetFolderName, settings.AcceptedExtensions);
        if (!folderValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(Environment.NewLine, folderValidation.Errors));
        }

        var imageValidation = _validationService.ValidateImageFile(sourceImagePath, settings.AcceptedExtensions);
        if (!imageValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(Environment.NewLine, imageValidation.Errors));
        }

        var normalizedRoot = ValidationService.NormalizePath(settings.AssetRootFolder);
        var expectedAssetFolder = ValidationService.NormalizePath(Path.Combine(settings.AssetRootFolder, assetFolderName));
        var actualParent = Path.GetDirectoryName(expectedAssetFolder);
        if (actualParent is null || !ValidationService.PathsEqual(actualParent, normalizedRoot))
        {
            throw new InvalidDataException("Asset folder is not a direct child of AssetRootFolder.");
        }

        var assetFolder = expectedAssetFolder;
        var referenceFolder = ValidationService.NormalizePath(Path.Combine(assetFolder, AppConstants.ReferenceFolderName));
        var referenceFilename = Path.GetFileName(sourceImagePath);
        var referenceDestination = ValidationService.NormalizePath(Path.Combine(referenceFolder, referenceFilename));
        var referenceProvenance = ValidationService.NormalizePath(Path.Combine(referenceFolder, AppConstants.ReferenceProvenanceFileName));

        var assetFolderExisted = Directory.Exists(assetFolder);
        var referenceFolderExisted = Directory.Exists(referenceFolder);

        if (assetFolderExisted && ValidationService.IsReparsePoint(assetFolder))
        {
            throw new IOException($"Asset folder is a reparse point (junction or symbolic link): {assetFolder}");
        }

        if (referenceFolderExisted && ValidationService.IsReparsePoint(referenceFolder))
        {
            throw new IOException($"Reference folder is a reparse point (junction or symbolic link): {referenceFolder}");
        }

        if (File.Exists(referenceDestination))
        {
            throw new IOException($"Reference destination already exists: {referenceDestination}");
        }

        if (File.Exists(referenceProvenance))
        {
            throw new IOException($"Reference provenance already exists: {referenceProvenance}");
        }

        var sourceHash = ComputeSha256(sourceImagePath);
        var generationDate = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var projectLabel = AssetNaming.DeriveProjectLabel(settings.AssetRootFolder);
        var provenance = _templateService.RenderReference(referenceFilename, projectLabel, generationDate);
        var provHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new System.Text.UTF8Encoding(false).GetBytes(provenance))).ToLowerInvariant();

        return new AssetSession
        {
            WorkflowMode = AssetWorkflowMode.ReferenceAssisted,
            ReferenceCommitPhase = ReferenceCommitPhase.Prepared,
            ReferenceTransactionId = Guid.NewGuid().ToString("N"),
            ProjectName = projectLabel,
            AssetRootFolder = settings.AssetRootFolder,
            AssetFolderName = assetFolderName,
            AssetFolder = assetFolder,
            ReferenceSourcePath = sourceImagePath,
            ReferenceDestinationPath = referenceDestination,
            ReferenceFilename = referenceFilename,
            ReferenceProvenancePath = referenceProvenance,
            ReferenceHash = sourceHash,
            ReferenceProvenanceHash = provHash,
            ReferenceProcessedAt = processedAt,
            WasAssetFolderCreatedByTool = !assetFolderExisted,
            WasReferenceFolderCreatedByTool = !referenceFolderExisted
        };
    }

    public AssetSession ProcessReference(
        AssetSession session,
        AppSettings settings,
        string sourceImagePath,
        DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);

        if (session.ReferenceCommitPhase != ReferenceCommitPhase.Prepared)
        {
            throw new InvalidOperationException("ProcessReference requires a prepared Reference session.");
        }

        var pathValidation = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
        if (!pathValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, pathValidation.Errors));
        }

        var assetFolder = session.AssetFolder;
        var referenceFolder = Path.Combine(assetFolder, AppConstants.ReferenceFolderName);
        var referenceDestination = session.ReferenceDestinationPath;
        var referenceProvenance = session.ReferenceProvenancePath;
        var referenceFilename = session.ReferenceFilename;

        var assetFolderExisted = !session.WasAssetFolderCreatedByTool;
        var referenceFolderExisted = !session.WasReferenceFolderCreatedByTool;

        var imageCopied = false;
        var provenanceWritten = false;
        string? sourceHash = session.ReferenceHash;
        string? provenance = null;

        try
        {
            if (File.Exists(referenceDestination))
            {
                throw new IOException($"Reference destination already exists: {referenceDestination}");
            }

            if (File.Exists(referenceProvenance))
            {
                throw new IOException($"Reference provenance already exists: {referenceProvenance}");
            }

            Directory.CreateDirectory(assetFolder);
            Directory.CreateDirectory(referenceFolder);

            sourceHash = ComputeSha256(sourceImagePath);

            CopyFileWithoutOverwrite(sourceImagePath, referenceDestination);
            imageCopied = true;
            OnFileCopiedHook?.Invoke(sourceImagePath, referenceDestination);

            var copiedValidation = _validationService.ValidateImageFile(referenceDestination, settings.AcceptedExtensions);
            if (!copiedValidation.IsValid)
            {
                throw new InvalidDataException("Copied reference image is invalid: " + string.Join("; ", copiedValidation.Errors));
            }

            var hash = ComputeSha256(referenceDestination);
            if (!string.Equals(sourceHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Reference source image changed during copy.");
            }

            var generationDate = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            provenance = _templateService.RenderReference(referenceFilename, session.ProjectName, generationDate);

            WriteTextAtomic(referenceProvenance, provenance);
            provenanceWritten = true;

            var validation = _validationService.ValidateExactReferenceOutput(session, _templateService);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
            }

            session.ReferenceCommitPhase = ReferenceCommitPhase.None;
            session.ReferenceTransactionId = null;

            return session;
        }
        catch (Exception primaryException)
        {
            var rollbackErrors = new List<string>();

            if (provenanceWritten)
            {
                if (provenance is not null && TryVerifyTextFileOwnership(referenceProvenance, provenance))
                {
                    TryDeleteFileWithError(referenceProvenance, rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add($"Reference provenance at '{referenceProvenance}' content no longer matches tool-written provenance. File preserved.");
                }
            }

            if (imageCopied)
            {
                if (sourceHash is not null && TryVerifyFileHashOwnership(referenceDestination, sourceHash))
                {
                    TryDeleteFileWithError(referenceDestination, rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add($"Reference image at '{referenceDestination}' hash no longer matches expected hash. File preserved.");
                }
            }

            if (!referenceFolderExisted && Directory.Exists(referenceFolder))
            {
                TryDeleteEmptyDirectoryWithError(referenceFolder, rollbackErrors);
            }

            if (!assetFolderExisted && Directory.Exists(assetFolder))
            {
                TryDeleteEmptyDirectoryWithError(assetFolder, rollbackErrors);
            }

            if (rollbackErrors.Count > 0)
            {
                throw new IOException(
                    "Reference processing failed and automatic rollback was incomplete."
                    + Environment.NewLine + Environment.NewLine
                    + "Primary error:" + Environment.NewLine + primaryException.Message
                    + Environment.NewLine + Environment.NewLine
                    + "Rollback errors:" + Environment.NewLine + string.Join(Environment.NewLine, rollbackErrors),
                    primaryException);
            }

            throw;
        }
    }

    /// <summary>
    /// Test convenience overload. Internalized to prevent production callers from bypassing durable session persistence (R2-006).
    /// </summary>
    internal AssetSession ProcessReference(
        AppSettings settings,
        string assetFolderName,
        string sourceImagePath,
        DateTimeOffset processedAt)
    {
        var session = CreateReferenceSession(settings, assetFolderName, sourceImagePath, processedAt);
        return ProcessReference(session, settings, sourceImagePath, processedAt);
    }

    public ValidationResult RollbackReference(
        AssetSession session)
    {
        // BUG-013 & BUG-R13-003: Validate full session path hierarchy and verify exact ownership before deletion
        var ownershipValidation =
            _validationService.ValidateReferenceOwnershipForDeletion(
                session,
                session.ReferenceDestinationPath,
                session.ReferenceProvenancePath,
                _templateService);

        if (!ownershipValidation.IsValid)
        {
            return ownershipValidation;
        }

        var errors =
            new List<string>();

        var normalizedAssetFolder = ValidationService.NormalizePath(session.AssetFolder);
        var expectedReferenceFolder = ValidationService.NormalizePath(Path.Combine(normalizedAssetFolder, AppConstants.ReferenceFolderName));

        if (!ValidationService.PathsEqual(Path.GetDirectoryName(ValidationService.NormalizePath(session.ReferenceDestinationPath)) ?? "", expectedReferenceFolder) ||
            !ValidationService.PathsEqual(Path.GetDirectoryName(ValidationService.NormalizePath(session.ReferenceProvenancePath)) ?? "", expectedReferenceFolder))
        {
            return ValidationResult.Failure("Reference paths escape expected reference folder.");
        }

        TryDeleteFileWithError(
            session.ReferenceProvenancePath,
            errors);

        TryDeleteFileWithError(
            session.ReferenceDestinationPath,
            errors);

        var referenceFolder =
            Path.Combine(
                session.AssetFolder,
                AppConstants.ReferenceFolderName);

        if (session.WasReferenceFolderCreatedByTool)
        {
            TryDeleteEmptyDirectoryWithError(
                referenceFolder,
                errors);
        }

        if (session.WasAssetFolderCreatedByTool)
        {
            TryDeleteEmptyDirectoryWithError(
                session.AssetFolder,
                errors);
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    /// <summary>
    /// R2-002: Creates a reference replacement transaction authority in memory without performing filesystem mutations.
    /// </summary>
    public ReferenceReplacementTransaction CreateReferenceReplacementTransaction(
        AssetSession oldSession,
        IReadOnlyCollection<string> acceptedExtensions,
        string newSourceImagePath,
        DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(oldSession);
        ArgumentNullException.ThrowIfNull(acceptedExtensions);
        ArgumentNullException.ThrowIfNull(newSourceImagePath);

        var oldValidation = _validationService.ValidateSession(oldSession);
        if (!oldValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, oldValidation.Errors));
        }

        var imageValidation = _validationService.ValidateImageFile(newSourceImagePath, acceptedExtensions);
        if (!imageValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, imageValidation.Errors));
        }

        var referenceFolder = Path.Combine(oldSession.AssetFolder, AppConstants.ReferenceFolderName);
        var newFilename = Path.GetFileName(newSourceImagePath);
        var newFinalReferencePath = Path.Combine(referenceFolder, newFilename);
        var finalProvenancePath = oldSession.ReferenceProvenancePath;

        if (!ValidationService.PathsEqual(newFinalReferencePath, oldSession.ReferenceDestinationPath) &&
            File.Exists(newFinalReferencePath))
        {
            throw new IOException($"Replacement reference destination already exists: {newFinalReferencePath}");
        }

        var id = Guid.NewGuid().ToString("N");
        var extension = Path.GetExtension(newFilename);
        var tempReferencePath = Path.Combine(referenceFolder, $".__new_reference_{id}{extension}");
        var tempProvenancePath = Path.Combine(referenceFolder, $".__new_provenance_{id}.tmp");
        var backupReferencePath = oldSession.ReferenceDestinationPath + "." + id + ".old";
        var backupProvenancePath = oldSession.ReferenceProvenancePath + "." + id + ".old";

        var sourceHash = ComputeSha256(newSourceImagePath);
        var generationDate = processedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var newProvenance = _templateService.RenderReference(newFilename, oldSession.ProjectName, generationDate);
        var newProvHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new System.Text.UTF8Encoding(false).GetBytes(newProvenance))).ToLowerInvariant();

        var newSession = new AssetSession
        {
            ProjectName = oldSession.ProjectName,
            AssetRootFolder = oldSession.AssetRootFolder,
            AssetFolderName = oldSession.AssetFolderName,
            AssetFolder = oldSession.AssetFolder,
            ReferenceSourcePath = newSourceImagePath,
            ReferenceDestinationPath = newFinalReferencePath,
            ReferenceFilename = newFilename,
            ReferenceProvenancePath = finalProvenancePath,
            ReferenceHash = sourceHash,
            ReferenceProvenanceHash = newProvHash,
            ReferenceProcessedAt = processedAt,
            WasAssetFolderCreatedByTool = oldSession.WasAssetFolderCreatedByTool,
            WasReferenceFolderCreatedByTool = oldSession.WasReferenceFolderCreatedByTool
        };

        return new ReferenceReplacementTransaction
        {
            TransactionId = id,
            OldSession = oldSession,
            NewSession = newSession,
            BackupReferencePath = backupReferencePath,
            BackupProvenancePath = backupProvenancePath,
            TempNewReferencePath = tempReferencePath,
            TempNewProvenancePath = tempProvenancePath
        };
    }

    /// <summary>
    /// R2-002: Creates temporary replacement reference image and provenance files on disk and verifies integrity.
    /// </summary>
    public void CreateReplacementTempFiles(
        ReferenceReplacementTransaction transaction,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(acceptedExtensions);

        var sourceHash = ComputeSha256(transaction.NewSession.ReferenceSourcePath);
        CopyFileWithoutOverwrite(transaction.NewSession.ReferenceSourcePath, transaction.TempNewReferencePath);
        OnFileCopiedHook?.Invoke(transaction.NewSession.ReferenceSourcePath, transaction.TempNewReferencePath);

        var copiedValidation = _validationService.ValidateImageFile(transaction.TempNewReferencePath, acceptedExtensions);
        if (!copiedValidation.IsValid)
        {
            throw new InvalidDataException("Copied replacement reference image is invalid: " + string.Join("; ", copiedValidation.Errors));
        }

        var newHash = ComputeSha256(transaction.TempNewReferencePath);
        if (!string.Equals(sourceHash, newHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Replacement reference image changed during copy.");
        }

        var generationDate = transaction.NewSession.ReferenceProcessedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var newProvenance = _templateService.RenderReference(
            transaction.NewSession.ReferenceFilename,
            transaction.OldSession.ProjectName,
            generationDate);

        WriteTextAtomic(transaction.TempNewProvenancePath, newProvenance);
    }

    /// <summary>
    /// R2-002: Moves old canonical reference image and provenance to deterministic backup paths.
    /// </summary>
    public void BackupOldReference(
        ReferenceReplacementTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!File.Exists(transaction.OldSession.ReferenceDestinationPath))
        {
            throw new IOException($"Old reference image does not exist: {transaction.OldSession.ReferenceDestinationPath}");
        }

        var oldRefHash = ComputeSha256(transaction.OldSession.ReferenceDestinationPath);
        if (!string.Equals(oldRefHash, transaction.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Old reference image on disk does not match session ReferenceHash (expected {transaction.OldSession.ReferenceHash}, found {oldRefHash}).");
        }

        var oldProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
            transaction.OldSession,
            transaction.OldSession.ReferenceProvenancePath,
            _templateService);

        if (!oldProvValidation.IsValid)
        {
            if (oldProvValidation.Errors.Any(e => e.StartsWith("Could not read", StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException(
                    $"Could not verify old reference provenance ownership before backup: {string.Join("; ", oldProvValidation.Errors)}");
            }

            throw new InvalidDataException(
                $"Old reference provenance on disk does not match expected session provenance: {string.Join("; ", oldProvValidation.Errors)}");
        }

        File.Move(
            transaction.OldSession.ReferenceDestinationPath,
            transaction.BackupReferencePath,
            overwrite: false);

        File.Move(
            transaction.OldSession.ReferenceProvenancePath,
            transaction.BackupProvenancePath,
            overwrite: false);

        OnPrepareReplacementOldBackedUpHook?.Invoke(
            transaction.BackupReferencePath,
            transaction.BackupProvenancePath);
    }

    /// <summary>
    /// R2-002: Promotes temporary replacement files to canonical destination paths.
    /// </summary>
    public void PromoteNewReference(
        ReferenceReplacementTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        File.Move(
            transaction.TempNewReferencePath,
            transaction.NewSession.ReferenceDestinationPath,
            overwrite: false);

        File.Move(
            transaction.TempNewProvenancePath,
            transaction.NewSession.ReferenceProvenancePath,
            overwrite: false);

        var validation = _validationService.ValidateExactReferenceOutput(
            transaction.NewSession,
            _templateService);

        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
        }
    }

    /// <summary>
    /// R2-002: Cleans up old backup files after successful commit. Alias for CommitReferenceReplacement.
    /// </summary>
    public ValidationResult CleanupReplacementBackups(
        ReferenceReplacementTransaction transaction)
    {
        return CommitReferenceReplacement(transaction);
    }

    public ReferenceReplacementTransaction PrepareReferenceReplacement(
        AssetSession oldSession,
        IReadOnlyCollection<string> acceptedExtensions,
        string newSourceImagePath,
        DateTimeOffset processedAt)
    {
        var tx = CreateReferenceReplacementTransaction(oldSession, acceptedExtensions, newSourceImagePath, processedAt);
        try
        {
            CreateReplacementTempFiles(tx, acceptedExtensions);
            BackupOldReference(tx);
            PromoteNewReference(tx);
            return tx;
        }
        catch (Exception ex)
        {
            var rollback = RollbackReferenceReplacement(tx);
            if (!rollback.IsValid)
            {
                throw new IOException(
                    "Reference replacement failed and automatic rollback was incomplete.\n" +
                    string.Join(Environment.NewLine, rollback.Errors),
                    ex);
            }

            if (ex is IOException ioEx)
            {
                throw new IOException($"Reference replacement failed: {ioEx.Message}", ioEx);
            }

            if (ex is InvalidDataException idEx)
            {
                throw new InvalidDataException($"Reference replacement failed: {idEx.Message}", idEx);
            }

            throw new InvalidOperationException(
                $"Reference replacement failed: {ex.Message}",
                ex);
        }
    }


    public ValidationResult CommitReferenceReplacement(
        ReferenceReplacementTransaction transaction)
    {
        var transactionValidation = _validationService.ValidateReferenceReplacementTransaction(transaction);
        if (!transactionValidation.IsValid)
        {
            return transactionValidation;
        }

        // BUG-R9-004: Re-validate current NewSession reference output (image, hash, provenance) before destroying old backups
        var newOutputValidation = _validationService.ValidateReferenceOutput(transaction.NewSession);
        if (!newOutputValidation.IsValid)
        {
            return ValidationResult.Failure(newOutputValidation.Errors);
        }

        // BUG-R20-002: Verify exact ownership of new reference provenance before destroying old backups
        var exactNewProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
            transaction.NewSession,
            transaction.NewSession.ReferenceProvenancePath,
            _templateService);

        if (!exactNewProvValidation.IsValid)
        {
            return ValidationResult.Failure(exactNewProvValidation.Errors);
        }

        var errors =
            new List<string>();

        // BUG-R13-002: Verify backup ownership and integrity before destroying old backups
        if (File.Exists(transaction.BackupReferencePath))
        {
            try
            {
                var backupHash = ComputeSha256(transaction.BackupReferencePath);
                if (!string.Equals(backupHash, transaction.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Failure(
                        $"Backup reference image at '{transaction.BackupReferencePath}' does not match old session hash. Refusing to delete unknown file.");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure(
                    $"Could not compute backup reference image hash: {ex.Message}");
            }
        }

        if (File.Exists(transaction.BackupProvenancePath))
        {
            var provValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                transaction.OldSession,
                transaction.BackupProvenancePath,
                _templateService);

            if (!provValidation.IsValid)
            {
                return ValidationResult.Failure(
                    $"Backup reference provenance at '{transaction.BackupProvenancePath}' does not match old session provenance. Refusing to delete unknown file.");
            }
        }

        TryDeleteFileWithError(
            transaction.BackupReferencePath,
            errors);

        TryDeleteFileWithError(
            transaction.BackupProvenancePath,
            errors);

        if (errors.Count == 0)
        {
            transaction.IsCommitted = true;
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult RollbackReferenceReplacement(
        ReferenceReplacementTransaction transaction)
    {
        var transactionValidation = _validationService.ValidateReferenceReplacementTransaction(transaction);
        if (!transactionValidation.IsValid)
        {
            return transactionValidation;
        }

        if (transaction.IsCommitted)
        {
            return ValidationResult.Failure(
                "Reference replacement transaction has already been committed and cannot be rolled back.");
        }

        var verificationErrors = new List<string>();

        // Phase A1: Verify Old Provenance Restorability / Integrity
        var backupProvExists = File.Exists(transaction.BackupProvenancePath);
        var oldProvExists = File.Exists(transaction.OldSession.ReferenceProvenancePath);

        if (backupProvExists)
        {
            var backupProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                transaction.OldSession,
                transaction.BackupProvenancePath,
                _templateService);

            if (!backupProvValidation.IsValid)
            {
                verificationErrors.Add($"Backup reference provenance at '{transaction.BackupProvenancePath}' is corrupted or does not match old session state.");
            }
        }
        else if (oldProvExists)
        {
            var oldProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                transaction.OldSession,
                transaction.OldSession.ReferenceProvenancePath,
                _templateService);

            if (!oldProvValidation.IsValid)
            {
                verificationErrors.Add($"Could not restore old reference provenance: backup not found and destination does not match old session state ({string.Join("; ", oldProvValidation.Errors)}).");
            }
        }
        else
        {
            verificationErrors.Add($"Could not restore old reference provenance: backup '{transaction.BackupProvenancePath}' not found and destination does not exist.");
        }

        // Phase A2: Verify Old Reference Image Restorability / Integrity
        var backupRefExists = File.Exists(transaction.BackupReferencePath);
        var oldRefExists = File.Exists(transaction.OldSession.ReferenceDestinationPath);
        var newRefExists = File.Exists(transaction.NewSession.ReferenceDestinationPath);
        var sameFilename = ValidationService.PathsEqual(
            transaction.OldSession.ReferenceDestinationPath,
            transaction.NewSession.ReferenceDestinationPath);

        if (backupRefExists)
        {
            try
            {
                var backupHash = ComputeSha256(transaction.BackupReferencePath);
                if (!string.Equals(backupHash, transaction.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                {
                    verificationErrors.Add($"Backup old reference image at '{transaction.BackupReferencePath}' hash does not match old session ReferenceHash.");
                }
            }
            catch (Exception ex)
            {
                verificationErrors.Add($"Could not verify backup old reference image: {ex.Message}");
            }
        }
        else if (oldRefExists)
        {
            try
            {
                var destHash = ComputeSha256(transaction.OldSession.ReferenceDestinationPath);
                if (!string.Equals(destHash, transaction.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                {
                    verificationErrors.Add($"Could not restore old reference image: backup '{transaction.BackupReferencePath}' not found and destination does not match old reference hash.");
                }
            }
            catch (Exception ex)
            {
                verificationErrors.Add($"Could not verify old reference image destination: {ex.Message}");
            }
        }
        else
        {
            verificationErrors.Add($"Could not restore old reference image: backup '{transaction.BackupReferencePath}' not found and destination does not exist.");
        }

        // Phase A3: Verify Current Reference Destination / New Reference before Delete / Overwrite
        if (sameFilename)
        {
            // When filenames match, the shared destination file is currently the New reference.
            // If backupRefExists, Phase B will delete the shared destination file to restore BackupReferencePath.
            // Therefore, if the destination file exists, it MUST match either NewSession.ReferenceHash (expected state after prepare)
            // or OldSession.ReferenceHash (if already restored in an idempotent partial rollback).
            if (oldRefExists)
            {
                try
                {
                    var destHash = ComputeSha256(transaction.OldSession.ReferenceDestinationPath);
                    var matchesNew = string.Equals(destHash, transaction.NewSession.ReferenceHash, StringComparison.OrdinalIgnoreCase);
                    var matchesOld = string.Equals(destHash, transaction.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase);

                    if (!matchesNew && !matchesOld)
                    {
                        verificationErrors.Add(
                            $"Current reference image at '{transaction.OldSession.ReferenceDestinationPath}' hash does not match new session ReferenceHash or old session ReferenceHash. Refusing to delete unknown file.");
                    }
                }
                catch (Exception ex)
                {
                    verificationErrors.Add($"Could not verify current reference image at '{transaction.OldSession.ReferenceDestinationPath}': {ex.Message}");
                }
            }
        }
        else
        {
            // When filenames differ:
            // 1. If new reference file exists, it will be deleted by Phase B -> must match NewSession.ReferenceHash
            if (newRefExists)
            {
                try
                {
                    var newHash = ComputeSha256(transaction.NewSession.ReferenceDestinationPath);
                    if (!string.Equals(newHash, transaction.NewSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        verificationErrors.Add($"Current new reference image at '{transaction.NewSession.ReferenceDestinationPath}' hash does not match new session ReferenceHash. Refusing to delete unknown file.");
                    }
                }
                catch (Exception ex)
                {
                    verificationErrors.Add($"Could not verify current new reference image: {ex.Message}");
                }
            }

            // 2. If old reference destination exists while backupRefExists, Phase B will delete it to restore backup -> must match OldSession.ReferenceHash
            if (backupRefExists && oldRefExists)
            {
                try
                {
                    var oldHash = ComputeSha256(transaction.OldSession.ReferenceDestinationPath);
                    if (!string.Equals(oldHash, transaction.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        verificationErrors.Add($"Old reference image destination at '{transaction.OldSession.ReferenceDestinationPath}' hash does not match old session ReferenceHash. Refusing to overwrite unknown file.");
                    }
                }
                catch (Exception ex)
                {
                    verificationErrors.Add($"Could not verify old reference image destination: {ex.Message}");
                }
            }
        }

        // Phase A4: Verify Current New Provenance before Overwrite/Delete
        if (backupProvExists && File.Exists(transaction.NewSession.ReferenceProvenancePath))
        {
            var newProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                transaction.NewSession,
                transaction.NewSession.ReferenceProvenancePath,
                _templateService);

            if (!newProvValidation.IsValid)
            {
                // If it doesn't match new session, check if it already matches old session
                var oldMatch = _validationService.ValidateExactReferenceProvenanceOwnership(
                    transaction.OldSession,
                    transaction.NewSession.ReferenceProvenancePath,
                    _templateService);

                if (!oldMatch.IsValid)
                {
                    verificationErrors.Add($"Current reference provenance at '{transaction.NewSession.ReferenceProvenancePath}' does not match new or old session state. Refusing to overwrite unknown file.");
                }
            }
        }

        // BUG-R12-002 & BUG-R13-002: If verification fails, FAIL CLOSED! Do not destroy new reference or any existing files.
        if (verificationErrors.Count > 0)
        {
            return ValidationResult.Failure(verificationErrors);
        }

        // Phase B: Execution / Mutation (Old state is proven restorable and new files verified)
        var errors = new List<string>();

        // Rollback Provenance
        if (backupProvExists)
        {
            TryDeleteFileWithError(
                transaction.OldSession.ReferenceProvenancePath,
                errors);

            TryRestoreFileWithError(
                transaction.BackupProvenancePath,
                transaction.OldSession.ReferenceProvenancePath,
                "old reference provenance",
                errors);
        }

        // Rollback Reference Image
        if (backupRefExists)
        {
            if (!sameFilename && newRefExists)
            {
                TryDeleteFileWithError(
                    transaction.NewSession.ReferenceDestinationPath,
                    errors);
            }

            if (oldRefExists)
            {
                TryDeleteFileWithError(
                    transaction.OldSession.ReferenceDestinationPath,
                    errors);
            }

            TryRestoreFileWithError(
                transaction.BackupReferencePath,
                transaction.OldSession.ReferenceDestinationPath,
                "old reference image",
                errors);
        }
        else
        {
            // Old reference destination was verified intact on disk; clean up new reference file if different
            if (!sameFilename && newRefExists)
            {
                TryDeleteFileWithError(
                    transaction.NewSession.ReferenceDestinationPath,
                    errors);
            }
        }

        // Clean up temporary files if they match expected ownership
        if (!string.IsNullOrWhiteSpace(transaction.TempNewReferencePath) && File.Exists(transaction.TempNewReferencePath))
        {
            try
            {
                var tempRefHash = ComputeSha256(transaction.TempNewReferencePath);
                if (string.Equals(tempRefHash, transaction.NewSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFileWithError(transaction.TempNewReferencePath, errors);
                }
            }
            catch
            {
                // Preserve unknown/unreadable temp
            }
        }

        if (!string.IsNullOrWhiteSpace(transaction.TempNewProvenancePath) && File.Exists(transaction.TempNewProvenancePath))
        {
            var tempProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                transaction.NewSession,
                transaction.TempNewProvenancePath,
                _templateService);

            if (tempProvValidation.IsValid)
            {
                TryDeleteFileWithError(transaction.TempNewProvenancePath, errors);
            }
        }

        if (errors.Count > 0)
        {
            return ValidationResult.Failure(errors);
        }

        // Post-mutation validation: ensure old session reference output is fully restored and valid
        var postValidation = _validationService.ValidateReferenceOutput(transaction.OldSession);
        if (!postValidation.IsValid)
        {
            return ValidationResult.Failure(postValidation.Errors);
        }

        return ValidationResult.Success();
    }
}

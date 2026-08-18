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

    public AssetSession ProcessReference(
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

    public ReferenceReplacementTransaction PrepareReferenceReplacement(
        AssetSession oldSession,
        IReadOnlyCollection<string> acceptedExtensions,
        string newSourceImagePath,
        DateTimeOffset processedAt)
    {
        var oldValidation =
            _validationService
                .ValidateSession(
                    oldSession);

        if (!oldValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(
                    Environment.NewLine,
                    oldValidation.Errors));
        }

        var imageValidation =
            _validationService
                .ValidateImageFile(
                    newSourceImagePath,
                    acceptedExtensions);

        if (!imageValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(
                    Environment.NewLine,
                    imageValidation.Errors));
        }

        var referenceFolder =
            Path.Combine(
                oldSession.AssetFolder,
                AppConstants.ReferenceFolderName);

        var newFilename =
            Path.GetFileName(
                newSourceImagePath);

        var newFinalReferencePath =
            Path.Combine(
                referenceFolder,
                newFilename);

        var finalProvenancePath =
            oldSession.ReferenceProvenancePath;

        if (!ValidationService.PathsEqual(
                newFinalReferencePath,
                oldSession.ReferenceDestinationPath) &&
            File.Exists(
                newFinalReferencePath))
        {
            throw new IOException(
                $"Replacement reference destination already exists: {newFinalReferencePath}");
        }

        var id =
            Guid
                .NewGuid()
                .ToString("N");

        var extension =
            Path.GetExtension(
                newFilename);

        var tempReferencePath =
            Path.Combine(
                referenceFolder,
                $".__new_reference_{id}{extension}");

        var tempProvenancePath =
            Path.Combine(
                referenceFolder,
                $".__new_provenance_{id}.tmp");

        var backupReferencePath =
            oldSession.ReferenceDestinationPath + "." + id + ".old";

        var backupProvenancePath =
            oldSession.ReferenceProvenancePath + "." + id + ".old";

        var oldReferenceMoved =
            false;

        var oldProvenanceMoved =
            false;

        var newReferencePromoted =
            false;

        var newProvenancePromoted =
            false;

        // BUG-R17-001: Declare outside try so catch block can verify ownership
        string? sourceHash = null;
        string? newHash = null;
        string? newProvenance = null;

        try
        {
            sourceHash =
                ComputeSha256(
                    newSourceImagePath);

            CopyFileWithoutOverwrite(
                newSourceImagePath,
                tempReferencePath);

            OnFileCopiedHook?.Invoke(
                newSourceImagePath,
                tempReferencePath);

            var copiedValidation =
                _validationService
                    .ValidateImageFile(
                        tempReferencePath,
                        acceptedExtensions);

            if (!copiedValidation.IsValid)
            {
                throw new InvalidDataException(
                    "Copied replacement reference image is invalid: "
                    + string.Join("; ", copiedValidation.Errors));
            }

            newHash =
                ComputeSha256(
                    tempReferencePath);

            if (!string.Equals(
                    sourceHash,
                    newHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Replacement reference image changed during copy.");
            }

            var generationDate =
                processedAt
                    .ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

            newProvenance =
                _templateService.RenderReference(
                    newFilename,
                    oldSession.ProjectName,
                    generationDate);

            WriteTextAtomic(
                tempProvenancePath,
                newProvenance);

            // BUG-R20-001: Freshly verify old canonical reference image and exact provenance ownership immediately before moving to backup paths
            if (!File.Exists(oldSession.ReferenceDestinationPath))
            {
                throw new IOException(
                    $"Old reference image does not exist: {oldSession.ReferenceDestinationPath}");
            }

            try
            {
                var oldRefHash = ComputeSha256(oldSession.ReferenceDestinationPath);
                if (!string.Equals(oldRefHash, oldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Old reference image on disk does not match session ReferenceHash (expected {oldSession.ReferenceHash}, found {oldRefHash}).");
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Could not verify old reference image before backup: {ex.Message}",
                    ex);
            }

            var oldProvValidation =
                _validationService.ValidateExactReferenceProvenanceOwnership(
                    oldSession,
                    oldSession.ReferenceProvenancePath,
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
                oldSession.ReferenceDestinationPath,
                backupReferencePath,
                overwrite: false);

            oldReferenceMoved =
                true;

            File.Move(
                oldSession.ReferenceProvenancePath,
                backupProvenancePath,
                overwrite: false);

            oldProvenanceMoved =
                true;

            OnPrepareReplacementOldBackedUpHook?.Invoke(
                backupReferencePath,
                backupProvenancePath);

            File.Move(
                tempReferencePath,
                newFinalReferencePath,
                overwrite: false);

            newReferencePromoted =
                true;

            File.Move(
                tempProvenancePath,
                finalProvenancePath,
                overwrite: false);

            newProvenancePromoted =
                true;

            var newProvHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new System.Text.UTF8Encoding(false).GetBytes(newProvenance))).ToLowerInvariant();

            var newSession =
                new AssetSession
                {
                    ProjectName =
                        oldSession.ProjectName,

                    AssetRootFolder =
                        oldSession.AssetRootFolder,

                    AssetFolderName =
                        oldSession.AssetFolderName,

                    AssetFolder =
                        oldSession.AssetFolder,

                    ReferenceSourcePath =
                        newSourceImagePath,

                    ReferenceDestinationPath =
                        newFinalReferencePath,

                    ReferenceFilename =
                        newFilename,

                    ReferenceProvenancePath =
                        finalProvenancePath,

                    ReferenceHash =
                        newHash,

                    ReferenceProvenanceHash =
                        newProvHash,

                    ReferenceProcessedAt =
                        processedAt,

                    WasAssetFolderCreatedByTool =
                        oldSession.WasAssetFolderCreatedByTool,

                    WasReferenceFolderCreatedByTool =
                        oldSession.WasReferenceFolderCreatedByTool
                };

            var validation =
                _validationService
                    .ValidateExactReferenceOutput(
                        newSession,
                        _templateService);

            if (!validation.IsValid)
            {
                throw new InvalidDataException(
                    string.Join(
                        Environment.NewLine,
                        validation.Errors));
            }

            return new ReferenceReplacementTransaction
            {
                TransactionId =
                    id,

                OldSession =
                    oldSession,

                NewSession =
                    newSession,

                BackupReferencePath =
                    backupReferencePath,

                BackupProvenancePath =
                    backupProvenancePath,

                TempNewReferencePath =
                    tempReferencePath,

                TempNewProvenancePath =
                    tempProvenancePath
            };
        }
        catch (Exception primaryException)
        {
            var rollbackErrors =
                new List<string>();

            // BUG-R17-001: Verify current content ownership before deleting promoted provenance
            if (newProvenancePromoted)
            {
                if (newProvenance is not null && TryVerifyTextFileOwnership(finalProvenancePath, newProvenance))
                {
                    TryDeleteFileWithError(
                        finalProvenancePath,
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Replacement reference provenance at '{finalProvenancePath}' content no longer matches tool-written provenance. File preserved.");
                }
            }

            // BUG-R17-001: Verify current content ownership before deleting promoted reference
            if (newReferencePromoted)
            {
                if (sourceHash is not null && TryVerifyFileHashOwnership(newFinalReferencePath, sourceHash))
                {
                    TryDeleteFileWithError(
                        newFinalReferencePath,
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Replacement reference image at '{newFinalReferencePath}' hash no longer matches expected hash. File preserved.");
                }
            }

            // BUG-R20-001: Verify backup ownership and integrity before restoring to canonical paths
            if (oldProvenanceMoved)
            {
                if (File.Exists(backupProvenancePath))
                {
                    var backupProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
                        oldSession,
                        backupProvenancePath,
                        _templateService);

                    if (backupProvValidation.IsValid)
                    {
                        TryRestoreFileWithError(
                            backupProvenancePath,
                            oldSession.ReferenceProvenancePath,
                            "old reference provenance",
                            rollbackErrors);
                    }
                    else
                    {
                        rollbackErrors.Add(
                            $"Old reference provenance backup at '{backupProvenancePath}' content no longer matches tool-written provenance. Backup preserved and not restored.");
                    }
                }
                else
                {
                    rollbackErrors.Add(
                        $"Old reference provenance backup at '{backupProvenancePath}' not found for rollback.");
                }
            }

            if (oldReferenceMoved)
            {
                if (File.Exists(backupReferencePath))
                {
                    if (oldSession.ReferenceHash is not null && TryVerifyFileHashOwnership(backupReferencePath, oldSession.ReferenceHash))
                    {
                        TryRestoreFileWithError(
                            backupReferencePath,
                            oldSession.ReferenceDestinationPath,
                            "old reference image",
                            rollbackErrors);
                    }
                    else
                    {
                        rollbackErrors.Add(
                            $"Old reference image backup at '{backupReferencePath}' hash no longer matches expected hash. Backup preserved and not restored.");
                    }
                }
                else
                {
                    rollbackErrors.Add(
                        $"Old reference image backup at '{backupReferencePath}' not found for rollback.");
                }
            }

            // BUG-R17-001: Verify temp reference ownership before deleting
            if (File.Exists(tempReferencePath) && !newReferencePromoted)
            {
                if (sourceHash is not null && TryVerifyFileHashOwnership(tempReferencePath, sourceHash))
                {
                    TryDeleteFileWithError(
                        tempReferencePath,
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Replacement temp reference image at '{tempReferencePath}' hash no longer matches expected hash. File preserved.");
                }
            }

            // BUG-R17-001: Verify temp provenance ownership before deleting
            if (File.Exists(tempProvenancePath) && !newProvenancePromoted)
            {
                if (newProvenance is not null && TryVerifyTextFileOwnership(tempProvenancePath, newProvenance))
                {
                    TryDeleteFileWithError(
                        tempProvenancePath,
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Replacement temp reference provenance at '{tempProvenancePath}' content no longer matches tool-written provenance. File preserved.");
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new IOException(
                    "Reference replacement failed and automatic rollback was incomplete."
                    + Environment.NewLine
                    + Environment.NewLine
                    + "Primary error:"
                    + Environment.NewLine
                    + primaryException.Message
                    + Environment.NewLine
                    + Environment.NewLine
                    + "Rollback errors:"
                    + Environment.NewLine
                    + string.Join(
                        Environment.NewLine,
                        rollbackErrors),
                    primaryException);
            }

            throw;
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

using System.Globalization;
using System.Text;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed partial class AssetProcessorService
{
    public AssetSession CreateReferenceSession(
        AppSettings settings,
        string assetFolderName,
        string sourceImagePath,
        DateTimeOffset processedAt,
        ProviderTemplateSnapshot? providerTemplate = null,
        string? sourceRequestKey = null)
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
        var projectLabel = AssetNaming.DeriveProjectLabel(settings.AssetRootFolder);

        var session = new AssetSession
        {
            SchemaVersion =
                providerTemplate is null
                    ? 2
                    : 3,

            WorkflowMode = AssetWorkflowMode.ReferenceAssisted,

            ProviderTemplate =
                providerTemplate?.Clone(),

            SourceRequestKey =
                sourceRequestKey,

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
            ReferenceProcessedAt = processedAt,
            WasAssetFolderCreatedByTool = !assetFolderExisted,
            WasReferenceFolderCreatedByTool = !referenceFolderExisted
        };

        var provenance =
            _templateService.RenderReferenceForSession(
                session,
                referenceFilename,
                processedAt);

        session.ReferenceProvenanceHash =
            Convert
                .ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        new UTF8Encoding(false)
                            .GetBytes(provenance)))
                .ToLowerInvariant();

        return session;
    }

    private string RequirePreparedReferenceAuthority(
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

        if (!ValidationService.PathsEqual(sourceImagePath, session.ReferenceSourcePath))
        {
            throw new InvalidOperationException("Reference source path does not match the Prepared session authority.");
        }

        if (!processedAt.EqualsExact(session.ReferenceProcessedAt))
        {
            throw new InvalidOperationException("Reference processedAt does not match the Prepared session authority.");
        }

        var sourceValidation = _validationService.ValidateImageFile(session.ReferenceSourcePath, settings.AcceptedExtensions);
        if (!sourceValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, sourceValidation.Errors));
        }

        var currentSourceHash = ComputeSha256(session.ReferenceSourcePath);
        if (!string.Equals(currentSourceHash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Reference source changed after the Prepared session was persisted.");
        }

        var provenance = _templateService.RenderReferenceForSession(
            session,
            session.ReferenceFilename,
            session.ReferenceProcessedAt);
        var provenanceHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                new System.Text.UTF8Encoding(false).GetBytes(provenance)))
            .ToLowerInvariant();

        if (!string.Equals(provenanceHash, session.ReferenceProvenanceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Reference provenance changed after the Prepared session was persisted.");
        }

        return provenance;
    }

    internal AssetSession ProcessReference(
        AssetSession session,
        AppSettings settings,
        string? sourceImagePath = null,
        DateTimeOffset? processedAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);

        var actualSourcePath = sourceImagePath ?? session.ReferenceSourcePath;
        var actualProcessedAt = processedAt ?? session.ReferenceProcessedAt;

        var verifiedProvenance = RequirePreparedReferenceAuthority(session, settings, actualSourcePath, actualProcessedAt);
        OnPreparedReferenceAuthorityVerifiedHook?.Invoke();

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

        var tempImagePath = session.GetReferenceTempImagePath();
        var tempProvenancePath = session.GetReferenceTempProvenancePath();

        if (string.IsNullOrWhiteSpace(tempImagePath) || string.IsNullOrWhiteSpace(tempProvenancePath))
        {
            throw new InvalidOperationException("Reference temporary paths could not be determined.");
        }

        var assetFolderExisted = !session.WasAssetFolderCreatedByTool;
        var referenceFolderExisted = !session.WasReferenceFolderCreatedByTool;

        var tempImageCopied = false;
        var tempProvenanceWritten = false;
        var imagePromoted = false;
        var provenancePromoted = false;

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

            if (File.Exists(tempImagePath))
            {
                throw new IOException($"Reference temporary image already exists: {tempImagePath}");
            }

            if (File.Exists(tempProvenancePath))
            {
                throw new IOException($"Reference temporary provenance already exists: {tempProvenancePath}");
            }

            Directory.CreateDirectory(assetFolder);
            if (ValidationService.IsReparsePoint(assetFolder))
            {
                throw new IOException($"Asset folder became a reparse point: {assetFolder}");
            }

            Directory.CreateDirectory(referenceFolder);
            if (ValidationService.IsReparsePoint(referenceFolder))
            {
                throw new IOException($"Reference folder became a reparse point: {referenceFolder}");
            }

            var postCreatePaths = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
            if (!postCreatePaths.IsValid)
            {
                throw new InvalidDataException(string.Join(Environment.NewLine, postCreatePaths.Errors));
            }

            // 1. Stage image in deterministic temp path
            CopyFileWithoutOverwrite(actualSourcePath, tempImagePath);
            tempImageCopied = true;
            OnFileCopiedHook?.Invoke(actualSourcePath, tempImagePath);

            var tempImageValidation = _validationService.ValidateImageFile(tempImagePath, settings.AcceptedExtensions);
            if (!tempImageValidation.IsValid)
            {
                throw new InvalidDataException("Copied reference temp image is invalid: " + string.Join("; ", tempImageValidation.Errors));
            }

            var tempImageHash = ComputeSha256(tempImagePath);
            if (!string.Equals(session.ReferenceHash, tempImageHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Copied reference temp image does not match Prepared ReferenceHash.");
            }

            // 2. Stage pre-rendered verified provenance in deterministic temp path
            WriteTextDurablyToReservedPath(tempProvenancePath, verifiedProvenance);
            tempProvenanceWritten = true;

            OnBeforeInitialReferenceStagingAuthorityGate?.Invoke(session);

            RequireInitialReferenceStagingAuthority(session, tempImagePath, tempProvenancePath);

            // 3. Promote staged artifacts to canonical paths
            MoveHashOwnedFileWithoutOverwrite(
                tempImagePath,
                referenceDestination,
                session.ReferenceHash,
                "Reference image",
                () => ValidateSessionDestructivePathSafety(session));
            imagePromoted = true;
            tempImageCopied = false;

            MoveHashOwnedFileWithoutOverwrite(
                tempProvenancePath,
                referenceProvenance,
                session.ReferenceProvenanceHash!,
                "Reference provenance",
                () => ValidateSessionDestructivePathSafety(session));
            provenancePromoted = true;
            tempProvenanceWritten = false;

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
            var rollbackPathSafety = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
            var refFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
            if (!rollbackPathSafety.IsValid || ValidationService.IsReparsePoint(session.AssetFolder) || ValidationService.IsReparsePoint(refFolder))
            {
                var errorDetails = rollbackPathSafety.IsValid
                    ? "Asset or Reference folder is a reparse point."
                    : string.Join(Environment.NewLine, rollbackPathSafety.Errors);

                throw new IOException(
                    "Reference processing failed and automatic rollback was not attempted because the destination hierarchy is no longer safe."
                    + Environment.NewLine
                    + errorDetails,
                    primaryException);
            }

            var rollbackErrors = new List<string>();

            var expectedProvHash = session.ReferenceProvenanceHash ?? (verifiedProvenance is not null
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new UTF8Encoding(false).GetBytes(verifiedProvenance))).ToLowerInvariant()
                : null);

            if (tempProvenanceWritten && File.Exists(tempProvenancePath))
            {
                if (expectedProvHash is not null)
                {
                    TryDeleteHashOwnedFileWithError(
                        tempProvenancePath,
                        expectedProvHash,
                        "Reference temp provenance",
                        () => ValidateSessionDestructivePathSafety(session),
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add($"Reference temp provenance at '{tempProvenancePath}' expected hash could not be determined. File preserved.");
                }
            }

            if (tempImageCopied && File.Exists(tempImagePath))
            {
                TryDeleteHashOwnedFileWithError(
                    tempImagePath,
                    session.ReferenceHash,
                    "Reference temp image",
                    () => ValidateSessionDestructivePathSafety(session),
                    rollbackErrors);
            }

            if (provenancePromoted && File.Exists(referenceProvenance))
            {
                if (expectedProvHash is not null)
                {
                    TryDeleteHashOwnedFileWithError(
                        referenceProvenance,
                        expectedProvHash,
                        "Reference provenance",
                        () => ValidateSessionDestructivePathSafety(session),
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add($"Reference provenance at '{referenceProvenance}' expected hash could not be determined. File preserved.");
                }
            }

            if (imagePromoted && File.Exists(referenceDestination))
            {
                TryDeleteHashOwnedFileWithError(
                    referenceDestination,
                    session.ReferenceHash,
                    "Reference image",
                    () => ValidateSessionDestructivePathSafety(session),
                    rollbackErrors);
            }

            if (!referenceFolderExisted && Directory.Exists(referenceFolder))
            {
                TryDeleteEmptyDirectoryWithError(
                    referenceFolder,
                    () => ValidateSessionDestructivePathSafety(session),
                    rollbackErrors);
            }

            if (!assetFolderExisted && Directory.Exists(assetFolder))
            {
                TryDeleteEmptyDirectoryWithError(
                    assetFolder,
                    () => ValidateSessionDestructivePathSafety(session),
                    rollbackErrors);
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

    internal ValidationResult RollbackReference(
        AssetSession session)
    {
        // Phase A: Verification only - Validate full session path hierarchy and verify exact ownership
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

        var normalizedAssetFolder = ValidationService.NormalizePath(session.AssetFolder);
        var expectedReferenceFolder = ValidationService.NormalizePath(Path.Combine(normalizedAssetFolder, AppConstants.ReferenceFolderName));

        if (!ValidationService.PathsEqual(Path.GetDirectoryName(ValidationService.NormalizePath(session.ReferenceDestinationPath)) ?? "", expectedReferenceFolder) ||
            !ValidationService.PathsEqual(Path.GetDirectoryName(ValidationService.NormalizePath(session.ReferenceProvenancePath)) ?? "", expectedReferenceFolder))
        {
            return ValidationResult.Failure("Reference paths escape expected reference folder.");
        }

        // Verify temp files if they exist
        var tempImage = session.GetReferenceTempImagePath();
        var tempProv = session.GetReferenceTempProvenancePath();

        if (!string.IsNullOrWhiteSpace(tempImage) && File.Exists(tempImage))
        {
            if (!ValidationService.PathsEqual(Path.GetDirectoryName(ValidationService.NormalizePath(tempImage)) ?? "", expectedReferenceFolder))
            {
                return ValidationResult.Failure("Reference temp image path escapes expected reference folder.");
            }

            try
            {
                var hash = ComputeSha256(tempImage);
                if (!string.Equals(hash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Failure($"Reference temp image at '{tempImage}' hash does not match session ReferenceHash. Refusing to delete unknown file.");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure($"Could not verify reference temp image: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(tempProv) && File.Exists(tempProv))
        {
            if (!ValidationService.PathsEqual(Path.GetDirectoryName(ValidationService.NormalizePath(tempProv)) ?? "", expectedReferenceFolder))
            {
                return ValidationResult.Failure("Reference temp provenance path escapes expected reference folder.");
            }

            try
            {
                var hash = ComputeSha256(tempProv);
                if (!string.Equals(hash, session.ReferenceProvenanceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Failure($"Reference temp provenance at '{tempProv}' hash does not match session ReferenceProvenanceHash. Refusing to delete unknown file.");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure($"Could not verify reference temp provenance: {ex.Message}");
            }
        }

        // Final Path / Reparse Gate
        OnBeforeRollbackReferenceFinalPathGate?.Invoke(session);

        var finalPathSafety = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
        if (!finalPathSafety.IsValid)
        {
            return finalPathSafety;
        }

        var referenceFolder =
            Path.Combine(
                session.AssetFolder,
                AppConstants.ReferenceFolderName);

        if (ValidationService.IsReparsePoint(session.AssetFolder) || (Directory.Exists(referenceFolder) && ValidationService.IsReparsePoint(referenceFolder)))
        {
            return ValidationResult.Failure("Reference folder hierarchy became a reparse point before rollback. No files were deleted.");
        }

        // Phase B: Execution / Mutation
        var errors =
            new List<string>();

        if (!string.IsNullOrWhiteSpace(tempProv) && File.Exists(tempProv))
        {
            TryDeleteHashOwnedFileWithError(
                tempProv,
                session.ReferenceProvenanceHash!,
                "Reference temp provenance",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (!string.IsNullOrWhiteSpace(tempImage) && File.Exists(tempImage))
        {
            TryDeleteHashOwnedFileWithError(
                tempImage,
                session.ReferenceHash!,
                "Reference temp image",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (File.Exists(session.ReferenceProvenancePath))
        {
            TryDeleteHashOwnedFileWithError(
                session.ReferenceProvenancePath,
                session.ReferenceProvenanceHash!,
                "Reference provenance",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (File.Exists(session.ReferenceDestinationPath))
        {
            TryDeleteHashOwnedFileWithError(
                session.ReferenceDestinationPath,
                session.ReferenceHash!,
                "Reference image",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (session.WasReferenceFolderCreatedByTool)
        {
            TryDeleteEmptyDirectoryWithError(
                referenceFolder,
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (session.WasAssetFolderCreatedByTool)
        {
            TryDeleteEmptyDirectoryWithError(
                session.AssetFolder,
                () => ValidateSessionDestructivePathSafety(session),
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

        var exactOld = _validationService.ValidateExactReferenceOutput(oldSession, _templateService);
        if (!exactOld.IsValid)
        {
            throw new InvalidDataException(
                "Current Reference output is inconsistent or modified and cannot be replaced."
                + Environment.NewLine
                + string.Join(Environment.NewLine, exactOld.Errors));
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

        // BUG-R14-002: Materialize legacy ReferenceProvenanceHash from single snapshot proof on transaction OldSession authority
        var oldProvResult = _validationService.TryGetExactReferenceProvenanceRawHash(
            oldSession,
            oldSession.ReferenceProvenancePath,
            _templateService,
            out var oldProvHash);

        if (!oldProvResult.IsValid || string.IsNullOrWhiteSpace(oldProvHash))
        {
            throw new InvalidDataException(
                "Could not establish exact byte authority for old reference provenance: "
                + string.Join("; ", oldProvResult.Errors));
        }

        var oldSessionAuthority = new AssetSession
        {
            SchemaVersion = oldSession.SchemaVersion,
            ProviderTemplate = oldSession.ProviderTemplate?.Clone(),
            SourceRequestKey = oldSession.SourceRequestKey,
            ProjectName = oldSession.ProjectName,
            AssetRootFolder = oldSession.AssetRootFolder,
            AssetFolderName = oldSession.AssetFolderName,
            AssetFolder = oldSession.AssetFolder,
            ReferenceSourcePath = oldSession.ReferenceSourcePath,
            ReferenceDestinationPath = oldSession.ReferenceDestinationPath,
            ReferenceFilename = oldSession.ReferenceFilename,
            ReferenceProvenancePath = oldSession.ReferenceProvenancePath,
            ReferenceHash = oldSession.ReferenceHash,
            ReferenceProvenanceHash = oldProvHash,
            ReferenceProcessedAt = oldSession.ReferenceProcessedAt,
            MainFilename = oldSession.MainFilename,
            MainPrompt = oldSession.MainPrompt,
            MainHash = oldSession.MainHash,
            MainProvenanceHash = oldSession.MainProvenanceHash,
            MainProcessedAt = oldSession.MainProcessedAt,
            WorkflowMode = oldSession.WorkflowMode,
            IsMainCommitting = oldSession.IsMainCommitting,
            WasAssetFolderCreatedByTool = oldSession.WasAssetFolderCreatedByTool,
            WasReferenceFolderCreatedByTool = oldSession.WasReferenceFolderCreatedByTool
        };

        var newSession = new AssetSession
        {
            SchemaVersion = oldSession.SchemaVersion,
            ProviderTemplate = oldSession.ProviderTemplate?.Clone(),
            SourceRequestKey = oldSession.SourceRequestKey,
            ProjectName = oldSession.ProjectName,
            AssetRootFolder = oldSession.AssetRootFolder,
            AssetFolderName = oldSession.AssetFolderName,
            AssetFolder = oldSession.AssetFolder,
            ReferenceSourcePath = newSourceImagePath,
            ReferenceDestinationPath = newFinalReferencePath,
            ReferenceFilename = newFilename,
            ReferenceProvenancePath = finalProvenancePath,
            ReferenceHash = sourceHash,
            ReferenceProcessedAt = processedAt,
            WasAssetFolderCreatedByTool = oldSession.WasAssetFolderCreatedByTool,
            WasReferenceFolderCreatedByTool = oldSession.WasReferenceFolderCreatedByTool
        };

        var newProvenance = _templateService.RenderReferenceForSession(
            newSession,
            newFilename,
            processedAt);

        newSession.ReferenceProvenanceHash =
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    new System.Text.UTF8Encoding(false).GetBytes(newProvenance)))
                .ToLowerInvariant();

        return new ReferenceReplacementTransaction
        {
            TransactionId = id,
            OldSession = oldSessionAuthority,
            NewSession = newSession,
            BackupReferencePath = backupReferencePath,
            BackupProvenancePath = backupProvenancePath,
            TempNewReferencePath = tempReferencePath,
            TempNewProvenancePath = tempProvenancePath
        };
    }

    private void RequireSafeReferenceReplacementTransaction(
        ReferenceReplacementTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var validation = _validationService.ValidateReferenceReplacementTransaction(transaction);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
        }
    }

    /// <summary>
    /// R2-002: Creates temporary replacement reference image and provenance files on disk and verifies integrity.
    /// Note: Caller contract requires persisting a Prepared replacement journal before invoking this mutator.
    /// </summary>
    internal void CreateReplacementTempFiles(
        ReferenceReplacementTransaction transaction,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(acceptedExtensions);

        RequireSafeReferenceReplacementTransaction(transaction);

        var sourceHash = ComputeSha256(transaction.NewSession.ReferenceSourcePath);
        if (!string.Equals(
                sourceHash,
                transaction.NewSession.ReferenceHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "Replacement Reference source changed after the Prepared transaction was created.");
        }

        CopyFileWithoutOverwrite(transaction.NewSession.ReferenceSourcePath, transaction.TempNewReferencePath);
        OnFileCopiedHook?.Invoke(transaction.NewSession.ReferenceSourcePath, transaction.TempNewReferencePath);

        var copiedValidation = _validationService.ValidateImageFile(transaction.TempNewReferencePath, acceptedExtensions);
        if (!copiedValidation.IsValid)
        {
            throw new InvalidDataException("Copied replacement reference image is invalid: " + string.Join("; ", copiedValidation.Errors));
        }

        var tempHash = ComputeSha256(transaction.TempNewReferencePath);
        if (!string.Equals(
                tempHash,
                transaction.NewSession.ReferenceHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Replacement temp Reference does not match Prepared ReferenceHash.");
        }

        var newProvenance = _templateService.RenderReferenceForSession(
            transaction.NewSession,
            transaction.NewSession.ReferenceFilename,
            transaction.NewSession.ReferenceProcessedAt);

        var provenanceHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                new System.Text.UTF8Encoding(false).GetBytes(newProvenance)))
            .ToLowerInvariant();

        if (!string.Equals(
                provenanceHash,
                transaction.NewSession.ReferenceProvenanceHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Replacement provenance changed after the Prepared transaction was created.");
        }

        WriteTextDurablyToReservedPath(transaction.TempNewProvenancePath, newProvenance);
    }

    /// <summary>
    /// BUG-R14-002 & BUG-R14-003: Ensures OldSession has proven raw SHA-256 byte authority.
    /// For legacy sessions with null ReferenceProvenanceHash, hydrates from a single exact-text byte snapshot.
    /// </summary>
    internal ValidationResult EnsureOldProvenanceByteAuthority(
        ReferenceReplacementTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!string.IsNullOrWhiteSpace(transaction.OldSession.ReferenceProvenanceHash))
        {
            return ValidationResult.Success();
        }

        // Candidate 1: BackupProvenancePath
        if (File.Exists(transaction.BackupProvenancePath))
        {
            var res = _validationService.TryGetExactReferenceProvenanceRawHash(
                transaction.OldSession,
                transaction.BackupProvenancePath,
                _templateService,
                out var hash);

            if (res.IsValid && !string.IsNullOrWhiteSpace(hash))
            {
                transaction.OldSession.ReferenceProvenanceHash = hash;
                return ValidationResult.Success();
            }

            return ValidationResult.Failure(
                $"Could not establish byte authority for legacy backup reference provenance: {string.Join("; ", res.Errors)}");
        }

        // Candidate 2: Canonical OldSession.ReferenceProvenancePath
        if (File.Exists(transaction.OldSession.ReferenceProvenancePath))
        {
            var res = _validationService.TryGetExactReferenceProvenanceRawHash(
                transaction.OldSession,
                transaction.OldSession.ReferenceProvenancePath,
                _templateService,
                out var hash);

            if (res.IsValid && !string.IsNullOrWhiteSpace(hash))
            {
                transaction.OldSession.ReferenceProvenanceHash = hash;
                return ValidationResult.Success();
            }

            return ValidationResult.Failure(
                $"Could not establish byte authority for legacy canonical reference provenance: {string.Join("; ", res.Errors)}");
        }

        return ValidationResult.Failure(
            "Could not locate old reference provenance to establish byte authority.");
    }

    /// <summary>
    /// R2-002: Moves old canonical reference image and provenance to deterministic backup paths.
    /// Note: Caller contract requires persisting an OldBackupPending replacement journal before invoking this mutator.
    /// </summary>
    internal void BackupOldReference(
        ReferenceReplacementTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        RequireSafeReferenceReplacementTransaction(transaction);

        var authorityResult = EnsureOldProvenanceByteAuthority(transaction);
        if (!authorityResult.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, authorityResult.Errors));
        }

        if (!File.Exists(transaction.OldSession.ReferenceDestinationPath))
        {
            throw new IOException($"Old reference destination not found: {transaction.OldSession.ReferenceDestinationPath}");
        }

        if (!File.Exists(transaction.OldSession.ReferenceProvenancePath))
        {
            throw new IOException($"Old reference provenance not found: {transaction.OldSession.ReferenceProvenancePath}");
        }

        var oldRefHash = ComputeSha256(transaction.OldSession.ReferenceDestinationPath);
        if (!string.Equals(oldRefHash, transaction.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Old reference image on disk does not match session ReferenceHash.");
        }

        var oldProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
            transaction.OldSession,
            transaction.OldSession.ReferenceProvenancePath,
            _templateService);

        if (!oldProvValidation.IsValid)
        {
            throw new InvalidDataException(
                $"Old reference provenance on disk does not match expected session provenance: {string.Join("; ", oldProvValidation.Errors)}");
        }

        OnBeforeBackupOldReferenceFinalAuthorityGate?.Invoke(transaction);

        RequireSafeReferenceReplacementTransaction(transaction);

        var finalOldRefHash = ComputeSha256(transaction.OldSession.ReferenceDestinationPath);
        if (!string.Equals(finalOldRefHash, transaction.OldSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("OLD Reference changed before backup.");
        }

        var finalOldProvValidation = _validationService.ValidateExactReferenceProvenanceOwnership(
            transaction.OldSession,
            transaction.OldSession.ReferenceProvenancePath,
            _templateService);

        if (!finalOldProvValidation.IsValid)
        {
            throw new InvalidDataException(
                "OLD Reference provenance changed before backup.");
        }

        MoveHashOwnedFileWithoutOverwrite(
            transaction.OldSession.ReferenceDestinationPath,
            transaction.BackupReferencePath,
            transaction.OldSession.ReferenceHash!,
            "OLD Reference image",
            () => _validationService.ValidateReferenceReplacementTransaction(transaction));

        MoveHashOwnedFileWithoutOverwrite(
            transaction.OldSession.ReferenceProvenancePath,
            transaction.BackupProvenancePath,
            transaction.OldSession.ReferenceProvenanceHash!,
            "OLD Reference provenance",
            () => _validationService.ValidateReferenceReplacementTransaction(transaction));

        OnPrepareReplacementOldBackedUpHook?.Invoke(
            transaction.BackupReferencePath,
            transaction.BackupProvenancePath);
    }

    /// <summary>
    /// R2-002: Promotes temporary replacement files to canonical destination paths.
    /// Note: Caller contract requires persisting a NewPromotionPending replacement journal before invoking this mutator.
    /// </summary>
    internal void PromoteNewReference(
        ReferenceReplacementTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        RequireSafeReferenceReplacementTransaction(transaction);

        if (!File.Exists(transaction.TempNewReferencePath))
        {
            throw new IOException("Replacement temp Reference is missing.");
        }

        var tempRefHash = ComputeSha256(transaction.TempNewReferencePath);
        if (!string.Equals(tempRefHash, transaction.NewSession.ReferenceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Replacement temp Reference no longer matches Prepared ReferenceHash.");
        }

        if (!File.Exists(transaction.TempNewProvenancePath))
        {
            throw new IOException("Replacement temp provenance is missing.");
        }

        var tempProvHash = ComputeSha256(transaction.TempNewProvenancePath);
        if (!string.Equals(tempProvHash, transaction.NewSession.ReferenceProvenanceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Replacement temp provenance no longer matches Prepared ReferenceProvenanceHash.");
        }

        OnBeforeReplacementFinalPathGate?.Invoke(transaction);

        // Final confinement/reparse gate after hash work immediately before canonical promotion
        RequireSafeReferenceReplacementTransaction(transaction);

        MoveHashOwnedFileWithoutOverwrite(
            transaction.TempNewReferencePath,
            transaction.NewSession.ReferenceDestinationPath,
            transaction.NewSession.ReferenceHash!,
            "New Reference image",
            () => _validationService.ValidateReferenceReplacementTransaction(transaction));

        MoveHashOwnedFileWithoutOverwrite(
            transaction.TempNewProvenancePath,
            transaction.NewSession.ReferenceProvenancePath,
            transaction.NewSession.ReferenceProvenanceHash!,
            "New Reference provenance",
            () => _validationService.ValidateReferenceReplacementTransaction(transaction));

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
    internal ValidationResult CleanupReplacementBackups(
        ReferenceReplacementTransaction transaction)
    {
        return CommitReferenceReplacement(transaction);
    }

    /// <summary>
    /// Test convenience overload. Internalized to prevent production callers from bypassing durable session persistence (R3-011).
    /// </summary>
    internal ReferenceReplacementTransaction PrepareReferenceReplacement(
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


    internal ValidationResult CommitReferenceReplacement(
        ReferenceReplacementTransaction transaction)
    {
        var transactionValidation = _validationService.ValidateReferenceReplacementTransaction(transaction);
        if (!transactionValidation.IsValid)
        {
            return transactionValidation;
        }

        var authorityResult = EnsureOldProvenanceByteAuthority(transaction);
        if (!authorityResult.IsValid)
        {
            return authorityResult;
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

        OnBeforeReplacementCleanupFinalPathGate?.Invoke(transaction);

        var finalSafety = _validationService.ValidateReferenceReplacementTransaction(transaction);
        if (!finalSafety.IsValid)
        {
            return finalSafety;
        }

        if (File.Exists(transaction.BackupReferencePath))
        {
            TryDeleteHashOwnedFileWithError(
                transaction.BackupReferencePath,
                transaction.OldSession.ReferenceHash!,
                "backup reference image",
                () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                errors);
        }

        if (File.Exists(transaction.BackupProvenancePath))
        {
            TryDeleteHashOwnedFileWithError(
                transaction.BackupProvenancePath,
                transaction.OldSession.ReferenceProvenanceHash!,
                "backup reference provenance",
                () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                errors);
        }

        if (errors.Count == 0)
        {
            transaction.IsCommitted = true;
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    internal ValidationResult RollbackReferenceReplacement(
        ReferenceReplacementTransaction transaction)
    {
        OnRollbackReferenceReplacementInvoked?.Invoke(transaction);

        var transactionValidation = _validationService.ValidateReferenceReplacementTransaction(transaction);
        if (!transactionValidation.IsValid)
        {
            return transactionValidation;
        }

        var authorityResult = EnsureOldProvenanceByteAuthority(transaction);
        if (!authorityResult.IsValid)
        {
            return authorityResult;
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

        OnBeforeRollbackReferenceReplacementFinalPathGate?.Invoke(transaction);

        var finalTransactionSafety = _validationService.ValidateReferenceReplacementTransaction(transaction);
        if (!finalTransactionSafety.IsValid)
        {
            return finalTransactionSafety;
        }

        // Phase B: Execution / Mutation (Old state is proven restorable and new files verified)
        var errors = new List<string>();

        // Rollback Provenance
        if (backupProvExists)
        {
            if (File.Exists(transaction.OldSession.ReferenceProvenancePath))
            {
                var currProvHash = ComputeSha256(transaction.OldSession.ReferenceProvenancePath);
                var validOld = string.Equals(currProvHash, transaction.OldSession.ReferenceProvenanceHash, StringComparison.OrdinalIgnoreCase);
                var validNew = string.Equals(currProvHash, transaction.NewSession.ReferenceProvenanceHash, StringComparison.OrdinalIgnoreCase);

                if (validOld || validNew)
                {
                    TryDeleteHashOwnedFileWithError(
                        transaction.OldSession.ReferenceProvenancePath,
                        currProvHash,
                        "current reference provenance",
                        () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                        errors);
                }
                else
                {
                    errors.Add(
                        $"Current reference provenance at '{transaction.OldSession.ReferenceProvenancePath}' hash no longer matches old or new provenance authority. File preserved.");
                }
            }

            TryRestoreHashOwnedFileWithError(
                transaction.BackupProvenancePath,
                transaction.OldSession.ReferenceProvenancePath,
                transaction.OldSession.ReferenceProvenanceHash!,
                "old reference provenance",
                () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                errors);
        }

        // Rollback Reference Image
        if (backupRefExists)
        {
            if (!sameFilename && newRefExists && File.Exists(transaction.NewSession.ReferenceDestinationPath))
            {
                TryDeleteHashOwnedFileWithError(
                    transaction.NewSession.ReferenceDestinationPath,
                    transaction.NewSession.ReferenceHash!,
                    "new reference image",
                    () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                    errors);
            }

            if (oldRefExists && File.Exists(transaction.OldSession.ReferenceDestinationPath))
            {
                var expectedDestHash = sameFilename
                    ? (string.Equals(ComputeSha256(transaction.OldSession.ReferenceDestinationPath), transaction.NewSession.ReferenceHash, StringComparison.OrdinalIgnoreCase)
                        ? transaction.NewSession.ReferenceHash!
                        : transaction.OldSession.ReferenceHash!)
                    : transaction.OldSession.ReferenceHash!;

                TryDeleteHashOwnedFileWithError(
                    transaction.OldSession.ReferenceDestinationPath,
                    expectedDestHash,
                    "old reference destination",
                    () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                    errors);
            }

            TryRestoreHashOwnedFileWithError(
                transaction.BackupReferencePath,
                transaction.OldSession.ReferenceDestinationPath,
                transaction.OldSession.ReferenceHash!,
                "old reference image",
                () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                errors);
        }
        else
        {
            // Old reference destination was verified intact on disk; clean up new reference file if different
            if (!sameFilename && newRefExists && File.Exists(transaction.NewSession.ReferenceDestinationPath))
            {
                TryDeleteHashOwnedFileWithError(
                    transaction.NewSession.ReferenceDestinationPath,
                    transaction.NewSession.ReferenceHash!,
                    "new reference image",
                    () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                    errors);
            }
        }

        // Clean up temporary files if they match expected ownership
        if (!string.IsNullOrWhiteSpace(transaction.TempNewReferencePath) && File.Exists(transaction.TempNewReferencePath))
        {
            TryDeleteHashOwnedFileWithError(
                transaction.TempNewReferencePath,
                transaction.NewSession.ReferenceHash!,
                "replacement temp Reference",
                () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                errors);
        }

        if (!string.IsNullOrWhiteSpace(transaction.TempNewProvenancePath) && File.Exists(transaction.TempNewProvenancePath))
        {
            TryDeleteHashOwnedFileWithError(
                transaction.TempNewProvenancePath,
                transaction.NewSession.ReferenceProvenanceHash!,
                "replacement temp provenance",
                () => _validationService.ValidateReferenceReplacementTransaction(transaction),
                errors);
        }

        if (errors.Count > 0)
        {
            return ValidationResult.Failure(errors);
        }

        // Post-mutation validation: ensure old session reference output is fully restored and valid
        var postValidation = _validationService.ValidateExactReferenceOutput(
            transaction.OldSession,
            _templateService);
        if (!postValidation.IsValid)
        {
            return ValidationResult.Failure(postValidation.Errors);
        }

        return ValidationResult.Success();
    }

    private void RequireInitialReferenceStagingAuthority(
        AssetSession session,
        string tempImagePath,
        string tempProvenancePath)
    {
        if (!File.Exists(tempImagePath))
        {
            throw new IOException("Initial Reference staging image is missing.");
        }

        var imageHash = ComputeSha256(tempImagePath);
        if (!string.Equals(imageHash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Initial Reference staging image no longer matches Prepared ReferenceHash.");
        }

        if (!File.Exists(tempProvenancePath))
        {
            throw new IOException("Initial Reference staging provenance is missing.");
        }

        var provenanceHash = ComputeSha256(tempProvenancePath);
        if (!string.Equals(provenanceHash, session.ReferenceProvenanceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Initial Reference staging provenance no longer matches Prepared ReferenceProvenanceHash.");
        }

        var pathValidation = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
        if (!pathValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, pathValidation.Errors));
        }

        var referenceFolder = Path.Combine(session.AssetFolder, AppConstants.ReferenceFolderName);
        if (ValidationService.IsReparsePoint(session.AssetFolder) || ValidationService.IsReparsePoint(referenceFolder))
        {
            throw new IOException("Reference folder hierarchy became a reparse point before promotion.");
        }
    }
}

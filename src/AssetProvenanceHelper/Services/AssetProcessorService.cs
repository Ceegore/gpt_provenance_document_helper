using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class AssetProcessorService
{
    [ThreadStatic]
    internal static Action<string, string>? OnFileCopiedHook;

    [ThreadStatic]
    internal static Action<string>? OnMainPromotedHook;

    [ThreadStatic]
    internal static Action<string, string>? OnPrepareReplacementOldBackedUpHook;

    private readonly TemplateService _templateService;
    private readonly ValidationService _validationService;

    public AssetProcessorService(
        TemplateService templateService,
        ValidationService validationService)
    {
        _templateService =
            templateService;

        _validationService =
            validationService;
    }

    public string ComputeSha256(
        string filePath)
    {
        using var stream =
            new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

        var hash =
            SHA256.HashData(
                stream);

        return Convert
            .ToHexString(hash)
            .ToLowerInvariant();
    }

    public void CopyFileWithoutOverwrite(
        string source,
        string destination)
    {
        if (File.Exists(destination))
        {
            throw new IOException(
                $"Destination file already exists: {destination}");
        }

        File.Copy(
            source,
            destination,
            overwrite: false);
    }

    public void WriteTextAtomic(
        string targetPath,
        string content)
    {
        if (File.Exists(targetPath))
        {
            throw new IOException(
                $"Destination file already exists: {targetPath}");
        }

        var directory =
            Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(
                $"Could not determine directory for '{targetPath}'.");

        Directory.CreateDirectory(
            directory);

        var tempPath =
            Path.Combine(
                directory,
                $".__write_{Guid.NewGuid():N}.tmp");

        try
        {
            using (
                var stream =
                    new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
            using (
                var writer =
                    new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
            {
                writer.Write(
                    content);

                writer.Flush();

                stream.Flush(
                    true);
            }

            File.Move(
                tempPath,
                targetPath,
                overwrite: false);
        }
        catch
        {
            TryDeleteFile(
                tempPath);

            throw;
        }
    }

    public AssetSession ProcessReference(
        AppSettings settings,
        string assetFolderName,
        string sourceImagePath,
        DateTimeOffset processedAt)
    {
        // BUG-R9-005: Validate all service inputs and paths before performing any filesystem mutation
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

        var folderValidation = _validationService.ValidateAssetFolderName(assetFolderName);
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

        var assetFolder =
            expectedAssetFolder;

        var referenceFolder =
            ValidationService.NormalizePath(
                Path.Combine(
                    assetFolder,
                    AppConstants.ReferenceFolderName));

        var referenceFilename =
            Path.GetFileName(
                sourceImagePath);

        var referenceDestination =
            ValidationService.NormalizePath(
                Path.Combine(
                    referenceFolder,
                    referenceFilename));

        var referenceProvenance =
            ValidationService.NormalizePath(
                Path.Combine(
                    referenceFolder,
                    AppConstants.ReferenceProvenanceFileName));

        var assetFolderExisted =
            Directory.Exists(
                assetFolder);

        var referenceFolderExisted =
            Directory.Exists(
                referenceFolder);

        // BUG-007: Check for reparse points (junctions/symlinks) before performing any write/directory operations
        if (ValidationService.IsReparsePoint(settings.AssetRootFolder))
        {
            throw new IOException(
                $"Asset root folder is a reparse point (junction or symbolic link): {settings.AssetRootFolder}");
        }

        if (assetFolderExisted && ValidationService.IsReparsePoint(assetFolder))
        {
            throw new IOException(
                $"Asset folder is a reparse point (junction or symbolic link): {assetFolder}");
        }

        if (referenceFolderExisted && ValidationService.IsReparsePoint(referenceFolder))
        {
            throw new IOException(
                $"Reference folder is a reparse point (junction or symbolic link): {referenceFolder}");
        }

        var imageCopied =
            false;

        var provenanceWritten =
            false;

        // BUG-R16-001: Declare outside try so catch block can verify ownership
        string? sourceHash = null;
        string? provenance = null;

        try
        {
            if (File.Exists(
                    referenceDestination))
            {
                throw new IOException(
                    $"Reference destination already exists: {referenceDestination}");
            }

            if (File.Exists(
                    referenceProvenance))
            {
                throw new IOException(
                    $"Reference provenance already exists: {referenceProvenance}");
            }

            Directory.CreateDirectory(
                assetFolder);

            Directory.CreateDirectory(
                referenceFolder);

            sourceHash =
                ComputeSha256(
                    sourceImagePath);

            CopyFileWithoutOverwrite(
                sourceImagePath,
                referenceDestination);

            imageCopied =
                true;

            OnFileCopiedHook?.Invoke(
                sourceImagePath,
                referenceDestination);

            var copiedValidation =
                _validationService
                    .ValidateImageFile(
                        referenceDestination,
                        settings.AcceptedExtensions);

            if (!copiedValidation.IsValid)
            {
                throw new InvalidDataException(
                    "Copied reference image is invalid: "
                    + string.Join("; ", copiedValidation.Errors));
            }

            var hash =
                ComputeSha256(
                    referenceDestination);

            if (!string.Equals(
                    sourceHash,
                    hash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Reference source image changed during copy.");
            }

            var generationDate =
                processedAt
                    .ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

            provenance =
                _templateService.RenderReference(
                    referenceFilename,
                    settings.ProjectName,
                    generationDate);

            WriteTextAtomic(
                referenceProvenance,
                provenance);

            provenanceWritten =
                true;

            var session =
                new AssetSession
                {
                    ProjectName =
                        settings.ProjectName,

                    AssetRootFolder =
                        settings.AssetRootFolder,

                    AssetFolderName =
                        assetFolderName,

                    AssetFolder =
                        assetFolder,

                    ReferenceSourcePath =
                        sourceImagePath,

                    ReferenceDestinationPath =
                        referenceDestination,

                    ReferenceFilename =
                        referenceFilename,

                    ReferenceProvenancePath =
                        referenceProvenance,

                    ReferenceHash =
                        hash,

                    ReferenceProcessedAt =
                        processedAt,

                    WasAssetFolderCreatedByTool =
                        !assetFolderExisted,

                    WasReferenceFolderCreatedByTool =
                        !referenceFolderExisted
                };

            var validation =
                _validationService
                    .ValidateReferenceOutput(
                        session);

            if (!validation.IsValid)
            {
                throw new InvalidDataException(
                    string.Join(
                        Environment.NewLine,
                        validation.Errors));
            }

            return session;
        }
        catch (Exception primaryException)
        {
            var rollbackErrors =
                new List<string>();

            // BUG-R16-001: Verify current content ownership before deleting
            if (provenanceWritten)
            {
                if (provenance is not null && TryVerifyTextFileOwnership(referenceProvenance, provenance))
                {
                    TryDeleteFileWithError(
                        referenceProvenance,
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Reference provenance at '{referenceProvenance}' content no longer matches tool-written provenance. File preserved.");
                }
            }

            if (imageCopied)
            {
                if (sourceHash is not null && TryVerifyFileHashOwnership(referenceDestination, sourceHash))
                {
                    TryDeleteFileWithError(
                        referenceDestination,
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Reference image at '{referenceDestination}' hash no longer matches source hash. File preserved.");
                }
            }

            if (!referenceFolderExisted)
            {
                TryDeleteEmptyDirectoryWithError(
                    referenceFolder,
                    rollbackErrors);
            }

            if (!assetFolderExisted)
            {
                TryDeleteEmptyDirectoryWithError(
                    assetFolder,
                    rollbackErrors);
            }

            if (rollbackErrors.Count > 0)
            {
                throw new IOException(
                    "Reference processing failed and automatic rollback was incomplete."
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

    public string ProcessMainImage(
        AssetSession session,
        IReadOnlyCollection<string> acceptedExtensions,
        string sourceImagePath,
        string prompt,
        DateTimeOffset processedAt)
    {
        var sessionValidation =
            _validationService
                .ValidateSession(
                    session);

        if (!sessionValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(
                    Environment.NewLine,
                    sessionValidation.Errors));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException(
                "Prompt must not be empty.",
                nameof(prompt));
        }

        var imageValidation =
            _validationService
                .ValidateImageFile(
                    sourceImagePath,
                    acceptedExtensions);

        if (!imageValidation.IsValid)
        {
            throw new InvalidDataException(
                string.Join(
                    Environment.NewLine,
                    imageValidation.Errors));
        }

        var mainFilename =
            Path.GetFileName(
                sourceImagePath);

        // BUG-R18-002 & BUG-R19-003: Bind active Main transaction journal metadata to call arguments with exact representation before writes
        if (session.IsMainCommitting)
        {
            if (!string.Equals(session.MainFilename, mainFilename, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Active Main transaction filename '{session.MainFilename}' does not match source filename '{mainFilename}'.");
            }

            if (!string.Equals(session.MainPrompt, prompt, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Active Main transaction prompt '{session.MainPrompt}' does not match provided prompt '{prompt}'.");
            }

            if (!session.MainProcessedAt.HasValue ||
                !session.MainProcessedAt.Value.EqualsExact(processedAt))
            {
                throw new InvalidOperationException(
                    $"Active Main transaction processedAt '{session.MainProcessedAt}' does not match provided processedAt '{processedAt}'.");
            }
        }

        var mainDestination =
            Path.Combine(
                session.AssetFolder,
                mainFilename);

        var finalProvenance =
            Path.Combine(
                session.AssetFolder,
                AppConstants.FinalProvenanceFileName);

        if (File.Exists(mainDestination))
        {
            throw new IOException(
                $"Main image destination already exists: {mainDestination}");
        }

        if (File.Exists(finalProvenance))
        {
            throw new IOException(
                $"Final provenance already exists: {finalProvenance}");
        }

        var tempMainPath =
            !string.IsNullOrWhiteSpace(session.GetMainTempImagePath())
                ? session.GetMainTempImagePath()
                : Path.Combine(
                    session.AssetFolder,
                    $".main-{Guid.NewGuid():N}{Path.GetExtension(mainFilename)}");

        var tempCopied =
            false;

        var mainPromoted =
            false;

        var provenanceWritten =
            false;

        // BUG-R16-001 & BUG-R17-002: Declare outside try so catch block can verify ownership
        string? sourceHash = null;
        string? mainHash = null;
        string? provenance = null;

        var tempProvenanceCreatedByThisCall =
            false;

        var tempProvenancePath =
            !string.IsNullOrWhiteSpace(session.GetMainTempProvenancePath())
                ? session.GetMainTempProvenancePath()
                : Path.Combine(
                    session.AssetFolder,
                    $".main-{Guid.NewGuid():N}.md.tmp");

        try
        {
            sourceHash =
                ComputeSha256(
                    sourceImagePath);

            // BUG-R18-002: Verify active Main transaction hash matches source image before copy
            if (session.IsMainCommitting &&
                !string.IsNullOrWhiteSpace(session.MainHash) &&
                !string.Equals(
                    session.MainHash,
                    sourceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Active Main transaction hash '{session.MainHash}' does not match source image hash '{sourceHash}'.");
            }

            CopyFileWithoutOverwrite(
                sourceImagePath,
                tempMainPath);

            tempCopied =
                true;

            OnFileCopiedHook?.Invoke(
                sourceImagePath,
                tempMainPath);

            var copiedValidation =
                _validationService
                    .ValidateImageFile(
                        tempMainPath,
                        acceptedExtensions);

            if (!copiedValidation.IsValid)
            {
                throw new InvalidDataException(
                    "Copied main image is invalid: "
                    + string.Join("; ", copiedValidation.Errors));
            }

            mainHash =
                ComputeSha256(
                    tempMainPath);

            // BUG-R18-001: Unconditionally verify that copied temp bytes match pre-copy source bytes
            if (!string.Equals(
                    sourceHash,
                    mainHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Main source image changed during copy.");
            }

            if (!string.IsNullOrWhiteSpace(session.MainHash) &&
                !string.Equals(
                    mainHash,
                    session.MainHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Main source changed between validation/hash and copy.");
            }

            if (string.Equals(
                    mainHash,
                    session.ReferenceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The selected main image is identical to the reference image.");
            }

            var generationDate =
                processedAt
                    .ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

            provenance =
                _templateService.RenderFinal(
                    mainFilename,
                    session.ReferenceFilename,
                    session.ProjectName,
                    generationDate,
                    prompt);

            // BUG-R13-004: Write temporary provenance without silently overwriting pre-existing files
            using (var stream = new FileStream(
                tempProvenancePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(provenance);
            }

            tempProvenanceCreatedByThisCall = true;

            File.Move(
                tempProvenancePath,
                finalProvenance,
                overwrite: false);

            tempProvenanceCreatedByThisCall = false;
            provenanceWritten =
                true;

            File.Move(
                tempMainPath,
                mainDestination,
                overwrite: false);

            mainPromoted =
                true;

            OnMainPromotedHook?.Invoke(
                mainDestination);

            session.IsMainCommitting = true;
            session.MainTransactionId ??= Guid.NewGuid().ToString("N");
            session.MainFilename = mainFilename;
            session.MainHash = mainHash;
            session.MainPrompt = prompt;
            session.MainProcessedAt = processedAt;

            var validation =
                _validationService
                    .ValidateCompleteAsset(
                        session,
                        mainDestination,
                        finalProvenance,
                        mainFilename,
                        generationDate,
                        prompt,
                        session.MainHash);

            if (!validation.IsValid)
            {
                throw new InvalidDataException(
                    string.Join(
                        Environment.NewLine,
                        validation.Errors));
            }

            return mainFilename;
        }
        catch (Exception primaryException)
        {
            var rollbackErrors =
                new List<string>();

            // BUG-R16-001: Verify current content ownership before deleting promoted files
            if (provenanceWritten)
            {
                if (provenance is not null && TryVerifyTextFileOwnership(finalProvenance, provenance))
                {
                    TryDeleteFileWithError(
                        finalProvenance,
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Final provenance at '{finalProvenance}' content no longer matches tool-written provenance. File preserved.");
                }
            }

            if (mainPromoted)
            {
                if (sourceHash is not null && TryVerifyFileHashOwnership(mainDestination, sourceHash))
                {
                    TryDeleteFileWithError(
                        mainDestination,
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Main image at '{mainDestination}' hash no longer matches expected hash. File preserved.");
                }
            }

            // BUG-R17-002: Verify current temp main image ownership before deleting
            if (tempCopied && !mainPromoted)
            {
                if (File.Exists(tempMainPath))
                {
                    if (sourceHash is not null && TryVerifyFileHashOwnership(tempMainPath, sourceHash))
                    {
                        TryDeleteFileWithError(
                            tempMainPath,
                            rollbackErrors);
                    }
                    else
                    {
                        rollbackErrors.Add(
                            $"Main temp image at '{tempMainPath}' hash no longer matches expected hash. File preserved.");
                    }
                }
            }

            // BUG-R13-004 & BUG-R17-002: Verify temp provenance ownership before deleting
            if (tempProvenanceCreatedByThisCall)
            {
                if (File.Exists(tempProvenancePath))
                {
                    if (provenance is not null && TryVerifyTextFileOwnership(tempProvenancePath, provenance))
                    {
                        TryDeleteFileWithError(
                            tempProvenancePath,
                            rollbackErrors);
                    }
                    else
                    {
                        rollbackErrors.Add(
                            $"Main temp provenance at '{tempProvenancePath}' content no longer matches tool-written provenance. File preserved.");
                    }
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new AssetProcessingException(
                    "Main Image processing failed and automatic rollback was incomplete."
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
                    primaryException,
                    rollbackComplete: false);
            }

            throw;
        }
    }

    public ValidationResult RollbackMain(
        AssetSession session,
        string? mainFilename = null)
    {
        // BUG-R9-001: RollbackMain must be strictly bound to an active Main commit
        if (!session.IsMainCommitting)
        {
            return ValidationResult.Failure("No active Main commit exists for rollback.");
        }

        // BUG-013: Validate full session path hierarchy against trusted root
        var pathValidation =
            ValidationService.ValidateSessionPathsForDestructiveOperation(session);

        if (!pathValidation.IsValid)
        {
            return pathValidation;
        }

        // BUG-R9-001 & BUG-R16-002: Validate Main metadata completeness including TransactionId
        if (string.IsNullOrWhiteSpace(session.MainFilename) ||
            !string.Equals(Path.GetFileName(session.MainFilename), session.MainFilename, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(session.MainHash) ||
            session.MainHash.Length != 64 ||
            session.MainHash.Any(c => !Uri.IsHexDigit(c)) ||
            session.MainPrompt is null ||
            !session.MainProcessedAt.HasValue ||
            string.IsNullOrWhiteSpace(session.MainTransactionId) ||
            session.MainTransactionId.Length != 32 ||
            session.MainTransactionId.Any(c => !Uri.IsHexDigit(c)))
        {
            return ValidationResult.Failure("Main rollback metadata is incomplete or invalid.");
        }

        if (!string.IsNullOrWhiteSpace(mainFilename) &&
            !string.Equals(session.MainFilename, mainFilename, StringComparison.Ordinal))
        {
            return ValidationResult.Failure("mainFilename does not match session.MainFilename.");
        }

        var targetFilename = session.MainFilename;

        var normalizedAssetFolder = ValidationService.NormalizePath(session.AssetFolder);
        var mainPath =
            Path.Combine(
                session.AssetFolder,
                targetFilename);

        var normalizedMainPath = ValidationService.NormalizePath(mainPath);
        if (!ValidationService.PathsEqual(Path.GetDirectoryName(normalizedMainPath) ?? "", normalizedAssetFolder))
        {
            return ValidationResult.Failure("mainFilename escapes the session asset folder.");
        }

        // BUG-R9-001: If main destination file exists on disk, verify its SHA-256 matches session.MainHash
        if (File.Exists(mainPath))
        {
            try
            {
                var existingHash = ComputeSha256(mainPath);
                if (!string.Equals(existingHash, session.MainHash, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Failure(
                        $"Main image on disk does not match session MainHash (expected {session.MainHash}, found {existingHash}). Refusing to delete unknown file.");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure(
                    $"Could not compute Main image SHA-256 hash: {ex.Message}");
            }
        }

        var provenancePath =
            Path.Combine(
                session.AssetFolder,
                AppConstants.FinalProvenanceFileName);

        // BUG-R12-001 & BUG-R13-001: Verify exact ownership of final provenance before deleting
        if (File.Exists(provenancePath))
        {
            var provValidation = _validationService.ValidateExactFinalProvenanceOwnership(
                session,
                provenancePath,
                _templateService);

            if (!provValidation.IsValid)
            {
                return ValidationResult.Failure(
                    $"Final provenance on disk does not match session state ({string.Join("; ", provValidation.Errors)}). Refusing to delete unknown file.");
            }
        }

        // BUG-R9-002 & BUG-R14-003: Verify exact ownership of deterministic temp files before deleting
        var tempImage = session.GetMainTempImagePath();
        if (!string.IsNullOrWhiteSpace(tempImage) && File.Exists(tempImage))
        {
            try
            {
                var tempHash = ComputeSha256(tempImage);
                if (!string.Equals(tempHash, session.MainHash, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Failure(
                        $"Main temp image at '{tempImage}' hash does not match session MainHash (expected {session.MainHash}, found {tempHash}). Refusing to delete unknown file.");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure(
                    $"Could not compute Main temp image SHA-256 hash: {ex.Message}");
            }
        }

        var tempProv = session.GetMainTempProvenancePath();
        if (!string.IsNullOrWhiteSpace(tempProv) && File.Exists(tempProv))
        {
            var tempProvValidation = _validationService.ValidateExactFinalProvenanceOwnership(
                session,
                tempProv,
                _templateService);

            if (!tempProvValidation.IsValid)
            {
                return ValidationResult.Failure(
                    $"Main temp provenance at '{tempProv}' does not match session state ({string.Join("; ", tempProvValidation.Errors)}). Refusing to delete unknown file.");
            }
        }

        var errors =
            new List<string>();

        TryDeleteFileWithError(
            provenancePath,
            errors);

        TryDeleteFileWithError(
            mainPath,
            errors);

        if (!string.IsNullOrWhiteSpace(tempImage) && File.Exists(tempImage))
        {
            TryDeleteFileWithError(
                tempImage,
                errors);
        }

        if (!string.IsNullOrWhiteSpace(tempProv) && File.Exists(tempProv))
        {
            TryDeleteFileWithError(
                tempProv,
                errors);
        }

        if (errors.Count == 0)
        {
            session.IsMainCommitting = false;
            session.MainFilename = null;
            session.MainPrompt = null;
            session.MainProcessedAt = null;
            session.MainHash = null;
            session.MainTransactionId = null;
            return ValidationResult.Success();
        }

        return ValidationResult.Failure(errors);
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

                    ReferenceProcessedAt =
                        processedAt,

                    WasAssetFolderCreatedByTool =
                        oldSession.WasAssetFolderCreatedByTool,

                    WasReferenceFolderCreatedByTool =
                        oldSession.WasReferenceFolderCreatedByTool
                };

            var validation =
                _validationService
                    .ValidateReferenceOutput(
                        newSession);

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
                    backupProvenancePath
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

    private static void TryDeleteFile(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Only used for cleanup of a temporary file while
            // preserving the original exception.
        }
    }

    // BUG-R16-001: Verify file hash ownership before allowing deletion
    private bool TryVerifyFileHashOwnership(
        string path,
        string expectedHash)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var currentHash = ComputeSha256(path);
            return string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // BUG-R16-001: Verify text file content ownership before allowing deletion
    private static bool TryVerifyTextFileOwnership(
        string path,
        string expectedContent)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var currentContent = File.ReadAllText(path, new UTF8Encoding(false));
            return string.Equals(currentContent, expectedContent, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFileWithError(
        string path,
        ICollection<string> errors)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not delete file '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteEmptyDirectoryWithError(
        string path,
        ICollection<string> errors)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            if (Directory
                .EnumerateFileSystemEntries(path)
                .Any())
            {
                return;
            }

            Directory.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not delete empty directory '{path}': {ex.Message}");
        }
    }

    private static void TryRestoreFileWithError(
        string backupPath,
        string destinationPath,
        string description,
        ICollection<string> errors)
    {
        try
        {
            if (!File.Exists(backupPath))
            {
                return;
            }

            if (File.Exists(destinationPath))
            {
                errors.Add(
                    $"Could not restore {description}: destination already exists: {destinationPath}");

                return;
            }

            File.Move(
                backupPath,
                destinationPath,
                overwrite: false);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Could not restore {description}: {ex.Message}");
        }
    }
}

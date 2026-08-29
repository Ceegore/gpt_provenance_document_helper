using System.Globalization;
using System.Text;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed partial class AssetProcessorService
{
    private static IReadOnlyList<string> FindExistingIngameVariants(
        string ingameFolder,
        string assetName,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        if (!Directory.Exists(ingameFolder))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(
                ingameFolder,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                var stem = Path.GetFileNameWithoutExtension(path);

                return acceptedExtensions.Contains(
                        extension,
                        StringComparer.OrdinalIgnoreCase)
                    && string.Equals(
                        stem,
                        assetName,
                        StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    public AssetSession CreateNoReferenceMainSession(
        AppSettings settings,
        string assetName,
        string sourceImagePath,
        string prompt,
        DateTimeOffset processedAt,
        ProviderTemplateSnapshot? providerTemplate = null,
        string? sourceRequestKey = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(assetName);
        ArgumentNullException.ThrowIfNull(sourceImagePath);
        ArgumentNullException.ThrowIfNull(prompt);

        var settingsValidation = _validationService.ValidateProcessingSettings(settings);
        if (!settingsValidation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, settingsValidation.Errors));
        }

        var nameValidation = _validationService.ValidateAssetName(assetName, settings.AcceptedExtensions);
        if (!nameValidation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, nameValidation.Errors));
        }

        var imageValidation = _validationService.ValidateImageFile(sourceImagePath, settings.AcceptedExtensions);
        if (!imageValidation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, imageValidation.Errors));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("Prompt must not be empty.");
        }

        var normalizedRoot = ValidationService.NormalizePath(settings.AssetRootFolder);
        var assetFolder = ValidationService.NormalizePath(Path.Combine(settings.AssetRootFolder, assetName));
        var actualParent = Path.GetDirectoryName(assetFolder);

        if (actualParent is null || !ValidationService.PathsEqual(actualParent, normalizedRoot))
        {
            throw new InvalidOperationException("Asset folder must be a direct child of AssetRootFolder.");
        }

        if (Directory.Exists(assetFolder) && ValidationService.IsReparsePoint(assetFolder))
        {
            throw new InvalidOperationException("Asset folder is a reparse point and cannot be used safely.");
        }

        var ingameFolder = ValidationService.NormalizePath(Path.Combine(assetFolder, AppConstants.IngameFolderName));
        if (Directory.Exists(ingameFolder) && ValidationService.IsReparsePoint(ingameFolder))
        {
            throw new InvalidOperationException("Ingame folder is a reparse point and cannot be used safely.");
        }

        var mainFilename = Path.GetFileName(sourceImagePath);
        var rootMainDestination = Path.Combine(assetFolder, mainFilename);
        var finalProvenance = Path.Combine(assetFolder, AppConstants.FinalProvenanceFileName);
        var ingameFilename = AssetNaming.BuildIngameFilename(assetName, mainFilename);

        if (File.Exists(rootMainDestination))
        {
            throw new IOException($"Main image destination already exists: {rootMainDestination}");
        }

        if (File.Exists(finalProvenance))
        {
            throw new IOException($"Final provenance already exists: {finalProvenance}");
        }

        var existingVariants = FindExistingIngameVariants(ingameFolder, assetName, settings.AcceptedExtensions);
        if (existingVariants.Count > 0)
        {
            throw new IOException($"An ingame asset variant already exists: {existingVariants[0]}");
        }

        var sourceHash = ComputeSha256(sourceImagePath);
        var projectLabel = AssetNaming.DeriveProjectLabel(settings.AssetRootFolder);

        var session = new AssetSession
        {
            SchemaVersion =
                providerTemplate is null
                    ? 2
                    : 3,

            WorkflowMode = AssetWorkflowMode.NoReference,

            ProviderTemplate =
                providerTemplate?.Clone(),

            SourceRequestKey =
                sourceRequestKey,

            ProjectName = projectLabel,
            AssetRootFolder = settings.AssetRootFolder,
            AssetFolderName = assetName,
            AssetFolder = assetFolder,
            ReferenceSourcePath = string.Empty,
            ReferenceDestinationPath = string.Empty,
            ReferenceFilename = string.Empty,
            ReferenceProvenancePath = string.Empty,
            ReferenceHash = string.Empty,
            ReferenceProcessedAt = default,
            WasAssetFolderCreatedByTool = !Directory.Exists(assetFolder),
            WasReferenceFolderCreatedByTool = false,
            WasIngameFolderCreatedByTool = !Directory.Exists(ingameFolder),
            IsMainCommitting = true,
            MainFilename = mainFilename,
            MainPrompt = prompt,
            MainProcessedAt = processedAt,
            MainHash = sourceHash,
            MainTransactionId = Guid.NewGuid().ToString("N")
        };

        var provenance =
            _templateService.RenderFinalForSession(
                session,
                mainFilename,
                prompt,
                processedAt);

        session.MainProvenanceHash =
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    new UTF8Encoding(false).GetBytes(provenance)))
                .ToLowerInvariant();

        return session;
    }

    /// <summary>
    /// R2-006/R3-014: Prepares Main commit metadata in memory without performing filesystem mutations.
    /// Caller is responsible for persisting the session before calling ProcessMainImage.
    /// </summary>
    public AssetSession PrepareMainCommit(
        AssetSession session,
        IReadOnlyCollection<string> acceptedExtensions,
        string sourceImagePath,
        string prompt,
        DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(acceptedExtensions);
        ArgumentNullException.ThrowIfNull(sourceImagePath);
        ArgumentNullException.ThrowIfNull(prompt);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt must not be empty.", nameof(prompt));
        }

        var imageValidation = _validationService.ValidateImageFile(sourceImagePath, acceptedExtensions);
        if (!imageValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, imageValidation.Errors));
        }

        var sourceHash = ComputeSha256(sourceImagePath);

        if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted &&
            !string.IsNullOrWhiteSpace(session.ReferenceHash) &&
            string.Equals(sourceHash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected main image is identical to the reference image.");
        }

        var mainFilename = Path.GetFileName(sourceImagePath);
        var projLabel = session.ProjectName;
        var provText = _templateService.RenderFinalForSession(
            session,
            mainFilename,
            prompt,
            processedAt);
        var provHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new UTF8Encoding(false).GetBytes(provText))).ToLowerInvariant();

        session.IsMainCommitting = true;
        session.MainFilename = mainFilename;
        session.MainPrompt = prompt;
        session.MainProcessedAt = processedAt;
        session.MainHash = sourceHash;
        session.MainTransactionId = Guid.NewGuid().ToString("N");
        session.MainProvenanceHash = provHash;
        session.WasIngameFolderCreatedByTool = !Directory.Exists(session.GetIngameFolderPath());

        return session;
    }

    internal string ProcessMainImage(
        AssetSession session,
        IReadOnlyCollection<string> acceptedExtensions,
        string sourceImagePath,
        string prompt,
        DateTimeOffset processedAt)
    {
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

        if (!session.IsMainCommitting)
        {
            throw new InvalidOperationException(
                "ProcessMainImage requires a prepared and durably persisted Main transaction.");
        }

        if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted)
        {
            var referenceValidation =
                _validationService.ValidateExactReferenceOutput(
                    session,
                    _templateService);

            if (!referenceValidation.IsValid)
            {
                throw new InvalidDataException(
                    string.Join(
                        Environment.NewLine,
                        referenceValidation.Errors));
            }
        }

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

        var mainFilename =
            Path.GetFileName(
                sourceImagePath);

        var ingameFilename =
            AssetNaming.BuildIngameFilename(
                session.AssetFolderName,
                sourceImagePath);

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

        var rootMainDestination =
            Path.Combine(
                session.AssetFolder,
                mainFilename);

        var finalProvenance =
            Path.Combine(
                session.AssetFolder,
                AppConstants.FinalProvenanceFileName);

        var ingameFolder =
            session.GetIngameFolderPath();

        var ingameDestination =
            Path.Combine(
                ingameFolder,
                ingameFilename);

        if (File.Exists(rootMainDestination))
        {
            throw new IOException(
                $"Main image destination already exists: {rootMainDestination}");
        }

        if (File.Exists(finalProvenance))
        {
            throw new IOException(
                $"Final provenance already exists: {finalProvenance}");
        }

        if (Directory.Exists(ingameFolder) && ValidationService.IsReparsePoint(ingameFolder))
        {
            throw new InvalidOperationException(
                "Ingame folder is a reparse point and cannot be used safely.");
        }

        var existingVariants = FindExistingIngameVariants(ingameFolder, session.AssetFolderName, acceptedExtensions);
        if (existingVariants.Count > 0)
        {
            throw new IOException(
                $"An ingame asset variant already exists: {existingVariants[0]}");
        }

        if (!Directory.Exists(session.AssetFolder))
        {
            Directory.CreateDirectory(session.AssetFolder);
            session.WasAssetFolderCreatedByTool = true;
        }

        if (!Directory.Exists(ingameFolder))
        {
            Directory.CreateDirectory(ingameFolder);
            session.WasIngameFolderCreatedByTool = true;
        }

        if (Directory.Exists(ingameFolder) && ValidationService.IsReparsePoint(ingameFolder))
        {
            throw new InvalidOperationException(
                "Ingame folder became a reparse point and cannot be used safely.");
        }

        var tempMainPath =
            !string.IsNullOrWhiteSpace(session.GetMainTempImagePath())
                ? session.GetMainTempImagePath()
                : Path.Combine(
                    session.AssetFolder,
                    $".main-{Guid.NewGuid():N}{Path.GetExtension(mainFilename)}");

        var tempIngamePath =
            !string.IsNullOrWhiteSpace(session.GetMainTempIngamePath())
                ? session.GetMainTempIngamePath()
                : Path.Combine(
                    ingameFolder,
                    $".main-ingame-{Guid.NewGuid():N}{Path.GetExtension(mainFilename)}");

        var tempCopied = false;
        var tempIngameCopied = false;
        var mainPromoted = false;
        var ingamePromoted = false;
        var provenanceWritten = false;

        string? sourceHash = null;
        string? mainHash = null;
        string? provenance = null;

        var tempProvenanceCreatedByThisCall = false;

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
                throw new IOException(
                    $"Main source changed between validation/hash and copy: expected {session.MainHash}, got {sourceHash}.");
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

            if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted &&
                string.Equals(
                    mainHash,
                    session.ReferenceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The selected main image is identical to the reference image.");
            }

            CopyFileWithoutOverwrite(
                tempMainPath,
                tempIngamePath);

            tempIngameCopied = true;

            OnIngameTempCopiedHook?.Invoke(
                tempIngamePath);

            var ingameHash =
                ComputeSha256(
                    tempIngamePath);

            if (!string.Equals(
                    mainHash,
                    ingameHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Ingame copy bytes do not match Main image bytes.");
            }

            var generationDate =
                processedAt
                    .ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);

            provenance =
                _templateService.RenderFinalForSession(
                    session,
                    mainFilename,
                    prompt,
                    processedAt);

            var renderedProvHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    new UTF8Encoding(false).GetBytes(provenance)))
                .ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(session.MainProvenanceHash) &&
                !string.Equals(renderedProvHash, session.MainProvenanceHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Final provenance content changed after the Main transaction journal was prepared. No canonical Main output was committed.");
            }

            // BUG-R13-004: Write temporary provenance without silently overwriting pre-existing files
            WriteTextDurablyToReservedPath(tempProvenancePath, provenance);
            tempProvenanceCreatedByThisCall = true;

            OnBeforeMainStagingAuthorityGate?.Invoke(session);

            RequireMainStagingAuthority(session, tempMainPath, tempIngamePath, tempProvenancePath);

            var forwardProvHash = session.MainProvenanceHash ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new UTF8Encoding(false).GetBytes(provenance))).ToLowerInvariant();

            MoveHashOwnedFileWithoutOverwrite(
                tempProvenancePath,
                finalProvenance,
                forwardProvHash,
                "Final provenance",
                () => ValidateSessionDestructivePathSafety(session));

            tempProvenanceCreatedByThisCall = false;
            provenanceWritten = true;

            MoveHashOwnedFileWithoutOverwrite(
                tempMainPath,
                rootMainDestination,
                mainHash,
                "Main root image",
                () => ValidateSessionDestructivePathSafety(session));

            mainPromoted = true;

            OnMainPromotedHook?.Invoke(
                rootMainDestination);

            MoveHashOwnedFileWithoutOverwrite(
                tempIngamePath,
                ingameDestination,
                mainHash,
                "Ingame image",
                () => ValidateSessionDestructivePathSafety(session));

            ingamePromoted = true;

            OnIngamePromotedHook?.Invoke(
                ingameDestination);

            session.IsMainCommitting = true;
            session.MainTransactionId ??= Guid.NewGuid().ToString("N");
            session.MainFilename = mainFilename;
            session.MainHash = mainHash;
            session.MainProvenanceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new UTF8Encoding(false).GetBytes(provenance))).ToLowerInvariant();
            session.MainPrompt = prompt;
            session.MainProcessedAt = processedAt;

            var validation =
                _validationService
                    .ValidateCompleteAsset(
                        session,
                        rootMainDestination,
                        finalProvenance,
                        mainFilename,
                        generationDate,
                        prompt,
                        _templateService,
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
            var pathSafety = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
            if (!pathSafety.IsValid || ValidationService.IsReparsePoint(session.AssetFolder) || ValidationService.IsReparsePoint(session.GetIngameFolderPath()))
            {
                var errorDetails = pathSafety.IsValid
                    ? "Asset or Ingame folder is a reparse point."
                    : string.Join(Environment.NewLine, pathSafety.Errors);

                throw new AssetProcessingException(
                    "Main Image processing failed and local rollback was not attempted because the destination hierarchy is no longer safe."
                    + Environment.NewLine
                    + errorDetails,
                    primaryException,
                    rollbackComplete: false);
            }

            var rollbackErrors =
                new List<string>();

            var expectedProvHash = session.MainProvenanceHash ?? (provenance is not null
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new UTF8Encoding(false).GetBytes(provenance))).ToLowerInvariant()
                : null);

            // BUG-R16-001 & BUG-R11-001 & BUG-R12-001 & BUG-R13-001: Verify current path safety and hash ownership at deletion boundary
            if (provenanceWritten)
            {
                if (expectedProvHash is not null)
                {
                    TryDeleteHashOwnedFileWithError(
                        finalProvenance,
                        expectedProvHash,
                        "Final provenance",
                        () => ValidateSessionDestructivePathSafety(session),
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Final provenance at '{finalProvenance}' expected hash could not be determined. File preserved.");
                }
            }

            if (mainPromoted)
            {
                if (sourceHash is not null)
                {
                    TryDeleteHashOwnedFileWithError(
                        rootMainDestination,
                        sourceHash,
                        "Main image",
                        () => ValidateSessionDestructivePathSafety(session),
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Main image at '{rootMainDestination}' expected hash could not be determined. File preserved.");
                }
            }

            if (ingamePromoted)
            {
                if (sourceHash is not null)
                {
                    TryDeleteHashOwnedFileWithError(
                        ingameDestination,
                        sourceHash,
                        "Ingame image",
                        () => ValidateSessionDestructivePathSafety(session),
                        rollbackErrors);
                }
                else
                {
                    rollbackErrors.Add(
                        $"Ingame image at '{ingameDestination}' expected hash could not be determined. File preserved.");
                }
            }

            // BUG-R17-002 & BUG-R12-001 & BUG-R13-001: Verify current temp main image ownership before deleting
            if (tempCopied && !mainPromoted)
            {
                if (File.Exists(tempMainPath))
                {
                    if (sourceHash is not null)
                    {
                        TryDeleteHashOwnedFileWithError(
                            tempMainPath,
                            sourceHash,
                            "Main temp image",
                            () => ValidateSessionDestructivePathSafety(session),
                            rollbackErrors);
                    }
                    else
                    {
                        rollbackErrors.Add(
                            $"Main temp image at '{tempMainPath}' expected hash could not be determined. File preserved.");
                    }
                }
            }

            if (tempIngameCopied && !ingamePromoted)
            {
                if (File.Exists(tempIngamePath))
                {
                    if (sourceHash is not null)
                    {
                        TryDeleteHashOwnedFileWithError(
                            tempIngamePath,
                            sourceHash,
                            "Ingame temp image",
                            () => ValidateSessionDestructivePathSafety(session),
                            rollbackErrors);
                    }
                    else
                    {
                        rollbackErrors.Add(
                            $"Ingame temp image at '{tempIngamePath}' expected hash could not be determined. File preserved.");
                    }
                }
            }

            // BUG-R13-004, BUG-R17-002, BUG-R11-001, BUG-R12-001 & BUG-R13-001: Verify temp provenance ownership before deleting
            if (tempProvenanceCreatedByThisCall)
            {
                if (File.Exists(tempProvenancePath))
                {
                    if (expectedProvHash is not null)
                    {
                        TryDeleteHashOwnedFileWithError(
                            tempProvenancePath,
                            expectedProvHash,
                            "Main temp provenance",
                            () => ValidateSessionDestructivePathSafety(session),
                            rollbackErrors);
                    }
                    else
                    {
                        rollbackErrors.Add(
                            $"Main temp provenance at '{tempProvenancePath}' expected hash could not be determined. File preserved.");
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

            if (primaryException is IOException ioEx)
            {
                throw new IOException($"Main Image processing failed: {ioEx.Message}", ioEx);
            }

            if (primaryException is InvalidDataException idEx)
            {
                throw new InvalidDataException($"Main Image processing failed: {idEx.Message}", idEx);
            }

            throw;
        }
    }

    internal ValidationResult RollbackMain(
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

        var targetIngameFilename = !string.IsNullOrWhiteSpace(session.MainFilename)
            ? session.GetIngameFilename()
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(mainFilename) &&
            !string.Equals(session.MainFilename, mainFilename, StringComparison.Ordinal) &&
            !string.Equals(targetIngameFilename, mainFilename, StringComparison.Ordinal))
        {
            return ValidationResult.Failure("mainFilename does not match session.MainFilename.");
        }

        var normalizedAssetFolder = ValidationService.NormalizePath(session.AssetFolder);
        var rootMainPath = Path.Combine(session.AssetFolder, session.MainFilename);
        var normalizedMainPath = ValidationService.NormalizePath(rootMainPath);

        if (!ValidationService.PathsEqual(Path.GetDirectoryName(normalizedMainPath) ?? "", normalizedAssetFolder))
        {
            return ValidationResult.Failure("mainFilename escapes the session asset folder.");
        }

        // BUG-R9-001: If main destination file exists on disk, verify its SHA-256 matches session.MainHash
        if (File.Exists(rootMainPath))
        {
            try
            {
                var existingHash = ComputeSha256(rootMainPath);
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

        var ingamePath = session.GetIngameImagePath();
        if (!string.IsNullOrWhiteSpace(ingamePath) && File.Exists(ingamePath))
        {
            try
            {
                var existingIngameHash = ComputeSha256(ingamePath);
                if (!string.Equals(existingIngameHash, session.MainHash, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Failure(
                        $"Ingame image on disk does not match session MainHash (expected {session.MainHash}, found {existingIngameHash}). Refusing to delete unknown file.");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure(
                    $"Could not compute Ingame image SHA-256 hash: {ex.Message}");
            }
        }

        var provenancePath =
            Path.Combine(
                session.AssetFolder,
                AppConstants.FinalProvenanceFileName);

        // BUG-R12-001 & BUG-R13-001 & BUG-R15-003: Verify exact ownership and derive raw hash of final provenance before deleting
        string? finalProvenanceRawHash = null;

        if (File.Exists(provenancePath))
        {
            var provValidation = _validationService.TryGetExactFinalProvenanceRawHash(
                session,
                provenancePath,
                _templateService,
                out finalProvenanceRawHash);

            if (!provValidation.IsValid || string.IsNullOrWhiteSpace(finalProvenanceRawHash))
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

        var tempIngame = session.GetMainTempIngamePath();
        if (!string.IsNullOrWhiteSpace(tempIngame) && File.Exists(tempIngame))
        {
            try
            {
                var tempIngameHash = ComputeSha256(tempIngame);
                if (!string.Equals(tempIngameHash, session.MainHash, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Failure(
                        $"Ingame temp image at '{tempIngame}' hash does not match session MainHash (expected {session.MainHash}, found {tempIngameHash}). Refusing to delete unknown file.");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure(
                    $"Could not compute Ingame temp image SHA-256 hash: {ex.Message}");
            }
        }

        var tempProv = session.GetMainTempProvenancePath();
        string? tempProvenanceRawHash = null;

        if (!string.IsNullOrWhiteSpace(tempProv) && File.Exists(tempProv))
        {
            var tempProvValidation = _validationService.TryGetExactFinalProvenanceRawHash(
                session,
                tempProv,
                _templateService,
                out tempProvenanceRawHash);

            if (!tempProvValidation.IsValid || string.IsNullOrWhiteSpace(tempProvenanceRawHash))
            {
                return ValidationResult.Failure(
                    $"Main temp provenance at '{tempProv}' does not match session state ({string.Join("; ", tempProvValidation.Errors)}). Refusing to delete unknown file.");
            }
        }

        OnBeforeRollbackMainFinalPathGate?.Invoke(session);

        var finalPathSafety =
            ValidationService.ValidateSessionPathsForDestructiveOperation(session);

        if (!finalPathSafety.IsValid)
        {
            return finalPathSafety;
        }

        if (ValidationService.IsReparsePoint(session.AssetFolder))
        {
            return ValidationResult.Failure(
                "Asset folder became a reparse point before Main rollback. No files were deleted.");
        }

        var ingameFolder = session.GetIngameFolderPath();
        if (Directory.Exists(ingameFolder) && ValidationService.IsReparsePoint(ingameFolder))
        {
            return ValidationResult.Failure(
                "Ingame folder became a reparse point before Main rollback. No files were deleted.");
        }

        var errors =
            new List<string>();

        if (File.Exists(provenancePath))
        {
            if (string.IsNullOrWhiteSpace(finalProvenanceRawHash))
            {
                return ValidationResult.Failure(
                    "Final provenance exists but verified raw hash authority is missing.");
            }

            TryDeleteHashOwnedFileWithError(
                provenancePath,
                finalProvenanceRawHash,
                "Final provenance",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (File.Exists(rootMainPath))
        {
            TryDeleteHashOwnedFileWithError(
                rootMainPath,
                session.MainHash!,
                "Main image",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (!string.IsNullOrWhiteSpace(ingamePath) && File.Exists(ingamePath))
        {
            TryDeleteHashOwnedFileWithError(
                ingamePath,
                session.MainHash!,
                "Ingame image",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (!string.IsNullOrWhiteSpace(tempImage) && File.Exists(tempImage))
        {
            TryDeleteHashOwnedFileWithError(
                tempImage,
                session.MainHash!,
                "Main temp image",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (!string.IsNullOrWhiteSpace(tempIngame) && File.Exists(tempIngame))
        {
            TryDeleteHashOwnedFileWithError(
                tempIngame,
                session.MainHash!,
                "Ingame temp image",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (!string.IsNullOrWhiteSpace(tempProv) && File.Exists(tempProv))
        {
            if (string.IsNullOrWhiteSpace(tempProvenanceRawHash))
            {
                return ValidationResult.Failure(
                    "Main temp provenance exists but verified raw hash authority is missing.");
            }

            TryDeleteHashOwnedFileWithError(
                tempProv,
                tempProvenanceRawHash,
                "Main temp provenance",
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (session.WasIngameFolderCreatedByTool)
        {
            TryDeleteEmptyDirectoryWithError(
                session.GetIngameFolderPath(),
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (session.WorkflowMode == AssetWorkflowMode.NoReference &&
            session.WasAssetFolderCreatedByTool)
        {
            TryDeleteEmptyDirectoryWithError(
                session.AssetFolder,
                () => ValidateSessionDestructivePathSafety(session),
                errors);
        }

        if (errors.Count == 0)
        {
            session.ResetMainCommitMetadata();
            return ValidationResult.Success();
        }

        return ValidationResult.Failure(errors);
    }

    private void RequireMainStagingAuthority(
        AssetSession session,
        string tempMainPath,
        string tempIngamePath,
        string tempProvenancePath)
    {
        if (!session.IsMainCommitting)
        {
            throw new InvalidOperationException("No active Main transaction exists.");
        }

        if (string.IsNullOrWhiteSpace(session.MainHash))
        {
            throw new InvalidDataException("MainHash is missing.");
        }

        if (string.IsNullOrWhiteSpace(session.MainProvenanceHash))
        {
            throw new InvalidDataException("MainProvenanceHash is missing.");
        }

        if (!File.Exists(tempMainPath))
        {
            throw new IOException("Main staging image is missing.");
        }

        var tempMainHash = ComputeSha256(tempMainPath);
        if (!string.Equals(tempMainHash, session.MainHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Main staging image no longer matches the durable MainHash.");
        }

        if (!File.Exists(tempIngamePath))
        {
            throw new IOException("Ingame staging image is missing.");
        }

        var tempIngameHash = ComputeSha256(tempIngamePath);
        if (!string.Equals(tempIngameHash, session.MainHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Ingame staging image no longer matches the durable MainHash.");
        }

        if (!File.Exists(tempProvenancePath))
        {
            throw new IOException("Main staging provenance is missing.");
        }

        var tempProvHash = ComputeSha256(tempProvenancePath);
        if (!string.Equals(tempProvHash, session.MainProvenanceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Main staging provenance no longer matches the durable MainProvenanceHash.");
        }

        var pathValidation = ValidationService.ValidateSessionPathsForDestructiveOperation(session);
        if (!pathValidation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, pathValidation.Errors));
        }

        if (ValidationService.IsReparsePoint(session.AssetFolder))
        {
            throw new IOException("Asset folder became a reparse point before Main promotion.");
        }

        var ingameFolder = session.GetIngameFolderPath();
        if (ValidationService.IsReparsePoint(ingameFolder))
        {
            throw new IOException("Ingame folder became a reparse point before Main promotion.");
        }
    }
}

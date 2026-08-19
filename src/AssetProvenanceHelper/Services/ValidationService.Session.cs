using System.Globalization;
using System.Text;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed partial class ValidationService
{
    [ThreadStatic]
    internal static Func<string, IEnumerable<string>>? EnumerateFilesInFolderHook;

    public ValidationResult ValidateSession(
        AssetSession session)
    {
        var errors =
            new List<string>();

        ValidateSessionCommon(
            session,
            errors);

        if (session.WorkflowMode == AssetWorkflowMode.NoReference)
        {
            ValidateNoReferenceSessionState(
                session,
                errors);
        }
        else
        {
            ValidateReferenceSessionState(
                session,
                errors);
        }

        ValidateMainCommitMetadata(
            session,
            errors);

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    private void ValidateSessionCommon(
        AssetSession session,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(
                session.ProjectName))
        {
            errors.Add(
                "Session ProjectName is missing.");
        }

        if (string.IsNullOrWhiteSpace(
                session.AssetRootFolder))
        {
            errors.Add(
                "Session AssetRootFolder is missing.");
        }
        else if (!Directory.Exists(
                     session.AssetRootFolder))
        {
            errors.Add(
                $"Session AssetRootFolder does not exist: {session.AssetRootFolder}");
        }
        else if (IsReparsePoint(session.AssetRootFolder))
        {
            errors.Add(
                "Session AssetRootFolder is a reparse point (junction or symbolic link) and cannot be used safely.");
        }

        if (string.IsNullOrWhiteSpace(
                session.AssetFolderName))
        {
            errors.Add(
                "Session AssetFolderName is missing.");
        }
        else
        {
            var folderNameValidation =
                ValidateAssetFolderName(
                    session.AssetFolderName);

            if (!folderNameValidation.IsValid)
            {
                errors.AddRange(
                    folderNameValidation.Errors.Select(
                        error =>
                            $"Session AssetFolderName is invalid: {error}"));
            }
        }

        if (string.IsNullOrWhiteSpace(
                session.AssetFolder))
        {
            errors.Add(
                "Session AssetFolder is missing.");
        }
        else if (!string.IsNullOrWhiteSpace(session.AssetRootFolder) &&
                 !string.IsNullOrWhiteSpace(session.AssetFolderName))
        {
            try
            {
                var normalizedRoot =
                    NormalizePath(
                        session.AssetRootFolder);

                var expectedAssetFolder =
                    NormalizePath(
                        Path.Combine(
                            session.AssetRootFolder,
                            session.AssetFolderName));

                var actualAssetFolder =
                    NormalizePath(
                        session.AssetFolder);

                if (!string.Equals(
                        expectedAssetFolder,
                        actualAssetFolder,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "Session AssetFolder does not match AssetRootFolder + AssetFolderName.");
                }

                var actualParent =
                    Path.GetDirectoryName(
                        actualAssetFolder);

                if (actualParent is null ||
                    !PathsEqual(
                        actualParent,
                        normalizedRoot))
                {
                    errors.Add(
                        "Session AssetFolder is not a direct child of AssetRootFolder.");
                }
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Session asset path is invalid: {ex.Message}");
            }
        }
    }

    private void ValidateNoReferenceSessionState(
        AssetSession session,
        List<string> errors)
    {
        if (session.CancelPhase != CancelPhase.None)
        {
            errors.Add(
                "Session CancelPhase must be None in NoReference mode.");
        }

        if (!string.IsNullOrWhiteSpace(session.CancellationId))
        {
            errors.Add(
                "Session CancellationId must be empty in NoReference mode.");
        }

        if (!string.IsNullOrWhiteSpace(session.ReferenceSourcePath))
        {
            errors.Add(
                "Session ReferenceSourcePath must be empty in NoReference mode.");
        }

        if (!string.IsNullOrWhiteSpace(session.ReferenceDestinationPath))
        {
            errors.Add(
                "Session ReferenceDestinationPath must be empty in NoReference mode.");
        }

        if (!string.IsNullOrWhiteSpace(session.ReferenceFilename))
        {
            errors.Add(
                "Session ReferenceFilename must be empty in NoReference mode.");
        }

        if (!string.IsNullOrWhiteSpace(session.ReferenceProvenancePath))
        {
            errors.Add(
                "Session ReferenceProvenancePath must be empty in NoReference mode.");
        }

        if (!string.IsNullOrWhiteSpace(session.ReferenceHash))
        {
            errors.Add(
                "Session ReferenceHash must be empty in NoReference mode.");
        }

        if (session.ReferenceProcessedAt != default)
        {
            errors.Add(
                "Session ReferenceProcessedAt must be default in NoReference mode.");
        }

        if (!session.IsMainCommitting)
        {
            errors.Add(
                "Session IsMainCommitting must be true for NoReference mode.");
        }

        if (!string.IsNullOrWhiteSpace(session.AssetFolder))
        {
            if (Directory.Exists(session.AssetFolder))
            {
                if (IsReparsePoint(session.AssetFolder))
                {
                    errors.Add(
                        "Session AssetFolder is a reparse point (junction or symbolic link) and cannot be used safely.");
                }
            }
            else if (!session.WasAssetFolderCreatedByTool)
            {
                errors.Add(
                    $"Session AssetFolder does not exist: {session.AssetFolder}");
            }
        }
    }

    private void ValidateReferenceSessionState(
        AssetSession session,
        List<string> errors)
    {
        if (session.ReferenceProcessedAt == default)
        {
            errors.Add(
                "Session ReferenceProcessedAt is missing.");
        }

        if (string.IsNullOrWhiteSpace(
                session.ReferenceFilename))
        {
            errors.Add(
                "Session ReferenceFilename is missing.");
        }
        else if (!string.Equals(
                     Path.GetFileName(
                         session.ReferenceFilename),
                     session.ReferenceFilename,
                     StringComparison.Ordinal))
        {
            errors.Add(
                "Session ReferenceFilename must contain only a filename, not a path.");
        }

        if (string.IsNullOrWhiteSpace(
                session.ReferenceHash))
        {
            errors.Add(
                "Session ReferenceHash is missing.");
        }
        else if (
            session.ReferenceHash.Length != 64 ||
            session.ReferenceHash.Any(
                character =>
                    !Uri.IsHexDigit(character)))
        {
            errors.Add(
                "Session ReferenceHash is not a valid SHA-256 hexadecimal value.");
        }

        if (session.CancelPhase == CancelPhase.None)
        {
            if (!string.IsNullOrWhiteSpace(session.CancellationId))
            {
                errors.Add(
                    "Session CancellationId must be empty when CancelPhase is None.");
            }

            if (!File.Exists(session.ReferenceDestinationPath))
            {
                errors.Add(
                    $"Session reference image does not exist: {session.ReferenceDestinationPath}");
            }

            if (!File.Exists(session.ReferenceProvenancePath))
            {
                errors.Add(
                    $"Session reference provenance does not exist: {session.ReferenceProvenancePath}");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(session.CancellationId) ||
                session.CancellationId.Length != 32 ||
                session.CancellationId.Any(character => !Uri.IsHexDigit(character)))
            {
                errors.Add(
                    "Session CancellationId is missing or is not a valid 32-character SHA/GUID hexadecimal value.");
            }

            var tempRef = session.GetCancelTempReferencePath();
            var tempProv = session.GetCancelTempProvenancePath();

            if (session.CancelPhase == CancelPhase.Prepared)
            {
                var origRefExists = File.Exists(session.ReferenceDestinationPath);
                var tempRefExists = File.Exists(tempRef);
                var origProvExists = File.Exists(session.ReferenceProvenancePath);
                var tempProvExists = File.Exists(tempProv);

                if ((origRefExists && tempRefExists) || (!origRefExists && !tempRefExists))
                {
                    errors.Add("Session in Prepared cancel phase has inconsistent reference file state (both exist or neither exists).");
                }

                if ((origProvExists && tempProvExists) || (!origProvExists && !tempProvExists))
                {
                    errors.Add("Session in Prepared cancel phase has inconsistent provenance file state (both exist or neither exists).");
                }
            }
            else if (session.CancelPhase == CancelPhase.FilesRenamed)
            {
                if (File.Exists(session.ReferenceDestinationPath))
                {
                    errors.Add("Session in FilesRenamed cancel phase but original reference file still exists.");
                }

                if (File.Exists(session.ReferenceProvenancePath))
                {
                    errors.Add("Session in FilesRenamed cancel phase but original provenance file still exists.");
                }
            }
            else
            {
                errors.Add($"Session CancelPhase '{session.CancelPhase}' is unrecognized.");
            }
        }

        if (errors.Count == 0 &&
            !Directory.Exists(
                session.AssetFolder))
        {
            errors.Add(
                $"Session AssetFolder does not exist: {session.AssetFolder}");
        }

        // BUG-007: Reject reparse points (junctions / symlinks) in the asset
        // folder chain.
        if (errors.Count == 0 &&
            IsReparsePoint(session.AssetFolder))
        {
            errors.Add(
                "Session AssetFolder is a reparse point (junction or symbolic link) and cannot be used safely.");
        }

        if (errors.Count == 0)
        {
            try
            {
                var referenceFolder =
                    NormalizePath(
                        Path.Combine(
                            session.AssetFolder,
                            AppConstants.ReferenceFolderName));

                var expectedReferencePath =
                    NormalizePath(
                        Path.Combine(
                            referenceFolder,
                            session.ReferenceFilename));

                var expectedProvenancePath =
                    NormalizePath(
                        Path.Combine(
                            referenceFolder,
                            AppConstants.ReferenceProvenanceFileName));

                var referenceParent =
                    Path.GetDirectoryName(
                        expectedReferencePath);

                if (referenceParent is null ||
                    !PathsEqual(
                        referenceParent,
                        referenceFolder))
                {
                    errors.Add(
                        "Session reference image path escapes the reference folder.");
                }

                if (!PathsEqual(
                        expectedReferencePath,
                        session.ReferenceDestinationPath))
                {
                    errors.Add(
                        "Session reference destination path is inconsistent.");
                }

                if (!PathsEqual(
                        expectedProvenancePath,
                        session.ReferenceProvenancePath))
                {
                    errors.Add(
                        "Session reference provenance path is inconsistent.");
                }

                // BUG-007: Also check the reference subfolder for reparse points
                var referenceFolderRaw =
                    Path.Combine(
                        session.AssetFolder,
                        AppConstants.ReferenceFolderName);

                if (Directory.Exists(referenceFolderRaw) &&
                    IsReparsePoint(referenceFolderRaw))
                {
                    errors.Add(
                        "Session reference folder is a reparse point and cannot be used safely.");
                }
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Session reference path is invalid: {ex.Message}");
            }
        }

        if (errors.Count == 0 && session.CancelPhase == CancelPhase.None)
        {
            try
            {
                var actualHash =
                    ComputeSha256(
                        session.ReferenceDestinationPath);

                if (!string.Equals(
                        actualHash,
                        session.ReferenceHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "Session ReferenceHash does not match the current reference image.");
                }
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Could not verify Session ReferenceHash: {ex.Message}");
            }
        }
    }

    private void ValidateMainCommitMetadata(
        AssetSession session,
        List<string> errors)
    {
        if (session.IsMainCommitting)
        {
            if (string.IsNullOrWhiteSpace(session.MainFilename) ||
                !string.Equals(
                    Path.GetFileName(session.MainFilename),
                    session.MainFilename,
                    StringComparison.Ordinal))
            {
                errors.Add(
                    "Session IsMainCommitting is true but MainFilename is missing or contains path separators.");
            }

            if (string.IsNullOrWhiteSpace(session.MainPrompt))
            {
                errors.Add(
                    "Session IsMainCommitting is true but MainPrompt is missing or blank.");
            }

            if (!session.MainProcessedAt.HasValue ||
                session.MainProcessedAt.Value == default)
            {
                errors.Add(
                    "Session IsMainCommitting is true but MainProcessedAt is missing.");
            }

            if (string.IsNullOrWhiteSpace(session.MainHash) ||
                session.MainHash.Length != 64 ||
                session.MainHash.Any(c => !Uri.IsHexDigit(c)))
            {
                errors.Add(
                    "Session IsMainCommitting is true but MainHash is missing or is not a valid 64-character SHA-256 hexadecimal value.");
            }

            // BUG-R16-002: MainTransactionId is mandatory for active Main commits
            if (string.IsNullOrWhiteSpace(session.MainTransactionId) ||
                session.MainTransactionId.Length != 32 ||
                session.MainTransactionId.Any(c => !Uri.IsHexDigit(c)))
            {
                errors.Add(
                    "Session IsMainCommitting is true but MainTransactionId is missing or invalid.");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(session.MainTransactionId))
            {
                errors.Add(
                    "Session IsMainCommitting is false but MainTransactionId is set.");
            }
        }
    }

    public ValidationResult ValidateReferenceProvenanceContent(
        AssetSession session,
        string provenancePath)
    {
        if (!File.Exists(provenancePath))
        {
            return ValidationResult.Failure(
                $"Provenance file does not exist: {provenancePath}");
        }

        var errors =
            new List<string>();

        string provenance;

        try
        {
            provenance =
                File.ReadAllText(
                    provenancePath,
                    Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                $"Could not read reference provenance: {ex.Message}");
        }

        var generationDate =
            session.ReferenceProcessedAt
                .ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

        RequireContains(
            provenance,
            $"Asset ID: {session.ReferenceFilename}",
            "Reference provenance does not contain the expected Asset ID.",
            errors);

        RequireContains(
            provenance,
            $"Project: {session.ProjectName}",
            "Reference provenance does not contain the expected Project value.",
            errors);

        RequireContains(
            provenance,
            $"Generation date: {generationDate}",
            "Reference provenance does not contain the expected Generation Date.",
            errors);

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateExactReferenceOutput(
        AssetSession session,
        TemplateService templateService)
    {
        var normal = ValidateReferenceOutput(session);
        if (!normal.IsValid)
        {
            return normal;
        }

        return ValidateExactReferenceProvenanceOwnership(
            session,
            session.ReferenceProvenancePath,
            templateService);
    }

    public ValidationResult ValidateReferenceOutput(
        AssetSession session)
    {
        var sessionValidation =
            ValidateSession(session);

        if (!sessionValidation.IsValid)
        {
            return sessionValidation;
        }

        return ValidateReferenceProvenanceContent(
            session,
            session.ReferenceProvenancePath);
    }

    public ValidationResult ValidateFinalProvenanceContent(
        AssetSession session,
        string finalProvenancePath,
        string mainFilename,
        string mainGenerationDate,
        string prompt)
    {
        if (!File.Exists(finalProvenancePath))
        {
            return ValidationResult.Failure(
                $"Final provenance does not exist: {finalProvenancePath}");
        }

        var errors =
            new List<string>();

        string finalText;

        try
        {
            finalText =
                File.ReadAllText(
                    finalProvenancePath,
                    Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                $"Could not read final provenance: {ex.Message}");
        }

        RequireContains(
            finalText,
            $"Asset ID: {mainFilename}",
            "Final provenance does not contain the expected Main Asset ID.",
            errors);

        if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted)
        {
            RequireContains(
                finalText,
                session.ReferenceFilename,
                "Final provenance does not contain ReferenceFilename.",
                errors);
        }

        RequireContains(
            finalText,
            $"Project: {session.ProjectName}",
            "Final provenance does not contain the expected Project value.",
            errors);

        RequireContains(
            finalText,
            $"Generation date: {mainGenerationDate}",
            "Final provenance does not contain the expected Main Generation Date.",
            errors);

        RequireContains(
            finalText,
            prompt,
            "Final provenance does not contain the exact prompt.",
            errors);

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateCompleteAsset(
        AssetSession session,
        string mainImagePath,
        string finalProvenancePath,
        string mainFilename,
        string mainGenerationDate,
        string prompt,
        TemplateService templateService,
        string? expectedMainHash = null)
    {
        var errors =
            new List<string>();

        if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted)
        {
            var referenceValidation =
                ValidateExactReferenceOutput(
                    session,
                    templateService);

            if (!referenceValidation.IsValid)
            {
                errors.AddRange(
                    referenceValidation.Errors);
            }
        }
        else
        {
            var sessionValidation =
                ValidateSession(session);

            if (!sessionValidation.IsValid)
            {
                errors.AddRange(
                    sessionValidation.Errors);
            }
        }

        string? rootMainHash = null;

        if (!File.Exists(mainImagePath))
        {
            errors.Add(
                $"Main image does not exist: {mainImagePath}");
        }
        else
        {
            try
            {
                rootMainHash = ComputeSha256(mainImagePath);
                if (!string.IsNullOrWhiteSpace(expectedMainHash) &&
                    !string.Equals(rootMainHash, expectedMainHash, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("Main image SHA-256 hash does not match expected MainHash.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Could not compute Main image SHA-256 hash: {ex.Message}");
            }
        }

        var ingamePath = session.GetIngameImagePath();
        if (!string.IsNullOrWhiteSpace(ingamePath))
        {
            if (!File.Exists(ingamePath))
            {
                errors.Add($"Ingame image does not exist: {ingamePath}");
            }
            else
            {
                try
                {
                    var ingameHash = ComputeSha256(ingamePath);
                    if (!string.IsNullOrWhiteSpace(expectedMainHash) &&
                        !string.Equals(ingameHash, expectedMainHash, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add("Ingame image SHA-256 hash does not match expected MainHash.");
                    }

                    if (rootMainHash is not null &&
                        !string.Equals(rootMainHash, ingameHash, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add("Root Main image SHA-256 hash does not match Ingame image SHA-256 hash.");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not compute Ingame image SHA-256 hash: {ex.Message}");
                }
            }
        }

        var finalProvValidation = ValidateExactFinalProvenanceOwnership(
            session,
            finalProvenancePath,
            templateService);

        if (!finalProvValidation.IsValid)
        {
            errors.AddRange(finalProvValidation.Errors);
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateExactReferenceProvenanceOwnership(
        AssetSession session,
        string provenancePath,
        TemplateService templateService)
    {
        if (!File.Exists(provenancePath))
        {
            return ValidationResult.Failure(
                $"Reference provenance file does not exist: {provenancePath}");
        }

        if (!string.IsNullOrWhiteSpace(session.ReferenceProvenanceHash))
        {
            try
            {
                var actualHash = ComputeSha256(provenancePath);
                return string.Equals(actualHash, session.ReferenceProvenanceHash, StringComparison.OrdinalIgnoreCase)
                    ? ValidationResult.Success()
                    : ValidationResult.Failure(
                        "Reference provenance SHA-256 hash does not match stored ReferenceProvenanceHash.");
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure(
                    $"Could not compute reference provenance hash at '{provenancePath}': {ex.Message}");
            }
        }

        // Legacy-session fallback only
        string expectedText;
        try
        {
            var generationDate = session.ReferenceProcessedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            expectedText = templateService.RenderReference(
                session.ReferenceFilename,
                session.ProjectName,
                generationDate);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                $"Could not render expected reference provenance for ownership validation: {ex.Message}");
        }

        string actualText;
        try
        {
            actualText = File.ReadAllText(provenancePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                $"Could not read reference provenance at '{provenancePath}': {ex.Message}");
        }

        if (!string.Equals(actualText, expectedText, StringComparison.Ordinal))
        {
            return ValidationResult.Failure(
                "Reference provenance content does not exactly match expected tool-generated provenance.");
        }

        return ValidationResult.Success();
    }

    public ValidationResult ValidateExactFinalProvenanceOwnership(
        AssetSession session,
        string finalProvenancePath,
        TemplateService templateService)
    {
        if (!File.Exists(finalProvenancePath))
        {
            return ValidationResult.Failure(
                $"Final provenance file does not exist: {finalProvenancePath}");
        }

        if (!string.IsNullOrWhiteSpace(session.MainProvenanceHash))
        {
            try
            {
                var actualHash = ComputeSha256(finalProvenancePath);
                return string.Equals(actualHash, session.MainProvenanceHash, StringComparison.OrdinalIgnoreCase)
                    ? ValidationResult.Success()
                    : ValidationResult.Failure(
                        "Final provenance SHA-256 hash does not match stored MainProvenanceHash.");
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure(
                    $"Could not compute final provenance hash at '{finalProvenancePath}': {ex.Message}");
            }
        }

        // Legacy-session fallback only
        if (string.IsNullOrWhiteSpace(session.MainFilename) ||
            !session.MainProcessedAt.HasValue)
        {
            return ValidationResult.Failure(
                "Legacy Main provenance authority is incomplete.");
        }

        string expectedText;
        try
        {
            var generationDate = session.MainProcessedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            expectedText = session.WorkflowMode switch
            {
                AssetWorkflowMode.ReferenceAssisted =>
                    templateService.RenderFinal(
                        session.MainFilename,
                        session.ReferenceFilename,
                        session.ProjectName,
                        generationDate,
                        session.MainPrompt ?? string.Empty),

                AssetWorkflowMode.NoReference =>
                    templateService.RenderFinalNoReference(
                        session.MainFilename,
                        session.ProjectName,
                        generationDate,
                        session.MainPrompt ?? string.Empty),

                _ => throw new InvalidOperationException($"Unsupported workflow mode: {session.WorkflowMode}")
            };
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                $"Could not render expected final provenance for ownership validation: {ex.Message}");
        }

        string actualText;
        try
        {
            actualText = File.ReadAllText(finalProvenancePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                $"Could not read final provenance at '{finalProvenancePath}': {ex.Message}");
        }

        if (!string.Equals(actualText, expectedText, StringComparison.Ordinal))
        {
            return ValidationResult.Failure(
                "Final provenance content does not exactly match expected tool-generated provenance.");
        }

        return ValidationResult.Success();
    }


    public ValidationResult ValidateReferenceOwnershipForDeletion(
        AssetSession session,
        string? referenceImagePath,
        string? provenancePath,
        TemplateService? templateService = null)
    {
        var pathValidation = ValidateSessionPathsForDestructiveOperation(session);
        if (!pathValidation.IsValid)
        {
            return pathValidation;
        }

        if (!string.IsNullOrWhiteSpace(referenceImagePath) && File.Exists(referenceImagePath))
        {
            try
            {
                var hash = ComputeSha256(referenceImagePath);
                if (!string.Equals(hash, session.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Failure(
                        $"Reference image at '{referenceImagePath}' hash ({hash}) does not match session ReferenceHash ({session.ReferenceHash}). Refusing to delete unknown file.");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.Failure(
                    $"Could not compute reference image SHA-256 hash: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(provenancePath) && File.Exists(provenancePath) && templateService != null)
        {
            var provValidation = ValidateExactReferenceProvenanceOwnership(session, provenancePath, templateService);
            if (!provValidation.IsValid)
            {
                return ValidationResult.Failure(
                    $"Reference provenance at '{provenancePath}' does not match session state ({string.Join("; ", provValidation.Errors)}). Refusing to delete unknown file.");
            }
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Validates a reference replacement transaction ensuring both sessions, deterministic backup paths, and deterministic temp paths are strictly valid and safe.
    /// Safely wraps all path operations against untrusted user data.
    /// </summary>
    public ValidationResult ValidateReferenceReplacementTransaction(
        ReferenceReplacementTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        try
        {
            return ValidateReferenceReplacementTransactionCore(transaction);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                "Replacement transaction contains invalid or unusable path metadata: " + ex.Message);
        }
    }

    private ValidationResult ValidateReferenceReplacementTransactionCore(
        ReferenceReplacementTransaction transaction)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(transaction.TransactionId) ||
            transaction.TransactionId.Length != 32 ||
            transaction.TransactionId.Any(c => !Uri.IsHexDigit(c)))
        {
            errors.Add("TransactionId is missing or is not a valid 32-character hexadecimal string.");
        }

        var oldValidation = ValidateSessionPathsForDestructiveOperation(transaction.OldSession);
        if (!oldValidation.IsValid)
        {
            errors.AddRange(oldValidation.Errors);
        }

        var newValidation = ValidateSessionPathsForDestructiveOperation(transaction.NewSession);
        if (!newValidation.IsValid)
        {
            errors.AddRange(newValidation.Errors);
        }

        if (errors.Count == 0)
        {
            if (!PathsEqual(transaction.OldSession.AssetRootFolder, transaction.NewSession.AssetRootFolder))
            {
                errors.Add("OldSession and NewSession AssetRootFolder do not match.");
            }

            if (!string.Equals(transaction.OldSession.AssetFolderName, transaction.NewSession.AssetFolderName, StringComparison.Ordinal))
            {
                errors.Add("OldSession and NewSession AssetFolderName do not match.");
            }

            if (!PathsEqual(transaction.OldSession.AssetFolder, transaction.NewSession.AssetFolder))
            {
                errors.Add("OldSession and NewSession AssetFolder do not match.");
            }

            if (!string.Equals(transaction.OldSession.ProjectName, transaction.NewSession.ProjectName, StringComparison.Ordinal))
            {
                errors.Add("OldSession and NewSession ProjectName do not match.");
            }

            if (!PathsEqual(transaction.OldSession.ReferenceProvenancePath, transaction.NewSession.ReferenceProvenancePath))
            {
                errors.Add("OldSession and NewSession ReferenceProvenancePath do not match.");
            }

            var referenceFolder =
                NormalizePath(
                    Path.Combine(
                        transaction.OldSession.AssetFolder,
                        AppConstants.ReferenceFolderName));

            if (Directory.Exists(referenceFolder)
                && IsReparsePoint(referenceFolder))
            {
                errors.Add(
                    "Replacement reference folder is a reparse point.");
            }

            var expectedBackupRef =
                NormalizePath(
                    transaction.OldSession.ReferenceDestinationPath
                    + "."
                    + transaction.TransactionId
                    + ".old");

            var expectedBackupProv =
                NormalizePath(
                    transaction.OldSession.ReferenceProvenancePath
                    + "."
                    + transaction.TransactionId
                    + ".old");

            var newExtension =
                Path.GetExtension(transaction.NewSession.ReferenceFilename);

            var expectedTempReference =
                NormalizePath(
                    Path.Combine(
                        referenceFolder,
                        $".__new_reference_{transaction.TransactionId}{newExtension}"));

            var expectedTempProvenance =
                NormalizePath(
                    Path.Combine(
                        referenceFolder,
                        $".__new_provenance_{transaction.TransactionId}.tmp"));

            if (string.IsNullOrWhiteSpace(transaction.BackupReferencePath))
            {
                errors.Add("Transaction BackupReferencePath is missing.");
            }
            else
            {
                if (!PathsEqual(transaction.BackupReferencePath, expectedBackupRef))
                {
                    errors.Add("Transaction BackupReferencePath does not match expected deterministic backup path.");
                }

                RequireExactParent(
                    transaction.BackupReferencePath,
                    referenceFolder,
                    "Replacement backup Reference",
                    errors);
            }

            if (string.IsNullOrWhiteSpace(transaction.BackupProvenancePath))
            {
                errors.Add("Transaction BackupProvenancePath is missing.");
            }
            else
            {
                if (!PathsEqual(transaction.BackupProvenancePath, expectedBackupProv))
                {
                    errors.Add("Transaction BackupProvenancePath does not match expected deterministic backup path.");
                }

                RequireExactParent(
                    transaction.BackupProvenancePath,
                    referenceFolder,
                    "Replacement backup provenance",
                    errors);
            }

            if (string.IsNullOrWhiteSpace(transaction.TempNewReferencePath))
            {
                errors.Add("Transaction TempNewReferencePath is missing.");
            }
            else
            {
                if (!PathsEqual(transaction.TempNewReferencePath, expectedTempReference))
                {
                    errors.Add("Transaction TempNewReferencePath does not match deterministic transaction temp path.");
                }

                RequireExactParent(
                    transaction.TempNewReferencePath,
                    referenceFolder,
                    "Replacement temporary Reference",
                    errors);
            }

            if (string.IsNullOrWhiteSpace(transaction.TempNewProvenancePath))
            {
                errors.Add("Transaction TempNewProvenancePath is missing.");
            }
            else
            {
                if (!PathsEqual(transaction.TempNewProvenancePath, expectedTempProvenance))
                {
                    errors.Add("Transaction TempNewProvenancePath does not match deterministic transaction temp path.");
                }

                RequireExactParent(
                    transaction.TempNewProvenancePath,
                    referenceFolder,
                    "Replacement temporary provenance",
                    errors);
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    /// <summary>
    /// R3-007: Preflights Main destination availability without mutations.
    /// Used before durable active Main journal persistence to avoid false rollback failures.
    /// </summary>
    public ValidationResult ValidateMainDestinationAvailability(
        AssetSession session,
        IReadOnlyCollection<string> acceptedExtensions,
        string sourceImagePath)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(acceptedExtensions);
        ArgumentNullException.ThrowIfNull(sourceImagePath);

        var errors = new List<string>();

        var mainFilename = Path.GetFileName(sourceImagePath);
        var rootMain = Path.Combine(session.AssetFolder, mainFilename);
        var finalProvenance = Path.Combine(session.AssetFolder, AppConstants.FinalProvenanceFileName);
        var ingameFolder = session.GetIngameFolderPath();

        if (File.Exists(rootMain))
        {
            errors.Add($"Main image destination already exists: {rootMain}");
        }

        if (File.Exists(finalProvenance))
        {
            errors.Add($"Final provenance already exists: {finalProvenance}");
        }

        if (Directory.Exists(ingameFolder) && IsReparsePoint(ingameFolder))
        {
            errors.Add("Ingame folder is a reparse point.");
        }

        if (Directory.Exists(ingameFolder))
        {
            try
            {
                var files = EnumerateFilesInFolderHook != null
                    ? EnumerateFilesInFolderHook(ingameFolder)
                    : Directory.EnumerateFiles(
                        ingameFolder,
                        "*",
                        SearchOption.TopDirectoryOnly);

                foreach (var path in files)
                {
                    var ext = Path.GetExtension(path);
                    var stem = Path.GetFileNameWithoutExtension(path);

                    if (acceptedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                        && string.Equals(stem, session.AssetFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"An ingame asset variant already exists: {path}");
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                errors.Add($"Could not inspect ingame folder: {ex.Message}");
            }
            catch (IOException ex)
            {
                errors.Add($"Could not inspect ingame folder: {ex.Message}");
            }
            catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
            {
                errors.Add($"Could not inspect ingame folder: {ex.Message}");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    private static bool IsSha256Hex(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length == 64
            && value.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// R2-001/R3-010: Validates a replacement journal's structural integrity before any recovery mutation.
    /// Proves all 14 path/phase/reparse constraints before trusting journal paths.
    /// Safely wraps all path operations against untrusted user data.
    /// </summary>
    public ValidationResult ValidateReferenceReplacementJournal(
        ReferenceReplacementJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        try
        {
            return ValidateReferenceReplacementJournalCore(journal);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                "Replacement journal contains invalid or unusable path metadata: " + ex.Message);
        }
    }

    private ValidationResult ValidateReferenceReplacementJournalCore(
        ReferenceReplacementJournal journal)
    {
        var errors = new List<string>();

        if (!Enum.IsDefined(typeof(ReferenceReplacementPhase), journal.Phase))
        {
            errors.Add($"Unknown replacement phase '{journal.Phase}'.");
        }

        if (string.IsNullOrWhiteSpace(journal.TransactionId)
            || journal.TransactionId.Length != 32
            || journal.TransactionId.Any(c => !Uri.IsHexDigit(c)))
        {
            errors.Add(
                "Replacement TransactionId must be exactly 32 hexadecimal characters.");
        }

        if (journal.OldSession is null || journal.NewSession is null)
        {
            errors.Add("Replacement journal OldSession/NewSession is missing.");
            return ValidationResult.Failure(errors);
        }

        var oldSession = journal.OldSession;
        var newSession = journal.NewSession;

        var oldPathValidation =
            ValidateSessionPathsForDestructiveOperation(oldSession);

        if (!oldPathValidation.IsValid)
        {
            errors.AddRange(
                oldPathValidation.Errors.Select(
                    e => "OldSession: " + e));
        }

        var newPathValidation =
            ValidateSessionPathsForDestructiveOperation(newSession);

        if (!newPathValidation.IsValid)
        {
            errors.AddRange(
                newPathValidation.Errors.Select(
                    e => "NewSession: " + e));
        }

        if (oldSession.WorkflowMode != AssetWorkflowMode.ReferenceAssisted
            || newSession.WorkflowMode != AssetWorkflowMode.ReferenceAssisted)
        {
            errors.Add(
                "Reference replacement journal must contain ReferenceAssisted sessions.");
        }

        if (!PathsEqual(
                oldSession.AssetRootFolder,
                newSession.AssetRootFolder))
        {
            errors.Add("Old/New AssetRootFolder mismatch.");
        }

        if (!string.Equals(
                oldSession.AssetFolderName,
                newSession.AssetFolderName,
                StringComparison.Ordinal))
        {
            errors.Add("Old/New AssetFolderName mismatch.");
        }

        if (!PathsEqual(
                oldSession.AssetFolder,
                newSession.AssetFolder))
        {
            errors.Add("Old/New AssetFolder mismatch.");
        }

        if (!string.Equals(
                oldSession.ProjectName,
                newSession.ProjectName,
                StringComparison.Ordinal))
        {
            errors.Add("Old/New ProjectName mismatch.");
        }

        if (errors.Count != 0)
        {
            return ValidationResult.Failure(errors);
        }

        var referenceFolder =
            NormalizePath(
                Path.Combine(
                    oldSession.AssetFolder,
                    AppConstants.ReferenceFolderName));

        if (Directory.Exists(referenceFolder)
            && IsReparsePoint(referenceFolder))
        {
            errors.Add(
                "Replacement reference folder is a reparse point.");
        }

        var expectedBackupReference =
            NormalizePath(
                oldSession.ReferenceDestinationPath
                + "."
                + journal.TransactionId
                + ".old");

        var expectedBackupProvenance =
            NormalizePath(
                oldSession.ReferenceProvenancePath
                + "."
                + journal.TransactionId
                + ".old");

        var newExtension =
            Path.GetExtension(newSession.ReferenceFilename);

        var expectedTempReference =
            NormalizePath(
                Path.Combine(
                    referenceFolder,
                    $".__new_reference_{journal.TransactionId}{newExtension}"));

        var expectedTempProvenance =
            NormalizePath(
                Path.Combine(
                    referenceFolder,
                    $".__new_provenance_{journal.TransactionId}.tmp"));

        if (!PathsEqual(
                journal.BackupReferencePath,
                expectedBackupReference))
        {
            errors.Add(
                "BackupReferencePath does not match deterministic transaction path.");
        }

        if (!PathsEqual(
                journal.BackupProvenancePath,
                expectedBackupProvenance))
        {
            errors.Add(
                "BackupProvenancePath does not match deterministic transaction path.");
        }

        if (!PathsEqual(
                journal.TempNewReferencePath,
                expectedTempReference))
        {
            errors.Add(
                "TempNewReferencePath does not match deterministic transaction path.");
        }

        if (!PathsEqual(
                journal.TempNewProvenancePath,
                expectedTempProvenance))
        {
            errors.Add(
                "TempNewProvenancePath does not match deterministic transaction path.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    /// <summary>
    /// R2-003/R3-010: Validates a session in ReferenceCommitPhase.Prepared without requiring files to exist on disk.
    /// Used during startup recovery BEFORE running the full ValidateSession which would reject in-progress sessions.
    /// Safely wraps all path operations against untrusted user data and requires complete authority hashes.
    /// </summary>
    public ValidationResult ValidatePreparedReferenceSession(
        AssetSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            return ValidatePreparedReferenceSessionCore(session);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                "Prepared reference session contains invalid or unusable path metadata: " + ex.Message);
        }
    }

    private ValidationResult ValidatePreparedReferenceSessionCore(
        AssetSession session)
    {
        var errors = new List<string>();

        if (session.WorkflowMode != AssetWorkflowMode.ReferenceAssisted)
        {
            errors.Add("Prepared reference session must have ReferenceAssisted workflow mode.");
        }

        if (session.ReferenceCommitPhase != ReferenceCommitPhase.Prepared)
        {
            errors.Add("Session is not in ReferenceCommitPhase.Prepared.");
        }

        if (string.IsNullOrWhiteSpace(session.ReferenceTransactionId)
            || session.ReferenceTransactionId.Length != 32
            || session.ReferenceTransactionId.Any(c => !Uri.IsHexDigit(c)))
        {
            errors.Add("ReferenceTransactionId must be exactly 32 hexadecimal characters.");
        }

        if (string.IsNullOrWhiteSpace(session.ProjectName))
        {
            errors.Add("Prepared session ProjectName is missing.");
        }

        if (session.ReferenceProcessedAt == default)
        {
            errors.Add("Prepared session ReferenceProcessedAt is missing.");
        }

        if (session.CancelPhase != CancelPhase.None)
        {
            errors.Add("Prepared session CancelPhase must be None.");
        }

        if (session.IsMainCommitting)
        {
            errors.Add("Prepared session must not have active Main commit state.");
        }

        // Validate path security (does not require directories to exist)
        var pathValidation = ValidateSessionPathsForDestructiveOperation(session);
        if (!pathValidation.IsValid)
        {
            errors.AddRange(pathValidation.Errors);
        }

        if (!IsSha256Hex(session.ReferenceHash))
        {
            errors.Add("Prepared ReferenceHash is missing or invalid.");
        }

        if (!IsSha256Hex(session.ReferenceProvenanceHash))
        {
            errors.Add("Prepared ReferenceProvenanceHash is missing or invalid.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }
}

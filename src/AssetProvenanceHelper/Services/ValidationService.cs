using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class ValidationService
{
    // BUG-005: Extended with Unicode-superscript COM/LPT variants (COM¹ COM² COM³
    // LPT¹ LPT² LPT³) documented by Microsoft as reserved on Windows.
    private static readonly HashSet<string> ReservedDeviceNames =
        new(
            new[]
            {
                "CON",
                "PRN",
                "AUX",
                "NUL",

                "COM1",
                "COM2",
                "COM3",
                "COM4",
                "COM5",
                "COM6",
                "COM7",
                "COM8",
                "COM9",
                "COM\u00B9", // COM¹
                "COM\u00B2", // COM²
                "COM\u00B3", // COM³

                "LPT1",
                "LPT2",
                "LPT3",
                "LPT4",
                "LPT5",
                "LPT6",
                "LPT7",
                "LPT8",
                "LPT9",
                "LPT\u00B9", // LPT¹
                "LPT\u00B2", // LPT²
                "LPT\u00B3", // LPT³

                "CONIN$",
                "CONOUT$"
            },
            StringComparer.OrdinalIgnoreCase);

    public ValidationResult ValidateSettings(
        AppSettings settings)
    {
        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(
                settings.ProjectName))
        {
            errors.Add(
                "Project Name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(
                settings.DownloadFolder))
        {
            errors.Add(
                "Download Folder must not be empty.");
        }
        else if (!Directory.Exists(
                     settings.DownloadFolder))
        {
            errors.Add(
                $"Download Folder does not exist: {settings.DownloadFolder}");
        }

        if (string.IsNullOrWhiteSpace(
                settings.AssetRootFolder))
        {
            errors.Add(
                "Asset Root Folder must not be empty.");
        }
        else if (!Directory.Exists(
                     settings.AssetRootFolder))
        {
            errors.Add(
                $"Asset Root Folder does not exist: {settings.AssetRootFolder}");
        }
        else if (IsReparsePoint(settings.AssetRootFolder)) // BUG-007
        {
            errors.Add(
                "Asset Root Folder is a reparse point (junction or symbolic link) and cannot be used safely.");
        }

        if (settings.AcceptedExtensions is null ||
            settings.AcceptedExtensions.Count == 0)
        {
            errors.Add(
                "Accepted image extensions must not be empty.");
        }
        else
        {
            foreach (var ext in settings.AcceptedExtensions)
            {
                if (string.IsNullOrWhiteSpace(ext) ||
                    !ext.StartsWith('.') ||
                    ext.Length == 1 ||
                    ext.Contains('/') ||
                    ext.Contains('\\') ||
                    ext.Contains(".."))
                {
                    errors.Add(
                        $"Accepted extension '{ext}' is invalid. Extensions must start with '.' and contain no path separators.");
                }
            }
        }

        if (Directory.Exists(settings.DownloadFolder) &&
            Directory.Exists(settings.AssetRootFolder))
        {
            if (PathsEqual(settings.DownloadFolder, settings.AssetRootFolder))
            {
                errors.Add(
                    "Download Folder and Asset Root Folder cannot be the same directory.");
            }
            else
            {
                var normDownload =
                    NormalizePath(settings.DownloadFolder)
                    + Path.DirectorySeparatorChar;

                var normAsset =
                    NormalizePath(settings.AssetRootFolder)
                    + Path.DirectorySeparatorChar;

                if (normAsset.StartsWith(
                        normDownload,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "Asset Root Folder cannot be inside the Download Folder.");
                }
                else if (normDownload.StartsWith(
                             normAsset,
                             StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "Download Folder cannot be inside the Asset Root Folder.");
                }
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateAssetFolderName(
        string name)
    {
        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(
                "Asset Folder Name must not be empty.");

            return ValidationResult.Failure(
                errors);
        }

        if (name is "." or "..")
        {
            errors.Add(
                "Asset Folder Name must not be '.' or '..'.");
        }

        if (name.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0 ||
            name.Any(char.IsControl))
        {
            errors.Add(
                "Asset Folder Name contains invalid Windows filename characters.");
        }

        if (name.EndsWith(
                " ",
                StringComparison.Ordinal) ||
            name.EndsWith(
                ".",
                StringComparison.Ordinal))
        {
            errors.Add(
                "Asset Folder Name must not end with a space or dot.");
        }

        // BUG-005: Extract the stem before the *first* dot, not the last, so
        // that names like NUL.tar.gz, PRN.foo.bar, COM².webp are all caught.
        // GetFileNameWithoutExtension stops at the last dot only.
        var firstDotIndex =
            name.IndexOf('.');

        var stem =
            (firstDotIndex >= 0 ? name[..firstDotIndex] : name)
                .TrimEnd(' ', '.');

        if (ReservedDeviceNames.Contains(stem))
        {
            errors.Add(
                $"Asset Folder Name uses reserved Windows device name '{stem}'.");
        }

        if (!string.Equals(
                Path.GetFileName(name),
                name,
                StringComparison.Ordinal))
        {
            errors.Add(
                "Asset Folder Name must be a single folder name and must not contain a path.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateImageFile(
        string path,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(
                "Image path must not be empty.");

            return ValidationResult.Failure(
                errors);
        }

        if (!File.Exists(path))
        {
            errors.Add(
                $"Image file does not exist: {path}");

            return ValidationResult.Failure(
                errors);
        }

        var extension =
            Path.GetExtension(path);

        if (!acceptedExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Unsupported image extension '{extension}'.");
        }

        try
        {
            var info =
                new FileInfo(path);

            if (info.Length <= 0)
            {
                errors.Add(
                    $"Image file is empty: {path}");
            }

            using var stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            if (stream.Length <= 0)
            {
                errors.Add(
                    $"Image file is not readable or contains no data: {path}");
            }
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Image file cannot be opened for reading: {path}. {ex.Message}");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public ValidationResult ValidateSession(
        AssetSession session)
    {
        var errors =
            new List<string>();

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

        if (session.ReferenceProcessedAt ==
            default)
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

            if (session.MainPrompt is null)
            {
                errors.Add(
                    "Session IsMainCommitting is true but MainPrompt is missing.");
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

        if (errors.Count == 0)
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

        if (errors.Count == 0 &&
            !Directory.Exists(
                session.AssetFolder))
        {
            errors.Add(
                $"Session AssetFolder does not exist: {session.AssetFolder}");
        }

        // BUG-007: Reject reparse points (junctions / symlinks) in the asset
        // folder chain. Path-string comparisons cannot be trusted when the
        // directory is a reparse point because the real bytes land elsewhere.
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

                // BUG-007: Also check the reference subfolder for reparse
                // points once we know it exists.
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

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
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

        RequireContains(
            finalText,
            session.ReferenceFilename,
            "Final provenance does not contain ReferenceFilename.",
            errors);

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
        string? expectedMainHash = null)
    {
        var errors =
            new List<string>();

        var referenceValidation =
            ValidateReferenceOutput(
                session);

        if (!referenceValidation.IsValid)
        {
            errors.AddRange(
                referenceValidation.Errors);
        }

        if (!File.Exists(mainImagePath))
        {
            errors.Add(
                $"Main image does not exist: {mainImagePath}");
        }
        else if (!string.IsNullOrWhiteSpace(expectedMainHash))
        {
            try
            {
                var actualHash = ComputeSha256(mainImagePath);
                if (!string.Equals(actualHash, expectedMainHash, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("Main image SHA-256 hash does not match expected MainHash.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Could not compute Main image SHA-256 hash: {ex.Message}");
            }
        }

        var finalProvValidation =
            ValidateFinalProvenanceContent(
                session,
                finalProvenancePath,
                mainFilename,
                mainGenerationDate,
                prompt);

        if (!finalProvValidation.IsValid)
        {
            errors.AddRange(
                finalProvValidation.Errors);
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

        if (string.IsNullOrWhiteSpace(session.MainFilename) ||
            !session.MainProcessedAt.HasValue)
        {
            return ValidationResult.Failure(
                "Session Main metadata is incomplete for final provenance ownership validation.");
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

        string expectedText;
        try
        {
            var generationDate = session.MainProcessedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            expectedText = templateService.RenderFinal(
                session.MainFilename,
                session.ReferenceFilename,
                session.ProjectName,
                generationDate,
                session.MainPrompt ?? string.Empty);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure(
                $"Could not render expected final provenance for ownership validation: {ex.Message}");
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

    public static bool PathsEqual(
        string left,
        string right)
    {
        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// BUG-006: Replaced the manual <c>TrimEnd(separators)</c> with the
    /// framework API <see cref="Path.TrimEndingDirectorySeparator"/> which is
    /// root-aware: it correctly leaves <c>C:\</c> as <c>C:\</c> instead of
    /// stripping the backslash and producing the relative-drive form <c>C:</c>.
    /// </summary>
    public static string NormalizePath(
        string path)
    {
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
    }

    /// <summary>
    /// Validates a reference replacement transaction ensuring both sessions and backup paths are strictly valid and safe.
    /// </summary>
    public ValidationResult ValidateReferenceReplacementTransaction(
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

            var expectedBackupRef = transaction.OldSession.ReferenceDestinationPath + "." + transaction.TransactionId + ".old";
            var expectedBackupProv = transaction.OldSession.ReferenceProvenancePath + "." + transaction.TransactionId + ".old";

            if (!PathsEqual(transaction.BackupReferencePath, expectedBackupRef))
            {
                errors.Add("Transaction BackupReferencePath does not match expected deterministic backup path.");
            }

            if (!PathsEqual(transaction.BackupProvenancePath, expectedBackupProv))
            {
                errors.Add("Transaction BackupProvenancePath does not match expected deterministic backup path.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    /// <summary>
    /// Validates that the session paths are strictly confined to the trusted AssetRootFolder
    /// and that neither the root nor the asset folder nor the reference folder is a reparse point.
    /// </summary>
    public static ValidationResult ValidateSessionPathsForDestructiveOperation(
        AssetSession session)
    {
        if (string.IsNullOrWhiteSpace(session.AssetRootFolder) ||
            string.IsNullOrWhiteSpace(session.AssetFolderName) ||
            string.IsNullOrWhiteSpace(session.AssetFolder) ||
            string.IsNullOrWhiteSpace(session.ReferenceFilename))
        {
            return ValidationResult.Failure(
                "Session contains insufficient path information for safe operation.");
        }

        if (!string.Equals(
                Path.GetFileName(session.ReferenceFilename),
                session.ReferenceFilename,
                StringComparison.Ordinal))
        {
            return ValidationResult.Failure(
                "ReferenceFilename contains an unsafe path.");
        }

        var normalizedRoot =
            NormalizePath(session.AssetRootFolder);

        var expectedAssetFolder =
            NormalizePath(
                Path.Combine(
                    session.AssetRootFolder,
                    session.AssetFolderName));

        var actualAssetFolder =
            NormalizePath(session.AssetFolder);

        if (!string.Equals(
                expectedAssetFolder,
                actualAssetFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Failure(
                "Session AssetFolder does not match AssetRootFolder + AssetFolderName.");
        }

        var actualAssetParent =
            Path.GetDirectoryName(actualAssetFolder);

        if (actualAssetParent is null ||
            !PathsEqual(actualAssetParent, normalizedRoot))
        {
            return ValidationResult.Failure(
                "Session AssetFolder is not a direct child of AssetRootFolder.");
        }

        if (IsReparsePoint(session.AssetFolder) ||
            IsReparsePoint(session.AssetRootFolder))
        {
            return ValidationResult.Failure(
                "Session path is a reparse point (junction or symbolic link) and cannot be operated on safely.");
        }

        var referenceFolder =
            NormalizePath(
                Path.Combine(
                    session.AssetFolder,
                    AppConstants.ReferenceFolderName));

        if (Directory.Exists(referenceFolder) && IsReparsePoint(referenceFolder))
        {
            return ValidationResult.Failure(
                "Reference folder is a reparse point and cannot be operated on safely.");
        }

        // BUG-R4-005: Strictly verify ReferenceDestinationPath and ReferenceProvenancePath
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

        if (!PathsEqual(expectedReferencePath, session.ReferenceDestinationPath))
        {
            return ValidationResult.Failure(
                "Session ReferenceDestinationPath is inconsistent with expected asset reference location.");
        }

        if (!PathsEqual(expectedProvenancePath, session.ReferenceProvenancePath))
        {
            return ValidationResult.Failure(
                "Session ReferenceProvenancePath is inconsistent with expected asset provenance location.");
        }

        return ValidationResult.Success();
    }

    [ThreadStatic]
    internal static Func<string, FileAttributes>? FileAttributesProvider;

    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var attributes = FileAttributesProvider is not null
                ? FileAttributesProvider(path)
                : File.GetAttributes(path);

            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            // Fail closed for safety: if attributes cannot be checked on an existing path (e.g. UnauthorizedAccessException), treat as unsafe
            return true;
        }
    }

    public static string ComputeSha256(
        string filePath)
    {
        using var stream =
            new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

        var hash =
            SHA256.HashData(
                stream);

        return Convert
            .ToHexString(hash)
            .ToLowerInvariant();
    }

    private static void RequireContains(
        string source,
        string expected,
        string error,
        ICollection<string> errors)
    {
        if (!source.Contains(
                expected,
                StringComparison.Ordinal))
        {
            errors.Add(
                error);
        }
    }
}

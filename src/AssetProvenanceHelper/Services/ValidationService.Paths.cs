using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed partial class ValidationService
{
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
    /// Validates that the session paths are strictly confined to the trusted AssetRootFolder
    /// and that neither the root nor the asset folder nor the reference folder is a reparse point.
    /// </summary>
    public static ValidationResult ValidateSessionPathsForDestructiveOperation(
        AssetSession session)
    {
        if (string.IsNullOrWhiteSpace(session.AssetRootFolder) ||
            string.IsNullOrWhiteSpace(session.AssetFolderName) ||
            string.IsNullOrWhiteSpace(session.AssetFolder))
        {
            return ValidationResult.Failure(
                "Session contains insufficient path information for safe operation.");
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

        if (IsReparsePoint(session.AssetRootFolder))
        {
            return ValidationResult.Failure(
                "Session path is a reparse point (junction or symbolic link) and cannot be operated on safely.");
        }

        if (Directory.Exists(session.AssetFolder) && IsReparsePoint(session.AssetFolder))
        {
            return ValidationResult.Failure(
                "Session path is a reparse point (junction or symbolic link) and cannot be operated on safely.");
        }

        if (session.WorkflowMode == AssetWorkflowMode.NoReference)
        {
            return ValidationResult.Success();
        }

        if (string.IsNullOrWhiteSpace(session.ReferenceFilename))
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
}

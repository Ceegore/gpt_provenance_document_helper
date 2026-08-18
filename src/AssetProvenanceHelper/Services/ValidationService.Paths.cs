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

    private static bool IsSafeFilename(string? filename) =>
        !string.IsNullOrWhiteSpace(filename)
        && string.Equals(
            Path.GetFileName(filename),
            filename,
            StringComparison.Ordinal);

    private static void RequireExactParent(
        string path,
        string expectedParent,
        string description,
        ICollection<string> errors)
    {
        try
        {
            var normalized = NormalizePath(path);
            var parent = Path.GetDirectoryName(normalized);

            if (parent is null || !PathsEqual(parent, expectedParent))
            {
                errors.Add(
                    $"{description} is not inside the expected directory '{expectedParent}'.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{description} path is invalid: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that the session paths are strictly confined to the trusted AssetRootFolder
    /// and that neither the root nor the asset folder nor the reference folder nor the ingame folder is a reparse point.
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

        var errors = new List<string>();

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
            errors.Add("Session AssetFolder does not match AssetRootFolder + AssetFolderName.");
        }

        var actualAssetParent =
            Path.GetDirectoryName(actualAssetFolder);

        if (actualAssetParent is null ||
            !PathsEqual(actualAssetParent, normalizedRoot))
        {
            errors.Add("Session AssetFolder is not a direct child of AssetRootFolder.");
        }

        if (IsReparsePoint(session.AssetRootFolder))
        {
            errors.Add("Session path is a reparse point (junction or symbolic link) and cannot be operated on safely.");
        }

        if (Directory.Exists(session.AssetFolder) && IsReparsePoint(session.AssetFolder))
        {
            errors.Add("Session path is a reparse point (junction or symbolic link) and cannot be operated on safely.");
        }

        var ingameFolder =
            NormalizePath(
                Path.Combine(
                    actualAssetFolder,
                    AppConstants.IngameFolderName));

        if (Directory.Exists(ingameFolder) && IsReparsePoint(ingameFolder))
        {
            errors.Add("Session ingame folder is a reparse point and cannot be operated on safely.");
        }

        if (session.IsMainCommitting)
        {
            if (!IsSafeFilename(session.MainFilename))
            {
                errors.Add("Session MainFilename is unsafe.");
            }
            else
            {
                var rootMain =
                    NormalizePath(
                        Path.Combine(
                            actualAssetFolder,
                            session.MainFilename!));

                RequireExactParent(
                    rootMain,
                    actualAssetFolder,
                    "Root Main image",
                    errors);

                var finalProvenance =
                    NormalizePath(
                        Path.Combine(
                            actualAssetFolder,
                            AppConstants.FinalProvenanceFileName));

                RequireExactParent(
                    finalProvenance,
                    actualAssetFolder,
                    "Final provenance",
                    errors);

                var ingameFilename =
                    AssetNaming.BuildIngameFilename(
                        session.AssetFolderName,
                        session.MainFilename!);

                var ingamePath =
                    NormalizePath(
                        Path.Combine(
                            ingameFolder,
                            ingameFilename));

                RequireExactParent(
                    ingamePath,
                    ingameFolder,
                    "Ingame image",
                    errors);

                var tempMain = session.GetMainTempImagePath();
                var tempProv = session.GetMainTempProvenancePath();
                var tempIngame = session.GetMainTempIngamePath();

                if (!string.IsNullOrWhiteSpace(tempMain))
                {
                    RequireExactParent(
                        tempMain,
                        actualAssetFolder,
                        "Temporary Main image",
                        errors);
                }

                if (!string.IsNullOrWhiteSpace(tempProv))
                {
                    RequireExactParent(
                        tempProv,
                        actualAssetFolder,
                        "Temporary Main provenance",
                        errors);
                }

                if (!string.IsNullOrWhiteSpace(tempIngame))
                {
                    RequireExactParent(
                        tempIngame,
                        ingameFolder,
                        "Temporary ingame image",
                        errors);
                }
            }
        }

        if (session.WorkflowMode == AssetWorkflowMode.ReferenceAssisted)
        {
            if (string.IsNullOrWhiteSpace(session.ReferenceFilename))
            {
                errors.Add("Session contains insufficient path information for safe operation.");
            }
            else
            {
                if (!IsSafeFilename(session.ReferenceFilename))
                {
                    errors.Add("ReferenceFilename contains an unsafe path.");
                }

                var referenceFolder =
                    NormalizePath(
                        Path.Combine(
                            actualAssetFolder,
                            AppConstants.ReferenceFolderName));

                if (Directory.Exists(referenceFolder) && IsReparsePoint(referenceFolder))
                {
                    errors.Add("Reference folder is a reparse point and cannot be operated on safely.");
                }

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

                RequireExactParent(
                    expectedReferencePath,
                    referenceFolder,
                    "Reference image",
                    errors);

                RequireExactParent(
                    expectedProvenancePath,
                    referenceFolder,
                    "Reference provenance",
                    errors);

                if (!string.IsNullOrWhiteSpace(session.ReferenceDestinationPath) &&
                    !PathsEqual(expectedReferencePath, session.ReferenceDestinationPath))
                {
                    errors.Add("Session ReferenceDestinationPath is inconsistent with expected asset reference location.");
                }

                if (!string.IsNullOrWhiteSpace(session.ReferenceProvenancePath) &&
                    !PathsEqual(expectedProvenancePath, session.ReferenceProvenancePath))
                {
                    errors.Add("Session ReferenceProvenancePath is inconsistent with expected asset provenance location.");
                }
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }
}

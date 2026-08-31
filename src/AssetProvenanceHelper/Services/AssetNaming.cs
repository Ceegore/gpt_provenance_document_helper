namespace AssetProvenanceHelper.Services;

public static class AssetNaming
{
    public static string DeriveProjectLabel(
        string assetRootFolder)
    {
        if (string.IsNullOrWhiteSpace(assetRootFolder))
        {
            return string.Empty;
        }

        var normalized =
            ValidationService.NormalizePath(assetRootFolder);

        var directory =
            new DirectoryInfo(normalized);

        if (!string.IsNullOrWhiteSpace(directory.Name))
        {
            return directory.Name;
        }

        return normalized;
    }

    public static string BuildIngameFilename(
        string assetName,
        string mainFilename)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            throw new ArgumentException(
                "Asset name must not be empty.",
                nameof(assetName));
        }

        if (string.IsNullOrWhiteSpace(mainFilename))
        {
            throw new ArgumentException(
                "Main filename must not be empty.",
                nameof(mainFilename));
        }

        return assetName
            + Path.GetExtension(mainFilename);
    }

    /// <summary>
    /// Suffix for variant N (1-based): 1 -> "A" ... 10 -> "J".
    /// Capped at 10 so one character always suffices, and so no suffix can turn a
    /// valid asset name into a reserved Windows device name: the reserved names in
    /// ValidationService end in N, N, X, L, a digit, a superscript or '$' - none of
    /// which are in A..J.
    /// </summary>
    public static string GetVariantSuffix(int variantNumber)
    {
        if (variantNumber < 1 || variantNumber > AppConstants.MaxVariantCount)
        {
            throw new ArgumentOutOfRangeException(nameof(variantNumber));
        }

        return ((char)('A' + variantNumber - 1)).ToString();
    }

    public static string BuildVariantAssetName(string baseName, int variantNumber)
    {
        if (baseName is null)
        {
            throw new ArgumentNullException(nameof(baseName));
        }

        return baseName.Trim() + GetVariantSuffix(variantNumber);
    }
}

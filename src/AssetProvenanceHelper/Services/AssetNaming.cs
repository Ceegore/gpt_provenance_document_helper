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
}

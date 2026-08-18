using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class ImageFinderService
{
    public string? FindLatestImage(
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(
            settings);

        if (string.IsNullOrWhiteSpace(
                settings.DownloadFolder))
        {
            return null;
        }

        if (!Directory.Exists(
                settings.DownloadFolder))
        {
            return null;
        }

        var allowed =
            new HashSet<string>(
                settings.AcceptedExtensions,
                StringComparer.OrdinalIgnoreCase);

        var allCandidates =
            Directory
                .EnumerateFiles(
                    settings.DownloadFolder,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(path =>
                    new FileInfo(path))
                .Where(file =>
                    allowed.Contains(
                        file.Extension))
                .ToList();

        if (allCandidates.Count == 0)
        {
            return null;
        }

        return allCandidates
            .OrderByDescending(
                file => file.LastWriteTimeUtc)
            .ThenByDescending(
                file => file.CreationTimeUtc)
            .ThenBy(
                file => file.Name,
                StringComparer.OrdinalIgnoreCase)
            .First()
            .FullName;
    }
}

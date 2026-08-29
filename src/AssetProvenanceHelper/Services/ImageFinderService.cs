using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class ImageFinderService
{
    public IReadOnlyList<string> FindLatestImages(
        AppSettings settings,
        int count)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count));
        }

        if (string.IsNullOrWhiteSpace(
                settings.DownloadFolder)
            || !Directory.Exists(
                settings.DownloadFolder))
        {
            return Array.Empty<string>();
        }

        var allowed =
            new HashSet<string>(
                settings.AcceptedExtensions,
                StringComparer.OrdinalIgnoreCase);

        return Directory
            .EnumerateFiles(
                settings.DownloadFolder,
                "*",
                SearchOption.TopDirectoryOnly)
            .Select(
                path => new FileInfo(path))
            .Where(
                file =>
                    allowed.Contains(
                        file.Extension))
            .OrderByDescending(
                file =>
                    file.LastWriteTimeUtc)
            .ThenByDescending(
                file =>
                    file.CreationTimeUtc)
            .ThenBy(
                file =>
                    file.Name,
                StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(
                file =>
                    file.FullName)
            .ToArray();
    }

    public string? FindLatestImage(
        AppSettings settings)
    {
        return FindLatestImages(
                settings,
                1)
            .FirstOrDefault();
    }
}

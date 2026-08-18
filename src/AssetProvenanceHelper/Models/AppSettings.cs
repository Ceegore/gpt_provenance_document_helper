using AssetProvenanceHelper;

namespace AssetProvenanceHelper.Models;

public sealed class AppSettings
{
    public string ProjectName { get; set; } = string.Empty;

    public string DownloadFolder { get; set; } = string.Empty;

    public string AssetRootFolder { get; set; } = string.Empty;

    public List<string> AcceptedExtensions { get; set; } =
        AppConstants.DefaultImageExtensions.ToList();
}

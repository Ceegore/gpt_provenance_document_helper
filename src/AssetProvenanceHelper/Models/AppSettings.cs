using AssetProvenanceHelper;

namespace AssetProvenanceHelper.Models;

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = string.Empty;

    public string AssetRootFolder { get; set; } = string.Empty;

    public List<string> AcceptedExtensions { get; set; } =
        AppConstants.DefaultImageExtensions.ToList();

    public string SelectedProviderTemplateFileName { get; set; }
        = AppConstants.DefaultProviderTemplateFileName;

    public bool DirectModeEnabled { get; set; }
        = false;

    public bool KeepSettingsEnabled { get; set; }
        = false;

    public bool ApiGenerationEnabled { get; set; }
        = false;

    public string OpenAiModel { get; set; }
        = "gpt-image-2";

    public string DirectImageQuality { get; set; }
        = "medium";

    public string BatchImageQuality { get; set; }
        = "medium";

    public int DirectStartsPerMinute { get; set; }
        = 5;

    public int DirectMaxConcurrency { get; set; }
        = 5;

    public int BatchPollSeconds { get; set; }
        = 30;

    public int MaxBatchRequestsPerSubmission { get; set; }
        = 500;

    public int DirectRetryAttempts { get; set; }
        = 3;
}

using System.Text;
using System.Text.Json;
using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = true
        };

    public SettingsService(
        string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    /// <summary>
    /// Test seam: fires after the new content has been fully written and
    /// durably flushed to the temp file, but before that temp file is
    /// promoted onto the real settings path. Lets a test prove a failure was
    /// injected at the exact promotion boundary - distinguishing this
    /// implementation from a naive direct write, which would never reach
    /// this point at all.
    /// </summary>
    internal static Action<string>? OnAfterTempFlushedBeforePromoteHook;

    public AppSettings CreateDefaults()
    {
        var userProfile =
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

        var defaultDownloads =
            Path.Combine(
                userProfile,
                "Downloads");

        return new AppSettings
        {
            DownloadFolder =
                Directory.Exists(defaultDownloads)
                    ? defaultDownloads
                    : string.Empty,

            AssetRootFolder = string.Empty,

            AcceptedExtensions =
                AppConstants.DefaultImageExtensions.ToList()
        };
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return CreateDefaults();
        }

        try
        {
            var json =
                File.ReadAllText(
                    _settingsPath,
                    Encoding.UTF8);

            var settings =
                JsonSerializer.Deserialize<AppSettings>(
                    json,
                    _jsonOptions);

            if (settings is null)
            {
                throw new InvalidDataException(
                    "settings.json could not be deserialized.");
            }

            Normalize(settings);

            return settings;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Could not parse settings file '{_settingsPath}'.",
                ex);
        }
    }

    public void Save(
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(
            settings);

        Normalize(settings);

        var directory =
            Path.GetDirectoryName(
                _settingsPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        var json =
            JsonSerializer.Serialize(
                settings,
                _jsonOptions);

        // BUG-009: Write atomically via a temp file so that a crash or I/O
        // error during the write cannot leave a partially-written settings.json.
        var tempPath =
            _settingsPath
            + $".{Guid.NewGuid():N}.tmp";

        try
        {
            using (
                var stream =
                    new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
            using (
                var writer =
                    new StreamWriter(
                        stream,
                        new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            OnAfterTempFlushedBeforePromoteHook?.Invoke(tempPath);

            File.Move(
                tempPath,
                _settingsPath,
                overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Preserve the original exception.
            }

            throw;
        }
    }

    private static void Normalize(
        AppSettings settings)
    {
        settings.DownloadFolder ??= string.Empty;
        settings.AssetRootFolder ??= string.Empty;

        settings.AcceptedExtensions =
            settings.AcceptedExtensions?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            ?? AppConstants.DefaultImageExtensions.ToList();

        // BUG-010: A JSON payload with an empty array (or one containing only
        // whitespace entries) yields an empty list after the filter above.
        // Fall back to defaults rather than persisting a list that makes every
        // subsequent ValidateSettings call fail with no way to recover from the UI.
        if (settings.AcceptedExtensions.Count == 0)
        {
            settings.AcceptedExtensions =
                AppConstants.DefaultImageExtensions.ToList();
        }

        settings.SelectedProviderTemplateFileName ??=
            AppConstants.DefaultProviderTemplateFileName;

        if (string.IsNullOrWhiteSpace(
                settings.SelectedProviderTemplateFileName)
            || !string.Equals(
                Path.GetFileName(
                    settings.SelectedProviderTemplateFileName),
                settings.SelectedProviderTemplateFileName,
                StringComparison.Ordinal)
            || !string.Equals(
                Path.GetExtension(
                    settings.SelectedProviderTemplateFileName),
                ".md",
                StringComparison.OrdinalIgnoreCase))
        {
            settings.SelectedProviderTemplateFileName =
                AppConstants.DefaultProviderTemplateFileName;
        }

        settings.OpenAiModel = string.IsNullOrWhiteSpace(settings.OpenAiModel)
            ? "gpt-image-2"
            : settings.OpenAiModel.Trim();

        settings.DirectImageQuality = NormalizeQuality(settings.DirectImageQuality);
        settings.BatchImageQuality = NormalizeQuality(settings.BatchImageQuality);

        settings.DirectStartsPerMinute = Math.Clamp(settings.DirectStartsPerMinute, 1, 60);
        settings.DirectMaxConcurrency = Math.Clamp(settings.DirectMaxConcurrency, 1, 20);
        settings.BatchPollSeconds = Math.Clamp(settings.BatchPollSeconds, 5, 300);
        settings.MaxBatchRequestsPerSubmission = Math.Clamp(settings.MaxBatchRequestsPerSubmission, 1, 5000);
        settings.DirectRetryAttempts = Math.Clamp(settings.DirectRetryAttempts, 1, 10);
    }

    private static string NormalizeQuality(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return "medium";
        }

        var lower = quality.Trim().ToLowerInvariant();
        return lower is "low" or "medium" or "high" ? lower : "medium";
    }

    private static string NormalizeExtension(
        string value)
    {
        var normalized =
            value
                .Trim()
                .ToLowerInvariant();

        if (!normalized.StartsWith('.'))
        {
            normalized =
                "." + normalized;
        }

        return normalized;
    }
}

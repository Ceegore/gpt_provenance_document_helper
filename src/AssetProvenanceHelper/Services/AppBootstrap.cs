using System.Security.Cryptography;
using System.Text;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper;

public sealed class AppBootstrapContext
{
    public required string BaseDirectory { get; init; }

    public required string MutexName { get; init; }

    public required string SettingsPath { get; init; }

    public required string SessionPath { get; init; }

    public required string ReferenceTemplatePath { get; init; }

    public required string FinalTemplatePath { get; init; }

    public required string FinalNoReferenceTemplatePath { get; init; }

    public required AppSettings Settings { get; set; }

    public required SettingsService SettingsService { get; init; }

    public required SessionService SessionService { get; init; }

    public required TemplateService TemplateService { get; init; }

    public required ValidationService ValidationService { get; init; }

    public required ImageFinderService ImageFinderService { get; init; }

    public required AssetProcessorService AssetProcessorService { get; init; }
}

public static class AppBootstrap
{
    public static string BuildSingleInstanceMutexName(
        string baseDirectory)
    {
        // State and single-instance authority must survive portable upgrades.
        // The parameter remains for source compatibility with earlier callers.
        _ = baseDirectory;
        return "Local\\AssetProvenanceHelper_"
            + Convert.ToHexString(
                SHA256.HashData(
                Encoding.UTF8.GetBytes(
                        Environment.UserName.ToUpperInvariant())));
    }

    public static string GetStateDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ceegore",
            "AssetProvenanceHelper");

    /// <summary>
    /// Copies (never moves) legacy portable state into stable per-user state on
    /// first launch. Conflicting state is rejected so recovery never chooses an
    /// arbitrary journal.
    /// </summary>
    public static void MigrateLegacyState(string legacyDirectory, string stateDirectory)
    {
        if (PathsEqual(legacyDirectory, stateDirectory))
        {
            return;
        }

        foreach (var fileName in new[]
                 {
                     AppConstants.SettingsFileName,
                     AppConstants.SessionFileName,
                     AppConstants.ReferenceReplacementFileName
                 })
        {
            var legacyPath = Path.Combine(legacyDirectory, fileName);
            var stablePath = Path.Combine(stateDirectory, fileName);

            if (!File.Exists(legacyPath))
            {
                continue;
            }

            if (File.Exists(stablePath))
            {
                var legacyHash = ValidationService.ComputeSha256(legacyPath);
                var stableHash = ValidationService.ComputeSha256(stablePath);
                if (!string.Equals(legacyHash, stableHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Conflicting legacy and stable state files were found for '{fileName}'. "
                        + "No state was selected; reconcile the files manually.");
                }

                continue;
            }

            File.Copy(legacyPath, stablePath, overwrite: false);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public static string GetSettingsPath(
        string baseDirectory) =>
        Path.Combine(
            baseDirectory,
            AppConstants.SettingsFileName);

    public static string GetSessionPath(
        string baseDirectory) =>
        Path.Combine(
            baseDirectory,
            AppConstants.SessionFileName);

    public static string GetReferenceTemplatePath(
        string baseDirectory) =>
        Path.Combine(
            baseDirectory,
            "templates",
            "reference.md");

    public static string GetFinalTemplatePath(
        string baseDirectory) =>
        Path.Combine(
            baseDirectory,
            "templates",
            "final.md");

    public static string GetFinalNoReferenceTemplatePath(
        string baseDirectory) =>
        Path.Combine(
            baseDirectory,
            "templates",
            "final_no_reference.md");

    public static AppSettings LoadSettingsOrDefaults(
        SettingsService settingsService,
        Action<string, string>? showWarning = null)
    {
        try
        {
            return settingsService.Load();
        }
        catch (Exception ex)
        {
            showWarning?.Invoke(
                "Could not load settings.json.\n\n"
                + ex.Message
                + "\n\nDefault settings will be used for this run.",
                "Settings error");

            return settingsService.CreateDefaults();
        }
    }

    public static AppBootstrapContext CreateContext(
        string baseDirectory,
        Action<string, string>? showSettingsWarning = null)
    {
        var mutexName =
            BuildSingleInstanceMutexName(
                baseDirectory);

        var stateDirectory = GetStateDirectory();
        Directory.CreateDirectory(stateDirectory);

        var settingsPath =
            GetSettingsPath(
                stateDirectory);

        var sessionPath =
            GetSessionPath(
                stateDirectory);

        var referenceTemplatePath =
            GetReferenceTemplatePath(
                baseDirectory);

        var finalTemplatePath =
            GetFinalTemplatePath(
                baseDirectory);

        var finalNoReferenceTemplatePath =
            GetFinalNoReferenceTemplatePath(
                baseDirectory);

        var settingsService =
            new SettingsService(
                settingsPath);

        var settings =
            LoadSettingsOrDefaults(
                settingsService,
                showSettingsWarning);

        var validationService =
            new ValidationService();

        var templateService =
            new TemplateService(
                referenceTemplatePath,
                finalTemplatePath,
                finalNoReferenceTemplatePath);

        var imageFinderService =
            new ImageFinderService();

        var assetProcessorService =
            new AssetProcessorService(
                templateService,
                validationService);

        var sessionService =
            new SessionService(
                sessionPath,
                templateService,
                validationService);

        return new AppBootstrapContext
        {
            BaseDirectory =
                baseDirectory,

            MutexName =
                mutexName,

            SettingsPath =
                settingsPath,

            SessionPath =
                sessionPath,

            ReferenceTemplatePath =
                referenceTemplatePath,

            FinalTemplatePath =
                finalTemplatePath,

            FinalNoReferenceTemplatePath =
                finalNoReferenceTemplatePath,

            Settings =
                settings,

            SettingsService =
                settingsService,

            SessionService =
                sessionService,

            TemplateService =
                templateService,

            ValidationService =
                validationService,

            ImageFinderService =
                imageFinderService,

            AssetProcessorService =
                assetProcessorService
        };
    }
}

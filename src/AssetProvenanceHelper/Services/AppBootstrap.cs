using System.Security.Cryptography;
using System.Text;
using AssetProvenanceHelper.Core.Generation;
using AssetProvenanceHelper.Core.Generation.Providers;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;
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

    public required string ProviderTemplateDirectory { get; init; }

    public required string RecentDocumentsPath { get; init; }

    public required string RequestProgressPath { get; init; }

    public required string RequestQueueStatePath { get; init; }

    public required string PixelExactBatchStatePath { get; init; }

    public required string PixelExactStagingPath { get; init; }

    public required AppSettings Settings { get; set; }

    public required SettingsService SettingsService { get; init; }

    public required SessionService SessionService { get; init; }

    public required TemplateService TemplateService { get; init; }

    public required ValidationService ValidationService { get; init; }

    public required ImageFinderService ImageFinderService { get; init; }

    public required AssetProcessorService AssetProcessorService { get; init; }

    public required ProviderTemplateCatalogService ProviderTemplateCatalogService { get; init; }

    public required RecentDocumentHistoryService RecentDocumentHistoryService { get; init; }

    public required RequestProgressService RequestProgressService { get; init; }

    public required RequestQueueStateService RequestQueueStateService { get; init; }

    public required PixelExactBatchStateService PixelExactBatchStateService { get; init; }

    public required ISecretStore SecretStore { get; init; }

    public required GenerationJobStore GenerationJobStore { get; init; }

    public required IImageGenerationProvider ImageGenerationProvider { get; init; }
}

public static class AppBootstrap
{
    private const string LegacyMigrationMarkerFileName =
        ".legacy-state-migration-complete";

    /// <summary>
    /// Test seam: overrides the derived mutex name so tests never contend for the
    /// real, systemwide single-instance mutex that a genuinely running instance of
    /// the app also holds.
    /// </summary>
    internal static Func<string>? MutexNameOverride;

    public static string BuildSingleInstanceMutexName(
        string baseDirectory)
    {
        if (MutexNameOverride is not null)
        {
            return MutexNameOverride();
        }

        // State and single-instance authority must survive portable upgrades.
        // The parameter remains for source compatibility with earlier callers.
        _ = baseDirectory;
        return "Local\\AssetProvenanceHelper_"
            + Convert.ToHexString(
                SHA256.HashData(
                Encoding.UTF8.GetBytes(
                        Environment.UserName.ToUpperInvariant())));
    }

    /// <summary>
    /// Test seam: overrides the resolved state directory so tests never create,
    /// migrate into, or write a migration marker under the real per-user
    /// %LOCALAPPDATA%\Ceegore\AssetProvenanceHelper - doing so from a test could
    /// silently mark a real pending legacy migration as complete without ever
    /// having imported it.
    /// </summary>
    internal static Func<string>? StateDirectoryOverride;

    public static string GetStateDirectory() =>
        StateDirectoryOverride is not null
            ? StateDirectoryOverride()
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ceegore",
                "AssetProvenanceHelper");

    /// <summary>
    /// Copies (never moves) legacy portable state into stable per-user state once.
    /// Existing per-user state is authoritative. A durable marker prevents stale
    /// portable journals from being imported again after recovery removes them.
    /// </summary>
    public static void MigrateLegacyState(string legacyDirectory, string stateDirectory)
    {
        if (PathsEqual(legacyDirectory, stateDirectory))
        {
            return;
        }

        Directory.CreateDirectory(stateDirectory);

        var migrationMarkerPath =
            Path.Combine(
                stateDirectory,
                LegacyMigrationMarkerFileName);

        if (File.Exists(migrationMarkerPath))
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
                continue;
            }

            File.Copy(legacyPath, stablePath, overwrite: false);
        }

        WriteMigrationMarker(migrationMarkerPath);
    }

    private static void WriteMigrationMarker(string migrationMarkerPath)
    {
        var tempPath =
            migrationMarkerPath
            + $".{Guid.NewGuid():N}.tmp";

        try
        {
            var markerBytes =
                Encoding.UTF8.GetBytes(
                    "Legacy state migration completed.\n");

            using (
                var stream =
                    new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.WriteThrough))
            {
                stream.Write(markerBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(
                tempPath,
                migrationMarkerPath,
                overwrite: false);
        }
        finally
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
                // Preserve the migration failure, if any.
            }
        }
    }

    private static bool PathsEqual(string? left, string? right) =>
        ValidationService.PathsEqual(left, right);

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

    public static string GetProviderTemplateDirectory(
        string baseDirectory) =>
        Path.Combine(
            baseDirectory,
            AppConstants.ProviderTemplateFolderName);

    public static string GetRecentDocumentsPath(
        string stateDirectory) =>
        Path.Combine(
            stateDirectory,
            AppConstants.RecentDocumentsFileName);

    public static string GetRequestProgressPath(
        string stateDirectory) =>
        Path.Combine(
            stateDirectory,
            AppConstants.RequestProgressFileName);

    public static string GetRequestQueueStatePath(
        string stateDirectory) =>
        Path.Combine(
            stateDirectory,
            AppConstants.RequestQueueStateFileName);

    public static string GetPixelExactBatchStatePath(string stateDirectory) =>
        Path.Combine(stateDirectory, AppConstants.PixelExactBatchStateFileName);

    public static string GetPixelExactStagingPath(string stateDirectory) =>
        Path.Combine(stateDirectory, AppConstants.PixelExactStagingFolderName);

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

        var providerTemplateCatalogService =
            new ProviderTemplateCatalogService(
                GetProviderTemplateDirectory(
                    baseDirectory));

        var recentDocumentHistoryService =
            new RecentDocumentHistoryService(
                GetRecentDocumentsPath(
                    stateDirectory));

        var requestProgressService =
            new RequestProgressService(
                GetRequestProgressPath(
                    stateDirectory));

        var requestQueueStateService =
            new RequestQueueStateService(
                GetRequestQueueStatePath(stateDirectory),
                validationService);

        var pixelExactBatchStateService = new PixelExactBatchStateService(
            GetPixelExactBatchStatePath(stateDirectory),
            GetPixelExactStagingPath(stateDirectory));

        var secretStore =
            new DpapiSecretStore(
                Path.Combine(
                    stateDirectory,
                    "secrets.dat"));

        var generationJobStore =
            new GenerationJobStore(
                Path.Combine(
                    stateDirectory,
                    "generation-jobs.json"));

        var imageGenerationProvider =
            new OpenAiImageGenerationProvider();

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

            ProviderTemplateDirectory =
                GetProviderTemplateDirectory(
                    baseDirectory),

            RecentDocumentsPath =
                GetRecentDocumentsPath(
                    stateDirectory),

            RequestProgressPath =
                GetRequestProgressPath(
                    stateDirectory),

            RequestQueueStatePath =
                GetRequestQueueStatePath(stateDirectory),

            PixelExactBatchStatePath =
                GetPixelExactBatchStatePath(stateDirectory),

            PixelExactStagingPath =
                GetPixelExactStagingPath(stateDirectory),

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
                assetProcessorService,

            ProviderTemplateCatalogService =
                providerTemplateCatalogService,

            RecentDocumentHistoryService =
                recentDocumentHistoryService,

            RequestProgressService =
                requestProgressService,

            RequestQueueStateService =
                requestQueueStateService,

            PixelExactBatchStateService =
                pixelExactBatchStateService,

            SecretStore =
                secretStore,

            GenerationJobStore =
                generationJobStore,

            ImageGenerationProvider =
                imageGenerationProvider
        };
    }
}

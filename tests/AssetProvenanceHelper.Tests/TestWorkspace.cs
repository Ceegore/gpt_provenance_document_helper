using AssetProvenanceHelper;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class TestWorkspace : IDisposable
{
    public string Root { get; }

    public string Downloads { get; }

    public string Assets { get; }

    public string Templates { get; }

    public string ReferenceTemplatePath =>
        Path.Combine(
            Templates,
            "reference.md");

    public string FinalTemplatePath =>
        Path.Combine(
            Templates,
            "final.md");

    public string SettingsPath =>
        Path.Combine(
            Root,
            "settings.json");

    public string SessionPath =>
        Path.Combine(
            Root,
            "session.json");

    public TestWorkspace()
    {
        Root =
            Path.Combine(
                Path.GetTempPath(),
                "AssetProvenanceHelperTests",
                Guid.NewGuid().ToString("N"));

        Downloads =
            Path.Combine(
                Root,
                "Downloads");

        Assets =
            Path.Combine(
                Root,
                "Assets");

        Templates =
            Path.Combine(
                Root,
                "templates");

        Directory.CreateDirectory(
            Downloads);

        Directory.CreateDirectory(
            Assets);

        Directory.CreateDirectory(
            Templates);

        WriteValidTemplates();
    }

    public AppSettings CreateSettings()
    {
        return new AppSettings
        {
            ProjectName =
                "SpellQuake",

            DownloadFolder =
                Downloads,

            AssetRootFolder =
                Assets,

            AcceptedExtensions =
                AppConstants
                    .DefaultImageExtensions
                    .ToList()
        };
    }

    public string CreateImage(
        string filename,
        byte[]? contents = null)
    {
        var path =
            Path.Combine(
                Downloads,
                filename);

        File.WriteAllBytes(
            path,
            contents
            ?? new byte[]
            {
                1,
                2,
                3,
                4
            });

        return path;
    }

    public TemplateService CreateTemplateService()
    {
        return new TemplateService(
            ReferenceTemplatePath,
            FinalTemplatePath);
    }

    public ValidationService CreateValidationService()
    {
        return new ValidationService();
    }

    public AssetProcessorService CreateAssetProcessor()
    {
        return new AssetProcessorService(
            CreateTemplateService(),
            CreateValidationService());
    }

    public SessionService CreateSessionService()
    {
        return new SessionService(
            SessionPath,
            CreateTemplateService(),
            CreateValidationService());
    }

    public SettingsService CreateSettingsService()
    {
        return new SettingsService(
            SettingsPath);
    }

    public ImageFinderService CreateImageFinder()
    {
        return new ImageFinderService();
    }

    public void WriteValidTemplates()
    {
        File.WriteAllText(
            ReferenceTemplatePath,
            """
            # AI ASSET RIGHTS / PROVENANCE RECORD

            Asset ID: {{REFERENCE_FILENAME}}
            Project: {{PROJECT}}
            Generation date: {{GENERATION_DATE}}

            STATIC_REFERENCE_MARKER
            """);

        File.WriteAllText(
            FinalTemplatePath,
            """
            # AI ASSET RIGHTS / PROVENANCE RECORD

            Asset ID: {{FINAL_FILENAME}}
            Project: {{PROJECT}}
            Generation date: {{GENERATION_DATE}}
            Reference asset: {{REFERENCE_FILENAME}}
            Prompt: "{{PROMPT}}"

            STATIC_FINAL_MARKER
            """);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(
                    Root,
                    recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }
}

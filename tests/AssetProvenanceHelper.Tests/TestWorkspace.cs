using System.Text;
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

    public string ProviderTemplates { get; }

    public string Examples { get; }

    public string RecentDocumentsPath =>
        Path.Combine(
            Root,
            AppConstants.RecentDocumentsFileName);

    public string RequestProgressPath =>
        Path.Combine(
            Root,
            AppConstants.RequestProgressFileName);

    public string RequestQueueStatePath =>
        Path.Combine(
            Root,
            AppConstants.RequestQueueStateFileName);

    public string ChatGptProviderTemplatePath =>
        Path.Combine(
            ProviderTemplates,
            "ChatGPT.md");

    public string ReferenceTemplatePath =>
        Path.Combine(
            Templates,
            "reference.md");

    public string FinalTemplatePath =>
        Path.Combine(
            Templates,
            "final.md");

    public string FinalNoReferenceTemplatePath =>
        Path.Combine(
            Templates,
            "final_no_reference.md");

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

        ProviderTemplates =
            Path.Combine(
                Root,
                "provider_templates");

        Examples =
            Path.Combine(
                Root,
                "examples");

        Directory.CreateDirectory(
            ProviderTemplates);

        Directory.CreateDirectory(
            Examples);

        WriteValidTemplates();
        WriteValidProviderTemplate();
    }

    public AppSettings CreateSettings()
    {
        return new AppSettings
        {
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

    public static byte[] GetValidImageBytesForExtension(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".png" => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D },
            ".jpg" or ".jpeg" => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 },
            ".webp" => new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x24, 0x00, 0x00, 0x00, (byte)'W', (byte)'E', (byte)'B', (byte)'P' },
            _ => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
        };
    }

    public static byte[] EnsureMagicBytes(string filename, byte[] payload)
    {
        var magic = GetValidImageBytesForExtension(filename);
        if (payload.Length >= magic.Length)
        {
            var match = true;
            for (int i = 0; i < magic.Length; i++)
            {
                if (payload[i] != magic[i])
                {
                    match = false;
                    break;
                }
            }
            if (match) return payload;
        }

        var result = new byte[magic.Length + payload.Length];
        Buffer.BlockCopy(magic, 0, result, 0, magic.Length);
        Buffer.BlockCopy(payload, 0, result, magic.Length, payload.Length);
        return result;
    }

    public string CreateImage(
        string filename,
        byte[]? contents = null)
    {
        var path =
            Path.Combine(
                Downloads,
                filename);

        var bytes = contents is not null
            ? EnsureMagicBytes(filename, contents)
            : GetValidImageBytesForExtension(filename);

        File.WriteAllBytes(
            path,
            bytes);

        return path;
    }

    public string CreateRawFile(
        string filename,
        byte[] contents)
    {
        var path =
            Path.Combine(
                Downloads,
                filename);

        File.WriteAllBytes(
            path,
            contents);

        return path;
    }

    public TemplateService CreateTemplateService()
    {
        return new TemplateService(
            ReferenceTemplatePath,
            FinalTemplatePath,
            FinalNoReferenceTemplatePath);
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

        File.WriteAllText(
            FinalNoReferenceTemplatePath,
            """
            # AI ASSET RIGHTS / PROVENANCE RECORD

            Asset ID: {{FINAL_FILENAME}}
            Project: {{PROJECT}}
            Generation date: {{GENERATION_DATE}}
            Prompt: "{{PROMPT}}"

            STATIC_FINAL_NO_REFERENCE_MARKER
            """);
    }

    public void WriteValidProviderTemplate(
        string fileName = "ChatGPT.md")
    {
        var content =
            """
            Provider: <<<PROVIDER>>>
            Date: <<<DATE>>>
            File: <<<FILENAME>>>
            Asset: <<<ASSET_NAME>>>
            Project: <<<PROJECT>>>
            Role: <<<ROLE>>>
            Workflow: <<<WORKFLOW>>>
            Reference: <<<REFERENCE_FILENAME>>>
            Prompt:
            <<<PROMPT>>>
            """;

        File.WriteAllText(
            Path.Combine(
                ProviderTemplates,
                fileName),
            content,
            new UTF8Encoding(false));
    }

    public ProviderTemplateCatalogService
        CreateProviderTemplateCatalogService() =>
        new(
            ProviderTemplates);

    public RecentDocumentHistoryService
        CreateRecentDocumentHistoryService() =>
        new(
            RecentDocumentsPath);

    public RequestProgressService
        CreateRequestProgressService() =>
        new(
            RequestProgressPath);

    public RequestQueueStateService
        CreateRequestQueueStateService() =>
        new(
            RequestQueueStatePath,
            CreateValidationService());

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(
                        Root,
                        recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }

        if (Directory.Exists(Root))
        {
            throw new IOException($"TestWorkspace leaked: {Root}");
        }
    }
}

public static class TestExtensions
{
    public static string ProcessMainPrepared(
        this AssetProcessorService processor,
        AssetSession session,
        IReadOnlyCollection<string> acceptedExtensions,
        string sourceImagePath,
        string prompt,
        DateTimeOffset processedAt)
    {
        processor.PrepareMainCommit(session, acceptedExtensions, sourceImagePath, prompt, processedAt);
        return processor.ProcessMainImage(session, acceptedExtensions, sourceImagePath, prompt, processedAt);
    }
}

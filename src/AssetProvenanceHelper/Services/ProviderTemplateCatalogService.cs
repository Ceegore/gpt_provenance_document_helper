using System.Text;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class ProviderTemplateCatalogService
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    private readonly string _templateDirectory;

    public ProviderTemplateCatalogService(
        string templateDirectory)
    {
        _templateDirectory =
            templateDirectory;
    }

    public string TemplateDirectory =>
        _templateDirectory;

    public ProviderCatalogResult Load()
    {
        var result =
            new ProviderCatalogResult();

        if (!Directory.Exists(
                _templateDirectory))
        {
            result.Errors.Add(
                $"Provider template directory does not exist: {_templateDirectory}");

            return result;
        }

        string[] files;

        try
        {
            files =
                Directory
                    .EnumerateFiles(
                        _templateDirectory,
                        "*.md",
                        SearchOption.TopDirectoryOnly)
                    .OrderBy(
                        Path.GetFileName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
        catch (Exception ex)
        {
            result.Errors.Add(
                $"Could not scan provider template directory: {ex.Message}");

            return result;
        }

        var seenDisplayNames =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var fileName =
                Path.GetFileName(path);

            if (fileName.StartsWith(
                    "_",
                    StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var info =
                    new FileInfo(path);

                if ((info.Attributes
                     & FileAttributes.ReparsePoint) != 0)
                {
                    result.Errors.Add(
                        $"Provider template '{fileName}' is a reparse point and was ignored.");

                    continue;
                }

                if (info.Length <= 0)
                {
                    result.Errors.Add(
                        $"Provider template '{fileName}' is empty.");

                    continue;
                }

                if (info.Length
                    > ProviderTemplateRules.MaxTemplateBytes + 3)
                {
                    result.Errors.Add(
                        $"Provider template '{fileName}' exceeds the size limit.");

                    continue;
                }

                var raw =
                    File.ReadAllBytes(path);

                var content =
                    DecodeUtf8(raw);

                var validation =
                    ProviderTemplateRules.ValidateContent(
                        fileName,
                        content);

                if (!validation.IsValid)
                {
                    result.Errors.AddRange(
                        validation.Errors);

                    continue;
                }

                var displayName =
                    Path.GetFileNameWithoutExtension(
                        fileName);

                if (!seenDisplayNames.Add(
                        displayName))
                {
                    result.Errors.Add(
                        $"Provider display name '{displayName}' is duplicated. File '{fileName}' was ignored.");

                    continue;
                }

                result.Templates.Add(
                    new ProviderTemplateDefinition
                    {
                        FileName =
                            fileName,

                        DisplayName =
                            displayName,

                        FullPath =
                            info.FullName,

                        ContentSha256 =
                            ProviderTemplateRules
                                .ComputeContentSha256(
                                    content),

                        Content =
                            content,

                        IsSessionSnapshot =
                            false
                    });
            }
            catch (Exception ex)
            {
                result.Errors.Add(
                    $"Could not load provider template '{fileName}': {ex.Message}");
            }
        }

        result.Templates.Sort(
            (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(
                    left.DisplayName,
                    right.DisplayName));

        return result;
    }

    private static string DecodeUtf8(
        byte[] raw)
    {
        var offset = 0;

        if (raw.Length >= 3
            && raw[0] == 0xEF
            && raw[1] == 0xBB
            && raw[2] == 0xBF)
        {
            offset = 3;
        }

        if (raw.Length >= 2
            && ((raw[0] == 0xFF
                 && raw[1] == 0xFE)
                || (raw[0] == 0xFE
                    && raw[1] == 0xFF)))
        {
            throw new InvalidDataException(
                "Provider templates must be saved as UTF-8, not UTF-16.");
        }

        return StrictUtf8.GetString(
            raw,
            offset,
            raw.Length - offset);
    }
}
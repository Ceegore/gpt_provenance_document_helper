using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public sealed class AssetRequestManifestService
{
    private const long MaxManifestBytes =
        32L * 1024L * 1024L;

    private const int MaxAssets =
        5000;

    private const int MaxPromptCharacters =
        1_000_000;

    private static readonly Regex ResolutionRegex =
        new(
            @"^\s*(?<w>[0-9]+)\s*[x×]\s*(?<h>[0-9]+)\s*$",
            RegexOptions.Compiled
            | RegexOptions.CultureInvariant);

    private readonly ValidationService _validationService;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            PropertyNameCaseInsensitive =
                false,

            AllowTrailingCommas =
                false,

            ReadCommentHandling =
                JsonCommentHandling.Disallow,

            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow
        };

    public AssetRequestManifestService(
        ValidationService validationService)
    {
        _validationService =
            validationService;
    }

    public AssetRequestManifest Load(
        string path,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        ArgumentNullException.ThrowIfNull(
            acceptedExtensions);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                "Request Manifest path is empty.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Request Manifest does not exist.",
                path);
        }

        var info =
            new FileInfo(path);

        if (info.Length <= 0)
        {
            throw new InvalidDataException(
                "Request Manifest is empty.");
        }

        if (info.Length > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"Request Manifest exceeds the {MaxManifestBytes} byte limit.");
        }

        ManifestDto? dto;

        try
        {
            var json =
                File.ReadAllText(
                    path,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true));

            dto =
                JsonSerializer.Deserialize<ManifestDto>(
                    json,
                    _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Request Manifest JSON is invalid: {ex.Message}",
                ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException(
                "Request Manifest could not be deserialized.");
        }

        if (dto.ManifestVersion != 1 && dto.ManifestVersion != 2)
        {
            throw new InvalidDataException(
                $"Unsupported manifestVersion {dto.ManifestVersion}. Expected 1 or 2.");
        }

        if (dto.Assets is null
            || dto.Assets.Count == 0)
        {
            throw new InvalidDataException(
                "Request Manifest contains no assets.");
        }

        if (dto.Assets.Count > MaxAssets)
        {
            throw new InvalidDataException(
                $"Request Manifest contains more than {MaxAssets} assets.");
        }

        var items =
            new List<AssetRequestItem>();

        var filenames =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        for (var index = 0;
             index < dto.Assets.Count;
             index++)
        {
            var raw =
                dto.Assets[index];

            var itemNumber =
                index + 1;

            try
            {
                var fileName =
                    ValidateFilename(
                        raw.Filename,
                        acceptedExtensions);

                if (!filenames.Add(fileName))
                {
                    throw new InvalidDataException(
                        $"Duplicate filename '{fileName}'.");
                }

                var (width, height, normalizedResolution) =
                    ParseResolution(
                        raw.Resolution);

                if (raw.Prompt is null
                    || string.IsNullOrWhiteSpace(
                        raw.Prompt))
                {
                    throw new InvalidDataException(
                        "prompt is missing or blank.");
                }

                if (raw.Prompt.Length
                    > MaxPromptCharacters)
                {
                    throw new InvalidDataException(
                        $"prompt exceeds {MaxPromptCharacters} characters.");
                }

                var assetName =
                    Path.GetFileNameWithoutExtension(
                        fileName);

                var assetValidation =
                    _validationService.ValidateAssetName(
                        assetName,
                        acceptedExtensions);

                if (!assetValidation.IsValid)
                {
                    throw new InvalidDataException(
                        string.Join(
                            "; ",
                            assetValidation.Errors));
                }

                var alpha =
                    ParseAlphaRequirement(
                        raw.Alpha,
                        dto.ManifestVersion);

                var requestKey =
                    dto.ManifestVersion == 2
                        ? ComputeRequestKeyV2(
                            fileName,
                            normalizedResolution,
                            raw.Prompt,
                            alpha)
                        : ComputeRequestKey(
                            fileName,
                            normalizedResolution,
                            raw.Prompt);

                items.Add(
                    new AssetRequestItem
                    {
                        FileName =
                            fileName,

                        AssetName =
                            assetName,

                        Width =
                            width,

                        Height =
                            height,

                        Resolution =
                            normalizedResolution,

                        Prompt =
                            raw.Prompt,

                        RequestKey =
                            requestKey,

                        Alpha =
                            alpha
                    });
            }
            catch (Exception ex)
                when (ex is InvalidDataException
                      or ArgumentException)
            {
                throw new InvalidDataException(
                    $"Asset #{itemNumber}: {ex.Message}",
                    ex);
            }
        }

        var manifestFingerprint =
            ComputeManifestFingerprint(
                items,
                dto.ManifestVersion);

        return new AssetRequestManifest
        {
            Version =
                dto.ManifestVersion,

            SourcePath =
                Path.GetFullPath(path),

            ManifestFingerprint =
                manifestFingerprint,

            Items =
                items
        };
    }

    private static string ValidateFilename(
        string? value,
        IReadOnlyCollection<string> acceptedExtensions)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                "filename is missing or blank.");
        }

        if (!string.Equals(
                Path.GetFileName(value),
                value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "filename must contain only a leaf filename, not a path.");
        }

        if (value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "filename contains control characters.");
        }

        var extension =
            Path.GetExtension(value);

        if (string.IsNullOrWhiteSpace(extension)
            || !acceptedExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"filename uses unsupported image extension '{extension}'.");
        }

        return value;
    }

    private static (
        int Width,
        int Height,
        string Normalized)
        ParseResolution(
            string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                "resolution is missing or blank.");
        }

        var match =
            ResolutionRegex.Match(value);

        if (!match.Success)
        {
            throw new InvalidDataException(
                $"resolution '{value}' is invalid.");
        }

        if (!int.TryParse(
                match.Groups["w"].Value,
                out var width)
            || !int.TryParse(
                match.Groups["h"].Value,
                out var height))
        {
            throw new InvalidDataException(
                $"resolution '{value}' contains invalid numbers.");
        }

        if (width < 1
            || width > 100_000
            || height < 1
            || height > 100_000)
        {
            throw new InvalidDataException(
                "resolution dimensions must each be between 1 and 100000.");
        }

        return (
            width,
            height,
            $"{width}x{height}");
    }

    internal static string ComputeRequestKey(
        string fileName,
        string normalizedResolution,
        string prompt)
    {
        var normalizedPrompt =
            NormalizeLineEndings(prompt);

        var material =
            fileName.ToLowerInvariant()
            + "\n"
            + normalizedResolution
            + "\n"
            + normalizedPrompt;

        return ComputeSha256(
            material);
    }

    internal static string ComputeRequestKeyV2(
        string fileName,
        string normalizedResolution,
        string prompt,
        Core.Generation.AlphaRequirement alpha)
    {
        var normalizedPrompt =
            NormalizeLineEndings(prompt);

        var alphaStr = alpha switch
        {
            Core.Generation.AlphaRequirement.Required => "required",
            Core.Generation.AlphaRequirement.NotRequired => "not_required",
            _ => "unknown"
        };

        var material =
            fileName.ToLowerInvariant()
            + "\n"
            + normalizedResolution
            + "\n"
            + alphaStr
            + "\n"
            + normalizedPrompt;

        return ComputeSha256(
            material);
    }

    internal static string ComputeManifestFingerprint(
        IEnumerable<AssetRequestItem> items,
        int manifestVersion = 1)
    {
        var keys =
            items
                .Select(item => item.RequestKey)
                .OrderBy(
                    key => key,
                    StringComparer.Ordinal)
                .ToArray();

        var material =
            $"manifestVersion={manifestVersion}\n"
            + string.Join(
                "\n",
                keys);

        return ComputeSha256(
            material);
    }

    private static Core.Generation.AlphaRequirement ParseAlphaRequirement(
        string? value,
        int manifestVersion)
    {
        if (manifestVersion == 1)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException(
                    "alpha property is not supported in manifestVersion 1.");
            }

            return Core.Generation.AlphaRequirement.Unknown;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return Core.Generation.AlphaRequirement.Unknown;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "required" =>
                Core.Generation.AlphaRequirement.Required,

            "not_required" =>
                Core.Generation.AlphaRequirement.NotRequired,

            "unknown" =>
                Core.Generation.AlphaRequirement.Unknown,

            _ =>
                throw new InvalidDataException(
                    $"Unsupported alpha value '{value}'. Expected 'required', 'not_required', or 'unknown'.")
        };
    }

    private static string NormalizeLineEndings(
        string value)
    {
        return value
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
            .Replace(
                '\r',
                '\n');
    }

    private static string ComputeSha256(
        string value)
    {
        var bytes =
            Encoding.UTF8.GetBytes(value);

        return Convert
            .ToHexString(
                SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed class ManifestDto
    {
        [JsonPropertyName("manifestVersion")]
        public int ManifestVersion { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetDto>? Assets { get; set; }
    }

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed class AssetDto
    {
        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("resolution")]
        public string? Resolution { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("alpha")]
        public string? Alpha { get; set; }
    }
}
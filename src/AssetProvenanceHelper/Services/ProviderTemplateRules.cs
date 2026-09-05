using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AssetProvenanceHelper.Models;

namespace AssetProvenanceHelper.Services;

public static class ProviderTemplateRules
{
    public const int MaxTemplateBytes =
        256 * 1024;

    public static readonly IReadOnlyList<string> RequiredTags =
        new[]
        {
            "<<<PROVIDER>>>",
            "<<<DATE>>>",
            "<<<FILENAME>>>",
            "<<<ASSET_NAME>>>",
            "<<<PROJECT>>>",
            "<<<ROLE>>>",
            "<<<WORKFLOW>>>",
            "<<<REFERENCE_FILENAME>>>",
            "<<<PROMPT>>>"
        };

    public static readonly IReadOnlyList<string> OptionalApiTags =
        new[]
        {
            "<<<API_CANDIDATE_ID>>>",
            "<<<API_PROVIDER>>>",
            "<<<API_MODEL>>>",
            "<<<API_MODE>>>",
            "<<<API_CUSTOM_ID>>>",
            "<<<API_TARGET_RESOLUTION>>>",
            "<<<API_PROVIDER_RESOLUTION>>>",
            "<<<API_RAW_SHA256>>>",
            "<<<API_NORMALIZED_SHA256>>>",
            "<<<API_PROVIDER_REQUEST_ID>>>",
            "<<<API_BATCH_ID>>>",
            "<<<API_CREATED_AT_UTC>>>"
        };

    private static readonly HashSet<string> SupportedTags =
        new(
            RequiredTags.Concat(OptionalApiTags),
            StringComparer.Ordinal);

    private static readonly Regex AnyTagRegex =
        new(
            @"<<<[^<>\r\n]+>>>",
            RegexOptions.Compiled
            | RegexOptions.CultureInvariant);

    public static ValidationResult ValidateContent(
        string fileName,
        string content)
    {
        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(content))
        {
            errors.Add(
                $"Provider template '{fileName}' is empty.");

            return ValidationResult.Failure(errors);
        }

        var utf8Length =
            Encoding.UTF8.GetByteCount(content);

        if (utf8Length > MaxTemplateBytes)
        {
            errors.Add(
                $"Provider template '{fileName}' exceeds the {MaxTemplateBytes} byte UTF-8 limit.");
        }

        foreach (var requiredTag in RequiredTags)
        {
            if (!content.Contains(
                    requiredTag,
                    StringComparison.Ordinal))
            {
                errors.Add(
                    $"Provider template '{fileName}' is missing required tag {requiredTag}.");
            }
        }

        foreach (Match match in AnyTagRegex.Matches(content))
        {
            if (!SupportedTags.Contains(match.Value))
            {
                errors.Add(
                    $"Provider template '{fileName}' contains unsupported tag {match.Value}.");
            }
        }

        var withoutRecognizedTags =
            AnyTagRegex.Replace(
                content,
                string.Empty);

        if (withoutRecognizedTags.Contains(
                "<<<",
                StringComparison.Ordinal)
            || withoutRecognizedTags.Contains(
                ">>>",
                StringComparison.Ordinal))
        {
            errors.Add(
                $"Provider template '{fileName}' contains malformed <<<...>>> tag delimiters.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public static ValidationResult ValidateSnapshot(
        ProviderTemplateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var errors =
            new List<string>();

        if (string.IsNullOrWhiteSpace(snapshot.FileName))
        {
            errors.Add(
                "Provider snapshot FileName is missing.");
        }
        else
        {
            if (!string.Equals(
                    Path.GetFileName(snapshot.FileName),
                    snapshot.FileName,
                    StringComparison.Ordinal))
            {
                errors.Add(
                    "Provider snapshot FileName must contain only a filename.");
            }

            if (!string.Equals(
                    Path.GetExtension(snapshot.FileName),
                    ".md",
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    "Provider snapshot FileName must use .md.");
            }
        }

        if (string.IsNullOrWhiteSpace(snapshot.DisplayName))
        {
            errors.Add(
                "Provider snapshot DisplayName is missing.");
        }

        var contentValidation =
            ValidateContent(
                snapshot.FileName,
                snapshot.Content);

        if (!contentValidation.IsValid)
        {
            errors.AddRange(
                contentValidation.Errors);
        }

        var actualHash =
            ComputeContentSha256(
                snapshot.Content);

        if (string.IsNullOrWhiteSpace(
                snapshot.ContentSha256)
            || snapshot.ContentSha256.Length != 64
            || snapshot.ContentSha256.Any(
                c => !Uri.IsHexDigit(c)))
        {
            errors.Add(
                "Provider snapshot ContentSha256 is missing or invalid.");
        }
        else if (!string.Equals(
                     actualHash,
                     snapshot.ContentSha256,
                     StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "Provider snapshot content does not match ContentSha256.");
        }

        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors);
    }

    public static string ComputeContentSha256(
        string content)
    {
        var bytes =
            new UTF8Encoding(false)
                .GetBytes(content);

        return Convert
            .ToHexString(
                SHA256.HashData(bytes))
            .ToLowerInvariant();
    }
}
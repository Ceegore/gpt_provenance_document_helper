using System.Text.Json;

namespace AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

public static class OpenAiBatchResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<BatchItemOutput> ParseResults(
        string? outputJsonlContent,
        string? errorJsonlContent)
    {
        var results = new List<BatchItemOutput>();
        var seenCustomIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateCustomIds = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(outputJsonlContent))
        {
            ParseLines(outputJsonlContent, results, seenCustomIds, duplicateCustomIds);
        }

        if (!string.IsNullOrWhiteSpace(errorJsonlContent))
        {
            ParseLines(errorJsonlContent, results, seenCustomIds, duplicateCustomIds);
        }

        if (duplicateCustomIds.Count > 0)
        {
            for (var i = 0; i < results.Count; i++)
            {
                if (duplicateCustomIds.Contains(results[i].CustomId))
                {
                    results[i] = new BatchItemOutput(
                        CustomId: results[i].CustomId,
                        IsSuccess: false,
                        ImageBytes: null,
                        StatusCode: 409,
                        ErrorCode: "duplicate_custom_id",
                        ErrorMessage: "Duplicate custom_id detected in batch output files. Rejected for safety.",
                        ProviderRequestId: null);
                }
            }
        }

        return results;
    }

    private static void ParseLines(
        string jsonl,
        List<BatchItemOutput> results,
        HashSet<string> seenCustomIds,
        HashSet<string> duplicateCustomIds)
    {
        using var reader = new StringReader(jsonl);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            OpenAiBatchResultLine? item;
            try
            {
                item = JsonSerializer.Deserialize<OpenAiBatchResultLine>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (item == null || string.IsNullOrWhiteSpace(item.CustomId))
            {
                continue;
            }

            if (!seenCustomIds.Add(item.CustomId))
            {
                // Duplicate custom_id in output/error files: fail closed for duplicate
                duplicateCustomIds.Add(item.CustomId);
                continue;
            }

            if (item.Error != null)
            {
                results.Add(new BatchItemOutput(
                    CustomId: item.CustomId,
                    IsSuccess: false,
                    ImageBytes: null,
                    StatusCode: item.Response?.StatusCode ?? 400,
                    ErrorCode: item.Error.Code,
                    ErrorMessage: item.Error.Message,
                    ProviderRequestId: item.Response?.RequestId));
                continue;
            }

            var statusCode = item.Response?.StatusCode ?? 0;
            var isMalformedBase64 = false;
            if (statusCode >= 200 && statusCode < 300)
            {
                var b64 = item.Response?.Body?.Data?.FirstOrDefault()?.B64Json;
                byte[]? imageBytes = null;
                if (!string.IsNullOrEmpty(b64))
                {
                    try
                    {
                        imageBytes = Convert.FromBase64String(b64);
                    }
                    catch (FormatException)
                    {
                        imageBytes = null;
                        isMalformedBase64 = true;
                    }
                }

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    results.Add(new BatchItemOutput(
                        CustomId: item.CustomId,
                        IsSuccess: true,
                        ImageBytes: imageBytes,
                        StatusCode: statusCode,
                        ErrorCode: null,
                        ErrorMessage: null,
                        ProviderRequestId: item.Response?.RequestId));
                    continue;
                }
            }

            // Unsuccessful response
            results.Add(new BatchItemOutput(
                CustomId: item.CustomId,
                IsSuccess: false,
                ImageBytes: null,
                StatusCode: statusCode == 0 ? 500 : statusCode,
                ErrorCode: isMalformedBase64 ? "malformed_base64" : (item.Error?.Code ?? "unknown_error"),
                ErrorMessage: isMalformedBase64 ? "Batch item contained malformed base64 image data." : (item.Error?.Message ?? "Batch item response did not contain image data."),
                ProviderRequestId: item.Response?.RequestId));
        }
    }
}

using System.Net;
using System.Text.Json;

namespace AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

public sealed class OpenAiApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }
    public string? ErrorType { get; }
    public string? RequestId { get; }

    public OpenAiApiException(
        HttpStatusCode statusCode,
        string? errorCode,
        string? errorType,
        string message,
        string? requestId = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorType = errorType;
        RequestId = requestId;
    }
}

public static class OpenAiErrorParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static OpenAiApiException Parse(HttpStatusCode statusCode, string? responseBody, string? requestId = null)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new OpenAiApiException(
                statusCode,
                null,
                null,
                $"OpenAI API request failed with HTTP {(int)statusCode} ({statusCode}).",
                requestId);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<OpenAiErrorEnvelope>(responseBody, JsonOptions);
            var err = envelope?.Error;
            if (err != null)
            {
                var message = string.IsNullOrWhiteSpace(err.Message)
                    ? $"OpenAI API returned error type '{err.Type}' with code '{err.Code}'."
                    : err.Message.Trim();

                return new OpenAiApiException(statusCode, err.Code, err.Type, message, requestId);
            }
        }
        catch (JsonException)
        {
            // Fallback for non-JSON error payloads
        }

        if (responseBody.TrimStart().StartsWith('<'))
        {
            var titleMatch = System.Text.RegularExpressions.Regex.Match(
                responseBody,
                @"<title>(?<title>[^<]+)</title>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (titleMatch.Success)
            {
                var title = titleMatch.Groups["title"].Value.Trim();
                return new OpenAiApiException(
                    statusCode,
                    null,
                    "html_gateway_error",
                    $"OpenAI API returned HTTP {(int)statusCode}: {title}",
                    requestId);
            }
        }

        var trimmed = responseBody.Length > 300 ? responseBody[..300] + "..." : responseBody;
        return new OpenAiApiException(
            statusCode,
            null,
            null,
            $"OpenAI API returned HTTP {(int)statusCode}: {trimmed}",
            requestId);
    }
}

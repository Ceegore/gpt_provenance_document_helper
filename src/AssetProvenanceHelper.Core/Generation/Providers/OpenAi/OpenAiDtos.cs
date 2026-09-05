using System.Text.Json.Serialization;

namespace AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

public sealed record OpenAiImageGenerationRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("quality")] string Quality,
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("output_format")] string OutputFormat,
    [property: JsonPropertyName("background")] string Background);

public sealed record OpenAiImageGenerationData(
    [property: JsonPropertyName("b64_json")] string? B64Json,
    [property: JsonPropertyName("url")] string? Url);

public sealed record OpenAiImageGenerationResponse(
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiImageGenerationData>? Data)
{
    public string? RequestId { get; init; }
}

public sealed record OpenAiCreateBatchRequest(
    [property: JsonPropertyName("input_file_id")] string InputFileId,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("completion_window")] string CompletionWindow);

public sealed record OpenAiBatchCounts(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("failed")] int Failed);

public sealed record OpenAiBatchResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("input_file_id")] string? InputFileId,
    [property: JsonPropertyName("completion_window")] string? CompletionWindow,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("output_file_id")] string? OutputFileId,
    [property: JsonPropertyName("error_file_id")] string? ErrorFileId,
    [property: JsonPropertyName("created_at")] long? CreatedAt,
    [property: JsonPropertyName("in_progress_at")] long? InProgressAt,
    [property: JsonPropertyName("expires_at")] long? ExpiresAt,
    [property: JsonPropertyName("completed_at")] long? CompletedAt,
    [property: JsonPropertyName("failed_at")] long? FailedAt,
    [property: JsonPropertyName("expired_at")] long? ExpiredAt,
    [property: JsonPropertyName("cancelling_at")] long? CancellingAt,
    [property: JsonPropertyName("cancelled_at")] long? CancelledAt,
    [property: JsonPropertyName("request_counts")] OpenAiBatchCounts? RequestCounts);

public sealed record OpenAiFileResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("purpose")] string? Purpose);

public sealed record OpenAiErrorDetail(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("param")] string? Param,
    [property: JsonPropertyName("code")] string? Code);

public sealed record OpenAiErrorEnvelope(
    [property: JsonPropertyName("error")] OpenAiErrorDetail? Error);

public sealed record OpenAiBatchJsonlRequestLine(
    [property: JsonPropertyName("custom_id")] string CustomId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("body")] OpenAiImageGenerationRequest Body);

public sealed record OpenAiBatchResponseBody(
    [property: JsonPropertyName("created")] long? Created,
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiImageGenerationData>? Data);

public sealed record OpenAiBatchResponsePayload(
    [property: JsonPropertyName("status_code")] int StatusCode,
    [property: JsonPropertyName("request_id")] string? RequestId,
    [property: JsonPropertyName("body")] OpenAiBatchResponseBody? Body);

public sealed record OpenAiBatchResultLine(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("custom_id")] string CustomId,
    [property: JsonPropertyName("response")] OpenAiBatchResponsePayload? Response,
    [property: JsonPropertyName("error")] OpenAiErrorDetail? Error);

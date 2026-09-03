using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

public sealed class OpenAiApiClient : IDisposable
{
    public static readonly Uri DefaultBaseUri = new("https://api.openai.com/v1/");
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly RetryPolicy _retryPolicy;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public OpenAiApiClient(HttpClient? httpClient = null, RetryPolicy? retryPolicy = null)
    {
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = DefaultBaseUri;
        }

        if (_httpClient.Timeout != DefaultTimeout && httpClient == null)
        {
            _httpClient.Timeout = DefaultTimeout;
        }

        _retryPolicy = retryPolicy ?? new RetryPolicy();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<OpenAiImageGenerationResponse> GenerateImageAsync(
        OpenAiImageGenerationRequest request,
        string apiKey,
        CancellationToken cancellationToken = default,
        RetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var policy = retryPolicy ?? _retryPolicy;

        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "images/generations")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

                var requestId = GetRequestId(response);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var result = JsonSerializer.Deserialize<OpenAiImageGenerationResponse>(responseJson, JsonOptions);
                    if (result == null || result.Data == null || result.Data.Count == 0)
                    {
                        throw new OpenAiApiException(response.StatusCode, "empty_response", null, "OpenAI API returned empty generation data.", requestId);
                    }
                    return result with { RequestId = requestId };
                }

                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (policy.MaxAttempts > attempt && RetryPolicy.IsRetryableStatusCode(response.StatusCode))
                {
                    var delay = policy.GetDelay(attempt, response.Headers);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw OpenAiErrorParser.Parse(response.StatusCode, errorBody, requestId);
            }
            catch (Exception ex) when (ex is not OpenAiApiException && policy.MaxAttempts > attempt && RetryPolicy.IsRetryableException(ex))
            {
                var delay = policy.GetDelay(attempt, response?.Headers);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw new InvalidOperationException("Exceeded maximum retry attempts for image generation.");
    }

    public async Task<OpenAiFileResponse> UploadBatchFileAsync(
        byte[] jsonlBytes,
        string fileName,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonlBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "files");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var multipart = new MultipartFormDataContent();
        using var byteContent = new ByteArrayContent(jsonlBytes);
        byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        multipart.Add(byteContent, "file", fileName);
        multipart.Add(new StringContent("batch"), "purpose");

        httpRequest.Content = multipart;

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var requestId = GetRequestId(response);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw OpenAiErrorParser.Parse(response.StatusCode, errorBody, requestId);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var fileResponse = JsonSerializer.Deserialize<OpenAiFileResponse>(responseJson, JsonOptions);
        return fileResponse ?? throw new OpenAiApiException(response.StatusCode, "deserialization_failed", null, "Failed to parse files endpoint response.", requestId);
    }

    public async Task<OpenAiBatchResponse> CreateBatchAsync(
        string inputFileId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var requestBody = new OpenAiCreateBatchRequest(
            InputFileId: inputFileId,
            Endpoint: "/v1/images/generations",
            CompletionWindow: "24h");

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "batches")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var requestId = GetRequestId(response);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw OpenAiErrorParser.Parse(response.StatusCode, errorBody, requestId);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var batchResponse = JsonSerializer.Deserialize<OpenAiBatchResponse>(responseJson, JsonOptions);
        return batchResponse ?? throw new OpenAiApiException(response.StatusCode, "deserialization_failed", null, "Failed to parse batch creation response.", requestId);
    }

    public async Task<OpenAiBatchResponse> GetBatchAsync(
        string batchId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"batches/{Uri.EscapeDataString(batchId)}");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var requestId = GetRequestId(response);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw OpenAiErrorParser.Parse(response.StatusCode, errorBody, requestId);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var batchResponse = JsonSerializer.Deserialize<OpenAiBatchResponse>(responseJson, JsonOptions);
        return batchResponse ?? throw new OpenAiApiException(response.StatusCode, "deserialization_failed", null, "Failed to parse batch status response.", requestId);
    }

    public async Task<string> GetFileContentAsync(
        string fileId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"files/{Uri.EscapeDataString(fileId)}/content");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var requestId = GetRequestId(response);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw OpenAiErrorParser.Parse(response.StatusCode, errorBody, requestId);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TestConnectionAsync(
        string apiKey,
        string model = "gpt-image-2",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"models/{Uri.EscapeDataString(model)}");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw OpenAiErrorParser.Parse(response.StatusCode, errorBody, GetRequestId(response));
    }

    private static string? GetRequestId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-request-id", out var values))
        {
            return values.FirstOrDefault();
        }
        return null;
    }
}

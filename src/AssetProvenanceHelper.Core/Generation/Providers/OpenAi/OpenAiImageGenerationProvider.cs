using System.Security.Cryptography;

namespace AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

public sealed class OpenAiImageGenerationProvider : IImageGenerationProvider
{
    public const string ProviderIdentifier = "OpenAI";
    public const string GptImage2Model = "gpt-image-2";

    private readonly OpenAiApiClient _client;

    public OpenAiImageGenerationProvider(OpenAiApiClient? client = null)
    {
        _client = client ?? new OpenAiApiClient();
    }

    public string ProviderId => ProviderIdentifier;

    public ProviderCapabilities GetCapabilities(string model)
    {
        // GPT-Image-2 does not support transparent background currently
        return new ProviderCapabilities(
            SupportsTextToImage: true,
            SupportsBatch: true,
            SupportsTransparentBackground: false,
            SupportsReferenceImages: true,
            SupportsArbitrarySize: true);
    }

    public async Task<ImageGenerationCandidate> GenerateAsync(
        ImageGenerationSpec spec,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (spec.AlphaRequirement == AlphaRequirement.Required)
        {
            throw new InvalidOperationException($"Model '{spec.Model}' does not support transparent backgrounds (alpha=required is blocked).");
        }

        var request = new OpenAiImageGenerationRequest(
            Model: spec.Model,
            Prompt: spec.Prompt,
            Size: $"{spec.GenerationWidth}x{spec.GenerationHeight}",
            Quality: spec.Quality,
            N: 1,
            OutputFormat: "png",
            Background: "opaque");

        var effectivePolicy = spec.RetryAttempts.HasValue
            ? new RetryPolicy(Math.Max(1, spec.RetryAttempts.Value))
            : null;

        var response = await _client.GenerateImageAsync(request, apiKey, cancellationToken, effectivePolicy).ConfigureAwait(false);

        var firstData = response.Data?.FirstOrDefault()
            ?? throw new InvalidOperationException("OpenAI API response contained no image data.");

        if (string.IsNullOrEmpty(firstData.B64Json))
        {
            throw new InvalidOperationException("OpenAI API response did not contain base64 image data.");
        }

        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(firstData.B64Json);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("OpenAI API response contained malformed base64 image data.", ex);
        }
        var rawSha256 = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        var candidateId = Guid.NewGuid().ToString("N");

        return new ImageGenerationCandidate(
            CandidateId: candidateId,
            CustomId: spec.CustomId,
            RawBytes: rawBytes,
            RawSha256: rawSha256,
            ProviderWidth: spec.GenerationWidth,
            ProviderHeight: spec.GenerationHeight,
            ProviderRequestId: response.RequestId);
    }

    public async Task<string> UploadBatchInputFileAsync(
        IReadOnlyList<ImageGenerationSpec> specs,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (specs.Count == 0)
        {
            throw new ArgumentException("Cannot submit empty batch of specifications.", nameof(specs));
        }

        foreach (var spec in specs)
        {
            if (spec.AlphaRequirement == AlphaRequirement.Required)
            {
                throw new InvalidOperationException($"Model '{spec.Model}' does not support transparent backgrounds. Blocked spec for '{spec.AssetName}'.");
            }
        }

        var jsonlBytes = OpenAiBatchJsonlBuilder.Build(specs);
        var fileResponse = await _client.UploadBatchFileAsync(jsonlBytes, "batch.jsonl", apiKey, cancellationToken).ConfigureAwait(false);
        return fileResponse.Id;
    }

    public async Task<BatchSubmissionResult> CreateBatchAsync(
        string inputFileId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var batchResponse = await _client.CreateBatchAsync(inputFileId, apiKey, cancellationToken).ConfigureAwait(false);

        return new BatchSubmissionResult(
            ProviderInputFileId: inputFileId,
            ProviderBatchId: batchResponse.Id,
            SubmittedCount: batchResponse.RequestCounts?.Total ?? 0,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }



    public async Task<BatchStatusResult> GetBatchStatusAsync(
        string providerBatchId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerBatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var response = await _client.GetBatchAsync(providerBatchId, apiKey, cancellationToken).ConfigureAwait(false);

        DateTimeOffset? completedAt = response.CompletedAt.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(response.CompletedAt.Value)
            : null;

        DateTimeOffset? expiresAt = response.ExpiresAt.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(response.ExpiresAt.Value)
            : null;

        return new BatchStatusResult(
            ProviderBatchId: response.Id,
            Status: response.Status,
            OutputFileId: response.OutputFileId,
            ErrorFileId: response.ErrorFileId,
            TotalCount: response.RequestCounts?.Total ?? 0,
            CompletedCount: response.RequestCounts?.Completed ?? 0,
            FailedCount: response.RequestCounts?.Failed ?? 0,
            CompletedAtUtc: completedAt,
            ExpiresAtUtc: expiresAt);
    }

    public async Task<BatchDownloadResult> DownloadBatchResultsAsync(
        BatchStatusResult completedBatch,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedBatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        string? outputContent = null;
        if (!string.IsNullOrWhiteSpace(completedBatch.OutputFileId))
        {
            outputContent = await _client.GetFileContentAsync(completedBatch.OutputFileId, apiKey, cancellationToken).ConfigureAwait(false);
        }

        string? errorContent = null;
        if (!string.IsNullOrWhiteSpace(completedBatch.ErrorFileId))
        {
            errorContent = await _client.GetFileContentAsync(completedBatch.ErrorFileId, apiKey, cancellationToken).ConfigureAwait(false);
        }

        var items = OpenAiBatchResultParser.ParseResults(outputContent, errorContent);

        return new BatchDownloadResult(completedBatch.ProviderBatchId, items);
    }
}

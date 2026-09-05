namespace AssetProvenanceHelper.Core.Generation.Providers;

public sealed record BatchSubmissionResult(
    string ProviderInputFileId,
    string ProviderBatchId,
    int SubmittedCount,
    DateTimeOffset CreatedAtUtc);

public sealed record BatchStatusResult(
    string ProviderBatchId,
    string Status,
    string? OutputFileId,
    string? ErrorFileId,
    int TotalCount,
    int CompletedCount,
    int FailedCount,
    DateTimeOffset? CompletedAtUtc = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record BatchItemOutput(
    string CustomId,
    bool IsSuccess,
    byte[]? ImageBytes,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessage,
    string? ProviderRequestId = null);

public sealed record BatchDownloadResult(
    string ProviderBatchId,
    IReadOnlyList<BatchItemOutput> Items);

public interface IImageGenerationProvider
{
    string ProviderId { get; }

    ProviderCapabilities GetCapabilities(string model);

    Task<ImageGenerationCandidate> GenerateAsync(
        ImageGenerationSpec spec,
        string apiKey,
        CancellationToken cancellationToken = default);



    Task<string> UploadBatchInputFileAsync(
        IReadOnlyList<ImageGenerationSpec> specs,
        string apiKey,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    Task<BatchSubmissionResult> CreateBatchAsync(
        string inputFileId,
        string apiKey,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    Task<BatchStatusResult> GetBatchStatusAsync(
        string providerBatchId,
        string apiKey,
        CancellationToken cancellationToken = default);

    Task<BatchDownloadResult> DownloadBatchResultsAsync(
        BatchStatusResult completedBatch,
        string apiKey,
        CancellationToken cancellationToken = default);
}
